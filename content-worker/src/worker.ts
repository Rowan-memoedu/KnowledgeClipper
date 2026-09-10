import { access, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { loadCategoryRegistry, loadContracts, type LoadedContracts } from "./contracts.js";
import { ContentGateError, extractOcrLexicalAuditCandidates, validateCards, validateClassification, validateCleaning, validateOcrLexicalAudit, validateOcrProofreading } from "./gates.js";
import { loadPrompt } from "./prompts.js";
import { loadLanguageResume } from "./language-resume.js";
import type { ContentProvider, ProviderRequest } from "./provider.js";
import type {
  AiTrace,
  AssetRole,
  CardBlockCandidate,
  CardBundle,
  CardCandidate,
  CardRecord,
  CategoryDefinition,
  CategoryRegistry,
  ClassifiedUnitCandidate,
  GateResult,
  JsonObject,
  LanguagePracticeDecisionBundle,
  LanguagePracticeDecisionRecord,
  LanguagePracticeMode,
  LearningBundle,
  LearningItem,
  LearningUnit,
  OcrHumanResolutionBundle,
  OcrHumanResolutionRecord,
  OcrProofreadBundle,
  OcrProofreadItem,
  QuestionAnswerBundle,
  QuestionAnswerRecord,
  QuestionWorkflowInput,
  SourceSegment,
  StandardInputItem,
  StandardInput,
  WorkerPaths,
} from "./types.js";
import { packagePath, readJson, segmentText, sha256, stableStringify } from "./util.js";

export const WORKER_ID = "content.deepseek" as const;
export const WORKER_VERSION = "0.12.0";
const LEARNING_FILE = "learning-bundle.schema-v1.json";
const LEARNING_V2_FILE = "learning-bundle.schema-v2.json";
const LEARNING_V3_FILE = "learning-bundle.schema-v3.json";
const OCR_PROOFREAD_FILE = "ocr-proofread-bundle.schema-v1.json";
const OCR_HUMAN_RESOLUTION_FILE = "ocr-human-resolution-bundle.schema-v1.json";
const CARD_FILE = "card-bundle.schema-v1.json";
const LANGUAGE_DECISION_FILE = "language-practice-decision.schema-v2.json";
const QUESTION_ANSWER_FILE = "question-answer.schema-v1.json";

interface PendingCard {
  itemId: string;
  unit: LearningUnit;
  candidate: CardCandidate;
  ordinal: number;
}

interface ProcessedItem {
  item: LearningItem;
  cards: PendingCard[];
  decisions: LanguagePracticeDecisionRecord[];
  ocrProofread?: OcrProofreadItem;
  ocrHumanResolution?: OcrHumanResolutionRecord;
  questionAnswer?: {
    turns: QuestionAnswerRecord[];
    newAnswerId: string;
    questionChainSha256: string;
  };
}

export interface WorkerResult {
  learningBundlePath: string;
  learningBundleSha256: string;
  learningBundleSchemaVersion: "learning-bundle/1.0.0" | "learning-bundle/2.0.0" | "learning-bundle/3.0.0";
  learningBundleContractVersion: "1.0.0" | "2.0.0" | "3.0.0";
  ocrProofreadBundlePath?: string;
  ocrProofreadBundleSha256?: string;
  ocrHumanResolutionBundlePath?: string;
  ocrHumanResolutionBundleSha256?: string;
  cardBundlePath?: string;
  cardBundleSha256?: string;
  languagePracticeDecisionPath?: string;
  languagePracticeDecisionSha256?: string;
  languagePracticeDecisionSchemaVersion?: "language-practice-decision/1.0.0" | "language-practice-decision/2.0.0";
  languagePracticeDecisionContractVersion?: "1.0.0" | "2.0.0";
  questionAnswerPath?: string;
  questionAnswerSha256?: string;
  releaseState: LearningBundle["release_state"];
  itemStates: Array<{ itemId: string; state: LearningItem["state"]; reasons: string[] }>;
}

export async function runWorker(paths: WorkerPaths, provider: ContentProvider): Promise<WorkerResult> {
  await assertOutputDoesNotExist(paths.outputDir);
  const contracts = await loadContracts(paths.manifestPath, paths.inputPath, paths.jobRoot);
  validateOperation(paths.operation, contracts.input);
  const registry = await loadCategoryRegistry(paths.registryPath);
  const registryRaw = await readFile(paths.registryPath);
  const resumedLearning = await loadLanguageResume(contracts, paths);
  const traces: AiTrace[] = [];
  const processed: ProcessedItem[] = [];
  for (const inputItem of contracts.input.items) {
    throwIfCancelled(paths.signal);
    processed.push(await processItem(inputItem, contracts, registry, paths.promptRoot, provider, traces, paths.signal, paths.languagePracticeDefault, resumedLearning));
  }

  const items = processed.map((entry) => entry.item);
  const pendingCards = processed.flatMap((entry) => entry.cards);
  const languageDecisions = processed.flatMap((entry) => entry.decisions);
  const ocrProofreadItems = processed.flatMap((entry) => entry.ocrProofread ? [entry.ocrProofread] : []);
  const ocrHumanResolutionRecords = processed.flatMap((entry) => entry.ocrHumanResolution ? [entry.ocrHumanResolution] : []);
  const questionAnswers = processed.flatMap((entry) => entry.questionAnswer ? [entry.questionAnswer] : []);
  if (questionAnswers.length > 1) throw new Error("INPUT_INVALID multiple question answers in one attempt");
  const readyItemCount = items.filter((item) => item.state === "ready").length;
  const someReady = items.some((item) => item.state === "ready" || item.state === "partial");
  const anyFailed = items.some((item) => item.state === "failed_ai" || item.state === "failed_content_gate");
  const releaseState: LearningBundle["release_state"] = readyItemCount === items.length
    ? "ready"
    : someReady
      ? "partial"
      : anyFailed
        ? "failed"
        : "unresolved";
  const createdAt = new Date().toISOString();
  const usesOcrProofreading = ocrProofreadItems.length > 0;
  const usesOcrHumanResolution = ocrHumanResolutionRecords.length > 0;
  if (usesOcrProofreading && usesOcrHumanResolution) throw new Error("INPUT_INVALID one attempt cannot both proofread and resolve frozen OCR");
  const learningFile = usesOcrHumanResolution ? LEARNING_V3_FILE : usesOcrProofreading ? LEARNING_V2_FILE : LEARNING_FILE;
  let ocrProofreadBundle: OcrProofreadBundle | undefined;
  let ocrProofreadJson: string | undefined;
  let ocrProofreadHash: string | undefined;
  if (usesOcrProofreading) {
    const proofreadReleaseState = ocrProofreadItems.every((item) => item.state === "ready") ? "ready" : "unresolved";
    ocrProofreadBundle = {
      schema_version: "ocr-proofread-bundle/1.0.0",
      bundle_id: `ocr-proofread-${sha256(`${contracts.inputSha256}\0${paths.attemptId}`).slice(0, 24)}`,
      package_id: contracts.input.package_id,
      task_id: contracts.input.task_id,
      input_version: contracts.input.input_version,
      attempt_id: paths.attemptId,
      created_at: createdAt,
      items: ocrProofreadItems,
      release_state: proofreadReleaseState,
      ai_trace_keys: traces.filter((trace) => trace.stage === "ocr_proofreading" || trace.stage === "ocr_lexical_audit").map((trace) => trace.call_key),
      gates: [
        { gate_id: "ocr-proofread-bundle.immutable-source", status: "pass", evidence: "原始 OCR payload 与 manifest 仅按 SHA 引用，未改写" },
        { gate_id: "ocr-proofread-bundle.release", status: proofreadReleaseState === "ready" ? "pass" : "notice", evidence: `release_state=${proofreadReleaseState}` },
      ],
    };
    ocrProofreadJson = `${stableStringify(ocrProofreadBundle, 2)}\n`;
    ocrProofreadHash = sha256(ocrProofreadJson);
  }
  let ocrHumanResolutionBundle: OcrHumanResolutionBundle | undefined;
  let ocrHumanResolutionJson: string | undefined;
  let ocrHumanResolutionHash: string | undefined;
  if (usesOcrHumanResolution) {
    const resolutionReleaseState = ocrHumanResolutionRecords.every((record) => record.state === "resolved") ? "ready" : "unresolved";
    ocrHumanResolutionBundle = {
      schema_version: "ocr-human-resolution-bundle/1.0.0",
      bundle_id: `ocr-human-resolution-${sha256(`${contracts.inputSha256}\0${paths.attemptId}`).slice(0, 24)}`,
      package_id: contracts.input.package_id,
      task_id: contracts.input.task_id,
      input_version: contracts.input.input_version,
      attempt_id: paths.attemptId,
      created_at: createdAt,
      records: ocrHumanResolutionRecords,
      release_state: resolutionReleaseState,
      gates: [
        { gate_id: "ocr-human-resolution.frozen-ancestry", status: "pass", evidence: "原始 OCR、0.8.0 frozen proofread manifest/payload、候选与 SHA-256 已逐项验真" },
        { gate_id: "ocr-human-resolution.user-provenance", status: "pass", evidence: "决议 provenance=human_submitted；未声明为 AI 自主纠错" },
        { gate_id: "ocr-human-resolution.release", status: resolutionReleaseState === "ready" ? "pass" : "notice", evidence: `release_state=${resolutionReleaseState}` },
      ],
    };
    ocrHumanResolutionJson = `${stableStringify(ocrHumanResolutionBundle, 2)}\n`;
    ocrHumanResolutionHash = sha256(ocrHumanResolutionJson);
  }
  const bundleId = `learning-${sha256(`${contracts.inputSha256}\0${paths.attemptId}`).slice(0, 24)}`;
  const unresolvedItems = items
    .filter((item) => item.state !== "ready")
    .map((item) => ({ item_id: item.item_id, reasons: item.unresolved_reasons.length > 0 ? item.unresolved_reasons : [item.state] }));
  const bundleGates: GateResult[] = [
    { gate_id: "bundle.input-integrity", status: "pass", evidence: `${contracts.manifest.files.length} 个声明文件通过 SHA-256 与大小校验` },
    { gate_id: "bundle.category-registry", status: "pass", evidence: `${registry.categories.length} 个开放类别已加载` },
    {
      gate_id: "bundle.release",
      status: releaseState === "ready" ? "pass" : releaseState === "partial" ? "notice" : "fail",
      evidence: `release_state=${releaseState}; ready_items=${readyItemCount}/${items.length}`,
    },
  ];
  const learningBundle: LearningBundle = {
    schema_version: usesOcrHumanResolution ? "learning-bundle/3.0.0" : usesOcrProofreading ? "learning-bundle/2.0.0" : "learning-bundle/1.0.0",
    bundle_id: bundleId,
    package_id: contracts.input.package_id,
    task_id: contracts.input.task_id,
    input_version: contracts.input.input_version,
    attempt_id: paths.attemptId,
    created_at: createdAt,
    input: {
      manifest_path: normalizeRelative(contracts.packageRoot, paths.manifestPath),
      manifest_sha256: contracts.manifestSha256,
      standard_input_path: normalizeRelative(contracts.packageRoot, paths.inputPath),
      standard_input_sha256: contracts.inputSha256,
    },
    ...(ocrProofreadHash ? { ocr_proofread_bundle_path: OCR_PROOFREAD_FILE, ocr_proofread_bundle_sha256: ocrProofreadHash } : {}),
    ...(ocrHumanResolutionHash ? { ocr_human_resolution_bundle_path: OCR_HUMAN_RESOLUTION_FILE, ocr_human_resolution_bundle_sha256: ocrHumanResolutionHash } : {}),
    worker: {
      name: "content-understanding-card-worker",
      version: WORKER_VERSION,
      category_registry_sha256: sha256(registryRaw),
      ...(paths.settingsRevisionId ? { settings_revision_id: paths.settingsRevisionId } : {}),
      ...(paths.promptSetId ? { prompt_set_id: paths.promptSetId } : {}),
      ...(paths.promptSetSha256 ? { prompt_set_sha256: paths.promptSetSha256 } : {}),
      ...(paths.capabilityProfileId ? { capability_profile_id: paths.capabilityProfileId } : {}),
      ...(paths.capabilityProfileVersion ? { capability_profile_version: paths.capabilityProfileVersion } : {}),
      ...(paths.capabilityProfileSha256 ? { capability_profile_sha256: paths.capabilityProfileSha256 } : {}),
      ...(paths.languagePracticeDefault ? { language_practice_default: paths.languagePracticeDefault } : {}),
    },
    items,
    ai_trace: traces,
    release_state: releaseState,
    unresolved_items: unresolvedItems,
    gates: bundleGates,
  };

  const cardRecords = pendingCards.map((pending) => makeCardRecord(contracts.input.task_id, pending));
  bundleGates.push(validateCardSourceSegmentReferences(items, cardRecords));
  const hasUnselectedLanguageMode = languageDecisions.some((decision) => decision.reasons.includes("language_practice_mode_required"));
  const learningJson = `${stableStringify(learningBundle, 2)}\n`;
  const learningHash = sha256(learningJson);
  let cardBundle: CardBundle | undefined;
  let cardJson: string | undefined;
  if (cardRecords.length > 0 && !hasUnselectedLanguageMode) {
    const withheldUnits = items.flatMap((item) => item.units
      .filter((unit) => unit.state !== "ready")
      .map((unit) => ({ item_id: item.item_id, unit_id: unit.unit_id, reasons: unit.unresolved_reasons.length > 0 ? unit.unresolved_reasons : [unit.state] })));
    cardBundle = {
      schema_version: "card-bundle/1.0.0",
      bundle_id: `cards-${sha256(`${learningHash}\0${cardRecords.map((card) => card.card_id).join("\0")}`).slice(0, 24)}`,
      package_id: contracts.input.package_id,
      task_id: contracts.input.task_id,
      attempt_id: paths.attemptId,
      created_at: createdAt,
      learning_bundle_path: learningFile,
      learning_bundle_sha256: learningHash,
      cards: cardRecords,
      withheld_units: withheldUnits,
      release_state: releaseState === "ready" ? "ready" : "partial",
      gates: [
        { gate_id: "card-bundle.learning-link", status: "pass", evidence: `LearningBundle SHA-256=${learningHash}` },
        { gate_id: "card-bundle.destination-neutral", status: "pass", evidence: "卡片只包含来源、生成内容与素材引用，不含任何下游目标路由字段" },
      ],
    };
    cardJson = `${stableStringify(cardBundle, 2)}\n`;
  }

  let languageDecision: LanguagePracticeDecisionBundle | undefined;
  let languageDecisionJson: string | undefined;
  if (languageDecisions.length > 0) {
    languageDecision = {
      schema_version: languageDecisions.some((decision) => decision.target_token) ? "language-practice-decision/2.0.0" : "language-practice-decision/1.0.0",
      decision_bundle_id: `language-decision-${sha256(`${learningHash}\0${languageDecisions.map((decision) => decision.decision_id).join("\0")}`).slice(0, 24)}`,
      package_id: contracts.input.package_id,
      task_id: contracts.input.task_id,
      input_version: contracts.input.input_version,
      attempt_id: paths.attemptId,
      created_at: createdAt,
      learning_bundle_path: learningFile,
      learning_bundle_sha256: learningHash,
      state: "requires_human",
      decisions: languageDecisions,
    };
    languageDecisionJson = `${stableStringify(languageDecision, 2)}\n`;
  }

  let questionAnswer: QuestionAnswerBundle | undefined;
  let questionAnswerJson: string | undefined;
  const questionAnswerResult = questionAnswers[0];
  if (questionAnswerResult) {
    const answerBundleId = `question-answer-${sha256(`${learningHash}\0${questionAnswerResult.questionChainSha256}\0${questionAnswerResult.newAnswerId}`).slice(0, 24)}`;
    const turns = questionAnswerResult.turns.map((turn) => turn.answer_id === questionAnswerResult.newAnswerId
      ? { ...turn, artifact_ref: answerBundleId }
      : turn);
    questionAnswer = {
      schema_version: "question-answer/1.0.0",
      answer_bundle_id: answerBundleId,
      package_id: contracts.input.package_id,
      task_id: contracts.input.task_id,
      input_version: contracts.input.input_version,
      attempt_id: paths.attemptId,
      created_at: createdAt,
      phase: "answer",
      learning_bundle_path: learningFile,
      learning_bundle_sha256: learningHash,
      question_chain_sha256: questionAnswerResult.questionChainSha256,
      new_answer_id: questionAnswerResult.newAnswerId,
      turns,
      gates: [
        { gate_id: "question-answer.learning-link", status: "pass", evidence: `LearningBundle SHA-256=${learningHash}` },
        { gate_id: "question-answer.ordered-chain", status: "pass", evidence: `${turns.length} 个问答 turn 按连续 turn_index 发布；旧 ancestry 保持前缀不变` },
        { gate_id: "question-answer.destination-neutral", status: "pass", evidence: "答案工件不含下游目标路由或写入字段" },
      ],
    };
    questionAnswerJson = `${stableStringify(questionAnswer, 2)}\n`;
  }

  throwIfCancelled(paths.signal);
  await mkdir(path.dirname(path.resolve(paths.outputDir)), { recursive: true });
  await mkdir(paths.outputDir, { recursive: false });
  let ocrProofreadBundlePath: string | undefined;
  if (ocrProofreadJson) {
    ocrProofreadBundlePath = path.join(paths.outputDir, OCR_PROOFREAD_FILE);
    await writeFile(ocrProofreadBundlePath, ocrProofreadJson, { encoding: "utf8", flag: "wx" });
  }
  let ocrHumanResolutionBundlePath: string | undefined;
  if (ocrHumanResolutionJson) {
    ocrHumanResolutionBundlePath = path.join(paths.outputDir, OCR_HUMAN_RESOLUTION_FILE);
    await writeFile(ocrHumanResolutionBundlePath, ocrHumanResolutionJson, { encoding: "utf8", flag: "wx" });
  }
  const learningBundlePath = path.join(paths.outputDir, learningFile);
  await writeFile(learningBundlePath, learningJson, { encoding: "utf8", flag: "wx" });
  let languagePracticeDecisionPath: string | undefined;
  let languagePracticeDecisionSha256: string | undefined;
  if (languageDecision && languageDecisionJson) {
    languagePracticeDecisionPath = path.join(paths.outputDir, LANGUAGE_DECISION_FILE);
    await writeFile(languagePracticeDecisionPath, languageDecisionJson, { encoding: "utf8", flag: "wx" });
    languagePracticeDecisionSha256 = sha256(languageDecisionJson);
  }
  let questionAnswerPath: string | undefined;
  let questionAnswerSha256: string | undefined;
  if (questionAnswer && questionAnswerJson) {
    questionAnswerPath = path.join(paths.outputDir, QUESTION_ANSWER_FILE);
    await writeFile(questionAnswerPath, questionAnswerJson, { encoding: "utf8", flag: "wx" });
    questionAnswerSha256 = sha256(questionAnswerJson);
  }
  let cardBundlePath: string | undefined;
  let cardBundleSha256: string | undefined;
  if (cardBundle && cardJson) {
    cardBundlePath = path.join(paths.outputDir, CARD_FILE);
    await writeFile(cardBundlePath, cardJson, { encoding: "utf8", flag: "wx" });
    cardBundleSha256 = sha256(cardJson);
  }
  return {
    learningBundlePath,
    learningBundleSha256: learningHash,
    learningBundleSchemaVersion: learningBundle.schema_version,
    learningBundleContractVersion: usesOcrHumanResolution ? "3.0.0" : usesOcrProofreading ? "2.0.0" : "1.0.0",
    ...(ocrProofreadBundlePath ? { ocrProofreadBundlePath } : {}),
    ...(ocrProofreadHash ? { ocrProofreadBundleSha256: ocrProofreadHash } : {}),
    ...(ocrHumanResolutionBundlePath ? { ocrHumanResolutionBundlePath } : {}),
    ...(ocrHumanResolutionHash ? { ocrHumanResolutionBundleSha256: ocrHumanResolutionHash } : {}),
    ...(cardBundlePath ? { cardBundlePath } : {}),
    ...(cardBundleSha256 ? { cardBundleSha256 } : {}),
    ...(languagePracticeDecisionPath ? { languagePracticeDecisionPath } : {}),
    ...(languagePracticeDecisionSha256 ? { languagePracticeDecisionSha256 } : {}),
    ...(languageDecision ? { languagePracticeDecisionSchemaVersion: languageDecision.schema_version } : {}),
    ...(languageDecision ? { languagePracticeDecisionContractVersion: languageDecision.schema_version.endsWith("2.0.0") ? "2.0.0" as const : "1.0.0" as const } : {}),
    ...(questionAnswerPath ? { questionAnswerPath } : {}),
    ...(questionAnswerSha256 ? { questionAnswerSha256 } : {}),
    releaseState,
    itemStates: items.map((item) => ({ itemId: item.item_id, state: item.state, reasons: item.unresolved_reasons })),
  };
}

async function processItem(inputItem: StandardInputItem, contracts: LoadedContracts, registry: CategoryRegistry, promptRoot: string, provider: ContentProvider, traces: AiTrace[], signal?: AbortSignal, languagePracticeDefault?: "ask" | LanguagePracticeMode, resumedLearning?: LearningBundle): Promise<ProcessedItem> {
  if (inputItem.source.kind === "media_set") return processMediaItem(inputItem, contracts);
  if (inputItem.source.kind === "question_workflow") return processQuestionWorkflowItem(inputItem, contracts, registry, promptRoot, provider, traces, signal);
  const source = await loadTextSource(inputItem, contracts);
  const sourceRefs = [
    { path: inputItem.source.material_path, sha256: contracts.filesByPath.get(inputItem.source.material_path)?.sha256 ?? "" },
    ...(inputItem.language_selection?.context_segments ?? []).map((segment) => ({ path: segment.material_path, sha256: segment.sha256 })),
  ];
  let ocrProofread: OcrProofreadItem | undefined;
  let ocrHumanResolution: OcrHumanResolutionRecord | undefined;
  const baseItem = (): LearningItem => ({
    item_id: inputItem.item_id,
    source_kind: inputItem.source.kind,
    source_refs: sourceRefs,
    source_segments: source.segments,
    cleaning: { state: "failed_content_gate", removals: [], format_anomalies: [] },
    ...(ocrProofread ? { proofreading: {
      state: ocrProofread.state,
      proofread_text: ocrProofread.proofread_text,
      proofread_text_sha256: ocrProofread.proofread_text_sha256,
      proofread_segments: ocrProofread.proofread_segments,
      corrections: ocrProofread.corrections,
      unresolved: ocrProofread.unresolved,
    } } : {}),
    ...(ocrHumanResolution ? { human_resolution: ocrHumanResolution } : {}),
    units: [],
    state: "failed_content_gate",
    unresolved_reasons: [],
    gates: [],
  });
  try {
    let workingSegments = source.segments;
    let workingText = source.text;
    if (inputItem.source.kind === "ocr_text") {
      const validatedResolution = contracts.ocrHumanResolutions.get(inputItem.item_id);
      if (validatedResolution) {
        workingSegments = validatedResolution.resolvedSegments;
        workingText = validatedResolution.resolvedText;
        const resolutionInput = validatedResolution.input;
        ocrHumanResolution = {
          resolution_id: resolutionInput.resolution_id,
          item_id: inputItem.item_id,
          state: "resolved",
          action: resolutionInput.action,
          actor: "user",
          provenance: "human_submitted",
          reason: resolutionInput.reason,
          source_proofread_artifact: resolutionInput.source_proofread_artifact,
          source_ocr: { path: inputItem.source.material_path, sha256: validatedResolution.sourceItem.original_text_sha256, text: validatedResolution.sourceItem.original_text },
          frozen_proofread: {
            text: validatedResolution.sourceItem.proofread_text,
            sha256: validatedResolution.sourceItem.proofread_text_sha256,
            corrections: validatedResolution.sourceItem.corrections,
            unresolved_candidates: validatedResolution.sourceItem.unresolved,
          },
          user_diffs: resolutionInput.correction?.diffs ?? [],
          resolved_text: validatedResolution.resolvedText,
          resolved_text_sha256: sha256(validatedResolution.resolvedText),
          protected_ranges: validatedResolution.protectedRanges,
          unresolved_reasons: [],
          gates: [
            { gate_id: "ocr-human-resolution.source-binding", status: "pass", evidence: "frozen proofread artifact、原始 OCR、候选与当前 item 均按 SHA-256/identity 验真" },
            { gate_id: "ocr-human-resolution.action", status: "pass", evidence: `${resolutionInput.action}; actor=user; provenance=human_submitted` },
            { gate_id: "ocr-human-resolution.diff-replay", status: "pass", evidence: resolutionInput.action === "accept_original" ? "接受 frozen proofread 中保留的原文字面值；无用户 diff" : `${resolutionInput.correction!.diffs.length} 个用户 diff 从 frozen proofread 精确重放` },
          ],
        };
        ocrProofread = validatedResolution.sourceItem;
      } else {
        const proofreadingRaw = await callStage(provider, traces, promptRoot, "ocr_proofreading", `ocr_proofreading:${inputItem.item_id}`, {
        item_id: inputItem.item_id,
        source_text: source.text,
        segments: source.segments,
        provenance: inputItem.source.provenance,
      }, undefined, signal);
        const proofreading = validateOcrProofreading(proofreadingRaw, source.segments);
        if (proofreading.corrections.length === 0 && proofreading.unresolved.length === 0) {
          const lexicalCandidates = extractOcrLexicalAuditCandidates(source.segments);
          if (lexicalCandidates.length > 0) {
            const lexicalRaw = await callStage(provider, traces, promptRoot, "ocr_lexical_audit", `ocr_lexical_audit:${inputItem.item_id}`, {
              item_id: inputItem.item_id,
              source_text: source.text,
              segments: source.segments,
              candidates: lexicalCandidates,
              provenance: inputItem.source.provenance,
            }, undefined, signal);
            const lexicalAudit = validateOcrLexicalAudit(lexicalRaw, lexicalCandidates);
            proofreading.unresolved.push(...lexicalAudit.unresolved);
            proofreading.response.unresolved.push(...lexicalAudit.unresolved);
            proofreading.gates.push(...lexicalAudit.gates);
            const releaseGate = proofreading.gates.find((gate) => gate.gate_id === "ocr-proofreading.release");
            if (releaseGate && lexicalAudit.unresolved.length > 0) {
              releaseGate.status = "notice";
              releaseGate.evidence = `${proofreading.unresolved.length} 项经首轮或词法漏报复核后保留原文并 fail-closed`;
            }
          }
        }
        workingSegments = identifyReleasedOcrSegments(proofreading.proofreadSegments);
        workingText = proofreading.response.proofread_text;
        ocrProofread = {
        item_id: inputItem.item_id,
        state: proofreading.unresolved.length === 0 ? "ready" : "unresolved",
        provenance: inputItem.source.provenance,
        source_ref: sourceRefs[0]!,
        original_text: source.text,
        original_text_sha256: sha256(source.text),
        proofread_text: proofreading.response.proofread_text,
        proofread_text_sha256: sha256(proofreading.response.proofread_text),
        source_segments: source.segments,
        proofread_segments: proofreading.proofreadSegments,
        corrections: proofreading.corrections,
        unresolved: proofreading.unresolved,
        gates: proofreading.gates,
      };
        if (ocrProofread.state === "unresolved") {
          const reasons = unique(ocrProofread.unresolved.map((entry) => `ocr_${entry.reason}`));
          return {
          item: {
            item_id: inputItem.item_id,
            source_kind: "ocr_text",
            source_refs: sourceRefs,
            source_segments: source.segments,
            cleaning: { state: "not_applicable", removals: [], format_anomalies: [] },
            proofreading: {
              state: "unresolved",
              proofread_text: ocrProofread.proofread_text,
              proofread_text_sha256: ocrProofread.proofread_text_sha256,
              proofread_segments: ocrProofread.proofread_segments,
              corrections: ocrProofread.corrections,
              unresolved: ocrProofread.unresolved,
            },
            units: [],
            state: "unresolved",
            unresolved_reasons: reasons,
            gates: ocrProofread.gates,
          },
          cards: [],
          decisions: [],
            ocrProofread,
          };
        }
      }
    }
    const frozenItem = resumedLearning?.items.find(item => item.item_id === inputItem.item_id);
    if (resumedLearning && (!frozenItem || stableStringify(frozenItem.source_segments) !== stableStringify(workingSegments))) throw new Error("INPUT_STALE language_practice_resume source segments");
    const cleaningRaw = resumedLearning
      ? resumedLearning.ai_trace.find(trace => trace.call_key === `cleaning:${inputItem.item_id}`)?.response
      : await callStage(provider, traces, promptRoot, "cleaning", `cleaning:${inputItem.item_id}`, {
      item_id: inputItem.item_id,
      source_text: workingText,
      segments: workingSegments,
      hints: inputItem.hints ?? {},
      human_overrides: inputItem.human_overrides ?? {},
      ...(ocrHumanResolution ? { human_resolution: { action: ocrHumanResolution.action, actor: "user", protected_ranges: ocrHumanResolution.protected_ranges } } : {}),
    }, undefined, signal);
    const cleaning = validateCleaning(cleaningRaw, workingSegments);
    if (ocrHumanResolution) {
      assertHumanResolvedCharactersPreserved(cleaning.removals, workingSegments, ocrHumanResolution.protected_ranges);
      cleaning.gates.push({ gate_id: "cleaning.human-resolution-protected", status: "pass", evidence: `${ocrHumanResolution.protected_ranges.length} 个用户明确字符范围未被清洗删除或回改` });
    }
    const classificationRaw = frozenItem ? { units: frozenItem.units.map(unit => ({
      unit_id: unit.unit_id,
      source_segment_indexes: unit.source_segment_ids.map(id => workingSegments.findIndex(segment => segment.segment_id === id)),
      category_id: unit.category_id, subtype: unit.subtype, answer_state: unit.answer_state,
      context_state: unit.context_state, risk_state: unit.risk_state,
      ...(unit.question ? { question: unit.question } : {}),
      ...(unit.supplied_answer ? { supplied_answer: unit.supplied_answer } : {}),
      ...(unit.target_term ? { target_term: unit.target_term } : {}),
      unresolved_reasons: unit.unresolved_reasons.filter(reason => reason !== "language_practice_mode_required"),
    })) } : await callStage(provider, traces, promptRoot, "classification", `classification:${inputItem.item_id}`, {
      item_id: inputItem.item_id,
      cleaned_text: cleaning.response.cleaned_text,
      segments: workingSegments.filter((segment) => cleaning.activeSegmentIndexes.includes(segment.segment_index)),
      category_registry: registry,
      hints: inputItem.hints ?? {},
      human_overrides: inputItem.human_overrides ?? {},
      ...(inputItem.language_selection ? { language_selection: {
        schema_version: inputItem.language_selection.schema_version,
        selected_text: inputItem.language_selection.selected_text,
        selected_text_sha256: inputItem.language_selection.selected_text_sha256,
        target_text: inputItem.language_selection.target_text,
        target_text_sha256: inputItem.language_selection.target_text_sha256,
        context_enabled: inputItem.language_selection.context_enabled,
        context_segments: inputItem.language_selection.context_segments,
      } } : {}),
    }, undefined, signal);
    const modelClassification = validateClassification(classificationRaw, workingSegments, cleaning.activeSegmentIndexes, registry);
    const overriddenCandidates = modelClassification.units.map((unit) => applyHumanOverrides(unit, inputItem));
    const classification = validateClassification({ units: overriddenCandidates }, workingSegments, cleaning.activeSegmentIndexes, registry);
    const selectedTarget = inputItem.human_overrides?.languageTargetResolution?.selected_text ?? inputItem.language_selection?.target_text;
    if (selectedTarget) {
      for (const candidate of classification.units) if (isLanguageCategory(candidate.category_id)) candidate.target_term = selectedTarget;
    }
    if (resumedLearning) classification.gates.push({ gate_id: "classification.frozen-resume", status: "pass", evidence: `Validated prior learning ${contracts.input.language_practice_resume!.learning_ref.payload_sha256}; decision ${contracts.input.language_practice_resume!.decision_ref.payload_sha256}; cleaning/classification provider calls skipped` });
    if (inputItem.human_overrides?.units) classification.gates.push({ gate_id: "classification.human-overrides", status: "pass", evidence: "human_overrides 已重新通过完整分类契约与来源门禁" });
    const units: LearningUnit[] = [];
    const cards: PendingCard[] = [];
    const decisions: LanguagePracticeDecisionRecord[] = [];
    const itemReasons: string[] = [];
    const humanFormatSegmentIndexes = new Set(cleaning.response.format_anomalies
      .filter((anomaly) => anomaly.severity === "requires_human")
      .map((anomaly) => anomaly.segment_index));

    for (const rawUnit of classification.units) {
      const unit = applyHumanOverrides(rawUnit, inputItem);
      const category = registry.categories.find((entry) => entry.id === unit.category_id);
      if (!category) throw new Error(`CATEGORY_REGISTRY_INVALID missing ${unit.category_id}`);
      const assignedSegments = unit.source_segment_indexes.map((index) => workingSegments[index]).filter((segment): segment is SourceSegment => Boolean(segment));
      const assignedSource = assignedSegments.map((segment) => segment.text).join("\n\n");
      const reasons = [...unit.unresolved_reasons];
      const isLanguage = isLanguageCategory(unit.category_id);
      const languagePracticeMode = isLanguage ? resolveLanguagePracticeMode(contracts.input, inputItem, languagePracticeDefault) : undefined;
      const unitRequiresHumanFormat = unit.source_segment_indexes.some((index) => humanFormatSegmentIndexes.has(index));
      if (unitRequiresHumanFormat) reasons.push("format_anomaly_requires_human");
      if (unit.answer_state === "ambiguous") reasons.push("answer_state_ambiguous");
      if (unit.answer_state === "missing" && unit.risk_state !== "ordinary") reasons.push("needs_research");
      if (isLanguage && !unit.target_term) reasons.push("target_term_ambiguous");
      if (!isLanguage && inputItem.hints?.requested_templates?.some(template => !category.allowed_templates.includes(template))) reasons.push("requested_template_category_conflict");
      if (isLanguage && !languagePracticeMode) {
        reasons.push("language_practice_mode_required");
        decisions.push(makeLanguageDecision(inputItem.item_id, unit, assignedSegments, ["language_practice_mode_required"], {
          requestedOverridePaths: ["human_overrides.languagePracticeMode"],
        }));
      }
      if (isLanguage && languagePracticeMode && includesWriting(languagePracticeMode) && !unit.target_term) {
        reasons.push("language_writing_target_not_unique");
        decisions.push(makeLanguageDecision(inputItem.item_id, unit, assignedSegments, ["language_writing_target_not_unique"], {
          currentMode: languagePracticeMode,
          targetOccurrenceCount: 0,
          requestedOverridePaths: ["human_overrides.languageTargetResolution"],
        }));
      }
      if (reasons.length > 0) {
        const learningUnit = makeLearningUnit(unit, assignedSegments, [], "unresolved", unique(reasons), [
          { gate_id: "unit.release", status: "notice", evidence: unique(reasons).join("; ") },
        ]);
        units.push(learningUnit);
        itemReasons.push(...reasons);
        continue;
      }
      const unitCards: CardCandidate[] = [];
      const unitGeneratedContent: Array<{ kind: string; text: string }> = [];
      const unitGates: GateResult[] = [];
      const unitReasons: string[] = [];

      if (!isLanguage || languagePracticeMode === "meaning-reading" || languagePracticeMode === "both") {
        const requestedTemplates = isLanguage ? ["language_context"] : inputItem.hints?.requested_templates ?? category.allowed_templates.slice(0, 1);
        const cardRaw = await callStage(provider, traces, promptRoot, "card_generation", `card_generation:${inputItem.item_id}:${unit.unit_id}`, {
          item_id: inputItem.item_id,
          unit,
          source_segments: assignedSegments,
          requested_templates: requestedTemplates,
          human_overrides: inputItem.human_overrides ?? {},
        }, category, signal);
        const cardResult = validateCards(cardRaw, unit, category, assignedSource, new Set(sourceRefs.map((ref) => ref.path)));
        unitCards.push(...cardResult.cards);
        unitGeneratedContent.push(...cardResult.response.generated_content);
        unitGates.push(...cardResult.gates);
        unitReasons.push(...cardResult.response.unresolved_reasons);
      }

      if (isLanguage && languagePracticeMode && includesWriting(languagePracticeMode)) {
        const targetTerm = unit.target_term!;
        const writingSource = stripOuterTerminalLineEndings(assignedSegments.map((segment) => segment.text).join("\n\n"));
        const uniqueWritingBody = assignedSegments.length === 1 && writingSource.trim() !== "";
        const chosenOccurrence = inputItem.human_overrides?.languageTargetResolution?.action === "accept_candidate"
          ? inputItem.human_overrides.languageTargetResolution.candidateOccurrence
          : undefined;
        const occurrenceCount = chosenOccurrence ? 1 : uniqueWritingBody ? countLiteralOccurrences(writingSource, targetTerm) : 0;
        if (!uniqueWritingBody || occurrenceCount !== 1) {
          unitReasons.push("language_writing_target_not_unique");
          decisions.push(makeLanguageDecision(inputItem.item_id, unit, assignedSegments, ["language_writing_target_not_unique"], {
            currentMode: languagePracticeMode,
            targetOccurrenceCount: occurrenceCount,
            requestedOverridePaths: ["human_overrides.languageTargetResolution"],
          }));
        } else {
          const writing = await resolveLanguageWritingDetails(contracts.input, inputItem, unit, assignedSegments, category, promptRoot, provider, traces, signal);
          if (!writing.ready) {
            unitReasons.push("language_writing_details_required");
            decisions.push(makeLanguageDecision(inputItem.item_id, unit, assignedSegments, ["language_writing_details_required"], {
              currentMode: languagePracticeMode,
              targetOccurrenceCount: occurrenceCount,
              ...(writing.pronunciationState ? { pronunciationState: writing.pronunciationState } : {}),
              ...(writing.meaningState ? { meaningState: writing.meaningState } : {}),
              requestedOverridePaths: [
                `human_overrides.languageWriting.units.${unit.unit_id}.pronunciation`,
                `human_overrides.languageWriting.units.${unit.unit_id}.meaning`,
                `human_overrides.languageWriting.units.${unit.unit_id}.confirmed`,
              ],
            }));
          } else {
            const replacement = `（${writing.pronunciation}；${writing.meaning}）`;
            const front = chosenOccurrence
              ? replaceChosenOccurrence(writingSource, assignedSegments, chosenOccurrence, replacement)
              : writingSource.replace(targetTerm, replacement);
            const frontProvenance = writing.humanConfirmed ? "human_confirmed" as const : "generated" as const;
            const generatedContent = writing.humanConfirmed ? [] : [{ kind: "language_writing_cloze", text: front }];
            const writingResult = validateCards({
              generated_content: generatedContent,
              cards: [{
                template: "language_writing",
                front_blocks: [{ type: "text", text: front, provenance: frontProvenance }],
                back_blocks: [{ type: "text", text: targetTerm, provenance: "source" }],
                answer_provenance: "not_applicable",
              }],
              unresolved_reasons: [],
            }, unit, category, assignedSource, new Set(sourceRefs.map((ref) => ref.path)), { allowHumanConfirmed: writing.humanConfirmed });
            unitCards.push(...writingResult.cards);
            unitGeneratedContent.push(...writingResult.response.generated_content);
            unitGates.push(...writingResult.gates, {
              gate_id: "language-writing.deterministic-front",
              status: "pass",
              evidence: "仅移除 segment 集合最外层终止 CR/LF；内部换行与其他空白不变；唯一 target_term 已替换为全角线索；背面保持来源原词",
            });
          }
        }
      }

      if (isLanguage && languagePracticeMode === "both" && unitReasons.length === 0) {
        if (unitCards.length !== 2 || unitCards[0]?.template !== "language_context" || unitCards[1]?.template !== "language_writing") {
          throw new ContentGateError("FAILED_CONTENT_GATE language-practice.both-cardinality", [{ gate_id: "language-practice.both-cardinality", status: "fail", evidence: "both 必须恰好输出 language_context、language_writing 各一张且顺序固定" }]);
        }
        unitGates.push({ gate_id: "language-practice.both-order", status: "pass", evidence: "meaning-reading 在前，writing 在后，共 2 张" });
      }

      const state: LearningUnit["state"] = unitReasons.length === 0 && unitCards.length > 0 ? "ready" : "unresolved";
      const learningUnit = makeLearningUnit(unit, assignedSegments, unitGeneratedContent, state, unique(unitReasons), unitGates);
      units.push(learningUnit);
      if (state === "unresolved") itemReasons.push(...unitReasons);
      if (state === "ready") unitCards.forEach((candidate, ordinal) => cards.push({ itemId: inputItem.item_id, unit: learningUnit, candidate, ordinal }));
    }
    const readyUnits = units.filter((unit) => unit.state === "ready").length;
    const itemState: LearningItem["state"] = readyUnits === units.length ? "ready" : readyUnits > 0 ? "partial" : "unresolved";
    return {
      item: {
        item_id: inputItem.item_id,
        source_kind: inputItem.source.kind,
        source_refs: sourceRefs,
        source_segments: workingSegments,
        cleaning: {
          state: humanFormatSegmentIndexes.size > 0 ? "unresolved" : "ready",
          cleaned_text: cleaning.response.cleaned_text,
          removals: cleaning.removals,
          format_anomalies: cleaning.response.format_anomalies,
        },
        ...(ocrProofread ? { proofreading: {
          state: ocrProofread.state,
          proofread_text: ocrProofread.proofread_text,
          proofread_text_sha256: ocrProofread.proofread_text_sha256,
          proofread_segments: ocrProofread.proofread_segments,
          corrections: ocrProofread.corrections,
          unresolved: ocrProofread.unresolved,
        } } : {}),
        ...(ocrHumanResolution ? { human_resolution: ocrHumanResolution } : {}),
        units,
        state: itemState,
        unresolved_reasons: unique(itemReasons),
        gates: [...cleaning.gates, ...classification.gates],
      },
      cards,
      decisions: decisions.map((decision) => enrichLanguageDecision(decision, inputItem, workingSegments)),
      ...(ocrProofread && !ocrHumanResolution ? { ocrProofread } : {}),
      ...(ocrHumanResolution ? { ocrHumanResolution } : {}),
    };
  } catch (error) {
    const item = baseItem();
    const message = error instanceof Error ? error.message : String(error);
    if (message.startsWith("WORKER_CANCELLED")) throw error;
    const isAi = /^AI_|^REPLAY_/u.test(message);
    item.cleaning.state = isAi ? "failed_ai" : "failed_content_gate";
    item.state = isAi ? "failed_ai" : "failed_content_gate";
    item.unresolved_reasons = [message];
    item.gates = error instanceof ContentGateError ? error.gates : [{ gate_id: isAi ? "item.ai" : "item.contract", status: "fail", evidence: message }];
    return { item, cards: [], decisions: [], ...(ocrProofread && !ocrHumanResolution ? { ocrProofread } : {}), ...(ocrHumanResolution ? { ocrHumanResolution } : {}) };
  }
}

function assertHumanResolvedCharactersPreserved(
  removals: LearningItem["cleaning"]["removals"],
  segments: SourceSegment[],
  protectedRanges: OcrHumanResolutionRecord["protected_ranges"],
): void {
  for (const protectedRange of protectedRanges) {
    const segment = segments[protectedRange.segment_index];
    if (!segment || segment.text.slice(protectedRange.start, protectedRange.end) !== protectedRange.exact_text) {
      throw new ContentGateError("FAILED_CONTENT_GATE human-resolution.protected-range", [{ gate_id: "human-resolution.protected-range", status: "fail", evidence: `受保护字符范围无法逐字定位 segment=${protectedRange.segment_index}` }]);
    }
    for (const removal of removals.filter((entry) => entry.segment_index === protectedRange.segment_index)) {
      const removalStart = removal.source_start - segment.start;
      const removalEnd = removal.source_end - segment.start;
      if (removalStart < protectedRange.end && removalEnd > protectedRange.start) {
        throw new ContentGateError("FAILED_CONTENT_GATE human-resolution.user-characters", [{ gate_id: "human-resolution.user-characters", status: "fail", evidence: `清洗删除与用户明确字符重叠 segment=${protectedRange.segment_index}` }]);
      }
    }
  }
}

async function processQuestionWorkflowItem(
  inputItem: StandardInputItem,
  contracts: LoadedContracts,
  registry: CategoryRegistry,
  promptRoot: string,
  provider: ContentProvider,
  traces: AiTrace[],
  signal?: AbortSignal,
): Promise<ProcessedItem> {
  const workflow = contracts.input.questionWorkflow;
  if (!workflow || inputItem.source.kind !== "question_workflow") throw new Error("INPUT_INVALID missing question workflow");
  const category = registry.categories.find((entry) => entry.id === "question_answer");
  if (!category) throw new Error("CATEGORY_REGISTRY_INVALID missing question_answer");
  const segments = makeQuestionWorkflowSegments(workflow, contracts.inputRelativePath);
  const sourceRefs: LearningItem["source_refs"] = [{ path: contracts.inputRelativePath, sha256: contracts.inputSha256 }];
  if (workflow.cardStructureRef) {
    sourceRefs.push({ path: workflow.cardStructureRef, sha256: contracts.filesByPath.get(workflow.cardStructureRef)?.sha256 ?? "" });
  }
  const latestTurn = workflow.questionTurns.at(-1)!;
  const baseCandidate: ClassifiedUnitCandidate = {
    unit_id: "unit-question-chain",
    source_segment_indexes: segments.map((_, index) => index),
    category_id: "question_answer",
    subtype: workflow.phase === "answer" ? "interactive_question" : "interactive_question_cards",
    answer_state: workflow.phase === "answer" ? "missing" : "provided",
    context_state: workflow.questionTurns.length > 1 ? "provided" : "not_applicable",
    risk_state: inputItem.hints?.risk_state ?? "ordinary",
    question: latestTurn.question,
    ...(workflow.phase === "make-cards" ? { supplied_answer: workflow.answerAncestry.at(-1)!.answerText } : {}),
    unresolved_reasons: [],
  };
  const chainGate: GateResult = {
    gate_id: "question-chain.ancestry",
    status: "pass",
    evidence: `${workflow.questionTurns.length} 个 question turn 与 ${workflow.answerAncestry.length} 个 persisted answer 通过连续序号、父 answer 与 SHA-256 校验`,
  };

  if (workflow.phase === "answer") {
    const raw = await callStage(provider, traces, promptRoot, "question_answer", `question_answer:${inputItem.item_id}:${latestTurn.turnId}`, {
      item_id: inputItem.item_id,
      phase: workflow.phase,
      question_turns: workflow.questionTurns,
      answer_ancestry: workflow.answerAncestry,
      current_turn: latestTurn,
    }, undefined, signal);
    const answer = validateQuestionAnswerResponse(raw);
    if (!answer.ready) {
      const unit = makeLearningUnit(baseCandidate, segments, [], "unresolved", answer.reasons, [
        { gate_id: "question-answer.release", status: "notice", evidence: answer.reasons.join("; ") },
      ]);
      return {
        item: makeQuestionLearningItem(inputItem, sourceRefs, segments, unit, "unresolved", answer.reasons, [chainGate]),
        cards: [],
        decisions: [],
      };
    }
    const answerHash = sha256(answer.text);
    const previousAnswer = workflow.answerAncestry.at(-1);
    const newAnswerId = `answer-${sha256(`${contracts.input.task_id}\0${latestTurn.turnId}\0${answerHash}\0${previousAnswer?.answerId ?? "root"}`).slice(0, 24)}`;
    const turns: QuestionAnswerRecord[] = workflow.answerAncestry.map((entry, index) => ({
      answer_id: entry.answerId,
      artifact_ref: entry.artifactRef,
      turn_id: entry.turnId,
      turn_index: entry.turnIndex,
      question: workflow.questionTurns[index]!.question,
      question_sha256: entry.questionSha256,
      answer_text: entry.answerText,
      answer_text_sha256: entry.answerTextSha256,
      ...(entry.parentAnswerId ? { parent_answer_id: entry.parentAnswerId } : {}),
      provenance: "persisted",
    }));
    turns.push({
      answer_id: newAnswerId,
      artifact_ref: "pending",
      turn_id: latestTurn.turnId,
      turn_index: latestTurn.turnIndex,
      question: latestTurn.question,
      question_sha256: sha256(latestTurn.question),
      answer_text: answer.text,
      answer_text_sha256: answerHash,
      ...(previousAnswer ? { parent_answer_id: previousAnswer.answerId } : {}),
      provenance: "generated",
    });
    const questionChainSha256 = sha256(stableStringify(turns.map((turn) => ({
      answer_id: turn.answer_id,
      turn_id: turn.turn_id,
      turn_index: turn.turn_index,
      question_sha256: turn.question_sha256,
      answer_text_sha256: turn.answer_text_sha256,
      ...(turn.parent_answer_id ? { parent_answer_id: turn.parent_answer_id } : {}),
    }))));
    const unit = makeLearningUnit(baseCandidate, segments, [{ kind: "answer", text: answer.text }], "ready", [], [
      { gate_id: "question-answer.pure-text", status: "pass", evidence: "答案非空，且未含 UI、版权页脚、Markdown 围栏、链接或 HTML 噪声" },
      { gate_id: "question-answer.append-one", status: "pass", evidence: `保留 ${workflow.answerAncestry.length} 个 ancestry answer，只追加 turn_index=${latestTurn.turnIndex}` },
    ]);
    return {
      item: makeQuestionLearningItem(inputItem, sourceRefs, segments, unit, "ready", [], [chainGate]),
      cards: [],
      decisions: [],
      questionAnswer: { turns, newAnswerId, questionChainSha256 },
    };
  }

  const cardStructure = await loadQuestionCardStructure(workflow, contracts, category);
  const requestedTemplates = cardStructure.requestedTemplates
    ?? inputItem.hints?.requested_templates
    ?? category.allowed_templates.slice(0, 1);
  const assignedSource = segments.map((segment) => segment.text).join("\n\n");
  const cardRaw = await callStage(provider, traces, promptRoot, "card_generation", `card_generation:${inputItem.item_id}:${baseCandidate.unit_id}`, {
    item_id: inputItem.item_id,
    unit: baseCandidate,
    source_segments: segments,
    question_chain: workflow,
    requested_templates: requestedTemplates,
    ...(cardStructure.document ? { card_structure: cardStructure.document } : {}),
  }, category, signal);
  const cardResult = validateCards(cardRaw, baseCandidate, category, assignedSource, new Set(sourceRefs.map((ref) => ref.path)));
  const reasons = unique(cardResult.response.unresolved_reasons);
  const state: LearningUnit["state"] = reasons.length === 0 && cardResult.cards.length > 0 ? "ready" : "unresolved";
  const unit = makeLearningUnit(baseCandidate, segments, cardResult.response.generated_content, state, reasons, [
    ...cardResult.gates,
    { gate_id: "question-cards.complete-chain", status: "pass", evidence: `${workflow.questionTurns.length} 个问题均有持久化 answer 后才进入现有卡片质量门` },
  ]);
  const cards: PendingCard[] = [];
  if (state === "ready") cardResult.cards.forEach((candidate, ordinal) => cards.push({ itemId: inputItem.item_id, unit, candidate, ordinal }));
  return {
    item: makeQuestionLearningItem(inputItem, sourceRefs, segments, unit, state === "ready" ? "ready" : "unresolved", reasons, [chainGate]),
    cards,
    decisions: [],
  };
}

function makeQuestionLearningItem(
  inputItem: StandardInputItem,
  sourceRefs: LearningItem["source_refs"],
  segments: SourceSegment[],
  unit: LearningUnit,
  state: "ready" | "unresolved",
  reasons: string[],
  gates: GateResult[],
): LearningItem {
  return {
    item_id: inputItem.item_id,
    source_kind: "question_workflow",
    source_refs: sourceRefs,
    source_segments: segments,
    cleaning: { state: "not_applicable", removals: [], format_anomalies: [] },
    units: [unit],
    state,
    unresolved_reasons: unique(reasons),
    gates,
  };
}

function makeQuestionWorkflowSegments(workflow: QuestionWorkflowInput, sourcePath: string): SourceSegment[] {
  const answers = new Map(workflow.answerAncestry.map((entry) => [entry.turnId, entry]));
  const segments: SourceSegment[] = [];
  let cursor = 0;
  const add = (text: string, turnId: string, role: "user" | "assistant") => {
    const start = cursor;
    const end = start + text.length;
    const segmentIndex = segments.length;
    segments.push({
      segment_index: segmentIndex,
      segment_id: `seg-${sha256(`${sourcePath}\0${turnId}\0${role}\0${segmentIndex}\0${text}`).slice(0, 16)}`,
      source_kind: "question_turn",
      source_path: sourcePath,
      start,
      end,
      text,
      turn_id: turnId,
      role,
    });
    cursor = end + 2;
  };
  for (const turn of workflow.questionTurns) {
    add(turn.question, turn.turnId, "user");
    const answer = answers.get(turn.turnId);
    if (answer) add(answer.answerText, answer.answerId, "assistant");
  }
  return segments;
}

type QuestionAnswerResolution = { ready: true; text: string } | { ready: false; reasons: string[] };

function validateQuestionAnswerResponse(raw: unknown): QuestionAnswerResolution {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) throw new Error("AI_CONTRACT question_answer response must be an object");
  const record = raw as Record<string, unknown>;
  const unknown = Object.keys(record).filter((key) => !["answer_state", "answer_text", "unresolved_reasons"].includes(key));
  if (unknown.length > 0) throw new Error(`AI_CONTRACT question_answer additional properties: ${unknown.join(",")}`);
  if (record.answer_state !== "answered" && record.answer_state !== "unresolved") throw new Error("AI_CONTRACT question_answer answer_state");
  if (!Array.isArray(record.unresolved_reasons) || record.unresolved_reasons.some((value) => typeof value !== "string" || value.trim() === "")) throw new Error("AI_CONTRACT question_answer unresolved_reasons");
  const reasons = unique(record.unresolved_reasons as string[]);
  if (record.answer_state === "unresolved") {
    if (reasons.length === 0 || record.answer_text !== undefined) throw new Error("AI_CONTRACT unresolved question_answer shape");
    return { ready: false, reasons };
  }
  if (reasons.length > 0) throw new Error("AI_CONTRACT answered question_answer cannot have unresolved reasons");
  if (typeof record.answer_text !== "string") return { ready: false, reasons: ["question_answer_empty"] };
  const answerText = stripOuterTerminalLineEndings(record.answer_text);
  if (answerText.trim() === "") return { ready: false, reasons: ["question_answer_empty"] };
  if (hasQuestionAnswerNoise(answerText)) return { ready: false, reasons: ["question_answer_noise_detected"] };
  return { ready: true, text: answerText };
}

function hasQuestionAnswerNoise(text: string): boolean {
  if (/```|<\/?[A-Za-z][^>]*>|\[[^\]\r\n]+\]\([^\)\r\n]+\)/u.test(text)) return true;
  if (/^(?:答案|回答|answer)\s*[:：]/iu.test(text)) return true;
  return text.split(/\r\n|\r|\n/u).some((line) => {
    const value = line.trim();
    return /^(?:复制|分享|收藏|点赞|下载|返回|菜单)(?:\s|[:：]|$)/u.test(value)
      || /^(?:copyright\s*(?:©|\(c\))?|©\s*\d{4}(?:-\d{4})?|all rights reserved|版权所有)(?:\s+.*)?$/iu.test(value);
  });
}

async function loadQuestionCardStructure(
  workflow: QuestionWorkflowInput,
  contracts: LoadedContracts,
  category: CategoryDefinition,
): Promise<{ document?: JsonObject; requestedTemplates?: string[] }> {
  if (!workflow.cardStructureRef) return {};
  const document = await readJson<JsonObject>(packagePath(contracts.packageRoot, workflow.cardStructureRef));
  if (!document || typeof document !== "object" || Array.isArray(document)) throw new Error("INPUT_INVALID cardStructureRef document");
  const requested = document.requested_templates;
  if (requested === undefined) return { document };
  if (!Array.isArray(requested) || requested.length === 0 || requested.some((value) => typeof value !== "string" || !category.allowed_templates.includes(value))) throw new Error("INPUT_INVALID cardStructureRef requested_templates");
  return { document, requestedTemplates: requested as string[] };
}

function processMediaItem(inputItem: StandardInputItem, contracts: LoadedContracts): ProcessedItem {
  if (inputItem.source.kind !== "media_set") throw new Error("INPUT_INVALID expected media_set");
  const sourceRefs = inputItem.source.assets.map((asset) => ({
    path: asset.material_path,
    sha256: contracts.filesByPath.get(asset.material_path)?.sha256 ?? "",
    role: asset.role,
  }));
  const questions = sourceRefs.filter((ref) => ref.role === "question");
  const answers = sourceRefs.filter((ref) => ref.role === "answer");
  const reasons: string[] = [];
  if (questions.length !== 1) reasons.push("media_question_role_ambiguous");
  if (answers.length === 0) reasons.push("multimodal_answer_provider_required");
  if (answers.length > 1) reasons.push("media_answer_pairing_ambiguous");
  const state: LearningUnit["state"] = reasons.length === 0 ? "ready" : "unresolved";
  const unit: LearningUnit = {
    unit_id: "media-qa-1",
    source_segment_ids: [],
    category_id: "question_answer",
    subtype: "media_problem",
    answer_state: answers.length === 1 ? "provided" : answers.length === 0 ? "missing" : "ambiguous",
    context_state: "not_applicable",
    risk_state: inputItem.hints?.risk_state ?? "ordinary",
    generated_content: [],
    state,
    unresolved_reasons: reasons,
    gates: [{ gate_id: "media.roles", status: state === "ready" ? "pass" : "notice", evidence: state === "ready" ? "唯一 question/answer 素材对" : reasons.join("; ") }],
  };
  const cards: PendingCard[] = [];
  if (state === "ready" && questions[0] && answers[0]) {
    const candidate: CardCandidate = {
      template: "media_qa",
      front_blocks: [{ type: "asset_ref", material_path: questions[0].path, provenance: "source" }],
      back_blocks: [{ type: "asset_ref", material_path: answers[0].path, provenance: "source" }],
      answer_provenance: "source",
    };
    cards.push({ itemId: inputItem.item_id, unit, candidate, ordinal: 0 });
  }
  return {
    item: {
      item_id: inputItem.item_id,
      source_kind: "media_set",
      source_refs: sourceRefs,
      source_segments: [],
      cleaning: { state: "not_applicable", removals: [], format_anomalies: [] },
      units: [unit],
      state: state === "ready" ? "ready" : "unresolved",
      unresolved_reasons: reasons,
      gates: unit.gates,
    },
    cards,
    decisions: [],
  };
}

async function loadTextSource(inputItem: StandardInputItem, contracts: LoadedContracts): Promise<{ text: string; segments: SourceSegment[] }> {
  if (inputItem.source.kind === "media_set") throw new Error("INPUT_INVALID media has no text source");
  if (inputItem.source.kind === "question_workflow") throw new Error("INPUT_INVALID question workflow has no material text source");
  const sourcePath = inputItem.source.material_path;
  const resolved = packagePath(contracts.packageRoot, sourcePath);
  if (inputItem.source.kind === "text" || inputItem.source.kind === "ocr_text") {
    if (inputItem.source.kind === "text" && inputItem.language_selection) {
      const selection = inputItem.language_selection;
      if (!selection.context_enabled) return { text: selection.selected_text, segments: segmentText(selection.selected_text, inputItem.source.material_path, "text") };
      const segments: SourceSegment[] = [];
      for (const context of selection.context_segments) {
        const contextText = await readFile(packagePath(contracts.packageRoot, context.material_path), "utf8");
        for (const part of segmentText(contextText, context.material_path, "text")) {
          segments.push({ ...part, segment_index: segments.length, segment_id: `${context.segment_id}-${part.segment_id}` });
        }
      }
      return { text: segments.map((entry) => entry.text).join("\n\n"), segments };
    }
    const text = await readFile(resolved, "utf8");
    if (text.trim() === "") throw new Error(`INPUT_INVALID empty source ${sourcePath}`);
    return { text, segments: segmentText(text, sourcePath, inputItem.source.kind) };
  }
  const conversation = await readJson<{ schema_version: string; turns: Array<{ turn_id: string; role: "user" | "assistant"; text: string }> }>(resolved);
  if (conversation.schema_version !== "conversation/1.0.0" || !Array.isArray(conversation.turns) || conversation.turns.length === 0) throw new Error(`INPUT_INVALID conversation ${sourcePath}`);
  const segments: SourceSegment[] = [];
  for (const turn of conversation.turns) {
    if (!turn.turn_id || !["user", "assistant"].includes(turn.role) || !turn.text?.trim()) throw new Error(`INPUT_INVALID conversation turn ${sourcePath}`);
    const turnSegments = segmentText(turn.text, sourcePath, "conversation_turn", { id: turn.turn_id, role: turn.role });
    for (const segment of turnSegments) segments.push({ ...segment, segment_index: segments.length });
  }
  return { text: segments.map((segment) => segment.text).join("\n\n"), segments };
}

async function callStage(provider: ContentProvider, traces: AiTrace[], promptRoot: string, stage: ProviderRequest["stage"], callKey: string, payload: JsonObject, category?: CategoryDefinition, signal?: AbortSignal): Promise<unknown> {
  throwIfCancelled(signal);
  const prompt = await loadPrompt(promptRoot, stage, category);
  const requestHash = sha256(stableStringify({ stage, callKey, prompt_sha256: prompt.combinedSha256, payload }));
  const result = await provider.call({ stage, callKey, system: prompt.system, payload, ...(signal ? { signal } : {}) });
  const responseHash = sha256(stableStringify(result.response));
  traces.push({
    stage,
    call_key: callKey,
    mode: result.mode,
    prompt_files: prompt.files,
    prompt_sha256: prompt.combinedSha256,
    request_sha256: requestHash,
    provider: result.provider,
    model: result.model,
    response_sha256: responseHash,
    response: result.response,
    attempts: result.attempts,
  });
  return result.response;
}

function makeLearningUnit(candidate: ClassifiedUnitCandidate, segments: SourceSegment[], generatedContent: Array<{ kind: string; text: string }>, state: LearningUnit["state"], reasons: string[], gates: GateResult[]): LearningUnit {
  return {
    unit_id: candidate.unit_id,
    source_segment_ids: segments.map((segment) => segment.segment_id),
    category_id: candidate.category_id,
    subtype: candidate.subtype,
    answer_state: candidate.answer_state,
    context_state: candidate.context_state,
    risk_state: candidate.risk_state,
    ...(candidate.question ? { question: candidate.question } : {}),
    ...(candidate.supplied_answer ? { supplied_answer: candidate.supplied_answer } : {}),
    ...(candidate.target_term ? { target_term: candidate.target_term } : {}),
    generated_content: generatedContent,
    state,
    unresolved_reasons: unique(reasons),
    gates,
  };
}

function makeCardRecord(taskId: string, pending: PendingCard): CardRecord {
  const identity = `${taskId}\0${pending.itemId}\0${pending.unit.unit_id}\0${pending.candidate.template}\0${pending.ordinal}`;
  const cardId = `card-${sha256(identity).slice(0, 24)}`;
  return {
    card_id: cardId,
    item_id: pending.itemId,
    unit_id: pending.unit.unit_id,
    variant_group_id: `variant-${sha256(`${taskId}\0${pending.itemId}\0${pending.unit.unit_id}`).slice(0, 20)}`,
    category_id: pending.unit.category_id,
    subtype: pending.unit.subtype,
    template: pending.candidate.template,
    front_blocks: pending.candidate.front_blocks.map((block, index) => makeBlock(cardId, "front", index, block)),
    back_blocks: pending.candidate.back_blocks.map((block, index) => makeBlock(cardId, "back", index, block)),
    answer_provenance: pending.candidate.answer_provenance,
    source_segment_ids: pending.unit.source_segment_ids,
  };
}

export function validateCardSourceSegmentReferences(items: LearningItem[], cards: CardRecord[]): GateResult {
  const segmentIdsByItem = new Map<string, Set<string>>();
  for (const item of items) {
    const sourceIds = new Set(item.source_segments.map((segment) => segment.segment_id));
    const proofreadIds = new Set(item.proofreading?.proofread_segments.map((segment) => segment.segment_id) ?? []);
    const collisions = [...proofreadIds].filter((segmentId) => sourceIds.has(segmentId));
    if (collisions.length > 0) {
      throw new ContentGateError("FAILED_CONTENT_GATE learning-bundle.ocr-segment-identity-collision", [{
        gate_id: "learning-bundle.ocr-segment-identity-collision",
        status: "fail",
        evidence: `LearningItem ${item.item_id} source/proofreading segment identities collide: ${collisions.join(",")}`,
      }]);
    }
    const available = new Set(sourceIds);
    segmentIdsByItem.set(item.item_id, available);
    for (const unit of item.units) {
      const missing = unit.source_segment_ids.filter((segmentId) => !available.has(segmentId));
      if (missing.length > 0) {
        throw new ContentGateError("FAILED_CONTENT_GATE learning-bundle.unit-source-segment-reference", [{
          gate_id: "learning-bundle.unit-source-segment-reference",
          status: "fail",
          evidence: `Unit ${unit.unit_id} references missing source segments: ${missing.join(",")}`,
        }]);
      }
    }
  }
  for (const card of cards) {
    const available = segmentIdsByItem.get(card.item_id);
    if (!available) {
      throw new ContentGateError("FAILED_CONTENT_GATE card-bundle.source-item-reference", [{
        gate_id: "card-bundle.source-item-reference",
        status: "fail",
        evidence: `Card ${card.card_id} references missing item ${card.item_id}`,
      }]);
    }
    const missing = card.source_segment_ids.filter((segmentId) => !available.has(segmentId));
    if (missing.length > 0) {
      throw new ContentGateError("FAILED_CONTENT_GATE card-bundle.source-segment-reference", [{
        gate_id: "card-bundle.source-segment-reference",
        status: "fail",
        evidence: `Card ${card.card_id} references missing source segments: ${missing.join(",")}`,
      }]);
    }
  }
  return {
    gate_id: "card-bundle.source-segment-reference",
    status: "pass",
    evidence: `${cards.length} 张卡片及全部 unit 的 source_segment_ids 均可在对应 final/proofread identity space 中解析，且 source/proofreading 无碰撞`,
  };
}

function identifyReleasedOcrSegments(segments: SourceSegment[]): SourceSegment[] {
  return segments.map((segment) => ({
    ...segment,
    segment_id: `rseg-${sha256(`${segment.segment_id}\0${segment.text}`).slice(0, 16)}`,
  }));
}

function makeBlock(cardId: string, side: string, index: number, block: CardBlockCandidate) {
  return { block_id: `block-${sha256(`${cardId}\0${side}\0${index}`).slice(0, 20)}`, ...block };
}

function applyHumanOverrides(candidate: ClassifiedUnitCandidate, item: StandardInputItem): ClassifiedUnitCandidate {
  const overridesRoot = item.human_overrides;
  const unitOverrides = overridesRoot?.units;
  if (!unitOverrides || typeof unitOverrides !== "object" || Array.isArray(unitOverrides)) return candidate;
  const override = (unitOverrides as Record<string, unknown>)[candidate.unit_id];
  if (!override || typeof override !== "object" || Array.isArray(override)) return candidate;
  const allowed = ["category_id", "subtype", "answer_state", "context_state", "risk_state", "question", "supplied_answer", "target_term", "unresolved_reasons"];
  const merged = { ...candidate } as unknown as Record<string, unknown>;
  for (const key of allowed) if (key in (override as Record<string, unknown>)) merged[key] = (override as Record<string, unknown>)[key];
  return merged as unknown as ClassifiedUnitCandidate;
}

function isLanguageCategory(categoryId: string): categoryId is "language_zh" | "language_en" {
  return categoryId === "language_zh" || categoryId === "language_en";
}

function resolveLanguagePracticeMode(input: StandardInput, item: StandardInputItem, configuredDefault?: "ask" | LanguagePracticeMode): LanguagePracticeMode | undefined {
  return item.human_overrides?.languagePracticeMode
    ?? input.human_overrides?.languagePracticeMode
    ?? item.hints?.languagePracticeMode
    ?? input.languagePracticeMode
    ?? (configuredDefault === "ask" ? undefined : configuredDefault);
}

function includesWriting(mode: LanguagePracticeMode): boolean {
  return mode === "writing" || mode === "both";
}

function countLiteralOccurrences(source: string, target: string): number {
  if (target === "") return 0;
  let count = 0;
  let cursor = 0;
  while (true) {
    const index = source.indexOf(target, cursor);
    if (index < 0) return count;
    count += 1;
    cursor = index + target.length;
  }
}

function stripOuterTerminalLineEndings(value: string): string {
  return value.replace(/(?:\r\n|\r|\n)+$/u, "");
}

function replaceChosenOccurrence(source: string, segments: SourceSegment[], occurrence: { segmentId: string; order: number; start: number; end: number; exactText: string; sourceSha256: string }, replacement: string): string {
  const segmentIndex = segments.findIndex((segment) => segment.segment_id === occurrence.segmentId);
  if (segmentIndex < 0) throw new Error("INPUT_STALE candidateOccurrence segment");
  const offset = segments.slice(0, segmentIndex).reduce((sum, segment) => sum + segment.text.length + 2, 0);
  const start = offset + occurrence.start;
  const end = offset + occurrence.end;
  if (source.slice(start, end) !== occurrence.exactText) throw new Error("INPUT_STALE candidateOccurrence replacement");
  return source.slice(0, start) + replacement + source.slice(end);
}

function validateOperation(operation: WorkerPaths["operation"], input: StandardInput): void {
  if (!input.questionWorkflow) {
    if (operation && operation !== "understand-and-card") throw new Error(`INPUT_INVALID operation ${operation} requires questionWorkflow`);
    return;
  }
  const expected = input.questionWorkflow.phase === "answer" ? "question.answer" : "question.make-cards";
  if (operation !== expected) throw new Error(`INPUT_INVALID operation ${operation ?? "missing"} does not match questionWorkflow.phase=${input.questionWorkflow.phase}`);
}

function makeLanguageDecision(
  itemId: string,
  unit: ClassifiedUnitCandidate,
  segments: SourceSegment[],
  reasons: LanguagePracticeDecisionRecord["reasons"],
  details: {
    requestedOverridePaths: string[];
    currentMode?: LanguagePracticeMode;
    targetOccurrenceCount?: number;
    pronunciationState?: "missing" | "unreliable";
    meaningState?: "missing" | "unreliable";
  },
): LanguagePracticeDecisionRecord {
  if (!isLanguageCategory(unit.category_id)) throw new Error("INPUT_INVALID language decision category");
  const identity = `${itemId}\0${unit.unit_id}\0${reasons.join("\0")}\0${details.currentMode ?? "none"}`;
  return {
    decision_id: `decision-${sha256(identity).slice(0, 24)}`,
    item_id: itemId,
    unit_id: unit.unit_id,
    category_id: unit.category_id,
    state: "requires_human",
    reasons,
    allowed_modes: ["meaning-reading", "writing", "both"],
    requested_override_paths: details.requestedOverridePaths,
    source_segment_ids: segments.map((segment) => segment.segment_id),
    ...(unit.target_term ? { target_term: unit.target_term } : {}),
    ...(details.targetOccurrenceCount !== undefined ? { target_occurrence_count: details.targetOccurrenceCount } : {}),
    ...(details.currentMode ? { current_mode: details.currentMode } : {}),
    ...(details.pronunciationState ? { pronunciation_state: details.pronunciationState } : {}),
    ...(details.meaningState ? { meaning_state: details.meaningState } : {}),
  };
}

function enrichLanguageDecision(decision: LanguagePracticeDecisionRecord, item: StandardInputItem, segments: SourceSegment[]): LanguagePracticeDecisionRecord {
  const selection = item.language_selection;
  if (!selection) return decision;
  const targetToken = sha256(stableStringify({ item_id: item.item_id, selected_text_sha256: selection.selected_text_sha256, target_text_sha256: selection.target_text_sha256, context_enabled: selection.context_enabled, context_segment_sha256s: selection.context_segments.map((entry) => entry.sha256) }));
  const selected = item.human_overrides?.languageTargetResolution?.selected_text ?? selection.target_text;
  const candidates = segments.flatMap((segment, order) => {
    const sourceSha = sha256(segment.text);
    const positions: NonNullable<LanguagePracticeDecisionRecord["candidate_positions"]> = [];
    let cursor = 0;
    while (selected !== "") {
      const start = segment.text.indexOf(selected, cursor);
      if (start < 0) break;
      positions.push({ segment_id: segment.segment_id, order, start, end: start + selected.length, exact_text: selected, source_sha256: sourceSha });
      cursor = start + selected.length;
    }
    return positions;
  });
  return {
    ...decision,
    target_term: selected,
    target_token: targetToken,
    selected_text_sha256: selection.selected_text_sha256,
    target_text_sha256: selection.target_text_sha256,
    context_segment_sha256s: selection.context_segments.map((entry) => entry.sha256),
    candidate_positions: candidates,
    suggested_user_fields: ["action", "selected_text", "selected_text_sha256", "target_token", "candidateOccurrence", "confirmed"],
  };
}

type LanguageWritingResolution =
  | { ready: true; pronunciation: string; meaning: string; humanConfirmed: boolean }
  | { ready: false; pronunciationState?: "missing" | "unreliable"; meaningState?: "missing" | "unreliable" };

async function resolveLanguageWritingDetails(
  input: StandardInput,
  item: StandardInputItem,
  unit: ClassifiedUnitCandidate,
  segments: SourceSegment[],
  category: CategoryDefinition,
  promptRoot: string,
  provider: ContentProvider,
  traces: AiTrace[],
  signal?: AbortSignal,
): Promise<LanguageWritingResolution> {
  const human = item.human_overrides?.languageWriting?.units?.[unit.unit_id]
    ?? input.human_overrides?.languageWriting?.units?.[unit.unit_id];
  if (human) return { ready: true, pronunciation: human.pronunciation, meaning: human.meaning, humanConfirmed: true };
  const raw = await callStage(provider, traces, promptRoot, "language_writing", `language_writing:${item.item_id}:${unit.unit_id}`, {
    item_id: item.item_id,
    unit_id: unit.unit_id,
    category_id: unit.category_id,
    target_term: unit.target_term,
    source_segments: segments,
  }, category, signal);
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) throw new Error("AI_CONTRACT language_writing response must be an object");
  const record = raw as Record<string, unknown>;
  const pronunciation = parseWritingField(record.pronunciation, "pronunciation");
  const meaning = parseWritingField(record.meaning, "meaning");
  if (!Array.isArray(record.unresolved_reasons) || record.unresolved_reasons.some((value) => typeof value !== "string" || value.trim() === "")) throw new Error("AI_CONTRACT language_writing unresolved_reasons");
  if (pronunciation.state !== "reliable" || meaning.state !== "reliable") {
    return {
      ready: false,
      ...(pronunciation.state === "reliable" ? {} : { pronunciationState: pronunciation.state }),
      ...(meaning.state === "reliable" ? {} : { meaningState: meaning.state }),
    };
  }
  return { ready: true, pronunciation: pronunciation.value!, meaning: meaning.value!, humanConfirmed: false };
}

function parseWritingField(raw: unknown, name: string): { state: "reliable" | "missing" | "unreliable"; value?: string } {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return { state: "missing" };
  const field = raw as Record<string, unknown>;
  const state = field.reliability;
  if (state !== "reliable" && state !== "missing" && state !== "unreliable") throw new Error(`AI_CONTRACT language_writing ${name}.reliability`);
  if (typeof field.evidence !== "string" || field.evidence.trim() === "") throw new Error(`AI_CONTRACT language_writing ${name}.evidence`);
  const value = typeof field.value === "string" ? field.value.trim() : "";
  if (state === "reliable" && value === "") return { state: "missing" };
  return value === "" ? { state } : { state, value };
}

async function assertOutputDoesNotExist(outputDir: string): Promise<void> {
  try {
    await access(outputDir);
    throw new Error(`OUTPUT_EXISTS ${outputDir}`);
  } catch (error) {
    if (error instanceof Error && error.message.startsWith("OUTPUT_EXISTS")) throw error;
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
  }
}

function normalizeRelative(root: string, target: string): string {
  return path.relative(root, path.resolve(target)).split(path.sep).join("/");
}

function unique(values: string[]): string[] {
  return [...new Set(values.filter((value) => value.trim() !== ""))];
}

function throwIfCancelled(signal?: AbortSignal): void {
  if (signal?.aborted) throw new Error("WORKER_CANCELLED");
}

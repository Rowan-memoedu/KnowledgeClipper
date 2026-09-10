import assert from "node:assert/strict";
import { copyFile, mkdtemp, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { extractOcrLexicalAuditCandidates, validateCards, validateClassification, validateCleaning, validateOcrLexicalAudit, validateOcrProofreading } from "../src/gates.js";
import { validateOcrHumanResolutionCommand } from "../src/ocr-human-resolution.js";
import { runCommonRequest, WORKER_DESCRIBE } from "../src/common-adapter.js";
import { PRODUCT_DISPLAY_NAME, PRODUCT_ID } from "../src/identity.js";
import { ReplayProvider } from "../src/provider.js";
import { copyBuiltInSettings, describeContentCompanion, describeContentSettings, diffContentSettingsDraft, validateContentCompanionSettings, validateContentSettingsDraft } from "../src/settings-adapter.js";
import type { CardBundle, CategoryDefinition, CategoryRegistry, ClassifiedUnitCandidate, CommonArtifactManifest, CommonWorkerRequest, LanguagePracticeDecisionBundle, LearningBundle, OcrHumanResolutionBundle, OcrProofreadBundle, QuestionAnswerBundle, StandardInput, TaskManifest } from "../src/types.js";
import { fileDigest, segmentText, sha256, stableStringify } from "../src/util.js";
import { runWorker, validateCardSourceSegmentReferences } from "../src/worker.js";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const fixtureRoot = path.join(workerRoot, "fixtures", "offline-qa");

async function listFiles(root: string): Promise<string[]> {
  const entries = await readdir(root, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async (entry) => {
    const fullPath = path.join(root, entry.name);
    return entry.isDirectory() ? listFiles(fullPath) : [fullPath];
  }));
  return nested.flat();
}

test("delete-only cleaning rejects lexical rewrite", () => {
  const source = "原文不能改写。";
  const segments = segmentText(source, "materials/source.md", "text");
  assert.throws(() => validateCleaning({ cleaned_text: "原文不可改写。", removed_fragments: [], format_anomalies: [] }, segments), /cleaning\.delete-only/u);
});

test("OCR proofreading accepts a source-neutral one-character insertion with replayable diff", () => {
  const source = "The observatons support the conclusion.";
  const segments = segmentText(source, "materials/ocr.md", "ocr_text");
  const result = validateOcrProofreading({
    proofread_text: "The observations support the conclusion.",
    corrections: [{
      segment_index: 0,
      operation: "insertion",
      start: 12,
      end: 12,
      before: "",
      after: "i",
      confidence: 0.97,
      evidence_type: "lexical_context",
      evidence: "The sentence grammar and word context have one standard completion.",
    }],
    unresolved: [],
  }, segments);
  assert.equal(result.corrections[0]?.edit_distance, 1);
  assert.equal(result.proofreadSegments[0]?.text, "The observations support the conclusion.");
});

test("OCR proofreading retains ambiguous text and publishes unresolved without a correction", () => {
  const source = "Answer: MODL.";
  const segments = segmentText(source, "materials/ocr.md", "ocr_text");
  const result = validateOcrProofreading({
    proofread_text: source,
    corrections: [],
    unresolved: [{ segment_index: 0, start: 8, end: 12, exact_text: "MODL", reason: "insufficient_context", evidence: "Could be an identifier or several different words." }],
  }, segments);
  assert.equal(result.corrections.length, 0);
  assert.equal(result.unresolved[0]?.exact_text, "MODL");
  assert.equal(result.proofreadSegments[0]?.text, source);
});

test("OCR unresolved coordinates relocate unique text and fan out repeated text without releasing either", () => {
  const uniqueSource = "Prefix TRUNC suffix.";
  const unique = validateOcrProofreading({
    proofread_text: uniqueSource,
    corrections: [],
    unresolved: [{ segment_index: 0, start: 0, end: 5, exact_text: "TRUNC", reason: "insufficient_context", evidence: "The token has more than one plausible completion." }],
  }, segmentText(uniqueSource, "materials/unique-unresolved.md", "ocr_text"));
  assert.deepEqual(unique.unresolved.map(({ start, end, exact_text }) => ({ start, end, exact_text })), [{ start: 7, end: 12, exact_text: "TRUNC" }]);
  assert.ok(unique.unresolved[0]?.evidence.includes('"localization":"unique_relocated"'));
  assert.equal(unique.response.proofread_text, uniqueSource);

  const repeatedSource = "CODEX then CODEX.";
  const repeated = validateOcrProofreading({
    proofread_text: repeatedSource,
    corrections: [],
    unresolved: [{ segment_index: 0, start: 2, end: 7, exact_text: "CODEX", reason: "insufficient_context", evidence: "The declared occurrence is not reliable." }],
  }, segmentText(repeatedSource, "materials/repeated-unresolved.md", "ocr_text"));
  assert.deepEqual(repeated.unresolved.map(({ start, end }) => ({ start, end })), [{ start: 0, end: 5 }, { start: 11, end: 16 }]);
  assert.ok(repeated.unresolved.every((candidate) => candidate.reason === "ambiguous_text" && candidate.evidence.includes('"localization":"multiple_matches"')));
  assert.equal(repeated.response.proofread_text, repeatedSource);
});

test("OCR lexical audit covers two anomaly classes and preserves a contextual proper name", async () => {
  const fixture = JSON.parse(await readFile(path.join(workerRoot, "fixtures", "ocr-lexical-audit-v1", "cases.json"), "utf8")) as {
    cases: Array<{ id: string; source: string; verdicts: Record<string, "ordinary" | "contextual_identifier" | "suspicious">; expectedUnresolved: string[] }>;
  };
  for (const entry of fixture.cases) {
    const segments = segmentText(entry.source, `fixtures/${entry.id}.md`, "ocr_text");
    const candidates = extractOcrLexicalAuditCandidates(segments);
    const response = {
      reviewed_tokens: candidates.map((candidate) => ({
        ...candidate,
        verdict: entry.verdicts[candidate.exact_text] ?? "ordinary",
        evidence: entry.verdicts[candidate.exact_text] === "suspicious" ? "The input does not establish a unique reliable reading." : "The token is ordinary or explicitly anchored by the sentence context.",
      })),
    };
    const result = validateOcrLexicalAudit(response, candidates);
    assert.deepEqual(result.unresolved.map((candidate) => candidate.exact_text), entry.expectedUnresolved, entry.id);
  }
});

test("empty primary OCR review cannot silently release a lexical anomaly", async () => {
  const root = path.join(workerRoot, "fixtures", "offline-ocr-ambiguous");
  const source = await readFile(path.join(root, "upstream", "markdown", "content.md"), "utf8");
  const provider = {
    async call(request: { stage: string; payload: Record<string, unknown> }) {
      if (request.stage === "ocr_proofreading") return { response: { proofread_text: source, corrections: [], unresolved: [] }, provider: "fixture", model: "fixture", mode: "replay" as const, attempts: 1 };
      if (request.stage === "ocr_lexical_audit") {
        const candidates = request.payload.candidates as Array<{ exact_text: string }>;
        return {
          response: { reviewed_tokens: candidates.map((candidate) => ({ ...candidate, verdict: candidate.exact_text === "MODL" ? "suspicious" : "ordinary", evidence: candidate.exact_text === "MODL" ? "The token is word-like but the input does not establish a unique reading." : "Ordinary contextual token." })) },
          provider: "fixture", model: "fixture", mode: "replay" as const, attempts: 1,
        };
      }
      throw new Error(`unexpected stage ${request.stage}`);
    },
  };
  const output = await mkdtemp(path.join(tmpdir(), "clipper-ocr-lexical-audit-"));
  const result = await runWorker({
    manifestPath: path.join(root, "artifact.json"),
    inputPath: path.join(root, "inputs", "standard-input.v4.json"),
    outputDir: path.join(output, "attempt"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v2.json"),
    attemptId: "attempt-ocr-lexical-audit",
    jobRoot: root,
    operation: "understand-and-card",
  }, provider);
  assert.equal(result.releaseState, "unresolved");
  assert.equal(result.cardBundlePath, undefined);
  const proofread = JSON.parse(await readFile(result.ocrProofreadBundlePath!, "utf8")) as OcrProofreadBundle;
  assert.equal(proofread.items[0]?.unresolved[0]?.exact_text, "MODL");
  assert.ok(proofread.items[0]?.gates.some((gate) => gate.gate_id === "ocr-proofreading.lexical-audit" && gate.status === "notice"));
});

test("OCR coordinate recovery keeps unique, multiple, missing and maliciously out-of-bounds candidates unresolved", async () => {
  const fixture = JSON.parse(await readFile(path.join(workerRoot, "fixtures", "ocr-coordinate-normalization-v1", "cases.json"), "utf8")) as {
    ordinaryTextDeleteOnly: { source: string; invalidCleanedText: string };
    cases: Array<{
      id: string;
      source: string;
      response: unknown;
      expectedLocalization: string;
      expectedRanges: Array<{ start: number; end: number }>;
      expectedUnresolvedCount: number;
    }>;
  };
  for (const entry of fixture.cases) {
    const result = validateOcrProofreading(entry.response, segmentText(entry.source, `materials/${entry.id}.md`, "ocr_text"));
    assert.equal(result.response.proofread_text, entry.source, `${entry.id}: suggested rewrite must not be applied`);
    assert.equal(result.corrections.length, 0, `${entry.id}: coordinate-uncertain correction must not be released`);
    assert.equal(result.unresolved.length, entry.expectedUnresolvedCount, `${entry.id}: decision candidates`);
    assert.deepEqual(result.unresolved.map(({ start, end }) => ({ start, end })), entry.expectedRanges, `${entry.id}: normalized review ranges`);
    assert.ok(result.unresolved.every((candidate) => candidate.evidence.includes(`\"localization\":\"${entry.expectedLocalization}\"`)), `${entry.id}: localization evidence`);
    assert.ok(result.unresolved.every((candidate) => candidate.evidence.includes("unresolved_human_review_not_applied")), `${entry.id}: disposition`);
    assert.equal(result.gates.find((gate) => gate.gate_id === "ocr-proofreading.coordinate-localization")?.status, "notice");
  }
  const textSegments = segmentText(fixture.ordinaryTextDeleteOnly.source, "materials/plain.md", "text");
  assert.throws(() => validateCleaning({
    cleaned_text: fixture.ordinaryTextDeleteOnly.invalidCleanedText,
    removed_fragments: [],
    format_anomalies: [],
  }, textSegments), /cleaning\.delete-only/u);
});

test("frozen v1.2.18 DeepSeek response is replayed as a normalized human-review candidate", async () => {
  const fixture = JSON.parse(await readFile(path.join(workerRoot, "fixtures", "ocr-coordinate-normalization-v1", "v1218-frozen-evidence.json"), "utf8")) as {
    source: { sourceText: string; sourceSha256: string };
    response: unknown;
    responseSha256: string;
  };
  assert.equal(sha256(fixture.source.sourceText), fixture.source.sourceSha256);
  assert.equal(sha256(stableStringify(fixture.response)), fixture.responseSha256);
  const result = validateOcrProofreading(fixture.response, segmentText(fixture.source.sourceText, "upstream/content.md", "ocr_text"));
  assert.equal(result.response.proofread_text, fixture.source.sourceText);
  assert.equal(result.corrections.length, 0);
  assert.equal(result.unresolved.length, 1);
  assert.equal(result.unresolved[0]?.start, 31);
  assert.equal(result.unresolved[0]?.end, 36);
  assert.equal(result.unresolved[0]?.exact_text, "CANAR");
  assert.ok(result.unresolved[0]?.evidence.includes("CANARY"));
  assert.ok(result.unresolved[0]?.evidence.includes("unique_relocated"));
});

test("OCR proofreading rejects edits to whitespace, code and low-confidence guesses", () => {
  const whitespace = segmentText("alpha beta", "materials/ocr.md", "ocr_text");
  assert.throws(() => validateOcrProofreading({
    proofread_text: "alphabeta",
    corrections: [{ segment_index: 0, operation: "deletion", start: 5, end: 6, before: " ", after: "", confidence: 0.99, evidence_type: "document_context", evidence: "guess" }],
    unresolved: [],
  }, whitespace), /ocr-proofreading\.structure/u);
  const code = segmentText("`CONFG`", "materials/ocr.md", "ocr_text");
  assert.throws(() => validateOcrProofreading({
    proofread_text: "`CONFIG`",
    corrections: [{ segment_index: 0, operation: "insertion", start: 5, end: 5, before: "", after: "I", confidence: 0.99, evidence_type: "lexical_context", evidence: "guess" }],
    unresolved: [],
  }, code), /ocr-proofreading\.protected-content/u);
  const uncertain = segmentText("MODL", "materials/ocr.md", "ocr_text");
  assert.throws(() => validateOcrProofreading({
    proofread_text: "MODEL",
    corrections: [{ segment_index: 0, operation: "insertion", start: 3, end: 3, before: "", after: "E", confidence: 0.7, evidence_type: "lexical_context", evidence: "one possible guess" }],
    unresolved: [],
  }, uncertain), /ocr-proofreading\.confidence/u);
});

test("cleaning rejects flattened Markdown list structure even when non-whitespace text is unchanged", () => {
  const source = "步骤：\n1. 保留标题\n2. 保留缩进\n";
  const segments = segmentText(source, "materials/source.md", "text");
  assert.throws(() => validateCleaning({
    cleaned_text: "步骤： 1. 保留标题 2. 保留缩进",
    removed_fragments: [],
    format_anomalies: [],
  }, segments), /cleaning\.structure-fidelity/u);
});

test("cleaning rejects code-block newline and indentation damage", () => {
  const source = "```text\nx = 1\n  y = 2\n```\n";
  const segments = segmentText(source, "materials/source.md", "text");
  assert.throws(() => validateCleaning({
    cleaned_text: "```text x = 1 y = 2 ```",
    removed_fragments: [],
    format_anomalies: [],
  }, segments), /cleaning\.structure-fidelity/u);
});

test("cleaning does not mistake domain uses of copy, share, or copyright for UI noise", () => {
  const cases = [
    { source: "保留正文。\n\nDNA 复制保证遗传信息传递。\n", exact: "复制", reason: "control" },
    { source: "保留正文。\n\n分享经济是一种资源协同模式。\n", exact: "分享经济是一种资源协同模式。\n", reason: "sharing_prompt" },
    { source: "保留正文。\n\n下载速度会受网络带宽影响。\n", exact: "下载速度会受网络带宽影响。\n", reason: "sharing_prompt" },
    { source: "保留正文。\n\nCopyright 法律保护作品表达。\n", exact: "Copyright 法律保护作品表达。\n", reason: "site_footer" },
  ];
  for (const entry of cases) {
    const segments = segmentText(entry.source, "materials/source.md", "text");
    const cleaned = entry.source.replace(entry.exact, "");
    assert.throws(() => validateCleaning({
      cleaned_text: cleaned,
      removed_fragments: [{ segment_index: 1, exact_text: entry.exact, occurrence: 0, reason: entry.reason, evidence: "错误噪声判断" }],
      format_anomalies: [],
    }, segments), /cleaning\.noise-evidence/u);
  }
});

test("cleaning accepts a line-bounded cluster of sharing and download controls", () => {
  const body = "这是一段需要完整保留、足以独立表达知识含义的正文；它同时提供充分长度，以便删除少量界面控件后仍满足保守清洗比例门禁。\n";
  for (const controlText of ["分享 收藏 下载", "Share Favorite Download"]) {
    const source = `${controlText}\n\n${body}`;
    const segments = segmentText(source, "materials/source.md", "text");
    for (const reason of ["sharing_prompt", "control"]) {
      const accepted = validateCleaning({
        cleaned_text: body,
        removed_fragments: [{ segment_index: 0, exact_text: controlText, occurrence: 0, reason, evidence: "独立分享与下载控件行" }],
        format_anomalies: [],
      }, segments);
      assert.deepEqual(accepted.activeSegmentIndexes, [1]);
      assert.equal(accepted.removals.length, 1);
    }
  }
});

test("Chinese copyright prose is not treated as a footer", () => {
  const source = "保留正文。\n\n版权所有人依法享有复制权和信息网络传播权。\n";
  const segments = segmentText(source, "materials/source.md", "text");
  assert.throws(() => validateCleaning({
    cleaned_text: "保留正文。\n",
    removed_fragments: [{ segment_index: 1, exact_text: "版权所有人依法享有复制权和信息网络传播权。\n", occurrence: 0, reason: "site_footer", evidence: "错误地删除版权法正文" }],
    format_anomalies: [],
  }, segments), /cleaning\.noise-evidence/u);
});

test("advertising and sponsorship prose is not treated as advertisement noise", () => {
  for (const protectedText of ["广告是一种商业传播形式。", "赞助研究需要披露利益冲突。"]) {
    const source = `保留正文。\n\n${protectedText}\n`;
    const segments = segmentText(source, "materials/source.md", "text");
    assert.throws(() => validateCleaning({
      cleaned_text: "保留正文。\n",
      removed_fragments: [{ segment_index: 1, exact_text: `${protectedText}\n`, occurrence: 0, reason: "advertisement", evidence: "错误地按关键词删除领域正文" }],
      format_anomalies: [],
    }, segments), /cleaning\.noise-evidence/u);
  }
});

test("duplicate cleaning only removes a whole duplicate segment and preserves one copy", () => {
  const source = "重复页眉\n\n重复页眉\n\n正文。\n";
  const segments = segmentText(source, "materials/source.md", "text");
  const accepted = validateCleaning({
    cleaned_text: "重复页眉\n\n正文。\n",
    removed_fragments: [{ segment_index: 0, exact_text: "重复页眉", occurrence: 0, reason: "duplicate", evidence: "完整分段逐字重复" }],
    format_anomalies: [],
  }, segments);
  assert.equal(accepted.removals.length, 1);
  assert.deepEqual(accepted.activeSegmentIndexes, [1, 2]);

  assert.throws(() => validateCleaning({
    cleaned_text: "不是噪声。\n\n知识不是噪声。\n\n保留正文。\n",
    removed_fragments: [{ segment_index: 0, exact_text: "知识", occurrence: 0, reason: "duplicate", evidence: "错误地删除重复词语" }],
    format_anomalies: [],
  }, segmentText("知识不是噪声。\n\n知识不是噪声。\n\n保留正文。\n", "materials/source.md", "text")), /cleaning\.duplicate-whole-segment/u);

  assert.throws(() => validateCleaning({
    cleaned_text: "课程页眉扩展\n\n正文。\n",
    removed_fragments: [{ segment_index: 0, exact_text: "课程页眉", occurrence: 0, reason: "duplicate", evidence: "错误地把较长分段中的子串当成完整副本" }],
    format_anomalies: [],
  }, segmentText("课程页眉\n\n课程页眉扩展\n\n正文。\n", "materials/source.md", "text")), /cleaning\.duplicate/u);

  assert.throws(() => validateCleaning({
    cleaned_text: "正文。\n",
    removed_fragments: [
      { segment_index: 0, exact_text: "重复页眉", occurrence: 0, reason: "duplicate", evidence: "删除第一份" },
      { segment_index: 1, exact_text: "重复页眉", occurrence: 0, reason: "duplicate", evidence: "错误地删除最后一份" },
    ],
    format_anomalies: [],
  }, segments), /cleaning\.duplicate-preserve-one/u);
});

test("cleaning cannot delete inline code or single-dollar math as advertisement noise", () => {
  for (const protectedText of ["广告：公式 $x+1$", "广告：运行 `copy()`"]) {
    const source = `保留正文。\n\n${protectedText}\n`;
    const segments = segmentText(source, "materials/source.md", "text");
    assert.throws(() => validateCleaning({
      cleaned_text: "保留正文。\n",
      removed_fragments: [{ segment_index: 1, exact_text: `${protectedText}\n`, occurrence: 0, reason: "advertisement", evidence: "广告行" }],
      format_anomalies: [],
    }, segments), /cleaning\.protected-content/u);
  }
});

test("generated card blocks require an exact generated-content record", () => {
  assert.throws(() => validateCards({
    generated_content: [{ kind: "explanation", text: "词语：释义。" }],
    cards: [{
      template: "language_context",
      front_blocks: [{ type: "text", text: "原句包含词语。", provenance: "source" }],
      back_blocks: [{ type: "text", text: "词语（cí yǔ）：释义。", provenance: "generated" }],
      answer_provenance: "not_applicable",
    }],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-language",
    source_segment_indexes: [0],
    category_id: "language_zh",
    subtype: "word",
    answer_state: "not_applicable",
    context_state: "provided",
    risk_state: "ordinary",
    target_term: "词语",
    unresolved_reasons: [],
  }, {
    id: "language_zh",
    description: "test",
    prompt_file: "categories/language-zh.md",
    allowed_templates: ["language_context"],
  }, "原句包含词语。", new Set()), /card\.generated-block/u);
});

test("source card blocks allow only balanced added emphasis", () => {
  const unit: ClassifiedUnitCandidate = {
    unit_id: "unit-source-emphasis",
    source_segment_indexes: [0],
    category_id: "general_learning",
    subtype: "concept",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    supplied_answer: "关键词用于检索。",
    unresolved_reasons: [],
  };
  const category: CategoryDefinition = {
    id: "general_learning",
    description: "test",
    prompt_file: "categories/general-learning.md",
    allowed_templates: ["basic_qa"],
  };
  const valid = validateCards({
    generated_content: [{ kind: "question_wording", text: "关键词有什么作用？" }],
    cards: [{
      template: "basic_qa",
      front_blocks: [{ type: "text", text: "关键词有什么作用？", provenance: "generated" }],
      back_blocks: [{ type: "markdown", text: "**关键词**用于检索。", provenance: "source" }],
      answer_provenance: "source",
    }],
    unresolved_reasons: [],
  }, unit, category, "关键词用于检索。", new Set());
  assert.equal(valid.cards.length, 1);

  for (const invalidText of ["**关键词用于检索。", "****关键词用于检索。"]) {
    assert.throws(() => validateCards({
      generated_content: [{ kind: "question_wording", text: "关键词有什么作用？" }],
      cards: [{
        template: "basic_qa",
        front_blocks: [{ type: "text", text: "关键词有什么作用？", provenance: "generated" }],
        back_blocks: [{ type: "markdown", text: invalidText, provenance: "source" }],
        answer_provenance: "source",
      }],
      unresolved_reasons: [],
    }, unit, category, "关键词用于检索。", new Set()), /card\.source-block/u);
  }
});

test("generated card blocks reject substring-only generated-content registration", () => {
  assert.throws(() => validateCards({
    generated_content: [{ kind: "question_wording", text: "前缀：应如何操作？（请简答）" }],
    cards: [{
      template: "procedure",
      front_blocks: [{ type: "text", text: "应如何操作？", provenance: "generated" }],
      back_blocks: [{ type: "text", text: "先检查输入。", provenance: "source" }],
      answer_provenance: "source",
    }],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-procedure",
    source_segment_indexes: [0],
    category_id: "general_learning",
    subtype: "procedure",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    supplied_answer: "先检查输入。",
    unresolved_reasons: [],
  }, {
    id: "general_learning",
    description: "test",
    prompt_file: "categories/general-learning.md",
    allowed_templates: ["procedure"],
  }, "先检查输入。", new Set()), /card\.generated-block/u);
});

test("generated content cannot contain unused model text", () => {
  assert.throws(() => validateCards({
    generated_content: [
      { kind: "question_wording", text: "应如何操作？" },
      { kind: "unused_draft", text: "这是一段未使用的草稿。" },
    ],
    cards: [{
      template: "procedure",
      front_blocks: [{ type: "text", text: "应如何操作？", provenance: "generated" }],
      back_blocks: [{ type: "text", text: "先检查输入。", provenance: "source" }],
      answer_provenance: "source",
    }],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-unused-generated",
    source_segment_indexes: [0],
    category_id: "general_learning",
    subtype: "procedure",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    supplied_answer: "先检查输入。",
    unresolved_reasons: [],
  }, {
    id: "general_learning",
    description: "test",
    prompt_file: "categories/general-learning.md",
    allowed_templates: ["procedure"],
  }, "先检查输入。", new Set()), /card\.unused-generated-content/u);
});

test("card output cannot mix released cards with unresolved reasons", () => {
  assert.throws(() => validateCards({
    generated_content: [{ kind: "question_wording", text: "应如何操作？" }],
    cards: [{
      template: "procedure",
      front_blocks: [{ type: "text", text: "应如何操作？", provenance: "generated" }],
      back_blocks: [{ type: "text", text: "先检查输入。", provenance: "source" }],
      answer_provenance: "source",
    }],
    unresolved_reasons: ["仍有未决内容"],
  }, {
    unit_id: "unit-mixed-state",
    source_segment_indexes: [0],
    category_id: "general_learning",
    subtype: "procedure",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    supplied_answer: "先检查输入。",
    unresolved_reasons: [],
  }, {
    id: "general_learning",
    description: "test",
    prompt_file: "categories/general-learning.md",
    allowed_templates: ["procedure"],
  }, "先检查输入。", new Set()), /card\.mixed-state/u);
});

test("model output cannot claim human-confirmed card provenance without a validated card override", () => {
  assert.throws(() => validateCards({
    generated_content: [],
    cards: [{
      template: "basic_qa",
      front_blocks: [{ type: "text", text: "问题？", provenance: "source" }],
      back_blocks: [{ type: "text", text: "人工答案", provenance: "human_confirmed" }],
      answer_provenance: "human_confirmed",
    }],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-qa",
    source_segment_indexes: [0],
    category_id: "question_answer",
    subtype: "qa",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    question: "问题？",
    supplied_answer: "来源答案",
    unresolved_reasons: [],
  }, {
    id: "question_answer",
    description: "test",
    prompt_file: "categories/question-answer.md",
    allowed_templates: ["basic_qa"],
  }, "问题？\n\n来源答案", new Set()), /card\.human-confirmed/u);
});

test("answer provenance must be represented by a matching back block", () => {
  assert.throws(() => validateCards({
    generated_content: [{ kind: "answer", text: "生成答案" }],
    cards: [{
      template: "basic_qa",
      front_blocks: [{ type: "text", text: "问题？", provenance: "source" }],
      back_blocks: [{ type: "text", text: "仅有上下文。", provenance: "source" }],
      answer_provenance: "generated",
    }],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-qa",
    source_segment_indexes: [0],
    category_id: "question_answer",
    subtype: "qa",
    answer_state: "missing",
    context_state: "provided",
    risk_state: "ordinary",
    question: "问题？",
    unresolved_reasons: [],
  }, {
    id: "question_answer",
    description: "test",
    prompt_file: "categories/question-answer.md",
    allowed_templates: ["basic_qa"],
  }, "问题？\n\n仅有上下文。", new Set()), /card\.answer-block-provenance/u);
});

test("AI card generation cannot mark an asset reference as generated", () => {
  assert.throws(() => validateCards({
    generated_content: [],
    cards: [{
      template: "media_qa",
      front_blocks: [{ type: "asset_ref", material_path: "materials/question.png", provenance: "generated" }],
      back_blocks: [{ type: "asset_ref", material_path: "materials/answer.png", provenance: "source" }],
      answer_provenance: "source",
    }],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-media",
    source_segment_indexes: [0],
    category_id: "question_answer",
    subtype: "media_problem",
    answer_state: "provided",
    context_state: "not_applicable",
    risk_state: "ordinary",
    unresolved_reasons: [],
  }, {
    id: "question_answer",
    description: "test",
    prompt_file: "categories/question-answer.md",
    allowed_templates: ["media_qa"],
  }, "", new Set(["materials/question.png", "materials/answer.png"])), /card\.asset-provenance/u);
});

test("card generation rejects identical front and back sides", () => {
  assert.throws(() => validateCards({
    generated_content: [],
    cards: [{
      template: "basic_qa",
      front_blocks: [{ type: "text", text: "同一文本", provenance: "source" }],
      back_blocks: [{ type: "text", text: "同一文本", provenance: "source" }],
      answer_provenance: "source",
    }],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-same",
    source_segment_indexes: [0],
    category_id: "question_answer",
    subtype: "qa",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    question: "同一文本",
    supplied_answer: "同一文本",
    unresolved_reasons: [],
  }, {
    id: "question_answer",
    description: "test",
    prompt_file: "categories/question-answer.md",
    allowed_templates: ["basic_qa"],
  }, "同一文本", new Set()), /card\.same-sides/u);
});

test("card generation rejects duplicate cards within one unit", () => {
  const duplicateCard = {
    template: "procedure",
    front_blocks: [{ type: "text", text: "如何操作？", provenance: "generated" }],
    back_blocks: [{ type: "text", text: "先检查输入。", provenance: "source" }],
    answer_provenance: "source",
  };
  assert.throws(() => validateCards({
    generated_content: [{ kind: "question_wording", text: "如何操作？" }],
    cards: [duplicateCard, duplicateCard],
    unresolved_reasons: [],
  }, {
    unit_id: "unit-duplicate",
    source_segment_indexes: [0],
    category_id: "general_learning",
    subtype: "procedure",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    supplied_answer: "先检查输入。",
    unresolved_reasons: [],
  }, {
    id: "general_learning",
    description: "test",
    prompt_file: "categories/general-learning.md",
    allowed_templates: ["procedure"],
  }, "先检查输入。", new Set()), /card\.duplicate/u);
});

test("language units cannot reinterpret the target term as a provided answer", async () => {
  const registry = JSON.parse(await readFile(path.join(workerRoot, "config", "categories.v1.json"), "utf8")) as CategoryRegistry;
  const source = "The committee reached a tentative conclusion.";
  const segments = segmentText(source, "materials/source.md", "text");
  assert.throws(() => validateClassification({ units: [{
    unit_id: "unit-language",
    source_segment_indexes: [0],
    category_id: "language_en",
    subtype: "vocabulary",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    supplied_answer: "tentative",
    target_term: "tentative",
    unresolved_reasons: [],
  }] }, segments, [0], registry), /classification\.language-answer-state/u);
});

test("classification rejects duplicate source indexes and generated language targets", async () => {
  const registry = JSON.parse(await readFile(path.join(workerRoot, "config", "categories.v1.json"), "utf8")) as CategoryRegistry;
  const source = "The committee reached a tentative conclusion.";
  const segments = segmentText(source, "materials/source.md", "text");
  const base = {
    unit_id: "unit-language-source",
    source_segment_indexes: [0],
    category_id: "language_en",
    subtype: "vocabulary",
    answer_state: "not_applicable",
    context_state: "provided",
    risk_state: "ordinary",
    target_term: "tentative",
    unresolved_reasons: [],
  };
  assert.throws(() => validateClassification({ units: [{ ...base, source_segment_indexes: [0, 0] }] }, segments, [0], registry), /classification\.unique-source/u);
  assert.throws(() => validateClassification({ units: [{ ...base, target_term: "provisional" }] }, segments, [0], registry), /classification\.source-target/u);
});

test("classification question is extractive and cannot contain generated card wording", async () => {
  const registry = JSON.parse(await readFile(path.join(workerRoot, "config", "categories.v1.json"), "utf8")) as CategoryRegistry;
  const source = "解一元一次方程时，先移项并合并同类项。";
  const segments = segmentText(source, "materials/source.md", "text");
  assert.throws(() => validateClassification({ units: [{
    unit_id: "unit-procedure",
    source_segment_indexes: [0],
    category_id: "general_learning",
    subtype: "procedure",
    answer_state: "provided",
    context_state: "provided",
    risk_state: "ordinary",
    question: "如何解一元一次方程？",
    supplied_answer: source,
    unresolved_reasons: [],
  }] }, segments, [0], registry), /classification\.source-question/u);
});

test("offline replay produces linked ready bundles and refuses overwrite", async () => {
  const temp = await mkdtemp(path.join(tmpdir(), "clipper-content-test-"));
  const output = path.join(temp, "attempt-1");
  const replay = await ReplayProvider.fromFile(path.join(fixtureRoot, "provider-replay.v1.json"));
  const paths = {
    manifestPath: path.join(fixtureRoot, "manifest.v1.json"),
    inputPath: path.join(fixtureRoot, "inputs", "standard-input.v1.json"),
    outputDir: output,
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v1.json"),
    attemptId: "attempt-1",
  };
  const result = await runWorker(paths, replay);
  assert.equal(result.releaseState, "ready");
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.items[0]?.cleaning.removals.length, 1);
  assert.equal(learning.ai_trace.length, 3);
  assert.ok(result.cardBundlePath);
  const cards = JSON.parse(await readFile(result.cardBundlePath, "utf8")) as CardBundle;
  assert.equal(cards.cards.length, 1);
  assert.equal(cards.learning_bundle_sha256, result.learningBundleSha256);
  await assert.rejects(() => runWorker(paths, replay), /OUTPUT_EXISTS/u);
  const second = await runWorker({ ...paths, outputDir: path.join(temp, "attempt-2"), attemptId: "attempt-2" }, replay);
  const secondCards = JSON.parse(await readFile(second.cardBundlePath!, "utf8")) as CardBundle;
  assert.equal(secondCards.cards[0]?.card_id, cards.cards[0]?.card_id);
  assert.notEqual(second.learningBundleSha256, result.learningBundleSha256);
});

test("independent OCR missing-character fixture emits immutable proofread bundle, LearningBundle v2 and card", async () => {
  const root = path.join(workerRoot, "fixtures", "offline-ocr-missing-character");
  const originalPath = path.join(root, "upstream", "markdown", "content.md");
  const before = await fileDigest(originalPath);
  const replay = await ReplayProvider.fromFile(path.join(root, "provider-replay.v1.json"));
  const output = await mkdtemp(path.join(tmpdir(), "clipper-ocr-missing-output-"));
  const result = await runWorker({
    manifestPath: path.join(root, "artifact.json"),
    inputPath: path.join(root, "inputs", "standard-input.v4.json"),
    outputDir: path.join(output, "attempt"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v2.json"),
    attemptId: "attempt-ocr-missing",
    jobRoot: root,
    operation: "understand-and-card",
  }, replay);
  const after = await fileDigest(originalPath);
  assert.deepEqual(after, before);
  assert.equal(result.learningBundleSchemaVersion, "learning-bundle/2.0.0");
  assert.equal(result.learningBundleContractVersion, "2.0.0");
  assert.ok(result.ocrProofreadBundlePath);
  assert.ok(result.cardBundlePath);
  const proofreading = JSON.parse(await readFile(result.ocrProofreadBundlePath, "utf8")) as OcrProofreadBundle;
  assert.equal(proofreading.items[0]?.corrections[0]?.before, "");
  assert.equal(proofreading.items[0]?.corrections[0]?.after, "N");
  assert.equal(proofreading.items[0]?.corrections[0]?.edit_distance, 1);
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.schema_version, "learning-bundle/2.0.0");
  assert.equal(proofreading.items[0]?.source_segments[1]?.text, "Answer: PHOTOSYTHESIS.");
  assert.equal(learning.items[0]?.source_segments[1]?.text, "Answer: PHOTOSYNTHESIS.");
  assert.equal(learning.items[0]?.proofreading?.proofread_segments[1]?.text, "Answer: PHOTOSYNTHESIS.");
  assert.ok(learning.items[0]?.source_segments.every((segment) => segment.segment_id.startsWith("rseg-")));
  assert.ok(learning.items[0]?.proofreading?.proofread_segments.every((segment) => segment.segment_id.startsWith("pseg-")));
  assert.equal(learning.items[0]?.source_segments.some((source) => learning.items[0]?.proofreading?.proofread_segments.some((proofread) => proofread.segment_id === source.segment_id)), false);
  assert.equal(learning.items[0]?.cleaning.removals[0]?.reason, "site_footer");
  const cards = JSON.parse(await readFile(result.cardBundlePath, "utf8")) as CardBundle;
  assert.equal(cards.cards[0]?.back_blocks[0]?.text, "PHOTOSYNTHESIS.");
  assert.ok(cards.cards[0]?.source_segment_ids.every((segmentId) => learning.items[0]?.source_segments.some((segment) => segment.segment_id === segmentId)));
  const oldMissingItems = structuredClone(learning.items);
  oldMissingItems[0]!.source_segments = structuredClone(proofreading.items[0]!.source_segments);
  const oldMissingCards = structuredClone(cards.cards);
  oldMissingCards[0]!.source_segment_ids = learning.items[0]!.proofreading!.proofread_segments.map((segment) => segment.segment_id);
  assert.throws(() => validateCardSourceSegmentReferences(oldMissingItems, oldMissingCards), /source-segment-reference/u);
});

test("independent OCR ambiguity fixture retains original, marks unresolved and withholds cards", async () => {
  const root = path.join(workerRoot, "fixtures", "offline-ocr-ambiguous");
  const replay = await ReplayProvider.fromFile(path.join(root, "provider-replay.v1.json"));
  const output = await mkdtemp(path.join(tmpdir(), "clipper-ocr-ambiguous-output-"));
  const result = await runWorker({
    manifestPath: path.join(root, "artifact.json"),
    inputPath: path.join(root, "inputs", "standard-input.v4.json"),
    outputDir: path.join(output, "attempt"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v2.json"),
    attemptId: "attempt-ocr-ambiguous",
    jobRoot: root,
    operation: "understand-and-card",
  }, replay);
  assert.equal(result.releaseState, "unresolved");
  assert.equal(result.cardBundlePath, undefined);
  const proofreading = JSON.parse(await readFile(result.ocrProofreadBundlePath!, "utf8")) as OcrProofreadBundle;
  assert.equal(proofreading.items[0]?.original_text, proofreading.items[0]?.proofread_text);
  assert.equal(proofreading.items[0]?.corrections.length, 0);
  assert.equal(proofreading.items[0]?.unresolved[0]?.exact_text, "MODL");
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.items[0]?.state, "unresolved");
  assert.ok(learning.items[0]?.unresolved_reasons.includes("ocr_insufficient_context"));
});

test("OCR resume command accepts strict accept_original and user_correction payloads and rejects unknown or incomplete actions", async () => {
  const root = path.join(workerRoot, "fixtures", "ocr-human-resolution-v1", "commands");
  const accept = validateOcrHumanResolutionCommand(JSON.parse(await readFile(path.join(root, "accept-original.valid.json"), "utf8")));
  assert.equal(accept.action, "accept_original");
  const correction = validateOcrHumanResolutionCommand(JSON.parse(await readFile(path.join(root, "user-correction.valid.json"), "utf8")));
  assert.equal(correction.action, "user_correction");
  assert.equal(correction.correction?.diffs[0]?.after, "E");
  for (const invalid of ["unknown-action.invalid.json", "correction-missing.invalid.json"]) {
    const raw = JSON.parse(await readFile(path.join(root, invalid), "utf8"));
    assert.throws(() => validateOcrHumanResolutionCommand(raw), /OCR_RESOLUTION_COMMAND_INVALID/u);
  }
});

test("accept_original resumes from frozen unresolved OCR without re-running OCR and publishes auditable downstream bundles", async () => {
  const staged = await stageOcrHumanResolution("accept_original");
  const replay = await ReplayProvider.fromFile(path.join(workerRoot, "fixtures", "ocr-human-resolution-v1", "accept-original.provider-replay.v1.json"));
  const result = await runWorker(staged.paths, replay);
  assert.equal(result.releaseState, "ready");
  assert.equal(result.ocrProofreadBundlePath, undefined);
  assert.ok(result.ocrHumanResolutionBundlePath);
  assert.equal(result.learningBundleSchemaVersion, "learning-bundle/3.0.0");
  assert.ok(result.cardBundlePath);
  const audit = JSON.parse(await readFile(result.ocrHumanResolutionBundlePath!, "utf8")) as OcrHumanResolutionBundle;
  assert.equal(audit.records[0]?.action, "accept_original");
  assert.equal(audit.records[0]?.provenance, "human_submitted");
  assert.equal(audit.records[0]?.source_ocr.text, audit.records[0]?.frozen_proofread.text);
  assert.deepEqual(audit.records[0]?.user_diffs, []);
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.ai_trace.some((trace) => trace.stage === "ocr_proofreading"), false);
  assert.equal(learning.items[0]?.human_resolution?.action, "accept_original");
  const cards = JSON.parse(await readFile(result.cardBundlePath!, "utf8")) as CardBundle;
  assert.equal(cards.cards[0]?.back_blocks[0]?.text, "Answer: MODL.");
  assert.ok(learning.items[0]?.source_segments.every((segment) => segment.segment_id.startsWith("hseg-")));
  assert.ok(learning.items[0]?.proofreading?.proofread_segments.every((segment) => segment.segment_id.startsWith("pseg-")));
  assert.equal(learning.items[0]?.source_segments.some((source) => learning.items[0]?.proofreading?.proofread_segments.some((proofread) => proofread.segment_id === source.segment_id)), false);
  assert.ok(cards.cards[0]?.source_segment_ids.every((segmentId) => learning.items[0]?.source_segments.some((segment) => segment.segment_id === segmentId)));
  assert.ok(learning.gates.some((gate) => gate.gate_id === "card-bundle.source-segment-reference" && gate.status === "pass"));
  const collidingItems = structuredClone(learning.items);
  collidingItems[0]!.source_segments = structuredClone(collidingItems[0]!.proofreading!.proofread_segments);
  assert.throws(() => validateCardSourceSegmentReferences(collidingItems, cards.cards), /ocr-segment-identity-collision/u);
  const missingReferenceCards = structuredClone(cards.cards);
  missingReferenceCards[0]!.source_segment_ids = ["hseg-not-declared"];
  assert.throws(() => validateCardSourceSegmentReferences(learning.items, missingReferenceCards), /source-segment-reference/u);
});

test("user_correction replays only inside the frozen candidate, protects user characters, and does not claim AI correction", async () => {
  const staged = await stageOcrHumanResolution("user_correction");
  const replay = await ReplayProvider.fromFile(path.join(workerRoot, "fixtures", "ocr-human-resolution-v1", "user-correction.provider-replay.v1.json"));
  const result = await runWorker(staged.paths, replay);
  assert.equal(result.releaseState, "ready");
  const audit = JSON.parse(await readFile(result.ocrHumanResolutionBundlePath!, "utf8")) as OcrHumanResolutionBundle;
  assert.equal(audit.records[0]?.source_ocr.text.includes("MODL"), true);
  assert.equal(audit.records[0]?.resolved_text.includes("MODEL"), true);
  assert.equal(audit.records[0]?.actor, "user");
  assert.equal(audit.records[0]?.provenance, "human_submitted");
  assert.equal(audit.records[0]?.user_diffs[0]?.after, "E");
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.ai_trace.some((trace) => trace.stage === "ocr_proofreading"), false);
  assert.ok(learning.items[0]?.gates.some((gate) => gate.gate_id === "cleaning.human-resolution-protected" && gate.status === "pass"));
  const cards = JSON.parse(await readFile(result.cardBundlePath!, "utf8")) as CardBundle;
  assert.equal(cards.cards[0]?.back_blocks[0]?.text, "Answer: MODEL.");
  assert.ok(cards.cards[0]?.source_segment_ids.every((segmentId) => learning.items[0]?.source_segments.some((segment) => segment.segment_id === segmentId)));
  assert.ok(learning.items[0]?.source_segments.every((segment) => segment.segment_id.startsWith("hseg-")));
});

test("common WorkerRequest publishes the human-resolution audit, LearningBundle v3 and CardBundle with frozen ancestry", async () => {
  const staged = await stageOcrHumanResolution("user_correction");
  const requestPath = await writeOcrHumanCommonRequest(staged.jobRoot, "user_correction");
  const result = await runCommonRequest(requestPath, workerRoot);
  assert.equal(result.status, "completed", JSON.stringify(result));
  assert.deepEqual(result.outputs.map((entry) => [entry.kind, entry.contractVersion]), [
    ["ocr-human-resolution-bundle", "1.0.0"],
    ["learning-bundle", "3.0.0"],
    ["card-bundle", "1.0.0"],
  ]);
  const resolutionRef = result.outputs[0]!;
  const manifest = JSON.parse(await readFile(path.join(staged.jobRoot, ...resolutionRef.manifestPath.split("/")), "utf8")) as CommonArtifactManifest;
  assert.ok(manifest.inputRefs.some((entry) => entry.artifactRef === "ocr-proofread-frozen-ambiguous-v1"));
  assert.equal(manifest.producer.workerVersion, "0.12.0");
  const learning = await readOutputBundle<LearningBundle>(staged.jobRoot, result, "learning-bundle");
  assert.equal(learning.schema_version, "learning-bundle/3.0.0");
  assert.equal(learning.ai_trace.some((trace) => trace.stage === "ocr_proofreading"), false);
});

test("OCR human resolution fails closed for tampered ancestry, diff outside candidate, hash mismatch and cleaning removal of user-confirmed characters", async () => {
  for (const [id, mutate, expected] of [
    ["candidate", (input: StandardInput) => { input.items[0]!.ocr_human_resolution!.unresolved_candidates[0]!.exact_text = "MODEL"; }, /candidates/u],
    ["outside", (input: StandardInput) => { const diff = input.items[0]!.ocr_human_resolution!.correction!.diffs[0]!; diff.start = 1; diff.end = 1; }, /outside unresolved candidate/u],
    ["hash", (input: StandardInput) => { input.items[0]!.ocr_human_resolution!.correction!.corrected_text_sha256 = "0".repeat(64); }, /corrected_text_sha256/u],
  ] as const) {
    const staged = await stageOcrHumanResolution("user_correction", mutate);
    const replay = await ReplayProvider.fromFile(path.join(workerRoot, "fixtures", "ocr-human-resolution-v1", "user-correction.provider-replay.v1.json"));
    await assert.rejects(() => runWorker({ ...staged.paths, outputDir: `${staged.paths.outputDir}-${id}` }, replay), expected);
  }

  const staged = await stageOcrHumanResolution("user_correction");
  const maliciousReplayPath = path.join(staged.jobRoot, "malicious-replay.json");
  const malicious = JSON.parse(await readFile(path.join(workerRoot, "fixtures", "ocr-human-resolution-v1", "user-correction.provider-replay.v1.json"), "utf8"));
  malicious.responses["cleaning:item-ocr-ambiguous"] = {
    cleaned_text: "Question: Which code is shown?\n\nAnswer: MODL.",
    removed_fragments: [{ segment_index: 1, exact_text: "E", occurrence: 0, reason: "control", evidence: "malicious removal" }],
    format_anomalies: [],
  };
  await writeFile(maliciousReplayPath, `${stableStringify(malicious, 2)}\n`);
  const maliciousReplay = await ReplayProvider.fromFile(maliciousReplayPath);
  const result = await runWorker({ ...staged.paths, outputDir: `${staged.paths.outputDir}-protected` }, maliciousReplay);
  assert.equal(result.releaseState, "failed");
  assert.equal(result.cardBundlePath, undefined);
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.match(learning.items[0]?.unresolved_reasons[0] ?? "", /cleaning\.noise-evidence|human-resolution\.user-characters/u);
});

test("ordinary text copyright cleaning keeps 0.7 delete-only semantics and LearningBundle v1", async () => {
  const replay = await ReplayProvider.fromFile(path.join(fixtureRoot, "provider-replay.v1.json"));
  const output = await mkdtemp(path.join(tmpdir(), "clipper-ordinary-delete-only-"));
  const result = await runWorker({
    manifestPath: path.join(fixtureRoot, "manifest.v1.json"),
    inputPath: path.join(fixtureRoot, "inputs", "standard-input.v1.json"),
    outputDir: path.join(output, "attempt"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v1.json"),
    attemptId: "attempt-ordinary-regression",
  }, replay);
  assert.equal(result.learningBundleSchemaVersion, "learning-bundle/1.0.0");
  assert.equal(result.ocrProofreadBundlePath, undefined);
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.items[0]?.cleaning.removals[0]?.reason, "site_footer");
  assert.doesNotMatch(learning.items[0]?.cleaning.cleaned_text ?? "", /Copyright|分享/u);
  assert.equal(learning.ai_trace[0]?.stage, "cleaning");
});

test("failed cleaning gate emits a failed LearningBundle and no CardBundle", async () => {
  const temp = await mkdtemp(path.join(tmpdir(), "clipper-failed-clean-test-"));
  const replayData = JSON.parse(await readFile(path.join(fixtureRoot, "provider-replay.v1.json"), "utf8")) as { responses: Record<string, unknown>; schema_version: string };
  const cleaning = replayData.responses["cleaning:item-qa"] as { cleaned_text: string };
  cleaning.cleaned_text = cleaning.cleaned_text.replace("蓝色", "红色");
  const replayPath = path.join(temp, "replay.json");
  await writeFile(replayPath, `${JSON.stringify(replayData, null, 2)}\n`);
  const replay = await ReplayProvider.fromFile(replayPath);
  const result = await runWorker({
    manifestPath: path.join(fixtureRoot, "manifest.v1.json"),
    inputPath: path.join(fixtureRoot, "inputs", "standard-input.v1.json"),
    outputDir: path.join(temp, "attempt-failed"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v1.json"),
    attemptId: "attempt-failed",
  }, replay);
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.release_state, "failed");
  assert.equal(learning.items[0]?.state, "failed_content_gate");
  assert.equal(result.cardBundlePath, undefined);
});

test("a new registry category works without code changes", async () => {
  const temp = await mkdtemp(path.join(tmpdir(), "clipper-category-extension-test-"));
  const registry = JSON.parse(await readFile(path.join(workerRoot, "config", "categories.v1.json"), "utf8")) as CategoryRegistry;
  registry.categories.push({
    id: "science_experiment",
    description: "后续增加的科学实验类",
    prompt_file: "categories/general-learning.md",
    allowed_templates: ["basic_qa"],
  });
  const registryPath = path.join(temp, "categories.extended.json");
  await writeFile(registryPath, `${JSON.stringify(registry, null, 2)}\n`);
  const replayData = JSON.parse(await readFile(path.join(fixtureRoot, "provider-replay.v1.json"), "utf8")) as { responses: Record<string, unknown>; schema_version: string };
  const classification = replayData.responses["classification:item-qa"] as { units: Array<{ category_id: string }> };
  classification.units[0]!.category_id = "science_experiment";
  const replayPath = path.join(temp, "replay.json");
  await writeFile(replayPath, `${JSON.stringify(replayData, null, 2)}\n`);
  const replay = await ReplayProvider.fromFile(replayPath);
  const result = await runWorker({
    manifestPath: path.join(fixtureRoot, "manifest.v1.json"),
    inputPath: path.join(fixtureRoot, "inputs", "standard-input.v1.json"),
    outputDir: path.join(temp, "attempt-extension"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath,
    attemptId: "attempt-extension",
  }, replay);
  const cards = JSON.parse(await readFile(result.cardBundlePath!, "utf8")) as CardBundle;
  assert.equal(cards.cards[0]?.category_id, "science_experiment");
});

test("paired source images pass through without OCR or AI", async () => {
  const temp = await mkdtemp(path.join(tmpdir(), "clipper-media-test-"));
  await mkdir(path.join(temp, "inputs"));
  await mkdir(path.join(temp, "materials"));
  await writeFile(path.join(temp, "materials", "question.png"), Buffer.from("question-image-fixture"));
  await writeFile(path.join(temp, "materials", "answer.png"), Buffer.from("answer-image-fixture"));
  const input = {
    schema_version: "standard-input/1.0.0",
    package_id: "media-pair-package",
    task_id: "media-pair-task",
    input_version: "1",
    mode: "card",
    items: [{
      item_id: "media-1",
      source: {
        kind: "media_set",
        assets: [
          { material_path: "materials/question.png", role: "question" },
          { material_path: "materials/answer.png", role: "answer" },
        ],
      },
    }],
  };
  const inputPath = path.join(temp, "inputs", "standard-input.v1.json");
  await writeFile(inputPath, `${JSON.stringify(input, null, 2)}\n`);
  const declarations = await Promise.all([
    declaration(temp, "inputs/standard-input.v1.json", "application/json"),
    declaration(temp, "materials/question.png", "image/png"),
    declaration(temp, "materials/answer.png", "image/png"),
  ]);
  const manifest: TaskManifest = {
    schema_version: "task-manifest/1.0.0",
    package_id: "media-pair-package",
    package_version: "1",
    created_at: "2026-08-25T00:00:00.000Z",
    files: declarations,
  };
  const manifestPath = path.join(temp, "manifest.v1.json");
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  const replay = await ReplayProvider.fromFile(path.join(fixtureRoot, "provider-replay.v1.json"));
  const result = await runWorker({
    manifestPath,
    inputPath,
    outputDir: path.join(temp, "attempt-media"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v1.json"),
    attemptId: "attempt-media",
  }, replay);
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  const cards = JSON.parse(await readFile(result.cardBundlePath!, "utf8")) as CardBundle;
  assert.equal(learning.ai_trace.length, 0);
  assert.equal(cards.cards[0]?.front_blocks[0]?.type, "asset_ref");
  assert.equal(cards.cards[0]?.back_blocks[0]?.type, "asset_ref");
});

test("question image without answer remains traceable unresolved", async () => {
  const temp = await mkdtemp(path.join(tmpdir(), "clipper-media-unresolved-test-"));
  await mkdir(path.join(temp, "inputs"));
  await mkdir(path.join(temp, "materials"));
  await writeFile(path.join(temp, "materials", "question.png"), Buffer.from("question-only-fixture"));
  const input = {
    schema_version: "standard-input/1.0.0",
    package_id: "media-question-package",
    task_id: "media-question-task",
    input_version: "1",
    mode: "card",
    items: [{ item_id: "media-question", source: { kind: "media_set", assets: [{ material_path: "materials/question.png", role: "question" }] } }],
  };
  const inputPath = path.join(temp, "inputs", "standard-input.v1.json");
  await writeFile(inputPath, `${JSON.stringify(input, null, 2)}\n`);
  const manifest: TaskManifest = {
    schema_version: "task-manifest/1.0.0",
    package_id: "media-question-package",
    package_version: "1",
    created_at: "2026-08-25T00:00:00.000Z",
    files: await Promise.all([
      declaration(temp, "inputs/standard-input.v1.json", "application/json"),
      declaration(temp, "materials/question.png", "image/png"),
    ]),
  };
  const manifestPath = path.join(temp, "manifest.v1.json");
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  const replay = await ReplayProvider.fromFile(path.join(fixtureRoot, "provider-replay.v1.json"));
  const result = await runWorker({
    manifestPath,
    inputPath,
    outputDir: path.join(temp, "attempt-unresolved"),
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v1.json"),
    attemptId: "attempt-unresolved",
  }, replay);
  const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8")) as LearningBundle;
  assert.equal(learning.release_state, "unresolved");
  assert.ok(learning.unresolved_items[0]?.reasons.includes("multimodal_answer_provider_required"));
  assert.equal(result.cardBundlePath, undefined);
});

test("describe exposes the stable common worker capability", () => {
  assert.equal(WORKER_DESCRIBE.schemaVersion, "1.0.0");
  assert.equal(WORKER_DESCRIBE.workerId, "content.deepseek");
  assert.equal(WORKER_DESCRIBE.workerVersion, "0.12.0");
  assert.deepEqual(WORKER_DESCRIBE.inputKinds, ["content-input", "content-config-snapshot"]);
  assert.deepEqual(WORKER_DESCRIBE.outputKinds, ["ocr-proofread-bundle", "ocr-human-resolution-bundle", "learning-bundle", "card-bundle", "language-practice-decision", "question.answer"]);
  assert.deepEqual([...WORKER_DESCRIBE.cancellation.supportedReasons].sort(), ["superseded", "task_cancel", "timeout", "user_stop"]);
  assert.doesNotMatch(JSON.stringify(WORKER_DESCRIBE), /remnote|anki/iu);
});

test("settings adapter exposes only real mappings and never credential values", async () => {
  const settings = await describeContentSettings(workerRoot);
  assert.equal(settings.schemaVersion, "content-settings-adapter/1.0.0");
  const serialized = JSON.stringify(settings);
  assert.match(serialized, /AI_PRODUCER_API_KEY/u);
  assert.match(serialized, /content-config-snapshot/u);
  assert.match(serialized, /meaning-reading/u);
  assert.match(serialized, /writing/u);
  assert.match(serialized, /both/u);
  assert.match(serialized, /unsupported/u);
  assert.doesNotMatch(serialized, /\bsk-[A-Za-z0-9_-]{16,}\b/u);
  const schemas = ["content-settings-draft.v1.schema.json", "content-config-snapshot.v1.schema.json"];
  for (const name of schemas) {
    const schema = JSON.parse(await readFile(path.join(workerRoot, "schemas", name), "utf8")) as { $id: string };
    assert.match(schema.$id, /\/1\.0\.0$/u);
  }
});

test("companion descriptor exposes snapshot-bound defaults and explicit Core-conditional interaction without changing worker describe v1", async () => {
  const descriptor = await describeContentCompanion(workerRoot) as { schemaVersion: string; promptProfile: { version: string }; taskSnapshot: { schemaVersion: string }; [key: string]: unknown };
  assert.equal(descriptor.schemaVersion, "content-companion-descriptor/1.1.0");
  assert.equal(descriptor.promptProfile.version, "3.0.0");
  assert.equal(descriptor.taskSnapshot.schemaVersion, "content-config-snapshot/1.1.0");
  const serialized = JSON.stringify(descriptor);
  assert.match(serialized, /supported-snapshot-bound/u);
  assert.match(serialized, /conditional-core-adapter/u);
  assert.match(serialized, /meaning-reading/u);
  assert.match(serialized, /deepseek-reasoner/u);
  assert.match(serialized, /ocr-human-resolution/u);
  assert.match(serialized, /standard-input\/5\.0\.0/u);
  assert.match(serialized, /rerunOcrDefault/u);
  assert.doesNotMatch(serialized, /\bsk-[A-Za-z0-9_-]{16,}\b/u);
  assert.deepEqual(Object.keys(WORKER_DESCRIBE).sort(), ["cancellation", "concurrency", "contractVersion", "idempotencyLevel", "inputKinds", "outputKinds", "schemaVersion", "sideEffectLevel", "stages", "workerId", "workerVersion"].sort());
  for (const name of ["content-companion-descriptor.v1.1.schema.json", "content-companion-settings.v1.schema.json", "content-config-snapshot.v1.1.schema.json"]) {
    const schema = JSON.parse(await readFile(path.join(workerRoot, "schemas", name), "utf8")) as { $id: string };
    assert.match(schema.$id, /\/1\.(?:0|1)\.0$/u);
  }
});

test("default companion profile validates and unknown settings fail closed", async () => {
  const validPath = path.join(workerRoot, "config", "content-companion-settings.default.v1.json");
  const valid = await validateContentCompanionSettings(validPath) as { valid: boolean; runtimeProjection: { languagePracticeDefault: string }; conditional: { workerConsumed: boolean } };
  assert.equal(valid.valid, true);
  assert.equal(valid.runtimeProjection.languagePracticeDefault, "ask");
  assert.equal(valid.conditional.workerConsumed, false);
  const root = await mkdtemp(path.join(tmpdir(), "clipper-content-companion-invalid-"));
  const invalidPath = path.join(root, "invalid.json");
  const profile = JSON.parse(await readFile(validPath, "utf8")) as Record<string, unknown>;
  profile.unknownSetting = true;
  await writeFile(invalidPath, `${stableStringify(profile, 2)}\n`);
  const invalid = await validateContentCompanionSettings(invalidPath) as { valid: boolean; errors: Array<{ message: string }> };
  assert.equal(invalid.valid, false);
  assert.match(invalid.errors[0]?.message ?? "", /additional properties: unknownSetting/u);
});

test("built-in prompts copy to an isolated user draft with validation and SHA diff metadata", async () => {
  const parent = await mkdtemp(path.join(tmpdir(), "clipper-content-settings-"));
  const userRoot = path.join(parent, "user-copy-v1");
  const copied = await copyBuiltInSettings(workerRoot, userRoot) as { validation: { valid: boolean } };
  assert.equal(copied.validation.valid, true);
  const systemPath = path.join(userRoot, "prompts", "shared", "system.md");
  const original = await readFile(systemPath, "utf8");
  await writeFile(systemPath, `${original}\n<!-- 用户副本离线测试 -->\n`);
  const validation = await validateContentSettingsDraft(workerRoot, userRoot) as { valid: boolean; diff: { promptSetChanged: boolean; promptFiles: Array<{ path: string; status: string }> } };
  assert.equal(validation.valid, true);
  assert.equal(validation.diff.promptSetChanged, true);
  assert.equal(validation.diff.promptFiles.find((entry) => entry.path === "shared/system.md")?.status, "modified");
  const diff = await diffContentSettingsDraft(workerRoot, userRoot) as { changes: { promptSetChanged: boolean } };
  assert.equal(diff.changes.promptSetChanged, true);
  assert.equal(await readFile(path.join(workerRoot, "prompts", "shared", "system.md"), "utf8"), original);
});

test("settings validation rejects credentials embedded in an editable Prompt", async () => {
  const parent = await mkdtemp(path.join(tmpdir(), "clipper-content-secret-settings-"));
  const userRoot = path.join(parent, "user-copy-v1");
  await copyBuiltInSettings(workerRoot, userRoot);
  await writeFile(path.join(userRoot, "prompts", "stages", "cleaning.md"), "AI_PRODUCER_API_KEY=sk-this-must-never-be-packaged-123456789\n");
  const validation = await validateContentSettingsDraft(workerRoot, userRoot) as { valid: boolean; errors: Array<{ message: string }> };
  assert.equal(validation.valid, false);
  assert.match(validation.errors.map((entry) => entry.message).join(";"), /PROMPT_SECRET_DETECTED/u);
});

test("offline settings fixture matrix covers ordinary content and all three language card modes without routing or dates", async () => {
  const matrix = JSON.parse(await readFile(path.join(workerRoot, "fixtures", "settings-combinations-v1", "manifest.json"), "utf8")) as { scenarios: Array<{ id: string; settings: unknown }> };
  assert.deepEqual(matrix.scenarios.map((entry) => entry.id), ["ordinary-structured-cleaning", "language-meaning-reading", "language-writing", "language-both"]);
  const serialized = JSON.stringify(matrix);
  assert.match(serialized, /meaning-reading/u);
  assert.match(serialized, /writing/u);
  assert.match(serialized, /both/u);
  assert.doesNotMatch(serialized, /(?:date|routing|destination)/iu);
});

test("authoritative package and launcher identities use KnowledgeClipper", async () => {
  const packageJson = JSON.parse(await readFile(path.join(workerRoot, "package.json"), "utf8")) as { name: string; version: string };
  assert.equal(packageJson.name, "knowledge-clipper-content-worker");
  assert.equal(packageJson.version, "0.12.0");
  const authoritativeLauncher = await readFile(path.join(workerRoot, "launcher", "KnowledgeClipper.Content.Worker.csproj"), "utf8");
  assert.match(authoritativeLauncher, /<AssemblyName>KnowledgeClipper\.Content\.Worker<\/AssemblyName>/u);
  assert.match(authoritativeLauncher, /<RootNamespace>KnowledgeClipper\.Content\.Worker<\/RootNamespace>/u);
  assert.doesNotMatch(authoritativeLauncher, /remnote|anki/iu);
  const launcherProgram = await readFile(path.join(workerRoot, "launcher", "Program.cs"), "utf8");
  assert.match(launcherProgram, /KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT/u);
  assert.match(launcherProgram, /KNOWLEDGE_CLIPPER_NODE_PATH/u);
  const legacyLauncher = await readFile(path.join(workerRoot, "launcher", "RemNoteQuickClipper.Content.Worker.csproj"), "utf8");
  assert.match(legacyLauncher, /<IsLegacyCompatibilityAlias>true<\/IsLegacyCompatibilityAlias>/u);
});

test("authoritative content surfaces remain destination-neutral", async () => {
  const authoritativeFiles = [
    path.join(workerRoot, "package.json"),
    path.join(workerRoot, "launcher", "KnowledgeClipper.Content.Worker.csproj"),
    path.join(workerRoot, "launcher", "Directory.Build.props"),
    path.join(workerRoot, "docs", "DESTINATION-NEUTRAL-ARTIFACT-SEMANTICS.md"),
    ...await listFiles(path.join(workerRoot, "src")),
    ...await listFiles(path.join(workerRoot, "prompts")),
    ...await listFiles(path.join(workerRoot, "config")),
    ...await listFiles(path.join(workerRoot, "schemas")),
  ];
  for (const file of authoritativeFiles) {
    assert.doesNotMatch(await readFile(file, "utf8"), /remnote|anki/iu, file);
  }
});

test("common run adapter publishes append-only result and artifact manifests", async () => {
  const fixture = await stageCommonFixture("offline-qa", "attempt-common-qa");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "card-bundle"]);
  for (const output of result.outputs) {
    const manifestPath = path.join(fixture.jobRoot, ...output.manifestPath.split("/"));
    const digest = await fileDigest(manifestPath);
    assert.equal(digest.sha256, output.expectedSha256);
    const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as CommonArtifactManifest;
    assert.equal(manifest.schemaVersion, "1.0.0");
    assert.equal(manifest.producer.workerId, "content.deepseek");
    assert.deepEqual(manifest.metadata, {
      bundleSchemaVersion: output.kind === "learning-bundle" ? "learning-bundle/1.0.0" : "card-bundle/1.0.0",
      destinationNeutral: true,
      moduleRole: "content-understanding-and-card-generation",
      productDisplayName: PRODUCT_DISPLAY_NAME,
      productId: PRODUCT_ID,
    });
    assert.doesNotMatch(JSON.stringify(manifest), /remnote|anki/iu);
    for (const file of manifest.files) {
      const actual = await fileDigest(path.join(fixture.jobRoot, ...file.path.split("/")));
      assert.equal(actual.sha256, file.sha256);
      assert.equal(actual.size, file.bytes);
    }
  }
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.ok(cards.gates.some((gate) => gate.gate_id === "card-bundle.destination-neutral" && gate.status === "pass"));
  assert.doesNotMatch(JSON.stringify(cards), /remnote|anki/iu);
  const before = await readFile(fixture.resultPath, "utf8");
  await assert.rejects(() => runCommonRequest(fixture.requestPath, workerRoot), /RESULT_EXISTS/u);
  assert.equal(await readFile(fixture.resultPath, "utf8"), before);
});

test("common adapter binds an immutable user Prompt snapshot and records its revision and SHA", async () => {
  const fixture = await stageCommonFixture("offline-qa", "attempt-custom-settings");
  const binding = await stageContentConfigSnapshot(fixture.jobRoot, fixture.requestPath, "settings-revision-offline-v1");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.payload?.contentSettings, {
    settingsRevisionId: "settings-revision-offline-v1",
    promptSetId: "user-open-md-v2-offline",
    promptSetSha256: binding.promptSetSha256,
    categoryRegistryId: "category-registry/2.0.0",
    categoryRegistrySha256: binding.categoryRegistrySha256,
    credentialsRecorded: false,
  });
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  assert.equal(learning.worker.settings_revision_id, "settings-revision-offline-v1");
  assert.equal(learning.worker.prompt_set_id, "user-open-md-v2-offline");
  assert.equal(learning.worker.prompt_set_sha256, binding.promptSetSha256);
  assert.equal(learning.ai_trace[0]?.prompt_files[0]?.sha256, binding.systemPromptSha256);
});

test("snapshot-bound meaning-reading default applies when the language input has no task mode", async () => {
  const fixture = await stageCommonFixture("offline-language-meaning-reading-v2", "attempt-profile-meaning", "personal", "replay", (input) => {
    const items = input.items as Array<{ hints?: Record<string, unknown> }>;
    delete items[0]?.hints?.languagePracticeMode;
  });
  const runtimePolicy = { profileId: "profile-meaning-v1", profileVersion: "1.0.0", languagePracticeDefault: "meaning-reading" as const, provider: { model: "deepseek-chat" as const, maxRetries: 2 } };
  const binding = await stageContentConfigSnapshot(fixture.jobRoot, fixture.requestPath, "settings-profile-meaning-v1", runtimePolicy);
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.equal(result.outputs.some((entry) => entry.kind === "language-practice-decision"), false);
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.deepEqual(cards.cards.map((card) => card.template), ["language_context"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  assert.equal(learning.worker.language_practice_default, "meaning-reading");
  assert.equal(learning.worker.capability_profile_sha256, binding.capabilityProfileSha256);
  assert.equal((result.payload?.contentSettings as { providerModel: string }).providerModel, "deepseek-chat");
});

test("snapshot-bound writing default replaces the unique term and keeps the exact term on the back", async () => {
  const fixture = await stageCommonFixture("offline-language-writing-whitespace-v2", "attempt-profile-writing", "personal", "replay", (input) => { delete input.languagePracticeMode; });
  await stageContentConfigSnapshot(fixture.jobRoot, fixture.requestPath, "settings-profile-writing-v1", { profileId: "profile-writing-v1", profileVersion: "1.0.0", languagePracticeDefault: "writing", provider: { model: "deepseek-chat", maxRetries: 3 } });
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.deepEqual(cards.cards.map((card) => card.template), ["language_writing"]);
  assert.equal(cards.cards[0]?.front_blocks[0]?.text, "First （/tɜːm/；术语） line.\nSecond line keeps two spaces.  ");
  assert.equal(cards.cards[0]?.back_blocks[0]?.text, "term");
});

test("snapshot-bound both default generates meaning-reading before writing for each safe language unit", async () => {
  const fixture = await stageCommonFixture("offline-language-practice-v2", "attempt-profile-both", "personal", "replay", (input) => {
    const overrides = input.human_overrides as Record<string, unknown>;
    delete overrides.languagePracticeMode;
  });
  await stageContentConfigSnapshot(fixture.jobRoot, fixture.requestPath, "settings-profile-both-v1", { profileId: "profile-both-v1", profileVersion: "1.0.0", languagePracticeDefault: "both", provider: { model: "deepseek-reasoner", maxRetries: 1 } });
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.deepEqual(cards.cards.map((card) => [card.category_id, card.template]), [
    ["language_zh", "language_context"], ["language_zh", "language_writing"],
    ["language_en", "language_context"], ["language_en", "language_writing"],
  ]);
  assert.equal(cards.cards[1]?.front_blocks[0]?.text, "他阅读完补充材料后，对这个结论仍然心存（yí dòu；疑问或疑惑）。");
  assert.equal(cards.cards[1]?.back_blocks[0]?.text, "疑窦");
  assert.equal((result.payload?.contentSettings as { providerModel: string }).providerModel, "deepseek-reasoner");
});

test("a language default does not trigger a three-choice decision for non-language content", async () => {
  const fixture = await stageCommonFixture("offline-general-learning", "attempt-profile-non-language");
  await stageContentConfigSnapshot(fixture.jobRoot, fixture.requestPath, "settings-profile-non-language-v1", { profileId: "profile-non-language-v1", profileVersion: "1.0.0", languagePracticeDefault: "both", provider: { model: "deepseek-chat", maxRetries: 3 } });
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.equal(result.outputs.some((entry) => entry.kind === "language-practice-decision"), false);
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.ok(cards.cards.every((card) => card.category_id === "general_learning"));
});

test("snapshot-bound writing still fails closed for non-unique targets and unreliable pronunciation or meaning", async () => {
  for (const [fixtureName, attemptId, expectedReason] of [
    ["offline-language-writing-nonunique-v2", "attempt-profile-nonunique", "language_writing_target_not_unique"],
    ["offline-language-writing-unreliable-v2", "attempt-profile-unreliable", "language_writing_details_required"],
  ] as const) {
    const fixture = await stageCommonFixture(fixtureName, attemptId, "personal", "replay", (input) => {
      const items = input.items as Array<{ hints?: Record<string, unknown> }>;
      delete items[0]?.hints?.languagePracticeMode;
    });
    await stageContentConfigSnapshot(fixture.jobRoot, fixture.requestPath, `settings-${attemptId}`, { profileId: `profile-${attemptId}`, profileVersion: "1.0.0", languagePracticeDefault: "writing", provider: { model: "deepseek-chat", maxRetries: 3 } });
    const result = await runCommonRequest(fixture.requestPath, workerRoot);
    assert.equal(result.status, "completed");
    assert.equal(result.outputs.some((entry) => entry.kind === "card-bundle"), false);
    const decision = await readOutputBundle<LanguagePracticeDecisionBundle>(fixture.jobRoot, result, "language-practice-decision");
    assert.ok(decision.decisions[0]?.reasons.includes(expectedReason));
  }
});

test("common adapter rejects credentials in task config before reading provider or writing a result", async () => {
  const fixture = await stageCommonFixture("offline-qa", "attempt-config-secret");
  const request = JSON.parse(await readFile(fixture.requestPath, "utf8")) as CommonWorkerRequest;
  request.configSnapshot.apiKey = "sk-task-package-secret-1234567890";
  await writeFile(fixture.requestPath, `${stableStringify(request, 2)}\n`);
  await assert.rejects(() => runCommonRequest(fixture.requestPath, workerRoot), /secret field/u);
});

test("common request adapter rejects properties outside the stable schema", async () => {
  const fixture = await stageCommonFixture("offline-qa", "attempt-extra-property");
  const request = JSON.parse(await readFile(fixture.requestPath, "utf8")) as Record<string, unknown>;
  request.unstableField = true;
  await writeFile(fixture.requestPath, `${stableStringify(request, 2)}\n`);
  await assert.rejects(() => runCommonRequest(fixture.requestPath, workerRoot), /additional properties/u);
});

test("meaning-reading preserves the existing English language_context card semantics", async () => {
  const fixture = await stageCommonFixture("offline-language-meaning-reading-v2", "attempt-language-en-meaning");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(cards.cards.length, 1);
  assert.equal(cards.cards[0]?.category_id, "language_en");
  assert.equal(cards.cards[0]?.template, "language_context");
  assert.equal(cards.cards[0]?.front_blocks[0]?.text, "The committee reached a **tentative** conclusion after reviewing the evidence.");
  assert.equal(cards.cards[0]?.answer_provenance, "not_applicable");
  assert.match(cards.cards[0]?.back_blocks[0]?.text ?? "", /tentative/u);
});

test("exact selected target uses ordered sentence context and both emits context then writing", async () => {
  const fixture = await stageCommonFixture("offline-language-selection-v6", "attempt-language-selection-v6");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed", JSON.stringify(result));
  assert.equal(result.payload?.moduleReleaseState, "ready");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.items[0]?.units[0]?.target_term, "assumption");
  assert.deepEqual(cards.cards.map((card) => card.template), ["language_context", "language_writing"]);
  assert.equal(cards.cards[0]?.front_blocks[0]?.text, "The conclusion depends on one assumption.");
  assert.equal(cards.cards[1]?.front_blocks[0]?.text, "The conclusion depends on one （/əˈsʌmpʃən/；假设）.");
  assert.equal(cards.cards[1]?.back_blocks[0]?.text, "assumption");
  assert.doesNotMatch(JSON.stringify(cards), /model-must-not-rewrite-target/u);
  assert.equal(WORKER_DESCRIBE.outputKinds.some((kind) => /speech|spoken|audio/u.test(kind)), false);
  assert.equal(learning.items[0]?.units[0]?.target_term, "assumption");
});

test("repeated selected target emits a SHA-bound resolution artifact instead of success without cards", async () => {
  const fixture = await stageCommonFixture("offline-language-selection-repeated-v6", "attempt-language-selection-repeated-v6");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.equal(result.payload?.moduleReleaseState, "unresolved");
  assert.equal(result.outputs.some((entry) => entry.kind === "card-bundle"), false);
  const ref = result.outputs.find((entry) => entry.kind === "language-practice-decision")!;
  assert.equal(ref.contractVersion, "2.0.0");
  const decision = await readOutputBundle<LanguagePracticeDecisionBundle>(fixture.jobRoot, result, "language-practice-decision");
  assert.equal(decision.schema_version, "language-practice-decision/2.0.0");
  assert.equal(decision.decisions[0]?.candidate_positions?.length, 2);
  assert.match(decision.decisions[0]?.target_token ?? "", /^[a-f0-9]{64}$/u);
  assert.deepEqual(decision.decisions[0]?.suggested_user_fields, ["action", "selected_text", "selected_text_sha256", "target_token", "candidateOccurrence", "confirmed"]);
});

test("occurrence-bound accept selects exactly one repeated target and emits two cards", async () => {
  const fixture = await stageCommonFixture("offline-language-occurrence-v7", "attempt-language-occurrence-v7");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed", JSON.stringify(result));
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.deepEqual(cards.cards.map((card) => card.template), ["language_context", "language_writing"]);
  assert.equal(cards.cards[1]?.front_blocks[0]?.text, "An （/əˈsʌmpʃən/；假设） can differ from another assumption.");
  assert.equal(cards.cards[1]?.back_blocks[0]?.text, "assumption");
});

test("occurrence-bound input rejects missing, drifting, cross-segment and stale coordinates with zero outputs", async () => {
  const cases = [
    ["missing", (resolution: Record<string, unknown>) => { delete resolution.candidateOccurrence; }],
    ["coordinate", (resolution: Record<string, unknown>) => { (resolution.candidateOccurrence as Record<string, unknown>).start = 4; }],
    ["source-sha", (resolution: Record<string, unknown>) => { (resolution.candidateOccurrence as Record<string, unknown>).sourceSha256 = "1".repeat(64); }],
    ["cross-segment", (resolution: Record<string, unknown>) => { (resolution.candidateOccurrence as Record<string, unknown>).segmentId = "another-segment"; }],
    ["stale-token", (resolution: Record<string, unknown>) => { resolution.target_token = "2".repeat(64); }],
  ] as const;
  for (const [name, mutate] of cases) {
    const fixture = await stageCommonFixture("offline-language-occurrence-v7", `attempt-occurrence-${name}`, "personal", "replay", (input) => {
      const item = (input.items as Array<Record<string, unknown>>)[0]!;
      const resolution = ((item.human_overrides as Record<string, unknown>).languageTargetResolution as Record<string, unknown>);
      mutate(resolution);
    });
    const result = await runCommonRequest(fixture.requestPath, workerRoot);
    assert.equal(result.status, "failed", name);
    assert.equal(result.outputs.length, 0, name);
    assert.match(result.error?.message ?? "", /INPUT_(?:INVALID|STALE)/u, name);
  }
});

test("self-contained selection needs no context and preserves 1/1/2 mode cardinality", async () => {
  for (const [mode, count, templates] of [
    ["meaning-reading", 1, ["language_context"]],
    ["writing", 1, ["language_writing"]],
    ["both", 2, ["language_context", "language_writing"]],
  ] as const) {
    const fixture = await stageCommonFixture("offline-language-selection-self-contained-v6", `attempt-self-contained-${mode}`, "personal", "replay", (input) => {
      (input.human_overrides as Record<string, unknown>).languagePracticeMode = mode;
    });
    const result = await runCommonRequest(fixture.requestPath, workerRoot);
    assert.equal(result.status, "completed");
    const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
    assert.equal(cards.cards.length, count);
    assert.deepEqual(cards.cards.map((card) => card.template), templates);
  }
});

test("human target correction creates a new ready revision and a stale target token fails closed", async () => {
  const unresolved = await stageCommonFixture("offline-language-selection-correction-v6", "attempt-target-before-correction");
  const unresolvedResult = await runCommonRequest(unresolved.requestPath, workerRoot);
  assert.equal(unresolvedResult.payload?.moduleReleaseState, "unresolved");
  const decision = await readOutputBundle<LanguagePracticeDecisionBundle>(unresolved.jobRoot, unresolvedResult, "language-practice-decision");
  const targetToken = decision.decisions[0]!.target_token!;

  const corrected = await stageCommonFixture("offline-language-selection-correction-v6", "attempt-target-after-correction", "personal", "replay", (input) => {
    const item = (input.items as Array<Record<string, unknown>>)[0]!;
    const overrides = item.human_overrides = {};
    (overrides as Record<string, unknown>).languageTargetResolution = {
      schema_version: "language-target-resolution/1.0.0", decision_id: decision.decisions[0]!.decision_id,
      target_token: targetToken, action: "user_correction", selected_text: "assumption",
      selected_text_sha256: sha256("assumption"), confirmed: true,
    };
  });
  const correctedResult = await runCommonRequest(corrected.requestPath, workerRoot);
  const cards = await readOutputBundle<CardBundle>(corrected.jobRoot, correctedResult, "card-bundle");
  assert.equal(correctedResult.payload?.moduleReleaseState, "ready");
  assert.deepEqual(cards.cards.map((card) => card.template), ["language_context", "language_writing"]);

  const stale = await stageCommonFixture("offline-language-selection-correction-v6", "attempt-target-stale", "personal", "replay", (input) => {
    const item = (input.items as Array<Record<string, unknown>>)[0]!;
    item.human_overrides = { languageTargetResolution: { schema_version: "language-target-resolution/1.0.0", decision_id: "decision-stale", target_token: "0".repeat(64), action: "user_correction", selected_text: "assumption", selected_text_sha256: sha256("assumption"), confirmed: true } };
  });
  const staleResult = await runCommonRequest(stale.requestPath, workerRoot);
  assert.equal(staleResult.status, "failed");
  assert.match(staleResult.error?.message ?? "", /INPUT_STALE languageTargetResolution target_token/u);
});

test("missing operation languagePracticeMode publishes a versioned decision artifact and no CardBundle", async () => {
  const fixture = await stageCommonFixture("offline-language-decision-v2", "attempt-language-decision");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "language-practice-decision"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const decision = await readOutputBundle<LanguagePracticeDecisionBundle>(fixture.jobRoot, result, "language-practice-decision");
  assert.equal(learning.release_state, "unresolved");
  assert.ok(learning.items[0]?.units[0]?.unresolved_reasons.includes("language_practice_mode_required"));
  assert.equal(decision.schema_version, "language-practice-decision/1.0.0");
  assert.equal(decision.state, "requires_human");
  assert.deepEqual(decision.decisions[0]?.allowed_modes, ["meaning-reading", "writing", "both"]);
  assert.deepEqual(decision.decisions[0]?.requested_override_paths, ["human_overrides.languagePracticeMode"]);
  assert.equal(result.outputs.some((entry) => entry.kind === "card-bundle"), false);
});

test("one operation-level human override applies both mode to Chinese and English units with fixed card order", async () => {
  const fixture = await stageCommonFixture("offline-language-practice-v2", "attempt-language-practice");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "card-bundle"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.release_state, "ready");
  assert.equal(cards.cards.length, 4);
  assert.deepEqual(cards.cards.map((card) => [card.category_id, card.template]), [
    ["language_zh", "language_context"],
    ["language_zh", "language_writing"],
    ["language_en", "language_context"],
    ["language_en", "language_writing"],
  ]);
  const zhWriting = cards.cards[1]!;
  assert.equal(zhWriting.front_blocks[0]?.text, "他阅读完补充材料后，对这个结论仍然心存（yí dòu；疑问或疑惑）。");
  assert.equal(zhWriting.front_blocks[0]?.provenance, "human_confirmed");
  assert.equal(zhWriting.back_blocks[0]?.text, "疑窦");
  const enWriting = cards.cards[3]!;
  assert.equal(enWriting.front_blocks[0]?.text, "The committee reached a （/ˈtentətɪv/；暂定的） conclusion after reviewing the evidence.");
  assert.equal(enWriting.front_blocks[0]?.provenance, "generated");
  assert.equal(enWriting.back_blocks[0]?.text, "tentative");
  assert.ok(learning.items.every((item) => item.units[0]?.gates.some((gate) => gate.gate_id === "language-practice.both-order" && gate.status === "pass")));
});

test("mixed-language partial result keeps the unsafe unit unresolved while releasing the safe writing card", async () => {
  const fixture = await stageCommonFixture("offline-language-mixed-partial-v2", "attempt-language-mixed-partial");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "language-practice-decision", "card-bundle"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const decision = await readOutputBundle<LanguagePracticeDecisionBundle>(fixture.jobRoot, result, "language-practice-decision");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.release_state, "partial");
  assert.equal(learning.items.find((item) => item.item_id === "item-language-zh")?.state, "ready");
  assert.equal(learning.items.find((item) => item.item_id === "item-language-en")?.state, "unresolved");
  assert.equal(cards.release_state, "partial");
  assert.equal(cards.cards.length, 1);
  assert.equal(cards.cards[0]?.category_id, "language_zh");
  assert.deepEqual(cards.withheld_units, [{ item_id: "item-language-en", unit_id: "unit-draft", reasons: ["language_writing_details_required"] }]);
  assert.equal(decision.decisions.length, 1);
  assert.equal(decision.decisions[0]?.unit_id, "unit-draft");
  assert.equal(decision.decisions[0]?.meaning_state, "unreliable");
  assert.equal(decision.decisions[0]?.pronunciation_state, undefined);
});

test("non-unique target term is decided locally without a writing provider call", async () => {
  const fixture = await stageCommonFixture("offline-language-writing-nonunique-v2", "attempt-language-nonunique");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "language-practice-decision"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const decision = await readOutputBundle<LanguagePracticeDecisionBundle>(fixture.jobRoot, result, "language-practice-decision");
  assert.equal(learning.ai_trace.length, 2);
  assert.equal(decision.decisions[0]?.target_occurrence_count, 2);
  assert.deepEqual(decision.decisions[0]?.reasons, ["language_writing_target_not_unique"]);
});

test("unreliable writing meaning produces a decision instead of a guessed card", async () => {
  const fixture = await stageCommonFixture("offline-language-writing-unreliable-v2", "attempt-language-unreliable");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "language-practice-decision"]);
  const decision = await readOutputBundle<LanguagePracticeDecisionBundle>(fixture.jobRoot, result, "language-practice-decision");
  assert.equal(decision.decisions[0]?.meaning_state, "unreliable");
  assert.equal(result.outputs.some((entry) => entry.kind === "card-bundle"), false);
});

test("language writing removes only outer terminal CR/LF while preserving internal newline and trailing spaces", async () => {
  const fixture = await stageCommonFixture("offline-language-writing-whitespace-v2", "attempt-language-whitespace");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.items[0]?.source_segments[0]?.text, "First term line.\nSecond line keeps two spaces.  \n");
  assert.equal(cards.cards[0]?.front_blocks[0]?.text, "First （/tɜːm/；术语） line.\nSecond line keeps two spaces.  ");
  assert.equal(cards.cards[0]?.back_blocks[0]?.text, "term");
});

test("question.answer initial turn publishes immutable answer and LearningBundle without CardBundle", async () => {
  const fixture = await stageCommonFixture("offline-question-answer-v3", "attempt-question-answer");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "question.answer"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const answer = await readOutputBundle<QuestionAnswerBundle>(fixture.jobRoot, result, "question.answer");
  assert.equal(answer.schema_version, "question-answer/1.0.0");
  assert.equal(answer.turns.length, 1);
  assert.equal(answer.turns[0]?.turn_index, 0);
  assert.equal(answer.turns[0]?.question, "为什么 TCP 建立连接需要三次握手？");
  assert.equal(answer.turns[0]?.answer_text, "三次握手让双方确认彼此的发送与接收能力，并同步初始序列号。");
  assert.equal(answer.turns[0]?.artifact_ref, answer.answer_bundle_id);
  assert.equal(answer.turns[0]?.answer_text_sha256, sha256(answer.turns[0]!.answer_text));
  assert.equal(answer.turns[0]?.question_sha256, sha256(answer.turns[0]!.question));
  assert.equal(learning.items[0]?.source_segments.length, 1);
  assert.equal(learning.items[0]?.units[0]?.generated_content[0]?.kind, "answer");
  assert.equal(result.outputs.some((entry) => entry.kind === "card-bundle"), false);
});

test("question.answer follow-up preserves ancestry prefix and appends exactly one new answer", async () => {
  const fixture = await stageCommonFixture("offline-question-followup-v3", "attempt-question-followup");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "question.answer"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const answer = await readOutputBundle<QuestionAnswerBundle>(fixture.jobRoot, result, "question.answer");
  assert.equal(answer.turns.length, 2);
  assert.deepEqual(answer.turns.map((turn) => turn.turn_index), [0, 1]);
  assert.equal(answer.turns[0]?.answer_id, "answer-tcp-1");
  assert.equal(answer.turns[0]?.artifact_ref, "question-answer-initial-tcp");
  assert.equal(answer.turns[0]?.provenance, "persisted");
  assert.equal(answer.turns[1]?.parent_answer_id, "answer-tcp-1");
  assert.equal(answer.turns[1]?.provenance, "generated");
  assert.equal(answer.turns[1]?.answer_text, "服务端无法确认客户端是否收到了自己的确认，历史失效连接请求也可能造成半开连接和资源浪费。");
  assert.equal(answer.new_answer_id, answer.turns[1]?.answer_id);
  assert.deepEqual(learning.items[0]?.source_segments.map((segment) => segment.role), ["user", "assistant", "user"]);
  assert.deepEqual(learning.ai_trace.map((trace) => trace.stage), ["question_answer"]);
});

test("question.make-cards consumes the complete persisted chain and optional cardStructureRef", async () => {
  const fixture = await stageCommonFixture("offline-question-make-cards-v3", "attempt-question-cards");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "card-bundle"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.deepEqual(learning.items[0]?.source_segments.map((segment) => segment.role), ["user", "assistant", "user", "assistant"]);
  assert.ok(learning.items[0]?.source_refs.some((ref) => ref.path.endsWith("materials/card-structure.v1.json")));
  assert.equal(cards.cards.length, 1);
  assert.equal(cards.cards[0]?.front_blocks[0]?.text, "如果只有两次握手，会留下什么风险？");
  assert.equal(cards.cards[0]?.back_blocks[0]?.text, "服务端无法确认客户端是否收到了自己的确认，历史失效连接请求也可能造成半开连接和资源浪费。");
  assert.equal(cards.cards[0]?.answer_provenance, "source");
  assert.equal(cards.cards[0]?.source_segment_ids.length, 4);
  assert.equal(result.outputs.some((entry) => entry.kind === "question.answer"), false);
});

test("question workflow rejects empty questions, missing ancestry, and out-of-order turns before provider use", async () => {
  const cases = [
    ["offline-question-empty-v3", "attempt-question-empty", /INPUT_INVALID questionTurns\[0\]\.question/u],
    ["offline-question-ancestry-missing-v3", "attempt-question-ancestry", /answerAncestry missing or extra/u],
    ["offline-question-turn-order-invalid-v3", "attempt-question-order", /questionTurns order/u],
  ] as const;
  for (const [fixtureName, attemptId, expected] of cases) {
    const fixture = await stageCommonFixture(fixtureName, attemptId);
    const result = await runCommonRequest(fixture.requestPath, workerRoot);
    assert.equal(result.status, "failed");
    assert.equal(result.outputs.length, 0);
    assert.equal(result.error?.code, "content.input-invalid");
    assert.match(result.error?.message ?? "", expected);
  }
});

test("question.answer with UI or copyright noise remains unresolved and publishes no answer artifact", async () => {
  const fixture = await stageCommonFixture("offline-question-answer-noise-v3", "attempt-question-noise");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  assert.equal(learning.release_state, "unresolved");
  assert.deepEqual(learning.items[0]?.units[0]?.unresolved_reasons, ["question_answer_noise_detected"]);
});

test("question.answer unsafe provider decision remains unresolved and fail-closed", async () => {
  const fixture = await stageCommonFixture("offline-question-answer-unresolved-v3", "attempt-question-unresolved");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  assert.equal(learning.release_state, "unresolved");
  assert.deepEqual(learning.items[0]?.units[0]?.unresolved_reasons, ["missing_required_context"]);
});

test("ordinary question without an answer produces a generated-answer card with provenance", async () => {
  const fixture = await stageCommonFixture("offline-no-answer", "attempt-no-answer");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.items[0]?.units[0]?.answer_state, "missing");
  assert.equal(learning.items[0]?.units[0]?.generated_content[0]?.kind, "answer");
  assert.equal(cards.cards[0]?.answer_provenance, "generated");
});

test("multi-turn follow-up synthesis retains every turn segment", async () => {
  const fixture = await stageCommonFixture("offline-multiturn", "attempt-multiturn");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.items[0]?.source_segments.length, 3);
  assert.equal(learning.items[0]?.units[0]?.source_segment_ids.length, 3);
  assert.equal(cards.cards[0]?.source_segment_ids.length, 3);
  assert.equal(learning.items[0]?.units[0]?.generated_content[0]?.kind, "multiturn_answer");
});

test("general learning concept-transfer and procedure cards pass through the common replay adapter", async () => {
  const fixture = await stageCommonFixture("offline-general-learning", "attempt-general-learning");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.release_state, "ready");
  assert.equal(learning.items.length, 2);
  assert.deepEqual(new Set(learning.items.flatMap((item) => item.units.map((unit) => unit.category_id))), new Set(["general_learning"]));
  assert.deepEqual(new Set(cards.cards.map((card) => card.template)), new Set(["concept_transfer", "procedure"]));
  assert.equal(cards.cards.length, 2);
  for (const card of cards.cards) {
    assert.equal(card.front_blocks[0]?.provenance, "generated");
    assert.equal(card.back_blocks[0]?.provenance, "source");
    assert.equal(card.answer_provenance, "source");
  }
});

test("Phase B ordinary and short Chinese statements register every generated block exactly", async () => {
  const promptContract = await readFile(path.join(workerRoot, "prompts", "shared", "card-output-contract.md"), "utf8");
  const categoryPrompt = await readFile(path.join(workerRoot, "prompts", "categories", "general-learning.md"), "utf8");
  assert.match(promptContract, /最终一致性检查/u);
  assert.match(categoryPrompt, /kind=question_wording/u);
  assert.doesNotMatch(categoryPrompt, /返回 `\{ "generated_content": \[\], "cards": \[\]/u);

  const fixture = await stageCommonFixture("offline-phase-b-generated-content-v1", "attempt-phase-b-generated-content");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle", "card-bundle"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  assert.equal(learning.release_state, "ready");
  assert.deepEqual(learning.items.map((item) => item.item_id), ["item-phase-b-a", "item-phase-b-d"]);
  assert.equal(cards.cards.length, 2);
  for (const item of learning.items) {
    const unit = item.units[0];
    assert.ok(unit);
    const generatedTexts = new Set(unit.generated_content.map((entry) => entry.text));
    const itemCards = cards.cards.filter((card) => card.item_id === item.item_id);
    const usedGeneratedTexts = new Set(itemCards.flatMap((card) => [...card.front_blocks, ...card.back_blocks]
      .filter((block) => block.type !== "asset_ref" && block.provenance === "generated")
      .map((block) => block.text)));
    assert.deepEqual(generatedTexts, usedGeneratedTexts);
    assert.equal(generatedTexts.size, 1);
  }
});

test("structured cleaning removes copy and copyright noise without damaging Markdown, code, or math", async () => {
  const fixture = await stageCommonFixture("offline-structured-cleaning", "attempt-structured-cleaning");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  const item = learning.items[0];
  assert.equal(learning.release_state, "ready");
  assert.equal(item?.cleaning.removals.length, 2);
  assert.deepEqual(item?.cleaning.removals.map((entry) => entry.reason), ["sharing_prompt", "site_footer"]);
  assert.match(item?.cleaning.cleaned_text ?? "", /1\. 去括号\n2\. 移项并合并同类项\n3\. 把未知数的系数化为 1/u);
  assert.match(item?.cleaning.cleaned_text ?? "", /```text\nx \+ 2 = 5\nx = 3\n```/u);
  assert.match(item?.cleaning.cleaned_text ?? "", /\$\$x \+ 2 = 5\$\$/u);
  assert.doesNotMatch(item?.cleaning.cleaned_text ?? "", /复制|分享|Copyright/iu);
  assert.ok(item?.gates.some((gate) => gate.gate_id === "cleaning.structure-fidelity" && gate.status === "pass"));
  assert.equal(cards.cards.length, 1);
  assert.equal(cards.cards[0]?.template, "procedure");
  assert.equal(cards.cards[0]?.back_blocks.length, 4);
  assert.ok(cards.cards[0]?.back_blocks.every((block) => block.provenance === "source"));
});

test("duplicate cleaning preserves one complete copy through the common replay adapter", async () => {
  const fixture = await stageCommonFixture("offline-duplicate-cleaning", "attempt-duplicate-cleaning");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  const item = learning.items[0];
  assert.equal(learning.release_state, "ready");
  assert.equal(item?.cleaning.removals.length, 1);
  assert.equal(item?.cleaning.removals[0]?.reason, "duplicate");
  assert.equal(item?.cleaning.cleaned_text, "课程页眉\n\n核心结论：重复清洗必须至少保留一个完整副本。\n");
  assert.equal(item?.source_segments.filter((segment) => segment.text === "课程页眉").length, 2);
  assert.equal(cards.cards.length, 1);
  assert.equal(cards.cards[0]?.back_blocks[0]?.text, "核心结论：重复清洗必须至少保留一个完整副本。");
});

test("malformed table and formula stay unchanged and require human review before card generation", async () => {
  const fixture = await stageCommonFixture("offline-format-anomaly", "attempt-format-anomaly");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle"]);
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const item = learning.items[0];
  assert.equal(learning.release_state, "unresolved");
  assert.equal(item?.cleaning.state, "unresolved");
  const originalStructuredText = item?.source_segments.map((segment) => segment.text.replace(/\r\n?/gu, "\n").replace(/\n+$/u, "")).join("\n\n");
  assert.equal(item?.cleaning.cleaned_text?.replace(/\r\n?/gu, "\n").replace(/\n+$/u, ""), originalStructuredText);
  assert.deepEqual(item?.cleaning.format_anomalies.map((entry) => [entry.kind, entry.severity]), [
    ["broken_table", "requires_human"],
    ["broken_formula", "requires_human"],
  ]);
  assert.deepEqual(item?.unresolved_reasons, ["format_anomaly_requires_human"]);
  assert.equal(item?.units[0]?.state, "unresolved");
  assert.equal(learning.ai_trace.length, 2);
});

test("a format anomaly only withholds units that reference the affected segment", async () => {
  const fixture = await stageCommonFixture("offline-partial-format-anomaly", "attempt-partial-format-anomaly");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const cards = await readOutputBundle<CardBundle>(fixture.jobRoot, result, "card-bundle");
  const cleanUnit = learning.items[0]?.units.find((unit) => unit.unit_id === "unit-clean-fact");
  const brokenUnit = learning.items[0]?.units.find((unit) => unit.unit_id === "unit-broken-formula");
  assert.equal(learning.release_state, "partial");
  assert.equal(learning.items[0]?.state, "partial");
  assert.equal(learning.items[0]?.cleaning.state, "unresolved");
  assert.equal(cleanUnit?.state, "ready");
  assert.equal(brokenUnit?.state, "unresolved");
  assert.deepEqual(brokenUnit?.unresolved_reasons, ["format_anomaly_requires_human"]);
  assert.equal(cards.release_state, "partial");
  assert.equal(cards.cards.length, 1);
  assert.equal(cards.cards[0]?.unit_id, "unit-clean-fact");
  assert.deepEqual(cards.withheld_units, [{ item_id: "item-partial-format", unit_id: "unit-broken-formula", reasons: ["format_anomaly_requires_human"] }]);
  assert.equal(learning.ai_trace.length, 3);
});

test("high-stakes missing answer remains needs-research and publishes no card bundle", async () => {
  const fixture = await stageCommonFixture("offline-needs-research", "attempt-needs-research");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "completed");
  assert.deepEqual(result.outputs.map((entry) => entry.kind), ["learning-bundle"]);
  assert.ok(result.warnings.includes("module release_state=unresolved; unresolved is preserved for review"));
  const learning = await readOutputBundle<LearningBundle>(fixture.jobRoot, result, "learning-bundle");
  const unit = learning.items[0]?.units[0];
  assert.equal(learning.release_state, "unresolved");
  assert.equal(unit?.state, "unresolved");
  assert.equal(unit?.risk_state, "high_stakes");
  assert.equal(unit?.answer_state, "missing");
  assert.ok(unit?.unresolved_reasons.includes("needs_research"));
  assert.equal(unit?.generated_content.length, 0);
  assert.equal(learning.ai_trace.length, 2);
});

test("human review reruns the same source through validated overrides before card release", async () => {
  const pendingFixture = await stageCommonFixture("offline-human-review-pending", "attempt-human-pending");
  const pendingResult = await runCommonRequest(pendingFixture.requestPath, workerRoot);
  assert.equal(pendingResult.status, "completed");
  assert.deepEqual(pendingResult.outputs.map((entry) => entry.kind), ["learning-bundle"]);
  const pendingLearning = await readOutputBundle<LearningBundle>(pendingFixture.jobRoot, pendingResult, "learning-bundle");
  const pendingUnit = pendingLearning.items[0]?.units[0];
  assert.equal(pendingLearning.release_state, "unresolved");
  assert.equal(pendingUnit?.answer_state, "ambiguous");
  assert.ok(pendingUnit?.unresolved_reasons.includes("answer_scope_requires_human"));
  assert.ok(pendingUnit?.unresolved_reasons.includes("answer_state_ambiguous"));
  assert.equal(pendingLearning.ai_trace.length, 2);

  const confirmedFixture = await stageCommonFixture("offline-human-review-confirmed", "attempt-human-confirmed");
  const confirmedResult = await runCommonRequest(confirmedFixture.requestPath, workerRoot);
  assert.equal(confirmedResult.status, "completed");
  assert.deepEqual(confirmedResult.outputs.map((entry) => entry.kind), ["learning-bundle", "card-bundle"]);
  const confirmedLearning = await readOutputBundle<LearningBundle>(confirmedFixture.jobRoot, confirmedResult, "learning-bundle");
  const confirmedCards = await readOutputBundle<CardBundle>(confirmedFixture.jobRoot, confirmedResult, "card-bundle");
  const confirmedUnit = confirmedLearning.items[0]?.units[0];
  assert.equal(confirmedLearning.release_state, "ready");
  assert.equal(confirmedUnit?.answer_state, "provided");
  assert.equal(confirmedUnit?.supplied_answer, "提交后原则上不可修改；管理员确认录入错误时可以退回重填。");
  assert.deepEqual(confirmedUnit?.unresolved_reasons, []);
  assert.ok(confirmedLearning.items[0]?.gates.some((gate) => gate.gate_id === "classification.human-overrides" && gate.status === "pass"));
  assert.equal(confirmedCards.cards[0]?.answer_provenance, "source");
  assert.equal(confirmedCards.cards[0]?.back_blocks[0]?.provenance, "source");
});

test("pre-existing common cancellation signal publishes cancelled with no outputs", async () => {
  const fixture = await stageCommonFixture("offline-qa", "attempt-cancelled");
  const requestedAt = new Date();
  await writeFile(fixture.cancelPath, `${JSON.stringify({
    schemaVersion: "1.0.0",
    attemptId: "attempt-cancelled",
    operationId: "operation-attempt-cancelled",
    reason: "user_stop",
    requestedAt: requestedAt.toISOString(),
    graceDeadline: new Date(requestedAt.getTime() + 2000).toISOString(),
  }, null, 2)}\n`);
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "cancelled");
  assert.equal(result.outputs.length, 0);
  assert.equal(result.error?.code, "cancel.user_stop");
});

test("secret-prohibited input fails closed before live provider creation", async () => {
  const fixture = await stageCommonFixture("offline-qa", "attempt-secret", "secret-prohibited", "live");
  const result = await runCommonRequest(fixture.requestPath, workerRoot);
  assert.equal(result.status, "failed");
  assert.equal(result.outputs.length, 0);
  assert.equal(result.error?.code, "content.input-invalid");
  assert.match(result.error?.message ?? "", /secret-prohibited/u);
});

async function stageOcrHumanResolution(
  action: "accept_original" | "user_correction",
  mutate?: (input: StandardInput) => void,
): Promise<{ jobRoot: string; paths: Parameters<typeof runWorker>[0] }> {
  const sourceRoot = path.join(workerRoot, "fixtures", "offline-ocr-ambiguous");
  const jobRoot = await mkdtemp(path.join(tmpdir(), "clipper-ocr-human-resolution-"));
  const frozenOutput = path.join(jobRoot, "frozen-attempt", "outputs");
  const frozenReplay = await ReplayProvider.fromFile(path.join(sourceRoot, "provider-replay.v1.json"));
  const frozenResult = await runWorker({
    manifestPath: path.join(sourceRoot, "artifact.json"),
    inputPath: path.join(sourceRoot, "inputs", "standard-input.v4.json"),
    outputDir: frozenOutput,
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v2.json"),
    attemptId: "attempt-frozen-ocr-0.8.0",
    jobRoot: sourceRoot,
    operation: "understand-and-card",
  }, frozenReplay);
  const frozenBundle = JSON.parse(await readFile(frozenResult.ocrProofreadBundlePath!, "utf8")) as OcrProofreadBundle;
  const frozenItem = frozenBundle.items[0]!;

  for (const relative of [
    "upstream/markdown/content.md",
    "upstream/markdown/artifact.json",
    "upstream/structured/content.json",
    "upstream/structured/artifact.json",
  ]) {
    const target = path.join(jobRoot, ...relative.split("/"));
    await mkdir(path.dirname(target), { recursive: true });
    await copyFile(path.join(sourceRoot, ...relative.split("/")), target);
  }

  const frozenPayloadRelative = "ancestry/ocr-proofread-0.8.0/ocr-proofread-bundle.schema-v1.json";
  const frozenManifestRelative = "ancestry/ocr-proofread-0.8.0/artifact.json";
  const frozenPayloadPath = path.join(jobRoot, ...frozenPayloadRelative.split("/"));
  await mkdir(path.dirname(frozenPayloadPath), { recursive: true });
  await copyFile(frozenResult.ocrProofreadBundlePath!, frozenPayloadPath);
  const frozenPayloadDigest = await fileDigest(frozenPayloadPath);
  const frozenArtifact: CommonArtifactManifest = {
    schemaVersion: "1.0.0",
    artifactId: "ocr-proofread-frozen-ambiguous-v1",
    kind: "ocr-proofread-bundle",
    contractVersion: "1.0.0",
    mediaType: "application/json",
    files: [{ path: frozenPayloadRelative, sha256: frozenPayloadDigest.sha256, bytes: frozenPayloadDigest.size, mediaType: "application/json" }],
    createdAt: "2026-08-30T00:00:00.000Z",
    producer: { stageId: "content.understand-and-card", workerId: "content.deepseek", workerVersion: "0.8.0", attemptId: "attempt-frozen-ocr-0.8.0" },
    inputRefs: [],
    dataClass: "public",
    metadata: { bundleSchemaVersion: "ocr-proofread-bundle/1.0.0", frozen: true },
  };
  const frozenManifestPath = path.join(jobRoot, ...frozenManifestRelative.split("/"));
  await writeFile(frozenManifestPath, `${stableStringify(frozenArtifact, 2)}\n`);
  const frozenManifestDigest = await fileDigest(frozenManifestPath);

  const baseInput = JSON.parse(await readFile(path.join(sourceRoot, "inputs", "standard-input.v4.json"), "utf8")) as StandardInput;
  baseInput.schema_version = "standard-input/5.0.0";
  baseInput.input_version = action === "accept_original" ? "2-accept-original" : "2-user-correction";
  const item = baseInput.items[0]!;
  const sourceProofreadArtifact = {
    artifactRef: frozenArtifact.artifactId,
    kind: "ocr-proofread-bundle" as const,
    contractVersion: "1.0.0" as const,
    manifestPath: frozenManifestRelative,
    expectedSha256: frozenManifestDigest.sha256,
    payload_path: frozenPayloadRelative,
    payload_sha256: frozenPayloadDigest.sha256,
  };
  const correctedText = frozenItem.proofread_text.replace("MODL", "MODEL");
  item.ocr_human_resolution = {
    schema_version: "ocr-human-resolution-input/1.0.0",
    resolution_id: action === "accept_original" ? "resolution-accept-original-v1" : "resolution-user-correction-v1",
    action,
    source_proofread_artifact: sourceProofreadArtifact,
    source_item_id: item.item_id,
    original_text_sha256: frozenItem.original_text_sha256,
    proofread_text_sha256: frozenItem.proofread_text_sha256,
    unresolved_candidates: JSON.parse(JSON.stringify(frozenItem.unresolved)),
    reason: action === "accept_original" ? "User confirms the retained OCR literal is intentional." : "User explicitly supplies the intended token.",
    ...(action === "user_correction" ? { correction: {
      corrected_text: correctedText,
      corrected_text_sha256: sha256(correctedText),
      diffs: [{ segment_index: 1, operation: "insertion" as const, start: 11, end: 11, before: "", after: "E" }],
    } } : {}),
  };
  mutate?.(baseInput);
  const inputRelative = "inputs/standard-input.v5.json";
  const replayRelative = "inputs/provider-replay.v1.json";
  const inputPath = path.join(jobRoot, ...inputRelative.split("/"));
  await mkdir(path.dirname(inputPath), { recursive: true });
  await writeFile(inputPath, `${stableStringify(baseInput, 2)}\n`);
  await copyFile(path.join(workerRoot, "fixtures", "ocr-human-resolution-v1", action === "accept_original" ? "accept-original.provider-replay.v1.json" : "user-correction.provider-replay.v1.json"), path.join(jobRoot, ...replayRelative.split("/")));
  const declared = await Promise.all([
    inputRelative,
    replayRelative,
    "upstream/markdown/content.md",
    "upstream/structured/content.json",
  ].map(async (relative) => {
    const digest = await fileDigest(path.join(jobRoot, ...relative.split("/")));
    return { path: relative, sha256: digest.sha256, bytes: digest.size, mediaType: relative.endsWith(".md") ? "text/markdown; charset=utf-8" : "application/json" };
  }));
  const sourceArtifact = JSON.parse(await readFile(path.join(sourceRoot, "artifact.json"), "utf8")) as CommonArtifactManifest;
  const contentArtifact: CommonArtifactManifest = {
    schemaVersion: "1.0.0",
    artifactId: `content-input-ocr-human-${action}`,
    kind: "content-input",
    contractVersion: "1.0.0",
    mediaType: "application/json",
    files: declared,
    createdAt: "2026-08-30T00:01:00.000Z",
    producer: { stageId: "core.resume", workerId: "core.orchestrator", workerVersion: "1.0.0", attemptId: `resume-${action}` },
    inputRefs: [...sourceArtifact.inputRefs, {
      artifactRef: sourceProofreadArtifact.artifactRef,
      kind: sourceProofreadArtifact.kind,
      contractVersion: sourceProofreadArtifact.contractVersion,
      manifestPath: sourceProofreadArtifact.manifestPath,
      expectedSha256: sourceProofreadArtifact.expectedSha256,
    }],
    dataClass: "public",
    metadata: { entrypoint: inputRelative, moduleSchema: "standard-input/5.0.0", continuation: true, rerunOcr: false },
  };
  const artifactPath = path.join(jobRoot, "artifact.json");
  await writeFile(artifactPath, `${stableStringify(contentArtifact, 2)}\n`);
  return {
    jobRoot,
    paths: {
      manifestPath: artifactPath,
      inputPath,
      outputDir: path.join(jobRoot, "resumed-attempt", "outputs"),
      promptRoot: path.join(workerRoot, "prompts"),
      registryPath: path.join(workerRoot, "config", "categories.v2.json"),
      attemptId: `attempt-resumed-${action}`,
      jobRoot,
      operation: "understand-and-card",
    },
  };
}

async function writeOcrHumanCommonRequest(jobRoot: string, action: "accept_original" | "user_correction"): Promise<string> {
  const artifactPath = path.join(jobRoot, "artifact.json");
  const artifact = JSON.parse(await readFile(artifactPath, "utf8")) as CommonArtifactManifest;
  const artifactDigest = await fileDigest(artifactPath);
  const attemptId = `attempt-common-ocr-human-${action}`;
  const attemptPath = `runs/run-ocr-human/stages/content.understand-and-card/attempts/${attemptId}`;
  const request: CommonWorkerRequest = {
    schemaVersion: "1.0.0",
    requestId: `request-${attemptId}`,
    operationId: `operation-${attemptId}`,
    taskId: artifact.artifactId,
    runId: "run-ocr-human",
    stageId: "content.understand-and-card",
    attemptId,
    workerId: "content.deepseek",
    operation: "understand-and-card",
    idempotencyKey: sha256(`ocr-human:${action}`),
    attemptPath,
    outputPath: `${attemptPath}/outputs`,
    progressPath: `${attemptPath}/progress.ndjson`,
    resultPath: `${attemptPath}/result.json`,
    cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
    inputRefs: [{ artifactRef: artifact.artifactId, kind: artifact.kind, contractVersion: artifact.contractVersion, manifestPath: "artifact.json", expectedSha256: artifactDigest.sha256 }],
    configSnapshot: { promptSet: "open-md-v2", categoryRegistry: "category-registry/2.0.0" },
    payload: { schemaVersion: "content-run/1.0.0", contentInputPath: "inputs/standard-input.v5.json", providerMode: "replay", replayPath: "inputs/provider-replay.v1.json" },
  };
  const requestPath = path.join(jobRoot, ...attemptPath.split("/"), "request.json");
  await mkdir(path.dirname(requestPath), { recursive: true });
  await writeFile(requestPath, `${stableStringify(request, 2)}\n`);
  return requestPath;
}

async function declaration(root: string, relativePath: string, mediaType: string) {
  const digest = await fileDigest(path.join(root, ...relativePath.split("/")));
  return { path: relativePath, sha256: digest.sha256, size: digest.size, media_type: mediaType };
}

async function stageCommonFixture(fixtureName: string, attemptId: string, dataClass: CommonArtifactManifest["dataClass"] = "personal", providerMode: "replay" | "live" = "replay", inputMutator?: (input: Record<string, unknown>) => void): Promise<{ jobRoot: string; requestPath: string; resultPath: string; cancelPath: string }> {
  const sourceRoot = path.join(workerRoot, "fixtures", fixtureName);
  const jobRoot = await mkdtemp(path.join(tmpdir(), "clipper-content-common-test-"));
  const inputRoot = path.join(jobRoot, "inputs", "content");
  await mkdir(inputRoot, { recursive: true });
  const inputNames = await readdir(path.join(sourceRoot, "inputs"));
  const inputFileName = inputNames.includes("standard-input.v7.json")
    ? "standard-input.v7.json"
    : inputNames.includes("standard-input.v3.json")
      ? "standard-input.v3.json"
    : inputNames.includes("standard-input.v6.json")
      ? "standard-input.v6.json"
    : inputNames.includes("standard-input.v2.json")
      ? "standard-input.v2.json"
      : "standard-input.v1.json";
  const input = JSON.parse(await readFile(path.join(sourceRoot, "inputs", inputFileName), "utf8")) as {
    schema_version: string;
    items: Array<{ source: { kind: string; material_path?: string; assets?: Array<{ material_path: string }> }; language_selection?: { context_segments: Array<{ material_path: string }> } }>;
    questionWorkflow?: { phase: "answer" | "make-cards"; cardStructureRef?: string };
  };
  inputMutator?.(input as unknown as Record<string, unknown>);
  const materialPaths: string[] = [];
  for (const item of input.items) {
    if (item.source.kind === "media_set") {
      for (const asset of item.source.assets ?? []) {
        materialPaths.push(asset.material_path);
        asset.material_path = `inputs/content/${asset.material_path}`;
      }
    } else if (item.source.material_path) {
      materialPaths.push(item.source.material_path);
      item.source.material_path = `inputs/content/${item.source.material_path}`;
    }
    for (const context of item.language_selection?.context_segments ?? []) {
      materialPaths.push(context.material_path);
      context.material_path = `inputs/content/${context.material_path}`;
    }
  }
  if (input.questionWorkflow?.cardStructureRef) {
    const cardStructurePath = input.questionWorkflow.cardStructureRef;
    materialPaths.push(cardStructurePath);
    input.questionWorkflow.cardStructureRef = `inputs/content/${cardStructurePath}`;
  }
  const contentInputRelative = `inputs/content/${inputFileName}`;
  const replayRelative = "inputs/content/provider-replay.v1.json";
  await writeFile(path.join(jobRoot, ...contentInputRelative.split("/")), `${stableStringify(input, 2)}\n`);
  if (providerMode === "replay") await writeFile(path.join(jobRoot, ...replayRelative.split("/")), await readFile(path.join(sourceRoot, "provider-replay.v1.json")));
  for (const materialPath of materialPaths) {
    const target = path.join(inputRoot, ...materialPath.split("/"));
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, await readFile(path.join(sourceRoot, ...materialPath.split("/"))));
  }
  const declaredPaths = [contentInputRelative, ...(providerMode === "replay" ? [replayRelative] : []), ...materialPaths.map((value) => `inputs/content/${value}`)];
  const files = await Promise.all(declaredPaths.map(async (relativePath) => {
    const digest = await fileDigest(path.join(jobRoot, ...relativePath.split("/")));
    return { path: relativePath, sha256: digest.sha256, bytes: digest.size, mediaType: relativePath.endsWith(".md") ? "text/markdown; charset=utf-8" : "application/json" };
  }));
  const artifact: CommonArtifactManifest = {
    schemaVersion: "1.0.0",
    artifactId: `content-input-${attemptId}`,
    kind: "content-input",
    contractVersion: "1.0.0",
    mediaType: "application/json",
    files,
    createdAt: "2026-08-25T00:00:00.000Z",
    producer: { stageId: "capture.normalize", workerId: "test.fixture", workerVersion: "1.0.0", attemptId: `source-${attemptId}` },
    inputRefs: [],
    dataClass,
    metadata: { entrypoint: contentInputRelative, moduleSchema: input.schema_version },
  };
  const artifactRelative = "inputs/content/artifact.json";
  const artifactPath = path.join(jobRoot, ...artifactRelative.split("/"));
  await writeFile(artifactPath, `${stableStringify(artifact, 2)}\n`);
  const artifactDigest = await fileDigest(artifactPath);
  const attemptPath = `runs/run-1/stages/content.understand-and-card/attempts/${attemptId}`;
  const attemptDirectory = path.join(jobRoot, ...attemptPath.split("/"));
  await mkdir(attemptDirectory, { recursive: true });
  const request: CommonWorkerRequest = {
    schemaVersion: "1.0.0",
    requestId: `request-${attemptId}`,
    operationId: `operation-${attemptId}`,
    taskId: `task-${attemptId}`,
    runId: "run-1",
    stageId: "content.understand-and-card",
    attemptId,
    workerId: "content.deepseek",
    operation: input.questionWorkflow?.phase === "answer"
      ? "question.answer"
      : input.questionWorkflow?.phase === "make-cards"
        ? "question.make-cards"
        : "understand-and-card",
    idempotencyKey: sha256(`idempotency:${attemptId}`),
    attemptPath,
    outputPath: `${attemptPath}/outputs`,
    progressPath: `${attemptPath}/progress.ndjson`,
    resultPath: `${attemptPath}/result.json`,
    cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
    inputRefs: [{ artifactRef: artifact.artifactId, kind: artifact.kind, contractVersion: artifact.contractVersion, manifestPath: artifactRelative, expectedSha256: artifactDigest.sha256 }],
    configSnapshot: { promptSet: "open-md-v2", categoryRegistry: "category-registry/2.0.0" },
    payload: providerMode === "replay"
      ? { schemaVersion: "content-run/1.0.0", contentInputPath: contentInputRelative, providerMode, replayPath: replayRelative }
      : { schemaVersion: "content-run/1.0.0", contentInputPath: contentInputRelative, providerMode },
  };
  const requestPath = path.join(attemptDirectory, "request.json");
  await writeFile(requestPath, `${stableStringify(request, 2)}\n`);
  return {
    jobRoot,
    requestPath,
    resultPath: path.join(jobRoot, ...request.resultPath.split("/")),
    cancelPath: path.join(jobRoot, ...request.cancelSignal.path.split("/")),
  };
}

async function stageContentConfigSnapshot(jobRoot: string, requestPath: string, settingsRevisionId: string, runtimePolicy?: { profileId: string; profileVersion: string; languagePracticeDefault: "ask" | "meaning-reading" | "writing" | "both"; provider: { model: "deepseek-chat" | "deepseek-reasoner"; maxRetries: number } }): Promise<{ promptSetSha256: string; systemPromptSha256: string; categoryRegistrySha256: string; capabilityProfileSha256?: string }> {
  const rootRelative = `inputs/content-settings/${settingsRevisionId}`;
  const promptRootRelative = `${rootRelative}/prompts`;
  const registryRelative = `${rootRelative}/config/categories.v2.json`;
  const snapshotRelative = `${rootRelative}/content-config-snapshot.v1.json`;
  const artifactRelative = `${rootRelative}/artifact.json`;
  const registry = JSON.parse(await readFile(path.join(workerRoot, "config", "categories.v2.json"), "utf8")) as CategoryRegistry;
  const promptPaths = new Set(["shared/system.md", "shared/card-output-contract.md", "stages/ocr-proofreading.md", "stages/cleaning.md", "stages/classification.md", "stages/language-writing.md", "stages/question-answer.md"]);
  for (const category of registry.categories) {
    promptPaths.add(category.prompt_file);
    for (const promptFile of Object.values(category.template_prompt_files ?? {})) promptPaths.add(promptFile);
  }
  const promptFiles: Array<{ path: string; packagePath: string; sha256: string; bytes: number }> = [];
  for (const promptPath of [...promptPaths].sort((left, right) => left.localeCompare(right))) {
    const packageRelative = `${promptRootRelative}/${promptPath}`;
    const target = path.join(jobRoot, ...packageRelative.split("/"));
    await mkdir(path.dirname(target), { recursive: true });
    await copyFile(path.join(workerRoot, "prompts", ...promptPath.split("/")), target);
    if (promptPath === "shared/system.md") await writeFile(target, `${await readFile(target, "utf8")}\n<!-- immutable custom settings replay -->\n`);
    const digest = await fileDigest(target);
    promptFiles.push({ path: promptPath, packagePath: packageRelative, sha256: digest.sha256, bytes: digest.size });
  }
  const registryTarget = path.join(jobRoot, ...registryRelative.split("/"));
  await mkdir(path.dirname(registryTarget), { recursive: true });
  await copyFile(path.join(workerRoot, "config", "categories.v2.json"), registryTarget);
  const registryDigest = await fileDigest(registryTarget);
  const promptSetSha256 = sha256(stableStringify(promptFiles.map(({ path: promptPath, sha256: digest, bytes }) => ({ path: promptPath, sha256: digest, bytes }))));
  const snapshot = {
    schemaVersion: runtimePolicy ? "content-config-snapshot/1.1.0" : "content-config-snapshot/1.0.0",
    settingsRevisionId,
    workerCompatibility: { workerId: "content.deepseek", minimumVersion: "0.6.0" },
    promptSet: { id: "user-open-md-v2-offline", source: "user-copy", promptRootPath: promptRootRelative, sha256: promptSetSha256, files: promptFiles },
    categoryRegistry: { id: "category-registry/2.0.0", schemaVersion: "category-registry/2.0.0", packagePath: registryRelative, sha256: registryDigest.sha256, bytes: registryDigest.size },
    providerBoundary: { provider: "deepseek", credentials: "process-injected-only" },
    ...(runtimePolicy ? { runtimePolicy } : {}),
  };
  const snapshotPath = path.join(jobRoot, ...snapshotRelative.split("/"));
  await writeFile(snapshotPath, `${stableStringify(snapshot, 2)}\n`);
  const snapshotDigest = await fileDigest(snapshotPath);
  const artifact: CommonArtifactManifest = {
    schemaVersion: "1.0.0",
    artifactId: `content-config-${settingsRevisionId}`,
    kind: "content-config-snapshot",
    contractVersion: "1.0.0",
    mediaType: "application/json",
    files: [
      { path: snapshotRelative, sha256: snapshotDigest.sha256, bytes: snapshotDigest.size, mediaType: "application/json" },
      { path: registryRelative, sha256: registryDigest.sha256, bytes: registryDigest.size, mediaType: "application/json" },
      ...promptFiles.map((file) => ({ path: file.packagePath, sha256: file.sha256, bytes: file.bytes, mediaType: "text/markdown; charset=utf-8" })),
    ],
    createdAt: "2026-08-28T00:00:00.000Z",
    producer: { stageId: "content.settings", workerId: "test.settings", workerVersion: "1.0.0", attemptId: `publish-${settingsRevisionId}` },
    inputRefs: [],
    dataClass: "public",
    metadata: { entrypoint: snapshotRelative, credentialsRecorded: false },
  };
  const artifactPath = path.join(jobRoot, ...artifactRelative.split("/"));
  await writeFile(artifactPath, `${stableStringify(artifact, 2)}\n`);
  const artifactDigest = await fileDigest(artifactPath);
  const request = JSON.parse(await readFile(requestPath, "utf8")) as CommonWorkerRequest;
  request.inputRefs.push({ artifactRef: artifact.artifactId, kind: artifact.kind, contractVersion: artifact.contractVersion, manifestPath: artifactRelative, expectedSha256: artifactDigest.sha256 });
  request.configSnapshot.contentSettingsRevisionId = settingsRevisionId;
  request.payload.contentConfigPath = snapshotRelative;
  await writeFile(requestPath, `${stableStringify(request, 2)}\n`);
  return {
    promptSetSha256,
    systemPromptSha256: promptFiles.find((file) => file.path === "shared/system.md")!.sha256,
    categoryRegistrySha256: registryDigest.sha256,
    ...(runtimePolicy ? { capabilityProfileSha256: sha256(stableStringify(runtimePolicy)) } : {}),
  };
}

async function readOutputBundle<T>(jobRoot: string, result: { outputs: Array<{ kind: string; manifestPath: string }> }, kind: string): Promise<T> {
  const ref = result.outputs.find((entry) => entry.kind === kind);
  assert.ok(ref, `missing ${kind} output`);
  const manifest = JSON.parse(await readFile(path.join(jobRoot, ...ref.manifestPath.split("/")), "utf8")) as CommonArtifactManifest;
  assert.equal(manifest.files.length, 1);
  return JSON.parse(await readFile(path.join(jobRoot, ...manifest.files[0]!.path.split("/")), "utf8")) as T;
}

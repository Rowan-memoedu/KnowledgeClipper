import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const projectRoot = path.dirname(workerRoot);
const stampValue = "20260831T093500Z";
const jobRoot = path.join(projectRoot, "canary", "sealed-live-ocr-anki-v1.2.18", "20260831T091200Z", "jobs", "live-ocr-anki-v1218-20260831090948536");
const originalAttempt = path.join(jobRoot, "runs", "run-20260831090951961", "stages", "content.understand-and-card", "attempts", "attempt-20260831091015983-2e6b5b8beeac4f65ae0d4adbe5e0b659");
const recoveryAttempt = path.join(jobRoot, "runs", "run-content-ocr-coordinate-recovery-20260831T093110Z", "stages", "content.understand-and-card", "attempts", "attempt-content-ocr-coordinate-recovery-replay-0001");
const correctionAttempt = path.join(jobRoot, "runs", "run-content-ocr-human-20260831T093138Z", "stages", "content.understand-and-card", "attempts", "attempt-content-ocr-human-user_correction-replay-0001");
const acceptAttempt = path.join(jobRoot, "runs", "run-content-ocr-human-20260831T093326Z", "stages", "content.understand-and-card", "attempts", "attempt-content-ocr-human-accept_original-replay-0001");
const sourcePath = path.join(jobRoot, "runs", "run-20260831090951961", "stages", "image-ocr", "attempts", "attempt-20260831090952367-6c06e8121ea541b6adcf7e9ce730917e", "outputs", "ocr.capture.image.question.0001.markdown", "v0001", "content.md");
const recoveryModuleReceiptPath = path.join(workerRoot, "canary", "ocr-coordinate-recovery", "20260831T093110Z-v1218-frozen-response-replay", "receipt.v1.json");
const testReceiptPath = path.join(workerRoot, "canary", "ocr-human-resolution-tests", "20260831T093424Z", "receipt.v1.json");
const releaseRoot = path.join(workerRoot, "releases", "content.deepseek-0.9.1-win-x64-formal-20260831T092939Z");
const releaseManifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const formalReceiptPath = path.join(workerRoot, "canary", "formal-release", path.basename(releaseRoot), "20260831T093009218Z-settings-snapshot-replay", "formal-release-offline-receipt.v1.json");
const fixturePath = path.join(workerRoot, "fixtures", "ocr-coordinate-normalization-v1", "v1218-frozen-evidence.json");
const casesPath = path.join(workerRoot, "fixtures", "ocr-coordinate-normalization-v1", "cases.json");

const [originalResult, recoveryResult, correctionResult, acceptResult, recoveryBundle, correctionResolution, correctionLearning, correctionCards, acceptResolution, releaseManifest, testReceipt] = await Promise.all([
  readJson(path.join(originalAttempt, "result.json")),
  readJson(path.join(recoveryAttempt, "result.json")),
  readJson(path.join(correctionAttempt, "result.json")),
  readJson(path.join(acceptAttempt, "result.json")),
  readJson(path.join(recoveryAttempt, "outputs", "ocr-proofread-bundle.schema-v1.json")),
  readJson(path.join(correctionAttempt, "outputs", "ocr-human-resolution-bundle.schema-v1.json")),
  readJson(path.join(correctionAttempt, "outputs", "learning-bundle.schema-v3.json")),
  readJson(path.join(correctionAttempt, "outputs", "card-bundle.schema-v1.json")),
  readJson(path.join(acceptAttempt, "outputs", "ocr-human-resolution-bundle.schema-v1.json")),
  readJson(releaseManifestPath),
  readJson(testReceiptPath),
]);
const source = await readFile(sourcePath, "utf8");
const sourceEvidence = await evidence(sourcePath);
if (sourceEvidence.sha256 !== "1B43E9F82B60C3CA998A587FD5AC577AAA98E69C51E4A238347C27579649ACAA") throw new Error("SOURCE_SHA_INVALID");
if (originalResult.status !== "failed" || !originalResult.error.message.includes("ocr-proofreading.before-exact") || originalResult.outputs.length !== 0) throw new Error("ORIGINAL_FAILURE_INVALID");
if (recoveryResult.status !== "completed" || recoveryResult.payload.moduleReleaseState !== "unresolved" || recoveryResult.outputs.some((entry) => entry.kind === "card-bundle")) throw new Error("RECOVERY_DECISION_INVALID");
const recoveryItem = recoveryBundle.items[0];
const candidate = recoveryItem.unresolved[0];
if (recoveryItem.original_text !== source || recoveryItem.proofread_text !== source || recoveryItem.corrections.length !== 0 || candidate.start !== 31 || candidate.end !== 36 || candidate.exact_text !== "CANAR" || !candidate.evidence.includes("CANARY") || !candidate.evidence.includes("unique_relocated")) throw new Error("RECOVERY_BUNDLE_INVALID");
for (const result of [correctionResult, acceptResult]) if (result.status !== "completed" || result.payload.moduleReleaseState !== "ready" || result.outputs.map((entry) => entry.kind).join(",") !== "ocr-human-resolution-bundle,learning-bundle,card-bundle") throw new Error("HUMAN_CONTINUATION_INVALID");
const correctionRecord = correctionResolution.records[0];
if (correctionRecord.actor !== "user" || correctionRecord.provenance !== "human_submitted" || correctionRecord.resolved_text !== source.replace("CANAR", "CANARY") || correctionRecord.user_diffs[0].after !== "Y") throw new Error("USER_CORRECTION_INVALID");
if (acceptResolution.records[0].resolved_text !== source || acceptResolution.records[0].user_diffs.length !== 0) throw new Error("ACCEPT_ORIGINAL_INVALID");
if (correctionLearning.ai_trace.some((entry) => entry.stage === "ocr_proofreading") || correctionLearning.ai_trace.some((entry) => entry.mode !== "replay")) throw new Error("CONTINUATION_PROVIDER_INVALID");
if (correctionCards.cards.length !== 1 || correctionCards.cards[0].front_blocks[0].text !== "Question: What is 19 + 23?" || correctionCards.cards[0].back_blocks[0].text !== "Answer: 42") throw new Error("CARD_INVALID");
if (testReceipt.tests !== 87 || testReceipt.pass !== 87 || testReceipt.fail !== 0) throw new Error("TEST_RECEIPT_INVALID");
if (releaseManifest.worker.workerVersion !== "0.9.1") throw new Error("RELEASE_VERSION_INVALID");

const readablePath = path.join(workerRoot, "acceptance", `content-ocr-coordinate-recovery-0.9.1-${stampValue}.md`);
await writeTextOnce(readablePath, [
  "# Content 0.9.1 OCR 模型坐标恢复与人工续跑验收",
  "",
  "## 失败基线：authority v1.2.18 / Content 0.9.0",
  "",
  `原始 OCR：${source}`,
  "",
  "DeepSeek 候选：before=CANAR，after=CANARY，模型坐标=[34,39)。0.9.0 因 before-exact 直接 failed，未产生 decision token。",
  "",
  "## 0.9.1 冻结 response replay",
  "",
  "- exact before 在声明 segment 中唯一匹配，规范化为 [31,36)。",
  "- 候选 disposition=unresolved_human_review_not_applied；原文仍为 CANAR。",
  "- WorkerResult=completed，moduleReleaseState=unresolved；只发布 proofread + LearningBundle v2，不发布 CardBundle。",
  "- DeepSeek 外部调用 0；OCR 重跑 0；Exporter 调用 0。",
  "",
  "## 人工 user_correction",
  "",
  `用户 diff：${JSON.stringify(correctionRecord.user_diffs[0])}`,
  "",
  `Resolved：${correctionRecord.resolved_text}`,
  "",
  `Card Front：${correctionCards.cards[0].front_blocks[0].text}`,
  "",
  `Card Back：${correctionCards.cards[0].back_blocks[0].text}`,
  "",
  "该字符来自 actor=user / provenance=human_submitted，不包装为 AI 自主修复。",
  "",
  "## 人工 accept_original",
  "",
  "用户明确接受后保留 CANAR，diff=[]；才进入清洗、分类、制卡并发布 ready bundles。",
  "",
  "## 边界",
  "",
  "普通 text 仍执行 delete-only；坐标恢复仅作用于 ocr_text proofreading。Core 仍拥有 waiting_input、一次性 decision token、resume、新 attempt、ancestry 与 destinations；本模块未调用任何 exporter。",
  "",
].join("\n"));

const receiptPath = path.join(workerRoot, "receipts", `content-ocr-coordinate-recovery-0.9.1-admission-${stampValue}.json`);
const receipt = {
  schemaVersion: "content-ocr-coordinate-recovery-admission/1.0.0",
  status: "PASS",
  authorityCandidate: true,
  worker: { id: "content.deepseek", version: "0.9.1" },
  defect: { authority: "v1.2.18", originalWorkerVersion: "0.9.0", error: originalResult.error.message, source: sourceEvidence, originalResult: await evidence(path.join(originalAttempt, "result.json")) },
  fix: {
    scope: "ocr_text proofreading only",
    policy: "If the model range is not exact, locate literal before only inside the declared segment. Unique, multiple, missing and invalid ranges all remain unresolved for human review and never apply the suggestion.",
    candidate: { before: "CANAR", suggestedAfter: "CANARY", modelRange: { start: 34, end: 39 }, normalizedRange: { start: 31, end: 36 }, applied: false, reason: candidate.reason, evidence: candidate.evidence },
    ordinaryTextDeleteOnlyUnchanged: true,
  },
  fixtures: { sourceNeutralCases: await evidence(casesPath), frozenRealResponse: await evidence(fixturePath) },
  validation: { fullTests: await evidence(testReceiptPath), tests: 87, pass: 87, fail: 0 },
  replayDecision: {
    providerMode: "frozen-real-response-replay",
    externalConnections: 0,
    receipt: await evidence(recoveryModuleReceiptPath),
    request: await evidence(path.join(recoveryAttempt, "request.json")),
    result: await evidence(path.join(recoveryAttempt, "result.json")),
    proofreadManifest: await evidence(path.join(recoveryAttempt, "outputs", "ocr-proofread-bundle.artifact.v1.json")),
    proofreadBundle: await evidence(path.join(recoveryAttempt, "outputs", "ocr-proofread-bundle.schema-v1.json")),
    learningManifest: await evidence(path.join(recoveryAttempt, "outputs", "learning-bundle.artifact.v2.json")),
    cardBundlePublished: false,
  },
  humanContinuations: {
    userCorrection: await continuationEvidence(correctionAttempt, correctionResult, correctionRecord),
    acceptOriginal: await continuationEvidence(acceptAttempt, acceptResult, acceptResolution.records[0]),
  },
  formalRelease: {
    root: releaseRoot,
    manifest: await evidence(releaseManifestPath),
    payloadTreeSha256: releaseManifest.payloadTreeSha256,
    exe: await evidence(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe")),
    offlineQualification: await evidence(formalReceiptPath),
  },
  securityAndEffects: { sourceMutated: false, liveDeepSeekCalledThisFix: false, syntheticOracleUsed: false, ocrRerun: false, exporterCalls: 0, credentialsRecorded: false },
  coreAdmission: { waitingInputOwner: "core", decisionTokenOwner: "core", preserveAncestryAndDestinations: true, createNewAttempt: true, defaultRerunOcr: false },
  readable: await evidence(readablePath),
  completedAt: new Date().toISOString(),
};
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ status: "PASS", receipt: await evidence(receiptPath), readable: await evidence(readablePath), release: { root: releaseRoot, manifest: await evidence(releaseManifestPath), exe: await evidence(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe")), treeSha256: releaseManifest.payloadTreeSha256 }, replayResult: await evidence(path.join(recoveryAttempt, "result.json")), correctionResult: await evidence(path.join(correctionAttempt, "result.json")), acceptResult: await evidence(path.join(acceptAttempt, "result.json")) })}\n`);

async function continuationEvidence(attemptRoot, result, record) {
  return {
    action: record.action,
    actor: record.actor,
    provenance: record.provenance,
    status: result.status,
    moduleReleaseState: result.payload.moduleReleaseState,
    request: await evidence(path.join(attemptRoot, "request.json")),
    result: await evidence(path.join(attemptRoot, "result.json")),
    resolutionManifest: await evidence(path.join(attemptRoot, "outputs", "ocr-human-resolution-bundle.artifact.v1.json")),
    learningManifest: await evidence(path.join(attemptRoot, "outputs", "learning-bundle.artifact.v3.json")),
    cardManifest: await evidence(path.join(attemptRoot, "outputs", "card-bundle.artifact.v1.json")),
    resolvedText: record.resolved_text,
    userDiffs: record.user_diffs,
    ocrRerun: false,
    exporterCalls: 0,
  };
}
async function evidence(target) { const bytes = await readFile(target); return { path: target, bytes: bytes.length, sha256: createHash("sha256").update(bytes).digest("hex").toUpperCase() }; }
async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function writeJsonOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" }); }
async function writeTextOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, value, { flag: "wx" }); }

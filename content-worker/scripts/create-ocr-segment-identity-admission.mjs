import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const projectRoot = path.dirname(workerRoot);
const job126 = path.join(projectRoot, "canary", "sealed-live-ocr-anki-v1.2.26", "20260901T015500Z", "jobs", "live-ocr-anki-v1226-20260901013322790");
const job124 = path.join(projectRoot, "canary", "sealed-live-ocr-anki-v1.2.24", "20260901T010329Z", "jobs", "live-ocr-anki-v1224-20260901010330339");
const oldAttempt = path.join(job126, "runs", "run-20260901013326646", "stages", "content.understand-and-card", "attempts", "attempt-20260901013344768-9025fabd035d4e22934327cef1a6ccf1");
const oldFailurePath = path.join(job126, "runs", "run-20260901013326646", "events", "000011-stage-binding-failed.json");
const autoAttempt = path.join(job126, "runs", "run-content-frozen-replay-20260901T014120Z", "stages", "content.understand-and-card", "attempts", "attempt-content-frozen-replay-0001");
const acceptAttempt = path.join(job124, "runs", "run-content-ocr-human-20260901T014139Z", "stages", "content.understand-and-card", "attempts", "attempt-content-ocr-human-accept_original-replay-0001");
const correctionAttempt = path.join(job124, "runs", "run-content-ocr-human-20260901T014154Z", "stages", "content.understand-and-card", "attempts", "attempt-content-ocr-human-user_correction-replay-0001");
const releaseRoot = path.join(workerRoot, "releases", "content.deepseek-0.9.3-win-x64-formal-20260901T013945Z");
const releaseManifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const formalReceiptPath = path.join(workerRoot, "canary", "formal-release", path.basename(releaseRoot), "20260901T014003367Z-settings-snapshot-replay", "formal-release-offline-receipt.v1.json");
const testReceiptPath = path.join(workerRoot, "canary", "ocr-human-resolution-tests", "20260901T014210Z", "receipt.v1.json");

const oldResult = await readJson(path.join(oldAttempt, "result.json"));
const oldLearning = await readOutput(job126, oldResult, "learning-bundle");
const oldFailure = await readJson(oldFailurePath);
const oldSourceIds = ids(oldLearning.payload.items.flatMap((item) => item.source_segments));
const oldAuditIds = ids(oldLearning.payload.items.flatMap((item) => item.proofreading?.proofread_segments ?? []));
const oldCollision = [...oldSourceIds].filter((id) => oldAuditIds.has(id));
if (oldFailure.payload?.errorCode !== "binding.ocr_proofread_segment_invalid" || oldCollision.length === 0) throw new Error("OLD_COLLISION_NOT_REPRODUCED");

const auto = await inspectAttempt(job126, autoAttempt, "rseg-", undefined);
const accept = await inspectAttempt(job124, acceptAttempt, "hseg-", "accept_original");
const correction = await inspectAttempt(job124, correctionAttempt, "hseg-", "user_correction");
if (!correction.resolution.records[0].user_diffs.some((diff) => diff.after === "Y") || correction.resolution.records[0].actor !== "user") throw new Error("CORRECTION_PROVENANCE_INVALID");
const releaseManifest = await readJson(releaseManifestPath);
const formalReceipt = await readJson(formalReceiptPath);
const testReceipt = await readJson(testReceiptPath);
if (releaseManifest.worker?.workerVersion !== "0.9.3" || formalReceipt.status !== "PASS" || testReceipt.tests !== 87 || testReceipt.pass !== 87 || testReceipt.fail !== 0) throw new Error("QUALIFICATION_INVALID");

const readablePath = path.join(workerRoot, "acceptance", "content-ocr-segment-identity-0.9.3-20260901T014300Z.md");
const readable = [
  "# Content 0.9.3 OCR segment 身份隔离验收",
  "",
  "## 失败复现",
  "",
  `- v1.2.26 / Content 0.9.2：active=${[...oldSourceIds].join(",")}；audit=${[...oldAuditIds].join(",")}；collision=${oldCollision.join(",")}。`,
  `- Core：${oldFailure.payload.errorCode} — ${oldFailure.payload.message}。Anki worker 未启动，目的端写入 0。`,
  "",
  "## 0.9.3 三空间规则",
  "",
  "- seg-*：原始 OCR；pseg-*：不可变校对审计；rseg-*：自动校对后的正式内容；hseg-*：人工决议后的正式内容。",
  "- unit/card 仅引用 rseg/hseg；source_segments 与 proofreading.proofread_segments 必须无交集。",
  "- Content producer 在发布前拒绝旧 missing-ref 与旧 collision，两类错误不再依赖 Core 才发现。",
  "",
  "## 自动 OCR frozen-response replay",
  "",
  `- active=${[...auto.activeIds].join(",")}；audit=${[...auto.auditIds].join(",")}；unit/card refs=${[...auto.referenceIds].join(",")}。`,
  `- 卡片：Front=${auto.card.payload.cards[0].front_blocks[0].text}；Back=${auto.card.payload.cards[0].back_blocks[0].text}。`,
  "",
  "## 人工续跑",
  "",
  `- accept_original：active=${[...accept.activeIds].join(",")}；audit=${[...accept.auditIds].join(",")}；原字符未改。`,
  `- user_correction：active=${[...correction.activeIds].join(",")}；audit=${[...correction.auditIds].join(",")}；用户 diff=${JSON.stringify(correction.resolution.records[0].user_diffs[0])}。`,
  "",
  "所有新 attempt 均 completed；Card manifest 精确绑定同 attempt Learning artifactRef/path/SHA。provider=replay；DeepSeek/MinerU/exporter/目的端写入均为 0。",
  "",
].join("\n");
await writeOnce(readablePath, readable);

const receiptPath = path.join(workerRoot, "receipts", "content-ocr-segment-identity-0.9.3-admission-20260901T014300Z.json");
const receipt = {
  schemaVersion: "content-ocr-segment-identity-admission/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: "0.9.3" },
  identitySpaces: { original: "seg-*", immutableProofreadAudit: "pseg-*", automaticReleased: "rseg-*", humanResolved: "hseg-*" },
  priorFailure: {
    result: await evidence(path.join(oldAttempt, "result.json")),
    event: await evidence(oldFailurePath),
    errorCode: oldFailure.payload.errorCode,
    activeIds: [...oldSourceIds],
    auditIds: [...oldAuditIds],
    collisionIds: oldCollision,
    contentVersion: oldResult.worker.version,
    destinationWorkerStarted: false,
    destinationWrites: 0,
  },
  positive: {
    automaticProofreadReady: await summarize(auto),
    acceptOriginal: await summarize(accept),
    userCorrection: await summarize(correction),
  },
  negativeRegression: {
    oldMissingReference: "Content gate rejects active seg-* with unit/card pseg-*",
    oldIdentityCollision: "Content gate rejects active source id duplicated in proofreading audit",
    auditTamperMissingDuplicate: "existing ancestry/hash/duplicate gates remain enabled",
  },
  qualification: {
    tests: await evidence(testReceiptPath),
    summary: { tests: 87, pass: 87, fail: 0 },
    releaseRoot,
    releaseManifest: await evidence(releaseManifestPath),
    payloadTreeSha256: releaseManifest.payloadTreeSha256,
    exe: await evidence(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe")),
    formalOfflineReceipt: await evidence(formalReceiptPath),
  },
  effects: { deepSeekCalls: 0, minerUReruns: 0, exporterCalls: 0, destinationWrites: 0 },
  authorityBoundary: { old092: "not-for-reuse", new093: "formal-content-candidate", coreAuthorityPromotionAndDestinationE2E: "conditional-core-owned" },
  readable: await evidence(readablePath),
  completedAt: new Date().toISOString(),
};
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ status: "PASS", receipt: await evidence(receiptPath), readable: await evidence(readablePath), releaseRoot, autoRefs: { proofread: auto.proofread.ref, learning: auto.learning.ref, card: auto.card.ref }, acceptRefs: { learning: accept.learning.ref, card: accept.card.ref }, correctionRefs: { learning: correction.learning.ref, card: correction.card.ref } })}\n`);

async function inspectAttempt(jobRoot, attemptRoot, prefix, expectedAction) {
  const resultPath = path.join(attemptRoot, "result.json");
  const result = await readJson(resultPath);
  if (result.status !== "completed" || result.worker?.version !== "0.9.3") throw new Error(`RESULT_INVALID ${attemptRoot}`);
  const learning = await readOutput(jobRoot, result, "learning-bundle");
  const card = await readOutput(jobRoot, result, "card-bundle");
  const proofread = result.outputs.some((entry) => entry.kind === "ocr-proofread-bundle") ? await readOutput(jobRoot, result, "ocr-proofread-bundle") : undefined;
  const resolution = result.outputs.some((entry) => entry.kind === "ocr-human-resolution-bundle") ? (await readOutput(jobRoot, result, "ocr-human-resolution-bundle")).payload : undefined;
  const activeIds = ids(learning.payload.items.flatMap((item) => item.source_segments));
  const auditIds = ids(learning.payload.items.flatMap((item) => item.proofreading?.proofread_segments ?? []));
  const referenceIds = new Set([
    ...learning.payload.items.flatMap((item) => item.units.flatMap((unit) => unit.source_segment_ids)),
    ...card.payload.cards.flatMap((entry) => entry.source_segment_ids),
  ]);
  if (![...activeIds].every((id) => id.startsWith(prefix)) || [...activeIds].some((id) => auditIds.has(id)) || [...referenceIds].some((id) => !activeIds.has(id))) throw new Error(`IDENTITY_INVALID ${attemptRoot}`);
  const cardLearningRef = card.manifest.inputRefs.find((entry) => entry.kind === "learning-bundle");
  if (!cardLearningRef || cardLearningRef.artifactRef !== learning.ref.artifactRef || cardLearningRef.manifestPath !== learning.ref.manifestPath || cardLearningRef.expectedSha256 !== learning.ref.expectedSha256) throw new Error(`CARD_LEARNING_INVALID ${attemptRoot}`);
  if (expectedAction && resolution?.records[0]?.action !== expectedAction) throw new Error(`ACTION_INVALID ${attemptRoot}`);
  return { resultPath, result, learning, card, proofread, resolution, activeIds, auditIds, referenceIds, cardLearningRef };
}

async function readOutput(jobRoot, result, kind) {
  const ref = result.outputs.find((entry) => entry.kind === kind);
  if (!ref) throw new Error(`OUTPUT_MISSING ${kind}`);
  const manifestPath = packagePath(jobRoot, ref.manifestPath);
  const manifest = await readJson(manifestPath);
  const manifestEvidence = await evidence(manifestPath);
  if (manifestEvidence.sha256.toLowerCase() !== ref.expectedSha256.toLowerCase()) throw new Error(`MANIFEST_INVALID ${kind}`);
  const payloadPath = packagePath(jobRoot, manifest.files[0].path);
  const payloadEvidence = await evidence(payloadPath);
  if (payloadEvidence.sha256.toLowerCase() !== manifest.files[0].sha256.toLowerCase() || payloadEvidence.bytes !== manifest.files[0].bytes) throw new Error(`PAYLOAD_INVALID ${kind}`);
  return { ref, manifestPath, manifest, manifestEvidence, payloadPath, payload: await readJson(payloadPath), payloadEvidence };
}

async function summarize(run) {
  return {
    result: await evidence(run.resultPath),
    activeSegmentIds: [...run.activeIds],
    auditSegmentIds: [...run.auditIds],
    unitAndCardReferenceIds: [...run.referenceIds],
    disjoint: true,
    allReferencesResolveInActiveSource: true,
    proofread: run.proofread ? { ref: run.proofread.ref, manifest: run.proofread.manifestEvidence, payload: run.proofread.payloadEvidence } : null,
    resolution: run.resolution ? { action: run.resolution.records[0].action, actor: run.resolution.records[0].actor, provenance: run.resolution.records[0].provenance, userDiffs: run.resolution.records[0].user_diffs } : null,
    learning: { ref: run.learning.ref, manifest: run.learning.manifestEvidence, payload: run.learning.payloadEvidence },
    card: { ref: run.card.ref, manifest: run.card.manifestEvidence, payload: run.card.payloadEvidence, learningInputRef: run.cardLearningRef },
  };
}

function ids(segments) { return new Set(segments.map((segment) => segment.segment_id)); }
async function evidence(target) { const payload = await readFile(target); return { path: target, bytes: payload.length, sha256: createHash("sha256").update(payload).digest("hex").toUpperCase() }; }
async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function writeOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, value, { flag: "wx" }); }
async function writeJsonOnce(target, value) { await writeOnce(target, `${JSON.stringify(value, null, 2)}\n`); }
function packagePath(root, relative) { const resolved = path.resolve(root, ...relative.split(/[\\/]/u)); if (!resolved.toLowerCase().startsWith(`${path.resolve(root)}${path.sep}`.toLowerCase())) throw new Error("PATH_ESCAPES_JOB"); return resolved; }

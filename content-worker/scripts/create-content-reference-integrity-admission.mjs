import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const projectRoot = path.dirname(workerRoot);
const jobRoot = path.join(projectRoot, "canary", "sealed-live-ocr-anki-v1.2.24", "20260901T010329Z", "jobs", "live-ocr-anki-v1224-20260901010330339");
const oldAttempt = path.join(jobRoot, "runs", "run-20260901010500354-e936aab2e32f48af8f3ed04cf640ce47", "stages", "content.ocr-human-resolution", "attempts", "attempt-20260901010500544-29abd69ff0c74071ae94269a74a40041");
const newAttempt = path.join(jobRoot, "runs", "run-content-ocr-human-20260901T012159Z", "stages", "content.understand-and-card", "attempts", "attempt-content-ocr-human-accept_original-replay-0001");
const bindingFailurePath = path.join(jobRoot, "runs", "run-20260901010500354-e936aab2e32f48af8f3ed04cf640ce47", "events", "000008-stage-binding-failed.json");
const sourceProofreadManifestPath = path.join(jobRoot, "runs", "run-20260901010336990", "stages", "content.understand-and-card", "attempts", "attempt-20260901010359993-52f4f41c07fa43af8aa08d6f798e8ac1", "outputs", "ocr-proofread-bundle.artifact.v1.json");
const releaseRoot = path.join(workerRoot, "releases", "content.deepseek-0.9.2-win-x64-formal-20260901T012100Z");
const releaseManifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const formalReceiptPath = path.join(workerRoot, "canary", "formal-release", path.basename(releaseRoot), "20260901T012120898Z-settings-snapshot-replay", "formal-release-offline-receipt.v1.json");
const testReceiptPath = path.join(workerRoot, "canary", "ocr-human-resolution-tests", "20260901T012248Z", "receipt.v1.json");

const oldBundle = await inspectAttempt(oldAttempt, "0.9.1");
const newBundle = await inspectAttempt(newAttempt, "0.9.2");
const failure = await readJson(bindingFailurePath);
if (failure.payload?.errorCode !== "binding.content_reference_missing" || !failure.payload?.message?.includes("pseg-d46220ad54e66e4c")) throw new Error("OLD_BINDING_FAILURE_NOT_FOUND");
if (oldBundle.missing.length === 0 || !oldBundle.missing.includes("ocr-0001|pseg-d46220ad54e66e4c")) throw new Error("OLD_DEFECT_NOT_REPRODUCED");
if (newBundle.missing.length !== 0) throw new Error(`NEW_REFERENCE_MISSING ${newBundle.missing.join(",")}`);
if (newBundle.cardLearningRef.artifactRef !== newBundle.learningRef.artifactRef || newBundle.cardLearningRef.expectedSha256 !== newBundle.learningRef.expectedSha256) throw new Error("NEW_CARD_LEARNING_BINDING_INVALID");

const releaseManifest = await readJson(releaseManifestPath);
const formalReceipt = await readJson(formalReceiptPath);
const testReceipt = await readJson(testReceiptPath);
if (releaseManifest.worker?.workerVersion !== "0.9.2" || formalReceipt.status !== "PASS" || testReceipt.tests !== 87 || testReceipt.pass !== 87 || testReceipt.fail !== 0) throw new Error("QUALIFICATION_INVALID");

const sourceProofreadManifest = await readJson(sourceProofreadManifestPath);
const sourceProofreadPayloadPath = packagePath(jobRoot, sourceProofreadManifest.files[0].path);
const sourceProofreadPayload = await readJson(sourceProofreadPayloadPath);
const newResolution = await readJson(path.join(newAttempt, "outputs", "ocr-human-resolution-bundle.schema-v1.json"));
if (newResolution.records[0].action !== "accept_original" || newResolution.records[0].resolved_text !== sourceProofreadPayload.items[0].proofread_text) throw new Error("ACCEPT_ORIGINAL_CHANGED_TEXT");

const readablePath = path.join(workerRoot, "acceptance", "content-card-reference-integrity-0.9.2-20260901T012500Z.md");
const firstCard = newBundle.card.cards[0];
const readable = [
  "# Content 0.9.2 卡片来源引用一致性验收",
  "",
  "## 旧 0.9.1 失败事实",
  "",
  `- Core 诊断：\`${failure.payload.errorCode}\` — ${failure.payload.message}`,
  `- LearningBundle 活动 segment：${oldBundle.learningSegmentIds.map((id) => `\`${id}\``).join("、")}`,
  `- CardBundle 引用：${oldBundle.cardSegmentIds.map((id) => `\`${id}\``).join("、")}`,
  "- 三个 Content artifact/manifest 均合法，但组合内部不一致；Core 在 Anki worker 启动前拒绝是正确行为。",
  "",
  "## 0.9.2 修复与重跑",
  "",
  "- ocr-proofread-bundle 继续保存原始 OCR segment；没有改写历史工件。",
  "- accept_original 后，LearningBundle 顶层 source_segments 使用已接受的 pseg；user_correction 使用 hseg。",
  "- Content 新增卡片来源引用门禁：任一 card source_segment_id 无法在同一 LearningBundle item 解析即 fail-closed。",
  `- 新 LearningBundle 活动 segment：${newBundle.learningSegmentIds.map((id) => `\`${id}\``).join("、")}`,
  `- 新 CardBundle 引用：${newBundle.cardSegmentIds.map((id) => `\`${id}\``).join("、")}`,
  `- 卡片：Front = ${firstCard.front_blocks[0].text}；Back = ${firstCard.back_blocks[0].text}。`,
  `- 原 OCR / accept_original 最终文本：${newResolution.records[0].resolved_text}`,
  "- provider=replay；OCR rerun=0；DeepSeek live calls=0；exporter calls=0。",
  "",
  "## Core 精确消费规则",
  "",
  `- CardBundle：\`${newBundle.cardRef.artifactRef}\`，manifest SHA-256 \`${newBundle.cardRef.expectedSha256}\`。`,
  `- LearningBundle：\`${newBundle.learningRef.artifactRef}\`，manifest SHA-256 \`${newBundle.learningRef.expectedSha256}\`。`,
  "- 必须消费上述同一 Content attempt 的成对工件；ocr-human-resolution-bundle 仅为审计，不代替内容/卡片工件。",
  "",
].join("\n");
await writeOnce(readablePath, readable);

const receiptPath = path.join(workerRoot, "receipts", "content-card-reference-integrity-0.9.2-admission-20260901T012500Z.json");
const receipt = {
  schemaVersion: "content-card-reference-integrity-admission/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: "0.9.2" },
  diagnosis: {
    owner: "content",
    coreBehavior: "correct-fail-closed-before-exporter",
    failure: await evidence(bindingFailurePath),
    code: failure.payload.errorCode,
    oldAttempt: await summarize(oldBundle),
  },
  fix: {
    sourceNeutral: true,
    learningBundleUsesResolvedWorkingSegments: true,
    proofreadAuditRetainsOriginalSegments: true,
    contentGateRejectsMissingCardSourceReferences: true,
    newAttempt: await summarize(newBundle),
  },
  frozenSource: {
    proofreadManifest: await evidence(sourceProofreadManifestPath),
    proofreadPayload: await evidence(sourceProofreadPayloadPath),
    humanAction: "accept_original",
    textChanged: false,
  },
  qualification: {
    fullTests: await evidence(testReceiptPath),
    summary: { tests: 87, pass: 87, fail: 0 },
    releaseManifest: await evidence(releaseManifestPath),
    payloadTreeSha256: releaseManifest.payloadTreeSha256,
    exe: await evidence(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe")),
    formalOfflineReceipt: await evidence(formalReceiptPath),
  },
  effects: { deepSeekLiveCalls: 0, minerUReruns: 0, exporterCalls: 0, destinationWrites: 0 },
  readable: await evidence(readablePath),
  completedAt: new Date().toISOString(),
};
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ status: "PASS", receipt: await evidence(receiptPath), readable: await evidence(readablePath), releaseRoot, coreRefs: { learning: newBundle.learningRef, card: newBundle.cardRef } })}\n`);

async function inspectAttempt(attemptRoot, expectedVersion) {
  const resultPath = path.join(attemptRoot, "result.json");
  const result = await readJson(resultPath);
  if (result.status !== "completed" || result.worker?.id !== "content.deepseek" || result.worker?.version !== expectedVersion) throw new Error(`RESULT_INVALID ${attemptRoot}`);
  const learningRef = result.outputs.find((entry) => entry.kind === "learning-bundle");
  const cardRef = result.outputs.find((entry) => entry.kind === "card-bundle");
  if (!learningRef || !cardRef) throw new Error(`OUTPUT_MISSING ${attemptRoot}`);
  const learningManifestPath = packagePath(jobRoot, learningRef.manifestPath);
  const cardManifestPath = packagePath(jobRoot, cardRef.manifestPath);
  const learningManifest = await readJson(learningManifestPath);
  const cardManifest = await readJson(cardManifestPath);
  await verifyManifest(learningRef, learningManifestPath, learningManifest);
  await verifyManifest(cardRef, cardManifestPath, cardManifest);
  const learningPath = packagePath(jobRoot, learningManifest.files[0].path);
  const cardPath = packagePath(jobRoot, cardManifest.files[0].path);
  const learning = await readJson(learningPath);
  const card = await readJson(cardPath);
  const active = new Set(learning.items.flatMap((item) => item.source_segments.map((segment) => `${item.item_id}|${segment.segment_id}`)));
  const requested = card.cards.flatMap((entry) => entry.source_segment_ids.map((segmentId) => `${entry.item_id}|${segmentId}`));
  return {
    attemptRoot, resultPath, result, learningRef, cardRef, learningManifestPath, cardManifestPath, learningPath, cardPath, learning, card,
    cardLearningRef: cardManifest.inputRefs.find((entry) => entry.kind === "learning-bundle"),
    learningSegmentIds: [...active].map((entry) => entry.split("|")[1]),
    cardSegmentIds: requested.map((entry) => entry.split("|")[1]),
    missing: requested.filter((entry) => !active.has(entry)),
  };
}

async function verifyManifest(ref, manifestPath, manifest) {
  const manifestDigest = await evidence(manifestPath);
  if (manifestDigest.sha256.toLowerCase() !== ref.expectedSha256.toLowerCase()) throw new Error(`MANIFEST_SHA_INVALID ${ref.kind}`);
  for (const file of manifest.files) {
    const digest = await evidence(packagePath(jobRoot, file.path));
    if (digest.sha256.toLowerCase() !== file.sha256.toLowerCase() || digest.bytes !== file.bytes) throw new Error(`PAYLOAD_INVALID ${file.path}`);
  }
}

async function summarize(bundle) {
  return {
    result: await evidence(bundle.resultPath),
    producer: bundle.result.worker,
    learning: { ref: bundle.learningRef, manifest: await evidence(bundle.learningManifestPath), payload: await evidence(bundle.learningPath), activeSegmentIds: bundle.learningSegmentIds },
    card: { ref: bundle.cardRef, manifest: await evidence(bundle.cardManifestPath), payload: await evidence(bundle.cardPath), sourceSegmentIds: bundle.cardSegmentIds, learningInputRef: bundle.cardLearningRef },
    missingReferences: bundle.missing,
  };
}

async function evidence(target) { const payload = await readFile(target); return { path: target, bytes: payload.length, sha256: createHash("sha256").update(payload).digest("hex").toUpperCase() }; }
async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function writeOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, value, { flag: "wx" }); }
async function writeJsonOnce(target, value) { await writeOnce(target, `${JSON.stringify(value, null, 2)}\n`); }
function packagePath(root, relative) { const resolved = path.resolve(root, ...relative.split(/[\\/]/u)); if (!resolved.toLowerCase().startsWith(`${path.resolve(root)}${path.sep}`.toLowerCase())) throw new Error("PATH_ESCAPES_JOB"); return resolved; }

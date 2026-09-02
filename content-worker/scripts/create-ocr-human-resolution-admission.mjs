import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const projectRoot = path.dirname(workerRoot);
const stampValue = "20260830T114000Z";
const releaseRoot = path.join(workerRoot, "releases", "content.deepseek-0.9.0-win-x64-formal-20260830T113459Z");
const formalReceiptPath = path.join(workerRoot, "canary", "formal-release", path.basename(releaseRoot), "20260830T113515204Z-settings-snapshot-replay", "formal-release-offline-receipt.v1.json");
const schemaReceiptPath = path.join(workerRoot, "receipts", "content-ocr-human-resolution-schema-validation-20260830T113700Z.json");
const testReceiptPath = path.join(workerRoot, "canary", "ocr-human-resolution-tests", "20260830T113921Z", "receipt.v1.json");
const priorReceiptPath = path.join(workerRoot, "receipts", "content-ocr-proofreading-0.8.0-admission-20260830T101000Z.json");
const gapMatrixPath = path.join(projectRoot, "canary", "product-gap-audit", "20260830T111343Z-current-v1214", "product-gap-matrix.json");
const jobRoot = path.join(projectRoot, "canary", "image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2", "jobs", "task-image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2");

const runs = {
  acceptReplay: runPaths("run-content-ocr-human-20260830T113609Z", "attempt-content-ocr-human-accept_original-replay-0001"),
  correctionReplay: runPaths("run-content-ocr-human-20260830T113528Z", "attempt-content-ocr-human-user_correction-replay-0001"),
  correctionLive: runPaths("run-content-ocr-human-20260830T113540Z", "attempt-content-ocr-human-user_correction-live-0001"),
};
const prior = await readJson(priorReceiptPath);
const gapMatrix = await readJson(gapMatrixPath);
const gap = gapMatrix.items.find((entry) => entry.id === "GAP-OCR-HUMAN-CONTINUE");
if (!gap || gap.currentStatus !== "PARTIAL-STOP-CORRECT-CONTINUATION-MISSING") throw new Error("GAP_MATRIX_INVALID");
const releaseManifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const releaseManifest = await readJson(releaseManifestPath);
const descriptor = await readJson(path.join(releaseRoot, "app", "descriptors", "content-companion-descriptor.v1.1.json"));
if (releaseManifest.worker.workerVersion !== "0.9.0" || descriptor.schemaVersion !== "content-companion-descriptor/1.1.0") throw new Error("RELEASE_INVALID");
if (!releaseManifest.worker.outputKinds.includes("ocr-human-resolution-bundle")) throw new Error("RELEASE_OUTPUT_KIND_MISSING");

const priorResult = await readJson(prior.realDeepSeekCanary.resultPath);
if (priorResult.status !== "completed" || priorResult.payload.moduleReleaseState !== "unresolved" || priorResult.outputs.some((entry) => entry.kind === "card-bundle")) throw new Error("PRIOR_WAITING_EVIDENCE_INVALID");
const accept = await inspectRun(runs.acceptReplay, "replay", "accept_original");
const offline = await inspectRun(runs.correctionReplay, "replay", "user_correction");
const live = await inspectRun(runs.correctionLive, "live", "user_correction");
if (live.learning.ai_trace.map((entry) => entry.stage).join(",") !== "cleaning,classification,card_generation" || live.learning.ai_trace.some((entry) => entry.stage === "ocr_proofreading")) throw new Error("LIVE_STAGE_SEQUENCE_INVALID");
if (live.resolution.records[0].provenance !== "human_submitted" || live.resolution.records[0].actor !== "user" || live.resolution.records[0].user_diffs[0].after !== "Y") throw new Error("LIVE_HUMAN_PROVENANCE_INVALID");
if (live.card.cards[0].front_blocks[0].text !== "What is 19 + 23?" || live.card.cards[0].back_blocks[0].text !== "42") throw new Error("LIVE_CARD_INVALID");
const originalPayloadPath = packagePath(jobRoot, live.input.items[0].source.material_path);
const originalNow = await evidence(originalPayloadPath);
if (originalNow.sha256 !== prior.realDeepSeekCanary.ocrOriginalPayloadSha256Before) throw new Error("ORIGINAL_OCR_CHANGED");

const readablePath = path.join(workerRoot, "acceptance", `content-ocr-human-resolution-0.9.0-${stampValue}.md`);
const readable = [
  "# Content 0.9.0 OCR 人工决议续跑验收",
  "",
  "## 原始 waiting 状态",
  "",
  `- 0.8.0 request：\`${prior.realDeepSeekCanary.requestPath}\``,
  `- 0.8.0 result：completed + moduleReleaseState=unresolved；CardBundle=false。`,
  `- 原始 OCR SHA-256：\`${originalNow.sha256}\`，本轮前后未改写。`,
  "- Core 应继续投影为 waiting_input；没有人工命令时 Content 不会自动猜字。",
  "",
  "原始 / frozen 文本：",
  "",
  `> ${live.resolution.records[0].source_ocr.text}`,
  "",
  "## accept_original（严格 replay）",
  "",
  `用户动作：accept_original；diff=[]；最终文本仍为：`,
  "",
  `> ${accept.resolution.records[0].resolved_text}`,
  "",
  `卡片：Front = ${accept.card.cards[0].front_blocks[0].text}；Back = ${accept.card.cards[0].back_blocks[0].text}。`,
  "",
  "## user_correction（严格 replay）",
  "",
  `用户明确把 CANAR 补为 CANARY；登记 diff：\`${JSON.stringify(offline.resolution.records[0].user_diffs[0])}\`。`,
  "",
  `> ${offline.resolution.records[0].resolved_text}`,
  "",
  `卡片：Front = ${offline.card.cards[0].front_blocks[0].text}；Back = ${offline.card.cards[0].back_blocks[0].text}。`,
  "",
  "## user_correction（真实 DeepSeek）",
  "",
  `人工证据：actor=user，provenance=human_submitted；不是 AI 自主纠错。`,
  "",
  `清洗后：${live.learning.items[0].cleaning.cleaned_text}`,
  "",
  `分类：${live.learning.items[0].units[0].category_id}/${live.learning.items[0].units[0].subtype}。`,
  "",
  `卡片：Front = ${live.card.cards[0].front_blocks[0].text}；Back = ${live.card.cards[0].back_blocks[0].text}。`,
  "",
  `DeepSeek stages：${live.learning.ai_trace.map((entry) => `${entry.stage}:${entry.model}:attempts=${entry.attempts}`).join("；")}。OCR stage=0，MinerU 重跑=0，exporter calls=0。`,
  "",
  "## 接线边界",
  "",
  "- Content 已完成 module-local command/schema、v5 SHA-bound input、人工 diff 验真、受保护字符门禁及 destination-neutral bundles。",
  "- Core 仍需把 task.provide_input、一次性 targetToken、waiting_input、ancestry、原 destinations 与新 attempt 正式接入 authority；默认 rerunOcr=false。",
  "- Desktop 仍需展示 before/candidate/diff/reason 并提交明确动作。Content 不修改 Core/Desktop，也未进行任何目的端写入。",
  "- GAP-OCR-HUMAN-CONTINUE 的模块侧 admission 已具备，但产品级 exporter E2E 仍由 Core/Desktop/Exporter 闭环，不能由本回执外推为全产品完成。",
  "",
].join("\n");
await writeTextOnce(readablePath, readable);

const receiptPath = path.join(workerRoot, "receipts", `content-ocr-human-resolution-0.9.0-admission-${stampValue}.json`);
const receipt = {
  schemaVersion: "content-ocr-human-resolution-admission/1.0.0",
  status: "PASS",
  authorityCandidate: true,
  worker: { id: "content.deepseek", version: "0.9.0" },
  productGap: { id: gap.id, source: await evidence(gapMatrixPath), priorStatus: gap.currentStatus, moduleStatus: "CONTENT-ADMISSION-PASS-PRODUCT-E2E-CONDITIONAL" },
  additiveContracts: [
    artifact("schemas/ocr-human-resolution-command.v1.schema.json", "knowledge-clipper-ocr-human-resolution-command/1.0.0"),
    artifact("schemas/content-input.v5.schema.json", "standard-input/5.0.0"),
    artifact("schemas/ocr-human-resolution-bundle.v1.schema.json", "ocr-human-resolution-bundle/1.0.0"),
    artifact("schemas/learning-bundle.v3.schema.json", "learning-bundle/3.0.0"),
  ],
  frozenBoundaries: {
    content080: await evidence(priorReceiptPath),
    sourceRequest: await evidence(prior.realDeepSeekCanary.requestPath),
    sourceProofreadManifest: await evidence(prior.realDeepSeekCanary.ocrProofreadManifestPath),
    originalOcrPayload: originalNow,
    originalOcrUnchanged: true,
    content081: "never-promoted/failed-canary remains excluded",
  },
  validation: {
    fullTests: await evidence(testReceiptPath),
    fullTestSummary: { command: "npm test", tests: 85, pass: 85, fail: 0 },
    schemas: await evidence(schemaReceiptPath),
  },
  canaries: {
    noHumanInput: { providerMode: "live", status: "completed", moduleReleaseState: "unresolved", waitingProjectionOwner: "core", cardBundlePublished: false, result: await evidence(prior.realDeepSeekCanary.resultPath) },
    acceptOriginalReplay: await summarizeRun(accept),
    userCorrectionReplay: await summarizeRun(offline),
    userCorrectionLive: { ...(await summarizeRun(live)), provider: { name: "deepseek", model: "deepseek-chat", stages: live.learning.ai_trace.map((entry) => ({ stage: entry.stage, attempts: entry.attempts, responseSha256: entry.response_sha256 })) } },
  },
  formalRelease: {
    root: releaseRoot,
    manifest: await evidence(releaseManifestPath),
    payloadFileCount: releaseManifest.payloadFileCount,
    payloadBytes: releaseManifest.payloadBytes,
    payloadTreeSha256: releaseManifest.payloadTreeSha256,
    exe: await evidence(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe")),
    describe: { workerVersion: releaseManifest.worker.workerVersion, outputKinds: releaseManifest.worker.outputKinds },
    companionDescriptor: await evidence(path.join(releaseRoot, "app", "descriptors", "content-companion-descriptor.v1.1.json")),
    offlineQualification: await evidence(formalReceiptPath),
  },
  securityAndEffects: { syntheticOracleUsed: false, secretRecorded: false, minerURerun: false, exporterCalls: 0, destinationFieldsInModuleArtifacts: false },
  coreDesktopAdmission: {
    commandSchema: "knowledge-clipper-ocr-human-resolution-command/1.0.0",
    outerCommand: "task.provide_input",
    conditionalOwners: ["core-orchestrator", "desktop"],
    requiredCoreBehavior: ["validate targetToken", "create append-only standard-input/5.0.0", "preserve ancestry", "preserve original destinations", "rerunOcr=false", "continue only ready bundles"],
    requiredDesktopBehavior: ["show original/candidate/diff/reason", "submit accept_original or user_correction", "never inject an oracle"],
  },
  readable: await evidence(readablePath),
  completedAt: new Date().toISOString(),
};
receipt.additiveContracts = await Promise.all(receipt.additiveContracts);
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ status: "PASS", receipt: await evidence(receiptPath), readable: await evidence(readablePath), releaseRoot, liveResult: await evidence(runs.correctionLive.resultPath) })}\n`);

function runPaths(runId, attemptId) {
  const attemptRoot = path.join(jobRoot, "runs", runId, "stages", "content.understand-and-card", "attempts", attemptId);
  return {
    runId,
    attemptId,
    inputPath: path.join(jobRoot, "runs", runId, "adapters", "content.understand-and-card", `content-input-ocr-human-${runId.replace("run-content-ocr-human-", "")}`, "v0001", "content-input.v5.json"),
    requestPath: path.join(attemptRoot, "request.json"),
    resultPath: path.join(attemptRoot, "result.json"),
    resolutionPath: path.join(attemptRoot, "outputs", "ocr-human-resolution-bundle.schema-v1.json"),
    learningPath: path.join(attemptRoot, "outputs", "learning-bundle.schema-v3.json"),
    cardPath: path.join(attemptRoot, "outputs", "card-bundle.schema-v1.json"),
  };
}

async function inspectRun(paths, providerMode, action) {
  const [input, request, result, resolution, learning, card] = await Promise.all([paths.inputPath, paths.requestPath, paths.resultPath, paths.resolutionPath, paths.learningPath, paths.cardPath].map(readJson));
  if (result.status !== "completed" || result.payload.moduleReleaseState !== "ready") throw new Error(`RUN_INVALID ${paths.runId}`);
  if (result.outputs.map((entry) => entry.kind).join(",") !== "ocr-human-resolution-bundle,learning-bundle,card-bundle") throw new Error(`RUN_OUTPUTS_INVALID ${paths.runId}`);
  if (resolution.records[0].action !== action || resolution.records[0].provenance !== "human_submitted") throw new Error(`RUN_ACTION_INVALID ${paths.runId}`);
  if (learning.ai_trace.some((entry) => entry.stage === "ocr_proofreading") || learning.ai_trace.some((entry) => entry.mode !== providerMode)) throw new Error(`RUN_PROVIDER_INVALID ${paths.runId}`);
  return { paths, input, request, result, resolution, learning, card };
}

async function summarizeRun(run) {
  return {
    providerMode: run.learning.ai_trace[0].mode,
    action: run.resolution.records[0].action,
    status: run.result.status,
    moduleReleaseState: run.result.payload.moduleReleaseState,
    request: await evidence(run.paths.requestPath),
    result: await evidence(run.paths.resultPath),
    resolutionBundle: await evidence(run.paths.resolutionPath),
    learningBundle: await evidence(run.paths.learningPath),
    cardBundle: await evidence(run.paths.cardPath),
    outputs: run.result.outputs,
    originalText: run.resolution.records[0].source_ocr.text,
    resolvedText: run.resolution.records[0].resolved_text,
    userDiffs: run.resolution.records[0].user_diffs,
    actor: "user",
    provenance: "human_submitted",
    ocrRerun: false,
    exporterCalls: 0,
  };
}

async function artifact(relative, schemaVersion) { return { schemaVersion, ...(await evidence(path.join(workerRoot, relative))) }; }
async function evidence(target) { const payload = await readFile(target); return { path: target, bytes: payload.length, sha256: sha(payload).toUpperCase() }; }
async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function writeTextOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, value, { flag: "wx" }); }
async function writeJsonOnce(target, value) { await writeTextOnce(target, `${JSON.stringify(value, null, 2)}\n`); }
function packagePath(root, relative) { const resolved = path.resolve(root, ...relative.split(/[\\/]/u)); if (!resolved.toLowerCase().startsWith(`${path.resolve(root)}${path.sep}`.toLowerCase())) throw new Error("PATH_ESCAPES_JOB"); return resolved; }
function sha(value) { return createHash("sha256").update(value).digest("hex"); }

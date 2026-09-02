import { spawn } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceRequestPath = absoluteArg("--source-request");
const frozenLearningPath = absoluteArg("--frozen-learning");
const releaseRoot = absoluteArg("--release-root");
const sourceRequest = await readJson(sourceRequestPath);
const frozenLearning = await readJson(frozenLearningPath);
const releaseManifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const releaseManifest = await readJson(releaseManifestPath);
const workerVersion = releaseManifest.worker?.workerVersion;
if (releaseManifest.worker?.workerId !== "content.deepseek" || typeof workerVersion !== "string") throw new Error("RELEASE_INVALID");
const responses = Object.fromEntries(frozenLearning.ai_trace.map((trace) => {
  if (!trace.call_key || trace.response === undefined) throw new Error("FROZEN_TRACE_INVALID");
  return [trace.call_key, trace.response];
}));
if (Object.keys(responses).length === 0) throw new Error("FROZEN_TRACE_EMPTY");

const jobRoot = deriveJobRoot(sourceRequestPath, sourceRequest.attemptPath);
const stampValue = stamp();
const runId = `run-content-real-response-replay-${stampValue}`;
const attemptId = `attempt-content-real-response-replay-${randomUUID().replaceAll("-", "")}`;
const replayRoot = `runs/${runId}/adapters/content.understand-and-card/provider-replay-${stampValue}/v0001`;
const replayPath = `${replayRoot}/provider-replay.v1.json`;
const replayManifestRelative = `${replayRoot}/artifact.json`;
await writeJsonOnce(packagePath(jobRoot, replayPath), { schema_version: "provider-replay/1.0.0", responses });
const replayPayloadEvidence = await evidence(packagePath(jobRoot, replayPath));
const frozenLearningEvidence = await evidence(frozenLearningPath);
const replayManifest = {
  schemaVersion: "1.0.0",
  artifactId: `content-provider-replay-${stampValue}`,
  kind: "content-provider-replay",
  contractVersion: "1.0.0",
  mediaType: "application/json",
  files: [{ path: replayPath, sha256: replayPayloadEvidence.sha256.toLowerCase(), bytes: replayPayloadEvidence.bytes, mediaType: "application/json" }],
  createdAt: new Date().toISOString(),
  producer: { stageId: "content.fixture-replay", workerId: "content.replay.fixture", workerVersion: "1.0.0", attemptId: `fixture-${stampValue}` },
  inputRefs: [],
  dataClass: "public",
  metadata: { entrypoint: replayPath, sourceProviderMode: "live", responseReuseBoundary: "frozen-real-deepseek-ai-trace-response", frozenLearningSha256: frozenLearningEvidence.sha256.toLowerCase() },
};
await writeJsonOnce(packagePath(jobRoot, replayManifestRelative), replayManifest);
const replayManifestEvidence = await evidence(packagePath(jobRoot, replayManifestRelative));

const attemptPath = `runs/${runId}/stages/content.understand-and-card/attempts/${attemptId}`;
const requestPath = packagePath(jobRoot, `${attemptPath}/request.json`);
const request = {
  ...sourceRequest,
  requestId: `request-${randomUUID().replaceAll("-", "")}`,
  operationId: `operation-${randomUUID().replaceAll("-", "")}`,
  runId,
  attemptId,
  idempotencyKey: sha(`${sourceRequest.idempotencyKey}\0real-response-replay\0${stampValue}`).toUpperCase(),
  attemptPath,
  outputPath: `${attemptPath}/outputs`,
  progressPath: `${attemptPath}/progress.ndjson`,
  resultPath: `${attemptPath}/result.json`,
  cancelSignal: { ...sourceRequest.cancelSignal, path: `${attemptPath}/cancel.json` },
  inputRefs: [...sourceRequest.inputRefs, { artifactRef: replayManifest.artifactId, kind: replayManifest.kind, contractVersion: replayManifest.contractVersion, manifestPath: replayManifestRelative, expectedSha256: replayManifestEvidence.sha256.toLowerCase() }],
  configSnapshot: { ...sourceRequest.configSnapshot, promptSet: "open-md-v3" },
  payload: { ...sourceRequest.payload, providerMode: "replay", replayPath },
};
await writeJsonOnce(requestPath, request);
const executable = path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe");
const execution = await run(executable, ["run", "--request", requestPath], releaseRoot);
if (execution.code !== 0) throw new Error(`WORKER_FAILED code=${execution.code} stderr=${execution.stderr}`);
const resultPath = packagePath(jobRoot, request.resultPath);
const result = await readJson(resultPath);
if (result.status !== "completed" || result.worker?.version !== workerVersion) throw new Error("RESULT_INVALID");
const learning = await loadOutput(jobRoot, outputRef(result, "learning-bundle"));
const proofread = await loadOutput(jobRoot, outputRef(result, "ocr-proofread-bundle"));
if (result.outputs.some((entry) => entry.kind === "card-bundle")) throw new Error("CARD_RELEASE_INVALID");
if (learning.payload.release_state !== "unresolved" || learning.payload.unresolved_items.length === 0) throw new Error("UNRESOLVED_INVALID");
const item = learning.payload.items[0];
const candidate = item?.proofreading?.unresolved?.[0];
if (!candidate || !candidate.evidence.includes('"localization":"unique_relocated"')) throw new Error("COORDINATE_RECOVERY_INVALID");
const before = item.proofreading.proofread_segments.map((entry) => entry.text).join("");
if (item.proofreading.proofread_text !== before) throw new Error("OCR_TEXT_MUTATED");

const canaryRoot = path.join(workerRoot, "canary", "ocr-lexical-audit", `${stampValue}-v1228-frozen-real-response-replay`);
await mkdir(canaryRoot, { recursive: true });
const readablePath = path.join(canaryRoot, "readable-before-after.md");
await writeFile(readablePath, [
  "# Frozen real DeepSeek response replay",
  "",
  `- worker: content.deepseek@${workerVersion}`,
  "- provider mode: replay of the previous real DeepSeek response",
  "- OCR rerun/API/exporter/destination write: none",
  "- release state: unresolved",
  "",
  "## Immutable OCR text",
  "",
  "```text",
  before,
  "```",
  "",
  "## Human-review candidate",
  "",
  "```json",
  JSON.stringify(candidate, null, 2),
  "```",
  "",
].join("\n"), { flag: "wx" });
const receiptPath = path.join(canaryRoot, "receipt.v1.json");
const receipt = {
  schemaVersion: "content-ocr-real-response-replay-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: workerVersion },
  providerMode: "replay",
  responseBoundary: "frozen real DeepSeek response from failed 0.9.4 Content-only attempt",
  source: { request: await evidence(sourceRequestPath), frozenLearning: frozenLearningEvidence },
  replay: { manifest: replayManifestEvidence, payload: replayPayloadEvidence },
  attempt: { request: await evidence(requestPath), result: await evidence(resultPath) },
  outputs: { proofread: proofread.evidence, learning: learning.evidence, cardBundlePublished: false },
  observed: { releaseState: "unresolved", originalOcrPreserved: true, coordinateLocalization: "unique_relocated", unresolvedCandidate: { segment_index: candidate.segment_index, start: candidate.start, end: candidate.end, exact_text: candidate.exact_text, reason: candidate.reason } },
  effects: { deepSeekCalls: 0, minerUCalls: 0, exporterCalls: 0, destinationWrites: 0, oldAttemptMutated: false, oldDestinationObjectMutated: false },
  release: { manifest: await evidence(releaseManifestPath), payloadTreeSha256: releaseManifest.payloadTreeSha256, exeSha256: releaseManifest.files.find((entry) => entry.path === "KnowledgeClipper.Content.Worker.exe")?.sha256 },
  readable: await evidence(readablePath),
  completedAt: new Date().toISOString(),
};
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ status: "PASS", root: canaryRoot, request: await evidence(requestPath), result: await evidence(resultPath), proofread: proofread.evidence, learning: learning.evidence, readable: await evidence(readablePath), receipt: await evidence(receiptPath), observed: receipt.observed, effects: receipt.effects })}\n`);

function outputRef(resultValue, kind) { const ref = resultValue.outputs.find((entry) => entry.kind === kind); if (!ref) throw new Error(`OUTPUT_MISSING ${kind}`); return ref; }
async function loadOutput(root, ref) { const manifestPath = packagePath(root, ref.manifestPath); const manifestEvidence = await evidence(manifestPath); if (manifestEvidence.sha256.toLowerCase() !== ref.expectedSha256.toLowerCase()) throw new Error(`MANIFEST_INVALID ${ref.kind}`); const manifest = await readJson(manifestPath); const file = manifest.files[0]; const payloadPath = packagePath(root, file.path); const payloadEvidence = await evidence(payloadPath); if (payloadEvidence.sha256.toLowerCase() !== file.sha256.toLowerCase() || payloadEvidence.bytes !== file.bytes) throw new Error(`PAYLOAD_INVALID ${ref.kind}`); return { payload: await readJson(payloadPath), evidence: { ref, manifest: manifestEvidence, payload: payloadEvidence } }; }
function run(command, args, cwd) { return new Promise((resolve, reject) => { const child = spawn(command, args, { cwd, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] }); let stdout = ""; let stderr = ""; child.stdout.on("data", (chunk) => { stdout += chunk; }); child.stderr.on("data", (chunk) => { stderr += chunk; }); child.on("error", reject); child.on("close", (code) => resolve({ code, stdout, stderr })); }); }
async function evidence(target) { const payload = await readFile(target); return { path: target, bytes: payload.length, sha256: sha(payload).toUpperCase() }; }
async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function writeJsonOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" }); }
function deriveJobRoot(requestPathValue, attemptRelative) { const normalizedRequest = path.resolve(requestPathValue); const suffix = path.join(...attemptRelative.split(/[\\/]/u), "request.json"); if (!normalizedRequest.toLowerCase().endsWith(suffix.toLowerCase())) throw new Error("PATH_INVALID source request"); return normalizedRequest.slice(0, normalizedRequest.length - suffix.length).replace(/[\\/]$/u, ""); }
function packagePath(root, relative) { const resolved = path.resolve(root, ...relative.split(/[\\/]/u)); if (!resolved.toLowerCase().startsWith(`${path.resolve(root)}${path.sep}`.toLowerCase())) throw new Error("PATH_ESCAPES_JOB"); return resolved; }
function arg(name) { const index = process.argv.indexOf(name); if (index < 0 || !process.argv[index + 1]) throw new Error(`CLI_INVALID ${name}`); return process.argv[index + 1]; }
function absoluteArg(name) { return path.resolve(arg(name)); }
function sha(value) { return createHash("sha256").update(value).digest("hex"); }
function stamp() { return new Date().toISOString().replace(/[-:]/gu, "").replace(/\.\d{3}Z$/u, "Z"); }

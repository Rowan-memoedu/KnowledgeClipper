import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const sourceRequestPath = absoluteArg("--source-request");
const releaseRoot = absoluteArg("--release-root");
const sourceRequest = await readJson(sourceRequestPath);
const jobRoot = deriveJobRoot(sourceRequestPath, sourceRequest.attemptPath);
const sourceResult = await readJson(packagePath(jobRoot, sourceRequest.resultPath));
const sourceLearningRef = sourceResult.outputs.find((entry) => entry.kind === "learning-bundle");
if (!sourceLearningRef) throw new Error("SOURCE_INVALID missing LearningBundle");
const sourceLearningManifestPath = packagePath(jobRoot, sourceLearningRef.manifestPath);
const sourceLearningManifest = await readJson(sourceLearningManifestPath);
const sourceLearningPath = packagePath(jobRoot, sourceLearningManifest.files[0].path);
const sourceLearning = await readJson(sourceLearningPath);
const responses = Object.fromEntries(sourceLearning.ai_trace.map((trace) => {
  if (!trace.call_key || trace.response === undefined) throw new Error("SOURCE_INVALID trace response unavailable");
  return [trace.call_key, trace.response];
}));
if (Object.keys(responses).length === 0) throw new Error("SOURCE_INVALID no replayable traces");

const stampValue = stamp();
const runId = `run-content-frozen-replay-${stampValue}`;
const attemptId = `attempt-content-frozen-replay-0001`;
const replayRoot = `runs/${runId}/adapters/content.understand-and-card/provider-replay-${stampValue}/v0001`;
const replayPath = `${replayRoot}/provider-replay.v1.json`;
const replayManifestRelative = `${replayRoot}/artifact.json`;
const replay = { schema_version: "provider-replay/1.0.0", responses };
await writeJsonOnce(packagePath(jobRoot, replayPath), replay);
const replayDigest = await evidence(packagePath(jobRoot, replayPath));
const sourceLearningManifestDigest = await evidence(sourceLearningManifestPath);
const replayManifest = {
  schemaVersion: "1.0.0",
  artifactId: `content-provider-replay-${stampValue}`,
  kind: "content-provider-replay",
  contractVersion: "1.0.0",
  mediaType: "application/json",
  files: [{ path: replayPath, sha256: replayDigest.sha256.toLowerCase(), bytes: replayDigest.bytes, mediaType: "application/json" }],
  createdAt: new Date().toISOString(),
  producer: { stageId: "content.fixture-replay", workerId: "content.replay.fixture", workerVersion: "1.0.0", attemptId: `fixture-${stampValue}` },
  inputRefs: [{ ...sourceLearningRef, expectedSha256: sourceLearningManifestDigest.sha256.toLowerCase() }],
  dataClass: "public",
  metadata: { entrypoint: replayPath, sourceAttemptId: sourceRequest.attemptId, sourceProviderMode: sourceRequest.payload.providerMode, responseReuseBoundary: "frozen-ai-trace-response" },
};
const replayManifestPath = packagePath(jobRoot, replayManifestRelative);
await writeJsonOnce(replayManifestPath, replayManifest);
const replayManifestDigest = await evidence(replayManifestPath);

const attemptPath = `runs/${runId}/stages/content.understand-and-card/attempts/${attemptId}`;
const requestPath = packagePath(jobRoot, `${attemptPath}/request.json`);
const request = {
  ...sourceRequest,
  requestId: `request-${attemptId}`,
  operationId: `operation-${attemptId}`,
  runId,
  attemptId,
  idempotencyKey: sha(`${sourceRequest.idempotencyKey}\0${stampValue}`).toUpperCase(),
  attemptPath,
  outputPath: `${attemptPath}/outputs`,
  progressPath: `${attemptPath}/progress.ndjson`,
  resultPath: `${attemptPath}/result.json`,
  cancelSignal: { ...sourceRequest.cancelSignal, path: `${attemptPath}/cancel.json` },
  inputRefs: [...sourceRequest.inputRefs, {
    artifactRef: replayManifest.artifactId,
    kind: replayManifest.kind,
    contractVersion: replayManifest.contractVersion,
    manifestPath: replayManifestRelative,
    expectedSha256: replayManifestDigest.sha256.toLowerCase(),
  }],
  payload: { ...sourceRequest.payload, providerMode: "replay", replayPath },
};
await writeJsonOnce(requestPath, request);
const executable = path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe");
const execution = await run(executable, ["run", "--request", requestPath], releaseRoot);
if (execution.code !== 0) throw new Error(`WORKER_FAILED code=${execution.code} stderr=${execution.stderr}`);

const resultPath = packagePath(jobRoot, request.resultPath);
const result = await readJson(resultPath);
if (result.status !== "completed" || result.worker?.id !== "content.deepseek" || result.worker?.version !== "0.9.3") throw new Error("RESULT_INVALID");
const learningRef = result.outputs.find((entry) => entry.kind === "learning-bundle");
const cardRef = result.outputs.find((entry) => entry.kind === "card-bundle");
const proofreadRef = result.outputs.find((entry) => entry.kind === "ocr-proofread-bundle");
if (!learningRef || !cardRef || !proofreadRef) throw new Error("OUTPUT_INVALID");
const learningManifestPath = packagePath(jobRoot, learningRef.manifestPath);
const cardManifestPath = packagePath(jobRoot, cardRef.manifestPath);
const proofreadManifestPath = packagePath(jobRoot, proofreadRef.manifestPath);
const learningManifest = await readJson(learningManifestPath);
const cardManifest = await readJson(cardManifestPath);
const proofreadManifest = await readJson(proofreadManifestPath);
await verifyOutput(learningRef, learningManifestPath, learningManifest);
await verifyOutput(cardRef, cardManifestPath, cardManifest);
await verifyOutput(proofreadRef, proofreadManifestPath, proofreadManifest);
const learningPath = packagePath(jobRoot, learningManifest.files[0].path);
const cardPath = packagePath(jobRoot, cardManifest.files[0].path);
const proofreadPath = packagePath(jobRoot, proofreadManifest.files[0].path);
const learning = await readJson(learningPath);
const card = await readJson(cardPath);
const proofread = await readJson(proofreadPath);
const activeByItem = new Map(learning.items.map((item) => [item.item_id, new Set(item.source_segments.map((segment) => segment.segment_id))]));
for (const item of learning.items) {
  const active = activeByItem.get(item.item_id);
  const auditIds = new Set(item.proofreading?.proofread_segments.map((segment) => segment.segment_id) ?? []);
  if (item.source_segments.some((segment) => auditIds.has(segment.segment_id))) throw new Error("IDENTITY_COLLISION");
  if (!item.source_segments.every((segment) => segment.segment_id.startsWith("rseg-"))) throw new Error("ACTIVE_IDENTITY_INVALID");
  for (const unit of item.units) if (unit.source_segment_ids.some((segmentId) => !active.has(segmentId))) throw new Error("UNIT_REFERENCE_INVALID");
}
for (const entry of card.cards) if (entry.source_segment_ids.some((segmentId) => !activeByItem.get(entry.item_id)?.has(segmentId))) throw new Error("CARD_REFERENCE_INVALID");
const cardLearningRef = cardManifest.inputRefs.find((entry) => entry.kind === "learning-bundle");
if (!cardLearningRef || cardLearningRef.artifactRef !== learningRef.artifactRef || cardLearningRef.manifestPath !== learningRef.manifestPath || cardLearningRef.expectedSha256 !== learningRef.expectedSha256) throw new Error("CARD_LEARNING_BINDING_INVALID");

process.stdout.write(`${JSON.stringify({
  status: "PASS",
  providerMode: "replay",
  responseReuseBoundary: "frozen-ai-trace-response",
  source: { request: await evidence(sourceRequestPath), learningManifest: sourceLearningManifestDigest, learningPayload: await evidence(sourceLearningPath) },
  replay: { manifest: replayManifestDigest, payload: replayDigest },
  request: await evidence(requestPath),
  result: await evidence(resultPath),
  outputs: {
    proofread: { ref: proofreadRef, manifest: await evidence(proofreadManifestPath), payload: await evidence(proofreadPath) },
    learning: { ref: learningRef, manifest: await evidence(learningManifestPath), payload: await evidence(learningPath), activeSegmentIds: learning.items.flatMap((item) => item.source_segments.map((segment) => segment.segment_id)), auditSegmentIds: learning.items.flatMap((item) => item.proofreading?.proofread_segments.map((segment) => segment.segment_id) ?? []) },
    card: { ref: cardRef, manifest: await evidence(cardManifestPath), payload: await evidence(cardPath), sourceSegmentIds: card.cards.flatMap((entry) => entry.source_segment_ids), learningInputRef: cardLearningRef },
  },
  sourceProofreadSegmentIds: proofread.items.flatMap((item) => item.proofread_segments.map((segment) => segment.segment_id)),
  externalCalls: { deepSeek: 0, minerU: 0, exporters: 0, destinationWrites: 0 },
})}\n`);

async function verifyOutput(ref, manifestPath, manifest) {
  const manifestDigest = await evidence(manifestPath);
  if (manifestDigest.sha256.toLowerCase() !== ref.expectedSha256.toLowerCase()) throw new Error(`MANIFEST_INVALID ${ref.kind}`);
  for (const file of manifest.files) {
    const digest = await evidence(packagePath(jobRoot, file.path));
    if (digest.sha256.toLowerCase() !== file.sha256.toLowerCase() || digest.bytes !== file.bytes) throw new Error(`PAYLOAD_INVALID ${file.path}`);
  }
}
function run(command, args, cwd) { return new Promise((resolve, reject) => { const child = spawn(command, args, { cwd, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] }); let stdout = ""; let stderr = ""; child.stdout.on("data", (chunk) => { stdout += chunk; }); child.stderr.on("data", (chunk) => { stderr += chunk; }); child.on("error", reject); child.on("close", (code) => resolve({ code, stdout, stderr })); }); }
async function evidence(target) { const payload = await readFile(target); return { path: target, bytes: payload.length, sha256: sha(payload).toUpperCase() }; }
async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function writeJsonOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" }); }
function deriveJobRoot(requestPath, attemptRelative) { const normalizedRequest = path.resolve(requestPath); const suffix = path.join(...attemptRelative.split(/[\\/]/u), "request.json"); if (!normalizedRequest.toLowerCase().endsWith(suffix.toLowerCase())) throw new Error("PATH_INVALID source request"); return normalizedRequest.slice(0, normalizedRequest.length - suffix.length).replace(/[\\/]$/u, ""); }
function packagePath(root, relative) { const resolved = path.resolve(root, ...relative.split(/[\\/]/u)); if (!resolved.toLowerCase().startsWith(`${path.resolve(root)}${path.sep}`.toLowerCase())) throw new Error("PATH_ESCAPES_JOB"); return resolved; }
function arg(name) { const index = process.argv.indexOf(name); if (index < 0 || !process.argv[index + 1]) throw new Error(`CLI_INVALID ${name}`); return process.argv[index + 1]; }
function absoluteArg(name) { const value = path.resolve(arg(name)); if (!path.isAbsolute(value)) throw new Error(`CLI_INVALID ${name}`); return value; }
function sha(value) { return createHash("sha256").update(value).digest("hex"); }
function stamp() { return new Date().toISOString().replace(/[-:]/gu, "").replace(/\.\d{3}Z$/u, "Z"); }

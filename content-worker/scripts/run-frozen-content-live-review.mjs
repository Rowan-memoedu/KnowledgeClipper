import { spawn } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceRequestPath = absoluteArg("--source-request");
const releaseRoot = absoluteArg("--release-root");
const sourceRequest = await readJson(sourceRequestPath);
const releaseManifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const releaseManifest = await readJson(releaseManifestPath);
const workerVersion = releaseManifest.worker?.workerVersion;
if (releaseManifest.worker?.workerId !== "content.deepseek" || typeof workerVersion !== "string") throw new Error("RELEASE_INVALID");

const jobRoot = deriveJobRoot(sourceRequestPath, sourceRequest.attemptPath);
const stampValue = stamp();
const runId = `run-content-live-review-${stampValue}`;
const attemptId = `attempt-content-live-review-${randomUUID().replaceAll("-", "")}`;
const attemptPath = `runs/${runId}/stages/content.understand-and-card/attempts/${attemptId}`;
const requestPath = packagePath(jobRoot, `${attemptPath}/request.json`);
const request = {
  ...sourceRequest,
  requestId: `request-${randomUUID().replaceAll("-", "")}`,
  operationId: `operation-${randomUUID().replaceAll("-", "")}`,
  runId,
  attemptId,
  idempotencyKey: sha(`${sourceRequest.idempotencyKey}\0live-content-review\0${stampValue}`).toUpperCase(),
  attemptPath,
  outputPath: `${attemptPath}/outputs`,
  progressPath: `${attemptPath}/progress.ndjson`,
  resultPath: `${attemptPath}/result.json`,
  cancelSignal: { ...sourceRequest.cancelSignal, path: `${attemptPath}/cancel.json` },
  configSnapshot: { ...sourceRequest.configSnapshot, promptSet: "open-md-v3" },
  payload: { ...sourceRequest.payload, providerMode: "live" },
};
await writeJsonOnce(requestPath, request);

const executable = path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe");
const execution = await run(executable, ["run", "--request", requestPath], releaseRoot);
if (execution.code !== 0) throw new Error(`WORKER_FAILED code=${execution.code} stderr=${execution.stderr}`);

const resultPath = packagePath(jobRoot, request.resultPath);
const result = await readJson(resultPath);
if (result.status !== "completed" || result.worker?.id !== "content.deepseek" || result.worker?.version !== workerVersion) throw new Error("RESULT_INVALID");
const learningRef = outputRef(result, "learning-bundle");
const proofreadRef = outputRef(result, "ocr-proofread-bundle");
const cardRef = result.outputs.find((entry) => entry.kind === "card-bundle");
const learning = await loadOutput(jobRoot, learningRef);
const proofread = await loadOutput(jobRoot, proofreadRef);
if (learning.payload.release_state !== "unresolved") throw new Error(`SEMANTIC_GATE_INVALID release_state=${learning.payload.release_state}`);
if (cardRef) throw new Error("SEMANTIC_GATE_INVALID CardBundle published for unresolved OCR");
if (!Array.isArray(learning.payload.unresolved_items) || learning.payload.unresolved_items.length === 0) throw new Error("SEMANTIC_GATE_INVALID unresolved_items empty");
const liveTraces = learning.payload.ai_trace.filter((entry) => entry.mode === "live");
if (liveTraces.length === 0 || liveTraces.some((entry) => entry.provider !== "deepseek")) throw new Error("PROVIDER_EVIDENCE_INVALID");

const canaryRoot = path.join(workerRoot, "canary", "ocr-lexical-audit", `${stampValue}-v1228-live-content-only`);
await mkdir(canaryRoot, { recursive: true });
const readablePath = path.join(canaryRoot, "readable-before-after.md");
const first = learning.payload.items[0];
const before = first?.proofreading?.proofread_segments?.map((entry) => entry.text).join("") ?? "";
const after = first?.proofreading?.proofread_text ?? "";
const unresolved = first?.proofreading?.unresolved ?? [];
const readable = [
  "# OCR Content-only live review",
  "",
  `- worker: content.deepseek@${workerVersion}`,
  "- provider: DeepSeek live",
  "- OCR rerun: no",
  "- exporter/destination write: no",
  `- release state: ${learning.payload.release_state}`,
  "",
  "## Before (immutable OCR text)",
  "",
  "```text",
  before,
  "```",
  "",
  "## After (Content proofreading text)",
  "",
  "```text",
  after,
  "```",
  "",
  "## Unresolved review items",
  "",
  "```json",
  JSON.stringify(unresolved, null, 2),
  "```",
  "",
  "## Provider stages",
  "",
  ...liveTraces.map((entry) => `- ${entry.stage}: ${entry.model}, attempts=${entry.attempts}`),
  "",
].join("\n");
await writeFile(readablePath, readable, { flag: "wx" });

const receiptPath = path.join(canaryRoot, "receipt.v1.json");
const receipt = {
  schemaVersion: "content-ocr-lexical-live-review-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: workerVersion },
  providerMode: "live",
  sourceBoundary: "existing immutable OCR content-input; no OCR rerun",
  source: { request: await evidence(sourceRequestPath), inputRefs: sourceRequest.inputRefs },
  attempt: { request: await evidence(requestPath), result: await evidence(resultPath) },
  outputs: {
    proofread: proofread.evidence,
    learning: learning.evidence,
    cardBundlePublished: false,
  },
  observed: {
    releaseState: learning.payload.release_state,
    unresolvedItems: learning.payload.unresolved_items.length,
    unresolvedCandidates: unresolved.length,
    originalOcrPreserved: before === after,
    liveStages: liveTraces.map((entry) => ({ stage: entry.stage, callKey: entry.call_key, provider: entry.provider, model: entry.model, attempts: entry.attempts })),
  },
  effects: { minerUCalls: 0, exporterCalls: 0, destinationWrites: 0, oldAttemptMutated: false, oldDestinationObjectMutated: false },
  release: { manifest: await evidence(releaseManifestPath), payloadTreeSha256: releaseManifest.payloadTreeSha256, exeSha256: releaseManifest.files.find((entry) => entry.path === "KnowledgeClipper.Content.Worker.exe")?.sha256 },
  readable: await evidence(readablePath),
  completedAt: new Date().toISOString(),
};
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ status: "PASS", root: canaryRoot, request: await evidence(requestPath), result: await evidence(resultPath), learning: learning.evidence, proofread: proofread.evidence, readable: await evidence(readablePath), receipt: await evidence(receiptPath), observed: receipt.observed, effects: receipt.effects })}\n`);

function outputRef(resultValue, kind) {
  const ref = resultValue.outputs.find((entry) => entry.kind === kind);
  if (!ref) throw new Error(`OUTPUT_MISSING ${kind}`);
  return ref;
}
async function loadOutput(root, ref) {
  const manifestPath = packagePath(root, ref.manifestPath);
  const manifestEvidence = await evidence(manifestPath);
  if (manifestEvidence.sha256.toLowerCase() !== ref.expectedSha256.toLowerCase()) throw new Error(`MANIFEST_INVALID ${ref.kind}`);
  const manifest = await readJson(manifestPath);
  const file = manifest.files[0];
  const payloadPath = packagePath(root, file.path);
  const payloadEvidence = await evidence(payloadPath);
  if (payloadEvidence.sha256.toLowerCase() !== file.sha256.toLowerCase() || payloadEvidence.bytes !== file.bytes) throw new Error(`PAYLOAD_INVALID ${ref.kind}`);
  return { payload: await readJson(payloadPath), evidence: { ref, manifest: manifestEvidence, payload: payloadEvidence } };
}
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

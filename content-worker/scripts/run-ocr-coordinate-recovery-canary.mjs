import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceRequestPath = absoluteArg("--source-request");
const releaseRoot = absoluteArg("--release-root");
const fixturePath = path.join(workerRoot, "fixtures", "ocr-coordinate-normalization-v1", "v1218-frozen-evidence.json");
const fixture = await readJson(fixturePath);
const sourceRequest = await readJson(sourceRequestPath);
const jobRoot = deriveJobRoot(sourceRequestPath, sourceRequest.attemptPath);
const sourceInput = await readJson(packagePath(jobRoot, sourceRequest.payload.contentInputPath));
const sourceContentRef = sourceRequest.inputRefs.find((entry) => entry.kind === "content-input");
if (!sourceContentRef) throw new Error("INPUT_INVALID source content-input ref");
const sourceContentManifest = await readJson(packagePath(jobRoot, sourceContentRef.manifestPath));
if (sourceInput.schema_version !== "standard-input/4.0.0" || sourceInput.items.length !== 1 || sourceInput.items[0].source.kind !== "ocr_text") throw new Error("INPUT_INVALID source OCR v4");
const item = sourceInput.items[0];
const sourceMaterialPath = packagePath(jobRoot, item.source.material_path);
const sourceBefore = await evidence(sourceMaterialPath);
if (sourceBefore.sha256.toLowerCase() !== fixture.source.sourceSha256 || await readFile(sourceMaterialPath, "utf8") !== fixture.source.sourceText) throw new Error("INPUT_TAMPERED frozen OCR source");
if (sha(stableStringify(fixture.response)).toLowerCase() !== fixture.responseSha256) throw new Error("INPUT_TAMPERED frozen provider response");

const stampValue = stamp();
const runId = `run-content-ocr-coordinate-recovery-${stampValue}`;
const attemptId = `attempt-content-ocr-coordinate-recovery-replay-0001`;
const adapterRoot = `runs/${runId}/adapters/content.understand-and-card/content-input-ocr-coordinate-recovery-${stampValue}/v0001`;
const inputRelative = `${adapterRoot}/content-input.v4.json`;
const replayRelative = `${adapterRoot}/provider-replay.v1.json`;
const artifactRelative = `${adapterRoot}/artifact.json`;
await writeJsonOnce(packagePath(jobRoot, inputRelative), sourceInput);
await writeJsonOnce(packagePath(jobRoot, replayRelative), {
  schema_version: "provider-replay/1.0.0",
  responses: { [`ocr_proofreading:${item.item_id}`]: fixture.response },
});
const declaredPaths = [inputRelative, replayRelative, item.source.material_path];
if (item.source.provenance.structured_artifact) declaredPaths.push(item.source.provenance.structured_artifact.payload_path);
const files = await Promise.all([...new Set(declaredPaths)].map(async (relative) => {
  const digest = await evidence(packagePath(jobRoot, relative));
  return { path: relative, sha256: digest.sha256.toLowerCase(), bytes: digest.bytes, mediaType: relative.endsWith(".md") ? "text/markdown; charset=utf-8" : "application/json" };
}));
const contentArtifact = {
  schemaVersion: "1.0.0",
  artifactId: `content-input-ocr-coordinate-recovery-${stampValue}`,
  kind: "content-input",
  contractVersion: "1.0.0",
  mediaType: "application/json",
  files,
  createdAt: new Date().toISOString(),
  producer: { stageId: "content.fixture-replay", workerId: "content.deepseek", workerVersion: "0.9.1", attemptId: `stage-${stampValue}` },
  inputRefs: sourceContentManifest.inputRefs,
  dataClass: sourceContentManifest.dataClass,
  metadata: { entrypoint: inputRelative, moduleSchema: "standard-input/4.0.0", frozenProviderResponseReplay: true, rerunOcr: false },
};
await writeJsonOnce(packagePath(jobRoot, artifactRelative), contentArtifact);
const contentArtifactDigest = await evidence(packagePath(jobRoot, artifactRelative));
const attemptPath = `runs/${runId}/stages/content.understand-and-card/attempts/${attemptId}`;
const request = {
  ...sourceRequest,
  requestId: `request-${attemptId}`,
  operationId: `operation-${attemptId}`,
  runId,
  attemptId,
  idempotencyKey: sha(`ocr-coordinate-recovery\0${sourceRequest.taskId}\0${fixture.responseSha256}\0${stampValue}`),
  attemptPath,
  outputPath: `${attemptPath}/outputs`,
  progressPath: `${attemptPath}/progress.ndjson`,
  resultPath: `${attemptPath}/result.json`,
  cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
  inputRefs: [{ artifactRef: contentArtifact.artifactId, kind: "content-input", contractVersion: "1.0.0", manifestPath: artifactRelative, expectedSha256: contentArtifactDigest.sha256.toLowerCase() }],
  configSnapshot: { ...sourceRequest.configSnapshot, ocrCoordinatePolicy: "exact-before-human-review-v1", rerunOcr: false },
  payload: { schemaVersion: "content-run/1.0.0", contentInputPath: inputRelative, providerMode: "replay", replayPath: replayRelative },
};
const requestPath = packagePath(jobRoot, `${attemptPath}/request.json`);
await writeJsonOnce(requestPath, request);
const exe = path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe");
await stat(exe);
const processResult = await runCapture(exe, ["run", "--request", requestPath], releaseRoot);
await writeTextOnce(packagePath(jobRoot, `${attemptPath}/worker-stdout.ndjson`), processResult.stdout);
await writeTextOnce(packagePath(jobRoot, `${attemptPath}/worker-stderr.txt`), processResult.stderr);
const resultPath = packagePath(jobRoot, request.resultPath);
const result = await readJson(resultPath);
if (result.status !== "completed" || result.payload.moduleReleaseState !== "unresolved") throw new Error(`RESULT_INVALID ${result.status}/${result.payload.moduleReleaseState}`);
if (result.outputs.map((entry) => entry.kind).join(",") !== "ocr-proofread-bundle,learning-bundle") throw new Error("OUTPUTS_INVALID");
const proofreadRef = result.outputs.find((entry) => entry.kind === "ocr-proofread-bundle");
const learningRef = result.outputs.find((entry) => entry.kind === "learning-bundle");
const proofreadManifestPath = packagePath(jobRoot, proofreadRef.manifestPath);
const proofreadManifest = await readJson(proofreadManifestPath);
const proofreadPath = packagePath(jobRoot, proofreadManifest.files[0].path);
const proofread = await readJson(proofreadPath);
const proofreadItem = proofread.items[0];
if (proofreadItem.original_text !== fixture.source.sourceText || proofreadItem.proofread_text !== fixture.source.sourceText) throw new Error("SOURCE_REWRITE_DETECTED");
if (proofreadItem.corrections.length !== 0 || proofreadItem.unresolved.length !== 1) throw new Error("DECISION_CANDIDATE_INVALID");
const unresolved = proofreadItem.unresolved[0];
if (unresolved.start !== 31 || unresolved.end !== 36 || unresolved.exact_text !== "CANAR" || !unresolved.evidence.includes("CANARY") || !unresolved.evidence.includes("unique_relocated")) throw new Error("NORMALIZED_CANDIDATE_INVALID");
if (proofreadItem.gates.find((gate) => gate.gate_id === "ocr-proofreading.coordinate-localization")?.status !== "notice") throw new Error("COORDINATE_GATE_MISSING");
const sourceAfter = await evidence(sourceMaterialPath);
if (stableStringify(sourceBefore) !== stableStringify(sourceAfter)) throw new Error("OCR_SOURCE_MUTATED");
const canaryRoot = path.join(workerRoot, "canary", "ocr-coordinate-recovery", `${stampValue}-v1218-frozen-response-replay`);
await mkdir(path.dirname(canaryRoot), { recursive: true });
await mkdir(canaryRoot, { recursive: false });
const readablePath = path.join(canaryRoot, "readable-before-decision.md");
await writeTextOnce(readablePath, [
  "# Content 0.9.1 OCR 坐标恢复 Canary",
  "",
  "- Provider：冻结真实 DeepSeek response replay；外部调用 0",
  "- OCR 重跑：0；Exporter 调用：0",
  "- WorkerResult：completed；moduleReleaseState=unresolved",
  "",
  "## 原始 OCR（保持不变）",
  "",
  fixture.source.sourceText,
  "",
  "## 模型建议（未应用）",
  "",
  `before=${fixture.response.corrections[0].before}；after=${fixture.response.corrections[0].after}；modelRange=[${fixture.response.corrections[0].start},${fixture.response.corrections[0].end})`,
  "",
  "## 规范化人工复核候选",
  "",
  `exact=${unresolved.exact_text}；normalizedRange=[${unresolved.start},${unresolved.end})；reason=${unresolved.reason}`,
  "",
  unresolved.evidence,
  "",
  "未发布 CardBundle；等待 Core 生成 decision token 并接收 accept_original/user_correction。",
  "",
].join("\n"));
const receiptPath = path.join(canaryRoot, "receipt.v1.json");
const receipt = {
  schemaVersion: "content-ocr-coordinate-recovery-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: "0.9.1" },
  provider: { mode: "frozen-real-response-replay", externalConnections: 0, responseSha256: fixture.responseSha256.toUpperCase() },
  immutableSource: { before: sourceBefore, after: sourceAfter, unchanged: true },
  sourceRequest: await evidence(sourceRequestPath),
  fixture: await evidence(fixturePath),
  request: await evidence(requestPath),
  result: await evidence(resultPath),
  proofreadManifest: await evidence(proofreadManifestPath),
  proofreadBundle: await evidence(proofreadPath),
  learningManifest: await evidence(packagePath(jobRoot, learningRef.manifestPath)),
  decision: { modelRange: { start: 34, end: 39 }, normalizedRange: { start: unresolved.start, end: unresolved.end }, before: unresolved.exact_text, suggestedAfter: "CANARY", applied: false, releaseState: "unresolved" },
  effects: { ocrRerun: false, exporterCalls: 0, originalOcrMutated: false },
  readable: await evidence(readablePath),
  completedAt: new Date().toISOString(),
};
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ status: "PASS", jobRoot, runId, attemptId, request: await evidence(requestPath), result: await evidence(resultPath), proofreadManifest: await evidence(proofreadManifestPath), proofreadBundle: await evidence(proofreadPath), learningManifest: await evidence(packagePath(jobRoot, learningRef.manifestPath)), receipt: await evidence(receiptPath), readable: await evidence(readablePath) })}\n`);

function deriveJobRoot(requestPath, attemptPath) { let candidate = path.dirname(path.resolve(requestPath)); for (const _ of attemptPath.split(/[\\/]/u).filter(Boolean)) candidate = path.dirname(candidate); return candidate; }
function packagePath(root, relative) { const resolved = path.resolve(root, ...relative.split(/[\\/]/u)); const prefix = `${path.resolve(root)}${path.sep}`.toLowerCase(); if (!resolved.toLowerCase().startsWith(prefix)) throw new Error(`PATH_ESCAPES_JOB ${relative}`); return resolved; }
async function evidence(target) { const bytes = await readFile(target); return { path: target, bytes: bytes.length, sha256: sha(bytes).toUpperCase() }; }
async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function writeJsonOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" }); }
async function writeTextOnce(target, value) { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, value, { flag: "wx" }); }
function runCapture(command, args, cwd) { return new Promise((resolve, reject) => { const child = spawn(command, args, { cwd, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] }); let stdout = ""; let stderr = ""; child.stdout.on("data", (chunk) => { stdout += chunk.toString(); }); child.stderr.on("data", (chunk) => { stderr += chunk.toString(); }); child.on("error", reject); child.on("close", (code) => code === 0 ? resolve({ stdout, stderr }) : reject(new Error(`COMMAND_FAILED exit=${code}: ${stderr}`))); }); }
function arg(name) { const index = process.argv.indexOf(name); if (index < 0 || !process.argv[index + 1]) throw new Error(`CLI_INVALID missing ${name}`); return process.argv[index + 1]; }
function absoluteArg(name) { const value = path.resolve(arg(name)); if (!path.isAbsolute(value)) throw new Error(`CLI_INVALID ${name} absolute path required`); return value; }
function sha(value) { return createHash("sha256").update(value).digest("hex"); }
function stableStringify(value) { return JSON.stringify(sortValue(value)); }
function sortValue(value) { if (Array.isArray(value)) return value.map(sortValue); if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).sort(([left], [right]) => left.localeCompare(right)).map(([key, item]) => [key, sortValue(item)])); return value; }
function stamp() { return new Date().toISOString().replace(/[-:]/gu, "").replace(/\.\d{3}Z$/u, "Z"); }

import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";

const sourceRequestPath = absoluteArg("--source-request");
const sourceProofreadManifestPath = absoluteArg("--source-proofread-manifest");
const releaseRoot = absoluteArg("--release-root");
const action = arg("--action");
const providerMode = arg("--provider-mode");
if (!new Set(["accept_original", "user_correction"]).has(action)) throw new Error("CLI_INVALID --action");
if (!new Set(["replay", "live"]).has(providerMode)) throw new Error("CLI_INVALID --provider-mode");
const correctedToken = action === "user_correction" ? arg("--corrected-token") : undefined;
if (action === "user_correction" && !correctedToken) throw new Error("CLI_INVALID --corrected-token");

const sourceRequest = await readJson(sourceRequestPath);
const jobRoot = deriveJobRoot(sourceRequestPath, sourceRequest.attemptPath);
const sourceContentRef = sourceRequest.inputRefs.find((entry) => entry.kind === "content-input");
if (!sourceContentRef) throw new Error("INPUT_INVALID source request content-input");
const sourceContentManifestPath = packagePath(jobRoot, sourceContentRef.manifestPath);
const sourceContentManifest = await readJson(sourceContentManifestPath);
const sourceInputPath = packagePath(jobRoot, sourceRequest.payload.contentInputPath);
const sourceInput = await readJson(sourceInputPath);
if (sourceInput.schema_version !== "standard-input/4.0.0" || sourceInput.items.length !== 1 || sourceInput.items[0].source.kind !== "ocr_text") throw new Error("INPUT_INVALID source must be one unresolved OCR v4 item");

const frozenManifest = await readJson(sourceProofreadManifestPath);
const frozenManifestDigest = await evidence(sourceProofreadManifestPath);
if (frozenManifest.kind !== "ocr-proofread-bundle" || frozenManifest.contractVersion !== "1.0.0" || frozenManifest.producer.workerId !== "content.deepseek") throw new Error("INPUT_INVALID frozen proofread artifact");
const frozenPayloadFile = frozenManifest.files[0];
if (!frozenPayloadFile) throw new Error("INPUT_INVALID frozen proofread payload");
const frozenPayloadPath = packagePath(jobRoot, frozenPayloadFile.path);
const frozenPayloadDigest = await evidence(frozenPayloadPath);
if (frozenPayloadDigest.sha256.toLowerCase() !== frozenPayloadFile.sha256.toLowerCase() || frozenPayloadDigest.bytes !== frozenPayloadFile.bytes) throw new Error("INPUT_TAMPERED frozen proofread payload");
const frozen = await readJson(frozenPayloadPath);
const item = frozen.items.find((entry) => entry.item_id === sourceInput.items[0].item_id);
if (!item || item.state !== "unresolved" || item.unresolved.length === 0 || frozen.release_state !== "unresolved") throw new Error("INPUT_INVALID frozen proofread unresolved state");

const candidate = item.unresolved[0];
const frozenSegment = item.proofread_segments[candidate.segment_index];
if (!frozenSegment) throw new Error("INPUT_INVALID unresolved candidate segment");
const candidateStart = locateFrozenCandidate(frozenSegment.text, candidate, item.corrections);
const candidateEnd = candidateStart + candidate.exact_text.length;
const userDiffs = [];
const resolvedSegments = item.proofread_segments.map((segment) => ({ ...segment }));
if (action === "user_correction") {
  const diff = minimalSingleDiff(candidate.exact_text, correctedToken);
  const segment = resolvedSegments[candidate.segment_index];
  const start = candidateStart + diff.start;
  const end = candidateStart + diff.end;
  if (segment.text.slice(start, end) !== diff.before) throw new Error("INPUT_TAMPERED user correction before-exact");
  segment.text = `${segment.text.slice(0, start)}${diff.after}${segment.text.slice(end)}`;
  userDiffs.push({ segment_index: candidate.segment_index, operation: diff.operation, start, end, before: diff.before, after: diff.after });
}
const resolvedText = canonicalSegments(resolvedSegments);

const stampValue = stamp();
const runId = `run-content-ocr-human-${stampValue}`;
const attemptId = `attempt-content-ocr-human-${action}-${providerMode}-0001`;
const adapterRoot = `runs/${runId}/adapters/content.understand-and-card/content-input-ocr-human-${stampValue}/v0001`;
const inputRelative = `${adapterRoot}/content-input.v5.json`;
const artifactRelative = `${adapterRoot}/artifact.json`;
const replayRelative = `${adapterRoot}/provider-replay.v1.json`;
const v5 = structuredClone(sourceInput);
v5.schema_version = "standard-input/5.0.0";
v5.input_version = `${sourceInput.input_version}-human-${stampValue}`;
v5.items[0].ocr_human_resolution = {
  schema_version: "ocr-human-resolution-input/1.0.0",
  resolution_id: `resolution-${action}-${stampValue}`,
  action,
  source_proofread_artifact: {
    artifactRef: frozenManifest.artifactId,
    kind: "ocr-proofread-bundle",
    contractVersion: "1.0.0",
    manifestPath: relativeJobPath(jobRoot, sourceProofreadManifestPath),
    expectedSha256: frozenManifestDigest.sha256.toLowerCase(),
    payload_path: frozenPayloadFile.path,
    payload_sha256: frozenPayloadFile.sha256.toLowerCase(),
  },
  source_item_id: item.item_id,
  original_text_sha256: item.original_text_sha256,
  proofread_text_sha256: item.proofread_text_sha256,
  unresolved_candidates: item.unresolved,
  reason: action === "accept_original" ? "User confirms the retained OCR literal is intentional." : "User explicitly supplies the corrected token; this is human evidence, not an AI autonomous correction.",
  ...(action === "user_correction" ? { correction: { corrected_text: resolvedText, corrected_text_sha256: sha(resolvedText), diffs: userDiffs } } : {}),
};
await writeJsonOnce(packagePath(jobRoot, inputRelative), v5);

if (providerMode === "replay") await writeJsonOnce(packagePath(jobRoot, replayRelative), makeReplay(item.item_id, resolvedSegments, resolvedText));
const declaredPaths = [inputRelative, sourceInput.items[0].source.material_path];
if (sourceInput.items[0].source.provenance.structured_artifact) declaredPaths.push(sourceInput.items[0].source.provenance.structured_artifact.payload_path);
if (providerMode === "replay") declaredPaths.push(replayRelative);
const files = await Promise.all([...new Set(declaredPaths)].map(async (relative) => {
  const digest = await evidence(packagePath(jobRoot, relative));
  return { path: relative, sha256: digest.sha256.toLowerCase(), bytes: digest.bytes, mediaType: relative.endsWith(".md") ? "text/markdown; charset=utf-8" : "application/json" };
}));
const frozenCommonRef = {
  artifactRef: frozenManifest.artifactId,
  kind: "ocr-proofread-bundle",
  contractVersion: "1.0.0",
  manifestPath: relativeJobPath(jobRoot, sourceProofreadManifestPath),
  expectedSha256: frozenManifestDigest.sha256.toLowerCase(),
};
const contentArtifact = {
  schemaVersion: "1.0.0",
  artifactId: `content-input-ocr-human-${stampValue}`,
  kind: "content-input",
  contractVersion: "1.0.0",
  mediaType: "application/json",
  files,
  createdAt: new Date().toISOString(),
  producer: { stageId: "core.resume", workerId: "core.orchestrator", workerVersion: "1.0.0", attemptId: `resume-${stampValue}` },
  inputRefs: dedupeRefs([...sourceContentManifest.inputRefs, frozenCommonRef]),
  dataClass: sourceContentManifest.dataClass,
  metadata: { entrypoint: inputRelative, moduleSchema: "standard-input/5.0.0", continuation: true, rerunOcr: false, humanAction: action, destinationsOwnedByCore: true },
};
const contentArtifactPath = packagePath(jobRoot, artifactRelative);
await writeJsonOnce(contentArtifactPath, contentArtifact);
const contentArtifactDigest = await evidence(contentArtifactPath);

const attemptPath = `runs/${runId}/stages/content.understand-and-card/attempts/${attemptId}`;
const request = {
  ...sourceRequest,
  requestId: `request-${attemptId}`,
  operationId: `operation-${attemptId}`,
  runId,
  attemptId,
  idempotencyKey: sha(`ocr-human-continuation\0${sourceRequest.taskId}\0${action}\0${providerMode}\0${stampValue}`),
  attemptPath,
  outputPath: `${attemptPath}/outputs`,
  progressPath: `${attemptPath}/progress.ndjson`,
  resultPath: `${attemptPath}/result.json`,
  cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
  inputRefs: [{ artifactRef: contentArtifact.artifactId, kind: "content-input", contractVersion: "1.0.0", manifestPath: artifactRelative, expectedSha256: contentArtifactDigest.sha256.toLowerCase() }],
  configSnapshot: { ...sourceRequest.configSnapshot, ocrHumanResolution: "user-evidence-bound-v1", rerunOcr: false },
  payload: { schemaVersion: "content-run/1.0.0", contentInputPath: inputRelative, providerMode, ...(providerMode === "replay" ? { replayPath: replayRelative } : {}) },
};
const requestPath = packagePath(jobRoot, `${attemptPath}/request.json`);
await writeJsonOnce(requestPath, request);

const exe = path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe");
await stat(exe);
const processResult = await runCapture(exe, ["run", "--request", requestPath], releaseRoot);
const stdoutPath = packagePath(jobRoot, `${attemptPath}/worker-stdout.ndjson`);
const stderrPath = packagePath(jobRoot, `${attemptPath}/worker-stderr.txt`);
await writeTextOnce(stdoutPath, processResult.stdout);
await writeTextOnce(stderrPath, processResult.stderr);
const resultPath = packagePath(jobRoot, request.resultPath);
const result = await readJson(resultPath);
const outputEvidence = await Promise.all(result.outputs.map(async (ref) => ({ ref, manifest: await evidence(packagePath(jobRoot, ref.manifestPath)) })));
process.stdout.write(`${JSON.stringify({ status: result.status, providerMode, action, jobRoot, runId, attemptId, sourceRequest: await evidence(sourceRequestPath), frozenProofreadManifest: frozenManifestDigest, input: await evidence(packagePath(jobRoot, inputRelative)), contentArtifact: await evidence(contentArtifactPath), request: await evidence(requestPath), result: await evidence(resultPath), outputs: outputEvidence, originalText: item.original_text, frozenText: item.proofread_text, resolvedText, userDiffs, ocrRerun: false, exporterCalls: 0 })}\n`);

function makeReplay(itemId, segments, cleanedText) {
  const allIndexes = segments.map((segment) => segment.segment_index);
  const questionMatch = /Question:\s*([^?]+\?)/u.exec(cleanedText);
  const answerMatch = /Answer:\s*([^\s]+)/u.exec(cleanedText);
  const question = questionMatch ? `Question: ${questionMatch[1]}` : cleanedText;
  const answer = answerMatch ? `Answer: ${answerMatch[1]}` : cleanedText;
  return {
    schema_version: "provider-replay/1.0.0",
    responses: {
      [`cleaning:${itemId}`]: { cleaned_text: cleanedText, removed_fragments: [], format_anomalies: [] },
      [`classification:${itemId}`]: { units: [{ unit_id: "unit-user-resolved-ocr", source_segment_indexes: allIndexes, category_id: "question_answer", subtype: "provided_answer", answer_state: "provided", context_state: "provided", risk_state: "ordinary", question, supplied_answer: answer, unresolved_reasons: [] }] },
      [`card_generation:${itemId}:unit-user-resolved-ocr`]: { generated_content: [], cards: [{ template: "basic_qa", front_blocks: [{ type: "text", text: question, provenance: "source" }], back_blocks: [{ type: "text", text: answer, provenance: "source" }], answer_provenance: "source" }], unresolved_reasons: [] },
    },
  };
}

function locateFrozenCandidate(text, candidate, corrections) {
  const mapped = corrections.filter((entry) => entry.segment_index === candidate.segment_index && entry.end <= candidate.start).reduce((value, entry) => value + entry.after.length - entry.before.length, candidate.start);
  if (text.slice(mapped, mapped + candidate.exact_text.length) === candidate.exact_text) return mapped;
  const first = text.indexOf(candidate.exact_text);
  if (first < 0 || text.indexOf(candidate.exact_text, first + 1) >= 0) throw new Error("INPUT_INVALID candidate not uniquely located in frozen proofread segment");
  return first;
}

function minimalSingleDiff(before, after) {
  if (before === after) throw new Error("INPUT_INVALID corrected token equals unresolved token");
  let prefix = 0;
  while (prefix < before.length && prefix < after.length && before[prefix] === after[prefix]) prefix += 1;
  let suffix = 0;
  while (suffix < before.length - prefix && suffix < after.length - prefix && before[before.length - 1 - suffix] === after[after.length - 1 - suffix]) suffix += 1;
  const beforeMiddle = before.slice(prefix, before.length - suffix);
  const afterMiddle = after.slice(prefix, after.length - suffix);
  const operation = beforeMiddle === "" ? "insertion" : afterMiddle === "" ? "deletion" : "substitution";
  return { operation, start: prefix, end: before.length - suffix, before: beforeMiddle, after: afterMiddle };
}

function canonicalSegments(segments) {
  return segments.map((segment) => segment.text.replace(/\r\n?/gu, "\n").replace(/\n+$/u, "")).filter((value) => value.trim() !== "").join("\n\n");
}

function deriveJobRoot(requestPath, attemptPath) {
  let candidate = path.dirname(path.resolve(requestPath));
  for (const _ of attemptPath.split(/[\\/]/u).filter(Boolean)) candidate = path.dirname(candidate);
  return candidate;
}

function packagePath(root, relative) {
  const resolved = path.resolve(root, ...relative.split(/[\\/]/u));
  const prefix = `${path.resolve(root)}${path.sep}`.toLowerCase();
  if (!resolved.toLowerCase().startsWith(prefix)) throw new Error(`PATH_ESCAPES_JOB ${relative}`);
  return resolved;
}

function relativeJobPath(root, absolute) {
  const relative = path.relative(root, absolute).split(path.sep).join("/");
  if (relative.startsWith("../") || path.isAbsolute(relative)) throw new Error("PATH_OUTSIDE_JOB");
  return relative;
}

function dedupeRefs(refs) {
  const seen = new Set();
  return refs.filter((ref) => {
    const key = `${ref.kind}\0${ref.artifactRef}\0${ref.expectedSha256.toLowerCase()}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

async function evidence(target) {
  const bytes = await readFile(target);
  return { path: target, bytes: bytes.length, sha256: sha(bytes).toUpperCase() };
}

async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }

async function writeJsonOnce(target, value) {
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
}

async function writeTextOnce(target, value) {
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, value, { flag: "wx" });
}

function runCapture(command, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk.toString(); });
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });
    child.on("error", reject);
    child.on("close", (code) => code === 0 ? resolve({ stdout, stderr }) : reject(new Error(`COMMAND_FAILED exit=${code}: ${stderr}`)));
  });
}

function arg(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1]) throw new Error(`CLI_INVALID missing ${name}`);
  return process.argv[index + 1];
}

function absoluteArg(name) {
  const value = path.resolve(arg(name));
  if (!path.isAbsolute(value)) throw new Error(`CLI_INVALID ${name} absolute path required`);
  return value;
}

function sha(value) { return createHash("sha256").update(value).digest("hex"); }
function stamp() { return new Date().toISOString().replace(/[-:]/gu, "").replace(/\.\d{3}Z$/u, "Z"); }

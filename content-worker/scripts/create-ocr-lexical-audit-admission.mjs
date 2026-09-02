import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const projectRoot = path.resolve(workerRoot, "..");
const jobRoot = path.join(projectRoot, "canary", "sealed-live-ocr-anki-v1.2.28", "20260901T023500Z", "jobs", "live-ocr-anki-v1228-20260901015508307");
const originalAttempt = path.join(jobRoot, "runs", "run-20260901015510527", "stages", "content.understand-and-card", "attempts", "attempt-20260901015533653-43f2d4a7f414439e8cb0550efa0de5cc");
const liveAttempt = path.join(jobRoot, "runs", "run-content-live-review-20260901T021211Z", "stages", "content.understand-and-card", "attempts", "attempt-content-live-review-e6d6df99ef5c49f3b4e114980289c9e1");
const replayRoot = path.join(workerRoot, "canary", "ocr-lexical-audit", "20260901T021716Z-v1228-frozen-real-response-replay");
const releaseRoot = path.join(workerRoot, "releases", "content.deepseek-0.9.5-win-x64-formal-20260901T021446Z");
const formalRoot = path.join(workerRoot, "canary", "formal-release", "content.deepseek-0.9.5-win-x64-formal-20260901T021446Z", "20260901T021505608Z-settings-snapshot-replay");
const testRoot = path.join(workerRoot, "canary", "ocr-human-resolution-tests", "20260901T021514Z");
const releaseManifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const formalReceiptPath = path.join(formalRoot, "formal-release-offline-receipt.v1.json");
const testReceiptPath = path.join(testRoot, "receipt.v1.json");
const replayReceiptPath = path.join(replayRoot, "receipt.v1.json");
const originalResultPath = path.join(originalAttempt, "result.json");
const originalLearningPath = path.join(originalAttempt, "outputs", "learning-bundle.schema-v2.json");
const liveResultPath = path.join(liveAttempt, "result.json");
const liveLearningPath = path.join(liveAttempt, "outputs", "learning-bundle.schema-v1.json");
const fixturePath = path.join(workerRoot, "fixtures", "ocr-lexical-audit-v1", "cases.json");
const promptPath = path.join(workerRoot, "prompts", "stages", "ocr-lexical-audit.md");

const [release, formal, tests, replay, originalResult, originalLearning, liveResult, liveLearning, fixtures] = await Promise.all([
  readJson(releaseManifestPath), readJson(formalReceiptPath), readJson(testReceiptPath), readJson(replayReceiptPath), readJson(originalResultPath), readJson(originalLearningPath), readJson(liveResultPath), readJson(liveLearningPath), readJson(fixturePath),
]);
if (release.worker?.workerVersion !== "0.9.5" || release.promotedToCanonicalRegistry !== false) throw new Error("RELEASE_INVALID");
if (formal.status !== "PASS" || tests.tests !== 90 || tests.pass !== 90 || tests.fail !== 0 || replay.status !== "PASS") throw new Error("QUALIFICATION_INVALID");
if (originalResult.status !== "completed" || !originalResult.outputs.some((entry) => entry.kind === "card-bundle") || originalLearning.release_state !== "ready") throw new Error("ORIGINAL_EVIDENCE_INVALID");
if (liveResult.status !== "failed" || liveResult.error?.code !== "content.gate-failed") throw new Error("LIVE_FAILURE_EVIDENCE_INVALID");
const liveTrace = liveLearning.ai_trace.find((entry) => entry.stage === "ocr_proofreading");
if (liveTrace?.provider !== "deepseek" || liveTrace?.mode !== "live" || liveTrace.response?.unresolved?.length !== 1) throw new Error("LIVE_TRACE_INVALID");
if (replay.observed?.releaseState !== "unresolved" || replay.observed?.originalOcrPreserved !== true || replay.outputs?.cardBundlePublished !== false) throw new Error("REPLAY_SEMANTICS_INVALID");
if (fixtures.cases?.length !== 3) throw new Error("FIXTURE_COVERAGE_INVALID");
const productionText = `${await readFile(path.join(workerRoot, "src", "gates.ts"), "utf8")}\n${await readFile(promptPath, "utf8")}`;
if (productionText.includes("CANAR")) throw new Error("SAMPLE_HARDCODE_DETECTED");

const stampValue = stamp();
const readablePath = path.join(replayRoot, "module-admission-readable.md");
const originalText = originalLearning.items[0].proofreading.proofread_text;
const liveCandidate = liveTrace.response.unresolved[0];
const normalizedCandidate = replay.observed.unresolvedCandidate;
await writeFile(readablePath, [
  "# Content OCR lexical fail-closed admission",
  "",
  "## Before: v1.2.28 released text",
  "",
  "```text",
  originalText,
  "```",
  "",
  "- content.deepseek@0.9.3: ready, CardBundle published, then the existing chain wrote to Anki.",
  "- This historical task and destination object were not retried, deleted, or edited.",
  "",
  "## Real DeepSeek observation under 0.9.4",
  "",
  `- provider classified the token as ${liveCandidate.reason}.`,
  `- provider-declared range: [${liveCandidate.start}, ${liveCandidate.end}) (not byte-exact).`,
  "- the gate rejected the imprecise range; no Content artifact was published and no exporter was called.",
  "",
  "## 0.9.5 normalized fail-closed result",
  "",
  `- exact source range: [${normalizedCandidate.start}, ${normalizedCandidate.end})`,
  `- retained text: ${normalizedCandidate.exact_text}`,
  `- reason: ${normalizedCandidate.reason}`,
  "- release_state: unresolved",
  "- CardBundle: not published",
  "- next product state: Core projects this immutable Content result to waiting_input; user accept/correct creates a later attempt.",
  "",
  "## Source-neutral regression coverage",
  "",
  ...fixtures.cases.map((entry) => `- ${entry.id}: ${entry.expectedUnresolved.length > 0 ? "unresolved" : "accepted contextual identifier"}`),
  "- misaligned unique unresolved text is relocated exactly; repeated text fans out to ambiguous candidates.",
  "- ordinary text remains on the pre-existing delete-only cleaning path.",
  "",
].join("\n"), { flag: "wx" });

const receiptPath = path.join(workerRoot, "receipts", `content-ocr-lexical-audit-0.9.5-${stampValue}.json`);
await mkdir(path.dirname(receiptPath), { recursive: true });
const old094Manifests = [
  path.join(workerRoot, "releases", "content.deepseek-0.9.4-win-x64-formal-20260901T020653Z", "release-manifest.v1.json"),
  path.join(workerRoot, "releases", "content.deepseek-0.9.4-win-x64-formal-20260901T021114Z", "release-manifest.v1.json"),
];
const receipt = {
  schemaVersion: "content-ocr-lexical-audit-admission/1.0.0",
  status: "PASS",
  admission: { workerId: "content.deepseek", workerVersion: "0.9.5", scope: "module-candidate-for-core-authority; not self-promoted" },
  defect: { sourceTask: originalResult.taskId, priorWorkerVersion: originalResult.worker.version, observed: "OCR lexical anomaly released as ready with no unresolved item", oldDestinationWritePreserved: true },
  fix: {
    emptyPrimaryReview: "exact-coverage lexical audit over deterministic Latin/alphanumeric token candidates",
    imprecisePrimaryUnresolved: "exact_text relocation inside the declared segment; unique, repeated, missing, and invalid segment cases remain fail-closed",
    ordinaryTextBoundary: "unchanged delete-only cleaning",
    sampleHardcoded: false,
    syntheticOracleUsed: false,
  },
  realProviderEvidence: { mode: "live", result: await evidence(liveResultPath), unpublishedLearning: await evidence(liveLearningPath), trace: { promptSha256: liveTrace.prompt_sha256, requestSha256: liveTrace.request_sha256, responseSha256: liveTrace.response_sha256, provider: liveTrace.provider, model: liveTrace.model, attempts: liveTrace.attempts } },
  successfulReplay: await evidence(replayReceiptPath),
  validation: { tests: await evidence(testReceiptPath), summary: { tests: 90, pass: 90, fail: 0 }, fixture: await evidence(fixturePath), prompt: await evidence(promptPath), formalRelease: await evidence(formalReceiptPath) },
  release: { root: releaseRoot, manifest: await evidence(releaseManifestPath), payloadTreeSha256: release.payloadTreeSha256, exeSha256: release.files.find((entry) => entry.path === "KnowledgeClipper.Content.Worker.exe")?.sha256, promotedToCanonicalRegistry: false },
  supersededUnpromotedCandidates: await Promise.all(old094Manifests.map(evidence)),
  originalEvidence: { result: await evidence(originalResultPath), learning: await evidence(originalLearningPath) },
  readable: await evidence(readablePath),
  effects: { deepSeekCallsForThisAdmission: 1, minerUCalls: 0, exporterCalls: 0, destinationWrites: 0, oldTaskMutated: false, oldDestinationObjectMutated: false },
  owners: { content: "implemented and admitted", core: "bind 0.9.5 and project unresolved to waiting_input", desktop: "render existing human accept/correct choice" },
  completedAt: new Date().toISOString(),
};
await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ status: "PASS", receipt: await evidence(receiptPath), readable: await evidence(readablePath), release: receipt.release, validation: receipt.validation, realProviderEvidence: receipt.realProviderEvidence, successfulReplay: receipt.successfulReplay })}\n`);

async function readJson(target) { return JSON.parse(await readFile(target, "utf8")); }
async function evidence(target) { const payload = await readFile(target); return { path: target, bytes: payload.length, sha256: sha(payload).toUpperCase() }; }
function sha(value) { return createHash("sha256").update(value).digest("hex"); }
function stamp() { return new Date().toISOString().replace(/[-:]/gu, "").replace(/\.\d{3}Z$/u, "Z"); }

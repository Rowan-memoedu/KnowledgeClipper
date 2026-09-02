import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFile, mkdir, readFile, readdir, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const releaseRoot = path.resolve(process.argv[2] || "");
if (!path.isAbsolute(releaseRoot) || releaseRoot === path.parse(releaseRoot).root) throw new Error("CLI_INVALID absolute release-root required");
const releaseId = path.basename(releaseRoot);
const releaseIdentity = /^content\.deepseek-(\d+\.\d+\.\d+)-win-x64-formal-\d{8}T\d{6}Z$/u.exec(releaseId);
if (!releaseIdentity) throw new Error("CLI_INVALID release-root identity");
const workerVersion = releaseIdentity[1];
const exePath = path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe");
const manifestPath = path.join(releaseRoot, "release-manifest.v1.json");
const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
assert(manifest.schemaVersion === "content-formal-release/1.0.0", "RELEASE_MANIFEST_INVALID schemaVersion");
assert(manifest.releaseId === releaseId, "RELEASE_MANIFEST_INVALID releaseId");

const verifiedFiles = [];
for (const expected of manifest.files) {
  const absolute = packagePath(releaseRoot, expected.path);
  const actual = await digest(absolute);
  assert(actual.bytes === expected.bytes && actual.sha256.toUpperCase() === expected.sha256.toUpperCase(), `RELEASE_FILE_MISMATCH ${expected.path}`);
  verifiedFiles.push({ path: expected.path, bytes: actual.bytes, sha256: actual.sha256.toUpperCase() });
}
verifiedFiles.sort((left, right) => left.path.localeCompare(right.path));
const payloadTreeSha256 = sha(verifiedFiles.map((file) => `${file.path}\0${file.bytes}\0${file.sha256}\n`).join("")).toUpperCase();
assert(payloadTreeSha256 === manifest.payloadTreeSha256, "RELEASE_TREE_MISMATCH");
assert(verifiedFiles.length === manifest.payloadFileCount, "RELEASE_FILE_COUNT_MISMATCH");

const canaryParent = path.join(workerRoot, "canary", "formal-release", releaseId);
await mkdir(canaryParent, { recursive: true });
const canaryRoot = path.join(canaryParent, `${stamp()}-settings-snapshot-replay`);
await mkdir(canaryRoot, { recursive: false });
const arbitraryCwd = path.join(canaryRoot, "arbitrary-current-working-directory");
await mkdir(arbitraryCwd);

const describeReleaseRoot = parseJsonOutput(await runCapture(exePath, ["describe", "--json"], releaseRoot));
const describeArbitraryCwd = parseJsonOutput(await runCapture(exePath, ["describe", "--json"], arbitraryCwd));
for (const descriptor of [describeReleaseRoot, describeArbitraryCwd]) {
  assert(descriptor.workerId === "content.deepseek", "DESCRIBE_INVALID workerId");
  assert(descriptor.workerVersion === workerVersion, "DESCRIBE_INVALID workerVersion");
  assert(descriptor.inputKinds.includes("content-config-snapshot"), "DESCRIBE_INVALID inputKinds");
  for (const kind of ["question.answer", "language-practice-decision", "ocr-human-resolution-bundle"]) assert(descriptor.outputKinds.includes(kind), `DESCRIBE_INVALID outputKinds ${kind}`);
}
assert(stableStringify(describeReleaseRoot) === stableStringify(describeArbitraryCwd), "DESCRIBE_CWD_DEPENDENT");
const settingsDescribe = parseJsonOutput(await runCapture(exePath, ["settings", "describe", "--json"], arbitraryCwd));
assert(settingsDescribe.schemaVersion === "content-settings-adapter/1.0.0", "SETTINGS_DESCRIBE_INVALID");
assert(settingsDescribe.worker.id === "content.deepseek" && settingsDescribe.worker.version === workerVersion, "SETTINGS_WORKER_INVALID");
assert(settingsDescribe.resources.runtimeSelection.artifactKind === "content-config-snapshot", "SETTINGS_RUNTIME_SELECTION_INVALID");
const companionDescribe = parseJsonOutput(await runCapture(exePath, ["settings", "companion", "--json"], arbitraryCwd));
assert(companionDescribe.schemaVersion === "content-companion-descriptor/1.1.0", "COMPANION_DESCRIBE_INVALID");
assert(companionDescribe.worker.currentVersion === workerVersion, "COMPANION_WORKER_INVALID");
assert(companionDescribe.taskSnapshot.schemaVersion === "content-config-snapshot/1.1.0", "COMPANION_SNAPSHOT_INVALID");
const packagedCompanion = JSON.parse(await readFile(path.join(releaseRoot, "app", "descriptors", "content-companion-descriptor.v1.1.json"), "utf8"));
assert(stableStringify(companionDescribe) === stableStringify(packagedCompanion), "COMPANION_PACKAGED_DESCRIPTOR_MISMATCH");

await writeJsonOnce(path.join(canaryRoot, "describe-release-root.json"), describeReleaseRoot);
await writeJsonOnce(path.join(canaryRoot, "describe-arbitrary-cwd.json"), describeArbitraryCwd);
await writeJsonOnce(path.join(canaryRoot, "settings-describe.json"), settingsDescribe);
await writeJsonOnce(path.join(canaryRoot, "content-companion-describe.json"), companionDescribe);

const draftRoot = path.join(canaryRoot, "settings-user-copy");
const copyResult = parseJsonOutput(await runCapture(exePath, ["settings", "copy", "--target", draftRoot, "--json"], arbitraryCwd));
assert(copyResult.validation.valid === true, "SETTINGS_COPY_INVALID");
const systemPromptPath = path.join(draftRoot, "prompts", "shared", "system.md");
const systemPromptOriginal = await readFile(systemPromptPath, "utf8");
await writeFile(systemPromptPath, `${systemPromptOriginal}\n<!-- formal release offline snapshot replay -->\n`, { flag: "w" });
const validation = parseJsonOutput(await runCapture(exePath, ["settings", "validate", "--root", draftRoot, "--json"], arbitraryCwd));
assert(validation.valid === true, "SETTINGS_CUSTOM_VALIDATION_FAILED");
assert(validation.diff.promptSetChanged === true, "SETTINGS_CUSTOM_DIFF_MISSING");
await writeJsonOnce(path.join(canaryRoot, "settings-copy-result.json"), copyResult);
await writeJsonOnce(path.join(canaryRoot, "settings-validation.json"), validation);

const staged = await stageWorkerRequest(canaryRoot, draftRoot, validation);
const runCaptureResult = await runCapture(exePath, ["run", "--request", staged.requestPath], arbitraryCwd);
await writeFile(path.join(canaryRoot, "worker-stdout.ndjson"), runCaptureResult.stdout, { flag: "wx" });
await writeFile(path.join(canaryRoot, "worker-stderr.txt"), runCaptureResult.stderr, { flag: "wx" });
const result = JSON.parse(await readFile(staged.resultPath, "utf8"));
assert(result.status === "completed", `WORKER_RESULT_INVALID ${result.status}`);
assert(result.worker.id === "content.deepseek" && result.worker.version === workerVersion, "WORKER_RESULT_VERSION_INVALID");
assert(result.payload.contentSettings.settingsRevisionId === staged.settingsRevisionId, "WORKER_SETTINGS_REVISION_NOT_CONSUMED");
assert(result.payload.contentSettings.promptSetSha256 === staged.promptSetSha256, "WORKER_PROMPT_SET_NOT_CONSUMED");
assert(result.payload.contentSettings.categoryRegistrySha256 === staged.categoryRegistrySha256, "WORKER_REGISTRY_NOT_CONSUMED");
assert(result.payload.contentSettings.credentialsRecorded === false, "WORKER_CREDENTIAL_POLICY_INVALID");

const learning = await loadBundle(staged.jobRoot, result, "learning-bundle");
const cards = await loadBundle(staged.jobRoot, result, "card-bundle");
assert(learning.worker.version === workerVersion, "LEARNING_VERSION_INVALID");
assert(learning.worker.settings_revision_id === staged.settingsRevisionId, "LEARNING_SETTINGS_REVISION_NOT_CONSUMED");
assert(learning.worker.prompt_set_sha256 === staged.promptSetSha256, "LEARNING_PROMPT_SET_NOT_CONSUMED");
assert(learning.ai_trace.every((trace) => trace.mode === "replay"), "PROVIDER_MODE_NOT_REPLAY");
assert(learning.ai_trace.some((trace) => trace.prompt_files.some((file) => file.path === "shared/system.md" && file.sha256 === staged.systemPromptSha256)), "CUSTOM_PROMPT_NOT_USED");
assert(cards.cards.length > 0, "CARD_BUNDLE_EMPTY");

const originalText = await readFile(staged.materialPath, "utf8");
const cleanedText = learning.items[0].cleaning.cleaned_text;
const readable = [
  `# Content ${workerVersion} formal release offline acceptance`,
  "",
  `- release: ${releaseId}`,
  "- provider: replay (external connections: 0)",
  `- settings revision: ${staged.settingsRevisionId}`,
  `- prompt set SHA-256: ${staged.promptSetSha256}`,
  "",
  "## Original text",
  "",
  originalText.trimEnd(),
  "",
  "## Cleaned text",
  "",
  cleanedText,
  "",
  "## Cards",
  "",
  ...cards.cards.flatMap((card, index) => [
    `### Card ${index + 1} (${card.template})`,
    "",
    `Front: ${card.front_blocks.map((block) => block.text).join("\n")}`,
    "",
    `Back: ${card.back_blocks.map((block) => block.text).join("\n")}`,
    "",
  ]),
].join("\n");
const readablePath = path.join(canaryRoot, "readable-original-to-result.md");
await writeFile(readablePath, readable, { flag: "wx" });

const receiptPath = path.join(canaryRoot, "formal-release-offline-receipt.v1.json");
const receipt = {
  schemaVersion: "content-formal-release-offline-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: workerVersion },
  release: {
    id: releaseId,
    root: releaseRoot,
    manifest: await evidence(manifestPath),
    payloadFileCount: verifiedFiles.length,
    payloadTreeSha256,
    entrypoint: await evidence(exePath),
    nodeRuntime: await evidence(path.join(releaseRoot, "runtime", "node-v24.18.0-win-x64", "node.exe")),
    appendOnly: true,
    currentWorkingDirectoryIndependent: true,
  },
  describe: {
    releaseRoot: await evidence(path.join(canaryRoot, "describe-release-root.json")),
    arbitraryCurrentWorkingDirectory: await evidence(path.join(canaryRoot, "describe-arbitrary-cwd.json")),
    workerId: describeReleaseRoot.workerId,
    workerVersion: describeReleaseRoot.workerVersion,
    stages: describeReleaseRoot.stages,
    inputKinds: describeReleaseRoot.inputKinds,
    outputKinds: describeReleaseRoot.outputKinds,
  },
  companionDescriptor: {
    packaged: await evidence(path.join(releaseRoot, "app", "descriptors", "content-companion-descriptor.v1.1.json")),
    runtime: await evidence(path.join(canaryRoot, "content-companion-describe.json")),
    schemaVersion: companionDescribe.schemaVersion,
    settingsSchemaVersion: companionDescribe.settingsSchema.schemaVersion,
    taskSnapshotSchemaVersion: companionDescribe.taskSnapshot.schemaVersion,
  },
  settingsSnapshotReplay: {
    providerMode: "replay",
    externalConnections: 0,
    credentialsRecorded: false,
    settingsRevisionId: staged.settingsRevisionId,
    promptSetSha256: staged.promptSetSha256,
    categoryRegistrySha256: staged.categoryRegistrySha256,
    request: await evidence(staged.requestPath),
    result: await evidence(staged.resultPath),
    resultStatus: result.status,
    outputKinds: result.outputs.map((output) => output.kind),
    learningBundle: await evidence(learning.path),
    cardBundle: await evidence(cards.path),
    customSystemPromptSha256: staged.systemPromptSha256,
    customSystemPromptObservedInTrace: true,
  },
  readable: await evidence(readablePath),
  assertions: [
    "release payload file bytes/SHA and aggregate tree SHA match manifest",
    "describe is identical from release-root and arbitrary current working directory",
    `workerVersion is ${workerVersion} and settings snapshot artifact is advertised`,
    "companion descriptor is packaged and identical to arbitrary-CWD CLI output",
    "formal WorkerRequest consumed immutable content-config-snapshot",
    "LearningBundle trace proves the copied and edited Markdown prompt was used",
    "provider mode is replay; no DeepSeek or exporter call was made",
  ],
  completedAt: new Date().toISOString(),
};
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ type: "content.formal-release.verification", status: "PASS", releaseRoot, payloadTreeSha256, exeSha256: receipt.release.entrypoint.sha256, receiptPath, receiptSha256: (await digest(receiptPath)).sha256.toUpperCase(), readablePath })}\n`);

async function stageWorkerRequest(root, draftRoot, validation) {
  const fixtureRoot = path.join(workerRoot, "fixtures", "offline-qa");
  const jobRoot = path.join(root, "jobs", "task-formal-settings-v1");
  const contentRoot = path.join(jobRoot, "inputs", "content");
  await mkdir(path.join(contentRoot, "materials"), { recursive: true });
  const input = JSON.parse(await readFile(path.join(fixtureRoot, "inputs", "standard-input.v1.json"), "utf8"));
  input.items[0].source.material_path = "inputs/content/materials/clip.md";
  const inputRelative = "inputs/content/standard-input.v1.json";
  const materialRelative = "inputs/content/materials/clip.md";
  const replayRelative = "inputs/content/provider-replay.v1.json";
  await writeJsonOnce(packagePath(jobRoot, inputRelative), input);
  await copyFileOnce(path.join(fixtureRoot, "materials", "clip.md"), packagePath(jobRoot, materialRelative));
  await copyFileOnce(path.join(fixtureRoot, "provider-replay.v1.json"), packagePath(jobRoot, replayRelative));
  const inputArtifact = await makeArtifact(jobRoot, {
    artifactId: "content-input-formal-settings-v1",
    kind: "content-input",
    files: [inputRelative, materialRelative, replayRelative],
    entrypoint: inputRelative,
    producer: { stageId: "capture.normalize", workerId: "formal.release.fixture", workerVersion: "1.0.0", attemptId: "source-formal-settings-v1" },
  });

  const settingsRevisionId = "formal-settings-replay-v1";
  const settingsRootRelative = `inputs/content-settings/${settingsRevisionId}`;
  const promptRootRelative = `${settingsRootRelative}/prompts`;
  const registryRelative = `${settingsRootRelative}/config/categories.v2.json`;
  const snapshotRelative = `${settingsRootRelative}/content-config-snapshot.v1.json`;
  const promptFiles = [];
  for (const source of validation.promptSet.files) {
    const packageRelative = `${promptRootRelative}/${source.path}`;
    const sourceAbsolute = path.join(draftRoot, "prompts", ...source.path.split("/"));
    await copyFileOnce(sourceAbsolute, packagePath(jobRoot, packageRelative));
    const file = await digest(packagePath(jobRoot, packageRelative));
    promptFiles.push({ path: source.path, packagePath: packageRelative, sha256: file.sha256, bytes: file.bytes });
  }
  promptFiles.sort((left, right) => left.path.localeCompare(right.path));
  const promptSetSha256 = sha(stableStringify(promptFiles.map(({ path: promptPath, sha256, bytes }) => ({ path: promptPath, sha256, bytes }))));
  await copyFileOnce(path.join(draftRoot, "config", "categories.v2.json"), packagePath(jobRoot, registryRelative));
  const registry = await digest(packagePath(jobRoot, registryRelative));
  const snapshot = {
    schemaVersion: "content-config-snapshot/1.0.0",
    settingsRevisionId,
    workerCompatibility: { workerId: "content.deepseek", minimumVersion: workerVersion },
    promptSet: { id: "user-open-md-v2-formal-canary", source: "user-copy", promptRootPath: promptRootRelative, sha256: promptSetSha256, files: promptFiles },
    categoryRegistry: { id: "category-registry/2.0.0", schemaVersion: "category-registry/2.0.0", packagePath: registryRelative, sha256: registry.sha256, bytes: registry.bytes },
    providerBoundary: { provider: "deepseek", credentials: "process-injected-only" },
  };
  await writeJsonOnce(packagePath(jobRoot, snapshotRelative), snapshot);
  const configArtifact = await makeArtifact(jobRoot, {
    artifactId: "content-config-formal-settings-replay-v1",
    kind: "content-config-snapshot",
    files: [snapshotRelative, registryRelative, ...promptFiles.map((file) => file.packagePath)],
    entrypoint: snapshotRelative,
    producer: { stageId: "content.settings", workerId: "formal.release.fixture", workerVersion: "1.0.0", attemptId: "publish-formal-settings-v1" },
    credentialsRecorded: false,
  });
  const attemptId = "attempt-formal-settings-v1";
  const attemptPath = `runs/run-formal-settings-v1/stages/content.understand-and-card/attempts/${attemptId}`;
  const request = {
    schemaVersion: "1.0.0",
    requestId: "request-formal-settings-v1",
    operationId: "operation-formal-settings-v1",
    taskId: "task-formal-settings-v1",
    runId: "run-formal-settings-v1",
    stageId: "content.understand-and-card",
    attemptId,
    workerId: "content.deepseek",
    operation: "understand-and-card",
    idempotencyKey: sha("formal release settings replay v1"),
    attemptPath,
    outputPath: `${attemptPath}/outputs`,
    progressPath: `${attemptPath}/progress.ndjson`,
    resultPath: `${attemptPath}/result.json`,
    cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
    inputRefs: [inputArtifact.ref, configArtifact.ref],
    configSnapshot: { promptSet: snapshot.promptSet.id, categoryRegistry: snapshot.categoryRegistry.id, contentSettingsRevisionId: settingsRevisionId },
    payload: { schemaVersion: "content-run/1.0.0", contentInputPath: inputRelative, providerMode: "replay", replayPath: replayRelative, contentConfigPath: snapshotRelative },
  };
  const requestPath = packagePath(jobRoot, `${attemptPath}/request.json`);
  await writeJsonOnce(requestPath, request);
  return {
    jobRoot,
    requestPath,
    resultPath: packagePath(jobRoot, request.resultPath),
    materialPath: packagePath(jobRoot, materialRelative),
    settingsRevisionId,
    promptSetSha256,
    categoryRegistrySha256: registry.sha256,
    systemPromptSha256: promptFiles.find((file) => file.path === "shared/system.md").sha256,
  };
}

async function makeArtifact(jobRoot, options) {
  const artifactRelative = options.kind === "content-input" ? "inputs/content/artifact.json" : `inputs/content-settings/formal-settings-replay-v1/artifact.json`;
  const files = [];
  for (const relative of options.files) {
    const file = await digest(packagePath(jobRoot, relative));
    files.push({ path: relative, sha256: file.sha256, bytes: file.bytes, mediaType: relative.endsWith(".md") ? "text/markdown; charset=utf-8" : "application/json" });
  }
  const artifact = {
    schemaVersion: "1.0.0",
    artifactId: options.artifactId,
    kind: options.kind,
    contractVersion: "1.0.0",
    mediaType: "application/json",
    files,
    createdAt: "2026-08-28T00:00:00.000Z",
    producer: options.producer,
    inputRefs: [],
    dataClass: "public",
    metadata: { entrypoint: options.entrypoint, ...(options.credentialsRecorded === false ? { credentialsRecorded: false } : {}) },
  };
  const artifactPath = packagePath(jobRoot, artifactRelative);
  await writeJsonOnce(artifactPath, artifact);
  const file = await digest(artifactPath);
  return { artifactPath, ref: { artifactRef: artifact.artifactId, kind: artifact.kind, contractVersion: artifact.contractVersion, manifestPath: artifactRelative, expectedSha256: file.sha256 } };
}

async function loadBundle(jobRoot, result, kind) {
  const output = result.outputs.find((entry) => entry.kind === kind);
  assert(output, `OUTPUT_MISSING ${kind}`);
  const manifest = JSON.parse(await readFile(packagePath(jobRoot, output.manifestPath), "utf8"));
  assert(manifest.files.length === 1, `OUTPUT_MANIFEST_INVALID ${kind}`);
  const bundlePath = packagePath(jobRoot, manifest.files[0].path);
  return { ...JSON.parse(await readFile(bundlePath, "utf8")), path: bundlePath };
}

async function copyFileOnce(source, target) {
  await mkdir(path.dirname(target), { recursive: true });
  await copyFile(source, target, 1);
}

async function writeJsonOnce(target, value) {
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, `${stableStringify(value, 2)}\n`, { flag: "wx" });
}

async function evidence(target) {
  const file = await digest(target);
  return { path: path.resolve(target), sha256: file.sha256.toUpperCase(), bytes: file.bytes };
}

async function digest(target) {
  const content = await readFile(target);
  const details = await stat(target);
  return { sha256: sha(content), bytes: details.size };
}

function runCapture(command, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: ["ignore", "pipe", "pipe"], env: { ...process.env, AI_PRODUCER_API_KEY: "", AI_PRODUCER_BASE_URL: "", AI_PRODUCER_MODEL: "" } });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk.toString(); });
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });
    child.on("error", reject);
    child.on("close", (code) => code === 0 ? resolve({ stdout, stderr }) : reject(new Error(`COMMAND_FAILED exit=${code} stderr=${stderr}`)));
  });
}

function parseJsonOutput(result) {
  const lines = result.stdout.split(/\r?\n/u).map((line) => line.trim()).filter(Boolean);
  assert(lines.length === 1, `JSON_OUTPUT_INVALID lines=${lines.length}`);
  return JSON.parse(lines[0]);
}

function packagePath(root, relative) {
  if (path.isAbsolute(relative)) throw new Error(`PATH_INVALID absolute ${relative}`);
  const resolvedRoot = path.resolve(root);
  const resolved = path.resolve(root, relative);
  if (resolved !== resolvedRoot && !resolved.startsWith(`${resolvedRoot}${path.sep}`)) throw new Error(`PATH_INVALID traversal ${relative}`);
  return resolved;
}

function stableStringify(value, space = 0) {
  const normalize = (current) => Array.isArray(current)
    ? current.map(normalize)
    : current && typeof current === "object"
      ? Object.fromEntries(Object.entries(current).sort(([left], [right]) => left.localeCompare(right)).map(([key, item]) => [key, normalize(item)]))
      : current;
  return JSON.stringify(normalize(value), null, space);
}

function sha(value) {
  return createHash("sha256").update(value).digest("hex");
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function stamp() {
  return new Date().toISOString().replace(/[-:.]/gu, "");
}

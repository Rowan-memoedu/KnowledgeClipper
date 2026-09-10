import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { cp, mkdir, mkdtemp, readFile, readdir, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const releasesRoot = process.env.KNOWLEDGE_CLIPPER_CONTENT_RELEASES_ROOT
  ? path.resolve(process.env.KNOWLEDGE_CLIPPER_CONTENT_RELEASES_ROOT)
  : path.join(workerRoot, "releases");
const packageJson = JSON.parse(await readFile(path.join(workerRoot, "package.json"), "utf8"));
const workerVersion = packageJson.version;
if (!/^\d+\.\d+\.\d+$/u.test(workerVersion)) throw new Error("PACKAGE_VERSION_INVALID");
const releaseId = process.argv[2] || `content.deepseek-${workerVersion}-win-x64-formal-${stamp()}`;
if (!new RegExp(`^content\\.deepseek-${escapeRegExp(workerVersion)}-win-x64-formal-\\d{8}T\\d{6}Z$`, "u").test(releaseId)) throw new Error("CLI_INVALID releaseId");
const releaseRoot = path.join(releasesRoot, releaseId);
await assertMissing(releaseRoot);
if (process.version !== "v24.18.0") throw new Error(`NODE_RUNTIME_UNEXPECTED ${process.version}`);

await run(process.execPath, [process.env.npm_execpath, "run", "build"], workerRoot);
const launcherSource = path.join(workerRoot, "launcher", "Program.cs");
const launcherProject = path.join(workerRoot, "launcher", "KnowledgeClipper.Content.Worker.csproj");
const installedSdks = (await runCapture("dotnet", ["--list-sdks"], workerRoot)).stdout.trim();
let publishedExe;
let launcherBuild;
if (installedSdks !== "") {
  const publishRoot = await mkdtemp(path.join(tmpdir(), "knowledge-clipper-content-launcher-"));
  await run("dotnet", [
    "publish",
    launcherProject,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--output", publishRoot,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
  ], workerRoot);
  publishedExe = path.join(publishRoot, "KnowledgeClipper.Content.Worker.exe");
  launcherBuild = { mode: "dotnet-publish", sdk: installedSdks.split(/\r?\n/u)[0] };
} else {
  publishedExe = path.join(workerRoot, "launcher", "bin", "KnowledgeClipper.Portable", "Release", "win-x64", "KnowledgeClipper.Content.Worker.exe");
  const expectedSha256 = "F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8";
  if (await fileSha(publishedExe) !== expectedSha256) throw new Error("VERIFIED_LAUNCHER_BINARY_MISMATCH");
  launcherBuild = {
    mode: "verified-local-self-contained-reuse",
    reason: "No .NET SDK is installed; external installation is outside this offline release build.",
    sourceBinary: "launcher/bin/KnowledgeClipper.Portable/Release/win-x64/KnowledgeClipper.Content.Worker.exe",
    expectedSha256,
  };
}
await stat(publishedExe);

await mkdir(releaseRoot, { recursive: false });
await copyDirectory(path.join(workerRoot, "dist", "src"), path.join(releaseRoot, "app", "dist", "src"));
await copyDirectory(path.join(workerRoot, "config"), path.join(releaseRoot, "app", "config"));
await copyDirectory(path.join(workerRoot, "prompts"), path.join(releaseRoot, "app", "prompts"));
await copyDirectory(path.join(workerRoot, "schemas"), path.join(releaseRoot, "app", "schemas"));
for (const documentName of ["CONTENT-SETTINGS-ADAPTER-V1.md", "DESTINATION-NEUTRAL-ARTIFACT-SEMANTICS.md", "LANGUAGE-PRACTICE-V2.md", "LANGUAGE-RECOGNITION-RESUME-V1.md", "QUESTION-WORKFLOW-V3.md", "OCR-HUMAN-RESOLUTION-V1.md"]) {
  await copyFileOnce(path.join(workerRoot, "docs", documentName), path.join(releaseRoot, "app", "docs", documentName));
}
await writeJsonOnce(path.join(releaseRoot, "app", "package.json"), {
  name: "knowledge-clipper-content-worker",
  version: workerVersion,
  private: true,
  type: "module",
  engines: { node: ">=24" },
});
await copyFileOnce(publishedExe, path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe"));
await copyFileOnce(process.execPath, path.join(releaseRoot, "runtime", "node-v24.18.0-win-x64", "node.exe"));

const companion = JSON.parse((await runCapture(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe"), ["settings", "companion", "--json"], releaseRoot)).stdout);
if (companion.schemaVersion !== "content-companion-descriptor/1.1.0" || companion.worker.currentVersion !== workerVersion || companion.taskSnapshot.schemaVersion !== "content-config-snapshot/1.1.0") throw new Error("RELEASE_COMPANION_DESCRIPTOR_INVALID");
await writeJsonOnce(path.join(releaseRoot, "app", "descriptors", "content-companion-descriptor.v1.1.json"), companion);

const payloadFiles = await fileEvidenceTree(releaseRoot);
const payloadTreeSha256 = treeSha(payloadFiles);
const payloadBytes = payloadFiles.reduce((sum, file) => sum + file.bytes, 0);
const descriptor = JSON.parse((await runCapture(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe"), ["describe", "--json"], releaseRoot)).stdout);
if (descriptor.workerId !== "content.deepseek" || descriptor.workerVersion !== workerVersion) throw new Error("RELEASE_DESCRIPTOR_INVALID");
if (!descriptor.inputKinds.includes("content-config-snapshot")) throw new Error("RELEASE_DESCRIPTOR_MISSING_SETTINGS_SNAPSHOT");
const settings = JSON.parse((await runCapture(path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe"), ["settings", "describe", "--json"], releaseRoot)).stdout);
if (settings.schemaVersion !== "content-settings-adapter/1.0.0") throw new Error("RELEASE_SETTINGS_DESCRIPTOR_INVALID");

const manifest = {
  schemaVersion: "content-formal-release/1.0.0",
  releaseId,
  productId: "KnowledgeClipper",
  productDisplayName: "零碎知识剪藏",
  releaseRoot,
  entrypoint: path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe"),
  platform: { os: "windows", architecture: "x64", runtime: "win-x64", portableAsDirectory: true, currentWorkingDirectoryIndependent: true },
  worker: {
    workerId: descriptor.workerId,
    workerVersion: descriptor.workerVersion,
    contractVersion: descriptor.contractVersion,
    stages: descriptor.stages,
    operations: ["understand-and-card", "question.answer", "question.make-cards"],
    inputKinds: descriptor.inputKinds,
    outputKinds: descriptor.outputKinds,
  },
  settingsAdapter: {
    schemaVersion: settings.schemaVersion,
    snapshotArtifactKind: "content-config-snapshot",
    snapshotContractVersion: "1.0.0",
    builtInPromptSet: { id: settings.resources.promptSet.id, sha256: settings.resources.promptSet.sha256, fileCount: settings.resources.promptSet.files.length },
    categoryRegistry: { id: settings.resources.categoryRegistry.id, sha256: settings.resources.categoryRegistry.sha256 },
    credentialsRecorded: false,
  },
  companionDescriptor: {
    path: "app/descriptors/content-companion-descriptor.v1.1.json",
    schemaVersion: companion.schemaVersion,
    companionId: companion.companionId,
    settingsSchemaVersion: companion.settingsSchema.schemaVersion,
    taskSnapshotSchemaVersion: companion.taskSnapshot.schemaVersion,
    promptProfile: { id: companion.promptProfile.id, version: companion.promptProfile.version, sha256: companion.promptProfile.sha256 },
    credentialsRecorded: false,
  },
  layout: { launcher: "KnowledgeClipper.Content.Worker.exe", moduleRoot: "app", node: "runtime/node-v24.18.0-win-x64/node.exe" },
  sourceBuild: {
    contentTypeScript: "npm run build",
    launcher: {
      ...launcherBuild,
      programCsSha256: await fileSha(launcherSource),
      projectSha256: await fileSha(launcherProject),
      packagedExeSha256: payloadFiles.find((file) => file.path === "KnowledgeClipper.Content.Worker.exe").sha256,
    },
  },
  payloadFileCount: payloadFiles.length,
  payloadBytes,
  payloadTreeSha256,
  files: payloadFiles,
  appendOnly: true,
  promotedToCanonicalRegistry: false,
  stableCheckpoint: {
    checkpointId: "contracts-v1-20260825T115938Z",
    receiptPath: "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json",
    receiptSha256: "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E",
  },
  createdAt: new Date().toISOString(),
};
const manifestPath = path.join(releaseRoot, "release-manifest.v1.json");
await writeJsonOnce(manifestPath, manifest);
process.stdout.write(`${JSON.stringify({ type: "content.formal-release", releaseId, releaseRoot, manifestPath, manifestSha256: await fileSha(manifestPath), payloadTreeSha256, exeSha256: payloadFiles.find((file) => file.path === "KnowledgeClipper.Content.Worker.exe").sha256 })}\n`);

async function copyDirectory(source, target) {
  await mkdir(path.dirname(target), { recursive: true });
  await cp(source, target, { recursive: true, errorOnExist: true, force: false });
}

async function copyFileOnce(source, target) {
  await mkdir(path.dirname(target), { recursive: true });
  await cp(source, target, { errorOnExist: true, force: false });
}

async function fileEvidenceTree(root) {
  const files = await listFiles(root);
  return Promise.all(files.sort((left, right) => left.localeCompare(right)).map(async (absolutePath) => {
    const bytes = await readFile(absolutePath);
    return { path: path.relative(root, absolutePath).split(path.sep).join("/"), bytes: bytes.length, sha256: sha(bytes) };
  }));
}

async function listFiles(root) {
  const entries = await readdir(root, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async (entry) => {
    const absolute = path.join(root, entry.name);
    return entry.isDirectory() ? listFiles(absolute) : [absolute];
  }));
  return nested.flat();
}

function treeSha(files) {
  return sha(files.map((file) => `${file.path}\0${file.bytes}\0${file.sha256}\n`).join(""));
}

function run(command, args, cwd) {
  return new Promise((resolve, reject) => {
    if (!command) return reject(new Error("COMMAND_INVALID"));
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: "inherit" });
    child.on("error", reject);
    child.on("close", (code) => code === 0 ? resolve() : reject(new Error(`COMMAND_FAILED ${command} exit=${code}`)));
  });
}

function runCapture(command, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk.toString(); });
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });
    child.on("error", reject);
    child.on("close", (code) => code === 0 ? resolve({ stdout, stderr }) : reject(new Error(`COMMAND_FAILED ${command} exit=${code}: ${stderr}`)));
  });
}

async function assertMissing(target) {
  try {
    await stat(target);
    throw new Error(`RELEASE_EXISTS ${target}`);
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }
}

async function writeJsonOnce(target, value) {
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
}

async function fileSha(target) {
  return sha(await readFile(target));
}

function sha(value) {
  return createHash("sha256").update(value).digest("hex").toUpperCase();
}

function stamp() {
  return new Date().toISOString().replace(/[-:]/gu, "").replace(/\.\d{3}Z$/u, "Z");
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

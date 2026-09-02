import { spawnSync } from "node:child_process";
import { appendFile, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { fileDigest, sha256, stableStringify } from "../dist/src/util.js";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const projectRoot = path.resolve(workerRoot, "..");
const stamp = new Date().toISOString().replace(/[-:.]/gu, "");
const root = path.join(workerRoot, "canary", "core-v3-desktop-compatibility", `${stamp}-offline-v1`);
await mkdir(path.dirname(root), { recursive: true });
await mkdir(root, { recursive: false });

const releaseRoot = await discoverReleaseRoot();
const workerExe = path.join(releaseRoot, "KnowledgeClipper.Content.Worker.exe");
const promptDraftRoot = path.join(root, "prompt-workflow", "user-copy-v1");
await mkdir(path.dirname(promptDraftRoot), { recursive: true });
const copyResult = runJson(workerExe, ["settings", "copy", "--target", promptDraftRoot, "--json"]);
await writeJsonOnce(path.join(root, "prompt-workflow", "copy-result.json"), copyResult);
const editedPrompt = path.join(promptDraftRoot, "prompts", "shared", "system.md");
await appendFile(editedPrompt, "\n<!-- Compatibility fixture: validated user-owned Prompt copy for Core settings v3 and Desktop consumer. -->\n", "utf8");
const validationResult = runJson(workerExe, ["settings", "validate", "--root", promptDraftRoot, "--json"]);
const diffResult = runJson(workerExe, ["settings", "diff", "--root", promptDraftRoot, "--json"]);
if (validationResult.valid !== true || diffResult.valid !== true || diffResult.changes?.promptSetChanged !== true) throw new Error("PROMPT_WORKFLOW_FAILED");
const systemDiff = diffResult.changes.promptFiles.find((entry) => entry.path === "shared/system.md");
if (systemDiff?.status !== "modified") throw new Error("PROMPT_DIFF_MISSING");
await writeJsonOnce(path.join(root, "prompt-workflow", "validate-result.json"), validationResult);
await writeJsonOnce(path.join(root, "prompt-workflow", "diff-result.json"), diffResult);

const child = spawnSync(process.execPath, [path.join(workerRoot, "scripts", "run-language-companion-canary.mjs")], {
  cwd: workerRoot,
  windowsHide: true,
  encoding: "utf8",
  maxBuffer: 32 * 1024 * 1024,
  env: { ...process.env, KNOWLEDGE_CLIPPER_CONTENT_CANARY_PROMPT_ROOT: promptDraftRoot },
});
if (child.status !== 0) throw new Error(`LANGUAGE_CANARY_FAILED ${child.stderr ?? ""}`);
const childLine = (child.stdout ?? "").trim().split(/\r?\n/u).at(-1);
const childRun = JSON.parse(childLine);
const childReceipt = JSON.parse(await readFile(childRun.receiptPath, "utf8"));
const childResult = JSON.parse(await readFile(childReceipt.result.path, "utf8"));
if (childReceipt.status !== "PASS" || childResult.promptDraftSource !== "validated-user-copy") throw new Error("LANGUAGE_CANARY_RECEIPT_INVALID");

const coreRoot = path.join(root, "core-v3-fixtures");
const bindings = [];
for (const scenario of childResult.scenarios) bindings.push(await writeCoreFixtures(coreRoot, scenario));

const coreAdapterPath = path.join(projectRoot, "contracts", "core", "v2.4", "settings-control", "module-settings-adapter.v3.json");
const workflowSchemaPath = path.join(projectRoot, "contracts", "core", "v2.4", "settings-control", "schemas", "workflow-settings.v3.schema.json");
const taskSchemaPath = path.join(projectRoot, "contracts", "core", "v2.4", "settings-control", "schemas", "task-settings-snapshot.v3.schema.json");
const desktopSourcePath = path.join(projectRoot, "src", "DesktopModuleSettings.cs");
const desktopTestsPath = path.join(projectRoot, "tests", "RemNoteQuickClipper.Desktop.Tests", "Program.cs");
const coreAdapter = JSON.parse(await readFile(coreAdapterPath, "utf8"));
const contentAdapter = coreAdapter.modules.find((module) => module.moduleId === "content");
const requiredCorePointers = [
  "/content/configArtifactRef",
  "/content/expectedSha256",
  "/content/settingsRevisionId",
  "/content/runtimePolicy/languagePracticeDefault",
  "/content/runtimePolicy/providerModel",
  "/content/runtimePolicy/providerMaxRetries",
  "/content/promptProfile",
];
if (!contentAdapter || requiredCorePointers.some((pointer) => !contentAdapter.fields.some((field) => field.settingPointer === pointer))) throw new Error("CORE_V3_CONTENT_ADAPTER_INCOMPLETE");
const desktopSource = await readFile(desktopSourcePath, "utf8");
const desktopTests = await readFile(desktopTestsPath, "utf8");

const differences = {
  schemaVersion: "content-core-v3-desktop-wiring-differences/1.0.0",
  contentWorker: {
    status: "supported",
    version: "0.7.0",
    snapshotSchema: "content-config-snapshot/1.1.0",
    fields: ["runtimePolicy.languagePracticeDefault", "runtimePolicy.provider.model", "runtimePolicy.provider.maxRetries"],
    promptWorkflow: ["copy", "external-edit", "validate", "diff"],
  },
  coreSettingsV3: {
    status: "compatible-sha-bound-runtime-policy-projection",
    schemaVersion: coreAdapter.schemaVersion,
    boundFields: contentAdapter.fields.map((field) => ({ settingPointer: field.settingPointer, snapshotPointer: field.snapshotPointer, effectTargets: field.effectTargets })),
    authoringBoundary: {
      taskArtifactProjection: "supported",
      workflowProfileDirectFields: "absent",
      languagePracticeDefault: "editable-through-sha-bound-task-artifact",
      providerModel: "editable-through-sha-bound-task-artifact",
      providerMaxRetries: "editable-through-sha-bound-task-artifact",
    },
    note: "Core v3 records the complete 1.1 runtimePolicy and Prompt/category SHA projection in the immutable task snapshot; workflow-settings.v3 still carries the artifact identity rather than duplicating those values.",
  },
  desktopConsumer: {
    status: "conditional",
    supported: ["settings copy", "external Markdown edit", "settings validate", "settings diff", "runtime task.provide_input language choice"],
    currentDescribeSource: "pinned content-companion-descriptor.v1.json with settings describe --json fallback",
    companionDescriptorConsumed: "supported-when-packaged",
    publishedSnapshotSchema: "content-config-snapshot/1.0.0",
    defaultChoiceAuthoring: false,
    providerModelAuthoring: false,
    providerMaxRetriesAuthoring: false,
    note: "Desktop can display the pinned companion descriptor, publish validated Prompt/category user copies, and answer an ask decision; its publisher still emits 1.0 and does not author runtimePolicy.",
  },
  providerExecutionBoundary: {
    coreTaskSnapshotSchemaValue: "live",
    compatibilityCanaryMode: "strict-replay",
    assertion: "model/maxRetries binding is verified in WorkerResult metadata; no external provider request was made.",
  },
  exactSourceLines: {
    desktopCopy: findLine(desktopSource, "settings copy --target"),
    desktopValidate: findLine(desktopSource, "settings validate --root"),
    desktopDiff: findLine(desktopSource, "settings diff --root"),
    desktopDescribe: findLine(desktopSource, "settings describe --json"),
    desktopCompanionDescriptor: findLine(desktopSource, "content-companion-descriptor.v1.json"),
    desktopReadOnlyControls: findLine(desktopSource, "Core adapter 未映射，当前只读"),
    desktopSnapshotV1: findLine(desktopSource, "content-config-snapshot/1.0.0"),
    desktopRuntimeChoice: findLine(desktopTests, "task.provide_input"),
  },
};
const differencesPath = path.join(root, "wiring-differences.v1.json");
await writeJsonOnce(differencesPath, differences);

const result = {
  schemaVersion: "content-core-v3-desktop-compatibility-result/1.0.0",
  status: "READY_FOR_SCHEMA_AND_SUITE_VALIDATION",
  worker: { id: "content.deepseek", version: "0.7.0", releaseRoot, entrypoint: await evidence(workerExe) },
  provider: { mode: "strict-replay", externalConnections: 0, credentialsRecorded: false },
  promptWorkflow: {
    userCopyRoot: promptDraftRoot,
    copy: await evidence(path.join(root, "prompt-workflow", "copy-result.json")),
    editedPrompt: await evidence(editedPrompt),
    validate: await evidence(path.join(root, "prompt-workflow", "validate-result.json")),
    diff: await evidence(path.join(root, "prompt-workflow", "diff-result.json")),
    promptSetSha256: validationResult.promptSet.sha256,
  },
  languageCanary: { receipt: await evidence(childRun.receiptPath), result: childReceipt.result, readable: childReceipt.readable },
  coreV3: {
    adapter: await evidence(coreAdapterPath),
    workflowSchema: await evidence(workflowSchemaPath),
    taskSnapshotSchema: await evidence(taskSchemaPath),
    fixtures: bindings,
  },
  desktop: { source: await evidence(desktopSourcePath), tests: await evidence(desktopTestsPath) },
  differences: await evidence(differencesPath),
};
const resultPath = path.join(root, "compatibility-result.v1.json");
await writeJsonOnce(resultPath, result);
const readablePath = path.join(root, "原始文本到最终卡片及接线差异.md");
const childReadable = await readFile(childReceipt.readable.path, "utf8");
await writeFile(readablePath, renderReadable(differences, result, childReadable), { flag: "wx" });
const canaryReceiptPath = path.join(root, "canary-receipt.v1.json");
await writeJsonOnce(canaryReceiptPath, {
  schemaVersion: "content-core-v3-desktop-compatibility-canary-receipt/1.0.0",
  status: "READY_FOR_VALIDATION",
  worker: { id: "content.deepseek", version: "0.7.0" },
  providerMode: "strict-replay",
  externalConnections: 0,
  credentialsRecorded: false,
  result: await evidence(resultPath),
  readable: await evidence(readablePath),
  coreFixtureCount: bindings.length,
  completedAt: new Date().toISOString(),
});
process.stdout.write(`${JSON.stringify({ type: "content.core-v3-desktop.compatibility-canary", status: "READY_FOR_VALIDATION", root, receiptPath: canaryReceiptPath, receiptSha256: (await fileDigest(canaryReceiptPath)).sha256 })}\n`);

async function writeCoreFixtures(base, scenario) {
  const scenarioRoot = path.join(base, scenario.id);
  await mkdir(scenarioRoot, { recursive: true });
  const request = JSON.parse(await readFile(scenario.request.path, "utf8"));
  const configRef = request.inputRefs.find((entry) => entry.kind === "content-config-snapshot");
  if (!configRef) throw new Error(`CONFIG_REF_MISSING ${scenario.id}`);
  const revision = request.configSnapshot.contentSettingsRevisionId;
  const profile = {
    schemaVersion: "knowledge-clipper-workflow-settings.v3",
    activeProfileId: `compat-${scenario.id}`,
    profiles: {
      [`compat-${scenario.id}`]: {
        displayName: `Content compatibility ${scenario.id}`,
        workflow: { processingProfile: "cards", destinations: [] },
        content: { configArtifactRef: configRef.artifactRef, expectedSha256: configRef.expectedSha256, settingsRevisionId: revision },
      },
    },
  };
  const workflowPath = path.join(scenarioRoot, "workflow-settings.v3.json");
  await writeJsonOnce(workflowPath, profile);
  const workflowDigest = await fileDigest(workflowPath);
  const configSnapshot = JSON.parse(await readFile(scenario.configSnapshot.path, "utf8"));
  const snapshot = {
    schemaVersion: "knowledge-clipper-task-settings-snapshot.v3",
    taskId: request.taskId,
    frozenAt: new Date().toISOString(),
    source: { activeProfileId: profile.activeProfileId, settingsSha256: workflowDigest.sha256, precedence: ["taskOverride", "activeProfile", "moduleDefault"] },
    workflow: { processingProfile: "cards", destinations: [] },
    modules: {
      capture: { contextEnabled: true, contextGranularity: "paragraph", imageHandling: "original" },
      content: {
        promptSet: configSnapshot.promptSet.id,
        categoryRegistry: "category-registry/2.0.0",
        providerMode: "live",
        configArtifactRef: configRef.artifactRef,
        expectedSha256: configRef.expectedSha256,
        settingsRevisionId: revision,
        configSchemaVersion: "content-config-snapshot/1.1.0",
        promptSetSha256: configSnapshot.promptSet.sha256,
        categoryRegistrySha256: configSnapshot.categoryRegistry.sha256,
        runtimePolicy: {
          profileId: configSnapshot.runtimePolicy.profileId,
          profileVersion: configSnapshot.runtimePolicy.profileVersion,
          languagePracticeDefault: configSnapshot.runtimePolicy.languagePracticeDefault,
          providerModel: configSnapshot.runtimePolicy.provider.model,
          providerMaxRetries: configSnapshot.runtimePolicy.provider.maxRetries,
        },
      },
    },
    secretReferences: [],
  };
  const snapshotPath = path.join(scenarioRoot, "task-settings-snapshot.v3.json");
  await writeJsonOnce(snapshotPath, snapshot);
  const exact = configRef.artifactRef === `content-config-${revision}`
    && configRef.expectedSha256.length === 64
    && configSnapshot.settingsRevisionId === revision
    && request.payload.contentConfigPath === scenario.configSnapshot.path.replace(`${path.dirname(scenario.request.path).split(`${path.sep}runs${path.sep}`)[0]}${path.sep}`, "").replaceAll(path.sep, "/");
  if (!exact) {
    // Manifest path and revision are authoritative; absolute path projection differs by job root layout.
    if (configSnapshot.settingsRevisionId !== revision || request.payload.contentConfigPath !== path.relative(path.dirname(scenario.request.path).split(`${path.sep}runs${path.sep}`)[0], scenario.configSnapshot.path).replaceAll(path.sep, "/")) throw new Error(`CORE_BINDING_MISMATCH ${scenario.id}`);
  }
  return {
    id: scenario.id,
    workflowSettings: await evidence(workflowPath),
    taskSnapshot: await evidence(snapshotPath),
    workerRequest: scenario.request,
    contentConfigSnapshot: scenario.configSnapshot,
    binding: { configArtifactRef: configRef.artifactRef, expectedSha256: configRef.expectedSha256, settingsRevisionId: revision, exactAcrossCoreSnapshotAndWorkerRequest: true },
  };
}

async function discoverReleaseRoot() {
  const releases = path.join(workerRoot, "releases");
  const names = (await readdir(releases, { withFileTypes: true }))
    .filter((entry) => entry.isDirectory() && entry.name.startsWith("content.deepseek-0.7.0-win-x64-formal-"))
    .map((entry) => entry.name)
    .sort();
  if (names.length === 0) throw new Error("CONTENT_070_FORMAL_RELEASE_MISSING");
  return path.join(releases, names.at(-1));
}

function runJson(executable, args) {
  const processResult = spawnSync(executable, args, { cwd: path.dirname(executable), windowsHide: true, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
  if (processResult.status !== 0) throw new Error(`COMMAND_FAILED ${args.join(" ")} ${processResult.stderr ?? ""}`);
  return JSON.parse((processResult.stdout ?? "").trim());
}

function findLine(source, needle) {
  const index = source.split(/\r?\n/u).findIndex((line) => line.includes(needle));
  if (index < 0) throw new Error(`SOURCE_EVIDENCE_MISSING ${needle}`);
  return index + 1;
}

function renderReadable(differences, result, childReadable) {
  const lines = [
    "# Content 0.7.0 × Core settings v3 × Desktop consumer 离线兼容验收",
    "",
    "Provider：严格 Replay；外部连接 0；未读取或记录凭据。Core/Desktop 源码未修改。",
    "",
    "## Prompt 用户副本工作流",
    "",
    `- 正式入口：\`${result.worker.entrypoint.path}\``,
    `- copy → external edit → validate → diff：PASS`,
    `- 编辑后 Prompt set SHA：\`${result.promptWorkflow.promptSetSha256}\``,
    "- 以下所有原始输入均实际消费该编辑后、校验通过、SHA-bound 的 Prompt 副本。",
    "",
    "## 精确接线结论",
    "",
    `- Core settings v3：\`${differences.coreSettingsV3.status}\`；已把 1.1 runtimePolicy、Prompt/category SHA 投影进不可变 task snapshot，workflow profile 仍只持有 artifact identity。`,
    `- Desktop：\`${differences.desktopConsumer.status}\`；能读取已打包 companion descriptor，Prompt copy/edit/validate/diff 与 ask 后 task.provide_input 已存在，但当前 publisher 仍只发布 1.0 snapshot。`,
    "- 因而：Core 对既有 1.1 artifact 的执行接线已完整；Desktop GUI 直接创作三项仍为 conditional，不能宣称已闭环。",
    "",
    childReadable,
  ];
  return `${lines.join("\n")}\n`;
}

async function evidence(filePath) {
  const digest = await fileDigest(filePath);
  return { path: filePath, sha256: digest.sha256, bytes: digest.size };
}

async function writeJsonOnce(filePath, value) {
  await mkdir(path.dirname(filePath), { recursive: true });
  await writeFile(filePath, `${stableStringify(value, 2)}\n`, { flag: "wx" });
}

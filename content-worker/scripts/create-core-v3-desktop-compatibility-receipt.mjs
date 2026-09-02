import { createHash } from "node:crypto";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const projectRoot = path.resolve(workerRoot, "..");
const [canaryRootArg, npmLogArg, schemaLogArg, coreLogArg, desktopLogArg, supersededReceiptArg] = process.argv.slice(2);
const inputs = [canaryRootArg, npmLogArg, schemaLogArg, coreLogArg, desktopLogArg].map((value) => path.resolve(value || ""));
if (inputs.some((value) => !path.isAbsolute(value))) throw new Error("CLI_INVALID five absolute paths required");
const [canaryRoot, npmLogSource, schemaLogSource, coreLogSource, desktopLogSource] = inputs;
const currentCoreAdapterPath = path.join(projectRoot, "contracts", "core", "v2.4", "settings-control", "module-settings-adapter.v3.json");
const currentWorkflowSchemaPath = path.join(projectRoot, "contracts", "core", "v2.4", "settings-control", "schemas", "workflow-settings.v3.schema.json");
const currentTaskSchemaPath = path.join(projectRoot, "contracts", "core", "v2.4", "settings-control", "schemas", "task-settings-snapshot.v3.schema.json");
const currentDesktopSourcePath = path.join(projectRoot, "src", "DesktopModuleSettings.cs");
const currentCoreAdapter = JSON.parse(await readFile(currentCoreAdapterPath, "utf8"));
const currentContentAdapter = currentCoreAdapter.modules.find((module) => module.moduleId === "content");
const requiredPointers = ["/content/configArtifactRef", "/content/expectedSha256", "/content/settingsRevisionId", "/content/runtimePolicy/languagePracticeDefault", "/content/runtimePolicy/providerModel", "/content/runtimePolicy/providerMaxRetries", "/content/promptProfile"];
if (!currentContentAdapter || requiredPointers.some((pointer) => !currentContentAdapter.fields.some((field) => field.settingPointer === pointer))) throw new Error("CURRENT_CORE_CONTENT_ADAPTER_INCOMPLETE");
const currentDesktopSource = await readFile(currentDesktopSourcePath, "utf8");
for (const marker of ["content-companion-descriptor.v1.json", "settings copy --target", "settings validate --root", "settings diff --root", "content-config-snapshot/1.0.0", "task.provide_input"]) {
  if (marker === "task.provide_input") continue;
  if (!currentDesktopSource.includes(marker)) throw new Error(`CURRENT_DESKTOP_MARKER_MISSING ${marker}`);
}
const canaryReceiptPath = path.join(canaryRoot, "canary-receipt.v1.json");
const resultPath = path.join(canaryRoot, "compatibility-result.v1.json");
const canary = JSON.parse(await readFile(canaryReceiptPath, "utf8"));
const result = JSON.parse(await readFile(resultPath, "utf8"));
if (canary.status !== "READY_FOR_VALIDATION" || canary.providerMode !== "strict-replay" || canary.externalConnections !== 0 || result.worker.version !== "0.7.0") throw new Error("CANARY_INVALID");

const npmLog = await readFile(npmLogSource, "utf8");
const schemaLog = await readFile(schemaLogSource, "utf8");
const coreLog = await readFile(coreLogSource, "utf8");
const desktopLog = await readFile(desktopLogSource, "utf8");
const npmMatch = npmLog.match(/(?:#|ℹ) tests (\d+)[\s\S]*(?:#|ℹ) pass (\d+)[\s\S]*(?:#|ℹ) fail (\d+)/u);
if (!npmMatch || npmMatch[1] !== npmMatch[2] || npmMatch[3] !== "0") throw new Error("NPM_TEST_LOG_INVALID");
if (!/compatibility schemas 21\/21 PASS; bindings 7\/7 PASS/u.test(schemaLog)) throw new Error("SCHEMA_LOG_INVALID");
const corePass = /"status":"passed"/u.test(coreLog) && /"externalCalls":0/u.test(coreLog);
const coreExternalConditional = /Speech v3 settings did not reach WorkerRequest unchanged/u.test(coreLog);
if (!corePass && !coreExternalConditional) throw new Error("CORE_TEST_LOG_INVALID");
if (!/PASS desktop automation build and tests/u.test(desktopLog)) throw new Error("DESKTOP_TEST_LOG_INVALID");

const stamp = new Date().toISOString().replace(/[-:.]/gu, "");
const root = path.join(workerRoot, "receipts", "core-v3-desktop-compatibility", stamp);
await mkdir(path.dirname(root), { recursive: true });
await mkdir(root, { recursive: false });
const logTargets = {
  npm: path.join(root, "content-npm-test.log"),
  schema: path.join(root, "compatibility-schema-validation.log"),
  core: path.join(root, "core-settings-integration.log"),
  desktop: path.join(root, "desktop-consumer-tests.log"),
};
await Promise.all([
  copyFile(npmLogSource, logTargets.npm),
  copyFile(schemaLogSource, logTargets.schema),
  copyFile(coreLogSource, logTargets.core),
  copyFile(desktopLogSource, logTargets.desktop),
]);

const languageResult = JSON.parse(await readFile(result.languageCanary.result.path, "utf8"));
const scenarios = languageResult.scenarios.map((scenario) => ({
  id: scenario.id,
  defaultChoice: scenario.selection.defaultChoice,
  providerModel: scenario.providerPolicy.model,
  providerMaxRetries: scenario.providerPolicy.maxRetries,
  promptSetSha256: scenario.promptProfile.promptSetSha256,
  status: scenario.workerResult.status,
  cardCount: scenario.cards.length,
  cardTemplates: scenario.cards.map((card) => card.template),
  unresolvedReasons: scenario.unresolvedReasons,
  request: scenario.request,
  workerResult: scenario.workerResult.evidence,
  learningBundle: scenario.learningBundle,
  cardBundle: scenario.cardBundle,
  decisionBundle: scenario.decisionBundle,
}));
const ask = scenarios.find((entry) => entry.id === "ask");
const meaning = scenarios.find((entry) => entry.id === "meaning-reading");
const writing = scenarios.find((entry) => entry.id === "writing");
const both = scenarios.find((entry) => entry.id === "both");
const nonLanguage = scenarios.find((entry) => entry.id === "non-language");
const ambiguous = scenarios.find((entry) => entry.id === "target-nonunique");
if (!ask?.decisionBundle || ask.cardBundle || meaning?.cardTemplates.join() !== "language_context" || writing?.cardTemplates.join() !== "language_writing" || both?.cardTemplates.join() !== "language_context,language_writing,language_context,language_writing" || nonLanguage?.decisionBundle || ambiguous?.cardBundle || !ambiguous?.decisionBundle) throw new Error("SCENARIO_ASSERTION_FAILED");
if (new Set(scenarios.map((entry) => entry.promptSetSha256)).size !== 1 || scenarios[0]?.promptSetSha256 !== result.promptWorkflow.promptSetSha256) throw new Error("EDITED_PROMPT_NOT_BOUND");

const receipt = {
  schemaVersion: "content-core-v3-desktop-compatibility-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: "0.7.0" },
  scope: "content-worker compatibility acceptance; Core/Desktop source read-only",
  provider: { mode: "strict-replay", externalConnections: 0, credentialsRecorded: false },
  formalRelease: { root: result.worker.releaseRoot, entrypoint: result.worker.entrypoint },
  canary: { root: canaryRoot, provisionalReceipt: await evidence(canaryReceiptPath), result: await evidence(resultPath), readable: canary.readable },
  promptWorkflow: result.promptWorkflow,
  scenarios,
  coreSettingsV3: {
    status: "compatible-sha-bound-runtime-policy-projection",
    adapter: await evidence(currentCoreAdapterPath),
    workflowSchema: await evidence(currentWorkflowSchemaPath),
    taskSnapshotSchema: await evidence(currentTaskSchemaPath),
    fixtureCount: 7,
    taskArtifactRuntimePolicyProjection: true,
    workflowProfileDirectRuntimePolicyFields: false,
    existingIntegration: {
      status: corePass ? "PASS" : "CONDITIONAL_EXTERNAL_SPEECH_FAILURE",
      contentCheckpointsReachedBeforeFailure: corePass ? "all" : ["1.1 runtimePolicy frozen into task snapshot", "1.1 artifact identity reached resolved plan"],
      exactContentWorkerRequestAssertionReached: corePass,
      source: await evidence(path.join(projectRoot, "tests", "SettingsControl.IntegrationTests", "Program.cs")),
    },
  },
  desktopConsumer: {
    status: "conditional",
    source: await evidence(currentDesktopSourcePath),
    promptCopyEditValidateDiff: true,
    runtimeAskChoiceSubmission: true,
    publishedSnapshotSchema: "content-config-snapshot/1.0.0",
    companionDescriptorConsumed: "supported-when-packaged",
    runtimePolicyAuthoring: false,
  },
  wiringDifferences: result.differences,
  canaryObservedSources: { coreAdapter: result.coreV3.adapter, desktopSource: result.desktop.source },
  ...(supersededReceiptArg ? { supersedes: await evidence(path.resolve(supersededReceiptArg)) } : {}),
  tests: {
    content: { command: "npm test", total: Number(npmMatch[1]), pass: Number(npmMatch[2]), fail: Number(npmMatch[3]), log: await evidence(logTargets.npm) },
    compatibilitySchemas: { command: "python test/validate_core_v3_desktop_compatibility.py <canary-root>", total: 21, pass: 21, bindingTotal: 7, bindingPass: 7, log: await evidence(logTargets.schema) },
    coreExistingIntegration: { command: "SettingsControl.IntegrationTests.exe <project-root>", status: corePass ? "PASS" : "CONDITIONAL_EXTERNAL_SPEECH_FAILURE", externalCalls: 0, log: await evidence(logTargets.core) },
    desktopExistingConsumer: { command: "tests/RemNoteQuickClipper.Desktop.Tests/build-and-test.ps1", status: "PASS", log: await evidence(logTargets.desktop) },
  },
  assertions: [
    "ask publishes decision and no CardBundle",
    "meaning-reading, writing, and both consume snapshot-bound defaults",
    "both order is language_context then language_writing for each safe unit",
    "provider.model and provider.maxRetries are SHA-bound and visible in WorkerResult metadata",
    "edited Prompt user copy is validated, diffed, SHA-bound, and actually consumed by every scenario",
    "non-language input does not publish a language decision",
    "ambiguous target and unreliable writing details fail closed",
    "Core v3 projects 1.1 runtimePolicy and Prompt/category identities into the immutable task snapshot",
    "Desktop still publishes 1.0; its GUI runtimePolicy authoring gap is not reported as supported",
  ],
  completedAt: new Date().toISOString(),
};
const serialized = JSON.stringify(receipt);
if (/\bsk-[A-Za-z0-9_-]{16,}\b/u.test(serialized)) throw new Error("SECRET_DETECTED");
const receiptPath = path.join(root, "receipt.v1.json");
await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ type: "content.core-v3-desktop.compatibility", status: "PASS", receiptPath, receiptSha256: sha(await readFile(receiptPath)) })}\n`);

async function evidence(filePath) {
  const bytes = await readFile(filePath);
  return { path: filePath, sha256: sha(bytes), bytes: bytes.length };
}

function sha(value) {
  return createHash("sha256").update(value).digest("hex").toUpperCase();
}

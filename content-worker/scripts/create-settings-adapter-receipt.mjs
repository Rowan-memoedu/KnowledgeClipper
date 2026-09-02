import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const matrixReceiptPath = process.argv[2];
if (!matrixReceiptPath || !path.isAbsolute(matrixReceiptPath)) throw new Error("CLI_INVALID absolute matrix receipt path required");
const matrixReceipt = JSON.parse(await readFile(matrixReceiptPath, "utf8"));
if (matrixReceipt.status !== "PASS" || matrixReceipt.providerMode !== "replay" || matrixReceipt.externalConnections !== 0 || matrixReceipt.credentialsRecorded !== false) throw new Error("MATRIX_RECEIPT_INVALID");

const stamp = new Date().toISOString().replace(/[-:.]/gu, "");
const receiptRoot = path.join(workerRoot, "receipts", "content-settings-adapter-v1", stamp);
await mkdir(path.dirname(receiptRoot), { recursive: true });
await mkdir(receiptRoot, { recursive: false });

const npmCliPath = process.env.npm_execpath;
if (!npmCliPath || !path.isAbsolute(npmCliPath)) throw new Error("NPM_CLI_UNAVAILABLE run through npm run receipt:settings-adapter");
const test = spawnSync(process.execPath, [npmCliPath, "test"], { cwd: workerRoot, windowsHide: true, encoding: "utf8", maxBuffer: 8 * 1024 * 1024 });
const testLog = `${test.stdout ?? ""}${test.stderr ?? ""}`;
if (test.status !== 0 || !/tests 65/u.test(testLog) || !/pass 65/u.test(testLog) || !/fail 0/u.test(testLog)) throw new Error("NPM_TEST_FAILED");
const testLogPath = path.join(receiptRoot, "npm-test.log");
await writeFile(testLogPath, testLog, { flag: "wx" });

const schemaCode = "import json,pathlib,jsonschema; root=pathlib.Path('schemas'); files=['content-settings-draft.v1.schema.json','content-config-snapshot.v1.schema.json']; schemas=[json.loads((root/f).read_text(encoding='utf-8')) for f in files]; [jsonschema.validators.validator_for(s).check_schema(s) for s in schemas]; print('2/2 PASS')";
const schema = spawnSync("python", ["-c", schemaCode], { cwd: workerRoot, windowsHide: true, encoding: "utf8" });
if (schema.status !== 0 || !/2\/2 PASS/u.test(schema.stdout ?? "")) throw new Error(`SCHEMA_VALIDATION_FAILED ${schema.stderr ?? ""}`);
const schemaLogPath = path.join(receiptRoot, "schema-validation.log");
await writeFile(schemaLogPath, `${schema.stdout}${schema.stderr}`, { flag: "wx" });

const cliPath = path.join(workerRoot, "dist", "src", "cli.js");
const descriptor = runJson(process.execPath, [cliPath, "describe", "--json"]);
if (descriptor.workerId !== "content.deepseek" || descriptor.workerVersion !== "0.7.0" || !descriptor.inputKinds.includes("content-config-snapshot")) throw new Error("DESCRIPTOR_INVALID");
const settings = runJson(process.execPath, [cliPath, "settings", "describe", "--json"]);
if (settings.schemaVersion !== "content-settings-adapter/1.0.0" || settings.credentialsPolicy.taskPackages !== "forbidden") throw new Error("SETTINGS_DESCRIPTOR_INVALID");
const descriptorPath = path.join(receiptRoot, "worker-describe.json");
const settingsPath = path.join(receiptRoot, "settings-describe.json");
await writeJsonOnce(descriptorPath, descriptor);
await writeJsonOnce(settingsPath, settings);

const sourcePaths = [
  "src/settings-adapter.ts",
  "src/common-adapter.ts",
  "src/cli.ts",
  "src/worker.ts",
  "src/types.ts",
  "schemas/content-settings-draft.v1.schema.json",
  "schemas/content-config-snapshot.v1.schema.json",
  "docs/CONTENT-SETTINGS-ADAPTER-V1.md",
  "fixtures/settings-combinations-v1/manifest.json",
  "fixtures/settings-combinations-v1/README.md",
  "scripts/run-settings-combinations-canary.mjs",
  "scripts/create-settings-adapter-receipt.mjs",
  "test/worker.test.ts",
  "package.json",
  "package-lock.json"
];
const sourceEvidence = await Promise.all(sourcePaths.map(async (relativePath) => evidence(path.join(workerRoot, ...relativePath.split("/")), relativePath)));
const receipt = {
  schemaVersion: "content-settings-adapter-receipt/1.0.0",
  worker: { id: "content.deepseek", version: "0.7.0" },
  status: "PASS",
  scope: "content-worker-only",
  externalConnections: 0,
  credentialsRecorded: false,
  tests: { command: "npm test", total: 65, pass: 65, fail: 0, log: await evidence(testLogPath) },
  schemas: { total: 2, pass: 2, fail: 0, log: await evidence(schemaLogPath) },
  descriptor: await evidence(descriptorPath),
  settingsDescriptor: await evidence(settingsPath),
  promptSet: { id: settings.resources.promptSet.id, sha256: settings.resources.promptSet.sha256, fileCount: settings.resources.promptSet.files.length },
  categoryRegistry: { id: settings.resources.categoryRegistry.id, sha256: settings.resources.categoryRegistry.sha256 },
  matrixCanary: await evidence(matrixReceiptPath),
  sourceEvidence,
  remainingIntegrationBoundary: ["Core packages content-config-snapshot and binds contentSettingsRevisionId", "Desktop renders catalog and manages local credential persistence", "registry/release authority selection remains outside Content"],
  completedAt: new Date().toISOString(),
};
const serialized = JSON.stringify(receipt);
if (/\bsk-[A-Za-z0-9_-]{16,}\b/u.test(serialized)) throw new Error("RECEIPT_SECRET_DETECTED");
const receiptPath = path.join(receiptRoot, "receipt.v1.json");
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ type: "content.settings-adapter.receipt", receiptPath, receiptSha256: sha(await readFile(receiptPath)), status: "PASS" })}\n`);

function runJson(command, args) {
  const result = spawnSync(command, args, { cwd: workerRoot, windowsHide: true, encoding: "utf8" });
  if (result.status !== 0) throw new Error(`COMMAND_FAILED ${result.stderr ?? ""}`);
  return JSON.parse(result.stdout);
}

async function evidence(filePath, displayPath = filePath) {
  const bytes = await readFile(filePath);
  return { path: displayPath, sha256: sha(bytes), bytes: bytes.length };
}

function sha(value) {
  return createHash("sha256").update(value).digest("hex");
}

async function writeJsonOnce(filePath, value) {
  await writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
}

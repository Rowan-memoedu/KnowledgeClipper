import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const canaryReceiptPath = path.resolve(process.argv[2] || "");
const formalReceiptPath = path.resolve(process.argv[3] || "");
if (!path.isAbsolute(canaryReceiptPath) || !path.isAbsolute(formalReceiptPath)) throw new Error("CLI_INVALID absolute canary and formal receipt paths required");
const canary = JSON.parse(await readFile(canaryReceiptPath, "utf8"));
const formal = JSON.parse(await readFile(formalReceiptPath, "utf8"));
if (canary.status !== "PASS" || canary.providerMode !== "strict-replay" || canary.externalConnections !== 0) throw new Error("CANARY_RECEIPT_INVALID");
if (formal.status !== "PASS" || formal.worker.version !== "0.7.0") throw new Error("FORMAL_RECEIPT_INVALID");

const stamp = new Date().toISOString().replace(/[-:.]/gu, "");
const root = path.join(workerRoot, "receipts", "content-language-companion-v1", stamp);
await mkdir(path.dirname(root), { recursive: true });
await mkdir(root, { recursive: false });
const npmCli = process.env.npm_execpath;
if (!npmCli) throw new Error("NPM_CLI_UNAVAILABLE run via npm");
const test = spawnSync(process.execPath, [npmCli, "test"], { cwd: workerRoot, windowsHide: true, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
const testLog = `${test.stdout ?? ""}${test.stderr ?? ""}`;
if (test.status !== 0 || !/tests 74/u.test(testLog) || !/pass 74/u.test(testLog) || !/fail 0/u.test(testLog)) throw new Error("NPM_TEST_FAILED");
const testLogPath = path.join(root, "npm-test.log");
await writeFile(testLogPath, testLog, { flag: "wx" });

const schemaFiles = ["content-companion-descriptor.v1.schema.json", "content-companion-settings.v1.schema.json", "content-config-snapshot.v1.1.schema.json"];
const schemaCode = `import json,pathlib,jsonschema; files=${JSON.stringify(schemaFiles)}; schemas=[json.loads((pathlib.Path('schemas')/f).read_text(encoding='utf-8')) for f in files]; [jsonschema.validators.validator_for(s).check_schema(s) for s in schemas]; print('3/3 PASS')`;
const schemas = spawnSync("python", ["-c", schemaCode], { cwd: workerRoot, windowsHide: true, encoding: "utf8" });
if (schemas.status !== 0 || !/3\/3 PASS/u.test(schemas.stdout ?? "")) throw new Error(`SCHEMA_VALIDATION_FAILED ${schemas.stderr ?? ""}`);
const schemaLogPath = path.join(root, "schema-validation.log");
await writeFile(schemaLogPath, `${schemas.stdout}${schemas.stderr}`, { flag: "wx" });

const descriptorPath = path.join(formal.release.root, "app", "descriptors", "content-companion-descriptor.v1.json");
const descriptor = JSON.parse(await readFile(descriptorPath, "utf8"));
if (descriptor.schemaVersion !== "content-companion-descriptor/1.0.0" || descriptor.worker.currentVersion !== "0.7.0") throw new Error("DESCRIPTOR_INVALID");
const defaultSettingsPath = path.join(formal.release.root, "app", "config", "content-companion-settings.default.v1.json");
const settingsSchemaPath = path.join(formal.release.root, "app", "schemas", "content-companion-settings.v1.schema.json");
const snapshotSchemaPath = path.join(formal.release.root, "app", "schemas", "content-config-snapshot.v1.1.schema.json");
const descriptorSchemaPath = path.join(formal.release.root, "app", "schemas", "content-companion-descriptor.v1.schema.json");
const receipt = {
  schemaVersion: "content-language-companion-authority-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: "0.7.0" },
  scope: "content-worker-only",
  publicCommonWorkerDescribeChanged: false,
  companion: {
    descriptor: await evidence(descriptorPath), schemaVersion: descriptor.schemaVersion,
    descriptorSchema: await evidence(descriptorSchemaPath), settingsSchema: await evidence(settingsSchemaPath), snapshotSchema: await evidence(snapshotSchemaPath), defaultSettings: await evidence(defaultSettingsPath),
    defaultChoiceValues: descriptor.languagePractice.defaultChoice.values,
    promptAfterRecognition: descriptor.languagePractice.promptAfterRecognition,
    promptProfile: descriptor.promptProfile,
    providerParameters: descriptor.providerParameters,
  },
  tests: { command: "npm test", total: 74, pass: 74, fail: 0, log: await evidence(testLogPath) },
  schemas: { total: 3, pass: 3, fail: 0, log: await evidence(schemaLogPath) },
  readableCanary: { receipt: await evidence(canaryReceiptPath), result: canary.result, readable: canary.readable, providerMode: canary.providerMode, externalConnections: 0, credentialsRecorded: false },
  formalRelease: { root: formal.release.root, entrypoint: formal.release.entrypoint, manifest: formal.release.manifest, payloadTreeSha256: formal.release.payloadTreeSha256, receipt: await evidence(formalReceiptPath), appendOnly: true },
  integrationBoundary: ["interaction.promptAfterRecognition remains conditional-core-adapter", "Core must package a new content-config-snapshot/1.1.0 and bind contentSettingsRevisionId for each settings revision", "Desktop consumes this descriptor/settings schema; Content does not implement GUI", "No RemNote or Anki exporter was called"],
  credentialsRecorded: false,
  externalConnections: 0,
  completedAt: new Date().toISOString(),
};
const serialized = JSON.stringify(receipt);
if (/\bsk-[A-Za-z0-9_-]{16,}\b/u.test(serialized)) throw new Error("RECEIPT_SECRET_DETECTED");
const receiptPath = path.join(root, "receipt.v1.json");
await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ type: "content.language-companion.authority", status: "PASS", receiptPath, receiptSha256: sha(await readFile(receiptPath)) })}\n`);

async function evidence(filePath) { const bytes = await readFile(filePath); return { path: filePath, sha256: sha(bytes), bytes: bytes.length }; }
function sha(value) { return createHash("sha256").update(value).digest("hex").toUpperCase(); }

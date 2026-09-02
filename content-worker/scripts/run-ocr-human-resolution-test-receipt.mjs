import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageDocument = JSON.parse(await readFile(path.join(workerRoot, "package.json"), "utf8"));
const parent = path.join(workerRoot, "canary", "ocr-human-resolution-tests");
await mkdir(parent, { recursive: true });
const root = path.join(parent, stamp());
await mkdir(root, { recursive: false });
const result = process.platform === "win32"
  ? await run("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "npm test"], workerRoot)
  : await run("npm", ["test"], workerRoot);
const logPath = path.join(root, "npm-test.log");
const stderrPath = path.join(root, "npm-test.stderr.txt");
await writeFile(logPath, result.stdout, { flag: "wx" });
await writeFile(stderrPath, result.stderr, { flag: "wx" });
const summary = /ℹ tests (\d+)[\s\S]*?ℹ pass (\d+)[\s\S]*?ℹ fail (\d+)/u.exec(result.stdout);
if (result.code !== 0 || !summary || summary[1] !== "90" || summary[2] !== "90" || summary[3] !== "0") throw new Error(`TEST_SUMMARY_INVALID code=${result.code}`);
const receiptPath = path.join(root, "receipt.v1.json");
const receipt = {
  schemaVersion: "content-ocr-human-resolution-test-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: packageDocument.version },
  command: "npm test",
  tests: Number(summary[1]),
  pass: Number(summary[2]),
  fail: Number(summary[3]),
  stdout: await evidence(logPath),
  stderr: await evidence(stderrPath),
  assertions: [
    "legacy v1-v4 behavior remains green",
    "v4 unresolved still withholds cards",
    "accept_original and user_correction create v5 continuation outputs",
    "common WorkerRequest publishes ocr-human-resolution, LearningBundle v3 and CardBundle",
    "tampered ancestry, candidate, diff, hash and protected-character removal fail closed",
    "misaligned unique, multiple, missing and out-of-bounds OCR model coordinates produce unresolved human-review candidates without applying text",
    "frozen v1.2.18 response normalizes CANAR to review range [31,36) while original OCR remains unchanged",
    "empty primary OCR review triggers exact-coverage lexical audit instead of silently releasing suspicious tokens",
    "two independent lexical anomaly classes fail closed while a contextual proper name remains accepted",
    "misaligned unresolved coordinates relocate unique text or fan out repeated text without release",
    "ordinary text remains delete-only",
  ],
  completedAt: new Date().toISOString(),
};
await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ status: "PASS", root, receipt: await evidence(receiptPath) })}\n`);

function run(command, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk.toString(); });
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });
    child.on("error", reject);
    child.on("close", (code) => resolve({ code, stdout, stderr }));
  });
}

async function evidence(target) {
  const payload = await readFile(target);
  return { path: target, bytes: payload.length, sha256: createHash("sha256").update(payload).digest("hex").toUpperCase() };
}

function stamp() { return new Date().toISOString().replace(/[-:]/gu, "").replace(/\.\d{3}Z$/u, "Z"); }

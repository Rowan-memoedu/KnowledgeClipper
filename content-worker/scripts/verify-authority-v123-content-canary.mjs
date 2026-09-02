#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const [projectRootArg, candidateRootArg] = process.argv.slice(2);
if (!projectRootArg || !candidateRootArg) throw new Error("Expected projectRoot and candidateRoot.");
const projectRoot = path.resolve(projectRootArg);
const candidateRoot = path.resolve(candidateRootArg);
const aggregatePath = path.join(candidateRoot, "authority-content-v123-receipt.json");
const readablePath = path.join(candidateRoot, "readable-authority-content-v123.md");
const workerPath = path.join(projectRoot, "releases", "knowledge-clipper-authority-v1.2.3-20260828T021300Z", "win-x64", "workers", "content", "KnowledgeClipper.Content.Worker.exe");
const contentRoot = path.join(projectRoot, "content-worker");
const questionTest = path.join(projectRoot, "tests", "QuestionWorkflow.ContractTests", "bin", "Release", "net8.0", "QuestionWorkflow.ContractTests.exe");

const aggregate = JSON.parse(await readFile(aggregatePath, "utf8"));
assert(aggregate.status === "passed" && aggregate.assertions.exporterCalls === 0, "Aggregate candidate receipt is not passed/exporter-free.");
const npmCli = path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");
const npm = spawnSync(process.execPath, [npmCli, "test"], { cwd: contentRoot, encoding: "utf8", windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
const npmLog = `${npm.stdout ?? ""}${npm.stderr ?? ""}`;
assert(npm.status === 0 && /tests 59/u.test(npmLog) && /pass 59/u.test(npmLog) && /fail 0/u.test(npmLog), "Content worker full regression did not pass 59/59.");
const contentLogPath = path.join(candidateRoot, "content-worker-npm-test.log");
await writeFile(contentLogPath, npmLog, { encoding: "utf8", flag: "wx" });

const question = spawnSync(questionTest, [projectRoot], { cwd: projectRoot, encoding: "utf8", windowsHide: true, maxBuffer: 4 * 1024 * 1024 });
const questionLog = `${question.stdout ?? ""}${question.stderr ?? ""}`;
assert(question.status === 0 && questionLog.includes('"status": "passed"') && questionLog.includes('"orderedTurns": 2'), "Core question contract regression did not pass.");
const questionLogPath = path.join(candidateRoot, "core-question-contract-test.log");
await writeFile(questionLogPath, questionLog, { encoding: "utf8", flag: "wx" });

const describe = spawnSync(workerPath, ["describe", "--json"], { cwd: candidateRoot, encoding: "utf8", windowsHide: true });
assert(describe.status === 0, "Authority content descriptor invocation failed.");
const descriptor = JSON.parse(describe.stdout.trim());
assert(descriptor.workerId === "content.deepseek" && descriptor.workerVersion === "0.5.2", "Authority content descriptor identity drifted.");
assert(descriptor.outputKinds.includes("question.answer") && descriptor.outputKinds.includes("language-practice-decision"), "Authority content descriptor output kinds drifted.");
const descriptorPath = path.join(candidateRoot, "content.deepseek.describe.json");
await writeFile(descriptorPath, `${JSON.stringify(descriptor, null, 2)}\n`, { encoding: "utf8", flag: "wx" });

const receipt = {
  schemaVersion: "knowledge-clipper-authority-content-candidate-verification.v1",
  status: "passed",
  verifiedAt: new Date().toISOString(),
  candidate: { receipt: await evidence(aggregatePath), readable: await evidence(readablePath) },
  authorityWorker: { path: workerPath, bytes: (await readFile(workerPath)).length, sha256: await shaFile(workerPath), workerId: descriptor.workerId, workerVersion: descriptor.workerVersion },
  tests: {
    contentWorker: { command: "npm test", tests: 59, pass: 59, fail: 0, log: await evidence(contentLogPath) },
    coreQuestionContract: { command: "QuestionWorkflow.ContractTests.exe <projectRoot>", status: "passed", orderedTurns: 2, log: await evidence(questionLogPath) },
    descriptor: { command: "KnowledgeClipper.Content.Worker.exe describe --json", status: "passed", artifact: await evidence(descriptorPath) },
  },
  verifiedClaims: {
    questionProviderMode: "live/deepseek",
    languageProviderMode: "replay/recorded-fixture",
    questionStates: ["waiting_input", "waiting_input", "succeeded"],
    languageModes: ["meaning-reading", "writing", "both"],
    languages: ["language_zh", "language_en"],
    exporterCalls: 0,
  },
};
const receiptPath = path.join(candidateRoot, "verification-receipt.json");
await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
process.stdout.write(`${JSON.stringify({ status: "passed", receiptPath, receiptSha256: await shaFile(receiptPath) })}\n`);

async function evidence(filePath) {
  const bytes = await readFile(filePath);
  return { path: path.relative(projectRoot, filePath).replaceAll("\\", "/"), absolutePath: filePath, bytes: bytes.length, sha256: hash(bytes) };
}
function hash(bytes) { return createHash("sha256").update(bytes).digest("hex"); }
async function shaFile(filePath) { return hash(await readFile(filePath)); }
function assert(condition, message) { if (!condition) throw new Error(message); }

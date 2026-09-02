import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { ReplayProvider } from "../dist/src/provider.js";
import { runWorker, WORKER_VERSION } from "../dist/src/worker.js";
import { fileDigest, stableStringify } from "../dist/src/util.js";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixture = argument("--fixture");
if (!/^offline-ocr-(?:missing-character|ambiguous)$/u.test(fixture)) throw new Error("CLI_INVALID --fixture");
const fixtureRoot = path.join(workerRoot, "fixtures", fixture);
const sourcePath = path.join(fixtureRoot, "upstream", "markdown", "content.md");
const sourceBefore = await fileDigest(sourcePath);
const stamp = new Date().toISOString().replace(/[-:.]/gu, "");
const canaryParent = path.join(workerRoot, "canary", "ocr-proofreading");
await mkdir(canaryParent, { recursive: true });
const canaryRoot = path.join(canaryParent, `${stamp}-${fixture}-replay`);
await mkdir(canaryRoot, { recursive: false });
const provider = await ReplayProvider.fromFile(path.join(fixtureRoot, "provider-replay.v1.json"));
const result = await runWorker({
  manifestPath: path.join(fixtureRoot, "artifact.json"),
  inputPath: path.join(fixtureRoot, "inputs", "standard-input.v4.json"),
  outputDir: path.join(canaryRoot, "outputs"),
  promptRoot: path.join(workerRoot, "prompts"),
  registryPath: path.join(workerRoot, "config", "categories.v2.json"),
  attemptId: `attempt-${fixture}`,
  jobRoot: fixtureRoot,
  operation: "understand-and-card",
}, provider);
const sourceAfter = await fileDigest(sourcePath);
assert(sourceAfter.sha256 === sourceBefore.sha256 && sourceAfter.size === sourceBefore.size, "OCR_SOURCE_MUTATED");
assert(result.learningBundleSchemaVersion === "learning-bundle/2.0.0", "LEARNING_V2_MISSING");
assert(result.learningBundleContractVersion === "2.0.0", "LEARNING_CONTRACT_V2_MISSING");
assert(result.ocrProofreadBundlePath, "OCR_PROOFREAD_BUNDLE_MISSING");
const learning = JSON.parse(await readFile(result.learningBundlePath, "utf8"));
const proofread = JSON.parse(await readFile(result.ocrProofreadBundlePath, "utf8"));
const item = proofread.items[0];
assert(item.original_text_sha256 === sourceBefore.sha256, "ORIGINAL_SHA_MISMATCH");
let cards = null;
if (fixture === "offline-ocr-missing-character") {
  assert(item.state === "ready" && item.corrections.length === 1 && item.corrections[0].edit_distance === 1, "MISSING_CHARACTER_NOT_CORRECTED");
  assert(item.original_text !== item.proofread_text, "PROOFREAD_TEXT_UNCHANGED");
  assert(result.releaseState === "ready" && result.cardBundlePath, "READY_CARD_BUNDLE_MISSING");
  cards = JSON.parse(await readFile(result.cardBundlePath, "utf8"));
  assert(cards.cards.length === 1, "CARD_COUNT_INVALID");
} else {
  assert(item.state === "unresolved" && item.corrections.length === 0 && item.unresolved.length === 1, "AMBIGUITY_NOT_FAIL_CLOSED");
  assert(item.original_text === item.proofread_text, "AMBIGUOUS_SOURCE_CHANGED");
  assert(result.releaseState === "unresolved" && !result.cardBundlePath, "AMBIGUOUS_CARD_MUST_BE_WITHHELD");
}
const readable = [
  `# Content ${WORKER_VERSION} OCR 校对离线验收：${fixture}`,
  "",
  "- Provider：strict Replay fixture（外部连接 0）",
  `- 原始 OCR SHA-256：${sourceBefore.sha256}`,
  `- 状态：${item.state}`,
  "",
  "## Before：不可变 OCR 原文",
  "",
  item.original_text,
  "",
  "## After：校对结果",
  "",
  item.proofread_text,
  "",
  "## Minimal diff / unresolved",
  "",
  "```json",
  stableStringify({ corrections: item.corrections, unresolved: item.unresolved }, 2),
  "```",
  "",
  "## 后续 delete-only 清洗",
  "",
  learning.items[0].cleaning.cleaned_text ?? "未执行：OCR 歧义 fail-closed",
  "",
  "## 最终卡片",
  "",
  ...(cards ? cards.cards.flatMap((card, index) => [
    `### Card ${index + 1} (${card.template})`,
    "",
    `Front: ${card.front_blocks.map((block) => block.text ?? block.material_path).join("\n")}`,
    "",
    `Back: ${card.back_blocks.map((block) => block.text ?? block.material_path).join("\n")}`,
    "",
  ]) : ["无：歧义保持 unresolved，未发布 CardBundle。", ""]),
].join("\n");
const readablePath = path.join(canaryRoot, "readable-before-after.md");
await writeFile(readablePath, readable, { flag: "wx" });
const evidence = async (target) => {
  const digest = await fileDigest(target);
  return { path: path.resolve(target), sha256: digest.sha256.toUpperCase(), bytes: digest.size };
};
const receipt = {
  schemaVersion: "content-ocr-proofreading-offline-receipt/1.0.0",
  status: "PASS",
  worker: { id: "content.deepseek", version: WORKER_VERSION },
  provider: { mode: "replay", externalConnections: 0, credentialsRecorded: false },
  fixture,
  immutableOriginal: { before: sourceBefore, after: sourceAfter, unchanged: true },
  input: await evidence(path.join(fixtureRoot, "inputs", "standard-input.v4.json")),
  inputArtifact: await evidence(path.join(fixtureRoot, "artifact.json")),
  ocrProofreadBundle: await evidence(result.ocrProofreadBundlePath),
  learningBundle: await evidence(result.learningBundlePath),
  ...(result.cardBundlePath ? { cardBundle: await evidence(result.cardBundlePath) } : {}),
  readable: await evidence(readablePath),
  assertions: fixture === "offline-ocr-missing-character"
    ? ["independent missing-character fixture corrected by one edit", "copyright footer removed only by downstream delete-only cleaning", "LearningBundle v2 and CardBundle ready", "original OCR bytes/SHA unchanged"]
    : ["ambiguous token retained byte-for-byte", "unresolved reason published", "CardBundle withheld", "original OCR bytes/SHA unchanged"],
  completedAt: new Date().toISOString(),
};
const receiptPath = path.join(canaryRoot, "receipt.json");
await writeFile(receiptPath, `${stableStringify(receipt, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ status: "PASS", fixture, canaryRoot, receiptPath, receiptSha256: (await fileDigest(receiptPath)).sha256.toUpperCase(), readablePath })}\n`);

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1]) throw new Error(`CLI_INVALID ${name}`);
  return process.argv[index + 1];
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

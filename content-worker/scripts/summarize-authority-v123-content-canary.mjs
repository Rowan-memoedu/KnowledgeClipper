#!/usr/bin/env node
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const [projectRootArg, outputRootArg, questionReceiptArg, meaningReceiptArg, writingReceiptArg, bothReceiptArg] = process.argv.slice(2);
if (![projectRootArg, outputRootArg, questionReceiptArg, meaningReceiptArg, writingReceiptArg, bothReceiptArg].every(Boolean)) {
  throw new Error("Expected projectRoot outputRoot questionReceipt meaningReceipt writingReceipt bothReceipt.");
}
const projectRoot = path.resolve(projectRootArg);
const outputRoot = path.resolve(outputRootArg);
const authorityExecutable = path.join(projectRoot, "releases", "knowledge-clipper-authority-v1.2.3-20260828T021300Z", "win-x64", "workers", "content", "KnowledgeClipper.Content.Worker.exe");
const expectedWorkerSha = "f6ec35798f8948727b8664517fdbb096db8a94c8dd079f612b05bac4ea72d3b8";

const question = await readJson(questionReceiptArg);
const scenarios = await Promise.all([
  loadLanguageScenario(meaningReceiptArg, "meaning-reading"),
  loadLanguageScenario(writingReceiptArg, "writing"),
  loadLanguageScenario(bothReceiptArg, "both"),
]);
assert(question.status === "passed" && question.providerMode === "live", "Question workflow is not a passed live canary.");
assert(question.observed.states.join(",") === "waiting_input,waiting_input,succeeded", "Question state sequence drifted.");
assert(question.observed.orderedTurns === 2 && question.observed.firstAnswerPreserved && question.observed.targetTokenRotated, "Question ancestry assertions are incomplete.");
assert(question.observed.exporterResults === 0, "Question workflow invoked an exporter.");
assert(question.attempts.length === 3 && question.attempts.every((attempt) => attempt.providerTrace.every((trace) => trace.mode === "live" && trace.provider === "deepseek")), "Question attempts are not all live DeepSeek.");
assert(await shaFile(authorityExecutable) === expectedWorkerSha, "Authority content executable SHA-256 drifted.");

for (const scenario of scenarios) {
  assert(scenario.receipt.status === "completed" && scenario.receipt.mode === "replay", `${scenario.mode} did not complete in replay mode.`);
  assert(path.resolve(scenario.receipt.entrypoint).toLowerCase() === authorityExecutable.toLowerCase(), `${scenario.mode} did not use the exact authority executable.`);
  assert(scenario.receipt.providerEvidence.every((trace) => trace.mode === "replay" && trace.provider === "recorded"), `${scenario.mode} contains non-replay provider evidence.`);
  assert(scenario.bundle.schema_version === "card-bundle/1.0.0" && scenario.bundle.release_state === "ready", `${scenario.mode} CardBundle is not ready.`);
}

const meaning = scenarios[0];
assert(meaning.bundle.cards.length === 1 && meaning.bundle.cards[0].category_id === "language_en" && meaning.bundle.cards[0].template === "language_context", "meaning-reading card shape drifted.");
const writing = scenarios[1];
assert(writing.bundle.cards.length === 1 && writing.bundle.cards[0].category_id === "language_en" && writing.bundle.cards[0].template === "language_writing", "writing card shape drifted.");
const writingFront = blockText(writing.bundle.cards[0].front_blocks);
assert(writingFront.includes("\n") && writingFront.includes("spaces.  "), "Writing card did not retain internal newline and trailing spaces.");
assert(!writingFront.endsWith("\n") && !writingFront.endsWith("\r"), "Writing card retained a terminal CR/LF.");
assert(blockText(writing.bundle.cards[0].back_blocks) === "term", "Writing back is not the exact target term.");
const both = scenarios[2];
assert(both.bundle.cards.length === 4, "Both mode did not create exactly two cards for each of two language units.");
for (const category of ["language_zh", "language_en"]) {
  const cards = both.bundle.cards.filter((card) => card.category_id === category);
  assert(cards.length === 2 && cards[0].template === "language_context" && cards[1].template === "language_writing", `${category} both order is not meaning-reading then writing.`);
  assert(cards[1].front_blocks.every((block) => !block.text.endsWith("\n") && !block.text.endsWith("\r")), `${category} writing front retained terminal CR/LF.`);
  assert(cards[1].back_blocks[0].provenance === "source", `${category} writing back provenance is not source.`);
}

const readable = [
  "# authority v1.2.3 内容/制卡候选绑定：可读验收",
  "",
  "## 问题工作流（live / DeepSeek）",
  "",
  `用户问题：${question.observed.initialQuestion}`,
  "",
  `AI 回答：${question.observed.initialAnswer}`,
  "",
  `用户追问：${question.observed.followUpQuestion}`,
  "",
  `AI 回答：${question.observed.followUpAnswer}`,
  "",
  ...question.observed.cards.flatMap((card, index) => [
    `### 最终卡片 ${index + 1} · ${card.template}`,
    "",
    `Front：${card.front}`,
    "",
    `Back：${card.back}`,
    "",
  ]),
  "Core 查询状态：`waiting_input → waiting_input → succeeded`；两次 waiting_input 均有 answerAvailable=true、递增 questionTurnCount 和轮换 token；Exporter 调用 0。",
  "",
  "## 语言三选项（replay / recorded fixture）",
  "",
  ...scenarios.flatMap((scenario) => [
    `### ${scenario.mode}`,
    "",
    `原始文本：${scenario.sources.map((source) => JSON.stringify(source)).join(" / ")}`,
    "",
    ...scenario.bundle.cards.flatMap((card, index) => [
      `${index + 1}. ${card.category_id} · ${card.template}`,
      "",
      `   Front：${blockText(card.front_blocks).replaceAll("\n", "\\n")}`,
      "",
      `   Back：${blockText(card.back_blocks).replaceAll("\n", "\\n")}`,
      "",
      `   front provenance：${card.front_blocks.map((block) => block.provenance).join(", ")}；back provenance：${card.back_blocks.map((block) => block.provenance).join(", ")}`,
      "",
    ]),
  ]),
  "`both` 对中文与英文 unit 均严格按 `language_context` 在前、`language_writing` 在后；writing back 为 exact target_term。",
  "",
  "本验收未调用 RemNote、Anki 或任何 Exporter。",
  "",
].join("\n");
const readablePath = path.join(outputRoot, "readable-authority-content-v123.md");
await writeFile(readablePath, readable, { encoding: "utf8", flag: "wx" });

const receipt = {
  schemaVersion: "knowledge-clipper-authority-content-candidate-receipt.v1",
  status: "passed",
  completedAt: new Date().toISOString(),
  authority: {
    checkpointId: question.authority.checkpointId,
    registry: question.authority.registry,
    checkpoint: question.authority.checkpoint,
    worker: { workerId: "content.deepseek", workerVersion: "0.5.2", executable: evidence(authorityExecutable) },
  },
  providerModes: { questionWorkflow: "live/deepseek", languagePractice: "replay/recorded-fixture" },
  questionWorkflow: {
    receipt: evidence(path.resolve(questionReceiptArg)),
    taskId: question.task.taskId,
    states: question.observed.states,
    orderedTurns: question.observed.orderedTurns,
    firstAnswerPreserved: question.observed.firstAnswerPreserved,
    targetTokenRotated: question.observed.targetTokenRotated,
    attempts: question.attempts.map((attempt) => ({ attemptId: attempt.attemptId, stageId: attempt.stageId, operation: attempt.operation, request: attempt.request, result: attempt.result, providerTrace: attempt.providerTrace })),
    finalCards: question.observed.cards,
    exporterResults: question.observed.exporterResults,
  },
  languagePractice: scenarios.map((scenario) => ({
    mode: scenario.mode,
    receipt: evidence(path.resolve(scenario.receiptPath)),
    request: evidence(path.resolve(scenario.receipt.requestPath)),
    result: evidence(path.resolve(scenario.receipt.resultPath)),
    sourceTexts: scenario.sources,
    cards: scenario.bundle.cards.map((card) => ({ categoryId: card.category_id, template: card.template, front: blockText(card.front_blocks), back: blockText(card.back_blocks), frontProvenance: card.front_blocks.map((block) => block.provenance), backProvenance: card.back_blocks.map((block) => block.provenance) })),
    providerEvidence: scenario.receipt.providerEvidence,
  })),
  assertions: {
    questionStates: "waiting_input -> waiting_input -> succeeded",
    followUpAncestry: "persisted first answer + generated second answer",
    bothOrderPerLanguageUnit: ["language_context", "language_writing"],
    writingTerminalCrLfExcluded: true,
    writingInternalNewlinePreserved: true,
    writingTrailingSpacesPreserved: true,
    exporterCalls: 0,
  },
  readable: evidence(readablePath),
};
const receiptPath = path.join(outputRoot, "authority-content-v123-receipt.json");
await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
process.stdout.write(`${JSON.stringify({ status: "passed", receiptPath, receiptSha256: await shaFile(receiptPath), readablePath, readableSha256: await shaFile(readablePath) })}\n`);

async function loadLanguageScenario(receiptPath, mode) {
  const receipt = await readJson(receiptPath);
  const cardOutput = receipt.outputs.find((output) => output.kind === "card-bundle");
  assert(cardOutput, `${mode} has no CardBundle output.`);
  const manifestPath = path.join(receipt.jobRoot, ...cardOutput.manifestPath.split("/"));
  assert(await shaFile(manifestPath) === cardOutput.manifestSha256, `${mode} CardBundle manifest hash mismatch.`);
  const manifest = await readJson(manifestPath);
  const payloadPath = path.join(receipt.jobRoot, ...manifest.files[0].path.split("/"));
  assert(await shaFile(payloadPath) === manifest.files[0].sha256, `${mode} CardBundle payload hash mismatch.`);
  const request = await readJson(receipt.requestPath);
  const inputPath = path.join(receipt.jobRoot, ...request.payload.contentInputPath.split("/"));
  const input = await readJson(inputPath);
  const sources = await Promise.all(input.items.filter((item) => item.source.material_path).map((item) => readFile(path.join(receipt.jobRoot, ...item.source.material_path.split("/")), "utf8")));
  return { mode, receiptPath, receipt, bundle: await readJson(payloadPath), sources };
}

function evidence(filePath) {
  const bytes = requireRead(filePath);
  return { path: path.relative(projectRoot, filePath).replaceAll("\\", "/"), absolutePath: filePath, bytes: bytes.length, sha256: hash(bytes) };
}
function requireRead(filePath) {
  return readFileSync(filePath);
}
function blockText(blocks) { return blocks.map((block) => block.text).join("\n"); }
function hash(bytes) { return createHash("sha256").update(bytes).digest("hex"); }
async function shaFile(filePath) { return hash(await readFile(filePath)); }
async function readJson(filePath) { return JSON.parse(await readFile(path.resolve(filePath), "utf8")); }
function assert(condition, message) { if (!condition) throw new Error(message); }

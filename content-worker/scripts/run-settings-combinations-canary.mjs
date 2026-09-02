import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixtureManifestPath = path.join(workerRoot, "fixtures", "settings-combinations-v1", "manifest.json");
const matrix = JSON.parse(await readFile(fixtureManifestPath, "utf8"));
if (matrix.schemaVersion !== "content-settings-fixture-matrix/1.0.0" || matrix.providerMode !== "replay") throw new Error("FIXTURE_MATRIX_INVALID");
const stamp = new Date().toISOString().replace(/[-:.]/gu, "");
const receiptRoot = path.join(workerRoot, "canary", "settings-combinations", `${stamp}-offline-v1`);
await mkdir(path.dirname(receiptRoot), { recursive: true });
await mkdir(receiptRoot, { recursive: false });

const scenarios = [];
for (const scenario of matrix.scenarios) {
  const child = await runCanary(scenario.fixture);
  const receipt = JSON.parse(await readFile(child.receiptPath, "utf8"));
  if (receipt.status !== "completed") throw new Error(`CANARY_FAILED ${scenario.id}`);
  const result = JSON.parse(await readFile(receipt.resultPath, "utf8"));
  const learning = await readBundle(receipt.jobRoot, result, "learning-bundle");
  const cards = await readBundle(receipt.jobRoot, result, "card-bundle");
  const readableItems = learning.value.items.map((item) => ({
    itemId: item.item_id,
    originalText: item.source_segments.map((segment) => segment.text).join("\n\n"),
    cleanedText: item.cleaning.cleaned_text ?? null,
    cleaningState: item.cleaning.state,
    removals: item.cleaning.removals,
    classification: item.units.map((unit) => ({
      unitId: unit.unit_id,
      categoryId: unit.category_id,
      subtype: unit.subtype,
      answerState: unit.answer_state,
      targetTerm: unit.target_term ?? null,
      state: unit.state,
      unresolvedReasons: unit.unresolved_reasons,
    })),
    unresolvedReasons: item.unresolved_reasons,
  }));
  const readableCards = cards
    ? cards.value.cards.map((card) => ({
        cardId: card.card_id,
        itemId: card.item_id,
        unitId: card.unit_id,
        template: card.template,
        front: card.front_blocks.map(blockText).join("\n"),
        back: card.back_blocks.map(blockText).join("\n"),
        answerProvenance: card.answer_provenance,
      }))
    : [];
  scenarios.push({
    id: scenario.id,
    fixture: scenario.fixture,
    settings: scenario.settings,
    provider: { mode: "replay", externalConnections: 0, credentialsRecorded: false },
    childReceipt: { path: child.receiptPath, sha256: child.receiptSha256 },
    workerResult: { path: receipt.resultPath, sha256: receipt.resultSha256, status: result.status },
    learningBundle: { path: learning.path, sha256: learning.sha256, schemaVersion: learning.value.schema_version },
    cardBundle: cards ? { path: cards.path, sha256: cards.sha256, schemaVersion: cards.value.schema_version } : null,
    items: readableItems,
    cards: readableCards,
  });
}

validateMatrix(scenarios);
const resultDocument = {
  schemaVersion: "content-settings-fixture-result/1.0.0",
  worker: { id: "content.deepseek", version: "0.7.0" },
  provider: { mode: "replay", externalConnections: 0, credentialsRecorded: false },
  sourceMatrix: evidence(fixtureManifestPath),
  scenarios,
};
const resultPath = path.join(receiptRoot, "settings-combinations-result.v1.json");
await writeJsonOnce(resultPath, resultDocument);
const readablePath = path.join(receiptRoot, "settings-combinations-readable.md");
await writeFile(readablePath, readableMarkdown(scenarios), { flag: "wx" });
const receipt = {
  schemaVersion: "content-settings-combinations-receipt/1.0.0",
  worker: { id: "content.deepseek", version: "0.7.0" },
  status: "PASS",
  providerMode: "replay",
  externalConnections: 0,
  credentialsRecorded: false,
  scenarios: scenarios.map((scenario) => ({ id: scenario.id, status: scenario.workerResult.status, childReceipt: scenario.childReceipt })),
  result: evidence(resultPath),
  readable: evidence(readablePath),
  completedAt: new Date().toISOString(),
};
const receiptPath = path.join(receiptRoot, "receipt.v1.json");
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ type: "content.settings-combinations.receipt", receiptPath, receiptSha256: sha(await readFile(receiptPath)), status: "PASS" })}\n`);

function runCanary(fixture) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [path.join(workerRoot, "dist", "src", "canary.js"), "--fixture", fixture, "--mode", "replay"], { cwd: workerRoot, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk.toString(); process.stdout.write(chunk); });
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); process.stderr.write(chunk); });
    child.on("error", reject);
    child.on("close", (code) => {
      if (code !== 0) return reject(new Error(`CANARY_PROCESS_FAILED ${fixture}: ${stderr}`));
      const lines = stdout.trim().split(/\r?\n/u);
      const parsed = JSON.parse(lines.at(-1));
      resolve(parsed);
    });
  });
}

async function readBundle(jobRoot, result, kind) {
  const ref = result.outputs.find((entry) => entry.kind === kind);
  if (!ref) return null;
  const manifestPath = path.join(jobRoot, ...ref.manifestPath.split("/"));
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  const bundlePath = path.join(jobRoot, ...manifest.files[0].path.split("/"));
  return { path: bundlePath, sha256: sha(await readFile(bundlePath)), value: JSON.parse(await readFile(bundlePath, "utf8")) };
}

function validateMatrix(scenarios) {
  if (scenarios.length !== 4) throw new Error("MATRIX_INVALID scenario count");
  const ordinary = scenarios.find((entry) => entry.id === "ordinary-structured-cleaning");
  if (!ordinary?.items.some((item) => item.originalText !== item.cleanedText && item.removals.length > 0) || ordinary.cards.length === 0) throw new Error("MATRIX_INVALID ordinary cleaning/card evidence");
  const meaning = scenarios.find((entry) => entry.id === "language-meaning-reading");
  if (!meaning || meaning.cards.length === 0 || meaning.cards.some((card) => card.template !== "language_context")) throw new Error("MATRIX_INVALID meaning-reading");
  const writing = scenarios.find((entry) => entry.id === "language-writing");
  if (!writing || writing.cards.length === 0 || writing.cards.some((card) => card.template !== "language_writing")) throw new Error("MATRIX_INVALID writing");
  const both = scenarios.find((entry) => entry.id === "language-both");
  if (!both) throw new Error("MATRIX_INVALID both");
  const byUnit = Map.groupBy(both.cards, (card) => `${card.itemId}\0${card.unitId}`);
  for (const cards of byUnit.values()) {
    if (cards.length !== 2 || cards[0].template !== "language_context" || cards[1].template !== "language_writing") throw new Error("MATRIX_INVALID both order/cardinality");
  }
}

function readableMarkdown(scenarios) {
  const lines = ["# Content 0.7.0 设置组合离线验收", "", "Provider：replay fixture；外部连接 0；凭据记录 false。", ""];
  for (const scenario of scenarios) {
    lines.push(`## ${scenario.id}`, "", `- Fixture：\`${scenario.fixture}\``, `- 设置：\`${JSON.stringify(scenario.settings)}\``, "");
    for (const item of scenario.items) {
      lines.push(`### ${item.itemId}`, "", "原始文本：", "", "````text", item.originalText, "````", "", "清洗后文本：", "", "````text", item.cleanedText ?? "<none>", "````", "", `分类：\`${JSON.stringify(item.classification)}\``, "", `清理/拒绝理由：\`${JSON.stringify({ removals: item.removals, unresolvedReasons: item.unresolvedReasons })}\``, "");
    }
    lines.push("卡片：", "");
    for (const card of scenario.cards) lines.push(`- \`${card.template}\` — Front：${inline(card.front)}；Back：${inline(card.back)}`);
    lines.push("");
  }
  return `${lines.join("\n")}\n`;
}

function blockText(block) {
  return block.text ?? `[asset:${block.material_path}]`;
}

function inline(value) {
  return `\`${String(value).replace(/`/gu, "\\`").replace(/\r?\n/gu, "\\n")}\``;
}

function evidence(filePath) {
  const bytes = readFileSync(filePath);
  return { path: filePath, sha256: sha(bytes), bytes: bytes.length };
}

function sha(value) {
  return createHash("sha256").update(value).digest("hex");
}

async function writeJsonOnce(filePath, value) {
  await writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
}

#!/usr/bin/env node
import { createHash } from "node:crypto";
import { access, copyFile, mkdir, readdir, readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const specPath = path.resolve(process.argv[2] ?? path.join(workerRoot, "canary", "live-synthetic-matrix-v1.spec.json"));
const spec = JSON.parse(await readFile(specPath, "utf8"));
const outputRoot = path.join(workerRoot, "canary", "authority", spec.packageId);
try {
  await access(outputRoot);
  throw new Error(`output already exists: ${outputRoot}`);
} catch (error) {
  if (error?.code !== "ENOENT") throw error;
}
await mkdir(path.dirname(outputRoot), { recursive: true });
await mkdir(outputRoot, { recursive: false });

const readable = [
  "# Content DeepSeek live synthetic matrix",
  "",
  "All examples below are real DeepSeek HTTPS responses over synthetic, non-private inputs. No RemNote or Anki call is present.",
  "",
];
const scenarioEvidence = [];

for (const scenario of spec.scenarios) {
  const receiptPath = path.join(workerRoot, ...scenario.receipt.split("/"));
  const receipt = JSON.parse(await readFile(receiptPath, "utf8"));
  if (receipt.mode !== "live" || receipt.status !== "completed") throw new Error(`${scenario.id}: not a completed live receipt`);
  if (receipt.externalConnectionEvidence?.transport !== "https" || receipt.externalConnectionEvidence?.credentialRecorded !== false) {
    throw new Error(`${scenario.id}: invalid sanitized connection evidence`);
  }
  if (!receipt.providerEvidence?.every((entry) => entry.provider === "deepseek" && entry.mode === "live" && Number.isInteger(entry.attempts))) {
    throw new Error(`${scenario.id}: invalid provider evidence`);
  }

  const scenarioRoot = path.join(outputRoot, "scenarios", scenario.id);
  await mkdir(path.join(scenarioRoot, "source"), { recursive: true });
  await copyTree(path.join(workerRoot, "fixtures", scenario.fixture), path.join(scenarioRoot, "source"));
  await mkdir(path.join(scenarioRoot, "evidence"), { recursive: true });
  await copyFile(receiptPath, path.join(scenarioRoot, "evidence", "canary-receipt.json"));
  await copyFile(receipt.resultPath, path.join(scenarioRoot, "evidence", "worker-result.json"));
  const result = JSON.parse(await readFile(receipt.resultPath, "utf8"));
  const outputSource = path.join(path.dirname(receipt.resultPath), "outputs");
  await copyTree(outputSource, path.join(scenarioRoot, "outputs"));

  const learning = await readOptionalJson(path.join(outputSource, "learning-bundle.schema-v1.json"));
  const cards = await readOptionalJson(path.join(outputSource, "card-bundle.schema-v1.json"));
  const decision = await readOptionalJson(path.join(outputSource, "language-practice-decision.schema-v1.json"));
  const answer = await readOptionalJson(path.join(outputSource, "question-answer.schema-v1.json"));
  validateExpectedScenario(scenario, learning, cards, decision);
  if (answer) await writeFile(path.join(scenarioRoot, "outputs", "answer.text"), `${answer.turns.at(-1).answer_text}\n`, { flag: "wx" });

  readable.push(`## ${scenario.id}`, "", `- Fixture: \`${scenario.fixture}\``, `- Worker result: \`${result.status}\``, `- Output kinds: ${result.outputs.map((entry) => `\`${entry.kind}\``).join(", ")}`, `- Provider calls: ${receipt.providerEvidence.map((entry) => `${entry.provider}/${entry.model}, attempts=${entry.attempts}`).join("; ")}`, "");
  const input = await firstInput(path.join(workerRoot, "fixtures", scenario.fixture, "inputs"));
  const sourceMaterialPath = await firstMaterialPath(path.join(workerRoot, "fixtures", scenario.fixture));
  if (scenario.kind === "cleaning") {
    const raw = await firstMaterial(path.join(workerRoot, "fixtures", scenario.fixture));
    readable.push("Raw input:", "", "```text", raw.replace(/\r\n/gu, "\n").replace(/\n$/u, ""), "```", "", "DeepSeek cleaned text:", "", "```text", learning.items[0].cleaning.cleaned_text, "```", "", `Removed: ${learning.items[0].cleaning.removals.map((entry) => `\`${entry.exact_text.replace(/\n/gu, "\\n")}\` (${entry.reason})`).join(", ")}`, "");
  } else if (scenario.kind === "language") {
    const raw = await firstMaterial(path.join(workerRoot, "fixtures", scenario.fixture));
    readable.push("Raw input:", "", "```text", raw.replace(/\r\n/gu, "\n").replace(/\n$/u, ""), "```", "");
    if (decision) readable.push(`Decision: mode required; ${decision.decisions.length} language unit(s); no CardBundle published.`, "");
    if (cards) {
      for (const card of cards.cards) readable.push(`- ${card.template}: front=\`${blocks(card.front_blocks)}\`; back=\`${blocks(card.back_blocks)}\``);
      readable.push("");
    }
  } else {
    readable.push(`Questions: ${input.questionWorkflow.questionTurns.map((turn) => `${turn.turnIndex}. ${turn.question}`).join(" | ")}`, "");
    if (answer) readable.push(`Latest actual answer: ${answer.turns.at(-1).answer_text}`, "");
    if (cards) {
      for (const card of cards.cards) readable.push(`- ${card.template}: front=\`${blocks(card.front_blocks)}\`; back=\`${blocks(card.back_blocks)}\``);
      readable.push("");
    }
  }

  scenarioEvidence.push({
    id: scenario.id,
    fixture: scenario.fixture,
    sourceJobRoot: path.relative(workerRoot, receipt.jobRoot).replaceAll("\\", "/"),
    receiptSha256: await sha256File(receiptPath),
    resultSha256: await sha256File(receipt.resultPath),
    sourceMaterialSha256: await sha256File(sourceMaterialPath),
    learningBundleSha256: learning ? await sha256File(path.join(outputSource, "learning-bundle.schema-v1.json")) : null,
    cardBundleSha256: cards ? await sha256File(path.join(outputSource, "card-bundle.schema-v1.json")) : null,
    outputKinds: result.outputs.map((entry) => entry.kind),
    providerEvidence: receipt.providerEvidence,
    externalConnectionEvidence: receipt.externalConnectionEvidence,
});
}

const languageSourceHashes = new Set(scenarioEvidence.filter((entry) => spec.scenarios.find((scenario) => scenario.id === entry.id)?.kind === "language").map((entry) => entry.sourceMaterialSha256));
if (languageSourceHashes.size > 1) throw new Error("language scenarios do not share one immutable source material");

await writeFile(path.join(outputRoot, "READABLE-SAMPLES.md"), `${readable.join("\n")}\n`, { flag: "wx" });
const releaseManifestPath = path.join(workerRoot, ...spec.releaseManifest.split("/"));
const releaseManifest = JSON.parse(await readFile(releaseManifestPath, "utf8"));
const files = await inventory(outputRoot, new Set(["manifest.json", "authority-receipt.json"]));
const manifest = {
  schemaVersion: "content-live-synthetic-matrix/1.0.0",
  packageId: spec.packageId,
  productId: "KnowledgeClipper",
  productDisplayName: "零碎知识剪藏",
  destinationNeutral: true,
  syntheticNonPrivate: true,
  provider: "deepseek",
  model: "deepseek-chat",
  externalWrites: [],
  release: { releaseId: releaseManifest.releaseId, workerVersion: releaseManifest.worker.workerVersion, manifestPath: spec.releaseManifest, manifestSha256: await sha256File(releaseManifestPath), promotedToCanonicalRegistry: spec.promotedToCanonicalRegistry === true, authorityRegistryVersion: spec.authorityRegistryVersion ?? null },
  scenarios: scenarioEvidence,
  files,
};
const manifestPath = path.join(outputRoot, "manifest.json");
await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, { flag: "wx" });
const authorityReceipt = {
  schemaVersion: "content-live-synthetic-authority-receipt/1.0.0",
  packageId: spec.packageId,
  status: "completed",
  scenarioCount: scenarioEvidence.length,
  manifestPath,
  manifestSha256: await sha256File(manifestPath),
  allCanariesLive: true,
  allCanariesCompleted: true,
  schemasValidatedSeparately: true,
  credentialsRecorded: false,
  externalProvidersCalled: ["deepseek"],
  externalDestinationTargetsCalled: [],
};
const authorityReceiptPath = path.join(outputRoot, "authority-receipt.json");
await writeFile(authorityReceiptPath, `${JSON.stringify(authorityReceipt, null, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ outputRoot, manifestPath, manifestSha256: authorityReceipt.manifestSha256, authorityReceiptPath, authorityReceiptSha256: await sha256File(authorityReceiptPath), scenarioCount: scenarioEvidence.length, fileCount: files.length })}\n`);

function blocks(entries) {
  return entries.map((entry) => entry.text ?? entry.asset_ref).join(" | ").replace(/\r?\n/gu, "\\n").replaceAll("`", "\\`");
}

async function firstInput(directory) {
  for (const name of ["standard-input.v3.json", "standard-input.v2.json", "standard-input.v1.json"]) {
    const value = await readOptionalJson(path.join(directory, name));
    if (value) return value;
  }
  throw new Error(`no input in ${directory}`);
}

async function firstMaterial(fixtureRoot) {
  return readFile(await firstMaterialPath(fixtureRoot), "utf8");
}

async function firstMaterialPath(fixtureRoot) {
  const materialRoot = path.join(fixtureRoot, "materials");
  const names = await readdir(materialRoot);
  if (names.length !== 1) throw new Error(`expected one material in ${materialRoot}`);
  return path.join(materialRoot, names[0]);
}

function validateExpectedScenario(scenario, learning, cards, decision) {
  const expected = scenario.expected;
  if (!expected) return;
  if (!learning || learning.release_state !== expected.releaseState) throw new Error(`${scenario.id}: unexpected LearningBundle release state`);
  if (expected.categoryId && !learning.items?.some((item) => item.units?.some((unit) => unit.category_id === expected.categoryId))) throw new Error(`${scenario.id}: expected category missing`);
  const templates = cards?.cards?.map((card) => card.template) ?? [];
  if (JSON.stringify(templates) !== JSON.stringify(expected.cardTemplates ?? [])) throw new Error(`${scenario.id}: card template sequence mismatch`);
  if (expected.noDecision === true && decision) throw new Error(`${scenario.id}: unexpected decision artifact`);
  const removals = learning.items?.flatMap((item) => item.cleaning?.removals?.map((entry) => entry.exact_text.trimEnd()) ?? []) ?? [];
  for (const exact of expected.removedExact ?? []) if (!removals.includes(exact)) throw new Error(`${scenario.id}: required cleaning removal missing: ${exact}`);
}

async function readOptionalJson(filePath) {
  try { return JSON.parse(await readFile(filePath, "utf8")); } catch (error) { if (error?.code === "ENOENT") return undefined; throw error; }
}

async function copyTree(source, destination) {
  const sourceStat = await stat(source);
  if (sourceStat.isFile()) { await mkdir(path.dirname(destination), { recursive: true }); await copyFile(source, destination); return; }
  await mkdir(destination, { recursive: true });
  for (const entry of await readdir(source, { withFileTypes: true })) await copyTree(path.join(source, entry.name), path.join(destination, entry.name));
}

async function inventory(root, excluded) {
  const result = [];
  async function visit(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const fullPath = path.join(directory, entry.name);
      const relative = path.relative(root, fullPath).replaceAll("\\", "/");
      if (entry.isDirectory()) await visit(fullPath);
      else if (!excluded.has(relative)) result.push({ path: relative, bytes: (await stat(fullPath)).size, sha256: await sha256File(fullPath) });
    }
  }
  await visit(root);
  return result.sort((left, right) => left.path.localeCompare(right.path));
}

async function sha256File(filePath) {
  return createHash("sha256").update(await readFile(filePath)).digest("hex");
}

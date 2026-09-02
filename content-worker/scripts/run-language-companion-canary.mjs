import { copyFile, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { runCommonRequest } from "../dist/src/common-adapter.js";
import { describeContentCompanion, validateContentCompanionSettings } from "../dist/src/settings-adapter.js";
import { fileDigest, sha256, stableStringify } from "../dist/src/util.js";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const externalPromptDraftRoot = process.env.KNOWLEDGE_CLIPPER_CONTENT_CANARY_PROMPT_ROOT
  ? path.resolve(process.env.KNOWLEDGE_CLIPPER_CONTENT_CANARY_PROMPT_ROOT)
  : null;
const stamp = new Date().toISOString().replace(/[-:.]/gu, "");
const canaryRoot = path.join(workerRoot, "canary", "language-companion-settings", `${stamp}-offline-v1`);
await mkdir(path.dirname(canaryRoot), { recursive: true });
await mkdir(canaryRoot, { recursive: false });

const definitions = [
  { id: "ask", fixture: "offline-language-decision-v2", choice: "ask", expectUnresolved: "language_practice_mode_required", mutate: removeItemHintMode },
  { id: "meaning-reading", fixture: "offline-language-meaning-reading-v2", choice: "meaning-reading", mutate: removeItemHintMode },
  { id: "writing", fixture: "offline-language-writing-whitespace-v2", choice: "writing", mutate: (input) => { delete input.languagePracticeMode; } },
  { id: "both", fixture: "offline-language-practice-v2", choice: "both", model: "deepseek-reasoner", mutate: (input) => { delete input.human_overrides.languagePracticeMode; } },
  { id: "non-language", fixture: "offline-general-learning", choice: "both", expectNoLanguageChoice: true, mutate: () => {} },
  { id: "target-nonunique", fixture: "offline-language-writing-nonunique-v2", choice: "writing", expectUnresolved: "language_writing_target_not_unique", mutate: removeItemHintMode },
  { id: "details-unreliable", fixture: "offline-language-writing-unreliable-v2", choice: "writing", expectUnresolved: "language_writing_details_required", mutate: removeItemHintMode },
];

const descriptor = await describeContentCompanion(workerRoot);
const descriptorPath = path.join(canaryRoot, "content-companion-descriptor.v1.json");
await writeJsonOnce(descriptorPath, descriptor);
const schemaPaths = [
  "schemas/content-companion-descriptor.v1.schema.json",
  "schemas/content-companion-settings.v1.schema.json",
  "schemas/content-config-snapshot.v1.1.schema.json",
];

const scenarios = [];
for (const definition of definitions) scenarios.push(await runScenario(definition));
assertEvidence(scenarios);

const resultDocument = {
  schemaVersion: "content-language-companion-canary-result/1.0.0",
  worker: { id: "content.deepseek", version: "0.7.0" },
  provider: { mode: "replay", implementation: "strict-provider-replay", externalConnections: 0, credentialsRecorded: false },
  promptDraftSource: externalPromptDraftRoot ? "validated-user-copy" : "built-in-copy",
  descriptor: await evidence(descriptorPath),
  schemas: await Promise.all(schemaPaths.map((relative) => evidence(path.join(workerRoot, ...relative.split("/")), relative))),
  scenarios,
};
const resultPath = path.join(canaryRoot, "language-companion-result.v1.json");
await writeJsonOnce(resultPath, resultDocument);
const readablePath = path.join(canaryRoot, "原始文本到最终卡片.md");
await writeFile(readablePath, renderReadable(resultDocument), { flag: "wx" });
const receipt = {
  schemaVersion: "content-language-companion-receipt/1.0.0",
  worker: { id: "content.deepseek", version: "0.7.0" },
  status: "PASS",
  providerMode: "strict-replay",
  externalConnections: 0,
  credentialsRecorded: false,
  descriptor: await evidence(descriptorPath),
  result: await evidence(resultPath),
  readable: await evidence(readablePath),
  scenarioReceipts: scenarios.map((scenario) => ({ id: scenario.id, status: scenario.workerResult.status, request: scenario.request, result: scenario.workerResult.evidence })),
  assertions: [
    "ask default publishes an immutable language-practice-decision and no CardBundle",
    "three independent raw language sources consume snapshot-bound meaning-reading, writing, and both defaults",
    "provider.model and provider.maxRetries survive the SHA-bound snapshot and are visible in WorkerResult payload metadata",
    "writing front replaces one exact target with （pronunciation；meaning） and back equals target",
    "both order is language_context then language_writing for every safe unit",
    "non-language categories do not emit language-practice-decision",
    "non-unique target and unreliable writing details publish decision/unresolved without CardBundle",
    "unknown project settings are rejected by the profile validator",
    "no credential value is present in settings, task packages, logs, fixtures, or receipts",
  ],
  completedAt: new Date().toISOString(),
};
const receiptPath = path.join(canaryRoot, "receipt.v1.json");
await writeJsonOnce(receiptPath, receipt);
process.stdout.write(`${JSON.stringify({ type: "content.language-companion.receipt", status: "PASS", receiptPath, receiptSha256: (await fileDigest(receiptPath)).sha256 })}\n`);

async function runScenario(definition) {
  const scenarioRoot = path.join(canaryRoot, "scenarios", definition.id);
  const jobRoot = path.join(scenarioRoot, "job");
  const sourceRoot = path.join(workerRoot, "fixtures", definition.fixture);
  await mkdir(jobRoot, { recursive: true });
  const inputName = await firstInput(sourceRoot);
  const input = JSON.parse(await readFile(path.join(sourceRoot, "inputs", inputName), "utf8"));
  definition.mutate(input);
  const originalText = await collectAndRewriteMaterials(sourceRoot, jobRoot, input);
  const contentInputRelative = `inputs/content/${inputName}`;
  await mkdir(path.dirname(path.join(jobRoot, ...contentInputRelative.split("/"))), { recursive: true });
  await writeJsonOnce(path.join(jobRoot, ...contentInputRelative.split("/")), input);
  const replayRelative = "inputs/content/provider-replay.v1.json";
  await copyFile(path.join(sourceRoot, "provider-replay.v1.json"), path.join(jobRoot, ...replayRelative.split("/")));
  const contentFiles = await declarations(jobRoot, [contentInputRelative, replayRelative, ...materialPackagePaths(input)]);
  const contentArtifactRelative = "inputs/content/artifact.json";
  const contentArtifact = {
    schemaVersion: "1.0.0", artifactId: `content-input-${definition.id}-v1`, kind: "content-input", contractVersion: "1.0.0", mediaType: "application/json",
    files: contentFiles, createdAt: new Date().toISOString(), producer: { stageId: "fixture", workerId: "content.companion.canary", workerVersion: "1.0.0", attemptId: `source-${definition.id}` }, inputRefs: [], dataClass: "public",
    metadata: { entrypoint: contentInputRelative, moduleSchema: input.schema_version, fixture: definition.fixture, taskLanguageModeRemoved: true },
  };
  await writeJsonOnce(path.join(jobRoot, ...contentArtifactRelative.split("/")), contentArtifact);
  const contentArtifactDigest = await fileDigest(path.join(jobRoot, ...contentArtifactRelative.split("/")));

  const settings = {
    schemaVersion: "content-companion-settings/1.0.0", profileId: `canary-${definition.id}-v1`, profileVersion: "1.0.0",
    languagePractice: { defaultChoice: definition.choice }, interaction: { promptAfterRecognition: true },
    promptProfile: { promptSetId: "open-md-v2", profileVersion: "2.0.0" },
    provider: { model: definition.model ?? "deepseek-chat", maxRetries: 3 },
    security: { credentials: "local-secret-only", taskPackageSecrets: "forbidden" },
  };
  const settingsPath = path.join(scenarioRoot, "project-settings.v1.json");
  await writeJsonOnce(settingsPath, settings);
  const validation = await validateContentCompanionSettings(settingsPath);
  if (validation.valid !== true) throw new Error(`SETTINGS_INVALID ${definition.id}`);
  const config = await stageContentConfig(jobRoot, definition.id, validation.runtimeProjection);

  const attemptPath = `runs/run-${definition.id}/stages/content.understand-and-card/attempts/attempt-${definition.id}`;
  const request = {
    schemaVersion: "1.0.0", requestId: `request-${definition.id}`, operationId: `operation-${definition.id}`, taskId: `task-${definition.id}`, runId: `run-${definition.id}`,
    stageId: "content.understand-and-card", attemptId: `attempt-${definition.id}`, workerId: "content.deepseek", operation: "understand-and-card", idempotencyKey: sha256(`language-companion:${definition.id}`),
    attemptPath, outputPath: `${attemptPath}/outputs`, progressPath: `${attemptPath}/progress.ndjson`, resultPath: `${attemptPath}/result.json`, cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
    inputRefs: [
      { artifactRef: contentArtifact.artifactId, kind: "content-input", contractVersion: "1.0.0", manifestPath: contentArtifactRelative, expectedSha256: contentArtifactDigest.sha256 },
      { artifactRef: config.artifact.artifactId, kind: "content-config-snapshot", contractVersion: "1.0.0", manifestPath: config.artifactRelative, expectedSha256: config.artifactDigest.sha256 },
    ],
    configSnapshot: { promptSet: config.snapshot.promptSet.id, categoryRegistry: config.snapshot.categoryRegistry.id, contentSettingsRevisionId: config.snapshot.settingsRevisionId },
    payload: { schemaVersion: "content-run/1.0.0", contentInputPath: contentInputRelative, providerMode: "replay", replayPath: replayRelative, contentConfigPath: config.snapshotRelative },
  };
  const requestPath = path.join(jobRoot, ...attemptPath.split("/"), "request.json");
  await mkdir(path.dirname(requestPath), { recursive: true });
  await writeJsonOnce(requestPath, request);
  const result = await runCommonRequest(requestPath, workerRoot);
  if (result.status !== "completed") throw new Error(`WORKER_FAILED ${definition.id}`);
  const learning = await readBundle(jobRoot, result, "learning-bundle");
  const cards = await readBundle(jobRoot, result, "card-bundle");
  const decision = await readBundle(jobRoot, result, "language-practice-decision");
  return {
    id: definition.id, fixture: definition.fixture, originalText, recognized: learning.value.items.map((item) => item.units.map((unit) => ({ itemId: item.item_id, unitId: unit.unit_id, categoryId: unit.category_id, targetTerm: unit.target_term ?? null, state: unit.state, unresolvedReasons: unit.unresolved_reasons }))).flat(),
    selection: { defaultChoice: definition.choice, source: "sha-bound-content-config-snapshot", promptAfterRecognition: { value: true, status: "conditional-core-adapter", workerConsumed: false } },
    providerPolicy: { model: validation.runtimeProjection.provider.model, maxRetries: validation.runtimeProjection.provider.maxRetries, source: "sha-bound-content-config-snapshot" },
    promptProfile: { id: learning.value.worker.prompt_set_id, promptSetSha256: learning.value.worker.prompt_set_sha256, profileId: learning.value.worker.capability_profile_id, profileVersion: learning.value.worker.capability_profile_version, profileSha256: learning.value.worker.capability_profile_sha256 },
    settings: await evidence(settingsPath), configSnapshot: await evidence(path.join(jobRoot, ...config.snapshotRelative.split("/"))), request: await evidence(requestPath),
    workerResult: { status: result.status, evidence: await evidence(path.join(jobRoot, ...request.resultPath.split("/"))), payloadContentSettings: result.payload?.contentSettings ?? null }, learningBundle: learning.evidence,
    cardBundle: cards?.evidence ?? null, decisionBundle: decision?.evidence ?? null,
    cards: cards?.value.cards.map((card) => ({ categoryId: card.category_id, unitId: card.unit_id, template: card.template, front: card.front_blocks.map(blockText).join("\n"), back: card.back_blocks.map(blockText).join("\n") })) ?? [],
    unresolvedReasons: decision?.value.decisions.flatMap((entry) => entry.reasons) ?? [],
  };
}

async function stageContentConfig(jobRoot, id, runtimePolicy) {
  const rootRelative = `inputs/content-settings/settings-${id}-v1`;
  const promptRootRelative = `${rootRelative}/prompts`;
  const registryRelative = `${rootRelative}/config/categories.v2.json`;
  const snapshotRelative = `${rootRelative}/content-config-snapshot.v1.1.json`;
  const artifactRelative = `${rootRelative}/artifact.json`;
  const resourceRoot = externalPromptDraftRoot ?? workerRoot;
  const registrySource = path.join(resourceRoot, "config", "categories.v2.json");
  const registry = JSON.parse(await readFile(registrySource, "utf8"));
  const promptPaths = new Set(["shared/system.md", "shared/card-output-contract.md", "stages/ocr-proofreading.md", "stages/cleaning.md", "stages/classification.md", "stages/language-writing.md", "stages/question-answer.md"]);
  for (const category of registry.categories) {
    promptPaths.add(category.prompt_file);
    for (const promptPath of Object.values(category.template_prompt_files ?? {})) promptPaths.add(promptPath);
  }
  const promptFiles = [];
  for (const promptPath of [...promptPaths].sort()) {
    const packagePath = `${promptRootRelative}/${promptPath}`;
    const target = path.join(jobRoot, ...packagePath.split("/"));
    await mkdir(path.dirname(target), { recursive: true });
    await copyFile(path.join(resourceRoot, "prompts", ...promptPath.split("/")), target);
    const digest = await fileDigest(target);
    promptFiles.push({ path: promptPath, packagePath, sha256: digest.sha256, bytes: digest.size });
  }
  const registryTarget = path.join(jobRoot, ...registryRelative.split("/"));
  await mkdir(path.dirname(registryTarget), { recursive: true });
  await copyFile(registrySource, registryTarget);
  const registryDigest = await fileDigest(registryTarget);
  const promptSetSha256 = sha256(stableStringify(promptFiles.map(({ path: promptPath, sha256: digest, bytes }) => ({ path: promptPath, sha256: digest, bytes }))));
  const snapshot = {
    schemaVersion: "content-config-snapshot/1.1.0", settingsRevisionId: `settings-${id}-v1`, workerCompatibility: { workerId: "content.deepseek", minimumVersion: "0.7.0" },
    promptSet: { id: externalPromptDraftRoot ? "open-md-v2-validated-user-edit" : "open-md-v2-canary-copy", source: "user-copy", promptRootPath: promptRootRelative, sha256: promptSetSha256, files: promptFiles },
    categoryRegistry: { id: "category-registry/2.0.0", schemaVersion: "category-registry/2.0.0", packagePath: registryRelative, sha256: registryDigest.sha256, bytes: registryDigest.size },
    providerBoundary: { provider: "deepseek", credentials: "process-injected-only" }, runtimePolicy,
  };
  await writeJsonOnce(path.join(jobRoot, ...snapshotRelative.split("/")), snapshot);
  const snapshotDigest = await fileDigest(path.join(jobRoot, ...snapshotRelative.split("/")));
  const artifact = {
    schemaVersion: "1.0.0", artifactId: `content-config-settings-${id}-v1`, kind: "content-config-snapshot", contractVersion: "1.0.0", mediaType: "application/json",
    files: [{ path: snapshotRelative, sha256: snapshotDigest.sha256, bytes: snapshotDigest.size, mediaType: "application/json" }, { path: registryRelative, sha256: registryDigest.sha256, bytes: registryDigest.size, mediaType: "application/json" }, ...promptFiles.map((file) => ({ path: file.packagePath, sha256: file.sha256, bytes: file.bytes, mediaType: "text/markdown; charset=utf-8" }))],
    createdAt: new Date().toISOString(), producer: { stageId: "content.settings", workerId: "content.companion.canary", workerVersion: "1.0.0", attemptId: `publish-settings-${id}` }, inputRefs: [], dataClass: "public",
    metadata: { entrypoint: snapshotRelative, credentialsRecorded: false, companionSettingsSchema: "content-companion-settings/1.0.0" },
  };
  await writeJsonOnce(path.join(jobRoot, ...artifactRelative.split("/")), artifact);
  return { snapshot, snapshotRelative, artifact, artifactRelative, artifactDigest: await fileDigest(path.join(jobRoot, ...artifactRelative.split("/"))) };
}

async function collectAndRewriteMaterials(sourceRoot, jobRoot, input) {
  const originals = [];
  for (const item of input.items) {
    if (!item.source.material_path) continue;
    const relative = item.source.material_path;
    const bytes = await readFile(path.join(sourceRoot, ...relative.split("/")));
    originals.push(bytes.toString("utf8"));
    item.source.material_path = `inputs/content/${relative}`;
    const target = path.join(jobRoot, "inputs", "content", ...relative.split("/"));
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, bytes, { flag: "wx" });
  }
  return originals;
}

function materialPackagePaths(input) { return input.items.flatMap((item) => item.source.material_path ? [item.source.material_path] : []); }
async function declarations(root, paths) { return Promise.all(paths.map(async (relative) => { const digest = await fileDigest(path.join(root, ...relative.split("/"))); return { path: relative, sha256: digest.sha256, bytes: digest.size, mediaType: relative.endsWith(".md") ? "text/markdown; charset=utf-8" : "application/json" }; })); }
async function firstInput(root) { const names = await readdir(path.join(root, "inputs")); for (const name of ["standard-input.v3.json", "standard-input.v2.json", "standard-input.v1.json"]) if (names.includes(name)) return name; throw new Error("FIXTURE_INPUT_MISSING"); }
function removeItemHintMode(input) { delete input.items[0].hints.languagePracticeMode; }
async function readBundle(jobRoot, result, kind) { const ref = result.outputs.find((entry) => entry.kind === kind); if (!ref) return null; const manifest = JSON.parse(await readFile(path.join(jobRoot, ...ref.manifestPath.split("/")), "utf8")); const bundlePath = path.join(jobRoot, ...manifest.files[0].path.split("/")); return { value: JSON.parse(await readFile(bundlePath, "utf8")), evidence: await evidence(bundlePath) }; }
function blockText(block) { return block.text ?? `[asset:${block.material_path}]`; }
async function evidence(filePath, displayPath = filePath) { const digest = await fileDigest(filePath); return { path: displayPath, sha256: digest.sha256, bytes: digest.size }; }
async function writeJsonOnce(filePath, value) { await writeFile(filePath, `${stableStringify(value, 2)}\n`, { flag: "wx" }); }

function assertEvidence(scenarios) {
  const ask = scenarios.find((entry) => entry.id === "ask");
  if (ask.cards.length !== 0 || !ask.decisionBundle || !ask.unresolvedReasons.includes("language_practice_mode_required")) throw new Error("ASSERT_ASK_DECISION");
  const meaning = scenarios.find((entry) => entry.id === "meaning-reading");
  if (meaning.cards.length !== 1 || meaning.cards[0].template !== "language_context") throw new Error("ASSERT_MEANING_READING");
  const writing = scenarios.find((entry) => entry.id === "writing");
  if (writing.cards.length !== 1 || writing.cards[0].template !== "language_writing" || writing.cards[0].front !== "First （/tɜːm/；术语） line.\nSecond line keeps two spaces.  " || writing.cards[0].back !== "term") throw new Error("ASSERT_WRITING");
  const both = scenarios.find((entry) => entry.id === "both");
  const groups = Map.groupBy(both.cards, (card) => card.unitId);
  for (const cards of groups.values()) if (cards.length !== 2 || cards[0].template !== "language_context" || cards[1].template !== "language_writing") throw new Error("ASSERT_BOTH_ORDER");
  const nonLanguage = scenarios.find((entry) => entry.id === "non-language");
  if (nonLanguage.decisionBundle !== null || nonLanguage.recognized.some((unit) => unit.categoryId.startsWith("language_"))) throw new Error("ASSERT_NON_LANGUAGE");
  for (const id of ["target-nonunique", "details-unreliable"]) { const entry = scenarios.find((value) => value.id === id); if (entry.cards.length !== 0 || !entry.decisionBundle || entry.unresolvedReasons.length === 0) throw new Error(`ASSERT_FAIL_CLOSED ${id}`); }
  for (const entry of scenarios) {
    const payload = entry.workerResult.payloadContentSettings;
    if (!payload || payload.providerModel !== entry.providerPolicy.model || payload.providerMaxRetries !== entry.providerPolicy.maxRetries) throw new Error(`ASSERT_PROVIDER_POLICY ${entry.id}`);
  }
}

function renderReadable(document) {
  const lines = ["# Content 0.7.0 语言设置：原始文本 → 最终卡片", "", "Provider：严格 Replay fixture；外部连接 0；凭据记录 false。`promptAfterRecognition` 为 Core/UI conditional；`ask` 发布 decision 且不发布 CardBundle，其余默认 choice 由 worker 从 SHA-bound snapshot 消费。", ""];
  for (const scenario of document.scenarios) {
    lines.push(`## ${scenario.id}`, "", `- 来源 fixture：\`${scenario.fixture}\``, `- 选择：\`${scenario.selection.defaultChoice}\`（${scenario.selection.source}）`, `- Provider 参数：\`${scenario.providerPolicy.model}\` / maxRetries=\`${scenario.providerPolicy.maxRetries}\`（只验证快照绑定，未联网）`, `- Prompt：\`${scenario.promptProfile.id}\` / \`${scenario.promptProfile.promptSetSha256}\``, `- Profile：\`${scenario.promptProfile.profileId}@${scenario.promptProfile.profileVersion}\` / \`${scenario.promptProfile.profileSha256}\``, `- Request SHA：\`${scenario.request.sha256}\``, "", "原始文本：", "");
    for (const text of scenario.originalText) lines.push("````text", text, "````", "");
    lines.push(`识别结果：\`${JSON.stringify(scenario.recognized)}\``, "", "最终卡片：", "");
    if (scenario.cards.length === 0) lines.push(`- 无（decision / fail-closed：\`${JSON.stringify(scenario.unresolvedReasons)}\`）`);
    for (const card of scenario.cards) lines.push(`- \`${card.template}\` — Front：${inline(card.front)}；Back：${inline(card.back)}`);
    lines.push("");
  }
  return `${lines.join("\n")}\n`;
}
function inline(value) { return `\`${String(value).replace(/`/gu, "\\`").replace(/\r?\n/gu, "\\n")}\``; }

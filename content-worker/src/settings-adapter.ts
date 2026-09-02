import { constants as fsConstants } from "node:fs";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { loadCategoryRegistry } from "./contracts.js";
import { loadPrompt } from "./prompts.js";
import type { CategoryRegistry, CommonArtifactManifest, JsonObject, LanguagePracticeMode } from "./types.js";
import { fileDigest, packagePath, readJson, requireString, sha256, stableStringify } from "./util.js";
import { WORKER_ID, WORKER_VERSION } from "./worker.js";

const BUILTIN_PROMPT_SET_ID = "open-md-v3";
const BUILTIN_CATEGORY_REGISTRY_ID = "category-registry/2.0.0";
const DRAFT_FILE = "content-settings-draft.v1.json";
const PROMPT_SIZE_LIMIT = 512 * 1024;

interface ResourceDigest {
  path: string;
  sha256: string;
  bytes: number;
}

interface ContentSettingsDraft {
  schemaVersion: "content-settings-draft/1.0.0";
  draftId: string;
  basedOn: {
    workerId: typeof WORKER_ID;
    workerVersion: string;
    promptSetId: string;
    promptSetSha256: string;
    promptFiles: ResourceDigest[];
    categoryRegistryId: string;
    categoryRegistrySha256: string;
  };
  resources: {
    promptRoot: "prompts";
    categoryRegistryPath: "config/categories.v2.json";
  };
  security: {
    credentials: "process-injected-only";
    taskPackageSecrets: "forbidden";
  };
}

export interface ContentRuntimePolicy {
  profileId: string;
  profileVersion: string;
  languagePracticeDefault: "ask" | LanguagePracticeMode;
  provider: {
    model: "deepseek-chat" | "deepseek-reasoner";
    maxRetries: number;
  };
}

export interface ContentConfigSnapshot {
  schemaVersion: "content-config-snapshot/1.0.0" | "content-config-snapshot/1.1.0";
  settingsRevisionId: string;
  workerCompatibility: { workerId: typeof WORKER_ID; minimumVersion: string };
  promptSet: {
    id: string;
    source: "user-copy";
    promptRootPath: string;
    sha256: string;
    files: Array<ResourceDigest & { packagePath: string }>;
  };
  categoryRegistry: {
    id: string;
    schemaVersion: "category-registry/2.0.0";
    packagePath: string;
    sha256: string;
    bytes: number;
  };
  providerBoundary: {
    provider: "deepseek";
    credentials: "process-injected-only";
  };
  runtimePolicy?: ContentRuntimePolicy;
}

export interface RuntimeContentResources {
  settingsRevisionId: string;
  promptSetId: string;
  promptSetSha256: string;
  categoryRegistryId: string;
  categoryRegistrySha256: string;
  promptRoot: string;
  registryPath: string;
  capabilityProfileId?: string;
  capabilityProfileVersion?: string;
  capabilityProfileSha256?: string;
  languagePracticeDefault?: "ask" | LanguagePracticeMode;
  providerModel?: "deepseek-chat" | "deepseek-reasoner";
  providerMaxRetries?: number;
}

export async function describeContentCompanion(workerRoot: string): Promise<JsonObject> {
  const builtIn = await inspectBuiltInResources(workerRoot);
  return {
    schemaVersion: "content-companion-descriptor/1.1.0",
    companionId: "content.language-practice-settings",
    companionVersion: "1.1.0",
    worker: { id: WORKER_ID, minimumVersion: "0.7.0", currentVersion: WORKER_VERSION },
    settingsSchema: {
      schemaVersion: "content-companion-settings/1.0.0",
      path: "schemas/content-companion-settings.v1.schema.json",
    },
    taskSnapshot: {
      artifactKind: "content-config-snapshot",
      commonContractVersion: "1.0.0",
      schemaVersion: "content-config-snapshot/1.1.0",
      requestPayloadField: "contentConfigPath",
      requestRevisionField: "configSnapshot.contentSettingsRevisionId",
      shaBound: true,
    },
    languageRecognition: {
      status: "supported",
      categories: ["language_zh", "language_en"],
      nonLanguageBehavior: "no-language-choice",
    },
    languagePractice: {
      defaultChoice: {
        key: "languagePractice.defaultChoice",
        status: "supported-snapshot-bound",
        snapshotPath: "runtimePolicy.languagePracticeDefault",
        values: ["ask", "meaning-reading", "writing", "both"],
        default: "ask",
      },
      promptAfterRecognition: {
        key: "interaction.promptAfterRecognition",
        status: "conditional-core-adapter",
        owner: "core-ui",
        workerBehavior: "not-consumed",
        testAction: "Select a language input and verify Core persists human_overrides.languagePracticeMode in a new attempt before run.",
      },
      choices: [
        { id: "meaning-reading", label: "含义与读音", output: ["language_context"] },
        { id: "writing", label: "练习书写", output: ["language_writing"], frontRule: "replace the unique target term with （读音；意思）", backRule: "exact target term" },
        { id: "both", label: "双向提取", output: ["language_context", "language_writing"], orderFixed: true, exactCardCountPerSafeUnit: 2 },
      ],
    },
    ocrHumanResolution: {
      status: "supported-module-adapter-core-conditional",
      unresolvedInput: { artifactKind: "ocr-proofread-bundle", contractVersion: "1.0.0", state: "unresolved", immutable: true },
      resumeCommand: {
        outerCommandOwner: "core-orchestrator",
        outerCommand: "task.provide_input",
        moduleDecisionKind: "ocr-human-resolution",
        schemaVersion: "knowledge-clipper-ocr-human-resolution-command/1.0.0",
        schemaPath: "schemas/ocr-human-resolution-command.v1.schema.json",
        actions: ["accept_original", "user_correction"],
        targetTokenRequired: true,
      },
      taskSnapshot: {
        schemaVersion: "standard-input/5.0.0",
        schemaPath: "schemas/content-input.v5.schema.json",
        resolutionPath: "items[].ocr_human_resolution",
        shaBound: true,
        appendOnlyNewAttempt: true,
      },
      runtime: {
        rerunOcrDefault: false,
        rerunOcrOperation: "not-owned-by-content-resume",
        standardReviewStages: ["cleaning", "classification", "card_generation"],
        userCharactersProtected: true,
        remainingAmbiguity: "unresolved",
      },
      outputs: [
        { kind: "ocr-human-resolution-bundle", contractVersion: "1.0.0", provenance: "human_submitted" },
        { kind: "learning-bundle", contractVersion: "3.0.0" },
        { kind: "card-bundle", contractVersion: "1.0.0", conditional: "learning-ready" },
      ],
      ownership: {
        worker: ["frozen-artifact-validation", "user-diff-replay", "protected-character-gate", "content-review", "destination-neutral-bundles"],
        core: ["waiting_input", "targetToken", "ancestry", "original-destinations", "new-attempt", "inputRefs"],
        desktop: ["collect-explicit-user-action", "show-before-candidate-diff-reason"],
      },
      testAction: "Resume one frozen unresolved OCR result with accept_original and user_correction; verify a new attempt, zero ocr_proofreading calls, human_submitted provenance, and downstream-neutral bundles.",
    },
    promptProfile: {
      id: BUILTIN_PROMPT_SET_ID,
      version: "3.0.0",
      status: "built-in-read-only-copy-edit-validate",
      sha256: builtIn.promptSet.sha256,
      categoryRegistry: { id: builtIn.categoryRegistry.id, sha256: builtIn.categoryRegistry.sha256 },
      adjustable: { mechanism: "user-copy-markdown", constraints: ["path-containment", "non-empty", "size-limit", "control-character", "secret-pattern", "category-reference", "sha256"] },
    },
    providerParameters: {
      model: { key: "provider.model", status: "supported-snapshot-bound", snapshotPath: "runtimePolicy.provider.model", values: ["deepseek-chat", "deepseek-reasoner"], default: "deepseek-chat", secret: false },
      maxRetries: { key: "provider.maxRetries", status: "supported-snapshot-bound", snapshotPath: "runtimePolicy.provider.maxRetries", minimum: 1, maximum: 5, default: 3, secret: false },
      baseUrl: { key: "provider.baseUrl", status: "conditional-control-plane-local", workerEnvironmentVariable: "AI_PRODUCER_BASE_URL", taskSnapshot: "forbidden", secret: false },
      apiKey: { key: "provider.apiKey", status: "local-secret", taskSnapshot: "forbidden", valueExposed: false },
      temperature: { key: "provider.temperature", status: "read-only", value: 0 },
      maxTokens: { key: "provider.maxTokens", status: "read-only", value: 8192 },
    },
    safety: { unknownSettings: "reject", credentialsInSettingsOrTaskPackage: "forbidden", unresolvedLanguageDetails: "fail-closed" },
    commands: { describe: "settings companion --json", validateProjectSettings: "settings profile validate --path <absolute-json> --json" },
  };
}

interface ContentCompanionSettings {
  schemaVersion: "content-companion-settings/1.0.0";
  profileId: string;
  profileVersion: string;
  languagePractice: { defaultChoice: "ask" | LanguagePracticeMode };
  interaction: { promptAfterRecognition: boolean };
  promptProfile: { promptSetId: string; profileVersion: string };
  provider: { model: "deepseek-chat" | "deepseek-reasoner"; maxRetries: number };
  security: { credentials: "local-secret-only"; taskPackageSecrets: "forbidden" };
}

export async function validateContentCompanionSettings(settingsPath: string): Promise<JsonObject> {
  requireAbsolute(settingsPath, "--path");
  try {
    const settings = await readJson<ContentCompanionSettings>(settingsPath);
    validateCompanionSettings(settings);
    const digest = await fileDigest(settingsPath);
    return {
      schemaVersion: "content-companion-settings-validation/1.0.0",
      valid: true,
      path: settingsPath,
      sha256: digest.sha256,
      bytes: digest.size,
      profileId: settings.profileId,
      profileVersion: settings.profileVersion,
      runtimeProjection: {
        profileId: settings.profileId,
        profileVersion: settings.profileVersion,
        languagePracticeDefault: settings.languagePractice.defaultChoice,
        provider: settings.provider,
      },
      conditional: { promptAfterRecognition: settings.interaction.promptAfterRecognition, owner: "core-ui", workerConsumed: false },
      credentialsRecorded: false,
    };
  } catch (error) {
    return { schemaVersion: "content-companion-settings-validation/1.0.0", valid: false, path: settingsPath, errors: [{ code: "SETTINGS_INVALID", message: errorMessage(error) }] };
  }
}

export async function describeContentSettings(workerRoot: string): Promise<JsonObject> {
  const builtIn = await inspectBuiltInResources(workerRoot);
  return {
    schemaVersion: "content-settings-adapter/1.0.0",
    worker: { id: WORKER_ID, version: WORKER_VERSION },
    credentialsPolicy: {
      owner: "control-plane-local",
      persistence: "local-secret-store-or-user-environment",
      workerConsumption: "existing-discovery-or-process-injection",
      taskPackages: "forbidden",
      logsReceiptsFixtures: "forbidden",
    },
    provider: {
      id: setting("provider.id", "read-only", "deepseek"),
      apiKey: { key: "provider.apiKey", status: "local-secret", environmentVariable: "AI_PRODUCER_API_KEY", valueExposed: false },
      model: { key: "provider.model", status: "supported-snapshot-bound-or-control-plane-local", snapshotPath: "runtimePolicy.provider.model", environmentVariable: "AI_PRODUCER_MODEL", allowedValues: ["deepseek-chat", "deepseek-reasoner"], default: "deepseek-chat", secret: false },
      baseUrl: { key: "provider.baseUrl", status: "supported", scope: "control-plane-local", environmentVariable: "AI_PRODUCER_BASE_URL", default: "https://api.deepseek.com", secret: false },
      maxRetries: { key: "provider.maxRetries", status: "supported-advanced-snapshot-bound-or-control-plane-local", snapshotPath: "runtimePolicy.provider.maxRetries", environmentVariable: "AI_PRODUCER_MAX_RETRIES", default: 3, minimum: 1, maximum: 5, secret: false },
      temperature: setting("provider.temperature", "read-only", 0),
      maxTokens: setting("provider.maxTokens", "read-only", 8192),
      requestTimeoutMs: setting("provider.requestTimeoutMs", "read-only", 240000),
    },
    resources: {
      promptSet: { id: BUILTIN_PROMPT_SET_ID, status: "built-in-read-only", copyToUserSupported: true, externalEditingSupported: true, ...builtIn.promptSet },
      categoryRegistry: { status: "built-in-read-only", copyToUserSupported: true, ...builtIn.categoryRegistry },
      runtimeSelection: { status: "supported", artifactKind: "content-config-snapshot", contractVersion: "1.0.0", moduleSchemaVersions: ["content-config-snapshot/1.0.0", "content-config-snapshot/1.1.0"], requestPayloadField: "contentConfigPath" },
    },
    processing: {
      ocrProofreading: {
        enabled: setting("ocrProofreading.enabled", "read-only-source-conditional", true),
        sourceCondition: setting("ocrProofreading.sourceCondition", "read-only", "standard-input/4.0.0 source.kind=ocr_text with verified image.ocr.mineru provenance"),
        promptResource: setting("ocrProofreading.promptResource", "resource-selectable", "stages/ocr-proofreading.md"),
        maximumEditDistance: setting("ocrProofreading.maximumEditDistance", "read-only", 2),
        minimumConfidence: setting("ocrProofreading.minimumConfidence", "read-only", 0.9),
        ambiguousPolicy: setting("ocrProofreading.ambiguousPolicy", "read-only", "retain-source-and-unresolved"),
      },
      ocrHumanResolution: {
        commandSchema: setting("ocrHumanResolution.commandSchema", "read-only", "knowledge-clipper-ocr-human-resolution-command/1.0.0"),
        inputSchema: setting("ocrHumanResolution.inputSchema", "read-only", "standard-input/5.0.0"),
        actions: setting("ocrHumanResolution.actions", "read-only", ["accept_original", "user_correction"]),
        rerunOcrDefault: setting("ocrHumanResolution.rerunOcrDefault", "read-only", false),
        coreResumeBinding: setting("ocrHumanResolution.coreResumeBinding", "conditional-core-adapter", "task.provide_input"),
      },
      cleaning: {
        enabled: setting("cleaning.enabled", "read-only", true),
        promptResource: setting("cleaning.promptResource", "resource-selectable", "stages/cleaning.md"),
        deletionOnly: setting("cleaning.deletionOnly", "read-only", true),
        gateThresholds: { key: "cleaning.gateThresholds", status: "unsupported" },
      },
      classification: {
        enabled: setting("classification.enabled", "read-only", true),
        promptResource: setting("classification.promptResource", "resource-selectable", "stages/classification.md"),
        categoryRegistry: setting("classification.categoryRegistry", "resource-selectable", BUILTIN_CATEGORY_REGISTRY_ID),
        categoryOverride: { key: "classification.categoryOverride", status: "supported-advanced", taskInputPath: "items[].human_overrides.units.<unitId>.category_id" },
      },
      learning: {
        requestedTemplates: { key: "learning.requestedTemplates", status: "supported", taskInputPath: "items[].hints.requested_templates", allowedValuesFrom: "selected category allowed_templates" },
        answerAndRiskOverrides: { key: "learning.answerAndRiskOverrides", status: "supported-advanced", taskInputPath: "items[].human_overrides.units.<unitId>" },
      },
      cards: {
        languagePracticeMode: {
          key: "cards.languagePracticeMode",
          status: "supported",
          taskInputPaths: ["human_overrides.languagePracticeMode", "languagePracticeMode", "items[].human_overrides.languagePracticeMode", "items[].hints.languagePracticeMode"],
          values: [
            { id: "meaning-reading", label: "含义与读音", output: ["language_context"] },
            { id: "writing", label: "练习书写", output: ["language_writing"] },
            { id: "both", label: "双向提取", output: ["language_context", "language_writing"], orderFixed: true },
          ],
          projectDefault: { status: "supported-snapshot-bound", snapshotPath: "runtimePolicy.languagePracticeDefault", values: ["ask", "meaning-reading", "writing", "both"] },
          promptAfterRecognition: { status: "conditional-core-adapter", owner: "core-ui", workerConsumed: false },
        },
        writingDetails: { key: "cards.writingDetails", status: "supported-advanced", taskInputPath: "human_overrides.languageWriting.units.<unitId>", failClosed: true },
        date: { key: "cards.date", status: "unsupported" },
        routing: { key: "cards.routing", status: "unsupported" },
        destination: { key: "cards.destination", status: "unsupported" },
      },
    },
    promptSecurity: {
      userCopiesAreTrustedExecutableConfiguration: true,
      validation: ["path-containment", "non-empty", "size-limit", "control-character", "secret-pattern", "category-reference", "sha256"],
      semanticPromptInjectionReview: "human-review-required",
    },
    commands: {
      describe: "settings describe --json",
      copy: "settings copy --target <absolute-directory> --json",
      validate: "settings validate --root <absolute-directory> --json",
      diff: "settings diff --root <absolute-directory> --json",
    },
  };
}

export async function copyBuiltInSettings(workerRoot: string, targetRoot: string): Promise<JsonObject> {
  requireAbsolute(targetRoot, "--target");
  const builtIn = await inspectBuiltInResources(workerRoot);
  await mkdir(targetRoot, { recursive: false });
  for (const file of builtIn.promptSet.files) {
    const target = packagePath(targetRoot, `prompts/${file.path}`);
    await mkdir(path.dirname(target), { recursive: true });
    await copyFile(path.join(workerRoot, "prompts", ...file.path.split("/")), target, fsConstants.COPYFILE_EXCL);
  }
  const registryTarget = packagePath(targetRoot, "config/categories.v2.json");
  await mkdir(path.dirname(registryTarget), { recursive: true });
  await copyFile(path.join(workerRoot, "config", "categories.v2.json"), registryTarget, fsConstants.COPYFILE_EXCL);
  const draft: ContentSettingsDraft = {
    schemaVersion: "content-settings-draft/1.0.0",
    draftId: `content-settings-${builtIn.promptSet.sha256.slice(0, 16)}`,
    basedOn: {
      workerId: WORKER_ID,
      workerVersion: WORKER_VERSION,
      promptSetId: BUILTIN_PROMPT_SET_ID,
      promptSetSha256: builtIn.promptSet.sha256,
      promptFiles: builtIn.promptSet.files,
      categoryRegistryId: BUILTIN_CATEGORY_REGISTRY_ID,
      categoryRegistrySha256: builtIn.categoryRegistry.sha256,
    },
    resources: { promptRoot: "prompts", categoryRegistryPath: "config/categories.v2.json" },
    security: { credentials: "process-injected-only", taskPackageSecrets: "forbidden" },
  };
  await writeFile(path.join(targetRoot, DRAFT_FILE), `${stableStringify(draft, 2)}\n`, { flag: "wx" });
  return { schemaVersion: "content-settings-copy-result/1.0.0", targetRoot, draftFile: path.join(targetRoot, DRAFT_FILE), validation: await validateContentSettingsDraft(workerRoot, targetRoot) };
}

export async function validateContentSettingsDraft(workerRoot: string, draftRoot: string): Promise<JsonObject> {
  requireAbsolute(draftRoot, "--root");
  const errors: Array<{ code: string; path?: string; message: string }> = [];
  const warnings: Array<{ code: string; path?: string; message: string }> = [];
  let draft: ContentSettingsDraft | undefined;
  try {
    draft = await readJson<ContentSettingsDraft>(path.join(draftRoot, DRAFT_FILE));
    validateDraftHeader(draft);
  } catch (error) {
    errors.push({ code: "DRAFT_INVALID", message: errorMessage(error) });
  }
  let tree: Awaited<ReturnType<typeof inspectResourceTree>> | undefined;
  if (draft) {
    try {
      tree = await inspectResourceTree(packagePath(draftRoot, draft.resources.promptRoot), packagePath(draftRoot, draft.resources.categoryRegistryPath));
      warnings.push(...tree.warnings);
    } catch (error) {
      errors.push({ code: "RESOURCE_INVALID", message: errorMessage(error) });
    }
  }
  return {
    schemaVersion: "content-settings-validation/1.0.0",
    valid: errors.length === 0,
    worker: { id: WORKER_ID, version: WORKER_VERSION },
    draftRoot,
    errors,
    warnings,
    ...(draft && tree ? {
      settingsRevisionCandidate: `content-settings-${sha256(stableStringify({ promptSet: tree.promptSet.sha256, registry: tree.categoryRegistry.sha256 })).slice(0, 24)}`,
      promptSet: tree.promptSet,
      categoryRegistry: tree.categoryRegistry,
      diff: diffFromBaseline(draft, tree),
    } : {}),
  };
}

export async function diffContentSettingsDraft(workerRoot: string, draftRoot: string): Promise<JsonObject> {
  const validation = await validateContentSettingsDraft(workerRoot, draftRoot);
  let basedOn: ContentSettingsDraft["basedOn"] | null = null;
  try {
    basedOn = (await readJson<ContentSettingsDraft>(path.join(draftRoot, DRAFT_FILE))).basedOn;
  } catch {
    // The validation payload already carries the actionable draft error.
  }
  return {
    schemaVersion: "content-settings-diff/1.0.0",
    valid: validation.valid,
    draftRoot,
    basedOn,
    current: { promptSet: validation.promptSet, categoryRegistry: validation.categoryRegistry },
    changes: validation.diff,
    warnings: validation.warnings,
    errors: validation.errors,
  };
}

export async function loadRuntimeContentResources(jobRoot: string, artifact: CommonArtifactManifest, entrypointPath: string): Promise<RuntimeContentResources> {
  if (artifact.kind !== "content-config-snapshot" || artifact.contractVersion !== "1.0.0") throw new Error("INPUT_INVALID content config artifact identity");
  const entry = artifact.files.find((file) => file.path === entrypointPath);
  if (!entry) throw new Error(`INPUT_INVALID content config entrypoint not declared ${entrypointPath}`);
  const snapshot = await readJson<ContentConfigSnapshot>(packagePath(jobRoot, entrypointPath));
  validateSnapshotHeader(snapshot);
  const declared = new Map(artifact.files.map((file) => [file.path, file]));
  const promptRoot = packagePath(jobRoot, snapshot.promptSet.promptRootPath);
  for (const file of snapshot.promptSet.files) {
    const expectedPath = `${snapshot.promptSet.promptRootPath.replace(/\/+$/u, "")}/${file.path}`;
    if (file.packagePath !== expectedPath) throw new Error(`INPUT_INVALID content config prompt path ${file.path}`);
    const manifestFile = declared.get(file.packagePath);
    if (!manifestFile || manifestFile.sha256 !== file.sha256 || manifestFile.bytes !== file.bytes) throw new Error(`INPUT_TAMPERED content config prompt ${file.path}`);
  }
  const registryFile = declared.get(snapshot.categoryRegistry.packagePath);
  if (!registryFile || registryFile.sha256 !== snapshot.categoryRegistry.sha256 || registryFile.bytes !== snapshot.categoryRegistry.bytes) throw new Error("INPUT_TAMPERED content config category registry");
  const registryPath = packagePath(jobRoot, snapshot.categoryRegistry.packagePath);
  const inspected = await inspectResourceTree(promptRoot, registryPath);
  if (inspected.promptSet.sha256 !== snapshot.promptSet.sha256) throw new Error("INPUT_TAMPERED content config prompt set sha256");
  if (inspected.categoryRegistry.sha256 !== snapshot.categoryRegistry.sha256 || inspected.categoryRegistry.id !== snapshot.categoryRegistry.id) throw new Error("INPUT_TAMPERED content config category registry identity");
  return {
    settingsRevisionId: snapshot.settingsRevisionId,
    promptSetId: snapshot.promptSet.id,
    promptSetSha256: snapshot.promptSet.sha256,
    categoryRegistryId: snapshot.categoryRegistry.id,
    categoryRegistrySha256: snapshot.categoryRegistry.sha256,
    promptRoot,
    registryPath,
    ...(snapshot.runtimePolicy ? {
      capabilityProfileId: snapshot.runtimePolicy.profileId,
      capabilityProfileVersion: snapshot.runtimePolicy.profileVersion,
      capabilityProfileSha256: sha256(stableStringify(snapshot.runtimePolicy)),
      languagePracticeDefault: snapshot.runtimePolicy.languagePracticeDefault,
      providerModel: snapshot.runtimePolicy.provider.model,
      providerMaxRetries: snapshot.runtimePolicy.provider.maxRetries,
    } : {}),
  };
}

export async function loadBuiltInRuntimeContentResources(workerRoot: string): Promise<RuntimeContentResources> {
  const builtIn = await inspectBuiltInResources(workerRoot);
  return {
    settingsRevisionId: "builtin-open-md-v3",
    promptSetId: BUILTIN_PROMPT_SET_ID,
    promptSetSha256: builtIn.promptSet.sha256,
    categoryRegistryId: builtIn.categoryRegistry.id,
    categoryRegistrySha256: builtIn.categoryRegistry.sha256,
    promptRoot: path.join(workerRoot, "prompts"),
    registryPath: path.join(workerRoot, "config", "categories.v2.json"),
  };
}

async function inspectBuiltInResources(workerRoot: string): Promise<Awaited<ReturnType<typeof inspectResourceTree>>> {
  return inspectResourceTree(path.join(workerRoot, "prompts"), path.join(workerRoot, "config", "categories.v2.json"));
}

async function inspectResourceTree(promptRoot: string, registryPath: string): Promise<{
  promptSet: { sha256: string; files: ResourceDigest[] };
  categoryRegistry: { id: string; sha256: string; bytes: number; path: string };
  warnings: Array<{ code: string; path?: string; message: string }>;
}> {
  const registry = await loadCategoryRegistry(registryPath);
  assertNoSecretMaterial(registry, "categoryRegistry");
  if (registry.schema_version !== BUILTIN_CATEGORY_REGISTRY_ID) throw new Error(`CATEGORY_REGISTRY_INVALID unsupported editable schema ${registry.schema_version}`);
  await validateAllPromptCompositions(promptRoot, registry);
  const warnings: Array<{ code: string; path?: string; message: string }> = [];
  const files = await Promise.all(requiredPromptFiles(registry).map(async (relativePath) => {
    const absolute = packagePath(promptRoot, relativePath);
    const content = await readFile(absolute, "utf8");
    const digest = await fileDigest(absolute);
    validatePromptContent(content, digest.size, relativePath, warnings);
    return { path: relativePath, sha256: digest.sha256, bytes: digest.size };
  }));
  const registryDigest = await fileDigest(registryPath);
  return {
    promptSet: { sha256: promptSetDigest(files), files },
    categoryRegistry: { id: registry.schema_version, sha256: registryDigest.sha256, bytes: registryDigest.size, path: "config/categories.v2.json" },
    warnings,
  };
}

async function validateAllPromptCompositions(promptRoot: string, registry: CategoryRegistry): Promise<void> {
  await loadPrompt(promptRoot, "ocr_proofreading");
  await loadPrompt(promptRoot, "cleaning");
  await loadPrompt(promptRoot, "classification");
  await loadPrompt(promptRoot, "question_answer");
  for (const category of registry.categories) {
    await loadPrompt(promptRoot, "card_generation", category);
    if (category.allowed_templates.includes("language_writing")) await loadPrompt(promptRoot, "language_writing", category);
  }
}

function requiredPromptFiles(registry: CategoryRegistry): string[] {
  const files = new Set(["shared/system.md", "shared/card-output-contract.md", "stages/ocr-proofreading.md", "stages/cleaning.md", "stages/classification.md", "stages/language-writing.md", "stages/question-answer.md"]);
  for (const category of registry.categories) {
    files.add(normalizeResourcePath(category.prompt_file));
    for (const promptFile of Object.values(category.template_prompt_files ?? {})) files.add(normalizeResourcePath(promptFile));
  }
  return [...files].sort((left, right) => left.localeCompare(right));
}

function normalizeResourcePath(value: string): string {
  const normalized = value.replace(/\\/gu, "/");
  if (normalized.startsWith("/") || normalized.split("/").includes("..")) throw new Error(`PROMPT_INVALID path ${value}`);
  return normalized;
}

function validatePromptContent(content: string, bytes: number, relativePath: string, warnings: Array<{ code: string; path?: string; message: string }>): void {
  if (content.trim() === "") throw new Error(`PROMPT_INVALID empty ${relativePath}`);
  if (bytes > PROMPT_SIZE_LIMIT) throw new Error(`PROMPT_INVALID too large ${relativePath}`);
  if (/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/u.test(content)) throw new Error(`PROMPT_INVALID control character ${relativePath}`);
  if (/\bsk-[A-Za-z0-9_-]{16,}\b/u.test(content) || /(?:AI_PRODUCER_API_KEY|api[_ -]?key|access[_ -]?token|client[_ -]?secret)\s*[:=]\s*["']?[^\s"']{12,}/iu.test(content)) throw new Error(`PROMPT_SECRET_DETECTED ${relativePath}`);
  if (/(?:ignore|disregard)\s+(?:all\s+)?(?:previous|prior)\s+instructions|忽略.{0,12}(?:此前|之前|系统)(?:指令|提示)/iu.test(content)) {
    warnings.push({ code: "PROMPT_INJECTION_REVIEW", path: relativePath, message: "Prompt 包含潜在指令优先级改写措辞，发布前需要人工 diff 审查" });
  }
}

function promptSetDigest(files: ResourceDigest[]): string {
  return sha256(stableStringify(files.map((file) => ({ path: file.path, sha256: file.sha256, bytes: file.bytes }))));
}

function validateDraftHeader(draft: ContentSettingsDraft): void {
  assertNoSecretMaterial(draft, "settingsDraft");
  rejectKeys(draft, ["schemaVersion", "draftId", "basedOn", "resources", "security"], "settingsDraft");
  if (draft.schemaVersion !== "content-settings-draft/1.0.0") throw new Error("SETTINGS_INVALID schemaVersion");
  requireString(draft.draftId, "draftId");
  if (draft.basedOn?.workerId !== WORKER_ID || !draft.basedOn.promptFiles || !Array.isArray(draft.basedOn.promptFiles)) throw new Error("SETTINGS_INVALID basedOn");
  if (draft.resources?.promptRoot !== "prompts" || draft.resources.categoryRegistryPath !== "config/categories.v2.json") throw new Error("SETTINGS_INVALID resources");
  if (draft.security?.credentials !== "process-injected-only" || draft.security.taskPackageSecrets !== "forbidden") throw new Error("SETTINGS_INVALID security");
}

function validateSnapshotHeader(snapshot: ContentConfigSnapshot): void {
  assertNoSecretMaterial(snapshot, "contentConfigSnapshot");
  rejectKeys(snapshot, ["schemaVersion", "settingsRevisionId", "workerCompatibility", "promptSet", "categoryRegistry", "providerBoundary", "runtimePolicy"], "contentConfigSnapshot");
  if (snapshot.schemaVersion !== "content-config-snapshot/1.0.0" && snapshot.schemaVersion !== "content-config-snapshot/1.1.0") throw new Error("INPUT_INVALID content config schemaVersion");
  requireString(snapshot.settingsRevisionId, "settingsRevisionId");
  if (snapshot.workerCompatibility?.workerId !== WORKER_ID) throw new Error("INPUT_INVALID content config workerId");
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(snapshot.workerCompatibility.minimumVersion) || compareVersions(WORKER_VERSION, snapshot.workerCompatibility.minimumVersion) < 0) throw new Error("INPUT_INVALID content config worker version incompatible");
  if (snapshot.promptSet?.source !== "user-copy" || !Array.isArray(snapshot.promptSet.files) || snapshot.promptSet.files.length === 0) throw new Error("INPUT_INVALID content config promptSet");
  if (!/^[a-f0-9]{64}$/u.test(snapshot.promptSet.sha256)) throw new Error("INPUT_INVALID content config promptSet.sha256");
  packagePath("validation-root", snapshot.promptSet.promptRootPath);
  if (snapshot.categoryRegistry?.schemaVersion !== BUILTIN_CATEGORY_REGISTRY_ID || !/^[a-f0-9]{64}$/u.test(snapshot.categoryRegistry.sha256)) throw new Error("INPUT_INVALID content config categoryRegistry");
  packagePath("validation-root", snapshot.categoryRegistry.packagePath);
  if (snapshot.providerBoundary?.provider !== "deepseek" || snapshot.providerBoundary.credentials !== "process-injected-only") throw new Error("INPUT_INVALID content config providerBoundary");
  if (snapshot.schemaVersion === "content-config-snapshot/1.1.0") {
    if (!snapshot.runtimePolicy) throw new Error("INPUT_INVALID content config runtimePolicy required");
    validateRuntimePolicy(snapshot.runtimePolicy);
  } else if (snapshot.runtimePolicy) {
    throw new Error("INPUT_INVALID content config runtimePolicy requires schemaVersion 1.1.0");
  }
}

function validateCompanionSettings(settings: ContentCompanionSettings): void {
  assertNoSecretMaterial(settings, "contentCompanionSettings");
  rejectKeys(settings, ["schemaVersion", "profileId", "profileVersion", "languagePractice", "interaction", "promptProfile", "provider", "security"], "contentCompanionSettings");
  if (settings.schemaVersion !== "content-companion-settings/1.0.0") throw new Error("SETTINGS_INVALID schemaVersion");
  requireString(settings.profileId, "profileId");
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(settings.profileVersion)) throw new Error("SETTINGS_INVALID profileVersion");
  rejectKeys(settings.languagePractice, ["defaultChoice"], "contentCompanionSettings.languagePractice");
  if (!["ask", "meaning-reading", "writing", "both"].includes(settings.languagePractice.defaultChoice)) throw new Error("SETTINGS_INVALID languagePractice.defaultChoice");
  rejectKeys(settings.interaction, ["promptAfterRecognition"], "contentCompanionSettings.interaction");
  if (typeof settings.interaction.promptAfterRecognition !== "boolean") throw new Error("SETTINGS_INVALID interaction.promptAfterRecognition");
  rejectKeys(settings.promptProfile, ["promptSetId", "profileVersion"], "contentCompanionSettings.promptProfile");
  requireString(settings.promptProfile.promptSetId, "promptProfile.promptSetId");
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(settings.promptProfile.profileVersion)) throw new Error("SETTINGS_INVALID promptProfile.profileVersion");
  validateProviderPolicy(settings.provider, "contentCompanionSettings.provider");
  rejectKeys(settings.security, ["credentials", "taskPackageSecrets"], "contentCompanionSettings.security");
  if (settings.security.credentials !== "local-secret-only" || settings.security.taskPackageSecrets !== "forbidden") throw new Error("SETTINGS_INVALID security");
}

function validateRuntimePolicy(policy: ContentRuntimePolicy): void {
  rejectKeys(policy, ["profileId", "profileVersion", "languagePracticeDefault", "provider"], "contentConfigSnapshot.runtimePolicy");
  requireString(policy.profileId, "runtimePolicy.profileId");
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(policy.profileVersion)) throw new Error("INPUT_INVALID content config runtimePolicy.profileVersion");
  if (!["ask", "meaning-reading", "writing", "both"].includes(policy.languagePracticeDefault)) throw new Error("INPUT_INVALID content config runtimePolicy.languagePracticeDefault");
  validateProviderPolicy(policy.provider, "contentConfigSnapshot.runtimePolicy.provider");
}

function validateProviderPolicy(provider: ContentRuntimePolicy["provider"], location: string): void {
  rejectKeys(provider, ["model", "maxRetries"], location);
  if (provider.model !== "deepseek-chat" && provider.model !== "deepseek-reasoner") throw new Error(`SETTINGS_INVALID ${location}.model`);
  if (!Number.isInteger(provider.maxRetries) || provider.maxRetries < 1 || provider.maxRetries > 5) throw new Error(`SETTINGS_INVALID ${location}.maxRetries`);
}

function assertNoSecretMaterial(value: unknown, location: string): void {
  if (Array.isArray(value)) {
    value.forEach((entry, index) => assertNoSecretMaterial(entry, `${location}[${index}]`));
    return;
  }
  if (value && typeof value === "object") {
    for (const [key, entry] of Object.entries(value as Record<string, unknown>)) {
      if (/(?:api.?key|access.?token|authorization|password|client.?secret)/iu.test(key)) throw new Error(`SETTINGS_SECRET_DETECTED ${location}.${key}`);
      assertNoSecretMaterial(entry, `${location}.${key}`);
    }
    return;
  }
  if (typeof value === "string" && (/\bsk-[A-Za-z0-9_-]{16,}\b/u.test(value) || /Bearer\s+[A-Za-z0-9._~-]{16,}/iu.test(value))) throw new Error(`SETTINGS_SECRET_DETECTED ${location}`);
}

function rejectKeys(value: object, allowed: string[], location: string): void {
  const unknown = Object.keys(value).filter((key) => !allowed.includes(key));
  if (unknown.length > 0) throw new Error(`SETTINGS_INVALID ${location} additional properties: ${unknown.join(",")}`);
}

function compareVersions(left: string, right: string): number {
  const leftParts = left.split("-")[0]!.split(".").map(Number);
  const rightParts = right.split("-")[0]!.split(".").map(Number);
  for (let index = 0; index < 3; index += 1) {
    const delta = (leftParts[index] ?? 0) - (rightParts[index] ?? 0);
    if (delta !== 0) return delta;
  }
  return 0;
}

function diffFromBaseline(draft: ContentSettingsDraft, current: Awaited<ReturnType<typeof inspectResourceTree>>): JsonObject {
  const baseline = new Map(draft.basedOn.promptFiles.map((file) => [file.path, file]));
  const now = new Map(current.promptSet.files.map((file) => [file.path, file]));
  const allPaths = [...new Set([...baseline.keys(), ...now.keys()])].sort((left, right) => left.localeCompare(right));
  const promptFiles = allPaths.map((resourcePath) => {
    const before = baseline.get(resourcePath);
    const after = now.get(resourcePath);
    const status = !before ? "added" : !after ? "missing" : before.sha256 === after.sha256 && before.bytes === after.bytes ? "unchanged" : "modified";
    return { path: resourcePath, status, beforeSha256: before?.sha256 ?? null, afterSha256: after?.sha256 ?? null, beforeBytes: before?.bytes ?? null, afterBytes: after?.bytes ?? null };
  });
  return {
    promptFiles,
    promptSetChanged: current.promptSet.sha256 !== draft.basedOn.promptSetSha256,
    categoryRegistryChanged: current.categoryRegistry.sha256 !== draft.basedOn.categoryRegistrySha256,
  };
}

function setting(key: string, status: string, value: unknown): JsonObject {
  return { key, status, value };
}

function requireAbsolute(value: string, name: string): void {
  if (!path.isAbsolute(value)) throw new Error(`CLI_INVALID ${name} must be absolute`);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

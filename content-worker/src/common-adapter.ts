import { access, link, mkdir, unlink, writeFile } from "node:fs/promises";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { DESTINATION_NEUTRAL_METADATA } from "./identity.js";
import { DeepSeekProvider, ReplayProvider } from "./provider.js";
import { loadBuiltInRuntimeContentResources, loadRuntimeContentResources, type RuntimeContentResources } from "./settings-adapter.js";
import type {
  ArtifactReference,
  CommonArtifactManifest,
  CommonWorkerError,
  CommonWorkerRequest,
  CommonWorkerResult,
  JsonObject,
} from "./types.js";
import { fileDigest, packagePath, readJson, requireString, sha256, stableStringify } from "./util.js";
import { runWorker, WORKER_ID, WORKER_VERSION } from "./worker.js";

export const WORKER_DESCRIBE = {
  schemaVersion: "1.0.0",
  workerId: WORKER_ID,
  workerVersion: WORKER_VERSION,
  contractVersion: "1.0.0",
  stages: ["content.understand-and-card"],
  inputKinds: ["content-input", "content-config-snapshot"],
  outputKinds: ["ocr-proofread-bundle", "ocr-human-resolution-bundle", "learning-bundle", "card-bundle", "language-practice-decision", "question.answer"],
  concurrency: { model: "parallel", maxParallelAttempts: 4 },
  sideEffectLevel: "local",
  idempotencyLevel: "idempotent",
  cancellation: {
    protocol: "attempt-signal-file-v1",
    supportedReasons: ["user_stop", "superseded", "task_cancel", "timeout"],
  },
} as const;

interface ContentRunPayload extends JsonObject {
  schemaVersion: "content-run/1.0.0";
  contentInputPath: string;
  providerMode: "replay" | "live";
  replayPath?: string;
  contentConfigPath?: string;
}

interface CancelSignal {
  schemaVersion: "1.0.0";
  attemptId: string;
  operationId: string;
  reason: "user_stop" | "superseded" | "task_cancel" | "timeout";
  requestedAt: string;
  graceDeadline: string;
}

interface LoadedInputArtifact {
  ref: ArtifactReference;
  manifest: CommonArtifactManifest;
  manifestPath: string;
}

export async function runCommonRequest(requestPath: string, workerRoot: string): Promise<CommonWorkerResult> {
  if (!path.isAbsolute(requestPath)) throw new Error("CLI_INVALID --request must be an absolute path");
  await access(requestPath);
  const request = await readJson<CommonWorkerRequest>(requestPath);
  validateRequest(request);
  const jobRoot = deriveJobRoot(requestPath, request.attemptPath);
  const resultPath = packagePath(jobRoot, request.resultPath);
  await assertDoesNotExist(resultPath, "RESULT_EXISTS");
  const startedAt = new Date().toISOString();
  const cancelPath = packagePath(jobRoot, request.cancelSignal.path);
  const abortController = new AbortController();
  let observedCancel: CancelSignal | undefined;
  let stopMonitor = false;
  const monitor = (async () => {
    while (!stopMonitor) {
      try {
        const signal = await readJson<CancelSignal>(cancelPath);
        validateCancelSignal(signal, request);
        observedCancel = signal;
        abortController.abort(signal.reason);
        return;
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      }
      await delay(25);
    }
  })();

  const initialCancel = await readCancelIfPresent(cancelPath, request);
  if (initialCancel) {
    observedCancel = initialCancel;
    abortController.abort(initialCancel.reason);
  }

  emitProgress("worker.started", request);
  let terminal: CommonWorkerResult;
  try {
    const artifacts = await loadInputArtifacts(jobRoot, request.inputRefs);
    const payload = parsePayload(request.payload);
    const contentArtifacts = artifacts.filter((entry) => entry.manifest.kind === "content-input");
    const configArtifacts = artifacts.filter((entry) => entry.manifest.kind === "content-config-snapshot");
    if (contentArtifacts.length !== 1) throw new Error("INPUT_INVALID exactly one content-input artifact is required");
    requireDeclaredFile(artifacts, payload.contentInputPath);
    const runtimeResources = await resolveRuntimeResources(jobRoot, workerRoot, request, payload, configArtifacts);
    if (payload.providerMode === "live" && artifacts.some((entry) => entry.manifest.dataClass === "secret-prohibited")) throw new Error("INPUT_INVALID secret-prohibited artifact cannot be sent to a live provider");
    const provider = payload.providerMode === "replay"
      ? await ReplayProvider.fromFile(packagePath(jobRoot, requireReplayPath(payload, artifacts)))
      : await DeepSeekProvider.create({
        ...(runtimeResources.providerModel ? { model: runtimeResources.providerModel } : {}),
        ...(runtimeResources.providerMaxRetries ? { maxRetries: runtimeResources.providerMaxRetries } : {}),
      });
    const outputDir = packagePath(jobRoot, request.outputPath);
    const workerResult = await runWorker({
      manifestPath: contentArtifacts[0]!.manifestPath,
      inputPath: packagePath(jobRoot, payload.contentInputPath),
      outputDir,
      promptRoot: runtimeResources.promptRoot,
      registryPath: runtimeResources.registryPath,
      attemptId: request.attemptId,
      operation: request.operation as "understand-and-card" | "question.answer" | "question.make-cards",
      jobRoot,
      signal: abortController.signal,
      settingsRevisionId: runtimeResources.settingsRevisionId,
      promptSetId: runtimeResources.promptSetId,
      ...(runtimeResources.promptSetSha256 ? { promptSetSha256: runtimeResources.promptSetSha256 } : {}),
      ...(runtimeResources.capabilityProfileId ? { capabilityProfileId: runtimeResources.capabilityProfileId } : {}),
      ...(runtimeResources.capabilityProfileVersion ? { capabilityProfileVersion: runtimeResources.capabilityProfileVersion } : {}),
      ...(runtimeResources.capabilityProfileSha256 ? { capabilityProfileSha256: runtimeResources.capabilityProfileSha256 } : {}),
      ...(runtimeResources.languagePracticeDefault ? { languagePracticeDefault: runtimeResources.languagePracticeDefault } : {}),
    }, provider);
    if (abortController.signal.aborted) throw new Error("WORKER_CANCELLED");

    const dataClass = strongestDataClass(artifacts.map((entry) => entry.manifest.dataClass));
    let ocrProofreadRef: ArtifactReference | undefined;
    if (workerResult.ocrProofreadBundlePath) {
      ocrProofreadRef = await publishBundleArtifact({
        jobRoot,
        request,
        bundlePath: workerResult.ocrProofreadBundlePath,
        kind: "ocr-proofread-bundle",
        contractVersion: "1.0.0",
        bundleSchemaVersion: "ocr-proofread-bundle/1.0.0",
        manifestName: "ocr-proofread-bundle.artifact.v1.json",
        inputRefs: dedupeArtifactRefs([...request.inputRefs, ...contentArtifacts[0]!.manifest.inputRefs]),
        dataClass,
      });
    }
    let ocrHumanResolutionRef: ArtifactReference | undefined;
    if (workerResult.ocrHumanResolutionBundlePath) {
      ocrHumanResolutionRef = await publishBundleArtifact({
        jobRoot,
        request,
        bundlePath: workerResult.ocrHumanResolutionBundlePath,
        kind: "ocr-human-resolution-bundle",
        contractVersion: "1.0.0",
        bundleSchemaVersion: "ocr-human-resolution-bundle/1.0.0",
        manifestName: "ocr-human-resolution-bundle.artifact.v1.json",
        inputRefs: dedupeArtifactRefs([...request.inputRefs, ...contentArtifacts[0]!.manifest.inputRefs]),
        dataClass,
      });
    }
    const learningRef = await publishBundleArtifact({
      jobRoot,
      request,
      bundlePath: workerResult.learningBundlePath,
      kind: "learning-bundle",
      contractVersion: workerResult.learningBundleContractVersion,
      bundleSchemaVersion: workerResult.learningBundleSchemaVersion,
      manifestName: workerResult.learningBundleContractVersion === "3.0.0" ? "learning-bundle.artifact.v3.json" : workerResult.learningBundleContractVersion === "2.0.0" ? "learning-bundle.artifact.v2.json" : "learning-bundle.artifact.v1.json",
      inputRefs: ocrHumanResolutionRef ? [...request.inputRefs, ocrHumanResolutionRef] : ocrProofreadRef ? [...request.inputRefs, ocrProofreadRef] : request.inputRefs,
      dataClass,
    });
    const outputs: ArtifactReference[] = [...(ocrProofreadRef ? [ocrProofreadRef] : []), ...(ocrHumanResolutionRef ? [ocrHumanResolutionRef] : []), learningRef];
    if (workerResult.questionAnswerPath) {
      outputs.push(await publishBundleArtifact({
        jobRoot,
        request,
        bundlePath: workerResult.questionAnswerPath,
        kind: "question.answer",
        manifestName: "question-answer.artifact.v1.json",
        inputRefs: [...request.inputRefs, learningRef],
        dataClass,
      }));
    }
    if (workerResult.languagePracticeDecisionPath) {
      outputs.push(await publishBundleArtifact({
        jobRoot,
        request,
        bundlePath: workerResult.languagePracticeDecisionPath,
        kind: "language-practice-decision",
        manifestName: "language-practice-decision.artifact.v1.json",
        inputRefs: [...request.inputRefs, learningRef],
        dataClass,
      }));
    }
    if (workerResult.cardBundlePath) {
      outputs.push(await publishBundleArtifact({
        jobRoot,
        request,
        bundlePath: workerResult.cardBundlePath,
        kind: "card-bundle",
        manifestName: "card-bundle.artifact.v1.json",
        inputRefs: [...request.inputRefs, learningRef],
        dataClass,
      }));
    }
    const failedItems = workerResult.itemStates.filter((entry) => entry.state === "failed_ai" || entry.state === "failed_content_gate");
    const completedItems = workerResult.itemStates.filter((entry) => entry.state !== "failed_ai" && entry.state !== "failed_content_gate");
    if (failedItems.length > 0 && completedItems.length === 0) {
      const aiFailed = failedItems.some((entry) => entry.state === "failed_ai");
      terminal = makeResult(request, startedAt, "failed", [], makeError(
        aiFailed ? "content.ai-failed" : "content.gate-failed",
        aiFailed ? "dependency" : "worker",
        aiFailed,
        toJobRelative(jobRoot, workerResult.learningBundlePath),
        failedItems.flatMap((entry) => entry.reasons).join("; "),
      ), {
        moduleReleaseState: workerResult.releaseState,
        unpublishedLearningBundleSha256: workerResult.learningBundleSha256,
      });
    } else if (failedItems.length > 0) {
      const detailsRef = toJobRelative(jobRoot, workerResult.learningBundlePath);
      const itemDiagnostics = [
        ...completedItems.map((entry) => ({ itemId: entry.itemId, status: "completed" as const, outputs, error: null })),
        ...failedItems.map((entry) => ({
          itemId: entry.itemId,
          status: "failed" as const,
          outputs: [] as ArtifactReference[],
          error: makeError("content.item-failed", entry.state === "failed_ai" ? "dependency" : "worker", entry.state === "failed_ai", detailsRef, entry.reasons.join("; ")),
        })),
      ];
      terminal = { ...makeResult(request, startedAt, "partial", outputs, null, { moduleReleaseState: workerResult.releaseState, contentSettings: publicResourceBinding(runtimeResources) }), itemDiagnostics };
    } else {
      const warnings = workerResult.releaseState === "ready" ? [] : [`module release_state=${workerResult.releaseState}; unresolved is preserved for review`];
      terminal = { ...makeResult(request, startedAt, "completed", outputs, null, { moduleReleaseState: workerResult.releaseState, contentSettings: publicResourceBinding(runtimeResources) }), warnings };
    }
  } catch (error) {
    const cancellation = observedCancel ?? await readCancelIfPresent(cancelPath, request);
    if (cancellation) {
      const timeout = cancellation.reason === "timeout";
      terminal = makeResult(
        request,
        startedAt,
        timeout ? "failed" : "cancelled",
        [],
        makeError(`cancel.${cancellation.reason}`, timeout ? "transient" : "cancelled", timeout, request.cancelSignal.path, cancellation.reason),
        { terminalDetail: cancellation.reason },
      );
    } else {
      const message = error instanceof Error ? error.message : String(error);
      const detailsRef = await writeErrorDetails(jobRoot, request, message);
      terminal = makeResult(request, startedAt, "failed", [], classifyError(message, detailsRef), { terminalDetail: message });
    }
  } finally {
    stopMonitor = true;
    await monitor.catch(() => undefined);
  }
  await writeJsonOnce(resultPath, terminal);
  emitProgress("worker.finished", request, { status: terminal.status });
  return terminal;
}

function validateRequest(request: CommonWorkerRequest): void {
  rejectUnknownKeys(request, ["schemaVersion", "requestId", "operationId", "taskId", "runId", "stageId", "attemptId", "workerId", "operation", "idempotencyKey", "attemptPath", "outputPath", "progressPath", "resultPath", "cancelSignal", "inputRefs", "configSnapshot", "payload"], "worker request");
  if (request.schemaVersion !== "1.0.0") throw new Error("CONTRACT_INVALID schemaVersion");
  for (const [name, value] of Object.entries({
    requestId: request.requestId, operationId: request.operationId, taskId: request.taskId, runId: request.runId,
    stageId: request.stageId, attemptId: request.attemptId, workerId: request.workerId, operation: request.operation,
  })) requireIdentifier(value, name);
  if (request.workerId !== WORKER_ID) throw new Error(`CONTRACT_INVALID workerId ${request.workerId}`);
  if (!["understand-and-card", "question.answer", "question.make-cards"].includes(request.operation)) throw new Error(`CONTRACT_INVALID operation ${request.operation}`);
  if (!/^[A-Fa-f0-9]{64}$/u.test(request.idempotencyKey)) throw new Error("CONTRACT_INVALID idempotencyKey");
  for (const [name, value] of Object.entries({ attemptPath: request.attemptPath, outputPath: request.outputPath, progressPath: request.progressPath, resultPath: request.resultPath, cancelPath: request.cancelSignal?.path })) requireJobRelative(value, name);
  if (request.cancelSignal?.schemaVersion !== "1.0.0" || request.cancelSignal.gracePeriodMs !== 2000) throw new Error("CONTRACT_INVALID cancelSignal");
  rejectUnknownKeys(request.cancelSignal, ["schemaVersion", "path", "gracePeriodMs"], "cancelSignal");
  for (const [name, value] of Object.entries({ outputPath: request.outputPath, progressPath: request.progressPath, resultPath: request.resultPath, cancelPath: request.cancelSignal.path })) {
    if (!isWithin(request.attemptPath, value)) throw new Error(`CONTRACT_INVALID ${name} must remain in attemptPath`);
  }
  if (!Array.isArray(request.inputRefs)) throw new Error("CONTRACT_INVALID inputRefs");
  for (const ref of request.inputRefs) validateArtifactReference(ref);
  if (!request.configSnapshot || typeof request.configSnapshot !== "object" || Array.isArray(request.configSnapshot)) throw new Error("CONTRACT_INVALID configSnapshot");
  assertNoSecrets(request.configSnapshot, "configSnapshot");
  if (!request.payload || typeof request.payload !== "object" || Array.isArray(request.payload)) throw new Error("CONTRACT_INVALID payload");
  assertNoSecrets(request.payload, "payload");
}

function parsePayload(payload: JsonObject): ContentRunPayload {
  rejectUnknownKeys(payload, ["schemaVersion", "contentInputPath", "providerMode", "replayPath", "contentConfigPath"], "content run payload");
  if (payload.schemaVersion !== "content-run/1.0.0") throw new Error("INPUT_INVALID payload.schemaVersion");
  requireJobRelative(payload.contentInputPath, "payload.contentInputPath");
  if (payload.providerMode !== "replay" && payload.providerMode !== "live") throw new Error("INPUT_INVALID payload.providerMode");
  if (payload.providerMode === "replay") requireJobRelative(payload.replayPath, "payload.replayPath");
  if (payload.contentConfigPath !== undefined) requireJobRelative(payload.contentConfigPath, "payload.contentConfigPath");
  return payload as ContentRunPayload;
}

async function resolveRuntimeResources(
  jobRoot: string,
  workerRoot: string,
  request: CommonWorkerRequest,
  payload: ContentRunPayload,
  configArtifacts: LoadedInputArtifact[],
): Promise<RuntimeContentResources> {
  if (!payload.contentConfigPath) {
    if (configArtifacts.length > 0) throw new Error("INPUT_INVALID content-config-snapshot requires payload.contentConfigPath");
    return loadBuiltInRuntimeContentResources(workerRoot);
  }
  if (configArtifacts.length !== 1) throw new Error("INPUT_INVALID exactly one content-config-snapshot artifact is required");
  if (configArtifacts[0]!.manifest.dataClass === "secret-prohibited") throw new Error("INPUT_INVALID content config cannot contain secrets");
  requireDeclaredFile(configArtifacts, payload.contentConfigPath);
  const resources = await loadRuntimeContentResources(jobRoot, configArtifacts[0]!.manifest, payload.contentConfigPath);
  if (request.configSnapshot.contentSettingsRevisionId !== resources.settingsRevisionId) throw new Error("CONTRACT_INVALID configSnapshot.contentSettingsRevisionId mismatch");
  return resources;
}

function publicResourceBinding(resources: RuntimeContentResources): JsonObject {
  return {
    settingsRevisionId: resources.settingsRevisionId,
    promptSetId: resources.promptSetId,
    ...(resources.promptSetSha256 ? { promptSetSha256: resources.promptSetSha256 } : {}),
    categoryRegistryId: resources.categoryRegistryId,
    categoryRegistrySha256: resources.categoryRegistrySha256,
    ...(resources.capabilityProfileId ? { capabilityProfileId: resources.capabilityProfileId } : {}),
    ...(resources.capabilityProfileVersion ? { capabilityProfileVersion: resources.capabilityProfileVersion } : {}),
    ...(resources.capabilityProfileSha256 ? { capabilityProfileSha256: resources.capabilityProfileSha256 } : {}),
    ...(resources.languagePracticeDefault ? { languagePracticeDefault: resources.languagePracticeDefault } : {}),
    ...(resources.providerModel ? { providerModel: resources.providerModel } : {}),
    ...(resources.providerMaxRetries ? { providerMaxRetries: resources.providerMaxRetries } : {}),
    credentialsRecorded: false,
  };
}

async function loadInputArtifacts(jobRoot: string, refs: ArtifactReference[]): Promise<LoadedInputArtifact[]> {
  const loaded: LoadedInputArtifact[] = [];
  for (const ref of refs) {
    const manifestPath = packagePath(jobRoot, ref.manifestPath);
    const digest = await fileDigest(manifestPath);
    if (digest.sha256.toLowerCase() !== ref.expectedSha256.toLowerCase()) throw new Error(`INPUT_TAMPERED ${ref.manifestPath}`);
    const manifest = await readJson<CommonArtifactManifest>(manifestPath);
    validateArtifactManifest(manifest, ref);
    for (const file of manifest.files) {
      const actual = await fileDigest(packagePath(jobRoot, file.path));
      if (actual.sha256.toLowerCase() !== file.sha256.toLowerCase() || actual.size !== file.bytes) throw new Error(`INPUT_TAMPERED ${file.path}`);
    }
    loaded.push({ ref, manifest, manifestPath });
  }
  return loaded;
}

function validateArtifactManifest(manifest: CommonArtifactManifest, ref: ArtifactReference): void {
  rejectUnknownKeys(manifest, ["schemaVersion", "artifactId", "kind", "contractVersion", "mediaType", "files", "createdAt", "producer", "inputRefs", "dataClass", "metadata"], "artifact manifest");
  if (manifest.schemaVersion !== "1.0.0" || manifest.contractVersion !== ref.contractVersion || manifest.artifactId !== ref.artifactRef || manifest.kind !== ref.kind) throw new Error(`CONTRACT_INVALID artifact identity ${ref.manifestPath}`);
  requireIdentifier(manifest.artifactId, "artifactId");
  requireIdentifier(manifest.kind, "artifact kind", /^[a-z0-9][a-z0-9._-]*$/u);
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(manifest.contractVersion) || typeof manifest.mediaType !== "string" || manifest.mediaType === "" || !Number.isFinite(Date.parse(manifest.createdAt))) throw new Error(`CONTRACT_INVALID artifact header ${ref.manifestPath}`);
  if (!manifest.producer || typeof manifest.producer !== "object" || Array.isArray(manifest.producer)) throw new Error(`CONTRACT_INVALID artifact producer ${ref.manifestPath}`);
  rejectUnknownKeys(manifest.producer, ["stageId", "workerId", "workerVersion", "attemptId"], "artifact producer");
  requireIdentifier(manifest.producer.stageId, "producer.stageId");
  requireIdentifier(manifest.producer.workerId, "producer.workerId");
  requireIdentifier(manifest.producer.attemptId, "producer.attemptId");
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(manifest.producer.workerVersion)) throw new Error("CONTRACT_INVALID producer.workerVersion");
  if (!Array.isArray(manifest.inputRefs)) throw new Error(`CONTRACT_INVALID artifact inputRefs ${ref.manifestPath}`);
  for (const inputRef of manifest.inputRefs) validateArtifactReference(inputRef);
  if (!["public", "personal", "sensitive", "secret-prohibited"].includes(manifest.dataClass)) throw new Error(`CONTRACT_INVALID artifact dataClass ${ref.manifestPath}`);
  if (!Array.isArray(manifest.files) || manifest.files.length === 0) throw new Error(`CONTRACT_INVALID artifact files ${ref.manifestPath}`);
  for (const file of manifest.files) {
    rejectUnknownKeys(file, ["path", "sha256", "bytes", "mediaType"], "artifact file");
    requireJobRelative(file.path, "artifact.files[].path");
    if (!/^[A-Fa-f0-9]{64}$/u.test(file.sha256) || !Number.isInteger(file.bytes) || file.bytes < 0 || typeof file.mediaType !== "string" || file.mediaType === "") throw new Error(`CONTRACT_INVALID artifact file ${file.path}`);
  }
}

function requireDeclaredFile(artifacts: LoadedInputArtifact[], relativePath: string): void {
  if (!artifacts.some((entry) => entry.manifest.files.some((file) => file.path === relativePath))) throw new Error(`INPUT_INVALID undeclared file ${relativePath}`);
}

function requireReplayPath(payload: ContentRunPayload, artifacts: LoadedInputArtifact[]): string {
  const replayPath = payload.replayPath;
  if (!replayPath) throw new Error("INPUT_INVALID missing replayPath");
  requireDeclaredFile(artifacts, replayPath);
  return replayPath;
}

async function publishBundleArtifact(options: {
  jobRoot: string;
  request: CommonWorkerRequest;
  bundlePath: string;
  kind: "ocr-proofread-bundle" | "ocr-human-resolution-bundle" | "learning-bundle" | "card-bundle" | "language-practice-decision" | "question.answer";
  contractVersion?: "1.0.0" | "2.0.0" | "3.0.0";
  bundleSchemaVersion?: string;
  manifestName: string;
  inputRefs: ArtifactReference[];
  dataClass: CommonArtifactManifest["dataClass"];
}): Promise<ArtifactReference> {
  const digest = await fileDigest(options.bundlePath);
  const artifactId = `${options.kind}-${sha256(`${options.request.taskId}\0${options.request.runId}\0${options.request.stageId}\0${options.request.attemptId}\0${options.kind}`).slice(0, 24)}`;
  const manifestPath = path.join(path.dirname(options.bundlePath), options.manifestName);
  const manifest: CommonArtifactManifest = {
    schemaVersion: "1.0.0",
    artifactId,
    kind: options.kind,
    contractVersion: options.contractVersion ?? "1.0.0",
    mediaType: "application/json",
    files: [{ path: toJobRelative(options.jobRoot, options.bundlePath), sha256: digest.sha256, bytes: digest.size, mediaType: "application/json" }],
    createdAt: new Date().toISOString(),
    producer: { stageId: options.request.stageId, workerId: WORKER_ID, workerVersion: WORKER_VERSION, attemptId: options.request.attemptId },
    inputRefs: options.inputRefs,
    dataClass: options.dataClass,
    metadata: {
      ...DESTINATION_NEUTRAL_METADATA,
      bundleSchemaVersion: options.bundleSchemaVersion ?? (options.kind === "learning-bundle"
        ? "learning-bundle/1.0.0"
        : options.kind === "card-bundle"
          ? "card-bundle/1.0.0"
          : options.kind === "language-practice-decision"
            ? "language-practice-decision/1.0.0"
            : options.kind === "question.answer"
            ? "question-answer/1.0.0"
              : options.kind === "ocr-human-resolution-bundle"
                ? "ocr-human-resolution-bundle/1.0.0"
                : "ocr-proofread-bundle/1.0.0"),
    },
  };
  const bytes = `${stableStringify(manifest, 2)}\n`;
  await writeTextOnce(manifestPath, bytes);
  return { artifactRef: artifactId, kind: options.kind, contractVersion: options.contractVersion ?? "1.0.0", manifestPath: toJobRelative(options.jobRoot, manifestPath), expectedSha256: sha256(bytes) };
}

function dedupeArtifactRefs(refs: ArtifactReference[]): ArtifactReference[] {
  const seen = new Set<string>();
  return refs.filter((ref) => {
    const key = `${ref.kind}\0${ref.artifactRef}\0${ref.expectedSha256.toLowerCase()}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function makeResult(request: CommonWorkerRequest, startedAt: string, status: CommonWorkerResult["status"], outputs: ArtifactReference[], error: CommonWorkerError | null, payload: JsonObject): CommonWorkerResult {
  return {
    schemaVersion: "1.0.0", requestId: request.requestId, operationId: request.operationId, taskId: request.taskId,
    runId: request.runId, stageId: request.stageId, attemptId: request.attemptId,
    worker: { id: WORKER_ID, version: WORKER_VERSION }, contractVersion: "1.0.0", idempotencyKey: request.idempotencyKey,
    status, outputs, error, startedAt, finishedAt: new Date().toISOString(), warnings: [], payload,
  };
}

function makeError(code: string, category: CommonWorkerError["category"], retryable: boolean, detailsRef: string, message: string): CommonWorkerError {
  return { code, category, retryable, detailsRef, message: message.slice(0, 2000), blame: category === "dependency" ? "dependency" : category === "cancelled" ? "user" : "worker" };
}

function classifyError(message: string, detailsRef: string): CommonWorkerError {
  if (/^(CONTRACT_INVALID|INPUT_INVALID|INPUT_TAMPERED|UNSUPPORTED_SCHEMA)/u.test(message)) return makeError("content.input-invalid", "contract", false, detailsRef, message);
  if (/^(AI_AUTH|AI_TRANSIENT|AI_CONTRACT|REPLAY_)/u.test(message)) return makeError("content.provider-failed", "dependency", !message.startsWith("AI_AUTH"), detailsRef, message);
  if (message.startsWith("OUTPUT_EXISTS") || message.startsWith("RESULT_EXISTS")) return makeError("content.append-only-conflict", "worker", false, detailsRef, message);
  return makeError("content.internal", "internal", false, detailsRef, message);
}

async function writeErrorDetails(jobRoot: string, request: CommonWorkerRequest, message: string): Promise<string> {
  const detailsPath = packagePath(jobRoot, `${request.attemptPath}/error-details.json`);
  await writeJsonOnce(detailsPath, { schemaVersion: "content-error/1.0.0", message, recordedAt: new Date().toISOString() });
  return toJobRelative(jobRoot, detailsPath);
}

async function readCancelIfPresent(cancelPath: string, request: CommonWorkerRequest): Promise<CancelSignal | undefined> {
  try {
    const signal = await readJson<CancelSignal>(cancelPath);
    validateCancelSignal(signal, request);
    return signal;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

function validateCancelSignal(signal: CancelSignal, request: CommonWorkerRequest): void {
  if (signal.schemaVersion !== "1.0.0" || signal.attemptId !== request.attemptId || signal.operationId !== request.operationId || !["user_stop", "superseded", "task_cancel", "timeout"].includes(signal.reason)) throw new Error("CONTRACT_INVALID cancel signal identity");
  const requestedAt = Date.parse(signal.requestedAt);
  const graceDeadline = Date.parse(signal.graceDeadline);
  if (!Number.isFinite(requestedAt) || graceDeadline - requestedAt !== 2000) throw new Error("CONTRACT_INVALID cancel signal grace deadline");
}

function deriveJobRoot(requestPath: string, attemptPath: string): string {
  requireJobRelative(attemptPath, "attemptPath");
  let candidate = path.dirname(path.resolve(requestPath));
  const segments = attemptPath.split(/[\\/]/u).filter(Boolean);
  for (let index = 0; index < segments.length; index += 1) candidate = path.dirname(candidate);
  if (path.resolve(candidate, ...segments) !== path.dirname(path.resolve(requestPath))) throw new Error("CONTRACT_INVALID request path does not match attemptPath");
  return candidate;
}

function validateArtifactReference(ref: ArtifactReference): void {
  if (!ref || typeof ref !== "object" || Array.isArray(ref)) throw new Error("CONTRACT_INVALID artifact reference");
  rejectUnknownKeys(ref, ["artifactRef", "kind", "contractVersion", "manifestPath", "expectedSha256"], "artifact reference");
  requireIdentifier(ref.artifactRef, "artifactRef");
  requireIdentifier(ref.kind, "artifact kind", /^[a-z0-9][a-z0-9._-]*$/u);
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(ref.contractVersion)) throw new Error("CONTRACT_INVALID artifact contractVersion");
  requireJobRelative(ref.manifestPath, "artifact manifestPath");
  if (!/^[A-Fa-f0-9]{64}$/u.test(ref.expectedSha256)) throw new Error("CONTRACT_INVALID artifact expectedSha256");
}

function requireIdentifier(value: unknown, name: string, pattern = /^[A-Za-z0-9][A-Za-z0-9._-]*$/u): asserts value is string {
  requireString(value, name);
  if (value.length > 128 || !pattern.test(value)) throw new Error(`CONTRACT_INVALID ${name}`);
}

function requireJobRelative(value: unknown, name: string): asserts value is string {
  requireString(value, name);
  if (value.length > 1024 || path.isAbsolute(value) || /(^|[\\/])\.\.([\\/]|$)/u.test(value)) throw new Error(`CONTRACT_INVALID ${name}`);
}

function strongestDataClass(values: CommonArtifactManifest["dataClass"][]): CommonArtifactManifest["dataClass"] {
  const ordered: CommonArtifactManifest["dataClass"][] = ["public", "personal", "sensitive", "secret-prohibited"];
  return values.reduce((current, value) => ordered.indexOf(value) > ordered.indexOf(current) ? value : current, "public");
}

function isWithin(parent: string, child: string): boolean {
  const normalizedParent = parent.replace(/\\/gu, "/").replace(/\/+$/u, "");
  const normalizedChild = child.replace(/\\/gu, "/");
  return normalizedChild.startsWith(`${normalizedParent}/`);
}

function rejectUnknownKeys(value: object, allowed: string[], name: string): void {
  const unknown = Object.keys(value).filter((key) => !allowed.includes(key));
  if (unknown.length > 0) throw new Error(`CONTRACT_INVALID ${name} additional properties: ${unknown.join(",")}`);
}

function assertNoSecrets(value: unknown, location: string): void {
  if (Array.isArray(value)) {
    value.forEach((entry, index) => assertNoSecrets(entry, `${location}[${index}]`));
    return;
  }
  if (!value || typeof value !== "object") {
    if (typeof value === "string" && /\bsk-[A-Za-z0-9_-]{16,}\b/u.test(value)) throw new Error(`CONTRACT_INVALID secret value in ${location}`);
    return;
  }
  for (const [key, entry] of Object.entries(value as Record<string, unknown>)) {
    if (/(?:api.?key|access.?token|authorization|password|client.?secret)/iu.test(key)) throw new Error(`CONTRACT_INVALID secret field in ${location}.${key}`);
    assertNoSecrets(entry, `${location}.${key}`);
  }
}

function toJobRelative(jobRoot: string, target: string): string {
  const relative = path.relative(path.resolve(jobRoot), path.resolve(target)).split(path.sep).join("/");
  requireJobRelative(relative, "job-relative output path");
  return relative;
}

async function assertDoesNotExist(filePath: string, code: string): Promise<void> {
  try {
    await access(filePath);
    throw new Error(`${code} ${filePath}`);
  } catch (error) {
    if (error instanceof Error && error.message.startsWith(code)) throw error;
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
  }
}

async function writeJsonOnce(filePath: string, value: unknown): Promise<void> {
  await writeTextOnce(filePath, `${stableStringify(value, 2)}\n`);
}

async function writeTextOnce(filePath: string, content: string): Promise<void> {
  await mkdir(path.dirname(filePath), { recursive: true });
  const temporary = path.join(path.dirname(filePath), `.${path.basename(filePath)}.${process.pid}.${Date.now()}.tmp`);
  await writeFile(temporary, content, { encoding: "utf8", flag: "wx" });
  try {
    await link(temporary, filePath);
  } finally {
    await unlink(temporary).catch(() => undefined);
  }
}

function emitProgress(type: string, request: CommonWorkerRequest, extra: JsonObject = {}): void {
  process.stdout.write(`${JSON.stringify({ schemaVersion: "1.0.0", type, requestId: request.requestId, attemptId: request.attemptId, timestamp: new Date().toISOString(), ...extra })}\n`);
}

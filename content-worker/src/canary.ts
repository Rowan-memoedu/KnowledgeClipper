#!/usr/bin/env node
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { access, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { WORKER_DESCRIBE } from "./common-adapter.js";
import { DESTINATION_NEUTRAL_METADATA, PRODUCT_DISPLAY_NAME, PRODUCT_ID } from "./identity.js";
import type { CommonArtifactManifest, CommonWorkerRequest, CommonWorkerResult } from "./types.js";
import { fileDigest, sha256, stableStringify } from "./util.js";

const CHECKPOINT_ID = "contracts-v1-20260825T115938Z";
const CHECKPOINT_SHA256 = "1cb738fe133f6645b9757e246e2fbc210785086342f70bad32b5015bd19e1b1e";

interface CanaryArgs {
  fixture: string;
  mode: "replay" | "live";
  executable?: string;
}

async function main(): Promise<void> {
  const args = parseArguments(process.argv.slice(2));
  const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
  const projectRoot = path.dirname(workerRoot);
  const checkpointPath = path.join(projectRoot, "receipts", "contracts-v1-stable-checkpoint-20260825T115938Z.json");
  const checkpointDigest = await fileDigest(checkpointPath);
  if (checkpointDigest.sha256 !== CHECKPOINT_SHA256) throw new Error(`CHECKPOINT_MISMATCH ${checkpointDigest.sha256}`);
  const sourceRoot = path.join(workerRoot, "fixtures", args.fixture);
  const inputFileName = await firstExistingInput(sourceRoot);
  if (args.mode === "replay") await access(path.join(sourceRoot, "provider-replay.v1.json"));

  const stamp = new Date().toISOString().replace(/[-:.]/gu, "").replace("Z", "Z");
  const canaryId = `${stamp}-${args.fixture}-${args.mode}-${randomUUID().slice(0, 8)}`;
  const canaryStore = process.env.KNOWLEDGE_CLIPPER_CONTENT_CANARY_ROOT ? path.resolve(process.env.KNOWLEDGE_CLIPPER_CONTENT_CANARY_ROOT) : path.join(workerRoot, "canary", "jobs");
  const jobRoot = path.join(canaryStore, canaryId);
  await mkdir(path.dirname(jobRoot), { recursive: true });
  await mkdir(jobRoot, { recursive: false });
  const inputRoot = path.join(jobRoot, "inputs", "content");
  await mkdir(inputRoot, { recursive: true });

  const input = JSON.parse(await readFile(path.join(sourceRoot, "inputs", inputFileName), "utf8")) as {
    schema_version: string;
    items: Array<{ source: { kind: string; material_path?: string; assets?: Array<{ material_path: string }> }; language_selection?: { context_segments: Array<{ material_path: string }> } }>;
    questionWorkflow?: { phase: "answer" | "make-cards"; cardStructureRef?: string };
  };
  const materialPaths: string[] = [];
  for (const item of input.items) {
    if (item.source.kind === "media_set") {
      for (const asset of item.source.assets ?? []) {
        materialPaths.push(asset.material_path);
        asset.material_path = `inputs/content/${asset.material_path}`;
      }
    } else if (item.source.material_path) {
      materialPaths.push(item.source.material_path);
      item.source.material_path = `inputs/content/${item.source.material_path}`;
    }
    for (const context of item.language_selection?.context_segments ?? []) {
      materialPaths.push(context.material_path);
      context.material_path = `inputs/content/${context.material_path}`;
    }
  }
  if (input.questionWorkflow?.cardStructureRef) {
    const cardStructurePath = input.questionWorkflow.cardStructureRef;
    materialPaths.push(cardStructurePath);
    input.questionWorkflow.cardStructureRef = `inputs/content/${cardStructurePath}`;
  }
  const contentInputRelative = `inputs/content/${inputFileName}`;
  const replayRelative = "inputs/content/provider-replay.v1.json";
  await writeFile(path.join(jobRoot, ...contentInputRelative.split("/")), `${stableStringify(input, 2)}\n`, { flag: "wx" });
  if (args.mode === "replay") await writeFile(path.join(jobRoot, ...replayRelative.split("/")), await readFile(path.join(sourceRoot, "provider-replay.v1.json")), { flag: "wx" });
  for (const materialPath of materialPaths) {
    const target = path.join(inputRoot, ...materialPath.split("/"));
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, await readFile(path.join(sourceRoot, ...materialPath.split("/"))), { flag: "wx" });
  }

  const declaredPaths = [contentInputRelative, ...(args.mode === "replay" ? [replayRelative] : []), ...materialPaths.map((value) => `inputs/content/${value}`)];
  const files = await Promise.all(declaredPaths.map(async (relativePath) => {
    const digest = await fileDigest(path.join(jobRoot, ...relativePath.split("/")));
    return { path: relativePath, sha256: digest.sha256, bytes: digest.size, mediaType: relativePath.endsWith(".md") ? "text/markdown; charset=utf-8" : "application/json" };
  }));
  const shortId = sha256(canaryId).slice(0, 20);
  const artifact: CommonArtifactManifest = {
    schemaVersion: "1.0.0",
    artifactId: `content-input-${shortId}`,
    kind: "content-input",
    contractVersion: "1.0.0",
    mediaType: "application/json",
    files,
    createdAt: new Date().toISOString(),
    producer: { stageId: "capture.normalize", workerId: "canary.fixture", workerVersion: "1.0.0", attemptId: `source-${shortId}` },
    inputRefs: [],
    dataClass: "personal",
    metadata: { ...DESTINATION_NEUTRAL_METADATA, entrypoint: contentInputRelative, moduleSchema: input.schema_version, fixture: args.fixture },
  };
  const artifactRelative = "inputs/content/artifact.json";
  const artifactPath = path.join(jobRoot, ...artifactRelative.split("/"));
  await writeFile(artifactPath, `${stableStringify(artifact, 2)}\n`, { flag: "wx" });
  const artifactDigest = await fileDigest(artifactPath);

  const runId = `run-${shortId}`;
  const attemptId = `attempt-${shortId}`;
  const operationId = `operation-${shortId}`;
  const attemptPath = `runs/${runId}/stages/content.understand-and-card/attempts/${attemptId}`;
  const attemptDirectory = path.join(jobRoot, ...attemptPath.split("/"));
  await mkdir(attemptDirectory, { recursive: true });
  const payload: Record<string, unknown> = { schemaVersion: "content-run/1.0.0", contentInputPath: contentInputRelative, providerMode: args.mode };
  if (args.mode === "replay") payload.replayPath = replayRelative;
  const request: CommonWorkerRequest = {
    schemaVersion: "1.0.0",
    requestId: `request-${shortId}`,
    operationId,
    taskId: `task-${shortId}`,
    runId,
    stageId: "content.understand-and-card",
    attemptId,
    workerId: "content.deepseek",
    operation: input.questionWorkflow?.phase === "answer"
      ? "question.answer"
      : input.questionWorkflow?.phase === "make-cards"
        ? "question.make-cards"
        : "understand-and-card",
    idempotencyKey: sha256(`idempotency:${canaryId}`),
    attemptPath,
    outputPath: `${attemptPath}/outputs`,
    progressPath: `${attemptPath}/progress.ndjson`,
    resultPath: `${attemptPath}/result.json`,
    cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
    inputRefs: [{ artifactRef: artifact.artifactId, kind: artifact.kind, contractVersion: artifact.contractVersion, manifestPath: artifactRelative, expectedSha256: artifactDigest.sha256 }],
    configSnapshot: { productId: PRODUCT_ID, productDisplayName: PRODUCT_DISPLAY_NAME, checkpointId: CHECKPOINT_ID, promptSet: "open-md-v3", categoryRegistry: "category-registry/2.0.0", destinationNeutral: true },
    payload,
  };
  const requestPath = path.join(attemptDirectory, "request.json");
  await writeFile(requestPath, `${stableStringify(request, 2)}\n`, { flag: "wx" });
  await writeFile(path.join(jobRoot, "describe.json"), `${stableStringify(WORKER_DESCRIBE, 2)}\n`, { flag: "wx" });

  const cliPath = path.join(workerRoot, "dist", "src", "cli.js");
  const processResult = args.executable
    ? await invokeWorkerExecutable(args.executable, requestPath, workerRoot)
    : await invokeWorker(cliPath, requestPath, workerRoot);
  const resultPath = path.join(jobRoot, ...request.resultPath.split("/"));
  const result = JSON.parse(await readFile(resultPath, "utf8")) as CommonWorkerResult;
  const resultDigest = await fileDigest(resultPath);
  const requestDigest = await fileDigest(requestPath);
  const outputEvidence = await Promise.all(result.outputs.map(async (ref) => {
    const manifestDigest = await fileDigest(path.join(jobRoot, ...ref.manifestPath.split("/")));
    return { artifactRef: ref.artifactRef, kind: ref.kind, manifestPath: ref.manifestPath, manifestSha256: manifestDigest.sha256 };
  }));
  const outputRoot = path.join(jobRoot, ...request.outputPath.split("/"));
  const learningPath = await firstExistingFile(outputRoot, ["learning-bundle.schema-v2.json", "learning-bundle.schema-v1.json"]);
  let providerEvidence: unknown[] = [];
  try {
    const learning = JSON.parse(await readFile(learningPath, "utf8")) as { ai_trace?: Array<{ mode: string; provider: string; model: string; prompt_sha256: string; response_sha256: string; attempts: number }> };
    providerEvidence = (learning.ai_trace ?? []).map((trace) => ({ mode: trace.mode, provider: trace.provider, model: trace.model, attempts: trace.attempts, promptSha256: trace.prompt_sha256, responseSha256: trace.response_sha256 }));
  } catch {
    providerEvidence = [];
  }
  const receipt = {
    schemaVersion: "content-canary-receipt/1.0.0",
    productId: PRODUCT_ID,
    productDisplayName: PRODUCT_DISPLAY_NAME,
    destinationNeutral: true,
    canaryId,
    fixture: args.fixture,
    mode: args.mode,
    entrypoint: args.executable ?? cliPath,
    checkpoint: { checkpointId: CHECKPOINT_ID, receiptPath: checkpointPath, sha256: checkpointDigest.sha256 },
    jobRoot,
    requestPath,
    requestSha256: requestDigest.sha256,
    resultPath,
    resultSha256: resultDigest.sha256,
    workerExitCode: processResult.exitCode,
    status: result.status,
    moduleReleaseState: result.payload?.moduleReleaseState,
    outputs: outputEvidence,
    providerEvidence,
    ...(args.mode === "live" ? { externalConnectionEvidence: liveConnectionEvidence() } : {}),
    completedAt: new Date().toISOString(),
  };
  const receiptPath = path.join(jobRoot, "canary-receipt.json");
  await writeFile(receiptPath, `${stableStringify(receipt, 2)}\n`, { flag: "wx" });
  const receiptDigest = await fileDigest(receiptPath);
  process.stdout.write(`${JSON.stringify({ type: "canary.receipt", receiptPath, receiptSha256: receiptDigest.sha256, status: result.status })}\n`);
  if (processResult.exitCode !== 0 || result.status !== "completed") process.exitCode = 1;
}

async function firstExistingInput(sourceRoot: string): Promise<string> {
  for (const fileName of ["standard-input.v7.json", "standard-input.v6.json", "standard-input.v5.json", "standard-input.v4.json", "standard-input.v3.json", "standard-input.v2.json", "standard-input.v1.json"]) {
    try {
      await access(path.join(sourceRoot, "inputs", fileName));
      return fileName;
    } catch {
      // Try the preceding module-local schema version.
    }
  }
  throw new Error(`FIXTURE_INVALID no standard input in ${sourceRoot}`);
}

async function firstExistingFile(root: string, names: string[]): Promise<string> {
  for (const name of names) {
    const target = path.join(root, name);
    try {
      await access(target);
      return target;
    } catch {
      // Continue to the prior module-local output schema.
    }
  }
  throw new Error(`OUTPUT_MISSING learning bundle in ${root}`);
}

async function invokeWorker(cliPath: string, requestPath: string, workerRoot: string): Promise<{ exitCode: number }> {
  const child = spawn(process.execPath, [cliPath, "run", "--request", requestPath], { cwd: workerRoot, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  child.stdout.on("data", (chunk: Buffer) => process.stdout.write(chunk));
  child.stderr.on("data", (chunk: Buffer) => process.stderr.write(chunk));
  const exitCode = await new Promise<number>((resolve, reject) => {
    child.on("error", reject);
    child.on("close", (code) => resolve(code ?? 1));
  });
  return { exitCode };
}

async function invokeWorkerExecutable(executablePath: string, requestPath: string, workerRoot: string): Promise<{ exitCode: number }> {
  const child = spawn(executablePath, ["run", "--request", requestPath], { cwd: workerRoot, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  child.stdout.on("data", (chunk: Buffer) => process.stdout.write(chunk));
  child.stderr.on("data", (chunk: Buffer) => process.stderr.write(chunk));
  const exitCode = await new Promise<number>((resolve, reject) => {
    child.on("error", reject);
    child.on("close", (code) => resolve(code ?? 1));
  });
  return { exitCode };
}

function liveConnectionEvidence(): { transport: "https"; endpointOrigin: string; endpointPath: "/chat/completions"; credentialRecorded: false } {
  const configuredBase = process.env.AI_PRODUCER_BASE_URL?.trim() || "https://api.deepseek.com";
  const endpointOrigin = new URL(configuredBase).origin;
  return { transport: "https", endpointOrigin, endpointPath: "/chat/completions", credentialRecorded: false };
}

function parseArguments(values: string[]): CanaryArgs {
  const map = new Map<string, string>();
  for (let index = 0; index < values.length; index += 2) {
    const key = values[index];
    const value = values[index + 1];
    if (!key?.startsWith("--") || value === undefined) throw new Error(`CLI_INVALID near ${key ?? "end"}`);
    map.set(key, value);
  }
  const fixture = map.get("--fixture");
  const mode = map.get("--mode");
  const executable = map.get("--executable");
  if (!fixture || !/^[a-z0-9][a-z0-9-]*$/u.test(fixture)) throw new Error("CLI_INVALID --fixture");
  if (mode !== "replay" && mode !== "live") throw new Error("CLI_INVALID --mode");
  if (executable && !path.isAbsolute(executable)) throw new Error("CLI_INVALID --executable must be absolute");
  return { fixture, mode, ...(executable ? { executable } : {}) };
}

main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 2;
});

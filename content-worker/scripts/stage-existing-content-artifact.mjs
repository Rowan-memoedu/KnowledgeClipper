#!/usr/bin/env node
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const args = new Map();
for (let index = 2; index < process.argv.length; index += 2) args.set(process.argv[index], process.argv[index + 1]);
const jobRoot = path.resolve(args.get("--job-root") ?? "");
const artifactPath = path.resolve(args.get("--artifact") ?? "");
const runId = args.get("--run-id");
if (!path.isAbsolute(jobRoot) || !path.isAbsolute(artifactPath) || !/^run-[a-z0-9][a-z0-9-]*$/u.test(runId ?? "")) {
  throw new Error("usage: --job-root <absolute> --artifact <absolute> --run-id run-<id>");
}
const artifactRelative = path.relative(jobRoot, artifactPath).replaceAll("\\", "/");
if (artifactRelative.startsWith("../") || path.isAbsolute(artifactRelative)) throw new Error("artifact must be inside job root");
const artifactBytes = await readFile(artifactPath);
const artifact = JSON.parse(artifactBytes.toString("utf8"));
if (artifact.schemaVersion !== "1.0.0" || artifact.kind !== "content-input" || artifact.contractVersion !== "1.0.0") throw new Error("not a common content-input artifact");
const contentInputPath = artifact.metadata?.entrypoint;
if (typeof contentInputPath !== "string" || !contentInputPath) throw new Error("artifact metadata.entrypoint missing");
await readFile(path.join(jobRoot, ...contentInputPath.split("/")));

const shortId = createHash("sha256").update(`${runId}:${sha256(artifactBytes)}`).digest("hex").slice(0, 20);
const attemptId = `attempt-${shortId}`;
const attemptPath = `runs/${runId}/stages/content.understand-and-card/attempts/${attemptId}`;
const attemptRoot = path.join(jobRoot, ...attemptPath.split("/"));
await mkdir(attemptRoot, { recursive: true });
const request = {
  schemaVersion: "1.0.0",
  requestId: `request-${shortId}`,
  operationId: `operation-${shortId}`,
  taskId: artifact.metadata?.sourceTaskId ?? path.basename(jobRoot),
  runId,
  stageId: "content.understand-and-card",
  attemptId,
  workerId: "content.deepseek",
  operation: "understand-and-card",
  idempotencyKey: createHash("sha256").update(`content:${runId}:${artifact.artifactId}`).digest("hex"),
  attemptPath,
  outputPath: `${attemptPath}/outputs`,
  progressPath: `${attemptPath}/progress.ndjson`,
  resultPath: `${attemptPath}/result.json`,
  cancelSignal: { schemaVersion: "1.0.0", path: `${attemptPath}/cancel.json`, gracePeriodMs: 2000 },
  inputRefs: [{ artifactRef: artifact.artifactId, kind: artifact.kind, contractVersion: artifact.contractVersion, manifestPath: artifactRelative, expectedSha256: sha256(artifactBytes) }],
  configSnapshot: { productId: "KnowledgeClipper", productDisplayName: "零碎知识剪藏", checkpointId: "contracts-v1-20260825T115938Z", promptSet: "open-md-v2", categoryRegistry: "category-registry/2.0.0", destinationNeutral: true },
  payload: { schemaVersion: "content-run/1.0.0", contentInputPath, providerMode: "live" },
};
const requestPath = path.join(attemptRoot, "request.json");
await writeFile(requestPath, `${JSON.stringify(request, null, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ requestPath, requestSha256: sha256(await readFile(requestPath)), resultPath: path.join(jobRoot, ...request.resultPath.split("/")), attemptPath })}\n`);

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Orchestrator.Core;

if (args.Length is not (6 or 8))
    throw new ArgumentException("Usage: FullPipeline.Canary <projectRoot> <releaseRoot> <registryPath> <canaryRoot> <mode:clip|question|remnote|anki|remnote-replay> <runStamp> [remNoteRootRemId remNoteRootTitle]");

var projectRoot = Path.GetFullPath(args[0]);
var releaseRoot = Path.GetFullPath(args[1]);
var registryPath = Path.GetFullPath(args[2]);
var canaryRoot = Path.GetFullPath(args[3]);
var mode = args[4];
var stamp = args[5];
if (mode is not ("clip" or "question" or "remnote" or "anki" or "remnote-replay")) throw new ArgumentException("Unknown mode.");
var remNoteRootRemId = args.Length == 8 ? args[6] : null;
var remNoteRootTitle = args.Length == 8 ? args[7] : null;
if (mode == "remnote" && (string.IsNullOrWhiteSpace(remNoteRootRemId) || string.IsNullOrWhiteSpace(remNoteRootTitle)))
    throw new ArgumentException("RemNote mode requires an explicit isolated root Rem ID and title.");
Directory.CreateDirectory(canaryRoot);

var registry = new RegistryLoader().Load(registryPath);
await new RegistryLoader().ValidateEntrypointsAsync(registry, releaseRoot);
if (mode == "remnote-replay")
{
    var sourceJobRoot = Path.Combine(canaryRoot, "jobs", $"backend-remnote-{stamp.ToLowerInvariant()}");
    var sourceRequestPath = Directory.GetFiles(sourceJobRoot, "request.json", SearchOption.AllDirectories)
        .Single(path => path.Contains($"{Path.DirectorySeparatorChar}export-remnote{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    var sourceRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(sourceRequestPath), ContractJson.Options)
        ?? throw new InvalidDataException("Original RemNote request is empty.");
    var replayRunId = "run-idempotency-001";
    var replayAttemptId = $"attempt-replay-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    var attemptPath = $"runs/{replayRunId}/stages/export-remnote/attempts/{replayAttemptId}";
    var replayRequest = sourceRequest with
    {
        RequestId = "request-" + Guid.NewGuid().ToString("N"),
        OperationId = "operation-" + Guid.NewGuid().ToString("N"),
        RunId = replayRunId,
        AttemptId = replayAttemptId,
        AttemptPath = attemptPath,
        OutputPath = attemptPath + "/outputs",
        ProgressPath = attemptPath + "/progress.ndjson",
        ResultPath = attemptPath + "/result.json",
        CancelSignal = new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000)
    };
    var worker = registry.Workers.Single(item => item.WorkerId == "export.remnote");
    var execution = await new WorkerProcessRunner().RunAsync(worker, releaseRoot, sourceJobRoot, replayRequest, TimeSpan.FromMinutes(5), new CancellationReasonState(), CancellationToken.None);
    var payload = execution.Result.Payload ?? throw new InvalidDataException("Replay RemNote payload is missing.");
    var replayReport = new
    {
        schemaVersion = "knowledge-clipper-remnote-idempotency-canary.v1",
        generatedAt = DateTimeOffset.UtcNow,
        status = execution.Result.Status,
        sourceRequest = Path.GetRelativePath(sourceJobRoot, sourceRequestPath).Replace('\\', '/'),
        replayRequest = replayRequest.AttemptPath + "/request.json",
        sourceRequest.IdempotencyKey,
        replayAttemptId,
        workerExitCode = execution.ExitCode,
        outcome = payload.GetProperty("outcome").GetString(),
        documentRemId = payload.GetProperty("documentRemId").GetString(),
        readbackVerified = payload.GetProperty("readbackVerified").GetBoolean()
    };
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(canaryRoot, "result.remnote-idempotency.json"), replayReport);
    Console.WriteLine(JsonSerializer.Serialize(replayReport, ContractJson.Options));
    return;
}
var taskId = $"backend-{mode}-{stamp.ToLowerInvariant()}";
var jobRoot = Path.Combine(canaryRoot, "jobs", taskId);
var runId = "run-001";
var inputText = mode == "question"
    ? "为什么不可变任务包能让重试过程保持可审计？"
    : "问题：在不可变任务包中，重试为什么不会覆盖前一次证据？\n答案：因为每次重试都会创建新的 attempt 并只追加产物，旧 manifest、请求和结果保持不变。";
var captureRef = await CreateJobAsync(projectRoot, jobRoot, taskId, mode, inputText);
var configPath = Path.Combine(canaryRoot, $"profile-resolver.{mode}.json");
await CreateConfigurationAsync(projectRoot, configPath, mode == "remnote", stamp, remNoteRootRemId, remNoteRootTitle);
var configuration = DesktopIntentResolver.LoadConfiguration(configPath);
var plan = await new DesktopIntentResolver().ResolveAsync(
    jobRoot, Path.Combine(jobRoot, "plans", "clip-intent.v2.json"), registry, runId, configuration, CancellationToken.None);
var state = await new OrchestratorEngine().RunAsync(jobRoot, plan, registry, releaseRoot, runId, CancellationToken.None);

var results = new List<(string Path, WorkerResult Result)>();
foreach (var path in Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    var result = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(path), ContractJson.Options)
        ?? throw new InvalidDataException($"Empty result: {path}");
    results.Add((Path.GetRelativePath(jobRoot, path).Replace('\\', '/'), result));
}
var outputs = results.SelectMany(item => item.Result.Outputs).ToArray();
var readablePath = Path.Combine(canaryRoot, $"readable-output.{mode}.md");
await WriteReadableAsync(jobRoot, readablePath, mode, inputText, state, outputs);

string? remNoteDocumentId = null;
string? remNoteOutcome = null;
bool? remNoteReadbackVerified = null;
if (mode == "remnote")
{
    var exportResult = results.Single(item => item.Result.Worker.Id == "export.remnote").Result;
    if (exportResult.Payload is null) throw new InvalidDataException("RemNote result payload is missing.");
    var payload = exportResult.Payload.Value;
    remNoteDocumentId = payload.GetProperty("documentRemId").GetString();
    remNoteOutcome = payload.GetProperty("outcome").GetString();
    remNoteReadbackVerified = payload.GetProperty("readbackVerified").GetBoolean();
}

var report = new
{
    schemaVersion = "knowledge-clipper-backend-canary.v1",
    generatedAt = DateTimeOffset.UtcNow,
    mode,
    status = state,
    taskId,
    runId,
    releaseRoot,
    registry = new { path = registryPath, sha256 = Sha(await File.ReadAllBytesAsync(registryPath)) },
    input = new { text = inputText, captureArtifact = captureRef },
    plan = new { plan.SchemaVersion, plan.PlanVersion, plan.MaxConcurrency, stages = plan.Stages.Select(s => new { s.StageId, s.WorkerId, s.Operation, s.DependsOn, s.OutputKinds }) },
    attempts = results.Select(item => new
    {
        resultPath = item.Path,
        item.Result.StageId,
        workerId = item.Result.Worker.Id,
        workerVersion = item.Result.Worker.Version,
        item.Result.AttemptId,
        item.Result.IdempotencyKey,
        item.Result.Status,
        outputKinds = item.Result.Outputs.Select(output => output.Kind),
        item.Result.StartedAt,
        item.Result.FinishedAt,
        item.Result.Warnings
    }),
    invocationCounts = results.GroupBy(item => item.Result.Worker.Id).ToDictionary(group => group.Key, group => group.Count()),
    outputs,
    readableOutput = Path.GetRelativePath(canaryRoot, readablePath).Replace('\\', '/'),
    remnote = mode == "remnote" ? new { documentRemId = remNoteDocumentId, outcome = remNoteOutcome, readbackVerified = remNoteReadbackVerified, isolatedParentRemId = remNoteRootRemId } : null
};
await AtomicFile.WriteJsonOnceAsync(Path.Combine(canaryRoot, $"result.{mode}.json"), report);
Console.WriteLine(JsonSerializer.Serialize(report, ContractJson.Options));

static async Task<ArtifactReference> CreateJobAsync(string projectRoot, string jobRoot, string taskId, string mode, string text)
{
    Directory.CreateDirectory(jobRoot);
    var textBytes = Encoding.UTF8.GetBytes(text);
    var textRef = await PublishInitialAsync(jobRoot, "capture-text-001", "text.selected", new[] { ("inputs/selection.txt", textBytes, "text/plain") }, "inputs/selection.txt");
    var questionTurns = mode == "question"
        ? new object[] { new { turnIndex = 1, role = "user", artifactRef = textRef.ArtifactRef } }
        : Array.Empty<object>();
    var capture = new
    {
        schemaVersion = "capture-bundle.v1",
        captureId = "capture-" + taskId,
        createdAt = DateTimeOffset.UtcNow,
        intent = mode == "question" ? "question" : "card",
        source = "selection",
        modeSnapshot = new { workflow = mode == "question" ? "question" : "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "text" },
        artifacts = new[] { new { artifactRef = textRef.ArtifactRef, relativePath = "inputs/selection.txt", sha256 = Sha(textBytes), bytes = textBytes.LongLength, mediaType = "text/plain", encoding = "utf-8" } },
        selectedTextArtifactRef = textRef.ArtifactRef,
        contextSegments = (object?)null,
        questionImages = Array.Empty<string>(),
        answerImages = Array.Empty<string>(),
        questionTurns
    };
    var captureBytes = JsonSerializer.SerializeToUtf8Bytes(capture, ContractJson.Options);
    var captureRef = await PublishInitialAsync(jobRoot, "capture-bundle-001", "capture-bundle", new[] { ("inputs/capture-bundle.v1.json", captureBytes, "application/json") }, "inputs/capture-bundle.v1.json", new[] { textRef });
    ArtifactReference? ankiConfigRef = null;
    if (mode == "anki")
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectRoot, "workers", "anki-exporter", "config", "anki-export.v1.example.json")))?.AsObject()
            ?? throw new InvalidDataException("Anki example configuration is empty.");
        config["expected_profile"] = "账户 1";
        var routes = config["deck_routes"]?.AsObject() ?? throw new InvalidDataException("Anki deck routes are missing.");
        foreach (var route in routes.ToArray()) routes[route.Key] = "零碎知识剪藏::Canary";
        var configBytes = JsonSerializer.SerializeToUtf8Bytes(config, ContractJson.Options);
        ankiConfigRef = await PublishInitialAsync(jobRoot, "anki-export-config-001", "anki-export-config",
            new[] { ("inputs/anki-export.v1.json", configBytes, "application/json") }, "inputs/anki-export.v1.json");
    }
    var intent = mode == "question" ? "clip.question" : "clip.card";
    var requested = mode == "question" ? new[] { "question.answer" } : new[] { "card-bundle" };
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "plans", "clip-intent.v2.json"), new
    {
        schemaVersion = "knowledge-clipper-intent.v2", taskId, intent, inputArtifactRef = captureRef.ArtifactRef, requestedOutputs = requested
    });
    var manifestInputs = ankiConfigRef is null ? new[] { captureRef } : new[] { captureRef, ankiConfigRef };
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v2.json"), new
    {
        schemaVersion = "2.0.0", taskId, createdAt = DateTimeOffset.UtcNow,
        createdBy = new { application = "KnowledgeClipper.FullPipeline.Canary", version = "1.0.0" }, intent,
        executionProfile = "cards", destinations = mode == "remnote" ? new[] { "remnote" } : mode == "anki" ? new[] { "anki" } : Array.Empty<string>(),
        inputRefs = manifestInputs.Select(item => new { item.ArtifactRef, item.Kind, item.ContractVersion, expectedSha256 = item.ExpectedSha256 }).ToArray(),
        requestedOutputs = requested, initialPlanRef = "plans/clip-intent.v2.json", retentionClass = "ephemeral"
    });
    if (mode == "anki")
    {
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow,
            createdBy = new { application = "KnowledgeClipper.FullPipeline.Canary.LegacyAlias", version = "1.0.0" }, intent,
            executionProfile = "cards-anki",
            inputRefs = manifestInputs.Select(item => new { item.ArtifactRef, item.Kind, item.ContractVersion, expectedSha256 = item.ExpectedSha256 }).ToArray(),
            requestedOutputs = requested, initialPlanRef = "plans/clip-intent.v2.json", retentionClass = "ephemeral",
            metadata = new { canonicalManifestRef = "task.manifest.v2.json", legacyCompatibilityAlias = true }
        });
    }
    return captureRef;
}

static async Task<ArtifactReference> PublishInitialAsync(string jobRoot, string artifactId, string kind, IEnumerable<(string Path, byte[] Bytes, string MediaType)> payloads, string entrypoint, IReadOnlyList<ArtifactReference>? inputRefs = null)
{
    var files = new List<object>();
    foreach (var payload in payloads)
    {
        var full = PathPolicy.ResolveJobRelative(jobRoot, payload.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, payload.Bytes);
        files.Add(new { path = payload.Path, sha256 = Sha(payload.Bytes), bytes = payload.Bytes.LongLength, mediaType = payload.MediaType });
    }
    var manifestRelative = $"artifacts/{artifactId}/artifact.json";
    var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, manifestRelative);
    await AtomicFile.WriteJsonOnceAsync(manifestPath, new
    {
        schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "application/json", files, createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId = "desktop-capture", workerId = "desktop.capture", workerVersion = "2.0.0", attemptId = "capture-attempt-001" },
        inputRefs = inputRefs ?? Array.Empty<ArtifactReference>(), dataClass = "sensitive", metadata = new { entrypoint, fixtureClass = "synthetic-non-private" }
    });
    return new ArtifactReference(artifactId, kind, "1.0.0", manifestRelative, Sha(await File.ReadAllBytesAsync(manifestPath)));
}

static async Task CreateConfigurationAsync(string projectRoot, string targetPath, bool enableRemNote, string stamp, string? remNoteRootRemId, string? remNoteRootTitle)
{
    var sourcePath = Path.Combine(projectRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json");
    var root = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath))?.AsObject() ?? throw new InvalidDataException("Resolver configuration is empty.");
    if (enableRemNote)
    {
        var remnote = root["destinationConfigurations"]?["remnote"]?.AsObject() ?? throw new InvalidDataException("RemNote configuration is missing.");
        remnote["export"] = JsonSerializer.SerializeToNode(new
        {
            sink = "mcp-direct",
            rootRemId = remNoteRootRemId ?? throw new InvalidOperationException("Explicit isolated RemNote root ID is missing."),
            rootTitle = remNoteRootTitle ?? throw new InvalidOperationException("Explicit isolated RemNote root title is missing."),
            documentTitlePrefix = "KnowledgeClipper Backend Canary " + stamp
        });
    }
    await AtomicFile.WriteJsonOnceAsync(targetPath, root);
}

static async Task WriteReadableAsync(string jobRoot, string path, string mode, string input, string state, IReadOnlyList<ArtifactReference> outputs)
{
    var text = new StringBuilder();
    text.AppendLine("# KnowledgeClipper backend Canary").AppendLine();
    text.AppendLine($"- mode: {mode}").AppendLine($"- state: {state}").AppendLine();
    text.AppendLine("## Original input").AppendLine().AppendLine("```text").AppendLine(input).AppendLine("```").AppendLine();
    foreach (var output in outputs)
    {
        text.AppendLine($"## {output.Kind} · {output.ArtifactRef}").AppendLine();
        text.AppendLine($"manifest: `{output.ManifestPath}`").AppendLine($"manifest SHA-256: `{output.ExpectedSha256}`").AppendLine();
        var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, output.ManifestPath);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString()!;
            var mediaType = file.GetProperty("mediaType").GetString()!;
            text.AppendLine($"### {relative}").AppendLine();
            var bytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, relative));
            text.AppendLine($"bytes={bytes.LongLength}, sha256={Sha(bytes)}, mediaType={mediaType}").AppendLine();
            if (bytes.LongLength <= 512_000 && (mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType == "application/json"))
            {
                var content = Encoding.UTF8.GetString(bytes);
                if (mediaType == "application/json")
                {
                    using var json = JsonDocument.Parse(content);
                    content = JsonSerializer.Serialize(json.RootElement, ContractJson.Options);
                }
                text.AppendLine(mediaType == "application/json" ? "```json" : "```text").AppendLine(content).AppendLine("```").AppendLine();
            }
        }
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false));
}

static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

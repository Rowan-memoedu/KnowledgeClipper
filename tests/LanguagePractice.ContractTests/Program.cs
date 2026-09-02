using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Orchestrator.Core;

var projectRoot = Path.GetFullPath(args.Length == 1 ? args[0] : throw new ArgumentException("Expected project root."));
var fakeWorker = Path.Combine(projectRoot, "tests", "FakeWorker", "bin", "Release", "net8.0", "RemNoteQuickClipper.FakeWorker.exe");
var contentWorker = Path.Combine(projectRoot, "workers", "content", "RemNoteQuickClipper.Content.Worker.exe");
Assert(File.Exists(fakeWorker), "Fake content worker is missing.");
Assert(File.Exists(contentWorker), "Real replay-capable content worker is missing.");

var results = new List<object>
{
    await VerifyRealContentReplayAsync(projectRoot, contentWorker),
    await VerifyOrdinaryWaitingInputAsync(),
    await VerifyTamperFailClosedAsync(projectRoot, fakeWorker)
};
var receipt = new { schemaVersion = "knowledge-clipper-language-practice-test-receipt.v2", status = "passed", tests = results };
var receiptPath = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_LANGUAGE_TEST_RECEIPT");
if (!string.IsNullOrWhiteSpace(receiptPath))
    await AtomicFile.WriteJsonOnceAsync(Path.GetFullPath(receiptPath), receipt);
Console.WriteLine(JsonSerializer.Serialize(receipt, ContractJson.Options));

static async Task<object> VerifyRealContentReplayAsync(string projectRoot, string contentWorker)
{
    var root = Path.Combine(Path.GetTempPath(), "KnowledgeClipper.LanguagePractice.Real", Guid.NewGuid().ToString("N"));
    var jobsRoot = Path.Combine(root, "jobs");
    const string taskId = "task-language-real-replay";
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    var pipe = "KnowledgeClipper.Language.Real." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    Task? host = null;
    try
    {
        var input = await StageRealContentFixtureAsync(projectRoot, jobRoot, taskId);
        var originalInputPath = PathPolicy.ResolveJobRelative(jobRoot, "inputs/content/standard-input.v2.json");
        var originalInputHash = Hash(await File.ReadAllBytesAsync(originalInputPath));
        await WritePlanAndManifestAsync(jobRoot, taskId, input, new
        {
            schemaVersion = "content-run/1.0.0",
            contentInputPath = "inputs/content/standard-input.v2.json",
            providerMode = "replay",
            replayPath = "inputs/content/provider-replay.v1.json"
        });
        var registryPath = Path.Combine(root, "worker-registry.v1.json");
        await AtomicFile.WriteJsonOnceAsync(registryPath, Registry(projectRoot, contentWorker, false));
        host = new OrchestratorHost(jobsRoot, registryPath, projectRoot, 4).ServeAsync(Ipc(pipe), shutdown.Token);

        await SendCommandAsync(pipe, taskId, "task.submit", null, taskId + "/task.manifest.v1.json", "submit-real", null);
        var waiting = await WaitStatusAsync(pipe, taskId, "waiting_input");
        Assert(waiting.GetProperty("decisionKind").GetString() == "language-practice", "Real content decision did not project decisionKind.");
        var token = waiting.GetProperty("decisionTargetToken").GetString()!;
        Assert(token.Length == 64, "Opaque language-practice target token is invalid.");

        var firstRequestPath = Directory.GetFiles(Path.Combine(jobRoot, "runs"), "request.json", SearchOption.AllDirectories).Single();
        var firstResultPath = Path.Combine(Path.GetDirectoryName(firstRequestPath)!, "result.json");
        using (var firstResult = JsonDocument.Parse(await File.ReadAllBytesAsync(firstResultPath)))
        {
            var kinds = firstResult.RootElement.GetProperty("outputs").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ToArray();
            Assert(kinds.Contains("learning-bundle") && kinds.Contains("language-practice-decision") && !kinds.Contains("card-bundle"), "First real Content attempt did not publish the authoritative audit plus decision shape.");
        }

        var choiceRef = "commands/language-choice-both.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, choiceRef), new
        {
            schemaVersion = "knowledge-clipper-language-practice-choice.v2",
            decisionKind = "language-practice",
            targetToken = token,
            choice = "both"
        });
        var simultaneous = new[]
        {
            SendCommandAsync(pipe, taskId, "task.provide_input", taskId, choiceRef, "choice-a", "waiting_input"),
            SendCommandAsync(pipe, taskId, "task.provide_input", taskId, choiceRef, "choice-b", "waiting_input")
        };
        var receipts = await Task.WhenAll(simultaneous);
        Assert(receipts.Count(item => item.GetProperty("accepted").GetBoolean()) == 2, "Concurrent choices were not durably accepted from the same waiting state.");
        await WaitForAsync(() => Directory.GetFiles(Path.Combine(jobRoot, "runs"), "command.execution_completed.json", SearchOption.AllDirectories).Length > 0 ||
            ReadEvents(jobRoot).Count(item => item.Type == "command.execution_completed") >= 2, "choice execution completion");
        await WaitForAsync(() => ReadEvents(jobRoot).Count(item => item.Type is "command.execution_completed" or "command.execution_failed") >= 3, "concurrent choice terminal events");

        var requests = Directory.GetFiles(Path.Combine(jobRoot, "runs"), "request.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, Request: JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(path), ContractJson.Options)!))
            .Where(item => item.Request.WorkerId == "content.deepseek")
            .ToArray();
        Assert(requests.Length == 2, $"Concurrent decision resolution started {requests.Length - 1} replay attempts instead of exactly one.");
        var second = requests.Single(item => item.Path != firstRequestPath);
        Assert(second.Request.InputRefs.Count(item => item.Kind == "content-input") == 1, "Replay request has no unique derived content input.");
        Assert(!second.Request.Payload.TryGetProperty("languagePracticeChoice", out _) &&
               !second.Request.Payload.TryGetProperty("targetToken", out _) &&
               !second.Request.Payload.TryGetProperty("binding", out _), "Choice or internal binding leaked into the worker payload.");
        var derivedPath = second.Request.Payload.GetProperty("contentInputPath").GetString()!;
        Assert(derivedPath != "inputs/content/standard-input.v2.json", "Replay overwrote or reused the original content input path.");
        using (var derived = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, derivedPath))))
        {
            Assert(derived.RootElement.GetProperty("schema_version").GetString() == "standard-input/2.0.0", "Derived input is not standard-input v2.");
            Assert(derived.RootElement.GetProperty("human_overrides").GetProperty("languagePracticeMode").GetString() == "both", "Derived input did not apply the operation-level choice.");
            Assert(derived.RootElement.GetProperty("items").GetRawText() == JsonDocument.Parse(await File.ReadAllBytesAsync(originalInputPath)).RootElement.GetProperty("items").GetRawText(), "Derived input did not preserve source items exactly.");
        }
        Assert(Hash(await File.ReadAllBytesAsync(originalInputPath)) == originalInputHash, "Original content input changed during resume.");
        var secondResultPath = Path.Combine(Path.GetDirectoryName(second.Path)!, "result.json");
        var bothOrderGroups = 0;
        using (var secondResult = JsonDocument.Parse(await File.ReadAllBytesAsync(secondResultPath)))
        {
            var kinds = secondResult.RootElement.GetProperty("outputs").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ToArray();
            Assert(kinds.Contains("card-bundle") && !kinds.Contains("language-practice-decision"), "Second real Content attempt repeated the decision instead of producing CardBundle.");
            var cardRef = secondResult.RootElement.GetProperty("outputs").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "card-bundle").Deserialize<ArtifactReference>(ContractJson.Options)!;
            using var cards = await ReadArtifactPayloadAsync(jobRoot, cardRef);
            var ordered = cards.RootElement.GetProperty("cards").EnumerateArray()
                .GroupBy(card => card.GetProperty("variant_group_id").GetString(), StringComparer.Ordinal)
                .Select(group => group.Select(card => card.GetProperty("template").GetString()).ToArray()).ToArray();
            Assert(ordered.Length == 2 && ordered.All(templates => templates.SequenceEqual(new[] { "language_context", "language_writing" })),
                "Content both output did not preserve language_context then language_writing within each variant group.");
            bothOrderGroups = ordered.Length;
        }
        var resolutionFiles = Directory.GetFiles(Path.Combine(jobRoot, "runs"), "decision-resolution.json", SearchOption.AllDirectories);
        Assert(resolutionFiles.Length == 1, "Concurrent submissions published more than one decision resolution.");
        var terminal = ReadEvents(jobRoot).Where(item => item.Type is "command.execution_completed" or "command.execution_failed").ToArray();
        Assert(terminal.Any(item => item.Type == "command.execution_completed" && item.Payload.TryGetProperty("finalState", out var state) && state.GetString() == "succeeded"), "No choice command completed the resumed Content run.");
        Assert(terminal.Any(item => item.Type == "command.execution_failed" && item.Payload.GetProperty("errorCode").GetString() == "decision.already_resolved"), "Concurrent loser did not fail closed as already resolved.");

        await SendCommandAsync(pipe, taskId, "task.provide_input", taskId, choiceRef, "choice-repeat", null);
        await WaitErrorAsync(jobRoot, "choice-repeat", "decision.already_resolved");
        Assert(Directory.GetFiles(Path.Combine(jobRoot, "runs"), "request.json", SearchOption.AllDirectories).Length == 2, "Repeated token started another Content attempt.");

        var expiredRef = "commands/language-choice-expired.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, expiredRef), new { schemaVersion = "knowledge-clipper-language-practice-choice.v2", decisionKind = "language-practice", targetToken = new string('a', 64), choice = "both" });
        await SendCommandAsync(pipe, taskId, "task.provide_input", taskId, expiredRef, "choice-expired", null);
        await WaitErrorAsync(jobRoot, "choice-expired", "decision.not_found");

        const string otherTask = "task-language-cross-target";
        var otherRoot = Path.Combine(jobsRoot, otherTask);
        Directory.CreateDirectory(otherRoot);
        await WriteMinimalManifestAsync(otherRoot, otherTask);
        var crossRef = "commands/language-choice-cross.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(otherRoot, crossRef), new { schemaVersion = "knowledge-clipper-language-practice-choice.v2", decisionKind = "language-practice", targetToken = token, choice = "both" });
        await SendCommandAsync(pipe, otherTask, "task.provide_input", otherTask, crossRef, "choice-cross", null);
        await WaitErrorAsync(otherRoot, "choice-cross", "decision.not_found");

        var invalidRef = "commands/language-choice-invalid.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, invalidRef), new { schemaVersion = "knowledge-clipper-language-practice-choice.v2", decisionKind = "language-practice", targetToken = token, choice = "unsupported" });
        await SendCommandAsync(pipe, taskId, "task.provide_input", taskId, invalidRef, "choice-invalid", null);
        await WaitErrorAsync(jobRoot, "choice-invalid", "decision.payload_invalid");

        return new
        {
            name = "language-practice-real-content-replay",
            status = "passed",
            firstAttemptOutputs = new[] { "learning-bundle", "language-practice-decision" },
            choice = "both",
            derivedInput = derivedPath,
            originalInputSha256 = originalInputHash,
            secondAttemptOutputs = new[] { "learning-bundle", "card-bundle" },
            bothProjectionOrder = new { groups = bothOrderGroups, templates = new[] { "language_context", "language_writing" } },
            concurrentAccepted = 2,
            replayAttemptsStarted = 1,
            failClosed = new[] { "expired", "repeated", "cross-task", "wrong-choice", "concurrent-loser" }
        };
    }
    finally
    {
        shutdown.Cancel();
        if (host is not null) try { await host; } catch (OperationCanceledException) { }
        if (Directory.Exists(root) && Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_KEEP_LANGUAGE_TEST") != "1") Directory.Delete(root, true);
    }
}

static async Task<object> VerifyOrdinaryWaitingInputAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "KnowledgeClipper.LanguagePractice.Ordinary", Guid.NewGuid().ToString("N"));
    const string taskId = "task-ordinary-waiting";
    var jobRoot = Path.Combine(root, taskId);
    Directory.CreateDirectory(jobRoot);
    try
    {
        await WriteMinimalManifestAsync(jobRoot, taskId);
        var store = new EventStore(jobRoot, "run-ordinary");
        foreach (var state in new[] { "validating", "queued", "running", "waiting_input" })
            await store.AppendAsync("task.state", taskId, new { status = state });
        var handler = new CommandHandler(root);
        using var query = JsonDocument.Parse(JsonSerializer.Serialize(new { schemaVersion = "2.0.0", queryId = "ordinary", type = "task.get", taskId }));
        var response = JsonSerializer.SerializeToElement(handler.HandleQuery(query.RootElement), IpcJson.Options);
        var item = response.GetProperty("items")[0];
        Assert(item.GetProperty("status").GetString() == "waiting_input", "Ordinary waiting_input status was lost.");
        Assert(item.GetProperty("decisionKind").ValueKind == JsonValueKind.Null && item.GetProperty("decisionTargetToken").ValueKind == JsonValueKind.Null, "Ordinary waiting_input was mislabeled as language-practice.");
        return new { name = "ordinary-waiting-input-projection", status = "passed", decisionKind = (string?)null, decisionTargetToken = (string?)null };
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static async Task<object> VerifyTamperFailClosedAsync(string projectRoot, string fakeWorker)
{
    var cases = new[] { "decision-manifest", "decision-payload", "learning-ancestry" };
    var observed = new List<object>();
    foreach (var tamper in cases)
    {
        var root = Path.Combine(Path.GetTempPath(), "KnowledgeClipper.LanguagePractice.Tamper", Guid.NewGuid().ToString("N"));
        var jobRoot = Path.Combine(root, "jobs", "task-tamper-" + tamper);
        Directory.CreateDirectory(jobRoot);
        try
        {
            var input = await StageSyntheticContentInputAsync(jobRoot, "task-tamper-" + tamper);
            var plan = new PipelinePlan("2.0.0", "2.1.0", "task-tamper-" + tamper, 1,
                new[] { ContentStage(input, new { schemaVersion = "content-run/1.0.0", contentInputPath = "inputs/content/standard-input.v2.json", providerMode = "replay", fakeLanguagePracticeDecision = true, fakeLanguagePracticeTamper = tamper }) });
            var registry = Registry(projectRoot, fakeWorker, true);
            var state = await new OrchestratorEngine().RunAsync(jobRoot, plan, registry, projectRoot, "run-tamper", CancellationToken.None);
            throw new InvalidOperationException($"Tampered case {tamper} unexpectedly returned {state}.");
        }
        catch (Exception exception) when (exception is OrchestratorDispatchException or InvalidDataException)
        {
            Assert(!Directory.GetFiles(jobRoot, "decision.json", SearchOption.AllDirectories)
                .Any(path => !path.Contains($"{Path.DirectorySeparatorChar}outputs{Path.DirectorySeparatorChar}")), $"Tampered {tamper} published an active Core decision.");
            observed.Add(new { tamper, rejected = true, error = exception.Message });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    return new { name = "language-practice-artifact-integrity-fail-closed", status = "passed", cases = observed };
}

static async Task<ArtifactReference> StageRealContentFixtureAsync(string projectRoot, string jobRoot, string taskId)
{
    var fixture = Path.Combine(projectRoot, "content-worker", "fixtures", "offline-language-practice-v2");
    var inputNode = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "inputs", "standard-input.v2.json")))!.AsObject();
    inputNode["task_id"] = taskId;
    inputNode["human_overrides"]!.AsObject().Remove("languagePracticeMode");
    foreach (var item in inputNode["items"]!.AsArray())
        item!["source"]!["material_path"] = "inputs/content/" + item["source"]!["material_path"]!.GetValue<string>();
    var files = new[]
    {
        ("inputs/content/standard-input.v2.json", Encoding.UTF8.GetBytes(inputNode.ToJsonString(ContractJson.Options) + "\n"), "application/json"),
        ("inputs/content/provider-replay.v1.json", await File.ReadAllBytesAsync(Path.Combine(fixture, "provider-replay.v1.json")), "application/json"),
        ("inputs/content/materials/zh.md", await File.ReadAllBytesAsync(Path.Combine(fixture, "materials", "zh.md")), "text/markdown; charset=utf-8"),
        ("inputs/content/materials/en.md", await File.ReadAllBytesAsync(Path.Combine(fixture, "materials", "en.md")), "text/markdown; charset=utf-8")
    };
    return await PublishInputAsync(jobRoot, "content-input-language-real", files);
}

static async Task<ArtifactReference> StageSyntheticContentInputAsync(string jobRoot, string taskId)
{
    var input = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = "standard-input/2.0.0", package_id = "package-test", task_id = taskId, input_version = "1", mode = "card", items = new[] { new { item_id = "item-1", source = new { kind = "text", material_path = "inputs/content/materials/clip.md" } } } }, ContractJson.Options);
    return await PublishInputAsync(jobRoot, "content-input-language-fake", new[] { ("inputs/content/standard-input.v2.json", input, "application/json"), ("inputs/content/materials/clip.md", Encoding.UTF8.GetBytes("language fixture"), "text/markdown; charset=utf-8") });
}

static async Task<ArtifactReference> PublishInputAsync(string jobRoot, string artifactId, IReadOnlyList<(string Path, byte[] Bytes, string MediaType)> files)
{
    foreach (var file in files)
    {
        var path = PathPolicy.ResolveJobRelative(jobRoot, file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, file.Bytes);
    }
    const string manifestRef = "inputs/content/artifact.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef), new
    {
        schemaVersion = "1.0.0", artifactId, kind = "content-input", contractVersion = "1.0.0", mediaType = "application/json",
        files = files.Select(file => new { path = file.Path, sha256 = Hash(file.Bytes), bytes = file.Bytes.LongLength, mediaType = file.MediaType }).ToArray(),
        createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId = "capture.normalize", workerId = "language-test.fixture", workerVersion = "1.0.0", attemptId = "source-001" },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = files[0].Path }
    });
    return new ArtifactReference(artifactId, "content-input", "1.0.0", manifestRef, Hash(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, manifestRef))));
}

static PipelineStage ContentStage(ArtifactReference input, object payload) =>
    new("content-understand", "content.deepseek", "understand-and-card", Array.Empty<string>(), new[] { input }, new[] { "learning-bundle", "card-bundle" }, 20000,
        new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { categoryRegistry = "category-registry/2.0.0", promptSet = "open-md-v2" }), JsonSerializer.SerializeToElement(payload));

static async Task WritePlanAndManifestAsync(string jobRoot, string taskId, ArtifactReference input, object payload)
{
    const string planRef = "plans/language-plan.v2.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, planRef), new PipelinePlan("2.0.0", "2.1.0", taskId, 1, new[] { ContentStage(input, payload) }));
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
    {
        schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow,
        createdBy = new { application = "knowledge-clipper-language-test", version = "2.1.0" }, intent = "clip.card", executionProfile = "cards-neutral",
        inputRefs = new[] { new { input.ArtifactRef, input.Kind, input.ContractVersion, expectedSha256 = input.ExpectedSha256 } },
        requestedOutputs = new[] { "card-bundle" }, initialPlanRef = planRef, retentionClass = "ephemeral"
    });
}

static async Task WriteMinimalManifestAsync(string jobRoot, string taskId) =>
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new { schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "language-test", version = "2.1.0" }, intent = "question.follow-up", executionProfile = "cards-neutral", inputRefs = Array.Empty<object>(), requestedOutputs = Array.Empty<string>(), initialPlanRef = "plans/unused.json", retentionClass = "ephemeral" });

static WorkerRegistry Registry(string projectRoot, string workerPath, bool fake) =>
    new("1.0.0", "2.1.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[]
    {
        new WorkerEntry("content.deepseek", ">=1.0.0 <2.0.0", new WorkerEntrypoint(Path.GetRelativePath(projectRoot, workerPath).Replace('\\', '/'), fake ? new[] { "--worker-id", "content.deepseek" } : Array.Empty<string>()),
            new[] { "content.understand-and-card" }, new[] { "content-input" }, new[] { "learning-bundle", "card-bundle", "language-practice-decision", "question.answer" },
            new[] { "parity-remnote", "cards-remnote", "cards-anki" }, "local", "idempotent", true)
    });

static async Task<JsonElement> SendCommandAsync(string pipe, string taskFolder, string type, string? taskId, string payloadRef, string suffix, string? expectedState)
{
    var payload = new { schemaVersion = "1.0.0", commandId = "command-" + suffix, operationId = "operation-" + suffix, taskId, type, issuedAt = DateTimeOffset.UtcNow, idempotencyKey = Hash(Encoding.UTF8.GetBytes("idempotency-" + suffix)), payloadRef, expectedState };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipe, new { protocolVersion = "1.0", correlationId = "correlation-" + suffix, messageType = "task-command", payload }));
    Assert(response.RootElement.GetProperty("ok").GetBoolean(), $"Command {suffix} returned protocol error.");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<JsonElement> WaitStatusAsync(string pipe, string taskId, string expected)
{
    for (var index = 0; index < 200; index++)
    {
        using var response = JsonDocument.Parse(await RoundTripAsync(pipe, new { protocolVersion = "1.0", correlationId = "query-" + index, messageType = "task-query", payload = new { schemaVersion = "2.0.0", queryId = "query-" + index, type = "task.get", taskId } }));
        var items = response.RootElement.GetProperty("payload").GetProperty("items");
        if (items.GetArrayLength() == 1 && items[0].GetProperty("status").GetString() == expected) return items[0].Clone();
        await Task.Delay(50);
    }
    throw new TimeoutException($"Task {taskId} did not reach {expected}.");
}

static async Task WaitErrorAsync(string jobRoot, string commandSuffix, string code) =>
    await WaitForAsync(() => ReadEvents(jobRoot).Any(item => item.CommandId == "command-" + commandSuffix && item.Type == "command.execution_failed" && item.Payload.GetProperty("errorCode").GetString() == code), $"{commandSuffix}/{code}");

static IReadOnlyList<TaskEvent> ReadEvents(string jobRoot) =>
    Directory.Exists(Path.Combine(jobRoot, "runs"))
        ? Directory.GetFiles(Path.Combine(jobRoot, "runs"), "*.json", SearchOption.AllDirectories).Where(path => path.Contains($"{Path.DirectorySeparatorChar}events{Path.DirectorySeparatorChar}")).Select(path => JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options)!).OrderBy(item => item.Timestamp).ToArray()
        : Array.Empty<TaskEvent>();

static async Task WaitForAsync(Func<bool> predicate, string label)
{
    for (var index = 0; index < 200; index++)
    {
        if (predicate()) return;
        await Task.Delay(50);
    }
    throw new TimeoutException("Timed out waiting for " + label + ".");
}

static async Task<string> RoundTripAsync(string pipe, object value)
{
    await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(5000);
    var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(value, IpcJson.Options) + "\n");
    await client.WriteAsync(bytes);
    await client.FlushAsync();
    using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, true);
    return await reader.ReadLineAsync() ?? throw new InvalidDataException("No IPC response.");
}

static async Task<JsonDocument> ReadArtifactPayloadAsync(string jobRoot, ArtifactReference reference)
{
    var manifestBytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, reference.ManifestPath));
    Assert(Hash(manifestBytes).Equals(reference.ExpectedSha256, StringComparison.OrdinalIgnoreCase), "Artifact manifest hash mismatch.");
    using var manifest = JsonDocument.Parse(manifestBytes);
    var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
    var entrypoint = manifest.RootElement.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("entrypoint", out var entry)
        ? entry.GetString()! : files.Single().GetProperty("path").GetString()!;
    var file = files.Single(item => item.GetProperty("path").GetString() == entrypoint);
    var payload = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, entrypoint));
    Assert(payload.LongLength == file.GetProperty("bytes").GetInt64() && Hash(payload).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Artifact payload integrity mismatch.");
    return JsonDocument.Parse(payload);
}

static IpcConfiguration Ipc(string pipe) => new("1.0.0", "windows-named-pipe", pipe, $"\\\\.\\pipe\\{pipe}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query" }, true, "job-event-sequence", 1_048_576, new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"), "../schemas/common/ipc-request.schema.json", "../schemas/common/ipc-response.schema.json", "respond-when-possible-then-close");
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

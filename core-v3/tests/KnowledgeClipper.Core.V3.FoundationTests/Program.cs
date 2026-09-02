using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var receiptPath = ParseReceiptPath(args, repositoryRoot);
var tests = new List<TestResult>();
var startedAt = DateTimeOffset.UtcNow;
var JsonOptions = TestJson.Options;

await RunAsync("authority-bound-capability-catalog", VerifyCapabilityCatalogAsync);
await RunAsync("architecture-legacy-boundary", VerifyArchitectureBoundaryAsync);
await RunAsync("sdk-opaque-artifact-and-domain-facade", VerifySdkBoundaryAsync);
await RunAsync("sdk-external-consumer-compile-boundary", VerifyExternalSdkConsumerBoundaryAsync);
await RunAsync("file-event-store-concurrent-expected-revision", VerifyEventStoreConcurrencyAsync);
await RunAsync("file-event-store-stale-command", VerifyEventStoreStaleRevisionAsync);
await RunAsync("file-event-store-late-segment", VerifyEventStoreLateSegmentAsync);
await RunAsync("file-event-store-truncated-segment", VerifyEventStoreTruncatedSegmentAsync);
await RunAsync("file-event-store-replay", VerifyEventStoreReplayAsync);
await RunAsync("command-index-concurrent-same-key", VerifyConcurrentIdempotencyAsync);
await RunAsync("command-index-same-key-different-body", VerifyIdempotencyFingerprintConflictAsync);
await RunAsync("command-index-restart-replay", VerifyIdempotencyRestartReplayAsync);
await RunAsync("command-index-crash-after-claim", VerifyCrashAfterClaimAsync);
await RunAsync("command-index-crash-after-start", VerifyCrashAfterStartAsync);
await RunAsync("startup-recovery-query-without-resubmit", VerifyStartupRecoveryWithoutResubmitAsync);
await RunAsync("operation-handler-cancellation-terminal", VerifyHandlerCancellationAsync);
await RunAsync("operation-timeout-late-completion-terminal", VerifyLateCompletionTimeoutAsync);
await RunAsync("artifact-v2-publish-and-open", VerifyV2ArtifactAsync);
await RunAsync("artifact-v1-real-anki-four-file-shadow", VerifyRealAnkiArtifactAsync);
await RunAsync("artifact-v1-historical-single-file-shadow", VerifyHistoricalArtifactAsync);
await RunAsync("artifact-malicious-duplicate-property", () => VerifyMaliciousArtifactAsync("duplicate"));
await RunAsync("artifact-malicious-path-escape", () => VerifyMaliciousArtifactAsync("path"));
await RunAsync("artifact-malicious-hash-mismatch", () => VerifyMaliciousArtifactAsync("hash"));
await RunAsync("artifact-malicious-entrypoint-mismatch", () => VerifyMaliciousArtifactAsync("entrypoint"));
await RunAsync("artifact-malicious-duplicate-path", () => VerifyMaliciousArtifactAsync("duplicate-path"));
await RunAsync("client-response-schema-fail-closed", () => VerifyClientResponseBindingAsync("schema"));
await RunAsync("client-response-correlation-fail-closed", () => VerifyClientResponseBindingAsync("correlation"));
await RunAsync("client-response-type-fail-closed", () => VerifyClientResponseBindingAsync("type"));
await RunAsync("client-response-duplicate-property-fail-closed", () => VerifyClientResponseBindingAsync("duplicate"));
await RunAsync("process-current-and-legacy-equivalence", VerifyProcessCanaryAsync);

var passed = tests.All(item => item.Status == "PASS");
var receipt = new
{
    schema = "kc.core-v3-foundation-test-receipt/1",
    startedAt,
    completedAt = DateTimeOffset.UtcNow,
    repositoryRoot,
    status = passed ? "PASS" : "FAIL",
    tests,
    boundaries = new
    {
        externalServicesCalled = false,
        desktopModified = false,
        workersModified = false,
        frozenAuthorityModified = false,
        candidateAssembled = false
    }
};
Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
Console.WriteLine($"RECEIPT {receiptPath}");
Console.WriteLine($"RESULT {(passed ? "PASS" : "FAIL")} {tests.Count(item => item.Status == "PASS")}/{tests.Count}");
return passed ? 0 : 1;

async Task RunAsync(string name, Func<Task> test)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await test();
        tests.Add(new TestResult(name, "PASS", stopwatch.ElapsedMilliseconds, null));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        tests.Add(new TestResult(name, "FAIL", stopwatch.ElapsedMilliseconds, ex.ToString()));
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

async Task VerifyCapabilityCatalogAsync()
{
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.ref.json");
    var catalog = await AdmittedCapabilityCatalog.LoadAsync(repositoryRoot, binding, CancellationToken.None);
    Require(catalog.Document.Operations.Count == 6, "Expected two active and four prepared operations.");
    Require(catalog.GetRequired("content.settings.describe").Available, "Offline operation is not admitted.");
    Require(catalog.GetRequired("artifact.publish").Available, "Artifact publish is not admitted.");
    Require(!catalog.GetRequired("anki.probe").Available, "Anki probe must remain unavailable in foundation.");
    Require(!catalog.GetRequired("question.initial").Available, "Question initial must remain unavailable in foundation.");
    Require(!catalog.GetRequired("question.follow-up").Available, "Question follow-up must remain unavailable in foundation.");
    Require(!catalog.GetRequired("question.make-cards").Available, "Question make-cards must remain unavailable in foundation.");
}

Task VerifyArchitectureBoundaryAsync()
{
    var kernelRoot = Path.Combine(repositoryRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Kernel");
    var allowedRoot = Path.Combine(kernelRoot, "CompatibilityGateway") + Path.DirectorySeparatorChar;
    var violations = new List<string>();
    foreach (var path in Directory.EnumerateFiles(kernelRoot, "*.cs", SearchOption.AllDirectories))
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var content = File.ReadAllText(fullPath);
        if (content.Contains("protocolVersion", StringComparison.Ordinal) ||
            content.Contains("knowledge-clipper-module-action", StringComparison.Ordinal) ||
            content.Contains("module-action", StringComparison.Ordinal))
        {
            violations.Add(Path.GetRelativePath(repositoryRoot, fullPath));
        }
    }

    var clientRoot = Path.Combine(repositoryRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Client");
    foreach (var path in Directory.EnumerateFiles(clientRoot, "*.cs", SearchOption.AllDirectories))
    {
        var content = File.ReadAllText(path);
        if (content.Contains("protocolVersion", StringComparison.Ordinal) || content.Contains("module-action", StringComparison.Ordinal))
        {
            violations.Add(Path.GetRelativePath(repositoryRoot, path));
        }
    }

    Require(violations.Count == 0, "Legacy protocol interpretation escaped CompatibilityGateway: " + string.Join(", ", violations));
    return Task.CompletedTask;
}

Task VerifySdkBoundaryAsync()
{
    var handleType = typeof(TypedArtifactHandle);
    Require(handleType.GetConstructors().Length == 0, "TypedArtifactHandle exposes a public constructor.");
    Require(handleType.GetProperties().All(property => property.PropertyType != typeof(ArtifactReference)), "TypedArtifactHandle exposes ArtifactReference publicly.");
    var facadeMethods = typeof(CoreV3Client).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    Require(facadeMethods.All(method =>
        method.ReturnType != typeof(Task<CommandReceipt>) &&
        method.ReturnType != typeof(Task<QueryResult>) &&
        method.GetParameters().All(parameter => parameter.ParameterType != typeof(CommandEnvelope) && parameter.ParameterType != typeof(QueryEnvelope))),
        "Public SDK facade exposes low-level command/query DTOs.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.AnkiProbeAsync)), "SDK lacks typed AnkiProbeAsync.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.QuestionInitialAsync)), "SDK lacks typed QuestionInitialAsync.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.QuestionFollowUpAsync)), "SDK lacks typed QuestionFollowUpAsync.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.QuestionMakeCardsAsync)), "SDK lacks typed QuestionMakeCardsAsync.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.WaitQuestionProjectionAsync)), "SDK lacks bounded question wait.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.ObserveQuestionProjectionAsync)), "SDK lacks bounded question observation.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.PublishTextArtifactAsync)), "SDK lacks domain artifact publish.");
    Require(facadeMethods.Any(method => method.Name == nameof(CoreV3Client.ReadArtifactAsync)), "SDK lacks typed artifact read.");
    var forbiddenPublicTypes = new[] { typeof(JsonElement), typeof(ArtifactReference), typeof(CapabilityDescriptor), typeof(CommandReceipt), typeof(QueryResult), typeof(CommandEnvelope), typeof(QueryEnvelope) };
    var leakedProperties = typeof(CoreV3Client).Assembly.ExportedTypes
        .SelectMany(type => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        .Where(property => forbiddenPublicTypes.Contains(property.PropertyType))
        .Select(property => $"{property.DeclaringType?.Name}.{property.Name}")
        .ToArray();
    Require(leakedProperties.Length == 0, "Public SDK properties expose protocol DTOs: " + string.Join(", ", leakedProperties));
    Require(!typeof(CommandEnvelope).Assembly.ExportedTypes.Any(), "Contracts assembly exports protocol types.");
    return Task.CompletedTask;
}

async Task VerifyExternalSdkConsumerBoundaryAsync()
{
    var dotnet = ResolveDotnetSdk();
    var positiveProject = Path.Combine(repositoryRoot, "core-v3", "qualification-consumers", "positive", "KnowledgeClipper.Core.V3.SdkPositiveConsumer.csproj");
    var negativeProject = Path.Combine(repositoryRoot, "core-v3", "qualification-consumers", "negative", "KnowledgeClipper.Core.V3.SdkNegativeConsumer.csproj");

    var positive = await RunProcessAsync(dotnet, new[] { "run", "--project", positiveProject, "-c", "Release" }, TimeSpan.FromSeconds(30));
    Require(positive.ExitCode == 0, "External domain SDK consumer did not compile/run: " + positive.Output);
    Require(positive.Output.Contains("SDK_POSITIVE_CONSUMER_OK", StringComparison.Ordinal), "External domain SDK consumer did not reach its runtime assertion.");

    var negative = await RunProcessAsync(dotnet, new[] { "build", negativeProject, "-c", "Release", "--nologo" }, TimeSpan.FromSeconds(30));
    Require(negative.ExitCode != 0, "External consumer compiled forbidden low-level protocol access.");
    foreach (var forbiddenName in new[] { "CommandEnvelope", "QueryEnvelope", "ArtifactReference", "KernelSchemas", "CoreV3ProtocolClient" })
    {
        Require(negative.Output.Contains(forbiddenName + "Access.cs", StringComparison.Ordinal), $"Negative consumer did not prove '{forbiddenName}' inaccessible. Output: {negative.Output}");
    }
}

async Task VerifyEventStoreConcurrencyAsync()
{
    var root = NewTemporaryDirectory("event-store-concurrency");
    try
    {
        var store = new FileEventStore(root);
        store.Append("stream-concurrent", 0, new[] { Event("stream-concurrent", "command.accepted", "seed") });
        var attempts = new[]
        {
            Task.Run(() => TryAppend(store, "stream-concurrent", 1, "left")),
            Task.Run(() => TryAppend(store, "stream-concurrent", 1, "right"))
        };
        var results = await Task.WhenAll(attempts);
        Require(results.Count(item => item) == 1, "Exactly one concurrent writer must win expectedRevision=1.");
        Require(store.ReadStream("stream-concurrent").Count == 2, "Concurrent append produced an unexpected stream length.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

Task VerifyEventStoreStaleRevisionAsync()
{
    var root = NewTemporaryDirectory("event-store-stale");
    try
    {
        var store = new FileEventStore(root);
        store.Append("stream-stale", 0, new[] { Event("stream-stale", "command.accepted", "seed") });
        var rejected = false;
        try
        {
            store.Append("stream-stale", 0, new[] { Event("stream-stale", "operation.started", "late-command") });
        }
        catch (EventStoreConcurrencyException ex)
        {
            rejected = ex.ExpectedRevision == 0 && ex.ActualRevision == 1;
        }

        Require(rejected, "A stale expectedRevision was not rejected precisely.");
        return Task.CompletedTask;
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyEventStoreLateSegmentAsync()
{
    var root = NewTemporaryDirectory("event-store-late-segment");
    try
    {
        var store = new FileEventStore(root);
        store.Append("stream-late", 0, new[] { Event("stream-late", "command.accepted", "seed") });
        var streamDirectory = Path.Combine(root, "streams", "stream-late");
        var latePath = Path.Combine(streamDirectory, "00000000000000000003-00000000000000000003.events.ndjson");
        var lateEvent = Event("stream-late", "operation.completed", "late") with { Sequence = 3 };
        await File.WriteAllTextAsync(latePath, JsonSerializer.Serialize(lateEvent, JsonOptions) + "\n", new UTF8Encoding(false));
        var rejected = false;
        try
        {
            _ = new FileEventStore(root);
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("noncontiguous", StringComparison.Ordinal);
        }

        Require(rejected, "A late/noncontiguous event segment was not rejected during recovery.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyEventStoreTruncatedSegmentAsync()
{
    var root = NewTemporaryDirectory("event-store-truncated");
    try
    {
        var streamDirectory = Path.Combine(root, "streams", "stream-truncated");
        Directory.CreateDirectory(streamDirectory);
        var segmentPath = Path.Combine(streamDirectory, "00000000000000000001-00000000000000000002.events.ndjson");
        var oneEvent = Event("stream-truncated", "command.accepted", "only") with { Sequence = 1 };
        await File.WriteAllTextAsync(segmentPath, JsonSerializer.Serialize(oneEvent, JsonOptions) + "\n", new UTF8Encoding(false));
        var rejected = false;
        try
        {
            _ = new FileEventStore(root);
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("Truncated", StringComparison.Ordinal);
        }

        Require(rejected, "A truncated final event segment was not rejected during recovery.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

Task VerifyEventStoreReplayAsync()
{
    var root = NewTemporaryDirectory("event-store-replay");
    try
    {
        var writer = new FileEventStore(root);
        var committed = writer.Append("stream-replay", 0, new[]
        {
            Event("stream-replay", "command.accepted", "one"),
            Event("stream-replay", "operation.started", "two")
        });
        writer.Append("stream-replay", committed[^1].Sequence, new[] { Event("stream-replay", "operation.completed", "three") });

        var recovered = new FileEventStore(root);
        var replay = recovered.ReadStream("stream-replay");
        Require(recovered.Cursor == 3, "Recovered cursor is not the number of committed events.");
        Require(replay.Select(item => item.Sequence).SequenceEqual(new long[] { 1, 2, 3 }), "Replay did not preserve monotonic sequence.");
        Require(replay.Select(item => item.EventType).SequenceEqual(new[] { "command.accepted", "operation.started", "operation.completed" }), "Replay changed event order.");
        return Task.CompletedTask;
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyConcurrentIdempotencyAsync()
{
    var root = NewTemporaryDirectory("command-index-concurrent");
    try
    {
        var handler = new CountingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(150, cancellationToken);
            return SuccessfulOperationResult("concurrent");
        });
        var application = CreateTestApplication(root, handler, timeoutMilliseconds: 1000);
        var key = new string('C', 64);
        var first = TestCommand("first-command", "first-operation", key, new { value = 1 });
        var retry = TestCommand("retry-command", "retry-operation", key, new { value = 1 });
        var receipts = await Task.WhenAll(
            application.HandleCommandAsync(first, CancellationToken.None),
            application.HandleCommandAsync(retry, CancellationToken.None));
        Require(handler.ExecutionCount == 1, "Concurrent semantic retries executed the handler more than once.");
        Require(receipts[0].CommandId == receipts[1].CommandId, "Concurrent semantic retry did not return the durable first receipt.");
        Require(receipts[0].Events.Select(item => item.EventId).SequenceEqual(receipts[1].Events.Select(item => item.EventId)), "Concurrent semantic retry returned different event evidence.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyIdempotencyFingerprintConflictAsync()
{
    var root = NewTemporaryDirectory("command-index-fingerprint");
    try
    {
        var handler = new CountingHandler((_, _) => Task.FromResult(SuccessfulOperationResult("fingerprint")));
        var application = CreateTestApplication(root, handler, timeoutMilliseconds: 1000);
        var key = new string('D', 64);
        await application.HandleCommandAsync(TestCommand("first-command", "first-operation", key, new { value = 1 }), CancellationToken.None);
        var rejected = false;
        try
        {
            await application.HandleCommandAsync(TestCommand("retry-command", "retry-operation", key, new { value = 2 }), CancellationToken.None);
        }
        catch (KernelContractException ex)
        {
            rejected = ex.Code == "idempotency_conflict";
        }

        Require(rejected, "Same idempotency key with a different canonical body was accepted.");
        Require(handler.ExecutionCount == 1, "Fingerprint conflict executed the handler again.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyIdempotencyRestartReplayAsync()
{
    var root = NewTemporaryDirectory("command-index-restart");
    try
    {
        var firstHandler = new CountingHandler((_, _) => Task.FromResult(SuccessfulOperationResult("restart")));
        var firstApplication = CreateTestApplication(root, firstHandler, timeoutMilliseconds: 1000);
        var key = new string('E', 64);
        var firstReceipt = await firstApplication.HandleCommandAsync(TestCommand("first-command", "first-operation", key, new { value = 1 }), CancellationToken.None);

        var replayHandler = new CountingHandler((_, _) => Task.FromResult(SuccessfulOperationResult("must-not-run")));
        var replayApplication = CreateTestApplication(root, replayHandler, timeoutMilliseconds: 1000);
        var replayReceipt = await replayApplication.HandleCommandAsync(TestCommand("retry-command", "retry-operation", key, new { value = 1 }), CancellationToken.None);
        Require(replayHandler.ExecutionCount == 0, "Restart replay executed the handler again.");
        Require(firstReceipt.Events.Select(item => item.EventId).SequenceEqual(replayReceipt.Events.Select(item => item.EventId)), "Restart replay did not return the same durable receipt.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyCrashAfterClaimAsync()
{
    var root = NewTemporaryDirectory("crash-after-claim");
    try
    {
        var key = new string('1', 64);
        var command = TestCommand("claimed-command", "claimed-operation", key, new { value = 1 });
        var abandoned = new FileCommandIndex(Path.Combine(root, "command-index"), "dead-owner", int.MaxValue);
        Require(abandoned.TryClaim(command, "none").Acquired, "Could not create abandoned claim fixture.");

        var handler = new CountingHandler((_, _) => Task.FromResult(SuccessfulOperationResult("recovered-claim")));
        var application = CreateTestApplication(root, handler, timeoutMilliseconds: 1000);
        var receipt = await application.HandleCommandAsync(TestCommand("retry-command", "retry-operation", key, new { value = 1 }), CancellationToken.None);
        Require(handler.ExecutionCount == 0, "Crash-after-claim recovery blindly re-executed a claimed command.");
        Require(receipt.Events.Last().EventType == "operation.failed", "Crash-after-claim recovery did not become terminal failed.");
        Require(receipt.Events.Last().Data.GetProperty("code").GetString() == "crash_after_claim", "Crash-after-claim recovery code is not crash_after_claim.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyCrashAfterStartAsync()
{
    var root = NewTemporaryDirectory("crash-after-start");
    try
    {
        var key = new string('2', 64);
        var command = TestCommand("started-command", "started-operation", key, new { value = 1 });
        var abandoned = new FileCommandIndex(Path.Combine(root, "command-index"), "dead-owner", int.MaxValue);
        Require(abandoned.TryClaim(command, "none").Acquired, "Could not create abandoned started claim.");
        var eventStore = new FileEventStore(Path.Combine(root, "events"));
        eventStore.Append(command.OperationId, 0, new[]
        {
            Event(command.OperationId, "command.accepted", command.CommandId),
            Event(command.OperationId, "operation.started", command.CommandId)
        });

        var handler = new CountingHandler((_, _) => Task.FromResult(SuccessfulOperationResult("must-not-run")));
        var application = CreateTestApplication(root, handler, timeoutMilliseconds: 1000);
        var receipt = await application.HandleCommandAsync(TestCommand("retry-command", "retry-operation", key, new { value = 1 }), CancellationToken.None);
        Require(handler.ExecutionCount == 0, "Crash-after-start blindly re-executed the handler.");
        Require(receipt.Events.Last().EventType == "operation.failed", "Effect-none crash-after-start did not recover to failed.");
        Require(receipt.Events.Last().Data.GetProperty("code").GetString() == "process_interrupted", "Crash-after-start recovery code is not process_interrupted.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyStartupRecoveryWithoutResubmitAsync()
{
    var root = NewTemporaryDirectory("startup-recovery-query-only");
    try
    {
        var key = new string('3', 64);
        var command = TestCommand("started-command", "started-operation", key, new { value = 1 });
        var abandoned = new FileCommandIndex(Path.Combine(root, "command-index"), "dead-owner", int.MaxValue);
        Require(abandoned.TryClaim(command, "none").Acquired, "Could not create startup recovery claim.");
        var eventStore = new FileEventStore(Path.Combine(root, "events"));
        eventStore.Append(command.OperationId, 0, new[]
        {
            Event(command.OperationId, "command.accepted", command.CommandId),
            Event(command.OperationId, "operation.started", command.CommandId)
        });

        var handler = new CountingHandler((_, _) => Task.FromResult(SuccessfulOperationResult("must-not-run")));
        var application = CreateTestApplication(root, handler, timeoutMilliseconds: 1000);
        application.RecoverIncompleteCommands();
        var projection = await application.HandleQueryAsync(new QueryEnvelope(
            KernelSchemas.Query,
            "startup-query",
            "operation.projection",
            Subject: null,
            "read-your-writes",
            Cursor: null,
            JsonSerializer.SerializeToElement(new { operationId = command.OperationId })), CancellationToken.None);
        Require(projection.Data.GetProperty("lifecycle").EnumerateArray().Last().GetString() == "operation.failed", "Startup sweep left the projection Running.");
        Require(handler.ExecutionCount == 0, "Startup sweep invoked an operation handler.");
        Require(new FileCommandIndex(Path.Combine(root, "command-index")).TryReadReceipt(key) is not null, "Startup sweep did not rebuild the durable receipt.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyHandlerCancellationAsync()
{
    var root = NewTemporaryDirectory("handler-cancellation");
    try
    {
        var handler = new CountingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SuccessfulOperationResult("unreachable");
        });
        var application = CreateTestApplication(root, handler, timeoutMilliseconds: 1000);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        var receipt = await application.HandleCommandAsync(TestCommand("cancel-command", "cancel-operation", new string('4', 64), new { value = 1 }), cancellation.Token);
        Require(receipt.Events.Last().EventType == "operation.cancelled", "Handler cancellation did not produce a durable cancelled terminal.");
        Require(receipt.Events.Count(item => IsTerminalEvent(item.EventType)) == 1, "Handler cancellation produced multiple terminal events.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyLateCompletionTimeoutAsync()
{
    var root = NewTemporaryDirectory("late-completion");
    try
    {
        var handler = new CountingHandler(async (_, _) =>
        {
            await Task.Delay(350);
            return SuccessfulOperationResult("late-success");
        });
        var application = CreateTestApplication(root, handler, timeoutMilliseconds: 75);
        var command = TestCommand("timeout-command", "timeout-operation", new string('5', 64), new { value = 1 });
        var receipt = await application.HandleCommandAsync(command, CancellationToken.None);
        Require(receipt.Events.Last().EventType == "operation.failed", "Effect-none timeout did not produce failed terminal.");
        Require(receipt.Events.Last().Data.GetProperty("code").GetString() == "operation_timed_out", "Timeout terminal code is not operation_timed_out.");
        await Task.Delay(450);
        var replay = new FileEventStore(Path.Combine(root, "events")).ReadStream(command.OperationId);
        Require(replay.Count(item => IsTerminalEvent(item.EventType)) == 1, "Late handler completion appended a second terminal event.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyV2ArtifactAsync()
{
    var root = NewTemporaryDirectory("artifact-v2");
    try
    {
        var primary = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
        var supporting = Encoding.UTF8.GetBytes("trace\n");
        var envelope = new ArtifactEnvelope(
            KernelSchemas.Artifact,
            "foundation-artifact",
            "foundation-status",
            "1.0.0",
            "foundation-test-store",
            new[]
            {
                Representation("primary", "status.json", "application/json", primary, "primary"),
                Representation("trace", "evidence/trace.ndjson", "application/x-ndjson", supporting, "supporting")
            },
            "primary",
            new ArtifactProvenance("foundation-test", Array.Empty<ArtifactReference>()),
            new ArtifactPolicies("test", "ephemeral"));
        var store = new FileArtifactStore(root, "foundation-test-store");
        var reference = await store.PublishAsync(
            envelope,
            new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            {
                ["primary"] = primary,
                ["trace"] = supporting
            },
            CancellationToken.None);
        var opened = await store.OpenVerifiedAsync(reference, "foundation-status", "1.0.0", CancellationToken.None);
        Require(opened.Representations.Count == 2, "V2 artifact lost a representation.");
        Require(Encoding.UTF8.GetString(opened.Primary.Content) == "{\"status\":\"ok\"}", "V2 primary representation differs.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyRealAnkiArtifactAsync()
{
    var packageRoot = NewTemporaryDirectory("artifact-v1-anki-shadow");
    try
    {
        var outputRoot = "runs/run-fixture/stages/anki-probe/attempts/attempt-fixture/outputs";
        var files = new Dictionary<string, byte[]>
        {
            [outputRoot + "/connection-status.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"anki-connection-status.v1\",\"externalMutation\":false}"),
            [outputRoot + "/operations.ndjson"] = Encoding.UTF8.GetBytes("{\"action\":\"version\",\"status\":\"SUCCEEDED\"}\n"),
            [outputRoot + "/preflight.json"] = Encoding.UTF8.GetBytes("{\"status\":\"SUCCEEDED\"}"),
            [outputRoot + "/receipt.json"] = Encoding.UTF8.GetBytes("{\"status\":\"SUCCEEDED\"}")
        };
        var manifest = await WriteLegacyFixtureAsync(
            packageRoot,
            outputRoot + "/anki-connection-status.artifact.json",
            "anki-connection-status-fixture",
            "anki-connection-status",
            files,
            outputRoot + "/connection-status.json");
        var artifact = await FileArtifactStore.OpenLegacyV1AndUpconvertAsync(manifest, packageRoot, CancellationToken.None);
        Require(artifact.Envelope.Kind == "anki-connection-status", "Unexpected real-shape Anki artifact kind.");
        Require(artifact.Representations.Count == 4, "Self-contained Anki v1 shadow did not retain all four files.");
        Require(artifact.Primary.Descriptor.Path.EndsWith("/connection-status.json", StringComparison.Ordinal), "Anki metadata.entrypoint was not selected.");
    }
    finally
    {
        DeleteTemporaryDirectory(packageRoot);
    }
}

async Task VerifyHistoricalArtifactAsync()
{
    var jobRoot = NewTemporaryDirectory("artifact-v1-historical-shadow");
    try
    {
        var manifest = await WriteLegacyFixtureAsync(
            jobRoot,
            "artifacts/capture-text-001/artifact.json",
            "capture-text-001",
            "capture-text",
            new Dictionary<string, byte[]> { ["inputs/selection.txt"] = Encoding.UTF8.GetBytes("self-contained historical fixture") },
            "inputs/selection.txt");
        var artifact = await FileArtifactStore.OpenLegacyV1AndUpconvertAsync(manifest, jobRoot, CancellationToken.None);
        Require(artifact.Representations.Count == 1, "Historical single-file artifact changed cardinality.");
        Require(artifact.Primary.Descriptor.Path == "inputs/selection.txt", "Historical entrypoint changed.");
    }
    finally
    {
        DeleteTemporaryDirectory(jobRoot);
    }
}

async Task<string> WriteLegacyFixtureAsync(
    string root,
    string manifestRelative,
    string artifactId,
    string kind,
    IReadOnlyDictionary<string, byte[]> files,
    string entrypoint)
{
    foreach (var pair in files)
    {
        var path = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, pair.Value);
    }
    var manifest = new
    {
        schemaVersion = "1.0.0",
        artifactId,
        contractVersion = "1.0.0",
        kind,
        mediaType = "application/json",
        files = files.Select(pair => new
        {
            path = pair.Key,
            mediaType = pair.Key.EndsWith(".ndjson", StringComparison.Ordinal) ? "application/x-ndjson" : pair.Key.EndsWith(".txt", StringComparison.Ordinal) ? "text/plain" : "application/json",
            bytes = pair.Value.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()
        }).ToArray(),
        createdAt = DateTimeOffset.UnixEpoch,
        producer = new { stageId = "fixture", workerId = "fixture", workerVersion = "1.0.0", attemptId = "fixture" },
        inputRefs = Array.Empty<object>(),
        dataClass = "synthetic",
        metadata = new { entrypoint }
    };
    var manifestPath = Path.Combine(root, manifestRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
    await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
    return manifestPath;
}

async Task VerifyMaliciousArtifactAsync(string mode)
{
    var root = NewTemporaryDirectory("artifact-malicious-" + mode);
    try
    {
        var payloadPath = Path.Combine(root, "payload.json");
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
        await File.WriteAllBytesAsync(payloadPath, payload);
        var correctHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var path = mode == "path" ? "../outside.json" : "payload.json";
        var hash = mode == "hash" ? new string('0', 64) : correctHash;
        var entrypoint = mode == "entrypoint" ? "missing.json" : "payload.json";
        var files = mode == "duplicate-path"
            ? $"{{\"path\":\"payload.json\",\"mediaType\":\"application/json\",\"bytes\":{payload.Length},\"sha256\":\"{correctHash}\"}},{{\"path\":\"payload.json\",\"mediaType\":\"application/json\",\"bytes\":{payload.Length},\"sha256\":\"{correctHash}\"}}"
            : $"{{\"path\":\"{path}\",\"mediaType\":\"application/json\",\"bytes\":{payload.Length},\"sha256\":\"{hash}\"}}";
        var duplicate = mode == "duplicate" ? ",\"artifactId\":\"overwritten\"" : string.Empty;
        var manifestJson = $"{{\"schemaVersion\":\"1.0.0\",\"artifactId\":\"malicious\"{duplicate},\"contractVersion\":\"1.0.0\",\"kind\":\"fixture\",\"files\":[{files}],\"metadata\":{{\"entrypoint\":\"{entrypoint}\"}}}}";
        var manifest = Path.Combine(root, "artifact.json");
        await File.WriteAllTextAsync(manifest, manifestJson, new UTF8Encoding(false));
        var rejected = false;
        try
        {
            await FileArtifactStore.OpenLegacyV1AndUpconvertAsync(manifest, root, CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected, $"Malicious {mode} fixture was accepted.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyClientResponseBindingAsync(string mode)
{
    var pipeName = "KnowledgeClipper.Core.V3.ResponseBinding." + Guid.NewGuid().ToString("N");
    var server = ServeSingleResponseAsync(pipeName, mode);
    var client = new CoreV3ProtocolClient(pipeName, TimeSpan.FromSeconds(2));
    string? code = null;
    try
    {
        _ = await client.HandshakeAsync(CancellationToken.None);
    }
    catch (CoreV3ClientException ex)
    {
        code = ex.Code;
    }

    await server;
    var expected = mode switch
    {
        "schema" => "response_schema_mismatch",
        "correlation" => "response_correlation_mismatch",
        "type" => "response_type_mismatch",
        "duplicate" => "response_duplicate_property",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    Require(code == expected, $"Client response {mode} mismatch did not fail closed with {expected}; actual={code}.");
}

async Task ServeSingleResponseAsync(string pipeName, string mode)
{
    await using var server = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await server.WaitForConnectionAsync();
    using var reader = new StreamReader(server, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    var requestText = await reader.ReadLineAsync() ?? throw new EndOfStreamException("Response-binding fixture received no request.");
    using var request = JsonDocument.Parse(requestText);
    var correlationId = request.RootElement.GetProperty("correlationId").GetString()!;
    var response = JsonSerializer.Serialize(new
    {
        schema = mode == "schema" ? "kc.wire-response/999" : KernelSchemas.WireResponse,
        correlationId = mode == "correlation" ? "wrong-correlation" : correlationId,
        messageType = mode == "type" ? "query-result" : "handshake-result",
        ok = true,
        payload = new
        {
            schema = KernelSchemas.HandshakeResult,
            kernelVersion = "fixture",
            currentWireSchema = KernelSchemas.WireRequest,
            capabilityCatalogId = "fixture",
            capabilityCatalogSha256 = new string('0', 64),
            legacyCompatibilityBoundary = "fixture"
        },
        error = (object?)null
    }, CompactJson.Options);
    if (mode == "duplicate")
    {
        response = response.Insert(response.Length - 1, ",\"ok\":true");
    }

    response += "\n";
    var bytes = Encoding.UTF8.GetBytes(response);
    await server.WriteAsync(bytes);
    await server.FlushAsync();
}

async Task VerifyProcessCanaryAsync()
{
    var dotnet = ResolveDotnetSdk();
    var hostDll = Path.Combine(repositoryRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Host", "bin", "Release", "net8.0", "KnowledgeClipper.Core.V3.Host.dll");
    Require(File.Exists(hostDll), "Core v3 Host has not been built.");
    var pipeName = "KnowledgeClipper.Core.V3.Foundation." + Guid.NewGuid().ToString("N");
    var dataRoot = NewTemporaryDirectory("process-canary");
    var longRunningStore = new FileEventStore(Path.Combine(dataRoot, "events-v3"));
    longRunningStore.Append("question-long-running", 0, new[]
    {
        Event("question-long-running", "command.accepted", "question-long-running"),
        Event("question-long-running", "operation.started", "question-long-running")
    });
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.ref.json");
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    process.StartInfo.ArgumentList.Add(hostDll);
    process.StartInfo.ArgumentList.Add("--repository-root");
    process.StartInfo.ArgumentList.Add(repositoryRoot);
    process.StartInfo.ArgumentList.Add("--binding");
    process.StartInfo.ArgumentList.Add(binding);
    process.StartInfo.ArgumentList.Add("--data-root");
    process.StartInfo.ArgumentList.Add(dataRoot);
    process.StartInfo.ArgumentList.Add("--pipe-name");
    process.StartInfo.ArgumentList.Add(pipeName);

    try
    {
        Require(process.Start(), "Core v3 Host did not start.");
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = await process.StandardOutput.ReadLineAsync(readyTimeout.Token);
        if (ready != $"CORE_V3_READY {pipeName}")
        {
            var error = process.HasExited ? await process.StandardError.ReadToEndAsync() : "Host remained running without the expected readiness line.";
            throw new InvalidOperationException($"Core v3 Host did not report readiness. Actual='{ready}'. {error}");
        }

        var client = new CoreV3Client(pipeName);
        Console.WriteLine("CANARY capabilities.get");
        var capabilities = await WithIpcTimeoutAsync("capabilities.get", client.GetCapabilitiesAsync);
        Require(capabilities.CatalogId == "knowledge-clipper-core-v3-foundation", "Capabilities query returned a different catalog.");

        var currentOperationId = "content-settings-describe-current-offline-canary";
        Console.WriteLine("CANARY current command");
        var currentDescription = await WithIpcTimeoutAsync("current command", token => client.DescribeContentSettingsAsync("current-offline-canary", token));
        Require(currentDescription.ExternalWriteCount == 0, "Current offline operation reported external writes.");

        Console.WriteLine("CANARY artifact publish/read");
        var artifactHandle = await WithIpcTimeoutAsync(
            "artifact publish",
            token => client.PublishTextArtifactAsync(new PublishTextArtifactRequest("artifact-roundtrip-canary", ArtifactPurpose.Question, "2 + 2 = ?"), token));
        var artifactContent = await WithIpcTimeoutAsync("artifact read", token => client.ReadArtifactAsync(artifactHandle, cancellationToken: token));
        Require(Encoding.UTF8.GetString(artifactContent.Content) == "2 + 2 = ?", "Typed artifact publish/read roundtrip changed content.");

        Console.WriteLine("CANARY question observation >5s");
        var observer = new CoreV3Client(pipeName, new CoreV3ClientOptions(
            RequestTimeout: TimeSpan.FromSeconds(5),
            ObservationTimeout: TimeSpan.FromSeconds(8),
            PollInterval: TimeSpan.FromMilliseconds(100)));
        using (var observationCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5500)))
        {
            var observations = new List<QuestionProjectionSnapshot>();
            var observationStopwatch = Stopwatch.StartNew();
            try
            {
                await foreach (var observation in observer.ObserveQuestionProjectionAsync("question-long-running", observationCancellation.Token))
                {
                    observations.Add(observation);
                }
            }
            catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested)
            {
            }

            Require(observationStopwatch.Elapsed >= TimeSpan.FromSeconds(5), "Long-running observation did not remain active for more than five seconds.");
            Require(observations.Count > 0 && observations.All(item => item.State == QuestionProjectionState.Running), "A healthy >5s running projection was degraded because its cursor was unchanged.");
        }

        var actionId = "legacy-offline-canary";
        var inputRef = $"module-actions/{actionId}/input.json";
        var inputPath = Path.Combine(dataRoot, "module-actions", actionId, "input.json");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await File.WriteAllTextAsync(inputPath, "{\"includeSchemas\":true}", new UTF8Encoding(false));
        var legacyRequest = JsonSerializer.Serialize(new
        {
            protocolVersion = "1.6",
            correlationId = "legacy-correlation-canary",
            messageType = "module-action",
            payload = new
            {
                schemaVersion = "knowledge-clipper-module-action.v5",
                actionId,
                operation = "content.settings.describe",
                payloadRef = inputRef,
                idempotencyKey = new string('B', 64)
            }
        }, CompactJson.Options);
        Console.WriteLine("CANARY legacy command");
        var legacyResponse = await WithIpcTimeoutAsync("legacy command", token => client.SendCompatibilityFrameAsync(legacyRequest, token));
        using var legacyJson = JsonDocument.Parse(legacyResponse);
        Require(legacyJson.RootElement.GetProperty("ok").GetBoolean(), "Legacy converted operation failed.");
        Require(legacyJson.RootElement.GetProperty("payload").GetProperty("externalWriteCount").GetInt32() == 0, "Legacy operation reported external writes.");

        Console.WriteLine("CANARY projections");
        var currentProjection = await WithIpcTimeoutAsync("current projection", token => QueryProjectionAsync(client, "projection-current", currentOperationId, token));
        var legacyProjection = await WithIpcTimeoutAsync("legacy projection", token => QueryProjectionAsync(client, "projection-legacy", "legacy-" + actionId, token));
        var currentResult = currentProjection.Data.GetProperty("result");
        var legacyResult = legacyProjection.Data.GetProperty("result");
        Require(Canonicalize(currentResult) == Canonicalize(legacyResult), "Current and legacy clients reached different operation results.");
        Require(Canonicalize(currentProjection.Data.GetProperty("lifecycle")) == Canonicalize(legacyProjection.Data.GetProperty("lifecycle")), "Current and legacy clients produced different lifecycle events.");
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        DeleteTemporaryDirectory(dataRoot);
    }
}

async Task<QueryResult> QueryProjectionAsync(CoreV3Client client, string queryId, string operationId, CancellationToken cancellationToken)
{
    return await client.QueryProtocolAsync(new QueryEnvelope(
        KernelSchemas.Query,
        queryId,
        "operation.projection",
        Subject: null,
        "read-your-writes",
        Cursor: null,
        JsonSerializer.SerializeToElement(new { operationId }, JsonOptions)), cancellationToken);
}

static async Task<T> WithIpcTimeoutAsync<T>(string step, Func<CancellationToken, Task<T>> action)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        return await action(timeout.Token);
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        throw new TimeoutException($"Process Canary timed out during {step}.");
    }
}

static ArtifactRepresentation Representation(string id, string path, string mediaType, byte[] content, string role)
{
    return new ArtifactRepresentation(
        id,
        path,
        mediaType,
        Schema: null,
        new ArtifactDigest("sha256", Convert.ToHexString(SHA256.HashData(content))),
        content.Length,
        new[] { role });
}

static KernelEvent Event(string streamId, string eventType, string marker)
{
    return new KernelEvent(
        KernelSchemas.Event,
        "evt-" + Guid.NewGuid().ToString("N"),
        streamId,
        Sequence: 0,
        eventType,
        EventVersion: 1,
        DateTimeOffset.UtcNow,
        CorrelationId: marker,
        CausationId: marker,
        Operation: "foundation.event-store-test",
        EffectClass: "none",
        JsonSerializer.SerializeToElement(new { marker }),
        Array.Empty<ArtifactReference>());
}

static KernelApplication CreateTestApplication(string root, IOperationHandler handler, int timeoutMilliseconds)
{
    var descriptor = new CapabilityDescriptor(
        handler.OperationId,
        Available: true,
        AvailabilityReason: null,
        CommandSchema: "kc.content-settings-describe-command/1",
        ResultArtifactKind: null,
        ResultContract: null,
        EffectClass: "none",
        AuthorizationPolicy: "none",
        RetryPolicy: "safe",
        IdempotencyPolicy: "required",
        timeoutMilliseconds,
        new WorkerBinding("in-process", null, null, handler.GetType().Name),
        new UiAffordance("test", "test", false));
    var catalog = AdmittedCapabilityCatalog.CreateForTesting(new CapabilityCatalogDocument(
        KernelSchemas.CapabilityCatalog,
        "foundation-test-catalog",
        "test",
        new AuthorityAnchor("test", "test", new string('0', 64)),
        new[] { descriptor }));
    return new KernelApplication(
        catalog,
        new OperationRegistry(new[] { handler }),
        new DisabledWorkerRuntime(),
        new FileEventStore(Path.Combine(root, "events")),
        new FileCommandIndex(Path.Combine(root, "command-index")),
        new FileArtifactStore(Path.Combine(root, "artifacts"), "test-store"));
}

static CommandEnvelope TestCommand(string commandId, string operationId, string idempotencyKey, object body)
{
    return new CommandEnvelope(
        KernelSchemas.Command,
        commandId,
        operationId,
        "content.settings.describe",
        new KernelSubject("foundation-test", "same-subject"),
        idempotencyKey,
        ExpectedRevision: null,
        Authorization: null,
        Inputs: Array.Empty<ArtifactReference>(),
        new ContractBody("kc.content-settings-describe-command/1", JsonSerializer.SerializeToElement(body)));
}

static OperationExecutionResult SuccessfulOperationResult(string marker)
    => new(JsonSerializer.SerializeToElement(new { marker }), Array.Empty<ArtifactReference>(), 0, true);

static bool IsTerminalEvent(string eventType)
    => eventType is "operation.completed" or "operation.failed" or "operation.cancelled" or "operation.unknown-external-state";

static bool TryAppend(FileEventStore store, string streamId, long expectedRevision, string marker)
{
    try
    {
        store.Append(streamId, expectedRevision, new[] { Event(streamId, "operation.started", marker) });
        return true;
    }
    catch (EventStoreConcurrencyException)
    {
        return false;
    }
}

static string Canonicalize(JsonElement element)
{
    return element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => JsonSerializer.Serialize(item.Name) + ":" + Canonicalize(item.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonicalize)) + "]",
        _ => element.GetRawText()
    };
}

static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    Require(process.Start(), $"Could not start '{fileName}'.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
        await process.WaitForExitAsync(cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw new TimeoutException($"Process '{fileName}' exceeded {timeout.TotalSeconds:0} seconds.");
    }

    var output = (await standardOutput) + Environment.NewLine + (await standardError);
    return (process.ExitCode, output);
}

static string ResolveDotnetSdk()
{
    var configured = Environment.GetEnvironmentVariable("KC_DOTNET_PATH");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
    {
        return configured;
    }

    var projectSdk = @"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe";
    return File.Exists(projectSdk) ? projectSdk : throw new FileNotFoundException("The project .NET SDK was not found.", projectSdk);
}

static string NewTemporaryDirectory(string label)
{
    var path = Path.Combine(Path.GetTempPath(), "KnowledgeClipper.Core.V3", label + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return Path.GetFullPath(path);
}

static void DeleteTemporaryDirectory(string path)
{
    var fullPath = Path.GetFullPath(path);
    var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KnowledgeClipper.Core.V3")) + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Refusing to delete a directory outside the Core v3 test root.");
    }

    if (Directory.Exists(fullPath))
    {
        Directory.Delete(fullPath, recursive: true);
    }
}

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "contracts", "core", "v2.10", "checkpoints", "v2.10.6-20260902T041800Z", "checkpoint.json")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the RemNote Quick Clipper repository root.");
}

static string ParseReceiptPath(string[] args, string root)
{
    if (args.Length == 2 && args[0] == "--receipt")
    {
        return Path.GetFullPath(args[1]);
    }

    return Path.Combine(root, "core-v3", "receipts", "foundation-tests.latest.json");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record TestResult(string Name, string Status, long DurationMilliseconds, string? Error);

sealed class CountingHandler : IOperationHandler
{
    private readonly Func<CommandEnvelope, CancellationToken, Task<OperationExecutionResult>> _execute;
    private int _executionCount;

    public CountingHandler(Func<CommandEnvelope, CancellationToken, Task<OperationExecutionResult>> execute)
    {
        _execute = execute;
    }

    public string OperationId => "content.settings.describe";

    public int ExecutionCount => Volatile.Read(ref _executionCount);

    public Task<OperationExecutionResult> ExecuteAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executionCount);
        return _execute(command, cancellationToken);
    }
}

static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

static class CompactJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

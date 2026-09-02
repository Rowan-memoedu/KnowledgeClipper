using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
if (args.Length == 4 && args[0] == "--real-anki-probe-config" && args[2] == "--real-anki-probe-receipt")
{
    return await RunRealAnkiProbeAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
var receiptPath = ParseReceiptPath(args, repositoryRoot);
var startedAt = DateTimeOffset.UtcNow;
var tests = new List<TestResult>();
SliceEvidence? sliceEvidence = null;

await RunAsync("authority-bound-fake-question-anki-vertical-slice", async () => sliceEvidence = await VerifyVerticalSliceAsync());
await RunAsync("sdk-projection-uses-named-domain-artifacts", VerifyNamedProjectionOutputsAsync);
await RunAsync("production-host-rejects-fake-runtime-switch", VerifyProductionFailClosedAsync);
await RunAsync("slow-worker-fast-accept-and-reconnect-observation", VerifySlowWorkerAndReconnectAsync);
await RunAsync("question-session-crash-recovery", VerifyQuestionSessionCrashRecoveryAsync);
await RunAsync("authority-entrypoint-hash-drift-rejected", () => VerifyAdmissionDriftAsync("hash"));
await RunAsync("authority-exact-worker-version-drift-rejected", () => VerifyAdmissionDriftAsync("version"));
await RunAsync("authority-contract-version-drift-rejected", () => VerifyAdmissionDriftAsync("contract"));
await RunAsync("worker-process-runtime-offline-contract", VerifyWorkerProcessRuntimeOfflineAsync);
await RunAsync("worker-process-unknown-state-is-distinct", VerifyWorkerProcessUnknownStateAsync);

var passed = tests.All(item => item.Status == "PASS");
var receipt = new
{
    schema = "kc.core-v3-phase2-question-anki-slice-test-receipt/1",
    startedAt,
    completedAt = DateTimeOffset.UtcNow,
    repositoryRoot,
    status = passed ? "PASS" : "FAIL",
    tests,
    evidence = sliceEvidence,
    boundaries = new
    {
        executionMode = "authority-bound-independent-fake-proxies",
        legacyOrchestratorInvoked = false,
        realAnkiCalled = false,
        realDeepSeekCalled = false,
        remNoteCalled = false,
        minerUCalled = false,
        speakerCalled = false,
        frozenAuthorityModified = false,
        foundation2Modified = false,
        finalCandidateAssembled = false,
        specSourceStatus = "temporarily-unavailable-non-blocking-for-approved-scope"
    }
};
Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
Console.WriteLine($"RECEIPT {receiptPath}");
Console.WriteLine($"RESULT {(passed ? "PASS" : "FAIL")} {tests.Count(item => item.Status == "PASS")}/{tests.Count}");
return passed ? 0 : 1;

async Task RunAsync(string name, Func<Task> action)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await action();
        tests.Add(new TestResult(name, "PASS", stopwatch.ElapsedMilliseconds, null));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        tests.Add(new TestResult(name, "FAIL", stopwatch.ElapsedMilliseconds, ex.ToString()));
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

async Task<int> RunRealAnkiProbeAsync(string configurationPath, string outputReceiptPath)
{
    var started = DateTimeOffset.UtcNow;
    var dataRoot = Path.Combine(Path.GetDirectoryName(outputReceiptPath)!, "isolated-data");
    Directory.CreateDirectory(dataRoot);
    Process? process = null;
    try
    {
        var dotnet = ResolveDotnetSdk();
        var hostDll = Path.Combine(repositoryRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Host", "bin", "Release", "net8.0", "KnowledgeClipper.Core.V3.Host.dll");
        var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
        var pipeName = "KnowledgeClipper.Core.V3.RealProbe." + Guid.NewGuid().ToString("N");
        process = StartProductionHost(dotnet, hostDll, binding, pipeName, dataRoot);
        await WaitReadyAsync(process, pipeName);
        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100)));
        var configurationJson = await File.ReadAllTextAsync(configurationPath);
        var configuration = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(
            "real-anki-readonly-config-" + Guid.NewGuid().ToString("N")[..12],
            ArtifactPurpose.AnkiConfiguration,
            configurationJson,
            "sensitive"));
        var submission = await client.AnkiProbeAsync(new AnkiProbeRequest("real-anki-readonly-probe-" + Guid.NewGuid().ToString("N")[..12], configuration));
        var projection = await client.WaitOperationProjectionAsync(submission.OperationId);
        Require(projection.State == OperationProjectionState.Succeeded && projection.AnkiConnectionStatusArtifact is not null,
            $"Real Anki read-only probe did not succeed. State={projection.State}; Notice={projection.Notice}");
        Require(projection.AnkiReadiness is { ReadyForRequestedAction: true, SelectedRouteReady: true, OverallState: "partially_ready" or "ready" },
            "Real Anki readiness summary did not preserve selected-route readiness.");
        using var statusDocument = JsonDocument.Parse((await client.ReadArtifactAsync(projection.AnkiConnectionStatusArtifact!)).Content);
        var status = statusDocument.RootElement;
        Require(status.GetProperty("externalMutation").ValueKind == JsonValueKind.False, "Real Anki probe reported external mutation.");
        Require(status.GetProperty("mutatingActionsObserved").GetArrayLength() == 0, "Real Anki probe observed mutating actions.");
        Require(status.GetProperty("externalWriteCount").GetInt32() == 0, "Real Anki probe reported an external write.");
        var metrics = await ReadMetricsAsync(client);
        Require(metrics.AnkiProbeAttempts == 1 && metrics.AnkiExportAttempts == 0 && metrics.AnkiExternalWriteCount == 0 && metrics.AnkiUnknownWriteAttempts == 0,
            "Real Anki probe runtime metrics are not strictly read-only.");
        var receipt = new
        {
            schema = "kc.core-v3-real-anki-readonly-probe-receipt/1",
            startedAt = started,
            completedAt = DateTimeOffset.UtcNow,
            status = "PASS",
            projectionState = projection.State.ToString(),
            connectionStatus = status.GetProperty("status").GetString(),
            readiness = projection.AnkiReadiness,
            externalMutation = false,
            mutatingActionsObserved = 0,
            confirmedExternalWrites = metrics.AnkiExternalWriteCount,
            unknownWriteAttempts = metrics.AnkiUnknownWriteAttempts,
            ankiProbeAttempts = metrics.AnkiProbeAttempts,
            ankiExportAttempts = metrics.AnkiExportAttempts,
            configurationRecorded = false,
            productionHost = true,
            fakeRuntimeAvailableInProductionHost = false,
            realDeepSeekCalled = false,
            remNoteCalled = false,
            minerUCalled = false,
            speakerCalled = false
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputReceiptPath)!);
        await File.WriteAllBytesAsync(outputReceiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine($"RECEIPT {outputReceiptPath}");
        Console.WriteLine("RESULT PASS REAL_ANKI_READONLY_PROBE");
        return 0;
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputReceiptPath)!);
        var receipt = new
        {
            schema = "kc.core-v3-real-anki-readonly-probe-receipt/1",
            startedAt = started,
            completedAt = DateTimeOffset.UtcNow,
            status = "FAIL",
            errorType = ex.GetType().Name,
            error = ex.Message,
            externalWriteCountKnown = false,
            configurationRecorded = false
        };
        await File.WriteAllBytesAsync(outputReceiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine($"RECEIPT {outputReceiptPath}");
        Console.WriteLine($"RESULT FAIL REAL_ANKI_READONLY_PROBE {ex.Message}");
        return 1;
    }
    finally
    {
        if (process is not null)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}

async Task VerifyWorkerProcessRuntimeOfflineAsync()
{
    var bindingPath = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var catalog = await AdmittedCapabilityCatalog.LoadAsync(repositoryRoot, bindingPath, CancellationToken.None);
    var packageRoot = NewTemporaryDirectory("worker-process-offline");
    try
    {
        var invoker = new OfflineConformanceWorkerProcessInvoker();
        var runtime = await WorkerProcessRuntime.CreateAsync(catalog, repositoryRoot, packageRoot, CancellationToken.None, invoker);
        var anki = runtime.GetAdmittedBinding("export.anki");
        var content = runtime.GetAdmittedBinding("content.deepseek");
        const string configJson = "{\"schema_version\":\"anki-export-config.v1\",\"provision_managed\":false}";
        var probe = await runtime.ExecuteAsync(new WorkerInvocation(
            "offline-probe",
            anki,
            StrictJson.CloneElement(new { phase = "probe", configurationJson = configJson, selectedRoute = "canary", idempotencyKey = new string('A', 64) }),
            TimeSpan.FromSeconds(5)), CancellationToken.None);
        Require(probe.ExternalWriteCount == 0 && probe.ExternalStateKnown, "Offline process probe was not read-only and known.");

        var answer = await runtime.ExecuteAsync(new WorkerInvocation(
            "offline-answer",
            content,
            StrictJson.CloneElement(new { phase = "initial", prompt = "Why?", idempotencyKey = new string('B', 64) }),
            TimeSpan.FromSeconds(5)), CancellationToken.None);
        Require(answer.Payload.GetProperty("answerText").GetString() == "Offline exact answer.", "Offline content answer was not projected.");

        var cards = await runtime.ExecuteAsync(new WorkerInvocation(
            "offline-cards",
            content,
            StrictJson.CloneElement(new { phase = "make-cards", question = "Why?", answer = "Because.", idempotencyKey = new string('C', 64) }),
            TimeSpan.FromSeconds(5)), CancellationToken.None);
        Require(cards.Payload.GetProperty("front").GetString() == "Why?" && cards.Payload.GetProperty("back").GetString() == "Because.",
            "Offline content card was not projected.");

        var beforeRejected = invoker.InvocationCount;
        try
        {
            _ = await runtime.ExecuteAsync(new WorkerInvocation(
                "offline-export-rejected",
                anki,
                StrictJson.CloneElement(new { phase = "export", card = new { front = "Q", back = "A" }, configurationJson = configJson, idempotencyKey = new string('D', 64) }),
                TimeSpan.FromSeconds(5)), CancellationToken.None);
            throw new InvalidOperationException("Missing Anki authorization was accepted.");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("authorization", StringComparison.OrdinalIgnoreCase))
        {
        }
        Require(invoker.InvocationCount == beforeRejected, "Missing authorization reached the worker process boundary.");

        var export = await runtime.ExecuteAsync(new WorkerInvocation(
            "offline-export",
            anki,
            StrictJson.CloneElement(new
            {
                phase = "export", card = new { front = "Q", back = "A" }, configurationJson = configJson,
                sideEffectAuthorization = new { authorizationId = "offline-canary", mode = "explicit-user-action", scope = "isolated-canary-deck" },
                idempotencyKey = new string('E', 64)
            }),
            TimeSpan.FromSeconds(5)), CancellationToken.None);
        Require(export.ExternalWriteCount == 1 && export.Payload.GetProperty("exactReadback").GetBoolean(), "Offline Anki export lacked one-write exact-readback evidence.");
        Require(invoker.ValidatedTaskManifests == invoker.InvocationCount, "Not every offline process invocation had a validated task manifest.");
        Require(invoker.ExternalConnections == 0, "Offline conformance unexpectedly used an external connection.");
    }
    finally
    {
        DeleteTemporaryDirectory(packageRoot);
    }
}

async Task VerifyWorkerProcessUnknownStateAsync()
{
    var bindingPath = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var catalog = await AdmittedCapabilityCatalog.LoadAsync(repositoryRoot, bindingPath, CancellationToken.None);
    const string configJson = "{\"schema_version\":\"anki-export-config.v1\",\"provision_managed\":false}";
    foreach (var mode in new[] { UnknownProcessMode.ExitAfterDispatch, UnknownProcessMode.NonCompletedResult })
    {
        var root = NewTemporaryDirectory("worker-process-unknown-" + mode);
        try
        {
            var runtime = await WorkerProcessRuntime.CreateAsync(catalog, repositoryRoot, root, CancellationToken.None, new UnknownWorkerProcessInvoker(mode));
            try
            {
                _ = await runtime.ExecuteAsync(new WorkerInvocation(
                    "offline-unknown-" + mode,
                    runtime.GetAdmittedBinding("export.anki"),
                    StrictJson.CloneElement(new
                    {
                        phase = "export", card = new { front = "Q", back = "A" }, configurationJson = configJson,
                        sideEffectAuthorization = new { authorizationId = "offline-unknown", mode = "explicit-user-action", scope = "isolated-canary-deck" },
                        idempotencyKey = mode == UnknownProcessMode.ExitAfterDispatch ? new string('F', 64) : new string('1', 64)
                    }),
                    TimeSpan.FromSeconds(5)), CancellationToken.None);
                throw new InvalidOperationException($"{mode} was not classified as unknown external state.");
            }
            catch (WorkerExternalStateUnknownException)
            {
            }
            var metrics = runtime.GetMetrics();
            Require(metrics.AnkiExportAttempts == 1 && metrics.AnkiExternalWriteCount == 0 && metrics.AnkiUnknownWriteAttempts == 1,
                $"{mode} confused confirmed writes with unknown write attempts.");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    var appRoot = NewTemporaryDirectory("worker-unknown-projection");
    try
    {
        var eventStore = new FileEventStore(Path.Combine(appRoot, "events"));
        var artifactStore = new FileArtifactStore(Path.Combine(appRoot, "artifacts"), "phase2-unknown");
        var commandIndex = new FileCommandIndex(Path.Combine(appRoot, "command-index"));
        var application = new KernelApplication(
            catalog,
            new OperationRegistry(new IOperationHandler[] { new QuestionWorkflowOperation("question.initial") }),
            new AlwaysUnknownWorkerRuntime(),
            eventStore,
            commandIndex,
            artifactStore);
        var question = await PublishFixtureTextAsync(artifactStore, "unknown-question", "question-text", "3.0.0", "kc.question/1", "Q?");
        var command = QuestionCommand("unknown-command", "unknown-operation", "question.initial", "kc.question-initial-command/2", "unknown-session", "question", question);
        _ = await application.HandleCommandAsync(command, CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !eventStore.ReadStream(command.OperationId).Any(item => item.EventType == "operation.unknown-external-state"))
        {
            await Task.Delay(25);
        }
        Require(eventStore.ReadStream(command.OperationId)[^1].EventType == "operation.unknown-external-state", "Typed worker unknown state was projected as an ordinary failure.");
        Require(eventStore.ReadStream("question-session-unknown-session")[^1].EventType == "question.session-unknown-external-state", "Typed worker unknown state did not lock the question session.");
        while (DateTimeOffset.UtcNow < deadline && commandIndex.TryReadReceipt(command.IdempotencyKey) is null)
        {
            await Task.Delay(25);
        }
        Require(commandIndex.TryReadReceipt(command.IdempotencyKey) is not null, "Unknown-state command receipt was not durably completed.");
    }
    finally
    {
        DeleteTemporaryDirectory(appRoot);
    }
}

async Task<SliceEvidence> VerifyVerticalSliceAsync()
{
    var dotnet = ResolveDotnetSdk();
    var hostDll = Path.Combine(repositoryRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Host", "bin", "Release", "net8.0", "KnowledgeClipper.Core.V3.Host.dll");
    Require(File.Exists(hostDll), "Core v3 Host has not been built.");
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var pipeName = "KnowledgeClipper.Core.V3.Phase2." + Guid.NewGuid().ToString("N");
    var dataRoot = NewTemporaryDirectory("phase2-slice");
    using var process = StartHost(dotnet, hostDll, binding, pipeName, dataRoot);
    try
    {
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ready = await process.StandardOutput.ReadLineAsync(readyTimeout.Token);
        if (ready != $"CORE_V3_READY {pipeName}")
        {
            var error = process.HasExited ? await process.StandardError.ReadToEndAsync() : "Host remained running without the expected readiness line.";
            throw new InvalidOperationException($"Phase2 Host did not report readiness. Actual='{ready}'. {error}");
        }

        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)));
        var capabilities = await client.GetCapabilitiesAsync();
        foreach (var operation in new[] { "anki.probe", "question.initial", "question.follow-up", "question.make-cards" })
        {
            Require(capabilities.Operations.Single(item => item.Operation == operation).Available, $"{operation} was not admitted.");
        }

        const string rawQuestion = "Why does the derivative of x^2 equal 2x?";
        const string rawFollowUp = "Explain with a geometric intuition.";
        var config = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("phase2-anki-config", ArtifactPurpose.AnkiConfiguration, "{\"endpoint\":\"fake://anki\"}"));
        var configReplay = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("phase2-anki-config", ArtifactPurpose.AnkiConfiguration, "{\"endpoint\":\"fake://anki\"}"));
        Require(config.Equals(configReplay), "Artifact publish replay changed the typed artifact identity.");
        var probeSubmission = await client.AnkiProbeAsync(new AnkiProbeRequest("phase2-anki-probe", config));
        var probe = await client.WaitOperationProjectionAsync(probeSubmission.OperationId);
        Require(probe.State == OperationProjectionState.Succeeded && probe.AnkiConnectionStatusArtifact is not null, "Anki probe did not publish one typed status artifact.");
        using var probeJson = JsonDocument.Parse((await client.ReadArtifactAsync(probe.AnkiConnectionStatusArtifact!)).Content);
        Require(probeJson.RootElement.GetProperty("externalWriteCount").GetInt32() == 0, "Anki probe wrote externally.");

        var question = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("phase2-question", ArtifactPurpose.Question, rawQuestion));
        var initialSubmission = await client.QuestionInitialAsync(new QuestionInitialRequest("phase2-session-main", question, Array.Empty<TypedArtifactHandle>()));
        var initial = await client.WaitQuestionProjectionAsync(initialSubmission.OperationId);
        Require(initial.State == QuestionProjectionState.WaitingInput && initial.AnswerArtifact is not null && initial.CardBundleArtifact is null && initial.DestinationEvidenceArtifact is null, "Initial answer outputs were not typed exactly.");
        var initialAnswer = Encoding.UTF8.GetString((await client.ReadArtifactAsync(initial.AnswerArtifact!)).Content);

        var followUp = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("phase2-follow-up", ArtifactPurpose.QuestionFollowUp, rawFollowUp));
        var followUpSubmission = await client.QuestionFollowUpAsync(new QuestionFollowUpRequest("phase2-follow-up-command", initialSubmission.SessionId, followUp));
        var followUpResult = await client.WaitQuestionProjectionAsync(followUpSubmission.OperationId);
        Require(followUpResult.State == QuestionProjectionState.WaitingInput && followUpResult.AnswerArtifact is not null && followUpResult.CardBundleArtifact is null && followUpResult.DestinationEvidenceArtifact is null, "Follow-up outputs were not typed exactly.");
        var finalAnswer = Encoding.UTF8.GetString((await client.ReadArtifactAsync(followUpResult.AnswerArtifact!)).Content);

        var makeRequest = new QuestionMakeCardsRequest(
            "phase2-make-cards",
            initialSubmission.SessionId,
            followUpResult.AnswerArtifact!,
            config,
            new AnkiWriteAuthorization("phase2-make-cards-authorization"));
        var makeSubmission = await client.QuestionMakeCardsAsync(makeRequest);
        var make = await client.WaitQuestionProjectionAsync(makeSubmission.OperationId);
        Require(make.State == QuestionProjectionState.Succeeded && make.AnswerArtifact is null && make.CardBundleArtifact is not null && make.DestinationEvidenceArtifact is not null, $"Make-cards outputs were not typed exactly. State={make.State}; Notice={make.Notice}");
        var cardJson = Encoding.UTF8.GetString((await client.ReadArtifactAsync(make.CardBundleArtifact!)).Content);
        var exportEvidenceJson = Encoding.UTF8.GetString((await client.ReadArtifactAsync(make.DestinationEvidenceArtifact!)).Content);
        using (var evidenceDocument = JsonDocument.Parse(exportEvidenceJson))
        {
            Require(evidenceDocument.RootElement.GetProperty("exactReadback").GetBoolean(), "Anki evidence lacks exact readback.");
            Require(evidenceDocument.RootElement.GetProperty("externalWriteCount").GetInt32() == 1, "First Anki export was not exactly one bounded fake write.");
        }

        var metricsBeforeReplay = await ReadMetricsAsync(client);
        Require(metricsBeforeReplay == new WorkerRuntimeMetrics(3, 1, 1, 1, 1, 0, 0, 0, "authority-bound-independent-fake-proxies"), "Main slice metrics are not exact.");
        var replaySubmission = await client.QuestionMakeCardsAsync(makeRequest);
        var replay = await client.WaitQuestionProjectionAsync(replaySubmission.OperationId);
        Require(replay.CardBundleArtifact?.Token == make.CardBundleArtifact!.Token && replay.DestinationEvidenceArtifact?.Token == make.DestinationEvidenceArtifact!.Token, "Make-cards replay changed typed artifact identities.");
        var metricsAfterReplay = await ReadMetricsAsync(client);
        Require(metricsAfterReplay == metricsBeforeReplay, "Exact request replay caused a worker attempt or write.");

        var concurrentQuestion = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("phase2-concurrent-question", ArtifactPurpose.Question, "What is a limit?"));
        Require(!concurrentQuestion.Equals(question), "Different question commands collided on artifact identity.");
        var concurrentInitialSubmission = await client.QuestionInitialAsync(new QuestionInitialRequest("phase2-session-concurrent", concurrentQuestion, Array.Empty<TypedArtifactHandle>()));
        var concurrentInitial = await client.WaitQuestionProjectionAsync(concurrentInitialSubmission.OperationId);
        Require(concurrentInitial.State == QuestionProjectionState.WaitingInput && concurrentInitial.AnswerArtifact is not null, "Concurrent fixture initial answer failed.");
        Require(!concurrentInitial.AnswerArtifact!.Equals(initial.AnswerArtifact), "Distinct sessions collided on derived answer artifact identity.");
        var concurrentResults = await Task.WhenAll(
            TryMakeCardsAsync(client, new QuestionMakeCardsRequest("phase2-concurrent-left", concurrentInitialSubmission.SessionId, concurrentInitial.AnswerArtifact!, config, new AnkiWriteAuthorization("phase2-concurrent-left-authorization"))),
            TryMakeCardsAsync(client, new QuestionMakeCardsRequest("phase2-concurrent-right", concurrentInitialSubmission.SessionId, concurrentInitial.AnswerArtifact!, config, new AnkiWriteAuthorization("phase2-concurrent-right-authorization"))));
        Require(concurrentResults.Count(item => item) == 1, "Concurrent make-cards did not admit exactly one writer.");
        var metricsAfterConcurrency = await ReadMetricsAsync(client);
        var expectedAfterConcurrency = metricsBeforeReplay with
        {
            ContentAttempts = metricsBeforeReplay.ContentAttempts + 2,
            AnkiExportAttempts = metricsBeforeReplay.AnkiExportAttempts + 1,
            AnkiExternalWriteCount = metricsBeforeReplay.AnkiExternalWriteCount + 1,
            AnkiReadbackCount = metricsBeforeReplay.AnkiReadbackCount + 1
        };
        Require(metricsAfterConcurrency == expectedAfterConcurrency, "Concurrent session produced duplicate worker attempts or writes.");

        return new SliceEvidence(
            capabilities.CatalogId,
            capabilities.CatalogSha256,
            rawQuestion,
            initialAnswer,
            rawFollowUp,
            finalAnswer,
            JsonDocument.Parse(cardJson).RootElement.Clone(),
            JsonDocument.Parse(exportEvidenceJson).RootElement.Clone(),
            metricsBeforeReplay,
            metricsAfterReplay,
            metricsAfterConcurrency,
            ReplayWorkerAttemptDelta: 0,
            ReplayExternalWriteDelta: 0,
            ConcurrentSuccessfulWriters: concurrentResults.Count(item => item));
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

async Task VerifyAdmissionDriftAsync(string mode)
{
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var catalog = await AdmittedCapabilityCatalog.LoadAsync(repositoryRoot, binding, CancellationToken.None);
    var operations = catalog.Document.Operations.Select(operation =>
    {
        if (operation.WorkerBinding.WorkerId != "export.anki") return operation;
        var authority = operation.WorkerBinding.Authority!;
        authority = mode switch
        {
            "hash" => authority with { EntrypointSha256 = new string('0', 64) },
            "version" => authority with { ExactWorkerVersion = "9.9.9" },
            "contract" => authority with { ContractVersion = "9.9.9" },
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var versionRange = mode == "version" ? "=9.9.9" : operation.WorkerBinding.VersionRange;
        return operation with { WorkerBinding = operation.WorkerBinding with { VersionRange = versionRange, Authority = authority } };
    }).ToArray();
    var drifted = AdmittedCapabilityCatalog.CreateForTesting(catalog.Document with { Operations = operations });
    try
    {
        _ = await AuthorityBoundFakeWorkerRuntime.CreateAsync(drifted, repositoryRoot, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException($"Authority admission accepted {mode} drift.");
}

Task VerifyNamedProjectionOutputsAsync()
{
    var questionProperties = typeof(QuestionProjectionSnapshot).GetProperties().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
    Require(questionProperties.Contains("AnswerArtifact") && questionProperties.Contains("CardBundleArtifact") && questionProperties.Contains("DestinationEvidenceArtifact"), "Question projection lacks named domain artifacts.");
    Require(!questionProperties.Contains("Artifacts"), "Question projection still exposes an ambiguous artifact list.");
    var operationProperties = typeof(OperationProjectionSnapshot).GetProperties().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
    Require(operationProperties.Contains("AnkiConnectionStatusArtifact"), "Operation projection lacks typed Anki status output.");
    Require(!operationProperties.Contains("Artifacts"), "Operation projection still exposes an ambiguous artifact list.");
    Require(!typeof(OperationSubmission).GetProperties().Any(item => item.Name == "Artifacts"), "Operation submission exposes business artifacts before typed projection.");
    Require(!typeof(QuestionOperationSubmission).GetProperties().Any(item => item.Name == "Artifacts"), "Question submission exposes business artifacts before typed projection.");
    return Task.CompletedTask;
}

async Task VerifyProductionFailClosedAsync()
{
    var dotnet = ResolveDotnetSdk();
    var hostDll = Path.Combine(repositoryRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Host", "bin", "Release", "net8.0", "KnowledgeClipper.Core.V3.Host.dll");
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var dataRoot = NewTemporaryDirectory("phase2-production-fail-closed");
    using var process = StartHost(dotnet, hostDll, binding, "KnowledgeClipper.Core.V3.Production." + Guid.NewGuid().ToString("N"), dataRoot, testOnly: false);
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        var stderr = await process.StandardError.ReadToEndAsync();
        Require(process.ExitCode != 0, "Production Host started the test-only fake runtime.");
        Require(stderr.Contains("does not accept test worker runtime switches", StringComparison.Ordinal), "Production fake-runtime rejection reason was not explicit.");
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        DeleteTemporaryDirectory(dataRoot);
    }
}

async Task VerifySlowWorkerAndReconnectAsync()
{
    var dotnet = ResolveDotnetSdk();
    var hostDll = Path.Combine(repositoryRoot, "core-v3", "src", "KnowledgeClipper.Core.V3.Host", "bin", "Release", "net8.0", "KnowledgeClipper.Core.V3.Host.dll");
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var pipeName = "KnowledgeClipper.Core.V3.Slow." + Guid.NewGuid().ToString("N");
    var dataRoot = NewTemporaryDirectory("phase2-slow-reconnect");
    Process? process = null;
    Process? restarted = null;
    try
    {
        process = StartHost(dotnet, hostDll, binding, pipeName, dataRoot, fakeDelayMilliseconds: 2500);
        await WaitReadyAsync(process, pipeName);
        var fastClient = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100)));
        var question = await fastClient.PublishTextArtifactAsync(new PublishTextArtifactRequest("slow-question", ArtifactPurpose.Question, "Why is a slow task still observable?"));
        var stopwatch = Stopwatch.StartNew();
        var submission = await fastClient.QuestionInitialAsync(new QuestionInitialRequest("slow-session", question, Array.Empty<TypedArtifactHandle>()));
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Slow worker blocked command acceptance on the Named Pipe request.");
        var completed = await fastClient.WaitQuestionProjectionAsync(submission.OperationId);
        Require(completed.State == QuestionProjectionState.WaitingInput && completed.AnswerArtifact is not null, "Slow worker did not complete through typed projection observation.");

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
        process = null;

        var reconnectingClient = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)));
        var reconnectObservation = reconnectingClient.WaitQuestionProjectionAsync(submission.OperationId);
        await Task.Delay(750);
        restarted = StartHost(dotnet, hostDll, binding, pipeName, dataRoot);
        await WaitReadyAsync(restarted, pipeName);
        var reobserved = await reconnectObservation;
        Require(reobserved.State == QuestionProjectionState.WaitingInput && reobserved.AnswerArtifact is not null, "Observation did not reconnect to the existing terminal operation.");
    }
    finally
    {
        foreach (var candidate in new[] { process, restarted })
        {
            if (candidate is not null)
            {
                if (!candidate.HasExited) candidate.Kill(entireProcessTree: true);
                candidate.Dispose();
            }
        }
        DeleteTemporaryDirectory(dataRoot);
    }
}

async Task VerifyQuestionSessionCrashRecoveryAsync()
{
    var binding = Path.Combine(repositoryRoot, "contracts", "core", "v3", "capabilities", "authority-bound-capability-catalog.phase2.ref.json");
    var catalog = await AdmittedCapabilityCatalog.LoadAsync(repositoryRoot, binding, CancellationToken.None);
    await VerifyReadOnlySessionRecoveryAsync(catalog);
    await VerifyExternalWriteUnknownRecoveryAsync(catalog);
}

async Task VerifyReadOnlySessionRecoveryAsync(AdmittedCapabilityCatalog catalog)
{
    var root = NewTemporaryDirectory("phase2-session-readonly-recovery");
    try
    {
        const string sessionId = "recovery-readonly-session";
        var answer = PlaceholderReference("prior-answer", "question-answer", "1.0.0");
        var question = PlaceholderReference("prior-question", "question-text", "3.0.0");
        var crashed = QuestionCommand("crashed-followup", "question-follow-up-crashed", "question.follow-up", "kc.question-follow-up-command/1", sessionId, "followUp", PlaceholderReference("crashed-followup-input", "question-follow-up", "3.0.0"));
        var eventStore = new FileEventStore(Path.Combine(root, "events"));
        eventStore.Append("question-session-" + sessionId, 0, new[]
        {
            TestEvent("question-session-" + sessionId, "question.answer-recorded", "prior", "question.initial", new[] { question, answer }),
            TestEvent("question-session-" + sessionId, "question.session-command-claimed", crashed.CommandId, crashed.Operation, crashed.Inputs),
            TestEvent("question-session-" + sessionId, "question.worker-dispatch-started", crashed.CommandId, crashed.Operation, Array.Empty<ArtifactReference>())
        });
        eventStore.Append(crashed.OperationId, 0, new[]
        {
            TestEvent(crashed.OperationId, "command.accepted", crashed.CommandId, crashed.Operation, crashed.Inputs),
            TestEvent(crashed.OperationId, "operation.started", crashed.CommandId, crashed.Operation, crashed.Inputs)
        });
        var abandoned = new FileCommandIndex(Path.Combine(root, "command-index"), "dead-owner", int.MaxValue);
        Require(abandoned.TryClaim(crashed, "read-only-external").Acquired, "Could not create read-only abandoned claim.");
        var runtime = new RecoveryWorkerRuntime();
        var artifactStore = new FileArtifactStore(Path.Combine(root, "artifacts"), "phase2-recovery");
        var application = CreateRecoveryApplication(catalog, root, runtime, eventStore, artifactStore);
        application.RecoverIncompleteCommands();
        Require(eventStore.ReadStream(crashed.OperationId)[^1].EventType == "operation.failed", "Read-only crash did not become an explicit operation failure.");
        Require(eventStore.ReadStream("question-session-" + sessionId)[^1].EventType == "question.session-command-aborted", "Read-only dispatched crash did not reopen the session safely.");

        var followUp = await PublishFixtureTextAsync(artifactStore, "recovery-new-followup", "question-follow-up", "3.0.0", "kc.question-follow-up/1", "Try again safely.");
        var retry = QuestionCommand("retry-followup", "question-follow-up-retry", "question.follow-up", "kc.question-follow-up-command/1", sessionId, "followUp", followUp);
        var result = await new QuestionWorkflowOperation("question.follow-up").ExecuteAsync(
            retry,
            new OperationContext(catalog.GetRequired("question.follow-up"), runtime, artifactStore, eventStore),
            CancellationToken.None);
        Require(result.Artifacts.Count == 1 && runtime.Attempts == 1, "A new legal read-only command could not continue after recovery.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyExternalWriteUnknownRecoveryAsync(AdmittedCapabilityCatalog catalog)
{
    var root = NewTemporaryDirectory("phase2-session-write-unknown");
    try
    {
        const string sessionId = "recovery-write-session";
        var answer = PlaceholderReference("write-answer", "question-answer", "1.0.0");
        var question = PlaceholderReference("write-question", "question-text", "3.0.0");
        var crashed = QuestionCommand("crashed-make", "question-make-crashed", "question.make-cards", "kc.question-make-cards-command/1", sessionId, "answer", answer);
        var eventStore = new FileEventStore(Path.Combine(root, "events"));
        eventStore.Append("question-session-" + sessionId, 0, new[]
        {
            TestEvent("question-session-" + sessionId, "question.answer-recorded", "prior", "question.initial", new[] { question, answer }),
            TestEvent("question-session-" + sessionId, "question.session-command-claimed", crashed.CommandId, crashed.Operation, crashed.Inputs),
            TestEvent("question-session-" + sessionId, "question.worker-dispatch-started", crashed.CommandId, crashed.Operation, Array.Empty<ArtifactReference>())
        });
        eventStore.Append(crashed.OperationId, 0, new[]
        {
            TestEvent(crashed.OperationId, "command.accepted", crashed.CommandId, crashed.Operation, crashed.Inputs),
            TestEvent(crashed.OperationId, "operation.started", crashed.CommandId, crashed.Operation, crashed.Inputs)
        });
        var abandoned = new FileCommandIndex(Path.Combine(root, "command-index"), "dead-owner", int.MaxValue);
        Require(abandoned.TryClaim(crashed, "external-write").Acquired, "Could not create external-write abandoned claim.");
        var runtime = new RecoveryWorkerRuntime();
        var artifactStore = new FileArtifactStore(Path.Combine(root, "artifacts"), "phase2-recovery-write");
        var application = CreateRecoveryApplication(catalog, root, runtime, eventStore, artifactStore);
        application.RecoverIncompleteCommands();
        Require(eventStore.ReadStream(crashed.OperationId)[^1].EventType == "operation.unknown-external-state", "External-write crash was not preserved as unknown.");
        Require(eventStore.ReadStream("question-session-" + sessionId)[^1].EventType == "question.session-unknown-external-state", "External-write session was incorrectly reopened after dispatch.");
        var retry = QuestionCommand("retry-make", "question-make-retry", "question.make-cards", "kc.question-make-cards-command/1", sessionId, "answer", answer);
        try
        {
            _ = await new QuestionWorkflowOperation("question.make-cards").ExecuteAsync(
                retry,
                new OperationContext(catalog.GetRequired("question.make-cards"), runtime, artifactStore, eventStore),
                CancellationToken.None);
            throw new InvalidOperationException("External-write unknown state allowed an automatic retry.");
        }
        catch (InvalidDataException)
        {
        }
        Require(runtime.Attempts == 0, "External-write unknown recovery re-invoked a worker.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

static KernelApplication CreateRecoveryApplication(
    AdmittedCapabilityCatalog catalog,
    string root,
    IWorkerRuntime runtime,
    FileEventStore eventStore,
    FileArtifactStore artifactStore)
{
    return new KernelApplication(
        catalog,
        new OperationRegistry(new IOperationHandler[]
        {
            new QuestionWorkflowOperation("question.initial"),
            new QuestionWorkflowOperation("question.follow-up"),
            new QuestionWorkflowOperation("question.make-cards")
        }),
        runtime,
        eventStore,
        new FileCommandIndex(Path.Combine(root, "command-index")),
        artifactStore);
}

static CommandEnvelope QuestionCommand(
    string commandId,
    string operationId,
    string operation,
    string bodySchema,
    string sessionId,
    string referenceName,
    ArtifactReference reference)
{
    var body = new Dictionary<string, object>
    {
        ["sessionId"] = sessionId,
        [referenceName] = reference
    };
    return new CommandEnvelope(
        KernelSchemas.Command,
        commandId,
        operationId,
        operation,
        new KernelSubject("phase2-recovery-test", sessionId),
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + commandId))),
        ExpectedRevision: null,
        Authorization: null,
        new[] { reference },
        new ContractBody(bodySchema, JsonSerializer.SerializeToElement(body, JsonOptionsHolder.Options)));
}

static KernelEvent TestEvent(
    string streamId,
    string eventType,
    string commandId,
    string operation,
    IReadOnlyList<ArtifactReference> artifacts)
{
    return new KernelEvent(
        KernelSchemas.Event,
        "evt-" + Guid.NewGuid().ToString("N"),
        streamId,
        Sequence: 0,
        eventType,
        EventVersion: 1,
        DateTimeOffset.UtcNow,
        commandId,
        commandId,
        operation,
        operation == "question.make-cards" ? "external-write" : "read-only-external",
        JsonSerializer.SerializeToElement(new { fixture = true }, JsonOptionsHolder.Options),
        artifacts);
}

static ArtifactReference PlaceholderReference(string id, string kind, string contract)
    => new(KernelSchemas.ArtifactReference, "artifact-" + id, kind, contract, new ArtifactDigest("sha256", new string('A', 64)), "phase2-recovery");

static async Task<ArtifactReference> PublishFixtureTextAsync(
    FileArtifactStore store,
    string id,
    string kind,
    string contract,
    string schema,
    string text)
{
    var content = Encoding.UTF8.GetBytes(text);
    var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
    var envelope = new ArtifactEnvelope(
        KernelSchemas.Artifact,
        "artifact-" + id,
        kind,
        contract,
        store.StoreId,
        new[] { new ArtifactRepresentation("primary", "content.txt", "text/plain; charset=utf-8", schema, new ArtifactDigest("sha256", digest), content.Length, new[] { "primary" }) },
        "primary",
        new ArtifactProvenance("phase2-recovery-test", Array.Empty<ArtifactReference>()),
        new ArtifactPolicies("synthetic", "test-only"));
    return await store.PublishAsync(envelope, new Dictionary<string, ReadOnlyMemory<byte>> { ["primary"] = content }, CancellationToken.None);
}

static async Task WaitReadyAsync(Process process, string pipeName)
{
    using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var ready = await process.StandardOutput.ReadLineAsync(readyTimeout.Token);
    if (ready != $"CORE_V3_READY {pipeName}")
    {
        var error = process.HasExited ? await process.StandardError.ReadToEndAsync() : "Host remained running without the expected readiness line.";
        throw new InvalidOperationException($"Phase2 Host did not report readiness. Actual='{ready}'. {error}");
    }
}

static Process StartHost(
    string dotnet,
    string hostDll,
    string binding,
    string pipeName,
    string dataRoot,
    bool testOnly = true,
    int fakeDelayMilliseconds = 0)
{
    if (testOnly)
    {
        hostDll = Path.Combine(
            FindRepositoryRoot(AppContext.BaseDirectory),
            "core-v3", "src", "KnowledgeClipper.Core.V3.TestHost", "bin", "Release", "net8.0", "KnowledgeClipper.Core.V3.TestHost.dll");
    }
    var process = new Process
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
    var arguments = new List<string> { hostDll, "--repository-root", FindRepositoryRoot(AppContext.BaseDirectory), "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName };
    if (testOnly && fakeDelayMilliseconds > 0)
    {
        arguments.Add("--fake-worker-delay-ms");
        arguments.Add(fakeDelayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    else if (!testOnly)
    {
        arguments.Add("--runtime-mode");
        arguments.Add("authority-bound-fake-test-only");
    }
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }
    Require(process.Start(), "Core v3 Phase2 Host did not start.");
    return process;
}

static Process StartProductionHost(string dotnet, string hostDll, string binding, string pipeName, string dataRoot)
{
    var process = new Process
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
    foreach (var argument in new[]
    {
        hostDll,
        "--repository-root", FindRepositoryRoot(AppContext.BaseDirectory),
        "--binding", binding,
        "--data-root", dataRoot,
        "--pipe-name", pipeName
    })
    {
        process.StartInfo.ArgumentList.Add(argument);
    }
    Require(process.Start(), "Core v3 production Host did not start.");
    return process;
}

static async Task<bool> TryMakeCardsAsync(CoreV3Client client, QuestionMakeCardsRequest request)
{
    try
    {
        var submission = await client.QuestionMakeCardsAsync(request);
        var projection = await client.WaitQuestionProjectionAsync(submission.OperationId);
        return projection.State == QuestionProjectionState.Succeeded;
    }
    catch (Exception ex) when (ex is CoreV3OperationException or CoreV3ClientException)
    {
        return false;
    }
}

static async Task<WorkerRuntimeMetrics> ReadMetricsAsync(CoreV3Client client)
{
    var query = new QueryEnvelope(
        KernelSchemas.Query,
        "query-metrics-" + Guid.NewGuid().ToString("N"),
        "vertical-slice.metrics",
        Subject: null,
        "read-your-writes",
        Cursor: null,
        JsonSerializer.SerializeToElement(new { }, JsonOptionsHolder.Options));
    var result = await client.QueryProtocolAsync(query, CancellationToken.None);
    return result.Data.Deserialize<WorkerRuntimeMetrics>(JsonOptionsHolder.Options)
           ?? throw new InvalidDataException("Metrics projection was invalid.");
}

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "contracts", "core", "v2.10", "checkpoints", "v2.10.6-20260902T041800Z", "checkpoint.json"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate the RemNote Quick Clipper repository root.");
}

static string ResolveDotnetSdk()
{
    var configured = Environment.GetEnvironmentVariable("KC_DOTNET_PATH");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
    const string projectSdk = @"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe";
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
    if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to delete outside the test root.");
    if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
}

static string ParseReceiptPath(string[] values, string root)
    => values.Length == 2 && values[0] == "--receipt"
        ? Path.GetFullPath(values[1])
        : Path.Combine(root, "core-v3", "receipts", "phase2-tests.latest.json");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record TestResult(string Name, string Status, long DurationMilliseconds, string? Error);

sealed record SliceEvidence(
    string CatalogId,
    string CatalogSha256,
    string RawQuestion,
    string InitialAnswer,
    string RawFollowUp,
    string FinalAnswer,
    JsonElement FinalCard,
    JsonElement AnkiExportEvidence,
    WorkerRuntimeMetrics MetricsBeforeReplay,
    WorkerRuntimeMetrics MetricsAfterReplay,
    WorkerRuntimeMetrics MetricsAfterConcurrency,
    int ReplayWorkerAttemptDelta,
    int ReplayExternalWriteDelta,
    int ConcurrentSuccessfulWriters);

enum UnknownProcessMode
{
    ExitAfterDispatch,
    NonCompletedResult
}

sealed class UnknownWorkerProcessInvoker : IWorkerProcessInvoker
{
    private readonly UnknownProcessMode _mode;

    public UnknownWorkerProcessInvoker(UnknownProcessMode mode) => _mode = mode;

    public async Task<WorkerProcessInvocationResult> InvokeAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_mode == UnknownProcessMode.ExitAfterDispatch)
        {
            return new WorkerProcessInvocationResult(19, false, false, Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "stderr.log"));
        }
        var result = new LegacyWorkerResult(
            "1.0.0", package.Request.RequestId, package.Request.OperationId, package.Request.TaskId,
            package.Request.RunId, package.Request.StageId, package.Request.AttemptId,
            new LegacyWorkerIdentity(worker.WorkerId, worker.WorkerVersion), worker.ContractVersion,
            package.Request.IdempotencyKey, "unknown_external_state", Array.Empty<LegacyArtifactReference>(),
            new LegacyWorkerError("UNKNOWN_EXTERNAL_STATE", "external", false, "stderr.log", "Injected unknown state.", "worker"),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), Array.Empty<string>());
        Directory.CreateDirectory(Path.GetDirectoryName(package.ResultPath)!);
        await File.WriteAllBytesAsync(package.ResultPath, JsonSerializer.SerializeToUtf8Bytes(result, JsonOptionsHolder.Options), cancellationToken);
        return new WorkerProcessInvocationResult(0, false, false, Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "stderr.log"));
    }
}

sealed class AlwaysUnknownWorkerRuntime : IWorkerRuntime
{
    public Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
        => throw new WorkerExternalStateUnknownException("injected_unknown", "Injected unknown external state after dispatch.");

    public WorkerRuntimeMetrics GetMetrics() => new(0, 0, 0, 0, 0, 0, 0, 0, "unknown-test");

    public WorkerBinding GetAdmittedBinding(string workerId) => throw new InvalidOperationException();
}

sealed class OfflineConformanceWorkerProcessInvoker : IWorkerProcessInvoker
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);
    private int _invocationCount;
    private int _validatedTaskManifests;

    public int InvocationCount => Volatile.Read(ref _invocationCount);
    public int ValidatedTaskManifests => Volatile.Read(ref _validatedTaskManifests);
    public int ExternalConnections => 0;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public async Task<WorkerProcessInvocationResult> InvokeAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _invocationCount);
        var taskManifestPath = Path.Combine(package.JobRoot, "task.manifest.v1.json");
        using var taskDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(taskManifestPath, cancellationToken));
        var task = taskDocument.RootElement;
        Require(task.GetProperty("taskId").GetString() == package.Request.TaskId, "Offline task manifest identity mismatch.");
        Require(task.GetProperty("executionProfile").GetString() == "cards-anki", "Offline task manifest profile mismatch.");
        Require(task.GetProperty("inputRefs").GetArrayLength() > 0 && task.GetProperty("requestedOutputs").GetArrayLength() > 0,
            "Offline task manifest lacks roots or outputs.");
        Interlocked.Increment(ref _validatedTaskManifests);

        var outputs = package.Request.WorkerId == "content.deepseek"
            ? await PublishContentAsync(worker, package, cancellationToken)
            : package.Request.Operation == "probe"
                ? new[] { await PublishProbeAsync(worker, package, cancellationToken) }
                : new[] { await PublishExportAsync(worker, package, task, cancellationToken) };
        var result = new LegacyWorkerResult(
            "1.0.0", package.Request.RequestId, package.Request.OperationId, package.Request.TaskId,
            package.Request.RunId, package.Request.StageId, package.Request.AttemptId,
            new LegacyWorkerIdentity(worker.WorkerId, worker.WorkerVersion), worker.ContractVersion,
            package.Request.IdempotencyKey, "completed", outputs, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), Array.Empty<string>(),
            JsonSerializer.SerializeToElement(new { moduleStatus = "SUCCEEDED" }, JsonOptionsHolder.Options),
            package.Request.Operation == "export"
                ? JsonSerializer.SerializeToElement(new[] { new { kind = "anki.export", state = "verified" } }, JsonOptionsHolder.Options)
                : JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonOptionsHolder.Options));
        Directory.CreateDirectory(Path.GetDirectoryName(package.ResultPath)!);
        await File.WriteAllBytesAsync(package.ResultPath, JsonSerializer.SerializeToUtf8Bytes(result, JsonOptionsHolder.Options), cancellationToken);
        return new WorkerProcessInvocationResult(0, false, false, Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "stderr.log"));
    }

    private static async Task<LegacyArtifactReference[]> PublishContentAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        CancellationToken cancellationToken)
    {
        if (package.Request.Operation == "question.answer")
        {
            var payload = new
            {
                schema_version = "question-answer/1.0.0",
                turns = new[] { new { answer_text = "Offline exact answer." } }
            };
            return new[] { await PublishJsonAsync(worker, package, "question.answer", "question-answer.json", payload, cancellationToken) };
        }
        var card = new
        {
            schema_version = "card-bundle/1.0.0",
            cards = new[]
            {
                new
                {
                    front_blocks = new[] { new { text = "Why?" } },
                    back_blocks = new[] { new { text = "Because." } }
                }
            }
        };
        return new[] { await PublishJsonAsync(worker, package, "card-bundle", "card-bundle.json", card, cancellationToken) };
    }

    private static Task<LegacyArtifactReference> PublishProbeAsync(AdmittedAuthorityWorker worker, LegacyAttemptPackage package, CancellationToken cancellationToken)
        => PublishJsonAsync(worker, package, "anki-connection-status", "connection-status.json", new
        {
            schemaVersion = "anki-connection-status.v1", operation = "probe", status = "ready",
            permission = "granted",
            decks = new[] { new { routeKey = "canary", status = "ready" } },
            models = new[] { new { noteTypeKey = "basic_qa", status = "ready" } },
            externalMutation = false, mutatingActionsObserved = Array.Empty<string>()
        }, cancellationToken);

    private static async Task<LegacyArtifactReference> PublishExportAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        JsonElement task,
        CancellationToken cancellationToken)
    {
        var cardRef = package.Request.InputRefs.Single(item => item.Kind == "card-bundle");
        Require(cardRef.ManifestPath.StartsWith($"runs/{package.Request.RunId}/adapters/anki-export/", StringComparison.Ordinal),
            "Offline derived card path is not canonical.");
        using var cardManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, cardRef.ManifestPath), cancellationToken));
        var producer = cardManifest.RootElement.GetProperty("producer");
        Require(producer.GetProperty("workerId").GetString() == "orchestrator.core-adapter", "Offline derived card producer is not the admitted compatibility identity.");
        var roots = task.GetProperty("inputRefs").EnumerateArray().Select(item => item.GetProperty("artifactRef").GetString()).ToHashSet(StringComparer.Ordinal);
        var parents = cardManifest.RootElement.GetProperty("inputRefs").EnumerateArray().Select(item => item.GetProperty("artifactRef").GetString()).ToArray();
        Require(parents.Length > 0 && parents.All(item => item is not null && roots.Contains(item)), "Offline derived card ancestry is disconnected from task roots.");

        var outputRoot = CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, package.Request.OutputPath);
        Directory.CreateDirectory(outputRoot);
        var operationsPath = package.Request.OutputPath + "/operations.ndjson";
        var itemsPath = package.Request.OutputPath + "/items.ndjson";
        var receiptPath = package.Request.OutputPath + "/receipt.json";
        var operations = string.Join('\n', new[]
        {
            JsonSerializer.Serialize(new { action = "findNotes", status = "SUCCEEDED" }, CompactJson),
            JsonSerializer.Serialize(new { action = "addNote", status = "SUCCEEDED" }, CompactJson),
            JsonSerializer.Serialize(new { action = "notesInfo", status = "SUCCEEDED" }, CompactJson),
            JsonSerializer.Serialize(new { action = "cardsInfo", status = "SUCCEEDED" }, CompactJson)
        }) + "\n";
        var items = JsonSerializer.Serialize(new { card_id = "card-offline", status = "CREATED" }, CompactJson) + "\n";
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new { status = "SUCCEEDED", counts = new { succeeded = 1 } }, JsonOptionsHolder.Options);
        await File.WriteAllTextAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, operationsPath), operations, cancellationToken);
        await File.WriteAllTextAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, itemsPath), items, cancellationToken);
        await File.WriteAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, receiptPath), receipt, cancellationToken);
        return await PublishManifestAsync(worker, package, "anki-export-evidence", receiptPath, new[] { operationsPath, itemsPath, receiptPath }, cancellationToken);
    }

    private static async Task<LegacyArtifactReference> PublishJsonAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        string kind,
        string fileName,
        object payload,
        CancellationToken cancellationToken)
    {
        var path = package.Request.OutputPath + "/" + fileName;
        var full = CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptionsHolder.Options), cancellationToken);
        return await PublishManifestAsync(worker, package, kind, path, new[] { path }, cancellationToken);
    }

    private static async Task<LegacyArtifactReference> PublishManifestAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        string kind,
        string entrypoint,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var artifactId = kind.Replace('.', '-') + "-offline";
        var files = new List<object>();
        foreach (var path in paths)
        {
            var bytes = await File.ReadAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, path), cancellationToken);
            files.Add(new
            {
                path,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes = bytes.LongLength,
                mediaType = path.EndsWith(".ndjson", StringComparison.Ordinal) ? "application/x-ndjson" : "application/json"
            });
        }
        var manifestPath = package.Request.OutputPath + "/" + kind.Replace('.', '-') + ".artifact.v1.json";
        var manifest = new
        {
            schemaVersion = "1.0.0", artifactId, kind, contractVersion = "1.0.0", mediaType = "application/json",
            files, createdAt = DateTimeOffset.UnixEpoch,
            producer = new { package.Request.StageId, workerId = worker.WorkerId, workerVersion = worker.WorkerVersion, package.Request.AttemptId },
            inputRefs = package.Request.InputRefs, dataClass = "personal", metadata = new { entrypoint }
        };
        var bytesManifest = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptionsHolder.Options);
        await File.WriteAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, manifestPath), bytesManifest, cancellationToken);
        return new LegacyArtifactReference(artifactId, kind, "1.0.0", manifestPath, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytesManifest)).ToLowerInvariant());
    }
}

sealed class RecoveryWorkerRuntime : IWorkerRuntime
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _attempts);
        return Task.FromResult(new WorkerExecutionResult(
            "completed",
            JsonSerializer.SerializeToElement(new { status = "waiting_input", answerText = "Recovered retry answer.", externalWriteCount = 0 }, JsonOptionsHolder.Options),
            Array.Empty<ArtifactReference>(),
            0,
            true));
    }

    public WorkerRuntimeMetrics GetMetrics() => new(Attempts, 0, 0, 0, 0, 0, 0, 0, "recovery-test-only");

    public WorkerBinding GetAdmittedBinding(string workerId) => throw new InvalidOperationException("Recovery fixture does not export to Anki.");
}

static class JsonOptionsHolder
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

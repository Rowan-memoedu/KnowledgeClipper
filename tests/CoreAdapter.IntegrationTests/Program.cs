using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

var projectRoot = Path.GetFullPath(args.Length == 1 ? args[0] : throw new ArgumentException("Expected project root."));
var fakeWorker = "tests/FakeWorker/bin/Release/net8.0/RemNoteQuickClipper.FakeWorker.exe";
Assert(File.Exists(Path.Combine(projectRoot, fakeWorker.Replace('/', Path.DirectorySeparatorChar))), "Release fake worker is missing.");

var testRoot = Path.Combine(Path.GetTempPath(), "rqc-ca", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(testRoot);
try
{
    var results = new List<object>();
    results.Add(await RunPositiveMatrixAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunDestinationNeutralRoutingAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunPlaybackControlAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunStageControlFailClosedAndRecoveryAsync(testRoot));
    results.Add(await RunPartialBlockAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunOriginalMediaBoundaryAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunFailedItemsOnlyContinuationAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunImageUnknownExternalNoContinuationAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunOcrProofreadSegmentGuardsAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunOcrUnresolvedAttentionAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunStateAwareRetentionAsync(testRoot));
    results.Add(await RunModuleActionDispatchAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunRemNoteTargetDiscoveryV13Async(projectRoot, testRoot, fakeWorker));
    results.Add(await RunAnkiManagedInitializationV14Async(projectRoot, testRoot, fakeWorker));
    results.Add(await RunNegativeResolverMatrixAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunRemNoteLosslessFailuresAsync(projectRoot, testRoot, fakeWorker));
    results.Add(await RunCompletedOutputGateAsync(projectRoot, testRoot, fakeWorker));
    var authorityPath = Environment.GetEnvironmentVariable("RQC_AUTHORITY_REGISTRY");
    object? authorityBinding = null;
    if (!string.IsNullOrWhiteSpace(authorityPath))
    {
        var fullAuthorityPath = Path.GetFullPath(authorityPath);
        var authority = new RegistryLoader().Load(fullAuthorityPath);
        authorityBinding = new
        {
            path = fullAuthorityPath,
            sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullAuthorityPath))),
            authority.RegistryVersion,
            workerIds = authority.Workers.Select(item => item.WorkerId).ToArray(),
            fakeEntrypointOverlayOnly = true
        };
    }
    var report = new { schemaVersion = "core-adapter-canary.v1", status = "passed", authorityBinding, results };
    var receiptPath = Environment.GetEnvironmentVariable("RQC_CORE_ADAPTER_RECEIPT");
    if (!string.IsNullOrWhiteSpace(receiptPath))
        await AtomicFile.WriteJsonOnceAsync(Path.GetFullPath(receiptPath), report);
    Console.WriteLine(JsonSerializer.Serialize(report, ContractJson.Options));
    return 0;
}
finally
{
    if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
}

static async Task<object> RunPositiveMatrixAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "positive");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registryPath = Path.Combine(root, "registry.json");
    await AtomicFile.WriteJsonOnceAsync(registryPath, Registry(fakeWorker, realRemNote: false));
    var configPath = Path.Combine(root, "resolver.json");
    await AtomicFile.WriteJsonOnceAsync(configPath, ResolverConfig(remNoteEnabled: true));
    var pipeName = "Rqc.CoreAdapter.Positive." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    var host = new OrchestratorHost(jobs, registryPath, projectRoot, 4, configPath);
    var hostTask = host.ServeAsync(Ipc(pipeName), shutdown.Token);
    try
    {
        var clip = await CreateJobAsync(jobs, "task-clip", "clip", "cards-remnote", "text", "ordinary clip", includeAnkiConfig: false);
        var read = await CreateJobAsync(jobs, "task-read", "read", "parity-remnote", "text", "read this selection", includeAnkiConfig: false);
        var screenshot = await CreateJobAsync(jobs, "task-screenshot", "screenshot", "cards-remnote", "ocr", null, includeAnkiConfig: false);
        var anki = await CreateJobAsync(jobs, "task-anki", "clip", "cards-anki", "text", "anki source", includeAnkiConfig: true);
        foreach (var job in new[] { clip, read, screenshot, anki })
        {
            await SubmitAsync(pipeName, job.TaskId);
            var state = await WaitTerminalAsync(pipeName, job.TaskId);
            Assert(state == "succeeded", $"{job.TaskId} ended as {state}: {ReadLatestRunEvents(job.JobRoot)}");
        }

        var readPlan = ReadResolvedPlan(read.JobRoot);
        var readStages = readPlan.GetProperty("stages").EnumerateArray().ToArray();
        Assert(readPlan.GetProperty("schemaVersion").GetString() == "2.0.0", "Read plan was not resolved to pipeline 2.0.0.");
        Assert(readPlan.GetProperty("routing").GetProperty("processingProfile").GetString() == "learning", "Legacy parity profile did not resolve to learning.");
        Assert(readPlan.GetProperty("routing").GetProperty("source").GetString() == "legacy-profile-alias:parity-remnote", "Legacy route was not marked as an alias.");
        Assert(readStages.Any(item => item.GetProperty("stageId").GetString() == "content.understand-and-card"), "Read lost the content branch.");
        Assert(readStages.Any(item => item.GetProperty("stageId").GetString() == "export-remnote"), "Read lost the parity RemNote branch.");
        Assert(readStages.Any(item => item.GetProperty("stageId").GetString() == "speech-synthesize"), "Read lost the speech branch.");
        var speech = readStages.Single(item => item.GetProperty("stageId").GetString() == "speech-synthesize");
        Assert(speech.GetProperty("dependsOn").GetArrayLength() == 0, "Read speech branch is not parallel with the content branch.");
        var readManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(read.JobRoot, "task.manifest.v1.json"))).RootElement;
        Assert(readManifest.GetProperty("executionProfile").GetString() == "parity-remnote", "Read profile was rewritten instead of preserved.");

        var screenshotInputPath = Directory.GetFiles(Path.Combine(screenshot.JobRoot, "runs"), "content-input.v4.json", SearchOption.AllDirectories).Single();
        using var screenshotInput = JsonDocument.Parse(await File.ReadAllBytesAsync(screenshotInputPath));
        var orderedOcrItems = screenshotInput.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert(orderedOcrItems.Select(item => item.GetProperty("hints").GetProperty("role").GetString()).SequenceEqual(new[] { "Question image", "Question image", "Answer image" }) &&
               orderedOcrItems.Select(item => item.GetProperty("hints").GetProperty("index").GetInt32()).SequenceEqual(new[] { 1, 2, 1 }),
            "OCR standard-input v4 did not preserve capture question/answer order.");
        var screenshotPlan = ReadResolvedPlan(screenshot.JobRoot);
        var screenshotImageStage = screenshotPlan.GetProperty("stages").EnumerateArray()
            .Single(item => item.GetProperty("stageId").GetString() == "image-ocr");
        Assert(screenshotImageStage.GetProperty("outputKinds").EnumerateArray().Select(item => item.GetString())
                .SequenceEqual(new[] { "image-ocr.markdown", "image-ocr.structured", "image.processing.provenance" }),
            "OCR mode incorrectly requires original-media outputs.");
        Assert(StageFiles(screenshot.JobRoot, "image-ocr", "request.json").Length == 1,
            "Completed OCR must not produce an adapter-only retry for an original-media binding.");

        var remnoteResult = Directory.GetFiles(Path.Combine(clip.JobRoot, "runs"), "result.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)!)
            .Single(item => item.Worker.Id == "export.remnote");
        Assert(remnoteResult.Worker.Version == "1.0.0" && remnoteResult.Status == "completed", "Fake RemNote worker did not accept the resolved projection.");

        var ankiRequestPath = Directory.GetFiles(Path.Combine(anki.JobRoot, "runs"), "request.json", SearchOption.AllDirectories)
            .Single(path => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("workerId").GetString() == "export.anki");
        var ancestry = await ValidateWithRealAnkiLoaderAsync(projectRoot, anki.JobRoot, ankiRequestPath);
        Assert(ancestry == "ANKI_STRICT_LOAD_OK", "Real Anki loader rejected derived ancestry.");
        return new { name = "fake-worker-submit-profile-matrix", status = "passed", profiles = new[] { "cards-remnote", "parity-remnote", "cards-anki" }, clip = "succeeded", read = "succeeded", screenshot = "succeeded", anki = "succeeded", remNoteWorker = "fake", realAnkiStrictLoader = true, ocrOrder = "question1-question2-answer1" };
    }
    finally
    {
        shutdown.Cancel();
        try { await hostTask; } catch (OperationCanceledException) { }
    }
}

static async Task<object> RunDestinationNeutralRoutingAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "destination-neutral");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registry = Registry(fakeWorker, realRemNote: false);
    var configuration = DesktopIntentResolver.LoadConfiguration(
        await WriteJsonAsync(Path.Combine(root, "resolver.v2.json"), ResolverConfigV2(remNoteEnabled: true)));

    var contentOnly = await CreateJobAsync(jobs, "task-v2-content", "clip", "cards", "text", "content only", false, "2.0.0", Array.Empty<string>());
    var remnote = await CreateJobAsync(jobs, "task-v2-remnote", "clip", "cards", "text", "remnote endpoint", false, "2.0.0", new[] { "remnote" });
    var anki = await CreateJobAsync(jobs, "task-v2-anki", "clip", "cards", "text", "anki endpoint", true, "2.0.0", new[] { "anki" });
    var dual = await CreateJobAsync(jobs, "task-v2-dual", "clip", "cards", "text", "dual endpoint", true, "2.0.0", new[] { "remnote", "anki" });

    foreach (var job in new[] { contentOnly, remnote, anki, dual })
        Assert(await RunDirectAsync(job, registry, configuration, projectRoot) == "succeeded", $"Destination-neutral route failed: {job.TaskId}");

    var contentStages = ReadResolvedPlan(contentOnly.JobRoot).GetProperty("stages").EnumerateArray().Select(item => item.GetProperty("stageId").GetString()).ToArray();
    Assert(contentStages.SequenceEqual(new[] { "content.understand-and-card" }), "Content-only route unexpectedly selected an exporter.");
    var dualPlan = ReadResolvedPlan(dual.JobRoot);
    var dualStages = dualPlan.GetProperty("stages").EnumerateArray().Select(item => item.GetProperty("stageId").GetString()).ToArray();
    Assert(dualStages.Contains("export-remnote") && dualStages.Contains("anki-export"), "Dual destination route did not schedule parallel exporters.");
    Assert(dualPlan.GetProperty("routing").GetProperty("processingProfile").GetString() == "cards" &&
           dualPlan.GetProperty("routing").GetProperty("destinations").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "remnote", "anki" }),
        "Resolved routing snapshot did not preserve the canonical profile/destinations.");

    var learningAnki = await CreateJobAsync(jobs, "task-v2-learning-anki", "clip", "learning", "text", "unsupported route", true, "2.0.0", new[] { "anki" });
    await ExpectCodeAsync("resolver.route_unsupported", () => ResolveAsync(learningAnki, registry, configuration, projectRoot));

    var query = JsonSerializer.Serialize(new CommandHandler(jobs).HandleQuery(JsonSerializer.SerializeToElement(new
    {
        schemaVersion = "1.0.0",
        queryId = "query-neutral",
        type = "task.query_recent",
        limit = 20,
        cursor = (string?)null
    })), ContractJson.Options);
    Assert(!query.Contains("RemNoteQuickClipper", StringComparison.Ordinal) && !query.Contains("parity-remnote", StringComparison.Ordinal) &&
           !query.Contains("cards-remnote", StringComparison.Ordinal) && !query.Contains("cards-anki", StringComparison.Ordinal),
        "Task status projection leaked a legacy product/profile identity.");

    return new
    {
        name = "knowledge-clipper-destination-neutral-routing-v2",
        status = "passed",
        processingProfiles = new[] { "learning", "cards" },
        destinations = new[] { "remnote", "anki" },
        contentOnlyStages = contentStages,
        dualDestinationStages = dualStages,
        legacyAliasAccepted = true,
        statusProjectionLegacyIdentityLeak = false,
        unsupportedLearningAnki = "rejected"
    };
}

static async Task<object> RunPlaybackControlAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "playback-control");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var baseline = Registry(fakeWorker, realRemNote: false);
    var workers = baseline.Workers.Select(worker => worker.WorkerId == "speech.edge"
        ? worker with
        {
            Entrypoint = worker.Entrypoint with
            {
                Args = worker.Entrypoint.Args.Concat(new[] { "--wait-current-run-playback-cancel", "true" }).ToArray()
            }
        }
        : worker).ToArray();
    var registry = baseline with { Workers = workers };
    var registryPath = Path.Combine(root, "registry.json");
    await AtomicFile.WriteJsonOnceAsync(registryPath, registry);
    var configPath = Path.Combine(root, "resolver.json");
    await AtomicFile.WriteJsonOnceAsync(configPath, ResolverConfig(remNoteEnabled: true));
    var pipeName = "Rqc.CoreAdapter.Playback." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    var hostTask = new OrchestratorHost(jobs, registryPath, projectRoot, 4, configPath).ServeAsync(Ipc(pipeName), shutdown.Token);
    try
    {
        var job = await CreateJobAsync(jobs, "task-playback-control", "read", "parity-remnote", "text", "read and keep clipping", false);
        await SubmitAsync(pipeName, job.TaskId);
        await WaitUntilAsync(() =>
        {
            var playbackRequests = StageFiles(job.JobRoot, "speech-playback", "request.json");
            if (playbackRequests.Length != 1 || File.Exists(Path.Combine(Path.GetDirectoryName(playbackRequests[0])!, "result.json"))) return false;
            return StageResults(job.JobRoot, "content.understand-and-card").Any(item => item.Status == "completed") &&
                   StageResults(job.JobRoot, "export-remnote").Any(item => item.Status == "completed");
        }, "Playback did not remain active while the clipping branch completed.");

        var stopResponses = await Task.WhenAll(
            StageControlAsync(pipeName, job.JobRoot, job.TaskId, "stop-a"),
            StageControlAsync(pipeName, job.JobRoot, job.TaskId, "stop-b"));
        var acceptedStop = stopResponses.Single(item => item.GetProperty("accepted").GetBoolean());
        var rejectedStop = stopResponses.Single(item => !item.GetProperty("accepted").GetBoolean());
        Assert(acceptedStop.GetProperty("schemaVersion").GetString() == "2.3.0" && acceptedStop.GetProperty("resolved").ValueKind == JsonValueKind.Object,
            "Public stage-control acceptance did not durably expose the resolved target.");
        Assert(rejectedStop.GetProperty("error").GetProperty("code").GetString() is "control.playback_already_requested" or "control.playback_terminal" or "control.task_terminal",
            "Concurrent playback stop did not fail closed after one command reserved the active attempt.");
        var taskState = await WaitTerminalAsync(pipeName, job.TaskId);
        Assert(taskState == "succeeded", $"Stage-scoped user stop changed the whole task to {taskState}.");

        var stopped = StageResults(job.JobRoot, "speech-playback").Single();
        Assert(stopped.Status == "cancelled" && stopped.Payload?.GetProperty("playbackState").GetString() == "stopped", "Playback stop did not produce cancelled/stopped.");
        var stoppedAttemptDirectory = Directory.GetParent(PathPolicy.ResolveJobRelative(job.JobRoot,
            $"runs/{stopped.RunId}/stages/speech-playback/attempts/{stopped.AttemptId}/result.json"))!.FullName;
        var signal = JsonSerializer.Deserialize<WorkerCancelSignal>(await File.ReadAllTextAsync(Path.Combine(stoppedAttemptDirectory, "cancel.json")), ContractJson.Options)
            ?? throw new InvalidDataException("Playback stop signal is invalid.");
        Assert(signal.Reason == "user_stop" && signal.AttemptId == stopped.AttemptId, "Playback stop targeted the wrong attempt/reason.");
        var resolvedTarget = acceptedStop.GetProperty("resolved");
        Assert(resolvedTarget.GetProperty("runId").GetString() == stopped.RunId &&
               resolvedTarget.GetProperty("stageId").GetString() == stopped.StageId &&
               resolvedTarget.GetProperty("attemptId").GetString() == stopped.AttemptId &&
               resolvedTarget.GetProperty("operationId").GetString() == stopped.OperationId,
            "Public receipt resolved target does not match the stopped worker request.");
        Assert(!Directory.GetFiles(job.JobRoot, "cancel-requested.json", SearchOption.AllDirectories).Any(),
            "Stage-scoped playback stop wrote a run-level cancellation request.");
        var controlEvent = Directory.GetFiles(Path.Combine(job.JobRoot, "runs"), "*.json", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}events{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options))
            .Single(item => item?.Type == "stage.control_requested");
        Assert(controlEvent!.Payload.GetProperty("resolved").GetProperty("operationId").GetString() == stopped.OperationId,
            "Stage-control event omitted the resolved worker operationId.");
        Assert(StageResults(job.JobRoot, "content.understand-and-card").Single().Status == "completed" &&
               StageResults(job.JobRoot, "export-remnote").Single().Status == "completed",
            "Stage-scoped playback stop interrupted the clipping/export branch.");

        var synth = StageResults(job.JobRoot, "speech-synthesize").Single();
        var audio = synth.Outputs.Single(item => item.Kind == "audio.speech");
        var replayRef = "controls/playback-replay.v1.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(job.JobRoot, replayRef), new { schemaVersion = "desktop-action.v1", action = "playback.replay" });
        await ProvideInputAsync(pipeName, job.TaskId, "replay", replayRef);
        await WaitUntilAsync(() => StageResults(job.JobRoot, "speech-playback").Count == 2 &&
            StageResults(job.JobRoot, "speech-playback").Any(item => item.RunId != stopped.RunId && item.Status == "completed"),
            "Playback replay did not publish a fresh completed result.");

        var replayed = StageResults(job.JobRoot, "speech-playback").Single(item => item.RunId != stopped.RunId);
        Assert(replayed.AttemptId != stopped.AttemptId && replayed.Status == "completed", "Replay reused the stopped playback attempt.");
        var replayRunStages = Directory.GetDirectories(Path.Combine(job.JobRoot, "runs", replayed.RunId, "stages"))
            .Select(Path.GetFileName).ToArray();
        Assert(replayRunStages.SequenceEqual(new[] { "speech-playback" }), "Replay reran synthesis/content/export instead of playback only.");
        var replayRequestPath = StageFiles(job.JobRoot, "speech-playback", "request.json")
            .Single(path => path.Contains($"{Path.DirectorySeparatorChar}{replayed.RunId}{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var replayRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(replayRequestPath), ContractJson.Options)
            ?? throw new InvalidDataException("Replay request is invalid.");
        Assert(replayRequest.InputRefs.Count == 1 && replayRequest.InputRefs[0].ArtifactRef == audio.ArtifactRef &&
               replayRequest.InputRefs[0].ExpectedSha256 == audio.ExpectedSha256,
            "Replay did not bind the prior successful audio.speech artifact.");
        Assert(StageResults(job.JobRoot, "speech-synthesize").Count == 1 &&
               StageResults(job.JobRoot, "content.understand-and-card").Count == 1 &&
               StageResults(job.JobRoot, "export-remnote").Count == 1,
            "Replay reran an unrelated branch.");
        var starts = StageFiles(job.JobRoot, "speech-playback", "progress.ndjson")
            .Count(path => File.ReadAllText(path).Contains("worker.started", StringComparison.Ordinal));
        Assert(starts == 2, "Stop/replay did not launch two distinct playback workers.");

        return new
        {
            name = "stage-scoped-playback-stop-and-replay",
            status = "passed",
            inputText = "read and keep clipping",
            stop = new
            {
                taskState,
                stopped.RunId,
                stopped.AttemptId,
                publicStatus = stopped.Status,
                playbackState = stopped.Payload?.GetProperty("playbackState").GetString(),
                cancelReason = signal.Reason,
                receiptSchemaVersion = acceptedStop.GetProperty("schemaVersion").GetString(),
                resolvedOperationId = resolvedTarget.GetProperty("operationId").GetString(),
                concurrentLoserCode = rejectedStop.GetProperty("error").GetProperty("code").GetString(),
                runCancelRequested = false,
                contentStatus = "completed",
                exportStatus = "completed"
            },
            replay = new
            {
                replayed.RunId,
                replayed.AttemptId,
                publicStatus = replayed.Status,
                inputArtifactRef = replayRequest.InputRefs[0].ArtifactRef,
                inputSha256 = replayRequest.InputRefs[0].ExpectedSha256,
                stages = replayRunStages,
                workerStarts = starts,
                synthesisRuns = 1,
                contentRuns = 1,
                exportRuns = 1
            }
        };
    }
    finally
    {
        shutdown.Cancel();
        try { await hostTask; } catch (OperationCanceledException) { }
    }
}

static async Task<object> RunStageControlFailClosedAndRecoveryAsync(string testRoot)
{
    var jobs = Path.Combine(testRoot, "stage-control-contract");
    Directory.CreateDirectory(jobs);
    var request = new StageControlRequest("task-no-active", "speech.playback", "current-active", "user_stop");
    Directory.CreateDirectory(Path.Combine(jobs, request.TaskId));
    var noActive = await new StageControlHandler(jobs).HandleAsync(request, CancellationToken.None);
    Assert(!noActive.Accepted && noActive.Error?.Code == "control.playback_not_active", "No-active playback did not fail closed.");

    var terminalTask = "task-terminal-playback";
    var terminalRequest = await WritePlaybackAttemptAsync(jobs, terminalTask, "run-terminal", "attempt-terminal");
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(Path.Combine(jobs, terminalTask), terminalRequest.ResultPath), new { status = "cancelled" });
    var terminal = await new StageControlHandler(jobs).HandleAsync(
        new StageControlRequest(terminalTask, "speech.playback", "current-active", "user_stop"), CancellationToken.None);
    Assert(!terminal.Accepted && terminal.Error?.Code == "control.playback_terminal", "Terminal playback did not fail closed.");

    var ambiguousTask = "task-ambiguous-playback";
    var ambiguousA = await WritePlaybackAttemptAsync(jobs, ambiguousTask, "run-ambiguous", "attempt-a");
    var ambiguousB = await WritePlaybackAttemptAsync(jobs, ambiguousTask, "run-ambiguous", "attempt-b");
    var ambiguous = await new StageControlHandler(jobs).HandleAsync(
        new StageControlRequest(ambiguousTask, "speech.playback", "current-active", "user_stop"), CancellationToken.None);
    Assert(!ambiguous.Accepted && ambiguous.Error?.Code == "control.playback_ambiguous", "Ambiguous playback did not fail closed.");
    Assert(!File.Exists(PathPolicy.ResolveJobRelative(Path.Combine(jobs, ambiguousTask), ambiguousA.CancelSignal.Path)) &&
           !File.Exists(PathPolicy.ResolveJobRelative(Path.Combine(jobs, ambiguousTask), ambiguousB.CancelSignal.Path)),
        "Ambiguous playback published a cancellation signal.");

    var recoveryTask = "task-recovery-playback";
    var recoveryRequest = await WritePlaybackAttemptAsync(jobs, recoveryTask, "run-recovery", "attempt-recovery");
    var recoveryRoot = Path.Combine(jobs, recoveryTask);
    var recoverySignalPath = PathPolicy.ResolveJobRelative(recoveryRoot, recoveryRequest.CancelSignal.Path);
    var requestedAt = DateTimeOffset.UtcNow;
    await AtomicFile.WriteJsonOnceAsync(recoverySignalPath, new WorkerCancelSignal(
        "1.0.0", recoveryRequest.AttemptId, recoveryRequest.OperationId, "user_stop", requestedAt,
        requestedAt.AddMilliseconds(2000), "control-recovery-fixture"));
    var handler = new StageControlHandler(jobs);
    Assert(await handler.RecoverAsync(CancellationToken.None) == 1, "Stage-control recovery did not repair an interrupted publish.");
    Assert(await handler.RecoverAsync(CancellationToken.None) == 0, "Stage-control recovery was not idempotent.");
    var receiptPath = PathPolicy.ResolveJobRelative(recoveryRoot, "runs/run-recovery/controls/control-recovery-fixture/receipt.json");
    var recoveredReceipt = JsonSerializer.Deserialize<StageControlReceipt>(await File.ReadAllTextAsync(receiptPath), ContractJson.Options)
        ?? throw new InvalidDataException("Recovered stage-control receipt is invalid.");
    Assert(recoveredReceipt.Accepted && recoveredReceipt.Resolved?.OperationId == recoveryRequest.OperationId,
        "Recovered receipt omitted the resolved playback identity.");
    var recoveredEvent = Directory.GetFiles(Path.Combine(recoveryRoot, "runs", "run-recovery", "events"), "*.json")
        .Select(path => JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options))
        .Single(item => item?.Type == "stage.control_requested");
    Assert(recoveredEvent!.Payload.GetProperty("recovered").GetBoolean(), "Recovery event did not disclose recovered=true.");
    Assert(!Directory.GetFiles(jobs, "cancel-requested.json", SearchOption.AllDirectories).Any(),
        "Fail-closed/recovery matrix wrote a run-level cancellation request.");

    return new
    {
        name = "public-stage-control-fail-closed-and-recovery",
        status = "passed",
        noActive = noActive.Error!.Code,
        terminal = terminal.Error!.Code,
        ambiguous = ambiguous.Error!.Code,
        ambiguitySignalsWritten = 0,
        recovery = "repaired-and-idempotent",
        resolvedOperationId = recoveredReceipt.Resolved!.OperationId,
        runCancelRequested = false
    };
}

static async Task<WorkerRequest> WritePlaybackAttemptAsync(string jobs, string taskId, string runId, string attemptId)
{
    var jobRoot = Path.Combine(jobs, taskId);
    Directory.CreateDirectory(jobRoot);
    var attemptPath = $"runs/{runId}/stages/speech-playback/attempts/{attemptId}";
    var request = new WorkerRequest(
        "1.0.0", $"request-{attemptId}", $"operation-{attemptId}", taskId, runId, "speech-playback", attemptId,
        "speech.edge", "speech.playback", $"idempotency-{attemptId}", attemptPath, attemptPath + "/outputs",
        attemptPath + "/progress.ndjson", attemptPath + "/result.json",
        new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000), Array.Empty<ArtifactReference>(),
        JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, attemptPath + "/request.json"), request);
    return request;
}

static string[] StageFiles(string jobRoot, string stageId, string fileName)
{
    var runs = Path.Combine(jobRoot, "runs");
    return Directory.Exists(runs)
        ? Directory.GetFiles(runs, fileName, SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}stages{Path.DirectorySeparatorChar}{stageId}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray()
        : Array.Empty<string>();
}

static IReadOnlyList<WorkerResult> StageResults(string jobRoot, string stageId) => StageFiles(jobRoot, stageId, "result.json")
    .Select(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)
        ?? throw new InvalidDataException($"Invalid worker result: {path}"))
    .ToArray();

static async Task WaitUntilAsync(Func<bool> condition, string message)
{
    for (var retry = 0; retry < 400; retry++)
    {
        if (condition()) return;
        await Task.Delay(25);
    }
    throw new TimeoutException(message);
}

static async Task<object> RunPartialBlockAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "partial");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registryPath = Path.Combine(root, "registry.json");
    await AtomicFile.WriteJsonOnceAsync(registryPath, Registry(fakeWorker, realRemNote: false, imageStatus: "partial"));
    var configPath = Path.Combine(root, "resolver.json");
    await AtomicFile.WriteJsonOnceAsync(configPath, ResolverConfig(remNoteEnabled: true));
    var pipeName = "Rqc.CoreAdapter.Partial." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    var hostTask = new OrchestratorHost(jobs, registryPath, projectRoot, 2, configPath).ServeAsync(Ipc(pipeName), shutdown.Token);
    try
    {
        var job = await CreateJobAsync(jobs, "task-partial", "screenshot", "cards-remnote", "ocr", null, false);
        await SubmitAsync(pipeName, job.TaskId);
        var state = await WaitTerminalAsync(pipeName, job.TaskId);
        Assert(state == "partial", $"Partial OCR task ended as {state}.");
        var contentStage = Path.Combine(job.JobRoot, "runs");
        Assert(!Directory.GetDirectories(contentStage, "content.understand-and-card", SearchOption.AllDirectories).Any(), "Partial OCR incorrectly started content downstream.");
        return new { name = "partial-blocks-all-downstream", status = "passed", taskStatus = state, contentStarted = false };
    }
    finally
    {
        shutdown.Cancel();
        try { await hostTask; } catch (OperationCanceledException) { }
    }
}

static async Task<object> RunOriginalMediaBoundaryAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "original-media");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registry = Registry(fakeWorker, realRemNote: false);
    var config = DesktopIntentResolver.LoadConfiguration(await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(true)));
    var remnoteSelectedJob = await CreateJobAsync(jobs, "task-original-remnote", "screenshot", "cards", "original", null, true, "2.0.0", new[] { "remnote", "anki" });
    try
    {
        await RunDirectAsync(remnoteSelectedJob, registry, config, projectRoot);
        throw new InvalidDataException("Original-media task with RemNote destination was accepted.");
    }
    catch (OrchestratorDispatchException exception)
    {
        Assert(exception.Code == "resolver.original_remnote_incompatible",
            $"Original-media RemNote rejection drifted: {exception.Code}.");
    }

    var job = await CreateJobAsync(jobs, "task-original", "screenshot", "cards", "original", null, true, "2.0.0", new[] { "anki" });
    var state = await RunDirectAsync(job, registry, config, projectRoot);
    Assert(state == "partial", $"Original-media terminal boundary ended as {state}.");
    var plan = ReadResolvedPlan(job.JobRoot);
    Assert(plan.GetProperty("schemaVersion").GetString() == "2.1.0" && plan.GetProperty("planVersion").GetString() == "2.6.0" && plan.GetProperty("stages").GetArrayLength() == 2,
        "Original media did not resolve to the additive pipeline 2.6 plan.");
    var stage = plan.GetProperty("stages")[0];
    Assert(stage.GetProperty("outputKinds").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "image.original", "image.original.bundle", "image.processing.provenance" }) &&
           stage.GetProperty("configSnapshot").EnumerateObject().Count() == 0,
        "Original media plan authorized OCR/provider configuration.");
    var boundary = plan.GetProperty("terminalBoundary");
    Assert(boundary.GetProperty("status").GetString() == "conditional" && boundary.GetProperty("code").GetString() == "media.destination_adapter_unavailable" &&
           boundary.GetProperty("unsupportedConsumers").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "content.deepseek" }),
        "Original media plan did not retain the content-only terminal boundary.");
    var imageResult = StageResults(job.JobRoot, "image-ocr").Single();
    Assert(imageResult.Status == "completed" && imageResult.Outputs.Count(item => item.Kind == "image.original") == 3 &&
           imageResult.Outputs.Count(item => item.Kind == "image.original.bundle") == 1 &&
           imageResult.Outputs.Count(item => item.Kind == "image.processing.provenance") == 1,
        "Original passthrough did not publish ordered media/provenance plus one immutable media reference per capture item.");
    Assert(imageResult.Payload is JsonElement payload && payload.GetProperty("mode").GetString() == "original_passthrough" &&
           payload.GetProperty("providerCallItemIds").GetArrayLength() == 0,
        "Original passthrough invoked the provider boundary.");
    foreach (var diagnostic in imageResult.ItemDiagnostics!)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(diagnostic.ItemId);
        var reference = diagnostic.Outputs.Single();
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(job.JobRoot, reference.ManifestPath.Replace('/', Path.DirectorySeparatorChar))));
        var file = manifest.RootElement.GetProperty("files")[0];
        var published = await File.ReadAllBytesAsync(Path.Combine(job.JobRoot, file.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
        Assert(Sha(sourceBytes).Equals(Sha(published), StringComparison.OrdinalIgnoreCase), $"Original bytes drifted for {diagnostic.ItemId}.");
    }
    var ankiResult = StageResults(job.JobRoot, "anki-export").Single();
    Assert(ankiResult.Status == "completed", "Original media did not reach the supported Anki destination.");
    using var request = JsonDocument.Parse(await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(job.JobRoot, "runs"), "request.json", SearchOption.AllDirectories)
        .Single(path => path.Contains($"{Path.DirectorySeparatorChar}anki-export{Path.DirectorySeparatorChar}", StringComparison.Ordinal))));
    var originalAnkiRequestPath = Directory.GetFiles(Path.Combine(job.JobRoot, "runs"), "request.json", SearchOption.AllDirectories)
        .Single(path => path.Contains($"{Path.DirectorySeparatorChar}anki-export{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    Assert(await ValidateWithRealAnkiLoaderAsync(projectRoot, job.JobRoot, originalAnkiRequestPath) == "ANKI_STRICT_LOAD_OK",
        "Real Anki 1.3 loader rejected the Core-owned original-media projection ancestry.");
    var cardReference = request.RootElement.GetProperty("inputRefs").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "card-bundle");
    using var cardManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(job.JobRoot, cardReference.GetProperty("manifestPath").GetString()!)));
    var cardPayloadPath = cardManifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
    using var cardBundle = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(job.JobRoot, cardPayloadPath)));
    var mediaRefs = cardBundle.RootElement.GetProperty("cards")[0].GetProperty("media_refs").EnumerateArray().ToArray();
    Assert(cardBundle.RootElement.GetProperty("schema_version").GetString() == "1.1" &&
           mediaRefs.Select(item => $"{item.GetProperty("role").GetString()}:{item.GetProperty("ordinal").GetInt32()}").SequenceEqual(new[] { "questionImages:0", "questionImages:1", "answerImages:0" }),
        "Ordered original-media CardBundle did not preserve CaptureBundle question/answer ordering.");
    var recursiveKinds = cardManifest.RootElement.GetProperty("inputRefs").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ToArray();
    Assert(recursiveKinds.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "capture-bundle", "image.original", "image.original.bundle", "image.processing.provenance", "anki-export-config" }) &&
           recursiveKinds.Count(kind => kind == "image.original") == 3 &&
           cardManifest.RootElement.GetProperty("metadata").GetProperty("validatedOriginals").GetArrayLength() == 3,
        "Ordered original-media CardBundle did not preserve exact recursive zero-copy alias evidence.");
    Assert(!Directory.GetDirectories(Path.Combine(job.JobRoot, "runs"), "content.understand-and-card", SearchOption.AllDirectories).Any() &&
           !Directory.GetDirectories(Path.Combine(job.JobRoot, "runs"), "export-remnote", SearchOption.AllDirectories).Any(),
        "Original route started an unsupported Content/RemNote consumer.");
    return new { name = "original-media-anki-only", status = "passed", taskStatus = state, originalOutputs = imageResult.Outputs.Count(item => item.Kind == "image.original"), providerCalls = 0, remnoteRejectedBeforeWorker = true, ankiStarted = true, remnoteStarted = false, ordering = "questionImages-then-answerImages" };
}

static async Task<object> RunFailedItemsOnlyContinuationAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "partial-continuation");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registry = Registry(fakeWorker, realRemNote: false, imageStatus: "partial-once");
    var config = DesktopIntentResolver.LoadConfiguration(await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(true)));
    var job = await CreateJobAsync(jobs, "task-partial-continuation", "screenshot", "cards", "ocr", null, false, "2.0.0", Array.Empty<string>());
    var state = await RunDirectAsync(job, registry, config, projectRoot);
    Assert(state == "succeeded", $"Failed-items-only continuation ended as {state}.");
    var results = StageResults(job.JobRoot, "image-ocr").OrderBy(item => item.StartedAt).ToArray();
    Assert(results.Length == 2 && results[0].Status == "partial" && results[1].Status == "completed", "Image continuation did not create one append-only successor attempt.");
    var firstCompleted = results[0].ItemDiagnostics!.Where(item => item.Status == "completed").ToDictionary(item => item.ItemId, StringComparer.Ordinal);
    var second = results[1].ItemDiagnostics!.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
    foreach (var prior in firstCompleted.Values)
        Assert(prior.Outputs.SequenceEqual(second[prior.ItemId].Outputs), $"Completed image item was not hash-reused: {prior.ItemId}");
    var failedId = results[0].ItemDiagnostics!.Single(item => item.Status == "failed").ItemId;
    Assert(results[1].Payload is JsonElement payload &&
           payload.GetProperty("providerCallItemIds").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { failedId }),
        "Successor attempt called the provider for more than the failed item.");
    var requests = StageFiles(job.JobRoot, "image-ocr", "request.json")
        .Select(path => (Path: path, Request: JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(path), ContractJson.Options)!))
        .OrderBy(item => item.Request.AttemptId == results[0].AttemptId ? 0 : 1)
        .ToArray();
    using var successor = JsonDocument.Parse(await File.ReadAllBytesAsync(requests[1].Path));
    Assert(successor.RootElement.GetProperty("payload").GetProperty("retryModuleResultPath").GetString() == results[0].Payload!.Value.GetProperty("moduleResultPath").GetString(),
        "Successor request did not bind the immutable prior module result.");
    Assert(StageResults(job.JobRoot, "content.understand-and-card").Single().Status == "completed", "Downstream was not released after the merged image result completed.");
    var events = new EventStore(job.JobRoot, Path.GetFileName(Directory.GetDirectories(Path.Combine(job.JobRoot, "runs")).Single())).ReadAll();
    Assert(events.Count(item => item.Type == "stage.partial_continuation") == 1, "Failed-items-only continuation event is missing or duplicated.");
    return new { name = "image-partial-failed-items-only", status = "passed", attempts = 2, failedItemId = failedId, successfulItemsHashReused = firstCompleted.Count, downstreamAfterMergedCompletion = true };
}

static async Task<object> RunImageUnknownExternalNoContinuationAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "image-unknown-external");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registry = Registry(fakeWorker, realRemNote: false, imageStatus: "unknown_external_state");
    var config = DesktopIntentResolver.LoadConfiguration(await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(true)));
    var job = await CreateJobAsync(jobs, "task-image-unknown", "screenshot", "cards", "ocr", null, false, "2.0.0", Array.Empty<string>());
    var state = await RunDirectAsync(job, registry, config, projectRoot);
    Assert(state == "failed" && StageFiles(job.JobRoot, "image-ocr", "request.json").Count() == 1,
        "unknown_external_state was automatically continued.");
    Assert(!Directory.GetDirectories(Path.Combine(job.JobRoot, "runs"), "content.understand-and-card", SearchOption.AllDirectories).Any(),
        "unknown_external_state started downstream.");
    return new { name = "image-unknown-external-no-auto-retry", status = "passed", taskStatus = state, attempts = 1, downstreamStarted = false };
}

static async Task<object> RunOcrProofreadSegmentGuardsAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "ocr-proofread-segment-guards");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registry = Registry(fakeWorker, realRemNote: false);
    var configuration = DesktopIntentResolver.LoadConfiguration(
        await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(remNoteEnabled: true)));
    var cases = new[] { "OCR_PROOFREAD_SEGMENT_TAMPER", "OCR_PROOFREAD_SEGMENT_MISSING", "OCR_PROOFREAD_SEGMENT_DUPLICATE" };
    foreach (var marker in cases)
    {
        var job = await CreateJobAsync(jobs, "task-" + marker.ToLowerInvariant(), "screenshot", "cards", "ocr", null, false, "2.0.0", new[] { "remnote" }, marker);
        var state = await RunDirectAsync(job, registry, configuration, projectRoot);
        Assert(state == "partial", $"Proofread guard unexpectedly allowed {marker}: {state}");
        var events = ReadLatestRunEvents(job.JobRoot);
        Assert(events.Contains("binding.ocr_proofread_segment_invalid", StringComparison.Ordinal), $"Proofread guard emitted the wrong error for {marker}: {events}");
        Assert(!StageResults(job.JobRoot, "export-remnote").Any(result => result.Worker.Id == "export.remnote") &&
               !StageResults(job.JobRoot, "anki-export").Any(result => result.Worker.Id == "export.anki"),
               $"Proofread guard started a destination worker for {marker}.");
    }
    return new { name = "ocr-proofread-segment-guards", status = "passed", rejects = new[] { "tampered", "missing", "duplicate" }, destinationWorkersStarted = 0 };
}

static async Task<object> RunOcrUnresolvedAttentionAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "ocr-unresolved-attention");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registryPath = Path.Combine(root, "registry.json");
    await AtomicFile.WriteJsonOnceAsync(registryPath, Registry(fakeWorker, realRemNote: false));
    var configPath = Path.Combine(root, "resolver.json");
    await AtomicFile.WriteJsonOnceAsync(configPath, ResolverConfigV2(true));
    var job = await CreateJobAsync(jobs, "task-ocr-unresolved", "screenshot", "cards", "ocr", null, true, "2.0.0", new[] { "remnote", "anki" }, "OCR_UNRESOLVED_FIXTURE");
    var pipeName = "Rqc.CoreAdapter.OcrAttention." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    var host = new OrchestratorHost(jobs, registryPath, projectRoot, 4, configPath);
    var hostTask = host.ServeAsync(Ipc(pipeName), shutdown.Token);
    try
    {
        await SubmitAsync(pipeName, job.TaskId);
        JsonElement projection;
        try { projection = await WaitProjectedStatusV23Async(pipeName, job.TaskId, "waiting_input"); }
        catch (TimeoutException exception) { throw new InvalidOperationException($"OCR unresolved projection failed. Latest events: {ReadLatestRunEvents(job.JobRoot)}", exception); }
        Assert(projection.GetProperty("attentionRequired").GetBoolean(), "OCR unresolved task did not project attentionRequired=true.");
        var ocrProjection = projection.GetProperty("ocrHumanResolution");
        Assert(ocrProjection.GetProperty("decisionKind").GetString() == "ocr-human-resolution" &&
               ocrProjection.GetProperty("sourceImageProvenanceRef").GetProperty("kind").GetString() == "image.processing.provenance" &&
               ocrProjection.GetProperty("sourceOriginalImageRef").GetProperty("kind").GetString() == "image.original" &&
               ocrProjection.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "accept_original", "submit_correction", "cancel" }),
            "OCR query projection omitted its hash-bound Image provenance/original or stable action set.");

        var imageResult = StageResults(job.JobRoot, "image-ocr").Single();
        var contentResult = StageResults(job.JobRoot, "content.understand-and-card").Single();
        Assert(imageResult.Status == "completed" && contentResult.Status == "completed", "Workers did not preserve their actual completed terminal results.");
        Assert(contentResult.Payload is JsonElement resultPayload && resultPayload.GetProperty("moduleReleaseState").GetString() == "unresolved",
            "Content result did not declare the immutable unresolved release state.");
        Assert(contentResult.Outputs.Count(item => item.Kind == "ocr-proofread-bundle") == 1 &&
               contentResult.Outputs.Count(item => item.Kind == "learning-bundle" && item.ContractVersion == "2.0.0") == 1 &&
               contentResult.Outputs.All(item => item.Kind != "card-bundle"),
            "OCR unresolved output set is not proofread+LearningBundle v2 without CardBundle.");

        var contentRequestPath = StageFiles(job.JobRoot, "content.understand-and-card", "request.json").Single();
        using var request = JsonDocument.Parse(await File.ReadAllBytesAsync(contentRequestPath));
        var inputPath = request.RootElement.GetProperty("payload").GetProperty("contentInputPath").GetString()!;
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(job.JobRoot, inputPath)));
        Assert(input.RootElement.GetProperty("schema_version").GetString() == "standard-input/4.0.0", "OCR input was flattened into a legacy text contract.");
        var items = input.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert(items.Length == 3 && items.All(item => item.GetProperty("source").GetProperty("kind").GetString() == "ocr_text"),
            "OCR input did not preserve one provenance-bearing item per captured image.");
        foreach (var item in items)
        {
            var source = item.GetProperty("source");
            var provenance = source.GetProperty("provenance");
            var markdown = provenance.GetProperty("markdown_artifact");
            Assert(provenance.GetProperty("producer_worker_id").GetString() == "image.ocr.mineru" &&
                   markdown.GetProperty("kind").GetString() == "image-ocr.markdown" &&
                   markdown.GetProperty("payload_path").GetString() == source.GetProperty("material_path").GetString(),
                "OCR provenance identity/path did not reach standard-input v4.");
            var payloadBytes = await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(job.JobRoot, source.GetProperty("material_path").GetString()!));
            Assert(Sha(payloadBytes) == markdown.GetProperty("payload_sha256").GetString(), "OCR payload hash drifted in standard-input v4.");
        }

        Assert(!Directory.GetDirectories(Path.Combine(job.JobRoot, "runs"), "export-remnote", SearchOption.AllDirectories).Any() &&
               !Directory.GetDirectories(Path.Combine(job.JobRoot, "runs"), "anki-export", SearchOption.AllDirectories).Any(),
            "OCR unresolved Content started a destination worker.");
        var runId = Path.GetFileName(Directory.GetDirectories(Path.Combine(job.JobRoot, "runs")).Single());
        var events = new EventStore(job.JobRoot, runId).ReadAll();
        var attention = events.Single(item => item.Type == "stage.content_attention_required");
        Assert(attention.Payload.GetProperty("releaseState").GetString() == "unresolved" &&
               attention.Payload.GetProperty("downstreamBlocked").GetBoolean() &&
               !attention.Payload.GetProperty("automaticRetry").GetBoolean(),
            "OCR unresolved attention event lost its fail-closed semantics.");
        var candidateReference = ocrProjection.GetProperty("candidateTextRef").Deserialize<ArtifactReference>(ContractJson.Options)!;
        using var candidateManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(job.JobRoot, candidateReference.ManifestPath)));
        var candidatePath = candidateManifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
        var candidateText = await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(job.JobRoot, candidatePath), new UTF8Encoding(false, true));
        var correctedText = candidateText.Replace("CANAR", "CANARY", StringComparison.Ordinal);
        Assert(correctedText != candidateText, "OCR correction fixture did not contain the expected unresolved token.");
        var imageAttemptsBefore = StageFiles(job.JobRoot, "image-ocr", "request.json").Count();
        var actionId = "ocr-correct";
        var actionRef = await PublishOcrCorrectionActionAsync(job.JobRoot, job.TaskId, actionId, ocrProjection.GetProperty("targetToken").GetString()!, candidateText, correctedText);
        await ProvideInputAsync(pipeName, job.TaskId, actionId, actionRef);
        var terminal = await WaitTerminalAsync(pipeName, job.TaskId);
        Assert(terminal == "succeeded", $"OCR correction continuation ended as {terminal}. Latest events: {ReadLatestRunEvents(job.JobRoot)} Errors: {string.Join(" | ", Directory.GetFiles(Path.Combine(job.JobRoot, "runs"), "result.json", SearchOption.AllDirectories).Select(path => File.ReadAllText(path)).Where(text => text.Contains("\"error\":", StringComparison.Ordinal)))}");
        var contentRequestsAfter = StageFiles(job.JobRoot, "content.ocr-human-resolution", "request.json").Count();
        Assert(imageAttemptsBefore == 1 && StageFiles(job.JobRoot, "image-ocr", "request.json").Count() == imageAttemptsBefore && contentRequestsAfter == 1,
            "OCR human continuation reran OCR or did not create exactly one new Content attempt.");
        var resolutionRequestPath = StageFiles(job.JobRoot, "content.ocr-human-resolution", "request.json").Single();
        using var resolutionRequest = JsonDocument.Parse(await File.ReadAllBytesAsync(resolutionRequestPath));
        var resolutionInputRef = resolutionRequest.RootElement.GetProperty("inputRefs").EnumerateArray()
            .Single(reference => reference.GetProperty("kind").GetString() == "content-input");
        using var resolutionInputManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(job.JobRoot, resolutionInputRef.GetProperty("manifestPath").GetString()!)));
        Assert(resolutionInputManifest.RootElement.GetProperty("inputRefs").EnumerateArray().Any(reference =>
                   reference.GetProperty("artifactRef").GetString() == "ocr.capture.image.question.0001.markdown" &&
                   reference.GetProperty("kind").GetString() == "image-ocr.markdown"),
            "OCR human continuation content-input did not directly bind the immutable OCR markdown provenance artifact.");
        Assert(!resolutionInputManifest.RootElement.GetProperty("inputRefs").EnumerateArray().Any(reference =>
                   reference.GetProperty("artifactRef").GetString() == "ocr.human-correction.ocr-correct"),
            "Raw Desktop correction artifact was incorrectly promoted into the adapter ancestry roots.");
        var adapterProof = resolutionInputManifest.RootElement.GetProperty("metadata").GetProperty("interactiveCommandRoot");
        Assert(adapterProof.GetProperty("schemaVersion").GetString() == "knowledge-clipper-ocr-interactive-command-root.v1" &&
               adapterProof.GetProperty("taskId").GetString() == job.TaskId &&
               adapterProof.GetProperty("action").GetString() == "submit_correction" &&
               adapterProof.GetProperty("correctionRef").GetProperty("artifactRef").GetString() == "ocr.human-correction.ocr-correct" &&
               adapterProof.GetProperty("acceptedEventRef").GetString()!.StartsWith("runs/", StringComparison.Ordinal) &&
               adapterProof.GetProperty("resolutionRef").GetString()!.StartsWith("runs/", StringComparison.Ordinal) &&
               adapterProof.GetProperty("diffsSha256").GetString()!.Length == 64,
            "OCR correction adapter metadata did not preserve the strict command/event/hash proof.");
        var resolutionResult = StageResults(job.JobRoot, "content.ocr-human-resolution").Single();
        Assert(resolutionResult.Status == "completed" && resolutionResult.Outputs.Any(item => item.Kind == "ocr-human-resolution-bundle") &&
               resolutionResult.Outputs.Any(item => item.Kind == "learning-bundle" && item.ContractVersion == "3.0.0") && resolutionResult.Outputs.Any(item => item.Kind == "card-bundle"),
            "OCR correction did not produce the ready human-resolution/Learning/Card bundle set.");
        Assert(StageResults(job.JobRoot, "export-remnote").Count == 1 && StageResults(job.JobRoot, "anki-export").Count == 1,
            "OCR correction did not resume the original frozen destinations exactly once.");
        await ProvideInputAsync(pipeName, job.TaskId, actionId, actionRef);
        await Task.Delay(250);
        Assert(StageFiles(job.JobRoot, "content.ocr-human-resolution", "request.json").Count() == 1 &&
               StageResults(job.JobRoot, "export-remnote").Count == 1 && StageResults(job.JobRoot, "anki-export").Count == 1,
            "OCR correction command replay duplicated Content or destination effects.");
        return new
        {
            name = "ocr-unresolved-human-continuation",
            status = "passed",
            taskStatus = terminal,
            inputSchema = "standard-input/4.0.0",
            contentOutputs = contentResult.Outputs.Select(item => new { item.Kind, item.ContractVersion }).ToArray(),
            initialDownstreamStarted = false,
            resolutionInputSchema = "standard-input/5.0.0",
            ocrRerun = false,
            destinationRuns = new { remnote = 1, anki = 1 },
            replayIdempotent = true,
            automaticRetry = false
        };
    }
    finally
    {
        shutdown.Cancel();
        try { await hostTask; } catch (OperationCanceledException) { }
    }
}

static async Task<object> RunNegativeResolverMatrixAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "negative");
    Directory.CreateDirectory(root);
    var registry = Registry(fakeWorker, realRemNote: false);
    var config = DesktopIntentResolver.LoadConfiguration(await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfig(true)));

    var unknownProfile = await CreateJobAsync(root, "task-unknown-profile", "clip", "unknown-profile", "text", "text", false);
    await ExpectCodeAsync("resolver.profile_unknown", () => ResolveAsync(unknownProfile, registry, config, projectRoot));
    var missingRegistry = registry with { Workers = registry.Workers.Where(item => item.WorkerId != "content.deepseek").ToArray() };
    var missing = await CreateJobAsync(root, "task-worker-missing", "clip", "cards-remnote", "text", "text", false);
    await ExpectCodeAsync("resolver.worker_missing", () => ResolveAsync(missing, missingRegistry, config, projectRoot));

    var intentPath = Path.Combine(missing.JobRoot, "plans", "desktop-intent.v1.json");
    var invalid = JsonSerializer.Deserialize<Dictionary<string, object?>>(await File.ReadAllTextAsync(intentPath), ContractJson.Options)!;
    invalid["intent"] = "clip.unknown";
    var unknownIntentPath = Path.Combine(missing.JobRoot, "plans", "desktop-intent.unknown.json");
    await File.WriteAllTextAsync(unknownIntentPath, JsonSerializer.Serialize(invalid, ContractJson.Options));
    await ExpectCodeAsync("resolver.intent_unknown", () => new DesktopIntentResolver().ResolveAsync(missing.JobRoot, unknownIntentPath, registry, "run-negative", config, CancellationToken.None));
    return new { name = "resolver-negative-matrix", status = "passed", unknownIntent = "rejected", unknownProfile = "rejected", workerMissing = "rejected" };
}

static async Task<object> RunStateAwareRetentionAsync(string testRoot)
{
    var jobs = Path.Combine(testRoot, "state-aware-retention");
    Directory.CreateDirectory(jobs);
    async Task SeedAsync(string taskId, string retentionClass, string terminalStatus, bool safe, bool attention = false, bool unknown = false)
    {
        var jobRoot = Path.Combine(jobs, taskId);
        Directory.CreateDirectory(jobRoot);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow.AddDays(-2), createdBy = new { application = "retention-test", version = "1.0.0" },
            intent = "clip.card", executionProfile = "cards-anki", inputRefs = Array.Empty<object>(), requestedOutputs = Array.Empty<string>(),
            initialPlanRef = "plans/unused.json", retentionClass
        });
        var store = new EventStore(jobRoot, "run-retention");
        await store.AppendAsync("task.state", taskId, new { status = "validating" });
        await store.AppendAsync("task.state", taskId, new { status = "queued" });
        await store.AppendAsync("task.state", taskId, new { status = "running" });
        if (attention) await store.AppendAsync("stage.content_attention_required", taskId, new { releaseState = "unresolved" });
        await store.AppendAsync("task.state", taskId, new { status = terminalStatus });
        if (safe) await store.AppendAsync("retention.safe_to_delete", taskId, new { status = terminalStatus, reconciled = true });
        if (unknown)
        {
            var attempt = Path.Combine(jobRoot, "runs", "run-retention", "stages", "export", "attempts", "attempt-1");
            Directory.CreateDirectory(attempt);
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(attempt, "result.json"), new { status = "unknown_external_state" });
        }
    }
    await SeedAsync("retention-eligible", "ephemeral", "succeeded", true);
    await SeedAsync("retention-evidence", "evidence", "succeeded", true);
    await SeedAsync("retention-pinned", "pinned", "succeeded", true);
    await SeedAsync("retention-attention", "ephemeral", "partial", true, attention: true);
    await SeedAsync("retention-unknown", "ephemeral", "failed", true, unknown: true);
    await SeedAsync("retention-active", "ephemeral", "running", true);
    await SeedAsync("retention-waiting", "ephemeral", "waiting_input", true);
    var service = new StateAwareRetentionService();
    var evaluations = service.Evaluate(jobs, DateTimeOffset.UtcNow, TimeSpan.Zero);
    Assert(evaluations.Single(item => item.TaskId == "retention-eligible").Eligible, "Reconciled terminal ephemeral task was not retention-eligible.");
    Assert(evaluations.Single(item => item.TaskId == "retention-evidence").Reason == "retention_class_evidence" &&
           evaluations.Single(item => item.TaskId == "retention-pinned").Reason == "retention_class_pinned" &&
           evaluations.Single(item => item.TaskId == "retention-attention").Reason == "attention_required" &&
           evaluations.Single(item => item.TaskId == "retention-unknown").Reason == "unknown_external_state" &&
           evaluations.Single(item => item.TaskId == "retention-active").Reason == "task_not_terminal" &&
           evaluations.Single(item => item.TaskId == "retention-waiting").Reason == "task_not_terminal",
        "State-aware retention did not preserve evidence/pinned/active/waiting/attention/unknown-external-state boundaries.");
    var quarantine = Path.Combine(testRoot, "retention-quarantine");
    var quarantined = await service.QuarantineEligibleAsync(jobs, quarantine, evaluations, CancellationToken.None);
    Assert(quarantined.Select(item => item.TaskId).SequenceEqual(new[] { "retention-eligible" }) && !Directory.Exists(Path.Combine(jobs, "retention-eligible")) &&
           Directory.Exists(quarantined[0].QuarantineJobRoot) && Directory.Exists(Path.Combine(jobs, "retention-evidence")) &&
           Directory.Exists(Path.Combine(jobs, "retention-pinned")) && Directory.Exists(Path.Combine(jobs, "retention-active")) &&
           Directory.Exists(Path.Combine(jobs, "retention-waiting")) && Directory.Exists(Path.Combine(jobs, "retention-attention")) && Directory.Exists(Path.Combine(jobs, "retention-unknown")),
        "State-aware retention quarantine moved a non-eligible task or did not preserve the recoverable eligible task.");
    return new { name = "state-aware-retention", status = "passed", quarantined = quarantined.Select(item => item.TaskId).ToArray(), preserved = new[] { "evidence", "pinned", "active", "waiting_input", "attention_required", "unknown_external_state" }, legacyPurgeInvoked = false, destructiveDeleteInvoked = false };
}

static async Task<object> RunModuleActionDispatchAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "module-action");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registryPath = await WriteJsonAsync(Path.Combine(root, "registry.json"), Registry(fakeWorker, realRemNote: false));
    var configPath = await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(true));
    var pipeName = "Rqc.CoreAdapter.ModuleAction." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    var hostTask = new OrchestratorHost(jobs, registryPath, projectRoot, 2, configPath).ServeAsync(IpcV12(pipeName), shutdown.Token);
    try
    {
        var voicesId = "voices-action";
        var voicesRoot = Path.Combine(jobs, "module-actions", voicesId);
        Directory.CreateDirectory(voicesRoot);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(voicesRoot, "input.json"), new
        {
            schemaVersion = "speech-settings-voice-discovery-input.v1", mode = "validate_only", requestedLanguages = new[] { "zh-CN" }, cacheMaxAgeSeconds = 3600,
            deployment = new { edgeTtsPath = (string?)null, ffplayPath = (string?)null },
            catalogSnapshot = new { provider = "edge-tts", catalogVersion = "fake", updatedAtUtc = "2026-08-30T00:00:00Z", voices = Array.Empty<object>() }
        });
        var voicesRequest = new
        {
            protocolVersion = "1.2", correlationId = "module-voices", messageType = "module-action",
            payload = new { schemaVersion = "knowledge-clipper-module-action.v1", actionId = voicesId, operation = "speech.voices", payloadRef = $"module-actions/{voicesId}/input.json", idempotencyKey = new string('1', 64) }
        };
        var voicesResponseText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(voicesRequest) + "\n");
        using var voicesResponse = JsonDocument.Parse(voicesResponseText);
        Assert(voicesResponse.RootElement.GetProperty("ok").GetBoolean() && voicesResponse.RootElement.GetProperty("payload").GetProperty("accepted").GetBoolean(), "Speech voice catalog public dispatch failed: " + voicesResponseText);
        var catalogRef = voicesResponse.RootElement.GetProperty("payload").GetProperty("catalogSnapshotRef").GetString()!;
        Assert(File.Exists(PathPolicy.ResolveJobRelative(jobs, catalogRef)), "Speech voice catalog snapshot was not persisted.");
        using var voicesReplay = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(voicesRequest) + "\n"));
        Assert(voicesReplay.RootElement.GetProperty("payload").GetProperty("outputSha256").GetString() == voicesResponse.RootElement.GetProperty("payload").GetProperty("outputSha256").GetString(), "Speech voice discovery replay was not idempotent.");

        var remnoteId = "remnote-probe-action";
        var remnoteRoot = Path.Combine(jobs, "module-actions", remnoteId);
        Directory.CreateDirectory(remnoteRoot);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(remnoteRoot, "input.json"), new { schemaVersion = "remnote-public-probe-request.v2", probeId = "fake-probe", authorizationScope = "isolated-canary", timeoutMs = 15000, operations = new[] { "status", "search", "list", "read" }, target = new { rootRemId = "fake-root", rootTitle = "Canary" } });
        var remnoteRequest = new { protocolVersion = "1.2", correlationId = "module-remnote", messageType = "module-action", payload = new { schemaVersion = "knowledge-clipper-module-action.v1", actionId = remnoteId, operation = "remnote.probe", payloadRef = $"module-actions/{remnoteId}/input.json", idempotencyKey = new string('2', 64) } };
        using var remnoteResponse = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(remnoteRequest) + "\n"));
        Assert(remnoteResponse.RootElement.GetProperty("payload").GetProperty("accepted").GetBoolean() &&
               remnoteResponse.RootElement.GetProperty("payload").GetProperty("externalWriteCount").GetInt32() == 0 &&
               !remnoteResponse.RootElement.GetProperty("payload").GetProperty("physicalAudio").GetBoolean(),
            "Healthy RemNote probe public dispatch violated the typed read-only boundary.");
        var negativeProjections = new[]
        {
            await RunRemNoteTypedNonzeroModuleActionAsync(projectRoot, root, fakeWorker, "degraded", 3, true),
            await RunRemNoteTypedNonzeroModuleActionAsync(projectRoot, root, fakeWorker, "blocked", 3, true),
            await RunRemNoteTypedNonzeroModuleActionAsync(projectRoot, root, fakeWorker, "malformed", 3, false)
        };

        var previewId = "preview-rejected";
        var previewRoot = Path.Combine(jobs, "module-actions", previewId); Directory.CreateDirectory(previewRoot);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(previewRoot, "input.json"), new { schemaVersion = "speech-settings-speaker-preview-input.v1", scope = "project", effectiveAt = "preview_only", action = "speaker.preview", explicitSideEffectAuthorization = false, previewLanguage = "zh-CN", adapterInput = new { } });
        var previewRequest = new { protocolVersion = "1.2", correlationId = "module-preview-rejected", messageType = "module-action", payload = new { schemaVersion = "knowledge-clipper-module-action.v1", actionId = previewId, operation = "speech.preview", payloadRef = $"module-actions/{previewId}/input.json", idempotencyKey = new string('3', 64) } };
        using var previewResponse = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(previewRequest) + "\n"));
        Assert(!previewResponse.RootElement.GetProperty("ok").GetBoolean() && !File.Exists(Path.Combine(previewRoot, "receipt.json")), "Speaker preview ran without explicit authorization.");
        return new { name = "module-action-public-dispatch", status = "passed", ipc = "1.2", voiceCatalogPersisted = true, voiceReplayIdempotent = true, remnoteProbeWrites = 0, remnoteTypedStates = negativeProjections, previewWithoutAuthorization = "rejected", physicalSpeakerInvocations = 0 };
    }
    finally { shutdown.Cancel(); try { await hostTask; } catch (OperationCanceledException) { } }
}

static async Task<object> RunRemNoteTargetDiscoveryV13Async(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "remnote-target-discovery-v13");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registry = Registry(fakeWorker, realRemNote: false) with
    {
        Workers = Registry(fakeWorker, realRemNote: false).Workers.Select(worker => worker.WorkerId == "export.remnote"
            ? worker with { Entrypoint = new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", "export.remnote" }) }
            : worker).ToArray()
    };
    var registryPath = await WriteJsonAsync(Path.Combine(root, "registry.json"), registry);
    var configPath = await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(true));
    var pipeName = "Rqc.CoreAdapter.RemNoteDiscovery." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    var hostTask = new OrchestratorHost(jobs, registryPath, projectRoot, 1, configPath).ServeAsync(IpcV13(pipeName), shutdown.Token);
    try
    {
        var actionId = "discover-unique";
        var actionRoot = Path.Combine(jobs, "module-actions", actionId);
        Directory.CreateDirectory(actionRoot);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(actionRoot, "input.json"), new
        {
            schemaVersion = "remnote-target-discovery-request.v1", discoveryId = "fake-discovery-unique",
            authorizationScope = "configured-destination", timeoutMs = 15000,
            operations = new[] { "status", "search", "list", "read" },
            selector = new { rootTitle = "Configured Root", expectedType = "document", match = "exact", uniqueness = "required" }
        });
        var request = new
        {
            protocolVersion = "1.3", correlationId = "discover-unique", messageType = "module-action",
            payload = new { schemaVersion = "knowledge-clipper-module-action.v2", actionId, operation = "remnote.discover-target", payloadRef = $"module-actions/{actionId}/input.json", idempotencyKey = new string('d', 64) }
        };
        var responseText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(request) + "\n");
        using var response = JsonDocument.Parse(responseText);
        var payload = response.RootElement.GetProperty("payload");
        Assert(response.RootElement.GetProperty("protocolVersion").GetString() == "1.3" && payload.GetProperty("schemaVersion").GetString() == "knowledge-clipper-module-action-receipt.v2" &&
               payload.GetProperty("accepted").GetBoolean() && payload.GetProperty("status").GetString() == "ready" && payload.GetProperty("externalWriteCount").GetInt32() == 0 &&
               !payload.GetProperty("automaticRetryAllowed").GetBoolean(), "Unique RemNote discovery did not preserve the v1.3 read-only receipt boundary.");
        var resultPath = PathPolicy.ResolveJobRelative(jobs, payload.GetProperty("outputRef").GetString()!);
        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath));
        var selection = result.RootElement.GetProperty("selection");
        Assert(result.RootElement.GetProperty("state").GetString() == "healthy" && result.RootElement.GetProperty("admission").GetString() == "ready" &&
               selection.GetProperty("resolution").GetString() == "unique" && selection.GetProperty("exactReadback").GetBoolean() && selection.GetProperty("persistableBinding").GetBoolean() &&
               selection.GetProperty("resolvedRootRemId").GetString() == "fake-root", "Unique RemNote discovery did not return a persistable exact binding.");
        using var replay = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(request) + "\n"));
        Assert(replay.RootElement.GetProperty("payload").GetProperty("outputSha256").GetString() == payload.GetProperty("outputSha256").GetString(), "RemNote discovery replay identity drifted.");
    }
    finally { shutdown.Cancel(); try { await hostTask; } catch (OperationCanceledException) { } }

    var blockedRoot = Path.Combine(root, "blocked");
    var blockedJobs = Path.Combine(blockedRoot, "jobs");
    Directory.CreateDirectory(blockedJobs);
    var blockedRegistry = registry with
    {
        Workers = registry.Workers.Select(worker => worker.WorkerId == "export.remnote"
            ? worker with { Entrypoint = new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", "export.remnote", "--remnote-discovery-state", "ambiguous" }) }
            : worker).ToArray()
    };
    var blockedRegistryPath = await WriteJsonAsync(Path.Combine(blockedRoot, "registry.json"), blockedRegistry);
    var blockedId = "discover-ambiguous";
    var blockedActionRoot = Path.Combine(blockedJobs, "module-actions", blockedId);
    Directory.CreateDirectory(blockedActionRoot);
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(blockedActionRoot, "input.json"), new
    {
        schemaVersion = "remnote-target-discovery-request.v1", discoveryId = "fake-discovery-ambiguous",
        authorizationScope = "configured-destination", timeoutMs = 15000,
        operations = new[] { "status", "search", "list", "read" },
        selector = new { rootTitle = "Configured Root", expectedType = "document", match = "exact", uniqueness = "required" }
    });
    var blockedReceipt = await new ModuleActionHandler(blockedJobs, blockedRegistryPath, projectRoot).HandleAsync(
        new ModuleActionRequest("knowledge-clipper-module-action.v2", blockedId, "remnote.discover-target", $"module-actions/{blockedId}/input.json", new string('a', 64)), CancellationToken.None);
    Assert(blockedReceipt.Accepted && blockedReceipt.Status == "blocked" && blockedReceipt.ExternalWriteCount == 0 && !blockedReceipt.AutomaticRetryAllowed,
        "Ambiguous RemNote discovery did not remain a typed zero-write blocked result.");
    using var blockedResult = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(blockedActionRoot, "result.json")));
    Assert(blockedResult.RootElement.GetProperty("selection").GetProperty("resolution").GetString() == "ambiguous" &&
           !blockedResult.RootElement.GetProperty("selection").GetProperty("persistableBinding").GetBoolean() &&
           blockedResult.RootElement.GetProperty("reasonCodes").EnumerateArray().Single().GetString() == "target.discovery_ambiguous",
        "Ambiguous RemNote discovery became persistable or lost its typed reason.");
    return new { name = "remnote-target-discovery-v13", status = "passed", ipc = "1.3", uniquePersistableBinding = true, ambiguousPersistableBinding = false, externalWrites = 0, automaticRetryAllowed = false };
}

static async Task<object> RunAnkiManagedInitializationV14Async(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "anki-managed-initialization-v14");
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var actionLog = Path.Combine(root, "fake-anki-actions.ndjson");

    WorkerRegistry RegistryFor(string mode)
    {
        var current = Registry(fakeWorker, realRemNote: false);
        return current with
        {
            Workers = current.Workers.Select(worker => worker.WorkerId == "export.anki"
                ? worker with
                {
                    Entrypoint = new WorkerEntrypoint(fakeWorker, new[]
                    {
                        "--worker-id", "export.anki", "--anki-initialize-mode", mode, "--anki-action-log", actionLog
                    }),
                    OutputKinds = new[] { "anki-export-evidence", "anki-connection-status", "anki-managed-initialization-status" }
                }
                : worker).ToArray()
        };
    }

    var initializedRegistry = await WriteJsonAsync(Path.Combine(root, "registry-initialized.json"), RegistryFor("initialized"));
    var initializedHandler = new ModuleActionHandler(jobs, initializedRegistry, projectRoot);
    var unauthorized = await CreateAnkiInitializationActionPackageAsync(jobs, "anki-init-unauthorized", new string('5', 64), explicitAuthorization: false);
    try
    {
        await initializedHandler.HandleAsync(unauthorized, CancellationToken.None);
        throw new InvalidOperationException("Unauthorized Anki managed initialization was accepted.");
    }
    catch (InvalidDataException) { }
    Assert(!File.Exists(actionLog), "Unauthorized Anki initialization invoked the admitted worker/provider.");

    var action = await CreateAnkiInitializationActionPackageAsync(jobs, "anki-init-first", new string('6', 64), explicitAuthorization: true);
    var pipeName = "Rqc.CoreAdapter.AnkiInitialize." + Guid.NewGuid().ToString("N");
    var configPath = await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(true));
    using (var shutdown = new CancellationTokenSource())
    {
        var hostTask = new OrchestratorHost(jobs, initializedRegistry, projectRoot, 1, configPath).ServeAsync(IpcV14(pipeName), shutdown.Token);
        try
        {
            var envelope = new { protocolVersion = "1.4", correlationId = "anki-init-first", messageType = "module-action", payload = action };
            var responseText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(envelope) + "\n");
            using var response = JsonDocument.Parse(responseText);
            Assert(response.RootElement.TryGetProperty("payload", out var receipt), "Anki initialize-managed IPC returned no receipt: " + responseText);
            Assert(response.RootElement.GetProperty("ok").GetBoolean() && receipt.GetProperty("schemaVersion").GetString() == "knowledge-clipper-module-action-receipt.v3" &&
                   receipt.GetProperty("accepted").GetBoolean() && receipt.GetProperty("status").GetString() == "initialized" &&
                   receipt.GetProperty("externalWriteCount").GetInt32() == 2 && !receipt.GetProperty("automaticRetryAllowed").GetBoolean(),
                "Anki initialize-managed IPC 1.4 dispatch did not return a verified initialized receipt.");
            using var replay = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(envelope) + "\n"));
            Assert(replay.RootElement.GetProperty("payload").GetProperty("outputSha256").GetString() == receipt.GetProperty("outputSha256").GetString(),
                "Same-action Anki initialization replay was not receipt-idempotent.");
        }
        finally { shutdown.Cancel(); try { await hostTask; } catch (OperationCanceledException) { } }
    }
    var afterSameActionReplay = File.ReadAllLines(actionLog).Length;
    Assert(afterSameActionReplay == 1, "Same-action Anki initialization replay invoked the worker again.");

    var replayRegistry = await WriteJsonAsync(Path.Combine(root, "registry-already-current.json"), RegistryFor("already_current"));
    var explicitReplay = await CreateAnkiInitializationActionPackageAsync(jobs, "anki-init-explicit-replay", new string('7', 64), explicitAuthorization: true);
    var explicitReplayReceipt = await new ModuleActionHandler(jobs, replayRegistry, projectRoot).HandleAsync(explicitReplay, CancellationToken.None);
    Assert(explicitReplayReceipt.Accepted && explicitReplayReceipt.Status == "already_current" && explicitReplayReceipt.ExternalWriteCount == 0,
        "Explicit Anki initialization replay created resources or lost already_current semantics.");

    var unknownRegistry = await WriteJsonAsync(Path.Combine(root, "registry-unknown.json"), RegistryFor("unknown_external_state"));
    var unknownAction = await CreateAnkiInitializationActionPackageAsync(jobs, "anki-init-unknown", new string('8', 64), explicitAuthorization: true);
    var unknownHandler = new ModuleActionHandler(jobs, unknownRegistry, projectRoot);
    var unknownReceipt = await unknownHandler.HandleAsync(unknownAction, CancellationToken.None);
    Assert(!unknownReceipt.Accepted && unknownReceipt.Status == "unknown_external_state" && unknownReceipt.ExternalWriteCount == -1 && !unknownReceipt.AutomaticRetryAllowed,
        "Anki response loss did not remain unknown_external_state/non-retryable.");
    var beforeUnknownReplay = File.ReadAllLines(actionLog).Length;
    var unknownReplay = await unknownHandler.HandleAsync(unknownAction, CancellationToken.None);
    Assert(unknownReplay.Status == "unknown_external_state" && File.ReadAllLines(actionLog).Length == beforeUnknownReplay,
        "Anki unknown_external_state was automatically retried.");

    var calls = File.ReadAllLines(actionLog).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    Assert(calls.All(item => item.GetProperty("realAnkiConnectCalls").GetInt32() == 0) &&
           calls[0].GetProperty("createModelCalls").GetInt32() == 1 && calls[0].GetProperty("createDeckCalls").GetInt32() == 1 && calls[0].GetProperty("exactReadback").GetBoolean() &&
           calls[1].GetProperty("createModelCalls").GetInt32() == 0 && calls[1].GetProperty("createDeckCalls").GetInt32() == 0 && calls[1].GetProperty("exactReadback").GetBoolean(),
        "Fake Anki initialization call ledger violated missing-only/readback/zero-real-provider gates.");
    return new
    {
        name = "anki-managed-initialization-v14",
        status = "passed",
        ipc = "1.4",
        unauthorizedWorkerCalls = 0,
        firstRun = new { modelsCreated = 1, decksCreated = 1, exactReadback = true },
        explicitReplay = new { modelsCreated = 0, decksCreated = 0, status = "already_current" },
        unknownExternalStateAutomaticRetries = 0,
        realAnkiConnectCalls = 0
    };
}

static async Task<ModuleActionRequest> CreateAnkiInitializationActionPackageAsync(
    string jobsRoot,
    string actionId,
    string idempotencyKey,
    bool explicitAuthorization)
{
    var actionRoot = Path.Combine(jobsRoot, "module-actions", actionId);
    var packageRoot = Path.Combine(actionRoot, "package");
    var attemptRelative = "runs/run-init/stages/anki-export/attempts/attempt-1";
    var attemptRoot = PathPolicy.ResolveJobRelative(packageRoot, attemptRelative);
    var configArtifactId = "anki-config";
    var configPayloadRelative = $"artifacts/{configArtifactId}/anki-export.v2.json";
    var configPayloadPath = PathPolicy.ResolveJobRelative(packageRoot, configPayloadRelative);
    Directory.CreateDirectory(Path.GetDirectoryName(configPayloadPath)!);
    var config = new
    {
        schema_version = "anki-export-config.v2",
        endpoint = "http://127.0.0.1:1",
        expected_profile = "Fake Profile",
        deck_routes = new { canary = "KnowledgeClipper::Managed" },
        note_types = new { image_qa = new { model_name = "KnowledgeClipper Managed v1", required_fields = new[] { "question", "answer" }, field_map = new { question = "Front", answer = "Back", source = "Source" }, media_projection = "ordered-original-images.v1" } },
        timeout_seconds = 1,
        max_media_bytes = 1048576,
        provision_managed = true
    };
    await AtomicFile.WriteJsonOnceAsync(configPayloadPath, config);
    var configPayloadBytes = await File.ReadAllBytesAsync(configPayloadPath);
    var configPayloadSha = Sha(configPayloadBytes);
    var configManifestRelative = $"artifacts/{configArtifactId}/artifact.json";
    var configManifestPath = PathPolicy.ResolveJobRelative(packageRoot, configManifestRelative);
    await AtomicFile.WriteJsonOnceAsync(configManifestPath, new
    {
        schemaVersion = "1.0.0", artifactId = configArtifactId, kind = "anki-export-config", contractVersion = "1.0.0", mediaType = "application/vnd.rqc.anki-export-config+json",
        files = new[] { new { path = configPayloadRelative, sha256 = configPayloadSha, bytes = configPayloadBytes.LongLength, mediaType = "application/vnd.rqc.anki-export-config+json" } },
        createdAt = DateTimeOffset.UtcNow, producer = new { stageId = "desktop-settings", workerId = "desktop.settings", workerVersion = "1.0.0", attemptId = "settings-1" },
        inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = configPayloadRelative }
    });
    var configManifestSha = Sha(await File.ReadAllBytesAsync(configManifestPath));
    var taskId = "task-" + actionId;
    var taskManifestPath = Path.Combine(packageRoot, "task.manifest.v1.json");
    await AtomicFile.WriteJsonOnceAsync(taskManifestPath, new
    {
        schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "CoreAdapter", version = "2.9.0" }, intent = "settings.initialize-managed",
        executionProfile = "cards-anki", inputRefs = new[] { new { artifactRef = configArtifactId, kind = "anki-export-config", contractVersion = "1.0.0", expectedSha256 = configManifestSha } },
        requestedOutputs = new[] { "anki-managed-initialization-status" }, initialPlanRef = "plans/initial-plan.json", retentionClass = "evidence"
    });
    var authorization = new
    {
        schemaVersion = "1.0.0", authorizationId = "authorization-" + actionId, mode = explicitAuthorization ? "explicit-user-action" : "automatic",
        operation = "initialize-managed", scope = "managed-models-and-decks", taskId, configArtifactId, authorizedAt = DateTimeOffset.UtcNow
    };
    var requestPath = Path.Combine(attemptRoot, "request.json");
    await AtomicFile.WriteJsonOnceAsync(requestPath, new
    {
        schemaVersion = "1.0.0", requestId = "request-" + actionId, operationId = "operation-" + actionId, taskId, runId = "run-init", stageId = "anki-export", attemptId = "attempt-1",
        workerId = "export.anki", operation = "initialize-managed", attemptPath = attemptRelative,
        inputRefs = new[] { new { artifactRef = configArtifactId, kind = "anki-export-config", contractVersion = "1.0.0", manifestPath = configManifestRelative, expectedSha256 = configManifestSha } },
        outputPath = attemptRelative + "/outputs", resultPath = attemptRelative + "/result.json", progressPath = attemptRelative + "/progress.ndjson",
        cancelSignal = new { schemaVersion = "1.0.0", path = attemptRelative + "/cancel.json", gracePeriodMs = 2000 },
        idempotencyKey, configSnapshot = new { ankiConfigArtifactId = configArtifactId }, payload = new { sideEffectAuthorization = authorization }
    });
    var packageRef = $"module-actions/{actionId}/package";
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(actionRoot, "input.json"), new
    {
        schemaVersion = "anki-managed-initialization-action.v1", workerId = "export.anki", workerVersion = "1.4.0", packageRootRef = packageRef,
        taskManifestRef = packageRef + "/task.manifest.v1.json", taskManifestSha256 = Sha(await File.ReadAllBytesAsync(taskManifestPath)),
        requestRef = packageRef + "/" + attemptRelative + "/request.json", requestSha256 = Sha(await File.ReadAllBytesAsync(requestPath)),
        configManifestRef = packageRef + "/" + configManifestRelative, configManifestSha256 = configManifestSha,
        configPayloadRef = packageRef + "/" + configPayloadRelative, configPayloadSha256 = configPayloadSha,
        authorization
    });
    return new ModuleActionRequest("knowledge-clipper-module-action.v3", actionId, "anki.initialize-managed", $"module-actions/{actionId}/input.json", idempotencyKey);
}

static async Task<object> RunRemNoteTypedNonzeroModuleActionAsync(
    string projectRoot,
    string parentRoot,
    string fakeWorker,
    string state,
    int exitCode,
    bool expectAccepted)
{
    var root = Path.Combine(parentRoot, "remnote-nonzero-" + state);
    var jobs = Path.Combine(root, "jobs");
    Directory.CreateDirectory(jobs);
    var registry = Registry(fakeWorker, realRemNote: false);
    registry = registry with
    {
        Workers = registry.Workers.Select(worker => worker.WorkerId == "export.remnote"
            ? worker with { Entrypoint = new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", "export.remnote", "--remnote-probe-state", state, "--remnote-probe-exit", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) }) }
            : worker).ToArray()
    };
    var registryPath = await WriteJsonAsync(Path.Combine(root, "registry.json"), registry);
    var configPath = await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfigV2(true));
    var directJobs = Path.Combine(root, "direct-jobs");
    var directActionId = "direct-remnote-" + state;
    var directActionRoot = Path.Combine(directJobs, "module-actions", directActionId);
    Directory.CreateDirectory(directActionRoot);
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(directActionRoot, "input.json"), new
    {
        schemaVersion = "remnote-public-probe-request.v2", probeId = "direct-fake-probe-" + state,
        authorizationScope = "isolated-canary", timeoutMs = 15000,
        operations = new[] { "status", "search", "list", "read" },
        target = new { rootRemId = "fake-root", rootTitle = "Canary" }
    });
    var directReceipt = await new ModuleActionHandler(directJobs, registryPath, projectRoot).HandleAsync(
        new ModuleActionRequest("knowledge-clipper-module-action.v1", directActionId, "remnote.probe", $"module-actions/{directActionId}/input.json", new string(state == "malformed" ? 'a' : state[0], 64)),
        CancellationToken.None);
    Assert(directReceipt.Accepted == expectAccepted && directReceipt.ExternalWriteCount == 0,
        $"Direct RemNote {state} nonzero handler did not preserve the expected receipt.");
    var pipeName = "Rqc.CoreAdapter.RemNoteNonzero." + state + "." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    var hostTask = new OrchestratorHost(jobs, registryPath, projectRoot, 1, configPath).ServeAsync(IpcV12(pipeName), shutdown.Token);
    try
    {
        var actionId = "remnote-" + state;
        var actionRoot = Path.Combine(jobs, "module-actions", actionId);
        Directory.CreateDirectory(actionRoot);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(actionRoot, "input.json"), new
        {
            schemaVersion = "remnote-public-probe-request.v2", probeId = "fake-probe-" + state,
            authorizationScope = "isolated-canary", timeoutMs = 15000,
            operations = new[] { "status", "search", "list", "read" },
            target = new { rootRemId = "fake-root", rootTitle = "Canary" }
        });
        var request = new
        {
            protocolVersion = "1.2", correlationId = "module-remnote-" + state, messageType = "module-action",
            payload = new { schemaVersion = "knowledge-clipper-module-action.v1", actionId, operation = "remnote.probe", payloadRef = $"module-actions/{actionId}/input.json", idempotencyKey = new string(state == "malformed" ? 'a' : state[0], 64) }
        };
        var responseText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(request) + "\n");
        using var response = JsonDocument.Parse(responseText);
        Assert(response.RootElement.TryGetProperty("payload", out _), $"RemNote {state} nonzero action returned no receipt payload: {responseText}");
        var payload = response.RootElement.GetProperty("payload");
        Assert(response.RootElement.GetProperty("ok").GetBoolean() && payload.GetProperty("accepted").GetBoolean() == expectAccepted &&
               payload.GetProperty("externalWriteCount").GetInt32() == 0 && !payload.GetProperty("automaticRetryAllowed").GetBoolean(),
            $"RemNote {state} nonzero result did not preserve the expected fail-closed typed receipt.");
        var receiptPath = Path.Combine(actionRoot, "receipt.json");
        Assert(File.Exists(receiptPath), $"RemNote {state} nonzero result did not publish a durable receipt.");
        if (expectAccepted)
        {
            Assert(payload.GetProperty("status").GetString() == "completed", $"RemNote {state} typed result did not complete the module action.");
            var resultPath = PathPolicy.ResolveJobRelative(jobs, payload.GetProperty("outputRef").GetString()!);
            using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath));
            Assert(result.RootElement.GetProperty("state").GetString() == state &&
                   result.RootElement.GetProperty("admission").GetString() == "blocked" &&
                   result.RootElement.GetProperty("sanitized").GetBoolean() &&
                   result.RootElement.GetProperty("reasonCodes").GetArrayLength() == 1,
                $"RemNote {state} typed state/reasonCodes were not preserved.");
            using var replay = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(request) + "\n"));
            Assert(replay.RootElement.GetProperty("payload").GetProperty("outputSha256").GetString() == payload.GetProperty("outputSha256").GetString(),
                $"RemNote {state} typed receipt replay drifted.");
        }
        else
        {
            Assert(payload.GetProperty("status").GetString() == "failed" &&
                   payload.GetProperty("error").GetProperty("code").GetString() == "module.action_failed" &&
                   !File.Exists(Path.Combine(actionRoot, "result.json")),
                "Malformed nonzero RemNote output was accepted or persisted as typed state.");
        }
        return new { state, processExitCode = exitCode, accepted = expectAccepted, typedResultPreserved = expectAccepted, externalWrites = 0, replayStable = expectAccepted };
    }
    finally
    {
        shutdown.Cancel();
        try { await hostTask; } catch (OperationCanceledException) { }
    }
}

static async Task<object> RunRemNoteLosslessFailuresAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "remnote-lossless");
    Directory.CreateDirectory(root);
    var registry = Registry(fakeWorker, realRemNote: false);
    var config = DesktopIntentResolver.LoadConfiguration(await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfig(true)));
    var multiline = await CreateJobAsync(root, "task-multiline", "clip", "parity-remnote", "text", "MULTILINE_FIXTURE", false);
    var reserved = await CreateJobAsync(root, "task-reserved", "clip", "cards-remnote", "text", "RESERVED_FIXTURE", false);
    var multilineState = await RunDirectAsync(multiline, registry, config, projectRoot);
    var reservedState = await RunDirectAsync(reserved, registry, config, projectRoot);
    Assert(multilineState == "succeeded" && reservedState == "partial", "RemNote multiline projection or reserved-delimiter rejection drifted.");
    Assert(!HasError(multiline.JobRoot, "binding.remnote_inline_unsupported"), "Representable multiline learning text was rejected instead of projected as ordered v2 entries.");
    Assert(HasError(reserved.JobRoot, "binding.remnote_reserved_delimiter"), "Reserved RemNote delimiter did not emit the lossless error.");
    return new { name = "remnote-v2-lossless-boundary", status = "passed", multiline = "ordered-entry-projection", reservedDelimiter = "explicit-failure", silentNormalization = false };
}

static async Task<object> RunCompletedOutputGateAsync(string projectRoot, string testRoot, string fakeWorker)
{
    var root = Path.Combine(testRoot, "missing-output");
    Directory.CreateDirectory(root);
    var registry = Registry(fakeWorker, false, contentStatus: "missing-output");
    var config = DesktopIntentResolver.LoadConfiguration(await WriteJsonAsync(Path.Combine(root, "resolver.json"), ResolverConfig(true)));
    var job = await CreateJobAsync(root, "task-output-missing", "clip", "cards-remnote", "text", "ordinary", false);
    var state = await RunDirectAsync(job, registry, config, projectRoot);
    Assert(state == "failed" && HasError(job.JobRoot, "binding.completed_output_missing"), "Completed result without required outputs was consumable.");
    return new { name = "completed-required-output-gate", status = "passed", downstreamStarted = false };
}

static async Task<string> RunDirectAsync(Job job, WorkerRegistry registry, DestinationNeutralResolverConfiguration config, string projectRoot)
{
    var runId = "run-" + Guid.NewGuid().ToString("N");
    var plan = await new DesktopIntentResolver().ResolveAsync(job.JobRoot, FindIntentPath(job.JobRoot), registry, runId, config, CancellationToken.None);
    return await new OrchestratorEngine().RunAsync(job.JobRoot, plan, registry, projectRoot, runId, CancellationToken.None);
}

static Task ResolveAsync(Job job, WorkerRegistry registry, DestinationNeutralResolverConfiguration config, string projectRoot) =>
    new DesktopIntentResolver().ResolveAsync(job.JobRoot, FindIntentPath(job.JobRoot), registry, "run-resolve", config, CancellationToken.None);

static JsonElement ReadResolvedPlan(string jobRoot)
{
    var path = Directory.GetFiles(Path.Combine(jobRoot, "plans", "resolved"), "pipeline.v2.json", SearchOption.AllDirectories).Single();
    return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.Clone();
}

static string FindIntentPath(string jobRoot)
{
    var canonical = Path.Combine(jobRoot, "plans", "clip-intent.v2.json");
    return File.Exists(canonical) ? canonical : Path.Combine(jobRoot, "plans", "desktop-intent.v1.json");
}

static bool HasError(string jobRoot, string code) => Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories)
    .Select(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options))
    .Any(result => result?.Error?.Code == code);

static WorkerRegistry Registry(string fakeWorker, bool realRemNote, string? imageStatus = null, string? contentStatus = null)
{
    var authorityPath = Environment.GetEnvironmentVariable("RQC_AUTHORITY_REGISTRY");
    if (!string.IsNullOrWhiteSpace(authorityPath))
    {
        if (realRemNote)
            throw new InvalidOperationException("Authority-bound dispatcher Canary must not enable a real RemNote entrypoint.");
        var authority = new RegistryLoader().Load(Path.GetFullPath(authorityPath));
        var fakeEntries = authority.Workers.Select(worker =>
        {
            var forced = worker.WorkerId switch
            {
                "image.ocr.mineru" => imageStatus,
                "content.deepseek" => contentStatus,
                _ => null
            };
            var args = new[] { "--worker-id", worker.WorkerId }
                .Concat(forced is null ? Array.Empty<string>() : new[] { "--force-status", forced })
                .ToArray();
            return worker with { Entrypoint = new WorkerEntrypoint(fakeWorker, args) };
        }).ToArray();
        return authority with { Workers = fakeEntries };
    }

    WorkerEntry Fake(string id, string[] stages, string[] inputs, string[] outputs, string sideEffect, string idempotency, string? forced = null) =>
        new(id, ">=1.0.0 <2.0.0", new WorkerEntrypoint(fakeWorker, new[] { "--worker-id", id }.Concat(forced is null ? Array.Empty<string>() : new[] { "--force-status", forced }).ToArray()), stages, inputs, outputs,
            id switch { "export.anki" => new[] { "cards-anki" }, "export.remnote" => new[] { "parity-remnote", "cards-remnote" }, _ => new[] { "parity-remnote", "cards-remnote", "cards-anki" } }, sideEffect, idempotency, true);
    var entries = new List<WorkerEntry>
    {
        Fake("image.ocr.mineru", new[] { "image-ocr" }, new[] { "capture-bundle" }, new[] { "image.original", "image.original.bundle", "image.processing.provenance", "image-ocr.markdown", "image-ocr.structured", "image-ocr.image" }, "external", "reconcilable", imageStatus),
        Fake("content.deepseek", new[] { "content.understand-and-card" }, new[] { "content-input" }, new[] { "learning-bundle", "card-bundle", "language-practice-decision", "question.answer", "ocr-proofread-bundle" }, "local", "idempotent", contentStatus),
        Fake("speech.edge", new[] { "speech.synthesize", "speech.playback" }, new[] { "text.spoken", "audio.speech" }, new[] { "audio.speech" }, "local", "idempotent"),
        Fake("export.anki", new[] { "anki-export" }, new[] { "card-bundle", "learning-bundle", "anki-export-config" }, new[] { "anki-export-evidence", "anki-connection-status", "anki-managed-initialization-status" }, "external", "reconcilable")
    };
    entries.Add(realRemNote
        ? new WorkerEntry("export.remnote", ">=1.0.0 <2.0.0", new WorkerEntrypoint("workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe", Array.Empty<string>()), new[] { "export-remnote" }, new[] { "learning.bundle", "card.bundle" }, new[] { "remnote.write-receipt", "remnote.import-package" }, new[] { "parity-remnote", "cards-remnote" }, "external", "reconcilable", true)
        : Fake("export.remnote", new[] { "export-remnote" }, new[] { "learning.bundle", "card.bundle" }, new[] { "remnote.write-receipt", "remnote.import-package" }, "external", "reconcilable"));
    return new WorkerRegistry("1.0.0", "1.1.0-canary.1", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, entries);
}

static object ResolverConfig(bool remNoteEnabled) => new
{
    schemaVersion = "desktop-profile-resolver.v1", defaultProfile = "cards-remnote", profiles = new[] { "parity-remnote", "cards-remnote", "cards-anki" },
    remNoteExport = remNoteEnabled ? new { sink = "markdown-zip", rootRemId = "fake-root", rootTitle = "剪藏", documentTitlePrefix = "Core Adapter Canary" } : null,
    remNoteProjection = new
    {
        categoryRegistryVersion = "category-registry/1.0.0",
        templateMappings = new Dictionary<string, object>
        {
            ["basic_qa"] = new { policy = "project", cardType = "basic", direction = "forward" },
            ["media_qa"] = new { policy = "reject-media-deferred", cardType = (string?)null, direction = (string?)null },
            ["language_context"] = new { policy = "project", cardType = "basic", direction = "forward" },
            ["concept_transfer"] = new { policy = "project", cardType = "basic", direction = "forward" },
            ["procedure"] = new { policy = "project", cardType = "basic", direction = "forward" }
        },
        allowedTextBlockTypes = new[] { "text", "markdown" }, blockRendering = "single-block-verbatim-inline", assetRefPolicy = "reject-media-deferred"
    },
    ankiProjection = new
    {
        categoryRegistryVersion = "category-registry/1.0.0",
        categoryToDeckRoute = new Dictionary<string, string> { ["question_answer"] = "question", ["language_zh"] = "language.zh", ["language_en"] = "language.en", ["general_learning"] = "other" },
        templateMappings = new Dictionary<string, object>
        {
            ["basic_qa"] = new { policy = "project", noteTypeKey = "basic_qa", frontField = "question", backField = "answer" },
            ["media_qa"] = new { policy = "reject-media-deferred", noteTypeKey = (string?)null, frontField = (string?)null, backField = (string?)null },
            ["language_context"] = new { policy = "project", noteTypeKey = "language_item", frontField = "context_sentence", backField = "reading_and_meaning" },
            ["concept_transfer"] = new { policy = "project", noteTypeKey = "basic_qa", frontField = "question", backField = "answer" },
            ["procedure"] = new { policy = "project", noteTypeKey = "basic_qa", frontField = "question", backField = "answer" }
        },
        revisionPolicy = "initial-revision-1", contentHashPolicy = "sha256-canonical-projection-v1", tags = Array.Empty<string>(), assetRefPolicy = "reject-media-deferred"
    },
    speech = new { voicesByLanguage = new Dictionary<string, string> { ["zh-CN"] = "zh-CN-XiaoxiaoNeural" }, ratePercent = 0, volumePercent = 0, pitchHz = 0, edgeTtsPath = (string?)null, ffplayPath = (string?)null }
};

static object ResolverConfigV2(bool remNoteEnabled)
{
    var legacy = JsonSerializer.SerializeToElement(ResolverConfig(remNoteEnabled), ContractJson.Options);
    return new
    {
        schemaVersion = "knowledge-clipper-profile-resolver.v2",
        defaultProcessingProfile = "cards",
        processingProfiles = new[] { "learning", "cards" },
        defaultDestinations = Array.Empty<string>(),
        destinations = new[] { "remnote", "anki" },
        legacyProfileAliases = new Dictionary<string, object>
        {
            ["parity-remnote"] = new { processingProfile = "learning", destinations = new[] { "remnote" } },
            ["cards-remnote"] = new { processingProfile = "cards", destinations = new[] { "remnote" } },
            ["cards-anki"] = new { processingProfile = "cards", destinations = new[] { "anki" } }
        },
        destinationConfigurations = new
        {
            remnote = new { export = legacy.GetProperty("remNoteExport"), projection = legacy.GetProperty("remNoteProjection") },
            anki = new { projection = legacy.GetProperty("ankiProjection") }
        },
        speech = legacy.GetProperty("speech")
    };
}

static async Task<Job> CreateJobAsync(
    string jobsRoot,
    string taskId,
    string captureIntent,
    string profile,
    string imageHandling,
    string? text,
    bool includeAnkiConfig,
    string manifestVersion = "1.0.0",
    IReadOnlyList<string>? destinations = null,
    string? imagePayloadMarker = null)
{
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(Path.Combine(jobRoot, "plans"));
    var artifacts = new List<object>();
    var files = new List<(string Path, byte[] Bytes, string MediaType)>();
    string? selected = null;
    var questionImages = new List<string>();
    var answerImages = new List<string>();
    if (text is not null)
    {
        selected = "capture.text.selection";
        var relative = "inputs/selection.txt";
        var bytes = Encoding.UTF8.GetBytes(text);
        files.Add((relative, bytes, "text/plain"));
        artifacts.Add(new { artifactRef = selected, relativePath = relative, sha256 = Sha(bytes), bytes = bytes.LongLength, mediaType = "text/plain", encoding = "utf-8" });
    }
    else
    {
        foreach (var item in new[] { ("capture.image.question.0001", "question-0001.png", "question"), ("capture.image.question.0002", "question-0002.png", "question"), ("capture.image.answer.0001", "answer-0001.png", "answer") })
        {
            var relative = "inputs/images/" + item.Item2;
            var bytes = Encoding.UTF8.GetBytes(imagePayloadMarker ?? item.Item1);
            files.Add((relative, bytes, "image/png"));
            artifacts.Add(new { artifactRef = item.Item1, relativePath = relative, sha256 = Sha(bytes), bytes = bytes.LongLength, mediaType = "image/png", encoding = (string?)null });
            if (item.Item3 == "question") questionImages.Add(item.Item1); else answerImages.Add(item.Item1);
        }
    }
    var capture = new
    {
        schemaVersion = "capture-bundle.v1", captureId = "capture-" + taskId, createdAt = DateTimeOffset.UtcNow, intent = captureIntent,
        source = text is null ? "screenshot" : "selection",
        modeSnapshot = new { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling },
        artifacts, selectedTextArtifactRef = selected, contextSegments = (object?)null, questionImages, answerImages, questionTurns = Array.Empty<object>()
    };
    var captureBytes = JsonSerializer.SerializeToUtf8Bytes(capture, ContractJson.Options);
    files.Add(("inputs/capture-bundle.v1.json", captureBytes, "application/json"));
    var captureRef = await PublishInitialAsync(jobRoot, "capture-bundle-001", "capture-bundle", files, "inputs/capture-bundle.v1.json");
    var inputs = new List<ArtifactReference> { captureRef };
    if (includeAnkiConfig)
    {
        var configVersion = imageHandling == "original" ? "v2" : "v1";
        var configName = $"anki-export.{configVersion}.example.json";
        var configBytes = await File.ReadAllBytesAsync(Path.Combine(Directory.GetCurrentDirectory(), "workers", "anki-exporter", "config", configName));
        var inputName = $"inputs/anki-export.{configVersion}.json";
        inputs.Add(await PublishInitialAsync(jobRoot, "anki-export-config-001", "anki-export-config", new[] { (inputName, configBytes, "application/json") }, inputName));
    }
    var taskIntent = captureIntent == "read" ? "clip.read-aloud" : captureIntent == "question" ? "clip.question" : "clip.card";
    var requested = taskIntent == "clip.read-aloud"
        ? new[] { "card-bundle", "audio.speech" }
        : taskIntent == "clip.card" && profile is ("learning" or "parity-remnote") ? new[] { "learning-bundle" } : new[] { "card-bundle" };
    var canonicalV2 = manifestVersion == "2.0.0";
    var intentName = canonicalV2 ? "clip-intent.v2.json" : "desktop-intent.v1.json";
    var intent = new { schemaVersion = canonicalV2 ? "knowledge-clipper-intent.v2" : "desktop-intent.v1", taskId, intent = taskIntent, inputArtifactRef = captureRef.ArtifactRef, requestedOutputs = requested };
    await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "plans", intentName), intent);
    var inputRefs = inputs.Select(item => new { item.ArtifactRef, item.Kind, item.ContractVersion, expectedSha256 = item.ExpectedSha256 }).ToArray();
    if (canonicalV2)
    {
        var selectedDestinations = destinations?.ToArray() ?? Array.Empty<string>();
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v2.json"), new
        {
            schemaVersion = "2.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "KnowledgeClipper", version = "2.0.0" }, intent = taskIntent,
            executionProfile = profile,
            destinations = selectedDestinations,
            inputRefs,
            requestedOutputs = requested, initialPlanRef = "plans/clip-intent.v2.json", retentionClass = "ephemeral"
        });
        var legacyProfile = selectedDestinations.Contains("anki", StringComparer.Ordinal) ? "cards-anki" : profile == "learning" ? "parity-remnote" : "cards-remnote";
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "KnowledgeClipper-legacy-manifest-alias", version = "2.0.0" }, intent = taskIntent,
            executionProfile = legacyProfile,
            inputRefs,
            requestedOutputs = requested, initialPlanRef = "plans/clip-intent.v2.json", retentionClass = "ephemeral",
            metadata = new { legacyCompatibilityAlias = true, canonicalManifestRef = "task.manifest.v2.json" }
        });
    }
    else
    {
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow, createdBy = new { application = "core-adapter-integration", version = "1.0.0" }, intent = taskIntent,
            executionProfile = profile,
            inputRefs,
            requestedOutputs = requested, initialPlanRef = "plans/desktop-intent.v1.json", retentionClass = "ephemeral"
        });
    }
    return new Job(taskId, jobRoot);
}

static async Task<ArtifactReference> PublishInitialAsync(string jobRoot, string artifactId, string kind, IEnumerable<(string Path, byte[] Bytes, string MediaType)> payloads, string entrypoint)
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
        producer = new { stageId = "desktop-capture", workerId = "desktop.capture", workerVersion = "1.0.0", attemptId = "capture-attempt-001" }, inputRefs = Array.Empty<object>(), dataClass = "sensitive", metadata = new { entrypoint }
    });
    var bytes = await File.ReadAllBytesAsync(manifestPath);
    return new ArtifactReference(artifactId, kind, "1.0.0", manifestRelative, Sha(bytes));
}

static async Task SubmitAsync(string pipeName, string taskId)
{
    var command = new
    {
        protocolVersion = "1.0", correlationId = "correlation-" + taskId, messageType = "task-command",
        payload = new { schemaVersion = "1.0.0", commandId = "command-" + taskId, operationId = "operation-" + taskId, taskId = (string?)null, type = "task.submit", issuedAt = DateTimeOffset.UtcNow, idempotencyKey = Sha(Encoding.UTF8.GetBytes(taskId)), payloadRef = taskId + "/task.manifest.v1.json" }
    };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(command, IpcJson.Options) + "\n"));
    Assert(response.RootElement.GetProperty("ok").GetBoolean() && response.RootElement.GetProperty("payload").GetProperty("accepted").GetBoolean(), $"Submit was rejected: {taskId}");
}

static async Task ProvideInputAsync(string pipeName, string taskId, string actionId, string payloadRef)
{
    var command = new
    {
        protocolVersion = "1.0",
        correlationId = $"correlation-{taskId}-{actionId}",
        messageType = "task-command",
        payload = new
        {
            schemaVersion = "1.0.0",
            commandId = $"command-{taskId}-{actionId}",
            operationId = $"operation-{taskId}-{actionId}",
            taskId,
            type = "task.provide_input",
            issuedAt = DateTimeOffset.UtcNow,
            idempotencyKey = Sha(Encoding.UTF8.GetBytes($"{taskId}:{actionId}")),
            payloadRef
        }
    };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(command, IpcJson.Options) + "\n"));
    Assert(response.RootElement.GetProperty("ok").GetBoolean() && response.RootElement.GetProperty("payload").GetProperty("accepted").GetBoolean(),
        $"Playback control was rejected: {actionId}");
}

static async Task<JsonElement> StageControlAsync(string pipeName, string jobRoot, string taskId, string actionId)
{
    var command = new
    {
        protocolVersion = "1.1",
        correlationId = $"correlation-{taskId}-{actionId}",
        messageType = "stage-control",
        payload = new
        {
            taskId,
            operation = "speech.playback",
            selector = "current-active",
            reason = "user_stop"
        }
    };
    using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(command, IpcJson.Options) + "\n"));
    Assert(response.RootElement.GetProperty("ok").GetBoolean(), $"Stage control protocol failed: {actionId}");
    return response.RootElement.GetProperty("payload").Clone();
}

static async Task<string> WaitTerminalAsync(string pipeName, string taskId)
{
    for (var index = 0; index < 300; index++)
    {
        var query = new { protocolVersion = "1.0", correlationId = $"query-{taskId}-{index}", messageType = "task-query", payload = new { schemaVersion = "1.0.0", queryId = $"query-{taskId}-{index}", type = "task.get", taskId } };
        using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(query, IpcJson.Options) + "\n"));
        var items = response.RootElement.GetProperty("payload").GetProperty("items");
        if (items.GetArrayLength() == 1)
        {
            var status = items[0].GetProperty("status").GetString()!;
            if (status is "succeeded" or "partial" or "failed" or "cancelled") return status;
        }
        await Task.Delay(50);
    }
    throw new TimeoutException($"Task did not become terminal: {taskId}");
}

static async Task<JsonElement> WaitProjectedStatusV23Async(string pipeName, string taskId, string expectedStatus)
{
    for (var index = 0; index < 300; index++)
    {
        var query = new { protocolVersion = "1.0", correlationId = $"query-v23-{taskId}-{expectedStatus}-{index}", messageType = "task-query", payload = new { schemaVersion = "2.3.0", queryId = $"query-v23-{taskId}-{index}", type = "task.get", taskId } };
        using var response = JsonDocument.Parse(await RoundTripAsync(pipeName, JsonSerializer.Serialize(query, IpcJson.Options) + "\n"));
        var items = response.RootElement.GetProperty("payload").GetProperty("items");
        if (items.GetArrayLength() == 1 && items[0].GetProperty("status").GetString() == expectedStatus)
            return items[0].Clone();
        await Task.Delay(50);
    }
    throw new TimeoutException($"Task did not reach {expectedStatus}: {taskId}");
}

static async Task<string> PublishOcrCorrectionActionAsync(
    string jobRoot,
    string taskId,
    string actionId,
    string targetToken,
    string candidateText,
    string correctedText)
{
    var commandId = $"command-{taskId}-{actionId}";
    var artifactId = $"ocr.human-correction.{actionId}";
    var payloadPath = $"commands/{commandId}/ocr-correction/payload.txt";
    var correctionBytes = new UTF8Encoding(false).GetBytes(correctedText);
    var payloadAbsolute = PathPolicy.ResolveJobRelative(jobRoot, payloadPath);
    Directory.CreateDirectory(Path.GetDirectoryName(payloadAbsolute)!);
    await File.WriteAllBytesAsync(payloadAbsolute, correctionBytes);
    var manifestPath = $"artifacts/{artifactId}/artifact.json";
    var manifestAbsolute = PathPolicy.ResolveJobRelative(jobRoot, manifestPath);
    await AtomicFile.WriteJsonOnceAsync(manifestAbsolute, new
    {
        schemaVersion = "1.0.0", artifactId, kind = "ocr.human-correction.text", contractVersion = "1.0.0", mediaType = "text/plain",
        files = new[] { new { path = payloadPath, sha256 = Sha(correctionBytes), bytes = correctionBytes.LongLength, mediaType = "text/plain" } },
        createdAt = DateTimeOffset.UtcNow,
        producer = new { stageId = "desktop-ocr-human-resolution", workerId = "desktop-capture", workerVersion = "1.0.0", attemptId = $"attempt-{actionId}" },
        inputRefs = Array.Empty<object>(), dataClass = "sensitive"
    });
    var manifestBytes = await File.ReadAllBytesAsync(manifestAbsolute);
    var actionPath = $"commands/{commandId}/ocr-human-resolution-action.v1.json";
    await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, actionPath), new
    {
        schemaVersion = "knowledge-clipper-ocr-human-resolution-action.v1", decisionKind = "ocr-human-resolution", targetToken,
        action = "submit_correction", reason = "The user supplied an exact correction.",
        correctionRef = new { artifactRef = artifactId, kind = "ocr.human-correction.text", contractVersion = "1.0.0", manifestPath, expectedSha256 = Sha(manifestBytes) },
        diffs = new[] { new { segmentIndex = 0, operation = "substitution", start = 0, end = candidateText.Length, before = candidateText, after = correctedText } }
    });
    return actionPath;
}

static async Task<string> RoundTripAsync(string pipeName, string request)
{
    Exception? last = null;
    for (var retry = 0; retry < 100; retry++)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(500);
            await client.WriteAsync(new UTF8Encoding(false).GetBytes(request));
            await client.FlushAsync();
            using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, true);
            return await reader.ReadLineAsync() ?? throw new InvalidDataException("No IPC response.");
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            last = exception;
            await Task.Delay(25);
        }
    }
    throw new InvalidOperationException("IPC connection failed.", last);
}

static IpcConfiguration Ipc(string pipeName) => new("1.1.0", "windows-named-pipe", pipeName, $"\\\\.\\pipe\\{pipeName}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query", "stage-control" }, true, "job-event-sequence", 1048576,
    new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"),
    "schemas/ipc-request.v1.1.schema.json", "schemas/ipc-response.v1.1.schema.json", "respond-when-possible-then-close");

static IpcConfiguration IpcV12(string pipeName) => new("1.2.0", "windows-named-pipe", pipeName, $"\\\\.\\pipe\\{pipeName}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query", "stage-control", "module-action" }, true, "job-event-sequence", 1048576,
    new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"),
    "schemas/ipc-request.v1.2.schema.json", "schemas/ipc-response.v1.2.schema.json", "respond-when-possible-then-close");

static IpcConfiguration IpcV13(string pipeName) => new("1.3.0", "windows-named-pipe", pipeName, $"\\\\.\\pipe\\{pipeName}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query", "stage-control", "module-action" }, true, "job-event-sequence", 1048576,
    new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"),
    "schemas/ipc-request.v1.3.schema.json", "schemas/ipc-response.v1.3.schema.json", "respond-when-possible-then-close");

static IpcConfiguration IpcV14(string pipeName) => new("1.4.0", "windows-named-pipe", pipeName, $"\\\\.\\pipe\\{pipeName}", "orchestrator-exclusive", "current-windows-user", new[] { "task-command", "task-query", "stage-control", "module-action" }, true, "job-event-sequence", 1048576,
    new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"),
    "schemas/ipc-request.v1.4.schema.json", "schemas/ipc-response.v1.4.schema.json", "respond-when-possible-then-close");

static async Task<string> ValidateWithRealAnkiLoaderAsync(string projectRoot, string jobRoot, string requestPath)
{
    var python = "C:\\Users\\fy\\.cache\\codex-runtimes\\codex-primary-runtime\\dependencies\\python\\python.exe";
    if (!File.Exists(python)) python = "python";
    var script = "import sys; from pathlib import Path; sys.path.insert(0,sys.argv[1]); import anki_exporter as a; root=Path(sys.argv[2]); rp=Path(sys.argv[3]); task=a.load_json(root/'task.manifest.v1.json'); req=a.load_json(rp); a.validate_worker_request(req,task,rp.relative_to(root).as_posix()); a.load_package(root,task,req,rp); print('ANKI_STRICT_LOAD_OK')";
    var start = new ProcessStartInfo { FileName = python, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { "-c", script, Path.Combine(projectRoot, "workers", "anki-exporter"), jobRoot, requestPath }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start real Anki loader.");
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Assert(process.ExitCode == 0, "Real Anki loader failed: " + stderr);
    return stdout.Trim();
}

static async Task ExpectCodeAsync(string code, Func<Task> action)
{
    try { await action(); }
    catch (OrchestratorDispatchException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException("Expected dispatch code was not thrown: " + code);
}

static async Task<string> WriteJsonAsync(string path, object value)
{
    await AtomicFile.WriteJsonOnceAsync(path, value);
    return path;
}

static string ReadLatestRunEvents(string jobRoot)
{
    var runs = Directory.Exists(Path.Combine(jobRoot, "runs")) ? Directory.GetDirectories(Path.Combine(jobRoot, "runs")) : Array.Empty<string>();
    if (runs.Length == 0) return "no run events";
    var events = new EventStore(jobRoot, Path.GetFileName(runs.OrderBy(path => path, StringComparer.Ordinal).Last())).ReadAll();
    return JsonSerializer.Serialize(events.TakeLast(6), ContractJson.Options);
}

static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
sealed record Job(string TaskId, string JobRoot);

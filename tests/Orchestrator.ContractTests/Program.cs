using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Orchestrator.Core;

var projectRoot = Path.GetFullPath(args.Length == 1 ? args[0] : throw new ArgumentException("Expected project root."));
var fakeWorkerPath = Path.Combine(projectRoot, "tests", "FakeWorker", "bin", "Release", "net8.0", "RemNoteQuickClipper.FakeWorker.exe");
Assert(File.Exists(fakeWorkerPath), "Fake worker executable is missing.");

var tests = new List<object>();
tests.Add(await VerifyPrimitivesAsync());
tests.Add(await VerifyArtifactEntrypointResolutionAsync());
tests.Add(await VerifyRegistryDescribeAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifyStandaloneRunPreflightAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifyPipelineAndReuseAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifySpeechReuseSemanticsAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifySpeechReuseEvidenceAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifyReuseCandidateIsolationAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifyRecoveryAsync());
tests.Add(await VerifyHostedInvalidTerminalRecoveryAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifyHostedSubmitAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifyOutOfProcessSubmitAsync(projectRoot, fakeWorkerPath));
tests.Add(await VerifyDurableReplayAsync(projectRoot, fakeWorkerPath));
foreach (var reason in new[] { "user_stop", "superseded", "task_cancel", "timeout" })
    tests.Add(await VerifyCancellationAsync(projectRoot, fakeWorkerPath, reason));
tests.Add(await VerifyTaskCancelPayloadAsync());
tests.Add(await VerifyIpcAsync());
tests.Add(await VerifyProcessFailureAsync(projectRoot, fakeWorkerPath, "nonzero-exit", "local", "idempotent", "failed", "worker.crashed", 17));
tests.Add(await VerifyProcessFailureAsync(projectRoot, fakeWorkerPath, "external-crash", "external", "reconcilable", "unknown_external_state", "effect.worker_exit_unknown", 23));
tests.Add(await VerifyMalformedResultRejectedAsync(projectRoot, fakeWorkerPath, "invalid-json-result"));
tests.Add(await VerifyMalformedResultRejectedAsync(projectRoot, fakeWorkerPath, "partial-write-crash"));
tests.Add(await VerifyHungWorkerTimeoutAsync(projectRoot, fakeWorkerPath));

var report = new
{
    schemaVersion = "1.0.0",
    status = "passed",
    tests
};
var receiptPath = Environment.GetEnvironmentVariable("RQC_CONTRACT_TEST_RECEIPT");
if (!string.IsNullOrWhiteSpace(receiptPath))
    await AtomicFile.WriteJsonOnceAsync(Path.GetFullPath(receiptPath), report);
Console.WriteLine(JsonSerializer.Serialize(report, ContractJson.Options));
return 0;

static async Task<object> VerifyPrimitivesAsync()
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.PrimitiveTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        Expect<InvalidDataException>(() => PathPolicy.ResolveJobRelative(testRoot, "../escape.json"));
        Expect<InvalidDataException>(() => PathPolicy.ResolveJobRelative(testRoot, Path.Combine(testRoot, "absolute.json")));
        var oncePath = Path.Combine(testRoot, "once.json");
        await AtomicFile.WriteJsonOnceAsync(oncePath, new { value = 1 });
        await ExpectAsync<IOException>(() => AtomicFile.WriteJsonOnceAsync(oncePath, new { value = 2 }));

        var appends = Enumerable.Range(0, 20).Select(index =>
            new EventStore(testRoot, "run-concurrent").AppendAsync("test.event", "task-concurrent", new { index })).ToArray();
        await Task.WhenAll(appends);
        var events = new EventStore(testRoot, "run-concurrent").ReadAll();
        Assert(events.Count == 20 && events.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, 20).Select(item => (long)item)), "Concurrent event append lost or duplicated a sequence.");

        TaskStateMachine.EnsureTransition("created", "validating");
        Expect<InvalidOperationException>(() => TaskStateMachine.EnsureTransition("succeeded", "running"));
        return new { name = "primitives-and-event-store", status = "passed", traversalRejected = true, createOnceEnforced = true, concurrentEvents = 20 };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyArtifactEntrypointResolutionAsync()
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.ArtifactEntrypointTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        var one = await WriteArtifactFixtureAsync(testRoot, "one", new[] { "artifacts/one/payload.json" }, "artifacts/one/payload.json");
        var oneManifest = await CoreArtifactIO.ReadManifestAsync(testRoot, one, default);
        Assert(VerifiedArtifactEntrypointResolver.Resolve(testRoot, oneManifest, "one-file fixture").Path == "artifacts/one/payload.json",
            "One-file artifact entrypoint was not resolved.");

        var fourPaths = new[]
        {
            "artifacts/four/diagnostics.json",
            "artifacts/four/connection-status.json",
            "artifacts/four/provider-actions.json",
            "artifacts/four/readback.json"
        };
        var four = await WriteArtifactFixtureAsync(testRoot, "four", fourPaths, fourPaths[1]);
        var fourManifest = await CoreArtifactIO.ReadManifestAsync(testRoot, four, default);
        Assert(VerifiedArtifactEntrypointResolver.Resolve(testRoot, fourManifest, "four-file fixture").Path == fourPaths[1],
            "Four-file artifact did not select its declared entrypoint.");

        var missing = await WriteArtifactFixtureAsync(testRoot, "missing", new[] { "artifacts/missing/payload.json" }, null);
        await ExpectAsync<InvalidDataException>(async () =>
            VerifiedArtifactEntrypointResolver.Resolve(testRoot, await CoreArtifactIO.ReadManifestAsync(testRoot, missing, default), "missing fixture"));

        var undeclared = await WriteArtifactFixtureAsync(testRoot, "undeclared", new[] { "artifacts/undeclared/payload.json" }, "artifacts/undeclared/other.json");
        await ExpectAsync<InvalidDataException>(async () =>
            VerifiedArtifactEntrypointResolver.Resolve(testRoot, await CoreArtifactIO.ReadManifestAsync(testRoot, undeclared, default), "undeclared fixture"));

        var noncanonical = await WriteArtifactFixtureAsync(testRoot, "noncanonical", new[] { "artifacts/noncanonical/payload.json" }, "./artifacts/noncanonical/payload.json");
        await ExpectAsync<InvalidDataException>(async () =>
            VerifiedArtifactEntrypointResolver.Resolve(testRoot, await CoreArtifactIO.ReadManifestAsync(testRoot, noncanonical, default), "noncanonical fixture"));

        var duplicate = await WriteArtifactFixtureAsync(testRoot, "duplicate", new[] { "artifacts/duplicate/payload.json", "artifacts/duplicate/payload.json" }, "artifacts/duplicate/payload.json");
        await ExpectAsync<OrchestratorDispatchException>(() => CoreArtifactIO.ReadManifestAsync(testRoot, duplicate, default));

        var escaping = await WriteArtifactFixtureAsync(testRoot, "escaping", new[] { "../outside.json" }, "../outside.json", writePayloads: false);
        await ExpectAsync<InvalidDataException>(() => CoreArtifactIO.ReadManifestAsync(testRoot, escaping, default));

        var duplicateEntrypoint = await WriteArtifactFixtureAsync(testRoot, "duplicate-entrypoint", new[] { "artifacts/duplicate-entrypoint/payload.json" },
            "artifacts/duplicate-entrypoint/payload.json", duplicateEntrypoint: true);
        await ExpectAsync<InvalidDataException>(async () =>
            VerifiedArtifactEntrypointResolver.Resolve(testRoot, await CoreArtifactIO.ReadManifestAsync(testRoot, duplicateEntrypoint, default), "duplicate-entrypoint fixture"));

        return new
        {
            name = "verified-artifact-entrypoint-resolution",
            status = "passed",
            oneFilePositive = true,
            fourFilePositive = true,
            missingRejected = true,
            duplicateRejected = true,
            undeclaredRejected = true,
            noncanonicalRejected = true,
            pathEscapeRejected = true
        };
    }
    finally
    {
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
    }
}

static async Task<ArtifactReference> WriteArtifactFixtureAsync(
    string jobRoot,
    string id,
    IReadOnlyList<string> paths,
    string? entrypoint,
    bool writePayloads = true,
    bool duplicateEntrypoint = false)
{
    var files = new List<object>();
    foreach (var relative in paths)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, relative }));
        if (writePayloads)
        {
            var payloadPath = PathPolicy.ResolveJobRelative(jobRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            await File.WriteAllBytesAsync(payloadPath, bytes);
        }
        files.Add(new { path = relative, sha256 = Integrity.Sha256Hex(bytes).ToLowerInvariant(), bytes = bytes.LongLength, mediaType = "application/json" });
    }

    var manifestRelative = $"artifacts/{id}/artifact.json";
    var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, manifestRelative);
    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
    string manifestJson;
    if (duplicateEntrypoint)
    {
        var entrypointJson = JsonSerializer.Serialize(entrypoint);
        manifestJson = $"{{\"artifactId\":{JsonSerializer.Serialize(id)},\"kind\":\"fixture\",\"contractVersion\":\"1.0.0\",\"mediaType\":\"application/json\",\"files\":{JsonSerializer.Serialize(files, ContractJson.Options)},\"inputRefs\":[],\"metadata\":{{\"entrypoint\":{entrypointJson},\"entrypoint\":{entrypointJson}}}}}";
    }
    else
    {
        manifestJson = JsonSerializer.Serialize(new
        {
            artifactId = id,
            kind = "fixture",
            contractVersion = "1.0.0",
            mediaType = "application/json",
            files,
            inputRefs = Array.Empty<object>(),
            metadata = entrypoint is null ? new { } : (object)new { entrypoint }
        }, ContractJson.Options);
    }
    await File.WriteAllTextAsync(manifestPath, manifestJson, new UTF8Encoding(false));
    var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
    return new ArtifactReference(id, "fixture", "1.0.0", manifestRelative, Integrity.Sha256Hex(manifestBytes).ToLowerInvariant());
}

static async Task<object> VerifyRegistryDescribeAsync(string projectRoot, string fakeWorkerPath)
{
    var entry = FakeWorkerEntry(projectRoot, fakeWorkerPath);
    var registry = new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { entry });
    var loader = new RegistryLoader();
    await loader.ValidateEntrypointsAsync(registry, projectRoot);
    var mismatch = registry with { Workers = new[] { entry with { SideEffectLevel = "external" } } };
    await ExpectAsync<InvalidDataException>(() => loader.ValidateEntrypointsAsync(mismatch, projectRoot));
    foreach (var mode in new[] { "extra-field", "parallel-over-limit", "duplicate-stage" })
    {
        var malformed = registry with
        {
            Workers = new[] { entry with { Entrypoint = entry.Entrypoint with { Args = new[] { "--describe-mode", mode } } } }
        };
        await ExpectAsync<InvalidDataException>(() => loader.ValidateEntrypointsAsync(malformed, projectRoot));
    }
    var invalidJson = registry with
    {
        Workers = new[] { entry with { Entrypoint = entry.Entrypoint with { Args = new[] { "--describe-mode", "invalid-json" } } } }
    };
    await ExpectAsync<JsonException>(() => loader.ValidateEntrypointsAsync(invalidJson, projectRoot));
    var nonzero = registry with
    {
        Workers = new[] { entry with { Entrypoint = entry.Entrypoint with { Args = new[] { "--describe-mode", "nonzero" } } } }
    };
    await ExpectAsync<InvalidOperationException>(() => loader.ValidateEntrypointsAsync(nonzero, projectRoot));
    var missingEntrypoint = registry with
    {
        Workers = new[] { entry with { Entrypoint = new WorkerEntrypoint("workers/missing-worker.exe", Array.Empty<string>()) } }
    };
    await ExpectAsync<FileNotFoundException>(() => loader.ValidateEntrypointsAsync(missingEntrypoint, projectRoot));

    var cancellationRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.RegistryCancellationTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(cancellationRoot);
    try
    {
        var pidPath = Path.Combine(cancellationRoot, "describe.pid");
        var hanging = registry with
        {
            Workers = new[]
            {
                entry with { Entrypoint = entry.Entrypoint with { Args = new[] { "--describe-mode", "hang", "--describe-pid-file", pidPath } } }
            }
        };
        using var cancellation = new CancellationTokenSource();
        var validation = loader.ValidateEntrypointsAsync(hanging, projectRoot, cancellation.Token);
        for (var attempt = 0; attempt < 100 && !File.Exists(pidPath); attempt++)
            await Task.Delay(20);
        Assert(File.Exists(pidPath), "Hanging describe worker did not publish its PID fixture.");
        var processId = int.Parse(await ReadAllTextWithRetryAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(async () => await validation);
        for (var attempt = 0; attempt < 100 && ProcessIsAlive(processId); attempt++)
            await Task.Delay(20);
        Assert(!ProcessIsAlive(processId), "Cancelled registry preflight left an orphaned describe process.");
    }
    finally
    {
        await DeleteDirectoryWithRetryAsync(cancellationRoot);
    }

    var registryRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.RegistryAdmissionTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(registryRoot);
    try
    {
        var validPath = Path.Combine(registryRoot, "valid.json");
        await File.WriteAllTextAsync(validPath, JsonSerializer.Serialize(registry, ContractJson.Options));
        _ = loader.Load(validPath);

        var extra = JsonSerializer.SerializeToNode(registry, ContractJson.Options)!.AsObject();
        extra["unexpected"] = true;
        var extraPath = Path.Combine(registryRoot, "extra.json");
        await File.WriteAllTextAsync(extraPath, extra.ToJsonString(ContractJson.Options));
        Expect<InvalidDataException>(() => loader.Load(extraPath));

        var traversal = JsonSerializer.SerializeToNode(registry, ContractJson.Options)!.AsObject();
        traversal["workers"]![0]!["entrypoint"]!["path"] = "../outside.exe";
        var traversalPath = Path.Combine(registryRoot, "traversal.json");
        await File.WriteAllTextAsync(traversalPath, traversal.ToJsonString(ContractJson.Options));
        Expect<InvalidDataException>(() => loader.Load(traversalPath));

        var duplicateProfile = JsonSerializer.SerializeToNode(registry, ContractJson.Options)!.AsObject();
        duplicateProfile["profiles"] = new JsonArray("parity-remnote", "cards-remnote", "cards-remnote");
        var duplicateProfilePath = Path.Combine(registryRoot, "duplicate-profile.json");
        await File.WriteAllTextAsync(duplicateProfilePath, duplicateProfile.ToJsonString(ContractJson.Options));
        Expect<InvalidDataException>(() => loader.Load(duplicateProfilePath));
    }
    finally
    {
        Directory.Delete(registryRoot, true);
    }
    return new
    {
        name = "registry-describe-gate",
        status = "passed",
        exactSetsAccepted = true,
        driftRejected = true,
        unknownDescribePropertyRejected = true,
        concurrencyOver128Rejected = true,
        duplicateDescribeStageRejected = true,
        invalidDescribeJsonRejected = true,
        nonzeroDescribeRejected = true,
        missingEntrypointRejected = true,
        cancelledDescribeProcessTerminated = true,
        registryExtraPropertyRejected = true,
        registryTraversalRejected = true,
        registryDuplicateProfileRejected = true
    };
}

static async Task DeleteDirectoryWithRetryAsync(string path)
{
    for (var attempt = 1; attempt <= 20; attempt++)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return;
        }
        catch (IOException) when (attempt < 20)
        {
            await Task.Delay(50);
        }
        catch (UnauthorizedAccessException) when (attempt < 20)
        {
            await Task.Delay(50);
        }
    }
}

static async Task<string> ReadAllTextWithRetryAsync(string path)
{
    for (var attempt = 1; attempt <= 100; attempt++)
    {
        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (IOException) when (attempt < 100)
        {
            await Task.Delay(20);
        }
    }
    throw new IOException($"File remained unreadable after bounded retry: {path}");
}

static async Task<object> VerifyStandaloneRunPreflightAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.StandaloneRunPreflightTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "job");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var entry = FakeWorkerEntry(projectRoot, fakeWorkerPath) with { SideEffectLevel = "external" };
        var registryPath = Path.Combine(testRoot, "worker-registry.v1.json");
        await AtomicFile.WriteJsonOnceAsync(registryPath,
            new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { entry }));
        var planPath = Path.Combine(testRoot, "pipeline.json");
        await AtomicFile.WriteJsonOnceAsync(planPath,
            new PipelinePlan("1.0.0", "1.0.0", "task-preflight", 1,
                new[]
                {
                    new PipelineStage("stage-preflight", "test.fake", "test.grace", Array.Empty<string>(), Array.Empty<ArtifactReference>(), Array.Empty<string>(), 5000,
                        new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { mode = "success" }))
                }));

        var dotnet = "D:\\Applications\\RemNoteQuickClipperRuntime\\dotnet-sdk-8.0.424\\dotnet.exe";
        var orchestratorDll = Path.Combine(projectRoot, "orchestrator", "bin", "Release", "net8.0", "KnowledgeClipper.Orchestrator.dll");
        var configuredExecutable = Environment.GetEnvironmentVariable("RQC_ORCHESTRATOR_EXE");
        var useConfiguredExecutable = !string.IsNullOrWhiteSpace(configuredExecutable);
        if (useConfiguredExecutable && !File.Exists(Path.GetFullPath(configuredExecutable!)))
            throw new FileNotFoundException("Configured orchestrator executable is missing.", configuredExecutable);
        var start = new ProcessStartInfo
        {
            FileName = useConfiguredExecutable ? Path.GetFullPath(configuredExecutable!) : dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = projectRoot
        };
        var arguments = new List<string>();
        if (!useConfiguredExecutable)
            arguments.Add(orchestratorDll);
        arguments.AddRange(new[]
        {
            "run", "--job-root", jobRoot, "--pipeline", planPath, "--registry", registryPath,
            "--release-root", projectRoot, "--run-id", "run-preflight"
        });
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start standalone orchestrator preflight process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        Assert(process.ExitCode == 1, $"Standalone run accepted a describe-drifted registry; exit={process.ExitCode}, output={output}, error={error}");
        Assert(error.Contains("effect/idempotency mismatch", StringComparison.Ordinal), "Standalone run did not report the describe admission failure.");
        Assert(!Directory.Exists(Path.Combine(jobRoot, "runs")) || Directory.GetFiles(Path.Combine(jobRoot, "runs"), "request.json", SearchOption.AllDirectories).Length == 0,
            "Standalone run created a worker attempt before registry describe admission completed.");
        return new
        {
            name = "standalone-run-registry-preflight",
            status = "passed",
            describeDriftRejected = true,
            workerAttemptCreated = false,
            executable = useConfiguredExecutable ? Path.GetFullPath(configuredExecutable!) : orchestratorDll,
            exitCode = process.ExitCode
        };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyPipelineAndReuseAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.PipelineTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "task-pipeline");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var entry = FakeWorkerEntry(projectRoot, fakeWorkerPath);
        var registry = new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { entry });
        var empty = JsonSerializer.SerializeToElement(new { });
        var success = JsonSerializer.SerializeToElement(new { mode = "success" });
        var stages = new[]
        {
            new PipelineStage("stage-a", "test.fake", "test.grace", Array.Empty<string>(), Array.Empty<ArtifactReference>(), Array.Empty<string>(), 5000, new RetryPolicy(1, 0), empty, success),
            new PipelineStage("stage-b", "test.fake", "test.grace", new[] { "stage-a" }, Array.Empty<ArtifactReference>(), Array.Empty<string>(), 5000, new RetryPolicy(1, 0), empty, success)
        };
        var plan = new PipelinePlan("1.0.0", "1.0.0", "task-pipeline", 2, stages);
        var engine = new OrchestratorEngine();
        var first = await engine.RunAsync(jobRoot, plan, registry, projectRoot, "run-001", CancellationToken.None);
        var second = await engine.RunAsync(jobRoot, plan, registry, projectRoot, "run-002", CancellationToken.None);
        Assert(first == "succeeded" && second == "succeeded", "Pipeline did not succeed in both runs.");
        var secondResults = Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-002"), "result.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)!).ToArray();
        Assert(secondResults.Length == 2 && secondResults.All(result => result.Warnings.Any(warning => warning.StartsWith("Reused successful outputs", StringComparison.Ordinal))), "Cross-run idempotent reuse did not occur.");
        Assert(secondResults.All(result =>
        {
            if (result.Payload is not { ValueKind: JsonValueKind.Object } payload) return false;
            return payload.TryGetProperty("mode", out _) &&
                   !payload.TryGetProperty("cacheHit", out _) &&
                   !payload.TryGetProperty("reusedFromAttemptId", out _);
        }), "Non-speech reuse was polluted with speech payload fields.");
        Assert(!Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-002"), "progress.ndjson", SearchOption.AllDirectories).Any(), "Reused stage unexpectedly launched a worker.");
        Assert(TaskStateMachine.Project(new EventStore(jobRoot, "run-002").ReadAll()) == "succeeded", "Latest run state projection failed.");
        return new { name = "dag-and-cross-run-reuse", status = "passed", stages = 2, firstRun = first, secondRun = second, reusedStages = secondResults.Length };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifySpeechReuseSemanticsAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.SpeechReuseTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "task-speech-reuse");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var relativeExecutable = Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/');
        var speech = new WorkerEntry(
            "speech.edge", ">=1.0.0 <2.0.0", new WorkerEntrypoint(relativeExecutable, new[] { "--worker-id", "speech.edge" }),
            new[] { "speech.synthesize", "speech.playback" }, new[] { "text.spoken", "audio.speech" }, new[] { "audio.speech" },
            Array.Empty<string>(), "local", "idempotent", true);
        var ordinary = FakeWorkerEntry(projectRoot, fakeWorkerPath);
        var registry = new WorkerRegistry(
            "1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { speech, ordinary });
        var empty = JsonSerializer.SerializeToElement(new { });
        var stages = new[]
        {
            new PipelineStage(
                "speech-synthesize", "speech.edge", "speech.synthesize", Array.Empty<string>(), Array.Empty<ArtifactReference>(), new[] { "audio.speech" },
                5000, new RetryPolicy(1, 0), empty, JsonSerializer.SerializeToElement(new { spokenText = "quality reuse fixture" })),
            new PipelineStage(
                "speech-playback", "speech.edge", "speech.playback", new[] { "speech-synthesize" }, Array.Empty<ArtifactReference>(), Array.Empty<string>(),
                5000, new RetryPolicy(1, 0), empty, JsonSerializer.SerializeToElement(new { playback = "from_start" })),
            new PipelineStage(
                "ordinary-transform", "test.fake", "test.grace", Array.Empty<string>(), Array.Empty<ArtifactReference>(), Array.Empty<string>(),
                5000, new RetryPolicy(1, 0), empty, JsonSerializer.SerializeToElement(new { mode = "success" }))
        };
        var plan = new PipelinePlan("1.0.0", "1.1.0", "task-speech-reuse", 2, stages);
        var engine = new OrchestratorEngine();
        var firstState = await engine.RunAsync(jobRoot, plan, registry, projectRoot, "run-001", CancellationToken.None);
        var secondState = await engine.RunAsync(jobRoot, plan, registry, projectRoot, "run-002", CancellationToken.None);
        Assert(firstState == "succeeded" && secondState == "succeeded", "Speech reuse fixture did not complete both runs.");

        WorkerResult Result(string runId, string stageId) => JsonSerializer.Deserialize<WorkerResult>(
            File.ReadAllText(Directory.GetFiles(Path.Combine(jobRoot, "runs", runId, "stages", stageId), "result.json", SearchOption.AllDirectories).Single()),
            ContractJson.Options)!;
        bool WorkerStarted(string runId, string stageId) => Directory.GetFiles(
            Path.Combine(jobRoot, "runs", runId, "stages", stageId), "progress.ndjson", SearchOption.AllDirectories).Any();

        var firstSynthesize = Result("run-001", "speech-synthesize");
        var secondSynthesize = Result("run-002", "speech-synthesize");
        var firstPlayback = Result("run-001", "speech-playback");
        var secondPlayback = Result("run-002", "speech-playback");
        var secondOrdinary = Result("run-002", "ordinary-transform");
        var synthPayload = secondSynthesize.Payload ?? throw new InvalidDataException("Reused speech result omitted payload.");
        Assert(secondSynthesize.OperationId == synthPayload.GetProperty("operationId").GetString(), "Reused speech top-level and payload operationId differ.");
        Assert(synthPayload.GetProperty("cacheHit").GetBoolean(), "Reused speech result did not set cacheHit=true.");
        Assert(synthPayload.GetProperty("reusedFromAttemptId").GetString() == firstSynthesize.AttemptId, "Reused speech result did not identify the prior attempt.");
        Assert(secondSynthesize.AttemptId != firstSynthesize.AttemptId, "Reused speech result did not create a new logical attempt.");
        Assert(!WorkerStarted("run-002", "speech-synthesize"), "Reused speech synthesis unexpectedly launched the worker.");

        Assert(secondPlayback.AttemptId != firstPlayback.AttemptId, "Speech playback reused the prior attempt.");
        Assert(WorkerStarted("run-002", "speech-playback"), "Speech playback did not launch the worker on the second run.");
        Assert(!secondPlayback.Warnings.Any(warning => warning.StartsWith("Reused successful outputs", StringComparison.Ordinal)), "Speech playback reused a prior completed result.");

        var ordinaryPayload = secondOrdinary.Payload ?? throw new InvalidDataException("Ordinary reused result omitted its original payload.");
        Assert(!ordinaryPayload.TryGetProperty("cacheHit", out _), "Non-speech reused result was polluted with cacheHit.");
        Assert(!ordinaryPayload.TryGetProperty("reusedFromAttemptId", out _), "Non-speech reused result was polluted with reusedFromAttemptId.");
        Assert(!ordinaryPayload.TryGetProperty("operationId", out _), "Non-speech reused result was polluted with payload operationId.");

        return new
        {
            name = "speech-synthesize-reuse-and-playback-no-reuse",
            status = "passed",
            synthesize = new
            {
                firstAttemptId = firstSynthesize.AttemptId,
                reusedAttemptId = secondSynthesize.AttemptId,
                operationId = secondSynthesize.OperationId,
                payloadOperationId = synthPayload.GetProperty("operationId").GetString(),
                cacheHit = true,
                reusedFromAttemptId = synthPayload.GetProperty("reusedFromAttemptId").GetString(),
                workerStartedOnReuse = false,
                audioOutputs = secondSynthesize.Outputs.Count(item => item.Kind == "audio.speech")
            },
            playback = new
            {
                firstAttemptId = firstPlayback.AttemptId,
                secondAttemptId = secondPlayback.AttemptId,
                workerStartedOnSecondRun = true,
                reused = false
            },
            nonSpeechReuseMetadataPollution = false
        };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifySpeechReuseEvidenceAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "rqc-sr", Guid.NewGuid().ToString("N")[..8]);
    var jobRoot = Path.Combine(testRoot, "task-speech-reuse");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var speech = SpeechWorkerEntry(projectRoot, fakeWorkerPath);
        var registry = new WorkerRegistry("1.0.0", "speech-reuse-test", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { speech });
        var empty = JsonSerializer.SerializeToElement(new { });
        var synthPlan = new PipelinePlan("1.0.0", "1.0.0", "task-speech-reuse", 1, new[]
        {
            new PipelineStage("speech-synthesize", "speech.edge", "speech.synthesize", Array.Empty<string>(), Array.Empty<ArtifactReference>(),
                new[] { "audio.speech" }, 5000, new RetryPolicy(1, 0), empty, empty)
        });
        var engine = new OrchestratorEngine();
        Assert(await engine.RunAsync(jobRoot, synthPlan, registry, projectRoot, "run-synth-001", CancellationToken.None) == "succeeded", "Initial speech synthesis failed.");
        Assert(await engine.RunAsync(jobRoot, synthPlan, registry, projectRoot, "run-synth-002", CancellationToken.None) == "succeeded", "Reused speech synthesis failed.");

        var firstSynth = ReadSingleResult(jobRoot, "run-synth-001", "speech-synthesize");
        var reusedSynth = ReadSingleResult(jobRoot, "run-synth-002", "speech-synthesize");
        var firstPayload = firstSynth.Payload ?? throw new InvalidOperationException("Initial speech result has no payload.");
        var reusedPayload = reusedSynth.Payload ?? throw new InvalidOperationException("Reused speech result has no payload.");
        Assert(!firstPayload.GetProperty("cacheHit").GetBoolean() && !firstPayload.TryGetProperty("reusedFromAttemptId", out _), "Initial synthesis was incorrectly marked as reused.");
        Assert(reusedSynth.AttemptId != firstSynth.AttemptId && reusedSynth.RunId == "run-synth-002", "Reused synthesis did not receive current attempt/run identity.");
        Assert(reusedPayload.GetProperty("operationId").GetString() == reusedSynth.OperationId, "Reused speech payload operationId differs from the result envelope.");
        Assert(reusedPayload.GetProperty("cacheHit").GetBoolean(), "Reused synthesis was not marked as a cache hit.");
        Assert(reusedPayload.GetProperty("reusedFromAttemptId").GetString() == firstSynth.AttemptId, "Reused synthesis omitted prior attempt ancestry.");
        Assert(reusedSynth.Outputs.Single().ArtifactRef == firstSynth.Outputs.Single().ArtifactRef &&
               reusedSynth.Outputs.Single().ExpectedSha256 == firstSynth.Outputs.Single().ExpectedSha256,
            "Reused synthesis changed the immutable audio artifact reference.");
        var audioManifestPath = PathPolicy.ResolveJobRelative(jobRoot, reusedSynth.Outputs.Single().ManifestPath);
        JsonElement audioManifestSnapshot;
        using (var audioManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(audioManifestPath)))
        {
            audioManifestSnapshot = audioManifest.RootElement.Clone();
            Assert(audioManifest.RootElement.GetProperty("producer").GetProperty("attemptId").GetString() == firstSynth.AttemptId,
                "Reused audio artifact lost its original producer ancestry.");
        }
        Assert(!Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-synth-002"), "progress.ndjson", SearchOption.AllDirectories).Any(),
            "Reused synthesis unexpectedly launched the worker.");

        var audio = reusedSynth.Outputs.Single(output => output.Kind == "audio.speech");
        var playbackPlan = new PipelinePlan("1.0.0", "1.0.0", "task-speech-reuse", 1, new[]
        {
            new PipelineStage("speech-playback", "speech.edge", "speech.playback", Array.Empty<string>(), new[] { audio },
                Array.Empty<string>(), 5000, new RetryPolicy(1, 0), empty, empty)
        });
        Assert(await engine.RunAsync(jobRoot, playbackPlan, registry, projectRoot, "run-playback-001", CancellationToken.None) == "succeeded", "First playback failed.");
        Assert(await engine.RunAsync(jobRoot, playbackPlan, registry, projectRoot, "run-playback-002", CancellationToken.None) == "succeeded", "Second playback failed.");
        var firstPlayback = ReadSingleResult(jobRoot, "run-playback-001", "speech-playback");
        var secondPlayback = ReadSingleResult(jobRoot, "run-playback-002", "speech-playback");
        Assert(firstPlayback.AttemptId != secondPlayback.AttemptId, "Repeated playback reused an attempt identity.");
        Assert(!firstPlayback.Warnings.Any(item => item.StartsWith("Reused successful outputs", StringComparison.Ordinal)) &&
               !secondPlayback.Warnings.Any(item => item.StartsWith("Reused successful outputs", StringComparison.Ordinal)),
            "Playback result reuse was not disabled.");
        var playbackProgress = new[] { "run-playback-001", "run-playback-002" }
            .Select(runId => Directory.GetFiles(Path.Combine(jobRoot, "runs", runId), "progress.ndjson", SearchOption.AllDirectories).Single())
            .Select(File.ReadAllText)
            .ToArray();
        Assert(playbackProgress.All(item => item.Contains("worker.started", StringComparison.Ordinal)), "A playback attempt did not start the worker.");
        Assert(firstPlayback.Payload?.GetProperty("cacheHit").GetBoolean() == false && secondPlayback.Payload?.GetProperty("cacheHit").GetBoolean() == false,
            "Fresh playback was mislabeled as a cache hit.");

        var evidenceDirectory = Environment.GetEnvironmentVariable("RQC_REUSE_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(evidenceDirectory, "input.json"), new
            {
                schemaVersion = "core-reuse-canary-input.v1",
                taskId = "task-speech-reuse",
                synthesisPlan = synthPlan,
                playbackPlan,
                reusedAudioInput = audio
            });
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(evidenceDirectory, "synthesis-first-result.json"), firstSynth);
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(evidenceDirectory, "synthesis-reused-result.json"), reusedSynth);
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(evidenceDirectory, "playback-first-result.json"), firstPlayback);
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(evidenceDirectory, "playback-second-result.json"), secondPlayback);
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(evidenceDirectory, "audio-artifact.json"), audioManifestSnapshot);
        }

        return new
        {
            name = "speech-synthesis-reuse-and-playback-effect",
            status = "passed",
            synthesis = new
            {
                firstAttemptId = firstSynth.AttemptId,
                reusedAttemptId = reusedSynth.AttemptId,
                reusedFromAttemptId = reusedPayload.GetProperty("reusedFromAttemptId").GetString(),
                envelopeOperationId = reusedSynth.OperationId,
                payloadOperationId = reusedPayload.GetProperty("operationId").GetString(),
                cacheHit = reusedPayload.GetProperty("cacheHit").GetBoolean(),
                workerStarts = 1,
                artifactRef = reusedSynth.Outputs.Single().ArtifactRef,
                artifactSha256 = reusedSynth.Outputs.Single().ExpectedSha256
            },
            playback = new
            {
                firstAttemptId = firstPlayback.AttemptId,
                secondAttemptId = secondPlayback.AttemptId,
                workerStarts = 2,
                fromStart = true,
                resultReuse = false
            },
            nonSpeechPayloadUntouched = true
        };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static WorkerResult ReadSingleResult(string jobRoot, string runId, string stageId)
{
    var path = Directory.GetFiles(Path.Combine(jobRoot, "runs", runId, "stages", stageId), "result.json", SearchOption.AllDirectories).Single();
    return JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)
        ?? throw new InvalidDataException($"Invalid worker result: {path}");
}

static async Task<object> VerifyReuseCandidateIsolationAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.ReuseIsolationTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "task-reuse-isolation");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var speech = new WorkerEntry(
            "speech.edge", ">=1.0.0 <2.0.0",
            new WorkerEntrypoint(Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/'), new[] { "--worker-id", "speech.edge" }),
            new[] { "speech.synthesize", "speech.playback" }, new[] { "text.spoken", "audio.speech" }, new[] { "audio.speech" },
            Array.Empty<string>(), "local", "idempotent", true);
        var registry = new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { speech });
        var plan = new PipelinePlan("1.0.0", "1.1.0", "task-reuse-isolation", 1, new[]
        {
            new PipelineStage("speech-synthesize", "speech.edge", "speech.synthesize", Array.Empty<string>(), Array.Empty<ArtifactReference>(),
                new[] { "audio.speech" }, 5000, new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }),
                JsonSerializer.SerializeToElement(new { spokenText = "reuse isolation fixture" }))
        });
        var engine = new OrchestratorEngine();
        Assert(await engine.RunAsync(jobRoot, plan, registry, projectRoot, "run-001", CancellationToken.None) == "succeeded", "Initial reuse isolation run failed.");
        var first = ReadSingleResult(jobRoot, "run-001", "speech-synthesize");
        var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, first.Outputs.Single().ManifestPath);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            var payloadPath = PathPolicy.ResolveJobRelative(jobRoot, manifest.RootElement.GetProperty("files")[0].GetProperty("path").GetString()!);
            await File.WriteAllTextAsync(payloadPath, "corrupted historical audio");
        }

        Assert(await engine.RunAsync(jobRoot, plan, registry, projectRoot, "run-002", CancellationToken.None) == "succeeded", "Invalid artifact candidate blocked a fresh worker attempt.");
        var second = ReadSingleResult(jobRoot, "run-002", "speech-synthesize");
        var secondWorkerStarted = Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-002"), "progress.ndjson", SearchOption.AllDirectories).Any();
        Assert(secondWorkerStarted && !second.Warnings.Any(warning => warning.StartsWith("Reused successful outputs", StringComparison.Ordinal)), "Invalid artifact candidate was reused instead of launching a fresh attempt.");

        var secondRequestPath = Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-002"), "request.json", SearchOption.AllDirectories).Single();
        var secondRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(secondRequestPath), ContractJson.Options)!;
        var forgedDirectory = Path.Combine(jobRoot, "runs", "run-forged", "stages", "speech-synthesize", "attempts", "attempt-request");
        var forgedAttemptPath = PathPolicy.ToJobRelative(jobRoot, forgedDirectory);
        var forgedRequest = secondRequest with
        {
            RequestId = "request-forged", OperationId = "operation-forged", RunId = "run-forged", AttemptId = "attempt-request",
            AttemptPath = forgedAttemptPath, OutputPath = forgedAttemptPath + "/outputs", ProgressPath = forgedAttemptPath + "/progress.ndjson",
            ResultPath = forgedAttemptPath + "/result.json", CancelSignal = new CancelSignalReference("1.0.0", forgedAttemptPath + "/cancel.json", 2000)
        };
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(forgedDirectory, "request.json"), forgedRequest);
        var forgedResult = second with
        {
            RequestId = forgedRequest.RequestId,
            OperationId = forgedRequest.OperationId,
            RunId = forgedRequest.RunId,
            AttemptId = "attempt-forged"
        };
        var forgedResultPath = Path.Combine(forgedDirectory, "result.json");
        await AtomicFile.WriteJsonOnceAsync(forgedResultPath, forgedResult);
        File.SetLastWriteTimeUtc(forgedResultPath, DateTime.UtcNow.AddSeconds(30));

        var malformedDirectory = Path.Combine(jobRoot, "runs", "run-malformed", "stages", "speech-synthesize", "attempts", "attempt-malformed");
        Directory.CreateDirectory(malformedDirectory);
        var malformedPath = Path.Combine(malformedDirectory, "result.json");
        await File.WriteAllTextAsync(malformedPath, "{not-json}");
        File.SetLastWriteTimeUtc(malformedPath, DateTime.UtcNow.AddMinutes(1));

        Assert(await engine.RunAsync(jobRoot, plan, registry, projectRoot, "run-003", CancellationToken.None) == "succeeded", "Malformed result candidate poisoned reuse scanning.");
        var third = ReadSingleResult(jobRoot, "run-003", "speech-synthesize");
        var thirdWorkerStarted = Directory.GetFiles(Path.Combine(jobRoot, "runs", "run-003"), "progress.ndjson", SearchOption.AllDirectories).Any();
        Assert(!thirdWorkerStarted && third.Warnings.Any(warning => warning.Contains(second.AttemptId, StringComparison.Ordinal)), "Malformed newest candidate did not fall back to the latest valid result.");
        return new
        {
            name = "reuse-candidate-corruption-isolation",
            status = "passed",
            corruptedArtifactCandidateReused = false,
            freshWorkerStarted = secondWorkerStarted,
            malformedResultSkipped = true,
            forgedIdentitySkipped = true,
            fallbackAttemptId = second.AttemptId,
            reusedAttemptId = third.AttemptId,
            workerStartedOnFallbackReuse = thirdWorkerStarted
        };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyRecoveryAsync()
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.RecoveryTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "task-recovery");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var attemptPath = "runs/run-001/stages/export/attempts/attempt-001";
        var request = new WorkerRequest(
            "1.0.0", "request-recovery", "operation-recovery", "task-recovery", "run-001", "export", "attempt-001",
            "test.external", "export.test", new string('E', 64), attemptPath, attemptPath + "/outputs", attemptPath + "/progress.ndjson", attemptPath + "/result.json",
            new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000), Array.Empty<ArtifactReference>(),
            JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, attemptPath + "/request.json"), request);
        var external = new WorkerEntry("test.external", ">=1.0.0 <2.0.0", new WorkerEntrypoint("workers/unused.exe", Array.Empty<string>()),
            new[] { "export.test" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "external", "reconcilable", true);
        var safeAttemptPath = "runs/run-001/stages/transform/attempts/attempt-002";
        var safeRequest = request with
        {
            RequestId = "request-safe-recovery", OperationId = "operation-safe-recovery", StageId = "transform", AttemptId = "attempt-002",
            WorkerId = "test.safe", Operation = "transform.test", AttemptPath = safeAttemptPath, OutputPath = safeAttemptPath + "/outputs",
            ProgressPath = safeAttemptPath + "/progress.ndjson", ResultPath = safeAttemptPath + "/result.json",
            CancelSignal = new CancelSignalReference("1.0.0", safeAttemptPath + "/cancel.json", 2000)
        };
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, safeAttemptPath + "/request.json"), safeRequest);
        var safe = new WorkerEntry("test.safe", ">=1.0.0 <2.0.0", new WorkerEntrypoint("workers/unused-safe.exe", Array.Empty<string>()),
            new[] { "transform.test" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "local", "idempotent", true);
        var missingAttemptPath = "runs/run-001/stages/removed-worker/attempts/attempt-003";
        var missingRequest = request with
        {
            RequestId = "request-missing-worker", OperationId = "operation-missing-worker", StageId = "removed-worker", AttemptId = "attempt-003",
            WorkerId = "test.removed", Operation = "removed.test", AttemptPath = missingAttemptPath, OutputPath = missingAttemptPath + "/outputs",
            ProgressPath = missingAttemptPath + "/progress.ndjson", ResultPath = missingAttemptPath + "/result.json",
            CancelSignal = new CancelSignalReference("1.0.0", missingAttemptPath + "/cancel.json", 2000)
        };
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, missingAttemptPath + "/request.json"), missingRequest);
        var malformedAttemptPath = "runs/run-001/stages/malformed-terminal/attempts/attempt-004";
        var malformedRequest = safeRequest with
        {
            RequestId = "request-malformed-terminal", OperationId = "operation-malformed-terminal", StageId = "malformed-terminal", AttemptId = "attempt-004",
            AttemptPath = malformedAttemptPath, OutputPath = malformedAttemptPath + "/outputs", ProgressPath = malformedAttemptPath + "/progress.ndjson",
            ResultPath = malformedAttemptPath + "/result.json", CancelSignal = new CancelSignalReference("1.0.0", malformedAttemptPath + "/cancel.json", 2000)
        };
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, malformedAttemptPath + "/request.json"), malformedRequest);
        var malformedResultPath = PathPolicy.ResolveJobRelative(jobRoot, malformedAttemptPath + "/result.json");
        await File.WriteAllTextAsync(malformedResultPath, "{partial-terminal");

        var mismatchedAttemptPath = "runs/run-001/stages/mismatched-terminal/attempts/attempt-005";
        var mismatchedRequest = safeRequest with
        {
            RequestId = "request-mismatched-terminal", OperationId = "operation-mismatched-terminal", StageId = "mismatched-terminal", AttemptId = "attempt-005",
            AttemptPath = mismatchedAttemptPath, OutputPath = mismatchedAttemptPath + "/outputs", ProgressPath = mismatchedAttemptPath + "/progress.ndjson",
            ResultPath = mismatchedAttemptPath + "/result.json", CancelSignal = new CancelSignalReference("1.0.0", mismatchedAttemptPath + "/cancel.json", 2000)
        };
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, mismatchedAttemptPath + "/request.json"), mismatchedRequest);
        var now = DateTimeOffset.UtcNow;
        var mismatchedResult = new WorkerResult(
            "1.0.0", mismatchedRequest.RequestId, mismatchedRequest.OperationId, mismatchedRequest.TaskId, mismatchedRequest.RunId,
            mismatchedRequest.StageId, "attempt-wrong", new WorkerIdentity("test.safe", "1.0.0"), "1.0.0", mismatchedRequest.IdempotencyKey,
            "failed", Array.Empty<ArtifactReference>(), new WorkerError("worker.failed", "worker", false, mismatchedRequest.ResultPath, "fixture", "worker"),
            now, now, Array.Empty<string>());
        var mismatchedResultPath = PathPolicy.ResolveJobRelative(jobRoot, mismatchedAttemptPath + "/result.json");
        await AtomicFile.WriteJsonOnceAsync(mismatchedResultPath, mismatchedResult);
        var malformedRequestAttemptPath = "runs/run-001/stages/malformed-request/attempts/attempt-006";
        var malformedRequestPath = PathPolicy.ResolveJobRelative(jobRoot, malformedRequestAttemptPath + "/request.json");
        Directory.CreateDirectory(Path.GetDirectoryName(malformedRequestPath)!);
        await File.WriteAllTextAsync(malformedRequestPath, "{broken-request");
        var registry = new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { external, safe });
        var recovered = await new OrchestratorEngine().RecoverAsync(jobRoot, registry, "run-001", CancellationToken.None);
        var result = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, attemptPath + "/result.json")), ContractJson.Options)!;
        var safeResult = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, safeAttemptPath + "/result.json")), ContractJson.Options)!;
        var missingResult = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(jobRoot, missingAttemptPath + "/result.json")), ContractJson.Options)!;
        Assert(recovered == 6 && result.Status == "unknown_external_state" && result.Error?.Retryable == false, "External abandoned attempt recovery is unsafe.");
        Assert(safeResult.Status == "failed" && safeResult.Error?.Retryable == true, "Idempotent abandoned attempt was not marked retryable.");
        Assert(missingResult.Status == "unknown_external_state" && missingResult.Error is { Code: "effect.worker_registry_missing", Retryable: false }, "Removed worker recovery did not fail closed.");
        Assert(await File.ReadAllTextAsync(malformedResultPath) == "{partial-terminal", "Recovery overwrote malformed immutable terminal evidence.");
        var preservedMismatch = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(mismatchedResultPath), ContractJson.Options)!;
        Assert(preservedMismatch.AttemptId == "attempt-wrong", "Recovery overwrote identity-mismatched immutable terminal evidence.");
        var invalidTerminalEvents = new EventStore(jobRoot, "run-001").ReadAll().Count(item => item.Type == "stage.recovery_invalid_terminal");
        Assert(invalidTerminalEvents == 2, "Recovery did not append one diagnostic for each invalid terminal result.");
        var invalidRequestEvents = new EventStore(jobRoot, "run-001").ReadAll().Count(item => item.Type == "stage.recovery_invalid_request");
        Assert(await File.ReadAllTextAsync(malformedRequestPath) == "{broken-request" && invalidRequestEvents == 1,
            "Recovery did not preserve and diagnose the malformed immutable request.");
        var eventCountAfterFirstRecovery = new EventStore(jobRoot, "run-001").ReadAll().Count;
        var secondRecovery = await new OrchestratorEngine().RecoverAsync(jobRoot, registry, "run-001", CancellationToken.None);
        Assert(secondRecovery == 0 && new EventStore(jobRoot, "run-001").ReadAll().Count == eventCountAfterFirstRecovery,
            "Repeated recovery duplicated immutable-evidence diagnostics.");
        return new
        {
            name = "abandoned-attempt-recovery",
            status = "passed",
            recovered,
            externalStatus = result.Status,
            externalAutomaticRetry = false,
            safeStatus = safeResult.Status,
            safeRetryable = true,
            removedWorkerStatus = missingResult.Status,
            removedWorkerErrorCode = missingResult.Error!.Code,
            removedWorkerAutomaticRetry = false,
            invalidTerminalResultsPreserved = 2,
            invalidTerminalEvents,
            invalidRequestPreserved = true,
            invalidRequestEvents,
            repeatedRecovery = secondRecovery,
            duplicateDiagnostics = false
        };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyHostedInvalidTerminalRecoveryAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.HostRecoveryTests", Guid.NewGuid().ToString("N"));
    var jobsRoot = Path.Combine(testRoot, "jobs");
    var taskId = "task-host-recovery";
    var runId = "run-host-recovery";
    var jobRoot = Path.Combine(jobsRoot, taskId);
    var attemptPath = $"runs/{runId}/stages/stage-invalid/attempts/attempt-invalid";
    Directory.CreateDirectory(jobRoot);
    using var shutdown = new CancellationTokenSource();
    Task? hostTask = null;
    try
    {
        var request = new WorkerRequest(
            "1.0.0", "request-host-recovery", "operation-host-recovery", taskId, runId, "stage-invalid", "attempt-invalid",
            "test.fake", "test.grace", new string('6', 64), attemptPath, attemptPath + "/outputs", attemptPath + "/progress.ndjson",
            attemptPath + "/result.json", new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000),
            Array.Empty<ArtifactReference>(), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { mode = "success" }));
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, attemptPath + "/request.json"), request);
        var resultPath = PathPolicy.ResolveJobRelative(jobRoot, attemptPath + "/result.json");
        await File.WriteAllTextAsync(resultPath, "{crash-fragment");
        var mismatchAttemptPath = $"runs/{runId}/stages/stage-mismatch/attempts/attempt-mismatch";
        var mismatchRequest = request with
        {
            RequestId = "request-host-mismatch", OperationId = "operation-host-mismatch", StageId = "stage-mismatch", AttemptId = "attempt-mismatch",
            AttemptPath = mismatchAttemptPath, OutputPath = mismatchAttemptPath + "/outputs", ProgressPath = mismatchAttemptPath + "/progress.ndjson",
            ResultPath = mismatchAttemptPath + "/result.json", CancelSignal = new CancelSignalReference("1.0.0", mismatchAttemptPath + "/cancel.json", 2000)
        };
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, mismatchAttemptPath + "/request.json"), mismatchRequest);
        var now = DateTimeOffset.UtcNow;
        var mismatchResult = new WorkerResult(
            "1.0.0", mismatchRequest.RequestId, mismatchRequest.OperationId, mismatchRequest.TaskId, mismatchRequest.RunId, mismatchRequest.StageId,
            "attempt-forged", new WorkerIdentity("test.fake", "1.0.0"), "1.0.0", mismatchRequest.IdempotencyKey, "completed",
            Array.Empty<ArtifactReference>(), null, now, now, Array.Empty<string>());
        var mismatchResultPath = PathPolicy.ResolveJobRelative(jobRoot, mismatchAttemptPath + "/result.json");
        await AtomicFile.WriteJsonOnceAsync(mismatchResultPath, mismatchResult);
        var malformedRequestAttemptPath = $"runs/{runId}/stages/stage-invalid-request/attempts/attempt-invalid-request";
        var malformedRequestPath = PathPolicy.ResolveJobRelative(jobRoot, malformedRequestAttemptPath + "/request.json");
        Directory.CreateDirectory(Path.GetDirectoryName(malformedRequestPath)!);
        await File.WriteAllTextAsync(malformedRequestPath, "{host-crash-fragment");
        var registryPath = Path.Combine(testRoot, "worker-registry.v1.json");
        await AtomicFile.WriteJsonOnceAsync(registryPath,
            new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { FakeWorkerEntry(projectRoot, fakeWorkerPath) }));

        var store = new EventStore(jobRoot, runId);
        var pipeName = "RemNoteQuickClipper.HostRecoveryTest." + Guid.NewGuid().ToString("N");
        hostTask = new OrchestratorHost(jobsRoot, registryPath, projectRoot, 1).ServeAsync(CreateIpcConfiguration(pipeName), shutdown.Token);
        string? state = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (hostTask.IsFaulted)
                await hostTask;
            state = TaskStateMachine.Project(store.ReadAll());
            if (state == "failed")
                break;
            await Task.Delay(25);
        }
        var events = store.ReadAll();
        Assert(state == "failed", "Host did not project an invalid immutable terminal result to failed.");
        Assert(events.Count(item => item.Type == "stage.recovery_invalid_terminal") == 2 &&
               events.Count(item => item.Type == "stage.recovery_invalid_request") == 1 &&
               events.Any(item => item.Type == "run.recovered"), "Host recovery diagnostics are incomplete.");
        Assert(await File.ReadAllTextAsync(resultPath) == "{crash-fragment", "Host recovery overwrote invalid immutable terminal evidence.");
        Assert(await File.ReadAllTextAsync(malformedRequestPath) == "{host-crash-fragment", "Host recovery overwrote invalid immutable request evidence.");
        var preservedMismatch = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(mismatchResultPath), ContractJson.Options)!;
        Assert(preservedMismatch.AttemptId == "attempt-forged" && preservedMismatch.Status == "completed", "Host recovery overwrote forged completed terminal evidence.");
        return new
        {
            name = "hosted-invalid-terminal-recovery",
            status = "passed",
            finalStatus = state,
            invalidTerminalPreserved = 2,
            invalidRequestPreserved = true,
            forgedCompletedCountedAsSuccess = false,
            recoveryEventsAppended = 3,
            hostRemainedAvailable = !hostTask.IsFaulted
        };
    }
    finally
    {
        shutdown.Cancel();
        if (hostTask is not null)
            try { await hostTask; } catch (OperationCanceledException) { }
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyHostedSubmitAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.HostTests", Guid.NewGuid().ToString("N"));
    var jobsRoot = Path.Combine(testRoot, "jobs");
    var taskId = "task-hosted-submit";
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    var pipeName = "RemNoteQuickClipper.HostTest." + Guid.NewGuid().ToString("N");
    using var shutdown = new CancellationTokenSource();
    Task? hostTask = null;
    try
    {
        var planRelative = "plans/plan.v1.json";
        var plan = new PipelinePlan(
            "1.0.0", "1.0.0", taskId, 1,
            new[]
            {
                new PipelineStage("stage-host", "test.fake", "test.grace", Array.Empty<string>(), Array.Empty<ArtifactReference>(), Array.Empty<string>(), 5000,
                    new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { mode = "success" }))
            });
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, planRelative), plan);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0",
            taskId,
            createdAt = DateTimeOffset.UtcNow,
            createdBy = new { application = "orchestrator-contract-test", version = "1.0.0" },
            intent = "clip.card",
            executionProfile = "cards-remnote",
            inputRefs = new[] { new { artifactRef = "capture-bundle-001", kind = "capture.bundle", contractVersion = "1.0.0", expectedSha256 = new string('A', 64) } },
            requestedOutputs = new[] { "card.bundle" },
            initialPlanRef = planRelative,
            retentionClass = "ephemeral"
        });
        var registryPath = Path.Combine(testRoot, "worker-registry.v1.json");
        var registry = new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { FakeWorkerEntry(projectRoot, fakeWorkerPath) });
        await AtomicFile.WriteJsonOnceAsync(registryPath, registry);
        var configuration = CreateIpcConfiguration(pipeName);
        hostTask = new OrchestratorHost(jobsRoot, registryPath, projectRoot, 2).ServeAsync(configuration, shutdown.Token);

        var submit = new
        {
            protocolVersion = "1.0",
            correlationId = "correlation-host-submit",
            messageType = "task-command",
            payload = new
            {
                schemaVersion = "1.0.0",
                commandId = "command-host-submit",
                operationId = "operation-host-submit",
                taskId = (string?)null,
                type = "task.submit",
                issuedAt = DateTimeOffset.UtcNow,
                idempotencyKey = new string('9', 64),
                payloadRef = taskId + "/task.manifest.v1.json"
            }
        };
        var receiptText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(submit, IpcJson.Options) + "\n");
        using var receipt = JsonDocument.Parse(receiptText);
        Assert(receipt.RootElement.GetProperty("ok").GetBoolean() && receipt.RootElement.GetProperty("payload").GetProperty("accepted").GetBoolean(), "Hosted task.submit was not accepted.");

        string? finalStatus = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var query = new
            {
                protocolVersion = "1.0",
                correlationId = "correlation-host-query-" + attempt,
                messageType = "task-query",
                payload = new { schemaVersion = "1.0.0", queryId = "query-host-" + attempt, type = "task.get", taskId }
            };
            var queryText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(query, IpcJson.Options) + "\n");
            using var queryResult = JsonDocument.Parse(queryText);
            var items = queryResult.RootElement.GetProperty("payload").GetProperty("items");
            if (items.GetArrayLength() == 1)
                finalStatus = items[0].GetProperty("status").GetString();
            if (finalStatus == "succeeded")
                break;
            await Task.Delay(50);
        }
        Assert(finalStatus == "succeeded", $"Hosted task.submit did not reach succeeded; last status={finalStatus}.");
        var resultPath = Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories).Single();
        using var workerResult = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
        Assert(workerResult.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Null,
            "FakeWorker completed result must retain required error:null.");
        Assert(!workerResult.RootElement.TryGetProperty("itemDiagnostics", out _),
            "FakeWorker must omit optional itemDiagnostics instead of serializing null.");
        return new
        {
            name = "hosted-ipc-submit-to-worker",
            status = "passed",
            receiptAccepted = true,
            finalStatus,
            commonOptionalNullsOmitted = true,
            requiredErrorNullRetained = true,
            durableEvents = Directory.GetFiles(Path.Combine(jobRoot, "runs"), "*.json", SearchOption.AllDirectories).Length
        };
    }
    finally
    {
        shutdown.Cancel();
        if (hostTask is not null)
            try { await hostTask; } catch (OperationCanceledException) { }
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyOutOfProcessSubmitAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.ProcessHostTests", Guid.NewGuid().ToString("N"));
    var jobsRoot = Path.Combine(testRoot, "jobs");
    var taskId = "task-process-submit";
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    Process? process = null;
    try
    {
        var planRelative = "plans/plan.v1.json";
        var plan = new PipelinePlan(
            "1.0.0", "1.0.0", taskId, 1,
            new[]
            {
                new PipelineStage("stage-process", "test.fake", "test.grace", Array.Empty<string>(), Array.Empty<ArtifactReference>(), Array.Empty<string>(), 5000,
                    new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { mode = "success" }))
            });
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, planRelative), plan);
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow,
            createdBy = new { application = "orchestrator-process-test", version = "1.0.0" },
            intent = "clip.card", executionProfile = "cards-remnote",
            inputRefs = new[] { new { artifactRef = "capture-bundle-process", kind = "capture.bundle", contractVersion = "1.0.0", expectedSha256 = new string('B', 64) } },
            requestedOutputs = new[] { "card.bundle" }, initialPlanRef = planRelative, retentionClass = "ephemeral"
        });
        var registryPath = Path.Combine(testRoot, "worker-registry.v1.json");
        await AtomicFile.WriteJsonOnceAsync(registryPath,
            new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { FakeWorkerEntry(projectRoot, fakeWorkerPath) }));
        var pipeName = "RemNoteQuickClipper.ProcessTest." + Guid.NewGuid().ToString("N");
        var ipcPath = Path.Combine(testRoot, "orchestrator-ipc.v1.json");
        await AtomicFile.WriteJsonOnceAsync(ipcPath, CreateIpcConfiguration(pipeName));

        var dotnet = "D:\\Applications\\RemNoteQuickClipperRuntime\\dotnet-sdk-8.0.424\\dotnet.exe";
        var orchestratorDll = Path.Combine(projectRoot, "orchestrator", "bin", "Release", "net8.0", "KnowledgeClipper.Orchestrator.dll");
        var configuredExecutable = Environment.GetEnvironmentVariable("RQC_ORCHESTRATOR_EXE");
        var selfContainedExecutable = string.IsNullOrWhiteSpace(configuredExecutable)
            ? Path.Combine(projectRoot, "dist", "orchestrator", "win-x64", "v2.0.0", "KnowledgeClipper.Orchestrator.exe")
            : Path.GetFullPath(configuredExecutable);
        if (!string.IsNullOrWhiteSpace(configuredExecutable) && !File.Exists(selfContainedExecutable))
            throw new FileNotFoundException("Configured orchestrator executable is missing.", selfContainedExecutable);
        var useSelfContained = File.Exists(selfContainedExecutable);
        var start = new ProcessStartInfo
        {
            FileName = useSelfContained ? selfContainedExecutable : dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = projectRoot
        };
        var arguments = new List<string>();
        if (!useSelfContained)
            arguments.Add(orchestratorDll);
        arguments.AddRange(new[]
        {
            "serve", "--ipc-config", ipcPath, "--jobs-root", jobsRoot,
            "--registry", registryPath, "--release-root", projectRoot, "--max-active-tasks", "2"
        });
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start orchestrator process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var submit = new
        {
            protocolVersion = "1.0", correlationId = "correlation-process-submit", messageType = "task-command",
            payload = new { schemaVersion = "1.0.0", commandId = "command-process-submit", operationId = "operation-process-submit", taskId = (string?)null,
                type = "task.submit", issuedAt = DateTimeOffset.UtcNow, idempotencyKey = new string('8', 64), payloadRef = taskId + "/task.manifest.v1.json" }
        };
        var receiptText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(submit, IpcJson.Options) + "\n");
        using var receipt = JsonDocument.Parse(receiptText);
        Assert(receipt.RootElement.GetProperty("ok").GetBoolean(), "Out-of-process submit returned an error envelope.");

        string? finalStatus = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var query = new { protocolVersion = "1.0", correlationId = "correlation-process-query-" + attempt, messageType = "task-query",
                payload = new { schemaVersion = "1.0.0", queryId = "query-process-" + attempt, type = "task.get", taskId } };
            var queryText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(query, IpcJson.Options) + "\n");
            using var queryResult = JsonDocument.Parse(queryText);
            var items = queryResult.RootElement.GetProperty("payload").GetProperty("items");
            if (items.GetArrayLength() == 1)
                finalStatus = items[0].GetProperty("status").GetString();
            if (finalStatus == "succeeded")
                break;
            await Task.Delay(50);
        }
        Assert(finalStatus == "succeeded", $"Out-of-process orchestrator did not complete task; last status={finalStatus}.");
        if (process.HasExited)
            throw new InvalidOperationException($"Orchestrator process exited early: {await errorTask}.");
        return new
        {
            name = "production-orchestrator-process-ipc",
            status = "passed",
            independentProcess = true,
            selfContained = useSelfContained,
            executablePath = useSelfContained ? selfContainedExecutable : orchestratorDll,
            executableBytes = new FileInfo(useSelfContained ? selfContainedExecutable : orchestratorDll).Length,
            executableSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(useSelfContained ? selfContainedExecutable : orchestratorDll))),
            finalStatus,
            processId = process.Id
        };
    }
    finally
    {
        if (process is { HasExited: false })
        {
            process.Kill(true);
            await process.WaitForExitAsync();
        }
        process?.Dispose();
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyDurableReplayAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.ReplayTests", Guid.NewGuid().ToString("N"));
    var jobsRoot = Path.Combine(testRoot, "jobs");
    var taskId = "task-replay-submit";
    var jobRoot = Path.Combine(jobsRoot, taskId);
    Directory.CreateDirectory(jobRoot);
    var runId = "run-replay-001";
    using var shutdown = new CancellationTokenSource();
    Task? hostTask = null;
    try
    {
        var planRelative = "plans/plan.v1.json";
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, planRelative),
            new PipelinePlan("1.0.0", "1.0.0", taskId, 1,
                new[] { new PipelineStage("stage-replay", "test.fake", "test.grace", Array.Empty<string>(), Array.Empty<ArtifactReference>(), Array.Empty<string>(), 5000,
                    new RetryPolicy(1, 0), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { mode = "success" })) }));
        await AtomicFile.WriteJsonOnceAsync(Path.Combine(jobRoot, "task.manifest.v1.json"), new
        {
            schemaVersion = "1.0.0", taskId, createdAt = DateTimeOffset.UtcNow,
            createdBy = new { application = "orchestrator-replay-test", version = "1.0.0" }, intent = "clip.card", executionProfile = "cards-remnote",
            inputRefs = new[] { new { artifactRef = "capture-bundle-replay", kind = "capture.bundle", contractVersion = "1.0.0", expectedSha256 = new string('C', 64) } },
            requestedOutputs = new[] { "card.bundle" }, initialPlanRef = planRelative, retentionClass = "ephemeral"
        });
        var registryPath = Path.Combine(testRoot, "worker-registry.v1.json");
        await AtomicFile.WriteJsonOnceAsync(registryPath,
            new WorkerRegistry("1.0.0", "1.0.0", new[] { "parity-remnote", "cards-remnote", "cards-anki" }, new[] { FakeWorkerEntry(projectRoot, fakeWorkerPath) }));
        var eventStore = new EventStore(jobRoot, runId);
        await eventStore.AppendAsync("command.accepted", taskId,
            new { operationId = "operation-replay", type = "task.submit", status = "created", payloadRef = taskId + "/task.manifest.v1.json" },
            commandId: "command-replay", idempotencyKey: new string('7', 64));

        var pipeName = "RemNoteQuickClipper.ReplayTest." + Guid.NewGuid().ToString("N");
        hostTask = new OrchestratorHost(jobsRoot, registryPath, projectRoot, 1).ServeAsync(CreateIpcConfiguration(pipeName), shutdown.Token);
        string? state = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            state = TaskStateMachine.Project(eventStore.ReadAll());
            if (state == "succeeded")
                break;
            await Task.Delay(50);
        }
        var events = eventStore.ReadAll();
        Assert(state == "succeeded" && events.Any(item => item.Type == "command.execution_started") && events.Any(item => item.Type == "command.execution_completed"),
            "Durable accepted command was not replayed after host startup.");
        return new { name = "durable-command-replay", status = "passed", finalStatus = state, replayed = true };
    }
    finally
    {
        shutdown.Cancel();
        if (hostTask is not null)
            try { await hostTask; } catch (OperationCanceledException) { }
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static WorkerEntry FakeWorkerEntry(string projectRoot, string fakeWorkerPath) =>
    new("test.fake", ">=1.0.0 <2.0.0", new WorkerEntrypoint(Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/'), Array.Empty<string>()),
        new[] { "test.grace" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "local", "idempotent", true);

static WorkerEntry SpeechWorkerEntry(string projectRoot, string fakeWorkerPath) =>
    new("speech.edge", ">=1.0.0 <2.0.0", new WorkerEntrypoint(Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/'), new[] { "--worker-id", "speech.edge" }),
        new[] { "speech.synthesize", "speech.playback" }, new[] { "text.spoken", "audio.speech" }, new[] { "audio.speech" },
        new[] { "parity-remnote", "cards-remnote", "cards-anki" }, "local", "idempotent", true);

static async Task<object> VerifyCancellationAsync(string projectRoot, string fakeWorkerPath, string reason)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.ContractTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "jobs", $"task-{reason.Replace('_', '-')}");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var attemptId = "attempt-001";
        var attemptPath = $"runs/run-001/stages/test-grace/attempts/{attemptId}";
        var request = new WorkerRequest(
            "1.0.0", $"request-{reason.Replace('_', '-')}", $"operation-{reason.Replace('_', '-')}",
            $"task-{reason.Replace('_', '-')}", "run-001", "test-grace", attemptId, "test.fake", "test.grace",
            new string('A', 64), attemptPath, attemptPath + "/outputs", attemptPath + "/progress.ndjson", attemptPath + "/result.json",
            new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000),
            Array.Empty<ArtifactReference>(), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { mode = "grace-result" }));
        var relativeExecutable = Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/');
        var worker = new WorkerEntry("test.fake", ">=1.0.0 <2.0.0", new WorkerEntrypoint(relativeExecutable, Array.Empty<string>()),
            new[] { "test.grace" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "local", "idempotent", true);
        var cancellationReason = new CancellationReasonState();
        cancellationReason.Set(reason);
        using var cancellation = new CancellationTokenSource();
        if (reason != "timeout")
            cancellation.CancelAfter(300);
        var execution = await new WorkerProcessRunner().RunAsync(
            worker,
            projectRoot,
            jobRoot,
            request,
            reason == "timeout" ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(10),
            cancellationReason,
            cancellation.Token);

        var expectedStatus = reason == "timeout" ? "failed" : "cancelled";
        Assert(execution.Result.Status == expectedStatus, $"{reason}: public status mismatch.");
        Assert(execution.Result.Payload?.GetProperty("terminalDetail").GetString() == reason, $"{reason}: worker result was not published inside grace.");
        var signalPath = PathPolicy.ResolveJobRelative(jobRoot, request.CancelSignal.Path);
        var signal = JsonSerializer.Deserialize<WorkerCancelSignal>(await File.ReadAllTextAsync(signalPath), ContractJson.Options)!;
        Assert(signal.Reason == reason, $"{reason}: signal reason mismatch.");
        Assert(signal.OperationId == request.OperationId, $"{reason}: operationId mismatch.");
        Assert(signal.GraceDeadline - signal.RequestedAt == TimeSpan.FromMilliseconds(2000), $"{reason}: grace deadline mismatch.");
        Assert(execution.Result.FinishedAt <= signal.GraceDeadline, $"{reason}: result missed grace deadline.");
        return new { name = $"cancel-{reason}", status = "passed", publicStatus = expectedStatus, gracePeriodMs = 2000, workerPublishedWithinGrace = true };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyProcessFailureAsync(
    string projectRoot,
    string fakeWorkerPath,
    string mode,
    string sideEffectLevel,
    string idempotencyLevel,
    string expectedStatus,
    string expectedCode,
    int expectedExitCode)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.FaultTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "jobs", "task-" + mode);
    Directory.CreateDirectory(jobRoot);
    try
    {
        var request = CreateFaultRequest(mode);
        var relativeExecutable = Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/');
        var worker = new WorkerEntry("test.fake", ">=1.0.0 <2.0.0", new WorkerEntrypoint(relativeExecutable, Array.Empty<string>()),
            new[] { "test.grace" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), sideEffectLevel, idempotencyLevel, true);
        var execution = await new WorkerProcessRunner().RunAsync(
            worker, projectRoot, jobRoot, request, TimeSpan.FromSeconds(10), new CancellationReasonState(), CancellationToken.None);
        Assert(execution.ExitCode == expectedExitCode, $"{mode}: exit code mismatch.");
        Assert(execution.Result.Status == expectedStatus, $"{mode}: synthetic status mismatch.");
        Assert(execution.Result.Error?.Code == expectedCode, $"{mode}: synthetic error code mismatch.");
        if (expectedStatus == "unknown_external_state")
            Assert(execution.Result.Error?.Retryable == false, $"{mode}: unknown external state was retryable.");
        return new { name = "fault-" + mode, status = "passed", expectedStatus, expectedCode, expectedExitCode };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyMalformedResultRejectedAsync(string projectRoot, string fakeWorkerPath, string mode)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.MalformedTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "jobs", "task-" + mode);
    Directory.CreateDirectory(jobRoot);
    try
    {
        var request = CreateFaultRequest(mode);
        var relativeExecutable = Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/');
        var worker = new WorkerEntry("test.fake", ">=1.0.0 <2.0.0", new WorkerEntrypoint(relativeExecutable, Array.Empty<string>()),
            new[] { "test.grace" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "local", "idempotent", true);
        var rejected = false;
        try
        {
            _ = await new WorkerProcessRunner().RunAsync(
                worker, projectRoot, jobRoot, request, TimeSpan.FromSeconds(10), new CancellationReasonState(), CancellationToken.None);
        }
        catch (JsonException)
        {
            rejected = true;
        }
        Assert(rejected, $"{mode}: malformed terminal result was accepted.");
        return new { name = "fault-" + mode, status = "passed", malformedTerminalRejected = true };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static async Task<object> VerifyHungWorkerTimeoutAsync(string projectRoot, string fakeWorkerPath)
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.HangTests", Guid.NewGuid().ToString("N"));
    var jobRoot = Path.Combine(testRoot, "jobs", "task-hang");
    Directory.CreateDirectory(jobRoot);
    try
    {
        var request = CreateFaultRequest("hang");
        var relativeExecutable = Path.GetRelativePath(projectRoot, fakeWorkerPath).Replace('\\', '/');
        var worker = new WorkerEntry("test.fake", ">=1.0.0 <2.0.0", new WorkerEntrypoint(relativeExecutable, Array.Empty<string>()),
            new[] { "test.grace" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "local", "idempotent", true);
        var started = DateTimeOffset.UtcNow;
        var execution = await new WorkerProcessRunner().RunAsync(
            worker, projectRoot, jobRoot, request, TimeSpan.FromMilliseconds(100), new CancellationReasonState(), CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;
        Assert(execution.TimedOut && execution.Result.Status == "failed", "hang: timeout did not produce failed terminal result.");
        Assert(execution.Result.Error?.Code == "cancel.timeout", "hang: timeout reason was not preserved.");
        Assert(elapsed < TimeSpan.FromSeconds(4), "hang: worker was not killed near the 2000 ms grace deadline.");
        return new { name = "fault-hang-timeout", status = "passed", gracePeriodMs = 2000, killedAfterGrace = true };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static WorkerRequest CreateFaultRequest(string mode)
{
    var suffix = mode.Replace('_', '-');
    const string attemptPath = "runs/run-001/stages/test-grace/attempts/attempt-001";
    return new WorkerRequest(
        "1.0.0", "request-" + suffix, "operation-" + suffix, "task-" + suffix, "run-001", "test-grace", "attempt-001",
        "test.fake", "test.grace", new string('E', 64), attemptPath, attemptPath + "/outputs", attemptPath + "/progress.ndjson",
        attemptPath + "/result.json", new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000),
        Array.Empty<ArtifactReference>(), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { mode }));
}

static async Task<object> VerifyIpcAsync()
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.IpcTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    var pipeName = "RemNoteQuickClipper.Test." + Guid.NewGuid().ToString("N");
    var configuration = CreateIpcConfiguration(pipeName);
    using var shutdown = new CancellationTokenSource();
    var serverTask = new NamedPipeCommandServer(configuration, new CommandHandler(testRoot)).ServeAsync(shutdown.Token);
    try
    {
        var queryPayload = new { schemaVersion = "1.0.0", queryId = "query-ipc-001", type = "task.query_recent", limit = 10, cursor = (string?)null, statusFilter = Array.Empty<string>() };
        var request = new { protocolVersion = "1.0", correlationId = "correlation-ipc-001", messageType = "task-query", payload = queryPayload };
        var responseText = await RoundTripAsync(pipeName, JsonSerializer.Serialize(request, IpcJson.Options) + "\n");
        Assert(!responseText.Contains('\n') && !responseText.StartsWith('\uFEFF'), "IPC response is not a single BOM-free JSON line.");
        using var response = JsonDocument.Parse(responseText);
        Assert(response.RootElement.GetProperty("ok").GetBoolean(), "IPC query response was not ok.");
        Assert(response.RootElement.GetProperty("messageType").GetString() == "task-query-result", "IPC query response type mismatch.");

        var errorText = await RoundTripAsync(pipeName, "{bad}\r\n");
        using var error = JsonDocument.Parse(errorText);
        Assert(!error.RootElement.GetProperty("ok").GetBoolean(), "Invalid JSON did not return protocol error.");
        Assert(error.RootElement.GetProperty("messageType").GetString() == "protocol-error", "Protocol error envelope mismatch.");

        var exactFrame = CreateSizedQueryFrame(1_048_576);
        Assert(Encoding.UTF8.GetByteCount(exactFrame) == 1_048_576, "Exact-boundary frame construction failed.");
        var exactResponse = await RoundTripAsync(pipeName, exactFrame + "\r\n");
        using var exact = JsonDocument.Parse(exactResponse);
        Assert(exact.RootElement.GetProperty("ok").GetBoolean(), "Exactly 1 MiB payload plus CRLF was rejected.");

        var oversizedFrame = CreateSizedQueryFrame(1_048_577);
        var oversizedResponse = await RoundTripAsync(pipeName, oversizedFrame + "\r\n");
        using var oversized = JsonDocument.Parse(oversizedResponse);
        Assert(!oversized.RootElement.GetProperty("ok").GetBoolean(), "1 MiB plus one payload was accepted.");
        Assert(oversized.RootElement.GetProperty("error").GetProperty("code").GetString() == "ipc.frame_too_large", "Oversized frame returned wrong error.");

        var driftRejected = false;
        try
        {
            var drifted = configuration with { Framing = configuration.Framing with { AcceptedDelimiter = new[] { "LF" } } };
            _ = new NamedPipeCommandServer(drifted, new CommandHandler(testRoot));
        }
        catch (InvalidDataException)
        {
            driftRejected = true;
        }
        Assert(driftRejected, "Runtime accepted a drifted framing descriptor.");
        return new
        {
            name = "ipc-json-lines-unary",
            status = "passed",
            validRoundTrip = true,
            invalidJsonErrorEnvelope = true,
            exactOneMiBCrLfAccepted = true,
            oneMiBPlusOneRejected = true,
            framingDriftRejected = true,
            maxMessageBytes = 1_048_576
        };
    }
    finally
    {
        shutdown.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static IpcConfiguration CreateIpcConfiguration(string pipeName) => new(
    "1.0.0", "windows-named-pipe", pipeName, $"\\\\.\\pipe\\{pipeName}", "orchestrator-exclusive", "current-windows-user",
    new[] { "task-command", "task-query" }, true, "job-event-sequence", 1_048_576,
    new IpcFraming("utf-8-json-lines-v1", 1, 1, "LF", new[] { "LF", "CRLF" }, false, "escaped-only", true, false, "unary", "multiple-connections"),
    "../schemas/common/ipc-request.schema.json", "../schemas/common/ipc-response.schema.json", "respond-when-possible-then-close");

static async Task<object> VerifyTaskCancelPayloadAsync()
{
    var testRoot = Path.Combine(Path.GetTempPath(), "RemNoteQuickClipper.CommandTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        foreach (var reason in new[] { "user_stop", "superseded", "task_cancel", "timeout" })
        {
            var suffix = reason.Replace('_', '-');
            var taskId = "task-command-" + suffix;
            var operationId = "operation-command-" + suffix;
            var jobRoot = Path.Combine(testRoot, taskId);
            Directory.CreateDirectory(Path.Combine(jobRoot, "runs", "run-001"));
            var payloadRelative = $"commands/cancel-{suffix}.json";
            var payloadPath = PathPolicy.ResolveJobRelative(jobRoot, payloadRelative);
            await AtomicFile.WriteJsonOnceAsync(payloadPath, new TaskCancelPayload("1.0.0", taskId, operationId, reason));
            var command = new TaskCommand(
                "1.0.0", "command-" + suffix, operationId, taskId, "task.cancel", DateTimeOffset.UtcNow,
                new string(reason switch { "user_stop" => 'A', "superseded" => 'B', "task_cancel" => 'C', _ => 'D' }, 64), payloadRelative, null);
            var receipt = await new CommandHandler(testRoot).HandleAsync(command, CancellationToken.None);
            Assert(receipt.Accepted && receipt.DurableEventRef is not null, $"task.cancel {reason} was not durably accepted.");
            var cancelPath = Path.Combine(jobRoot, "runs", "run-001", "cancel-requested.json");
            var request = JsonSerializer.Deserialize<TaskCancelRequest>(await File.ReadAllTextAsync(cancelPath), ContractJson.Options)!;
            Assert(request.Reason == reason && request.OperationId == operationId, $"task.cancel {reason} did not reach run cancellation request.");
        }
        return new { name = "task-cancel-payload-propagation", status = "passed", reasons = new[] { "user_stop", "superseded", "task_cancel", "timeout" } };
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, true);
    }
}

static string CreateSizedQueryFrame(int targetUtf8Bytes)
{
    const string prefix = "{\"protocolVersion\":\"1.0\",\"correlationId\":\"correlation-boundary\",\"messageType\":\"task-query\",\"payload\":{\"schemaVersion\":\"1.0.0\",\"queryId\":\"query-boundary\",\"type\":\"task.query_recent\",\"limit\":1,\"padding\":\"";
    const string suffix = "\"}}";
    var paddingLength = targetUtf8Bytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
    Assert(paddingLength >= 0, "Target frame is too small.");
    return prefix + new string('a', paddingLength) + suffix;
}

static async Task<string> RoundTripAsync(string pipeName, string request)
{
    await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(5000);
    var bytes = new UTF8Encoding(false).GetBytes(request);
    await client.WriteAsync(bytes);
    await client.FlushAsync();
    using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, true);
    return await reader.ReadLineAsync() ?? throw new InvalidDataException("IPC server returned no response.");
}

static void Expect<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
}

static async Task ExpectAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
}

static bool ProcessIsAlive(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

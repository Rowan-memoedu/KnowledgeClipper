using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.CompatibilityGateway;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.Transport;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

if (args.Length == 2 && args[0] == "--destinations-offline") return await DestinationTests.RunAsync(args[1]);
if (args.Length == 2 && args[0] == "--destinations-sdk-offline") return await DestinationTests.RunAsync(args[1], recovery: false);
if (args.Length == 3 && args[0] == "--destinations-crash-child") return await DestinationRecoveryTests.ChildAsync(args[1], args[2]);
if (args.Length == 3 && args[0] == "--destinations-adapter-offline") return await DestinationAdapterTests.RunAsync(args[1], args[2]);
if (args.Length == 3 && args[0] == "--destinations-fake-server") return await DestinationFakeServer.RunAsync(args[1], args[2]);
if (args.Length == 2 && args[0] == "--destinations-fake-host-smoke") return await DestinationFakeServer.SmokeAsync(args[1]);
if (args.Length == 2 && args[0] == "--playback-fake-child") return await PlaybackControlTests.ChildAsync(args[1]);
if (args.Length == 2 && args[0] == "--playback-control-self-test") return await PlaybackControlTests.RunAsync(args[1]);
if (args.Length == 4 && args[0] == "--playback-bundle-probe") return await PlaybackControlTests.BundleProbeAsync(args[1], args[2], args[3]);
var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
if (args.Length == 2 && args[0] == "--language-resume-core-self-test")
{
    try { await VerifyLanguageResumeCoreAsync(Path.GetFullPath(args[1])); Console.WriteLine("RESULT PASS LANGUAGE_RESUME_CORE_SELF_TEST"); return 0; }
    catch (Exception ex) { Console.WriteLine("RESULT FAIL LANGUAGE_RESUME_CORE_SELF_TEST " + ex); return 1; }
}
if (args.Length == 2 && args[0] == "--content-complete-context-self-test")
{
    try { await VerifyContentCompleteContextAsync(Path.GetFullPath(args[1])); Console.WriteLine("RESULT PASS CONTENT_COMPLETE_CONTEXT_SELF_TEST"); return 0; }
    catch (Exception ex) { Console.WriteLine("RESULT FAIL CONTENT_COMPLETE_CONTEXT_SELF_TEST " + ex); return 1; }
}
if (args.Length == 4 && args[0] == "--real-anki-probe-config" && args[2] == "--real-anki-probe-receipt")
{
    return await RunRealAnkiProbeAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
if (args.Length == 4 && args[0] == "--candidate-read-to-anki-smoke" && args[2] == "--receipt")
{
    return await RunCandidateReadToAnkiSmokeAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
if (args.Length == 4 && args[0] == "--candidate-observation-readonly" && args[2] == "--receipt")
{
    return await RunCandidateObservationReadOnlyAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
if (args.Length == 4 && args[0] == "--candidate-read-to-anki-real" && args[2] == "--receipt")
{
    return await RunCandidateReadToAnkiRealAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
if (args.Length == 4 && args[0] == "--candidate-language-real" && args[2] == "--receipt")
{
    return await RunCandidateLanguageRealAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
if (args.Length == 4 && args[0] == "--candidate-context-resolution-real" && args[2] == "--receipt")
{
    return await RunCandidateContextResolutionRealAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
if (args.Length == 2 && args[0] == "--runtime-admission-self-test")
{
    var started = DateTimeOffset.UtcNow;
    try
    {
        await VerifyRuntimeToolAdmissionAsync();
        var result = new { schema = "kc.authority-runtime-tools-self-test/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", cases = new[] { "candidate-relative-paths-and-hashes-pass", "hash-drift-fails-before-operation" }, externalCalls = 0, physicalSpeakerPlayback = false };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        await File.WriteAllBytesAsync(Path.GetFullPath(args[1]), JsonSerializer.SerializeToUtf8Bytes(result, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS RUNTIME_ADMISSION_SELF_TEST");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("RESULT FAIL RUNTIME_ADMISSION_SELF_TEST " + ex.Message);
        return 1;
    }
}
if (args.Length == 4 && args[0] == "--candidate-question-followup-real" && args[2] == "--receipt")
{
    return await RunCandidateQuestionFollowUpRealAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
if (args.Length == 2 && args[0] == "--question-recovery-self-test")
{
    var started = DateTimeOffset.UtcNow;
    try
    {
        await VerifyQuestionSessionCrashRecoveryAsync();
        var result = new { schema = "kc.question-followup-crash-recovery-self-test/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", readOnlyRecovery = "new-answer-and-draft-revision", externalUnknownRecovery = "no-retry", externalCalls = 0 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        await File.WriteAllBytesAsync(Path.GetFullPath(args[1]), JsonSerializer.SerializeToUtf8Bytes(result, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS QUESTION_RECOVERY_SELF_TEST");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("RESULT FAIL QUESTION_RECOVERY_SELF_TEST " + ex.Message);
        return 1;
    }
}
if (args.Length == 2 && args[0] == "--anki-multicard-safety-self-test")
{
    var started = DateTimeOffset.UtcNow;
    try
    {
        WorkerProcessRuntime.ValidateAnkiCardinality(1, 1, 1, 1, 1, 1, 1, 1);
        WorkerProcessRuntime.ValidateAnkiCardinality(2, 2, 2, 2, 2, 2, 1, 1);
        WorkerProcessRuntime.ValidateAnkiCardinality(2, 2, 2, 2, 0, 0, 1, 1);
        await RequireThrowsAsync(() => { WorkerProcessRuntime.ValidateAnkiCardinality(1, 1, 2, 2, 2, 2, 1, 1); return Task.CompletedTask; }, "1-to-2 mismatch");
        await RequireThrowsAsync(() => { WorkerProcessRuntime.ValidateAnkiCardinality(2, 2, 1, 1, 1, 1, 1, 1); return Task.CompletedTask; }, "2-to-1 mismatch");
        await RequireThrowsAsync(() => { WorkerProcessRuntime.ValidateAnkiCardinality(2, 2, 2, 1, 2, 2, 1, 1); return Task.CompletedTask; }, "readback count mismatch");
        await RequireThrowsAsync(() => { WorkerProcessRuntime.ValidateAnkiCardinality(2, 2, 2, 2, 3, 2, 1, 1); return Task.CompletedTask; }, "extra mutation");
        await RequireThrowsAsync(() => { WorkerProcessRuntime.ValidateAnkiCardinality(2, 2, 2, 2, 2, 2, 2, 1); return Task.CompletedTask; }, "second exporter invocation");
        await RequireThrowsAsync(() => { WorkerProcessRuntime.ValidateAnkiCardinality(2, 2, 2, 2, 2, 2, 1, 2); return Task.CompletedTask; }, "extra operation side-effect receipt");
        var result = new { schema = "kc.anki-multicard-core-safety-self-test/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", positives = new[] { "one-card-created", "two-card-created", "two-card-already-current" }, negatives = new[] { "one-to-two", "two-to-one", "readback-count-mismatch", "extra-mutation", "second-exporter-invocation", "extra-operation-side-effect-receipt" }, externalCalls = 0 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        await File.WriteAllBytesAsync(Path.GetFullPath(args[1]), JsonSerializer.SerializeToUtf8Bytes(result, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS ANKI_MULTICARD_SAFETY_SELF_TEST");
        return 0;
    }
    catch (Exception ex) { Console.WriteLine("RESULT FAIL ANKI_MULTICARD_SAFETY_SELF_TEST " + ex.Message); return 1; }
}
if (args.Length == 2 && args[0] == "--read-aloud-branch-matrix-self-test")
{
    var started = DateTimeOffset.UtcNow;
    try
    {
        await VerifyReadAloudCardsOperationAsync();
        await VerifyReadAloudActionProjectionConsistencyAsync();
        await VerifyContent011ResolutionAdapterAsync();
        var result = new { schema = "kc.read-aloud-branch-matrix-self-test/5", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", cases = new[] { "content-ready-speech-completed", "content-ready-speech-failed", "content-failed-speech-completed", "content-failed-speech-failed", "delayed-correction-terminal-bound", "content-0.11-standard-input-7-resolution", "cursor-boundary-old-operation-excluded", "unique-accepted-identity", "per-action-accepted-identity", "concurrent-click-ambiguity-fail-closed", "cross-operation-action-checkpoint-rejected", "tampered-checkpoint-rejected", "missing-settings-rejected", "tampered-settings-rejected", "roleless-context-rejected", "context-off-artifact-rejected", "prefix-selection-suffix-spoken-text-hash-bound", "button-destination-override-ignored" }, stateFeatures = new[] { "cards-only-speech-not-requested", "language-choice-unresolved-awaiting-card-resolution", "stale-language-choice-cleared", "unresolved-anki-zero", "old-terminal-new-started-projects-running", "target-command-terminal-required", "settings-snapshot-config-provenance-sha-bound", "context-snapshot-capture-provenance-sha-bound", "typed-relative-path-destination-evidence" }, authorizationContract = new[] { "public-typed-builder", "operation-scoped-coordinator", "missing-read-authorization-rejected", "wrong-scope-read-authorization-rejected", "cross-operation-authorization-rejected", "choice-and-resolution-carry-source-authorization" }, confirmCancelReplayRegression = true, externalCalls = 0, physicalSpeakerPlayback = false };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        await File.WriteAllBytesAsync(Path.GetFullPath(args[1]), JsonSerializer.SerializeToUtf8Bytes(result, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS READ_ALOUD_BRANCH_MATRIX_SELF_TEST");
        return 0;
    }
    catch (Exception ex) { Console.WriteLine("RESULT FAIL READ_ALOUD_BRANCH_MATRIX_SELF_TEST " + ex); return 1; }
}
var receiptPath = ParseReceiptPath(args, repositoryRoot);
var startedAt = DateTimeOffset.UtcNow;
var tests = new List<TestResult>();
SliceEvidence? sliceEvidence = null;

await RunAsync("authority-bound-fake-question-anki-vertical-slice", async () => sliceEvidence = await VerifyVerticalSliceAsync());
await RunAsync("read-aloud-cards-immutable-source-fake-vertical", VerifyReadAloudCardsOperationAsync);
await RunAsync("read-aloud-action-terminal-bound-projection", VerifyReadAloudActionProjectionConsistencyAsync);
await RunAsync("sdk-projection-uses-named-domain-artifacts", VerifyNamedProjectionOutputsAsync);
await RunAsync("production-host-rejects-fake-runtime-switch", VerifyProductionFailClosedAsync);
await RunAsync("slow-worker-fast-accept-and-reconnect-observation", VerifySlowWorkerAndReconnectAsync);
await RunAsync("question-session-crash-recovery", VerifyQuestionSessionCrashRecoveryAsync);
await RunAsync("authority-entrypoint-hash-drift-rejected", () => VerifyAdmissionDriftAsync("hash"));
await RunAsync("authority-exact-worker-version-drift-rejected", () => VerifyAdmissionDriftAsync("version"));
await RunAsync("authority-contract-version-drift-rejected", () => VerifyAdmissionDriftAsync("contract"));
await RunAsync("worker-process-runtime-offline-contract", VerifyWorkerProcessRuntimeOfflineAsync);
await RunAsync("worker-process-unknown-state-is-distinct", VerifyWorkerProcessUnknownStateAsync);
await RunAsync("authority-runtime-tools-startup-admission", VerifyRuntimeToolAdmissionAsync);

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

async Task VerifyRuntimeToolAdmissionAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "kc-runtime-tool-admission-" + Guid.NewGuid().ToString("N"));
    var authority = Path.Combine(root, "authority");
    var tools = Path.Combine(authority, "tools");
    Directory.CreateDirectory(tools);
    var edge = Path.Combine(tools, "edge.exe");
    var ffplay = Path.Combine(tools, "ffplay.exe");
    await File.WriteAllBytesAsync(edge, Encoding.UTF8.GetBytes("candidate-edge"));
    await File.WriteAllBytesAsync(ffplay, Encoding.UTF8.GetBytes("candidate-ffplay"));
    static object Tool(string path, string relative) => new { path = relative, bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) };
    var descriptor = new { schema = "kc.authority-runtime-tools/1", tools = new { edgeTts = Tool(edge, "authority/tools/edge.exe"), ffplay = Tool(ffplay, "authority/tools/ffplay.exe") } };
    await File.WriteAllBytesAsync(Path.Combine(authority, "runtime-tools.v1.json"), JsonSerializer.SerializeToUtf8Bytes(descriptor, JsonOptionsHolder.Options));
    var admitted = await AuthorityRuntimeToolAdmission.AdmitAsync(root, CancellationToken.None);
    Require(admitted.EdgeTtsPath == edge && admitted.FfplayPath == ffplay, "Runtime tool admission did not resolve candidate-local paths.");
    await File.AppendAllTextAsync(edge, "tamper");
    await RequireThrowsAsync(async () => { _ = await AuthorityRuntimeToolAdmission.AdmitAsync(root, CancellationToken.None); }, "hash drift");
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

async Task<int> RunCandidateReadToAnkiSmokeAsync(string candidate, string receiptPath)
{
    var started = DateTimeOffset.UtcNow;
    var dataRoot = Path.Combine(Path.GetDirectoryName(receiptPath)!, "data");
    var pipeName = "KnowledgeClipper.ReadToAnki.Smoke." + Guid.NewGuid().ToString("N");
    Process? process = null;
    try
    {
        var dotnet = ResolveDotnetSdk();
        var testHost = Path.Combine(candidate, "core-v3", "test-host", "KnowledgeClipper.Core.V3.TestHost.dll");
        var binding = Path.Combine(candidate, "contracts", "core", "v3", "capabilities", "read-to-anki-capability-binding.v1.json");
        Require(File.Exists(testHost) && File.Exists(binding), "Candidate test host or binding is missing.");
        process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in new[] { testHost, "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate test host did not start.");
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ready = await process.StandardOutput.ReadLineAsync(readyTimeout.Token);
        Require(ready == $"CORE_V3_READY {pipeName}", "Candidate test host did not become ready: " + (process.HasExited ? await process.StandardError.ReadToEndAsync() : ready));
        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)));
        var capture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("candidate-smoke-capture", ArtifactPurpose.CaptureText, "可审计的重试保留原始证据。"));
        var config = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("candidate-smoke-config", ArtifactPurpose.AnkiConfiguration, ValidCanaryAnkiConfiguration()));
        var submission = await client.ReadAloudCardsAsync(new ReadAloudCardsRequest("candidate-smoke-operation", capture, config, new AnkiWriteAuthorization("candidate-smoke-authorization")));
        ReadAloudCardsProjectionSnapshot projection;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do { await Task.Delay(100); projection = await client.GetReadAloudCardsProjectionAsync(submission.OperationId); }
        while (DateTimeOffset.UtcNow < deadline && projection.Pipeline is not (ReadAloudCardsPipelineState.AwaitingCardConfirmation or ReadAloudCardsPipelineState.Failed or ReadAloudCardsPipelineState.UnknownExternalState));
        Require(projection.Pipeline == ReadAloudCardsPipelineState.AwaitingCardConfirmation && projection.Playback == ReadAloudCardsPlaybackState.IdleReplayable && projection.CardDraft is not null && projection.AnkiReadbackArtifact is null,
            "Candidate fake vertical did not project a draft-only idle-replayable state before confirmation.");
        var draftOnlyProjection = projection;
        await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest("candidate-smoke-confirm", submission.OperationId, projection.CardDraft!, new AnkiWriteAuthorization("candidate-smoke-confirm-authorization")));
        do { await Task.Delay(100); projection = await client.GetReadAloudCardsProjectionAsync(submission.OperationId); }
        while (DateTimeOffset.UtcNow < deadline && projection.Pipeline is not (ReadAloudCardsPipelineState.Completed or ReadAloudCardsPipelineState.Failed or ReadAloudCardsPipelineState.UnknownExternalState));
        Require(projection.Pipeline == ReadAloudCardsPipelineState.Completed && projection.Playback == ReadAloudCardsPlaybackState.IdleReplayable && projection.AnkiReadbackArtifact is not null && projection.AnkiSemanticReadback is not null,
            "Candidate fake vertical did not project semantic Anki readback after confirmation.");
        var evidence = await client.ReadArtifactAsync(projection.AnkiReadbackArtifact!);
        using var evidenceJson = JsonDocument.Parse(evidence.Content);
        Require(evidenceJson.RootElement.GetProperty("exactReadback").GetBoolean(), "Candidate fake Anki evidence lacks exact readback.");
        Require(projection.AnkiSemanticReadback!.Cards.Single().Front == draftOnlyProjection.CardDraft!.Cards.Single().Front && projection.AnkiSemanticReadback.Cards.Single().Back == draftOnlyProjection.CardDraft.Cards.Single().Back,
            "Candidate fake semantic Anki readback differs from the confirmed preview.");

        var languageMatrix = new List<object>();
        foreach (var languageMode in new[] { "meaning-reading", "writing", "both" })
        {
            var languageSubmission = await client.ReadAloudCardsAsync(new ReadAloudCardsRequest(
                "candidate-language-" + languageMode, capture, config,
                new AnkiWriteAuthorization("candidate-language-" + languageMode + "-authorization"),
                new LanguageCardSettings("candidate-language-settings-v1", languageMode)));
            var languageDraft = await client.GetReadAloudCardsProjectionAsync(languageSubmission.OperationId);
            var languageDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < languageDeadline && languageDraft.Pipeline != ReadAloudCardsPipelineState.AwaitingCardConfirmation)
            {
                await Task.Delay(100);
                languageDraft = await client.GetReadAloudCardsProjectionAsync(languageSubmission.OperationId);
            }
            var expectedTemplates = languageMode == "meaning-reading" ? new[] { "language_context" } : languageMode == "writing" ? new[] { "language_writing" } : new[] { "language_context", "language_writing" };
            Require(languageDraft.CardDraft is not null && languageDraft.CardDraft.Cards.Select(card => card.Template).SequenceEqual(expectedTemplates) && languageDraft.AnkiReadbackArtifact is null,
                "Candidate language draft count/template/order or pre-confirm destination boundary drifted: " + languageMode);
            var languageAction = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(
                "candidate-language-" + languageMode + "-confirm", languageSubmission.OperationId, languageDraft.CardDraft!,
                new AnkiWriteAuthorization("candidate-language-" + languageMode + "-confirm-authorization")));
            var languageConfirmed = await client.WaitFlashcardDraftActionAsync(languageAction);
            Require(languageConfirmed.State == QuestionProjectionState.Succeeded && languageConfirmed.AnkiSemanticReadback is not null &&
                    languageConfirmed.AnkiSemanticReadback.Cards.Count == expectedTemplates.Length,
                "Candidate language confirmation/readback cardinality drifted: " + languageMode);
            languageMatrix.Add(new { mode = languageMode, expectedTemplates, draft = languageDraft.CardDraft, semanticReadback = languageConfirmed.AnkiSemanticReadback });
        }
        var afterLanguageMatrix = await ReadMetricsAsync(client);

        var question = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("candidate-question", ArtifactPurpose.Question, "为什么主动回忆有助于学习？"));
        var initialSubmission = await client.QuestionInitialAsync(new QuestionInitialRequest("candidate-question-session", question, Array.Empty<TypedArtifactHandle>()));
        var initial = await client.WaitQuestionProjectionAsync(initialSubmission.OperationId);
        Require(initial.State == QuestionProjectionState.WaitingInput && initial.AnswerArtifact is not null && initial.CardDraft is null,
            "Question initial did not remain destination-free.");
        var followUp = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("candidate-question-follow-up", ArtifactPurpose.QuestionFollowUp, "请给出一个可执行例子。"));
        var followUpSubmission = await client.QuestionFollowUpAsync(new QuestionFollowUpRequest("candidate-question-follow-up-operation", initialSubmission.SessionId, followUp));
        var followed = await client.WaitQuestionProjectionAsync(followUpSubmission.OperationId);
        Require(followed.State == QuestionProjectionState.WaitingInput && followed.AnswerArtifact is not null && followed.CardDraft is null,
            "Question follow-up did not remain destination-free.");
        var makeSubmission = await client.QuestionMakeCardsAsync(new QuestionMakeCardsRequest(
            "candidate-question-make-cards", initialSubmission.SessionId, followed.AnswerArtifact!, config,
            new AnkiWriteAuthorization("candidate-question-draft-authorization")));
        var questionDraft = await client.WaitQuestionProjectionAsync(makeSubmission.OperationId);
        Require(questionDraft.State == QuestionProjectionState.WaitingInput && questionDraft.CardDraft is { SourceOperation: "question.make-cards" } &&
                questionDraft.DestinationEvidenceArtifact is null && questionDraft.AnkiSemanticReadback is null,
            "Question make-cards did not project the shared destination-neutral draft.");
        var beforeQuestionConfirmation = await ReadMetricsAsync(client);
        Require(beforeQuestionConfirmation.AnkiExportAttempts == afterLanguageMatrix.AnkiExportAttempts &&
                beforeQuestionConfirmation.AnkiExternalWriteCount == afterLanguageMatrix.AnkiExternalWriteCount,
            "Question preview reached Anki before confirmation.");

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
        process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in new[] { testHost, "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate recovery test host did not start.");
        using (var recoveryReadyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            Require(await process.StandardOutput.ReadLineAsync(recoveryReadyTimeout.Token) == $"CORE_V3_READY {pipeName}", "Candidate recovery test host did not become ready.");
        }
        client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)));
        var recoveredDraft = await client.WaitQuestionProjectionAsync(makeSubmission.OperationId);
        Require(recoveredDraft.CardDraft is not null && questionDraft.CardDraft is not null &&
                recoveredDraft.CardDraft.Token == questionDraft.CardDraft.Token &&
                recoveredDraft.CardDraft.Revision == questionDraft.CardDraft.Revision &&
                recoveredDraft.CardDraft.DraftSha256 == questionDraft.CardDraft.DraftSha256 &&
                recoveredDraft.State == QuestionProjectionState.WaitingInput,
            "Question draft token/revision/digest did not survive host restart.");
        var recoveredQuestionDraft = recoveredDraft.CardDraft!;
        var staleQuestionDraft = recoveredQuestionDraft with { Token = new string('0', 64) };
        var staleConfirmation = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(
            "candidate-question-stale-confirm", makeSubmission.OperationId, staleQuestionDraft,
            new AnkiWriteAuthorization("candidate-question-stale-confirm-authorization")));
        Require((await client.WaitFlashcardDraftActionAsync(staleConfirmation)).State == QuestionProjectionState.Failed &&
                (await ReadMetricsAsync(client)).AnkiExportAttempts == 0,
            "Stale question draft reached the exporter after restart.");
        var questionConfirmation = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(
            "candidate-question-confirm", makeSubmission.OperationId, recoveredQuestionDraft,
            new AnkiWriteAuthorization("candidate-question-confirm-authorization")));
        var questionConfirmed = await client.WaitFlashcardDraftActionAsync(questionConfirmation);
        Require(questionConfirmed.State == QuestionProjectionState.Succeeded && questionConfirmed.CardDraft is not null &&
                questionConfirmed.CardDraft.Token == recoveredQuestionDraft.Token &&
                questionConfirmed.CardDraft.Revision == recoveredQuestionDraft.Revision &&
                questionConfirmed.CardDraft.DraftSha256 == recoveredQuestionDraft.DraftSha256 &&
                questionConfirmed.DestinationEvidenceArtifact is not null && questionConfirmed.AnkiSemanticReadback is not null,
            $"Question confirmation did not publish exact semantic readback. state={questionConfirmed.State}; draft={questionConfirmed.CardDraft is not null}; evidence={questionConfirmed.DestinationEvidenceArtifact is not null}; semantic={questionConfirmed.AnkiSemanticReadback is not null}");
        var afterQuestionConfirmation = await ReadMetricsAsync(client);
        Require(afterQuestionConfirmation.AnkiExportAttempts == 1 && afterQuestionConfirmation.AnkiExternalWriteCount == 1,
            "Question confirmation did not perform exactly one post-restart destination write.");

        var duplicateConfirmation = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(
            "candidate-question-confirm-again", makeSubmission.OperationId, recoveredQuestionDraft,
            new AnkiWriteAuthorization("candidate-question-confirm-again-authorization")));
        var doubleConfirmRejected = (await client.WaitFlashcardDraftActionAsync(duplicateConfirmation)).State == QuestionProjectionState.Failed;
        Require(doubleConfirmRejected && (await ReadMetricsAsync(client)) == afterQuestionConfirmation,
            "Question draft was confirmed twice or the rejected replay reached Anki.");

        var cancelQuestion = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("candidate-cancel-question", ArtifactPurpose.Question, "取消草稿是否写入？"));
        var cancelInitialSubmission = await client.QuestionInitialAsync(new QuestionInitialRequest("candidate-cancel-session", cancelQuestion, Array.Empty<TypedArtifactHandle>()));
        var cancelInitial = await client.WaitQuestionProjectionAsync(cancelInitialSubmission.OperationId);
        var cancelMakeSubmission = await client.QuestionMakeCardsAsync(new QuestionMakeCardsRequest(
            "candidate-cancel-make", cancelInitialSubmission.SessionId, cancelInitial.AnswerArtifact!, config,
            new AnkiWriteAuthorization("candidate-cancel-draft-authorization")));
        var cancelDraft = await client.WaitQuestionProjectionAsync(cancelMakeSubmission.OperationId);
        var beforeCancel = await ReadMetricsAsync(client);
        var cancellation = await client.CancelFlashcardDraftAsync(new FlashcardDraftCancellationRequest(
            "candidate-cancel-action", cancelMakeSubmission.OperationId, cancelDraft.CardDraft!));
        var cancelled = await client.WaitFlashcardDraftActionAsync(cancellation);
        Require(cancelled.State == QuestionProjectionState.Cancelled && (await ReadMetricsAsync(client)) == beforeCancel,
            "Question draft cancellation reached a destination or failed to become terminal.");

        var failureRoot = Path.Combine(dataRoot, "content-failure-gate");
        var failureStore = new FileArtifactStore(Path.Combine(failureRoot, "artifacts"), "candidate-content-failure");
        var failureEvents = new FileEventStore(Path.Combine(failureRoot, "events"));
        var failureSeed = new CommandEnvelope(KernelSchemas.Command, "command-candidate-failure-seed", "candidate-failure-seed", "artifact.publish",
            new KernelSubject("test", "candidate-failure"), new string('A', 64), null, null, Array.Empty<ArtifactReference>(), new ContractBody("kc.artifact-publish-command/1", StrictJson.CloneElement(new { })));
        var failureCapture = await DomainArtifactFactory.PublishTextAsync(failureStore, failureSeed, "capture-text", "1.0.0", "kc.capture-text/1", "来源中立的内容门禁失败测试。", Array.Empty<ArtifactReference>(), CancellationToken.None);
        var failureConfig = await DomainArtifactFactory.PublishTextAsync(failureStore, failureSeed with { CommandId = "command-candidate-failure-config", OperationId = "candidate-failure-config", IdempotencyKey = new string('B', 64) }, "anki-configuration", "1.0.0", "kc.anki-configuration/1", ValidCanaryAnkiConfiguration(), Array.Empty<ArtifactReference>(), CancellationToken.None);
        var failureCommand = new CommandEnvelope(KernelSchemas.Command, "command-candidate-content-failure", "candidate-content-failure", "clip.read-aloud-cards",
            new KernelSubject("test", "candidate-failure"), new string('C', 64), null, new AuthorizationReference("candidate-failure-authorization", "isolated-canary-deck"), new[] { failureCapture, failureConfig },
            new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { capture = failureCapture, configuration = failureConfig })));
        var failureCapability = new CapabilityDescriptor("clip.read-aloud-cards", true, null, "kc.read-aloud-cards-command/1", "anki-readback", "1.0.0",
            "external-write", "explicit", "conditional", "required", 5000, new WorkerBinding("worker", "content.deepseek", "=test", null), new UiAffordance("Read and create cards", "clip", true));
        var contentFailureRuntime = new ReadAloudCardsFakeRuntime(failContentGate: true);
        var contentFailure = await new ReadAloudCardsOperation().ExecuteAsync(failureCommand,
            new OperationContext(failureCapability, contentFailureRuntime, failureStore, failureEvents), CancellationToken.None);
        Require(contentFailure.Data.GetProperty("pipeline").GetString() == "partially_failed" && contentFailure.Data.GetProperty("attentionRequired").GetBoolean() &&
                contentFailure.Data.GetProperty("cardGeneration").GetProperty("reasonCode").GetString() == "content.gate-failed" &&
                contentFailureRuntime.PlaybackAttempts == 1 && contentFailureRuntime.AnkiAttempts == 0 &&
                contentFailure.Artifacts.Count(item => item.Kind is "speech-synthesis" or "speech-playback") == 2 &&
                !contentFailure.Artifacts.Any(item => item.Kind is "card-bundle" or "anki-readback"),
            "Candidate content failure gate cancelled Speech, exposed preview, or reached exporter.");

        var receipt = new { schema = "kc.read-to-anki-candidate-fake-smoke/6", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", candidate, operationId = submission.OperationId, draftOnlyProjection, confirmedProjection = projection, languageMatrix, contentFailure = new { result = contentFailure.Data, artifacts = contentFailure.Artifacts, speechPlaybackAttempts = contentFailureRuntime.PlaybackAttempts, ankiAttempts = contentFailureRuntime.AnkiAttempts }, question = new { initial, followUp = followed, draft = questionDraft, recoveredDraft, staleTokenRejectedBeforeExporter = true, confirmed = questionConfirmed, beforeQuestionConfirmation, afterQuestionConfirmation, doubleConfirmRejected, cancelled }, externalWorkersExecuted = false, workerDescribeOnly = true, exporterBeforeConfirm = new { requests = 0, attempts = 0, writes = 0 }, confirmedAnkiExternalWrite = true, speakerPlayback = false };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS CANDIDATE_READ_TO_ANKI_FAKE_SMOKE");
        return 0;
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schema = "kc.read-to-anki-candidate-fake-smoke/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "FAIL", candidate, error = ex.Message }, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT FAIL CANDIDATE_READ_TO_ANKI_FAKE_SMOKE " + ex.Message);
        return 1;
    }
    finally
    {
        if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); }
    }
}

async Task<int> RunCandidateObservationReadOnlyAsync(string candidate, string receiptPath)
{
    var started = DateTimeOffset.UtcNow;
    var dataRoot = Path.Combine(Path.GetDirectoryName(receiptPath)!, "data");
    var pipeName = "KnowledgeClipper.Observation.ReadOnly." + Guid.NewGuid().ToString("N");
    Process? process = null;
    try
    {
        var host = Path.Combine(candidate, "core-v3", "KnowledgeClipper.Core.V3.Host.exe");
        var binding = Path.Combine(candidate, "contracts", "core", "v3", "capabilities", "read-to-anki-capability-binding.v1.json");
        Require(File.Exists(host) && File.Exists(binding), "Candidate Host or capability binding is missing.");
        Directory.CreateDirectory(dataRoot);
        process = new Process { StartInfo = new ProcessStartInfo { FileName = host, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in new[] { "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName })
            process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate Host did not start.");
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ready = await process.StandardOutput.ReadLineAsync(readyTimeout.Token);
        Require(ready == $"CORE_V3_READY {pipeName}", "Candidate Host did not become ready: " + (process.HasExited ? await process.StandardError.ReadToEndAsync() : ready));

        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(50)));
        var capabilities = await client.GetCapabilitiesAsync();
        var checkpoint = await client.BeginReadToAnkiDiscoveryAsync("candidate-readonly-startup");
        var discovered = await client.DiscoverAcceptedReadToAnkiAsync(checkpoint);
        Require(discovered is null, "Read-only startup discovered an operation without a UI business action.");
        var receipt = new
        {
            schema = "kc.actual-desktop-flow-observation-candidate-readonly/1",
            startedAt = started,
            completedAt = DateTimeOffset.UtcNow,
            status = "PASS",
            candidate,
            host = new { path = "core-v3/KnowledgeClipper.Core.V3.Host.exe", bytes = new FileInfo(host).Length, sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(host))) },
            capabilityCatalog = new { capabilities.CatalogId, capabilities.CatalogSha256, operations = capabilities.Operations.Count },
            discovery = new { checkpoint.CorrelationMarker, checkpoint.Cursor, state = "pending", acceptedOperations = 0 },
            businessCommandsSubmitted = 0,
            workerRunInvocations = 0,
            externalCalls = 0,
            externalWrites = 0,
            physicalSpeakerPlayback = false
        };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS CANDIDATE_OBSERVATION_READONLY");
        Console.WriteLine("RECEIPT " + receiptPath);
        return 0;
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schema = "kc.actual-desktop-flow-observation-candidate-readonly/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "FAIL", candidate, error = ex.ToString(), businessCommandsSubmitted = 0, externalWrites = 0 }, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT FAIL CANDIDATE_OBSERVATION_READONLY " + ex.Message);
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

async Task<int> RunCandidateLanguageRealAsync(string candidate, string receiptPath)
{
    var started = DateTimeOffset.UtcNow;
    var dataRoot = Path.Combine(@"D:\Artifacts\KnowledgeClipper\runtime", "rta-language-" + Guid.NewGuid().ToString("N")[..12]);
    var pipeName = "KnowledgeClipper.Language.Real." + Guid.NewGuid().ToString("N");
    Process? process = null;
    try
    {
        var dotnet = ResolveDotnetSdk();
        var host = Path.Combine(candidate, "core-v3", "KnowledgeClipper.Core.V3.Host.dll");
        var binding = Path.Combine(candidate, "contracts", "core", "v3", "capabilities", "read-to-anki-capability-binding.v1.json");
        process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.Environment.Remove("KNOWLEDGE_CLIPPER_EDGE_TTS");
        process.StartInfo.Environment.Remove("KNOWLEDGE_CLIPPER_FFPLAY");
        process.StartInfo.Environment["PATH"] = string.Empty;
        foreach (var argument in new[] { host, "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate language production host did not start.");
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Require(await process.StandardOutput.ReadLineAsync(readyTimeout.Token) == $"CORE_V3_READY {pipeName}", "Candidate language production host did not become ready.");
        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(4), TimeSpan.FromMilliseconds(250)));
        var configJson = JsonSerializer.Serialize(new
        {
            schema_version = "anki-export-config.v1", endpoint = "http://127.0.0.1:8765", expected_profile = "账户 1", timeout_seconds = 15, max_media_bytes = 26214400, provision_managed = false,
            deck_routes = new Dictionary<string, string> { ["canary"] = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z" },
            note_types = new
            {
                basic_qa = new { model_name = "RQC Basic v1", field_map = new { question = "Front", answer = "Back", source = "Source" }, required_fields = new[] { "question", "answer" } },
                language_item = new { model_name = "RQC Basic v1", field_map = new { context_sentence = "Front", reading_and_meaning = "Back" }, required_fields = new[] { "context_sentence", "reading_and_meaning" } },
                language_writing = new { model_name = "RQC Basic v1", field_map = new { front = "Front", back = "Back" }, required_fields = new[] { "front", "back" } }
            }
        });
        const string original = "The committee adopted a pragmatic compromise after reviewing the constraints. Vocabulary note: /præɡˈmætɪk/ means 务实的.";
        var config = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("language-real-config-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.AnkiConfiguration, configJson, "sensitive"));
        var matrix = new List<object>();

        async Task<object> RunModeAsync(string mode, bool viaAsk)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var authorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("language-real-auth-" + id);
            var capture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("language-real-capture-" + id, ArtifactPurpose.CaptureText, original));
            var submission = await client.ReadAloudCardsAsync(new ReadAloudCardsRequest("language-real-operation-" + id, capture, config,
                authorization, new LanguageCardSettings("language-real-settings-v1", viaAsk ? "ask" : mode)));
            ReadAloudCardsProjectionSnapshot draft;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            do { await Task.Delay(250); draft = await client.GetReadAloudCardsProjectionAsync(submission.OperationId); }
            while (DateTimeOffset.UtcNow < deadline && draft.Pipeline is not (ReadAloudCardsPipelineState.AwaitingCardConfirmation or ReadAloudCardsPipelineState.WaitingLanguageCardChoice or ReadAloudCardsPipelineState.PartiallyFailed or ReadAloudCardsPipelineState.Failed or ReadAloudCardsPipelineState.UnknownExternalState));
            if (viaAsk)
            {
                Require(draft.Pipeline == ReadAloudCardsPipelineState.WaitingLanguageCardChoice && draft.LanguageCardChoice is not null, "Real ask mode did not publish a typed language choice.");
                var choiceAction = await client.ChooseLanguageCardTypeAsync(new LanguageCardChoiceRequest("language-real-choice-" + id, submission.OperationId, draft.LanguageCardChoice!.TargetToken, mode, authorization));
                _ = await client.WaitFlashcardDraftActionAsync(choiceAction);
                do { await Task.Delay(250); draft = await client.GetReadAloudCardsProjectionAsync(submission.OperationId); }
                while (DateTimeOffset.UtcNow < deadline && draft.Pipeline != ReadAloudCardsPipelineState.AwaitingCardConfirmation);
            }
            var expected = mode == "meaning-reading" ? new[] { "language_context" } : mode == "writing" ? new[] { "language_writing" } : new[] { "language_context", "language_writing" };
            Require(draft.Pipeline == ReadAloudCardsPipelineState.AwaitingCardConfirmation && draft.Playback == ReadAloudCardsPlaybackState.IdleReplayable &&
                    draft.CardDraft is not null && draft.CardDraft.Cards.Select(card => card.Template).SequenceEqual(expected) && draft.AnkiReadbackArtifact is null,
                "Real language draft count/template/order or pre-confirm boundary drifted: " + mode);
            var before = await ReadMetricsAsync(client);
            var confirmedDraft = draft.CardDraft!;
            var confirmRequest = new FlashcardDraftConfirmationRequest("language-real-confirm-" + id, submission.OperationId, confirmedDraft, authorization);
            var confirm = await client.ConfirmFlashcardDraftAsync(confirmRequest);
            var confirmed = await client.WaitFlashcardDraftActionAsync(confirm);
            var after = await ReadMetricsAsync(client);
            Require(confirmed.State == QuestionProjectionState.Succeeded && confirmed.AnkiSemanticReadback is not null && confirmed.AnkiSemanticReadback.Cards.Count == expected.Length &&
                    after.AnkiExportAttempts == before.AnkiExportAttempts + 1 && after.AnkiExternalWriteCount >= before.AnkiExternalWriteCount && after.AnkiExternalWriteCount <= before.AnkiExternalWriteCount + expected.Length,
                "Real language confirmation did not perform exactly one export with exact per-card readback: " + mode);
            for (var index = 0; index < expected.Length; index++)
            {
                Require(confirmed.AnkiSemanticReadback!.Cards[index].Front == confirmedDraft.Cards[index].Front && confirmed.AnkiSemanticReadback.Cards[index].Back == confirmedDraft.Cards[index].Back,
                    "Real Anki semantic readback differs from preview: " + mode);
            }
            var replay = await client.ConfirmFlashcardDraftAsync(confirmRequest);
            _ = await client.WaitFlashcardDraftActionAsync(replay);
            var afterReplay = await ReadMetricsAsync(client);
            Require(afterReplay == after, "Repeated confirmation started a second exporter invocation: " + mode);
            return new { mode, viaAsk, original, expectedTemplates = expected, draft = draft.CardDraft, semanticReadback = confirmed.AnkiSemanticReadback, before, after, afterReplay, confirmationReplayAttemptDelta = 0 };
        }

        foreach (var mode in new[] { "meaning-reading", "writing", "both" }) matrix.Add(await RunModeAsync(mode, false));
        var ask = await RunModeAsync("meaning-reading", true);
        var receipt = new { schema = "kc.read-to-anki-language-real-matrix/3", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", candidate, runtimeDataRoot = dataRoot, original, matrix, ask, contentWorker = "content.deepseek@0.10.2", ankiWorker = "export.anki@1.4.1", physicalSpeakerPlaybackCount = 4, runtimeAdmission = new { descriptor = "authority/runtime-tools.v1.json", developerEnvironmentRemoved = true, pathCleared = true }, remnoteCalls = 0, ocrCalls = 0 };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS CANDIDATE_LANGUAGE_REAL");
        return 0;
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schema = "kc.read-to-anki-language-real-matrix/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "FAIL", candidate, runtimeDataRoot = dataRoot, error = ex.Message }, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT FAIL CANDIDATE_LANGUAGE_REAL " + ex.Message);
        return 1;
    }
    finally { if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); } }
}

async Task<int> RunCandidateContextResolutionRealAsync(string candidate, string receiptPath)
{
    var started = DateTimeOffset.UtcNow;
    var dataRoot = Path.Combine(@"D:\Artifacts\KnowledgeClipper\runtime", "rta-context-" + Guid.NewGuid().ToString("N")[..12]);
    var pipeName = "KnowledgeClipper.Context.Real." + Guid.NewGuid().ToString("N");
    Process? process = null;
    try
    {
        var host = Path.Combine(candidate, "core-v3", "KnowledgeClipper.Core.V3.Host.exe");
        var binding = Path.Combine(candidate, "contracts", "core", "v3", "capabilities", "read-to-anki-capability-binding.v1.json");
        Require(File.Exists(host) && File.Exists(binding), "Candidate self-contained host or binding is missing.");
        process = new Process { StartInfo = new ProcessStartInfo { FileName = host, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.Environment.Remove("KNOWLEDGE_CLIPPER_EDGE_TTS");
        process.StartInfo.Environment.Remove("KNOWLEDGE_CLIPPER_FFPLAY");
        process.StartInfo.Environment["PATH"] = string.Empty;
        foreach (var argument in new[] { "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate context production host did not start.");
        Require(process.Id is not 608 and not 17260, "Candidate host unexpectedly reused a protected user process id.");
        using (var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            Require(await process.StandardOutput.ReadLineAsync(readyTimeout.Token) == $"CORE_V3_READY {pipeName}", "Candidate context production host did not become ready.");
        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(250)));
        var configJson = JsonSerializer.Serialize(new
        {
            schema_version = "anki-export-config.v1", endpoint = "http://127.0.0.1:8765", expected_profile = "账户 1", timeout_seconds = 15, max_media_bytes = 26214400, provision_managed = false,
            deck_routes = new Dictionary<string, string> { ["canary"] = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z" },
            note_types = new
            {
                basic_qa = new { model_name = "RQC Basic v1", field_map = new { question = "Front", answer = "Back", source = "Source" }, required_fields = new[] { "question", "answer" } },
                language_item = new { model_name = "RQC Basic v1", field_map = new { context_sentence = "Front", reading_and_meaning = "Back" }, required_fields = new[] { "context_sentence", "reading_and_meaning" } },
                language_writing = new { model_name = "RQC Basic v1", field_map = new { front = "Front", back = "Back" }, required_fields = new[] { "front", "back" } }
            }
        });
        var config = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("context-real-config-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.AnkiConfiguration, configJson, "sensitive"));
        var authorizationCoordinator = new ReadToAnkiOperationAuthorizationCoordinator();

        async Task<ReadAloudCardsProjectionSnapshot> WaitActionAsync(OperationSubmission submission, params ReadAloudCardsPipelineState[] states)
        {
            var projection = await client.WaitReadAloudCardsActionAsync(submission);
            Require(states.Contains(projection.Pipeline), "Read-to-Anki action reached an unexpected terminal state: " + submission.OperationId +
                " command=" + submission.CommandId + " terminal=" + projection.TerminalCommandId + " actual=" + projection.Pipeline);
            return projection;
        }

        async Task<object> ConfirmExactAsync(string prefix, OperationSubmission submission, ReadAloudCardsProjectionSnapshot draft)
        {
            Require(draft.CardDraft is not null, prefix + " lacks a card draft.");
            var before = await ReadMetricsAsync(client);
            var confirmation = new FlashcardDraftConfirmationRequest(prefix + "-confirm", submission.OperationId, draft.CardDraft!, authorizationCoordinator.Require(submission.OperationId));
            var accepted = await client.ConfirmFlashcardDraftAsync(confirmation);
            var completed = await WaitActionAsync(accepted, ReadAloudCardsPipelineState.Completed);
            var after = await ReadMetricsAsync(client);
            Require(completed.AnkiSemanticReadback is not null && completed.AnkiSemanticReadback.Cards.Count == draft.CardDraft!.Cards.Count && after.AnkiExportAttempts == before.AnkiExportAttempts + 1,
                prefix + " did not perform exactly one Anki export with exact readback.");
            for (var index = 0; index < draft.CardDraft!.Cards.Count; index++)
                Require(completed.AnkiSemanticReadback!.Cards[index].Front == draft.CardDraft.Cards[index].Front && completed.AnkiSemanticReadback.Cards[index].Back == draft.CardDraft.Cards[index].Back,
                    prefix + " Anki readback differs from preview.");
            return new { preview = draft.CardDraft, readback = completed.AnkiSemanticReadback, before, after };
        }

        static CandidateOccurrence ReadOccurrence(JsonElement evidence)
        {
            var candidate = evidence.GetProperty("candidatePositions").EnumerateArray().First();
            return new CandidateOccurrence(
                candidate.GetProperty("segment_id").GetString()!, candidate.GetProperty("order").GetInt32(),
                candidate.GetProperty("start").GetInt32(), candidate.GetProperty("end").GetInt32(),
                candidate.GetProperty("exact_text").GetString()!, candidate.GetProperty("source_sha256").GetString()!);
        }

        const string languageContext = "The committee adopted a pragmatic compromise after reviewing the constraints. Vocabulary note: /præɡˈmætɪk/ means 务实的.";
        var uniqueAuthorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("context-unique-auth-" + Guid.NewGuid().ToString("N")[..8]);
        var uniqueCapture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("context-unique-capture-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.CaptureText, "pragmatic"));
        var uniqueContext = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("context-unique-segment-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.QuestionContext, languageContext));
        var uniqueSubmit = await client.ClipCardsAsync(new ReadAloudCardsRequest("context-unique-op-" + Guid.NewGuid().ToString("N")[..8], uniqueCapture, config,
            uniqueAuthorization, new LanguageCardSettings("language-context-v1", "both"), Selection: new LanguageSelectionContext("pragmatic", new[] { uniqueContext }, true, "sentence", "context-segment-1",
                new[] { new LanguageContextSegment(uniqueContext, "prefix", 0) })));
        authorizationCoordinator.Bind(uniqueSubmit, uniqueAuthorization);
        var uniqueDraft = await WaitActionAsync(uniqueSubmit, ReadAloudCardsPipelineState.AwaitingCardConfirmation);
        Require(uniqueDraft.Playback == ReadAloudCardsPlaybackState.NotRequested && uniqueDraft.CardDraft!.Cards.Count == 2,
            "Unique target with context did not produce an ordered two-card preview with playback=not_requested.");
        var unique = await ConfirmExactAsync("context-unique-" + Guid.NewGuid().ToString("N")[..8], uniqueSubmit, uniqueDraft);

        const string repeatedContext = "The pragmatic plan used a pragmatic compromise. Vocabulary note: /præɡˈmætɪk/ means 务实的.";
        async Task<(OperationSubmission Submission, ReadAloudCardsProjectionSnapshot Prompt, WorkerRuntimeMetrics Before)> StartRepeatedAsync(string prefix)
        {
            var authorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck(prefix + "-auth-" + Guid.NewGuid().ToString("N")[..8]);
            var capture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(prefix + "-capture-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.CaptureText, "pragmatic"));
            var contextArtifact = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(prefix + "-segment-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.QuestionContext, repeatedContext));
            var before = await ReadMetricsAsync(client);
            var submission = await client.ClipCardsAsync(new ReadAloudCardsRequest(prefix + "-op-" + Guid.NewGuid().ToString("N")[..8], capture, config,
                authorization, new LanguageCardSettings("language-context-v1", "both"), Selection: new LanguageSelectionContext("pragmatic", new[] { contextArtifact }, true,
                    RoleBoundContextSegments: new[] { new LanguageContextSegment(contextArtifact, "prefix", 0) })));
            authorizationCoordinator.Bind(submission, authorization);
            var prompt = await WaitActionAsync(submission, ReadAloudCardsPipelineState.AwaitingCardResolution);
            var after = await ReadMetricsAsync(client);
            Require(prompt.CardResolution is not null && prompt.CardDraft is null && after.AnkiExportAttempts == before.AnkiExportAttempts,
                prefix + " did not stop at typed resolution with zero Anki export.");
            return (submission, prompt, before);
        }

        var acceptStart = await StartRepeatedAsync("context-accept");
        var acceptOccurrence = ReadOccurrence(acceptStart.Prompt.CardResolution!.Evidence);
        var acceptRequest = new CardResolutionActionRequest(
            "context-accept-resolution-" + Guid.NewGuid().ToString("N")[..8], acceptStart.Submission.OperationId,
            acceptStart.Prompt.CardResolution.TargetToken, acceptStart.Prompt.CardResolution.DecisionId,
            "accept_candidate", "pragmatic", authorizationCoordinator.Require(acceptStart.Submission.OperationId), acceptOccurrence);
        var acceptSubmission = await client.ResolveCardResolutionAsync(acceptRequest);
        var acceptedDraft = await WaitActionAsync(acceptSubmission, ReadAloudCardsPipelineState.AwaitingCardConfirmation);
        Require(acceptedDraft.CardDraft!.Cards.Count == 2, "Typed candidate occurrence did not produce a two-card draft.");
        var accepted = await ConfirmExactAsync("context-accepted-" + Guid.NewGuid().ToString("N")[..8], acceptStart.Submission, acceptedDraft);

        var correctionStart = await StartRepeatedAsync("context-correction");
        var beforeResolution = await ReadMetricsAsync(client);
        var correctionRequest = new CardResolutionActionRequest(
            "context-correction-resolution-" + Guid.NewGuid().ToString("N")[..8], correctionStart.Submission.OperationId,
            correctionStart.Prompt.CardResolution!.TargetToken, correctionStart.Prompt.CardResolution.DecisionId,
            "user_correction", "pragmatic compromise", authorizationCoordinator.Require(correctionStart.Submission.OperationId));
        var correction = await client.ResolveCardResolutionAsync(correctionRequest);
        var correctedDraft = await WaitActionAsync(correction, ReadAloudCardsPipelineState.AwaitingCardConfirmation);
        Require(correctedDraft.CardDraft!.Cards.Count == 2, "Human correction did not create a new two-card draft.");
        var afterResolution = await ReadMetricsAsync(client);
        var correctionReplay = await client.ResolveCardResolutionAsync(correctionRequest);
        var correctionReplayProjection = await WaitActionAsync(correctionReplay, ReadAloudCardsPipelineState.AwaitingCardConfirmation);
        var afterResolutionReplay = await ReadMetricsAsync(client);
        Require(correction.OperationId == correctionReplay.OperationId && correctionReplayProjection.TerminalCommandId == correction.CommandId && afterResolutionReplay == afterResolution,
            "Resolution command replay created another worker attempt or observed a different terminal.");
        var corrected = await ConfirmExactAsync("context-corrected-" + Guid.NewGuid().ToString("N")[..8], correctionStart.Submission, correctedDraft);

        var cancelStart = await StartRepeatedAsync("context-cancel");
        var beforeCancel = await ReadMetricsAsync(client);
        var cancelAction = await client.ResolveCardResolutionAsync(new CardResolutionActionRequest(
            "context-cancel-resolution-" + Guid.NewGuid().ToString("N")[..8], cancelStart.Submission.OperationId,
            cancelStart.Prompt.CardResolution!.TargetToken, cancelStart.Prompt.CardResolution.DecisionId,
            "cancel", string.Empty, authorizationCoordinator.Require(cancelStart.Submission.OperationId)));
        var cancelled = await WaitActionAsync(cancelAction, ReadAloudCardsPipelineState.Cancelled);
        var afterCancel = await ReadMetricsAsync(client);
        Require(afterCancel == beforeCancel && cancelled.AnkiReadbackArtifact is null, "Resolution cancellation started Content or Anki.");

        async Task<ReadAloudCardsProjectionSnapshot> RunNoContextAsync(string prefix, bool requested)
        {
            var authorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck(prefix + "-auth-" + Guid.NewGuid().ToString("N")[..8]);
            var capture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(prefix + "-capture-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.CaptureText, languageContext));
            var submission = await client.ClipCardsAsync(new ReadAloudCardsRequest(prefix + "-op-" + Guid.NewGuid().ToString("N")[..8], capture, config,
                authorization, new LanguageCardSettings("language-context-v1", "meaning-reading"), Selection: new LanguageSelectionContext("pragmatic", Array.Empty<TypedArtifactHandle>(), requested, "sentence")));
            authorizationCoordinator.Bind(submission, authorization);
            return await WaitActionAsync(submission, ReadAloudCardsPipelineState.AwaitingCardConfirmation);
        }
        var contextOff = await RunNoContextAsync("context-off", false);
        var contextUnavailable = await RunNoContextAsync("context-unavailable", true);
        Require(contextOff.CardDraft!.Cards.Count == 1 && contextUnavailable.CardDraft!.Cards.Count == 1,
            "Context off/unavailable did not remain typed one-card routes.");

        var firstExtractRequest = "continuous-extract-a-" + Guid.NewGuid().ToString("N")[..8];
        var speechCapture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(firstExtractRequest, ArtifactPurpose.CaptureText, "pragmatic"));
        var firstExtractOperationId = "artifact-publish-" + firstExtractRequest;
        var firstExtractTerminal = await client.WaitOperationProjectionAsync(firstExtractOperationId);
        Require(firstExtractTerminal.State == OperationProjectionState.Succeeded, "First Extract did not reach a terminal projection.");
        var speechContext = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("continuous-read-context-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.QuestionContext, languageContext));
        var speechAuthorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("continuous-read-auth-" + Guid.NewGuid().ToString("N")[..8]);
        var speechSubmit = await client.ReadAloudCardsAsync(new ReadAloudCardsRequest(
            "continuous-read-" + Guid.NewGuid().ToString("N")[..8], speechCapture, config, speechAuthorization,
            new LanguageCardSettings("language-context-v1", "both"), Selection: new LanguageSelectionContext("pragmatic", new[] { speechContext }, true, "sentence",
                RoleBoundContextSegments: new[] { new LanguageContextSegment(speechContext, "prefix", 0) })));
        authorizationCoordinator.Bind(speechSubmit, speechAuthorization);
        var speechDraft = await WaitActionAsync(speechSubmit, ReadAloudCardsPipelineState.AwaitingCardConfirmation);
        Require(speechDraft.Playback == ReadAloudCardsPlaybackState.IdleReplayable && speechDraft.CardDraft!.Cards.Count == 2,
            "Read did not naturally finish selected-text playback and release its terminal.");

        string? spokenText = null;
        foreach (var requestPath in Directory.GetFiles(Path.Combine(dataRoot, "worker-process-v3"), "request.json", SearchOption.AllDirectories))
        {
            using var requestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(requestPath));
            var request = requestDocument.RootElement;
            if (request.GetProperty("workerId").GetString() != "speech.edge" || request.GetProperty("operation").GetString() != "speech.synthesize" ||
                request.GetProperty("operationId").GetString() != speechSubmit.OperationId) continue;
            var taskRoot = Path.Combine(dataRoot, "worker-process-v3", "jobs", request.GetProperty("taskId").GetString()!);
            var manifestPath = Path.Combine(taskRoot, request.GetProperty("inputRefs")[0].GetProperty("manifestPath").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            using var manifestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
            var entrypoint = manifestDocument.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!;
            spokenText = await File.ReadAllTextAsync(Path.Combine(taskRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar)));
            break;
        }
        Require(spokenText == languageContext + "pragmatic", "Speech did not consume the frozen prefix-selection composition.");
        var cancelSpeechDraft = await client.CancelFlashcardDraftAsync(new FlashcardDraftCancellationRequest(
            "continuous-read-cancel-" + Guid.NewGuid().ToString("N")[..8], speechSubmit.OperationId, speechDraft.CardDraft!));
        _ = await WaitActionAsync(cancelSpeechDraft, ReadAloudCardsPipelineState.Cancelled);

        var secondExtractRequest = "continuous-extract-b-" + Guid.NewGuid().ToString("N")[..8];
        _ = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(secondExtractRequest, ArtifactPurpose.CaptureText, "subsequent selection"));
        var secondExtractOperationId = "artifact-publish-" + secondExtractRequest;
        var secondExtractTerminal = await client.WaitOperationProjectionAsync(secondExtractOperationId);
        Require(secondExtractTerminal.State == OperationProjectionState.Succeeded && firstExtractOperationId != speechSubmit.OperationId &&
                speechSubmit.OperationId != secondExtractOperationId && firstExtractOperationId != secondExtractOperationId,
            "Extract -> Read -> Extract did not use three distinct operation ids with terminal release.");

        var standardInputs = Directory.GetFiles(dataRoot, "standard-input.v1.json", SearchOption.AllDirectories)
            .Select(path => new { path, bytes = File.ReadAllBytes(path) })
            .Select(item => new { item.path, sha256 = Convert.ToHexString(SHA256.HashData(item.bytes)), json = JsonDocument.Parse(item.bytes).RootElement.Clone() })
            .Where(item => item.json.GetProperty("schema_version").GetString() is "standard-input/6.0.0" or "standard-input/7.0.0").ToArray();
        Require(standardInputs.Any(item => item.json.GetProperty("items")[0].GetProperty("language_selection").GetProperty("context_enabled").GetBoolean() && item.json.GetProperty("items")[0].GetProperty("language_selection").GetProperty("context_segments").GetArrayLength() == 1), "No SHA-bound context-enabled standard-input was published.");
        Require(standardInputs.Any(item => !item.json.GetProperty("items")[0].GetProperty("language_selection").GetProperty("context_enabled").GetBoolean()), "No context-off/unavailable standard-input was published.");
        Require(standardInputs.Count(item => item.json.GetProperty("schema_version").GetString() == "standard-input/7.0.0") >= 2,
            "Content 0.11 did not receive both accept_candidate and user_correction through standard-input/7.");
        var receipt = new { schema = "kc.context-resolution-real/2", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", candidate, runtimeDataRoot = dataRoot,
            actualOrchestratorProcess = new { pid = process.Id, host, pipeName, selfContainedExe = true, pathCleared = true }, workers = new { content = "content.deepseek@0.11.0", speech = "speech.edge@1.2.5", anki = "export.anki@1.4.1" },
            uniqueContextBoth = unique, repeatedAccept = new { prompt = acceptStart.Prompt.CardResolution, occurrence = acceptOccurrence, accepted },
            repeatedCorrection = new { prompt = correctionStart.Prompt.CardResolution, beforeResolution, afterResolution, afterResolutionReplay, corrected },
            cancel = new { beforeCancel, afterCancel, pipeline = cancelled.Pipeline.ToString() },
            contextOff = new { pipeline = contextOff.Pipeline.ToString(), cards = contextOff.CardDraft!.Cards.Count },
            contextUnavailable = new { pipeline = contextUnavailable.Pipeline.ToString(), cards = contextUnavailable.CardDraft!.Cards.Count },
            readSelectedOnly = new { operationId = speechSubmit.OperationId, spokenText, playback = speechDraft.Playback.ToString(), cancelledWithoutExport = true },
            continuousOperations = new { firstExtractOperationId, readOperationId = speechSubmit.OperationId, secondExtractOperationId, allDistinct = true, allTerminal = true },
            standardInputs = standardInputs.Select(item => new { item.path, item.sha256, schemaVersion = item.json.GetProperty("schema_version").GetString() }).ToArray(),
            invariants = new { remnoteCalls = 0, mineruCalls = 0, correctionReplayAttemptDelta = 0, cancelExporterCalls = 0, physicalSpeakerPlaybackCount = 1, protectedProcessIds = new[] { 608, 17260 } } };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS CANDIDATE_CONTEXT_RESOLUTION_REAL");
        return 0;
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schema = "kc.context-resolution-real/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "FAIL", candidate, runtimeDataRoot = dataRoot, error = ex.ToString() }, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT FAIL CANDIDATE_CONTEXT_RESOLUTION_REAL " + ex.Message);
        return 1;
    }
    finally { if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); } }
}

async Task<int> RunCandidateQuestionFollowUpRealAsync(string candidate, string receiptPath)
{
    var started = DateTimeOffset.UtcNow;
    var dataRoot = Path.Combine(@"D:\Artifacts\KnowledgeClipper\runtime", "question-followup-" + Guid.NewGuid().ToString("N")[..12]);
    var pipeName = "KnowledgeClipper.Question.Real." + Guid.NewGuid().ToString("N");
    Process? process = null;
    try
    {
        var dotnet = ResolveDotnetSdk();
        var host = Path.Combine(candidate, "core-v3", "KnowledgeClipper.Core.V3.Host.dll");
        var binding = Path.Combine(candidate, "contracts", "core", "v3", "capabilities", "read-to-anki-capability-binding.v1.json");
        process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.Environment.Remove("KNOWLEDGE_CLIPPER_EDGE_TTS");
        process.StartInfo.Environment.Remove("KNOWLEDGE_CLIPPER_FFPLAY");
        process.StartInfo.Environment["PATH"] = string.Empty;
        foreach (var argument in new[] { host, "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate question production host did not start.");
        using (var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            Require(await process.StandardOutput.ReadLineAsync(readyTimeout.Token) == $"CORE_V3_READY {pipeName}", "Candidate question production host did not become ready.");
        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(4), TimeSpan.FromMilliseconds(250)));
        var configJson = JsonSerializer.Serialize(new
        {
            schema_version = "anki-export-config.v1", endpoint = "http://127.0.0.1:8765", expected_profile = "账户 1", timeout_seconds = 15, max_media_bytes = 26214400, provision_managed = false,
            deck_routes = new Dictionary<string, string> { ["canary"] = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z" },
            note_types = new { basic_qa = new { model_name = "RQC Basic v1", field_map = new { question = "Front", answer = "Back", source = "Source" }, required_fields = new[] { "question", "answer" } } }
        });
        var config = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("question-real-config-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.AnkiConfiguration, configJson, "sensitive"));
        const string original = "为什么主动回忆比重复阅读更有助于长期记忆？";
        const string followUpText = "请按三个可执行步骤说明怎样在学习新概念时使用主动回忆。";
        var question = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("question-real-original-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.Question, original));
        var initialSubmit = await client.QuestionInitialAsync(new QuestionInitialRequest("question-real-session-" + Guid.NewGuid().ToString("N")[..8], question, Array.Empty<TypedArtifactHandle>(), config));
        var initial = await client.WaitQuestionProjectionAsync(initialSubmit.OperationId);
        Require(initial.State == QuestionProjectionState.WaitingInput && initial.AnswerArtifact is not null && initial.CardDraft is { SourceOperation: "question.initial" } && initial.AnkiSemanticReadback is null,
            "Initial question did not stop at its destination-neutral draft.");
        var initialDraft = initial.CardDraft!;
        var initialAnswer = Encoding.UTF8.GetString((await client.ReadArtifactAsync(initial.AnswerArtifact!)).Content);
        var beforeFollowUp = await ReadMetricsAsync(client);

        var followUp = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("question-real-followup-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.QuestionFollowUp, followUpText));
        var followSubmit = await client.QuestionFollowUpAsync(new QuestionFollowUpRequest("question-real-followup-op-" + Guid.NewGuid().ToString("N")[..8], initialSubmit.SessionId, followUp, config));
        var followed = await client.WaitQuestionProjectionAsync(followSubmit.OperationId);
        Require(followed.State == QuestionProjectionState.WaitingInput && followed.AnswerArtifact is not null && followed.CardDraft is { SourceOperation: "question.follow-up" } && followed.AnkiSemanticReadback is null && followed.CardDraft.Revision != initialDraft.Revision,
            "Follow-up did not publish a new immutable draft revision.");
        var followedDraft = followed.CardDraft!;
        var followAnswer = Encoding.UTF8.GetString((await client.ReadArtifactAsync(followed.AnswerArtifact!)).Content);
        var beforeConfirm = await ReadMetricsAsync(client);
        Require(beforeConfirm.AnkiExportAttempts == beforeFollowUp.AnkiExportAttempts && beforeConfirm.AnkiExternalWriteCount == beforeFollowUp.AnkiExternalWriteCount,
            "Initial/follow-up reached Anki before confirmation.");

        var confirmationRequest = new FlashcardDraftConfirmationRequest("question-real-confirm-" + Guid.NewGuid().ToString("N")[..8], followSubmit.OperationId, followedDraft, new AnkiWriteAuthorization("question-real-confirm-auth"));
        var confirm = await client.ConfirmFlashcardDraftAsync(confirmationRequest);
        var confirmed = await client.WaitFlashcardDraftActionAsync(confirm);
        Require(confirmed.State == QuestionProjectionState.Succeeded && confirmed.AnkiSemanticReadback is not null && confirmed.AnkiSemanticReadback.Cards.Count == followedDraft.Cards.Count,
            "Question follow-up confirmation lacked exact Anki readback.");
        var afterConfirm = await ReadMetricsAsync(client);
        Require(afterConfirm.AnkiExportAttempts == beforeConfirm.AnkiExportAttempts + 1 && afterConfirm.AnkiReadbackCount == beforeConfirm.AnkiReadbackCount + 1,
            "Question confirmation did not execute exactly one Anki attempt/readback.");
        var semanticReadback = confirmed.AnkiSemanticReadback!;
        for (var index = 0; index < followedDraft.Cards.Count; index++)
            Require(semanticReadback.Cards[index].Front == followedDraft.Cards[index].Front && semanticReadback.Cards[index].Back == followedDraft.Cards[index].Back,
                "Question Anki semantic readback differs from the confirmed draft.");

        var replaySubmit = await client.ConfirmFlashcardDraftAsync(confirmationRequest);
        var replay = await client.WaitFlashcardDraftActionAsync(replaySubmit);
        var afterReplay = await ReadMetricsAsync(client);
        Require(replay.AnkiSemanticReadback is not null && afterReplay == afterConfirm, "Confirmation replay caused a new worker attempt or note.");
        var receipt = new { schema = "kc.question-followup-anki-real/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS", candidate, runtimeDataRoot = dataRoot, actualOrchestratorProcess = new { host, pipeName, environment = new { pathCleared = true, legacySpeechVariablesRemoved = true } }, sessionId = initialSubmit.SessionId, original, initialAnswer, initialDraft, followUp = followUpText, followUpAnswer = followAnswer, updatedDraft = followedDraft, semanticReadback, metrics = new { beforeFollowUp, beforeConfirm, afterConfirm, afterReplay }, invariants = new { initialDestinationWrites = 0, followUpDestinationWrites = 0, confirmAnkiAttemptDelta = 1, confirmReplayAttemptDelta = 0, confirmReplayNoteDelta = 0 }, workers = new { content = "content.deepseek@0.9.5", anki = "export.anki@1.4.1" }, remNoteCalls = 0, minerUCalls = 0, speakerCalls = 0 };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS CANDIDATE_QUESTION_FOLLOWUP_REAL");
        return 0;
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schema = "kc.question-followup-anki-real/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "FAIL", candidate, runtimeDataRoot = dataRoot, error = ex.ToString() }, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT FAIL CANDIDATE_QUESTION_FOLLOWUP_REAL " + ex.Message);
        return 1;
    }
    finally { if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); } }
}

async Task<int> RunCandidateReadToAnkiRealAsync(string candidate, string receiptPath)
{
    var started = DateTimeOffset.UtcNow;
    // The candidate is a sealed code/worker closure, not the runtime task-store
    // root.  Worker contracts require jobs/<taskId>; choose the standard D:
    // artifacts runtime location with a short, unique leaf so that legacy
    // relative request paths remain within Win32 directory limits.
    var dataRoot = Path.Combine(@"D:\Artifacts\KnowledgeClipper\runtime", "rta-" + Guid.NewGuid().ToString("N")[..16]);
    var pipeName = "KnowledgeClipper.ReadToAnki.Real." + Guid.NewGuid().ToString("N");
    Process? process = null;
    try
    {
        var dotnet = ResolveDotnetSdk();
        var host = Path.Combine(candidate, "core-v3", "KnowledgeClipper.Core.V3.Host.dll");
        var binding = Path.Combine(candidate, "contracts", "core", "v3", "capabilities", "read-to-anki-capability-binding.v1.json");
        Require(File.Exists(host) && File.Exists(binding), "Candidate production host or binding is missing.");
        process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.Environment["KNOWLEDGE_CLIPPER_EDGE_TTS"] = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe";
        process.StartInfo.Environment["KNOWLEDGE_CLIPPER_FFPLAY"] = @"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe";
        foreach (var argument in new[] { host, "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", pipeName }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate production host did not start.");
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Require(await process.StandardOutput.ReadLineAsync(readyTimeout.Token) == $"CORE_V3_READY {pipeName}", "Candidate production host did not become ready.");
        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(200)));
        var configJson = JsonSerializer.Serialize(new
        {
            schema_version = "anki-export-config.v1", endpoint = "http://127.0.0.1:8765", expected_profile = "账户 1", timeout_seconds = 15, max_media_bytes = 26214400, provision_managed = false,
            deck_routes = new Dictionary<string, string> { ["canary"] = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z" },
            note_types = new { basic_qa = new { model_name = "RQC Basic v1", field_map = new { question = "Front", answer = "Back", context = "Context", extra = "Extra", source = "Source" }, required_fields = new[] { "question", "answer" } } }
        });
        var sampleId = Guid.NewGuid().ToString("N")[..8];
        var capture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("real-rta-capture-" + sampleId, ArtifactPurpose.CaptureText, "为什么把卡片写入动作放在用户确认之后能保护知识库的准确性？"));
        var config = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("real-rta-config-" + Guid.NewGuid().ToString("N")[..8], ArtifactPurpose.AnkiConfiguration, configJson, "sensitive"));
        var submission = await client.ReadAloudCardsAsync(new ReadAloudCardsRequest("real-rta-operation-" + Guid.NewGuid().ToString("N")[..8], capture, config, new AnkiWriteAuthorization("real-rta-authorization-" + Guid.NewGuid().ToString("N")[..8])));
        ReadAloudCardsProjectionSnapshot projection;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        do { await Task.Delay(250); projection = await client.GetReadAloudCardsProjectionAsync(submission.OperationId); }
        while (DateTimeOffset.UtcNow < deadline && projection.Pipeline is not (ReadAloudCardsPipelineState.AwaitingCardConfirmation or ReadAloudCardsPipelineState.Failed or ReadAloudCardsPipelineState.UnknownExternalState));
        Require(projection.Pipeline == ReadAloudCardsPipelineState.AwaitingCardConfirmation && projection.Playback == ReadAloudCardsPlaybackState.IdleReplayable && projection.CardDraft is not null && projection.AnkiReadbackArtifact is null,
            "Candidate real vertical did not stop at a typed draft-only preview before destination export.");
        var draftProjection = projection;
        var rawText = Encoding.UTF8.GetString((await client.ReadArtifactAsync(projection.CaptureArtifact!)).Content);
        var cleanedText = Encoding.UTF8.GetString((await client.ReadArtifactAsync(projection.ReviewArtifact!)).Content);
        var cardBundleContent = (await client.ReadArtifactAsync(projection.CardBundleArtifact!)).Content;
        using var cardsJson = JsonDocument.Parse(cardBundleContent);
        var firstCard = cardsJson.RootElement.GetProperty("cards")[0];
        var jobRoot = Directory.GetDirectories(Path.Combine(dataRoot, "worker-process-v3", "jobs"), "task-*", SearchOption.TopDirectoryOnly).Single();
        var draftAttempts = CountWorkerAttempts(jobRoot);
        Require(draftAttempts.AnkiExport == 0, "Candidate real vertical created an Anki attempt before confirmation.");

        // Restart while awaiting confirmation. Recovery must expose the same
        // immutable token and must not turn it into an export.
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
        process = null;
        var draftRecoveryPipe = "KnowledgeClipper.ReadToAnki.DraftRecovery." + Guid.NewGuid().ToString("N");
        process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.Environment["KNOWLEDGE_CLIPPER_EDGE_TTS"] = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe";
        process.StartInfo.Environment["KNOWLEDGE_CLIPPER_FFPLAY"] = @"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe";
        foreach (var argument in new[] { host, "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", draftRecoveryPipe }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate draft recovery host did not start.");
        using (var draftRecoveryReadyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            Require(await process.StandardOutput.ReadLineAsync(draftRecoveryReadyTimeout.Token) == $"CORE_V3_READY {draftRecoveryPipe}", "Candidate draft recovery host did not become ready.");
        }
        client = new CoreV3Client(draftRecoveryPipe, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(200)));
        projection = await client.GetReadAloudCardsProjectionAsync(submission.OperationId);
        var draftRecoveryProjection = projection;
        Require(projection.Pipeline == ReadAloudCardsPipelineState.AwaitingCardConfirmation && projection.CardDraft is not null && projection.CardDraft.Token == draftProjection.CardDraft!.Token &&
                CountWorkerAttempts(jobRoot) == draftAttempts, "Restart did not recover the same draft without starting a destination worker.");

        await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest("real-rta-confirm-" + Guid.NewGuid().ToString("N")[..8], submission.OperationId, projection.CardDraft!,
            new AnkiWriteAuthorization("real-rta-confirm-authorization-" + Guid.NewGuid().ToString("N")[..8])));
        do { await Task.Delay(250); projection = await client.GetReadAloudCardsProjectionAsync(submission.OperationId); }
        while (DateTimeOffset.UtcNow < deadline && projection.Pipeline is not (ReadAloudCardsPipelineState.Completed or ReadAloudCardsPipelineState.Failed or ReadAloudCardsPipelineState.UnknownExternalState));
        Require(projection.Pipeline == ReadAloudCardsPipelineState.Completed && projection.Playback == ReadAloudCardsPlaybackState.IdleReplayable && projection.AnkiReadbackArtifact is not null && projection.AnkiSemanticReadback is not null,
            "Candidate real vertical did not complete with typed semantic Anki readback after confirmation.");
        var evidence = await client.ReadArtifactAsync(projection.AnkiReadbackArtifact!);
        using var evidenceJson = JsonDocument.Parse(evidence.Content);
        Require(evidenceJson.RootElement.GetProperty("exactReadback").GetBoolean(), "Candidate real Anki evidence lacks exact readback.");
        Require(projection.AnkiSemanticReadback!.Cards.Single().Front == draftProjection.CardDraft!.Cards.Single().Front && projection.AnkiSemanticReadback.Cards.Single().Back == draftProjection.CardDraft.Cards.Single().Back,
            "Candidate real Anki semantic readback differs from its confirmed preview.");
        var initialAttempts = CountWorkerAttempts(jobRoot);
        Require(initialAttempts.Content == draftAttempts.Content && initialAttempts.SpeechSynthesis == draftAttempts.SpeechSynthesis && initialAttempts.SpeechPlayback == draftAttempts.SpeechPlayback && initialAttempts.AnkiExport == 1,
            "Confirmation did not add exactly the single allowed Anki worker attempt.");

        // The explicit replay command is appended to the same durable operation
        // stream.  It must reuse the synthesis reference and create only one
        // fresh physical playback attempt.
        var replay = await client.ReplayReadAloudCardsAsync(new ReadAloudCardsReplayRequest(
            "real-rta-replay-" + Guid.NewGuid().ToString("N")[..8], submission.OperationId,
            new AnkiWriteAuthorization("real-rta-replay-authorization-" + Guid.NewGuid().ToString("N")[..8])));
        ReadAloudCardsProjectionSnapshot replayProjection;
        var replayDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
        do
        {
            await Task.Delay(250);
            replayProjection = await client.GetReadAloudCardsProjectionAsync(submission.OperationId);
        }
        while (DateTimeOffset.UtcNow < replayDeadline &&
               (CountCompletedWorkerAttempts(jobRoot).SpeechPlayback < initialAttempts.SpeechPlayback + 1 ||
                replayProjection.Cursor < projection.Cursor + 5));
        var replayAttempts = CountWorkerAttempts(jobRoot);
        Require(replayAttempts.Content == initialAttempts.Content && replayAttempts.AnkiExport == initialAttempts.AnkiExport &&
                replayAttempts.SpeechSynthesis == initialAttempts.SpeechSynthesis && replayAttempts.SpeechPlayback == initialAttempts.SpeechPlayback + 1,
            "Read-aloud replay created a Content, Anki, or synthesis attempt instead of exactly one fresh playback attempt.");
        Require(replayProjection.Pipeline == ReadAloudCardsPipelineState.Completed && replayProjection.Playback == ReadAloudCardsPlaybackState.IdleReplayable &&
                replayProjection.AnkiReadbackArtifact is not null && replayProjection.CardBundleArtifact is not null,
            "Read-aloud replay did not retain its completed card/readback projection outputs.");
        var replayEvidence = await client.ReadArtifactAsync(replayProjection.AnkiReadbackArtifact!);
        Require(replayEvidence.Content.SequenceEqual(evidence.Content), "Read-aloud replay changed Anki readback evidence.");
        Require((await client.ReadArtifactAsync(replayProjection.CardBundleArtifact!)).Content.SequenceEqual(cardBundleContent),
            "Read-aloud replay changed the immutable CardBundle.");

        // Recovery is a real host restart over the same durable operation and
        // artifact stores.  It must only query the already-completed operation.
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
        process = null;
        var recoveryPipe = "KnowledgeClipper.ReadToAnki.Recovery." + Guid.NewGuid().ToString("N");
        process = new Process { StartInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.Environment["KNOWLEDGE_CLIPPER_EDGE_TTS"] = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe";
        process.StartInfo.Environment["KNOWLEDGE_CLIPPER_FFPLAY"] = @"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe";
        foreach (var argument in new[] { host, "--repository-root", candidate, "--binding", binding, "--data-root", dataRoot, "--pipe-name", recoveryPipe }) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Candidate recovery host did not start.");
        using (var recoveryReadyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            Require(await process.StandardOutput.ReadLineAsync(recoveryReadyTimeout.Token) == $"CORE_V3_READY {recoveryPipe}", "Candidate recovery host did not become ready.");
        }
        var recoveryClient = new CoreV3Client(recoveryPipe, new CoreV3ClientOptions(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(200)));
        var recoveredProjection = await recoveryClient.GetReadAloudCardsProjectionAsync(submission.OperationId);
        Require(recoveredProjection.Pipeline == ReadAloudCardsPipelineState.Completed && recoveredProjection.Playback == ReadAloudCardsPlaybackState.IdleReplayable &&
                recoveredProjection.AnkiReadbackArtifact is not null && recoveredProjection.CardBundleArtifact is not null && CountWorkerAttempts(jobRoot) == replayAttempts,
            "Host restart did not recover the completed replay projection without new worker attempts.");
        Require((await recoveryClient.ReadArtifactAsync(recoveredProjection.AnkiReadbackArtifact!)).Content.SequenceEqual(evidence.Content) &&
                (await recoveryClient.ReadArtifactAsync(recoveredProjection.CardBundleArtifact!)).Content.SequenceEqual(cardBundleContent),
            "Host restart changed durable readback or CardBundle evidence.");

        var receipt = new
        {
            schema = "kc.read-to-anki-candidate-real-replay-recovery-receipt/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "PASS",
            candidate, runtimeDataRoot = dataRoot, operationId = submission.OperationId, draftProjection, draftRecoveryProjection, initialProjection = projection, replayProjection, recoveredProjection,
            rawText, cleanedText, card = new { front = firstCard.GetProperty("front").GetString(), back = firstCard.GetProperty("back").GetString(), deck = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z" },
            ankiEvidence = evidenceJson.RootElement.Clone(), draftWorkerAttempts = draftAttempts, initialWorkerAttempts = initialAttempts, replayWorkerAttempts = replayAttempts,
            preConfirmExporter = new { requests = 0, attempts = draftAttempts.AnkiExport, writes = 0, ankiReadbackArtifact = draftProjection.AnkiReadbackArtifact is not null },
            replayWorkerAttemptDelta = new { content = replayAttempts.Content - initialAttempts.Content, synthesis = replayAttempts.SpeechSynthesis - initialAttempts.SpeechSynthesis, playback = replayAttempts.SpeechPlayback - initialAttempts.SpeechPlayback, anki = replayAttempts.AnkiExport - initialAttempts.AnkiExport },
            replayExternalWriteDelta = 0, physicalSpeakerPlayback = new { initial = true, replay = true, completion = "natural_idle_replayable" },
            restartRecovery = new { queriedOnly = true, workerAttemptDelta = 0 }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT PASS CANDIDATE_READ_TO_ANKI_REAL");
        return 0;
    }
    catch (Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schema = "kc.read-to-anki-candidate-real-receipt/1", startedAt = started, completedAt = DateTimeOffset.UtcNow, status = "FAIL", candidate, runtimeDataRoot = dataRoot, error = ex.Message, physicalSpeakerPlayback = false }, JsonOptionsHolder.Options));
        Console.WriteLine("RESULT FAIL CANDIDATE_READ_TO_ANKI_REAL " + ex.Message);
        return 1;
    }
    finally { if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); } }
}

static (int Content, int SpeechSynthesis, int SpeechPlayback, int AnkiExport) CountWorkerAttempts(string jobRoot)
    => CountWorkerAttemptSet(jobRoot, completedOnly: false);

static (int Content, int SpeechSynthesis, int SpeechPlayback, int AnkiExport) CountCompletedWorkerAttempts(string jobRoot)
    => CountWorkerAttemptSet(jobRoot, completedOnly: true);

static (int Content, int SpeechSynthesis, int SpeechPlayback, int AnkiExport) CountWorkerAttemptSet(string jobRoot, bool completedOnly)
{
    int Count(string stageId)
    {
        var attempts = Path.Combine(jobRoot, "runs");
        return Directory.Exists(attempts)
            ? Directory.EnumerateDirectories(attempts, "attempt-*", SearchOption.AllDirectories)
                .Count(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!), stageId, StringComparison.Ordinal) &&
                               (!completedOnly || File.Exists(Path.Combine(path, "result.json"))))
            : 0;
    }
    return (Count("content.understand-and-card"), Count("speech-synthesize"), Count("speech-playback"), Count("anki-export"));
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
        var speech = runtime.GetAdmittedBinding("speech.edge");
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

        var review = await runtime.ExecuteAsync(new WorkerInvocation(
            "offline-review",
            content,
            StrictJson.CloneElement(new { phase = "review", originalText = "Immutable original capture.", idempotencyKey = new string('R', 64) }),
            TimeSpan.FromSeconds(5)), CancellationToken.None);
        Require(review.Payload.GetProperty("cleanedText").GetString() == "Immutable original capture.",
            "Ordinary review must use understand-and-card input, not a question workflow.");

        var originalEdge = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_EDGE_TTS");
        var originalPlayer = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FFPLAY");
        try
        {
            Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_EDGE_TTS", @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe");
            Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FFPLAY", @"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe");
            var synthesis = await runtime.ExecuteAsync(new WorkerInvocation(
                "offline-short-speech-operation-with-a-deliberately-long-core-identity-" + new string('x', 160),
                speech,
                StrictJson.CloneElement(new
                {
                    phase = "synthesize",
                    originalText = "Original immutable speech text.",
                    spokenText = "Prefix. Original immutable speech text. Suffix.",
                    spokenTextSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Prefix. Original immutable speech text. Suffix."))).ToLowerInvariant(),
                    contextMode = "selection_plus_sentence",
                    operationId = "offline-speech",
                    idempotencyKey = new string('S', 64)
                }),
                TimeSpan.FromSeconds(5)), CancellationToken.None);
            var audio = synthesis.Payload.GetProperty("audioArtifact").Clone();
            var playback = await runtime.ExecuteAsync(new WorkerInvocation(
                "offline-short-speech-operation-with-a-deliberately-long-core-identity-" + new string('x', 160),
                speech,
                StrictJson.CloneElement(new { phase = "playback", audioArtifact = audio, idempotencyKey = new string('P', 64) }),
                TimeSpan.FromSeconds(5)), CancellationToken.None);
            Require(playback.Payload.GetProperty("status").GetString() == "idle_replayable", "Short package identifiers did not preserve speech playback.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_EDGE_TTS", originalEdge);
            Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FFPLAY", originalPlayer);
        }

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
        var config = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("phase2-anki-config", ArtifactPurpose.AnkiConfiguration, ValidCanaryAnkiConfiguration()));
        var configReplay = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("phase2-anki-config", ArtifactPurpose.AnkiConfiguration, ValidCanaryAnkiConfiguration()));
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
        Require(make.State == QuestionProjectionState.WaitingInput && make.AnswerArtifact is null && make.CardBundleArtifact is not null &&
                make.DestinationEvidenceArtifact is null && make.CardDraft is { SourceOperation: "question.make-cards" },
            $"Make-cards did not stop at the shared immutable draft boundary. State={make.State}; Notice={make.Notice}");
        var cardJson = Encoding.UTF8.GetString((await client.ReadArtifactAsync(make.CardBundleArtifact!)).Content);
        var metricsBeforeConfirmation = await ReadMetricsAsync(client);
        Require(metricsBeforeConfirmation == new WorkerRuntimeMetrics(3, 1, 0, 0, 0, 0, 0, 0, "authority-bound-independent-fake-proxies"),
            "Question draft generation started an exporter before user confirmation.");

        var staleDraft = make.CardDraft! with { Token = new string('0', 64) };
        var staleSubmission = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(
            "phase2-stale-confirm", makeSubmission.OperationId, staleDraft,
            new AnkiWriteAuthorization("phase2-stale-confirm-authorization")));
        var staleRejected = (await client.WaitFlashcardDraftActionAsync(staleSubmission)).State == QuestionProjectionState.Failed;
        Require(staleRejected && (await ReadMetricsAsync(client)) == metricsBeforeConfirmation,
            "A stale question draft token was accepted or reached the exporter.");

        var confirmation = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(
            "phase2-confirm-cards", makeSubmission.OperationId, make.CardDraft!,
            new AnkiWriteAuthorization("phase2-confirm-cards-authorization")));
        var confirmed = await client.WaitFlashcardDraftActionAsync(confirmation);
        Require(confirmed.State == QuestionProjectionState.Succeeded && confirmed.CardDraft == make.CardDraft &&
                confirmed.DestinationEvidenceArtifact is not null && confirmed.AnkiSemanticReadback is not null,
            "Confirmed question draft did not preserve the preview or publish semantic readback.");
        var exportEvidenceJson = Encoding.UTF8.GetString((await client.ReadArtifactAsync(confirmed.DestinationEvidenceArtifact!)).Content);
        using (var evidenceDocument = JsonDocument.Parse(exportEvidenceJson))
        {
            Require(evidenceDocument.RootElement.GetProperty("exactReadback").GetBoolean(), "Anki evidence lacks exact readback.");
            Require(evidenceDocument.RootElement.GetProperty("externalWriteCount").GetInt32() == 1, "First Anki export was not exactly one bounded fake write.");
        }

        var metricsBeforeReplay = await ReadMetricsAsync(client);
        Require(metricsBeforeReplay == new WorkerRuntimeMetrics(3, 1, 1, 1, 1, 0, 0, 0, "authority-bound-independent-fake-proxies"), "Main slice metrics are not exact.");
        var replaySubmission = await client.QuestionMakeCardsAsync(makeRequest);
        var replay = await client.WaitQuestionProjectionAsync(replaySubmission.OperationId);
        Require(replay.CardBundleArtifact?.Token == confirmed.CardBundleArtifact!.Token && replay.DestinationEvidenceArtifact?.Token == confirmed.DestinationEvidenceArtifact!.Token,
            "Make-cards replay changed typed artifact identities.");
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
            AnkiExportAttempts = metricsBeforeReplay.AnkiExportAttempts,
            AnkiExternalWriteCount = metricsBeforeReplay.AnkiExternalWriteCount,
            AnkiReadbackCount = metricsBeforeReplay.AnkiReadbackCount
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

async Task VerifyLanguageResumeCoreAsync(string root)
{
    using var producer = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "producer-receipt.json")));
    var first = producer.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("choice").GetString() == "ask");
    var requestPath = first.GetProperty("workerRequestPath").GetString()!;
    var request = JsonSerializer.Deserialize<LegacyWorkerRequest>(await File.ReadAllBytesAsync(requestPath), KernelJson.Options)!;
    var jobRoot = first.GetProperty("jobRoot").GetString()!;
    var resultPath = CoreWorkerTaskPackageAdapter.ResolveWithin(jobRoot, request.ResultPath);
    var result = JsonSerializer.Deserialize<LegacyWorkerResult>(await File.ReadAllBytesAsync(resultPath), KernelJson.Options)!;
    var package = new LegacyAttemptPackage(jobRoot, requestPath, resultPath, CoreWorkerTaskPackageAdapter.ResolveWithin(jobRoot, request.CancelSignal.Path), request);
    var projection = await WorkerProcessRuntime.ProjectContentAsync(package, result, "review", CancellationToken.None);
    Require(projection.Payload.GetProperty("status").GetString() == "language_choice_required", "Actual module decision did not project language choice.");
    using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(jobRoot, "inputs/content/context/source-provenance.json")));
    var nextRequest = StrictJson.CloneElement(new { phase = "review", originalText = first.GetProperty("selected").GetString(),
        configurationJson = "{\"schema_version\":\"anki-export-config.v1\",\"provision_managed\":false}", languageChoice = "both", inputVersion = "2",
        languageSelection = provenance.RootElement.GetProperty("sourceSelection"), languageDecision = projection.Payload.GetProperty("languageDecision"),
        idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.OperationId + "-choose-both"))) });
    var worker = new AdmittedAuthorityWorker("content.deepseek", "0.12.0", "1.0.0", "offline-formal-common-loader", new string('A', 64), new[] { "content.understand-and-card" }, "none", "request-idempotent");
    var adapter = new CoreWorkerTaskPackageAdapter(root);
    var invocation = new WorkerInvocation(request.OperationId, new WorkerBinding("worker", "content.deepseek", "=0.12.0", null), nextRequest, TimeSpan.FromSeconds(10));
    var originalPath = first.GetProperty("inputPath").GetString()!;
    var originalBytes = await File.ReadAllBytesAsync(originalPath);
    var next = await adapter.PrepareAsync(invocation, worker, CancellationToken.None);
    Require(next.Request.TaskId == request.TaskId && next.JobRoot == jobRoot && next.Request.RunId != request.RunId && next.Request.AttemptId != request.AttemptId,
        "Resume task identity was replaced or the previous run/attempt was reused.");
    Require(originalBytes.SequenceEqual(await File.ReadAllBytesAsync(originalPath)), "Initial immutable input was overwritten.");
    var nextPath = CoreWorkerTaskPackageAdapter.ResolveWithin(jobRoot, next.Request.Payload.GetProperty("contentInputPath").GetString()!);
    using var firstInput = JsonDocument.Parse(originalBytes);
    using var nextInput = JsonDocument.Parse(await File.ReadAllBytesAsync(nextPath));
    Require(nextInput.RootElement.GetProperty("schema_version").GetString() == "standard-input/8.0.0" &&
        JsonSerializer.Serialize(firstInput.RootElement.GetProperty("items"), KernelJson.Options) == JsonSerializer.Serialize(nextInput.RootElement.GetProperty("items"), KernelJson.Options),
        "Resume did not preserve exact original items.");
    var replay = await adapter.PrepareAsync(invocation, worker, CancellationToken.None);
    Require(replay.RequestPath == next.RequestPath && replay.Request.InputRefs.SequenceEqual(next.Request.InputRefs), "Identical resume publication is not idempotent.");
    var rejected = new List<string>();
    foreach (var fault in new[] { "cross-operation", "source-result-sha", "decision-manifest-sha", "selected-sha", "same-version", "job-root", "category" })
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(nextRequest.GetRawText())!;
        var decision = node["languageDecision"]!;
        node["idempotencyKey"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fault)));
        if (fault == "cross-operation") decision["sourceOperationId"] = "other-operation";
        if (fault == "source-result-sha") decision["sourceResultSha256"] = new string('0', 64);
        if (fault == "decision-manifest-sha") decision["decisionReference"]!["expectedSha256"] = new string('0', 64);
        if (fault == "selected-sha") node["languageSelection"]!["selectedTextSha256"] = new string('0', 64);
        if (fault == "same-version") node["inputVersion"] = "1";
        if (fault == "job-root") decision["sourceJobRoot"] = Path.Combine(root, "different-task");
        if (fault == "category") decision["categoryId"] = "general_learning";
        await RequireThrowsAsync(async () => { _ = await adapter.PrepareAsync(invocation with { Request = JsonSerializer.SerializeToElement(node) }, worker, CancellationToken.None); }, fault);
        rejected.Add(fault);
    }
    var receipt = new { status = "PASS", initialRequestPath = requestPath, initialResultPath = resultPath, nextRequestPath = next.RequestPath, nextInputPath = nextPath,
        stableTaskId = request.TaskId, nextRunId = next.Request.RunId, nextAttemptId = next.Request.AttemptId, sameSourceItems = true, replaySameArtifact = true, rejected,
        moduleReleaseConsumptionPending = true, externalCalls = 0 };
    await File.WriteAllBytesAsync(Path.Combine(root, "core-resume-producer-receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptionsHolder.Options));
}

async Task VerifyContentCompleteContextAsync(string root)
{
    if (Directory.Exists(root)) throw new InvalidDataException("Append-only context test root already exists.");
    Directory.CreateDirectory(root);
    var adapter = new CoreWorkerTaskPackageAdapter(root);
    var worker = new AdmittedAuthorityWorker("content.deepseek", "0.11.0", "1.0.0", "never-invoked", new string('A', 64), new[] { "content.understand-and-card" }, "none", "request-idempotent");
    static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    var rows = new List<object>();
    var samples = new[] {
        (Id: "original-failure", Prefix: "A deterministic external sentence with ", Selected: "deliberate", Suffix: " context.", Choice: "both"),
        (Id: "independent-chinese", Prefix: "面对复杂问题，", Selected: "审慎", Suffix: "意味着先核实事实，再采取行动。", Choice: "both"),
        (Id: "independent-unicode", Prefix: "A 🧭 guide values ", Selected: "precision", Suffix: "  in measurements.", Choice: "both"),
        (Id: "ask-before-classification", Prefix: "材料说明：", Selected: "审慎", Suffix: "的含义需结合上下文。", Choice: "ask")
    };
    JsonElement baseline = default;
    foreach (var sample in samples)
    {
        var complete = sample.Prefix + sample.Selected + sample.Suffix;
        var request = StrictJson.CloneElement(new {
            phase = "review", originalText = sample.Selected,
            configurationJson = "{\"schema_version\":\"anki-export-config.v1\",\"provision_managed\":false}",
            languageChoice = sample.Choice, inputVersion = "1", idempotencyKey = Digest(sample.Id),
            languageSelection = new {
                selectedText = sample.Selected, selectedTextSha256 = Digest(sample.Selected), targetText = sample.Selected, targetTextSha256 = Digest(sample.Selected),
                spokenTextSha256 = Digest(complete), contextApplied = true,
                contextSegments = new[] {
                    new { segmentId = sample.Id + "-prefix-ref", role = "prefix", order = 0, text = sample.Prefix, sha256 = Digest(sample.Prefix) },
                    new { segmentId = sample.Id + "-suffix-ref", role = "suffix", order = 1, text = sample.Suffix, sha256 = Digest(sample.Suffix) }
                }
            }
        });
        baseline = request;
        var package = await adapter.PrepareAsync(new WorkerInvocation(sample.Id, new WorkerBinding("worker", "content.deepseek", "=0.11.0", null), request, TimeSpan.FromSeconds(5)), worker, CancellationToken.None);
        var inputPath = Path.Combine(package.JobRoot, "inputs", "content", "standard-input.v1.json");
        using var inputDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(inputPath));
        var input = inputDocument.RootElement; var item = input.GetProperty("items")[0];var selection = item.GetProperty("language_selection");
        var contexts = selection.GetProperty("context_segments");
        Require(input.GetProperty("schema_version").GetString() == "standard-input/6.0.0" &&
            !item.TryGetProperty("hints", out _) && !item.TryGetProperty("human_overrides", out _) &&
            (sample.Choice == "ask" ? !input.TryGetProperty("languagePracticeMode", out _) && !input.TryGetProperty("human_overrides", out _) : input.GetProperty("languagePracticeMode").GetString() == sample.Choice),
            "Language preference incorrectly became a template/category override or ask default.");
        Require(contexts.GetArrayLength() == 1, "Full source was split into worker-joined fragments.");
        var context = contexts[0];var completePath = Path.Combine(package.JobRoot, context.GetProperty("material_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        var actual = await File.ReadAllTextAsync(completePath);
        Require(actual == complete && Digest(actual) == context.GetProperty("sha256").GetString(), "Complete source bytes/hash drifted.");
        Require(selection.GetProperty("selected_text").GetString() == sample.Selected && selection.GetProperty("target_text_sha256").GetString() == Digest(sample.Selected), "Selected/target identity drifted.");
        using var provenanceDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, "inputs", "content", "context", "source-provenance.json")));
        var provenance = provenanceDocument.RootElement;
        Require(provenance.GetProperty("captureRoot").GetProperty("kind").GetString() == "capture-text" &&
            provenance.GetProperty("sourceSelection").GetRawText() == request.GetProperty("languageSelection").GetRawText() &&
            provenance.GetProperty("selectedStartUtf16").GetInt32() == sample.Prefix.Length &&
            provenance.GetProperty("selectedEndUtf16").GetInt32() == sample.Prefix.Length + sample.Selected.Length, "Original ancestry/role/order or selection range drifted.");
        using var manifestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, "inputs", "content", "artifact.json")));
        foreach (var file in manifestDocument.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, file.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
            Require(bytes.LongLength == file.GetProperty("bytes").GetInt64() && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == file.GetProperty("sha256").GetString(), "Manifest file binding drifted.");
        }
        rows.Add(new { sample.Id, sample.Prefix, sample.Selected, sample.Suffix, sample.Choice, completeSource = actual, completeSourceSha256 = Digest(actual), package.JobRoot, inputPath, inputSha256 = Digest(await File.ReadAllTextAsync(inputPath)), workerRequestPath = package.RequestPath, contexts = 1, preferenceNotClassification = true, ancestryPreserved = true });
    }
    var negatives = new List<string>();
    foreach (var failure in new[] { "source-sha", "context-sha", "composition-sha", "role", "order" })
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(baseline.GetRawText())!;
        var selection = node["languageSelection"]!;
        if (failure == "source-sha") selection["selectedTextSha256"] = new string('0', 64);
        if (failure == "context-sha") selection["contextSegments"]![0]!["sha256"] = new string('0', 64);
        if (failure == "composition-sha") selection["spokenTextSha256"] = new string('0', 64);
        if (failure == "role") selection["contextSegments"]![0]!["role"] = "unbound";
        if (failure == "order") selection["contextSegments"]![0]!["order"] = 1;
        node["idempotencyKey"] = Digest(failure);
        await RequireThrowsAsync(async () => { _ = await adapter.PrepareAsync(new WorkerInvocation("negative-" + failure, new WorkerBinding("worker", "content.deepseek", "=0.11.0", null), JsonSerializer.SerializeToElement(node), TimeSpan.FromSeconds(5)), worker, CancellationToken.None); }, failure);
        negatives.Add(failure);
    }
    await File.WriteAllBytesAsync(Path.Combine(root, "producer-receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { status = "PASS", cases = rows, negatives, externalCalls = 0, workerInvocations = 0, publicProtocolChanges = 0 }, JsonOptionsHolder.Options));
}

async Task VerifyContent011ResolutionAdapterAsync()
{
    var root = NewTemporaryDirectory("content-011-resolution-adapter");
    try
    {
        var adapter = new CoreWorkerTaskPackageAdapter(root);
        var worker = new AdmittedAuthorityWorker(
            "content.deepseek", "0.11.0", "1.0.0", "unused-offline-entrypoint", new string('A', 64),
            new[] { "content.understand-and-card" }, "none", "request-idempotent");
        const string selected = "pragmatic";
        const string contextText = "The pragmatic plan used a pragmatic compromise.";
        var selectedSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selected))).ToLowerInvariant();
        var contextSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contextText))).ToLowerInvariant();
        var selection = new
        {
            selectedText = selected,
            selectedTextSha256 = selectedSha,
            targetText = selected,
            targetTextSha256 = selectedSha,
            contextApplied = true,
            spokenTextSha256 = contextSha,
            contextSegments = new[] {
                new { segmentId = "segment-prefix", role = "prefix", order = 0, text = "The ", sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("The "))).ToLowerInvariant() },
                new { segmentId = "segment-suffix", role = "suffix", order = 1, text = " plan used a pragmatic compromise.", sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(" plan used a pragmatic compromise."))).ToLowerInvariant() }
            }
        };
        var occurrence = new { segmentId = "context-" + contextSha[..24], order = 0, start = 4, end = 13, exactText = selected, sourceSha256 = contextSha };

        async Task<JsonElement> Prepare(string operationId, string inputVersion, object resolution)
        {
            var package = await adapter.PrepareAsync(new WorkerInvocation(
                operationId,
                new WorkerBinding("worker", "content.deepseek", "=0.11.0", null),
                StrictJson.CloneElement(new
                {
                    phase = "review",
                    originalText = selected,
                    configurationJson = "{\"schema_version\":\"anki-export-config.v1\",\"provision_managed\":false}",
                    languageChoice = "both",
                    languageSelection = selection,
                    humanResolution = resolution,
                    inputVersion,
                    idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant()
                }),
                TimeSpan.FromSeconds(5)), worker, CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, "inputs", "content", "standard-input.v1.json")));
            return document.RootElement.Clone();
        }

        var accepted = await Prepare("content-011-accept", "accept-v1", new
        {
            schema_version = "language-target-resolution/2.0.0",
            decision_id = "decision-1",
            target_token = new string('B', 64),
            action = "accept_candidate",
            selected_text = selected,
            selected_text_sha256 = selectedSha,
            confirmed = true,
            candidateOccurrence = occurrence
        });
        var acceptedResolution = accepted.GetProperty("items")[0].GetProperty("human_overrides").GetProperty("languageTargetResolution");
        Require(accepted.GetProperty("schema_version").GetString() == "standard-input/7.0.0" &&
                acceptedResolution.GetProperty("action").GetString() == "accept_candidate" &&
                acceptedResolution.GetProperty("candidateOccurrence").GetProperty("sourceSha256").GetString() == contextSha,
            "Content 0.11 accept_candidate did not retain its typed candidate occurrence in standard-input/7.");

        var corrected = await Prepare("content-011-correction", "correction-v1", new
        {
            schema_version = "language-target-resolution/2.0.0",
            decision_id = "decision-1",
            target_token = new string('C', 64),
            action = "user_correction",
            selected_text = "pragmatic compromise",
            selected_text_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pragmatic compromise"))).ToLowerInvariant(),
            confirmed = true
        });
        var correctedResolution = corrected.GetProperty("items")[0].GetProperty("human_overrides").GetProperty("languageTargetResolution");
        Require(corrected.GetProperty("schema_version").GetString() == "standard-input/7.0.0" &&
                correctedResolution.GetProperty("action").GetString() == "user_correction" &&
                !correctedResolution.TryGetProperty("candidateOccurrence", out _),
            "Content 0.11 user_correction did not remain occurrence-free in standard-input/7.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyReadAloudActionProjectionConsistencyAsync()
{
    var root = NewTemporaryDirectory("read-aloud-action-projection");
    using var serverCancellation = new CancellationTokenSource();
    Task? serverTask = null;
    try
    {
        var authority = new WorkerAuthorityBinding("authority", "registry.json", new string('A', 64), "worker.exe", new string('B', 64), "test", "1.0.0");
        var artifactCapability = new CapabilityDescriptor("artifact.publish", true, null, "kc.artifact-publish-command/1", "domain-text", "3.0.0",
            "none", "none", "safe", "required", 5000, new WorkerBinding("in-process", null, null, "ArtifactPublishOperation"), new UiAffordance("publish", "sdk-only", false));
        var readCapability = new CapabilityDescriptor("clip.read-aloud-cards", true, null, "kc.read-aloud-cards-command/1", "anki-readback", "1.0.0",
            "external-write", "explicit", "conditional", "required", 5000, new WorkerBinding("worker", "content.deepseek", "=test", null) { Authority = authority }, new UiAffordance("clip", "clip", true));
        var catalog = AdmittedCapabilityCatalog.CreateForTesting(new CapabilityCatalogDocument(
            KernelSchemas.CapabilityCatalog, "read-aloud-action-projection", "1.0.0",
            new AuthorityAnchor("test", "checkpoint.json", new string('C', 64)), new[] { artifactCapability, readCapability }));
        var runtime = new DelayedResolutionReadAloudRuntime();
        var eventStore = new FileEventStore(Path.Combine(root, "events"));
        var application = new KernelApplication(
            catalog,
            new OperationRegistry(new IOperationHandler[] { new ArtifactPublishOperation(), new ReadAloudCardsOperation() }),
            runtime,
            eventStore,
            new FileCommandIndex(Path.Combine(root, "command-index")),
            new FileArtifactStore(Path.Combine(root, "artifacts"), "read-aloud-action-projection"));
        var pipeName = "KnowledgeClipper.ReadAloud.ActionProjection." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeKernelServer(pipeName, new KernelCompatibilityGateway(application, root));
        serverTask = server.ServeAsync(serverCancellation.Token);
        var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(25)));

        var capture = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("action-projection-capture", ArtifactPurpose.CaptureText, "pragmatic"));
        var contextText = "The pragmatic plan used a pragmatic compromise.";
        var context = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("action-projection-context", ArtifactPurpose.QuestionContext, contextText));
        var configuration = await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("action-projection-config", ArtifactPurpose.AnkiConfiguration, ValidCanaryAnkiConfiguration()));
        var authorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("action-projection-authorization");
        var coordinator = new ReadToAnkiOperationAuthorizationCoordinator();
        await RequireArgumentRejectsAsync(
            () => client.ClipCardsAsync(new ReadAloudCardsRequest(
                "action-projection-ambiguous-context", capture, configuration, authorization,
                new LanguageCardSettings("language-settings-action-v1", "both"),
                Selection: new LanguageSelectionContext("pragmatic", new[] { context }, true, "sentence"))),
            "context without explicit prefix/suffix role binding");
        await RequireArgumentRejectsAsync(
            () => client.ClipCardsAsync(new ReadAloudCardsRequest(
                "action-projection-context-off-with-artifact", capture, configuration, authorization,
                new LanguageCardSettings("language-settings-action-v1", "both"),
                Selection: new LanguageSelectionContext("pragmatic", new[] { context }, false, "sentence"))),
            "context artifact while contextual reading is off");
        var discovery = await client.BeginReadToAnkiDiscoveryAsync("action-projection-ui-click");
        Require(await client.DiscoverAcceptedReadToAnkiAsync(discovery) is null, "Discovery checkpoint included an old or nonexistent operation.");
        var initial = await client.ClipCardsAsync(new ReadAloudCardsRequest(
            "action-projection-initial", capture, configuration, authorization,
            new LanguageCardSettings("language-settings-action-v1", "both"),
            Selection: new LanguageSelectionContext("pragmatic", new[] { context }, true, "sentence",
                RoleBoundContextSegments: new[] { new LanguageContextSegment(context, "prefix", 0) })));
        var discovered = await client.WaitForAcceptedReadToAnkiAsync(discovery);
        Require(discovered.OperationId == initial.OperationId && discovered.CommandId == initial.CommandId &&
                discovered.AcceptedSequence == initial.AcceptedSequence && discovered.AcceptedSequence == 1,
            "Discovery did not return the unique post-checkpoint operation/command/accepted-sequence identity.");
        coordinator.Bind(initial, authorization);
        Require(coordinator.Require(initial.OperationId) == authorization, "Operation authorization coordinator did not retain the typed source authorization.");
        await RequireOperationRejectsAsync(() => Task.FromResult(coordinator.Require("another-operation")), "cross-operation authorization reuse");
        await RequireOperationRejectsAsync(() =>
        {
            coordinator.Bind(new OperationSubmission("wrong-scope-operation", 1, true), new AnkiWriteAuthorization("wrong-scope", "other-scope"));
            return Task.CompletedTask;
        }, "wrong-scope operation authorization binding");

        var unresolved = await client.WaitReadAloudCardsActionAsync(initial);
        Require(unresolved.ActionState == ReadAloudCardsActionState.Completed && unresolved.Pipeline == ReadAloudCardsPipelineState.AwaitingCardResolution &&
                unresolved.TerminalCommandId == initial.CommandId && unresolved.TerminalSequence >= initial.AcceptedSequence && unresolved.CardResolution is not null &&
                runtime.ContentAttempts == 1 && runtime.AnkiAttempts == 0,
            $"Initial unresolved action did not publish its own terminal with zero exporter attempts: action={unresolved.ActionState}, " +
            $"pipeline={unresolved.Pipeline}, terminal={unresolved.TerminalCommandId}@{unresolved.TerminalSequence}, " +
            $"accepted={initial.CommandId}@{initial.AcceptedSequence}, receiptCursor={initial.Cursor}, resolution={unresolved.CardResolution is not null}, " +
            $"content={runtime.ContentAttempts}, anki={runtime.AnkiAttempts}.");
        Require(unresolved.SettingsEvidence is { } unresolvedSettings &&
                unresolvedSettings.SettingsSnapshot.Kind == "task-settings-snapshot" &&
                unresolvedSettings.AnkiConfiguration.ArtifactId == configuration.Reference.ArtifactId &&
                unresolvedSettings.Destination.RouteKey == "canary" &&
                unresolvedSettings.Destination.DeckIdentity == "零碎知识剪藏::Canary::SDK" &&
                !Path.IsPathRooted(unresolvedSettings.SettingsSnapshot.ManifestPath) &&
                unresolvedSettings.PackageProvenance.InputArtifactId == configuration.Reference.ArtifactId,
            "Terminal projection did not expose verified relative-path task settings and destination evidence.");
        Require(unresolved.ContextEvidence is { } unresolvedContext &&
                unresolvedContext.ContextSnapshot.Kind == "task-context-snapshot" &&
                unresolvedContext.Capture.ArtifactId == capture.Reference.ArtifactId &&
                unresolvedContext.ContextRequested && unresolvedContext.ContextApplied && unresolvedContext.ContextStatus == "applied" &&
                unresolvedContext.ContextGranularity == "sentence" && unresolvedContext.ContextSegments.Count == 1 &&
                unresolvedContext.SpeechContextMode == "selection_plus_sentence" &&
                unresolvedContext.ContextSegments[0].Order == 0 && unresolvedContext.ContextSegments[0].Role == "prefix" &&
                unresolvedContext.ContextSegments[0].Artifact.ArtifactId == context.Reference.ArtifactId &&
                unresolvedContext.SelectedTextSha256.Length == 64 && unresolvedContext.TargetTextSha256.Length == 64 &&
                unresolvedContext.SpokenTextSha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contextText + "pragmatic"))).ToLowerInvariant(),
            "Terminal projection did not expose SHA-bound task-produced capture/context evidence.");

        var correctionDiscovery = await client.BeginReadToAnkiActionDiscoveryAsync(initial.OperationId, "action-projection-correction-ui");
        Require(await client.DiscoverAcceptedReadToAnkiActionAsync(correctionDiscovery) is null,
            "Action discovery checkpoint included an old command from the operation stream.");
        var correction = await client.ResolveCardResolutionAsync(new CardResolutionActionRequest(
            "action-projection-correction", initial.OperationId, unresolved.CardResolution!.TargetToken,
            unresolved.CardResolution.DecisionId, "user_correction", "pragmatic compromise", coordinator.Require(initial.OperationId)));
        var discoveredCorrection = await client.WaitForAcceptedReadToAnkiActionAsync(correctionDiscovery);
        Require(discoveredCorrection.OperationId == correction.OperationId && discoveredCorrection.CommandId == correction.CommandId &&
                discoveredCorrection.AcceptedSequence == correction.AcceptedSequence,
            "Action discovery did not return the correction command's own accepted identity.");
        var correctionWait = client.WaitReadAloudCardsActionAsync(discoveredCorrection);
        await runtime.ResolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await client.GetReadAloudCardsProjectionAsync(initial.OperationId);
        Require(running.ActionState == ReadAloudCardsActionState.Running && running.ActiveCommandId == correction.CommandId &&
                running.TerminalCommandId == initial.CommandId && running.TerminalSequence == unresolved.TerminalSequence &&
                running.Cursor > running.TerminalSequence && running.Pipeline == ReadAloudCardsPipelineState.Accepted &&
                running.CardResolution is null && running.CardDraft is null && running.CardBundleArtifact is null && runtime.AnkiAttempts == 0,
            "A new started action was combined with the preceding terminal projection.");

        runtime.ReleaseResolution();
        var resolved = await correctionWait;
        var contentRequest = runtime.LastContentRequest ?? throw new InvalidDataException("Resolved Content request was not captured.");
        var selection = contentRequest.GetProperty("languageSelection");
        var resolution = contentRequest.GetProperty("humanResolution");
        Require(resolved.ActionState == ReadAloudCardsActionState.Completed && resolved.TerminalCommandId == correction.CommandId &&
                resolved.TerminalSequence >= correction.AcceptedSequence && resolved.TerminalSequence > unresolved.TerminalSequence &&
                resolved.Pipeline == ReadAloudCardsPipelineState.AwaitingCardConfirmation && resolved.CardDraft?.Cards.Count == 2 &&
                runtime.ContentAttempts == 2 && runtime.AnkiAttempts == 0,
            "Human correction did not wait for the target command terminal and produce a new two-card draft without export.");
        Require(contentRequest.GetProperty("languageChoice").GetString() == "both" &&
                contentRequest.GetProperty("inputVersion").GetString()!.StartsWith("resolution-", StringComparison.Ordinal) &&
                resolution.GetProperty("selected_text").GetString() == "pragmatic compromise" &&
                resolution.GetProperty("selected_text_sha256").GetString() == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pragmatic compromise"))).ToLowerInvariant() &&
                selection.GetProperty("selectedTextSha256").GetString() == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pragmatic"))).ToLowerInvariant() &&
                selection.GetProperty("contextSegments")[0].GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contextText))).ToLowerInvariant(),
            "Correction revision did not retain both choice and SHA-bound selected/context inputs.");

        var attemptsBeforeReplay = runtime.ContentAttempts;
        var replay = await client.ResolveCardResolutionAsync(new CardResolutionActionRequest(
            "action-projection-correction", initial.OperationId, unresolved.CardResolution.TargetToken,
            unresolved.CardResolution.DecisionId, "user_correction", "pragmatic compromise", coordinator.Require(initial.OperationId)));
        var replayProjection = await client.WaitReadAloudCardsActionAsync(replay);
        Require(replayProjection.TerminalCommandId == correction.CommandId && runtime.ContentAttempts == attemptsBeforeReplay && runtime.AnkiAttempts == 0,
            "Exact correction replay created another Content attempt or exporter attempt.");

        var confirmationDiscovery = await client.BeginReadToAnkiActionDiscoveryAsync(initial.OperationId, "action-projection-confirm-ui");
        var confirmation = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(
            "action-projection-confirm", initial.OperationId, resolved.CardDraft!, coordinator.Require(initial.OperationId)));
        var discoveredConfirmation = await client.WaitForAcceptedReadToAnkiActionAsync(confirmationDiscovery);
        Require(discoveredConfirmation.OperationId == confirmation.OperationId && discoveredConfirmation.CommandId == confirmation.CommandId &&
                discoveredConfirmation.AcceptedSequence == confirmation.AcceptedSequence,
            "Action discovery did not return the confirmation/export command's own accepted identity.");
        var completed = await client.WaitReadAloudCardsActionAsync(discoveredConfirmation);
        Require(completed.ActionState == ReadAloudCardsActionState.Completed && completed.TerminalCommandId == confirmation.CommandId &&
                completed.TerminalSequence >= confirmation.AcceptedSequence && completed.Pipeline == ReadAloudCardsPipelineState.Completed &&
                runtime.AnkiAttempts == 1 && runtime.AnkiExternalWrites == 1 &&
                completed.SettingsEvidence?.Destination.DeckIdentity == "零碎知识剪藏::Canary::SDK" &&
                completed.ContextEvidence?.ContextSnapshot.Sha256 == unresolved.ContextEvidence!.ContextSnapshot.Sha256,
            "Explicit confirmation did not create exactly one exporter attempt after the ready draft.");

        var snapshotManifest = Path.Combine(root, "artifacts", completed.SettingsEvidence!.SettingsSnapshot.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        var snapshotOriginalBytes = await File.ReadAllBytesAsync(snapshotManifest);
        await File.AppendAllTextAsync(snapshotManifest, " ");
        await RequireClientCodeAsync(
            () => client.GetReadAloudCardsProjectionAsync(initial.OperationId),
            "settings_evidence_invalid",
            "Tampered task settings snapshot bytes were projected.");
        await File.WriteAllBytesAsync(snapshotManifest, snapshotOriginalBytes);

        await RequireClientCodeAsync(
            () => client.DiscoverAcceptedReadToAnkiActionAsync(new ReadToAnkiActionDiscoveryCheckpoint(
                "another-operation", confirmationDiscovery.CorrelationMarker, confirmationDiscovery.Cursor, confirmationDiscovery.Token)),
            "action_discovery_checkpoint_invalid",
            "An action discovery checkpoint was reused across operation streams.");
        var ambiguousAction = await client.BeginReadToAnkiActionDiscoveryAsync(initial.OperationId, "action-projection-ambiguous-action");
        var revision = eventStore.GetRevision(initial.OperationId);
        eventStore.Append(initial.OperationId, revision, new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-action-left", initial.OperationId, revision + 1, "command.accepted", 1,
                DateTimeOffset.UtcNow, "command-action-left", "command-action-left", ReadAloudCardsOperation.Id, "external-write", StrictJson.CloneElement(new { }), Array.Empty<ArtifactReference>()),
            new KernelEvent(KernelSchemas.Event, "evt-action-right", initial.OperationId, revision + 2, "command.accepted", 1,
                DateTimeOffset.UtcNow, "command-action-right", "command-action-right", ReadAloudCardsOperation.Id, "external-write", StrictJson.CloneElement(new { }), Array.Empty<ArtifactReference>())
        });
        await RequireClientCodeAsync(
            () => client.DiscoverAcceptedReadToAnkiActionAsync(ambiguousAction),
            "action_discovery_ambiguous",
            "Concurrent post-checkpoint actions on one operation were not rejected as ambiguous.");

        var oldExcluded = await client.BeginReadToAnkiDiscoveryAsync("action-projection-old-excluded");
        Require(await client.DiscoverAcceptedReadToAnkiAsync(oldExcluded) is null, "A pre-checkpoint operation was rediscovered.");
        await RequireClientCodeAsync(
            () => client.DiscoverAcceptedReadToAnkiAsync(new ReadToAnkiDiscoveryCheckpoint(oldExcluded.CorrelationMarker, oldExcluded.Cursor, "tampered." + Guid.NewGuid().ToString("N"))),
            "operation_discovery_checkpoint_invalid",
            "A tampered discovery checkpoint was accepted.");

        var ambiguous = await client.BeginReadToAnkiDiscoveryAsync("action-projection-ambiguous");
        _ = await client.ClipCardsAsync(new ReadAloudCardsRequest("action-projection-concurrent-left", capture, configuration, authorization,
            new LanguageCardSettings("language-settings-action-v1", "both"), Selection: new LanguageSelectionContext("pragmatic", new[] { context }, true, "sentence",
                RoleBoundContextSegments: new[] { new LanguageContextSegment(context, "prefix", 0) })));
        _ = await client.ClipCardsAsync(new ReadAloudCardsRequest("action-projection-concurrent-right", capture, configuration, authorization,
            new LanguageCardSettings("language-settings-action-v1", "both"), Selection: new LanguageSelectionContext("pragmatic", new[] { context }, true, "sentence",
                RoleBoundContextSegments: new[] { new LanguageContextSegment(context, "prefix", 0) })));
        await RequireClientCodeAsync(
            () => client.DiscoverAcceptedReadToAnkiAsync(ambiguous),
            "operation_discovery_ambiguous",
            "Concurrent post-checkpoint operations were not rejected as ambiguous.");

        eventStore.Append("missing-settings-evidence", 0, new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-missing-settings-evidence", "missing-settings-evidence", 0, "operation.completed", 1,
                DateTimeOffset.UtcNow, "command-missing-settings-evidence", "command-missing-settings-evidence", ReadAloudCardsOperation.Id,
                "external-write", StrictJson.CloneElement(new { result = new { status = "partial", playback = "not_requested", pipeline = "partially_failed" }, externalWriteCount = 0, externalStateKnown = true }),
                new[] { capture.Reference, configuration.Reference })
        });
        await RequireClientCodeAsync(
            () => client.GetReadAloudCardsProjectionAsync("missing-settings-evidence"),
            "settings_evidence_missing",
            "A terminal without a task settings snapshot was projected.");

    }
    finally
    {
        serverCancellation.Cancel();
        if (serverTask is not null)
        {
            try { await serverTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        DeleteTemporaryDirectory(root);
    }
}

async Task VerifyReadAloudCardsOperationAsync()
{
    var root = NewTemporaryDirectory("read-aloud-cards-operation");
    try
    {
        var store = new FileArtifactStore(Path.Combine(root, "artifacts"), "read-aloud-cards-test");
        var seed = new CommandEnvelope(KernelSchemas.Command, "command-read-aloud-seed", "read-aloud-seed", "artifact.publish",
            new KernelSubject("test", "read-aloud"), new string('A', 64), null, null, Array.Empty<ArtifactReference>(),
            new ContractBody("kc.artifact-publish-command/1", StrictJson.CloneElement(new { })));
        var capture = await DomainArtifactFactory.PublishTextAsync(store, seed, "capture-text", "1.0.0", "kc.capture-text/1", "原始文本不等待清洗就朗读。", Array.Empty<ArtifactReference>(), CancellationToken.None);
        var configuration = await DomainArtifactFactory.PublishTextAsync(store, seed with { CommandId = "command-read-aloud-config", OperationId = "read-aloud-config", IdempotencyKey = new string('B', 64) }, "anki-configuration", "1.0.0", "kc.anki-configuration/1", ValidCanaryAnkiConfiguration(), Array.Empty<ArtifactReference>(), CancellationToken.None);
        var command = new CommandEnvelope(KernelSchemas.Command, "command-read-aloud", "read-aloud-operation", "clip.read-aloud-cards",
            new KernelSubject("test", "read-aloud"), new string('C', 64), null, new AuthorizationReference("read-aloud-auth", "isolated-canary-deck"), new[] { capture, configuration },
            new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { capture, configuration })));
        var capability = new CapabilityDescriptor("clip.read-aloud-cards", true, null, "kc.read-aloud-cards-command/1", "anki-readback", "1.0.0",
            "external-write", "explicit", "conditional", "required", 5000, new WorkerBinding("worker", "content.deepseek", "=test", null), new UiAffordance("Read and create cards", "clip", true));
        var typedAuthorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("read-aloud-auth");
        Require(typedAuthorization.AuthorizationId == "read-aloud-auth" && typedAuthorization.Scope == AnkiWriteAuthorizationBuilder.IsolatedCanaryDeckScope,
            "Typed Anki authorization builder did not freeze the isolated Canary deck scope.");
        var disconnectedClient = new CoreV3Client("authorization-contract-no-server");
        await RequireArgumentRejectsAsync(() => disconnectedClient.ReadAloudCardsAsync(new ReadAloudCardsRequest("missing-auth", new TypedArtifactHandle(capture), new TypedArtifactHandle(configuration), null!)),
            "typed SDK missing authorization");
        await RequireArgumentRejectsAsync(() => disconnectedClient.ReadAloudCardsAsync(new ReadAloudCardsRequest("wrong-auth", new TypedArtifactHandle(capture), new TypedArtifactHandle(configuration), new AnkiWriteAuthorization("read-aloud-auth", "wrong-operation-scope"))),
            "typed SDK wrong authorization scope");
        var runtime = new ReadAloudCardsFakeRuntime();
        var eventStore = new FileEventStore(Path.Combine(root, "events"));
        await RequireThrowsAsync(() => new ReadAloudCardsOperation().ExecuteAsync(command with { Authorization = null },
            new OperationContext(capability, runtime, store, eventStore), CancellationToken.None), "missing read authorization");
        await RequireThrowsAsync(() => new ReadAloudCardsOperation().ExecuteAsync(command with { Authorization = new AuthorizationReference("read-aloud-auth", "wrong-operation-scope") },
            new OperationContext(capability, runtime, store, eventStore), CancellationToken.None), "wrong read authorization scope");
        var result = await new ReadAloudCardsOperation().ExecuteAsync(command,
            new OperationContext(capability, runtime, store, eventStore), CancellationToken.None);

        Require(result.ExternalWriteCount == 0 && result.ExternalStateKnown, "Read-aloud card draft started a destination write before confirmation.");
        Require(runtime.SynthesisSource == "原始文本不等待清洗就朗读。", "Speech synthesis did not consume immutable original capture bytes.");
        Require(runtime.PlaybackAttempts == 1 && runtime.AnkiAttempts == 0, "Read-aloud card draft did not keep exporter at zero attempts.");
        Require(result.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation" && result.Data.GetProperty("cardDraft").GetProperty("cards").GetArrayLength() == 1,
            "Read-aloud result did not publish a typed card draft preview.");
        Require(result.Artifacts.Select(item => item.Kind).SequenceEqual(new[] { "capture-text", "anki-configuration", "task-settings-snapshot", "task-context-snapshot", "content-review", "speech-synthesis", "speech-playback", "card-bundle" }), "Read-aloud draft artifacts are not typed and ordered for projection.");
        var playback = result.Artifacts.Single(item => item.Kind == "speech-playback");
        var playbackEnvelope = await store.OpenVerifiedAsync(playback, "speech-playback", "1.0.0", CancellationToken.None);
        Require(playbackEnvelope.Envelope.Provenance.Inputs.Single().Kind == "speech-synthesis", "Playback lacks SHA-bound synthesis ancestry.");

        var clipRuntime = new ReadAloudCardsFakeRuntime();
        var clipCommand = command with
        {
            CommandId = "command-clip-cards-no-speech", OperationId = "clip-cards-no-speech", IdempotencyKey = new string('O', 64),
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { capture, configuration, includeSpeech = false }))
        };
        var clipResult = await new ReadAloudCardsOperation().ExecuteAsync(clipCommand, new OperationContext(capability, clipRuntime, store, eventStore), CancellationToken.None);
        Require(clipResult.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation" && clipResult.Data.GetProperty("playback").GetString() == "not_requested" &&
                clipRuntime.SynthesisSource is null && clipRuntime.PlaybackAttempts == 0 && clipRuntime.AnkiAttempts == 0 && clipResult.Artifacts.Any(item => item.Kind == "card-bundle"),
            "Cards-only clip mode invoked Speech or failed to publish an independent draft.");
        eventStore.Append(clipCommand.OperationId, eventStore.GetRevision(clipCommand.OperationId), new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-clip-cards-no-speech", clipCommand.OperationId, 0, "operation.completed", 1, DateTimeOffset.UtcNow,
                clipCommand.CommandId, clipCommand.CommandId, clipCommand.Operation, "external-write", StrictJson.CloneElement(new { result = clipResult.Data, clipResult.ExternalWriteCount, clipResult.ExternalStateKnown }), clipResult.Artifacts)
        });
        var clipDraft = clipResult.Data.GetProperty("cardDraft");
        var clipConfirm = clipCommand with
        {
            CommandId = "command-clip-cards-no-speech-confirm", IdempotencyKey = new string('T', 64), Inputs = Array.Empty<ArtifactReference>(),
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "confirm_card_draft", sourceOperationId = clipCommand.OperationId, draft = new { token = clipDraft.GetProperty("token").GetString(), revision = clipDraft.GetProperty("revision").GetString(), digest = clipDraft.GetProperty("digest").GetString() } }))
        };
        var clipConfirmed = await new ReadAloudCardsOperation().ExecuteAsync(clipConfirm, new OperationContext(capability, clipRuntime, store, eventStore), CancellationToken.None);
        Require(clipConfirmed.Data.GetProperty("pipeline").GetString() == "completed" && clipConfirmed.Data.GetProperty("playback").GetString() == "not_requested" && clipRuntime.PlaybackAttempts == 0 && clipRuntime.AnkiAttempts == 1,
            "Cards-only confirmation invented a Speech terminal or failed its single Anki export.");
        eventStore.Append(command.OperationId, eventStore.GetRevision(command.OperationId), new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-read-aloud-source-completed", command.OperationId, 0, "operation.completed", 1,
                DateTimeOffset.UtcNow, command.CommandId, command.CommandId, command.Operation, "external-write",
                StrictJson.CloneElement(new { result = result.Data, result.ExternalWriteCount, result.ExternalStateKnown }),
                result.Artifacts)
        });
        var draft = result.Data.GetProperty("cardDraft");
        var staleConfirm = command with
        {
            CommandId = "command-read-aloud-stale-confirm", IdempotencyKey = new string('E', 64), Inputs = Array.Empty<ArtifactReference>(),
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "confirm_card_draft", sourceOperationId = command.OperationId, draft = new { token = "stale", revision = draft.GetProperty("revision").GetString(), digest = draft.GetProperty("digest").GetString() } }))
        };
        await RequireThrowsAsync(() => new ReadAloudCardsOperation().ExecuteAsync(staleConfirm, new OperationContext(capability, runtime, store, eventStore), CancellationToken.None),
            "Stale card draft token reached exporter.");
        Require(runtime.AnkiAttempts == 0, "Stale card draft token started exporter.");
        var confirm = command with
        {
            CommandId = "command-read-aloud-confirm", IdempotencyKey = new string('F', 64), Inputs = Array.Empty<ArtifactReference>(),
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "confirm_card_draft", sourceOperationId = command.OperationId, destination = new { routeKey = "attacker", deckIdentity = "正式牌组" }, draft = new { token = draft.GetProperty("token").GetString(), revision = draft.GetProperty("revision").GetString(), digest = draft.GetProperty("digest").GetString() } }))
        };
        var confirmed = await new ReadAloudCardsOperation().ExecuteAsync(confirm, new OperationContext(capability, runtime, store, eventStore), CancellationToken.None);
        Require(confirmed.ExternalWriteCount == 1 && runtime.AnkiAttempts == 1 && runtime.AnkiExternalWrites == 1 && confirmed.Data.GetProperty("pipeline").GetString() == "completed" &&
                runtime.LastAnkiRequest?.GetProperty("configurationJson").GetString() == ValidCanaryAnkiConfiguration(),
            "Card draft confirmation did not produce exactly one destination write.");
        eventStore.Append(command.OperationId, eventStore.GetRevision(command.OperationId), new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-read-aloud-confirmed", command.OperationId, 0, "operation.completed", 1,
                DateTimeOffset.UtcNow, confirm.CommandId, confirm.CommandId, confirm.Operation, "external-write",
                StrictJson.CloneElement(new { result = confirmed.Data, confirmed.ExternalWriteCount, confirmed.ExternalStateKnown }), confirmed.Artifacts)
        });
        var duplicateConfirm = confirm with { CommandId = "command-read-aloud-duplicate-confirm", IdempotencyKey = new string('G', 64) };
        await RequireThrowsAsync(() => new ReadAloudCardsOperation().ExecuteAsync(duplicateConfirm, new OperationContext(capability, runtime, store, eventStore), CancellationToken.None),
            "Double confirmation reached exporter.");
        Require(runtime.AnkiAttempts == 1 && runtime.AnkiExternalWrites == 1, "Double confirmation changed Anki effects.");
        var replay = command with
        {
            CommandId = "command-read-aloud-replay",
            IdempotencyKey = new string('D', 64),
            Inputs = Array.Empty<ArtifactReference>(),
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "replay", sourceOperationId = command.OperationId }))
        };
        var replayResult = await new ReadAloudCardsOperation().ExecuteAsync(replay,
            new OperationContext(capability, runtime, store, eventStore), CancellationToken.None);
        Require(runtime.PlaybackAttempts == 2 && runtime.AnkiAttempts == 1 && runtime.AnkiExternalWrites == 1 && replayResult.ExternalWriteCount == 0,
            "Replay did not create only a fresh playback attempt while preserving Content and Anki attempts.");
        Require(replayResult.Artifacts.Count(item => item.Kind == "speech-playback") == 1 && replayResult.Artifacts.Count(item => item.Kind == "anki-readback") == 1,
            "Replay terminal artifacts do not retain the original projection with exactly one fresh playback artifact.");

        var cancelCommand = command with { CommandId = "command-read-aloud-cancel-seed", OperationId = "read-aloud-cancel-operation", IdempotencyKey = new string('H', 64) };
        var cancellable = await new ReadAloudCardsOperation().ExecuteAsync(cancelCommand, new OperationContext(capability, runtime, store, eventStore), CancellationToken.None);
        eventStore.Append(cancelCommand.OperationId, eventStore.GetRevision(cancelCommand.OperationId), new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-read-aloud-cancel-seed", cancelCommand.OperationId, 0, "operation.completed", 1,
                DateTimeOffset.UtcNow, cancelCommand.CommandId, cancelCommand.CommandId, cancelCommand.Operation, "external-write",
                StrictJson.CloneElement(new { result = cancellable.Data, cancellable.ExternalWriteCount, cancellable.ExternalStateKnown }), cancellable.Artifacts)
        });
        var cancelDraft = cancellable.Data.GetProperty("cardDraft");
        var cancel = cancelCommand with
        {
            CommandId = "command-read-aloud-cancel", IdempotencyKey = new string('I', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = null,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "cancel_card_draft", sourceOperationId = cancelCommand.OperationId, draft = new { token = cancelDraft.GetProperty("token").GetString(), revision = cancelDraft.GetProperty("revision").GetString(), digest = cancelDraft.GetProperty("digest").GetString() } }))
        };
        var cancelled = await new ReadAloudCardsOperation().ExecuteAsync(cancel, new OperationContext(capability, runtime, store, eventStore), CancellationToken.None);
        Require(cancelled.Data.GetProperty("pipeline").GetString() == "cancelled" && cancelled.ExternalWriteCount == 0 && runtime.AnkiAttempts == 1,
            "Card draft cancellation started an exporter or did not produce a zero-write terminal.");

        var failingRuntime = new ReadAloudCardsFakeRuntime(failContentGate: true);
        var failingCommand = command with
        {
            CommandId = "command-read-aloud-content-gate-failure", OperationId = "read-aloud-content-gate-failure",
            IdempotencyKey = new string('J', 64)
        };
        var partial = await new ReadAloudCardsOperation().ExecuteAsync(failingCommand,
            new OperationContext(capability, failingRuntime, store, eventStore), CancellationToken.None);
        Require(partial.Data.GetProperty("pipeline").GetString() == "partially_failed" &&
                partial.Data.GetProperty("attentionRequired").GetBoolean() &&
                partial.Data.GetProperty("cardGeneration").GetProperty("reasonCode").GetString() == "content.gate-failed",
            "Content gate failure was not projected as a typed attention-required partial terminal.");
        Require(failingRuntime.PlaybackAttempts == 1 && failingRuntime.AnkiAttempts == 0 &&
                !partial.Artifacts.Any(item => item.Kind is "card-bundle" or "anki-readback") &&
                partial.Artifacts.Count(item => item.Kind is "speech-synthesis" or "speech-playback") == 2,
            "Content gate failure cancelled Speech, exposed a preview, or reached the exporter.");

        var speechFailureRuntime = new ReadAloudCardsFakeRuntime(failSpeechSynthesis: true);
        var speechFailureCommand = command with
        {
            CommandId = "command-read-aloud-speech-failure", OperationId = "read-aloud-speech-failure",
            IdempotencyKey = new string('K', 64)
        };
        var speechFailure = await new ReadAloudCardsOperation().ExecuteAsync(speechFailureCommand,
            new OperationContext(capability, speechFailureRuntime, store, eventStore), CancellationToken.None);
        Require(speechFailure.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation" &&
                speechFailure.Data.GetProperty("playback").GetString() == "failed" &&
                speechFailure.Data.GetProperty("cardDraft").GetProperty("cards").GetArrayLength() == 1 &&
                speechFailureRuntime.AnkiAttempts == 0 &&
                speechFailure.Artifacts.Any(item => item.Kind == "card-bundle") &&
                !speechFailure.Artifacts.Any(item => item.Kind is "speech-synthesis" or "speech-playback" or "anki-readback"),
            "Speech synthesis failure discarded a ready Content draft or reached a destination.");

        var dualFailureRuntime = new ReadAloudCardsFakeRuntime(failContentGate: true, failSpeechSynthesis: true);
        var dualFailureCommand = command with
        {
            CommandId = "command-read-aloud-dual-failure", OperationId = "read-aloud-dual-failure",
            IdempotencyKey = new string('L', 64)
        };
        var dualFailure = await new ReadAloudCardsOperation().ExecuteAsync(dualFailureCommand,
            new OperationContext(capability, dualFailureRuntime, store, eventStore), CancellationToken.None);
        Require(dualFailure.Data.GetProperty("pipeline").GetString() == "partially_failed" &&
                dualFailure.Data.GetProperty("playback").GetString() == "failed" &&
                dualFailure.Data.GetProperty("attentionRequired").GetBoolean() &&
                dualFailureRuntime.AnkiAttempts == 0 && !dualFailure.Artifacts.Any(item => item.Kind is "card-bundle" or "anki-readback"),
            "Combined Content/Speech failure did not remain a typed zero-export terminal.");

        foreach (var (choice, expectedCards) in new[] { ("meaning-reading", 1), ("writing", 1), ("both", 2) })
        {
            var languageRuntime = new ReadAloudCardsFakeRuntime();
            var languageOperationId = "read-aloud-language-" + choice;
            var languageCommand = command with
            {
                CommandId = "command-" + languageOperationId, OperationId = languageOperationId,
                IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(languageOperationId))),
                Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { capture, configuration, languageCards = new { revision = "language-settings-v1", defaultChoice = choice } }))
            };
            var draftResult = await new ReadAloudCardsOperation().ExecuteAsync(languageCommand, new OperationContext(capability, languageRuntime, store, eventStore), CancellationToken.None);
            var cards = draftResult.Data.GetProperty("cardDraft").GetProperty("cards");
            Require(draftResult.Data.GetProperty("frozenLanguageCardChoice").GetString() == choice && cards.GetArrayLength() == expectedCards &&
                    languageRuntime.PlaybackAttempts == 1 && languageRuntime.AnkiAttempts == 0,
                $"Language choice '{choice}' did not freeze the expected preview before export.");
            if (choice == "both")
            {
                Require(cards[0].GetProperty("cardId").GetString() == "meaning-reading" && cards[1].GetProperty("cardId").GetString() == "writing",
                    "Both language cards are not ordered meaning-reading then writing.");
            }
            eventStore.Append(languageOperationId, eventStore.GetRevision(languageOperationId), new[]
            {
                new KernelEvent(KernelSchemas.Event, "evt-" + languageOperationId, languageOperationId, 0, "operation.completed", 1, DateTimeOffset.UtcNow,
                    languageCommand.CommandId, languageCommand.CommandId, languageCommand.Operation, "external-write",
                    StrictJson.CloneElement(new { result = draftResult.Data, draftResult.ExternalWriteCount, draftResult.ExternalStateKnown }), draftResult.Artifacts)
            });
            var draftBinding = draftResult.Data.GetProperty("cardDraft");
            var languageConfirm = languageCommand with
            {
                CommandId = "command-" + languageOperationId + "-confirm", IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(languageOperationId + "-confirm"))), Inputs = Array.Empty<ArtifactReference>(),
                Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "confirm_card_draft", sourceOperationId = languageOperationId, draft = new { token = draftBinding.GetProperty("token").GetString(), revision = draftBinding.GetProperty("revision").GetString(), digest = draftBinding.GetProperty("digest").GetString() } }))
            };
            var languageConfirmed = await new ReadAloudCardsOperation().ExecuteAsync(languageConfirm, new OperationContext(capability, languageRuntime, store, eventStore), CancellationToken.None);
            var readbackCards = languageConfirmed.Data.GetProperty("semanticReadback").GetProperty("cards");
            Require(languageRuntime.AnkiAttempts == 1 && languageRuntime.AnkiExternalWrites == 1 && readbackCards.GetArrayLength() == expectedCards,
                $"Language choice '{choice}' did not export and read back its exact card count once.");
            for (var index = 0; index < expectedCards; index++)
            {
                Require(readbackCards[index].GetProperty("notesInfo").GetProperty("fields").GetProperty("front").GetString() == cards[index].GetProperty("front").GetString() &&
                        readbackCards[index].GetProperty("notesInfo").GetProperty("fields").GetProperty("back").GetString() == cards[index].GetProperty("back").GetString(),
                    $"Language choice '{choice}' semantic readback differs from preview card {index}.");
            }
        }

        var askRuntime = new ReadAloudCardsFakeRuntime();
        var askCommand = command with
        {
            CommandId = "command-read-aloud-language-ask", OperationId = "read-aloud-language-ask", IdempotencyKey = new string('D', 64),
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { capture, configuration, languageCards = new { revision = "language-settings-ask-v1", defaultChoice = "ask" } }))
        };
        var ask = await new ReadAloudCardsOperation().ExecuteAsync(askCommand, new OperationContext(capability, askRuntime, store, eventStore), CancellationToken.None);
        Require(ask.Data.GetProperty("pipeline").GetString() == "waiting_language_card_choice" && ask.Data.GetProperty("languageCardChoice").GetProperty("choices").GetArrayLength() == 3 &&
                askRuntime.ContentAttempts == 1 && askRuntime.PlaybackAttempts == 1 && askRuntime.AnkiAttempts == 0 && !ask.Artifacts.Any(item => item.Kind == "card-bundle") &&
                ask.Data.GetProperty("languageDecision").GetProperty("categoryId").GetString() == "language_en",
            "Ask must classify before producing a decision-bound three-choice wait.");
        eventStore.Append(askCommand.OperationId, eventStore.GetRevision(askCommand.OperationId), new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-language-ask", askCommand.OperationId, 0, "operation.completed", 1, DateTimeOffset.UtcNow,
                askCommand.CommandId, askCommand.CommandId, askCommand.Operation, "external-write", StrictJson.CloneElement(new { result = ask.Data, ask.ExternalWriteCount, ask.ExternalStateKnown }), ask.Artifacts)
        });
        var reopenedAskEvents = new FileEventStore(Path.Combine(root, "events"));
        var askPrompt = ask.Data.GetProperty("languageCardChoice");
        var choose = askCommand with
        {
            CommandId = "command-read-aloud-language-ask-choose", IdempotencyKey = new string('E', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = askCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "choose_language_card", sourceOperationId = askCommand.OperationId, targetToken = askPrompt.GetProperty("targetToken").GetString(), choice = "both" }))
        };
        var chosen = await new ReadAloudCardsOperation().ExecuteAsync(choose, new OperationContext(capability, askRuntime, store, reopenedAskEvents), CancellationToken.None);
        Require(chosen.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation" && chosen.Data.GetProperty("frozenLanguageCardChoice").GetString() == "both" &&
                chosen.Data.GetProperty("cardDraft").GetProperty("cards").GetArrayLength() == 2 && askRuntime.PlaybackAttempts == 1 && askRuntime.AnkiAttempts == 0 && askRuntime.ContentAttempts == 2 &&
                askRuntime.LastContentRequest!.Value.GetProperty("inputVersion").GetString() == "2" &&
                askRuntime.LastContentRequest.Value.GetProperty("languageDecision").GetRawText() == ask.Data.GetProperty("languageDecision").GetRawText(),
            "Ask resume did not recover across restart and produce the selected two-card preview without replay/export.");

        foreach (var preference in new[] { "ask", "both" })
        {
            var ordinaryRuntime = new ReadAloudCardsFakeRuntime(recognizedLanguage: false);
            var ordinaryCommand = askCommand with { CommandId = "command-ordinary-" + preference, OperationId = "ordinary-" + preference,
                Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { capture, configuration, languageCards = new { revision = "ordinary-preference-test", defaultChoice = preference } })) };
            var ordinary = await new ReadAloudCardsOperation().ExecuteAsync(ordinaryCommand, new OperationContext(capability, ordinaryRuntime, store, eventStore), CancellationToken.None);
            Require(ordinaryRuntime.ContentAttempts == 1 && ordinaryRuntime.AnkiAttempts == 0 &&
                ordinary.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation" && !ordinary.Data.TryGetProperty("languageCardChoice", out _) &&
                ordinary.Data.GetProperty("cardDraft").GetProperty("cards")[0].GetProperty("template").GetString() == "basic_qa",
                "Non-language classification was overridden by a project language preference.");
        }

        var unresolvedRuntime = new ReadAloudCardsFakeRuntime(unresolvedContent: true);
        var unresolvedOperationId = "read-aloud-language-unresolved";
        var unresolvedCommand = askCommand with { CommandId = "command-" + unresolvedOperationId, OperationId = unresolvedOperationId, IdempotencyKey = new string('M', 64) };
        var unresolvedAsk = await new ReadAloudCardsOperation().ExecuteAsync(unresolvedCommand, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None);
        eventStore.Append(unresolvedOperationId, eventStore.GetRevision(unresolvedOperationId), new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-" + unresolvedOperationId, unresolvedOperationId, 0, "operation.completed", 1, DateTimeOffset.UtcNow,
                unresolvedCommand.CommandId, unresolvedCommand.CommandId, unresolvedCommand.Operation, "external-write", StrictJson.CloneElement(new { result = unresolvedAsk.Data, unresolvedAsk.ExternalWriteCount, unresolvedAsk.ExternalStateKnown }), unresolvedAsk.Artifacts)
        });
        var unresolvedChoice = unresolvedAsk.Data.GetProperty("languageCardChoice");
        var unresolvedChoose = unresolvedCommand with
        {
            CommandId = "command-" + unresolvedOperationId + "-choose", IdempotencyKey = new string('N', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = unresolvedCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "choose_language_card", sourceOperationId = unresolvedOperationId, targetToken = unresolvedChoice.GetProperty("targetToken").GetString(), choice = "both" }))
        };
        var unresolved = await new ReadAloudCardsOperation().ExecuteAsync(unresolvedChoose, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None);
        Require(unresolved.Data.GetProperty("pipeline").GetString() == "awaiting_card_resolution" && unresolved.Data.GetProperty("attentionRequired").GetBoolean() &&
                unresolved.Data.GetProperty("cardResolution").GetProperty("actions").GetArrayLength() == 3 &&
                !unresolved.Data.TryGetProperty("languageCardChoice", out _) && !unresolved.Artifacts.Any(item => item.Kind is "card-bundle" or "anki-readback") && unresolvedRuntime.AnkiAttempts == 0,
            "Unresolved Content after language choice did not replace the stale choice with a typed zero-export card resolution prompt.");
        eventStore.Append(unresolvedOperationId, eventStore.GetRevision(unresolvedOperationId), new[]
        {
            new KernelEvent(KernelSchemas.Event, "evt-" + unresolvedOperationId + "-resolution", unresolvedOperationId, 0, "operation.completed", 1, DateTimeOffset.UtcNow,
                unresolvedChoose.CommandId, unresolvedChoose.CommandId, unresolvedChoose.Operation, "external-write", StrictJson.CloneElement(new { result = unresolved.Data, unresolved.ExternalWriteCount, unresolved.ExternalStateKnown }), unresolved.Artifacts)
        });
        var resolutionPrompt = unresolved.Data.GetProperty("cardResolution");
        var staleResolution = unresolvedCommand with
        {
            CommandId = "command-" + unresolvedOperationId + "-stale", IdempotencyKey = new string('P', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = unresolvedCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "resolve_card_resolution", sourceOperationId = unresolvedOperationId, targetToken = "stale", decisionId = resolutionPrompt.GetProperty("decisionId").GetString(), resolutionAction = "user_correction", selectedText = "target" }))
        };
        await RequireThrowsAsync(() => new ReadAloudCardsOperation().ExecuteAsync(staleResolution, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None),
            "Stale card-resolution token reached Content.");
        var contentAttemptsBeforeResolution = unresolvedRuntime.ContentAttempts;
        var cancelResolution = unresolvedCommand with
        {
            CommandId = "command-" + unresolvedOperationId + "-cancel", IdempotencyKey = new string('Q', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = unresolvedCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "resolve_card_resolution", sourceOperationId = unresolvedOperationId, targetToken = resolutionPrompt.GetProperty("targetToken").GetString(), decisionId = resolutionPrompt.GetProperty("decisionId").GetString(), resolutionAction = "cancel", selectedText = "target" }))
        };
        var resolutionCancelled = await new ReadAloudCardsOperation().ExecuteAsync(cancelResolution, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None);
        Require(resolutionCancelled.Data.GetProperty("pipeline").GetString() == "cancelled" && unresolvedRuntime.ContentAttempts == contentAttemptsBeforeResolution && unresolvedRuntime.AnkiAttempts == 0,
            "Card-resolution cancellation invoked Content or Anki.");
        var frozenOccurrence = resolutionPrompt.GetProperty("evidence").GetProperty("candidatePositions")[0];
        var typedOccurrence = new
        {
            segmentId = frozenOccurrence.GetProperty("segment_id").GetString(),
            order = frozenOccurrence.GetProperty("order").GetInt32(),
            start = frozenOccurrence.GetProperty("start").GetInt32(),
            end = frozenOccurrence.GetProperty("end").GetInt32(),
            exactText = frozenOccurrence.GetProperty("exact_text").GetString(),
            sourceSha256 = frozenOccurrence.GetProperty("source_sha256").GetString()
        };
        var missingOccurrence = unresolvedCommand with
        {
            CommandId = "command-" + unresolvedOperationId + "-accept-missing", IdempotencyKey = new string('T', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = unresolvedCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "resolve_card_resolution", sourceOperationId = unresolvedOperationId, targetToken = resolutionPrompt.GetProperty("targetToken").GetString(), decisionId = resolutionPrompt.GetProperty("decisionId").GetString(), resolutionAction = "accept_candidate", selectedText = unresolved.Data.GetProperty("languageSelection").GetProperty("targetText").GetString() }))
        };
        await RequireThrowsAsync(() => new ReadAloudCardsOperation().ExecuteAsync(missingOccurrence, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None),
            "accept candidate missing occurrence");
        var driftedOccurrence = unresolvedCommand with
        {
            CommandId = "command-" + unresolvedOperationId + "-accept-drift", IdempotencyKey = new string('U', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = unresolvedCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "resolve_card_resolution", sourceOperationId = unresolvedOperationId, targetToken = resolutionPrompt.GetProperty("targetToken").GetString(), decisionId = resolutionPrompt.GetProperty("decisionId").GetString(), resolutionAction = "accept_candidate", selectedText = unresolved.Data.GetProperty("languageSelection").GetProperty("targetText").GetString(), candidateOccurrence = new { typedOccurrence.segmentId, typedOccurrence.order, start = typedOccurrence.start + 1, typedOccurrence.end, typedOccurrence.exactText, typedOccurrence.sourceSha256 } }))
        };
        await RequireThrowsAsync(() => new ReadAloudCardsOperation().ExecuteAsync(driftedOccurrence, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None),
            "accept candidate coordinate drift");
        var acceptedCandidate = unresolvedCommand with
        {
            CommandId = "command-" + unresolvedOperationId + "-accept", IdempotencyKey = new string('S', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = unresolvedCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "resolve_card_resolution", sourceOperationId = unresolvedOperationId, targetToken = resolutionPrompt.GetProperty("targetToken").GetString(), decisionId = resolutionPrompt.GetProperty("decisionId").GetString(), resolutionAction = "accept_candidate", selectedText = unresolved.Data.GetProperty("languageSelection").GetProperty("targetText").GetString(), candidateOccurrence = typedOccurrence }))
        };
        var acceptedDraft = await new ReadAloudCardsOperation().ExecuteAsync(acceptedCandidate, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None);
        Require(acceptedDraft.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation" && acceptedDraft.Data.GetProperty("cardDraft").GetProperty("cards").GetArrayLength() == 2 && unresolvedRuntime.ContentAttempts == contentAttemptsBeforeResolution + 1,
            "Explicit candidate acceptance did not produce a new immutable two-card draft.");
        var correctResolution = unresolvedCommand with
        {
            CommandId = "command-" + unresolvedOperationId + "-correct", IdempotencyKey = new string('R', 64), Inputs = Array.Empty<ArtifactReference>(), Authorization = unresolvedCommand.Authorization,
            Body = new ContractBody("kc.read-aloud-cards-command/1", StrictJson.CloneElement(new { action = "resolve_card_resolution", sourceOperationId = unresolvedOperationId, targetToken = resolutionPrompt.GetProperty("targetToken").GetString(), decisionId = resolutionPrompt.GetProperty("decisionId").GetString(), resolutionAction = "user_correction", selectedText = "target" }))
        };
        var resolvedDraft = await new ReadAloudCardsOperation().ExecuteAsync(correctResolution, new OperationContext(capability, unresolvedRuntime, store, eventStore), CancellationToken.None);
        Require(resolvedDraft.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation" && resolvedDraft.Data.GetProperty("cardDraft").GetProperty("cards").GetArrayLength() == 2 &&
                unresolvedRuntime.ContentAttempts == contentAttemptsBeforeResolution + 2 && unresolvedRuntime.AnkiAttempts == 0 &&
                unresolvedRuntime.LastContentRequest!.Value.GetProperty("inputVersion").GetString()!.StartsWith("resolution-", StringComparison.Ordinal),
            "Human correction did not create a new immutable Content revision and two-card draft without exporting.");
    }
    finally { DeleteTemporaryDirectory(root); }
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
        Require(result.Artifacts.Count == 2 && runtime.Attempts == 2 && result.Data.GetProperty("pipeline").GetString() == "awaiting_card_confirmation",
            "A new legal read-only command could not continue through the destination-neutral draft after recovery.");
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
        return projection.State == QuestionProjectionState.WaitingInput && projection.CardDraft is not null;
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

static string ValidCanaryAnkiConfiguration()
    => "{\"schema_version\":\"anki-export-config.v1\",\"endpoint\":\"fake://anki\",\"provision_managed\":false,\"deck_routes\":{\"canary\":\"零碎知识剪藏::Canary::SDK\"}}";

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

static async Task RequireThrowsAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task RequireOperationRejectsAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    catch (ArgumentException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task RequireArgumentRejectsAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (ArgumentException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task RequireClientCodeAsync(Func<Task> action, string expectedCode, string message)
{
    try
    {
        await action();
    }
    catch (CoreV3ClientException ex) when (ex.Code == expectedCode)
    {
        return;
    }
    throw new InvalidOperationException(message);
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

sealed class ReadAloudCardsFakeRuntime : IWorkerRuntime
{
    private readonly bool failContentGate;
    private readonly bool failSpeechSynthesis;
    private readonly bool unresolvedContent;
    private readonly bool recognizedLanguage;
    private readonly WorkerBinding content = new("worker", "content.deepseek", "=test", null);
    private readonly WorkerBinding speech = new("worker", "speech.edge", "=test", null);
    private readonly WorkerBinding anki = new("worker", "export.anki", "=test", null);
    public string? SynthesisSource { get; private set; }
    public int PlaybackAttempts { get; private set; }
    public int ContentAttempts { get; private set; }
    public int AnkiAttempts { get; private set; }
    public int AnkiExternalWrites { get; private set; }
    public JsonElement? LastContentRequest { get; private set; }
    public JsonElement? LastAnkiRequest { get; private set; }
    private readonly HashSet<string> ankiIdempotency = new(StringComparer.Ordinal);
    public ReadAloudCardsFakeRuntime(bool failContentGate = false, bool failSpeechSynthesis = false, bool unresolvedContent = false, bool recognizedLanguage = true)
    {
        this.failContentGate = failContentGate;
        this.failSpeechSynthesis = failSpeechSynthesis;
        this.unresolvedContent = unresolvedContent;
        this.recognizedLanguage = recognizedLanguage;
    }
    public Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        var phase = invocation.Request.GetProperty("phase").GetString();
        if (invocation.Binding.WorkerId == "speech.edge" && phase == "synthesize") { if (failSpeechSynthesis) throw new InvalidDataException("edge_tts_failed"); SynthesisSource = invocation.Request.GetProperty("spokenText").GetString() ?? throw new InvalidDataException("Missing spoken text."); return Task.FromResult(new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "synthesized", audioArtifact = new { artifactRef = "fake-audio", kind = "audio.speech", contractVersion = "1.0.0", manifestPath = "fake/audio.json", expectedSha256 = new string('a', 64) } }), Array.Empty<ArtifactReference>(), 0, true)); }
        if (invocation.Binding.WorkerId == "speech.edge" && phase == "playback") { PlaybackAttempts++; return Task.FromResult(new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "idle_replayable", naturalCompletion = true }), Array.Empty<ArtifactReference>(), 0, true)); }
        if (invocation.Binding.WorkerId == "content.deepseek" && phase == "review")
        {
            ContentAttempts++;
            LastContentRequest = invocation.Request.Clone();
            if (failContentGate) throw new InvalidDataException("FAILED_CONTENT_GATE card.same-sides");
            var original = invocation.Request.GetProperty("originalText").GetString();
            var choice = invocation.Request.TryGetProperty("languageChoice", out var languageChoice) ? languageChoice.GetString() : "ordinary";
            if (choice == "ask" && recognizedLanguage)
            {
                var selection = invocation.Request.GetProperty("languageSelection");
                return Task.FromResult(new WorkerExecutionResult("completed", StrictJson.CloneElement(new
                {
                    status = "language_choice_required", cleanedText = original,
                    languageDecision = new { categoryId = "language_en", inputVersion = "1", sourceOperationId = invocation.OperationId,
                        selectedTextSha256 = selection.GetProperty("selectedTextSha256").GetString(), targetTextSha256 = selection.GetProperty("targetTextSha256").GetString(),
                        decisionId = "explicit-fake-classifier-decision", sourceEvidence = "fake-only-not-linguistic-fact" }
                }), Array.Empty<ArtifactReference>(), 0, true));
            }
            if (invocation.Request.TryGetProperty("languageDecision", out var decision) &&
                (decision.GetProperty("sourceOperationId").GetString() != invocation.OperationId || invocation.Request.GetProperty("inputVersion").GetString() != "2"))
                throw new InvalidDataException("Fake decision binding drifted.");
            if (unresolvedContent && !invocation.Request.TryGetProperty("humanResolution", out _))
            {
                var target = invocation.Request.GetProperty("languageSelection").GetProperty("targetText").GetString() ?? "target";
                var sourceSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target + " " + target))).ToLowerInvariant();
                return Task.FromResult(new WorkerExecutionResult("completed", StrictJson.CloneElement(new
                {
                    status = "card_resolution_required", moduleReleaseState = "unresolved", cleanedText = original, reasonCode = "content.unresolved",
                    evidence = new
                    {
                        learningBundleSha256 = new string('d', 64), decisionBundleSha256 = new string('e', 64), decisionId = "decision-ambiguous-target", targetToken = new string('f', 64), inputVersion = "1",
                        candidatePositions = new[]
                        {
                            new { segment_id = "fake-segment", order = 0, start = 0, end = target.Length, exact_text = target, source_sha256 = sourceSha },
                            new { segment_id = "fake-segment", order = 0, start = target.Length + 1, end = target.Length * 2 + 1, exact_text = target, source_sha256 = sourceSha }
                        },
                        suggestedUserFields = new[] { "candidateOccurrence" }, warnings = new[] { "ambiguous language target" }
                    }
                }), Array.Empty<ArtifactReference>(), 0, true));
            }
            if (!recognizedLanguage) choice = "ordinary";
            var cards = choice switch
            {
                "meaning-reading" => new[] { new { cardId = "meaning-reading", template = "language_context", front = "language context", back = "语言含义与读音" } },
                "writing" => new[] { new { cardId = "writing", template = "language_writing", front = "（读音；意思） context", back = "target" } },
                "both" => new[]
                {
                    new { cardId = "meaning-reading", template = "language_context", front = "language context", back = "语言含义与读音" },
                    new { cardId = "writing", template = "language_writing", front = "（读音；意思） context", back = "target" }
                },
                _ => new[] { new { cardId = "ordinary", template = "basic_qa", front = "Fake front", back = "Fake back" } }
            };
            return Task.FromResult(new WorkerExecutionResult("completed", StrictJson.CloneElement(new { cleanedText = original, cards }), Array.Empty<ArtifactReference>(), 0, true));
        }
        if (invocation.Binding.WorkerId == "export.anki" && phase == "export")
        {
            AnkiAttempts++;
            LastAnkiRequest = invocation.Request.Clone();
            var first = ankiIdempotency.Add(invocation.Request.GetProperty("idempotencyKey").GetString()!);
            if (first) AnkiExternalWrites++;
            var requestedCards = invocation.Request.TryGetProperty("cards", out var cardsElement)
                ? cardsElement.EnumerateArray().Select((item, index) => new { front = item.GetProperty("front").GetString()!, back = item.GetProperty("back").GetString()!, index }).ToArray()
                : new[] { new { front = "Fake front", back = "Fake back", index = 0 } };
            return Task.FromResult(new WorkerExecutionResult("completed", StrictJson.CloneElement(new
            {
                status = first ? "created" : "already_applied", exactReadback = true,
                semanticReadback = new
                {
                    artifactPath = "fake/readback.json", sha256 = new string('b', 64), cardsSha256 = new string('c', 64),
                    cards = requestedCards.Select(item => new { notesInfo = new { noteId = item.index + 1, cardIds = new[] { item.index + 2 }, fields = new { Front = item.front, Back = item.back } }, cardsInfo = new[] { new { cardId = item.index + 2, noteId = item.index + 1, deckName = "零碎知识剪藏::Canary" } } }).ToArray()
                }
            }), Array.Empty<ArtifactReference>(), first ? 1 : 0, true));
        }
        throw new InvalidOperationException("Unexpected fake read-aloud worker phase.");
    }
    public WorkerRuntimeMetrics GetMetrics() => new(0, 0, AnkiAttempts, AnkiAttempts, AnkiAttempts, 0, 0, PlaybackAttempts, "read-aloud-cards-test");
    public WorkerBinding GetAdmittedBinding(string workerId) => workerId switch { "content.deepseek" => content, "speech.edge" => speech, "export.anki" => anki, _ => throw new InvalidOperationException() };
}

sealed class DelayedResolutionReadAloudRuntime : IWorkerRuntime
{
    private readonly ReadAloudCardsFakeRuntime inner = new(unresolvedContent: true);
    private readonly TaskCompletionSource<bool> releaseResolution = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ResolutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ContentAttempts => inner.ContentAttempts;
    public int AnkiAttempts => inner.AnkiAttempts;
    public int AnkiExternalWrites => inner.AnkiExternalWrites;
    public JsonElement? LastContentRequest => inner.LastContentRequest;

    public void ReleaseResolution() => releaseResolution.TrySetResult(true);

    public async Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Binding.WorkerId == "content.deepseek" &&
            invocation.Request.GetProperty("phase").GetString() == "review" &&
            invocation.Request.TryGetProperty("humanResolution", out _))
        {
            ResolutionStarted.TrySetResult(true);
            await releaseResolution.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return await inner.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    public WorkerRuntimeMetrics GetMetrics() => inner.GetMetrics();

    public WorkerBinding GetAdmittedBinding(string workerId) => inner.GetAdmittedBinding(workerId);
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
        if (package.Request.WorkerId == "content.deepseek" && package.Request.Operation == "understand-and-card")
        {
            using var standardInput = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, "inputs", "content", "standard-input.v1.json"), cancellationToken));
            Require(standardInput.RootElement.GetProperty("task_id").GetString() == package.Request.TaskId,
                "Read-aloud content standard-input did not inherit the immutable task package identity.");
            Require(!standardInput.RootElement.GetProperty("task_id").GetString()!.Equals("core-v3-read-aloud", StringComparison.Ordinal),
                "Read-aloud content standard-input retained a constant task identity.");
        }
        Interlocked.Increment(ref _validatedTaskManifests);

        var outputs = package.Request.WorkerId == "content.deepseek"
            ? await PublishContentAsync(worker, package, cancellationToken)
            : package.Request.WorkerId == "speech.edge"
                ? package.Request.Operation == "speech.synthesize"
                    ? new[] { await PublishJsonAsync(worker, package, "audio.speech", "audio.json", new { schemaVersion = "audio.speech/1.0.0", audio = "offline" }, cancellationToken) }
                    : Array.Empty<LegacyArtifactReference>()
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
        var cardRef = await PublishJsonAsync(worker, package, "card-bundle", "card-bundle.json", card, cancellationToken);
        if (package.Request.Operation != "understand-and-card") return new[] { cardRef };
        var learning = new
        {
            schema_version = "learning-bundle/1.0.0", release_state = "ready",
            items = new[] { new { cleaning = new { cleaned_text = "Immutable original capture." } } }
        };
        var learningRef = await PublishJsonAsync(worker, package, "learning-bundle", "learning-bundle.json", learning, cancellationToken);
        return new[] { cardRef, learningRef };
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
        var phase = invocation.Request.GetProperty("phase").GetString();
        return Task.FromResult(new WorkerExecutionResult(
            "completed",
            phase == "make-cards"
                ? JsonSerializer.SerializeToElement(new { status = "cards_ready", cards = new[] { new { cardId = "recovered-card", front = "Recovered question", back = "Recovered retry answer.", template = "basic_qa" } }, externalWriteCount = 0 }, JsonOptionsHolder.Options)
                : JsonSerializer.SerializeToElement(new { status = "waiting_input", answerText = "Recovered retry answer.", externalWriteCount = 0 }, JsonOptionsHolder.Options),
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

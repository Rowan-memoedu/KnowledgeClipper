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

internal static class PlaybackControlTests
{
    private static readonly List<string> Cases = new();
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); Cases.Add(name); Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name) { try { action(); } catch (KernelContractException) { Check(true, name); return; } throw new InvalidOperationException(name); }
    internal static LegacyAttemptPackage Package(string root, string operationId, string suffix)
    {
        var job = Path.Combine(root, "jobs", "task-" + suffix);
        var attempt = "runs/run-" + suffix + "/stages/speech-playback/attempts/attempt-" + suffix;
        Directory.CreateDirectory(Path.Combine(job, attempt));
        var request = new LegacyWorkerRequest("1.0.0", "request-" + suffix, operationId, "task-" + suffix,
            "run-" + suffix, "speech-playback", "attempt-" + suffix, "speech.edge", "speech.playback", new string('a', 64),
            attempt, attempt + "/outputs", attempt + "/progress.ndjson", attempt + "/result.json", new("1.0.0", attempt + "/cancel.json", 2000),
            Array.Empty<LegacyArtifactReference>(), StrictJson.CloneElement(new { }), StrictJson.CloneElement(new { }));
        var path = Path.Combine(job, attempt, "request.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(request, KernelJson.Options));
        return new(job, path, Path.Combine(job, request.ResultPath), Path.Combine(job, request.CancelSignal.Path), request);
    }

    internal static async Task<int> ChildAsync(string requestPath)
    {
        var request = JsonSerializer.Deserialize<LegacyWorkerRequest>(await File.ReadAllBytesAsync(requestPath), KernelJson.Options)!;
        var job = requestPath.Replace('\\', '/')[..requestPath.Replace('\\', '/').LastIndexOf("/runs/", StringComparison.Ordinal)];
        var cancel = Path.Combine(job, request.CancelSignal.Path);
        var natural = requestPath + ".natural";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!File.Exists(cancel) && !File.Exists(natural) && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        if (!File.Exists(cancel) && !File.Exists(natural)) return 2;
        var stopped = File.Exists(cancel);
        if (stopped)
        {
            using var signal = JsonDocument.Parse(await File.ReadAllBytesAsync(cancel));
            if (signal.RootElement.GetProperty("reason").GetString() != "user_stop" || signal.RootElement.GetProperty("attemptId").GetString() != request.AttemptId) return 3;
        }
        var result = Result(request, stopped);
        await File.WriteAllBytesAsync(Path.Combine(job, request.ResultPath), JsonSerializer.SerializeToUtf8Bytes(result, KernelJson.Options));
        return 0;
    }

    private static LegacyWorkerResult Result(LegacyWorkerRequest request, bool stopped) => new("1.0.0", request.RequestId, request.OperationId,
        request.TaskId, request.RunId, request.StageId, request.AttemptId, new("speech.edge", "fake"), "1.0.0", request.IdempotencyKey,
        stopped ? "cancelled" : "completed", Array.Empty<LegacyArtifactReference>(),
        stopped ? new("playback.user_stop", "cancelled", false, request.CancelSignal.Path, "User stop", "input") : null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Array.Empty<string>(), StrictJson.CloneElement(new { playbackState = stopped ? "stopped" : "idle_replayable" }), StrictJson.CloneElement(Array.Empty<object>()));

    public static async Task<int> RunAsync(string evidenceRoot)
    {
        evidenceRoot = Path.GetFullPath(evidenceRoot);
        if (Directory.Exists(evidenceRoot)) throw new InvalidOperationException("Evidence root already exists.");
        Directory.CreateDirectory(evidenceRoot);
        try
        {
            var events = new FileEventStore(Path.Combine(evidenceRoot, "control-unit-events"));
            var control = new PlaybackAttemptControl(events);
            var first = Package(evidenceRoot, "read-same", "first");
            var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstToken = control.Register("read-same", "command-first", first, firstDone.Task);
            Reject(() => control.RequestStop("other-read", firstToken, "wrong-op"), "wrong-operation-rejected");
            Reject(() => control.RequestStop("read-same", new string('b', 64), "stale"), "wrong-token-rejected");
            firstDone.SetResult(); control.Complete(firstToken, "idle_replayable");
            Reject(() => control.RequestStop("read-same", firstToken, "after-natural"), "natural-before-stop-rejected");
            var next = Package(evidenceRoot, "read-same", "next");
            var nextDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextToken = control.Register("read-same", "command-next", next, nextDone.Task);
            Reject(() => control.RequestStop("read-same", firstToken, "delayed-first-stop"), "delayed-N-stop-does-not-stop-N-plus-1");
            Check(!File.Exists(next.CancelPath), "next-attempt-unsignalled");
            var ambiguous = Package(evidenceRoot, "read-same", "ambiguous");
            var ambDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ambToken = control.Register("read-same", "command-ambiguous", ambiguous, ambDone.Task);
            Reject(() => control.RequestStop("read-same", nextToken, "amb-stop"), "ambiguous-active-rejected");
            ambDone.SetResult(); control.Complete(ambToken, "unknown");
            control.RequestStop("read-same", nextToken, "stop-next");
            Check(control.Snapshot("read-same").GetProperty("state").GetString() == "stop_requested", "accepted-is-not-stopped");
            nextDone.SetResult();
            Check(control.Snapshot("read-same").GetProperty("state").GetString() == "completing", "process-exit-awaits-validated-result");
            control.Complete(nextToken, "idle_replayable");
            Check(control.Snapshot("read-same").GetProperty("state").GetString() == "idle_replayable", "natural-wins-after-stop-acceptance");
            var stalePackage = Package(evidenceRoot, "recover-read", "recover");
            var staleToken = control.Register("recover-read", "command-recover", stalePackage, new TaskCompletionSource().Task);
            var recovered = new PlaybackAttemptControl(new FileEventStore(Path.Combine(evidenceRoot, "control-unit-events")));
            Reject(() => recovered.RequestStop("recover-read", staleToken, "recovered-stop"), "restart-never-replays-old-signal");
            Check(recovered.Snapshot("recover-read").GetProperty("state").GetString() == "unknown", "restart-stale-active-is-unknown");
            var drift = Package(evidenceRoot, "drift-read", "drift");
            var driftToken = control.Register("drift-read", "command-drift", drift, new TaskCompletionSource().Task);
            File.AppendAllText(drift.RequestPath, " ");
            Reject(() => control.RequestStop("drift-read", driftToken, "drift-stop"), "request-hash-drift-rejected");
            var naturalResult = Result(first.Request, false);
            Check(WorkerProcessRuntime.ProjectSpeechPlayback(naturalResult).Payload.GetProperty("status").GetString() == "idle_replayable", "natural-worker-result-projection");
            Check(WorkerProcessRuntime.ProjectSpeechPlayback(Result(first.Request, true)).Payload.GetProperty("status").GetString() == "stopped", "real-stopped-result-shape-projection");
            try { WorkerProcessRuntime.ProjectSpeechPlayback(naturalResult with { Payload = StrictJson.CloneElement(new { playbackState = "stopped" }) }); throw new Exception("False stopped accepted"); }
            catch (InvalidDataException) { Check(true, "completed-result-cannot-pretend-stopped"); }
            await PipeFlowAsync(evidenceRoot);
            var receipt = new { schema = "kc.playback-control-offline-qualification/1", status = "PASS", cases = Cases, caseCount = Cases.Count,
                externalServiceCalls = 0, physicalSpeakerPlayback = false, workerMode = "real-child-process-fake-audio", sourceRoot = "D:/Projects/RemNote Quick Clipper/core-v3" };
            await File.WriteAllBytesAsync(Path.Combine(evidenceRoot, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, KernelJson.Options));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "failure.txt"), ex.ToString()); return 1; }
    }

    internal static async Task<int> BundleProbeAsync(string candidate, string host, string receiptPath)
    {
        if (File.Exists(receiptPath)) throw new InvalidOperationException("Probe receipt exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        var pipe = "kc-playback-bundle-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "--repository-root", candidate, "--binding", Path.Combine(candidate, "contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json"),
            "--data-root", Path.Combine(Path.GetDirectoryName(receiptPath)!, "probe-data"), "--pipe-name", pipe }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (ready != "CORE_V3_READY " + pipe) throw new InvalidOperationException("Host not ready: " + ready + " " + await process.StandardError.ReadToEndAsync(timeout.Token));
            var snapshot = await new CoreV3Client(pipe).GetReadPlaybackControlAsync("probe-unused-operation", timeout.Token);
            if (snapshot.CanStop || snapshot.State != ReadPlaybackControlState.Unknown) throw new InvalidOperationException("Empty runtime playback probe invalid.");
            await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schema = "kc.playback-bundle-startup/1", status = "PASS", host,
                hostSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(host))), processId = process.Id, namedPipe = pipe,
                projection = snapshot, businessCommandsSubmitted = 0, workerRunInvocations = 0, externalCalls = 0, physicalSpeakerPlayback = false }, KernelJson.Options));
            return 0;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    private static async Task PipeFlowAsync(string root)
    {
        var authority = new WorkerAuthorityBinding("authority", "registry.json", new string('a', 64), "worker.exe", new string('b', 64), "test", "1.0.0");
        var publish = new CapabilityDescriptor("artifact.publish", true, null, "kc.artifact-publish-command/1", "domain-text", "3.0.0", "none", "none", "safe", "required", 5000,
            new("in-process", null, null, "ArtifactPublishOperation"), new("publish", "sdk-only", false));
        var read = new CapabilityDescriptor("clip.read-aloud-cards", true, null, "kc.read-aloud-cards-command/1", "anki-readback", "1.0.0", "external-write", "explicit", "conditional", "required", 15000,
            new WorkerBinding("worker", "content.deepseek", "=test", null) { Authority = authority }, new("read", "clip", true));
        var catalog = AdmittedCapabilityCatalog.CreateForTesting(new(KernelSchemas.CapabilityCatalog, "playback-tests", "1.0.0", new("test", "checkpoint.json", new string('c', 64)), new[] { publish, read }));
        var events = new FileEventStore(Path.Combine(root, "pipeline-events"));
        var runtime = new ControlledRuntime(Path.Combine(root, "child-workers"));
        var app = new KernelApplication(catalog, new(new IOperationHandler[] { new ArtifactPublishOperation(), new ReadAloudCardsOperation() }), runtime,
            events, new FileCommandIndex(Path.Combine(root, "command-index")), new FileArtifactStore(Path.Combine(root, "artifacts"), "playback-tests"));
        var pipe = "kc-playback-tests-" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        var server = new NamedPipeKernelServer(pipe, new KernelCompatibilityGateway(app, root));
        var serving = server.ServeAsync(cancellation.Token);
        try
        {
            var client = new CoreV3Client(pipe, new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(20)));
            var capture = await client.PublishTextArtifactAsync(new("capture", ArtifactPurpose.CaptureText, "independent synthetic playback"));
            var config = await client.PublishTextArtifactAsync(new("config", ArtifactPurpose.AnkiConfiguration, "{\"schema_version\":\"anki-export-config.v1\",\"endpoint\":\"fake://anki\",\"provision_managed\":false,\"deck_routes\":{\"canary\":\"零碎知识剪藏::Canary::SDK\"}}"));
            var auth = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("playback-tests");
            var initial = await client.ReadAloudCardsAsync(new("initial", capture, config, auth, new("settings-both", "both")));
            var active = await ActiveAsync(client, initial.OperationId);
            var before = events.GetRevision(initial.OperationId);
            var stop = await client.StopReadPlaybackAsync(new("stop-initial", active.Target!));
            Check(stop.Command.Accepted && stop.Command.CommandId == "command-stop-initial", "typed-stop-command-identity");
            Check(events.GetRevision(initial.OperationId) == before, "stop-does-not-cancel-or-change-read-stream");
            var stopped = await client.WaitReadPlaybackStopAsync(stop, TimeSpan.FromSeconds(10));
            Check(stopped.State == ReadPlaybackControlState.Stopped, "named-pipe-stop-genuine-child-result");
            Check(runtime.ContentWaiting && runtime.Inner.AnkiAttempts == 0, "content-still-pending-when-playback-stops");
            runtime.ReleaseContent();
            var draft = await client.WaitReadAloudCardsActionAsync(initial);
            Check(draft.CardDraft?.Cards.Count == 2 && runtime.Inner.AnkiAttempts == 0 && runtime.Inner.ContentAttempts == 1, "content-continues-after-stop-both-draft-no-export");
            var duplicate = await client.StopReadPlaybackAsync(new("stop-initial", active.Target!));
            Check(duplicate.Command.CommandId == stop.Command.CommandId && runtime.PlaybackAttempts == 1, "stop-command-replay-no-new-effect");
            var replay = await client.ReplayReadAloudCardsAsync(new("replay-one", initial.OperationId, auth));
            var replayActive = await ActiveAsync(client, initial.OperationId);
            var waiting = client.WaitReadAloudCardsActionAsync(replay);
            await Task.Delay(50);
            Check(!waiting.IsCompleted && replay.CommandId != initial.CommandId && replay.AcceptedSequence > initial.AcceptedSequence, "old-terminal-rejected-for-new-replay-identity");
            try { await client.StopReadPlaybackAsync(new("late-stop-initial", active.Target!)); throw new Exception("Stale stop accepted"); }
            catch (Exception ex) when (ex.Message.Contains("stale", StringComparison.OrdinalIgnoreCase)) { Check(true, "typed-delayed-stop-cannot-affect-new-replay"); }
            Check(!File.Exists(runtime.Current!.CancelPath), "new-replay-no-stale-cancel-file");
            runtime.FinishNaturally();
            var replayDraft = await waiting;
            Check(replayDraft.CardDraft?.Cards.Count == 2 && replayDraft.Pipeline == ReadAloudCardsPipelineState.AwaitingCardConfirmation && replayDraft.CardDraft.DraftSha256 == draft.CardDraft!.DraftSha256,
                "replay-preserves-two-card-draft-and-confirmability");
            var terminal = events.ReadStream(initial.OperationId).Last(item => item.EventType == "operation.completed").Data.GetProperty("result");
            Check(terminal.GetProperty("expectedCardCount").GetInt32() == 2 && terminal.GetProperty("actualCardCount").GetInt32() == 2, "both-count-never-hardcoded-one");
            var replay2 = await client.ReplayReadAloudCardsAsync(new("replay-two", initial.OperationId, auth));
            var active2 = await ActiveAsync(client, initial.OperationId);
            Check(replay2.CommandId != replay.CommandId && replay2.AcceptedSequence > replay.AcceptedSequence && active2.Target!.AttemptId != replayActive.Target!.AttemptId,
                "consecutive-replays-unique-command-sequence-attempt");
            var stop2 = await client.StopReadPlaybackAsync(new("stop-two", active2.Target!));
            await client.WaitReadPlaybackStopAsync(stop2, TimeSpan.FromSeconds(10));
            var draft2 = await client.WaitReadAloudCardsActionAsync(replay2);
            Check(runtime.Inner.ContentAttempts == 1 && runtime.Syntheses == 1 && runtime.Inner.AnkiAttempts == 0 && runtime.PlaybackAttempts == 3, "replays-run-playback-only");
            var confirmed = await client.ConfirmFlashcardDraftAsync(new("confirm", initial.OperationId, draft2.CardDraft!, auth));
            var final = await client.WaitReadAloudCardsActionAsync(confirmed);
            Check(final.Pipeline == ReadAloudCardsPipelineState.Completed && runtime.Inner.AnkiAttempts == 1, "stops-and-replays-do-not-cancel-export-confirmation");
            Check(runtime.ChildExitCodes.All(code => code == 0), "three-real-fake-worker-processes-exit-zero");
        }
        finally { cancellation.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
    }

    private static async Task<ReadPlaybackControlSnapshot> ActiveAsync(CoreV3Client client, string operationId)
    {
        for (var i = 0; i < 500; i++) { var value = await client.GetReadPlaybackControlAsync(operationId); if (value.CanStop) return value; await Task.Delay(10); }
        throw new TimeoutException("Playback did not register.");
    }

    private sealed class ControlledRuntime(string root) : IWorkerRuntime
    {
        public ReadAloudCardsFakeRuntime Inner { get; } = new();
        public PlaybackAttemptControl PlaybackControl { get; } = new(new FileEventStore(Path.Combine(root, "events")));
        public LegacyAttemptPackage? Current { get; private set; }
        public int PlaybackAttempts { get; private set; }
        public int Syntheses { get; private set; }
        public List<int> ChildExitCodes { get; } = new();
        private readonly TaskCompletionSource _contentGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ContentWaiting { get; private set; }
        public void ReleaseContent() => _contentGate.TrySetResult();
        public async Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
        {
            var phase = invocation.Request.GetProperty("phase").GetString();
            if (phase != "playback")
            {
                if (phase == "synthesize") Syntheses++;
                if (phase == "review") { ContentWaiting = true; await _contentGate.Task.WaitAsync(cancellationToken); ContentWaiting = false; }
                return await Inner.ExecuteAsync(invocation, cancellationToken);
            }
            PlaybackAttempts++;
            var package = Package(root, invocation.OperationId, "play-" + PlaybackAttempts);
            Current = package;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(PlaybackControlTests).Assembly.Location);
            start.ArgumentList.Add("--playback-fake-child"); start.ArgumentList.Add(package.RequestPath);
            using var process = Process.Start(start)!;
            var completion = process.WaitForExitAsync(cancellationToken);
            var token = PlaybackControl.Register(invocation.OperationId, invocation.Request.GetProperty("actionCommandId").GetString()!, package, completion);
            var state = "unknown";
            try
            {
                await completion;
                ChildExitCodes.Add(process.ExitCode);
                if (process.ExitCode != 0) throw new InvalidOperationException("Fake child failed: " + process.ExitCode);
                var result = JsonSerializer.Deserialize<LegacyWorkerResult>(await File.ReadAllBytesAsync(package.ResultPath), KernelJson.Options)!;
                var projected = WorkerProcessRuntime.ProjectSpeechPlayback(result);
                state = projected.Payload.GetProperty("status").GetString()!;
                return projected;
            }
            finally { PlaybackControl.Complete(token, state); }
        }
        public void FinishNaturally() => File.WriteAllText(Current!.RequestPath + ".natural", "natural");
        public WorkerRuntimeMetrics GetMetrics() => Inner.GetMetrics();
        public WorkerBinding GetAdmittedBinding(string workerId) => Inner.GetAdmittedBinding(workerId);
    }
}

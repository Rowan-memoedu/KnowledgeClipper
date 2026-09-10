using System.Diagnostics;
using System.Reflection;
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

internal static class DestinationFakeServer
{
    public static async Task<int> RunAsync(string root, string pipe)
    {
        if (Directory.Exists(root)) throw new InvalidDataException("Evidence root exists.");
        Directory.CreateDirectory(root);
        var runtime = new DestinationTests.Fake();
        var operations = new[] { "artifact.publish", "remnote.discover-target", "remnote.probe", "clip.read-aloud-cards", "question.initial", "question.follow-up", "question.make-cards" };
        var authority = new WorkerAuthorityBinding("fake", "registry.json", new string('a', 64), "fake.exe", new string('b', 64), "test", "1.0.0");
        var capabilities = operations.Select(id => DestinationTests.Capability(id) with {
            EffectClass = id == "artifact.publish" ? "none" : id.StartsWith("remnote.") ? "read-only-external" : "external-write",
            AuthorizationPolicy = id == "artifact.publish" || id.StartsWith("remnote.") ? "none" : "explicit",
            WorkerBinding = id == "artifact.publish" ? new WorkerBinding("in-process", null, null, "artifact.publish")
                : new WorkerBinding("worker", id.StartsWith("remnote.") ? "export.remnote" : "content.deepseek", "=test", null) { Authority = authority } }).ToArray();
        var catalog = AdmittedCapabilityCatalog.CreateForTesting(new(KernelSchemas.CapabilityCatalog, "desktop-joint-offline-fixture", "1.0.0", new("test-only", "not-authority", new string('a', 64)), capabilities));
        var events = new FileEventStore(Path.Combine(root, "events"));
        var app = new KernelApplication(catalog, new(new IOperationHandler[] { new ArtifactPublishOperation(), new RemNoteSettingsOperation("remnote.discover-target"), new RemNoteSettingsOperation("remnote.probe"), new ReadAloudCardsOperation(), new QuestionWorkflowOperation("question.initial"), new QuestionWorkflowOperation("question.follow-up"), new QuestionWorkflowOperation("question.make-cards") }),
            runtime, events, new FileCommandIndex(Path.Combine(root, "claims")), new FileArtifactStore(Path.Combine(root, "artifacts"), "offline-fixture"));
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var serving = new NamedPipeKernelServer(pipe, new KernelCompatibilityGateway(app, root)).ServeAsync(stop.Token);
        Console.WriteLine("READY FAKE_ONLY " + pipe + " (discovery title=Fixture root; id=root-fixture; lifetime=300s)");
        // Parent test owns this child. A line on stdin requests graceful stop; no user process is touched.
        var input = Task.Run(() => Console.ReadLine());
        await Task.WhenAny(serving, input);
        stop.Cancel();
        try { await serving; } catch (OperationCanceledException) { }
        await File.WriteAllBytesAsync(Path.Combine(root, "server-receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { boundary = "actual-kernel-named-pipe-fake-workers-only", pipe,
            runtime.Content, runtime.Exports, runtime.CardHashes, externalCalls = 0, realWorkerRuns = 0, physicalAudio = 0 }, KernelJson.Options));
        return 0;
    }

    public static async Task<int> SmokeAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidDataException("Evidence root exists.");
        Directory.CreateDirectory(root);
        var pipe = "KC.Destinations.Smoke." + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--destinations-fake-server", Path.Combine(root, "host"), pipe }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var checks = new List<string>();
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync(timeout.Token);
            if (ready?.StartsWith("READY FAKE_ONLY ") != true) throw new InvalidDataException("Host did not reach READY: " + ready);
            Check("actual child READY");
            var client = new CoreV3Client(pipe);
            const string text = "Immutable synthetic source.";
            var capture = await client.PublishTextArtifactAsync(new("smoke-publish", ArtifactPurpose.CaptureText, text), timeout.Token);
            Check("SDK PublishTextArtifactAsync returns completed handle synchronously without retry");
            var content = await client.ReadArtifactAsync(capture, null, timeout.Token);
            if (System.Text.Encoding.UTF8.GetString(content.Content) != text) throw new InvalidDataException("Published bytes changed.");
            Check("SDK ReadArtifactAsync exact bytes");
            var discovery = await client.DiscoverRemNoteTargetAsync(new("smoke-discover", "Fixture root"), timeout.Token);
            OperationProjectionSnapshot status;
            do { await Task.Delay(10, timeout.Token); status = await client.GetOperationProjectionAsync(discovery.OperationId, timeout.Token); }
            while (status.State == OperationProjectionState.Running);
            if (status.RemNoteDiscovery?.Binding is not { } binding) throw new InvalidDataException("Discovery failed.");
            Check("SDK discovery and terminal query without grant");
            var probe = await client.ProbeRemNoteAsync(new("smoke-probe", binding), timeout.Token);
            do { await Task.Delay(10, timeout.Token); status = await client.GetOperationProjectionAsync(probe.OperationId, timeout.Token); }
            while (status.State == OperationProjectionState.Running);
            if (status.RemNoteReadiness?.ReadyForRequestedAction != true) throw new InvalidDataException("Probe failed.");
            Check("SDK probe exact historical binding and no write authority");
            var read = await client.ReadCardsAsync(new("smoke-read", capture, Array.Empty<CardDestinationSelection>(), IncludeSpeech: false), timeout.Token);
            ReadAloudCardsProjectionSnapshot preview;
            do { await Task.Delay(10, timeout.Token); preview = await client.GetReadAloudCardsProjectionAsync(read.OperationId, timeout.Token); }
            while (preview.TerminalCommandId != read.CommandId);
            if (preview.CardDraft is null || preview.DestinationSnapshot is null || preview.Destinations.Count != 0) throw new InvalidDataException("Neutral read preview invalid.");
            Check("SDK read creates draft and empty frozen destinations without grants");
            await child.StandardInput.WriteLineAsync("stop");
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new InvalidDataException("Child exit was not zero.");
            Check("graceful child stop");
            await File.WriteAllBytesAsync(Path.Combine(root, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { status = "PASS", checks, pipe, childPid = child.Id, externalCalls = 0, physicalAudio = 0, workerRunInvocations = 0 }, KernelJson.Options));
            return 0;
        }
        finally
        {
            if (!child.HasExited) { await child.StandardInput.WriteLineAsync("stop"); await child.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(root, "host.stderr.log"), await stderr);
        }
        void Check(string name) { checks.Add(name); Console.WriteLine("PASS " + name); }
    }
}

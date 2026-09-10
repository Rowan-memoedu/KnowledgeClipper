using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;

internal static class Program
{
    private static int assertions;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); assertions++; Console.WriteLine("PASS " + label); }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Check(Hash(typeof(CoreV3Client).Assembly.Location) == "271A32528D779C5C052FB98C74FBE1FEC5CEAB2229086BFB90F8BEA811936591", "exact published SDK milestone");
            Check(Hash(Assembly.Load("KnowledgeClipper.Core.V3.Contracts").Location) == "69894606F2F3A5C92F100EBFF6FED5A388F9DB1F222E1A954FBA80873155FFA0", "exact published Contracts milestone");
            foreach (var title in new[] { "剪藏", "另一个目标" })
            {
                var fake = new Fixture(title);
                var bridge = fake.Bridge();
                var result = await bridge.DiscoverAsync(new("request-discover", title), CancellationToken.None);
                Check(result.Binding.RootTitle == title && fake.DiscoveryCount == 1 && fake.ReadCount == 1, "discovery preserves exact selector and reads evidence");
                var reference = CoreV3Client.ExportRemNoteBinding(result.Binding);
                var restored = await bridge.RestoreAsync(reference, CancellationToken.None);
                Check(restored.Status == "verified_historical" && fake.ProbeCount == 0, "restore does not probe or imply readiness");
                var readiness = await bridge.ProbeAsync(new("request-probe", result.Binding), CancellationToken.None);
                Check(RemNoteSettingsSdkBridge.IsReady(readiness) && fake.ProbeCount == 1 && fake.RestoreCount == 2, "probe uses verified binding and typed readiness");
            }
            foreach (var scenario in new[] { "rejected", "operation-mismatch", "failed", "unknown", "duplicate", "missing-binding", "title-mismatch", "binding-operation-mismatch", "evidence-mismatch", "empty-evidence", "hash-mismatch" })
            {
                var fake = new Fixture("target") { Scenario = scenario };
                await Reject(() => fake.Bridge().DiscoverAsync(new("request", "target"), CancellationToken.None), "discovery " + scenario);
                Check(fake.ProbeCount == 0, "discovery negative never invokes probe");
            }
            foreach (var missing in new[] { "connection", "permission", "exact-match", "requested-action" })
            {
                var fake = new Fixture("target") { Scenario = missing };
                var r = await fake.Bridge().ProbeAsync(new("request", fake.Binding), CancellationToken.None);
                Check(!RemNoteSettingsSdkBridge.IsReady(r), "not ready when missing " + missing);
            }
            foreach (var scenario in new[] { "restore-invalid", "restore-mismatch", "restore-title-mismatch" })
            {
                var fake = new Fixture("target") { Scenario = scenario };
                await Reject(() => fake.Bridge().ProbeAsync(new("request", fake.Binding), CancellationToken.None), scenario);
                Check(fake.ProbeCount == 0, "invalid historical binding blocks probe submission");
            }
            var blank = new Fixture("target");
            await Reject(() => blank.Bridge().DiscoverAsync(new("request", " "), CancellationToken.None), "blank title");
            Check(blank.DiscoveryCount == 0, "blank title fails before submission");
            await LateQueryCancellation();
            await Reject(() => new Fixture("target") { Scenario = "running" }.Bridge().DiscoverAsync(new("request", "target"), CancellationToken.None), "bounded observation timeout");
            if (args.Length == 2 && args[0] == "--receipt")
            {
                var path = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(stream, new { status = "PASS-SDK-COMPILE-AND-FAKE-ONLY", assertions,
                    sdkSha256 = Hash(typeof(CoreV3Client).Assembly.Location),
                    externalCalls = 0, workerRuns = 0, physicalAudio = 0, productionUiExercised = false,
                    limitation = "Core handler qualification pending; this test covers the bridge only, SettingsForm has a separate Fake gate; no user candidate generated" }, new JsonSerializerOptions { WriteIndented = true });
            }
            Console.WriteLine("REMNOTE_SDK_COMPILE_FAKE PASS assertions=" + assertions);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task LateQueryCancellation()
    {
        var fake = new Fixture("A") { QueryGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var bridge = fake.Bridge();
        using var cancel = new CancellationTokenSource();
        var pending = bridge.DiscoverAsync(new("request-A", "A"), cancel.Token);
        await fake.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Reject(() => pending, "non-cooperative query releases cancelled observer");
        var newer = new Fixture("B");
        var b = await newer.Bridge().DiscoverAsync(new("request-B", "B"), CancellationToken.None);
        fake.QueryGate.SetResult(true);
        await fake.QueryFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(fake.ReadCount == 0 && b.Binding.RootTitle == "B", "late A query does not read evidence or change B result");
    }

    private static async Task Reject(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        { Check(true, label + " rejected"); return; }
        throw new Exception(label + " unexpectedly succeeded");
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Fixture
    {
        internal string Scenario = "ready";
        internal int DiscoveryCount, ProbeCount, RestoreCount, ReadCount;
        internal TaskCompletionSource<bool> QueryGate;
        internal readonly TaskCompletionSource<bool> QueryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> QueryFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal VerifiedRemNoteBinding Binding;
        private readonly string title;
        internal Fixture(string title) { this.title = title; Binding = FakeBinding("discovery", title); }
        internal RemNoteSettingsSdkBridge Bridge() => new(
            (request, ct) => { DiscoveryCount++; Check(request.RootTitle == title, "SDK request selector unchanged"); return Task.FromResult(new OperationSubmission("discovery", 1, Scenario != "rejected")); },
            (request, ct) => { ProbeCount++; Check(request.Target.Evidence.Token == Binding.Evidence.Token, "probe carries original opaque evidence"); return Task.FromResult(new OperationSubmission("probe", 1, true)); },
            async (id, ct) =>
            {
                QueryEntered.TrySetResult(true);
                if (QueryGate != null) await QueryGate.Task; // Deliberately ignores cancellation.
                var binding = Scenario switch {
                    "missing-binding" => null,
                    "title-mismatch" => FakeBinding("discovery", "wrong-title"),
                    "binding-operation-mismatch" => FakeBinding("other-operation", title),
                    _ => Binding };
                var state = Scenario switch { "running" => OperationProjectionState.Running, "failed" => OperationProjectionState.Failed, "unknown" => OperationProjectionState.Degraded, _ => OperationProjectionState.Succeeded };
                var p = new OperationProjectionSnapshot(Scenario == "operation-mismatch" ? "other" : id, state, 2, "fixture-state", null)
                {
                    RemNoteDiscovery = new("fixture", Array.Empty<string>(), Scenario == "duplicate" ? 2 : 1, true, binding,
                        Scenario == "evidence-mismatch" ? FakeBinding("different-evidence", title).Evidence : Binding.Evidence),
                    RemNoteReadiness = new(Scenario != "connection", Scenario != "permission", Scenario != "exact-match", Scenario != "requested-action", Array.Empty<string>(), Binding.Evidence)
                };
                QueryFinished.TrySetResult(true);
                return p;
            },
            (handle, ct) => { ReadCount++; if (Scenario == "hash-mismatch") throw new InvalidOperationException("artifact_hash_mismatch"); return Task.FromResult(Scenario == "empty-evidence" ? Array.Empty<byte>() : new byte[] { 1 }); },
            (reference, ct) => { RestoreCount++; return Task.FromResult(Scenario == "restore-invalid" ? new BindingRestoreResult("invalid", null, "missing-evidence") :
                new BindingRestoreResult("verified_historical", Scenario == "restore-mismatch" ? FakeBinding("other", title) : Scenario == "restore-title-mismatch" ? FakeBinding("discovery", "changed") : Binding, null)); },
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1));
    }

    // Offline fixture only: create SDK-owned opaque values through their real constructors.
    // Production bridge never uses reflection, parses a token, or constructs a verified binding.
    private static VerifiedRemNoteBinding FakeBinding(string operation, string title)
    {
        var assembly = Assembly.Load("KnowledgeClipper.Core.V3.Contracts");
        var reference = Activator.CreateInstance(assembly.GetType("KnowledgeClipper.Core.V3.Contracts.ArtifactReference"),
            "fixture", "evidence-" + operation, "remnote-discovery", "fixture", null, "fixture-store");
        var binding = Activator.CreateInstance(assembly.GetType("KnowledgeClipper.Core.V3.Contracts.RemNoteBindingReference"),
            operation, reference, "root-" + title, title);
        return (VerifiedRemNoteBinding)Activator.CreateInstance(typeof(VerifiedRemNoteBinding), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { binding }, null);
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

internal static class DestinationRecoveryTests
{
    // Only the dedicated test child exits. No real worker or user process is started/stopped.
    public static async Task<int> ChildAsync(string root, string cut)
    {
        Directory.CreateDirectory(root);
        var events = new CutStore(new FileEventStore(Path.Combine(root, "events")), cut);
        var store = new FileArtifactStore(Path.Combine(root, "artifacts"), "test");
        var index = new FileCommandIndex(Path.Combine(root, "claims"));
        var app = Application(events, store, index, new CrashFake(cut));
        await Submit(app, index, DestinationTests.Command("discovery", "remnote.discover-target", "kc.remnote-discovery/1", new { rootTitle = "Fixture root" }));
        var discovery = events.ReadStream("discovery").Last(x => x.EventType == "operation.completed");
        var binding = discovery.Data.GetProperty("result").GetProperty("discovery").GetProperty("binding").Deserialize<RemNoteBindingReference>(KernelJson.Options)!;
        var anki = await Publish("anki-export-config", "kc.anki-export-config/1", new { schema_version = "anki-export-config.v1", provision_managed = false, deck_routes = new { canary = "零碎知识剪藏::Canary::Offline" } });
        var remnote = await Publish("remnote-settings", "remnote-destination-settings.v1", new { schemaVersion = "remnote-destination-settings.v1", enabled = true, target = new { rootRemId = binding.RootRemId, rootTitle = binding.RootTitle }, advanced = new { sink = "mcp-direct", clipRouting = "clip-daily-route", cardRouting = "card-export", timeZone = "Asia/Shanghai", dateFormat = "yyyy-MM-dd" } });
        var capture = await DomainArtifactFactory.PublishTextAsync(store, DestinationTests.Command("capture", "artifact.publish", "kc.artifact-publish-command/1", new { }), "capture-text", "1.0.0", "kc.capture-text/1", "Immutable synthetic source.", Array.Empty<ArtifactReference>(), default);
        var selections = new[] { new DestinationSelection("anki", anki, "canary", null), new DestinationSelection("remnote", remnote, null, binding) };
        var read = DestinationTests.Command("read", "clip.read-aloud-cards", "kc.read-aloud-cards-command/1", new { destinationMode = "confirmed-cards", capture, destinations = selections, includeSpeech = false }, new[] { capture }.Concat(CardDestinations.Inputs(selections)).ToArray());
        await Submit(app, index, read);
        var preview = events.ReadStream("read").Last(x => x.EventType == "operation.completed");
        var snapshot = preview.Artifacts.Single(x => x.Kind == "destination-snapshot");
        var confirm = DestinationTests.Command("confirm", read.Operation, read.Body.Schema, new { destinationMode = "confirmed-cards", action = "confirm_card_draft", sourceOperationId = "read", draft = preview.Data.GetProperty("result").GetProperty("cardDraft"), destinationSnapshot = snapshot,
            grants = new[] { new DestinationWriteGrant("anki", "confirmed", "isolated-canary-deck", "零碎知识剪藏::Canary::Offline"), new DestinationWriteGrant("remnote", "confirmed", "confirmed-remnote-root", binding.RootRemId) } }) with { OperationId = "read" };
        foreach (var attack in new[] { "cross-operation", "grant-scope", "snapshot-hash", "draft-token" })
        {
            var data = confirm.Body.Data.Deserialize<Dictionary<string, JsonElement>>(KernelJson.Options)!;
            if (attack == "cross-operation") data["sourceOperationId"] = StrictJson.CloneElement("foreign-operation");
            if (attack == "grant-scope") data["grants"] = StrictJson.CloneElement(new[] { new DestinationWriteGrant("anki", "confirmed", "isolated-canary-deck", "零碎知识剪藏::Canary::Offline"), new DestinationWriteGrant("remnote", "confirmed", "isolated-canary-deck", binding.RootRemId) });
            if (attack == "snapshot-hash") data["destinationSnapshot"] = StrictJson.CloneElement(anki);
            if (attack == "draft-token")
            {
                var draft = data["draft"].Deserialize<Dictionary<string, JsonElement>>(KernelJson.Options)!;
                draft["token"] = StrictJson.CloneElement("foreign-token"); data["draft"] = StrictJson.CloneElement(draft);
            }
            var denied = DestinationTests.Command(attack, confirm.Operation, confirm.Body.Schema, data) with { OperationId = confirm.OperationId };
            await app.HandleCommandAsync(denied, default);
            var deniedReceipt = await AwaitReceipt(index, denied);
            DestinationTests.Check(deniedReceipt.Error is not null && !events.ReadAllStreams().Keys.Any(x => x.StartsWith("destination-", StringComparison.Ordinal)), "Kernel command index rejects " + attack + " before any destination");
        }
        try { await app.HandleCommandAsync(confirm with { Authorization = new("wrong-scope", "isolated-canary-deck") }, default); throw new InvalidDataException("Envelope authority accepted."); }
        catch (KernelContractException ex) when (ex.Code == "authorization_not_applicable") { DestinationTests.Check(true, "Kernel neutral route rejects Anki envelope authority"); }
        await File.WriteAllBytesAsync(Path.Combine(root, "confirm.json"), JsonSerializer.SerializeToUtf8Bytes(confirm, KernelJson.Options));
        await Submit(app, index, confirm);
        throw new InvalidDataException("Child must exit at selected cut.");
        Task<ArtifactReference> Publish(string kind, string schema, object data) => DomainArtifactFactory.PublishJsonAsync(store, DestinationTests.Command(kind, "artifact.publish", schema, new { }), kind, "1.0.0", schema, data, Array.Empty<ArtifactReference>(), default);
    }
    public static async Task RunAsync(string parent)
    {
        foreach (var cut in new[] { "before-start", "after-start", "before-terminal" })
        foreach (var startup in new[] { false, true })
        {
            var root = Path.Combine(parent, cut + (startup ? "-startup" : "-resubmit"));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var arg in new[] { "--destinations-crash-child", root, cut }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            var childOutput = await stdout + await stderr;
            if (child.ExitCode != 92) throw new InvalidDataException("Crash child missed cut: " + childOutput);
            await File.WriteAllTextAsync(Path.Combine(root, "child.log"), childOutput);
            var events = new FileEventStore(Path.Combine(root, "events"));
            var store = new FileArtifactStore(Path.Combine(root, "artifacts"), "test");
            var index = new FileCommandIndex(Path.Combine(root, "claims"));
            var fake = new DestinationTests.Fake();
            var app = Application(events, store, index, fake);
            var command = JsonSerializer.Deserialize<CommandEnvelope>(await File.ReadAllBytesAsync(Path.Combine(root, "confirm.json")), KernelJson.Options)!;
            if (startup) app.RecoverIncompleteCommands();
            var receipt = await app.HandleCommandAsync(command, default);
            DestinationTests.Check(receipt.Error?.Code == "process_interrupted" && receipt.Events.Last().CorrelationId == command.CommandId, cut + " claim not preview terminal " + startup);
            var replay = await app.HandleCommandAsync(command with { CommandId = "changed-command-id" }, default);
            DestinationTests.Check(JsonSerializer.Serialize(replay, KernelJson.Options) == JsonSerializer.Serialize(receipt, KernelJson.Options) && fake.Content == 0 && fake.Exports == 0, cut + " durable replay no worker rerun " + startup);
            var projection = await app.HandleQueryAsync(new(KernelSchemas.Query, "query", "operation.projection", null, "test", null, StrictJson.CloneElement(new { operationId = "read" })), default);
            var destinations = projection.Data.GetProperty("result").GetProperty("destinations").EnumerateArray().ToArray();
            DestinationTests.Check(projection.Data.GetProperty("state").GetString() == "Degraded" && destinations[0].GetProperty("state").GetString() == "succeeded" && destinations[1].GetProperty("state").GetString() == (cut == "before-start" ? "not_started" : "unknown"), cut + " separate durable destination states " + startup);
            DestinationTests.Check(destinations[1].GetProperty("externalWriteCount").ValueKind == (cut == "before-start" ? JsonValueKind.Number : JsonValueKind.Null), cut + " unknown count nullable " + startup);
            await File.WriteAllBytesAsync(Path.Combine(root, "recovery-receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { childExitCode = child.ExitCode, receipt, projection, realExternalCalls = 0 }, KernelJson.Options));
        }
    }
    private static KernelApplication Application(IEventStore events, FileArtifactStore store, FileCommandIndex index, IWorkerRuntime runtime)
    {
        var authority = new WorkerAuthorityBinding("authority", "registry.json", new string('a', 64), "worker.exe", new string('b', 64), "test", "1.0.0");
        var descriptors = new[] { "clip.read-aloud-cards", "remnote.discover-target" }.Select(id => DestinationTests.Capability(id) with { WorkerBinding = new WorkerBinding("worker", "test", "=test", "fake") { Authority = authority }, AuthorizationPolicy = id == "remnote.discover-target" ? "none" : "explicit" }).ToArray();
        var catalog = AdmittedCapabilityCatalog.CreateForTesting(new(KernelSchemas.CapabilityCatalog, "recovery-tests", "1.0.0", new("test", "checkpoint.json", new string('a', 64)), descriptors));
        return new(catalog, new OperationRegistry(new IOperationHandler[] { new ReadAloudCardsOperation(), new RemNoteSettingsOperation("remnote.discover-target") }), runtime, events, index, store);
    }
    private static async Task Submit(KernelApplication app, FileCommandIndex index, CommandEnvelope command)
    {
        await app.HandleCommandAsync(command, default);
        var result = await AwaitReceipt(index, command);
        if (result.Error is not null) throw new InvalidDataException(JsonSerializer.Serialize(result, KernelJson.Options));
    }
    private static async Task<CommandReceipt> AwaitReceipt(FileCommandIndex index, CommandEnvelope command)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (index.TryReadReceipt(command.IdempotencyKey) is null && DateTime.UtcNow < timeout) await Task.Delay(10);
        return index.TryReadReceipt(command.IdempotencyKey) ?? throw new TimeoutException("No durable receipt.");
    }
    private sealed class CrashFake(string cut) : IWorkerRuntime
    {
        private readonly DestinationTests.Fake inner = new();
        public WorkerBinding GetAdmittedBinding(string workerId)
        { if (workerId == "export.remnote" && inner.Exports > 0 && cut == "before-start") Environment.Exit(92); return inner.GetAdmittedBinding(workerId); }
        public WorkerRuntimeMetrics GetMetrics() => inner.GetMetrics();
        public Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken ct)
        { if (invocation.Request.GetProperty("phase").GetString() == "export" && invocation.Request.GetProperty("destination").GetString() == "remnote" && cut == "after-start") Environment.Exit(92); return inner.ExecuteAsync(invocation, ct); }
    }
    private sealed class CutStore(IEventStore inner, string cut) : IEventStore
    {
        public long Cursor => inner.Cursor;
        public long GetRevision(string id) => inner.GetRevision(id);
        public IReadOnlyList<KernelEvent> ReadStream(string id) => inner.ReadStream(id);
        public IReadOnlyDictionary<string, IReadOnlyList<KernelEvent>> ReadAllStreams() => inner.ReadAllStreams();
        public IReadOnlyList<KernelEvent> Append(string id, long expected, IReadOnlyList<KernelEvent> events)
        { if (cut == "before-terminal" && events.Any(x => x.EventType == "destination.terminal" && x.Data.GetProperty("kind").GetString() == "remnote")) Environment.Exit(92); return inner.Append(id, expected, events); }
    }
}

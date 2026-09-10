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
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

internal static class DestinationTests
{
    private static readonly List<string> Cases = new();
    internal static void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); Cases.Add(name); Console.WriteLine("PASS " + name); }
    public static async Task<int> RunAsync(string root, bool recovery = true)
    {
        if (Directory.Exists(root)) throw new InvalidDataException("Evidence root exists.");
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new Fake();
            var events = new FileEventStore(Path.Combine(root, "events"));
            var store = new FileArtifactStore(Path.Combine(root, "artifacts"), "test");
            var context = new OperationContext(Capability("clip.read-aloud-cards"), runtime, store, events);
            var discovery = await Execute(Command("discover", "remnote.discover-target", "kc.remnote-discovery/1", new { rootTitle = "Fixture root" }), new RemNoteSettingsOperation("remnote.discover-target"), context);
            var binding = discovery.Data.GetProperty("discovery").GetProperty("binding").Deserialize<KnowledgeClipper.Core.V3.Contracts.RemNoteBindingReference>(KernelJson.Options)!;
            await RemNoteSettingsOperation.VerifyBindingAsync(binding, context, default);
            Check(discovery.Data.GetProperty("discovery").GetProperty("persistableBinding").GetBoolean(), "discovery unique evidence verified");
            await Reject(() => RemNoteSettingsOperation.VerifyBindingAsync(binding with { DiscoveryOperationId = "other" }, context, default), "other operation plus real token rejected");
            await Reject(() => RemNoteSettingsOperation.VerifyBindingAsync(binding with { RootRemId = "other" }, context, default), "binding target drift rejected");
            var remnote = await Publish("remnote-settings", "remnote-destination-settings.v1", new { schemaVersion = "remnote-destination-settings.v1", enabled = true,
                target = new { rootRemId = "root-fixture", rootTitle = "Fixture root" }, advanced = new { sink = "mcp-direct", clipRouting = "clip-daily-route", cardRouting = "card-export", timeZone = "Asia/Shanghai", dateFormat = "yyyy-MM-dd" } });
            var anki = await Publish("anki-export-config", "kc.anki-export-config/1", new { schema_version = "anki-export-config.v1", provision_managed = false, deck_routes = new { canary = "零碎知识剪藏::Canary::Offline" } });
            var capture = await DomainArtifactFactory.PublishTextAsync(store, Command("capture", "artifact.publish", "kc.artifact-publish-command/1", new { }), "capture-text", "1.0.0", "kc.capture-text/1", "Immutable synthetic source.", Array.Empty<ArtifactReference>(), default);
            var options = new[] { new KnowledgeClipper.Core.V3.Contracts.DestinationSelection("anki", anki, "canary", null), new("remnote", remnote, null, binding) };
            foreach (var selections in new[] { Array.Empty<KnowledgeClipper.Core.V3.Contracts.DestinationSelection>(), options[..1], options[1..], options })
            {
                var name = "combination-" + string.Join('-', selections.Select(x => x.Kind));
                var read = Command(name, "clip.read-aloud-cards", "kc.read-aloud-cards-command/1", new { destinationMode = "confirmed-cards", capture, destinations = selections, includeSpeech = false }, new[] { capture }.Concat(CardDestinations.Inputs(selections)).ToArray());
                var beforeContent = runtime.Content;
                var draft = await Execute(read, new ReadAloudCardsOperation(), context);
                Check(runtime.Content == beforeContent + 1 && draft.Artifacts.Count(x => x.Kind == "destination-snapshot") == 1, name + " freezes one snapshot and reviews once");
                var snapshot = draft.Artifacts.Single(x => x.Kind == "destination-snapshot");
                var frozen = await CardDestinations.LoadAsync(snapshot, context, default);
                Check(frozen.Destinations.Count == selections.Length, name + " exact frozen selection including empty");
                var grants = frozen.Destinations.Select(x => new DestinationWriteGrant(x.Kind, "user-confirm", x.Kind == "anki" ? "isolated-canary-deck" : "confirmed-remnote-root", x.TargetIdentity)).ToArray();
                var confirm = Command(name + "-confirm", read.Operation, read.Body.Schema, new { destinationMode = "confirmed-cards", action = "confirm_card_draft", sourceOperationId = read.OperationId,
                    draft = draft.Data.GetProperty("cardDraft"), destinationSnapshot = snapshot, grants }) with { OperationId = read.OperationId };
                var beforeExports = runtime.Exports;
                var completed = await Execute(confirm, new ReadAloudCardsOperation(), context);
                Check(runtime.Content == beforeContent + 1 && runtime.Exports == beforeExports + selections.Length && completed.Data.GetProperty("pipeline").GetString() == "completed", name + " confirms without extra content");
                var replay = await CardDestinations.ConfirmAsync(confirm with { CommandId = confirm.CommandId + "-replay" }, context, draft.Data, draft.Artifacts, default);
                Check(runtime.Exports == beforeExports + selections.Length && replay.Data.GetProperty("destinations").GetArrayLength() == selections.Length, name + " changed command id cannot duplicate exports");
                await Reject(() => CardDestinations.ConfirmAsync(confirm with { OperationId = "cross-task" }, context, draft.Data, draft.Artifacts, default), name + " cross operation denied");
                if (selections.Length == 2) Check(runtime.CardHashes.TakeLast(2).Distinct().Count() == 1, "two destinations consume the same immutable card hash");
            }
            foreach (var selected in new[] { Array.Empty<KnowledgeClipper.Core.V3.Contracts.DestinationSelection>(), options[..1], options[1..], options })
            {
                var id = "question-" + selected.Length + "-" + string.Join('-', selected.Select(x => x.Kind));
                var question = await DomainArtifactFactory.PublishTextAsync(store, Command(id + "-text", "artifact.publish", "test", new { }), "question", "1.0.0", "kc.question/1", "Question source", Array.Empty<ArtifactReference>(), default);
                var initialCommand = Command(id, "question.initial", DestinationSchemas.Command, new { destinationMode = "confirmed-cards", sessionId = id, question, context = Array.Empty<ArtifactReference>(), destinations = selected }, new[] { question }.Concat(CardDestinations.Inputs(selected)).ToArray());
                var before = runtime.Exports;
                var initial = await Execute(initialCommand, new QuestionWorkflowOperation("question.initial"), context);
                var frozen = initial.Artifacts.Single(x => x.Kind == "destination-snapshot");
                var followText = await DomainArtifactFactory.PublishTextAsync(store, Command(id + "-follow-text", "artifact.publish", "test", new { }), "question-follow-up", "1.0.0", "kc.question-follow-up/1", "Follow-up source", new[] { question }, default);
                var follow = await Execute(Command(id + "-follow", "question.follow-up", DestinationSchemas.Command, new { destinationMode = "confirmed-cards", sessionId = id, followUp = followText }, new[] { followText }), new QuestionWorkflowOperation("question.follow-up"), context);
                var answer = follow.Artifacts.Single(x => x.Kind == "question-answer");
                var make = await Execute(Command(id + "-make", "question.make-cards", DestinationSchemas.Command, new { destinationMode = "confirmed-cards", sessionId = id, answer }, new[] { answer }), new QuestionWorkflowOperation("question.make-cards"), context);
                Check(initial.Artifacts.Contains(frozen) && follow.Artifacts.Contains(frozen) && make.Artifacts.Contains(frozen) && runtime.Exports == before, id + " initial-followup-makecards inherit exact frozen selection and never export");
                var stored = await CardDestinations.LoadAsync(frozen, context, default);
                Check(stored.Destinations.Count == selected.Length, id + " empty remains empty not legacy Anki");
            }
            var catalog = AdmittedCapabilityCatalog.CreateForTesting(new(KernelSchemas.CapabilityCatalog, "destination-tests", "1.0.0", new("test", "checkpoint.json", new string('a', 64)),
                new[] { Capability("remnote.discover-target"), Capability("remnote.probe"), Capability("artifact.publish"), Capability("clip.read-aloud-cards") with {
                    WorkerBinding = new WorkerBinding("worker", "content.deepseek", "=test", null) { Authority = new("authority", "registry.json", new string('a', 64), "worker.exe", new string('b', 64), "test", "1.0.0") } } }));
            var registry = new OperationRegistry(new IOperationHandler[] { new RemNoteSettingsOperation("remnote.discover-target"), new RemNoteSettingsOperation("remnote.probe"), new ArtifactPublishOperation(), new ReadAloudCardsOperation() });
            var application = new KernelApplication(catalog, registry, runtime, new FileEventStore(Path.Combine(root, "events")), new FileCommandIndex(Path.Combine(root, "claims")), new FileArtifactStore(Path.Combine(root, "artifacts"), "test"));
            using var stop = new CancellationTokenSource();
            var pipe = "KC.Destinations." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeKernelServer(pipe, new KernelCompatibilityGateway(application, root));
            var serving = server.ServeAsync(stop.Token);
            try
            {
                var client = new CoreV3Client(pipe);
                var restored = await client.RestoreRemNoteBindingAsync(new("discover", new TypedArtifactHandle(binding.Evidence).Token));
                Check(restored.Status == "verified_historical" && restored.Binding!.RootRemId == "root-fixture", "SDK restore from reopened durable stores");
                Check((await client.RestoreRemNoteBindingAsync(new("discover", new string('f', 64)))).Binding is null, "SDK restore token mismatch denied");
                var snapshotRef = events.ReadStream("combination-anki-remnote").Last(x => x.EventType == "operation.completed").Artifacts.Single(x => x.Kind == "destination-snapshot");
                var frozenTargets = await client.GetFrozenDestinationsAsync(new("combination-anki-remnote", new TypedArtifactHandle(snapshotRef).Token));
                Check(frozenTargets.Destinations.Count == 2 && frozenTargets.Destinations[0].TargetIdentity == "零碎知识剪藏::Canary::Offline" && frozenTargets.Destinations[1].RemNoteBinding?.RootRemId == "root-fixture", "SDK restores typed frozen targets without current profile or Anki fallback");
                var readProjection = await client.GetReadAloudCardsProjectionAsync("combination-anki-remnote");
                Check(readProjection.DestinationSnapshot?.Token == new TypedArtifactHandle(snapshotRef).Token && readProjection.Destinations.Count == 2, "same read projection returns frozen snapshot and per-destination evidence");
                var rawProjection = await client.QueryProtocolAsync(new(KernelSchemas.Query, "projection-neutral", "read-aloud-cards.projection", null, "read-your-writes", null, StrictJson.CloneElement(new { operationId = "combination-anki-remnote" })), default);
                Check(rawProjection.ProjectionVersion == 5, "neutral read projection uses additive version not closed legacy v4");
                await File.WriteAllBytesAsync(Path.Combine(root, "read-projection.v5.json"), JsonSerializer.SerializeToUtf8Bytes(rawProjection.Data, KernelJson.Options));
                var notFound = await client.QueryProtocolAsync(new(KernelSchemas.Query, "projection-legacy", "read-aloud-cards.projection", null, "read-your-writes", null, StrictJson.CloneElement(new { operationId = "missing-legacy-operation" })), default);
                Check(notFound.ProjectionVersion == 4 && !notFound.Data.TryGetProperty("destinations", out _), "legacy read projection remains closed v4");
                await File.WriteAllBytesAsync(Path.Combine(root, "read-projection.v4.json"), JsonSerializer.SerializeToUtf8Bytes(notFound.Data, KernelJson.Options));
                var project = await client.ReadCardsAsync(new("sdk-confirm-both", new(capture), new CardDestinationSelection[] { new AnkiSelection(new(anki)), new RemNoteSelection(new(remnote), restored.Binding!) }, IncludeSpeech: false));
                var preview = await Wait(project);
                var targets = await client.GetFrozenDestinationsAsync(new(project.OperationId, preview.DestinationSnapshot!.Token));
                Check(preview.Destinations.All(x => x.ConfigurationSnapshot.Token == preview.DestinationSnapshot.Token) && targets.Destinations.All(x => x.Configuration.Token != preview.DestinationSnapshot.Token), "actual SDK preview distinguishes overall snapshot from per-destination config handles");
                var count = runtime.Exports; var contentCount = runtime.Content;
                var confirm = await client.ConfirmCardsAsync(new("sdk-confirm-both-action", project.OperationId, preview.CardDraft!, preview.DestinationSnapshot,
                    new CardDestinationAuthorization[] { new AnkiCardAuthorization(new("offline-confirm"), "零碎知识剪藏::Canary::Offline"), new RemNoteCardAuthorization("offline-confirm", "root-fixture") }));
                var confirmed = await Wait(confirm);
                Check(confirmed.Destinations.Count == 2 && confirmed.Destinations.All(x => x.State == "succeeded" && x.ConfigurationSnapshot.Token == preview.DestinationSnapshot.Token) && runtime.Exports == count + 2 && runtime.Content == contentCount, "actual SDK project-to-confirm exports identical draft through both Fake destinations without Content rerun");

                async Task<ReadAloudCardsProjectionSnapshot> Wait(OperationSubmission submission)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < deadline)
                    {
                        var value = await client.GetReadAloudCardsProjectionAsync(submission.OperationId);
                        if (value.TerminalCommandId == submission.CommandId) return value;
                        await Task.Delay(10);
                    }
                    throw new TimeoutException("SDK confirmation did not terminate.");
                }
            }
            finally { stop.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
            if (recovery) await DestinationRecoveryTests.RunAsync(Path.Combine(root, "recovery"));
            if (!recovery) await DestinationActionTests.RunAsync(Path.Combine(root, "actions"), store, events, capture, options);
            await File.WriteAllBytesAsync(Path.Combine(root, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { status = "PASS", cases = Cases, externalCalls = 0, realWorkerRuns = 0 }, KernelJson.Options));
            return 0;
            Task<ArtifactReference> Publish(string kind, string schema, object value) => DomainArtifactFactory.PublishJsonAsync(store, Command(kind, "artifact.publish", schema, new { }), kind, "1.0.0", schema, value, Array.Empty<ArtifactReference>(), default);
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
    internal static async Task Reject(Func<Task> action, string name)
    { try { await action(); } catch (InvalidDataException) { Check(true, name); return; } throw new InvalidDataException("Expected rejection: " + name); }
    internal static CommandEnvelope Command(string id, string op, string schema, object body, IReadOnlyList<ArtifactReference>? inputs = null)
        => new(KernelSchemas.Command, "command-" + id, id, op, new("task", id), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))), null, null, inputs ?? Array.Empty<ArtifactReference>(), new(StrictJson.CloneElement(body).TryGetProperty("destinationMode", out _) ? DestinationSchemas.Command : schema, StrictJson.CloneElement(body)));
    internal static CapabilityDescriptor Capability(string id) => new(id, true, null, "test", "test", "1.0.0", "external-write", "explicit", "never", "required", 10000, new("in-process", null, null, id), new("test", "sdk-only", false));
    internal static async Task<OperationExecutionResult> Execute(CommandEnvelope command, IOperationHandler handler, OperationContext context)
    {
        var start = new KernelEvent(KernelSchemas.Event, "evt-" + Guid.NewGuid().ToString("N"), command.OperationId, 0, "command.accepted", 1, DateTimeOffset.UtcNow, command.CommandId, command.CommandId, command.Operation, "external-write", StrictJson.CloneElement(new { }), Array.Empty<ArtifactReference>());
        context.EventStore.Append(command.OperationId, context.EventStore.GetRevision(command.OperationId), new[] { start });
        var result = await handler.ExecuteAsync(command, context, default);
        context.EventStore.Append(command.OperationId, context.EventStore.GetRevision(command.OperationId), new[] { start with { EventId = "evt-" + Guid.NewGuid().ToString("N"), EventType = "operation.completed", Data = StrictJson.CloneElement(new { result = result.Data }), Artifacts = result.Artifacts } });
        return result;
    }
    internal sealed class Fake : IWorkerRuntime
    {
        public int Content, Exports;
        public List<string> CardHashes = new();
        public WorkerBinding GetAdmittedBinding(string workerId) => new("in-process", workerId, "test", "fake");
        public WorkerRuntimeMetrics GetMetrics() => new(Content, 0, Exports, Exports, Exports, 0, 0, 0, "fake");
        public Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
        {
            var phase = invocation.Request.GetProperty("phase").GetString(); object result; var writes = 0;
            if (phase == "discover-target") result = new { schemaVersion = "remnote-target-discovery-result.v1", discoveryId = invocation.OperationId, worker = new { id = "export.remnote" },
                state = "healthy", admission = "ready", externalWriteCount = 0, sanitized = true, fixedSemantics = new { automaticRetryAllowed = false }, reasonCodes = Array.Empty<string>(),
                connection = new { connected = true, acceptWriteOperations = true }, selection = new { selector = new { rootTitle = "Fixture root", expectedType = "document", match = "exact", uniqueness = "required" },
                    candidateCount = 1, resolution = "unique", resolvedRootRemId = "root-fixture", observedTitle = "Fixture root", observedType = "document", exactReadback = true, persistableBinding = true } };
            else if (phase == "probe") result = new { schemaVersion = "remnote-public-probe-result.v2", probeId = invocation.OperationId, worker = new { id = "export.remnote" }, state = "healthy", admission = "ready", externalWriteCount = 0, sanitized = true,
                fixedSemantics = new { automaticRetryAllowed = false }, reasonCodes = Array.Empty<string>(), connection = new { connected = true, acceptWriteOperations = true },
                targetBinding = new { rootRemId = "root-fixture", expectedTitle = "Fixture root", observedTitle = "Fixture root", observedType = "document", matched = true } };
            else if (phase == "export") { Exports++; CardHashes.Add(invocation.Request.GetProperty("cardBundleSha256").GetString()!); writes = 1; result = new { exactReadback = true, status = "completed" }; }
            else { Content++; result = new { status = "cards_ready", answerText = "Answer with provenance", cleanedText = "Immutable synthetic source.", cards = new[] { new { cardId = "card-1", front = "Question", back = "Answer", template = "basic_qa" } } }; }
            return Task.FromResult(new WorkerExecutionResult("completed", StrictJson.CloneElement(result), Array.Empty<ArtifactReference>(), writes, true));
        }
    }
}

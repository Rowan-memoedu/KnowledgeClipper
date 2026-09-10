using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.CompatibilityGateway;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.Transport;

internal static class DestinationActionTests
{
    public static async Task RunAsync(string root, FileArtifactStore store, FileEventStore events, ArtifactReference capture,
        IReadOnlyList<KnowledgeClipper.Core.V3.Contracts.DestinationSelection> options)
    {
        foreach (var mode in new[] { "choice", "correction", "cancel" })
        {
            var runtime = new ReadAloudCardsFakeRuntime(unresolvedContent: mode != "choice");
            var authority = new WorkerAuthorityBinding("authority", "registry.json", new string('a', 64), "worker.exe", new string('b', 64), "test", "1.0.0");
            var capability = DestinationTests.Capability("clip.read-aloud-cards") with { WorkerBinding = new WorkerBinding("worker", "content.deepseek", "=test", null) { Authority = authority } };
            var catalog = AdmittedCapabilityCatalog.CreateForTesting(new(KernelSchemas.CapabilityCatalog, "offline-actions", "1.0.0", new("test", "checkpoint", new string('a', 64)), new[] { capability }));
            var app = new KernelApplication(catalog, new(new IOperationHandler[] { new ReadAloudCardsOperation() }), runtime, events, new FileCommandIndex(Path.Combine(root, mode, "claims")), store);
            using var stop = new CancellationTokenSource();
            var pipe = "KC.DestinationActions." + Guid.NewGuid().ToString("N");
            var serving = new NamedPipeKernelServer(pipe, new KernelCompatibilityGateway(app, root)).ServeAsync(stop.Token);
            try
            {
                var client = new CoreV3Client(pipe);
                foreach (var selected in new[] { Array.Empty<KnowledgeClipper.Core.V3.Contracts.DestinationSelection>(), options.ToArray() })
                {
                    var id = "action-" + mode + "-" + selected.Length;
                    var targets = selected.Select(x => x.Kind == "anki" ? (CardDestinationSelection)new AnkiSelection(new(x.Configuration), x.RouteKey!) : new RemNoteSelection(new(x.Configuration), new VerifiedRemNoteBinding(x.Binding!))).ToArray();
                    var initial = await client.ReadCardsAsync(new(id, new(capture), targets, new("frozen-settings", mode == "choice" ? "ask" : "both")));
                    var before = await Wait(client, initial);
                    var token = before.DestinationSnapshot!.Token;
                    var calls = runtime.ContentAttempts;
                    OperationSubmission resolved;
                    if (mode == "choice")
                    {
                        var invalid = await client.ChooseDestinationLanguageCardsAsync(new(id + "-bad", initial.OperationId, "invalid-token", "both"));
                        var rejected = await Wait(client, invalid);
                        DestinationTests.Check(rejected.ActionState == ReadAloudCardsActionState.Failed && runtime.ContentAttempts == calls, id + " actual SDK stale choice rejected before worker");
                        resolved = await client.ChooseDestinationLanguageCardsAsync(new(id + "-choose", initial.OperationId, before.LanguageCardChoice!.TargetToken, "both"));
                    }
                    else
                    {
                        var prompt = before.CardResolution!;
                        var invalid = await client.ResolveDestinationCardsAsync(new(id + "-bad", initial.OperationId, "invalid-token", prompt.DecisionId, "cancel"));
                        var rejected = await Wait(client, invalid);
                        DestinationTests.Check(rejected.ActionState == ReadAloudCardsActionState.Failed && runtime.ContentAttempts == calls, id + " actual SDK stale resolution rejected before worker");
                        resolved = await client.ResolveDestinationCardsAsync(new(id + "-resolve", initial.OperationId, prompt.TargetToken, prompt.DecisionId, mode == "cancel" ? "cancel" : "user_correction", mode == "cancel" ? null : "user supplied correction"));
                    }
                    var after = await Wait(client, resolved);
                    DestinationTests.Check(after.DestinationSnapshot?.Token == token && runtime.AnkiAttempts == 0 && after.Destinations.All(x => x.State == "not_started"), id + " neutral action retains exact destinations without exporter");
                    if (mode != "cancel")
                    {
                        DestinationTests.Check(after.CardDraft is not null, id + " creates only new draft");
                        var contentCalls = runtime.ContentAttempts; var playbackCalls = runtime.PlaybackAttempts;
                        var replay = await client.ReplayDestinationReadAsync(new(id + "-replay", initial.OperationId, after.TerminalSequence));
                        var replayed = await Wait(client, replay);
                        DestinationTests.Check(replayed.DestinationSnapshot?.Token == token && runtime.ContentAttempts == contentCalls && runtime.PlaybackAttempts == playbackCalls + 1 && runtime.AnkiAttempts == 0, id + " revision-bound replay affects only Fake playback");
                        var cancelled = await client.CancelDestinationCardDraftAsync(new(id + "-cancel-draft", initial.OperationId, replayed.CardDraft!));
                        var cancellation = await Wait(client, cancelled);
                        DestinationTests.Check(cancellation.DestinationSnapshot?.Token == token && cancellation.Pipeline == ReadAloudCardsPipelineState.Cancelled && runtime.ContentAttempts == contentCalls && runtime.AnkiAttempts == 0, id + " typed neutral draft cancellation preserves destinations and performs no worker call");
                    }
                    else DestinationTests.Check(runtime.ContentAttempts == calls, id + " cancel performs no Content call");
                }
            }
            finally { stop.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
        }
    }
    private static async Task<ReadAloudCardsProjectionSnapshot> Wait(CoreV3Client client, OperationSubmission submission)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var state = await client.GetReadAloudCardsProjectionAsync(submission.OperationId);
            if (state.TerminalCommandId == submission.CommandId) return state;
            await Task.Delay(10);
        }
        throw new TimeoutException("Destination action did not reach its own terminal.");
    }
}

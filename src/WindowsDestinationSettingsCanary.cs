using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.CoreGateway.Tests;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        private static int DestinationInputsOnly(string root)
        {
            if (Directory.Exists(root)) throw new Exception("immutable-evidence-exists");
            Directory.CreateDirectory(root);
            DestinationInputsAsync().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { status = "PASS-DESTINATION-INPUT-FAKE-ONLY", assertions,
                externalCalls = 0, workerRuns = 0, uiExercised = false, productionTaskWired = false,
                boundary = "settings publisher and typed question lifetime only; confirmation/presentation await SDK03 binding" }));
            Console.WriteLine("DESTINATION_INPUTS PASS assertions=" + assertions); return 0;
        }

        private static async Task DestinationInputsAsync()
        {
            foreach (var choices in new[] { Array.Empty<string>(), new[] { "anki" }, new[] { "remnote" }, new[] { "remnote", "anki" } })
            {
                var fake = new RemNoteSettingsFixture();
                var binding = (await fake.DiscoverRemNoteAsync(new("fixture-discover", "Saved"), CancellationToken.None)).Binding;
                var remnote = new RemNoteDestinationSettings("remnote-destination-settings.v1", choices.Contains("remnote"), new(binding.RootRemId, binding.RootTitle), new("mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd"));
                var document = new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v3", "profile",
                    new Dictionary<string, WorkflowSettingsProfile> { ["profile"] = new("Profile", new("cards", choices), Remnote: remnote) });
                var modules = new DesktopModuleSettingsDocument();
                modules.RemNoteBindings["profile"] = new(remnote, CoreV3Client.ExportRemNoteBinding(binding));
                var workflows = 0; var moduleReads = 0; var ankiBuilds = 0; var published = new List<ArtifactPurpose>();
                var publisher = new DesktopDestinationSettingsPublisher(() => { workflows++; return document; }, () => { moduleReads++; return modules; },
                    _ => { ankiBuilds++; return "{\"deck_routes\":{\"canary\":\"零碎知识剪藏::Canary::fixture\"},\"provision_managed\":false}"; });
                Task<TypedArtifactHandle> Publish(ArtifactPurpose purpose, string text, CancellationToken ct)
                { published.Add(purpose); return Task.FromResult(binding.Evidence); }
                var frozen = await publisher.FreezeAsync(Publish, fake.RestoreRemNoteAsync, CancellationToken.None);
                Assert(frozen.Count == choices.Length && frozen.Select(x => x is AnkiSelection ? "anki" : "remnote").SequenceEqual(choices), "publisher preserves exact selected destination set and order: " + string.Join(",", choices));
                Assert(ankiBuilds == (choices.Contains("anki") ? 1 : 0) && published.Count(x => x == ArtifactPurpose.AnkiConfiguration) == ankiBuilds,
                    "none/RemNote-only never build or publish an Anki configuration");
                Assert(fake.Restores == (choices.Contains("remnote") ? 1 : 0) && workflows == 1 && moduleReads == (choices.Length == 0 ? 0 : 1), "new task reads settings once and restores only selected historical RemNote evidence");

                var freezes = 0; var followUps = 0; var madeCards = 0;
                DestinationQuestionInitialRequest actual = null;
                var currentSelections = frozen;
                var bridge = new DestinationQuestionSessionSdkBridge(ct => { freezes++; return Task.FromResult(currentSelections); },
                    (request, ct) => { actual = request; return Task.FromResult(new QuestionOperationSubmission("question-1", "session", 1, true)); },
                    (request, ct) => { followUps++; return Task.FromResult(new QuestionOperationSubmission("question-2", request.SessionId, 2, true)); },
                    (request, ct) => { madeCards++; return Task.FromResult(new QuestionOperationSubmission("question-3", request.SessionId, 3, true)); });
                await bridge.InitialAsync("initial", binding.Evidence, Array.Empty<TypedArtifactHandle>(), CancellationToken.None);
                currentSelections = Array.Empty<CardDestinationSelection>();
                await bridge.FollowUpAsync("follow-up", "session", binding.Evidence, CancellationToken.None);
                await bridge.MakeCardsAsync("make-cards", "session", binding.Evidence, CancellationToken.None);
                Assert(actual.Destinations.SequenceEqual(frozen) && bridge.SubmittedDestinations("question-3").SequenceEqual(frozen) && freezes == 1 && followUps == 1 && madeCards == 1,
                    "typed initial/follow-up/make-cards preserve original destinations after current settings change");
                foreach (var type in new[] { typeof(DestinationQuestionInitialRequest), typeof(DestinationQuestionFollowUpRequest), typeof(DestinationQuestionMakeCardsRequest) })
                    Assert(type.GetProperties().All(p => !p.Name.Contains("Authorization", StringComparison.Ordinal)), "public " + type.Name + " cannot carry premature write grants");
                try { await bridge.FollowUpAsync("unbound", "different-session", binding.Evidence, CancellationToken.None); throw new Exception("unexpected session accepted"); }
                catch (InvalidOperationException) { Assert(followUps == 1, "unbound session fails before public follow-up dispatch"); }
                if (choices.Contains("remnote"))
                {
                    modules.RemNoteBindings["profile"] = modules.RemNoteBindings["profile"] with { Settings = remnote with { Target = new("wrong-id", "wrong-title") } };
                    var count = published.Count;
                    try { await publisher.FreezeAsync(Publish, fake.RestoreRemNoteAsync, CancellationToken.None); throw new Exception("mismatched pair accepted"); }
                    catch (InvalidOperationException) { Assert(published.Count == count, "mismatched saved profile/evidence pair cannot become any next-task configuration"); }
                }
            }
        }
    }
}

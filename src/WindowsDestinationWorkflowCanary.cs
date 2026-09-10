using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.CoreGateway.Tests;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        private static int DestinationWorkflowOnly(string root)
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("immutable-evidence-exists");
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (var choices in new[] { Array.Empty<string>(), new[] { "anki" }, new[] { "remnote" }, new[] { "anki", "remnote" } })
                foreach (var mode in new[] { "clip", "read", "question" }) DestinationWorkflowCase(root, choices, mode);
            DestinationWorkflowNegatives();
            File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { status = "PASS-DESTINATION-PRODUCTION-GATEWAY-UI-FAKE", assertions,
                inputSets = new[] { "none", "anki", "remnote", "anki+remnote" }, flows = new[] { "clip", "read", "question/follow-up/make-cards" },
                publicSdkCallsInjected = true, productionPublisherGatewayFacadePreviewExercised = true, namedPipeTransportExercised = false,
                externalCalls = 0, workerRuns = 0, physicalInput = 0, physicalAudio = 0, candidateModified = false }));
            Console.WriteLine("DESTINATION_WORKFLOW_UI PASS assertions=" + assertions);
            return 0;
        }

        private static void DestinationWorkflowCase(string root, string[] selected, string mode)
        {
            var f = new DestinationWorkflowFixture(selected);
            var gateway = f.Publisher.CreateGateway("unused-offline", () => new SdkLanguageCardSettings("saved", "ask"), f.Calls);
            var ct = CancellationToken.None;
            string operation;
            FlashcardDraftProjection projection;
            if (mode == "question")
            {
                var first = gateway.SubmitQuestionAsync(new("解释惯性", false, null), ct).GetAwaiter().GetResult();
                Assert(first.Accepted, "question initial accepted through production publisher/gateway/facade");
                f.SelectNone();
                var follow = gateway.SubmitQuestionAsync(new("给一个日常例子", true, first.SessionId), ct).GetAwaiter().GetResult();
                var answered = gateway.GetQuestionAsync(follow.TaskId, ct).GetAwaiter().GetResult();
                var text = gateway.ReadAsync<QuestionAnswerContent>(answered.Answer, ct).GetAwaiter().GetResult();
                Assert(text.Text == "惯性是物体保持原有运动状态的性质。", "actual answer artifact reaches question UI model without export");
                var cards = gateway.MakeCardsAsync(new(first.SessionId, null, answered.Answer), ct).GetAwaiter().GetResult();
                operation = cards.TaskId;
                projection = gateway.GetQuestionAsync(operation, ct).GetAwaiter().GetResult().FlashcardDraft;
                Assert(f.Initials == 1 && f.FollowUps == 1 && f.MakeCards == 1 && f.Confirms == 0, "initial/follow-up/make-cards carry no grants and no export");
            }
            else
            {
                var capture = gateway.PublishCaptureTextAsync(new("物体具有惯性"), ct).GetAwaiter().GetResult();
                var submit = gateway.SubmitAsync(new(capture, mode == "read"), ct).GetAwaiter().GetResult();
                Assert(submit.Accepted && f.LastRead.IncludeSpeech == (mode == "read"), "clip/read share neutral task path and preserve speech choice");
                operation = submit.OperationId;
                f.SelectNone();
                projection = gateway.GetProjectionAsync(operation, ct).GetAwaiter().GetResult().FlashcardDraft;
            }
            Assert(f.WorkflowReads == 1 && f.AnkiBuilds == (selected.Contains("anki") ? 1 : 0), "targets freeze once; none/RemNote-only build zero Anki configuration");
            Assert(f.Purposes.Count(x => x == ArtifactPurpose.AnkiConfiguration) == (selected.Contains("anki") ? 1 : 0), "only selected destination configuration is published");
            Assert(projection.Draft.Destinations.SequenceEqual(selected.Select(x => x == "anki" ? "Anki" : "RemNote"))
                && projection.Draft.DestinationSnapshot != null && projection.Draft.DestinationOutcomes.All(x => x.State == "not_started" && x.ExternalWriteCount == 0), "same card preview shows all and only frozen targets, not_started before confirmation");
            var binding = FlashcardDraftConfirmation.From(projection.Draft);
            var wrong = gateway.ConfirmAsync(binding with { DestinationSnapshot = new ArtifactHandle("wrong", "destination-snapshot") }, ct).GetAwaiter().GetResult();
            Assert(!wrong.Accepted && f.Confirms == 0, "wrong snapshot cannot authorize any write");
            var stale = gateway.ConfirmAsync(binding with { Revision = "stale" }, ct).GetAwaiter().GetResult();
            Assert(!stale.Accepted && f.Confirms == 0, "stale draft cannot authorize any write");

            var useCases = new FlashcardDraftPreviewUseCases(gateway);
            useCases.Project(projection);
            using var preview = new FlashcardPreviewForm(async () => { await useCases.ConfirmAsync(ct); }, async () => { await useCases.CancelAsync(ct); }, _ => Task.CompletedTask);
            useCases.Changed += preview.Render;
            preview.Render(useCases.Current); preview.Show(); Pump(20);
            Assert(Find<TextBox>(preview, FlashcardPreviewAutomation.Front).Text == "什么是惯性？" && Find<TextBox>(preview, FlashcardPreviewAutomation.Back).Text == "保持原有运动状态的性质。", "one actual preview renders the same front/back for every destination set");
            var label = Find<Label>(preview, FlashcardPreviewAutomation.Destinations).Text;
            Assert(selected.Length == 0 ? label == "目标：无" : selected.All(x => label.Contains(x == "anki" ? "Anki" : "RemNote")), "visible target label equals frozen selection");
            Find<Button>(preview, FlashcardPreviewAutomation.Confirm).PerformClick(); Pump(30);
            Assert(f.Confirms == 1 && f.LastConfirm.Authorizations.Count == selected.Length
                && f.LastConfirm.Authorizations.OfType<AnkiCardAuthorization>().Count() == (selected.Contains("anki") ? 1 : 0)
                && f.LastConfirm.Authorizations.OfType<RemNoteCardAuthorization>().Count() == (selected.Contains("remnote") ? 1 : 0), "one explicit preview click generates exactly the frozen destination grants");
            Assert(f.LastConfirm.Draft.Token == binding.DraftToken && f.LastConfirm.DestinationSnapshot.Token == binding.DestinationSnapshot.Id && f.WorkflowReads == 1, "confirmation preserves rendered draft/snapshot and never reads new settings");
            var terminal = gateway.GetAsync(operation, ct).GetAwaiter().GetResult();
            useCases.Project(terminal); Pump(20);
            Assert(terminal.Status == FlashcardDraftStatus.Completed && terminal.Draft.DestinationOutcomes.All(x => x.State == "succeeded" && x.Evidence != null), "typed per-target terminal outcomes and evidence reach the same preview");
            Assert(!Find<Button>(preview, FlashcardPreviewAutomation.Confirm).Enabled, "completed preview cannot send a second confirmation");
            gateway.ConfirmAsync(binding, ct).GetAwaiter().GetResult();
            Assert(f.Confirms == 1, "duplicate gateway confirmation reuses original acknowledgement; does not submit another write");
            if (selected.Length > 0)
            {
                f.Unknown = true;
                terminal = gateway.GetAsync(operation, ct).GetAwaiter().GetResult(); useCases.Project(terminal); Pump(20);
                Assert(terminal.Status == FlashcardDraftStatus.Unknown && terminal.Draft.DestinationOutcomes.Last().ExternalWriteCount == null
                    && !terminal.Draft.DestinationOutcomes.Last().ExternalStateKnown, "unknown destination never becomes zero writes or overall success");
                Assert(Find<TextBox>(preview, FlashcardPreviewAutomation.DestinationResults).Text.Contains("未知") && !Find<Button>(preview, FlashcardPreviewAutomation.Confirm).Enabled, "actual UI renders unknown destination and forbids blind rewrite");
            }
            if (mode != "question")
            {
                gateway.ResumeLanguageChoiceAsync(new(operation, LanguagePracticeChoice.Both), ct).GetAwaiter().GetResult();
                gateway.ResolveCardAsync(new(operation, "accept_original", null, null), ct).GetAwaiter().GetResult();
                gateway.ReplayAsync(operation, ct).GetAwaiter().GetResult();
                Assert(f.Choices == 1 && f.Resolutions == 1 && f.Replays == 1 && f.LastReplay.OperationId == operation && f.LastReplay.SourceRevision > 0
                    && f.WorkflowReads == 1 && f.Confirms == 1, "neutral language choice and replay keep owning task/revision and never add export grants");
            }
            var caseName = mode + "-" + (selected.Length == 0 ? "none" : string.Join("-", selected));
            Assert(preview.Controls[0].Width <= preview.ClientSize.Width && Find<Button>(preview, FlashcardPreviewAutomation.Confirm).Right <= Find<Button>(preview, FlashcardPreviewAutomation.Confirm).Parent.ClientSize.Width, "results do not expand preview outside its client area");
            using (var image = new System.Drawing.Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, preview.Width, preview.Height)); image.Save(Path.Combine(root, caseName + ".png")); }
            File.WriteAllText(Path.Combine(root, caseName + ".json"), JsonSerializer.Serialize(new { input = mode == "question" ? "解释惯性 → 给一个日常例子 → 制卡" : "物体具有惯性", selected, settingsChangedAfterSubmit = true,
                preview = projection.Draft, destinationSnapshot = projection.Draft.DestinationSnapshot,
                beforeOutcomes = projection.Draft.DestinationOutcomes, result = terminal, afterOutcomes = terminal.Draft.DestinationOutcomes,
                f.WorkflowReads, f.AnkiBuilds, f.Confirms, externalWrites = 0 }));
            Console.WriteLine("DESTINATION_CASE " + caseName + " PASS");
            useCases.Changed -= preview.Render;
        }

        private static void DestinationWorkflowNegatives()
        {
            foreach (var scenario in new[] { "cancel", "frozen-mismatch", "hash-mismatch", "interrupted-confirm", "missing-snapshot" })
            {
                var f = new DestinationWorkflowFixture(new[] { "anki", "remnote" });
                var gateway = f.Publisher.CreateGateway("offline", null, f.Calls);
                var ct = CancellationToken.None;
                var capture = gateway.PublishCaptureTextAsync(new("取消与证据负例"), ct).GetAwaiter().GetResult();
                var submit = gateway.SubmitAsync(new(capture, false), ct).GetAwaiter().GetResult();
                if (scenario == "missing-snapshot")
                {
                    var read = f.Calls.ReadProjection;
                    f.Calls.ReadProjection = async (id, token) => (await read(id, token)) with { DestinationSnapshot = null };
                    try { gateway.GetProjectionAsync(submit.OperationId, ct).GetAwaiter().GetResult(); throw new Exception("missing snapshot accepted"); }
                    catch (InvalidOperationException) { Assert(f.Confirms == 0, "missing snapshot never exposes actionable guessed Anki preview"); }
                    continue;
                }
                var projection = gateway.GetProjectionAsync(submit.OperationId, ct).GetAwaiter().GetResult().FlashcardDraft;
                var useCases = new FlashcardDraftPreviewUseCases(gateway); useCases.Project(projection);
                if (scenario == "cancel")
                {
                    using var preview = new FlashcardPreviewForm(async () => { await useCases.ConfirmAsync(ct); }, async () => { await useCases.CancelAsync(ct); }, _ => Task.CompletedTask);
                    useCases.Changed += preview.Render; preview.Render(useCases.Current); preview.Show(); Pump(10);
                    Find<Button>(preview, FlashcardPreviewAutomation.Cancel).PerformClick(); Pump(20);
                    Assert(f.Cancels == 1 && f.Confirms == 0 && useCases.Current.Status == FlashcardDraftStatus.Cancelled,
                        "actual Cancel uses neutral draft command and waits typed cancelled projection; no grants/export/task.cancel");
                    useCases.Changed -= preview.Render; continue;
                }
                if (scenario == "frozen-mismatch")
                {
                    var frozen = f.Calls.Frozen;
                    f.Calls.Frozen = async (request, token) => (await frozen(request, token)) with { Destinations = Array.Empty<FrozenDestinationSummary>() };
                }
                if (scenario == "hash-mismatch") f.Calls.Frozen = (_, _) => throw new CoreV3ClientException("artifact_hash_mismatch", "fixture");
                if (scenario == "interrupted-confirm") f.Calls.Confirm = (_, _) => { f.Confirms++; throw new OperationCanceledException(); };
                var result = useCases.ConfirmAsync(ct).GetAwaiter().GetResult();
                Assert(!result.Accepted && f.Confirms == (scenario == "interrupted-confirm" ? 1 : 0), "negative fails closed before write or retains single unknown submission: " + scenario);
                if (scenario == "interrupted-confirm") Assert(useCases.Current.Status == FlashcardDraftStatus.Unknown, "cancelled observation after confirmation cannot claim no writes");
                useCases.ConfirmAsync(ct).GetAwaiter().GetResult();
                Assert(f.Confirms == (scenario == "interrupted-confirm" ? 1 : 0), "failed/unknown confirmation never auto-retries: " + scenario);
            }
        }

        private sealed class DestinationWorkflowFixture
        {
            internal int WorkflowReads, AnkiBuilds, Initials, FollowUps, MakeCards, Confirms, Choices, Replays, Resolutions, Cancels;
            internal bool Unknown;
            internal readonly List<ArtifactPurpose> Purposes = new();
            internal ReadCardsRequest LastRead;
            internal ConfirmedCardsRequest LastConfirm;
            internal DestinationReplayRequest LastReplay;
            internal DesktopDestinationSettingsPublisher Publisher;
            internal DestinationSdkCalls Calls;
            private WorkflowSettingsDocument workflow;
            private readonly Dictionary<string, IReadOnlyList<CardDestinationSelection>> operations = new();
            private IReadOnlyList<CardDestinationSelection> session;
            private readonly TypedArtifactHandle snapshot = FakeHandle("snapshot"), card = FakeHandle("card"), answer = FakeHandle("answer"), receipt = FakeHandle("receipt");
            private readonly VerifiedRemNoteBinding remnote;
            private readonly KnowledgeClipper.Core.V3.Client.FlashcardDraft draft = new("clip.read-aloud-cards", "draft", "1", new string('a', 64),
                new[] { new KnowledgeClipper.Core.V3.Client.FlashcardDraftCard("card-1", "1", new string('b', 64), "什么是惯性？", "保持原有运动状态的性质。") });
            internal DestinationWorkflowFixture(string[] selected)
            {
                var settings = new RemNoteSettingsFixture();
                remnote = settings.DiscoverRemNoteAsync(new("discover", "Saved"), CancellationToken.None).GetAwaiter().GetResult().Binding;
                var config = new RemNoteDestinationSettings("remnote-destination-settings.v1", true, new(remnote.RootRemId, remnote.RootTitle), new("mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd"));
                workflow = new("knowledge-clipper-workflow-settings.v3", "profile", new Dictionary<string, WorkflowSettingsProfile> { ["profile"] = new("Saved", new("cards", selected), Remnote: config) });
                var local = new DesktopModuleSettingsDocument(); local.RemNoteBindings["profile"] = new(config, CoreV3Client.ExportRemNoteBinding(remnote));
                Publisher = new(() => { WorkflowReads++; return workflow; }, () => local, _ => { AnkiBuilds++; return "{\"deck_routes\":{\"canary\":\"零碎知识剪藏::Canary::semantic\"}}"; });
                Calls = new()
                {
                    Publish = (request, ct) => { Purposes.Add(request.Purpose); return Task.FromResult(FakeHandle(request.RequestId)); },
                    ReadArtifact = (handle, ct) => Task.FromResult(Encoding.UTF8.GetBytes("惯性是物体保持原有运动状态的性质。")),
                    RestoreRemNote = settings.RestoreRemNoteAsync,
                    Read = (request, ct) => { LastRead = request; operations["read"] = request.Destinations; return Task.FromResult(Accept("read")); },
                    Initial = (request, ct) => { Initials++; session = request.Destinations; operations["q1"] = session; return Task.FromResult(new QuestionOperationSubmission("q1", "session", 1, true)); },
                    FollowUp = (request, ct) => { FollowUps++; operations["q2"] = session; return Task.FromResult(new QuestionOperationSubmission("q2", "session", 2, true)); },
                    MakeCards = (request, ct) => { MakeCards++; operations["q3"] = session; return Task.FromResult(new QuestionOperationSubmission("q3", "session", 3, true)); },
                    ReadProjection = (op, ct) => Task.FromResult(new ReadAloudCardsProjectionSnapshot(op, ReadAloudCardsPlaybackState.IdleReplayable,
                        Cancels > 0 ? ReadAloudCardsPipelineState.Cancelled : Unknown ? ReadAloudCardsPipelineState.UnknownExternalState : Confirms > 0 ? ReadAloudCardsPipelineState.Completed : ReadAloudCardsPipelineState.AwaitingCardConfirmation,
                        10 + Confirms, "fake", null, null, null, null, card, null) { CardDraft = draft, DestinationSnapshot = snapshot, Destinations = Results(op),
                        LanguageCardChoice = new("choice-token", "saved", new[] { "meaning-reading", "writing", "both" }),
                        CardResolution = new("resolution-token", "saved", "decision", "ambiguity", JsonSerializer.SerializeToElement(new { }), new[] { "accept_original" }), AttentionRequired = Confirms == 0 }),
                    QuestionProjection = (op, ct) => Task.FromResult(new QuestionProjectionSnapshot(op, Unknown ? QuestionProjectionState.Degraded : Confirms > 0 ? QuestionProjectionState.Succeeded : QuestionProjectionState.WaitingInput,
                        10 + Confirms, "fake", answer, op == "q3" ? card : null, null) { CardDraft = op == "q3" ? draft with { SourceOperation = "question.make-cards" } : null, DestinationSnapshot = snapshot, Destinations = Results(op) }),
                    Frozen = (reference, ct) => Task.FromResult(new FrozenDestinationSnapshot(reference.OperationId, reference.OperationId, snapshot,
                        operations[reference.OperationId].Select((x, index) => x is AnkiSelection a ? new FrozenDestinationSummary("anki", "anki", "零碎知识剪藏::Canary::semantic", a.RouteKey, a.Configuration, null)
                            : new FrozenDestinationSummary("remnote", "remnote", remnote.RootRemId, null, ((RemNoteSelection)x).Settings, remnote)).ToArray())),
                    Confirm = (request, ct) => { Confirms++; LastConfirm = request; return Task.FromResult(Accept(request.OperationId)); },
                    Cancel = (request, ct) => { Cancels++; return Task.FromResult(Accept(request.OperationId)); },
                    Choice = (request, ct) => { Choices++; return Task.FromResult(Accept(request.OperationId)); },
                    Resolution = (request, ct) => { Resolutions++; return Task.FromResult(Accept(request.OperationId)); },
                    Replay = (request, ct) => { Replays++; LastReplay = request; return Task.FromResult(Accept(request.OperationId)); }
                };
            }
            internal void SelectNone() => workflow = workflow with { Profiles = new Dictionary<string, WorkflowSettingsProfile> { ["profile"] = workflow.Profiles["profile"] with { Workflow = new("cards", Array.Empty<string>()) } } };
            private IReadOnlyList<DestinationProjection> Results(string op) => operations[op].Select((selection, index) => new DestinationProjection(
                selection is AnkiSelection ? "anki" : "remnote", selection is AnkiSelection ? "anki" : "remnote",
                Unknown && index == operations[op].Count - 1 ? "unknown_external_state" : Confirms == 0 ? "not_started" : "succeeded", Unknown ? "fake-unconfirmed" : null,
                card, snapshot, Confirms > 0 ? receipt : null,
                !(Unknown && index == operations[op].Count - 1), Unknown && index == operations[op].Count - 1 ? null : Confirms == 0 ? 0 : 1)).ToArray();
            private static OperationSubmission Accept(string op) => new(op, 2, true) { CommandId = "command-" + op, AcceptedSequence = 2 };
            // Only offline fixtures construct opaque SDK values. Production never parses tokens or uses reflection.
            private static TypedArtifactHandle FakeHandle(string id)
            {
                var assembly = Assembly.Load("KnowledgeClipper.Core.V3.Contracts");
                var reference = Activator.CreateInstance(assembly.GetType("KnowledgeClipper.Core.V3.Contracts.ArtifactReference"), "fixture", id, "fixture", "1", null, "fixture-store");
                return (TypedArtifactHandle)Activator.CreateInstance(typeof(TypedArtifactHandle), BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { reference }, null);
            }
        }
    }
}

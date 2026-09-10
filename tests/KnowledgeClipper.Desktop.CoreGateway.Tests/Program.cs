using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.Desktop.CoreGateway.Tests
{
    internal static partial class Program
    {
        private static int assertions;

        private static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--anki-probe-only")
                {
                    await AnkiProbeTests();
                    Console.WriteLine("ANKI_PROBE_TYPED_GATE PASS assertions=" + assertions);
                    return 0;
                }
                if (args.Length >= 1 && args[0] == "--current-sdk-authorization-contract")
                {
                    GatewaySourceUsesOnlyPublicDomainFacade();
                    CurrentSdkAuthorizationCoordinatorIsShared();
                    if (args.Length == 3 && args[1] == "--receipt")
                    {
                        var receiptPath = Path.GetFullPath(args[2]);
                        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath));
                        File.WriteAllBytes(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            schema = "kc.desktop-core-authorization-contract/1",
                            status = "PASS",
                            assertions,
                            sdkBoundary = "current-source-build",
                            cases = new[] { "typed-builder-only", "one-operation-coordinator", "read-and-clip-shared-request", "choice-reuses-source-authorization", "confirm-reuses-source-authorization", "replay-reuses-source-authorization", "no-low-level-authorization-reference" },
                            externalCalls = 0,
                            ankiWrites = 0
                        }));
                    }
                    Console.WriteLine("DESKTOP_CORE_GATEWAY_AUTHORIZATION_CONTRACT PASS assertions=" + assertions);
                    return 0;
                }
                await FoundationUnavailableIsProjectedWithoutLegacyFallback();
                await FakeDesktopJourneyUsesOneGatewayAndNormalizedState();
                await QuestionSessionSdkBindings();
                await QuestionBindingNegatives();
                await ReadToAnkiUsesOneTypedSdkPathAndNamedProjection();
                await ProductionReadToAnkiFailsClosedBeforeUnconfirmedExport();
                await LanguageCardSettingsAndChoiceStayTyped();
                await ActionIdentitySurvivesBothProjectionAdapters();
                await NonemptyCommandObservationHandlesStaleAndLocalFailure();
                await ConfirmResumesOneCorrelatedReadObserver();
                GatewaySourceUsesOnlyPublicDomainFacade();
                ImmutableSdkBindingIsClosed();
                Console.WriteLine("DESKTOP_CORE_GATEWAY_TESTS PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("DESKTOP_CORE_GATEWAY_TESTS FAIL " + exception);
                return 1;
            }
        }

        private static async Task FoundationUnavailableIsProjectedWithoutLegacyFallback()
        {
            var sdk = new FakeSdkDomainFacade { QuestionFailure = "capability_unavailable", AnkiFailure = "capability_unavailable" };
            var gateway = new NamedPipeCoreGateway(sdk);
            var local = new LocalPorts();
            var root = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, local, local, local, new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));

            var question = await root.QuestionUseCases.SubmitInitialAsync("foundation unavailable", CancellationToken.None);
            Assert(!question.Accepted && root.State.Snapshot().Question.Status == OperationStatus.Failed && root.State.Snapshot().Question.NoticeCode == "capability_unavailable", "typed unavailable question becomes a normalized failed notice without legacy fallback");

            var configuration = await gateway.PublishAsync("anki-configuration", "{}", CancellationToken.None);
            var noRoute = await root.ModuleActionUseCases.ExecuteAsync(ModuleActionKind.AnkiProbe, new Dictionary<string, object> { ["configuration"] = configuration }, CancellationToken.None);
            Assert(!noRoute.Accepted && noRoute.NoticeCode == "anki-probe-target-selection-required" && sdk.AnkiProbeAttemptCount == 0,
                "missing explicit route fails before any SDK call, distinct from unavailable capability");
            var anki = await root.ModuleActionUseCases.ExecuteAsync(ModuleActionKind.AnkiProbe, new Dictionary<string, object> { ["configuration"] = configuration, ["selectedRoute"] = "default" }, CancellationToken.None);
            Assert(!anki.Accepted && anki.NoticeCode == "capability_unavailable", "typed unavailable Anki probe is not advertised as available");
            Assert(sdk.QuestionInitialCount == 1 && sdk.AnkiProbeAttemptCount == 1, "foundation unavailable makes one typed attempt and invokes no fallback path");
        }

        private static async Task FakeDesktopJourneyUsesOneGatewayAndNormalizedState()
        {
            var sdk = new FakeSdkDomainFacade();
            var gateway = new NamedPipeCoreGateway(sdk);
            var local = new LocalPorts();
            var root = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, local, local, local, new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero), null, null, null, null, gateway);

            await root.Commands.InvokeAsync(InvocationSource.Keyboard, new ApplicationCommand(ApplicationCommandKind.QuestionInitial, "初问"), CancellationToken.None);
            Assert(root.State.Snapshot().Question.Status == OperationStatus.WaitingInput && root.State.Snapshot().Question.AnswerText == "初问答案" && root.FlashcardDraftUseCases.Current.CanFollowUp, "initial question publishes answer plus automatic draft and waits for explicit confirmation through the typed gateway");

            await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.QuestionFollowUp, "追问"), CancellationToken.None);
            Assert(root.State.Snapshot().Question.AnswerText == "追问答案" && sdk.FollowUpCount == 1, "follow-up from UI Automation replaces answer through Application state only");

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.QuestionMakeCards, CardStructureId: "basic"), CancellationToken.None);
            Assert(sdk.MakeCardsCount == 1 && sdk.LastMakeCardsAnswerToken == root.State.Snapshot().Question.Answer.Id, "make-cards uses the opaque answer handle selected by Application state");
            Assert(root.FlashcardDraftUseCases.Current.CanConfirm && root.FlashcardDraftUseCases.Current.Draft.Cards[0].Front == "Question Front" && root.FlashcardDraftUseCases.Current.Draft.Cards[0].Back == "Question Back", "question make-cards projects Core literal Front and Back into the shared preview reducer");

            var configuration = await gateway.PublishAsync("anki-configuration", "anki-config", CancellationToken.None);
            var anki = await root.ModuleActionUseCases.ExecuteAsync(ModuleActionKind.AnkiProbe, new Dictionary<string, object> { ["configuration"] = configuration, ["selectedRoute"] = "default" }, CancellationToken.None);
            var readiness = root.State.Snapshot().ModuleActions[ModuleActionKind.AnkiProbe].AnkiReadiness;
            Assert(anki.Accepted && sdk.AnkiProbeCount == 1 && readiness.CurrentTargetReady && readiness.OptionalRoutesUninitialized, "Anki health consumes typed current-target readiness without treating optional uninitialized routes as failure");

            var remnote = await root.ModuleActionUseCases.ExecuteAsync(ModuleActionKind.RemNoteDiscoverTarget, null, CancellationToken.None);
            var stage = await gateway.RequestPlaybackStopAsync("task", CancellationToken.None);
            Assert(!remnote.Accepted && remnote.NoticeCode == "remnote-typed-sdk-unavailable" && !stage.Accepted && stage.NoticeCode == "core-v3.stage-control-unavailable", "facades absent from the injected adapter remain explicit unavailable instead of using a side channel");
        }

        private static async Task ReadToAnkiUsesOneTypedSdkPathAndNamedProjection()
        {
            var sdk = new FakeSdkDomainFacade();
            sdk.ReadToAnkiProjections.Enqueue(new SdkReadToAnkiProjection(SdkReadToAnkiPlayback.Playing, SdkReadToAnkiPipeline.Reviewing, false, "reviewing"));
            sdk.ReadToAnkiProjections.Enqueue(new SdkReadToAnkiProjection(SdkReadToAnkiPlayback.IdleReplayable, SdkReadToAnkiPipeline.Completed, false, null));
            var gateway = new NamedPipeCoreGateway(sdk, () => "{\"profile\":\"isolated-canary\"}");
            var capture = await gateway.PublishCaptureTextAsync(new ImmutablePlainText("一次捕获的纯文本"), CancellationToken.None);
            var accepted = await gateway.SubmitAsync(new ReadToAnkiSubmission(capture), CancellationToken.None);
            var running = await gateway.GetProjectionAsync(accepted.OperationId, CancellationToken.None);
            var completed = await gateway.GetProjectionAsync(accepted.OperationId, CancellationToken.None);
            var replay = await gateway.ReplayAsync(accepted.OperationId, CancellationToken.None);

            Assert(accepted.Accepted && sdk.ReadToAnkiSubmitCount == 1 && sdk.PublishedPurposes.SequenceEqual(new[] { SdkArtifactPurpose.CaptureText, SdkArtifactPurpose.AnkiConfiguration }),
                "Read button publishes one capture-text and one current Anki configuration before one typed Core use case");
            Assert(sdk.LastIncludeSpeech, "Read routes through the shared typed card operation with Speech explicitly included");
            Assert(running.Playback == ReadToAnkiPlaybackStatus.Playing && running.Pipeline == ReadToAnkiPipelineStatus.Reviewing &&
                   completed.Playback == ReadToAnkiPlaybackStatus.IdleReplayable && completed.Pipeline == ReadToAnkiPipelineStatus.Completed,
                "named Core projection maps playback and pipeline independently without receipt or file inference");
            Assert(replay.Accepted && sdk.ReplayCount == 1 && sdk.LastReplayOperationId == accepted.OperationId && replay.OperationId == accepted.OperationId && replay.CommandId == "replay-command" && replay.AcceptedSequence == 42,
                "replay targets the same typed operation and does not submit a second content or Anki use case");
            var choice = await gateway.ResumeLanguageChoiceAsync(new ReadToAnkiChoiceSubmission(accepted.OperationId, LanguagePracticeChoice.Both), CancellationToken.None);
            Assert(choice.Accepted && sdk.LastLanguageChoice == LanguagePracticeChoice.Both, "language choice uses the admitted typed Core dispatch without a private fallback");

            var clipCapture = await gateway.PublishCaptureTextAsync(new ImmutablePlainText("typed clip"), CancellationToken.None);
            var clip = await gateway.SubmitAsync(new ReadToAnkiSubmission(clipCapture, false), CancellationToken.None);
            Assert(clip.Accepted && !sdk.LastIncludeSpeech && sdk.ReadToAnkiSubmitCount == 2, "Clip routes through the same typed card operation with Speech explicitly omitted");
            var prefix = await gateway.PublishCaptureTextAsync(new ImmutablePlainText("before selection", IsContext: true), CancellationToken.None);
            var suffix = await gateway.PublishCaptureTextAsync(new ImmutablePlainText("after selection", IsContext: true), CancellationToken.None);
            Assert(prefix.Kind == "question-context" && suffix.Kind == "question-context" && sdk.PublishedPurposes.TakeLast(2).All(item => item == SdkArtifactPurpose.QuestionContext),
                "ordered context segments publish with the existing typed QuestionContext purpose rather than CaptureText");
        }

        private static async Task ProductionReadToAnkiFailsClosedBeforeUnconfirmedExport()
        {
            var sdk = new FakeSdkDomainFacade();
            var gateway = new NamedPipeCoreGateway(sdk, () => "{\"profile\":\"must-not-publish\"}");
            var capture = await gateway.PublishCaptureTextAsync(new ImmutablePlainText("仅发布输入，不得未确认导出"), CancellationToken.None);
            var rejected = await gateway.SubmitAsync(new ReadToAnkiSubmission(capture), CancellationToken.None);
            Assert(rejected.Accepted &&
                   sdk.PublishedPurposes.SequenceEqual(new[] { SdkArtifactPurpose.CaptureText, SdkArtifactPurpose.AnkiConfiguration }) && sdk.ReadToAnkiSubmitCount == 1,
                "production read enters the typed Core draft pipeline without a legacy contract-unavailable branch");
        }

        private static async Task LanguageCardSettingsAndChoiceStayTyped()
        {
            foreach (var choice in new[] { "ask", "meaning-reading", "writing", "both" })
            {
                var sdk = new FakeSdkDomainFacade();
                var gateway = new NamedPipeCoreGateway(sdk, () => "{}", () => new SdkLanguageCardSettings("settings-revision-1", choice));
                var capture = await gateway.PublishCaptureTextAsync(new ImmutablePlainText("language input"), CancellationToken.None);
                var submitted = await gateway.SubmitAsync(new ReadToAnkiSubmission(capture), CancellationToken.None);
                Assert(submitted.Accepted && sdk.LastLanguageSettings?.DefaultChoice == choice && sdk.LastLanguageSettings.Revision == "settings-revision-1", "language setting " + choice + " is frozen into the typed operation submit");
            }
            var askSdk = new FakeSdkDomainFacade();
            var askGateway = new NamedPipeCoreGateway(askSdk, () => "{}", () => new SdkLanguageCardSettings("settings-revision-ask", "ask"));
            var askCapture = await askGateway.PublishCaptureTextAsync(new ImmutablePlainText("ask input"), CancellationToken.None);
            var ask = await askGateway.SubmitAsync(new ReadToAnkiSubmission(askCapture), CancellationToken.None);
            var resumed = await askGateway.ResumeLanguageChoiceAsync(new ReadToAnkiChoiceSubmission(ask.OperationId, LanguagePracticeChoice.Both), CancellationToken.None);
            Assert(resumed.Accepted && askSdk.LastLanguageChoice == LanguagePracticeChoice.Both, "ask resumes the same typed operation through one explicit language choice");
            askSdk.ReadToAnkiProjections.Enqueue(new SdkReadToAnkiProjection(SdkReadToAnkiPlayback.IdleReplayable, SdkReadToAnkiPipeline.AwaitingCardConfirmation, true, "draft-ready", 4,
                new SdkFlashcardDraft("token", "revision", new string('a', 64), new[] {
                    new SdkFlashcardDraftCard("context", "revision", new string('b', 64), "Context Front", "Context Back", "language_context"),
                    new SdkFlashcardDraftCard("writing", "revision", new string('c', 64), "Writing Front", "Writing Back", "language_writing") })));
            var both = await askGateway.GetProjectionAsync(ask.OperationId, CancellationToken.None);
            Assert(both.FlashcardDraft.Draft.Cards.Count == 2 && both.FlashcardDraft.Draft.Cards[0].Template == "language_context" && both.FlashcardDraft.Draft.Cards[1].Template == "language_writing", "both preserves the Core typed context then writing order in the shared preview");
        }

        private static async Task ActionIdentitySurvivesBothProjectionAdapters()
        {
            var sdk = new FakeSdkDomainFacade();
            var gateway = new NamedPipeCoreGateway(sdk);
            foreach (var pair in new[] { (ReadAloudCardsActionState.Running, "running"), (ReadAloudCardsActionState.Completed, "completed"), (ReadAloudCardsActionState.Failed, "failed"), (ReadAloudCardsActionState.Cancelled, "cancelled"), (ReadAloudCardsActionState.UnknownExternalState, "unknown_external_state"), (ReadAloudCardsActionState.NotFound, "not_found") })
            {
                var typed = new ReadAloudCardsProjectionSnapshot("identity-op", ReadAloudCardsPlaybackState.IdleReplayable, ReadAloudCardsPipelineState.WaitingLanguageCardChoice, 12, "choice", null, null, null, null, null, null)
                { ActionState = pair.Item1, ActiveCommandId = "active-command", TerminalCommandId = "terminal-command", TerminalSequence = 7, AttentionRequired = true };
                sdk.ReadToAnkiProjections.Enqueue(SdkDomainFacade.ConvertReadProjection(typed));
                var projected = await gateway.GetProjectionAsync("identity-op", CancellationToken.None);
                Assert(projected.ActionState == pair.Item2 && projected.ActiveCommandId == "active-command" && projected.TerminalCommandId == "terminal-command" && projected.TerminalSequence == 7 && projected.AttentionRequired && projected.Pipeline == ReadToAnkiPipelineStatus.WaitingUserChoice,
                    "SDK to gateway to Application preserves explicit action identity: " + pair.Item2);
            }
        }

        private static async Task NonemptyCommandObservationHandlesStaleAndLocalFailure()
        {
            foreach (var queryError in new Exception[] { new SdkDomainException("query_unavailable", null), new OperationCanceledException() })
            {
                var sdk = new FakeSdkDomainFacade { SubmissionCommandId = "accepted-command", QueryError = queryError };
                var gateway = new NamedPipeCoreGateway(sdk, () => "{}");
                var local = new LocalPorts();
                var root = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, local, local, local, null, local, gateway, gateway,
                    new ReadToAnkiObservationPolicy(4, TimeSpan.Zero, TimeSpan.Zero));
                await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
                await root.ReadToAnkiUseCases.ObservationCompletion;
                Assert(root.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.UnknownExternalState && sdk.QueryCount == 1 && sdk.ReadToAnkiSubmitCount == 1,
                    "nonempty command query error/cancel interrupts observation immediately without a fabricated domain terminal or automatic resubmit: " + queryError.GetType().Name);
                await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
                await root.ReadToAnkiUseCases.ObservationCompletion;
                Assert(sdk.ReadToAnkiSubmitCount == 2 && !sdk.LastIncludeSpeech && sdk.QueryCount == 2,
                    "after observation interruption a fresh explicit Clip command can enter without replaying the unknown operation");
            }
            var observedSdk = new FakeSdkDomainFacade { SubmissionCommandId = "current-command" };
            observedSdk.ReadToAnkiProjections.Enqueue(new SdkReadToAnkiProjection(SdkReadToAnkiPlayback.IdleReplayable, SdkReadToAnkiPipeline.Completed, false, "old") { ActionState = "completed", TerminalCommandId = "old-command", TerminalSequence = 20 });
            observedSdk.ReadToAnkiProjections.Enqueue(new SdkReadToAnkiProjection(SdkReadToAnkiPlayback.IdleReplayable, SdkReadToAnkiPipeline.WaitingUserChoice, true, "choice") { ActionState = "completed", TerminalCommandId = "current-command", TerminalSequence = 7 });
            var observedGateway = new NamedPipeCoreGateway(observedSdk, () => "{}");
            var ports = new LocalPorts();
            var observed = new DesktopCompositionRoot(observedGateway, observedGateway, observedGateway, observedGateway, observedGateway, ports, ports, ports, null, ports, observedGateway, observedGateway, new ReadToAnkiObservationPolicy(3, TimeSpan.Zero, TimeSpan.Zero));
            await observed.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await observed.ReadToAnkiUseCases.ObservationCompletion;
            Assert(observed.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.WaitingUserChoice && observedSdk.QueryCount == 2,
                "real gateway observer discards the wrong terminal command and accepts the matching current choice projection");
        }

        private static async Task ConfirmResumesOneCorrelatedReadObserver()
        {
            var draft = new SdkFlashcardDraft("draft-token", "draft-revision", new string('a', 64), new[] { new SdkFlashcardDraftCard("card", "card-revision", new string('b', 64), "independent front", "independent back") });
            foreach (var terminal in new[] { SdkReadToAnkiPipeline.Completed, SdkReadToAnkiPipeline.Failed, SdkReadToAnkiPipeline.Cancelled })
            {
                var sdk = new FakeSdkDomainFacade { SubmissionCommandId = "read-initial", ConfirmReply = new SdkDraftCommand(true, FlashcardDraftStatus.Exporting, "accepted", "confirm-current", 10) };
                SdkReadToAnkiProjection Projection(SdkReadToAnkiPipeline status, string command, long cursor) => new(SdkReadToAnkiPlayback.IdleReplayable, status, status == SdkReadToAnkiPipeline.AwaitingCardConfirmation, null, cursor, draft)
                    { ActionState = "completed", TerminalCommandId = command, TerminalSequence = cursor };
                sdk.ReadToAnkiProjections.Enqueue(Projection(SdkReadToAnkiPipeline.AwaitingCardConfirmation, "read-initial", 7));
                var gateway = new NamedPipeCoreGateway(sdk, () => "{}"); var local = new LocalPorts();
                var root = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, local, local, local, null, local, gateway, gateway, new ReadToAnkiObservationPolicy(5, TimeSpan.Zero, TimeSpan.Zero), gateway);
                await root.ReadToAnkiUseCases.StartAsync(CancellationToken.None); await root.ReadToAnkiUseCases.ObservationCompletion;
                Assert(root.FlashcardDraftUseCases.Current.CanConfirm, "actual waiting-confirmation projection retains the preview: " + terminal);
                sdk.ReadToAnkiProjections.Enqueue(Projection(SdkReadToAnkiPipeline.AwaitingCardConfirmation, "read-initial", 7));
                sdk.ReadToAnkiProjections.Enqueue(Projection(SdkReadToAnkiPipeline.WritingAnki, "confirm-current", 11) with { ActionState = "running", ActiveCommandId = "confirm-current" });
                sdk.ReadToAnkiProjections.Enqueue(Projection(terminal, "confirm-current", 15));
                await root.FlashcardDraftUseCases.ConfirmAsync(CancellationToken.None); await root.ReadToAnkiUseCases.ObservationCompletion;
                var expected = terminal == SdkReadToAnkiPipeline.Completed ? FlashcardDraftStatus.Completed : terminal == SdkReadToAnkiPipeline.Cancelled ? FlashcardDraftStatus.Cancelled : FlashcardDraftStatus.Failed;
                Assert(sdk.ConfirmCount == 1 && sdk.QueryCount == 4 && root.FlashcardDraftUseCases.Current.Status == expected && !root.FlashcardDraftUseCases.Current.CanConfirm,
                    "one correlated observer rejects stale initial action and preserves retained-draft terminal without reopening confirm: " + terminal);
                await root.FlashcardDraftUseCases.ConfirmAsync(CancellationToken.None);
                Assert(sdk.ConfirmCount == 1, "settled draft cannot issue a duplicate confirm: " + terminal);
                sdk.QueryError = new SdkDomainException("new-query-unavailable", null);
                await root.ReadToAnkiUseCases.StartAsync(false, CancellationToken.None); await root.ReadToAnkiUseCases.ObservationCompletion;
                Assert(sdk.ReadToAnkiSubmitCount == 2 && !sdk.LastIncludeSpeech, "fresh explicit Clip enters after confirmed terminal: " + terminal);
            }
        }

        private static void GatewaySourceUsesOnlyPublicDomainFacade()
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(root, "src", "KnowledgeClipper.Desktop.Adapters", "NamedPipeCoreGateway.cs");
            var source = File.ReadAllText(path);
            var forbidden = new[]
            {
                "KnowledgeClipper.Core.V3.Contracts", "CoreV3ProtocolClient", "CommandEnvelope", "QueryEnvelope",
                "WireRequestEnvelope", "KernelSchemas", "protocolVersion", "schemaVersion", "idempotencyKey",
                "authorityReleaseRoot", "artifactPath", "NamedPipeCommonIpcClient", "DesktopAnkiProbeHost",
                "DesktopContentSettingsHost", "FileQuestionAnswerArtifactReader"
            };
            Assert(forbidden.All(value => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0), "gateway source references only the public typed SDK facade and normalized Desktop contracts");
            var forbiddenHeuristics = new[] { ".Single(", "Outputs[", "GetProperty(", "GetType(", "JsonDocument", "connection-status.json" };
            Assert(forbiddenHeuristics.All(value => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0), "gateway uses named typed projections with no array ordering reflection raw worker JSON or UI heuristic");
            Assert(Directory.GetFiles(Path.GetDirectoryName(path), "*NamedPipeCoreGateway*.cs").Length == 1, "Desktop contains exactly one NamedPipeCoreGateway implementation");
        }

        private static void CurrentSdkAuthorizationCoordinatorIsShared()
        {
            var path = Path.Combine(FindRepositoryRoot(), "src", "KnowledgeClipper.Desktop.Adapters", "NamedPipeCoreGateway.cs");
            var source = File.ReadAllText(path);
            Assert(source.Contains("AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck", StringComparison.Ordinal) &&
                   source.Contains("operationAuthorizations", StringComparison.Ordinal) &&
                   source.Contains("RequireOperationAuthorization", StringComparison.Ordinal),
                "Desktop obtains Anki authority only through the public typed builder and one operation authorization coordinator");
            Assert(source.Contains("client.ReadAloudCardsAsync(request", StringComparison.Ordinal) &&
                   source.Contains("client.ClipCardsAsync(request", StringComparison.Ordinal) &&
                   source.Contains("LanguageCardChoiceRequest(requestId, operationId, prompt.TargetToken, selected, authorization)", StringComparison.Ordinal) &&
                   source.Contains("ReadAloudCardsReplayRequest(requestId, operationId, RequireOperationAuthorization(operationId))", StringComparison.Ordinal),
                "Read Extract-choice and replay reuse the same typed authorization without constructing protocol references");
            Assert(!source.Contains("new AuthorizationReference", StringComparison.Ordinal) && !source.Contains("new AnkiWriteAuthorization", StringComparison.Ordinal),
                "Desktop does not create low-level or scope-string authorization paths");
        }

        private static void ImmutableSdkBindingIsClosed()
        {
            var root = FindRepositoryRoot();
            var legacyFreeze = @"D:\Artifacts\KnowledgeClipper\playback-control\sdk-runtime-20260908-01\sdk";
            var freeze = @"D:\Artifacts\KnowledgeClipper\remnote-destinations\sdk-integration-20260909-04";
            var packet = Path.Combine(legacyFreeze, "..", "bundle-manifest.json");
            var clientPath = Path.Combine(freeze, "KnowledgeClipper.Core.V3.Client.dll");
            var contractsPath = Path.Combine(freeze, "KnowledgeClipper.Core.V3.Contracts.dll");
            Assert(Hash(packet) == "fbbdfd7c25b7116e99b1b668c46511b900b04183e217e490d3e351f4942921b7" &&
                   Hash(clientPath) == "271a32528d779c5c052fb98c74fbe1fec5ceab2229086bfb90f8bea811936591" &&
                   Hash(contractsPath) == "69894606f2f3a5c92f100ebff6fed5a388f9db1f222e1a954fba80873155ffa0",
                "Gateway binds exact additive SDK milestone 04 while legacy packet remains unchanged");

            var contracts = Assembly.LoadFrom(contractsPath);
            Assert(contracts.GetExportedTypes().Length == 0, "runtime Contracts dependency exports zero public protocol types");
            var client = Assembly.LoadFrom(clientPath);
            var forbidden = new[] { "CommandEnvelope", "QueryEnvelope", "ArtifactReference", "KernelSchemas", "CoreV3ProtocolClient" };
            Assert(client.GetExportedTypes().All(type => !forbidden.Contains(type.Name, StringComparer.Ordinal)), "Client public surface excludes every low-level protocol DTO");

            var clientType = client.GetType("KnowledgeClipper.Core.V3.Client.CoreV3Client", true);
            Assert(new[] { "GetReadPlaybackControlAsync", "StopReadPlaybackAsync", "WaitReadPlaybackStopAsync" }.All(name=>clientType.GetMethods().Any(m=>m.Name==name)), "new immutable SDK exposes typed round-scoped Stop and verified terminal wait");
            foreach (var state in Enum.GetValues<ReadPlaybackControlState>())
            {
                var native = new ReadPlaybackControlSnapshot("op",state,state==ReadPlaybackControlState.Playing,new ReadPlaybackTarget("op","token","action","attempt"),51);
                var mapped = SdkDomainFacade.MapPlayback(native);
                Assert(mapped.Target.OperationId=="op"&&mapped.Target.TargetToken=="token"&&mapped.Target.ActionCommandId=="action"&&mapped.Target.AttemptId=="attempt"&&mapped.Sequence==51&&mapped.CanStop==native.CanStop&&
                    (state!=ReadPlaybackControlState.Completing||mapped.State==ReadToAnkiPlaybackStatus.StopRequested),"typed stop target and intermediate Completing map without guessing terminal: "+state);
            }
            Assert(new[] { "ReadAloudCardsAsync", "GetReadAloudCardsProjectionAsync", "ChooseLanguageCardTypeAsync", "ConfirmFlashcardDraftAsync", "CancelFlashcardDraftAsync", "ReplayReadAloudCardsAsync", "BeginReadToAnkiActionDiscoveryAsync", "WaitForAcceptedReadToAnkiActionAsync", "WaitReadAloudCardsActionAsync" }
                .All(name => clientType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(method => method.Name == name)),
                "immutable SDK exposes submit typed projection and same-operation replay without a private transport seam");

            var projects = new[]
            {
                Path.Combine(root, "desktop", "RemNoteQuickClipper.Desktop.csproj"),
                Path.Combine(root, "desktop", "RemNoteQuickClipper.Desktop.WindowsCanary.csproj"),
                Path.Combine(root, "tests", "KnowledgeClipper.Desktop.CoreGateway.Tests", "KnowledgeClipper.Desktop.CoreGateway.Tests.csproj")
            };
            var projectText = string.Join("\n", projects.Select(File.ReadAllText));
            Assert(projectText.IndexOf("core-v3\\src", StringComparison.OrdinalIgnoreCase) < 0 &&
                   projectText.IndexOf("remnote-destinations\\sdk-integration-20260909-04", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   projectText.IndexOf("<ProjectReference", StringComparison.OrdinalIgnoreCase) < 0,
                "Desktop projects reference only the immutable SDK root and no mutable Core source or project");
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src", "KnowledgeClipper.Desktop.Adapters"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("repository-root-not-found");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
            assertions++;
            Console.WriteLine("PASS " + message);
        }

        private sealed class LocalPorts : ISettingsRepository, ICredentialStore, IWindowActivationPort, ICurrentTextCapturePort
        {
            public Task<CapturedPlainText> CaptureOnceAsync(CancellationToken token) => Task.FromResult(new CapturedPlainText("independent input"));
            public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new SettingsSnapshot("default", "revision", new Dictionary<string, object>()));
            public Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken cancellationToken) => Task.FromResult(settings);
            public Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken cancellationToken) => Task.FromResult(new CredentialMetadata(credentialId, false, false, "CurrentUser"));
            public Task StoreAsync(string credentialId, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<WindowActivationSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new WindowActivationSnapshot(1, "runtime", "selection", 1));
            public Task<bool> RestoreAsync(long nativeWindowId, CancellationToken cancellationToken) => Task.FromResult(true);
        }

        private sealed class FakeSdkDomainFacade : ISdkDomainFacade
        {
            private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);
            private readonly Dictionary<string, SdkQuestionProjection> projections = new(StringComparer.Ordinal);
            private int artifactSequence;
            internal string QuestionFailure;
            internal string AnkiFailure;
            internal int QuestionInitialCount;
            internal int AnkiProbeAttemptCount;
            internal int FollowUpCount;
            internal int MakeCardsCount;
            internal int AnkiProbeCount;
            internal string LastMakeCardsAnswerToken;
            internal readonly List<SdkArtifactPurpose> PublishedPurposes = new();
            internal readonly Queue<SdkReadToAnkiProjection> ReadToAnkiProjections = new();
            internal int ReadToAnkiSubmitCount;
            internal int ReplayCount;
            internal string LastReplayOperationId;
            internal SdkLanguageCardSettings LastLanguageSettings;
            internal LanguagePracticeChoice? LastLanguageChoice;
            internal bool LastIncludeSpeech;
            internal string SubmissionCommandId;
            internal Exception QueryError;
            internal int QueryCount;
            internal int ConfirmCount;
            internal SdkDraftCommand ConfirmReply = new(true, FlashcardDraftStatus.Completed, "flashcard-draft.confirm-accepted");

            public Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken cancellationToken)
            {
                PublishedPurposes.Add(purpose);
                var artifact = NewArtifact(text);
                return Task.FromResult(artifact);
            }

            public Task<byte[]> ReadArtifactAsync(SdkArtifact artifact, CancellationToken cancellationToken) => Task.FromResult(content[artifact.Token]);

            public Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, string selectedRoute, CancellationToken cancellationToken)
            {
                AnkiProbeAttemptCount++;
                if (AnkiFailure != null) throw new SdkDomainException(AnkiFailure, new InvalidOperationException(AnkiFailure));
                AnkiProbeCount++;
                return Task.FromResult(new SdkSubmission("anki-probe-operation", null));
            }

            public Task<SdkAnkiReadiness> ObserveAnkiReadinessAsync(string operationId, CancellationToken cancellationToken)
            {
                var evidence = NewArtifact("typed readiness evidence");
                return Task.FromResult(new SdkAnkiReadiness(OperationStatus.Succeeded, "current-target-ready", true, true, "default", new[] { "optional-writing" }, evidence));
            }

            public Task<SdkSubmission> QuestionInitialAsync(SdkArtifact question, IReadOnlyList<SdkArtifact> context, CancellationToken cancellationToken)
            {
                QuestionInitialCount++;
                if (QuestionFailure != null) throw new SdkDomainException(QuestionFailure, new InvalidOperationException(QuestionFailure));
                var answer = NewArtifact("初问答案");
                projections["question-operation-initial"] = new SdkQuestionProjection(OperationStatus.WaitingInput, "draft-ready", 1, answer, null, null, 1, QuestionDraft("initial"));
                return Task.FromResult(new SdkSubmission("question-operation-initial", "question-session"));
            }

            public Task<SdkSubmission> QuestionFollowUpAsync(string sessionId, SdkArtifact followUp, CancellationToken cancellationToken)
            {
                FollowUpCount++;
                var answer = NewArtifact("追问答案");
                projections["question-operation-follow-up"] = new SdkQuestionProjection(OperationStatus.WaitingInput, "draft-ready", 2, answer, null, null, 2, QuestionDraft("follow-up"));
                return Task.FromResult(new SdkSubmission("question-operation-follow-up", sessionId));
            }

            public Task<SdkSubmission> QuestionMakeCardsAsync(string sessionId, SdkArtifact answer, CancellationToken cancellationToken)
            {
                MakeCardsCount++;
                LastMakeCardsAnswerToken = answer.Token;
                var cards = NewArtifact("typed card bundle");
                var evidence = NewArtifact("typed destination evidence");
                var draft = new SdkFlashcardDraft("question-token", "question-revision-1", new string('a', 64), new[] { new SdkFlashcardDraftCard("question-card-1", "question-revision-1", new string('b', 64), "Question Front", "Question Back") });
                projections["question-operation-make-cards"] = new SdkQuestionProjection(OperationStatus.WaitingInput, "awaiting-card-confirmation", 2, answer, cards, evidence, 7, draft);
                return Task.FromResult(new SdkSubmission("question-operation-make-cards", sessionId));
            }

            public Task<SdkQuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(projections[taskId]);
            public Task<SdkProjection> GetTaskAsync(string taskId, CancellationToken cancellationToken)
            {
                var projection = projections[taskId];
                return Task.FromResult(new SdkProjection(projection.Status, projection.NoticeCode));
            }

            public Task<SdkReadToAnkiSubmission> ReadToAnkiAsync(SdkArtifact capture, IReadOnlyList<SdkArtifact> orderedContext, IReadOnlyList<SdkContextSegment> roleBoundContext, string targetText, bool contextRequested, string contextGranularity, string sourceWindow, SdkArtifact configuration, SdkLanguageCardSettings languageCards, bool includeSpeech, CancellationToken cancellationToken)
            {
                ReadToAnkiSubmitCount++;
                LastLanguageSettings = languageCards;
                LastIncludeSpeech = includeSpeech;
                return Task.FromResult(new SdkReadToAnkiSubmission("read-to-anki-operation", true, SubmissionCommandId, 1));
            }

            public Task<SdkReadToAnkiSubmission> ChooseLanguageCardAsync(string operationId, LanguagePracticeChoice choice, CancellationToken cancellationToken)
            {
                LastLanguageChoice = choice;
                return Task.FromResult(new SdkReadToAnkiSubmission(operationId, true));
            }

            public Task<SdkReadToAnkiSubmission> ResolveCardAsync(CardResolutionSubmission submission, CancellationToken cancellationToken)
                => Task.FromResult(new SdkReadToAnkiSubmission(submission.OperationId, true, "resolution-command", 2));

            public Task<SdkReadToAnkiProjection> GetReadToAnkiProjectionAsync(string operationId, CancellationToken cancellationToken)
            {
                QueryCount++;
                if (QueryError != null) throw QueryError;
                return Task.FromResult(ReadToAnkiProjections.Dequeue());
            }

            public Task<SdkReadToAnkiSubmission> ReplayReadToAnkiAsync(string operationId, CancellationToken cancellationToken)
            {
                ReplayCount++;
                LastReplayOperationId = operationId;
                return Task.FromResult(new SdkReadToAnkiSubmission(operationId, true, "replay-command", 42));
            }

            public Task<SdkDraftCommand> ConfirmDraftAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken)
            { ConfirmCount++; return Task.FromResult(ConfirmReply); }

            public Task<SdkDraftCommand> CancelDraftAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken) =>
                Task.FromResult(new SdkDraftCommand(true, FlashcardDraftStatus.Cancelled, "flashcard-draft.cancel-accepted"));

            private static SdkFlashcardDraft QuestionDraft(string suffix) => new("question-token-" + suffix, "question-revision-" + suffix, new string('a', 64), new[] { new SdkFlashcardDraftCard("card-" + suffix, "1", new string('b', 64), "Question Front", "Question Back") });

            private SdkArtifact NewArtifact(string text)
            {
                var token = "artifact-" + (++artifactSequence).ToString("D4");
                content[token] = Encoding.UTF8.GetBytes(text);
                return new SdkArtifact(token, token);
            }
        }
    }
}

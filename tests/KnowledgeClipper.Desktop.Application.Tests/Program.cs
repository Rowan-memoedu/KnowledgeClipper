using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.Application.Tests
{
    internal static partial class Program
    {
        private static int assertions;

        private static async Task<int> Main()
        {
            try
            {
                await ModuleActionsAreNormalized();
                await RemNoteCommandsPreserveArgumentsAndEvidence();
                await ModuleObservationRejectsLateCompletion();
                await QuestionSessionUsesNormalizedResults();
                await SettingsStateComesFromRepository();
                await AllInvocationSourcesShareOneCommand();
                await QuestionPortFailureDoesNotLeaveRunning();
                await AcceptedQuestionIsObservedUntilSettled();
                await TypedQuestionProjectionStateMatrixDrivesView();
                await KeyboardFollowUpDispatchesExactlyOnce();
                await QuestionObservationTimeoutAndCancellationTerminate();
                await DuplicateQuestionCommandIsRejectedWhileInFlight();
                await ReadToAnkiCapturesPublishesAndSubmitsExactlyOnceWithoutBlockingTheView();
                await ClipAndReadShareOneCoordinatorWithExplicitSpeechMode();
                await ReadToAnkiTypedProjectionDrivesParallelPlaybackAndPipelineState();
                await ReadToAnkiWaitingChoiceResumesWithoutRecapture();
                await ReadToAnkiReplayTouchesPlaybackOnly();
                await DuplicateReadToAnkiReplayIsRejectedWhileFirstReplayIsInFlight();
                await ReadToAnkiReplayFailurePreservesCompletedPipeline();
                await ReplayIdentityRegression();
                await PlaybackStopTargetsRegression();
                await ReadToAnkiFailureAndTimeoutAreTerminal();
                await TerminalOperationNeverLocksTheNextUserAction();
                await WaitingAndTerminalStatesReleaseBusyAndRejectLateProjection();
                await SupersededObserverCannotOverwriteNewOperation();
                await CurrentCommandIdentityRejectsPriorTerminalProjection();
                await ReadToAnkiTypedTerminalStatesRemainDistinct();
                await UnifiedFlashcardPreviewIsEntrypointNeutralAndConfirmGated();
                await FlashcardPreviewRejectsStaleDoubleConfirmAndRestoresLiteralCards();
                CaptureSessionFreezesExternalContext();
                ApplicationContractsDoNotLeakTransportIdentity();
                ApplicationSourceBoundaryIsIsolated();
                Console.WriteLine("DESKTOP_V3_APPLICATION_TESTS PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("DESKTOP_V3_APPLICATION_TESTS FAIL " + exception);
                return 1;
            }
        }

        private static async Task ModuleActionsAreNormalized()
        {
            var fake = new FakePorts();
            fake.ModuleResults.Enqueue(Result(ModuleActionKind.AnkiProbe, "current-target-ready") with
            {
                AnkiReadiness = new AnkiReadinessSummary(true, true, "default", new[] { "optional-writing" })
            });
            fake.ModuleResults.Enqueue(Result(ModuleActionKind.RemNoteDiscoverTarget, "unique-match", new Dictionary<string, object> { ["targetTitle"] = "剪藏" }));
            var root = fake.Root();

            await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.AnkiProbe), CancellationToken.None);
            var anki = root.State.Snapshot().ModuleActions[ModuleActionKind.AnkiProbe];
            Assert(anki.Status == OperationStatus.Succeeded && anki.AnkiReadiness.CurrentTargetReady && anki.AnkiReadiness.OptionalRoutesUninitialized, "Anki probe distinguishes current target readiness from optional uninitialized routes");

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.RemNoteDiscoverTarget), CancellationToken.None);
            var remnote = root.State.Snapshot().ModuleActions[ModuleActionKind.RemNoteDiscoverTarget];
            Assert(remnote.Status == OperationStatus.Succeeded && Convert.ToString(remnote.Values["targetTitle"]) == "剪藏", "RemNote discovery projects normalized values without parsing UI text");
            Assert(fake.ModuleRequests.Select(item => item.Kind).SequenceEqual(new[] { ModuleActionKind.AnkiProbe, ModuleActionKind.RemNoteDiscoverTarget }), "module actions do not expose operation strings or protocol versions");
        }

        private static async Task RemNoteCommandsPreserveArgumentsAndEvidence()
        {
            var fake = new FakePorts();
            var root = fake.Root();
            foreach (var source in new[] { InvocationSource.Pointer, InvocationSource.Keyboard, InvocationSource.UiAutomation })
            foreach (var pair in new[] {
                (ApplicationCommandKind.RemNoteDiscoverTarget, ModuleActionKind.RemNoteDiscoverTarget),
                (ApplicationCommandKind.RemNoteProbe, ModuleActionKind.RemNoteProbe) })
            {
                var arguments = new Dictionary<string, object> { ["binding"] = new object() };
                var evidence = new ArtifactHandle("evidence-" + fake.ModuleRequests.Count, "settings-evidence");
                fake.ModuleResults.Enqueue(Result(pair.Item2, "typed-result") with { Output = evidence });
                await root.Commands.InvokeAsync(source, new ApplicationCommand(pair.Item1) { Arguments = arguments }, CancellationToken.None);
                Assert(ReferenceEquals(fake.ModuleRequests.Last().Arguments, arguments) && fake.ModuleRequests.Last().Kind == pair.Item2,
                    "RemNote " + source + " preserves caller arguments through the shared Application command");
                Assert(root.State.Snapshot().ModuleActions[pair.Item2].Output == evidence,
                    "RemNote command preserves normalized evidence without UI parsing");
            }
            Assert(fake.ModuleRequests.Count == 6, "RemNote sources dispatch once each through the existing module port");
        }

        private static async Task ModuleObservationRejectsLateCompletion()
        {
            var pending = new TaskCompletionSource<ModuleActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var port = new DeferredModulePort(); port.Results.Enqueue(pending.Task);
            var state = new DesktopStateStore(); var useCases = new ModuleActionUseCases(port, state);
            using var cancel = new CancellationTokenSource();
            var old = useCases.ExecuteAsync(ModuleActionKind.RemNoteDiscoverTarget, null, cancel.Token);
            cancel.Cancel(); await old.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(state.Snapshot().ModuleActions[ModuleActionKind.RemNoteDiscoverTarget].Status == OperationStatus.Cancelled,
                "local observation cancellation is not rewritten as a business failure");
            var b = Result(ModuleActionKind.RemNoteDiscoverTarget, "B") with { Output = new ArtifactHandle("B", "evidence") };
            port.Results.Enqueue(Task.FromResult(b));
            await useCases.ExecuteAsync(ModuleActionKind.RemNoteDiscoverTarget, null, CancellationToken.None);
            pending.SetResult(Result(ModuleActionKind.RemNoteDiscoverTarget, "A-late"));
            await pending.Task;
            Assert(state.Snapshot().ModuleActions[ModuleActionKind.RemNoteDiscoverTarget].Output == b.Output,
                "cancelled non-cooperative module completion cannot overwrite B evidence");
            var a2 = new TaskCompletionSource<ModuleActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            port.Results.Enqueue(a2.Task);
            var oldUncancelled = useCases.ExecuteAsync(ModuleActionKind.RemNoteProbe, null, CancellationToken.None);
            port.Results.Enqueue(Task.FromResult(b));
            await useCases.ExecuteAsync(ModuleActionKind.RemNoteProbe, null, CancellationToken.None);
            a2.SetResult(Result(ModuleActionKind.RemNoteProbe, "stale")); await oldUncancelled;
            Assert(state.Snapshot().ModuleActions[ModuleActionKind.RemNoteProbe].Output == b.Output,
                "new observation generation wins even when superseded caller was not cancelled");
        }
        private sealed class DeferredModulePort : IModuleActionPort
        {
            internal Queue<Task<ModuleActionResult>> Results = new();
            public Task<ModuleActionResult> ExecuteAsync(ModuleActionRequest request, CancellationToken ct) => Results.Dequeue();
        }

        private static async Task QuestionSessionUsesNormalizedResults()
        {
            var fake = new FakePorts();
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "operation-question-1", "accepted") { SessionId = "session-question-1" });
            fake.QuestionProjections.Enqueue(new QuestionProjection("operation-question-1", OperationStatus.Succeeded, 1, true, new ArtifactHandle("answer-1", "question-answer-text"), "answer-ready"));
            fake.Artifacts["answer-1"] = new QuestionAnswerContent("第一轮答案");
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "operation-question-2", "accepted") { SessionId = "session-question-1" });
            fake.QuestionProjections.Enqueue(new QuestionProjection("operation-question-2", OperationStatus.Succeeded, 2, true, new ArtifactHandle("answer-2", "question-answer-text"), "answer-ready"));
            fake.Artifacts["answer-2"] = new QuestionAnswerContent("第二轮答案");
            fake.CardResults.Enqueue(new TaskCommandResult(true, "operation-question-3", "cards-accepted") { SessionId = "session-question-1" });
            fake.QuestionProjections.Enqueue(new QuestionProjection("operation-question-3", OperationStatus.Succeeded, 2, true, new ArtifactHandle("answer-2", "question-answer-text"), "cards-ready")
            {
                CardBundle = new ArtifactHandle("cards-1", "question-card-bundle"),
                DestinationEvidence = new ArtifactHandle("anki-evidence-1", "destination-evidence")
            });
            var root = fake.Root();

            await root.Commands.InvokeAsync(InvocationSource.Keyboard, new ApplicationCommand(ApplicationCommandKind.QuestionInitial, "第一问"), CancellationToken.None);
            Assert(root.State.Snapshot().Question.AnswerAvailable && root.State.Snapshot().Question.TurnCount == 1 && root.State.Snapshot().Question.AnswerText == "第一轮答案", "initial question reaches answer-ready only through query projection and normalized artifact content");

            await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.QuestionFollowUp, "追问"), CancellationToken.None);
            Assert(root.State.Snapshot().Question.Answer.Id == "answer-2" && root.State.Snapshot().Question.TurnCount == 2 && root.State.Snapshot().Question.AnswerText == "第二轮答案", "follow-up replaces visible answer through normalized projection and artifact gateway");

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.QuestionMakeCards, CardStructureId: "basic"), CancellationToken.None);
            Assert(fake.QuestionSubmissions.Count == 2 && !fake.QuestionSubmissions[0].FollowUp && fake.QuestionSubmissions[1].FollowUp, "initial and follow-up share one task command port with domain flags");
            Assert(fake.MakeCardsSubmissions.Single().SessionId == "session-question-1", "make-cards uses the typed question session identity rather than the latest operation identity");
            Assert(root.State.Snapshot().Question.CardBundle.Id == "cards-1" && root.State.Snapshot().Question.DestinationEvidence.Id == "anki-evidence-1", "make-cards reads named card and destination evidence handles from projection");
        }

        private static async Task SettingsStateComesFromRepository()
        {
            var fake = new FakePorts();
            fake.Settings = new SettingsSnapshot("default", "revision-a", new Dictionary<string, object> { ["speech.language"] = "auto" });
            var root = fake.Root();
            var loaded = await root.SettingsUseCases.LoadAsync(CancellationToken.None);
            var saved = await root.SettingsUseCases.SaveAsync(loaded with { Revision = "draft" }, CancellationToken.None);
            Assert(root.State.Snapshot().Settings.Status == OperationStatus.Succeeded && root.State.Snapshot().Settings.Draft.Revision == saved.Revision, "settings state follows repository commit result");
            Assert(fake.SettingsSaveCount == 1, "settings use case has one repository commit seam");
        }

        private static async Task AllInvocationSourcesShareOneCommand()
        {
            var fake = new FakePorts();
            fake.ModuleResults.Enqueue(Result(ModuleActionKind.AnkiProbe, "one"));
            fake.ModuleResults.Enqueue(Result(ModuleActionKind.AnkiProbe, "two"));
            fake.ModuleResults.Enqueue(Result(ModuleActionKind.AnkiProbe, "three"));
            var root = fake.Root();
            var observed = new List<InvocationSource>();
            root.Commands.Invoked += (source, _) => observed.Add(source);
            foreach (var source in new[] { InvocationSource.Pointer, InvocationSource.Keyboard, InvocationSource.UiAutomation })
                await root.Commands.InvokeAsync(source, new ApplicationCommand(ApplicationCommandKind.AnkiProbe), CancellationToken.None);
            Assert(observed.SequenceEqual(new[] { InvocationSource.Pointer, InvocationSource.Keyboard, InvocationSource.UiAutomation }), "pointer keyboard and UI Automation enter one command binding");
            Assert(fake.ModuleRequests.Count == 3 && fake.ModuleRequests.All(item => item.Kind == ModuleActionKind.AnkiProbe), "invocation source cannot change business command semantics");
        }

        private static async Task QuestionPortFailureDoesNotLeaveRunning()
        {
            var fake = new FakePorts { QuestionSubmitException = new IOException("fake-submit-failure") };
            var root = fake.Root();
            var result = await root.QuestionUseCases.SubmitInitialAsync("失败问题", CancellationToken.None);
            Assert(!result.Accepted && root.State.Snapshot().Question.Status == OperationStatus.Unknown && root.State.Snapshot().Question.NoticeCode == "question-submit-IOException", "question command port exception leaves an unresolved submission outcome without inventing Core failure");
        }

        private static async Task AcceptedQuestionIsObservedUntilSettled()
        {
            var fake = new FakePorts();
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "task-observe", "accepted") { SessionId = "session-observe" });
            fake.QuestionProjections.Enqueue(new QuestionProjection("task-observe", OperationStatus.Running, 0, false, null, "running"));
            fake.QuestionProjections.Enqueue(new QuestionProjection("task-observe", OperationStatus.WaitingInput, 1, false, null, "needs-input"));
            var root = fake.Root(new QuestionObservationPolicy(3, TimeSpan.Zero, TimeSpan.Zero));
            var statuses = new List<OperationStatus>();
            root.State.Changed += value => statuses.Add(value.Question.Status);
            await root.QuestionUseCases.SubmitInitialAsync("观察状态", CancellationToken.None);
            Assert(root.State.Snapshot().Question.Status == OperationStatus.WaitingInput && fake.QuestionQueryCount == 2, "accepted question is observed by Application until waiting_input without View polling");
            Assert(statuses.Contains(OperationStatus.Accepted) && statuses.Contains(OperationStatus.Running) && statuses.Contains(OperationStatus.WaitingInput), "typed command receipt and projection drive accepted running and waiting_input states");
        }

        private static async Task TypedQuestionProjectionStateMatrixDrivesView()
        {
            foreach (var terminal in new[] { OperationStatus.Succeeded, OperationStatus.Failed, OperationStatus.Unknown })
            {
                var fake = new FakePorts();
                var operationId = "operation-" + terminal.ToString().ToLowerInvariant();
                fake.QuestionResults.Enqueue(new TaskCommandResult(true, operationId, "accepted") { SessionId = "session-state-matrix" });
                var answer = terminal == OperationStatus.Succeeded ? new ArtifactHandle("answer-matrix", "question-answer-text") : null;
                fake.QuestionProjections.Enqueue(new QuestionProjection(operationId, terminal, 1, answer != null, answer, terminal.ToString().ToLowerInvariant()));
                if (answer != null) fake.Artifacts[answer.Id] = new QuestionAnswerContent("typed answer");
                var root = fake.Root(new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));
                var view = new FakeView();
                using var binding = new DesktopViewBinding(root, view);
                await binding.InvokeAsync(InvocationSource.Keyboard, new ApplicationCommand(ApplicationCommandKind.QuestionInitial, "state matrix"), CancellationToken.None);
                Assert(view.Projections.Last().Question.Status == terminal, "View receives typed " + terminal + " question state without UI wording heuristics");
            }
        }

        private static async Task KeyboardFollowUpDispatchesExactlyOnce()
        {
            var fake = new FakePorts();
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "operation-initial", "accepted") { SessionId = "session-enter" });
            fake.QuestionProjections.Enqueue(new QuestionProjection("operation-initial", OperationStatus.Succeeded, 1, true, new ArtifactHandle("answer-enter-1", "question-answer-text"), "answer-ready"));
            fake.Artifacts["answer-enter-1"] = new QuestionAnswerContent("initial");
            var root = fake.Root(new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));
            var view = new FakeView();
            using var binding = new DesktopViewBinding(root, view);
            await binding.InvokeAsync(InvocationSource.Keyboard, new ApplicationCommand(ApplicationCommandKind.QuestionInitial, "initial"), CancellationToken.None);

            fake.BlockQuestionSubmit = true;
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "operation-follow-up", "accepted") { SessionId = "session-enter" });
            fake.QuestionProjections.Enqueue(new QuestionProjection("operation-follow-up", OperationStatus.Succeeded, 2, true, new ArtifactHandle("answer-enter-2", "question-answer-text"), "answer-ready"));
            fake.Artifacts["answer-enter-2"] = new QuestionAnswerContent("follow-up");
            var first = binding.InvokeAsync(InvocationSource.Keyboard, new ApplicationCommand(ApplicationCommandKind.QuestionFollowUp, "follow-up"), CancellationToken.None);
            await fake.QuestionSubmitEntered.Task;
            await binding.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.QuestionFollowUp, "duplicate"), CancellationToken.None);
            fake.QuestionSubmitRelease.TrySetResult(true);
            await first;
            Assert(fake.QuestionSubmissions.Count(item => item.FollowUp) == 1, "Enter follow-up dispatches exactly one typed command while an operation is in flight");
            Assert(view.Projections.Last().Question.Status == OperationStatus.Succeeded && view.Projections.Last().Question.AnswerText == "follow-up", "follow-up View state comes from typed projection and artifact content");
        }

        private static async Task QuestionObservationTimeoutAndCancellationTerminate()
        {
            var timeout = new FakePorts();
            timeout.QuestionResults.Enqueue(new TaskCommandResult(true, "task-timeout", "accepted") { SessionId = "session-timeout" });
            timeout.QuestionProjections.Enqueue(new QuestionProjection("task-timeout", OperationStatus.Running, 0, false, null, "running"));
            timeout.QuestionProjections.Enqueue(new QuestionProjection("task-timeout", OperationStatus.Running, 0, false, null, "running"));
            var timeoutRoot = timeout.Root(new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));
            await timeoutRoot.QuestionUseCases.SubmitInitialAsync("超时问题", CancellationToken.None);
            Assert(timeoutRoot.State.Snapshot().Question.Status == OperationStatus.Unknown && timeoutRoot.State.Snapshot().Question.NoticeCode == "question-observation-timeout-not-core-failed" && timeoutRoot.State.Snapshot().Question.SessionId == "session-timeout", "question observation timeout is unresolved and retains the accepted session instead of inventing Core failure");

            var cancelled = new FakePorts { BlockQuestionQuery = true };
            cancelled.QuestionResults.Enqueue(new TaskCommandResult(true, "task-cancel", "accepted") { SessionId = "session-cancel" });
            using var source = new CancellationTokenSource();
            var cancelledRoot = cancelled.Root(new QuestionObservationPolicy(3, TimeSpan.Zero, TimeSpan.Zero));
            var pending = cancelledRoot.QuestionUseCases.SubmitInitialAsync("取消问题", source.Token);
            await cancelled.QuestionQueryEntered.Task;
            source.Cancel();
            await pending;
            Assert(cancelledRoot.State.Snapshot().Question.Status == OperationStatus.Unknown && cancelledRoot.State.Snapshot().Question.NoticeCode == "question-observation-interrupted-not-core-cancelled" && cancelledRoot.State.Snapshot().Question.SessionId == "session-cancel", "cancelling UI observation retains session and does not claim Core cancellation");
        }

        private static async Task DuplicateQuestionCommandIsRejectedWhileInFlight()
        {
            var fake = new FakePorts { BlockQuestionSubmit = true };
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "task-one", "accepted") { SessionId = "session-one" });
            fake.QuestionProjections.Enqueue(new QuestionProjection("task-one", OperationStatus.WaitingInput, 0, false, null, "waiting"));
            var root = fake.Root(new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));
            var first = root.QuestionUseCases.SubmitInitialAsync("第一条", CancellationToken.None);
            await fake.QuestionSubmitEntered.Task;
            var duplicate = await root.QuestionUseCases.SubmitInitialAsync("重复 Enter", CancellationToken.None);
            fake.QuestionSubmitRelease.TrySetResult(true);
            await first;
            Assert(!duplicate.Accepted && duplicate.NoticeCode == "question-command-in-progress" && fake.QuestionSubmissions.Count == 1, "duplicate Enter is rejected idempotently while one question command is in flight");
        }

        private static async Task ReadToAnkiCapturesPublishesAndSubmitsExactlyOnceWithoutBlockingTheView()
        {
            var fake = new FakePorts { BlockTextCapture = true };
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-1", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-1", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            var root = fake.Root(readPolicy: OneReadProjection());
            var view = new FakeView();
            using var binding = new DesktopViewBinding(root, view);

            var invocation = binding.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await fake.TextCaptureEntered.Task;
            Assert(!invocation.IsCompleted && view.Projections.Last().ReadToAnki.IsProcessing && root.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Accepted,
                "read click publishes immediate typed processing state while capture continues asynchronously");
            var duplicate = await root.ReadToAnkiUseCases.StartAsync(CancellationToken.None);
            Assert(!duplicate.Accepted && duplicate.NoticeCode == "read-to-anki-operation-active" && fake.TextCaptureCount == 1,
                "duplicate read click is rejected before a second selection or clipboard capture");

            fake.TextCaptureRelease.TrySetResult(true);
            await invocation;
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(fake.TextCaptureCount == 1 && fake.PublishedCaptureArtifacts.Count == 1 && fake.ReadToAnkiSubmissions.Count == 1,
                "read click captures once publishes one immutable capture-text artifact and submits one Core use case");
            Assert(fake.ReadToAnkiSubmissions[0].SourceText.Id == fake.PublishedCaptureArtifacts[0].Id,
                "Core submission receives the exact immutable capture-text handle returned by the artifact gateway");
            Assert(fake.QuestionSubmissions.Count == 0 && fake.ModuleRequests.Count == 0,
                "read click does not trigger legacy question RemNote OCR or parallel Desktop business flows");
        }

        private static async Task ReadToAnkiTypedProjectionDrivesParallelPlaybackAndPipelineState()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-2", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-2", ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.Reviewing, false, "reviewing"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-2", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.CreatingCards, false, "creating-cards"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-2", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.WritingAnki, false, "writing-anki"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-2", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            var root = fake.Root(readPolicy: new ReadToAnkiObservationPolicy(4, TimeSpan.Zero, TimeSpan.Zero));
            var view = new FakeView();
            using var binding = new DesktopViewBinding(root, view);

            await binding.InvokeAsync(InvocationSource.Keyboard, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(view.Projections.Any(item => item.ReadToAnki.Playback == ReadToAnkiPlaybackStatus.Playing && item.ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Reviewing),
                "typed projection can show natural playback and background review concurrently");
            Assert(view.Projections.Any(item => item.ReadToAnki.Playback == ReadToAnkiPlaybackStatus.IdleReplayable && item.ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.CreatingCards) &&
                   view.Projections.Any(item => item.ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.WritingAnki),
                "typed projection keeps background card creation and Anki write visible after natural playback ends");
            Assert(view.Projections.Last().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Completed && view.Projections.Last().ReadToAnki.CanReplay,
                "completed projection remains idle replayable without rerunning the pipeline");
        }

        private static async Task ClipAndReadShareOneCoordinatorWithExplicitSpeechMode()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "clip-operation", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("clip-operation", ReadToAnkiPlaybackStatus.NotRequested, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            var root = fake.Root(readPolicy: OneReadProjection());

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;

            Assert(fake.ReadToAnkiSubmissions.Count == 2 && !fake.ReadToAnkiSubmissions[0].IncludeSpeech && fake.ReadToAnkiSubmissions[1].IncludeSpeech,
                "clip and read enter one operation coordinator and differ only by the explicit Speech branch flag");
            Assert(fake.ReadToAnkiSubmissions.Select(item => item.SourceText.Id).Distinct().Count() == 2 && fake.TextCaptureCount == 2,
                "each clip or read action owns a fresh capture and immutable operation input");
        }

        private static async Task ReadToAnkiWaitingChoiceResumesWithoutRecapture()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-3", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-3", ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.WaitingUserChoice, true, "language-choice-required"));
            fake.ReadToAnkiResumeResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-3", "resumed"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-3", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            var root = fake.Root(readPolicy: OneReadProjection());
            var view = new FakeView();
            using var binding = new DesktopViewBinding(root, view);

            await binding.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(view.Projections.Last().ReadToAnki.NeedsLanguageChoice && view.Projections.Last().ReadToAnki.AttentionRequired,
                "waiting_user_choice is shown only from the typed Core projection");

            await binding.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ReadToAnkiResumeChoice, LanguageChoice: LanguagePracticeChoice.Both), CancellationToken.None);
            Assert(!view.Projections.Last().ReadToAnki.NeedsLanguageChoice && view.Projections.Last().ReadToAnki.Pipeline != ReadToAnkiPipelineStatus.WaitingUserChoice,
                "accepted language choice immediately clears stale waiting-choice UI before or while draft generation resumes");
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(fake.ReadToAnkiChoices.Count == 1 && fake.ReadToAnkiChoices[0].Choice == LanguagePracticeChoice.Both,
                "existing language choice resumes the same typed operation without changing card semantics");
            Assert(fake.TextCaptureCount == 1 && fake.PublishedCaptureArtifacts.Count == 1 && fake.ReadToAnkiSubmissions.Count == 1,
                "language choice resume does not recapture republish or start a second Read-to-Anki operation");
        }

        private static async Task ReadToAnkiReplayTouchesPlaybackOnly()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-4", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-4", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            fake.ReplayResults.Enqueue(ReplayAccepted("read-operation-4"));
            fake.ReadToAnkiProjections.Enqueue(ReplayProjection("read-operation-4"));
            var root = fake.Root(readPolicy: OneReadProjection());

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnkiReplay), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(fake.ReplayOperationIds.SequenceEqual(new[] { "read-operation-4" }) && fake.ReadToAnkiSubmissions.Count == 1 && fake.TextCaptureCount == 1,
                "replay invokes only the typed playback action and never restarts capture review cards or Anki export");
        }

        private static async Task ReadToAnkiReplayFailurePreservesCompletedPipeline()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-replay-failure", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-replay-failure", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            fake.ReplayResults.Enqueue(new ReadToAnkiPlaybackCommandResult(false, "playback-unavailable"));
            var root = fake.Root(readPolicy: OneReadProjection());

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnkiReplay), CancellationToken.None);
            var state = root.State.Snapshot().ReadToAnki;
            Assert(state.Playback == ReadToAnkiPlaybackStatus.Failed && state.Pipeline == ReadToAnkiPipelineStatus.Completed && state.NoticeCode == "playback-unavailable",
                "replay failure is actionable without rewriting a completed review card or Anki result");
        }

        private static async Task DuplicateReadToAnkiReplayIsRejectedWhileFirstReplayIsInFlight()
        {
            var fake = new FakePorts { BlockReplay = true };
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-replay-guard", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-replay-guard", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            fake.ReplayResults.Enqueue(ReplayAccepted("read-operation-replay-guard"));
            fake.ReadToAnkiProjections.Enqueue(ReplayProjection("read-operation-replay-guard"));
            var root = fake.Root(readPolicy: OneReadProjection());

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            var first = root.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None);
            await fake.ReplayEntered.Task;
            var duplicate = await root.ReadToAnkiUseCases.ReplayAsync(CancellationToken.None);
            fake.ReplayRelease.TrySetResult(true);
            await first;
            await root.ReadToAnkiUseCases.ObservationCompletion;

            Assert(!duplicate.Accepted && duplicate.NoticeCode == "read-to-anki-operation-active" && fake.ReplayOperationIds.Count == 1,
                "concurrent replay clicks are de-duplicated before reaching Core or the physical player");
        }

        private static async Task ReadToAnkiFailureAndTimeoutAreTerminal()
        {
            var failed = new FakePorts { ReadToAnkiSubmitException = new IOException("fake-read-submit") };
            var failedRoot = failed.Root(readPolicy: OneReadProjection());
            await failedRoot.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            Assert(failedRoot.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Failed && failedRoot.State.Snapshot().ReadToAnki.NoticeCode == "read-to-anki-submit-IOException",
                "read submit failure becomes terminal actionable typed state instead of infinite processing");

            var timedOut = new FakePorts();
            timedOut.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-timeout", "accepted"));
            timedOut.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-timeout", ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.Reviewing, false, "reviewing"));
            var timeoutRoot = timedOut.Root(readPolicy: OneReadProjection());
            await timeoutRoot.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await timeoutRoot.ReadToAnkiUseCases.ObservationCompletion;
            Assert(timeoutRoot.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Failed && timeoutRoot.State.Snapshot().ReadToAnki.NoticeCode == "read-to-anki-observation-timeout",
                "bounded typed observation fails explicitly instead of leaving the status in progress forever");
        }

        private static async Task ReadToAnkiTypedTerminalStatesRemainDistinct()
        {
            foreach (var terminal in new[] { ReadToAnkiPipelineStatus.PartiallyFailed, ReadToAnkiPipelineStatus.UnknownExternalState })
            {
                var fake = new FakePorts();
                var operationId = "read-operation-" + terminal.ToString().ToLowerInvariant();
                fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, operationId, "accepted"));
                var playback = terminal == ReadToAnkiPipelineStatus.UnknownExternalState ? ReadToAnkiPlaybackStatus.Playing : ReadToAnkiPlaybackStatus.IdleReplayable;
                fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection(operationId, playback, terminal, false, terminal.ToString()));
                var root = fake.Root(readPolicy: OneReadProjection());
                var view = new FakeView();
                using var binding = new DesktopViewBinding(root, view);
                await binding.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
                await root.ReadToAnkiUseCases.ObservationCompletion;
                Assert(view.Projections.Last().ReadToAnki.Pipeline == terminal,
                    "typed " + terminal + " remains distinct and is never inferred from logs file counts or error wording");
            }
        }

        private static async Task TerminalOperationNeverLocksTheNextUserAction()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-old", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-old", ReadToAnkiPlaybackStatus.Failed, ReadToAnkiPipelineStatus.Failed, false, "content-failed"));
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "read-operation-new", "accepted"));
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("read-operation-new", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "completed"));
            var root = fake.Root(readPolicy: OneReadProjection());

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(root.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Failed,
                "a failed operation reaches a terminal state and releases its busy lease");

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(root.State.Snapshot().ReadToAnki.OperationId == "read-operation-new" && root.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Completed && fake.TextCaptureCount == 2,
                "a new action receives an independent operation ID and is not locked by the previous failed operation");
        }

        private static async Task WaitingAndTerminalStatesReleaseBusyAndRejectLateProjection()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "recovery-waiting", "accepted") { CommandId = "command-waiting", AcceptedSequence = 10 });
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("recovery-waiting", ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.WaitingUserChoice, true, "waiting") { ActionState = "running", ActiveCommandId = "command-waiting" });
            fake.ReadToAnkiResumeResults.Enqueue(new ReadToAnkiSubmissionResult(true, "recovery-waiting", "choice-accepted") { CommandId = "command-choice", AcceptedSequence = 12 });
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("recovery-waiting", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "choice-completed") { ActionState = "completed", TerminalCommandId = "command-choice", TerminalSequence = 13 });
            var cases = new[]
            {
                ("completed", ReadToAnkiPipelineStatus.Completed, "completed"),
                ("cancelled", ReadToAnkiPipelineStatus.Failed, "cancelled"),
                ("failed", ReadToAnkiPipelineStatus.Failed, "failed")
            };
            foreach (var item in cases)
            {
                var operationId = "recovery-" + item.Item1;
                fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, operationId, "accepted") { CommandId = "command-" + item.Item1, AcceptedSequence = 10 });
                fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection(operationId, ReadToAnkiPlaybackStatus.NotRequested, item.Item2, item.Item2 == ReadToAnkiPipelineStatus.WaitingUserChoice, item.Item1)
                {
                    ActionState = item.Item3, ActiveCommandId = item.Item2 == ReadToAnkiPipelineStatus.WaitingUserChoice ? "command-" + item.Item1 : null,
                    TerminalCommandId = item.Item2 == ReadToAnkiPipelineStatus.WaitingUserChoice ? null : "command-" + item.Item1, TerminalSequence = 11
                });
            }
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "recovery-final", "accepted") { CommandId = "command-final", AcceptedSequence = 20 });
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("recovery-failed", ReadToAnkiPlaybackStatus.NotRequested, ReadToAnkiPipelineStatus.Failed, false, "late-old") { ActionState = "failed", TerminalCommandId = "command-failed", TerminalSequence = 12 });
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("recovery-final", ReadToAnkiPlaybackStatus.NotRequested, ReadToAnkiPipelineStatus.Completed, false, "final-visible") { ActionState = "completed", TerminalCommandId = "command-final", TerminalSequence = 21 });
            var root = fake.Root(readPolicy: new ReadToAnkiObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));

            await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(root.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.WaitingUserChoice, "waiting choice is visible and releases the GUI command lease for its explicit choice action");
            await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ReadToAnkiResumeChoice, LanguageChoice: LanguagePracticeChoice.Both), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(root.State.Snapshot().ReadToAnki.Pipeline == ReadToAnkiPipelineStatus.Completed, "both choice resumes the same operation and reaches a terminal visible state");
            foreach (var item in cases)
            {
                await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
                await root.ReadToAnkiUseCases.ObservationCompletion;
                Assert(root.State.Snapshot().ReadToAnki.OperationId == "recovery-" + item.Item1, item.Item1 + " releases the shared GUI command lease with a visible typed state");
            }
            await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(root.State.Snapshot().ReadToAnki.OperationId == "recovery-final" && root.State.Snapshot().ReadToAnki.NoticeCode == "final-visible" && fake.TextCaptureCount == 5,
                "a late projection from the old task cannot overwrite or suppress the next GUI action");

            var counts = new Dictionary<string, int> { ["meaning-reading"] = 1, ["writing"] = 1, ["both"] = 2 };
            foreach (var item in counts)
            {
                var port = new FakeFlashcardDraftPort();
                var preview = new FlashcardDraftPreviewUseCases(port);
                var cards = Enumerable.Range(0, item.Value).Select(index => new FlashcardDraftCard(item.Key + "-" + index, "1", new string((char)('a' + index), 64), item.Key + " front " + index, item.Key + " back " + index)).ToArray();
                preview.Project(new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, new FlashcardDraftRevision("operation-" + item.Key, "token-" + item.Key, "1", new string('f', 64), FlashcardDraftSource.ReadAloud, cards, new[] { "Anki" }), 1, "draft-ready", true));
                Assert(preview.Current.Draft.Cards.Count == item.Value, item.Key + " projects the locked 1/1/2 card count through the shared preview reducer");
            }
        }

        private static async Task SupersededObserverCannotOverwriteNewOperation()
        {
            foreach (var releaseWithException in new[] { false, true })
            {
                var fake = new FakePorts();
                fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "generation-old", "accepted"));
                fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "generation-new", "accepted"));
                var oldWait = new TaskCompletionSource<ReadToAnkiProjection>(TaskCreationOptions.RunContinuationsAsynchronously);
                fake.ReadToAnkiProjectionTasks.Enqueue(Task.FromResult(new ReadToAnkiProjection("generation-old", ReadToAnkiPlaybackStatus.Playing, ReadToAnkiPipelineStatus.Completed, false, "old-cards-completed")));
                fake.ReadToAnkiProjectionTasks.Enqueue(oldWait.Task);
                fake.ReadToAnkiProjectionTasks.Enqueue(Task.FromResult(new ReadToAnkiProjection("generation-new", ReadToAnkiPlaybackStatus.NotRequested, ReadToAnkiPipelineStatus.Completed, false, "new-visible")));
                var draftPort = new FakeFlashcardDraftPort();
                var root = fake.Root(readPolicy: new ReadToAnkiObservationPolicy(3, TimeSpan.Zero, TimeSpan.Zero), flashcardDraft: draftPort);

                await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
                await fake.ProjectionWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert(root.State.Snapshot().ReadToAnki.OperationId == "generation-old" && root.State.Snapshot().ReadToAnki.Playback == ReadToAnkiPlaybackStatus.Playing,
                    "old cards-completed plus playback-playing remains observable while its typed projection wait is pending");

                await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
                await root.ReadToAnkiUseCases.ObservationCompletion;
                Assert(root.State.Snapshot().ReadToAnki.OperationId == "generation-new" && root.State.Snapshot().ReadToAnki.NoticeCode == "new-visible",
                    "new GUI action owns the state before the superseded observer is released");

                if (releaseWithException) oldWait.SetException(new InvalidOperationException("late-old-failure"));
                else oldWait.SetResult(new ReadToAnkiProjection("generation-old", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "late-old-success")
                {
                    FlashcardDraft = new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, Draft(FlashcardDraftSource.ReadAloud, 1, "late-old"), 99, "late-old-draft", true)
                });
                await Task.Delay(20);
                Assert(root.State.Snapshot().ReadToAnki.OperationId == "generation-new" && root.State.Snapshot().ReadToAnki.NoticeCode == "new-visible" && root.FlashcardDraftUseCases.Current.Draft == null,
                    releaseWithException ? "superseded observer exception cannot fail the new operation" : "superseded observer projection and preview callback cannot overwrite the new operation");
            }
        }

        private static ReadToAnkiObservationPolicy OneReadProjection() => new(1, TimeSpan.Zero, TimeSpan.Zero);

        private static async Task CurrentCommandIdentityRejectsPriorTerminalProjection()
        {
            var fake = new FakePorts();
            fake.ReadToAnkiResults.Enqueue(new ReadToAnkiSubmissionResult(true, "shared-operation", "accepted") { CommandId = "command-new", AcceptedSequence = 12 });
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("shared-operation", ReadToAnkiPlaybackStatus.IdleReplayable, ReadToAnkiPipelineStatus.Completed, false, "old-terminal") { ActionState = "completed", TerminalCommandId = "command-old", TerminalSequence = 11 });
            fake.ReadToAnkiProjections.Enqueue(new ReadToAnkiProjection("shared-operation", ReadToAnkiPlaybackStatus.NotRequested, ReadToAnkiPipelineStatus.Completed, false, "new-terminal") { ActionState = "completed", TerminalCommandId = "command-new", TerminalSequence = 13 });
            var root = fake.Root(readPolicy: new ReadToAnkiObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ClipToAnki), CancellationToken.None);
            await root.ReadToAnkiUseCases.ObservationCompletion;
            Assert(root.State.Snapshot().ReadToAnki.NoticeCode == "new-terminal" && root.State.Snapshot().ReadToAnki.Playback == ReadToAnkiPlaybackStatus.NotRequested,
                "a prior terminal projection cannot complete the current command even when it shares the operation ID");
        }

        private static async Task UnifiedFlashcardPreviewIsEntrypointNeutralAndConfirmGated()
        {
            foreach (var source in new[] { FlashcardDraftSource.ReadAloud, FlashcardDraftSource.Question, FlashcardDraftSource.FollowUp, FlashcardDraftSource.MakeCards })
            {
                var port = new FakeFlashcardDraftPort();
                var useCases = new FlashcardDraftPreviewUseCases(port);
                var draft = Draft(source, 1, source.ToString().ToLowerInvariant());
                useCases.Project(new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, draft, 1, "draft-ready", true));
                Assert(useCases.Current.CanConfirm && useCases.Current.Draft.Cards.Count == 2 && useCases.Current.Draft.Cards[0].Front == "Front A" && useCases.Current.Draft.Cards[1].Back == "Back B",
                    source + " renders the same ordered literal Front and Back draft through one preview reducer");
                Assert(port.Confirmations.Count == 0, source + " preview creates no exporter request before explicit confirmation");

                var accepted = await useCases.ConfirmAsync(CancellationToken.None);
                Assert(accepted.Accepted && port.Confirmations.Count == 1 && port.Confirmations[0].DraftToken == draft.DraftToken && port.Confirmations[0].Revision == draft.Revision && port.Confirmations[0].Sha256 == draft.Sha256,
                    source + " explicit confirm carries exactly operation token revision and digest once");
            }

            var cancelledPort = new FakeFlashcardDraftPort();
            var cancelled = new FlashcardDraftPreviewUseCases(cancelledPort);
            cancelled.Project(new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, Draft(FlashcardDraftSource.ReadAloud, 1, "cancel"), 1, "draft-ready", true));
            await cancelled.CancelAsync(CancellationToken.None);
            Assert(cancelled.Current.Status == FlashcardDraftStatus.Cancelled && cancelledPort.Confirmations.Count == 0,
                "closing or cancelling the shared preview produces zero destination commands");
        }

        private static async Task FlashcardPreviewRejectsStaleDoubleConfirmAndRestoresLiteralCards()
        {
            var port = new FakeFlashcardDraftPort();
            var useCases = new FlashcardDraftPreviewUseCases(port);
            var first = Draft(FlashcardDraftSource.Question, 1, "revision-one");
            var followUp = Draft(FlashcardDraftSource.FollowUp, 2, "revision-two");
            useCases.Project(new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, first, 1, "draft-ready", true));
            useCases.Project(new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, followUp, 2, "draft-replaced", true));
            useCases.Project(new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, first, 1, "late-projection", true));
            Assert(useCases.Current.Draft.Revision == "2" && useCases.Current.Draft.DraftToken == followUp.DraftToken && useCases.Current.NoticeCode == "flashcard-draft.stale-projection-rejected",
                "follow-up replaces the draft revision in place and an older token projection is rejected");

            var confirmed = await useCases.ConfirmAsync(CancellationToken.None);
            var duplicate = await useCases.ConfirmAsync(CancellationToken.None);
            Assert(confirmed.Accepted && !duplicate.Accepted && port.Confirmations.Count == 1 && port.Confirmations[0].DraftToken == followUp.DraftToken,
                "double confirm is rejected before a second destination command and only the newest token can leave the preview");

            var restored = new FlashcardDraftPreviewUseCases(new FakeFlashcardDraftPort());
            restored.Project(new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, followUp, 2, "restored", true));
            Assert(restored.Current.CanConfirm && restored.Current.Draft.Revision == "2" &&
                   restored.Current.Draft.Cards.Select(card => card.Front + "\n" + card.Back).SequenceEqual(followUp.Cards.Select(card => card.Front + "\n" + card.Back)),
                "restart recovery reprojects the identical revision and literal Front Back values without auto-confirming");
        }

        private static FlashcardDraftRevision Draft(FlashcardDraftSource source, long revision, string suffix) => new(
            "operation-shared-preview", "draft-token-" + suffix, revision.ToString(), new string(revision == 1 ? 'a' : 'b', 64), source,
            new[] { new FlashcardDraftCard("card-a", "1", new string('c', 64), "Front A", "Back A"), new FlashcardDraftCard("card-b", "1", new string('d', 64), "Front B", "Back B") },
            new[] { "Anki" });

        private static void CaptureSessionFreezesExternalContext()
        {
            var source = new WindowActivationSnapshot(42, "runtime-id:1.2.3", "清晰选区", 7);
            var session = CaptureSession.Start("capture-session-1", DateTimeOffset.Parse("2026-09-02T04:00:00Z"), source);
            source = source with { NativeWindowId = 99, FocusedElementRuntimeId = "changed", Selection = "changed", ClipboardSequence = 8 };
            Assert(session.FrozenWindow.NativeWindowId == 42 && session.FrozenWindow.FocusedElementRuntimeId == "runtime-id:1.2.3" && session.FrozenWindow.Selection == "清晰选区" && session.FrozenWindow.ClipboardSequence == 7, "capture session freezes foreground focus selection and clipboard sequence before UI interaction");
        }

        private static void ApplicationContractsDoNotLeakTransportIdentity()
        {
            var forbidden = new[] { "Operation", "SchemaVersion", "WorkerId", "Subject", "IdempotencyKey", "ManifestPath", "Authority", "Authorization", "AuthorizationReference", "ExpiresAt", "AuthorityHash", "AuthoritySha", "IpcVersion", "RawPath", "ArtifactPath", "FilePath" };
            var types = typeof(DesktopCompositionRoot).Assembly.GetTypes().Where(type => type.Namespace == "KnowledgeClipper.Desktop.Application.V3");
            var leaks = types.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(property => type.Name + "." + property.Name)).Where(name => forbidden.Any(item => name.EndsWith("." + item, StringComparison.OrdinalIgnoreCase))).ToArray();
            Assert(leaks.Length == 0, "Application DTOs contain no IPC version worker schema path authority hash or raw artifact path");
            Assert(types.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).All(property => property.Name.IndexOf("Authorization", StringComparison.OrdinalIgnoreCase) < 0 && property.Name.IndexOf("Expires", StringComparison.OrdinalIgnoreCase) < 0), "missing wrong or expired capability authorization cannot be injected through Desktop UI or Application commands");
            Assert(types.All(type => type.FullName.IndexOf("RemNoteQuickClipper", StringComparison.OrdinalIgnoreCase) < 0), "v3 Application identity is neutral and has no legacy namespace dependency");
        }

        private static void ApplicationSourceBoundaryIsIsolated()
        {
            var root = FindRepositoryRoot();
            var applicationRoot = Path.Combine(root, "src", "KnowledgeClipper.Desktop.Application");
            var source = string.Join("\n", Directory.GetFiles(applicationRoot, "*.cs").Select(File.ReadAllText));
            var forbidden = new[]
            {
                "DesktopAnkiProbeHost", "DesktopContentSettingsHost", "FileQuestionAnswerArtifactReader",
                "FileOcrHumanResolutionArtifactReader", "NamedPipeCommonIpcClient", "CandidateIntegrationConfig",
                "AutomationCatalogV", "schemaVersion", "authorityReleaseRoot", "CoreV3Client", "protocolVersion", "idempotencyKey", "artifactPath"
            };
            Assert(forbidden.All(item => source.IndexOf(item, StringComparison.OrdinalIgnoreCase) < 0), "v3 Application source has no legacy host file reader pipe schema or authority dependency");
            Assert(source.IndexOf("Button.Text", StringComparison.OrdinalIgnoreCase) < 0 && source.IndexOf("AutomationId", StringComparison.OrdinalIgnoreCase) < 0 && source.IndexOf("WindowState", StringComparison.OrdinalIgnoreCase) < 0,
                "v3 Application state contains no UI wording UI Automation success heuristic or shell size ownership");

            var floating = File.ReadAllText(Path.Combine(root, "src", "DesktopUi.cs"));
            Assert(floating.IndexOf("UserSettings.Load(config)", StringComparison.Ordinal) < 0 &&
                   floating.IndexOf("new FileQuestionAnswerArtifactReader", StringComparison.Ordinal) < 0 &&
                   floating.IndexOf("new FileOcrHumanResolutionArtifactReader", StringComparison.Ordinal) < 0 &&
                   floating.IndexOf("CreateProductionController", StringComparison.Ordinal) < 0,
                "BoundClipperForm no longer loads settings creates transport services or opens artifact readers");

            var settings = File.ReadAllText(Path.Combine(root, "src", "ProjectSettingsForm.cs"));
            Assert(settings.IndexOf("DesktopCredentialSettings.Load(", StringComparison.Ordinal) < 0 &&
                   settings.IndexOf("new DesktopWorkflowSettingsService", StringComparison.Ordinal) < 0 &&
                   settings.IndexOf("DesktopModuleActionHost.Load(", StringComparison.Ordinal) < 0,
                "SettingsForm no longer constructs stores descriptors or module action hosts");
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src", "KnowledgeClipper.Desktop.Application"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("repository-root-not-found");
        }

        private static ModuleActionResult Result(ModuleActionKind kind, string notice, IReadOnlyDictionary<string, object> values = null) =>
            new(true, OperationStatus.Succeeded, notice, values ?? new Dictionary<string, object> { ["kind"] = kind.ToString() }, null, false, false);

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
            assertions++;
            Console.WriteLine("PASS " + message);
        }

        private sealed class FakeView : IDesktopApplicationView
        {
            internal readonly List<DesktopViewProjection> Projections = new();
            public void Render(DesktopViewProjection projection) => Projections.Add(projection);
        }

        private sealed class FakeFlashcardDraftPort : IFlashcardDraftPort
        {
            internal readonly List<FlashcardDraftConfirmation> Confirmations = new();
            public Task<FlashcardDraftCommandResult> ConfirmAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken)
            {
                Confirmations.Add(confirmation);
                return Task.FromResult(new FlashcardDraftCommandResult(true, FlashcardDraftStatus.Exporting, "flashcard-draft.confirm-accepted"));
            }
            public Task<FlashcardDraftCommandResult> CancelAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken) =>
                Task.FromResult(new FlashcardDraftCommandResult(true, FlashcardDraftStatus.Cancelled, "flashcard-draft.cancel-accepted"));
            public Task<FlashcardDraftProjection> GetAsync(string operationId, CancellationToken cancellationToken) =>
                Task.FromResult(new FlashcardDraftProjection(FlashcardDraftStatus.Completed, null, 99, "flashcard-draft.completed", false));
        }

        private sealed class FakePorts : ITaskCommandPort, ITaskQueryPort, IModuleActionPort, IStageControlPort, IArtifactGateway, ISettingsRepository, ICredentialStore, IWindowActivationPort, ICurrentTextCapturePort, IReadToAnkiInputArtifactPort, IReadToAnkiPort
        {
            internal readonly Queue<ModuleActionResult> ModuleResults = new();
            internal readonly List<ModuleActionRequest> ModuleRequests = new();
            internal readonly Queue<TaskCommandResult> QuestionResults = new();
            internal readonly Queue<QuestionProjection> QuestionProjections = new();
            internal readonly Queue<TaskCommandResult> CardResults = new();
            internal readonly List<QuestionSubmission> QuestionSubmissions = new();
            internal readonly List<MakeCardsSubmission> MakeCardsSubmissions = new();
            internal readonly Dictionary<string, object> Artifacts = new(StringComparer.Ordinal);
            internal SettingsSnapshot Settings = new("default", "revision-0", new Dictionary<string, object>());
            internal int SettingsSaveCount;
            internal int QuestionQueryCount;
            internal Exception QuestionSubmitException;
            internal bool BlockQuestionSubmit;
            internal bool BlockQuestionQuery;
            internal readonly TaskCompletionSource<bool> QuestionSubmitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> QuestionSubmitRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> QuestionQueryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly Queue<ReadToAnkiSubmissionResult> ReadToAnkiResults = new();
            internal readonly Queue<ReadToAnkiSubmissionResult> ReadToAnkiResumeResults = new();
            internal readonly Queue<ReadToAnkiPlaybackCommandResult> ReplayResults = new();
            internal readonly Queue<ReadToAnkiProjection> ReadToAnkiProjections = new();
            internal readonly Queue<Task<ReadToAnkiProjection>> ReadToAnkiProjectionTasks = new();
            internal readonly TaskCompletionSource<bool> ProjectionWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly List<ReadToAnkiSubmission> ReadToAnkiSubmissions = new();
            internal readonly List<ReadToAnkiChoiceSubmission> ReadToAnkiChoices = new();
            internal readonly List<string> ReplayOperationIds = new();
            internal readonly List<ArtifactHandle> PublishedCaptureArtifacts = new();
            internal int TextCaptureCount;
            internal bool BlockTextCapture;
            internal bool BlockReplay;
            internal Exception ReadToAnkiSubmitException;
            internal readonly TaskCompletionSource<bool> TextCaptureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> TextCaptureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReplayEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReplayRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal DesktopCompositionRoot Root(QuestionObservationPolicy policy = null, ReadToAnkiObservationPolicy readPolicy = null, IFlashcardDraftPort flashcardDraft = null) => new(this, this, this, this, this, this, this, this, policy, this, this, this, readPolicy, flashcardDraft);
            public Task<ModuleActionResult> ExecuteAsync(ModuleActionRequest request, CancellationToken cancellationToken) { ModuleRequests.Add(request); return Task.FromResult(ModuleResults.Dequeue()); }
            public async Task<TaskCommandResult> SubmitQuestionAsync(QuestionSubmission submission, CancellationToken cancellationToken)
            {
                QuestionSubmissions.Add(submission);
                if (QuestionSubmitException != null) throw QuestionSubmitException;
                if (BlockQuestionSubmit)
                {
                    QuestionSubmitEntered.TrySetResult(true);
                    await QuestionSubmitRelease.Task.WaitAsync(cancellationToken);
                }
                return QuestionResults.Dequeue();
            }
            public Task<TaskCommandResult> MakeCardsAsync(MakeCardsSubmission submission, CancellationToken cancellationToken) { MakeCardsSubmissions.Add(submission); return Task.FromResult(CardResults.Dequeue()); }
            public async Task<QuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken)
            {
                QuestionQueryCount++;
                if (BlockQuestionQuery)
                {
                    QuestionQueryEntered.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return QuestionProjections.Dequeue();
            }
            public Task<TaskProjection> GetRecentAsync(CancellationToken cancellationToken) => Task.FromResult(new TaskProjection(null, OperationStatus.Idle, null, null));
            public Task<StageControlResult> RequestPlaybackStopAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(new StageControlResult(true, "stop-requested", "accepted"));
            public Task<T> ReadAsync<T>(ArtifactHandle artifact, CancellationToken cancellationToken)
            {
                object value;
                if (artifact == null || !Artifacts.TryGetValue(artifact.Id, out value) || value is not T typed) throw new InvalidOperationException("fake-artifact-not-found");
                return Task.FromResult(typed);
            }
            public Task<ArtifactHandle> PublishAsync<T>(string kind, T value, CancellationToken cancellationToken)
            {
                var handle = new ArtifactHandle("published-" + (Artifacts.Count + 1), kind);
                Artifacts[handle.Id] = value;
                return Task.FromResult(handle);
            }
            public Task<ArtifactHandle> PublishCaptureTextAsync(ImmutablePlainText value, CancellationToken cancellationToken)
            {
                var handle = new ArtifactHandle("capture-text-" + (PublishedCaptureArtifacts.Count + 1), "capture-text");
                PublishedCaptureArtifacts.Add(handle);
                Artifacts[handle.Id] = value;
                return Task.FromResult(handle);
            }
            public async Task<CapturedPlainText> CaptureOnceAsync(CancellationToken cancellationToken)
            {
                TextCaptureCount++;
                if (BlockTextCapture)
                {
                    TextCaptureEntered.TrySetResult(true);
                    await TextCaptureRelease.Task.WaitAsync(cancellationToken);
                }
                return new CapturedPlainText("普通文本：用于朗读并后台制卡。");
            }
            public Task<ReadToAnkiSubmissionResult> SubmitAsync(ReadToAnkiSubmission submission, CancellationToken cancellationToken)
            {
                ReadToAnkiSubmissions.Add(submission);
                if (ReadToAnkiSubmitException != null) throw ReadToAnkiSubmitException;
                return Task.FromResult(ReadToAnkiResults.Dequeue());
            }
            public Task<ReadToAnkiProjection> GetProjectionAsync(string operationId, CancellationToken cancellationToken)
            {
                if (ReadToAnkiProjectionTasks.Count == 0) return Task.FromResult(ReadToAnkiProjections.Dequeue());
                var task = ReadToAnkiProjectionTasks.Dequeue();
                if (!task.IsCompleted) ProjectionWaitEntered.TrySetResult(true);
                return task;
            }
            public Task<ReadToAnkiSubmissionResult> ResumeLanguageChoiceAsync(ReadToAnkiChoiceSubmission submission, CancellationToken cancellationToken)
            {
                ReadToAnkiChoices.Add(submission);
                return Task.FromResult(ReadToAnkiResumeResults.Dequeue());
            }
            public Task<ReadToAnkiSubmissionResult> ResolveCardAsync(CardResolutionSubmission submission, CancellationToken cancellationToken)
                => Task.FromResult(new ReadToAnkiSubmissionResult(true, submission.OperationId, "resolution.accepted") { CommandId = "resolution-command", AcceptedSequence = 2 });
            public async Task<ReadToAnkiPlaybackCommandResult> ReplayAsync(string operationId, CancellationToken cancellationToken)
            {
                ReplayOperationIds.Add(operationId);
                if (BlockReplay)
                {
                    ReplayEntered.TrySetResult(true);
                    await ReplayRelease.Task.WaitAsync(cancellationToken);
                }
                return ReplayResults.Dequeue();
            }
            public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
            public Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken cancellationToken) { SettingsSaveCount++; Settings = settings with { Revision = "revision-committed" }; return Task.FromResult(Settings); }
            public Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken cancellationToken) => Task.FromResult(new CredentialMetadata(credentialId, false, false, "CurrentUser"));
            public Task StoreAsync(string credentialId, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<WindowActivationSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new WindowActivationSnapshot(42, "runtime", "selection", 1));
            public Task<bool> RestoreAsync(long nativeWindowId, CancellationToken cancellationToken) => Task.FromResult(true);
        }
    }
}

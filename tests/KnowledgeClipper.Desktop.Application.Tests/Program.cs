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
    internal static class Program
    {
        private static int assertions;

        private static async Task<int> Main()
        {
            try
            {
                await ModuleActionsAreNormalized();
                await QuestionSessionUsesNormalizedResults();
                await SettingsStateComesFromRepository();
                await AllInvocationSourcesShareOneCommand();
                await QuestionPortFailureDoesNotLeaveRunning();
                await AcceptedQuestionIsObservedUntilSettled();
                await TypedQuestionProjectionStateMatrixDrivesView();
                await KeyboardFollowUpDispatchesExactlyOnce();
                await QuestionObservationTimeoutAndCancellationTerminate();
                await DuplicateQuestionCommandIsRejectedWhileInFlight();
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
            Assert(!result.Accepted && root.State.Snapshot().Question.Status == OperationStatus.Failed && root.State.Snapshot().Question.NoticeCode == "question-submit-IOException", "question command port exception reduces to a terminal failed state");
        }

        private static async Task AcceptedQuestionIsObservedUntilSettled()
        {
            var fake = new FakePorts();
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "task-observe", "accepted"));
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
            timeout.QuestionResults.Enqueue(new TaskCommandResult(true, "task-timeout", "accepted"));
            timeout.QuestionProjections.Enqueue(new QuestionProjection("task-timeout", OperationStatus.Running, 0, false, null, "running"));
            timeout.QuestionProjections.Enqueue(new QuestionProjection("task-timeout", OperationStatus.Running, 0, false, null, "running"));
            var timeoutRoot = timeout.Root(new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));
            await timeoutRoot.QuestionUseCases.SubmitInitialAsync("超时问题", CancellationToken.None);
            Assert(timeoutRoot.State.Snapshot().Question.Status == OperationStatus.Failed && timeoutRoot.State.Snapshot().Question.NoticeCode == "question-observation-timeout", "question observation timeout terminates instead of leaving Running");

            var cancelled = new FakePorts { BlockQuestionQuery = true };
            cancelled.QuestionResults.Enqueue(new TaskCommandResult(true, "task-cancel", "accepted"));
            using var source = new CancellationTokenSource();
            var cancelledRoot = cancelled.Root(new QuestionObservationPolicy(3, TimeSpan.Zero, TimeSpan.Zero));
            var pending = cancelledRoot.QuestionUseCases.SubmitInitialAsync("取消问题", source.Token);
            await cancelled.QuestionQueryEntered.Task;
            source.Cancel();
            await pending;
            Assert(cancelledRoot.State.Snapshot().Question.Status == OperationStatus.Cancelled && cancelledRoot.State.Snapshot().Question.NoticeCode == "question-observation-cancelled", "question observation cancellation terminates explicitly");
        }

        private static async Task DuplicateQuestionCommandIsRejectedWhileInFlight()
        {
            var fake = new FakePorts { BlockQuestionSubmit = true };
            fake.QuestionResults.Enqueue(new TaskCommandResult(true, "task-one", "accepted"));
            fake.QuestionProjections.Enqueue(new QuestionProjection("task-one", OperationStatus.WaitingInput, 0, false, null, "waiting"));
            var root = fake.Root(new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));
            var first = root.QuestionUseCases.SubmitInitialAsync("第一条", CancellationToken.None);
            await fake.QuestionSubmitEntered.Task;
            var duplicate = await root.QuestionUseCases.SubmitInitialAsync("重复 Enter", CancellationToken.None);
            fake.QuestionSubmitRelease.TrySetResult(true);
            await first;
            Assert(!duplicate.Accepted && duplicate.NoticeCode == "question-command-in-progress" && fake.QuestionSubmissions.Count == 1, "duplicate Enter is rejected idempotently while one question command is in flight");
        }

        private static void CaptureSessionFreezesExternalContext()
        {
            var source = new WindowActivationSnapshot(42, "runtime-id:1.2.3", "清晰选区", 7);
            var session = CaptureSession.Start("capture-session-1", DateTimeOffset.Parse("2026-09-02T04:00:00Z"), source);
            source = source with { NativeWindowId = 99, FocusedElementRuntimeId = "changed", Selection = "changed", ClipboardSequence = 8 };
            Assert(session.FrozenWindow.NativeWindowId == 42 && session.FrozenWindow.FocusedElementRuntimeId == "runtime-id:1.2.3" && session.FrozenWindow.Selection == "清晰选区" && session.FrozenWindow.ClipboardSequence == 7, "capture session freezes foreground focus selection and clipboard sequence before UI interaction");
        }

        private static void ApplicationContractsDoNotLeakTransportIdentity()
        {
            var forbidden = new[] { "Operation", "SchemaVersion", "WorkerId", "Subject", "IdempotencyKey", "ManifestPath", "Authority", "AuthorityHash", "AuthoritySha", "IpcVersion", "RawPath", "ArtifactPath", "FilePath" };
            var types = typeof(DesktopCompositionRoot).Assembly.GetTypes().Where(type => type.Namespace == "KnowledgeClipper.Desktop.Application.V3");
            var leaks = types.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(property => type.Name + "." + property.Name)).Where(name => forbidden.Any(item => name.EndsWith("." + item, StringComparison.OrdinalIgnoreCase))).ToArray();
            Assert(leaks.Length == 0, "Application DTOs contain no IPC version worker schema path authority hash or raw artifact path");
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

        private sealed class FakePorts : ITaskCommandPort, ITaskQueryPort, IModuleActionPort, IStageControlPort, IArtifactGateway, ISettingsRepository, ICredentialStore, IWindowActivationPort
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

            internal DesktopCompositionRoot Root(QuestionObservationPolicy policy = null) => new(this, this, this, this, this, this, this, this, policy);
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
            public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
            public Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken cancellationToken) { SettingsSaveCount++; Settings = settings with { Revision = "revision-committed" }; return Task.FromResult(Settings); }
            public Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken cancellationToken) => Task.FromResult(new CredentialMetadata(credentialId, false, false, "CurrentUser"));
            public Task StoreAsync(string credentialId, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<WindowActivationSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new WindowActivationSnapshot(42, "runtime", "selection", 1));
            public Task<bool> RestoreAsync(long nativeWindowId, CancellationToken cancellationToken) => Task.FromResult(true);
        }
    }
}

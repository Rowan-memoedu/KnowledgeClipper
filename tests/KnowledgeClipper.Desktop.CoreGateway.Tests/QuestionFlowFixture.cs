using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.TestFixtures
{
    // Deterministic port boundary. Forms and all Application reducers/use cases remain production.
    internal sealed class QuestionFlowFixture : ITaskCommandPort, ITaskQueryPort, IArtifactGateway, IFlashcardDraftPort,
        IModuleActionPort, IStageControlPort, ISettingsRepository, ICredentialStore, IWindowActivationPort
    {
        internal readonly List<QuestionSubmission> Submissions = new();
        internal readonly List<FlashcardDraftConfirmation> Confirmations = new();
        internal readonly Dictionary<string, QuestionProjection> Projections = new();
        internal readonly Dictionary<string, string> Answers = new();
        internal TaskCompletionSource<bool> SubmitGate { get; set; }
        internal TaskCompletionSource<QuestionProjection> QueryGate { get; set; }
        internal bool MissingSession { get; set; }
        internal string Reject { get; set; }
        internal int MakeCardsCount;
        internal int QueryCount;
        internal int Sequence;
        internal DesktopCompositionRoot Root() => new(this, this, this, this, this, this, this, this,
            new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero), null, null, null, null, this);

        public async Task<TaskCommandResult> SubmitQuestionAsync(QuestionSubmission submission, CancellationToken token)
        {
            Submissions.Add(submission);
            if (SubmitGate != null) await SubmitGate.Task.WaitAsync(token);
            if (Reject != null) return new(false, null, Reject);
            var op = "question-operation-" + ++Sequence;
            var session = submission.FollowUp ? submission.SessionId : "question-session-" + Sequence;
            var answer = new ArtifactHandle("answer-" + Sequence, "question-answer-text");
            Answers[answer.Id] = "Offline answer to: " + submission.Text;
            var draft = Draft(op, Sequence);
            Projections[op] = new QuestionProjection(op, OperationStatus.WaitingInput, Sequence, true, answer, "draft-ready")
            { FlashcardDraft = new FlashcardDraftProjection(FlashcardDraftStatus.AwaitingConfirmation, draft, Sequence, "draft-ready", true) };
            return new TaskCommandResult(true, op, "accepted") { SessionId = MissingSession ? null : session };
        }
        internal static FlashcardDraftRevision Draft(string op, int sequence, FlashcardDraftSource source = FlashcardDraftSource.Question) =>
            new(op, "token-" + sequence, sequence.ToString(), new string((char)('a' + sequence % 6), 64), source,
                new[] { new FlashcardDraftCard("card-" + sequence, "1", new string('b', 64), "Offline front " + sequence, "Offline back " + sequence) }, new[] { "Anki" });
        public Task<TaskCommandResult> MakeCardsAsync(MakeCardsSubmission submission, CancellationToken token) { MakeCardsCount++; throw new InvalidOperationException("automatic-make-cards-forbidden"); }
        public async Task<QuestionProjection> GetQuestionAsync(string taskId, CancellationToken token)
        { QueryCount++; return QueryGate == null ? Projections[taskId] : await QueryGate.Task.WaitAsync(token); }
        public Task<TaskProjection> GetRecentAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<T> ReadAsync<T>(ArtifactHandle artifact, CancellationToken token) => Task.FromResult((T)(object)new QuestionAnswerContent(Answers[artifact.Id]));
        public Task<ArtifactHandle> PublishAsync<T>(string kind, T value, CancellationToken token) => throw new NotSupportedException();
        public Task<FlashcardDraftCommandResult> ConfirmAsync(FlashcardDraftConfirmation binding, CancellationToken token)
        { Confirmations.Add(binding); return Task.FromResult(new FlashcardDraftCommandResult(true, FlashcardDraftStatus.Completed, "offline-confirmed")); }
        public Task<FlashcardDraftCommandResult> CancelAsync(FlashcardDraftConfirmation binding, CancellationToken token) => Task.FromResult(new FlashcardDraftCommandResult(true, FlashcardDraftStatus.Cancelled, "offline-cancelled"));
        public Task<FlashcardDraftProjection> GetAsync(string op, CancellationToken token) => Task.FromResult(Projections[op].FlashcardDraft);
        public Task<ModuleActionResult> ExecuteAsync(ModuleActionRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<StageControlResult> RequestPlaybackStopAsync(string taskId, CancellationToken token) => throw new NotSupportedException();
        public Task<SettingsSnapshot> LoadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken token) => throw new NotSupportedException();
        public Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken token) => throw new NotSupportedException();
        public Task StoreAsync(string credentialId, string secret, CancellationToken token) => throw new NotSupportedException();
        public Task<WindowActivationSnapshot> CaptureAsync(CancellationToken token) => Task.FromResult(new WindowActivationSnapshot(0, "offline", "", 0));
        public Task<bool> RestoreAsync(long nativeWindowId, CancellationToken token) => Task.FromResult(true);
    }
}

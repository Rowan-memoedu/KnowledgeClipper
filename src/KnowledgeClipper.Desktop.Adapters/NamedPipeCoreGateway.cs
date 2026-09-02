using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    internal sealed class NamedPipeCoreGateway : ITaskCommandPort, ITaskQueryPort, IModuleActionPort, IStageControlPort, IArtifactGateway
    {
        private readonly ISdkDomainFacade sdk;
        private readonly ConcurrentDictionary<string, SdkArtifact> artifacts = new(StringComparer.Ordinal);
        private string recentTaskId;

        internal NamedPipeCoreGateway(string pipeName, CoreV3ClientOptions options = null)
            : this(new SdkDomainFacade(pipeName, options)) { }

        internal NamedPipeCoreGateway(ISdkDomainFacade sdk)
        {
            this.sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
        }

        public async Task<TaskCommandResult> SubmitQuestionAsync(QuestionSubmission submission, CancellationToken cancellationToken)
        {
            if (submission == null || string.IsNullOrWhiteSpace(submission.Text)) return Rejected(null, "question-text-required");
            try
            {
                var input = await sdk.PublishTextAsync(submission.FollowUp ? SdkArtifactPurpose.QuestionFollowUp : SdkArtifactPurpose.Question, submission.Text, cancellationToken).ConfigureAwait(false);
                Register(input, submission.FollowUp ? "question-follow-up" : "question");
                var accepted = submission.FollowUp
                    ? await sdk.QuestionFollowUpAsync(submission.SessionId, input, cancellationToken).ConfigureAwait(false)
                    : await sdk.QuestionInitialAsync(input, Array.Empty<SdkArtifact>(), cancellationToken).ConfigureAwait(false);
                recentTaskId = accepted.OperationId;
                return new TaskCommandResult(true, accepted.OperationId, "accepted") { SessionId = accepted.SessionId };
            }
            catch (SdkDomainException exception) { return Rejected(null, exception.Code); }
            catch (OperationCanceledException) { return Rejected(null, "question-submit-cancelled"); }
        }

        public async Task<TaskCommandResult> MakeCardsAsync(MakeCardsSubmission submission, CancellationToken cancellationToken)
        {
            if (submission == null || string.IsNullOrWhiteSpace(submission.SessionId)) return Rejected(null, "question-session-required");
            var answerHandle = submission.Answer;
            if (answerHandle == null) return Rejected(null, "question-answer-artifact-unavailable");
            SdkArtifact answer;
            if (!artifacts.TryGetValue(answerHandle.Id, out answer)) return Rejected(null, "question-answer-artifact-unavailable");
            try
            {
                var accepted = await sdk.QuestionMakeCardsAsync(submission.SessionId, answer, cancellationToken).ConfigureAwait(false);
                recentTaskId = accepted.OperationId;
                return new TaskCommandResult(true, accepted.OperationId, "accepted") { SessionId = accepted.SessionId };
            }
            catch (SdkDomainException exception) { return Rejected(null, exception.Code); }
            catch (OperationCanceledException) { return Rejected(null, "question-make-cards-cancelled"); }
        }

        public async Task<QuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return new QuestionProjection(taskId, OperationStatus.Failed, 0, false, null, "question-task-required");
            try
            {
                var snapshot = await sdk.GetQuestionAsync(taskId, cancellationToken).ConfigureAwait(false);
                var answer = snapshot.Answer == null ? null : Handle(snapshot.Answer, "question-answer-text");
                return new QuestionProjection(taskId, snapshot.Status, snapshot.TurnCount, answer != null, answer, snapshot.NoticeCode)
                {
                    CardBundle = snapshot.CardBundle == null ? null : Handle(snapshot.CardBundle, "question-card-bundle"),
                    DestinationEvidence = snapshot.DestinationEvidence == null ? null : Handle(snapshot.DestinationEvidence, "destination-evidence")
                };
            }
            catch (SdkDomainException exception) { return new QuestionProjection(taskId, OperationStatus.Failed, 0, false, null, exception.Code); }
            catch (OperationCanceledException) { return new QuestionProjection(taskId, OperationStatus.Cancelled, 0, false, null, "question-query-cancelled"); }
        }

        public async Task<TaskProjection> GetRecentAsync(CancellationToken cancellationToken)
        {
            var taskId = recentTaskId;
            if (string.IsNullOrWhiteSpace(taskId)) return new TaskProjection(null, OperationStatus.Idle, null, "recent-task-unavailable");
            try
            {
                var snapshot = await sdk.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
                return new TaskProjection(taskId, snapshot.Status, null, snapshot.NoticeCode);
            }
            catch (SdkDomainException exception) { return new TaskProjection(taskId, OperationStatus.Failed, null, exception.Code); }
            catch (OperationCanceledException) { return new TaskProjection(taskId, OperationStatus.Cancelled, null, "task-query-cancelled"); }
        }

        public async Task<ModuleActionResult> ExecuteAsync(ModuleActionRequest request, CancellationToken cancellationToken)
        {
            if (request == null) return ModuleRejected("module-action-required");
            if (request.Kind != ModuleActionKind.AnkiProbe) return ModuleRejected("core-v3.typed-facade-unavailable");
            object value;
            var input = request.Arguments != null && request.Arguments.TryGetValue("configuration", out value) ? value as ArtifactHandle : null;
            SdkArtifact configuration;
            if (input == null || !artifacts.TryGetValue(input.Id, out configuration)) return ModuleRejected("anki-configuration-artifact-required");
            try
            {
                var accepted = await sdk.AnkiProbeAsync(configuration, cancellationToken).ConfigureAwait(false);
                var readiness = await sdk.ObserveAnkiReadinessAsync(accepted.OperationId, cancellationToken).ConfigureAwait(false);
                var output = readiness.Evidence == null ? null : Handle(readiness.Evidence, "anki-readiness-evidence");
                return new ModuleActionResult(true, readiness.Status, readiness.NoticeCode, new Dictionary<string, object>(), output, false, false)
                {
                    AnkiReadiness = new AnkiReadinessSummary(
                        readiness.CurrentTargetReady,
                        readiness.OptionalRoutesUninitialized,
                        readiness.CurrentTarget,
                        readiness.UninitializedRoutes)
                };
            }
            catch (SdkDomainException exception) { return ModuleRejected(exception.Code); }
            catch (OperationCanceledException) { return ModuleRejected("anki-probe-cancelled", OperationStatus.Cancelled); }
        }

        public Task<StageControlResult> RequestPlaybackStopAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult(new StageControlResult(false, "failed", "core-v3.stage-control-unavailable"));

        public async Task<T> ReadAsync<T>(ArtifactHandle artifact, CancellationToken cancellationToken)
        {
            SdkArtifact value;
            if (artifact == null || !artifacts.TryGetValue(artifact.Id, out value)) throw new InvalidOperationException("artifact-handle-unavailable");
            try
            {
                var content = await sdk.ReadArtifactAsync(value, cancellationToken).ConfigureAwait(false);
                if (typeof(T) == typeof(QuestionAnswerContent)) return (T)(object)new QuestionAnswerContent(new UTF8Encoding(false, true).GetString(content));
                if (typeof(T) == typeof(string)) return (T)(object)new UTF8Encoding(false, true).GetString(content);
                if (typeof(T) == typeof(byte[])) return (T)(object)content;
                throw new NotSupportedException("artifact-content-type-unsupported");
            }
            catch (SdkDomainException exception) { throw new InvalidOperationException(exception.Code, exception); }
        }

        public async Task<ArtifactHandle> PublishAsync<T>(string kind, T value, CancellationToken cancellationToken)
        {
            var text = value switch
            {
                string plain => plain,
                QuestionAnswerContent answer => answer.Text,
                _ => throw new NotSupportedException("artifact-content-type-unsupported")
            };
            var purpose = Purpose(kind);
            try
            {
                var artifact = await sdk.PublishTextAsync(purpose, text, cancellationToken).ConfigureAwait(false);
                return Register(artifact, kind);
            }
            catch (SdkDomainException exception) { throw new InvalidOperationException(exception.Code, exception); }
        }

        private ArtifactHandle Register(SdkArtifact artifact, string kind)
        {
            artifacts[artifact.Token] = artifact;
            return new ArtifactHandle(artifact.Token, kind);
        }

        private ArtifactHandle Handle(SdkArtifact artifact, string kind)
        {
            artifacts[artifact.Token] = artifact;
            return new ArtifactHandle(artifact.Token, kind);
        }

        private static SdkArtifactPurpose Purpose(string kind)
        {
            return kind switch
            {
                "question" => SdkArtifactPurpose.Question,
                "question-context" => SdkArtifactPurpose.QuestionContext,
                "question-follow-up" => SdkArtifactPurpose.QuestionFollowUp,
                "question-answer-text" => SdkArtifactPurpose.QuestionAnswer,
                "anki-configuration" => SdkArtifactPurpose.AnkiConfiguration,
                _ => throw new NotSupportedException("artifact-kind-unsupported")
            };
        }

        private static TaskCommandResult Rejected(string taskId, string notice) => new(false, taskId, notice);
        private static ModuleActionResult ModuleRejected(string notice, OperationStatus status = OperationStatus.Failed) => new(false, status, notice, new Dictionary<string, object>(), null, false, false);
    }

    internal enum SdkArtifactPurpose { Question, QuestionContext, QuestionFollowUp, QuestionAnswer, AnkiConfiguration }
    internal sealed record SdkArtifact(string Token, object Native);
    internal sealed record SdkSubmission(string OperationId, string SessionId);
    internal sealed record SdkProjection(OperationStatus Status, string NoticeCode);
    internal sealed record SdkQuestionProjection(
        OperationStatus Status,
        string NoticeCode,
        int TurnCount,
        SdkArtifact Answer,
        SdkArtifact CardBundle,
        SdkArtifact DestinationEvidence);
    internal sealed record SdkAnkiReadiness(
        OperationStatus Status,
        string NoticeCode,
        bool CurrentTargetReady,
        bool OptionalRoutesUninitialized,
        string CurrentTarget,
        IReadOnlyList<string> UninitializedRoutes,
        SdkArtifact Evidence);

    internal interface ISdkDomainFacade
    {
        Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken cancellationToken);
        Task<byte[]> ReadArtifactAsync(SdkArtifact artifact, CancellationToken cancellationToken);
        Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, CancellationToken cancellationToken);
        Task<SdkAnkiReadiness> ObserveAnkiReadinessAsync(string operationId, CancellationToken cancellationToken);
        Task<SdkSubmission> QuestionInitialAsync(SdkArtifact question, IReadOnlyList<SdkArtifact> context, CancellationToken cancellationToken);
        Task<SdkSubmission> QuestionFollowUpAsync(string sessionId, SdkArtifact followUp, CancellationToken cancellationToken);
        Task<SdkSubmission> QuestionMakeCardsAsync(string sessionId, SdkArtifact answer, CancellationToken cancellationToken);
        Task<SdkQuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken);
        Task<SdkProjection> GetTaskAsync(string taskId, CancellationToken cancellationToken);
    }

    internal sealed class SdkDomainException : Exception
    {
        internal SdkDomainException(string code, Exception inner) : base(code, inner) { Code = code; }
        internal string Code { get; }
    }

    internal sealed class SdkDomainFacade : ISdkDomainFacade
    {
        private readonly CoreV3Client client;
        private long requestSequence;

        internal SdkDomainFacade(string pipeName, CoreV3ClientOptions options)
        {
            client = new CoreV3Client(pipeName, options);
        }

        public Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken cancellationToken)
            => Translate(async () => Wrap(await client.PublishTextArtifactAsync(new PublishTextArtifactRequest(NextRequestId(), Map(purpose), text), cancellationToken).ConfigureAwait(false)));

        public Task<byte[]> ReadArtifactAsync(SdkArtifact artifact, CancellationToken cancellationToken)
            => Translate(async () => (await client.ReadArtifactAsync(Unwrap(artifact), null, cancellationToken).ConfigureAwait(false)).Content);

        public Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, CancellationToken cancellationToken)
            => Translate(async () => Convert(await client.AnkiProbeAsync(new AnkiProbeRequest(NextRequestId(), Unwrap(configuration)), cancellationToken).ConfigureAwait(false)));

        public Task<SdkAnkiReadiness> ObserveAnkiReadinessAsync(string operationId, CancellationToken cancellationToken)
            => Task.FromResult(new SdkAnkiReadiness(OperationStatus.Unknown, "core-v3.typed-readiness-unavailable", false, false, null, Array.Empty<string>(), null));

        public Task<SdkSubmission> QuestionInitialAsync(SdkArtifact question, IReadOnlyList<SdkArtifact> context, CancellationToken cancellationToken)
        {
            var requestId = NextRequestId();
            return Translate(async () => ConvertQuestion(await client.QuestionInitialAsync(new QuestionInitialRequest(requestId, Unwrap(question), context.Select(Unwrap).ToArray()), cancellationToken).ConfigureAwait(false), requestId));
        }

        public Task<SdkSubmission> QuestionFollowUpAsync(string sessionId, SdkArtifact followUp, CancellationToken cancellationToken)
            => Translate(async () => ConvertQuestion(await client.QuestionFollowUpAsync(new QuestionFollowUpRequest(NextRequestId(), sessionId, Unwrap(followUp)), cancellationToken).ConfigureAwait(false), sessionId));

        public Task<SdkSubmission> QuestionMakeCardsAsync(string sessionId, SdkArtifact answer, CancellationToken cancellationToken)
            => Translate(async () => ConvertQuestion(await client.QuestionMakeCardsAsync(new QuestionMakeCardsRequest(NextRequestId(), sessionId, Unwrap(answer)), cancellationToken).ConfigureAwait(false), sessionId));

        public Task<SdkQuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken)
            => Translate(async () => { var value = await client.GetQuestionProjectionAsync(taskId, cancellationToken).ConfigureAwait(false); return new SdkQuestionProjection(Map(value.State), value.Notice, 0, null, null, null); });

        public Task<SdkProjection> GetTaskAsync(string taskId, CancellationToken cancellationToken)
            => Translate(async () => { var value = await client.GetTaskProjectionAsync(taskId, cancellationToken).ConfigureAwait(false); return new SdkProjection(Map(value.State), value.Notice); });

        private string NextRequestId() => "desktop-" + Interlocked.Increment(ref requestSequence).ToString("x16") + "-" + Guid.NewGuid().ToString("N");
        private static SdkArtifact Wrap(TypedArtifactHandle value) => new(value.Token, value);
        private static TypedArtifactHandle Unwrap(SdkArtifact value) => value.Native as TypedArtifactHandle ?? throw new InvalidOperationException("sdk-artifact-handle-invalid");
        private static SdkSubmission Convert(OperationSubmission value) => new(value.OperationId, null);
        private static SdkSubmission ConvertQuestion(OperationSubmission value, string sessionId) => new(value.OperationId, sessionId);
        private static ArtifactPurpose Map(SdkArtifactPurpose value) => value switch
        {
            SdkArtifactPurpose.Question => ArtifactPurpose.Question,
            SdkArtifactPurpose.QuestionContext => ArtifactPurpose.QuestionContext,
            SdkArtifactPurpose.QuestionFollowUp => ArtifactPurpose.QuestionFollowUp,
            SdkArtifactPurpose.QuestionAnswer => ArtifactPurpose.QuestionAnswer,
            SdkArtifactPurpose.AnkiConfiguration => throw new InvalidOperationException("core-v3.foundation.2-does-not-expose-anki-configuration-publication"),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        private static OperationStatus Map(QuestionProjectionState value) => value switch { QuestionProjectionState.Running => OperationStatus.Running, QuestionProjectionState.WaitingInput => OperationStatus.WaitingInput, QuestionProjectionState.Succeeded => OperationStatus.Succeeded, QuestionProjectionState.Failed => OperationStatus.Failed, QuestionProjectionState.Cancelled => OperationStatus.Cancelled, _ => OperationStatus.Unknown };
        private static OperationStatus Map(TaskProjectionState value) => value switch { TaskProjectionState.Running => OperationStatus.Running, TaskProjectionState.Succeeded => OperationStatus.Succeeded, TaskProjectionState.Failed => OperationStatus.Failed, TaskProjectionState.Cancelled => OperationStatus.Cancelled, _ => OperationStatus.Unknown };

        private static async Task<T> Translate<T>(Func<Task<T>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (CoreV3OperationException exception) { throw new SdkDomainException(exception.Code, exception); }
            catch (CoreV3ClientException exception) { throw new SdkDomainException(exception.Code, exception); }
            catch (QuestionObservationException exception) { throw new SdkDomainException(exception.Code, exception); }
        }
    }
}

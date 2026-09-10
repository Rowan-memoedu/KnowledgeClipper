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
    internal sealed partial class NamedPipeCoreGateway : ITaskCommandPort, ITaskQueryPort, IModuleActionPort, IStageControlPort, IArtifactGateway, IReadToAnkiInputArtifactPort, IReadToAnkiPort, IFlashcardDraftPort, IReadPlaybackControlPort
    {
        private readonly ISdkDomainFacade sdk;
        private readonly Func<string> readToAnkiConfiguration;
        private readonly Func<SdkLanguageCardSettings> readLanguageCardSettings;
        private readonly ConcurrentDictionary<string, SdkArtifact> artifacts = new(StringComparer.Ordinal);
        private string recentTaskId;
        private readonly ConcurrentDictionary<string, bool> questionTasks = new(StringComparer.Ordinal);

        internal NamedPipeCoreGateway(string pipeName, Func<string> readToAnkiConfiguration = null, Func<SdkLanguageCardSettings> readLanguageCardSettings = null, CoreV3ClientOptions options = null,
            Func<CoreV3Client, CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freezeDestinations = null)
            : this(new SdkDomainFacade(pipeName, options, readToAnkiConfiguration, freezeDestinations), readToAnkiConfiguration, readLanguageCardSettings) { }

        internal NamedPipeCoreGateway(ISdkDomainFacade sdk, Func<string> readToAnkiConfiguration = null, Func<SdkLanguageCardSettings> readLanguageCardSettings = null)
        {
            this.sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
            this.readToAnkiConfiguration = readToAnkiConfiguration;
            this.readLanguageCardSettings = readLanguageCardSettings;
        }

        public async Task<ArtifactHandle> PublishCaptureTextAsync(ImmutablePlainText value, CancellationToken cancellationToken)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.Text)) throw new InvalidOperationException("read-to-anki-text-required");
            try
            {
                var purpose = value.IsContext ? SdkArtifactPurpose.QuestionContext : SdkArtifactPurpose.CaptureText;
                return Register(await sdk.PublishTextAsync(purpose, value.Text, cancellationToken).ConfigureAwait(false), value.IsContext ? "question-context" : "capture-text");
            }
            catch (SdkDomainException exception) { throw new InvalidOperationException(exception.Code, exception); }
        }

        public async Task<ReadToAnkiSubmissionResult> SubmitAsync(ReadToAnkiSubmission submission, CancellationToken cancellationToken)
        {
            if (submission?.SourceText == null) return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-source-artifact-required");
            if (!artifacts.TryGetValue(submission.SourceText.Id, out var capture)) return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-source-artifact-unavailable");
            if (!sdk.UsesDestinationSettings && readToAnkiConfiguration == null) return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-anki-configuration-unavailable");
            try
            {
                SdkArtifact configuration = null;
                if (!sdk.UsesDestinationSettings)
                {
                    var configurationJson = readToAnkiConfiguration();
                    if (string.IsNullOrWhiteSpace(configurationJson)) return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-anki-configuration-required");
                    configuration = await sdk.PublishTextAsync(SdkArtifactPurpose.AnkiConfiguration, configurationJson, cancellationToken).ConfigureAwait(false);
                    Register(configuration, "anki-configuration");
                }
                var orderedContext = submission.OrderedContextSegments.Select(item => artifacts.TryGetValue(item.Id, out var artifact) ? artifact : null).ToArray();
                if (orderedContext.Any(item => item == null)) return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-context-artifact-unavailable");
                var roleBoundContext = submission.RoleBoundContextSegments.Select(item => new SdkContextSegment(artifacts.TryGetValue(item.Artifact.Id, out var artifact) ? artifact : null, item.Role, item.Order)).ToArray();
                if (roleBoundContext.Any(item => item.Artifact == null) || roleBoundContext.Length != orderedContext.Length || !roleBoundContext.Select(item => item.Artifact.Token).SequenceEqual(orderedContext.Select(item => item.Token))) return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-context-role-binding-invalid");
                var accepted = await sdk.ReadToAnkiAsync(capture, orderedContext, roleBoundContext, submission.TargetText ?? string.Empty,
                    submission.ContextRequested, submission.ContextGranularity, submission.SourceWindow,
                    configuration, readLanguageCardSettings?.Invoke(), submission.IncludeSpeech, cancellationToken).ConfigureAwait(false);
                recentTaskId = accepted.OperationId;
                return new ReadToAnkiSubmissionResult(accepted.Accepted, accepted.OperationId, accepted.Accepted ? "read-to-anki.accepted" : "read-to-anki.rejected") { CommandId = accepted.CommandId, AcceptedSequence = accepted.AcceptedSequence };
            }
            catch (SdkDomainException exception) { return new ReadToAnkiSubmissionResult(false, null, exception.Code); }
            catch (OperationCanceledException) { return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-submit-cancelled"); }
            catch (Exception) { return new ReadToAnkiSubmissionResult(false, null, "read-to-anki-configuration-invalid"); }
        }

        public async Task<ReadToAnkiProjection> GetProjectionAsync(string operationId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return new ReadToAnkiProjection(operationId, ReadToAnkiPlaybackStatus.Failed, ReadToAnkiPipelineStatus.Failed, false, "read-to-anki-operation-required");
            try
            {
                var value = await sdk.GetReadToAnkiProjectionAsync(operationId, cancellationToken).ConfigureAwait(false);
                var result = new ReadToAnkiProjection(operationId, SdkReadToAnkiMapping.Map(value.Playback), SdkReadToAnkiMapping.Map(value.Pipeline), value.AttentionRequired, value.NoticeCode)
                {
                    ActionState = value.ActionState, ActiveCommandId = value.ActiveCommandId,
                    TerminalCommandId = value.TerminalCommandId, TerminalSequence = value.TerminalSequence
                };
                if (value.CardResolution != null)
                    result = result with { CardResolution = new KnowledgeClipper.Desktop.Application.V3.CardResolutionPrompt(value.CardResolution.TargetToken, value.CardResolution.Revision, value.CardResolution.DecisionId, value.CardResolution.ReasonCode, value.CardResolution.Actions,
                        value.CardResolution.CandidateOccurrences.Select(item => new CardResolutionOccurrence(item.SegmentId, item.Order, item.Start, item.End, item.ExactText, item.SourceSha256)).ToArray()) };
                if (value.Draft != null)
                    result = result with { FlashcardDraft = new FlashcardDraftProjection(MapDraftPipeline(value.Pipeline),
                        ProjectDraft(operationId, value.Draft, FlashcardDraftSource.ReadAloud, value.DestinationSnapshot, value.Destinations),
                        value.Cursor, value.NoticeCode, value.AttentionRequired) };
                return result;
            }
            // A local observation error is not a Core terminal action. Do not invent
            // command IDs to get it past the Application's stale-projection guard.
            catch (SdkDomainException exception) { throw new InvalidOperationException("read-to-anki-query-unavailable", exception); }
        }

        public async Task<ReadToAnkiSubmissionResult> ResumeLanguageChoiceAsync(ReadToAnkiChoiceSubmission submission, CancellationToken cancellationToken)
        {
            try { var value = await sdk.ChooseLanguageCardAsync(submission.OperationId, submission.Choice, cancellationToken).ConfigureAwait(false); return new ReadToAnkiSubmissionResult(value.Accepted, value.OperationId, value.Accepted ? "read-to-anki.language-choice-accepted" : "read-to-anki.language-choice-rejected") { CommandId = value.CommandId, AcceptedSequence = value.AcceptedSequence }; }
            catch (SdkDomainException exception) { return new ReadToAnkiSubmissionResult(false, submission?.OperationId, exception.Code); }
        }

        public async Task<ReadToAnkiSubmissionResult> ResolveCardAsync(CardResolutionSubmission submission, CancellationToken cancellationToken)
        {
            try { var value = await sdk.ResolveCardAsync(submission, cancellationToken).ConfigureAwait(false); return new ReadToAnkiSubmissionResult(value.Accepted, value.OperationId, value.Accepted ? "read-to-anki.card-resolution-accepted" : "read-to-anki.card-resolution-rejected") { CommandId = value.CommandId, AcceptedSequence = value.AcceptedSequence }; }
            catch (SdkDomainException exception) { return new ReadToAnkiSubmissionResult(false, submission?.OperationId, exception.Code); }
        }

        public async Task<ReadToAnkiPlaybackCommandResult> ReplayAsync(string operationId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return new ReadToAnkiPlaybackCommandResult(false, "read-to-anki-replay-operation-required");
            try
            {
                var accepted = await sdk.ReplayReadToAnkiAsync(operationId, cancellationToken).ConfigureAwait(false);
                return new ReadToAnkiPlaybackCommandResult(accepted.Accepted, accepted.Accepted ? "read-to-anki.replay-accepted" : "read-to-anki.replay-rejected")
                { OperationId = accepted.OperationId, CommandId = accepted.CommandId, AcceptedSequence = accepted.AcceptedSequence };
            }
            catch (SdkDomainException exception) { return new ReadToAnkiPlaybackCommandResult(false, exception.Code) { OutcomeUnknown = true }; }
            catch (OperationCanceledException) { return new ReadToAnkiPlaybackCommandResult(false, "read-to-anki-replay-observation-cancelled") { OutcomeUnknown = true }; }
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
                return new TaskCommandResult(accepted.Accepted, accepted.OperationId, accepted.Accepted ? "accepted" : "question.rejected") { SessionId = accepted.SessionId };
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
                return new TaskCommandResult(accepted.Accepted, accepted.OperationId, accepted.Accepted ? "accepted" : "question.rejected") { SessionId = accepted.SessionId };
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
                questionTasks[taskId] = true;
                var answer = snapshot.Answer == null ? null : Handle(snapshot.Answer, "question-answer-text");
                return new QuestionProjection(taskId, snapshot.Status, snapshot.TurnCount, answer != null, answer, snapshot.NoticeCode)
                {
                    CardBundle = snapshot.CardBundle == null ? null : Handle(snapshot.CardBundle, "question-card-bundle"),
                    DestinationEvidence = snapshot.DestinationEvidence == null ? null : Handle(snapshot.DestinationEvidence, "destination-evidence"),
                    FlashcardDraft = snapshot.Draft == null ? null : new FlashcardDraftProjection(MapQuestionDraftStatus(snapshot.Status),
                        ProjectDraft(taskId, snapshot.Draft, FlashcardDraftSource.MakeCards, snapshot.DestinationSnapshot, snapshot.Destinations), snapshot.Cursor, snapshot.NoticeCode, snapshot.Status == OperationStatus.WaitingInput)
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
            if (request.Kind is ModuleActionKind.RemNoteDiscoverTarget or ModuleActionKind.RemNoteProbe or ModuleActionKind.RemNoteRestoreBinding)
                return await ExecuteRemNoteAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Kind != ModuleActionKind.AnkiProbe) return ModuleRejected("core-v3.typed-facade-unavailable");
            object value;
            var input = request.Arguments != null && request.Arguments.TryGetValue("configuration", out value) ? value as ArtifactHandle : null;
            var selectedRoute = request.Arguments != null && request.Arguments.TryGetValue("selectedRoute", out value) ? value as string : null;
            if (string.IsNullOrWhiteSpace(selectedRoute)) return ModuleRejected("anki-probe-target-selection-required");
            SdkArtifact configuration;
            if (input == null || !artifacts.TryGetValue(input.Id, out configuration)) return ModuleRejected("anki-configuration-artifact-required");
            try
            {
                var accepted = await sdk.AnkiProbeAsync(configuration, selectedRoute, cancellationToken).ConfigureAwait(false);
                if (!accepted.Accepted || string.IsNullOrWhiteSpace(accepted.OperationId)) return ModuleRejected("anki-probe-rejected");
                var readiness = await sdk.ObserveAnkiReadinessAsync(accepted.OperationId, cancellationToken).ConfigureAwait(false);
                var output = readiness.Evidence == null ? null : Handle(readiness.Evidence, "anki-readiness-evidence");
                return new ModuleActionResult(true, readiness.Status, readiness.NoticeCode, new Dictionary<string, object>(), output, false, false)
                {
                    AnkiReadiness = new KnowledgeClipper.Desktop.Application.V3.AnkiReadinessSummary(
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

        public async Task<FlashcardDraftCommandResult> ConfirmAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken)
        {
            try { var value = await sdk.ConfirmDraftAsync(confirmation, cancellationToken).ConfigureAwait(false); return new FlashcardDraftCommandResult(value.Accepted, value.Status, value.NoticeCode) { CommandId = value.CommandId, AcceptedSequence = value.AcceptedSequence }; }
            catch (SdkDomainException exception) { return new FlashcardDraftCommandResult(false, FlashcardDraftStatus.Failed, exception.Code); }
        }

        public async Task<FlashcardDraftCommandResult> CancelAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken)
        {
            try { var value = await sdk.CancelDraftAsync(confirmation, cancellationToken).ConfigureAwait(false); return new FlashcardDraftCommandResult(value.Accepted, value.Status, value.NoticeCode); }
            catch (SdkDomainException exception) { return new FlashcardDraftCommandResult(false, FlashcardDraftStatus.Failed, exception.Code); }
        }

        public async Task<FlashcardDraftProjection> GetAsync(string operationId, CancellationToken cancellationToken)
        {
            try
            {
                if (questionTasks.ContainsKey(operationId))
                {
                    var question = await GetQuestionAsync(operationId, cancellationToken).ConfigureAwait(false);
                    return question.FlashcardDraft ?? new FlashcardDraftProjection(MapQuestionDraftStatus(question.Status), null, 0, question.NoticeCode, true);
                }
                var value = await sdk.GetReadToAnkiProjectionAsync(operationId, cancellationToken).ConfigureAwait(false);
                var status = MapDraftPipeline(value.Pipeline);
                var draft = value.Draft == null ? null : ProjectDraft(operationId, value.Draft, FlashcardDraftSource.ReadAloud, value.DestinationSnapshot, value.Destinations);
                return new FlashcardDraftProjection(status, draft, value.Cursor, value.NoticeCode, value.AttentionRequired);
            }
            catch (SdkDomainException exception) { return new FlashcardDraftProjection(FlashcardDraftStatus.Failed, null, 0, exception.Code, true); }
        }

        private static FlashcardDraftStatus MapDraftPipeline(SdkReadToAnkiPipeline value) => value switch
        {
            SdkReadToAnkiPipeline.AwaitingCardConfirmation => FlashcardDraftStatus.AwaitingConfirmation,
            SdkReadToAnkiPipeline.WritingAnki => FlashcardDraftStatus.Exporting,
            SdkReadToAnkiPipeline.Completed => FlashcardDraftStatus.Completed,
            SdkReadToAnkiPipeline.Cancelled => FlashcardDraftStatus.Cancelled,
            SdkReadToAnkiPipeline.PartiallyFailed or SdkReadToAnkiPipeline.Failed => FlashcardDraftStatus.Failed,
            SdkReadToAnkiPipeline.UnknownExternalState => FlashcardDraftStatus.Unknown,
            _ => FlashcardDraftStatus.Processing
        };

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

        private FlashcardDraftRevision ProjectDraft(string operationId, SdkFlashcardDraft draft, FlashcardDraftSource source,
            TypedArtifactHandle snapshot, IReadOnlyList<DestinationProjection> destinations)
        {
            if (sdk.UsesDestinationSettings && snapshot == null) throw new SdkDomainException("destination-snapshot-unavailable", null);
            var values = destinations ?? Array.Empty<DestinationProjection>();
            return new FlashcardDraftRevision(operationId, draft.Token, draft.Revision, draft.Sha256, source,
                draft.Cards.Select(card => new KnowledgeClipper.Desktop.Application.V3.FlashcardDraftCard(card.CardId, card.Revision, card.Sha256, card.Front, card.Back, card.Template)).ToArray(),
                snapshot == null ? new[] { "Anki" } : values.Select(x => x.Kind == "anki" ? "Anki" : x.Kind == "remnote" ? "RemNote" : x.Kind).ToArray())
            {
                DestinationSnapshot = snapshot == null ? null : Handle(new SdkArtifact(snapshot.Token, snapshot), "destination-snapshot"),
                DestinationOutcomes = values.Select(x => new CardDestinationOutcome(x.DestinationId, x.Kind, x.State, x.ReasonCode,
                    NativeHandle(x.SourceCardArtifact, "card-bundle"), NativeHandle(x.ConfigurationSnapshot, "destination-configuration"),
                    NativeHandle(x.Evidence, "destination-evidence"), x.ExternalStateKnown, x.ExternalWriteCount)).ToArray()
            };
        }
        private ArtifactHandle NativeHandle(TypedArtifactHandle value, string kind) => value == null ? null : Handle(new SdkArtifact(value.Token, value), kind);
        private static FlashcardDraftStatus MapQuestionDraftStatus(OperationStatus status) => status switch
        {
            OperationStatus.WaitingInput => FlashcardDraftStatus.AwaitingConfirmation, OperationStatus.Succeeded => FlashcardDraftStatus.Completed,
            OperationStatus.Cancelled => FlashcardDraftStatus.Cancelled, OperationStatus.Failed => FlashcardDraftStatus.Failed,
            OperationStatus.Running => FlashcardDraftStatus.Processing, _ => FlashcardDraftStatus.Unknown
        };

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

    internal enum SdkArtifactPurpose { CaptureText, Question, QuestionContext, QuestionFollowUp, QuestionAnswer, AnkiConfiguration }
    internal sealed record SdkArtifact(string Token, object Native);
    internal sealed record SdkContextSegment(SdkArtifact Artifact, string Role, int Order);
    internal sealed record SdkSubmission(string OperationId, string SessionId, bool Accepted = true);
    internal sealed record SdkProjection(OperationStatus Status, string NoticeCode);
    internal sealed record SdkQuestionProjection(
        OperationStatus Status,
        string NoticeCode,
        int TurnCount,
        SdkArtifact Answer,
        SdkArtifact CardBundle,
        SdkArtifact DestinationEvidence,
        long Cursor = 0,
        SdkFlashcardDraft Draft = null)
    {
        internal TypedArtifactHandle DestinationSnapshot { get; init; }
        internal IReadOnlyList<DestinationProjection> Destinations { get; init; }
    }
    internal sealed record SdkAnkiReadiness(
        OperationStatus Status,
        string NoticeCode,
        bool CurrentTargetReady,
        bool OptionalRoutesUninitialized,
        string CurrentTarget,
        IReadOnlyList<string> UninitializedRoutes,
        SdkArtifact Evidence);
    internal sealed record SdkReadToAnkiSubmission(string OperationId, bool Accepted, string CommandId = null, long AcceptedSequence = 0);
    internal sealed record SdkReadToAnkiProjection(
        SdkReadToAnkiPlayback Playback,
        SdkReadToAnkiPipeline Pipeline,
        bool AttentionRequired,
        string NoticeCode,
        long Cursor = 0,
        SdkFlashcardDraft Draft = null,
        SdkCardResolutionPrompt CardResolution = null)
    {
        internal TypedArtifactHandle DestinationSnapshot { get; init; }
        internal IReadOnlyList<DestinationProjection> Destinations { get; init; }
        internal string ActionState { get; init; }
        internal string ActiveCommandId { get; init; }
        internal string TerminalCommandId { get; init; }
        internal long TerminalSequence { get; init; }
    }
    internal sealed record SdkCardResolutionOccurrence(string SegmentId, int Order, int Start, int End, string ExactText, string SourceSha256);
    internal sealed record SdkCardResolutionPrompt(string TargetToken, string Revision, string DecisionId, string ReasonCode, IReadOnlyList<string> Actions, IReadOnlyList<SdkCardResolutionOccurrence> CandidateOccurrences);
    internal sealed record SdkFlashcardDraft(string Token, string Revision, string Sha256, IReadOnlyList<SdkFlashcardDraftCard> Cards);
    internal sealed record SdkFlashcardDraftCard(string CardId, string Revision, string Sha256, string Front, string Back, string Template = "basic_qa");
    internal sealed record SdkLanguageCardSettings(string Revision, string DefaultChoice);
    internal sealed record SdkDraftCommand(bool Accepted, FlashcardDraftStatus Status, string NoticeCode, string CommandId = null, long AcceptedSequence = 0);
    internal enum SdkReadToAnkiPlayback { NotRequested, Accepted, Playing, IdleReplayable, Failed, Stopped, Unknown }
    internal enum SdkReadToAnkiPipeline { Accepted, Reviewing, WaitingUserChoice, AwaitingCardResolution, AwaitingCardConfirmation, CreatingCards, WritingAnki, Completed, Cancelled, PartiallyFailed, Failed, UnknownExternalState }

    internal interface ISdkDomainFacade
    {
        bool UsesDestinationSettings => false; // Explicit legacy constructors retain the Anki-only contract.
        Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken cancellationToken);
        Task<byte[]> ReadArtifactAsync(SdkArtifact artifact, CancellationToken cancellationToken);
        Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, CancellationToken cancellationToken)
            => throw new NotSupportedException("anki-probe-target-selection-required");
        Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, string selectedRoute, CancellationToken cancellationToken)
            => throw new NotSupportedException("anki-probe-explicit-route-adapter-required");
        Task<SdkAnkiReadiness> ObserveAnkiReadinessAsync(string operationId, CancellationToken cancellationToken);
        Task<SdkSubmission> QuestionInitialAsync(SdkArtifact question, IReadOnlyList<SdkArtifact> context, CancellationToken cancellationToken);
        Task<SdkSubmission> QuestionFollowUpAsync(string sessionId, SdkArtifact followUp, CancellationToken cancellationToken);
        Task<SdkSubmission> QuestionMakeCardsAsync(string sessionId, SdkArtifact answer, CancellationToken cancellationToken);
        Task<SdkQuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken);
        Task<SdkProjection> GetTaskAsync(string taskId, CancellationToken cancellationToken);
        Task<SdkReadToAnkiSubmission> ReadToAnkiAsync(SdkArtifact capture, IReadOnlyList<SdkArtifact> orderedContext, IReadOnlyList<SdkContextSegment> roleBoundContext, string targetText, bool contextRequested, string contextGranularity, string sourceWindow, SdkArtifact configuration, SdkLanguageCardSettings languageCards, bool includeSpeech, CancellationToken cancellationToken);
        Task<SdkReadToAnkiSubmission> ChooseLanguageCardAsync(string operationId, LanguagePracticeChoice choice, CancellationToken cancellationToken);
        Task<SdkReadToAnkiSubmission> ResolveCardAsync(CardResolutionSubmission submission, CancellationToken cancellationToken);
        Task<SdkReadToAnkiProjection> GetReadToAnkiProjectionAsync(string operationId, CancellationToken cancellationToken);
        Task<SdkReadToAnkiSubmission> ReplayReadToAnkiAsync(string operationId, CancellationToken cancellationToken);
        Task<SdkDraftCommand> ConfirmDraftAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken);
        Task<SdkDraftCommand> CancelDraftAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken);
    }

    internal sealed class SdkDomainException : Exception
    {
        internal SdkDomainException(string code, Exception inner) : base(code, inner) { Code = code; }
        internal string Code { get; }
    }

    internal sealed partial class SdkDomainFacade : ISdkDomainFacade, IReadPlaybackControlPort
    {
        private readonly CoreV3Client client;
        private readonly QuestionSessionSdkBridge questionSessions;
        private readonly ConcurrentDictionary<string, KnowledgeClipper.Core.V3.Client.FlashcardDraft> projectedDrafts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> projectedDraftSources = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, LanguageCardChoicePrompt> languagePrompts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, KnowledgeClipper.Core.V3.Client.CardResolutionPrompt> resolutionPrompts = new(StringComparer.Ordinal);
        private readonly ReadToAnkiOperationAuthorizationCoordinator operationAuthorizations = new();
        private long requestSequence;

        internal SdkDomainFacade(string pipeName, CoreV3ClientOptions options, Func<string> questionConfiguration = null,
            Func<CoreV3Client, CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freezeDestinations = null)
        {
            client = new CoreV3Client(pipeName, options);
            if (freezeDestinations != null) InitializeDestinations(freezeDestinations);
            questionSessions = new QuestionSessionSdkBridge(questionConfiguration,
                (json, token) => client.PublishTextArtifactAsync(new PublishTextArtifactRequest(NextRequestId(), ArtifactPurpose.AnkiConfiguration, json), token),
                (request, token) => client.QuestionInitialAsync(request, token),
                (request, token) => client.QuestionFollowUpAsync(request, token),
                (request, token) => client.QuestionMakeCardsAsync(request, token));
        }

        public Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken cancellationToken)
            => Translate(async () => Wrap(await (UsesDestinationSettings ? destinationCalls.Publish(new PublishTextArtifactRequest(NextRequestId(), Map(purpose), text), cancellationToken) : client.PublishTextArtifactAsync(new PublishTextArtifactRequest(NextRequestId(), Map(purpose), text), cancellationToken)).ConfigureAwait(false)));

        public Task<byte[]> ReadArtifactAsync(SdkArtifact artifact, CancellationToken cancellationToken)
            => Translate(async () => UsesDestinationSettings ? await destinationCalls.ReadArtifact(Unwrap(artifact), cancellationToken).ConfigureAwait(false) : (await client.ReadArtifactAsync(Unwrap(artifact), null, cancellationToken).ConfigureAwait(false)).Content);

        private readonly ConcurrentDictionary<string, string> ankiProbeRoutes = new(StringComparer.Ordinal);

        public Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, string selectedRoute, CancellationToken cancellationToken)
            => Translate(async () =>
            {
                if (string.IsNullOrWhiteSpace(selectedRoute)) throw new InvalidOperationException("anki-probe-target-selection-required");
                var accepted = Convert(await client.AnkiProbeAsync(new AnkiProbeRequest(NextRequestId(), Unwrap(configuration), selectedRoute), cancellationToken).ConfigureAwait(false));
                if (accepted.Accepted) ankiProbeRoutes[accepted.OperationId] = selectedRoute;
                return accepted;
            });

        public async Task<SdkAnkiReadiness> ObserveAnkiReadinessAsync(string operationId, CancellationToken cancellationToken)
        {
            if (!ankiProbeRoutes.TryRemove(operationId, out var route)) throw new InvalidOperationException("anki-probe-unbound-operation");
            var observer = new AnkiProbeSdkObserver(
                (id, ct) => client.GetOperationProjectionAsync(id, ct),
                async (handle, ct) => (await client.ReadArtifactAsync(handle, null, ct).ConfigureAwait(false)).Content);
            return await Translate(() => observer.ObserveAsync(operationId, route, cancellationToken)).ConfigureAwait(false);
        }

        public Task<SdkSubmission> QuestionInitialAsync(SdkArtifact question, IReadOnlyList<SdkArtifact> context, CancellationToken cancellationToken)
        {
            var requestId = NextRequestId();
            return Translate(async () => ConvertQuestion(await (UsesDestinationSettings ? destinationQuestions.InitialAsync(requestId, Unwrap(question), context.Select(Unwrap).ToArray(), cancellationToken) : questionSessions.InitialAsync(requestId, Unwrap(question), context.Select(Unwrap).ToArray(), cancellationToken)).ConfigureAwait(false), requestId));
        }

        public Task<SdkSubmission> QuestionFollowUpAsync(string sessionId, SdkArtifact followUp, CancellationToken cancellationToken)
            => Translate(async () => ConvertQuestion(await (UsesDestinationSettings ? destinationQuestions.FollowUpAsync(NextRequestId(), sessionId, Unwrap(followUp), cancellationToken) : questionSessions.FollowUpAsync(NextRequestId(), sessionId, Unwrap(followUp), cancellationToken)).ConfigureAwait(false), sessionId));

        public Task<SdkSubmission> QuestionMakeCardsAsync(string sessionId, SdkArtifact answer, CancellationToken cancellationToken)
            => Translate(async () => ConvertQuestion(await (UsesDestinationSettings ? destinationQuestions.MakeCardsAsync(NextRequestId(), sessionId, Unwrap(answer), cancellationToken) : questionSessions.MakeCardsAsync(NextRequestId(), sessionId, Unwrap(answer), cancellationToken)).ConfigureAwait(false), sessionId));

        public Task<SdkQuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken)
            => Translate(async () => {
                var value = await (UsesDestinationSettings ? destinationCalls.QuestionProjection(taskId, cancellationToken) : client.GetQuestionProjectionAsync(taskId, cancellationToken)).ConfigureAwait(false);
                if (value.CardDraft != null) { projectedDrafts[taskId] = value.CardDraft; projectedDraftSources[taskId] = "question"; }
                CacheDestinationProjection(taskId, value.DestinationSnapshot, value.Cursor, value.Destinations);
                return new SdkQuestionProjection(Map(value.State), value.Notice, 0, value.AnswerArtifact == null ? null : Wrap(value.AnswerArtifact), value.CardBundleArtifact == null ? null : Wrap(value.CardBundleArtifact), value.DestinationEvidenceArtifact == null ? null : Wrap(value.DestinationEvidenceArtifact), value.Cursor, ConvertDraft(value.CardDraft))
                { DestinationSnapshot = value.DestinationSnapshot, Destinations = value.Destinations };
            });

        public Task<SdkProjection> GetTaskAsync(string taskId, CancellationToken cancellationToken)
            => Translate(async () => { var value = await client.GetTaskProjectionAsync(taskId, cancellationToken).ConfigureAwait(false); return new SdkProjection(Map(value.State), value.Notice); });

        public Task<SdkReadToAnkiSubmission> ReadToAnkiAsync(SdkArtifact capture, IReadOnlyList<SdkArtifact> orderedContext, IReadOnlyList<SdkContextSegment> roleBoundContext, string targetText, bool contextRequested, string contextGranularity, string sourceWindow, SdkArtifact configuration, SdkLanguageCardSettings languageCards, bool includeSpeech, CancellationToken cancellationToken)
        {
            var requestId = NextRequestId();
            return Translate(async () =>
            {
                var nativeContext = (orderedContext ?? Array.Empty<SdkArtifact>()).Select(Unwrap).ToArray();
                var nativeRoleBound = (roleBoundContext ?? Array.Empty<SdkContextSegment>())
                    .Select(item => new LanguageContextSegment(Unwrap(item.Artifact), item.Role, item.Order))
                    .ToArray();
                var selection = new LanguageSelectionContext(targetText, nativeContext, contextRequested, contextGranularity ?? "sentence", sourceWindow ?? "unknown", nativeRoleBound);
                if (UsesDestinationSettings)
                {
                    var targets = (await freezeDestinations(cancellationToken).ConfigureAwait(false)).ToArray();
                    var accepted = await destinationCalls.Read(new ReadCardsRequest(requestId, Unwrap(capture), targets,
                        languageCards == null ? null : new LanguageCardSettings(languageCards.Revision, languageCards.DefaultChoice), includeSpeech, selection), cancellationToken).ConfigureAwait(false);
                    return new SdkReadToAnkiSubmission(accepted.OperationId, accepted.Accepted, accepted.CommandId, accepted.AcceptedSequence);
                }
                var authorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("desktop-anki-write-" + requestId);
                var request = new ReadAloudCardsRequest(requestId, Unwrap(capture), Unwrap(configuration), authorization,
                    languageCards == null ? null : new LanguageCardSettings(languageCards.Revision, languageCards.DefaultChoice), includeSpeech, selection);
                var value = includeSpeech
                    ? await client.ReadAloudCardsAsync(request, cancellationToken).ConfigureAwait(false)
                    : await client.ClipCardsAsync(request, cancellationToken).ConfigureAwait(false);
                if (value.Accepted) operationAuthorizations.Bind(value, authorization);
                return new SdkReadToAnkiSubmission(value.OperationId, value.Accepted, value.CommandId, value.AcceptedSequence);
            });
        }

        public Task<SdkReadToAnkiSubmission> ChooseLanguageCardAsync(string operationId, LanguagePracticeChoice choice, CancellationToken cancellationToken)
            => Translate(async () =>
            {
                if (!languagePrompts.TryGetValue(operationId, out var prompt)) throw new SdkDomainException("language-card-choice-prompt-unavailable", null);
                var requestId = NextRequestId();
                var selected = choice switch { LanguagePracticeChoice.MeaningReading => "meaning-reading", LanguagePracticeChoice.Writing => "writing", LanguagePracticeChoice.Both => "both", _ => throw new ArgumentOutOfRangeException(nameof(choice)) };
                var value = UsesDestinationSettings
                    ? await destinationCalls.Choice(new DestinationLanguageChoiceRequest(requestId, operationId, prompt.TargetToken, selected), cancellationToken).ConfigureAwait(false)
                    : await client.ChooseLanguageCardTypeAsync(new LanguageCardChoiceRequest(requestId, operationId, prompt.TargetToken, selected, RequireOperationAuthorization(operationId)), cancellationToken).ConfigureAwait(false);
                return new SdkReadToAnkiSubmission(value.OperationId, value.Accepted, value.CommandId, value.AcceptedSequence);
            });

        public Task<SdkReadToAnkiProjection> GetReadToAnkiProjectionAsync(string operationId, CancellationToken cancellationToken)
            => Translate(async () =>
            {
                var value = await (UsesDestinationSettings ? destinationCalls.ReadProjection(operationId, cancellationToken) : client.GetReadAloudCardsProjectionAsync(operationId, cancellationToken)).ConfigureAwait(false);
                CacheDestinationProjection(operationId, value.DestinationSnapshot, value.Cursor, value.Destinations);
                if (value.LanguageCardChoice != null) languagePrompts[operationId] = value.LanguageCardChoice;
                if (value.CardDraft != null) { projectedDrafts[operationId] = value.CardDraft; projectedDraftSources[operationId] = "read"; }
                if (value.CardResolution != null) resolutionPrompts[operationId] = value.CardResolution;
                return ConvertReadProjection(value);
            });

        internal static SdkReadToAnkiProjection ConvertReadProjection(ReadAloudCardsProjectionSnapshot value)
            => new SdkReadToAnkiProjection(Map(value.Playback), Map(value.Pipeline), value.AttentionRequired, value.CardGenerationFailure?.ReasonCode ?? value.Notice, value.Cursor,
                ConvertDraft(value.CardDraft), value.CardResolution == null ? null : CardResolutionEvidenceMapper.Convert(value.CardResolution))
            {
                ActionState = Map(value.ActionState), ActiveCommandId = value.ActiveCommandId,
                TerminalCommandId = value.TerminalCommandId, TerminalSequence = value.TerminalSequence,
                DestinationSnapshot = value.DestinationSnapshot, Destinations = value.Destinations
            };

        private static string Map(ReadAloudCardsActionState value) => value switch
        {
            ReadAloudCardsActionState.Running => "running", ReadAloudCardsActionState.Completed => "completed",
            ReadAloudCardsActionState.Failed => "failed", ReadAloudCardsActionState.Cancelled => "cancelled",
            ReadAloudCardsActionState.UnknownExternalState => "unknown_external_state", _ => "not_found"
        };

        public Task<SdkReadToAnkiSubmission> ResolveCardAsync(CardResolutionSubmission submission, CancellationToken cancellationToken)
            => Translate(async () =>
            {
                if (!resolutionPrompts.TryGetValue(submission.OperationId, out var prompt)) throw new SdkDomainException("card-resolution-prompt-unavailable", null);
                CandidateOccurrence occurrence = submission.CandidateOccurrence == null ? null : new CandidateOccurrence(submission.CandidateOccurrence.SegmentId, submission.CandidateOccurrence.Order, submission.CandidateOccurrence.Start, submission.CandidateOccurrence.End, submission.CandidateOccurrence.ExactText, submission.CandidateOccurrence.SourceSha256);
                var value = UsesDestinationSettings
                    ? await destinationCalls.Resolution(new DestinationResolutionRequest(NextRequestId(), submission.OperationId, prompt.TargetToken, prompt.DecisionId, submission.Action, submission.SelectedText, occurrence), cancellationToken).ConfigureAwait(false)
                    : await client.ResolveCardResolutionAsync(new CardResolutionActionRequest(NextRequestId(), submission.OperationId, prompt.TargetToken, prompt.DecisionId, submission.Action, submission.SelectedText, RequireOperationAuthorization(submission.OperationId), occurrence), cancellationToken).ConfigureAwait(false);
                return new SdkReadToAnkiSubmission(value.OperationId, value.Accepted, value.CommandId, value.AcceptedSequence);
            });

        public Task<SdkDraftCommand> ConfirmDraftAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken)
            => Translate(async () => { if (UsesDestinationSettings) return await ConfirmDestinationsAsync(confirmation, cancellationToken).ConfigureAwait(false); var draft = ResolveNative(confirmation); var value = await client.ConfirmFlashcardDraftAsync(new FlashcardDraftConfirmationRequest(NextRequestId(), confirmation.OperationId, draft, AuthorizationForDraft(confirmation.OperationId)), cancellationToken).ConfigureAwait(false); if (value.Accepted && projectedDraftSources.TryGetValue(confirmation.OperationId, out var source) && source == "question") { var terminal = await client.WaitFlashcardDraftActionAsync(value, cancellationToken).ConfigureAwait(false); return new SdkDraftCommand(terminal.State == QuestionProjectionState.Succeeded, MapDraftStatus(terminal.State), terminal.Notice); } return new SdkDraftCommand(value.Accepted, value.Accepted ? FlashcardDraftStatus.Exporting : FlashcardDraftStatus.Failed, value.Accepted ? "flashcard-draft.confirm-accepted" : "flashcard-draft.confirm-rejected", value.CommandId, value.AcceptedSequence); });

        public Task<SdkDraftCommand> CancelDraftAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken)
            => Translate(async () => {
                var request = new FlashcardDraftCancellationRequest(NextRequestId(), confirmation.OperationId, ResolveNative(confirmation));
                if (UsesDestinationSettings)
                {
                    if (destinationCalls.Cancel == null) throw new SdkDomainException("destination-cancel-sdk-unavailable", null);
                    var accepted = await destinationCalls.Cancel(request, cancellationToken).ConfigureAwait(false);
                    return new SdkDraftCommand(accepted.Accepted, accepted.Accepted ? FlashcardDraftStatus.Processing : FlashcardDraftStatus.Failed,
                        accepted.Accepted ? "flashcard-draft.cancel-requested" : "flashcard-draft.cancel-rejected", accepted.CommandId, accepted.AcceptedSequence);
                }
                var value = await client.CancelFlashcardDraftAsync(request, cancellationToken).ConfigureAwait(false);
                if (value.Accepted && projectedDraftSources.TryGetValue(confirmation.OperationId, out var source) && source == "question") { var terminal = await client.WaitFlashcardDraftActionAsync(value, cancellationToken).ConfigureAwait(false); return new SdkDraftCommand(terminal.State == QuestionProjectionState.Cancelled, MapDraftStatus(terminal.State), terminal.Notice); }
                return new SdkDraftCommand(value.Accepted, value.Accepted ? FlashcardDraftStatus.Cancelled : FlashcardDraftStatus.Failed, value.Accepted ? "flashcard-draft.cancel-accepted" : "flashcard-draft.cancel-rejected");
            });

        private static FlashcardDraftStatus MapDraftStatus(QuestionProjectionState value) => value switch { QuestionProjectionState.Succeeded => FlashcardDraftStatus.Completed, QuestionProjectionState.Cancelled => FlashcardDraftStatus.Cancelled, QuestionProjectionState.Failed => FlashcardDraftStatus.Failed, QuestionProjectionState.Degraded => FlashcardDraftStatus.Unknown, QuestionProjectionState.WaitingInput => FlashcardDraftStatus.AwaitingConfirmation, _ => FlashcardDraftStatus.Processing };

        private KnowledgeClipper.Core.V3.Client.FlashcardDraft ResolveNative(FlashcardDraftConfirmation value)
        {
            if (!projectedDrafts.TryGetValue(value.OperationId, out var draft) ||
                !string.Equals(draft.Token, value.DraftToken, StringComparison.Ordinal) ||
                !string.Equals(draft.Revision, value.Revision, StringComparison.Ordinal) ||
                !string.Equals(draft.DraftSha256, value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SdkDomainException("flashcard-draft.stale-or-unprojected", null);
            return draft;
        }

        private static SdkFlashcardDraft ConvertDraft(KnowledgeClipper.Core.V3.Client.FlashcardDraft value) => value == null ? null :
            new SdkFlashcardDraft(value.Token, value.Revision, value.DraftSha256,
                value.Cards.Select(card => new SdkFlashcardDraftCard(card.CardId, card.Revision, card.CardSha256, card.Front, card.Back, card.Template)).ToArray());

        public Task<SdkReadToAnkiSubmission> ReplayReadToAnkiAsync(string operationId, CancellationToken cancellationToken)
        {
            var requestId = NextRequestId();
            return Translate(async () =>
            {
                if (UsesDestinationSettings && !projectedCursors.ContainsKey(operationId)) throw new SdkDomainException("destination-replay-projection-required", null);
                var value = UsesDestinationSettings
                    ? await destinationCalls.Replay(new DestinationReplayRequest(requestId, operationId, projectedCursors[operationId]), cancellationToken).ConfigureAwait(false)
                    : await client.ReplayReadAloudCardsAsync(new ReadAloudCardsReplayRequest(requestId, operationId, RequireOperationAuthorization(operationId)), cancellationToken).ConfigureAwait(false);
                return new SdkReadToAnkiSubmission(value.OperationId, value.Accepted, value.CommandId, value.AcceptedSequence);
            });
        }

        private AnkiWriteAuthorization RequireOperationAuthorization(string operationId)
            => operationAuthorizations.Require(operationId);

        private AnkiWriteAuthorization AuthorizationForDraft(string operationId)
            => projectedDraftSources.TryGetValue(operationId, out var source) && source == "read"
                ? RequireOperationAuthorization(operationId)
                : questionSessions.AuthorizationForOperation(operationId, NextRequestId());

        private string NextRequestId() => "desktop-" + Interlocked.Increment(ref requestSequence).ToString("x16") + "-" + Guid.NewGuid().ToString("N");
        private static SdkArtifact Wrap(TypedArtifactHandle value) => new(value.Token, value);
        private static TypedArtifactHandle Unwrap(SdkArtifact value) => value.Native as TypedArtifactHandle ?? throw new InvalidOperationException("sdk-artifact-handle-invalid");
        private static SdkSubmission Convert(OperationSubmission value) => new(value.OperationId, null);
        private static SdkSubmission ConvertQuestion(QuestionOperationSubmission value, string sessionId) => new(value.OperationId, value.SessionId, value.Accepted);
        private static ArtifactPurpose Map(SdkArtifactPurpose value) => value switch
        {
            SdkArtifactPurpose.Question => ArtifactPurpose.Question,
            SdkArtifactPurpose.QuestionContext => ArtifactPurpose.QuestionContext,
            SdkArtifactPurpose.QuestionFollowUp => ArtifactPurpose.QuestionFollowUp,
            SdkArtifactPurpose.QuestionAnswer => ArtifactPurpose.QuestionAnswer,
            SdkArtifactPurpose.CaptureText => ArtifactPurpose.CaptureText,
            SdkArtifactPurpose.AnkiConfiguration => ArtifactPurpose.AnkiConfiguration,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        private static OperationStatus Map(QuestionProjectionState value) => value switch { QuestionProjectionState.Running => OperationStatus.Running, QuestionProjectionState.WaitingInput => OperationStatus.WaitingInput, QuestionProjectionState.Succeeded => OperationStatus.Succeeded, QuestionProjectionState.Failed => OperationStatus.Failed, QuestionProjectionState.Cancelled => OperationStatus.Cancelled, _ => OperationStatus.Unknown };
        private static OperationStatus Map(TaskProjectionState value) => value switch { TaskProjectionState.Running => OperationStatus.Running, TaskProjectionState.Succeeded => OperationStatus.Succeeded, TaskProjectionState.Failed => OperationStatus.Failed, TaskProjectionState.Cancelled => OperationStatus.Cancelled, _ => OperationStatus.Unknown };
        private static SdkReadToAnkiPlayback Map(ReadAloudCardsPlaybackState value) => value switch { ReadAloudCardsPlaybackState.NotRequested => SdkReadToAnkiPlayback.NotRequested, ReadAloudCardsPlaybackState.Accepted => SdkReadToAnkiPlayback.Accepted, ReadAloudCardsPlaybackState.Playing => SdkReadToAnkiPlayback.Playing, ReadAloudCardsPlaybackState.IdleReplayable => SdkReadToAnkiPlayback.IdleReplayable, ReadAloudCardsPlaybackState.Failed => SdkReadToAnkiPlayback.Failed, ReadAloudCardsPlaybackState.Stopped => SdkReadToAnkiPlayback.Stopped, _ => SdkReadToAnkiPlayback.Unknown };
        private static SdkReadToAnkiPipeline Map(ReadAloudCardsPipelineState value) => value switch { ReadAloudCardsPipelineState.Accepted => SdkReadToAnkiPipeline.Accepted, ReadAloudCardsPipelineState.Reviewing => SdkReadToAnkiPipeline.Reviewing, ReadAloudCardsPipelineState.WaitingLanguageCardChoice => SdkReadToAnkiPipeline.WaitingUserChoice, ReadAloudCardsPipelineState.AwaitingCardResolution => SdkReadToAnkiPipeline.AwaitingCardResolution, ReadAloudCardsPipelineState.AwaitingCardConfirmation => SdkReadToAnkiPipeline.AwaitingCardConfirmation, ReadAloudCardsPipelineState.WritingAnki => SdkReadToAnkiPipeline.WritingAnki, ReadAloudCardsPipelineState.Completed => SdkReadToAnkiPipeline.Completed, ReadAloudCardsPipelineState.Cancelled => SdkReadToAnkiPipeline.Cancelled, ReadAloudCardsPipelineState.PartiallyFailed => SdkReadToAnkiPipeline.PartiallyFailed, ReadAloudCardsPipelineState.UnknownExternalState => SdkReadToAnkiPipeline.UnknownExternalState, _ => SdkReadToAnkiPipeline.Failed };

        private static async Task<T> Translate<T>(Func<Task<T>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (CoreV3OperationException exception) { throw new SdkDomainException(exception.Code, exception); }
            catch (CoreV3ClientException exception) { throw new SdkDomainException(exception.Code, exception); }
            catch (QuestionObservationException exception) { throw new SdkDomainException(exception.Code, exception); }
        }
    }

    internal static class SdkReadToAnkiMapping
    {
        internal static ReadToAnkiPlaybackStatus Map(SdkReadToAnkiPlayback value) => value switch
        {
            SdkReadToAnkiPlayback.NotRequested => ReadToAnkiPlaybackStatus.NotRequested,
            SdkReadToAnkiPlayback.Accepted => ReadToAnkiPlaybackStatus.Accepted,
            SdkReadToAnkiPlayback.Playing => ReadToAnkiPlaybackStatus.Playing,
            SdkReadToAnkiPlayback.IdleReplayable => ReadToAnkiPlaybackStatus.IdleReplayable,
            SdkReadToAnkiPlayback.Failed => ReadToAnkiPlaybackStatus.Failed,
            SdkReadToAnkiPlayback.Stopped => ReadToAnkiPlaybackStatus.Stopped,
            _ => ReadToAnkiPlaybackStatus.Unknown
        };

        internal static ReadToAnkiPipelineStatus Map(SdkReadToAnkiPipeline value) => value switch
        {
            SdkReadToAnkiPipeline.Accepted => ReadToAnkiPipelineStatus.Accepted,
            SdkReadToAnkiPipeline.Reviewing => ReadToAnkiPipelineStatus.Reviewing,
            SdkReadToAnkiPipeline.WaitingUserChoice => ReadToAnkiPipelineStatus.WaitingUserChoice,
            SdkReadToAnkiPipeline.AwaitingCardResolution => ReadToAnkiPipelineStatus.AwaitingCardResolution,
            SdkReadToAnkiPipeline.AwaitingCardConfirmation => ReadToAnkiPipelineStatus.AwaitingCardConfirmation,
            SdkReadToAnkiPipeline.CreatingCards => ReadToAnkiPipelineStatus.CreatingCards,
            SdkReadToAnkiPipeline.WritingAnki => ReadToAnkiPipelineStatus.WritingAnki,
            SdkReadToAnkiPipeline.Completed => ReadToAnkiPipelineStatus.Completed,
            SdkReadToAnkiPipeline.PartiallyFailed => ReadToAnkiPipelineStatus.PartiallyFailed,
            SdkReadToAnkiPipeline.UnknownExternalState => ReadToAnkiPipelineStatus.UnknownExternalState,
            _ => ReadToAnkiPipelineStatus.Failed
        };
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed class ModuleActionUseCases
    {
        private readonly IModuleActionPort port;
        private readonly DesktopStateStore state;
        private long nextGeneration;
        internal ModuleActionUseCases(IModuleActionPort port, DesktopStateStore state) { this.port = port ?? throw new ArgumentNullException(nameof(port)); this.state = state ?? throw new ArgumentNullException(nameof(state)); }

        internal async Task<ModuleActionResult> ExecuteAsync(ModuleActionKind kind, IReadOnlyDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            var generation = Interlocked.Increment(ref nextGeneration);
            state.Reduce(value => ModuleActionReducers.Started(value, kind, generation));
            ModuleActionResult result;
            try { cancellationToken.ThrowIfCancellationRequested(); result = await port.ExecuteAsync(new ModuleActionRequest(kind, arguments ?? new Dictionary<string, object>()), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = new ModuleActionResult(false, OperationStatus.Cancelled, "observation-cancelled", new Dictionary<string, object>(), null, false, false); }
            catch (Exception exception) { result = new ModuleActionResult(false, OperationStatus.Failed, exception.GetType().Name, new Dictionary<string, object>(), null, false, false); }
            state.TryReduce(value => value.ModuleActions.TryGetValue(kind, out var current) && current.BindingGeneration == generation,
                value => ModuleActionReducers.Completed(value, kind, result));
            return result;
        }
    }

    internal sealed class SettingsUseCases
    {
        private readonly ISettingsRepository repository;
        private readonly DesktopStateStore state;
        internal SettingsUseCases(ISettingsRepository repository, DesktopStateStore state) { this.repository = repository ?? throw new ArgumentNullException(nameof(repository)); this.state = state ?? throw new ArgumentNullException(nameof(state)); }

        internal async Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            state.Reduce(SettingsReducers.Loading);
            try { var result = await repository.LoadAsync(cancellationToken).ConfigureAwait(false); state.Reduce(value => SettingsReducers.Loaded(value, result)); return result; }
            catch (Exception exception) { state.Reduce(value => SettingsReducers.Failed(value, exception.GetType().Name)); throw; }
        }

        internal async Task<SettingsSnapshot> SaveAsync(SettingsSnapshot draft, CancellationToken cancellationToken)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            state.Reduce(value => value with { Settings = new SettingsFeatureState(OperationStatus.Running, draft, null) });
            try { var result = await repository.SaveAsync(draft, cancellationToken).ConfigureAwait(false); state.Reduce(value => SettingsReducers.Loaded(value, result)); return result; }
            catch (Exception exception) { state.Reduce(value => SettingsReducers.Failed(value, exception.GetType().Name)); throw; }
        }
    }

    internal sealed class QuestionUseCases
    {
        private readonly ITaskCommandPort commands;
        private readonly QuestionObserver observer;
        private readonly DesktopStateStore state;
        private int commandInFlight;
        internal FlashcardDraftPreviewUseCases Preview { get; set; }
        internal QuestionUseCases(ITaskCommandPort commands, QuestionObserver observer, DesktopStateStore state) { this.commands = commands ?? throw new ArgumentNullException(nameof(commands)); this.observer = observer ?? throw new ArgumentNullException(nameof(observer)); this.state = state ?? throw new ArgumentNullException(nameof(state)); }

        internal Task<TaskCommandResult> SubmitInitialAsync(string text, CancellationToken cancellationToken) => SubmitAsync(text, false, null, cancellationToken);

        internal Task<TaskCommandResult> SubmitFollowUpAsync(string text, CancellationToken cancellationToken)
        {
            if (Preview != null)
            {
                var draft = Preview.Current.Draft;
                return SubmitPreviewFollowUpAsync(text, FlashcardDraftConfirmation.From(draft), cancellationToken);
            }
            var sessionId = state.Snapshot().Question.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidOperationException("question-session-required");
            return SubmitAsync(text, true, sessionId, cancellationToken);
        }

        internal string PreviewFollowUpUnavailableReason(FlashcardDraftRevision draft)
        {
            var question = state.Snapshot().Question;
            if (draft?.Source == FlashcardDraftSource.ReadAloud) return "question.read-clip-follow-up-unsupported";
            if (draft == null || string.IsNullOrWhiteSpace(question.SessionId) || draft.OperationId != question.TaskId) return "question.preview-session-binding-unavailable";
            if (question.Status != OperationStatus.WaitingInput || !question.AnswerAvailable || question.DestinationEvidence != null) return "question.session-not-continuable";
            return null;
        }

        internal Task<TaskCommandResult> SubmitPreviewFollowUpAsync(string text, FlashcardDraftConfirmation expected, CancellationToken cancellationToken)
            => SubmitAsync(text, true, state.Snapshot().Question.SessionId, cancellationToken, expected);

        private async Task<TaskCommandResult> SubmitAsync(string text, bool followUp, string sessionId, CancellationToken cancellationToken, FlashcardDraftConfirmation expected = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return new TaskCommandResult(false, null, "question-text-required");
            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new TaskCommandResult(false, state.Snapshot().Question.TaskId, "question-command-in-progress");
            try
            {
                if (cancellationToken.IsCancellationRequested) return new TaskCommandResult(false, state.Snapshot().Question.TaskId, "question-submit-interrupted-before-send");
                if (followUp && Preview != null)
                {
                    var current = Preview.Current;
                    var reason = PreviewFollowUpUnavailableReason(current.Draft);
                    if (reason != null || state.Snapshot().Question.SessionId != sessionId || !Preview.TryBeginFollowUp(expected))
                        return new TaskCommandResult(false, state.Snapshot().Question.TaskId, reason ?? "question.preview-stale-or-command-in-progress");
                }
                else Preview?.BeginQuestionWork();
                var generation = state.Reduce(value => QuestionReducers.Submitting(followUp ? value : value with { Question = value.Question with { SessionId = null } })).Question.BindingGeneration;
                TaskCommandResult result;
                try { result = await commands.SubmitQuestionAsync(new QuestionSubmission(text, followUp, sessionId), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { state.Reduce(value => QuestionReducers.Unresolved(value, "question-submit-outcome-unknown")); Preview?.QuestionWorkUnsettled("question-submit-outcome-unknown", true); return new TaskCommandResult(false, state.Snapshot().Question.TaskId, "question-submit-outcome-unknown"); }
                catch (Exception exception) { var notice = "question-submit-" + exception.GetType().Name; state.Reduce(value => QuestionReducers.Unresolved(value, notice)); Preview?.QuestionWorkUnsettled(notice, true); return new TaskCommandResult(false, state.Snapshot().Question.TaskId, notice); }
                return await CompleteSubmissionAsync(result, generation, followUp ? sessionId : null, cancellationToken).ConfigureAwait(false);
            }
            finally { Interlocked.Exchange(ref commandInFlight, 0); }
        }

        private async Task<TaskCommandResult> CompleteSubmissionAsync(TaskCommandResult result, long generation, string expectedSession, CancellationToken token)
        {
            if (state.Snapshot().Question.BindingGeneration != generation) return new TaskCommandResult(false, result?.TaskId, "question.observation-superseded");
            if (result == null || result.Accepted && (string.IsNullOrWhiteSpace(result.TaskId) || string.IsNullOrWhiteSpace(result.SessionId) || expectedSession != null && result.SessionId != expectedSession))
            {
                state.TryReduce(value => value.Question.BindingGeneration == generation, value => value with { Question = value.Question with { Status = OperationStatus.Unknown, TaskId = result?.TaskId, SessionId = null, NoticeCode = "question.invalid-session-receipt" } });
                Preview?.QuestionWorkUnsettled("question.invalid-session-receipt", true);
                return new TaskCommandResult(false, result?.TaskId, "question.invalid-session-receipt");
            }
            if (!state.TryReduce(value => value.Question.BindingGeneration == generation, value => QuestionReducers.Submitted(value, result)))
                return new TaskCommandResult(false, result.TaskId, "question.observation-superseded");
            if (result.Accepted) await observer.ObserveUntilSettledAsync(result.TaskId, token).ConfigureAwait(false);
            var question = state.Snapshot().Question;
            if (question.BindingGeneration != generation || result.Accepted && (question.TaskId != result.TaskId || question.SessionId != result.SessionId))
                return new TaskCommandResult(false, result.TaskId, "question.observation-superseded");
            if (Preview != null && (!result.Accepted || Preview.Current.Draft?.OperationId != result.TaskId || Preview.Current.Status != FlashcardDraftStatus.AwaitingConfirmation))
            {
                var notice = question.NoticeCode ?? "question.new-draft-not-observed";
                Preview.QuestionWorkUnsettled(notice, result.Accepted && question.Status != OperationStatus.Failed && question.Status != OperationStatus.Cancelled);
                return new TaskCommandResult(false, result.TaskId, notice) { SessionId = result.SessionId };
            }
            return result;
        }

        internal async Task<TaskCommandResult> MakeCardsAsync(string cardStructureId, CancellationToken cancellationToken)
        {
            var current = state.Snapshot().Question;
            if (string.IsNullOrWhiteSpace(current.SessionId) || !current.AnswerAvailable) throw new InvalidOperationException("question-answer-required");
            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new TaskCommandResult(false, current.TaskId, "question-command-in-progress");
            try
            {
                if (cancellationToken.IsCancellationRequested) return new TaskCommandResult(false, current.TaskId, "question-submit-interrupted-before-send");
                Preview?.BeginQuestionWork();
                var generation = state.Reduce(QuestionReducers.Submitting).Question.BindingGeneration;
                TaskCommandResult result;
                try { result = await commands.MakeCardsAsync(new MakeCardsSubmission(current.SessionId, cardStructureId, current.Answer), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { state.Reduce(value => QuestionReducers.Unresolved(value, "question-make-cards-outcome-unknown")); Preview?.QuestionWorkUnsettled("question-make-cards-outcome-unknown", true); return new TaskCommandResult(false, current.TaskId, "question-make-cards-outcome-unknown"); }
                catch (Exception exception) { var notice = "question-make-cards-" + exception.GetType().Name; state.Reduce(value => QuestionReducers.Unresolved(value, notice)); Preview?.QuestionWorkUnsettled(notice, true); return new TaskCommandResult(false, current.TaskId, notice); }
                return await CompleteSubmissionAsync(result, generation, current.SessionId, cancellationToken).ConfigureAwait(false);
            }
            finally { Interlocked.Exchange(ref commandInFlight, 0); }
        }

        internal async Task<QuestionProjection> RefreshAsync(CancellationToken cancellationToken)
        {
            var taskId = state.Snapshot().Question.TaskId;
            if (string.IsNullOrWhiteSpace(taskId)) throw new InvalidOperationException("question-task-required");
            return await observer.ObserveUntilSettledAsync(taskId, cancellationToken).ConfigureAwait(false);
        }
    }
}

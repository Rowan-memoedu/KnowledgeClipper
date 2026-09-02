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
        internal ModuleActionUseCases(IModuleActionPort port, DesktopStateStore state) { this.port = port ?? throw new ArgumentNullException(nameof(port)); this.state = state ?? throw new ArgumentNullException(nameof(state)); }

        internal async Task<ModuleActionResult> ExecuteAsync(ModuleActionKind kind, IReadOnlyDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            state.Reduce(value => ModuleActionReducers.Started(value, kind));
            ModuleActionResult result;
            try { result = await port.ExecuteAsync(new ModuleActionRequest(kind, arguments ?? new Dictionary<string, object>()), cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { result = new ModuleActionResult(false, OperationStatus.Failed, exception.GetType().Name, new Dictionary<string, object>(), null, false, false); }
            state.Reduce(value => ModuleActionReducers.Completed(value, kind, result));
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
        internal QuestionUseCases(ITaskCommandPort commands, QuestionObserver observer, DesktopStateStore state) { this.commands = commands ?? throw new ArgumentNullException(nameof(commands)); this.observer = observer ?? throw new ArgumentNullException(nameof(observer)); this.state = state ?? throw new ArgumentNullException(nameof(state)); }

        internal Task<TaskCommandResult> SubmitInitialAsync(string text, CancellationToken cancellationToken) => SubmitAsync(text, false, null, cancellationToken);

        internal Task<TaskCommandResult> SubmitFollowUpAsync(string text, CancellationToken cancellationToken)
        {
            var sessionId = state.Snapshot().Question.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidOperationException("question-session-required");
            return SubmitAsync(text, true, sessionId, cancellationToken);
        }

        private async Task<TaskCommandResult> SubmitAsync(string text, bool followUp, string sessionId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("question-text-required", nameof(text));
            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new TaskCommandResult(false, state.Snapshot().Question.TaskId, "question-command-in-progress");
            try
            {
                state.Reduce(QuestionReducers.Submitting);
                TaskCommandResult result;
                try { result = await commands.SubmitQuestionAsync(new QuestionSubmission(text, followUp, sessionId), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { state.Reduce(value => QuestionReducers.Cancelled(value, "question-submit-cancelled")); return new TaskCommandResult(false, state.Snapshot().Question.TaskId, "question-submit-cancelled"); }
                catch (Exception exception) { var notice = "question-submit-" + exception.GetType().Name; state.Reduce(value => QuestionReducers.Failed(value, notice)); return new TaskCommandResult(false, state.Snapshot().Question.TaskId, notice); }
                state.Reduce(value => QuestionReducers.Submitted(value, result));
                if (result.Accepted) await observer.ObserveUntilSettledAsync(result.TaskId, cancellationToken).ConfigureAwait(false);
                return result;
            }
            finally { Interlocked.Exchange(ref commandInFlight, 0); }
        }

        internal async Task<TaskCommandResult> MakeCardsAsync(string cardStructureId, CancellationToken cancellationToken)
        {
            var current = state.Snapshot().Question;
            if (string.IsNullOrWhiteSpace(current.SessionId) || !current.AnswerAvailable) throw new InvalidOperationException("question-answer-required");
            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new TaskCommandResult(false, current.TaskId, "question-command-in-progress");
            try
            {
                state.Reduce(value => value with { Question = value.Question with { Status = OperationStatus.Running, NoticeCode = null } });
                TaskCommandResult result;
                try { result = await commands.MakeCardsAsync(new MakeCardsSubmission(current.SessionId, cardStructureId, current.Answer), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { state.Reduce(value => QuestionReducers.Cancelled(value, "question-make-cards-cancelled")); return new TaskCommandResult(false, current.TaskId, "question-make-cards-cancelled"); }
                catch (Exception exception) { var notice = "question-make-cards-" + exception.GetType().Name; state.Reduce(value => QuestionReducers.Failed(value, notice)); return new TaskCommandResult(false, current.TaskId, notice); }
                if (!result.Accepted) state.Reduce(value => QuestionReducers.Failed(value, result.NoticeCode));
                else await observer.ObserveUntilSettledAsync(result.TaskId, cancellationToken).ConfigureAwait(false);
                return result;
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

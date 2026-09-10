using System;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed record QuestionObservationPolicy(int MaxAttempts, TimeSpan InitialDelay, TimeSpan MaximumDelay)
    {
        internal static QuestionObservationPolicy Default => new(40, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));

        internal void Validate()
        {
            if (MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
            if (InitialDelay < TimeSpan.Zero || MaximumDelay < InitialDelay) throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        }
    }

    internal sealed class QuestionObserver
    {
        private readonly ITaskQueryPort queries;
        private readonly IArtifactGateway artifacts;
        private readonly DesktopStateStore state;
        private readonly QuestionObservationPolicy policy;
        private readonly Action<FlashcardDraftProjection> projectFlashcardDraft;

        internal QuestionObserver(ITaskQueryPort queries, IArtifactGateway artifacts, DesktopStateStore state, QuestionObservationPolicy policy, Action<FlashcardDraftProjection> projectFlashcardDraft = null)
        {
            this.queries = queries ?? throw new ArgumentNullException(nameof(queries));
            this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.policy = policy ?? QuestionObservationPolicy.Default;
            this.projectFlashcardDraft = projectFlashcardDraft;
            this.policy.Validate();
        }

        internal async Task<QuestionProjection> ObserveUntilSettledAsync(string taskId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("question-task-required", nameof(taskId));
            var binding = state.Snapshot().Question;
            bool SameBinding(DesktopApplicationState value) => value.Question.TaskId == taskId && !string.IsNullOrWhiteSpace(binding.SessionId) && value.Question.SessionId == binding.SessionId && value.Question.BindingGeneration == binding.BindingGeneration;
            bool Current() => SameBinding(state.Snapshot());
            void Unresolved(string notice) => state.TryReduce(SameBinding, value => QuestionReducers.Unresolved(value, notice));
            var delay = policy.InitialDelay;
            for (var attempt = 0; attempt < policy.MaxAttempts; attempt++)
            {
                if (!Current()) return null;
                if (attempt > 0 && delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Unresolved("question-observation-interrupted-not-core-cancelled"); return null; }
                    var nextTicks = Math.Min(policy.MaximumDelay.Ticks, Math.Max(delay.Ticks + 1, delay.Ticks * 2));
                    delay = TimeSpan.FromTicks(nextTicks);
                }

                QuestionProjection projection;
                try { projection = await queries.GetQuestionAsync(taskId, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { Unresolved("question-observation-interrupted-not-core-cancelled"); return null; }
                catch (Exception exception) { Unresolved("question-observation-" + exception.GetType().Name); return null; }
                if (!Current()) return null;

                if (projection == null || !string.Equals(projection.TaskId, taskId, StringComparison.Ordinal))
                {
                    Unresolved("question-observation-invalid-projection");
                    return projection;
                }

                QuestionAnswerContent answer = null;
                if (projection.AnswerAvailable)
                {
                    if (projection.Answer == null)
                    {
                        Unresolved("question-answer-artifact-required");
                        return projection;
                    }
                    try { answer = await artifacts.ReadAsync<QuestionAnswerContent>(projection.Answer, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Unresolved("question-observation-interrupted-not-core-cancelled"); return null; }
                    catch (Exception exception) { Unresolved("question-answer-" + exception.GetType().Name); return projection; }
                    if (answer == null || string.IsNullOrWhiteSpace(answer.Text))
                    {
                        Unresolved("question-answer-content-required");
                        return projection;
                    }
                }

                if (!Current()) return null;
                state.TryReduce(SameBinding, value => QuestionReducers.Projected(value, projection, answer));
                if (!Current()) return null;
                if (projection.FlashcardDraft != null) projectFlashcardDraft?.Invoke(projection.FlashcardDraft);
                if (projection.Status == OperationStatus.WaitingInput || projection.Status == OperationStatus.Succeeded ||
                    projection.Status == OperationStatus.Failed || projection.Status == OperationStatus.Cancelled ||
                    projection.Status == OperationStatus.Unknown)
                    return projection;
            }

            Unresolved("question-observation-timeout-not-core-failed");
            return null;
        }
    }
}

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

        internal QuestionObserver(ITaskQueryPort queries, IArtifactGateway artifacts, DesktopStateStore state, QuestionObservationPolicy policy)
        {
            this.queries = queries ?? throw new ArgumentNullException(nameof(queries));
            this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.policy = policy ?? QuestionObservationPolicy.Default;
            this.policy.Validate();
        }

        internal async Task<QuestionProjection> ObserveUntilSettledAsync(string taskId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("question-task-required", nameof(taskId));
            var delay = policy.InitialDelay;
            for (var attempt = 0; attempt < policy.MaxAttempts; attempt++)
            {
                if (attempt > 0 && delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { state.Reduce(value => QuestionReducers.Cancelled(value, "question-observation-cancelled")); return null; }
                    var nextTicks = Math.Min(policy.MaximumDelay.Ticks, Math.Max(delay.Ticks + 1, delay.Ticks * 2));
                    delay = TimeSpan.FromTicks(nextTicks);
                }

                QuestionProjection projection;
                try { projection = await queries.GetQuestionAsync(taskId, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { state.Reduce(value => QuestionReducers.Cancelled(value, "question-observation-cancelled")); return null; }
                catch (Exception exception) { state.Reduce(value => QuestionReducers.Failed(value, "question-observation-" + exception.GetType().Name)); return null; }

                if (projection == null || !string.Equals(projection.TaskId, taskId, StringComparison.Ordinal))
                {
                    state.Reduce(value => QuestionReducers.Failed(value, "question-observation-invalid-projection"));
                    return projection;
                }

                QuestionAnswerContent answer = null;
                if (projection.AnswerAvailable)
                {
                    if (projection.Answer == null)
                    {
                        state.Reduce(value => QuestionReducers.Failed(value, "question-answer-artifact-required"));
                        return projection;
                    }
                    try { answer = await artifacts.ReadAsync<QuestionAnswerContent>(projection.Answer, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { state.Reduce(value => QuestionReducers.Cancelled(value, "question-observation-cancelled")); return null; }
                    catch (Exception exception) { state.Reduce(value => QuestionReducers.Failed(value, "question-answer-" + exception.GetType().Name)); return projection; }
                    if (answer == null || string.IsNullOrWhiteSpace(answer.Text))
                    {
                        state.Reduce(value => QuestionReducers.Failed(value, "question-answer-content-required"));
                        return projection;
                    }
                }

                state.Reduce(value => QuestionReducers.Projected(value, projection, answer));
                if (projection.Status == OperationStatus.WaitingInput || projection.Status == OperationStatus.Succeeded ||
                    projection.Status == OperationStatus.Failed || projection.Status == OperationStatus.Cancelled ||
                    projection.Status == OperationStatus.Unknown)
                    return projection;
            }

            state.Reduce(value => QuestionReducers.Failed(value, "question-observation-timeout"));
            return null;
        }
    }
}

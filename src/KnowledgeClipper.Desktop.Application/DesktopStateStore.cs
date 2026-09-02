using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed record ModuleActionState(ModuleActionKind Kind, OperationStatus Status, string NoticeCode, IReadOnlyDictionary<string, object> Values)
    {
        internal AnkiReadinessSummary AnkiReadiness { get; init; }
    }
    internal sealed record SettingsFeatureState(OperationStatus Status, SettingsSnapshot Draft, string NoticeCode);
    internal sealed record QuestionFeatureState(OperationStatus Status, string TaskId, int TurnCount, bool AnswerAvailable, ArtifactHandle Answer, string AnswerText, string NoticeCode)
    {
        internal string SessionId { get; init; }
        internal ArtifactHandle CardBundle { get; init; }
        internal ArtifactHandle DestinationEvidence { get; init; }
    }

    internal sealed record DesktopApplicationState(
        long Revision,
        IReadOnlyDictionary<ModuleActionKind, ModuleActionState> ModuleActions,
        SettingsFeatureState Settings,
        QuestionFeatureState Question)
    {
        internal static DesktopApplicationState Empty => new(
            0,
            new Dictionary<ModuleActionKind, ModuleActionState>(),
            new SettingsFeatureState(OperationStatus.Idle, null, null),
            new QuestionFeatureState(OperationStatus.Idle, null, 0, false, null, null, null));
    }

    internal sealed class DesktopStateStore
    {
        private readonly object gate = new();
        private DesktopApplicationState current = DesktopApplicationState.Empty;

        internal event Action<DesktopApplicationState> Changed;

        internal DesktopApplicationState Snapshot()
        {
            lock (gate) return current;
        }

        internal DesktopApplicationState Reduce(Func<DesktopApplicationState, DesktopApplicationState> reducer)
        {
            if (reducer == null) throw new ArgumentNullException(nameof(reducer));
            DesktopApplicationState next;
            lock (gate)
            {
                next = reducer(current) ?? throw new InvalidOperationException("application-reducer-returned-null");
                current = next with { Revision = current.Revision + 1 };
                next = current;
            }
            Changed?.Invoke(next);
            return next;
        }
    }

    internal static class ModuleActionReducers
    {
        internal static DesktopApplicationState Started(DesktopApplicationState state, ModuleActionKind kind)
        {
            var actions = new Dictionary<ModuleActionKind, ModuleActionState>(state.ModuleActions)
            {
                [kind] = new ModuleActionState(kind, OperationStatus.Running, null, new Dictionary<string, object>())
            };
            return state with { ModuleActions = actions };
        }

        internal static DesktopApplicationState Completed(DesktopApplicationState state, ModuleActionKind kind, ModuleActionResult result)
        {
            var actions = new Dictionary<ModuleActionKind, ModuleActionState>(state.ModuleActions)
            {
                [kind] = new ModuleActionState(kind, result.Accepted ? result.Status : OperationStatus.Failed, result.NoticeCode, result.Values ?? new Dictionary<string, object>())
                {
                    AnkiReadiness = result.AnkiReadiness
                }
            };
            return state with { ModuleActions = actions };
        }
    }

    internal static class SettingsReducers
    {
        internal static DesktopApplicationState Loading(DesktopApplicationState state) => state with { Settings = new SettingsFeatureState(OperationStatus.Running, state.Settings.Draft, null) };
        internal static DesktopApplicationState Loaded(DesktopApplicationState state, SettingsSnapshot draft) => state with { Settings = new SettingsFeatureState(OperationStatus.Succeeded, draft, null) };
        internal static DesktopApplicationState Saving(DesktopApplicationState state) => state with { Settings = new SettingsFeatureState(OperationStatus.Running, state.Settings.Draft, null) };
        internal static DesktopApplicationState Failed(DesktopApplicationState state, string notice) => state with { Settings = new SettingsFeatureState(OperationStatus.Failed, state.Settings.Draft, notice) };
    }

    internal static class QuestionReducers
    {
        internal static DesktopApplicationState Submitting(DesktopApplicationState state) => state with { Question = state.Question with { Status = OperationStatus.Running, AnswerAvailable = false, Answer = null, AnswerText = null, NoticeCode = null, CardBundle = null, DestinationEvidence = null } };
        internal static DesktopApplicationState Submitted(DesktopApplicationState state, TaskCommandResult result) => state with
        {
            Question = result.Accepted
                ? state.Question with { Status = OperationStatus.Accepted, TaskId = result.TaskId, SessionId = result.SessionId ?? state.Question.SessionId ?? result.TaskId, AnswerAvailable = false, Answer = null, AnswerText = null, NoticeCode = result.NoticeCode, CardBundle = null, DestinationEvidence = null }
                : state.Question with { Status = OperationStatus.Failed, NoticeCode = result.NoticeCode }
        };
        internal static DesktopApplicationState Projected(DesktopApplicationState state, QuestionProjection projection, QuestionAnswerContent answer) => state with
        {
            Question = new QuestionFeatureState(projection.Status, projection.TaskId, projection.TurnCount, projection.AnswerAvailable, projection.Answer, answer == null ? null : answer.Text, projection.NoticeCode)
            {
                SessionId = state.Question.SessionId,
                CardBundle = projection.CardBundle,
                DestinationEvidence = projection.DestinationEvidence
            }
        };
        internal static DesktopApplicationState Failed(DesktopApplicationState state, string notice) => state with { Question = state.Question with { Status = OperationStatus.Failed, NoticeCode = notice } };
        internal static DesktopApplicationState Cancelled(DesktopApplicationState state, string notice) => state with { Question = state.Question with { Status = OperationStatus.Cancelled, NoticeCode = notice } };
    }
}

using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed record ModuleActionState(ModuleActionKind Kind, OperationStatus Status, string NoticeCode, IReadOnlyDictionary<string, object> Values)
    {
        internal long BindingGeneration { get; init; }
        internal ArtifactHandle Output { get; init; }
        internal AnkiReadinessSummary AnkiReadiness { get; init; }
    }
    internal sealed record SettingsFeatureState(OperationStatus Status, SettingsSnapshot Draft, string NoticeCode);
    internal sealed record QuestionFeatureState(OperationStatus Status, string TaskId, int TurnCount, bool AnswerAvailable, ArtifactHandle Answer, string AnswerText, string NoticeCode)
    {
        internal string SessionId { get; init; }
        internal long BindingGeneration { get; init; }
        internal ArtifactHandle CardBundle { get; init; }
        internal ArtifactHandle DestinationEvidence { get; init; }
    }
    internal sealed record ReadToAnkiFeatureState(
        string OperationId,
        ReadToAnkiPlaybackStatus Playback,
        ReadToAnkiPipelineStatus Pipeline,
        bool AttentionRequired,
        string NoticeCode)
    {
        internal string ActiveCommandId { get; init; }
        internal long AcceptedSequence { get; init; }
        internal CardResolutionPrompt CardResolution { get; init; }
        // Desktop observation identity only; never a replacement Core command.
        internal string PlaybackCommandId { get; init; }
        internal long PlaybackAcceptedSequence { get; init; }
        internal bool PlaybackOwnedLocally { get; init; }
        internal PlaybackTarget PlaybackTarget { get; init; }
        internal bool CanStop { get; init; }
        internal bool VerifiedPlaybackTerminal { get; init; }
    }

    internal sealed record DesktopApplicationState(
        long Revision,
        IReadOnlyDictionary<ModuleActionKind, ModuleActionState> ModuleActions,
        SettingsFeatureState Settings,
        QuestionFeatureState Question,
        ReadToAnkiFeatureState ReadToAnki)
    {
        internal static DesktopApplicationState Empty => new(
            0,
            new Dictionary<ModuleActionKind, ModuleActionState>(),
            new SettingsFeatureState(OperationStatus.Idle, null, null),
            new QuestionFeatureState(OperationStatus.Idle, null, 0, false, null, null, null),
            new ReadToAnkiFeatureState(null, ReadToAnkiPlaybackStatus.Idle, ReadToAnkiPipelineStatus.Idle, false, null));
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

        internal bool TryReduce(Func<DesktopApplicationState, bool> predicate, Func<DesktopApplicationState, DesktopApplicationState> reducer)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (reducer == null) throw new ArgumentNullException(nameof(reducer));
            DesktopApplicationState next;
            lock (gate)
            {
                if (!predicate(current)) return false;
                next = reducer(current) ?? throw new InvalidOperationException("application-reducer-returned-null");
                current = next with { Revision = current.Revision + 1 };
                next = current;
            }
            Changed?.Invoke(next);
            return true;
        }
    }

    internal static class ModuleActionReducers
    {
        internal static DesktopApplicationState Started(DesktopApplicationState state, ModuleActionKind kind, long generation = 0)
        {
            var actions = new Dictionary<ModuleActionKind, ModuleActionState>(state.ModuleActions)
            {
                [kind] = new ModuleActionState(kind, OperationStatus.Running, null, new Dictionary<string, object>()) { BindingGeneration = generation }
            };
            return state with { ModuleActions = actions };
        }

        internal static DesktopApplicationState Completed(DesktopApplicationState state, ModuleActionKind kind, ModuleActionResult result)
        {
            var actions = new Dictionary<ModuleActionKind, ModuleActionState>(state.ModuleActions)
            {
                [kind] = new ModuleActionState(kind, result.Accepted || result.Status is OperationStatus.Cancelled or OperationStatus.Unknown ? result.Status : OperationStatus.Failed, result.NoticeCode, result.Values ?? new Dictionary<string, object>())
                {
                    AnkiReadiness = result.AnkiReadiness, Output = result.Output
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
        internal static DesktopApplicationState Unresolved(DesktopApplicationState state, string notice) => state with { Question = state.Question with { Status = OperationStatus.Unknown, NoticeCode = notice } };
        internal static DesktopApplicationState Submitting(DesktopApplicationState state) => state with { Question = state.Question with { BindingGeneration = state.Question.BindingGeneration + 1, Status = OperationStatus.Running, AnswerAvailable = false, Answer = null, AnswerText = null, NoticeCode = null, CardBundle = null, DestinationEvidence = null } };
        internal static DesktopApplicationState Submitted(DesktopApplicationState state, TaskCommandResult result) => result.Accepted && (string.IsNullOrWhiteSpace(result.SessionId) || string.IsNullOrWhiteSpace(result.TaskId))
            ? state with { Question = state.Question with { Status = OperationStatus.Unknown, TaskId = result.TaskId, SessionId = null, NoticeCode = "question.invalid-session-receipt" } }
            : state with
        {
            Question = result.Accepted
                ? state.Question with { Status = OperationStatus.Accepted, TaskId = result.TaskId, SessionId = result.SessionId, AnswerAvailable = false, Answer = null, AnswerText = null, NoticeCode = result.NoticeCode, CardBundle = null, DestinationEvidence = null }
                : state.Question with { Status = OperationStatus.Failed, NoticeCode = result.NoticeCode }
        };
        internal static DesktopApplicationState Projected(DesktopApplicationState state, QuestionProjection projection, QuestionAnswerContent answer) => projection.TaskId != state.Question.TaskId || string.IsNullOrWhiteSpace(state.Question.SessionId) ? state : state with
        {
            Question = new QuestionFeatureState(projection.Status, projection.TaskId, projection.TurnCount, projection.AnswerAvailable, projection.Answer, answer == null ? null : answer.Text, projection.NoticeCode)
            {
                SessionId = state.Question.SessionId,
                BindingGeneration = state.Question.BindingGeneration,
                CardBundle = projection.CardBundle,
                DestinationEvidence = projection.DestinationEvidence
            }
        };
        internal static DesktopApplicationState Failed(DesktopApplicationState state, string notice) => state with { Question = state.Question with { Status = OperationStatus.Failed, NoticeCode = notice } };
        internal static DesktopApplicationState Cancelled(DesktopApplicationState state, string notice) => state with { Question = state.Question with { Status = OperationStatus.Cancelled, NoticeCode = notice } };
    }

    internal static class ReadToAnkiReducers
    {
        internal static DesktopApplicationState Started(DesktopApplicationState state) => state with
        {
            ReadToAnki = new ReadToAnkiFeatureState(null, ReadToAnkiPlaybackStatus.Accepted, ReadToAnkiPipelineStatus.Accepted, false, null)
        };

        internal static DesktopApplicationState Submitted(DesktopApplicationState state, ReadToAnkiSubmissionResult result) => state with
        {
            ReadToAnki = result.Accepted
                ? state.ReadToAnki with { OperationId = result.OperationId, ActiveCommandId = result.CommandId, AcceptedSequence = result.AcceptedSequence, Pipeline = ReadToAnkiPipelineStatus.Accepted, AttentionRequired = false, NoticeCode = result.NoticeCode }
                : state.ReadToAnki with { Playback = ReadToAnkiPlaybackStatus.Failed, Pipeline = ReadToAnkiPipelineStatus.Failed, NoticeCode = result.NoticeCode }
        };

        internal static DesktopApplicationState ResumeSubmitted(DesktopApplicationState state, ReadToAnkiSubmissionResult result) => state with
        {
            ReadToAnki = result.Accepted
                ? state.ReadToAnki with { OperationId = result.OperationId, ActiveCommandId = result.CommandId, AcceptedSequence = result.AcceptedSequence, Pipeline = ReadToAnkiPipelineStatus.Accepted, AttentionRequired = false, NoticeCode = result.NoticeCode }
                : state.ReadToAnki with { Pipeline = ReadToAnkiPipelineStatus.Failed, AttentionRequired = false, NoticeCode = result.NoticeCode }
        };

        internal static DesktopApplicationState Projected(DesktopApplicationState state, ReadToAnkiProjection projection) => state with
        {
            ReadToAnki = state.ReadToAnki with
            {
                OperationId = projection.OperationId,
                Playback =
                state.ReadToAnki.PlaybackOwnedLocally ? state.ReadToAnki.Playback : projection.Playback,
                Pipeline = projection.Pipeline, AttentionRequired = projection.AttentionRequired,
                NoticeCode = state.ReadToAnki.PlaybackOwnedLocally ? state.ReadToAnki.NoticeCode : projection.NoticeCode,
                ActiveCommandId = projection.ActionState == "running" ? projection.ActiveCommandId : null,
                AcceptedSequence = state.ReadToAnki.AcceptedSequence,
                CardResolution = projection.CardResolution,
                PlaybackCommandId = state.ReadToAnki.PlaybackCommandId,
                PlaybackAcceptedSequence = state.ReadToAnki.PlaybackAcceptedSequence,
                PlaybackOwnedLocally = state.ReadToAnki.PlaybackOwnedLocally
            }
        };

        internal static DesktopApplicationState ObservationInterrupted(DesktopApplicationState state, string notice) => state with
        {
            ReadToAnki = state.ReadToAnki with
            {
                Playback = state.ReadToAnki.PlaybackOwnedLocally ? state.ReadToAnki.Playback : ReadToAnkiPlaybackStatus.Unknown,
                Pipeline = ReadToAnkiPipelineStatus.UnknownExternalState,
                AttentionRequired = true, NoticeCode = notice
            }
        };

        internal static DesktopApplicationState ReplayCompleted(DesktopApplicationState state, ReadToAnkiPlaybackCommandResult result) => state with
        {
            ReadToAnki = result.Accepted
                ? state.ReadToAnki with { Playback = ReadToAnkiPlaybackStatus.Accepted, PlaybackOwnedLocally = true, PlaybackCommandId = result.CommandId, PlaybackAcceptedSequence = result.AcceptedSequence, NoticeCode = result.NoticeCode }
                : state.ReadToAnki with { Playback = result.OutcomeUnknown ? ReadToAnkiPlaybackStatus.Unknown : ReadToAnkiPlaybackStatus.Failed, PlaybackOwnedLocally = true, NoticeCode = result.NoticeCode }
        };

        internal static DesktopApplicationState Failed(DesktopApplicationState state, string notice) => state with
        {
            ReadToAnki = state.ReadToAnki with
            {
                Playback = state.ReadToAnki.Playback is ReadToAnkiPlaybackStatus.IdleReplayable or ReadToAnkiPlaybackStatus.Stopped
                    ? state.ReadToAnki.Playback
                    : ReadToAnkiPlaybackStatus.Failed,
                Pipeline = ReadToAnkiPipelineStatus.Failed,
                AttentionRequired = false,
                NoticeCode = notice
            }
        };
    }
}

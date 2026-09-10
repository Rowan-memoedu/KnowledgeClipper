using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal enum OperationStatus { Idle, Accepted, Running, WaitingInput, Succeeded, Failed, Cancelled, Unknown }
    internal enum ModuleActionKind { AnkiProbe, RemNoteProbe, RemNoteDiscoverTarget, SpeechVoiceDiscovery, SpeechPreview, ContentCopy, ContentValidate, ContentDiff, RemNoteRestoreBinding }
    internal enum ApplicationCommandKind { AnkiProbe, RemNoteDiscoverTarget, QuestionInitial, QuestionFollowUp, QuestionMakeCards, ClipToAnki, ReadToAnki, ReadToAnkiReplay, ReadToAnkiStop, ReadToAnkiResumeChoice, ReadToAnkiResolve, RemNoteProbe }
    internal enum ReadToAnkiPlaybackStatus { Idle, NotRequested, Accepted, Playing, IdleReplayable, Failed, Stopped, Unknown, StopRequested }
    internal enum ReadToAnkiPipelineStatus { Idle, Accepted, Reviewing, WaitingUserChoice, AwaitingCardResolution, AwaitingCardConfirmation, CreatingCards, WritingAnki, Completed, Cancelled, PartiallyFailed, Failed, UnknownExternalState }
    internal enum LanguagePracticeChoice { MeaningReading, Writing, Both }

    internal sealed record ArtifactHandle(string Id, string Kind);
    internal sealed record QuestionAnswerContent(string Text);
    internal sealed record AnkiReadinessSummary(
        bool CurrentTargetReady,
        bool OptionalRoutesUninitialized,
        string CurrentTarget,
        IReadOnlyList<string> UninitializedRoutes);
    internal sealed record TaskCommandResult(bool Accepted, string TaskId, string NoticeCode)
    {
        internal string SessionId { get; init; }
    }
    internal sealed record StageControlResult(bool Accepted, string State, string NoticeCode);
    internal sealed record ModuleActionRequest(ModuleActionKind Kind, IReadOnlyDictionary<string, object> Arguments);
    internal sealed record ModuleActionResult(bool Accepted, OperationStatus Status, string NoticeCode, IReadOnlyDictionary<string, object> Values, ArtifactHandle Output, bool ExternalMutation, bool PhysicalAudio)
    {
        internal AnkiReadinessSummary AnkiReadiness { get; init; }
    }
    internal sealed record QuestionSubmission(string Text, bool FollowUp, string SessionId);
    internal sealed record MakeCardsSubmission(string SessionId, string CardStructureId, ArtifactHandle Answer);
    internal sealed record CapturedTextSegment(string Text, int Order, string Role = null);
    internal sealed record CapturedPlainText(string Text)
    {
        internal string TargetText { get; init; } = Text;
        internal IReadOnlyList<CapturedTextSegment> OrderedContextSegments { get; init; } = Array.Empty<CapturedTextSegment>();
        internal bool ContextRequested { get; init; }
        internal string ContextGranularity { get; init; }
        internal string SourceWindow { get; init; }
    }
    internal sealed record ImmutablePlainText(string Text, bool IsContext = false);
    internal sealed record ContextArtifactSegment(ArtifactHandle Artifact, string Role, int Order);
    internal sealed record ReadToAnkiSubmission(ArtifactHandle SourceText, bool IncludeSpeech = true)
    {
        internal string TargetText { get; init; }
        internal IReadOnlyList<ArtifactHandle> OrderedContextSegments { get; init; } = Array.Empty<ArtifactHandle>();
        internal IReadOnlyList<ContextArtifactSegment> RoleBoundContextSegments { get; init; } = Array.Empty<ContextArtifactSegment>();
        internal bool ContextRequested { get; init; }
        internal string ContextGranularity { get; init; }
        internal string SourceWindow { get; init; }
    }
    internal sealed record ReadToAnkiSubmissionResult(bool Accepted, string OperationId, string NoticeCode)
    {
        internal string CommandId { get; init; }
        internal long AcceptedSequence { get; init; }
    }
    internal sealed record ReadToAnkiChoiceSubmission(string OperationId, LanguagePracticeChoice Choice);
    internal sealed record CardResolutionOccurrence(string SegmentId, int Order, int Start, int End, string ExactText, string SourceSha256);
    internal sealed record CardResolutionPrompt(string TargetToken, string Revision, string DecisionId, string ReasonCode, IReadOnlyList<string> Actions, IReadOnlyList<CardResolutionOccurrence> CandidateOccurrences);
    internal sealed record CardResolutionSubmission(string OperationId, string Action, string SelectedText, CardResolutionOccurrence CandidateOccurrence);
    internal sealed record ReadToAnkiPlaybackCommandResult(bool Accepted, string NoticeCode)
    {
        internal string OperationId { get; init; }
        internal string CommandId { get; init; }
        internal long AcceptedSequence { get; init; }
        internal bool OutcomeUnknown { get; init; }
    }
    internal sealed record PlaybackTarget(string OperationId, string TargetToken, string ActionCommandId, string AttemptId);
    internal sealed record PlaybackControlSnapshot(string OperationId, ReadToAnkiPlaybackStatus State, bool CanStop, PlaybackTarget Target, long Sequence);
    internal sealed record PlaybackStopAcceptance(bool Accepted, string CommandId, long AcceptedSequence, PlaybackTarget Target);
    internal sealed record ReadToAnkiProjection(
        string OperationId,
        ReadToAnkiPlaybackStatus Playback,
        ReadToAnkiPipelineStatus Pipeline,
        bool AttentionRequired,
        string NoticeCode)
    {
        internal FlashcardDraftProjection FlashcardDraft { get; init; }
        internal string ActionState { get; init; }
        internal string ActiveCommandId { get; init; }
        internal string TerminalCommandId { get; init; }
        internal long TerminalSequence { get; init; }
        internal CardResolutionPrompt CardResolution { get; init; }
    }
    internal sealed record QuestionProjection(string TaskId, OperationStatus Status, int TurnCount, bool AnswerAvailable, ArtifactHandle Answer, string NoticeCode)
    {
        internal ArtifactHandle CardBundle { get; init; }
        internal ArtifactHandle DestinationEvidence { get; init; }
        internal FlashcardDraftProjection FlashcardDraft { get; init; }
    }
    internal sealed record TaskProjection(string TaskId, OperationStatus Status, string AttentionKind, string NoticeCode);
    internal sealed record SettingsSnapshot(string ActiveProfileId, string Revision, IReadOnlyDictionary<string, object> Values);
    internal sealed record CredentialMetadata(string CredentialId, bool Configured, bool Verified, string ProtectionScope);
    internal sealed record ApplicationCommand(ApplicationCommandKind Kind, string Text = null, string CardStructureId = null, LanguagePracticeChoice? LanguageChoice = null)
    {
        internal IReadOnlyDictionary<string, object> Arguments { get; init; }
        internal CardResolutionSubmission CardResolution { get; init; }
        internal PlaybackTarget PlaybackTarget { get; init; }
    }
}

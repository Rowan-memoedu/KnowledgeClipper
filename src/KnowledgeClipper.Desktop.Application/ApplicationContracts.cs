using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal enum OperationStatus { Idle, Accepted, Running, WaitingInput, Succeeded, Failed, Cancelled, Unknown }
    internal enum ModuleActionKind { AnkiProbe, RemNoteProbe, RemNoteDiscoverTarget, SpeechVoiceDiscovery, SpeechPreview, ContentCopy, ContentValidate, ContentDiff }
    internal enum ApplicationCommandKind { AnkiProbe, RemNoteDiscoverTarget, QuestionInitial, QuestionFollowUp, QuestionMakeCards }

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
    internal sealed record QuestionProjection(string TaskId, OperationStatus Status, int TurnCount, bool AnswerAvailable, ArtifactHandle Answer, string NoticeCode)
    {
        internal ArtifactHandle CardBundle { get; init; }
        internal ArtifactHandle DestinationEvidence { get; init; }
    }
    internal sealed record TaskProjection(string TaskId, OperationStatus Status, string AttentionKind, string NoticeCode);
    internal sealed record SettingsSnapshot(string ActiveProfileId, string Revision, IReadOnlyDictionary<string, object> Values);
    internal sealed record CredentialMetadata(string CredentialId, bool Configured, bool Verified, string ProtectionScope);
    internal sealed record ApplicationCommand(ApplicationCommandKind Kind, string Text = null, string CardStructureId = null);
}

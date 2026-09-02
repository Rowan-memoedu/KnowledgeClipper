using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeClipper.Orchestrator.Core;

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(ContractJson.Options)
    {
        WriteIndented = false
    };
}

public sealed record ArtifactReference(
    string ArtifactRef,
    string Kind,
    string ContractVersion,
    string ManifestPath,
    string ExpectedSha256);

public sealed record RetryPolicy(int MaxAttempts, int BackoffMs);

public sealed record PartialRetryPolicy(
    string Strategy,
    int MaxContinuationAttempts);

public sealed record PipelineStage(
    string StageId,
    string WorkerId,
    string Operation,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<ArtifactReference> InputRefs,
    IReadOnlyList<string> OutputKinds,
    int TimeoutMs,
    RetryPolicy RetryPolicy,
    JsonElement ConfigSnapshot,
    JsonElement Payload,
    IReadOnlyList<StageOutputBinding>? InputBindings = null,
    StageInputAdapter? InputAdapter = null,
    StageCompletionPolicy? CompletionPolicy = null,
    PartialRetryPolicy? PartialRetryPolicy = null);

public sealed record StageOutputBinding(
    string FromStageId,
    string Kind,
    bool Required);

public sealed record StageInputAdapter(
    string Type,
    string? TargetKind = null,
    JsonElement? MappingSnapshot = null);

public sealed record StageCompletionPolicy(
    IReadOnlyList<string> ToleratedCancellationReasons,
    string Outcome);

public sealed record DesktopStageControl(
    string SchemaVersion,
    string Action);

public sealed record LanguagePracticeDecisionBinding(
    string TaskId,
    string RunId,
    string StageId,
    string AttemptId,
    string OperationId);

public sealed record LanguagePracticeDecision(
    string SchemaVersion,
    string DecisionKind,
    string TargetToken,
    LanguagePracticeDecisionBinding Binding,
    ArtifactReference SourceDecisionArtifact,
    int DecisionCount,
    IReadOnlyList<string> CategoryIds,
    IReadOnlyList<string> Choices,
    DateTimeOffset RequestedAt);

public sealed record LanguagePracticeChoice(
    string SchemaVersion,
    string DecisionKind,
    string TargetToken,
    string Choice);

public sealed record QuestionWorkflowBinding(
    string TaskId,
    string RunId,
    string StageId,
    string AttemptId,
    string OperationId);

public sealed record QuestionWorkflowSession(
    string SchemaVersion,
    string TargetToken,
    QuestionWorkflowBinding Binding,
    ArtifactReference SourceAnswerArtifact,
    ArtifactReference DisplayAnswerArtifact,
    int AnsweredTurnCount,
    string LatestAnswerId,
    DateTimeOffset RequestedAt);

public sealed record QuestionWorkflowAction(
    string SchemaVersion,
    string Action,
    string TargetToken,
    int? TurnIndex,
    ArtifactReference? QuestionTurnRef,
    ArtifactReference? CardStructureRef);

public sealed record OcrHumanResolutionBinding(
    string TaskId,
    string RunId,
    string StageId,
    string AttemptId,
    string OperationId);

public sealed record OcrHumanResolutionSession(
    string SchemaVersion,
    string TargetToken,
    OcrHumanResolutionBinding Binding,
    ArtifactReference SourceProofreadArtifact,
    ArtifactReference SourceLearningArtifact,
    ArtifactReference SourceImageProvenanceArtifact,
    ArtifactReference SourceOriginalImageArtifact,
    ArtifactReference OriginalTextRef,
    ArtifactReference CandidateTextRef,
    string SourceItemId,
    string OriginalTextSha256,
    string CandidateTextSha256,
    JsonElement Diff,
    JsonElement Reason,
    IReadOnlyList<string> AllowedActions,
    DateTimeOffset RequestedAt);

public sealed record OcrHumanResolutionAction(
    string SchemaVersion,
    string DecisionKind,
    string TargetToken,
    string Action,
    string Reason,
    ArtifactReference? CorrectionRef,
    JsonElement? Diffs);

public sealed record OcrHumanResolutionProjection(
    string DecisionKind,
    string TargetToken,
    ArtifactReference SourceProofreadRef,
    ArtifactReference SourceImageProvenanceRef,
    ArtifactReference SourceOriginalImageRef,
    ArtifactReference OriginalTextRef,
    ArtifactReference CandidateTextRef,
    JsonElement Diff,
    JsonElement Reason,
    IReadOnlyList<string> AllowedActions);

public sealed record PipelinePlan(
    string SchemaVersion,
    string PlanVersion,
    string TaskId,
    int MaxConcurrency,
    IReadOnlyList<PipelineStage> Stages,
    PipelineRoutingSnapshot? Routing = null,
    PipelineTerminalBoundary? TerminalBoundary = null);

public sealed record PipelineRoutingSnapshot(
    string ProcessingProfile,
    IReadOnlyList<string> Destinations,
    string Source);

public sealed record PipelineTerminalBoundary(
    string Status,
    string Code,
    IReadOnlyList<string> AvailableOutputKinds,
    IReadOnlyList<string> UnsupportedConsumers);

public sealed record DesktopIntentDocument(
    string SchemaVersion,
    string TaskId,
    string Intent,
    string InputArtifactRef,
    IReadOnlyList<string> RequestedOutputs);

public sealed record LegacyProfileRoute(
    string ProcessingProfile,
    IReadOnlyList<string> Destinations);

public sealed record RemNoteExportProfile(
    string Sink,
    string RootRemId,
    string RootTitle,
    string DocumentTitlePrefix);

public sealed record SpeechProfile(
    IReadOnlyDictionary<string, string> VoicesByLanguage,
    int RatePercent,
    int VolumePercent,
    int PitchHz,
    string? EdgeTtsPath,
    string? FfplayPath);

public sealed record DesktopProfileResolverConfiguration(
    string SchemaVersion,
    string DefaultProfile,
    IReadOnlyList<string> Profiles,
    RemNoteExportProfile? RemNoteExport,
    RemNoteProjectionProfile RemNoteProjection,
    SpeechProfile Speech,
    AnkiProjectionProfile AnkiProjection);

public sealed record KnowledgeClipperProfileResolverV2(
    string SchemaVersion,
    string DefaultProcessingProfile,
    IReadOnlyList<string> ProcessingProfiles,
    IReadOnlyList<string> DefaultDestinations,
    IReadOnlyList<string> Destinations,
    IReadOnlyDictionary<string, LegacyProfileRoute> LegacyProfileAliases,
    DestinationConfigurations DestinationConfigurations,
    SpeechProfile Speech);

public sealed record DestinationConfigurations(
    RemNoteDestinationConfiguration Remnote,
    AnkiDestinationConfiguration Anki);

public sealed record RemNoteDestinationConfiguration(
    RemNoteExportProfile? Export,
    RemNoteProjectionProfile Projection);

public sealed record AnkiDestinationConfiguration(
    AnkiProjectionProfile Projection);

public sealed record DestinationNeutralResolverConfiguration(
    string SchemaVersion,
    string DefaultProcessingProfile,
    IReadOnlyList<string> ProcessingProfiles,
    IReadOnlyList<string> DefaultDestinations,
    IReadOnlyList<string> Destinations,
    IReadOnlyDictionary<string, LegacyProfileRoute> LegacyProfileAliases,
    RemNoteExportProfile? RemNoteExport,
    RemNoteProjectionProfile RemNoteProjection,
    SpeechProfile Speech,
    AnkiProjectionProfile AnkiProjection);

public sealed record RemNoteProjectionProfile(
    string CategoryRegistryVersion,
    IReadOnlyDictionary<string, RemNoteTemplateMapping> TemplateMappings,
    IReadOnlyList<string> AllowedTextBlockTypes,
    string BlockRendering,
    string AssetRefPolicy);

public sealed record RemNoteTemplateMapping(
    string Policy,
    string? CardType,
    string? Direction);

public sealed record AnkiProjectionProfile(
    string CategoryRegistryVersion,
    IReadOnlyDictionary<string, string> CategoryToDeckRoute,
    IReadOnlyDictionary<string, AnkiTemplateMapping> TemplateMappings,
    string RevisionPolicy,
    string ContentHashPolicy,
    IReadOnlyList<string> Tags,
    string AssetRefPolicy);

public sealed record AnkiTemplateMapping(
    string Policy,
    string? NoteTypeKey,
    string? FrontField,
    string? BackField);

public sealed class OrchestratorDispatchException : Exception
{
    public OrchestratorDispatchException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public sealed record WorkerEntrypoint(string Path, IReadOnlyList<string> Args);

public sealed record WorkerEntry(
    string WorkerId,
    string ContractVersionRange,
    WorkerEntrypoint Entrypoint,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> InputKinds,
    IReadOnlyList<string> OutputKinds,
    IReadOnlyList<string> RequiredForProfiles,
    string SideEffectLevel,
    string IdempotencyLevel,
    bool EnabledByDefault);

public sealed record WorkerRegistry(
    string SchemaVersion,
    string RegistryVersion,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<WorkerEntry> Workers);

public sealed record WorkerIdentity(string Id, string Version);

public sealed record WorkerConcurrency(string Model, int MaxParallelAttempts);

public sealed record WorkerCancellationCapability(string Protocol, IReadOnlyList<string> SupportedReasons);

public sealed record WorkerDescribe(
    string SchemaVersion,
    string WorkerId,
    string WorkerVersion,
    string ContractVersion,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> InputKinds,
    IReadOnlyList<string> OutputKinds,
    WorkerConcurrency Concurrency,
    string SideEffectLevel,
    string IdempotencyLevel,
    WorkerCancellationCapability Cancellation);

public sealed record CancelSignalReference(string SchemaVersion, string Path, int GracePeriodMs);

public sealed record WorkerCancelSignal(
    string SchemaVersion,
    string AttemptId,
    string OperationId,
    string Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset GraceDeadline,
    string? CommandId = null);

public sealed record TaskCancelPayload(string SchemaVersion, string TaskId, string OperationId, string Reason);

public sealed record WorkerError(
    string Code,
    string Category,
    bool Retryable,
    string DetailsRef,
    string? Message = null,
    string? Blame = null);

public sealed record ItemDiagnostic(
    string ItemId,
    string Status,
    IReadOnlyList<ArtifactReference> Outputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerError? Error);

public sealed record WorkerRequest(
    string SchemaVersion,
    string RequestId,
    string OperationId,
    string TaskId,
    string RunId,
    string StageId,
    string AttemptId,
    string WorkerId,
    string Operation,
    string IdempotencyKey,
    string AttemptPath,
    string OutputPath,
    string ProgressPath,
    string ResultPath,
    CancelSignalReference CancelSignal,
    IReadOnlyList<ArtifactReference> InputRefs,
    JsonElement ConfigSnapshot,
    JsonElement Payload);

public sealed record WorkerResult(
    string SchemaVersion,
    string RequestId,
    string OperationId,
    string TaskId,
    string RunId,
    string StageId,
    string AttemptId,
    WorkerIdentity Worker,
    string ContractVersion,
    string IdempotencyKey,
    string Status,
    IReadOnlyList<ArtifactReference> Outputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerError? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ItemDiagnostic>? ItemDiagnostics = null,
    JsonElement? Payload = null);

public sealed record TaskEvent(
    string SchemaVersion,
    long Sequence,
    string EventId,
    DateTimeOffset Timestamp,
    string Type,
    string TaskId,
    string? RunId,
    string? StageId,
    string? AttemptId,
    string? CommandId,
    string? IdempotencyKey,
    JsonElement Payload);

public sealed record TaskCommand(
    string SchemaVersion,
    string CommandId,
    string OperationId,
    string? TaskId,
    string Type,
    DateTimeOffset IssuedAt,
    string IdempotencyKey,
    string PayloadRef,
    string? ExpectedState);

public sealed record CommandReceipt(
    string SchemaVersion,
    string CommandId,
    bool Accepted,
    string TaskId,
    string OperationId,
    string Status,
    DateTimeOffset Timestamp,
    WorkerError? Error,
    string? DurableEventRef);

public sealed record IpcConfiguration(
    string SchemaVersion,
    string Transport,
    string PipeName,
    string Endpoint,
    string ServerOwnership,
    string AccessControl,
    IReadOnlyList<string> AllowedMessages,
    bool InlineContentProhibited,
    string PersistentSourceOfTruth,
    int MaxMessageBytes,
    IpcFraming Framing,
    string RequestSchema,
    string ResponseSchema,
    string ProtocolErrorBehavior);

public sealed record IpcFraming(
    string Protocol,
    int RequestPerConnection,
    int ResponsePerConnection,
    string Delimiter,
    IReadOnlyList<string> AcceptedDelimiter,
    bool BomAllowed,
    string JsonNewlines,
    bool ByteLimitExcludesDelimiter,
    bool ServerPush,
    string ConnectionMode,
    string Concurrency);

public sealed record IpcRequestEnvelope(
    string ProtocolVersion,
    string CorrelationId,
    string MessageType,
    JsonElement Payload);

public sealed record IpcProtocolError(string Code, string Category, string Message);

public sealed record IpcResponseEnvelope(
    string ProtocolVersion,
    string CorrelationId,
    string MessageType,
    bool Ok,
    object? Payload,
    IpcProtocolError? Error);

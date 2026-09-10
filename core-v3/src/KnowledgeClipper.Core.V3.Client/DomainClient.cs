using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;

namespace KnowledgeClipper.Core.V3.Client;

public sealed record CoreV3ClientOptions(
    TimeSpan RequestTimeout,
    TimeSpan ObservationTimeout,
    TimeSpan PollInterval)
{
    public static CoreV3ClientOptions Default { get; } = new(
        RequestTimeout: TimeSpan.FromSeconds(10),
        ObservationTimeout: TimeSpan.FromMinutes(2),
        PollInterval: TimeSpan.FromMilliseconds(250));
}

public enum QuestionProjectionState
{
    Running,
    WaitingInput,
    Succeeded,
    Failed,
    Cancelled,
    Degraded
}

public sealed record CapabilityAvailability(string Operation, bool Available, string? AvailabilityReason);

public sealed record CapabilitySnapshot(string CatalogId, string CatalogSha256, IReadOnlyList<CapabilityAvailability> Operations);

public enum TaskProjectionState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Degraded
}

public enum OperationProjectionState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Degraded
}

public enum ReadAloudCardsPlaybackState { NotRequested, Accepted, Playing, IdleReplayable, Failed, Stopped, Unknown }

public enum ReadAloudCardsPipelineState { Accepted, Reviewing, WaitingLanguageCardChoice, AwaitingCardResolution, AwaitingCardConfirmation, WritingAnki, Completed, Cancelled, PartiallyFailed, Failed, UnknownExternalState }

public enum ReadAloudCardsActionState { Running, Completed, Failed, Cancelled, UnknownExternalState, NotFound }

public sealed record ContentSettingsDescription(string SettingsContract, IReadOnlyList<string> SupportedActions, string ExecutionMode, int ExternalWriteCount);

public sealed class TypedArtifactHandle : IEquatable<TypedArtifactHandle>
{
    internal TypedArtifactHandle(ArtifactReference reference)
    {
        Reference = reference;
        Token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(reference))).ToLowerInvariant();
    }

    public string Token { get; }

    internal ArtifactReference Reference { get; }

    public bool Equals(TypedArtifactHandle? other) => other is not null && Token == other.Token;

    public override bool Equals(object? obj) => obj is TypedArtifactHandle other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Token);
}

public enum ArtifactPurpose
{
    CaptureText,
    Question,
    QuestionContext,
    QuestionFollowUp,
    QuestionAnswer,
    AnkiConfiguration,
    RemNoteSettings
}

public sealed record PublishTextArtifactRequest(string RequestId, ArtifactPurpose Purpose, string Text, string DataClass = "personal");

public sealed record TypedArtifactContent(
    TypedArtifactHandle Artifact,
    string RepresentationId,
    string MediaType,
    string? ContractSchema,
    byte[] Content);

public sealed record AnkiProbeRequest(string RequestId, TypedArtifactHandle Configuration, string SelectedRoute = "canary");

public sealed record AnkiReadinessSummary(
    bool ConnectionReady,
    string SelectedRoute,
    bool SelectedRouteReady,
    bool ReadyForRequestedAction,
    string OverallState,
    IReadOnlyList<string> NonBlockingIssues,
    IReadOnlyList<string> BlockingIssues);

public sealed record QuestionInitialRequest(string RequestId, TypedArtifactHandle Question, IReadOnlyList<TypedArtifactHandle> Context,
    TypedArtifactHandle? AnkiConfiguration = null, AnkiWriteAuthorization? Authorization = null);

public sealed record QuestionFollowUpRequest(string RequestId, string SessionId, TypedArtifactHandle FollowUp,
    TypedArtifactHandle? AnkiConfiguration = null, AnkiWriteAuthorization? Authorization = null);

public sealed record AnkiWriteAuthorization(string AuthorizationId, string Scope = "isolated-canary-deck");

/// <summary>
/// The single public construction point for Anki write authority.  Desktop and
/// other typed consumers do not construct low-level AuthorizationReference
/// values or choose a scope string themselves.
/// </summary>
public static class AnkiWriteAuthorizationBuilder
{
    public const string IsolatedCanaryDeckScope = "isolated-canary-deck";

    public static AnkiWriteAuthorization ForIsolatedCanaryDeck(string authorizationId)
    {
        if (string.IsNullOrWhiteSpace(authorizationId) || authorizationId.Length > 128 ||
            !authorizationId.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            throw new ArgumentException("Anki authorizationId must be a non-empty kernel identifier.", nameof(authorizationId));
        return new AnkiWriteAuthorization(authorizationId, IsolatedCanaryDeckScope);
    }
}

/// <summary>
/// Keeps one typed authorization attached to the operation created by an
/// explicit user action. UI callers retain only the operation id; follow-up
/// actions cannot borrow authority from another operation.
/// </summary>
public sealed class ReadToAnkiOperationAuthorizationCoordinator
{
    private readonly ConcurrentDictionary<string, AnkiWriteAuthorization> _authorizations = new(StringComparer.Ordinal);

    public void Bind(OperationSubmission submission, AnkiWriteAuthorization authorization)
    {
        if (submission is null || !submission.Accepted || string.IsNullOrWhiteSpace(submission.OperationId))
            throw new ArgumentException("Only an accepted operation can retain Anki authorization.", nameof(submission));
        var normalized = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck(authorization?.AuthorizationId
            ?? throw new ArgumentNullException(nameof(authorization)));
        if (!string.Equals(authorization.Scope, normalized.Scope, StringComparison.Ordinal))
            throw new ArgumentException("Anki authorization has an unsupported scope.", nameof(authorization));
        if (!_authorizations.TryAdd(submission.OperationId, normalized) &&
            (!_authorizations.TryGetValue(submission.OperationId, out var existing) || existing != normalized))
            throw new InvalidOperationException("The operation is already bound to different Anki authorization.");
    }

    public AnkiWriteAuthorization Require(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || !_authorizations.TryGetValue(operationId, out var authorization))
            throw new InvalidOperationException("Anki authorization is unavailable for this operation.");
        return authorization;
    }
}

public sealed record ReadAloudCardsRequest(
    string RequestId,
    TypedArtifactHandle Capture,
    TypedArtifactHandle AnkiConfiguration,
    AnkiWriteAuthorization Authorization,
    LanguageCardSettings? LanguageCards = null,
    bool IncludeSpeech = true,
    LanguageSelectionContext? Selection = null);

public sealed record LanguageSelectionContext(
    string TargetText,
    IReadOnlyList<TypedArtifactHandle> OrderedContextSegments,
    bool ContextRequested,
    string ContextGranularity = "sentence",
    string? SourceWindow = null,
    IReadOnlyList<LanguageContextSegment>? RoleBoundContextSegments = null);

/// <summary>
/// A context artifact whose semantic position was supplied by the capture
/// provider. Core never infers prefix/suffix roles from list position.
/// </summary>
public sealed record LanguageContextSegment(TypedArtifactHandle Artifact, string Role, int Order);

public sealed record LanguageCardSettings(string Revision, string DefaultChoice);

public sealed record LanguageCardChoiceRequest(
    string RequestId,
    string OperationId,
    string TargetToken,
    string Choice,
    AnkiWriteAuthorization Authorization);

/// <summary>
/// An explicit user replay of a completed read-and-card operation.  The source
/// operation remains the durable owner of its capture, synthesis, cards and
/// destination evidence; replay only creates a new local playback attempt.
/// </summary>
public sealed record ReadAloudCardsReplayRequest(
    string RequestId,
    string SourceOperationId,
    AnkiWriteAuthorization Authorization);

/// <summary>Destination-neutral immutable preview returned by every card-producing Core operation.</summary>
public sealed record FlashcardDraftCard(string CardId, string Revision, string CardSha256, string Front, string Back, string Template = "basic_qa");

public sealed record FlashcardDraft(string SourceOperation, string Token, string Revision, string DraftSha256, IReadOnlyList<FlashcardDraftCard> Cards);
public sealed record CardGenerationFailure(string Status, string ReasonCode);
public sealed record LanguageCardChoicePrompt(string TargetToken, string SettingsRevision, IReadOnlyList<string> Choices);
public sealed record CardResolutionPrompt(string TargetToken, string Revision, string DecisionId, string ReasonCode, JsonElement Evidence, IReadOnlyList<string> Actions);

public sealed record CandidateOccurrence(
    string SegmentId,
    int Order,
    int Start,
    int End,
    string ExactText,
    string SourceSha256);

public sealed record CardResolutionActionRequest(string RequestId, string OperationId, string TargetToken, string DecisionId, string Action, string SelectedText,
    AnkiWriteAuthorization Authorization, CandidateOccurrence? CandidateOccurrence = null);

public sealed record FlashcardDraftConfirmationRequest(
    string RequestId,
    string OperationId,
    FlashcardDraft Draft,
    AnkiWriteAuthorization Authorization);

public sealed record FlashcardDraftCancellationRequest(string RequestId, string OperationId, FlashcardDraft Draft);

public sealed record AnkiSemanticReadbackCard(long NoteId, IReadOnlyList<long> CardIds, string Front, string Back, string DeckName);

public sealed record AnkiSemanticReadbackSummary(string ArtifactPath, string Sha256, string CardsSha256, IReadOnlyList<AnkiSemanticReadbackCard> Cards);

public sealed record ImmutableArtifactEvidence(string ArtifactId, string Kind, string ManifestPath, string Sha256);

public sealed record EffectiveDestinationEvidence(string RouteKey, string DeckIdentity, string DeckSha256);

public sealed record TaskPackageProvenanceEvidence(string Producer, string InputArtifactId, string InputManifestSha256);

public sealed record ReadToAnkiSettingsEvidence(
    ImmutableArtifactEvidence SettingsSnapshot,
    ImmutableArtifactEvidence AnkiConfiguration,
    EffectiveDestinationEvidence Destination,
    TaskPackageProvenanceEvidence PackageProvenance);

public sealed record OrderedContextArtifactEvidence(int Order, string Role, ImmutableArtifactEvidence Artifact);

/// <summary>
/// Privacy-preserving proof of the exact capture/context inputs frozen by Core
/// for the task. Text bodies and source-window names are deliberately omitted;
/// consumers receive only verified immutable artifact identities and hashes.
/// </summary>
public sealed record ReadToAnkiContextEvidence(
    ImmutableArtifactEvidence ContextSnapshot,
    ImmutableArtifactEvidence Capture,
    string SelectedTextSha256,
    string TargetTextSha256,
    string SpokenTextSha256,
    string SpeechContextMode,
    bool ContextRequested,
    bool ContextApplied,
    string ContextStatus,
    string ContextGranularity,
    IReadOnlyList<OrderedContextArtifactEvidence> ContextSegments);

public sealed class ReadToAnkiDiscoveryCheckpoint
{
    internal ReadToAnkiDiscoveryCheckpoint(string correlationMarker, long cursor, string token)
    {
        CorrelationMarker = correlationMarker;
        Cursor = cursor;
        Token = token;
    }

    public string CorrelationMarker { get; }
    public long Cursor { get; }
    internal string Token { get; }
}

/// <summary>
/// Signed read-only boundary for one subsequent Desktop UI action on an
/// already accepted read-to-Anki operation. It does not submit a command.
/// </summary>
public sealed class ReadToAnkiActionDiscoveryCheckpoint
{
    internal ReadToAnkiActionDiscoveryCheckpoint(string operationId, string correlationMarker, long cursor, string token)
    {
        OperationId = operationId;
        CorrelationMarker = correlationMarker;
        Cursor = cursor;
        Token = token;
    }

    public string OperationId { get; }
    public string CorrelationMarker { get; }
    public long Cursor { get; }
    internal string Token { get; }
}

public sealed record AcceptedOperationIdentity(string OperationId, string CommandId, long AcceptedSequence);

public sealed record ReadAloudCardsProjectionSnapshot(
    string OperationId,
    ReadAloudCardsPlaybackState Playback,
    ReadAloudCardsPipelineState Pipeline,
    long Cursor,
    string? Notice,
    TypedArtifactHandle? CaptureArtifact,
    TypedArtifactHandle? ReviewArtifact,
    TypedArtifactHandle? SynthesisArtifact,
    TypedArtifactHandle? PlaybackArtifact,
    TypedArtifactHandle? CardBundleArtifact,
    TypedArtifactHandle? AnkiReadbackArtifact)
{
    public ReadAloudCardsActionState ActionState { get; init; }
    public string? ActiveCommandId { get; init; }
    public string? TerminalCommandId { get; init; }
    public long TerminalSequence { get; init; }
    public bool AttentionRequired { get; init; }
    public CardGenerationFailure? CardGenerationFailure { get; init; }
    public LanguageCardChoicePrompt? LanguageCardChoice { get; init; }
    public CardResolutionPrompt? CardResolution { get; init; }
    public string? FrozenLanguageCardChoice { get; init; }
    public FlashcardDraft? CardDraft { get; init; }
    public AnkiSemanticReadbackSummary? AnkiSemanticReadback { get; init; }
    public ReadToAnkiSettingsEvidence? SettingsEvidence { get; init; }
    public ReadToAnkiContextEvidence? ContextEvidence { get; init; }
    public TypedArtifactHandle? DestinationSnapshot { get; init; }
    public IReadOnlyList<DestinationProjection> Destinations { get; init; } = Array.Empty<DestinationProjection>();
}

public sealed record QuestionMakeCardsRequest(
    string RequestId,
    string SessionId,
    TypedArtifactHandle Answer,
    TypedArtifactHandle? AnkiConfiguration = null,
    AnkiWriteAuthorization? Authorization = null);

public sealed record OperationSubmission(string OperationId, long Cursor, bool Accepted)
{
    public string? CommandId { get; init; }
    public long AcceptedSequence { get; init; }
}

public sealed record QuestionOperationSubmission(string OperationId, string SessionId, long Cursor, bool Accepted);

public sealed record TaskProjectionSnapshot(string TaskId, TaskProjectionState State, long Cursor, string? Notice);

public sealed record OperationProjectionSnapshot(
    string OperationId,
    OperationProjectionState State,
    long Cursor,
    string? Notice,
    TypedArtifactHandle? AnkiConnectionStatusArtifact)
{
    public AnkiReadinessSummary? AnkiReadiness { get; init; }
    public RemNoteDiscoverySummary? RemNoteDiscovery { get; init; }
    public RemNoteReadinessSummary? RemNoteReadiness { get; init; }
    public TypedArtifactHandle? DestinationSnapshot { get; init; }
    public IReadOnlyList<DestinationProjection> Destinations { get; init; } = Array.Empty<DestinationProjection>();
}

public sealed record QuestionProjectionSnapshot(
    string OperationId,
    QuestionProjectionState State,
    long Cursor,
    string? Notice,
    TypedArtifactHandle? AnswerArtifact,
    TypedArtifactHandle? CardBundleArtifact,
    TypedArtifactHandle? DestinationEvidenceArtifact)
{
    public string? TerminalCommandId { get; init; }
    public FlashcardDraft? CardDraft { get; init; }
    public AnkiSemanticReadbackSummary? AnkiSemanticReadback { get; init; }
    public TypedArtifactHandle? DestinationSnapshot { get; init; }
    public IReadOnlyList<DestinationProjection> Destinations { get; init; } = Array.Empty<DestinationProjection>();
}

public sealed partial class CoreV3Client
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CoreV3ProtocolClient _protocol;
    private readonly CoreV3ClientOptions _options;

    public CoreV3Client(string pipeName, CoreV3ClientOptions? options = null)
    {
        _options = options ?? CoreV3ClientOptions.Default;
        if (_options.RequestTimeout <= TimeSpan.Zero ||
            _options.ObservationTimeout <= TimeSpan.Zero ||
            _options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Client timeout and observation limits must be positive.");
        }

        _protocol = new CoreV3ProtocolClient(pipeName, _options.RequestTimeout);
    }

    public async Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        _ = await _protocol.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        var result = await QueryAsync("capabilities.get", new { }, cancellationToken).ConfigureAwait(false);
        var catalog = result.Data.GetProperty("catalog").Deserialize<CapabilityCatalogDocument>(JsonOptions)
            ?? throw new CoreV3ClientException("capability_payload_invalid", "Capability catalog payload is invalid.");
        return new CapabilitySnapshot(
            catalog.CatalogId,
            result.Data.GetProperty("catalogSha256").GetString()!,
            catalog.Operations.Select(item => new CapabilityAvailability(item.OperationId, item.Available, item.AvailabilityReason)).ToArray());
    }

    public async Task<ContentSettingsDescription> DescribeContentSettingsAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var receipt = await SubmitAsync(
            requestId,
            "content.settings.describe",
            "kc.content-settings-describe-command/1",
            new { includeSchemas = true },
            Array.Empty<ArtifactReference>(),
            cancellationToken).ConfigureAwait(false);
        var result = GetCompletedResult(receipt);
        return result.Deserialize<ContentSettingsDescription>(JsonOptions)
            ?? throw new CoreV3ClientException("operation_payload_invalid", "Content settings description payload is invalid.");
    }

    public async Task<TypedArtifactHandle> PublishTextArtifactAsync(
        PublishTextArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        var purpose = request.Purpose switch
        {
            ArtifactPurpose.CaptureText => "capture-text",
            ArtifactPurpose.Question => "question",
            ArtifactPurpose.QuestionContext => "question-context",
            ArtifactPurpose.QuestionFollowUp => "question-follow-up",
            ArtifactPurpose.QuestionAnswer => "question-answer",
            ArtifactPurpose.AnkiConfiguration => "anki-configuration",
            ArtifactPurpose.RemNoteSettings => "remnote-settings",
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unknown artifact purpose.")
        };
        var receipt = await SubmitAsync(
            request.RequestId,
            "artifact.publish",
            "kc.artifact-publish-command/1",
            new
            {
                purpose,
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Text)),
                request.DataClass
            },
            Array.Empty<ArtifactReference>(),
            cancellationToken).ConfigureAwait(false);
        _ = GetCompletedResult(receipt);
        var artifacts = receipt.Events.Where(item => item.EventType == "operation.completed").SelectMany(item => item.Artifacts).ToArray();
        if (artifacts.Length != 1)
        {
            throw new CoreV3OperationException(
                "artifact_publish_result_invalid",
                "Artifact publish did not return exactly one artifact handle.",
                receipt.OperationId,
                "failed");
        }

        return new TypedArtifactHandle(artifacts[0]);
    }

    public Task<OperationSubmission> AnkiProbeAsync(AnkiProbeRequest request, CancellationToken cancellationToken = default)
    {
        return SubmitDomainOperationAsync(
            request.RequestId,
            "anki.probe",
            "kc.anki-probe-command/1",
            new { configuration = request.Configuration.Reference, selectedRoute = request.SelectedRoute },
            new[] { request.Configuration.Reference },
            cancellationToken);
    }

    public Task<OperationSubmission> ReadAloudCardsAsync(ReadAloudCardsRequest request, CancellationToken cancellationToken = default)
    {
        var context = request.Selection?.OrderedContextSegments ?? Array.Empty<TypedArtifactHandle>();
        var roleBound = request.Selection?.RoleBoundContextSegments ?? Array.Empty<LanguageContextSegment>();
        if (request.Selection is { ContextRequested: false } && context.Count > 0)
            throw new ArgumentException("Context artifacts are not allowed when contextual reading is off.", nameof(request));
        if (request.Selection is { ContextRequested: true } && context.Count > 0)
        {
            if (roleBound.Count != context.Count)
                throw new ArgumentException("Context-enabled read-to-Anki requires one explicit role binding per context artifact.", nameof(request));
            var suffixStarted = false;
            for (var index = 0; index < context.Count; index++)
            {
                var segment = roleBound[index];
                if (segment.Role == "suffix") suffixStarted = true;
                if (segment.Order != index || segment.Role is not ("prefix" or "suffix") || segment.Artifact.Reference != context[index].Reference)
                    throw new ArgumentException("Context role bindings must preserve exact artifact order and use only prefix/suffix roles.", nameof(request));
                if (suffixStarted && segment.Role != "suffix")
                    throw new ArgumentException("Every prefix context segment must precede every suffix segment.", nameof(request));
            }
        }
        else if (roleBound.Count != 0)
        {
            throw new ArgumentException("Context role bindings are not allowed when contextual reading is not active.", nameof(request));
        }
        return SubmitDomainOperationAsync(
            request.RequestId,
            "clip.read-aloud-cards",
            "kc.read-aloud-cards-command/1",
            new { capture = request.Capture.Reference, configuration = request.AnkiConfiguration.Reference, languageCards = request.LanguageCards, includeSpeech = request.IncludeSpeech,
                selection = request.Selection is null ? null : new
                {
                    request.Selection.TargetText,
                    contextSegments = roleBound.Select(item => new { reference = item.Artifact.Reference, item.Role, item.Order }).ToArray(),
                    request.Selection.ContextRequested,
                    request.Selection.ContextGranularity,
                    request.Selection.SourceWindow
                } },
            new[] { request.Capture.Reference, request.AnkiConfiguration.Reference }.Concat(context.Select(item => item.Reference)).ToArray(),
            cancellationToken,
            MapAnkiAuthorization(request.Authorization));
    }

    public Task<OperationSubmission> ClipCardsAsync(ReadAloudCardsRequest request, CancellationToken cancellationToken = default)
        => ReadAloudCardsAsync(request with { IncludeSpeech = false }, cancellationToken);

    /// <summary>
    /// Captures a signed, read-only observation boundary before an external UI
    /// action. It neither constructs nor submits a business command.
    /// </summary>
    public async Task<ReadToAnkiDiscoveryCheckpoint> BeginReadToAnkiDiscoveryAsync(
        string correlationMarker,
        CancellationToken cancellationToken = default)
    {
        RequireCorrelationMarker(correlationMarker);
        var result = await QueryAsync(
            "read-aloud-cards.discovery.begin",
            new { correlationMarker },
            cancellationToken).ConfigureAwait(false);
        var returnedMarker = result.Data.GetProperty("correlationMarker").GetString();
        var token = result.Data.GetProperty("checkpointToken").GetString();
        if (returnedMarker != correlationMarker || string.IsNullOrWhiteSpace(token))
            throw new CoreV3ClientException("operation_discovery_payload_invalid", "Core returned an invalid operation discovery checkpoint.");
        return new ReadToAnkiDiscoveryCheckpoint(correlationMarker, result.Cursor, token);
    }

    public async Task<AcceptedOperationIdentity?> DiscoverAcceptedReadToAnkiAsync(
        ReadToAnkiDiscoveryCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        RequireCorrelationMarker(checkpoint.CorrelationMarker);
        var result = await QueryAsync(
            "read-aloud-cards.discovery.accepted",
            new { correlationMarker = checkpoint.CorrelationMarker, checkpointToken = checkpoint.Token },
            cancellationToken,
            checkpoint.Cursor).ConfigureAwait(false);
        var returnedMarker = result.Data.GetProperty("correlationMarker").GetString();
        var status = result.Data.GetProperty("status").GetString();
        if (returnedMarker != checkpoint.CorrelationMarker)
            throw new CoreV3ClientException("operation_discovery_payload_invalid", "Core returned a mismatched operation discovery marker.");
        if (status == "pending") return null;
        if (status != "accepted")
            throw new CoreV3ClientException("operation_discovery_payload_invalid", "Core returned an unknown operation discovery state.");
        var operationId = result.Data.GetProperty("operationId").GetString();
        var commandId = result.Data.GetProperty("commandId").GetString();
        var acceptedSequence = result.Data.GetProperty("acceptedSequence").GetInt64();
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(commandId) || acceptedSequence <= 0)
            throw new CoreV3ClientException("operation_discovery_payload_invalid", "Core returned an invalid accepted operation identity.");
        return new AcceptedOperationIdentity(operationId, commandId, acceptedSequence);
    }

    public async Task<AcceptedOperationIdentity> WaitForAcceptedReadToAnkiAsync(
        ReadToAnkiDiscoveryCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < _options.ObservationTimeout)
        {
            try
            {
                var accepted = await DiscoverAcceptedReadToAnkiAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                if (accepted is not null) return accepted;
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
            }
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new ReadAloudCardsObservationException(
            "read_to_anki_operation_discovery_timeout",
            $"No unique read-to-Anki operation was accepted after marker '{checkpoint.CorrelationMarker}'.",
            lastProjection: null);
    }

    /// <summary>
    /// Captures a signed boundary immediately before Desktop invokes a
    /// follow-up UI action (language choice, card resolution, confirmation,
    /// cancellation, or replay) on an existing operation stream.
    /// </summary>
    public async Task<ReadToAnkiActionDiscoveryCheckpoint> BeginReadToAnkiActionDiscoveryAsync(
        string operationId,
        string correlationMarker,
        CancellationToken cancellationToken = default)
    {
        RequireRequestId(operationId);
        RequireCorrelationMarker(correlationMarker);
        var result = await QueryAsync(
            "read-aloud-cards.action-discovery.begin",
            new { operationId, correlationMarker },
            cancellationToken).ConfigureAwait(false);
        var returnedOperationId = result.Data.GetProperty("operationId").GetString();
        var returnedMarker = result.Data.GetProperty("correlationMarker").GetString();
        var token = result.Data.GetProperty("checkpointToken").GetString();
        if (returnedOperationId != operationId || returnedMarker != correlationMarker || string.IsNullOrWhiteSpace(token))
            throw new CoreV3ClientException("action_discovery_payload_invalid", "Core returned an invalid action discovery checkpoint.");
        return new ReadToAnkiActionDiscoveryCheckpoint(operationId, correlationMarker, result.Cursor, token);
    }

    public async Task<AcceptedOperationIdentity?> DiscoverAcceptedReadToAnkiActionAsync(
        ReadToAnkiActionDiscoveryCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        RequireRequestId(checkpoint.OperationId);
        RequireCorrelationMarker(checkpoint.CorrelationMarker);
        var result = await QueryAsync(
            "read-aloud-cards.action-discovery.accepted",
            new
            {
                operationId = checkpoint.OperationId,
                correlationMarker = checkpoint.CorrelationMarker,
                checkpointToken = checkpoint.Token
            },
            cancellationToken,
            checkpoint.Cursor).ConfigureAwait(false);
        var returnedMarker = result.Data.GetProperty("correlationMarker").GetString();
        var status = result.Data.GetProperty("status").GetString();
        if (returnedMarker != checkpoint.CorrelationMarker)
            throw new CoreV3ClientException("action_discovery_payload_invalid", "Core returned a mismatched action discovery marker.");
        if (status == "pending") return null;
        if (status != "accepted")
            throw new CoreV3ClientException("action_discovery_payload_invalid", "Core returned an unknown action discovery state.");
        var operationId = result.Data.GetProperty("operationId").GetString();
        var commandId = result.Data.GetProperty("commandId").GetString();
        var acceptedSequence = result.Data.GetProperty("acceptedSequence").GetInt64();
        if (operationId != checkpoint.OperationId || string.IsNullOrWhiteSpace(commandId) || acceptedSequence <= 0)
            throw new CoreV3ClientException("action_discovery_payload_invalid", "Core returned an invalid accepted action identity.");
        return new AcceptedOperationIdentity(operationId, commandId, acceptedSequence);
    }

    public async Task<AcceptedOperationIdentity> WaitForAcceptedReadToAnkiActionAsync(
        ReadToAnkiActionDiscoveryCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < _options.ObservationTimeout)
        {
            try
            {
                var accepted = await DiscoverAcceptedReadToAnkiActionAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                if (accepted is not null) return accepted;
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
            }
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new ReadAloudCardsObservationException(
            "read_to_anki_action_discovery_timeout",
            $"No unique read-to-Anki action was accepted for operation '{checkpoint.OperationId}' after marker '{checkpoint.CorrelationMarker}'.",
            lastProjection: null);
    }

    public Task<OperationSubmission> ResolveCardResolutionAsync(CardResolutionActionRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, "clip.read-aloud-cards", "kc.read-aloud-cards-command/1",
            new { action = "resolve_card_resolution", sourceOperationId = request.OperationId, targetToken = request.TargetToken, decisionId = request.DecisionId, resolutionAction = request.Action, selectedText = request.SelectedText, candidateOccurrence = request.CandidateOccurrence },
            Array.Empty<ArtifactReference>(), cancellationToken, MapAnkiAuthorization(request.Authorization), request.OperationId);

    public Task<OperationSubmission> ChooseLanguageCardTypeAsync(LanguageCardChoiceRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(
            request.RequestId, "clip.read-aloud-cards", "kc.read-aloud-cards-command/1",
            new { action = "choose_language_card", sourceOperationId = request.OperationId, targetToken = request.TargetToken, choice = request.Choice },
            Array.Empty<ArtifactReference>(), cancellationToken, MapAnkiAuthorization(request.Authorization), request.OperationId);

    public Task<OperationSubmission> ReplayReadAloudCardsAsync(ReadAloudCardsReplayRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(
            request.RequestId,
            "clip.read-aloud-cards",
            "kc.read-aloud-cards-command/1",
            new { action = "replay", sourceOperationId = request.SourceOperationId },
            Array.Empty<ArtifactReference>(),
            cancellationToken,
            MapAnkiAuthorization(request.Authorization),
            request.SourceOperationId);

    public Task<OperationSubmission> ConfirmFlashcardDraftAsync(FlashcardDraftConfirmationRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(
            request.RequestId,
            RequireFlashcardSourceOperation(request.Draft.SourceOperation),
            FlashcardCommandSchema(request.Draft.SourceOperation),
            new { action = "confirm_card_draft", sourceOperationId = request.OperationId, draft = new { token = request.Draft.Token, revision = request.Draft.Revision, digest = request.Draft.DraftSha256 } },
            Array.Empty<ArtifactReference>(), cancellationToken,
            MapAnkiAuthorization(request.Authorization), request.OperationId);

    public Task<OperationSubmission> CancelFlashcardDraftAsync(FlashcardDraftCancellationRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(
            request.RequestId,
            RequireFlashcardSourceOperation(request.Draft.SourceOperation),
            FlashcardCommandSchema(request.Draft.SourceOperation),
            new { action = "cancel_card_draft", sourceOperationId = request.OperationId, draft = new { token = request.Draft.Token, revision = request.Draft.Revision, digest = request.Draft.DraftSha256 } },
            Array.Empty<ArtifactReference>(), cancellationToken, authorization: null, request.OperationId);

    public async Task<ReadAloudCardsProjectionSnapshot> GetReadAloudCardsProjectionAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync("read-aloud-cards.projection", new { operationId }, cancellationToken).ConfigureAwait(false);
        var outputs = result.Data.GetProperty("outputs");
        var snapshot = new ReadAloudCardsProjectionSnapshot(
            operationId,
            ParseReadAloudPlayback(result.Data.GetProperty("playback").GetString()),
            ParseReadAloudPipeline(result.Data.GetProperty("pipeline").GetString()),
            result.Cursor, GetOptionalString(result.Data, "notice"),
            ReadOptionalArtifactHandle(outputs, "captureArtifact"),
            ReadOptionalArtifactHandle(outputs, "reviewArtifact"),
            ReadOptionalArtifactHandle(outputs, "synthesisArtifact"),
            ReadOptionalArtifactHandle(outputs, "playbackArtifact"),
            ReadOptionalArtifactHandle(outputs, "cardBundleArtifact"),
            ReadOptionalArtifactHandle(outputs, "ankiReadbackArtifact"))
        {
            DestinationSnapshot = ReadOptionalArtifactHandle(outputs, "destinationSnapshotArtifact"),
            Destinations = ReadDestinations(result.Data),
            ActionState = ParseReadAloudActionState(GetOptionalString(result.Data, "actionState")),
            ActiveCommandId = GetOptionalString(result.Data, "activeCommandId"),
            TerminalCommandId = GetOptionalString(result.Data, "terminalCommandId"),
            TerminalSequence = result.Data.TryGetProperty("terminalSequence", out var terminalSequence) && terminalSequence.TryGetInt64(out var sequence) ? sequence : 0,
            AttentionRequired = result.Data.TryGetProperty("attentionRequired", out var attention) && attention.ValueKind == JsonValueKind.True
        };
        if (result.Data.TryGetProperty("cardDraft", out var draft) && draft.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with { CardDraft = ParseFlashcardDraft(draft) };
        }
        if (result.Data.TryGetProperty("cardGeneration", out var cardGeneration) && cardGeneration.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with
            {
                CardGenerationFailure = cardGeneration.Deserialize<CardGenerationFailure>(JsonOptions)
                    ?? throw new CoreV3ClientException("projection_payload_invalid", "Card generation failure projection is invalid.")
            };
        }
        if (result.Data.TryGetProperty("languageCardChoice", out var languageChoice) && languageChoice.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with { LanguageCardChoice = languageChoice.Deserialize<LanguageCardChoicePrompt>(JsonOptions) };
        }
        if (result.Data.TryGetProperty("cardResolution", out var cardResolution) && cardResolution.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with { CardResolution = cardResolution.Deserialize<CardResolutionPrompt>(JsonOptions) ?? throw new CoreV3ClientException("projection_payload_invalid", "Card resolution projection is invalid.") };
        }
        if (result.Data.TryGetProperty("frozenLanguageCardChoice", out var frozenChoice) && frozenChoice.ValueKind == JsonValueKind.String)
        {
            snapshot = snapshot with { FrozenLanguageCardChoice = frozenChoice.GetString() };
        }
        if (result.Data.TryGetProperty("semanticReadback", out var readback) && readback.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with { AnkiSemanticReadback = ParseAnkiSemanticReadback(readback) };
        }
        if (result.Data.TryGetProperty("settingsEvidence", out var settingsEvidence) && settingsEvidence.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with
            {
                SettingsEvidence = settingsEvidence.Deserialize<ReadToAnkiSettingsEvidence>(JsonOptions)
                    ?? throw new CoreV3ClientException("projection_payload_invalid", "Read-to-Anki settings evidence is invalid.")
            };
        }
        if (result.Data.TryGetProperty("contextEvidence", out var contextEvidence) && contextEvidence.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with
            {
                ContextEvidence = contextEvidence.Deserialize<ReadToAnkiContextEvidence>(JsonOptions)
                    ?? throw new CoreV3ClientException("projection_payload_invalid", "Read-to-Anki context evidence is invalid.")
            };
        }
        return snapshot;
    }

    /// <summary>
    /// Waits for the terminal created by this exact accepted command. A newer
    /// stream cursor is not sufficient because an accepted/started action can
    /// coexist with the preceding command's terminal projection.
    /// </summary>
    public async Task<ReadAloudCardsProjectionSnapshot> WaitReadAloudCardsActionAsync(
        OperationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        if (submission is null || !submission.Accepted || string.IsNullOrWhiteSpace(submission.CommandId))
            throw new ArgumentException("An accepted read-to-Anki command submission is required.", nameof(submission));

        var started = DateTimeOffset.UtcNow;
        ReadAloudCardsProjectionSnapshot? last = null;
        while (DateTimeOffset.UtcNow - started < _options.ObservationTimeout)
        {
            try
            {
                last = await GetReadAloudCardsProjectionAsync(submission.OperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (last.ActionState != ReadAloudCardsActionState.Running &&
                string.Equals(last.TerminalCommandId, submission.CommandId, StringComparison.Ordinal) &&
                submission.AcceptedSequence > 0 && last.TerminalSequence >= submission.AcceptedSequence)
                return last;

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new ReadAloudCardsObservationException(
            "read_to_anki_action_observation_timeout",
            $"The target read-to-Anki command '{submission.CommandId}' did not publish its own terminal before the observation timeout. " +
            $"Last actionState='{last?.ActionState}', activeCommandId='{last?.ActiveCommandId}', terminalCommandId='{last?.TerminalCommandId}', " +
            $"terminalSequence={last?.TerminalSequence}, acceptedSequence={submission.AcceptedSequence}, " +
            $"submissionCursor={submission.Cursor}, streamCursor={last?.Cursor}.",
            last);
    }

    public Task<ReadAloudCardsProjectionSnapshot> WaitReadAloudCardsActionAsync(
        AcceptedOperationIdentity operation,
        CancellationToken cancellationToken = default)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        return WaitReadAloudCardsActionAsync(
            new OperationSubmission(operation.OperationId, operation.AcceptedSequence, Accepted: true)
            {
                CommandId = operation.CommandId,
                AcceptedSequence = operation.AcceptedSequence
            },
            cancellationToken);
    }

    public Task<QuestionOperationSubmission> QuestionInitialAsync(QuestionInitialRequest request, CancellationToken cancellationToken = default)
    {
        return SubmitQuestionOperationAsync(
            request.RequestId,
            "question.initial",
            "kc.question-initial-command/2",
            request.RequestId,
            new { sessionId = request.RequestId, question = request.Question.Reference, context = request.Context.Select(item => item.Reference).ToArray(), configuration = request.AnkiConfiguration?.Reference },
            new[] { request.Question.Reference }.Concat(request.Context.Select(item => item.Reference)).Concat(request.AnkiConfiguration is null ? Array.Empty<ArtifactReference>() : new[] { request.AnkiConfiguration.Reference }).ToArray(),
            request.Authorization is null ? null : new AuthorizationReference(request.Authorization.AuthorizationId, request.Authorization.Scope),
            cancellationToken);
    }

    public Task<QuestionOperationSubmission> QuestionFollowUpAsync(QuestionFollowUpRequest request, CancellationToken cancellationToken = default)
    {
        return SubmitQuestionOperationAsync(
            request.RequestId,
            "question.follow-up",
            "kc.question-follow-up-command/1",
            request.SessionId,
            new { request.SessionId, followUp = request.FollowUp.Reference, configuration = request.AnkiConfiguration?.Reference },
            new[] { request.FollowUp.Reference }.Concat(request.AnkiConfiguration is null ? Array.Empty<ArtifactReference>() : new[] { request.AnkiConfiguration.Reference }).ToArray(),
            request.Authorization is null ? null : new AuthorizationReference(request.Authorization.AuthorizationId, request.Authorization.Scope),
            cancellationToken);
    }

    public Task<QuestionOperationSubmission> QuestionMakeCardsAsync(QuestionMakeCardsRequest request, CancellationToken cancellationToken = default)
    {
        var inputs = new[] { request.Answer.Reference }
            .Concat(request.AnkiConfiguration is null ? Array.Empty<ArtifactReference>() : new[] { request.AnkiConfiguration.Reference })
            .ToArray();
        return SubmitQuestionOperationAsync(
            request.RequestId,
            "question.make-cards",
            "kc.question-make-cards-command/1",
            request.SessionId,
            new { request.SessionId, answer = request.Answer.Reference, configuration = request.AnkiConfiguration?.Reference },
            inputs,
            request.Authorization is null ? null : new AuthorizationReference(request.Authorization.AuthorizationId, request.Authorization.Scope),
            cancellationToken);
    }

    public async Task<TaskProjectionSnapshot> GetTaskProjectionAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync("task.projection", new { taskId }, cancellationToken).ConfigureAwait(false);
        var stateText = result.Data.GetProperty("state").GetString()!;
        if (!Enum.TryParse<TaskProjectionState>(stateText, ignoreCase: true, out var state))
        {
            state = TaskProjectionState.Degraded;
        }

        return new TaskProjectionSnapshot(
            taskId,
            state,
            result.Cursor,
            GetOptionalString(result.Data, "notice"));
    }

    public async Task<OperationProjectionSnapshot> GetOperationProjectionAsync(string operationId, CancellationToken cancellationToken = default)
        => await GetOperationProjectionCoreAsync(operationId, afterCursor: null, cancellationToken).ConfigureAwait(false);

    public async Task<OperationProjectionSnapshot> WaitOperationProjectionAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        long? cursor = null;
        while (DateTimeOffset.UtcNow - started < _options.ObservationTimeout)
        {
            OperationProjectionSnapshot snapshot;
            try
            {
                snapshot = await GetOperationProjectionCoreAsync(operationId, cursor, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (snapshot.State is OperationProjectionState.Succeeded or OperationProjectionState.Failed or OperationProjectionState.Cancelled or OperationProjectionState.Degraded)
            {
                return snapshot;
            }
            cursor = Math.Max(cursor ?? 0, snapshot.Cursor);
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new CoreV3ClientException("observation_timeout", $"Operation '{operationId}' did not reach a terminal state within {_options.ObservationTimeout}.");
    }

    private async Task<OperationProjectionSnapshot> GetOperationProjectionCoreAsync(string operationId, long? afterCursor, CancellationToken cancellationToken)
    {
        var result = await QueryAsync("operation.projection", new { operationId }, cancellationToken, afterCursor).ConfigureAwait(false);
        var stateText = result.Data.GetProperty("state").GetString()!;
        if (!Enum.TryParse<OperationProjectionState>(stateText, ignoreCase: true, out var state)) state = OperationProjectionState.Degraded;
        var outputs = result.Data.GetProperty("outputs");
        var snapshot = new OperationProjectionSnapshot(operationId, state, result.Cursor, GetOptionalString(result.Data, "notice"), ReadOptionalArtifactHandle(outputs, "ankiConnectionStatusArtifact"));
        if (result.Data.TryGetProperty("result", out var operationResult) && operationResult.ValueKind == JsonValueKind.Object &&
            operationResult.TryGetProperty("readiness", out var readiness) && readiness.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with
            {
                AnkiReadiness = readiness.Deserialize<AnkiReadinessSummary>(JsonOptions)
                    ?? throw new CoreV3ClientException("projection_payload_invalid", "Anki readiness projection is invalid.")
            };
        }
        return result.Data.TryGetProperty("result", out var typedResult) && typedResult.ValueKind == JsonValueKind.Object
            ? WithDestinations(snapshot, typedResult, outputs) : snapshot;
    }

    public async Task<QuestionProjectionSnapshot> GetQuestionProjectionAsync(string operationId, CancellationToken cancellationToken = default)
        => await GetQuestionProjectionCoreAsync(operationId, afterCursor: null, cancellationToken).ConfigureAwait(false);

    private async Task<QuestionProjectionSnapshot> GetQuestionProjectionCoreAsync(string operationId, long? afterCursor, CancellationToken cancellationToken)
    {
        var result = await QueryAsync("question.projection", new { operationId }, cancellationToken, afterCursor).ConfigureAwait(false);
        var stateText = result.Data.GetProperty("state").GetString()!;
        if (!Enum.TryParse<QuestionProjectionState>(stateText, ignoreCase: true, out var state))
        {
            state = QuestionProjectionState.Degraded;
        }

        var outputs = result.Data.GetProperty("outputs");
        var snapshot = new QuestionProjectionSnapshot(
            operationId,
            state,
            result.Cursor,
            GetOptionalString(result.Data, "notice"),
            ReadOptionalArtifactHandle(outputs, "answerArtifact"),
            ReadOptionalArtifactHandle(outputs, "cardBundleArtifact"),
            ReadOptionalArtifactHandle(outputs, "destinationEvidenceArtifact"))
        { TerminalCommandId = GetOptionalString(result.Data, "terminalCommandId"),
            DestinationSnapshot = ReadOptionalArtifactHandle(outputs, "destinationSnapshotArtifact"), Destinations = ReadDestinations(result.Data) };
        if (result.Data.TryGetProperty("cardDraft", out var draft) && draft.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with { CardDraft = ParseFlashcardDraft(draft) };
        }
        if (result.Data.TryGetProperty("semanticReadback", out var readback) && readback.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with { AnkiSemanticReadback = ParseAnkiSemanticReadback(readback) };
        }
        return snapshot;
    }

    public async Task<QuestionProjectionSnapshot> WaitQuestionProjectionAsync(
        string operationId,
        CancellationToken cancellationToken = default)
        => await WaitQuestionProjectionCoreAsync(operationId, terminalCommandId: null, cancellationToken).ConfigureAwait(false);

    public async Task<QuestionProjectionSnapshot> WaitFlashcardDraftActionAsync(
        OperationSubmission submission,
        CancellationToken cancellationToken = default)
        => await WaitQuestionProjectionCoreAsync(submission.OperationId, submission.CommandId, cancellationToken).ConfigureAwait(false);

    private async Task<QuestionProjectionSnapshot> WaitQuestionProjectionCoreAsync(
        string operationId,
        string? terminalCommandId,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        QuestionProjectionSnapshot? last = null;
        long? lastCursor = null;
        while (DateTimeOffset.UtcNow - started < _options.ObservationTimeout)
        {
            try
            {
                last = await GetQuestionProjectionCoreAsync(operationId, lastCursor, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            lastCursor = Math.Max(lastCursor ?? 0, last.Cursor);
            var terminal = last.State is QuestionProjectionState.WaitingInput or QuestionProjectionState.Succeeded or QuestionProjectionState.Failed or QuestionProjectionState.Cancelled or QuestionProjectionState.Degraded;
            if (terminal && (terminalCommandId is null || last.TerminalCommandId == terminalCommandId))
            {
                break;
            }
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (last is null)
        {
            throw new QuestionObservationException("observation_empty", "Question observation returned no projection.", null);
        }

        if (last.State == QuestionProjectionState.Degraded)
        {
            throw new QuestionObservationException("observation_degraded", last.Notice ?? "Question observation degraded.", last);
        }

        return last;
    }

    public async IAsyncEnumerable<QuestionProjectionSnapshot> ObserveQuestionProjectionAsync(
        string operationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        long? lastCursor = null;
        while (true)
        {
            if (DateTimeOffset.UtcNow - started >= _options.ObservationTimeout)
            {
                yield return Degraded(operationId, lastCursor ?? 0, "Question observation timed out before a terminal state.");
                yield break;
            }

            QuestionProjectionSnapshot snapshot;
            try
            {
                snapshot = await GetQuestionProjectionCoreAsync(operationId, lastCursor, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            yield return snapshot;
            if (snapshot.State is QuestionProjectionState.WaitingInput or QuestionProjectionState.Succeeded or QuestionProjectionState.Failed or QuestionProjectionState.Cancelled or QuestionProjectionState.Degraded)
            {
                yield break;
            }

            lastCursor = Math.Max(lastCursor ?? 0, snapshot.Cursor);

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TypedArtifactContent> ReadArtifactAsync(
        TypedArtifactHandle artifact,
        string? representationId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync("artifact.read", new { reference = artifact.Reference, representationId }, cancellationToken).ConfigureAwait(false);
        return new TypedArtifactContent(
            artifact,
            result.Data.GetProperty("representationId").GetString()!,
            result.Data.GetProperty("mediaType").GetString()!,
            GetOptionalString(result.Data, "schema"),
            Convert.FromBase64String(result.Data.GetProperty("contentBase64").GetString()!));
    }

    internal Task<QueryResult> QueryProtocolAsync(QueryEnvelope query, CancellationToken cancellationToken)
        => _protocol.QueryAsync(query, cancellationToken);

    internal Task<string> SendCompatibilityFrameAsync(string requestJson, CancellationToken cancellationToken)
        => _protocol.SendCompatibilityFrameAsync(requestJson, cancellationToken);

    private async Task<OperationSubmission> SubmitDomainOperationAsync(
        string requestId,
        string operation,
        string bodySchema,
        object body,
        IReadOnlyList<ArtifactReference> inputs,
        CancellationToken cancellationToken,
        AuthorizationReference? authorization = null,
        string? operationIdOverride = null)
    {
        var receipt = await SubmitAsync(requestId, operation, bodySchema, body, inputs, cancellationToken, authorization, operationIdOverride).ConfigureAwait(false);
        var commandId = "command-" + requestId;
        var acceptedSequence = receipt.Events.LastOrDefault(item =>
            item.EventType == "command.accepted" && string.Equals(item.CorrelationId, commandId, StringComparison.Ordinal))?.Sequence ?? 0;
        return new OperationSubmission(receipt.OperationId, receipt.Cursor, receipt.Accepted)
        {
            CommandId = commandId,
            AcceptedSequence = acceptedSequence
        };
    }

    private static AuthorizationReference MapAnkiAuthorization(AnkiWriteAuthorization? authorization)
    {
        if (authorization is null)
            throw new ArgumentNullException(nameof(authorization), "Anki write authorization is required by this typed operation.");
        if (!string.Equals(authorization.Scope, AnkiWriteAuthorizationBuilder.IsolatedCanaryDeckScope, StringComparison.Ordinal))
            throw new ArgumentException("Anki write authorization has an unsupported scope.", nameof(authorization));
        _ = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck(authorization.AuthorizationId);
        return new AuthorizationReference(authorization.AuthorizationId, authorization.Scope);
    }

    private async Task<QuestionOperationSubmission> SubmitQuestionOperationAsync(
        string requestId,
        string operation,
        string bodySchema,
        string sessionId,
        object body,
        IReadOnlyList<ArtifactReference> inputs,
        AuthorizationReference? authorization,
        CancellationToken cancellationToken)
    {
        var receipt = await SubmitAsync(requestId, operation, bodySchema, body, inputs, cancellationToken, authorization).ConfigureAwait(false);
        return new QuestionOperationSubmission(receipt.OperationId, sessionId, receipt.Cursor, receipt.Accepted);
    }

    private Task<CommandReceipt> SubmitAsync(
        string requestId,
        string operation,
        string bodySchema,
        object body,
        IReadOnlyList<ArtifactReference> inputs,
        CancellationToken cancellationToken,
        AuthorizationReference? authorization = null,
        string? operationIdOverride = null)
    {
        RequireRequestId(requestId);
        var operationId = operationIdOverride ?? operation.Replace('.', '-') + "-" + requestId;
        var idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + requestId)));
        var command = new CommandEnvelope(
            KernelSchemas.Command,
            "command-" + requestId,
            operationId,
            operation,
            new KernelSubject("desktop-request", requestId),
            idempotencyKey,
            ExpectedRevision: null,
            Authorization: authorization,
            inputs,
            new ContractBody(bodySchema, JsonSerializer.SerializeToElement(body, JsonOptions)));
        return _protocol.CommandAsync(command, cancellationToken);
    }

    private Task<QueryResult> QueryAsync(string projection, object parameters, CancellationToken cancellationToken, long? cursor = null)
    {
        var query = new QueryEnvelope(
            KernelSchemas.Query,
            "query-" + Guid.NewGuid().ToString("N"),
            projection,
            Subject: null,
            "read-your-writes",
            Cursor: cursor,
            JsonSerializer.SerializeToElement(parameters, JsonOptions));
        return _protocol.QueryAsync(query, cancellationToken);
    }

    private static JsonElement GetCompletedResult(CommandReceipt receipt)
    {
        var completed = receipt.Events.LastOrDefault(item => item.EventType == "operation.completed");
        if (completed is null)
        {
            throw new CoreV3OperationException(
                receipt.Error?.Code ?? "operation_not_completed",
                receipt.Error?.Message ?? "Operation did not reach completed state.",
                receipt.OperationId,
                receipt.Events.LastOrDefault()?.EventType ?? "not-terminal");
        }

        return completed.Data.GetProperty("result");
    }

    private static QuestionProjectionSnapshot Degraded(string operationId, long cursor, string notice)
        => new(operationId, QuestionProjectionState.Degraded, cursor, notice, null, null, null);

    private static TypedArtifactHandle? ReadOptionalArtifactHandle(JsonElement outputs, string name)
    {
        if (!outputs.TryGetProperty(name, out var artifact) || artifact.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return new TypedArtifactHandle(
            artifact.Deserialize<ArtifactReference>(JsonOptions)
            ?? throw new CoreV3ClientException("projection_payload_invalid", $"Projection output '{name}' is invalid."));
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static FlashcardDraft ParseFlashcardDraft(JsonElement draft)
    {
        static string Required(JsonElement value, string name)
            => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()! : throw new CoreV3ClientException("flashcard_draft_invalid", $"Card draft '{name}' is invalid.");
        if (!draft.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array || cards.GetArrayLength() == 0)
        {
            throw new CoreV3ClientException("flashcard_draft_invalid", "Card draft contains no cards.");
        }
        return new FlashcardDraft(Required(draft, "sourceOperation"), Required(draft, "token"), Required(draft, "revision"), Required(draft, "digest"),
            cards.EnumerateArray().Select(card => new FlashcardDraftCard(
                Required(card, "cardId"), Required(card, "revision"), Required(card, "cardSha256"), Required(card, "front"), Required(card, "back"),
                card.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.String ? template.GetString()! : "basic_qa")).ToArray());
    }

    private static string RequireFlashcardSourceOperation(string operation)
        => operation == "clip.read-aloud-cards"
            ? operation
            : operation is "question.initial" or "question.follow-up" or "question.make-cards"
            ? "question.make-cards"
            : throw new ArgumentException("Flashcard draft source operation is unsupported.", nameof(operation));

    private static string FlashcardCommandSchema(string operation)
        => operation.StartsWith("question.", StringComparison.Ordinal) ? "kc.question-make-cards-command/1" : "kc.read-aloud-cards-command/1";

    private static AnkiSemanticReadbackSummary ParseAnkiSemanticReadback(JsonElement readback)
    {
        static string Required(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var property))
            {
                var camel = char.ToLowerInvariant(name[0]) + name[1..];
                value.TryGetProperty(camel, out property);
            }
            return property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()! : throw new CoreV3ClientException("anki_semantic_readback_invalid", $"Anki semantic readback '{name}' is invalid.");
        }
        if (!readback.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array || cards.GetArrayLength() == 0)
        {
            throw new CoreV3ClientException("anki_semantic_readback_invalid", "Anki semantic readback contains no cards.");
        }
        var projected = cards.EnumerateArray().Select(card =>
        {
            var notes = card.GetProperty("notesInfo");
            var cardInfo = card.GetProperty("cardsInfo");
            if (cardInfo.ValueKind != JsonValueKind.Array || cardInfo.GetArrayLength() == 0)
            {
                throw new CoreV3ClientException("anki_semantic_readback_invalid", "Anki semantic readback has no card info.");
            }
            var fields = notes.GetProperty("fields");
            return new AnkiSemanticReadbackCard(notes.GetProperty("noteId").GetInt64(),
                notes.GetProperty("cardIds").EnumerateArray().Select(item => item.GetInt64()).ToArray(),
                Required(fields, "Front"), Required(fields, "Back"), Required(cardInfo[0], "deckName"));
        }).ToArray();
        return new AnkiSemanticReadbackSummary(Required(readback, "artifactPath"), Required(readback, "sha256"), Required(readback, "cardsSha256"), projected);
    }

    private static ReadAloudCardsPlaybackState ParseReadAloudPlayback(string? value) => value switch
    {
        "not_requested" => ReadAloudCardsPlaybackState.NotRequested,
        "accepted" => ReadAloudCardsPlaybackState.Accepted,
        "playing" => ReadAloudCardsPlaybackState.Playing,
        "idle_replayable" => ReadAloudCardsPlaybackState.IdleReplayable,
        "failed" => ReadAloudCardsPlaybackState.Failed,
        "stopped" => ReadAloudCardsPlaybackState.Stopped,
        _ => ReadAloudCardsPlaybackState.Unknown
    };

    private static ReadAloudCardsPipelineState ParseReadAloudPipeline(string? value) => value switch
    {
        "accepted" => ReadAloudCardsPipelineState.Accepted,
        "reviewing" => ReadAloudCardsPipelineState.Reviewing,
        "awaiting_card_confirmation" => ReadAloudCardsPipelineState.AwaitingCardConfirmation,
        "waiting_language_card_choice" => ReadAloudCardsPipelineState.WaitingLanguageCardChoice,
        "awaiting_card_resolution" => ReadAloudCardsPipelineState.AwaitingCardResolution,
        "writing_anki" => ReadAloudCardsPipelineState.WritingAnki,
        "completed" => ReadAloudCardsPipelineState.Completed,
        "cancelled" => ReadAloudCardsPipelineState.Cancelled,
        "partially_failed" => ReadAloudCardsPipelineState.PartiallyFailed,
        "failed" => ReadAloudCardsPipelineState.Failed,
        "unknown_external_state" => ReadAloudCardsPipelineState.UnknownExternalState,
        _ => ReadAloudCardsPipelineState.Failed
    };

    private static ReadAloudCardsActionState ParseReadAloudActionState(string? value) => value switch
    {
        "running" => ReadAloudCardsActionState.Running,
        "completed" => ReadAloudCardsActionState.Completed,
        "failed" => ReadAloudCardsActionState.Failed,
        "cancelled" => ReadAloudCardsActionState.Cancelled,
        "unknown_external_state" => ReadAloudCardsActionState.UnknownExternalState,
        _ => ReadAloudCardsActionState.NotFound
    };

    private static void RequireCorrelationMarker(string correlationMarker)
    {
        if (string.IsNullOrWhiteSpace(correlationMarker) || correlationMarker.Length > 128 ||
            !correlationMarker.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            throw new ArgumentException("correlationMarker must be a stable 1-128 character kernel identifier.", nameof(correlationMarker));
    }

    private static void RequireRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 80 || !requestId.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            throw new ArgumentException("requestId must be a stable 1-80 character domain request identifier.", nameof(requestId));
        }
    }
}

public sealed class CoreV3OperationException : Exception
{
    public CoreV3OperationException(string code, string message, string operationId, string terminalState)
        : base(message)
    {
        Code = code;
        OperationId = operationId;
        TerminalState = terminalState;
    }

    public string Code { get; }

    public string OperationId { get; }

    public string TerminalState { get; }
}

public sealed class QuestionObservationException : Exception
{
    public QuestionObservationException(string code, string message, QuestionProjectionSnapshot? lastProjection)
        : base(message)
    {
        Code = code;
        LastProjection = lastProjection;
    }

    public string Code { get; }

    public QuestionProjectionSnapshot? LastProjection { get; }
}

public sealed class ReadAloudCardsObservationException : Exception
{
    public ReadAloudCardsObservationException(string code, string message, ReadAloudCardsProjectionSnapshot? lastProjection)
        : base(message)
    {
        Code = code;
        LastProjection = lastProjection;
    }

    public string Code { get; }

    public ReadAloudCardsProjectionSnapshot? LastProjection { get; }
}

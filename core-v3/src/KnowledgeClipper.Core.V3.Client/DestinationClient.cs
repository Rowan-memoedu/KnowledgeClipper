using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;

namespace KnowledgeClipper.Core.V3.Client;

public sealed record RemNoteDiscoveryRequest(string RequestId, string RootTitle);
public sealed record RemNoteProbeRequest(string RequestId, VerifiedRemNoteBinding Target);
public sealed record RemNoteBindingReference(string DiscoveryOperationId, string EvidenceToken);
public sealed record DestinationSnapshotReference(string OperationId, string SnapshotToken);
public sealed record FrozenDestinationSummary(string DestinationId, string Kind, string TargetIdentity, string? RouteKey,
    TypedArtifactHandle Configuration, VerifiedRemNoteBinding? RemNoteBinding);
public sealed record FrozenDestinationSnapshot(string OperationId, string SourceOperationId, TypedArtifactHandle Snapshot,
    IReadOnlyList<FrozenDestinationSummary> Destinations);
public sealed class VerifiedRemNoteBinding
{
    internal VerifiedRemNoteBinding(Contracts.RemNoteBindingReference value) { Reference = value; Evidence = new(value.Evidence); }
    internal Contracts.RemNoteBindingReference Reference { get; }
    public TypedArtifactHandle Evidence { get; }
    public string DiscoveryOperationId => Reference.DiscoveryOperationId;
    public string RootRemId => Reference.RootRemId;
    public string RootTitle => Reference.RootTitle;
}
public sealed record BindingRestoreResult(string Status, VerifiedRemNoteBinding? Binding, string? ReasonCode);
public sealed record RemNoteDiscoverySummary(string State, IReadOnlyList<string> ReasonCodes, int CandidateCount,
    bool PersistableBinding, VerifiedRemNoteBinding? Binding, TypedArtifactHandle Evidence);
public sealed record RemNoteReadinessSummary(bool Connected, bool WritePermission, bool ExactTargetMatch,
    bool ReadyForRequestedAction, IReadOnlyList<string> ReasonCodes, TypedArtifactHandle Evidence);
public abstract record CardDestinationSelection;
public sealed record AnkiSelection(TypedArtifactHandle Configuration, string RouteKey = "canary") : CardDestinationSelection;
public sealed record RemNoteSelection(TypedArtifactHandle Settings, VerifiedRemNoteBinding Binding) : CardDestinationSelection;
public sealed record DestinationProjection(string DestinationId, string Kind, string State, string? ReasonCode,
    TypedArtifactHandle SourceCardArtifact, TypedArtifactHandle ConfigurationSnapshot, TypedArtifactHandle? Evidence,
    bool ExternalStateKnown, int? ExternalWriteCount);
public sealed record ReadCardsRequest(string RequestId, TypedArtifactHandle Capture, IReadOnlyList<CardDestinationSelection> Destinations,
    LanguageCardSettings? LanguageCards = null, bool IncludeSpeech = true, LanguageSelectionContext? Selection = null);
public sealed record DestinationQuestionInitialRequest(string RequestId, TypedArtifactHandle Question,
    IReadOnlyList<TypedArtifactHandle> Context, IReadOnlyList<CardDestinationSelection> Destinations);
public sealed record DestinationQuestionFollowUpRequest(string RequestId, string SessionId, TypedArtifactHandle FollowUp);
public sealed record DestinationQuestionMakeCardsRequest(string RequestId, string SessionId, TypedArtifactHandle Answer);
public abstract record CardDestinationAuthorization;
public sealed record AnkiCardAuthorization(AnkiWriteAuthorization Authorization, string DeckIdentity) : CardDestinationAuthorization;
public sealed record RemNoteCardAuthorization(string AuthorizationId, string RootRemId) : CardDestinationAuthorization;
public sealed record ConfirmedCardsRequest(string RequestId, string OperationId, FlashcardDraft Draft,
    TypedArtifactHandle DestinationSnapshot, IReadOnlyList<CardDestinationAuthorization> Authorizations);
public sealed record DestinationLanguageChoiceRequest(string RequestId, string OperationId, string TargetToken, string Choice);
public sealed record DestinationResolutionRequest(string RequestId, string OperationId, string TargetToken, string DecisionId,
    string Action, string? SelectedText = null, CandidateOccurrence? CandidateOccurrence = null);
public sealed record DestinationReplayRequest(string RequestId, string OperationId, long SourceRevision);

public sealed partial class CoreV3Client
{
    public Task<OperationSubmission> DiscoverRemNoteTargetAsync(RemNoteDiscoveryRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, "remnote.discover-target", "kc.remnote-discovery/1",
            new { rootTitle = request.RootTitle }, Array.Empty<ArtifactReference>(), cancellationToken);
    public Task<OperationSubmission> ProbeRemNoteAsync(RemNoteProbeRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, "remnote.probe", "kc.remnote-probe/1",
            new { binding = request.Target.Reference }, new[] { request.Target.Reference.Evidence }, cancellationToken);
    public static RemNoteBindingReference ExportRemNoteBinding(VerifiedRemNoteBinding binding)
        => new(binding.DiscoveryOperationId, binding.Evidence.Token);
    public async Task<BindingRestoreResult> RestoreRemNoteBindingAsync(RemNoteBindingReference reference, CancellationToken cancellationToken = default)
    {
        try
        {
            var projection = await GetOperationProjectionAsync(reference.DiscoveryOperationId, cancellationToken).ConfigureAwait(false);
            var binding = projection.RemNoteDiscovery?.Binding;
            if (projection.State != OperationProjectionState.Succeeded || binding is null)
                return new("invalid", null, "binding_evidence_unavailable");
            if (!string.Equals(binding.Evidence.Token, reference.EvidenceToken, StringComparison.Ordinal))
                return new("invalid", null, "binding_evidence_mismatch");
            _ = await ReadArtifactAsync(binding.Evidence, null, cancellationToken).ConfigureAwait(false);
            return new("verified_historical", binding, null);
        }
        catch (CoreV3ClientException ex) { return new("unavailable", null, ex.Code); }
    }
    public async Task<TypedArtifactHandle> RestoreDestinationSnapshotAsync(DestinationSnapshotReference reference, CancellationToken cancellationToken = default)
    {
        var snapshot = (await GetOperationProjectionAsync(reference.OperationId, cancellationToken).ConfigureAwait(false)).DestinationSnapshot;
        if (snapshot is null || snapshot.Token != reference.SnapshotToken)
            throw new CoreV3ClientException("destination_snapshot_mismatch", "Destination snapshot is missing or mismatched.");
        _ = await ReadArtifactAsync(snapshot, null, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }
    public Task<OperationSubmission> ReadCardsAsync(ReadCardsRequest request, CancellationToken cancellationToken = default)
    {
        var destinations = MapDestinations(request.Destinations);
        var context = request.Selection?.OrderedContextSegments ?? Array.Empty<TypedArtifactHandle>();
        return SubmitDomainOperationAsync(request.RequestId, "clip.read-aloud-cards", DestinationSchemas.Command,
            new { destinationMode = "confirmed-cards", capture = request.Capture.Reference, destinations,
                languageCards = request.LanguageCards, includeSpeech = request.IncludeSpeech, selection = MapNeutralSelection(request.Selection) },
            new[] { request.Capture.Reference }.Concat(DestinationInputs(destinations)).Concat(context.Select(x => x.Reference)).ToArray(), cancellationToken);
    }
    public async Task<FrozenDestinationSnapshot> GetFrozenDestinationsAsync(DestinationSnapshotReference reference, CancellationToken cancellationToken = default)
    {
        var handle = await RestoreDestinationSnapshotAsync(reference, cancellationToken).ConfigureAwait(false);
        var content = await ReadArtifactAsync(handle, null, cancellationToken).ConfigureAwait(false);
        if (content.ContractSchema != "kc.destination-snapshot/1") throw new CoreV3ClientException("destination_snapshot_invalid", "Snapshot schema differs from the frozen contract.");
        var frozen = JsonSerializer.Deserialize<Contracts.DestinationSnapshot>(content.Content, JsonOptions)
            ?? throw new CoreV3ClientException("destination_snapshot_invalid", "Snapshot payload is invalid.");
        var values = new List<FrozenDestinationSummary>();
        foreach (var item in frozen.Destinations)
        {
            var configuration = new TypedArtifactHandle(item.Configuration);
            _ = await ReadArtifactAsync(configuration, null, cancellationToken).ConfigureAwait(false);
            VerifiedRemNoteBinding? binding = null;
            if (item.Binding is not null)
            {
                var restored = await RestoreRemNoteBindingAsync(new(item.Binding.DiscoveryOperationId, new TypedArtifactHandle(item.Binding.Evidence).Token), cancellationToken).ConfigureAwait(false);
                binding = restored.Binding;
                if (binding is null || binding.RootRemId != item.TargetIdentity || binding.RootTitle != item.Binding.RootTitle)
                    throw new CoreV3ClientException("destination_binding_invalid", "Frozen RemNote binding is no longer verifiable.");
            }
            values.Add(new(item.DestinationId, item.Kind, item.TargetIdentity, item.RouteKey, configuration, binding));
        }
        return new(reference.OperationId, frozen.SourceOperationId, handle, values);
    }
    public Task<QuestionOperationSubmission> StartDestinationQuestionAsync(DestinationQuestionInitialRequest request, CancellationToken cancellationToken = default)
    {
        var destinations = MapDestinations(request.Destinations);
        var session = "session-" + request.RequestId;
        return SubmitQuestionOperationAsync(request.RequestId, "question.initial", DestinationSchemas.Command, session,
            new { destinationMode = "confirmed-cards", sessionId = session, question = request.Question.Reference, context = request.Context.Select(x => x.Reference).ToArray(), destinations },
            new[] { request.Question.Reference }.Concat(request.Context.Select(x => x.Reference)).Concat(DestinationInputs(destinations)).ToArray(), null, cancellationToken);
    }
    public Task<QuestionOperationSubmission> FollowUpDestinationQuestionAsync(DestinationQuestionFollowUpRequest request, CancellationToken cancellationToken = default)
        => SubmitQuestionOperationAsync(request.RequestId, "question.follow-up", DestinationSchemas.Command, request.SessionId,
            new { destinationMode = "confirmed-cards", sessionId = request.SessionId, followUp = request.FollowUp.Reference }, new[] { request.FollowUp.Reference }, null, cancellationToken);
    public Task<QuestionOperationSubmission> MakeDestinationQuestionCardsAsync(DestinationQuestionMakeCardsRequest request, CancellationToken cancellationToken = default)
        => SubmitQuestionOperationAsync(request.RequestId, "question.make-cards", DestinationSchemas.Command, request.SessionId,
            new { destinationMode = "confirmed-cards", sessionId = request.SessionId, answer = request.Answer.Reference }, new[] { request.Answer.Reference }, null, cancellationToken);
    public Task<OperationSubmission> ConfirmCardsAsync(ConfirmedCardsRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, request.Draft.SourceOperation,
            DestinationSchemas.Command,
            new { destinationMode = "confirmed-cards", action = "confirm_card_draft", sourceOperationId = request.OperationId,
                draft = new { token = request.Draft.Token, revision = request.Draft.Revision, digest = request.Draft.DraftSha256 },
                destinationSnapshot = request.DestinationSnapshot.Reference, grants = request.Authorizations.Select(MapGrant).ToArray() },
            Array.Empty<ArtifactReference>(), cancellationToken, operationIdOverride: request.OperationId);
    public Task<OperationSubmission> ChooseDestinationLanguageCardsAsync(DestinationLanguageChoiceRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, "clip.read-aloud-cards", DestinationSchemas.Command,
            new { destinationMode = "confirmed-cards", action = "choose_language_card", sourceOperationId = request.OperationId, targetToken = request.TargetToken, choice = request.Choice },
            Array.Empty<ArtifactReference>(), cancellationToken, operationIdOverride: request.OperationId);
    public Task<OperationSubmission> CancelDestinationCardDraftAsync(FlashcardDraftCancellationRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, RequireFlashcardSourceOperation(request.Draft.SourceOperation), DestinationSchemas.Command,
            new { destinationMode = "confirmed-cards", action = "cancel_card_draft", sourceOperationId = request.OperationId,
                draft = new { token = request.Draft.Token, revision = request.Draft.Revision, digest = request.Draft.DraftSha256 } },
            Array.Empty<ArtifactReference>(), cancellationToken, operationIdOverride: request.OperationId);
    public Task<OperationSubmission> ResolveDestinationCardsAsync(DestinationResolutionRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, "clip.read-aloud-cards", DestinationSchemas.Command,
            new { destinationMode = "confirmed-cards", action = "resolve_card_resolution", sourceOperationId = request.OperationId, targetToken = request.TargetToken,
                decisionId = request.DecisionId, resolutionAction = request.Action, selectedText = request.SelectedText, candidateOccurrence = request.CandidateOccurrence },
            Array.Empty<ArtifactReference>(), cancellationToken, operationIdOverride: request.OperationId);
    public Task<OperationSubmission> ReplayDestinationReadAsync(DestinationReplayRequest request, CancellationToken cancellationToken = default)
        => SubmitDomainOperationAsync(request.RequestId, "clip.read-aloud-cards", DestinationSchemas.Command,
            new { destinationMode = "confirmed-cards", action = "replay", sourceOperationId = request.OperationId, sourceRevision = request.SourceRevision },
            Array.Empty<ArtifactReference>(), cancellationToken, operationIdOverride: request.OperationId);
    private static Contracts.DestinationWriteGrant MapGrant(CardDestinationAuthorization grant) => grant switch
    {
        AnkiCardAuthorization anki => new("anki", anki.Authorization.AuthorizationId, MapAnkiAuthorization(anki.Authorization).Policy, anki.DeckIdentity),
        RemNoteCardAuthorization remnote when !string.IsNullOrWhiteSpace(remnote.AuthorizationId) && !string.IsNullOrWhiteSpace(remnote.RootRemId)
            => new("remnote", remnote.AuthorizationId, "confirmed-remnote-root", remnote.RootRemId),
        _ => throw new ArgumentException("Unsupported destination grant.")
    };
    private static Contracts.DestinationSelection[] MapDestinations(IReadOnlyList<CardDestinationSelection> selections)
    {
        var result = selections.Select(item => item switch
        {
            AnkiSelection anki => new Contracts.DestinationSelection("anki", anki.Configuration.Reference, anki.RouteKey, null),
            RemNoteSelection remnote => new Contracts.DestinationSelection("remnote", remnote.Settings.Reference, null, remnote.Binding.Reference),
            _ => throw new ArgumentException("Unsupported destination selection.")
        }).ToArray();
        if (result.Select(x => x.Kind).Distinct().Count() != result.Length || result.Length > 2) throw new ArgumentException("Destination kinds must be unique.");
        return result;
    }
    private static IEnumerable<ArtifactReference> DestinationInputs(IEnumerable<Contracts.DestinationSelection> selections)
        => selections.SelectMany(x => x.Binding is null ? new[] { x.Configuration } : new[] { x.Configuration, x.Binding.Evidence });
    private static object? MapNeutralSelection(LanguageSelectionContext? selection) => selection is null ? null : new
    {
        targetText = selection.TargetText, contextRequested = selection.ContextRequested, contextGranularity = selection.ContextGranularity, sourceWindow = selection.SourceWindow,
        contextSegments = (selection.RoleBoundContextSegments ?? Array.Empty<LanguageContextSegment>()).Select(x => new { reference = x.Artifact.Reference, role = x.Role, order = x.Order }).ToArray()
    };
    private static OperationProjectionSnapshot WithDestinations(OperationProjectionSnapshot snapshot, JsonElement result, JsonElement outputs)
    {
        var evidence = ReadOptionalArtifactHandle(outputs, "remNoteDiscoveryArtifact");
        if (evidence is not null && result.TryGetProperty("discovery", out var discovery))
        {
            var binding = discovery.TryGetProperty("binding", out var value) && value.ValueKind == JsonValueKind.Object
                ? new VerifiedRemNoteBinding(value.Deserialize<Contracts.RemNoteBindingReference>(JsonOptions)!) : null;
            snapshot = snapshot with { RemNoteDiscovery = new(discovery.GetProperty("state").GetString()!, discovery.GetProperty("reasonCodes").Deserialize<string[]>(JsonOptions)!,
                discovery.GetProperty("candidateCount").GetInt32(), discovery.GetProperty("persistableBinding").GetBoolean(), binding, evidence) };
        }
        evidence = ReadOptionalArtifactHandle(outputs, "remNoteProbeArtifact");
        if (evidence is not null && result.TryGetProperty("remNoteReadiness", out var readiness))
            snapshot = snapshot with { RemNoteReadiness = new(readiness.GetProperty("connected").GetBoolean(), readiness.GetProperty("writePermission").GetBoolean(),
                readiness.GetProperty("exactTargetMatch").GetBoolean(), readiness.GetProperty("readyForRequestedAction").GetBoolean(), readiness.GetProperty("reasonCodes").Deserialize<string[]>(JsonOptions)!, evidence) };
        return snapshot with { DestinationSnapshot = ReadOptionalArtifactHandle(outputs, "destinationSnapshotArtifact"),
            Destinations = ReadDestinations(result) };
    }
    private static IReadOnlyList<DestinationProjection> ReadDestinations(JsonElement value)
        => value.TryGetProperty("destinations", out var destinations) && destinations.ValueKind == JsonValueKind.Array
            ? destinations.Deserialize<Contracts.DestinationResult[]>(JsonOptions)!.Select(x => new DestinationProjection(x.DestinationId, x.Kind, x.State, x.ReasonCode,
                new(x.SourceCardArtifact), new(x.ConfigurationSnapshot), x.Evidence is null ? null : new(x.Evidence), x.ExternalStateKnown, x.ExternalWriteCount)).ToArray()
            : Array.Empty<DestinationProjection>();
}

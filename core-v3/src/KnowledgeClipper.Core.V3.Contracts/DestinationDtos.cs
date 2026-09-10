namespace KnowledgeClipper.Core.V3.Contracts;

// Additive payloads; the existing wire/event/artifact envelopes are unchanged.
internal static class DestinationSchemas { public const string Command = "kc.destination-cards-command/1"; }
internal sealed record RemNoteBindingReference(string DiscoveryOperationId, ArtifactReference Evidence, string RootRemId, string RootTitle);
internal sealed record DestinationSelection(string Kind, ArtifactReference Configuration, string? RouteKey, RemNoteBindingReference? Binding);
internal sealed record DestinationWriteGrant(string Kind, string AuthorizationId, string Scope, string TargetIdentity);
internal sealed record FrozenDestination(string DestinationId, string Kind, ArtifactReference Configuration, string TargetIdentity, string? RouteKey, RemNoteBindingReference? Binding);
internal sealed record DestinationSnapshot(string SourceOperationId, IReadOnlyList<FrozenDestination> Destinations);
internal sealed record DestinationResult(string DestinationId, string Kind, string State, string? ReasonCode,
    ArtifactReference SourceCardArtifact, ArtifactReference ConfigurationSnapshot, ArtifactReference? Evidence,
    bool ExternalStateKnown, int? ExternalWriteCount);

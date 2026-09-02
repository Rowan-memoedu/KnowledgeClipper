using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeClipper.Core.V3.Contracts;

internal static class KernelSchemas
{
    public const string WireRequest = "kc.wire-request/2";
    public const string WireResponse = "kc.wire-response/2";
    public const string Command = "kc.command/2";
    public const string CommandReceipt = "kc.command-receipt/2";
    public const string Query = "kc.query/2";
    public const string QueryResult = "kc.query-result/2";
    public const string Event = "kc.event/2";
    public const string Artifact = "kc.artifact/2";
    public const string ArtifactReference = "kc.artifact-ref/2";
    public const string CapabilityCatalog = "kc.capability-catalog/1";
    public const string CapabilityBinding = "kc.capability-binding/1";
    public const string HandshakeResult = "kc.handshake-result/2";
}

internal sealed record KernelSubject(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id);

internal sealed record AuthorizationReference(
    [property: JsonPropertyName("authorizationId")] string AuthorizationId,
    [property: JsonPropertyName("policy")] string Policy);

internal sealed record ContractBody(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("data")] JsonElement Data);

internal sealed record CommandEnvelope(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("subject")] KernelSubject Subject,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("expectedRevision")] long? ExpectedRevision,
    [property: JsonPropertyName("authorization")] AuthorizationReference? Authorization,
    [property: JsonPropertyName("inputs")] IReadOnlyList<ArtifactReference> Inputs,
    [property: JsonPropertyName("body")] ContractBody Body);

internal sealed record QueryEnvelope(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("queryId")] string QueryId,
    [property: JsonPropertyName("projection")] string Projection,
    [property: JsonPropertyName("subject")] KernelSubject? Subject,
    [property: JsonPropertyName("consistency")] string Consistency,
    [property: JsonPropertyName("cursor")] long? Cursor,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

internal sealed record KernelEvent(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("streamId")] string StreamId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("eventVersion")] int EventVersion,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("causationId")] string CausationId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("effectClass")] string EffectClass,
    [property: JsonPropertyName("data")] JsonElement Data,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<ArtifactReference> Artifacts);

internal sealed record CommandReceipt(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("events")] IReadOnlyList<KernelEvent> Events,
    [property: JsonPropertyName("error")] KernelError? Error);

internal sealed record QueryResult(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("queryId")] string QueryId,
    [property: JsonPropertyName("projection")] string Projection,
    [property: JsonPropertyName("projectionVersion")] int ProjectionVersion,
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("data")] JsonElement Data);

internal sealed record KernelError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable);

internal sealed record WireRequestEnvelope(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("payload")] JsonElement Payload);

internal sealed record WireResponseEnvelope(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("error")] KernelError? Error);

internal sealed record HandshakeResult(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("kernelVersion")] string KernelVersion,
    [property: JsonPropertyName("currentWireSchema")] string CurrentWireSchema,
    [property: JsonPropertyName("capabilityCatalogId")] string CapabilityCatalogId,
    [property: JsonPropertyName("capabilityCatalogSha256")] string CapabilityCatalogSha256,
    [property: JsonPropertyName("legacyCompatibilityBoundary")] string LegacyCompatibilityBoundary);

internal sealed record ArtifactDigest(
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("value")] string Value);

internal sealed record ArtifactRepresentation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("schema")] string? Schema,
    [property: JsonPropertyName("digest")] ArtifactDigest Digest,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles);

internal sealed record ArtifactProvenance(
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("inputs")] IReadOnlyList<ArtifactReference> Inputs);

internal sealed record ArtifactPolicies(
    [property: JsonPropertyName("dataClass")] string DataClass,
    [property: JsonPropertyName("retention")] string Retention);

internal sealed record ArtifactEnvelope(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("storeId")] string StoreId,
    [property: JsonPropertyName("representations")] IReadOnlyList<ArtifactRepresentation> Representations,
    [property: JsonPropertyName("primaryRepresentation")] string PrimaryRepresentation,
    [property: JsonPropertyName("provenance")] ArtifactProvenance Provenance,
    [property: JsonPropertyName("policies")] ArtifactPolicies Policies);

internal sealed record ArtifactReference(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("manifestDigest")] ArtifactDigest ManifestDigest,
    [property: JsonPropertyName("storeId")] string StoreId);

internal sealed record WorkerBinding(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("workerId")] string? WorkerId,
    [property: JsonPropertyName("versionRange")] string? VersionRange,
    [property: JsonPropertyName("handler")] string? Handler)
{
    [JsonPropertyName("authority")]
    public WorkerAuthorityBinding? Authority { get; init; }
}

internal sealed record WorkerAuthorityBinding(
    [property: JsonPropertyName("releaseRoot")] string ReleaseRoot,
    [property: JsonPropertyName("registryPath")] string RegistryPath,
    [property: JsonPropertyName("registrySha256")] string RegistrySha256,
    [property: JsonPropertyName("entrypointPath")] string EntrypointPath,
    [property: JsonPropertyName("entrypointSha256")] string EntrypointSha256,
    [property: JsonPropertyName("exactWorkerVersion")] string ExactWorkerVersion,
    [property: JsonPropertyName("contractVersion")] string ContractVersion);

internal sealed record UiAffordance(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("visible")] bool Visible);

internal sealed record CapabilityDescriptor(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("availabilityReason")] string? AvailabilityReason,
    [property: JsonPropertyName("commandSchema")] string CommandSchema,
    [property: JsonPropertyName("resultArtifactKind")] string? ResultArtifactKind,
    [property: JsonPropertyName("resultContract")] string? ResultContract,
    [property: JsonPropertyName("effectClass")] string EffectClass,
    [property: JsonPropertyName("authorizationPolicy")] string AuthorizationPolicy,
    [property: JsonPropertyName("retryPolicy")] string RetryPolicy,
    [property: JsonPropertyName("idempotencyPolicy")] string IdempotencyPolicy,
    [property: JsonPropertyName("timeoutMilliseconds")] int TimeoutMilliseconds,
    [property: JsonPropertyName("workerBinding")] WorkerBinding WorkerBinding,
    [property: JsonPropertyName("uiAffordance")] UiAffordance UiAffordance);

internal sealed record AuthorityAnchor(
    [property: JsonPropertyName("authorityVersion")] string AuthorityVersion,
    [property: JsonPropertyName("checkpointPath")] string CheckpointPath,
    [property: JsonPropertyName("checkpointSha256")] string CheckpointSha256);

internal sealed record CapabilityCatalogDocument(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("catalogId")] string CatalogId,
    [property: JsonPropertyName("catalogVersion")] string CatalogVersion,
    [property: JsonPropertyName("authorityAnchor")] AuthorityAnchor AuthorityAnchor,
    [property: JsonPropertyName("operations")] IReadOnlyList<CapabilityDescriptor> Operations);

internal sealed record CapabilityCatalogBinding(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("catalogPath")] string CatalogPath,
    [property: JsonPropertyName("catalogSha256")] string CatalogSha256,
    [property: JsonPropertyName("authorityAnchor")] AuthorityAnchor AuthorityAnchor);

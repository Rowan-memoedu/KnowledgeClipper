using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed record LegacyArtifactReference(
    string ArtifactRef,
    string Kind,
    string ContractVersion,
    string ManifestPath,
    string ExpectedSha256);

internal sealed record LegacyCancelSignalReference(string SchemaVersion, string Path, int GracePeriodMs);

internal sealed record LegacyWorkerRequest(
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
    LegacyCancelSignalReference CancelSignal,
    IReadOnlyList<LegacyArtifactReference> InputRefs,
    JsonElement ConfigSnapshot,
    JsonElement Payload);

internal sealed record LegacyWorkerIdentity(string Id, string Version);

internal sealed record LegacyWorkerError(
    string Code,
    string Category,
    bool Retryable,
    string DetailsRef,
    string? Message,
    string? Blame);

internal sealed record LegacyWorkerResult(
    string SchemaVersion,
    string RequestId,
    string OperationId,
    string TaskId,
    string RunId,
    string StageId,
    string AttemptId,
    LegacyWorkerIdentity Worker,
    string ContractVersion,
    string IdempotencyKey,
    string Status,
    IReadOnlyList<LegacyArtifactReference> Outputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] LegacyWorkerError? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> Warnings,
    JsonElement? Payload = null,
    JsonElement? SideEffects = null);

internal sealed record LegacyAttemptPackage(
    string JobRoot,
    string RequestPath,
    string ResultPath,
    string CancelPath,
    LegacyWorkerRequest Request);

internal sealed record WorkerProcessInvocationResult(int ExitCode, bool TimedOut, bool Cancelled, string StandardErrorPath);
internal sealed record PublicWorkerCommandPackage(string Command, string RequestPath, string StandardOutputPath, string StandardErrorPath);

internal interface IWorkerProcessInvoker
{
    Task<WorkerProcessInvocationResult> InvokePublicAsync(AdmittedAuthorityWorker worker, PublicWorkerCommandPackage package, TimeSpan timeout, CancellationToken cancellationToken)
        => throw new NotSupportedException("This invoker does not implement read-only public worker commands.");
    Task<WorkerProcessInvocationResult> InvokeAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

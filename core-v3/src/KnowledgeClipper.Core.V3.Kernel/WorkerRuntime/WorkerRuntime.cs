using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

internal sealed record WorkerInvocation(
    string OperationId,
    WorkerBinding Binding,
    JsonElement Request,
    TimeSpan Timeout);

internal sealed record WorkerExecutionResult(
    string Status,
    JsonElement Payload,
    IReadOnlyList<ArtifactReference> Artifacts,
    int ExternalWriteCount,
    bool ExternalStateKnown);

internal sealed class WorkerExternalStateUnknownException : Exception
{
    public WorkerExternalStateUnknownException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal interface IWorkerRuntime
{
    bool RequiresLegacyQuestionWorkflow => false;

    Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken);

    WorkerRuntimeMetrics GetMetrics();

    WorkerBinding GetAdmittedBinding(string workerId);
}

internal sealed record WorkerRuntimeMetrics(
    int ContentAttempts,
    int AnkiProbeAttempts,
    int AnkiExportAttempts,
    int AnkiExternalWriteCount,
    int AnkiReadbackCount,
    int RemNoteCalls,
    int MinerUCalls,
    int SpeakerCalls,
    string Mode)
{
    public int AnkiUnknownWriteAttempts { get; init; }
}

internal sealed class DisabledWorkerRuntime : IWorkerRuntime
{
    public Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException($"Worker execution is not enabled in the Core v3 foundation. Requested operation: {invocation.OperationId}");
    }

    public WorkerRuntimeMetrics GetMetrics() => new(0, 0, 0, 0, 0, 0, 0, 0, "disabled");

    public WorkerBinding GetAdmittedBinding(string workerId) => throw new InvalidOperationException("Worker execution is disabled.");
}

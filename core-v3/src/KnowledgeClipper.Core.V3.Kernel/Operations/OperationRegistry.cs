using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Application;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal sealed record OperationExecutionResult(
    JsonElement Data,
    IReadOnlyList<ArtifactReference> Artifacts,
    int ExternalWriteCount,
    bool ExternalStateKnown);

internal sealed record OperationContext(
    CapabilityDescriptor Capability,
    IWorkerRuntime WorkerRuntime,
    FileArtifactStore ArtifactStore,
    IEventStore EventStore);

internal interface IOperationHandler
{
    string OperationId { get; }

    Task<OperationExecutionResult> ExecuteAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken);
}

internal sealed class OperationRegistry
{
    private readonly IReadOnlyDictionary<string, IOperationHandler> _handlers;

    public OperationRegistry(IEnumerable<IOperationHandler> handlers)
    {
        var map = new Dictionary<string, IOperationHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.OperationId, handler))
            {
                throw new InvalidOperationException($"Duplicate operation handler '{handler.OperationId}'.");
            }
        }

        _handlers = map;
    }

    public IOperationHandler GetRequired(string operationId)
    {
        if (!_handlers.TryGetValue(operationId, out var handler))
        {
            throw new KernelContractException("operation_handler_not_found", $"No handler is registered for operation '{operationId}'.");
        }

        return handler;
    }
}

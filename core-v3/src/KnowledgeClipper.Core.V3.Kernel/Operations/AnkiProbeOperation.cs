using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal sealed class AnkiProbeOperation : IOperationHandler
{
    public string OperationId => "anki.probe";

    public async Task<OperationExecutionResult> ExecuteAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        RequireBody(command, "kc.anki-probe-command/1");
        var configuration = ReadReference(command.Body.Data, "configuration");
        Require(command.Inputs.Count == 1 && command.Inputs[0] == configuration, "Anki probe input reference mismatch.");
        var verifiedConfiguration = await context.ArtifactStore.OpenVerifiedAsync(configuration, configuration.Kind, configuration.Contract, cancellationToken).ConfigureAwait(false);
        var configurationJson = System.Text.Encoding.UTF8.GetString(verifiedConfiguration.Primary.Content);
        var selectedRoute = command.Body.Data.TryGetProperty("selectedRoute", out var route) && route.ValueKind == JsonValueKind.String
            ? route.GetString()!
            : "canary";
        var worker = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
            command.OperationId,
            context.Capability.WorkerBinding,
            StrictJson.CloneElement(new { phase = "probe", configuration, configurationJson, selectedRoute, command.IdempotencyKey }),
            TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
        Require(worker.Status == "completed" && worker.ExternalWriteCount == 0 && worker.ExternalStateKnown, "Anki probe violated read-only semantics.");
        var artifact = await DomainArtifactFactory.PublishJsonAsync(
            context.ArtifactStore,
            command,
            "anki-connection-status",
            "1.0.0",
            "kc.anki-connection-status/1",
            worker.Payload,
            command.Inputs,
            cancellationToken).ConfigureAwait(false);
        return new OperationExecutionResult(
            StrictJson.CloneElement(new
            {
                status = worker.Payload.GetProperty("status").GetString(),
                worker = "export.anki",
                externalWriteCount = 0,
                readiness = worker.Payload.GetProperty("readiness")
            }),
            new[] { artifact },
            0,
            true);
    }

    private static void RequireBody(CommandEnvelope command, string schema)
        => Require(command.Body.Schema == schema && command.Body.Data.ValueKind == JsonValueKind.Object, "Anki probe command body is invalid.");

    private static ArtifactReference ReadReference(JsonElement element, string name)
        => element.GetProperty(name).Deserialize<ArtifactReference>(KernelJson.Options)
           ?? throw new InvalidDataException($"Anki probe '{name}' reference is invalid.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

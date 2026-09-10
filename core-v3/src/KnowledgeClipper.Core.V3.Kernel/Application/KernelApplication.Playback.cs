using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

namespace KnowledgeClipper.Core.V3.Kernel.Application;

internal sealed partial class KernelApplication
{
    private readonly object _playbackCommandGate = new();

    private CommandReceipt HandlePlaybackStop(CommandEnvelope command)
    {
        // A local control command has no exporter authorization, no input artifact,
        // and never appends operation lifecycle events to the Read stream.
        if (command.Operation != "clip.read-aloud-cards" || command.Authorization is not null || command.ExpectedRevision is not null || command.Inputs.Count != 0 ||
            command.Body.Data.ValueKind != JsonValueKind.Object || command.Body.Data.EnumerateObject().Count() != 2 ||
            !command.Body.Data.TryGetProperty("action", out var action) || action.GetString() != "stop_playback" ||
            !command.Body.Data.TryGetProperty("targetToken", out var target) || target.ValueKind != JsonValueKind.String ||
            target.GetString() is not { Length: 64 } targetToken || !targetToken.All(Uri.IsHexDigit))
            throw new KernelContractException("playback.stop_contract_invalid", "Stop requires its typed target and no task inputs or export authorization.");
        if (!_catalog.GetRequired(command.Operation).Available || !_eventStore.ReadStream(command.OperationId).Any(item => item.Operation == command.Operation))
            throw new KernelContractException("playback.operation_not_found", "Stop must target an existing Read operation.");
        var control = _workerRuntime.PlaybackControl ?? throw new KernelContractException("playback.control_unavailable", "This runtime does not expose playback control.");
        lock (_playbackCommandGate)
        {
            var claimed = _commandIndex.TryClaim(command, "playback-control");
            if (!claimed.Acquired)
            {
                var existing = _commandIndex.TryReadReceipt(command.IdempotencyKey);
                if (existing is not null) return existing;
                RecoverPlaybackStop(claimed.Claim);
                return _commandIndex.TryReadReceipt(command.IdempotencyKey)!;
            }
            var stream = StopCommandStream(command.CommandId);
            KernelEvent Event(string type, JsonElement data) => new(KernelSchemas.Event, "evt-" + Guid.NewGuid().ToString("N"), stream, 0,
                type, 1, DateTimeOffset.UtcNow, command.CommandId, command.CommandId, command.Operation, "playback-control", data, Array.Empty<ArtifactReference>());
            _eventStore.Append(stream, 0, new[] { Event("command.accepted", StrictJson.CloneElement(new { command.OperationId, command.CommandId, targetToken })) });
            KernelError? error = null;
            try
            {
                var resolved = control.RequestStop(command.OperationId, targetToken, command.CommandId);
                _eventStore.Append(stream, 1, new[] { Event("playback.stop.requested", resolved) });
            }
            catch (Exception ex)
            {
                error = new KernelError(ex is KernelContractException contract ? contract.Code : "playback.stop_state_unknown", ex.Message, false);
                _eventStore.Append(stream, 1, new[] { Event("playback.stop.rejected", StrictJson.CloneElement(error)) });
            }
            var receipt = new CommandReceipt(KernelSchemas.CommandReceipt, command.CommandId, command.OperationId, error is null,
                _eventStore.Cursor, _eventStore.ReadStream(stream), error);
            _commandIndex.Complete(command.IdempotencyKey, receipt);
            return receipt;
        }
    }

    private void RecoverPlaybackStop(CommandClaim claim)
    {
        if (_commandIndex.TryReadReceipt(claim.IdempotencyKey) is not null) return;
        var events = _eventStore.ReadStream(StopCommandStream(claim.FirstCommandId));
        var requested = events.Any(item => item.EventType == "playback.stop.requested");
        // Even if a signal may already exist, never send it again during recovery.
        _commandIndex.Complete(claim.IdempotencyKey, new CommandReceipt(KernelSchemas.CommandReceipt, claim.FirstCommandId,
            claim.OperationId, requested, _eventStore.Cursor, events,
            requested ? null : new KernelError("playback.stop_interrupted", "Stop acknowledgement was interrupted; inspect playback state. No signal was retried.", false)));
    }

    private QueryResult BuildPlaybackControlProjection(QueryEnvelope query)
    {
        if (!query.Parameters.TryGetProperty("operationId", out var value) || value.ValueKind != JsonValueKind.String)
            throw new KernelContractException("query_parameters_invalid", "Playback query requires operationId.");
        var operationId = value.GetString()!;
        RequireToken(operationId, "operationId");
        var snapshot = _workerRuntime.PlaybackControl?.Snapshot(operationId)
            ?? StrictJson.CloneElement(new { operationId, state = "unsupported", canStop = false, sequence = 0 });
        return new QueryResult(KernelSchemas.QueryResult, query.QueryId, query.Projection, 1, _eventStore.Cursor, snapshot);
    }
    private static string StopCommandStream(string commandId) => "stop-" + PlaybackAttemptControl.Stream(commandId);
}

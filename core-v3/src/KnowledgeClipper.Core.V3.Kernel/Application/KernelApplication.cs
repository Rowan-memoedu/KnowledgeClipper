using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.Application;

internal sealed partial class KernelApplication
{
    private readonly AdmittedCapabilityCatalog _catalog;
    private readonly OperationRegistry _operations;
    private readonly IWorkerRuntime _workerRuntime;
    private readonly IEventStore _eventStore;
    private readonly ICommandIndex _commandIndex;
    private readonly FileArtifactStore _artifactStore;
    private readonly byte[] _operationDiscoveryKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Task> _backgroundDispatches = new(StringComparer.Ordinal);

    public KernelApplication(
        AdmittedCapabilityCatalog catalog,
        OperationRegistry operations,
        IWorkerRuntime workerRuntime,
        IEventStore eventStore,
        ICommandIndex commandIndex,
        FileArtifactStore artifactStore)
    {
        _catalog = catalog;
        _operations = operations;
        _workerRuntime = workerRuntime;
        _eventStore = eventStore;
        _commandIndex = commandIndex;
        _artifactStore = artifactStore;
    }

    public AdmittedCapabilityCatalog Catalog => _catalog;

    public void RecoverIncompleteCommands()
    {
        foreach (var claim in _commandIndex.ReadClaims())
        {
            if (claim.EffectClass == "playback-control") { RecoverPlaybackStop(claim); continue; }
            if (_commandIndex.TryReadReceipt(claim.IdempotencyKey) is not null)
            {
                continue;
            }

            var events = _eventStore.ReadStream(claim.OperationId);
            var accepted = events.LastOrDefault(x => x.EventType == "command.accepted" && x.CorrelationId == claim.FirstCommandId);
            if (TerminalForCommand(events, claim.FirstCommandId) is null)
            {
                var recoveryEvents = accepted is null
                    ? new[] { NewRecoveryAccepted(claim), NewRecoveryTerminal(claim, "crash_after_claim", externalStateMayBeUnknown: false) }
                    : new[] { NewRecoveryTerminal(claim, "process_interrupted", externalStateMayBeUnknown: claim.EffectClass == "external-write") };
                try
                {
                    var appended = _eventStore.Append(claim.OperationId, events.LastOrDefault()?.Sequence ?? 0, recoveryEvents);
                    events = events.Concat(appended).ToArray();
                }
                catch (EventStoreConcurrencyException)
                {
                    events = _eventStore.ReadStream(claim.OperationId);
                    if (TerminalForCommand(events, claim.FirstCommandId) is null)
                    {
                        throw;
                    }
                }
            }

            var receipt = BuildReceipt(claim.FirstCommandId, claim.OperationId, events);
            _commandIndex.Complete(claim.IdempotencyKey, receipt);
            ReconcileOrphanQuestionSession(claim, terminalEventType: events.Last().EventType);
        }
    }

    public async Task<CommandReceipt> HandleCommandAsync(CommandEnvelope command, CancellationToken cancellationToken)
    {
        ValidateCommand(command);
        if (command.Body.Schema == "kc.read-playback-stop/1") return HandlePlaybackStop(command);
        var capability = _catalog.GetRequired(command.Operation);
        if (!capability.Available)
        {
            throw new KernelContractException("capability_unavailable", capability.AvailabilityReason ?? "Capability is unavailable.");
        }

        ValidatePolicies(command, capability);
        var handler = _operations.GetRequired(command.Operation);
        var claim = _commandIndex.TryClaim(command, capability.EffectClass);
        if (!claim.Acquired)
        {
            var existingReceipt = _commandIndex.TryReadReceipt(command.IdempotencyKey);
            if (existingReceipt is not null)
            {
                return existingReceipt;
            }

            if (claim.OwnerProcessAlive && capability.WorkerBinding.Mode == "worker")
            {
                var acceptedEvents = _eventStore.ReadStream(claim.Claim.OperationId);
                if (acceptedEvents.Count > 0)
                {
                    return BuildReceipt(claim.Claim.FirstCommandId, claim.Claim.OperationId, acceptedEvents);
                }
            }

            if (claim.OwnerProcessAlive)
            {
                return await WaitForConcurrentReceiptAsync(command.IdempotencyKey, capability, cancellationToken).ConfigureAwait(false);
            }

            command = command with
            {
                CommandId = claim.Claim.FirstCommandId,
                OperationId = claim.Claim.OperationId
            };
            var recovered = RecoverInterruptedCommand(command, capability);
            if (recovered is not null)
            {
                _commandIndex.Complete(command.IdempotencyKey, recovered);
                return recovered;
            }
        }

        var initialRevision = _eventStore.GetRevision(command.OperationId);
        if (command.ExpectedRevision.HasValue && command.ExpectedRevision.Value != initialRevision)
        {
            throw new EventStoreConcurrencyException(command.OperationId, command.ExpectedRevision.Value, initialRevision);
        }

        var now = DateTimeOffset.UtcNow;
        if (capability.WorkerBinding.Mode == "worker")
        {
            var accepted = _eventStore.Append(command.OperationId, initialRevision, new[]
            {
                NewEvent(command, "command.accepted", capability, now, StrictJson.CloneElement(new { command.CommandId, command.IdempotencyKey, dispatch = "background" }))
            });
            var dispatch = DispatchWorkerCommandAsync(command, capability, handler, accepted[^1].Sequence);
            _backgroundDispatches[command.IdempotencyKey] = dispatch;
            _ = dispatch.ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    _backgroundDispatches.TryRemove(command.IdempotencyKey, out var ignoredDispatch);
                    _ = ignoredDispatch;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return BuildReceipt(command, accepted);
        }

        var started = _eventStore.Append(command.OperationId, initialRevision, new List<KernelEvent>
        {
            NewEvent(command, "command.accepted", capability, now, StrictJson.CloneElement(new { command.CommandId, command.IdempotencyKey })),
            NewEvent(command, "operation.started", capability, now, StrictJson.CloneElement(new { command.OperationId }))
        });

        var terminal = await ExecuteWithTerminalGuaranteeAsync(command, capability, handler, cancellationToken).ConfigureAwait(false);

        var completed = _eventStore.Append(command.OperationId, started[^1].Sequence, new[] { terminal });
        var committed = started.Concat(completed).ToArray();
        var receipt = BuildReceipt(command, committed);
        _commandIndex.Complete(command.IdempotencyKey, receipt);
        return receipt;
    }

    private async Task DispatchWorkerCommandAsync(
        CommandEnvelope command,
        CapabilityDescriptor capability,
        IOperationHandler handler,
        long acceptedRevision)
    {
        try
        {
            var started = _eventStore.Append(command.OperationId, acceptedRevision, new[]
            {
                NewEvent(command, "operation.started", capability, DateTimeOffset.UtcNow, StrictJson.CloneElement(new { command.OperationId, dispatch = "background" }))
            });
            var terminal = await ExecuteWithTerminalGuaranteeAsync(command, capability, handler, CancellationToken.None).ConfigureAwait(false);
            // Handlers may persist typed progress events between started and terminal.
            // Terminal state is always appended after the latest durable stage, never
            // by replaying the pre-dispatch revision.
            _eventStore.Append(command.OperationId, _eventStore.GetRevision(command.OperationId), new[] { terminal });
        }
        catch (Exception ex)
        {
            var events = _eventStore.ReadStream(command.OperationId);
            // A replay appends a second command to an already completed durable
            // stream.  Only a terminal after this command's accepted revision
            // completes this dispatch; an earlier terminal must never mask a
            // failed replay.
            if (!events.Any(item => item.Sequence > acceptedRevision && IsTerminal(item)))
            {
                try
                {
                    _eventStore.Append(command.OperationId, events.LastOrDefault()?.Sequence ?? 0, new[]
                    {
                        FailureEvent(command, capability, "background_dispatch_failed", ex.Message)
                    });
                }
                catch (EventStoreConcurrencyException)
                {
                }
            }
        }
        finally
        {
            var events = _eventStore.ReadStream(command.OperationId);
            if (events.Any(item => item.Sequence > acceptedRevision && IsTerminal(item)))
            {
                _commandIndex.Complete(command.IdempotencyKey, BuildReceipt(command, events));
            }
        }
    }

    public async Task<QueryResult> HandleQueryAsync(QueryEnvelope query, CancellationToken cancellationToken)
    {
        if (query.Schema != KernelSchemas.Query)
        {
            throw new KernelContractException("query_schema_unsupported", $"Unsupported query schema '{query.Schema}'.");
        }

        return query.Projection switch
        {
            "capabilities.get" => new QueryResult(
                KernelSchemas.QueryResult,
                query.QueryId,
                query.Projection,
                ProjectionVersion: 1,
                _eventStore.Cursor,
                StrictJson.CloneElement(new
                {
                    catalog = _catalog.Document,
                    catalogSha256 = _catalog.Sha256
                })),
            "operation.projection" => await BuildOperationProjectionAsync(query, cancellationToken).ConfigureAwait(false),
            "read-aloud-cards.projection" => await BuildReadAloudCardsProjectionAsync(query, cancellationToken).ConfigureAwait(false),
            "read-playback.projection" => BuildPlaybackControlProjection(query),
            "read-aloud-cards.discovery.begin" => BuildReadAloudCardsDiscoveryBegin(query),
            "read-aloud-cards.discovery.accepted" => BuildReadAloudCardsDiscoveryAccepted(query),
            "read-aloud-cards.action-discovery.begin" => BuildReadAloudCardsActionDiscoveryBegin(query),
            "read-aloud-cards.action-discovery.accepted" => BuildReadAloudCardsActionDiscoveryAccepted(query),
            "question.projection" => await BuildQuestionProjectionAsync(query, cancellationToken).ConfigureAwait(false),
            "task.projection" => BuildTaskProjection(query),
            "artifact.read" => await BuildArtifactReadAsync(query, cancellationToken).ConfigureAwait(false),
            "vertical-slice.metrics" => new QueryResult(
                KernelSchemas.QueryResult,
                query.QueryId,
                query.Projection,
                ProjectionVersion: 1,
                _eventStore.Cursor,
                StrictJson.CloneElement(_workerRuntime.GetMetrics())),
            _ => throw new KernelContractException("projection_not_found", $"Unknown projection '{query.Projection}'.")
        };
    }

    private async Task<QueryResult> BuildOperationProjectionAsync(QueryEnvelope query, CancellationToken cancellationToken)
    {
        if (query.Parameters.ValueKind != JsonValueKind.Object ||
            !query.Parameters.TryGetProperty("operationId", out var operationIdElement) ||
            operationIdElement.ValueKind != JsonValueKind.String)
        {
            throw new KernelContractException("query_parameters_invalid", "operation.projection requires a string operationId.");
        }

        var operationId = operationIdElement.GetString()!;
        var events = _eventStore.ReadStream(operationId);
        var terminal = events.LastOrDefault(IsTerminal);
        var lastAccepted = events.LastOrDefault(x => x.EventType == "command.accepted");
        var pending = lastAccepted is not null && (terminal is null || lastAccepted.Sequence > terminal.Sequence);
        if (terminal is { EventType: "operation.completed", Operation: "remnote.discover-target" } &&
            terminal.Data.GetProperty("result").GetProperty("discovery").TryGetProperty("binding", out var restoredBinding) && restoredBinding.ValueKind == JsonValueKind.Object)
            await RemNoteSettingsOperation.VerifyBindingAsync(restoredBinding.Deserialize<RemNoteBindingReference>(KernelJson.Options)!,
                new OperationContext(_catalog.GetRequired(terminal.Operation), _workerRuntime, _artifactStore, _eventStore), cancellationToken).ConfigureAwait(false);
        var state = terminal?.EventType switch
        {
            "operation.completed" => "Succeeded",
            "operation.failed" => "Failed",
            "operation.cancelled" => "Cancelled",
            "operation.unknown-external-state" => "Degraded",
            _ when events.Count > 0 => "Running",
            _ => "Degraded"
        };
        if (pending) state = "Running";
        else if (terminal is { EventType: "operation.completed" } && terminal.Data.GetProperty("result").TryGetProperty("destinations", out var endStates) &&
            endStates.EnumerateArray().Any(x => x.GetProperty("state").GetString() != "succeeded")) state = "Degraded";
        var destinationSource = events.LastOrDefault(x => x.EventType == "operation.completed" && x.Artifacts.Any(a => a.Kind == "destination-snapshot"));
        var durableResults = await ProjectDestinationsAsync(operationId, destinationSource, cancellationToken).ConfigureAwait(false);
        var notice = terminal is null
            ? (events.Count == 0 ? "Operation was not found." : null)
            : terminal.EventType == "operation.completed"
                ? null
                : terminal.Data.TryGetProperty("message", out var message) ? message.GetString() : "Operation reached a non-success terminal state.";
        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            _eventStore.Cursor,
            StrictJson.CloneElement(new
            {
                operationId,
                state,
                notice,
                terminalCommandId = terminal?.CorrelationId,
                lifecycle = events.Select(item => item.EventType).ToArray(),
                result = MergeDestinationResults(terminal?.EventType == "operation.completed" ? terminal.Data.GetProperty("result") : (JsonElement?)null, durableResults),
                outputs = BuildOperationOutputs(destinationSource is not null ? terminal?.EventType == "operation.completed" ? terminal : destinationSource : terminal)
            }));
    }

    private async Task<QueryResult> BuildReadAloudCardsProjectionAsync(QueryEnvelope query, CancellationToken cancellationToken)
    {
        var operationId = GetRequiredParameter(query, "operationId");
        var events = _eventStore.ReadStream(operationId);
        var terminal = events.LastOrDefault(IsTerminal);
        var terminalSequence = terminal?.Sequence ?? 0;
        var tail = events.LastOrDefault();
        var actionRunning = tail is not null && tail.Sequence > terminalSequence;
        var activeCommand = actionRunning
            ? events.LastOrDefault(item => item.Sequence > terminalSequence && item.EventType == "command.accepted")
            : null;
        var projectionTerminal = actionRunning ? null : terminal;
        var latestStage = events.LastOrDefault(item => item.EventType == "read-aloud-cards.stage" && (!actionRunning || item.Sequence > terminalSequence));
        var terminalResult = projectionTerminal?.EventType == "operation.completed" && projectionTerminal.Data.TryGetProperty("result", out var completed)
            ? completed : (JsonElement?)null;
        var playback = terminalResult is { } result && result.TryGetProperty("playback", out var terminalPlayback)
            ? terminalPlayback.GetString()
            : latestStage?.Data.TryGetProperty("playback", out var stagePlayback) == true ? stagePlayback.GetString() : "accepted";
        var runningPipeline = latestStage?.Data.TryGetProperty("pipeline", out var activeStagePipeline) == true &&
            activeStagePipeline.GetString() is "reviewing" or "writing_anki"
                ? activeStagePipeline.GetString()
                : "accepted";
        var pipeline = actionRunning ? runningPipeline : projectionTerminal?.EventType switch
        {
            "operation.unknown-external-state" => "unknown_external_state",
            "operation.failed" or "operation.cancelled" => "failed",
            "operation.completed" when terminalResult is { } value && value.TryGetProperty("pipeline", out var terminalPipeline) => terminalPipeline.GetString(),
            _ when latestStage?.Data.TryGetProperty("pipeline", out var stagePipeline) == true => stagePipeline.GetString(),
            _ when events.Count > 0 => "accepted",
            _ => "failed"
        };
        var actionState = actionRunning ? "running" : projectionTerminal?.EventType switch
        {
            "operation.completed" => "completed",
            "operation.failed" => "failed",
            "operation.cancelled" => "cancelled",
            "operation.unknown-external-state" => "unknown_external_state",
            _ => events.Count == 0 ? "not_found" : "running"
        };
        var notice = actionRunning ? null
            : projectionTerminal is null ? (events.Count == 0 ? "Operation was not found." : null)
            : projectionTerminal.EventType == "operation.completed" ? null
            : projectionTerminal.Data.TryGetProperty("message", out var message) ? message.GetString() : "Read-to-Anki operation reached a non-success terminal state.";
        var cardDraft = terminalResult is { } projectionResult && projectionResult.TryGetProperty("cardDraft", out var draft)
            ? draft : (JsonElement?)null;
        var semanticReadback = terminalResult is { } terminalProjectionResult && terminalProjectionResult.TryGetProperty("semanticReadback", out var readback)
            ? readback : (JsonElement?)null;
        var cardGeneration = terminalResult is { } cardGenerationResult && cardGenerationResult.TryGetProperty("cardGeneration", out var generation)
            ? generation : (JsonElement?)null;
        var languageCardChoice = terminalResult is { } choiceResult && choiceResult.TryGetProperty("languageCardChoice", out var choice)
            ? choice : (JsonElement?)null;
        var cardResolution = terminalResult is { } resolutionResult && resolutionResult.TryGetProperty("cardResolution", out var resolution)
            ? resolution : (JsonElement?)null;
        var frozenLanguageCardChoice = terminalResult is { } frozenResult && frozenResult.TryGetProperty("frozenLanguageCardChoice", out var frozenChoice)
            ? frozenChoice : (JsonElement?)null;
        var attentionRequired = terminalResult is { } attentionResult && attentionResult.TryGetProperty("attentionRequired", out var attention) && attention.ValueKind == JsonValueKind.True;
        var settingsEvidence = projectionTerminal?.EventType == "operation.completed" && !projectionTerminal.Artifacts.Any(x => x.Kind == "destination-snapshot")
            ? await BuildReadAloudSettingsEvidenceAsync(projectionTerminal, cancellationToken).ConfigureAwait(false)
            : (JsonElement?)null;
        var contextEvidence = projectionTerminal?.EventType == "operation.completed"
            ? await BuildReadAloudContextEvidenceAsync(projectionTerminal, cancellationToken).ConfigureAwait(false)
            : (JsonElement?)null;
        var destinationSource = events.LastOrDefault(x => x.EventType == "operation.completed" && x.Artifacts.Any(a => a.Kind == "destination-snapshot"));
        var destinations = await ProjectDestinationsAsync(operationId, destinationSource, cancellationToken).ConfigureAwait(false);
        if (!actionRunning && destinations?.Any(x => x.State is "unknown" or "failed") == true) attentionRequired = true;
        return new QueryResult(KernelSchemas.QueryResult, query.QueryId, query.Projection, ProjectionVersion: destinationSource is null ? 4 : 5,
            tail?.Sequence ?? 0,
            ReadDestinationProjection(StrictJson.CloneElement(new
            {
                operationId,
                actionState,
                activeCommandId = activeCommand?.CorrelationId,
                terminalCommandId = terminal?.CorrelationId,
                terminalSequence,
                playback,
                pipeline,
                attentionRequired,
                notice,
                cardGeneration,
                languageCardChoice,
                cardResolution,
                frozenLanguageCardChoice,
                cardDraft,
                semanticReadback,
                settingsEvidence,
                contextEvidence,
                destinations,
                lifecycle = events.Select(item => item.EventType).ToArray(),
                outputs = BuildOperationOutputs(projectionTerminal?.EventType == "operation.completed" ? projectionTerminal : destinationSource ?? projectionTerminal)
            }), destinationSource is not null));
    }

    private QueryResult BuildReadAloudCardsDiscoveryBegin(QueryEnvelope query)
    {
        var marker = GetDiscoveryMarker(query);
        var accepted = ReadAcceptedReadAloudOperations();
        var payload = new OperationDiscoveryTokenPayload(
            "kc.read-aloud-cards-discovery-token/1",
            marker,
            _eventStore.Cursor,
            accepted.Select(item => item.CommandId).OrderBy(item => item, StringComparer.Ordinal).ToArray());
        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            payload.Cursor,
            StrictJson.CloneElement(new
            {
                correlationMarker = marker,
                checkpointToken = ProtectDiscoveryToken(payload)
            }));
    }

    private QueryResult BuildReadAloudCardsDiscoveryAccepted(QueryEnvelope query)
    {
        var marker = GetDiscoveryMarker(query);
        var token = GetRequiredParameter(query, "checkpointToken");
        var payload = UnprotectDiscoveryToken(token);
        if (!string.Equals(payload.CorrelationMarker, marker, StringComparison.Ordinal) ||
            query.Cursor != payload.Cursor)
        {
            throw new KernelContractException("operation_discovery_checkpoint_invalid", "Operation discovery marker or cursor does not match its signed checkpoint.");
        }

        var baseline = payload.AcceptedCommandIds.ToHashSet(StringComparer.Ordinal);
        var current = ReadAcceptedReadAloudOperations();
        var discovered = current.Where(item => !baseline.Contains(item.CommandId)).ToArray();
        if (discovered.Length > 1)
        {
            throw new KernelContractException("operation_discovery_ambiguous", "More than one new read-to-Anki operation was accepted after the checkpoint.");
        }

        var accepted = discovered.SingleOrDefault();
        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            _eventStore.Cursor,
            StrictJson.CloneElement(new
            {
                correlationMarker = marker,
                status = accepted is null ? "pending" : "accepted",
                operationId = accepted?.OperationId,
                commandId = accepted?.CommandId,
                acceptedSequence = accepted?.AcceptedSequence
            }));
    }

    private IReadOnlyList<AcceptedReadAloudOperation> ReadAcceptedReadAloudOperations()
    {
        var accepted = new List<AcceptedReadAloudOperation>();
        foreach (var claim in _commandIndex.ReadClaims().Where(item => item.Operation == ReadAloudCardsOperation.Id))
        {
            var evt = _eventStore.ReadStream(claim.OperationId).SingleOrDefault(item =>
                item.Sequence == 1 &&
                item.EventType == "command.accepted" &&
                item.CorrelationId == claim.FirstCommandId &&
                item.Operation == ReadAloudCardsOperation.Id);
            if (evt is not null)
            {
                accepted.Add(new AcceptedReadAloudOperation(claim.OperationId, claim.FirstCommandId, evt.Sequence));
            }
        }
        return accepted.OrderBy(item => item.OperationId, StringComparer.Ordinal).ToArray();
    }

    private QueryResult BuildReadAloudCardsActionDiscoveryBegin(QueryEnvelope query)
    {
        var operationId = GetRequiredParameter(query, "operationId");
        var marker = GetDiscoveryMarker(query);
        var accepted = ReadAcceptedReadAloudActions(operationId);
        if (accepted.Count == 0)
            throw new KernelContractException("action_discovery_subject_invalid", "Action discovery requires an accepted read-to-Anki operation stream.");
        var payload = new ActionDiscoveryTokenPayload(
            "kc.read-aloud-cards-action-discovery-token/1",
            operationId,
            marker,
            _eventStore.Cursor,
            accepted.Select(item => item.CommandId).OrderBy(item => item, StringComparer.Ordinal).ToArray());
        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            payload.Cursor,
            StrictJson.CloneElement(new
            {
                operationId,
                correlationMarker = marker,
                checkpointToken = ProtectActionDiscoveryToken(payload)
            }));
    }

    private QueryResult BuildReadAloudCardsActionDiscoveryAccepted(QueryEnvelope query)
    {
        var operationId = GetRequiredParameter(query, "operationId");
        var marker = GetDiscoveryMarker(query);
        var token = GetRequiredParameter(query, "checkpointToken");
        var payload = UnprotectActionDiscoveryToken(token);
        if (!string.Equals(payload.OperationId, operationId, StringComparison.Ordinal) ||
            !string.Equals(payload.CorrelationMarker, marker, StringComparison.Ordinal) ||
            query.Cursor != payload.Cursor)
        {
            throw new KernelContractException("action_discovery_checkpoint_invalid", "Action discovery operation, marker, or cursor does not match its signed checkpoint.");
        }

        var baseline = payload.AcceptedCommandIds.ToHashSet(StringComparer.Ordinal);
        var discovered = ReadAcceptedReadAloudActions(operationId)
            .Where(item => !baseline.Contains(item.CommandId))
            .ToArray();
        if (discovered.Length > 1)
            throw new KernelContractException("action_discovery_ambiguous", "More than one new action was accepted on the read-to-Anki operation after the checkpoint.");

        var accepted = discovered.SingleOrDefault();
        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            _eventStore.Cursor,
            StrictJson.CloneElement(new
            {
                correlationMarker = marker,
                status = accepted is null ? "pending" : "accepted",
                operationId = accepted?.OperationId,
                commandId = accepted?.CommandId,
                acceptedSequence = accepted?.AcceptedSequence
            }));
    }

    private IReadOnlyList<AcceptedReadAloudOperation> ReadAcceptedReadAloudActions(string operationId)
        => _eventStore.ReadStream(operationId)
            .Where(item => item.EventType == "command.accepted" &&
                           item.Operation == ReadAloudCardsOperation.Id &&
                           !string.IsNullOrWhiteSpace(item.CorrelationId))
            .Select(item => new AcceptedReadAloudOperation(operationId, item.CorrelationId, item.Sequence))
            .OrderBy(item => item.AcceptedSequence)
            .ToArray();

    private static string GetDiscoveryMarker(QueryEnvelope query)
    {
        var marker = GetRequiredParameter(query, "correlationMarker");
        if (marker.Length > 128 || marker.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')))
        {
            throw new KernelContractException("query_parameters_invalid", "Operation discovery correlationMarker is not a kernel identifier.");
        }
        return marker;
    }

    private string ProtectDiscoveryToken(OperationDiscoveryTokenPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, KernelJson.Options);
        var signature = HMACSHA256.HashData(_operationDiscoveryKey, bytes);
        return Base64UrlEncode(bytes) + "." + Base64UrlEncode(signature);
    }

    private OperationDiscoveryTokenPayload UnprotectDiscoveryToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = Base64UrlDecode(parts[0]);
            var signature = Base64UrlDecode(parts[1]);
            var expected = HMACSHA256.HashData(_operationDiscoveryKey, bytes);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) throw new CryptographicException();
            var payload = JsonSerializer.Deserialize<OperationDiscoveryTokenPayload>(bytes, KernelJson.Options)
                ?? throw new FormatException();
            if (payload.Schema != "kc.read-aloud-cards-discovery-token/1" || payload.Cursor < 0 ||
                payload.AcceptedCommandIds.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
            return payload;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            throw new KernelContractException("operation_discovery_checkpoint_invalid", "Operation discovery checkpoint is invalid or was issued by another Core process.");
        }
    }

    private string ProtectActionDiscoveryToken(ActionDiscoveryTokenPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, KernelJson.Options);
        var signature = HMACSHA256.HashData(_operationDiscoveryKey, bytes);
        return Base64UrlEncode(bytes) + "." + Base64UrlEncode(signature);
    }

    private ActionDiscoveryTokenPayload UnprotectActionDiscoveryToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = Base64UrlDecode(parts[0]);
            var signature = Base64UrlDecode(parts[1]);
            var expected = HMACSHA256.HashData(_operationDiscoveryKey, bytes);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) throw new CryptographicException();
            var payload = JsonSerializer.Deserialize<ActionDiscoveryTokenPayload>(bytes, KernelJson.Options)
                ?? throw new FormatException();
            if (payload.Schema != "kc.read-aloud-cards-action-discovery-token/1" ||
                payload.Cursor < 0 || string.IsNullOrWhiteSpace(payload.OperationId) ||
                string.IsNullOrWhiteSpace(payload.CorrelationMarker) ||
                payload.AcceptedCommandIds.Count == 0 || payload.AcceptedCommandIds.Any(string.IsNullOrWhiteSpace))
                throw new FormatException();
            return payload;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            throw new KernelContractException("action_discovery_checkpoint_invalid", "Action discovery checkpoint is invalid or was issued by another Core process.");
        }
    }

    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private async Task<JsonElement> BuildReadAloudSettingsEvidenceAsync(KernelEvent terminal, CancellationToken cancellationToken)
    {
        var snapshots = terminal.Artifacts.Where(item => item.Kind == "task-settings-snapshot").ToArray();
        if (snapshots.Length == 0)
            throw new KernelContractException("settings_evidence_missing", "Read-to-Anki terminal lacks its task settings snapshot.");
        if (snapshots.Length != 1)
            throw new KernelContractException("settings_evidence_ambiguous", "Read-to-Anki terminal has ambiguous task settings snapshots.");
        var snapshotReference = snapshots[0];
        var configurationReference = terminal.Artifacts.SingleOrDefault(IsAnkiConfiguration)
            ?? throw new KernelContractException("settings_evidence_missing", "Read-to-Anki terminal lacks its Anki configuration artifact.");
        if (terminal.Artifacts.Count(IsAnkiConfiguration) != 1)
            throw new KernelContractException("settings_evidence_ambiguous", "Read-to-Anki terminal has ambiguous Anki configuration evidence.");

        try
        {
            var snapshot = await _artifactStore.OpenVerifiedAsync(snapshotReference, "task-settings-snapshot", "1.0.0", cancellationToken).ConfigureAwait(false);
            var configuration = await _artifactStore.OpenVerifiedAsync(configurationReference, configurationReference.Kind, configurationReference.Contract, cancellationToken).ConfigureAwait(false);
            if (snapshot.Envelope.Provenance.Inputs.Count != 1 || snapshot.Envelope.Provenance.Inputs[0] != configurationReference)
                throw new InvalidDataException("Task settings snapshot provenance is not rooted in the Anki configuration artifact.");
            using var snapshotDocument = StrictJson.Parse(snapshot.Primary.Content, "task settings snapshot");
            using var configurationDocument = StrictJson.Parse(configuration.Primary.Content, "Anki configuration");
            var value = snapshotDocument.RootElement;
            var config = configurationDocument.RootElement;
            if (GetRequiredString(value, "schema") != "kc.read-to-anki-task-settings-snapshot/1")
                throw new InvalidDataException("Task settings snapshot schema is invalid.");
            var destination = value.GetProperty("destination");
            var anki = value.GetProperty("ankiConfiguration");
            var routeKey = GetRequiredString(destination, "routeKey");
            var deckIdentity = GetRequiredString(destination, "deckIdentity");
            var deckSha256 = GetRequiredString(destination, "deckSha256");
            var configuredDeck = GetRequiredString(config.GetProperty("deck_routes"), routeKey);
            if (routeKey != "canary" || !IsCanaryDeck(configuredDeck) || deckIdentity != configuredDeck || deckSha256 != Sha256(configuredDeck) ||
                GetRequiredString(anki, "artifactId") != configurationReference.ArtifactId ||
                GetRequiredString(anki, "manifestSha256") != configurationReference.ManifestDigest.Value)
                throw new InvalidDataException("Task settings snapshot does not match its SHA-bound destination configuration.");

            return StrictJson.CloneElement(new
            {
                settingsSnapshot = ToEvidence(snapshotReference),
                ankiConfiguration = ToEvidence(configurationReference),
                destination = new { routeKey, deckIdentity, deckSha256 },
                packageProvenance = new
                {
                    producer = snapshot.Envelope.Provenance.Producer,
                    inputArtifactId = configurationReference.ArtifactId,
                    inputManifestSha256 = configurationReference.ManifestDigest.Value
                }
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            throw new KernelContractException("settings_evidence_invalid", "Read-to-Anki settings evidence is missing, tampered, or inconsistent.");
        }
    }

    private async Task<JsonElement> BuildReadAloudContextEvidenceAsync(KernelEvent terminal, CancellationToken cancellationToken)
    {
        var snapshots = terminal.Artifacts.Where(item => item.Kind == "task-context-snapshot").ToArray();
        if (snapshots.Length != 1)
            throw new KernelContractException(snapshots.Length == 0 ? "context_evidence_missing" : "context_evidence_ambiguous",
                "Read-to-Anki terminal must contain exactly one task context snapshot.");
        var captures = terminal.Artifacts.Where(item => item.Kind == "capture-text").ToArray();
        if (captures.Length != 1)
            throw new KernelContractException(captures.Length == 0 ? "context_evidence_missing" : "context_evidence_ambiguous",
                "Read-to-Anki terminal must contain exactly one capture artifact for context evidence.");

        try
        {
            var snapshotReference = snapshots[0];
            var captureReference = captures[0];
            var snapshot = await _artifactStore.OpenVerifiedAsync(snapshotReference, "task-context-snapshot", "1.0.0", cancellationToken).ConfigureAwait(false);
            using var document = StrictJson.Parse(snapshot.Primary.Content, "task context snapshot");
            var value = document.RootElement;
            if (GetRequiredString(value, "schema") != "kc.read-to-anki-context-snapshot/1" ||
                GetRequiredString(value, "sourceOperationId") != terminal.StreamId)
                throw new InvalidDataException("Task context snapshot identity is invalid.");

            var capture = value.GetProperty("capture");
            if (GetRequiredString(capture, "artifactId") != captureReference.ArtifactId ||
                GetRequiredString(capture, "manifestSha256") != captureReference.ManifestDigest.Value)
                throw new InvalidDataException("Task context snapshot capture binding is invalid.");

            var contextRequested = value.GetProperty("contextRequested").GetBoolean();
            var contextApplied = value.GetProperty("contextApplied").GetBoolean();
            var contextStatus = GetRequiredString(value, "contextStatus");
            var contextGranularity = GetRequiredString(value, "contextGranularity");
            var spokenTextSha256 = GetRequiredString(value, "spokenTextSha256");
            var speechContextMode = GetRequiredString(value, "speechContextMode");
            if (contextStatus is not ("off" or "applied" or "unavailable") ||
                contextApplied != (contextRequested && contextStatus == "applied") ||
                spokenTextSha256.Length != 64 || !spokenTextSha256.All(Uri.IsHexDigit) ||
                speechContextMode != (contextApplied ? "selection_plus_" + contextGranularity : "selection_only"))
                throw new InvalidDataException("Task context snapshot state is inconsistent.");

            var segments = value.GetProperty("contextSegments");
            if (segments.ValueKind != JsonValueKind.Array || snapshot.Envelope.Provenance.Inputs.Count != 1 + segments.GetArrayLength() ||
                snapshot.Envelope.Provenance.Inputs[0] != captureReference)
                throw new InvalidDataException("Task context snapshot provenance is incomplete.");
            var projectedSegments = new List<object>();
            var index = 0;
            var suffixStarted = false;
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.GetProperty("order").GetInt32() != index)
                    throw new InvalidDataException("Task context segment order is invalid.");
                var role = GetRequiredString(segment, "role");
                if (role == "suffix") suffixStarted = true;
                if (role is not ("prefix" or "suffix") || suffixStarted && role != "suffix")
                    throw new InvalidDataException("Task context segment role ordering is invalid.");
                var reference = snapshot.Envelope.Provenance.Inputs[index + 1];
                if (reference.Kind != "question-context" ||
                    GetRequiredString(segment, "artifactId") != reference.ArtifactId ||
                    GetRequiredString(segment, "manifestSha256") != reference.ManifestDigest.Value ||
                    GetRequiredString(segment, "sha256").Length != 64)
                    throw new InvalidDataException("Task context segment binding is invalid.");
                projectedSegments.Add(new { order = index, role, artifact = ToEvidence(reference) });
                index++;
            }
            if (contextApplied != (projectedSegments.Count > 0 && contextRequested))
                throw new InvalidDataException("Task context application state does not match its ordered inputs.");

            return StrictJson.CloneElement(new
            {
                contextSnapshot = ToEvidence(snapshotReference),
                capture = ToEvidence(captureReference),
                selectedTextSha256 = GetRequiredString(value, "selectedTextSha256"),
                targetTextSha256 = GetRequiredString(value, "targetTextSha256"),
                spokenTextSha256,
                speechContextMode,
                contextRequested,
                contextApplied,
                contextStatus,
                contextGranularity,
                contextSegments = projectedSegments
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            throw new KernelContractException("context_evidence_invalid", "Read-to-Anki context evidence is missing, tampered, or inconsistent.");
        }
    }

    private static object ToEvidence(ArtifactReference reference) => new
    {
        artifactId = reference.ArtifactId,
        kind = reference.Kind,
        manifestPath = $"artifacts/{reference.ArtifactId}/artifact.json",
        sha256 = reference.ManifestDigest.Value
    };

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsCanaryDeck(string value) => value == "零碎知识剪藏::Canary" || value.StartsWith("零碎知识剪藏::Canary::", StringComparison.Ordinal);

    private async Task<QueryResult> BuildQuestionProjectionAsync(QueryEnvelope query, CancellationToken cancellationToken)
    {
        var operationId = GetRequiredParameter(query, "operationId");
        var events = _eventStore.ReadStream(operationId);
        if (query.Cursor.HasValue && events.LastOrDefault()?.Sequence <= query.Cursor.Value && !events.Any(IsTerminal))
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                events = _eventStore.ReadStream(operationId);
                if (events.LastOrDefault()?.Sequence > query.Cursor.Value || events.Any(IsTerminal))
                {
                    break;
                }
            }
        }
        var terminal = events.LastOrDefault(IsTerminal);
        var state = terminal?.EventType switch
        {
            "operation.completed" when CompletedResultIsWaitingInput(terminal) => "WaitingInput",
            "operation.completed" when CompletedResultIsCancelled(terminal) => "Cancelled",
            "operation.completed" => "Succeeded",
            "operation.failed" => "Failed",
            "operation.cancelled" => "Cancelled",
            "operation.unknown-external-state" => "Degraded",
            _ when events.Any(item => item.EventType is "command.accepted" or "operation.started") => "Running",
            _ => "Degraded"
        };
        var destinationSource = events.LastOrDefault(x => x.EventType == "operation.completed" && x.Artifacts.Any(a => a.Kind == "destination-snapshot"));
        var destinations = await ProjectDestinationsAsync(operationId, destinationSource, cancellationToken).ConfigureAwait(false);
        var active = events.LastOrDefault(x => x.EventType == "command.accepted");
        if (active is not null && (terminal is null || active.Sequence > terminal.Sequence)) state = "Running";
        else if (destinations?.Any(x => x.State is "failed" or "unknown") == true) state = "Degraded";
        var notice = terminal is null
            ? (events.Count == 0 ? "Question operation was not found." : null)
            : terminal.EventType == "operation.completed"
                ? null
                : terminal.Data.TryGetProperty("message", out var message) ? message.GetString() : "Question operation reached a non-success terminal state.";
        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            events.LastOrDefault()?.Sequence ?? 0,
            StrictJson.CloneElement(new
            {
                operationId,
                state,
                notice,
                destinations,
                terminalCommandId = terminal?.CorrelationId,
                lifecycle = events.Select(item => item.EventType).ToArray(),
                result = terminal?.EventType == "operation.completed" ? terminal.Data.GetProperty("result") : (JsonElement?)null,
                cardDraft = terminal?.EventType == "operation.completed" && terminal.Data.GetProperty("result").TryGetProperty("cardDraft", out var questionDraft) ? questionDraft : (JsonElement?)null,
                semanticReadback = terminal?.EventType == "operation.completed" && terminal.Data.GetProperty("result").TryGetProperty("semanticReadback", out var questionReadback) ? questionReadback : (JsonElement?)null,
                outputs = BuildQuestionOutputs(terminal?.EventType == "operation.completed" ? terminal : destinationSource ?? terminal)
            }));
    }

    private static JsonElement BuildOperationOutputs(KernelEvent? terminal)
    {
        var status = GetExactlyOneArtifact(terminal, "anki-connection-status", required: terminal?.Operation == "anki.probe" && terminal.EventType == "operation.completed");
        var readAloud = terminal?.Operation == "clip.read-aloud-cards" && terminal.EventType == "operation.completed";
        var completedReadAloudExport = readAloud && terminal!.Data.TryGetProperty("result", out var readAloudResult) &&
            readAloudResult.TryGetProperty("pipeline", out var readAloudPipeline) && readAloudPipeline.GetString() == "completed";
        var cardReadyReadAloud = readAloud && terminal!.Data.TryGetProperty("result", out var cardReadyResult) &&
            cardReadyResult.TryGetProperty("pipeline", out var cardReadyPipeline) && cardReadyPipeline.GetString() is "awaiting_card_confirmation" or "completed";
        var completedSpeech = readAloud && terminal!.Data.TryGetProperty("result", out var speechResult) &&
            speechResult.TryGetProperty("playback", out var playbackState) && playbackState.GetString() == "idle_replayable";
        var capture = GetExactlyOneArtifact(terminal, "capture-text", required: readAloud);
        var contextSnapshot = GetExactlyOneArtifact(terminal, "task-context-snapshot", required: readAloud);
        var review = GetExactlyOneArtifact(terminal, "content-review", required: cardReadyReadAloud);
        var synthesis = GetExactlyOneArtifact(terminal, "speech-synthesis", required: completedSpeech);
        var playback = GetExactlyOneArtifact(terminal, "speech-playback", required: completedSpeech);
        var cards = GetExactlyOneArtifact(terminal, "card-bundle", required: cardReadyReadAloud);
        var anki = GetExactlyOneArtifact(terminal, "anki-readback", required: completedReadAloudExport && !terminal!.Artifacts.Any(x => x.Kind == "destination-snapshot"));
        return StrictJson.CloneElement(new
        {
            ankiConnectionStatusArtifact = status,
            remNoteDiscoveryArtifact = GetExactlyOneArtifact(terminal, "remnote-discovery", required: false),
            remNoteProbeArtifact = GetExactlyOneArtifact(terminal, "remnote-probe", required: false),
            destinationSnapshotArtifact = GetExactlyOneArtifact(terminal, "destination-snapshot", required: false),
            captureArtifact = capture,
            contextSnapshotArtifact = contextSnapshot,
            reviewArtifact = review,
            synthesisArtifact = synthesis,
            playbackArtifact = playback,
            cardBundleArtifact = cards,
            ankiReadbackArtifact = anki
        });
    }

    private static JsonElement BuildQuestionOutputs(KernelEvent? terminal)
    {
        var completed = terminal?.EventType == "operation.completed";
        var answerRequired = completed && terminal!.Operation is "question.initial" or "question.follow-up";
        var cardsRequired = completed && terminal!.Operation == "question.make-cards";
        var destinationRequired = cardsRequired && terminal!.Data.GetProperty("result").TryGetProperty("pipeline", out var pipeline) && pipeline.GetString() == "completed";
        var answer = GetExactlyOneArtifact(terminal, "question-answer", answerRequired);
        var cardBundle = GetExactlyOneArtifact(terminal, "question-card-bundle", cardsRequired);
        var destinationEvidence = GetExactlyOneArtifact(terminal, "anki-export-evidence", destinationRequired && !terminal!.Artifacts.Any(x => x.Kind == "destination-snapshot"));
        return StrictJson.CloneElement(new
        {
            answerArtifact = answer,
            cardBundleArtifact = cardBundle,
            destinationEvidenceArtifact = destinationEvidence
            , destinationSnapshotArtifact = GetExactlyOneArtifact(terminal, "destination-snapshot", required: false)
        });
    }

    private static ArtifactReference? GetExactlyOneArtifact(KernelEvent? terminal, string kind, bool required)
    {
        var matches = terminal?.Artifacts.Where(item => item.Kind == kind).ToArray() ?? Array.Empty<ArtifactReference>();
        if (matches.Length > 1 || required && matches.Length != 1)
        {
            throw new KernelContractException("projection_artifact_cardinality_invalid", $"Projection requires exactly one '{kind}' artifact.");
        }
        return matches.SingleOrDefault();
    }

    private QueryResult BuildTaskProjection(QueryEnvelope query)
    {
        var taskId = GetRequiredParameter(query, "taskId");
        var events = _eventStore.ReadStream(taskId);
        var terminal = events.LastOrDefault(IsTerminal);
        var state = terminal?.EventType switch
        {
            "operation.completed" => "Succeeded",
            "operation.failed" => "Failed",
            "operation.cancelled" => "Cancelled",
            "operation.unknown-external-state" => "Degraded",
            _ when events.Count > 0 => "Running",
            _ => "Degraded"
        };
        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            events.LastOrDefault()?.Sequence ?? 0,
            StrictJson.CloneElement(new
            {
                taskId,
                state,
                notice = events.Count == 0 ? "Task was not found." : null,
                lifecycle = events.Select(item => item.EventType).ToArray()
            }));
    }

    private async Task<QueryResult> BuildArtifactReadAsync(QueryEnvelope query, CancellationToken cancellationToken)
    {
        if (query.Parameters.ValueKind != JsonValueKind.Object ||
            !query.Parameters.TryGetProperty("reference", out var referenceElement))
        {
            throw new KernelContractException("query_parameters_invalid", "artifact.read requires an artifact reference.");
        }

        var reference = referenceElement.Deserialize<ArtifactReference>(KernelJson.Options)
            ?? throw new KernelContractException("query_parameters_invalid", "artifact.read reference is invalid.");
        var artifact = await _artifactStore.OpenVerifiedAsync(reference, reference.Kind, reference.Contract, cancellationToken).ConfigureAwait(false);
        var representationId = query.Parameters.TryGetProperty("representationId", out var representationElement) && representationElement.ValueKind == JsonValueKind.String
            ? representationElement.GetString()!
            : artifact.Envelope.PrimaryRepresentation;
        if (!artifact.Representations.TryGetValue(representationId, out var representation))
        {
            throw new KernelContractException("artifact_representation_not_found", $"Artifact representation '{representationId}' was not found.");
        }

        return new QueryResult(
            KernelSchemas.QueryResult,
            query.QueryId,
            query.Projection,
            ProjectionVersion: 1,
            _eventStore.Cursor,
            StrictJson.CloneElement(new
            {
                artifactId = artifact.Envelope.ArtifactId,
                kind = artifact.Envelope.Kind,
                contract = artifact.Envelope.Contract,
                representationId,
                mediaType = representation.Descriptor.MediaType,
                schema = representation.Descriptor.Schema,
                bytes = representation.Descriptor.Bytes,
                contentBase64 = Convert.ToBase64String(representation.Content)
            }));
    }

    private static string GetRequiredParameter(QueryEnvelope query, string name)
    {
        if (query.Parameters.ValueKind != JsonValueKind.Object ||
            !query.Parameters.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new KernelContractException("query_parameters_invalid", $"{query.Projection} requires a string {name}.");
        }

        return value.GetString()!;
    }

    private static string GetRequiredString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
           property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"Required string '{name}' is missing.");

    private static bool IsAnkiConfiguration(ArtifactReference artifact)
        => artifact.Kind is "anki-configuration" or "anki-export-config";

    private static bool CompletedResultIsWaitingInput(KernelEvent terminal)
    {
        if (!terminal.Data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return status.GetString() is "waiting_input" or "awaiting_card_confirmation";
    }

    private static bool CompletedResultIsCancelled(KernelEvent terminal)
        => terminal.Data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
           result.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
           status.GetString() == "cancelled";

    private async Task<KernelEvent> ExecuteWithTerminalGuaranteeAsync(
        CommandEnvelope command,
        CapabilityDescriptor capability,
        IOperationHandler handler,
        CancellationToken callerCancellation)
    {
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        Task<OperationExecutionResult> execution;
        try
        {
            execution = handler.ExecuteAsync(
                command,
                new OperationContext(capability, _workerRuntime, _artifactStore, _eventStore),
                handlerCancellation.Token);
        }
        catch (Exception ex)
        {
            return FailureEvent(command, capability, "handler_start_failed", ex.Message);
        }

        var timeout = Task.Delay(TimeSpan.FromMilliseconds(capability.TimeoutMilliseconds));
        var callerCancelled = Task.Delay(Timeout.InfiniteTimeSpan, callerCancellation);
        var winner = await Task.WhenAny(execution, timeout, callerCancelled).ConfigureAwait(false);
        if (winner == execution)
        {
            try
            {
                var result = await execution.ConfigureAwait(false);
                if (capability.EffectClass == "none" && result.ExternalWriteCount != 0)
                {
                    return FailureEvent(command, capability, "effect_policy_violated", "An effect-free operation reported external writes.");
                }

                return NewEvent(
                    command,
                    "operation.completed",
                    capability,
                    DateTimeOffset.UtcNow,
                    StrictJson.CloneElement(new
                    {
                        result = result.Data,
                        externalWriteCount = result.ExternalWriteCountKnown ? (int?)result.ExternalWriteCount : null,
                        result.ExternalStateKnown
                    }),
                    result.Artifacts);
            }
            catch (OperationCanceledException)
            {
                return CancellationEvent(command, capability, callerCancellation.IsCancellationRequested ? "caller_cancelled" : "handler_cancelled");
            }
            catch (WorkerExternalStateUnknownException ex)
            {
                return NewEvent(
                    command,
                    "operation.unknown-external-state",
                    capability,
                    DateTimeOffset.UtcNow,
                    StrictJson.CloneElement(new { code = ex.Code, message = ex.Message, retryable = false }));
            }
            catch (Exception ex)
            {
                return FailureEvent(command, capability, "operation_failed", ex.Message);
            }
        }

        handlerCancellation.Cancel();
        _ = ObserveLateCompletionAsync(execution);
        if (winner == callerCancelled)
        {
            return CancellationEvent(command, capability, "caller_cancelled");
        }

        return capability.EffectClass == "none"
            ? FailureEvent(command, capability, "operation_timed_out", "The operation exceeded its admitted timeout.")
            : NewEvent(
                command,
                "operation.unknown-external-state",
                capability,
                DateTimeOffset.UtcNow,
                StrictJson.CloneElement(new { code = "operation_timed_out", message = "The operation timed out after external dispatch; completion state is unknown." }));
    }

    private static async Task ObserveLateCompletionAsync(Task<OperationExecutionResult> execution)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static KernelEvent CancellationEvent(CommandEnvelope command, CapabilityDescriptor capability, string code)
    {
        if (capability.EffectClass == "external-write")
        {
            return NewEvent(
                command,
                "operation.unknown-external-state",
                capability,
                DateTimeOffset.UtcNow,
                StrictJson.CloneElement(new { code, message = "Cancellation occurred after a potentially mutating operation started; external state is unknown." }));
        }

        return NewEvent(
            command,
            "operation.cancelled",
            capability,
            DateTimeOffset.UtcNow,
            StrictJson.CloneElement(new { code, message = "The operation was cancelled and reached a durable terminal state." }));
    }

    private static KernelEvent FailureEvent(CommandEnvelope command, CapabilityDescriptor capability, string code, string message)
    {
        return NewEvent(
            command,
            "operation.failed",
            capability,
            DateTimeOffset.UtcNow,
            StrictJson.CloneElement(new { code, message }));
    }

    private async Task<CommandReceipt> WaitForConcurrentReceiptAsync(
        string idempotencyKey,
        CapabilityDescriptor capability,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1000, capability.TimeoutMilliseconds * 2L));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = _commandIndex.TryReadReceipt(idempotencyKey);
            if (receipt is not null)
            {
                return receipt;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        throw new KernelContractException("command_in_progress", "A command with this idempotency key is still in progress; no second execution was started.");
    }

    private CommandReceipt? RecoverInterruptedCommand(CommandEnvelope command, CapabilityDescriptor capability)
    {
        var events = _eventStore.ReadStream(command.OperationId);
        if (!events.Any(x => x.EventType == "command.accepted" && x.CorrelationId == command.CommandId))
        {
            var accepted = NewEvent(
                command,
                "command.accepted",
                capability,
                DateTimeOffset.UtcNow,
                StrictJson.CloneElement(new { command.CommandId, command.IdempotencyKey, recovered = true }));
            var recoveryTerminal = FailureEvent(
                command,
                capability,
                "crash_after_claim",
                "Core found a durable command claim without dispatch evidence; the command was not re-executed.");
            var recoveryAppend = _eventStore.Append(command.OperationId, events.LastOrDefault()?.Sequence ?? 0, new[] { accepted, recoveryTerminal });
            return BuildReceipt(command, recoveryAppend);
        }

        if (TerminalForCommand(events, command.CommandId) is not null)
        {
            return BuildReceipt(command, events);
        }

        var terminal = capability.EffectClass == "none"
            ? FailureEvent(command, capability, "process_interrupted", "The previous Core process stopped before recording completion.")
            : NewEvent(
                command,
                "operation.unknown-external-state",
                capability,
                DateTimeOffset.UtcNow,
                StrictJson.CloneElement(new { code = "process_interrupted", message = "The previous Core process stopped after external dispatch; external state is unknown." }));
        var appended = _eventStore.Append(command.OperationId, events[^1].Sequence, new[] { terminal });
        ReconcileOrphanQuestionSession(new CommandClaim(
            command.IdempotencyKey,
            command.CommandId,
            command.OperationId,
            command.Operation,
            CommandFingerprint.Compute(command),
            capability.EffectClass,
            "recovery",
            Environment.ProcessId,
            DateTimeOffset.UtcNow), terminal.EventType);
        return BuildReceipt(command, events.Concat(appended).ToArray());
    }

    private void ReconcileOrphanQuestionSession(CommandClaim claim, string terminalEventType)
    {
        if (!claim.Operation.StartsWith("question.", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var pair in _eventStore.ReadAllStreams().Where(item => item.Key.StartsWith("question-session-", StringComparison.Ordinal)))
        {
            var events = pair.Value;
            var last = events.LastOrDefault();
            if (last is null ||
                last.EventType is not ("question.session-command-claimed" or "question.worker-dispatch-started") ||
                last.CorrelationId != claim.FirstCommandId)
            {
                continue;
            }

            var safeToRetry = claim.EffectClass != "external-write" || !events.Any(item => item.EventType == "question.worker-dispatch-started");
            var eventType = safeToRetry ? "question.session-command-aborted" : "question.session-unknown-external-state";
            var recovery = new KernelEvent(
                KernelSchemas.Event,
                $"evt-{Guid.NewGuid():N}",
                pair.Key,
                Sequence: 0,
                eventType,
                EventVersion: 1,
                DateTimeOffset.UtcNow,
                claim.FirstCommandId,
                claim.FirstCommandId,
                claim.Operation,
                claim.EffectClass,
                StrictJson.CloneElement(new { claim.OperationId, terminalEventType, safeToRetry, code = "orphan_session_claim_recovered" }),
                Array.Empty<ArtifactReference>());
            try
            {
                _eventStore.Append(pair.Key, events.Count, new[] { recovery });
            }
            catch (EventStoreConcurrencyException)
            {
            }
        }
    }

    private CommandReceipt BuildReceipt(CommandEnvelope command, IReadOnlyList<KernelEvent> events)
        => BuildReceipt(command.CommandId, command.OperationId, events);

    private CommandReceipt BuildReceipt(string commandId, string operationId, IReadOnlyList<KernelEvent> events)
    {
        var terminal = TerminalForCommand(events, commandId);
        var error = terminal is null || terminal.EventType == "operation.completed"
            ? null
            : new KernelError(
                terminal.Data.TryGetProperty("code", out var code) ? code.GetString() ?? "operation_terminal" : "operation_terminal",
                terminal.Data.TryGetProperty("message", out var message) ? message.GetString() ?? "Operation reached a non-success terminal state." : "Operation reached a non-success terminal state.",
                Retryable: false);
        return new CommandReceipt(
            KernelSchemas.CommandReceipt,
            commandId,
            operationId,
            Accepted: true,
            _eventStore.Cursor,
            events,
            error);
    }

    private static KernelEvent NewRecoveryAccepted(CommandClaim claim)
    {
        return new KernelEvent(
            KernelSchemas.Event,
            $"evt-{Guid.NewGuid():N}",
            claim.OperationId,
            Sequence: 0,
            "command.accepted",
            EventVersion: 1,
            DateTimeOffset.UtcNow,
            claim.FirstCommandId,
            claim.FirstCommandId,
            claim.Operation,
            claim.EffectClass,
            StrictJson.CloneElement(new { claim.FirstCommandId, claim.IdempotencyKey, recovered = true }),
            Array.Empty<ArtifactReference>());
    }

    private static KernelEvent NewRecoveryTerminal(CommandClaim claim, string code, bool externalStateMayBeUnknown)
    {
        var eventType = externalStateMayBeUnknown ? "operation.unknown-external-state" : "operation.failed";
        var message = externalStateMayBeUnknown
            ? "Core restarted without a durable terminal event after a potentially mutating command; external state is unknown."
            : "Core restarted without a durable terminal event; the interrupted command was not re-executed.";
        return new KernelEvent(
            KernelSchemas.Event,
            $"evt-{Guid.NewGuid():N}",
            claim.OperationId,
            Sequence: 0,
            eventType,
            EventVersion: 1,
            DateTimeOffset.UtcNow,
            claim.FirstCommandId,
            claim.FirstCommandId,
            claim.Operation,
            claim.EffectClass,
            StrictJson.CloneElement(new { code, message }),
            Array.Empty<ArtifactReference>());
    }

    private static KernelEvent? TerminalForCommand(IReadOnlyList<KernelEvent> events, string commandId)
    {
        var accepted = events.LastOrDefault(x => x.EventType == "command.accepted" && x.CorrelationId == commandId);
        return accepted is null ? null : events.LastOrDefault(x => IsTerminal(x) && x.Sequence > accepted.Sequence && x.CorrelationId == commandId);
    }

    private static bool IsTerminal(KernelEvent item)
    {
        return item.EventType is "operation.completed" or "operation.failed" or "operation.cancelled" or "operation.unknown-external-state";
    }

    private static KernelEvent NewEvent(
        CommandEnvelope command,
        string eventType,
        CapabilityDescriptor capability,
        DateTimeOffset occurredAt,
        JsonElement data,
        IReadOnlyList<ArtifactReference>? artifacts = null)
    {
        return new KernelEvent(
            KernelSchemas.Event,
            $"evt-{Guid.NewGuid():N}",
            command.OperationId,
            Sequence: 0,
            eventType,
            EventVersion: 1,
            occurredAt,
            CorrelationId: command.CommandId,
            CausationId: command.CommandId,
            command.Operation,
            capability.EffectClass,
            data,
            artifacts ?? Array.Empty<ArtifactReference>());
    }

    private static void ValidateCommand(CommandEnvelope command)
    {
        if (command.Schema != KernelSchemas.Command)
        {
            throw new KernelContractException("command_schema_unsupported", $"Unsupported command schema '{command.Schema}'.");
        }

        RequireToken(command.CommandId, "commandId");
        RequireToken(command.OperationId, "operationId");
        RequireToken(command.Operation, "operation");
        if (command.IdempotencyKey.Length != 64 || !command.IdempotencyKey.All(Uri.IsHexDigit))
        {
            throw new KernelContractException("idempotency_key_invalid", "idempotencyKey must contain exactly 64 hexadecimal characters.");
        }
    }

    private static void ValidatePolicies(CommandEnvelope command, CapabilityDescriptor capability)
    {
        if (CardDestinations.IsNeutral(command) && command.Operation is "clip.read-aloud-cards" or "question.initial" or "question.follow-up" or "question.make-cards")
        {
            // This closed additive route validates durable ownership/tokens in
            // its action handlers and per-target grants at confirmation. It is
            // NOT an exemption for arbitrary effect-free callers or old SDKs.
            if (command.Authorization is not null) throw new KernelContractException("authorization_not_applicable", "Neutral operations do not accept Anki envelope authority.");
            return;
        }
        var zeroEffectDraftAction = command.Body.Data.ValueKind == JsonValueKind.Object &&
            command.Body.Data.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String &&
            action.GetString() is "cancel_card_draft";
        if (capability.AuthorizationPolicy == "explicit" && command.Authorization is null && !zeroEffectDraftAction)
        {
            throw new KernelContractException("authorization_required", $"Operation '{command.Operation}' requires explicit authorization.");
        }

        if (capability.AuthorizationPolicy == "none" && command.Authorization is not null)
        {
            throw new KernelContractException("authorization_not_applicable", $"Operation '{command.Operation}' does not accept authorization.");
        }
    }

    private static void RequireToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            throw new KernelContractException("identifier_invalid", $"{name} is not a valid kernel identifier.");
        }
    }

    private sealed record OperationDiscoveryTokenPayload(
        string Schema,
        string CorrelationMarker,
        long Cursor,
        IReadOnlyList<string> AcceptedCommandIds);

    private sealed record ActionDiscoveryTokenPayload(
        string Schema,
        string OperationId,
        string CorrelationMarker,
        long Cursor,
        IReadOnlyList<string> AcceptedCommandIds);

    private sealed record AcceptedReadAloudOperation(string OperationId, string CommandId, long AcceptedSequence);
}

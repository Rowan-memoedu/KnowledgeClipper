using System.Collections.Concurrent;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.Application;

internal sealed class KernelApplication
{
    private readonly AdmittedCapabilityCatalog _catalog;
    private readonly OperationRegistry _operations;
    private readonly IWorkerRuntime _workerRuntime;
    private readonly IEventStore _eventStore;
    private readonly ICommandIndex _commandIndex;
    private readonly FileArtifactStore _artifactStore;
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
            if (_commandIndex.TryReadReceipt(claim.IdempotencyKey) is not null)
            {
                continue;
            }

            var events = _eventStore.ReadStream(claim.OperationId);
            if (!events.Any(IsTerminal))
            {
                var recoveryEvents = events.Count == 0
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
                    if (!events.Any(IsTerminal))
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
            _eventStore.Append(command.OperationId, started[^1].Sequence, new[] { terminal });
        }
        catch (Exception ex)
        {
            var events = _eventStore.ReadStream(command.OperationId);
            if (!events.Any(IsTerminal))
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
            if (events.Any(IsTerminal))
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
            "operation.projection" => BuildOperationProjection(query),
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

    private QueryResult BuildOperationProjection(QueryEnvelope query)
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
        var state = terminal?.EventType switch
        {
            "operation.completed" => "Succeeded",
            "operation.failed" => "Failed",
            "operation.cancelled" => "Cancelled",
            "operation.unknown-external-state" => "Degraded",
            _ when events.Count > 0 => "Running",
            _ => "Degraded"
        };
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
                lifecycle = events.Select(item => item.EventType).ToArray(),
                result = terminal?.EventType == "operation.completed" ? terminal.Data.GetProperty("result") : (JsonElement?)null,
                outputs = BuildOperationOutputs(terminal)
            }));
    }

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
            "operation.completed" => "Succeeded",
            "operation.failed" => "Failed",
            "operation.cancelled" => "Cancelled",
            "operation.unknown-external-state" => "Degraded",
            _ when events.Any(item => item.EventType is "command.accepted" or "operation.started") => "Running",
            _ => "Degraded"
        };
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
                lifecycle = events.Select(item => item.EventType).ToArray(),
                result = terminal?.EventType == "operation.completed" ? terminal.Data.GetProperty("result") : (JsonElement?)null,
                outputs = BuildQuestionOutputs(terminal)
            }));
    }

    private static JsonElement BuildOperationOutputs(KernelEvent? terminal)
    {
        var status = GetExactlyOneArtifact(terminal, "anki-connection-status", required: terminal?.Operation == "anki.probe" && terminal.EventType == "operation.completed");
        return StrictJson.CloneElement(new { ankiConnectionStatusArtifact = status });
    }

    private static JsonElement BuildQuestionOutputs(KernelEvent? terminal)
    {
        var completed = terminal?.EventType == "operation.completed";
        var answerRequired = completed && terminal!.Operation is "question.initial" or "question.follow-up";
        var cardsRequired = completed && terminal!.Operation == "question.make-cards";
        var answer = GetExactlyOneArtifact(terminal, "question-answer", answerRequired);
        var cardBundle = GetExactlyOneArtifact(terminal, "question-card-bundle", cardsRequired);
        var destinationEvidence = GetExactlyOneArtifact(terminal, "anki-export-evidence", cardsRequired);
        return StrictJson.CloneElement(new
        {
            answerArtifact = answer,
            cardBundleArtifact = cardBundle,
            destinationEvidenceArtifact = destinationEvidence
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

    private static bool CompletedResultIsWaitingInput(KernelEvent terminal)
    {
        if (!terminal.Data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return string.Equals(status.GetString(), "waiting_input", StringComparison.Ordinal);
    }

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
                        result.ExternalWriteCount,
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
        if (events.Count == 0)
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
            var recoveryAppend = _eventStore.Append(command.OperationId, 0, new[] { accepted, recoveryTerminal });
            return BuildReceipt(command, recoveryAppend);
        }

        if (events.Any(IsTerminal))
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
        var terminal = events.LastOrDefault(IsTerminal);
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
        if (capability.AuthorizationPolicy == "explicit" && command.Authorization is null)
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
}

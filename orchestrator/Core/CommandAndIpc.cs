using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed class CommandHandler
{
    private readonly string jobsRoot;
    private readonly Func<AcceptedCommand, CancellationToken, ValueTask>? acceptedCommand;
    private readonly StageControlHandler stageControl;
    private readonly ModuleActionHandler? moduleAction;

    public CommandHandler(string jobsRoot, Func<AcceptedCommand, CancellationToken, ValueTask>? acceptedCommand = null, ModuleActionHandler? moduleAction = null)
    {
        this.jobsRoot = Path.GetFullPath(jobsRoot);
        this.acceptedCommand = acceptedCommand;
        this.moduleAction = moduleAction;
        Directory.CreateDirectory(this.jobsRoot);
        stageControl = new StageControlHandler(this.jobsRoot);
    }

    public async Task<CommandReceipt> HandleAsync(TaskCommand command, CancellationToken cancellationToken)
    {
        var duplicate = FindAcceptedCommand(command.IdempotencyKey);
        if (duplicate is not null)
            return duplicate;

        string taskId;
        string jobRoot;
        if (command.Type == "task.submit")
        {
            var manifestPath = PathPolicy.ResolveJobRelative(jobsRoot, command.PayloadRef);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            taskId = manifest.RootElement.GetProperty("taskId").GetString() ?? throw new InvalidDataException("Task manifest has no taskId.");
            jobRoot = Path.GetDirectoryName(manifestPath) ?? throw new InvalidDataException("Task manifest has no directory.");
            var manifestName = Path.GetFileName(manifestPath);
            if (!manifestName.Equals("task.manifest.v1.json", StringComparison.OrdinalIgnoreCase) &&
                !manifestName.Equals("task.manifest.v2.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("task.submit payloadRef must target task.manifest.v2.json or the legacy task.manifest.v1.json.");
        }
        else
        {
            taskId = command.TaskId ?? throw new InvalidDataException($"{command.Type} requires taskId.");
            jobRoot = PathPolicy.ResolveJobRelative(jobsRoot, taskId);
            if (!Directory.Exists(jobRoot))
                throw new DirectoryNotFoundException($"Task does not exist: {taskId}");
        }

        var runId = ResolveRunId(jobRoot);
        var store = new EventStore(jobRoot, runId);
        var current = TaskStateMachine.Project(LatestRunEvents(ReadAllEvents(jobRoot)));
        if (command.ExpectedState is not null && command.ExpectedState != current)
            return Rejected(command, taskId, current, "contract.expected_state_mismatch", "contract", "Command expectedState does not match current state.");

        string? cancelReason = null;
        if (command.Type == "task.cancel")
        {
            var payloadPath = PathPolicy.ResolveJobRelative(jobRoot, command.PayloadRef);
            var payload = JsonSerializer.Deserialize<TaskCancelPayload>(await File.ReadAllTextAsync(payloadPath, cancellationToken), ContractJson.Options)
                ?? throw new InvalidDataException("task.cancel payload is empty.");
            if (payload.SchemaVersion != "1.0.0" || payload.TaskId != taskId || payload.OperationId != command.OperationId ||
                payload.Reason is not ("user_stop" or "superseded" or "task_cancel" or "timeout"))
                throw new InvalidDataException("task.cancel payload does not match command identity or reason contract.");
            cancelReason = payload.Reason;
            var cancelPath = Path.Combine(jobRoot, "runs", runId, "cancel-requested.json");
            if (!File.Exists(cancelPath))
                await AtomicFile.WriteJsonOnceAsync(
                    cancelPath,
                    new TaskCancelRequest("1.0.0", taskId, command.OperationId, payload.Reason, DateTimeOffset.UtcNow),
                    cancellationToken);
        }

        var eventRef = await store.AppendAsync(
            "command.accepted",
            taskId,
            new { command.OperationId, command.Type, status = current, command.PayloadRef, cancelReason },
            commandId: command.CommandId,
            idempotencyKey: command.IdempotencyKey,
            cancellationToken: cancellationToken);

        var receipt = new CommandReceipt("1.0.0", command.CommandId, true, taskId, command.OperationId, current, DateTimeOffset.UtcNow, null, eventRef);
        if (acceptedCommand is not null && command.Type is "task.submit" or "task.retry" or "task.provide_input")
            await acceptedCommand(new AcceptedCommand(command, taskId, jobRoot, runId, eventRef), cancellationToken);
        return receipt;
    }

    public object HandleQuery(JsonElement query)
    {
        var schemaVersion = query.GetProperty("schemaVersion").GetString();
        if (schemaVersion is not ("1.0.0" or "2.0.0" or "2.2.0" or "2.3.0" or "2.4.0"))
            throw new InvalidDataException($"Unsupported task query schemaVersion: {schemaVersion}");
        var queryId = query.GetProperty("queryId").GetString()!;
        var type = query.GetProperty("type").GetString()!;
        var items = BuildProjection();
        if (type == "task.get")
        {
            var taskId = query.GetProperty("taskId").GetString();
            items = items.Where(item => item.TaskId == taskId).ToArray();
        }
        else
        {
            if (query.TryGetProperty("statusFilter", out var filter) && filter.ValueKind == JsonValueKind.Array)
            {
                var allowed = filter.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).ToHashSet(StringComparer.Ordinal);
                items = items.Where(item => allowed.Contains(item.Status)).ToArray();
            }
            var limit = query.GetProperty("limit").GetInt32();
            items = items.OrderByDescending(item => item.UpdatedAt).Take(limit).ToArray();
        }
        if (schemaVersion is "2.0.0" or "2.2.0" or "2.3.0" or "2.4.0")
        {
            // Dictionary values deliberately retain nulls. The v2 projection contract
            // requires both decision fields even for ordinary waiting_input states.
            var v2Items = items.Select(item => new Dictionary<string, object?>
            {
                ["taskId"] = item.TaskId,
                ["operationId"] = item.OperationId,
                ["intent"] = item.Intent,
                ["status"] = item.Status,
                ["createdAt"] = item.CreatedAt,
                ["updatedAt"] = item.UpdatedAt,
                ["attentionRequired"] = item.AttentionRequired,
                ["replayable"] = item.Replayable,
                ["summaryCode"] = item.SummaryCode,
                ["decisionKind"] = item.DecisionKind,
                ["decisionTargetToken"] = item.DecisionTargetToken,
                ["answerAvailable"] = item.AnswerAvailable,
                ["questionTargetToken"] = item.QuestionTargetToken,
                ["questionTurnCount"] = item.QuestionTurnCount,
                ["questionAnswerRef"] = item.QuestionAnswerRef,
                ["ocrHumanResolution"] = item.OcrHumanResolution,
                ["activeQuestionSession"] = item.ActiveQuestionSession
            }).ToArray();
            if (schemaVersion == "2.0.0")
            {
                foreach (var item in v2Items)
                {
                    item.Remove("answerAvailable");
                    item.Remove("questionTargetToken");
                    item.Remove("questionTurnCount");
                    item.Remove("questionAnswerRef");
                    item.Remove("ocrHumanResolution");
                    item.Remove("activeQuestionSession");
                }
            }
            else if (schemaVersion == "2.2.0")
            {
                foreach (var item in v2Items)
                {
                    item.Remove("ocrHumanResolution");
                    item.Remove("activeQuestionSession");
                }
            }
            else if (schemaVersion == "2.3.0")
            {
                foreach (var item in v2Items) item.Remove("activeQuestionSession");
            }
            return new { schemaVersion, queryId, type, nextCursor = (string?)null, items = v2Items };
        }
        var v1Items = items.Select(item => new
        {
            item.TaskId,
            item.OperationId,
            item.Intent,
            item.Status,
            item.CreatedAt,
            item.UpdatedAt,
            item.AttentionRequired,
            item.Replayable,
            item.SummaryCode
        }).ToArray();
        return new { schemaVersion = "1.0.0", queryId, type, nextCursor = (string?)null, items = v1Items };
    }

    public Task<StageControlReceipt> HandleStageControlAsync(StageControlRequest request, CancellationToken cancellationToken) =>
        stageControl.HandleAsync(request, cancellationToken);

    public Task<ModuleActionReceipt> HandleModuleActionAsync(ModuleActionRequest request, CancellationToken cancellationToken) =>
        moduleAction?.HandleAsync(request, cancellationToken)
        ?? throw new IpcProtocolException("ipc.module_action_unavailable", "module-action requires an admitted registry/release boundary.");

    private CommandReceipt? FindAcceptedCommand(string idempotencyKey)
    {
        foreach (var job in Directory.GetDirectories(jobsRoot))
        {
            foreach (var eventPath in Directory.GetFiles(job, "*.json", SearchOption.AllDirectories).Where(path => path.Contains($"{Path.DirectorySeparatorChar}events{Path.DirectorySeparatorChar}")))
            {
                TaskEvent? item;
                try { item = JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(eventPath), ContractJson.Options); }
                catch (JsonException) { continue; }
                if (item?.Type != "command.accepted" || item.IdempotencyKey != idempotencyKey || item.CommandId is null)
                    continue;
                var operationId = item.Payload.GetProperty("operationId").GetString()!;
                var status = item.Payload.GetProperty("status").GetString()!;
                return new CommandReceipt("1.0.0", item.CommandId, true, item.TaskId, operationId, status, item.Timestamp, null, PathPolicy.ToJobRelative(job, eventPath));
            }
        }
        return null;
    }

    private IReadOnlyList<TaskProjectionItem> BuildProjection()
    {
        var output = new List<TaskProjectionItem>();
        foreach (var job in Directory.GetDirectories(jobsRoot))
        {
            var canonicalManifest = Path.Combine(job, "task.manifest.v2.json");
            var manifestPath = File.Exists(canonicalManifest) ? canonicalManifest : Path.Combine(job, "task.manifest.v1.json");
            if (!File.Exists(manifestPath))
                continue;
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var events = ReadAllEvents(job);
            var taskId = manifest.RootElement.GetProperty("taskId").GetString()!;
            var intent = manifest.RootElement.GetProperty("intent").GetString()!;
            var createdAt = manifest.RootElement.GetProperty("createdAt").GetDateTimeOffset();
            var latestEvents = LatestRunEvents(events);
            var status = TaskStateMachine.Project(latestEvents);
            var updatedAt = events.Count == 0 ? createdAt : events.Max(item => item.Timestamp);
            var attention = status is "waiting_input" or "partial" or "failed";
            var replayable = status is "succeeded" or "partial";
            string? decisionKind = null;
            string? decisionTargetToken = null;
            var answerAvailable = false;
            string? questionTargetToken = null;
            int? questionTurnCount = null;
            ArtifactReference? questionAnswerRef = null;
            QuestionSessionProjection? activeQuestionSession = null;
            OcrHumanResolutionProjection? ocrHumanResolution = null;
            if (status == "waiting_input")
            {
                var decision = latestEvents.LastOrDefault(item => item.Type == "task.decision_requested");
                if (decision is not null)
                {
                    decisionKind = decision.Payload.GetProperty("decisionKind").GetString();
                    decisionTargetToken = decision.Payload.GetProperty("targetToken").GetString();
                }
                var answer = latestEvents.LastOrDefault(item => item.Type == "task.question_answer_available");
                if (answer is not null)
                {
                    answerAvailable = answer.Payload.GetProperty("answerAvailable").GetBoolean();
                    questionTargetToken = answer.Payload.GetProperty("questionTargetToken").GetString();
                    questionTurnCount = answer.Payload.GetProperty("answeredTurnCount").GetInt32();
                    questionAnswerRef = answer.Payload.GetProperty("questionAnswerRef").Deserialize<ArtifactReference>(ContractJson.Options)
                        ?? throw new InvalidDataException("Question answer projection reference is invalid.");
                    activeQuestionSession = new QuestionSessionProjection(
                        answer.Payload.GetProperty("questionTargetToken").GetString()!,
                        answer.Payload.GetProperty("binding").Deserialize<QuestionWorkflowBinding>(ContractJson.Options)
                            ?? throw new InvalidDataException("Question session binding projection is invalid."),
                        answer.Payload.GetProperty("sessionRef").GetString()!,
                        answer.Payload.GetProperty("sourceAnswerArtifact").Deserialize<ArtifactReference>(ContractJson.Options)
                            ?? throw new InvalidDataException("Question source answer projection reference is invalid."),
                        questionAnswerRef,
                        answer.Payload.GetProperty("answeredTurnCount").GetInt32());
                }
                var ocr = latestEvents.LastOrDefault(item => item.Type == "stage.content_attention_required" &&
                    item.Payload.TryGetProperty("decisionKind", out var kind) && kind.GetString() == "ocr-human-resolution");
                if (ocr is not null)
                {
                    decisionKind = "ocr-human-resolution";
                    decisionTargetToken = ocr.Payload.GetProperty("targetToken").GetString();
                    ocrHumanResolution = new OcrHumanResolutionProjection(
                        "ocr-human-resolution", ocr.Payload.GetProperty("targetToken").GetString()!,
                        ocr.Payload.GetProperty("sourceProofreadArtifact").Deserialize<ArtifactReference>(ContractJson.Options)
                            ?? throw new InvalidDataException("OCR proofread projection reference is invalid."),
                        ocr.Payload.GetProperty("sourceImageProvenanceArtifact").Deserialize<ArtifactReference>(ContractJson.Options)
                            ?? throw new InvalidDataException("OCR image provenance projection reference is invalid."),
                        ocr.Payload.GetProperty("sourceOriginalImageArtifact").Deserialize<ArtifactReference>(ContractJson.Options)
                            ?? throw new InvalidDataException("OCR original image projection reference is invalid."),
                        ocr.Payload.GetProperty("originalTextRef").Deserialize<ArtifactReference>(ContractJson.Options)
                            ?? throw new InvalidDataException("OCR original projection reference is invalid."),
                        ocr.Payload.GetProperty("candidateTextRef").Deserialize<ArtifactReference>(ContractJson.Options)
                            ?? throw new InvalidDataException("OCR candidate projection reference is invalid."),
                        ocr.Payload.GetProperty("diff").Clone(), ocr.Payload.GetProperty("reason").Clone(),
                        ocr.Payload.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()!).ToArray());
                }
            }
            output.Add(new TaskProjectionItem(taskId, FindOperationId(events), intent, status, createdAt, updatedAt, attention, replayable,
                answerAvailable ? "task.answer_available" : "task." + status, decisionKind, decisionTargetToken,
                answerAvailable, questionTargetToken, questionTurnCount, questionAnswerRef, ocrHumanResolution, activeQuestionSession));
        }
        return output;
    }

    private static string FindOperationId(IReadOnlyList<TaskEvent> events)
    {
        foreach (var item in events.OrderBy(taskEvent => taskEvent.Sequence))
            if (item.Payload.TryGetProperty("operationId", out var operationId) && operationId.ValueKind == JsonValueKind.String)
                return operationId.GetString()!;
        return "operation-unknown";
    }

    private static IReadOnlyList<TaskEvent> ReadAllEvents(string jobRoot)
    {
        var runs = Path.Combine(jobRoot, "runs");
        if (!Directory.Exists(runs))
            return Array.Empty<TaskEvent>();
        return Directory.GetFiles(runs, "*.json", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}events{Path.DirectorySeparatorChar}"))
            .Select(path => JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options))
            .Where(item => item is not null)
            .Cast<TaskEvent>()
            .OrderBy(item => item.Timestamp)
            .ToArray();
    }

    private static IReadOnlyList<TaskEvent> LatestRunEvents(IReadOnlyList<TaskEvent> events)
    {
        if (events.Count == 0)
            return events;
        var groups = events.GroupBy(item => item.RunId ?? string.Empty, StringComparer.Ordinal).ToArray();
        var stateful = groups.Where(group => group.Any(item => item.Type == "task.state")).ToArray();
        return (stateful.Length > 0 ? stateful : groups)
            .OrderByDescending(group => group.Max(item => item.Timestamp))
            .First()
            .OrderBy(item => item.Sequence)
            .ToArray();
    }

    private static string ResolveRunId(string jobRoot)
    {
        var runs = Path.Combine(jobRoot, "runs");
        if (Directory.Exists(runs))
        {
            var latest = Directory.GetDirectories(runs).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest is not null)
                return Path.GetFileName(latest);
        }
        return $"run-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    }

    private static CommandReceipt Rejected(TaskCommand command, string taskId, string status, string code, string category, string message) =>
        new("1.0.0", command.CommandId, false, taskId, command.OperationId, status, DateTimeOffset.UtcNow,
            new WorkerError(code, category, false, command.PayloadRef, message, "input"), null);
}

public sealed record AcceptedCommand(TaskCommand Command, string TaskId, string JobRoot, string AcceptanceRunId, string DurableEventRef);

public sealed record TaskProjectionItem(
    string TaskId,
    string OperationId,
    string Intent,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool AttentionRequired,
    bool Replayable,
    string SummaryCode,
    string? DecisionKind = null,
    string? DecisionTargetToken = null,
    bool AnswerAvailable = false,
    string? QuestionTargetToken = null,
    int? QuestionTurnCount = null,
    ArtifactReference? QuestionAnswerRef = null,
    OcrHumanResolutionProjection? OcrHumanResolution = null,
    QuestionSessionProjection? ActiveQuestionSession = null);

public sealed record QuestionSessionProjection(
    string TargetToken,
    QuestionWorkflowBinding Binding,
    string SessionRef,
    ArtifactReference SourceAnswerRef,
    ArtifactReference DisplayAnswerRef,
    int AnsweredTurnCount);

public sealed class NamedPipeCommandServer
{
    private readonly IpcConfiguration configuration;
    private readonly CommandHandler handler;
    private readonly bool stageControlEnabled;
    private readonly bool moduleActionEnabled;

    public NamedPipeCommandServer(IpcConfiguration configuration, CommandHandler handler)
    {
        this.configuration = configuration;
        this.handler = handler;
        if (configuration.Transport != "windows-named-pipe" || configuration.AccessControl != "current-windows-user")
            throw new InvalidDataException("IPC configuration must require a current-user Windows named pipe.");
        if (configuration.Framing.Protocol != "utf-8-json-lines-v1" || configuration.Framing.RequestPerConnection != 1 ||
            configuration.Framing.ResponsePerConnection != 1 || configuration.Framing.Delimiter != "LF" ||
            !configuration.Framing.AcceptedDelimiter.SequenceEqual(new[] { "LF", "CRLF" }) ||
            configuration.Framing.BomAllowed || configuration.Framing.JsonNewlines != "escaped-only" ||
            !configuration.Framing.ByteLimitExcludesDelimiter || configuration.Framing.ServerPush ||
            configuration.Framing.ConnectionMode != "unary" || configuration.Framing.Concurrency != "multiple-connections" ||
            configuration.MaxMessageBytes != 1_048_576 || configuration.ServerOwnership != "orchestrator-exclusive" ||
            !configuration.InlineContentProhibited || configuration.PersistentSourceOfTruth != "job-event-sequence" ||
            configuration.ProtocolErrorBehavior != "respond-when-possible-then-close")
            throw new InvalidDataException("IPC configuration does not match the JSON Lines transport contract.");
        var frozenV1 = configuration.SchemaVersion == "1.0.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query" }) &&
            configuration.RequestSchema == "../schemas/common/ipc-request.schema.json" &&
            configuration.ResponseSchema == "../schemas/common/ipc-response.schema.json";
        var stageControlV11 = configuration.SchemaVersion == "1.1.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query", "stage-control" }) &&
            configuration.RequestSchema == "schemas/ipc-request.v1.1.schema.json" &&
            configuration.ResponseSchema == "schemas/ipc-response.v1.1.schema.json";
        var moduleActionV12 = configuration.SchemaVersion == "1.2.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query", "stage-control", "module-action" }) &&
            configuration.RequestSchema == "schemas/ipc-request.v1.2.schema.json" &&
            configuration.ResponseSchema == "schemas/ipc-response.v1.2.schema.json";
        var moduleActionV13 = configuration.SchemaVersion == "1.3.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query", "stage-control", "module-action" }) &&
            configuration.RequestSchema == "schemas/ipc-request.v1.3.schema.json" &&
            configuration.ResponseSchema == "schemas/ipc-response.v1.3.schema.json";
        var moduleActionV14 = configuration.SchemaVersion == "1.4.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query", "stage-control", "module-action" }) &&
            configuration.RequestSchema == "schemas/ipc-request.v1.4.schema.json" &&
            configuration.ResponseSchema == "schemas/ipc-response.v1.4.schema.json";
        var moduleActionV15 = configuration.SchemaVersion == "1.5.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query", "stage-control", "module-action" }) &&
            configuration.RequestSchema == "schemas/ipc-request.v1.5.schema.json" &&
            configuration.ResponseSchema == "schemas/ipc-response.v1.5.schema.json";
        var moduleActionV16 = configuration.SchemaVersion == "1.6.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query", "stage-control", "module-action" }) &&
            configuration.RequestSchema == "schemas/ipc-request.v1.6.schema.json" &&
            configuration.ResponseSchema == "schemas/ipc-response.v1.6.schema.json";
        var moduleActionV17 = configuration.SchemaVersion == "1.7.0" &&
            configuration.AllowedMessages.SequenceEqual(new[] { "task-command", "task-query", "stage-control", "module-action" }) &&
            configuration.RequestSchema == "schemas/ipc-request.v1.7.schema.json" &&
            configuration.ResponseSchema == "schemas/ipc-response.v1.7.schema.json";
        if (!frozenV1 && !stageControlV11 && !moduleActionV12 && !moduleActionV13 && !moduleActionV14 && !moduleActionV15 && !moduleActionV16 && !moduleActionV17)
            throw new InvalidDataException("IPC configuration is neither frozen v1 nor append-only stage-control/module-action v1.1/v1.2/v1.3/v1.4/v1.5/v1.6/v1.7.");
        stageControlEnabled = stageControlV11 || moduleActionV12 || moduleActionV13 || moduleActionV14 || moduleActionV15 || moduleActionV16 || moduleActionV17;
        moduleActionEnabled = moduleActionV12 || moduleActionV13 || moduleActionV14 || moduleActionV15 || moduleActionV16 || moduleActionV17;
    }

    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                configuration.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
            _ = HandleAndDisposeAsync(server, cancellationToken);
        }
    }

    private async Task HandleAndDisposeAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        await using (stream)
        {
            var correlationId = $"protocol-generated-{Guid.NewGuid():N}";
            var responseProtocol = "1.0";
            try
            {
                var frame = await ReadFrameAsync(stream, configuration.MaxMessageBytes, cancellationToken);
                using var document = JsonDocument.Parse(frame);
                if (document.RootElement.TryGetProperty("correlationId", out var correlationElement) && correlationElement.ValueKind == JsonValueKind.String)
                    correlationId = correlationElement.GetString() ?? correlationId;
                var request = document.RootElement.Deserialize<IpcRequestEnvelope>(ContractJson.Options)
                    ?? throw new IpcProtocolException("ipc.invalid_envelope", "Request envelope is empty.");
                responseProtocol = request.ProtocolVersion;
                if (request.ProtocolVersion is not ("1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7") || string.IsNullOrWhiteSpace(request.CorrelationId))
                    throw new IpcProtocolException("ipc.unsupported_protocol", "protocolVersion must be 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, or 1.7 and correlationId must be present.");

                IpcResponseEnvelope response;
                if (request.MessageType == "task-command")
                {
                    if (request.ProtocolVersion != "1.0") throw new IpcProtocolException("ipc.unsupported_protocol", "task-command requires protocolVersion 1.0.");
                    var command = request.Payload.Deserialize<TaskCommand>(ContractJson.Options)
                        ?? throw new IpcProtocolException("ipc.invalid_payload", "task-command payload is invalid.");
                    var receipt = await handler.HandleAsync(command, cancellationToken);
                    response = new IpcResponseEnvelope("1.0", request.CorrelationId, "task-command-receipt", true, receipt, null);
                }
                else if (request.MessageType == "task-query")
                {
                    if (request.ProtocolVersion != "1.0") throw new IpcProtocolException("ipc.unsupported_protocol", "task-query requires protocolVersion 1.0.");
                    var result = handler.HandleQuery(request.Payload);
                    response = new IpcResponseEnvelope("1.0", request.CorrelationId, "task-query-result", true, result, null);
                }
                else if (request.MessageType == "stage-control")
                {
                    if (!stageControlEnabled) throw new IpcProtocolException("ipc.unknown_message_type", "stage-control requires the append-only IPC v1.1 configuration.");
                    if (request.ProtocolVersion != "1.1") throw new IpcProtocolException("ipc.unsupported_protocol", "stage-control requires protocolVersion 1.1.");
                    var names = request.Payload.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
                    if (!names.SetEquals(new[] { "taskId", "operation", "selector", "reason" }))
                        throw new IpcProtocolException("ipc.invalid_payload", "stage-control payload must contain exactly taskId, operation, selector, and reason.");
                    var control = request.Payload.Deserialize<StageControlRequest>(ContractJson.Options)
                        ?? throw new IpcProtocolException("ipc.invalid_payload", "stage-control payload is invalid.");
                    var receipt = await handler.HandleStageControlAsync(control, cancellationToken);
                    response = new IpcResponseEnvelope("1.1", request.CorrelationId, "stage-control-receipt", true, receipt, null);
                }
                else if (request.MessageType == "module-action")
                {
                    if (!moduleActionEnabled) throw new IpcProtocolException("ipc.unknown_message_type", "module-action requires append-only IPC v1.2 through v1.7.");
                    if (request.ProtocolVersion is not ("1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7")) throw new IpcProtocolException("ipc.unsupported_protocol", "module-action requires protocolVersion 1.2 through 1.7.");
                    var action = request.Payload.Deserialize<ModuleActionRequest>(ContractJson.Options)
                        ?? throw new IpcProtocolException("ipc.invalid_payload", "module-action payload is invalid.");
                    var expectedActionSchema = request.ProtocolVersion switch
                    {
                        "1.2" => "knowledge-clipper-module-action.v1",
                        "1.3" => "knowledge-clipper-module-action.v2",
                        "1.4" => "knowledge-clipper-module-action.v3",
                        "1.5" => "knowledge-clipper-module-action.v4",
                        "1.6" => "knowledge-clipper-module-action.v5",
                        _ => "knowledge-clipper-module-action.v6"
                    };
                    if (action.SchemaVersion != expectedActionSchema)
                        throw new IpcProtocolException("ipc.invalid_payload", "module-action schemaVersion must match its append-only IPC protocol.");
                    var receipt = await handler.HandleModuleActionAsync(action, cancellationToken);
                    response = new IpcResponseEnvelope(request.ProtocolVersion, request.CorrelationId, "module-action-receipt", true, receipt, null);
                }
                else
                {
                    throw new IpcProtocolException("ipc.unknown_message_type", $"Unknown messageType: {request.MessageType}");
                }
                await WriteFrameAsync(stream, response, configuration.MaxMessageBytes, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException or IpcProtocolException or InvalidDataException)
            {
                var code = exception is IpcProtocolException protocolException ? protocolException.Code :
                    exception is JsonException or DecoderFallbackException ? "ipc.invalid_json" : "ipc.invalid_frame";
                var response = new IpcResponseEnvelope(
                    responseProtocol is "1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" ? responseProtocol : "1.0",
                    correlationId,
                    "protocol-error",
                    false,
                    null,
                    new IpcProtocolError(code, "protocol", SafeProtocolMessage(exception.Message)));
                try
                {
                    await WriteFrameAsync(stream, response, configuration.MaxMessageBytes, CancellationToken.None);
                }
                catch (IOException)
                {
                    // The peer may already have closed after sending an invalid frame.
                }
            }
        }
    }

    private static async Task<string> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        await using var payload = new MemoryStream(Math.Min(maxBytes, 65_536));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                throw new IpcProtocolException("ipc.incomplete_frame", "Connection closed before the LF delimiter.");
            var delimiterIndex = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var payloadBytes = delimiterIndex >= 0 ? delimiterIndex : read;
            if (delimiterIndex >= 0 && payloadBytes > 0 && buffer[payloadBytes - 1] == (byte)'\r')
                payloadBytes--;
            else if (delimiterIndex == 0 && payload.Length > 0 && payload.GetBuffer()[(int)payload.Length - 1] == (byte)'\r')
                payload.SetLength(payload.Length - 1);
            if (delimiterIndex >= 0)
            {
                if (payload.Length + payloadBytes > maxBytes)
                    throw new IpcProtocolException("ipc.frame_too_large", $"Request exceeds {maxBytes} UTF-8 bytes.");
                await payload.WriteAsync(buffer.AsMemory(0, payloadBytes), cancellationToken);
            }
            else
            {
                if (payload.Length + payloadBytes > maxBytes + 1L)
                    throw new IpcProtocolException("ipc.frame_too_large", $"Request exceeds {maxBytes} UTF-8 bytes.");
                await payload.WriteAsync(buffer.AsMemory(0, payloadBytes), cancellationToken);
                if (payload.Length > maxBytes && payload.GetBuffer()[(int)payload.Length - 1] != (byte)'\r')
                    throw new IpcProtocolException("ipc.frame_too_large", $"Request exceeds {maxBytes} UTF-8 bytes.");
            }
            if (delimiterIndex >= 0)
                break;
        }
        var bytes = payload.ToArray();
        var count = bytes.Length;
        if (count == 0)
            throw new IpcProtocolException("ipc.empty_frame", "Request frame is empty.");
        if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new IpcProtocolException("ipc.bom_forbidden", "UTF-8 BOM is forbidden.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, count);
    }

    private static async Task WriteFrameAsync(Stream stream, object value, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(JsonSerializer.Serialize(value, IpcJson.Options));
        if (bytes.Length > maxBytes)
            throw new IpcProtocolException("ipc.response_too_large", $"Response exceeds {maxBytes} UTF-8 bytes.");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string SafeProtocolMessage(string message) => message.Length <= 1000 ? message : message[..1000];
}

public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;

namespace KnowledgeClipper.Core.V3.Client;

public sealed record CoreV3ClientOptions(
    TimeSpan RequestTimeout,
    TimeSpan ObservationTimeout,
    TimeSpan PollInterval)
{
    public static CoreV3ClientOptions Default { get; } = new(
        RequestTimeout: TimeSpan.FromSeconds(10),
        ObservationTimeout: TimeSpan.FromMinutes(2),
        PollInterval: TimeSpan.FromMilliseconds(250));
}

public enum QuestionProjectionState
{
    Running,
    WaitingInput,
    Succeeded,
    Failed,
    Cancelled,
    Degraded
}

public sealed record CapabilityAvailability(string Operation, bool Available, string? AvailabilityReason);

public sealed record CapabilitySnapshot(string CatalogId, string CatalogSha256, IReadOnlyList<CapabilityAvailability> Operations);

public enum TaskProjectionState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Degraded
}

public enum OperationProjectionState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Degraded
}

public sealed record ContentSettingsDescription(string SettingsContract, IReadOnlyList<string> SupportedActions, string ExecutionMode, int ExternalWriteCount);

public sealed class TypedArtifactHandle : IEquatable<TypedArtifactHandle>
{
    internal TypedArtifactHandle(ArtifactReference reference)
    {
        Reference = reference;
        Token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(reference))).ToLowerInvariant();
    }

    public string Token { get; }

    internal ArtifactReference Reference { get; }

    public bool Equals(TypedArtifactHandle? other) => other is not null && Token == other.Token;

    public override bool Equals(object? obj) => obj is TypedArtifactHandle other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Token);
}

public enum ArtifactPurpose
{
    Question,
    QuestionContext,
    QuestionFollowUp,
    QuestionAnswer,
    AnkiConfiguration
}

public sealed record PublishTextArtifactRequest(string RequestId, ArtifactPurpose Purpose, string Text, string DataClass = "personal");

public sealed record TypedArtifactContent(
    TypedArtifactHandle Artifact,
    string RepresentationId,
    string MediaType,
    string? ContractSchema,
    byte[] Content);

public sealed record AnkiProbeRequest(string RequestId, TypedArtifactHandle Configuration, string SelectedRoute = "canary");

public sealed record AnkiReadinessSummary(
    bool ConnectionReady,
    string SelectedRoute,
    bool SelectedRouteReady,
    bool ReadyForRequestedAction,
    string OverallState,
    IReadOnlyList<string> NonBlockingIssues,
    IReadOnlyList<string> BlockingIssues);

public sealed record QuestionInitialRequest(string RequestId, TypedArtifactHandle Question, IReadOnlyList<TypedArtifactHandle> Context);

public sealed record QuestionFollowUpRequest(string RequestId, string SessionId, TypedArtifactHandle FollowUp);

public sealed record AnkiWriteAuthorization(string AuthorizationId, string Scope = "isolated-canary-deck");

public sealed record QuestionMakeCardsRequest(
    string RequestId,
    string SessionId,
    TypedArtifactHandle Answer,
    TypedArtifactHandle? AnkiConfiguration = null,
    AnkiWriteAuthorization? Authorization = null);

public sealed record OperationSubmission(string OperationId, long Cursor, bool Accepted);

public sealed record QuestionOperationSubmission(string OperationId, string SessionId, long Cursor, bool Accepted);

public sealed record TaskProjectionSnapshot(string TaskId, TaskProjectionState State, long Cursor, string? Notice);

public sealed record OperationProjectionSnapshot(
    string OperationId,
    OperationProjectionState State,
    long Cursor,
    string? Notice,
    TypedArtifactHandle? AnkiConnectionStatusArtifact)
{
    public AnkiReadinessSummary? AnkiReadiness { get; init; }
}

public sealed record QuestionProjectionSnapshot(
    string OperationId,
    QuestionProjectionState State,
    long Cursor,
    string? Notice,
    TypedArtifactHandle? AnswerArtifact,
    TypedArtifactHandle? CardBundleArtifact,
    TypedArtifactHandle? DestinationEvidenceArtifact);

public sealed class CoreV3Client
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CoreV3ProtocolClient _protocol;
    private readonly CoreV3ClientOptions _options;

    public CoreV3Client(string pipeName, CoreV3ClientOptions? options = null)
    {
        _options = options ?? CoreV3ClientOptions.Default;
        if (_options.RequestTimeout <= TimeSpan.Zero ||
            _options.ObservationTimeout <= TimeSpan.Zero ||
            _options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Client timeout and observation limits must be positive.");
        }

        _protocol = new CoreV3ProtocolClient(pipeName, _options.RequestTimeout);
    }

    public async Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        _ = await _protocol.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        var result = await QueryAsync("capabilities.get", new { }, cancellationToken).ConfigureAwait(false);
        var catalog = result.Data.GetProperty("catalog").Deserialize<CapabilityCatalogDocument>(JsonOptions)
            ?? throw new CoreV3ClientException("capability_payload_invalid", "Capability catalog payload is invalid.");
        return new CapabilitySnapshot(
            catalog.CatalogId,
            result.Data.GetProperty("catalogSha256").GetString()!,
            catalog.Operations.Select(item => new CapabilityAvailability(item.OperationId, item.Available, item.AvailabilityReason)).ToArray());
    }

    public async Task<ContentSettingsDescription> DescribeContentSettingsAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var receipt = await SubmitAsync(
            requestId,
            "content.settings.describe",
            "kc.content-settings-describe-command/1",
            new { includeSchemas = true },
            Array.Empty<ArtifactReference>(),
            cancellationToken).ConfigureAwait(false);
        var result = GetCompletedResult(receipt);
        return result.Deserialize<ContentSettingsDescription>(JsonOptions)
            ?? throw new CoreV3ClientException("operation_payload_invalid", "Content settings description payload is invalid.");
    }

    public async Task<TypedArtifactHandle> PublishTextArtifactAsync(
        PublishTextArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        var purpose = request.Purpose switch
        {
            ArtifactPurpose.Question => "question",
            ArtifactPurpose.QuestionContext => "question-context",
            ArtifactPurpose.QuestionFollowUp => "question-follow-up",
            ArtifactPurpose.QuestionAnswer => "question-answer",
            ArtifactPurpose.AnkiConfiguration => "anki-configuration",
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unknown artifact purpose.")
        };
        var receipt = await SubmitAsync(
            request.RequestId,
            "artifact.publish",
            "kc.artifact-publish-command/1",
            new
            {
                purpose,
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Text)),
                request.DataClass
            },
            Array.Empty<ArtifactReference>(),
            cancellationToken).ConfigureAwait(false);
        _ = GetCompletedResult(receipt);
        var artifacts = receipt.Events.Where(item => item.EventType == "operation.completed").SelectMany(item => item.Artifacts).ToArray();
        if (artifacts.Length != 1)
        {
            throw new CoreV3OperationException(
                "artifact_publish_result_invalid",
                "Artifact publish did not return exactly one artifact handle.",
                receipt.OperationId,
                "failed");
        }

        return new TypedArtifactHandle(artifacts[0]);
    }

    public Task<OperationSubmission> AnkiProbeAsync(AnkiProbeRequest request, CancellationToken cancellationToken = default)
    {
        return SubmitDomainOperationAsync(
            request.RequestId,
            "anki.probe",
            "kc.anki-probe-command/1",
            new { configuration = request.Configuration.Reference, selectedRoute = request.SelectedRoute },
            new[] { request.Configuration.Reference },
            cancellationToken);
    }

    public Task<QuestionOperationSubmission> QuestionInitialAsync(QuestionInitialRequest request, CancellationToken cancellationToken = default)
    {
        return SubmitQuestionOperationAsync(
            request.RequestId,
            "question.initial",
            "kc.question-initial-command/2",
            request.RequestId,
            new { sessionId = request.RequestId, question = request.Question.Reference, context = request.Context.Select(item => item.Reference).ToArray() },
            new[] { request.Question.Reference }.Concat(request.Context.Select(item => item.Reference)).ToArray(),
            null,
            cancellationToken);
    }

    public Task<QuestionOperationSubmission> QuestionFollowUpAsync(QuestionFollowUpRequest request, CancellationToken cancellationToken = default)
    {
        return SubmitQuestionOperationAsync(
            request.RequestId,
            "question.follow-up",
            "kc.question-follow-up-command/1",
            request.SessionId,
            new { request.SessionId, followUp = request.FollowUp.Reference },
            new[] { request.FollowUp.Reference },
            null,
            cancellationToken);
    }

    public Task<QuestionOperationSubmission> QuestionMakeCardsAsync(QuestionMakeCardsRequest request, CancellationToken cancellationToken = default)
    {
        var inputs = new[] { request.Answer.Reference }
            .Concat(request.AnkiConfiguration is null ? Array.Empty<ArtifactReference>() : new[] { request.AnkiConfiguration.Reference })
            .ToArray();
        return SubmitQuestionOperationAsync(
            request.RequestId,
            "question.make-cards",
            "kc.question-make-cards-command/1",
            request.SessionId,
            new { request.SessionId, answer = request.Answer.Reference, configuration = request.AnkiConfiguration?.Reference },
            inputs,
            request.Authorization is null ? null : new AuthorizationReference(request.Authorization.AuthorizationId, request.Authorization.Scope),
            cancellationToken);
    }

    public async Task<TaskProjectionSnapshot> GetTaskProjectionAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync("task.projection", new { taskId }, cancellationToken).ConfigureAwait(false);
        var stateText = result.Data.GetProperty("state").GetString()!;
        if (!Enum.TryParse<TaskProjectionState>(stateText, ignoreCase: true, out var state))
        {
            state = TaskProjectionState.Degraded;
        }

        return new TaskProjectionSnapshot(
            taskId,
            state,
            result.Cursor,
            GetOptionalString(result.Data, "notice"));
    }

    public async Task<OperationProjectionSnapshot> GetOperationProjectionAsync(string operationId, CancellationToken cancellationToken = default)
        => await GetOperationProjectionCoreAsync(operationId, afterCursor: null, cancellationToken).ConfigureAwait(false);

    public async Task<OperationProjectionSnapshot> WaitOperationProjectionAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        long? cursor = null;
        while (DateTimeOffset.UtcNow - started < _options.ObservationTimeout)
        {
            OperationProjectionSnapshot snapshot;
            try
            {
                snapshot = await GetOperationProjectionCoreAsync(operationId, cursor, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (snapshot.State is OperationProjectionState.Succeeded or OperationProjectionState.Failed or OperationProjectionState.Cancelled or OperationProjectionState.Degraded)
            {
                return snapshot;
            }
            cursor = Math.Max(cursor ?? 0, snapshot.Cursor);
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new CoreV3ClientException("observation_timeout", $"Operation '{operationId}' did not reach a terminal state within {_options.ObservationTimeout}.");
    }

    private async Task<OperationProjectionSnapshot> GetOperationProjectionCoreAsync(string operationId, long? afterCursor, CancellationToken cancellationToken)
    {
        var result = await QueryAsync("operation.projection", new { operationId }, cancellationToken, afterCursor).ConfigureAwait(false);
        var stateText = result.Data.GetProperty("state").GetString()!;
        if (!Enum.TryParse<OperationProjectionState>(stateText, ignoreCase: true, out var state)) state = OperationProjectionState.Degraded;
        var outputs = result.Data.GetProperty("outputs");
        var snapshot = new OperationProjectionSnapshot(operationId, state, result.Cursor, GetOptionalString(result.Data, "notice"), ReadOptionalArtifactHandle(outputs, "ankiConnectionStatusArtifact"));
        if (result.Data.TryGetProperty("result", out var operationResult) && operationResult.ValueKind == JsonValueKind.Object &&
            operationResult.TryGetProperty("readiness", out var readiness) && readiness.ValueKind == JsonValueKind.Object)
        {
            snapshot = snapshot with
            {
                AnkiReadiness = readiness.Deserialize<AnkiReadinessSummary>(JsonOptions)
                    ?? throw new CoreV3ClientException("projection_payload_invalid", "Anki readiness projection is invalid.")
            };
        }
        return snapshot;
    }

    public async Task<QuestionProjectionSnapshot> GetQuestionProjectionAsync(string operationId, CancellationToken cancellationToken = default)
        => await GetQuestionProjectionCoreAsync(operationId, afterCursor: null, cancellationToken).ConfigureAwait(false);

    private async Task<QuestionProjectionSnapshot> GetQuestionProjectionCoreAsync(string operationId, long? afterCursor, CancellationToken cancellationToken)
    {
        var result = await QueryAsync("question.projection", new { operationId }, cancellationToken, afterCursor).ConfigureAwait(false);
        var stateText = result.Data.GetProperty("state").GetString()!;
        if (!Enum.TryParse<QuestionProjectionState>(stateText, ignoreCase: true, out var state))
        {
            state = QuestionProjectionState.Degraded;
        }

        var outputs = result.Data.GetProperty("outputs");
        return new QuestionProjectionSnapshot(
            operationId,
            state,
            result.Cursor,
            GetOptionalString(result.Data, "notice"),
            ReadOptionalArtifactHandle(outputs, "answerArtifact"),
            ReadOptionalArtifactHandle(outputs, "cardBundleArtifact"),
            ReadOptionalArtifactHandle(outputs, "destinationEvidenceArtifact"));
    }

    public async Task<QuestionProjectionSnapshot> WaitQuestionProjectionAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        QuestionProjectionSnapshot? last = null;
        await foreach (var snapshot in ObserveQuestionProjectionAsync(operationId, cancellationToken).ConfigureAwait(false))
        {
            last = snapshot;
        }

        if (last is null)
        {
            throw new QuestionObservationException("observation_empty", "Question observation returned no projection.", null);
        }

        if (last.State == QuestionProjectionState.Degraded)
        {
            throw new QuestionObservationException("observation_degraded", last.Notice ?? "Question observation degraded.", last);
        }

        return last;
    }

    public async IAsyncEnumerable<QuestionProjectionSnapshot> ObserveQuestionProjectionAsync(
        string operationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        long? lastCursor = null;
        while (true)
        {
            if (DateTimeOffset.UtcNow - started >= _options.ObservationTimeout)
            {
                yield return Degraded(operationId, lastCursor ?? 0, "Question observation timed out before a terminal state.");
                yield break;
            }

            QuestionProjectionSnapshot snapshot;
            try
            {
                snapshot = await GetQuestionProjectionCoreAsync(operationId, lastCursor, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreV3ClientException ex) when (ex.Code is "request_timeout" or "transport_disconnected")
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            yield return snapshot;
            if (snapshot.State is QuestionProjectionState.WaitingInput or QuestionProjectionState.Succeeded or QuestionProjectionState.Failed or QuestionProjectionState.Cancelled or QuestionProjectionState.Degraded)
            {
                yield break;
            }

            lastCursor = Math.Max(lastCursor ?? 0, snapshot.Cursor);

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TypedArtifactContent> ReadArtifactAsync(
        TypedArtifactHandle artifact,
        string? representationId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync("artifact.read", new { reference = artifact.Reference, representationId }, cancellationToken).ConfigureAwait(false);
        return new TypedArtifactContent(
            artifact,
            result.Data.GetProperty("representationId").GetString()!,
            result.Data.GetProperty("mediaType").GetString()!,
            GetOptionalString(result.Data, "schema"),
            Convert.FromBase64String(result.Data.GetProperty("contentBase64").GetString()!));
    }

    internal Task<QueryResult> QueryProtocolAsync(QueryEnvelope query, CancellationToken cancellationToken)
        => _protocol.QueryAsync(query, cancellationToken);

    internal Task<string> SendCompatibilityFrameAsync(string requestJson, CancellationToken cancellationToken)
        => _protocol.SendCompatibilityFrameAsync(requestJson, cancellationToken);

    private async Task<OperationSubmission> SubmitDomainOperationAsync(
        string requestId,
        string operation,
        string bodySchema,
        object body,
        IReadOnlyList<ArtifactReference> inputs,
        CancellationToken cancellationToken)
    {
        var receipt = await SubmitAsync(requestId, operation, bodySchema, body, inputs, cancellationToken).ConfigureAwait(false);
        return new OperationSubmission(receipt.OperationId, receipt.Cursor, receipt.Accepted);
    }

    private async Task<QuestionOperationSubmission> SubmitQuestionOperationAsync(
        string requestId,
        string operation,
        string bodySchema,
        string sessionId,
        object body,
        IReadOnlyList<ArtifactReference> inputs,
        AuthorizationReference? authorization,
        CancellationToken cancellationToken)
    {
        var receipt = await SubmitAsync(requestId, operation, bodySchema, body, inputs, cancellationToken, authorization).ConfigureAwait(false);
        return new QuestionOperationSubmission(receipt.OperationId, sessionId, receipt.Cursor, receipt.Accepted);
    }

    private Task<CommandReceipt> SubmitAsync(
        string requestId,
        string operation,
        string bodySchema,
        object body,
        IReadOnlyList<ArtifactReference> inputs,
        CancellationToken cancellationToken,
        AuthorizationReference? authorization = null)
    {
        RequireRequestId(requestId);
        var operationId = operation.Replace('.', '-') + "-" + requestId;
        var idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + requestId)));
        var command = new CommandEnvelope(
            KernelSchemas.Command,
            "command-" + requestId,
            operationId,
            operation,
            new KernelSubject("desktop-request", requestId),
            idempotencyKey,
            ExpectedRevision: null,
            Authorization: authorization,
            inputs,
            new ContractBody(bodySchema, JsonSerializer.SerializeToElement(body, JsonOptions)));
        return _protocol.CommandAsync(command, cancellationToken);
    }

    private Task<QueryResult> QueryAsync(string projection, object parameters, CancellationToken cancellationToken, long? cursor = null)
    {
        var query = new QueryEnvelope(
            KernelSchemas.Query,
            "query-" + Guid.NewGuid().ToString("N"),
            projection,
            Subject: null,
            "read-your-writes",
            Cursor: cursor,
            JsonSerializer.SerializeToElement(parameters, JsonOptions));
        return _protocol.QueryAsync(query, cancellationToken);
    }

    private static JsonElement GetCompletedResult(CommandReceipt receipt)
    {
        var completed = receipt.Events.LastOrDefault(item => item.EventType == "operation.completed");
        if (completed is null)
        {
            throw new CoreV3OperationException(
                receipt.Error?.Code ?? "operation_not_completed",
                receipt.Error?.Message ?? "Operation did not reach completed state.",
                receipt.OperationId,
                receipt.Events.LastOrDefault()?.EventType ?? "not-terminal");
        }

        return completed.Data.GetProperty("result");
    }

    private static QuestionProjectionSnapshot Degraded(string operationId, long cursor, string notice)
        => new(operationId, QuestionProjectionState.Degraded, cursor, notice, null, null, null);

    private static TypedArtifactHandle? ReadOptionalArtifactHandle(JsonElement outputs, string name)
    {
        if (!outputs.TryGetProperty(name, out var artifact) || artifact.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return new TypedArtifactHandle(
            artifact.Deserialize<ArtifactReference>(JsonOptions)
            ?? throw new CoreV3ClientException("projection_payload_invalid", $"Projection output '{name}' is invalid."));
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void RequireRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 80 || !requestId.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            throw new ArgumentException("requestId must be a stable 1-80 character domain request identifier.", nameof(requestId));
        }
    }
}

public sealed class CoreV3OperationException : Exception
{
    public CoreV3OperationException(string code, string message, string operationId, string terminalState)
        : base(message)
    {
        Code = code;
        OperationId = operationId;
        TerminalState = terminalState;
    }

    public string Code { get; }

    public string OperationId { get; }

    public string TerminalState { get; }
}

public sealed class QuestionObservationException : Exception
{
    public QuestionObservationException(string code, string message, QuestionProjectionSnapshot? lastProjection)
        : base(message)
    {
        Code = code;
        LastProjection = lastProjection;
    }

    public string Code { get; }

    public QuestionProjectionSnapshot? LastProjection { get; }
}

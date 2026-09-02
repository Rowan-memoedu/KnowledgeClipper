using System.Collections.Concurrent;
using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed record StageControlRequest(string TaskId, string Operation, string Selector, string Reason);

public sealed record StageControlResolution(
    string RunId,
    string StageId,
    string AttemptId,
    string OperationId,
    string CancelSignalRef);

public sealed record StageControlError(string Code, string Category, bool Retryable, string Message);

public sealed record StageControlReceipt(
    string SchemaVersion,
    string ControlId,
    bool Accepted,
    string TaskId,
    string Operation,
    string Selector,
    string Reason,
    string Status,
    StageControlResolution? Resolved,
    DateTimeOffset Timestamp,
    string? DurableEventRef,
    string? ReceiptRef,
    StageControlError? Error);

public sealed class StageControlHandler
{
    public const string ReceiptSchemaVersion = "2.3.0";
    private const int GracePeriodMs = 2_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TaskGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string jobsRoot;

    public StageControlHandler(string jobsRoot)
    {
        this.jobsRoot = Path.GetFullPath(jobsRoot);
        Directory.CreateDirectory(this.jobsRoot);
    }

    public async Task<StageControlReceipt> HandleAsync(StageControlRequest request, CancellationToken cancellationToken)
    {
        if (!ValidIdentifier(request.TaskId) || request.Operation != "speech.playback" ||
            request.Selector != "current-active" || request.Reason != "user_stop")
            return Rejected(request, "control.request_invalid", "contract", "Only the exact current-active speech.playback user_stop request is supported.");

        var jobRoot = PathPolicy.ResolveJobRelative(jobsRoot, request.TaskId);
        if (!Directory.Exists(jobRoot))
            return Rejected(request, "control.task_not_found", "input", "The requested task does not exist.");

        var gate = TaskGates.GetOrAdd(jobRoot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var candidates = ReadCandidates(jobRoot, request.TaskId);
            var active = candidates.Where(item => !item.ResultExists && !item.CancelExists).ToArray();
            if (active.Length > 1)
                return Rejected(request, "control.playback_ambiguous", "state", "More than one active playback attempt exists; no signal was published.");
            if (active.Length == 0)
            {
                if (candidates.Any(item => !item.ResultExists && item.CancelExists))
                    return Rejected(request, "control.playback_already_requested", "state", "The active playback attempt already has a cancellation signal.");
                if (candidates.Any(item => item.ResultExists))
                    return Rejected(request, "control.playback_terminal", "state", "The playback attempt is already terminal.");
                return Rejected(request, "control.playback_not_active", "state", "No active playback attempt exists.");
            }

            var target = active[0];
            if (File.Exists(target.ResultPath))
                return Rejected(request, "control.playback_terminal", "state", "The playback attempt became terminal before cancellation was published.");
            if (File.Exists(target.CancelPath))
                return Rejected(request, "control.playback_already_requested", "state", "The playback attempt already has a cancellation signal.");

            var controlId = $"control-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var requestedAt = DateTimeOffset.UtcNow;
            var signal = new WorkerCancelSignal(
                "1.0.0", target.Request.AttemptId, target.Request.OperationId, request.Reason,
                requestedAt, requestedAt.AddMilliseconds(GracePeriodMs), controlId);
            try
            {
                await AtomicFile.WriteJsonOnceAsync(target.CancelPath, signal, cancellationToken);
            }
            catch (IOException) when (File.Exists(target.CancelPath))
            {
                return Rejected(request, "control.playback_already_requested", "state", "Another stop request won the cancellation race.");
            }

            return await PublishReceiptAsync(jobRoot, request, target.Request, controlId, requestedAt, recovered: false, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var recovered = 0;
        foreach (var jobRoot in Directory.GetDirectories(jobsRoot))
        {
            var taskId = Path.GetFileName(jobRoot);
            var runsRoot = Path.Combine(jobRoot, "runs");
            if (!Directory.Exists(runsRoot))
                continue;
            foreach (var cancelPath in Directory.GetFiles(runsRoot, "cancel.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkerCancelSignal? signal;
                WorkerRequest? workerRequest;
                try
                {
                    signal = JsonSerializer.Deserialize<WorkerCancelSignal>(await File.ReadAllTextAsync(cancelPath, cancellationToken), ContractJson.Options);
                    var requestPath = Path.Combine(Path.GetDirectoryName(cancelPath)!, "request.json");
                    workerRequest = File.Exists(requestPath)
                        ? JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken), ContractJson.Options)
                        : null;
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (signal?.SchemaVersion != "1.0.0" || string.IsNullOrWhiteSpace(signal.CommandId) || signal.Reason != "user_stop" ||
                    workerRequest is null || workerRequest.Operation != "speech.playback" || workerRequest.TaskId != taskId ||
                    workerRequest.AttemptId != signal.AttemptId || workerRequest.OperationId != signal.OperationId ||
                    !IsExactAttemptPath(jobRoot, workerRequest, Path.GetDirectoryName(cancelPath)!))
                    continue;

                var receiptPath = ReceiptPath(jobRoot, workerRequest.RunId, signal.CommandId);
                if (File.Exists(receiptPath))
                    continue;
                var gate = TaskGates.GetOrAdd(jobRoot, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                try
                {
                    if (File.Exists(receiptPath)) continue;
                    var request = new StageControlRequest(taskId, "speech.playback", "current-active", "user_stop");
                    await PublishReceiptAsync(jobRoot, request, workerRequest, signal.CommandId, signal.RequestedAt, recovered: true, cancellationToken);
                    recovered++;
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        return recovered;
    }

    private static async Task<StageControlReceipt> PublishReceiptAsync(
        string jobRoot,
        StageControlRequest request,
        WorkerRequest workerRequest,
        string controlId,
        DateTimeOffset requestedAt,
        bool recovered,
        CancellationToken cancellationToken)
    {
        var resolution = new StageControlResolution(
            workerRequest.RunId, workerRequest.StageId, workerRequest.AttemptId, workerRequest.OperationId,
            workerRequest.CancelSignal.Path.Replace('\\', '/'));
        var store = new EventStore(jobRoot, workerRequest.RunId);
        var priorEvent = FindControlEvent(store, controlId);
        var eventRef = priorEvent ?? await store.AppendAsync(
            "stage.control_requested",
            request.TaskId,
            new
            {
                controlId,
                request.Operation,
                request.Selector,
                request.Reason,
                resolved = resolution,
                recovered,
                gracePeriodMs = GracePeriodMs
            },
            workerRequest.StageId,
            workerRequest.AttemptId,
            commandId: controlId,
            cancellationToken: cancellationToken);
        var receiptPath = ReceiptPath(jobRoot, workerRequest.RunId, controlId);
        var receiptRef = PathPolicy.ToJobRelative(jobRoot, receiptPath);
        var receipt = new StageControlReceipt(
            ReceiptSchemaVersion, controlId, true, request.TaskId, request.Operation, request.Selector, request.Reason,
            "requested", resolution, requestedAt, eventRef, receiptRef, null);
        if (!File.Exists(receiptPath))
            await AtomicFile.WriteJsonOnceAsync(receiptPath, receipt, cancellationToken);
        return receipt;
    }

    private static string? FindControlEvent(EventStore store, string controlId)
    {
        var match = store.ReadAll().SingleOrDefault(item => item.Type == "stage.control_requested" && item.CommandId == controlId);
        if (match is null) return null;
        var file = Directory.GetFiles(store.EventsDirectory, "*.json")
            .Single(path => JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options)?.EventId == match.EventId);
        return PathPolicy.ToJobRelative(Directory.GetParent(Directory.GetParent(Directory.GetParent(store.EventsDirectory)!.FullName)!.FullName)!.FullName, file);
    }

    private static IReadOnlyList<Candidate> ReadCandidates(string jobRoot, string taskId)
    {
        var runsRoot = Path.Combine(jobRoot, "runs");
        if (!Directory.Exists(runsRoot)) return Array.Empty<Candidate>();
        var output = new List<Candidate>();
        foreach (var requestPath in Directory.GetFiles(runsRoot, "request.json", SearchOption.AllDirectories))
        {
            WorkerRequest? request;
            try { request = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(requestPath), ContractJson.Options); }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { continue; }
            if (request is null || request.SchemaVersion != "1.0.0" || request.TaskId != taskId || request.Operation != "speech.playback" ||
                !ValidIdentifier(request.RunId) || !ValidIdentifier(request.StageId) || !ValidIdentifier(request.AttemptId) || !ValidIdentifier(request.OperationId) ||
                request.CancelSignal.SchemaVersion != "1.0.0" || request.CancelSignal.GracePeriodMs != GracePeriodMs ||
                !IsExactAttemptPath(jobRoot, request, Path.GetDirectoryName(requestPath)!))
                continue;
            var expectedCancel = PathPolicy.ResolveJobRelative(jobRoot, request.CancelSignal.Path);
            var attemptDirectory = Path.GetDirectoryName(requestPath)!;
            if (!expectedCancel.Equals(Path.Combine(attemptDirectory, "cancel.json"), StringComparison.OrdinalIgnoreCase))
                continue;
            output.Add(new Candidate(request, expectedCancel, Path.Combine(attemptDirectory, "result.json"),
                File.Exists(expectedCancel), File.Exists(Path.Combine(attemptDirectory, "result.json"))));
        }
        return output;
    }

    private static bool IsExactAttemptPath(string jobRoot, WorkerRequest request, string attemptDirectory)
    {
        var expected = PathPolicy.ResolveJobRelative(jobRoot,
            $"runs/{request.RunId}/stages/{request.StageId}/attempts/{request.AttemptId}");
        return expected.Equals(Path.GetFullPath(attemptDirectory), StringComparison.OrdinalIgnoreCase) &&
               request.AttemptPath.Replace('\\', '/') == $"runs/{request.RunId}/stages/{request.StageId}/attempts/{request.AttemptId}";
    }

    private static string ReceiptPath(string jobRoot, string runId, string controlId) =>
        PathPolicy.ResolveJobRelative(jobRoot, $"runs/{runId}/controls/{controlId}/receipt.json");

    private static StageControlReceipt Rejected(StageControlRequest request, string code, string category, string message) =>
        new(ReceiptSchemaVersion, $"control-{Guid.NewGuid():N}", false, request.TaskId, request.Operation, request.Selector, request.Reason,
            "rejected", null, DateTimeOffset.UtcNow, null, null, new StageControlError(code, category, false, message));

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private sealed record Candidate(WorkerRequest Request, string CancelPath, string ResultPath, bool CancelExists, bool ResultExists);
}

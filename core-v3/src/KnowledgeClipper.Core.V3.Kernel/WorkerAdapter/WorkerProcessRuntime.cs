using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed class WorkerProcessRuntime : IWorkerRuntime
{
    private readonly IReadOnlyDictionary<string, AdmittedAuthorityWorker> _admitted;
    private readonly IReadOnlyDictionary<string, WorkerBinding> _bindings;
    private readonly CoreWorkerTaskPackageAdapter _adapter;
    private readonly IWorkerProcessInvoker _invoker;
    private int _contentAttempts;
    private int _ankiProbeAttempts;
    private int _ankiExportAttempts;
    private int _ankiExternalWriteCount;
    private int _ankiReadbackCount;
    private int _ankiUnknownWriteAttempts;

    public bool RequiresLegacyQuestionWorkflow => true;

    private WorkerProcessRuntime(
        IReadOnlyDictionary<string, AdmittedAuthorityWorker> admitted,
        IReadOnlyDictionary<string, WorkerBinding> bindings,
        CoreWorkerTaskPackageAdapter adapter,
        IWorkerProcessInvoker invoker)
    {
        _admitted = admitted;
        _bindings = bindings;
        _adapter = adapter;
        _invoker = invoker;
    }

    public static async Task<WorkerProcessRuntime> CreateAsync(
        AdmittedCapabilityCatalog catalog,
        string repositoryRoot,
        string taskPackageRoot,
        CancellationToken cancellationToken,
        IWorkerProcessInvoker? invoker = null)
    {
        var admitted = await FrozenAuthorityWorkerAdmission.AdmitAsync(catalog, repositoryRoot, cancellationToken).ConfigureAwait(false);
        var bindings = catalog.Document.Operations
            .Where(item => item.Available && item.WorkerBinding.Mode == "worker" && item.WorkerBinding.WorkerId is not null)
            .GroupBy(item => item.WorkerBinding.WorkerId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().WorkerBinding, StringComparer.Ordinal);
        return new WorkerProcessRuntime(admitted, bindings, new CoreWorkerTaskPackageAdapter(taskPackageRoot), invoker ?? new SystemWorkerProcessInvoker());
    }

    public async Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workerId = invocation.Binding.WorkerId ?? throw new InvalidDataException("Worker invocation lacks workerId.");
        if (!_admitted.TryGetValue(workerId, out var worker)) throw new InvalidDataException($"Worker '{workerId}' was not authority-admitted.");
        Require(invocation.Binding.Authority?.ExactWorkerVersion == worker.WorkerVersion, $"Worker '{workerId}' invocation version drifted from admission.");
        Require(invocation.Binding.Authority?.ContractVersion == worker.ContractVersion, $"Worker '{workerId}' invocation contract drifted from admission.");

        var phase = RequiredString(invocation.Request, "phase");
        IncrementAttempt(workerId, phase);
        var package = await _adapter.PrepareAsync(invocation, worker, cancellationToken).ConfigureAwait(false);
        WorkerProcessInvocationResult process;
        try
        {
            process = await _invoker.InvokeAsync(worker, package, invocation.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExternalWrite(workerId, phase) && ex is not WorkerExternalStateUnknownException)
        {
            throw UnknownExternalState("worker_process_exception_after_dispatch", $"Anki worker process failed after dispatch; external state is unknown: {ex.Message}");
        }

        if (process.TimedOut || process.Cancelled || process.ExitCode != 0 || !File.Exists(package.ResultPath))
        {
            var detail = process.TimedOut ? "timed out" : process.Cancelled ? "was cancelled" : process.ExitCode != 0 ? $"exited with code {process.ExitCode}" : "did not publish result.json";
            if (IsExternalWrite(workerId, phase)) throw UnknownExternalState("worker_process_incomplete_after_dispatch", $"Anki worker {detail} after dispatch; external state is unknown.");
            if (process.TimedOut) throw new TimeoutException($"Worker '{workerId}' {detail}.");
            if (process.Cancelled) throw new OperationCanceledException($"Worker '{workerId}' {detail}.", cancellationToken);
            throw new InvalidDataException($"Worker '{workerId}' {detail}. See local stderr evidence: {process.StandardErrorPath}");
        }

        var result = await ReadAndValidateResultAsync(package, worker, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Status, "completed", StringComparison.Ordinal) || result.Error is not null)
        {
            var message = result.Error?.Message ?? $"Worker '{workerId}' returned status '{result.Status}'.";
            if (IsExternalWrite(workerId, phase)) throw UnknownExternalState("worker_result_noncompleted_after_dispatch", message);
            throw new InvalidDataException(message);
        }

        return workerId switch
        {
            "content.deepseek" => await ProjectContentAsync(package, result, phase, cancellationToken).ConfigureAwait(false),
            "export.anki" when phase == "probe" => await ProjectAnkiProbeAsync(package, result, RequiredString(invocation.Request, "selectedRoute"), cancellationToken).ConfigureAwait(false),
            "export.anki" when phase == "export" => await ProjectAnkiExportAsync(package, result, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException($"Worker route '{workerId}:{phase}' is outside the Core v3 process boundary.")
        };
    }

    public WorkerRuntimeMetrics GetMetrics() => new(
        Volatile.Read(ref _contentAttempts),
        Volatile.Read(ref _ankiProbeAttempts),
        Volatile.Read(ref _ankiExportAttempts),
        Volatile.Read(ref _ankiExternalWriteCount),
        Volatile.Read(ref _ankiReadbackCount),
        0, 0, 0,
        "authority-bound-worker-process")
    {
        AnkiUnknownWriteAttempts = Volatile.Read(ref _ankiUnknownWriteAttempts)
    };

    public WorkerBinding GetAdmittedBinding(string workerId)
        => _bindings.TryGetValue(workerId, out var binding) ? binding : throw new InvalidDataException($"Worker '{workerId}' is not admitted.");

    private void IncrementAttempt(string workerId, string phase)
    {
        if (workerId == "content.deepseek") Interlocked.Increment(ref _contentAttempts);
        else if (phase == "probe") Interlocked.Increment(ref _ankiProbeAttempts);
        else Interlocked.Increment(ref _ankiExportAttempts);
    }

    private WorkerExternalStateUnknownException UnknownExternalState(string code, string message)
    {
        Interlocked.Increment(ref _ankiUnknownWriteAttempts);
        return new WorkerExternalStateUnknownException(code, message);
    }

    private static async Task<LegacyWorkerResult> ReadAndValidateResultAsync(
        LegacyAttemptPackage package,
        AdmittedAuthorityWorker worker,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(package.ResultPath, cancellationToken).ConfigureAwait(false);
        using var document = StrictJson.Parse(bytes, package.ResultPath);
        var result = document.RootElement.Deserialize<LegacyWorkerResult>(KernelJson.Options)
            ?? throw new InvalidDataException("Worker result is invalid.");
        var request = package.Request;
        Require(result.SchemaVersion == "1.0.0", "Worker result schemaVersion is unsupported.");
        Require(result.RequestId == request.RequestId && result.OperationId == request.OperationId && result.TaskId == request.TaskId,
            "Worker result request/task identity mismatch.");
        Require(result.RunId == request.RunId && result.StageId == request.StageId && result.AttemptId == request.AttemptId,
            "Worker result run/stage/attempt identity mismatch.");
        Require(result.IdempotencyKey.Equals(request.IdempotencyKey, StringComparison.OrdinalIgnoreCase), "Worker result idempotency identity mismatch.");
        Require(result.Worker.Id == worker.WorkerId && result.Worker.Version == worker.WorkerVersion, "Worker result exact worker identity mismatch.");
        Require(result.ContractVersion == worker.ContractVersion, "Worker result contract version mismatch.");
        Require(result.FinishedAt >= result.StartedAt, "Worker result time range is invalid.");
        Require(result.Outputs.Select(item => item.ArtifactRef).Distinct(StringComparer.Ordinal).Count() == result.Outputs.Count,
            "Worker result contains duplicate output artifact identities.");
        foreach (var output in result.Outputs)
        {
            _ = await LoadArtifactAsync(package, output, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<WorkerExecutionResult> ProjectContentAsync(
        LegacyAttemptPackage package,
        LegacyWorkerResult result,
        string phase,
        CancellationToken cancellationToken)
    {
        if (phase == "make-cards")
        {
            var bundle = await LoadSinglePayloadAsync(package, result, "card-bundle", cancellationToken).ConfigureAwait(false);
            var cards = bundle.GetProperty("cards");
            Require(cards.GetArrayLength() == 1, "Core v3 question flow requires exactly one generated card.");
            var card = cards[0];
            var front = JoinTextBlocks(card.GetProperty("front_blocks"));
            var back = JoinTextBlocks(card.GetProperty("back_blocks"));
            Require(front.Length > 0 && back.Length > 0, "Content worker card front/back is empty.");
            return new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "cards_ready", front, back, cardCount = 1, provider = "content.deepseek" }), Array.Empty<ArtifactReference>(), 0, true);
        }

        var answer = await LoadSinglePayloadAsync(package, result, "question.answer", cancellationToken).ConfigureAwait(false);
        var turns = answer.GetProperty("turns");
        Require(turns.GetArrayLength() > 0, "Content worker answer contains no turns.");
        var answerText = RequiredString(turns[turns.GetArrayLength() - 1], "answer_text");
        return new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "waiting_input", answerText, provider = "content.deepseek" }), Array.Empty<ArtifactReference>(), 0, true);
    }

    private static async Task<WorkerExecutionResult> ProjectAnkiProbeAsync(
        LegacyAttemptPackage package,
        LegacyWorkerResult result,
        string selectedRoute,
        CancellationToken cancellationToken)
    {
        Require(IsEmptyArray(result.SideEffects), "Read-only Anki probe reported side effects.");
        var status = await LoadSinglePayloadAsync(package, result, "anki-connection-status", cancellationToken).ConfigureAwait(false);
        Require(RequiredString(status, "schemaVersion") == "anki-connection-status.v1", "Anki probe status schema is unsupported.");
        Require(RequiredString(status, "operation") == "probe", "Anki probe status operation mismatch.");
        Require(status.TryGetProperty("externalMutation", out var mutation) && mutation.ValueKind == JsonValueKind.False,
            "Anki probe reported external mutation.");
        Require(status.TryGetProperty("mutatingActionsObserved", out var actions) && actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() == 0,
            "Anki probe observed mutating actions.");
        var connectionReady = GetOptionalString(status, "permission") == "granted";
        var selectedDecks = status.GetProperty("decks").EnumerateArray()
            .Where(item => GetOptionalString(item, "routeKey") == selectedRoute)
            .ToArray();
        var selectedRouteReady = selectedDecks.Length == 1 && GetOptionalString(selectedDecks[0], "status") == "ready";
        var basicModels = status.GetProperty("models").EnumerateArray()
            .Where(item => GetOptionalString(item, "noteTypeKey") == "basic_qa")
            .ToArray();
        var basicModelReady = basicModels.Length == 1 && GetOptionalString(basicModels[0], "status") == "ready";
        var blockingIssues = new List<string>();
        if (!connectionReady) blockingIssues.Add("anki_connection_not_ready");
        if (!selectedRouteReady) blockingIssues.Add("selected_route_not_ready");
        if (!basicModelReady) blockingIssues.Add("basic_qa_model_not_ready");
        var nonBlockingIssues = status.GetProperty("decks").EnumerateArray()
            .Where(item => GetOptionalString(item, "routeKey") != selectedRoute && GetOptionalString(item, "status") != "ready")
            .Select(item => "route_not_ready:" + RequiredString(item, "routeKey"))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var readyForRequestedAction = blockingIssues.Count == 0;
        var overallState = !readyForRequestedAction ? "blocked" : nonBlockingIssues.Length > 0 ? "partially_ready" : "ready";
        return new WorkerExecutionResult("completed", StrictJson.CloneElement(new
        {
            status = RequiredString(status, "status"), worker = "export.anki", provider = "worker-process",
            externalMutation = false, mutatingActionsObserved = Array.Empty<string>(), externalWriteCount = 0,
            readiness = new
            {
                connectionReady,
                selectedRoute,
                selectedRouteReady,
                readyForRequestedAction,
                overallState,
                nonBlockingIssues,
                blockingIssues = blockingIssues.ToArray()
            }
        }), Array.Empty<ArtifactReference>(), 0, true);
    }

    private async Task<WorkerExecutionResult> ProjectAnkiExportAsync(
        LegacyAttemptPackage package,
        LegacyWorkerResult result,
        CancellationToken cancellationToken)
    {
        var artifact = result.Outputs.SingleOrDefault(item => item.Kind == "anki-export-evidence")
            ?? throw new InvalidDataException("Anki export did not publish exactly one evidence artifact.");
        Require(result.Outputs.Count(item => item.Kind == "anki-export-evidence") == 1, "Anki export evidence cardinality is invalid.");
        var loaded = await LoadArtifactAsync(package, artifact, cancellationToken).ConfigureAwait(false);
        var operations = await ReadNdjsonAsync(package.JobRoot, loaded.Files.Single(item => item.MediaType == "application/x-ndjson" && item.Path.EndsWith("operations.ndjson", StringComparison.Ordinal)), cancellationToken).ConfigureAwait(false);
        var items = await ReadNdjsonAsync(package.JobRoot, loaded.Files.Single(item => item.MediaType == "application/x-ndjson" && item.Path.EndsWith("items.ndjson", StringComparison.Ordinal)), cancellationToken).ConfigureAwait(false);
        var mutationActions = new HashSet<string>(StringComparer.Ordinal) { "addNote", "addNotes", "updateNoteFields", "deleteNotes", "changeDeck" };
        var writes = operations.Count(item => item.TryGetProperty("action", out var action) && mutationActions.Contains(action.GetString() ?? string.Empty));
        Require(writes <= 1, "Anki export exceeded the one-write Core v3 Canary boundary.");
        Require(items.Count == 1, "Anki export must return exact readback evidence for one card.");
        var itemStatus = RequiredString(items[0], "status");
        var exactReadback = itemStatus is "CREATED" or "ALREADY_CURRENT" &&
            operations.Any(item => GetOptionalString(item, "action") == "findNotes") &&
            (writes == 0 || operations.Any(item => GetOptionalString(item, "action") == "notesInfo") && operations.Any(item => GetOptionalString(item, "action") == "cardsInfo"));
        Require(exactReadback, "Anki export exact readback proof is incomplete.");
        Require(result.SideEffects is { ValueKind: JsonValueKind.Array } && result.SideEffects.Value.GetArrayLength() <= 1,
            "Anki export side-effect receipt cardinality is invalid.");
        if (writes > 0) Interlocked.Add(ref _ankiExternalWriteCount, writes);
        Interlocked.Increment(ref _ankiReadbackCount);
        return new WorkerExecutionResult("completed", StrictJson.CloneElement(new
        {
            status = itemStatus == "CREATED" ? "created" : "already_applied",
            worker = "export.anki", provider = "worker-process",
            writeId = "anki-evidence-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifact.ExpectedSha256)))[..16].ToLowerInvariant(),
            exactReadback = true, externalWriteCount = writes
        }), Array.Empty<ArtifactReference>(), writes, true);
    }

    private sealed record ManifestFile(string Path, string Sha256, long Bytes, string MediaType);
    private sealed record LoadedLegacyArtifact(JsonElement Payload, IReadOnlyList<ManifestFile> Files);

    private static async Task<JsonElement> LoadSinglePayloadAsync(
        LegacyAttemptPackage package,
        LegacyWorkerResult result,
        string kind,
        CancellationToken cancellationToken)
    {
        var matches = result.Outputs.Where(item => item.Kind == kind).ToArray();
        Require(matches.Length == 1, $"Worker output requires exactly one '{kind}' artifact.");
        return (await LoadArtifactAsync(package, matches[0], cancellationToken).ConfigureAwait(false)).Payload;
    }

    private static async Task<LoadedLegacyArtifact> LoadArtifactAsync(
        LegacyAttemptPackage package,
        LegacyArtifactReference reference,
        CancellationToken cancellationToken)
    {
        var manifestPath = CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, reference.ManifestPath);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        RequireHash(manifestBytes, reference.ExpectedSha256, $"worker output manifest '{reference.ArtifactRef}'");
        using var document = StrictJson.Parse(manifestBytes, manifestPath);
        var root = document.RootElement;
        Require(RequiredString(root, "artifactId") == reference.ArtifactRef && RequiredString(root, "kind") == reference.Kind &&
                RequiredString(root, "contractVersion") == reference.ContractVersion,
            "Worker output manifest identity mismatch.");
        var producer = root.GetProperty("producer");
        Require(RequiredString(producer, "attemptId") == package.Request.AttemptId && RequiredString(producer, "stageId") == package.Request.StageId,
            "Worker output producer attempt identity mismatch.");
        Require(RequiredString(producer, "workerId") == package.Request.WorkerId, "Worker output producer worker identity mismatch.");
        var files = new List<ManifestFile>();
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var descriptor = new ManifestFile(RequiredString(file, "path"), RequiredString(file, "sha256"), file.GetProperty("bytes").GetInt64(), RequiredString(file, "mediaType"));
            var path = CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, descriptor.Path);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            Require(bytes.LongLength == descriptor.Bytes, $"Worker output file length mismatch: {descriptor.Path}");
            RequireHash(bytes, descriptor.Sha256, $"worker output file '{descriptor.Path}'");
            files.Add(descriptor);
        }
        Require(files.Count > 0, "Worker output manifest contains no files.");
        var entrypoint = RequiredString(root.GetProperty("metadata"), "entrypoint");
        Require(files.Count(item => item.Path == entrypoint) == 1, "Worker output entrypoint is not bound exactly once.");
        var payloadBytes = await File.ReadAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, entrypoint), cancellationToken).ConfigureAwait(false);
        using var payload = StrictJson.Parse(payloadBytes, entrypoint);
        return new LoadedLegacyArtifact(payload.RootElement.Clone(), files);
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadNdjsonAsync(string root, ManifestFile descriptor, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(root, descriptor.Path), cancellationToken).ConfigureAwait(false);
        var values = new List<JsonElement>();
        foreach (var line in lines.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            using var document = StrictJson.Parse(Encoding.UTF8.GetBytes(line), descriptor.Path);
            values.Add(document.RootElement.Clone());
        }
        return values;
    }

    private static string JoinTextBlocks(JsonElement blocks)
        => string.Join("\n", blocks.EnumerateArray().Select(item => RequiredString(item, "text"))).Trim();

    private static bool IsEmptyArray(JsonElement? value) => value is null || value.Value.ValueKind == JsonValueKind.Array && value.Value.GetArrayLength() == 0;
    private static bool IsExternalWrite(string workerId, string phase) => workerId == "export.anki" && phase == "export";
    private static string? GetOptionalString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredString(JsonElement element, string name)
        => GetOptionalString(element, name) is { Length: > 0 } value ? value : throw new InvalidDataException($"Required worker field '{name}' is missing.");
    private static void RequireHash(ReadOnlySpan<byte> bytes, string expected, string label)
        => Require(expected.Length == 64 && expected.All(Uri.IsHexDigit) && Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase), $"SHA-256 mismatch for {label}.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

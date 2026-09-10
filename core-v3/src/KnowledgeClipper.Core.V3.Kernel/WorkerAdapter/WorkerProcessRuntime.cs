using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;
using KnowledgeClipper.Core.V3.Kernel.Application;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed partial class WorkerProcessRuntime : IWorkerRuntime
{
    private readonly IReadOnlyDictionary<string, AdmittedAuthorityWorker> _admitted;
    private readonly IReadOnlyDictionary<string, WorkerBinding> _bindings;
    private readonly CoreWorkerTaskPackageAdapter _adapter;
    private readonly IWorkerProcessInvoker _invoker;
    private readonly string _repositoryRoot;
    private int _contentAttempts;
    private int _ankiProbeAttempts;
    private int _ankiExportAttempts;
    private int _ankiExternalWriteCount;
    private int _ankiReadbackCount;
    private int _ankiUnknownWriteAttempts;

    public bool RequiresLegacyQuestionWorkflow => true;
    public PlaybackAttemptControl? PlaybackControl { get; private set; }

    private WorkerProcessRuntime(
        IReadOnlyDictionary<string, AdmittedAuthorityWorker> admitted,
        IReadOnlyDictionary<string, WorkerBinding> bindings,
        CoreWorkerTaskPackageAdapter adapter,
        IWorkerProcessInvoker invoker,
        string repositoryRoot)
    {
        _admitted = admitted;
        _bindings = bindings;
        _adapter = adapter;
        _invoker = invoker;
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    public static async Task<WorkerProcessRuntime> CreateAsync(
        AdmittedCapabilityCatalog catalog,
        string repositoryRoot,
        string taskPackageRoot,
        CancellationToken cancellationToken,
        IWorkerProcessInvoker? invoker = null,
        IEventStore? playbackEvents = null)
    {
        var admitted = await FrozenAuthorityWorkerAdmission.AdmitAsync(catalog, repositoryRoot, cancellationToken).ConfigureAwait(false);
        var runtimeTools = catalog.Document.Operations.Any(item => item.Available && item.WorkerBinding.WorkerId == "speech.edge")
            ? await AuthorityRuntimeToolAdmission.AdmitAsync(repositoryRoot, cancellationToken).ConfigureAwait(false)
            : null;
        var bindings = catalog.Document.Operations
            .Where(item => item.Available && item.WorkerBinding.Mode == "worker" && item.WorkerBinding.WorkerId is not null)
            .GroupBy(item => item.WorkerBinding.WorkerId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().WorkerBinding, StringComparer.Ordinal);
        return new WorkerProcessRuntime(admitted, bindings, new CoreWorkerTaskPackageAdapter(taskPackageRoot, runtimeTools), invoker ?? new SystemWorkerProcessInvoker(), repositoryRoot)
        { PlaybackControl = new PlaybackAttemptControl(playbackEvents ?? new FileEventStore(Path.Combine(taskPackageRoot, "playback-events"))) };
    }

    public async Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workerId = invocation.Binding.WorkerId ?? throw new InvalidDataException("Worker invocation lacks workerId.");
        if (!_admitted.TryGetValue(workerId, out var worker)) throw new InvalidDataException($"Worker '{workerId}' was not authority-admitted.");
        Require(invocation.Binding.Authority?.ExactWorkerVersion == worker.WorkerVersion, $"Worker '{workerId}' invocation version drifted from admission.");
        Require(invocation.Binding.Authority?.ContractVersion == worker.ContractVersion, $"Worker '{workerId}' invocation contract drifted from admission.");

        var phase = RequiredString(invocation.Request, "phase");
        if (workerId == "export.remnote" && phase is "discover-target" or "probe")
            return await ExecuteRemNotePublicAsync(worker, invocation, cancellationToken).ConfigureAwait(false);
        IncrementAttempt(workerId, phase);
        var package = await _adapter.PrepareAsync(invocation, worker, cancellationToken).ConfigureAwait(false);
        string? playbackToken = null;
        var playbackTerminal = "unknown";
        try
        {
        WorkerProcessInvocationResult process;
        try
        {
            var processTask = _invoker.InvokeAsync(worker, package, invocation.Timeout, cancellationToken);
            if (workerId == "speech.edge" && phase == "playback")
                playbackToken = PlaybackControl!.Register(invocation.OperationId, GetOptionalString(invocation.Request, "actionCommandId") ?? invocation.OperationId, package, processTask);
            process = await processTask.ConfigureAwait(false);
            await WriteExecutionEvidenceAsync(worker, package, process, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExternalWrite(workerId, phase) && ex is not WorkerExternalStateUnknownException)
        {
            throw UnknownExternalState(workerId, "worker_process_exception_after_dispatch", $"Worker '{workerId}' process failed after dispatch; external state is unknown: {ex.Message}");
        }

        if (process.TimedOut || process.Cancelled || process.ExitCode != 0 || !File.Exists(package.ResultPath))
        {
            var detail = process.TimedOut ? "timed out" : process.Cancelled ? "was cancelled" : process.ExitCode != 0 ? $"exited with code {process.ExitCode}" : "did not publish result.json";
            if (IsExternalWrite(workerId, phase)) throw UnknownExternalState(workerId, "worker_process_incomplete_after_dispatch", $"Worker '{workerId}' {detail} after dispatch; external state is unknown.");
            if (process.TimedOut) throw new TimeoutException($"Worker '{workerId}' {detail}.");
            if (process.Cancelled) throw new OperationCanceledException($"Worker '{workerId}' {detail}.", cancellationToken);
            throw new InvalidDataException($"Worker '{workerId}' {detail}. See local stderr evidence: {process.StandardErrorPath}");
        }

        var result = await ReadAndValidateResultAsync(package, worker, cancellationToken).ConfigureAwait(false);
        var userStopped = workerId == "speech.edge" && phase == "playback" && result.Status == "cancelled" && result.Error?.Code == "playback.user_stop";
        if (userStopped)
        {
            Require(File.Exists(package.CancelPath) && result.Error!.DetailsRef == package.Request.CancelSignal.Path,
                "Stopped playback lacks its attempt-local cancel signal proof.");
            using var signal = StrictJson.Parse(await File.ReadAllBytesAsync(package.CancelPath, cancellationToken).ConfigureAwait(false), package.CancelPath);
            Require(RequiredString(signal.RootElement, "reason") == "user_stop" &&
                RequiredString(signal.RootElement, "operationId") == package.Request.OperationId &&
                RequiredString(signal.RootElement, "attemptId") == package.Request.AttemptId,
                "Stopped playback cancellation proof identity mismatch.");
        }
        if ((!string.Equals(result.Status, "completed", StringComparison.Ordinal) || result.Error is not null) && !userStopped)
        {
            var message = result.Error?.Message ?? $"Worker '{workerId}' returned status '{result.Status}'.";
            if (IsExternalWrite(workerId, phase)) throw UnknownExternalState(workerId, "worker_result_noncompleted_after_dispatch", message);
            throw new InvalidDataException(message);
        }

        var projected = workerId switch
        {
            "content.deepseek" => await ProjectContentAsync(package, result, phase, cancellationToken).ConfigureAwait(false),
            "speech.edge" when phase == "synthesize" => await ProjectSpeechSynthesisAsync(package, result, cancellationToken).ConfigureAwait(false),
            "speech.edge" when phase == "playback" => ProjectSpeechPlayback(result),
            "export.anki" when phase == "probe" => await ProjectAnkiProbeAsync(package, result, RequiredString(invocation.Request, "selectedRoute"), cancellationToken).ConfigureAwait(false),
            "export.anki" when phase == "export" => await ProjectAnkiExportAsync(package, result, cancellationToken).ConfigureAwait(false),
            "export.remnote" when phase == "export" => await ProjectRemNoteExportAsync(package, result, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException($"Worker route '{workerId}:{phase}' is outside the Core v3 process boundary.")
        };
        if (workerId == "speech.edge" && phase == "playback") playbackTerminal = RequiredString(projected.Payload, "status");
        return projected;
        }
        finally { if (playbackToken is not null) PlaybackControl!.Complete(playbackToken, playbackTerminal); }
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
        else if (workerId == "export.anki" && phase == "probe") Interlocked.Increment(ref _ankiProbeAttempts);
        else if (workerId == "export.anki" && phase == "export") Interlocked.Increment(ref _ankiExportAttempts);
    }

    private WorkerExternalStateUnknownException UnknownExternalState(string workerId, string code, string message)
    {
        if (workerId == "export.anki") Interlocked.Increment(ref _ankiUnknownWriteAttempts);
        return new WorkerExternalStateUnknownException(code, message);
    }

    private async Task WriteExecutionEvidenceAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        WorkerProcessInvocationResult process,
        CancellationToken cancellationToken)
    {
        var entrypointBytes = await File.ReadAllBytesAsync(worker.Entrypoint, cancellationToken).ConfigureAwait(false);
        var relativeEntrypoint = Path.GetRelativePath(_repositoryRoot, worker.Entrypoint).Replace('\\', '/');
        Require(!relativeEntrypoint.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relativeEntrypoint),
            "Started worker entrypoint is outside the sealed candidate root.");
        var evidence = StrictJson.CloneElement(new
        {
            schema = "kc.worker-execution-evidence/1",
            request = new { package.Request.OperationId, package.Request.TaskId, package.Request.RunId, package.Request.StageId, package.Request.AttemptId, package.Request.WorkerId, package.Request.Operation },
            startedEntrypoint = new
            {
                candidateRelativePath = relativeEntrypoint,
                bytes = entrypointBytes.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(entrypointBytes)),
                workerId = worker.WorkerId,
                workerVersion = worker.WorkerVersion,
                contractVersion = worker.ContractVersion,
                describeVerifiedAtAdmission = true
            },
            process = new { process.ExitCode, process.TimedOut, process.Cancelled, standardErrorPath = Path.GetRelativePath(package.JobRoot, process.StandardErrorPath).Replace('\\', '/') }
        });
        var path = Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "worker-execution.evidence.v1.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, KernelJson.Options);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    internal static async Task<WorkerExecutionResult> ProjectContentAsync(
        LegacyAttemptPackage package,
        LegacyWorkerResult result,
        string phase,
        CancellationToken cancellationToken)
    {
        if (phase is "make-cards" or "review")
        {
            if (phase == "review" && result.Payload is { ValueKind: JsonValueKind.Object } resultPayload &&
                GetOptionalString(resultPayload, "moduleReleaseState") == "unresolved")
            {
                var learning = await LoadSinglePayloadAsync(package, result, "learning-bundle", cancellationToken).ConfigureAwait(false);
                var items = learning.GetProperty("items");
                Require(items.GetArrayLength() == 1 && RequiredString(learning, "release_state") == "unresolved",
                    "Content unresolved result lacks its single immutable LearningBundle evidence.");
                var cleanedText = RequiredString(items[0].GetProperty("cleaning"), "cleaned_text");
                var decisionBundle = await LoadSinglePayloadAsync(package, result, "language-practice-decision", cancellationToken).ConfigureAwait(false);
                Require(RequiredString(decisionBundle, "schema_version") == "language-practice-decision/2.0.0" && RequiredString(decisionBundle, "state") == "requires_human",
                    "Content unresolved result lacks its typed decision/2 evidence.");
                var decisions = decisionBundle.GetProperty("decisions");
                Require(decisions.GetArrayLength() == 1, "Core requires exactly one language target decision.");
                var decision = decisions[0];
                var reasons = decision.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()).ToArray();
                if (reasons.SequenceEqual(new[] { "language_practice_mode_required" }))
                {
                    var category = RequiredString(decision, "category_id");
                    var units = items[0].GetProperty("units");
                    Require(category is "language_zh" or "language_en" && units.GetArrayLength() == 1 &&
                        RequiredString(units[0], "unit_id") == RequiredString(decision, "unit_id") &&
                        RequiredString(units[0], "category_id") == category &&
                        RequiredString(decisionBundle, "task_id") == package.Request.TaskId &&
                        RequiredString(decisionBundle, "attempt_id") == package.Request.AttemptId,
                        "Language mode decision lacks matching producer/classification evidence.");
                    var allowedModes = decision.GetProperty("allowed_modes").EnumerateArray().Select(value => value.GetString()).ToArray();
                    Require(allowedModes.SequenceEqual(new[] { "meaning-reading", "writing", "both" }), "Language decision modes drifted.");
                    return new WorkerExecutionResult("completed", StrictJson.CloneElement(new
                    {
                        status = "language_choice_required", moduleReleaseState = "unresolved", cleanedText,
                        languageDecision = new
                        {
                            categoryId = category, inputVersion = RequiredString(decisionBundle, "input_version"),
                            selectedTextSha256 = RequiredString(decision, "selected_text_sha256"), targetTextSha256 = RequiredString(decision, "target_text_sha256"),
                            sourceOperationId = package.Request.OperationId, sourceJobRoot = package.JobRoot,
                            sourceRequestPath = package.Request.AttemptPath + "/request.json", sourceRequestSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package.RequestPath, cancellationToken).ConfigureAwait(false))).ToLowerInvariant(),
                            sourceResultPath = package.Request.ResultPath, sourceResultSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package.ResultPath, cancellationToken).ConfigureAwait(false))).ToLowerInvariant(),
                            inputReference = package.Request.InputRefs.Single(item => item.Kind == "content-input"),
                            learningReference = result.Outputs.Single(item => item.Kind == "learning-bundle"),
                            decisionReference = result.Outputs.Single(item => item.Kind == "language-practice-decision"),
                            decision = decision.Clone(), classification = units[0].Clone()
                        }
                    }), Array.Empty<ArtifactReference>(), 0, true);
                }
                return new WorkerExecutionResult("completed", StrictJson.CloneElement(new
                {
                    status = "card_resolution_required", moduleReleaseState = "unresolved", cleanedText,
                    reasonCode = decision.GetProperty("reasons")[0].GetString(), evidence = new { learningBundleSha256 = result.Outputs.Single(item => item.Kind == "learning-bundle").ExpectedSha256, decisionBundleSha256 = result.Outputs.Single(item => item.Kind == "language-practice-decision").ExpectedSha256, decisionId = RequiredString(decision, "decision_id"), targetToken = RequiredString(decision, "target_token"), inputVersion = RequiredString(decisionBundle, "input_version"), candidatePositions = decision.GetProperty("candidate_positions"), suggestedUserFields = decision.GetProperty("suggested_user_fields"), warnings = result.Warnings }
                }), Array.Empty<ArtifactReference>(), 0, true);
            }
            var bundle = await LoadSinglePayloadAsync(package, result, "card-bundle", cancellationToken).ConfigureAwait(false);
            var cards = bundle.GetProperty("cards");
            Require(cards.GetArrayLength() is >= 1 and <= 2, "Core v3 flow requires one or two generated cards.");
            var projectedCards = cards.EnumerateArray().Select((card, index) =>
            {
                var front = JoinTextBlocks(card.GetProperty("front_blocks"));
                var back = JoinTextBlocks(card.GetProperty("back_blocks"));
                Require(front.Length > 0 && back.Length > 0, "Content worker card front/back is empty.");
                return new { cardId = GetOptionalString(card, "card_id") ?? $"card-{index + 1}", template = RequiredString(card, "template"), front, back };
            }).ToArray();
            if (phase == "review")
            {
                var learning = await LoadSinglePayloadAsync(package, result, "learning-bundle", cancellationToken).ConfigureAwait(false);
                var items = learning.GetProperty("items");
                Require(items.GetArrayLength() == 1, "Content review requires exactly one learning item.");
                var cleanedText = RequiredString(items[0].GetProperty("cleaning"), "cleaned_text");
                Require(RequiredString(learning, "release_state") == "ready", "Content review is not ready for export.");
                return new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "cards_ready", cleanedText, cards = projectedCards, cardCount = projectedCards.Length, provider = "content.deepseek" }), Array.Empty<ArtifactReference>(), 0, true);
            }
            return new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "cards_ready", cards = projectedCards, cardCount = projectedCards.Length, provider = "content.deepseek" }), Array.Empty<ArtifactReference>(), 0, true);
        }

        var answer = await LoadSinglePayloadAsync(package, result, "question.answer", cancellationToken).ConfigureAwait(false);
        var turns = answer.GetProperty("turns");
        Require(turns.GetArrayLength() > 0, "Content worker answer contains no turns.");
        var answerText = RequiredString(turns[turns.GetArrayLength() - 1], "answer_text");
        return new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "waiting_input", answerText, provider = "content.deepseek" }), Array.Empty<ArtifactReference>(), 0, true);
    }

    private static async Task<WorkerExecutionResult> ProjectSpeechSynthesisAsync(
        LegacyAttemptPackage package,
        LegacyWorkerResult result,
        CancellationToken cancellationToken)
    {
        var audio = result.Outputs.SingleOrDefault(item => item.Kind == "audio.speech")
            ?? throw new InvalidDataException("Speech synthesis did not publish audio.speech.");
        Require(result.Outputs.Count(item => item.Kind == "audio.speech") == 1, "Speech synthesis published duplicate audio artifacts.");
        _ = await LoadArtifactAsync(package, audio, cancellationToken).ConfigureAwait(false);
        return new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = "synthesized", audioArtifact = audio }), Array.Empty<ArtifactReference>(), 0, true);
    }

    internal static WorkerExecutionResult ProjectSpeechPlayback(LegacyWorkerResult result)
    {
        Require(IsEmptyArray(result.SideEffects), "Speech playback must not report external writes.");
        var state = result.Payload is { } payload ? GetOptionalString(payload, "playbackState") : null;
        var stopped = result.Status == "cancelled" && result.Error?.Code == "playback.user_stop" && state == "stopped";
        Require(stopped || result.Status == "completed" && result.Error is null && state == "idle_replayable", "Speech playback lacks a verified natural/stopped terminal.");
        return new WorkerExecutionResult("completed", StrictJson.CloneElement(new { status = stopped ? "stopped" : "idle_replayable", naturalCompletion = !stopped }), Array.Empty<ArtifactReference>(), 0, true);
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
        var expectedCount = package.Request.Payload.GetProperty("expectedCardCount").GetInt32();
        var expectedCards = package.Request.Payload.GetProperty("expectedCards").EnumerateArray().ToArray();
        var expectedDeck = RequiredString(package.Request.Payload, "expectedDeck");
        var itemStatuses = items.Select(item => RequiredString(item, "status")).ToArray();
        var createdCount = itemStatuses.Count(status => status == "CREATED");
        var exactReadback = itemStatuses.All(status => status is "CREATED" or "ALREADY_CURRENT") &&
            operations.Any(item => GetOptionalString(item, "action") == "findNotes") &&
            (writes == 0 || operations.Any(item => GetOptionalString(item, "action") == "notesInfo") && operations.Any(item => GetOptionalString(item, "action") == "cardsInfo"));
        Require(exactReadback, "Anki export exact readback proof is incomplete.");
        Require(result.SideEffects is { ValueKind: JsonValueKind.Array } && result.SideEffects.Value.GetArrayLength() == 1,
            "Anki export must publish exactly one operation-level verified side-effect receipt.");
        Require(loaded.Metadata.TryGetProperty("readback", out var metadataReadback) && metadataReadback.ValueKind == JsonValueKind.Object,
            "Anki export evidence is missing SHA-bound semantic readback metadata.");
        var readbackPath = RequiredString(metadataReadback, "path");
        var readbackSha256 = RequiredString(metadataReadback, "sha256");
        Require(RequiredString(metadataReadback, "schemaVersion") == "anki-export-readback.v1", "Anki readback metadata schema is unsupported.");
        var readbackFile = loaded.Files.SingleOrDefault(item => item.Path == readbackPath);
        Require(readbackFile is not null && readbackFile.Sha256.Equals(readbackSha256, StringComparison.OrdinalIgnoreCase),
            "Anki readback metadata is not bound to an immutable artifact file.");
        var readbackBytes = await File.ReadAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, readbackPath), cancellationToken).ConfigureAwait(false);
        RequireHash(readbackBytes, readbackSha256, "Anki semantic readback");
        using var readbackDocument = StrictJson.Parse(readbackBytes, readbackPath);
        var semanticReadback = readbackDocument.RootElement.Clone();
        Require(RequiredString(semanticReadback, "schemaVersion") == "anki-export-readback.v1", "Anki semantic readback schema is unsupported.");
        var readbackCards = semanticReadback.GetProperty("cards").EnumerateArray().ToArray();
        var cardsHash = RequiredString(semanticReadback, "cardsSha256");
        ValidateAnkiCardinality(expectedCount, expectedCards.Length, items.Count, readbackCards.Length, writes, createdCount, exporterInvocationCount: 1, operationLevelSideEffectCount: 1);
        for (var index = 0; index < readbackCards.Length; index++)
        {
            var semanticCard = readbackCards[index];
            var expectedCard = expectedCards[index];
            var notesInfo = semanticCard.GetProperty("notesInfo");
            var cardsInfo = semanticCard.GetProperty("cardsInfo");
            Require(notesInfo.GetProperty("noteId").ValueKind == JsonValueKind.Number &&
                notesInfo.GetProperty("cardIds").ValueKind == JsonValueKind.Array && notesInfo.GetProperty("cardIds").GetArrayLength() > 0 &&
                notesInfo.GetProperty("fields").ValueKind == JsonValueKind.Object &&
                !string.IsNullOrWhiteSpace(RequiredString(notesInfo.GetProperty("fields"), "Front")) &&
                !string.IsNullOrWhiteSpace(RequiredString(notesInfo.GetProperty("fields"), "Back")) &&
                cardsInfo.ValueKind == JsonValueKind.Array && cardsInfo.GetArrayLength() > 0 &&
                cardsInfo[0].GetProperty("cardId").ValueKind == JsonValueKind.Number && cardsInfo[0].GetProperty("noteId").ValueKind == JsonValueKind.Number &&
                !string.IsNullOrWhiteSpace(RequiredString(cardsInfo[0], "deckName")),
                "Anki semantic readback lacks exact note/card/Front/Back/deck evidence.");
            ValidateAnkiSemanticCard(expectedCard, semanticCard, expectedDeck);
        }
        if (writes > 0) Interlocked.Add(ref _ankiExternalWriteCount, writes);
        Interlocked.Increment(ref _ankiReadbackCount);
        return new WorkerExecutionResult("completed", StrictJson.CloneElement(new
        {
            status = itemStatuses.Any(status => status == "CREATED") ? "created" : "already_applied",
            worker = "export.anki", provider = "worker-process",
            writeId = "anki-evidence-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifact.ExpectedSha256)))[..16].ToLowerInvariant(),
            exactReadback = true, externalWriteCount = writes,
            safetyMetrics = new { exporterInvocationCount = 1, logicalCommittedCardCount = expectedCount, lowLevelMutatingApiActionCount = writes, operationLevelSideEffectCount = 1 },
            semanticReadback = new { artifactPath = readbackPath, sha256 = readbackSha256, cardsSha256 = cardsHash, cards = readbackCards }
        }), Array.Empty<ArtifactReference>(), writes, true);
    }

    internal static void ValidateAnkiCardinality(int expectedCount, int expectedCardsCount, int itemCount, int readbackCount, int mutationActions, int createdCount, int exporterInvocationCount, int operationLevelSideEffectCount)
    {
        Require(expectedCount is >= 1 and <= 2 && expectedCardsCount == expectedCount,
            "Anki export request lacks its confirmed immutable draft cardinality.");
        Require(itemCount == expectedCount, "Anki export item count differs from the confirmed immutable draft.");
        Require(readbackCount == expectedCount, "Anki semantic readback card cardinality differs from the confirmed immutable draft.");
        Require(mutationActions == createdCount && mutationActions <= expectedCount,
            "Anki mutating action count differs from the number of newly created confirmed cards.");
        Require(exporterInvocationCount == 1, "Anki confirmation must execute exactly one exporter invocation.");
        Require(operationLevelSideEffectCount == 1, "Anki confirmation must publish exactly one operation-level side-effect receipt.");
    }

    internal static void ValidateAnkiSemanticCard(JsonElement expectedCard, JsonElement semanticCard, string expectedDeck)
    {
        Require(RequiredString(semanticCard, "sourceCardId") == RequiredString(expectedCard, "cardId"),
            "Anki semantic readback card identity/order drifted from the confirmed draft.");
        var notesInfo = semanticCard.GetProperty("notesInfo");
        var cardsInfo = semanticCard.GetProperty("cardsInfo");
        Require(RequiredString(notesInfo.GetProperty("fields"), "Front") == RequiredString(expectedCard, "front") &&
            RequiredString(notesInfo.GetProperty("fields"), "Back") == RequiredString(expectedCard, "back") &&
            cardsInfo.EnumerateArray().All(card => RequiredString(card, "deckName") == expectedDeck),
            "Anki semantic readback content/deck differs from the confirmed immutable draft.");
    }

    private sealed record ManifestFile(string Path, string Sha256, long Bytes, string MediaType);
    private sealed record LoadedLegacyArtifact(JsonElement Payload, IReadOnlyList<ManifestFile> Files, JsonElement Metadata);

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
        // Common Artifact manifests for binary speech output have one immutable
        // audio file but deliberately no JSON entrypoint.  Accept that narrow,
        // independently hash-verified shape without relaxing JSON artifact
        // entrypoint requirements for any other output kind.
        var declaredEntrypoint = root.TryGetProperty("metadata", out var metadata) ? GetOptionalString(metadata, "entrypoint") : null;
        if (reference.Kind == "audio.speech" && files.Count == 1 && files[0].MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && declaredEntrypoint is null)
        {
            return new LoadedLegacyArtifact(StrictJson.CloneElement(new { }), files, metadata.ValueKind == JsonValueKind.Object ? metadata.Clone() : StrictJson.CloneElement(new { }));
        }
        // Common artifact v1 does not require metadata.entrypoint.  A sole,
        // hash-bound JSON file is unambiguous; multi-file JSON artifacts remain
        // fail-closed unless their producer declares one exact entrypoint.
        var entrypoint = declaredEntrypoint ?? (files.Count == 1 && files[0].MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            ? files[0].Path
            : throw new InvalidDataException("Worker output requires an explicit entrypoint unless it has exactly one JSON payload file."));
        Require(files.Count(item => item.Path == entrypoint) == 1, "Worker output entrypoint is not bound exactly once.");
        var payloadBytes = await File.ReadAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, entrypoint), cancellationToken).ConfigureAwait(false);
        using var payload = StrictJson.Parse(payloadBytes, entrypoint);
        return new LoadedLegacyArtifact(payload.RootElement.Clone(), files, metadata.ValueKind == JsonValueKind.Object ? metadata.Clone() : StrictJson.CloneElement(new { }));
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
    private static bool IsExternalWrite(string workerId, string phase) => workerId is "export.anki" or "export.remnote" && phase == "export";
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

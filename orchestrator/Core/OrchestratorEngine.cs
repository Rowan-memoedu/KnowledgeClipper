using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed class OrchestratorEngine
{
    private readonly WorkerProcessRunner workerRunner = new();

    public async Task<string> RunAsync(
        string jobRoot,
        PipelinePlan plan,
        WorkerRegistry registry,
        string releaseRoot,
        string runId,
        CancellationToken cancellationToken)
    {
        jobRoot = Path.GetFullPath(jobRoot);
        ValidatePlan(plan, registry);
        var eventStore = new EventStore(jobRoot, runId);
        await AppendStateAsync(eventStore, plan.TaskId, "validating", cancellationToken);
        await AppendStateAsync(eventStore, plan.TaskId, "queued", cancellationToken);
        await AppendStateAsync(eventStore, plan.TaskId, "running", cancellationToken);

        var completed = new Dictionary<string, WorkerResult>(StringComparer.Ordinal);
        var pending = plan.Stages.ToDictionary(stage => stage.StageId, StringComparer.Ordinal);
        var cancelPath = Path.Combine(jobRoot, "runs", runId, "cancel-requested.json");
        using var cancelMonitor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancellationReason = new CancellationReasonState();
        var monitorTask = MonitorCancelAsync(cancelPath, cancelMonitor, cancellationReason);

        try
        {
            while (pending.Count > 0)
            {
                cancelMonitor.Token.ThrowIfCancellationRequested();
                var ready = pending.Values
                    .Where(stage => stage.DependsOn.All(completed.ContainsKey))
                    .Where(stage => stage.DependsOn.All(dependency =>
                        StageSatisfied(jobRoot, runId, plan.Stages.Single(item => item.StageId == dependency), completed[dependency])))
                    .ToArray();
                if (ready.Length == 0)
                    break;

                using var concurrency = new SemaphoreSlim(plan.MaxConcurrency, plan.MaxConcurrency);
                var tasks = ready.Select(async stage =>
                {
                    await concurrency.WaitAsync(cancelMonitor.Token);
                    try
                    {
                        var worker = registry.Workers.Single(item => item.WorkerId == stage.WorkerId);
                        return (stage.StageId, Result: await RunStageAsync(jobRoot, plan.TaskId, runId, stage, worker, releaseRoot, eventStore, completed, cancellationReason, cancelMonitor.Token));
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }).ToArray();

                foreach (var item in await Task.WhenAll(tasks))
                {
                    completed[item.StageId] = item.Result;
                    pending.Remove(item.StageId);
                }
                var decisions = completed.Values.Where(IsLanguagePracticeDecisionResult).ToArray();
                if (decisions.Length > 1)
                    throw new InvalidDataException("A pipeline run cannot expose more than one active language-practice decision.");
                var questionAnswers = completed.Values.Where(IsQuestionAnswerResult).ToArray();
                if (questionAnswers.Length > 1)
                    throw new InvalidDataException("A pipeline run cannot expose more than one question answer.");
                if (decisions.Length == 1 && questionAnswers.Length == 1)
                    throw new InvalidDataException("A pipeline run cannot request language input and publish a question answer simultaneously.");
                if (decisions.Length == 1 || questionAnswers.Length == 1)
                {
                    await AppendStateAsync(eventStore, plan.TaskId, "waiting_input", CancellationToken.None);
                    return "waiting_input";
                }
                var contentAttention = completed.Values.Where(IsContentAttentionRequiredResult).ToArray();
                if (contentAttention.Length > 1)
                    throw new InvalidDataException("A pipeline run cannot expose more than one unresolved Content result.");
                if (contentAttention.Length == 1)
                {
                    await AppendStateAsync(eventStore, plan.TaskId, "waiting_input", CancellationToken.None);
                    return "waiting_input";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationReason.Get() == "timeout")
            {
                await AppendStateAsync(eventStore, plan.TaskId, "failed", CancellationToken.None);
                return "failed";
            }
            await AppendStateAsync(eventStore, plan.TaskId, "cancelling", CancellationToken.None);
            await AppendStateAsync(eventStore, plan.TaskId, "cancelled", CancellationToken.None);
            return "cancelled";
        }
        finally
        {
            cancelMonitor.Cancel();
            try { await monitorTask; } catch (OperationCanceledException) { }
        }

        var satisfied = completed.Count(item => StageSatisfied(jobRoot, runId, plan.Stages.Single(stage => stage.StageId == item.Key), item.Value));
        var partials = completed.Values.Count(result => result.Status == "partial");
        var failed = completed.Count(item => !StageSatisfied(jobRoot, runId, plan.Stages.Single(stage => stage.StageId == item.Key), item.Value) && item.Value.Status is "failed" or "cancelled" or "unknown_external_state") + pending.Count;
        var terminalBoundary = plan.TerminalBoundary;
        if (terminalBoundary is not null && failed == 0 && partials == 0 && satisfied == plan.Stages.Count)
        {
            var available = completed.Values.SelectMany(result => result.Outputs)
                .Where(reference => terminalBoundary.AvailableOutputKinds.Contains(reference.Kind, StringComparer.Ordinal))
                .ToArray();
            if (available.Count(reference => terminalBoundary.AvailableOutputKinds.Contains(reference.Kind, StringComparer.Ordinal)) == 0)
                throw new InvalidDataException("Terminal boundary declared no available immutable outputs.");
            await eventStore.AppendAsync("pipeline.terminal_boundary", plan.TaskId, new
            {
                terminalBoundary.Status,
                terminalBoundary.Code,
                terminalBoundary.AvailableOutputKinds,
                terminalBoundary.UnsupportedConsumers,
                outputs = available
            }, cancellationToken: CancellationToken.None);
        }
        var finalState = terminalBoundary is not null && failed == 0 && partials == 0 && satisfied == plan.Stages.Count
            ? "partial"
            : failed == 0 && partials == 0 && satisfied == plan.Stages.Count
            ? "succeeded"
            : satisfied + partials > 0
                ? "partial"
                : "failed";
        await AppendStateAsync(eventStore, plan.TaskId, finalState, CancellationToken.None);
        return finalState;
    }

    public async Task<int> RecoverAsync(string jobRoot, WorkerRegistry registry, string runId, CancellationToken cancellationToken)
    {
        jobRoot = Path.GetFullPath(jobRoot);
        var eventStore = new EventStore(jobRoot, runId);
        var attemptsRoot = Path.Combine(jobRoot, "runs", runId, "stages");
        if (!Directory.Exists(attemptsRoot))
            return 0;
        var recovered = 0;
        var diagnosedEvidence = eventStore.ReadAll()
            .Where(item => item.Type is "stage.recovery_invalid_request" or "stage.recovery_invalid_terminal")
            .Select(item => $"{item.Type}\u001f{item.StageId}\u001f{item.AttemptId}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var requestPath in Directory.GetFiles(attemptsRoot, "request.json", SearchOption.AllDirectories))
        {
            var attemptDirectory = Path.GetDirectoryName(requestPath)!;
            var resultPath = Path.Combine(attemptDirectory, "result.json");
            WorkerRequest request;
            try
            {
                request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken), ContractJson.Options)
                    ?? throw new InvalidDataException($"Invalid abandoned request: {requestPath}");
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The request itself is immutable attempt evidence. A damaged one
                // cannot be retried safely, but must not block the other attempts.
                var attemptId = Path.GetFileName(attemptDirectory);
                var stageId = Directory.GetParent(attemptDirectory)?.Parent?.Name ?? "unknown-stage";
                if (!diagnosedEvidence.Add($"stage.recovery_invalid_request\u001f{stageId}\u001f{attemptId}"))
                    continue;
                await eventStore.AppendAsync(
                    "stage.recovery_invalid_request",
                    Path.GetFileName(jobRoot),
                    new { status = "failed", errorCode = "recovery.invalid_attempt_request", requestRef = PathPolicy.ToJobRelative(jobRoot, requestPath) },
                    stageId,
                    attemptId,
                    cancellationToken: cancellationToken);
                recovered++;
                continue;
            }
            if (File.Exists(resultPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(resultPath, cancellationToken), ContractJson.Options)
                        ?? throw new InvalidDataException("Existing terminal result is empty.");
                    WorkerProcessRunner.ValidateResultIdentity(request, request.WorkerId, existing);
                    await WorkerProcessRunner.ValidatePublishedOutputsAsync(jobRoot, existing, cancellationToken);
                    continue;
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
                {
                    // Keep the invalid terminal file as immutable evidence.  Recovery
                    // cannot overwrite it, but it can append a durable diagnostic so
                    // the host projects the interrupted run to a safe failed state.
                    if (!diagnosedEvidence.Add($"stage.recovery_invalid_terminal\u001f{request.StageId}\u001f{request.AttemptId}"))
                        continue;
                    await eventStore.AppendAsync(
                        "stage.recovery_invalid_terminal",
                        request.TaskId,
                        new { status = "failed", errorCode = "recovery.invalid_terminal_result", resultRef = PathPolicy.ToJobRelative(jobRoot, resultPath) },
                        request.StageId,
                        request.AttemptId,
                        cancellationToken: cancellationToken);
                    recovered++;
                    continue;
                }
            }
            var worker = registry.Workers.SingleOrDefault(item => item.WorkerId == request.WorkerId);
            // A run may outlive the registry revision that admitted it.  Without the
            // original worker's effect/idempotency declaration, automatic retry is
            // unsafe, so recovery must fail closed instead of aborting every run.
            var registryMissing = worker is null;
            var unknown = worker is null || (worker.SideEffectLevel == "external" && (worker.IdempotencyLevel is "reconcilable" or "non_idempotent"));
            var status = unknown ? "unknown_external_state" : "failed";
            var error = new WorkerError(
                registryMissing ? "effect.worker_registry_missing" : unknown ? "effect.abandoned_unknown" : "internal.abandoned_attempt",
                unknown ? "effect" : "internal",
                !unknown && (worker!.IdempotencyLevel is "pure" or "idempotent"),
                PathPolicy.ToJobRelative(jobRoot, requestPath),
                registryMissing
                    ? "The orchestrator recovered an attempt whose worker is absent from the current registry; external state is unknown."
                    : "The orchestrator recovered an attempt without a terminal result.",
                "orchestrator");
            var result = new WorkerResult(
                "1.0.0", request.RequestId, request.OperationId, request.TaskId, request.RunId, request.StageId,
                request.AttemptId, new WorkerIdentity(request.WorkerId, "0.0.0-unknown"), "1.0.0",
                request.IdempotencyKey, status, Array.Empty<ArtifactReference>(), error,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new[] { registryMissing ? "Recovered abandoned attempt with a worker missing from the current registry." : "Recovered abandoned attempt." });
            await AtomicFile.WriteJsonOnceAsync(resultPath, result, cancellationToken);
            await eventStore.AppendAsync("stage.recovered", request.TaskId, new { status }, request.StageId, request.AttemptId, cancellationToken: cancellationToken);
            recovered++;
        }
        return recovered;
    }

    private async Task<WorkerResult> RunStageAsync(
        string jobRoot,
        string taskId,
        string runId,
        PipelineStage stage,
        WorkerEntry worker,
        string releaseRoot,
        EventStore eventStore,
        IReadOnlyDictionary<string, WorkerResult> completed,
        CancellationReasonState cancellationReason,
        CancellationToken cancellationToken)
    {
        ResolvedStageInput resolved;
        try
        {
            resolved = await new StageInputResolver().ResolveAsync(jobRoot, runId, taskId, stage, completed, cancellationToken);
        }
        catch (OrchestratorDispatchException exception) when (exception.Code.StartsWith("binding.", StringComparison.Ordinal))
        {
            return await PublishBindingFailureAsync(jobRoot, taskId, runId, stage, worker, eventStore, exception, cancellationToken);
        }
        var activePayload = resolved.Payload;
        WorkerResult? priorPartial = null;
        var partialContinuations = 0;
        for (var attemptNumber = 1; attemptNumber <= stage.RetryPolicy.MaxAttempts; attemptNumber++)
        {
            var resolvedStage = stage with { InputRefs = resolved.InputRefs, Payload = activePayload };
            var idempotencyKey = ComputeIdempotencyKey(taskId, resolvedStage, worker);
            var attemptId = $"attempt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var attemptPath = $"runs/{runId}/stages/{stage.StageId}/attempts/{attemptId}";
            var requestId = $"request-{Guid.NewGuid():N}";
            var operationId = $"operation-{Guid.NewGuid():N}";
            var requestPayload = resolved.AddRequestIdentity
                ? StageInputResolver.AddRequestIdentity(activePayload, requestId, operationId)
                : activePayload;
            var request = new WorkerRequest(
                "1.0.0",
                requestId,
                operationId,
                taskId,
                runId,
                stage.StageId,
                attemptId,
                worker.WorkerId,
                stage.Operation,
                idempotencyKey,
                attemptPath,
                attemptPath + "/outputs",
                attemptPath + "/progress.ndjson",
                attemptPath + "/result.json",
                new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000),
                resolved.InputRefs,
                stage.ConfigSnapshot,
                requestPayload);

            var reused = await TryReuseAsync(jobRoot, request, worker, stage.OutputKinds, cancellationToken);
            if (reused is not null)
            {
                await eventStore.AppendAsync("stage.reused", taskId, new { idempotencyKey }, stage.StageId, attemptId, cancellationToken: cancellationToken);
                return reused;
            }

            await eventStore.AppendAsync("stage.started", taskId, new { workerId = worker.WorkerId, attemptNumber }, stage.StageId, attemptId, cancellationToken: cancellationToken);
            var execution = await workerRunner.RunAsync(worker, releaseRoot, jobRoot, request, TimeSpan.FromMilliseconds(stage.TimeoutMs), cancellationReason, cancellationToken);
            await eventStore.AppendAsync("stage.finished", taskId, new { execution.Result.Status, execution.ExitCode, execution.TimedOut, execution.Cancelled }, stage.StageId, attemptId, cancellationToken: CancellationToken.None);

            if (execution.Result.Status == "completed" && worker.WorkerId == "image.ocr.mineru" &&
                stage.OutputKinds.SequenceEqual(new[] { "image.original" }))
            {
                var mediaBundle = await PublishOriginalMediaBundleAsync(jobRoot, request, execution.Result, eventStore, cancellationToken);
                execution = execution with { Result = execution.Result with { Outputs = execution.Result.Outputs.Concat(new[] { mediaBundle }).ToArray() } };
            }

            if (priorPartial is not null)
                await ValidateFailedItemsOnlyContinuationAsync(jobRoot, priorPartial, execution.Result, cancellationToken);

            if (await TryPublishLanguagePracticeDecisionAsync(jobRoot, request, execution.Result, eventStore, cancellationToken))
                return execution.Result;

            if (await TryPublishQuestionAnswerAsync(jobRoot, request, execution.Result, eventStore, cancellationToken))
                return execution.Result;

            if (execution.Result.Status == "completed" && worker.WorkerId == "content.deepseek" &&
                !execution.Result.Outputs.Any(output => output.Kind is "language-practice-decision" or "question.answer"))
            {
                try
                {
                    var learningReferences = execution.Result.Outputs.Where(output => output.Kind == "learning-bundle").ToArray();
                    if (learningReferences.Length != 1)
                        throw new OrchestratorDispatchException("binding.completed_output_missing", "Content result requires exactly one LearningBundle.");
                    var releaseState = await StageInputResolver.ReadValidatedLearningReleaseStateAsync(jobRoot, learningReferences[0], cancellationToken);
                    if (execution.Result.Payload is JsonElement resultPayload && resultPayload.ValueKind == JsonValueKind.Object &&
                        resultPayload.TryGetProperty("moduleReleaseState", out var declaredState) &&
                        (declaredState.ValueKind != JsonValueKind.String || declaredState.GetString() != releaseState))
                        throw new OrchestratorDispatchException("binding.content_release_state_mismatch", "WorkerResult moduleReleaseState does not match the immutable LearningBundle.");
                    if (releaseState != "ready")
                    {
                        OcrHumanResolutionSession? ocrResolution = null;
                        if (releaseState == "unresolved" && learningReferences[0].ContractVersion == "2.0.0")
                            ocrResolution = await PublishOcrHumanResolutionSessionAsync(
                                jobRoot, request, execution.Result, learningReferences[0], cancellationToken);
                        await eventStore.AppendAsync("stage.content_attention_required", taskId, new
                        {
                            releaseState,
                            learningBundle = learningReferences[0],
                            decisionKind = ocrResolution is null ? null : "ocr-human-resolution",
                            targetToken = ocrResolution?.TargetToken,
                            resolutionSessionRef = ocrResolution is null ? null : request.AttemptPath + "/ocr-resolution-session.json",
                            originalTextRef = ocrResolution?.OriginalTextRef,
                            candidateTextRef = ocrResolution?.CandidateTextRef,
                            sourceProofreadArtifact = ocrResolution?.SourceProofreadArtifact,
                            sourceImageProvenanceArtifact = ocrResolution?.SourceImageProvenanceArtifact,
                            sourceOriginalImageArtifact = ocrResolution?.SourceOriginalImageArtifact,
                            diff = ocrResolution?.Diff,
                            reason = ocrResolution?.Reason,
                            allowedActions = ocrResolution?.AllowedActions,
                            downstreamBlocked = true,
                            automaticRetry = false
                        }, stage.StageId, attemptId, cancellationToken: cancellationToken);
                        execution = execution with
                        {
                            Result = execution.Result with
                            {
                                Status = "partial",
                                Warnings = execution.Result.Warnings.Concat(new[] { $"Core blocked downstream because LearningBundle release_state={releaseState}." }).ToArray()
                            }
                        };
                    }
                }
                catch (OrchestratorDispatchException exception)
                {
                    return await PublishBindingFailureAsync(jobRoot, taskId, runId, stage, worker, eventStore, exception, CancellationToken.None);
                }
            }

            if (execution.Result.Status == "completed")
            {
                var missing = MissingRequiredOutputs(stage.OutputKinds, execution.Result);
                if (missing.Count > 0)
                    return await PublishBindingFailureAsync(
                        jobRoot, taskId, runId, stage, worker, eventStore,
                        new OrchestratorDispatchException("binding.completed_output_missing", $"Completed worker result omitted required output kinds: {string.Join(", ", missing)}"),
                        CancellationToken.None);
            }

            if (execution.Result.Status == "partial" && stage.PartialRetryPolicy is not null &&
                partialContinuations < stage.PartialRetryPolicy.MaxContinuationAttempts && attemptNumber < stage.RetryPolicy.MaxAttempts &&
                TryBuildFailedItemsOnlyPayload(jobRoot, stage, execution.Result, activePayload, out var continuationPayload, out var failedItemIds))
            {
                partialContinuations++;
                priorPartial = execution.Result;
                activePayload = continuationPayload;
                await eventStore.AppendAsync("stage.partial_continuation", taskId, new
                {
                    strategy = stage.PartialRetryPolicy.Strategy,
                    sourceAttemptId = execution.Result.AttemptId,
                    failedItemIds,
                    reusedItemIds = execution.Result.ItemDiagnostics!.Where(item => item.Status == "completed").Select(item => item.ItemId).ToArray()
                }, stage.StageId, attemptId, cancellationToken: cancellationToken);
                continue;
            }

            if (execution.Result.Status is "completed" or "partial" or "unknown_external_state" or "cancelled")
                return execution.Result;
            if (execution.Result.Error?.Retryable != true || attemptNumber == stage.RetryPolicy.MaxAttempts)
                return execution.Result;
            await eventStore.AppendAsync("stage.retry_wait", taskId, new { attemptNumber, stage.RetryPolicy.BackoffMs }, stage.StageId, attemptId, cancellationToken: cancellationToken);
            if (stage.RetryPolicy.BackoffMs > 0)
                await Task.Delay(stage.RetryPolicy.BackoffMs, cancellationToken);
        }
        throw new InvalidOperationException("Retry loop terminated unexpectedly.");
    }

    private static bool IsLanguagePracticeDecisionResult(WorkerResult result) =>
        result.Worker.Id == "content.deepseek" &&
        result.Status == "completed" &&
        result.Outputs.Count(output => output.Kind == "language-practice-decision") == 1;

    private static bool IsQuestionAnswerResult(WorkerResult result) =>
        result.Worker.Id == "content.deepseek" &&
        result.Status == "completed" &&
        result.Outputs.Count(output => output.Kind == "question.answer") == 1;

    private static bool IsContentAttentionRequiredResult(WorkerResult result) =>
        result.Worker.Id == "content.deepseek" &&
        result.Status == "partial" &&
        result.Outputs.Count(output => output.Kind == "learning-bundle") == 1 &&
        result.Payload is JsonElement payload && payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("moduleReleaseState", out var releaseState) &&
        releaseState.ValueKind == JsonValueKind.String &&
        releaseState.GetString() is "partial" or "unresolved" or "failed";

    private static async Task<bool> TryPublishQuestionAnswerAsync(
        string jobRoot,
        WorkerRequest request,
        WorkerResult result,
        EventStore eventStore,
        CancellationToken cancellationToken)
    {
        var answerReferences = result.Outputs.Where(output => output.Kind == "question.answer").ToArray();
        if (answerReferences.Length == 0) return false;
        if (answerReferences.Length != 1 || request.WorkerId != "content.deepseek" || request.Operation != "question.answer" || result.Status != "completed")
            throw new InvalidDataException("Only a completed content.deepseek question.answer operation may publish one structured question.answer artifact.");
        if (result.Outputs.Any(output => output.Kind == "card-bundle" || output.Kind == "language-practice-decision"))
            throw new InvalidDataException("Question answering cannot publish cards or a language-practice decision.");
        var learningReferences = result.Outputs.Where(output => output.Kind == "learning-bundle").ToArray();
        if (learningReferences.Length != 1)
            throw new InvalidDataException("Question answering requires exactly one learning-bundle audit artifact.");

        var structuredReference = answerReferences[0];
        var structuredManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, structuredReference, cancellationToken);
        var learningManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, learningReferences[0], cancellationToken);
        if (structuredManifest.Files.Count != 1 || structuredManifest.Files[0].MediaType != "application/json" || learningManifest.Files.Count != 1)
            throw new InvalidDataException("Question answer or its learning audit artifact has an invalid file shape.");
        var matchingLearningAncestry = structuredManifest.InputRefs.Count(input =>
            input.ArtifactRef == learningReferences[0].ArtifactRef && input.Kind == learningReferences[0].Kind &&
            input.ContractVersion == learningReferences[0].ContractVersion && input.ManifestPath == learningReferences[0].ManifestPath &&
            input.ExpectedSha256.Equals(learningReferences[0].ExpectedSha256, StringComparison.OrdinalIgnoreCase));
        if (matchingLearningAncestry != 1)
            throw new InvalidDataException("Structured question.answer does not contain exactly one learning-bundle ancestry reference.");

        using var answerDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, structuredManifest.Files[0].Path), cancellationToken));
        var answer = answerDocument.RootElement;
        StageInputResolver.ValidateQuestionAnswerPayload(answer, request.TaskId);
        if (answer.GetProperty("attempt_id").GetString() != request.AttemptId)
            throw new InvalidDataException("Structured question.answer attempt identity is invalid.");
        var declaredLearningPath = answer.GetProperty("learning_bundle_path").GetString()!;
        var normalizedLearningPath = Path.GetFileName(declaredLearningPath) == declaredLearningPath
            ? Path.Combine(Path.GetDirectoryName(structuredManifest.Files[0].Path.Replace('/', Path.DirectorySeparatorChar))!, declaredLearningPath).Replace('\\', '/')
            : PathPolicy.ToJobRelative(jobRoot, PathPolicy.ResolveJobRelative(jobRoot, declaredLearningPath));
        if (normalizedLearningPath != learningManifest.Files[0].Path ||
            !answer.GetProperty("learning_bundle_sha256").GetString()!.Equals(learningManifest.Files[0].Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Structured question.answer learning payload ancestry is invalid.");
        var turns = answer.GetProperty("turns").EnumerateArray().ToArray();
        var latest = turns[^1];
        if (latest.GetProperty("answer_id").GetString() != answer.GetProperty("new_answer_id").GetString() || latest.GetProperty("provenance").GetString() != "generated")
            throw new InvalidDataException("Structured question.answer does not identify exactly the newly generated answer.");
        var chainHash = answer.GetProperty("question_chain_sha256").GetString();
        if (chainHash is null || chainHash.Length != 64 || chainHash.Any(character => character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw new InvalidDataException("Structured question.answer chain hash is invalid.");

        var answerText = latest.GetProperty("answer_text").GetString()!;
        var textBytes = new UTF8Encoding(false).GetBytes(answerText);
        var textDigest = Integrity.Sha256Hex(textBytes).ToLowerInvariant();
        var textArtifact = await CoreArtifactIO.PublishAsync(
            jobRoot, request.RunId, request.StageId, $"question-answer-text-{textDigest[..24]}",
            "question.answer.text", "text/plain", "answer.txt", textBytes, new[] { structuredReference },
            JsonSerializer.SerializeToElement(new
            {
                entrypoint = $"runs/{request.RunId}/adapters/{request.StageId}/question-answer-text-{textDigest[..24]}/v0001/answer.txt",
                sourceSchemaVersion = "question-answer/1.0.0",
                newAnswerId = answer.GetProperty("new_answer_id").GetString(),
                encoding = "utf-8"
            }), cancellationToken);

        var targetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var session = new QuestionWorkflowSession(
            "knowledge-clipper-question-session.v2", targetToken,
            new QuestionWorkflowBinding(request.TaskId, request.RunId, request.StageId, request.AttemptId, request.OperationId),
            structuredReference, textArtifact, turns.Length, answer.GetProperty("new_answer_id").GetString()!, DateTimeOffset.UtcNow);
        var sessionPath = PathPolicy.ResolveJobRelative(jobRoot, request.AttemptPath + "/question-session.json");
        await AtomicFile.WriteJsonOnceAsync(sessionPath, session, cancellationToken);
        await eventStore.AppendAsync(
            "task.question_answer_available", request.TaskId,
            new
            {
                answerAvailable = true,
                questionTargetToken = session.TargetToken,
                session.Binding,
                sourceAnswerArtifact = session.SourceAnswerArtifact,
                questionAnswerRef = session.DisplayAnswerArtifact,
                session.AnsweredTurnCount,
                session.LatestAnswerId,
                sessionRef = PathPolicy.ToJobRelative(jobRoot, sessionPath)
            }, request.StageId, request.AttemptId, cancellationToken: cancellationToken);
        return true;
    }

    private static async Task<bool> TryPublishLanguagePracticeDecisionAsync(
        string jobRoot,
        WorkerRequest request,
        WorkerResult result,
        EventStore eventStore,
        CancellationToken cancellationToken)
    {
        var decisionReferences = result.Outputs.Where(output => output.Kind == "language-practice-decision").ToArray();
        if (decisionReferences.Length == 0)
            return false;
        if (decisionReferences.Length != 1)
            throw new InvalidDataException("Content result must publish at most one language-practice-decision artifact.");
        if (request.WorkerId != "content.deepseek" || result.Status != "completed")
            throw new InvalidDataException("Only content.deepseek may request a language-practice decision.");
        if (result.Outputs.Any(output => output.Kind == "card-bundle"))
            throw new InvalidDataException("A language-practice decision result must not publish a consumable card-bundle.");
        var learningReferences = result.Outputs.Where(output => output.Kind == "learning-bundle").ToArray();
        if (learningReferences.Length != 1)
            throw new InvalidDataException("A language-practice decision requires exactly one learning-bundle audit artifact.");

        var decisionReference = decisionReferences[0];
        var decisionManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, decisionReference, cancellationToken);
        var learningManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, learningReferences[0], cancellationToken);
        if (decisionManifest.Files.Count != 1 || decisionManifest.Files[0].MediaType != "application/json")
            throw new InvalidDataException("Language-practice decision artifact must contain exactly one JSON file.");
        if (learningManifest.Files.Count != 1)
            throw new InvalidDataException("Language-practice learning audit artifact must contain exactly one file.");
        var decisionFile = decisionManifest.Files[0];
        using var decisionDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, decisionFile.Path), cancellationToken));
        var root = decisionDocument.RootElement;
        var declaredLearningPath = root.GetProperty("learning_bundle_path").GetString()!;
        string normalizedLearningPath;
        if (Path.GetFileName(declaredLearningPath) == declaredLearningPath)
        {
            var decisionDirectory = Path.GetDirectoryName(decisionFile.Path.Replace('/', Path.DirectorySeparatorChar))!;
            normalizedLearningPath = Path.Combine(decisionDirectory, declaredLearningPath).Replace('\\', '/');
        }
        else
        {
            normalizedLearningPath = PathPolicy.ToJobRelative(jobRoot, PathPolicy.ResolveJobRelative(jobRoot, declaredLearningPath));
        }
        var learningReference = learningReferences[0];
        var matchingLearningAncestry = decisionManifest.InputRefs.Count(input =>
            input.ArtifactRef == learningReference.ArtifactRef && input.Kind == learningReference.Kind &&
            input.ContractVersion == learningReference.ContractVersion && input.ManifestPath == learningReference.ManifestPath &&
            input.ExpectedSha256.Equals(learningReference.ExpectedSha256, StringComparison.OrdinalIgnoreCase));
        var requiredRoot = new[] { "schema_version", "decision_bundle_id", "package_id", "task_id", "input_version", "attempt_id", "created_at", "learning_bundle_path", "learning_bundle_sha256", "state", "decisions" };
        if (!root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(requiredRoot) ||
            root.GetProperty("schema_version").GetString() != "language-practice-decision/1.0.0" ||
            root.GetProperty("task_id").GetString() != request.TaskId ||
            root.GetProperty("attempt_id").GetString() != request.AttemptId ||
            root.GetProperty("state").GetString() != "requires_human" ||
            normalizedLearningPath != learningManifest.Files[0].Path ||
            matchingLearningAncestry != 1 ||
            !root.GetProperty("learning_bundle_sha256").GetString()!.Equals(learningManifest.Files[0].Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Content language-practice decision bundle identity or learning ancestry is invalid.");
        var items = root.GetProperty("decisions").EnumerateArray().ToArray();
        if (items.Length == 0)
            throw new InvalidDataException("Content language-practice decision bundle is empty.");
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var name in new[] { "decision_id", "item_id", "unit_id" })
                if (string.IsNullOrWhiteSpace(item.GetProperty(name).GetString()))
                    throw new InvalidDataException($"Content language-practice decision field is empty: {name}.");
            var categoryId = item.GetProperty("category_id").GetString();
            if (categoryId is not ("language_zh" or "language_en"))
                throw new InvalidDataException("Content language-practice decision category is invalid.");
            categoryIds.Add(categoryId);
            if (item.GetProperty("state").GetString() != "requires_human" ||
                !item.GetProperty("allowed_modes").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "meaning-reading", "writing", "both" }) ||
                item.GetProperty("reasons").GetArrayLength() == 0 || item.GetProperty("requested_override_paths").GetArrayLength() == 0)
                throw new InvalidDataException("Content language-practice decision options or reasons are invalid.");
        }

        var targetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var binding = new LanguagePracticeDecisionBinding(request.TaskId, request.RunId, request.StageId, request.AttemptId, request.OperationId);
        var decision = new LanguagePracticeDecision(
            "knowledge-clipper-language-practice-decision.v2",
            "language-practice",
            targetToken,
            binding,
            decisionReference,
            items.Length,
            categoryIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            new[] { "meaning-reading", "writing", "both" },
            DateTimeOffset.UtcNow);
        var decisionPath = PathPolicy.ResolveJobRelative(jobRoot, request.AttemptPath + "/decision.json");
        await AtomicFile.WriteJsonOnceAsync(decisionPath, decision, cancellationToken);
        await eventStore.AppendAsync(
            "task.decision_requested",
            request.TaskId,
            new
            {
                decision.DecisionKind,
                decision.TargetToken,
                decision.Binding,
                decision.DecisionCount,
                decision.CategoryIds,
                decision.SourceDecisionArtifact,
                decision.Choices,
                decisionRef = PathPolicy.ToJobRelative(jobRoot, decisionPath)
            },
            request.StageId,
            request.AttemptId,
            cancellationToken: cancellationToken);
        return true;
    }

    private static async Task<WorkerResult> PublishBindingFailureAsync(
        string jobRoot,
        string taskId,
        string runId,
        PipelineStage stage,
        WorkerEntry worker,
        EventStore eventStore,
        OrchestratorDispatchException exception,
        CancellationToken cancellationToken)
    {
        var attemptId = $"attempt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var attemptPath = $"runs/{runId}/stages/{stage.StageId}/attempts/{attemptId}";
        var requestId = $"request-{Guid.NewGuid():N}";
        var operationId = $"operation-{Guid.NewGuid():N}";
        var idempotencyKey = ComputeIdempotencyKey(taskId, stage, worker);
        var request = new WorkerRequest(
            "1.0.0", requestId, operationId, taskId, runId, stage.StageId, attemptId, worker.WorkerId, stage.Operation,
            idempotencyKey, attemptPath, attemptPath + "/outputs", attemptPath + "/progress.ndjson", attemptPath + "/result.json",
            new CancelSignalReference("1.0.0", attemptPath + "/cancel.json", 2000), stage.InputRefs, stage.ConfigSnapshot, stage.Payload);
        var requestPath = PathPolicy.ResolveJobRelative(jobRoot, attemptPath + "/request.json");
        await AtomicFile.WriteJsonOnceAsync(requestPath, request, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var result = new WorkerResult(
            "1.0.0", requestId, operationId, taskId, runId, stage.StageId, attemptId,
            new WorkerIdentity("orchestrator.core-adapter", "1.0.0"), "1.0.0", idempotencyKey, "failed",
            Array.Empty<ArtifactReference>(), new WorkerError(exception.Code, "contract", false, PathPolicy.ToJobRelative(jobRoot, requestPath), exception.Message, "orchestrator"),
            now, now, new[] { "The worker was not started because required upstream outputs could not be bound." });
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, attemptPath + "/result.json"), result, cancellationToken);
        await eventStore.AppendAsync("stage.binding_failed", taskId, new { errorCode = exception.Code, message = exception.Message }, stage.StageId, attemptId, cancellationToken: cancellationToken);
        return result;
    }

    private static async Task<WorkerResult?> TryReuseAsync(
        string jobRoot,
        WorkerRequest request,
        WorkerEntry worker,
        IReadOnlyList<string> requiredOutputKinds,
        CancellationToken cancellationToken)
    {
        // Playback is an effect, not a reusable computation.  The audio artifact may
        // be reused, but every playback/replay command must launch a fresh attempt.
        if (request.Operation == "speech.playback")
            return null;

        var runsRoot = Path.Combine(jobRoot, "runs");
        if (!Directory.Exists(runsRoot))
            return null;
        foreach (var priorResultPath in Directory.GetFiles(runsRoot, "result.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            WorkerResult? prior;
            try
            {
                prior = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(priorResultPath, cancellationToken), ContractJson.Options);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // Historical terminal files are immutable evidence, but a damaged or
                // partial file is never a reusable result and must not poison new work.
                continue;
            }
            if (prior is null || prior.Status != "completed" || prior.IdempotencyKey != request.IdempotencyKey ||
                prior.StageId != request.StageId || prior.Worker.Id != worker.WorkerId || MissingRequiredOutputs(requiredOutputKinds, prior).Count > 0)
                continue;
            try
            {
                var priorRequestPath = Path.Combine(Path.GetDirectoryName(priorResultPath)!, "request.json");
                var priorRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(priorRequestPath, cancellationToken), ContractJson.Options)
                    ?? throw new InvalidDataException("Reuse candidate request is empty.");
                WorkerProcessRunner.ValidateResultIdentity(priorRequest, worker.WorkerId, prior);
                await WorkerProcessRunner.ValidatePublishedOutputsAsync(jobRoot, prior, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A result whose referenced artifacts no longer pass bytes/SHA checks
                // is not a cache hit.  Continue to an older valid candidate or launch
                // a new worker attempt.
                continue;
            }
            var reusedPayload = prior.Payload;
            if (request.WorkerId == "speech.edge" && request.Operation == "speech.synthesize")
            {
                if (!TryCreateSpeechSynthesisReusePayload(prior, request, out reusedPayload))
                    continue;
            }
            var attemptDirectory = PathPolicy.ResolveJobRelative(jobRoot, request.AttemptPath);
            Directory.CreateDirectory(attemptDirectory);
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(attemptDirectory, "request.json"), request, cancellationToken);
            var reused = prior with
            {
                RequestId = request.RequestId,
                OperationId = request.OperationId,
                RunId = request.RunId,
                AttemptId = request.AttemptId,
                Worker = prior.Worker with { Id = worker.WorkerId },
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                Warnings = prior.Warnings.Concat(new[] { $"Reused successful outputs from {prior.AttemptId}." }).ToArray(),
                Payload = reusedPayload
            };
            await AtomicFile.WriteJsonOnceAsync(Path.Combine(attemptDirectory, "result.json"), reused, cancellationToken);
            return reused;
        }
        return null;
    }

    private static bool TryCreateSpeechSynthesisReusePayload(
        WorkerResult prior,
        WorkerRequest request,
        out JsonElement? reusedPayload)
    {
        reusedPayload = null;
        if (prior.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return false;
        var required = new[] { "operationId", "languageTag", "voiceId", "cacheHit", "playbackState" };
        if (required.Any(name => !payload.TryGetProperty(name, out _)))
            return false;

        var properties = payload.EnumerateObject()
            .Where(item => item.Name is not ("operationId" or "cacheHit" or "reusedFromAttemptId"))
            .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        properties["operationId"] = JsonSerializer.SerializeToElement(request.OperationId);
        properties["cacheHit"] = JsonSerializer.SerializeToElement(true);
        properties["reusedFromAttemptId"] = JsonSerializer.SerializeToElement(prior.AttemptId);
        reusedPayload = JsonSerializer.SerializeToElement(properties, ContractJson.Options);
        return true;
    }

    private static string ComputeIdempotencyKey(string taskId, PipelineStage stage, WorkerEntry worker)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            taskId,
            stage.StageId,
            stage.Operation,
            worker.WorkerId,
            worker.ContractVersionRange,
            inputs = stage.InputRefs.Select(item => new { item.ArtifactRef, item.ExpectedSha256 }).ToArray(),
            stage.OutputKinds,
            stage.InputBindings,
            stage.InputAdapter,
            stage.CompletionPolicy,
            stage.ConfigSnapshot,
            stage.Payload
        }, ContractJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidatePlan(PipelinePlan plan, WorkerRegistry registry)
    {
        if (plan.SchemaVersion is not ("1.0.0" or "1.1.0" or "2.0.0" or "2.1.0"))
            throw new InvalidDataException($"Unsupported pipeline schema: {plan.SchemaVersion}");
        var stageIds = plan.Stages.Select(stage => stage.StageId).ToHashSet(StringComparer.Ordinal);
        if (stageIds.Count != plan.Stages.Count)
            throw new InvalidDataException("Pipeline has duplicate stage IDs.");
        foreach (var stage in plan.Stages)
        {
            if (!registry.Workers.Any(worker => worker.WorkerId == stage.WorkerId && worker.EnabledByDefault))
                throw new InvalidDataException($"Pipeline worker is not enabled in the authoritative registry: {stage.WorkerId}");
            if (stage.DependsOn.Any(dependency => !stageIds.Contains(dependency)))
                throw new InvalidDataException($"Stage {stage.StageId} depends on an unknown stage.");
            if ((stage.InputBindings ?? Array.Empty<StageOutputBinding>()).Any(binding =>
                    !stage.DependsOn.Contains(binding.FromStageId, StringComparer.Ordinal) || !stageIds.Contains(binding.FromStageId)))
                throw new InvalidDataException($"Stage {stage.StageId} binds output from a non-dependency stage.");
            if (stage.InputAdapter?.Type is not (null or "content-input.v1" or "content-input-language-choice.v2" or "question-input.v3" or "question-resume-input.v3" or "ocr-human-resolution-input.v1" or "text-spoken.v1" or "content-to-remnote.v1" or "content-to-anki.v1" or "ordered-original-images.v1" or "kind-alias.v1"))
                throw new InvalidDataException($"Stage {stage.StageId} uses an unsupported input adapter.");
            if (stage.CompletionPolicy is not null &&
                (stage.CompletionPolicy.Outcome != "succeeded-with-playback-stopped" ||
                 stage.CompletionPolicy.ToleratedCancellationReasons.Count != 1 ||
                 stage.CompletionPolicy.ToleratedCancellationReasons[0] != "user_stop" ||
                 stage.Operation != "speech.playback"))
                throw new InvalidDataException($"Stage {stage.StageId} uses an unsupported completion policy.");
            if (stage.PartialRetryPolicy is not null &&
                (plan.SchemaVersion != "2.1.0" || stage.PartialRetryPolicy.Strategy != "failed-items-only" ||
                 stage.PartialRetryPolicy.MaxContinuationAttempts is < 1 or > 9 || stage.WorkerId != "image.ocr.mineru" ||
                 stage.Operation != "process-images" || stage.RetryPolicy.MaxAttempts <= stage.PartialRetryPolicy.MaxContinuationAttempts))
                throw new InvalidDataException($"Stage {stage.StageId} uses an unsupported partial retry policy.");
            if (plan.SchemaVersion == "1.0.0" && ((stage.InputBindings?.Count ?? 0) != 0 || stage.InputAdapter is not null))
                throw new InvalidDataException("Pipeline 1.0.0 cannot use dynamic bindings or core input adapters.");
        }
        if (plan.TerminalBoundary is not null)
        {
            var legacyBoundary = plan.TerminalBoundary.Status == "unsupported" && plan.TerminalBoundary.Code == "media.downstream_unsupported";
            var destinationConditional = plan.TerminalBoundary.Status == "conditional" && plan.TerminalBoundary.Code == "media.destination_adapter_unavailable";
            var availableKinds = plan.Stages.SelectMany(stage => stage.OutputKinds).Append("media-bundle").ToHashSet(StringComparer.Ordinal);
            if (plan.SchemaVersion != "2.1.0" || (!legacyBoundary && !destinationConditional) ||
                plan.TerminalBoundary.AvailableOutputKinds.Count == 0 ||
                plan.TerminalBoundary.AvailableOutputKinds.Any(kind => !availableKinds.Contains(kind)) ||
                plan.TerminalBoundary.UnsupportedConsumers.Count == 0)
                throw new InvalidDataException("Pipeline uses an unsupported terminal boundary.");
        }
        DetectCycle(plan.Stages);
    }

    private static bool TryBuildFailedItemsOnlyPayload(
        string jobRoot,
        PipelineStage stage,
        WorkerResult result,
        JsonElement activePayload,
        out JsonElement continuationPayload,
        out IReadOnlyList<string> failedItemIds)
    {
        continuationPayload = activePayload;
        failedItemIds = Array.Empty<string>();
        if (stage.PartialRetryPolicy?.Strategy != "failed-items-only" || result.Worker.Id != "image.ocr.mineru" ||
            result.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object ||
            result.ItemDiagnostics is null || result.ItemDiagnostics.Count == 0)
            return false;
        var failed = result.ItemDiagnostics.Where(item => item.Status == "failed").ToArray();
        if (failed.Length == 0 || failed.Any(item => item.Error?.Retryable != true) ||
            result.ItemDiagnostics.Any(item => item.Status is not ("completed" or "failed")))
            return false;
        if (!payload.TryGetProperty("moduleResultPath", out var pathValue) || pathValue.ValueKind != JsonValueKind.String)
            return false;
        var moduleResultPath = pathValue.GetString()!;
        var absolute = PathPolicy.ResolveJobRelative(jobRoot, moduleResultPath);
        if (!File.Exists(absolute)) return false;
        if (payload.TryGetProperty("moduleResultSha256", out var shaValue) && shaValue.ValueKind == JsonValueKind.String &&
            !Integrity.Sha256Hex(File.ReadAllBytes(absolute)).Equals(shaValue.GetString(), StringComparison.OrdinalIgnoreCase))
            return false;
        var properties = activePayload.ValueKind == JsonValueKind.Object
            ? activePayload.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        properties["retryModuleResultPath"] = JsonSerializer.SerializeToElement(moduleResultPath);
        continuationPayload = JsonSerializer.SerializeToElement(properties, ContractJson.Options);
        failedItemIds = failed.Select(item => item.ItemId).Order(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static async Task ValidateFailedItemsOnlyContinuationAsync(
        string jobRoot,
        WorkerResult prior,
        WorkerResult current,
        CancellationToken cancellationToken)
    {
        if (current.Status is not ("completed" or "partial" or "failed" or "unknown_external_state" or "cancelled")) return;
        if (current.Status is "failed" or "unknown_external_state" or "cancelled") return;
        var before = prior.ItemDiagnostics?.ToDictionary(item => item.ItemId, StringComparer.Ordinal)
            ?? throw new InvalidDataException("Partial continuation source omitted item diagnostics.");
        var after = current.ItemDiagnostics?.ToDictionary(item => item.ItemId, StringComparer.Ordinal)
            ?? throw new InvalidDataException("Partial continuation result omitted item diagnostics.");
        if (!before.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(after.Keys))
            throw new InvalidDataException("Partial continuation changed the image item set.");
        foreach (var priorItem in before.Values.Where(item => item.Status == "completed"))
        {
            var currentItem = after[priorItem.ItemId];
            if (currentItem.Status != "completed" || priorItem.Outputs.Count != currentItem.Outputs.Count ||
                !priorItem.Outputs.Zip(currentItem.Outputs).All(pair => pair.First == pair.Second))
                throw new InvalidDataException($"Partial continuation did not hash-reuse completed item {priorItem.ItemId}.");
            foreach (var reference in currentItem.Outputs)
                await CoreArtifactIO.ValidateReferenceAsync(jobRoot, reference, cancellationToken);
        }
    }

    private static async Task<OcrHumanResolutionSession> PublishOcrHumanResolutionSessionAsync(
        string jobRoot,
        WorkerRequest request,
        WorkerResult result,
        ArtifactReference learningReference,
        CancellationToken cancellationToken)
    {
        var proofreadReferences = result.Outputs.Where(output => output.Kind == "ocr-proofread-bundle" && output.ContractVersion == "1.0.0").ToArray();
        if (proofreadReferences.Length != 1)
            throw new OrchestratorDispatchException("binding.ocr_resolution_source_missing", "OCR unresolved Content requires exactly one immutable ocr-proofread-bundle.");
        var proofreadReference = proofreadReferences[0];
        var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, proofreadReference, cancellationToken);
        var entrypoint = manifest.Metadata.TryGetProperty("entrypoint", out var entrypointElement) && entrypointElement.ValueKind == JsonValueKind.String
            ? entrypointElement.GetString()
            : manifest.Files.Count == 1 ? manifest.Files[0].Path : null;
        if (string.IsNullOrWhiteSpace(entrypoint))
            throw new OrchestratorDispatchException("binding.ocr_resolution_source_invalid", "OCR proofread artifact has no unique entrypoint.");
        var file = manifest.Files.SingleOrDefault(item => item.Path == entrypoint)
            ?? throw new OrchestratorDispatchException("binding.ocr_resolution_source_invalid", "OCR proofread entrypoint is not declared by its manifest.");
        using var proofreadDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, file.Path), cancellationToken));
        var root = proofreadDocument.RootElement;
        if (root.GetProperty("schema_version").GetString() != "ocr-proofread-bundle/1.0.0" || root.GetProperty("release_state").GetString() != "unresolved")
            throw new OrchestratorDispatchException("binding.ocr_resolution_source_invalid", "OCR proofread bundle is not an unresolved v1 bundle.");
        var items = root.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("state").GetString() == "unresolved").ToArray();
        if (items.Length != 1)
            throw new OrchestratorDispatchException("binding.ocr_resolution_cardinality_unsupported", "OCR human resolution currently requires exactly one unresolved item per task.");
        var item = items[0];
        var itemProvenance = item.GetProperty("provenance");
        var markdownElement = itemProvenance.GetProperty("markdown_artifact");
        var markdownReference = new ArtifactReference(
            markdownElement.GetProperty("artifactRef").GetString()!, markdownElement.GetProperty("kind").GetString()!, markdownElement.GetProperty("contractVersion").GetString()!,
            markdownElement.GetProperty("manifestPath").GetString()!, markdownElement.GetProperty("expectedSha256").GetString()!);
        if (!await CoreArtifactIO.HasAncestorAsync(jobRoot, proofreadReference, markdownReference, cancellationToken))
            throw new OrchestratorDispatchException("binding.ocr_resolution_ancestry_invalid", "OCR proofread bundle is not rooted in its declared markdown artifact.");
        var (imageProvenanceReference, sourceOriginalReference) = await ValidateImageHumanContinuationProvenanceAsync(
            jobRoot, request, markdownReference, markdownElement, cancellationToken);
        var originalText = item.GetProperty("original_text").GetString() ?? string.Empty;
        var candidateText = item.GetProperty("proofread_text").GetString() ?? string.Empty;
        var originalBytes = new UTF8Encoding(false).GetBytes(originalText);
        var candidateBytes = new UTF8Encoding(false).GetBytes(candidateText);
        var originalSha = Integrity.Sha256Hex(originalBytes).ToLowerInvariant();
        var candidateSha = Integrity.Sha256Hex(candidateBytes).ToLowerInvariant();
        if (item.GetProperty("original_text_sha256").GetString() != originalSha || item.GetProperty("proofread_text_sha256").GetString() != candidateSha)
            throw new OrchestratorDispatchException("binding.ocr_resolution_text_hash_mismatch", "OCR proofread text hashes do not match the immutable payload.");
        var originalRef = await CoreArtifactIO.PublishAsync(
            jobRoot, request.RunId, request.StageId, $"ocr-original-text-{originalSha[..24]}", "ocr.original.text", "text/plain", "original.txt",
            originalBytes, new[] { proofreadReference }, JsonSerializer.SerializeToElement(new { entrypoint = $"runs/{request.RunId}/adapters/{request.StageId}/ocr-original-text-{originalSha[..24]}/v0001/original.txt", sourceItemId = item.GetProperty("item_id").GetString(), provenance = "image.ocr.mineru" }), cancellationToken);
        var candidateRef = await CoreArtifactIO.PublishAsync(
            jobRoot, request.RunId, request.StageId, $"ocr-candidate-text-{candidateSha[..24]}", "ocr.candidate.text", "text/plain", "candidate.txt",
            candidateBytes, new[] { proofreadReference }, JsonSerializer.SerializeToElement(new { entrypoint = $"runs/{request.RunId}/adapters/{request.StageId}/ocr-candidate-text-{candidateSha[..24]}/v0001/candidate.txt", sourceItemId = item.GetProperty("item_id").GetString(), provenance = "content.deepseek.proofread" }), cancellationToken);
        var session = new OcrHumanResolutionSession(
            "knowledge-clipper-ocr-human-resolution-session.v1",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            new OcrHumanResolutionBinding(request.TaskId, request.RunId, request.StageId, request.AttemptId, request.OperationId),
            proofreadReference, learningReference, imageProvenanceReference, sourceOriginalReference, originalRef, candidateRef,
            item.GetProperty("item_id").GetString()!, originalSha, candidateSha,
            item.GetProperty("corrections").Clone(), item.GetProperty("unresolved").Clone(),
            new[] { "accept_original", "submit_correction", "cancel" }, DateTimeOffset.UtcNow);
        await AtomicFile.WriteJsonOnceAsync(PathPolicy.ResolveJobRelative(jobRoot, request.AttemptPath + "/ocr-resolution-session.json"), session, cancellationToken);
        return session;
    }

    private static async Task<(ArtifactReference Provenance, ArtifactReference Original)> ValidateImageHumanContinuationProvenanceAsync(
        string jobRoot,
        WorkerRequest contentRequest,
        ArtifactReference markdownReference,
        JsonElement markdownDeclaration,
        CancellationToken cancellationToken)
    {
        var stageRoot = PathPolicy.ResolveJobRelative(jobRoot, $"runs/{contentRequest.RunId}/stages/image-ocr/attempts");
        if (!Directory.Exists(stageRoot))
            throw new OrchestratorDispatchException("binding.ocr_image_provenance_missing", "OCR human continuation has no same-run image stage evidence.");
        var candidates = new List<WorkerResult>();
        foreach (var resultPath in Directory.GetFiles(stageRoot, "result.json", SearchOption.AllDirectories))
        {
            try
            {
                var candidate = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(resultPath, cancellationToken), ContractJson.Options);
                if (candidate is not null && candidate.TaskId == contentRequest.TaskId && candidate.RunId == contentRequest.RunId &&
                    candidate.StageId == "image-ocr" && candidate.Worker.Id == "image.ocr.mineru" && candidate.Status == "completed" &&
                    candidate.Outputs.Any(reference => SameArtifactReference(reference, markdownReference)))
                    candidates.Add(candidate);
            }
            catch (JsonException) { }
        }
        if (candidates.Count != 1)
            throw new OrchestratorDispatchException("binding.ocr_image_provenance_ambiguous", "OCR human continuation requires exactly one completed same-run image result owning the proofread markdown.");
        var result = candidates[0];
        var provenanceReferences = result.Outputs.Where(reference => reference.Kind == "image.processing.provenance").ToArray();
        if (provenanceReferences.Length != 1)
            throw new OrchestratorDispatchException("binding.ocr_image_provenance_missing", "Completed image result must publish one processing provenance artifact.");
        var provenanceReference = provenanceReferences[0];
        var provenanceManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, provenanceReference, cancellationToken);
        var provenanceFile = provenanceManifest.Files.SingleOrDefault(file => file.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || file.MediaType == "application/json")
            ?? throw new OrchestratorDispatchException("binding.ocr_image_provenance_invalid", "Image processing provenance has no JSON payload.");
        using var provenanceDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, provenanceFile.Path), cancellationToken));
        var provenance = provenanceDocument.RootElement;
        var continuation = provenance.GetProperty("continuation");
        if (provenance.GetProperty("schemaVersion").GetString() != "image-processing-provenance.v1" ||
            provenance.GetProperty("mode").GetString() != "ocr_mineru" || provenance.GetProperty("terminalStatus").GetString() != "completed" ||
            continuation.GetProperty("defaultOcrRerun").GetBoolean() ||
            !continuation.GetProperty("requiresOriginalArtifactHashMatch").GetBoolean() ||
            !continuation.GetProperty("requiresSuccessfulOutputHashMatch").GetBoolean())
            throw new OrchestratorDispatchException("binding.ocr_image_provenance_invalid", "Image processing provenance does not authorize hash-bound no-rerun human continuation.");
        var markdownItem = provenance.GetProperty("items").EnumerateArray().SingleOrDefault(item =>
            item.GetProperty("status").GetString() == "completed" && item.GetProperty("representations").TryGetProperty("markdown", out var markdown) &&
            markdown.GetProperty("artifactRef").GetString() == markdownReference.ArtifactRef);
        if (markdownItem.ValueKind != JsonValueKind.Object)
            throw new OrchestratorDispatchException("binding.ocr_image_provenance_invalid", "Image processing provenance does not own the proofread markdown.");
        var representations = markdownItem.GetProperty("representations");
        var markdownRepresentation = representations.GetProperty("markdown");
        var markdownManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, markdownReference, cancellationToken);
        var markdownFile = markdownManifest.Files.SingleOrDefault(file => file.Path == markdownDeclaration.GetProperty("payload_path").GetString())
            ?? throw new OrchestratorDispatchException("binding.ocr_image_provenance_invalid", "Proofread markdown payload is absent from its artifact.");
        if (!markdownFile.Sha256.Equals(markdownDeclaration.GetProperty("payload_sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            !markdownFile.Sha256.Equals(markdownRepresentation.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            markdownFile.Bytes != markdownRepresentation.GetProperty("bytes").GetInt64())
            throw new OrchestratorDispatchException("binding.ocr_image_output_hash_mismatch", "Proofread markdown differs from the successful image output recorded by provenance.");
        var originalRepresentation = representations.GetProperty("original");
        var originalReference = result.Outputs.SingleOrDefault(reference => reference.Kind == "image.original" &&
            reference.ArtifactRef == originalRepresentation.GetProperty("artifactRef").GetString())
            ?? throw new OrchestratorDispatchException("binding.ocr_original_artifact_missing", "Image provenance original artifact is absent from the completed image result.");
        var originalManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, originalReference, cancellationToken);
        if (originalManifest.Files.Count != 1 ||
            !originalManifest.Files[0].Sha256.Equals(originalRepresentation.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            originalManifest.Files[0].Bytes != originalRepresentation.GetProperty("bytes").GetInt64() ||
            originalManifest.Files[0].MediaType != originalRepresentation.GetProperty("mediaType").GetString())
            throw new OrchestratorDispatchException("binding.ocr_original_artifact_hash_mismatch", "Original image artifact differs from the hash-bound image processing provenance.");
        return (provenanceReference, originalReference);
    }

    private static bool SameArtifactReference(ArtifactReference left, ArtifactReference right) =>
        left.ArtifactRef == right.ArtifactRef && left.Kind == right.Kind && left.ContractVersion == right.ContractVersion &&
        left.ManifestPath == right.ManifestPath && left.ExpectedSha256.Equals(right.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

    private static async Task<ArtifactReference> PublishOriginalMediaBundleAsync(
        string jobRoot,
        WorkerRequest request,
        WorkerResult result,
        EventStore eventStore,
        CancellationToken cancellationToken)
    {
        var originals = result.Outputs.Where(output => output.Kind == "image.original").ToArray();
        if (originals.Length == 0)
            throw new OrchestratorDispatchException("binding.media_original_missing", "Original mode completed without immutable image.original artifacts.");
        foreach (var original in originals) await CoreArtifactIO.ValidateReferenceAsync(jobRoot, original, cancellationToken);
        var identity = string.Join("\n", originals.OrderBy(item => item.ArtifactRef, StringComparer.Ordinal)
            .Select(item => $"{item.ArtifactRef}\0{item.ExpectedSha256.ToLowerInvariant()}"));
        var bundleId = $"media-bundle-{Integrity.Sha256Hex(Encoding.UTF8.GetBytes(identity)).ToLowerInvariant()[..24]}";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "knowledge-clipper-media-bundle.v1", bundleId, request.TaskId, request.RunId,
            sourceMode = "original", ocrInvoked = false,
            items = originals.Select((reference, index) => new { itemId = $"media-{index + 1:D4}", mediaRef = reference }).ToArray(),
            destinationStatus = new[]
            {
                new { destination = "remnote", status = "conditional", code = "mcp.media_write_unavailable" },
                new { destination = "anki", status = "supported", code = "ordered-original-images.v1" }
            },
            createdAt = DateTimeOffset.UtcNow
        }, ContractJson.Options);
        var reference = await CoreArtifactIO.PublishAsync(
            jobRoot, request.RunId, request.StageId, bundleId, "media-bundle", "application/vnd.knowledge-clipper.media-bundle+json",
            "media-bundle.json", payload, originals,
            JsonSerializer.SerializeToElement(new { entrypoint = $"runs/{request.RunId}/adapters/{request.StageId}/{bundleId}/v0001/media-bundle.json", sourceMode = "original", destinationNeutral = true }),
            cancellationToken);
        await eventStore.AppendAsync("pipeline.media_projection_available", request.TaskId,
            new { mediaBundle = reference, sourceMode = "original", ocrInvoked = false, downstreamStatus = "conditional", automaticExport = false },
            request.StageId, request.AttemptId, cancellationToken: cancellationToken);
        return reference;
    }

    private static IReadOnlyList<string> MissingRequiredOutputs(IReadOnlyList<string> requiredKinds, WorkerResult result)
    {
        var published = result.Outputs.Select(item => item.Kind).ToHashSet(StringComparer.Ordinal);
        return requiredKinds.Where(kind => !published.Contains(kind)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool StageSatisfied(string jobRoot, string runId, PipelineStage stage, WorkerResult result)
    {
        if (result.Status == "completed") return true;
        if (result.Status != "cancelled" || stage.CompletionPolicy is null) return false;
        var signalPath = Path.Combine(jobRoot, "runs", runId, "stages", stage.StageId, "attempts", result.AttemptId, "cancel.json");
        if (!File.Exists(signalPath)) return false;
        try
        {
            var signal = JsonSerializer.Deserialize<WorkerCancelSignal>(File.ReadAllText(signalPath), ContractJson.Options);
            return signal is not null && stage.CompletionPolicy.ToleratedCancellationReasons.Contains(signal.Reason, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void DetectCycle(IReadOnlyList<PipelineStage> stages)
    {
        var remaining = stages.ToDictionary(stage => stage.StageId, stage => stage.DependsOn.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(item => item.Value.Count == 0).Select(item => item.Key).ToArray();
            if (ready.Length == 0)
                throw new InvalidDataException("Pipeline dependency graph contains a cycle.");
            foreach (var id in ready)
                remaining.Remove(id);
            foreach (var dependencies in remaining.Values)
                dependencies.ExceptWith(ready);
        }
    }

    private static async Task AppendStateAsync(EventStore store, string taskId, string status, CancellationToken cancellationToken)
    {
        await store.AppendAsync("task.state", taskId, new { status }, cancellationToken: cancellationToken);
    }

    private static async Task MonitorCancelAsync(string cancelPath, CancellationTokenSource source, CancellationReasonState reasonState)
    {
        while (!source.IsCancellationRequested)
        {
            if (File.Exists(cancelPath))
            {
                var document = JsonSerializer.Deserialize<TaskCancelRequest>(await File.ReadAllTextAsync(cancelPath, source.Token), ContractJson.Options)
                    ?? throw new InvalidDataException("Run cancellation request is empty.");
                reasonState.Set(document.Reason);
                source.Cancel();
                return;
            }
            await Task.Delay(200, source.Token);
        }
    }
}

public sealed class CancellationReasonState
{
    private string reason = "task_cancel";

    public string Get() => Volatile.Read(ref reason);

    public void Set(string value)
    {
        if (value is not ("user_stop" or "superseded" or "task_cancel" or "timeout"))
            throw new InvalidDataException($"Unsupported cancellation reason: {value}");
        Volatile.Write(ref reason, value);
    }
}

public sealed record TaskCancelRequest(
    string SchemaVersion,
    string TaskId,
    string OperationId,
    string Reason,
    DateTimeOffset RequestedAt);

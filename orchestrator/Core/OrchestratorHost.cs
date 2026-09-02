using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed class OrchestratorHost
{
    private readonly string jobsRoot;
    private readonly string registryPath;
    private readonly string releaseRoot;
    private readonly string resolverConfigurationPath;
    private readonly int maxActiveTasks;
    private readonly Channel<AcceptedCommand> queue = Channel.CreateUnbounded<AcceptedCommand>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly ConcurrentDictionary<string, byte> scheduledCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> taskGates = new(StringComparer.Ordinal);
    private WorkerRegistry? registry;

    public OrchestratorHost(string jobsRoot, string registryPath, string releaseRoot, int maxActiveTasks = 4, string? resolverConfigurationPath = null)
    {
        this.jobsRoot = Path.GetFullPath(jobsRoot);
        this.registryPath = Path.GetFullPath(registryPath);
        this.releaseRoot = Path.GetFullPath(releaseRoot);
        var extendedResolver = Path.Combine(this.releaseRoot, "contracts", "core", "v2.1", "profile-resolver.v2.1.json");
        var canonicalResolver = Path.Combine(this.releaseRoot, "contracts", "core", "v2", "profile-resolver.v2.json");
        var legacyResolver = Path.Combine(this.releaseRoot, "contracts", "core-adapter", "v1", "profile-resolver.v1.json");
        this.resolverConfigurationPath = Path.GetFullPath(resolverConfigurationPath ??
            (File.Exists(extendedResolver) ? extendedResolver : File.Exists(canonicalResolver) ? canonicalResolver : legacyResolver));
        this.maxActiveTasks = maxActiveTasks is >= 1 and <= 32 ? maxActiveTasks : throw new ArgumentOutOfRangeException(nameof(maxActiveTasks));
        Directory.CreateDirectory(this.jobsRoot);
    }

    public async Task ServeAsync(IpcConfiguration configuration, CancellationToken cancellationToken)
    {
        var loader = new RegistryLoader();
        registry = loader.Load(registryPath);
        await loader.ValidateEntrypointsAsync(registry, releaseRoot, cancellationToken);
        await new StageControlHandler(jobsRoot).RecoverAsync(cancellationToken);
        await RecoverInterruptedRunsAsync(cancellationToken);
        await ReplayUndispatchedCommandsAsync(cancellationToken);

        var consumers = Enumerable.Range(0, maxActiveTasks)
            .Select(_ => ConsumeAsync(cancellationToken))
            .ToArray();
        var handler = new CommandHandler(jobsRoot, EnqueueAsync, new ModuleActionHandler(jobsRoot, registryPath, releaseRoot));
        try
        {
            await new NamedPipeCommandServer(configuration, handler).ServeAsync(cancellationToken);
        }
        finally
        {
            queue.Writer.TryComplete();
            try { await Task.WhenAll(consumers); } catch (OperationCanceledException) { }
        }
    }

    public ValueTask EnqueueAsync(AcceptedCommand command, CancellationToken cancellationToken)
    {
        if (!scheduledCommands.TryAdd(command.Command.CommandId, 0))
            return ValueTask.CompletedTask;
        return queue.Writer.WriteAsync(command, cancellationToken);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in queue.Reader.ReadAllAsync(cancellationToken))
        {
            if (await IsPlaybackStopAsync(command, cancellationToken))
            {
                await ExecuteAsync(command, cancellationToken);
                continue;
            }
            var taskGate = taskGates.GetOrAdd(command.TaskId, _ => new SemaphoreSlim(1, 1));
            await taskGate.WaitAsync(cancellationToken);
            try
            {
                await ExecuteAsync(command, cancellationToken);
            }
            finally
            {
                taskGate.Release();
            }
        }
    }

    private async Task ExecuteAsync(AcceptedCommand accepted, CancellationToken cancellationToken)
    {
        var command = accepted.Command;
        var control = await ReadPlaybackControlAsync(accepted, cancellationToken);
        var runId = command.Type == "task.submit" || control?.Action == "playback.stop"
            ? accepted.AcceptanceRunId
            : $"run-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var store = new EventStore(accepted.JobRoot, runId);
        await store.AppendAsync(
            "command.execution_started",
            accepted.TaskId,
            new { command.OperationId, command.Type, command.PayloadRef, accepted.DurableEventRef },
            commandId: command.CommandId,
            idempotencyKey: command.IdempotencyKey,
            cancellationToken: cancellationToken);
        try
        {
            var languagePracticeChoice = await ReadLanguagePracticeChoiceAsync(accepted, cancellationToken);
            var questionAction = await ReadQuestionActionAsync(accepted, cancellationToken);
            var ocrResolutionAction = await ReadOcrHumanResolutionActionAsync(accepted, cancellationToken);
            if (control?.Action == "playback.stop")
            {
                await StopPlaybackStageAsync(accepted, store, cancellationToken);
                await store.AppendAsync("command.execution_completed", accepted.TaskId,
                    new { command.OperationId, command.Type, finalState = "stage-control-accepted" },
                    commandId: command.CommandId, idempotencyKey: command.IdempotencyKey, cancellationToken: CancellationToken.None);
                return;
            }
            if (control?.Action == "playback.replay")
            {
                var replayPlan = await ResolvePlaybackReplayAsync(accepted, cancellationToken);
                var replayState = await new OrchestratorEngine().RunAsync(
                    accepted.JobRoot, replayPlan,
                    registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                    releaseRoot, runId, cancellationToken);
                await store.AppendAsync("command.execution_completed", accepted.TaskId,
                    new { command.OperationId, command.Type, finalState = replayState, control = control.Action },
                    commandId: command.CommandId, idempotencyKey: command.IdempotencyKey, cancellationToken: CancellationToken.None);
                return;
            }
            if (languagePracticeChoice is not null)
            {
                var resumePlan = await ResolveLanguagePracticeResumeAsync(accepted, languagePracticeChoice, runId, store, cancellationToken);
                var resumeState = await new OrchestratorEngine().RunAsync(
                    accepted.JobRoot,
                    resumePlan,
                    registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                    releaseRoot,
                    runId,
                    cancellationToken);
                await store.AppendAsync(
                    "command.execution_completed",
                    accepted.TaskId,
                    new { command.OperationId, command.Type, finalState = resumeState, decisionKind = languagePracticeChoice.DecisionKind },
                    commandId: command.CommandId,
                    idempotencyKey: command.IdempotencyKey,
                    cancellationToken: CancellationToken.None);
                return;
            }
            if (questionAction is not null)
            {
                var questionPlan = await ResolveQuestionResumeAsync(accepted, questionAction, runId, store, cancellationToken);
                var questionState = await new OrchestratorEngine().RunAsync(
                    accepted.JobRoot, questionPlan,
                    registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                    releaseRoot, runId, cancellationToken);
                await store.AppendAsync(
                    "command.execution_completed", accepted.TaskId,
                    new { command.OperationId, command.Type, finalState = questionState, questionAction = questionAction.Action },
                    commandId: command.CommandId, idempotencyKey: command.IdempotencyKey, cancellationToken: CancellationToken.None);
                return;
            }
            if (ocrResolutionAction is not null)
            {
                var ocrPlan = await ResolveOcrHumanResolutionAsync(accepted, ocrResolutionAction, runId, store, cancellationToken);
                if (ocrPlan is null)
                {
                    await store.AppendAsync("task.state", accepted.TaskId, new { status = "cancelling" }, commandId: command.CommandId, idempotencyKey: command.IdempotencyKey, cancellationToken: CancellationToken.None);
                    await store.AppendAsync("task.state", accepted.TaskId, new { status = "cancelled" }, commandId: command.CommandId, idempotencyKey: command.IdempotencyKey, cancellationToken: CancellationToken.None);
                    await store.AppendAsync("command.execution_completed", accepted.TaskId,
                        new { command.OperationId, command.Type, finalState = "cancelled", ocrResolutionAction = ocrResolutionAction.Action },
                        commandId: command.CommandId, idempotencyKey: command.IdempotencyKey, cancellationToken: CancellationToken.None);
                    return;
                }
                var ocrState = await new OrchestratorEngine().RunAsync(
                    accepted.JobRoot, ocrPlan,
                    registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                    releaseRoot, runId, cancellationToken);
                await store.AppendAsync("command.execution_completed", accepted.TaskId,
                    new { command.OperationId, command.Type, finalState = ocrState, ocrResolutionAction = ocrResolutionAction.Action },
                    commandId: command.CommandId, idempotencyKey: command.IdempotencyKey, cancellationToken: CancellationToken.None);
                return;
            }
            var planPath = command.Type == "task.submit"
                ? ResolveInitialPlan(accepted.JobRoot)
                : PathPolicy.ResolveJobRelative(accepted.JobRoot, command.PayloadRef);
            PipelinePlan plan;
            using (var planDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(planPath, cancellationToken)))
            {
                var schemaVersion = planDocument.RootElement.GetProperty("schemaVersion").GetString();
                if (schemaVersion is "desktop-intent.v1" or "knowledge-clipper-intent.v2")
                {
                    if (!File.Exists(resolverConfigurationPath))
                        throw new OrchestratorDispatchException("resolver.configuration_missing", $"Desktop profile resolver configuration is missing: {resolverConfigurationPath}");
                    var configuration = DesktopIntentResolver.LoadConfiguration(resolverConfigurationPath);
                    plan = await new DesktopIntentResolver().ResolveAsync(
                        accepted.JobRoot, planPath, registry ?? throw new InvalidOperationException("Host registry is not initialized."), runId, configuration, cancellationToken);
                    await store.AppendAsync(
                        "pipeline.resolved", accepted.TaskId,
                        new
                        {
                            sourceSchemaVersion = schemaVersion,
                            plan.SchemaVersion,
                            plan.PlanVersion,
                            processingProfile = plan.Routing?.ProcessingProfile,
                            destinations = plan.Routing?.Destinations ?? Array.Empty<string>(),
                            routingSource = plan.Routing?.Source,
                            stages = plan.Stages.Select(item => item.StageId).ToArray()
                        },
                        commandId: command.CommandId,
                        idempotencyKey: command.IdempotencyKey,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    plan = JsonSerializer.Deserialize<PipelinePlan>(planDocument.RootElement.GetRawText(), ContractJson.Options)
                        ?? throw new InvalidDataException("Pipeline plan is empty.");
                }
            }
            if (plan.TaskId != accepted.TaskId)
                throw new InvalidDataException("Pipeline taskId does not match accepted command.");
            var finalState = await new OrchestratorEngine().RunAsync(
                accepted.JobRoot,
                plan,
                registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                releaseRoot,
                runId,
                cancellationToken);
            await store.AppendAsync(
                "command.execution_completed",
                accepted.TaskId,
                new { command.OperationId, command.Type, finalState },
                commandId: command.CommandId,
                idempotencyKey: command.IdempotencyKey,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var errorCode = exception is OrchestratorDispatchException dispatch ? dispatch.Code : "orchestrator.dispatch_failed";
            var rejectedControlInput = exception is OrchestratorDispatchException &&
                (errorCode.StartsWith("question.", StringComparison.Ordinal) || errorCode.StartsWith("decision.", StringComparison.Ordinal) ||
                 errorCode.StartsWith("ocr.", StringComparison.Ordinal) || errorCode.StartsWith("control.", StringComparison.Ordinal));
            if (!rejectedControlInput)
                await EnsureFailedStateAsync(store, accepted.TaskId);
            await store.AppendAsync(
                "command.execution_failed",
                accepted.TaskId,
                new { command.OperationId, command.Type, errorCode, message = Trim(exception.Message) },
                commandId: command.CommandId,
                idempotencyKey: command.IdempotencyKey,
                cancellationToken: CancellationToken.None);
        }
    }

    private async Task<bool> IsPlaybackStopAsync(AcceptedCommand accepted, CancellationToken cancellationToken) =>
        (await ReadPlaybackControlAsync(accepted, cancellationToken))?.Action == "playback.stop";

    private static async Task<LanguagePracticeChoice?> ReadLanguagePracticeChoiceAsync(AcceptedCommand accepted, CancellationToken cancellationToken)
    {
        if (accepted.Command.Type != "task.provide_input") return null;
        var path = PathPolicy.ResolveJobRelative(accepted.JobRoot, accepted.Command.PayloadRef);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetString() != "knowledge-clipper-language-practice-choice.v2")
            return null;
        var names = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals(new[] { "schemaVersion", "decisionKind", "targetToken", "choice" }))
            throw new OrchestratorDispatchException("decision.payload_invalid", "Language-practice choice contains unsupported fields.");
        var choice = JsonSerializer.Deserialize<LanguagePracticeChoice>(root.GetRawText(), ContractJson.Options)
            ?? throw new OrchestratorDispatchException("decision.payload_invalid", "Language-practice choice payload is empty.");
        if (choice.DecisionKind != "language-practice" ||
            choice.Choice is not ("meaning-reading" or "writing" or "both") ||
            choice.TargetToken.Length != 64 || !choice.TargetToken.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new OrchestratorDispatchException("decision.payload_invalid", "Language-practice choice does not match the v2 contract.");
        return choice;
    }

    private static async Task<QuestionWorkflowAction?> ReadQuestionActionAsync(AcceptedCommand accepted, CancellationToken cancellationToken)
    {
        if (accepted.Command.Type != "task.provide_input") return null;
        var path = PathPolicy.ResolveJobRelative(accepted.JobRoot, accepted.Command.PayloadRef);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetString() != "knowledge-clipper-question-action.v2")
            return null;
        var action = JsonSerializer.Deserialize<QuestionWorkflowAction>(root.GetRawText(), ContractJson.Options)
            ?? throw new OrchestratorDispatchException("question.payload_invalid", "Question workflow action is empty.");
        var names = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var common = new[] { "schemaVersion", "action", "targetToken" };
        if (action.TargetToken.Length != 64 || action.TargetToken.Any(character => character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw new OrchestratorDispatchException("question.payload_invalid", "Question workflow target token is invalid.");
        if (action.Action == "question.follow-up")
        {
            if (!names.SetEquals(common.Concat(new[] { "turnIndex", "questionTurnRef" })) || action.TurnIndex is null or < 1 ||
                action.QuestionTurnRef is null || action.QuestionTurnRef.Kind != "question.turn" || action.QuestionTurnRef.ContractVersion != "1.0.0" || action.CardStructureRef is not null)
                throw new OrchestratorDispatchException("question.payload_invalid", "Follow-up action must contain one ordered question.turn reference.");
        }
        else if (action.Action == "question.make-cards")
        {
            var allowed = common.Concat(new[] { "cardStructureRef" }).ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(common) && !names.SetEquals(allowed) || action.TurnIndex is not null || action.QuestionTurnRef is not null ||
                action.CardStructureRef is not null && (action.CardStructureRef.Kind != "question.card-structure" || action.CardStructureRef.ContractVersion != "1.0.0"))
                throw new OrchestratorDispatchException("question.payload_invalid", "make-cards action contains unsupported fields or artifact kinds.");
        }
        else
        {
            throw new OrchestratorDispatchException("question.payload_invalid", "Question workflow action is unknown.");
        }
        return action;
    }

    private static async Task<OcrHumanResolutionAction?> ReadOcrHumanResolutionActionAsync(AcceptedCommand accepted, CancellationToken cancellationToken)
    {
        if (accepted.Command.Type != "task.provide_input") return null;
        var path = PathPolicy.ResolveJobRelative(accepted.JobRoot, accepted.Command.PayloadRef);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetString() != "knowledge-clipper-ocr-human-resolution-action.v1")
            return null;
        var action = JsonSerializer.Deserialize<OcrHumanResolutionAction>(root.GetRawText(), ContractJson.Options)
            ?? throw new OrchestratorDispatchException("ocr.payload_invalid", "OCR human-resolution action is empty.");
        var names = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var baseNames = new[] { "schemaVersion", "decisionKind", "targetToken", "action", "reason" };
        if (action.DecisionKind != "ocr-human-resolution" || action.TargetToken.Length != 64 ||
            action.TargetToken.Any(character => character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')) || string.IsNullOrWhiteSpace(action.Reason))
            throw new OrchestratorDispatchException("ocr.payload_invalid", "OCR human-resolution identity, token, or reason is invalid.");
        if (action.Action == "submit_correction")
        {
            if (!names.SetEquals(baseNames.Concat(new[] { "correctionRef", "diffs" })) || action.CorrectionRef is null ||
                action.CorrectionRef.Kind != "ocr.human-correction.text" || action.CorrectionRef.ContractVersion != "1.0.0" ||
                action.Diffs is not JsonElement diffs || diffs.ValueKind != JsonValueKind.Array || diffs.GetArrayLength() == 0)
                throw new OrchestratorDispatchException("ocr.payload_invalid", "submit_correction requires one correction artifact and a non-empty diff list.");
        }
        else if (action.Action is "accept_original" or "cancel")
        {
            if (!names.SetEquals(baseNames) || action.CorrectionRef is not null || action.Diffs is not null)
                throw new OrchestratorDispatchException("ocr.payload_invalid", "accept_original and cancel forbid correction fields.");
        }
        else
        {
            throw new OrchestratorDispatchException("ocr.payload_invalid", "OCR human-resolution action is unknown.");
        }
        return action;
    }

    private async Task<PipelinePlan?> ResolveOcrHumanResolutionAsync(
        AcceptedCommand accepted,
        OcrHumanResolutionAction action,
        string runId,
        EventStore store,
        CancellationToken cancellationToken)
    {
        var runsRoot = Path.Combine(accepted.JobRoot, "runs");
        var candidates = Directory.Exists(runsRoot)
            ? Directory.GetFiles(runsRoot, "ocr-resolution-session.json", SearchOption.AllDirectories)
                .Select(path => (Path: path, Session: TryReadOcrResolutionSession(path)))
                .Where(item => item.Session?.TargetToken == action.TargetToken)
                .ToArray()
            : Array.Empty<(string Path, OcrHumanResolutionSession? Session)>();
        if (candidates.Length == 0)
            throw new OrchestratorDispatchException("ocr.session_not_found", "OCR human-resolution token is unknown for this task.");
        if (candidates.Length != 1)
            throw new OrchestratorDispatchException("ocr.session_ambiguous", "OCR human-resolution token does not resolve uniquely.");
        var sessionPath = candidates[0].Path;
        var session = candidates[0].Session!;
        if (session.SchemaVersion != "knowledge-clipper-ocr-human-resolution-session.v1" || session.Binding.TaskId != accepted.TaskId ||
            !ValidIdentifier(session.Binding.TaskId) || !ValidIdentifier(session.Binding.RunId) || !ValidIdentifier(session.Binding.StageId) ||
            !ValidIdentifier(session.Binding.AttemptId) || !ValidIdentifier(session.Binding.OperationId) || !ValidIdentifier(session.SourceItemId))
            throw new OrchestratorDispatchException("ocr.session_binding_mismatch", "OCR action does not match the authoritative session binding.");
        var expectedSessionPath = PathPolicy.ResolveJobRelative(accepted.JobRoot,
            $"runs/{session.Binding.RunId}/stages/{session.Binding.StageId}/attempts/{session.Binding.AttemptId}/ocr-resolution-session.json");
        if (!Path.GetFullPath(sessionPath).Equals(expectedSessionPath, StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("ocr.session_binding_mismatch", "OCR session file does not match its binding.");
        var attemptDirectory = Path.GetDirectoryName(sessionPath)!;
        var sourceRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(Path.Combine(attemptDirectory, "request.json"), cancellationToken), ContractJson.Options);
        var sourceResult = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(Path.Combine(attemptDirectory, "result.json"), cancellationToken), ContractJson.Options);
        if (sourceRequest is null || sourceResult is null || sourceRequest.TaskId != session.Binding.TaskId || sourceRequest.RunId != session.Binding.RunId ||
            sourceRequest.StageId != session.Binding.StageId || sourceRequest.AttemptId != session.Binding.AttemptId || sourceRequest.OperationId != session.Binding.OperationId ||
            sourceRequest.WorkerId != "content.deepseek" || sourceResult.Status != "completed" ||
            !sourceResult.Outputs.Any(output => SameReference(output, session.SourceProofreadArtifact)) ||
            !sourceResult.Outputs.Any(output => SameReference(output, session.SourceLearningArtifact)))
            throw new OrchestratorDispatchException("ocr.session_binding_mismatch", "OCR session does not match its immutable Content request and result.");
        foreach (var reference in new[]
        {
            session.SourceProofreadArtifact, session.SourceLearningArtifact, session.SourceImageProvenanceArtifact,
            session.SourceOriginalImageArtifact, session.OriginalTextRef, session.CandidateTextRef
        })
            await CoreArtifactIO.ValidateReferenceAsync(accepted.JobRoot, reference, cancellationToken);
        if (TaskStateMachine.Project(new EventStore(accepted.JobRoot, session.Binding.RunId).ReadAll()) != "waiting_input")
            throw new OrchestratorDispatchException("ocr.session_not_active", "OCR human-resolution session is no longer active.");
        var resolutionPath = Path.Combine(attemptDirectory, "ocr-resolution.json");
        var resolutionRef = PathPolicy.ToJobRelative(accepted.JobRoot, resolutionPath);
        if (File.Exists(resolutionPath))
            throw new OrchestratorDispatchException("ocr.session_already_resolved", "OCR human-resolution session was already resolved.");

        var inputs = new List<ArtifactReference>
        {
            session.SourceProofreadArtifact, session.SourceLearningArtifact, session.SourceImageProvenanceArtifact,
            session.SourceOriginalImageArtifact, session.OriginalTextRef, session.CandidateTextRef
        };
        JsonElement? interactiveCommandRoot = null;
        if (action.CorrectionRef is not null)
        {
            await ValidateOcrCorrectionArtifactAsync(accepted, action.CorrectionRef, cancellationToken);
            var actionPath = PathPolicy.ResolveJobRelative(accepted.JobRoot, accepted.Command.PayloadRef);
            var correctionManifest = await CoreArtifactIO.ReadManifestAsync(accepted.JobRoot, action.CorrectionRef, cancellationToken);
            var correctionFile = correctionManifest.Files.Single();
            interactiveCommandRoot = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = "knowledge-clipper-ocr-interactive-command-root.v1",
                accepted.TaskId,
                commandId = accepted.Command.CommandId,
                operationId = accepted.Command.OperationId,
                idempotencyKey = accepted.Command.IdempotencyKey,
                acceptanceRunId = accepted.AcceptanceRunId,
                resolutionRunId = runId,
                payloadRef = accepted.Command.PayloadRef,
                payloadSha256 = Integrity.Sha256Hex(await File.ReadAllBytesAsync(actionPath, cancellationToken)).ToLowerInvariant(),
                acceptedEventRef = accepted.DurableEventRef,
                resolutionRef,
                action = action.Action,
                action.TargetToken,
                correctionRef = action.CorrectionRef,
                correctionPayload = new { path = correctionFile.Path, sha256 = correctionFile.Sha256.ToLowerInvariant(), correctionFile.Bytes, correctionFile.MediaType },
                diffsSha256 = Integrity.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(action.Diffs, ContractJson.Options)).ToLowerInvariant()
            }, ContractJson.Options);
        }
        PipelinePlan? plan = null;
        if (action.Action != "cancel")
        {
            if (!File.Exists(resolverConfigurationPath))
                throw new OrchestratorDispatchException("resolver.configuration_missing", $"Desktop profile resolver configuration is missing: {resolverConfigurationPath}");
            var adapterSnapshot = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = "knowledge-clipper-ocr-human-resolution-input-adapter.v1",
                resolutionId = $"resolution-{accepted.Command.CommandId}", action = action.Action, action.Reason,
                session.SourceItemId, action.Diffs, targetToken = action.TargetToken,
                commandProof = new { commandId = accepted.Command.CommandId, operationId = accepted.Command.OperationId, acceptedEventRef = accepted.DurableEventRef, resolutionRef },
                interactiveCommandRoot,
                humanProvenance = action.Action == "accept_original" ? "human_accepted_original" : "human_submitted_correction",
                ocrRerun = false
            }, ContractJson.Options);
            plan = await new DesktopIntentResolver().ResolveOcrHumanResolutionPlanAsync(
                accepted.JobRoot, accepted.TaskId, runId, inputs, adapterSnapshot,
                registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                DesktopIntentResolver.LoadConfiguration(resolverConfigurationPath), cancellationToken);
        }
        try
        {
            await AtomicFile.WriteJsonOnceAsync(resolutionPath, new
            {
                schemaVersion = "knowledge-clipper-ocr-human-resolution.v1", action.Action, action.TargetToken, action.Reason,
                session.Binding, session.SourceItemId, correctionRef = action.CorrectionRef, diffs = action.Diffs,
                commandId = accepted.Command.CommandId, acceptedEventRef = accepted.DurableEventRef, resolutionRunId = runId,
                resolvedAt = DateTimeOffset.UtcNow, provenance = "human_submitted", ocrRerun = false
            }, cancellationToken);
        }
        catch (IOException)
        {
            throw new OrchestratorDispatchException("ocr.session_already_resolved", "OCR human-resolution session was resolved concurrently.");
        }
        await store.AppendAsync("task.ocr_human_resolution_resolved", accepted.TaskId,
            new { action.Action, action.TargetToken, session.Binding, session.SourceItemId, sessionRef = PathPolicy.ToJobRelative(accepted.JobRoot, sessionPath), ocrRerun = false },
            session.Binding.StageId, session.Binding.AttemptId, accepted.Command.CommandId, accepted.Command.IdempotencyKey, cancellationToken);
        return plan;
    }

    private static async Task ValidateOcrCorrectionArtifactAsync(AcceptedCommand accepted, ArtifactReference reference, CancellationToken cancellationToken)
    {
        var expectedManifest = $"artifacts/{reference.ArtifactRef}/artifact.json";
        if (reference.ManifestPath != expectedManifest)
            throw new OrchestratorDispatchException("ocr.correction_path_invalid", "OCR correction manifest is not at its canonical artifact-owned path.");
        var manifest = await CoreArtifactIO.ReadManifestAsync(accepted.JobRoot, reference, cancellationToken);
        var expectedPayload = $"commands/{accepted.Command.CommandId}/ocr-correction/payload.txt";
        if (manifest.Files.Count != 1 || manifest.Files[0].Path != expectedPayload || manifest.Files[0].MediaType != "text/plain")
            throw new OrchestratorDispatchException("ocr.correction_path_invalid", "OCR correction payload is not at its canonical command-owned path.");
        using var manifestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(accepted.JobRoot, reference.ManifestPath), cancellationToken));
        var producer = manifestDocument.RootElement.GetProperty("producer");
        if (producer.GetProperty("stageId").GetString() != "desktop-ocr-human-resolution" || producer.GetProperty("workerId").GetString() != "desktop-capture")
            throw new OrchestratorDispatchException("ocr.correction_owner_invalid", "OCR correction artifact is not owned by the Desktop human-resolution producer.");
        var text = await File.ReadAllTextAsync(PathPolicy.ResolveJobRelative(accepted.JobRoot, expectedPayload), new System.Text.UTF8Encoding(false, true), cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new OrchestratorDispatchException("ocr.correction_invalid", "OCR correction text is empty.");
    }

    private static OcrHumanResolutionSession? TryReadOcrResolutionSession(string path)
    {
        try { return JsonSerializer.Deserialize<OcrHumanResolutionSession>(File.ReadAllText(path), ContractJson.Options); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool SameReference(ArtifactReference left, ArtifactReference right) =>
        left.ArtifactRef == right.ArtifactRef && left.Kind == right.Kind && left.ContractVersion == right.ContractVersion &&
        left.ManifestPath == right.ManifestPath && left.ExpectedSha256.Equals(right.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

    private async Task<PipelinePlan> ResolveQuestionResumeAsync(
        AcceptedCommand accepted,
        QuestionWorkflowAction action,
        string runId,
        EventStore store,
        CancellationToken cancellationToken)
    {
        var runsRoot = Path.Combine(accepted.JobRoot, "runs");
        var candidates = Directory.Exists(runsRoot)
            ? Directory.GetFiles(runsRoot, "question-session.json", SearchOption.AllDirectories)
                .Select(path => (Path: path, Session: TryReadQuestionSession(path)))
                .Where(item => item.Session?.TargetToken == action.TargetToken)
                .ToArray()
            : Array.Empty<(string Path, QuestionWorkflowSession? Session)>();
        if (candidates.Length == 0)
            throw new OrchestratorDispatchException("question.session_not_found", "Question target token is unknown for this task.");
        if (candidates.Length != 1)
            throw new OrchestratorDispatchException("question.session_ambiguous", "Question target token does not resolve uniquely.");
        var sessionPath = candidates[0].Path;
        var session = candidates[0].Session!;
        if (session.SchemaVersion != "knowledge-clipper-question-session.v2" || session.Binding.TaskId != accepted.TaskId ||
            !ValidIdentifier(session.Binding.TaskId) || !ValidIdentifier(session.Binding.RunId) || !ValidIdentifier(session.Binding.StageId) ||
            !ValidIdentifier(session.Binding.AttemptId) || !ValidIdentifier(session.Binding.OperationId) || session.AnsweredTurnCount < 1 ||
            !ValidIdentifier(session.LatestAnswerId))
            throw new OrchestratorDispatchException("question.session_binding_mismatch", "Question action does not match the authoritative session binding.");
        var expectedSessionPath = PathPolicy.ResolveJobRelative(accepted.JobRoot,
            $"runs/{session.Binding.RunId}/stages/{session.Binding.StageId}/attempts/{session.Binding.AttemptId}/question-session.json");
        if (!Path.GetFullPath(sessionPath).Equals(expectedSessionPath, StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("question.session_binding_mismatch", "Question session file does not match its binding.");
        if (action.Action == "question.follow-up" && action.TurnIndex != session.AnsweredTurnCount)
            throw new OrchestratorDispatchException("question.turn_order_invalid", "Follow-up turnIndex is stale or out of order.");

        var sourceRequestPath = Path.Combine(Path.GetDirectoryName(sessionPath)!, "request.json");
        var sourceResultPath = Path.Combine(Path.GetDirectoryName(sessionPath)!, "result.json");
        var sourceRequest = File.Exists(sourceRequestPath)
            ? JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(sourceRequestPath, cancellationToken), ContractJson.Options) : null;
        var sourceResult = File.Exists(sourceResultPath)
            ? JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(sourceResultPath, cancellationToken), ContractJson.Options) : null;
        if (sourceRequest is null || sourceResult is null || sourceRequest.TaskId != session.Binding.TaskId || sourceRequest.RunId != session.Binding.RunId ||
            sourceRequest.StageId != session.Binding.StageId || sourceRequest.AttemptId != session.Binding.AttemptId || sourceRequest.OperationId != session.Binding.OperationId ||
            sourceRequest.WorkerId != "content.deepseek" || sourceRequest.Operation != "question.answer" || sourceResult.Status != "completed" ||
            sourceResult.Outputs.Count(output => output.ArtifactRef == session.SourceAnswerArtifact.ArtifactRef && output.Kind == "question.answer" &&
                output.ManifestPath == session.SourceAnswerArtifact.ManifestPath && output.ExpectedSha256.Equals(session.SourceAnswerArtifact.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new OrchestratorDispatchException("question.session_binding_mismatch", "Question session does not match its immutable worker request and result.");
        await CoreArtifactIO.ValidateReferenceAsync(accepted.JobRoot, session.SourceAnswerArtifact, cancellationToken);
        await CoreArtifactIO.ValidateReferenceAsync(accepted.JobRoot, session.DisplayAnswerArtifact, cancellationToken);
        var sourceRunStore = new EventStore(accepted.JobRoot, session.Binding.RunId);
        if (TaskStateMachine.Project(sourceRunStore.ReadAll()) != "waiting_input")
            throw new OrchestratorDispatchException("question.session_not_active", "Question session is no longer active.");
        var resolutionPath = Path.Combine(Path.GetDirectoryName(sessionPath)!, "question-resolution.json");
        if (File.Exists(resolutionPath))
            throw new OrchestratorDispatchException("question.session_already_resolved", "Question session has already been resolved.");

        var inputs = new List<ArtifactReference> { session.SourceAnswerArtifact };
        if (action.QuestionTurnRef is not null)
        {
            await CoreArtifactIO.ValidateReferenceAsync(accepted.JobRoot, action.QuestionTurnRef, cancellationToken);
            inputs.Add(action.QuestionTurnRef);
        }
        if (action.CardStructureRef is not null)
        {
            await CoreArtifactIO.ValidateReferenceAsync(accepted.JobRoot, action.CardStructureRef, cancellationToken);
            inputs.Add(action.CardStructureRef);
        }
        var answerPhase = action.Action == "question.follow-up";
        if (!File.Exists(resolverConfigurationPath))
            throw new OrchestratorDispatchException("resolver.configuration_missing", $"Desktop profile resolver configuration is missing: {resolverConfigurationPath}");
        var configuration = DesktopIntentResolver.LoadConfiguration(resolverConfigurationPath);
        JsonElement? interactiveCommandRoot = null;
        if (answerPhase)
        {
            var actionPath = PathPolicy.ResolveJobRelative(accepted.JobRoot, accepted.Command.PayloadRef);
            interactiveCommandRoot = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = "knowledge-clipper-interactive-command-root.v1",
                accepted.TaskId,
                commandId = accepted.Command.CommandId,
                operationId = accepted.Command.OperationId,
                idempotencyKey = accepted.Command.IdempotencyKey,
                acceptanceRunId = accepted.AcceptanceRunId,
                resolutionRunId = runId,
                payloadRef = accepted.Command.PayloadRef,
                payloadSha256 = Integrity.Sha256Hex(await File.ReadAllBytesAsync(actionPath, cancellationToken)).ToLowerInvariant(),
                acceptedEventRef = accepted.DurableEventRef,
                resolutionRef = PathPolicy.ToJobRelative(accepted.JobRoot, resolutionPath),
                action = action.Action,
                action.TargetToken,
                action.TurnIndex,
                questionTurnRef = action.QuestionTurnRef
            }, ContractJson.Options);
        }
        var plan = await new DesktopIntentResolver().ResolveQuestionResumePlanAsync(
            accepted.JobRoot, accepted.TaskId, runId, answerPhase, action.TurnIndex, inputs, interactiveCommandRoot,
            registry ?? throw new InvalidOperationException("Host registry is not initialized."), configuration, cancellationToken);

        try
        {
            await AtomicFile.WriteJsonOnceAsync(resolutionPath, new
            {
                schemaVersion = "knowledge-clipper-question-resolution.v2",
                action.Action,
                action.TargetToken,
                session.Binding,
                action.TurnIndex,
                questionTurnRef = action.QuestionTurnRef,
                cardStructureRef = action.CardStructureRef,
                commandId = accepted.Command.CommandId,
                resolvedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        catch (IOException)
        {
            throw new OrchestratorDispatchException("question.session_already_resolved", "Question session was resolved concurrently.");
        }
        await store.AppendAsync("task.question_action_resolved", accepted.TaskId,
            new { action.Action, action.TargetToken, session.Binding, action.TurnIndex, sessionRef = PathPolicy.ToJobRelative(accepted.JobRoot, sessionPath) },
            session.Binding.StageId, session.Binding.AttemptId, accepted.Command.CommandId, accepted.Command.IdempotencyKey, cancellationToken);
        return plan;
    }

    private static QuestionWorkflowSession? TryReadQuestionSession(string path)
    {
        try { return JsonSerializer.Deserialize<QuestionWorkflowSession>(File.ReadAllText(path), ContractJson.Options); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<PipelinePlan> ResolveLanguagePracticeResumeAsync(
        AcceptedCommand accepted,
        LanguagePracticeChoice choice,
        string runId,
        EventStore store,
        CancellationToken cancellationToken)
    {
        var runsRoot = Path.Combine(accepted.JobRoot, "runs");
        var candidates = Directory.Exists(runsRoot)
            ? Directory.GetFiles(runsRoot, "decision.json", SearchOption.AllDirectories)
                .Select(path => (Path: path, Decision: TryReadLanguageDecision(path)))
                .Where(item => item.Decision?.TargetToken == choice.TargetToken)
                .ToArray()
            : Array.Empty<(string Path, LanguagePracticeDecision? Decision)>();
        if (candidates.Length == 0)
            throw new OrchestratorDispatchException("decision.not_found", "Language-practice target token is unknown for this task.");
        if (candidates.Length != 1)
            throw new OrchestratorDispatchException("decision.token_ambiguous", "Language-practice target token does not resolve uniquely.");
        var decisionPath = candidates[0].Path;
        var decision = candidates[0].Decision!;
        if (decision.SchemaVersion != "knowledge-clipper-language-practice-decision.v2" ||
            decision.DecisionKind != choice.DecisionKind || decision.TargetToken != choice.TargetToken ||
            decision.Binding.TaskId != accepted.TaskId || !decision.Choices.Contains(choice.Choice, StringComparer.Ordinal) ||
            !ValidIdentifier(decision.Binding.TaskId) || !ValidIdentifier(decision.Binding.RunId) || !ValidIdentifier(decision.Binding.StageId) ||
            !ValidIdentifier(decision.Binding.AttemptId) || !ValidIdentifier(decision.Binding.OperationId))
            throw new OrchestratorDispatchException("decision.binding_mismatch", "Language-practice choice does not match the active decision.");
        var expectedDecisionPath = PathPolicy.ResolveJobRelative(
            accepted.JobRoot,
            $"runs/{decision.Binding.RunId}/stages/{decision.Binding.StageId}/attempts/{decision.Binding.AttemptId}/decision.json");
        if (!Path.GetFullPath(decisionPath).Equals(expectedDecisionPath, StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorDispatchException("decision.binding_mismatch", "Language-practice decision file does not match its authoritative binding.");
        var sourceRequestPath = Path.Combine(Path.GetDirectoryName(decisionPath)!, "request.json");
        var sourceRequest = File.Exists(sourceRequestPath)
            ? JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(sourceRequestPath, cancellationToken), ContractJson.Options)
            : null;
        if (sourceRequest is null || sourceRequest.TaskId != decision.Binding.TaskId || sourceRequest.RunId != decision.Binding.RunId ||
            sourceRequest.StageId != decision.Binding.StageId || sourceRequest.AttemptId != decision.Binding.AttemptId ||
            sourceRequest.OperationId != decision.Binding.OperationId || sourceRequest.WorkerId != "content.deepseek")
            throw new OrchestratorDispatchException("decision.binding_mismatch", "Language-practice decision binding does not match its immutable worker request.");
        var decisionRunStore = new EventStore(accepted.JobRoot, decision.Binding.RunId);
        if (TaskStateMachine.Project(decisionRunStore.ReadAll()) != "waiting_input")
            throw new OrchestratorDispatchException("decision.not_active", "Language-practice decision is no longer active.");
        var resolutionPath = Path.Combine(Path.GetDirectoryName(decisionPath)!, "decision-resolution.json");
        if (File.Exists(resolutionPath))
            throw new OrchestratorDispatchException("decision.already_resolved", "Language-practice decision has already been resolved.");

        var planPath = ResolveInitialPlan(accepted.JobRoot);
        PipelinePlan plan;
        using (var planDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(planPath, cancellationToken)))
        {
            var schemaVersion = planDocument.RootElement.GetProperty("schemaVersion").GetString();
            if (schemaVersion is "desktop-intent.v1" or "knowledge-clipper-intent.v2")
            {
                if (!File.Exists(resolverConfigurationPath))
                    throw new OrchestratorDispatchException("resolver.configuration_missing", $"Desktop profile resolver configuration is missing: {resolverConfigurationPath}");
                plan = await new DesktopIntentResolver().ResolveAsync(
                    accepted.JobRoot,
                    planPath,
                    registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                    runId,
                    DesktopIntentResolver.LoadConfiguration(resolverConfigurationPath),
                    cancellationToken);
            }
            else
            {
                plan = JsonSerializer.Deserialize<PipelinePlan>(planDocument.RootElement.GetRawText(), ContractJson.Options)
                    ?? throw new OrchestratorDispatchException("decision.resume_plan_invalid", "Language-practice resume plan is empty.");
            }
        }
        if (plan.TaskId != accepted.TaskId)
            throw new OrchestratorDispatchException("decision.resume_plan_invalid", "Language-practice resume plan taskId mismatch.");
        var contentStages = plan.Stages.Where(stage => stage.WorkerId == "content.deepseek").ToArray();
        if (contentStages.Length != 1)
            throw new OrchestratorDispatchException("decision.resume_plan_invalid", "Language-practice resume requires exactly one content.deepseek stage.");
        var selectedStageIds = new HashSet<string>(StringComparer.Ordinal) { contentStages[0].StageId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var stage in plan.Stages)
                if (!selectedStageIds.Contains(stage.StageId) && stage.DependsOn.Any(selectedStageIds.Contains))
                    added |= selectedStageIds.Add(stage.StageId);
        }
        var adapterSnapshot = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "knowledge-clipper-language-practice-input-adapter.v2",
            choice = choice.Choice
        }, ContractJson.Options);
        var stages = plan.Stages
            .Where(stage => selectedStageIds.Contains(stage.StageId))
            .Select(stage => stage.WorkerId == "content.deepseek"
                ? stage with
                {
                    DependsOn = Array.Empty<string>(),
                    InputRefs = sourceRequest.InputRefs,
                    InputBindings = Array.Empty<StageOutputBinding>(),
                    InputAdapter = new StageInputAdapter("content-input-language-choice.v2", MappingSnapshot: adapterSnapshot),
                    ConfigSnapshot = sourceRequest.ConfigSnapshot,
                    Payload = sourceRequest.Payload
                }
                : stage)
            .ToArray();
        var resumePlan = plan with { SchemaVersion = "2.0.0", PlanVersion = "2.1.0", Stages = stages };

        try
        {
            await AtomicFile.WriteJsonOnceAsync(
                resolutionPath,
                new
                {
                    schemaVersion = "knowledge-clipper-language-practice-resolution.v2",
                    choice.DecisionKind,
                    choice.TargetToken,
                    decision.Binding,
                    choice.Choice,
                    commandId = accepted.Command.CommandId,
                    resolvedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
        catch (IOException)
        {
            throw new OrchestratorDispatchException("decision.already_resolved", "Language-practice decision was resolved concurrently.");
        }
        await store.AppendAsync(
            "task.decision_resolved",
            accepted.TaskId,
            new { choice.DecisionKind, choice.TargetToken, decision.Binding, choice.Choice, decisionRef = PathPolicy.ToJobRelative(accepted.JobRoot, decisionPath) },
            decision.Binding.StageId,
            decision.Binding.AttemptId,
            accepted.Command.CommandId,
            accepted.Command.IdempotencyKey,
            cancellationToken);
        return resumePlan;
    }

    private static LanguagePracticeDecision? TryReadLanguageDecision(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<LanguagePracticeDecision>(File.ReadAllText(path), ContractJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ValidIdentifier(string value) =>
        value.Length is >= 1 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static async Task<DesktopStageControl?> ReadPlaybackControlAsync(AcceptedCommand accepted, CancellationToken cancellationToken)
    {
        if (accepted.Command.Type != "task.provide_input") return null;
        var path = PathPolicy.ResolveJobRelative(accepted.JobRoot, accepted.Command.PayloadRef);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetString() != "desktop-action.v1") return null;
        if (!root.TryGetProperty("action", out var actionElement) || actionElement.ValueKind != JsonValueKind.String ||
            actionElement.GetString() is not ("playback.stop" or "playback.replay")) return null;
        var names = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals(new[] { "schemaVersion", "action" }))
            throw new OrchestratorDispatchException("control.payload_invalid", "Playback control must contain only schemaVersion and action.");
        var control = JsonSerializer.Deserialize<DesktopStageControl>(root.GetRawText(), ContractJson.Options)
            ?? throw new OrchestratorDispatchException("control.payload_invalid", "Playback control payload is empty.");
        if (control.Action is not ("playback.stop" or "playback.replay")) return null;
        return control;
    }

    private static async Task StopPlaybackStageAsync(AcceptedCommand accepted, EventStore store, CancellationToken cancellationToken)
    {
        _ = store;
        var jobsRoot = Directory.GetParent(accepted.JobRoot)?.FullName
            ?? throw new OrchestratorDispatchException("control.playback_not_active", "Task ownership root is unavailable.");
        var receipt = await new StageControlHandler(jobsRoot).HandleAsync(
            new StageControlRequest(accepted.TaskId, "speech.playback", "current-active", "user_stop"),
            cancellationToken);
        if (!receipt.Accepted)
            throw new OrchestratorDispatchException(receipt.Error?.Code ?? "control.playback_not_active", receipt.Error?.Message ?? "Playback stop was rejected.");
    }

    private async Task<PipelinePlan> ResolvePlaybackReplayAsync(AcceptedCommand accepted, CancellationToken cancellationToken)
    {
        var candidates = Directory.GetFiles(Path.Combine(accepted.JobRoot, "runs"), "result.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, Result: JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(path), ContractJson.Options)))
            .Where(item => item.Result?.Status == "completed" && item.Result.Outputs.Any(output => output.Kind == "audio.speech"))
            .OrderByDescending(item => item.Result!.FinishedAt)
            .ToArray();
        if (candidates.Length == 0)
            throw new OrchestratorDispatchException("control.replay_audio_missing", "Replay requires a prior completed audio.speech artifact.");
        var sourceResult = candidates[0].Result!;
        await WorkerProcessRunner.ValidatePublishedOutputsAsync(accepted.JobRoot, sourceResult, cancellationToken);
        var audio = sourceResult.Outputs.Single(output => output.Kind == "audio.speech");
        var sourceRequestPath = Path.Combine(Path.GetDirectoryName(candidates[0].Path)!, "request.json");
        var sourceRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(sourceRequestPath, cancellationToken), ContractJson.Options)
            ?? throw new OrchestratorDispatchException("control.replay_source_invalid", "Replay source request is invalid.");
        var speech = (registry ?? throw new InvalidOperationException("Host registry is not initialized.")).Workers.SingleOrDefault(item => item.WorkerId == "speech.edge" && item.EnabledByDefault)
            ?? throw new OrchestratorDispatchException("resolver.worker_missing", "Replay requires enabled speech.edge worker.");
        var stage = new PipelineStage(
            "speech-playback", speech.WorkerId, "speech.playback", Array.Empty<string>(), new[] { audio }, Array.Empty<string>(),
            3_600_000, new RetryPolicy(1, 0), sourceRequest.ConfigSnapshot, JsonSerializer.SerializeToElement(new { }),
            Array.Empty<StageOutputBinding>(), null,
            new StageCompletionPolicy(new[] { "user_stop" }, "succeeded-with-playback-stopped"));
        return new PipelinePlan("1.1.0", "1.1.0", accepted.TaskId, 1, new[] { stage });
    }

    private string ResolveInitialPlan(string jobRoot)
    {
        var canonicalManifest = Path.Combine(jobRoot, "task.manifest.v2.json");
        var manifestPath = File.Exists(canonicalManifest) ? canonicalManifest : Path.Combine(jobRoot, "task.manifest.v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var planRef = manifest.RootElement.GetProperty("initialPlanRef").GetString()
            ?? throw new InvalidDataException("Task manifest initialPlanRef is empty.");
        return PathPolicy.ResolveJobRelative(jobRoot, planRef);
    }

    private async Task ReplayUndispatchedCommandsAsync(CancellationToken cancellationToken)
    {
        foreach (var jobRoot in Directory.GetDirectories(jobsRoot))
        {
            var runsRoot = Path.Combine(jobRoot, "runs");
            if (!Directory.Exists(runsRoot))
                continue;
            var eventFiles = Directory.GetFiles(runsRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}events{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var events = eventFiles
                .Select(path => (Path: path, Event: JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options)
                    ?? throw new InvalidDataException($"Invalid event during replay: {path}")))
                .ToArray();
            var started = events
                .Where(item => item.Event.Type == "command.execution_started" && item.Event.CommandId is not null)
                .Select(item => item.Event.CommandId!)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in events.Where(item => item.Event.Type == "command.accepted" && item.Event.CommandId is not null))
            {
                var taskEvent = item.Event;
                var type = taskEvent.Payload.GetProperty("type").GetString();
                if (type is not ("task.submit" or "task.retry" or "task.provide_input") || started.Contains(taskEvent.CommandId!))
                    continue;
                var operationId = taskEvent.Payload.GetProperty("operationId").GetString()!;
                var payloadRef = taskEvent.Payload.GetProperty("payloadRef").GetString()!;
                var command = new TaskCommand(
                    "1.0.0", taskEvent.CommandId!, operationId, taskEvent.TaskId, type, taskEvent.Timestamp,
                    taskEvent.IdempotencyKey ?? throw new InvalidDataException("Accepted command has no idempotency key."), payloadRef, null);
                await EnqueueAsync(
                    new AcceptedCommand(command, taskEvent.TaskId, jobRoot, taskEvent.RunId!, PathPolicy.ToJobRelative(jobRoot, item.Path)),
                    cancellationToken);
            }
        }
    }

    private async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        foreach (var jobRoot in Directory.GetDirectories(jobsRoot))
        {
            var runsRoot = Path.Combine(jobRoot, "runs");
            if (!Directory.Exists(runsRoot))
                continue;
            foreach (var runDirectory in Directory.GetDirectories(runsRoot))
            {
                var runId = Path.GetFileName(runDirectory);
                var recovered = await new OrchestratorEngine().RecoverAsync(
                    jobRoot,
                    registry ?? throw new InvalidOperationException("Host registry is not initialized."),
                    runId,
                    cancellationToken);
                if (recovered == 0)
                    continue;
                // jobs/<taskId> is the durable ownership boundary. Recovery must
                // not depend on an arbitrary first request remaining parseable.
                var taskId = Path.GetFileName(jobRoot);
                var store = new EventStore(jobRoot, runId);
                var state = TaskStateMachine.Project(store.ReadAll());
                if (!TaskStateMachine.TerminalStates.Contains(state))
                {
                    if (state == "created")
                    {
                        await store.AppendAsync("task.state", taskId, new { status = "validating" }, cancellationToken: cancellationToken);
                        state = "validating";
                    }
                    var hasSuccess = await HasValidSuccessfulResultAsync(jobRoot, runDirectory, cancellationToken);
                    var terminal = state == "running" && hasSuccess ? "partial" : "failed";
                    await store.AppendAsync("task.state", taskId, new { status = terminal }, cancellationToken: cancellationToken);
                }
                await store.AppendAsync("run.recovered", taskId, new { recoveredAttempts = recovered }, cancellationToken: cancellationToken);
            }
        }
    }

    private static async Task EnsureFailedStateAsync(EventStore store, string taskId)
    {
        var state = TaskStateMachine.Project(store.ReadAll());
        if (TaskStateMachine.TerminalStates.Contains(state))
            return;
        if (state == "created")
        {
            await store.AppendAsync("task.state", taskId, new { status = "validating" }, cancellationToken: CancellationToken.None);
            state = "validating";
        }
        if (state != "failed")
            await store.AppendAsync("task.state", taskId, new { status = "failed" }, cancellationToken: CancellationToken.None);
    }

    private static async Task<bool> HasValidSuccessfulResultAsync(string jobRoot, string runDirectory, CancellationToken cancellationToken)
    {
        foreach (var requestPath in Directory.GetFiles(runDirectory, "request.json", SearchOption.AllDirectories))
        {
            var resultPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "result.json");
            if (!File.Exists(resultPath))
                continue;
            try
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken), ContractJson.Options)
                    ?? throw new InvalidDataException("Recovery projection request is empty.");
                var result = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(resultPath, cancellationToken), ContractJson.Options)
                    ?? throw new InvalidDataException("Recovery projection result is empty.");
                WorkerProcessRunner.ValidateResultIdentity(request, request.WorkerId, result);
                await WorkerProcessRunner.ValidatePublishedOutputsAsync(jobRoot, result, cancellationToken);
                if (result.Status is "completed" or "partial")
                    return true;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                // Invalid terminal evidence was already retained and diagnosed by
                // RecoverAsync.  It must never count as successful work here.
            }
        }
        return false;
    }

    private static string Trim(string value) => value.Length <= 1000 ? value : value[..1000];
}

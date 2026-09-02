using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed record RetentionEvaluation(
    string TaskId,
    string RetentionClass,
    string Status,
    bool Eligible,
    string Reason,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset? TerminalAt,
    bool UnknownExternalState,
    bool AttentionRequired,
    bool ExplicitSafeToDelete);

public sealed record RetentionQuarantineRecord(
    string TaskId,
    string SourceJobRoot,
    string QuarantineJobRoot,
    DateTimeOffset QuarantinedAt,
    string Reason);

public sealed class StateAwareRetentionService
{
    public IReadOnlyList<RetentionEvaluation> Evaluate(string jobsRoot, DateTimeOffset now, TimeSpan minimumTerminalAge)
    {
        jobsRoot = Path.GetFullPath(jobsRoot);
        if (!Directory.Exists(jobsRoot)) return Array.Empty<RetentionEvaluation>();
        return Directory.GetDirectories(jobsRoot)
            .Select(job => EvaluateJob(job, now, minimumTerminalAge))
            .Where(item => item is not null)
            .Cast<RetentionEvaluation>()
            .OrderBy(item => item.TaskId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> PurgeEligibleAsync(
        string jobsRoot,
        IReadOnlyList<RetentionEvaluation> evaluations,
        CancellationToken cancellationToken)
    {
        jobsRoot = Path.GetFullPath(jobsRoot);
        var removed = new List<string>();
        foreach (var evaluation in evaluations.Where(item => item.Eligible))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidIdentifier(evaluation.TaskId)) throw new InvalidDataException("Retention taskId is unsafe.");
            var jobRoot = Path.GetFullPath(Path.Combine(jobsRoot, evaluation.TaskId));
            if (!Path.GetDirectoryName(jobRoot)!.Equals(jobsRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(jobRoot))
                throw new InvalidDataException("Retention target escaped the jobs root or disappeared.");
            var fresh = EvaluateJob(jobRoot, DateTimeOffset.UtcNow, TimeSpan.Zero);
            if (fresh is null || !fresh.Eligible || fresh.RetentionClass != evaluation.RetentionClass || fresh.Status != evaluation.Status ||
                !fresh.ExplicitSafeToDelete || fresh.UnknownExternalState || fresh.AttentionRequired)
                throw new InvalidOperationException($"Retention eligibility drifted for {evaluation.TaskId}.");
            Directory.Delete(jobRoot, recursive: true);
            removed.Add(evaluation.TaskId);
            await Task.Yield();
        }
        return removed;
    }

    // Production callers must quarantine first.  Physical deletion remains an
    // explicit, separately authorized maintenance operation; this method only
    // moves a freshly revalidated eligible task into a recoverable root.
    public async Task<IReadOnlyList<RetentionQuarantineRecord>> QuarantineEligibleAsync(
        string jobsRoot,
        string quarantineRoot,
        IReadOnlyList<RetentionEvaluation> evaluations,
        CancellationToken cancellationToken)
    {
        jobsRoot = Path.GetFullPath(jobsRoot);
        quarantineRoot = Path.GetFullPath(quarantineRoot);
        if (quarantineRoot.StartsWith(jobsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Retention quarantine root must not be nested inside jobs root.");

        var records = new List<RetentionQuarantineRecord>();
        foreach (var evaluation in evaluations.Where(item => item.Eligible))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidIdentifier(evaluation.TaskId)) throw new InvalidDataException("Retention taskId is unsafe.");
            var source = Path.GetFullPath(Path.Combine(jobsRoot, evaluation.TaskId));
            if (!Path.GetDirectoryName(source)!.Equals(jobsRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source))
                throw new InvalidDataException("Retention target escaped the jobs root or disappeared.");

            var fresh = EvaluateJob(source, DateTimeOffset.UtcNow, TimeSpan.Zero);
            if (fresh is null || !fresh.Eligible || fresh.RetentionClass != evaluation.RetentionClass || fresh.Status != evaluation.Status ||
                !fresh.ExplicitSafeToDelete || fresh.UnknownExternalState || fresh.AttentionRequired)
                throw new InvalidOperationException($"Retention eligibility drifted for {evaluation.TaskId}.");

            Directory.CreateDirectory(quarantineRoot);
            var target = Path.Combine(quarantineRoot, $"{evaluation.TaskId}--{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
            if (Directory.Exists(target)) throw new IOException("Retention quarantine target already exists.");
            Directory.Move(source, target);
            records.Add(new RetentionQuarantineRecord(evaluation.TaskId, source, target, DateTimeOffset.UtcNow, "eligible_explicitly_reconciled_ephemeral"));
            await Task.Yield();
        }
        return records;
    }

    private static RetentionEvaluation? EvaluateJob(string jobRoot, DateTimeOffset now, TimeSpan minimumTerminalAge)
    {
        var manifestPath = File.Exists(Path.Combine(jobRoot, "task.manifest.v2.json"))
            ? Path.Combine(jobRoot, "task.manifest.v2.json")
            : Path.Combine(jobRoot, "task.manifest.v1.json");
        if (!File.Exists(manifestPath)) return null;
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var taskId = manifest.RootElement.GetProperty("taskId").GetString()!;
        if (taskId != Path.GetFileName(jobRoot) || !ValidIdentifier(taskId))
            return new RetentionEvaluation(taskId, "unknown", "invalid", false, "manifest_identity_invalid", now, null, false, true, false);
        var retentionClass = manifest.RootElement.GetProperty("retentionClass").GetString()!;
        var events = ReadEvents(jobRoot);
        var latest = LatestStatefulRun(events);
        var status = latest.Count == 0 ? "created" : TaskStateMachine.Project(latest);
        var terminalAt = latest.Where(item => item.Type == "task.state" && item.Payload.TryGetProperty("status", out var state) && TaskStateMachine.TerminalStates.Contains(state.GetString()!))
            .Select(item => (DateTimeOffset?)item.Timestamp).LastOrDefault();
        var unknownExternal = Directory.Exists(Path.Combine(jobRoot, "runs")) && Directory.GetFiles(Path.Combine(jobRoot, "runs"), "result.json", SearchOption.AllDirectories)
            .Any(path => TryResultStatus(path) == "unknown_external_state");
        var attentionRequested = events.Any(item => item.Type is "stage.content_attention_required" or "task.question_answer_available" or "task.decision_requested");
        var attentionResolved = events.Any(item => item.Type is "task.ocr_human_resolution_resolved" or "task.question_action_resolved" or "task.decision_resolved");
        var attention = status == "waiting_input" || attentionRequested && !attentionResolved;
        var explicitSafe = events.Any(item => item.Type == "retention.safe_to_delete" && item.TaskId == taskId &&
            item.Payload.TryGetProperty("status", out var safeStatus) && safeStatus.GetString() == status);
        string reason;
        var eligible = false;
        if (retentionClass is "evidence" or "pinned" or "standard") reason = $"retention_class_{retentionClass}";
        else if (retentionClass != "ephemeral") reason = "retention_class_invalid";
        else if (!TaskStateMachine.TerminalStates.Contains(status)) reason = "task_not_terminal";
        else if (unknownExternal) reason = "unknown_external_state";
        else if (attention) reason = "attention_required";
        else if (terminalAt is null || now - terminalAt < minimumTerminalAge) reason = "terminal_age_not_met";
        else if (!explicitSafe) reason = "explicit_safe_to_delete_missing";
        else { eligible = true; reason = "eligible_explicitly_reconciled_ephemeral"; }
        return new RetentionEvaluation(taskId, retentionClass, status, eligible, reason, now, terminalAt, unknownExternal, attention, explicitSafe);
    }

    private static IReadOnlyList<TaskEvent> ReadEvents(string jobRoot)
    {
        var runsRoot = Path.Combine(jobRoot, "runs");
        if (!Directory.Exists(runsRoot)) return Array.Empty<TaskEvent>();
        return Directory.GetFiles(runsRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}events{Path.DirectorySeparatorChar}"))
            .Select(path => { try { return JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options); } catch { return null; } })
            .Where(item => item is not null).Cast<TaskEvent>().OrderBy(item => item.Timestamp).ToArray();
    }

    private static IReadOnlyList<TaskEvent> LatestStatefulRun(IReadOnlyList<TaskEvent> events) => events
        .GroupBy(item => item.RunId ?? string.Empty, StringComparer.Ordinal)
        .Where(group => group.Any(item => item.Type == "task.state"))
        .OrderByDescending(group => group.Max(item => item.Timestamp))
        .FirstOrDefault()?.OrderBy(item => item.Sequence).ToArray() ?? Array.Empty<TaskEvent>();

    private static string? TryResultStatus(string path)
    {
        try { using var document = JsonDocument.Parse(File.ReadAllBytes(path)); return document.RootElement.GetProperty("status").GetString(); }
        catch { return "invalid"; }
    }

    private static bool ValidIdentifier(string value) => value.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

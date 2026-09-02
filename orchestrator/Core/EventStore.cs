using System.Text.Json;
using System.Collections.Concurrent;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed class EventStore
{
    private readonly string jobRoot;
    private readonly string runId;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);

    public EventStore(string jobRoot, string runId)
    {
        this.jobRoot = Path.GetFullPath(jobRoot);
        this.runId = runId;
    }

    public string EventsDirectory => Path.Combine(jobRoot, "runs", runId, "events");

    public async Task<string> AppendAsync(
        string type,
        string taskId,
        object payload,
        string? stageId = null,
        string? attemptId = null,
        string? commandId = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var writeGate = WriteGates.GetOrAdd(EventsDirectory, _ => new SemaphoreSlim(1, 1));
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(EventsDirectory);
            var sequence = NextSequence();
            var eventId = $"event-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var safeType = type.Replace('.', '-').Replace('_', '-');
            var fileName = $"{sequence:D6}-{safeType}.json";
            var path = Path.Combine(EventsDirectory, fileName);
            var element = JsonSerializer.SerializeToElement(payload, ContractJson.Options);
            var envelope = new TaskEvent(
                "1.0.0",
                sequence,
                eventId,
                DateTimeOffset.UtcNow,
                type,
                taskId,
                runId,
                stageId,
                attemptId,
                commandId,
                idempotencyKey,
                element);
            await AtomicFile.WriteJsonOnceAsync(path, envelope, cancellationToken);
            return PathPolicy.ToJobRelative(jobRoot, path);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public IReadOnlyList<TaskEvent> ReadAll()
    {
        if (!Directory.Exists(EventsDirectory))
            return Array.Empty<TaskEvent>();
        return Directory.GetFiles(EventsDirectory, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => JsonSerializer.Deserialize<TaskEvent>(File.ReadAllText(path), ContractJson.Options)
                ?? throw new InvalidDataException($"Invalid task event: {path}"))
            .ToArray();
    }

    private long NextSequence()
    {
        if (!Directory.Exists(EventsDirectory))
            return 1;
        var last = Directory.GetFiles(EventsDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name?.Split('-', 2)[0])
            .Select(value => long.TryParse(value, out var parsed) ? parsed : 0)
            .DefaultIfEmpty(0)
            .Max();
        return last + 1;
    }
}

public static class TaskStateMachine
{
    public static readonly IReadOnlySet<string> TerminalStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "succeeded", "partial", "failed", "cancelled"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["created"] = Set("validating", "cancelling", "cancelled"),
            ["validating"] = Set("queued", "failed", "cancelling"),
            ["queued"] = Set("running", "cancelling", "cancelled"),
            ["running"] = Set("waiting_input", "retry_wait", "cancelling", "succeeded", "partial", "failed"),
            ["waiting_input"] = Set("queued", "running", "cancelling", "failed"),
            ["retry_wait"] = Set("queued", "running", "cancelling", "failed"),
            ["cancelling"] = Set("cancelled", "partial", "failed"),
            ["succeeded"] = Set(),
            ["partial"] = Set(),
            ["failed"] = Set(),
            ["cancelled"] = Set()
        };

    public static void EnsureTransition(string current, string next)
    {
        if (!Allowed.TryGetValue(current, out var targets) || !targets.Contains(next))
            throw new InvalidOperationException($"Illegal task state transition: {current} -> {next}");
    }

    public static string Project(IEnumerable<TaskEvent> events)
    {
        var state = "created";
        foreach (var taskEvent in events.Where(item => item.Type == "task.state"))
        {
            if (!taskEvent.Payload.TryGetProperty("status", out var statusProperty))
                continue;
            var next = statusProperty.GetString() ?? throw new InvalidDataException("task.state event has no status.");
            if (next == state)
                continue;
            EnsureTransition(state, next);
            state = next;
        }
        return state;
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}

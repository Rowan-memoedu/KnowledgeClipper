using System.Collections.Concurrent;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed class AuthorityBoundFakeWorkerRuntime : IWorkerRuntime
{
    private readonly IReadOnlyDictionary<string, AdmittedAuthorityWorker> _admitted;
    private readonly IReadOnlyDictionary<string, WorkerBinding> _bindings;
    private readonly TimeSpan _executionDelay;
    private readonly ConcurrentDictionary<string, byte> _ankiWrites = new(StringComparer.Ordinal);
    private int _contentAttempts;
    private int _ankiProbeAttempts;
    private int _ankiExportAttempts;
    private int _ankiExternalWriteCount;
    private int _ankiReadbackCount;

    private AuthorityBoundFakeWorkerRuntime(
        IReadOnlyDictionary<string, AdmittedAuthorityWorker> admitted,
        IReadOnlyDictionary<string, WorkerBinding> bindings,
        TimeSpan executionDelay)
    {
        _admitted = admitted;
        _bindings = bindings;
        _executionDelay = executionDelay;
    }

    public static async Task<AuthorityBoundFakeWorkerRuntime> CreateAsync(
        AdmittedCapabilityCatalog catalog,
        string repositoryRoot,
        CancellationToken cancellationToken,
        TimeSpan? executionDelay = null)
    {
        var admitted = await FrozenAuthorityWorkerAdmission.AdmitAsync(catalog, repositoryRoot, cancellationToken).ConfigureAwait(false);
        var bindings = catalog.Document.Operations
            .Where(item => item.Available && item.WorkerBinding.Mode == "worker" && item.WorkerBinding.WorkerId is not null)
            .GroupBy(item => item.WorkerBinding.WorkerId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().WorkerBinding, StringComparer.Ordinal);
        return new AuthorityBoundFakeWorkerRuntime(admitted, bindings, executionDelay ?? TimeSpan.Zero);
    }

    public async Task<WorkerExecutionResult> ExecuteAsync(WorkerInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_executionDelay > TimeSpan.Zero)
        {
            await Task.Delay(_executionDelay, cancellationToken).ConfigureAwait(false);
        }
        var workerId = invocation.Binding.WorkerId ?? throw new InvalidDataException("Worker invocation lacks workerId.");
        if (!_admitted.TryGetValue(workerId, out var admitted))
        {
            throw new InvalidDataException($"Worker '{workerId}' was not authority-admitted.");
        }
        if (invocation.Binding.Authority?.ExactWorkerVersion != admitted.WorkerVersion)
        {
            throw new InvalidDataException($"Worker '{workerId}' invocation drifted from its admitted exact version.");
        }

        return workerId switch
        {
            "content.deepseek" => ExecuteContent(invocation),
            "export.anki" => ExecuteAnki(invocation),
            _ => throw new InvalidDataException($"Worker '{workerId}' is outside the Phase 2 fake proxy boundary.")
        };
    }

    public WorkerRuntimeMetrics GetMetrics() => new(
        Volatile.Read(ref _contentAttempts),
        Volatile.Read(ref _ankiProbeAttempts),
        Volatile.Read(ref _ankiExportAttempts),
        Volatile.Read(ref _ankiExternalWriteCount),
        Volatile.Read(ref _ankiReadbackCount),
        RemNoteCalls: 0,
        MinerUCalls: 0,
        SpeakerCalls: 0,
        Mode: "authority-bound-independent-fake-proxies");

    public WorkerBinding GetAdmittedBinding(string workerId)
        => _bindings.TryGetValue(workerId, out var binding)
            ? binding
            : throw new InvalidDataException($"Worker '{workerId}' is not admitted.");

    private WorkerExecutionResult ExecuteContent(WorkerInvocation invocation)
    {
        Interlocked.Increment(ref _contentAttempts);
        var phase = GetRequiredString(invocation.Request, "phase");
        var payload = phase switch
        {
            "initial" or "follow-up" => StrictJson.CloneElement(new
            {
                status = "waiting_input",
                answerText = $"Fake {phase} answer: {GetRequiredString(invocation.Request, "prompt")}",
                provider = "independent-fake-content-proxy",
                externalWriteCount = 0
            }),
            "make-cards" => StrictJson.CloneElement(new
            {
                status = "cards_ready",
                front = GetRequiredString(invocation.Request, "question"),
                back = GetRequiredString(invocation.Request, "answer"),
                cardCount = 1,
                provider = "independent-fake-content-proxy",
                externalWriteCount = 0
            }),
            _ => throw new InvalidDataException($"Unsupported fake content phase '{phase}'.")
        };
        return new WorkerExecutionResult("completed", payload, Array.Empty<ArtifactReference>(), 0, true);
    }

    private WorkerExecutionResult ExecuteAnki(WorkerInvocation invocation)
    {
        var phase = GetRequiredString(invocation.Request, "phase");
        if (phase == "probe")
        {
            Interlocked.Increment(ref _ankiProbeAttempts);
            return new WorkerExecutionResult(
                "completed",
                StrictJson.CloneElement(new
                {
                    status = "ready",
                    worker = "export.anki",
                    provider = "independent-fake-anki-proxy",
                    readOnlyOperations = 18,
                    externalMutation = false,
                    mutatingActionsObserved = Array.Empty<string>(),
                    readiness = new
                    {
                        connectionReady = true,
                        selectedRoute = "canary",
                        selectedRouteReady = true,
                        readyForRequestedAction = true,
                        overallState = "ready",
                        nonBlockingIssues = Array.Empty<string>(),
                        blockingIssues = Array.Empty<string>()
                    },
                    externalWriteCount = 0
                }),
                Array.Empty<ArtifactReference>(),
                0,
                true);
        }

        if (phase != "export")
        {
            throw new InvalidDataException($"Unsupported fake Anki phase '{phase}'.");
        }

        Interlocked.Increment(ref _ankiExportAttempts);
        var idempotencyKey = GetRequiredString(invocation.Request, "idempotencyKey");
        var firstWrite = _ankiWrites.TryAdd(idempotencyKey, 0);
        if (firstWrite)
        {
            Interlocked.Increment(ref _ankiExternalWriteCount);
        }
        Interlocked.Increment(ref _ankiReadbackCount);
        return new WorkerExecutionResult(
            "completed",
            StrictJson.CloneElement(new
            {
                status = firstWrite ? "created" : "already_applied",
                worker = "export.anki",
                provider = "independent-fake-anki-proxy",
                writeId = "fake-anki-" + idempotencyKey[..16].ToLowerInvariant(),
                exactReadback = true,
                externalWriteCount = firstWrite ? 1 : 0
            }),
            Array.Empty<ArtifactReference>(),
            firstWrite ? 1 : 0,
            true);
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Fake worker request requires string '{name}'.");
        }
        return value.GetString()!;
    }
}

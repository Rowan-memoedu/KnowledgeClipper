using System.Text.Json;
using System.Security.Cryptography;
using KnowledgeClipper.Orchestrator.Core;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            WriteUsage();
            return args.Length == 0 ? 2 : 0;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var options = ParseOptions(args[1..]);
            return args[0] switch
            {
                "describe" => Describe(),
                "validate-registry" => await ValidateRegistryAsync(options, shutdown.Token),
                "run" => await RunPipelineAsync(options, shutdown.Token),
                "recover" => await RecoverAsync(options, shutdown.Token),
                "cancel" => await CancelAsync(options, shutdown.Token),
                "retention-evaluate" => await RetentionAsync(options, false, shutdown.Token),
                "retention-quarantine" => await RetentionAsync(options, true, shutdown.Token),
                "status" => Status(options),
                "serve" => await ServeAsync(options, shutdown.Token),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (OperationCanceledException)
        {
            WriteJson(new { schemaVersion = "1.0.0", status = "cancelled" });
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0.0",
                status = "failed",
                error = new { code = "orchestrator.command_failed", category = "internal", retryable = false, message = exception.Message }
            }, ContractJson.Options));
            return 1;
        }
    }

    private static int Describe()
    {
        WriteJson(new
        {
            schemaVersion = "1.0.0",
            identityVersion = "2.0.0",
            productId = "knowledge-clipper",
            productName = "零碎知识剪藏",
            componentId = "knowledge-clipper.orchestrator",
            description = "Destination-neutral task orchestrator with optional exporter endpoints.",
            architecture = "destination-neutral",
            destinations = new[] { "remnote", "anki" },
            legacyCompatibilityAliases = new[] { "orchestrator.core", "RemNoteQuickClipper.Orchestrator" },
            version = typeof(ProgramEntry).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            coreContractVersion = "2.10.6",
            ipc = new
            {
                configurationSchemaVersions = new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0", "1.6.0", "1.7.0" },
                protocolVersions = new[] { "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7" },
                moduleActionContracts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["1.2"] = "knowledge-clipper-module-action.v1",
                    ["1.3"] = "knowledge-clipper-module-action.v2",
                    ["1.4"] = "knowledge-clipper-module-action.v3",
                    ["1.5"] = "knowledge-clipper-module-action.v4",
                    ["1.6"] = "knowledge-clipper-module-action.v5",
                    ["1.7"] = "knowledge-clipper-module-action.v6"
                }
            },
            runtime = ".NET 8",
            commands = new[] { "validate-registry", "run", "recover", "cancel", "retention-evaluate", "retention-quarantine", "status", "serve" }
        });
        return 0;
    }

    private static async Task<int> ValidateRegistryAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var loader = new RegistryLoader();
        var registry = loader.Load(Required(options, "registry"));
        await loader.ValidateEntrypointsAsync(registry, Required(options, "release-root"), cancellationToken);
        WriteJson(new { schemaVersion = "1.0.0", status = "valid", registry.RegistryVersion, workers = registry.Workers.Count });
        return 0;
    }

    private static async Task<int> RunPipelineAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var plan = ReadJson<PipelinePlan>(Required(options, "pipeline"));
        var releaseRoot = Required(options, "release-root");
        var loader = new RegistryLoader();
        var registry = loader.Load(Required(options, "registry"));
        await loader.ValidateEntrypointsAsync(registry, releaseRoot, cancellationToken);
        var state = await new OrchestratorEngine().RunAsync(
            Required(options, "job-root"),
            plan,
            registry,
            releaseRoot,
            Required(options, "run-id"),
            cancellationToken);
        WriteJson(new { schemaVersion = "1.0.0", taskId = plan.TaskId, runId = Required(options, "run-id"), status = state });
        return state == "succeeded" ? 0 : 1;
    }

    private static async Task<int> RecoverAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var registry = new RegistryLoader().Load(Required(options, "registry"));
        var recovered = await new OrchestratorEngine().RecoverAsync(
            Required(options, "job-root"), registry, Required(options, "run-id"), cancellationToken);
        WriteJson(new { schemaVersion = "1.0.0", status = "completed", recoveredAttempts = recovered });
        return 0;
    }

    private static async Task<int> CancelAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var jobRoot = Path.GetFullPath(Required(options, "job-root"));
        var reason = options.TryGetValue("reason", out var requestedReason) ? requestedReason : "task_cancel";
        if (reason is not ("user_stop" or "superseded" or "task_cancel" or "timeout"))
            throw new ArgumentException($"Unsupported cancellation reason: {reason}");
        if (options.TryGetValue("signal-path", out var signalPath))
        {
            var attemptId = Required(options, "attempt-id");
            var operationId = Required(options, "operation-id");
            var path = PathPolicy.ResolveJobRelative(jobRoot, signalPath);
            if (!File.Exists(path))
            {
                var requestedAt = DateTimeOffset.UtcNow;
                await AtomicFile.WriteJsonOnceAsync(path, new WorkerCancelSignal("1.0.0", attemptId, operationId, reason, requestedAt, requestedAt.AddMilliseconds(2000)), cancellationToken);
            }
            WriteJson(new { schemaVersion = "1.0.0", status = "cancelling", reason, cancelRef = PathPolicy.ToJobRelative(jobRoot, path) });
            return 0;
        }

        var runId = Required(options, "run-id");
        var taskId = Required(options, "task-id");
        var operationIdForRun = Required(options, "operation-id");
        var cancelPath = Path.Combine(jobRoot, "runs", runId, "cancel-requested.json");
        if (!File.Exists(cancelPath))
            await AtomicFile.WriteJsonOnceAsync(cancelPath, new TaskCancelRequest("1.0.0", taskId, operationIdForRun, reason, DateTimeOffset.UtcNow), cancellationToken);
        WriteJson(new { schemaVersion = "1.0.0", status = "cancelling", cancelRef = PathPolicy.ToJobRelative(jobRoot, cancelPath) });
        return 0;
    }

    private static int Status(IReadOnlyDictionary<string, string> options)
    {
        var store = new EventStore(Required(options, "job-root"), Required(options, "run-id"));
        var events = store.ReadAll();
        WriteJson(new
        {
            schemaVersion = "1.0.0",
            status = TaskStateMachine.Project(events),
            lastSequence = events.Count == 0 ? 0 : events.Max(item => item.Sequence),
            lastEventAt = events.Count == 0 ? (DateTimeOffset?)null : events.Max(item => item.Timestamp)
        });
        return 0;
    }

    private static async Task<int> RetentionAsync(IReadOnlyDictionary<string, string> options, bool quarantine, CancellationToken cancellationToken)
    {
        var jobsRoot = Required(options, "jobs-root");
        var hours = options.TryGetValue("minimum-terminal-age-hours", out var value) && int.TryParse(value, out var parsed) ? parsed : 24;
        if (hours < 0 || hours > 8760) throw new ArgumentException("minimum-terminal-age-hours must be between 0 and 8760.");
        var service = new StateAwareRetentionService();
        var evaluations = service.Evaluate(jobsRoot, DateTimeOffset.UtcNow, TimeSpan.FromHours(hours));
        object? policy = null;
        IReadOnlyList<RetentionQuarantineRecord> records = Array.Empty<RetentionQuarantineRecord>();
        if (quarantine)
        {
            var policyPath = Required(options, "retention-policy-snapshot");
            policy = ValidateRetentionPolicy(policyPath, hours);
            records = await service.QuarantineEligibleAsync(jobsRoot, Required(options, "quarantine-root"), evaluations, cancellationToken);
        }
        WriteJson(new
        {
            schemaVersion = "knowledge-clipper-retention-scheduler-receipt.v1",
            status = "completed",
            mode = quarantine ? "quarantine" : "evaluate",
            automaticProductionInvocation = false,
            jobsRoot = Path.GetFullPath(jobsRoot),
            minimumTerminalAgeHours = hours,
            policy,
            evaluations,
            quarantined = records
        });
        return 0;
    }

    private static object ValidateRetentionPolicy(string policyPath, int minimumTerminalAgeHours)
    {
        var fullPath = Path.GetFullPath(policyPath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(fullPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetString() != "knowledge-clipper-retention-policy.v1" ||
            !root.TryGetProperty("action", out var action) || action.GetString() != "quarantine" ||
            !root.TryGetProperty("automatic", out var automatic) || automatic.GetBoolean() ||
            !root.TryGetProperty("minimumTerminalAgeHours", out var policyHours) || policyHours.GetInt32() != minimumTerminalAgeHours)
            throw new InvalidDataException("Retention policy snapshot is invalid or does not match the requested quarantine operation.");
        return new
        {
            path = fullPath,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))),
            schemaVersion = schema.GetString(),
            action = action.GetString(),
            automatic = automatic.GetBoolean(),
            minimumTerminalAgeHours
        };
    }

    private static async Task<int> ServeAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var configuration = ReadJson<IpcConfiguration>(Required(options, "ipc-config"));
        var maxActiveTasks = options.TryGetValue("max-active-tasks", out var value) && int.TryParse(value, out var parsed) ? parsed : 4;
        var host = new OrchestratorHost(
            Required(options, "jobs-root"),
            Required(options, "registry"),
            Required(options, "release-root"),
            maxActiveTasks,
            options.TryGetValue("adapter-config", out var adapterConfig) ? adapterConfig : null);
        WriteJson(new { schemaVersion = "1.0.0", status = "starting", configuration.Endpoint, maxActiveTasks });
        await host.ServeAsync(configuration, cancellationToken);
        return 0;
    }

    private static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), ContractJson.Options)
        ?? throw new InvalidDataException($"JSON document is empty: {path}");

    private static IReadOnlyDictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                throw new ArgumentException($"Expected --name value, received: {option}");
            if (!output.TryAdd(option[2..], args[index + 1]))
                throw new ArgumentException($"Duplicate option: {option}");
        }
        return output;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option: --{name}");

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, ContractJson.Options));

    private static void WriteUsage() => Console.WriteLine(
        "KnowledgeClipper.Orchestrator <describe|validate-registry|run|recover|cancel|status|serve> [--name value]");
}

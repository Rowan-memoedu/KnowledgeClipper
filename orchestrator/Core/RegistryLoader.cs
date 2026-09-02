using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed class RegistryLoader
{
    public WorkerRegistry Load(string registryPath)
    {
        var json = File.ReadAllText(registryPath);
        using var document = JsonDocument.Parse(json);
        ValidateRegistryDocument(document.RootElement);
        var registry = JsonSerializer.Deserialize<WorkerRegistry>(json, ContractJson.Options)
            ?? throw new InvalidDataException("Worker registry is empty.");
        ValidateRegistryModel(registry);
        return registry;
    }

    public async Task ValidateEntrypointsAsync(WorkerRegistry registry, string releaseRoot, CancellationToken cancellationToken = default)
    {
        foreach (var worker in registry.Workers)
        {
            var executable = PathPolicy.ResolveJobRelative(releaseRoot, worker.Entrypoint.Path);
            if (!File.Exists(executable))
                throw new FileNotFoundException($"Registered worker does not exist: {worker.WorkerId}", executable);

            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            foreach (var argument in worker.Entrypoint.Args)
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add("describe");
            start.ArgumentList.Add("--json");
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start worker: {worker.WorkerId}");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var describeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, describeTimeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                if (describeTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException($"Worker describe timed out after 10 seconds: {worker.WorkerId}");
                throw;
            }
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Worker describe failed: {worker.WorkerId}: {Trim(error)}");
            using var document = JsonDocument.Parse(output);
            ValidateDescribeDocument(document.RootElement, worker.WorkerId);
            var description = JsonSerializer.Deserialize<WorkerDescribe>(output, ContractJson.Options)
                ?? throw new InvalidDataException($"Worker describe is empty: {worker.WorkerId}");
            if (!string.Equals(description.WorkerId, worker.WorkerId, StringComparison.Ordinal))
                throw new InvalidDataException($"Worker describe ID mismatch: registry={worker.WorkerId}, describe={description.WorkerId}");
            if (description.SchemaVersion != "1.0.0" || description.ContractVersion != "1.0.0")
                throw new InvalidDataException($"Worker describe contract mismatch: {worker.WorkerId}");
            EnsureSameSet(worker.Stages, description.Stages, worker.WorkerId, "stages");
            EnsureSameSet(worker.InputKinds, description.InputKinds, worker.WorkerId, "inputKinds");
            EnsureSameSet(worker.OutputKinds, description.OutputKinds, worker.WorkerId, "outputKinds");
            ValidateSemanticVersion(description.WorkerVersion, $"Worker describe workerVersion: {worker.WorkerId}");
            ValidateStringList(description.Stages, worker.WorkerId, "stages", ValidateIdentifier, requireNonEmpty: true);
            ValidateStringList(description.InputKinds, worker.WorkerId, "inputKinds", ValidateArtifactKind);
            ValidateStringList(description.OutputKinds, worker.WorkerId, "outputKinds", ValidateArtifactKind);
            if (worker.SideEffectLevel != description.SideEffectLevel || worker.IdempotencyLevel != description.IdempotencyLevel)
                throw new InvalidDataException($"Worker describe effect/idempotency mismatch: {worker.WorkerId}");
            if (description.Concurrency.MaxParallelAttempts is < 1 or > 128 || description.Concurrency.Model is not ("serial" or "parallel"))
                throw new InvalidDataException($"Worker describe concurrency is invalid: {worker.WorkerId}");
            if (description.Concurrency.Model == "serial" && description.Concurrency.MaxParallelAttempts != 1)
                throw new InvalidDataException($"Serial worker must report maxParallelAttempts=1: {worker.WorkerId}");
            var requiredCancellationReasons = new[] { "user_stop", "superseded", "task_cancel", "timeout" };
            if (description.Cancellation.Protocol != "attempt-signal-file-v1" ||
                !requiredCancellationReasons.ToHashSet(StringComparer.Ordinal).SetEquals(description.Cancellation.SupportedReasons))
                throw new InvalidDataException($"Worker describe cancellation contract mismatch: {worker.WorkerId}");
        }
    }

    private static void EnsureSameSet(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string workerId, string field)
    {
        if (!expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual))
            throw new InvalidDataException($"Worker describe {field} mismatch: {workerId}");
    }

    private static void ValidateRegistryDocument(JsonElement root)
    {
        EnsureExactProperties(root, "worker registry", "schemaVersion", "registryVersion", "profiles", "workers");
        RequireString(root, "schemaVersion", "worker registry");
        RequireString(root, "registryVersion", "worker registry");
        RequireArray(root, "profiles", "worker registry");
        var workers = RequireArray(root, "workers", "worker registry");
        foreach (var worker in workers.EnumerateArray())
        {
            EnsureExactProperties(worker, "worker registry entry", "workerId", "contractVersionRange", "entrypoint", "stages", "inputKinds", "outputKinds", "requiredForProfiles", "sideEffectLevel", "idempotencyLevel", "enabledByDefault");
            RequireString(worker, "workerId", "worker registry entry");
            RequireString(worker, "contractVersionRange", "worker registry entry");
            var entrypoint = worker.GetProperty("entrypoint");
            EnsureExactProperties(entrypoint, "worker registry entrypoint", "path", "args");
            RequireString(entrypoint, "path", "worker registry entrypoint");
            RequireArray(entrypoint, "args", "worker registry entrypoint");
            RequireArray(worker, "stages", "worker registry entry");
            RequireArray(worker, "inputKinds", "worker registry entry");
            RequireArray(worker, "outputKinds", "worker registry entry");
            RequireArray(worker, "requiredForProfiles", "worker registry entry");
            RequireString(worker, "sideEffectLevel", "worker registry entry");
            RequireString(worker, "idempotencyLevel", "worker registry entry");
            if (worker.GetProperty("enabledByDefault").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("worker registry entry enabledByDefault must be boolean.");
        }
    }

    private static void ValidateRegistryModel(WorkerRegistry registry)
    {
        if (registry.SchemaVersion != "1.0.0")
            throw new InvalidDataException($"Unsupported worker registry schema: {registry.SchemaVersion}");
        ValidateSemanticVersion(registry.RegistryVersion, "Worker registry registryVersion");
        var requiredProfiles = new[] { "parity-remnote", "cards-remnote", "cards-anki" };
        if (registry.Profiles is null || !requiredProfiles.ToHashSet(StringComparer.Ordinal).SetEquals(registry.Profiles) || registry.Profiles.Count != requiredProfiles.Length)
            throw new InvalidDataException("Worker registry profiles must contain each v1 profile exactly once.");
        if (registry.Workers is null)
            throw new InvalidDataException("Worker registry workers array is missing.");
        var duplicates = registry.Workers.GroupBy(item => item.WorkerId, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidDataException("Duplicate worker IDs: " + string.Join(", ", duplicates));
        foreach (var worker in registry.Workers)
        {
            ValidateIdentifier(worker.WorkerId, $"Worker registry workerId: {worker.WorkerId}");
            if (worker.ContractVersionRange != ">=1.0.0 <2.0.0")
                throw new InvalidDataException($"Worker registry contract range is invalid: {worker.WorkerId}");
            if (worker.Entrypoint is null || string.IsNullOrWhiteSpace(worker.Entrypoint.Path) || worker.Entrypoint.Path.Length > 1024)
                throw new InvalidDataException($"Worker registry entrypoint is invalid: {worker.WorkerId}");
            PathPolicy.ResolveJobRelative(Path.GetTempPath(), worker.Entrypoint.Path);
            if (worker.Entrypoint.Args is null || worker.Entrypoint.Args.Any(argument => argument is null || argument.Length > 512))
                throw new InvalidDataException($"Worker registry entrypoint arguments are invalid: {worker.WorkerId}");
            ValidateStringList(worker.Stages, worker.WorkerId, "stages", ValidateIdentifier, requireNonEmpty: true);
            ValidateStringList(worker.InputKinds, worker.WorkerId, "inputKinds", ValidateArtifactKind);
            ValidateStringList(worker.OutputKinds, worker.WorkerId, "outputKinds", ValidateArtifactKind);
            ValidateStringList(worker.RequiredForProfiles, worker.WorkerId, "requiredForProfiles", (value, context) =>
            {
                if (!requiredProfiles.Contains(value, StringComparer.Ordinal))
                    throw new InvalidDataException($"{context} is not a v1 profile: {value}");
            });
            if (worker.SideEffectLevel is not ("none" or "local" or "external"))
                throw new InvalidDataException($"Worker registry sideEffectLevel is invalid: {worker.WorkerId}");
            if (worker.IdempotencyLevel is not ("pure" or "idempotent" or "reconcilable" or "non_idempotent"))
                throw new InvalidDataException($"Worker registry idempotencyLevel is invalid: {worker.WorkerId}");
        }
    }

    private static void ValidateDescribeDocument(JsonElement root, string workerId)
    {
        EnsureExactProperties(root, $"Worker describe: {workerId}", "schemaVersion", "workerId", "workerVersion", "contractVersion", "stages", "inputKinds", "outputKinds", "concurrency", "sideEffectLevel", "idempotencyLevel", "cancellation");
        RequireString(root, "schemaVersion", $"Worker describe: {workerId}");
        RequireString(root, "workerId", $"Worker describe: {workerId}");
        RequireString(root, "workerVersion", $"Worker describe: {workerId}");
        RequireString(root, "contractVersion", $"Worker describe: {workerId}");
        RequireArray(root, "stages", $"Worker describe: {workerId}");
        RequireArray(root, "inputKinds", $"Worker describe: {workerId}");
        RequireArray(root, "outputKinds", $"Worker describe: {workerId}");
        RequireString(root, "sideEffectLevel", $"Worker describe: {workerId}");
        RequireString(root, "idempotencyLevel", $"Worker describe: {workerId}");
        var concurrency = root.GetProperty("concurrency");
        EnsureExactProperties(concurrency, $"Worker describe concurrency: {workerId}", "model", "maxParallelAttempts");
        RequireString(concurrency, "model", $"Worker describe concurrency: {workerId}");
        if (concurrency.GetProperty("maxParallelAttempts").ValueKind != JsonValueKind.Number || !concurrency.GetProperty("maxParallelAttempts").TryGetInt32(out _))
            throw new InvalidDataException($"Worker describe maxParallelAttempts must be an integer: {workerId}");
        var cancellation = root.GetProperty("cancellation");
        EnsureExactProperties(cancellation, $"Worker describe cancellation: {workerId}", "protocol", "supportedReasons");
        RequireString(cancellation, "protocol", $"Worker describe cancellation: {workerId}");
        RequireArray(cancellation, "supportedReasons", $"Worker describe cancellation: {workerId}");
    }

    private static void EnsureExactProperties(JsonElement element, string context, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context} must be an object.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length || !expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual))
            throw new InvalidDataException($"{context} properties do not match the frozen v1 schema.");
    }

    private static string RequireString(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{context} {property} must be a string.");
        return value.GetString()!;
    }

    private static JsonElement RequireArray(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{context} {property} must be an array.");
        return value;
    }

    private static void ValidateStringList(IReadOnlyList<string>? values, string workerId, string field, Action<string, string> validate, bool requireNonEmpty = false)
    {
        if (values is null || (requireNonEmpty && values.Count == 0) || values.Any(value => value is null) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new InvalidDataException($"Worker {field} is invalid or contains duplicates: {workerId}");
        foreach (var value in values)
            validate(value, $"Worker {field}: {workerId}");
    }

    private static void ValidateIdentifier(string value, string context)
    {
        if (value is null || value.Length is < 1 or > 128 || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException($"{context} is not a valid identifier.");
    }

    private static void ValidateArtifactKind(string value, string context)
    {
        if (value is null || value.Length is < 1 or > 128 || !Regex.IsMatch(value, "^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException($"{context} is not a valid artifact kind.");
    }

    private static void ValidateSemanticVersion(string value, string context)
    {
        if (value is null || !Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException($"{context} is not a semantic version.");
    }

    private static string Trim(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 500 ? text[..500] : text;
    }
}

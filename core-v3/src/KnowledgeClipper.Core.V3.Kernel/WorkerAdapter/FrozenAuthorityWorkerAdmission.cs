using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed record AdmittedAuthorityWorker(
    string WorkerId,
    string WorkerVersion,
    string ContractVersion,
    string Entrypoint,
    string EntrypointSha256,
    IReadOnlyList<string> Stages,
    string SideEffectLevel,
    string IdempotencyLevel);

internal static class FrozenAuthorityWorkerAdmission
{
    public static async Task<IReadOnlyDictionary<string, AdmittedAuthorityWorker>> AdmitAsync(
        AdmittedCapabilityCatalog catalog,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var admitted = new Dictionary<string, AdmittedAuthorityWorker>(StringComparer.Ordinal);
        foreach (var capability in catalog.Document.Operations.Where(item => item.Available && item.WorkerBinding.Mode == "worker"))
        {
            var binding = capability.WorkerBinding;
            var authority = binding.Authority
                ?? throw new InvalidDataException($"Available worker capability '{capability.OperationId}' lacks a frozen authority binding.");
            var workerId = binding.WorkerId
                ?? throw new InvalidDataException($"Available worker capability '{capability.OperationId}' lacks workerId.");
            if (admitted.TryGetValue(workerId, out var prior))
            {
                Require(prior.WorkerVersion == authority.ExactWorkerVersion && prior.ContractVersion == authority.ContractVersion,
                    $"Worker '{workerId}' has inconsistent exact admissions.");
                continue;
            }

            var releaseRoot = ResolveRepositoryPath(repositoryRoot, authority.ReleaseRoot);
            var registryPath = ResolveRepositoryPath(repositoryRoot, authority.RegistryPath);
            var registryBytes = await File.ReadAllBytesAsync(registryPath, cancellationToken).ConfigureAwait(false);
            RequireHash(registryBytes, authority.RegistrySha256, $"registry for '{workerId}'");
            using var registryDocument = StrictJson.Parse(registryBytes, registryPath);
            var registry = registryDocument.RootElement;
            var expectedRegistryVersion = catalog.Document.AuthorityAnchor.AuthorityVersion;
            Require(GetRequiredString(registry, "registryVersion") == expectedRegistryVersion,
                $"Worker authority registryVersion must match capability authorityAnchor '{expectedRegistryVersion}'.");
            var matches = registry.GetProperty("workers").EnumerateArray()
                .Where(item => GetRequiredString(item, "workerId") == workerId)
                .Select(item => item.Clone())
                .ToArray();
            Require(matches.Length == 1, $"Authority registry must contain exactly one '{workerId}' entry.");
            var registryWorker = matches[0];
            var registryEntrypoint = GetRequiredString(registryWorker.GetProperty("entrypoint"), "path");
            Require(registryEntrypoint == authority.EntrypointPath, $"Authority entrypoint mismatch for '{workerId}'.");
            var entrypoint = ResolveWithinRoot(releaseRoot, registryEntrypoint);
            var entrypointBytes = await File.ReadAllBytesAsync(entrypoint, cancellationToken).ConfigureAwait(false);
            RequireHash(entrypointBytes, authority.EntrypointSha256, $"entrypoint for '{workerId}'");

            var describe = await DescribeAsync(entrypoint, registryWorker.GetProperty("entrypoint"), cancellationToken).ConfigureAwait(false);
            Require(GetRequiredString(describe, "schemaVersion") == "1.0.0", $"Worker '{workerId}' describe schema is unsupported.");
            Require(GetRequiredString(describe, "workerId") == workerId, $"Worker '{workerId}' describe identity mismatch.");
            Require(GetRequiredString(describe, "workerVersion") == authority.ExactWorkerVersion, $"Worker '{workerId}' exact version mismatch.");
            Require(GetRequiredString(describe, "contractVersion") == authority.ContractVersion, $"Worker '{workerId}' contract version mismatch.");
            Require(binding.VersionRange == "=" + authority.ExactWorkerVersion, $"Capability '{capability.OperationId}' is not exact-version admitted.");
            Require(GetRequiredString(describe, "sideEffectLevel") == GetRequiredString(registryWorker, "sideEffectLevel"), $"Worker '{workerId}' side-effect declaration mismatch.");
            Require(GetRequiredString(describe, "idempotencyLevel") == GetRequiredString(registryWorker, "idempotencyLevel"), $"Worker '{workerId}' idempotency declaration mismatch.");

            var stages = describe.GetProperty("stages").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var requiredStage = workerId switch
            {
                "content.deepseek" => "content.understand-and-card",
                "speech.edge" => "speech.synthesize",
                "export.anki" when capability.OperationId == "anki.probe" => "anki-probe",
                "export.anki" => "anki-export",
                "export.remnote" => "export-remnote",
                _ => throw new InvalidDataException($"Worker '{workerId}' is outside the Phase 2 slice.")
            };
            Require(stages.Contains(requiredStage, StringComparer.Ordinal), $"Worker '{workerId}' does not describe required stage '{requiredStage}'.");

            admitted.Add(workerId, new AdmittedAuthorityWorker(
                workerId,
                authority.ExactWorkerVersion,
                authority.ContractVersion,
                entrypoint,
                authority.EntrypointSha256,
                stages,
                GetRequiredString(describe, "sideEffectLevel"),
                GetRequiredString(describe, "idempotencyLevel")));
        }

        if (catalog.Document.Operations.Any(item => item.Available && (item.OperationId.StartsWith("question.", StringComparison.Ordinal) || item.OperationId == "clip.read-aloud-cards")))
        {
            Require(admitted.ContainsKey("content.deepseek"), "Card workflows require admitted content.deepseek.");
            Require(admitted.ContainsKey("export.anki"), "The existing card workflow catalog requires admitted export.anki.");
        }
        if (catalog.Document.Operations.Any(item => item.Available && item.WorkerBinding.WorkerId == "speech.edge"))
        {
            Require(admitted.ContainsKey("speech.edge"), "Read-to-Anki requires admitted speech.edge.");
        }
        return admitted;
    }

    private static async Task<JsonElement> DescribeAsync(string entrypoint, JsonElement entrypointDefinition, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = entrypoint,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        if (entrypointDefinition.TryGetProperty("args", out var args))
        {
            foreach (var argument in args.EnumerateArray())
            {
                process.StartInfo.ArgumentList.Add(argument.GetString()!);
            }
        }
        process.StartInfo.ArgumentList.Add("describe");
        process.StartInfo.ArgumentList.Add("--json");
        Require(process.Start(), $"Could not start frozen worker describe '{entrypoint}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException($"Frozen worker describe timed out: {entrypoint}");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        Require(process.ExitCode == 0, $"Frozen worker describe failed ({process.ExitCode}): {error}");
        using var document = StrictJson.Parse(Encoding.UTF8.GetBytes(output), entrypoint + " describe");
        return document.RootElement.Clone();
    }

    private static string ResolveRepositoryPath(string repositoryRoot, string relativePath)
    {
        Require(!Path.IsPathRooted(relativePath) && !relativePath.Contains('\\'), "Authority repository paths must be forward-slash relative paths.");
        return ResolveWithinRoot(Path.GetFullPath(repositoryRoot), relativePath);
    }

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        Require(!Path.IsPathRooted(relativePath), "Authority entrypoint path must be relative.");
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var boundary = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Require(full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase), "Authority path escapes its frozen root.");
        return full;
    }

    private static void RequireHash(ReadOnlySpan<byte> bytes, string expected, string label)
    {
        Require(expected.Length == 64 && expected.All(Uri.IsHexDigit), $"Invalid expected SHA-256 for {label}.");
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        Require(actual.Equals(expected, StringComparison.OrdinalIgnoreCase), $"SHA-256 mismatch for {label}.");
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        Require(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
            $"Required string '{name}' is missing.");
        return value.GetString()!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

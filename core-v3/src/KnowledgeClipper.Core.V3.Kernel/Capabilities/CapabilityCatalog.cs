using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.Capabilities;

internal sealed class AdmittedCapabilityCatalog
{
    private readonly IReadOnlyDictionary<string, CapabilityDescriptor> _operations;

    private AdmittedCapabilityCatalog(CapabilityCatalogDocument document, string sha256)
    {
        Document = document;
        Sha256 = sha256;
        _operations = document.Operations.ToDictionary(item => item.OperationId, StringComparer.Ordinal);
    }

    public CapabilityCatalogDocument Document { get; }

    public string Sha256 { get; }

    internal static AdmittedCapabilityCatalog CreateForTesting(CapabilityCatalogDocument document)
    {
        ValidateCatalog(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, KernelJson.Options);
        return new AdmittedCapabilityCatalog(document, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public CapabilityDescriptor GetRequired(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var descriptor))
        {
            throw new KernelContractException("capability_not_found", $"Operation '{operationId}' is not present in the admitted capability catalog.");
        }

        return descriptor;
    }

    public static async Task<AdmittedCapabilityCatalog> LoadAsync(
        string repositoryRoot,
        string bindingPath,
        CancellationToken cancellationToken)
    {
        var bindingBytes = await File.ReadAllBytesAsync(bindingPath, cancellationToken).ConfigureAwait(false);
        using var bindingJson = StrictJson.Parse(bindingBytes, bindingPath);
        var binding = bindingJson.RootElement.Deserialize<CapabilityCatalogBinding>(KernelJson.Options)
            ?? throw new InvalidDataException("Capability binding deserialized to null.");
        Require(binding.Schema == KernelSchemas.CapabilityBinding, "Capability binding schema is not current.");

        var catalogPath = ResolveRepositoryPath(repositoryRoot, binding.CatalogPath);
        var catalogBytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        var catalogSha256 = Convert.ToHexString(SHA256.HashData(catalogBytes));
        Require(string.Equals(catalogSha256, binding.CatalogSha256, StringComparison.OrdinalIgnoreCase), "Capability catalog hash does not match its authority binding.");

        using var catalogJson = StrictJson.Parse(catalogBytes, catalogPath);
        var document = catalogJson.RootElement.Deserialize<CapabilityCatalogDocument>(KernelJson.Options)
            ?? throw new InvalidDataException("Capability catalog deserialized to null.");
        ValidateCatalog(document);
        Require(document.AuthorityAnchor == binding.AuthorityAnchor, "Capability catalog and binding authority anchors differ.");

        var checkpointPath = ResolveRepositoryPath(repositoryRoot, binding.AuthorityAnchor.CheckpointPath);
        var checkpointBytes = await File.ReadAllBytesAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        var checkpointSha256 = Convert.ToHexString(SHA256.HashData(checkpointBytes));
        Require(string.Equals(checkpointSha256, binding.AuthorityAnchor.CheckpointSha256, StringComparison.OrdinalIgnoreCase), "Authority checkpoint hash does not match the capability binding.");

        return new AdmittedCapabilityCatalog(document, catalogSha256);
    }

    private static void ValidateCatalog(CapabilityCatalogDocument document)
    {
        Require(document.Schema == KernelSchemas.CapabilityCatalog, "Capability catalog schema is not current.");
        Require(!string.IsNullOrWhiteSpace(document.CatalogId), "Capability catalog id is required.");
        Require(document.Operations.Count > 0, "Capability catalog must contain at least one operation.");

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in document.Operations)
        {
            Require(operationIds.Add(operation.OperationId), $"Duplicate capability operation '{operation.OperationId}'.");
            Require(operation.TimeoutMilliseconds > 0, $"Capability '{operation.OperationId}' has an invalid timeout.");
            Require(operation.IdempotencyPolicy is "required" or "optional", $"Capability '{operation.OperationId}' has an invalid idempotency policy.");
            Require(operation.EffectClass is "none" or "read-only-external" or "external-write", $"Capability '{operation.OperationId}' has an invalid effect class.");
            Require(operation.AuthorizationPolicy is "none" or "explicit", $"Capability '{operation.OperationId}' has an invalid authorization policy.");
            Require(operation.RetryPolicy is "safe" or "never" or "conditional", $"Capability '{operation.OperationId}' has an invalid retry policy.");
            if (!operation.Available)
            {
                Require(!string.IsNullOrWhiteSpace(operation.AvailabilityReason), $"Unavailable capability '{operation.OperationId}' requires a reason.");
            }
            if (operation.Available && operation.WorkerBinding.Mode == "worker")
            {
                Require(operation.WorkerBinding.WorkerId is not null, $"Available worker capability '{operation.OperationId}' requires workerId.");
                Require(operation.WorkerBinding.Authority is not null, $"Available worker capability '{operation.OperationId}' requires frozen authority metadata.");
                Require(operation.WorkerBinding.VersionRange == "=" + operation.WorkerBinding.Authority!.ExactWorkerVersion,
                    $"Available worker capability '{operation.OperationId}' must use an exact admitted worker version.");
            }
        }
    }

    private static string ResolveRepositoryPath(string repositoryRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Repository path must be a forward-slash relative path: {relativePath}");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var boundary = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Repository path escapes the repository root: {relativePath}");
        }

        return fullPath;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

internal sealed class KernelContractException : Exception
{
    public KernelContractException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

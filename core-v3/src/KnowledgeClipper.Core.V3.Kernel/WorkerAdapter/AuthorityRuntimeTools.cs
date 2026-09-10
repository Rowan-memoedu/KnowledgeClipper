using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed record AuthorityRuntimeTools(string EdgeTtsPath, string FfplayPath);

internal static class AuthorityRuntimeToolAdmission
{
    private const string DescriptorRelativePath = "authority/runtime-tools.v1.json";

    public static async Task<AuthorityRuntimeTools> AdmitAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var descriptorPath = ResolveWithin(root, DescriptorRelativePath);
        var bytes = await File.ReadAllBytesAsync(descriptorPath, cancellationToken).ConfigureAwait(false);
        using var document = StrictJson.Parse(bytes, descriptorPath);
        var descriptor = document.RootElement;
        Require(descriptor.GetProperty("schema").GetString() == "kc.authority-runtime-tools/1",
            "Authority runtime tool descriptor schema is unsupported.");
        var tools = descriptor.GetProperty("tools");
        return new AuthorityRuntimeTools(
            await AdmitToolAsync(root, tools.GetProperty("edgeTts"), "edgeTts", cancellationToken).ConfigureAwait(false),
            await AdmitToolAsync(root, tools.GetProperty("ffplay"), "ffplay", cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> AdmitToolAsync(string root, JsonElement definition, string name, CancellationToken cancellationToken)
    {
        var relativePath = definition.GetProperty("path").GetString()
            ?? throw new InvalidDataException($"Runtime tool '{name}' path is missing.");
        var expectedHash = definition.GetProperty("sha256").GetString()
            ?? throw new InvalidDataException($"Runtime tool '{name}' hash is missing.");
        var expectedBytes = definition.GetProperty("bytes").GetInt64();
        var path = ResolveWithin(root, relativePath);
        var info = new FileInfo(path);
        Require(info.Exists, $"Authority runtime tool '{name}' is missing: {relativePath}");
        Require(info.Length == expectedBytes, $"Authority runtime tool '{name}' byte length drifted.");
        var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
        Require(string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase), $"Authority runtime tool '{name}' hash drifted.");
        return path;
    }

    private static string ResolveWithin(string root, string relativePath)
    {
        Require(!string.IsNullOrWhiteSpace(relativePath) && !Path.IsPathFullyQualified(relativePath), "Runtime tool path must be candidate-relative.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Require(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Runtime tool path escapes the sealed candidate root.");
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public static class PathPolicy
{
    public static string ResolveJobRelative(string jobRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("Relative path is empty.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Absolute paths are forbidden.");

        var normalizedSegments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedSegments.Any(segment => segment == ".."))
            throw new InvalidDataException("Path traversal is forbidden.");

        var root = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(jobRoot, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path resolves outside the job root.");
        return resolved;
    }

    public static string ToJobRelative(string jobRoot, string absolutePath)
    {
        var root = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(absolutePath);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path is outside the job root.");
        return Path.GetRelativePath(jobRoot, resolved).Replace('\\', '/');
    }
}

public static class AtomicFile
{
    public static async Task WriteJsonOnceAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
            throw new IOException($"Append-only target already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Target has no directory."));
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, ContractJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static async Task AppendLineAsync(string path, string line, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Target has no directory."));
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 65536, true);
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

public static class Integrity
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    public static async Task VerifyFileAsync(string jobRoot, string relativePath, long expectedBytes, string expectedSha256, CancellationToken cancellationToken = default)
    {
        var path = PathPolicy.ResolveJobRelative(jobRoot, relativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Artifact file is missing.", path);
        if (info.Length != expectedBytes)
            throw new InvalidDataException($"Artifact byte count mismatch: {relativePath}");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact SHA-256 mismatch: {relativePath}");
    }
}

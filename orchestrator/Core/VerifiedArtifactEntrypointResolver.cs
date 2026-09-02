using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

internal static class VerifiedArtifactEntrypointResolver
{
    public static CoreArtifactFile Resolve(string jobRoot, CoreArtifactManifest manifest, string label)
    {
        if (manifest.Metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} metadata must be an object.");

        var declarations = manifest.Metadata.EnumerateObject()
            .Where(property => property.NameEquals("entrypoint"))
            .ToArray();
        if (declarations.Length != 1 || declarations[0].Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{label} must declare exactly one string metadata.entrypoint.");

        var entrypoint = declarations[0].Value.GetString();
        EnsureCanonicalJobRelativePath(jobRoot, entrypoint, $"{label} metadata.entrypoint");

        var matches = manifest.Files.Where(file => file.Path == entrypoint).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{label} metadata.entrypoint must identify exactly one declared file.");
        return matches[0];
    }

    public static void EnsureCanonicalJobRelativePath(string jobRoot, string? relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath != relativePath.Trim() || relativePath.Contains('\\'))
            throw new InvalidDataException($"{label} is not a canonical job-relative path.");
        if (relativePath.StartsWith("/", StringComparison.Ordinal) || relativePath.EndsWith("/", StringComparison.Ordinal) ||
            relativePath.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"{label} is not a canonical job-relative path.");

        string resolved;
        try
        {
            resolved = PathPolicy.ResolveJobRelative(jobRoot, relativePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"{label} is outside the artifact boundary.", exception);
        }

        if (PathPolicy.ToJobRelative(jobRoot, resolved) != relativePath)
            throw new InvalidDataException($"{label} is not a canonical job-relative path.");
    }
}

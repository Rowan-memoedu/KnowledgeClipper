using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal static class DomainArtifactFactory
{
    public static async Task<ArtifactReference> PublishJsonAsync(
        FileArtifactStore store,
        CommandEnvelope command,
        string kind,
        string contract,
        string schema,
        object value,
        IReadOnlyList<ArtifactReference> inputs,
        CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(value, KernelJson.Options);
        return await PublishAsync(store, command, kind, contract, schema, "data.json", "application/json", content, inputs, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ArtifactReference> PublishTextAsync(
        FileArtifactStore store,
        CommandEnvelope command,
        string kind,
        string contract,
        string schema,
        string text,
        IReadOnlyList<ArtifactReference> inputs,
        CancellationToken cancellationToken)
    {
        return await PublishAsync(store, command, kind, contract, schema, "content.txt", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), inputs, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadTextAsync(FileArtifactStore store, ArtifactReference reference, CancellationToken cancellationToken)
    {
        var artifact = await store.OpenVerifiedAsync(reference, reference.Kind, reference.Contract, cancellationToken).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(artifact.Primary.Content);
    }

    private static async Task<ArtifactReference> PublishAsync(
        FileArtifactStore store,
        CommandEnvelope command,
        string kind,
        string contract,
        string schema,
        string fileName,
        string mediaType,
        byte[] content,
        IReadOnlyList<ArtifactReference> inputs,
        CancellationToken cancellationToken)
    {
        var artifactId = ArtifactIdentityPolicy.Create(command, kind, content);
        var representation = new ArtifactRepresentation(
            "primary",
            fileName,
            mediaType,
            schema,
            new ArtifactDigest("sha256", Convert.ToHexString(SHA256.HashData(content))),
            content.Length,
            new[] { "primary" });
        var envelope = new ArtifactEnvelope(
            KernelSchemas.Artifact,
            artifactId,
            kind,
            contract,
            store.StoreId,
            new[] { representation },
            "primary",
            new ArtifactProvenance("core-v3.phase2", inputs),
            new ArtifactPolicies("personal", "task-bound"));
        return await store.PublishAsync(
            envelope,
            new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["primary"] = content },
            cancellationToken).ConfigureAwait(false);
    }
}

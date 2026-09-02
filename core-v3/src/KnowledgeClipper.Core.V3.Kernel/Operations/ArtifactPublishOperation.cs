using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal sealed class ArtifactPublishOperation : IOperationHandler
{
    public const string Id = "artifact.publish";

    public string OperationId => Id;

    public async Task<OperationExecutionResult> ExecuteAsync(
        CommandEnvelope command,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        if (command.Body.Schema != "kc.artifact-publish-command/1" || command.Body.Data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Artifact publish command body is invalid.");
        }

        var purpose = GetRequiredString(command.Body.Data, "purpose");
        var contentBase64 = GetRequiredString(command.Body.Data, "contentBase64");
        byte[] content;
        try
        {
            content = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Artifact publish contentBase64 is invalid.", ex);
        }

        var (kind, contract, contractSchema) = purpose switch
        {
            "question" => ("question-text", "3.0.0", "kc.question-text/1"),
            "question-context" => ("question-context", "3.0.0", "kc.question-context/1"),
            "question-follow-up" => ("question-follow-up", "3.0.0", "kc.question-follow-up/1"),
            "question-answer" => ("question-answer", "3.0.0", "kc.question-answer/1"),
            "anki-configuration" => ("anki-export-config", "1.0.0", "kc.anki-export-config/1"),
            _ => throw new InvalidDataException($"Unsupported artifact publish purpose '{purpose}'.")
        };
        var dataClass = command.Body.Data.TryGetProperty("dataClass", out var dataClassElement) && dataClassElement.ValueKind == JsonValueKind.String
            ? dataClassElement.GetString()!
            : "personal";
        var artifactId = ArtifactIdentityPolicy.Create(command, kind, content);
        var representation = new ArtifactRepresentation(
            "primary",
            "content.txt",
            "text/plain; charset=utf-8",
            contractSchema,
            new ArtifactDigest("sha256", Convert.ToHexString(SHA256.HashData(content))),
            content.Length,
            new[] { "primary" });
        var envelope = new ArtifactEnvelope(
            KernelSchemas.Artifact,
            artifactId,
            kind,
            contract,
            context.ArtifactStore.StoreId,
            new[] { representation },
            "primary",
            new ArtifactProvenance("core-v3.artifact.publish", command.Inputs),
            new ArtifactPolicies(dataClass, "task-bound"));
        var reference = await context.ArtifactStore.PublishAsync(
            envelope,
            new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["primary"] = content },
            cancellationToken).ConfigureAwait(false);
        return new OperationExecutionResult(
            StrictJson.CloneElement(new { artifactId, kind, contract }),
            new[] { reference },
            ExternalWriteCount: 0,
            ExternalStateKnown: true);
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Artifact publish property '{name}' is required.");
        }

        return property.GetString()!;
    }
}

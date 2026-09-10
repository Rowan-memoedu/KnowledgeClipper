using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

/// <summary>
/// The single destination-neutral card preview identity used by every Core
/// card-producing workflow. Destination handlers consume it only after a
/// token-bound explicit confirmation.
/// </summary>
internal static class FlashcardDraftProjection
{
    public static JsonElement Create(
        string sourceOperation,
        string operationId,
        ArtifactReference cardArtifact,
        IReadOnlyList<(string CardId, string Front, string Back)> cards)
        => Create(sourceOperation, operationId, cardArtifact, cards.Select(card => (card.CardId, card.Front, card.Back, "basic_qa")).ToArray());

    public static JsonElement Create(
        string sourceOperation,
        string operationId,
        ArtifactReference cardArtifact,
        IReadOnlyList<(string CardId, string Front, string Back, string Template)> cards)
    {
        if (cards.Count == 0) throw new InvalidDataException("Flashcard draft contains no cards.");
        var revision = cardArtifact.ManifestDigest.Value;
        var projected = cards.Select((card, ordinal) =>
        {
            var cardSha256 = Hash("card\n" + revision + "\n" + ordinal + "\n" + card.CardId + "\n" + card.Template + "\n" + card.Front + "\n" + card.Back);
            return new
            {
                cardId = string.IsNullOrWhiteSpace(card.CardId) ? cardSha256[..24] : card.CardId,
                revision,
                cardSha256,
                front = card.Front,
                back = card.Back,
                template = card.Template,
                metadata = new { ordinal, sourceArtifactId = cardArtifact.ArtifactId }
            };
        }).ToArray();
        var digestSource = JsonSerializer.Serialize(projected, KernelJson.Options);
        var digest = Hash("draft\n" + sourceOperation + "\n" + operationId + "\n" + revision + "\n" + digestSource);
        var token = Hash("confirm\n" + sourceOperation + "\n" + operationId + "\n" + revision + "\n" + digest);
        return StrictJson.CloneElement(new { sourceOperation, token, revision, digest, cards = projected });
    }

    public static void RequireBinding(JsonElement body, JsonElement expectedDraft)
    {
        if (!body.TryGetProperty("draft", out var provided) || provided.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Flashcard action requires a draft token, revision, and digest.");
        }
        foreach (var name in new[] { "token", "revision", "digest" })
        {
            if (!provided.TryGetProperty(name, out var actual) || actual.ValueKind != JsonValueKind.String ||
                !expectedDraft.TryGetProperty(name, out var expected) || expected.ValueKind != JsonValueKind.String ||
                !string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Flashcard draft token, revision, or digest is stale.");
            }
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

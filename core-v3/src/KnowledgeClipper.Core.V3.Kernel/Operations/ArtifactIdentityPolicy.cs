using System.Security.Cryptography;
using System.Text;
using KnowledgeClipper.Core.V3.Contracts;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal static class ArtifactIdentityPolicy
{
    public static string Create(CommandEnvelope command, string kind, ReadOnlySpan<byte> canonicalContent)
    {
        var contentDigest = Convert.ToHexString(SHA256.HashData(canonicalContent));
        var identity = Encoding.UTF8.GetBytes(command.IdempotencyKey + "\n" + command.CommandId + "\n" + kind + "\n" + contentDigest);
        return "artifact-" + Convert.ToHexString(SHA256.HashData(identity))[..24].ToLowerInvariant();
    }
}

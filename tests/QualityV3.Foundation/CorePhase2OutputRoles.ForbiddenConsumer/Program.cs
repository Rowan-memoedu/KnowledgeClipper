using KnowledgeClipper.Core.V3.Client;

namespace QualityV3.CorePhase2OutputRoles.ForbiddenConsumer;

internal static class Program
{
    private static void Main()
    {
    }

    private static object ReadForbiddenSurface(TypedArtifactHandle artifact)
    {
        return new { artifact.Reference, artifact.Kind, artifact.Path };
    }
}

using KnowledgeClipper.Core.V3.Client;

namespace QualityV3.CorePhase2OutputRoles.Consumer;

internal static class Program
{
    private static int Main()
    {
        QuestionProjectionSnapshot projection = null!;
        Consume(projection.AnswerArtifact, "answer");
        Consume(projection.CardBundleArtifact, "card-bundle");
        Consume(projection.DestinationEvidenceArtifact, "destination-evidence");
        Console.WriteLine("QUALITY_PHASE2_OUTPUT_ROLE_CONSUMER COMPILE_PASS");
        return 0;
    }

    private static void Consume(TypedArtifactHandle? artifact, string role)
    {
        _ = artifact;
        _ = role;
    }
}

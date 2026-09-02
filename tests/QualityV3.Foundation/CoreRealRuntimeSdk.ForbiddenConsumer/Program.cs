using KnowledgeClipper.Core.V3.Client;

internal static class Program
{
    private static void Main()
    {
    }

    private static object ReadForbiddenHandleSurface(TypedArtifactHandle artifact)
        => new { artifact.Reference, artifact.Kind, artifact.Path };

    private static object ReadForbiddenSubmissionSurface(OperationSubmission operation, QuestionOperationSubmission question)
        => new { operation.Artifacts, question.Artifacts };
}

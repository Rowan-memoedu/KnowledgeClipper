using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal sealed class ContentSettingsDescribeOperation : IOperationHandler
{
    public const string Id = "content.settings.describe";

    public string OperationId => Id;

    public Task<OperationExecutionResult> ExecuteAsync(
        CommandEnvelope command,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(command.Body.Schema, "kc.content-settings-describe-command/1", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected command body schema '{command.Body.Schema}'.");
        }

        var result = StrictJson.CloneElement(new
        {
            schema = "kc.content-settings-description/1",
            settingsContract = "knowledge-clipper-workflow-settings.v3",
            supportedActions = new[] { "describe", "copy", "validate", "diff" },
            executionMode = "offline",
            externalWriteCount = 0
        });

        return Task.FromResult(new OperationExecutionResult(
            result,
            Array.Empty<ArtifactReference>(),
            ExternalWriteCount: 0,
            ExternalStateKnown: true));
    }
}

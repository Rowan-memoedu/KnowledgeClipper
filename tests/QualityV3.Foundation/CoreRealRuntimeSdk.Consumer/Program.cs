using KnowledgeClipper.Core.V3.Client;

var readiness = new AnkiReadinessSummary(
    ConnectionReady: true,
    SelectedRoute: "canary",
    SelectedRouteReady: true,
    ReadyForRequestedAction: true,
    OverallState: "partially_ready",
    NonBlockingIssues: new[] { "optional route is not initialized" },
    BlockingIssues: Array.Empty<string>());
var projection = new OperationProjectionSnapshot(
    "quality-sdk-operation",
    OperationProjectionState.Succeeded,
    1,
    null,
    null)
{
    AnkiReadiness = readiness
};

if (projection.AnkiReadiness is not { ReadyForRequestedAction: true, OverallState: "partially_ready" } scoped ||
    !scoped.SelectedRouteReady || scoped.NonBlockingIssues.Count == 0 || scoped.BlockingIssues.Count != 0)
{
    return 1;
}

if (typeof(OperationSubmission).GetProperty("Artifacts") is not null ||
    typeof(QuestionOperationSubmission).GetProperty("Artifacts") is not null)
{
    return 2;
}

Console.WriteLine("REAL_RUNTIME_SDK_TYPED_READINESS_PASS");
return 0;

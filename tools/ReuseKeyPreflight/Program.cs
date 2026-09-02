using System.Reflection;
using System.Text.Json;
using KnowledgeClipper.Orchestrator.Core;

if (args.Length != 6)
{
    Console.Error.WriteLine("usage: ReuseKeyPreflight <jobRoot> <intentPath> <registryPath> <adapterConfigPath> <runId> <priorRequestPath>");
    return 2;
}

var jobRoot = Path.GetFullPath(args[0]);
var intentPath = Path.GetFullPath(args[1]);
var registryPath = Path.GetFullPath(args[2]);
var adapterConfigPath = Path.GetFullPath(args[3]);
var runId = args[4];
var priorRequestPath = Path.GetFullPath(args[5]);

var registry = new RegistryLoader().Load(registryPath);
var configuration = DesktopIntentResolver.LoadConfiguration(adapterConfigPath);
var plan = await new DesktopIntentResolver().ResolveAsync(jobRoot, intentPath, registry, runId, configuration, CancellationToken.None);
var imageStage = plan.Stages.Single(stage => stage.StageId == "image-ocr");
var imageWorker = registry.Workers.Single(worker => worker.WorkerId == "image.ocr.mineru");
var method = typeof(OrchestratorEngine).GetMethod("ComputeIdempotencyKey", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(typeof(OrchestratorEngine).FullName, "ComputeIdempotencyKey");
var computed = (string?)method.Invoke(null, new object[] { plan.TaskId, imageStage, imageWorker })
    ?? throw new InvalidOperationException("Core idempotency computation returned no value.");
var priorRequest = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(priorRequestPath), ContractJson.Options)
    ?? throw new InvalidDataException("Prior Image WorkerRequest is empty.");
var priorResultPath = Path.Combine(Path.GetDirectoryName(priorRequestPath)!, "result.json");
var priorResult = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(priorResultPath), ContractJson.Options)
    ?? throw new InvalidDataException("Prior Image WorkerResult is empty.");
await WorkerProcessRunner.ValidatePublishedOutputsAsync(jobRoot, priorResult, CancellationToken.None);

var requiredKinds = imageStage.OutputKinds.ToHashSet(StringComparer.Ordinal);
var actualKinds = priorResult.Outputs.Select(output => output.Kind).ToHashSet(StringComparer.Ordinal);
var matched = computed.Equals(priorRequest.IdempotencyKey, StringComparison.OrdinalIgnoreCase) &&
              priorRequest.IdempotencyKey.Equals(priorResult.IdempotencyKey, StringComparison.OrdinalIgnoreCase) &&
              priorRequest.TaskId == plan.TaskId && priorResult.TaskId == plan.TaskId &&
              priorRequest.StageId == imageStage.StageId && priorResult.StageId == imageStage.StageId &&
              priorRequest.WorkerId == imageWorker.WorkerId && priorResult.Worker.Id == imageWorker.WorkerId &&
              priorResult.Status == "completed" && requiredKinds.IsSubsetOf(actualKinds);

var report = new
{
    schemaVersion = "knowledge-clipper-reuse-key-preflight.v1",
    status = matched ? "passed" : "failed",
    taskId = plan.TaskId,
    runId,
    stageId = imageStage.StageId,
    workerId = imageWorker.WorkerId,
    workerContractVersionRange = imageWorker.ContractVersionRange,
    computedIdempotencyKey = computed.ToLowerInvariant(),
    priorRequestIdempotencyKey = priorRequest.IdempotencyKey.ToLowerInvariant(),
    priorResultIdempotencyKey = priorResult.IdempotencyKey.ToLowerInvariant(),
    priorResultStatus = priorResult.Status,
    requiredOutputKinds = imageStage.OutputKinds,
    priorOutputKinds = actualKinds.Order(StringComparer.Ordinal).ToArray(),
    publishedOutputsValidated = true,
    workerLaunchAllowed = false,
    imageWorkerStarts = 0,
    providerCalls = 0
};
Console.WriteLine(JsonSerializer.Serialize(report, ContractJson.Options));
return matched ? 0 : 3;

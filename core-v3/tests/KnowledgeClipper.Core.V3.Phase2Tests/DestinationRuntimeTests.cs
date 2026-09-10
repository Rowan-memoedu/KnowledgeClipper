using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

// Real package/result validation and projection; never invokes a worker process or destination.
internal sealed class DestinationRuntimeFake : IWorkerProcessInvoker
{
    public string Mode = "created";
    public int Invocations;
    public List<string> ResultPaths = new();
    public async Task<WorkerProcessInvocationResult> InvokeAsync(AdmittedAuthorityWorker worker, LegacyAttemptPackage package, TimeSpan timeout, CancellationToken ct)
    {
        Invocations++;
        var request = package.Request;
        if (worker.WorkerId != "export.remnote" || request.Operation != "remnote.export") throw new InvalidDataException("Fake received unexpected route.");
        var stderr = Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "stderr.log");
        await File.WriteAllTextAsync(stderr, "offline fake", ct);
        if (Mode == "exit2") return new(2, false, false, stderr);
        var artifactId = "receipt-fixture";
        var payloadPath = request.OutputPath + "/receipt.json";
        var manifestPath = request.OutputPath + "/artifact.json";
        var receipt = new { schemaVersion = "2.0.0", sink = "mcp-direct", route = "card-export", dateRouting = false, readbackVerified = Mode != "no-readback", duplicateCount = 0,
            rootRemId = Mode == "target-drift" ? "foreign-root" : request.Payload.GetProperty("target").GetProperty("rootRemId").GetString(), documentTitle = request.Payload.GetProperty("documentTitle").GetString(),
            request.IdempotencyKey, outcome = Mode == "already_applied" ? Mode : "created", documentRemId = "fake-document" };
        var payload = JsonSerializer.SerializeToUtf8Bytes(receipt, KernelJson.Options);
        Directory.CreateDirectory(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, request.OutputPath));
        await File.WriteAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, payloadPath), payload, ct);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "1.0.0", artifactId, kind = "remnote.write-receipt", contractVersion = "2.0.0",
            producer = new { workerId = worker.WorkerId, workerVersion = worker.WorkerVersion, request.StageId, request.AttemptId },
            files = new[] { new { path = payloadPath, sha256 = Mode == "hash-drift" ? new string('0', 64) : Hash(payload), bytes = payload.LongLength, mediaType = "application/json" } },
            metadata = new { entrypoint = payloadPath }, inputRefs = request.InputRefs }, KernelJson.Options);
        await File.WriteAllBytesAsync(CoreWorkerTaskPackageAdapter.ResolveWithin(package.JobRoot, manifestPath), manifest, ct);
        var result = new LegacyWorkerResult("1.0.0", request.RequestId, request.OperationId, request.TaskId, request.RunId, request.StageId, request.AttemptId,
            new(worker.WorkerId, Mode == "worker-drift" ? "0.0.0" : worker.WorkerVersion), "1.0.0", request.IdempotencyKey, "completed",
            new[] { new LegacyArtifactReference(artifactId, "remnote.write-receipt", "2.0.0", manifestPath, Hash(manifest)) }, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Array.Empty<string>());
        await File.WriteAllBytesAsync(package.ResultPath, JsonSerializer.SerializeToUtf8Bytes(result, KernelJson.Options), ct);
        ResultPaths.Add(package.ResultPath);
        return new(0, false, false, stderr);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public async Task VerifyAsync(WorkerProcessRuntime runtime, WorkerInvocation source)
    {
        foreach (var mode in new[] { "created", "already_applied", "no-readback", "target-drift", "hash-drift", "worker-drift", "exit2" })
        {
            Mode = mode;
            var invocation = source with { OperationId = "offline-runtime-" + mode, Binding = runtime.GetAdmittedBinding("export.remnote") };
            if (mode == "exit2")
            {
                try { await runtime.ExecuteAsync(invocation, default); throw new InvalidDataException("Expected unknown state."); }
                catch (WorkerExternalStateUnknownException)
                {
                    DestinationTests.Check(runtime.GetMetrics().AnkiUnknownWriteAttempts == 0, "RemNote post-dispatch unknown is not counted as Anki effect");
                }
            }
            else if (mode is "created" or "already_applied")
            {
                var result = await runtime.ExecuteAsync(invocation, default);
                DestinationTests.Check(result.ExternalStateKnown && result.Payload.GetProperty("exactReadback").GetBoolean() &&
                    !result.Payload.GetProperty("externalWriteCountKnown").GetBoolean() && result.Payload.GetProperty("providerMutationCount").ValueKind == JsonValueKind.Null &&
                    result.Payload.GetProperty("logicalDocumentCount").GetInt32() == (mode == "created" ? 1 : 0), "real runtime Fake " + mode + " proves logical document without inventing mutation count");
            }
            else await DestinationTests.Reject(() => runtime.ExecuteAsync(invocation, default), "real runtime Fake rejects " + mode);
        }
    }
}

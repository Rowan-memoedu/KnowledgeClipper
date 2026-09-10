using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed partial class WorkerProcessRuntime
{
    private async Task<WorkerExecutionResult> ExecuteRemNotePublicAsync(AdmittedAuthorityWorker worker, WorkerInvocation invocation, CancellationToken ct)
    {
        var package = await _adapter.PrepareRemNotePublicAsync(invocation, ct).ConfigureAwait(false);
        var process = await _invoker.InvokePublicAsync(worker, package, invocation.Timeout, ct).ConfigureAwait(false);
        if (process.Cancelled) throw new OperationCanceledException("RemNote read-only public action cancelled.", ct);
        if (process.TimedOut) throw new TimeoutException("RemNote read-only public action timed out; write authorization was not granted.");
        Require(process.ExitCode == 0 && File.Exists(package.StandardOutputPath), "RemNote public action did not publish a successful JSON result.");
        var bytes = await File.ReadAllBytesAsync(package.StandardOutputPath, ct).ConfigureAwait(false);
        using var document = StrictJson.Parse(bytes, "RemNote public output");
        var result = document.RootElement;
        Require(RequiredString(result.GetProperty("worker"), "id") == worker.WorkerId && RequiredString(result.GetProperty("worker"), "version") == worker.WorkerVersion,
            "RemNote public result worker identity differs from admission.");
        Require(result.GetProperty("externalWriteCount").GetInt32() == 0, "RemNote public action violated zero-write boundary.");
        var receipt = new { schema = "kc.readonly-worker-execution/1", invocation.OperationId, package.Command,
            worker.WorkerId, worker.WorkerVersion, worker.EntrypointSha256, process.ExitCode,
            requestSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package.RequestPath, ct).ConfigureAwait(false))),
            outputSha256 = Convert.ToHexString(SHA256.HashData(bytes)), externalWriteCount = 0 };
        var path = Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "execution.json");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, receipt, KernelJson.Options, ct).ConfigureAwait(false);
        return new("completed", result.Clone(), Array.Empty<ArtifactReference>(), 0, true);
    }
    private static async Task<WorkerExecutionResult> ProjectRemNoteExportAsync(LegacyAttemptPackage package, LegacyWorkerResult result, CancellationToken ct)
    {
        var receipt = await LoadSinglePayloadAsync(package, result, "remnote.write-receipt", ct).ConfigureAwait(false);
        var request = package.Request;
        Require(RequiredString(receipt, "schemaVersion") == "2.0.0" && RequiredString(receipt, "sink") == "mcp-direct" &&
            RequiredString(receipt, "route") == "card-export" && receipt.GetProperty("dateRouting").ValueKind == JsonValueKind.False &&
            receipt.GetProperty("readbackVerified").ValueKind == JsonValueKind.True && receipt.GetProperty("duplicateCount").GetInt32() == 0 &&
            RequiredString(receipt, "rootRemId") == RequiredString(request.Payload.GetProperty("target"), "rootRemId") &&
            RequiredString(receipt, "documentTitle") == RequiredString(request.Payload, "documentTitle") && RequiredString(receipt, "idempotencyKey") == request.IdempotencyKey,
            "RemNote receipt does not prove the frozen card destination and exact readback.");
        var outcome = RequiredString(receipt, "outcome");
        Require(outcome is "created" or "already_applied" or "recovered_after_uncertain_commit", "RemNote outcome is not verified.");
        var documentId = RequiredString(receipt, "documentRemId");
        // The module proves one logical document, not the number of low-level MCP mutations.
        var logicalWrites = outcome == "already_applied" ? 0 : 1;
        return new("completed", StrictJson.CloneElement(new { exactReadback = true, documentRemId = documentId, outcome,
            logicalDocumentCount = logicalWrites, providerMutationCount = (int?)null, externalWriteCountKnown = false,
            readback = receipt }), Array.Empty<ArtifactReference>(), 0, true);
    }
}

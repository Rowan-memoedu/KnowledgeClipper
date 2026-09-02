using System.Diagnostics;
using System.Text.Json;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed record AttemptExecution(WorkerResult Result, int ExitCode, bool TimedOut, bool Cancelled);

public sealed class WorkerProcessRunner
{
    public async Task<AttemptExecution> RunAsync(
        WorkerEntry worker,
        string releaseRoot,
        string jobRoot,
        WorkerRequest request,
        TimeSpan timeout,
        CancellationReasonState cancellationReason,
        CancellationToken cancellationToken)
    {
        var attemptDirectory = PathPolicy.ResolveJobRelative(jobRoot, request.AttemptPath);
        Directory.CreateDirectory(attemptDirectory);
        var requestPath = PathPolicy.ResolveJobRelative(jobRoot, request.AttemptPath + "/request.json");
        var progressPath = PathPolicy.ResolveJobRelative(jobRoot, request.ProgressPath);
        var resultPath = PathPolicy.ResolveJobRelative(jobRoot, request.ResultPath);
        var cancelSignalPath = PathPolicy.ResolveJobRelative(jobRoot, request.CancelSignal.Path);
        var stderrPath = Path.Combine(attemptDirectory, "stderr.log");
        if (request.CancelSignal.SchemaVersion != "1.0.0" || request.CancelSignal.GracePeriodMs != 2000 ||
            !cancelSignalPath.StartsWith(attemptDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cancel signal must use v1 and remain inside the current attempt.");
        await AtomicFile.WriteJsonOnceAsync(requestPath, request, cancellationToken);

        var executable = PathPolicy.ResolveJobRelative(releaseRoot, worker.Entrypoint.Path);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in worker.Entrypoint.Args)
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start worker {worker.WorkerId}.");
        var stdoutTask = PumpLinesAsync(process.StandardOutput, progressPath, CancellationToken.None);
        var stderrTask = PumpLinesAsync(process.StandardError, stderrPath, CancellationToken.None);
        var timedOut = false;
        var cancelled = false;
        string? cancelReason = null;
        using var monitorStop = new CancellationTokenSource();
        var processExitTask = process.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(timeout, monitorStop.Token);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var signalTask = WaitForCancelSignalAsync(cancelSignalPath, monitorStop.Token);
        var completedTask = await Task.WhenAny(processExitTask, timeoutTask, cancellationTask, signalTask);
        if (completedTask != processExitTask)
        {
            if (completedTask == timeoutTask)
                cancelReason = "timeout";
            else if (completedTask == cancellationTask)
                cancelReason = cancellationReason.Get();
            else
                cancelReason = (await signalTask).Reason;

            timedOut = cancelReason == "timeout";
            cancelled = !timedOut;
            if (!File.Exists(cancelSignalPath))
            {
                var requestedAt = DateTimeOffset.UtcNow;
                var signal = new WorkerCancelSignal(
                    "1.0.0",
                    request.AttemptId,
                    request.OperationId,
                    cancelReason,
                    requestedAt,
                    requestedAt.AddMilliseconds(request.CancelSignal.GracePeriodMs));
                await AtomicFile.WriteJsonOnceAsync(cancelSignalPath, signal, CancellationToken.None);
            }

            var publishedSignal = await ReadCancelSignalAsync(cancelSignalPath, CancellationToken.None);
            var remainingGrace = publishedSignal.GraceDeadline - DateTimeOffset.UtcNow;
            if (remainingGrace < TimeSpan.Zero)
                remainingGrace = TimeSpan.Zero;
            var gracefulExit = await Task.WhenAny(processExitTask, Task.Delay(remainingGrace, CancellationToken.None));
            if (gracefulExit != processExitTask)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }
            }
            await processExitTask;
        }
        monitorStop.Cancel();
        await Task.WhenAll(IgnoreCancellation(stdoutTask), IgnoreCancellation(stderrTask));

        WorkerResult result;
        if (File.Exists(resultPath))
        {
            result = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(resultPath, CancellationToken.None), ContractJson.Options)
                ?? throw new InvalidDataException($"Worker result is empty: {resultPath}");
        }
        else
        {
            result = CreateSyntheticFailure(request, worker, timedOut, cancelled, cancelReason, process.ExitCode, stderrPath, jobRoot);
            await AtomicFile.WriteJsonOnceAsync(resultPath, result, CancellationToken.None);
        }

        ValidateResultIdentity(request, worker.WorkerId, result);
        await ValidatePublishedOutputsAsync(jobRoot, result, CancellationToken.None);
        return new AttemptExecution(result, process.ExitCode, timedOut, cancelled);
    }

    private static async Task PumpLinesAsync(StreamReader reader, string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            await AtomicFile.AppendLineAsync(path, line, cancellationToken);
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<WorkerCancelSignal> WaitForCancelSignalAsync(string path, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                return await ReadCancelSignalAsync(path, cancellationToken);
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task<WorkerCancelSignal> ReadCancelSignalAsync(string path, CancellationToken cancellationToken)
    {
        var signal = JsonSerializer.Deserialize<WorkerCancelSignal>(await File.ReadAllTextAsync(path, cancellationToken), ContractJson.Options)
            ?? throw new InvalidDataException("Worker cancel signal is empty.");
        if (signal.SchemaVersion != "1.0.0" || signal.Reason is not ("user_stop" or "superseded" or "task_cancel" or "timeout"))
            throw new InvalidDataException("Worker cancel signal violates the v1 reason contract.");
        if (signal.GraceDeadline < signal.RequestedAt || signal.GraceDeadline - signal.RequestedAt != TimeSpan.FromMilliseconds(2000))
            throw new InvalidDataException("Worker cancel signal violates the v1 grace deadline contract.");
        return signal;
    }

    private static WorkerResult CreateSyntheticFailure(
        WorkerRequest request,
        WorkerEntry worker,
        bool timedOut,
        bool cancelled,
        string? cancelReason,
        int exitCode,
        string stderrPath,
        string jobRoot)
    {
        var status = cancelled ? "cancelled" : "failed";
        var category = cancelled ? "cancelled" : timedOut ? "transient" : "worker";
        var code = cancelReason is not null ? $"cancel.{cancelReason}" : "worker.crashed";
        var retryable = !cancelled && (worker.IdempotencyLevel is "pure" or "idempotent");
        if (worker.SideEffectLevel == "external" && (worker.IdempotencyLevel is "reconcilable" or "non_idempotent"))
        {
            status = "unknown_external_state";
            category = "effect";
            code = "effect.worker_exit_unknown";
            retryable = false;
        }
        return new WorkerResult(
            "1.0.0",
            request.RequestId,
            request.OperationId,
            request.TaskId,
            request.RunId,
            request.StageId,
            request.AttemptId,
            new WorkerIdentity(worker.WorkerId, "0.0.0-unknown"),
            "1.0.0",
            request.IdempotencyKey,
            status,
            Array.Empty<ArtifactReference>(),
            new WorkerError(code, category, retryable, PathPolicy.ToJobRelative(jobRoot, stderrPath), $"Worker exit code {exitCode}.", "worker"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Array.Empty<string>());
    }

    internal static void ValidateResultIdentity(WorkerRequest request, string workerId, WorkerResult result)
    {
        if (result.SchemaVersion != "1.0.0" || result.ContractVersion != "1.0.0")
            throw new InvalidDataException("Unsupported worker result contract version.");
        if (result.RequestId != request.RequestId || result.OperationId != request.OperationId || result.TaskId != request.TaskId ||
            result.RunId != request.RunId || result.StageId != request.StageId || result.AttemptId != request.AttemptId ||
            result.IdempotencyKey != request.IdempotencyKey)
            throw new InvalidDataException("Worker result identity does not match request.");
        if (result.Worker.Id != workerId)
            throw new InvalidDataException("Worker result ID does not match registry.");
        if (result.Status is not ("completed" or "partial" or "failed" or "cancelled" or "unknown_external_state"))
            throw new InvalidDataException($"Unsupported worker result status: {result.Status}");
        if (result.FinishedAt < result.StartedAt)
            throw new InvalidDataException("Worker result finishedAt precedes startedAt.");
        if (result.Status == "completed" && result.Error is not null)
            throw new InvalidDataException("completed result must not contain an error.");
        if (result.Status == "unknown_external_state" && result.Error?.Retryable != false)
            throw new InvalidDataException("unknown_external_state must not be retryable.");
        if ((result.Status is "failed" or "cancelled" or "unknown_external_state") && result.Outputs.Count != 0)
            throw new InvalidDataException("Failed, cancelled, or unknown attempts must not publish outputs.");
        if (result.Status == "partial")
        {
            var diagnostics = result.ItemDiagnostics ?? Array.Empty<ItemDiagnostic>();
            if (!diagnostics.Any(item => item.Status == "completed") || !diagnostics.Any(item => item.Status == "failed"))
                throw new InvalidDataException("partial requires at least one completed and one failed item.");
        }
    }

    public static async Task ValidatePublishedOutputsAsync(string jobRoot, WorkerResult result, CancellationToken cancellationToken)
    {
        foreach (var output in result.Outputs)
        {
            var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, output.ManifestPath);
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            var actualManifestHash = Integrity.Sha256Hex(manifestBytes);
            if (!actualManifestHash.Equals(output.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Artifact manifest hash mismatch: {output.ManifestPath}");
            using var document = JsonDocument.Parse(manifestBytes);
            foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                await Integrity.VerifyFileAsync(
                    jobRoot,
                    file.GetProperty("path").GetString()!,
                    file.GetProperty("bytes").GetInt64(),
                    file.GetProperty("sha256").GetString()!,
                    cancellationToken);
            }
        }
    }
}

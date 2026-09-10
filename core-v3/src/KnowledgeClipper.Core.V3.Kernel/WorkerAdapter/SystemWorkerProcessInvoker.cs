using System.Diagnostics;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed class SystemWorkerProcessInvoker : IWorkerProcessInvoker
{
    public async Task<WorkerProcessInvocationResult> InvokePublicAsync(AdmittedAuthorityWorker worker, PublicWorkerCommandPackage package, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (worker.WorkerId != "export.remnote" || package.Command is not ("discover-target-public" or "probe-public")) throw new InvalidDataException("Read-only public route not admitted.");
        using var process = new Process { StartInfo = new ProcessStartInfo(worker.Entrypoint) { WorkingDirectory = Path.GetDirectoryName(worker.Entrypoint)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { package.Command, "--request", package.RequestPath, "--json" }) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("Could not start admitted read-only RemNote command.");
        var stdout = PumpAsync(process.StandardOutput, package.StandardOutputPath, CancellationToken.None);
        var stderr = PumpAsync(process.StandardError, package.StandardErrorPath, CancellationToken.None);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        var timedOut = false; var cancelled = false;
        try { await process.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested; cancelled = !timedOut;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new(process.ExitCode, timedOut, cancelled, package.StandardErrorPath);
    }
    public async Task<WorkerProcessInvocationResult> InvokeAsync(
        AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stderrPath = Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "stderr.log");
        var stdoutPath = Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "stdout.log");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = worker.Entrypoint,
                WorkingDirectory = Path.GetDirectoryName(worker.Entrypoint)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--request");
        process.StartInfo.ArgumentList.Add(package.RequestPath);
        if (!process.Start()) throw new InvalidOperationException($"Could not start authority worker '{worker.WorkerId}'.");
        var stdout = PumpAsync(process.StandardOutput, stdoutPath, CancellationToken.None);
        var stderr = PumpAsync(process.StandardError, stderrPath, CancellationToken.None);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            var now = DateTimeOffset.UtcNow;
            var signal = new
            {
                schemaVersion = "1.0.0", package.Request.AttemptId, package.Request.OperationId,
                reason = timedOut ? "timeout" : "task_cancel", requestedAt = now, graceDeadline = now.AddSeconds(2)
            };
            await WriteCancelCreateOnceAsync(package.CancelPath, JsonSerializer.SerializeToUtf8Bytes(signal, KernelJson.Options)).ConfigureAwait(false);
            var graceful = await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new WorkerProcessInvocationResult(process.ExitCode, timedOut, cancelled, stderrPath);
    }

    private static async Task PumpAsync(StreamReader reader, string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteCancelCreateOnceAsync(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
            await stream.WriteAsync(content).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
    }
}

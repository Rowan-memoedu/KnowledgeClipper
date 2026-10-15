using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

public sealed class CodexChatProcess(string helper, string dataRoot)
{
    public const string PythonPath = @"D:\Applications\RemNoteQuickClipperRuntime\codex-python\Scripts\python.exe";
    private Process? process;
    private bool cancelRequested;
    private readonly SemaphoreSlim stdinGate = new(1, 1);
    public async Task CancelAsync()
    {
        await stdinGate.WaitAsync();
        try
        {
            if (process is { HasExited: false } child && !cancelRequested)
            {
                cancelRequested = true;
                await child.StandardInput.WriteLineAsync("{\"command\":\"cancel\"}");
                _ = StopUnresponsiveChild(child);
            }
        }
        catch (IOException) { }
        finally { stdinGate.Release(); }
    }
    private static async Task StopUnresponsiveChild(Process child)
    {
        // Interrupt gets time to finish. A lost reply ends local waiting, retaining Unknown identity.
        await Task.Delay(TimeSpan.FromSeconds(20));
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    public async Task RunAsync(object request, Func<JsonElement, Task> receive, CancellationToken ct = default)
    {
        if (process is { HasExited: false }) throw new InvalidOperationException("同一对话已有在途请求。");
        if (!File.Exists(PythonPath)) throw new FileNotFoundException("Codex SDK 运行时未安装，请运行 tools/Install-CodexChatRuntime.ps1。");
        var folder = Path.Combine(dataRoot, "chat-requests"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        WorkflowJson.Save(path, request);
        var start = new ProcessStartInfo(PythonPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(helper); start.ArgumentList.Add(path);
        start.Environment.Remove("CODEX_APP_TOOLS_PIPE_PATH"); start.Environment["PYTHONUNBUFFERED"] = "1";
        using var child = new Process { StartInfo = start };
        child.Start(); process = child; cancelRequested = false;
        // stderr is drained, never forwarded to the web view (it can contain native diagnostics).
        var diagnostics = child.StandardError.ReadToEndAsync();
        using var cancel = ct.Register(() => { _ = CancelAsync(); });
        try
        {
            string? line;
            while ((line = await child.StandardOutput.ReadLineAsync()) != null)
            {
                using var json = JsonDocument.Parse(line);
                await receive(json.RootElement);
                if (json.RootElement.TryGetProperty("durable", out var durable) && durable.GetBoolean())
                {
                    await stdinGate.WaitAsync();
                    try { await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { ack = json.RootElement.GetProperty("seq").GetInt32() })); }
                    finally { stdinGate.Release(); }
                }
            }
            await child.WaitForExitAsync();
            await diagnostics;
            if (child.ExitCode != 0) throw new IOException("Codex 请求未完成，输入及原身份已保留。");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            process = null;
            // Only this invocation's generated transport input is removed; durable snapshots live in the draft.
            File.Delete(path);
        }
    }
}

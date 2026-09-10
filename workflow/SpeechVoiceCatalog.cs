using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

public sealed record SpeechVoiceOption(string Id, string Locale, string Gender)
{
    public override string ToString() => $"{Id} ({Gender})";
}

public static class SpeechVoiceCatalog
{
    // Uses the same installed Python/edge_tts as streaming. No user text, settings writes or playback.
    public static async Task<IReadOnlyList<SpeechVoiceOption>> DiscoverAsync(string pythonPath, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var info = new ProcessStartInfo(pythonPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import asyncio,json,edge_tts; print(json.dumps(asyncio.run(edge_tts.list_voices())))");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动音色目录查询。");
        using var cancel = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var json = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("音色目录查询失败，请检查现有 Python 与 Edge TTS 网络连接。");
            using var document = JsonDocument.Parse(json);
            var voices = document.RootElement.EnumerateArray().Select(item => new SpeechVoiceOption(
                item.GetProperty("ShortName").GetString()!, item.GetProperty("Locale").GetString()!,
                item.GetProperty("Gender").GetString()!)).DistinctBy(v => v.Id).OrderBy(v => v.Id).ToArray();
            if (voices.Length == 0) throw new InvalidOperationException("Edge TTS 未返回可用音色。");
            return voices;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("音色目录查询超时，请稍后重试。"); }
    }
}

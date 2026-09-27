using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace KnowledgeClipper.Workflow;

public sealed record SpeechVoiceOption(string Id, string Locale, string Gender)
{
    public override string ToString() => $"{Id} ({Gender})" +
        (Id.Contains("Multilingual", StringComparison.OrdinalIgnoreCase) ? " · 多语言，可能朗读中文" : "");
}

public static class SpeechVoiceCatalog
{
    public static string NormalizeVoiceId(string voice)
    {
        if (voice.StartsWith("mimo-", StringComparison.Ordinal)) return voice[5..];
        var english = voice.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
        var chinese = voice.StartsWith("zh-", StringComparison.OrdinalIgnoreCase);
        if (!english && !chinese) return voice;
        var male = new[] { "Guy", "Ryan", "Andrew", "Brian", "Yunxi", "Yunjian" }
            .Any(name => voice.Contains(name, StringComparison.OrdinalIgnoreCase));
        return english ? (male ? "Dean" : "Mia") : (male ? "白桦" : "茉莉");
    }

    public sealed record CachedCatalog(DateTimeOffset ObservedAt, SpeechVoiceOption[] Voices);
    private static string CachePath(string pythonPath) => Path.Combine(@"D:\Caches\KnowledgeClipper\voices\mimo-v2.5-tts",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(pythonPath).ToUpperInvariant()))).ToLowerInvariant() + ".json");

    public static CachedCatalog? ReadCached(string pythonPath)
    {
        try
        {
            var cached = JsonSerializer.Deserialize<CachedCatalog>(File.ReadAllText(CachePath(pythonPath)));
            return cached?.Voices is { Length: > 0 } voices && voices.All(v => v != null && !string.IsNullOrWhiteSpace(v.Id) && !string.IsNullOrWhiteSpace(v.Locale)) ? cached : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private static void SaveCache(string pythonPath, SpeechVoiceOption[] voices)
    {
        try
        {
            var path = CachePath(pythonPath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CachedCatalog(DateTimeOffset.UtcNow, voices)));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Uses the shared MiMo catalog; the configured Python still serves OCR and streaming.
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
        info.ArgumentList.Add("import json,sys\nsys.path.insert(0,r'D:\\Projects\\MimoTTS\\src')\nfrom mimo_tts.client import voices\nprint(json.dumps([{'ShortName':v['id'],'Locale':'en-US' if v['language']=='en' else 'zh-CN','Gender':v['gender']} for v in voices()]))");
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
            if (process.ExitCode != 0)
            {
                var errorType = "provider-error";
                try { using var failure = JsonDocument.Parse(json); errorType = failure.RootElement.GetProperty("errorType").GetString() ?? errorType; }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { }
                throw new InvalidOperationException("音色目录查询失败（" + errorType + "），可稍后刷新；现有音色仍保留。");
            }
            using var document = JsonDocument.Parse(json);
            var voices = document.RootElement.EnumerateArray().Select(item => new SpeechVoiceOption(
                item.GetProperty("ShortName").GetString()!, item.GetProperty("Locale").GetString()!,
                item.GetProperty("Gender").GetString()!)).DistinctBy(v => v.Id).OrderBy(v => v.Id).ToArray();
            if (voices.Length == 0) throw new InvalidOperationException("MiMo 未返回可用音色。");
            SaveCache(pythonPath, voices);
            return voices;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("音色目录查询超时，请稍后重试。"); }
    }
}

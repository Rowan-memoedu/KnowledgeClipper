using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class LiveSpeechCheck
{
    public static async Task RunAsync(LanguageSettings defaults, string? caseFilter = null, string? outputRoot = null)
    {
        if (caseFilter != null && !new[] { "selection", "sentence", "mixed", "translation", "alternate_voice", "chinese" }.Contains(caseFilter)) throw new ArgumentException("Unknown live case.");
        var root = outputRoot ?? @"D:\Artifacts\KnowledgeClipper\validation\maintenance-speech-live";
        const string helper = @"D:\Projects\RemNote Quick Clipper\helpers\stream_speech.py";
        const string ffplay = @"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe";
        Directory.CreateDirectory(root);
        IReadOnlyList<SpeechVoiceOption> voices;
        try { voices = await SpeechVoiceCatalog.DiscoverAsync(defaults.PythonPath); }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(root, "attempts.jsonl"), JsonSerializer.Serialize(new { observedAt = DateTimeOffset.UtcNow, stage = "voice_discovery", error = ex.Message }) + Environment.NewLine);
            throw;
        }
        var english = voices.First(v => v.Id == defaults.EnglishVoice);
        var alternate = voices.First(v => v.Locale == "en-US" && v.Id != english.Id);
        var chinese = voices.First(v => v.Id == defaults.Voice);
        var settings = defaults with { FfplayPath = ffplay, EnglishVoice = english.Id, Voice = chinese.Id };
        var draft = new ClipDraft { Id = Guid.NewGuid().ToString("N"), Original = new("apple"), Settings = settings };
        using var speech = new StreamingSpeech(helper, root);
        var reports = new List<object>();
        var checks = new List<bool>();
        Save();
        await Run("selection", draft, "apple", SpeechPurpose.Selection);
        var originalAudio = draft.AudioPath;
        await Run("sentence", draft, "An apple is on the table.", SpeechPurpose.Sentence);
        if (caseFilter == null) checks.Add(originalAudio == draft.AudioPath);
        await Run("mixed", draft, "苹果 apple，葡萄 grape，最后一句 The pear is ripe.", SpeechPurpose.Selection);
        await Run("translation", draft, "The apple is on the table.", SpeechPurpose.Translation);
        var alternativeDraft = new ClipDraft { Id = draft.Id, Original = new("apple"), Settings = settings with { EnglishVoice = alternate.Id } };
        await Run("alternate_voice", alternativeDraft, "apple", SpeechPurpose.Selection);
        if (caseFilter == null) checks.Add(originalAudio != alternativeDraft.AudioPath);
        await Run("chinese", draft, "苹果在桌上。", SpeechPurpose.Selection);
        Save();
        if (checks.Any(pass => !pass)) throw new InvalidOperationException("A live synthesis, output or cache isolation check failed; inspect report.json.");

        async Task Run(string name, ClipDraft item, string text, SpeechPurpose purpose)
        {
            if (caseFilter != null && name != caseFilter) return;
            Console.WriteLine("LIVE starting " + name);
            using var observedCancellation = new CancellationTokenSource();
            Task<PlaybackObservation>? observed = null;
            var speaking = speech.SpeakTextAsync(item, text, purpose, false, () => { }, pid =>
                observed = WindowsPlaybackMonitor.FirstOutputAsync(pid, observedCancellation.Token));
            var timedOut = false;
            try { await speaking.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException) { timedOut = true; speech.Stop(); await speaking; }
            observedCancellation.Cancel();
            var output = observed == null ? null : await observed;
            var path = Path.Combine(root, item.Id + "-" + StreamingSpeech.AudioCacheKey(text, purpose, item.Settings) + ".mp3");
            var exists = File.Exists(path);
            var bytes = exists ? new FileInfo(path).Length : 0;
            var valid = item.SpeechError == null && exists && bytes > 0 && item.Times.ContainsKey("audioComplete") && item.Times.ContainsKey("playbackComplete");
            var probe = exists ? await Probe(path) : null;
            valid &= probe != null;
            var outputSeen = output?.FirstOutputAt != null;
            checks.Add(valid && outputSeen);
            reports.Add(new { name, text, spokenText = StreamingSpeech.SpokenText(text, item.Settings), timedOut, purpose = purpose.ToString(), voice = StreamingSpeech.SelectVoice(text, item.Settings),
                item.SpeechStatus, item.SpeechError, path, bytes,
                sha256 = exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null,
                probe, times = item.Times.ToDictionary(), output, synthesisAndPlaybackComplete = valid, unmutedProcessOutputObserved = outputSeen });
            Save();
            Console.WriteLine($"LIVE {name}: synthesis/playback={valid}, observed unmuted stream={outputSeen}, bytes={bytes}");
        }
        void Save() => File.WriteAllText(Path.Combine(root, caseFilter == null ? "report.json" : $"report-{caseFilter}.json"), JsonSerializer.Serialize(new {
            observedAt = DateTimeOffset.UtcNow, provider = "existing edge_tts", helper,
            settings.PythonPath, settings.FfplayPath, english, alternate, chinese,
            evidenceBoundary = "Production StreamingSpeech and WindowsPlaybackMonitor linked into isolated DTO harness. No fixture in live path. Metering proves process output, not physical audibility. Mixed Chinese skipping and perceived voice identity have not been verified by listening.",
            reports, completedChecks = checks.Count, passedChecks = checks.Count(v => v)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<JsonElement?> Probe(string path)
    {
        var info = new ProcessStartInfo(@"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffprobe.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_name,sample_rate,channels", "-of", "json", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await error;
        if (process.ExitCode != 0) return null;
        using var json = JsonDocument.Parse(await output);
        return json.RootElement.Clone();
    }
}

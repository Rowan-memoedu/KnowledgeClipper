using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class RecordedSpeechChecks
{
    // A real child process accepts the production helper's MP3 stream, with no audio device.
    public static async Task ReceivePlayerAsync()
    {
        using var bytes = new MemoryStream();
        await Console.OpenStandardInput().CopyToAsync(bytes);
        var report = Environment.GetEnvironmentVariable("KC_RECORDED_PLAYER_REPORT")!;
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { bytes = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes.ToArray())) }));
        if (Environment.GetEnvironmentVariable("KC_RECORDED_PLAYER_FAIL") == "1") Environment.ExitCode = 9;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(@"D:\Caches\KnowledgeClipper\recorded-speech", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var audioRoot = Path.Combine(root, "audio"); Directory.CreateDirectory(audioRoot);
        var helper = Path.Combine(root, "stream_speech.py");
        File.Copy(@"D:\Projects\RemNote Quick Clipper\helpers\stream_speech.py", helper);
        // Warm imports are deliberately slow. Recorded playback must bypass them.
        File.WriteAllText(Path.Combine(root, "edge_tts.py"), "import time\ntime.sleep(4)\nraise RuntimeError('TTS forbidden in recorded playback fixture')\n");
        var playerReport = Path.Combine(root, "player.json");
        Environment.SetEnvironmentVariable("KC_RECORDED_PLAYER_REPORT", playerReport);
        var settings = new LanguageSettings { FfplayPath = Path.Combine(AppContext.BaseDirectory, "KnowledgeClipper.SpeechUsage.Tests.exe") };
        var oldSettings = settings with { EnglishVoice = "en-US-AvaMultilingualNeural", RatePercent = 15 };
        var recording = Path.Combine(audioRoot, "old-draft-" + StreamingSpeech.AudioCacheKey("Lead", SpeechPurpose.Selection, oldSettings) + ".mp3");
        var info = new ProcessStartInfo(@"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffmpeg.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.15", "-codec:a", "libmp3lame", recording }) info.ArgumentList.Add(arg);
        using (var generator = Process.Start(info)!) { await generator.WaitForExitAsync(); Check(generator.ExitCode == 0, "generated independent MP3 fixture without provider or audio output"); }
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(recording)));
        using var speech = new StreamingSpeech(helper, audioRoot);
        speech.Prepare(settings);
        var draft = new ClipDraft { Id = "new-draft", Original = new("lead"), Settings = settings with { EnglishVoice = "changed-voice", RatePercent = -20 } };
        var snapshot = draft.Settings;
        var clock = Stopwatch.StartNew();
        double? playerMs = null;
        await speech.SpeakRecordedAsync(draft, "Lead", oldSettings, recording, () => { }, _ => playerMs = clock.Elapsed.TotalMilliseconds);
        Check(draft.SpeechError == null && draft.Times.ContainsKey("playbackComplete"), "production helper completed explicit recorded playback");
        Check(playerMs is >= 0 and < 3500, "recorded player starts before four-second TTS dependency import");
        Check(ReferenceEquals(draft.Settings, snapshot) && draft.AudioPath == recording, "new draft settings and source recording remain intact");
        using (var received = JsonDocument.Parse(File.ReadAllText(playerReport)))
            Check(received.RootElement.GetProperty("sha256").GetString() == originalHash, "real player child received exact existing MP3 bytes");
        Check(File.GetLastWriteTimeUtc(recording) < DateTime.UtcNow && Directory.GetFiles(audioRoot).Length == 1, "replay creates no synthesized audio");
        await Reject("wrong source text", () => speech.SpeakRecordedAsync(draft, "lead", oldSettings, recording, () => { }));
        await Reject("wrong source voice", () => speech.SpeakRecordedAsync(draft, "Lead", settings, recording, () => { }));
        var outside = Path.Combine(root, Path.GetFileName(recording)); File.Copy(recording, outside);
        await Reject("outside audio root", () => speech.SpeakRecordedAsync(draft, "Lead", oldSettings, outside, () => { }));
        var empty = Path.Combine(audioRoot, "empty-" + StreamingSpeech.AudioCacheKey("Lead", SpeechPurpose.Selection, oldSettings) + ".mp3"); File.WriteAllBytes(empty, []);
        await Reject("empty recording", () => speech.SpeakRecordedAsync(draft, "Lead", oldSettings, empty, () => { }));
        await Reject("missing recording", () => speech.SpeakRecordedAsync(draft, "Lead", oldSettings, Path.Combine(audioRoot, "missing-" + Path.GetFileName(recording)), () => { }));
        Environment.SetEnvironmentVariable("KC_RECORDED_PLAYER_FAIL", "1");
        await speech.SpeakRecordedAsync(draft, "Lead", oldSettings, recording, () => { });
        Check(draft.SpeechError != null && Directory.GetFiles(audioRoot).Length == 2, "failed explicit playback reports failure without TTS fallback");
        Environment.SetEnvironmentVariable("KC_RECORDED_PLAYER_FAIL", null);
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, playerStartedMs = playerMs, originalHash,
            scope = "Production C# and Python helper, generated MP3 and real receiver process; no TTS service or audio device." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("RESULT " + Path.Combine(root, "result.json"));
    }
    private static async Task Reject(string name, Func<Task> operation)
    {
        try { await operation(); } catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException) { Check(true, "reject " + name); return; }
        throw new Exception("Expected rejection: " + name);
    }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
}

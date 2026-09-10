using KnowledgeClipper.Workflow;
using System.Text.Json;

var settings = new LanguageSettings();
if (args.Contains("--live")) { await LiveSpeechCheck.RunAsync(settings, args.SkipWhile(a => a != "--case").Skip(1).FirstOrDefault()); return; }
Check(StreamingSpeech.SelectVoice("只有中文。", settings) == settings.Voice, "Chinese voice");
Check(StreamingSpeech.SelectVoice("English text", settings) == settings.EnglishVoice, "English voice");
Check(StreamingSpeech.SelectVoice("这段 English 保留中文", settings) == settings.EnglishVoice, "Mixed text uses English voice");
Check(StreamingSpeech.SelectVoice("English", settings with { AutoVoice = false }) == settings.Voice, "Manual voice retained");
var key = StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Selection, settings);
Check(key != StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Sentence, settings), "Purpose isolation");
Check(key != StreamingSpeech.AudioCacheKey("selected sentence", SpeechPurpose.Selection, settings), "Text isolation");
Check(key != StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Selection, settings with { EnglishVoice = "other" }), "Voice isolation");
Check(key != StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Selection, settings with { RatePercent = 25 }), "Rate isolation");
var audioRoot = @"D:\Artifacts\KnowledgeClipper\validation\speech-usage-unit";
Directory.CreateDirectory(audioRoot);
using (var speech = new StreamingSpeech(Path.Combine(AppContext.BaseDirectory, "speech-fixture.py"), audioRoot))
{
    var draft = new ClipDraft { Id = Guid.NewGuid().ToString("N") };
    await speech.SpeakAsync(draft, false, () => { });
    Check(draft.SpeechError == null && File.ReadAllText(draft.AudioPath!) == "selected", "Default transport reads selection");
    var selectionAudio = draft.AudioPath;
    await speech.SpeakTextAsync(draft, "the complete sentence", SpeechPurpose.Sentence, false, () => { });
    Check(draft.SpeechError == null && draft.AudioPath == selectionAudio, "Sentence keeps default AudioPath");
    await speech.SpeakAsync(draft, true, () => { });
    Check(draft.SpeechError == null && draft.AudioPath == selectionAudio && File.ReadAllText(draft.AudioPath!) == "selected", "Replay selection after sentence stays selection");
    await speech.SpeakTextAsync(draft, "the full translation", SpeechPurpose.Translation, false, () => { });
    Check(draft.SpeechError == null && File.ReadAllText(draft.AudioPath!) == "the full translation", "Explicit translation transport");
    await speech.SpeakAsync(draft, true, () => { });
    Check(draft.SpeechError == null && draft.AudioPath == selectionAudio, "Selection replay restores AudioPath after translation");
}
if (args.Length == 2 && args[0] == "--discover")
{
    var voices = await SpeechVoiceCatalog.DiscoverAsync(settings.PythonPath);
    Check(voices.Any(v => v.Locale.StartsWith("en-")) && voices.Any(v => v.Locale.StartsWith("zh-")), "Live Edge voice discovery");
    File.WriteAllText(args[1], JsonSerializer.Serialize(new { observedAt = DateTimeOffset.UtcNow, voices }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"LIVE voice count: {voices.Count}");
}
void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }

// Transport fixture exercises orchestration, not provider synthesis or physical playback.
// Isolated compile boundary: only the speech class's DTO/platform dependencies are substituted.
namespace KnowledgeClipper.Workflow
{
    public sealed record LanguageSettings
    {
        public string PythonPath { get; init; } = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe";
        public string FfplayPath { get; init; } = "unused";
        public bool AutoVoice { get; init; } = true;
        public string Voice { get; init; } = "zh-CN-XiaoxiaoNeural";
        public string EnglishVoice { get; init; } = "en-US-AvaMultilingualNeural";
        public int RatePercent { get; init; }
        public int VolumePercent { get; init; }
        public int PitchHz { get; init; }
    }
    public sealed record CaptureMaterial(string SelectedText);
    public sealed class ClipDraft
    {
        public string Id { get; set; } = "test";
        public CaptureMaterial Original { get; set; } = new("selected");
        public LanguageSettings Settings { get; set; } = new();
        public string? AudioPath { get; set; }
        public string? SpeechStatus { get; set; }
        public string? SpeechError { get; set; }
        public System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Times { get; } = new();
    }
    public static class WorkflowJson { public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web); }
}

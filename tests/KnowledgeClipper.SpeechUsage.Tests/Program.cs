using KnowledgeClipper.Workflow;
using System.Text.Json;

if (args.Contains("-nodisp")) { await RecordedSpeechChecks.ReceivePlayerAsync(); return; }
if (args.Contains("--recorded")) { await RecordedSpeechChecks.RunAsync(); return; }
if (args.Contains("--pronunciation")) { await PronunciationChecks.RunAsync(args[Array.IndexOf(args, "--pronunciation") + 1]); return; }

var settings = new LanguageSettings();
if (args.Contains("--live"))
{
    string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
    await LiveSpeechCheck.RunAsync(settings with { EnglishVoice = Option("--voice") ?? settings.EnglishVoice }, Option("--case"), Option("--output")); return;
}
Check(StreamingSpeech.SelectVoice("只有中文。", settings) == settings.Voice, "Chinese voice");
Check(StreamingSpeech.SelectVoice("English text", settings) == settings.EnglishVoice, "English voice");
Check(StreamingSpeech.SelectVoice("这段 English 保留中文", settings) == settings.EnglishVoice, "Mixed text uses English voice");
Check(StreamingSpeech.SelectVoice("English", settings with { AutoVoice = false }) == settings.Voice, "Manual voice retained");
foreach (var speaker in new[] { "Ava", "Andrew", "Brian", "Emma" })
{
    var multilingual = settings with { EnglishVoice = "en-US-" + speaker + "MultilingualNeural" };
    Check(StreamingSpeech.SelectVoice("Flux", multilingual) == "en-US-" + speaker + "Neural", speaker + " automatic English uses monolingual counterpart");
    Check(StreamingSpeech.SelectVoice("pain", multilingual) == "en-US-" + speaker + "Neural", speaker + " independent shared-language word keeps English voice");
}
Check(StreamingSpeech.SpokenText("苹果 apple，葡萄 grape", settings) == "apple， grape", "Mixed speech skips Chinese before synthesis");
Check(StreamingSpeech.SpokenText("前缀hello中间world后缀", settings) == "hello world", "Removing Chinese does not join adjacent English words");
Check(StreamingSpeech.SpokenText("𠀀hello𰀀", settings) == "hello", "Supplementary Han characters are skipped");
Check(StreamingSpeech.SpokenText("只有中文。", settings) == "只有中文。", "Pure Chinese playback is preserved");
Check(StreamingSpeech.SpokenText("English\ntext", settings) == "English\ntext", "Pure English text is preserved");
Check(StreamingSpeech.SpokenText("苹果 apple", settings with { AutoVoice = false }) == "苹果 apple", "Manual playback preserves mixed text");
Check(StreamingSpeech.SpeechStyle("严谨", settings).Contains("标准普通话"), "Chinese words receive Mandarin reading instructions");
Check(StreamingSpeech.SpeechStyle("pain", settings).Contains("American English"), "Short English words receive an explicit pronunciation language");
Check(StreamingSpeech.SpeechStyle("苹果 apple", settings).Contains("American English"), "Automatic mixed text style follows the actual English spoken text");
Check(StreamingSpeech.SpeechStyle("苹果 apple", settings with { AutoVoice = false }).Contains("按原文顺序切换语言"), "Manual bilingual speech keeps both languages");
var key = StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Selection, settings);
var legacyKey = StreamingSpeech.LegacyAudioCacheKey("selected", SpeechPurpose.Selection, settings);
Check(key != legacyKey, "New reading instructions do not reuse an unguided synthesis cache entry");
Check(StreamingSpeech.MatchesAudioCacheKey("history-" + legacyKey + ".mp3", "selected", SpeechPurpose.Selection, settings), "Explicit old recording identity remains valid");
Check(!StreamingSpeech.MatchesAudioCacheKey("history-" + legacyKey + ".mp3", "different", SpeechPurpose.Selection, settings), "Legacy compatibility still rejects different source text");
Check(key != StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Sentence, settings), "Purpose isolation");
Check(key != StreamingSpeech.AudioCacheKey("selected sentence", SpeechPurpose.Selection, settings), "Text isolation");
Check(key != StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Selection, settings with { EnglishVoice = "other" }), "Voice isolation");
Check(key != StreamingSpeech.AudioCacheKey("selected", SpeechPurpose.Selection, settings with { RatePercent = 25 }), "Rate isolation");
var audioRoot = @"D:\Artifacts\KnowledgeClipper\validation\speech-usage-unit";
Directory.CreateDirectory(audioRoot);
using (var speech = new StreamingSpeech(Path.Combine(AppContext.BaseDirectory, "speech-fixture.py"), audioRoot))
{
    speech.Prepare(settings);
    await speech.PreparationReady;
    var preparedId = speech.PreparedProcessId;
    speech.Prepare(settings);
    Check(speech.PreparedProcessId == preparedId, "Repeated selection keeps a recent prepared helper");
    var preparedField = typeof(StreamingSpeech).GetField("prepared", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var preparedValue = preparedField.GetValue(speech)!;
    var preparedType = preparedValue.GetType();
    preparedField.SetValue(speech, Activator.CreateInstance(preparedType, preparedType.GetProperty("Process")!.GetValue(preparedValue),
        settings.PythonPath, preparedType.GetProperty("Ready")!.GetValue(preparedValue),
        System.Diagnostics.Stopwatch.GetTimestamp() - 21 * System.Diagnostics.Stopwatch.Frequency));
    speech.Prepare(settings);
    await speech.PreparationReady;
    Check(speech.PreparedProcessId != preparedId, "Selection after idle refreshes an expired prepared helper without sending text");
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
    var mixed = new ClipDraft { Id = Guid.NewGuid().ToString("N"), Original = new("苹果 apple 和 pear"),
        Settings = settings with { EnglishVoice = "en-US-AvaMultilingualNeural" } };
    await speech.SpeakAsync(mixed, false, () => { });
    Check(mixed.SpeechError == null && File.ReadAllText(mixed.AudioPath!) == "apple pear" && mixed.Original.SelectedText == "苹果 apple 和 pear",
        "Multilingual voice transport receives only English while the captured source stays intact");
    var legacyDraft = new ClipDraft { Id = Guid.NewGuid().ToString("N") };
    var legacyPath = Path.Combine(audioRoot, legacyDraft.Id + "-" + legacyKey + ".mp3");
    File.WriteAllText(legacyPath, "previous recording");
    await speech.SpeakAsync(legacyDraft, true, () => { });
    Check(legacyDraft.SpeechError == null && legacyDraft.AudioPath == legacyPath && File.ReadAllText(legacyPath) == "previous recording" &&
        !legacyDraft.Times.ContainsKey("audioComplete"), "Replay after upgrade plays the original recording without synthesis");
    await speech.SpeakAsync(legacyDraft, false, () => { });
    Check(legacyDraft.SpeechError == null && legacyDraft.AudioPath != legacyPath && File.ReadAllText(legacyPath) == "previous recording",
        "Explicit fresh synthesis uses the new profile and preserves the old recording");
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
        public string PythonPath { get; init; } = Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_CODEX_PYTHON") ??
            @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe";
        public string FfplayPath { get; init; } = "unused";
        public bool AutoVoice { get; init; } = true;
        public bool SpeechAutoPlay { get; init; } = true;
        public string Voice { get; init; } = "zh-CN-XiaoxiaoNeural";
        public string EnglishVoice { get; init; } = "en-US-AnaNeural";
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

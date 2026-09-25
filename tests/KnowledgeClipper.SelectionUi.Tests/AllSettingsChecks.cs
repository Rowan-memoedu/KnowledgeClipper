using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using KnowledgeClipper.Workflow;

// Uses the production menu, form, persistence and speech helper with isolated settings.
// Voice discovery and the short public speech samples are LIVE. No Anki mutation or real capture.
internal static class AllSettingsChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Get(object target, string field) => target.GetType().GetField(field, Instance)!.GetValue(target);
    private static object? Call(object target, string method, params object[] values) => target.GetType().GetMethod(method, Instance)!.Invoke(target, values);
    private static T Find<T>(Form form, string suffix) where T : Control => (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();
    private static IEnumerable<Control> Controls(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Controls(c)));
    private static void Page(Control control)
    {
        var page = control.Parent; while (page is not TabPage) page = page!.Parent;
        ((TabControl)page.Parent!).SelectedTab = (TabPage)page;
    }
    private static async Task Until(Func<bool> done, int seconds = 60)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(40);
        if (!done()) throw new TimeoutException("Settings action did not complete.");
    }

    internal static async Task RunAsync(string projectRoot, string receipt)
    {
        var checks = new List<string>();
        var liveSpeech = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\all-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(dataRoot, "language-settings.json");
        var baseline = new LanguageSettings { AnkiRootDeck = "Settings fixture", AnkiDeck = "Settings fixture", TypedInputAutoRead = false,
            ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1" };
        WorkflowJson.Save(settingsPath, baseline);
        var beforePreview = File.ReadAllText(settingsPath);
        var assembly = Assembly.Load("KnowledgeClipper");
        // Synthetic credentials must remain inside this isolated test process.
        var secretConfig = assembly.GetType("KnowledgeClipper.SecretConfig", true)!;
        secretConfig.GetField("DeepSeekWriteTarget", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, EnvironmentVariableTarget.Process);
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(projectRoot, "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Instance).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        var mainType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        using var main = (Form)mainType.GetConstructor(Instance, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        Call(main, "EnableLanguageWorkflow"); // Keep owner hidden: the real global selection hook must not start in this check.
        var flow = Get(main, "languageFlow")!;
        Call(main, "OpenSettings");
        var dialog = (Form)Get(main, "languageSettingsForm")!;
        Call(main, "OpenSettings");
        Check(ReferenceEquals(dialog, Get(main, "languageSettingsForm")) && Get(main, "settingsForm") == null,
            "both settings entry points reuse the same current settings window");
        var editable = Controls(dialog).Where(c => c is TextBox or ComboBox or NumericUpDown or CheckBox).ToArray();
        Check(editable.All(c => c.Enabled && (c is not TextBox text || !text.ReadOnly)), "every current functional setting is editable");
        Check(Controls(dialog).OfType<TabControl>().Single().TabPages.Cast<TabPage>().Select(page => page.Name).SequenceEqual(
            new[] { "capture", "floating", "routing", "speech", "anki", "content", "runtime", "destinations", "modes" }
                .Select(id => "knowledge-clipper.language.page." + id)), "all current settings are grouped into nine distinct accessible pages");

        var english = Find<ComboBox>(dialog, "english-voice");
        Page(english);
        await Until(() => Find<Button>(dialog, "read-voices").Enabled);
        if (english.Items.Count == 0) Console.WriteLine("LIVE catalog error: " + Find<Label>(dialog, "settings-notice").Text);
        Check(english.Items.Count > 10 && Find<ComboBox>(dialog, "voice").Items.Count > 2, "LIVE available English and Chinese voices load when opening the speech page");
        var oldEnglish = english.Text;
        var selectedEnglish = english.Items.Cast<SpeechVoiceOption>().First(v => v.Id == "en-US-GuyNeural");
        english.SelectedItem = selectedEnglish;
        var chinese = Find<ComboBox>(dialog, "voice");
        chinese.SelectedItem = chinese.Items.Cast<SpeechVoiceOption>().First(v => v.Id == "zh-CN-YunxiNeural");
        Find<NumericUpDown>(dialog, "rate").Value = 17;
        Find<NumericUpDown>(dialog, "volume").Value = -12;
        Find<NumericUpDown>(dialog, "pitch").Value = 7;
        Check(english.Text != oldEnglish && Find<CheckBox>(dialog, "auto-voice").Checked, "a different real voice can be selected without disabling automatic language routing");
        foreach (var suffix in new[] { "preview-english-voice", "preview-voice" })
        {
            var button = Find<Button>(dialog, suffix); button.PerformClick();
            await Until(() => button.Enabled);
            var draft = (ClipDraft)Get(dialog, "previewDraft")!;
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(receipt)!, "speech-attempts.jsonl"), JsonSerializer.Serialize(new {
                observedAt = DateTimeOffset.UtcNow, purpose = suffix, voice = draft.Settings.Voice, draft.Settings.RatePercent,
                draft.Settings.VolumePercent, draft.Settings.PitchHz, draft.AudioPath, draft.Times, draft.SpeechError }) + Environment.NewLine);
            if (draft.SpeechError != null) Console.WriteLine("LIVE speech error: " + draft.SpeechError);
            Check(draft.SpeechError == null && draft.Times.ContainsKey("playbackComplete") && File.Exists(draft.AudioPath) && new FileInfo(draft.AudioPath!).Length > 1000,
                "LIVE " + suffix + " synthesizes and completes playback with the changed voice and parameters");
            Check(draft.Settings.RatePercent == 17 && draft.Settings.VolumePercent == -12 && draft.Settings.PitchHz == 7 &&
                draft.Settings.Voice == (suffix.Contains("english") ? "en-US-GuyNeural" : "zh-CN-YunxiNeural"), "preview consumes the current unsaved speech fields for " + suffix);
            liveSpeech.Add(new { purpose = suffix, voice = draft.Settings.Voice, draft.AudioPath, draft.Times, draft.SpeechError });
        }
        Check(File.ReadAllText(settingsPath) == beforePreview, "voice discovery and both previews leave saved settings unchanged");
        Find<CheckBox>(dialog, "auto-voice").Checked = false;
        Check(chinese.Items.Cast<SpeechVoiceOption>().Any(v => v.Locale.StartsWith("es-")) && chinese.Text.Contains("Yunxi"), "manual voice exposes other languages and keeps the selected Chinese voice");
        Find<CheckBox>(dialog, "auto-voice").Checked = true;

        var changes = new Dictionary<string, object>
        {
            ["global-selection"] = true, ["auto-read"] = false, ["typed-auto-read"] = true,
            ["question-card-context"] = false, ["context-enabled"] = true, ["context-granularity"] = 1,
            ["practice"] = 2, ["speech-auto-play"] = false, ["content-timeout"] = 7m, ["content-retries"] = 2m,
            ["anki-endpoint"] = "http://localhost:18765", ["deck"] = "Modified settings::Root", ["note-type"] = "Modified fixture model",
            ["api-url"] = "http://localhost:1", ["api-model"] = "fixture-model-changed",
            ["python-path"] = Path.Combine(Path.GetDirectoryName(baseline.PythonPath)!, ".", "python.exe"),
            ["node-path"] = Path.Combine(Path.GetDirectoryName(baseline.NodePath)!, ".", "node.exe"),
            ["ffplay-path"] = Path.Combine(Path.GetDirectoryName(baseline.FfplayPath)!, ".", "ffplay.exe")
        };
        foreach (var (id, value) in changes)
        {
            var control = Find<Control>(dialog, id);
            if (control is CheckBox check) check.Checked = (bool)value;
            else if (control is NumericUpDown number) number.Value = (decimal)value;
            else if (control is ComboBox combo && value is int index) combo.SelectedIndex = index;
            else control.Text = (string)value;
        }
        // A synthetic credential checks the real encrypted credential store; never use or print a user's key.
        Find<TextBox>(dialog, "api-key").Text = "settings-fixture-key";
        var mode = Find<ComboBox>(dialog, "mode"); Page(mode);
        Find<CheckBox>(dialog, "follow-up-default").Checked = !Find<CheckBox>(dialog, "follow-up-default").Checked;
        Find<TextBox>(dialog, "mode-description").Text += " Settings fixture description.";
        Find<TextBox>(dialog, "processing-prompt").Text += "\nSettings fixture prompt edit.";
        Find<ComboBox>(dialog, "mode-deck-path").Text = "Edited::Mode";
        var modeBeforeSave = (ModeDefinition)mode.SelectedItem!;
        var save = Find<Button>(dialog, "save-settings"); save.PerformClick();
        await Task.Delay(60);
        Check(dialog.IsDisposed, "all valid settings save together through the production Save action");
        var saved = WorkflowJson.Read<LanguageSettings>(settingsPath);
        var savedBytes = File.ReadAllText(settingsPath);
        Check(saved.Voice == "zh-CN-YunxiNeural" && saved.EnglishVoice == "en-US-GuyNeural" && saved.RatePercent == 17 && saved.VolumePercent == -12 && saved.PitchHz == 7,
            "changed voices and all speech parameters persist as IDs and numbers");
        Check(saved.AnkiRootDeck == saved.AnkiDeck && saved.AnkiDeck == "Modified settings::Root", "root and compatible deck destination stay synchronized");
        Check(saved.SpeechAutoPlay == false && saved.GlobalSelection && saved.Practice == PracticeChoice.Writing && saved.ContentMaxRetries == 2 && saved.ContentTimeoutSeconds == 7,
            "playback, capture, practice and request policies persist");
        Check(!File.ReadAllText(settingsPath).Contains("settings-fixture-key"), "credential is excluded from ordinary settings JSON");
        var synthetic = secretConfig.GetMethod("Get", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, ["DEEPSEEK_KNOWLEDGECLIPPER_API_KEY"]);
        Check((string?)synthetic == "settings-fixture-key", "synthetic credential is available only in the isolated process");
        Call(main, "OpenSettings");
        dialog = (Form)Get(main, "languageSettingsForm")!;
        foreach (var (id, value) in changes)
        {
            var control = Find<Control>(dialog, id);
            var actual = control is CheckBox check ? (object)check.Checked : control is NumericUpDown number ? number.Value :
                control is ComboBox combo && value is int ? combo.SelectedIndex : control.Text;
            Check(actual.Equals(value), "reopened setting retains " + id);
        }
        Check(Find<TextBox>(dialog, "api-key").Text == "", "reopened credential field stays empty and protected");
        string SelectedVoice(ComboBox control) => control.SelectedItem is SpeechVoiceOption voice ? voice.Id : control.Text;
        Check(SelectedVoice(Find<ComboBox>(dialog, "voice")) == saved.Voice && SelectedVoice(Find<ComboBox>(dialog, "english-voice")) == saved.EnglishVoice, "reopened voices retain exact IDs before refresh");
        Check(Find<TextBox>(dialog, "mode-description").Text.EndsWith("Settings fixture description.") &&
            Find<TextBox>(dialog, "processing-prompt").Text.EndsWith("Settings fixture prompt edit.") &&
            Find<CheckBox>(dialog, "follow-up-default").Checked != modeBeforeSave.FollowUpDefault && Find<ComboBox>(dialog, "mode-deck-path").Text == "Edited::Mode",
            "mode prompt, description, follow-up default and deck edit all round-trip");
        Find<TextBox>(dialog, "python-path").Text = @"D:\missing-settings-fixture\python.exe";
        Find<Button>(dialog, "save-settings").PerformClick();
        Check(!dialog.IsDisposed && File.ReadAllText(settingsPath) == savedBytes, "invalid runtime path is rejected without committing partial settings");
        dialog.Close();
        Check(File.ReadAllText(settingsPath) == savedBytes, "closing without saving discards unsaved edits");
        Check(StreamingSpeech.SelectVoice("English", saved) == saved.EnglishVoice && StreamingSpeech.SelectVoice("中文", saved) == saved.Voice, "saved voice settings drive language routing");
        // Prove the saved auto-play policy using the production helper, then explicit playback of the cached result.
        var material = new CaptureMaterial("The new settings have been saved.", null, "unavailable", null, "Public settings sample", 0, DateTimeOffset.UtcNow);
        var speechDraft = new ClipDraft { Original = material, Material = material, Settings = saved, Configuration = new("", []) };
        using (var speech = new StreamingSpeech(Path.Combine(projectRoot, "helpers", "stream_speech.py"), Path.Combine(Path.GetDirectoryName(receipt)!, "saved-settings-speech")))
        {
            await speech.SpeakAsync(speechDraft, false, () => { });
            Check(speechDraft.SpeechError == null && File.Exists(speechDraft.AudioPath) && !speechDraft.Times.ContainsKey("playerStarted"), "LIVE saved auto-play off synthesizes and saves audio without starting a player");
            var audio = speechDraft.AudioPath;
            await speech.SpeakAsync(speechDraft, true, () => { });
            Check(speechDraft.SpeechError == null && speechDraft.AudioPath == audio && speechDraft.Times.ContainsKey("playbackComplete"), "LIVE explicit replay plays the saved audio even when automatic playback is off");
            liveSpeech.Add(new { purpose = "saved-settings-replay", voice = saved.EnglishVoice, speechDraft.AudioPath, speechDraft.Times, speechDraft.SpeechError });
        }
        await (Task<bool>)Call(flow, "StartQuestionTextAsync", "Settings snapshot fixture")!;
        var nextDraft = (ClipDraft)Get(flow, "latest")!;
        Check(nextDraft.Settings.Voice == saved.Voice && nextDraft.Settings.EnglishVoice == saved.EnglishVoice &&
            nextDraft.Settings.Practice == saved.Practice && nextDraft.Practice == saved.Practice,
            "the next real desktop operation freezes the edited settings into its draft");
        await Task.Delay(1600); // Allow the deliberately unreachable local content endpoint to finish its configured retries.
        Call(main, "ExitApplication");
        WorkflowJson.Save(receipt, new { passed = true, checks, editableControlCount = editable.Length, dataRoot, liveSpeech,
            scope = "Production settings controls and isolated persistence; LIVE voice discovery, synthesis and playback; no user settings change, external Anki write or real selection capture." });
    }
}

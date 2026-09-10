using System.IO;
using System.Reflection;
using KnowledgeClipper.Workflow;

internal static class DeckSettingsChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Find<T>(Form form, string suffix) where T : Control => (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();
    private static object? Get(object target, string field) => target.GetType().GetField(field, Instance)!.GetValue(target);
    private static object? Call(object target, string method, params object[] values) => target.GetType().GetMethod(method, Instance)!.Invoke(target, values);

    internal static async Task RunAsync(string projectRoot, string receipt)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\deck-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(dataRoot, "language-settings.json");
        WorkflowJson.Save(settingsPath, new LanguageSettings { AnkiDeck = "旧牌组", AnkiRootDeck = null, GlobalSelection = false, TypedInputAutoRead = false,
            ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1" });
        var overrides = Path.Combine(dataRoot, "modes"); Directory.CreateDirectory(overrides);
        var defaults = ModeConfigurationLoader.Load(Path.Combine(projectRoot, "modes"));
        var definitions = defaults.Modes.Select(m => m.Definition with { Description = m.Definition.Description + "（设置检查）" }).ToArray();
        WorkflowJson.Save(Path.Combine(overrides, "modes.json"), new ModeFile(definitions));
        foreach (var mode in defaults.Modes) File.WriteAllText(Path.Combine(overrides, mode.Definition.PromptFile), mode.Prompt + "\n设置检查保留行。\n");
        var original = ModeConfigurationLoader.Load(Path.Combine(projectRoot, "modes"), overrides);
        var originalModeFile = File.ReadAllText(Path.Combine(overrides, "modes.json"));
        var assembly = Assembly.Load("KnowledgeClipper");
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, new[] { Path.Combine(projectRoot, "appsettings.json") })!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Instance).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        var mainType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        using var main = (Form)mainType.GetConstructor(Instance, null, new[] { configType, controllerType, typeof(bool) }, null)!.Invoke(new[] { config, controller, false });
        Call(main, "EnableLanguageWorkflow"); main.Show(); await Task.Delay(60);
        var flow = Get(main, "languageFlow")!;
        Form Open() => (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Instance).Single().Invoke(new[] { flow, dataRoot });
        var editedPaths = new Dictionary<string, string> { ["language"] = "学习::{语种}::{格式}", ["question"] = "问答::课堂", ["translation"] = "翻译::专用" };
        using (var dialog = Open())
        {
            dialog.Show(main); await Task.Delay(50);
            Check(Find<ComboBox>(dialog, "deck").Text == "旧牌组", "legacy deck is shown as the editable root before migration");
            Find<ComboBox>(dialog, "deck").Text = "KnowledgeClipper UI Fixture";
            var modes = Find<ComboBox>(dialog, "mode");
            var page = modes.Parent; while (page is not TabPage) page = page!.Parent;
            ((TabControl)page.Parent!).SelectedTab = (TabPage)page;
            for (var index = 0; index < modes.Items.Count; index++)
            {
                modes.SelectedIndex = index; var mode = (ModeDefinition)modes.SelectedItem!;
                Check(Find<TextBox>(dialog, "mode-deck-path").Text == DeckRouting.PathFor(mode), "mode editor exposes effective default path for " + mode.Id);
                Find<TextBox>(dialog, "mode-deck-path").Text = editedPaths[mode.OutputKind];
            }
            for (var index = 0; index < modes.Items.Count; index++)
            {
                modes.SelectedIndex = index; var mode = (ModeDefinition)modes.SelectedItem!;
                Check(Find<TextBox>(dialog, "mode-deck-path").Text == editedPaths[mode.OutputKind], "mode switching retains the edited path for " + mode.Id);
            }
            var questionIndex = Enumerable.Range(0, modes.Items.Count).Single(i => ((ModeDefinition)modes.Items[i]!).OutputKind == "question");
            modes.SelectedIndex = questionIndex; Find<TextBox>(dialog, "mode-deck-path").Text = "问答::{未知}";
            Find<Button>(dialog, "save-settings").PerformClick();
            Check(!dialog.IsDisposed && WorkflowJson.Read<LanguageSettings>(settingsPath).AnkiRootDeck == null &&
                File.ReadAllText(Path.Combine(overrides, "modes.json")) == originalModeFile, "invalid mode path blocks all settings and mode writes");
            Find<TextBox>(dialog, "mode-deck-path").Text = editedPaths["question"];
            dialog.Size = dialog.MinimumSize; await Task.Delay(50);
            var path = Find<TextBox>(dialog, "mode-deck-path"); ((TabPage)page).ScrollControlIntoView(path);
            Check(path.Visible && path.Width > 200 && path.Height > 0 && ((TabPage)page).ClientRectangle.Contains(((TabPage)page).RectangleToClient(path.RectangleToScreen(path.ClientRectangle))) &&
                ((TabPage)page).DisplayRectangle.Height >= ((TabPage)page).ClientSize.Height,
                "path input remains editable in the scrollable mode page at minimum size");
            var save = Find<Button>(dialog, "save-settings");
            Check(dialog.ClientRectangle.Contains(dialog.RectangleToClient(save.RectangleToScreen(save.ClientRectangle))), "settings save action stays visible outside the mode scroll area");
            save.PerformClick(); await Task.Delay(60);
            Check(dialog.IsDisposed, "valid root and all mode paths save in one action");
        }
        var saved = WorkflowJson.Read<LanguageSettings>(settingsPath);
        Check(saved.AnkiRootDeck == "KnowledgeClipper UI Fixture" && saved.AnkiDeck == saved.AnkiRootDeck, "root persists and synchronizes the legacy deck value");
        var updated = ModeConfigurationLoader.Load(Path.Combine(projectRoot, "modes"), overrides);
        foreach (var mode in updated.Modes)
        {
            var before = original.Modes.Single(m => m.Definition.Id == mode.Definition.Id);
            Check(mode.Definition.AnkiDeckPath == editedPaths[mode.Definition.OutputKind] && mode.Prompt == before.Prompt &&
                mode.Definition.Description == before.Definition.Description && mode.Definition.FollowUpDefault == before.Definition.FollowUpDefault,
                "saved path preserves prompt, description and follow-up default for " + mode.Definition.Id);
        }
        using (var dialog = Open())
        {
            dialog.Show(main); var modes = Find<ComboBox>(dialog, "mode");
            Check(Find<ComboBox>(dialog, "deck").Text == saved.AnkiRootDeck, "reopened settings show the saved root");
            for (var index = 0; index < modes.Items.Count; index++)
            { modes.SelectedIndex = index; Check(Find<TextBox>(dialog, "mode-deck-path").Text == editedPaths[((ModeDefinition)modes.SelectedItem!).OutputKind], "reopened settings retain path " + index); }
            dialog.Close();
        }
        main.Close();
        WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot, scope = "production settings semantic controls; no API, Anki write, speech request or global selection" });
    }
}

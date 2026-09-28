using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using KnowledgeClipper.Workflow;

// Production forms on a private Windows desktop. Import callbacks are local fixtures;
// the live SDK exporter is verified separately and is never called by this UI check.
internal static class ExportDestinationChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static T Find<T>(Form form, string id) where T : Control => (T)form.Controls.Find(id, true).Single();
    private static object? Get(object target, string field) => target.GetType().GetField(field, Instance)!.GetValue(target);
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Instance)!.Invoke(target, args);

    internal static async Task RunAsync(string projectRoot, string receipt)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Run destination UI checks with tools/Invoke-IsolatedDesktopTest.ps1.");
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\export-destination-ui-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(dataRoot, "language-settings.json");
        var initial = new LanguageSettings { GlobalSelection = false, TypedInputAutoRead = false, SpeechAutoPlay = false,
            ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1",
            ModeDestinations = new() { ["unavailable-saved-mode"] = ExportDestination.Anki } };
        WorkflowJson.Save(settingsPath, initial);
        var defaults = Path.Combine(projectRoot, "modes");
        var overrides = Path.Combine(dataRoot, "modes"); Directory.CreateDirectory(overrides);
        var excerptMode = new ModeDefinition("public-excerpt", "摘录测试模式", true, "Public fixture", "image-answer.md", "excerpt-card.schema.json", "excerpt", RequiresContext: false);
        WorkflowJson.Save(Path.Combine(overrides, "excerpt-modes.json"), new ModeFile([excerptMode]));
        var imageModes = ImageModeLoader.Load(defaults).Select(mode => mode.Definition).ToArray();
        var imageMode = imageModes[0] with { Id = "public-image", Name = "图片测试模式" };
        WorkflowJson.Save(Path.Combine(overrides, "image-modes.json"), new ModeFile([..imageModes, imageMode]));
        var modes = ModeConfigurationLoader.LoadDefinitions(defaults).Modes.Concat(ExportRouting.BuiltinModes)
            .Concat(new[] { excerptMode }).Concat(imageModes).Concat(new[] { imageMode }).ToArray();
        var assembly = Assembly.Load("KnowledgeClipper");
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, new[] { Path.Combine(projectRoot, "appsettings.json") })!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Instance).Single(ctor => ctor.GetParameters().Length == 5).Invoke(new object?[5]);
        var mainType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        using var main = (Form)mainType.GetConstructor(Instance, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        Call(main, "EnableLanguageWorkflow"); main.Show(); await Task.Delay(60);
        var flow = Get(main, "languageFlow")!;
        Check((bool)Get(main, "excerptDestinationHotkeyRegistered")! && (bool)Get(main, "replayLastHotkeyRegistered")!,
            "Windows registers both requested global punctuation shortcuts");
        Call(main, "WndProc", Message.Create(main.Handle, 0x0312, new IntPtr(0x4B44), IntPtr.Zero));
        Check(((Button)Get(main, "statusButton")!).Text.Contains("还没有可重播"), "replay global message reaches the existing replay action without generating audio");
        Call(main, "UnbindExcerptDestinationHotkey");
        Check(!(bool)Get(main, "excerptDestinationHotkeyRegistered")! && !(bool)Get(main, "replayLastHotkeyRegistered")!, "global shortcuts unregister together");
        Call(main, "BindExcerptDestinationHotkey");
        Form Settings() => (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Instance).Single().Invoke([flow, dataRoot]);
        using (var form = Settings())
        {
            form.Show(main);
            var page = Find<TabPage>(form, "knowledge-clipper.language.page.destinations");
            ((TabControl)page.Parent!).SelectedTab = page;
            await Task.Delay(80);
            Check(page.Text == "导出对象", "all destination controls share a separate export settings page");
            var excerptTarget = Find<Label>(form, "knowledge-clipper.excerpt.current-target");
            var excerptChooser = Find<Button>(form, "knowledge-clipper.excerpt.choose-destination");
            var excerptHotkey = Find<TextBox>(form, "knowledge-clipper.excerpt.destination-hotkey");
            var hotkeyPage = Find<TabPage>(form, "knowledge-clipper.language.page.hotkeys");
            var replayHotkey = Find<TextBox>(form, "knowledge-clipper.language.replay-last-hotkey");
            Check(page.Contains(excerptTarget) && page.Contains(excerptChooser) && hotkeyPage.Contains(excerptHotkey) && hotkeyPage.Contains(replayHotkey) &&
                excerptTarget.Text.Contains("按日期导入") && excerptHotkey.Text == "Alt+Shift+《" && replayHotkey.Text == "Alt+Shift+/",
                "both initialized shortcuts share the dedicated settings page");
            excerptHotkey.Text = replayHotkey.Text = "Alt+F10";
            Find<Button>(form, "knowledge-clipper.language.save-settings").PerformClick();
            Check(!form.IsDisposed && WorkflowJson.Read<LanguageSettings>(settingsPath).OriginalExcerptHotkey == "Alt+Shift+《", "duplicate shortcut save preserves existing settings");
            ((TabControl)hotkeyPage.Parent!).SelectedTab = hotkeyPage; await Task.Delay(80);
            Find<Button>(form, "knowledge-clipper.hotkeys.reset").PerformClick();
            Check(excerptHotkey.Text == "Alt+Shift+《" && replayHotkey.Text == "Alt+Shift+/", "restore defaults resets both shortcuts");
            ((TabControl)page.Parent!).SelectedTab = page; await Task.Delay(80);
            page.ScrollControlIntoView(excerptChooser); await Task.Delay(50);
            Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                using var graphics = Graphics.FromImage(bitmap); var dc = graphics.GetHdc();
                try { if (!PrintWindow(form.Handle, dc, 2)) throw new Exception("Target settings render failed."); }
                finally { graphics.ReleaseHdc(dc); }
                bitmap.Save(Path.Combine(Path.GetDirectoryName(receipt)!, "destination-target-settings.png"));
            }
            ((TabControl)hotkeyPage.Parent!).SelectedTab = hotkeyPage; await Task.Delay(80);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                using var graphics = Graphics.FromImage(bitmap); var dc = graphics.GetHdc();
                try { if (!PrintWindow(form.Handle, dc, 2)) throw new Exception("Hotkey settings render failed."); }
                finally { graphics.ReleaseHdc(dc); }
                bitmap.Save(Path.Combine(Path.GetDirectoryName(receipt)!, "hotkey-settings.png"));
            }
            ((TabControl)page.Parent!).SelectedTab = page;
            excerptHotkey.Text = "Ctrl+Alt+F10";
            replayHotkey.Text = "Ctrl+Alt+F11";
            foreach (var mode in modes)
            {
                var input = Find<ComboBox>(form, "knowledge-clipper.export.destination." + mode.Id);
                Check((ExportDestination)input.SelectedItem! == (mode.OutputKind == "language" ? ExportDestination.Anki : ExportDestination.RemNote),
                    "correct initial destination for " + mode.Id);
                input.SelectedItem = mode.OutputKind == "language" ? ExportDestination.RemNote : ExportDestination.Anki;
            }
            var endpoint = Find<TextBox>(form, "knowledge-clipper.export.sdk-endpoint"); endpoint.Text = "http://example.com:43101";
            Find<Button>(form, "knowledge-clipper.language.save-settings").PerformClick();
            Check(!form.IsDisposed && WorkflowJson.Read<LanguageSettings>(settingsPath).RemNoteSdkEndpoint == initial.RemNoteSdkEndpoint,
                "remote SDK address is rejected before any settings are saved");
            endpoint.Text = "http://127.0.0.1:43102";
            form.Size = form.MinimumSize;
            await Task.Delay(60);
            var last = Find<ComboBox>(form, "knowledge-clipper.export.destination." + modes[^1].Id);
            page.ScrollControlIntoView(last);
            Check(last.Width > 150 && page.ClientRectangle.Contains(page.RectangleToClient(last.RectangleToScreen(last.ClientRectangle))) &&
                form.ClientRectangle.Contains(form.RectangleToClient(last.RectangleToScreen(last.ClientRectangle))),
                "last registered mode remains selectable in the minimum-size scrollable page");
            var save = Find<Button>(form, "knowledge-clipper.language.save-settings");
            Check(form.ClientRectangle.Contains(form.RectangleToClient(save.RectangleToScreen(save.ClientRectangle))), "save remains visible outside destination scrolling");
            Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                using var graphics = Graphics.FromImage(bitmap); var dc = graphics.GetHdc();
                try { if (!PrintWindow(form.Handle, dc, 2)) throw new Exception("Settings render failed."); }
                finally { graphics.ReleaseHdc(dc); }
                bitmap.Save(Path.Combine(Path.GetDirectoryName(receipt)!, "destination-settings.png"));
            }
            save.PerformClick(); await Task.Delay(50);
            Check(form.IsDisposed, "all mode destinations and local SDK address save together");
        }
        var saved = WorkflowJson.Read<LanguageSettings>(settingsPath);
        Check(saved.ModeDestinations["unavailable-saved-mode"] == ExportDestination.Anki, "saving visible modes preserves an unavailable mode's previous choice");
        Check(saved.RemNoteSdkEndpoint == "http://127.0.0.1:43102", "SDK endpoint persists independently from AnkiConnect");
        Check(saved.OriginalExcerptHotkey == "Ctrl+Alt+F10", "original excerpt panel hotkey persists independently from mode destinations");
        Check(saved.ReplayLastHotkey == "Ctrl+Alt+F11", "replay shortcut persists independently from mode destinations");
        using (var form = Settings())
        {
            form.Show(main);
            foreach (var mode in modes)
                Check((ExportDestination)Find<ComboBox>(form, "knowledge-clipper.export.destination." + mode.Id).SelectedItem! ==
                    (mode.OutputKind == "language" ? ExportDestination.RemNote : ExportDestination.Anki), "saved destination reopens for " + mode.Id);
            form.Close();
        }

        var store = new DraftStore(dataRoot);
        var material = new CaptureMaterial("Public first line\nPublic second line", null, "unavailable", null, "Public destination UI fixture", 0, DateTimeOffset.UtcNow);
        var translation = new ModeDefinition("translation", "翻译", true, "", "", "", "translation", RequiresContext: false);
        ClipDraft Basic(LanguageSettings settings) => new() { Original = material, Material = material, Settings = settings,
            Configuration = new("", [new(translation, "", "{}")]), ModeId = translation.Id,
            Input = new(Intent: "translation"), Route = new(translation.Id, "translation", false, false), Content = new("translation", Basic: new(material.SelectedText, "")) };
        Form Preview(ClipDraft draft, Func<Task> import) => (Form)assembly.GetType("KnowledgeClipper.LanguageDraftForm", true)!.GetConstructors(Instance).Single()
            .Invoke([draft, store, import, (Func<Task>)(() => Task.CompletedTask), (Action)(() => { })]);
        var remNoteDraft = Basic(initial); store.Save(remNoteDraft); bool confirmed = false;
        using (var form = Preview(remNoteDraft, () => { confirmed = true; return Task.CompletedTask; }))
        {
            form.Show(main);
            var source = Find<Label>(form, "knowledge-clipper.language.source").Text;
            Check(source.Contains("RemNote") && source.Contains("仅保存正面内容") && !source.Contains("Anki"), "empty-back RemNote preview describes a plain-note import");
            var confirm = Find<Button>(form, "knowledge-clipper.language.confirm");
            Check(confirm.Enabled && confirm.Text.Contains("RemNote"), "empty-back RemNote content can be confirmed");
            await (Task)Call(form, "ConfirmAsync")!;
            Check(confirmed && remNoteDraft.ConfirmedNote != null && ExportRouting.Frozen(remNoteDraft), "confirmation freezes empty-back RemNote content before dispatch");
            Check(Find<TextBox>(form, "knowledge-clipper.language.basic-back").ReadOnly, "confirmed RemNote content cannot be edited");
            remNoteDraft.Settings.ModeDestinations[translation.Id] = ExportDestination.Anki;
            Call(form, "RefreshStatus");
            Check(Find<Label>(form, "knowledge-clipper.language.source").Text.Contains("RemNote"), "confirmed RemNote preview keeps its destination after a default change");
            remNoteDraft.Settings.ModeDestinations.Remove(translation.Id);
            remNoteDraft.RemNoteResult = new("fixture-root", "fixture-document", ["fixture-first", "fixture-second"], [], JsonSerializer.SerializeToElement(new { fixture = true }));
            remNoteDraft.ImportState = ImportState.Verified; store.Save(remNoteDraft); Call(form, "RefreshStatus");
            Check(Find<Label>(form, "knowledge-clipper.language.status").Text.Contains("普通节点") &&
                !Find<Label>(form, "knowledge-clipper.language.status").Text.Contains("0 张") && !confirm.Enabled,
                "verified RemNote plain content reports nodes and disables repeat import");
            form.Close();
        }
        var ankiDraft = Basic(initial with { ModeDestinations = new() { [translation.Id] = ExportDestination.Anki } });
        using (var form = Preview(ankiDraft, () => throw new Exception("An empty Anki back must not import.")))
        {
            form.Show(main);
            Check(!Find<Button>(form, "knowledge-clipper.language.confirm").Enabled, "Anki keeps its nonempty-back restriction");
            Check(Find<Label>(form, "knowledge-clipper.language.source").Text.Contains("Anki"), "Anki selection is visible in the same preview");
            form.Close();
        }
        var oldAnki = Basic(initial); oldAnki.Content = oldAnki.Content! with { Basic = new(material.SelectedText, "A saved answer") };
        oldAnki.ConfirmedNote = AnkiNoteDraft.From(oldAnki); oldAnki.ImportState = ImportState.Verified; oldAnki.NoteId = 123; oldAnki.CardIds = [456];
        using (var form = Preview(oldAnki, () => throw new Exception("Old imports must not repeat.")))
        {
            form.Show(main);
            Check(Find<Label>(form, "knowledge-clipper.language.source").Text.Contains("Anki") && !Find<Button>(form, "knowledge-clipper.language.confirm").Enabled,
                "previously confirmed Anki result remains Anki after non-language defaults change");
            form.Close();
        }
        var languageMaterial = new CaptureMaterial("lead", "They lead the team.", "source", 5, "Public language UI fixture", 0, DateTimeOffset.UtcNow);
        var languageModes = new[] {
            new ModeDefinition("public-reading", "Reading", true, "", "", "", Language: "en", Practice: PracticeChoice.Reading),
            new ModeDefinition("public-writing", "Writing", true, "", "", "", Language: "en", Practice: PracticeChoice.Writing) };
        var parent = new ClipDraft { Original = languageMaterial, Material = languageMaterial,
            Settings = initial with { ModeDestinations = new() { [languageModes[0].Id] = ExportDestination.RemNote, [languageModes[1].Id] = ExportDestination.Anki } },
            Configuration = new("", languageModes.Select(mode => new ModeSnapshot(mode, "", "{}")).ToArray()),
            Input = new(Intent: "language"), ModeId = "language", Route = new("language", "language", false, true) };
        foreach (var child in LanguageModeCards.Ensure(parent, languageModes, store))
        { child.Content = new(child.ModeId!, new("/liːd/", "带领", null, null)); store.Save(child); }
        using (var form = Preview(parent, () => throw new Exception("Mode import callback is not requested.")))
        {
            form.Show(main);
            var modeStatus = Find<Label>(form, "knowledge-clipper.language.mode-status").Text;
            Check(modeStatus.Contains("RemNote") && modeStatus.Contains("Anki"), "independent language modes show each actual destination");
            var selector = Find<ComboBox>(form, "knowledge-clipper.language.mode-preview"); selector.SelectedIndex = 0;
            Check(Find<Label>(form, "knowledge-clipper.language.source").Text.Contains("RemNote") &&
                !Find<Label>(form, "knowledge-clipper.language.source").Text.Contains(parent.Settings.AnkiModel), "RemNote language preview omits the Anki note type");
            selector.SelectedIndex = 1;
            Check(Find<Label>(form, "knowledge-clipper.language.source").Text.Contains("Anki"), "changing the active language mode refreshes its destination");
            form.Close();
        }
        var questionMode = new ModeDefinition("question", "问答", true, "", "", "", "question", RequiresContext: false);
        var questionMaterial = material with { SelectedText = "What is a unit test?" };
        var firstAnswer = new QuestionAnswer("answered", "A check of one bounded behavior.", []);
        var conversation = new ClipDraft { Original = questionMaterial, Material = questionMaterial, Settings = initial,
            Configuration = new("", [new(questionMode, "", "{}")]), ModeId = questionMode.Id,
            Input = new(Intent: "question"), Route = new(questionMode.Id, "question", true, false),
            Content = new(questionMode.Id, Basic: new(questionMaterial.SelectedText, firstAnswer.AnswerText!), Answer: firstAnswer),
            Conversation = [new(questionMaterial.SelectedText, firstAnswer), new("Why keep it bounded?", new("answered", "It makes failures easier to locate.", []))] };
        conversation.ConfirmedNote = AnkiNoteDraft.From(conversation); conversation.ImportState = ImportState.Verified;
        conversation.NoteId = 789; conversation.CardIds = [987]; store.Save(conversation);
        using (var form = Preview(conversation, () => throw new Exception("No question import requested.")))
        {
            form.Show(main);
            Check(Find<Label>(form, "knowledge-clipper.language.source").Text.Contains("新问答默认：RemNote"),
                "question page identifies the new-question default without relabelling previous imports");
            Check(Find<Label>(form, "knowledge-clipper.language.question-card-0.destination").Text.Contains("Anki") &&
                Find<Label>(form, "knowledge-clipper.language.question-card-1.destination").Text.Contains("RemNote"),
                "old imported and new question cards each show their actual destination");
            form.Close();
        }
        var excerpts = new ExcerptStore(dataRoot); var session = ExcerptCards.Create(material, initial, excerpts);
        var original = excerpts.Cards.Find(session.Cards[0].Id)!;
        var excerptType = assembly.GetType("KnowledgeClipper.ExcerptDraftForm", true)!;
        using (var form = (Form)excerptType.GetConstructors(Instance).Single().Invoke([session, excerpts, (Func<ModeSnapshot[]>)(() => []),
            (Func<ModeSnapshot, CancellationToken, Task>)((_, _) => throw new Exception("No content API expected.")),
            (Func<string[], CancellationToken, Action, Task>)((_, _, _) => throw new Exception("No exporter expected."))]))
        {
            form.Show(main);
            Check(Find<Label>(form, "knowledge-clipper.excerpt.destination." + original.Id).Text.Contains("RemNote") &&
                Find<Label>(form, "knowledge-clipper.excerpt.destination." + original.Id).Text.Contains("仅保存正面内容"), "raw excerpt displays its RemNote plain-note destination");
            ExportRouting.Confirm(original, ExportRouting.BuildNote(original));
            original.RemNoteResult = new("fixture-root", "fixture-document", ["fixture-excerpt"], [], JsonSerializer.SerializeToElement(new { fixture = true }));
            original.ImportState = ImportState.Verified; excerpts.Cards.Save(original); Call(form, "RefreshCards");
            Check(!Find<Button>(form, "knowledge-clipper.excerpt.import." + original.Id).Enabled &&
                Find<RichTextBox>(form, "knowledge-clipper.excerpt.front." + original.Id).ReadOnly,
                "verified RemNote excerpt stays read-only and cannot import twice");
            form.Close();
        }
        Call(main, "ExitApplication");
        WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot,
            scope = "Production settings and preview controls on an isolated Windows desktop; local confirmation fixtures only, no external API, Anki or RemNote mutation." });
    }
}

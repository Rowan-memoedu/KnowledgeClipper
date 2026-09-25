using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using KnowledgeClipper.Workflow;

// Real production settings UI on the private test desktop. The voice catalog is
// an isolated local fixture; these checks perform no capture, model or import call.
internal static class FloatingSettingsChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] PageIds = ["capture", "floating", "routing", "speech", "anki", "content", "runtime", "destinations", "modes"];
    private static readonly string[] FloatingIds = ["knowledge-clipper.language.global-selection", "knowledge-clipper.floating.clipboard-image",
        "knowledge-clipper.floating.quick-language", "knowledge-clipper.floating.quick-question", "knowledge-clipper.floating.quick-excerpt"];
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static T Find<T>(Form form, string id) where T : Control => (T)form.Controls.Find(id, true).Single();
    private static object? Get(object target, string field) => target.GetType().GetField(field, Instance)!.GetValue(target);
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Instance)!.Invoke(target, args);
    private static IEnumerable<Control> Controls(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Controls(c)));

    internal static async Task RunAsync(string projectRoot, string receipt)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Run floating settings checks with tools/Invoke-IsolatedDesktopTest.ps1.");
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\floating-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(dataRoot, "language-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        var legacy = JsonSerializer.Deserialize<LanguageSettings>("{\"globalSelection\":true,\"selectionLeftExcerpt\":true}", WorkflowJson.Options)!;
        Check(legacy.GlobalSelection && legacy.SelectionLeftExcerpt && legacy.ClipboardImageDotEnabled && legacy.QuickExcerpt &&
            legacy.QuickLanguage && !legacy.QuickQuestion && !legacy.QuickScreenshot,
            "old settings retain text-dot preferences and receive only the intended quick-mode defaults");
        var optedOut = JsonSerializer.Deserialize<LanguageSettings>("{\"quickLanguage\":false,\"quickQuestion\":true,\"quickExcerpt\":false}", WorkflowJson.Options)!;
        Check(!optedOut.QuickLanguage && optedOut.QuickQuestion && !optedOut.QuickExcerpt,
            "explicit saved language quick opt-out and the other quick-mode preferences remain unchanged");
        var baseline = new LanguageSettings { GlobalSelection = false, TypedInputAutoRead = false, SpeechAutoPlay = false, QuickScreenshot = true,
            PythonPath = Path.Combine(dataRoot, "unused-python.exe"), ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1",
            ModeDestinations = new() { ["unavailable-saved-mode"] = ExportDestination.Anki } };
        WorkflowJson.Save(settingsPath, baseline);
        typeof(SpeechVoiceCatalog).GetMethod("SaveCache", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [baseline.PythonPath, new SpeechVoiceOption[] { new(baseline.Voice, "zh-CN", "Female"), new(baseline.EnglishVoice, "en-US", "Female") }]);
        var originalJson = File.ReadAllText(settingsPath);
        var assembly = Assembly.Load("KnowledgeClipper");
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(projectRoot, "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Instance).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        var mainType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        using var main = (Form)mainType.GetConstructor(Instance, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        Call(main, "EnableLanguageWorkflow"); // Keep the owner hidden; do not start real selection or clipboard hooks.
        var flow = Get(main, "languageFlow")!;
        Form Settings() => (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Instance).Single().Invoke([flow, dataRoot]);
        void Snapshot(Form form, string name)
        {
            WorkflowJson.Save(Path.Combine(Path.GetDirectoryName(receipt)!, name + ".layout.json"), new { form.DeviceDpi, form.Size,
                inputs = Controls(form).Where(control => control.Visible && control is CheckBox or Label).Select(control => new {
                    control.Name, control.Text, control.Bounds, control.MaximumSize, font = control.Font.SizeInPoints,
                    preferred = control.GetPreferredSize(new Size(control.Width, 0)),
                    wrapped = TextRenderer.MeasureText(control.Text, control.Font, new Size(Math.Max(1, control.Width - control.Font.Height), int.MaxValue), TextFormatFlags.WordBreak) }) });
            using var bitmap = new Bitmap(form.Width, form.Height);
            using var graphics = Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(form.Handle, dc, 2)) throw new Exception("Settings screenshot failed."); }
            finally { graphics.ReleaseHdc(dc); }
            bitmap.Save(Path.Combine(Path.GetDirectoryName(receipt)!, name + ".png"));
        }
        using (var form = Settings())
        {
            var boundsAfterShown = new List<Rectangle>();
            var shown = false;
            form.Shown += (_, _) => shown = true;
            form.LocationChanged += (_, _) => { if (shown) boundsAfterShown.Add(form.Bounds); };
            form.SizeChanged += (_, _) => { if (shown) boundsAfterShown.Add(form.Bounds); };
            form.Show(main); await Task.Delay(90);
            Check(boundsAfterShown.Count == 0, "opening finishes layout before first display without subsequent window movement");
            var tabs = Controls(form).OfType<TabControl>().Single();
            Check(!tabs.Multiline && tabs.TabPages.Cast<TabPage>().Select(p => p.Name).SequenceEqual(PageIds.Select(id => "knowledge-clipper.language.page." + id)),
                "all nine settings sections are unique and use a fixed single tab row");
            var floating = Find<TabPage>(form, "knowledge-clipper.language.page.floating");
            tabs.SelectedTab = floating; await Task.Delay(40);
            foreach (var id in FloatingIds)
            {
                var input = Find<CheckBox>(form, id);
                Check(floating.Contains(input) && input.Enabled, "floating section owns the editable setting " + id);
            }
            Check(FloatingIds.Select(id => Find<CheckBox>(form, id).Checked).SequenceEqual(new[] { false, true, true, false, true }),
                "language and excerpt quick modes are enabled by default and existing text-dot opt-out is retained");
            Check(Controls(floating).OfType<Label>().Any(label => label.Text.Contains("同时作用于文字和图片浮点")),
                "shared quick settings describe both text and single-image floating actions");
            Check(!Controls(form).Any(control => control.Name == "knowledge-clipper.floating.quick-screenshot") &&
                Find<Label>(form, "knowledge-clipper.floating.multiple-images").Text.Contains("始终打开多图模式"),
                "legacy screenshot quick switch is absent and 图 explicitly opens multi-image collection");
            Snapshot(form, "floating-settings-default");
            foreach (var size in new[] { form.Size, form.MinimumSize })
            {
                form.Size = size; await Task.Delay(60);
                var bounds = form.Bounds;
                var display = tabs.DisplayRectangle;
                for (var pass = 0; pass < 2; pass++)
                {
                    foreach (var id in PageIds)
                    {
                        var page = Find<TabPage>(form, "knowledge-clipper.language.page." + id);
                        tabs.SelectedTab = page; await Task.Delay(35);
                        Check(form.Bounds == bounds && tabs.DisplayRectangle == display && tabs.RowCount == 1,
                            "switching sections keeps fixed geometry at " + size.Width + "px: " + id + " pass " + pass);
                        Check(Controls(page).Any(control => control is TextBox or CheckBox or ComboBox or NumericUpDown) && !page.HorizontalScroll.Visible,
                            "section has working inputs and no horizontal clipping at " + size.Width + "px: " + id + " pass " + pass);
                        Check(Controls(page).OfType<CheckBox>().Where(check => check.MaximumSize.Width > 0).All(check => check.Height >= TextRenderer.MeasureText(check.Text, check.Font,
                            new Size(Math.Max(1, check.Width - check.Font.Height), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height),
                            "checkbox captions have sufficient wrapped height at " + size.Width + "px: " + id + " pass " + pass);
                        var save = Find<Button>(form, "knowledge-clipper.language.save-settings");
                        Check(save.Visible && form.ClientRectangle.Contains(form.RectangleToClient(save.RectangleToScreen(save.ClientRectangle))),
                            "save stays visible while browsing " + id + " at " + size.Width + "px pass " + pass);
                    }
                }
                tabs.SelectedTab = floating;
                Snapshot(form, "floating-settings-" + size.Width);
            }
            Check(File.ReadAllText(settingsPath) == originalJson, "opening, scrolling and repeated section changes do not rewrite saved settings");
            foreach (var id in FloatingIds) Find<CheckBox>(form, id).Checked = !Find<CheckBox>(form, id).Checked;
            Find<Button>(form, "knowledge-clipper.language.save-settings").PerformClick();
            Check(form.IsDisposed, "all floating toggles save through the production save action");
        }
        var saved = WorkflowJson.Read<LanguageSettings>(settingsPath);
        Check(saved.GlobalSelection && !saved.ClipboardImageDotEnabled && !saved.QuickLanguage && saved.QuickQuestion && !saved.QuickExcerpt && saved.QuickScreenshot,
            "visible floating preferences persist while the hidden legacy screenshot value is preserved");
        Check(saved.ModeDestinations["unavailable-saved-mode"] == ExportDestination.Anki && saved.Practice == baseline.Practice && saved.Voice == baseline.Voice &&
            saved.OcrDefaultRoute == baseline.OcrDefaultRoute && saved.OriginalDefaultRoute == baseline.OriginalDefaultRoute,
            "saving floating preferences preserves unrelated capture, voice, route and unavailable-mode settings");
        using (var form = Settings())
        {
            form.Show(main);
            var page = Find<TabPage>(form, "knowledge-clipper.language.page.floating");
            ((TabControl)page.Parent!).SelectedTab = page;
            Check(FloatingIds.Select(id => Find<CheckBox>(form, id).Checked).SequenceEqual(new[] { true, false, false, true, false }),
                "reopening projects all five visible saved values back into the floating section");
            Find<CheckBox>(form, "knowledge-clipper.floating.quick-excerpt").Checked = true;
            form.Close();
        }
        Check(!WorkflowJson.Read<LanguageSettings>(settingsPath).QuickExcerpt, "closing without saving does not change quick-mode preferences");
        int SettingsSubscribers() => (Get(flow, "SettingsChanged") as Delegate)?.GetInvocationList().Length ?? 0;
        var subscribers = SettingsSubscribers();
        using (var form = Settings())
        {
            form.Show(main);
            Check(SettingsSubscribers() == subscribers + 1, "settings editor subscribes once to external floating preference changes");
            Find<CheckBox>(form, "knowledge-clipper.floating.quick-excerpt").Checked = true;
            Find<NumericUpDown>(form, "knowledge-clipper.language.rate").Value = 13;
            ((ToolStripMenuItem)Get(main, "textDotItem")!).PerformClick();
            Check(!Find<CheckBox>(form, "knowledge-clipper.language.global-selection").Checked,
                "production tray text toggle immediately updates the open settings editor");
            ((ToolStripMenuItem)Get(main, "imageDotItem")!).PerformClick();
            Check(Find<CheckBox>(form, "knowledge-clipper.floating.clipboard-image").Checked,
                "production tray image toggle immediately updates the open settings editor");
            var contextBefore = Find<CheckBox>(form, "knowledge-clipper.language.context-enabled").Checked;
            var originalBefore = Find<CheckBox>(form, "knowledge-clipper.image.original").Checked;
            ((ToolStripMenuItem)Get(main, "contextItem")!).PerformClick();
            ((ToolStripMenuItem)Get(main, "originalItem")!).PerformClick();
            Check(Find<CheckBox>(form, "knowledge-clipper.language.context-enabled").Checked == !contextBefore &&
                Find<CheckBox>(form, "knowledge-clipper.image.original").Checked == !originalBefore,
                "production context and original-image tray toggles update only their settings fields");
            Check(Find<CheckBox>(form, "knowledge-clipper.floating.quick-excerpt").Checked &&
                Find<NumericUpDown>(form, "knowledge-clipper.language.rate").Value == 13,
                "external tray changes preserve pending quick-mode and voice edits");
            Find<Button>(form, "knowledge-clipper.language.save-settings").PerformClick();
            var afterTray = WorkflowJson.Read<LanguageSettings>(settingsPath);
            Check(form.IsDisposed && !afterTray.GlobalSelection && afterTray.ClipboardImageDotEnabled && afterTray.QuickExcerpt && afterTray.RatePercent == 13,
                "saving pending edits retains both latest production tray settings");
            Check(afterTray.ContextEnabled == !contextBefore && afterTray.OriginalImageMode == !originalBefore,
                "saving pending edits retains the latest context and original-image tray settings");
        }
        Check(SettingsSubscribers() == subscribers, "closing the settings editor removes its external-settings subscription");
        using (var form = Settings())
        {
            form.Show(main);
            Find<CheckBox>(form, "knowledge-clipper.language.global-selection").Checked = true;
            var contextEdit = !Find<CheckBox>(form, "knowledge-clipper.language.context-enabled").Checked;
            var originalEdit = !Find<CheckBox>(form, "knowledge-clipper.image.original").Checked;
            Find<CheckBox>(form, "knowledge-clipper.language.context-enabled").Checked = contextEdit;
            Find<CheckBox>(form, "knowledge-clipper.image.original").Checked = originalEdit;
            Find<NumericUpDown>(form, "knowledge-clipper.language.rate").Value = 14;
            ((ToolStripMenuItem)Get(main, "imageDotItem")!).PerformClick();
            Check(Find<CheckBox>(form, "knowledge-clipper.language.global-selection").Checked &&
                !Find<CheckBox>(form, "knowledge-clipper.floating.clipboard-image").Checked,
                "an image-only tray change preserves an unsaved text-dot edit");
            Check(Find<CheckBox>(form, "knowledge-clipper.language.context-enabled").Checked == contextEdit &&
                Find<CheckBox>(form, "knowledge-clipper.image.original").Checked == originalEdit,
                "an image-only tray change preserves unsaved context and original-image edits");
            Find<Button>(form, "knowledge-clipper.language.save-settings").PerformClick();
            var merged = WorkflowJson.Read<LanguageSettings>(settingsPath);
            Check(merged.GlobalSelection && !merged.ClipboardImageDotEnabled && merged.RatePercent == 14,
                "local text-dot edit and external image-dot change save together");
            Check(merged.ContextEnabled == contextEdit && merged.OriginalImageMode == originalEdit,
                "pending context and original-image edits save after an unrelated tray change");
        }
        var disposedWithoutShowing = Settings();
        Check(SettingsSubscribers() == subscribers + 1, "an unshown settings editor also owns exactly one subscription");
        disposedWithoutShowing.Dispose();
        Check(SettingsSubscribers() == subscribers, "direct disposal also removes the external-settings subscription");
        Call(main, "OpenSettings");
        var single = (Form)Get(main, "languageSettingsForm")!;
        var singleBounds = single.Bounds;
        Call(main, "OpenSettings"); await Task.Delay(40);
        Check(ReferenceEquals(single, Get(main, "languageSettingsForm")) && single.Bounds == singleBounds &&
            Application.OpenForms.Cast<Form>().Count(f => f.Name == "knowledge-clipper.language.settings") == 1,
            "repeated production settings entry reuses exactly one window without repositioning");
        single.Close();
        Call(main, "ExitApplication");
        WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot,
            scope = "Production settings on an isolated Windows desktop; isolated voice catalog; no user settings, capture, model, OCR, TTS or external import calls." });
    }
}

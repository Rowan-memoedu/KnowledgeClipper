using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using KnowledgeClipper.Workflow;

internal static class ExcerptUiChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static void Render(Form form, string path)
    {
        form.Refresh();
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(form.Handle, dc, 2)) throw new InvalidOperationException("Native window rendering failed."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(path);
    }
    private static object? Get(object target, string field) => target.GetType().GetField(field, Flags)!.GetValue(target);
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static T Find<T>(Control parent, string name) where T : Control => (T)parent.Controls.Find(name, true).Single();
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", Flags)!.Invoke(button, [EventArgs.Empty]);
    internal static async Task RunAsync(string project, string receipt)
    {
        var checks = new List<string>();
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks.Add(text); Console.WriteLine("PASS " + text); }
        var data = Path.Combine(@"D:\Caches\KnowledgeClipper\excerpt-ui-tests", Guid.NewGuid().ToString("N"));
        var assembly = Assembly.Load("KnowledgeClipper");
        var store = new ExcerptStore(data);
        var original = "Learning connects ideas.\n每次练习都保留原文。";
        var session = ExcerptCards.Create(new(original, null, "unavailable", null, "Public UI fixture", 0, DateTimeOffset.UtcNow), new(), store);
        var promptCalls = 0; var importCalls = new List<string[]>();
        ModeSnapshot[] Modes() => [];
        Task Generate(ModeSnapshot mode, CancellationToken ct) { promptCalls++; return Task.CompletedTask; }
        Task Import(string[] ids, CancellationToken ct, Action changed) { importCalls.Add(ids); return Task.CompletedTask; }
        using var form = (Form)assembly.GetType("KnowledgeClipper.ExcerptDraftForm", true)!.GetConstructors(Flags).Single()
            .Invoke([session, store, (Func<ModeSnapshot[]>)Modes, (Func<ModeSnapshot, CancellationToken, Task>)Generate,
                (Func<string[], CancellationToken, Action, Task>)Import]);
        form.Show(); await Task.Delay(80); form.PerformLayout();
        var raw = session.Cards[0].Id;
        Check(session.Cards.Count == 1 && Find<RichTextBox>(form, "knowledge-clipper.excerpt.front." + raw).Text == original &&
            Find<RichTextBox>(form, "knowledge-clipper.excerpt.back." + raw).Text == "", "initial preview contains only the raw front and an empty back");
        Render(form, Path.Combine(Path.GetDirectoryName(receipt)!, "excerpt-initial.png"));
        var modes = Find<CheckedListBox>(form, "knowledge-clipper.excerpt.modes"); modes.SetItemChecked(0, true);
        var front = Find<RichTextBox>(form, "knowledge-clipper.excerpt.manual-front");
        var back = Find<RichTextBox>(form, "knowledge-clipper.excerpt.manual-back");
        Check(front.Text == original && back.Text == "", "manual editor starts from an independent original copy");
        front.Text = "Alpha connects beta."; back.Text = "A relationship.";
        Click(Find<Button>(form, "knowledge-clipper.excerpt.add-manual"));
        Check(session.Cards.Count == 2 && store.Cards.Find(session.Cards[1].Id)!.Excerpt!.Back == "A relationship." &&
            store.Cards.Find(raw)!.Excerpt!.Front == original, "manual add uses both editors without changing the raw card");
        var cloze = Find<CheckBox>(form, "knowledge-clipper.excerpt.cloze"); cloze.Checked = true;
        front.Select(0, 5); Call(form, "CaptureCloze"); var first = session.Cards[^1].Id;
        front.Select(15, 4); Call(form, "CaptureCloze"); var second = session.Cards[^1].Id;
        Check(session.Cards.Count == 4 && store.Cards.Find(first)!.Excerpt!.Front == "{{c1::Alpha}} connects beta." &&
            store.Cards.Find(second)!.Excerpt!.Front == "Alpha connects {{c1::beta}}.", "two selections create two independent Anki syntax previews");
        Find<CheckBox>(form, "knowledge-clipper.excerpt.select." + first).Checked = true;
        Find<CheckBox>(form, "knowledge-clipper.excerpt.select." + second).Checked = true;
        Click(Find<Button>(form, "knowledge-clipper.excerpt.merge"));
        var merged = session.Cards[^1].Id;
        Check(session.Cards.Count(c => c.SupersededBy == null) == 3 &&
            Find<RichTextBox>(form, "knowledge-clipper.excerpt.front." + merged).Text == "{{c1::Alpha}} connects {{c1::beta}}.", "checked card merge replaces only the two pending previews");
        Check(Find<RichTextBox>(form, "knowledge-clipper.excerpt.answer." + merged).Text == "Alpha connects beta.\nA relationship.", "cloze answer preview restores the source and includes the extra back");
        Click(Find<Button>(form, "knowledge-clipper.excerpt.import." + raw));
        Click(Find<Button>(form, "knowledge-clipper.excerpt.import-all")); await Task.Yield();
        Check(importCalls.Count == 2 && importCalls[0].SequenceEqual(new[] { raw }) && importCalls[1].Length == 3 &&
            !importCalls[1].Contains(first) && !importCalls[1].Contains(second), "individual and batch UI import send the correct independent active card IDs");
        Check(promptCalls == 0, "raw, edit, cloze, merge and import controls never invoke prompts");
        form.PerformLayout(); await Task.Delay(40);
        Render(form, Path.Combine(Path.GetDirectoryName(receipt)!, "excerpt-manual.png"));
        Click(Find<Button>(form, "knowledge-clipper.excerpt.undo." + merged));
        Check(session.Cards.Single(c => c.Id == first).SupersededBy == null && session.Cards.Single(c => c.Id == second).SupersededBy == null, "undo control restores the pre-merge cards");
        Call(form, "SaveManual");
        Check(store.Find(session.Id)!.ManualFront == front.Text && store.Find(session.Id)!.ManualBack == back.Text, "manual workspace survives reopen");

        WorkflowJson.Save(Path.Combine(data, "language-settings.json"), new LanguageSettings { GlobalSelection = false, LanguageAutoRead = true });
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(project, "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, data);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Flags).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        var mainType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        using var main = (Form)mainType.GetConstructor(Flags, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        Call(main, "EnableLanguageWorkflow");
        Check(((Button)Get(main, "readButton")!).Text == "语言" && ((Button)Get(main, "clipButton")!).Text == "摘录", "main buttons label the two independent features");
        Check(main.Controls.Find("knowledge-clipper.desktop.input-feature", true).Length == 0 &&
            ((TextBox)Get(main, "inlineQuestionInput")!).PlaceholderText.Contains("否则翻译"), "typed input removes the selector and describes its punctuation default");
        var flow = Get(main, "languageFlow")!;
        using var settings = (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Flags).Single().Invoke([flow, data]);
        Find<CheckBox>(settings, "knowledge-clipper.language.auto-read").Checked = false;
        Find<TextBox>(settings, "knowledge-clipper.excerpt.deck-path").Text = "阅读摘录::手动";
        Find<TextBox>(settings, "knowledge-clipper.excerpt.modes-directory").Text = Path.Combine(project, "modes");
        var typedRoute = Find<ComboBox>(settings, "knowledge-clipper.routing.typed");
        typedRoute.SelectedItem = typedRoute.Items.Cast<MaterialRoute>().Single(r => r.Id == "ask");
        Find<TextBox>(settings, "knowledge-clipper.image.model").Text = "image-model-fixture";
        Find<TextBox>(settings, "knowledge-clipper.image.ocr-language").Text = "en";
        Find<CheckBox>(settings, "knowledge-clipper.image.ocr-table").Checked = false;
        Find<CheckBox>(settings, "knowledge-clipper.image.ocr-formula").Checked = false;
        Click(Find<Button>(settings, "knowledge-clipper.language.save-settings"));
        var saved = WorkflowJson.Read<LanguageSettings>(Path.Combine(data, "language-settings.json"));
        Check(!saved.LanguageAutoRead && saved.ExcerptDeckPath == "阅读摘录::手动" && saved.ExcerptModesDirectory == Path.Combine(project, "modes"), "new language and excerpt settings save through the production UI");
        Check(((LanguageSettings)Get(Get(main, "excerptFlow")!, "settings")!).ExcerptDeckPath == saved.ExcerptDeckPath, "settings apply to the independent excerpt flow immediately");
        Check(saved.TypedDefaultRoute == "ask" && saved.ImageModel == "image-model-fixture" && saved.MineruLanguage == "en" && !saved.MineruTable && !saved.MineruFormula &&
            ((TextBox)Get(main, "inlineQuestionInput")!).PlaceholderText.Contains("选择处理路径"), "image settings persist and Ask changes the live input placeholder");
        var dot = (Form)Get(main, "selectionDot")!; dot.Show();
        var read = Find<Button>(dot, "knowledge-clipper.language.selection-dot.read");
        var excerpt = Find<Button>(dot, "knowledge-clipper.language.selection-dot.excerpt");
        var question = Find<Button>(dot, "knowledge-clipper.language.selection-dot.question");
        Check(read.Text == "读" && excerpt.Text == "摘" && question.Text == "问" && !read.Bounds.IntersectsWith(excerpt.Bounds) && !excerpt.Bounds.IntersectsWith(question.Bounds), "floating dot keeps three separate compact actions");
        using (var picture = new Bitmap(dot.Width, dot.Height)) { dot.DrawToBitmap(picture, dot.ClientRectangle); picture.Save(Path.Combine(Path.GetDirectoryName(receipt)!, "excerpt-dot.png")); }
        ((IDisposable)Get(main, "imageFlow")!).Dispose(); ((IDisposable)flow).Dispose(); ((IDisposable)Get(main, "excerptFlow")!).Dispose(); dot.Dispose(); ((IDisposable)Get(main, "selectionMonitor")!).Dispose();
        form.Close();
        WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot = data, scope = "production WinForms components on a private Windows test desktop; semantic handlers, no desktop switch, mouse/keyboard simulation, live prompts or Anki writes" });
    }
}

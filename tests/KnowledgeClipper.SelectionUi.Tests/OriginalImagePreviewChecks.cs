using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KnowledgeClipper.Workflow;

internal static class OriginalImagePreviewChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static IEnumerable<Control> Controls(Control parent) => parent.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(Controls(control)));
    private static T Find<T>(Control parent, string suffix) where T : Control =>
        (T)parent.Controls.Find("knowledge-clipper.language." + suffix, true).Single();
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, All)!.Invoke(target, args);
    private static void Render(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(form.Handle, dc, 2)) throw new InvalidOperationException("Native preview rendering failed."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(path);
    }

    internal static async Task RunAsync(string project, string receipt)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Run original-image previews on an isolated Windows desktop.");
        var output = Path.GetDirectoryName(receipt)!;
        Directory.CreateDirectory(output);
        var data = Path.Combine(@"D:\Caches\KnowledgeClipper\original-image-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var checks = new List<string>(); string? failure = null;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var desktop = Assembly.Load("KnowledgeClipper");
        try
        {
            var path = Path.Combine(data, "public-geometric-fixture.png");
            using (var bitmap = new Bitmap(600, 180))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.CornflowerBlue);
                graphics.FillRectangle(Brushes.Gold, 250, 30, 100, 120);
                bitmap.Save(path, ImageFormat.Png);
            }
            var image = new ImageAsset(path, ImageAsset.Hash(File.ReadAllBytes(path)), "question", 1);
            var configuration = ModeConfigurationLoader.Load(Path.Combine(project, "modes"));
            var definition = configuration.Modes.Single(mode => mode.Definition.Language == "en" && mode.Definition.Practice == PracticeChoice.Reading).Definition;
            var material = new CaptureMaterial("resilient", "The resilient tree survived.", "source", 4, "Public geometric UI fixture", 0, DateTimeOffset.UtcNow);
            var fields = new LanguageFields("/rɪˈzɪliənt/", null, null, null) { Format = PublishedLanguage.Format, Status = "ready",
                ExplanationEn = "able to recover after difficulties", SentenceTranslationZh = "这棵坚韧的树存活了下来。" };
            var store = new DraftStore(data);
            ClipDraft Language(bool withImage, bool ready = true) => new()
            {
                Original = material with { FrontImage = withImage ? image : null }, Material = material with { FrontImage = withImage ? image : null },
                Configuration = configuration, ModeId = definition.Id, Route = new(definition.Id, "language", false, true),
                Practice = withImage ? PracticeChoice.Reading : PracticeChoice.Both,
                Settings = new() { AnkiRootDeck = "KnowledgeClipper UI fixture", ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1" },
                Content = ready ? new(definition.Id, fields) : null
            };
            var formType = desktop.GetType("KnowledgeClipper.LanguageDraftForm", true)!;
            Form Form(ClipDraft draft) => (Form)formType.GetConstructors(All).Single().Invoke([draft, store,
                (Func<Task>)(() => throw new InvalidOperationException("No real import is permitted in a preview test.")),
                (Func<Task>)(() => throw new InvalidOperationException("No speech is permitted in a preview test.")), (Action)(() => { })]);
            void CheckImage(PictureBox picture, string name) => Check(picture.Image is Bitmap bitmap && bitmap.Size == new Size(600, 180) &&
                bitmap.GetPixel(20, 20).ToArgb() == Color.CornflowerBlue.ToArgb() && bitmap.GetPixel(300, 90).ToArgb() == Color.Gold.ToArgb() &&
                picture.SizeMode == PictureBoxSizeMode.Zoom, name);

            var pending = Language(true, false);
            using (var form = Form(pending))
            {
                form.Show(); await Task.Delay(50);
                var practice = Find<ComboBox>(form, "practice"); var tabs = Find<TabControl>(form, "faces");
                Check(practice.SelectedIndex == (int)PracticeChoice.Reading && !practice.Enabled && tabs.TabPages.Count == 1,
                    "original-image language is fixed to reading while content is still pending");
                CheckImage(Controls(form).OfType<PictureBox>().Single(), "pending preview displays the verified original image pixels");
                practice.SelectedIndex = (int)PracticeChoice.Writing;
                Check(practice.SelectedIndex == (int)PracticeChoice.Reading && pending.Practice == PracticeChoice.Reading,
                    "programmatic practice changes cannot select writing for an image");
                pending.Content = new(definition.Id, fields); Call(form, "ContentReady");
                var reading = Find<RichTextBox>(form, "reading-preview");
                var note = AnkiNoteDraft.From(pending);
                var back = WebUtility.HtmlDecode(note.Fields["Back"].Replace("<br>", "\n"));
                Check(reading.Text == "反面\n" + back && !reading.Text.Contains("正面"),
                    "image-language text preview exactly matches the generated card back");
                Check(reading.Text.Contains(material.ContextText!) && reading.Text.Contains(fields.Pronunciation!) &&
                    reading.Text.Contains(fields.ExplanationEn!) && reading.Text.Contains(fields.SentenceTranslationZh!),
                    "sentence pronunciation explanation and translation remain on the back");
                Check(tabs.TabPages.Count == 1 && tabs.TabPages[0].Text.StartsWith("理解与朗读") && !practice.Enabled,
                    "ready image language keeps only the reading preview");
                var selection = (Task<string>)Call(form, "ChooseModeAsync", "请选择模式")!;
                var combo = Find<ComboBox>(form, "mode-choice");
                var allowed = configuration.Modes.Where(mode => mode.Definition.Practice is not (PracticeChoice.Writing or PracticeChoice.Both)).ToArray();
                Check(combo.Items.Cast<string>().SequenceEqual(allowed.Select(mode => mode.Definition.Name)),
                    "image mode picker excludes writing and both-practice modes");
                Find<Button>(form, "choose-mode").PerformClick();
                Check(await selection == allowed[0].Definition.Id, "filtered mode picker returns its displayed mode identity");
                var scroll = Find<Panel>(form, "content-scroll");
                var picture = Controls(form).OfType<PictureBox>().Single();
                scroll.ScrollControlIntoView(picture); await Task.Delay(40);
                WorkflowJson.Save(Path.Combine(output, "original-image-language.layout.json"), new { form.DeviceDpi,
                    scroll.AutoScrollPosition, viewport = scroll.RectangleToScreen(scroll.ClientRectangle),
                    controls = Controls(form).Select(control => new { control.Name, type = control.GetType().Name, control.Visible,
                        control.Bounds, control.MinimumSize, screen = control.RectangleToScreen(control.ClientRectangle) }) });
                Render(form, Path.Combine(output, "original-image-language.png"));
                Check(Rectangle.Intersect(scroll.RectangleToScreen(scroll.ClientRectangle), picture.RectangleToScreen(picture.ClientRectangle)).Height > 50 &&
                    reading.Height >= 150, "scrolling reaches the real image and preserves a readable back panel");
                scroll.ScrollControlIntoView(reading); await Task.Delay(30);
                Check(Rectangle.Intersect(scroll.RectangleToScreen(scroll.ClientRectangle), reading.RectangleToScreen(reading.ClientRectangle)).Height >= 150,
                    "scrolling also reaches the original-image card back at actual DPI");
                Render(form, Path.Combine(output, "original-image-language-back.png"));
                form.Size = form.MinimumSize; await Task.Delay(30);
                scroll.ScrollControlIntoView(reading); await Task.Delay(30);
                Check(Rectangle.Intersect(scroll.RectangleToScreen(scroll.ClientRectangle), reading.RectangleToScreen(reading.ClientRectangle)).Height >= 150 &&
                    reading.Right <= reading.Parent!.ClientSize.Width && picture.Right <= picture.Parent!.ClientSize.Width,
                    "minimum preview size keeps the back reachable and image controls within their columns");
            }

            var text = Language(false);
            using (var form = Form(text))
            {
                form.Show(); await Task.Delay(40);
                var practice = Find<ComboBox>(form, "practice");
                Check(practice.Enabled && practice.Items.Count == 3 && Find<TabControl>(form, "faces").TabPages.Count == 2 && !Controls(form).OfType<PictureBox>().Any(),
                    "plain-text language retains both previews and all practice options");
                practice.SelectedIndex = (int)PracticeChoice.Writing;
                Check(text.Practice == PracticeChoice.Writing, "plain-text practice changes still update the draft");
                var selection = (Task<string>)Call(form, "ChooseModeAsync", "请选择模式")!;
                Check(Find<ComboBox>(form, "mode-choice").Items.Count == configuration.Modes.Length,
                    "plain-text mode picker keeps all configured choices");
                Find<Button>(form, "choose-mode").PerformClick(); await selection;
            }

            var questionMaterial = material with { FrontImage = image, VisualQuestion = true };
            var question = new ClipDraft { Original = questionMaterial, Material = questionMaterial, Configuration = configuration,
                ModeId = "question", Route = new("question", "question", false, false), Input = new(Intent: "question", ExplicitQuestion: "图中有什么形状？"),
                Practice = PracticeChoice.Reading, Settings = new() { AnkiRootDeck = "KnowledgeClipper UI fixture" },
                Content = new("question", Basic: new("图中有什么形状？", "蓝底上有一个黄色矩形。"), Answer: new("answered", "蓝底上有一个黄色矩形。", [])) };
            question.Conversation.Add(new("图中有什么形状？", new("answered", "蓝底上有一个黄色矩形。", [])));
            question.Conversation.Add(new("矩形位于什么位置？", new("answered", "位于图片中间。", []))); store.Save(question);
            using (var form = Form(question))
            {
                form.Show(); await Task.Delay(40);
                var panel = Find<Control>(form, "follow-up");
                Check(Controls(panel).OfType<PictureBox>().Count() == 2, "each question round previews the same original front image");
                foreach (var picture in Controls(panel).OfType<PictureBox>()) CheckImage(picture, "question round preserves original image pixels " + picture.Parent!.Name);
                Check(Find<TextBox>(form, "question-card-0.question").Text == question.Conversation[0].Question &&
                    Find<TextBox>(form, "question-card-1.question").Text == question.Conversation[1].Question,
                    "rounds show their distinct question text beside the fixed image front");
                Check(Controls(panel).OfType<Label>().Count(label => label.Text == "本轮问题 · 随回答放入反面") == 2,
                    "image question labels describe the actual back-side placement");
                Find<TextBox>(form, "question-card-0.answer").Text = "黄色矩形位于蓝色背景上。";
                Check(question.EditedAnswers[0] == "黄色矩形位于蓝色背景上。" && Find<TextBox>(form, "question-card-1.answer").Text == "位于图片中间。",
                    "editing one answer preserves the other round and its fixed front image");
                Find<Panel>(form, "content-scroll").ScrollControlIntoView(panel); await Task.Delay(30);
                Render(form, Path.Combine(output, "original-image-question.png"));
                var importer = new FixtureImporter();
                await QuestionCards.ImportAsync(question, [0], store, importer, () => Call(form, "RefreshConversation"));
                Check(importer.Calls == 1 && Find<TextBox>(form, "question-card-0.question").Text == question.Conversation[0].Question &&
                    Find<TextBox>(form, "question-card-0.answer").Text == "黄色矩形位于蓝色背景上。" && Find<TextBox>(form, "question-card-0.answer").ReadOnly,
                    "frozen image card retains its question and semantic answer without duplicate question text");
                Check(Controls(panel).OfType<PictureBox>().Count() == 2 && Controls(panel).OfType<PictureBox>().All(picture => picture.Image != null),
                    "refreshing after fixture import preserves both original image previews");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            WorkflowJson.Save(receipt, new { passed = failure == null, checks, error = failure, desktop = desktop.Location,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(desktop.Location))).ToLowerInvariant(), data,
                scope = "Production original-image language and question previews on a private Windows desktop; public generated geometry, local fixture importer, no clipboard, API, speech or real export." });
        }
    }

    private sealed class FixtureImporter : ICardImporter
    {
        public ExportDestination? FixedDestination => ExportDestination.Anki;
        internal int Calls { get; private set; }
        public Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
        {
            Calls++; draft.ImportState = ImportState.Verified; draft.NoteId = 101; draft.CardIds = [201]; store.Save(draft);
            return Task.CompletedTask;
        }
    }
}

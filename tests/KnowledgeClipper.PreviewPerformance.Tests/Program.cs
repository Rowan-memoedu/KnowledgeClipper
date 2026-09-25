using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using KnowledgeClipper.Workflow;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly List<object> Measurements = [];
    private static readonly List<string> Checks = [];
    private static Assembly desktop = null!;
    private static string receiptPath = "";
    private static object? Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Instance)!.Invoke(value, args);
    private static Form Construct(string type, params object[] args) => (Form)desktop.GetType("KnowledgeClipper." + type, true)!.GetConstructors(Instance).Single().Invoke(args);
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Checks.Add(name); }
    private static double Measure(Action action) { var clock = Stopwatch.StartNew(); action(); return clock.Elapsed.TotalMilliseconds; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Use tools/Invoke-IsolatedDesktopTest.ps1; this probe must never display on the user's desktop.");
        if (args.Length is not (3 or 4)) throw new ArgumentException("PreviewPerformance.Tests <project> <fixture-directory> <receipt> [--history-only]");
        receiptPath = args[2];
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        desktop = Assembly.Load("KnowledgeClipper");
        string? error = null;
        try { Run(args[0], args[1], args.Length == 4 && args[3].StartsWith("--history"), args.Length == 4 && args[3] == "--history-visible"); }
        catch (Exception ex) { error = ex.ToString(); Console.Error.WriteLine(error); }
        finally
        {
            WorkflowJson.Save(args[2], new { passed = error == null, utc = DateTimeOffset.UtcNow, error, checks = Checks,
                assemblies = new[] { desktop, typeof(ClipDraft).Assembly }.Select(a => new { path = a.Location, sha256 = Hash(a.Location) }),
                fixtureDirectory = args[1], measurements = Measurements,
                scope = "Public synthetic fixtures only, isolated WinForms desktop, no network/export/clipboard. Measurements include synchronous paint/layout via DoEvents; first iteration includes cold JIT. History test uses real form with constant status delegates, isolating rendering from history disk expansion and activation callbacks." });
        }
        return error == null ? 0 : 1;
    }

    private static void PersistProgress() => WorkflowJson.Save(receiptPath + ".progress.json", new { utc = DateTimeOffset.UtcNow, completedMeasurements = Measurements, checks = Checks });

    private static void Run(string project, string data, bool historyOnly, bool historyVisible)
    {
        Directory.CreateDirectory(data);
        var store = new DraftStore(data); var excerpts = new ExcerptStore(data);
        var configuration = ModeConfigurationLoader.Load(Path.Combine(project, "modes"));
        var reading = configuration.Modes.Single(mode => mode.Definition.Language == "en" && mode.Definition.Practice == PracticeChoice.Reading).Definition;
        var writing = configuration.Modes.Single(mode => mode.Definition.Language == "en" && mode.Definition.Practice == PracticeChoice.Writing).Definition;
        var material = new CaptureMaterial("resilient", "The resilient tree survived.", "source", 4, "Public preview performance fixture", 0, DateTimeOffset.Parse("2026-09-21T00:00:00Z"));
        var settings = new LanguageSettings { ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1",
            RemNoteSdkEndpoint = "http://127.0.0.1:1", AnkiRootDeck = "KnowledgeClipper performance fixture" };
        var fields = new LanguageFields("/rɪˈzɪliənt/", "有韧性的", null, null) { Format = PublishedLanguage.Format, Status = "ready",
            ExplanationEn = "able to recover after difficulties", SentenceTranslationZh = "这棵坚韧的树存活了下来。" };
        var imagePath = Path.Combine(data, "public-geometric-fixture.png");
        using (var bitmap = new Bitmap(1560, 1127))
        using (var graphics = Graphics.FromImage(bitmap))
        { graphics.Clear(Color.CornflowerBlue); graphics.FillRectangle(Brushes.Gold, 200, 200, 900, 500); bitmap.Save(imagePath, ImageFormat.Png); }
        var image = new ImageAsset(imagePath, ImageAsset.Hash(File.ReadAllBytes(imagePath)), "question", 1);
        ClipDraft Language(bool withImage = false) => new()
        {
            Original = material with { FrontImage = withImage ? image : null }, Material = material with { FrontImage = withImage ? image : null },
            Settings = settings, Configuration = configuration, ModeId = reading.Id, Route = new(reading.Id, "language", false, true),
            Practice = withImage ? PracticeChoice.Reading : PracticeChoice.Both, Content = new(reading.Id, fields)
        };
        ClipDraft Question(bool withImage)
        {
            var original = material with { FrontImage = withImage ? image : null, VisualQuestion = withImage };
            var answer = string.Concat(Enumerable.Repeat("This is a public explanation of a simple geometric example. ", 10));
            var draft = new ClipDraft { Original = original, Material = original, Settings = settings, Configuration = configuration,
                ModeId = "question", Route = new("question", "question", false, false), Input = new(Intent: "question", ExplicitQuestion: "Describe the public example."),
                Practice = PracticeChoice.Reading, Content = new("question", Basic: new("Describe the public example.", answer), Answer: new("answered", answer, [])) };
            for (var i = 0; i < 3; i++) draft.Conversation.Add(new("Public question " + (i + 1), new("answered", answer, [])));
            store.Save(draft); return draft;
        }
        var plain = Language(); store.Save(plain);
        var originalImage = Language(true); store.Save(originalImage);
        var multi = Language(); multi.Content = null;
        var children = LanguageModeCards.Ensure(multi, [reading, writing], store);
        foreach (var child in children) { child.Content = new(child.ModeId!, fields); store.Save(child); }
        var question = Question(false); var imageQuestion = Question(true);
        var excerpt = ExcerptCards.Create(material with { SelectedText = string.Concat(Enumerable.Repeat("Public excerpt sentence. ", 24)), ContextText = null, TargetStart = null, ContextOrigin = "unavailable" }, settings, excerpts);
        ExcerptCards.Add(excerpt, excerpts, new("Public second card", "Public second answer"), "手动");
        var imageExcerpt = new ExcerptSession { Original = material, Settings = settings };
        ExcerptCards.Add(imageExcerpt, excerpts, new("", "Public image answer") { FrontImages = [image] }, "原图");
        ExcerptCards.Add(imageExcerpt, excerpts, new("", "Public second image answer") { FrontImages = [image] }, "原图");
        Form LanguageForm(ClipDraft draft) => Construct("LanguageDraftForm", draft, store,
            (Func<Task>)(() => throw new InvalidOperationException("Unexpected export")),
            (Func<Task>)(() => throw new InvalidOperationException("Unexpected speech")), (Action)(() => { }));
        Form ExcerptForm(ExcerptSession session) => Construct("ExcerptDraftForm", session, excerpts, (Func<ModeSnapshot[]>)(() => configuration.Modes),
            (Func<ModeSnapshot, CancellationToken, Task>)((_, _) => throw new InvalidOperationException("Unexpected model")),
            (Func<string[], CancellationToken, Action, Task>)((_, _, _) => throw new InvalidOperationException("Unexpected export")));
        var cases = new[] {
            (Name: "language", Make: (Func<Form>)(() => LanguageForm(plain)), Refresh: "RefreshStatus", Pictures: 0),
            (Name: "language-two-modes", Make: (Func<Form>)(() => LanguageForm(multi)), Refresh: "RefreshStatus", Pictures: 0),
            (Name: "language-image", Make: (Func<Form>)(() => LanguageForm(originalImage)), Refresh: "RefreshStatus", Pictures: 1),
            (Name: "question-three-rounds", Make: (Func<Form>)(() => LanguageForm(question)), Refresh: "RefreshConversation", Pictures: 0),
            (Name: "question-image-three-rounds", Make: (Func<Form>)(() => LanguageForm(imageQuestion)), Refresh: "RefreshConversation", Pictures: 4),
            (Name: "excerpt-two-cards", Make: (Func<Form>)(() => ExcerptForm(excerpt)), Refresh: "RefreshCards", Pictures: 0),
            (Name: "excerpt-image-two-cards", Make: (Func<Form>)(() => ExcerptForm(imageExcerpt)), Refresh: "RefreshCards", Pictures: 2)
        };
        foreach (var item in historyOnly ? cases.Take(0) : cases)
        {
            for (var iteration = 0; iteration < 4; iteration++)
            {
                Form form = null!;
                var constructorMs = Measure(() => form = item.Make());
                using (form)
                {
                    var showMs = Measure(() => { form.Show(); Application.DoEvents(); form.Update(); });
                    Check(form.Visible, item.Name + " visible " + iteration);
                    Check(Descendants(form).OfType<PictureBox>().Count() == item.Pictures, item.Name + " picture count " + iteration);
                    var refreshMs = Measure(() => { Call(form, item.Refresh); Application.DoEvents(); form.Update(); });
                    var contentReadyMs = item.Name.StartsWith("excerpt") ? 0 : Measure(() => { Call(form, "ContentReady"); Application.DoEvents(); });
                    var controls = Descendants(form).Count();
                    var closeMs = Measure(() => { form.Close(); Application.DoEvents(); });
                    Measurements.Add(new { scenario = item.Name, iteration, constructorMs, showMs, refreshMs, contentReadyMs, closeMs, controls });
                    PersistProgress();
                    Console.WriteLine($"{item.Name} {iteration}: create {constructorMs:F1} ms; show {showMs:F1} ms; refresh {refreshMs:F1} ms");
                }
            }
        }
        var records = Enumerable.Range(0, 160).Select(_ => new HistoryRecord(Draft: Language()))
            .Concat(Enumerable.Range(0, 60).Select(_ => new HistoryRecord(Excerpt: new ExcerptSession { Original = material, Settings = settings }))).ToArray();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            using var history = Construct("RecordHistoryForm", (Action<HistoryRecord>)(_ => { }), (Action<HistoryRecord>)(_ => { }),
                (Action)(() => { }), (Action)(() => { }), (Func<HistoryRecord, string>)(_ => "公开样本 · 预览已就绪 · 未导入"),
                (Func<HistoryRecord, string>)(_ => "继续制卡"));
            var showCallMs = historyVisible ? Measure(() => history.Show()) : 0;
            var showPumpMs = historyVisible ? Measure(Application.DoEvents) : 0;
            var showPaintMs = historyVisible ? Measure(history.Update) : 0;
            Console.WriteLine($"history {iteration}: starting SetRecords for {records.Length} records; visible={history.Visible}");
            var setRecordsMs = Measure(() => { Call(history, "SetRecords", (object)records); if (historyVisible) { Application.DoEvents(); history.Update(); } });
            Console.WriteLine($"history {iteration}: SetRecords completed in {setRecordsMs:F1} ms");
            if (!historyVisible)
            {
                showCallMs = Measure(() => history.Show());
                showPumpMs = Measure(Application.DoEvents);
                showPaintMs = Measure(history.Update);
            }
            var showMs = showCallMs + showPumpMs + showPaintMs;
            var refreshMs = Measure(() => { Call(history, "SetRecords", (object)records); Application.DoEvents(); });
            var controls = Descendants(history).Count();
            Form preview = null!;
            var openCardMs = Measure(() => { preview = LanguageForm(multi); preview.Show(history); Call(preview, "ContentReady"); Application.DoEvents(); });
            var backMs = Measure(() => { preview.Close(); history.Activate(); Application.DoEvents(); });
            preview.Dispose(); history.Close();
            Measurements.Add(new { scenario = historyVisible ? "history-visible-220-records" : "history-220-records", iteration, setRecordsMs, showMs, showCallMs, showPumpMs, showPaintMs, refreshMs, openCardMs, backMs, controls });
            PersistProgress();
            Console.WriteLine($"history {iteration}: set {setRecordsMs:F1} ms; show {showMs:F1} ms; refresh {refreshMs:F1} ms; open-card {openCardMs:F1} ms");
        }
        Check(Application.OpenForms.Count == 0, "all test windows disposed");
    }
}

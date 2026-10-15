using System.Collections;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<string> checks = [];
    private static readonly List<string> screenshots = [];
    private static readonly List<string> statuses = [];
    private static string output = "", dataRoot = "";
    private static Assembly desktop = null!;
    private static bool imageHistoryOnly, quickLanguageOnly, historyReuseOnly, pendingAnkiOnly, meaningReplayOnly;
    private static object Initial => Type("UserSettings").GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Run with tools/Invoke-IsolatedDesktopTest.ps1.");
        output = args[0]; imageHistoryOnly = args.Length == 2 && args[1] == "--image-history"; Directory.CreateDirectory(output);
        quickLanguageOnly = args.Length == 2 && args[1] == "--quick-language";
        historyReuseOnly = args.Length == 2 && args[1] == "--history-reuse";
        pendingAnkiOnly = args.Length == 2 && args[1] == "--pending-anki";
        meaningReplayOnly = args.Length == 2 && args[1] == "--meaning-replay";
        dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\floating-entry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable("DEEPSEEK_KNOWLEDGECLIPPER_API_KEY", "isolated-test-fixture-not-a-real-credential");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var start = new System.Windows.Forms.Timer { Interval = 10 };
        var exit = 0;
        start.Tick += async (_, _) =>
        {
            start.Stop(); string? failure = null;
            try { desktop = Assembly.Load("KnowledgeClipper"); await RunAsync(); }
            catch (Exception ex) { failure = ex.ToString(); exit = 1; Console.Error.WriteLine(ex); }
            finally
            {
                foreach (Form form in Application.OpenForms.Cast<Form>().ToArray()) form.Dispose();
                File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new
                {
                    passed = exit == 0, checks, screenshots, statuses, error = failure, dataRoot,
                    desktop = desktop?.Location,
                    sha256 = desktop == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(desktop.Location))).ToLowerInvariant(),
                    workflow = typeof(ClipDraft).Assembly.Location,
                    workflowSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ClipDraft).Assembly.Location))).ToLowerInvariant(),
                    scope = "Actual desktop flows and preview forms on isolated Windows desktop. Loopback content/import fixtures and deliberately unavailable import endpoints; no real model, speech, clipboard capture or knowledge-base writes. Covers quick failure recovery, history concurrency and normal preview behavior."
                }, new JsonSerializerOptions { WriteIndented = true }));
                context.ExitThread();
            }
        };
        start.Start(); Application.Run(context); return exit;
    }

    private static async Task RunAsync()
    {
        using var owner = new Form(); _ = owner.Handle;
        if (Environment.GetCommandLineArgs().Contains("--quick-feedback")) { await QuickFeedbackAsync(owner); return; }
        if (meaningReplayOnly) { await MeaningReplayAsync(owner); return; }
        if (pendingAnkiOnly) { await PendingAnkiAsync(owner); return; }
        if (historyReuseOnly) { await HistoryReuseAsync(owner); return; }
        if (quickLanguageOnly) { await QuickLanguageSpeechAsync(owner); return; }
        if (imageHistoryOnly)
        {
            using var image = new Bitmap(100, 40); using (var g = Graphics.FromImage(image)) g.Clear(Color.White);
            using var png = new MemoryStream(); image.Save(png, ImageFormat.Png);
            await ImageHistoryAndContextAsync(owner, png.ToArray()); return;
        }
        var settings = Settings();
        var languageRoot = Path.Combine(dataRoot, "language");
        WorkflowJson.Save(Path.Combine(languageRoot, "language-settings.json"), settings);
        WorkflowJson.Save(Path.Combine(languageRoot, "modes", "chat", "question-types.json"), new ChatConfiguration(
            [new("fast-answer", "快速答案", "types/fast-answer.md", "few-shots/fast-answer.md", [], DocumentId: "fixture-type")], "fixture-root", "fixture-base"));
        using var language = (IDisposable)New("LanguageDesktopFlow", owner, languageRoot, (Action<string>)statuses.Add, Initial);
        var configuration = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"));

        var question = Start(language, "Why does a rectangle have equal diagonals?", "question", settings, configuration, true);
        Check(Forms(language).Length == 0, "quick question stays hidden while its content request is in progress");
        await Until(() => Forms(language).Length == 1, "failed quick question opens its original preview");
        var preview = Forms(language).Single();
        Check(ReferenceEquals(Get(preview, "Draft"), question) && question.ContentError != null && question.ImportState != ImportState.Verified,
            "content failure retains the same question draft and never marks it imported");
        Check(question.Original.SelectedText == "Why does a rectangle have equal diagonals?" && question.Conversation.Count == 0 && question.FollowUpCards.Count == 0,
            "question failure preserves original input without generating a follow-up");
        Save(preview, "question-content-failure.png");
        Call(language, "Resume", question.Id);
        var resumedChat = Application.OpenForms.Cast<Form>().Single(f => f.Name == "knowledge-clipper.chat");
        Check(ReferenceEquals(Get(resumedChat, "Draft"), question) && question.Chat != null,
            "resuming a failed quick question uses the current chat route and original draft identity");
        resumedChat.Close(); preview.Close(); await Task.Delay(30);

        var learning = Start(language, "patience", "language", settings, configuration, true);
        Check(Forms(language).Length == 0, "quick language starts with no preview window");
        await Until(() => Forms(language).Length == 1, "failed default language content returns to normal language preview");
        var store = new DraftStore(languageRoot);
        var children = LanguageModeCards.Children(learning, store);
        Check(children.Length == 2 && children.All(c => c.ContentError != null && c.ImportState != ImportState.Verified),
            "quick language failure keeps both configured default practice drafts and their errors");
        Check(children.All(c => c.Material.SelectedText == "patience" && c.ConfirmedDestination == null), "content failure never freezes an import or rewrites the selected material");
        preview = Forms(language).Single(); Save(preview, "language-content-failure.png"); preview.Close(); await Task.Delay(30);

        var normal = Start(language, "Why is the sky blue?", "question", settings, configuration, false);
        var normalChat = Application.OpenForms.Cast<Form>().Single(f => f.Name == "knowledge-clipper.chat");
        Check(ReferenceEquals(Get(normalChat, "Draft"), normal) && normal.Chat?.Input == "Why is the sky blue?", "disabled quick question opens chat with the original input");
        Check(normal.Chat!.Prompts.Count == 0 && normal.ImportState == ImportState.Unconfirmed, "ordinary question waits for explicit send and never imports automatically");
        normalChat.Close(); await Task.Delay(30);

        using (var fixture = new QuestionApiFixture())
        {
            var answered = Start(language, "What is two plus two?", "question", settings with { ApiBaseUrl = fixture.Url }, configuration, true);
            Check(Forms(language).Length == 0, "answered quick question starts without a visible window");
            await Until(() => Forms(language).Length == 1, "quick question import failure opens normal preview", 12000);
            Check(answered.ContentError == null && answered.Conversation.Count == 1 && answered.Conversation[0].Answer.AnswerText == "Two plus two equals four.",
                "import failure preserves the generated answer and its single original turn");
            var cards = QuestionCards.Preview(answered, store);
            Check(fixture.RequestCount == 1 && cards.Count == 1 && cards[0].State != ImportState.Verified && answered.ImportError != null,
                "quick question makes one content request and stops at failed import without a follow-up");
            Check(answered.FollowUpCards.Count == 1 && store.Find(answered.FollowUpCards[0])?.ConfirmedDestination == ExportDestination.RemNote,
                "failed question import preserves its frozen card identity and destination for retry");
            Save(Forms(language).Single(), "question-import-failure.png"); Forms(language).Single().Close();
        }

        using (var pendingApi = new QuestionApiFixture(holdResponses: true))
        {
            var pendingSettings = settings with { ApiBaseUrl = pendingApi.Url, ContentTimeoutSeconds = 20 };
            var older = Start(language, "First pending question?", "question", pendingSettings, configuration, true);
            var newer = Start(language, "Second pending question?", "question", pendingSettings, configuration, true);
            await Until(() => pendingApi.RequestCount == 2, "two independent quick questions begin one content request each");
            var fromHistory = store.Find(older.Id)!;
            Call(language, "ContinueHistory", fromHistory);
            await Task.Delay(200);
            var pendingChat = Application.OpenForms.Cast<Form>().Single(f => f.Name == "knowledge-clipper.chat");
            Check(ReferenceEquals(Get(pendingChat, "Draft"), older),
                "history resolves the active older quick draft rather than a detached disk copy");
            Check(pendingApi.RequestCount == 2, "opening history during background work does not request the same content again");
            pendingChat.Close();
            pendingApi.ReleaseResponses();
            await Until(() => Forms(language).Length == 2 && older.ImportError != null && newer.ImportError != null,
                "both concurrent quick failures retain their original previews", 12000);
            Check(Forms(language).Select(f => ((ClipDraft)Get(f, "Draft")!).Id).Distinct().Count() == 2 &&
                store.Find(older.Id)!.FollowUpCards.Count == 1 && store.Find(newer.Id)!.FollowUpCards.Count == 1,
                "concurrent quick drafts retain distinct import identities after history interaction");
            foreach (var form in Forms(language)) form.Close();
        }

        var excerptRoot = Path.Combine(dataRoot, "excerpt");
        using var excerpt = (IDisposable)New("ExcerptDesktopFlow", owner, excerptRoot, settings, (Action<string>)statuses.Add);
        var excerpts = new ExcerptStore(excerptRoot);
        var material = Material("First public excerpt line.\nSecond public excerpt line.");
        var session = ExcerptCards.Create(material, settings, excerpts);
        var cardId = session.Cards.Single().Id;
        var completion = (Task)Call(excerpt, "CompleteQuickAsync", session)!;
        Check(Forms(excerpt).Length == 0, "quick excerpt remains hidden during import attempt");
        await completion;
        Check(Forms(excerpt).Length == 1 && ReferenceEquals(Get(Forms(excerpt).Single(), "Session"), session), "failed quick excerpt returns to its normal preview and original session");
        var failed = ExcerptCards.Get(session, excerpts, cardId);
        Check(failed.ImportState != ImportState.Verified && !string.IsNullOrWhiteSpace(failed.ImportError) && failed.Id == cardId && failed.ConfirmedDestination == ExportDestination.RemNote,
            "excerpt import failure preserves saved card identity, destination and error");
        Check(failed.Excerpt?.Front == material.SelectedText && failed.Excerpt.Back == "", "quick excerpt failure retains raw front and empty back");
        preview = Forms(excerpt).Single(); Save(preview, "excerpt-import-failure.png");
        Call(excerpt, "Resume", session.Id);
        Check(Forms(excerpt).Length == 1 && ReferenceEquals(preview, Forms(excerpt).Single()) && excerpts.All().Count() == 1,
            "excerpt recovery reuses saved session and cannot duplicate previews or cards");
        preview.Close();

        var normalSession = (string)Call(excerpt, "StartMaterial", Material("Ordinary excerpt"), settings, null, false)!;
        Check(Forms(excerpt).Length == 1 && excerpts.Find(normalSession) != null, "ordinary excerpt still opens editable preview immediately");
        var normalSaved = excerpts.Find(normalSession)!;
        Check(ExcerptCards.Get(normalSaved, excerpts, normalSaved.Cards.Single().Id).ConfirmedDestination == null,
            "ordinary excerpt waits for manual import confirmation");
        Forms(excerpt).Single().Close();

        await CheckExcerptHistoryDuringQuickImportAsync(owner, settings);

        var imagePath = Path.Combine(dataRoot, "public-image.png");
        using (var image = new Bitmap(20, 12)) { using var g = Graphics.FromImage(image); g.Clear(Color.DarkSlateBlue); image.Save(imagePath, ImageFormat.Png); }
        var images = new ImageMaterialStore(excerptRoot);
        var job = images.Create([(imagePath, "question")], MaterialKind.OriginalImages, settings, Path.Combine(AppContext.BaseDirectory, "modes"), Path.Combine(excerptRoot, "modes"));
        var imageSession = (string)Call(excerpt, "StartImages", job, new ExcerptFields("", "") { FrontImages = job.Questions }, null, true)!;
        Check(Forms(excerpt).Length == 0, "quick single-image excerpt begins in the background");
        await Until(() => Forms(excerpt).Length == 1, "failed quick screenshot opens normal image preview", 12000);
        var savedImage = excerpts.Find(imageSession)!;
        var imageCard = ExcerptCards.Get(savedImage, excerpts, savedImage.Cards.Single().Id);
        Check(imageCard.Excerpt?.FrontImages.Length == 1 && imageCard.Excerpt.BackImages.Length == 0 && imageCard.Excerpt.Back == "" && imageCard.ImportState != ImportState.Verified,
            "quick screenshot failure preserves one original image with no generated answer or card back");
        Save(Forms(excerpt).Single(), "image-import-failure.png"); Forms(excerpt).Single().Close();

        using (var stoppedApi = new QuestionApiFixture(holdResponses: true))
        {
            var stoppedRoot = Path.Combine(dataRoot, "stopped-language");
            var stoppedSettings = settings with { ApiBaseUrl = stoppedApi.Url, ContentTimeoutSeconds = 20 };
            WorkflowJson.Save(Path.Combine(stoppedRoot, "language-settings.json"), stoppedSettings);
            var stoppedFlow = (IDisposable)New("LanguageDesktopFlow", owner, stoppedRoot, (Action<string>)statuses.Add, Initial);
            var stoppedDraft = Start(stoppedFlow, "Question cancelled on application exit?", "question", stoppedSettings, configuration, true);
            await Until(() => stoppedApi.RequestCount == 1, "quick content request is active before disposal");
            stoppedFlow.Dispose();
            await Task.Delay(250);
            Check(Forms(stoppedFlow).Length == 0 && new DraftStore(stoppedRoot).Find(stoppedDraft.Id)?.ImportState != ImportState.Verified,
                "disposing language workflow cancels quick work without reopening a preview or marking imported");
        }
        var stoppedExcerptRoot = Path.Combine(dataRoot, "stopped-excerpt");
        var stoppedExcerpt = (IDisposable)New("ExcerptDesktopFlow", owner, stoppedExcerptRoot, settings, (Action<string>)statuses.Add);
        var stoppedStore = new ExcerptStore(stoppedExcerptRoot);
        var stoppedSession = ExcerptCards.Create(Material("Excerpt cancelled on application exit."), settings, stoppedStore);
        var pendingImport = (Task)Call(stoppedExcerpt, "CompleteQuickAsync", stoppedSession)!;
        stoppedExcerpt.Dispose(); await pendingImport;
        Check(Forms(stoppedExcerpt).Length == 0 && ExcerptCards.Get(stoppedSession, stoppedStore, stoppedSession.Cards.Single().Id).ImportState != ImportState.Verified,
            "disposing excerpt workflow cancels quick import without reopening a preview or declaring success");
        await ImageRoutesAsync(owner);
    }

    private static async Task CheckExcerptHistoryDuringQuickImportAsync(Form owner, LanguageSettings settings)
    {
        using var endpoint = new DeferredExcerptFixture();
        var root = Path.Combine(dataRoot, "excerpt-history-concurrency");
        var currentSettings = settings with { RemNoteSdkEndpoint = endpoint.Url };
        using var flow = (IDisposable)New("ExcerptDesktopFlow", owner, root, currentSettings, (Action<string>)statuses.Add);
        var store = new ExcerptStore(root);
        var original = ExcerptCards.Create(Material("Public excerpt awaiting its first import."), currentSettings, store);
        var id = original.Cards.Single().Id;
        var quick = (Task)Call(flow, "CompleteQuickAsync", original)!;
        try
        {
            await Until(() => endpoint.ExportCount == 1, "quick excerpt reaches the delayed loopback importer once");
            Check(Forms(flow).Length == 0 && original.ImportGate.CurrentCount == 0,
                "quick excerpt holds its import gate without showing a preview");
            Call(flow, "Resume", original.Id);
            var form = Forms(flow).Single();
            var resumed = (ExcerptSession)Get(form, "Session")!;
            Check(ReferenceEquals(resumed, original),
                "excerpt history resolves the active quick session rather than a detached disk copy");
            Check(ReferenceEquals(resumed.ImportGate, original.ImportGate) && resumed.ImportGate.CurrentCount == 0,
                "excerpt history shares the gate held by the pending quick import");
            var manual = (Task)Call(form, "ImportAsync", (object)new[] { id })!;
            await Task.Delay(150);
            Check(!manual.IsCompleted && endpoint.ExportCount == 1 && endpoint.HealthCount == 1 && endpoint.ReadbackCount == 0,
                "manual import from history waits for quick import instead of issuing another request");
            endpoint.ReleaseResponses();
            await Task.WhenAll(quick, manual).WaitAsync(TimeSpan.FromSeconds(8));
            var saved = ExcerptCards.Get(original, store, id);
            Check(saved.ImportState == ImportState.Verified && saved.RemNoteResult?.RemIds.Length == 1 &&
                endpoint.ExportCount == 1 && endpoint.HealthCount == 1 && endpoint.ReadbackCount == 0,
                "quick and history imports verify one saved identity with exactly one export write");
            Check(Forms(flow).Length == 1 && ReferenceEquals(form, Forms(flow).Single()),
                "successful quick completion preserves only the preview explicitly opened from history");
        }
        finally
        {
            endpoint.ReleaseResponses();
            await quick.WaitAsync(TimeSpan.FromSeconds(8));
            foreach (var form in Forms(flow)) form.Close();
        }
    }

    private static LanguageSettings Settings() => new()
    {
        ApiBaseUrl = "http://127.0.0.1:1", ContentTimeoutSeconds = 1, ContentMaxRetries = 0,
        AnkiEndpoint = "http://127.0.0.1:1", RemNoteSdkEndpoint = "http://127.0.0.1:1",
        LanguageAutoRead = false, TypedInputAutoRead = false, SpeechAutoPlay = false,
        PythonPath = @"D:\Caches\KnowledgeClipper\not-an-installed-test-python.exe", Practice = PracticeChoice.Both
    };
    private static CaptureMaterial Material(string text) => new(text, null, "generated_allowed", null, "Public isolated UI fixture", 0, DateTimeOffset.UtcNow);
    private static ClipDraft Start(object flow, string text, string intent, LanguageSettings settings, ContentConfiguration config, bool quick)
        => (ClipDraft)Call(flow, "StartMaterial", Material(text), new InputMetadata("selection-dot", intent, ReadRequested: false, ExplicitQuestion: intent == "question" ? text : null), settings, config, DateTimeOffset.UtcNow, quick)!;
    private static Type Type(string name) => desktop.GetType("KnowledgeClipper." + name, true)!;
    private static object New(string name, params object?[] args) => Type(name).GetConstructors(Flags).Single(c => c.GetParameters().Length == args.Length).Invoke(args);
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static object? Get(object target, string property) => target.GetType().GetProperty(property, Flags)!.GetValue(target);
    private static Form[] Forms(object flow) => ((IEnumerable)flow.GetType().GetField("forms", Flags)!.GetValue(flow)!).Cast<Form>().Where(f => !f.IsDisposed).ToArray();
    private static Form[] ChatForms(object flow) => ((IEnumerable)flow.GetType().GetField("chatForms", Flags)!.GetValue(flow)!).Cast<Form>().Where(f => !f.IsDisposed).ToArray();
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
    private static async Task Until(Func<bool> condition, string name, int timeout = 8000)
    { var timer = Stopwatch.StartNew(); while (!condition() && timer.ElapsedMilliseconds < timeout) await Task.Delay(25); Check(condition(), name); }
    private static void Save(Form form, string name)
    { var path = Path.Combine(output, name); using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, form.ClientRectangle); image.Save(path, ImageFormat.Png); screenshots.Add(path); }

    private sealed class DeferredExcerptFixture : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Url { get; }
        internal int HealthCount, ExportCount, ReadbackCount;
        internal DeferredExcerptFixture()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Url = "http://127.0.0.1:" + port; listener.Prefixes.Add(Url + "/"); listener.Start(); _ = RunAsync();
        }
        internal void ReleaseResponses() => release.TrySetResult();
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    _ = RespondAsync(context);
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        private async Task RespondAsync(HttpListenerContext context)
        {
            try
            {
                object response;
                switch (context.Request.Url!.AbsolutePath.Split('/').Last())
                {
                    case "health":
                        Interlocked.Increment(ref HealthCount);
                        response = new { connected = true, supported = true, acceptWriteOperations = true };
                        break;
                    case "export":
                        using (var input = new StreamReader(context.Request.InputStream))
                        using (var document = JsonDocument.Parse(await input.ReadToEndAsync()))
                        {
                            var plan = document.RootElement.GetProperty("plan").Deserialize<RemNoteExportPlan>(WorkflowJson.Options)!;
                            if (plan.Cards.Any(card => card.Kind != "plain" || card.Back.Length != 0 || card.Front.Any(part => part.Kind != "text")))
                                throw new InvalidDataException("The delayed fixture accepts plain public excerpt text only.");
                            Interlocked.Increment(ref ExportCount);
                            await release.Task.WaitAsync(stop.Token);
                            response = PlainReceipt(plan);
                        }
                        break;
                    case "readback":
                        Interlocked.Increment(ref ReadbackCount);
                        throw new InvalidDataException("A shared pending excerpt import must wait rather than issue concurrent readback.");
                    default:
                        throw new InvalidDataException("Unexpected loopback fixture operation.");
                }
                await ReplyAsync(context, response);
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await ReplyAsync(context, new { error = ex.Message });
            }
        }
        private static object PlainReceipt(RemNoteExportPlan plan) => new
        {
            schemaVersion = plan.SchemaVersion, sourceId = plan.SourceId, rootRemId = "fixture-root", documentRemId = "fixture-excerpts",
            cards = plan.Cards.Select((card, index) => new
            {
                key = card.Key, kind = card.Kind, remIds = new[] { "fixture-front-" + index }, cardIds = Array.Empty<string>(), childRemIds = Array.Empty<string>(),
                nodes = new[] { new
                {
                    remId = "fixture-front-" + index, parentId = "fixture-excerpts", text = card.Front.Select(part => part.Text).ToArray(),
                    backText = Array.Empty<string>(), media = Array.Empty<object>(), remType = 0, isDocument = false,
                    isCardItem = false, isExtraCardDetail = false, practiceEnabled = false,
                    children = Array.Empty<string>(), cardIds = Array.Empty<string>(), cardTypes = Array.Empty<string>()
                } }
            }).ToArray()
        };
        private static async Task ReplyAsync(HttpListenerContext context, object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WorkflowJson.Options);
            context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
        }
        public void Dispose() { stop.Cancel(); listener.Close(); stop.Dispose(); }
    }

    private sealed class QuestionApiFixture : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Url { get; }
        internal int RequestCount;
        internal QuestionApiFixture(bool holdResponses = false)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Url = "http://127.0.0.1:" + port; listener.Prefixes.Add(Url + "/"); listener.Start(); _ = RunAsync();
            if (!holdResponses) release.TrySetResult();
        }
        internal void ReleaseResponses() => release.TrySetResult();
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    _ = RespondAsync(context);
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        private async Task RespondAsync(HttpListenerContext context)
        {
            try
            {
                using var input = new StreamReader(context.Request.InputStream); _ = await input.ReadToEndAsync();
                Interlocked.Increment(ref RequestCount);
                await release.Task.WaitAsync(stop.Token);
                var answer = JsonSerializer.Serialize(new { answer_state = "answered", answer_text = "Two plus two equals four.", unresolved_reasons = Array.Empty<string>() });
                var response = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = answer } } } });
                var bytes = Encoding.UTF8.GetBytes(response); context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        public void Dispose() { stop.Cancel(); listener.Close(); stop.Dispose(); }
    }
}

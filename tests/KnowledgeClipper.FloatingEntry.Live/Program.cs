using System.Collections;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string DefaultDesktop = @"D:\Artifacts\KnowledgeClipper\floating-quick\RemNoteQuickClipper.Desktop\Release\net8.0-windows\win-x64\KnowledgeClipper.dll";
    private static string projectRoot = "", dataRoot = "", receiptPath = "", desktopPath = "", mode = "", manifestPath = "";
    private static bool imageExcerptOnly;
    private static volatile string phase = "prepare";
    private static Assembly desktop = null!;
    private static LiveManifest manifest = null!;
    private static LiveAttempt attempt = null!;
    private static HttpObserver? observer;
    private static readonly List<object> flows = [];
    private static readonly HashSet<int> observedForms = [];
    private static DraftStore drafts = null!;
    private static ExcerptStore excerpts = null!;
    private static ImageMaterialStore images = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Run with tools/Invoke-IsolatedDesktopTest.ps1; never use the user's desktop.");
        if (args.Length is not (4 or 6) || args[3] is not ("--prepare" or "--live" or "--readback-only" or
            "--prepare-image-excerpt" or "--live-image-excerpt" or "--readback-image-excerpt") || args.Length == 6 && args[4] != "--desktop")
            throw new ArgumentException("FloatingEntry.Live <project-root> <fixed-data-root> <receipt.json> --prepare|--live|--readback-only or --prepare-image-excerpt|--live-image-excerpt|--readback-image-excerpt [--desktop KnowledgeClipper.dll]");
        projectRoot = Path.GetFullPath(args[0]); dataRoot = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
        receiptPath = Path.GetFullPath(args[2]); mode = args[3]; desktopPath = Path.GetFullPath(args.Length == 6 ? args[5] : DefaultDesktop);
        imageExcerptOnly = mode.EndsWith("-image-excerpt", StringComparison.Ordinal);
        if (imageExcerptOnly && args.Length != 6) throw new ArgumentException("The image-only canary requires an explicit final --desktop assembly path.");
        if (imageExcerptOnly) mode = mode switch { "--prepare-image-excerpt" => "--prepare", "--live-image-excerpt" => "--live", _ => "--readback-only" };
        if (!dataRoot.StartsWith(@"D:\Caches\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase) ||
            !receiptPath.StartsWith(@"D:\Artifacts\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use an isolated D:\\Caches\\KnowledgeClipper data root and D:\\Artifacts\\KnowledgeClipper receipt.");
        Directory.CreateDirectory(dataRoot);
        using var exclusive = new FileStream(Path.Combine(dataRoot, "live-run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        manifestPath = Path.Combine(dataRoot, "manifest.json");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var start = new System.Windows.Forms.Timer { Interval = 10 };
        var exit = 0;
        start.Tick += async (_, _) =>
        {
            start.Stop();
            try { if (imageExcerptOnly) await RunImageExcerptAsync(); else await RunAsync(); }
            catch (Exception ex)
            {
                exit = 1;
                if (attempt != null) { attempt.Error = SafeError(ex); attempt.Outcome = "FAILED_WITH_SAVED_IDENTITIES"; }
                Console.Error.WriteLine(SafeError(ex));
            }
            finally
            {
                if (attempt != null)
                {
                    SampleWindows(); attempt.CompletedAt = DateTimeOffset.UtcNow; Save();
                    Console.WriteLine(attempt.Outcome + " " + receiptPath);
                }
                observer?.Dispose();
                foreach (var flow in flows.OfType<IDisposable>().Reverse()) flow.Dispose();
                context.ExitThread();
            }
        };
        start.Start(); Application.Run(context); return exit;
    }

    private static async Task RunAsync()
    {
        if (!File.Exists(manifestPath))
        {
            if (mode == "--readback-only") throw new InvalidDataException("No saved manifest; readback never creates fixtures.");
            manifest = Prepare(); WorkflowJson.Save(manifestPath, manifest);
        }
        else manifest = WorkflowJson.Read<LiveManifest>(manifestPath);
        if (manifest.SchemaVersion != "knowledge-clipper.floating-entry-live.v1" || manifest.DataRoot != dataRoot)
            throw new InvalidDataException("The manifest belongs to a different fixture set; it will not be replaced.");
        if (ImageAsset.Hash(File.ReadAllBytes(manifest.ImagePath)) != manifest.ImageSha256)
            throw new InvalidDataException("The retained public PNG changed; it will not be regenerated.");
        attempt = new() { Number = manifest.Attempts.Count + 1, Mode = mode };
        manifest.Attempts.Add(attempt); Save();
        if (mode == "--prepare")
        {
            attempt.Outcome = "PREPARED_NO_EXTERNAL_CALLS"; Save(); return;
        }
        desktop = Assembly.LoadFrom(desktopPath);
        attempt.DesktopAssembly = desktop.Location;
        attempt.DesktopSha256 = Hash(desktop.Location);
        Check(attempt.DesktopSha256 == Hash(desktopPath), "runtime loads the exact requested production desktop assembly");
        var shippedWorkflow = Path.Combine(Path.GetDirectoryName(desktopPath)!, "KnowledgeClipper.Workflow.dll");
        Check(Hash(typeof(LanguageSettings).Assembly.Location) == Hash(shippedWorkflow), "test and production use identical workflow assemblies");
        Check(manifest.Settings.Practice == PracticeChoice.Reading && !manifest.Settings.LanguageAutoRead && !manifest.Settings.TypedInputAutoRead &&
            !manifest.Settings.SpeechAutoPlay && !manifest.Settings.GlobalSelection && !manifest.Settings.ClipboardImageDotEnabled,
            "retained fixture settings use one default reading mode with audio and capture listeners disabled");
        observer = new(() => phase);
        using var request = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(180) };
        using var preflight = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var health = await new RemNoteConnection(request, manifest.Settings.RemNoteSdkEndpoint).HealthAsync(preflight.Token);
        Check(health.GetProperty("connected").GetBoolean() && health.GetProperty("supported").GetBoolean() &&
            (mode == "--readback-only" || health.GetProperty("acceptWriteOperations").GetBoolean()), "the independent RemNote SDK plugin is connected and ready");
        _ = await new AnkiConnection(request, manifest.Settings.AnkiEndpoint).CallAsync("version", ct: preflight.Token);
        var preflightRequests = observer.Snapshot();
        Check(preflightRequests.Any(item => item.Operation == "health" && item.Status == 200) &&
            preflightRequests.Any(item => item.Operation == "version" && item.Status == 200),
            "HTTP observer captures successful SDK health and Anki version before any production entry");
        Check(RemNoteConnection.ResolveEndpoint(manifest.Settings.RemNoteSdkEndpoint).Contains(":43111", StringComparison.Ordinal), "RemNote transport resolves to the independent 43111 service");
        drafts = new(dataRoot); excerpts = new(dataRoot); images = new(dataRoot);
        var settingsFile = Path.Combine(dataRoot, "language-settings.json");
        if (!File.Exists(settingsFile)) WorkflowJson.Save(settingsFile, manifest.Settings);
        Check(JsonSerializer.Serialize(WorkflowJson.Read<LanguageSettings>(settingsFile), WorkflowJson.Options) == JsonSerializer.Serialize(manifest.Settings, WorkflowJson.Options),
            "only the isolated frozen settings file is used");
        using var owner = new Form { Name = "knowledge-clipper.live.hidden-owner" }; _ = owner.Handle;
        var initial = Type("UserSettings").GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        var language = New("LanguageDesktopFlow", owner, dataRoot, (Action<string>)Status, initial); flows.Add(language);
        var excerpt = New("ExcerptDesktopFlow", owner, dataRoot, manifest.Settings, (Action<string>)Status); flows.Add(excerpt);
        var config = Activator.CreateInstance(Type("AppConfig"), true)!;
        Type("AppConfig").GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        Type("AppConfig").GetProperty("MineruScriptPath")!.SetValue(config, manifest.Settings.MineruScriptPath);
        var image = New("ImageDesktopFlow", owner, config, language, excerpt, null!); flows.Add(image);
        using var watch = new System.Windows.Forms.Timer { Interval = 20 };
        watch.Tick += (_, _) => SampleWindows(); watch.Start();
        Application.Idle += OnIdle;
        try
        {
            await Step("language", async step => await LanguageOrQuestionAsync(language, manifest.Fixtures["language"], step, request));
            await Step("question", async step => await LanguageOrQuestionAsync(language, manifest.Fixtures["question"], step, request));
            await Step("excerpt", async step =>
            {
                var fixture = manifest.Fixtures["excerpt"];
                var session = FindExcerpt(fixture);
                if (session == null)
                {
                    RequireNewLaunch(fixture); session = ExcerptCards.Create(fixture.Material, manifest.Settings, excerpts);
                    fixture.SessionId = session.Id; Save(); step.Entry = "production-raw-excerpt-session";
                }
                else step.Entry = "resume-original-excerpt-session";
                if (mode != "--readback-only" && session.Cards.Any(link => excerpts.Cards.Find(link.Id)?.ImportState != ImportState.Verified))
                    await AwaitEntry((Task)Call(excerpt, "CompleteQuickAsync", session)!, () => ExcerptVerified(session), TimeSpan.FromMinutes(8));
                Check(session.Cards.Count == 1, "quick excerpt has exactly one original-only draft");
                var draft = ExcerptCards.Get(session, excerpts, session.Cards.Single().Id);
                Check(draft.Excerpt is { FrontImages.Length: 0, Back.Length: 0 } && draft.Excerpt.Front == fixture.Material.SelectedText,
                    "raw excerpt preserves the selected text with an empty back");
                step.CardIds = [draft.Id]; await VerifyAsync("excerpt", draft, excerpts.Cards, request);
            });
            await Step("image", async step =>
            {
                var fixture = manifest.Fixtures["image"];
                var job = FindImage(fixture);
                if (job == null)
                {
                    RequireNewLaunch(fixture); step.Entry = "production-StartClipboardImage-png-quickOriginal";
                    Call(image, "StartClipboardImage", File.ReadAllBytes(manifest.ImagePath), true);
                    job = FindImage(fixture) ?? throw new InvalidDataException("Image entry did not persist its original job.");
                }
                else step.Entry = "resume-original-image-job-and-session";
                Check(job.Questions.Length == 1 && job.Answers.Length == 0 && job.Kind == MaterialKind.OriginalImages && job.Route == "image-excerpt" &&
                    job.Answer == null && job.Ocr.All(item => item.RawText == null && item.Cleanup == null), "quick image keeps one original PNG without OCR or answer generation");
                var session = excerpts.Find(job.DestinationId ?? throw new InvalidDataException("Saved image job has no destination; no replacement job is created."))
                    ?? throw new InvalidDataException("The image's original excerpt session is missing.");
                fixture.SessionId = session.Id; Save();
                if (mode != "--readback-only")
                {
                    Task? retry = step.Entry.StartsWith("resume", StringComparison.Ordinal) && !ExcerptVerified(session)
                        ? (Task)Call(excerpt, "CompleteQuickAsync", session)! : null;
                    await AwaitEntry(retry, () => ExcerptVerified(session), TimeSpan.FromMinutes(8));
                }
                Check(session.Cards.Count == 1 && session.ImageMaterialId == job.Id, "quick image retains one card draft linked to the original image job");
                var draft = ExcerptCards.Get(session, excerpts, session.Cards.Single().Id);
                Check(draft.Excerpt is { Front.Length: 0, Back.Length: 0, FrontImages.Length: 1, BackImages.Length: 0 } &&
                    draft.Excerpt.FrontImages[0].Sha256 == manifest.ImageSha256, "image quick mode stores only the frozen image with no generated back");
                step.CardIds = [draft.Id]; await VerifyAsync("image", draft, excerpts.Cards, request);
            });
            phase = "history";
            var history = HistoryRecords.Read(drafts, excerpts);
            Check(history.Length == 4 && history.Select(item => item.Id).Distinct().Count() == 4, "all four routes have independent saved history records");
            Check(history.All(item => HistoryRecords.ActionText(item, drafts, excerpts) == "查看"), "each fully imported record is labelled view");
            Check(history.Count(item => item.CanPlay) == 1 && history.Single(item => item.CanPlay).Id == manifest.Fixtures["language"].DraftId,
                "only language history is eligible for playback");
            Check(drafts.Recent().All(draft => draft.AudioPath == null && !draft.Times.Keys.Any(key => key.StartsWith("speech", StringComparison.Ordinal))),
                "no speech request or audio file was created");
            NoWindows();
            attempt.Requests = observer.Snapshot();
            Check(attempt.Requests.Where(item => item.Phase is "excerpt" or "image").All(item => item.Operation != "content"), "excerpt and image shortcuts make no model requests");
            Check(attempt.Requests.All(item => item.Port is not (43101 or 43102) && item.Operation != "follow-up"), "no legacy SDK transport or follow-up operation was used");
            if (mode == "--readback-only")
                Check(attempt.Requests.All(item => item.Operation is not ("content" or "export" or "finalize-media" or "addNote")), "cold readback issues no generation or export writes");
            WorkflowJson.Save(Path.Combine(dataRoot, "history-readback.json"), history.Select(item => new { item.Key, item.Kind, item.CanPlay,
                action = HistoryRecords.ActionText(item, drafts, excerpts) }).ToArray());
            attempt.Outcome = "PASS"; Save();
        }
        finally { Application.Idle -= OnIdle; watch.Stop(); }
    }

    private static LiveManifest Prepare()
    {
        var user = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
        var runId = Guid.NewGuid().ToString("N");
        var deck = "KnowledgeClipper验收::浮点快捷入口::" + runId[..8];
        var settings = user with { Practice = PracticeChoice.Reading, AnkiRootDeck = deck, AnkiDeck = deck,
            GlobalSelection = false, ClipboardImageDotEnabled = false, LanguageAutoRead = false, TypedInputAutoRead = false, SpeechAutoPlay = false,
            QuickLanguage = true, QuickQuestion = true, QuickExcerpt = true, QuickScreenshot = true,
            ExcerptModesDirectory = Path.Combine(projectRoot, "modes"), ModeDestinations = new(user.ModeDestinations, StringComparer.Ordinal) };
        settings.Validate();
        var configuration = ModeConfigurationLoader.Load(Path.Combine(projectRoot, "modes"), Path.Combine(dataRoot, "modes"));
        if (configuration.Modes.Any(item => item.LoadError != null)) throw new InvalidDataException("The published mode files are incomplete.");
        foreach (var id in new[] { "language-en", "question", "excerpt-manual", "image-original" })
        {
            var definition = configuration.Modes.Select(item => item.Definition).Concat(ExportRouting.BuiltinModes).Single(item => item.Id == id);
            if (ExportRouting.ForMode(settings, definition) != (id == "language-en" ? ExportDestination.Anki : ExportDestination.RemNote))
                throw new InvalidDataException("Current mode destinations differ from the authorized live matrix: " + id);
        }
        var result = new LiveManifest { DataRoot = dataRoot, RunId = runId, Settings = settings, Configuration = configuration };
        string Title(string key) => "KnowledgeClipper public floating-entry fixture " + result.RunId + " / " + key;
        const string sentence = "The car maintained a constant velocity on the straight road.";
        foreach (var key in new[] { "language", "question", "excerpt", "image" })
        {
            var text = key switch { "language" => "velocity", "question" => "为什么直角三角形的斜边最长？", "excerpt" => "A triangle has three sides.", _ => "Public single image archive." };
            var material = key == "language" ? new CaptureMaterial(text, sentence, "source", sentence.IndexOf(text, StringComparison.Ordinal), Title(key), 0, result.CreatedAt)
                : new CaptureMaterial(text, null, "generated_allowed", null, Title(key), 0, result.CreatedAt);
            result.Fixtures[key] = new() { Key = key, Material = material };
        }
        result.ImagePath = Path.Combine(dataRoot, "public-single-image.png");
        if (File.Exists(result.ImagePath)) throw new InvalidDataException("A fixture PNG exists without its manifest; it will not be overwritten.");
        using (var bitmap = new Bitmap(640, 220))
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
            using var title = new Font("Segoe UI", 23); using var detail = new Font("Segoe UI", 18);
            graphics.DrawString("PUBLIC QUICK IMAGE", title, Brushes.DarkBlue, 28, 28);
            graphics.DrawString("3 x 4 = 12", detail, Brushes.Black, 28, 105);
            bitmap.Save(result.ImagePath, ImageFormat.Png);
        }
        result.ImageSha256 = ImageAsset.Hash(File.ReadAllBytes(result.ImagePath));
        WorkflowJson.Save(Path.Combine(dataRoot, "language-settings.json"), settings);
        return result;
    }

    private static async Task LanguageOrQuestionAsync(object language, EntryFixture fixture, StepObservation step, HttpClient http)
    {
        var parent = FindDraft(fixture);
        var intent = fixture.Key;
        if (parent == null)
        {
            RequireNewLaunch(fixture); step.Entry = "production-StartMaterial-quick";
            parent = (ClipDraft)Call(language, "StartMaterial", fixture.Material,
                new InputMetadata("selection-dot", intent, ReadRequested: false, ExplicitQuestion: intent == "question" ? fixture.Material.SelectedText : null),
                manifest.Settings, manifest.Configuration, DateTimeOffset.UtcNow, true)!;
            fixture.DraftId = parent.Id; Save();
        }
        else step.Entry = "resume-original-draft";
        if (mode != "--readback-only" && !LanguageVerified(parent, intent))
        {
            Task? retry = null;
            if (step.Entry.StartsWith("resume", StringComparison.Ordinal))
            {
                if (intent == "question" && parent.Conversation.Count == 1 && parent.Conversation[0].Answer.Answered)
                    retry = FloatingQuickImport.LanguageOrQuestionAsync(parent, drafts, new CardImportRouter(http));
                else retry = (Task)Call(language, "CompleteQuickAsync", parent, Task.CompletedTask)!;
            }
            await AwaitEntry(retry, () => LanguageVerified(drafts.Find(parent.Id)!, intent), TimeSpan.FromMinutes(12));
        }
        parent = drafts.Find(parent.Id)!;
        Check(parent.Original == fixture.Material && !parent.Input.ReadRequested, fixture.Key + ": original material and no-audio metadata are retained");
        ClipDraft child;
        if (intent == "language")
        {
            var children = LanguageModeCards.AllChildren(parent, drafts);
            Check(children.Length == 1 && children[0].ModeId == "language-en" && children[0].Practice == PracticeChoice.Reading && parent.FollowUpCards.Count == 0,
                "quick language generates exactly the default English reading mode");
            child = children.Single();
            Check(child.Content?.Fields is { IsUnresolved: false } && child.Material.SelectedText == "velocity" && child.Material.ContextText == fixture.Material.ContextText,
                "official language processing succeeds with the original complete velocity context");
        }
        else
        {
            Check(parent.Conversation.Count == 1 && parent.FollowUpCards.Count == 1 && parent.FollowUpCards.ContainsKey(0) && parent.PendingFollowUp == null && parent.ModeCards.Count == 0,
                "quick question has one generated answer, one imported card and no follow-up");
            child = drafts.Find(parent.FollowUpCards[0])!;
            Check(parent.Conversation[0].Answer.Answered && !string.IsNullOrWhiteSpace(parent.Conversation[0].Answer.AnswerText), "official question mode returned a complete answer");
        }
        if (step.Entry == "production-StartMaterial-quick")
            Check(observer!.Snapshot().Any(item => item.Phase == fixture.Key && item.Operation == "content" && item.Status is >= 200 and < 300),
                fixture.Key + ": the fresh production entry completed a real configured content API request");
        step.CardIds = [child.Id]; await VerifyAsync(fixture.Key, child, drafts, http);
    }

    private static ClipDraft? FindDraft(EntryFixture fixture)
    {
        if (fixture.DraftId != null) return drafts.Find(fixture.DraftId) ?? throw new InvalidDataException("The original draft is missing; no replacement is created.");
        var matches = drafts.AllSessions().Where(draft => draft.Original.SourceTitle == fixture.Material.SourceTitle).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Duplicate fixture drafts exist; none is selected automatically.");
        if (matches.Length == 0) return null;
        fixture.DraftId = matches[0].Id; Save(); return matches[0];
    }
    private static ExcerptSession? FindExcerpt(EntryFixture fixture)
    {
        if (fixture.SessionId != null) return excerpts.Find(fixture.SessionId) ?? throw new InvalidDataException("The original excerpt session is missing.");
        var matches = excerpts.All().Where(session => session.Original.SourceTitle == fixture.Material.SourceTitle).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Duplicate excerpt fixtures exist.");
        if (matches.Length == 0) return null;
        fixture.SessionId = matches[0].Id; Save(); return matches[0];
    }
    private static ImageMaterialJob? FindImage(EntryFixture fixture)
    {
        if (fixture.ImageJobId != null) return images.Find(fixture.ImageJobId);
        var matches = images.Recent().Where(job => job.Questions.Length == 1 && job.Questions[0].Sha256 == manifest.ImageSha256).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Duplicate image fixture jobs exist.");
        if (matches.Length == 0) return null;
        fixture.ImageJobId = matches[0].Id; fixture.SessionId = matches[0].DestinationId; Save(); return matches[0];
    }
    private static void RequireNewLaunch(EntryFixture fixture)
    {
        if (mode == "--readback-only" || fixture.LaunchStartedAt != null)
            throw new InvalidDataException("This entry was already started or is read-only, but its saved identity is missing; no second entry is launched.");
        fixture.LaunchStartedAt = DateTimeOffset.UtcNow; Save();
    }
    private static bool LanguageVerified(ClipDraft parent, string intent) => intent == "language"
        ? parent.ModeCards.Count == 1 && LanguageModeCards.AllChildren(parent, drafts).All(child => child.ImportState == ImportState.Verified)
        : parent.FollowUpCards.Count == 1 && parent.FollowUpCards.Values.All(id => drafts.Find(id)?.ImportState == ImportState.Verified);
    private static bool ExcerptVerified(ExcerptSession session) => session.Cards.Count == 1 && excerpts.Cards.Find(session.Cards[0].Id)?.ImportState == ImportState.Verified;
    private static async Task AwaitEntry(Task? task, Func<bool> done, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < timeout)
        {
            NoWindows();
            if (task?.IsCompleted == true) { await task; if (!done()) throw new InvalidDataException("The quick entry completed without a verified import."); }
            await Task.Delay(40);
        }
        NoWindows();
        if (!done()) throw new TimeoutException("The original quick entry remains incomplete; saved IDs are retained.");
        if (task != null) await task;
        await Task.Delay(180); NoWindows(); Save();
    }
    private static async Task Step(string key, Func<StepObservation, Task> action)
    {
        phase = key;
        var step = new StepObservation { Key = key }; attempt.Steps.Add(step); Save();
        try { await action(step); NoWindows(); step.Passed = true; Console.WriteLine("PASS " + key + " production entry and independent readback"); }
        catch (Exception ex) { step.Error = SafeError(ex); throw; }
        finally { step.CompletedAt = DateTimeOffset.UtcNow; Save(); }
    }
    private static void OnIdle(object? sender, EventArgs args) => SampleWindows();
    private static void SampleWindows()
    {
        if (attempt == null || mode == "--prepare") return;
        attempt.WindowSamples++;
        var forms = Application.OpenForms.Cast<Form>().Concat(flows.SelectMany(flow =>
        {
            if (flow.GetType().GetField("forms", Flags)?.GetValue(flow) is not IEnumerable values) return Array.Empty<Form>();
            return values.Cast<object>().Select(value => value as Form ?? value.GetType().GetProperty("Value")?.GetValue(value) as Form).OfType<Form>();
        }));
        foreach (var form in forms.Where(form => !form.IsDisposed && form.Name != "knowledge-clipper.live.hidden-owner").Distinct())
            if (observedForms.Add(RuntimeHelpers.GetHashCode(form)))
                attempt.Windows.Add(new(phase, form.Name, form.GetType().Name, form.Visible, DateTimeOffset.UtcNow));
    }
    private static void NoWindows()
    {
        SampleWindows();
        if (attempt.Windows.Count > 0) throw new InvalidDataException("A preview window appeared in the success-only canary: " + attempt.Windows[0].Type);
    }
    private static void Status(string text) { attempt.Statuses.Add(new(phase, SafeText(text), DateTimeOffset.UtcNow)); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); attempt.Checks.Add(message); Console.WriteLine("PASS " + message); }
    private static void Save()
    {
        if (observer != null) attempt.Requests = observer.Snapshot();
        WorkflowJson.Save(manifestPath, manifest);
        WorkflowJson.Save(receiptPath, new { passed = attempt.Outcome == "PASS", outcome = attempt.Outcome, manifestPath, dataRoot,
            fixtureIds = manifest.Fixtures, identities = manifest.Identities, attempts = manifest.Attempts,
            scope = imageExcerptOnly
                ? "One fixed public PNG through production StartClipboardImage(png, excerpt, true), OriginalImageMode=true, live RemNote only. No system clipboard access, user settings writes, OCR, model calls or preview windows. Repeated attempts retain the same job/session/draft/Rem identities. Readback independently hashes the exact SDK-managed image file."
                : "Real production floating quick entries with official published prompts and live configured DeepSeek/Anki/RemNote. Hidden owner on an isolated Windows desktop. No system clipboard access, user settings writes or audio playback. Stable fixture identities are reused on every attempt." });
    }
    private static Type Type(string name) => desktop.GetType("KnowledgeClipper." + name, true)!;
    private static object New(string type, params object?[] args) => Type(type).GetConstructors(Flags).Single(ctor => ctor.GetParameters().Length == args.Length).Invoke(args);
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethods(Flags)
        .Single(candidate => candidate.Name == method && candidate.GetParameters().Length == args.Length).Invoke(target, args);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string SafeError(Exception ex) => ex.GetBaseException().GetType().Name + ": " + SafeText(ex.GetBaseException().Message);
    private static string SafeText(string text) => Regex.Replace(text, @"(?i)(?:Bearer\s+|sk-)[A-Za-z0-9_\-.]+", "[redacted]");
}

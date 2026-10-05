using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private const string ImageExcerptSchema = "knowledge-clipper.image-excerpt-entry-live.v1";
    private const string HostConfiguration = @"D:\Artifacts\KnowledgeClipper\standalone-remnote\host-config.json";

    private static async Task RunImageExcerptAsync()
    {
        if (!File.Exists(manifestPath))
        {
            if (mode != "--prepare") throw new InvalidDataException("Prepare the new image-only manifest before live execution; no fixture is created during live/readback.");
            manifest = PrepareImageExcerpt(); WorkflowJson.Save(manifestPath, manifest);
        }
        else manifest = WorkflowJson.Read<LiveManifest>(manifestPath);
        if (manifest.SchemaVersion != ImageExcerptSchema || manifest.DataRoot != dataRoot ||
            manifest.Fixtures.Count != 1 || !manifest.Fixtures.ContainsKey("image"))
            throw new InvalidDataException("This is not the dedicated single-image manifest; retired four-route fixtures are never reused.");
        if (Hash(manifest.ImagePath) != manifest.ImageSha256)
            throw new InvalidDataException("The fixed public PNG changed; it will not be regenerated.");
        attempt = new() { Number = manifest.Attempts.Count + 1, Mode = mode };
        manifest.Attempts.Add(attempt); Save();
        attempt.IsolatedDesktop = CurrentDesktopName();
        Check(attempt.IsolatedDesktop.StartsWith("KnowledgeClipperUiTests_", StringComparison.Ordinal), "the actual thread desktop is the isolated test desktop");
        Check(manifest.Settings.OriginalImageMode == true && manifest.Settings.QuickExcerpt && !manifest.Settings.GlobalSelection &&
            !manifest.Settings.ClipboardImageDotEnabled && !manifest.Settings.LanguageAutoRead && !manifest.Settings.TypedInputAutoRead &&
            !manifest.Settings.SpeechAutoPlay, "image-only settings enable original quick excerpt and disable capture listeners and audio");
        var settingsFile = Path.Combine(dataRoot, "language-settings.json");
        Check(File.Exists(settingsFile) && JsonSerializer.Serialize(WorkflowJson.Read<LanguageSettings>(settingsFile), WorkflowJson.Options) ==
            JsonSerializer.Serialize(manifest.Settings, WorkflowJson.Options), "the retained isolated settings match the frozen manifest");
        if (mode == "--prepare") { attempt.Outcome = "PREPARED_NO_EXTERNAL_CALLS"; Save(); return; }

        desktop = Assembly.LoadFrom(desktopPath);
        attempt.DesktopAssembly = desktop.Location; attempt.DesktopSha256 = Hash(desktop.Location);
        attempt.WorkflowSha256 = Hash(typeof(LanguageSettings).Assembly.Location);
        Check(attempt.DesktopSha256 == Hash(desktopPath) && attempt.WorkflowSha256 ==
            Hash(Path.Combine(Path.GetDirectoryName(desktopPath)!, "KnowledgeClipper.Workflow.dll")), "runtime desktop and workflow match the requested production package exactly");
        observer = new(() => phase);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(180) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var health = await new RemNoteConnection(http, manifest.Settings.RemNoteSdkEndpoint).HealthAsync(deadline.Token);
        WorkflowJson.Save(Path.Combine(dataRoot, "readbacks", "attempt-" + attempt.Number.ToString("D3"), "health.json"), health);
        Check(health.GetProperty("connected").GetBoolean() && health.GetProperty("supported").GetBoolean() &&
            health.GetProperty("plainStructureSupported").GetBoolean() &&
            (mode == "--readback-only" || health.GetProperty("acceptWriteOperations").GetBoolean()), "the updated independent SDK is connected with plain-structure support");
        Check(observer.Snapshot().Any(item => item.Operation == "health" && item.Status == 200), "request observation is active before the production entry");
        drafts = new(dataRoot); excerpts = new(dataRoot); images = new(dataRoot);
        using var watch = new System.Windows.Forms.Timer { Interval = 20 };
        watch.Tick += (_, _) => SampleWindows(); watch.Start(); Application.Idle += OnIdle;
        using var owner = new Form { Name = "knowledge-clipper.live.hidden-owner" }; _ = owner.Handle;
        try
        {
            await Step("image", async step =>
            {
                var fixture = manifest.Fixtures["image"];
                var job = FindImage(fixture);
                if (mode != "--readback-only")
                {
                    var initial = Type("UserSettings").GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
                    var language = New("LanguageDesktopFlow", owner, dataRoot, (Action<string>)Status, initial); flows.Add(language);
                    var excerpt = New("ExcerptDesktopFlow", owner, dataRoot, manifest.Settings, (Action<string>)Status); flows.Add(excerpt);
                    var config = Activator.CreateInstance(Type("AppConfig"), true)!;
                    Type("AppConfig").GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
                    Type("AppConfig").GetProperty("MineruScriptPath")!.SetValue(config, manifest.Settings.MineruScriptPath);
                    var image = New("ImageDesktopFlow", owner, config, language, excerpt, null!); flows.Add(image);
                    if (job == null)
                    {
                        RequireNewLaunch(fixture); step.Entry = "production-StartClipboardImage-png-excerpt-quick";
                        Call(image, "StartClipboardImage", File.ReadAllBytes(manifest.ImagePath), "excerpt", true);
                        job = FindImage(fixture) ?? throw new InvalidDataException("The production image entry did not persist its original job.");
                    }
                    else
                    {
                        step.Entry = "resume-original-image-job-and-session";
                        // An interrupted run never launches a second image entry. Processing resumes on the saved job.
                        if (job.DestinationId == null) Call(image, "RunQuickAsync", job);
                    }
                    var session = job.DestinationId == null ? null : excerpts.Find(job.DestinationId);
                    if (session != null && step.Entry.StartsWith("resume", StringComparison.Ordinal) && !ExcerptVerified(session))
                        await AwaitEntry((Task)Call(excerpt, "CompleteQuickAsync", session)!, () => ExcerptVerified(session), TimeSpan.FromMinutes(8));
                    else await AwaitEntry(null, () =>
                    {
                        var saved = images.Find(job.Id);
                        return saved.DestinationId != null && excerpts.Find(saved.DestinationId) is { } current && ExcerptVerified(current);
                    }, TimeSpan.FromMinutes(8));
                    job = images.Find(job.Id);
                }
                else step.Entry = "readback-original-image-job-and-session";
                if (job == null) throw new InvalidDataException("The original image job is missing; readback never creates it.");
                Check(job.Entry == "excerpt" && job.Route == "excerpt" && job.Quick && job.Kind == MaterialKind.OriginalImages &&
                    job.Images.Length == 1 && job.Questions.Length == 1 && job.Answers.Length == 0 && !ImageEntryRouting.NeedsOcr(job),
                    "the explicit quick excerpt route retains exactly one original image");
                Check(job.Answer == null && job.Review == null && job.Ocr.All(item => item.RawText == null && item.RawManifest == null &&
                    item.Cleanup == null && item.Review == null) && !Directory.Exists(Path.Combine(images.DirectoryFor(job.Id), "ocr")) &&
                    !Directory.Exists(Path.Combine(images.DirectoryFor(job.Id), "content")), "no OCR, cleanup, review or answer artifacts were created");
                var retained = excerpts.Find(job.DestinationId ?? throw new InvalidDataException("Missing original excerpt destination."))
                    ?? throw new InvalidDataException("Missing original excerpt session.");
                fixture.SessionId = retained.Id; Save();
                Check(retained.Cards.Count == 1 && retained.ImageMaterialId == job.Id, "one excerpt draft belongs to the original image job");
                var draft = ExcerptCards.Get(retained, excerpts, retained.Cards.Single().Id);
                fixture.DraftId = draft.Id; step.CardIds = [draft.Id]; Save();
                Check(draft.Excerpt is { Front.Length: 0, Back.Length: 0, FrontImages.Length: 1, BackImages.Length: 0 } &&
                    draft.Excerpt.FrontImages[0].Sha256 == manifest.ImageSha256 && draft.Content == null && draft.PreviewContent == null &&
                    draft.Conversation.Count == 0 && draft.AudioPath == null, "the sole draft is the fixed PNG with an empty back and no generated content");
                await VerifyAsync("image", draft, excerpts.Cards, http);
                VerifyManagedImageBytes();
                Check(images.Recent().Count() == 1 && excerpts.All().Count() == 1 && excerpts.Cards.AllSessions().Count() == 1 &&
                    !drafts.AllSessions().Any(), "only one image job, excerpt session and card draft exist in the isolated run");
                var history = HistoryRecords.Read(drafts, excerpts);
                Check(history.Length == 1 && history[0].Id == retained.Id && !history[0].CanPlay &&
                    HistoryRecords.ActionText(history[0], drafts, excerpts) == "查看", "history contains one verified non-playable image excerpt under its original session");
                WorkflowJson.Save(Path.Combine(dataRoot, "readbacks", "attempt-" + attempt.Number.ToString("D3"), "history.json"),
                    history.Select(item => new { item.Key, item.Kind, item.CanPlay, action = HistoryRecords.ActionText(item, drafts, excerpts) }).ToArray());
            });
            NoWindows(); attempt.Requests = observer.Snapshot();
            Check(attempt.Requests.All(item => (item.Operation is "health" or "export" or "readback" or "finalize-media" or "finalize-spacing") &&
                (item.Host is "127.0.0.1" or "localhost") && item.Port == 43111), "all observed requests use the independent RemNote service; zero model, OCR or Anki requests");
            if (mode == "--readback-only") Check(attempt.Requests.All(item => item.Operation is "health" or "readback"),
                "pure readback sends no export or finalization writes");
            Check(manifest.Identities.Count == 1 && manifest.Identities["image"].ContentIds.Length == 1 &&
                manifest.Identities["image"].ReviewCardIds.Length == 0, "one ordinary RemNote content node and zero review cards are retained");
            attempt.Outcome = "PASS"; Save();
        }
        finally { Application.Idle -= OnIdle; watch.Stop(); }
    }

    private static LiveManifest PrepareImageExcerpt()
    {
        // These providers are unreachable by design: a routing regression must not call OCR or a model.
        var user = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
        var settings = user with { OriginalImageMode = true, QuickExcerpt = true, GlobalSelection = false, ClipboardImageDotEnabled = false,
            LanguageAutoRead = false, TypedInputAutoRead = false, SpeechAutoPlay = false, QuickLanguage = false, QuickQuestion = false,
            QuickScreenshot = false, ApiBaseUrl = "http://127.0.0.1:1/model-disabled", ContentMaxRetries = 0, ContentTimeoutSeconds = 1,
            MineruScriptPath = Path.Combine(dataRoot, "providers-disabled", "mineru-disabled.py"),
            ExcerptModesDirectory = Path.Combine(dataRoot, "published-modes"), ModeDestinations = new(user.ModeDestinations, StringComparer.Ordinal) };
        settings.Validate();
        if (ExportRouting.ForMode(settings, ExportRouting.BuiltinModes.Single(item => item.Id == "excerpt-manual")) != ExportDestination.RemNote)
            throw new InvalidDataException("The current manual-excerpt destination must be RemNote for this authorized canary.");
        var pngPath = Path.Combine(dataRoot, "public-single-image-excerpt.png");
        if (File.Exists(pngPath) || Directory.Exists(settings.ExcerptModesDirectory))
            throw new InvalidDataException("Fixture files already exist without their manifest; they will not be overwritten.");
        Directory.CreateDirectory(settings.ExcerptModesDirectory);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(projectRoot, "modes")))
            File.Copy(path, Path.Combine(settings.ExcerptModesDirectory, Path.GetFileName(path)));
        using (var bitmap = new Bitmap(780, 210))
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
            using var title = new Font("Segoe UI", 23); using var detail = new Font("Segoe UI", 20);
            graphics.DrawString("PUBLIC SINGLE IMAGE EXCERPT", title, Brushes.DarkBlue, 24, 28);
            graphics.DrawString("3 x 4 = 12", detail, Brushes.Black, 24, 108);
            bitmap.Save(pngPath, ImageFormat.Png);
        }
        var result = new LiveManifest { SchemaVersion = ImageExcerptSchema, DataRoot = dataRoot, Settings = settings,
            Configuration = new("", []), ImagePath = pngPath, ImageSha256 = Hash(pngPath) };
        result.Fixtures["image"] = new() { Key = "image", Material = new("Public single image excerpt.", null, "unavailable", null,
            "KnowledgeClipper public image excerpt " + result.RunId, 0, result.CreatedAt) };
        WorkflowJson.Save(Path.Combine(dataRoot, "language-settings.json"), settings); return result;
    }

    private static void VerifyManagedImageBytes()
    {
        var directory = Path.Combine(dataRoot, "readbacks", "attempt-" + attempt.Number.ToString("D3"));
        using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "image.json")));
        var media = raw.RootElement.GetProperty("cards")[0].GetProperty("nodes")[0].GetProperty("media")[0];
        var locator = media.GetProperty("uri").GetString() ?? throw new InvalidDataException("The SDK returned an empty image locator.");
        if (!locator.StartsWith("%LOCAL_FILE%", StringComparison.Ordinal)) throw new InvalidDataException("The image is not in managed RemNote storage.");
        var token = Uri.UnescapeDataString(locator["%LOCAL_FILE%".Length..]);
        if (string.IsNullOrEmpty(token) || Path.GetFileName(token) != token || token.Contains(':') || token.Contains('\0') || token is "." or "..")
            throw new InvalidDataException("The managed image token is not a safe basename.");
        using var config = JsonDocument.Parse(File.ReadAllText(HostConfiguration));
        var matches = new List<object>();
        foreach (var root in config.RootElement.GetProperty("mediaRoots").EnumerateArray().Select(item => Path.GetFullPath(item.GetString()!)))
        {
            var path = Path.GetFullPath(Path.Combine(root, token));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The managed image path escaped its configured storage root.");
            if (!File.Exists(path)) continue;
            var length = new FileInfo(path).Length;
            if (length is < 24 or > 33554432) throw new InvalidDataException("Managed image size is outside the PNG fixture boundary.");
            var bytes = File.ReadAllBytes(path); var actualHash = ImageAsset.Hash(bytes);
            Check(actualHash == manifest.ImageSha256 && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(manifest.ImagePath)) &&
                media.GetProperty("sha256").GetString() == actualHash && media.GetProperty("sizeBytes").GetInt64() == bytes.Length,
                "independently read RemNote-managed PNG bytes exactly equal the fixed original image");
            matches.Add(new { path, sha256 = actualHash, sizeBytes = bytes.Length });
        }
        Check(matches.Count > 0, "the SDK image locator resolves inside configured RemNote media storage");
        WorkflowJson.Save(Path.Combine(directory, "managed-image-bytes.json"), new { locator, matches });
    }

    private static string CurrentDesktopName()
    {
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        var name = new StringBuilder(512);
        if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int size, out int needed);
}

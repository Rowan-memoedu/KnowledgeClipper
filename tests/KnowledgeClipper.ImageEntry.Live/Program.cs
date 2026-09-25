using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private const string DataRoot = @"D:\Caches\KnowledgeClipper\image-entry-live-20260919";
    private const string Artifacts = @"D:\Artifacts\KnowledgeClipper\image-entry-live-20260919";
    private const string RootTitle = "KnowledgeClipper验收 · 原图路线 · 20260919";
    private static string QuestionRootTitle => manifest.QuestionRecovery == null ? RootTitle : "零碎知识剪藏";
    private const string Deck = "KnowledgeClipper验收::原图路线20260919";
    private static readonly DraftStore Drafts = new(DataRoot);
    private static readonly ImageMaterialStore Images = new(DataRoot);
    private static Manifest manifest = null!;
    private static Attempt attempt = null!;
    private static string project = "";
    private static readonly string ManifestPath = Path.Combine(DataRoot, "manifest.json");
    private static string Receipt => Path.Combine(Artifacts, "attempt-" + attempt.Number.ToString("D3") + ".json");
    private static void Save() { WorkflowJson.Save(ManifestPath, manifest); WorkflowJson.Save(Receipt, attempt); }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
        attempt.Checks.Add(message); Save(); Console.WriteLine("PASS " + message);
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[1] is not ("prepare-questions" or "recover-questions" or "prepare-language" or "recover-language" or "import-questions" or "import-language" or "quarantine-language-failure" or "readback-language" or "readback"))
            throw new ArgumentException("ImageEntry.Live <project-root> prepare-questions|recover-questions|prepare-language|recover-language|import-questions|import-language|quarantine-language-failure|readback-language|readback");
        project = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(DataRoot); Directory.CreateDirectory(Artifacts);
        using var exclusive = new FileStream(Path.Combine(DataRoot, "live-run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        manifest = File.Exists(ManifestPath) ? WorkflowJson.Read<Manifest>(ManifestPath) : CreateManifest();
        if (manifest.Schema != "image-entry-live.20260919" || manifest.DataRoot != DataRoot)
            throw new InvalidDataException("Existing fixture identity is incompatible; it must not be regenerated.");
        attempt = new Attempt { Number = ++manifest.Attempts, Phase = args[1], WorkflowSha256 = ImageAsset.Hash(File.ReadAllBytes(typeof(ClipDraft).Assembly.Location)) };
        Save();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        using var trace = new LiveTrace(Path.Combine(DataRoot, "responses", "attempt-" + attempt.Number.ToString("D3")), attempt, Save,
            args[1].StartsWith("readback", StringComparison.Ordinal));
        using var http = new HttpClient(trace) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            foreach (var image in manifest.PublicImages.Values) image.ReadVerified();
            if (args[1] is "prepare-questions" or "recover-questions") await PrepareQuestionsAsync(http, cancellation.Token, args[1] == "recover-questions");
            else if (args[1] is "prepare-language" or "recover-language") await PrepareLanguageAsync(http, cancellation.Token, args[1] == "recover-language");
            else if (args[1] == "quarantine-language-failure") await QuarantineLanguageFailureAsync(http, cancellation.Token);
            else if (args[1] == "import-questions")
            {
                var parent = Drafts.Find(manifest.QuestionDraftId!) ?? throw new InvalidDataException("Prepare real question content first.");
                Check(parent.FollowUpCards.Count == 2 && QuestionChildren().All(d => d.ConfirmedRemNote != null), "two original question plans already exist");
                await QuestionCards.ImportAsync(parent, [0, 1], Drafts, new CardImportRouter(http), Save, cancellation.Token);
                foreach (var child in QuestionChildren()) await VerifyAsync(child, http, cancellation.Token);
            }
            else if (args[1] == "import-language")
            {
                var parent = Drafts.Find(manifest.LanguageDraftId!) ?? throw new InvalidDataException("Prepare real OCR and language content first.");
                var children = LanguageModeCards.Children(parent, Drafts);
                Check(children.Length == 1 && children[0].ConfirmedNote != null && children[0].Practice == PracticeChoice.Reading, "one original-image reading plan already exists");
                await LanguageModeCards.ImportAsync(parent, [children[0].ModeId!], Drafts, new CardImportRouter(http), Save, cancellation.Token);
                await VerifyAsync(Drafts.Find(children[0].Id)!, http, cancellation.Token);
            }
            else if (args[1] == "readback-language") await VerifyAsync(LanguageChildren().Single(), http, cancellation.Token);
            else
            {
                var all = QuestionChildren().Concat(LanguageChildren()).ToArray();
                Check(all.Length == 3, "readback covers the same two visual-question cards and one language card");
                var questions = QuestionChildren();
                Check(questions.Length == 2 && questions.All(draft => draft.RemNoteResult is { RemIds.Length: 1, CardIds.Length: 1 }) &&
                    questions.SelectMany(draft => draft.RemNoteResult!.RemIds).Distinct().Count() == 2 &&
                    questions.SelectMany(draft => draft.RemNoteResult!.CardIds).Distinct().Count() == 2,
                    "the two frozen question sources bind exactly two distinct front nodes and review cards");
                foreach (var child in all) await VerifyAsync(child, http, cancellation.Token);
            }
            CheckRecoveryPreserved();
            attempt.Outcome = args[1].StartsWith("prepare-") || args[1].StartsWith("recover-") ? "PREPARED_REAL_CONTENT_NO_EXPORT" :
                args[1] == "quarantine-language-failure" ? "PASS_OWN_FAILED_SAMPLE_PRESERVED_AND_SUSPENDED" : "PASS_REAL_EXPORT_AND_READBACK";
            return 0;
        }
        catch (Exception ex)
        {
            attempt.Error = ex.GetType().Name + ": " + ex.Message;
            attempt.Outcome = "FAILED_RETAINED_SAME_IDENTITIES";
            Console.Error.WriteLine(attempt.Error); return 1;
        }
        finally { attempt.CompletedAt = DateTimeOffset.UtcNow; Save(); Console.WriteLine(attempt.Outcome + " " + Receipt); }
    }

    private static Manifest CreateManifest()
    {
        if (Directory.EnumerateFileSystemEntries(DataRoot).Any(path => Path.GetFileName(path) != "live-run.lock"))
            throw new InvalidDataException("The new fixture root is not empty and has no manifest; do not overwrite or resurrect old fixtures.");
        var user = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
        using var app = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "appsettings.json")));
        var settings = user with { AnkiRootDeck = Deck, AnkiDeck = Deck, RemNoteRootTitle = RootTitle, RemNoteRootId = null,
            ContextEnabled = false, Practice = PracticeChoice.Reading, OriginalImageMode = true, GlobalSelection = false,
            ClipboardImageDotEnabled = false, LanguageAutoRead = false, TypedInputAutoRead = false, SpeechAutoPlay = false,
            ModeDestinations = new() { ["question"] = ExportDestination.RemNote, ["language-en"] = ExportDestination.Anki },
            MineruScriptPath = string.IsNullOrWhiteSpace(user.MineruScriptPath) ? app.RootElement.GetProperty("MineruScriptPath").GetString()! : user.MineruScriptPath };
        settings.Validate();
        var configuration = ModeConfigurationLoader.Load(Path.Combine(project, "modes"));
        var result = new Manifest { DataRoot = DataRoot, Settings = settings, Configuration = configuration };
        foreach (var key in new[] { "geometry", "lead" })
        {
            var path = Path.Combine(DataRoot, "public-" + key + ".png");
            using var bitmap = new Bitmap(key == "geometry" ? 1000 : 600, key == "geometry" ? 600 : 180);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                using var font = new Font("Segoe UI", key == "geometry" ? 26 : 54);
                if (key == "lead") g.DrawString("lead", font, Brushes.Black, 210, 36);
                else
                {
                    using var pen = new Pen(Color.Black, 4); g.DrawRectangle(pen, 220, 130, 450, 350);
                    g.DrawString("9 cm", font, Brushes.Black, 425, 70); g.DrawString("7 cm", font, Brushes.Black, 690, 260);
                    g.DrawString("Find the perimeter and area.", font, Brushes.Black, 180, 500);
                }
            }
            bitmap.Save(path, ImageFormat.Png); result.PublicImages[key] = new(path, ImageAsset.Hash(File.ReadAllBytes(path)), "question", 1);
        }
        WorkflowJson.Save(ManifestPath, result); return result;
    }

    private static ImageMaterialJob Job(string key, string intent)
    {
        if (manifest.Jobs.TryGetValue(key, out var id)) return Images.Find(id);
        var job = Images.Create([(manifest.PublicImages[key].Path, "question")], MaterialKind.OriginalImages, manifest.Settings,
            Path.Combine(project, "modes"), Path.Combine(DataRoot, "modes"));
        ImageEntryRouting.SetSingle(job, intent, false); Images.Save(job); manifest.Jobs[key] = job.Id; Save(); return job;
    }
    private static ClipDraft NewDraft(CaptureMaterial material, string intent) => new()
    {
        Original = material, Material = material, Configuration = manifest.Configuration, Settings = manifest.Settings, Practice = PracticeChoice.Reading,
        ModeId = intent, Route = new(intent, intent, false, intent == "language"),
        Input = new(material.VisualQuestion ? "image-visual" : "image-ocr", intent, ExplicitQuestion: intent == "question" ? "解释：" + material.SelectedText : null)
    };
    private static bool Number(string text, int value) => Regex.IsMatch(text, @"(?<!\d)" + value + @"(?!\d)");
    // The controlled HTML reader may preserve a trailing soft line break from
    // the image container. Require one actual image and no other visible front content.
    private static bool ImageOnlyFront(RemNoteCardPlan card, ImageAsset image) =>
        card.Front.Count(part => part.Kind == "image" && part.Sha256 == image.Sha256) == 1 &&
        card.Front.All(part => part.Kind == "image" && part.Sha256 == image.Sha256 ||
            part.Kind == "text" && string.IsNullOrWhiteSpace(part.Text));
    private static async Task PrepareQuestionsAsync(HttpClient http, CancellationToken ct, bool recover = false)
    {
        var job = Job("geometry", "question");
        var parent = recover ? RecoveryQuestionDraft(job) : manifest.QuestionDraftId == null ? NewDraft(ImageEntryRouting.Material(job,
            "请解释这张图片的主要内容；若是题目，请给出解答。"), "question") : Drafts.Find(manifest.QuestionDraftId)!;
        Drafts.Save(parent); manifest.QuestionDraftId = parent.Id; Save();
        Check(!ImageEntryRouting.NeedsOcr(job) && parent.Original.VisualQuestion && parent.Original.FrontImage != null,
            "ordinary original-image question uses the visual model directly without OCR");
        var content = new ContentModule(http, () => ContentCredentials.Resolve());
        if (parent.Content?.Answer?.Answered != true)
        {
            Console.WriteLine("RUN visual question 1 with the frozen public rectangle PNG");
            parent.Content = await content.ProcessAsync(parent.Material, "question", parent.Configuration, parent.Settings, ct, parent.QuestionText);
            var answer = parent.Content.Answer ?? throw new InvalidDataException("No first answer.");
            if (parent.Conversation.Count == 0) parent.Conversation.Add(new(parent.QuestionText, answer));
            else parent.Conversation[0] = new(parent.QuestionText, answer);
            Drafts.Save(parent);
        }
        var first = parent.Conversation[0].Answer;
        Check(first.Answered && Number(first.AnswerText!, 32) && Number(first.AnswerText!, 63), "real visual answer identifies the rectangle perimeter 32 and area 63");
        const string next = "如果图中的长增加 2 cm，宽保持不变，面积和周长各增加多少？请说明计算依据。";
        if (parent.Conversation.Count < 2 || !parent.Conversation[1].Answer.Answered)
        {
            Console.WriteLine("RUN visual follow-up using the same PNG and first generated answer");
            parent.PendingFollowUp = next; Drafts.Save(parent);
            var answer = await content.AnswerAsync(parent.Material, parent.PreviewContent, parent.Conversation.Take(1).ToArray(), next,
                parent.Configuration, parent.Settings, ct, currentPreview: new(parent.Original.SelectedText, null, parent.Conversation.Take(1).ToArray()));
            if (parent.Conversation.Count == 1) parent.Conversation.Add(new(next, answer)); else parent.Conversation[1] = new(next, answer);
            parent.PendingFollowUp = null; Drafts.Save(parent);
        }
        var second = parent.Conversation[1].Answer;
        Check(second.Answered && Number(second.AnswerText!, 14) && Number(second.AnswerText!, 4), "real visual follow-up derives area increase 14 and perimeter increase 4");
        await QuestionCards.ImportAsync(parent, [0, 1], Drafts, new PlanOnly(ExportDestination.RemNote), Save, ct);
        foreach (var child in QuestionChildren())
            Check(child.ImportState == ImportState.Ready && child.ConfirmedRemNote?.RootTitle == QuestionRootTitle &&
                ImageOnlyFront(child.ConfirmedRemNote.Cards.Single(), job.Questions.Single()) && child.ConfirmedNote!.Media.Single().Sha256 == job.Questions.Single().Sha256,
                "real generated question freezes an image-only front and RemNote plan without writing " + child.Id);
        job.DestinationId = parent.Id; job.DestinationKind = "language"; Images.Save(job);
    }

    private static async Task PrepareLanguageAsync(HttpClient http, CancellationToken ct, bool recover = false)
    {
        var job = Job("lead", "language");
        var content = new ContentModule(http, () => ContentCredentials.Resolve());
        var preparation = new ImagePreparation(Images, new MineruCli(), content);
        Console.WriteLine("RUN retained MinerU direct OCR, cleanup and review for public lead PNG");
        var text = await preparation.TextAsync(job, Console.WriteLine, ct);
        Check(job.Ocr.Single().RawText?.Contains("lead", StringComparison.OrdinalIgnoreCase) == true && job.Ocr.Single().Review?.Verdict == "pass",
            "real MinerU output and independently reviewed cleanup retain lead");
        var material = ImageEntryRouting.Material(job, text);
        Check(material.SelectedText == "lead", "OCR language material contains only the visible word, without Markdown heading syntax");
        var parent = recover ? RecoveryDraft(job, material) : manifest.LanguageDraftId == null ? NewDraft(material, "language") : Drafts.Find(manifest.LanguageDraftId)!;
        Check(parent.Material.SelectedText == "lead", "the retained language draft also uses the visible word as its exact learning target");
        Drafts.Save(parent); manifest.LanguageDraftId = parent.Id; Save();
        var modes = LanguageModes.Select(parent.Configuration, parent.Material, parent.Practice);
        Check(modes.Length == 1 && modes[0].Practice == PracticeChoice.Reading, "original-image language selects exactly the English reading mode");
        var children = LanguageModeCards.Ensure(parent, modes, Drafts);
        var child = children.Single();
        if (child.Content?.Fields is not { IsUnresolved: false })
        {
            Console.WriteLine("RUN real language content for original-image reading card");
            child.Content = await content.ProcessAsync(child.Material, child.ModeId!, child.Configuration, child.Settings, ct); Drafts.Save(child);
        }
        PublishedLanguage.Validate(modes[0], child.Material, child.Content!.Fields!);
        Check(!child.Content.Fields!.IsUnresolved, "real English content satisfies the published reading schema");
        Check(child.Content.Fields.Lemma == "lead" &&
            !PublishedLanguage.EffectiveSentence(child.Material, child.Content.Fields).Contains("##", StringComparison.Ordinal),
            "the generated lemma is lead and its example contains no OCR heading marker");
        await LanguageModeCards.ImportAsync(parent, [child.ModeId!], Drafts, new PlanOnly(ExportDestination.Anki), Save, ct);
        child = Drafts.Find(child.Id)!;
        Check(child.ConfirmedNote!.Ordinals.SequenceEqual(new[] { 0 }) && child.ConfirmedNote.Media.Single().Sha256 == job.Questions.Single().Sha256 &&
            child.ConfirmedNote.Fields["Front"].Contains(job.Questions.Single().FileName) && child.ConfirmedNote.Deck.StartsWith(Deck + "::", StringComparison.Ordinal),
            "real OCR language result freezes one image-front reading card in the isolated Anki deck");
        job.DestinationId = parent.Id; job.DestinationKind = "language"; Images.Save(job);
    }
    private static ClipDraft[] QuestionChildren() => manifest.QuestionDraftId == null ? [] :
        Drafts.Find(manifest.QuestionDraftId)!.FollowUpCards.OrderBy(pair => pair.Key).Select(pair => Drafts.Find(pair.Value)!).ToArray();
    private static ClipDraft[] LanguageChildren() => manifest.LanguageDraftId == null ? [] : LanguageModeCards.Children(Drafts.Find(manifest.LanguageDraftId)!, Drafts);
    private sealed class PlanOnly(ExportDestination destination) : ICardImporter
    {
        public ExportDestination? FixedDestination => destination;
        public Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); if (draft.ConfirmedNote == null) throw new InvalidDataException("No frozen plan."); return Task.CompletedTask; }
    }
}

internal sealed class Manifest
{
    public string Schema { get; init; } = "image-entry-live.20260919";
    public required string DataRoot { get; init; }
    public required LanguageSettings Settings { get; init; }
    public required ContentConfiguration Configuration { get; init; }
    public Dictionary<string, ImageAsset> PublicImages { get; init; } = [];
    public Dictionary<string, string> Jobs { get; init; } = [];
    public string? QuestionDraftId { get; set; }
    public string? LanguageDraftId { get; set; }
    public LanguageRecovery? LanguageRecovery { get; set; }
    public QuestionRecovery? QuestionRecovery { get; set; }
    public int Attempts { get; set; }
    public Dictionary<string, string> Identities { get; init; } = [];
}
internal sealed class Attempt
{
    public int Number { get; init; }
    public required string Phase { get; init; }
    public required string WorkflowSha256 { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Outcome { get; set; } = "RUNNING";
    public List<string> Checks { get; } = [];
    public List<object> Requests { get; } = [];
    public string? Error { get; set; }
}

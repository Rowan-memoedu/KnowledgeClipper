using System.Security.Cryptography;
using System.Text;
using KnowledgeClipper.Workflow;

internal sealed record LiveOptions(string ProjectRoot, string DataRoot, string Receipt, string Images, bool Live, string SdkEndpoint, string AnkiEndpoint);
internal sealed class FixtureManifest
{
    public string Version { get; set; } = "remnote-live-fixtures.v2";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string DataRoot { get; init; }
    public required LanguageSettings Settings { get; init; }
    public Dictionary<string, string> Ids { get; set; } = new(StringComparer.Ordinal);
    public ImageAsset[] Images { get; set; } = [];
}

internal sealed class FixtureSet(LiveOptions options)
{
    internal static readonly string[] ExcerptKeys = ["raw", "manual-basic", "single-cloze", "merged-cloze", "math", "math-cloze", "images", "images-fresh", "image-front-only", "image-answer"];
    internal static readonly string[] DirectKeys = ["translation", "independent-cloze"];
    internal static readonly string[] LanguageParents = ["language-en", "language-zh", "anki-language"];
    internal DraftStore Drafts { get; } = new(options.DataRoot);
    internal ExcerptStore Excerpts { get; } = new(options.DataRoot);
    internal FixtureManifest Manifest { get; private set; } = null!;
    private string ManifestPath => Path.Combine(options.DataRoot, "fixture-manifest.json");
    internal ClipDraft Draft(string key) => Drafts.Find(Manifest.Ids[key]) ?? throw new InvalidDataException("Missing fixture: " + key);
    internal ExcerptSession Excerpt => Excerpts.Find(Manifest.Ids["excerpt-session"]) ?? throw new InvalidDataException("Missing excerpt session.");
    internal ExcerptSession OcrExcerpt => Excerpts.Find(Manifest.Ids["ocr-session"]) ?? throw new InvalidDataException("Missing OCR excerpt session.");
    internal ClipDraft ExcerptCard(string key) => Excerpts.Cards.Find(Manifest.Ids[key]) ?? throw new InvalidDataException("Missing excerpt card: " + key);
    internal void SaveManifest() => WorkflowJson.Save(ManifestPath, Manifest);
    internal string StableId(string key) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("KnowledgeClipper.RemNoteLive.v2:" + options.DataRoot.ToUpperInvariant() + ":" + key)).AsSpan(0, 16)).ToString("N");
    internal IEnumerable<(string Key, ClipDraft Draft)> LanguageCards(string parentKey) => LanguageModeCards.Children(Draft(parentKey), Drafts)
        .Select(child => (parentKey == "anki-language" ? "anki-language" : child.ModeId!, child));
    private CaptureMaterial Material(string text) => new(text, null, "unavailable", null, "Public SDK exporter fixture; no LLM", 0, Manifest.CreatedAt);
    private static readonly string[] ImageNames = ["question-1.png", "question-2.png", "answer-1.png", "answer-2.png"];

    internal async Task PrepareAsync()
    {
        if (File.Exists(ManifestPath))
        {
            Manifest = WorkflowJson.Read<FixtureManifest>(ManifestPath);
            if (Manifest.Version != "remnote-live-fixtures.v2" || Manifest.DataRoot != options.DataRoot ||
                RemNoteConnection.ResolveEndpoint(Manifest.Settings.RemNoteSdkEndpoint) != RemNoteConnection.ResolveEndpoint(options.SdkEndpoint) ||
                Manifest.Settings.AnkiEndpoint != options.AnkiEndpoint)
                throw new InvalidDataException("Existing fixture identity/settings differ; use the original arguments rather than rewriting a saved run.");
        }
        else
        {
            Manifest = new() { DataRoot = options.DataRoot, Settings = new LanguageSettings {
                RemNoteSdkEndpoint = options.SdkEndpoint, AnkiEndpoint = options.AnkiEndpoint, RemNoteRootTitle = "零碎知识剪藏",
                AnkiRootDeck = "KnowledgeClipper验收::RemNote SDK", AnkiDeck = "KnowledgeClipper验收::RemNote SDK",
                AnkiModel = "KnowledgeClipper SDK 导出验收 · 语言", GlobalSelection = false, SpeechAutoPlay = false,
                TypedInputAutoRead = false, LanguageAutoRead = false, QuestionCardIncludeContext = true } };
            Manifest.Settings.Validate(); SaveManifest();
        }
        var images = new List<ImageAsset>();
        for (var index = 0; index < ImageNames.Length; index++)
        {
            var destination = Path.Combine(options.DataRoot, "media", ImageNames[index]);
            if (!File.Exists(destination)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(Path.Combine(options.Images, ImageNames[index]), destination); }
            var image = new ImageAsset(destination, ImageAsset.Hash(File.ReadAllBytes(destination)), index < 2 ? "question" : "answer", index % 2 + 1);
            image.ReadVerified(); images.Add(image);
        }
        if (Manifest.Images.Length > 0 && !Manifest.Images.SequenceEqual(images)) throw new InvalidDataException("Saved fixture images changed; restore the originals.");
        Manifest.Images = images.ToArray();
        Manifest.Ids.TryAdd("excerpt-session", StableId("excerpt-session")); SaveManifest();
        var excerpt = Excerpts.Find(Manifest.Ids["excerpt-session"]);
        if (excerpt == null)
        {
            var original = Material("Mercury and Venus orbit the Sun.\nEarth is another planet.");
            excerpt = new() { Id = Manifest.Ids["excerpt-session"], Original = original, Settings = Manifest.Settings, ManualFront = original.SelectedText };
            Excerpts.Save(excerpt);
        }
        ClipDraft Add(string key, ExcerptFields fields, ModeSnapshot? mode = null)
        {
            var link = excerpt.Cards.SingleOrDefault(card => card.Label == key);
            var draft = link == null ? ExcerptCards.Add(excerpt, Excerpts, fields, key, mode) : ExcerptCards.Get(excerpt, Excerpts, link.Id);
            Manifest.Ids[key] = draft.Id; SaveManifest(); return draft;
        }
        Add("raw", new(excerpt.Original.SelectedText, ""));
        Add("manual-basic", new("A rectangle has width 7 cm.\nIts length is 9 cm.", "Perimeter: 32 cm.\nArea: 63 square cm."));
        var single = excerpt.Cards.SingleOrDefault(card => card.Label == "挖空");
        var singleDraft = single == null ? ExcerptCards.Cloze(excerpt, Excerpts, 0, 7) : ExcerptCards.Get(excerpt, Excerpts, single.Id);
        Manifest.Ids["single-cloze"] = singleDraft.Id; SaveManifest();
        var firstRanges = new[] { new ClozeRange(0, 7) };
        var secondRanges = new[] { new ClozeRange(excerpt.ManualFront.IndexOf("Venus", StringComparison.Ordinal), 5) };
        var first = Add("merge-source-first", new(ExcerptCloze.Render(excerpt.ManualFront, firstRanges), "", ExcerptCardKind.Cloze, excerpt.ManualFront, firstRanges));
        var second = Add("merge-source-second", new(ExcerptCloze.Render(excerpt.ManualFront, secondRanges), "", ExcerptCardKind.Cloze, excerpt.ManualFront, secondRanges));
        var merged = excerpt.Cards.SingleOrDefault(card => card.Label == "合并挖空");
        var mergedDraft = merged == null ? ExcerptCards.Merge(excerpt, Excerpts, [first.Id, second.Id]) : ExcerptCards.Get(excerpt, Excerpts, merged.Id);
        Manifest.Ids["merged-cloze"] = mergedDraft.Id; SaveManifest();
        Add("math", new(@"Evaluate \(7+9\).", @"Addition gives \(7+9=16\)." + "\n" + @"\[2(7+9)=32\]"));
        Add("math-cloze", new(@"The theorem is {{c1::\(a^2+b^2=c^2\)::equation}}.", "Right triangles only.", ExcerptCardKind.Cloze));
        var imageMode = new ModeSnapshot(ExportRouting.BuiltinModes.Single(mode => mode.Id == "image-original"), "", "{}");
        var defaults = Path.Combine(options.ProjectRoot, "modes");
        var imageModes = ImageModeLoader.Load(defaults);
        var imageAnswerMode = imageModes.Single(mode => mode.Definition.Id == "image-answer");
        var imageStore = new ImageMaterialStore(options.DataRoot);
        using var noNetwork = new HttpClient(new NoNetworkHandler());
        var preparation = new ImagePreparation(imageStore, new NoOcr(), new ContentModule(noNetwork, () => throw new InvalidOperationException("Exporter fixtures must not request an API key.")));
        ImageMaterialJob ImageJob(string key, ImageAsset[] inputs, MaterialKind kind) => new() { Id = StableId(key), Settings = Manifest.Settings,
            Images = inputs, Kind = kind, Modes = imageModes, CleanupMode = ImageModeLoader.Stage(defaults, defaults, "ocr-cleanup", "ocr-cleanup.schema.json"),
            ReviewMode = ImageModeLoader.Stage(defaults, defaults, "content-review", "content-review.schema.json") };
        var paired = await preparation.OriginalAsync(ImageJob("paired-images", images.ToArray(), MaterialKind.OriginalImages), "image-answer", _ => { }, CancellationToken.None);
        Add("images", paired.Fields, imageMode);
        // A separate stable card exercises a fresh export after the original failed image card is recovered.
        Add("images-fresh", new("Independent image export.", "Answer image.") {
            FrontImages = [images[1]], BackImages = [images[3]] }, imageMode);
        var originalOnly = await preparation.OriginalAsync(ImageJob("original-only", images.Take(2).ToArray(), MaterialKind.OriginalImages), "image-excerpt", _ => { }, CancellationToken.None);
        Add("image-front-only", originalOnly.Fields, imageMode);
        // Public, handwritten answer fields test the registered exporter contract; no image model is called.
        Add("image-answer", new("", "Given: width 7 cm and length 9 cm.\n" + @"Perimeter: \(2(7+9)=32\) cm." + "\n" + @"Area: \(7\times9=63\) square cm.") {
            FrontImages = images.Take(2).ToArray() }, imageAnswerMode);
        var ocrJob = ImageJob("ocr-text-source", [images[0]], MaterialKind.OcrImages);
        const string ocrText = "Water freezes at zero degrees Celsius.\nPure water boils at one hundred degrees Celsius.";
        // Exercise the existing cached-cleanup path with public exporter fixtures, not claimed OCR/model evidence.
        ocrJob.Ocr = [new() { Image = images[0], RawText = ocrText, RawSha256 = ImageAsset.Hash(Encoding.UTF8.GetBytes(ocrText)),
            Cleanup = new(ocrText, []), Review = new("pass", [], []) }];
        if (File.Exists(Path.Combine(imageStore.DirectoryFor(ocrJob.Id), "job.json"))) ocrJob = imageStore.Find(ocrJob.Id);
        else imageStore.Save(ocrJob);
        var cleaned = await preparation.TextAsync(ocrJob, _ => { }, CancellationToken.None);
        Manifest.Ids.TryAdd("ocr-session", StableId("ocr-session")); SaveManifest();
        var ocrExcerpt = Excerpts.Find(Manifest.Ids["ocr-session"]);
        if (ocrExcerpt == null)
        {
            ocrExcerpt = new() { Id = Manifest.Ids["ocr-session"], Original = Material(cleaned), Settings = Manifest.Settings,
                ManualFront = cleaned, ImageMaterialId = ocrJob.Id };
            Excerpts.Save(ocrExcerpt);
        }
        var ocrLink = ocrExcerpt.Cards.SingleOrDefault(card => card.Label == "ocr-text");
        var ocrDraft = ocrLink == null ? ExcerptCards.Add(ocrExcerpt, Excerpts, new(cleaned, ""), "ocr-text") : ExcerptCards.Get(ocrExcerpt, Excerpts, ocrLink.Id);
        Manifest.Ids["ocr-text"] = ocrDraft.Id; SaveManifest();
        var definitions = ModeConfigurationLoader.Load(defaults);
        var translationMode = definitions.Modes.Single(mode => mode.Definition.OutputKind == "translation");
        var questionMode = definitions.Modes.Single(mode => mode.Definition.OutputKind == "question");
        EnsureDraft("translation", () => {
            var material = Material("学习把想法联系起来。");
            return new() { Id = StableId("translation"), Original = material, Material = material, Settings = Manifest.Settings,
                Configuration = new("", [translationMode]), Input = new(Intent: "translation"), ModeId = translationMode.Definition.Id,
                Route = new(translationMode.Definition.Id, "translation", false, false), Content = new(translationMode.Definition.Id, Basic: new(material.SelectedText, "Learning connects ideas.")) };
        });
        EnsureDraft("questions", () => {
            var material = Material("What is the perimeter of a 7 cm by 9 cm rectangle?");
            var answer = new QuestionAnswer("answered", "The perimeter is 2 × (7 + 9) = 32 cm.", []);
            return new() { Id = StableId("questions"), Original = material, Material = material, Settings = Manifest.Settings,
                Configuration = new("", [questionMode]), Input = new(Intent: "question"), ModeId = questionMode.Definition.Id,
                Route = new(questionMode.Definition.Id, "question", true, false), Content = new(questionMode.Definition.Id, Basic: new(material.SelectedText, answer.AnswerText!), Answer: answer),
                Conversation = [new(material.SelectedText, answer), new("What is its area?", new("answered", "The area is 7 × 9 = 63 square cm.", []))] };
        });
        var independent = EnsureDraft("independent-cloze", () => {
            var material = Material("Copper has symbol Cu; silver has Ag; repeat copper: Cu.");
            var mode = ExportRouting.BuiltinModes.Single(mode => mode.Id == "excerpt-manual");
            return new() { Id = StableId("independent-cloze"), Original = material, Material = material, Settings = Manifest.Settings,
                Configuration = new("", [new(mode, "", "")]), Input = new(Source: "public-exporter-fixture", Intent: "excerpt"),
                ModeId = mode.Id, Route = new(mode.Id, "excerpt", false, false) };
        });
        // Excerpt UI deliberately only emits c1. This exercises the existing frozen-note converter's c1/c2 support.
        if (!ExportRouting.Frozen(independent))
        {
            ExportRouting.Confirm(independent, new(Manifest.Settings.AnkiRootDeck ?? Manifest.Settings.AnkiDeck, AnkiNoteDraft.ExcerptClozeModelName,
                new() { ["ClipId"] = independent.Id, ["Front"] = AnkiNoteDraft.Html("Copper has symbol {{c1::Cu::symbol}}; silver has {{c2::Ag::symbol}}; repeat copper: {{c1::Cu::symbol}}."),
                    ["Back"] = AnkiNoteDraft.Html("Cu is copper.\nAg is silver."), ["Source"] = AnkiNoteDraft.Html(independent.Original.SourceTitle) },
                ["KnowledgeClipper", "kc_" + independent.Id], [0]));
            Drafts.Save(independent);
        }
        foreach (var key in LanguageParents)
        {
            var language = key == "language-zh" ? "zh" : "en";
            var modes = definitions.Modes.Where(mode => mode.Definition.Language == language && mode.Definition.OutputKind == "language" &&
                (key != "anki-language" || mode.Definition.Practice == PracticeChoice.Reading)).OrderBy(mode => mode.Definition.Practice).ToArray();
            if (modes.Length != (key == "anki-language" ? 1 : 2) || modes.Any(mode => !PublishedLanguage.Applies(mode.Definition)))
                throw new InvalidDataException("All four published language modes must be available.");
            var parent = EnsureDraft(key, () => {
                var text = language == "zh" ? "持之以恒" : key == "anki-language" ? "learn" : "orbit";
                var sentence = language == "zh" ? "学习需要持之以恒。" : key == "anki-language" ? "We learn through practice." : "Planets orbit the Sun.";
                var material = new CaptureMaterial(text, sentence, "source", sentence.IndexOf(text, StringComparison.Ordinal), "Public SDK exporter fixture; no LLM", 0, Manifest.CreatedAt);
                var settings = key != "anki-language" ? Manifest.Settings with { ModeDestinations = modes.ToDictionary(mode => mode.Definition.Id, _ => ExportDestination.RemNote) } : Manifest.Settings;
                return new() { Id = StableId(key), Original = material, Material = material, Settings = settings, Configuration = new("", modes),
                    Input = new(Intent: "language"), ModeId = "language", Route = new("language", "language", false, true), Practice = PracticeChoice.Reading };
            });
            foreach (var child in LanguageModeCards.Ensure(parent, modes.Select(mode => mode.Definition).ToArray(), Drafts))
            {
                if (child.Content == null && !ExportRouting.Frozen(child))
                {
                    var pronunciation = language == "zh" ? "chí zhī yǐ héng" : key == "anki-language" ? "/lɜːrn/" : "/ˈɔːrbɪt/";
                    child.Content = new(child.ModeId!, new LanguageFields(pronunciation, language == "zh" ? "长久坚持，不轻易放弃。" : null, null, null) {
                        Format = PublishedLanguage.Format, Status = "ready", ExplanationEn = language == "zh" ? null : key == "anki-language" ? "To gain knowledge or skill." : "To move around a star or planet.",
                        SentenceTranslationZh = language == "zh" ? null : key == "anki-language" ? "我们通过练习学习。" : "行星围绕太阳运行。",
                        Lemma = language == "en" && child.Practice == PracticeChoice.Reading ? child.Material.SelectedText : null });
                    Drafts.Save(child);
                }
                Manifest.Ids[(key == "anki-language" ? key : child.ModeId!) + "-card"] = child.Id; SaveManifest();
            }
        }
        var prepare = new PrepareOnlyImporter();
        await ExcerptCards.ImportAsync(excerpt, Excerpts, prepare, excerpt.Cards.Where(card => card.SupersededBy == null).Select(card => card.Id));
        await ExcerptCards.ImportAsync(ocrExcerpt, Excerpts, prepare, [ocrDraft.Id]);
        var translation = Draft("translation"); ExportRouting.Confirm(translation, ExportRouting.BuildNote(translation)); Drafts.Save(translation);
        await QuestionCards.ImportAsync(Draft("questions"), [0, 1], Drafts, prepare, () => { });
        foreach (var key in LanguageParents)
            await LanguageModeCards.ImportAsync(Draft(key), LanguageCards(key).Select(item => item.Draft.ModeId!).ToArray(), Drafts, prepare);
        SaveManifest();
        WorkflowJson.Save(Path.Combine(options.DataRoot, "prepared-plans.json"), AllCards().Select(item => new {
            item.Key, item.Draft.Id, destination = ExportRouting.For(item.Draft), item.Draft.ImportState,
            ankiDeck = item.Draft.ConfirmedNote?.Deck, plan = item.Draft.ConfirmedRemNote }).ToArray());
    }
    private ClipDraft EnsureDraft(string key, Func<ClipDraft> create)
    {
        var id = Manifest.Ids.GetValueOrDefault(key) ?? StableId(key);
        var draft = Drafts.Find(id) ?? create();
        if (draft.Id != id) throw new InvalidDataException("Fixture identity mismatch.");
        if (Drafts.Find(id) == null) Drafts.Save(draft);
        Manifest.Ids[key] = id; SaveManifest(); return draft;
    }
    internal IEnumerable<(string Key, ClipDraft Draft, DraftStore Store)> AllCards()
    {
        foreach (var key in ExcerptKeys)
            yield return (key, ExcerptCard(key), Excerpts.Cards);
        yield return ("ocr-text", ExcerptCard("ocr-text"), Excerpts.Cards);
        if (Manifest.Ids.TryGetValue("history-added", out var added)) yield return ("history-added", Excerpts.Cards.Find(added)!, Excerpts.Cards);
        foreach (var key in DirectKeys) yield return (key, Draft(key), Drafts);
        var question = Draft("questions");
        foreach (var pair in question.FollowUpCards.OrderBy(pair => pair.Key)) yield return ("question-" + pair.Key, Drafts.Find(pair.Value)!, Drafts);
        foreach (var key in LanguageParents)
            foreach (var item in LanguageCards(key)) yield return (item.Key, item.Draft, Drafts);
    }
    private sealed class NoNetworkHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Exporter preparation cannot call a content service."); }
    private sealed class NoOcr : IImageOcrExtractor
    { public Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress, CancellationToken ct, string? resumeManifest = null) => throw new InvalidOperationException("Exporter preparation cannot invoke OCR."); }
    private sealed class PrepareOnlyImporter : ICardImporter
    {
        public ExportDestination? FixedDestination => null;
        public Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
        {
            if (draft.ConfirmedNote == null) throw new InvalidDataException("Preparation failed to freeze a card.");
            return Task.CompletedTask;
        }
    }
}

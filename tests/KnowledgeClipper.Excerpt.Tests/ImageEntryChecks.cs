using System.Net;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class ImageEntryChecks
{
    internal static async Task RunAsync(string project, string receipt)
    {
        var checks = new List<string>();
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
        var output = Path.GetDirectoryName(receipt)!;
        var data = Path.Combine(@"D:\Caches\KnowledgeClipper\image-entry-tests", Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(output, "fixtures", "ocr.png");
        var store = new ImageMaterialStore(data);
        var drafts = new DraftStore(data);
        var configuration = ModeConfigurationLoader.Load(Path.Combine(project, "modes"));
        var settings = new LanguageSettings { ContextEnabled = true, Practice = PracticeChoice.Both, LanguageAutoRead = false,
            OcrDefaultRoute = "language", OriginalDefaultRoute = "image-answer", AnkiRootDeck = "Public image route fixture", ContentMaxRetries = 0 };
        ImageMaterialJob Job(MaterialKind kind, LanguageSettings? snapshot = null) => store.Create([(imagePath, "question")], kind,
            snapshot ?? settings, Path.Combine(project, "modes"), Path.Combine(data, "modes"));
        foreach (var originalMode in new[] { false, true })
        foreach (var intent in new[] { "excerpt", "language", "question" })
        foreach (var quick in new[] { false, true })
        {
            var job = Job(originalMode ? MaterialKind.OriginalImages : MaterialKind.OcrImages);
            ImageEntryRouting.SetSingle(job, intent, quick); store.Save(job); job = store.Find(job.Id);
            var ocr = !originalMode || intent == "language" || intent == "question" && quick;
            var material = ImageEntryRouting.Material(job, "lead");
            Check(job.Entry == intent && job.Route == intent && job.Quick == quick && ImageEntryRouting.NeedsOcr(job) == ocr &&
                (material.FrontImage != null) == (originalMode && intent != "excerpt") && material.VisualQuestion == (originalMode && intent == "question" && !quick),
                $"single image route persists explicit {intent}, original={originalMode}, quick={quick}, without using saved defaults");
            Check(material.NeedsContextChoice && material.ContextText == null, $"OCR/image material does not invent context: {intent}/{originalMode}/{quick}");
        }
        var noContext = Job(MaterialKind.OcrImages, settings with { ContextEnabled = false });
        ImageEntryRouting.SetSingle(noContext, "language", false);
        Check(ImageEntryRouting.Material(noContext, "lead").ContextOrigin == "generated_allowed", "disabled context retains the existing generated-example route");
        var beforeOcrJob = JsonSerializer.Serialize(noContext, WorkflowJson.Options);
        Check(ImageEntryRouting.Material(noContext, "## lead").SelectedText == "lead",
            "OCR Markdown heading markers do not become part of the language target");
        Check(ImageEntryRouting.Material(noContext, "# 学习 ###\r\nPractice makes progress.\r\n  ### Clear examples ##").SelectedText ==
            "学习\nPractice makes progress.\nClear examples", "multilingual OCR heading levels and optional closing markers retain their real text");
        var literalSymbols = "C# and F#\n#hashtag\n\\# literal\n####### literal\n    ## indented code\n```text\n## fenced code\n```\n2 # 3 = 5";
        Check(ImageEntryRouting.Material(noContext, literalSymbols).SelectedText == literalSymbols,
            "language OCR adaptation preserves literal hashes, code blocks, and non-heading symbols");
        var excerptMarkup = Job(MaterialKind.OcrImages); ImageEntryRouting.SetSingle(excerptMarkup, "excerpt", false);
        Check(ImageEntryRouting.Material(excerptMarkup, "## lead").SelectedText == "## lead" &&
            JsonSerializer.Serialize(noContext, WorkflowJson.Options) == beforeOcrJob,
            "language adaptation does not rewrite OCR evidence or excerpt Markdown");
        var multi = store.Create([(imagePath, "question"), (imagePath, "question")], MaterialKind.OriginalImages, settings,
            Path.Combine(project, "modes"), Path.Combine(data, "modes"));
        Check(multi.Entry == "multi" && multi.Route == "image-answer", "multi-image collection retains its registered saved default");
        Reject(() => ImageEntryRouting.SetSingle(multi, "excerpt", true), "single-image action cannot accidentally absorb multiple captures");
        Check(MaterialRouting.Choices(MaterialKind.OcrImages).Select(x => x.Id).SequenceEqual(new[] { "excerpt", "language", "question" }),
            "completed OCR supports all three explicit text routes");

        var languageJob = Job(MaterialKind.OriginalImages, settings with { ContextEnabled = false });
        ImageEntryRouting.SetSingle(languageJob, "language", false);
        var original = ImageEntryRouting.Material(languageJob, "lead");
        var selected = LanguageModes.Select(configuration, original, PracticeChoice.Both);
        Check(selected.Length == 1 && selected[0].Practice == PracticeChoice.Reading, "image language always selects understanding and reading only");
        var parent = new ClipDraft { Original = original, Material = original, Settings = settings, Configuration = configuration, Practice = PracticeChoice.Reading,
            Input = new(Intent: "language"), Route = new(selected[0].Id, "language", true, true), ModeId = selected[0].Id };
        var language = LanguageModeCards.Ensure(parent, selected, drafts).Single();
        language.Content = new(selected[0].Id, new("/liːd/", null, "They lead the team.", 5)
        { Format = PublishedLanguage.Format, Status = "ready", ExplanationEn = "to guide or direct", SentenceTranslationZh = "他们带领这个团队。", Lemma = "lead" });
        drafts.Save(language); drafts.Save(parent);
        var reopened = drafts.Find(parent.Id)!;
        Check(LanguageModeCards.Children(reopened, drafts).Single().Original == reopened.Original, "image identity remains value-equal across independent parent and child JSON reads");
        var note = AnkiNoteDraft.From(language);
        Check(note.IsBasic && note.Fields["Front"] == "<div>" + original.FrontImage!.Html + "</div>" && note.Media.Single() == original.FrontImage &&
            note.Ordinals.SequenceEqual(new[] { 0 }) && note.Fields["Back"].Contains("/liːd/") && note.Fields["Back"].Contains("guide or direct"),
            "image language exports the original PNG on the front and all reading details on the back");
        var readingPlan = RemNoteCardConversion.Convert(note).Single();
        Check(readingPlan.Kind == "basic" && readingPlan.Front.Count(x => x.Kind == "image") == 1 && readingPlan.Back.Length >= 3 &&
            ExportRouting.For(language) == ExportDestination.Anki && ExportRouting.DocumentPath(language)[^1] == "理解与朗读",
            "image reading supports both export adapters while preserving language destination defaults");
        Reject(() => LanguageModeCards.Ensure(parent, configuration.Modes.Where(x => x.Definition.Language == "en" && x.Definition.Practice == PracticeChoice.Writing)
            .Select(x => x.Definition).ToArray(), drafts), "image language rejects direct attempts to create a writing card");

        using var provider = new QuestionProvider(); using var http = new HttpClient(provider);
        var content = new ContentModule(http, () => "offline-public-fixture");
        var questionJob = Job(MaterialKind.OriginalImages);
        ImageEntryRouting.SetSingle(questionJob, "question", false);
        var visual = ImageEntryRouting.Material(questionJob, "Explain the public image.");
        var answer = await content.AnswerAsync(visual, null, [], "Explain this image", configuration, settings, default);
        var request = provider.Requests.Single();
        var blocks = request.GetProperty("messages")[1].GetProperty("content").EnumerateArray().ToArray();
        var imageBlock = blocks.Single(x => x.GetProperty("type").GetString() == "image_url");
        Check(request.GetProperty("model").GetString() == settings.ImageModel &&
            ImageAsset.Hash(Convert.FromBase64String(imageBlock.GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1])) == visual.FrontImage!.Sha256,
            "normal original-image question sends the exact image bytes to the configured visual model");
        await content.AnswerAsync(visual, null, [new("Explain this image", answer)], "Give another detail", configuration, settings, default);
        Check(provider.Requests.Count == 2 && provider.Requests[1].GetProperty("messages")[1].GetProperty("content").EnumerateArray().Any(x => x.GetProperty("type").GetString() == "image_url"),
            "visual follow-up still receives the original image");
        ImageEntryRouting.SetSingle(questionJob, "question", true);
        var quickMaterial = ImageEntryRouting.Material(questionJob, "Learning connects ideas.");
        await content.AnswerAsync(quickMaterial, null, [], "Explain the OCR text", configuration, settings, default);
        Check(provider.Requests[^1].GetProperty("model").GetString() == settings.Model &&
            provider.Requests[^1].GetProperty("messages")[1].GetProperty("content").ValueKind == JsonValueKind.String && quickMaterial.FrontImage != null,
            "quick original-image question uses OCR text on the text model while retaining its export image");
        using (var malformed = new QuestionProvider { MalformedFirst = true })
        using (var retryHttp = new HttpClient(malformed))
        {
            var retryAnswer = await new ContentModule(retryHttp, () => "offline-public-fixture").AnswerAsync(visual, null, [], "Explain this image",
                configuration, settings with { ContentMaxRetries = 1 }, default);
            Check(retryAnswer.Answered && malformed.Requests.Count == 2,
                "invalid JSON formula escapes enter the bounded shared retry before schema validation");
        }

        var question = new ClipDraft { Original = visual, Material = visual, Settings = settings, Configuration = configuration,
            Input = new(Intent: "question", ExplicitQuestion: "Explain this image"), Route = new("question", "question", true, false), ModeId = "question",
            Content = new("question", Basic: new("Explain this image", answer.AnswerText!), Answer: answer),
            Conversation = [new("Explain this image", answer), new("Give another detail", new("answered", "A second detail.", []))] };
        drafts.Save(question);
        using var ankiBackend = new AnkiFixture(); using var ankiHttp = new HttpClient(ankiBackend);
        var anki = new AnkiConnection(ankiHttp, settings.AnkiEndpoint);
        await QuestionCards.ImportAsync(question, [0, 1], drafts, anki, () => { });
        var imported = question.FollowUpCards.OrderBy(x => x.Key).Select(x => drafts.Find(x.Value)!).ToArray();
        Check(imported.Length == 2 && ankiBackend.Adds == 2 && imported.All(x => x.ImportState == ImportState.Verified && x.ConfirmedNote!.Media.Single() == visual.FrontImage),
            "separate question rounds import actual image media through the Anki adapter");
        var second = imported[1].ConfirmedNote!;
        var secondPlan = RemNoteCardConversion.Convert(second).Single();
        Check(second.Fields["Front"] == "<div>" + visual.FrontImage!.Html + "</div>" && !second.HasQuestionContext &&
            second.Fields["Back"].Contains("Give another detail") && secondPlan.Front.All(x => x.Kind == "image" || string.IsNullOrWhiteSpace(x.Text)) &&
            secondPlan.Back.Any(row => row.Any(x => x.Text?.Contains("Explain this image") == true)),
            "question context and follow-up wording stay on the back, preserving an image-only front for Anki and RemNote");
        var reopenedQuestion = drafts.Find(question.Id)!;
        var previews = QuestionCards.Preview(reopenedQuestion, drafts);
        Check(previews.All(x => x.State == ImportState.Verified && x.Error == null) && previews[1].Question == "Give another detail" &&
            previews[1].Answer == "A second detail." && previews[1].Context.Contains("Explain this image"),
            "reopened frozen image questions retain semantic question, answer and context without HTML or duplicate prefixes");
        await QuestionCards.ImportAsync(reopenedQuestion, [0, 1], drafts, anki, () => { });
        Check(ankiBackend.Adds == 2, "repeated image-question import reuses both frozen identities");
        var damaged = imported[1]; damaged.ConfirmedNote = second with { Media = [] }; drafts.Save(damaged);
        Check(QuestionCards.Preview(reopenedQuestion, drafts)[1] is { State: ImportState.Unknown, Error: not null }, "altered frozen image metadata is rejected instead of being silently accepted");
        WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot = data, scope = "route matrix, real serialization/export conversion, content and Anki HTTP fixtures; no external writes" });
    }

    private sealed class QuestionProvider : HttpMessageHandler
    {
        internal List<JsonElement> Requests { get; } = [];
        internal bool MalformedFirst;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); Requests.Add(input.RootElement.Clone());
            var answer = JsonSerializer.Serialize(new { answer_state = "answered", answer_text = "Learning connects ideas.", unresolved_reasons = Array.Empty<string>() });
            if (MalformedFirst && Requests.Count == 1) answer = """{"answer_state":"answered","answer_text":"\(x\)","unresolved_reasons":[]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = answer } } } })) };
        }
    }
}

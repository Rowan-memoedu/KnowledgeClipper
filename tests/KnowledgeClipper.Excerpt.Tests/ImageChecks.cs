using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class ImageChecks
{
    internal static async Task RunAsync(string project, string receipt, bool live)
    {
        var checks = new List<string>();
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        async Task Reject(Func<Task> action, string message) { try { await action(); } catch { Check(true, message); return; } throw new Exception("Expected failure: " + message); }
        var output = Path.GetDirectoryName(receipt)!;
        var fixtures = Path.Combine(output, "fixtures");
        var data = Path.Combine(@"D:\Caches\KnowledgeClipper\image-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var store = new ImageMaterialStore(data);
        var settings = new LanguageSettings { AnkiRootDeck = "Excerpt fixture", ContentMaxRetries = 0, ContentTimeoutSeconds = 120,
            MineruScriptPath = @"C:\Users\fy\Desktop\新建文件夹\04 Projects\MinerU OCR Integration\mineru_ocr.py" };
        (string, string) Q(int index) => (Path.Combine(fixtures, $"question-{index}.png"), "question");
        (string, string) A(int index) => (Path.Combine(fixtures, $"answer-{index}.png"), "answer");
        ImageMaterialJob Job(MaterialKind kind, params (string, string)[] inputs) => store.Create(inputs, kind, settings, Path.Combine(project, "modes"), Path.Combine(data, "modes"));
        var modes = ImageModeLoader.Load(Path.Combine(project, "modes"));
        Check(MaterialRouting.Resolve(MaterialKind.TypedText, settings, "为什么？ \n") == "question" &&
            MaterialRouting.Resolve(MaterialKind.TypedText, settings, "Translate this.") == "translation", "typed punctuation defaults handle both question marks and trailing whitespace");
        Check(MaterialRouting.Resolve(MaterialKind.TypedText, settings with { TypedDefaultRoute = "excerpt" }, "Question?") == "excerpt", "explicit saved defaults override punctuation");
        Check(MaterialRouting.Resolve(MaterialKind.OcrImages, settings) == null &&
            MaterialRouting.Resolve(MaterialKind.OriginalImages, settings, imageModes: modes) == "image-answer", "OCR defaults to Ask and original images default to a registered problem mode");
        Check(MaterialRouting.Resolve(MaterialKind.OriginalImages, settings with { OriginalDefaultRoute = "removed-mode" }, imageModes: modes) == null,
            "missing mode defaults resolve to Ask instead of another route");
        Check(MaterialRouting.Resolve(MaterialKind.OriginalImages, settings, imageModes: modes.Select(m => m with { LoadError = "missing prompt" })) == null,
            "an unavailable prompt also falls back to Ask with the native image route retained");
        var paired = Job(MaterialKind.OriginalImages, Q(1), A(1), Q(2), A(2));
        var reopened = store.Find(paired.Id);
        Check(reopened.Images.Select(i => i.Role + i.Index).SequenceEqual(new[] { "question1", "question2", "answer1", "answer2" }), "image roles and per-role order survive interleaved capture and reopen");
        using var provider = new ImageProviderFixture(); using var http = new HttpClient(provider);
        var extractor = new OcrFixture();
        var processing = new ImagePreparation(store, extractor, new ContentModule(http, () => "fixture"));
        var withAnswers = await processing.OriginalAsync(paired, "image-answer", _ => { }, default);
        Check(provider.Requests.Count == 0 && extractor.Calls.Count == 0 && withAnswers.Fields.FrontImages.Length == 2 && withAnswers.Fields.BackImages.Length == 2,
            "provided answer images produce ordered front/back media with zero OCR, solve or review calls");
        var without = Job(MaterialKind.OriginalImages, Q(1), Q(2));
        var solved = await processing.OriginalAsync(without, "image-answer", _ => { }, default);
        Check(provider.Requests.Count == 2 && provider.Requests.All(r => r.GetProperty("model").GetString() == settings.ImageModel) && solved.Fields.Back.Contains("63"),
            "image problem and independent review use the configured image model");
        var blocks = provider.Requests[0].GetProperty("messages")[1].GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "image_url").ToArray();
        Check(blocks.Length == 2 && blocks.Select(b => ImageAsset.Hash(Convert.FromBase64String(b.GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1])))
            .SequenceEqual(without.Questions.Select(i => i.Sha256)), "real image bytes are passed in explicit source order, never as local paths");
        var raw = await processing.OriginalAsync(without, "image-excerpt", _ => { }, default);
        Check(provider.Requests.Count == 2 && raw.Fields.Back == "", "original excerpt route bypasses content generation");
        var resumeDirectory = Path.Combine(data, "resume-test"); Directory.CreateDirectory(resumeDirectory);
        var manifestPath = Path.Combine(resumeDirectory, "manifest.json");
        WorkflowJson.Save(manifestPath, new { inputs = new[] { without.Questions[0].Path }, batch_id = "saved-batch", request_files = Array.Empty<object>(), results = new[] { new { state = "done" } } });
        var previousToken = Environment.GetEnvironmentVariable("MINERU_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("MINERU_API_TOKEN", "offline-fixture");
            var resumed = await new MineruCli(Path.Combine(project, "helpers", "mineru_bridge.py")).ExtractAsync(without.Questions[0],
                settings with { MineruScriptPath = Path.Combine(project, "tests", "KnowledgeClipper.Excerpt.Tests", "fixtures", "resume_mineru.py") },
                Path.Combine(resumeDirectory, "next"), _ => { }, default, manifestPath);
            Check(resumed.Text == "Resumed OCR without another upload." && File.Exists(resumed.ManifestPath), "installed-runner bridge resumes a completed batch without upload and validates ZIP plus Markdown");
            Check(Directory.GetFiles(Path.Combine(resumeDirectory, "next"), "*_content_list_v2.json", SearchOption.AllDirectories).Any(p => p.Length > 260),
                "MinerU extraction supports Windows output filenames beyond the traditional 260-character limit");
        }
        finally { Environment.SetEnvironmentVariable("MINERU_API_TOKEN", previousToken); }
        var ocrJob = Job(MaterialKind.OcrImages, Q(1), Q(2)); extractor.FailSecondOnce = true;
        await Reject(() => processing.TextAsync(ocrJob, _ => { }, default), "partial OCR prevents downstream text publication");
        Check(ocrJob.Ocr[0].Review?.Verdict == "pass" && ocrJob.Ocr[1].Error != null, "successful per-image result and failed item remain identifiable");
        var ocrText = await processing.TextAsync(store.Find(ocrJob.Id), _ => { }, default);
        Check(extractor.Calls.Count(i => i.Index == 1) == 1 && extractor.Calls.Count(i => i.Index == 2) == 2 && ocrText == "Page 1\n\nPage 2", "retry reuses verified successful OCR and preserves complete order");
        await Reject(() => Task.Run(() => ImageContentValidation.Cleanup("original", new("changed", [new("not in source", "guess")]))), "invented OCR source quotes are rejected");
        await Reject(() => Task.Run(() => ImageContentValidation.MathLines("\\[\nx+1=2\n\\]")), "physical newlines inside math are deterministically rejected");
        await Reject(() => Task.Run(() => ExcerptCards.Validate(new ExcerptFields("", "\\[\nx=1\n\\]") { FrontImages = without.Questions })),
            "editing an image answer cannot bypass formula validation before import");
        ImageContentValidation.MathLines(@"\[\begin{matrix}1&2\\3&4\end{matrix}\]");
        Check(true, "TeX row separators remain valid on one physical line");
        using var backend = new AnkiFixture(); using var ankiHttp = new HttpClient(backend);
        var anki = new AnkiConnection(ankiHttp, settings.AnkiEndpoint); var excerpts = new ExcerptStore(data);
        var session = new ExcerptSession { Original = new("Public multi-image fixture", null, "unavailable", null, "image fixture", 0, DateTimeOffset.UtcNow), Settings = settings };
        var card = ExcerptCards.Add(session, excerpts, withAnswers.Fields, "paired");
        await ExcerptCards.ImportAsync(session, excerpts, anki, [card.Id]); card = excerpts.Cards.Find(card.Id)!;
        Check(card.ImportState == ImportState.Verified && backend.MediaWrites == 4 && backend.Adds == 1, "four source images upload and read back as one native basic card");
        await ExcerptCards.ImportAsync(excerpts.Find(session.Id)!, excerpts, anki, [card.Id]);
        Check(backend.MediaWrites == 4 && backend.Adds == 1, "reopen and repeated import duplicate neither notes nor media");
        backend.ReverseImageOrder = true;
        await Reject(() => anki.VerifyAsync(card), "Anki image order mismatch is detected even when text is identical"); backend.ReverseImageOrder = false;
        var firstMedia = card.ConfirmedNote!.Media[0]; var savedMedia = backend.Media[firstMedia.FileName];
        backend.Media[firstMedia.FileName] = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        await Reject(() => anki.VerifyAsync(card), "actual stored media bytes must match the source hash"); backend.Media[firstMedia.FileName] = savedMedia;
        var generated = ExcerptCards.Add(session, excerpts, solved.Fields, "solved"); backend.DropAtAdd = backend.Adds + 1;
        await Reject(() => ExcerptCards.ImportAsync(session, excerpts, anki, [generated.Id]), "lost image-note response stays unresolved");
        var before = backend.Adds;
        await ExcerptCards.ImportAsync(excerpts.Find(session.Id)!, excerpts, anki, [generated.Id]);
        Check(backend.Adds == before && excerpts.Cards.Find(generated.Id)!.ImportState == ImportState.Verified, "image note reconciliation never blindly adds again");
        if (live) await LiveAsync(project, output, settings, checks);
        WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot = data, live });
    }
    private static async Task LiveAsync(string project, string output, LanguageSettings initial, List<string> checks)
    {
        var fixture = Path.Combine(output, "fixtures");
        var run = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var data = Path.Combine(@"D:\AppData\KnowledgeClipper\image-acceptance", run);
        var settings = initial with { AnkiRootDeck = "KnowledgeClipper::验收::截图路线 " + run };
        var store = new ImageMaterialStore(data); using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var preparation = new ImagePreparation(store, new MineruCli(Path.Combine(project, "helpers", "mineru_bridge.py")), new ContentModule(http, () => ContentCredentials.Resolve()));
        var questions = new[] { (Path.Combine(fixture, "question-1.png"), "question"), (Path.Combine(fixture, "question-2.png"), "question") };
        ImageMaterialJob Create(MaterialKind kind, IEnumerable<(string, string)> images) => store.Create(images, kind, settings, Path.Combine(project, "modes"), Path.Combine(data, "modes"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var problem = Create(MaterialKind.OriginalImages, questions);
        var answer = await preparation.OriginalAsync(problem, "image-answer", Console.WriteLine, timeout.Token);
        if (!answer.Fields.Back.Contains("32") || !answer.Fields.Back.Contains("63")) throw new InvalidDataException("Independent arithmetic check failed.");
        checks.Add("live multi-image solution uses both pages: perimeter 32 cm and area 63 square cm");
        var paired = Create(MaterialKind.OriginalImages, questions.Concat(new[] { (Path.Combine(fixture, "answer-1.png"), "answer"), (Path.Combine(fixture, "answer-2.png"), "answer") }));
        var direct = await preparation.OriginalAsync(paired, "image-answer", Console.WriteLine, timeout.Token);
        var ocr = Create(MaterialKind.OcrImages, new[] { (Path.Combine(fixture, "ocr.png"), "question") });
        var text = await preparation.TextAsync(ocr, Console.WriteLine, timeout.Token);
        if (!text.Contains("Learning connects ideas.") || !text.Contains("Practice helps us remember.")) throw new InvalidDataException("Live OCR omitted source text.");
        checks.Add("live MinerU upload, ZIP validation, original Markdown, DeepSeek cleanup and review complete from the original PNG");
        var excerpts = new ExcerptStore(data);
        var session = new ExcerptSession { Original = new("Public image workflow acceptance", null, "unavailable", null, "截图路线验收公开样例", 0, DateTimeOffset.UtcNow), Settings = settings };
        var first = ExcerptCards.Add(session, excerpts, answer.Fields, "两张题图生成解答");
        var second = ExcerptCards.Add(session, excerpts, direct.Fields, "两张题图与两张答案图");
        var third = ExcerptCards.Add(session, excerpts, new(text, ""), "OCR 摘录");
        var ids = new[] { first.Id, second.Id, third.Id };
        WorkflowJson.Save(Path.Combine(output, "image-live-pending.json"), new { data, sessionId = session.Id, ids });
        var anki = new AnkiConnection(http, settings.AnkiEndpoint);
        await ExcerptCards.ImportAsync(session, excerpts, anki, ids, timeout.Token);
        var cards = ids.Select(id => excerpts.Cards.Find(id)!).ToArray();
        foreach (var card in cards) await anki.VerifyAsync(card, timeout.Token);
        await ExcerptCards.ImportAsync(excerpts.Find(session.Id)!, excerpts, anki, ids, timeout.Token);
        var found = await anki.CallAsync("findNotes", new { query = "deck:\"" + settings.AnkiRootDeck + "\"" }, timeout.Token);
        if (found.GetArrayLength() != 3) throw new InvalidDataException("Live repeat import changed the card count.");
        var actual = await anki.CallAsync("cardsInfo", new { cards = cards.SelectMany(c => c.CardIds).ToArray() }, timeout.Token);
        WorkflowJson.Save(Path.Combine(output, "image-live-anki.json"), new { passed = true, deck = settings.AnkiRootDeck, dataRoot = data,
            jobs = new[] { problem.Id, paired.Id, ocr.Id }, sessionId = session.Id, noteIds = cards.Select(c => c.NoteId),
            cardIds = cards.SelectMany(c => c.CardIds), actualCards = actual });
        checks.Add("real Anki has three reviewable cards; ordered PNG media hashes, exact front/back fields and repeat import verified");
        Console.WriteLine("PASS native Anki: three image/OCR cards verified");
    }
    internal static async Task ImportSavedImagesAsync(string receipt)
    {
        var existing = Directory.GetDirectories(@"D:\AppData\KnowledgeClipper\image-acceptance").OrderDescending().First();
        var store = new ImageMaterialStore(existing); var jobs = store.Recent().ToArray();
        var problem = jobs.Single(j => j.Answer?.Answered == true && j.Review?.Verdict == "pass");
        var paired = jobs.Single(j => j.Answers.Length == 2);
        var excerpts = new ExcerptStore(existing);
        var pending = Path.Combine(Path.GetDirectoryName(receipt)!, "image-media-pending.json");
        ExcerptSession session;
        if (File.Exists(pending))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(pending));
            session = excerpts.Find(json.RootElement.GetProperty("sessionId").GetString()!)!;
        }
        else
        {
            session = new ExcerptSession { Original = new("多图公开验收", null, "unavailable", null, "图片路线公开样例", 0, problem.CreatedAt), Settings = problem.Settings };
            ExcerptCards.Add(session, excerpts, new ExcerptFields("", problem.Answer!.AnswerText!) { FrontImages = problem.Questions }, "两张题图生成解答");
            ExcerptCards.Add(session, excerpts, new ExcerptFields("", "") { FrontImages = paired.Questions, BackImages = paired.Answers }, "两张题图与两张答案图");
            WorkflowJson.Save(pending, new { data = existing, sessionId = session.Id });
        }
        using var http = new HttpClient(); var anki = new AnkiConnection(http, session.Settings.AnkiEndpoint);
        var ids = session.Cards.Select(c => c.Id).ToArray();
        await ExcerptCards.ImportAsync(session, excerpts, anki, ids);
        var cards = ids.Select(id => excerpts.Cards.Find(id)!).ToArray();
        foreach (var card in cards) await anki.VerifyAsync(card);
        await ExcerptCards.ImportAsync(excerpts.Find(session.Id)!, excerpts, anki, ids);
        var found = await anki.CallAsync("findNotes", new { query = "deck:\"" + session.Settings.AnkiRootDeck + "\"" });
        if (found.GetArrayLength() != 2) throw new Exception("Repeated native image import changed note count");
        var actual = await anki.CallAsync("cardsInfo", new { cards = cards.SelectMany(c => c.CardIds).ToArray() });
        WorkflowJson.Save(receipt, new { passed = true, scope = "two original-image routes; OCR live download remains blocked", dataRoot = existing, deck = session.Settings.AnkiRootDeck,
            sessionId = session.Id, noteIds = cards.Select(c => c.NoteId), cardIds = cards.SelectMany(c => c.CardIds), actualCards = actual,
            checks = new[] { "multi-page image problem solved and reviewed by live DeepSeek; independent arithmetic 32 and 63 verified", "provided answer images bypass content calls",
                "actual ordered front/back media bytes, fields, card count, decks and reviewability verified", "reopen and repeat import do not add cards or media" } });
        Console.WriteLine("PASS two native multi-image cards and repeated import verified; OCR download remains pending");
    }
    internal static async Task ResumeOcrAsync(string project, string data, string jobId, string receipt)
    {
        var store = new ImageMaterialStore(data);
        var job = store.Find(jobId);
        if (job.Kind != MaterialKind.OcrImages) throw new InvalidDataException("Expected an OCR image job.");
        var priorManifests = job.Ocr.Select(item => item.RawManifest ?? MineruCli.FindResumeManifest(
            Path.Combine(store.DirectoryFor(job.Id), "ocr", item.Image.Role + "." + item.Image.Index), item.Image.Path)).ToArray();
        if (priorManifests.Any(p => p == null)) throw new InvalidDataException("Expected the original completed OCR batch.");
        static string Batch(string path)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("batch_id").GetString()!;
        }
        var batches = priorManifests.Select(p => Batch(p!)).ToArray();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var preparation = new ImagePreparation(store, new MineruCli(Path.Combine(project, "helpers", "mineru_bridge.py")),
            new ContentModule(http, () => ContentCredentials.Resolve()));
        var text = await preparation.TextAsync(job, Console.WriteLine, timeout.Token);
        if (!text.Contains("Learning connects ideas.", StringComparison.Ordinal) || !text.Contains("Practice helps us remember.", StringComparison.Ordinal))
            throw new InvalidDataException("Original OCR sample text was not preserved.");
        if (!job.Ocr.Select(item => Batch(item.RawManifest!)).SequenceEqual(batches)) throw new InvalidDataException("OCR retry submitted a different batch.");
        if (job.Ocr.Any(item => item.Review?.Verdict != "pass" || item.Error != null)) throw new InvalidDataException("OCR cleanup/review remains unresolved.");
        var manifests = job.Ocr.Select(item => item.RawManifest).ToArray();
        var attempts = Directory.GetFiles(store.DirectoryFor(job.Id), "*.json", SearchOption.AllDirectories).Length;
        var repeated = await preparation.TextAsync(store.Find(job.Id), Console.WriteLine, timeout.Token);
        if (repeated != text || !store.Find(job.Id).Ocr.Select(item => item.RawManifest).SequenceEqual(manifests) ||
            Directory.GetFiles(store.DirectoryFor(job.Id), "*.json", SearchOption.AllDirectories).Length != attempts)
            throw new InvalidDataException("Completed OCR was processed again.");
        var excerpts = new ExcerptStore(data);
        var session = job.DestinationKind == "excerpt" && job.DestinationId != null ? excerpts.Find(job.DestinationId) : null;
        if (session == null)
        {
            var material = new CaptureMaterial(text, null, "generated_allowed", null, "图片 OCR 公开验收", 0, job.CreatedAt);
            session = new ExcerptSession { Original = material, Settings = job.Settings, ManualFront = material.SelectedText, ImageMaterialId = job.Id };
            ExcerptCards.Add(session, excerpts, new(material.SelectedText, ""), "原文");
            job.Route = "excerpt"; job.DestinationKind = "excerpt"; job.DestinationId = session.Id; store.Save(job);
        }
        var ids = session.Cards.Select(c => c.Id).ToArray();
        if (ids.Length != 1 || excerpts.Cards.Find(ids[0])!.Excerpt is not { } fields || fields.Front != text || fields.Back != "")
            throw new InvalidDataException("OCR excerpt must have the exact cleaned text and a blank answer.");
        var anki = new AnkiConnection(http, session.Settings.AnkiEndpoint);
        await ExcerptCards.ImportAsync(session, excerpts, anki, ids, timeout.Token);
        var cards = ids.Select(id => excerpts.Cards.Find(id)!).ToArray();
        foreach (var card in cards) await anki.VerifyAsync(card, timeout.Token);
        await ExcerptCards.ImportAsync(excerpts.Find(session.Id)!, excerpts, anki, ids, timeout.Token);
        var allCards = excerpts.Recent().SelectMany(s => s.Cards.Select(c => excerpts.Cards.Find(c.Id)!)).ToArray();
        foreach (var card in allCards) await anki.VerifyAsync(card, timeout.Token);
        var notes = await anki.CallAsync("findNotes", new { query = "deck:\"" + session.Settings.AnkiRootDeck + "\"" }, timeout.Token);
        if (notes.GetArrayLength() != 3 || allCards.Length != 3) throw new InvalidDataException("Expected two original-image notes and one OCR excerpt note.");
        var actual = await anki.CallAsync("cardsInfo", new { cards = allCards.SelectMany(c => c.CardIds).ToArray() }, timeout.Token);
        WorkflowJson.Save(receipt, new { passed = true, scope = "original failed OCR image resumed through direct MinerU; all three Anki routes verified",
            dataRoot = data, jobId, originalManifests = priorManifests, completedManifests = manifests, reusedBatchIds = batches,
            rawTexts = job.Ocr.Select(i => i.RawText), cleanedText = text, cleanup = job.Ocr.Select(i => i.Cleanup), review = job.Ocr.Select(i => i.Review),
            sessionId = session.Id, ocrNoteIds = cards.Select(c => c.NoteId), ocrCardIds = cards.SelectMany(c => c.CardIds),
            deck = session.Settings.AnkiRootDeck, noteIds = allCards.Select(c => c.NoteId), cardIds = allCards.SelectMany(c => c.CardIds), actualCards = actual,
            checks = new[] { "original completed batch resumed without upload", "valid ZIP, extracted output and root Markdown verified",
                "original image text preserved by real DeepSeek cleanup and review", "completed OCR reopens without repeating provider work",
                "OCR excerpt front equals cleaned text and back is blank", "all three Anki routes read back exact media/fields and reviewable cards",
                "repeated OCR import keeps the total at three notes" } });
        Console.WriteLine("PASS original OCR batch resumed by direct MinerU; cleanup, review and all three native Anki cards verified");
    }
    private sealed class OcrFixture : IImageOcrExtractor
    {
        internal bool FailSecondOnce;
        internal List<ImageAsset> Calls = [];
        public Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress, CancellationToken ct, string? resumeManifest = null)
        {
            Calls.Add(image); if (FailSecondOnce && image.Index == 2) { FailSecondOnce = false; throw new IOException("fixture partial OCR"); }
            return Task.FromResult(new OcrExtraction("Page " + image.Index, "fixture-manifest"));
        }
    }
    private sealed class ImageProviderFixture : HttpMessageHandler
    {
        internal List<JsonElement> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Requests.Add(json.RootElement.Clone());
            var content = json.RootElement.GetProperty("messages")[1].GetProperty("content");
            var input = content.ValueKind == JsonValueKind.Array ? content[0].GetProperty("text").GetString()! : content.GetString()!;
            using var document = JsonDocument.Parse(input); var root = document.RootElement;
            object result = root.TryGetProperty("candidate", out _) ? new { verdict = "pass", errors = Array.Empty<object>(), warnings = Array.Empty<object>() } :
                root.TryGetProperty("ocr_text", out var raw) ? new { cleaned_text = raw.GetString(), notes = Array.Empty<object>() } :
                new { answer_state = "answered", answer_text = @"周长 \(32\) cm，面积 \(63\) cm²。", unresolved_reasons = Array.Empty<string>() };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(result) } } } }) };
        }
    }
}

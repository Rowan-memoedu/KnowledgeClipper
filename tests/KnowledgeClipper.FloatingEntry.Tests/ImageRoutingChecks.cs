using System.Collections;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static void Field(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static Form[] ImageForms(object flow) => ((IDictionary)flow.GetType().GetField("forms", Flags)!.GetValue(flow)!).Values.Cast<Form>().Where(x => !x.IsDisposed).ToArray();
    private static async Task ImageRoutesAsync(Form owner)
    {
        using var bitmap = new Bitmap(100, 40);
        using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.White); g.DrawString("lead", SystemFonts.DefaultFont, Brushes.Black, 8, 8); }
        using var bytes = new MemoryStream(); bitmap.Save(bytes, ImageFormat.Png); var png = bytes.ToArray();
        foreach (var original in new[] { false, true })
        foreach (var intent in new[] { "excerpt", "language", "question" })
        foreach (var quick in new[] { false, true })
        {
            var root = Path.Combine(dataRoot, $"image-{original}-{intent}-{quick}");
            var settings = Settings() with { OriginalImageMode = original, OcrDefaultRoute = "ask", OriginalDefaultRoute = "image-answer" };
            WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
            using var language = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
            using var excerpt = (IDisposable)New("ExcerptDesktopFlow", owner, root, settings, (Action<string>)statuses.Add);
            var config = New("AppConfig"); config.GetType().GetProperty("WorkDirectory")!.SetValue(config, root);
            using var image = (IDisposable)New("ImageDesktopFlow", owner, config, language, excerpt, null);
            var ocrText = intent == "language" ? "## lead" : "lead";
            using var provider = new ImageRouteProvider { CleanedText = ocrText }; using var contentHttp = new HttpClient(provider);
            ((HttpClient)language.GetType().GetField("http", Flags)!.GetValue(language)!).Dispose(); Field(language, "http", contentHttp);
            var extractor = new ImageRouteOcr { Text = ocrText }; var images = new ImageMaterialStore(root);
            Field(image, "preparation", new ImagePreparation(images, extractor, new ContentModule(contentHttp, () => "public-fixture")));
            Call(image, "StartClipboardImage", png, intent, quick);
            if (quick) Check(ImageForms(image).Length == 0 && Forms(language).Length == 0 && Forms(excerpt).Length == 0,
                $"image {intent}/{original} quick processing starts without a window");
            await Until(() => images.Recent().SingleOrDefault()?.DestinationId != null, $"image {intent}/{original}/{quick} reaches one downstream draft");
            var job = images.Recent().Single();
            var rawExcerpt = intent == "excerpt" && (original || quick);
            Check(job.Entry == intent && job.Images.Length == 1 && ImageForms(image).Length == 0 &&
                extractor.Calls == (rawExcerpt ? 0 : !original || intent == "language" || intent == "question" && quick ? 1 : 0),
                $"actual image {intent}/{original}/{quick} uses the required OCR policy and never starts multi-image collection");
            if (intent == "excerpt")
            {
                var excerpts = new ExcerptStore(root); var session = excerpts.Find(job.DestinationId!)!;
                var card = ExcerptCards.Get(session, excerpts, session.Cards.Single().Id);
                Check(card.Excerpt!.FrontImages.Length == (rawExcerpt ? 1 : 0) && card.Excerpt.Back == "" && (rawExcerpt || card.Excerpt.Front == "lead"),
                    $"image excerpt/{original}/{quick} keeps exactly the raw image or OCR text with a blank back");
                if (quick) await Until(() => Forms(excerpt).Length == 1 && ExcerptCards.Get(session, excerpts, card.Id).ImportError != null,
                    "failed image excerpt import returns to its one original preview");
                else Check(Forms(excerpt).Length == 1 && card.ConfirmedNote == null, "normal image excerpt waits for confirmation");
                card = ExcerptCards.Get(session, excerpts, card.Id);
                Check(card.ModeId == "excerpt-manual" && (card.ConfirmedRemNote == null ||
                    card.ConfirmedRemNote.DocumentPath.SequenceEqual(new[] { "摘录", DateTime.Today.ToString("M.d", System.Globalization.CultureInfo.InvariantCulture) })),
                    "single-image excerpt uses the manual excerpt destination and daily document, never image-original");
                Check(provider.Answers == 0, "raw image excerpt never asks the answer model");
            }
            else
            {
                var drafts = new DraftStore(root);
                ClipDraft draft;
                if (intent == "language" && quick)
                {
                    await Until(() => drafts.Find(job.DestinationId!) is { } pending && pending.ModeCards.Count == (original ? 1 : 2) &&
                        LanguageModeCards.Children(pending, drafts).All(child => child.AwaitingAnki && child.ConfirmedNote != null &&
                            child.ConfirmedDestination == ExportDestination.Anki && child.ImportState != ImportState.Verified),
                        $"image language/{original}/quick persists all confirmed cards while Anki is unavailable", 12000);
                    draft = drafts.Find(job.DestinationId!)!;
                    Check(Forms(language).Length == 0, $"image language/{original}/quick pending Anki does not open an error preview");
                }
                else if (intent == "question" && !quick)
                {
                    await Until(() => ChatForms(language).Length == 1, $"image question/{original} opens the persistent chat");
                    draft = (ClipDraft)Get(ChatForms(language).Single(), "Draft")!;
                }
                else
                {
                    await Until(() => Forms(language).Length == 1, $"image {intent}/{original}/{quick} has its normal or failed-import preview");
                    draft = (ClipDraft)Get(Forms(language).Single(), "Draft")!;
                }
                if (intent != "question" || quick)
                    await Until(() => intent == "language" ? LanguageModeCards.Children(draft, drafts).All(x => x.Content != null) && draft.ModeCards.Count > 0 : draft.Conversation.Count == 1,
                        $"image {intent}/{original}/{quick} completes the selected content flow");
                Check(draft.Id == job.DestinationId && (draft.Original.FrontImage != null) == original && draft.Original.ContextText == null,
                    "downstream language/question draft retains the correct original-image policy and no invented source context");
                if (intent == "language") Check(LanguageModeCards.Children(draft, drafts).Length == (original ? 1 : 2) &&
                    (!original || LanguageModeCards.Children(draft, drafts).Single().Practice == PracticeChoice.Reading), "image language restricts only original-image cards to reading");
                else if (!quick) Check(provider.Answers == 0 && draft.Chat!.Prompts.Count == 0 &&
                    draft.Chat.Attachments.Count == (original ? 1 : 0) && (original ? draft.Chat.Input == "" : draft.Chat.Input == draft.QuestionText),
                    "normal image question preserves image-only or OCR input for explicit send without calling a model");
                else Check(provider.VisualAnswers == 0 && provider.Answers == 1,
                    "quick image question remains OCR-based with one answer");
                if (intent == "language") Check(draft.Original.SelectedText == "lead" && job.Text == "## lead" &&
                    provider.LanguageTargets.Count > 0 && provider.LanguageTargets.All(x => x == "lead"),
                    "actual language entry preserves OCR evidence while sending only the heading text to the content model");
            }
            foreach (var form in Forms(language).Concat(ChatForms(language)).Concat(Forms(excerpt)).Concat(ImageForms(image))) form.Close();
        }
        await ImageHistoryAndContextAsync(owner, png);
    }

    private static async Task ImageHistoryAndContextAsync(Form owner, byte[] png)
    {
        var root = Path.Combine(dataRoot, "image-history-context");
        // This fixture deliberately holds the reply across a native window open;
        // it exercises task reuse, not the separate one-second timeout path.
        var settings = Settings() with { OriginalImageMode = true, ContextEnabled = true, ContentTimeoutSeconds = 20 };
        WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
        using var language = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
        using var excerpt = (IDisposable)New("ExcerptDesktopFlow", owner, root, settings, (Action<string>)statuses.Add);
        var config = New("AppConfig"); config.GetType().GetProperty("WorkDirectory")!.SetValue(config, root);
        using var image = (IDisposable)New("ImageDesktopFlow", owner, config, language, excerpt, null);
        using var provider = new ImageRouteProvider(); using var http = new HttpClient(provider);
        ((HttpClient)language.GetType().GetField("http", Flags)!.GetValue(language)!).Dispose(); Field(language, "http", http);
        var extractor = new ImageRouteOcr { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var store = new ImageMaterialStore(root);
        Field(image, "preparation", new ImagePreparation(store, extractor, new ContentModule(http, () => "public-fixture")));
        Call(image, "StartClipboardImage", png, "language", true);
        await Until(() => extractor.Calls == 1, "quick original-image language begins OCR before asking for context");
        var saved = store.Recent().Single();
        Call(image, "Open", saved, true);
        await Task.Delay(100);
        Check(extractor.Calls == 1 && ImageForms(image).Length == 1, "opening image history during quick OCR shares the same active task");
        extractor.Hold.TrySetResult();
        await Until(() => Forms(language).Length == 1 && store.Find(saved.Id).DestinationId != null, "missing OCR context returns quick language to its existing decision UI");
        var draft = (ClipDraft)Get(Forms(language).Single(), "Draft")!;
        Check(draft.Original.ContextOrigin == "unavailable" && draft.Material.ContextText == null && draft.Original.FrontImage != null &&
            draft.ModeCards.Count == 0 && new DraftStore(root).Recent().Count() == 1 && provider.Answers == 0,
            "enabled context without source waits for the existing user choice, preserves the image and does not duplicate drafts");
        Check(Forms(language).Single().Controls.Find("knowledge-clipper.language.allow-generated", true).Length == 1,
            "OCR context decision offers the existing explicit generated-example action");
        Save(Forms(language).Single(), "image-missing-context.png");
        Forms(language).Single().Close();

        Call(language, "SaveSettings", settings with { OriginalImageMode = false });
        var stopping = new ImageRouteOcr { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously),
            DrainCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Field(image, "preparation", new ImagePreparation(store, stopping, new ContentModule(http, () => "public-fixture")));
        Call(image, "StartClipboardImage", png, "excerpt", false);
        await Until(() => stopping.Calls == 1 && ImageForms(image).Length == 1, "normal OCR begins before the cancellation/reopen race");
        var interrupted = store.Recent().Single(x => x.Id != saved.Id);
        ImageForms(image).Single().Close();
        await Until(() => stopping.Cancelling, "closed preview cancels OCR while the provider drains in-flight work");
        Call(image, "Open", store.Find(interrupted.Id), true);
        await Until(() => ImageForms(image).Length == 1, "history can reopen the same job before canceled OCR exits");
        // WinForms posts Shown after Show returns. Let that handler join the old
        // task before draining it; otherwise this legitimately tests a fresh auto-resume.
        await Task.Delay(100);
        Check(stopping.Calls == 1, "reopened preview is waiting on the still-draining original OCR task");
        stopping.DrainCancellation.TrySetResult();
        try
        {
            await Until(() => ImageForms(image).Single().Controls.Find("knowledge-clipper.image.retry", true).SingleOrDefault()?.Enabled == true,
                "new preview restores retry after the old window's cancellation instead of remaining disabled");
        }
        catch
        {
            var preview = ImageForms(image).Single(); Save(preview, "cancel-reopen-failure.png");
            var retry = preview.Controls.Find("knowledge-clipper.image.retry", true).SingleOrDefault();
            WorkflowJson.Save(Path.Combine(output, "cancel-reopen-diagnostic.json"), new { preview.Visible, preview.Enabled, preview.IsDisposed,
                retryEnabled = retry?.Enabled, retryParentEnabled = retry?.Parent?.Enabled, cancelled = ((CancellationToken)Get(preview, "Token")!).IsCancellationRequested,
                status = ((Label)preview.GetType().GetField("status", Flags)!.GetValue(preview)!).Text,
                activeTasks = ((IDictionary)image.GetType().GetField("processingTasks", Flags)!.GetValue(image)!).Count });
            throw;
        }
        stopping.Hold = null;
        ((Button)ImageForms(image).Single().Controls.Find("knowledge-clipper.image.retry", true).Single()).PerformClick();
        await Until(() => store.Find(interrupted.Id).DestinationId != null && Forms(excerpt).Length == 1, "retry completes the same interrupted image job");
        Check(stopping.Calls == 2 && new ExcerptStore(root).All().Count() == 1, "cancellation recovery creates one downstream excerpt without replacing the source");
        Forms(excerpt).Single().Close();

        Call(language, "SaveSettings", settings with { OriginalImageMode = true, ContextEnabled = false });
        provider.HoldAnswers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Call(image, "StartClipboardImage", png, "question", true);
        await Until(() => provider.Answers == 1 && store.Recent().Any(x => x.Entry == "question" && x.DestinationId != null),
            "quick OCR question saves its destination while the text answer is still pending");
        var questionJob = store.Recent().Single(x => x.Entry == "question");
        var activeDraft = (ClipDraft)language.GetType().GetField("latest", Flags)!.GetValue(language)!;
        Call(image, "Open", questionJob, true);
        var pendingChat = Application.OpenForms.Cast<Form>().Single(f => f.Name == "knowledge-clipper.chat");
        Check(ReferenceEquals(Get(pendingChat, "Draft"), activeDraft), "image history opens the pending downstream question in the current chat route");
        Check(ReferenceEquals(Get(pendingChat, "Draft"), activeDraft) && provider.Answers == 1,
            "image history reuses the live language/question draft and content task after destination handoff");
        provider.HoldAnswers.TrySetResult();
        await Until(() => activeDraft.ImportError != null && activeDraft.Conversation.Count == 1, "pending image question completes into the same visible draft after failed import");
        Check(Forms(language).Length == 1 && ReferenceEquals(Get(Forms(language).Single(), "Draft"), activeDraft) &&
            ReferenceEquals(Get(pendingChat, "Draft"), activeDraft) && provider.Answers == 1,
            "downstream recovery neither duplicates content requests nor leaves a stale history form");
        pendingChat.Close(); Forms(language).Single().Close();
    }

    private sealed class ImageRouteOcr : IImageOcrExtractor
    {
        internal int Calls;
        internal string Text = "lead";
        internal TaskCompletionSource? Hold;
        internal TaskCompletionSource? DrainCancellation;
        internal bool Cancelling;
        public async Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress, CancellationToken ct, string? resumeManifest = null)
        {
            Calls++;
            try { if (Hold != null) await Hold.Task.WaitAsync(ct); else await Task.Delay(50, ct); }
            catch (OperationCanceledException)
            { Cancelling = true; if (DrainCancellation != null) await DrainCancellation.Task; throw; }
            return new(Text, Path.Combine(output, "public-manifest.json"));
        }
    }
    private sealed class ImageRouteProvider : HttpMessageHandler
    {
        internal int Answers, VisualAnswers;
        internal string CleanedText = "lead";
        internal List<string> LanguageTargets = [];
        internal TaskCompletionSource? HoldAnswers;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The current desktop also reads public dictionaries and examples.
            // Keep those reads inside the offline fixture instead of parsing GET
            // requests as model POSTs or reaching an external corpus.
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.Host == "api.tatoeba.org")
                {
                    var query = request.RequestUri.Query.TrimStart('?').Split('&')
                        .Select(part => part.Split('=', 2)).First(part => part[0] == "q")[1];
                    var target = Uri.UnescapeDataString(query);
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                        data = new[] { new { id = 1, text = "We practice " + target + " every day." } } })) };
                }
                return new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var messages = body.RootElement.GetProperty("messages"); var system = messages[0].GetProperty("content").GetString()!;
            var user = messages[1].GetProperty("content"); object result;
            if (system.Contains("\"verdict\"")) result = new { verdict = "pass", errors = Array.Empty<object>(), warnings = Array.Empty<object>() };
            else if (system.Contains("\"cleaned_text\"")) result = new { cleaned_text = CleanedText, notes = Array.Empty<object>() };
            else if (system.Contains("\"pronunciation\""))
            {
                using var input = JsonDocument.Parse(user.GetString()!);
                LanguageTargets.Add(input.RootElement.GetProperty("selectedText").GetString()!);
                var fields = new Dictionary<string, object?> { ["status"] = "ready", ["pronunciation"] = "/liːd/", ["explanationEn"] = "to guide or direct",
                    ["sentenceTranslationZh"] = "他们带领这个团队。", ["normalizedSentence"] = null,
                    ["generatedSentence"] = input.RootElement.TryGetProperty("contextText", out var sourceContext) && sourceContext.ValueKind == JsonValueKind.String ? null : "They lead the team.",
                    ["generatedTargetStart"] = null, ["unresolvedReasons"] = Array.Empty<string>() };
                if (system.Contains("\"lemma\"")) fields["lemma"] = "lead";
                result = fields;
            }
            else
            {
                Answers++; if (user.ValueKind == JsonValueKind.Array) VisualAnswers++;
                if (HoldAnswers != null) await HoldAnswers.Task.WaitAsync(cancellationToken);
                result = new { answer_state = "answered", answer_text = "To lead means to guide.", unresolved_reasons = Array.Empty<string>() };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(result) } } } })) };
        }
    }
}

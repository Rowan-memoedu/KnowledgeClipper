using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Workflow;

internal static class PublishedLanguageChecks
{
    internal static async Task RunAsync(string repository, string output, bool live)
    {
        Directory.CreateDirectory(output);
        var configuration = ModeConfigurationLoader.Load(Path.Combine(repository, "modes"));
        var modes = configuration.Modes.Where(m => LanguageModes.IsConcrete(m.Definition)).ToArray();
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); } catch (InvalidDataException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        Check(modes.Length == 4 && modes.All(m => m.LoadError == null && PublishedLanguage.Applies(m.Definition)), "all four published processors load");
        Check(AnkiMathText.Normalize("Before\n\n\\[\n\\begin{pmatrix}1 & 2 \\\\ 3 & 4\\end{pmatrix}\n\\]\nAfter") ==
            "Before\n\n\\[ \\begin{pmatrix}1 & 2 \\\\ 3 & 4\\end{pmatrix} \\]\nAfter", "math normalization preserves matrix rows and prose paragraphs");
        Check(AnkiMathText.Normalize("\\(x% comment\n+1\\) and \\(5\\%\\)") == "\\(x +1\\) and \\(5\\%\\)", "math normalization respects TeX comments and escaped percent");
        var settings = new LanguageSettings { ApiBaseUrl = "http://127.0.0.1", ContentMaxRetries = 0, AnkiRootDeck = "Published fixture" };
        using var handler = new Fixture(); using var http = new HttpClient(handler);
        var processor = new ContentModule(http, () => "fixture");
        var store = new DraftStore(Path.Combine(output, "fixture-state"));
        foreach (var mode in modes)
        {
            var en = mode.Definition.Language == "en";
            var target = en ? "lead" : "重";
            var source = en ? "😀 <selected>lead</selected> & lead.\r\n" : "😀 <selected>重</selected> & 重。\r\n";
            var material = new CaptureMaterial(target, source, "source", source.LastIndexOf(target, StringComparison.Ordinal), "Published fixture", 0, DateTimeOffset.UtcNow);
            var raw = Ready(mode.Definition);
            handler.Raw = raw.ToJsonString();
            var result = await processor.ProcessAsync(material, mode.Definition.Id, configuration, settings, CancellationToken.None);
            Check(handler.Input.GetProperty("markedContext").GetString() == "😀 &lt;selected&gt;" + target + "&lt;/selected&gt; &amp; <selected>" + target + "</selected>" + (en ? "." : "。") + "\r\n" &&
                handler.Input.GetProperty("contextText").GetString() == source, mode.Definition.Id + " preserves source, escapes markup, and marks the chosen UTF-16 occurrence");
            var draft = new ClipDraft { Original = material, Material = material, Configuration = configuration, Settings = settings,
                ModeId = mode.Definition.Id, Route = new(mode.Definition.Id, "language", false, true), Practice = mode.Definition.Practice!.Value, Content = result };
            draft.DictionaryDetails = new DirectDictionaryDetails(target, mode.Definition.Language!, "fixture translation", "noun", "fixture definition", null, "ready");
            store.Save(draft);
            Check(store.Find(draft.Id)!.Content!.Fields!.Format == PublishedLanguage.Format && store.Find(draft.Id)!.Material == material,
                mode.Definition.Id + " saves version and exact original material");
            var faces = PublishedLanguage.Render(mode.Definition, material, result.Fields!);
            var note = AnkiNoteDraft.From(draft);
            var actual = note.PublishedFaces(note.Ordinals.Single());
            var expected = mode.Definition.Practice == PracticeChoice.Reading ? (faces.ReadingFront, faces.ReadingBack) : (faces.WritingFront, faces.WritingBack);
            Check(actual == (expected.Item1.Replace("\r\n", "\n"), expected.Item2.Replace("\r\n", "\n") + "\n\n" + draft.DictionaryDetails.DisplayText) &&
                note.Fields["SentenceBefore"].Contains("&lt;selected&gt;") && note.Model.EndsWith(PublishedLanguage.DictionaryModelSuffix),
                mode.Definition.Id + " preview and encoded Anki mapping agree without interpreting source HTML");
            Check(en ? faces.ReadingBack.Split('\n').Length == 3 && faces.WritingBack.Split('\n').Length == 2 && !faces.WritingFront.Contains("（") :
                faces.WritingBack == target && faces.WritingFront.Contains("（zhòng；"), mode.Definition.Id + " exact published rows and punctuation");
            Check((faces.Warning != null) == (mode.Definition.Practice == PracticeChoice.Writing), mode.Definition.Id + " repeated answer warning follows writing mode");

            var generated = material with { ContextText = null, ContextOrigin = "generated_allowed", TargetStart = null };
            raw["generatedSentence"] = en ? "😀 We lead today." : "😀 这很重。";
            handler.Raw = raw.ToJsonString();
            var generatedResult = await processor.ProcessAsync(generated, mode.Definition.Id, configuration, settings, CancellationToken.None);
            Check(generatedResult.Fields!.GeneratedTargetStart == generatedResult.Fields.GeneratedSentence!.IndexOf(target, StringComparison.Ordinal) &&
                handler.Input.GetProperty("markedContext").ValueKind == JsonValueKind.Null, mode.Definition.Id + " computes generated UTF-16 position from raw null");
            raw["generatedTargetStart"] = 0; handler.Raw = raw.ToJsonString();
            await Reject(() => processor.ProcessAsync(generated, mode.Definition.Id, configuration, settings, CancellationToken.None), mode.Definition.Id + " rejects numeric model position");
            raw["generatedTargetStart"] = null; raw["generatedSentence"] = target + " " + target; handler.Raw = raw.ToJsonString();
            var repeated = await processor.ProcessAsync(generated, mode.Definition.Id, configuration, settings, CancellationToken.None);
            var repeatedFaces = PublishedLanguage.Render(mode.Definition, generated, repeated.Fields!);
            var repeatedDraft = new ClipDraft { Original = generated, Material = generated, Configuration = configuration, Settings = settings,
                ModeId = mode.Definition.Id, Content = repeated, Practice = mode.Definition.Practice!.Value };
            var repeatedNote = AnkiNoteDraft.From(repeatedDraft);
            Check(repeated.Fields!.GeneratedTargetStart == 0 && repeatedFaces.TargetStart == 0 && repeatedNote.Fields["SentenceBefore"] == "" &&
                repeatedNote.Fields["SentenceAfter"] == " " + target, mode.Definition.Id + " repeated generated target renders and exports first occurrence");
            Check(PublishedLanguage.EffectiveTargetStart(material, result.Fields!) == material.TargetStart && material.TargetStart > source.IndexOf(target, StringComparison.Ordinal),
                mode.Definition.Id + " source still uses selected second occurrence");
            var ready = Ready(mode.Definition);
            var missing = (JsonObject)ready.DeepClone(); missing.Remove(en ? "explanationEn" : "meaning"); handler.Raw = missing.ToJsonString();
            await Reject(() => processor.ProcessAsync(material, mode.Definition.Id, configuration, settings, CancellationToken.None), mode.Definition.Id + " rejects missing required field");
            ready["foreignField"] = "extra"; handler.Raw = ready.ToJsonString();
            await Reject(() => processor.ProcessAsync(material, mode.Definition.Id, configuration, settings, CancellationToken.None), mode.Definition.Id + " rejects foreign fields");
            ready = Ready(mode.Definition); ready[en ? "explanationEn" : "meaning"] = "first\nsecond"; handler.Raw = ready.ToJsonString();
            await Reject(() => processor.ProcessAsync(material, mode.Definition.Id, configuration, settings, CancellationToken.None), mode.Definition.Id + " rejects multiline learning field");
            handler.Raw = Pending(mode.Definition).ToJsonString();
            var pending = await processor.ProcessAsync(material, mode.Definition.Id, configuration, settings, CancellationToken.None);
            draft.Content = pending; store.Save(draft);
            Check(store.Find(draft.Id)!.Content!.Fields!.IsUnresolved && store.Find(draft.Id)!.Content!.Fields!.UnresolvedReasons.Single() == "当前语境不足以确定读音。",
                mode.Definition.Id + " preserves unresolved status and reasons");
            await Reject(() => Task.FromResult(AnkiNoteDraft.From(draft)), mode.Definition.Id + " blocks unresolved export");
            await Reject(() => Task.FromResult(PublishedLanguage.Render(mode.Definition, material, pending.Fields!)), mode.Definition.Id + " blocks normal unresolved card faces");
            var inconsistent = Pending(mode.Definition); inconsistent["pronunciation"] = "/a/"; handler.Raw = inconsistent.ToJsonString();
            await Reject(() => processor.ProcessAsync(material, mode.Definition.Id, configuration, settings, CancellationToken.None), mode.Definition.Id + " rejects unresolved result with learning content");
        }
        Check(PublishedLanguage.FirstTarget("aaa", "aa") == 0, "overlapping generated targets use first occurrence");
        await Reject(() => Task.FromResult(PublishedLanguage.UniqueTarget("aaa", "aa")), "ambiguous normalized source remains rejected for fallback");
        var english = modes.Single(m => m.Definition.Id == "language-en");
        var enSource = new CaptureMaterial("lead", "They lead.", "source", 5, "fixture", 0, DateTimeOffset.UtcNow);
        handler.Raw = Ready(english.Definition).ToJsonString().Replace("\"status\":\"ready\"", "\"status\":\"ready\",\"status\":\"ready\"");
        await Reject(() => processor.ProcessAsync(enSource, english.Definition.Id, configuration, settings, CancellationToken.None), "duplicate raw property rejected");
        var legacy = new LanguageFields("/liːd/", "带领", null, null);
        var legacyConfig = configuration with { Modes = configuration.Modes.Select(m => m with { Definition = m.Definition with { ContentFormat = null } }).ToArray() };
        var savedLegacy = new ClipDraft { Material = enSource, Original = enSource, Configuration = legacyConfig, Settings = settings,
            ModeId = english.Definition.Id, Content = new(english.Definition.Id, legacy), Practice = PracticeChoice.Reading };
        Check(!AnkiNoteDraft.From(savedLegacy).IsPublishedLanguage && AnkiNoteDraft.From(savedLegacy).Model == settings.AnkiModel,
            "saved legacy results retain legacy model and meaning");
        savedLegacy.Configuration = configuration;
        await Reject(() => Task.FromResult(AnkiNoteDraft.From(savedLegacy)), "old four-field result cannot masquerade as current publication");

        var parent = new ClipDraft { Original = enSource, Material = enSource, Configuration = configuration, Settings = settings,
            ModeId = "language", Route = new("language", "language", false, true), Practice = PracticeChoice.Both };
        var children = LanguageModeCards.Ensure(parent, modes.Where(m => m.Definition.Language == "en").Select(m => m.Definition).ToArray(), store);
        foreach (var child in children)
        {
            var mode = modes.Single(m => m.Definition.Id == child.ModeId).Definition;
            child.Content = new(child.ModeId!, PublishedLanguage.Parse((child.Practice == PracticeChoice.Reading ? Ready(mode) : Pending(mode)).ToJsonString(), mode, enSource));
            store.Save(child);
        }
        var before = handler.Calls;
        await Reject(() => LanguageModeCards.ImportAsync(parent, parent.SelectedModeIds, store, new AnkiConnection(http, "http://127.0.0.1")), "mixed ready/pending batch blocks before provider write");
        Check(handler.Calls == before && children.All(c => store.Find(c.Id)!.ConfirmedNote == null), "failed batch does not freeze or write ready sibling");
        foreach (var mode in modes.Where(m => m.Definition.Language == "en"))
        {
            var mixed = new CaptureMaterial("teamwork", "这项任务需要 teamwork，任何人都无法独自完成。", "source", 7, "fixture", 0, DateTimeOffset.UtcNow);
            var raw = Ready(mode.Definition);
            raw["normalizedSentence"] = "This task requires teamwork; no one can complete it alone.";
            raw["pronunciation"] = "/ˈtiːmwɜːrk/";
            raw["explanationEn"] = "Cooperative effort toward a shared goal.";
            raw["sentenceTranslationZh"] = "这项任务需要团队合作，任何人都无法独自完成。";
            if (mode.Definition.Practice == PracticeChoice.Reading) raw["lemma"] = "teamwork";
            handler.Raw = raw.ToJsonString();
            var result = await processor.ProcessAsync(mixed, mode.Definition.Id, configuration, settings, CancellationToken.None);
            var draft = new ClipDraft { Original = mixed, Material = mixed, Configuration = configuration, Settings = settings,
                ModeId = mode.Definition.Id, Content = result, Practice = mode.Definition.Practice!.Value };
            store.Save(draft);
            var note = AnkiNoteDraft.From(store.Find(draft.Id)!);
            Check(note.Fields["SentenceBefore"] == "This task requires " && note.Fields["SentenceAfter"] == "; no one can complete it alone." &&
                store.Find(draft.Id)!.Material.ContextText == mixed.ContextText, mode.Definition.Id + " translated context repositions target and preserves source through save/export");
            raw["normalizedSentence"] = null; handler.Raw = raw.ToJsonString();
            var fallback = await processor.ProcessAsync(mixed, mode.Definition.Id, configuration, settings, CancellationToken.None);
            Check(fallback.Fields!.Status == "ready" && PublishedLanguage.EffectiveSentence(mixed, fallback.Fields) == mixed.ContextText,
                mode.Definition.Id + " accepts useful mixed-source fallback without blocking the card");
            raw["normalizedSentence"] = "This teamwork needs teamwork."; handler.Raw = raw.ToJsonString();
            var ambiguous = await processor.ProcessAsync(mixed, mode.Definition.Id, configuration, settings, CancellationToken.None);
            Check(ambiguous.Fields!.Status == "ready" && ambiguous.Fields.NormalizedSentence == null &&
                PublishedLanguage.EffectiveTargetStart(mixed, ambiguous.Fields) == mixed.TargetStart, mode.Definition.Id + " ambiguous translation falls back to verified source span");
            raw["normalizedSentence"] = "这需要 teamwork."; handler.Raw = raw.ToJsonString();
            Check((await processor.ProcessAsync(mixed, mode.Definition.Id, configuration, settings, CancellationToken.None)).Fields!.Status == "ready",
                mode.Definition.Id + " accepts approximate bilingual conversion");
        }
        var generatedBranch = GeneratedExampleDrafts.Ensure(parent, store);
        Check(generatedBranch.Id != parent.Id && generatedBranch.ParentDraftId == parent.Id && generatedBranch.Original == parent.Original &&
            generatedBranch.Material.ContextText == null && generatedBranch.Material.TargetStart == null && generatedBranch.Material.ContextOrigin == "generated_allowed" &&
            generatedBranch.Practice == parent.Practice && !generatedBranch.Input.ReadRequested, "generated option creates an independent context-free draft with original provenance");
        Check(GeneratedExampleDrafts.Ensure(store.Find(parent.Id)!, store).Id == generatedBranch.Id && parent.Material.ContextText == enSource.ContextText,
            "repeated click or reopen reuses generated branch without overwriting original");
        var generatedChildren = LanguageModeCards.Ensure(generatedBranch, modes.Where(m => m.Definition.Language == "en").Select(m => m.Definition).ToArray(), store);
        Check(generatedChildren.All(child => !children.Any(original => original.Id == child.Id) && child.ModeParentId == generatedBranch.Id) &&
            children.Single(child => child.Practice == PracticeChoice.Reading).Content!.Fields!.Status == "ready", "original and generated mode results remain independent");
        await TemplateRepairChecks.RunAsync(configuration, settings, Check);
        WorkflowJson.Save(Path.Combine(output, "published-local.json"), new { outcome = "PASS", checks, boundary = "Synthetic responses through production request/schema/parser/export mapping; no content quality claim." });
        if (live) await PublishedLanguageLive.RunAsync(repository, output);
    }

    internal static JsonObject Ready(ModeDefinition mode)
    {
        var en = mode.Language == "en";
        var result = new JsonObject { ["status"] = "ready", ["pronunciation"] = en ? "/lɛd/" : "zhòng", ["generatedSentence"] = null,
            ["generatedTargetStart"] = null, ["unresolvedReasons"] = new JsonArray() };
        if (en)
        {
            result["normalizedSentence"] = null;
            result["explanationEn"] = mode.Practice == PracticeChoice.Reading ? "a heavy metal" : "a dense metal used in pipes";
            result["sentenceTranslationZh"] = "这是一种金属。";
            if (mode.Practice == PracticeChoice.Reading) result["lemma"] = "lead";
        }
        else result["meaning"] = mode.Practice == PracticeChoice.Reading ? "重量大" : "分量大";
        return result;
    }
    internal static JsonObject Pending(ModeDefinition mode)
    {
        var result = Ready(mode);
        foreach (var key in result.Select(p => p.Key).ToArray()) result[key] = null;
        result["status"] = "unresolved"; result["unresolvedReasons"] = new JsonArray("当前语境不足以确定读音。");
        return result;
    }
    private sealed class Fixture : HttpMessageHandler
    {
        internal string Raw = ""; internal JsonElement Input; internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            using var input = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Input = input.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = Raw } } } }) };
        }
    }
}

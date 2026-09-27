using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Reflection;
using KnowledgeClipper.Workflow;

var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }

if (args.Contains("--live"))
{
    using var liveHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var liveLookup = new DirectDictionaryLookup(liveHttp);
    var liveEnglish = await liveLookup.LookupAsync("take off", "en");
    var liveChinese = await liveLookup.LookupAsync("朋友", "zh");
    var liveExample = await new AuthenticExampleLookup(liveHttp).FindAsync("kinetic", "en");
    Check(liveEnglish.Status == "ready" && liveEnglish.Translation == null && liveEnglish.Definition != null &&
        liveEnglish.PartOfSpeech != null, "LIVE English phrase returns dictionary facts without unreliable machine gloss");
    Check(liveChinese.Status == "ready" && liveChinese.Translation == null && liveChinese.Definition != null,
        "LIVE Chinese word returns dictionary facts without unreliable machine gloss");
    Check(liveExample is { } && liveExample.Value.Sentence.Contains("kinetic") && liveExample.Value.Source.Contains("Tatoeba #"),
        "LIVE context-free word finds an attributed authentic example");
    Console.WriteLine("3 live direct dictionary checks passed");
    return;
}

using var http = new HttpClient(new FixtureHandler());
var lookup = new DirectDictionaryLookup(http);
var english = await lookup.LookupAsync("take off", "en");
Check(english.Translation == null && english.PartOfSpeech == "Verb" && english.Definition == "To remove." &&
    english.Example == "He took off his shoes." && english.Status == "ready", "English phrase uses source dictionary details without machine gloss");
Check(!english.DisplayText.Contains('<') && english.DisplayText.Contains("来源：Wiktionary"), "provider HTML is stripped and source is visible");
Check(english.Entries.Length == 2 && english.Entries[0].Senses.Length == 2 &&
    english.Entries[0].Senses[0].Examples.Length == 2 && english.DisplayText.Contains("释义 2：To leave") &&
    english.DisplayText.Contains("词性：Noun") && english.DisplayText.Contains("词源：From", StringComparison.Ordinal),
    "all returned parts of speech, senses, examples and etymology survive lookup");
var chinese = await lookup.LookupAsync("朋友", "zh");
Check(chinese.Translation == null && chinese.PartOfSpeech == "Noun" && chinese.Definition == "friend" &&
    chinese.Example == null, "Chinese word retains available facts without inventing an example");
var absent = await lookup.LookupAsync("unlisted", "en");
Check(absent.Status == "unavailable" && absent.Translation == null && absent.Definition == null &&
    absent.DisplayText.Contains("暂不可用"), "missing provider data remains explicit");
Check(!new LanguageSettings().ShowDictionaryInPreview, "dictionary preview defaults off");
var example = await new AuthenticExampleLookup(http).FindAsync("kinetic", "en");
Check(example is { } && example.Value.Sentence == "That kinetic typography video you made was awesome, Jim!" &&
    example.Value.Source.Contains("Tatoeba #11294501"), "context-free example comes from the attributed corpus");
var noContext = new CaptureMaterial("kinetic", null, "generated_allowed", null, "capture", 0, DateTimeOffset.UtcNow);
var sourced = AuthenticExampleLookup.Apply(noContext, example!.Value);
Check(sourced.ContextText == example.Value.Sentence && sourced.TargetStart == 5 && sourced.ContextOrigin == "source" &&
    sourced.SourceTitle.Contains("原选区来源：capture") && noContext.ContextText == null,
    "corpus example is fixed before the model call while the original capture stays intact");
var fallbackDictionary = new DirectDictionaryDetails("motion", "en", null, "Noun", "Movement", null, "ready")
{ Entries = [new("Noun", [new("Movement", ["The motion was slow."])])] };
var fallback = await new AuthenticExampleLookup(http).FindWithFallbackAsync("motion", "en", fallbackDictionary);
Check(fallback?.Sentence == "The motion was slow." && fallback?.Source.StartsWith("Wiktionary") == true,
    "a complete dictionary example is used when the corpus has no exact target");

var mode = new ModeDefinition("language-en", "English reading", true, "", "", "", Language: "en",
    Practice: PracticeChoice.Reading, ContentFormat: PublishedLanguage.Format);
var alteredSentence = "{\"status\":\"ready\",\"generatedSentence\":null,\"normalizedSentence\":\"That kinetic motion looked good.\",\"explanationEn\":\"Moving.\",\"sentenceTranslationZh\":\"那个动态效果不错。\",\"lemma\":\"kinetic\",\"pronunciation\":\"/kɪˈnɛtɪk/\",\"generatedTargetStart\":null,\"unresolvedReasons\":[]}";
try { PublishedLanguage.Parse(alteredSentence, mode, sourced); throw new Exception("Corpus rewrite accepted"); }
catch (InvalidDataException) { Check(true, "model cannot replace an attributed corpus example"); }
var material = new CaptureMaterial("take off", "They take off their shoes.", "source", 5, "fixture", 0, DateTimeOffset.UtcNow);
var fields = new LanguageFields("/teɪk ɔːf/", null, null, null) { Format = PublishedLanguage.Format, Status = "ready",
    ExplanationEn = "To remove clothing.", SentenceTranslationZh = "他们脱下鞋子。" };
var draft = new ClipDraft { Original = material, Material = material, Settings = new LanguageSettings(),
    Configuration = new ContentConfiguration("", [new ModeSnapshot(mode, "fixture", "{}")]), ModeId = mode.Id,
    Route = new ModeRoute(mode.Id, "language", false, true), Content = new ContentResult(mode.Id, fields),
    Practice = PracticeChoice.Reading, DictionaryDetails = english };
var note = AnkiNoteDraft.From(draft);
Check(note.HasDictionaryDetails && note.Model == PublishedLanguage.DictionaryModelName(draft.Settings.AnkiModel) &&
    note.Fields["DictionaryDetails"].Contains("例句："), "new language card has its own persistent dictionary field and note type");
Check(note.PublishedFaces(0).Back.EndsWith(english.DisplayText, StringComparison.Ordinal), "dictionary appears at the bottom of the card answer");
var remnote = RemNoteCardConversion.Convert(note);
Check(remnote.Single().Back.Last().Any(part => part.Text?.Contains("来源：Wiktionary") == true),
    "RemNote export also places the separate field after the original answer");
var frozen = note with { Fields = note.Fields.Where(pair => pair.Key != "DictionaryDetails")
    .ToDictionary(pair => pair.Key, pair => pair.Value), Model = PublishedLanguage.ModelName(draft.Settings.AnkiModel) };
Check(!frozen.HasDictionaryDetails && !frozen.PublishedFaces(0).Back.Contains("划词词典"),
    "old frozen language note keeps its original answer layout");
using var ankiHandler = new AnkiFixtureHandler(); using var ankiHttp = new HttpClient(ankiHandler);
var connection = new AnkiConnection(ankiHttp, "http://127.0.0.1:8765");
var prepare = typeof(AnkiConnection).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
await (Task)prepare.Invoke(connection, [note, CancellationToken.None])!;
await (Task)prepare.Invoke(connection, [frozen, CancellationToken.None])!;
Check(ankiHandler.Models[note.Model].GetProperty("inOrderFields").EnumerateArray().Last().GetString() == "DictionaryDetails" &&
    ankiHandler.Models[note.Model].GetProperty("cardTemplates").EnumerateArray().All(t => t.GetProperty("Back").GetString()!.EndsWith("<details class='dictionary'><summary>划词词典</summary><div>{{DictionaryDetails}}</div></details>")) &&
    ankiHandler.Models[frozen.Model].GetProperty("inOrderFields").EnumerateArray().All(f => f.GetString() != "DictionaryDetails"),
    "Anki creates a separate dictionary note type while old frozen type stays intact");
foreach (var isEnglish in new[] { true, false })
foreach (var ordinal in new[] { 0, 1 })
{
    var verifyNote = note with { Fields = new(note.Fields), Ordinals = [ordinal], CardDecks = null };
    verifyNote.Fields["EnglishMode"] = isEnglish ? "1" : "";
    verifyNote.Fields["Meaning"] = isEnglish ? "" : "脱下";
    verifyNote.Fields["ReadingEnabled"] = ordinal == 0 ? "1" : "";
    verifyNote.Fields["WritingEnabled"] = ordinal == 1 ? "1" : "";
    ankiHandler.ReadbackNote = verifyNote;
    ankiHandler.Ordinal = ordinal;
    var verifiedDraft = new ClipDraft { Original = material, Material = material, Settings = draft.Settings,
        Configuration = draft.Configuration, ConfirmedNote = verifyNote, NoteId = 42 };
    await connection.VerifyAsync(verifiedDraft);
    Check(verifiedDraft.CardIds.SequenceEqual(new long[] { 43 }),
        $"rendered {(isEnglish ? "English" : "Chinese")} template {ordinal} verifies with collapsed dictionary and speech controls");
    foreach (var mutation in new[] { "label", "dictionary", "translation" })
    {
        ankiHandler.Mutation = mutation;
        try { await connection.VerifyAsync(verifiedDraft); throw new Exception("Incorrect rendered content accepted"); }
        catch (InvalidDataException) { Check(true, $"template {ordinal} still rejects altered {mutation}"); }
    }
    ankiHandler.Mutation = null;
}
if (args.Contains("--live-import"))
{
    var liveStore = new DraftStore(@"D:\Caches\KnowledgeClipper\anki-dictionary-live-20260927");
    draft.ConfirmedNote = note with { Deck = "KnowledgeClipper Canary::Anki Dictionary 20260927", CardDecks = null };
    draft.ImportState = ImportState.Ready;
    liveStore.Save(draft);
    using var realHttp = new HttpClient();
    var realAnki = new AnkiConnection(realHttp, "http://127.0.0.1:8765");
    await realAnki.ImportAsync(draft, liveStore);
    await realAnki.VerifyAsync(draft);
    var originalNoteId = draft.NoteId;
    await realAnki.ImportAsync(liveStore.Find(draft.Id)!, liveStore);
    var matching = await realAnki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id });
    Check(draft.ImportState == ImportState.Verified && draft.CardIds.Length == 1 && matching.GetArrayLength() == 1 &&
        matching[0].GetInt64() == originalNoteId, "LIVE fresh folded-dictionary note imports, verifies and retries without duplication");
    WorkflowJson.Save(@"D:\Artifacts\KnowledgeClipper\anki-dictionary-recovery-20260927\validation\independent-live.json",
        new { passed = true, draft.Id, draft.NoteId, draft.CardIds, draft.ImportState, deck = draft.ConfirmedNote.Deck });
}
Console.WriteLine($"{checks} direct dictionary checks passed");

sealed class FixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        string json; var status = HttpStatusCode.OK;
        if (url.Contains("api.tatoeba.org", StringComparison.Ordinal))
            json = "{\"data\":[{\"id\":11294501,\"text\":\"That kinetic typography video you made was awesome, Jim!\"}]}";
        else if (url.Contains("prop=sections", StringComparison.Ordinal) && url.Contains("take%20off", StringComparison.OrdinalIgnoreCase))
            json = "{\"parse\":{\"sections\":[{\"index\":\"1\",\"line\":\"English\",\"level\":\"2\"},{\"index\":\"2\",\"line\":\"Etymology\",\"level\":\"3\"}]}}";
        else if (url.Contains("prop=text", StringComparison.Ordinal) && url.Contains("take%20off", StringComparison.OrdinalIgnoreCase))
            json = "{\"parse\":{\"text\":{\"*\":\"<h3>Etymology</h3><p>From <b>take</b> + off.</p>\"}}}";
        else if (url.Contains("take%20off", StringComparison.OrdinalIgnoreCase))
            json = "{\"en\":[{\"partOfSpeech\":\"Verb\",\"definitions\":[{\"definition\":\"To <b>remove</b>.\",\"examples\":[\"He <b>took off</b> his shoes.\",\"They took off their coats.\"]},{\"definition\":\"To leave.\",\"examples\":[\"The plane took off.\"]}]},{\"partOfSpeech\":\"Noun\",\"definitions\":[{\"definition\":\"A departure.\"}]}]}";
        else if (url.Contains("%E6%9C%8B%E5%8F%8B", StringComparison.OrdinalIgnoreCase))
            json = "{\"zh\":[{\"partOfSpeech\":\"Noun\",\"definitions\":[{\"definition\":\"<a href='/wiki/friend'>friend</a>\"}]}]}";
        else { json = "{}"; status = HttpStatusCode.NotFound; }
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

sealed class AnkiFixtureHandler : HttpMessageHandler
{
    internal Dictionary<string, JsonElement> Models = [];
    internal AnkiNoteDraft? ReadbackNote;
    internal int Ordinal;
    internal string? Mutation;
    private string Render(string side)
    {
        var note = ReadbackNote!;
        var template = Models[note.Model].GetProperty("cardTemplates")[Ordinal].GetProperty(side).GetString()!;
        var fields = new Dictionary<string, string>(note.Fields);
        if (side == "Back") fields["FrontSide"] = Render("Front");
        var condition = new System.Text.RegularExpressions.Regex(@"\{\{([#^])(\w+)\}\}(.*?)\{\{/\2\}\}", System.Text.RegularExpressions.RegexOptions.Singleline);
        while (condition.IsMatch(template)) template = condition.Replace(template, match =>
            (fields.GetValueOrDefault(match.Groups[2].Value, "").Length > 0) == (match.Groups[1].Value == "#") ? match.Groups[3].Value : "");
        template = System.Text.RegularExpressions.Regex.Replace(template, @"\{\{(\w+)\}\}", match => fields.GetValueOrDefault(match.Groups[1].Value, ""));
        if (side == "Back")
        {
            if (Mutation == "label") template = template.Replace("<summary>划词词典</summary>", "<summary>错误标题</summary>");
            if (Mutation == "dictionary") template = template.Replace(note.Fields["DictionaryDetails"], "错误词典正文");
            if (Mutation == "translation") template += "<div>额外错误译文</div>";
        }
        return "<style>.card{color:black}</style>" + template + "<!-- sftts-controls:start --><div>speech UI</div><!-- sftts-controls:end -->";
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var action = json.RootElement.GetProperty("action").GetString();
        var parameters = json.RootElement.GetProperty("params");
        object? result = action switch
        {
            "modelNames" => Models.Keys.ToArray(),
            "createModel" => Save(parameters),
            "modelFieldNames" => Models[parameters.GetProperty("modelName").GetString()!].GetProperty("inOrderFields"),
            "modelTemplates" => Models[parameters.GetProperty("modelName").GetString()!].GetProperty("cardTemplates")
                .EnumerateArray().ToDictionary(t => t.GetProperty("Name").GetString()!, t => t),
            "deckNames" => new[] { "零碎知识剪藏::语言学习" },
            "createDeck" => 1,
            "notesInfo" => new[] { new { noteId = 42, modelName = ReadbackNote!.Model,
                fields = ReadbackNote.Fields.ToDictionary(p => p.Key, p => new { value = p.Value }), tags = ReadbackNote.Tags, cards = new[] { 43 } } },
            "cardsInfo" => new[] { new { cardId = 43, note = 42, ord = Ordinal, deckName = ReadbackNote!.Deck,
                queue = 0, question = Render("Front"), answer = Render("Back") } },
            "areSuspended" => new[] { false },
            _ => throw new Exception("Unexpected Anki request: " + action)
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
    private object Save(JsonElement parameters)
    {
        Models.Add(parameters.GetProperty("modelName").GetString()!, parameters.Clone());
        return new { };
    }
}

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

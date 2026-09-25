using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Workflow;

// These distinct local responses exercise interfaces; they are not production prompt or quality evidence.
var repository = args[0];
var artifacts = args[1];
if (args.Contains("--validation-retry")) { await ValidationRetryChecks.RunAsync(repository, artifacts, args.Contains("--live")); return; }
if (args.Contains("--tolerance-live")) { await ContentToleranceLiveChecks.RunAsync(repository, artifacts); return; }
if (args.Contains("--published")) { await PublishedLanguageChecks.RunAsync(repository, artifacts, args.Contains("--live")); return; }
var fixture = Path.Combine(@"D:\Caches\KnowledgeClipper\mode-content-tests", Guid.NewGuid().ToString("N"));
var defaults = Path.Combine(fixture, "defaults");
var overrides = Path.Combine(fixture, "overrides");
Directory.CreateDirectory(defaults); Directory.CreateDirectory(overrides);
var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}

var definitions = ModeConfigurationLoader.LoadDefinitions(Path.Combine(repository, "modes"));
// Keep this fixture on the saved legacy four-field interface. Published-language checks use the real schemas separately.
definitions = definitions with { Modes = definitions.Modes.Select(mode => mode with { ContentFormat = null }).ToArray() };
var concrete = definitions.Modes.Where(LanguageModes.IsConcrete).ToArray();
Check(concrete.Length == 4 && concrete.Select(mode => mode.PromptFile).Distinct().Count() == 4 &&
    concrete.All(mode => !string.IsNullOrWhiteSpace(mode.SchemaFile)), "four concrete processors have independent prompts and configurable schema references");
WorkflowJson.Save(Path.Combine(defaults, "modes.json"), definitions);
File.WriteAllText(Path.Combine(defaults, "classification.md"), "fixture-classifier");
foreach (var mode in definitions.Modes)
{
    File.WriteAllText(Path.Combine(defaults, mode.PromptFile), "fixture:" + mode.Id);
    var schemaPath = Path.Combine(repository, "modes", mode.OutputKind + ".schema.json");
    var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!;
    if (LanguageModes.IsConcrete(mode))
    {
        schema["title"] = "Local interface fixture: " + mode.Id;
        schema["properties"]!["meaning"]!["enum"] = new JsonArray(mode.Id + " meaning");
    }
    File.WriteAllText(Path.Combine(defaults, mode.SchemaFile), schema.ToJsonString());
}
var config = ModeConfigurationLoader.Load(defaults);
Check(concrete.All(mode => LanguageModes.ClassificationChoices(config).Single(choice => choice.Id == "language").Description.Contains(mode.Description)),
    "classification uses the editable descriptions of all concrete modes");
var settings = new LanguageSettings { ApiBaseUrl = "http://fixture.invalid", AnkiRootDeck = "Fixture root" };
var en = new CaptureMaterial("lead", "They lead the team.", "source", 5, "local fixture", 0, DateTimeOffset.UtcNow);
var zh = new CaptureMaterial("带领", "他带领团队。", "source", 1, "local fixture", 0, DateTimeOffset.UtcNow);
var enModes = LanguageModes.Select(config, en, PracticeChoice.Both);
var zhModes = LanguageModes.Select(config, zh, PracticeChoice.Both);
Check(enModes.Select(mode => mode.Practice!.Value).SequenceEqual(new[] { PracticeChoice.Reading, PracticeChoice.Writing }) &&
    enModes.All(mode => mode.Language == "en") && zhModes.All(mode => mode.Language == "zh"), "Both selects two independent concrete modes for the captured language");
Check(LanguageModes.Select(config, en, PracticeChoice.Writing).Single().Id == enModes[1].Id &&
    LanguageModes.Select(config, en, PracticeChoice.Reading).Single().Id == enModes[0].Id, "single practice selects only its concrete processor");
var sourceDoesNotChooseLanguage = en with { ContextText = "中文原文中的 lead 仍是目标。", TargetStart = 7 };
sourceDoesNotChooseLanguage.Validate();
Check(LanguageModes.Select(config, sourceDoesNotChooseLanguage, PracticeChoice.Reading).Single().Language == "en", "surrounding context does not change the selected target language");
var noLanguage = en with { SelectedText = "123", ContextText = null, ContextOrigin = "generated_allowed", TargetStart = null };
try { LanguageModes.Select(config, noLanguage, PracticeChoice.Both); throw new Exception("Expected language choice"); }
catch (ModeChoiceRequiredException) { Check(true, "ambiguous target requires a language choice instead of guessing a concrete processor"); }
Check(LanguageModes.Select(config, noLanguage, PracticeChoice.Both, zhModes[0].Id).All(mode => mode.Language == "zh"), "explicit concrete choice resolves the language while preserving Both");

using var handler = new Handler();
using var http = new HttpClient(handler);
var content = new ContentModule(http, () => "local-fixture-key");
var modeId = await content.ClassifyAsync(en, config, settings, CancellationToken.None, new("selection-dot", "auto"));
var classifierInput = handler.Requests.Last().Input;
Check(modeId == "language" && classifierInput.GetProperty("modes").EnumerateArray().Count(m => m.GetProperty("id").GetString() == "language") == 1 &&
    !classifierInput.GetProperty("modes").EnumerateArray().Any(m => concrete.Any(c => c.Id == m.GetProperty("id").GetString())), "classification chooses a family without treating it as a content processor");
Check(ModeConfigurationLoader.Route(config, modeId) == new ModeRoute("language", "language", false, true), "family route retains language context and follow-up defaults");
var generated = await Task.WhenAll(enModes.Select(mode => content.ProcessAsync(en, mode.Id, config, settings, CancellationToken.None)));
Check(generated.Select(result => result.ModeId).SequenceEqual(enModes.Select(mode => mode.Id)) &&
    generated.Select(result => result.Fields!.Meaning).Distinct().Count() == 2, "Both sends two requests and retains independently generated results");
var generatedZh = await Task.WhenAll(zhModes.Select(mode => content.ProcessAsync(zh, mode.Id, config, settings, CancellationToken.None)));
Check(generatedZh.Select(result => result.Fields!.Meaning).Distinct().Count() == 2 && handler.Requests.Count(r => r.System.StartsWith("fixture:language")) == 4,
    "all four fixture processors use their own system prompt and response");
Check(handler.Requests.Where(r => r.System.StartsWith("fixture:language")).All(r => r.Input.GetProperty("contextOrigin").GetString() == "source"),
    "each concrete request keeps the original context provenance");
handler.CorruptMode = enModes[1].Id;
await Reject(() => content.ProcessAsync(en, enModes[1].Id, config, settings, CancellationToken.None), "a response that fits another mode fails this mode's schema");
Check(generated[0].Fields!.Meaning == enModes[0].Id + " meaning" && handler.Requests.Count(r => r.System.StartsWith("fixture:" + enModes[0].Id + "\n")) == 1,
    "a failed sibling does not modify or call the successful result again");
handler.CorruptMode = null;

var callsBeforeQuestion = handler.Requests.Count;
const string question = "解释：lead";
var metadata = new InputMetadata("selection-dot", "question", ExplicitQuestion: question);
Check(await content.ClassifyAsync(en, config, settings, CancellationToken.None, metadata) == "question" && handler.Requests.Count == callsBeforeQuestion,
    "explicit floating question bypasses classification without a question mark");
var answer = await content.ProcessAsync(en, "question", config, settings, CancellationToken.None, metadata.ExplicitQuestion);
var input = handler.Requests.Last().Input;
Check(input.GetProperty("question").GetString() == question && input.GetProperty("originalMaterial").GetProperty("selectedText").GetString() == "lead" &&
    input.GetProperty("originalMaterial").GetProperty("contextText").GetString() == en.ContextText && answer.Basic!.Front == question,
    "explicit question and card front stay separate from original selection and context");
var turn = new ConversationTurn(question, answer.Answer!);
await content.AnswerAsync(en, null, [turn], "再解释一个例子", config, settings, CancellationToken.None,
    currentPreview: new(en.SelectedText, null, [turn], generated));
var followUp = handler.Requests.Last().Input;
Check(followUp.GetProperty("conversation").GetArrayLength() == 1 && followUp.GetProperty("currentPreview").GetProperty("modeContents").GetArrayLength() == 2 &&
    followUp.GetProperty("question").GetString() == "再解释一个例子", "follow-up receives one ordered conversation and both distinct preview results");
var typedQuestion = noLanguage with { SelectedText = "这是为什么？  " };
callsBeforeQuestion = handler.Requests.Count;
Check(await content.ClassifyAsync(typedQuestion, config, settings, CancellationToken.None, new("typed", "lookup", true)) == "question" && handler.Requests.Count == callsBeforeQuestion,
    "typed terminal question marks keep deterministic question routing");
var lookup = new InputMetadata("typed", "lookup", true);
Check(await content.ClassifyAsync(zh, config, settings, CancellationToken.None, lookup) == "translation" &&
    !handler.Requests.Last().Input.GetProperty("modes").EnumerateArray().Any(m => m.GetProperty("id").GetString() == "question"),
    "typed Chinese lookup remains translation and excludes question from its candidates");
var translation = await content.ProcessAsync(zh, "translation", config, settings, CancellationToken.None);
Check(translation.Basic == new BasicFields(zh.SelectedText, "to lead"), "translation preserves the original Chinese front and English back");
Check(await content.ClassifyAsync(zh, config, settings, CancellationToken.None, new("selection-button", "auto")) == "language",
    "ordinary selected Chinese keeps language mode through entry metadata");

var legacy = new ModeDefinition("language", "legacy saved mode", true, "", "language.md", "language.schema.json");
var legacyConfig = new ContentConfiguration("saved classifier", [new(legacy, "saved prompt", "{}")]);
Check(!LanguageModes.HasConcrete(legacyConfig) && LanguageModes.ClassificationChoices(legacyConfig).Single() == legacy &&
    ModeConfigurationLoader.Route(legacyConfig, "language").OutputKind == "language", "saved generic-language configuration retains its legacy route");
var changedPath = enModes[0] with { Language = null, Practice = null, AnkiDeckPath = "Custom::English::Read" };
WorkflowJson.Save(Path.Combine(overrides, "modes.json"), new ModeFile([legacy, changedPath]));
var overridden = ModeConfigurationLoader.Load(defaults, overrides);
var overriddenMode = overridden.Modes.Single(mode => mode.Definition.Id == enModes[0].Id).Definition;
Check(overridden.Modes.Length == 6 && !overridden.Modes.Any(mode => mode.Definition.Id == "language") &&
    overriddenMode.Language == "en" && overriddenMode.Practice == PracticeChoice.Reading && overriddenMode.AnkiDeckPath == "Custom::English::Read",
    "old overrides preserve custom decks and inherit concrete metadata without restoring a generic processor");
var parent = new ClipDraft { Original = en, Material = en, Configuration = config, Settings = settings, ModeId = "language", Practice = PracticeChoice.Both };
Check(DeckRouting.Describe(parent) == "Fixture root::语言学习::英语::理解与朗读；Fixture root::语言学习::英语::书写练习",
    "English parent preview describes both concrete destinations before results exist");

var unavailable = definitions.Modes.Select(mode => mode.Id == enModes[1].Id ? mode with { PromptFile = "pending-writing.md", SchemaFile = "pending-writing.schema.json" } : mode).ToArray();
WorkflowJson.Save(Path.Combine(overrides, "modes.json"), new ModeFile(unavailable));
var missing = ModeConfigurationLoader.Load(defaults, overrides);
var unavailableMode = missing.Modes.Single(mode => mode.Definition.Id == enModes[1].Id);
Check(unavailableMode.LoadError!.Contains("pending-writing.md") && unavailableMode.LoadError.Contains("pending-writing.schema.json") &&
    missing.Modes.Where(mode => mode.Definition.Id != enModes[1].Id).All(mode => mode.LoadError == null), "missing processor files are reported per mode and do not disable other modes");
var beforeMissing = handler.Requests.Count;
await Reject(() => content.ProcessAsync(en, enModes[1].Id, missing, settings, CancellationToken.None), "unavailable processor fails before an API call");
Check(handler.Requests.Count == beforeMissing && !File.Exists(Path.Combine(defaults, "pending-writing.md")), "missing configuration is not replaced with invented prompt content");
var sibling = await content.ProcessAsync(en, enModes[0].Id, missing, settings, CancellationToken.None);
Check(sibling.Fields!.Meaning == enModes[0].Id + " meaning", "available independent processor remains usable when its sibling is unavailable");
var badPath = unavailable.Select(mode => mode.Id == enModes[1].Id ? mode with { PromptFile = "../outside.md" } : mode).ToArray();
WorkflowJson.Save(Path.Combine(overrides, "modes.json"), new ModeFile(badPath));
await Reject(() => { ModeConfigurationLoader.Load(defaults, overrides); return Task.CompletedTask; }, "invalid paths are rejected instead of being disguised as missing configuration");
using (var policyHandler = new PolicyHandler())
using (var policyHttp = new HttpClient(policyHandler) { Timeout = Timeout.InfiniteTimeSpan })
{
    var policyContent = new ContentModule(policyHttp, () => "public-fixture-key");
    var policySettings = settings with { Model = "edited-model-fixture", ContentMaxRetries = 0, ContentTimeoutSeconds = 1 };
    policyHandler.Failures = 1;
    await Reject(() => policyContent.ClassifyAsync(en, config, policySettings, CancellationToken.None), "zero configured retries stops after the initial failure");
    Check(policyHandler.Calls == 1, "zero retries makes exactly one provider request");
    policyHandler.Calls = 0; policyHandler.Failures = 2;
    Check(await policyContent.ClassifyAsync(en, config, policySettings with { ContentMaxRetries = 2 }, CancellationToken.None) == "language" && policyHandler.Calls == 3,
        "two configured retries recover on the third provider attempt");
    Check(policyHandler.Model == "edited-model-fixture" && policyHandler.Url == settings.ApiBaseUrl + "/chat/completions", "edited API model and URL reach the provider request");
    policyHandler.Calls = 0; policyHandler.Hang = true;
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    await Reject(() => policyContent.ClassifyAsync(en, config, policySettings, CancellationToken.None), "configured timeout cancels a pending provider request");
    Check(policyHandler.Calls == 1 && policyHandler.Cancelled && elapsed.Elapsed.TotalSeconds is >= 0.7 and < 10,
        "one-second timeout uses the configured policy and zero retries");
    policyHandler.Calls = 0;
    using var cancellation = new CancellationTokenSource(30);
    try { await policyContent.ClassifyAsync(en, config, policySettings with { ContentMaxRetries = 5 }, cancellation.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(policyHandler.Calls == 1, "caller cancellation never retries"); }
    policyHandler.Hang = false; policyHandler.Calls = 0; policyHandler.Unauthorized = true;
    try { await policyContent.ClassifyAsync(en, config, policySettings with { ContentMaxRetries = 5 }, CancellationToken.None); throw new Exception("Expected credential error"); }
    catch (UnauthorizedAccessException) { Check(policyHandler.Calls == 1, "authentication failure never retries"); }
}
WorkflowJson.Save(Path.Combine(artifacts, "mode-content-checks.json"), new { status = "PASS", scope = "local interfaces with fixed responses; production prompts and real content acceptance remain separate", checks });
Console.WriteLine($"{checks.Count} interface checks passed; no external calls");

sealed class Handler : HttpMessageHandler
{
    public List<(string System, JsonElement Input)> Requests { get; } = [];
    public string? CorruptMode;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var messages = json.RootElement.GetProperty("messages");
        var system = messages[0].GetProperty("content").GetString()!;
        using var inputDocument = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var input = inputDocument.RootElement.Clone();
        Requests.Add((system, input));
        object output;
        if (system == "fixture-classifier")
            output = new { modeId = input.GetProperty("intent").GetString() == "lookup" && input.GetProperty("selectedText").GetString() == "带领" ? "translation" : "language", question = (string?)null };
        else if (system.StartsWith("fixture:question\n")) output = new { answer_state = "answered", answer_text = "固定回答", unresolved_reasons = Array.Empty<string>() };
        else if (system.StartsWith("fixture:translation\n")) output = new { translation = "to lead" };
        else
        {
            var id = system.Split('\n')[0]["fixture:".Length..];
            output = new { pronunciation = "fixture pronunciation", meaning = (id == CorruptMode ? "wrong-mode" : id) + " meaning", generatedSentence = (string?)null, generatedTargetStart = (int?)null };
        }
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(output, WorkflowJson.Options) } } } }) };
    }
}

sealed class PolicyHandler : HttpMessageHandler
{
    public int Calls, Failures;
    public bool Hang, Cancelled, Unauthorized;
    public string? Model, Url;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Model = data.RootElement.GetProperty("model").GetString(); Url = request.RequestUri!.ToString();
        if (Hang)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        if (Unauthorized) return new(HttpStatusCode.Unauthorized);
        if (Calls <= Failures) return new(HttpStatusCode.ServiceUnavailable);
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = "{\"modeId\":\"language\",\"question\":null}" } } } }) };
    }
}

using System.Net;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var root = args[0];
var config = ModeConfigurationLoader.Load(Path.Combine(root, "modes"));
DeckRoutingChecks.Run(config);
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
async Task Fail(Func<Task> action, string name) { try { await action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Expected failure: " + name); }
CaptureMaterial Material(string text) => new(text, null, "generated_allowed", null, "local fixture", 0, DateTimeOffset.UtcNow);
var typed = new InputMetadata("typed", "lookup", true, true);
using var wire = new ContentWire();
using var http = new HttpClient(wire);
var module = new ContentModule(http, () => "fixture-key");
foreach (var text in new[] { "为何？", "Why?", "空白？  \r\n\t" })
    Check(await module.ClassifyAsync(Material(text), config, new(), default, typed) == "question", "terminal question mark routes: " + text.Trim());
Check(wire.Inputs.Count == 0, "explicit question needs no classification call");
Check(!ContentModule.IsQuestion("为何？补充") && !ContentModule.IsQuestion("？\n补充") && !ContentModule.IsQuestion(""), "internal question mark and empty text are not terminal questions");
Check(await module.ClassifyAsync(Material("Why?"), config, new(), default, typed with { Source = "any-injection" }) == "question", "routing uses metadata policy, independent of entry name");

wire.Add("{\"modeId\":\"translation\",\"question\":null}");
var raw = "  勇气与耐心\r\n让事情逐步变好。  ";
Check(await module.ClassifyAsync(Material(raw), config, new(), default, typed) == "translation", "typed lookup accepts translation");
var lookup = wire.Inputs.Last();
Check(lookup.GetProperty("intent").GetString() == "lookup" && lookup.GetProperty("selectedText").GetString() == raw, "lookup metadata and exact raw text reach common classifier");
Check(!lookup.GetProperty("modes").EnumerateArray().Any(m => m.GetProperty("id").GetString() == "question"), "no-question input cannot enter question candidate");
wire.Add("{\"translation\":\"Courage and patience gradually make things better.\"}");
var translated = await module.ProcessAsync(Material(raw), "translation", config, new(), default);
Check(translated.Fields == null && translated.Basic?.Front == raw && translated.Basic.Back.StartsWith("Courage"), "translation preserves arbitrary-length original and has only basic faces");

foreach (var source in new[] { "selection-button", "selection-dot" })
{
    wire.Add("{\"modeId\":\"language\",\"question\":null}");
    Check(await module.ClassifyAsync(Material("耐心"), config, new(), default, new(source)) == "language", source + " shares auto language route");
    Check(wire.Inputs.Last().GetProperty("intent").GetString() == "auto", source + " injects auto metadata");
}
wire.Add("{\"modeId\":\"question\",\"question\":null}", "{\"modeId\":\"question\",\"question\":null}");
await Fail(() => module.ClassifyAsync(Material("耐心"), config, new(), default, typed), "model cannot override lookup into question");

wire.Add("{\"answer_state\":\"answered\",\"answer_text\":\"Because the conditions differ.\",\"unresolved_reasons\":[]}");
var question = await module.ProcessAsync(Material("Why?") with { ContextOrigin = "unavailable" }, "question", config, new(), default);
Check(question.Answer?.Answered == true && question.Basic?.Front == "Why?", "question bypasses missing language context and yields preview");
var turns = new[] { new ConversationTurn("Why?", question.Answer!) };
wire.Add("{\"answer_state\":\"answered\",\"answer_text\":\"The second condition changes the outcome.\",\"unresolved_reasons\":[]}");
var followUp = await module.AnswerAsync(Material("Why?"), question, turns, "Explain the second condition", config, new(), default);
var sent = wire.Inputs.Last();
Check(followUp.Answered && sent.GetProperty("question").GetString() == "Explain the second condition", "follow-up accepts text without question mark");
Check(sent.GetProperty("conversation").GetArrayLength() == 1 && sent.GetProperty("conversation")[0].GetProperty("answer").GetProperty("answerText").GetString() == question.Answer!.AnswerText,
    "follow-up carries ordered previous question and actual answer");
Check(sent.GetProperty("originalMaterial").GetProperty("selectedText").GetString() == "Why?" && !sent.GetProperty("originalMaterial").TryGetProperty("sourceWindow", out _), "follow-up keeps material and excludes desktop metadata");
Check(turns.Length == 1 && turns[0].Answer.AnswerText == "Because the conditions differ.", "content calls do not rewrite history");
wire.Add("{\"answer_state\":\"answered\",\"answer_text\":\"This answers the edited preview.\",\"unresolved_reasons\":[]}");
var editedPreview = new ContentPreview("Explain the second condition", new ContentResult("question", Basic: new("Explain the second condition", "User edited this answer.")));
await module.AnswerAsync(Material("Why?"), question, turns, "Explain this edit", config, new(), default, currentPreview: editedPreview);
Check(wire.Inputs.Last().GetProperty("currentPreview").GetProperty("content").GetProperty("basic").GetProperty("back").GetString() == "User edited this answer." &&
    wire.Inputs.Last().GetProperty("conversation")[0].GetProperty("answer").GetProperty("answerText").GetString() == question.Answer!.AnswerText,
    "edited child preview accompanies the request without rewriting historical answers");
wire.Add("{\"answer_state\":\"unresolved\",\"answer_text\":null,\"unresolved_reasons\":[\"missing_facts\"]}");
var unresolved = await module.ProcessAsync(Material("Which one?"), "question", config, new(), default);
Check(unresolved.Basic == null && unresolved.Answer?.Answered == false, "unresolved answer cannot create an empty card");
wire.Add("{\"translation\":\" \"}", "{\"translation\":\" \"}");
await Fail(() => module.ProcessAsync(Material("词组"), "translation", config, new(), default), "blank translation rejected by production validator");

var temporary = Path.Combine(@"D:\Caches\KnowledgeClipper\routing-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
WorkflowJson.Save(Path.Combine(temporary, "modes.json"), new ModeFile([config.Modes.Single(m => m.Definition.Id == "language").Definition with { Description = "personal description" }]));
var merged = ModeConfigurationLoader.Load(Path.Combine(root, "modes"), temporary);
Check(merged.Modes.Length == 3 && merged.Modes.Single(m => m.Definition.Id == "language").Definition.Description == "personal description", "old single-mode overrides preserve edits while new modes remain available");
Check(!ModeConfigurationLoader.Route(config, "language").FollowUpDefault && ModeConfigurationLoader.Route(config, "question").FollowUpDefault, "mode metadata controls follow-up default");
var original = Material(raw);
var draft = new ClipDraft { Original = original, Material = original, Settings = new() { TypedInputAutoRead = false }, Configuration = config,
    Input = typed, ModeId = "translation", Content = translated, Route = ModeConfigurationLoader.Route(config, "translation"), Conversation = turns.ToList(), PendingFollowUp = "still typing" };
var store = new DraftStore(temporary); store.Save(draft);
var restored = store.Recent().Single();
Check(restored.Input == typed && JsonSerializer.Serialize(restored.Conversation.Single()) == JsonSerializer.Serialize(turns[0]) && restored.PendingFollowUp == "still typing" && !restored.Settings.TypedInputAutoRead,
    "metadata, follow-up input, conversation and read setting survive restart");
Check(restored.ConfirmedNote == null && restored.ImportState == ImportState.Unconfirmed, "previews and conversation never imply import confirmation");
restored.EditedBasic = new(restored.Original.SelectedText, "An edited translation.");
restored.FollowUpCards[0] = Guid.NewGuid().ToString("N"); store.Save(restored);
Check(store.Find(restored.Id)?.FollowUpCards[0] == restored.FollowUpCards[0] && store.Find("../outside") == null,
    "follow-up card links survive restart and draft lookup accepts only actual draft IDs");
Check(restored.PreviewContent?.Basic?.Back == "An edited translation." && restored.Content?.Basic?.Back != "An edited translation.",
    "current preview overlays edits without mutating original generated content");
var questionMaterial = Material(turns[0].Question);
var child = new ClipDraft { Id = restored.FollowUpCards[0], ParentDraftId = restored.Id, Original = questionMaterial, Material = questionMaterial,
    Settings = new(), Configuration = config, ModeId = "question", Route = ModeConfigurationLoader.Route(config, "question"), Conversation = turns.ToList() };
store.Save(child);
Check(store.RecentSessions().Select(d => d.Id).SequenceEqual(new[] { restored.Id }), "history folds a child already fully represented in its parent session");
child.Conversation.Add(new("An independent follow-up", new("answered", "An independent answer", []))); store.Save(child);
Check(store.RecentSessions().Any(d => d.Id == child.Id), "history preserves an older child with its own additional conversation branch");
child.Conversation = turns.ToList(); child.PendingFollowUp = "unsent child input"; store.Save(child);
Check(store.RecentSessions().Any(d => d.Id == child.Id), "history keeps an older child with unsent follow-up input accessible");
restored.FollowUpCards.Clear(); child.PendingFollowUp = null; store.Save(restored); store.Save(child);
Check(store.RecentSessions().Any(d => d.Id == child.Id), "an unlinked old child remains accessible without mutating legacy data");
Check(!child.SupportsSpeech && restored.SupportsSpeech && new LanguageSettings().QuestionCardIncludeContext,
    "question drafts hide playback while translation stays readable and preceding context defaults on");
Console.WriteLine($"TOTAL {checks} PASS");

sealed class ContentWire : HttpMessageHandler
{
    readonly Queue<string> replies = new();
    public List<JsonElement> Inputs { get; } = [];
    public void Add(params string[] json) { foreach (var text in json) replies.Enqueue(text); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var requestJson = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        using var input = JsonDocument.Parse(requestJson.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Inputs.Add(input.RootElement.Clone());
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = replies.Dequeue() } } } })) };
    }
}

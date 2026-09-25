using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

if (args.Length == 3 && args[0] == "--live") { await LiveModeImportCheck.RunAsync(args[1], args[2]); return; }

var checks = 0;
var store = new DraftStore(@"D:\Caches\KnowledgeClipper\language-mode-card-tests\" + Guid.NewGuid().ToString("N"));
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
ModeDefinition Mode(string language, PracticeChoice practice) => new(language + "-" + practice.ToString().ToLowerInvariant(),
    language + " " + practice, true, "fixture only", "fixture-prompt", "fixture-schema", AnkiDeckPath: language + "::" + practice,
    Language: language, Practice: practice);
var enReading = Mode("en", PracticeChoice.Reading); var enWriting = Mode("en", PracticeChoice.Writing);
var zhReading = Mode("zh", PracticeChoice.Reading); var zhWriting = Mode("zh", PracticeChoice.Writing);
var modes = new[] { enReading, enWriting, zhReading, zhWriting };
var configuration = new ContentConfiguration("fixture classifier", modes.Select(mode => new ModeSnapshot(mode, "fixture prompt: " + mode.Id, "{}")).ToArray());
ClipDraft Parent(bool chinese = false)
{
    var material = chinese ? new CaptureMaterial("带领", "他带领团队。", "source", 1, "local fixture", 0, DateTimeOffset.UtcNow) :
        new CaptureMaterial("lead", "They lead the team.", "source", 5, "local fixture", 0, DateTimeOffset.UtcNow);
    return new() { Original = material, Material = material, Settings = new() { AnkiRootDeck = "fixture root" }, Configuration = configuration,
        Input = new(ReadRequested: true), Practice = PracticeChoice.Both };
}
void Ready(ClipDraft child)
{
    var mode = child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition;
    child.Content = new(mode.Id, new(mode.Language == "zh" ? "dài lǐng" : "/liːd/", mode.Name + " result", null, null));
    store.Save(child);
}
ClipDraft ReadyParent()
{
    var result = Parent(); foreach (var child in LanguageModeCards.Ensure(result, [enReading, enWriting], store)) Ready(child); return result;
}

var parent = Parent();
var children = LanguageModeCards.Ensure(parent, [enReading, enWriting], store);
Check(children.Length == 2 && children[0].Id != children[1].Id && parent.ModeCards.Count == 2 && parent.SelectedModeIds.SequenceEqual(new[] { enReading.Id, enWriting.Id }), "one capture creates two separately identified selected mode drafts");
Check(children.All(child => child.Original == parent.Original && child.Material == parent.Material && child.ModeParentId == parent.Id && child.ParentDraftId == parent.Id && !child.Input.ReadRequested) && parent.Input.ReadRequested, "children retain capture identity and leave speech ownership on the parent");
foreach (var child in children) Ready(child);
children[0].EditedFields = children[0].Content!.Fields! with { Meaning = "edited reading result" }; store.Save(children[0]);
var reused = LanguageModeCards.Ensure(parent, [enWriting], store);
Check(reused.Single().Id == children[1].Id && parent.ModeCards.Count == 2 && LanguageModeCards.Children(parent, store).Select(child => child.ModeId).SequenceEqual(new[] { enWriting.Id }), "changing selection retains cached independent results but returns only selected modes");
LanguageModeCards.Ensure(parent, [enWriting, enReading], store);
Check(LanguageModeCards.Children(parent, store).Select(child => child.ModeId).SequenceEqual(new[] { enWriting.Id, enReading.Id }) && store.Find(children[0].Id)!.EditedFields!.Meaning == "edited reading result", "selection order and saved per-mode edits survive reopening");
parent.SelectedModeIds = []; store.Save(parent);
Check(LanguageModeCards.Children(parent, store).Select(child => child.ModeId).SequenceEqual(new[] { enReading.Id, enWriting.Id }), "old empty selection metadata falls back to binding order");
LanguageModeCards.Ensure(parent, [enReading, enWriting], store);
children[0].Practice = PracticeChoice.Both; children[1].Practice = PracticeChoice.Both;
var readingNote = AnkiNoteDraft.From(children[0]); var writingNote = AnkiNoteDraft.From(children[1]);
Check(readingNote.Ordinals.SequenceEqual(new[] { 0 }) && writingNote.Ordinals.SequenceEqual(new[] { 1 }) && readingNote.Fields["WritingEnabled"] == "" && writingNote.Fields["ReadingEnabled"] == "", "concrete mode metadata prevents a UI Both choice from adding a second card");
Check(readingNote.Fields["Meaning"] != writingNote.Fields["Meaning"] && readingNote.Deck == "fixture root::en::Reading" && writingNote.Deck == "fixture root::en::Writing", "independent mode results retain distinct fields and configured destinations");
var chinese = Parent(true); var chineseChildren = LanguageModeCards.Ensure(chinese, [zhReading, zhWriting], store);
foreach (var child in chineseChildren) Ready(child);
Check(AnkiNoteDraft.From(chineseChildren[0]).Ordinals.SequenceEqual(new[] { 0 }) && AnkiNoteDraft.From(chineseChildren[1]).Ordinals.SequenceEqual(new[] { 1 }) && AnkiNoteDraft.From(chineseChildren[1]).Deck == "fixture root::zh::Writing", "Chinese concrete modes also map to independent single-card destinations");

using var backend = new Handler(); using var http = new HttpClient(backend);
var anki = new AnkiConnection(http, "http://127.0.0.1:8765");
await LanguageModeCards.ImportAsync(parent, [enReading.Id], store, anki);
var selected = LanguageModeCards.Children(parent, store);
Check(backend.Adds == 1 && selected[0].ImportState == ImportState.Verified && selected[0].CardIds.Length == 1 && selected[1].ConfirmedNote == null, "single-mode confirmation imports only that result and leaves the other editable");
Check(selected[0].ConfirmedNote!.Fields["Meaning"] == "edited reading result" && selected[0].EditedFields == null, "single-mode confirmation freezes its saved preview edit");

var batch = ReadyParent(); var beforeAdds = backend.Adds;
backend.BeforeNextCall = () => Check(LanguageModeCards.Children(batch, store).All(child => child.ConfirmedNote != null && child.ImportState == ImportState.Ready), "every selected independent result is frozen before the first API request");
backend.LoseAtAdd = backend.Adds + 2;
await LanguageModeCards.ImportAsync(batch, [enReading.Id, enWriting.Id], store, anki);
var partial = LanguageModeCards.Children(batch, store);
Check(partial[0].ImportState == ImportState.Verified && partial[1].ImportState == ImportState.Unknown && partial[1].AwaitingAnki &&
    partial.All(child => child.ConfirmedNote!.Ordinals.Length == 1) && backend.Adds == beforeAdds + 2,
    "second-result lost response preserves first success and queues independent Unknown identity");
var frozenMeaning = partial[1].ConfirmedNote!.Fields["Meaning"];
partial[1].Content = partial[1].Content! with { Fields = partial[1].Content!.Fields! with { Meaning = "unconfirmed late content" } };
partial[1].EditedFields = partial[1].Content!.Fields! with { Meaning = "unconfirmed late edit" }; store.Save(partial[1]);
var beforeCalls = backend.Actions.Count;
await LanguageModeCards.ImportAsync(batch, [enReading.Id, enWriting.Id], store, anki);
var restored = LanguageModeCards.Children(batch, store);
Check(restored.All(child => child.ImportState == ImportState.Verified) && backend.Adds == beforeAdds + 2 && backend.Actions[beforeCalls] == "findNotes" && restored[1].ConfirmedNote!.Fields["Meaning"] == frozenMeaning, "retry reads the frozen failed result without re-adding or regenerating either mode");
beforeCalls = backend.Actions.Count;
await LanguageModeCards.ImportAsync(batch, [enReading.Id, enWriting.Id], store, anki);
Check(backend.Actions.Count == beforeCalls, "already verified mode batch sends no API request");
var stableIds = restored.Select(child => child.Id).ToArray(); batch.ModeCards.Clear(); store.Save(batch);
Check(LanguageModeCards.Ensure(batch, [enReading, enWriting], store).Select(child => child.Id).SequenceEqual(stableIds), "stable parent and mode identity reuses saved results after a stale binding save");

var notReady = Parent(); var waiting = LanguageModeCards.Ensure(notReady, [enReading, enWriting], store); Ready(waiting[0]); beforeCalls = backend.Actions.Count;
await Reject(() => LanguageModeCards.ImportAsync(notReady, [enReading.Id, enWriting.Id], store, anki), "missing independent content rejects the whole selection before confirmation");
Check(backend.Actions.Count == beforeCalls && LanguageModeCards.Children(notReady, store).All(child => child.ConfirmedNote == null), "a missing second result cannot partially freeze or import the first");
var invalid = Parent(); invalid.ModeCards[enReading.Id] = Guid.NewGuid().ToString("N"); store.Save(invalid);
await Reject(() => { LanguageModeCards.Ensure(invalid, [enReading], store); return Task.CompletedTask; }, "missing bound child is rejected rather than regenerated");
var wrongIdentity = ReadyParent(); var wrongChild = LanguageModeCards.Children(wrongIdentity, store)[0]; wrongChild.Route = wrongChild.Route! with { ModeId = enWriting.Id }; store.Save(wrongChild);
await Reject(() => { LanguageModeCards.Children(wrongIdentity, store); return Task.CompletedTask; }, "bound child mode identity is checked before use");

var concurrent = ReadyParent(); beforeAdds = backend.Adds;
await Task.WhenAll(LanguageModeCards.ImportAsync(concurrent, [enReading.Id, enWriting.Id], store, anki), LanguageModeCards.ImportAsync(concurrent, [enReading.Id, enWriting.Id], store, anki));
Check(backend.Adds == beforeAdds + 2, "same-parent concurrent confirmations serialize without duplicate notes");
var canceled = ReadyParent(); using var cancellation = new CancellationTokenSource(); beforeCalls = backend.Actions.Count;
try { await LanguageModeCards.ImportAsync(canceled, [enReading.Id, enWriting.Id], store, anki, () => cancellation.Cancel(), cancellation.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
Check(backend.Actions.Count == beforeCalls && LanguageModeCards.Children(canceled, store).All(child => child.ConfirmedNote != null && child.ImportState == ImportState.Ready) && canceled.ModeImportGate.CurrentCount == 1, "cancellation after confirmation preserves both frozen results and releases the gate");

var oldDefinition = new ModeDefinition("language", "old language", true, "", "", "");
var old = new ClipDraft { Original = parent.Original, Material = parent.Material, Settings = new() { AnkiDeck = "saved legacy deck" },
    Configuration = new("", [new(oldDefinition, "old fixture", "{}")]), Content = new("language", new("/liːd/", "old shared result", null, null)), Practice = PracticeChoice.Both };
old.ConfirmedNote = AnkiNoteDraft.From(old); old.ImportState = ImportState.Ready;
old.Practice = PracticeChoice.Writing; old.Content = old.Content with { Fields = old.Content.Fields! with { Meaning = "late replacement" } };
await anki.ImportAsync(old, store);
Check(old.CardIds.Length == 2 && old.ConfirmedNote.Ordinals.SequenceEqual(new[] { 0, 1 }) && old.ConfirmedNote.Deck == "saved legacy deck" && old.ConfirmedNote.Fields["Meaning"] == "old shared result", "old frozen shared-result notes retain both templates and their saved destination");

const string explicitQuestion = "这里的 lead 是什么意思？";
var question = new ClipDraft { Original = parent.Original, Material = parent.Material, Settings = parent.Settings, Configuration = new("", []),
    Input = new(Intent: "question", ExplicitQuestion: explicitQuestion), Route = new("question", "question", true, false),
    Content = new("question", Basic: new(explicitQuestion, "带领"), Answer: new("answered", "带领", [])) };
var questionNote = AnkiNoteDraft.From(question);
Check(questionNote.Fields["Front"] == AnkiNoteDraft.Html(explicitQuestion) && question.Original == parent.Original && question.Material.ContextText == "They lead the team." && question.Original.SelectedText == "lead", "explicit question becomes Basic front while original target and source context remain intact");
foreach (var alreadyImported in new[] { false, true })
{
    const string followUpQuestion = "这里的 lead 与 leader 有什么区别？";
    var rootQuestion = new ClipDraft { Original = question.Original, Material = question.Material, Settings = question.Settings,
        Configuration = question.Configuration, Input = question.Input, Route = question.Route, Content = question.Content,
        Conversation = [new(explicitQuestion, new("answered", "带领", [])), new(followUpQuestion, new("answered", "lead 是动词，leader 是名词。", []))] };
    rootQuestion.ConfirmedNote = AnkiNoteDraft.From(rootQuestion); rootQuestion.ImportState = ImportState.Ready;
    if (alreadyImported) await anki.ImportAsync(rootQuestion, store); else store.Save(rootQuestion);
    var stage = alreadyImported ? "already imported" : "frozen before import";
    var questionPreview = QuestionCards.Preview(rootQuestion, store);
    Check(questionPreview[0].Question == explicitQuestion && questionPreview[0].Frozen && questionPreview[0].Error == null &&
        questionPreview[0].State == (alreadyImported ? ImportState.Verified : ImportState.Ready), "explicit root first-round preview reuses its frozen identity: " + stage);
    var initialAdds = backend.Adds;
    await QuestionCards.ImportAsync(rootQuestion, [0], store, anki, () => { });
    Check(rootQuestion.FollowUpCards[0] == rootQuestion.Id && rootQuestion.ImportState == ImportState.Verified &&
        backend.Adds == initialAdds + (alreadyImported ? 0 : 1), "first-round confirmation never creates a replacement explicit-question note: " + stage);
    initialAdds = backend.Adds;
    await QuestionCards.ImportAsync(rootQuestion, [0, 1], store, anki, () => { });
    var followUp = store.Find(rootQuestion.FollowUpCards[1])!;
    Check(backend.Adds == initialAdds + 1 && followUp.Input.ExplicitQuestion == null && followUp.QuestionText == followUpQuestion &&
        followUp.ConfirmedNote!.Fields["Front"] == AnkiNoteDraft.Html(followUpQuestion) && followUp.ImportState == ImportState.Verified &&
        rootQuestion.Original.SelectedText == "lead" && rootQuestion.Material.ContextText == "They lead the team." &&
        followUp.ConversationMaterial == rootQuestion.Material, "second round uses its own question and retains the original selected material: " + stage);
    var repeatCalls = backend.Actions.Count;
    await QuestionCards.ImportAsync(rootQuestion, [0, 1], store, anki, () => { });
    Check(backend.Actions.Count == repeatCalls && QuestionCards.Preview(rootQuestion, store).All(row => row.State == ImportState.Verified),
        "reopening the imported explicit-question conversation causes no duplicate API call: " + stage);
}
var translation = new ClipDraft { Original = parent.Original, Material = parent.Material, Settings = parent.Settings, Configuration = new("", []),
    Input = question.Input, Content = new("translation", Basic: new(parent.Original.SelectedText, "带领")) };
Check(AnkiNoteDraft.From(translation).Fields["Front"] == "lead", "ordinary translation still retains the original input even with extra question metadata");
question.Content = question.Content! with { Basic = new("rewritten question", "带领") };
await Reject(() => { AnkiNoteDraft.From(question); return Task.CompletedTask; }, "rewriting the explicit question is rejected");

var basicParent = Parent(); var basicChild = LanguageModeCards.Ensure(basicParent, [enWriting], store).Single();
basicChild.Content = new(enWriting.Id, Basic: new(basicChild.Original.SelectedText, "existing Basic shape fixture")); store.Save(basicChild);
await LanguageModeCards.ImportAsync(basicParent, [enWriting.Id], store, anki);
var basicSaved = LanguageModeCards.Children(basicParent, store).Single();
Check(basicSaved.ConfirmedNote!.IsBasic && basicSaved.CardIds.Length == 1, "an existing supported Basic result shape remains one card without imposing a LanguageFields prompt contract");
Console.WriteLine($"{checks} independent mode checks passed; fixed fixtures only, no production prompts or external calls");

sealed class Handler : HttpMessageHandler
{
    public int Adds { get; private set; }
    public int LoseAtAdd;
    public Action? BeforeNextCall;
    public List<string> Actions { get; } = [];
    private readonly Dictionary<long, JsonElement> notes = [];
    private readonly Dictionary<string, JsonElement> models = [];
    private readonly HashSet<string> decks = [];
    private readonly Dictionary<long, (long Note, int Ordinal)> cards = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var before = BeforeNextCall; BeforeNextCall = null; before?.Invoke(); await Task.Yield();
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var action = json.RootElement.GetProperty("action").GetString()!; var p = json.RootElement.GetProperty("params"); Actions.Add(action);
        object? result;
        switch (action)
        {
            case "modelNames": result = models.Keys.ToArray(); break;
            case "createModel": models.Add(p.GetProperty("modelName").GetString()!, p.Clone()); result = new { }; break;
            case "modelFieldNames": result = models[p.GetProperty("modelName").GetString()!].GetProperty("inOrderFields"); break;
            case "modelTemplates": result = models[p.GetProperty("modelName").GetString()!].GetProperty("cardTemplates").EnumerateArray().ToDictionary(t => t.GetProperty("Name").GetString()!, t => t); break;
            case "deckNames": result = decks.ToArray(); break;
            case "createDeck": decks.Add(p.GetProperty("deck").GetString()!); result = decks.Count; break;
            case "findNotes": var tag = p.GetProperty("query").GetString()![4..]; result = notes.Where(n => n.Value.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == tag)).Select(n => n.Key).ToArray(); break;
            case "addNote":
                Adds++; var n = p.GetProperty("note").Clone(); notes[Adds] = n; var fields = n.GetProperty("fields");
                var ordinals = fields.TryGetProperty("Front", out _) ? new[] { 0 } : new[] { 0, 1 }.Where(ord => fields.GetProperty(ord == 0 ? "ReadingEnabled" : "WritingEnabled").GetString() == "1").ToArray();
                foreach (var ord in ordinals) cards[Adds * 10 + ord] = (Adds, ord);
                if (Adds == LoseAtAdd) throw new TaskCanceledException("fixture lost second-result write response");
                result = Adds; break;
            case "notesInfo":
                var nid = p.GetProperty("notes")[0].GetInt64(); var note = notes[nid];
                result = new[] { new { noteId = nid, modelName = note.GetProperty("modelName").GetString(), tags = note.GetProperty("tags"),
                    cards = cards.Where(card => card.Value.Note == nid).Select(card => card.Key).ToArray(),
                    fields = note.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => new { value = f.Value.GetString() }) } }; break;
            case "cardsInfo":
                result = p.GetProperty("cards").EnumerateArray().Select(c => { var cid = c.GetInt64(); var card = cards[cid]; var n = notes[card.Note]; var f = n.GetProperty("fields"); var basic = f.TryGetProperty("Front", out var front);
                    return new { cardId = cid, note = card.Note, ord = card.Ordinal, queue = 0, deckName = n.GetProperty("deckName").GetString(),
                        question = basic ? (f.TryGetProperty("Context", out var context) ? context.GetString() : "") + front.GetString() : card.Ordinal == 0 ? f.GetProperty("Target").GetString() : f.GetProperty("Pronunciation").GetString() + f.GetProperty("Meaning").GetString(),
                        answer = basic ? f.GetProperty("Back").GetString() : f.GetProperty("Target").GetString() + f.GetProperty("Pronunciation").GetString() + f.GetProperty("Meaning").GetString() }; }).ToArray(); break;
            case "areSuspended": result = p.GetProperty("cards").EnumerateArray().Select(_ => false).ToArray(); break;
            default: throw new InvalidOperationException("Unexpected action: " + action);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

const string ReadingDeck = "fixture root::language::reading";
const string WritingDeck = "fixture root::language::writing";
var checks = 0;
var store = new DraftStore(@"D:\Caches\KnowledgeClipper\anki-deck-tests\" + Guid.NewGuid().ToString("N"));
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
async Task Lost(Func<Task> action)
{
    try { await action(); } catch (TaskCanceledException) { return; }
    throw new Exception("Expected fixture lost response");
}
ClipDraft Confirm(PracticeChoice practice = PracticeChoice.Both, bool routed = true)
{
    var material = new CaptureMaterial("lead", "They lead the team.", "source", 5, "local fixture", 0, DateTimeOffset.UtcNow);
    var draft = new ClipDraft { Original = material, Material = material, Settings = new(), Configuration = new("", []),
        Content = new("language", new("/liːd/", "带领", null, null)), Practice = practice };
    var note = AnkiNoteDraft.From(draft);
    draft.ConfirmedNote = !routed ? note : note with
    {
        Deck = practice == PracticeChoice.Writing ? WritingDeck : ReadingDeck,
        CardDecks = note.Ordinals.ToDictionary(ordinal => ordinal, ordinal => ordinal == 0 ? ReadingDeck : WritingDeck)
    };
    draft.ImportState = ImportState.Ready;
    return draft;
}

using var backend = new DeckHandler(); using var http = new HttpClient(backend);
var anki = new AnkiConnection(http, "http://127.0.0.1:8765");
var oldJson = JsonSerializer.Serialize(new { Deck = "saved legacy deck", Model = "saved model", Fields = new Dictionary<string, string>(), Tags = Array.Empty<string>(), Ordinals = new[] { 0, 1 } });
var oldNote = JsonSerializer.Deserialize<AnkiNoteDraft>(oldJson)!;
Check(oldNote.CardDecks == null && oldNote.DeckFor(0) == "saved legacy deck" && oldNote.DeckFor(1) == "saved legacy deck", "saved notes without CardDecks deserialize with unchanged deck assignments");
var both = Confirm();
var routedDraft = new ClipDraft { Original = both.Original, Material = both.Material,
    Settings = new() { AnkiRootDeck = "fixture routed" }, Configuration = both.Configuration, Content = both.Content, Practice = PracticeChoice.Both };
var routedNote = AnkiNoteDraft.From(routedDraft);
Check(routedNote.Deck == "fixture routed::语言学习::英语::理解与朗读" && routedNote.DeckFor(1) == "fixture routed::语言学习::英语::书写练习", "From freezes resolved language destinations for both actual ordinals");
backend.BeforeAdd = () => Check(backend.Decks.Contains(ReadingDeck) && backend.Decks.Contains(WritingDeck), "all actual destination decks exist before addNote");
await anki.ImportAsync(both, store);
Check(both.ImportState == ImportState.Verified && backend.Adds == 1 && both.CardIds.Length == 2 && backend.Moves.Count == 1, "one note produces two reviewable cards with one required deck move");
Check(backend.Card(both.NoteId!.Value, 0).Deck == ReadingDeck && backend.Card(both.NoteId.Value, 1).Deck == WritingDeck && backend.Moves[0].Cards.SequenceEqual(new[] { backend.Card(both.NoteId.Value, 1).Id }), "actual ordinal determines destination even when cardsInfo arrives in reverse order");
Check(backend.Actions.LastIndexOf("cardsInfo") > backend.Actions.LastIndexOf("changeDeck"), "every completed move receives a fresh semantic card readback");
var beforeCalls = backend.Actions.Count;
await anki.ImportAsync(both, store);
Check(backend.Actions.Count == beforeCalls, "Verified retry performs no API calls");

var lostAdd = Confirm(); backend.DropAfterAddOnce = true;
await Lost(() => anki.ImportAsync(lostAdd, store));
var saved = store.Find(lostAdd.Id)!;
Check(saved.ImportState == ImportState.Unknown && saved.NoteId == null && saved.ConfirmedNote!.DeckFor(1) == WritingDeck, "lost add response persists the original per-card routing plan");
var beforeAdds = backend.Adds; var beforeMoves = backend.Moves.Count; beforeCalls = backend.Actions.Count;
await anki.ImportAsync(saved, store);
Check(saved.ImportState == ImportState.Verified && backend.Adds == beforeAdds && backend.Moves.Count == beforeMoves + 1 && backend.Actions[beforeCalls] == "findNotes", "Unknown after add reconciles identity then finishes the pending split without another note");

var lostMove = Confirm(); backend.DropAfterMoveOnce = true;
await Lost(() => anki.ImportAsync(lostMove, store));
Check(lostMove.ImportState == ImportState.Unknown && backend.Card(lostMove.NoteId!.Value, 1).Deck == WritingDeck, "lost deck-move response retains Unknown after the external move");
beforeAdds = backend.Adds; beforeMoves = backend.Moves.Count;
await anki.ImportAsync(store.Find(lostMove.Id)!, store);
Check(backend.Adds == beforeAdds && backend.Moves.Count == beforeMoves, "already completed split is recovered with read-only calls");

var pendingMove = Confirm(); backend.FailBeforeMoveOnce = true;
await Lost(() => anki.ImportAsync(pendingMove, store));
Check(pendingMove.ImportState == ImportState.Unknown && backend.Card(pendingMove.NoteId!.Value, 1).Deck == ReadingDeck, "failure before split keeps the pending card in the frozen initial deck");
beforeMoves = backend.Moves.Count;
await Reject(() => anki.VerifyAsync(pendingMove), "public VerifyAsync remains read-only and requires final per-card decks");
Check(backend.Moves.Count == beforeMoves, "verification never applies a pending deck assignment");
await anki.ImportAsync(pendingMove, store);
Check(pendingMove.ImportState == ImportState.Verified && backend.Moves.Count == beforeMoves + 1 && backend.Card(pendingMove.NoteId!.Value, 1).Deck == WritingDeck, "retry moves only the remaining initial-deck card");

var userMoved = Confirm(); backend.DropAfterAddOnce = true;
await Lost(() => anki.ImportAsync(userMoved, store));
backend.Card(backend.Adds, 0).Deck = "User chosen deck";
beforeMoves = backend.Moves.Count; beforeAdds = backend.Adds;
await Reject(() => anki.ImportAsync(userMoved, store), "a card outside its target and initial decks blocks reconciliation");
Check(backend.Adds == beforeAdds && backend.Moves.Count == beforeMoves && backend.Card(userMoved.NoteId!.Value, 0).Deck == "User chosen deck", "all cards are checked before moves and user placement is preserved");

var changedFields = Confirm(); backend.DropAfterAddOnce = true;
await Lost(() => anki.ImportAsync(changedFields, store));
backend.WrongFields = true; beforeMoves = backend.Moves.Count;
await Reject(() => anki.ImportAsync(changedFields, store), "field identity mismatch prevents any deck mutation");
backend.WrongFields = false;
Check(backend.Moves.Count == beforeMoves, "a mismatched frozen note is never sorted into destinations");

var wrongOrdinal = Confirm(); backend.WrongOrdinal = true; beforeMoves = backend.Moves.Count;
await Reject(() => anki.ImportAsync(wrongOrdinal, store), "unexpected actual template ordinal prevents deck mutation");
backend.WrongOrdinal = false;
Check(backend.Moves.Count == beforeMoves && wrongOrdinal.ImportState == ImportState.Unknown, "unexpected templates remain Unknown without movement");

var writing = Confirm(PracticeChoice.Writing); beforeMoves = backend.Moves.Count;
await anki.ImportAsync(writing, store);
Check(writing.CardIds.Length == 1 && backend.Card(writing.NoteId!.Value, 1).Deck == WritingDeck && backend.Moves.Count == beforeMoves, "writing-only ordinal one starts directly in its destination deck");
var legacy = Confirm(routed: false); beforeMoves = backend.Moves.Count;
await anki.ImportAsync(legacy, store);
Check(legacy.ConfirmedNote!.CardDecks == null && legacy.CardIds.All(id => backend.Cards[id].Deck == legacy.Settings.AnkiDeck) && backend.Moves.Count == beforeMoves, "legacy settings retain the exact single-deck language behavior");

var basic = Confirm(); basic.Content = new("translation", Basic: new(basic.Original.SelectedText, "带领"));
basic.ConfirmedNote = AnkiNoteDraft.From(basic) with { Deck = "fixture root::translation", CardDecks = new() { [0] = "fixture root::translation" } };
beforeMoves = backend.Moves.Count;
await anki.ImportAsync(basic, store);
Check(basic.ConfirmedNote.IsBasic && basic.CardIds.Length == 1 && backend.Cards[basic.CardIds[0]].Deck == "fixture root::translation" && backend.Moves.Count == beforeMoves, "four-field Basic card works with a single hierarchical destination");

var invalidPlan = Confirm(); invalidPlan.ConfirmedNote = invalidPlan.ConfirmedNote! with { CardDecks = new() { [0] = ReadingDeck } }; beforeCalls = backend.Actions.Count;
await Reject(() => anki.ImportAsync(invalidPlan, store), "incomplete frozen ordinal routing is rejected before any API request");
Check(backend.Actions.Count == beforeCalls, "invalid routing cannot fall back to a guessed deck");
Console.WriteLine($"{checks} Anki deck checks passed; fixed responses only, no external calls");

sealed class DeckHandler : HttpMessageHandler
{
    public sealed class CardState(long id, long note, int ordinal, string deck)
    { public long Id = id, Note = note; public int Ordinal = ordinal; public string Deck = deck; }
    public List<string> Actions { get; } = [];
    public HashSet<string> Decks { get; } = new(StringComparer.Ordinal);
    public Dictionary<long, CardState> Cards { get; } = [];
    public List<(long[] Cards, string Deck)> Moves { get; } = [];
    public int Adds { get; private set; }
    public bool DropAfterAddOnce, DropAfterMoveOnce, FailBeforeMoveOnce, WrongFields, WrongOrdinal;
    public Action? BeforeAdd;
    private readonly Dictionary<long, JsonElement> notes = [];
    private readonly Dictionary<string, JsonElement> models = [];
    public CardState Card(long note, int ordinal) => Cards.Values.Single(card => card.Note == note && card.Ordinal == ordinal);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content!.Headers.ContentLength is not > 0) throw new InvalidDataException("Missing buffered Content-Length");
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
        var action = json.RootElement.GetProperty("action").GetString()!; var p = json.RootElement.GetProperty("params"); Actions.Add(action);
        object? result;
        switch (action)
        {
            case "modelNames": result = models.Keys.ToArray(); break;
            case "createModel": models.Add(p.GetProperty("modelName").GetString()!, p.Clone()); result = new { }; break;
            case "modelFieldNames": result = models[p.GetProperty("modelName").GetString()!].GetProperty("inOrderFields"); break;
            case "modelTemplates": result = models[p.GetProperty("modelName").GetString()!].GetProperty("cardTemplates").EnumerateArray().ToDictionary(t => t.GetProperty("Name").GetString()!, t => t); break;
            case "deckNames": result = Decks.ToArray(); break;
            case "createDeck": Decks.Add(p.GetProperty("deck").GetString()!); result = Decks.Count; break;
            case "findNotes":
                var tag = p.GetProperty("query").GetString()![4..];
                result = notes.Where(n => n.Value.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == tag)).Select(n => n.Key).ToArray(); break;
            case "addNote":
                var beforeAdd = BeforeAdd; BeforeAdd = null; beforeAdd?.Invoke();
                Adds++; var note = p.GetProperty("note").Clone(); notes[Adds] = note; var fields = note.GetProperty("fields");
                var ordinals = fields.TryGetProperty("Front", out _) ? new[] { 0 } :
                    new[] { 0, 1 }.Where(ord => fields.GetProperty(ord == 0 ? "ReadingEnabled" : "WritingEnabled").GetString() == "1").ToArray();
                foreach (var ord in ordinals) Cards[Adds * 10 + ord] = new(Adds * 10 + ord, Adds, ord, note.GetProperty("deckName").GetString()!);
                if (DropAfterAddOnce) { DropAfterAddOnce = false; throw new TaskCanceledException("fixture lost add response"); }
                result = Adds; break;
            case "changeDeck":
                var movedIds = p.GetProperty("cards").EnumerateArray().Select(c => c.GetInt64()).ToArray(); var target = p.GetProperty("deck").GetString()!;
                Moves.Add((movedIds, target));
                if (FailBeforeMoveOnce) { FailBeforeMoveOnce = false; throw new TaskCanceledException("fixture move was not applied"); }
                foreach (var cid in movedIds) Cards[cid].Deck = target;
                if (DropAfterMoveOnce) { DropAfterMoveOnce = false; throw new TaskCanceledException("fixture lost move response"); }
                result = null; break;
            case "notesInfo":
                var nid = p.GetProperty("notes")[0].GetInt64(); var n = notes[nid];
                result = new[] { new { noteId = nid, modelName = n.GetProperty("modelName").GetString(), tags = n.GetProperty("tags"),
                    cards = Cards.Values.Where(c => c.Note == nid).OrderByDescending(c => c.Ordinal).Select(c => c.Id).ToArray(),
                    fields = n.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => new { value = WrongFields && f.Name == "Meaning" ? "external edit" : f.Value.GetString() }) } }; break;
            case "cardsInfo":
                result = p.GetProperty("cards").EnumerateArray().Select(c => Cards[c.GetInt64()]).OrderByDescending(c => c.Ordinal).Select(c =>
                {
                    var f = notes[c.Note].GetProperty("fields"); var basic = f.TryGetProperty("Front", out var front);
                    return new { cardId = c.Id, note = c.Note, ord = WrongOrdinal ? 9 : c.Ordinal, queue = 0, deckName = c.Deck,
                        question = basic ? front.GetString() : c.Ordinal == 0 ? f.GetProperty("Target").GetString() : f.GetProperty("Pronunciation").GetString() + " " + f.GetProperty("Meaning").GetString(),
                        answer = basic ? f.GetProperty("Back").GetString() : f.GetProperty("Target").GetString() + " " + f.GetProperty("Pronunciation").GetString() + " " + f.GetProperty("Meaning").GetString() };
                }).ToArray(); break;
            case "areSuspended": result = p.GetProperty("cards").EnumerateArray().Select(_ => false).ToArray(); break;
            default: throw new InvalidOperationException("Unexpected action: " + action);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
}

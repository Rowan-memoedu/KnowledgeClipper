using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var checks = 0;
var dataRoot = @"D:\Caches\KnowledgeClipper\question-card-tests\" + Guid.NewGuid().ToString("N");
var store = new DraftStore(dataRoot);
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name) { try { await action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
ClipDraft Parent(bool context = true)
{
    var conversation = Enumerable.Range(1, 3).Select(i => new ConversationTurn($"Question {i}?", new("answered", $"Answer {i}.", []))).ToList();
    var material = new CaptureMaterial(conversation[0].Question, null, "generated_allowed", null, "local fixture", 0, DateTimeOffset.UtcNow);
    return new() { Original = material, Material = material, Settings = new() { QuestionCardIncludeContext = context },
        Configuration = new("", []), ModeId = "question", Route = new("question", "question", true, false),
        Conversation = conversation, Content = new("question", Basic: new(material.SelectedText, conversation[0].Answer.AnswerText!), Answer: conversation[0].Answer) };
}
ClipDraft LegacyChild(ClipDraft parent, int index)
{
    var turn = parent.Conversation[index];
    var material = parent.Original with { SelectedText = turn.Question };
    return new() { Original = material, Material = material, Settings = parent.Settings, Configuration = parent.Configuration,
        ParentDraftId = parent.Id, ModeId = "question", Conversation = parent.Conversation.Take(index + 1).ToList(),
        Content = new("question", Basic: new(turn.Question, turn.Answer.AnswerText!), Answer: turn.Answer) };
}

using var backend = new Handler(); using var http = new HttpClient(backend);
var anki = new AnkiConnection(http, "http://127.0.0.1:8765");
var parent = Parent(); parent.EditedAnswers[0] = "Edited answer 1.";
var preview = QuestionCards.Preview(parent, store);
Check(preview.Count == 3 && preview[0].Context == "" && preview[1].Context.Contains("Edited answer 1."), "each preview contains only earlier rounds as separate context");
Check(QuestionCards.EffectiveConversation(parent, store)[0].Answer.AnswerText == "Edited answer 1." && parent.Conversation[0].Answer.AnswerText == "Answer 1.", "effective conversation overlays edits without rewriting history");
backend.BeforeFirstCall = () => Check(parent.FollowUpCards.Count == 3 && parent.FollowUpCards.Values.All(id => store.Find(id)!.ConfirmedNote != null), "all selected cards are saved frozen before any API call");
backend.DropAtAdd = 2;
try { await QuestionCards.ImportAsync(parent, [0, 1, 2], store, anki, () => { }); throw new Exception("Expected lost response"); } catch (TaskCanceledException) { }
preview = QuestionCards.Preview(parent, store);
Check(preview.Select(x => x.State).SequenceEqual(new[] { ImportState.Verified, ImportState.Unknown, ImportState.Ready }) && preview.All(x => x.Frozen), "partial write failure keeps completed Unknown and remaining frozen cards");
Check(parent.ConversationImportError!.Contains("第 2 轮") && backend.Adds == 2, "batch stops at failed round and identifies it");
var frozenContext = preview[2].Context;
parent.EditedAnswers[0] = "Late edit that must not replace a confirmed answer.";
parent.EditedAnswers[2] = "Late third answer.";
Check(QuestionCards.Preview(parent, store)[2].Context == frozenContext && QuestionCards.EffectiveConversation(parent, store)[0].Answer.AnswerText == "Edited answer 1.", "frozen card answers and context ignore later parent edits");
await QuestionCards.ImportAsync(parent, [0, 1, 2], store, anki, () => { });
Check(backend.Adds == 3 && QuestionCards.Preview(parent, store).All(p => p.State == ImportState.Verified), "retry reconciles Unknown and adds only the remaining frozen card");
var callCount = backend.Actions.Count;
await QuestionCards.ImportAsync(parent, [0, 1, 2], store, anki, () => { });
Check(backend.Actions.Count == callCount, "already verified batch invokes no Anki API");
var first = store.Find(parent.FollowUpCards[0])!;
var third = store.Find(parent.FollowUpCards[2])!;
Check(first.ConfirmedNote!.HasQuestionContext && first.ConfirmedNote.Model == AnkiNoteDraft.QuestionModelName && first.ConfirmedNote.Fields["Context"] == "", "first question still uses new context-capable model with empty context");
Check(third.ConfirmedNote!.Fields["Front"] == "Question 3?" && !third.ConfirmedNote.Fields["Front"].Contains("Answer 1") && third.ConfirmedNote.Fields["Context"].Contains("Edited answer 1."), "previous answers never enter this round Front field");
var template = backend.Models[AnkiNoteDraft.QuestionModelName].GetProperty("cardTemplates")[0];
Check(template.GetProperty("Front").GetString()!.IndexOf("{{Context}}", StringComparison.Ordinal) < template.GetProperty("Front").GetString()!.IndexOf("{{Front}}", StringComparison.Ordinal)
    && template.GetProperty("Back").GetString() == "<div>{{Back}}</div>", "native template displays auxiliary context before question and only Back on reverse");

var selected = Parent(false); var addsBefore = backend.Adds;
await QuestionCards.ImportAsync(selected, [2, 2], store, anki, () => { });
Check(backend.Adds == addsBefore + 1 && selected.FollowUpCards.Count == 1 && QuestionCards.Preview(selected, store)[2].Context == "", "single-round import deduplicates index and honors disabled context");

var invalid = Parent(); invalid.Conversation[1] = invalid.Conversation[1] with { Answer = new("unresolved", null, ["missing_facts"]) };
callCount = backend.Actions.Count;
await Reject(() => QuestionCards.ImportAsync(invalid, [0, 1], store, anki, () => { }), "unresolved selection rejects entire batch before freezing");
await Reject(() => QuestionCards.ImportAsync(invalid, [0, 99], store, anki, () => { }), "invalid index rejects entire batch");
Check(invalid.FollowUpCards.Count == 0 && backend.Actions.Count == callCount && !QuestionCards.Preview(invalid, store)[1].Frozen, "rejected batch creates no cards and preserves unresolved row");

var legacyParent = Parent(); legacyParent.ConfirmedNote = AnkiNoteDraft.From(legacyParent); legacyParent.ImportState = ImportState.Ready;
await anki.ImportAsync(legacyParent, store);
addsBefore = backend.Adds;
await QuestionCards.ImportAsync(legacyParent, [0, 1], store, anki, () => { });
Check(legacyParent.FollowUpCards[0] == legacyParent.Id && backend.Adds == addsBefore + 1 && !legacyParent.ConfirmedNote.HasQuestionContext, "old confirmed root is reused without migrating its Basic model");

var oldParent = Parent(); var oldChild = LegacyChild(oldParent, 1);
oldChild.Content = oldChild.Content! with { Basic = oldChild.Content.Basic! with { Back = "Saved child edit." } };
oldChild.ConfirmedNote = AnkiNoteDraft.From(oldChild); oldChild.ImportState = ImportState.Ready;
await anki.ImportAsync(oldChild, store);
oldParent.FollowUpCards[1] = oldChild.Id; oldParent.EditedAnswers[1] = "An impermissible later edit.";
Check(QuestionCards.Preview(oldParent, store)[1].Answer == "Saved child edit." && QuestionCards.EffectiveConversation(oldParent, store)[1].Answer.AnswerText == "Saved child edit.", "old frozen child real Back takes priority over history and new edits");
addsBefore = backend.Adds;
await QuestionCards.ImportAsync(oldParent, [1, 2], store, anki, () => { });
Check(backend.Adds == addsBefore + 1 && store.Find(oldChild.Id)!.ConfirmedNote!.Model == AnkiNoteDraft.BasicModelName && QuestionCards.Preview(oldParent, store)[2].Context.Contains("Saved child edit."), "old child keeps model and supplies accurate context to later card");
callCount = backend.Actions.Count;
await QuestionCards.ImportAsync(oldChild, [1], store, anki, () => { });
Check(oldChild.FollowUpCards[1] == oldChild.Id && backend.Actions.Count == callCount, "old child preview reuses its uniquely matching own round");

var unfinishedParent = Parent(); var unfinished = LegacyChild(unfinishedParent, 1);
unfinished.EditedBasic = new(unfinished.Original.SelectedText, "Child's saved unfinished edit.");
store.Save(unfinished); unfinishedParent.FollowUpCards[1] = unfinished.Id;
Check(QuestionCards.EffectiveConversation(unfinishedParent, store)[1].Answer.AnswerText == "Child's saved unfinished edit.", "unconfirmed old child edit appears in current conversation");
unfinishedParent.EditedAnswers[1] = "Latest list edit.";
await QuestionCards.ImportAsync(unfinishedParent, [1], store, anki, () => { });
var upgraded = store.Find(unfinished.Id)!;
Check(upgraded.ConfirmedNote!.HasQuestionContext && upgraded.ConfirmedNote.Fields["Back"] == "Latest list edit." && upgraded.ConfirmedNote.Fields["Context"].Length > 0 && upgraded.Id == unfinished.Id, "unconfirmed old child preserves ID and adopts current context on first confirmation");

var canceled = Parent(); using var cancellation = new CancellationTokenSource(); callCount = backend.Actions.Count;
try { await QuestionCards.ImportAsync(canceled, [0, 1], store, anki, () => cancellation.Cancel(), cancellation.Token); throw new Exception("Expected cancel"); } catch (OperationCanceledException) { }
Check(backend.Actions.Count == callCount && QuestionCards.Preview(canceled, store).Take(2).All(row => row.Frozen && row.State == ImportState.Ready) && canceled.ConversationImportGate.CurrentCount == 1, "cancellation after confirmation saves frozen cards and releases the gate without a write");

var concurrent = Parent(); addsBefore = backend.Adds;
await Task.WhenAll(QuestionCards.ImportAsync(concurrent, [0, 1], store, anki, () => { }), QuestionCards.ImportAsync(concurrent, [0, 1], store, anki, () => { }));
Check(backend.Adds == addsBefore + 2, "same-parent concurrent confirmations serialize without duplicate writes");

var ancestorFirst = Parent(); ancestorFirst.ConfirmedNote = AnkiNoteDraft.From(ancestorFirst); ancestorFirst.ImportState = ImportState.Ready;
await anki.ImportAsync(ancestorFirst, store);
await QuestionCards.ImportAsync(ancestorFirst, [1], store, anki, () => { });
var laterBranch = LegacyChild(ancestorFirst, 1);
laterBranch.Conversation.Add(new("Branch question 3?", new("answered", "Branch answer 3.", [])));
store.Save(laterBranch); addsBefore = backend.Adds;
await QuestionCards.ImportAsync(laterBranch, [0, 1, 2], store, anki, () => { });
Check(backend.Adds == addsBefore + 1 && laterBranch.FollowUpCards[0] == ancestorFirst.Id && laterBranch.FollowUpCards[1] == ancestorFirst.FollowUpCards[1], "ancestor-first branch reuses frozen ancestor self and shared-prefix cards");
Check(!store.Find(ancestorFirst.Id)!.FollowUpCards.ContainsKey(2) && ancestorFirst.Conversation[2].Question == "Question 3?" && laterBranch.Conversation[2].Question == "Branch question 3?", "diverged rounds keep independent history and card bindings");
await QuestionCards.ImportAsync(ancestorFirst, [2], store, anki, () => { });
Check(backend.Adds == addsBefore + 2 && ancestorFirst.FollowUpCards[2] != laterBranch.FollowUpCards[2], "ancestor divergent round imports separately without replacing branch card");

var branchFirst = Parent(); store.Save(branchFirst);
var earlyBranch = LegacyChild(branchFirst, 1); store.Save(earlyBranch); addsBefore = backend.Adds;
await QuestionCards.ImportAsync(earlyBranch, [0, 1], store, anki, () => { });
var savedAncestor = store.Find(branchFirst.Id)!;
Check(backend.Adds == addsBefore + 2 && savedAncestor.FollowUpCards[0] == earlyBranch.FollowUpCards[0] && savedAncestor.FollowUpCards[1] == earlyBranch.FollowUpCards[1], "branch-first import saves shared-prefix bindings on the ancestor");
callCount = backend.Actions.Count;
await QuestionCards.ImportAsync(branchFirst, [0], store, anki, () => { });
Check(backend.Actions.Count == callCount && store.Find(branchFirst.Id)!.FollowUpCards[1] == earlyBranch.FollowUpCards[1], "stale ancestor confirmation reuses the card and preserves unselected persisted bindings");
branchFirst.FollowUpCards.Clear(); store.Save(branchFirst); // Simulate an older still-open window saving its stale index.
await QuestionCards.ImportAsync(branchFirst, [0, 1], store, anki, () => { });
Check(backend.Actions.Count == callCount && branchFirst.FollowUpCards[0] == earlyBranch.FollowUpCards[0] && branchFirst.FollowUpCards[1] == earlyBranch.FollowUpCards[1], "stable shared identity recovers a lost stale index without new API calls");

var orphan = LegacyChild(Parent(), 1); store.Save(orphan); callCount = backend.Actions.Count;
Check(QuestionCards.Preview(orphan, store).All(row => row.State == ImportState.Unknown && row.Frozen && row.Error != null), "missing ancestor appears as a read-only preview error");
await Reject(() => QuestionCards.ImportAsync(orphan, [0], store, anki, () => { }), "missing ancestor blocks replacement cards");
var loopId = Guid.NewGuid().ToString("N"); var cyclic = new ClipDraft { Id = loopId, ParentDraftId = loopId,
    Original = orphan.Original, Material = orphan.Material, Settings = orphan.Settings, Configuration = orphan.Configuration,
    Conversation = orphan.Conversation };
store.Save(cyclic);
await Reject(() => QuestionCards.ImportAsync(cyclic, [0], store, anki, () => { }), "cyclic ancestor blocks replacement cards");
Check(backend.Actions.Count == callCount && orphan.FollowUpCards.Count == 0 && cyclic.FollowUpCards.Count == 0, "invalid ancestry creates no card and sends no API request");
Console.WriteLine($"{checks} question-card checks passed; fixed responses only, no external calls");

sealed class Handler : HttpMessageHandler
{
    public Dictionary<string, JsonElement> Models { get; } = new();
    public List<string> Actions { get; } = [];
    public int Adds { get; private set; }
    public int DropAtAdd;
    public Action? BeforeFirstCall;
    private readonly Dictionary<long, JsonElement> notes = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content!.Headers.ContentLength is not > 0) throw new InvalidDataException("Missing buffered Content-Length");
        var first = BeforeFirstCall; BeforeFirstCall = null; first?.Invoke();
        await Task.Yield(); ct.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
        var action = json.RootElement.GetProperty("action").GetString()!; var p = json.RootElement.GetProperty("params"); Actions.Add(action);
        object? result;
        switch (action)
        {
            case "findNotes": var tag = p.GetProperty("query").GetString()![4..]; result = notes.Where(n => n.Value.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == tag)).Select(n => n.Key).ToArray(); break;
            case "modelNames": result = Models.Keys.ToArray(); break;
            case "createModel": Models.Add(p.GetProperty("modelName").GetString()!, p.Clone()); result = new { }; break;
            case "modelFieldNames": result = Models[p.GetProperty("modelName").GetString()!].GetProperty("inOrderFields"); break;
            case "modelTemplates": result = Models[p.GetProperty("modelName").GetString()!].GetProperty("cardTemplates").EnumerateArray().ToDictionary(t => t.GetProperty("Name").GetString()!, t => t); break;
            case "deckNames": result = new[] { "零碎知识剪藏::语言学习" }; break;
            case "addNote":
                Adds++; notes[Adds] = p.GetProperty("note").Clone();
                if (Adds == DropAtAdd) throw new TaskCanceledException("fixture lost response after write");
                result = Adds; break;
            case "notesInfo":
                var nid = p.GetProperty("notes")[0].GetInt64(); var note = notes[nid];
                result = new[] { new { noteId = nid, modelName = note.GetProperty("modelName").GetString(), tags = note.GetProperty("tags"), cards = new[] { nid * 10 },
                    fields = note.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => new { value = f.Value.GetString() }) } }; break;
            case "cardsInfo":
                result = p.GetProperty("cards").EnumerateArray().Select(c => { var cid = c.GetInt64(); var n = notes[cid / 10]; var f = n.GetProperty("fields");
                    return new { cardId = cid, note = cid / 10, ord = 0, queue = 0, deckName = n.GetProperty("deckName").GetString(),
                        question = (f.TryGetProperty("Context", out var context) ? context.GetString() : "") + f.GetProperty("Front").GetString(), answer = f.GetProperty("Back").GetString() }; }).ToArray(); break;
            case "areSuspended": result = p.GetProperty("cards").EnumerateArray().Select(_ => false).ToArray(); break;
            default: throw new InvalidOperationException("Unexpected action including model overwrite: " + action);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
}

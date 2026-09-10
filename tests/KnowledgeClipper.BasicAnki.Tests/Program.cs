using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var checks = 0;
var store = new DraftStore(@"D:\Caches\KnowledgeClipper\basic-anki-tests\" + Guid.NewGuid().ToString("N"));
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
ClipDraft Draft(string mode = "translation")
{
    var material = new CaptureMaterial("Why is 3 < 5?\n为什么？", null, "generated_allowed", null, "fixture <source>", 0, DateTimeOffset.UtcNow);
    return new() { Original = material, Material = material, Settings = new(), Configuration = new("", []),
        Content = new(mode, Basic: new(material.SelectedText, "因为 3 小于 5。\n<script> is literal & safe.")), Practice = PracticeChoice.Both };
}
ClipDraft Confirm(string mode = "translation")
{
    var draft = Draft(mode); draft.ConfirmedNote = AnkiNoteDraft.From(draft); draft.ImportState = ImportState.Ready; return draft;
}

var draft = Draft("a-custom-mode");
var mapped = AnkiNoteDraft.From(draft);
Check(mapped.IsBasic && mapped.Model == "零碎知识剪藏 · 问答与翻译" && mapped.Ordinals.SequenceEqual(new[] { 0 }), "Basic shape selects a dedicated one-card model independently of mode and practice");
Check(mapped.Fields["Front"] == "Why is 3 &lt; 5?<br>为什么？" && mapped.Fields["Source"] == "fixture &lt;source&gt;", "immutable original and source are escaped without rewriting");
Check(mapped.Fields["Back"] == "因为 3 小于 5。<br>&lt;script&gt; is literal &amp; safe.", "confirmed answer stays plain text with preserved line breaks");
draft.Content = draft.Content! with { Basic = draft.Content!.Basic! with { Front = "a rewritten question" } };
await Reject(() => { AnkiNoteDraft.From(draft); return Task.CompletedTask; }, "rewritten Basic front is rejected before import");
draft = Draft(); draft.Content = draft.Content! with { Basic = draft.Content!.Basic! with { Back = " \n" } };
await Reject(() => { AnkiNoteDraft.From(draft); return Task.CompletedTask; }, "blank Basic answer is rejected before import");

using var backend = new BasicHandler(); using var http = new HttpClient(backend);
var anki = new AnkiConnection(http, "http://127.0.0.1:8765");
var first = Confirm();
await anki.ImportAsync(first, store);
Check(first.ImportState == ImportState.Verified && first.CardIds.Length == 1 && backend.Adds == 1, "Basic import reads back one actual-shaped note and card");
Check(backend.CreatedModel is { } model && model.GetProperty("inOrderFields").EnumerateArray().Select(f => f.GetString()).SequenceEqual(new[] { "ClipId", "Front", "Back", "Source" })
    && model.GetProperty("cardTemplates").GetArrayLength() == 1
    && model.GetProperty("cardTemplates")[0].GetProperty("Name").GetString() == "正反面"
    && model.GetProperty("cardTemplates")[0].GetProperty("Front").GetString()!.Contains("{{Front}}", StringComparison.Ordinal)
    && model.GetProperty("cardTemplates")[0].GetProperty("Back").GetString()!.Contains("{{Back}}", StringComparison.Ordinal), "new Basic model uses one native front/back template and exact API key casing");
var previousCalls = backend.Actions.Count;
await anki.ImportAsync(first, store);
Check(backend.Actions.Count == previousCalls, "repeated Basic confirmation makes no provider call");

var second = Confirm("question"); backend.DropAfterWrite = true;
try { await anki.ImportAsync(second, store); throw new Exception("Expected lost response"); } catch (TaskCanceledException) { }
Check(second.ImportState == ImportState.Unknown && second.NoteId == null, "Basic lost write response remains Unknown");
var restored = store.Recent().Single(d => d.Id == second.Id);
Check(restored.ConfirmedNote!.IsBasic && restored.Content!.Basic != null, "persisted Basic draft preserves frozen type and fields");
// Late content changes must not replace the exact Back that was already confirmed.
restored.Content = restored.Content! with { Basic = restored.Content!.Basic! with { Back = "late, unconfirmed replacement" } };
var beforeRecovery = backend.Actions.Count;
await anki.ImportAsync(restored, store);
Check(restored.ImportState == ImportState.Verified && backend.Adds == 2 && backend.Actions[beforeRecovery] == "findNotes", "Basic Unknown uses the original frozen note and query-only reconciliation");

backend.MissingRenderedBack = true;
await Reject(() => anki.VerifyAsync(first), "readback rejects an empty rendered Basic answer even when fields match");
backend.MissingRenderedBack = false;
backend.IncompatibleExistingTemplate = true;
var incompatible = Confirm();
await Reject(() => anki.ImportAsync(incompatible, store), "existing incompatible Basic template is rejected");
Check(backend.Adds == 2 && backend.Actions.Count(a => a == "createModel") == 1, "existing models and notes are never overwritten to force compatibility");

var languageMaterial = new CaptureMaterial("lead", "They lead the team.", "source", 5, "fixture", 0, DateTimeOffset.UtcNow);
var language = new ClipDraft { Original = languageMaterial, Material = languageMaterial, Settings = new(), Configuration = new("", []),
    Content = new("language", new("/liːd/", "带领", null, null)), Practice = PracticeChoice.Both };
var languageNote = AnkiNoteDraft.From(language);
Check(!languageNote.IsBasic && languageNote.Model == language.Settings.AnkiModel && languageNote.Ordinals.SequenceEqual(new[] { 0, 1 }) && languageNote.Fields["Target"] == "lead", "existing language mapping still has its two native templates");
Console.WriteLine($"{checks} Basic Anki checks passed; fixed responses only, no external calls");

sealed class BasicHandler : HttpMessageHandler
{
    public List<string> Actions { get; } = [];
    public int Adds { get; private set; }
    public JsonElement? CreatedModel { get; private set; }
    public bool DropAfterWrite, MissingRenderedBack, IncompatibleExistingTemplate;
    private readonly Dictionary<long, JsonElement> notes = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content!.Headers.ContentLength is not > 0) throw new InvalidDataException("Anki request requires Content-Length before a handler reads it");
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
        var action = json.RootElement.GetProperty("action").GetString()!;
        var p = json.RootElement.GetProperty("params");
        Actions.Add(action);
        object? result;
        switch (action)
        {
            case "modelNames": result = CreatedModel == null ? Array.Empty<string>() : new[] { AnkiNoteDraft.BasicModelName }; break;
            case "createModel": CreatedModel = p.Clone(); result = new { }; break;
            case "modelFieldNames": result = new[] { "ClipId", "Front", "Back", "Source" }; break;
            case "modelTemplates": result = new Dictionary<string, object> { [IncompatibleExistingTemplate ? "user-customized" : "正反面"] = new { } }; break;
            case "deckNames": result = new[] { "零碎知识剪藏::语言学习" }; break;
            case "findNotes":
                var tag = p.GetProperty("query").GetString()![4..];
                result = notes.Where(n => n.Value.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == tag)).Select(n => n.Key).ToArray(); break;
            case "addNote":
                Adds++; notes[Adds] = p.GetProperty("note").Clone();
                if (DropAfterWrite) { DropAfterWrite = false; throw new TaskCanceledException("fixture lost response"); }
                result = Adds; break;
            case "notesInfo":
                var id = p.GetProperty("notes")[0].GetInt64(); var note = notes[id];
                result = new[] { new { noteId = id, modelName = note.GetProperty("modelName").GetString(),
                    fields = note.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => new { value = f.Value.GetString() }),
                    tags = note.GetProperty("tags"), cards = new[] { id * 10 } } }; break;
            case "cardsInfo":
                result = p.GetProperty("cards").EnumerateArray().Select(c => { var cid = c.GetInt64(); var n = notes[cid / 10]; var f = n.GetProperty("fields");
                    return new { cardId = cid, note = cid / 10, ord = 0, queue = 0, deckName = n.GetProperty("deckName").GetString(),
                        question = f.GetProperty("Front").GetString(), answer = MissingRenderedBack ? "" : f.GetProperty("Back").GetString() }; }).ToArray(); break;
            case "areSuspended": result = p.GetProperty("cards").EnumerateArray().Select(_ => false).ToArray(); break;
            default: throw new InvalidOperationException("Unexpected action, including any model rewrite: " + action);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
}

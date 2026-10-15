using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var root = args[0];
// This suite protects saved legacy drafts, whose prompt/config snapshot predates concrete modes.
var config = new ContentConfiguration("legacy fixture classifier", [new(
    new ModeDefinition("language", "legacy language", true, "", "language.md", "language.schema.json"),
    File.ReadAllText(Path.Combine(root, "modes", "language.md")), File.ReadAllText(Path.Combine(root, "modes", "language.schema.json")))]);
var store = new DraftStore(@"D:\Caches\KnowledgeClipper\workflow-tests\" + Guid.NewGuid().ToString("N"));
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task MustFail(Func<Task> action, string name) { try { await action(); } catch { Check(true, name); return; } throw new Exception("Expected failure: " + name); }
ClipDraft Draft()
{
    var material = new CaptureMaterial("lead", "They lead the team; lead can also be a metal.", "source", 5, "public fixture", 1, DateTimeOffset.UtcNow);
    return new ClipDraft { Material = material, Original = material, Settings = new(), Configuration = config,
        Content = new("language", new("/liːd/", "带领", null, null)), Practice = PracticeChoice.Both };
}
var draft = Draft();
draft.Material.Validate();
var mapped = AnkiNoteDraft.From(draft);
Check(mapped.Fields["SentenceBefore"] == "They " && mapped.Fields["SentenceAfter"] == " the team; lead can also be a metal.", "selected occurrence and source preserved");
Check(mapped.Ordinals.SequenceEqual(new[] { 0, 1 }), "both means two native templates");
draft.Practice = PracticeChoice.Writing;
Check(AnkiNoteDraft.From(draft).Ordinals.SequenceEqual(new[] { 1 }), "single writing choice preserved");
Check(AnkiNoteDraft.Html("<script>\r\n&").Equals("&lt;script&gt;<br>&amp;"), "plain content escaped");
await MustFail(() => { (draft.Material with { TargetStart = 4 }).Validate(); return Task.CompletedTask; }, "wrong source location rejected");
await MustFail(() => { ContentModule.ValidateFields(draft.Material, new("a", "b", "lead", 0)); return Task.CompletedTask; }, "generated replacement of source rejected");
var unicode = new CaptureMaterial("词", null, "generated_allowed", null, "fixture", 0, DateTimeOffset.UtcNow);
ContentModule.ValidateFields(unicode, new("cí", "意义", "😀词", 2)); Check(true, "UTF16 exact target accepted");
await MustFail(() => { ContentModule.ValidateFields(unicode, new("cí", "意义", "😀词", 1)); return Task.CompletedTask; }, "UTF16 wrong offset rejected");
var contentHandler = new ContentHandler();
using var contentHttp = new HttpClient(contentHandler);
var content = new ContentModule(contentHttp, () => "fixture-key");
var output = await content.ProcessAsync(draft.Material, "language", config, new(), CancellationToken.None);
Check(contentHandler.Calls == 2 && output.Fields!.Pronunciation == "/liːd/", "one bounded retry handles malformed response");
contentHandler.AlwaysBad = true; contentHandler.Calls = 0;
await MustFail(async () => await content.ProcessAsync(draft.Material, "language", config, new(), CancellationToken.None), "invalid content fails closed");
Check(contentHandler.Calls == 2, "content retry remains bounded");
contentHandler.AlwaysBad = false; contentHandler.FailFirst = false;
contentHandler.Response = new("cí", "意义", "😀词", null);
var generated = await content.ProcessAsync(unicode, "language", config, new(), CancellationToken.None);
Check(generated.Fields!.GeneratedTargetStart == 2, "generated target resolved from unique exact UTF16 occurrence");
contentHandler.Response = contentHandler.Response with { GeneratedTargetStart = 999 };
generated = await content.ProcessAsync(unicode, "language", config, new(), CancellationToken.None);
Check(generated.Fields!.GeneratedTargetStart == 2, "legacy model offset cannot displace the unique generated target");
contentHandler.Response = new("cí", "意义", "词和词", null);
generated = await content.ProcessAsync(unicode, "language", config, new(), CancellationToken.None);
Check(generated.Fields!.GeneratedTargetStart == 0, "repeated exact target deterministically selects the first occurrence");
contentHandler.Response = new("cí", "意义", "没有原目标", null);
await MustFail(async () => await content.ProcessAsync(unicode, "language", config, new(), CancellationToken.None), "missing generated target rejected");
contentHandler.Response = new("a", "b", "aaa", null);
generated = await content.ProcessAsync(unicode with { SelectedText = "aa" }, "language", config, new(), CancellationToken.None);
Check(generated.Fields!.GeneratedTargetStart == 0, "overlapping exact matches retain the first occurrence");
contentHandler.Response = new("a", "b", "LEAD the team", null);
await MustFail(async () => await content.ProcessAsync(unicode with { SelectedText = "lead" }, "language", config, new(), CancellationToken.None), "generated target matching stays case sensitive");
contentHandler.Response = new("a", "b", "lead", null);
await MustFail(async () => await content.ProcessAsync(draft.Material, "language", config, new(), CancellationToken.None), "source context is never repaired into generated context");

using var backend = new AnkiHandler(); using var http = new HttpClient(backend);
var anki = new AnkiConnection(http, "http://127.0.0.1:8765");
var first = Draft(); first.ConfirmedNote = AnkiNoteDraft.From(first); first.ImportState = ImportState.Ready;
await anki.ImportAsync(first, store);
Check(first.ImportState == ImportState.Verified && first.CardIds.Length == 2 && backend.Adds == 1, "one note produces two verified cards");
await anki.ImportAsync(first, store);
Check(backend.Adds == 1, "repeated confirmation produces no new note");
var second = Draft(); second.ConfirmedNote = AnkiNoteDraft.From(second); second.ImportState = ImportState.Ready;
backend.TimeoutAfterWrite = true;
await anki.ImportAsync(second, store);
Check(second.AwaitingAnki, "uncertain write retains automatic Anki recovery intent");
Check(second.ImportState == ImportState.Unknown && backend.Adds == 2, "timeout does not become safe-to-add");
await anki.ImportAsync(second, store);
Check(second.ImportState == ImportState.Verified && backend.Adds == 2, "uncertain write reconciled without duplicate");
var missing = Draft(); missing.ConfirmedNote = AnkiNoteDraft.From(missing); missing.ImportState = ImportState.Unknown;
await MustFail(() => anki.ImportAsync(missing, store), "unknown absent result does not auto-add");
Check(backend.Adds == 2, "unknown absent result has zero mutations");
var drift = Draft(); drift.ConfirmedNote = AnkiNoteDraft.From(drift); drift.ImportState = ImportState.Ready;
backend.DriftReadback = true; await MustFail(() => anki.ImportAsync(drift, store), "field drift rejected after write");
var additions = backend.Adds; await MustFail(() => anki.ImportAsync(drift, store), "readback failure remains unresolved");
Check(backend.Adds == additions, "readback mismatch never re-adds");
Check(store.Recent().Any(d => d.Id == second.Id && d.CardIds.Length == 2 && d.ConfirmedNote != null), "confirmed result survives restart");

using var freshBackend = new AnkiHandler { ModelMissing = true };
using var freshHttp = new HttpClient(freshBackend);
var freshAnki = new AnkiConnection(freshHttp, "http://127.0.0.1:8765");
var fresh = Draft(); fresh.ConfirmedNote = AnkiNoteDraft.From(fresh); fresh.ImportState = ImportState.Ready;
await freshAnki.ImportAsync(fresh, store);
Check(freshBackend.ModelCreatedWithExactKeys, "first model creation preserves Anki template Name Front Back casing");
var buried = Draft(); buried.ConfirmedNote = AnkiNoteDraft.From(buried); buried.ImportState = ImportState.Ready;
freshBackend.Buried = true;
await MustFail(() => freshAnki.ImportAsync(buried, store), "buried card is not reported as reviewable");
Check(buried.ImportState == ImportState.Unknown && buried.NoteId != null, "unreviewable written note remains available for reconciliation");
freshBackend.Buried = false;
freshBackend.WrongCardId = true;
await MustFail(() => freshAnki.ImportAsync(buried, store), "card readback must match the note card identities");
Check(freshBackend.Adds == 2, "card-state and identity failures do not re-add an existing note");
var historyStore = new DraftStore(@"D:\Caches\KnowledgeClipper\history-tests\" + Guid.NewGuid().ToString("N"));
var earlierSpeech = Draft(); earlierSpeech.Times["speechStarted"] = DateTimeOffset.UtcNow.AddHours(-2); historyStore.Save(earlierSpeech);
var laterSpeech = Draft(); laterSpeech.Times["speechRequested"] = DateTimeOffset.UtcNow.AddHours(-1); historyStore.Save(laterSpeech);
for (var i = 0; i < 23; i++) historyStore.Save(Draft());
Check(historyStore.RecentSpeech().Select(d => d.Id).SequenceEqual(new[] { laterSpeech.Id, earlierSpeech.Id }), "reading history ignores newer text-only drafts and retains older readings");
earlierSpeech.Times["speechRequested"] = DateTimeOffset.UtcNow; historyStore.Save(earlierSpeech);
Check(historyStore.RecentSpeech().First().Id == earlierSpeech.Id, "last played record survives a fresh history load");
Check(historyStore.Recent().Count() == 20, "general draft list remains bounded");
Console.WriteLine($"{checks} checks passed; no external calls");

sealed class ContentHandler : HttpMessageHandler
{
    public int Calls; public bool AlwaysBad; public bool FailFirst = true;
    public LanguageFields Response = new("/liːd/", "带领", null, null);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = AlwaysBad || FailFirst && Calls == 1 ? "{}" : JsonSerializer.Serialize(new { Response.Pronunciation, Response.Meaning, Response.GeneratedSentence, Response.GeneratedTargetStart }, WorkflowJson.Options) } } } }) });
    }
}

sealed class AnkiHandler : HttpMessageHandler
{
    public int Adds; public bool TimeoutAfterWrite, DriftReadback;
    public bool ModelMissing, ModelCreatedWithExactKeys, Buried, WrongCardId;
    private readonly Dictionary<long, JsonElement> notes = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content!.Headers.ContentLength is not > 0) throw new InvalidDataException("Anki requires a buffered request with Content-Length");
        using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var action = data.RootElement.GetProperty("action").GetString(); var p = data.RootElement.GetProperty("params");
        object? result;
        switch (action)
        {
            case "modelNames": result = ModelMissing ? Array.Empty<string>() : new[] { "零碎知识剪藏 · 语言共享字段" }; break;
            case "createModel":
                foreach (var template in p.GetProperty("cardTemplates").EnumerateArray())
                    if (!template.TryGetProperty("Name", out _) || !template.TryGetProperty("Front", out _) || !template.TryGetProperty("Back", out _))
                        throw new InvalidDataException("Anki template keys are case-sensitive");
                ModelMissing = false; ModelCreatedWithExactKeys = true; result = new { }; break;
            case "deckNames": result = new[] { "零碎知识剪藏::语言学习" }; break;
            case "modelFieldNames": result = new[] { "ClipId", "Target", "SentenceBefore", "SentenceAfter", "Pronunciation", "Meaning", "Source", "Origin", "ReadingEnabled", "WritingEnabled" }; break;
            case "modelTemplates": result = new Dictionary<string, object> { ["理解与朗读"] = new { }, ["书写练习"] = new { } }; break;
            case "findNotes":
                var tag = p.GetProperty("query").GetString()![4..]; result = notes.Where(n => n.Value.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == tag)).Select(n => n.Key).ToArray(); break;
            case "addNote":
                Adds++; notes[Adds] = p.GetProperty("note").Clone();
                if (TimeoutAfterWrite) { TimeoutAfterWrite = false; throw new TaskCanceledException("fixture lost response"); }
                result = Adds; break;
            case "notesInfo":
                var id = p.GetProperty("notes")[0].GetInt64(); var note = notes[id];
                result = new[] { new { noteId = id, modelName = note.GetProperty("modelName").GetString(),
                    fields = note.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => new { value = DriftReadback && f.Name == "Meaning" ? "mismatch" : f.Value.GetString() }),
                    tags = note.GetProperty("tags"), cards = new[] { id * 10, id * 10 + 1 } } }; break;
            case "cardsInfo":
                result = p.GetProperty("cards").EnumerateArray().Select(c => { var cid = c.GetInt64(); var n = notes[cid / 10]; var f = n.GetProperty("fields");
                    return new { cardId = WrongCardId ? cid + 10000 : cid, note = cid / 10, ord = (int)(cid % 10), queue = Buried ? -2 : 0, deckName = n.GetProperty("deckName").GetString(),
                        question = "lead /liːd/ 带领", answer = "lead /liːd/ 带领" }; }).ToArray(); break;
            case "areSuspended": result = p.GetProperty("cards").EnumerateArray().Select(_ => false).ToArray(); break;
            default: throw new Exception("Unexpected action: " + action);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
}

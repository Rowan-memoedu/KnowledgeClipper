using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var output = args[0];
Directory.CreateDirectory(output);
var root = Path.Combine(@"D:\Caches\KnowledgeClipper\anki-queue-tests", Guid.NewGuid().ToString("N"));
var store = new DraftStore(root);
var excerpts = new DraftStore(root, "excerpt-cards");
var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
ClipDraft Draft()
{
    var material = new CaptureMaterial("Public queue fixture", null, "generated_allowed", null, "Local test", 0, DateTimeOffset.UtcNow);
    var d = new ClipDraft { Original = material, Material = material, Configuration = new("", []), Settings = new(),
        Content = new("translation", Basic: new(material.SelectedText, "公开队列测试")), ConfirmedDestination = ExportDestination.Anki };
    d.ConfirmedNote = AnkiNoteDraft.From(d); d.ImportState = ImportState.Ready; d.Times["confirmed"] = DateTimeOffset.UtcNow;
    return d;
}
using var backend = new QueueHandler();
using var http = new HttpClient(backend);
var importer = new AnkiConnection(http, "http://127.0.0.1:8765");
var first = Draft();
var second = Draft();
var third = Draft();
var old = Draft(); old.ImportState = ImportState.Failed; store.Save(old);
using (var queue = new AnkiPendingQueue(root, http))
{
    await queue.Ready;
    await importer.ImportAsync(first, store);
    await importer.ImportAsync(second, store);
    await importer.ImportAsync(third, excerpts);
    Check(queue.Count == 3 && backend.Adds == 0, "all three offline drafts queue without writes across both folders");
    foreach (var (draft, target) in new[] { (first, store), (second, store), (third, excerpts) })
    {
        var saved = target.Find(draft.Id)!;
        Check(saved.AwaitingAnki && saved.ConfirmedNote!.Fields["Back"] == draft.ConfirmedNote!.Fields["Back"] && saved.Content == draft.Content,
            "offline confirmation and generated content survive disk " + draft.Id);
    }
    await queue.PumpAsync();
    Check(queue.Count == 3 && backend.Adds == 0, "offline polling retains entire queue");
}
using (var queue = new AnkiPendingQueue(root, http))
{
    await queue.Ready;
    Check(queue.Count == 3 && !store.Find(old.Id)!.AwaitingAnki, "fresh queue restores disk entries and ignores unmarked old Failed");
    backend.Online = true;
    backend.CollectionLoaded = false;
    await queue.PumpAsync();
    Check(queue.Count == 3 && backend.Adds == 0, "running Anki without loaded collection retains queued drafts");
    backend.CollectionLoaded = true;
    var edited = store.Find(first.Id)!;
    edited.PendingFollowUp = "Public queued follow-up"; edited.SpeechStatus = "Updated while queued";
    store.Save(edited);
    backend.PauseNextAdd = true;
    var pumping = queue.PumpAsync();
    await backend.AddEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    edited = store.Find(first.Id)!;
    edited.Conversation.Add(new("Public later question", new("answered", "Public later answer", [])));
    edited.SpeechStatus = "Updated during import";
    store.Save(edited);
    var manual = importer.ImportAsync(store.Find(first.Id)!, store);
    backend.ReleaseAdd.TrySetResult();
    await Task.WhenAll(pumping, manual);
    Check(backend.Adds == 3 && queue.Count == 0, "automatic pump plus simultaneous manual retry adds each draft once");
    edited = store.Find(first.Id)!;
    Check(edited.PendingFollowUp == "Public queued follow-up" && edited.SpeechStatus == "Updated during import" && edited.Conversation.Count == 1,
        "queued and in-flight conversation and property edits survive import metadata saves");
    Check(store.Find(first.Id)!.ImportState == ImportState.Verified && store.Find(second.Id)!.ImportState == ImportState.Verified && excerpts.Find(third.Id)!.ImportState == ImportState.Verified,
        "online pump persists verified status for all queued drafts");
    Check(backend.Actions.Count(a => a == "notesInfo") >= 3 && backend.Actions.Count(a => a == "cardsInfo") >= 3 && backend.Actions.Count(a => a == "areSuspended") >= 3,
        "verified completion includes note fields, card rendering and suspension readback");
    var before = backend.Actions.Count;
    await queue.PumpAsync();
    Check(backend.Actions.Count == before && backend.Adds == 3, "completed queue makes no more provider calls");

    var uncertain = Draft();
    backend.DropAfterWrite = true;
    await importer.ImportAsync(uncertain, store);
    Check(uncertain.AwaitingAnki && uncertain.ImportState == ImportState.Unknown && backend.Adds == 4 && queue.Count == 1,
        "lost add response remains durable Unknown awaiting Anki");
    before = backend.Actions.Count;
    await queue.PumpAsync();
    var recovery = backend.Actions.Skip(before).ToArray();
    Check(store.Find(uncertain.Id)!.ImportState == ImportState.Verified && backend.Adds == 4 && recovery.All(a => a is "deckNames" or "findNotes" or "notesInfo" or "cardsInfo" or "areSuspended"),
        "Unknown recovery uses query and verification only without duplicate add");

    var semantic = Draft();
    backend.Online = false; await importer.ImportAsync(semantic, store); backend.Online = true;
    backend.SemanticFailure = true;
    await queue.PumpAsync();
    Check(!store.Find(semantic.Id)!.AwaitingAnki && queue.Count == 0 && backend.Adds == 4, "semantic template error exits automatic queue without write");
    before = backend.Actions.Count;
    await queue.PumpAsync(); await queue.PumpAsync();
    Check(backend.Actions.Count == before, "semantic error does not retry indefinitely");
    Check(store.Find(old.Id)!.ImportState == ImportState.Failed && store.Find(old.Id)!.NoteId == null, "old unmarked failure remains untouched");
}
backend.SemanticFailure = false;
var canceled = Draft();
using (var queue = new AnkiPendingQueue(root, http))
{
    await queue.Ready;
    backend.Online = false; await importer.ImportAsync(canceled, store); backend.Online = true;
    backend.PauseAfterWrite = true;
    using var stopping = new CancellationTokenSource();
    var pumping = queue.PumpAsync(stopping.Token);
    await backend.Written.Task.WaitAsync(TimeSpan.FromSeconds(10));
    stopping.Cancel();
    try { await pumping; throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
    Check(store.Find(canceled.Id) is { AwaitingAnki: true, ImportState: ImportState.Unknown } && backend.Adds == 5,
        "shutdown cancellation preserves queued Unknown after committed write");
}
using (var queue = new AnkiPendingQueue(root, http))
{
    await queue.Ready;
    Check(queue.Count == 1, "restart restores entry canceled during import");
    var before = backend.Actions.Count;
    await queue.PumpAsync();
    Check(store.Find(canceled.Id) is { AwaitingAnki: false, ImportState: ImportState.Verified } && queue.Count == 0 && backend.Adds == 5 &&
        backend.Actions.Skip(before).All(a => a is "deckNames" or "findNotes" or "notesInfo" or "cardsInfo" or "areSuspended"),
        "restart reconciles canceled Unknown using reads without second add");
}
using (var freshBackend = new QueueHandler { Online = true, PauseAfterWrite = true })
using (var freshHttp = new HttpClient(freshBackend))
using (var stopping = new CancellationTokenSource())
{
    var fresh = Draft(); store.Save(fresh);
    var request = new AnkiConnection(freshHttp, fresh.Settings.AnkiEndpoint).ImportAsync(fresh, store, stopping.Token);
    await freshBackend.Written.Task.WaitAsync(TimeSpan.FromSeconds(10)); stopping.Cancel();
    try { await request; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
    Check(store.Find(fresh.Id) is { AwaitingAnki: true, ImportState: ImportState.Unknown },
        "shutdown during the first confirmed import also preserves automatic recovery intent");
}
WorkflowJson.Save(Path.Combine(output, "checks.json"), new { outcome = "PASS", checks, root, actions = backend.Actions, boundary = "Fake HttpMessageHandler; no live Anki calls or production data" });

sealed class QueueHandler : HttpMessageHandler
{
    public List<string> Actions { get; } = [];
    public int Adds { get; private set; }
    public bool Online, DropAfterWrite, SemanticFailure, PauseNextAdd;
    public bool CollectionLoaded = true, PauseAfterWrite;
    public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AddEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseAdd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool model;
    private readonly Dictionary<long, JsonElement> notes = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var action = json.RootElement.GetProperty("action").GetString()!;
        var p = json.RootElement.GetProperty("params");
        Actions.Add(action);
        if (!Online) throw new HttpRequestException("fixture connection refused");
        object? result;
        switch (action)
        {
            case "version": result = 6; break;
            case "modelNames": result = model ? new[] { AnkiNoteDraft.BasicModelName } : Array.Empty<string>(); break;
            case "createModel": model = true; result = new { }; break;
            case "modelFieldNames": result = new[] { "ClipId", "Front", "Back", "Source" }; break;
            case "modelTemplates": result = new Dictionary<string, object> { [SemanticFailure ? "user-customized" : "正反面"] = new { } }; break;
            case "deckNames":
                if (!CollectionLoaded) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { result = (object?)null, error = "collection is not loaded" }) };
                result = new[] { "零碎知识剪藏::语言学习" }; break;
            case "findNotes":
                var tag = p.GetProperty("query").GetString()![4..];
                result = notes.Where(n => n.Value.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == tag)).Select(n => n.Key).ToArray(); break;
            case "addNote":
                if (PauseNextAdd) { PauseNextAdd = false; AddEntered.TrySetResult(); await ReleaseAdd.Task.WaitAsync(ct); }
                Adds++; notes[Adds] = p.GetProperty("note").Clone();
                if (PauseAfterWrite) { PauseAfterWrite = false; Written.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                if (DropAfterWrite) { DropAfterWrite = false; throw new TaskCanceledException("fixture response lost after committed write"); }
                result = Adds; break;
            case "notesInfo":
                var id = p.GetProperty("notes")[0].GetInt64(); var note = notes[id];
                result = new[] { new { noteId = id, modelName = note.GetProperty("modelName").GetString(), fields = note.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => new { value = f.Value.GetString() }), tags = note.GetProperty("tags"), cards = new[] { id * 10 } } }; break;
            case "cardsInfo":
                result = p.GetProperty("cards").EnumerateArray().Select(c => { var cid = c.GetInt64(); var n = notes[cid / 10]; var f = n.GetProperty("fields");
                    return new { cardId = cid, note = cid / 10, ord = 0, queue = 0, deckName = n.GetProperty("deckName").GetString(), question = f.GetProperty("Front").GetString(), answer = f.GetProperty("Back").GetString() }; }).ToArray(); break;
            case "areSuspended": result = p.GetProperty("cards").EnumerateArray().Select(_ => false).ToArray(); break;
            default: throw new InvalidOperationException("Unexpected mutation or action: " + action);
        }
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
}

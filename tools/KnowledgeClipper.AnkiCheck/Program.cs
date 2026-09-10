using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

var options = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("--input 文件 --result 文件 --settings 文件 --modes 目录 --output 文件 [--practice Both|Reading|Writing] [--lose-add-response true]");
    options.Add(args[i], args[i + 1]);
}
if (options.TryGetValue("--verify-draft", out var existingDraft))
    return await VerifyExistingAsync(existingDraft, options["--output"]);
if (options.ContainsKey("--probe-transport"))
    return await ProbeTransportAsync(options["--settings"], options["--output"]);
var outputPath = options["--output"];
var evidence = new Dictionary<string, object?>
{
    ["startedAt"] = DateTimeOffset.UtcNow,
    ["boundary"] = "Production AnkiConnection with actual AnkiConnect. No desktop or content quality claim.",
    ["inputPath"] = Path.GetFullPath(options["--input"]),
    ["resultPath"] = Path.GetFullPath(options["--result"]),
    ["inputSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options["--input"]))),
    ["resultSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options["--result"])))
};
var stages = new List<object>();
evidence["stages"] = stages;
using var transport = new TraceHandler { InnerHandler = new HttpClientHandler() };
using var http = new HttpClient(transport);
try
{
    using var input = JsonDocument.Parse(File.ReadAllText(options["--input"]));
    var data = input.RootElement;
    var text = data.GetProperty("selectedText").GetString()!;
    var context = data.TryGetProperty("contextText", out var c) ? c.GetString() : null;
    var origin = data.TryGetProperty("contextOrigin", out var o) ? o.GetString()! : context == null ? "generated_allowed" : "source";
    int? start = data.TryGetProperty("targetStart", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
    var material = new CaptureMaterial(text, context, origin, start, Path.GetFileName(options["--input"]), 0, DateTimeOffset.UtcNow);
    material.Validate();
    var settings = WorkflowJson.Read<LanguageSettings>(options["--settings"]);
    settings.Validate();
    var config = ModeConfigurationLoader.Load(options["--modes"]);
    var content = WorkflowJson.Read<ContentResult>(options["--result"]);
    var practice = Enum.Parse<PracticeChoice>(options.GetValueOrDefault("--practice", "Both"), true);
    var draft = new ClipDraft { Original = material, Material = material, Settings = settings, Configuration = config,
        ModeId = content.ModeId, Content = content, Practice = practice, ImportState = ImportState.Ready };
    draft.ConfirmedNote = AnkiNoteDraft.From(draft);
    var dataRoot = Path.Combine(@"D:\AppData\KnowledgeClipper\canary\anki", draft.Id);
    var store = new DraftStore(dataRoot);
    store.Save(draft);
    evidence["draftPath"] = Path.Combine(dataRoot, "language-drafts", draft.Id + ".json");
    evidence["draftId"] = draft.Id;
    evidence["deck"] = draft.ConfirmedNote.Deck;
    evidence["cardDecks"] = draft.ConfirmedNote.CardDecks;
    evidence["model"] = draft.ConfirmedNote.Model;
    evidence["practice"] = draft.ConfirmedNote.IsBasic ? "Basic" : practice.ToString();
    evidence["confirmedNote"] = draft.ConfirmedNote;
    var anki = new AnkiConnection(http, settings.AnkiEndpoint);
    var version = await anki.CallAsync("version");
    Check(version.GetInt32() == 6, "AnkiConnect v6 responds");
    evidence["ankiConnectVersion"] = version;
    var loseResponse = options.GetValueOrDefault("--lose-add-response") == "true";
    transport.DropNextAddResponse = loseResponse;
    WorkflowJson.Save(outputPath, evidence);
    try { await anki.ImportAsync(draft, store); }
    catch (TaskCanceledException) when (loseResponse && transport.DroppedResponses == 1)
    {
        Check(draft.ImportState == ImportState.Unknown && draft.NoteId == null, "lost add response remains Unknown");
        stages.Add(new { name = "lost-response", state = draft.ImportState.ToString(), actions = transport.Actions.ToArray(), injectedAfterActualSuccess = true });
        WorkflowJson.Save(outputPath, evidence);
        draft = store.Recent().Single(d => d.Id == draft.Id);
        var before = transport.Actions.Count;
        await anki.ImportAsync(draft, store);
        var reconciliation = transport.Actions.Skip(before).ToArray();
        Check(reconciliation.Length > 0 && reconciliation[0] == "findNotes" && !reconciliation.Contains("addNote"), "restart reconciliation queries before reuse, without another add");
        stages.Add(new { name = "reconciled", actions = reconciliation });
    }
    Check(draft.ImportState == ImportState.Verified && draft.NoteId != null, "production import verifies actual result");
    Check(transport.Actions.Count(a => a == "addNote") == 1, "one addNote invocation for this operation");
    var actionsBeforeRepeat = transport.Actions.Count;
    await anki.ImportAsync(draft, store);
    Check(transport.Actions.Count == actionsBeforeRepeat, "repeated confirmation performs no provider call");
    stages.Add(new { name = "repeat-confirmation", providerCalls = 0, noteId = draft.NoteId });
    var noteIds = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id });
    var cardIds = await anki.CallAsync("findCards", new { query = "nid:" + draft.NoteId });
    var notes = await anki.CallAsync("notesInfo", new { notes = new[] { draft.NoteId!.Value } });
    var cards = await anki.CallAsync("cardsInfo", new { cards = draft.CardIds });
    var suspended = await anki.CallAsync("areSuspended", new { cards = draft.CardIds });
    evidence["readback"] = new { noteIds, cardIds, notes, cards, suspended };
    Check(noteIds.GetArrayLength() == 1 && noteIds[0].GetInt64() == draft.NoteId, "exactly one actual note by operation tag");
    Check(cardIds.EnumerateArray().Select(x => x.GetInt64()).Order().SequenceEqual(draft.CardIds.Order()), "findCards and notesInfo identify the same cards");
    Check(draft.CardIds.Length == draft.ConfirmedNote!.Ordinals.Length, "actual card count matches confirmed note type and practice");
    var noteFields = notes[0].GetProperty("fields");
    foreach (var field in draft.ConfirmedNote!.Fields)
        Check(noteFields.GetProperty(field.Key).GetProperty("value").GetString() == field.Value, "actual field matches confirmation: " + field.Key);
    if (draft.ConfirmedNote.IsBasic)
    {
        Check(noteFields.GetProperty("Front").GetProperty("value").GetString() == AnkiNoteDraft.Html(material.SelectedText), "Basic front preserves the original input");
        Check(noteFields.GetProperty("Back").GetProperty("value").GetString() == AnkiNoteDraft.Html(content.Basic!.Back), "Basic back preserves the confirmed translation or answer");
    }
    else
    {
        var recoveredSentence = Plain(noteFields.GetProperty("SentenceBefore").GetProperty("value").GetString()!)
            + Plain(noteFields.GetProperty("Target").GetProperty("value").GetString()!)
            + Plain(noteFields.GetProperty("SentenceAfter").GetProperty("value").GetString()!);
        Check(recoveredSentence == (material.ContextText ?? content.Fields!.GeneratedSentence), "source or generated sentence preserved exactly");
    }
    foreach (var card in cards.EnumerateArray())
    {
        Check(card.GetProperty("note").GetInt64() == draft.NoteId && card.GetProperty("deckName").GetString() == draft.ConfirmedNote.DeckFor(card.GetProperty("ord").GetInt32()), "actual card belongs to selected note and its confirmed deck");
        Check(card.GetProperty("queue").GetInt32() >= 0, "actual card is neither suspended nor buried");
        var question = Plain(card.GetProperty("question").GetString()!);
        var answer = Plain(card.GetProperty("answer").GetString()!);
        if (draft.ConfirmedNote.IsBasic)
        {
            Check(card.GetProperty("ord").GetInt32() == 0 && question.Contains(Plain(draft.ConfirmedNote.Fields["Front"]), StringComparison.Ordinal)
                && answer.Contains(Plain(draft.ConfirmedNote.Fields["Back"]), StringComparison.Ordinal), "actual Basic card renders the original front and confirmed back");
            continue;
        }
        Check(!string.IsNullOrWhiteSpace(question) && answer.Contains(material.SelectedText, StringComparison.Ordinal), "rendered actual card has question and target answer");
        if (card.GetProperty("ord").GetInt32() == 1)
        {
            Check(question.Contains(content.Fields!.Pronunciation, StringComparison.Ordinal) && question.Contains(content.Fields.Meaning, StringComparison.Ordinal), "writing front exposes pronunciation and meaning hints");
            var contextWithoutTarget = (material.ContextText ?? content.Fields.GeneratedSentence!).Remove(material.TargetStart ?? content.Fields.GeneratedTargetStart!.Value, material.SelectedText.Length);
            if (!contextWithoutTarget.Contains(material.SelectedText, StringComparison.Ordinal) && !content.Fields.Meaning.Contains(material.SelectedText, StringComparison.Ordinal) && !content.Fields.Pronunciation.Contains(material.SelectedText, StringComparison.Ordinal))
                Check(!question.Contains(material.SelectedText, StringComparison.Ordinal), "writing front masks the selected target");
        }
    }
    evidence["noteId"] = draft.NoteId;
    evidence["cardIds"] = draft.CardIds;
    evidence["addNoteInvocations"] = transport.Actions.Count(a => a == "addNote");
    evidence["actions"] = transport.Actions;
    evidence["status"] = "passed";
    evidence["completedAt"] = DateTimeOffset.UtcNow;
    WorkflowJson.Save(outputPath, evidence);
    Console.WriteLine($"PASS Anki interface: note {draft.NoteId}; {draft.CardIds.Length} actual reviewable cards; {evidence["practice"]}; response-loss injection {loseResponse}.");
    Console.WriteLine("Evidence: " + Path.GetFullPath(outputPath));
    return 0;
}
catch (Exception ex)
{
    evidence["status"] = "failed";
    evidence["failure"] = ex.GetType().Name + ": " + ex.Message;
    evidence["actions"] = transport.Actions;
    WorkflowJson.Save(outputPath, evidence);
    Console.Error.WriteLine(evidence["failure"]);
    Console.Error.WriteLine("Evidence: " + Path.GetFullPath(outputPath));
    return 1;
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidDataException(name);
    stages.Add(new { check = name, passed = true });
}
static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]*>", ""));

static async Task<int> ProbeTransportAsync(string settingsFile, string output)
{
    var settings = WorkflowJson.Read<LanguageSettings>(settingsFile);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var observations = new List<object>();
    var payload = new { action = "findNotes", version = 6, @params = new { query = "tag:kc_transport_probe_absent" } };
    foreach (var buffered in new[] { false, true })
    {
        using HttpContent body = buffered ? new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload)) : JsonContent.Create(payload);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var contentLength = body.Headers.ContentLength;
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.AnkiEndpoint) { Content = body };
        using var response = await http.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        observations.Add(new { buffered, contentLengthBeforeSend = contentLength, transferEncodingChunked = request.Headers.TransferEncodingChunked,
            statusCode = (int)response.StatusCode, rootKind = root.ValueKind.ToString(),
            rootProperties = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(p => p.Name).ToArray() : [],
            hasV6Envelope = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _) && root.TryGetProperty("result", out _) });
    }
    // A bare HttpClient must also exercise the production adapter, without TraceHandler buffering.
    var actual = await new AnkiConnection(http, settings.AnkiEndpoint).CallAsync("findNotes", payload.@params);
    observations.Add(new { productionAdapterWithBareHttpClient = true, resultKind = actual.ValueKind.ToString(), resultCount = actual.GetArrayLength() });
    WorkflowJson.Save(output, observations);
    Console.WriteLine(JsonSerializer.Serialize(observations, WorkflowJson.Options));
    return 0;
}

static async Task<int> VerifyExistingAsync(string draftPath, string output)
{
    var draft = WorkflowJson.Read<ClipDraft>(draftPath);
    var report = new Dictionary<string, object?> { ["draftId"] = draft.Id, ["previousState"] = draft.ImportState.ToString(), ["previousError"] = draft.ImportError };
    using var transport = new TraceHandler { InnerHandler = new HttpClientHandler(), ReadOnly = true };
    using var http = new HttpClient(transport);
    var anki = new AnkiConnection(http, draft.Settings.AnkiEndpoint);
    var store = new DraftStore(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(draftPath)))!);
    try
    {
        var matches = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id });
        report["matches"] = matches;
        if (matches.GetArrayLength() == 0)
        {
            // Diagnose the same metadata calls without entering any write preparation.
            var model = draft.ConfirmedNote?.Model ?? (draft.Content?.Basic != null ? AnkiNoteDraft.BasicModelName : draft.Settings.AnkiModel);
            report["modelNamesIncludesTarget"] = (await anki.ModelsAsync()).Contains(model);
            report["modelFields"] = await anki.CallAsync("modelFieldNames", new { modelName = model });
            report["templates"] = await anki.CallAsync("modelTemplates", new { modelName = model });
            var decks = await anki.DecksAsync();
            report["deckNamesIncludesTarget"] = draft.ConfirmedNote!.Ordinals.All(ordinal => decks.Contains(draft.ConfirmedNote.DeckFor(ordinal)));
            report["status"] = "not-found-no-write";
            Console.WriteLine("No note for this draft; metadata responds; no write attempted.");
        }
        else
        {
            if (draft.ImportState == ImportState.Verified) await anki.VerifyAsync(draft);
            else await anki.ImportAsync(draft, store);
            report["notes"] = await anki.CallAsync("notesInfo", new { notes = new[] { draft.NoteId!.Value } });
            report["cards"] = await anki.CallAsync("cardsInfo", new { cards = draft.CardIds });
            report["state"] = draft.ImportState.ToString();
            report["noteId"] = draft.NoteId;
            report["cardIds"] = draft.CardIds;
            report["status"] = "verified-no-write";
            Console.WriteLine($"PASS readback: note {draft.NoteId}, {draft.CardIds.Length} cards; no write attempted.");
        }
        return 0;
    }
    catch (Exception ex)
    {
        report["status"] = "failed-no-write";
        report["exception"] = ex.ToString();
        Console.Error.WriteLine(ex.ToString());
        return 1;
    }
    finally
    {
        report["actions"] = transport.Actions;
        report["completedAt"] = DateTimeOffset.UtcNow;
        WorkflowJson.Save(output, report);
        Console.WriteLine("Evidence: " + Path.GetFullPath(output));
    }
}

sealed class TraceHandler : DelegatingHandler
{
    public List<string> Actions { get; } = [];
    public bool DropNextAddResponse { get; set; }
    public int DroppedResponses { get; private set; }
    public bool ReadOnly { get; init; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content!.Headers.ContentLength is not > 0)
            throw new InvalidOperationException("Anki request must have Content-Length before validation reads its body.");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var action = json.RootElement.GetProperty("action").GetString()!;
        if (ReadOnly && action is not ("version" or "findNotes" or "findCards" or "notesInfo" or "cardsInfo" or "areSuspended" or "modelNames" or "modelFieldNames" or "modelTemplates" or "deckNames"))
            throw new InvalidOperationException("Read-only validation refused action: " + action);
        Actions.Add(action);
        var response = await base.SendAsync(request, ct);
        if (DropNextAddResponse && action == "addNote")
        {
            using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (response.IsSuccessStatusCode && receipt.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null && receipt.RootElement.GetProperty("result").ValueKind == JsonValueKind.Number)
            {
                DropNextAddResponse = false;
                DroppedResponses++;
                response.Dispose();
                throw new TaskCanceledException("Validation injected a lost response after the real Anki addNote completed.");
            }
        }
        return response;
    }
}

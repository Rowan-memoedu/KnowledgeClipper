using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;

internal sealed class RequestTrace : DelegatingHandler
{
    internal List<TraceEntry> Entries { get; } = [];
    internal int WriteCount => Entries.Count(entry => entry.Operation is "export" or "finalize-media" or "addNote");
    internal RequestTrace() : base(new HttpClientHandler { UseProxy = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.IsLoopback != true) throw new InvalidOperationException("Live exporter fixtures may only contact loopback services.");
        var operation = request.RequestUri.Segments.Last().Trim('/');
        string? sourceId = null;
        if (request.Content != null)
        {
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            if (!request.RequestUri.AbsolutePath.Contains("/sdk/knowledge-clipper/", StringComparison.Ordinal))
                operation = payload.RootElement.GetProperty("action").GetString() ?? "unknown";
            else
            {
                var plan = payload.RootElement.TryGetProperty("plan", out var wrapped) ? wrapped : payload.RootElement;
                if (plan.TryGetProperty("sourceId", out var source)) sourceId = source.GetString();
            }
        }
        var entry = new TraceEntry(DateTimeOffset.UtcNow, request.Method.Method, operation) { SourceId = sourceId, Address = request.RequestUri!.AbsoluteUri, Port = request.RequestUri.Port }; Entries.Add(entry);
        try { var result = await base.SendAsync(request, ct); entry.Status = (int)result.StatusCode; return result; }
        catch (Exception ex) { entry.ErrorType = ex.GetType().Name; throw; }
    }
}
internal sealed class TraceEntry(DateTimeOffset at, string method, string operation)
{
    public DateTimeOffset At { get; } = at;
    public string Method { get; } = method;
    public string Operation { get; } = operation;
    public string? SourceId { get; set; }
    public string? Address { get; set; }
    public int? Port { get; set; }
    public int? Status { get; set; }
    public string? ErrorType { get; set; }
}

internal static class ReadbackChecks
{
    private static readonly JsonSerializerOptions Wire = new(WorkflowJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static async Task VerifyAsync(string key, ClipDraft draft, DraftStore store, FixtureSet fixtures, HttpClient http, string evidencePath,
        Action<bool, string> check, CancellationToken ct)
    {
        FixtureMatrix.CheckPlan(key, draft, fixtures, check);
        check(draft.ImportState == ImportState.Verified, key + ": production importer marked verified");
        try
        {
            if (ExportRouting.For(draft) == ExportDestination.Anki)
            {
                var beforeNote = draft.NoteId; var beforeCards = draft.CardIds.ToArray();
                var anki = new AnkiConnection(http, draft.Settings.AnkiEndpoint); await anki.VerifyAsync(draft, ct);
                var matches = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id }, ct);
                check(beforeNote == draft.NoteId && beforeCards.SequenceEqual(draft.CardIds) && matches.GetArrayLength() == 1 && matches[0].GetInt64() == draft.NoteId,
                    key + ": forced Anki readback has the original unique note and card IDs");
                check(draft.CardIds.Length == 1 && draft.ConfirmedNote!.Deck.StartsWith("KnowledgeClipper验收::RemNote SDK::", StringComparison.Ordinal),
                    key + ": one default-Anki language card is in the isolated acceptance deck");
                WorkflowJson.Save(evidencePath, new { draft.Id, destination = "Anki", draft.NoteId, draft.CardIds, deck = draft.ConfirmedNote!.Deck, verified = true });
                return;
            }
            var plan = draft.ConfirmedRemNote ?? throw new InvalidDataException("Missing confirmed RemNote plan.");
            var previous = draft.RemNoteResult ?? throw new InvalidDataException("Missing production receipt.");
            using var request = new HttpRequestMessage(HttpMethod.Post, RemNoteConnection.ResolveEndpoint(draft.Settings.RemNoteSdkEndpoint).TrimEnd('/') + "/sdk/knowledge-clipper/readback");
            request.Headers.Add("X-KnowledgeClipper-Protocol", "1"); request.Content = JsonContent.Create(plan, options: Wire);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(body); var actual = document.RootElement.Clone();
            // Preserve even a mismatching readback before validation fails.
            WorkflowJson.Save(evidencePath, actual);
            var verified = RemNoteConnection.Validate(plan, actual, previous);
            check(verified.RemIds.SequenceEqual(previous.RemIds) && verified.CardIds.SequenceEqual(previous.CardIds), key + ": forced SDK readback preserves every Rem and card ID");
            var cards = actual.GetProperty("cards").EnumerateArray().ToArray();
            var nodes = cards.SelectMany(card => card.GetProperty("nodes").EnumerateArray()).ToArray();
            check(nodes.All(node => !node.GetProperty("isDocument").GetBoolean() && node.GetProperty("remType").GetInt32() == 0), key + ": imported content consists of ordinary text Rems");
            FixtureMatrix.CheckReadback(key, actual, fixtures, check);
        }
        catch (Exception ex)
        {
            draft.ImportState = ImportState.Unknown; draft.ImportError = "Independent live readback failed: " + ex.Message; store.Save(draft); throw;
        }
    }
}

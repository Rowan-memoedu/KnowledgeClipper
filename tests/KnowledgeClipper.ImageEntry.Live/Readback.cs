using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Wire = new(WorkflowJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static async Task VerifyAsync(ClipDraft draft, HttpClient http, CancellationToken ct)
    {
        Check(draft.ImportState == ImportState.Verified && draft.ConfirmedNote != null, "production importer verified original draft " + draft.Id);
        var evidence = Path.Combine(DataRoot, "readbacks", "attempt-" + attempt.Number.ToString("D3"), draft.Id + ".json");
        string identity;
        if (ExportRouting.For(draft) == ExportDestination.Anki)
        {
            Check(draft.Material.SelectedText == "lead" && !draft.ConfirmedNote!.Fields["Back"].Contains("##", StringComparison.Ordinal),
                "the language target and generated back contain no OCR heading syntax");
            var anki = new AnkiConnection(http, draft.Settings.AnkiEndpoint);
            await anki.VerifyAsync(draft, ct);
            var matches = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id }, ct);
            var notes = await anki.CallAsync("notesInfo", new { notes = new[] { draft.NoteId!.Value } }, ct);
            var cards = await anki.CallAsync("cardsInfo", new { cards = draft.CardIds }, ct);
            var media = draft.ConfirmedNote!.Media.Single();
            var encoded = await anki.CallAsync("retrieveMediaFile", new { filename = media.FileName }, ct);
            var actualHash = ImageAsset.Hash(Convert.FromBase64String(encoded.GetString()!));
            WorkflowJson.Save(evidence, new { matches, notes, cards, actualImageSha256 = actualHash });
            Check(matches.GetArrayLength() == 1 && matches[0].GetInt64() == draft.NoteId && cards.GetArrayLength() == 1 &&
                cards[0].GetProperty("ord").GetInt32() == 0 && cards[0].GetProperty("deckName").GetString()!.StartsWith(Deck + "::", StringComparison.Ordinal),
                "independent Anki readback finds exactly one original note and reading card in the test deck");
            Check(actualHash == media.Sha256 && notes[0].GetProperty("fields").GetProperty("Front").GetProperty("value").GetString() == draft.ConfirmedNote.Fields["Front"] &&
                notes[0].GetProperty("fields").GetProperty("Back").GetProperty("value").GetString() == draft.ConfirmedNote.Fields["Back"],
                "Anki image bytes and both sides match the real generated frozen note exactly");
            identity = JsonSerializer.Serialize(new { destination = "Anki", draft.Id, draft.NoteId, draft.CardIds, actualHash }, WorkflowJson.Options);
        }
        else
        {
            var plan = draft.ConfirmedRemNote ?? throw new InvalidDataException("Missing RemNote plan.");
            var previous = draft.RemNoteResult ?? throw new InvalidDataException("Missing original RemNote result.");
            using var request = new HttpRequestMessage(HttpMethod.Post, RemNoteConnection.ResolveEndpoint(draft.Settings.RemNoteSdkEndpoint).TrimEnd('/') + "/sdk/knowledge-clipper/readback");
            request.Headers.Add("X-KnowledgeClipper-Protocol", "1"); request.Content = JsonContent.Create(plan, options: Wire);
            using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var raw = document.RootElement.Clone();
            WorkflowJson.Save(evidence, raw);
            var verified = RemNoteConnection.Validate(plan, raw, previous);
            var nodes = raw.GetProperty("cards")[0].GetProperty("nodes").EnumerateArray().ToArray();
            var media = nodes[0].GetProperty("media").EnumerateArray().ToArray();
            Check(plan.RootTitle == QuestionRootTitle && plan.DocumentPath.SequenceEqual(new[] { "问答" }) && plan.Cards.Length == 1 &&
                verified.CardIds.Length == 1 && verified.RemIds.SequenceEqual(previous.RemIds) && verified.CardIds.SequenceEqual(previous.CardIds),
                "independent RemNote readback preserves one question card under the authorized root and mode directory " + draft.Id);
            Check(nodes[0].GetProperty("parentId").GetString() == verified.DocumentRemId && !nodes[0].GetProperty("isDocument").GetBoolean() &&
                !nodes[0].GetProperty("isCardItem").GetBoolean() &&
                nodes[0].GetProperty("cardTypes").EnumerateArray().Single().GetString() == "forward" && nodes.Skip(1).All(node => node.GetProperty("isCardItem").GetBoolean()),
                "RemNote stores a single front and multiline answer children as an ordinary forward card");
            var directories = raw.GetProperty("directoryNodes").EnumerateArray().ToArray();
            Check(directories.Length == 2 && directories[0].GetProperty("remId").GetString() == verified.RootRemId &&
                directories[1].GetProperty("remId").GetString() == verified.DocumentRemId && directories.All(node =>
                    node.GetProperty("isDocument").GetBoolean() && !node.GetProperty("isCardItem").GetBoolean() &&
                    !node.GetProperty("isExtraCardDetail").GetBoolean() && !node.GetProperty("isMultiLineCard").GetBoolean() &&
                    node.GetProperty("backText").GetArrayLength() == 0 && (node.GetProperty("backTextState").GetString() is "absent" or "null") &&
                    node.GetProperty("cardIds").GetArrayLength() == 0 && node.GetProperty("cardTypes").GetArrayLength() == 0),
                "the root and mode documents have no back face, card role or practice cards");
            var savedDirectories = previous.Evidence.GetProperty("directoryNodes").EnumerateArray().ToArray();
            Check(savedDirectories.Length == directories.Length && directories.Zip(savedDirectories).All(pair =>
                pair.First.GetProperty("remId").GetString() == pair.Second.GetProperty("remId").GetString() &&
                pair.First.GetProperty("parentId").GetString() == pair.Second.GetProperty("parentId").GetString() &&
                JsonSerializer.Serialize(pair.First.GetProperty("text"), WorkflowJson.Options) == JsonSerializer.Serialize(pair.Second.GetProperty("text"), WorkflowJson.Options)),
                "the existing directory names, parents and IDs match their original import receipts");
            Check(ImageOnlyFront(plan.Cards.Single(), draft.Original.FrontImage!) && media.Length == 1 && media[0].GetProperty("sha256").GetString() == draft.Original.FrontImage!.Sha256 &&
                raw.TryGetProperty("batchSeparator", out var separator), "RemNote hosts the same original PNG and retains its empty batch separator");
            var blank = raw.GetProperty("batchSeparator").GetProperty("node");
            Check(!blank.GetProperty("practiceEnabled").GetBoolean() && blank.GetProperty("practiceDirection").GetString() == "none" &&
                (blank.GetProperty("backTextState").GetString() is "absent" or "null") && blank.GetProperty("backText").GetArrayLength() == 0 &&
                !blank.GetProperty("isCardItem").GetBoolean() && !blank.GetProperty("isExtraCardDetail").GetBoolean() &&
                !blank.GetProperty("isMultiLineCard").GetBoolean() && blank.GetProperty("cardIds").GetArrayLength() == 0,
                "the new batch separator has no back face, card role or cards, with practice explicitly disabled and direction none");
            identity = JsonSerializer.Serialize(new { destination = "RemNote", draft.Id, verified.RootRemId, verified.DocumentRemId,
                verified.RemIds, verified.CardIds, separator = raw.GetProperty("batchSeparator").GetProperty("remId").GetString() }, WorkflowJson.Options);
        }
        if (manifest.Identities.TryGetValue(draft.Id, out var saved)) Check(identity == saved, "repeat readback preserves every saved identity " + draft.Id);
        else { manifest.Identities.Add(draft.Id, identity); Save(); }
    }
}

// Store only safe request metadata and successful response payloads from public
// fixtures. Headers and request bodies, including Authorization, are never logged.
internal sealed class LiveTrace(string directory, Attempt attempt, Action save, bool readOnly = false) : DelegatingHandler(new HttpClientHandler())
{
    private int sequence;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidDataException("Missing request URI.");
        var contentCall = uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal);
        if (!contentCall && !uri.IsLoopback) throw new InvalidDataException("Exporter tests may only contact loopback services.");
        string? model = null; var imageCount = 0; string operation = contentCall ? "content" : uri.Segments.Last().Trim('/');
        if (request.Content != null)
        {
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            if (contentCall)
            {
                model = payload.RootElement.GetProperty("model").GetString();
                foreach (var message in payload.RootElement.GetProperty("messages").EnumerateArray())
                    if (message.GetProperty("content").ValueKind == JsonValueKind.Array)
                        imageCount += message.GetProperty("content").EnumerateArray().Count(part => part.GetProperty("type").GetString() == "image_url");
            }
            else if (payload.RootElement.TryGetProperty("action", out var action)) operation = action.GetString()!;
        }
        if (readOnly && operation is not ("readback" or "findNotes" or "notesInfo" or "cardsInfo" or "areSuspended" or "retrieveMediaFile"))
            throw new InvalidOperationException("The final readback phase cannot call model, OCR or mutating exporters: " + operation);
        var index = Interlocked.Increment(ref sequence);
        Console.WriteLine("REQUEST " + operation + (model == null ? "" : " model=" + model + " images=" + imageCount));
        var response = await base.SendAsync(request, ct);
        string? path = null;
        if (response.IsSuccessStatusCode)
        {
            Directory.CreateDirectory(directory); path = Path.Combine(directory, index.ToString("D3") + ".json");
            await File.WriteAllTextAsync(path, await response.Content.ReadAsStringAsync(ct), ct);
        }
        attempt.Requests.Add(new { operation, host = uri.Host, port = uri.Port, path = uri.AbsolutePath, model, imageCount, status = (int)response.StatusCode, response = path });
        save(); return response;
    }
}

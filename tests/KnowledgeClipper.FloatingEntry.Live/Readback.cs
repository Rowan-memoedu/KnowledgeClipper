using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Wire = new(WorkflowJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static async Task VerifyAsync(string key, ClipDraft draft, DraftStore store, HttpClient http)
    {
        Check(draft.ImportState == ImportState.Verified, key + ": production import completed with a verified state");
        var evidence = Path.Combine(dataRoot, "readbacks", "attempt-" + attempt.Number.ToString("D3"), key + ".json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        CardIdentity identity;
        try
        {
            if (key == "language")
            {
                Check(ExportRouting.For(draft) == ExportDestination.Anki && draft.NoteId != null && draft.CardIds.Length == 1 && draft.ConfirmedNote != null,
                    "language: one default Anki note and reading card were imported");
                var anki = new AnkiConnection(http, draft.Settings.AnkiEndpoint);
                var oldNote = draft.NoteId; var oldCards = draft.CardIds.ToArray();
                await anki.VerifyAsync(draft, ct);
                var matches = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id }, ct);
                var notes = await anki.CallAsync("notesInfo", new { notes = new[] { draft.NoteId!.Value } }, ct);
                var cards = await anki.CallAsync("cardsInfo", new { cards = draft.CardIds }, ct);
                WorkflowJson.Save(evidence, new { matches, notes, cards });
                Check(matches.GetArrayLength() == 1 && matches[0].GetInt64() == oldNote && draft.NoteId == oldNote && draft.CardIds.SequenceEqual(oldCards),
                    "language: independent Anki readback returns the exact unique note and original card IDs");
                Check(cards.GetArrayLength() == 1 && cards[0].GetProperty("ord").GetInt32() == 0 &&
                    cards[0].GetProperty("deckName").GetString()!.StartsWith(manifest.Settings.AnkiRootDeck + "::", StringComparison.Ordinal),
                    "language: the sole reading card is in this run's isolated Anki deck");
                identity = new(draft.Id, ExportDestination.Anki, "kc_" + draft.Id, "", draft.ConfirmedNote!.Deck,
                    [draft.NoteId!.Value.ToString()], draft.CardIds.Select(value => value.ToString()).ToArray(), null);
            }
            else
            {
                Check(ExportRouting.For(draft) == ExportDestination.RemNote && draft.ConfirmedRemNote != null && draft.RemNoteResult != null,
                    key + ": the confirmed target is RemNote and its original SDK identity is retained");
                var plan = draft.ConfirmedRemNote!; var previous = draft.RemNoteResult!;
                using var request = new HttpRequestMessage(HttpMethod.Post, RemNoteConnection.ResolveEndpoint(draft.Settings.RemNoteSdkEndpoint).TrimEnd('/') + "/sdk/knowledge-clipper/readback");
                request.Headers.Add("X-KnowledgeClipper-Protocol", "1"); request.Content = JsonContent.Create(plan, options: Wire);
                using var response = await http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct); response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(body); var raw = document.RootElement.Clone();
                WorkflowJson.Save(evidence, raw);
                var verified = RemNoteConnection.Validate(plan, raw, previous);
                Check(verified.RemIds.SequenceEqual(previous.RemIds) && verified.CardIds.SequenceEqual(previous.CardIds), key + ": forced SDK readback retains every original Rem and review-card ID");
                Check(raw.TryGetProperty("batchSeparator", out var separator), key + ": new batch has a verified empty sibling separator");
                var expectedDirectory = key switch { "question" => new[] { "问答" }, "excerpt" => new[] { "摘录" }, _ => new[] { "图片", "原图" } };
                Check(plan.DocumentPath.SequenceEqual(expectedDirectory), key + ": content was routed to the expected mode document");
                var card = raw.GetProperty("cards").EnumerateArray().Single();
                var nodes = card.GetProperty("nodes").EnumerateArray().ToArray();
                Check(nodes.All(node => !node.GetProperty("isDocument").GetBoolean() && node.GetProperty("remType").GetInt32() == 0) &&
                    nodes[0].GetProperty("parentId").GetString() == verified.DocumentRemId,
                    key + ": the imported content uses ordinary text Rems directly below its mode document");
                if (key == "question")
                {
                    Check(plan.Cards.Length == 1 && plan.Cards[0].Kind == "basic" && verified.CardIds.Length == 1 && nodes.Length >= 2 &&
                        nodes[0].GetProperty("cardTypes").EnumerateArray().Single().GetString() == "forward" &&
                        nodes.Skip(1).All(node => node.GetProperty("isCardItem").GetBoolean()),
                        "question: one native forward multiline card stores the single generated answer");
                    Check(LiteralText(nodes[0]) == manifest.Fixtures["question"].Material.SelectedText, "question: card front keeps the original question");
                }
                else
                {
                    Check(plan.Cards.Length == 1 && plan.Cards[0].Kind == "plain" && plan.Cards[0].Back.Length == 0 && nodes.Length == 1 && verified.CardIds.Length == 0 &&
                        !nodes[0].GetProperty("practiceEnabled").GetBoolean() && nodes[0].GetProperty("children").GetArrayLength() == 0,
                        key + ": empty-back content is a plain note with zero review cards or answer children");
                    if (key == "excerpt")
                        Check(LiteralText(nodes[0]) == manifest.Fixtures["excerpt"].Material.SelectedText && nodes[0].GetProperty("media").GetArrayLength() == 0,
                            "excerpt: independently read raw text equals the untouched public input");
                    else
                    {
                        var media = nodes[0].GetProperty("media").EnumerateArray().ToArray();
                        Check(LiteralText(nodes[0]) == "" && media.Length == 1 && media[0].GetProperty("sha256").GetString() == manifest.ImageSha256,
                            "image: one RemNote-hosted image has the original public PNG byte hash and no generated text");
                    }
                }
                identity = new(draft.Id, ExportDestination.RemNote, plan.SourceId, verified.RootRemId, verified.DocumentRemId,
                    verified.RemIds, verified.CardIds, separator.GetProperty("remId").GetString());
            }
            if (manifest.Identities.TryGetValue(key, out var original))
                Check(JsonSerializer.Serialize(identity, WorkflowJson.Options) == JsonSerializer.Serialize(original, WorkflowJson.Options),
                    key + ": cold/repeated verification preserves the originally recorded identity and separator");
            else manifest.Identities.Add(key, identity);
            Save();
        }
        catch (Exception ex)
        {
            draft.ImportState = ImportState.Unknown;
            draft.ImportError = "Independent live readback failed: " + SafeError(ex);
            store.Save(draft); throw;
        }
    }

    private static string LiteralText(JsonElement node) => string.Concat(node.GetProperty("text").EnumerateArray().Select(part =>
        part.ValueKind == JsonValueKind.String ? part.GetString() :
        part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : ""));
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;

internal static class IntegrationExports
{
    internal static async Task Run(string repo, string output)
    {
        var data = Path.Combine(output, "integration-live", "api"); var modelStore = new DraftStore(data);
        var sourceDraft = modelStore.Find(File.ReadAllText(Path.Combine(data, "draft-id.txt")))!;
        var initialStage = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "stage-2.json"))).RootElement.GetProperty("cardId").GetString();
        var first = sourceDraft.Chat!.Compositions.FindIndex(c => c.Id == initialStage);
        var originals = sourceDraft.Chat.Compositions.Skip(first).Take(2).ToArray();
        if (first < 0 || originals.Length != 2 || originals.Any(c => string.IsNullOrWhiteSpace(c.Front) || string.IsNullOrWhiteSpace(c.Back)))
            throw new InvalidDataException("Missing actual model-generated multi-card input.");
        var store = new DraftStore(Path.Combine(data, "export-fixture")); var exportIdFile = Path.Combine(data, "export-draft-id.txt"); ClipDraft draft;
        if (File.Exists(exportIdFile)) draft = store.Find(File.ReadAllText(exportIdFile))!;
        else
        {
            var workspace = JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(sourceDraft.Chat, WorkflowJson.Options), WorkflowJson.Options)!;
            workspace.Compositions = originals.Select(c => new CardComposition { Front = c.Front, Back = c.Back, TypeId = c.TypeId,
                FrontAnnotation = c.FrontAnnotation, Annotation = c.Annotation, FrontAnnotationEnabled = c.FrontAnnotationEnabled, AnnotationEnabled = c.AnnotationEnabled,
                FrontSources = [.. c.FrontSources], BackSources = [.. c.BackSources], ManuallyEdited = true }).ToList();
            draft = new() { Original = sourceDraft.Original, Material = sourceDraft.Material, Configuration = sourceDraft.Configuration, ModeId = "question", Chat = workspace,
                Settings = sourceDraft.Settings with { AnkiRootDeck = "KnowledgeClipper 验收::整合多卡 20261016", RemNoteRootTitle = "零碎知识剪藏" } };
            store.Save(draft); File.WriteAllText(exportIdFile, draft.Id);
        }
        var sourceCards = draft.Chat!.Compositions.Take(2).ToArray();
        if (!sourceCards.Select(c => (c.Front, c.Back)).SequenceEqual(originals.Select(c => (c.Front, c.Back))))
            throw new InvalidDataException("Export fixture changed the actual model-generated bodies.");
        var config = new ChatConfigurationLoader(Path.Combine(repo, "modes", "chat"), Path.Combine(data, "modes"));
        var receipts = new List<object>();
        foreach (var destination in new[] { ExportDestination.Anki, ExportDestination.RemNote })
        {
            var resultRoot = Path.Combine(data, "exports", destination.ToString()); Directory.CreateDirectory(resultRoot);
            var idFile = Path.Combine(resultRoot, "card-ids.json"); string[] ids;
            if (File.Exists(idFile)) ids = WorkflowJson.Read<string[]>(idFile);
            else
            {
                // The normal "edit as new draft" action makes independent frozen
                // destinations. Never repoint or overwrite the first export.
                ids = sourceCards.Select(card => card.ExportDraftId == null ? card.Id : ChatComposition.Editable(draft.Chat, card).Id).ToArray();
                store.Save(draft); WorkflowJson.Save(idFile, ids);
            }
            var lostReceipt = Path.Combine(resultRoot, "lost-provider-receipt.json");
            using var trace = new ResponseLoss(Path.Combine(resultRoot, "calls.jsonl"), lostReceipt) { InnerHandler = new HttpClientHandler(), Drop = !File.Exists(lostReceipt) };
            using var http = new HttpClient(trace) { Timeout = TimeSpan.FromMinutes(2) }; var importer = new CardImportRouter(http);
            foreach (var id in ids)
            {
                var card = draft.Chat.Compositions.Single(c => c.Id == id);
                try { await ChatComposition.ExportAsync(draft, card, store, config, importer, destination); }
                catch (IOException) when (trace.Dropped) { Console.WriteLine("PASS " + destination + " retained frozen multi-card identity after response loss"); }
                var child = store.Find(card.ExportDraftId!)!;
                if (child.ImportState != ImportState.Verified)
                {
                    var before = trace.Actions.Count;
                    await ChatComposition.ExportAsync(draft, card, store, config, importer, destination);
                    if (trace.Actions.Skip(before).Any(a => a is "export" or "addNote")) throw new InvalidDataException("Recovery repeated a write.");
                    child = store.Find(card.ExportDraftId!)!;
                }
                if (child.ImportState != ImportState.Verified) throw new InvalidDataException("Multi-card export did not verify.");
                var beforeDuplicate = trace.Actions.Count; await ChatComposition.ExportAsync(draft, card, store, config, importer, destination);
                if (beforeDuplicate != trace.Actions.Count) throw new InvalidDataException("Verified multi-card export called the provider again.");
                object readback;
                if (destination == ExportDestination.Anki)
                {
                    var anki = new AnkiConnection(http, draft.Settings.AnkiEndpoint); await anki.VerifyAsync(child);
                    var notes = await anki.CallAsync("notesInfo", new { notes = new[] { child.NoteId!.Value } });
                    foreach (var field in child.ConfirmedNote!.Fields)
                        if (notes[0].GetProperty("fields").GetProperty(field.Key).GetProperty("value").GetString() != field.Value)
                            throw new InvalidDataException("Actual Anki field differs: " + field.Key);
                    readback = new { notes, cards = await anki.CallAsync("cardsInfo", new { cards = child.CardIds }) };
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, RemNoteConnection.ResolveEndpoint(draft.Settings.RemNoteSdkEndpoint).TrimEnd('/') + "/sdk/knowledge-clipper/readback");
                    request.Headers.Add("X-KnowledgeClipper-Protocol", "1");
                    request.Content = JsonContent.Create(child.ConfirmedRemNote, options: new JsonSerializerOptions(WorkflowJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
                    using var response = await http.SendAsync(request); var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new InvalidDataException("Readback rejected: " + body);
                    var actual = JsonDocument.Parse(body).RootElement.Clone(); RemNoteConnection.Validate(child.ConfirmedRemNote!, actual, child.RemNoteResult); readback = actual;
                }
                WorkflowJson.Save(Path.Combine(resultRoot, id + ".json"), new { status = "Verified", id, childId = child.Id, child.NoteId, child.CardIds, remIds = child.RemNoteResult?.RemIds, readback });
                receipts.Add(new { destination = destination.ToString(), id, child.NoteId, child.CardIds, remIds = child.RemNoteResult?.RemIds, status = "Verified" });
            }
            Console.WriteLine("PASS actual " + destination + " two independently generated cards, frozen routing, readback, duplicate confirmation");
        }
        WorkflowJson.Save(Path.Combine(data, "exports", "acceptance.json"), new { status = "PASS", cards = receipts, perDestination = 2,
            originalModelProposal = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "stage-2.json"))).RootElement.Clone(), duplicateProviderCalls = 0 });
    }

    private sealed class ResponseLoss(string log, string receipt) : DelegatingHandler
    {
        internal bool Drop, Dropped; internal readonly List<string> Actions = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var action = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (request.Content != null)
            {
                using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("action", out var anki)) action = anki.GetString()!;
            }
            Actions.Add(action); File.AppendAllText(log, JsonSerializer.Serialize(new { action, utc = DateTimeOffset.UtcNow }) + "\n");
            var response = await base.SendAsync(request, ct);
            if (Drop && action is "addNote" or "export" && response.IsSuccessStatusCode)
            {
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (result.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) return response;
                WorkflowJson.Save(receipt, result.RootElement); Drop = false; Dropped = true; response.Dispose();
                throw new IOException("Controlled response loss after actual successful multi-card write.");
            }
            return response;
        }
    }
}

using System.Security.Cryptography;
using KnowledgeClipper.Workflow;

// Explicitly requested one-record recovery; does not discover or retry old failures.
if (args.Length != 4) throw new ArgumentException("RecoverImport <dataRoot> <childId> <repository> <evidenceDirectory>");
var root = Path.GetFullPath(args[0]); var store = new DraftStore(root);
var child = store.Find(args[1]) ?? throw new InvalidDataException("Original draft not found.");
if (child.ModeParentId == null || child.ConfirmedNote != null || child.NoteId != null || child.Content != null)
    throw new InvalidDataException("Recovery only accepts an untouched failed mode draft.");
var parent = store.Find(child.ModeParentId) ?? throw new InvalidDataException("Parent not found.");
if (parent.ModeCards.GetValueOrDefault(child.ModeId!) != child.Id) throw new InvalidDataException("Mode binding mismatch.");
var evidence = Path.GetFullPath(args[3]); Directory.CreateDirectory(evidence);
var initial = File.ReadAllBytes(store.Identity(child.Id));
File.WriteAllBytes(Path.Combine(evidence, "original-draft.json"), initial);
WorkflowJson.Save(Path.Combine(evidence, "original-parent.json"), parent);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
child.Configuration = ModeConfigurationLoader.Load(Path.Combine(args[2], "modes"), Path.Combine(root, "modes"));
var result = await new ContentModule(http, () => ContentCredentials.Resolve()).ProcessAsync(child.Material, child.ModeId!, child.Configuration, child.Settings, default);
if (result.Fields?.IsUnresolved != false) throw new InvalidDataException("Content is unresolved.");
if (!SHA256.HashData(initial).SequenceEqual(SHA256.HashData(File.ReadAllBytes(store.Identity(child.Id)))))
    throw new InvalidDataException("Draft changed while generating; no overwrite performed.");
child.Content = result; child.ContentError = null; child.ImportError = null;
child.Times["previewReady"] = DateTimeOffset.UtcNow; store.Save(child);
WorkflowJson.Save(Path.Combine(evidence, "generated-result.json"), result);
using (var offline = new HttpClient(new Offline()))
    await LanguageModeCards.ImportAsync(parent, [child.ModeId!], store, new CardImportRouter(offline));
child = store.Find(child.Id)!;
if (!child.AwaitingAnki || child.NoteId != null) throw new InvalidDataException("Offline persistence failed.");
WorkflowJson.Save(Path.Combine(evidence, "queued-draft.json"), child);
using (var queue = new AnkiPendingQueue(root, http))
{
    await queue.Ready;
    if (queue.Count != 1) throw new InvalidDataException("Unexpected queued records; stop before automatic import.");
    await queue.PumpAsync();
}
child = store.Find(child.Id)!;
if (child.ImportState != ImportState.Verified || child.AwaitingAnki) throw new InvalidDataException("Real Anki import not verified.");
await new AnkiConnection(http, child.Settings.AnkiEndpoint).VerifyAsync(child);
parent = store.Find(parent.Id)!; parent.ImportError = null; parent.ContentError = null; store.Save(parent);
WorkflowJson.Save(Path.Combine(evidence, "receipt.json"), new { passed = true, child.Id, child.NoteId, child.CardIds,
    child.ImportState, originalMaterialRetained = child.Original == WorkflowJson.Read<ClipDraft>(Path.Combine(evidence, "original-draft.json")).Original,
    queuedAcrossNewQueueInstance = true, boundary = "Transport refusal simulated without stopping user's Anki; resumed import and independent verification use real AnkiConnect." });
Console.WriteLine("PASS original draft regenerated, durably queued, automatically imported and independently verified.");

sealed class Offline : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException("Controlled transport refusal before any Anki request.");
}

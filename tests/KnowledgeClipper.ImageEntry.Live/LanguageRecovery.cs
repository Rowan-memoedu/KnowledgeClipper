using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static string DraftPath(string id) => Path.Combine(DataRoot, "language-drafts", id + ".json");
    private static string FileHash(string path) => ImageAsset.Hash(File.ReadAllBytes(path));

    private static ClipDraft RecoveryDraft(ImageMaterialJob job, CaptureMaterial material)
    {
        if (manifest.LanguageRecovery == null)
        {
            var source = Drafts.Find(manifest.LanguageDraftId!) ?? throw new InvalidDataException("The original failed language draft must remain available.");
            var failed = LanguageModeCards.Children(source, Drafts).Single();
            var ocr = job.Ocr.Single();
            Check(source.Material.SelectedText != material.SelectedText && source.Material.FrontImage == material.FrontImage &&
                failed.ImportState == ImportState.Verified && failed.ConfirmedNote != null && failed.NoteId != null &&
                failed.CardIds.Length == 1 && failed.ConfirmedNote.Deck.StartsWith(Deck + "::", StringComparison.Ordinal),
                "recovery is bounded to the already exported failed public-image language sample");
            Check(ocr.RawManifest != null && ocr.RawText != null && ocr.Cleanup != null && ocr.Review?.Verdict == "pass",
                "recovery reuses the already completed OCR and cleanup without resubmission");
            var recoveryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("ImageEntry.Live.OcrLanguageRecovery:" + source.Id + ":" + job.Id)).AsSpan(0, 16)).ToString("N");
            manifest.LanguageRecovery = new()
            {
                SourceParentId = source.Id, SourceChildId = failed.Id, ParentId = recoveryId, JobId = job.Id,
                ImageSha256 = material.FrontImage!.Sha256, RawTextSha256 = ocr.RawSha256!,
                RawManifest = ocr.RawManifest!, RawManifestSha256 = FileHash(ocr.RawManifest!),
                SourceParentSha256 = FileHash(DraftPath(source.Id)), SourceChildSha256 = FileHash(DraftPath(failed.Id)),
                SourceNoteId = failed.NoteId ?? throw new InvalidDataException("The failed sample has no saved note identity."), SourceCardIds = failed.CardIds
            };
            Save();
        }
        var recovery = manifest.LanguageRecovery;
        CheckRecoveryPreserved();
        var parent = Drafts.Find(recovery.ParentId);
        if (parent == null)
        {
            parent = NewDraft(material, "language"); parent.Id = recovery.ParentId;
            parent.Times["reprocessedOriginalImage"] = DateTimeOffset.UtcNow;
            Drafts.Save(parent);
        }
        Check(recovery.JobId == job.Id && parent.Material == material && parent.Original == material &&
            parent.Settings.AnkiRootDeck == Deck && parent.ImportState == ImportState.Unconfirmed,
            "the recovery parent retains one fixed identity and the newly adapted original image material");
        manifest.LanguageDraftId = parent.Id; Save(); return parent;
    }

    private static void CheckRecoveryPreserved()
    {
        CheckQuestionRecoveryPreserved();
        if (manifest.LanguageRecovery is not { } recovery) return;
        var job = Images.Find(recovery.JobId); var ocr = job.Ocr.Single();
        Check(FileHash(DraftPath(recovery.SourceParentId)) == recovery.SourceParentSha256 &&
            FileHash(DraftPath(recovery.SourceChildId)) == recovery.SourceChildSha256 &&
            job.Questions.Single().Sha256 == recovery.ImageSha256 && ocr.RawSha256 == recovery.RawTextSha256 &&
            ocr.RawManifest == recovery.RawManifest && FileHash(recovery.RawManifest) == recovery.RawManifestSha256,
            "the failed draft files, original PNG and MinerU evidence remain unchanged");
    }

    private static async Task QuarantineLanguageFailureAsync(HttpClient http, CancellationToken ct)
    {
        var recovery = manifest.LanguageRecovery ?? throw new InvalidDataException("No recorded recovery branch exists.");
        var correct = LanguageChildren().Single();
        Check(correct.ImportState == ImportState.Verified && correct.Id != recovery.SourceChildId,
            "the replacement language card is fully imported before handling the failed sample");
        await VerifyAsync(correct, http, ct);
        CheckRecoveryPreserved();
        var source = Drafts.Find(recovery.SourceChildId)!;
        var confirmed = source.ConfirmedNote!;
        var anki = new AnkiConnection(http, source.Settings.AnkiEndpoint);
        var matches = await anki.CallAsync("findNotes", new { query = "tag:kc_" + source.Id }, ct);
        var notes = await anki.CallAsync("notesInfo", new { notes = new[] { recovery.SourceNoteId } }, ct);
        var cards = await anki.CallAsync("cardsInfo", new { cards = recovery.SourceCardIds }, ct);
        var quarantineDeck = Deck + "::失败样本";
        Check(matches.GetArrayLength() == 1 && matches[0].GetInt64() == recovery.SourceNoteId && notes.GetArrayLength() == 1 &&
            notes[0].GetProperty("noteId").GetInt64() == recovery.SourceNoteId && notes[0].GetProperty("modelName").GetString() == confirmed.Model &&
            notes[0].GetProperty("tags").EnumerateArray().Any(tag => tag.GetString() == "kc_" + source.Id) &&
            notes[0].GetProperty("cards").EnumerateArray().Select(card => card.GetInt64()).Order().SequenceEqual(recovery.SourceCardIds.Order()) &&
            confirmed.Fields.All(field => notes[0].GetProperty("fields").GetProperty(field.Key).GetProperty("value").GetString() == field.Value),
            "the exact failed note still has its fixture tag, model, card binding and unchanged generated fields");
        Check(cards.GetArrayLength() == 1 && cards[0].GetProperty("cardId").GetInt64() == recovery.SourceCardIds.Single() &&
            cards[0].GetProperty("note").GetInt64() == recovery.SourceNoteId && cards[0].GetProperty("ord").GetInt32() == 0 &&
            cards[0].GetProperty("reps").GetInt32() == 0 && cards[0].GetProperty("lapses").GetInt32() == 0 &&
            new[] { confirmed.Deck, quarantineDeck }.Contains(cards[0].GetProperty("deckName").GetString()),
            "only the exact newly created, unreviewed failed test card is eligible for quarantine");
        var media = confirmed.Media.Single();
        var encoded = await anki.CallAsync("retrieveMediaFile", new { filename = media.FileName }, ct);
        Check(ImageAsset.Hash(Convert.FromBase64String(encoded.GetString()!)) == recovery.ImageSha256,
            "the failed note still references the original public PNG bytes");
        var backup = Path.Combine(DataRoot, "failed-language-sample", recovery.SourceChildId);
        Directory.CreateDirectory(backup);
        var before = Path.Combine(backup, "before.json");
        if (!File.Exists(before)) WorkflowJson.Save(before, new { note = notes, card = cards, media,
            actualImageBase64 = encoded.GetString(), sourceDraftSha256 = recovery.SourceChildSha256, capturedAt = DateTimeOffset.UtcNow });
        else
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(before));
            Check(saved.RootElement.GetProperty("note")[0].GetProperty("noteId").GetInt64() == recovery.SourceNoteId &&
                saved.RootElement.GetProperty("sourceDraftSha256").GetString() == recovery.SourceChildSha256,
                "an earlier full backup belongs to the same failed sample and is preserved");
        }
        await anki.CallAsync("createDeck", new { deck = quarantineDeck }, ct);
        await anki.CallAsync("changeDeck", new { cards = recovery.SourceCardIds, deck = quarantineDeck }, ct);
        await anki.CallAsync("suspend", new { cards = recovery.SourceCardIds }, ct);
        var afterNotes = await anki.CallAsync("notesInfo", new { notes = new[] { recovery.SourceNoteId } }, ct);
        var afterCards = await anki.CallAsync("cardsInfo", new { cards = recovery.SourceCardIds }, ct);
        var suspended = await anki.CallAsync("areSuspended", new { cards = recovery.SourceCardIds }, ct);
        WorkflowJson.Save(Path.Combine(backup, "after-attempt-" + attempt.Number.ToString("D3") + ".json"), new { note = afterNotes, card = afterCards, suspended });
        Check(afterNotes.GetArrayLength() == 1 && afterCards.GetArrayLength() == 1 &&
            afterNotes[0].GetProperty("noteId").GetInt64() == recovery.SourceNoteId &&
            afterCards[0].GetProperty("cardId").GetInt64() == recovery.SourceCardIds.Single() &&
            afterCards[0].GetProperty("deckName").GetString() == quarantineDeck && suspended.GetArrayLength() == 1 && suspended[0].GetBoolean() &&
            confirmed.Fields.All(field => afterNotes[0].GetProperty("fields").GetProperty(field.Key).GetProperty("value").GetString() == field.Value),
            "the failed sample keeps its note/card IDs and card faces in the failed-samples deck, suspended from review");
        recovery.QuarantinedAt ??= DateTimeOffset.UtcNow; recovery.BackupPath = before; Save();
    }
}

internal sealed class LanguageRecovery
{
    public required string SourceParentId { get; init; }
    public required string SourceChildId { get; init; }
    public required string ParentId { get; init; }
    public required string JobId { get; init; }
    public required string ImageSha256 { get; init; }
    public required string RawTextSha256 { get; init; }
    public required string RawManifest { get; init; }
    public required string RawManifestSha256 { get; init; }
    public required string SourceParentSha256 { get; init; }
    public required string SourceChildSha256 { get; init; }
    public long SourceNoteId { get; init; }
    public required long[] SourceCardIds { get; init; }
    public DateTimeOffset? QuarantinedAt { get; set; }
    public string? BackupPath { get; set; }
}

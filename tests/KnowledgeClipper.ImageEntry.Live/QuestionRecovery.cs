using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static string ContentHash(ClipDraft draft) => ImageAsset.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        draft.Original, draft.Material, draft.Input, draft.Configuration, draft.Content, draft.Conversation
    }, WorkflowJson.Options)));

    private static ClipDraft RecoveryQuestionDraft(ImageMaterialJob job)
    {
        if (manifest.QuestionRecovery == null)
        {
            var source = Drafts.Find(manifest.QuestionDraftId!) ?? throw new InvalidDataException("Keep the original question content and invalid test plans.");
            var children = QuestionChildren();
            Check(source.Conversation.Count == 2 && source.Conversation.All(turn => turn.Answer.Answered) && source.EditedAnswers.Count == 0 &&
                source.Content?.Answer?.Answered == true && children.Length == 2 && children.All(child => child.ImportState == ImportState.Ready &&
                    child.RemNoteResult == null && child.ConfirmedRemNote?.RootTitle == RootTitle && child.Original.FrontImage?.Sha256 == job.Questions.Single().Sha256),
                "question-route recovery retains the two completed answers and the never-exported invalid-root plans");
            var recoveryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("ImageEntry.Live.CanonicalRemNoteRoot:" + source.Id)).AsSpan(0, 16)).ToString("N");
            manifest.QuestionRecovery = new()
            {
                SourceParentId = source.Id, ParentId = recoveryId, JobId = job.Id, ImageSha256 = job.Questions.Single().Sha256,
                SourceParentSha256 = FileHash(DraftPath(source.Id)), ContentSha256 = ContentHash(source),
                SourceChildrenSha256 = children.ToDictionary(child => child.Id, child => FileHash(DraftPath(child.Id)))
            };
            Save();
        }
        var recovery = manifest.QuestionRecovery;
        CheckQuestionRecoveryPreserved();
        var original = Drafts.Find(recovery.SourceParentId)!;
        var parent = Drafts.Find(recovery.ParentId);
        if (parent == null)
        {
            // Reuse the actual generated content verbatim. Only the incorrect test
            // destination is reset through the normal new-draft/export pipeline.
            parent = new ClipDraft
            {
                Id = recovery.ParentId, Original = original.Original, Material = original.Material, Input = original.Input,
                Configuration = original.Configuration, Settings = original.Settings with { RemNoteRootTitle = "零碎知识剪藏", RemNoteRootId = null },
                ModeId = original.ModeId, Route = original.Route, Practice = original.Practice,
                Content = original.Content, Conversation = original.Conversation.ToList()
            };
            parent.Times["correctedTestDestination"] = DateTimeOffset.UtcNow; Drafts.Save(parent);
        }
        Check(recovery.JobId == job.Id && ContentHash(parent) == recovery.ContentSha256 && parent.Settings.RemNoteRootTitle == "零碎知识剪藏" &&
            parent.Settings.RemNoteRootId == null && parent.ParentDraftId == null,
            "the fixed recovery identity preserves every model-generated field and uses the formal default RemNote root");
        manifest.QuestionDraftId = parent.Id; Save(); return parent;
    }

    private static void CheckQuestionRecoveryPreserved()
    {
        if (manifest.QuestionRecovery is not { } recovery) return;
        var source = Drafts.Find(recovery.SourceParentId)!;
        Check(FileHash(DraftPath(recovery.SourceParentId)) == recovery.SourceParentSha256 && ContentHash(source) == recovery.ContentSha256 &&
            recovery.SourceChildrenSha256.All(pair => FileHash(DraftPath(pair.Key)) == pair.Value) &&
            Images.Find(recovery.JobId).Questions.Single().Sha256 == recovery.ImageSha256,
            "the invalid-root plans, original image and every real generated question/answer remain unchanged");
    }
}

internal sealed class QuestionRecovery
{
    public required string SourceParentId { get; init; }
    public required string ParentId { get; init; }
    public required string JobId { get; init; }
    public required string ImageSha256 { get; init; }
    public required string SourceParentSha256 { get; init; }
    public required string ContentSha256 { get; init; }
    public required Dictionary<string, string> SourceChildrenSha256 { get; init; }
}

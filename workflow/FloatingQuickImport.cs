namespace KnowledgeClipper.Workflow;

// Floating shortcuts use the same saved drafts, confirmation and verified import
// paths as the preview. A partial/unknown result must return to that preview.
public static class FloatingQuickImport
{
    public static async Task LanguageOrQuestionAsync(ClipDraft draft, DraftStore store, ICardImporter importer,
        Action? changed = null, CancellationToken ct = default)
    {
        if (draft.ContentError != null) throw new InvalidOperationException(draft.ContentError);
        if (draft.ModeCards.Count > 0)
        {
            var children = LanguageModeCards.Children(draft, store);
            if (children.Length == 0) throw new InvalidOperationException("默认语言模式尚未就绪。");
            foreach (var child in children) RequireReady(child);
            await LanguageModeCards.ImportAsync(draft, children.Select(c => c.ModeId!).ToArray(), store, importer, changed, ct);
            foreach (var child in LanguageModeCards.Children(draft, store)) RequireVerified(child);
        }
        else if (draft.Input.Intent == "question")
        {
            if (draft.Conversation.Count != 1) throw new InvalidOperationException("快捷提问需要一轮完整回答。");
            await QuestionCards.ImportAsync(draft, [0], store, importer, changed ?? (() => { }), ct);
            var card = QuestionCards.Preview(draft, store).Single();
            if (draft.FollowUpCards.TryGetValue(0, out var id) && store.Find(id)?.AwaitingAnki == true) throw new AnkiPendingException();
            if (card.State != ImportState.Verified) throw new InvalidOperationException("回答导入尚未核验，请在预览中继续。");
        }
        else
        {
            RequireReady(draft);
            ExportRouting.Confirm(draft, ExportRouting.BuildNote(draft, importer.FixedDestination), importer.FixedDestination);
            store.Save(draft);
            await importer.ImportAsync(draft, store, ct);
            RequireVerified(draft);
        }
    }

    public static async Task ExcerptAsync(ExcerptSession session, ExcerptStore store, ICardImporter importer,
        CancellationToken ct = default)
    {
        var ids = session.Cards.Where(c => c.SupersededBy == null).Select(c => c.Id).ToArray();
        if (ids.Length != 1) throw new InvalidOperationException("快捷摘录只导入一份原始材料。");
        await ExcerptCards.ImportAsync(session, store, importer, ids, ct);
        RequireVerified(ExcerptCards.Get(session, store, ids[0]));
    }

    private static void RequireReady(ClipDraft draft)
    {
        if (draft.ContentError != null) throw new InvalidOperationException(draft.ContentError);
        var content = draft.PreviewContent;
        if (content == null || content.Fields?.IsUnresolved == true || content.Fields == null && content.Basic == null)
            throw new InvalidOperationException("默认模式内容需要确认，请在预览中继续。");
    }

    private static void RequireVerified(ClipDraft draft)
    {
        if (draft.AwaitingAnki) throw new AnkiPendingException();
        if (draft.ImportState != ImportState.Verified)
            throw new InvalidOperationException(draft.ImportError ?? "导入结果尚未核验，请在预览中继续。");
    }
}

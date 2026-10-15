using System.Text.Json;

namespace KnowledgeClipper.Workflow;

// History joins the existing stores; records and card identities remain owned by their original flows.
public sealed record HistoryRecord(ClipDraft? Draft = null, ExcerptSession? Excerpt = null)
{
    public string Id => Draft?.Id ?? Excerpt!.Id;
    public string Key => (Draft == null ? "excerpt." : "draft.") + Id;
    public string Kind => Draft == null ? "摘录" : HistoryRecords.Kind(Draft);
    public bool CanPlay => Draft != null && HistoryRecords.IsLanguage(Draft);
    public CaptureMaterial Original => Draft?.Original ?? Excerpt!.Original;
}

public static class HistoryRecords
{
    // Export drafts own provider identities; their conversation owns the history row.
    // A genuine new conversation on a former card remains an accessible branch.
    internal static bool IsChatExportChild(ClipDraft child, ClipDraft? parent)
    {
        if (parent?.Chat == null || child.Id == parent.Id || child.ParentDraftId != parent.Id ||
            !parent.Chat.Compositions.Any(c => c.ExportDraftId == child.Id || c.ExportDraftId == null && c.Id == child.Id)) return false;
        if (child.PendingFollowUp != null || child.ConversationError != null || child.ConversationImportError != null || child.Conversation.Count > 0) return false;
        var chat = child.Chat;
        if (chat == null) return true;
        return chat.Prompts.Count == 0 && chat.Binding.ThreadId == null && chat.Messages.Count <= 2 &&
            string.IsNullOrWhiteSpace(chat.Input) && chat.Attachments.Count == 0 &&
            (chat.Auxiliary == null || chat.Auxiliary.Sessions.Values.All(s => !s.Binding.NeedsReconciliation));
    }

    public static ClipDraft ResolveChatSession(ClipDraft draft, DraftStore store) => ResolveChatSession(draft, store.Find);

    internal static ClipDraft ResolveChatSession(ClipDraft draft, Func<string, ClipDraft?> find)
    {
        var original = draft;
        var seen = new HashSet<string>(StringComparer.Ordinal) { draft.Id };
        while (draft.ParentDraftId is { } parentId)
        {
            var parent = find(parentId);
            if (!IsChatExportChild(draft, parent)) break;
            if (!seen.Add(parent!.Id)) return original; // Corrupt cycles never select an arbitrary owner.
            draft = parent;
        }
        return draft;
    }

    public static void RestoreChatCards(ClipDraft draft, DraftStore store) => RestoreChatCards(draft, store.Find);

    internal static void RestoreChatCards(ClipDraft draft, Func<string, ClipDraft?> find)
    {
        if (draft.Chat == null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal) { draft.Id };
        var identities = new HashSet<string>(draft.Chat.Compositions.Select(c => c.ExportDraftId ?? c.Id), StringComparer.Ordinal);
        var cardIds = new HashSet<string>(draft.Chat.Compositions.Select(c => c.Id), StringComparer.Ordinal);
        void Visit(ClipDraft owner)
        {
            foreach (var link in owner.Chat?.Compositions.ToArray() ?? [])
            {
                var id = link.ExportDraftId ?? link.Id;
                if (seen.Contains(id)) continue;
                var child = find(id);
                if (child == null || !IsChatExportChild(child, owner) || ResolveChatSession(child, find).Id == child.Id) continue;
                seen.Add(id);
                if (link.ExportDraftId == null && ExportRouting.Frozen(child)) link.ExportDraftId = child.Id;
                if (child.Chat != null)
                {
                    foreach (var card in child.Chat.Compositions)
                    {
                        var identity = card.ExportDraftId ?? card.Id;
                        if (identities.Contains(identity) || cardIds.Contains(card.Id)) continue;
                        // Read projections must not mutate the independently persisted child.
                        var copy = System.Text.Json.JsonSerializer.SerializeToElement(card, WorkflowJson.Options)
                            .Deserialize<CardComposition>(WorkflowJson.Options)!;
                        draft.Chat.Compositions.Add(copy); identities.Add(identity); cardIds.Add(copy.Id);
                    }
                    foreach (var image in child.Chat.Assets.Concat(child.Chat.Attachments))
                        if (draft.Chat.Assets.All(a => a.Sha256 != image.Sha256)) draft.Chat.Assets.Add(image);
                    Visit(child);
                }
            }
        }
        Visit(draft);
    }

    private static bool HasComposition(CardComposition card) => card.ExportDraftId != null ||
        !string.IsNullOrWhiteSpace(card.Front) || !string.IsNullOrWhiteSpace(card.Back) ||
        !string.IsNullOrWhiteSpace(card.Annotation) || !string.IsNullOrWhiteSpace(card.FrontAnnotation);

    public static string ChatStatus(ClipDraft draft, DraftStore store)
    {
        var cards = draft.Chat!.Compositions.Where(HasComposition).ToArray();
        var children = cards.Select(c => c.ExportDraftId == null ? null : c.ExportDraftId == draft.Id ? draft : store.Find(c.ExportDraftId)).ToArray();
        var rounds = draft.Chat.Messages.Count(m => m.Role == "user");
        return $"问答 {rounds} 轮 · 共 {cards.Length} 张 · 已导入 {children.Count(c => c?.ImportState == ImportState.Verified)} 张" +
            (children.Any(c => c?.AwaitingAnki == true) ? " · 等待 Anki 打开后自动导入" : "") +
            (children.Any(c => c?.ImportState == ImportState.Unknown || c?.ImportError != null) ? " · 部分卡片待核对" : "");
    }

    public static string ActionText(HistoryRecord record, DraftStore drafts, ExcerptStore excerpts)
    {
        try
        {
            var states = CardStates(record, drafts, excerpts).ToArray();
            return states.Length > 0 && states.All(state => state == ImportState.Verified) ? "查看" : "继续制卡";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        { return "继续制卡"; }
    }
    private static IEnumerable<ImportState> CardStates(HistoryRecord record, DraftStore drafts, ExcerptStore excerpts)
    {
        if (record.Excerpt is { } excerpt)
        {
            foreach (var link in excerpt.Cards.Where(card => card.SupersededBy == null))
                yield return excerpts.Cards.Find(link.Id)?.ImportState ?? ImportState.Unknown;
            yield break;
        }
        var draft = record.Draft!;
        if (draft.Chat != null)
        {
            foreach (var card in draft.Chat.Compositions.Where(HasComposition))
                yield return card.ExportDraftId == null ? ImportState.Unconfirmed : drafts.Find(card.ExportDraftId)?.ImportState ?? ImportState.Unknown;
            if (Kind(draft) == "问答") yield break; // Legacy rows already restored into these same compositions.
        }
        if (draft.ModeCards.Count > 0)
        {
            // A previously generated mode still counts after the user changes the current selection.
            foreach (var child in LanguageModeCards.AllChildren(draft, drafts).Where(HasCard))
                yield return child.ImportState;
        }
        else if ((draft.Conversation.Count == 0 || Kind(draft) != "问答") && HasCard(draft))
            yield return draft.ImportState;
        if (draft.Conversation.Count > 0)
            foreach (var card in QuestionCards.Preview(draft, drafts).Where(c => draft.Chat == null || c.Index < draft.Chat.LegacyTurnCount))
                if (card.State != ImportState.Unconfirmed || draft.Conversation[card.Index].Answer.Answered && !string.IsNullOrWhiteSpace(card.Answer))
                    yield return card.State;
    }
    private static bool HasCard(ClipDraft draft) => draft.ConfirmedNote != null || draft.NoteId != null ||
        draft.ImportState != ImportState.Unconfirmed || draft.Excerpt != null ||
        draft.PreviewContent?.Fields is { IsUnresolved: false } || draft.PreviewContent?.Basic != null || draft.PreviewContent?.Answer is { Answered: true };

    public static string Kind(ClipDraft draft)
    {
        var kind = draft.Route?.OutputKind ?? draft.Configuration.Modes.FirstOrDefault(m => m.Definition.Id == draft.ModeId)?.Definition.OutputKind
            ?? draft.Content?.ModeId ?? draft.ModeId;
        if (draft.Input.Intent == "question" || kind == "question") return "问答";
        if (draft.Input.Intent == "translation" || kind == "translation") return "翻译";
        if (kind == "excerpt" || draft.Excerpt != null) return "摘录";
        if (kind == "language" || draft.Input.Intent == "language" || kind == null && draft.SupportsSpeech) return "语言";
        return "其他";
    }
    public static bool IsLanguage(ClipDraft draft) => Kind(draft) == "语言";
    public static ClipDraft? LastLanguageSpeech(DraftStore drafts) => drafts.AllSessions()
        .Where(d => IsLanguage(d) && d.LastSpeechAt != null).OrderByDescending(d => d.LastSpeechAt).FirstOrDefault();
    public static HistoryRecord[] Read(DraftStore drafts, ExcerptStore excerpts) => drafts.AllSessions().Select(d => new HistoryRecord(Draft: d))
        .Concat(excerpts.All().Select(e => new HistoryRecord(Excerpt: e))).OrderByDescending(r => r.Original.CapturedAt)
        .ThenBy(r => r.Key, StringComparer.Ordinal).ToArray();
}

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
        if (draft.ModeCards.Count > 0)
        {
            // A previously generated mode still counts after the user changes the current selection.
            foreach (var child in LanguageModeCards.AllChildren(draft, drafts).Where(HasCard))
                yield return child.ImportState;
        }
        else if ((draft.Conversation.Count == 0 || Kind(draft) != "问答") && HasCard(draft))
            yield return draft.ImportState;
        if (draft.Conversation.Count > 0)
            foreach (var card in QuestionCards.Preview(draft, drafts))
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

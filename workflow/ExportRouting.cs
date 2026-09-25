using System.Globalization;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

public interface ICardImporter
{
    ExportDestination? FixedDestination { get; }
    Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default);
}

public sealed record RemNoteExportPlan(string SourceId, string RootTitle, string? RootRemId,
    string[] DocumentPath, RemNoteCardPlan[] Cards)
{
    public string SchemaVersion { get; init; } = "knowledge-clipper.remnote.v1";
    public string? TargetDocumentId { get; init; }
}
public sealed record RemNoteImportReceipt(string RootRemId, string DocumentRemId, string[] RemIds, string[] CardIds,
    JsonElement Evidence);

public sealed record CardExportConfirmation(AnkiNoteDraft Note, ExportDestination Destination, RemNoteExportPlan? RemNote)
{
    public void Apply(ClipDraft draft)
    {
        if (ExportRouting.Frozen(draft)) return;
        draft.ConfirmedNote = Note; draft.ConfirmedDestination = Destination; draft.ConfirmedRemNote = RemNote;
        draft.ImportState = ImportState.Ready; draft.ImportError = null; draft.Times["confirmed"] = DateTimeOffset.UtcNow;
    }
}

public static class ExportRouting
{
    public static ModeDefinition[] BuiltinModes => [
        new("excerpt-manual", "摘录：原文、手动与挖空", true, "", "", "", "excerpt", RequiresContext: false),
        new("image-original", "原图：正面与反面", true, "", "", "", "excerpt", RequiresContext: false)];

    public static bool Frozen(ClipDraft draft) => draft.ConfirmedNote != null || draft.ConfirmedRemNote != null ||
        draft.NoteId != null || draft.RemNoteResult != null || draft.ImportState is ImportState.Writing or ImportState.Unknown or ImportState.Verified;

    public static ExportDestination ForMode(LanguageSettings settings, ModeDefinition definition) =>
        settings.ModeDestinations.TryGetValue(definition.Id, out var selected) ? selected :
            definition.OutputKind == "language" ? ExportDestination.Anki : ExportDestination.RemNote;

    public static ExportDestination For(ClipDraft draft, bool questionView = false)
    {
        if (!questionView)
        {
            if (draft.ConfirmedDestination is { } saved) return saved;
            // Older confirmed drafts have no destination metadata and always belong to Anki.
            if (Frozen(draft)) return ExportDestination.Anki;
        }
        return ForMode(draft.Settings, Definition(draft, questionView));
    }

    private static ModeDefinition Definition(ClipDraft draft, bool questionView)
    {
        if (questionView) return draft.Configuration.Modes.FirstOrDefault(mode => mode.Definition.OutputKind == "question")?.Definition
            ?? new("question", "问答", true, "", "", "", "question", RequiresContext: false);
        var id = draft.Route?.ModeId ?? draft.ModeId ?? draft.Content?.ModeId;
        return draft.Configuration.Modes.FirstOrDefault(mode => mode.Definition.Id == id)?.Definition
            ?? BuiltinModes.FirstOrDefault(mode => mode.Id == id)
            ?? new(id ?? "language", id ?? "语言", true, "", "", "", draft.Route?.OutputKind ?? (draft.Excerpt != null ? "excerpt" : "language"));
    }

    public static string[] DocumentPath(ClipDraft draft, bool questionView = false)
    {
        var definition = Definition(draft, questionView);
        if (definition.Id == "image-original") return ["图片", "原图"];
        if (definition.Id == "image-answer") return ["图片", "多图模式"];
        if (definition.Id == "excerpt-manual") return ["摘录"];
        return definition.OutputKind switch
        {
            "language" => ["语言学习", definition.Language == "zh" ? "汉语" : definition.Language == "en" ? "英语" : "其他语言",
                definition.Practice == PracticeChoice.Writing ? "书写练习" : "理解与朗读"],
            "question" => ["问答"],
            "translation" => ["翻译"],
            _ => ["摘录", definition.Name]
        };
    }

    public static string Describe(ClipDraft draft, bool questionView = false)
    {
        if (For(draft, questionView) == ExportDestination.Anki) return "Anki · " + DeckRouting.Describe(draft, questionView);
        var plan = questionView ? null : draft.ConfirmedRemNote;
        return "RemNote · " + string.Join(" / ", new[] { plan?.RootTitle ?? draft.Settings.RemNoteRootTitle }
            .Concat(plan?.DocumentPath ?? NewDocumentPath(draft, questionView)));
    }

    // Resolve the local day only while building a new plan. Confirmed plans retain their original path.
    private static string[] NewDocumentPath(ClipDraft draft, bool questionView = false) =>
        !questionView && draft.IsOriginalExcerpt && draft.Settings.OriginalExcerptDocumentId != null
            ? ["摘录", draft.Settings.OriginalExcerptDocumentTitle!]
            : [.. DocumentPath(draft, questionView), DateTime.Today.ToString("M.d", CultureInfo.InvariantCulture)];

    public static string ResultSummary(ClipDraft draft)
    {
        if (draft.AwaitingAnki) return "待导入 Anki · 打开后自动完成";
        if (For(draft) == ExportDestination.Anki) return "已导入 Anki：" + draft.CardIds.Length + " 张卡";
        if (draft.ImportState != ImportState.Verified) return "RemNote 导入待核对";
        var result = draft.RemNoteResult;
        return result == null ? "RemNote 导入待核对" : "已导入 RemNote：" + result.RemIds.Length + " 条内容" +
            (result.CardIds.Length > 0 ? " · " + result.CardIds.Length + " 张卡" : "（普通节点）");
    }

    public static AnkiNoteDraft BuildNote(ClipDraft draft, ExportDestination? forced = null) =>
        draft.ConfirmedNote ?? AnkiNoteDraft.From(draft, allowEmptyBasic: (forced ?? For(draft)) == ExportDestination.RemNote);

    public static CardExportConfirmation Prepare(ClipDraft draft, AnkiNoteDraft note, ExportDestination? forced = null)
    {
        if (Frozen(draft))
        {
            if (draft.ConfirmedNote == null) throw new InvalidDataException("已确认卡片快照缺失，不会重新生成或改变导出目标。");
            var existing = For(draft);
            if (forced != null && forced != existing) throw new InvalidDataException("本卡片已有不同的已确认导出目标。");
            return new(draft.ConfirmedNote, existing, draft.ConfirmedRemNote);
        }
        var destination = forced ?? For(draft);
        var plan = destination == ExportDestination.RemNote
            ? new RemNoteExportPlan("knowledgeclipper." + draft.Id, draft.Settings.RemNoteRootTitle, draft.Settings.RemNoteRootId,
                NewDocumentPath(draft), RemNoteCardConversion.Convert(note))
                { TargetDocumentId = draft.IsOriginalExcerpt ? draft.Settings.OriginalExcerptDocumentId : null } : null;
        return new(note, destination, plan);
    }

    public static void Confirm(ClipDraft draft, AnkiNoteDraft note, ExportDestination? forced = null) => Prepare(draft, note, forced).Apply(draft);
}

public sealed class CardImportRouter(HttpClient http) : ICardImporter
{
    public ExportDestination? FixedDestination => null;
    public Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default) => ExportRouting.For(draft) switch
    {
        ExportDestination.Anki => new AnkiConnection(http, draft.Settings.AnkiEndpoint).ImportAsync(draft, store, ct),
        ExportDestination.RemNote => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store, ct),
        _ => throw new InvalidDataException("导出对象无效。")
    };
}

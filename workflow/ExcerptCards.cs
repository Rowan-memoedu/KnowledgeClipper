using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public enum ExcerptCardKind { Basic, Cloze }
public sealed record ClozeRange(int Start, int Length);
public sealed record ExcerptFields(string Front, string Back, ExcerptCardKind Kind = ExcerptCardKind.Basic,
    string? ClozeSource = null, ClozeRange[]? Ranges = null)
{
    public ImageAsset[] FrontImages { get; init; } = [];
    public ImageAsset[] BackImages { get; init; } = [];
    [JsonIgnore] public bool HasImages => FrontImages.Length + BackImages.Length > 0;
}
public sealed class ExcerptCardLink
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? SupersededBy { get; set; }
    public string[] Replaces { get; init; } = [];
}
public sealed class ExcerptSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required CaptureMaterial Original { get; init; }
    public required LanguageSettings Settings { get; init; }
    public string ManualFront { get; set; } = "";
    public string ManualBack { get; set; } = "";
    public string? ImageMaterialId { get; init; }
    public List<ExcerptCardLink> Cards { get; set; } = [];
    [JsonIgnore] public SemaphoreSlim ImportGate { get; } = new(1, 1);
}

// Excerpts have their own session and card stores; they never enter language history.
public sealed class ExcerptStore(string root)
{
    public DraftStore Cards { get; } = new(root, "excerpt-cards");
    public void Save(ExcerptSession session) => WorkflowJson.Save(Path.Combine(root, "excerpt-sessions", session.Id + ".json"), session);
    public ExcerptSession? Find(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        var path = Path.Combine(root, "excerpt-sessions", id + ".json");
        return File.Exists(path) ? WorkflowJson.Read<ExcerptSession>(path) : null;
    }
    public IEnumerable<ExcerptSession> Recent() => All().Take(30);
    public IEnumerable<ExcerptSession> All()
    {
        var path = Path.Combine(root, "excerpt-sessions");
        if (!Directory.Exists(path)) yield break;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            ExcerptSession? session = null;
            try { session = WorkflowJson.Read<ExcerptSession>(file.FullName); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            if (session != null) yield return session;
        }
    }
}

public static class ExcerptCloze
{
    private static readonly Regex Markup = new(@"\{\{c1::(?<text>(?:(?!\{\{|\}\}|::).)+)(?:::(?<hint>(?:(?!\{\{|\}\}).)*))?\}\}", RegexOptions.Singleline);
    public static string Render(string source, IEnumerable<ClozeRange> ranges)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("正面不能为空。");
        if (Regex.IsMatch(source, @"\{\{c\d+::")) throw new InvalidDataException("请在未挖空的原文上选择文字。");
        var ordered = ranges.OrderBy(r => r.Start).ToArray();
        if (ordered.Length == 0) throw new InvalidDataException("请先选择需要挖空的文字。");
        var merged = new List<ClozeRange>();
        foreach (var range in ordered)
        {
            if (range.Start < 0 || range.Length <= 0 || range.Start > source.Length - range.Length ||
                range.Start > 0 && char.IsLowSurrogate(source[range.Start]) ||
                range.Start + range.Length < source.Length && char.IsLowSurrogate(source[range.Start + range.Length]))
                throw new InvalidDataException("挖空位置无效，请重新选择文字。");
            if (merged.LastOrDefault() is { } previous && range.Start < previous.Start + previous.Length)
                merged[^1] = new(previous.Start, Math.Max(previous.Start + previous.Length, range.Start + range.Length) - previous.Start);
            else merged.Add(range);
        }
        var output = new StringBuilder(); int offset = 0;
        foreach (var range in merged)
        {
            var text = source.Substring(range.Start, range.Length);
            if (string.IsNullOrWhiteSpace(text) || text.Contains("::") || text.Contains("{{") || text.Contains("}}"))
                throw new InvalidDataException("所选文字包含空白或 Anki 挖空保留符号（::、{{、}}），请调整选区。");
            output.Append(source[offset..range.Start]).Append("{{c1::").Append(text).Append("}}");
            offset = range.Start + range.Length;
        }
        var result = output.Append(source[offset..]).ToString();
        Validate(result);
        return result;
    }
    public static void Validate(string front)
    {
        var matches = Markup.Matches(front);
        if (matches.Count == 0 || matches.Any(m => string.IsNullOrWhiteSpace(m.Groups["text"].Value)) ||
            Regex.IsMatch(Markup.Replace(front, ""), @"\{\{c\d+::", RegexOptions.IgnoreCase))
            throw new InvalidDataException("每次制卡只能生成一张卡：挖空请统一使用 {{c1::文字}}，且不能嵌套。");
    }
    public static string Question(string front) { Validate(front); return Markup.Replace(front, m => "[" + (m.Groups["hint"].Success ? m.Groups["hint"].Value : "...") + "]"); }
    public static string Answer(string front) { Validate(front); return Markup.Replace(front, m => m.Groups["text"].Value); }
}

public static class ExcerptCards
{
    public static ExcerptSession Create(CaptureMaterial original, LanguageSettings settings, ExcerptStore store)
    {
        original.Validate();
        original = ExcerptTextCleanup.Clean(original);
        var session = new ExcerptSession { Original = original, Settings = settings with { }, ManualFront = original.SelectedText };
        Add(session, store, new(original.SelectedText, ""), "原文", originalExcerpt: true);
        return session;
    }
    public static void Validate(ExcerptFields fields)
    {
        if (string.IsNullOrWhiteSpace(fields.Front) && fields.FrontImages.Length == 0 || fields.Back == null || !Enum.IsDefined(fields.Kind))
            throw new InvalidDataException("卡片正面不能为空，反面可以留空。");
        if (fields.HasImages)
        {
            if (fields.Kind != ExcerptCardKind.Basic) throw new InvalidDataException("图片卡不能直接挖空，请先转为文字。");
            foreach (var image in fields.FrontImages.Concat(fields.BackImages)) image.ReadVerified();
            ImageContentValidation.MathLines(fields.Back);
        }
        if (fields.Kind == ExcerptCardKind.Cloze) ExcerptCloze.Validate(fields.Front);
        if (fields.ClozeSource != null && (fields.Ranges == null || ExcerptCloze.Render(fields.ClozeSource, fields.Ranges) != fields.Front))
            throw new InvalidDataException("挖空卡与原文选区不一致。");
    }
    public static ClipDraft Add(ExcerptSession session, ExcerptStore store, ExcerptFields fields, string label, ModeSnapshot? mode = null,
        string[]? replaces = null, bool saveSession = true, bool originalExcerpt = false)
    {
        Validate(fields);
        var definition = mode?.Definition ?? new ModeDefinition("excerpt-manual", "手动", true, "", "", "", "excerpt", RequiresContext: false,
            AnkiDeckPath: session.Settings.ExcerptDeckPath);
        if (definition.AnkiDeckPath == null) definition = definition with { AnkiDeckPath = session.Settings.ExcerptDeckPath };
        var draft = new ClipDraft { Original = session.Original, Material = session.Original, Settings = session.Settings,
            Configuration = new("", [mode == null ? new(definition, "", "") : mode with { Definition = definition }]),
            ModeId = definition.Id, Route = new(definition.Id, "excerpt", false, false),
            Input = new(Intent: "excerpt"), Excerpt = fields, IsOriginalExcerpt = originalExcerpt && !fields.HasImages };
        store.Cards.Save(draft);
        session.Cards.Add(new() { Id = draft.Id, Label = label, Replaces = replaces ?? [] });
        if (saveSession) store.Save(session);
        return draft;
    }
    public static ClipDraft Get(ExcerptSession session, ExcerptStore store, string id)
    {
        if (!session.Cards.Any(c => c.Id == id)) throw new InvalidDataException("卡片不属于当前摘录。");
        return store.Cards.Find(id) ?? throw new InvalidDataException("找不到摘录卡片草稿。");
    }
    public static void Edit(ExcerptSession session, ExcerptStore store, string id, string front, string back)
    {
        var draft = Get(session, store, id);
        if (draft.ConfirmedNote != null) throw new InvalidOperationException("已确认导入的卡片内容已固定，请另建手动卡。");
        var old = draft.Excerpt!;
        var fields = old with { Front = front, Back = back, ClozeSource = front == old.Front ? old.ClozeSource : null, Ranges = front == old.Front ? old.Ranges : null };
        Validate(fields); draft.Excerpt = fields; store.Cards.Save(draft);
    }
    public static ClipDraft Cloze(ExcerptSession session, ExcerptStore store, int start, int length)
    {
        ClozeRange[] ranges = [new(start, length)];
        return Add(session, store, new(ExcerptCloze.Render(session.ManualFront, ranges), session.ManualBack,
            ExcerptCardKind.Cloze, session.ManualFront, ranges), "挖空");
    }
    public static ClipDraft Merge(ExcerptSession session, ExcerptStore store, IEnumerable<string> ids)
    {
        var selected = ids.Distinct().ToArray();
        if (selected.Length < 2) throw new InvalidDataException("请至少勾选两张挖空卡。");
        var cards = selected.Select(id => Get(session, store, id)).ToArray();
        var fields = cards[0].Excerpt!;
        if (cards.Any(c => c.Excerpt is not { Kind: ExcerptCardKind.Cloze, ClozeSource: not null, Ranges: not null } ||
                c.Excerpt.ClozeSource != fields.ClozeSource || c.Excerpt.Back != fields.Back ||
                session.Cards.Single(link => link.Id == c.Id).SupersededBy != null))
            throw new InvalidDataException("只能合并来自同一版正面原文、反面相同且仍在列表中的选区挖空卡。");
        if (cards.Any(c => c.ImportState is ImportState.Unknown or ImportState.Writing || c.NoteId != null && c.ImportState != ImportState.Verified))
            throw new InvalidDataException("请先核对导入状态不明的卡片，再合并。");
        var ranges = cards.SelectMany(c => c.Excerpt!.Ranges!).Distinct().ToArray();
        var pending = cards.Where(c => c.ImportState != ImportState.Verified).Select(c => c.Id).ToArray();
        var merged = Add(session, store, fields with { Front = ExcerptCloze.Render(fields.ClozeSource!, ranges), Ranges = ranges }, "合并挖空", replaces: pending, saveSession: false);
        foreach (var link in session.Cards.Where(link => pending.Contains(link.Id))) link.SupersededBy = merged.Id;
        store.Save(session);
        return merged;
    }
    public static void UndoMerge(ExcerptSession session, ExcerptStore store, string id)
    {
        var merged = Get(session, store, id);
        var link = session.Cards.Single(c => c.Id == id);
        if (link.Replaces.Length == 0 || merged.ConfirmedNote != null || link.SupersededBy != null)
            throw new InvalidOperationException("仅可撤销尚未确认导入的合并。");
        foreach (var original in session.Cards.Where(c => c.SupersededBy == id)) original.SupersededBy = null;
        link.SupersededBy = "undone";
        store.Save(session);
    }
    public static async Task ImportAsync(ExcerptSession session, ExcerptStore store, ICardImporter anki, IEnumerable<string> ids,
        CancellationToken ct = default, Action? changed = null)
    {
        await session.ImportGate.WaitAsync(ct);
        try
        {
            var active = session.Cards.Where(c => c.SupersededBy == null).Select(c => c.Id).ToHashSet();
            var requested = ids.Distinct().ToArray();
            if (requested.Any(id => !active.Contains(id))) throw new InvalidDataException("已替换的卡片不能再次导入。");
            var cards = requested.Select(id => Get(session, store, id)).Where(c => c.ImportState != ImportState.Verified).ToArray();
            // Validate the whole local batch before the first HTTP request. Confirmed identities never change on retry.
            var plans = cards.Select(c => ExportRouting.Prepare(c, ExportRouting.BuildNote(c, anki.FixedDestination), anki.FixedDestination)).ToArray();
            for (var i = 0; i < cards.Length; i++)
            {
                if (cards[i].ConfirmedNote != null) continue;
                plans[i].Apply(cards[i]); store.Cards.Save(cards[i]);
            }
            var errors = new List<string>();
            foreach (var card in cards)
            {
                ct.ThrowIfCancellationRequested();
                try { await anki.ImportAsync(card, store.Cards, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(session.Cards.Single(c => c.Id == card.Id).Label + "：" + ex.Message); }
                finally { changed?.Invoke(); }
            }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        }
        finally { session.ImportGate.Release(); }
    }
}

using System.Text;

namespace KnowledgeClipper.Workflow;

public sealed record DeckAssignment(string Deck, Dictionary<int, string>? CardDecks);

public static class DeckRouting
{
    public const string DefaultRoot = "KnowledgeClipper";

    public static string PathFor(ModeDefinition definition) => definition.AnkiDeckPath ?? definition.OutputKind switch
    {
        "language" => "语言学习::{语种}::{格式}",
        "question" => "问答",
        "translation" => "翻译::中译英",
        _ => definition.Name
    };

    public static void ValidateRoot(string root) => ValidatePath(root, false);

    public static void ValidatePattern(string path, string outputKind)
    {
        ValidatePath(path, true);
    }

    private static void ValidatePath(string path, bool pattern)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Split("::").Any(p => string.IsNullOrWhiteSpace(p)) || path.Any(char.IsControl))
            throw new InvalidDataException("牌组路径不能为空；多层牌组请使用 :: 分隔，且每层都要填写名称。");
        var literal = pattern ? path.Replace("{语种}", "").Replace("{格式}", "") : path;
        if (literal.Contains('{') || literal.Contains('}'))
            throw new InvalidDataException(pattern ? "牌组路径仅支持 {语种} 和 {格式} 占位符。" : "根牌组名称不能包含占位符。");
    }

    public static DeckAssignment Resolve(ClipDraft draft, int[] ordinals) => Resolve(draft, ordinals, false);

    private static DeckAssignment Resolve(ClipDraft draft, int[] ordinals, bool questionView)
    {
        if (ordinals.Length == 0 || ordinals.Distinct().Count() != ordinals.Length)
            throw new InvalidDataException("卡片模板序号无效。");
        if (draft.Settings.AnkiRootDeck is not { } root) return new(draft.Settings.AnkiDeck, null);
        ValidateRoot(root);
        var definition = DefinitionFor(draft, questionView);
        var pattern = PathFor(definition);
        ValidatePattern(pattern, definition.OutputKind);
        var language = LanguageFor(draft.Original.SelectedText);
        var decks = ordinals.ToDictionary(ordinal => ordinal, ordinal =>
        {
            var format = definition.OutputKind switch
            {
                "language" => ordinal switch { 0 => "理解与朗读", 1 => "书写练习", _ => throw new InvalidDataException("语言卡片模板序号无效。") },
                "question" => "问答",
                "translation" => "中译英",
                _ => "正反面"
            };
            var path = pattern.Replace("{语种}", language).Replace("{格式}", format);
            return Normalize(root) + "::" + Normalize(path);
        });
        return new(decks[ordinals[0]], decks);
    }

    private static string Normalize(string path) => string.Join("::", path.Split("::").Select(p => p.Trim()));

    private static ModeDefinition DefinitionFor(ClipDraft draft, bool questionView)
    {
        var id = draft.Route?.ModeId ?? draft.ModeId ?? draft.Content?.ModeId;
        var current = draft.Configuration.Modes.FirstOrDefault(m => m.Definition.Id == id)?.Definition;
        var kind = questionView ? "question" : draft.Route?.OutputKind ?? current?.OutputKind ??
            (draft.Content?.Basic is not null ? (draft.Content.Answer != null || id == "question" ? "question" : "translation") : "language");
        return !questionView && current != null ? current :
            draft.Configuration.Modes.FirstOrDefault(m => m.Definition.OutputKind == kind)?.Definition ??
            new ModeDefinition(kind, kind, true, "", "", "", kind);
    }

    // Routing describes the selected target, never the surrounding source sentence.
    private static string LanguageFor(string text)
    {
        bool han = false, latin = false, other = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af) han = true;
            else if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin = true;
            else if (Rune.IsLetter(rune)) other = true;
        }
        return other ? "其他语言" : han ? latin ? "中英混合" : "汉语" : latin ? "英语" : "其他语言";
    }

    public static string Describe(ClipDraft draft, bool questionView = false)
    {
        if (!questionView && draft.ConfirmedNote is { } frozen)
            return string.Join("；", frozen.Ordinals.Select(frozen.DeckFor).Distinct());
        var kind = DefinitionFor(draft, questionView).OutputKind;
        int[] ordinals = kind != "language" ? [0] : draft.Practice switch
        {
            PracticeChoice.Reading => [0], PracticeChoice.Writing => [1], _ => [0, 1]
        };
        var result = Resolve(draft, ordinals, questionView);
        return result.CardDecks == null ? result.Deck : string.Join("；", result.CardDecks.Values.Distinct());
    }
}

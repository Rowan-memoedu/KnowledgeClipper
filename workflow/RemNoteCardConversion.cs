using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public sealed record RemNoteRichPart(string Kind, string? Text = null, string? Uri = null,
    string? Sha256 = null, int? ClozeId = null, string? Hint = null, bool? Block = null);
public sealed record RemNoteCardPlan(string Key, string Kind, RemNoteRichPart[] Front, RemNoteRichPart[][] Back);

// Converts only the fields and templates produced by this application. It does not ask an LLM,
// interpret arbitrary Anki templates, or use RemNote's Markdown importer to infer card structure.
public static class RemNoteCardConversion
{
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.Singleline);
    private static readonly Regex ImageTag = new("^<img\\s+src=(?:\"(?<src>[^\"]*)\"|'(?<src>[^']*)')(?:\\s+style=(?:\"[^\"]*\"|'[^']*'))?\\s*/?>$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Cloze = new(@"\{\{c(?<id>[1-9]\d*)::(?<text>(?:(?!\{\{|\}\}|::).)+)(?:::(?<hint>(?:(?!\{\{|\}\}).)*))?\}\}", RegexOptions.Singleline);
    private static readonly Regex ClozeStart = new(@"\{\{c\d+::", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Math = new(@"(?<!\\)\\\(.*?(?<!\\)\\\)|(?<!\\)\\\[.*?(?<!\\)\\\]|(?<!\\)\$\$.*?(?<!\\)\$\$", RegexOptions.Singleline);

    public static RemNoteCardPlan[] Convert(AnkiNoteDraft note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Fields == null || note.Ordinals == null || note.Ordinals.Length == 0 ||
            note.Ordinals.Any(ordinal => ordinal < 0) || note.Ordinals.Distinct().Count() != note.Ordinals.Length)
            throw new InvalidDataException("已确认卡片缺少有效模板序号。");
        var reader = new FieldReader(note);
        RemNoteCardPlan[] result;
        if (note.IsCloze)
        {
            var front = ParseCloze(reader.Read("Front"));
            result = [new("card.0", "cloze", front, Rows(reader.Read("Back")))];
        }
        else if (note.IsBasic)
        {
            if (!note.Ordinals.SequenceEqual(new[] { 0 })) throw new InvalidDataException("正反面卡的模板序号必须为 0。");
            var front = reader.Read("Front");
            if (note.HasQuestionContext)
            {
                var context = reader.Read("Context");
                if (HasContent(context)) front = Compact(context.Concat([Text("\n")]).Concat(front));
            }
            if (!HasContent(front)) throw new InvalidDataException("RemNote 正面不能为空。");
            var back = Rows(reader.Read("Back"));
            if (note.IsDictionaryImage) back = back.Concat(Rows(reader.Read("DictionaryDetails"))).ToArray();
            result = back.Length > 0 ? [new("card.0", "basic", front, back)] :
                Rows(front).Select((row, index) => new RemNoteCardPlan("plain." + index, "plain", row, [])).ToArray();
        }
        else
        {
            if (note.Media?.Length > 0) throw new InvalidDataException("当前语言模板没有图片字段，不能忽略附带媒体。");
            result = note.Ordinals.Select(ordinal => Language(note, reader, ordinal)).ToArray();
        }
        reader.RequireAllMediaUsed();
        if (result.Length == 0) throw new InvalidDataException("没有可导入的内容。");
        return result.Select(plan => plan with { Front = ParseMath(plan.Front), Back = plan.Back.Select(ParseMath).ToArray() }).ToArray();
    }

    private static RemNoteCardPlan Language(AnkiNoteDraft note, FieldReader reader, int ordinal)
    {
        if (ordinal is not (0 or 1)) throw new InvalidDataException("语言卡片模板序号无效。");
        if (reader.Value(ordinal == 0 ? "ReadingEnabled" : "WritingEnabled") != "1")
            throw new InvalidDataException("已确认模板与启用的语言练习不符。");
        string front, back;
        if (note.IsPublishedLanguage)
        {
            // Validate the controlled HTML before PublishedFaces performs its single entity decode.
            foreach (var field in new[] { "Target", "Pronunciation", "SentenceBefore", "SentenceAfter", "Meaning",
                "ExplanationEn", "SentenceTranslationZh", "EnglishMode" }) reader.Value(field);
            if (note.HasDictionaryDetails) reader.Value("DictionaryDetails");
            (front, back) = note.PublishedFaces(ordinal);
        }
        else
        {
            var target = reader.Value("Target"); var pronunciation = reader.Value("Pronunciation");
            var meaning = reader.Value("Meaning"); var before = reader.Value("SentenceBefore");
            var after = reader.Value("SentenceAfter"); var origin = reader.Value("Origin");
            var sentence = before + target + after;
            front = (ordinal == 0 ? sentence : before + "［" + pronunciation + " · " + meaning + "］" + after) +
                (origin.Length > 0 ? "\n" + origin : "");
            back = ordinal == 0 ? target + " · " + pronunciation + "\n" + meaning : target + "\n" + sentence;
        }
        var frontParts = new[] { Text(NormalizeLines(front)) }; var backRows = Rows([Text(NormalizeLines(back))]);
        if (!HasContent(frontParts) || backRows.Length == 0) throw new InvalidDataException("语言卡片缺少正面或反面。");
        return new("card." + ordinal, "basic", frontParts, backRows);
    }

    private sealed class FieldReader
    {
        private readonly AnkiNoteDraft note;
        private readonly Dictionary<string, ImageAsset> media = new(StringComparer.Ordinal);
        private readonly HashSet<string> used = new(StringComparer.Ordinal);

        internal FieldReader(AnkiNoteDraft note)
        {
            this.note = note;
            foreach (var asset in note.Media ?? [])
            {
                asset.ReadVerified();
                media.TryAdd(asset.FileName, asset);
            }
        }
        internal string Value(string name)
        {
            var parts = Read(name);
            if (parts.Any(part => part.Kind != "text")) throw new InvalidDataException("语言字段不能包含图片：" + name);
            return string.Concat(parts.Select(part => part.Text));
        }
        internal RemNoteRichPart[] Read(string name)
        {
            if (!note.Fields.TryGetValue(name, out var html) || html == null) throw new InvalidDataException("已确认卡片缺少字段：" + name);
            var parts = new List<RemNoteRichPart>(); var offset = 0;
            foreach (Match tag in Tags.Matches(html))
            {
                AddLiteral(html[offset..tag.Index]);
                if (Regex.IsMatch(tag.Value, @"^<br\s*/?>$", RegexOptions.IgnoreCase)) parts.Add(Text("\n"));
                else if (tag.Value.Equals("<div>", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Count > 0 && !EndsWithLine(parts[^1])) parts.Add(Text("\n"));
                }
                else if (tag.Value.Equals("</div>", StringComparison.OrdinalIgnoreCase)) parts.Add(Text("\n"));
                else
                {
                    var image = ImageTag.Match(tag.Value);
                    if (!image.Success) throw new InvalidDataException("卡面含未支持的原始 HTML，不能无损转换：" + name);
                    var source = WebUtility.HtmlDecode(image.Groups["src"].Value);
                    if (!media.TryGetValue(source, out var asset)) throw new InvalidDataException("卡面图片缺少已验证原图：" + source);
                    used.Add(source);
                    parts.Add(new("image", Uri: new Uri(asset.Path).AbsoluteUri, Sha256: asset.Sha256));
                }
                offset = tag.Index + tag.Length;
            }
            AddLiteral(html[offset..]);
            return Compact(parts);

            void AddLiteral(string value)
            {
                // Source text is HTML-encoded by AnkiNoteDraft.Html. Decode after tokenization so
                // literal <img>, <br>, entities, and Anki braces stay text rather than becoming syntax.
                if (value.Contains('<')) throw new InvalidDataException("卡面含未闭合的原始 HTML：" + name);
                if (value.Length > 0) parts.Add(Text(NormalizeLines(WebUtility.HtmlDecode(value))));
            }
        }
        internal void RequireAllMediaUsed()
        {
            if (media.Keys.Any(file => !used.Contains(file))) throw new InvalidDataException("存在未出现在正反面的图片，不能静默丢弃媒体。");
        }
    }

    private static RemNoteRichPart[] ParseCloze(RemNoteRichPart[] parts)
    {
        var result = new List<RemNoteRichPart>(); var count = 0;
        foreach (var part in parts)
        {
            if (part.Kind != "text") { result.Add(part); continue; }
            var value = part.Text ?? ""; var offset = 0;
            foreach (Match match in Cloze.Matches(value))
            {
                AddLiteral(value[offset..match.Index]);
                if (!int.TryParse(match.Groups["id"].Value, out var id) || string.IsNullOrWhiteSpace(match.Groups["text"].Value))
                    throw new InvalidDataException("挖空序号或文字无效。");
                result.Add(new("cloze", Text: match.Groups["text"].Value, ClozeId: id,
                    Hint: match.Groups["hint"].Success ? match.Groups["hint"].Value : null));
                count++; offset = match.Index + match.Length;
            }
            AddLiteral(value[offset..]);

            void AddLiteral(string value)
            {
                if (ClozeStart.IsMatch(value)) throw new InvalidDataException("挖空语法无效或嵌套，无法无损转换。");
                if (value.Length > 0) result.Add(Text(value));
            }
        }
        if (count == 0) throw new InvalidDataException("挖空卡片没有有效挖空。");
        return Compact(result);
    }

    private static RemNoteRichPart[][] Rows(IEnumerable<RemNoteRichPart> parts)
    {
        var rows = new List<RemNoteRichPart[]>(); var row = new List<RemNoteRichPart>();
        foreach (var part in parts)
        {
            if (part.Kind != "text") { row.Add(part); continue; }
            var value = part.Text ?? ""; var formulas = Math.Matches(value).Cast<Match>().ToArray();
            var offset = 0; var formulaIndex = 0;
            for (var index = 0; index < value.Length; index++)
            {
                while (formulaIndex < formulas.Length && formulas[formulaIndex].Index + formulas[formulaIndex].Length <= index) formulaIndex++;
                if (value[index] != '\n' || formulaIndex < formulas.Length && formulas[formulaIndex].Index <= index) continue;
                if (index > offset) row.Add(Text(value[offset..index]));
                Flush(); offset = index + 1;
            }
            if (offset < value.Length) row.Add(Text(value[offset..]));
        }
        Flush(); return rows.ToArray();

        void Flush()
        {
            if (HasContent(row)) rows.Add(Compact(row));
            row.Clear();
        }
    }

    private static RemNoteRichPart[] ParseMath(RemNoteRichPart[] parts)
    {
        var result = new List<RemNoteRichPart>();
        foreach (var part in parts)
        {
            if (part.Kind is not ("text" or "cloze")) { result.Add(part); continue; }
            var value = part.Text ?? ""; var offset = 0;
            foreach (Match formula in Math.Matches(value))
            {
                if (formula.Index > offset) result.Add(part with { Text = value[offset..formula.Index] });
                result.Add(new("latex", Text: formula.Value[2..^2], ClozeId: part.ClozeId, Hint: part.Hint,
                    Block: !formula.Value.StartsWith(@"\(", StringComparison.Ordinal)));
                offset = formula.Index + formula.Length;
            }
            if (offset < value.Length) result.Add(part with { Text = value[offset..] });
        }
        return Compact(result);
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private static RemNoteRichPart Text(string text) => new("text", Text: text);
    private static bool EndsWithLine(RemNoteRichPart part) => part.Kind == "text" && part.Text?.EndsWith('\n') == true;
    private static bool HasContent(IEnumerable<RemNoteRichPart> parts) => parts.Any(part => part.Kind != "text" || !string.IsNullOrWhiteSpace(part.Text));
    private static RemNoteRichPart[] Compact(IEnumerable<RemNoteRichPart> parts)
    {
        var result = new List<RemNoteRichPart>(); var text = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Kind == "text") text.Append(part.Text);
            else { Flush(); result.Add(part); }
        }
        Flush(); return result.ToArray();
        void Flush() { if (text.Length > 0) { result.Add(Text(text.ToString())); text.Clear(); } }
    }
}

using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

// Content and card semantics of the prompt publication dated 2026-09-11.
public static class PublishedLanguage
{
    public const string Format = "language-content.2026-09-11";
    public const string ModelSuffix = " · 语言20260911";
    public const string DictionaryModelSuffix = " · 语言词典20260923折叠";
    public static bool Applies(ModeDefinition? mode) => mode?.ContentFormat == Format;
    public static bool Applies(LanguageFields? fields) => fields?.Format == Format;
    public static string ModelName(string selected) => selected.EndsWith(ModelSuffix, StringComparison.Ordinal) ? selected : selected + ModelSuffix;
    public static string DictionaryModelName(string selected) => selected.EndsWith(DictionaryModelSuffix, StringComparison.Ordinal) ? selected : selected + DictionaryModelSuffix;

    public static string? MarkedContext(CaptureMaterial material)
    {
        material.Validate();
        if (material.ContextOrigin == "generated_allowed" && material.ContextText == null) return null;
        if (material.ContextOrigin is not ("source" or "user") || string.IsNullOrWhiteSpace(material.ContextText))
            throw new InvalidDataException("请提供来源原文，或明确允许生成例句。");
        var start = material.TargetStart!.Value;
        static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return Escape(material.ContextText[..start]) + "<selected>" + Escape(material.SelectedText) + "</selected>" +
            Escape(material.ContextText[(start + material.SelectedText.Length)..]);
    }

    public static LanguageFields Parse(string json, ModeDefinition mode, CaptureMaterial material)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var expected = new HashSet<string>(["status", "pronunciation", "generatedSentence", "generatedTargetStart", "unresolvedReasons"], StringComparer.Ordinal);
        if (mode.Language == "zh") expected.Add("meaning");
        else
        {
            expected.Add("explanationEn"); expected.Add("sentenceTranslationZh"); expected.Add("normalizedSentence");
            if (mode.Practice == PracticeChoice.Reading) expected.Add("lemma");
        }
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Count || !expected.SetEquals(names)) throw new InvalidDataException("语言结果的字段集合与正式模式不符。");
        if (root.GetProperty("generatedTargetStart").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("模型不得返回生成例句的数值位置，位置由程序计算。");
        var fields = JsonSerializer.Deserialize<LanguageFields>(json, WorkflowJson.Options)! with { Format = Format };
        if (material.SourceTitle.StartsWith("Tatoeba #", StringComparison.Ordinal) && fields.NormalizedSentence != null)
            throw new InvalidDataException("开放语料例句必须原样保留，不能改写英文原句。");
        // A best-effort source translation must not block a usable card when it changes
        // the target spelling or repeats it. The original source still has a verified span.
        if (fields.Status == "ready" && mode.Language == "en" && material.ContextText != null && fields.NormalizedSentence != null)
        {
            try { _ = UniqueTarget(fields.NormalizedSentence, material.SelectedText); }
            catch (InvalidDataException) { fields = fields with { NormalizedSentence = null }; }
        }
        Validate(mode, material, fields, normalized: false);
        if (!fields.IsUnresolved && material.ContextText == null)
            fields = fields with { GeneratedTargetStart = FirstTarget(fields.GeneratedSentence!, material.SelectedText) };
        Validate(mode, material, fields);
        return fields;
    }

    public static int UniqueTarget(string sentence, string target)
    {
        var start = sentence.IndexOf(target, StringComparison.Ordinal);
        if (start < 0 || sentence.IndexOf(target, start + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("生成例句必须包含且只包含一次原样目标。");
        return start;
    }

    public static int FirstTarget(string sentence, string target)
    {
        var start = sentence.IndexOf(target, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("生成例句必须包含原样目标，保留大小写及全部字符。");
        return start;
    }

    public static void Validate(ModeDefinition mode, CaptureMaterial material, LanguageFields fields, bool normalized = true)
    {
        if (!Applies(mode) || !Applies(fields) || mode.Language is not ("zh" or "en") || mode.Practice is not (PracticeChoice.Reading or PracticeChoice.Writing))
            throw new InvalidDataException("语言字段版本与模式不符；旧结果保留原格式，不能当作本次发布结果。");
        _ = MarkedContext(material);
        if (fields.UnresolvedReasons == null) throw new InvalidDataException("缺少语言结果的待处理原因。");
        if (fields.IsUnresolved)
        {
            if (fields.Pronunciation != null || fields.Meaning != null || fields.ExplanationEn != null || fields.SentenceTranslationZh != null ||
                fields.Lemma != null || fields.GeneratedSentence != null || fields.NormalizedSentence != null || fields.GeneratedTargetStart != null ||
                fields.UnresolvedReasons.Length == 0 || fields.UnresolvedReasons.Any(reason => !Text(reason) || !HasHan(reason)))
                throw new InvalidDataException("待处理结果必须清空学习字段，并保留具体中文原因。");
            return;
        }
        if (fields.Status != "ready" || fields.UnresolvedReasons.Length != 0 || !Text(fields.Pronunciation) || HasHan(fields.Pronunciation!))
            throw new InvalidDataException("语言结果状态、读音或待处理原因不一致。");
        if (mode.Language == "zh")
        {
            if (!Text(fields.Meaning) || fields.ExplanationEn != null || fields.SentenceTranslationZh != null || fields.Lemma != null || fields.NormalizedSentence != null)
                throw new InvalidDataException("汉语模式需要中文意义字段，不接收英语模式字段。");
        }
        else if (fields.Meaning != null || !Text(fields.ExplanationEn) ||
            !Text(fields.SentenceTranslationZh) || fields.Lemma != null && (!Text(fields.Lemma) || mode.Practice != PracticeChoice.Reading))
            throw new InvalidDataException("英语模式需要英文解释与整句中文翻译，不能使用旧中文 meaning 代替。");
        if (material.ContextText != null)
        {
            if (fields.GeneratedSentence != null || fields.GeneratedTargetStart != null) throw new InvalidDataException("来源原文不能替换成生成例句。");
            if (fields.NormalizedSentence != null)
            {
                if (mode.Language != "en" || !Text(fields.NormalizedSentence))
                    throw new InvalidDataException("转换例句必须是英语模式的非空文本。");
                _ = UniqueTarget(fields.NormalizedSentence!, material.SelectedText);
            }
        }
        else
        {
            if (fields.NormalizedSentence != null) throw new InvalidDataException("生成分支不能同时返回来源转换例句。");
            if (!Text(fields.GeneratedSentence)) throw new InvalidDataException("缺少获准生成的自然例句。");
            var start = FirstTarget(fields.GeneratedSentence!, material.SelectedText);
            if (normalized ? fields.GeneratedTargetStart != start : fields.GeneratedTargetStart != null)
                throw new InvalidDataException("生成目标位置与程序的首次原样匹配不符。");
        }
    }

    public static string EffectiveSentence(CaptureMaterial material, LanguageFields fields) =>
        fields.NormalizedSentence ?? material.ContextText ?? fields.GeneratedSentence!;
    public static int EffectiveTargetStart(CaptureMaterial material, LanguageFields fields) =>
        fields.NormalizedSentence != null ? UniqueTarget(fields.NormalizedSentence, material.SelectedText) :
        material.TargetStart ?? fields.GeneratedTargetStart!.Value;

    private static bool Text(string? text) => !string.IsNullOrWhiteSpace(text) && text.IndexOfAny(['\r', '\n', '\u2028', '\u2029']) < 0;
    private static bool HasHan(string text) => text.EnumerateRunes().Any(r => r.Value is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x3347f);
    public static string PendingReason(LanguageFields fields) => "内容待处理：" + string.Join("；", fields.UnresolvedReasons);

    public sealed record Faces(string ReadingFront, string ReadingBack, string WritingFront, string WritingBack, int TargetStart, string? Warning);
    public static Faces Render(ModeDefinition mode, CaptureMaterial material, LanguageFields fields)
    {
        Validate(mode, material, fields);
        if (fields.IsUnresolved) throw new InvalidDataException(PendingReason(fields));
        var sentence = EffectiveSentence(material, fields);
        var start = EffectiveTargetStart(material, fields);
        var before = sentence[..start]; var after = sentence[(start + material.SelectedText.Length)..];
        var english = mode.Language == "en";
        var hint = english ? fields.Pronunciation : "（" + fields.Pronunciation + "；" + fields.Meaning + "）";
        var writingFront = before + hint + after + (english ? "\n" + fields.ExplanationEn : "");
        var readingBack = english ? string.Join("\n", fields.Pronunciation, fields.ExplanationEn, fields.SentenceTranslationZh) :
            string.Join("\n", material.SelectedText, fields.Pronunciation, fields.Meaning);
        var warning = mode.Practice == PracticeChoice.Writing &&
            (before.Contains(material.SelectedText, StringComparison.Ordinal) || after.Contains(material.SelectedText, StringComparison.Ordinal))
            ? "原文其他位置仍含相同答案；书写正面只替换本次选区，请核对是否适合作为回忆练习。" : null;
        return new(sentence, readingBack, writingFront, material.SelectedText + (english ? "\n" + fields.SentenceTranslationZh : ""), start, warning);
    }
}

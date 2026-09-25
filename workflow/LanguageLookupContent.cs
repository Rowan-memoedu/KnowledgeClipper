using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public sealed partial class ContentModule
{
    public Task<LanguageLookupResult> LookupMeaningAsync(CaptureMaterial material, LanguageSettings settings,
        string prompt, CancellationToken ct = default)
    {
        var context = material.LookupContextText ?? material.ContextText;
        if (string.IsNullOrWhiteSpace(context)) throw new InvalidDataException("未识别到当前句子。");
        return RequestAsync(settings, prompt, new { target = material.SelectedText, sentence = context }, json =>
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2)
                throw new InvalidDataException("词义响应字段不完整。");
            var meaning = root.GetProperty("meaning_zh").GetString()?.Trim();
            var pronunciation = root.GetProperty("pronunciation").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(meaning) || meaning.Length > 60 || !Regex.IsMatch(meaning, @"\p{IsCJKUnifiedIdeographs}") ||
                Regex.IsMatch(meaning, @"[A-Za-z#*<>\r\n]"))
                throw new InvalidDataException("词义必须是简短的纯中文解释。");
            if (string.IsNullOrWhiteSpace(pronunciation) || pronunciation.Length > 160 || Regex.IsMatch(pronunciation, @"\p{IsCJKUnifiedIdeographs}"))
                throw new InvalidDataException("缺少当前句子对应的读音。");
            if (Regex.IsMatch(material.SelectedText, @"[A-Za-z]") &&
                (!Regex.IsMatch(pronunciation, @"^(?:/[^/\r\n]+/|\[[^\[\]\r\n]+\])$") ||
                 !pronunciation.Any(char.IsLetter) || pronunciation.Any(char.IsDigit)))
                throw new InvalidDataException("英文词块的读音必须是音标。");
            return Task.FromResult(new LanguageLookupResult(meaning, pronunciation, context));
        }, ct, maxTokens: 240, disableThinking: true);
    }
}

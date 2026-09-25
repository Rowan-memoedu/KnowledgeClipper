namespace KnowledgeClipper.Workflow;

public enum MaterialKind { TypedText, OcrImages, OriginalImages }
public sealed record MaterialRoute(string Id, string Name);

// Every ingress resolves a saved preference against the routes its material supports.
// Missing or unavailable preferences are Ask, never an AI classification request.
public static class MaterialRouting
{
    public const string Ask = "ask", Punctuation = "punctuation";
    public static MaterialRoute[] Choices(MaterialKind kind, IEnumerable<ModeSnapshot>? imageModes = null) => kind switch
    {
        MaterialKind.TypedText => [new("excerpt", "摘录"), new("language", "语言学习"), new("translation", "翻译"), new("question", "问答")],
        MaterialKind.OcrImages => [new("excerpt", "摘录"), new("language", "语言学习"), new("question", "问答")],
        MaterialKind.OriginalImages => (imageModes ?? []).Where(m => m.Definition.Enabled && m.LoadError == null)
            .Select(m => new MaterialRoute(m.Definition.Id, m.Definition.Name)).Append(new("image-excerpt", "原图摘录（反面留空）")).ToArray(),
        _ => []
    };
    public static string? Resolve(MaterialKind kind, LanguageSettings settings, string text = "", IEnumerable<ModeSnapshot>? imageModes = null)
    {
        var selected = kind switch { MaterialKind.TypedText => settings.TypedDefaultRoute,
            MaterialKind.OcrImages => settings.OcrDefaultRoute, _ => settings.OriginalDefaultRoute };
        if (kind == MaterialKind.TypedText && selected == Punctuation)
            return ContentModule.IsQuestion(text) ? "question" : "translation";
        return Choices(kind, imageModes).Any(c => c.Id == selected) ? selected : null;
    }
    public static string TypedPlaceholder(LanguageSettings settings) => settings.TypedDefaultRoute switch
    {
        Punctuation => "输入文字：问号结尾提问，否则翻译；Enter 提交",
        "excerpt" => "输入摘录原文，Enter 提交", "language" => "输入学习内容，Enter 提交",
        "translation" => "输入待翻译内容，Enter 提交", "question" => "输入问题，Enter 提交",
        _ => "输入文字，Enter 后选择处理路径"
    };
}

using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public static class ExcerptTextCleanup
{
    // Zhihu appends this attribution block to copied text. Match only a complete
    // footer, so an author's discussion of licensing inside the body survives.
    private static readonly Regex ZhihuAttribution = new(
        @"(?:\r?\n){1,3}(?:作者[：:][^\r\n]{1,120}\r?\n)?(?:链接[：:][ \t]*https?://(?:www\.zhihu\.com|zhuanlan\.zhihu\.com)/[^\r\n]{1,500}\r?\n)?来源[：:][ \t]*知乎[ \t]*\r?\n著作权归作者所有[。.]?[ \t]*商业转载请联系作者获得授权[，,][ \t]*非商业转载请注明出处[。.]?[ \t\r\n]*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static CaptureMaterial Clean(CaptureMaterial material)
    {
        if (!material.SelectedText.Contains("著作权归作者所有", StringComparison.Ordinal)) return material;
        var match = ZhihuAttribution.Match(material.SelectedText);
        if (!match.Success) return material;
        var clean = material.SelectedText[..match.Index].TrimEnd();
        if (string.IsNullOrWhiteSpace(clean)) return material;
        string? context = material.ContextText;
        if (context != null)
        {
            var start = material.TargetStart ?? throw new InvalidDataException("摘录原文缺少选区位置。");
            context = context[..start] + clean + context[(start + material.SelectedText.Length)..];
        }
        var result = material with { SelectedText = clean, ContextText = context };
        result.Validate();
        return result;
    }
}

namespace KnowledgeClipper.Workflow;

// The clicked action determines a single-image route. Saved defaults only choose
// the destination after the explicit multi-image collection has finished.
public static class ImageEntryRouting
{
    public static bool NeedsOcr(ImageMaterialJob job) => job.Kind == MaterialKind.OcrImages ||
        job.Route == "language" || job.Route == "question" && job.Quick;

    public static void SetSingle(ImageMaterialJob job, string intent, bool quick)
    {
        if (intent is not ("excerpt" or "language" or "question") || job.Images.Length != 1)
            throw new InvalidDataException("单图入口需要一张图片和明确的处理方式。");
        job.Entry = intent; job.Route = intent; job.Quick = quick;
    }

    public static CaptureMaterial Material(ImageMaterialJob job, string text)
    {
        var preserveImage = job.Kind == MaterialKind.OriginalImages && job.Route is "language" or "question";
        if (preserveImage && job.Questions.Length != 1) throw new InvalidDataException("此入口只处理当前剪贴板的一张图片。");
        // MinerU emits Markdown structure. A heading marker is not part of the
        // language-learning target; keep the saved OCR and excerpt text intact.
        if (NeedsOcr(job) && job.Route == "language") text = LanguageText(text);
        return new(text, null, job.Settings.ContextEnabled ? "unavailable" : "generated_allowed", null,
            (NeedsOcr(job) ? "图片 OCR " : "原图 ") + job.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 0, job.CreatedAt)
        {
            FrontImage = preserveImage ? job.Questions.Single() : null,
            VisualQuestion = preserveImage && job.Route == "question" && !job.Quick
        };
    }

    private static string LanguageText(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        char fence = '\0'; var fenceLength = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var marker = System.Text.RegularExpressions.Regex.Match(lines[i], @"^ {0,3}(`{3,}|~{3,})(.*)$");
            if (marker.Success)
            {
                var run = marker.Groups[1].Value;
                if (fence == '\0') { fence = run[0]; fenceLength = run.Length; }
                else if (run[0] == fence && run.Length >= fenceLength && string.IsNullOrWhiteSpace(marker.Groups[2].Value)) fence = '\0';
                continue;
            }
            if (fence != '\0') continue;
            var heading = System.Text.RegularExpressions.Regex.Match(lines[i], @"^ {0,3}#{1,6}(?:[ \t]+|$)(.*)$");
            if (heading.Success)
                lines[i] = System.Text.RegularExpressions.Regex.Replace(heading.Groups[1].Value, @"[ \t]+#+[ \t]*$", "");
        }
        return string.Join('\n', lines).Trim();
    }
}

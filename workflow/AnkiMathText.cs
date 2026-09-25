using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public static class AnkiMathText
{
    // Keep prose paragraphs and TeX row separators; HTML export must not insert <br> inside math.
    public static string Normalize(string text) => Regex.Replace(text,
        @"(?<!\\)\\\(.*?(?<!\\)\\\)|(?<!\\)\\\[.*?(?<!\\)\\\]", match =>
        {
            // Remove TeX line comments before collapsing their line endings; preserve escaped percent signs.
            var formula = Regex.Replace(match.Value, @"(?<!\\)((?:\\\\)*)%[^\r\n]*", "$1");
            return Regex.Replace(formula, @"[\t\r\n]+", " ");
        }, RegexOptions.Singleline);
}

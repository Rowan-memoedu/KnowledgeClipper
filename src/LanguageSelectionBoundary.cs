using System;

namespace KnowledgeClipper;

// Input segments must be adjacent, verified source text. Never infer missing text.
internal static class LanguageSelectionBoundary
{
    internal sealed record Result(string Text, string Prefix, string Suffix);

    internal static Result Complete(string selected, string prefix, string suffix)
    {
        if (string.IsNullOrEmpty(selected) || prefix == null || suffix == null)
            return new Result(selected, prefix, suffix);
        var source = prefix + selected + suffix;
        var start = prefix.Length;
        var end = start + selected.Length;
        var left = start;
        var right = end;
        if (IsLetter(selected[0])) while (left > 0 && IsWordCharacter(source, left - 1)) left--;
        if (IsLetter(selected[^1])) while (right < source.Length && IsWordCharacter(source, right)) right++;
        // UIA caps each adjacent segment at 4000 characters. Reaching that cap
        // without a visible word boundary cannot prove that a word is complete.
        if (left == 0 && prefix.Length >= 4000) left = start;
        if (right == source.Length && suffix.Length >= 4000) right = end;
        return new Result(source[left..right], source[..left], source[right..]);
    }

    private static bool IsLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsWordCharacter(string source, int index) => IsLetter(source[index]) ||
        (source[index] is '\'' or '’' or '-') && index > 0 && index + 1 < source.Length &&
        IsLetter(source[index - 1]) && IsLetter(source[index + 1]);
}

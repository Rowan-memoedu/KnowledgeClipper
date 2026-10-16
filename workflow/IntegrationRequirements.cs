using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

internal static partial class IntegrationRequirements
{
    [GeneratedRegex(@"(?<count>[0-9零〇一二两三四五六七八九十百]+)\s*(?:张|个)\s*(?:知识卡片|学习卡片|记忆卡片|卡片|知识卡|学习卡|记忆卡|卡)|(?:生成|制作|输出|整合成|整理成|分成|拆成|只要|需要)\s*(?<count>[0-9零〇一二两三四五六七八九十百]+)\s*张(?=\s*(?:$|[。，,；;！!？?\n]))|(?<count>[0-9]+)\s+(?:flashcards?|cards?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountPattern();
    [GeneratedRegex(@"(?:第|至少|最多|最少|至多|不少于|不多于|不超过|大于|小于|大约|约|至|到|或|[-~～–—]|at least|at most|up to)\s*(?:(?:生成|制作|输出)\s*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonExactPrefix();
    [GeneratedRegex(@"(?:不要|不需要|不必|不能|不应|不用|不是|而非|避免|不限定|不限制|不限|不一定|不固定|不要求|无需|不局限|rather than|\bnot\b)[^。；;\n，,]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedClause();

    internal static int? CardCount(string requirements)
    {
        var text = requirements.Normalize(NormalizationForm.FormKC);
        var counts = CountPattern().Matches(text).Cast<Match>()
            .Where(match => !NonExactPrefix().IsMatch(text[..match.Index]) && !NegatedClause().IsMatch(text[..match.Index]))
            .Select(match => Number(match.Groups["count"].Value)).Distinct().ToArray();
        if (counts.Length > 1) throw new InvalidDataException("补充要求中有不同的卡片数量，请明确本次生成几张卡。");
        if (counts.Length == 0) return null;
        if (counts[0] is < 1 or > 20) throw new InvalidDataException("每次整合支持 1 至 20 张卡，请调整补充要求中的数量。");
        return counts[0];
    }

    private static int Number(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var integer)) return integer;
        var result = 0; var digit = 0;
        foreach (var character in value)
        {
            if (character is '十' or '百') { result += (digit == 0 ? 1 : digit) * (character == '十' ? 10 : 100); digit = 0; }
            else digit = character switch { '零' or '〇' => 0, '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4,
                '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => throw new InvalidDataException("卡片数量无效。") };
        }
        return result + digit;
    }
}

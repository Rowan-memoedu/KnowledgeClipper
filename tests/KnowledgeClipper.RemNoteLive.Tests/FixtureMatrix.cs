using System.Text.Json;
using KnowledgeClipper.Workflow;

// Independent, handwritten expectations. No expected face is derived from the production converter.
internal sealed record ExpectedCloze(string Text, int Group, string? Hint = null);
internal sealed record ExpectedMath(string Text, bool Block = false);
internal sealed record ExpectedNode(string Text, int[] Images, ExpectedCloze[] Clozes, ExpectedMath[] Math);
internal sealed record ExpectedCard(string Key, string Kind, ExpectedNode Front, ExpectedNode[] Back, int ReviewCards);
internal sealed record ExpectedFixture(string Key, string ModeId, string[] DocumentPath, ExpectedCard[] Cards,
    ExportDestination Destination = ExportDestination.RemNote);

internal static class FixtureMatrix
{
    internal const string FixedDataRoot = @"D:\Caches\KnowledgeClipper\standalone-remnote-live";
    private static ExpectedNode N(string text = "", int[]? images = null, ExpectedCloze[]? clozes = null, ExpectedMath[]? math = null) =>
        new(text, images ?? [], clozes ?? [], math ?? []);
    private static ExpectedCard B(ExpectedNode front, ExpectedNode[] back, string key = "card.0") => new(key, "basic", front, back, 1);
    private static ExpectedCard P(ExpectedNode front, int index) => new("plain." + index, "plain", front, [], 0);
    private static ExpectedCard C(ExpectedNode front, ExpectedNode[]? back = null, int count = 1) => new("card.0", "cloze", front, back ?? [], count);
    private const string Source = "Mercury and Venus orbit the Sun.\nEarth is another planet.";
    private const string FirstQuestion = "What is the perimeter of a 7 cm by 9 cm rectangle?";
    private const string FirstAnswer = "The perimeter is 2 × (7 + 9) = 32 cm.";
    internal static readonly ExpectedFixture[] All = [
        new("raw", "excerpt-manual", ["摘录"], [P(N("Mercury and Venus orbit the Sun."), 0), P(N("Earth is another planet."), 1)]),
        new("manual-basic", "excerpt-manual", ["摘录"], [B(N("A rectangle has width 7 cm.\nIts length is 9 cm."), [N("Perimeter: 32 cm."), N("Area: 63 square cm.")])]),
        new("single-cloze", "excerpt-manual", ["摘录"], [C(N(Source, clozes: [new("Mercury", 1)]))]),
        new("merged-cloze", "excerpt-manual", ["摘录"], [C(N(Source, clozes: [new("Mercury", 1), new("Venus", 1)]))]),
        new("math", "excerpt-manual", ["摘录"], [B(N("Evaluate 7+9.", math: [new("7+9")]), [N("Addition gives 7+9=16.", math: [new("7+9=16")]), N("2(7+9)=32", math: [new("2(7+9)=32", true)])])]),
        new("math-cloze", "excerpt-manual", ["摘录"], [C(N("The theorem is a^2+b^2=c^2.", clozes: [new("a^2+b^2=c^2", 1, "equation")], math: [new("a^2+b^2=c^2")]), [N("Right triangles only.")])]),
        new("independent-cloze", "excerpt-manual", ["摘录"], [C(N("Copper has symbol Cu; silver has Ag; repeat copper: Cu.", clozes: [new("Cu", 1, "symbol"), new("Ag", 2, "symbol"), new("Cu", 1, "symbol")]), [N("Cu is copper."), N("Ag is silver.")], 2)]),
        new("images", "image-original", ["图片", "原图"], [B(N("\n\n", [0, 1]), [N(images: [2]), N(images: [3])])]),
        new("images-fresh", "image-original", ["图片", "原图"], [B(N("\nIndependent image export.", [1]), [N(images: [3]), N("Answer image.")])]),
        new("image-front-only", "image-original", ["图片", "原图"], [P(N(images: [0]), 0), P(N(images: [1]), 1)]),
        new("image-answer", "image-answer", ["图片", "题目解答"], [B(N("\n\n", [0, 1]), [N("Given: width 7 cm and length 9 cm."), N("Perimeter: 2(7+9)=32 cm.", math: [new("2(7+9)=32")]), N(@"Area: 7\times9=63 square cm.", math: [new(@"7\times9=63")])])]),
        new("ocr-text", "excerpt-manual", ["摘录"], [P(N("Water freezes at zero degrees Celsius."), 0), P(N("Pure water boils at one hundred degrees Celsius."), 1)]),
        new("translation", "translation", ["翻译"], [B(N("学习把想法联系起来。"), [N("Learning connects ideas.")])]),
        new("question-0", "question", ["问答"], [B(N(FirstQuestion), [N(FirstAnswer)])]),
        new("question-1", "question", ["问答"], [B(N("第 1 轮\n问：" + FirstQuestion + "\n答：" + FirstAnswer + "\nWhat is its area?"), [N("The area is 7 × 9 = 63 square cm.")])]),
        new("language-en", "language-en", ["语言学习", "英语", "理解与朗读"], [B(N("Planets orbit the Sun."), [N("/ˈɔːrbɪt/"), N("To move around a star or planet."), N("行星围绕太阳运行。")])]),
        new("language-writing-en", "language-writing-en", ["语言学习", "英语", "书写练习"], [B(N("Planets /ˈɔːrbɪt/ the Sun.\nTo move around a star or planet."), [N("orbit"), N("行星围绕太阳运行。")], "card.1")]),
        new("language-zh", "language-zh", ["语言学习", "汉语", "理解与朗读"], [B(N("学习需要持之以恒。"), [N("持之以恒"), N("chí zhī yǐ héng"), N("长久坚持，不轻易放弃。")])]),
        new("language-writing-zh", "language-writing-zh", ["语言学习", "汉语", "书写练习"], [B(N("学习需要（chí zhī yǐ héng；长久坚持，不轻易放弃。）。"), [N("持之以恒")], "card.1")]),
        new("history-added", "excerpt-manual", ["摘录"], [P(N("Practice makes a skill easier to recall."), 0)]),
        new("anki-language", "language-en", [], [B(N("We learn through practice."), [N("/lɜːrn/"), N("To gain knowledge or skill."), N("我们通过练习学习。")])], ExportDestination.Anki)
    ];

    internal static ExpectedFixture For(string key) => All.Single(item => item.Key == key);
    internal static void CheckPlan(string key, ClipDraft draft, FixtureSet fixtures, Action<bool, string> check)
    {
        var expected = For(key);
        check(draft.ModeId == expected.ModeId && ExportRouting.For(draft) == expected.Destination, key + ": mode and destination match the independent matrix");
        check(draft.ConfirmedNote != null && draft.ConfirmedNote.Tags.Contains("kc_" + draft.Id), key + ": frozen source tag matches the draft identity");
        if (expected.Destination == ExportDestination.Anki)
        {
            var note = draft.ConfirmedNote!; var faces = note.PublishedFaces(0);
            check(draft.Settings.ModeDestinations.Count == 0 && note.Ordinals.SequenceEqual([0]) && faces.Front == expected.Cards[0].Front.Text &&
                faces.Back == string.Join("\n", expected.Cards[0].Back.Select(node => node.Text)), key + ": default Anki published reading faces and ordinal are unchanged");
            return;
        }
        var plan = draft.ConfirmedRemNote ?? throw new InvalidDataException("Missing frozen export plan: " + key);
        check(plan.SourceId == "knowledgeclipper." + draft.Id && plan.RootTitle == "零碎知识剪藏" && plan.DocumentPath.SequenceEqual(expected.DocumentPath),
            key + ": exact frozen identity and mode directory");
        check(plan.Cards.Length == expected.Cards.Length, key + ": expected number of top-level Rem plans");
        for (var index = 0; index < expected.Cards.Length; index++)
        {
            var card = plan.Cards[index]; var wanted = expected.Cards[index];
            check(card.Key == wanted.Key && card.Kind == wanted.Kind && card.Back.Length == wanted.Back.Length, key + ": card " + index + " has the expected kind, key, and answer row count");
            CheckPlanNode(card.Front, wanted.Front, fixtures, check, key + ": front " + index);
            for (var row = 0; row < wanted.Back.Length; row++) CheckPlanNode(card.Back[row], wanted.Back[row], fixtures, check, key + ": answer " + index + "/" + row);
        }
    }

    private static void CheckPlanNode(RemNoteRichPart[] actual, ExpectedNode wanted, FixtureSet fixtures, Action<bool, string> check, string label)
    {
        check(string.Concat(actual.Select(part => part.Text)) == wanted.Text, label + " preserves exact text and line breaks");
        check(actual.Where(part => part.Kind == "image").Select(part => part.Sha256).SequenceEqual(wanted.Images.Select(index => fixtures.Manifest.Images[index].Sha256)), label + " preserves ordered source image hashes");
        check(actual.Where(part => part.ClozeId != null).Select(part => new ExpectedCloze(part.Text!, part.ClozeId!.Value, part.Hint)).SequenceEqual(wanted.Clozes), label + " preserves cloze groups and hints");
        check(actual.Where(part => part.Kind == "latex").Select(part => new ExpectedMath(part.Text!, part.Block ?? false)).SequenceEqual(wanted.Math), label + " preserves native inline/block formula parts");
    }

    internal static void CheckReadback(string key, JsonElement actual, FixtureSet fixtures, Action<bool, string> check)
    {
        var expected = For(key); var cards = actual.GetProperty("cards").EnumerateArray().ToArray();
        check(cards.Length == expected.Cards.Length, key + ": raw SDK top-level content count");
        for (var index = 0; index < expected.Cards.Length; index++)
        {
            var card = cards[index]; var wanted = expected.Cards[index]; var nodes = card.GetProperty("nodes").EnumerateArray().ToArray();
            check(card.GetProperty("cardIds").GetArrayLength() == wanted.ReviewCards && nodes.Length == wanted.Back.Length + 1, key + ": independently expected review-card and answer-node counts");
            var types = nodes[0].GetProperty("cardTypes").EnumerateArray().ToArray();
            check(wanted.Kind == "plain" ? types.Length == 0 : wanted.Kind == "basic" ? types.Length == 1 && types[0].GetString() == "forward" :
                types.Select(type => type.GetProperty("clozeId").GetString()).Order().SequenceEqual(wanted.Front.Clozes.Select(cloze => "kc_" + cloze.Group).Distinct().Order()),
                key + ": native practice types and cloze groups match the independent expectation");
            check(nodes.Skip(1).All(node => node.GetProperty("cardIds").GetArrayLength() == 0 &&
                (wanted.Kind == "basic" ? node.GetProperty("isCardItem").GetBoolean() && !node.GetProperty("isExtraCardDetail").GetBoolean() :
                    node.GetProperty("isExtraCardDetail").GetBoolean() && !node.GetProperty("isCardItem").GetBoolean())),
                key + ": answer children keep the expected multiline-card or extra-detail role");
            check(nodes[0].GetProperty("parentId").GetString() == actual.GetProperty("documentRemId").GetString(), key + ": content belongs directly to its verified mode document");
            CheckRawNode(nodes[0], wanted.Front, fixtures, check, key + ": raw front " + index);
            for (var row = 0; row < wanted.Back.Length; row++) CheckRawNode(nodes[row + 1], wanted.Back[row], fixtures, check, key + ": raw answer " + index + "/" + row);
        }
    }

    private static void CheckRawNode(JsonElement node, ExpectedNode wanted, FixtureSet fixtures, Action<bool, string> check, string label)
    {
        var parts = node.GetProperty("text").EnumerateArray().ToArray();
        string Text(JsonElement part) => part.ValueKind == JsonValueKind.String ? part.GetString()! : part.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        check(string.Concat(parts.Select(Text)) == wanted.Text, label + " has the literal expected text and LF inside one Rem");
        var media = node.GetProperty("media").EnumerateArray().OrderBy(item => item.GetProperty("elementIndex").GetInt32()).ToArray();
        check(media.Select(item => item.GetProperty("sha256").GetString()).SequenceEqual(wanted.Images.Select(index => fixtures.Manifest.Images[index].Sha256)), label + " has the independently expected managed-image byte order");
        var clozes = parts.Where(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("cId", out _)).ToArray();
        check(clozes.Length == wanted.Clozes.Length && clozes.Zip(wanted.Clozes).All(pair => Text(pair.First) == pair.Second.Text &&
            pair.First.GetProperty("cId").GetString() == "kc_" + pair.Second.Group &&
            (pair.First.TryGetProperty("cloze-hint", out var hint) ? hint.GetString() : null) == pair.Second.Hint), label + " has the expected native cloze linkage and hints");
        check(parts.Where(part => part.ValueKind == JsonValueKind.Object && part.GetProperty("i").GetString() == "x")
            .Select(part => new ExpectedMath(Text(part), part.TryGetProperty("block", out var block) && block.GetBoolean())).SequenceEqual(wanted.Math), label + " has the expected native inline/block formulas");
    }

    internal static object Summary() => new {
        remNoteSources = All.Count(item => item.Destination == ExportDestination.RemNote),
        topLevelContentRems = All.Where(item => item.Destination == ExportDestination.RemNote).Sum(item => item.Cards.Length),
        answerChildRems = All.Where(item => item.Destination == ExportDestination.RemNote).Sum(item => item.Cards.Sum(card => card.Back.Length)),
        remNoteReviewCards = All.Where(item => item.Destination == ExportDestination.RemNote).Sum(item => item.Cards.Sum(card => card.ReviewCards)),
        ankiNotes = 1, ankiReviewCards = 1,
        leafDirectories = All.Where(item => item.Destination == ExportDestination.RemNote).Select(item => string.Join(" / ", item.DocumentPath)).Distinct().Order().ToArray(),
        documentsIncludingRoot = 1 + All.Where(item => item.Destination == ExportDestination.RemNote).SelectMany(item => Enumerable.Range(1, item.DocumentPath.Length).Select(count => string.Join(" / ", item.DocumentPath.Take(count)))).Distinct().Count()
    };
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var checks = new List<string>();
void Check(bool pass, string name)
{
    if (!pass) throw new Exception(name);
    checks.Add(name); Console.WriteLine("PASS " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("Expected conversion rejection: " + name);
}
AnkiNoteDraft Basic(string front, string back, string? context = null)
{
    var fields = new Dictionary<string, string> { ["Front"] = AnkiNoteDraft.Html(front), ["Back"] = AnkiNoteDraft.Html(back) };
    if (context != null) fields["Context"] = AnkiNoteDraft.Html(context);
    return new("Fixture", context == null ? AnkiNoteDraft.ExcerptModelName : AnkiNoteDraft.QuestionModelName, fields, [], [0]);
}
RemNoteCardPlan[] Convert(AnkiNoteDraft note) => RemNoteCardConversion.Convert(note);
string Text(IEnumerable<RemNoteRichPart> parts) => string.Concat(parts.Select(p => p.Text));
string Row(RemNoteCardPlan card, int index) => Text(card.Back[index]);

var basic = Convert(Basic("First line\r\nSecond line", "Answer one\r\nAnswer two"));
Check(basic.Length == 1 && basic[0].Kind == "basic" && basic[0].Front.Length == 1 &&
    Text(basic[0].Front) == "First line\nSecond line", "basic front stays one soft-line-break node");
Check(basic[0].Back.Length == 2 && Row(basic[0], 0) == "Answer one" && Row(basic[0], 1) == "Answer two",
    "basic multiline answer becomes ordered child rows");
var question = Convert(Basic("Current question?\nSupplement", "Current answer.", "Earlier question?\nEarlier answer."));
Check(question.Length == 1 && Text(question[0].Front) == "Earlier question?\nEarlier answer.\nCurrent question?\nSupplement" &&
    Row(question[0], 0) == "Current answer.", "question context and question share a single front");
var plain = Convert(Basic("😀 Alpha & beta.\n\nSecond line", " \n\t"));
Check(plain.Length == 2 && plain.All(p => p.Kind == "plain" && p.Back.Length == 0) &&
    plain.Select(p => p.Key).SequenceEqual(["plain.0", "plain.1"]), "empty basic answer becomes parallel plain nodes with stable keys");
Check(Text(plain[0].Front) == "😀 Alpha & beta." && Text(plain[1].Front) == "Second line",
    "plain conversion preserves emoji and removes only empty separator nodes");
var literal = Convert(Basic("Literal <br> <img src=\"literal.png\"> &amp; {{Front}}", "Use {{c1::literal}} and &lt;b&gt;."));
Check(Text(literal[0].Front) == "Literal <br> <img src=\"literal.png\"> &amp; {{Front}}" &&
    Row(literal[0], 0) == "Use {{c1::literal}} and &lt;b&gt;." && literal[0].Front.All(p => p.Kind == "text"),
    "literal HTML entities and Anki braces decode exactly once without becoming syntax");
var mixedNewlines = Convert(Basic("A\rB\r\nC\nD", "E\rF"));
Check(Text(mixedNewlines[0].Front) == "A\nB\nC\nD" && mixedNewlines[0].Back.Length == 2,
    "all source newline forms normalize to LF");
Reject(() => Convert(Basic(" \n", "")), "empty front is rejected before export");
Reject(() => Convert(Basic("front", "back") with { Ordinals = [1] }), "unknown basic template ordinal is rejected");
Reject(() => Convert(Basic("front", "back") with { Fields = new() { ["Front"] = "<b>raw HTML</b>", ["Back"] = "answer" } }),
    "unsupported raw HTML cannot be silently stripped");
Reject(() => Convert(Basic("front", "back") with { Fields = new() { ["Front"] = "unclosed <img", ["Back"] = "answer" } }),
    "malformed raw HTML cannot be silently stripped");

var math = Convert(Basic(@"For \(x < 2\), solve:", "Step one\n\\[\nx+y=3\\\\\nx-y=1\n\\]\nStep two"));
Check(math[0].Front.Single(p => p.Kind == "latex") is { Text: "x < 2", Block: false } &&
    math[0].Back.Length == 3, "inline mathematics becomes a native non-block formula and display math stays one row");
Check(math[0].Back[1].Single() is { Kind: "latex", Text: "\nx+y=3\\\\\nx-y=1\n", Block: true },
    "display formula preserves TeX newlines and row separators without delimiter text");
var dollarMath = Convert(Basic("Value", "Before\n$$\na+b\n$$\nAfter"));
Check(dollarMath[0].Back.Length == 3 && dollarMath[0].Back[1].Single() is { Kind: "latex", Text: "\na+b\n", Block: true },
    "paired double-dollar display formula remains one child row");
var money = Convert(Basic("Costs $20 and $30", "Money is literal"));
Check(money[0].Front.All(p => p.Kind == "text"), "ordinary currency is not interpreted as inline mathematics");
var escapedMath = Convert(Basic(@"Literal \\(x\\)", "Answer"));
Check(escapedMath[0].Front.All(p => p.Kind == "text") && Text(escapedMath[0].Front) == @"Literal \\(x\\)",
    "escaped math delimiters stay literal text");
var plainMath = Convert(Basic("Before\n\\[\nx=1\n\\]\nAfter", ""));
Check(plainMath.Length == 3 && plainMath[1].Front.Single() is { Kind: "latex", Block: true },
    "front-only display formula is one plain node despite internal line breaks");

AnkiNoteDraft Cloze(string front, string back = "") => Basic(front, back) with { Model = AnkiNoteDraft.ExcerptClozeModelName };
var cloze = Convert(Cloze("{{c1::Alpha::first}} and {{c1::beta}} follow {{c2::gamma & delta::group}}."));
Check(cloze.Length == 1 && cloze[0].Kind == "cloze" && cloze[0].Back.Length == 0,
    "cloze is recognized before the empty-answer plain-text rule");
var spans = cloze[0].Front.Where(p => p.Kind == "cloze").ToArray();
Check(spans.Select(p => p.ClozeId).SequenceEqual([1, 1, 2]) && spans.Select(p => p.Text).SequenceEqual(["Alpha", "beta", "gamma & delta"]),
    "merged cloze spans preserve group identifiers and order");
Check(spans[0].Hint == "first" && spans[1].Hint == null && spans[2].Hint == "group",
    "cloze hints are preserved independently from hidden text");
var clozeBack = Convert(Cloze("Before {{c1::line one\nline two}} after.", "Explanation one\nExplanation two"));
Check(clozeBack[0].Front.Single(p => p.Kind == "cloze").Text == "line one\nline two" && clozeBack[0].Back.Length == 2,
    "cloze soft newlines and optional multiline answer remain distinct");
var clozeMath = Convert(Cloze(@"{{c1::value \(x+1\) here::hint}}"));
Check(clozeMath[0].Front.Select(p => p.Kind).SequenceEqual(["cloze", "latex", "cloze"]) &&
    clozeMath[0].Front.All(p => p.ClozeId == 1 && p.Hint == "hint") && clozeMath[0].Front[1].Text == "x+1",
    "math inside a cloze shares its native group without literal delimiters");
var pureMathCloze = Convert(Cloze(@"{{c2::\[x=1\]::equation}}"));
Check(pureMathCloze[0].Front.Single() is { Kind: "latex", ClozeId: 2, Hint: "equation", Block: true, Text: "x=1" },
    "a pure formula cloze retains its group and hint in the export plan");
Reject(() => Convert(Cloze("No cloze markup")), "a cloze model without valid spans is rejected");
Reject(() => Convert(Cloze("{{c0::zero}}")), "invalid zero cloze identifiers are rejected");
Reject(() => Convert(Cloze("{{c99999999999999::overflow}}")), "overflowing cloze identifiers are rejected");
Reject(() => Convert(Cloze("{{c1::outer {{c2::inner}}}}")), "nested cloze syntax is rejected without partial conversion");

AnkiNoteDraft Language(bool published, bool english, int[]? ordinals = null)
{
    var fields = new Dictionary<string, string>
    {
        ["Target"] = AnkiNoteDraft.Html(english ? "lead" : "学习"), ["Pronunciation"] = english ? "/liːd/" : "xué xí",
        ["Meaning"] = AnkiNoteDraft.Html("学习 & 掌握"), ["SentenceBefore"] = english ? "They " : "我在",
        ["SentenceAfter"] = english ? " the team." : "。", ["Origin"] = "来源原文",
        ["ReadingEnabled"] = "1", ["WritingEnabled"] = "1", ["EnglishMode"] = english ? "1" : "",
        ["ExplanationEn"] = "guide a team", ["SentenceTranslationZh"] = "他们领导团队。"
    };
    if (published) fields["LanguageFormat"] = PublishedLanguage.Format;
    return new("Fixture", published ? PublishedLanguage.ModelName("Language fixture") : "Language fixture", fields, [], ordinals ?? [0, 1]);
}
var en = Convert(Language(true, true));
Check(en.Length == 2 && en.Select(p => p.Key).SequenceEqual(["card.0", "card.1"]) &&
    en.All(p => p.Kind == "basic"), "published language emits only declared native template ordinals");
Check(Text(en[0].Front) == "They lead the team." && en[0].Back.Select(Text).SequenceEqual(["/liːd/", "guide a team", "他们领导团队。"]),
    "published English reading uses the exact published faces");
Check(Text(en[1].Front) == "They /liːd/ the team.\nguide a team" && en[1].Back.Select(Text).SequenceEqual(["lead", "他们领导团队。"]),
    "published English writing preserves a single multiline front");
var zh = Convert(Language(true, false, [1, 0]));
Check(zh.Select(p => p.Key).SequenceEqual(["card.1", "card.0"]) && Text(zh[0].Front) == "我在（xué xí；学习 & 掌握）。" &&
    zh[0].Back.Length == 1 && Row(zh[0], 0) == "学习", "published Chinese follows declared order and writing semantics");
Check(zh[1].Back.Select(Text).SequenceEqual(["学习", "xué xí", "学习 & 掌握"]), "published Chinese reading preserves all answer lines");
var legacy = Convert(Language(false, true));
Check(Text(legacy[0].Front) == "They lead the team.\n来源原文" &&
    legacy[0].Back.Select(Text).SequenceEqual(["lead · /liːd/", "学习 & 掌握"]),
    "legacy reading maps the existing template without changing its answer");
Check(Text(legacy[1].Front) == "They ［/liːd/ · 学习 & 掌握］ the team.\n来源原文" &&
    legacy[1].Back.Select(Text).SequenceEqual(["lead", "They lead the team."]),
    "legacy writing preserves hints origin and complete sentence answer");
Check(Convert(Language(true, true, [1])).Single().Key == "card.1", "single-mode language exports no disabled second card");
var disabled = Language(true, true, [1]); disabled.Fields["WritingEnabled"] = "";
Reject(() => Convert(disabled), "declared but disabled language template is rejected");
var missing = Language(true, true); missing.Fields.Remove("Pronunciation");
Reject(() => Convert(missing), "incomplete published language fields are rejected");
Reject(() => Convert(Language(false, true, [0, 0])), "duplicate template ordinals are rejected");

var data = Path.Combine(@"D:\Caches\KnowledgeClipper\remnote-sdk\conversion-fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
ImageAsset Image(string name, string role, int index, byte red, byte green, byte blue)
{
    var bytes = Png(red, green, blue); var path = Path.Combine(data, name + ".png"); File.WriteAllBytes(path, bytes);
    return new(path, ImageAsset.Hash(bytes), role, index);
}
var questionOne = Image("公开题图 一", "question", 1, 200, 10, 20);
var questionTwo = Image("public question 2", "question", 2, 10, 200, 30);
var answer = Image("public answer", "answer", 1, 10, 20, 200);
var images = Basic("Picture question", "Picture answer") with
{
    Fields = new()
    {
        ["Front"] = "<div>" + questionTwo.Html + "</div><div>" + questionOne.Html + "</div>" + AnkiNoteDraft.Html("Picture question"),
        ["Back"] = "<div>" + answer.Html + "</div>" + AnkiNoteDraft.Html("Picture answer\nFurther detail")
    },
    Media = [questionOne, answer, questionTwo]
};
var imagePlan = Convert(images).Single();
Check(imagePlan.Kind == "basic" && imagePlan.Front.Where(p => p.Kind == "image").Select(p => p.Sha256)
    .SequenceEqual([questionTwo.Sha256, questionOne.Sha256]), "multiple front images follow actual card-face order rather than asset array order");
Check(imagePlan.Back.Length == 3 && imagePlan.Back[0].Single().Sha256 == answer.Sha256 &&
    Row(imagePlan, 1) == "Picture answer" && Row(imagePlan, 2) == "Further detail", "answer images and answer text preserve their original side and order");
Check(imagePlan.Front.Where(p => p.Kind == "image").All(p => new Uri(p.Uri!).IsFile) &&
    new Uri(imagePlan.Front.First(p => p.Kind == "image").Uri!).LocalPath == questionTwo.Path,
    "image plans carry reversible absolute file URIs including spaces and Unicode");
var onlyImage = Convert(Basic("", "") with { Fields = new() { ["Front"] = "<div>" + questionOne.Html + "</div>", ["Back"] = "" }, Media = [questionOne] });
Check(onlyImage.Single() is { Kind: "plain", Back.Length: 0 } && onlyImage[0].Front.Single().Kind == "image",
    "front-only image imports as a normal text node rather than an empty-answer card");
var imageAnswer = Convert(Basic("Question", "") with { Fields = new() { ["Front"] = "Question", ["Back"] = answer.Html }, Media = [answer] });
Check(imageAnswer.Single().Kind == "basic" && imageAnswer[0].Back[0].Single().Kind == "image",
    "an image-only answer is a valid card answer");
var repeatedImage = Convert(images with { Fields = new(images.Fields) { ["Back"] = answer.Html + questionOne.Html } });
Check(repeatedImage.Single().Back.Single().Where(p => p.Kind == "image").Select(p => p.Sha256).SequenceEqual([answer.Sha256, questionOne.Sha256]),
    "the same verified image may appear on both faces without losing either occurrence");
Reject(() => Convert(images with { Media = [questionOne, questionTwo] }), "unknown image references cannot be dropped");
Reject(() => Convert(Basic("front", "back") with { Media = [answer] }), "unused attached media cannot be silently dropped");
Reject(() => Convert(Basic("front", "back") with { Fields = new() { ["Front"] = "<img src=\"https://example.invalid/untracked.png\">", ["Back"] = "back" } }),
    "external image references must have verified local assets");
Reject(() => Convert(Language(true, true) with { Media = [answer] }), "unsupported language media is rejected");
var originalBytes = File.ReadAllBytes(questionOne.Path); File.AppendAllText(questionOne.Path, "tampered");
Reject(() => Convert(images), "media hash drift stops conversion before any external write");
File.WriteAllBytes(questionOne.Path, originalBytes);
var first = JsonSerializer.Serialize(Convert(images), WorkflowJson.Options);
var second = JsonSerializer.Serialize(Convert(images), WorkflowJson.Options);
Check(first == second, "conversion and plan serialization are deterministic");
var roundTrip = JsonSerializer.Deserialize<RemNoteCardPlan[]>(first, WorkflowJson.Options)!;
Check(JsonSerializer.Serialize(roundTrip, WorkflowJson.Options) == first, "workflow JSON round-trip preserves images and rich parts");
var receipt = args.Length > 0 ? Path.GetFullPath(args[0]) : @"D:\Artifacts\KnowledgeClipper\remnote-sdk\validation\conversion.json";
WorkflowJson.Save(receipt, new { passed = true, checks = checks.Count, scope = "Public local fixtures; deterministic conversion only; no RemNote, Anki, or model calls.", checksPassed = checks, fixtureDirectory = data });
Console.WriteLine($"PASS {checks.Count} conversion checks; receipt: {receipt}");

static byte[] Png(byte red, byte green, byte blue)
{
    using var png = new MemoryStream(); png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 1);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 1); header[8] = 8; header[9] = 6;
    Chunk("IHDR", header);
    using var encoded = new MemoryStream();
    using (var compressor = new ZLibStream(encoded, CompressionLevel.SmallestSize, leaveOpen: true))
        compressor.Write(new byte[] { 0, red, green, blue, 255 });
    Chunk("IDAT", encoded.ToArray()); Chunk("IEND", []);
    return png.ToArray();
    void Chunk(string type, byte[] payload)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type); var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, payload.Length); png.Write(size); png.Write(typeBytes); png.Write(payload);
        uint crc = uint.MaxValue;
        foreach (var value in typeBytes.Concat(payload))
        {
            crc ^= value;
            for (var index = 0; index < 8; index++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(size, crc ^ uint.MaxValue); png.Write(size);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

if (args.Length == 2 && args[0] == "--live") { await LiveExcerptCheck.RunAsync(args[1]); return; }
if (args.Length == 2 && args[0] == "--live-media") { await ImageChecks.ImportSavedImagesAsync(args[1]); return; }
if (args.Length == 5 && args[0] == "--resume-ocr") { await ImageChecks.ResumeOcrAsync(args[1], args[2], args[3], args[4]); return; }
if (args.Length == 3 && args[0] == "--ocr-parallel") { await OcrParallelChecks.RunAsync(args[1], args[2]); return; }
if (args.Length == 5 && args[0] == "--live-ocr-parallel") { await OcrParallelLive.RunAsync(args[1], args[2], args[3], args[4]); return; }
if (args.Length == 3 && args[0] == "--image-entries") { await ImageEntryChecks.RunAsync(args[1], args[2]); return; }
if (args.Length >= 3 && args[0] == "--images") { await ImageChecks.RunAsync(args[1], args[2], args.Contains("--live")); return; }
if (args.Length == 3 && args[0] == "--saved-watermarks")
{
    var matching = 0;
    foreach (var file in Directory.EnumerateFiles(args[1], "*.json"))
    {
        var saved = WorkflowJson.Read<ExcerptSession>(file);
        if (!saved.Original.SelectedText.Contains("著作权归作者所有。商业转载请联系作者获得授权", StringComparison.Ordinal)) continue;
        matching++;
        var clean = ExcerptTextCleanup.Clean(saved.Original);
        if (ReferenceEquals(clean, saved.Original) || !saved.Original.SelectedText.StartsWith(clean.SelectedText, StringComparison.Ordinal) ||
            clean.SelectedText.Contains("来源：知乎", StringComparison.Ordinal) ||
            clean.SelectedText.Contains("著作权归作者所有", StringComparison.Ordinal))
            throw new InvalidDataException("已保存摘录中的知乎尾注未按预期清理。");
    }
    if (matching < 7) throw new InvalidDataException("未找齐已确认的七条知乎尾注实例。");
    WorkflowJson.Save(args[2], new { passed = true, matchedSavedSessions = matching, mode = "read-only original session checks" });
    Console.WriteLine($"PASS {matching} saved Zhihu watermarks cleaned without changing the source files");
    return;
}
var project = args[0]; var receipt = args[1];
var checks = new List<string>();
void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
async Task Reject(Func<Task> action, string message)
{ try { await action(); } catch { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
Task Do(Action action) { action(); return Task.CompletedTask; }
var data = Path.Combine(@"D:\Caches\KnowledgeClipper\excerpt-tests", Guid.NewGuid().ToString("N"));
var store = new ExcerptStore(data);
var settings = new LanguageSettings { AnkiRootDeck = "Excerpt fixture", AnkiDeck = "Excerpt fixture" };
CaptureMaterial Material(string text) => new(text, null, "unavailable", null, "Public excerpt fixture", 0, DateTimeOffset.UtcNow);
var zhihuFooter = "\n\n作者：公开示例\n链接：https://www.zhihu.com/question/123/answer/456\n来源：知乎\n著作权归作者所有。商业转载请联系作者获得授权，非商业转载请注明出处。";
var zhihu = ExcerptCards.Create(Material("正文中的版权讨论应保留。" + zhihuFooter), settings, store);
Check(zhihu.Original.SelectedText == "正文中的版权讨论应保留。" && zhihu.ManualFront == zhihu.Original.SelectedText &&
    store.Cards.Find(zhihu.Cards[0].Id)!.Excerpt!.Front == zhihu.Original.SelectedText,
    "Zhihu attribution footer is removed before storing the source and raw excerpt card");
var withContext = Material("上下文摘录。" + zhihuFooter) with
    { ContextText = "前文。上下文摘录。" + zhihuFooter + "后文。", ContextOrigin = "source", TargetStart = 3 };
withContext.Validate();
var cleanedContext = ExcerptTextCleanup.Clean(withContext);
Check(cleanedContext.SelectedText == "上下文摘录。" && cleanedContext.ContextText == "前文。上下文摘录。后文。" &&
    cleanedContext.TargetStart == 3, "Zhihu footer cleanup preserves verified source context and target offset");
Check(ExcerptTextCleanup.Clean(Material("正文。\r\n\r\n链接：https://zhuanlan.zhihu.com/p/123\r\n来源：知乎\r\n著作权归作者所有。商业转载请联系作者获得授权，非商业转载请注明出处。"))
    .SelectedText == "正文。", "Zhihu article footer with CRLF and no author is removed");
Check(ExcerptTextCleanup.Clean(Material("正文。\n\n作者: 公开示例\n链接: https://www.zhihu.com/question/123/answer/456\n来源: 知乎\n著作权归作者所有。商业转载请联系作者获得授权，非商业转载请注明出处。"))
    .SelectedText == "正文。", "Zhihu footer with ASCII colons is removed");
foreach (var text in new[] {
    "正文提到来源：知乎以及著作权归作者所有，但仍有后文。",
    "正文。\n\n作者：公开示例\n链接：https://example.com/123\n来源：示例网站\n著作权归作者所有。商业转载请联系作者获得授权，非商业转载请注明出处。",
    "正文。\n\n作者：公开示例\n链接：https://www.zhihu.com/question/123/answer/456\n来源：知乎"
})
{
    var unchanged = Material(text);
    Check(ReferenceEquals(ExcerptTextCleanup.Clean(unchanged), unchanged), "ordinary body or incomplete attribution is preserved");
}
if (args[0] == "--footer-regression")
{
    WorkflowJson.Save(receipt, new { passed = true, checks, scope = "Zhihu footer cleanup only; public synthetic text and isolated draft store" });
    Console.WriteLine($"PASS {checks.Count} focused excerpt footer checks");
    return;
}
var session = ExcerptCards.Create(Material("😀 Alpha & beta.\r\nAlpha follows beta."), settings, store);
var rawId = session.Cards[0].Id;
var raw = store.Cards.Find(rawId)!;
var note = AnkiNoteDraft.From(raw);
Check(note.IsExcerpt && !note.IsCloze && note.Fields["Back"] == "" && WebUtility.HtmlDecode(note.Fields["Front"]) == "😀 Alpha & beta.<br>Alpha follows beta." && note.Ordinals.SequenceEqual(new[] { 0 }), "raw excerpt preserves the complete original and an empty answer in one card");
Check(note.Deck == "Excerpt fixture::摘录", "excerpt has its own deck path");
var selectedSettings = settings with { OriginalExcerptDocumentId = "reading-document-id", OriginalExcerptDocumentTitle = "科研论文" };
var selectedSession = ExcerptCards.Create(Material("Public reading material"), selectedSettings, store);
var selectedRaw = ExcerptCards.Get(selectedSession, store, selectedSession.Cards[0].Id);
var selectedPlan = ExportRouting.Prepare(selectedRaw, ExportRouting.BuildNote(selectedRaw)).RemNote!;
Check(selectedRaw.IsOriginalExcerpt && selectedPlan.DocumentPath.SequenceEqual(new[] { "摘录", "科研论文" }) &&
    selectedPlan.TargetDocumentId == "reading-document-id", "only the saved original excerpt uses the selected RemNote Document identity");
var selectedManual = ExcerptCards.Add(selectedSession, store, new("Review question", "Review answer"), "手动");
var manualPlan = ExportRouting.Prepare(selectedManual, ExportRouting.BuildNote(selectedManual)).RemNote!;
Check(!selectedManual.IsOriginalExcerpt && manualPlan.TargetDocumentId == null && manualPlan.DocumentPath[0] == "摘录" &&
    manualPlan.DocumentPath.Length == 2 && manualPlan.DocumentPath[1] == DateTime.Today.ToString("M.d", System.Globalization.CultureInfo.InvariantCulture),
    "excerpt card-making keeps the daily RemNote directory");
Check(ExcerptHotkey.Parse("Ctrl+Alt+Space") == new ExcerptHotkey(3, 0x20) &&
    ExcerptHotkey.Parse("").VirtualKey == 0, "original excerpt panel hotkey parses and can be disabled");
await Reject(() => Do(() => ExcerptHotkey.Parse("Shift+E")), "unsafe unmodified excerpt panel hotkey is rejected");
Check(ExcerptHotkey.Parse("Alt+Shift+《") == new ExcerptHotkey(5, 0xBC) &&
    ExcerptHotkey.Parse("Alt+Shift+<") == ExcerptHotkey.Parse("Alt+Shift+,") &&
    ExcerptHotkey.Parse("Alt+Shift+/") == new ExcerptHotkey(5, 0xBF), "requested punctuation shortcuts use physical comma and slash keys");
await Reject(() => Do(() => (new LanguageSettings { OriginalExcerptHotkey = "Alt+Shift+《", ReplayLastHotkey = "Shift+Alt+," }).Validate()),
    "duplicate hotkeys are rejected across aliases and modifier order");
Check(ExcerptHotkey.Parse(new LanguageSettings().OriginalExcerptHotkey) == new ExcerptHotkey(5, 0xBC) &&
    ExcerptHotkey.Parse(new LanguageSettings().ReplayLastHotkey) == new ExcerptHotkey(5, 0xBF), "new settings initialize both requested hotkeys");
if (args[0] == "--destination-regression")
{
    WorkflowJson.Save(receipt, new { passed = true, checks, scope = "original excerpt destination routing and hotkey only" });
    Console.WriteLine($"PASS {checks.Count} destination checks");
    return;
}
var legacySettings = ExcerptCards.Create(Material("Legacy settings excerpt"), new(), store);
Check(AnkiNoteDraft.From(store.Cards.Find(legacySettings.Cards[0].Id)!).Deck == "KnowledgeClipper::摘录", "legacy settings cannot route excerpt to the language deck");
var edited = ExcerptCards.Add(session, store, new("Changed front", "Changed back"), "手动");
ExcerptCards.Edit(session, store, edited.Id, "Edited <front>", "");
Check(store.Cards.Find(rawId)!.Excerpt == raw.Excerpt && store.Cards.Find(edited.Id)!.Excerpt!.Front == "Edited <front>", "manual edits remain independent of the original");
var a = ExcerptCards.Cloze(session, store, session.ManualFront.IndexOf("Alpha", StringComparison.Ordinal), 5);
var b = ExcerptCards.Cloze(session, store, session.ManualFront.LastIndexOf("beta", StringComparison.Ordinal), 4);
Check(a.Excerpt!.Front == "😀 {{c1::Alpha}} & beta.\r\nAlpha follows beta." && b.Excerpt!.Front == "😀 Alpha & beta.\r\nAlpha follows {{c1::beta}}.", "each exact selected occurrence produces a separate c1 card");
var merged = ExcerptCards.Merge(session, store, [a.Id, b.Id]);
Check(merged.Excerpt!.Front == "😀 {{c1::Alpha}} & beta.\r\nAlpha follows {{c1::beta}}." &&
    session.Cards.Where(c => c.Id == a.Id || c.Id == b.Id).All(c => c.SupersededBy == merged.Id), "merge uses a single cloze number and replaces pending originals");
var reopened = store.Find(session.Id)!;
Check(reopened.Cards.Single(c => c.Id == merged.Id).Replaces.SequenceEqual(new[] { a.Id, b.Id }) && store.Cards.Find(a.Id) != null, "merge keeps durable recoverable originals across reopen");
ExcerptCards.UndoMerge(session, store, merged.Id);
Check(session.Cards.Single(c => c.Id == a.Id).SupersededBy == null && session.Cards.Single(c => c.Id == merged.Id).SupersededBy == "undone", "undo restores the pending originals without touching Anki");
merged = ExcerptCards.Merge(session, store, [a.Id, b.Id]);
Check(ExcerptCloze.Question(merged.Excerpt!.Front) == "😀 [...] & beta.\r\nAlpha follows [...]." && ExcerptCloze.Answer(merged.Excerpt.Front) == session.ManualFront, "merged preview hides both spans and restores the exact original answer");
Check(ExcerptCloze.Render("abcdef", [new(0, 3), new(2, 3)]) == "{{c1::abcde}}f", "overlapping clozes merge without duplicate or lost text");
await Reject(() => Do(() => ExcerptCloze.Render("😀 alpha", [new(1, 1)])), "a split surrogate cannot corrupt Unicode");
await Reject(() => Do(() => ExcerptCloze.Validate("{{c1::one}} {{c2::two}}")), "multiple-card prompt output is rejected");
await Reject(() => Do(() => ExcerptCloze.Validate("{{c1::outer {{c1::inner}}}}")), "nested cloze is rejected");
var changedSource = ExcerptCards.Add(session, store, new("{{c1::other}} source", "", ExcerptCardKind.Cloze, "other source", [new(0, 5)]), "不同原文");
await Reject(() => Do(() => ExcerptCards.Merge(session, store, [merged.Id, changedSource.Id])), "unrelated source versions cannot be merged");
Check(!new DraftStore(data).Recent().Any() && store.Recent().Any(s => s.Id == session.Id), "excerpt history and card persistence are independent from language history");
Check(ExcerptModeLoader.Load(Path.Combine(project, "modes")).Length == 0, "unpublished prompt registry keeps raw and manual flows available");
var config = ModeConfigurationLoader.Load(Path.Combine(project, "modes"));
Check(EntryRouting.ModeId(config, "language") == "language" &&
    config.Modes.Single(m => m.Definition.Id == EntryRouting.ModeId(config, "translation")).Definition.OutputKind == "translation" &&
    config.Modes.Single(m => m.Definition.Id == EntryRouting.ModeId(config, "question")).Definition.OutputKind == "question", "explicit entries choose the requested flow without classification");

using var backend = new AnkiFixture(); using var http = new HttpClient(backend);
var anki = new AnkiConnection(http, "http://127.0.0.1:8765");
await ExcerptCards.ImportAsync(session, store, anki, [rawId]);
Check(store.Cards.Find(rawId)!.ImportState == ImportState.Verified && backend.Adds == 1, "raw card imports and verifies a genuinely blank answer");
await ExcerptCards.ImportAsync(session, store, anki, [rawId, merged.Id, edited.Id]);
Check(backend.Adds == 3 && store.Cards.Find(merged.Id)!.CardIds.Length == 1 && backend.Models[AnkiNoteDraft.ExcerptClozeModelName].GetProperty("isCloze").GetBoolean(), "mixed individual import uses a native cloze model and skips imported cards");
await Reject(() => Do(() => ExcerptCards.Edit(session, store, merged.Id, "replacement", "")), "confirmed card is immutable on retries");
await Reject(() => ExcerptCards.ImportAsync(session, store, anki, [a.Id]), "replaced cloze is excluded from later import");
var extra = ExcerptCards.Cloze(session, store, session.ManualFront.IndexOf("beta", StringComparison.Ordinal), 4);
var secondMerge = ExcerptCards.Merge(session, store, [merged.Id, extra.Id]);
Check(session.Cards.Single(c => c.Id == merged.Id).SupersededBy == null && store.Cards.Find(merged.Id)!.ImportState == ImportState.Verified && backend.Adds == 3,
    "merging with an imported card leaves its Anki note unchanged");
await ExcerptCards.ImportAsync(session, store, anki, [secondMerge.Id]);
Check(backend.Adds == 4, "the new merged card imports independently");
var timeout = ExcerptCards.Add(session, store, new("Lost response card", "Answer"), "超时测试");
backend.DropAtAdd = backend.Adds + 1;
await Reject(() => ExcerptCards.ImportAsync(session, store, anki, [timeout.Id]), "a lost write response remains unresolved");
var adds = backend.Adds;
Check(store.Cards.Find(timeout.Id)!.ImportState == ImportState.Unknown, "uncertain write state persists");
await ExcerptCards.ImportAsync(store.Find(session.Id)!, store, anki, [timeout.Id]);
Check(backend.Adds == adds && store.Cards.Find(timeout.Id)!.ImportState == ImportState.Verified, "reopening reconciles the same note without a second add");
var missing = ExcerptCards.Add(session, store, new("Unknown absent card", ""), "未知测试");
missing.ConfirmedNote = AnkiNoteDraft.From(missing); missing.ImportState = ImportState.Unknown; store.Cards.Save(missing);
await Reject(() => ExcerptCards.ImportAsync(session, store, anki, [missing.Id]), "unknown missing note cannot be silently added again");
Check(backend.Adds == adds, "unknown recovery performs no additional write");
var concurrent = ExcerptCards.Create(Material("Concurrent confirmation"), settings, store);
await Task.WhenAll(ExcerptCards.ImportAsync(concurrent, store, anki, [concurrent.Cards[0].Id]), ExcerptCards.ImportAsync(concurrent, store, anki, [concurrent.Cards[0].Id]));
Check(backend.Adds == adds + 1, "same-session concurrent imports cannot duplicate cards");
var drift = ExcerptCards.Create(Material("Blank answer verification"), settings, store);
backend.WrongBlankAnswer = true;
await Reject(() => ExcerptCards.ImportAsync(drift, store, anki, [drift.Cards[0].Id]), "unexpected content on a blank back fails actual face verification");
Check(store.Cards.Find(drift.Cards[0].Id)!.ImportState == ImportState.Unknown, "face mismatch is retained for reconciliation");
WorkflowJson.Save(receipt, new { passed = true, checks, dataRoot = data, scope = "domain and Anki API fault fixtures; no production prompt or live Anki calls" });
Console.WriteLine($"{checks.Count} excerpt checks passed");

sealed class AnkiFixture : HttpMessageHandler
{
    public Dictionary<string, JsonElement> Models { get; } = [];
    private readonly Dictionary<long, JsonElement> notes = [];
    public int Adds { get; private set; }
    public int DropAtAdd;
    public bool WrongBlankAnswer;
    public Dictionary<string, string> Media = new();
    public int MediaWrites;
    public bool ReverseImageOrder;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Yield(); ct.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var action = doc.RootElement.GetProperty("action").GetString(); var p = doc.RootElement.GetProperty("params");
        object? result;
        switch (action)
        {
            case "retrieveMediaFile": result = Media.TryGetValue(p.GetProperty("filename").GetString()!, out var media) ? (object)media : false; break;
            case "storeMediaFile": MediaWrites++; var filename = p.GetProperty("filename").GetString()!; Media[filename] = p.GetProperty("data").GetString()!; result = filename; break;
            case "findNotes": var tag = p.GetProperty("query").GetString()![4..]; result = notes.Where(n => n.Value.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == tag)).Select(n => n.Key).ToArray(); break;
            case "modelNames": result = Models.Keys.ToArray(); break;
            case "createModel": Models.Add(p.GetProperty("modelName").GetString()!, p.Clone()); result = null; break;
            case "modelFieldNames": result = Models[p.GetProperty("modelName").GetString()!].GetProperty("inOrderFields"); break;
            case "modelTemplates": result = Models[p.GetProperty("modelName").GetString()!].GetProperty("cardTemplates").EnumerateArray().ToDictionary(t => t.GetProperty("Name").GetString()!, t => t); break;
            case "deckNames": result = new[] { "Excerpt fixture::摘录" }; break;
            case "createDeck": result = 1; break;
            case "addNote":
                Adds++; notes[Adds] = p.GetProperty("note").Clone();
                if (Adds == DropAtAdd) throw new TaskCanceledException("fixture lost response after write");
                result = Adds; break;
            case "notesInfo":
                var id = p.GetProperty("notes")[0].GetInt64(); var note = notes[id];
                result = new[] { new { noteId = id, modelName = note.GetProperty("modelName").GetString(), tags = note.GetProperty("tags"), cards = new[] { id * 10 },
                    fields = note.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => new { value = f.Value.GetString() }) } }; break;
            case "cardsInfo":
                result = p.GetProperty("cards").EnumerateArray().Select(card =>
                {
                    var cid = card.GetInt64(); var n = notes[cid / 10]; var f = n.GetProperty("fields");
                    var isCloze = Models[n.GetProperty("modelName").GetString()!].GetProperty("isCloze").GetBoolean();
                    var front = f.GetProperty("Front").GetString()!; var back = f.GetProperty("Back").GetString()!;
                    // Independent fixture rendering, never call the production cloze renderer here.
                    var question = isCloze ? Regex.Replace(front, @"\{\{c1::(.*?)\}\}", "<span class='cloze'>[...]</span>", RegexOptions.Singleline) : front;
                    var answer = isCloze ? Regex.Replace(front, @"\{\{c1::(.*?)\}\}", "$1", RegexOptions.Singleline) + "<br>" + back : back;
                    if (WrongBlankAnswer && back == "") answer += "Unexpected text";
                    if (ReverseImageOrder)
                    {
                        var matches = Regex.Matches(question, "<img[^>]+>").Select(m => m.Value).Reverse().ToArray(); var index = 0;
                        question = Regex.Replace(question, "<img[^>]+>", _ => matches[index++]);
                    }
                    return new { cardId = cid, note = cid / 10, ord = 0, queue = 0, deckName = n.GetProperty("deckName").GetString(), question, answer };
                }).ToArray(); break;
            case "areSuspended": result = p.GetProperty("cards").EnumerateArray().Select(_ => false).ToArray(); break;
            default: throw new InvalidOperationException("Unexpected mutation/action: " + action);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
    }
}

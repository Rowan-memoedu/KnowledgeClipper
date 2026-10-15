using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Workflow;

if (args.Length == 4 && args[0] == "--captured-readback")
{
    var draft = WorkflowJson.Read<ClipDraft>(args[1]);
    using var raw = JsonDocument.Parse(File.ReadAllText(args[2]));
    var verified = RemNoteConnection.Validate(draft.ConfirmedRemNote ?? throw new InvalidDataException("The saved draft has no confirmed plan."), raw.RootElement, draft.RemNoteResult);
    WorkflowJson.Save(args[3], new { passed = true, scope = "Read-only validation replay of a saved real SDK response; no network or draft mutation.",
        draftId = draft.Id, sourceId = draft.ConfirmedRemNote.SourceId, draftPath = Path.GetFullPath(args[1]), readbackPath = Path.GetFullPath(args[2]),
        draftSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[1]))),
        readbackSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[2]))), verified });
    Console.WriteLine("PASS captured real SDK readback: " + draft.Id); return;
}

var results = new List<CheckResult>();
var receiptPath = args.Single();
var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\remnote-client-tests", Guid.NewGuid().ToString("N"));
var store = new DraftStore(dataRoot);
async Task Test(string name, Func<Task> test)
{
    try { await test(); results.Add(new(name, true, null)); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add(new(name, false, ex.ToString())); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
Task TestSync(string name, Action test) => Test(name, () => { test(); return Task.CompletedTask; });
void Assert(bool pass, string message) { if (!pass) throw new Exception(message); }
void Reject(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("Expected InvalidDataException; invalid readback was accepted.");
}
async Task RejectAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, WorkflowJson.Options);
RemNoteImportReceipt Verify(RemNoteExportPlan plan, JsonNode node, RemNoteImportReceipt? previous = null) => RemNoteConnection.Validate(plan, Element(node), previous);
var basicPlan = Fixtures.BasicPlan();

await TestSync("a single front Rem retains LF and two native multiline answer children", () =>
{
    var receipt = Verify(basicPlan, Fixtures.Basic());
    Assert(receipt.RemIds.SequenceEqual(["front-rem"]) && receipt.CardIds.SequenceEqual(["practice-1"]), "Wrong verified identities.");
    Assert(!receipt.Evidence.GetProperty("cards")[0].GetProperty("nodes")[0].GetProperty("isCardItem").GetBoolean(), "A multiline front must not turn its containing directory into a card.");
    Assert(receipt.Evidence.GetProperty("cards")[0].GetProperty("nodes")[0].GetProperty("text")[0].GetProperty("text").GetString() == "First\nSecond", "Lost actual raw text evidence.");
});
await TestSync("legacy multiline fronts retain their frozen identity during read-only compatibility validation", () =>
{
    var legacy = Fixtures.Basic(); legacy["cards"]![0]!["nodes"]![0]!["isCardItem"] = true;
    var receipt = Verify(basicPlan, legacy);
    Assert(receipt.RemIds.SequenceEqual(["front-rem"]) && receipt.CardIds.SequenceEqual(["practice-1"]), "Legacy identity changed.");
});
await TestSync("multiline answer children cannot own the card instead of its sole front", () =>
{
    var value = Fixtures.Basic(); var nodes = value["cards"]![0]!["nodes"]!;
    nodes[0]!["cardIds"] = new JsonArray(); nodes[0]!["cardTypes"] = new JsonArray();
    nodes[1]!["cardIds"] = new JsonArray("practice-1"); nodes[1]!["cardTypes"] = new JsonArray("forward");
    Reject(() => Verify(basicPlan, value));
});
await TestSync("adjacent raw text fragments are equivalent without inventing separate front nodes", () =>
{
    var value = Fixtures.Basic(); value["cards"]![0]!["nodes"]![0]!["text"] = JsonNode.Parse("[\"First\",{\"i\":\"m\",\"text\":\"\\nSecond\"}]");
    Verify(basicPlan, value);
});
await TestSync("echoed semantic fields cannot override a changed raw SDK front", () =>
{
    var value = Fixtures.Basic(); value["cards"]![0]!["nodes"]![0]!["text"]![0]!["text"] = "Wrong front";
    Reject(() => Verify(basicPlan, value));
});
await TestSync("raw SDK text is sufficient even when convenience semantic fields are absent", () =>
{
    var value = Fixtures.Basic(); value["cards"]![0]!.AsObject().Remove("front"); value["cards"]![0]!.AsObject().Remove("back");
    Verify(basicPlan, value);
});
var invalidBasic = new (string Name, Action<JsonNode> Mutate)[]
{
    ("source identity mismatch", j => j["sourceId"] = "different-source"),
    ("wire schema mismatch", j => j["schemaVersion"] = "old-schema"),
    ("wrong configured root", j => j["rootRemId"] = "other-root"),
    ("wrong front directory", j => j["cards"]![0]!["nodes"]![0]!["parentId"] = "other-directory"),
    ("wrong answer parent", j => j["cards"]![0]!["nodes"]![1]!["parentId"] = "another-front"),
    ("reordered answer children", j => j["cards"]![0]!["nodes"]![0]!["children"] = new JsonArray("answer-2", "answer-1")),
    ("missing native answer-card-item flag", j => j["cards"]![0]!["nodes"]![1]!["isCardItem"] = false),
    ("content incorrectly made a document", j => j["cards"]![0]!["nodes"]![0]!["isDocument"] = true),
    ("content incorrectly made a concept Rem", j => j["cards"]![0]!["nodes"]![0]!["remType"] = 1),
    ("back field used instead of native multiline children", j => j["cards"]![0]!["nodes"]![0]!["backText"] = new JsonArray("Unexpected two-sided field")),
    ("missing generated practice card", j => { j["cards"]![0]!["cardIds"] = new JsonArray(); j["cards"]![0]!["nodes"]![0]!["cardIds"] = new JsonArray(); }),
    ("practice card identity differs from SDK node evidence", j => j["cards"]![0]!["nodes"]![0]!["cardIds"] = new JsonArray("other-card")),
    ("wrong card kind", j => j["cards"]![0]!["kind"] = "plain"),
    ("wrong card key", j => j["cards"]![0]!["key"] = "card.99"),
    ("basic card changed to backward practice", j => j["cards"]![0]!["nodes"]![0]!["practiceDirection"] = "backward"),
    ("native practice card unexpectedly reversed despite forward Rem setting", j => j["cards"]![0]!["nodes"]![0]!["cardTypes"] = new JsonArray("backward")),
};
foreach (var (name, mutate) in invalidBasic)
    await TestSync("readback rejects " + name, () => { var value = Fixtures.Basic(); mutate(value); Reject(() => Verify(basicPlan, value)); });

await TestSync("plain front-only content confirms ordinary nodes with no cards", () =>
{
    var receipt = Verify(Fixtures.PlainPlan(), Fixtures.Plain());
    Assert(receipt.RemIds.SequenceEqual(["plain-one", "plain-two"]) && receipt.CardIds.Length == 0, "Front-only material became cards.");
});
await TestSync("one batch separator follows all source paragraphs without increasing content or card counts", () =>
{
    var value = Fixtures.WithSeparator(Fixtures.Plain());
    var receipt = Verify(Fixtures.PlainPlan(), value);
    Assert(receipt.RemIds.SequenceEqual(["plain-one", "plain-two"]) && receipt.CardIds.Length == 0, "Blank sibling was counted as imported content.");
    Assert(receipt.Evidence.GetProperty("batchSeparator").GetProperty("remId").GetString() == "batch-blank", "Separator evidence was not retained.");
    value["documentChildren"]!.AsArray().Add("next-batch-content"); value["documentChildren"]!.AsArray().Add("next-batch-blank");
    Verify(Fixtures.PlainPlan(), value, receipt);
});
await TestSync("legacy readback without spacing metadata remains valid without a new import", () =>
{
    var receipt = Verify(basicPlan, Fixtures.Basic());
    Verify(basicPlan, Fixtures.Basic(), receipt);
    Assert(!receipt.Evidence.TryGetProperty("batchSeparator", out _), "Legacy receipt was retroactively changed.");
});
await TestSync("SDK empty string representations remain empty sibling nodes", () =>
{
    var value = Fixtures.WithSeparator(Fixtures.Basic());
    value["batchSeparator"]!["node"]!["text"] = JsonNode.Parse("""["",{"i":"m","text":""}]""");
    value["batchSeparator"]!["node"]!["backText"] = new JsonArray("");
    Verify(basicPlan, value);
});
foreach (var (name, mutate) in new (string, Action<JsonNode>)[]
{
    ("nonempty front", j => j["batchSeparator"]!["node"]!["text"] = new JsonArray("User content")),
    ("nonempty back", j => j["batchSeparator"]!["node"]!["backText"] = new JsonArray("Answer")),
    ("null front evidence", j => j["batchSeparator"]!["node"]!["text"] = null),
    ("cloze hidden in empty text", j => j["batchSeparator"]!["node"]!["text"] = JsonNode.Parse("""[{"i":"m","text":"","cId":"kc_1"}]""")),
    ("document flag", j => j["batchSeparator"]!["node"]!["isDocument"] = true),
    ("concept type", j => j["batchSeparator"]!["node"]!["remType"] = 1),
    ("card item", j => j["batchSeparator"]!["node"]!["isCardItem"] = true),
    ("extra card detail", j => j["batchSeparator"]!["node"]!["isExtraCardDetail"] = true),
    ("enabled practice", j => j["batchSeparator"]!["node"]!["practiceEnabled"] = true),
    ("generated card", j => j["batchSeparator"]!["node"]!["cardIds"] = new JsonArray("unwanted-card")),
    ("generated card type", j => j["batchSeparator"]!["node"]!["cardTypes"] = new JsonArray("forward")),
    ("child content", j => j["batchSeparator"]!["node"]!["children"] = new JsonArray("unexpected-child")),
    ("wrong parent", j => j["batchSeparator"]!["node"]!["parentId"] = "front-rem"),
    ("wrong raw identity", j => j["batchSeparator"]!["node"]!["remId"] = "different-blank"),
    ("wrong predecessor", j => j["batchSeparator"]!["previousRemId"] = "answer-1"),
    ("missing raw node", j => j["batchSeparator"]!.AsObject().Remove("node")),
    ("missing sibling evidence", j => j.AsObject().Remove("documentChildren")),
    ("blank preceding the batch", j => j["documentChildren"] = new JsonArray("batch-blank", "front-rem")),
    ("another node between content and blank", j => j["documentChildren"] = new JsonArray("front-rem", "inserted", "batch-blank")),
    ("duplicate blank identity", j => j["documentChildren"] = new JsonArray("front-rem", "batch-blank", "batch-blank")),
}) await TestSync("separator readback rejects " + name, () =>
{
    var value = Fixtures.WithSeparator(Fixtures.Basic()); mutate(value); Reject(() => Verify(basicPlan, value));
});
await TestSync("batch spacing checks the order of every source paragraph", () =>
{
    var value = Fixtures.WithSeparator(Fixtures.Plain());
    value["documentChildren"] = new JsonArray("plain-two", "plain-one", "batch-blank");
    Reject(() => Verify(Fixtures.PlainPlan(), value));
});
await TestSync("previous separator identity cannot disappear or be replaced during a retry", () =>
{
    var previous = Verify(basicPlan, Fixtures.WithSeparator(Fixtures.Basic()));
    Reject(() => Verify(basicPlan, Fixtures.Basic(), previous));
    var value = Fixtures.WithSeparator(Fixtures.Basic()); value["batchSeparator"]!["remId"] = "new-blank";
    value["batchSeparator"]!["node"]!["remId"] = "new-blank"; value["documentChildren"] = new JsonArray("front-rem", "new-blank");
    Reject(() => Verify(basicPlan, value, previous));
});
await TestSync("plain content rejects an unintended practice card", () =>
{
    var value = Fixtures.Plain(); value["cards"]![0]!["cardIds"] = new JsonArray("unwanted");
    value["cards"]![0]!["nodes"]![0]!["cardIds"] = new JsonArray("unwanted");
    Reject(() => Verify(Fixtures.PlainPlan(), value));
});
await TestSync("plain and cloze fronts still cannot acquire a multiline card-item role", () =>
{
    var plain = Fixtures.Plain(); plain["cards"]![0]!["nodes"]![0]!["isCardItem"] = true;
    Reject(() => Verify(Fixtures.PlainPlan(), plain));
    var cloze = Fixtures.Cloze(); cloze["cards"]![0]!["nodes"]![0]!["isCardItem"] = true;
    Reject(() => Verify(Fixtures.ClozePlan(), cloze));
});
await TestSync("two distinct cloze groups produce two cards while repeated group stays linked", () =>
{
    var receipt = Verify(Fixtures.ClozePlan(), Fixtures.Cloze());
    Assert(receipt.CardIds.SequenceEqual(["cloze-one", "cloze-two"]), "Grouped cloze count changed.");
});
await TestSync("cloze with an empty Back still verifies as cards rather than plain content", () =>
{
    var plan = Fixtures.ClozePlan(); plan = plan with { Cards = [plan.Cards[0] with { Back = [] }] };
    var value = Fixtures.Cloze(); value["cards"]![0]!["childRemIds"] = new JsonArray();
    value["cards"]![0]!["nodes"]![0]!["children"] = new JsonArray(); value["cards"]![0]!["nodes"]!.AsArray().RemoveAt(1);
    Assert(Verify(plan, value).CardIds.Length == 2, "An empty cloze Back disabled native cards.");
});
await TestSync("existing card IDs do not verify a basic or cloze Rem disabled from practice", () =>
{
    var basic = Fixtures.Basic(); basic["cards"]![0]!["nodes"]![0]!["practiceEnabled"] = false;
    Reject(() => Verify(basicPlan, basic));
    var cloze = Fixtures.Cloze(); cloze["cards"]![0]!["nodes"]![0]!["practiceEnabled"] = false;
    Reject(() => Verify(Fixtures.ClozePlan(), cloze));
});
await TestSync("front-only ordinary nodes cannot be silently enabled for practice", () =>
{
    var value = Fixtures.Plain(); value["cards"]![0]!["nodes"]![0]!["practiceEnabled"] = true;
    Reject(() => Verify(Fixtures.PlainPlan(), value));
});
await TestSync("explicitly empty Anki cloze hint matches the SDK omission of that hint", () =>
{
    var plan = Fixtures.ClozePlan(); var front = plan.Cards[0].Front.ToArray(); front[0] = front[0] with { Hint = "" };
    plan = plan with { Cards = [plan.Cards[0] with { Front = front }] };
    var value = Fixtures.Cloze(); value["cards"]![0]!["nodes"]![0]!["text"]![0]!.AsObject().Remove("cloze-hint");
    Verify(plan, value);
});
await TestSync("cloze groups and hints are checked against raw SDK rich text", () =>
{
    var value = Fixtures.Cloze(); value["cards"]![0]!["nodes"]![0]!["text"]![2]!["cId"] = "kc_2";
    Reject(() => Verify(Fixtures.ClozePlan(), value));
    value = Fixtures.Cloze(); value["cards"]![0]!["nodes"]![0]!["text"]![0]!["cloze-hint"] = "changed hint";
    Reject(() => Verify(Fixtures.ClozePlan(), value));
});
await TestSync("equal cloze card counts cannot hide duplicate or wrong native group identities", () =>
{
    var value = Fixtures.Cloze(); value["cards"]![0]!["nodes"]![0]!["cardTypes"]![1]!["clozeId"] = "kc_1";
    Reject(() => Verify(Fixtures.ClozePlan(), value));
    value = Fixtures.Cloze(); value["cards"]![0]!["nodes"]![0]!["cardTypes"]![1]!["clozeId"] = "kc_3";
    Reject(() => Verify(Fixtures.ClozePlan(), value));
});
await TestSync("cloze extra details remain ordinary children instead of extra basic cards", () =>
{
    var value = Fixtures.Cloze(); value["cards"]![0]!["nodes"]![1]!["isCardItem"] = true;
    Reject(() => Verify(Fixtures.ClozePlan(), value));
});
await TestSync("multiformula rich text retains native block and inline TeX", () =>
{
    var plan = basicPlan with { Cards = [basicPlan.Cards[0] with { Front = [new("text", "For "), new("latex", "x<2", Block: false), new("text", "\n"), new("latex", "x+y=3\\\\\nx-y=1", Block: true)] }] };
    var value = Fixtures.Basic(); value["cards"]![0]!["nodes"]![0]!["text"] = JsonNode.Parse("""[{"i":"m","text":"For "},{"i":"x","text":"x<2","block":false},{"i":"m","text":"\n"},{"i":"x","text":"x+y=3\\\\\nx-y=1","block":true}]""");
    Verify(plan, value); value["cards"]![0]!["nodes"]![0]!["text"]![3]!["block"] = false;
    Reject(() => Verify(plan, value));
});
await TestSync("multiple images use independently hashed managed files in face order", () =>
{
    Verify(Fixtures.ImagesPlan(), Fixtures.Images());
});
foreach (var (name, mutate) in new (string, Action<JsonNode>)[]
{
    ("swapped image hash", j => j["cards"]![0]!["nodes"]![0]!["media"]![0]!["sha256"] = Fixtures.HashB),
    ("unmanaged image URL", j => j["cards"]![0]!["nodes"]![0]!["text"]![0]!["url"] = "file:///D:/outside.png"),
    ("different managed-media locator", j => j["cards"]![0]!["nodes"]![0]!["media"]![0]!["uri"] = "%LOCAL_FILE%different.png"),
    ("pending image copy", j => j["mediaPending"] = true),
}) await TestSync("readback rejects " + name, () => { var value = Fixtures.Images(); mutate(value); Reject(() => Verify(Fixtures.ImagesPlan(), value)); });
await TestSync("prior successful root directory and Rem identities remain fixed", () =>
{
    var previous = Verify(basicPlan, Fixtures.Basic()); var value = Fixtures.Basic(); value["documentRemId"] = "new-directory";
    value["cards"]![0]!["nodes"]![0]!["parentId"] = "new-directory";
    Reject(() => Verify(basicPlan, value, previous));
    value = Fixtures.Basic(); value["cards"]![0]!["remIds"] = new JsonArray("replacement-rem"); value["cards"]![0]!["nodes"]![0]!["remId"] = "replacement-rem";
    value["cards"]![0]!["nodes"]![1]!["parentId"] = "replacement-rem"; value["cards"]![0]!["nodes"]![2]!["parentId"] = "replacement-rem";
    Reject(() => Verify(basicPlan, value, previous));
});

var definitions = new[] {
    new ModeDefinition("en-reading", "英语阅读", true, "", "", "", "language", Language: "en", Practice: PracticeChoice.Reading),
    new ModeDefinition("en-writing", "英语书写", true, "", "", "", "language", Language: "en", Practice: PracticeChoice.Writing),
    new ModeDefinition("question", "问答", true, "", "", "", "question", RequiresContext: false),
    new ModeDefinition("translation", "翻译", true, "", "", "", "translation", RequiresContext: false),
    new ModeDefinition("summary-local", "我的摘要", true, "", "", "", "excerpt", RequiresContext: false),
}.Concat(ExportRouting.BuiltinModes).ToArray();
ClipDraft Draft(string mode = "excerpt-manual", LanguageSettings? settings = null, DateTimeOffset? capturedAt = null)
{
    var material = new CaptureMaterial("First\nSecond", null, "unavailable", null, "Public SDK fixture", 0, capturedAt ?? DateTimeOffset.UtcNow);
    var def = definitions.Single(d => d.Id == mode);
    return new() { Original = material, Material = material, Settings = settings ?? new(),
        Configuration = new("", definitions.Select(d => new ModeSnapshot(d, "", "")).ToArray()),
        ModeId = mode, Route = new(mode, def.OutputKind, false, false), Practice = def.Practice ?? PracticeChoice.Reading,
        Content = new(mode, Basic: new("First\nSecond", "Answer A\nAnswer B")) };
}
AnkiNoteDraft Note(string front = "First\nSecond", string back = "Answer A\nAnswer B") => new("Fixture", AnkiNoteDraft.ExcerptModelName,
    new() { ["Front"] = AnkiNoteDraft.Html(front), ["Back"] = AnkiNoteDraft.Html(back) }, [], [0]);
ClipDraft Confirmed(LanguageSettings? settings = null)
{
    var draft = Draft(settings: settings); ExportRouting.Confirm(draft, Note()); return draft;
}
JsonNode ForDraft(ClipDraft draft) { var value = Fixtures.Basic(); value["sourceId"] = draft.ConfirmedRemNote!.SourceId; return value; }
string FrozenSnapshot(ClipDraft draft) => JsonSerializer.Serialize(new { draft.Id, draft.Settings, draft.Original, draft.Material,
    draft.ModeId, draft.Route, draft.ConfirmedDestination, draft.ConfirmedNote, draft.ConfirmedRemNote }, WorkflowJson.Options);
void AssertFreshExport(WireCall call, ClipDraft draft)
{
    var request = JsonNode.Parse(call.Body!)!.AsObject();
    Assert(request.Count == 2 && request["newSource"]?.GetValue<bool>() == true && request["plan"] is JsonObject,
        "A fresh import must explicitly opt in to a new source using the export wrapper.");
    var sent = request["plan"]!.Deserialize<RemNoteExportPlan>(WorkflowJson.Options);
    Assert(JsonSerializer.Serialize(sent, WorkflowJson.Options) == JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options),
        "Transport opt-in changed the frozen export plan.");
}

await TestSync("new language defaults to Anki and every nonlanguage registered mode to RemNote", () =>
{
    foreach (var def in definitions) Assert(ExportRouting.For(Draft(def.Id)) == (def.OutputKind == "language" ? ExportDestination.Anki : ExportDestination.RemNote), "Wrong default for " + def.Id);
});
await TestSync("per-mode overrides support both destination directions independently", () =>
{
    var settings = new LanguageSettings { ModeDestinations = new() { ["en-reading"] = ExportDestination.RemNote, ["question"] = ExportDestination.Anki } };
    Assert(ExportRouting.For(Draft("en-reading", settings)) == ExportDestination.RemNote && ExportRouting.For(Draft("en-writing", settings)) == ExportDestination.Anki &&
        ExportRouting.For(Draft("question", settings)) == ExportDestination.Anki && ExportRouting.For(Draft("translation", settings)) == ExportDestination.RemNote, "Overrides leaked to another mode.");
});
await TestSync("documents are grouped by mode below the single configured root", () =>
{
    Assert(ExportRouting.DocumentPath(Draft("en-writing")).SequenceEqual(["语言学习", "英语", "书写练习"]), "Language document path mismatch.");
    Assert(ExportRouting.DocumentPath(Draft("question")).SequenceEqual(["问答"]) && ExportRouting.DocumentPath(Draft("translation")).SequenceEqual(["翻译"]), "Text route path mismatch.");
    Assert(ExportRouting.DocumentPath(Draft("image-original")).SequenceEqual(["图片", "原图"]) && ExportRouting.DocumentPath(Draft("summary-local")).SequenceEqual(["摘录", "我的摘要"]), "Image or custom path mismatch.");
});
await TestSync("new plans append the current local day below every mode instead of the old material capture date", () =>
{
    var localDay = DateTime.Today.ToString("M.d", System.Globalization.CultureInfo.InvariantCulture);
    foreach (var definition in definitions)
    {
        var draft = Draft(definition.Id, capturedAt: DateTimeOffset.Now.AddDays(-30));
        var confirmation = ExportRouting.Prepare(draft, Note(), ExportDestination.RemNote);
        Assert(confirmation.RemNote!.DocumentPath.SequenceEqual(ExportRouting.DocumentPath(draft).Append(localDay)),
            "New import did not use the local confirmation day for " + definition.Id);
        Assert(confirmation.RemNote.DocumentPath.Length <= 5, "Date exceeded the existing SDK path depth.");
        confirmation.Apply(draft);
        Assert(ExportRouting.Describe(draft).EndsWith(" / " + localDay), "Preview omitted the frozen day.");
    }
});
await TestSync("the new date label stays Gregorian M.d independently of UI culture and unconfirmed preview matches", () =>
{
    var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
        var draft = Draft();
        var expected = DateTime.Today.ToString("M.d", System.Globalization.CultureInfo.InvariantCulture);
        var preview = ExportRouting.Describe(draft);
        var confirmation = ExportRouting.Prepare(draft, Note());
        Assert(confirmation.RemNote!.DocumentPath[^1] == expected && preview.EndsWith(" / " + expected),
            "Culture changed the date or preview destination.");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
});
await Test("legacy and prior-day Unknown plans retain their exact path and recover through readback only", async () =>
{
    foreach (var path in new[] { new[] { "摘录" }, new[] { "摘录", DateTime.Today.AddDays(-1).ToString("M.d", System.Globalization.CultureInfo.InvariantCulture) } })
    {
        var draft = Draft();
        draft.ConfirmedNote = Note(); draft.ConfirmedDestination = ExportDestination.RemNote;
        draft.ConfirmedRemNote = new("knowledgeclipper." + draft.Id, draft.Settings.RemNoteRootTitle, null, path, RemNoteCardConversion.Convert(draft.ConfirmedNote));
        draft.ImportState = ImportState.Unknown;
        var original = JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options);
        var confirmation = ExportRouting.Prepare(draft, Note("ignored edits", "ignored answer"));
        Assert(ReferenceEquals(confirmation.RemNote, draft.ConfirmedRemNote) && confirmation.RemNote!.DocumentPath.SequenceEqual(path),
            "Frozen old plan acquired a new day.");
        Assert(ExportRouting.Describe(draft).EndsWith(" / " + string.Join(" / ", path)), "Old preview was relabeled with today.");
        store.Save(draft); draft = store.Find(draft.Id)!;
        using var backend = new ScriptedHandler((_, _) => Task.FromResult(ForDraft(draft)));
        using var http = new HttpClient(backend);
        await new CardImportRouter(http).ImportAsync(draft, store);
        var call = backend.Calls.Single();
        Assert(call.Path.EndsWith("/readback") && !JsonNode.Parse(call.Body!)!.AsObject().ContainsKey("newSource"), "Old recovery attempted an export.");
        Assert(JsonSerializer.Serialize(JsonSerializer.Deserialize<RemNoteExportPlan>(call.Body!, WorkflowJson.Options), WorkflowJson.Options) == original &&
            JsonSerializer.Serialize(store.Find(draft.Id)!.ConfirmedRemNote, WorkflowJson.Options) == original,
            "Recovery changed a frozen path or plan.");
    }
});
await TestSync("confirmed legacy Anki drafts cannot be rerouted by new defaults", () =>
{
    var draft = Draft("question"); draft.ConfirmedNote = Note(); draft.ImportState = ImportState.Ready;
    Assert(ExportRouting.For(draft) == ExportDestination.Anki && ExportRouting.Prepare(draft, Note("new", "new")).Note == draft.ConfirmedNote, "Legacy frozen fields changed.");
    Reject(() => ExportRouting.Prepare(draft, Note(), ExportDestination.RemNote));
    ExportRouting.Confirm(draft, Note("new", "new")); Assert(draft.ConfirmedDestination == null && draft.ConfirmedRemNote == null, "Legacy metadata was silently migrated.");
});
await TestSync("legacy Writing Unknown Verified and saved note identities stay pinned to Anki", () =>
{
    foreach (var state in new[] { ImportState.Writing, ImportState.Unknown, ImportState.Verified }) { var draft = Draft("question"); draft.ImportState = state; Assert(ExportRouting.For(draft) == ExportDestination.Anki, state.ToString()); }
    var withId = Draft("question"); withId.NoteId = 123; Assert(ExportRouting.For(withId) == ExportDestination.Anki, "Existing note ID changed destination.");
});
await TestSync("new RemNote confirmation freezes source fields directory and destination", () =>
{
    var draft = Confirmed(); var snapshot = JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options);
    draft.Settings.ModeDestinations["excerpt-manual"] = ExportDestination.Anki; draft.ModeId = "question"; draft.Route = new("question", "question", true, false);
    ExportRouting.Confirm(draft, Note("later edit", "later answer"));
    Assert(ExportRouting.For(draft) == ExportDestination.RemNote && JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options) == snapshot && draft.ConfirmedNote!.Fields["Front"] == "First<br>Second", "Frozen plan changed.");
    store.Save(draft); var loaded = store.Find(draft.Id)!;
    Assert(loaded.ConfirmedRemNote!.SourceId == "knowledgeclipper." + draft.Id && ExportRouting.Describe(loaded).EndsWith(" / " + string.Join(" / ", loaded.ConfirmedRemNote.DocumentPath)), "Reopen lost confirmed location.");
});
await TestSync("front-only translation is allowed for RemNote and still blocked for Anki", () =>
{
    var draft = Draft("translation"); draft.Content = new("translation", Basic: new(draft.Original.SelectedText, ""));
    var confirmation = ExportRouting.Prepare(draft, ExportRouting.BuildNote(draft));
    Assert(confirmation.RemNote!.Cards.Length == 2 && confirmation.RemNote.Cards.All(card => card.Kind == "plain"), "Blank Basic answer did not become two ordinary text nodes.");
    Reject(() => ExportRouting.BuildNote(draft, ExportDestination.Anki));
});
await TestSync("plain import summaries report content instead of claiming zero imported cards", () =>
{
    var draft = Confirmed(); draft.RemNoteResult = Verify(Fixtures.PlainPlan(), Fixtures.Plain()); draft.ImportState = ImportState.Verified;
    Assert(ExportRouting.ResultSummary(draft).Contains("2 条内容（普通节点）"), "Plain import status is misleading.");
});
await TestSync("settings reject remote and credential-bearing SDK endpoints", () =>
{
    foreach (var endpoint in new[] { "https://example.com", "http://user:pass@localhost:43101", "http://127.0.0.1:43101?token=value", "http://127.0.0.1:43101#route", "ftp://127.0.0.1" })
        Reject(() => (new LanguageSettings { RemNoteSdkEndpoint = endpoint }).Validate());
    (new LanguageSettings { RemNoteSdkEndpoint = "http://[::1]:43101" }).Validate();
});
await TestSync("new settings use the independent SDK host and only literal old standard roots migrate", () =>
{
    Assert(new LanguageSettings().RemNoteSdkEndpoint == "http://127.0.0.1:43111", "New settings retained the MCP-associated host.");
    foreach (var endpoint in new[] { "http://127.0.0.1:43101", "http://127.0.0.1:43101/", "http://localhost:43101", "HTTP://LOCALHOST:43101/", "http://[::1]:43101", "http://[::1]:43101/" })
        Assert(RemNoteConnection.ResolveEndpoint(endpoint) == RemNoteConnection.DefaultEndpoint, "Old standard endpoint did not migrate: " + endpoint);
});
await TestSync("endpoint compatibility preserves custom paths schemes credentials suffixes and ports", () =>
{
    foreach (var endpoint in new[] { "http://127.0.0.1:43111", "http://127.0.0.1:43200", "https://127.0.0.1:43101",
        "http://localhost:43101/sdk-proxy", "http://127.0.0.1:43101/.", "http://127.0.0.1:43101/custom/..", "http://127.0.0.1:43101//",
        "http://user:pass@127.0.0.1:43101", "http://127.0.0.1:43101?token=public-fixture", "http://127.0.0.1:43101#route",
        "http://127.0.0.2:43101", "http://localhost.:43101", "http://127.0.0.1:043101" })
        Assert(RemNoteConnection.ResolveEndpoint(endpoint) == endpoint, "A custom endpoint was rewritten: " + endpoint);
});
await Test("reopened legacy Ready draft imports through the independent host without mutating frozen content or settings", async () =>
{
    var draft = Confirmed(new() { RemNoteSdkEndpoint = "http://127.0.0.1:43101" }); store.Save(draft); draft = store.Find(draft.Id)!;
    var snapshot = FrozenSnapshot(draft);
    using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() : ForDraft(draft)));
    using var http = new HttpClient(backend); await new CardImportRouter(http).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Count == 2 && backend.Calls.All(call => call.Address.GetLeftPart(UriPartial.Authority) == RemNoteConnection.DefaultEndpoint),
        "Legacy draft still depended on the old MCP service port.");
    AssertFreshExport(backend.Calls[1], draft);
    Assert(FrozenSnapshot(store.Find(draft.Id)!) == snapshot, "Transport migration rewrote saved fields, IDs, target, or endpoint.");
});
await Test("custom SDK hosts and path prefixes remain in use for health and export", async () =>
{
    foreach (var endpoint in new[] { "http://127.0.0.1:43200", "http://localhost:43101/custom-sdk", "https://[::1]:43101" })
    {
        var draft = Confirmed(new() { RemNoteSdkEndpoint = endpoint }); var snapshot = FrozenSnapshot(draft);
        using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() : ForDraft(draft)));
        using var http = new HttpClient(backend); await new CardImportRouter(http).ImportAsync(draft, store);
        Assert(backend.Calls.Select(call => call.Address).SequenceEqual(new[] {
            new Uri(endpoint + "/sdk/knowledge-clipper/health"), new Uri(endpoint + "/sdk/knowledge-clipper/export") }), "Custom SDK route changed.");
        Assert(FrozenSnapshot(store.Find(draft.Id)!) == snapshot, "Custom endpoint import modified frozen content.");
        AssertFreshExport(backend.Calls[1], draft);
    }
});
await Test("an existing RemNote identity always takes readback even when the saved state appears ready or failed", async () =>
{
    foreach (var state in new[] { ImportState.Ready, ImportState.Failed })
    {
        var draft = Confirmed(new() { RemNoteSdkEndpoint = "http://localhost:43101/" });
        draft.RemNoteResult = Verify(draft.ConfirmedRemNote!, ForDraft(draft)); draft.ImportState = state; store.Save(draft); draft = store.Find(draft.Id)!;
        var snapshot = FrozenSnapshot(draft);
        using var backend = new ScriptedHandler((_, _) => Task.FromResult(ForDraft(draft))); using var http = new HttpClient(backend);
        await new CardImportRouter(http).ImportAsync(draft, store);
        var call = backend.Calls.Single(); var body = JsonNode.Parse(call.Body!)!.AsObject();
        Assert(call.Address.Port == 43111 && call.Path.EndsWith("/readback") && !body.ContainsKey("newSource") && !body.ContainsKey("plan"),
            "A known remote identity was treated as a new source after migration.");
        Assert(FrozenSnapshot(store.Find(draft.Id)!) == snapshot, "Readback migration changed frozen content.");
    }
});

await Test("successful import persists verified evidence and sends only the narrow SDK protocol", async () =>
{
    var draft = Confirmed(); using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() : ForDraft(draft)));
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await client.ImportAsync(draft, store); var saved = store.Find(draft.Id)!;
    Assert(saved.ImportState == ImportState.Verified && saved.RemNoteResult!.CardIds.Single() == "practice-1" && saved.Times.ContainsKey("remnoteVerified"), "Durable verification missing.");
    Assert(backend.Calls.Select(c => c.Method + " " + c.Path).SequenceEqual(["GET /sdk/knowledge-clipper/health", "POST /sdk/knowledge-clipper/export"]), "Unexpected transport operation.");
    var request = backend.Calls[1]; Assert(backend.Calls.All(c => c.Protocol == "1") && request.Body!.Contains("knowledge-clipper.remnote.v1") && !request.Body.Contains("\"rootRemId\""), "Protocol header or null omission incorrect.");
    AssertFreshExport(request, draft);
    await client.ImportAsync(saved, store); Assert(backend.Calls.Count == 2, "Verified retry performed another request.");
});
await Test("write-ahead state survives lost response and retry only reads back existing IDs", async () =>
{
    var draft = Confirmed(new() { RemNoteSdkEndpoint = "http://127.0.0.1:43101" }); var writingSavedBeforeSend = false;
    var snapshot = FrozenSnapshot(draft);
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        if (call.Path.EndsWith("/export"))
        {
            writingSavedBeforeSend = WorkflowJson.Read<ClipDraft>(Path.Combine(dataRoot, "language-drafts", draft.Id + ".json")).ImportState == ImportState.Writing;
            throw new TaskCanceledException("Simulated lost response after external write");
        }
        return Task.FromResult(ForDraft(draft));
    });
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await RejectAsync<TaskCanceledException>(() => client.ImportAsync(draft, store)); var saved = store.Find(draft.Id)!;
    Assert(writingSavedBeforeSend && saved.ImportState == ImportState.Unknown && saved.ImportError!.Contains("不重复添加") && saved.ImportGate.CurrentCount == 1, "Unknown write was not durably protected.");
    await client.ImportAsync(saved, store);
    Assert(saved.ImportState == ImportState.Verified && backend.Calls.Count(c => c.Path.EndsWith("/export")) == 1 && backend.Calls.Last().Path.EndsWith("/readback"), "Retry created a duplicate write.");
    AssertFreshExport(backend.Calls.Single(call => call.Path.EndsWith("/export")), draft);
    Assert(backend.Calls.All(call => call.Address.Port == 43111) && FrozenSnapshot(store.Find(draft.Id)!) == snapshot,
        "Legacy lost-response retry used the old host or changed the frozen source.");
    Assert(!JsonNode.Parse(backend.Calls.Last().Body!)!.AsObject().ContainsKey("newSource"), "Recovery readback opted in to a new source.");
});
await Test("process-exit Writing state reopens as Unknown and bypasses export and health", async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Writing; store.Save(draft); draft = store.Find(draft.Id)!;
    Assert(draft.ImportState == ImportState.Unknown, "Writing state was not recovered.");
    using var backend = new ScriptedHandler((_, _) => Task.FromResult(ForDraft(draft))); using var http = new HttpClient(backend);
    await new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store);
    Assert(backend.Calls.Count == 1 && backend.Calls[0].Path.EndsWith("/readback"), "Recovery did more than read back.");
});
await Test("health failure before write persists Failed and performs no export", async () =>
{
    var draft = Confirmed(); using var backend = new ScriptedHandler((_, _) => { var value = Fixtures.Health(); value["supported"] = false; return Task.FromResult(value); });
    using var http = new HttpClient(backend); await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(store.Find(draft.Id)!.ImportState == ImportState.Failed && backend.Calls.Count == 1 && draft.ImportGate.CurrentCount == 1, "Preflight failure wrote or left gate locked.");
});
foreach (var (field, text) in new[] { ("sdkResponsive", "已连接，但暂未响应"), ("connected", "尚未连接"), ("acceptWriteOperations", "未允许写入") })
await Test("health distinguishes " + field + " without sending an import", async () =>
{
    var draft = Confirmed();
    using var backend = new ScriptedHandler((_, _) => { var health = Fixtures.Health(); health[field] = false; return Task.FromResult(health); });
    using var http = new HttpClient(backend);
    await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    var saved = store.Find(draft.Id)!;
    Assert(saved.ImportState == ImportState.Failed && saved.ImportError!.Contains(text) && backend.Calls.Count == 1 && backend.Calls[0].Path.EndsWith("/health"), "Health diagnosis was conflated or an export was sent.");
});
await Test("raw verification failure remains Unknown across repeated recovery attempts", async () =>
{
    var draft = Confirmed(); using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        var value = ForDraft(draft); value["cards"]![0]!["nodes"]![1]!["text"]![0]!["text"] = "tampered answer"; return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await RejectAsync<InvalidDataException>(() => client.ImportAsync(draft, store)); draft = store.Find(draft.Id)!;
    await RejectAsync<InvalidDataException>(() => client.ImportAsync(draft, store));
    Assert(store.Find(draft.Id)!.ImportState == ImportState.Unknown && backend.Calls.Count(c => c.Path.EndsWith("/export")) == 1 && draft.RemNoteResult == null, "Tampered readback caused re-export or false verification.");
});
await Test("unknown HTTP rejection preserves read-only recovery and server reason", async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Unknown;
    using var backend = new ScriptedHandler((_, _) => Task.FromResult(JsonNode.Parse("{\"error\":\"Recorded import is incomplete\"}")!)) { Status = HttpStatusCode.Conflict };
    using var http = new HttpClient(backend); await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(store.Find(draft.Id)!.ImportState == ImportState.Unknown && draft.ImportError!.Contains("Recorded import is incomplete") && backend.Calls.Single().Path.EndsWith("/readback"), "Uncertain rejection was treated as permission to add again.");
});
JsonNode NotStarted(ClipDraft draft) => new JsonObject { ["schemaVersion"] = "knowledge-clipper.remnote.v1",
    ["sourceId"] = draft.ConfirmedRemNote!.SourceId, ["notStarted"] = true, ["error"] = "Directory preparation failed before content reservations" };
await Test("positive persisted notStarted recovery only marks Failed until a separate explicit retry", async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Unknown;
    var plan = JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options); var note = JsonSerializer.Serialize(draft.ConfirmedNote, WorkflowJson.Options);
    using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/readback") ? NotStarted(draft) : call.Path.EndsWith("/health") ? Fixtures.Health() : ForDraft(draft)));
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await RejectAsync<InvalidDataException>(() => client.ImportAsync(draft, store)); draft = store.Find(draft.Id)!;
    Assert(draft.ImportState == ImportState.Failed && draft.RemNoteResult == null && backend.Calls.Count == 1 && backend.Calls[0].Path.EndsWith("/readback"), "Checkpoint recovery performed an immediate replacement export.");
    Assert(JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options) == plan && JsonSerializer.Serialize(draft.ConfirmedNote, WorkflowJson.Options) == note && draft.ImportError != null, "Recovery changed the confirmed source or lost the failure state.");
    await client.ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Select(c => c.Path).SequenceEqual(["/sdk/knowledge-clipper/readback", "/sdk/knowledge-clipper/health", "/sdk/knowledge-clipper/export"]), "Explicit retry did not preserve the one-new-export boundary.");
    AssertFreshExport(backend.Calls.Last(), draft);
});
await Test("notStarted returned from the first export persists Failed without an automatic retry", async () =>
{
    var draft = Confirmed(); using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() : NotStarted(draft)));
    using var http = new HttpClient(backend);
    await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(store.Find(draft.Id)!.ImportState == ImportState.Failed && backend.Calls.Count == 2 && backend.Calls.Count(c => c.Path.EndsWith("/export")) == 1, "Initial no-write checkpoint caused another write.");
});
await Test("notStarted with a wrong source or schema cannot clear Unknown", async () =>
{
    foreach (var field in new[] { "sourceId", "schemaVersion" })
    {
        var draft = Confirmed(); draft.ImportState = ImportState.Unknown;
        using var backend = new ScriptedHandler((_, _) => { var value = NotStarted(draft); value[field] = "different-identity"; return Task.FromResult(value); });
        using var http = new HttpClient(backend);
        await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
        Assert(store.Find(draft.Id)!.ImportState == ImportState.Unknown && backend.Calls.Single().Path.EndsWith("/readback"), "Unrelated checkpoint cleared Unknown for " + field);
    }
});
await Test("notStarted cannot erase an existing verified remote identity", async () =>
{
    var draft = Confirmed(); draft.RemNoteResult = Verify(draft.ConfirmedRemNote!, ForDraft(draft)); draft.ImportState = ImportState.Unknown;
    var identity = JsonSerializer.Serialize(draft.RemNoteResult, WorkflowJson.Options);
    using var backend = new ScriptedHandler((_, _) => Task.FromResult(NotStarted(draft))); using var http = new HttpClient(backend);
    await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    var saved = store.Find(draft.Id)!;
    Assert(saved.ImportState == ImportState.Unknown && JsonSerializer.Serialize(saved.RemNoteResult, WorkflowJson.Options) == identity && backend.Calls.Single().Path.EndsWith("/readback"), "Conflicting no-write checkpoint discarded an established remote identity.");
});
await Test("absence of a recorded import is never treated as a positive notStarted checkpoint", async () =>
{
    var draft = Confirmed(new() { RemNoteSdkEndpoint = "http://[::1]:43101/" }); draft.ImportState = ImportState.Unknown;
    var snapshot = FrozenSnapshot(draft);
    using var backend = new ScriptedHandler((_, _) => Task.FromResult(JsonNode.Parse("{\"error\":\"No recorded import for this source\"}")!)) { Status = HttpStatusCode.Conflict };
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await RejectAsync<InvalidDataException>(() => client.ImportAsync(draft, store)); draft = store.Find(draft.Id)!;
    await RejectAsync<InvalidDataException>(() => client.ImportAsync(draft, store));
    Assert(store.Find(draft.Id)!.ImportState == ImportState.Unknown && backend.Calls.Count == 2 && backend.Calls.All(c => c.Path.EndsWith("/readback")), "Missing record permitted a blind duplicate export.");
    Assert(backend.Calls.All(call => call.Address.Port == 43111 && !JsonNode.Parse(call.Body!)!.AsObject().ContainsKey("newSource")) &&
        FrozenSnapshot(store.Find(draft.Id)!) == snapshot, "New host journal absence cleared identity or requested a replacement source.");
});
ClipDraft ImageDraft(ImportState state = ImportState.Unknown)
{
    var draft = Confirmed(); draft.ConfirmedRemNote = Fixtures.ImagesPlan() with { SourceId = "knowledgeclipper." + draft.Id };
    draft.ImportState = state; return draft;
}
JsonNode ManagedImages(ClipDraft draft) { var value = Fixtures.Images(); value["sourceId"] = draft.ConfirmedRemNote!.SourceId; return value; }
JsonNode PendingImages(ClipDraft draft)
{
    var value = ManagedImages(draft); var nodes = value["cards"]![0]!["nodes"]!;
    foreach (var (nodeIndex, uri) in new[] { (0, "file:///D:/first.png"), (1, "file:///D:/answer.png") })
    {
        nodes[nodeIndex]!["text"]![0]!["url"] = uri;
        var media = nodes[nodeIndex]!["media"]![0]!.AsObject(); media["uri"] = uri;
        media.Remove("sha256"); media.Remove("sizeBytes"); media.Remove("mimeType"); media["error"] = "Image is awaiting RemNote-managed storage";
    }
    value["mediaPending"] = true;
    value["mediaRecovery"] = new JsonObject { ["eligible"] = true, ["kind"] = "finalize-local-images", ["pendingImages"] = 2 };
    return value;
}
await Test("eligible unknown media restores existing image slots once between two pure readbacks", async () =>
{
    var draft = ImageDraft(); var readbacks = 0; var before = JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options);
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/readback")) return Task.FromResult(++readbacks == 1 ? PendingImages(draft) : ManagedImages(draft));
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        Assert(call.Path.EndsWith("/finalize-media"), "Recovery tried to create a new export.");
        var saved = store.Find(draft.Id)!;
        Assert(saved.ImportState == ImportState.Unknown && saved.RemNoteResult!.Evidence.GetProperty("mediaPending").GetBoolean() &&
            ExportRouting.ResultSummary(saved) == "RemNote 导入待核对", "Identity was not durably pinned as incomplete before media mutation.");
        var request = JsonNode.Parse(call.Body!)!; var expected = request["expected"]!;
        Assert(request["plan"]!["sourceId"]!.GetValue<string>() == draft.ConfirmedRemNote!.SourceId && expected["rootRemId"]!.GetValue<string>() == "root-document" &&
            expected["documentRemId"]!.GetValue<string>() == "dir-mode" && expected["cards"]![0]!["key"]!.GetValue<string>() == "card.0" &&
            expected["cards"]![0]!["remIds"]![0]!.GetValue<string>() == "image-rem" && expected["cards"]![0]!["childRemIds"]![0]!.GetValue<string>() == "answer-image" &&
            expected["cards"]![0]!["cardIds"]![0]!.GetValue<string>() == "image-card", "Finalize omitted the exact readback identity snapshot.");
        Assert(JsonSerializer.Serialize(request["plan"]!.Deserialize<RemNoteExportPlan>(WorkflowJson.Options), WorkflowJson.Options) == before,
            "Finalize changed the frozen source plan.");
        return Task.FromResult(ManagedImages(draft));
    });
    using var http = new HttpClient(backend); await new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Select(c => c.Path.Split('/').Last()).SequenceEqual(["readback", "health", "finalize-media", "readback"]), "Recovery did not use the single-write readback boundary.");
    Assert(store.Find(draft.Id)!.RemNoteResult!.RemIds.Single() == "image-rem" && JsonSerializer.Serialize(draft.ConfirmedRemNote, WorkflowJson.Options) == before, "Media recovery changed content identity.");
});
foreach (var (name, mutate) in new (string, Action<JsonNode>)[]
{
    ("source ID", j => j["sourceId"] = "wrong-source"),
    ("schema", j => j["schemaVersion"] = "wrong-schema"),
    ("recovery count", j => j["mediaRecovery"]!["pendingImages"] = 3),
    ("recovery kind", j => j["mediaRecovery"]!["kind"] = "unknown-repair"),
    ("front location", j => j["cards"]![0]!["nodes"]![0]!["parentId"] = "wrong-directory"),
    ("raw text", j => j["cards"]![0]!["nodes"]![0]!["text"]![1]!["text"] = "changed text"),
    ("pending source URI", j => { j["cards"]![0]!["nodes"]![0]!["text"]![0]!["url"] = "file:///D:/other.png"; j["cards"]![0]!["nodes"]![0]!["media"]![0]!["uri"] = "file:///D:/other.png"; }),
    ("image slot index", j => j["cards"]![0]!["nodes"]![0]!["media"]![0]!["elementIndex"] = 1),
    ("image ordinal", j => j["cards"]![0]!["nodes"]![0]!["media"]![0]!["imageIndex"] = 1),
    ("already managed SHA", j => j["cards"]![0]!["nodes"]![0]!["media"]![1]!["sha256"] = Fixtures.HashC),
    ("forged pending SHA", j => j["cards"]![0]!["nodes"]![0]!["media"]![0]!["sha256"] = Fixtures.HashA),
}) await Test("media recovery rejects changed " + name + " before any health or mutation", async () =>
{
    var draft = ImageDraft(); using var backend = new ScriptedHandler((_, _) => { var value = PendingImages(draft); mutate(value); return Task.FromResult(value); });
    using var http = new HttpClient(backend); await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(store.Find(draft.Id)!.ImportState == ImportState.Unknown && backend.Calls.Single().Path.EndsWith("/readback"), "Invalid recovery evidence permitted a mutation.");
});
await Test("prior receipt child identities prevent a changed answer node from entering media recovery", async () =>
{
    var draft = ImageDraft(); draft.RemNoteResult = Verify(draft.ConfirmedRemNote!, ManagedImages(draft));
    using var backend = new ScriptedHandler((_, _) =>
    {
        var value = PendingImages(draft); var card = value["cards"]![0]!; card["childRemIds"] = new JsonArray("replacement-child");
        card["nodes"]![0]!["children"] = new JsonArray("replacement-child"); card["nodes"]![1]!["remId"] = "replacement-child"; return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(draft.ImportState == ImportState.Unknown && backend.Calls.Count == 1, "Existing child identities were lost before media mutation.");
});
await Test("an old plugin without the media-finalize capability cannot receive the recovery write", async () =>
{
    var draft = ImageDraft(); using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/readback")) return Task.FromResult(PendingImages(draft));
        var health = Fixtures.Health(); health.AsObject().Remove("mediaFinalizeSupported"); return Task.FromResult(health);
    });
    using var http = new HttpClient(backend); await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(draft.ImportState == ImportState.Unknown && backend.Calls.Select(c => c.Path.Split('/').Last()).SequenceEqual(["readback", "health"]), "Unsupported media recovery wrote or lost Unknown.");
});
await Test("media finalization is not repeated while its resulting managed images are still pending", async () =>
{
    var draft = ImageDraft(); var reads = 0;
    using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() :
        call.Path.EndsWith("/readback") && ++reads >= 3 ? ManagedImages(draft) : PendingImages(draft)));
    using var http = new HttpClient(backend); await new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Count(c => c.Path.EndsWith("/finalize-media")) == 1 && backend.Calls.All(c => !c.Path.EndsWith("/export")), "Pending poll repeated the media mutation.");
});
foreach (var driftAt in new[] { "finalize-media", "final-readback" })
    await Test("media recovery pins every child identity through " + driftAt, async () =>
{
    var draft = ImageDraft(); var reads = 0;
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        if (call.Path.EndsWith("/readback") && ++reads == 1) return Task.FromResult(PendingImages(draft));
        var value = ManagedImages(draft);
        if (call.Path.EndsWith("/" + driftAt) || driftAt == "final-readback" && call.Path.EndsWith("/readback"))
        {
            var card = value["cards"]![0]!; card["childRemIds"] = new JsonArray("replacement-child");
            card["nodes"]![0]!["children"] = new JsonArray("replacement-child"); card["nodes"]![1]!["remId"] = "replacement-child";
        }
        return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(store.Find(draft.Id)!.ImportState == ImportState.Unknown && draft.RemNoteResult!.Evidence.GetProperty("mediaPending").GetBoolean() && backend.Calls.Count(c => c.Path.EndsWith("/finalize-media")) == 1 && backend.Calls.All(c => !c.Path.EndsWith("/export")), "Media recovery accepted new card identities.");
});
await Test("a lost finalize response remains Unknown and next retry only reads existing managed images", async () =>
{
    var draft = ImageDraft(); var finalized = false;
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        if (call.Path.EndsWith("/finalize-media")) { finalized = true; throw new TaskCanceledException("Lost media-finalize response"); }
        return Task.FromResult(finalized ? ManagedImages(draft) : PendingImages(draft));
    });
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await RejectAsync<TaskCanceledException>(() => client.ImportAsync(draft, store)); draft = store.Find(draft.Id)!;
    Assert(draft.ImportState == ImportState.Unknown, "A lost finalize response cleared Unknown."); await client.ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Count(c => c.Path.EndsWith("/finalize-media")) == 1 && backend.Calls.All(c => !c.Path.EndsWith("/export")), "Recovery retry duplicated the write.");
});
await Test("reopening after a lost finalize response rejects changed child IDs before another mutation", async () =>
{
    var draft = ImageDraft(); var lost = false;
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        if (call.Path.EndsWith("/finalize-media")) { lost = true; throw new TaskCanceledException("Simulated process-boundary lost response"); }
        var value = PendingImages(draft);
        if (lost)
        {
            var card = value["cards"]![0]!; card["childRemIds"] = new JsonArray("replacement-after-reopen");
            card["nodes"]![0]!["children"] = new JsonArray("replacement-after-reopen"); card["nodes"]![1]!["remId"] = "replacement-after-reopen";
        }
        return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await RejectAsync<TaskCanceledException>(() => client.ImportAsync(draft, store));
    draft = new DraftStore(dataRoot).Find(draft.Id)!;
    var pinned = JsonSerializer.Serialize(draft.RemNoteResult, WorkflowJson.Options);
    Assert(draft.RemNoteResult!.Evidence.GetProperty("mediaPending").GetBoolean() && draft.RemNoteResult.RemIds.Single() == "image-rem", "Reload lost pending identity evidence.");
    await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, new DraftStore(dataRoot)));
    Assert(draft.ImportState == ImportState.Unknown && JsonSerializer.Serialize(draft.RemNoteResult, WorkflowJson.Options) == pinned &&
        backend.Calls.Select(c => c.Path.Split('/').Last()).SequenceEqual(["readback", "health", "finalize-media", "readback"]), "Reloaded recovery forgot the original child identity or wrote again.");
});
await Test("media recovery retains an earlier verified receipt when finalization cannot complete", async () =>
{
    var draft = ImageDraft(); draft.RemNoteResult = Verify(draft.ConfirmedRemNote!, ManagedImages(draft));
    var earlier = JsonSerializer.Serialize(draft.RemNoteResult, WorkflowJson.Options);
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        if (call.Path.EndsWith("/finalize-media")) throw new HttpRequestException("Temporary media mutation failure");
        return Task.FromResult(PendingImages(draft));
    });
    using var http = new HttpClient(backend); await RejectAsync<HttpRequestException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(draft.ImportState == ImportState.Unknown && JsonSerializer.Serialize(store.Find(draft.Id)!.RemNoteResult, WorkflowJson.Options) == earlier,
        "Pending media observation overwrote an existing verified identity receipt.");
});
await Test("ordinary pending readback without explicit eligible evidence remains read-only", async () =>
{
    var draft = ImageDraft(); var reads = 0;
    using var backend = new ScriptedHandler((_, _) => { var value = ++reads == 1 ? PendingImages(draft) : ManagedImages(draft); value.AsObject().Remove("mediaRecovery"); return Task.FromResult(value); });
    using var http = new HttpClient(backend); await new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Count == 2 && backend.Calls.All(c => c.Path.EndsWith("/readback")), "Ordinary pending state triggered a recovery mutation.");
});
await Test("explicitly ineligible media recovery retains the source failure reason without a write", async () =>
{
    var draft = ImageDraft(); using var backend = new ScriptedHandler((_, _) =>
    {
        var value = PendingImages(draft); value["mediaRecovery"]!["eligible"] = false;
        value["mediaRecovery"]!["error"] = "Original image SHA-256 changed; recovery refused"; return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(draft.ImportState == ImportState.Unknown && draft.ImportError!.Contains("Original image SHA-256 changed") && backend.Calls.Single().Path.EndsWith("/readback"), "Ineligible recovery hid the source error or sent a write.");
});
await Test("fully managed image readback needs no new media mutation capability", async () =>
{
    var draft = ImageDraft(); using var backend = new ScriptedHandler((_, _) =>
    {
        var value = ManagedImages(draft);
        value["mediaRecovery"] = new JsonObject { ["eligible"] = false, ["kind"] = "finalize-local-images", ["error"] = "Refresh plugin to enable new media finalization" };
        return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); await new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Single().Path.EndsWith("/readback"), "An unused mutation capability blocked safe managed-image verification.");
});
await Test("a new export never enters the legacy finalize path even if its first result advertises eligibility", async () =>
{
    var draft = ImageDraft(ImportState.Ready);
    using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() : call.Path.EndsWith("/export") ? PendingImages(draft) : ManagedImages(draft)));
    using var http = new HttpClient(backend); await new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Select(c => c.Path.Split('/').Last()).SequenceEqual(["health", "export", "readback"]), "New import performed a legacy repair.");
});
await Test("asynchronous card creation polls readback without a second export", async () =>
{
    var draft = Confirmed(); using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        var value = ForDraft(draft);
        if (call.Path.EndsWith("/export")) { value["cards"]![0]!["cardIds"] = new JsonArray(); value["cards"]![0]!["nodes"]![0]!["cardIds"] = new JsonArray(); }
        return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); await new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Count == 3 && backend.Calls.Last().Path.EndsWith("/readback"), "Pending native card did not reconcile.");
});
await Test("concurrent duplicate import requests share the persisted verification gate", async () =>
{
    var draft = Confirmed(); using var backend = new ScriptedHandler(async (call, ct) => { await Task.Delay(25, ct); return call.Path.EndsWith("/health") ? Fixtures.Health() : ForDraft(draft); });
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await Task.WhenAll(client.ImportAsync(draft, store), client.ImportAsync(draft, store));
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Count(c => c.Path.EndsWith("/export")) == 1 && draft.ImportGate.CurrentCount == 1, "Concurrent imports duplicated external write.");
});
await Test("destination router sends RemNote confirmations to SDK and never AnkiConnect", async () =>
{
    var draft = Confirmed(); using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() : ForDraft(draft)));
    using var http = new HttpClient(backend); await new CardImportRouter(http).ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.All(c => c.Path.StartsWith("/sdk/knowledge-clipper/")), "Router used the wrong API.");
});
await Test("a two-round question batch without mode configuration defaults both cards to RemNote", async () =>
{
    var parent = Draft("question", new() { QuestionCardIncludeContext = false }); parent.Configuration = new("", []);
    parent.Conversation = [new(parent.QuestionText, new("answered", "Answer A", [])), new("Second question?", new("answered", "Answer B", []))];
    parent.Content = new("question", Basic: new(parent.QuestionText, "Answer A"), Answer: parent.Conversation[0].Answer);
    using var backend = new ScriptedHandler((call, _) =>
    {
        Assert(parent.FollowUpCards.Count == 2 && parent.FollowUpCards.Values.All(id => store.Find(id) is { ConfirmedDestination: ExportDestination.RemNote, ConfirmedRemNote: not null }), "Whole question batch was not frozen before network.");
        if (call.Path.EndsWith("/health")) return Task.FromResult(Fixtures.Health());
        var body = JsonNode.Parse(call.Body!)!;
        var source = (body["plan"] ?? body)["sourceId"]!.GetValue<string>();
        var first = source == "knowledgeclipper." + parent.FollowUpCards[0];
        Assert(first || source == "knowledgeclipper." + parent.FollowUpCards[1], "Unexpected card identity.");
        var value = Fixtures.Basic(); value["sourceId"] = source;
        var card = value["cards"]![0]!; card["childRemIds"] = new JsonArray("answer-1"); card["nodes"]!.AsArray().RemoveAt(2);
        card["nodes"]![0]!["children"] = new JsonArray("answer-1");
        card["nodes"]![0]!["text"]![0]!["text"] = first ? "First\nSecond" : "Second question?";
        card["nodes"]![1]!["text"]![0]!["text"] = first ? "Answer A" : "Answer B";
        return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); var router = new CardImportRouter(http);
    await QuestionCards.ImportAsync(parent, [0, 1], store, router, () => { });
    Assert(QuestionCards.Preview(parent, store).All(row => row.State == ImportState.Verified && row.DestinationLabel.StartsWith("RemNote · ")), "Question batch lost per-row destination.");
    var calls = backend.Calls.Count; await QuestionCards.ImportAsync(parent, [0, 1], store, router, () => { });
    Assert(backend.Calls.Count == calls && calls == 4, "Repeated question batch re-exported verified rows.");
});
await Test("mixed language defaults freeze each child's own destination before any provider call", async () =>
{
    var material = new CaptureMaterial("lead", "They lead the team.", "source", 5, "Public fixture", 0, DateTimeOffset.UtcNow);
    var parent = new ClipDraft { Original = material, Material = material, Settings = new() { ModeDestinations = new() { ["en-writing"] = ExportDestination.RemNote } },
        Configuration = new("", definitions.Select(d => new ModeSnapshot(d, "", "")).ToArray()), Practice = PracticeChoice.Both };
    var selected = definitions.Where(d => d.Id is "en-reading" or "en-writing").ToArray();
    foreach (var child in LanguageModeCards.Ensure(parent, selected, store))
    {
        child.Content = new(child.ModeId!, new("/liːd/", "lead the group", null, null)); store.Save(child);
    }
    var importer = new RecordingImporter((_, _) =>
    {
        var children = LanguageModeCards.AllChildren(parent, store);
        Assert(children.All(child => child.ConfirmedNote != null) && children.Single(c => c.ModeId == "en-reading").ConfirmedDestination == ExportDestination.Anki &&
            children.Single(c => c.ModeId == "en-writing") is { ConfirmedDestination: ExportDestination.RemNote, ConfirmedRemNote: not null }, "Mixed selection used a shared destination.");
        return Task.CompletedTask;
    });
    await LanguageModeCards.ImportAsync(parent, selected.Select(m => m.Id).ToArray(), store, importer);
    Assert(importer.Destinations.SequenceEqual([ExportDestination.Anki, ExportDestination.RemNote]), "Import dispatch order or targets changed.");
});
await Test("mixed excerpt batch freezes raw ordinary nodes and custom Anki cards independently", async () =>
{
    var excerptStore = new ExcerptStore(Path.Combine(dataRoot, "excerpt-batch"));
    var settings = new LanguageSettings { ModeDestinations = new() { ["summary-local"] = ExportDestination.Anki } };
    var session = ExcerptCards.Create(new("Original paragraph", null, "unavailable", null, "Public fixture", 0, DateTimeOffset.UtcNow), settings, excerptStore);
    var custom = ExcerptCards.Add(session, excerptStore, new("Independent summary", "Summary answer"), "摘要", new(definitions.Single(d => d.Id == "summary-local"), "", ""));
    var importer = new RecordingImporter((_, _) =>
    {
        var raw = excerptStore.Cards.Find(session.Cards[0].Id)!; var savedCustom = excerptStore.Cards.Find(custom.Id)!;
        Assert(raw.ConfirmedRemNote!.Cards.Single().Kind == "plain" && raw.ConfirmedDestination == ExportDestination.RemNote && savedCustom.ConfirmedDestination == ExportDestination.Anki && savedCustom.ConfirmedRemNote == null, "Excerpt confirmation mixed targets or replaced original.");
        return Task.CompletedTask;
    });
    await ExcerptCards.ImportAsync(session, excerptStore, importer, session.Cards.Select(c => c.Id));
    Assert(importer.Destinations.SequenceEqual([ExportDestination.RemNote, ExportDestination.Anki]), "Excerpt targets changed at dispatch.");
});

JsonNode SpacingFor(ClipDraft draft, bool pending)
{
    var value = Fixtures.WithSeparator(ForDraft(draft));
    if (pending)
    {
        value["batchSeparatorPending"] = true; value["spacingRecovery"] = new JsonObject { ["eligible"] = true, ["kind"] = "finalize-batch-separator" };
        value["batchSeparator"]!["node"]!["practiceEnabled"] = true; value["documentChildren"] = new JsonArray("batch-blank", "front-rem");
    }
    return value;
}
JsonNode SpacingHealth() { var value = Fixtures.Health(); value["spacingFinalizeSupported"] = true; return value; }
await TestSync("pending spacing evidence is never a completed import even when the blank already looks empty", () =>
{
    var value = Fixtures.WithSeparator(Fixtures.Basic()); value["batchSeparatorPending"] = true;
    Reject(() => Verify(basicPlan, value));
});
foreach (var orphan in new[] { false, true })
await Test("unknown spacing recovery pins original IDs and explicitly finalizes " + (orphan ? "an unattached blank" : "the existing batch order"), async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Unknown; var finished = false;
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(SpacingHealth());
        if (call.Path.EndsWith("/finalize-spacing"))
        {
            var saved = store.Find(draft.Id)!;
            Assert(saved.ImportState == ImportState.Unknown && saved.RemNoteResult!.Evidence.GetProperty("batchSeparatorPending").GetBoolean(), "Spacing identity was not persisted before the explicit write.");
            var payload = JsonNode.Parse(call.Body!)!;
            Assert(payload["separatorRemId"]!.GetValue<string>() == "batch-blank" && payload["expected"]!["cards"]![0]!["childRemIds"]!.AsArray().Count == 2, "Recovery omitted exact blank or child identities.");
            finished = true;
        }
        var value = SpacingFor(draft, !finished);
        if (orphan && !finished) { value["batchSeparator"]!["node"]!["parentId"] = null; value["documentChildren"] = new JsonArray("front-rem"); }
        return Task.FromResult(value);
    });
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await client.ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Select(call => call.Path.Split('/').Last()).SequenceEqual(["readback", "health", "finalize-spacing", "readback"]), "Spacing repair was not bounded by pure readbacks.");
    var count = backend.Calls.Count; await client.ImportAsync(draft, store); Assert(backend.Calls.Count == count, "Verified spacing triggered another write.");
});
foreach (var (name, mutate) in new (string, Action<JsonNode>)[]
{
    ("blank user text", value => value["batchSeparator"]!["node"]!["text"] = new JsonArray("User edit")),
    ("blank moved to another directory", value => value["batchSeparator"]!["node"]!["parentId"] = "other-directory"),
    ("foreign node interleaved in batch", value => value["documentChildren"] = new JsonArray("front-rem", "user-node", "batch-blank")),
    ("wrong source", value => value["sourceId"] = "another-source"),
    ("wrong recovery kind", value => value["spacingRecovery"]!["kind"] = "create-new-blank"),
}) await Test("spacing recovery refuses " + name + " before a mutation", async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Unknown;
    using var backend = new ScriptedHandler((_, _) => { var value = SpacingFor(draft, true); mutate(value); return Task.FromResult(value); });
    using var http = new HttpClient(backend);
    await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(draft.ImportState == ImportState.Unknown && backend.Calls.Single().Path.EndsWith("/readback"), "Invalid spacing evidence received a write.");
});
await Test("old plugins cannot receive a spacing finalization write", async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Unknown;
    using var backend = new ScriptedHandler((call, _) => Task.FromResult(call.Path.EndsWith("/health") ? Fixtures.Health() : SpacingFor(draft, true)));
    using var http = new HttpClient(backend);
    await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(draft.RemNoteResult != null && draft.ImportState == ImportState.Unknown && backend.Calls.Count == 2 && backend.Calls.All(call => !call.Path.EndsWith("/finalize-spacing")), "Missing capability lost pinned identity or permitted a write.");
});
await Test("a lost spacing finalize response reopens with identical IDs and only reads back", async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Unknown; var finished = false;
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(SpacingHealth());
        if (call.Path.EndsWith("/finalize-spacing")) { finished = true; throw new TaskCanceledException("Lost same-ID spacing response"); }
        return Task.FromResult(SpacingFor(draft, !finished));
    });
    using var http = new HttpClient(backend); var client = new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint);
    await RejectAsync<TaskCanceledException>(() => client.ImportAsync(draft, store));
    draft = store.Find(draft.Id)!;
    Assert(draft.ImportState == ImportState.Unknown && draft.RemNoteResult!.Evidence.GetProperty("batchSeparator").GetProperty("remId").GetString() == "batch-blank", "Lost response lost the separator identity.");
    await client.ImportAsync(draft, store);
    Assert(draft.ImportState == ImportState.Verified && backend.Calls.Count(call => call.Path.EndsWith("/finalize-spacing")) == 1 && backend.Calls.All(call => !call.Path.EndsWith("/export")), "Reopened spacing duplicated export or finalization.");
});
await Test("finalized spacing cannot substitute a new blank identity", async () =>
{
    var draft = Confirmed(); draft.ImportState = ImportState.Unknown;
    using var backend = new ScriptedHandler((call, _) =>
    {
        if (call.Path.EndsWith("/health")) return Task.FromResult(SpacingHealth());
        var value = SpacingFor(draft, !call.Path.EndsWith("/finalize-spacing"));
        if (call.Path.EndsWith("/finalize-spacing"))
        {
            value["batchSeparator"]!["remId"] = "replacement"; value["batchSeparator"]!["node"]!["remId"] = "replacement";
            value["documentChildren"] = new JsonArray("front-rem", "replacement");
        }
        return Task.FromResult(value);
    });
    using var http = new HttpClient(backend);
    await RejectAsync<InvalidDataException>(() => new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).ImportAsync(draft, store));
    Assert(draft.ImportState == ImportState.Unknown && draft.RemNoteResult!.Evidence.GetProperty("batchSeparator").GetProperty("remId").GetString() == "batch-blank", "Finalization changed the original blank identity.");
});

WorkflowJson.Save(receiptPath, new { success = results.All(r => r.Passed), count = results.Count, passed = results.Count(r => r.Passed),
    failed = results.Count(r => !r.Passed), network = "isolated HttpMessageHandler fixtures; no live RemNote or Anki calls", dataRoot, checks = results });
Console.WriteLine($"{results.Count(r => r.Passed)}/{results.Count} passed; receipt: {receiptPath}");
Environment.ExitCode = results.Any(r => !r.Passed) ? 1 : 0;

sealed record CheckResult(string Name, bool Passed, string? Error);
sealed record WireCall(string Method, string Path, string? Protocol, string? Body, Uri Address);
sealed class RecordingImporter(Func<ClipDraft, DraftStore, Task> inspect) : ICardImporter
{
    public ExportDestination? FixedDestination => null;
    public List<ExportDestination> Destinations { get; } = [];
    public async Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
    {
        await inspect(draft, store); Destinations.Add(ExportRouting.For(draft));
    }
}
sealed class ScriptedHandler(Func<WireCall, CancellationToken, Task<JsonNode>> handle) : HttpMessageHandler
{
    public List<WireCall> Calls { get; } = [];
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = new WireCall(request.Method.Method, request.RequestUri!.AbsolutePath,
            request.Headers.TryGetValues("X-KnowledgeClipper-Protocol", out var protocol) ? protocol.Single() : null,
            request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.RequestUri);
        Calls.Add(call); var body = await handle(call, cancellationToken);
        return new(Status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
static class Fixtures
{
    public const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    public static JsonNode WithSeparator(JsonNode value)
    {
        var ids = value["cards"]!.AsArray().SelectMany(card => card!["remIds"]!.AsArray().Select(id => id!.GetValue<string>())).ToArray();
        value["documentChildren"] = new JsonArray(ids.Append("batch-blank").Select(id => JsonValue.Create(id)).ToArray());
        value["batchSeparator"] = new JsonObject { ["remId"] = "batch-blank", ["previousRemId"] = ids[^1], ["node"] = JsonNode.Parse("""
            {"remId":"batch-blank","parentId":"dir-mode","text":[],"backText":[],"children":[],"remType":0,
             "isDocument":false,"isCardItem":false,"isExtraCardDetail":false,"practiceEnabled":false,"practiceDirection":"none","cardIds":[],"cardTypes":[]}
            """) };
        return value;
    }
    public static JsonNode Health() => JsonNode.Parse("""{"connected":true,"supported":true,"acceptWriteOperations":true,"mediaFinalizeSupported":true,"transport":"sdk-http"}""")!;
    public static RemNoteExportPlan BasicPlan() => new("knowledgeclipper.fixture", "零碎知识剪藏", "root-document", ["摘录"],
        [new("card.0", "basic", [new("text", "First\nSecond")], [[new("text", "Answer A")], [new("text", "Answer B")]])]);
    public static JsonNode Basic() => JsonNode.Parse("""
        {"schemaVersion":"knowledge-clipper.remnote.v1","sourceId":"knowledgeclipper.fixture","rootRemId":"root-document","documentRemId":"dir-mode","mediaPending":false,
         "cards":[{"key":"card.0","kind":"basic","reused":false,"remIds":["front-rem"],"childRemIds":["answer-1","answer-2"],"cardIds":["practice-1"],
           "front":[{"kind":"text","text":"First\nSecond"}],"back":[[{"kind":"text","text":"Answer A"}],[{"kind":"text","text":"Answer B"}]],"nodes":[
           {"remId":"front-rem","parentId":"dir-mode","text":[{"i":"m","text":"First\nSecond"}],"backText":[],"children":["answer-1","answer-2"],"remType":0,"isDocument":false,"isCardItem":false,"isExtraCardDetail":false,"practiceEnabled":true,"practiceDirection":"forward","cardIds":["practice-1"],"cardTypes":["forward"],"media":[]},
           {"remId":"answer-1","parentId":"front-rem","text":[{"i":"m","text":"Answer A"}],"backText":[],"children":[],"remType":0,"isDocument":false,"isCardItem":true,"isExtraCardDetail":false,"practiceEnabled":false,"practiceDirection":"none","cardIds":[],"cardTypes":[],"media":[]},
           {"remId":"answer-2","parentId":"front-rem","text":[{"i":"m","text":"Answer B"}],"backText":[],"children":[],"remType":0,"isDocument":false,"isCardItem":true,"isExtraCardDetail":false,"practiceEnabled":false,"practiceDirection":"none","cardIds":[],"cardTypes":[],"media":[]}]}]}
        """)!;
    public static RemNoteExportPlan PlainPlan() => new("knowledgeclipper.fixture", "零碎知识剪藏", "root-document", ["摘录"],
        [new("plain.0", "plain", [new("text", "Paragraph one")], []), new("plain.1", "plain", [new("text", "Paragraph two")], [])]);
    public static JsonNode Plain() => JsonNode.Parse("""
        {"schemaVersion":"knowledge-clipper.remnote.v1","sourceId":"knowledgeclipper.fixture","rootRemId":"root-document","documentRemId":"dir-mode","mediaPending":false,"cards":[
         {"key":"plain.0","kind":"plain","remIds":["plain-one"],"childRemIds":[],"cardIds":[],"nodes":[{"remId":"plain-one","parentId":"dir-mode","text":[{"i":"m","text":"Paragraph one"}],"backText":[],"children":[],"remType":0,"isDocument":false,"isCardItem":false,"isExtraCardDetail":false,"practiceEnabled":false,"practiceDirection":"none","cardIds":[],"cardTypes":[],"media":[]}]},
         {"key":"plain.1","kind":"plain","remIds":["plain-two"],"childRemIds":[],"cardIds":[],"nodes":[{"remId":"plain-two","parentId":"dir-mode","text":[{"i":"m","text":"Paragraph two"}],"backText":[],"children":[],"remType":0,"isDocument":false,"isCardItem":false,"isExtraCardDetail":false,"practiceEnabled":false,"practiceDirection":"none","cardIds":[],"cardTypes":[],"media":[]}]}]}
        """)!;
    public static RemNoteExportPlan ClozePlan() => new("knowledgeclipper.fixture", "零碎知识剪藏", "root-document", ["摘录"],
        [new("card.0", "cloze", [new("cloze", "Alpha", ClozeId: 1, Hint: "first"), new("text", " and "), new("cloze", "beta", ClozeId: 1), new("text", " follow "), new("cloze", "gamma", ClozeId: 2)], [[new("text", "Extra explanation")]])]);
    public static JsonNode Cloze() => JsonNode.Parse("""
        {"schemaVersion":"knowledge-clipper.remnote.v1","sourceId":"knowledgeclipper.fixture","rootRemId":"root-document","documentRemId":"dir-mode","mediaPending":false,"cards":[
         {"key":"card.0","kind":"cloze","remIds":["cloze-rem"],"childRemIds":["extra-rem"],"cardIds":["cloze-one","cloze-two"],"nodes":[
          {"remId":"cloze-rem","parentId":"dir-mode","text":[{"i":"m","text":"Alpha","cId":"kc_1","cloze-hint":"first"},{"i":"m","text":" and "},{"i":"m","text":"beta","cId":"kc_1"},{"i":"m","text":" follow "},{"i":"m","text":"gamma","cId":"kc_2"}],"backText":[],"children":["extra-rem"],"remType":0,"isDocument":false,"isCardItem":false,"isExtraCardDetail":false,"practiceEnabled":true,"practiceDirection":"none","cardIds":["cloze-one","cloze-two"],"cardTypes":[{"clozeId":"kc_1"},{"clozeId":"kc_2"}],"media":[]},
          {"remId":"extra-rem","parentId":"cloze-rem","text":[{"i":"m","text":"Extra explanation"}],"backText":[],"children":[],"remType":0,"isDocument":false,"isCardItem":false,"isExtraCardDetail":true,"practiceEnabled":false,"practiceDirection":"none","cardIds":[],"cardTypes":[],"media":[]}]}]}
        """)!;
    public static RemNoteExportPlan ImagesPlan() => new("knowledgeclipper.fixture", "零碎知识剪藏", "root-document", ["图片", "原图"],
        [new("card.0", "basic", [new("image", Uri: "file:///D:/first.png", Sha256: HashA), new("text", "\n"), new("image", Uri: "file:///D:/second.png", Sha256: HashB)],
            [[new("image", Uri: "file:///D:/answer.png", Sha256: HashC)]])]);
    public static JsonNode Images() => JsonNode.Parse("""
        {"schemaVersion":"knowledge-clipper.remnote.v1","sourceId":"knowledgeclipper.fixture","rootRemId":"root-document","documentRemId":"dir-mode","mediaPending":false,"cards":[
         {"key":"card.0","kind":"basic","remIds":["image-rem"],"childRemIds":["answer-image"],"cardIds":["image-card"],"nodes":[
          {"remId":"image-rem","parentId":"dir-mode","text":[{"i":"i","url":"%LOCAL_FILE%first.png"},{"i":"m","text":"\n"},{"i":"i","url":"%LOCAL_FILE%second.png"}],"backText":[],"children":["answer-image"],"remType":0,"isDocument":false,"isCardItem":false,"isExtraCardDetail":false,"practiceEnabled":true,"practiceDirection":"forward","cardIds":["image-card"],"cardTypes":["forward"],"media":[
           {"elementIndex":0,"imageIndex":0,"uri":"%LOCAL_FILE%first.png","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":91,"mimeType":"image/png"},
           {"elementIndex":2,"imageIndex":1,"uri":"%LOCAL_FILE%second.png","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sizeBytes":92,"mimeType":"image/png"}]},
          {"remId":"answer-image","parentId":"image-rem","text":[{"i":"i","url":"%LOCAL_FILE%answer.png"}],"backText":[],"children":[],"remType":0,"isDocument":false,"isCardItem":true,"isExtraCardDetail":false,"practiceEnabled":false,"practiceDirection":"none","cardIds":[],"cardTypes":[],"media":[
           {"elementIndex":0,"imageIndex":0,"uri":"%LOCAL_FILE%answer.png","sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","sizeBytes":93,"mimeType":"image/png"}]}]}]}
        """)!;
}

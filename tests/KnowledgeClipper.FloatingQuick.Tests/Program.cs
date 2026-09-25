using KnowledgeClipper.Workflow;

var root = @"D:\Caches\KnowledgeClipper\floating-quick-tests\" + Guid.NewGuid().ToString("N");
var store = new DraftStore(root);
var excerpts = new ExcerptStore(root);
var checks = new List<string>();
void Check(bool result, string message) { if (!result) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
async Task Reject(Func<Task> action, string message)
{ try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
var material = new CaptureMaterial("The red balloon rises.", "The red balloon rises.", "source", 0, "public fixture", 0, DateTimeOffset.UtcNow);
var modes = new[] { new ModeDefinition("language-en", "English reading", true, "", "", "", Language: "en", Practice: PracticeChoice.Reading),
    new ModeDefinition("language-writing-en", "English writing", true, "", "", "", Language: "en", Practice: PracticeChoice.Writing) };
var configuration = new ContentConfiguration("", modes.Select(m => new ModeSnapshot(m, "test fixture", "{}")).ToArray());
ClipDraft Language(PracticeChoice practice = PracticeChoice.Reading)
{
    var parent = new ClipDraft { Original = material, Material = material, Settings = new(), Configuration = configuration,
        Input = new("selection-dot", "language"), ModeId = "language", Route = new("language", "language", false, false), Practice = practice };
    foreach (var child in LanguageModeCards.Ensure(parent, LanguageModes.Select(configuration, material, practice), store))
    { child.Content = new(child.ModeId!, new("/ðə red bəˈluːn raɪzɪz/", "红色气球升起。", null, null)); store.Save(child); }
    return parent;
}
var importer = new Importer();
var language = Language();
await FloatingQuickImport.LanguageOrQuestionAsync(language, store, importer);
Check(importer.Calls == 1 && language.SelectedModeIds.SequenceEqual(new[] { "language-en" }), "language imports only the configured reading mode");
Check(importer.Destinations.Single() == ExportDestination.Anki, "language keeps default Anki destination");
await FloatingQuickImport.LanguageOrQuestionAsync(language, store, importer);
Check(importer.Calls == 1, "repeating verified shortcut does not add a card");
var both = Language(PracticeChoice.Both);
await FloatingQuickImport.LanguageOrQuestionAsync(both, store, importer);
Check(importer.Calls == 3 && LanguageModeCards.Children(both, store).All(c => c.ImportState == ImportState.Verified), "Both respects the user's two default language modes");
var unresolved = Language(PracticeChoice.Both);
var bad = LanguageModeCards.Children(unresolved, store)[1]; bad.ContentError = "independent mode failed"; store.Save(bad);
await Reject(() => FloatingQuickImport.LanguageOrQuestionAsync(unresolved, store, importer), "failed default sibling prevents all automatic writes");
Check(importer.Calls == 3, "preflight checks all default language cards before importing any");
var direct = new ClipDraft { Original = material, Material = material, Settings = new(), Configuration = configuration,
    ModeId = modes[0].Id, Route = new(modes[0].Id, "language", false, false), Practice = PracticeChoice.Reading,
    Input = new("selection-dot", "language"), Content = new(modes[0].Id, new("/ðə red bəˈluːn raɪzɪz/", "红色气球升起。", null, null)) };
await FloatingQuickImport.LanguageOrQuestionAsync(direct, store, importer);
Check(direct.ConfirmedNote != null && direct.ImportState == ImportState.Verified, "single-result language mode freezes confirmation before its importer runs");
var unknownImporter = new Importer { Result = ImportState.Unknown };
var uncertain = Language();
await Reject(() => FloatingQuickImport.LanguageOrQuestionAsync(uncertain, store, unknownImporter), "unknown import is not reported as shortcut success");
Check(LanguageModeCards.Children(uncertain, store).Single().ConfirmedNote != null, "failed import retains frozen note for normal preview recovery");

var questionDefinition = new ModeDefinition("question", "问答", true, "", "", "", "question", RequiresContext: false);
ClipDraft Question() => new() { Original = material, Material = material, Settings = new(),
    Configuration = new("", [new(questionDefinition, "test", "{}")]), Input = new("selection-dot", "question", ExplicitQuestion: "What rises?"),
    ModeId = "question", Route = new("question", "question", true, false),
    Content = new("question", Answer: new("answered", "The red balloon.", [])),
    Conversation = [new("What rises?", new("answered", "The red balloon.", []))] };
var question = Question(); store.Save(question);
await FloatingQuickImport.LanguageOrQuestionAsync(question, store, importer);
Check(QuestionCards.Preview(question, store).Single().State == ImportState.Verified && question.Conversation.Count == 1, "question shortcut imports exactly the initial answer");
Check(importer.Destinations.Last() == ExportDestination.RemNote, "question keeps default RemNote destination");
var followed = Question(); followed.Conversation.Add(new("Why?", new("answered", "Buoyancy.", []))); store.Save(followed);
await Reject(() => FloatingQuickImport.LanguageOrQuestionAsync(followed, store, importer), "automatic path refuses a follow-up conversation");

var excerpt = ExcerptCards.Create(material, new(), excerpts);
await FloatingQuickImport.ExcerptAsync(excerpt, excerpts, importer);
var original = ExcerptCards.Get(excerpt, excerpts, excerpt.Cards.Single().Id);
Check(original.Excerpt!.Front == material.SelectedText && original.Excerpt.Back == "" && original.Content == null, "excerpt shortcut preserves original text with blank back and no model result");
Check(original.ConfirmedRemNote!.Cards.All(c => c.Kind == "plain"), "RemNote original excerpt creates ordinary text without review cards");
var calls = importer.Calls;
await FloatingQuickImport.ExcerptAsync(excerpt, excerpts, importer);
Check(importer.Calls == calls, "verified original excerpt is not imported twice");
var multi = ExcerptCards.Create(material, new(), excerpts);
ExcerptCards.Add(multi, excerpts, new("Another front", "Answer"), "manual");
await Reject(() => FloatingQuickImport.ExcerptAsync(multi, excerpts, importer), "shortcut cannot import additional manual or prompted cards");
var failed = ExcerptCards.Create(material, new(), excerpts);
await Reject(() => FloatingQuickImport.ExcerptAsync(failed, excerpts, unknownImporter), "uncertain excerpt must return to its saved normal preview");
Check(ExcerptCards.Get(failed, excerpts, failed.Cards.Single().Id).ImportState == ImportState.Unknown, "unknown result and identity remain durable");

if (args.Length > 0) WorkflowJson.Save(args[0], new { passed = true, checks, fixtureRoot = root });
Console.WriteLine($"PASS {checks.Count} quick import checks");

sealed class Importer : ICardImporter
{
    public ExportDestination? FixedDestination => null;
    public int Calls { get; private set; }
    public List<ExportDestination> Destinations { get; } = [];
    public ImportState Result { get; init; } = ImportState.Verified;
    public Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Calls++;
        if (draft.ConfirmedNote == null) throw new InvalidOperationException("Importer requires a frozen confirmation.");
        Destinations.Add(ExportRouting.For(draft)); draft.ImportState = Result; store.Save(draft);
        return Task.CompletedTask;
    }
}

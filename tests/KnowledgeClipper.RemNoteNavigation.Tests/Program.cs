using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
if (args.Contains("--live")) { await LiveNavigation.RunAsync(output); return; }
var checks = new List<string>();
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks.Add(label); Console.WriteLine("PASS " + label); }
var store = new ExcerptStore(Path.Combine(output, "data"));
var material = new CaptureMaterial("Public navigation fixture", null, "unavailable", null, "isolated fixture", 0, DateTimeOffset.UtcNow);
var settings = new LanguageSettings();
foreach (var (fields, name) in new[] {
    (new ExcerptFields("Public first paragraph\nPublic second paragraph", ""), "multiline plain"),
    (new ExcerptFields("Public front", "Public answer"), "basic"),
    (new ExcerptFields("{{c1::Public}} excerpt", "", ExcerptCardKind.Cloze), "cloze") })
{
    var session = new ExcerptSession { Original = material, Settings = settings };
    var draft = ExcerptCards.Add(session, store, fields, name);
    var importer = new Importer();
    await ExcerptCards.ImportAsync(session, store, importer, [draft.Id]);
    Check(importer.Revealed.SequenceEqual([draft.Id]) && store.Cards.Find(draft.Id)!.ImportState == ImportState.Verified, name + " navigates only after verified import");
    await ExcerptCards.ImportAsync(session, store, importer, [draft.Id]);
    Check(importer.Imports == 1 && importer.Revealed.Count == 1, name + " verified retries never import or navigate again");
}
foreach (var failNavigation in new[] { false, true })
{
    var session = ExcerptCards.Create(material, settings, store);
    var second = ExcerptCards.Add(session, store, new("Second public card", "Answer"), "second");
    var importer = new Importer { FailNavigation = failNavigation };
    await ExcerptCards.ImportAsync(session, store, importer, session.Cards.Select(c=>c.Id));
    Check(importer.Imports == 2 && importer.Revealed.SequenceEqual([second.Id]), "batch navigates once to its final successful card; failure=" + failNavigation);
    var saved = store.Cards.Find(second.Id)!;
    Check(saved.ImportState == ImportState.Verified && saved.ImportError == null && (saved.RemNoteRevealError != null) == failNavigation,
        "navigation failure cannot downgrade verified import; failure=" + failNavigation);
    Check(ExportRouting.ResultSummary(saved).Contains("自动定位未完成") == failNavigation, "navigation outcome is separate in user feedback; failure=" + failNavigation);
}
{
    var session = ExcerptCards.Create(material, settings, store);
    var importer = new Importer { FailImport = true };
    try { await ExcerptCards.ImportAsync(session, store, importer, session.Cards.Select(c=>c.Id)); } catch (InvalidOperationException) { }
    Check(importer.Revealed.Count == 0, "failed import cannot change the RemNote page");
}
using (var handler = new NavigationHandler())
using (var http = new HttpClient(handler))
{
    var session = ExcerptCards.Create(material, settings, store);
    var draft = store.Cards.Find(session.Cards[0].Id)!;
    await new Importer().ImportAsync(draft,store.Cards);
    var connection = new RemNoteConnection(http,RemNoteConnection.DefaultEndpoint);
    await connection.RevealAsync(draft);
    Check(handler.Calls==1 && handler.LastPath!.EndsWith("/reveal"), "client issues explicit navigation using verified source and node identity");
    handler.WrongSelection=true;
    try { await connection.RevealAsync(draft); throw new Exception("Wrong selection accepted"); } catch (InvalidDataException) { }
    Check(draft.ImportState==ImportState.Verified,"incorrect navigation readback is rejected without changing import success");
    draft.ImportState=ImportState.Unknown;
    try { await connection.RevealAsync(draft); throw new Exception("Unknown navigation accepted"); } catch (InvalidOperationException) { }
    Check(handler.Calls==2,"unknown import never sends navigation");
}
WorkflowJson.Save(Path.Combine(output,"validation.json"),new{passed=true,checks,workflowSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ExcerptCards).Assembly.Location))).ToLowerInvariant()});

sealed class Importer : ICardImporter,IImportedCardRevealer
{
    public ExportDestination? FixedDestination=>ExportDestination.RemNote;
    public int Imports;public List<string> Revealed=[];public bool FailNavigation,FailImport;
    public Task ImportAsync(ClipDraft draft,DraftStore store,CancellationToken ct=default)
    {
        if(FailImport)throw new InvalidOperationException("Import failed fixture");
        Imports++;
        ExportRouting.Confirm(draft,ExportRouting.BuildNote(draft,FixedDestination),FixedDestination);
        var identity=new{rootRemId="root",documentRemId="document",cards=new[]{new{key="card.0",remIds=new[]{"front"},childRemIds=Array.Empty<string>(),cardIds=Array.Empty<string>()}}};
        draft.RemNoteResult=new("root","document",["front"],[],JsonSerializer.SerializeToElement(identity));
        draft.ImportState=ImportState.Verified;store.Save(draft);return Task.CompletedTask;
    }
    public Task RevealAsync(ClipDraft draft,CancellationToken ct=default)
    {
        if(draft.ImportState!=ImportState.Verified)throw new Exception("Premature navigation");
        Revealed.Add(draft.Id);if(FailNavigation)throw new IOException("Navigation failed fixture");return Task.CompletedTask;
    }
}
sealed class NavigationHandler : HttpMessageHandler
{
    public int Calls; public string? LastPath; public bool WrongSelection;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Calls++;LastPath=request.RequestUri!.AbsolutePath;
        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        return new(HttpStatusCode.OK){Content=JsonContent.Create(new{revealed=true,sourceId=body.RootElement.GetProperty("plan").GetProperty("sourceId").GetString(),documentRemId="document",remIds=new[]{WrongSelection?"wrong":"front"}})};
    }
}

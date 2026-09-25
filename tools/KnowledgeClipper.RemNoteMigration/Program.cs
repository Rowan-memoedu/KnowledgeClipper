using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;

// An explicit, bounded migration utility. It never exports cards, edits drafts, or changes settings.
if (args.Length < 5 || args[0] is not ("--prepare" or "--apply"))
    throw new ArgumentException("Usage: --prepare|--apply <old-sdk-origin> <new-sdk-origin> <evidence-directory> <data-root> [data-root...]");
var apply = args[0] == "--apply";
Uri Origin(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback ||
        uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        throw new ArgumentException("An exact loopback HTTP origin is required.");
    return uri;
}
var oldOrigin = Origin(args[1]); var newOrigin = Origin(args[2]);
if (oldOrigin == newOrigin) throw new ArgumentException("Migration origins must differ.");
var output = Path.GetFullPath(args[3]); Directory.CreateDirectory(output);
var wire = new JsonSerializerOptions(WorkflowJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
async Task<JsonElement> Post(Uri origin, string action, object body)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "sdk/knowledge-clipper/" + action));
    request.Headers.Add("X-KnowledgeClipper-Protocol", "1");
    request.Content = JsonContent.Create(body, options: wire);
    using var response = await http.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new IOException("Migration " + action + " returned HTTP " + (int)response.StatusCode + ": " + text);
    using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone();
}
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var snapshotKeys = new[] { "remId", "parentId", "text", "backText", "children", "remType", "isDocument", "isCardItem", "isExtraCardDetail", "practiceDirection", "practiceEnabled", "cardIds", "cardTypes" };
var results = new List<object>(); var failed = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
var files = args.Skip(4).Select(Path.GetFullPath).SelectMany(root => new[] { "language-drafts", "excerpt-cards" }
    .Select(folder => Path.Combine(root, folder)).Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.json")))
    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
foreach (var path in files)
{
    var hash = Hash(path);
    var draft = WorkflowJson.Read<ClipDraft>(path);
    if (draft.ConfirmedRemNote is not { } plan || ExportRouting.For(draft) != ExportDestination.RemNote) continue;
    // Ready/failed drafts without any evidence of a write have no plugin journal to migrate.
    if (draft.RemNoteResult == null && draft.ImportState is not (ImportState.Verified or ImportState.Unknown or ImportState.Writing)) continue;
    try
    {
        if (!seen.Add(plan.SourceId)) throw new InvalidDataException("Duplicate source identity across supplied roots; inspect before migration.");
        var before = await Post(oldOrigin, "readback", plan);
        var pinned = RemNoteConnection.Validate(plan, before, draft.RemNoteResult);
        var cards = before.GetProperty("cards").EnumerateArray().ToArray();
        var expected = new { rootRemId = pinned.RootRemId, documentRemId = pinned.DocumentRemId,
            cards = cards.Select(card => new { key = card.GetProperty("key").GetString(), remIds = card.GetProperty("remIds"),
                childRemIds = card.GetProperty("childRemIds"), cardIds = card.GetProperty("cardIds") }).ToArray() };
        var verifiedNodes = cards.SelectMany(card => card.GetProperty("nodes").EnumerateArray()).Select(node =>
            snapshotKeys.Where(key => node.TryGetProperty(key, out _)).ToDictionary(key => key, key => node.GetProperty(key))).ToArray();
        var evidence = new { plan, expected, verifiedNodes };
        var itemDirectory = Path.Combine(output, draft.Id); Directory.CreateDirectory(itemDirectory);
        WorkflowJson.Save(Path.Combine(itemDirectory, "source-readback.json"), before);
        WorkflowJson.Save(Path.Combine(itemDirectory, "adoption.json"), evidence);
        if (Hash(path) != hash) throw new InvalidDataException("Draft changed during readback; nothing adopted.");
        if (apply)
        {
            var adopted = await Post(newOrigin, "admin/adopt", evidence);
            WorkflowJson.Save(Path.Combine(itemDirectory, "adoption-result.json"), adopted);
            var after = await Post(newOrigin, "readback", plan);
            RemNoteConnection.Validate(plan, after, pinned);
            WorkflowJson.Save(Path.Combine(itemDirectory, "target-readback.json"), after);
        }
        if (Hash(path) != hash) throw new InvalidDataException("Draft changed concurrently; investigate before switching.");
        results.Add(new { draftId = draft.Id, sourceId = plan.SourceId, path, draftSha256 = hash, passed = true,
            adopted = apply, remIds = pinned.RemIds, cardIds = pinned.CardIds });
        Console.WriteLine((apply ? "ADOPTED " : "PREPARED ") + draft.Id);
    }
    catch (Exception ex)
    {
        failed++;
        results.Add(new { draftId = draft.Id, sourceId = plan.SourceId, path, draftSha256 = hash, passed = false, error = ex.Message });
        Console.Error.WriteLine("BLOCKED " + draft.Id + ": " + ex.Message);
    }
}
var report = Path.Combine(output, (apply ? "apply-" : "prepare-") + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".json");
WorkflowJson.Save(report, new { schemaVersion = "knowledge-clipper.sdk-migration.v1", passed = failed == 0, applied = apply,
    oldOrigin, newOrigin, results, scope = "SDK journal adoption only; zero content creation/deletion and no draft or settings edits." });
Console.WriteLine("Migration report: " + report);
return failed == 0 ? 0 : 1;

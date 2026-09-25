using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeClipper.Workflow;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("RemNoteSpacing.Live --prepare|--run|--readback --run-root <new D:\\Caches directory> [--endpoint http://127.0.0.1:43111]");
    Console.WriteLine("--prepare only saves two new fixed fixtures locally. --run imports them through CardImportRouter. --readback cannot export.");
    return;
}

var operation = args[0];
if (operation is not ("--prepare" or "--run" or "--readback")) throw new ArgumentException("Choose --prepare, --run, or --readback.");
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 1; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || args[index] is not ("--run-root" or "--endpoint") || !options.TryAdd(args[index], args[index + 1]))
        throw new ArgumentException("Arguments require unique --run-root and optional --endpoint values.");
}
if (!options.TryGetValue("--run-root", out var requestedRoot)) throw new ArgumentException("A new isolated --run-root is required.");
var runRoot = Path.GetFullPath(requestedRoot);
if (!runRoot.StartsWith(@"D:\Caches\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Run fixtures must use a new directory below D:\\Caches\\KnowledgeClipper.");
var manifestPath = Path.Combine(runRoot, "spacing-fixture.json");
var store = new DraftStore(runRoot, "spacing-drafts");
var endpoint = options.GetValueOrDefault("--endpoint", RemNoteConnection.DefaultEndpoint).TrimEnd('/');
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || !endpointUri.IsLoopback || endpointUri.Scheme != "http" ||
    endpointUri.UserInfo.Length > 0 || endpointUri.Port == 43101 || endpointUri.Query.Length > 0 || endpointUri.Fragment.Length > 0)
    throw new ArgumentException("Use the independent native-loopback SDK host, never the retired 43101 endpoint.");

if (operation == "--prepare")
{
    if (File.Exists(manifestPath))
    {
        var existing = LoadManifest();
        foreach (var id in existing.DraftIds) _ = store.Find(id) ?? throw new InvalidDataException("A prepared draft is missing; do not regenerate its import identity.");
        Console.WriteLine("Already prepared; unchanged fixture: " + manifestPath);
        return;
    }
    if (Directory.Exists(runRoot) && Directory.EnumerateFileSystemEntries(runRoot).Any())
        throw new InvalidDataException("Preparation requires an empty run directory; existing test data is never reused or replaced.");
    var runId = Guid.NewGuid().ToString("N");
    var dateTitle = DateTime.Today.ToString("M.d", System.Globalization.CultureInfo.InvariantCulture);
    var path = new[] { "验收", "批次间隔-" + runId[..12], dateTitle };
    var settings = new LanguageSettings { RemNoteSdkEndpoint = endpoint, ModeDestinations = new() { ["excerpt-manual"] = ExportDestination.RemNote } };
    ClipDraft Draft(string front, string back)
    {
        var material = new CaptureMaterial(front, null, "unavailable", null, "Public spacing canary " + runId, 0, DateTimeOffset.UtcNow);
        var draft = new ClipDraft { Original = material, Material = material, Settings = settings, Configuration = new("", []),
            ModeId = "excerpt-manual", Route = new("excerpt-manual", "excerpt", false, false), Input = new(Intent: "excerpt"), Excerpt = new(front, back) };
        var confirmation = ExportRouting.Prepare(draft, ExportRouting.BuildNote(draft));
        Check(confirmation.RemNote!.DocumentPath.SequenceEqual(ExportRouting.DocumentPath(draft).Append(dateTitle)),
            "The production confirmation did not append the current local day; no test plan was substituted.");
        // Freeze an isolated directory before the first network call; do not modify a confirmed draft afterward.
        (confirmation with { RemNote = confirmation.RemNote! with { DocumentPath = path } }).Apply(draft);
        store.Save(draft);
        return draft;
    }
    var plain = Draft($"Spacing canary {runId}: first public paragraph.\nSecond public paragraph from the same source.", "");
    var card = Draft($"Spacing canary {runId}: a multiline question.\nThe front remains one Rem.", "First public answer line.\nSecond public answer line.");
    Check(plain.ConfirmedRemNote!.Cards.Length == 2 && plain.ConfirmedRemNote.Cards.All(c => c.Kind == "plain") &&
        card.ConfirmedRemNote!.Cards.Length == 1 && card.ConfirmedRemNote.Cards[0].Kind == "basic" && card.ConfirmedRemNote.Cards[0].Back.Length == 2,
        "Fixture conversion must yield two plain siblings followed by one multiline card.");
    WorkflowJson.Save(manifestPath, new Manifest("knowledgeclipper.spacing-canary.v2", runId, endpoint, path, [plain.Id, card.Id], DateTimeOffset.UtcNow, dateTitle));
    Console.WriteLine("PREPARED locally; no network calls: " + manifestPath);
    return;
}

var manifest = LoadManifest();
var drafts = manifest.DraftIds.Select(id => store.Find(id) ?? throw new InvalidDataException("The fixed draft is missing: " + id)).ToArray();
Check(drafts.Length == 2 && drafts.All(draft => draft.ConfirmedDestination == ExportDestination.RemNote && draft.ConfirmedRemNote != null &&
    draft.ConfirmedRemNote.DocumentPath.SequenceEqual(manifest.DocumentPath) && draft.Settings.RemNoteSdkEndpoint == manifest.Endpoint), "Frozen fixture routing changed.");
var attemptRoot = Path.Combine(runRoot, "attempts", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(attemptRoot);
using var traffic = new TrafficHandler();
using var http = new HttpClient(traffic) { Timeout = TimeSpan.FromMinutes(3) };
var observations = new List<RemNoteImportReceipt>();
var passed = false;
string? failure = null;
try
{
    var health = await new RemNoteConnection(http, manifest.Endpoint).HealthAsync();
    WorkflowJson.Save(Path.Combine(attemptRoot, "health.json"), health);
    Check(health.TryGetProperty("batchSpacingSupported", out var capability) && capability.ValueKind == JsonValueKind.True &&
        health.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True, "The active independent plugin has not loaded batch-spacing support.");
    if (manifest.DateTitle != null)
        Check(health.TryGetProperty("plainStructureSupported", out var plainSupport) && plainSupport.ValueKind == JsonValueKind.True,
            "The active plugin cannot independently verify ordinary date and separator nodes.");
    if (operation == "--run")
    {
        var router = new CardImportRouter(http);
        foreach (var draft in drafts)
        {
            await router.ImportAsync(draft, store);
            Check(draft.ImportState == ImportState.Verified && draft.RemNoteResult != null, "Import did not independently verify: " + draft.Id);
        }
        var beforeRetry = traffic.Calls.Count;
        foreach (var draft in drafts) await router.ImportAsync(draft, store);
        Check(traffic.Calls.Count == beforeRetry, "A repeated verified import sent another network request.");
    }
    for (var round = 0; round < 2; round++)
    {
        var roundReceipts = new List<RemNoteImportReceipt>();
        for (var index = 0; index < drafts.Length; index++)
        {
            var draft = drafts[index];
            using var request = new HttpRequestMessage(HttpMethod.Post, manifest.Endpoint + "/sdk/knowledge-clipper/readback");
            request.Headers.Add("X-KnowledgeClipper-Protocol", "1");
            request.Content = JsonContent.Create(draft.ConfirmedRemNote, options: new JsonSerializerOptions(WorkflowJson.Options)
                { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            await File.WriteAllTextAsync(Path.Combine(attemptRoot, $"readback-{round + 1}-{index + 1}.json"), body);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(body);
            var receipt = RemNoteConnection.Validate(draft.ConfirmedRemNote!, json.RootElement, draft.RemNoteResult);
            Check(receipt.Evidence.TryGetProperty("batchSeparator", out _), "A new batch omitted its separator evidence.");
            if (manifest.DateTitle != null) VerifyDailyLeaf(receipt, manifest);
            roundReceipts.Add(receipt);
        }
        var expectedSiblings = roundReceipts.SelectMany(receipt => receipt.RemIds.Append(SeparatorId(receipt))).ToArray();
        Check(roundReceipts.Select(receipt => receipt.DocumentRemId).Distinct().Count() == 1, "Batches did not use the same isolated leaf directory.");
        Check(roundReceipts.All(receipt => receipt.Evidence.GetProperty("documentChildren").EnumerateArray().Select(id => id.GetString()!).SequenceEqual(expectedSiblings)),
            "Actual leaf siblings differ from two complete batches with one blank after each.");
        Check(roundReceipts[0].RemIds.Length == 2 && roundReceipts[0].CardIds.Length == 0 && roundReceipts[1].RemIds.Length == 1 && roundReceipts[1].CardIds.Length == 1,
            "Separators affected content or practice-card counts.");
        var identities = roundReceipts.Select(Identity).ToArray();
        var pinnedPath = Path.Combine(runRoot, "verified-identities.json");
        if (File.Exists(pinnedPath))
            Check(JsonSerializer.Serialize(WorkflowJson.Read<StableIdentity[]>(pinnedPath), WorkflowJson.Options) == JsonSerializer.Serialize(identities, WorkflowJson.Options),
                "Original Rem, child, card, directory, or separator identity changed across readback/reopen.");
        else WorkflowJson.Save(pinnedPath, identities);
        observations.Clear(); observations.AddRange(roundReceipts);
    }
    Check(operation != "--readback" || traffic.Calls.All(call => call.Operation is "health" or "readback"), "Readback-only mode performed a mutation.");
    passed = true;
    Console.WriteLine("PASS two real batches: 3 content Rems, 2 answer children, 2 blank siblings, 1 practice card; stable IDs on repeated readback.");
}
catch (Exception error)
{
    failure = error.ToString();
    Console.Error.WriteLine("FAIL retained evidence: " + error.Message);
    Environment.ExitCode = 1;
}
finally
{
    var known = observations.Count > 0 ? observations : drafts.Select(draft => store.Find(draft.Id)?.RemNoteResult).Where(receipt => receipt != null).Cast<RemNoteImportReceipt>().ToList();
    WorkflowJson.Save(Path.Combine(attemptRoot, "recovery.json"), new { manifest.RunId, manifest.DocumentPath, manifest.DraftIds,
        scope = "Only these public canary nodes; no cleanup or mutation is performed by this receipt.",
        records = known.Select(receipt => new { receipt.RootRemId, receipt.DocumentRemId, contentRemIds = receipt.RemIds,
            separatorRemId = receipt.Evidence.TryGetProperty("batchSeparator", out var separator) ? separator.GetProperty("remId").GetString() : null,
            childRemIds = receipt.Evidence.GetProperty("cards").EnumerateArray().SelectMany(card => card.GetProperty("childRemIds").EnumerateArray().Select(id => id.GetString()!)).ToArray(),
            receipt.CardIds, evidence = receipt.Evidence }) });
    WorkflowJson.Save(Path.Combine(attemptRoot, "receipt.json"), new { passed, operation, manifestPath, runRoot, endpoint = manifest.Endpoint,
        completedAt = DateTimeOffset.UtcNow, failure, calls = traffic.Calls,
        exportRequests = traffic.Calls.Count(call => call.Operation == "export"), readbackRequests = traffic.Calls.Count(call => call.Operation == "readback") });
    Console.WriteLine("Evidence: " + Path.Combine(attemptRoot, "receipt.json"));
}

Manifest LoadManifest()
{
    if (!File.Exists(manifestPath)) throw new InvalidOperationException("First run --prepare in a new run directory; no fixed fixture exists.");
    var saved = WorkflowJson.Read<Manifest>(manifestPath);
    Check(saved.Schema is "knowledgeclipper.spacing-canary.v1" or "knowledgeclipper.spacing-canary.v2" && saved.DraftIds.Length == 2 && saved.DraftIds.Distinct().Count() == 2 &&
        saved.DraftIds.All(id => Guid.TryParseExact(id, "N", out _)) && saved.Endpoint == endpoint, "Fixture protocol, identity, or endpoint differs; do not recreate imported fixtures.");
    Check(saved.Schema != "knowledgeclipper.spacing-canary.v2" || saved.DateTitle != null && saved.DocumentPath[^1] == saved.DateTitle,
        "The frozen daily fixture has lost its original date; do not infer it from the current day.");
    return saved;
}
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static string SeparatorId(RemNoteImportReceipt receipt) => receipt.Evidence.GetProperty("batchSeparator").GetProperty("remId").GetString()!;
static StableIdentity Identity(RemNoteImportReceipt receipt) => new(receipt.RootRemId, receipt.DocumentRemId, receipt.RemIds,
    receipt.Evidence.GetProperty("cards").EnumerateArray().SelectMany(card => card.GetProperty("childRemIds").EnumerateArray().Select(id => id.GetString()!)).ToArray(), receipt.CardIds, SeparatorId(receipt));

static void VerifyDailyLeaf(RemNoteImportReceipt receipt, Manifest manifest)
{
    var directories = receipt.Evidence.GetProperty("directoryNodes").EnumerateArray().ToArray();
    Check(directories.Length == manifest.DocumentPath.Length + 1, "Actual date hierarchy depth differs from the frozen path.");
    var date = directories[^1];
    var title = string.Concat(date.GetProperty("text").EnumerateArray().Select(part => part.ValueKind == JsonValueKind.String ? part.GetString() : part.GetProperty("text").GetString()));
    Check(title == manifest.DateTitle && date.GetProperty("remId").GetString() == receipt.DocumentRemId &&
        date.GetProperty("parentId").GetString() == directories[^2].GetProperty("remId").GetString() && date.GetProperty("isDocument").GetBoolean(),
        "Daily leaf is not the frozen ordinary child document of the mode path.");
    PlainStructure(date);
    PlainStructure(receipt.Evidence.GetProperty("batchSeparator").GetProperty("node"));
    foreach (var card in receipt.Evidence.GetProperty("cards").EnumerateArray())
    {
        var front = card.GetProperty("nodes")[0];
        Check(!front.GetProperty("isCardItem").GetBoolean() && !front.GetProperty("isMultiLineCard").GetBoolean(),
            "A top-level card front incorrectly turns its daily directory into a flashcard.");
    }
    static void PlainStructure(JsonElement node)
    {
        Check(node.GetProperty("backTextState").GetString() is "absent" or "null" && node.GetProperty("rawBackText").ValueKind == JsonValueKind.Null &&
            node.GetProperty("backText").GetArrayLength() == 0 && node.GetProperty("practiceDirection").GetString() == "none" &&
            !node.GetProperty("practiceEnabled").GetBoolean() && !node.GetProperty("isCardItem").GetBoolean() &&
            !node.GetProperty("isMultiLineCard").GetBoolean() && !node.GetProperty("isExtraCardDetail").GetBoolean() &&
            node.GetProperty("cardIds").GetArrayLength() == 0 && node.GetProperty("cardTypes").GetArrayLength() == 0,
            "A date directory or blank sibling has a card back, role, or practice card.");
    }
}

sealed record Manifest(string Schema, string RunId, string Endpoint, string[] DocumentPath, string[] DraftIds, DateTimeOffset CreatedAt, string? DateTitle = null);
sealed record StableIdentity(string RootRemId, string DocumentRemId, string[] RemIds, string[] ChildRemIds, string[] CardIds, string SeparatorRemId);
sealed record RequestLog(string Operation, string Method, string Address, DateTimeOffset At);
sealed class TrafficHandler() : DelegatingHandler(new HttpClientHandler())
{
    public List<RequestLog> Calls { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add(new(request.RequestUri!.AbsolutePath.Split('/').Last(), request.Method.Method, request.RequestUri.GetLeftPart(UriPartial.Path), DateTimeOffset.UtcNow));
        return base.SendAsync(request, cancellationToken);
    }
}

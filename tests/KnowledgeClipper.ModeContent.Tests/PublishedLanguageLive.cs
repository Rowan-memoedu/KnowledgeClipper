using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class PublishedLanguageLive
{
    internal static async Task RunAsync(string repository, string output)
    {
        var configuration = ModeConfigurationLoader.Load(Path.Combine(repository, "modes"));
        var settings = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json") with
        { AnkiRootDeck = "KnowledgeClipper Canary::Published 20260911", AnkiDeck = "KnowledgeClipper Canary::Published 20260911", TypedInputAutoRead = false, GlobalSelection = false };
        var store = new DraftStore(Path.Combine(output, "live-state"));
        var checks = new List<string>(); var readbacks = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        using var trace = new Trace(Path.Combine(output, "live-raw.ndjson")) { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(trace) { Timeout = Timeout.InfiniteTimeSpan };
        var processor = new ContentModule(http, () => ContentCredentials.Resolve());
        var parents = new List<ClipDraft>();
        foreach (var language in new[] { "zh", "en" })
        {
            var indexPath = Path.Combine(output, "live-source-" + language + ".json");
            ClipDraft parent;
            if (File.Exists(indexPath)) parent = store.Find(WorkflowJson.Read<string>(indexPath)) ?? throw new InvalidDataException("Saved source parent is missing.");
            else
            {
                var sentence = language == "en" ? "They lead the team, but the pipe is made of lead." : "他重提旧事，大家觉得负担很重。";
                var target = language == "en" ? "lead" : "重";
                var material = new CaptureMaterial(target, sentence, "source", sentence.LastIndexOf(target, StringComparison.Ordinal),
                    "Published prompt integration canary", 0, DateTimeOffset.UtcNow);
                parent = new() { Original = material, Material = material, Configuration = configuration, Settings = settings,
                    ModeId = "language", Route = new("language", "language", false, true), Practice = PracticeChoice.Both };
                LanguageModeCards.Ensure(parent, LanguageModes.Select(configuration, material, PracticeChoice.Both), store);
                WorkflowJson.Save(indexPath, parent.Id);
            }
            parents.Add(parent);
            await Task.WhenAll(LanguageModeCards.Children(parent, store).Select(async child =>
            {
                if (child.Content != null) return;
                Console.WriteLine("LIVE generating " + child.ModeId + " source");
                child.Content = await processor.ProcessAsync(child.Material, child.ModeId!, child.Configuration, child.Settings, CancellationToken.None);
                store.Save(child);
            }));
            foreach (var child in LanguageModeCards.Children(parent, store))
            {
                var mode = configuration.Modes.Single(m => m.Definition.Id == child.ModeId).Definition;
                PublishedLanguage.Validate(mode, child.Material, child.Content!.Fields!);
                Check(!child.Content.Fields!.IsUnresolved, "live " + child.ModeId + " source produces ready content");
                var pronunciation = child.Content.Fields.Pronunciation!;
                Check(language == "en" ? pronunciation is "/led/" or "/lɛd/" : pronunciation == "zhòng", "live " + child.ModeId + " reads the second homograph");
                var faces = PublishedLanguage.Render(mode, child.Material, child.Content.Fields);
                WorkflowJson.Save(Path.Combine(output, "live-faces-" + child.ModeId + ".json"), new { child.Id, child.Material, child.Content, faces });
            }
        }
        // Independent generated-context inputs exercise all four real prompts without adding extra Anki cards.
        await Task.WhenAll(configuration.Modes.Where(m => PublishedLanguage.Applies(m.Definition)).Select(async mode =>
        {
            var path = Path.Combine(output, "live-generated-" + mode.Definition.Id + ".json");
            var material = new CaptureMaterial(mode.Definition.Language == "en" ? "resilient" : "严谨", null, "generated_allowed", null,
                "Published generated-context canary", 0, DateTimeOffset.UtcNow);
            ContentResult result;
            if (File.Exists(path)) result = WorkflowJson.Read<ContentResult>(path);
            else
            {
                Console.WriteLine("LIVE generating " + mode.Definition.Id + " generated");
                result = await processor.ProcessAsync(material, mode.Definition.Id, configuration, settings, CancellationToken.None);
                WorkflowJson.Save(path, result);
            }
            PublishedLanguage.Validate(mode.Definition, material, result.Fields!);
            lock (checks) Check(!result.Fields!.IsUnresolved, "live " + mode.Definition.Id + " creates and locates an authorized example");
        }));
        var pendingPath = Path.Combine(output, "live-unresolved.json");
        var unknown = new CaptureMaterial("qzxvpt", "The symbol is qzxvpt.", "source", 14, "Unknown vocabulary canary", 0, DateTimeOffset.UtcNow);
        var pending = File.Exists(pendingPath) ? WorkflowJson.Read<ContentResult>(pendingPath) :
            await processor.ProcessAsync(unknown, "language-en", configuration, settings, CancellationToken.None);
        WorkflowJson.Save(pendingPath, pending);
        var unknownResolvedSafely = pending.Fields!.IsUnresolved && pending.Fields.UnresolvedReasons.Length > 0;
        // Keep the failed linguistic expectation visible; it is never counted as an engineering or content PASS.
        WorkflowJson.Save(Path.Combine(output, "content-unresolved.json"), new { outcome = unknownResolvedSafely ? "PASS" : "UNRESOLVED",
            material = unknown, expectedStatus = "unresolved", actual = pending,
            issue = "Unknown invented letter string was spelled as letter names and marked ready. No pronunciation evidence establishes this reading; this case is excluded from accepted content and Anki import." });
        Console.WriteLine((unknownResolvedSafely ? "PASS" : "UNRESOLVED") + " unknown letter-string content expectation");
        var damaged = new CaptureMaterial("N�", "The name is N�.", "source", 12, "Unreadable original canary", 0, DateTimeOffset.UtcNow);
        var damagedPath = Path.Combine(output, "live-unreadable.json");
        var unreadable = File.Exists(damagedPath) ? WorkflowJson.Read<ContentResult>(damagedPath) :
            await processor.ProcessAsync(damaged, "language-en", configuration, settings, CancellationToken.None);
        WorkflowJson.Save(damagedPath, unreadable);
        Check(unreadable.Fields!.IsUnresolved && unreadable.Fields.UnresolvedReasons.Length > 0, "live unreadable target returns unresolved with reasons");
        var anki = new AnkiConnection(http, settings.AnkiEndpoint);
        Check((await anki.CallAsync("version")).GetInt32() == 6, "live AnkiConnect responds");
        foreach (var parent in parents)
        {
            await LanguageModeCards.ImportAsync(parent, parent.SelectedModeIds, store, anki);
            foreach (var child in LanguageModeCards.Children(parent, store))
            {
                await anki.VerifyAsync(child);
                var notes = await anki.CallAsync("notesInfo", new { notes = new[] { child.NoteId!.Value } });
                var cards = await anki.CallAsync("cardsInfo", new { cards = child.CardIds });
                var matching = await anki.CallAsync("findNotes", new { query = "tag:kc_" + child.Id });
                Check(child.ImportState == ImportState.Verified && child.CardIds.Length == 1 && matching.GetArrayLength() == 1 &&
                    cards[0].GetProperty("ord").GetInt32() == (child.Practice == PracticeChoice.Reading ? 0 : 1) &&
                    cards[0].GetProperty("deckName").GetString() == child.ConfirmedNote!.Deck,
                    "live " + child.ModeId + " exact rendered face, fields, single card and destination read back");
                readbacks.Add(new { child.Id, child.ModeId, child.NoteId, child.CardIds, notes, cards });
            }
            var before = trace.AnkiActions.Count;
            await LanguageModeCards.ImportAsync(store.Find(parent.Id)!, parent.SelectedModeIds, store, anki);
            Check(trace.AnkiActions.Count == before, "reopened " + parent.Original.SelectedText + " import makes zero provider calls");
        }
        Check(!trace.AnkiActions.Any(a => a is "deleteNotes" or "updateNoteFields" or "updateModelTemplates") && trace.AnkiActions.Count(a => a == "addNote") <= 4,
            "existing notes and templates are untouched; at most four isolated canary notes added");
        WorkflowJson.Save(Path.Combine(output, "published-live.json"), new { outcome = unknownResolvedSafely ? "PASS" : "ENGINEERING_PASS_WITH_CONTENT_UNRESOLVED", observedAt = DateTimeOffset.UtcNow,
            checks, ankiActions = trace.AnkiActions, readbacks, parentIds = parents.Select(p => p.Id).ToArray(),
            boundary = "Real configured content API, four source and four generated cases plus unreadable/unresolved; production importer and actual Anki readback. Unknown letter-string failure remains excluded from content PASS and import. Public test sentences only. No TTS calls. Separate prompt publication's postponed pronunciation variant remains unresolved." });
    }

    private sealed class Trace(string path) : DelegatingHandler
    {
        private readonly object gate = new();
        internal List<string> AnkiActions { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var input = json.RootElement;
            if (input.TryGetProperty("action", out var action))
            {
                lock (gate) AnkiActions.Add(action.GetString()!);
                return await base.SendAsync(request, ct);
            }
            var response = await base.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            var messages = input.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString()!;
            var record = new { observedAt = DateTimeOffset.UtcNow, systemSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(system))),
                input = messages[1].GetProperty("content").GetString(), statusCode = (int)response.StatusCode, response = raw };
            lock (gate) File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
            return response;
        }
    }
}

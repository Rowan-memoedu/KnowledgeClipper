using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class LiveModeImportCheck
{
    internal static async Task RunAsync(string repository, string output)
    {
        const string dataRoot = @"D:\AppData\KnowledgeClipper\canary\engineering-mode-import";
        const string rootDeck = "KnowledgeClipper Canary::Engineering 20260911";
        var store = new DraftStore(dataRoot);
        var sessions = store.RecentSessions().ToArray();
        if (sessions.Length > 1) throw new InvalidDataException("Multiple canary sessions; inspect before continuing.");
        ClipDraft parent;
        if (sessions.Length == 1) parent = sessions[0];
        else
        {
            var settings = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json") with
                { AnkiRootDeck = rootDeck, AnkiDeck = rootDeck, GlobalSelection = false, TypedInputAutoRead = false };
            var modes = ModeConfigurationLoader.LoadDefinitions(Path.Combine(repository, "modes")).Modes
                .Where(mode => mode.Language == "en" && LanguageModes.IsConcrete(mode)).ToArray();
            if (modes.Length != 2) throw new InvalidDataException("Expected two independent English modes.");
            // These declared fixture results test only the exporter and recovery interface.
            // They are neither formal prompt text nor generated learning-content evidence.
            var configuration = new ContentConfiguration("Exporter interface fixture", modes.Select(mode => new ModeSnapshot(
                mode, "Exporter interface fixture; no content request", File.ReadAllText(Path.Combine(repository, "modes", "language.schema.json")))).ToArray());
            var material = new CaptureMaterial("lead", "They lead the team.", "source", 5,
                "Independent mode exporter canary", 0, DateTimeOffset.UtcNow);
            parent = new() { Original = material, Material = material, Settings = settings, Configuration = configuration,
                ModeId = "language", Route = new("language", "language", false, true), Practice = PracticeChoice.Both };
            foreach (var child in LanguageModeCards.Ensure(parent, modes, store))
            {
                child.Content = new(child.ModeId!, new("/liːd/", child.Practice == PracticeChoice.Reading
                    ? "带领（阅读接口样本）" : "率领（书写接口样本）", null, null));
                store.Save(child);
            }
        }
        if (parent.Settings.AnkiRootDeck != rootDeck || parent.Input.ReadRequested)
            throw new InvalidDataException("Canary must retain its isolated deck and no-speech boundary.");
        var children = LanguageModeCards.Children(parent, store);
        if (children.Length != 2 || children.Select(child => child.PreviewContent?.Fields?.Meaning).Distinct().Count() != 2)
            throw new InvalidDataException("The canary must retain two independent results.");
        var checks = new List<string>();
        var readbacks = new List<object>();
        using var trace = new Trace { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(trace);
        var anki = new AnkiConnection(http, parent.Settings.AnkiEndpoint);
        var outcome = "INCOMPLETE";
        string? failure = null;
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidDataException(name);
            checks.Add(name); Console.WriteLine("PASS " + name);
        }
        try
        {
            Check((await anki.CallAsync("version")).GetInt32() == 6, "real AnkiConnect v6 responds");
            await LanguageModeCards.ImportAsync(parent, parent.SelectedModeIds, store, anki);
            children = LanguageModeCards.Children(parent, store);
            Check(children.All(child => child.ImportState == ImportState.Verified && child.CardIds.Length == 1) &&
                children.Select(child => child.NoteId).Distinct().Count() == 2,
                "two independent notes produce exactly one actual card per practice");
            foreach (var child in children)
            {
                await anki.VerifyAsync(child);
                var notes = await anki.CallAsync("notesInfo", new { notes = new[] { child.NoteId!.Value } });
                var cards = await anki.CallAsync("cardsInfo", new { cards = child.CardIds });
                var ids = await anki.CallAsync("findNotes", new { query = "tag:kc_" + child.Id });
                Check(ids.GetArrayLength() == 1 && ids[0].GetInt64() == child.NoteId,
                    "exactly one actual note for " + child.ModeId);
                Check(cards[0].GetProperty("deckName").GetString() == child.ConfirmedNote!.Deck &&
                    cards[0].GetProperty("ord").GetInt32() == (child.Practice == PracticeChoice.Reading ? 0 : 1),
                    "actual mode-specific deck and template for " + child.ModeId);
                readbacks.Add(new { child.Id, child.ModeId, child.NoteId, child.CardIds, notes, cards });
            }
            var beforeRepeat = trace.Actions.Count;
            await LanguageModeCards.ImportAsync(store.Find(parent.Id)!, parent.SelectedModeIds, store, anki);
            Check(trace.Actions.Count == beforeRepeat, "reopening and repeating import performs zero provider calls");
            Check(trace.Actions.Count(action => action == "addNote") <= 2 &&
                !trace.Actions.Any(action => action is "deleteNotes" or "updateNoteFields" or "updateModelTemplates"),
                "only the two isolated notes can be created; existing notes and templates stay intact");
            outcome = "PASS";
        }
        catch (Exception ex) { failure = ex.Message; throw; }
        finally
        {
            WorkflowJson.Save(output, new { outcome, failure, observedAt = DateTimeOffset.UtcNow, parent.Id, dataRoot,
                rootDeck, checks, actions = trace.Actions, readbacks,
                boundary = "Real production LanguageModeCards and AnkiConnection with two explicit exporter fixtures. No formal prompt, real generation, desktop capture or learning-quality claim. Reuses the saved parent on rerun; test notes remain in the isolated canary deck." });
        }
    }

    private sealed class Trace : DelegatingHandler
    {
        internal List<string> Actions { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Actions.Add(json.RootElement.GetProperty("action").GetString()!);
            return await base.SendAsync(request, ct);
        }
    }
}

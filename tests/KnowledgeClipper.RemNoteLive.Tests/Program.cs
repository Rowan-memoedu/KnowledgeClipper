using KnowledgeClipper.Workflow;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        LiveOptions options;
        try { options = Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var report = File.Exists(options.Receipt) ? WorkflowJson.Read<LiveReport>(options.Receipt) : new LiveReport { DataRoot = options.DataRoot };
        if (report.DataRoot != options.DataRoot || report.SchemaVersion != "knowledge-clipper.remnote-live.v2") { Console.Error.WriteLine("Receipt belongs to a different data root or matrix version; old evidence is not rewritten."); return 2; }
        var attempt = new LiveAttempt { Number = report.Attempts.Count + 1, Mode = options.Live ? "live" : "prepare" };
        report.Attempts.Add(attempt); report.Passed = false; report.Outcome = "RUNNING";
        var fixtures = new FixtureSet(options);
        void Save() { report.UpdatedAt = DateTimeOffset.UtcNow; WorkflowJson.Save(options.Receipt, report); }
        Save();
        async Task Step(string name, Func<StepResult, CancellationToken, Task> action)
        {
            var step = new StepResult { Name = name }; attempt.Steps.Add(step); Save();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); deadline.CancelAfter(TimeSpan.FromMinutes(5));
            try { await action(step, deadline.Token); step.Passed = true; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { step.Error = ex.GetType().Name + ": " + ex.Message; Console.Error.WriteLine("FAIL " + name + ": " + step.Error); }
            finally { step.CompletedAt = DateTimeOffset.UtcNow; Save(); }
        }
        void Check(StepResult step, bool value, string message)
        { if (!value) throw new InvalidDataException(message); step.Checks.Add(message); Console.WriteLine("  PASS " + message); }
        await Step("prepare-stable-public-fixtures", async (step, _) => {
            await fixtures.PrepareAsync();
            foreach (var item in fixtures.AllCards())
            {
                Check(step, item.Draft.ConfirmedNote != null, item.Key + ": frozen plan exists without an external call");
                Check(step, ExportRouting.For(item.Draft) == (item.Key == "anki-language" ? ExportDestination.Anki : ExportDestination.RemNote), item.Key + ": expected configured destination");
                FixtureMatrix.CheckPlan(item.Key, item.Draft, fixtures, (value, text) => Check(step, value, text));
            }
            var raw = fixtures.ExcerptCard("raw").ConfirmedRemNote!;
            Check(step, raw.Cards.Length == 2 && raw.Cards.All(card => card.Kind == "plain" && card.Back.Length == 0), "raw: two ordinary sibling plans, no review card");
            var basic = fixtures.ExcerptCard("manual-basic").ConfirmedRemNote!.Cards.Single();
            Check(step, basic.Kind == "basic" && basic.Front.Any(part => part.Text?.Contains('\n') == true) && basic.Back.Length == 2, "basic: one front with LF and two answer child plans");
            foreach (var key in new[] { "single-cloze", "merged-cloze" })
            {
                var plan = fixtures.ExcerptCard(key).ConfirmedRemNote!.Cards.Single();
                var groups = plan.Front.Where(part => part.ClozeId != null).ToArray();
                Check(step, plan.Kind == "cloze" && plan.Back.Length == 0 && groups.Length == (key == "single-cloze" ? 1 : 2) && groups.All(part => part.ClozeId == 1), key + ": c1 group preserved with blank extra back");
            }
            var image = fixtures.ExcerptCard("images").ConfirmedRemNote!.Cards.Single();
            Check(step, image.Front.Where(part => part.Kind == "image").Select(part => part.Sha256).SequenceEqual(fixtures.Manifest.Images.Take(2).Select(asset => asset.Sha256)) &&
                image.Back.SelectMany(row => row).Where(part => part.Kind == "image").Select(part => part.Sha256).SequenceEqual(fixtures.Manifest.Images.Skip(2).Select(asset => asset.Sha256)), "image plans preserve both ordered groups and hashes");
            WorkflowJson.Save(Path.Combine(options.DataRoot, "expected-matrix.json"), new { scope = report.Scope, totals = FixtureMatrix.Summary(), fixtures = FixtureMatrix.All });
        });
        if (!attempt.Steps.Single().Passed) { report.Outcome = "PREPARATION_FAILED"; attempt.CompletedAt = DateTimeOffset.UtcNow; Save(); return 1; }
        if (!options.Live)
        {
            attempt.CompletedAt = DateTimeOffset.UtcNow; report.Outcome = "PREPARED_NO_EXTERNAL_CALLS"; Save();
            Console.WriteLine("Prepared stable fixtures. No live result is claimed. " + options.Receipt); return 0;
        }
        using var trace = new RequestTrace(); using var http = new HttpClient(trace) { Timeout = TimeSpan.FromSeconds(120) };
        attempt.Trace = trace.Entries;
        var router = new CardImportRouter(http);
        var evidenceRoot = Path.Combine(options.DataRoot, "readbacks", "attempt-" + attempt.Number.ToString("D3"));
        async Task Verify(StepResult step, string key, ClipDraft draft, DraftStore store, string suffix, CancellationToken ct)
        {
            await ReadbackChecks.VerifyAsync(key, draft, store, fixtures, http, Path.Combine(evidenceRoot, key + suffix + ".json"), (value, text) => Check(step, value, text), ct);
            step.Cards.Add(CardObservation.From(key, draft));
        }
        foreach (var key in FixtureSet.ExcerptKeys.Concat(new[] { "ocr-text" }))
        {
            if (cancellation.IsCancellationRequested) break;
            await Step("remnote-" + key, async (step, ct) => {
                var session = key == "ocr-text" ? fixtures.OcrExcerpt : fixtures.Excerpt;
                await ExcerptCards.ImportAsync(session, fixtures.Excerpts, router, [fixtures.Manifest.Ids[key]], ct);
                if (key == "images-fresh")
                {
                    var sourceId = fixtures.ExcerptCard(key).ConfirmedRemNote!.SourceId;
                    var operations = report.Attempts.SelectMany(previous => previous.Trace).Where(entry => entry.SourceId == sourceId).ToArray();
                    Check(step, operations.Count(entry => entry.Operation == "export") == 1 &&
                        operations.Any(entry => entry.Operation == "export" && entry.Status is >= 200 and < 300) &&
                        operations.All(entry => entry.Operation != "finalize-media"),
                        "retained fresh image evidence proves one ordinary export and no media recovery across every attempt");
                }
                await Verify(step, key, fixtures.ExcerptCard(key), fixtures.Excerpts.Cards, "-after-import", ct);
            });
        }
        foreach (var key in FixtureSet.DirectKeys)
        {
            if (cancellation.IsCancellationRequested) break;
            await Step("remnote-" + key, async (step, ct) => {
                var draft = fixtures.Draft(key); await router.ImportAsync(draft, fixtures.Drafts, ct);
                await Verify(step, key, fixtures.Draft(key), fixtures.Drafts, "-after-import", ct);
            });
        }
        if (!cancellation.IsCancellationRequested) await Step("remnote-two-question-rounds", async (step, ct) => {
            var parent = fixtures.Draft("questions"); await QuestionCards.ImportAsync(parent, [0, 1], fixtures.Drafts, router, () => { }, ct);
            parent = fixtures.Draft("questions");
            Check(step, parent.FollowUpCards.Count == 2 && QuestionCards.Preview(parent, fixtures.Drafts).All(card => card.State == ImportState.Verified), "both question rounds verified through the production batch path");
            foreach (var pair in parent.FollowUpCards.OrderBy(pair => pair.Key)) await Verify(step, "question-" + pair.Key, fixtures.Drafts.Find(pair.Value)!, fixtures.Drafts, "-after-import", ct);
            Check(step, HistoryRecords.ActionText(new(Draft: parent), fixtures.Drafts, fixtures.Excerpts) == "查看", "fully imported question history is labelled view");
        });
        foreach (var key in FixtureSet.LanguageParents)
        {
            if (cancellation.IsCancellationRequested) break;
            await Step(key, async (step, ct) => {
                var parent = fixtures.Draft(key); var children = fixtures.LanguageCards(key).ToArray();
                var modeIds = children.Select(child => child.Draft.ModeId!).ToArray();
                if (children.Length > 1)
                {
                    var transitionPath = Path.Combine(options.DataRoot, key + "-batch-transition.json");
                    var transition = File.Exists(transitionPath) ? WorkflowJson.Read<LanguageBatchTransition>(transitionPath) : new LanguageBatchTransition();
                    if (transition.Before == null)
                    {
                        transition.Before = HistoryRecords.ActionText(new(Draft: parent), fixtures.Drafts, fixtures.Excerpts);
                        Check(step, transition.Before == "继续制卡", key + ": both unimported modes initially show continue"); WorkflowJson.Save(transitionPath, transition);
                    }
                    else Check(step, transition.Before == "继续制卡", key + ": retained initial history evidence");
                    await LanguageModeCards.ImportAsync(parent, [modeIds[0]], fixtures.Drafts, router, ct: ct);
                    var first = fixtures.Drafts.Find(children[0].Draft.Id)!;
                    await Verify(step, children[0].Key, first, fixtures.Drafts, "-after-single", ct);
                    if (transition.AfterSingle == null)
                    {
                        transition.AfterSingle = HistoryRecords.ActionText(new(Draft: fixtures.Draft(key)), fixtures.Drafts, fixtures.Excerpts);
                        Check(step, transition.AfterSingle == "继续制卡", key + ": importing one mode leaves the other available"); WorkflowJson.Save(transitionPath, transition);
                    }
                    else Check(step, transition.AfterSingle == "继续制卡", key + ": retained single-card history evidence");
                    var firstSource = first.ConfirmedRemNote!.SourceId;
                    var firstWrites = trace.Entries.Count(entry => entry.SourceId == firstSource && entry.Operation is "export" or "finalize-media");
                    await LanguageModeCards.ImportAsync(parent, modeIds, fixtures.Drafts, router, ct: ct);
                    Check(step, trace.Entries.Count(entry => entry.SourceId == firstSource && entry.Operation is "export" or "finalize-media") == firstWrites,
                        key + ": bulk import skips the already imported mode in the same session");
                    transition.AfterBatch = HistoryRecords.ActionText(new(Draft: fixtures.Draft(key)), fixtures.Drafts, fixtures.Excerpts);
                    Check(step, transition.AfterBatch == "查看", key + ": all modes imported changes history to view"); WorkflowJson.Save(transitionPath, transition);
                }
                else
                await LanguageModeCards.ImportAsync(parent, modeIds, fixtures.Drafts, router, ct: ct);
                foreach (var child in fixtures.LanguageCards(key)) await Verify(step, child.Key, child.Draft, fixtures.Drafts, "-after-import", ct);
                Check(step, HistoryRecords.ActionText(new(Draft: fixtures.Draft(key)), fixtures.Drafts, fixtures.Excerpts) == "查看", key + ": fully imported language history is labelled view");
            });
        }
        if (!cancellation.IsCancellationRequested) await Step("excerpt-history-state-transition", async (step, ct) => {
            var transitionPath = Path.Combine(options.DataRoot, "history-transition.json");
            var transition = File.Exists(transitionPath) ? WorkflowJson.Read<HistoryTransition>(transitionPath) : new HistoryTransition();
            var session = fixtures.Excerpt;
            if (transition.Before == null)
            {
                transition.Before = HistoryRecords.ActionText(new(Excerpt: session), fixtures.Drafts, fixtures.Excerpts);
                Check(step, transition.Before == "查看", "all initial excerpts imported changes history to view");
                WorkflowJson.Save(transitionPath, transition);
            }
            else Check(step, transition.Before == "查看", "retained first-run evidence records view before adding content");
            var link = session.Cards.SingleOrDefault(card => card.Label == "history-added");
            var added = link == null ? ExcerptCards.Add(session, fixtures.Excerpts, new("Practice makes a skill easier to recall.", ""), "history-added") : ExcerptCards.Get(session, fixtures.Excerpts, link.Id);
            fixtures.Manifest.Ids["history-added"] = added.Id; fixtures.SaveManifest(); transition.CardId = added.Id;
            if (transition.AfterAddition == null)
            {
                transition.AfterAddition = HistoryRecords.ActionText(new(Excerpt: session), fixtures.Drafts, fixtures.Excerpts);
                Check(step, transition.AfterAddition == "继续制卡", "adding a new unimported item returns history to continue card-making");
                WorkflowJson.Save(transitionPath, transition);
            }
            else Check(step, transition.AfterAddition == "继续制卡", "retained first-run evidence records the new-item transition");
            await ExcerptCards.ImportAsync(session, fixtures.Excerpts, router, [added.Id], ct);
            await Verify(step, "history-added", fixtures.Excerpts.Cards.Find(added.Id)!, fixtures.Excerpts.Cards, "-after-import", ct);
            transition.Final = HistoryRecords.ActionText(new(Excerpt: fixtures.Excerpt), fixtures.Drafts, fixtures.Excerpts);
            Check(step, transition.Final == "查看", "importing the new ordinary item restores view"); WorkflowJson.Save(transitionPath, transition);
        });
        if (!cancellation.IsCancellationRequested) await Step("reopen-batches-and-force-readback", async (step, ct) => {
            var before = fixtures.AllCards().Select(item => CardObservation.From(item.Key, item.Draft)).ToArray();
            Check(step, fixtures.AllCards().All(item => item.Draft.ImportState == ImportState.Verified), "every initial item is verified before duplicate-import checks");
            var writes = trace.WriteCount;
            var session = fixtures.Excerpt;
            await ExcerptCards.ImportAsync(session, fixtures.Excerpts, router, session.Cards.Where(card => card.SupersededBy == null).Select(card => card.Id), ct);
            await ExcerptCards.ImportAsync(fixtures.OcrExcerpt, fixtures.Excerpts, router, [fixtures.Manifest.Ids["ocr-text"]], ct);
            foreach (var key in FixtureSet.DirectKeys) await router.ImportAsync(fixtures.Draft(key), fixtures.Drafts, ct);
            await QuestionCards.ImportAsync(fixtures.Draft("questions"), [0, 1], fixtures.Drafts, router, () => { }, ct);
            foreach (var key in FixtureSet.LanguageParents)
            {
                var parent = fixtures.Draft(key);
                await LanguageModeCards.ImportAsync(parent, LanguageModeCards.Children(parent, fixtures.Drafts).Select(child => child.ModeId!).ToArray(), fixtures.Drafts, router, ct: ct);
            }
            Check(step, trace.WriteCount == writes, "reopened verified batches issue no SDK export, media finalization, or Anki addNote request");
            foreach (var item in fixtures.AllCards())
            {
                var old = before.Single(value => value.Key == item.Key);
                var now = CardObservation.From(item.Key, item.Draft);
                Check(step, old.Identity == now.Identity, item.Key + ": reopened batch keeps the same imported identities");
                await Verify(step, item.Key, item.Draft, item.Store, "-after-repeat", ct);
            }
        });
        if (!cancellation.IsCancellationRequested) await Step("complete-matrix-counts-and-independent-transport", (step, _) => {
            var all = fixtures.AllCards().ToArray();
            Check(step, all.Select(item => item.Key).Order().SequenceEqual(FixtureMatrix.All.Select(item => item.Key).Order()), "every declared matrix item is present, including history-added");
            Check(step, all.All(item => item.Draft.ImportState == ImportState.Verified), "every matrix item is verified");
            var remnote = all.Where(item => ExportRouting.For(item.Draft) == ExportDestination.RemNote).Select(item => item.Draft.RemNoteResult!).ToArray();
            var expected = FixtureMatrix.All.Where(item => item.Destination == ExportDestination.RemNote).ToArray();
            var childIds = remnote.SelectMany(receipt => receipt.Evidence.GetProperty("cards").EnumerateArray().SelectMany(card => card.GetProperty("childRemIds").EnumerateArray().Select(id => id.GetString()!))).ToArray();
            Check(step, remnote.Length == expected.Length && remnote.SelectMany(receipt => receipt.RemIds).Distinct().Count() == expected.Sum(item => item.Cards.Length) &&
                childIds.Distinct().Count() == expected.Sum(item => item.Cards.Sum(card => card.Back.Length)) &&
                remnote.SelectMany(receipt => receipt.CardIds).Distinct().Count() == expected.Sum(item => item.Cards.Sum(card => card.ReviewCards)),
                "distinct source, content, answer-child and review-card identities match the independent complete matrix");
            var sdkRequests = trace.Entries.Where(entry => entry.Address?.Contains("/sdk/knowledge-clipper/", StringComparison.Ordinal) == true).ToArray();
            Check(step, sdkRequests.Length > 0 && sdkRequests.All(entry => entry.Port == 43111) && trace.Entries.All(entry => entry.Port is not (43101 or 43102)),
                "all SDK calls use independent port 43111; no legacy MCP-associated port is contacted");
            report.ActualCounts = new { remNoteSources = remnote.Length, topLevelContentRems = remnote.Sum(receipt => receipt.RemIds.Length), answerChildRems = childIds.Length,
                remNoteReviewCards = remnote.Sum(receipt => receipt.CardIds.Length), ankiNotes = all.Count(item => item.Draft.NoteId != null), ankiReviewCards = all.Sum(item => item.Draft.CardIds.Length) };
            return Task.CompletedTask;
        });
        report.Passed = !cancellation.IsCancellationRequested && attempt.Steps.All(step => step.Passed);
        report.Outcome = report.Passed ? "PASS" : cancellation.IsCancellationRequested ? "CANCELLED_WITH_SAVED_DRAFTS" : "FAILED_WITH_SAVED_DRAFTS";
        attempt.Trace = trace.Entries; attempt.CompletedAt = DateTimeOffset.UtcNow; Save();
        Console.WriteLine(report.Outcome + " " + options.Receipt); return report.Passed ? 0 : 1;
    }

    private static LiveOptions Parse(string[] args)
    {
        if (args.Length < 5 || args[4] is not ("--prepare" or "--live") || (args.Length - 5) % 2 != 0)
            throw new ArgumentException("Usage: RemNoteLive.Tests <project-root> <fixed-data-root> <receipt.json> <image-fixture-directory> --prepare|--live [--sdk-endpoint URL] [--anki-endpoint URL]");
        var sdk = RemNoteConnection.DefaultEndpoint; var anki = "http://127.0.0.1:8765";
        for (var i = 5; i < args.Length; i += 2)
            if (args[i] == "--sdk-endpoint") sdk = args[i + 1]; else if (args[i] == "--anki-endpoint") anki = args[i + 1]; else throw new ArgumentException("Unknown option: " + args[i]);
        var root = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
        var receipt = Path.GetFullPath(args[2]);
        if (!root.StartsWith(@"D:\Caches\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase) ||
            !receipt.StartsWith(@"D:\Artifacts\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Keep independent test data under D:\\Caches\\KnowledgeClipper and receipts under D:\\Artifacts\\KnowledgeClipper.");
        if (!root.Equals(FixtureMatrix.FixedDataRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This v2 matrix uses only " + FixtureMatrix.FixedDataRoot + "; earlier live fixtures must not be rewritten.");
        if (RemNoteConnection.ResolveEndpoint(sdk) != RemNoteConnection.DefaultEndpoint)
            throw new ArgumentException("The independent live matrix requires the standalone SDK host on port 43111.");
        new LanguageSettings { RemNoteSdkEndpoint = sdk, AnkiEndpoint = anki }.Validate();
        return new(Path.GetFullPath(args[0]), root, receipt, Path.GetFullPath(args[3]), args[4] == "--live", sdk, anki);
    }
}

internal sealed class LiveReport
{
    public string SchemaVersion { get; set; } = "knowledge-clipper.remnote-live.v2";
    public required string DataRoot { get; init; }
    public string Scope { get; set; } = "Handwritten public exporter fixtures through production routing; no LLM, prompt validation, private input, or audio synthesis. Prepare never contacts a service. Live imports are read back independently.";
    public object ExpectedCounts { get; set; } = FixtureMatrix.Summary();
    public object? ActualCounts { get; set; }
    public bool Passed { get; set; }
    public string Outcome { get; set; } = "NOT_RUN";
    public DateTimeOffset UpdatedAt { get; set; }
    public List<LiveAttempt> Attempts { get; set; } = [];
}
internal sealed class LiveAttempt
{
    public int Number { get; set; }
    public string Mode { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<StepResult> Steps { get; set; } = [];
    public List<TraceEntry> Trace { get; set; } = [];
}
internal sealed class StepResult
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<string> Checks { get; set; } = [];
    public List<CardObservation> Cards { get; set; } = [];
}
internal sealed record CardObservation(string Key, string DraftId, ExportDestination Destination, ImportState State, string[] RemIds, string[] RemNoteCardIds, long? AnkiNoteId, long[] AnkiCardIds)
{
    public string Identity => DraftId + ":" + Destination + ":" + string.Join(",", RemIds) + ":" + string.Join(",", RemNoteCardIds) + ":" + AnkiNoteId + ":" + string.Join(",", AnkiCardIds);
    internal static CardObservation From(string key, ClipDraft draft) => new(key, draft.Id, ExportRouting.For(draft), draft.ImportState,
        draft.RemNoteResult?.RemIds ?? [], draft.RemNoteResult?.CardIds ?? [], draft.NoteId, draft.CardIds);
}
internal sealed class HistoryTransition
{
    public string? Before { get; set; }
    public string? AfterAddition { get; set; }
    public string? Final { get; set; }
    public string? CardId { get; set; }
}
internal sealed class LanguageBatchTransition
{
    public string? Before { get; set; }
    public string? AfterSingle { get; set; }
    public string? AfterBatch { get; set; }
}

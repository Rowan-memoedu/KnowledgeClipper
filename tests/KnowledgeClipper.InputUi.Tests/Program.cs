using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string DataRoot = @"D:\AppData\KnowledgeClipper\canary\question-batch";
    private const string Deck = "KnowledgeClipper Canary::Architecture 20260910";
    private const string Question = "为什么正方形的对角线长度相等？";
    private const string FollowUp = "用更简单的话解释";
    private const string ThirdTurn = "换一个角度说明这个结论";
    private const string WithoutContextQuestion = "三角形内角和为什么是180度？";
    private static readonly DraftStore Store = new(DataRoot);
    private static readonly List<string> Checks = [];
    private static readonly Dictionary<string, object> Readbacks = [];
    private static readonly List<Form> Owned = [];
    private static string stage = "initialization", outcome = "INCOMPLETE", error = "";
    private static string mainBefore = "", mainAfter = "", desktopSha256 = "";
    private static int dpi;
    private static DateTimeOffset started;
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        started = DateTimeOffset.UtcNow;
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        var exit = 0;
        timer.Tick += async (_, _) => {
            timer.Stop();
            try { await RunAsync(args); outcome = "PASS"; }
            catch (Exception ex) {
                error = (ex is TargetInvocationException ? ex.InnerException : ex)?.Message ?? "Unknown failure";
                Console.Error.WriteLine("INCOMPLETE " + stage + ": " + error); exit = 1;
            }
            finally {
                foreach (var form in Owned.AsEnumerable().Reverse().Where(f => !f.IsDisposed).ToArray()) { form.Close(); form.Dispose(); }
                foreach (var form in Application.OpenForms.Cast<Form>().ToArray()) { form.Close(); form.Dispose(); }
                SaveEvidence(); context.ExitThread();
            }
        };
        timer.Start(); Application.Run(context); return exit;
    }

    private static object? Get(object target, string field) => target.GetType().GetField(field, Instance)!.GetValue(target);
    private static object? Property(object target, string property) => target.GetType().GetProperty(property, Instance)!.GetValue(target);
    private static object? Call(object target, string method, params object?[] values) => target.GetType().GetMethod(method, Instance)!.Invoke(target, values);
    private static T Control<T>(Form form, string suffix) where T : Control => (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();
    private static ClipDraft Draft(Form form) => (ClipDraft)Property(form, "Draft")!;
    private static void Check(bool condition, string name) {
        if (!condition) throw new InvalidOperationException(name);
        Checks.Add(name); Console.WriteLine("PASS " + name); SaveEvidence();
    }
    private static void Stage(string value) { stage = value; Console.WriteLine("STAGE " + value); SaveEvidence(); }
    private static async Task UntilAsync(Func<bool> done, string description, int seconds = 150)
    {
        var watch = Stopwatch.StartNew(); var lastLog = TimeSpan.Zero;
        while (!done()) {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(description);
            if (watch.Elapsed - lastLog >= TimeSpan.FromSeconds(15)) { Console.WriteLine("WAIT " + description); lastLog = watch.Elapsed; }
            await Task.Delay(50);
        }
    }
    private static void SaveEvidence()
    {
        var drafts = Store.Recent().Select(d => new {
            d.Id, d.ParentDraftId, d.Input, d.Route, d.ImportState, d.NoteId, d.CardIds,
            d.ContentError, d.ConversationError, conversationTurns = d.Conversation.Count,
            noSourceCapture = d.Original.SourceWindow == 0 && !d.Times.ContainsKey("captureStarted"),
            noSpeech = !d.Times.ContainsKey("speechRequested"), draftPath = Path.Combine(DataRoot, "language-drafts", d.Id + ".json")
        }).ToArray();
        WorkflowJson.Save(Path.Combine(DataRoot, "input-ui-canary.json"), new {
            outcome, stage, error, started, updated = DateTimeOffset.UtcNow, desktopSha256, dpi,
            mainBefore, mainAfter, checks = Checks, drafts, readbacks = Readbacks,
            openTestForms = Application.OpenForms.Count,
            scope = "Actual Desktop semantic controls and production conversation batching/Anki. Three ordered cards with context and one selected card without context; no simulated input, TTS or global selection."
        });
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--resume")
            throw new ArgumentException("InputUi.Tests <project-root> [--resume]");
        var resume = args.Length == 2;
        if (Store.Recent().Any() && !resume) throw new InvalidOperationException("This canary already has actual drafts. Inspect them; use --resume to reuse those identities without making duplicate cards.");
        Directory.CreateDirectory(DataRoot);
        var settingsPath = Path.Combine(DataRoot, "language-settings.json");
        if (!File.Exists(settingsPath)) WorkflowJson.Save(settingsPath,
            WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json") with { GlobalSelection = false, TypedInputAutoRead = false, AnkiDeck = Deck });
        var settings = WorkflowJson.Read<LanguageSettings>(settingsPath);
        Check(!settings.GlobalSelection && !settings.TypedInputAutoRead && settings.AnkiDeck == Deck, "isolated settings disable selection and speech and fix the Canary deck");
        _ = ContentCredentials.Resolve();
        using var http = new HttpClient(); var anki = new AnkiConnection(http, settings.AnkiEndpoint);
        await anki.CallAsync("version");
        var assembly = Assembly.Load("KnowledgeClipper");
        desktopSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(args[0], "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, DataRoot);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Instance).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        var mainType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        var main = (Form)mainType.GetConstructor(Instance, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        Owned.Add(main); Call(main, "EnableLanguageWorkflow"); main.Show(); await Task.Delay(100);
        if (!IsWindowVisible(main.Handle)) ShowWindow(main.Handle, 8);
        Check(IsWindowVisible(main.Handle), "test-owned production floating window is natively visible");
        var size = main.Size; mainBefore = size.ToString(); dpi = main.DeviceDpi;
        var flow = Get(main, "languageFlow")!; var monitor = Get(main, "selectionMonitor")!;
        void Unchanged(string when) {
            mainAfter = main.Size.ToString();
            Check(main.Size == size, "floating size unchanged " + when);
            Check(!(bool)Property(monitor, "IsRunning")! && Get(monitor, "input") == null, "global input stays uninstalled " + when);
        }

        Stage("question context setting");
        async Task SetContext(bool enabled) {
            var dialog = (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Instance).Single().Invoke([flow, DataRoot]);
            Owned.Add(dialog); dialog.Show(main);
            Control<CheckBox>(dialog, "question-card-context").Checked = enabled;
            Control<Button>(dialog, "save-settings").PerformClick(); await Task.Delay(50);
            Check(dialog.IsDisposed && WorkflowJson.Read<LanguageSettings>(settingsPath).QuestionCardIncludeContext == enabled &&
                ((LanguageSettings)Property(flow, "Settings")!).QuestionCardIncludeContext == enabled, "question context setting saves and applies " + enabled);
        }
        await SetContext(true);
        Unchanged("after settings");

        async Task<Form> Submit(string text)
        {
            var matches = Store.Recent().Where(d => d.ParentDraftId == null && d.Original.SelectedText == text).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && !resume) throw new InvalidOperationException("Unexpected existing root draft; refusing a new input.");
            Form preview;
            if (matches.Length == 1) {
                preview = (Form)Call(flow, "OpenDraft", matches[0])!;
                Console.WriteLine("REUSE actual draft " + matches[0].Id);
                if (matches[0].Content == null) Control<Button>(preview, "retry-content").PerformClick();
            }
            else {
                var input = (TextBox)Get(main, "inlineQuestionInput")!; input.Text = text;
                await (Task)Call(main, "SubmitInlineQuestionAsync")!;
                preview = ((System.Collections.IEnumerable)Get(flow, "forms")!).Cast<Form>().Single(f => Draft(f).Original.SelectedText == text && Draft(f).ParentDraftId == null);
                Check(input.Text.Length == 0 && input.Enabled, "inline submission accepts and clears " + text);
            }
            Owned.Add(preview);
            await UntilAsync(() => !(bool)Property(preview, "ContentRunning")!, "content result for " + text);
            var draft = Draft(preview);
            if (draft.Content == null) throw new InvalidOperationException(draft.ContentError ?? "Content did not complete.");
            Check(draft.Input is { Source: "typed", Intent: "lookup", QuestionMarkRouting: true, ReadRequested: false }, "typed lookup metadata retained for " + text);
            Check(draft.Original.SelectedText == text && draft.Material == draft.Original && draft.Original.SourceWindow == 0 &&
                draft.Original.ContextText == null && draft.Original.TargetStart == null && !draft.Times.ContainsKey("captureStarted"), "typed source has no selection/context capture for " + text);
            Check(!draft.Times.ContainsKey("speechRequested"), "typed input does not request speech for " + text);
            Unchanged("after input " + text); return preview;
        }

        async Task Follow(Form preview, string text)
        {
            var draft = Draft(preview);
            if (draft.Conversation.Any(t => t.Question == text)) return;
            var previous = draft.Conversation.ToArray();
            var original = draft.Original; var captured = draft.Times["captured"];
            var input = Control<TextBox>(preview, "follow-up-input");
            var send = Control<Button>(preview, "follow-up-send");
            input.Text = text; Check(send.Enabled, "unpunctuated follow-up is available");
            send.PerformClick();
            Check(!send.Enabled && !Control<Button>(preview, "confirm").Enabled, "pending follow-up blocks repeat submission and import");
            await UntilAsync(() => !(bool)Property(Control<Control>(preview, "follow-up"), "Sending")!, "follow-up reply");
            if (draft.ConversationError != null) throw new InvalidOperationException(draft.ConversationError);
            Check(draft.Conversation.Count == previous.Length + 1 && draft.Conversation.Take(previous.Length).SequenceEqual(previous) &&
                draft.Conversation.Last().Answer.Answered && input.Text.Length == 0, "new answer appends once and preserves preceding turns");
            Check(draft.Original == original && draft.Times["captured"] == captured && !draft.Times.ContainsKey("captureStarted"), "follow-up does not recapture");
        }
        async Task Import(Form preview, int? only = null)
        {
            var draft = Draft(preview);
            var indexes = only is int index ? new[] { index } : QuestionCards.Preview(draft, Store)
                .Where(c => c.State != ImportState.Verified && draft.Conversation[c.Index].Answer.Answered).Select(c => c.Index).ToArray();
            if (indexes.All(i => QuestionCards.Preview(draft, Store)[i].State == ImportState.Verified)) return;
            var button = Control<Button>(preview, only is int selected ? "question-card-" + selected + ".import" : "confirm");
            Check(button.Visible && button.Enabled, only == null ? "footer offers the selected batch" : "individual round has an import action");
            button.PerformClick();
            await UntilAsync(() => !(bool)Property(preview, "QuestionImportRunning")!, "question card import", 120);
            if (draft.ConversationImportError != null) throw new InvalidOperationException(draft.ConversationImportError);
            foreach (var i in indexes) await ReadQuestionAsync(draft, i, anki);
        }

        Stage("three-turn question session");
        var question = await Submit(Question); var questionDraft = Draft(question);
        Check(questionDraft.Route?.OutputKind == "question" && !Control<Control>(question, "basic-fields").Visible,
            "question uses the conversation list without a duplicate Basic preview");
        await Follow(question, FollowUp); await Follow(question, ThirdTurn);
        Check(QuestionCards.Preview(questionDraft, Store).Count == 3, "all three ordered questions have one preview row each");
        var secondBefore = QuestionCards.Preview(questionDraft, Store)[1];
        Check(secondBefore.Context.Contains(Question) && secondBefore.Context.Contains(questionDraft.Conversation[0].Answer.AnswerText!),
            "second card previews the preceding question and answer as separate context");
        await Import(question, 1);
        if (!resume) Check(QuestionCards.Preview(questionDraft, Store).Count(p => p.State == ImportState.Verified) == 1,
            "individual import writes only the chosen second round");
        await Import(question);
        var firstCards = questionDraft.FollowUpCards.Values.Distinct().Select(id => Store.Find(id)!).ToArray();
        Check(firstCards.Length == 3 && firstCards.All(d => d.ImportState == ImportState.Verified && d.CardIds.Length == 1),
            "single then batch import produces exactly three actual cards");
        foreach (var i in Enumerable.Range(0, 3)) await ReadQuestionAsync(questionDraft, i, anki);
        Check(!Control<Button>(question, "confirm").Enabled, "completed batch disables repeat confirmation");
        var beforeIds = firstCards.Select(d => d.NoteId).ToArray();
        Control<Button>(question, "confirm").PerformClick();
        Check(questionDraft.FollowUpCards.Values.Distinct().Select(id => Store.Find(id)!.NoteId).SequenceEqual(beforeIds),
            "repeat confirmation keeps the same note identities");
        Unchanged("after batch import");

        Stage("context disabled for next session");
        await SetContext(false);
        var without = await Submit(WithoutContextQuestion); var withoutDraft = Draft(without);
        await Follow(without, FollowUp);
        Check(!withoutDraft.Settings.QuestionCardIncludeContext && QuestionCards.Preview(withoutDraft, Store)[1].Context == "",
            "new session obeys disabled preceding context setting");
        await Import(without, 1);
        var noContextCard = Store.Find(withoutDraft.FollowUpCards[1])!;
        Check(noContextCard.ConfirmedNote!.Fields["Context"] == "" && !withoutDraft.FollowUpCards.ContainsKey(0),
            "only selected second card is imported with an empty auxiliary context field");
        await ReadQuestionAsync(withoutDraft, 1, anki);
        Check(questionDraft.Settings.QuestionCardIncludeContext && QuestionCards.Preview(questionDraft, Store)[1].Context.Length > 0,
            "changing settings does not mutate already confirmed earlier card context");

        Stage("history and persistence");
        Call(flow, "ShowRecent", false);
        var history = (Form)Get(flow, "history")!; Owned.Add(history);
        Check(!history.Controls.Find("knowledge-clipper.language.history.play." + questionDraft.Id, true).Single().Visible &&
            !history.Controls.Find("knowledge-clipper.language.history.play." + withoutDraft.Id, true).Single().Visible,
            "non-language sessions have no visible playback buttons");
        var rowPanel = (FlowLayoutPanel)Get(history, "rows")!;
        Check(rowPanel.Controls.Count == 2 && Store.RecentSessions().Count() == 2, "history contains one entry per session and no internal card duplicates");
        Check(Store.RecentSpeech().Count() == 0 && Store.Recent().All(d => !d.Times.ContainsKey("speechRequested")),
            "question sessions cannot enter reading history or request playback");
        foreach (var form in new[] { history, question, without }) form.Close();
        foreach (var draft in new[] { questionDraft, withoutDraft }) {
            var loaded = Store.Find(draft.Id)!;
            Check(JsonSerializer.Serialize(loaded.Conversation, WorkflowJson.Options) == JsonSerializer.Serialize(draft.Conversation, WorkflowJson.Options) &&
                loaded.FollowUpCards.Count == draft.FollowUpCards.Count, "closed session preserves conversation and per-round import identities");
        }
        Check(Store.Recent().Sum(d => d.CardIds.Length) == 4, "actual run created exactly four reviewable question cards");
        Unchanged("after closing drafts"); main.Close();
        Check(main.IsDisposed && !(bool)Property(monitor, "IsRunning")!, "closing the test app releases UI and watcher");
        Stage("complete");
    }

    private static async Task ReadQuestionAsync(ClipDraft parent, int index, AnkiConnection anki)
    {
        var kind = parent.Original.SelectedText + " 第 " + (index + 1) + " 轮";
        Check(parent.FollowUpCards.TryGetValue(index, out var childId), kind + " retains its per-round identity");
        var draft = Store.Find(childId!) ?? throw new InvalidOperationException("Missing confirmed question draft.");
        var frozen = draft.ConfirmedNote ?? throw new InvalidOperationException("Missing confirmed question card.");
        Check(draft.ImportState == ImportState.Verified && frozen.HasQuestionContext && frozen.Ordinals.SequenceEqual(new[] { 0 }) && draft.CardIds.Length == 1,
            kind + " freezes exactly one verified question card");
        var notes = await anki.CallAsync("notesInfo", new { notes = new[] { draft.NoteId!.Value } });
        var cards = await anki.CallAsync("cardsInfo", new { cards = draft.CardIds });
        var matches = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id });
        var suspended = await anki.CallAsync("areSuspended", new { cards = draft.CardIds });
        Check(matches.GetArrayLength() == 1 && matches[0].GetInt64() == draft.NoteId && notes.GetArrayLength() == 1 && cards.GetArrayLength() == 1,
            kind + " actual Anki readback finds one note and one card");
        var note = notes[0]; var card = cards[0];
        Check(note.GetProperty("modelName").GetString() == AnkiNoteDraft.QuestionModelName && frozen.Fields.All(f =>
            note.GetProperty("fields").GetProperty(f.Key).GetProperty("value").GetString() == f.Value), kind + " actual fields match the frozen preview");
        var view = QuestionCards.Preview(parent, Store)[index];
        Check(frozen.Fields["Front"] == AnkiNoteDraft.Html(parent.Conversation[index].Question) &&
            frozen.Fields["Back"] == AnkiNoteDraft.Html(view.Answer) && frozen.Fields["Context"] == AnkiNoteDraft.Html(view.Context),
            kind + " current question and prior context remain in separate fields");
        string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
        var front = card.GetProperty("question").GetString()!; var back = card.GetProperty("answer").GetString()!;
        Check(card.GetProperty("note").GetInt64() == draft.NoteId && card.GetProperty("deckName").GetString() == Deck && card.GetProperty("ord").GetInt32() == 0 &&
            card.GetProperty("queue").GetInt32() >= 0 && suspended.GetArrayLength() == 1 && suspended[0].ValueKind == JsonValueKind.False &&
            Plain(front).Contains(Plain(frozen.Fields["Front"]), StringComparison.Ordinal) &&
            Plain(back).Contains(Plain(frozen.Fields["Back"]), StringComparison.Ordinal), kind + " actual card has front/back in Canary deck and is available for review");
        Check(view.Context.Length == 0 ? !front.Contains("class='context'", StringComparison.Ordinal) :
            front.Contains("class='context'", StringComparison.Ordinal) && Plain(front).Contains(Plain(frozen.Fields["Context"]), StringComparison.Ordinal),
            kind + " preceding context is rendered only in the auxiliary front area when enabled");
        Readbacks[parent.Id + "-" + index] = new { draft.Id, draft.NoteId, draft.CardIds, deck = Deck, model = frozen.Model,
            front = frozen.Fields["Front"], back = frozen.Fields["Back"], context = frozen.Fields["Context"],
            queue = card.GetProperty("queue").GetInt32(), suspended = false };
        SaveEvidence();
    }
}

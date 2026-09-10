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
    private const string DataRoot = @"D:\AppData\KnowledgeClipper\canary\input-ui";
    private const string Deck = "KnowledgeClipper Canary::Architecture 20260910";
    private const string Translation = "循序渐进";
    private const string Question = "为什么正方形的对角线长度相等？";
    private const string FollowUp = "用更简单的话解释";
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
            scope = "Actual Desktop UI semantic controls and production Flow/API/Anki; no simulated input, TTS playback or global selection. At most one translation and one follow-up card."
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

        Stage("settings and modes");
        foreach (var enabled in new[] { true, false }) {
            var dialog = (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Instance).Single().Invoke([flow, DataRoot]);
            Owned.Add(dialog); dialog.Show(main);
            Control<CheckBox>(dialog, "typed-auto-read").Checked = enabled;
            var mode = Control<ComboBox>(dialog, "mode");
            var page = mode.Parent; while (page is not TabPage) page = page!.Parent;
            ((TabControl)page.Parent!).SelectedTab = (TabPage)page;
            for (var index = 0; index < mode.Items.Count; index++) {
                mode.SelectedIndex = index;
                var definition = (ModeDefinition)mode.SelectedItem!;
                Check(Control<CheckBox>(dialog, "follow-up-default").Checked == (definition.OutputKind == "question"), "settings follow-up default for " + definition.Id + " during typed toggle " + enabled);
            }
            Control<Button>(dialog, "save-settings").PerformClick(); await Task.Delay(50);
            Check(dialog.IsDisposed && WorkflowJson.Read<LanguageSettings>(settingsPath).TypedInputAutoRead == enabled &&
                ((LanguageSettings)Property(flow, "Settings")!).TypedInputAutoRead == enabled, "typed auto-read saves and applies " + enabled);
        }
        Check(!Directory.EnumerateFiles(Path.Combine(DataRoot, "modes"), "*", SearchOption.AllDirectories).Any(), "switching modes leaves prompts and definitions unchanged");
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

        Stage("translation input");
        var translation = await Submit(Translation); var translationDraft = Draft(translation);
        Check(translationDraft.Route is { OutputKind: "translation", RequiresContext: false, FollowUpDefault: false } &&
            translationDraft.Content is { Fields: null, Basic: not null, Answer: null }, "Chinese lookup creates a single Basic translation preview");
        Check(Control<TextBox>(translation, "basic-front").Text == Translation && Control<TextBox>(translation, "basic-front").ReadOnly &&
            !Control<ComboBox>(translation, "practice").Visible && !Control<Control>(translation, "follow-up").Visible, "translation preserves original front and omits practice and default follow-up");
        await ConfirmAndReadAsync(translation, anki, "translation"); Unchanged("after translation import");

        Stage("question and follow-up");
        var question = await Submit(Question); var questionDraft = Draft(question);
        Check(questionDraft.Route is { OutputKind: "question", RequiresContext: false, FollowUpDefault: true } &&
            questionDraft.Content?.Answer?.Answered == true && questionDraft.Content.Basic != null, "question route has a real answered Basic preview");
        Check(Control<Control>(question, "follow-up").Visible && Control<Button>(question, "follow-up-toggle").Text == "收起追问", "question follow-up is expanded by default");
        var first = questionDraft.Conversation.FirstOrDefault() ?? throw new InvalidOperationException("Initial question/answer missing.");
        Check(first.Question == Question && first.Answer == questionDraft.Content!.Answer, "initial question and answer begin the conversation");
        var original = questionDraft.Original; var material = questionDraft.Material; var captured = questionDraft.Times["captured"];
        var followInput = Control<TextBox>(question, "follow-up-input"); var send = Control<Button>(question, "follow-up-send");
        if (questionDraft.Conversation.Count == 1) {
            followInput.Text = FollowUp; Check(send.Enabled, "non-question-mark follow-up can be sent");
            send.PerformClick();
            Check(!send.Enabled && !Control<Button>(question, "confirm").Enabled, "follow-up blocks repeated send and import while pending");
            await UntilAsync(() => !(bool)Property(Control<Control>(question, "follow-up"), "Sending")!, "follow-up reply");
        }
        if (questionDraft.ConversationError != null) throw new InvalidOperationException(questionDraft.ConversationError);
        Check(questionDraft.Conversation.Count == 2 && questionDraft.Conversation[0] == first && questionDraft.Conversation[1].Question == FollowUp &&
            questionDraft.Conversation[1].Answer.Answered, "follow-up retains first turn and answers unpunctuated input in the same conversation");
        Check(questionDraft.Original == original && questionDraft.Material == material && questionDraft.Times["captured"] == captured &&
            !questionDraft.Times.ContainsKey("captureStarted"), "follow-up uses saved material without recapture");
        Check(followInput.Text.Length == 0 && questionDraft.PendingFollowUp == null, "successful follow-up clears the pending input");
        var transcript = Control<TextBox>(question, "conversation").Text;
        Check(transcript.Contains(Question) && transcript.Contains(first.Answer.AnswerText!) && transcript.IndexOf(Question, StringComparison.Ordinal) < transcript.IndexOf(FollowUp, StringComparison.Ordinal), "preview shows the original question and answer before follow-up");

        Stage("follow-up child card");
        var makeCard = Control<Button>(question, "follow-up-card"); Check(makeCard.Visible && makeCard.Enabled, "answered last turn offers its card preview");
        makeCard.PerformClick();
        var child = ((System.Collections.IEnumerable)Get(flow, "forms")!).Cast<Form>().Single(f => Draft(f).ParentDraftId == questionDraft.Id);
        Owned.Add(child); var childDraft = Draft(child);
        Check(childDraft.Original.SelectedText == FollowUp && childDraft.Content?.Basic?.Front == FollowUp && childDraft.ParentDraftId == questionDraft.Id &&
            childDraft.ConversationMaterial?.SelectedText == Question && childDraft.Conversation.First().Question == Question, "child card preserves this turn plus parent identity and original question context");
        Check(questionDraft.ConfirmedNote == null && questionDraft.NoteId == null && questionDraft.ImportState == ImportState.Unconfirmed &&
            questionDraft.FollowUpCards.GetValueOrDefault(1) == childDraft.Id, "child preview leaves original question unconfirmed and saves its link");
        await ConfirmAndReadAsync(child, anki, "follow-up"); Unchanged("after follow-up import");

        Stage("closed draft persistence");
        foreach (var form in new[] { translation, question, child }) form.Close();
        foreach (var draft in new[] { translationDraft, questionDraft, childDraft }) {
            var loaded = Store.Find(draft.Id) ?? throw new InvalidOperationException("Saved draft missing.");
            Check(loaded.Input == draft.Input && loaded.Route == draft.Route && loaded.ParentDraftId == draft.ParentDraftId &&
                JsonSerializer.Serialize(loaded.Conversation, WorkflowJson.Options) == JsonSerializer.Serialize(draft.Conversation, WorkflowJson.Options) &&
                loaded.ImportState == draft.ImportState && loaded.CardIds.SequenceEqual(draft.CardIds), "closed draft reload preserves metadata, conversation and import for " + draft.Id);
        }
        Check(Store.Recent().Count() == 3 && Store.Recent().Sum(d => d.CardIds.Length) == 2 && Store.Recent().All(d => !d.Times.ContainsKey("speechRequested")), "run retains exactly three drafts and two single cards without speech");
        Unchanged("after closing drafts"); main.Close();
        Check(main.IsDisposed && !(bool)Property(monitor, "IsRunning")!, "closing the test app releases UI and watcher");
        Stage("complete");
    }

    private static async Task ConfirmAndReadAsync(Form preview, AnkiConnection anki, string kind)
    {
        Stage(kind + " Anki confirmation"); var draft = Draft(preview); var button = Control<Button>(preview, "confirm");
        if (draft.ImportState != ImportState.Verified) {
            if (draft.ImportState is ImportState.Writing or ImportState.Unknown || draft.NoteId != null)
                throw new InvalidOperationException("Anki result needs explicit reconciliation; this canary will not add another note.");
            Check(draft.ConfirmedNote == null && button.Visible && button.Enabled, kind + " has one available preview confirmation");
            var before = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id });
            Check(before.GetArrayLength() == 0, kind + " identity has no existing Anki result before confirmation");
            button.PerformClick();
            await UntilAsync(() => !(bool)Get(preview, "committing")!, kind + " Anki import", 90);
            if (draft.ImportState != ImportState.Verified) throw new InvalidOperationException(draft.ImportError ?? "Anki import not verified.");
        }
        var frozen = draft.ConfirmedNote!;
        Check(frozen.IsBasic && frozen.Ordinals.SequenceEqual(new[] { 0 }) && draft.CardIds.Length == 1 && !button.Enabled,
            kind + " confirmation freezes one Basic card and disables a repeat");
        var notes = await anki.CallAsync("notesInfo", new { notes = new[] { draft.NoteId!.Value } });
        var cards = await anki.CallAsync("cardsInfo", new { cards = draft.CardIds });
        var matches = await anki.CallAsync("findNotes", new { query = "tag:kc_" + draft.Id });
        var suspended = await anki.CallAsync("areSuspended", new { cards = draft.CardIds });
        Check(matches.GetArrayLength() == 1 && matches[0].GetInt64() == draft.NoteId && notes.GetArrayLength() == 1 && cards.GetArrayLength() == 1,
            kind + " actual Anki readback finds one note and one card");
        var note = notes[0]; var card = cards[0];
        Check(note.GetProperty("modelName").GetString() == AnkiNoteDraft.BasicModelName && frozen.Fields.All(f =>
            note.GetProperty("fields").GetProperty(f.Key).GetProperty("value").GetString() == f.Value), kind + " actual fields match the frozen preview");
        string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
        Check(card.GetProperty("note").GetInt64() == draft.NoteId && card.GetProperty("deckName").GetString() == Deck && card.GetProperty("ord").GetInt32() == 0 &&
            card.GetProperty("queue").GetInt32() >= 0 && suspended.GetArrayLength() == 1 && suspended[0].ValueKind == JsonValueKind.False &&
            Plain(card.GetProperty("question").GetString()!).Contains(Plain(frozen.Fields["Front"]), StringComparison.Ordinal) &&
            Plain(card.GetProperty("answer").GetString()!).Contains(Plain(frozen.Fields["Back"]), StringComparison.Ordinal), kind + " actual card has front/back in Canary deck and is available for review");
        Readbacks[kind] = new { draft.Id, draft.NoteId, draft.CardIds, deck = Deck, model = frozen.Model,
            front = frozen.Fields["Front"], back = frozen.Fields["Back"], queue = card.GetProperty("queue").GetInt32(), suspended = false };
        SaveEvidence();
    }
}

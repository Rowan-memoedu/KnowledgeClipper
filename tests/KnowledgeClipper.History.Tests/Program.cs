using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using KnowledgeClipper.Workflow;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Use tools/Invoke-IsolatedDesktopTest.ps1; never run on the user's desktop.");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        var result = 0;
        timer.Tick += async (_, _) => { timer.Stop(); try { await RunAsync(args); } catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; } finally { context.ExitThread(); } };
        timer.Start(); Application.Run(context); return result;
    }
    private static object? Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Flags)!.Invoke(value, args);
    private static Button Button(Form form, string name) => (Button)form.Controls.Find(name, true).Single();
    private static void Click(Control control) => Call(control, "OnClick", EventArgs.Empty);
    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(25);
        if (!condition()) throw new TimeoutException("History or playback did not reach its expected state.");
    }
    private static void WriteAudio(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = 16000 * 2 * 3;
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }
    private static async Task RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("History.Tests <project> <receipt>");
        var project = args[0]; var receipt = args[1]; Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        var root = Path.Combine(@"D:\Caches\KnowledgeClipper\history-ui-tests", Guid.NewGuid().ToString("N"));
        var drafts = new DraftStore(root); var excerpts = new ExcerptStore(root);
        var settings = new LanguageSettings { GlobalSelection = false, LanguageAutoRead = false, ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1",
            AutoVoice = false, Voice = "invalid-voice-cache-only", ExcerptModesDirectory = Path.Combine(project, "modes") };
        var configuration = new ContentConfiguration("", new[] { "language", "question", "translation" }.Select(kind =>
            new ModeSnapshot(new ModeDefinition(kind, kind, true, "", "", "", kind, RequiresContext: false), "", "{}")).ToArray());
        ClipDraft Draft(string kind, string title, int minutes)
        {
            var material = new CaptureMaterial(title, null, "unavailable", null, "Public history fixture", 0, DateTimeOffset.UtcNow.AddMinutes(-minutes));
            var answer = new QuestionAnswer("answered", "A public fixture answer.", []);
            var draft = new ClipDraft { Original = material, Material = material, Settings = settings, Configuration = configuration,
                Input = new("fixture", kind), ModeId = kind, Route = new(kind, kind, false, false),
                Content = kind == "language" ? new(kind, new LanguageFields("/test/", "测试", null, null)) :
                    kind == "question" ? new(kind, Answer: answer) : new(kind, Basic: new(title, "测试译文")) };
            if (kind == "question") draft.Conversation.Add(new(title, answer));
            drafts.Save(draft); return draft;
        }
        var older = Draft("language", "Earlier language recording.", 120);
        var newer = Draft("language", "Latest language recording.", 60);
        foreach (var draft in new[] { older, newer })
        {
            var key = StreamingSpeech.AudioCacheKey(draft.Material.SelectedText, SpeechPurpose.Selection, settings);
            draft.AudioPath = Path.Combine(@"D:\Caches\KnowledgeClipper\audio", draft.Id + "-" + key + ".mp3");
            WriteAudio(draft.AudioPath); draft.Times["speechRequested"] = draft.Original.CapturedAt; drafts.Save(draft);
        }
        var question = Draft("question", "How can I review this card?", 5);
        var translation = Draft("translation", "Translate this fixture.", 1);
        translation.Times["speechRequested"] = DateTimeOffset.UtcNow; translation.AudioPath = newer.AudioPath; drafts.Save(translation);
        for (var i = 0; i < 25; i++) Draft("question", "Older question " + i, 200 + i);
        for (var i = 0; i < 35; i++) ExcerptCards.Create(new("Excerpt record " + i, null, "unavailable", null, "Public fixture", 0, DateTimeOffset.UtcNow.AddMinutes(-300-i)), settings, excerpts);
        var excerpt = ExcerptCards.Create(new("A manual excerpt remains editable.", null, "unavailable", null, "Public fixture", 0, DateTimeOffset.UtcNow.AddMinutes(-10)), settings, excerpts);
        var sameId = new ExcerptSession { Id = older.Id, Original = excerpt.Original, Settings = settings, ManualFront = excerpt.Original.SelectedText };
        ExcerptCards.Add(sameId, excerpts, new(sameId.Original.SelectedText, ""), "原文");
        var checks = new List<string>();
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        SnapshotChecks.Run(Path.Combine(root, "snapshot-regression"), Check);
        HistoryActionChecks.Run(Check);
        var all = HistoryRecords.Read(drafts, excerpts);
        Check(all.Length == 66 && all.Count(r => r.Excerpt != null) == 37, "unified history includes all records beyond both old 20/30 limits");
        Check(all.Select(r => r.Key).Distinct().Count() == all.Length, "equal identifiers from different stores remain separate records");
        Check(HistoryRecords.LastLanguageSpeech(drafts)?.Id == newer.Id, "newer questions and translated audio cannot replace the last language replay");
        Check(all.Where(r => r.Kind != "语言").All(r => !r.CanPlay), "only language history records are playable");
        WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
        var assembly = Assembly.Load("KnowledgeClipper");
        Type Type(string name) => assembly.GetType("KnowledgeClipper." + name, true)!;
        object New(string name, params object?[] values) => Type(name).GetConstructors(Flags).Single(c => c.GetParameters().Length == values.Length).Invoke(values);
        var configType = Type("AppConfig");
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(project, "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, root);
        var controller = New("Desktop.Core.DesktopController", new object?[5]);
        using var main = (Form)Type("BoundClipperForm").GetConstructor(Flags, null, [configType, Type("Desktop.Core.DesktopController"), typeof(bool)], null)!.Invoke([config, controller, false]);
        Call(main, "EnableLanguageWorkflow"); main.Show();
        var language = Field(main, "languageFlow")!; var historyFlow = Field(main, "historyFlow")!;
        var status = (Button)Field(main, "statusButton")!;
        Form? History() => (Form?)Field(historyFlow, "history");
        ClipDraft Last() => (ClipDraft)Field(language, "lastSpeech")!;
        try
        {
            var before = newer.LastSpeechAt;
            Click(status);
            Check(History() == null && Last().LastSpeechAt == before, "single click is deferred until the system double-click window has elapsed");
            await WaitFor(() => Last().LastSpeechAt > before);
            Check(History() == null && Last().Id == newer.Id, "single click replays the last language record without opening a window");
            await WaitFor(() => Last().Times.ContainsKey("playerStarted"));
            Check(Last().AudioPath == newer.AudioPath && !Last().Times.ContainsKey("firstAudioChunk"), "single replay uses the saved audio with no synthesis");
            Call(language, "Stop"); await WaitFor(() => !(bool)language.GetType().GetProperty("IsPlaying", Flags)!.GetValue(language)!);
            before = Last().LastSpeechAt;
            Click(status); Call(status, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 2, 4, 4, 0));
            await Task.Delay(SystemInformation.DoubleClickTime + 60);
            Check(History() == null && Last().LastSpeechAt == before, "holding the second click cannot let the pending single-click audio start");
            Call(status, "OnDoubleClick", EventArgs.Empty); Click(status);
            await Task.Delay(SystemInformation.DoubleClickTime + 100);
            var history = History()!;
            Check(history is { Visible: true, Text: "历史记录" } && Last().LastSpeechAt == before, "double-click opens history without triggering either pending or trailing single-click audio");
            await WaitFor(() => Descendants(history).Any(c => c.Name.StartsWith("knowledge-clipper.history.record.")));
            var rowLabels = Descendants(history).Where(c => c.Name.StartsWith("knowledge-clipper.history.record.")).ToArray();
            Check(rowLabels.Length == 20, "history bounds native controls to twenty rows per page");
            var visited = new HashSet<string>();
            do
            {
                foreach (var label in Descendants(history).Where(c => c.Name.StartsWith("knowledge-clipper.history.record.")))
                    visited.Add(label.Name["knowledge-clipper.history.record.".Length..]);
                foreach (var record in all.Where(r => !r.CanPlay && history.Controls.Find("knowledge-clipper.history.play." + r.Key, true).Length > 0))
                {
                    var play = Button(history, "knowledge-clipper.history.play." + record.Key);
                    if (play.Visible || play.Enabled) throw new Exception("Non-language playback is exposed.");
                }
                if (!Button(history, "knowledge-clipper.history.next").Enabled) break;
                Click(Button(history, "knowledge-clipper.history.next"));
            } while (true);
            Check(visited.SetEquals(all.Select(record => record.Key)), "pagination makes every saved record accessible without duplication");
            while (Button(history, "knowledge-clipper.history.previous").Enabled) Click(Button(history, "knowledge-clipper.history.previous"));
            var stableRow = history.Controls.Find("knowledge-clipper.history.record.draft." + older.Id, true).Single();
            Call(history, "OnActivated", EventArgs.Empty); await Task.Delay(100);
            Check(ReferenceEquals(stableRow, history.Controls.Find(stableRow.Name, true).Single()), "reactivating history does not rebuild rows or reread every draft");
            Click(Button(history, "knowledge-clipper.history.refresh"));
            await WaitFor(() => !(bool)Field(historyFlow, "loading")!);
            Check(ReferenceEquals(stableRow, history.Controls.Find(stableRow.Name, true).Single()), "explicit refresh updates unchanged rows without disposing native controls");
            Check(true, "excerpt, question and translation rows hide and disable playback");
            var oldPlay = Button(history, "knowledge-clipper.history.play.draft." + older.Id);
            var oldContinue = Button(history, "knowledge-clipper.history.continue.draft." + older.Id);
            history.Size = history.MinimumSize;
            var oldRow = oldPlay.Parent!.Parent!;
            ((ScrollableControl)oldRow.Parent!).ScrollControlIntoView(oldRow); await Task.Delay(60);
            var viewport = oldRow.Parent!;
            Check(viewport.ClientRectangle.Contains(viewport.RectangleToClient(oldContinue.RectangleToScreen(oldContinue.ClientRectangle))), "row actions remain fully inside the scrolling viewport at minimum history size");
            var oldTitle = history.Controls.Find("knowledge-clipper.history.record.draft." + older.Id, true).Single();
            Check(oldTitle.Width > 350 && history.ClientRectangle.Contains(history.RectangleToClient(oldTitle.RectangleToScreen(oldTitle.ClientRectangle))),
                "record text retains the full card width instead of being squeezed by playback actions");
            using (var bitmap = new Bitmap(history.Width, history.Height))
            {
                using var graphics = Graphics.FromImage(bitmap); var dc = graphics.GetHdc();
                try { if (!PrintWindow(history.Handle, dc, 2)) throw new Exception("History render failed."); } finally { graphics.ReleaseHdc(dc); }
                bitmap.Save(Path.Combine(Path.GetDirectoryName(receipt)!, "history-minimum.png"));
            }
            Click(oldPlay); await WaitFor(() => Last().Id == older.Id && Last().Times.ContainsKey("playerStarted"));
            Check(Last().AudioPath == older.AudioPath && !Last().Times.ContainsKey("firstAudioChunk"), "an older language row replays its own saved audio");
            Click(oldContinue); await Task.Delay(80);
            var languagePreview = Application.OpenForms.Cast<Form>().Single(f => f.Name == "knowledge-clipper.language.preview");
            Check(((ClipDraft)languagePreview.GetType().GetProperty("Draft", Flags)!.GetValue(languagePreview)!).Id == older.Id, "language continue opens the exact existing preview");
            languagePreview.Close();
            Click(Button(history, "knowledge-clipper.history.continue.draft." + question.Id)); await Task.Delay(80);
            var questionPreview = Application.OpenForms.Cast<Form>().Single(f => f.Name == "knowledge-clipper.chat");
            Check(((ClipDraft)questionPreview.GetType().GetProperty("Draft", Flags)!.GetValue(questionPreview)!).Id == question.Id, "question continue preserves the existing conversation");
            questionPreview.Close();
            Click(Button(history, "knowledge-clipper.history.continue.excerpt." + excerpt.Id)); await Task.Delay(80);
            var excerptPreview = Application.OpenForms.Cast<Form>().Single(f => f.GetType().Name == "ExcerptDraftForm");
            Check(((ExcerptSession)excerptPreview.GetType().GetProperty("Session", Flags)!.GetValue(excerptPreview)!).Id == excerpt.Id, "excerpt continue uses its independent original card session");
            excerptPreview.Close();
            Check(drafts.AllSessions().All(d => d.NoteId == null) && excerpts.All().All(s => s.Cards.All(c => excerpts.Cards.Find(c.Id)!.NoteId == null)), "history navigation creates no Anki notes or confirmation");
            Check(Descendants(history).OfType<Button>().Where(b => b.Name.StartsWith("knowledge-clipper.history.continue.")).All(b => b.Text == "继续制卡"),
                "every never-imported record shows the exact continue label");
            var liveLanguage = Last(); liveLanguage.ImportState = ImportState.Verified;
            Call(language, "Changed", liveLanguage, null);
            await WaitFor(() => Button(history, "knowledge-clipper.history.continue.draft." + older.Id).Text == "查看");
            Check(Button(history, "knowledge-clipper.history.continue.draft." + older.Id).Text == "查看", "language import notifications update the actual history action to view");
            var liveQuestion = (ClipDraft)Call(language, "LiveDraft", question)!;
            liveQuestion.Content = liveQuestion.Content! with { Basic = new(liveQuestion.QuestionText, liveQuestion.Content!.Answer!.AnswerText!) };
            HistoryActionChecks.VerifyFixture(liveQuestion, drafts);
            liveQuestion.Chat!.Compositions.Single().ExportDraftId = liveQuestion.Id;
            Call(language, "Changed", liveQuestion, null);
            await WaitFor(() => Button(history, "knowledge-clipper.history.continue.draft." + question.Id).Text == "查看");
            Check(Button(history, "knowledge-clipper.history.continue.draft." + question.Id).Text == "查看", "an imported question shows view instead of a follow-up-specific caption");
            liveQuestion.Chat!.Compositions.Add(new(){Front="A further public question?",Back="A further answer."});
            Call(language, "Changed", liveQuestion, null);
            await WaitFor(() => Button(history, "knowledge-clipper.history.continue.draft." + question.Id).Text == "继续制卡");
            Check(Button(history, "knowledge-clipper.history.continue.draft." + question.Id).Text == "继续制卡", "a newly generated chat card immediately restores continue in the visible history");
            Click(Button(history, "knowledge-clipper.history.continue.excerpt." + excerpt.Id)); await Task.Delay(60);
            excerptPreview = Application.OpenForms.Cast<Form>().Single(f => f.GetType().Name == "ExcerptDraftForm");
            foreach (var link in excerpt.Cards) HistoryActionChecks.VerifyFixture(excerpts.Cards.Find(link.Id)!, excerpts.Cards);
            Call(excerptPreview, "RefreshCards");
            Click(Button(history, "knowledge-clipper.history.refresh"));
            await WaitFor(() => Button(history, "knowledge-clipper.history.continue.excerpt." + excerpt.Id).Text == "查看");
            Check(Button(history, "knowledge-clipper.history.continue.excerpt." + excerpt.Id).Text == "查看", "excerpt import notifications update the visible history without reopening");
            ((CheckedListBox)excerptPreview.Controls.Find("knowledge-clipper.excerpt.modes", true).Single()).SetItemChecked(0, true);
            Click(Button(excerptPreview, "knowledge-clipper.excerpt.add-manual"));
            await WaitFor(() => Button(history, "knowledge-clipper.history.continue.excerpt." + excerpt.Id).Text == "继续制卡");
            Check(Button(history, "knowledge-clipper.history.continue.excerpt." + excerpt.Id).Text == "继续制卡", "adding a card through the actual manual form immediately restores continue");
            excerptPreview.Close();
            history.Close(); Call(language, "Stop"); await Task.Delay(100);
            Check(HistoryRecords.LastLanguageSpeech(new DraftStore(root))?.Id == older.Id, "the last selected language recording persists across reopening");
            var external = Draft("question", "New saved record while history is closed", 0);
            Call(historyFlow, "Show"); var reopened = History();
            Check(reopened is { Text: "历史记录", Visible: true }, "closed history reopens normally");
            await WaitFor(() => Descendants(reopened!).OfType<Button>().Any(b => b.Name == "knowledge-clipper.history.continue.draft." + external.Id));
            Check(true, "cached history reconciles disk changes without a desktop notification");
            external.Chat = new() { Input = "Newer persisted input after list projection" }; drafts.Save(external);
            Click(Button(reopened!, "knowledge-clipper.history.continue.draft." + external.Id));
            var currentChat = Application.OpenForms.Cast<Form>().Single(f => f.Name == "knowledge-clipper.chat");
            Check(((ClipDraft)currentChat.GetType().GetProperty("Draft", Flags)!.GetValue(currentChat)!).Chat!.Input == external.Chat.Input,
                "opening a cached history row uses current saved content rather than the presentation snapshot");
            currentChat.Close();
            reopened!.Close();
            WorkflowJson.Save(receipt, new { passed = true, checks, recordCount = all.Length, dataRoot = root, scope = "production controls and flow on private Windows desktop; saved public audio through real player; no content API or Anki writes" });
        }
        finally
        {
            ((IDisposable)historyFlow).Dispose(); ((IDisposable)Field(main, "imageFlow")!).Dispose();
            ((IDisposable)language).Dispose(); ((IDisposable)Field(main, "excerptFlow")!).Dispose();
            ((IDisposable)Field(main, "selectionMonitor")!).Dispose(); ((Form)Field(main, "selectionDot")!).Dispose();
        }
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls) { yield return control; foreach (var child in Descendants(control)) yield return child; }
    }
}

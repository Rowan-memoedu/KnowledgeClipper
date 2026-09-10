using System.Reflection;
using System.IO;
using KnowledgeClipper.Workflow;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        var result = 0;
        timer.Tick += async (_, _) => {
            timer.Stop();
            try { await RunAsync(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { context.ExitThread(); }
        };
        timer.Start(); Application.Run(context); return result;
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("History.Tests <older draft> <newer draft> <artifact directory>");
        var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\history-ui-tests", Guid.NewGuid().ToString("N"));
        var store = new DraftStore(dataRoot);
        ClipDraft Sample(string path, int minutesAgo)
        {
            var source = WorkflowJson.Read<ClipDraft>(path);
            if (source.AudioPath == null || !File.Exists(source.AudioPath) || source.Content == null) throw new InvalidDataException("Real completed audio and content are required.");
            var draft = new ClipDraft {
                Original = source.Original, Material = source.Material, Configuration = source.Configuration,
                Settings = source.Settings with { ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1", AutoVoice = false, Voice = "invalid-voice-must-use-saved-audio" },
                Content = source.Content, ModeId = source.ModeId, AudioPath = source.AudioPath, Practice = source.Practice,
                SpeechStatus = "朗读完成，可重播"
            };
            draft.Times["speechStarted"] = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
            store.Save(draft); return draft;
        }
        var older = Sample(args[0], 20); var newer = Sample(args[1], 10);
        WorkflowJson.Save(Path.Combine(dataRoot, "language-settings.json"), newer.Settings);
        var assembly = Assembly.Load("KnowledgeClipper");
        var flowType = assembly.GetType("KnowledgeClipper.LanguageDesktopFlow", true)!;
        var userSettings = Activator.CreateInstance(assembly.GetType("KnowledgeClipper.UserSettings", true)!)!;
        using var owner = new Form { Width = 360, Height = 160, Text = "Replay routing check" };
        owner.Show();
        var flow = flowType.GetConstructors().Single().Invoke([owner, dataRoot, (Action<string>)(_ => { }), userSettings]);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        object? Field(string name) => flowType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(flow);
        void Call(string name) => flowType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(flow, null);
        Button Button(Form form, string name) => (Button)form.Controls.Find(name, true).Single();
        async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!condition()) throw new TimeoutException("Playback/UI condition did not complete.");
        }
        try
        {
            var began = DateTimeOffset.UtcNow;
            Call("ReplayAndShowRecent");
            var history = (Form)Field("history")!;
            var playing = (ClipDraft)Field("lastSpeech")!;
            Check(history.Visible && playing.Id == newer.Id, "replay starts the last reading and opens history without item selection");
            await WaitFor(() => playing.Times.TryGetValue("audioOutputStarted", out var at) && at >= began);
            Check(playing.AudioPath == newer.AudioPath && !playing.Times.ContainsKey("firstAudioChunk"), "automatic replay uses its real saved audio without synthesis");
            history.Size = history.MinimumSize; await Task.Delay(80);
            var playOlder = Button(history, "knowledge-clipper.language.history.play." + older.Id);
            var continueOlder = Button(history, "knowledge-clipper.language.history.continue." + older.Id);
            ((ScrollableControl)playOlder.Parent!.Parent!).ScrollControlIntoView(playOlder); await Task.Delay(50);
            using (var image = new Bitmap(history.Width, history.Height)) { history.DrawToBitmap(image, new Rectangle(Point.Empty, history.Size)); image.Save(Path.Combine(args[2], "history-minimum.png")); }
            Console.WriteLine("History client=" + history.ClientRectangle + "; play=" + history.RectangleToClient(playOlder.RectangleToScreen(playOlder.ClientRectangle)) + "; continue=" + history.RectangleToClient(continueOlder.RectangleToScreen(continueOlder.ClientRectangle)));
            Check(history.ClientRectangle.Contains(history.RectangleToClient(playOlder.RectangleToScreen(playOlder.ClientRectangle))) &&
                history.ClientRectangle.Contains(history.RectangleToClient(continueOlder.RectangleToScreen(continueOlder.ClientRectangle))), "history row actions fit at minimum width and current DPI");
            began = DateTimeOffset.UtcNow; playOlder.PerformClick();
            playing = (ClipDraft)Field("lastSpeech")!;
            await WaitFor(() => playing.Times.TryGetValue("audioOutputStarted", out var at) && at >= began);
            Check(playing.Id == older.Id && playing.AudioPath == older.AudioPath && !playing.Times.ContainsKey("firstAudioChunk"), "clicking another row plays that record with its own saved audio");
            continueOlder.PerformClick(); await Task.Delay(80);
            var previews = Application.OpenForms.Cast<Form>().Where(f => f.Name == "knowledge-clipper.language.preview").ToArray();
            Check(previews.Length == 1 && ((ClipDraft)previews[0].GetType().GetProperty("Draft", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(previews[0])!).Id == older.Id, "continue cards opens the exact selected draft");
            Check(playing.ConfirmedNote == null && playing.NoteId == null, "opening continue cards does not confirm or import");
            var confirm = Button(previews[0], "knowledge-clipper.language.confirm");
            Check(confirm.Visible && confirm.Enabled && previews[0].ClientRectangle.Contains(previews[0].RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle))), "ready preview keeps its confirmation visible");
            history.Close();
            Check((bool)flowType.GetProperty("IsPlaying", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(flow)!, "closing history does not stop the reading");
            Call("Stop"); await WaitFor(() => !(bool)flowType.GetProperty("IsPlaying", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(flow)!);
            Check(store.RecentSpeech().First().Id == older.Id, "last clicked reading is persisted for replay after reopening");
            WorkflowJson.Save(Path.Combine(args[2], "history-ui-check.json"), new { passed = true, checks, playing.Id, playing.AudioPath, playing.Times, dataRoot });
        }
        finally { ((IDisposable)flow).Dispose(); owner.Close(); }
    }
}

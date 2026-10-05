using System.IO;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static void ListenQuickFeedback(object flow, List<string> messages) =>
        flow.GetType().GetEvent("QuickFeedback", Flags)!.GetAddMethod(true)!.Invoke(flow, [(Action<string>)messages.Add]);

    private static async Task QuickFeedbackAsync(Form owner)
    {
        foreach (var success in new[] { false, true })
        {
            using var endpoint = new DeferredExcerptFixture(); endpoint.ReleaseResponses();
            var root = Path.Combine(dataRoot, "excerpt-feedback-" + success);
            var settings = Settings() with { RemNoteSdkEndpoint = success ? endpoint.Url : "http://127.0.0.1:1" };
            using var flow = (IDisposable)New("ExcerptDesktopFlow", owner, root, settings, (Action<string>)statuses.Add);
            var messages = new List<string>(); ListenQuickFeedback(flow, messages);
            var store = new ExcerptStore(root);
            var session = ExcerptCards.Create(Material("A public excerpt for completion feedback."), settings, store);
            await (Task)Call(flow, "CompleteQuickAsync", session)!;
            Check(messages.Count == 1 && messages[0].Contains(success ? "成功" : "失败"),
                "quick text excerpt reports its verified success or failure exactly once");
            Check(success ? Forms(flow).Length == 0 && ExcerptCards.Get(session, store, session.Cards[0].Id).ImportState == ImportState.Verified :
                Forms(flow).Length == 1 && ReferenceEquals(Get(Forms(flow).Single(), "Session"), session),
                "feedback success follows verification; failure keeps the original retry session");
            foreach (var form in Forms(flow)) form.Close();
            Call(flow, "StartMaterial", Material("Ordinary excerpt still requires confirmation."), settings, null, false);
            Check(messages.Count == 1, "normal excerpt does not emit shortcut feedback");
            foreach (var form in Forms(flow)) form.Close();
        }

        using (var endpoint = new QuickLanguageImportFixture())
        using (var provider = new QuestionApiFixture())
        {
            var root = Path.Combine(dataRoot, "question-feedback");
            var settings = Settings() with { ApiBaseUrl = provider.Url, RemNoteSdkEndpoint = endpoint.Url };
            WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
            using var flow = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
            var messages = new List<string>(); ListenQuickFeedback(flow, messages);
            var configuration = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"));
            var draft = Start(flow, "What is two plus two?", "question", settings, configuration, true);
            await Until(() => messages.Count == 1, "quick question emits completion feedback after import", 12000);
            Check(messages[0].Contains("成功") && Forms(flow).Length == 0 && endpoint.ExportCount == 1 && provider.RequestCount == 1,
                "successful quick question shows a notice with one answer and one export");
            var failed = Start(flow, "Another public question?", "question", settings with { ApiBaseUrl = "http://127.0.0.1:1" }, configuration, true);
            await Until(() => messages.Count == 2, "quick question content failure emits feedback", 12000);
            Check(messages[1].Contains("失败") && Forms(flow).Length == 1 && ReferenceEquals(Get(Forms(flow).Single(), "Draft"), failed),
                "failed question notice points to the same retained draft");
            foreach (var form in Forms(flow)) form.Close();
        }

        // Use the exact presentation class shared by meaning and completion.
        using (var popup = (Form)New("MeaningPopup", (object)null!))
        {
            Call(popup, "ShowMeaning", "摘录成功 · 已导入 RemNote", new Point(250, 200));
            Check((bool)Get(popup, "IsRequestVisible")! && popup.AccessibleDescription == "摘录成功 · 已导入 RemNote" &&
                popup.BackColor == Color.FromArgb(231, 237, 245) && !popup.ShowInTaskbar && popup.TopMost,
                "completion uses the actual small meaning-popup style");
            Save(popup, "quick-success.png");
            Call(popup, "ShowError", "摘录失败：连接不可用，请稍后重试", new Point(250, 200));
            Save(popup, "quick-failure.png");
            Call(popup, "PointerDown", new Point(popup.Right + 30, popup.Bottom + 30));
            await Task.Delay(50);
            Check(!(bool)Get(popup, "IsRequestVisible")!, "completion notice dismisses on outside click");
        }
        await QuickLanguageSpeechAsync(owner);
        await PendingReadingFailureAsync(owner);
        await ImageRoutesAsync(owner);
    }

    private static async Task PendingReadingFailureAsync(Form owner)
    {
        var root = Path.Combine(dataRoot, "pending-reading-failure"); Directory.CreateDirectory(root);
        var settings = Settings() with { Practice = PracticeChoice.Reading, LanguageAutoRead = true,
            PythonPath = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe" };
        WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
        using var flow = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
        var messages = new List<string>(); ListenQuickFeedback(flow, messages);
        var helper = Path.Combine(root, "speech-fixture.py"); File.WriteAllText(helper, SpeechFixture);
        File.WriteAllText(Path.Combine(root, "fail-speech"), "deliberate failure");
        ((StreamingSpeech)flow.GetType().GetField("speech", Flags)!.GetValue(flow)!).Dispose();
        using var speech = new StreamingSpeech(helper, Path.Combine(root, "audio")); Field(flow, "speech", speech);
        using var provider = new ImageRouteProvider(); using var http = new System.Net.Http.HttpClient(provider);
        ((System.Net.Http.HttpClient)flow.GetType().GetField("http", Flags)!.GetValue(flow)!).Dispose(); Field(flow, "http", http);
        var configuration = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"));
        var draft = (ClipDraft)Call(flow, "StartMaterial", Material("lead"), new InputMetadata("selection-dot", "language", ReadRequested: true),
            settings, configuration, DateTimeOffset.UtcNow, true)!;
        Call(flow, "BeginMeaningPopup", draft, new Point(250, 200));
        var store = new DraftStore(root);
        await Until(() => draft.ModeCards.Count == 1 && LanguageModeCards.Children(draft, store).All(c => c.AwaitingAnki),
            "pending import keeps the original reading task alive", 12000);
        Check(messages.Count == 0, "pending import waits for the actual reading result before reporting completion");
        File.WriteAllText(Path.Combine(root, "release-speech"), "release");
        await Until(() => messages.Count == 1, "reading failure still reports when Anki is unavailable", 12000);
        Check(messages[0].Contains("失败") && draft.SpeechError != null && LanguageModeCards.Children(draft, store).All(c => c.AwaitingAnki),
            "reading failure notice preserves the already queued card identity");
        var popup = (Form)flow.GetType().GetField("meaningPopup", Flags)!.GetValue(flow)!;
        Check(!(bool)Get(popup, "IsRequestVisible")!, "failed reading invalidates its meaning so late results cannot overwrite failure feedback");
        foreach (var form in Forms(flow)) form.Close();
    }
}

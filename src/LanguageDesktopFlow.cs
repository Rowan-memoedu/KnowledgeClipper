using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class LanguageDesktopFlow : IDisposable
{
    private readonly Form owner;
    private readonly string dataRoot, baseRoot;
    private readonly Action<string> status;
    private readonly HttpClient http = new();
    private readonly DraftStore store;
    private readonly StreamingSpeech speech;
    private readonly LanguageDesktopCapture capture;
    private readonly List<LanguageDraftForm> forms = new();
    private ClipDraft latest, lastSpeech;
    private LanguageReadingHistoryForm history;
    private LanguageInputForm inputForm;
    private bool capturing;
    internal bool IsPlaying => speech.IsPlaying;
    internal LanguageSettings Settings { get; private set; }
    internal event Action SettingsChanged;
    internal string SettingsPath => Path.Combine(dataRoot, "language-settings.json");
    internal string ModeOverrides => Path.Combine(dataRoot, "modes");
    internal string ModeDefaults => Path.Combine(baseRoot, "modes");

    public LanguageDesktopFlow(Form owner, string dataRoot, Action<string> status, UserSettings initial)
    {
        this.owner = owner; this.dataRoot = dataRoot; this.status = status;
        baseRoot = AppContext.BaseDirectory;
        Settings = File.Exists(SettingsPath) ? WorkflowJson.Read<LanguageSettings>(SettingsPath) : new LanguageSettings
        {
            Voice = initial.ChineseVoice, EnglishVoice = initial.EnglishVoice,
            AutoVoice = initial.LanguageOverride == "auto", RatePercent = initial.RatePercent,
            VolumePercent = initial.VolumePercent, PitchHz = initial.PitchHz,
            ContextEnabled = initial.ContextModeEnabled, ContextGranularity = initial.ContextGranularity
        };
        Settings.Validate();
        store = new DraftStore(dataRoot);
        speech = new StreamingSpeech(Path.Combine(baseRoot, "helpers", "stream_speech.py"), @"D:\Caches\KnowledgeClipper\audio");
        speech.Prepare(Settings);
        capture = new LanguageDesktopCapture(Path.Combine(baseRoot, "helpers", "selection.cjs"));
        lastSpeech = store.RecentSpeech().FirstOrDefault();
    }

    internal void SaveSettings(LanguageSettings value)
    {
        value.Validate(); WorkflowJson.Save(SettingsPath, value); Settings = value; speech.Prepare(value); SettingsChanged?.Invoke();
    }

    public async Task StartAsync(bool read, SelectionSnapshot frozen = null)
    {
        if (capturing) return;
        capturing = true;
        var requestedAt = DateTimeOffset.UtcNow;
        try
        {
            var settings = Settings with { };
            var configuration = ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides);
            status("正在取得本次选区…");
            var captureStartedAt = DateTimeOffset.UtcNow;
            var material = await capture.CaptureAsync(settings, frozen);
            capturing = false;
            var draft = StartMaterial(material, new InputMetadata(frozen == null ? "selection-button" : "selection-dot", ReadRequested: read), settings, configuration, requestedAt);
            if (frozen != null) draft.Times["selectionObserved"] = frozen.CapturedAt;
            draft.Times["captureStarted"] = captureStartedAt;
            store.Save(draft);
        }
        catch (Exception ex) { status("取词未完成：" + ex.Message); }
        finally { capturing = false; }
    }

    internal Task<bool> StartTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
        try
        {
            var settings = Settings with { };
            var material = new CaptureMaterial(text, null, "generated_allowed", null, "文字框输入", 0, DateTimeOffset.UtcNow);
            StartMaterial(material, new InputMetadata("typed", "lookup", QuestionMarkRouting: true,
                ReadRequested: settings.TypedInputAutoRead), settings, ModeConfigurationLoader.Load(ModeDefaults, ModeOverrides), DateTimeOffset.UtcNow);
            return Task.FromResult(true);
        }
        catch (Exception ex) { status("输入未提交：" + ex.Message); return Task.FromResult(false); }
    }

    internal void OpenInput()
    {
        if (inputForm == null || inputForm.IsDisposed) inputForm = new LanguageInputForm(StartTextAsync);
        if (!inputForm.Visible) inputForm.Show(owner);
        inputForm.Activate();
    }

    // Every entry stops at this boundary: material + metadata, then the same draft and modules.
    private ClipDraft StartMaterial(CaptureMaterial material, InputMetadata input, LanguageSettings settings,
        ContentConfiguration configuration, DateTimeOffset requestedAt)
    {
        material.Validate();
        var draft = new ClipDraft { Original = material, Material = material, Input = input, Settings = settings,
            Configuration = configuration, Practice = settings.Practice };
        draft.Times["requested"] = requestedAt;
        draft.Times["captured"] = DateTimeOffset.UtcNow;
        latest = draft;
        // Do not wait for classification/content before opening the audio connection.
        if (input.ReadRequested && !(input.QuestionMarkRouting && ContentModule.IsQuestion(material.SelectedText))) _ = PlayAsync(draft, false);
        var form = OpenDraft(draft);
        draft.Times["previewOpened"] = DateTimeOffset.UtcNow;
        store.Save(draft);
        _ = ProcessAsync(draft, form);
        return draft;
    }

    private LanguageDraftForm OpenDraft(ClipDraft draft)
    {
        var existing = forms.FirstOrDefault(f => !f.IsDisposed && f.Draft.Id == draft.Id);
        if (existing != null) { existing.Show(); existing.Activate(); return existing; }
        var form = new LanguageDraftForm(draft, store,
            async () => { try { using var request = new HttpClient(); await new AnkiConnection(request, draft.Settings.AnkiEndpoint).ImportAsync(draft, store); } finally { Changed(draft, null); } },
            () => PlayAsync(draft, true), () => speech.Stop());
        forms.Add(form);
        form.RetryContent += () => _ = ProcessAsync(draft, form);
        form.SubmitFollowUp += text => FollowUpAsync(draft, form, text);
        form.PreviewFollowUpCard += turn => OpenFollowUpCard(draft, turn);
        form.FormClosed += (_, _) => { forms.Remove(form); store.Save(draft); };
        form.Show(owner);
        return form;
    }

    private async Task ProcessAsync(ClipDraft draft, LanguageDraftForm form)
    {
        if (form.ContentRunning) return;
        form.ContentRunning = true;
        try
        {
            draft.ContentError = null;
            var content = new ContentModule(http, () => ContentCredentials.Resolve(DesktopCredentialSettings.Load(dataRoot).DeepSeekApiKey));
            if (draft.ModeId == null)
            {
                try { draft.ModeId = await content.ClassifyAsync(draft.Material, draft.Configuration, draft.Settings, form.Lifetime, draft.Input); }
                catch (ModeChoiceRequiredException ex) { draft.ModeId = await form.ChooseModeAsync(ex.Message); }
                if (draft.ModeId == null) return;
                draft.Times["classified"] = DateTimeOffset.UtcNow;
            }
            draft.Route = ModeConfigurationLoader.Route(draft.Configuration, draft.ModeId);
            if (draft.Route.RequiresContext && draft.Material.NeedsContextChoice)
            {
                var material = await form.ResolveContextAsync();
                if (material == null) return;
                draft.Material = material; store.Save(draft);
            }
            draft.Content = await content.ProcessAsync(draft.Material, draft.ModeId, draft.Configuration, draft.Settings, form.Lifetime);
            if (draft.Content.Answer != null && draft.Conversation.Count == 0)
                draft.Conversation.Add(new ConversationTurn(draft.Material.SelectedText, draft.Content.Answer));
            draft.Times["previewReady"] = DateTimeOffset.UtcNow;
            if (!form.IsDisposed) form.ContentReady();
        }
        catch (OperationCanceledException) { draft.ContentError = "已停止内容处理，原材料已保留。"; }
        catch (Exception ex) { draft.ContentError = ex.Message; }
        finally { form.ContentRunning = false; Changed(draft, form); }
    }

    private async Task FollowUpAsync(ClipDraft draft, LanguageDraftForm form, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        draft.PendingFollowUp = text; draft.ConversationError = null; store.Save(draft);
        try
        {
            var content = new ContentModule(http, () => ContentCredentials.Resolve(DesktopCredentialSettings.Load(dataRoot).DeepSeekApiKey));
            var answer = await content.AnswerAsync(draft.ConversationMaterial ?? draft.Material, draft.ConversationContent ?? draft.PreviewContent, draft.Conversation.ToArray(), text,
                draft.Configuration, draft.Settings, form.Lifetime, currentPreview: new(draft.Original.SelectedText, draft.PreviewContent));
            draft.Conversation.Add(new ConversationTurn(text, answer));
            draft.PendingFollowUp = null;
            draft.Times["followUpAnswered"] = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { draft.ConversationError = "追问已停止，输入已保留。"; }
        catch (Exception ex) { draft.ConversationError = ex.Message; }
        finally { store.Save(draft); if (!form.IsDisposed) form.RefreshConversation(); }
    }

    private void OpenFollowUpCard(ClipDraft parent, ConversationTurn turn)
    {
        if (!turn.Answer.Answered) return;
        var index = parent.Conversation.IndexOf(turn);
        if (index < 0) return;
        if (turn.Question == parent.Original.SelectedText && turn.Answer.AnswerText == parent.Content?.Answer?.AnswerText)
        { OpenDraft(parent); return; }
        if (parent.FollowUpCards.TryGetValue(index, out var previous) && store.Find(previous) is { } existing)
        { OpenDraft(LiveDraft(existing)); return; }
        var mode = parent.Configuration.Modes.Single(m => m.Definition.OutputKind == "question");
        var material = new CaptureMaterial(turn.Question, null, "generated_allowed", null, parent.Original.SourceTitle, 0, DateTimeOffset.UtcNow);
        var draft = new ClipDraft { Original = material, Material = material, Input = parent.Input with { Intent = "question", ReadRequested = false },
            Settings = parent.Settings, Configuration = parent.Configuration, ParentDraftId = parent.Id,
            ConversationMaterial = parent.ConversationMaterial ?? parent.Material, ConversationContent = parent.ConversationContent ?? parent.PreviewContent,
            ModeId = mode.Definition.Id, Route = ModeConfigurationLoader.Route(parent.Configuration, mode.Definition.Id),
            Content = new ContentResult(mode.Definition.Id, Basic: new(turn.Question, turn.Answer.AnswerText!), Answer: turn.Answer),
            Conversation = parent.Conversation.Take(index + 1).ToList() };
        draft.Times["previewReady"] = DateTimeOffset.UtcNow;
        latest = draft; store.Save(draft);
        parent.FollowUpCards[index] = draft.Id; store.Save(parent);
        OpenDraft(draft);
    }

    private void Changed(ClipDraft draft, LanguageDraftForm form)
    {
        if (owner.IsDisposed) return;
        if (owner.InvokeRequired) { owner.BeginInvoke((Action)(() => Changed(draft, form))); return; }
        store.Save(draft);
        if (history != null && !history.IsDisposed) history.RefreshDraft(draft);
        foreach (var view in forms.Where(f => f.Draft.Id == draft.Id).ToArray()) if (!view.IsDisposed) view.RefreshStatus();
        if (ReferenceEquals(latest, draft))
            status(draft.ImportState == ImportState.Verified ? "已写入 Anki · " + draft.CardIds.Length + " 张卡" :
                draft.Content != null ? "预览已就绪 · " + draft.SpeechStatus : draft.ContentError != null ? "内容待重试 · " + draft.SpeechStatus : "内容处理中 · " + draft.SpeechStatus);
    }

    internal void Stop() => speech.Stop();
    private ClipDraft LiveDraft(ClipDraft draft) => forms.FirstOrDefault(f => !f.IsDisposed && f.Draft.Id == draft.Id)?.Draft
        ?? (latest?.Id == draft.Id ? latest : lastSpeech?.Id == draft.Id ? lastSpeech : draft);

    private Task PlayAsync(ClipDraft draft, bool replay)
    {
        draft = LiveDraft(draft);
        latest = lastSpeech = draft;
        draft.Times["speechRequested"] = DateTimeOffset.UtcNow;
        store.Save(draft);
        return speech.SpeakAsync(draft, replay, () => Changed(draft, null));
    }

    internal void ReplayAndShowRecent()
    {
        var target = lastSpeech ?? store.RecentSpeech().FirstOrDefault();
        if (target != null) _ = PlayAsync(target, true);
        ShowRecent(true);
    }

    internal void ShowRecent(bool speechOnly = false)
    {
        var recent = (speechOnly ? store.RecentSpeech() : store.Recent()).Select(LiveDraft).ToArray();
        if (history == null || history.IsDisposed)
        {
            history = new LanguageReadingHistoryForm(draft => _ = PlayAsync(draft, true), draft => {
                var form = OpenDraft(LiveDraft(draft)); latest = form.Draft;
                if (form.Draft.Content != null) form.ContentReady();
                else _ = ProcessAsync(form.Draft, form);
            }, () => speech.Stop());
            history.FormClosed += (_, _) => history = null;
        }
        history.SetDrafts(recent, speechOnly);
        if (!history.Visible) history.Show(owner);
        history.Activate();
    }

    public void Dispose() { inputForm?.Close(); history?.Close(); foreach (var f in forms.ToArray()) f.Close(); speech.Dispose(); http.Dispose(); }
}

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
    private ClipDraft latest;
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
            var draft = new ClipDraft { Original = material, Material = material, Settings = settings,
                Configuration = configuration, Practice = settings.Practice };
            draft.Times["requested"] = requestedAt;
            draft.Times["captureStarted"] = captureStartedAt;
            draft.Times["captured"] = DateTimeOffset.UtcNow;
            latest = draft;
            // Start the audio connection before constructing/showing the preview on the UI thread.
            if (read) _ = speech.SpeakAsync(draft, false, () => Changed(draft, null));
            var form = OpenDraft(draft);
            draft.Times["previewOpened"] = DateTimeOffset.UtcNow;
            store.Save(draft);
            _ = ProcessAsync(draft, form);
        }
        catch (Exception ex) { status("取词未完成：" + ex.Message); }
        finally { capturing = false; }
    }

    private LanguageDraftForm OpenDraft(ClipDraft draft)
    {
        var existing = forms.FirstOrDefault(f => !f.IsDisposed && f.Draft.Id == draft.Id);
        if (existing != null) { existing.Show(); existing.Activate(); return existing; }
        var form = new LanguageDraftForm(draft, store,
            async () => { try { using var request = new HttpClient(); await new AnkiConnection(request, draft.Settings.AnkiEndpoint).ImportAsync(draft, store); } finally { Changed(draft, null); } },
            () => speech.SpeakAsync(draft, true, () => Changed(draft, null)), () => speech.Stop());
        forms.Add(form);
        form.RetryContent += () => _ = ProcessAsync(draft, form);
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
            if (draft.Material.NeedsContextChoice)
            {
                var material = await form.ResolveContextAsync();
                if (material == null) return;
                draft.Material = material; store.Save(draft);
            }
            var content = new ContentModule(http, () => ContentCredentials.Resolve(DesktopCredentialSettings.Load(dataRoot).DeepSeekApiKey));
            if (draft.ModeId == null)
            {
                try { draft.ModeId = await content.ClassifyAsync(draft.Material, draft.Configuration, draft.Settings, form.Lifetime); }
                catch (ModeChoiceRequiredException ex) { draft.ModeId = await form.ChooseModeAsync(ex.Message); }
                if (draft.ModeId == null) return;
                draft.Times["classified"] = DateTimeOffset.UtcNow;
            }
            draft.Content = await content.ProcessAsync(draft.Material, draft.ModeId, draft.Configuration, draft.Settings, form.Lifetime);
            draft.Times["previewReady"] = DateTimeOffset.UtcNow;
            if (!form.IsDisposed) form.ContentReady();
        }
        catch (OperationCanceledException) { draft.ContentError = "已停止内容处理，原材料已保留。"; }
        catch (Exception ex) { draft.ContentError = ex.Message; }
        finally { form.ContentRunning = false; Changed(draft, form); }
    }

    private void Changed(ClipDraft draft, LanguageDraftForm form)
    {
        if (owner.IsDisposed) return;
        if (owner.InvokeRequired) { owner.BeginInvoke((Action)(() => Changed(draft, form))); return; }
        store.Save(draft);
        foreach (var view in forms.Where(f => f.Draft.Id == draft.Id).ToArray()) if (!view.IsDisposed) view.RefreshStatus();
        if (ReferenceEquals(latest, draft))
            status(draft.ImportState == ImportState.Verified ? "已写入 Anki · " + draft.CardIds.Length + " 张卡" :
                draft.Content != null ? "预览已就绪 · " + draft.SpeechStatus : draft.ContentError != null ? "内容待重试 · " + draft.SpeechStatus : "内容处理中 · " + draft.SpeechStatus);
    }

    internal void Stop() => speech.Stop();
    internal void Replay() { var target = latest; if (target != null) _ = speech.SpeakAsync(target, true, () => Changed(target, null)); }
    internal void ShowRecent()
    {
        var recent = store.Recent().ToArray();
        using var dialog = new Form { Text = "最近的语言草稿", Width = 680, Height = 420, StartPosition = FormStartPosition.CenterParent, Font = new Font("微软雅黑", 10) };
        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (var draft in recent) list.Items.Add(draft.Original.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm") + "  " + draft.Original.SelectedText.Replace("\r", " ").Replace("\n", " ").TakeText(45) + "  · " + (draft.ImportState == ImportState.Verified ? "已导入" : "草稿"));
        list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) { var f = OpenDraft(recent[list.SelectedIndex]); latest = f.Draft; if (f.Draft.Content != null) f.ContentReady(); dialog.Close(); } };
        dialog.Controls.Add(list); dialog.ShowDialog(owner);
    }

    public void Dispose() { foreach (var f in forms.ToArray()) f.Close(); speech.Dispose(); http.Dispose(); }
}

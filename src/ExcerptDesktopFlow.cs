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

internal sealed class ExcerptDesktopFlow : IDisposable
{
    private readonly Form owner;
    private readonly string dataRoot;
    private readonly Action<string> status;
    private readonly ExcerptStore store;
    private readonly LanguageDesktopCapture capture = new(Path.Combine(AppContext.BaseDirectory, "helpers", "selection.cjs"));
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<ExcerptDraftForm> forms = new();
    private readonly Dictionary<string, ExcerptSession> quickSessions = new();
    private LanguageSettings settings;
    private Form history;
    private bool capturing;
    private readonly CancellationTokenSource lifetime = new();
    internal event Action<ExcerptSession> SessionChanged;
    internal event Action<string> QuickFeedback;
    private void ReportQuickFeedback(string message)
    {
        if (!owner.IsDisposed && !lifetime.IsCancellationRequested) QuickFeedback?.Invoke(message);
    }

    internal ExcerptDesktopFlow(Form owner, string dataRoot, LanguageSettings settings, Action<string> status)
    { this.owner = owner; this.dataRoot = dataRoot; this.settings = settings; this.status = status; store = new(dataRoot); }

    internal void UpdateSettings(LanguageSettings value) => settings = value;
    internal async Task StartAsync(SelectionSnapshot selection = null, bool quick = false)
    {
        if (capturing) return;
        capturing = true;
        try
        {
            var snapshot = settings with { };
            status("正在取得摘录原文…");
            var material = await capture.CapturePlainTextAsync(snapshot, selection);
            capturing = false;
            var session = ExcerptCards.Create(material, snapshot, store);
            if (quick) _ = CompleteQuickAsync(session);
            else { Open(session); status("摘录原文已就绪，可直接导入或继续制卡"); }
        }
        catch (Exception ex)
        {
            status("摘录未完成：" + ex.Message);
            if (quick && selection != null && !owner.IsDisposed)
            {
                // No verified material exists yet: do not turn a stale UIA candidate
                // into an apparently valid draft when protected copying failed.
                ReportQuickFeedback("摘录失败：" + ex.Message + "\n请重新选择文字后摘录。");
            }
        }
        finally { capturing = false; }
    }
    internal Task<bool> StartTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
        try
        {
            Open(ExcerptCards.Create(new(text, null, "unavailable", null, "文字框输入", 0, DateTimeOffset.UtcNow), settings, store));
            status("摘录原文已就绪"); return Task.FromResult(true);
        }
        catch (Exception ex) { status("摘录未提交：" + ex.Message); return Task.FromResult(false); }
    }
    internal string StartMaterial(CaptureMaterial material, LanguageSettings snapshot, string imageId = null, bool quick = false)
    {
        material.Validate();
        material = ExcerptTextCleanup.Clean(material);
        var session = new ExcerptSession { Original = material, Settings = snapshot, ManualFront = material.SelectedText, ImageMaterialId = imageId };
        var fields = new ExcerptFields(material.FrontImage == null ? material.SelectedText : "", "")
        { FrontImages = material.FrontImage == null ? [] : [material.FrontImage] };
        ExcerptCards.Add(session, store, fields, material.FrontImage == null ? "原文" : "图片摘录", originalExcerpt: true);
        if (quick) _ = CompleteQuickAsync(session); else Open(session);
        return session.Id;
    }
    internal string StartImages(ImageMaterialJob job, ExcerptFields fields, ModeSnapshot mode, bool quick = false)
    {
        var original = new CaptureMaterial("正面图片 " + job.Questions.Length + " 张", null, "unavailable", null,
            "图片采集 " + job.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 0, job.CreatedAt);
        var session = new ExcerptSession { Original = original, Settings = job.Settings, ImageMaterialId = job.Id };
        var exportMode = mode == null ? new ModeSnapshot(ExportRouting.BuiltinModes.Single(m => m.Id == "image-original"), "", "") : mode with { Definition = mode.Definition with { OutputKind = "excerpt" } };
        ExcerptCards.Add(session, store, fields, job.Answers.Length > 0 ? "正面与反面图片" : mode?.Definition.Name ?? "原图", exportMode);
        if (quick) _ = CompleteQuickAsync(session); else Open(session);
        return session.Id;
    }
    private async Task CompleteQuickAsync(ExcerptSession session)
    {
        quickSessions[session.Id] = session;
        try
        {
            using var request = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            await FloatingQuickImport.ExcerptAsync(session, store, new CardImportRouter(request), lifetime.Token);
            status("快捷摘录已完成 · " + ExportRouting.ResultSummary(ExcerptCards.Get(session, store, session.Cards[0].Id)));
            ReportQuickFeedback("摘录成功 · " + ExportRouting.ResultSummary(ExcerptCards.Get(session, store, session.Cards[0].Id)));
        }
        catch (AnkiPendingException)
        {
            status("摘录已保存，等待 Anki 打开后自动导入。");
            ReportQuickFeedback("摘录已保存，等待 Anki 打开后导入");
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested || owner.IsDisposed) return;
            var card = ExcerptCards.Get(session, store, session.Cards[0].Id);
            card.ImportError ??= ex.Message; store.Cards.Save(card);
            status("快捷摘录需要继续确认：" + ex.Message);
            Open(session);
            ReportQuickFeedback("摘录失败：" + ex.Message);
        }
        finally
        {
            quickSessions.Remove(session.Id);
            if (!owner.IsDisposed) SessionChanged?.Invoke(session);
        }
    }
    internal void Resume(string id) => Open(store.Find(id) ?? throw new InvalidOperationException("找不到已保存的摘录。"));
    private ModeSnapshot[] Modes() => ExcerptModeLoader.Load(
        string.IsNullOrWhiteSpace(settings.ExcerptModesDirectory) ? Path.Combine(AppContext.BaseDirectory, "modes") : settings.ExcerptModesDirectory,
        Path.Combine(dataRoot, "modes"));
    internal void PendingImportChanged(ClipDraft draft)
    {
        foreach (var form in forms.ToArray())
            if (!form.IsDisposed && form.Session.Cards.Any(card => card.Id == draft.Id))
            { form.RefreshImportState(); SessionChanged?.Invoke(form.Session); }
        // The history listener treats this event as an invalidation signal.
        SessionChanged?.Invoke(null);
    }
    private void Open(ExcerptSession session)
    {
        // History can reopen while the background import owns the session gate.
        // Keep that live session so a manual retry queues behind the same import.
        if (quickSessions.TryGetValue(session.Id, out var active)) session = active;
        var existing = forms.FirstOrDefault(f => !f.IsDisposed && f.Session.Id == session.Id);
        if (existing != null) { existing.Show(); existing.Activate(); return; }
        var form = new ExcerptDraftForm(session, store, Modes, async (mode, ct) =>
        {
            var content = new ContentModule(http, () => ContentCredentials.Resolve());
            var result = await content.ProcessExcerptAsync(session.Original, mode, session.Settings, ct);
            ExcerptCards.Add(session, store, result, mode.Definition.Name, mode);
        }, async (ids, ct, changed) =>
        {
            using var request = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            await ExcerptCards.ImportAsync(session, store, new CardImportRouter(request), ids, ct, changed);
        });
        form.CardsChanged += changed => SessionChanged?.Invoke(changed);
        // The topmost capture bar must not make the card editor topmost too.
        forms.Add(form); form.FormClosed += (_, _) => forms.Remove(form); form.Show();
    }
    internal void ShowRecent()
    {
        if (history != null && !history.IsDisposed) { history.Show(); history.Activate(); return; }
        history = new Form { Text = "最近摘录", Width = 640, Height = 460, Font = new Font("微软雅黑", 10), StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill, DisplayMember = "Label" };
        list.Items.AddRange(store.Recent().Select(session => new HistoryItem(session,
            session.Original.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm") + "  " + session.Original.SelectedText.Replace("\n", " ").TakeText(45))).ToArray());
        var open = new Button { Text = "打开摘录", Dock = DockStyle.Bottom, Height = 38 };
        void Selected() { if (list.SelectedItem is HistoryItem item) Open(item.Session); }
        open.Click += (_, _) => Selected(); list.DoubleClick += (_, _) => Selected();
        history.Controls.Add(list); history.Controls.Add(open); history.Show(owner);
    }
    private sealed record HistoryItem(ExcerptSession Session, string Label);
    public void Dispose()
    { lifetime.Cancel(); foreach (var form in forms.ToArray()) form.Close(); history?.Close(); http.Dispose(); lifetime.Dispose(); }
}

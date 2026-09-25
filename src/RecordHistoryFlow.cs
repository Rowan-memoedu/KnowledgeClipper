using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class RecordHistoryFlow : IDisposable
{
    private readonly Form owner;
    private readonly LanguageDesktopFlow language;
    private readonly ExcerptDesktopFlow excerpt;
    private readonly DraftStore drafts;
    private readonly ExcerptStore excerpts;
    private RecordHistoryForm history;
    private readonly Timer refreshTimer = new() { Interval = 300 };
    private Dictionary<string, (string Status, string Action)> summaries = new();
    private bool loading, reloadPending, disposed;
    internal RecordHistoryFlow(Form owner, string dataRoot, LanguageDesktopFlow language, ExcerptDesktopFlow excerpt)
    {
        this.owner = owner; this.language = language; this.excerpt = excerpt;
        drafts = new(dataRoot); excerpts = new(dataRoot); language.DraftChanged += Changed; excerpt.SessionChanged += Changed;
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); Reload(); };
    }
    private void Changed(ClipDraft draft) => ScheduleReload();
    private void Changed(ExcerptSession session) => ScheduleReload();
    private void ScheduleReload()
    {
        if (disposed || history == null || history.IsDisposed) return;
        refreshTimer.Stop(); refreshTimer.Start();
    }
    private async void Reload()
    {
        if (disposed || history == null || history.IsDisposed) return;
        if (loading) { reloadPending = true; return; }
        loading = true;
        var target = history;
        try
        {
            // Disk reads and card-state expansion must not run on the WinForms message loop.
            // Resolve live drafts only when the user opens/plays a record, never for every row.
            var snapshot = await Task.Run(() =>
            {
                var records = HistoryRecords.Read(drafts, excerpts);
                var states = records.ToDictionary(record => record.Key,
                    record => (Status: Describe(record), Action: HistoryRecords.ActionText(record, drafts, excerpts)));
                return (records, states);
            });
            if (disposed || target.IsDisposed || !ReferenceEquals(history, target)) return;
            summaries = snapshot.states;
            target.SetRecords(snapshot.records);
        }
        catch (Exception ex)
        {
            if (!disposed && !target.IsDisposed) target.SetMessage("历史记录读取失败，可点击刷新重试：" + ex.Message);
        }
        finally
        {
            loading = false;
            if (reloadPending) { reloadPending = false; ScheduleReload(); }
        }
    }
    private string Describe(HistoryRecord record)
    {
        if (record.Draft != null) return language.DescribeStatus(record.Draft);
        var cards = record.Excerpt.Cards.Where(c => c.SupersededBy == null).Select(c => excerpts.Cards.Find(c.Id)).ToArray();
        return "共 " + cards.Length + " 张 · 已导入 " + cards.Count(c => c?.ImportState == ImportState.Verified) + " 张" +
            (cards.Any(c => c == null || c.ImportError != null || c.ImportState == ImportState.Unknown) ? " · 部分内容待核对" : "");
    }
    internal void Show()
    {
        if (history == null || history.IsDisposed)
        {
            history = new RecordHistoryForm(record => { if (record.CanPlay) language.PlayHistory(record.Draft); },
                record => { if (record.Excerpt != null) excerpt.Resume(record.Excerpt.Id); else language.ContinueHistory(record.Draft); },
                language.Stop, Reload, record => summaries.TryGetValue(record.Key, out var row) ? row.Status : "",
                record => summaries.TryGetValue(record.Key, out var row) ? row.Action : "继续制卡");
            history.FormClosed += (_, _) => history = null;
        }
        if (!history.Visible) history.Show(owner); history.Activate(); Reload();
    }
    public void Dispose() { disposed = true; refreshTimer.Dispose(); language.DraftChanged -= Changed; excerpt.SessionChanged -= Changed; history?.Close(); }
}

// A second native click cancels the pending single action before it can start audio.
internal sealed class RecordHistoryButton : Button
{
    private readonly Timer clickTimer = new();
    private bool suppressClick;
    private bool separateHistoryClick;
    internal bool SeparateHistoryClick
    {
        get => separateHistoryClick;
        set { separateHistoryClick = value; SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, value); if (!value) clickTimer.Stop(); }
    }
    internal RecordHistoryButton()
    {
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); if (!IsDisposed && Enabled && Visible) base.OnClick(EventArgs.Empty); };
    }
    protected override void OnClick(EventArgs e)
    {
        if (!SeparateHistoryClick) { base.OnClick(e); return; }
        if (suppressClick) return;
        clickTimer.Stop(); clickTimer.Interval = SystemInformation.DoubleClickTime; clickTimer.Start();
    }
    protected override void OnDoubleClick(EventArgs e)
    {
        clickTimer.Stop();
        if (SeparateHistoryClick)
        {
            suppressClick = true; base.OnDoubleClick(e);
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => suppressClick = false));
            else suppressClick = false;
        }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (SeparateHistoryClick && e.Button == MouseButtons.Left && e.Clicks > 1) clickTimer.Stop();
        base.OnMouseDown(e);
    }
    protected override void OnVisibleChanged(EventArgs e) { if (!Visible) clickTimer.Stop(); base.OnVisibleChanged(e); }
    protected override void Dispose(bool disposing) { if (disposing) clickTimer.Dispose(); base.Dispose(disposing); }
}

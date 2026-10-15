using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class RecordHistoryForm : Form
{
    private readonly Action<HistoryRecord> play, continueCards;
    private readonly Func<HistoryRecord, string> describe, actionText;
    private readonly FlowLayoutPanel rows;
    private readonly Dictionary<string, (Label Status, Button Continue, Button Play)> items = new();
    private IReadOnlyList<HistoryRecord> records = Array.Empty<HistoryRecord>();
    private readonly Dictionary<string, HistoryRecord> current = new();
    private const int PageSize = 20;
    private int page;
    private readonly Label pageLabel;
    private readonly Button previous, next;
    private bool fittingRows;

    internal RecordHistoryForm(Action<HistoryRecord> play, Action<HistoryRecord> continueCards, Action stop, Action refresh, Func<HistoryRecord, string> describe, Func<HistoryRecord, string> actionText)
    {
        this.play = play; this.continueCards = continueCards; this.describe = describe; this.actionText = actionText;
        Name = AccessibleName = "knowledge-clipper.history";
        Text = "历史记录"; Font = new Font("微软雅黑", 10); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 480); MinimumSize = new Size(620, 480);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "语言记录可点击播放；所有记录均可查看卡片或继续制卡。", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(4, 4, 4, 10) }, 0, 0);
        rows = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        rows.SizeChanged += (_, _) => FitRows(); layout.Controls.Add(rows, 0, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var stopButton = new Button { Text = "停止朗读", AutoSize = true, MinimumSize = new Size(100, 34), Name = "knowledge-clipper.history.stop" };
        var refreshButton = new Button { Text = "刷新", AutoSize = true, MinimumSize = new Size(80, 34), Name = "knowledge-clipper.history.refresh" };
        stopButton.Click += (_, _) => stop(); refreshButton.Click += (_, _) => refresh();
        previous = new Button { Text = "上一页", AutoSize = true, Name = "knowledge-clipper.history.previous" };
        next = new Button { Text = "下一页", AutoSize = true, Name = "knowledge-clipper.history.next" };
        pageLabel = new Label { AutoSize = true, Text = "正在读取历史记录…", Margin = new Padding(6, 8, 6, 0) };
        previous.Click += (_, _) => { if (page > 0) { page--; RenderPage(); } };
        next.Click += (_, _) => { if ((page + 1) * PageSize < records.Count) { page++; RenderPage(); } };
        actions.Controls.Add(stopButton); actions.Controls.Add(refreshButton);
        actions.Controls.Add(previous); actions.Controls.Add(next); actions.Controls.Add(pageLabel); layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
    }

    internal void SetRecords(IReadOnlyList<HistoryRecord> records)
    {
        this.records = records;
        current.Clear(); foreach (var record in records) current[record.Key] = record;
        page = Math.Min(page, Math.Max(0, (records.Count - 1) / PageSize));
        var visible = records.Skip(page * PageSize).Take(PageSize).ToArray();
        if (rows.Controls.Count > 0 && items.Keys.SequenceEqual(visible.Select(record => record.Key)))
        {
            foreach (var record in visible) RefreshRecord(record);
            UpdatePaging();
        }
        else RenderPage();
    }
    internal void SetMessage(string message) => pageLabel.Text = message;
    private void UpdatePaging()
    {
        previous.Enabled = page > 0; next.Enabled = (page + 1) * PageSize < records.Count;
        pageLabel.Text = $"共 {records.Count} 条 · {page + 1}/{Math.Max(1, (records.Count + PageSize - 1) / PageSize)} 页";
    }
    private void RenderPage()
    {
        rows.SuspendLayout();
        foreach (Control control in new List<Control>(GetRows())) control.Dispose();
        rows.Controls.Clear(); items.Clear();
        foreach (var record in records.Skip(page * PageSize).Take(PageSize))
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(6), BackColor = Color.FromArgb(246, 248, 250) };
            // Build and size each row before its first layout. AutoSize otherwise measures
            // every intermediate label/button insertion and propagates it up the page.
            row.SuspendLayout();
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var target = new Label { Text = record.Original.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm") + "  [" + record.Kind + "] " + record.Original.SelectedText.Replace("\r", " ").Replace("\n", " ").TakeText(70), AutoSize = true, Dock = DockStyle.Fill, Cursor = Cursors.Hand, Name = "knowledge-clipper.history.record." + record.Key };
            target.Click += (_, _) => { var latest = current[record.Key]; if (latest.CanPlay) play(latest); else continueCards(latest); };
            var state = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };
            var playButton = new Button { Text = "▶ 播放", AutoSize = true, Name = "knowledge-clipper.history.play." + record.Key };
            var continueButton = new Button { AutoSize = true, Name = "knowledge-clipper.history.continue." + record.Key };
            playButton.Click += (_, _) => { var latest = current[record.Key]; if (latest.CanPlay) play(latest); }; continueButton.Click += (_, _) => continueCards(current[record.Key]);
            row.Controls.Add(target, 0, 0); row.Controls.Add(state, 0, 1);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            actions.SuspendLayout();
            actions.Controls.Add(playButton); actions.Controls.Add(continueButton); row.Controls.Add(actions, 0, 2);
            items.Add(record.Key, (state, continueButton, playButton)); RefreshRecord(record);
            FitRow(row); actions.ResumeLayout(false); row.ResumeLayout(false); rows.Controls.Add(row);
        }
        if (records.Count == 0) rows.Controls.Add(new Label { AutoSize = true, Text = "还没有历史记录。可以摘录、学习语言或提问。" });
        UpdatePaging(); FitRows(); rows.ResumeLayout(true);
    }

    private IEnumerable<Control> GetRows() { foreach (Control control in rows.Controls) yield return control; }
    private void FitRows()
    {
        if (fittingRows) return;
        fittingRows = true;
        try
        {
            rows.SuspendLayout();
            foreach (Control control in rows.Controls) FitRow(control);
        }
        finally { rows.ResumeLayout(true); fittingRows = false; }
    }
    private void FitRow(Control control)
    {
        var width = Math.Max(200, rows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        control.SuspendLayout();
        try
        {
            var maximum = new Size(width, 0);
            if (control.MaximumSize != maximum) control.MaximumSize = maximum;
            if (control.Width != width) control.Width = width;
            if (control is not TableLayoutPanel row) return;
            var textWidth = Math.Max(80, width - row.Padding.Horizontal - 12);
            var textMaximum = new Size(textWidth, 0);
            for (var i = 0; i < 3; i++)
                if (row.GetControlFromPosition(0, i) is { } child && child.MaximumSize != textMaximum) child.MaximumSize = textMaximum;
        }
        finally { control.ResumeLayout(true); }
    }
    internal void RefreshDraft(ClipDraft draft) => RefreshRecord(new HistoryRecord(Draft: draft));
    internal void RefreshExcerpt(ExcerptSession session) => RefreshRecord(new HistoryRecord(Excerpt: session));
    private void RefreshRecord(HistoryRecord record)
    {
        current[record.Key] = record;
        if (!items.TryGetValue(record.Key, out var item)) return;
        item.Play.Visible = item.Play.Enabled = record.CanPlay;
        item.Status.Text = describe(record);
        item.Continue.Text = actionText(record);
    }
}

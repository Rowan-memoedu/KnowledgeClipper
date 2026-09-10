using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class LanguageReadingHistoryForm : Form
{
    private readonly Action<ClipDraft> play, continueCards;
    private readonly FlowLayoutPanel rows;
    private readonly Dictionary<string, (Label Status, Button Continue)> items = new();

    internal LanguageReadingHistoryForm(Action<ClipDraft> play, Action<ClipDraft> continueCards, Action stop)
    {
        this.play = play; this.continueCards = continueCards;
        Name = AccessibleName = "knowledge-clipper.language.history";
        Text = "最近朗读"; Font = new Font("微软雅黑", 10); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 400); MinimumSize = new Size(620, 360);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "点击文字或“播放”重听；“继续制卡”打开该条预览。", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(4, 4, 4, 10) }, 0, 0);
        rows = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        rows.SizeChanged += (_, _) => FitRows(); layout.Controls.Add(rows, 0, 1);
        var stopButton = new Button { Text = "停止朗读", AutoSize = true, MinimumSize = new Size(100, 34), Name = "knowledge-clipper.language.history.stop" };
        stopButton.Click += (_, _) => stop(); layout.Controls.Add(stopButton, 0, 2);
        Controls.Add(layout);
    }

    internal void SetDrafts(IReadOnlyList<ClipDraft> drafts, bool speechOnly)
    {
        Text = speechOnly ? "最近朗读" : "最近草稿与朗读";
        rows.SuspendLayout();
        foreach (Control control in new List<Control>(GetRows())) control.Dispose();
        rows.Controls.Clear(); items.Clear();
        foreach (var draft in drafts)
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(6), BackColor = Color.FromArgb(246, 248, 250) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var target = new Label { Text = (draft.LastSpeechAt ?? draft.Original.CapturedAt).ToLocalTime().ToString("MM-dd HH:mm") + "  " + draft.Original.SelectedText.Replace("\r", " ").Replace("\n", " ").TakeText(70), AutoSize = true, Dock = DockStyle.Fill, Cursor = Cursors.Hand };
            target.Click += (_, _) => play(draft);
            var state = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };
            var playButton = new Button { Text = "▶ 播放", AutoSize = true, Name = "knowledge-clipper.language.history.play." + draft.Id };
            var continueButton = new Button { AutoSize = true, Name = "knowledge-clipper.language.history.continue." + draft.Id };
            playButton.Click += (_, _) => play(draft); continueButton.Click += (_, _) => continueCards(draft);
            row.Controls.Add(target, 0, 0); row.Controls.Add(state, 0, 1);
            row.Controls.Add(playButton, 1, 0); row.SetRowSpan(playButton, 2);
            row.Controls.Add(continueButton, 2, 0); row.SetRowSpan(continueButton, 2);
            items.Add(draft.Id, (state, continueButton)); rows.Controls.Add(row); RefreshDraft(draft);
        }
        if (drafts.Count == 0) rows.Controls.Add(new Label { AutoSize = true, Text = "还没有朗读记录。选中文字后点击悬浮窗“朗读”。" });
        FitRows(); rows.ResumeLayout(true);
    }

    private IEnumerable<Control> GetRows() { foreach (Control control in rows.Controls) yield return control; }
    private void FitRows()
    {
        var width = Math.Max(200, rows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (Control control in rows.Controls)
        {
            control.MaximumSize = new Size(width, 0); control.Width = width;
            if (control is not TableLayoutPanel row) continue;
            var actions = row.GetControlFromPosition(1, 0).GetPreferredSize(Size.Empty).Width + row.GetControlFromPosition(2, 0).GetPreferredSize(Size.Empty).Width;
            var textWidth = Math.Max(80, width - actions - row.Padding.Horizontal - 30);
            row.GetControlFromPosition(0, 0).MaximumSize = row.GetControlFromPosition(0, 1).MaximumSize = new Size(textWidth, 0);
        }
    }
    internal void RefreshDraft(ClipDraft draft)
    {
        if (!items.TryGetValue(draft.Id, out var item)) return;
        item.Status.Text = draft.SpeechStatus + " · " + (draft.ImportState == ImportState.Verified ? "已制卡" : draft.Content != null ? "预览已就绪" : "草稿");
        item.Continue.Text = draft.ImportState == ImportState.Verified ? "查看卡片" : "继续制卡";
    }
}

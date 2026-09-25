using System;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class ExcerptDestinationForm : Form
{
    private readonly LanguageDesktopFlow flow;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox choices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
        Name = "knowledge-clipper.excerpt.destination-choice", DisplayMember = nameof(Choice.Label) };
    private readonly Label notice = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray };
    private readonly Button refresh = new() { Text = "刷新 RemNote 目录", AutoSize = true,
        Name = "knowledge-clipper.excerpt.destination-refresh" };
    private readonly Button apply = new() { Text = "切换目标", AutoSize = true,
        Name = "knowledge-clipper.excerpt.destination-apply" };
    private sealed record Choice(string Label, string Id, string Title);

    internal ExcerptDestinationForm(LanguageDesktopFlow flow)
    {
        this.flow = flow;
        Text = "原文摘录目标"; Name = AccessibleName = "knowledge-clipper.excerpt.destination-panel";
        Width = 510; Height = 245; MinimumSize = new Size(420, 220); Font = new Font("微软雅黑", 10);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "仅原文摘录使用此目标；摘录制卡继续按日期导入。", AutoSize = true }, 0, 0);
        layout.Controls.Add(choices, 0, 1); layout.Controls.Add(notice, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(apply); actions.Controls.Add(refresh); layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        refresh.Click += async (_, _) => await RefreshAsync();
        apply.Click += async (_, _) => await ApplyAsync();
        Shown += async (_, _) => await RefreshAsync();
        FormClosed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }

    private async Task<ExcerptDocumentList> ReadAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return await new RemNoteConnection(http, flow.Settings.RemNoteSdkEndpoint)
            .ListExcerptDocumentsAsync(flow.Settings.RemNoteRootId, lifetime.Token);
    }

    private async Task RefreshAsync()
    {
        refresh.Enabled = apply.Enabled = false;
        notice.Text = "正在读取 RemNote 的摘录子 Document…";
        try
        {
            var list = await ReadAsync();
            if (IsDisposed) return;
            var selected = flow.Settings.OriginalExcerptDocumentId;
            choices.Items.Clear();
            choices.Items.Add(new Choice("按日期导入（默认）", null, null));
            foreach (var item in list.Documents.OrderBy(d => d.Title, StringComparer.CurrentCulture))
                choices.Items.Add(new Choice(item.Title, item.Id, item.Title));
            choices.SelectedItem = choices.Items.Cast<Choice>().FirstOrDefault(item => item.Id == selected) ?? choices.Items[0];
            notice.Text = $"已读取 {list.Documents.Length} 个子 Document。当前：" +
                (selected == null ? "按日期" : flow.Settings.OriginalExcerptDocumentTitle +
                 (choices.SelectedIndex == 0 ? "（已不在当前目录，请重新选择）" : ""));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) notice.Text = "读取失败，保留原设置：" + ex.Message; }
        finally { if (!IsDisposed) refresh.Enabled = apply.Enabled = true; }
    }

    private async Task ApplyAsync()
    {
        if (choices.SelectedItem is not Choice choice) return;
        refresh.Enabled = apply.Enabled = false;
        try
        {
            if (choice.Id != null)
            {
                var current = await ReadAsync();
                if (!current.Documents.Any(item => item.Id == choice.Id && item.Title == choice.Title))
                    throw new InvalidOperationException("所选 Document 已变化，请刷新后重选。");
                if (flow.Settings.RemNoteRootId != null && flow.Settings.RemNoteRootId != current.RootId)
                    throw new InvalidOperationException("当前 RemNote 根目录与设置不一致。");
            }
            flow.SaveSettings(flow.Settings with
            { OriginalExcerptDocumentId = choice.Id, OriginalExcerptDocumentTitle = choice.Title });
            DialogResult = DialogResult.OK; Close();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) notice.Text = "未切换：" + ex.Message; }
        finally { if (!IsDisposed) refresh.Enabled = apply.Enabled = true; }
    }
}

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KnowledgeClipper;

internal sealed class LanguageInputForm : Form
{
    internal LanguageInputForm(Func<string, Task<bool>> submit)
    {
        Text = "提问"; Name = AccessibleName = "knowledge-clipper.text-input";
        Font = new Font("微软雅黑", 10); AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(620, 390); MinimumSize = new Size(420, 300); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "直接输入问题，无需在末尾添加问号。\nEnter 换行，Ctrl+Enter 提交。" }, 0, 0);
        var input = new TextBox { Multiline = true, MaxLength = 0, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Name = "knowledge-clipper.text-input.text", AccessibleName = "knowledge-clipper.text-input.text" };
        var send = new Button { Text = "提交", AutoSize = true, MinimumSize = new Size(100, 36), Name = "knowledge-clipper.text-input.submit", AccessibleName = "knowledge-clipper.text-input.submit" };
        async Task Send()
        {
            if (!send.Enabled || string.IsNullOrWhiteSpace(input.Text)) return;
            send.Enabled = input.Enabled = false;
            try { if (await submit(input.Text)) input.Clear(); }
            finally { if (!IsDisposed) send.Enabled = input.Enabled = true; }
        }
        send.Click += async (_, _) => await Send();
        input.KeyDown += async (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Send(); } };
        layout.Controls.Add(input, 0, 1); layout.Controls.Add(send, 0, 2); Controls.Add(layout);
        Shown += (_, _) => input.Focus();
    }
}

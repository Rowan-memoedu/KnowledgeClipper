using System;
using System.Drawing;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper
{
    internal sealed class CardResolutionForm : Form
    {
        private readonly ListBox occurrences = new() { Dock = DockStyle.Fill };
        private readonly TextBox correction = new() { Dock = DockStyle.Fill };
        internal CardResolutionSubmission Selection { get; private set; }

        internal CardResolutionForm(string operationId, CardResolutionPrompt prompt)
        {
            Text = "选择制卡目标"; Name = "knowledge-clipper.desktop.card-resolution.window"; AccessibleName = Name;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(560, 360); AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("微软雅黑", 9F); MinimizeBox = false; MaximizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { AutoSize = true, Text = "需要确认目标位置：" + prompt.ReasonCode }, 0, 0);
            foreach (var item in prompt.CandidateOccurrences) occurrences.Items.Add(new OccurrenceItem(item));
            occurrences.Name = "knowledge-clipper.desktop.card-resolution.occurrences"; occurrences.AccessibleName = occurrences.Name; root.Controls.Add(occurrences, 0, 1);
            root.Controls.Add(new Label { AutoSize = true, Text = "或输入修正后的目标文字" }, 0, 2);
            correction.Name = "knowledge-clipper.desktop.card-resolution.correction"; correction.AccessibleName = correction.Name; root.Controls.Add(correction, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            buttons.Controls.Add(MakeButton("取消", "knowledge-clipper.desktop.card-resolution.cancel", (_, _) => { Selection = new CardResolutionSubmission(operationId, "cancel", null, null); DialogResult = DialogResult.Cancel; Close(); }));
            buttons.Controls.Add(MakeButton("使用修正", "knowledge-clipper.desktop.card-resolution.correct", (_, _) => { if (string.IsNullOrWhiteSpace(correction.Text)) return; Selection = new CardResolutionSubmission(operationId, "user_correction", correction.Text, null); DialogResult = DialogResult.OK; Close(); }));
            buttons.Controls.Add(MakeButton("使用所选位置", "knowledge-clipper.desktop.card-resolution.accept", (_, _) => { if (occurrences.SelectedItem is not OccurrenceItem item) return; Selection = new CardResolutionSubmission(operationId, "accept_candidate", item.Value.ExactText, item.Value); DialogResult = DialogResult.OK; Close(); }));
            root.Controls.Add(buttons, 0, 4); Controls.Add(root);
        }
        private static Button MakeButton(string text, string id, EventHandler action) { var button = new Button { Text = text, Name = id, AccessibleName = id, AutoSize = true }; button.Click += action; return button; }
        private sealed class OccurrenceItem { internal OccurrenceItem(CardResolutionOccurrence value) { Value = value; } internal CardResolutionOccurrence Value { get; } public override string ToString() => $"第 {Value.Order + 1} 段 · {Value.Start}-{Value.End} · {Value.ExactText}"; }
    }
}

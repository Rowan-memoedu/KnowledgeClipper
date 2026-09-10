using System;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper
{
    internal static class FlashcardPreviewAutomation
    {
        internal const string Window = "knowledge-clipper.desktop.flashcard-preview.window";
        internal const string Status = "knowledge-clipper.desktop.flashcard-preview.status";
        internal const string Cards = "knowledge-clipper.desktop.flashcard-preview.cards";
        internal const string Front = "knowledge-clipper.desktop.flashcard-preview.front";
        internal const string Back = "knowledge-clipper.desktop.flashcard-preview.back";
        internal const string Destinations = "knowledge-clipper.desktop.flashcard-preview.destinations";
        internal const string DestinationResults = "knowledge-clipper.desktop.flashcard-preview.destination-results";
        internal const string Confirm = "knowledge-clipper.desktop.flashcard-preview.confirm";
        internal const string Cancel = "knowledge-clipper.desktop.flashcard-preview.cancel";
        internal const string FollowUpInput = "knowledge-clipper.desktop.flashcard-preview.follow-up-input";
        internal const string FollowUp = "knowledge-clipper.desktop.flashcard-preview.follow-up";
    }

    internal sealed class FlashcardPreviewForm : Form
    {
        private readonly Func<Task> confirm;
        private readonly Func<Task> cancel;
        private readonly Func<string, Task> followUp;
        private readonly Label status;
        private readonly Label destinations;
        private readonly TextBox destinationResults;
        private readonly ListBox cards;
        private readonly TextBox front;
        private readonly TextBox back;
        private readonly TextBox followUpInput;
        private readonly Button confirmButton;
        private readonly Button cancelButton;
        private readonly Button followUpButton;
        private FlashcardDraftPreviewState current;
        private string renderedContentKey;
        private long renderGeneration;
        private bool followUpInFlight;
        internal FlashcardDraftRevision RenderedDraft => current?.Draft;

        internal FlashcardPreviewForm(Func<Task> confirm, Func<Task> cancel, Func<string, Task> followUp)
        {
            this.confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
            this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
            this.followUp = followUp ?? throw new ArgumentNullException(nameof(followUp));
            Text = "卡片预览";
            Name = FlashcardPreviewAutomation.Window;
            AccessibleName = FlashcardPreviewAutomation.Window;
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(720, 520);
            ClientSize = new Size(820, 600);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            status = LabelFor(FlashcardPreviewAutomation.Status, "正在等待卡片草稿…", Color.FromArgb(55, 71, 92));
            destinations = LabelFor(FlashcardPreviewAutomation.Destinations, "目标：尚未确定", Color.FromArgb(92, 105, 121));
            destinationResults = new TextBox { Name = FlashcardPreviewAutomation.DestinationResults, AccessibleName = FlashcardPreviewAutomation.DestinationResults,
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Height = 70,
                BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Control, ForeColor = Color.FromArgb(92, 105, 121) };
            root.Controls.Add(status, 0, 0);
            var destinationPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            destinationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            destinationPanel.Controls.Add(destinations, 0, 0);
            destinationPanel.Controls.Add(destinationResults, 0, 1);
            root.Controls.Add(destinationPanel, 0, 1);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 220, FixedPanel = FixedPanel.Panel1 };
            cards = new ListBox { Dock = DockStyle.Fill, Name = FlashcardPreviewAutomation.Cards, AccessibleName = FlashcardPreviewAutomation.Cards, IntegralHeight = false };
            cards.QueryAccessibilityHelp += (_, args) => args.HelpString = cards.AccessibleDescription;
            cards.SelectedIndexChanged += delegate { RenderSelectedCard(); };
            split.Panel1.Padding = new Padding(0, 8, 8, 8);
            split.Panel1.Controls.Add(cards);
            var faces = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 4 };
            faces.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            faces.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            faces.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            faces.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            faces.Controls.Add(new Label { Text = "正面 Front", AutoSize = true, ForeColor = Color.FromArgb(55, 71, 92) }, 0, 0);
            front = FaceBox(FlashcardPreviewAutomation.Front);
            faces.Controls.Add(front, 0, 1);
            faces.Controls.Add(new Label { Text = "背面 Back", AutoSize = true, ForeColor = Color.FromArgb(55, 71, 92) }, 0, 2);
            back = FaceBox(FlashcardPreviewAutomation.Back);
            faces.Controls.Add(back, 0, 3);
            split.Panel2.Controls.Add(faces);
            root.Controls.Add(split, 0, 2);

            var followRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            followRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            followRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            followUpInput = new TextBox { Dock = DockStyle.Fill, Name = FlashcardPreviewAutomation.FollowUpInput, AccessibleName = FlashcardPreviewAutomation.FollowUpInput };
            followUpInput.TextChanged += delegate { followUpButton.Enabled = !followUpInFlight && current != null && current.CanFollowUp && !string.IsNullOrWhiteSpace(followUpInput.Text); };
            followUpInput.KeyDown += async (_, eventArgs) =>
            {
                if (eventArgs.KeyCode != Keys.Enter || eventArgs.Modifiers != Keys.None) return;
                eventArgs.SuppressKeyPress = true;
                await FollowUpAsync();
            };
            followUpButton = ButtonFor("追问", FlashcardPreviewAutomation.FollowUp);
            followUpButton.Click += async delegate { await FollowUpAsync(); };
            followRow.Controls.Add(followUpInput, 0, 0);
            followRow.Controls.Add(followUpButton, 1, 0);
            root.Controls.Add(followRow, 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            confirmButton = ButtonFor("确认制卡 / 导出", FlashcardPreviewAutomation.Confirm, 132);
            cancelButton = ButtonFor("取消", FlashcardPreviewAutomation.Cancel, 88);
            confirmButton.Click += async delegate { await confirm(); };
            cancelButton.Click += async delegate { await cancel(); if (current?.Status == FlashcardDraftStatus.Cancelled) { DialogResult = DialogResult.Cancel; Close(); } };
            buttons.Controls.Add(confirmButton);
            buttons.Controls.Add(cancelButton);
            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);
        }

        internal void Render(FlashcardDraftPreviewState state)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke(new Action<FlashcardDraftPreviewState>(Render), state); return; }
            current = state ?? FlashcardDraftPreviewState.Empty;
            var contentKey = JsonSerializer.Serialize(current.Draft == null ? null : new
            {
                current.Draft.OperationId, current.Draft.Revision, current.Draft.Sha256,
                current.Draft.Source, current.Draft.Cards, current.Draft.Destinations
            });
            if (!string.Equals(renderedContentKey, contentKey, StringComparison.Ordinal))
            {
                // Invalidate the rendered identity before replacing native ListItems.
                // Status-only refreshes must not destroy UIA references or selection.
                cards.AccessibleDescription = "";
                cards.BeginUpdate();
                try
                {
                    cards.Items.Clear();
                    if (current.Draft != null)
                        for (var index = 0; index < current.Draft.Cards.Count; index++) cards.Items.Add("卡片 " + (index + 1));
                    if (cards.Items.Count > 0) cards.SelectedIndex = 0;
                    else { front.Clear(); back.Clear(); }
                    renderedContentKey = contentKey;
                    renderGeneration++;
                }
                finally { cards.EndUpdate(); }
            }
            if (current.Draft != null)
            {
                destinations.Text = "目标：" + (current.Draft.Destinations == null || current.Draft.Destinations.Count == 0 ? "无" : string.Join("、", current.Draft.Destinations));
            }
            else destinations.Text = "目标：尚未确定";
            destinationResults.Text = string.Join(Environment.NewLine, (current.Draft?.DestinationOutcomes ?? Array.Empty<CardDestinationOutcome>()).Select(x =>
                (x.Kind == "anki" ? "Anki" : x.Kind == "remnote" ? "RemNote" : x.Kind) + "：" + DestinationStateText(x.State)
                + "；写入数：" + (x.ExternalStateKnown && x.ExternalWriteCount.HasValue ? x.ExternalWriteCount.Value.ToString() : "未知")
                + (string.IsNullOrWhiteSpace(x.ReasonCode) ? "" : "；原因：" + x.ReasonCode)
                + (x.Evidence == null ? "；尚无导出回执" : "；回执：" + x.Evidence.Id)));
            destinationResults.AccessibleDescription = JsonSerializer.Serialize(current.Draft?.DestinationOutcomes ?? Array.Empty<CardDestinationOutcome>());
            destinationResults.Visible = current.Draft?.DestinationOutcomes.Count > 0;
            status.Text = StatusText(current);
            status.AccessibleDescription = current.Status.ToString();
            confirmButton.Enabled = !followUpInFlight && current.CanConfirm;
            cancelButton.Enabled = !followUpInFlight && current.CanConfirm;
            followUpButton.Enabled = !followUpInFlight && current.CanFollowUp && !string.IsNullOrWhiteSpace(followUpInput.Text);
            followUpInput.Enabled = !followUpInFlight && current.CanFollowUp;
            followUpInput.AccessibleDescription = current.FollowUpUnavailableReason ?? "question.follow-up-current-session";
            if (current.Status == FlashcardDraftStatus.Completed)
                status.Text += " 本次操作已结束，可从悬浮窗开始新的提问或摘录。";
            else if (current.Draft != null && !current.CanFollowUp && !current.CommandInFlight)
                status.Text += current.Draft.Source == FlashcardDraftSource.ReadAloud ? " 摘录/朗读草稿暂不支持追问。" : " 追问不可用：当前会话未就绪或绑定已失效。";
            // UI-only, read-only HelpText on the existing Cards automation element.
            // Published last, from this rendered draft, never a pending Core query.
            // No token or card body is exposed through this identity surface.
            cards.AccessibleDescription = JsonSerializer.Serialize(new
            {
                schemaVersion = "knowledge-clipper-rendered-preview.v1",
                operationId = current.Draft?.OperationId,
                draftSha256 = current.Draft?.Sha256,
                revision = current.Draft?.Revision,
                renderGeneration,
                cardCount = cards.Items.Count,
                ready = !followUpInFlight && current.CanConfirm
            });
        }

        private async Task FollowUpAsync()
        {
            var text = followUpInput.Text.Trim();
            if (followUpInFlight || string.IsNullOrWhiteSpace(text) || current == null || !current.CanFollowUp) return;
            followUpInFlight = true;
            Render(current);
            string error = null;
            try { await followUp(text).ConfigureAwait(false); }
            catch (OperationCanceledException) { error = "question.wait-interrupted-backend-state-unconfirmed"; }
            catch (Exception exception) { error = exception.Message; }
            // A completion can arrive after the owner is closed, or on a pool thread
            // when a modeless caller did not establish a WinForms sync context.
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            void CompleteOnWindowThread()
            {
                if (IsDisposed || Disposing) return;
                followUpInFlight = false;
                if (error == null) followUpInput.Clear();
                Render(current);
                if (error != null) { status.Text = "追问未完成，输入已保留：" + error; status.AccessibleDescription = error; }
            }
            try { if (InvokeRequired) BeginInvoke((Action)CompleteOnWindowThread); else CompleteOnWindowThread(); }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
        }

        private void RenderSelectedCard()
        {
            if (current?.Draft == null || cards.SelectedIndex < 0 || cards.SelectedIndex >= current.Draft.Cards.Count) return;
            var card = current.Draft.Cards[cards.SelectedIndex];
            front.Text = card.Front;
            back.Text = card.Back;
        }

        private static string StatusText(FlashcardDraftPreviewState value) => value.Status switch
        {
            FlashcardDraftStatus.Processing => "正在生成卡片草稿…",
            FlashcardDraftStatus.AwaitingConfirmation => "请检查正反面，确认后才会写入目标。",
            FlashcardDraftStatus.Confirming => "正在确认草稿版本…",
            FlashcardDraftStatus.Exporting => "已确认，正在写入目标…",
            FlashcardDraftStatus.Completed => value.Draft?.Destinations?.Count == 0 ? "卡片已完成；未选择导出目标，未进行外部写入。" : "制卡与目标写入已完成。",
            FlashcardDraftStatus.Failed => "操作失败：" + (value.NoticeCode ?? "unknown"),
            FlashcardDraftStatus.Cancelled => "已取消，未写入目标。",
            _ => "结果状态未知，请检查后再操作。"
        };

        private static string DestinationStateText(string state) => state switch
        {
            "not_started" => "尚未写入", "succeeded" => "完成", "failed" => "失败",
            "unknown_external_state" => "外部结果未知（不会自动重写）", _ => state ?? "未知"
        };

        private static TextBox FaceBox(string automationId) => new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(248, 250, 252),
            Name = automationId,
            AccessibleName = automationId
        };

        private static Label LabelFor(string automationId, string text, Color color) => new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = text,
            ForeColor = color,
            Name = automationId,
            AccessibleName = automationId
        };

        private static Button ButtonFor(string text, string automationId, int width = 88) => new()
        {
            Text = text,
            Width = width,
            Height = 30,
            Name = automationId,
            AccessibleName = automationId
        };
    }
}

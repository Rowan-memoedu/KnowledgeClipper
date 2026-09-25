using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class LanguageFollowUpPanel : TableLayoutPanel
{
    private readonly ClipDraft draft;
    private readonly DraftStore store;
    private readonly Func<string, Task> submit;
    private readonly TableLayoutPanel cards, composer;
    private readonly TextBox question;
    private readonly Label failure, empty;
    private readonly Button send;
    private readonly Dictionary<int, QuestionRow> rows = new();
    private bool manuallyExpanded, expanded, loading, blocked;
    internal Button Toggle { get; }
    internal bool Sending { get; private set; }
    private bool QuestionMode => draft.Route?.OutputKind == "question" || draft.Route == null && draft.Content?.Answer != null;
    internal bool ShowingQuestions => QuestionMode || expanded;
    internal int[] SelectedIndexes => rows.Values.Where(row => row.Selected).Select(row => row.Index).OrderBy(i => i).ToArray();
    internal int AvailableCount => rows.Values.Count(row => row.Available);
    internal bool SelectedNeedsReconciliation => rows.Values.Any(row => row.Selected && row.NeedsReconciliation);
    internal event Action StateChanged;

    internal LanguageFollowUpPanel(ClipDraft draft, DraftStore store, Func<string, Task> submit, Func<int[], Task> import)
    {
        this.draft = draft; this.store = store; this.submit = submit;
        Name = AccessibleName = "knowledge-clipper.language.follow-up";
        Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Margin = new Padding(0, 8, 0, 4);
        Toggle = MakeButton("追问", "follow-up-toggle");
        Toggle.Click += (_, _) => { manuallyExpanded = true; expanded = !expanded; RefreshView(blocked); StateChanged?.Invoke(); };
        cards = new TableLayoutPanel { Name = "knowledge-clipper.language.question-cards", Dock = DockStyle.Top,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(cards, 0, 0);
        empty = new Label { Text = "还没有问答，可在下方继续追问。", AutoSize = true, ForeColor = Color.DimGray };
        Controls.Add(empty, 0, 1);
        failure = new Label { Name = "knowledge-clipper.language.conversation-error", AutoSize = true, ForeColor = Color.Firebrick };
        Controls.Add(failure, 0, 2);
        composer = new TableLayoutPanel { Name = "knowledge-clipper.language.follow-up-composer", Dock = DockStyle.Top,
            AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 12, 0, 0) };
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composer.Controls.Add(new Label { Text = "继续追问", AutoSize = true }, 0, 0);
        question = MakeText("follow-up-input", 85); question.PlaceholderText = "继续补充条件或提出问题";
        loading = true; question.Text = draft.PendingFollowUp ?? ""; loading = false;
        question.TextChanged += (_, _) => {
            if (loading || Sending) return;
            draft.PendingFollowUp = string.IsNullOrWhiteSpace(question.Text) ? null : question.Text;
            store.Save(draft); RefreshButtons();
        };
        composer.Controls.Add(question, 0, 1);
        send = MakeButton("发送追问", "follow-up-send"); send.Click += async (_, _) => await SendAsync();
        composer.Controls.Add(send, 0, 2); Controls.Add(composer, 0, 3);
        importCards = import;
        SizeChanged += (_, _) => failure.MaximumSize = new Size(Math.Max(150, ClientSize.Width - 12), 0);
        RefreshView(false);
    }

    private readonly Func<int[], Task> importCards;
    internal void RefreshView(bool busy)
    {
        if (IsDisposed) return;
        blocked = busy;
        if (!manuallyExpanded) expanded = draft.Route?.FollowUpDefault == true;
        Visible = ShowingQuestions;
        composer.Visible = expanded;
        Toggle.Text = QuestionMode ? expanded ? "收起追问" : "追问" : expanded ? "返回原卡片" : "追问";
        var previews = QuestionCards.Preview(draft, store);
        cards.SuspendLayout();
        try
        {
            foreach (var obsolete in rows.Keys.Where(index => !previews.Any(p => p.Index == index)).ToArray())
            { var row = rows[obsolete]; cards.Controls.Remove(row); rows.Remove(obsolete); row.Dispose(); }
            for (var position = 0; position < previews.Count; position++)
            {
                var preview = previews[position];
                if (!rows.TryGetValue(preview.Index, out var row))
                {
                    row = new QuestionRow(preview.Index, draft, store, importCards, () => StateChanged?.Invoke());
                    rows.Add(preview.Index, row); cards.Controls.Add(row, 0, position);
                }
                row.Refresh(preview, blocked || Sending);
            }
        }
        finally { cards.ResumeLayout(true); }
        empty.Visible = previews.Count == 0;
        failure.Text = string.Join("\n", new[] { draft.ConversationError, draft.ConversationImportError }.Where(text => !string.IsNullOrWhiteSpace(text)));
        failure.Visible = failure.Text.Length > 0;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        if (send == null) return;
        question.ReadOnly = blocked || Sending;
        send.Enabled = !blocked && !Sending && !string.IsNullOrWhiteSpace(question.Text);
        send.Text = Sending ? "正在回复…" : "发送追问";
    }

    private async Task SendAsync()
    {
        if (blocked || Sending || string.IsNullOrWhiteSpace(question.Text)) return;
        var value = question.Text; var count = draft.Conversation.Count;
        Sending = true; draft.PendingFollowUp = value; draft.ConversationError = null; store.Save(draft);
        RefreshView(blocked); StateChanged?.Invoke();
        try
        {
            await submit(value);
            if (draft.ConversationError == null && draft.Conversation.Count > count)
            {
                draft.PendingFollowUp = null;
                if (!IsDisposed) { loading = true; question.Clear(); loading = false; }
            }
            else if (draft.ConversationError == null) draft.ConversationError = "未收到追问回复，可重试本次问题。";
        }
        catch (Exception ex) { draft.ConversationError = ex.Message; }
        finally
        {
            if (draft.ConversationError != null) draft.PendingFollowUp = value;
            store.Save(draft); Sending = false;
            if (!IsDisposed) { RefreshView(blocked); StateChanged?.Invoke(); }
        }
    }

    private sealed class QuestionRow : TableLayoutPanel
    {
        private readonly ClipDraft draft;
        private readonly TextBox question, answer, context;
        private readonly GroupBox contextGroup;
        private readonly Label status, destination;
        private readonly CheckBox selected;
        private readonly Button import;
        private bool loading, initialized, frozen, answered;
        private ImportState state;
        internal int Index { get; }
        internal bool Available => answered && !string.IsNullOrWhiteSpace(answer.Text) && state != ImportState.Verified;
        internal bool Selected => selected.Checked && Available;
        internal bool NeedsReconciliation => state is ImportState.Unknown or ImportState.Writing;

        internal QuestionRow(int index, ClipDraft draft, DraftStore store, Func<int[], Task> requestImport, Action changed)
        {
            Index = index; this.draft = draft;
            Name = "knowledge-clipper.language.question-card-" + index;
            Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 1; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Padding = new Padding(8); Margin = new Padding(0, 4, 0, 12); BorderStyle = BorderStyle.FixedSingle;
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            selected = new CheckBox { Name = Name + ".selected", Text = "第 " + (index + 1) + " 轮", AutoSize = true, Margin = new Padding(4, 8, 4, 4) };
            selected.CheckedChanged += (_, _) => { if (!loading) changed(); };
            status = new Label { Name = Name + ".status", AutoSize = true, Margin = new Padding(8, 8, 4, 4), ForeColor = Color.DimGray };
            import = MakeButton("导入本张", "question-card-" + index + ".import");
            import.Click += async (_, _) => { if (import.Enabled && Available) await requestImport(new[] { Index }); };
            header.Controls.AddRange(new Control[] { selected, status, import }); Controls.Add(header, 0, 0);
            var row = 1;
            var hasImage = draft.Original.FrontImage != null;
            if (draft.Original.FrontImage is { } frontImage)
            {
                Controls.Add(new Label { Text = "正面 · 原图", AutoSize = true }, 0, row++);
                var image = new TableLayoutPanel { Name = Name + ".front-image", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
                image.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                ExcerptDraftForm.AddImage(image, frontImage); Controls.Add(image, 0, row++);
            }
            Controls.Add(new Label { Text = hasImage ? "本轮问题 · 随回答放入反面" : "问题 · 卡片正面", AutoSize = true }, 0, row++);
            question = MakeText("question-card-" + index + ".question", 64); question.ReadOnly = true; Controls.Add(question, 0, row++);
            Controls.Add(new Label { Text = "回答 · 卡片反面", AutoSize = true }, 0, row++);
            answer = MakeText("question-card-" + index + ".answer", 120); Controls.Add(answer, 0, row++);
            answer.TextChanged += (_, _) => {
                if (loading || frozen || !answered) return;
                draft.EditedAnswers[Index] = answer.Text; store.Save(draft); changed();
            };
            contextGroup = new GroupBox { Text = "辅助上下文 · 随卡片带入", Name = Name + ".context-group", Dock = DockStyle.Top, Height = 120, Padding = new Padding(6) };
            context = MakeText("question-card-" + index + ".context", 90); context.Dock = DockStyle.Fill; context.ReadOnly = true;
            contextGroup.Controls.Add(context); Controls.Add(contextGroup, 0, row++);
            destination = new Label { Name = Name + ".destination", AccessibleName = Name + ".destination", AutoSize = true,
                ForeColor = Color.DimGray, Margin = new Padding(4) };
            Controls.Add(destination, 0, row);
            SizeChanged += (_, _) => status.MaximumSize = destination.MaximumSize = new Size(Math.Max(120, ClientSize.Width - 28), 0);
        }

        internal void Refresh(QuestionCardPreview preview, bool blocked)
        {
            loading = true;
            try
            {
                state = preview.State; frozen = preview.Frozen;
                answered = Index < draft.Conversation.Count && draft.Conversation[Index].Answer.Answered;
                SetText(question, preview.Question); SetText(answer, preview.Answer ?? ""); SetText(context, preview.Context ?? "");
                destination.Text = "导入：" + preview.DestinationLabel; destination.Visible = !string.IsNullOrWhiteSpace(preview.DestinationLabel);
                contextGroup.Visible = context.Text.Length > 0;
                if (!initialized) { selected.Checked = Available; initialized = true; }
                if (state == ImportState.Verified || !answered) selected.Checked = false;
                selected.Enabled = Available && !blocked; selected.Visible = answered;
                answer.ReadOnly = frozen || blocked || !answered;
                import.Visible = answered; import.Enabled = Available && !blocked;
                import.Text = state == ImportState.Verified ? "已导入" : NeedsReconciliation ? "核对本张写入结果" : "导入本张";
                status.Text = !answered ? "暂未解答" : state switch {
                    ImportState.Verified => "已导入 · 内容已冻结",
                    ImportState.Unknown or ImportState.Writing => "写入结果待核对 · 内容已冻结",
                    ImportState.Failed => frozen ? "导入失败 · 内容已冻结" : "导入失败",
                    _ => frozen ? "待导入 · 内容已冻结" : "待确认"
                };
                if (!string.IsNullOrWhiteSpace(preview.Error)) status.Text += "\n" + preview.Error;
            }
            finally { loading = false; }
        }

        private static void SetText(TextBox box, string value)
        {
            if (box.Text == value) return;
            var start = box.SelectionStart; var length = box.SelectionLength;
            box.Text = value;
            box.Select(Math.Min(start, box.TextLength), Math.Min(length, Math.Max(0, box.TextLength - start)));
        }
    }

    private static TextBox MakeText(string suffix, int height) => new() { Name = "knowledge-clipper.language." + suffix,
        AccessibleName = "knowledge-clipper.language." + suffix, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Top, Height = height, Margin = new Padding(4) };
    private static Button MakeButton(string text, string suffix) => new() { Text = text, Name = "knowledge-clipper.language." + suffix,
        AccessibleName = "knowledge-clipper.language." + suffix, AutoSize = true, MinimumSize = new Size(90, 34), Margin = new Padding(4) };
}

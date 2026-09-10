using System;
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
    private readonly Action<ConversationTurn> previewCard;
    private readonly TextBox history, question;
    private readonly Label failure;
    private readonly Button send, makeCard;
    private bool manuallyExpanded, expanded, loading, blocked;
    private ConversationTurn previewedTurn;
    internal Button Toggle { get; }
    internal bool Sending { get; private set; }
    internal event Action StateChanged;

    internal LanguageFollowUpPanel(ClipDraft draft, DraftStore store, Func<string, Task> submit, Action<ConversationTurn> previewCard)
    {
        this.draft = draft; this.store = store; this.submit = submit; this.previewCard = previewCard;
        Name = AccessibleName = "knowledge-clipper.language.follow-up";
        Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Margin = new Padding(0, 12, 0, 4);
        Toggle = MakeButton("追问", "follow-up-toggle");
        Toggle.Click += (_, _) => { manuallyExpanded = true; expanded = !expanded; RefreshView(blocked); };
        Controls.Add(new Label { Text = "追问", AutoSize = true }, 0, 0);
        history = MakeText("conversation", 200); history.ReadOnly = true;
        Controls.Add(history, 0, 1);
        failure = new Label { Name = "knowledge-clipper.language.conversation-error", AutoSize = true, ForeColor = Color.Firebrick };
        Controls.Add(failure, 0, 2);
        question = MakeText("follow-up-input", 85); question.PlaceholderText = "继续补充条件或提出问题";
        loading = true; question.Text = draft.PendingFollowUp ?? ""; loading = false;
        question.TextChanged += (_, _) => {
            if (loading || Sending) return;
            draft.PendingFollowUp = string.IsNullOrWhiteSpace(question.Text) ? null : question.Text;
            store.Save(draft); RefreshButtons();
        };
        Controls.Add(question, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        send = MakeButton("发送追问", "follow-up-send"); send.Click += async (_, _) => await SendAsync();
        makeCard = MakeButton("将这轮问答制卡", "follow-up-card");
        makeCard.Click += (_, _) => {
            var turn = draft.Conversation.LastOrDefault();
            if (blocked || Sending || turn?.Answer.Answered != true || ReferenceEquals(turn, previewedTurn)) return;
            try { previewCard(turn); previewedTurn = turn; }
            catch (Exception ex) { draft.ConversationError = ex.Message; store.Save(draft); }
            RefreshView(blocked);
        };
        actions.Controls.AddRange(new Control[] { send, makeCard }); Controls.Add(actions, 0, 4);
        SizeChanged += (_, _) => failure.MaximumSize = new Size(Math.Max(150, ClientSize.Width - 12), 0);
        RefreshView(false);
    }

    internal void RefreshView(bool busy)
    {
        if (IsDisposed) return;
        blocked = busy;
        if (!manuallyExpanded) expanded = draft.Route?.FollowUpDefault == true;
        Visible = expanded;
        Toggle.Text = expanded ? "收起追问" : "追问";
        var transcript = string.Join("\r\n\r\n", draft.Conversation.Select((turn, index) =>
            $"{index + 1}. 问：{turn.Question}\r\n答：{turn.Answer.DisplayText}"));
        if (history.Text != transcript) history.Text = transcript;
        history.Visible = draft.Conversation.Count > 0;
        failure.Text = draft.ConversationError ?? ""; failure.Visible = failure.Text.Length > 0;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        if (send == null) return;
        question.ReadOnly = blocked || Sending;
        send.Enabled = !blocked && !Sending && !string.IsNullOrWhiteSpace(question.Text);
        send.Text = Sending ? "正在回复…" : "发送追问";
        var turn = draft.Conversation.LastOrDefault();
        makeCard.Visible = turn?.Answer.Answered == true;
        makeCard.Enabled = !blocked && !Sending && turn?.Answer.Answered == true && !ReferenceEquals(turn, previewedTurn);
        makeCard.Text = ReferenceEquals(turn, previewedTurn) ? "已打开这轮卡片预览" : "将这轮问答制卡";
    }

    private async Task SendAsync()
    {
        if (blocked || Sending || string.IsNullOrWhiteSpace(question.Text)) return;
        var value = question.Text;
        var count = draft.Conversation.Count;
        Sending = true;
        draft.PendingFollowUp = value; draft.ConversationError = null; store.Save(draft);
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

    private static TextBox MakeText(string suffix, int height) => new() { Name = "knowledge-clipper.language." + suffix,
        AccessibleName = "knowledge-clipper.language." + suffix, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Top, Height = height, Margin = new Padding(4) };
    private static Button MakeButton(string text, string suffix) => new() { Text = text, Name = "knowledge-clipper.language." + suffix,
        AccessibleName = "knowledge-clipper.language." + suffix, AutoSize = true, MinimumSize = new Size(90, 34), Margin = new Padding(4) };
}

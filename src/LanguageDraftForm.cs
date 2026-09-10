using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class LanguageDraftForm : Form
{
    internal ClipDraft Draft { get; }
    private bool contentRunning, followUpRunning;
    internal bool ContentRunning { get => contentRunning; set { contentRunning = value; if (followUp != null) RefreshStatus(); } }
    internal bool FollowUpRunning { get => followUpRunning; set { followUpRunning = value; if (followUp != null) RefreshStatus(); } }
    private readonly DraftStore store;
    private readonly Func<Task> import, replay;
    private readonly Label status, source;
    private readonly TextBox pronunciation, meaning, example, reading, writing;
    private readonly TextBox basicFront, basicBack;
    private readonly TableLayoutPanel languageFields, basicFields;
    private readonly TabControl tabs;
    private readonly LanguageFollowUpPanel followUp;
    private readonly Button confirm, retry;
    private readonly ComboBox practice;
    private readonly Panel decision;
    private readonly CancellationTokenSource lifetime = new();
    private bool committing, loadingFields;
    internal event Action RetryContent;
    internal event Func<string, Task> SubmitFollowUp;
    internal event Action<ConversationTurn> PreviewFollowUpCard;
    internal CancellationToken Lifetime => lifetime.Token;

    internal LanguageDraftForm(ClipDraft draft, DraftStore store, Func<Task> import, Func<Task> replay, Action stop)
    {
        Draft = draft; this.store = store; this.import = import; this.replay = replay;
        Text = "语言卡片预览 · " + draft.Material.SelectedText.TakeText(24);
        Name = AccessibleName = "knowledge-clipper.language.preview";
        Font = new Font("微软雅黑", 10); AutoScaleMode = AutoScaleMode.Dpi;
        Width = 840; Height = 740; MinimumSize = new Size(620, 520); StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroll = new Panel { Name = "knowledge-clipper.language.content-scroll", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 680), Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Percent : SizeType.AutoSize, i == 5 ? 100 : 0));
        status = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Text = "正在处理内容…", Name = "knowledge-clipper.language.status", AccessibleName = "knowledge-clipper.language.status" };
        source = new Label { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.DimGray,
            Text = "目标：" + draft.Material.SelectedText.TakeText(120) + "\n来源：" + draft.Material.SourceTitle.TakeText(80) + "\n导入：" + draft.Settings.AnkiDeck + " · " + draft.Settings.AnkiModel };
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(source, 0, 1);
        decision = new Panel { Dock = DockStyle.Top, AutoSize = true }; layout.Controls.Add(decision, 0, 2);
        var fields = languageFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pronunciation = Editor("knowledge-clipper.language.pronunciation", 42);
        meaning = Editor("knowledge-clipper.language.meaning", 60);
        example = Editor("knowledge-clipper.language.context", 80);
        fields.Controls.Add(new Label { Text = "读音", AutoSize = true }, 0, 0); fields.Controls.Add(pronunciation, 1, 0);
        fields.Controls.Add(new Label { Text = "语境义", AutoSize = true }, 0, 1); fields.Controls.Add(meaning, 1, 1);
        fields.Controls.Add(new Label { Text = "句子", AutoSize = true }, 0, 2); fields.Controls.Add(example, 1, 2);
        var contentFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        contentFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        contentFields.Controls.Add(fields, 0, 0); layout.Controls.Add(contentFields, 0, 3);
        basicFields = new TableLayoutPanel { Name = "knowledge-clipper.language.basic-fields", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false };
        basicFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        basicFront = Editor("knowledge-clipper.language.basic-front", 110); basicFront.ReadOnly = true;
        basicBack = Editor("knowledge-clipper.language.basic-back", 180);
        basicFields.Controls.Add(new Label { Text = "正面 · 原文", AutoSize = true }, 0, 0); basicFields.Controls.Add(basicFront, 0, 1);
        basicFields.Controls.Add(new Label { Text = "反面 · 翻译或答案", AutoSize = true }, 0, 2); basicFields.Controls.Add(basicBack, 0, 3);
        contentFields.Controls.Add(basicFields, 0, 1);
        practice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Name = "knowledge-clipper.language.practice", AccessibleName = "knowledge-clipper.language.practice" };
        practice.Items.AddRange(new object[] { "两者：理解与朗读 + 书写", "理解与朗读", "书写练习" }); practice.SelectedIndex = (int)draft.Practice;
        layout.Controls.Add(practice, 0, 4);
        tabs = new TabControl { Name = "knowledge-clipper.language.faces", Dock = DockStyle.Fill, MinimumSize = new Size(100, 130) };
        reading = Editor("knowledge-clipper.language.reading-preview", 150); reading.Dock = DockStyle.Fill; reading.ReadOnly = true;
        writing = Editor("knowledge-clipper.language.writing-preview", 150); writing.Dock = DockStyle.Fill; writing.ReadOnly = true;
        var readTab = new TabPage("理解与朗读 · 正反面"); readTab.Controls.Add(reading); tabs.TabPages.Add(readTab);
        var writeTab = new TabPage("书写练习 · 正反面"); writeTab.Controls.Add(writing); tabs.TabPages.Add(writeTab); layout.Controls.Add(tabs, 0, 5);
        var actions = new TableLayoutPanel { Name = "knowledge-clipper.language.actions", Dock = DockStyle.Fill, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(12, 6, 12, 6), BackColor = Color.WhiteSmoke };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        confirm = Button("确认制卡并导入 Anki", "knowledge-clipper.language.confirm"); confirm.Enabled = false;
        confirm.Dock = DockStyle.Fill; confirm.BackColor = Color.FromArgb(38, 90, 150); confirm.ForeColor = Color.White;
        confirm.FlatStyle = FlatStyle.Flat; confirm.FlatAppearance.BorderSize = 0; confirm.Padding = new Padding(8, 4, 8, 4);
        confirm.Click += async (_, _) => await ConfirmAsync();
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        retry = Button("重试内容", "knowledge-clipper.language.retry-content"); retry.Click += (_, _) => RetryContent?.Invoke();
        var replayButton = Button("重播 / 重新朗读", "knowledge-clipper.language.replay"); replayButton.Click += async (_, _) => await this.replay();
        var stopButton = Button("停止朗读", "knowledge-clipper.language.stop"); stopButton.Click += (_, _) => stop();
        followUp = new LanguageFollowUpPanel(draft, store, async question => {
            if (ContentRunning || FollowUpRunning || committing) throw new InvalidOperationException("请等待当前处理完成后追问。");
            var handler = SubmitFollowUp ?? throw new InvalidOperationException("追问功能尚未连接。");
            await handler(question);
        }, turn => {
            var handler = PreviewFollowUpCard ?? throw new InvalidOperationException("问答制卡预览尚未连接。");
            handler(turn);
        });
        followUp.StateChanged += RefreshStatus;
        followUp.Toggle.Click += (_, _) => { if (followUp.Visible) scroll.ScrollControlIntoView(followUp); };
        layout.Controls.Add(followUp, 0, 6);
        controls.Controls.AddRange(new Control[] { retry, replayButton, stopButton, followUp.Toggle });
        actions.Controls.Add(confirm, 0, 0); actions.Controls.Add(controls, 0, 1);
        var note = new Label { Name = "knowledge-clipper.language.preview-note", Text = "关闭窗口会保留草稿，可从悬浮窗的“最近草稿”继续。", AutoSize = true, ForeColor = Color.DimGray };
        layout.Controls.Add(note, 0, 7);
        scroll.Controls.Add(layout); root.Controls.Add(scroll, 0, 0); root.Controls.Add(actions, 0, 1); Controls.Add(root);
        void Fit() {
            note.MaximumSize = source.MaximumSize = status.MaximumSize = new Size(Math.Max(250, scroll.ClientSize.Width - 55), 0);
            layout.MinimumSize = new Size(0, Math.Max(scroll.ClientSize.Height, 680));
        }
        scroll.SizeChanged += (_, _) => Fit(); Fit();
        pronunciation.TextChanged += (_, _) => SaveEdits(); meaning.TextChanged += (_, _) => SaveEdits(); example.TextChanged += (_, _) => SaveEdits(); practice.SelectedIndexChanged += (_, _) => SaveEdits();
        example.MouseUp += (_, _) => SaveEdits(); example.KeyUp += (_, _) => SaveEdits();
        basicBack.TextChanged += (_, _) => SaveEdits();
        FormClosed += (_, _) => lifetime.Cancel();
        if (draft.Content != null) ContentReady();
        RefreshStatus();
    }

    private static TextBox Editor(string name, int height) => new() { Name = name, AccessibleName = name, Dock = DockStyle.Top, Height = height,
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Margin = new Padding(5), Font = new Font("微软雅黑", 11) };
    private static Button Button(string text, string id) => new() { Text = text, Name = id, AccessibleName = id, AutoSize = true, MinimumSize = new Size(100, 34), Margin = new Padding(4) };

    internal void ContentReady()
    {
        if (IsDisposed || Draft.Content == null) return;
        loadingFields = true;
        UpdateContentKind();
        if (IsBasic)
        {
            basicFront.Text = Draft.Original.SelectedText;
            basicBack.Text = UnresolvedAnswer ? Draft.Content.Answer.DisplayText :
                Draft.EditedBasic?.Back ?? Draft.Content.Basic?.Back ?? Draft.Content.Answer?.DisplayText ?? "";
            loadingFields = false;
            source.Text = "来源：" + Draft.Material.SourceTitle.TakeText(80) + "\n导入：" + Draft.Settings.AnkiDeck + " · 单张卡片";
            RefreshConversation(); RefreshStatus(); return;
        }
        var fields = Draft.EditedFields ?? Draft.Content.Fields;
        if (fields == null) { loadingFields = false; RefreshConversation(); RefreshStatus(); return; }
        pronunciation.Text = fields.Pronunciation; meaning.Text = fields.Meaning;
        example.Text = Draft.Material.ContextText ?? fields.GeneratedSentence;
        practice.SelectedIndex = (int)Draft.Practice;
        loadingFields = false;
        example.ReadOnly = Draft.Material.ContextText != null;
        source.Text = "目标：" + Draft.Material.SelectedText.TakeText(120) + "\n来源：" + Draft.Material.SourceTitle.TakeText(80) + " · " +
            (Draft.Material.ContextText == null ? "AI 生成例句" : Draft.Material.ContextOrigin == "user" ? "用户提供原文" : "来源原文") + "\n导入：" + Draft.Settings.AnkiDeck + " · " + Draft.Settings.AnkiModel;
        RenderFaces(); RefreshStatus();
    }

    private bool IsBasic => Draft.Route != null ? Draft.Route.OutputKind != "language" :
        Draft.Content?.Basic != null || Draft.Content?.Answer != null;
    private bool UnresolvedAnswer => Draft.Content?.Answer is { Answered: false };
    private bool FollowUpBusy => FollowUpRunning || followUp?.Sending == true;
    private void UpdateContentKind()
    {
        var basic = IsBasic;
        languageFields.Visible = practice.Visible = tabs.Visible = !basic;
        basicFields.Visible = basic && !UnresolvedAnswer;
        Text = (basic ? "卡片预览 · " : "语言卡片预览 · ") + Draft.Original.SelectedText.TakeText(24);
    }

    internal void RefreshConversation()
    {
        if (IsDisposed) return;
        UpdateContentKind();
        followUp.RefreshView(ContentRunning || FollowUpRunning || committing);
        RefreshStatus();
    }

    private LanguageFields EditedFields()
    {
        if (Draft.Material.ContextText != null) return new(pronunciation.Text, meaning.Text, null, null);
        var oldStart = (Draft.EditedFields ?? Draft.Content?.Fields)?.GeneratedTargetStart;
        var target = Draft.Material.SelectedText;
        int? start = oldStart is int old && old >= 0 && old <= example.Text.Length - target.Length && example.Text.AsSpan(old, target.Length).SequenceEqual(target) ? old : null;
        if (example.SelectedText == target) start = example.SelectionStart;
        if (start == null)
        {
            var unique = example.Text.IndexOf(target, StringComparison.Ordinal);
            if (unique >= 0 && example.Text.IndexOf(target, unique + 1, StringComparison.Ordinal) < 0) start = unique;
        }
        return new(pronunciation.Text, meaning.Text, example.Text, start);
    }

    private void RenderFaces()
    {
        if (Draft.Content == null || reading == null || IsBasic) return;
        var fields = EditedFields();
        var sentence = Draft.Material.ContextText ?? fields.GeneratedSentence ?? "";
        var start = Draft.Material.TargetStart ?? fields.GeneratedTargetStart;
        reading.Text = "正面\r\n" + sentence + "\r\n\r\n反面\r\n" + Draft.Material.SelectedText + " · " + fields.Pronunciation + "\r\n" + fields.Meaning;
        writing.Text = start is int i && i >= 0 && i <= sentence.Length - Draft.Material.SelectedText.Length
            ? "正面\r\n" + sentence[..i] + "［" + fields.Pronunciation + " · " + fields.Meaning + "］" + sentence[(i + Draft.Material.SelectedText.Length)..] + "\r\n\r\n反面\r\n" + Draft.Material.SelectedText + "\r\n" + sentence
            : "请在句子框中选定本次目标，以确认需要遮蔽的位置。";
        reading.Enabled = practice.SelectedIndex != (int)PracticeChoice.Writing;
        writing.Enabled = practice.SelectedIndex != (int)PracticeChoice.Reading;
    }

    private void SaveEdits()
    {
        if (loadingFields || Draft.Content == null || Draft.ConfirmedNote != null) return;
        if (IsBasic)
        {
            if (UnresolvedAnswer) return;
            Draft.EditedBasic = new BasicFields(Draft.Original.SelectedText, basicBack.Text);
            store.Save(Draft); RefreshStatus(); return;
        }
        Draft.EditedFields = EditedFields(); Draft.Practice = (PracticeChoice)practice.SelectedIndex;
        store.Save(Draft); RenderFaces();
    }

    internal void RefreshStatus()
    {
        if (IsDisposed) return;
        UpdateContentKind();
        status.Text = (committing ? "正在核对 Anki 写入结果…" : Draft.ImportState == ImportState.Verified ? "已核对 Anki：" + Draft.CardIds.Length + " 张可复习卡片" : Draft.ImportError ?? Draft.ContentError ?? (Draft.Content == null ? "正在准备内容…" : UnresolvedAnswer ? "暂未解答，可继续追问或补充条件。" : "内容已就绪，请核对后确认")) + "\n" + Draft.SpeechStatus + (Draft.SpeechError == null ? "" : " · " + Draft.SpeechError);
        confirm.Visible = !UnresolvedAnswer || Draft.ConfirmedNote != null;
        confirm.Enabled = Draft.Content != null && !ContentRunning && !FollowUpBusy && !committing && !UnresolvedAnswer &&
            (!IsBasic || !string.IsNullOrWhiteSpace(basicBack.Text)) && Draft.ImportState != ImportState.Verified;
        confirm.Text = committing ? "正在处理 Anki…" : ContentRunning || Draft.Content == null ? "等待内容准备完成…" :
            Draft.ImportState == ImportState.Unknown ? "核对写入结果" : Draft.ConfirmedNote == null ? "确认制卡并导入 Anki" : Draft.ImportState == ImportState.Verified ? "已导入" : "重试导入";
        retry.Enabled = !ContentRunning && !FollowUpBusy && Draft.Content == null;
        pronunciation.ReadOnly = meaning.ReadOnly = Draft.ConfirmedNote != null;
        example.ReadOnly = Draft.ConfirmedNote != null || Draft.Material.ContextText != null;
        practice.Enabled = Draft.ConfirmedNote == null;
        basicFront.ReadOnly = true;
        basicBack.ReadOnly = Draft.ConfirmedNote != null || UnresolvedAnswer;
        followUp.RefreshView(ContentRunning || FollowUpRunning || committing);
    }

    private async Task ConfirmAsync()
    {
        if (committing || ContentRunning || FollowUpBusy || Draft.Content == null || UnresolvedAnswer || Draft.ImportState == ImportState.Verified) return;
        committing = true;
        try
        {
            if (Draft.ConfirmedNote == null)
            {
                if (IsBasic)
                {
                    if (string.IsNullOrWhiteSpace(basicBack.Text)) throw new InvalidOperationException("请填写卡片反面。");
                    Draft.EditedBasic = new BasicFields(Draft.Original.SelectedText, basicBack.Text);
                    Draft.Content = Draft.Content with { Basic = Draft.EditedBasic };
                }
                else
                {
                    var fields = EditedFields(); ContentModule.ValidateFields(Draft.Material, fields);
                    Draft.Content = Draft.Content with { Fields = fields }; Draft.Practice = (PracticeChoice)practice.SelectedIndex;
                }
                Draft.ConfirmedNote = AnkiNoteDraft.From(Draft); Draft.ImportState = ImportState.Ready;
                Draft.Times["confirmed"] = DateTimeOffset.UtcNow; store.Save(Draft);
            }
            RefreshStatus(); await import();
        }
        catch (Exception ex) { if (!IsDisposed) status.Text = Draft.ImportError ?? ex.Message; if (Draft.ConfirmedNote == null) Draft.ContentError = ex.Message; }
        finally { committing = false; RefreshStatus(); }
    }

    internal Task<CaptureMaterial> ResolveContextAsync()
    {
        var done = new TaskCompletionSource<CaptureMaterial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var instructions = new Label { AutoSize = true, MaximumSize = new Size(500, 0), Text = "未取得可靠上下文。可允许生成，或补充原文；目标重复时请选定其中本次位置。" };
        panel.Controls.Add(instructions, 0, 0);
        var text = Editor("knowledge-clipper.language.user-context", 75); panel.Controls.Add(text, 0, 1);
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var generated = Button("使用生成例句", "knowledge-clipper.language.allow-generated");
        var supplied = Button("使用补充原文", "knowledge-clipper.language.use-context");
        var cancel = Button("取消制卡", "knowledge-clipper.language.cancel-content");
        void Complete(CaptureMaterial material) { decision.Controls.Clear(); done.TrySetResult(material); }
        generated.Click += (_, _) => Complete(Draft.Material with { ContextOrigin = "generated_allowed" });
        supplied.Click += (_, _) =>
        {
            var target = Draft.Material.SelectedText;
            var at = text.SelectedText == target ? text.SelectionStart : text.Text.IndexOf(target, StringComparison.Ordinal);
            if (at < 0 || text.SelectedText != target && text.Text.IndexOf(target, at + 1, StringComparison.Ordinal) >= 0)
            { status.Text = "请在补充原文框中选中本次目标。"; return; }
            Complete(Draft.Material with { ContextText = text.Text, ContextOrigin = "user", TargetStart = at });
        };
        cancel.Click += (_, _) => { Draft.ContentError = "已取消制卡，原材料已保留。"; Complete(null); };
        row.Controls.AddRange(new Control[] { generated, supplied, cancel }); panel.Controls.Add(row, 0, 2); decision.Controls.Add(panel);
        lifetime.Token.Register(() => done.TrySetResult(null));
        return done.Task;
    }

    internal Task<string> ChooseModeAsync(string question)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        row.Controls.Add(new Label { Text = question, AutoSize = true });
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        combo.Items.AddRange(Draft.Configuration.Modes.Select(m => (object)m.Definition.Name).ToArray()); combo.SelectedIndex = 0;
        var choose = Button("采用此模式", "knowledge-clipper.language.choose-mode");
        choose.Click += (_, _) => { decision.Controls.Clear(); done.TrySetResult(Draft.Configuration.Modes[combo.SelectedIndex].Definition.Id); };
        row.Controls.Add(combo); row.Controls.Add(choose); decision.Controls.Add(row);
        lifetime.Token.Register(() => done.TrySetResult(null));
        return done.Task;
    }
}

internal static class ShortLanguageText
{
    internal static string TakeText(this string value, int length) => value.Length <= length ? value : value[..length] + "…";
}

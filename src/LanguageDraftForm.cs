using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class LanguageDraftForm : Form
{
    internal ClipDraft Draft { get; }
    private bool contentRunning, languageImportRunning;
    internal bool ContentRunning { get => contentRunning; set { contentRunning = value; if (status != null) RefreshStatus(); } }
    internal bool LanguageImportRunning { get => languageImportRunning; set { languageImportRunning = value; if (status != null) RefreshStatus(); } }
    private readonly DraftStore store;
    private readonly Func<Task> import, replay;
    private readonly Label status, source, modeStatus, meaningLabel, translationLabel, lemmaLabel;
    private readonly TextBox pronunciation, meaning, example, translation, lemma;
    private readonly RichTextBox reading, writing;
    private readonly TextBox basicFront, basicBack, dictionaryPreview;
    private readonly TableLayoutPanel languageFields, basicFields, modeFields, dictionaryPanel;
    private readonly TabControl tabs;
    private readonly TabPage readTab, writeTab;
    private readonly Button confirm, retry, retryDictionary, replayButton, stopButton, importCurrentMode, sentencePlay, generateExample;
    private readonly ComboBox practice, modePreview;
    private readonly PictureBox frontPicture;
    private readonly Panel decision;
    private readonly CancellationTokenSource lifetime = new();
    private bool committing, languageCommitting, loadingFields, loadingModes;
    private ClipDraft[] modeChildren = [];
    private string activeModeId, modeError, languageImportError;
    private Func<bool> closeWhenIdle;
    private ClipDraft ActiveMode => modeChildren.FirstOrDefault(child => child.ModeId == activeModeId);
    private bool HasModeCards => Draft.ModeCards.Count > 0;
    private bool HasFrontImage => Draft.Material.FrontImage != null;
    private int ImagePreviewHeight => (frontPicture?.Height ?? 0) + (int)Math.Round(230.0 * DeviceDpi / 96);
    private ClipDraft EditingDraft => HasModeCards ? ActiveMode : Draft;
    internal ClipDraft SelectedLanguageDraft => EditingDraft;
    private static ModeDefinition Definition(ClipDraft draft) => draft?.Configuration.Modes.FirstOrDefault(m => m.Definition.Id == draft.ModeId)?.Definition;
    private bool UnresolvedLanguage => EditingDraft?.Content?.Fields?.IsUnresolved == true;
    internal event Action RetryContent;
    internal event Func<Task> RetryDictionary;
    internal event Action OpenChatRequested;
    internal event Func<string[], Task> ImportLanguageModes;
    internal event Func<string, Task> PlaySentence;
    internal event Action GenerateExampleRequested;
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
            MinimumSize = new Size(0, 680), Padding = new Padding(16), ColumnCount = 1, RowCount = 9 };
        for (var i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Percent : SizeType.AutoSize, i == 5 ? 100 : 0));
        status = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Text = "正在处理内容…", Name = "knowledge-clipper.language.status", AccessibleName = "knowledge-clipper.language.status" };
        source = new Label { Name = "knowledge-clipper.language.source", AccessibleName = "knowledge-clipper.language.source", AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.DimGray,
            Text = "目标：" + draft.Material.SelectedText.TakeText(120) + "\n来源：" + draft.Material.SourceTitle.TakeText(80) + "\n导入：" + ExportRouting.Describe(draft) };
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(source, 0, 1);
        decision = new Panel { Dock = DockStyle.Top, AutoSize = true }; layout.Controls.Add(decision, 0, 2);
        var fields = languageFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pronunciation = Editor("knowledge-clipper.language.pronunciation", 42);
        meaning = Editor("knowledge-clipper.language.meaning", 60);
        example = Editor("knowledge-clipper.language.context", 80);
        fields.Controls.Add(new Label { Text = "读音", AutoSize = true }, 0, 0); fields.Controls.Add(pronunciation, 1, 0);
        meaningLabel = new Label { Text = "语境义", AutoSize = true };
        fields.Controls.Add(meaningLabel, 0, 1); fields.Controls.Add(meaning, 1, 1);
        var sentenceLabel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        sentenceLabel.Controls.Add(new Label { Text = "句子", AutoSize = true, Margin = new Padding(0, 7, 3, 0) });
        sentencePlay = new Button { Text = "▶", Name = "knowledge-clipper.language.play-sentence", AccessibleName = "播放当前句子",
            AutoSize = true, MinimumSize = new Size(32, 32), Margin = Padding.Empty, Padding = new Padding(2), FlatStyle = FlatStyle.Flat };
        sentencePlay.Click += async (_, _) => {
            if (IsBasic || string.IsNullOrWhiteSpace(example.Text) || PlaySentence == null) return;
            var text = example.Text;
            try { await PlaySentence(text); }
            catch (Exception ex) { if (!IsDisposed) status.Text = "句子朗读未完成：" + ex.Message; }
        };
        sentenceLabel.Controls.Add(sentencePlay);
        fields.Controls.Add(sentenceLabel, 0, 2); fields.Controls.Add(example, 1, 2);
        translation = Editor("knowledge-clipper.language.sentence-translation", 65);
        lemma = Editor("knowledge-clipper.language.lemma", 42);
        translationLabel = new Label { Text = "整句中文翻译", AutoSize = true }; lemmaLabel = new Label { Text = "词典形式", AutoSize = true };
        fields.Controls.Add(translationLabel, 0, 3); fields.Controls.Add(translation, 1, 3);
        fields.Controls.Add(lemmaLabel, 0, 4); fields.Controls.Add(lemma, 1, 4);
        var contentFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        contentFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false };
        modeFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modePreview = new ComboBox { Name = "knowledge-clipper.language.mode-preview", AccessibleName = "knowledge-clipper.language.mode-preview",
            DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Margin = new Padding(5) };
        modeStatus = new Label { Name = "knowledge-clipper.language.mode-status", AccessibleName = "knowledge-clipper.language.mode-status",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(5) };
        modeFields.Controls.Add(new Label { Text = "本次独立语言卡片 · 切换查看与编辑", AutoSize = true }, 0, 0);
        modeFields.Controls.Add(modePreview, 0, 1); modeFields.Controls.Add(modeStatus, 0, 2);
        contentFields.Controls.Add(modeFields, 0, 0); contentFields.Controls.Add(fields, 0, 1); layout.Controls.Add(contentFields, 0, 3);
        basicFields = new TableLayoutPanel { Name = "knowledge-clipper.language.basic-fields", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false };
        basicFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        basicFront = Editor("knowledge-clipper.language.basic-front", 110); basicFront.ReadOnly = true;
        basicBack = Editor("knowledge-clipper.language.basic-back", 180);
        basicFields.Controls.Add(new Label { Text = "正面 · 原文", AutoSize = true }, 0, 0); basicFields.Controls.Add(basicFront, 0, 1);
        basicFields.Controls.Add(new Label { Text = "反面 · 翻译或答案", AutoSize = true }, 0, 2); basicFields.Controls.Add(basicBack, 0, 3);
        contentFields.Controls.Add(basicFields, 0, 2);
        practice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Name = "knowledge-clipper.language.practice", AccessibleName = "knowledge-clipper.language.practice" };
        practice.Items.AddRange(new object[] { "两者：理解与朗读 + 书写", "理解与朗读", "书写练习" });
        practice.SelectedIndex = (int)(HasFrontImage ? PracticeChoice.Reading : draft.Practice);
        layout.Controls.Add(practice, 0, 4);
        tabs = new TabControl { Name = "knowledge-clipper.language.faces", Dock = DockStyle.Fill, MinimumSize = new Size(100, 130) };
        reading = FaceEditor("knowledge-clipper.language.reading-preview");
        writing = FaceEditor("knowledge-clipper.language.writing-preview");
        readTab = new TabPage("理解与朗读 · 正反面");
        if (draft.Material.FrontImage is { } frontImage)
        {
            var faces = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            faces.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            faces.RowStyles.Add(new RowStyle(SizeType.AutoSize)); faces.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var image = new TableLayoutPanel { Name = "knowledge-clipper.language.front-image", Dock = DockStyle.Top,
                AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            image.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            image.Controls.Add(new Label { Text = "正面 · 原图", AutoSize = true });
            ExcerptDraftForm.AddImage(image, frontImage);
            frontPicture = image.Controls.OfType<PictureBox>().Single();
            faces.Controls.Add(image, 0, 0); faces.Controls.Add(reading, 0, 1); readTab.Controls.Add(faces);
            tabs.MinimumSize = new Size(100, ImagePreviewHeight);
            DpiChanged += (_, _) => tabs.MinimumSize = new Size(100, ImagePreviewHeight);
        }
        else readTab.Controls.Add(reading);
        tabs.TabPages.Add(readTab);
        writeTab = new TabPage("书写练习 · 正反面"); writeTab.Controls.Add(writing);
        if (!HasFrontImage) tabs.TabPages.Add(writeTab);
        layout.Controls.Add(tabs, 0, 5);
        dictionaryPanel = new TableLayoutPanel { Name = "knowledge-clipper.language.dictionary-panel", Dock = DockStyle.Top,
            AutoSize = true, ColumnCount = 1 };
        dictionaryPanel.Controls.Add(new Label { Text = "划词词典（独立免费接口）", AutoSize = true }, 0, 0);
        dictionaryPreview = Editor("knowledge-clipper.language.dictionary-preview", 110);
        dictionaryPreview.ReadOnly = true;
        dictionaryPanel.Controls.Add(dictionaryPreview, 0, 1);
        layout.Controls.Add(dictionaryPanel, 0, 6);
        var actions = new TableLayoutPanel { Name = "knowledge-clipper.language.actions", Dock = DockStyle.Fill, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(12, 6, 12, 6), BackColor = Color.WhiteSmoke };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        confirm = Button("确认导入", "knowledge-clipper.language.confirm"); confirm.Enabled = false;
        confirm.Dock = DockStyle.Fill; confirm.BackColor = Color.FromArgb(38, 90, 150); confirm.ForeColor = Color.White;
        confirm.FlatStyle = FlatStyle.Flat; confirm.FlatAppearance.BorderSize = 0; confirm.Padding = new Padding(8, 4, 8, 4);
        confirm.Click += async (_, _) => await ConfirmAsync();
        generateExample = Button("用生成例句制卡", "knowledge-clipper.language.generate-example");
        generateExample.Dock = DockStyle.Fill;
        generateExample.Click += (_, _) => {
            if (!generateExample.Enabled) return;
            try { GenerateExampleRequested?.Invoke(); }
            catch (Exception ex) { status.Text = "生成例句未启动：" + ex.Message; }
        };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        retry = Button("重试内容", "knowledge-clipper.language.retry-content"); retry.Click += (_, _) => RetryContent?.Invoke();
        retryDictionary = Button("重试词典", "knowledge-clipper.language.retry-dictionary");
        retryDictionary.Click += async (_, _) => {
            retryDictionary.Enabled = false;
            try { await (RetryDictionary?.Invoke() ?? Task.CompletedTask); }
            catch (Exception ex) { if (!IsDisposed) status.Text = "词典查询未完成：" + ex.Message; }
            finally { if (!IsDisposed) RefreshStatus(); }
        };
        replayButton = Button("重播 / 重新朗读", "knowledge-clipper.language.replay"); replayButton.Click += async (_, _) => await this.replay();
        stopButton = Button("停止朗读", "knowledge-clipper.language.stop"); stopButton.Click += (_, _) => stop();
        importCurrentMode = Button("导入当前模式", "knowledge-clipper.language.import-current-mode");
        importCurrentMode.Click += async (_, _) => {
            var modeId = activeModeId;
            if (modeId == null || ModeActionBusy) return;
            await ImportModesAsync([modeId]);
            TryCloseCompleted(() => languageImportError == null && Draft.ImportError == null && ModeVerified(modeId));
        };
        controls.Controls.AddRange(new Control[] { importCurrentMode, retry, retryDictionary, replayButton, stopButton });
        var openChat = new Button { Text = "多轮问答与组合制卡", AutoSize = true, Name = "knowledge-clipper.language.open-chat" };
        openChat.Click += (_, _) => { if (!Busy) OpenChatRequested?.Invoke(); };
        controls.Controls.Add(openChat);
        actions.RowCount = 3; actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.Controls.Add(confirm, 0, 0); actions.Controls.Add(generateExample, 0, 1); actions.Controls.Add(controls, 0, 2);
        var note = new Label { Name = "knowledge-clipper.language.preview-note", Text = "关闭窗口会保留草稿，可从悬浮窗的“最近草稿”继续。", AutoSize = true, ForeColor = Color.DimGray };
        layout.Controls.Add(note, 0, 8);
        scroll.Controls.Add(layout); root.Controls.Add(scroll, 0, 0); root.Controls.Add(actions, 0, 1); Controls.Add(root);
        void Fit() {
            note.MaximumSize = source.MaximumSize = status.MaximumSize = modeStatus.MaximumSize = new Size(Math.Max(250, scroll.ClientSize.Width - 55), 0);
            layout.MinimumSize = new Size(0, Math.Max(scroll.ClientSize.Height, 680));
        }
        scroll.SizeChanged += (_, _) => Fit(); Fit();
        pronunciation.TextChanged += (_, _) => SaveEdits(); meaning.TextChanged += (_, _) => SaveEdits(); example.TextChanged += (_, _) => SaveEdits();
        translation.TextChanged += (_, _) => SaveEdits(); lemma.TextChanged += (_, _) => SaveEdits();
        practice.SelectedIndexChanged += (_, _) => PracticeChanged();
        modePreview.SelectedIndexChanged += (_, _) => {
            if (loadingModes || modePreview.SelectedItem is not ModeOption choice) return;
            activeModeId = choice.Id; ContentReady();
        };
        example.MouseUp += (_, _) => SaveEdits(); example.KeyUp += (_, _) => SaveEdits();
        basicBack.TextChanged += (_, _) => SaveEdits();
        FormClosed += (_, _) => lifetime.Cancel();
        Disposed += (_, _) => { readTab.Dispose(); writeTab.Dispose(); };
        if (draft.Content != null || HasModeCards) ContentReady();
        RefreshStatus();
    }

    private static TextBox Editor(string name, int height) => new() { Name = name, AccessibleName = name, Dock = DockStyle.Top, Height = height,
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Margin = new Padding(5), Font = new Font("微软雅黑", 11) };
    private static Button Button(string text, string id) => new() { Text = text, Name = id, AccessibleName = id, AutoSize = true, MinimumSize = new Size(100, 34), Margin = new Padding(4) };
    private static RichTextBox FaceEditor(string id) => new() { Name = id, AccessibleName = id, Dock = DockStyle.Fill, Height = 150,
        ReadOnly = true, BackColor = Color.White, Font = new Font("微软雅黑", 11), ScrollBars = RichTextBoxScrollBars.Vertical };

    internal void ContentReady()
    {
        if (IsDisposed) return;
        if (!HasModeCards) LoadFields(Draft);
        RefreshStatus();
    }

    private static void SetText(TextBoxBase editor, string value)
    {
        value ??= "";
        if (editor.Text != value) editor.Text = value;
    }

    private void LoadFields(ClipDraft current)
    {
        loadingFields = true;
        try
        {
            practice.SelectedIndex = (int)(HasFrontImage ? PracticeChoice.Reading : Draft.Practice);
            if (IsBasic)
            {
                SetText(basicFront, Draft.Route?.OutputKind == "question" ? Draft.QuestionText : Draft.Original.SelectedText);
                SetText(basicBack, UnresolvedAnswer ? Draft.Content.Answer.DisplayText :
                    Draft.EditedBasic?.Back ?? Draft.Content?.Basic?.Back ?? Draft.Content?.Answer?.DisplayText ?? "");
                return;
            }
            var fields = current?.EditedFields ?? current?.Content?.Fields;
            var mode = Definition(current); var english = PublishedLanguage.Applies(mode) && mode.Language == "en";
            tabs.MinimumSize = new Size(100, HasFrontImage ? ImagePreviewHeight : PublishedLanguage.Applies(mode) ? 220 : 130);
            meaningLabel.Text = english ? "英文解释" : mode?.Practice == PracticeChoice.Writing ? "意义线索" : "语境义";
            translation.Visible = translationLabel.Visible = english;
            lemma.Visible = lemmaLabel.Visible = english && mode.Practice == PracticeChoice.Reading;
            SetText(pronunciation, fields?.Pronunciation); SetText(meaning, english ? fields?.ExplanationEn : fields?.Meaning);
            SetText(translation, fields?.SentenceTranslationZh); SetText(lemma, fields?.Lemma);
            SetText(example, fields?.NormalizedSentence ?? current?.Material.ContextText ?? fields?.GeneratedSentence);
            var desiredTabs = HasFrontImage ? new[] { readTab } : HasModeCards ? current?.Practice == PracticeChoice.Writing ? new[] { writeTab } : new[] { readTab } : new[] { readTab, writeTab };
            if (!tabs.TabPages.Cast<TabPage>().SequenceEqual(desiredTabs))
            {
                tabs.TabPages.Clear(); tabs.TabPages.AddRange(desiredTabs);
            }
            RenderFaces();
        }
        finally { loadingFields = false; }
    }

    private sealed record ModeOption(string Id, string Name) { public override string ToString() => Name; }
    private string ModeName(ClipDraft child) => child.Configuration.Modes.FirstOrDefault(mode => mode.Definition.Id == child.ModeId)?.Definition.Name ?? child.ModeId;
    private static bool Frozen(ClipDraft child) => child != null && ExportRouting.Frozen(child);
    private static bool ModeImportable(ClipDraft child)
    {
        if (child == null || child.ImportState == ImportState.Verified) return false;
        if (child.ConfirmedNote != null) return true;
        if (Frozen(child) || child.PreviewContent?.Fields == null || child.PreviewContent.Fields.IsUnresolved) return false;
        try
        {
            if (PublishedLanguage.Applies(Definition(child))) PublishedLanguage.Validate(Definition(child), child.Material, child.PreviewContent.Fields);
            else ContentModule.ValidateFields(child.Material, child.PreviewContent.Fields);
            return true;
        }
        catch (InvalidDataException) { return false; }
    }
    private static string ModeState(ClipDraft child) => child.ImportState == ImportState.Verified ? ExportRouting.ResultSummary(child) :
        child.ImportState is ImportState.Unknown or ImportState.Writing ? "写入结果待核对" :
        child.ImportError ?? child.ContentError ?? (child.ConfirmedNote != null ? "已确认，待导入" : child.Content?.Fields is { IsUnresolved: true } pending ? PublishedLanguage.PendingReason(pending) : child.Content?.Fields != null ? "已就绪" : "等待内容");

    private void RefreshModes()
    {
        loadingModes = true;
        try
        {
            modeChildren = LanguageModeCards.Children(Draft, store);
            modeError = null;
            if (!modeChildren.Any(child => child.ModeId == activeModeId))
                activeModeId = (modeChildren.FirstOrDefault(child => child.Content?.Fields != null) ?? modeChildren.FirstOrDefault())?.ModeId;
            var choices = modeChildren.Select(child => new ModeOption(child.ModeId, ModeName(child))).ToArray();
            if (!modePreview.Items.Cast<ModeOption>().SequenceEqual(choices))
            {
                modePreview.Items.Clear(); modePreview.Items.AddRange(choices);
            }
            modePreview.SelectedIndex = Array.FindIndex(choices, choice => choice.Id == activeModeId);
            modeStatus.Text = string.Join("\n", modeChildren.Select(child => ModeName(child) + "：" + ModeState(child) + "\n导入：" + ExportRouting.Describe(child)));
        }
        catch (Exception ex)
        {
            modeError = ex.Message; modeChildren = []; modePreview.Items.Clear(); modeStatus.Text = ex.Message;
        }
        finally { loadingModes = false; }
        LoadFields(ActiveMode);
    }

    private bool IsBasic => !HasModeCards && (Draft.Route != null ? Draft.Route.OutputKind != "language" :
        Draft.Content?.Basic != null || Draft.Content?.Answer != null);
    private bool UnresolvedAnswer => Draft.Content?.Answer is { Answered: false };
    private bool LanguageBusy => LanguageImportRunning || languageCommitting;
    private bool Busy => ContentRunning || committing || LanguageBusy;
    private bool ModeActionBusy => committing || LanguageBusy;
    private void UpdateContentKind()
    {
        var basic = IsBasic;
        var current = EditingDraft ?? Draft;
        languageFields.Visible = practice.Visible = tabs.Visible = !basic;
        modeFields.Visible = HasModeCards;
        basicFields.Visible = basic && !UnresolvedAnswer;
        source.Text = basic ? "来源：" + Draft.Material.SourceTitle.TakeText(80) + "\n导入：" + ExportRouting.Describe(Draft) +
                (ExportRouting.For(Draft) == ExportDestination.RemNote && string.IsNullOrWhiteSpace(basicBack.Text) ? " · 仅保存正面内容" : " · 单张卡片") :
            "目标：" + current.Material.SelectedText.TakeText(120) + "\n来源：" + current.Material.SourceTitle.TakeText(80) + " · " +
            (current.Material.ContextText == null ? "AI 生成例句" : current.Material.ContextOrigin == "user" ? "用户提供原文" : "来源原文") + "\n导入：" + ExportRouting.Describe(current) +
            (ExportRouting.For(current) == ExportDestination.Anki ? " · " + (PublishedLanguage.Applies(Definition(current)) ? PublishedLanguage.DictionaryModelName(current.Settings.AnkiModel) : current.Settings.AnkiModel) : "");
        Text = (basic ? "卡片预览 · " : Draft.Material.ContextText == null && Draft.ParentDraftId != null ? "生成例句卡片预览 · " : "语言卡片预览 · ") + Draft.Original.SelectedText.TakeText(24);
    }

    private LanguageFields EditedFields()
    {
        var current = EditingDraft ?? Draft;
        var prior = current.EditedFields ?? current.Content?.Fields;
        if (prior?.IsUnresolved == true) return prior;
        var published = PublishedLanguage.Applies(Definition(current)); var english = published && Definition(current).Language == "en";
        var edited = (prior ?? new(null, null, null, null)) with { Pronunciation = pronunciation.Text, Meaning = english ? null : meaning.Text,
            ExplanationEn = english ? meaning.Text : null, SentenceTranslationZh = english ? translation.Text : null,
            Lemma = english && Definition(current).Practice == PracticeChoice.Reading && !string.IsNullOrWhiteSpace(lemma.Text) ? lemma.Text : null };
        if (current.Material.ContextText != null) return edited with { GeneratedSentence = null, GeneratedTargetStart = null };
        var oldStart = (current.EditedFields ?? current.Content?.Fields)?.GeneratedTargetStart;
        var target = current.Material.SelectedText;
        int? start = oldStart is int old && old >= 0 && old <= example.Text.Length - target.Length && example.Text.AsSpan(old, target.Length).SequenceEqual(target) ? old : null;
        if (example.SelectedText == target) start = example.SelectionStart;
        if (start == null)
        {
            var unique = example.Text.IndexOf(target, StringComparison.Ordinal);
            if (unique >= 0 && example.Text.IndexOf(target, unique + 1, StringComparison.Ordinal) < 0) start = unique;
        }
        return edited with { GeneratedSentence = example.Text, GeneratedTargetStart = start };
    }

    private void RenderFaces()
    {
        var current = EditingDraft;
        if (reading == null || IsBasic) return;
        if (current?.Content?.Fields == null) { SetText(reading, ""); SetText(writing, ""); return; }
        var fields = EditedFields();
        if (fields.IsUnresolved)
        {
            SetText(reading, PublishedLanguage.PendingReason(fields)); SetText(writing, PublishedLanguage.PendingReason(fields)); return;
        }
        if (current.Material.FrontImage != null)
        {
            try
            {
                var published = PublishedLanguage.Applies(Definition(current));
                if (published) PublishedLanguage.Validate(Definition(current), current.Material, fields);
                else ContentModule.ValidateFields(current.Material, fields);
                var imageSentence = published ? PublishedLanguage.EffectiveSentence(current.Material, fields) : current.Material.ContextText ?? fields.GeneratedSentence;
                SetText(reading, "反面\n" + string.Join("\n", new[] { imageSentence, fields.Pronunciation, fields.Meaning, fields.ExplanationEn, fields.SentenceTranslationZh }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
            }
            catch (InvalidDataException ex) { SetText(reading, "请检查编辑内容：" + ex.Message); }
            reading.Enabled = true; writing.Enabled = false;
            return;
        }
        if (PublishedLanguage.Applies(Definition(current)))
        {
            try
            {
                var faces = PublishedLanguage.Render(Definition(current), current.Material, fields);
                static string DisplayLines(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");
                SetText(reading, "正面\n" + DisplayLines(faces.ReadingFront) + "\n\n反面\n" + faces.ReadingBack);
                var selectionStart = reading.SelectionStart; var selectionLength = reading.SelectionLength;
                reading.SelectAll(); reading.SelectionColor = Color.Black; reading.SelectionBackColor = Color.White;
                reading.Select(3 + DisplayLines(faces.ReadingFront[..faces.TargetStart]).Length, DisplayLines(current.Material.SelectedText).Length); reading.SelectionColor = Color.DarkBlue; reading.SelectionBackColor = Color.LightBlue;
                reading.Select(selectionStart, selectionLength);
                SetText(writing, "正面\n" + faces.WritingFront + "\n\n反面\n" + faces.WritingBack + (faces.Warning == null ? "" : "\n\n注意：" + faces.Warning));
            }
            catch (InvalidDataException ex) { SetText(reading, "请检查编辑内容：" + ex.Message); SetText(writing, reading.Text); }
            reading.Enabled = current.Practice != PracticeChoice.Writing; writing.Enabled = current.Practice != PracticeChoice.Reading;
            return;
        }
        var sentence = current.Material.ContextText ?? fields.GeneratedSentence ?? "";
        var start = current.Material.TargetStart ?? fields.GeneratedTargetStart;
        SetText(reading, "正面\r\n" + sentence + "\r\n\r\n反面\r\n" + current.Material.SelectedText + " · " + fields.Pronunciation + "\r\n" + fields.Meaning);
        SetText(writing, start is int i && i >= 0 && i <= sentence.Length - current.Material.SelectedText.Length
            ? "正面\r\n" + sentence[..i] + "［" + fields.Pronunciation + " · " + fields.Meaning + "］" + sentence[(i + current.Material.SelectedText.Length)..] + "\r\n\r\n反面\r\n" + current.Material.SelectedText + "\r\n" + sentence
            : "请在句子框中选定本次目标，以确认需要遮蔽的位置。");
        var selected = HasModeCards ? current.Practice : (PracticeChoice)practice.SelectedIndex;
        reading.Enabled = selected != PracticeChoice.Writing;
        writing.Enabled = selected != PracticeChoice.Reading;
    }

    private void SaveEdits()
    {
        if (loadingFields || EditingDraft?.Content == null || UnresolvedLanguage || Frozen(EditingDraft)) return;
        if (HasModeCards)
        {
            if (ModeActionBusy) return;
            // Read the saved child again so a late state callback cannot make an old editor overwrite a frozen result.
            var child = store.Find(EditingDraft.Id);
            if (child == null || Frozen(child) || child.Content?.Fields == null) { RefreshStatus(); return; }
            child.EditedFields = EditedFields(); store.Save(child); RenderFaces(); RefreshStatus(); return;
        }
        if (IsBasic)
        {
            if (UnresolvedAnswer) return;
            Draft.EditedBasic = new BasicFields(Draft.Route?.OutputKind == "question" ? Draft.QuestionText : Draft.Original.SelectedText, basicBack.Text);
            store.Save(Draft); RefreshStatus(); return;
        }
        Draft.EditedFields = EditedFields(); Draft.Practice = (PracticeChoice)practice.SelectedIndex;
        store.Save(Draft); RenderFaces(); RefreshStatus();
    }

    private void PracticeChanged()
    {
        if (loadingFields || practice.SelectedIndex < 0) return;
        if (HasFrontImage)
        {
            loadingFields = true;
            try { practice.SelectedIndex = (int)PracticeChoice.Reading; }
            finally { loadingFields = false; }
            return;
        }
        if (!HasModeCards)
        {
            if (Draft.Content == null && !Frozen(Draft))
            {
                Draft.Practice = (PracticeChoice)practice.SelectedIndex;
                store.Save(Draft);
            }
            else SaveEdits();
            return;
        }
        if (Busy) { RefreshStatus(); return; }
        var selected = (PracticeChoice)practice.SelectedIndex;
        if (selected == Draft.Practice) return;
        Draft.Practice = selected; store.Save(Draft); RetryContent?.Invoke(); RefreshStatus();
    }

    internal void RefreshStatus()
    {
        if (IsDisposed) return;
        if (HasModeCards) RefreshModes();
        UpdateContentKind();
        dictionaryPanel.Visible = !IsBasic && Draft.Settings.ShowDictionaryInPreview;
        if (dictionaryPanel.Visible) SetText(dictionaryPreview, EditingDraft?.DictionaryDetails?.DisplayText ?? "正在查询词典…");
        retryDictionary.Visible = dictionaryPanel.Visible;
        retryDictionary.Enabled = !Busy && EditingDraft != null && !Frozen(EditingDraft);
        if (HasModeCards)
        {
            var available = modeChildren.Where(ModeImportable).ToArray();
            var current = ActiveMode;
            status.Text = (LanguageBusy ? "正在核对语言卡片写入结果…" : languageImportError ?? modeError ?? Draft.ImportError ??
                (current == null ? "等待独立模式结果…" : ModeName(current) + "：" + ModeState(current))) +
                (ContentRunning ? "\n其他内容仍在处理，已就绪的模式可以先导入。" : "") +
                "\n" + Draft.SpeechStatus + (Draft.SpeechError == null ? "" : " · " + Draft.SpeechError);
            confirm.Visible = true; confirm.Enabled = !ModeActionBusy && available.Length > 0;
            confirm.Text = LanguageBusy ? "正在导入并核对…" : available.Length > 0 ?
                (available.Any(child => child.ImportState is ImportState.Unknown or ImportState.Writing) ? "核对并导入 " : "确认导入 ") + available.Length + " 个已就绪模式" :
                modeChildren.Length > 0 && modeChildren.All(child => child.ImportState == ImportState.Verified) ? "所选模式均已导入" : "等待内容准备完成…";
        }
        else
        {
            status.Text = (committing ? "正在核对写入结果…" : Draft.ImportState == ImportState.Verified ? ExportRouting.ResultSummary(Draft) : Draft.ImportError ?? Draft.ContentError ?? (Draft.Content == null ? "正在准备内容…" : UnresolvedLanguage ? PublishedLanguage.PendingReason(Draft.Content.Fields) : UnresolvedAnswer ? "暂未解答，可继续追问或补充条件。" : "内容已就绪，请核对后确认")) + "\n" + Draft.SpeechStatus + (Draft.SpeechError == null ? "" : " · " + Draft.SpeechError);
            confirm.Visible = !UnresolvedAnswer || Draft.ConfirmedNote != null;
            confirm.Enabled = Draft.Content != null && !Busy && !UnresolvedAnswer && !UnresolvedLanguage &&
                (!IsBasic || ExportRouting.For(Draft) == ExportDestination.RemNote || !string.IsNullOrWhiteSpace(basicBack.Text)) && Draft.ImportState != ImportState.Verified;
            confirm.Text = committing ? "正在导入并核对…" : ContentRunning || Draft.Content == null ? "等待内容准备完成…" :
                Draft.ImportState == ImportState.Unknown ? "核对写入结果" : Draft.ConfirmedNote == null ? "确认导入 " + ExportRouting.For(Draft) : Draft.ImportState == ImportState.Verified ? "已导入" : "重试导入";
        }
        retry.Enabled = !Busy && (HasModeCards ? modeChildren.Any(child => (child.Content == null || child.Content.Fields?.IsUnresolved == true) && !Frozen(child)) : Draft.Content == null || UnresolvedLanguage);
        generateExample.Visible = !IsBasic && Draft.Route?.OutputKind == "language" && Draft.Material.ContextText != null;
        generateExample.Enabled = generateExample.Visible && !LanguageBusy && !committing;
        replayButton.Visible = stopButton.Visible = Draft.SupportsSpeech;
        sentencePlay.Visible = !IsBasic;
        sentencePlay.Enabled = PlaySentence != null && !string.IsNullOrWhiteSpace(example.Text);
        importCurrentMode.Visible = HasModeCards;
        importCurrentMode.Enabled = !ModeActionBusy && ModeImportable(ActiveMode);
        importCurrentMode.Text = ActiveMode?.ImportState is ImportState.Unknown or ImportState.Writing ? "核对当前模式" : "导入当前模式";
        var editing = EditingDraft;
        var readOnly = Frozen(editing) || UnresolvedLanguage || HasModeCards && (editing?.Content?.Fields == null || ModeActionBusy);
        pronunciation.ReadOnly = meaning.ReadOnly = translation.ReadOnly = lemma.ReadOnly = readOnly;
        example.ReadOnly = readOnly || editing?.Material.ContextText != null;
        practice.Enabled = !HasFrontImage && (HasModeCards ? !Busy : !Frozen(Draft));
        basicFront.ReadOnly = true;
        basicBack.ReadOnly = Frozen(Draft) || UnresolvedAnswer;
        if (closeWhenIdle != null && !Busy && IsHandleCreated)
        {
            var verified = closeWhenIdle; closeWhenIdle = null;
            BeginInvoke((Action)(() => TryCloseCompleted(verified)));
        }
    }

    private async Task ImportModesAsync(string[] modeIds)
    {
        if (!HasModeCards || ModeActionBusy || modeIds.Length == 0) return;
        RefreshModes();
        var available = modeChildren.Where(ModeImportable).Select(child => child.ModeId).ToHashSet(StringComparer.Ordinal);
        var selected = modeIds.Where(available.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return;
        closeWhenIdle = null;
        languageCommitting = true; languageImportError = null; Draft.ImportError = null; store.Save(Draft); RefreshStatus();
        try { await (ImportLanguageModes ?? throw new InvalidOperationException("独立语言模式导入尚未连接。"))(selected); }
        catch (Exception ex) { languageImportError = ex.Message; }
        finally { languageCommitting = false; ContentReady(); }
    }

    private async Task ConfirmAsync()
    {
        if (HasModeCards)
        {
            if (ModeActionBusy) return;
            string[] modes;
            try { modes = Draft.SelectedModeIds.Length > 0 ? Draft.SelectedModeIds.ToArray() :
                LanguageModeCards.Children(Draft, store).Select(child => child.ModeId).ToArray(); }
            catch (Exception ex) { languageImportError = ex.Message; RefreshStatus(); return; }
            await ImportModesAsync(modes);
            TryCloseCompleted(() => modes.Length > 0 && languageImportError == null && Draft.ImportError == null &&
                modes.All(ModeVerified));
            return;
        }
        if (Busy || Draft.Content == null || UnresolvedAnswer || UnresolvedLanguage || Draft.ImportState == ImportState.Verified) return;
        committing = true;
        var completed = false;
        try
        {
            if (Draft.ConfirmedNote == null)
            {
                if (IsBasic)
                {
                    if (ExportRouting.For(Draft) != ExportDestination.RemNote && string.IsNullOrWhiteSpace(basicBack.Text)) throw new InvalidOperationException("请填写卡片反面。");
                    Draft.EditedBasic = new BasicFields(Draft.Route?.OutputKind == "question" ? Draft.QuestionText : Draft.Original.SelectedText, basicBack.Text);
                    Draft.Content = Draft.Content with { Basic = Draft.EditedBasic };
                }
                else
                {
                    var fields = EditedFields();
                    if (PublishedLanguage.Applies(Definition(Draft))) PublishedLanguage.Validate(Definition(Draft), Draft.Material, fields);
                    else ContentModule.ValidateFields(Draft.Material, fields);
                    Draft.Content = Draft.Content with { Fields = fields }; Draft.Practice = (PracticeChoice)practice.SelectedIndex;
                }
                ExportRouting.Confirm(Draft, ExportRouting.BuildNote(Draft)); Draft.ImportState = ImportState.Ready;
                Draft.Times["confirmed"] = DateTimeOffset.UtcNow; store.Save(Draft);
            }
            RefreshStatus(); await import();
            completed = Draft.ImportState == ImportState.Verified && Draft.ImportError == null;
        }
        catch (Exception ex) { if (!IsDisposed) status.Text = Draft.ImportError ?? ex.Message; if (Draft.ConfirmedNote == null) Draft.ContentError = ex.Message; }
        finally { committing = false; RefreshStatus(); }
        if (completed) TryCloseCompleted(() => Draft.ImportState == ImportState.Verified && Draft.ImportError == null);
    }

    private bool ModeVerified(string modeId)
    {
        if (!Draft.ModeCards.TryGetValue(modeId, out var id)) return false;
        var child = store.Find(id);
        return child != null && child.Id == id && child.ModeId == modeId && child.ModeParentId == Draft.Id &&
            child.ParentDraftId == Draft.Id && child.Original == Draft.Original && child.Route?.ModeId == modeId &&
            child.Route.OutputKind == "language" && child.ImportState == ImportState.Verified &&
            child.ImportError == null && child.ContentError == null &&
            child.Configuration.Modes.Any(mode => mode.Definition.Id == modeId && mode.LoadError == null);
    }

    private void TryCloseCompleted(Func<bool> verified)
    {
        if (IsDisposed) return;
        try
        {
            if (!verified()) return;
            // Closing cancels Lifetime. Finish an already-running sibling first, then close
            // with every remaining draft and edit still saved for later recovery.
            if (Busy) { closeWhenIdle = verified; return; }
            Close();
        }
        catch (Exception ex) { status.Text = "导入结果仍需核对，草稿已保留：" + ex.Message; }
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
        var choices = Draft.Configuration.Modes.Where(mode => !HasFrontImage || mode.Definition.Practice is not (PracticeChoice.Writing or PracticeChoice.Both)).ToArray();
        var combo = new ComboBox { Name = "knowledge-clipper.language.mode-choice", AccessibleName = "knowledge-clipper.language.mode-choice", DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        combo.Items.AddRange(choices.Select(m => (object)m.Definition.Name).ToArray());
        if (choices.Length > 0) combo.SelectedIndex = 0;
        var choose = Button("采用此模式", "knowledge-clipper.language.choose-mode");
        choose.Enabled = choices.Length > 0;
        choose.Click += (_, _) => { if (combo.SelectedIndex < 0) return; decision.Controls.Clear(); done.TrySetResult(choices[combo.SelectedIndex].Definition.Id); };
        row.Controls.Add(combo); row.Controls.Add(choose); decision.Controls.Add(row);
        lifetime.Token.Register(() => done.TrySetResult(null));
        return done.Task;
    }
}

internal static class ShortLanguageText
{
    internal static string TakeText(this string value, int length) => value.Length <= length ? value : value[..length] + "…";
}

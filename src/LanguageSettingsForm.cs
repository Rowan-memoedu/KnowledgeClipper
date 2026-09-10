using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class LanguageSettingsForm : Form
{
    internal LanguageSettingsForm(LanguageDesktopFlow flow, string dataRoot)
    {
        Text = "知识剪藏设置"; Name = AccessibleName = "knowledge-clipper.language.settings";
        Width = 800; Height = 720; MinimumSize = new Size(650, 540); Font = new Font("微软雅黑", 10); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill }; root.Controls.Add(tabs, 0, 0);
        var common = new TabPage("常用设置") { AutoScroll = true };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); common.Controls.Add(table); tabs.TabPages.Add(common);
        var value = flow.Settings;
        int row = 0;
        void Add(string label, Control control) { control.Dock = DockStyle.Top; control.Margin = new Padding(6); table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(6, 10, 6, 6) }, 0, row); table.Controls.Add(control, 1, row++); }
        TextBox TextField(string text, string id) => new() { Text = text, Name = id, AccessibleName = id };
        var globalSelection = new CheckBox { Text = "选中文字后显示朗读悬浮点", Checked = value.GlobalSelection, AutoSize = true,
            Name = "knowledge-clipper.language.global-selection", AccessibleName = "knowledge-clipper.language.global-selection" };
        Add("全局语言学习", globalSelection);
        var typedAutoRead = new CheckBox { Text = "文字框输入后自动朗读（问题除外）", Checked = value.TypedInputAutoRead, AutoSize = true,
            Name = "knowledge-clipper.language.typed-auto-read", AccessibleName = "knowledge-clipper.language.typed-auto-read" };
        Add("文字输入", typedAutoRead);
        var questionCardContext = new CheckBox { Text = "问答卡正面附带前序问答上下文（新会话生效）", Checked = value.QuestionCardIncludeContext, AutoSize = true,
            Name = "knowledge-clipper.language.question-card-context", AccessibleName = "knowledge-clipper.language.question-card-context" };
        Add("问答卡片", questionCardContext);
        var context = new CheckBox { Text = "使用来源上下文", Checked = value.ContextEnabled, AutoSize = true };
        var granularity = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; granularity.Items.AddRange(new[] { "一句", "一段" }); granularity.SelectedIndex = value.ContextGranularity == "paragraph" ? 1 : 0;
        Add("上下文", context); Add("上下文范围", granularity);
        var practice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; practice.Items.AddRange(new[] { "两者", "理解与朗读", "书写练习" }); practice.SelectedIndex = (int)value.Practice; Add("默认练习", practice);
        var autoVoice = new CheckBox { Text = "按文本自动选择中文或英文音色", AutoSize = true, Checked = value.AutoVoice }; Add("语音", autoVoice);
        var voice = TextField(value.Voice, "knowledge-clipper.language.voice"); Add("中文 / 指定音色", voice);
        var englishVoice = TextField(value.EnglishVoice, "knowledge-clipper.language.english-voice"); Add("英文音色", englishVoice);
        NumericUpDown Number(int n, int min, int max) => new() { Minimum = min, Maximum = max, Value = n };
        var rate = Number(value.RatePercent, -50, 100); Add("速度变化 %", rate);
        var volume = Number(value.VolumePercent, -100, 100); Add("音量变化 %", volume);
        var pitch = Number(value.PitchHz, -50, 50); Add("音调变化 Hz", pitch);
        var endpoint = TextField(value.AnkiEndpoint, "knowledge-clipper.language.anki-endpoint"); Add("AnkiConnect", endpoint);
        var deck = new ComboBox { Text = value.AnkiDeck, Name = "knowledge-clipper.language.deck", AccessibleName = "knowledge-clipper.language.deck" }; Add("Anki 牌组", deck);
        var model = new ComboBox { Text = value.AnkiModel, Name = "knowledge-clipper.language.note-type", AccessibleName = "knowledge-clipper.language.note-type" }; Add("语言笔记类型", model);
        var readAnki = new Button { Text = "读取 Anki 牌组与类型", AutoSize = true }; Add("", readAnki);
        var api = TextField(value.ApiBaseUrl, "knowledge-clipper.language.api-url"); Add("内容 API", api);
        var apiModel = TextField(value.Model, "knowledge-clipper.language.api-model"); Add("内容模型", apiModel);
        var apiKey = TextField("", "knowledge-clipper.language.api-key"); apiKey.UseSystemPasswordChar = true; apiKey.PlaceholderText = "留空保留现有密钥"; Add("API Key", apiKey);
        var config = ModeConfigurationLoader.Load(flow.ModeDefaults, flow.ModeOverrides);
        var definitions = ModeConfigurationLoader.LoadDefinitions(flow.ModeDefaults, flow.ModeOverrides);
        var modeEdits = config.Modes.ToDictionary(m => m.Definition.Id, m => new ModeEdit(m), StringComparer.Ordinal);
        var promptPage = new TabPage("现有模式与提示词"); tabs.TabPages.Add(promptPage);
        var prompts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(10) };
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        prompts.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        TextBox Prompt(string text) => new() { Text = text, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true, WordWrap = true };
        var modeBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(ModeDefinition.Name), Width = 240,
            Name = "knowledge-clipper.language.mode", AccessibleName = "knowledge-clipper.language.mode" };
        mode.Items.AddRange(config.Modes.Select(m => (object)m.Definition).ToArray());
        modeBar.Controls.Add(new Label { Text = "选择模式", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }); modeBar.Controls.Add(mode);
        var description = Prompt(""); var classification = Prompt(config.ClassificationPrompt); var processing = Prompt("");
        description.Name = description.AccessibleName = "knowledge-clipper.language.mode-description";
        classification.Name = classification.AccessibleName = "knowledge-clipper.language.classification-prompt";
        processing.Name = processing.AccessibleName = "knowledge-clipper.language.processing-prompt";
        var followUp = new CheckBox { Text = "本模式默认展开追问", AutoSize = true,
            Name = "knowledge-clipper.language.follow-up-default", AccessibleName = "knowledge-clipper.language.follow-up-default" };
        string selectedModeId = null;
        string PromptPath(ModeDefinition definition) => Path.GetFullPath(Path.Combine(flow.ModeOverrides, definition.PromptFile));
        void RememberMode()
        {
            if (selectedModeId == null) return;
            var edit = modeEdits[selectedModeId];
            edit.Description = description.Text; edit.FollowUpDefault = followUp.Checked;
            // If existing definitions deliberately share a prompt file, their editors share its value too.
            var path = PromptPath(edit.Original.Definition);
            foreach (var shared in modeEdits.Values.Where(m => StringComparer.OrdinalIgnoreCase.Equals(PromptPath(m.Original.Definition), path)))
                shared.Prompt = processing.Text;
        }
        mode.SelectedIndexChanged += (_, _) =>
        {
            RememberMode();
            if (mode.SelectedItem is not ModeDefinition selected) return;
            selectedModeId = selected.Id;
            var edit = modeEdits[selected.Id];
            description.Text = edit.Description; processing.Text = edit.Prompt; followUp.Checked = edit.FollowUpDefault;
        };
        prompts.Controls.Add(modeBar, 0, 0);
        prompts.Controls.Add(new Label { Text = "模式识别说明", AutoSize = true }, 0, 1); prompts.Controls.Add(description, 0, 2);
        prompts.Controls.Add(followUp, 0, 3);
        prompts.Controls.Add(new Label { Text = "公共分类提示词（所有模式共用）", AutoSize = true }, 0, 4); prompts.Controls.Add(classification, 0, 5);
        prompts.Controls.Add(new Label { Text = "本模式处理提示词", AutoSize = true }, 0, 6); prompts.Controls.Add(processing, 0, 7);
        promptPage.Controls.Add(prompts); mode.SelectedIndex = 0;
        var notice = new Label { AutoSize = true, MaximumSize = new Size(750, 0), Text = "全局选词开关保存后立即生效，其余设置用于下一次操作。专用语言类型在首次确认导入时创建；已有模板不会被覆盖。" }; root.Controls.Add(notice, 0, 1);
        readAnki.Click += async (_, _) =>
        {
            readAnki.Enabled = false;
            try
            {
                var proposed = value with { AnkiEndpoint = endpoint.Text }; proposed.Validate();
                using var http = new HttpClient(); var anki = new AnkiConnection(http, endpoint.Text);
                var decks = await anki.DecksAsync(); var models = await anki.ModelsAsync();
                var oldDeck = deck.Text; var oldModel = model.Text; deck.Items.Clear(); model.Items.Clear();
                deck.Items.AddRange(decks); model.Items.AddRange(models); deck.Text = oldDeck; model.Text = oldModel;
                notice.Text = "已读取 Anki；请选择兼容语言共享字段的类型，或保留专用类型名称。";
            }
            catch (Exception ex) { notice.Text = "读取未完成：" + ex.Message; }
            finally { readAnki.Enabled = true; }
        };
        var save = new Button { Text = "保存", AutoSize = true, Height = 36, Name = "knowledge-clipper.language.save-settings", AccessibleName = "knowledge-clipper.language.save-settings" };
        root.Controls.Add(save, 0, 2);
        save.Click += (_, _) =>
        {
            try
            {
                var changed = value with { GlobalSelection = globalSelection.Checked, TypedInputAutoRead = typedAutoRead.Checked,
                    QuestionCardIncludeContext = questionCardContext.Checked,
                    ContextEnabled = context.Checked, ContextGranularity = granularity.SelectedIndex == 1 ? "paragraph" : "sentence",
                    Practice = (PracticeChoice)practice.SelectedIndex, AutoVoice = autoVoice.Checked, Voice = voice.Text.Trim(), EnglishVoice = englishVoice.Text.Trim(),
                    RatePercent = (int)rate.Value, VolumePercent = (int)volume.Value, PitchHz = (int)pitch.Value,
                    AnkiEndpoint = endpoint.Text.Trim(), AnkiDeck = deck.Text.Trim(), AnkiModel = model.Text.Trim(), ApiBaseUrl = api.Text.Trim(), Model = apiModel.Text.Trim() };
                changed.Validate();
                RememberMode();
                if (string.IsNullOrWhiteSpace(classification.Text) || modeEdits.Values.Any(m => string.IsNullOrWhiteSpace(m.Description) || string.IsNullOrWhiteSpace(m.Prompt)))
                    throw new InvalidOperationException("模式说明和提示词不能为空。");
                var promptWrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (classification.Text != config.ClassificationPrompt) promptWrites.Add(Path.GetFullPath(Path.Combine(flow.ModeOverrides, "classification.md")), classification.Text);
                foreach (var edit in modeEdits.Values.Where(m => m.Prompt != m.Original.Prompt))
                {
                    var path = PromptPath(edit.Original.Definition);
                    if (promptWrites.TryGetValue(path, out var text) && text != edit.Prompt)
                        throw new InvalidOperationException("公共分类与模式处理提示词共用了同一文件，但编辑内容不同，请先调整模式文件配置。");
                    promptWrites[path] = edit.Prompt;
                }
                if (!string.IsNullOrWhiteSpace(apiKey.Text)) { var credentials = DesktopCredentialSettings.Load(dataRoot); credentials.DeepSeekApiKey = apiKey.Text.Trim(); credentials.Save(dataRoot); }
                Directory.CreateDirectory(flow.ModeOverrides);
                if (modeEdits.Values.Any(m => m.Description != m.Original.Definition.Description || m.FollowUpDefault != m.Original.Definition.FollowUpDefault))
                {
                    var effective = modeEdits.Values.ToDictionary(m => m.Original.Definition.Id,
                        m => m.Original.Definition with { Description = m.Description, FollowUpDefault = m.FollowUpDefault }, StringComparer.Ordinal);
                    WorkflowJson.Save(Path.Combine(flow.ModeOverrides, "modes.json"), new ModeFile(definitions.Modes
                        .Select(m => effective.TryGetValue(m.Id, out var updated) ? updated : m).ToArray()));
                }
                foreach (var promptWrite in promptWrites)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(promptWrite.Key)!);
                    var temporary = promptWrite.Key + ".tmp";
                    File.WriteAllText(temporary, promptWrite.Value); File.Move(temporary, promptWrite.Key, true);
                }
                flow.SaveSettings(changed); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { notice.Text = "未保存：" + ex.Message; }
        };
        Controls.Add(root);
    }

    private sealed class ModeEdit(ModeSnapshot original)
    {
        internal ModeSnapshot Original { get; } = original;
        internal string Description { get; set; } = original.Definition.Description;
        internal string Prompt { get; set; } = original.Prompt;
        internal bool FollowUpDefault { get; set; } = original.Definition.FollowUpDefault;
    }
}

using System;
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
        Text = "语言学习设置"; Name = AccessibleName = "knowledge-clipper.language.settings";
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
        var modeFile = WorkflowJson.Read<ModeFile>(File.Exists(Path.Combine(flow.ModeOverrides, "modes.json"))
            ? Path.Combine(flow.ModeOverrides, "modes.json") : Path.Combine(flow.ModeDefaults, "modes.json"));
        var active = config.Modes.First();
        var promptPage = new TabPage("现有模式与提示词"); tabs.TabPages.Add(promptPage);
        var prompts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        TextBox Prompt(string text) => new() { Text = text, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true, WordWrap = true };
        var description = Prompt(active.Definition.Description); var classification = Prompt(config.ClassificationPrompt); var processing = Prompt(active.Prompt);
        prompts.Controls.Add(new Label { Text = "模式识别说明（" + active.Definition.Name + "）", AutoSize = true }, 0, 0); prompts.Controls.Add(description, 0, 1);
        prompts.Controls.Add(new Label { Text = "公共分类提示词", AutoSize = true }, 0, 2); prompts.Controls.Add(classification, 0, 3);
        prompts.Controls.Add(new Label { Text = "语言处理提示词", AutoSize = true }, 0, 4); prompts.Controls.Add(processing, 0, 5); promptPage.Controls.Add(prompts);
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
                var changed = value with { GlobalSelection = globalSelection.Checked, ContextEnabled = context.Checked, ContextGranularity = granularity.SelectedIndex == 1 ? "paragraph" : "sentence",
                    Practice = (PracticeChoice)practice.SelectedIndex, AutoVoice = autoVoice.Checked, Voice = voice.Text.Trim(), EnglishVoice = englishVoice.Text.Trim(),
                    RatePercent = (int)rate.Value, VolumePercent = (int)volume.Value, PitchHz = (int)pitch.Value,
                    AnkiEndpoint = endpoint.Text.Trim(), AnkiDeck = deck.Text.Trim(), AnkiModel = model.Text.Trim(), ApiBaseUrl = api.Text.Trim(), Model = apiModel.Text.Trim() };
                changed.Validate();
                if (string.IsNullOrWhiteSpace(description.Text) || string.IsNullOrWhiteSpace(classification.Text) || string.IsNullOrWhiteSpace(processing.Text)) throw new InvalidOperationException("模式说明和提示词不能为空。");
                if (!string.IsNullOrWhiteSpace(apiKey.Text)) { var credentials = DesktopCredentialSettings.Load(dataRoot); credentials.DeepSeekApiKey = apiKey.Text.Trim(); credentials.Save(dataRoot); }
                Directory.CreateDirectory(flow.ModeOverrides);
                if (description.Text != active.Definition.Description) WorkflowJson.Save(Path.Combine(flow.ModeOverrides, "modes.json"), new ModeFile(modeFile.Modes.Select(m => m.Id == active.Definition.Id ? m with { Description = description.Text } : m).ToArray()));
                if (classification.Text != config.ClassificationPrompt) File.WriteAllText(Path.Combine(flow.ModeOverrides, "classification.md"), classification.Text);
                if (processing.Text != active.Prompt) File.WriteAllText(Path.Combine(flow.ModeOverrides, active.Definition.PromptFile), processing.Text);
                flow.SaveSettings(changed); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { notice.Text = "未保存：" + ex.Message; }
        };
        Controls.Add(root);
    }
}

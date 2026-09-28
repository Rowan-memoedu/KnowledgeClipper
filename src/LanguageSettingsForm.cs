using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed class LanguageSettingsForm : Form
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly StreamingSpeech previewSpeech = new(Path.Combine(AppContext.BaseDirectory, "helpers", "stream_speech.py"), @"D:\Caches\KnowledgeClipper\settings-preview");
    private ClipDraft previewDraft;
    private Action unsubscribeSettings;
    internal Action OpenExcerptDestination { get; set; }

    internal LanguageSettingsForm(LanguageDesktopFlow flow, string dataRoot)
    {
        SuspendLayout();
        Text = "知识剪藏设置"; Name = AccessibleName = "knowledge-clipper.language.settings";
        Width = 800; Height = 720; MinimumSize = new Size(650, 540); Font = new Font("微软雅黑", 10); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // A multiline native TabControl moves the selected row next to its page.
        // Keep one header row so switching sections never reorders or shifts them.
        var tabs = new TabControl { Dock = DockStyle.Fill, Multiline = false,
            Name = "knowledge-clipper.settings.sections", AccessibleName = "设置分区" }; root.Controls.Add(tabs, 0, 0);
        var tables = new List<TableLayoutPanel>();
        TableLayoutPanel table = null;
        int row = 0;
        TabPage Page(string title, string id)
        {
            var page = new TabPage(title) { AutoScroll = true, Name = "knowledge-clipper.language.page." + id };
            table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(table); tabs.TabPages.Add(page); tables.Add(table); row = 0; return page;
        }
        Page("采集与练习", "capture");
        var value = flow.Settings;
        void Add(string label, Control control) { control.Dock = DockStyle.Top; control.Margin = new Padding(6); control.AccessibleName ??= control.Name; table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(6, 10, 6, 6) }, 0, row); table.Controls.Add(control, 1, row++); }
        TextBox TextField(string text, string id) => new() { Text = text, Name = id, AccessibleName = id };
        var practice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Name = "knowledge-clipper.language.practice" };
        practice.Items.AddRange(new[] { "两者：理解与朗读 + 书写练习", "理解与朗读", "书写练习" });
        practice.SelectedIndex = (int)value.Practice; Add("语言模式默认制卡格式", practice);
        var languageAutoRead = new CheckBox { Text = "点击语言或浮点‘读’后自动朗读", Checked = value.LanguageAutoRead, AutoSize = true,
            Name = "knowledge-clipper.language.auto-read", AccessibleName = "knowledge-clipper.language.auto-read" };
        Add("语言入口", languageAutoRead);
        var typedAutoRead = new CheckBox { Text = "语言 / 翻译输入时自动朗读（语言仍遵循上方开关）", Checked = value.TypedInputAutoRead, AutoSize = true,
            Name = "knowledge-clipper.language.typed-auto-read", AccessibleName = "knowledge-clipper.language.typed-auto-read" };
        Add("文字输入", typedAutoRead);
        var questionCardContext = new CheckBox { Text = "正面附带前序问答", Checked = value.QuestionCardIncludeContext, AutoSize = true,
            Name = "knowledge-clipper.language.question-card-context", AccessibleName = "knowledge-clipper.language.question-card-context" };
        Add("问答卡片", questionCardContext);
        var showDictionary = new CheckBox { Text = "在制卡页显示划词翻译、词性、释义和例句（卡片始终保存该字段）",
            Checked = value.ShowDictionaryInPreview, AutoSize = true, Name = "knowledge-clipper.language.show-dictionary-preview" };
        Add("划词词典", showDictionary);
        var context = new CheckBox { Text = "使用来源上下文", Checked = value.ContextEnabled, AutoSize = true, Name = "knowledge-clipper.language.context-enabled" };
        var granularity = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Name = "knowledge-clipper.language.context-granularity" }; granularity.Items.AddRange(new[] { "一句", "一段" }); granularity.SelectedIndex = value.ContextGranularity == "paragraph" ? 1 : 0;
        Add("上下文", context); Add("上下文范围", granularity);
        Page("浮点", "floating");
        var globalSelection = new CheckBox { Text = "选中文字时显示浮点", Checked = value.GlobalSelection, AutoSize = true,
            Name = "knowledge-clipper.language.global-selection", AccessibleName = "knowledge-clipper.language.global-selection" };
        Add("文字浮点", globalSelection);
        var clipboardImage = new CheckBox { Text = "剪贴板加入图片时显示浮点；悬停保持，移开 2 秒后隐藏", Checked = value.ClipboardImageDotEnabled, AutoSize = true,
            Name = "knowledge-clipper.floating.clipboard-image", AccessibleName = "knowledge-clipper.floating.clipboard-image" };
        Add("图片浮点", clipboardImage);
        Add("快捷模式", new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "只用于点击浮点：后台生成并导入，成功时不打开窗口；失败或需要处理时打开原页面。各路径可分别开关。" });
        CheckBox Quick(string id, string text, bool enabled) => new() { Text = text, Checked = enabled, AutoSize = true,
            Name = "knowledge-clipper.floating.quick-" + id, AccessibleName = "knowledge-clipper.floating.quick-" + id };
        var quickLanguage = Quick("language", "按默认格式生成并导入；原图只用理解与朗读", value.QuickLanguage); Add("读 · 语言", quickLanguage);
        var quickQuestion = Quick("question", "生成本次回答并导入，不展开追问", value.QuickQuestion); Add("问 · 提问", quickQuestion);
        var quickExcerpt = Quick("excerpt", "直接导入本次原文或单张图片，不生成其他卡片", value.QuickExcerpt); Add("摘 · 摘录", quickExcerpt);
        Add("图 · 多图", new Label { AutoSize = true, ForeColor = Color.DimGray, Name = "knowledge-clipper.floating.multiple-images",
            Text = "始终打开多图模式，可继续收集正面和反面图片。" });
        Add("", new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "读、问、摘的快捷开关同时作用于文字和图片浮点，只处理当前选区或单张图片。导出对象沿用各模式的设置；主窗口操作仍进入正常页面。" });
        ComboBox RouteChoice(string id, string selected, MaterialRoute[] routes, bool punctuation = false)
        {
            var box = new ComboBox { Name = "knowledge-clipper.routing." + id, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
            box.Items.Add(new MaterialRoute(MaterialRouting.Ask, "Ask：每次选择"));
            if (punctuation) box.Items.Add(new MaterialRoute(MaterialRouting.Punctuation, "问号结尾提问，否则翻译"));
            box.Items.AddRange(routes.Cast<object>().ToArray());
            box.SelectedItem = box.Items.Cast<MaterialRoute>().FirstOrDefault(r => r.Id == selected) ?? box.Items[0];
            return box;
        }
        Page("默认路径与图片", "routing");
        var typedRoute = RouteChoice("typed", value.TypedDefaultRoute, MaterialRouting.Choices(MaterialKind.TypedText), true); Add("文字输入默认路径", typedRoute);
        var ocrRoute = RouteChoice("ocr", value.OcrDefaultRoute, MaterialRouting.Choices(MaterialKind.OcrImages)); Add("多图 · OCR 默认路径", ocrRoute);
        ModeSnapshot[] imageModes;
        try { imageModes = ImageModeLoader.Load(string.IsNullOrWhiteSpace(value.ExcerptModesDirectory) ? flow.ModeDefaults : value.ExcerptModesDirectory, flow.ModeOverrides); }
        catch { imageModes = []; }
        var originalRoute = RouteChoice("original", value.OriginalDefaultRoute, MaterialRouting.Choices(MaterialKind.OriginalImages, imageModes)); Add("多图 · 原图默认路径", originalRoute);
        var originalImage = new CheckBox { Text = "保留原图制卡", AutoSize = true, Checked = value.OriginalImageMode == true, Name = "knowledge-clipper.image.original" }; Add("原图模式", originalImage);
        Add("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "截图与图片浮点“图”均进入多图模式，正面、反面分别保留顺序。\n原图有反面图片时直接配对；仅有正面时，可选原图摘录或提示词制卡。\n关闭原图后转为文字，可走摘录、语言或问答；Ask 可在识别期间选择。" });
        var imageModel = TextField(value.ImageModel, "knowledge-clipper.image.model"); Add("图片解答与审核模型", imageModel);
        var mineruScript = TextField(value.MineruScriptPath, "knowledge-clipper.image.mineru-script"); mineruScript.PlaceholderText = "留空使用现有 MinerU 程序"; Add("MinerU 程序路径", mineruScript);
        var mineruLanguage = TextField(value.MineruLanguage, "knowledge-clipper.image.ocr-language"); Add("OCR 语言", mineruLanguage);
        var mineruTable = new CheckBox { Text = "识别表格", AutoSize = true, Checked = value.MineruTable, Name = "knowledge-clipper.image.ocr-table" }; Add("表格", mineruTable);
        var mineruFormula = new CheckBox { Text = "识别公式", AutoSize = true, Checked = value.MineruFormula, Name = "knowledge-clipper.image.ocr-formula" }; Add("公式", mineruFormula);
        var mineruTimeout = Number(value.MineruTimeoutSeconds, 30, 3600, "mineru-timeout"); Add("OCR 超时（秒）", mineruTimeout);
        var speechPage = Page("音色与播放", "speech");
        var autoVoice = new CheckBox { Text = "自动选择音色", AutoSize = true, Checked = value.AutoVoice,
            Name = "knowledge-clipper.language.auto-voice", AccessibleName = "knowledge-clipper.language.auto-voice" }; Add("语音", autoVoice);
        Add("", new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "纯中文：朗读全文。\n含英文：使用英文音色并跳过汉字。\n关闭自动：用指定音色朗读全文。" });
        var voice = new ComboBox { Text = value.Voice, DropDownWidth = 540, Name = "knowledge-clipper.language.voice", AccessibleName = "knowledge-clipper.language.voice" }; Add("中文 / 指定音色", voice);
        var englishVoice = new ComboBox { Text = value.EnglishVoice, DropDownWidth = 540, Name = "knowledge-clipper.language.english-voice", AccessibleName = "knowledge-clipper.language.english-voice" }; Add("英文音色", englishVoice);
        var readVoices = new Button { Text = "读取可用音色", AutoSize = true, Name = "knowledge-clipper.language.read-voices", AccessibleName = "knowledge-clipper.language.read-voices" }; Add("", readVoices);
        var cachedVoices = SpeechVoiceCatalog.ReadCached(value.PythonPath);
        IReadOnlyList<SpeechVoiceOption> availableVoices = cachedVoices?.Voices ?? Array.Empty<SpeechVoiceOption>();
        string VoiceId(ComboBox combo) => combo.SelectedItem is SpeechVoiceOption option && combo.Text == option.ToString() ? option.Id : combo.Text.Trim();
        void FillVoices(ComboBox combo, IEnumerable<SpeechVoiceOption> options)
        {
            var selected = SpeechVoiceCatalog.NormalizeVoiceId(VoiceId(combo));
            combo.Items.Clear(); combo.Items.AddRange(options.Cast<object>().ToArray());
            var found = combo.Items.Cast<SpeechVoiceOption>().FirstOrDefault(v => v.Id == selected);
            if (found != null) combo.SelectedItem = found;
            else combo.Text = selected;
        }
        void RefreshVoiceChoices()
        {
            FillVoices(voice, availableVoices.Where(v => !autoVoice.Checked || v.Id == "Mia" || v.Locale.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)));
            FillVoices(englishVoice, availableVoices.Where(v => v.Locale.StartsWith("en-", StringComparison.OrdinalIgnoreCase)));
        }
        autoVoice.CheckedChanged += (_, _) => RefreshVoiceChoices();
        RefreshVoiceChoices();
        var voiceStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Name = "knowledge-clipper.language.voice-status",
            Text = cachedVoices == null ? "进入本页会自动读取音色；也可手动填写音色 ID。" : "已载入 " + cachedVoices.ObservedAt.ToLocalTime().ToString("MM-dd HH:mm") + " 的音色列表，可随时刷新。" };
        Add("", voiceStatus);
        NumericUpDown Number(int n, int min, int max, string id) => new() { Minimum = min, Maximum = max, Value = n, Name = "knowledge-clipper.language." + id };
        var rate = Number(value.RatePercent, -50, 100, "rate"); Add("速度变化 %", rate);
        var volume = Number(value.VolumePercent, -100, 100, "volume"); Add("音量变化 %", volume);
        var pitch = Number(0, 0, 0, "pitch"); pitch.Enabled = false; Add("MiMo 音高固定", pitch);
        var speechAutoPlay = new CheckBox { Text = "合成后自动播放", AutoSize = true, Checked = value.SpeechAutoPlay, Name = "knowledge-clipper.language.speech-auto-play" }; Add("自动播放", speechAutoPlay);
        Add("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "关闭后仍保存音频，可在预览或历史记录中点击播放。试听和句子播放按钮立即播放。" });
        var previewChinese = new Button { Text = "试听中文 / 指定音色", AutoSize = true, Name = "knowledge-clipper.language.preview-voice" };
        var previewEnglish = new Button { Text = "试听英文音色", AutoSize = true, Name = "knowledge-clipper.language.preview-english-voice" };
        var stopPreview = new Button { Text = "停止试听", AutoSize = true, Name = "knowledge-clipper.language.stop-preview" };
        Add("", previewChinese); Add("", previewEnglish); Add("", stopPreview);
        Page("Anki 导入", "anki");
        var endpoint = TextField(value.AnkiEndpoint, "knowledge-clipper.language.anki-endpoint"); Add("AnkiConnect", endpoint);
        var deck = new ComboBox { Text = value.AnkiRootDeck ?? value.AnkiDeck, Name = "knowledge-clipper.language.deck", AccessibleName = "knowledge-clipper.language.deck" }; Add("Anki 根牌组", deck);
        var model = new ComboBox { Text = value.AnkiModel, Name = "knowledge-clipper.language.note-type", AccessibleName = "knowledge-clipper.language.note-type" }; Add("语言笔记类型", model);
        var effectiveModel = new Label { AutoSize = true, ForeColor = Color.DimGray };
        void ShowModel() => effectiveModel.Text = "语言卡使用：" + PublishedLanguage.DictionaryModelName(model.Text.Trim()) + "\n首次导入时创建，已有卡片保留原类型。";
        model.TextChanged += (_, _) => ShowModel(); ShowModel(); Add("", effectiveModel);
        var readAnki = new Button { Text = "读取 Anki 牌组与类型", AutoSize = true, Name = "knowledge-clipper.language.read-anki", AccessibleName = "knowledge-clipper.language.read-anki" }; Add("", readAnki);
        var excerptDeck = TextField(value.ExcerptDeckPath, "knowledge-clipper.excerpt.deck-path"); Add("摘录子牌组", excerptDeck);
        Page("内容处理", "content");
        var excerptModes = TextField(value.ExcerptModesDirectory, "knowledge-clipper.excerpt.modes-directory"); Add("摘录与图片提示词目录", excerptModes);
        Add("", new Label { Text = "目录留空使用程序 modes；可指定本地提示词工作目录，在摘录页面点击‘刷新模式’生效。\n原文与手动制卡不使用提示词。", AutoSize = true, ForeColor = Color.DimGray });
        var api = TextField(value.ApiBaseUrl, "knowledge-clipper.language.api-url"); Add("内容 API", api);
        var apiModel = TextField(value.Model, "knowledge-clipper.language.api-model"); Add("内容模型", apiModel);
        var apiKey = TextField("", "knowledge-clipper.language.api-key"); apiKey.UseSystemPasswordChar = true; apiKey.PlaceholderText = "留空保留现有密钥"; Add("API Key", apiKey);
        var contentTimeout = Number(value.ContentTimeoutSeconds, 1, 600, "content-timeout"); Add("单次超时（秒）", contentTimeout);
        var contentRetries = Number(value.ContentMaxRetries, 0, 5, "content-retries"); Add("失败重试次数", contentRetries);
        Add("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "重试次数不含首次请求；0 表示不自动重试。取消操作和身份验证失败不会重试。" });
        Page("运行程序", "runtime");
        var python = TextField(value.PythonPath, "knowledge-clipper.language.python-path"); Add("语音与 OCR Python", python);
        var node = TextField(value.NodePath, "knowledge-clipper.language.node-path"); Add("取词 Node.js", node);
        var ffplay = TextField(value.FfplayPath, "knowledge-clipper.language.ffplay-path"); Add("音频播放器", ffplay);
        Add("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "使用已安装程序的完整路径。修改保存后用于后续操作；不移动或重新安装程序。" });
        var config = ModeConfigurationLoader.Load(flow.ModeDefaults, flow.ModeOverrides);
        var definitions = ModeConfigurationLoader.LoadDefinitions(flow.ModeDefaults, flow.ModeOverrides);
        Page("快捷键", "hotkeys");
        var excerptHotkey = TextField(value.OriginalExcerptHotkey, "knowledge-clipper.excerpt.destination-hotkey");
        excerptHotkey.PlaceholderText = "Alt+Shift+《；留空关闭";
        Add("RemNote 导入目标", excerptHotkey);
        var replayHotkey = TextField(value.ReplayLastHotkey, "knowledge-clipper.language.replay-last-hotkey");
        replayHotkey.PlaceholderText = "Alt+Shift+/；留空关闭";
        Add("重读上一条", replayHotkey);
        Add("作用范围", new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "全局生效，无需切回剪藏窗口。导入目标快捷键打开原文摘录目录面板；重读上一条复用最近一条语言音频，不重新生成。" });
        Add("按键规则", new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "格式：Ctrl / Alt / Shift + 主键，至少包含 Ctrl 或 Alt。支持标点键、Space、字母、数字和 F1–F12。\n《、<、, 都指逗号键；/ 和 ? 都指斜杠键。修饰键按配置显式指定，不依赖输入法。\n留空关闭该动作；两个动作不可重复。保存后立即生效，系统占用会在主窗口提示，托盘入口始终可用。" });
        var resetHotkeys = new Button { Text = "恢复默认快捷键", AutoSize = true, Name = "knowledge-clipper.hotkeys.reset" };
        resetHotkeys.Click += (_, _) => { excerptHotkey.Text = "Alt+Shift+《"; replayHotkey.Text = "Alt+Shift+/"; };
        Add("", resetHotkeys);
        Page("导出对象", "destinations");
        var remNoteEndpoint = TextField(RemNoteConnection.ResolveEndpoint(value.RemNoteSdkEndpoint), "knowledge-clipper.export.sdk-endpoint"); Add("RemNote 插件本机地址", remNoteEndpoint);
        Add("默认规则", new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "语言制卡默认导入 Anki，其他模式默认导入 RemNote。每种模式可单独修改。\n设置用于新材料；已确认导入的内容与目标保持固定。" });
        Add("RemNote 目录", new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = value.RemNoteRootTitle + " → 模式目录。普通内容只有正面时保存为笔记；挖空及正反面内容制卡。" });
        ModeSnapshot[] excerptExportModes;
        try { excerptExportModes = ExcerptModeLoader.Load(string.IsNullOrWhiteSpace(value.ExcerptModesDirectory) ? flow.ModeDefaults : value.ExcerptModesDirectory, flow.ModeOverrides); }
        catch (Exception ex)
        {
            excerptExportModes = [];
            Add("摘录模式", new Label { AutoSize = true, ForeColor = Color.DarkOrange, Text = "提示词模式暂未载入：" + ex.Message });
        }
        var excerptTarget = new Label { AutoSize = true, ForeColor = Color.FromArgb(38, 90, 150),
            Name = "knowledge-clipper.excerpt.current-target" };
        void ShowExcerptTarget() => excerptTarget.Text = "当前目标：" + (flow.Settings.OriginalExcerptDocumentTitle ?? "按日期导入") +
            "\n仅新原文摘录使用；摘录制卡和已确认草稿不受影响。";
        ShowExcerptTarget(); Add("RemNote 目录", excerptTarget);
        var chooseExcerptTarget = new Button { Text = "选择摘录目录…", AutoSize = true,
            Name = "knowledge-clipper.excerpt.choose-destination" };
        chooseExcerptTarget.Click += (_, _) => OpenExcerptDestination?.Invoke();
        Add("", chooseExcerptTarget);
        var destinationEdits = new Dictionary<string, ComboBox>(StringComparer.Ordinal);
        foreach (var definition in definitions.Modes.Concat(ExportRouting.BuiltinModes)
            .Concat(excerptExportModes.Select(mode => mode.Definition)).Concat(imageModes.Select(mode => mode.Definition))
            .GroupBy(mode => mode.Id, StringComparer.Ordinal).Select(group => group.First()))
        {
            var destination = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
                Name = "knowledge-clipper.export.destination." + definition.Id, AccessibleName = "knowledge-clipper.export.destination." + definition.Id };
            destination.Items.AddRange(new object[] { ExportDestination.Anki, ExportDestination.RemNote });
            destination.SelectedItem = ExportRouting.ForMode(value, definition);
            destinationEdits.Add(definition.Id, destination); Add(definition.Name, destination);
        }
        var modeEdits = config.Modes.ToDictionary(m => m.Definition.Id, m => new ModeEdit(m), StringComparer.Ordinal);
        var promptPage = new TabPage("模式与提示词") { AutoScroll = true, Name = "knowledge-clipper.language.page.modes" }; tabs.TabPages.Add(promptPage);
        var prompts = new TableLayoutPanel { Dock = DockStyle.Top, Height = 660, ColumnCount = 1, RowCount = 9, Padding = new Padding(10) };
        prompts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        prompts.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        prompts.RowStyles.Add(new RowStyle(SizeType.AutoSize)); prompts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        TextBox Prompt(string text) => new() { Text = text, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true, WordWrap = true };
        var modeBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(ModeDefinition.Name), Width = 320, DropDownWidth = 480,
            Name = "knowledge-clipper.language.mode", AccessibleName = "knowledge-clipper.language.mode" };
        mode.Items.AddRange(config.Modes.Select(m => (object)m.Definition).ToArray());
        modeBar.Controls.Add(new Label { Text = "选择模式", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }); modeBar.Controls.Add(mode);
        var description = Prompt(""); var processing = Prompt("");
        description.Name = description.AccessibleName = "knowledge-clipper.language.mode-description";
        processing.Name = processing.AccessibleName = "knowledge-clipper.language.processing-prompt";
        var followUp = new CheckBox { Text = "本模式默认展开追问", AutoSize = true,
            Name = "knowledge-clipper.language.follow-up-default", AccessibleName = "knowledge-clipper.language.follow-up-default" };
        var deckPath = new ComboBox { Name = "knowledge-clipper.language.mode-deck-path", AccessibleName = "knowledge-clipper.language.mode-deck-path", Width = 320, DropDownWidth = 480 };
        var modeDeckBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var refreshModeDecks = new Button { Text = "读取牌组", AutoSize = true, Name = "knowledge-clipper.language.read-mode-decks", AccessibleName = "knowledge-clipper.language.read-mode-decks" };
        var createModeDeck = new Button { Text = "新建此子牌组", AutoSize = true, Name = "knowledge-clipper.language.create-mode-deck", AccessibleName = "knowledge-clipper.language.create-mode-deck" };
        modeDeckBar.Controls.AddRange(new Control[] { deckPath, refreshModeDecks, createModeDeck });
        var modeIssue = new Label { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(600, 0),
            Name = "knowledge-clipper.language.mode-issue", AccessibleName = "knowledge-clipper.language.mode-issue" };
        modeBar.Controls.Add(modeIssue);
        var deckHelp = new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "从列表选择根牌组下真实存在的子牌组，或填写相对子路径，用 :: 分层。每个具体模式独立保存。新建只创建当前指定子牌组，不移动已有卡片。" };
        void ShowDeckTarget() => deckHelp.Text = "当前目标：" + deck.Text.Trim() + "::" + deckPath.Text.Trim() +
            "\n从列表选择真实子牌组，或填写相对子路径，用 :: 分层。每个模式独立保存；新建只创建此目标，不移动已有卡片。";
        deckPath.TextChanged += (_, _) => ShowDeckTarget();
        string[] availableDecks = Array.Empty<string>();
        string deckCacheEndpoint = null;
        void RefreshModeDeckChoices()
        {
            var current = deckPath.Text;
            var prefix = deck.Text.Trim() + "::";
            deckPath.Items.Clear();
            deckPath.Items.AddRange(availableDecks.Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
                .Select(d => d[prefix.Length..]).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToArray());
            deckPath.Text = current;
            ShowDeckTarget();
        }
        deck.TextChanged += (_, _) => RefreshModeDeckChoices();
        endpoint.TextChanged += (_, _) =>
        {
            availableDecks = Array.Empty<string>(); deckCacheEndpoint = null;
            var current = deck.Text; deck.Items.Clear(); deck.Text = current; RefreshModeDeckChoices();
        };
        string selectedModeId = null;
        string PromptPath(ModeDefinition definition) => Path.GetFullPath(Path.Combine(flow.ModeOverrides, definition.PromptFile));
        void RememberMode()
        {
            if (selectedModeId == null) return;
            var edit = modeEdits[selectedModeId];
            edit.Description = description.Text; edit.FollowUpDefault = followUp.Checked; edit.DeckPath = deckPath.Text.Trim();
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
            description.Text = edit.Description; processing.Text = edit.Prompt; followUp.Checked = edit.FollowUpDefault; deckPath.Text = edit.DeckPath;
            modeIssue.Text = edit.Original.LoadError is { } error ? "本模式尚未配置：" + error + "。仍可保存常用设置与牌组。" : "";
            RefreshModeDeckChoices();
        };
        prompts.Controls.Add(modeBar, 0, 0);
        prompts.Controls.Add(new Label { Text = "模式说明", AutoSize = true }, 0, 1); prompts.Controls.Add(description, 0, 2);
        prompts.Controls.Add(followUp, 0, 3);
        prompts.Controls.Add(new Label { Text = "Anki 子牌组路径", AutoSize = true }, 0, 4); prompts.Controls.Add(modeDeckBar, 0, 5); prompts.Controls.Add(deckHelp, 0, 6);
        prompts.Controls.Add(new Label { Text = "本模式处理提示词", AutoSize = true }, 0, 7); prompts.Controls.Add(processing, 0, 8);
        promptPage.SizeChanged += (_, _) => {
            prompts.Height = Math.Max(660, promptPage.ClientSize.Height);
            deckHelp.MaximumSize = modeIssue.MaximumSize = new Size(Math.Max(250, promptPage.ClientSize.Width - 44), 0);
        };
        promptPage.Controls.Add(prompts); mode.SelectedIndex = 0;
        var notice = new Label { AutoSize = true, MaximumSize = new Size(750, 0), Name = "knowledge-clipper.language.settings-notice", Text = "浮点开关保存后立即生效，其余设置用于下一次操作。导入目标以‘导出对象’页为准；已有导入记录保持不变。" }; root.Controls.Add(notice, 0, 1);
        async Task RefreshAnkiAsync(bool includeModels)
        {
            var targetEndpoint = endpoint.Text.Trim();
            var proposed = value with { AnkiEndpoint = targetEndpoint }; proposed.Validate();
            using var http = new HttpClient(); var anki = new AnkiConnection(http, targetEndpoint);
            var decks = await anki.DecksAsync(lifetime.Token);
            var models = includeModels ? await anki.ModelsAsync(lifetime.Token) : null;
            if (IsDisposed || targetEndpoint != endpoint.Text.Trim()) return;
            availableDecks = decks; deckCacheEndpoint = targetEndpoint;
            var oldDeck = deck.Text; deck.Items.Clear(); deck.Items.AddRange(decks); deck.Text = oldDeck;
            if (models != null) { var oldModel = model.Text; model.Items.Clear(); model.Items.AddRange(models); model.Text = oldModel; }
            RefreshModeDeckChoices();
            notice.Text = "已读取 Anki：根牌组与各模式共用这份列表，可选择多层子牌组。";
        }
        async void ReadAnki(object sender, EventArgs args)
        {
            readAnki.Enabled = refreshModeDecks.Enabled = createModeDeck.Enabled = false;
            try { await RefreshAnkiAsync(true); }
            catch (Exception ex) { if (!IsDisposed) notice.Text = "读取未完成：" + ex.Message; }
            finally { if (!IsDisposed) readAnki.Enabled = refreshModeDecks.Enabled = createModeDeck.Enabled = true; }
        }
        readAnki.Click += ReadAnki;
        refreshModeDecks.Click += ReadAnki;
        createModeDeck.Click += async (_, _) =>
        {
            readAnki.Enabled = refreshModeDecks.Enabled = createModeDeck.Enabled = false;
            try
            {
                var rootDeck = deck.Text.Trim(); var relative = deckPath.Text.Trim();
                DeckRouting.ValidateRoot(rootDeck);
                if (selectedModeId == null || string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("请先选择模式并填写子牌组路径。");
                DeckRouting.ValidatePattern(relative, modeEdits[selectedModeId].Original.Definition.OutputKind);
                if (relative.Contains('{') || relative.Contains('}')) throw new InvalidOperationException("新建时请填写具体子牌组名称，不能包含占位符。");
                var targetEndpoint = endpoint.Text.Trim();
                (value with { AnkiEndpoint = targetEndpoint }).Validate();
                var fullDeck = rootDeck + "::" + relative;
                using var http = new HttpClient(); var anki = new AnkiConnection(http, targetEndpoint);
                if (deckCacheEndpoint != targetEndpoint || !availableDecks.Contains(fullDeck, StringComparer.Ordinal))
                    await anki.CallAsync("createDeck", new { deck = fullDeck }, lifetime.Token);
                await RefreshAnkiAsync(false);
                if (!IsDisposed) notice.Text = "子牌组已可选择：" + fullDeck;
            }
            catch (Exception ex) { if (!IsDisposed) notice.Text = "新建未完成：" + ex.Message; }
            finally { if (!IsDisposed) readAnki.Enabled = refreshModeDecks.Enabled = createModeDeck.Enabled = true; }
        };
        async Task ReadVoicesAsync()
        {
            if (!readVoices.Enabled) return;
            readVoices.Enabled = false;
            notice.Text = "正在读取可用音色，当前选择已保留…";
            try
            {
                var requestedPython = python.Text.Trim();
                var discovered = await SpeechVoiceCatalog.DiscoverAsync(requestedPython, lifetime.Token);
                if (IsDisposed) return;
                if (requestedPython != python.Text.Trim()) return;
                availableVoices = discovered; RefreshVoiceChoices();
                voiceStatus.Text = "已从语音服务读取 " + discovered.Count + " 个音色，列表已缓存。";
                notice.Text = "已读取可用音色；关闭自动选择后，中文 / 指定音色可选择其他语种。";
            }
            catch (Exception ex) { if (!IsDisposed) voiceStatus.Text = notice.Text = "音色读取未完成，原有列表与选择已保留：" + ex.Message; }
            finally { if (!IsDisposed) readVoices.Enabled = true; }
        }
        readVoices.Click += async (_, _) => await ReadVoicesAsync();
        tabs.SelectedIndexChanged += async (_, _) => { if (tabs.SelectedTab == speechPage && availableVoices.Count == 0) await ReadVoicesAsync(); };
        python.TextChanged += (_, _) => { availableVoices = Array.Empty<SpeechVoiceOption>(); RefreshVoiceChoices(); };
        async Task PreviewAsync(bool english)
        {
            previewChinese.Enabled = previewEnglish.Enabled = false;
            try
            {
                var previewSettings = value with { AutoVoice = false, Voice = VoiceId(english ? englishVoice : voice),
                    PythonPath = python.Text.Trim(), FfplayPath = ffplay.Text.Trim(), RatePercent = (int)rate.Value,
                    VolumePercent = (int)volume.Value, PitchHz = (int)pitch.Value, SpeechAutoPlay = true };
                previewSettings.Validate();
                var text = english ? "The voice settings are ready." : "你好，这是当前音色的试听。";
                var material = new CaptureMaterial(text, null, "unavailable", null, "设置试听", 0, DateTimeOffset.UtcNow);
                previewDraft = new ClipDraft { Original = material, Material = material, Settings = previewSettings, Configuration = config };
                notice.Text = "正在试听本页尚未保存的音色与播放参数…";
                await previewSpeech.SpeakAsync(previewDraft, false, () => { });
                if (!IsDisposed) notice.Text = previewDraft.SpeechError == null ? "试听结束；设置仍需点击保存。" : "试听未完成：" + previewDraft.SpeechError;
            }
            catch (Exception ex) { if (!IsDisposed) notice.Text = "试听未完成：" + ex.Message; }
            finally { if (!IsDisposed) previewChinese.Enabled = previewEnglish.Enabled = true; }
        }
        previewChinese.Click += async (_, _) => await PreviewAsync(false);
        previewEnglish.Click += async (_, _) => await PreviewAsync(true);
        stopPreview.Click += (_, _) => previewSpeech.Stop();
        var save = new Button { Text = "保存", AutoSize = true, Height = 36, Name = "knowledge-clipper.language.save-settings", AccessibleName = "knowledge-clipper.language.save-settings" };
        root.Controls.Add(save, 0, 2);
        save.Click += (_, _) =>
        {
            try
            {
                var modeDestinations = new Dictionary<string, ExportDestination>(value.ModeDestinations ?? new(), StringComparer.Ordinal);
                foreach (var edit in destinationEdits) modeDestinations[edit.Key] = (ExportDestination)edit.Value.SelectedItem;
                var changed = value with { GlobalSelection = globalSelection.Checked, LanguageAutoRead = languageAutoRead.Checked, TypedInputAutoRead = typedAutoRead.Checked,
                    ClipboardImageDotEnabled = clipboardImage.Checked, QuickLanguage = quickLanguage.Checked, QuickQuestion = quickQuestion.Checked,
                    QuickExcerpt = quickExcerpt.Checked,
                    OriginalExcerptDocumentId = flow.Settings.OriginalExcerptDocumentId,
                    OriginalExcerptDocumentTitle = flow.Settings.OriginalExcerptDocumentTitle,
                    OriginalExcerptHotkey = excerptHotkey.Text.Trim(),
                    ReplayLastHotkey = replayHotkey.Text.Trim(),
                    ModeDestinations = modeDestinations, RemNoteSdkEndpoint = remNoteEndpoint.Text.Trim(),
                    ExcerptDeckPath = excerptDeck.Text.Trim(), ExcerptModesDirectory = excerptModes.Text.Trim(),
                    TypedDefaultRoute = ((MaterialRoute)typedRoute.SelectedItem).Id, OcrDefaultRoute = ((MaterialRoute)ocrRoute.SelectedItem).Id,
                    OriginalDefaultRoute = ((MaterialRoute)originalRoute.SelectedItem).Id, OriginalImageMode = originalImage.Checked,
                    ImageModel = imageModel.Text.Trim(), MineruScriptPath = mineruScript.Text.Trim(), MineruLanguage = mineruLanguage.Text.Trim(),
                    MineruTable = mineruTable.Checked, MineruFormula = mineruFormula.Checked, MineruTimeoutSeconds = (int)mineruTimeout.Value,
                    QuestionCardIncludeContext = questionCardContext.Checked,
                    ShowDictionaryInPreview = showDictionary.Checked,
                    ContextEnabled = context.Checked, ContextGranularity = granularity.SelectedIndex == 1 ? "paragraph" : "sentence",
                    Practice = (PracticeChoice)practice.SelectedIndex, AutoVoice = autoVoice.Checked, Voice = VoiceId(voice), EnglishVoice = VoiceId(englishVoice),
                    SpeechAutoPlay = speechAutoPlay.Checked, ContentTimeoutSeconds = (int)contentTimeout.Value, ContentMaxRetries = (int)contentRetries.Value,
                    PythonPath = python.Text.Trim(), NodePath = node.Text.Trim(), FfplayPath = ffplay.Text.Trim(),
                    RatePercent = (int)rate.Value, VolumePercent = (int)volume.Value, PitchHz = (int)pitch.Value,
                    AnkiEndpoint = endpoint.Text.Trim(), AnkiRootDeck = deck.Text.Trim(), AnkiDeck = deck.Text.Trim(), AnkiModel = model.Text.Trim(), ApiBaseUrl = api.Text.Trim(), Model = apiModel.Text.Trim() };
                changed.Validate();
                foreach (var path in new[] { (changed.PythonPath, value.PythonPath), (changed.NodePath, value.NodePath), (changed.FfplayPath, value.FfplayPath) })
                    if (path.Item1 != path.Item2 && !File.Exists(path.Item1)) throw new InvalidOperationException("运行程序不存在：" + path.Item1);
                if (string.IsNullOrWhiteSpace(changed.Voice) || string.IsNullOrWhiteSpace(changed.EnglishVoice))
                    throw new InvalidOperationException("请选择或填写中文与英文音色。");
                RememberMode();
                if (modeEdits.Values.Any(m => (m.Description != m.Original.Definition.Description && string.IsNullOrWhiteSpace(m.Description)) ||
                        (m.Prompt != m.Original.Prompt && string.IsNullOrWhiteSpace(m.Prompt))))
                    throw new InvalidOperationException("模式说明和提示词不能为空。");
                foreach (var edit in modeEdits.Values) DeckRouting.ValidatePattern(edit.DeckPath, edit.Original.Definition.OutputKind);
                var promptWrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var edit in modeEdits.Values.Where(m => m.Prompt != m.Original.Prompt))
                {
                    var path = PromptPath(edit.Original.Definition);
                    if (promptWrites.TryGetValue(path, out var text) && text != edit.Prompt)
                        throw new InvalidOperationException("多个模式共用同一提示词文件，但编辑内容不同，请先调整模式文件配置。");
                    promptWrites[path] = edit.Prompt;
                }
                if (!string.IsNullOrWhiteSpace(apiKey.Text)) {
                    SecretConfig.SetDeepSeek(apiKey.Text.Trim());
                }
                Directory.CreateDirectory(flow.ModeOverrides);
                if (modeEdits.Values.Any(m => m.Description != m.Original.Definition.Description || m.FollowUpDefault != m.Original.Definition.FollowUpDefault || m.DeckPath != DeckRouting.PathFor(m.Original.Definition)))
                {
                    var effective = modeEdits.Values.ToDictionary(m => m.Original.Definition.Id,
                        m => m.Original.Definition with { Description = m.Description, FollowUpDefault = m.FollowUpDefault, AnkiDeckPath = m.DeckPath }, StringComparer.Ordinal);
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
        var fittingText = false;
        void FitText()
        {
            if (fittingText || IsDisposed) return;
            fittingText = true;
            try
            {
                foreach (var section in tables)
                {
                    var columns = section.GetColumnWidths();
                    if (columns.Length < 2) continue;
                    var width = Math.Max(120, section.ClientSize.Width - section.Padding.Horizontal - columns[0] - 12);
                    foreach (Control control in section.Controls)
                    {
                        if (section.GetColumn(control) != 1 || control is not (CheckBox or Label)) continue;
                        if (control.MaximumSize.Width != width) control.MaximumSize = new Size(width, 0);
                        if (control is CheckBox check)
                        {
                            // WinForms measures auto-sized checkboxes without word wrapping.
                            // Reserve the wrapped height while retaining native checkbox painting.
                            var textWidth = Math.Max(1, width - check.Font.Height);
                            var textHeight = TextRenderer.MeasureText(check.Text, check.Font, new Size(textWidth, int.MaxValue),
                                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                            var minimum = new Size(0, textHeight + (int)Math.Ceiling(4.0 * DeviceDpi / 96));
                            if (check.MinimumSize != minimum) check.MinimumSize = minimum;
                        }
                    }
                }
                var noticeWidth = Math.Max(250, root.ClientSize.Width - root.Padding.Horizontal - notice.Margin.Horizontal);
                if (notice.MaximumSize.Width != noticeWidth) notice.MaximumSize = new Size(noticeWidth, 0);
            }
            finally { fittingText = false; }
        }
        foreach (var section in tables) section.SizeChanged += (_, _) => FitText();
        tabs.SelectedIndexChanged += (_, _) => FitText();
        root.SizeChanged += (_, _) => FitText();
        Load += (_, _) => FitText();
        var observedSettings = value;
        void SyncExternalSettings()
        {
            if (IsDisposed) return;
            var current = flow.Settings;
            // Only the setting actually changed outside this editor should replace
            // its pending value. Unrelated saves must preserve local edits.
            if (current.GlobalSelection != observedSettings.GlobalSelection)
                globalSelection.Checked = current.GlobalSelection;
            if (current.ClipboardImageDotEnabled != observedSettings.ClipboardImageDotEnabled)
                clipboardImage.Checked = current.ClipboardImageDotEnabled;
            if (current.ContextEnabled != observedSettings.ContextEnabled)
                context.Checked = current.ContextEnabled;
            if (current.OriginalImageMode != observedSettings.OriginalImageMode)
                originalImage.Checked = current.OriginalImageMode == true;
            if (current.OriginalExcerptDocumentId != observedSettings.OriginalExcerptDocumentId) ShowExcerptTarget();
            observedSettings = current;
        }
        flow.SettingsChanged += SyncExternalSettings;
        unsubscribeSettings = () => flow.SettingsChanged -= SyncExternalSettings;
        FormClosed += (_, _) => { unsubscribeSettings?.Invoke(); unsubscribeSettings = null; };
        ResumeLayout(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            unsubscribeSettings?.Invoke(); unsubscribeSettings = null;
            lifetime.Cancel(); lifetime.Dispose(); previewSpeech.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ModeEdit(ModeSnapshot original)
    {
        internal ModeSnapshot Original { get; } = original;
        internal string Description { get; set; } = original.Definition.Description;
        internal string Prompt { get; set; } = original.Prompt;
        internal bool FollowUpDefault { get; set; } = original.Definition.FollowUpDefault;
        internal string DeckPath { get; set; } = DeckRouting.PathFor(original.Definition);
    }
}

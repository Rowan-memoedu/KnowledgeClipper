using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Application;
using KnowledgeClipper.Desktop.Core;
using KnowledgeClipper.Desktop.Application.V3;
using System.Threading;

namespace KnowledgeClipper
{
    internal static class DesktopSemanticActionTrace
    {
        private static readonly object Gate = new object();

        internal static string PathFor(string workDirectory)
        {
            return Path.Combine(workDirectory, "diagnostics", "desktop-semantic-actions.v1.jsonl");
        }

        internal static void Append(string path, string actionId, string transition, string reason = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var row = JsonSerializer.Serialize(new
                {
                    schemaVersion = "knowledge-clipper-desktop-semantic-action-trace.v1",
                    observedAtUtc = DateTime.UtcNow.ToString("o"),
                    processId = Process.GetCurrentProcess().Id,
                    managedThreadId = Environment.CurrentManagedThreadId,
                    actionId = actionId ?? "",
                    transition,
                    reason
                });
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, row + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Diagnostic evidence must never change the command outcome.
            }
        }
    }

    internal sealed class SemanticActionButton : Button
    {
        private Func<Task> command;
        private string tracePath;
        private int commandScheduled;

        internal void Bind(Func<Task> value, string diagnosticTracePath)
        {
            command = value ?? throw new ArgumentNullException("value");
            tracePath = diagnosticTracePath ?? throw new ArgumentNullException("diagnosticTracePath");
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            DesktopSemanticActionTrace.Append(tracePath, Name, "invoke_received");
            ScheduleCommand();
        }

        internal void ScheduleCommand()
        {
            if (command == null) { DesktopSemanticActionTrace.Append(tracePath, Name, "early_return_reason", "command-null"); return; }
            if (IsDisposed) { DesktopSemanticActionTrace.Append(tracePath, Name, "early_return_reason", "disposed"); return; }
            if (!IsHandleCreated) { DesktopSemanticActionTrace.Append(tracePath, Name, "early_return_reason", "handle-not-created"); return; }
            if (System.Threading.Interlocked.CompareExchange(ref commandScheduled, 1, 0) != 0) { DesktopSemanticActionTrace.Append(tracePath, Name, "early_return_reason", "in-flight"); return; }
            MethodInvoker invoke = async delegate
            {
                DesktopSemanticActionTrace.Append(tracePath, Name, "dispatch_started");
                try
                {
                    DesktopSemanticActionTrace.Append(tracePath, Name, "handler_entered");
                    await command();
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref commandScheduled, 0);
                    DesktopSemanticActionTrace.Append(tracePath, Name, "reset");
                }
            };
            try
            {
                BeginInvoke(invoke);
                DesktopSemanticActionTrace.Append(tracePath, Name, "dispatch_queued");
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref commandScheduled, 0);
                DesktopSemanticActionTrace.Append(tracePath, Name, "reset", "dispatch-queue-failed");
                throw;
            }
        }

    }

    internal sealed partial class SettingsForm : Form
    {
        private static readonly Color Canvas = Color.FromArgb(246, 248, 251);
        private static readonly Color Ink = Color.FromArgb(31, 42, 55);
        private static readonly Color Muted = Color.FromArgb(93, 107, 122);
        private static readonly Color Accent = Color.FromArgb(55, 112, 222);

        private readonly AppConfig config;
        private readonly UserSettings initial;
        private readonly DesktopCredentialSettings initialCredentials;
        private readonly DesktopWorkflowSettingsService settingsService;
        private readonly DesktopModuleSettingsStore moduleSettingsStore;
        private readonly DesktopSpeechCatalogCacheStore speechCatalogCacheStore;
        private readonly DesktopContentSettingsHost contentSettingsHost;
        private readonly DesktopModuleCapabilityHost capabilityHost;
        private readonly ContentSettingsDescriptorView contentDescriptor;
        private readonly SpeechSettingsDescriptorView speechDescriptor;
        private readonly DesktopAnkiProbeHost ankiProbeHost;
        private readonly DesktopCompositionRoot typedApplication;
        private CancellationTokenSource ankiProbeCancellation;
        private int ankiProbeInFlight;
        private ComboBox ankiProbeRoute;
        internal const string AnkiProbeRouteId = "knowledge-clipper.desktop.settings.export.anki.probe-route";
        private readonly DesktopModuleActionHost moduleActionHost;
        private DesktopModuleSettingsDocument moduleSettings;
        private readonly ProcessAudio previewAudio;
        private readonly ListBox navigation;
        private readonly Panel pageHost;
        private Label status;
        private readonly Dictionary<string, Control> pages = new Dictionary<string, Control>(StringComparer.Ordinal);

        private SettingsDraft settingsDraft;
        private WorkflowSettingsDocument workflowDocument
        {
            get { return settingsDraft.Document; }
            set { InvalidateRemNoteObservation(); settingsDraft.Replace(value); }
        }
        private bool changingProfile;
        private ComboBox workflowSelector, processingProfile, ankiDeckPreset;
        private ComboBox languageOverride, chinese, english, spanish, japanese, korean, contextGranularity, ocrModel, ocrLanguage, remnoteSink, promptVersion, contentDefaultChoice, contentModel;
        private CheckBox contextMode, originalImageMode, speechAutoPlay, remnoteDestination, ankiDestination, ocrTable, ocrFormula, contentPromptAfterRecognition;
        private NumericUpDown rate, volume, pitch, ocrTimeout, ocrPoll, contentMaxRetries;
        private TextBox deepSeekCredential, mineruCredential, ocrScript, remnoteRootId, remnoteRootTitle, ankiProfile, ankiEndpoint, ankiDeckRoutes, ankiProbeStatus, ankiInitializeStatus, speechVoiceCatalogAvailability, speechPreviewAvailability, remnoteProbeStatus, remnoteDiscoverTargetStatus, remnoteBoundTargetStatus;
        private string initialDeepSeekEnvironment;
        private Button promptCopy, promptOpen, promptValidate, promptDiff, ankiProbe, ankiInitialize, voiceDiscovery, speechPreview, remnoteProbe, remnoteDiscoverTarget;
        private string speechCatalogJson;

        public UserSettings SavedSettings { get; private set; }

        internal static readonly string[] PageKeys =
        {
            "capture", "ocr", "content", "question", "speech", "export", "diagnostics"
        };

        internal static readonly string[] PageTitles =
        {
            "桌面与采集", "图像 / OCR", "内容处理", "提问与制卡", "语音与播放", "导出", "运行与诊断"
        };

        public SettingsForm(AppConfig config, UserSettings current)
            : this(LegacyDesktopAdapter.CreateSettingsDependencies(config, current, null, null, null, null)) { }

        internal SettingsForm(AppConfig config, UserSettings current, DesktopModuleCapabilityHost injectedCapabilityHost)
            : this(LegacyDesktopAdapter.CreateSettingsDependencies(config, current, injectedCapabilityHost, null, null, null)) { }

        internal SettingsForm(AppConfig config, UserSettings current, DesktopModuleCapabilityHost injectedCapabilityHost, DesktopAnkiProbeHost injectedAnkiProbeHost)
            : this(LegacyDesktopAdapter.CreateSettingsDependencies(config, current, injectedCapabilityHost, injectedAnkiProbeHost, null, null)) { }

        internal SettingsForm(AppConfig config, UserSettings current, DesktopModuleCapabilityHost injectedCapabilityHost, DesktopAnkiProbeHost injectedAnkiProbeHost, DesktopModuleActionHost injectedModuleActionHost)
            : this(LegacyDesktopAdapter.CreateSettingsDependencies(config, current, injectedCapabilityHost, injectedAnkiProbeHost, injectedModuleActionHost, null)) { }

        internal SettingsForm(AppConfig config, UserSettings current, DesktopModuleCapabilityHost injectedCapabilityHost, DesktopAnkiProbeHost injectedAnkiProbeHost, DesktopModuleActionHost injectedModuleActionHost, DesktopContentSettingsHost injectedContentSettingsHost)
            : this(LegacyDesktopAdapter.CreateSettingsDependencies(config, current, injectedCapabilityHost, injectedAnkiProbeHost, injectedModuleActionHost, injectedContentSettingsHost)) { }

        internal SettingsForm(SettingsFormDependencies dependencies)
        {
            if (dependencies == null) throw new ArgumentNullException(nameof(dependencies));
            config = dependencies.Config;
            initial = dependencies.Initial.Copy();
            initialCredentials = dependencies.InitialCredentials;
            settingsService = dependencies.SettingsService;
            moduleSettingsStore = dependencies.ModuleSettingsStore;
            speechCatalogCacheStore = dependencies.SpeechCatalogCacheStore;
            moduleSettings = dependencies.ModuleSettings;
            contentSettingsHost = dependencies.ContentSettingsHost;
            capabilityHost = dependencies.CapabilityHost;
            contentDescriptor = dependencies.ContentDescriptor;
            speechDescriptor = dependencies.SpeechDescriptor;
            ankiProbeHost = dependencies.AnkiProbeHost;
            typedApplication = dependencies.Application;
            FormClosing += (_, __) => ankiProbeCancellation?.Cancel();
            FormClosing += (_, __) => remNoteScope.Dispose();
            moduleActionHost = dependencies.ModuleActionHost;
            settingsDraft = dependencies.SettingsDraft;
            foreach (var profile in settingsDraft.Document.Profiles) remNoteOriginalSettings[profile.Key] = profile.Value.Remnote;
            previewAudio = dependencies.PreviewAudio;

            Text = "零碎知识剪藏 · 设置";
            Name = AccessibleName = SettingsAutomationCatalog.Window;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 780);
            MinimumSize = new Size(820, 680);
            Font = new Font("微软雅黑", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Canvas;
            ShowInTaskbar = true;

            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18), BackColor = Canvas };
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.Controls.Add(BuildWorkflowBar(), 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 14, 0, 10) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            navigation = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                ItemHeight = 38,
                Font = new Font("微软雅黑", 10F),
                Name = SettingsAutomationCatalog.Navigation,
                AccessibleName = SettingsAutomationCatalog.Navigation
            };
            navigation.Items.AddRange(PageTitles.Cast<object>().ToArray());
            navigation.SelectedIndexChanged += delegate { ShowPage(navigation.SelectedIndex); };
            pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(20), Margin = new Padding(12, 0, 0, 0), BorderStyle = BorderStyle.FixedSingle, Name = SettingsAutomationCatalog.PageHost, AccessibleName = SettingsAutomationCatalog.PageHost };
            body.Controls.Add(navigation, 0, 0);
            body.Controls.Add(pageHost, 1, 0);
            shell.Controls.Add(body, 0, 1);

            BuildPages();
            ApplyCapabilityBindings();
            foreach (var page in pages.Values) { page.Visible = false; pageHost.Controls.Add(page); }
            shell.Controls.Add(BuildFooter(), 0, 2);
            Controls.Add(shell);
            navigation.SelectedIndex = 0;
            BindProfiles();
            Shown += async (_, __) => await RestoreRemNoteBindingAsync();
            RestoreSpeechCatalogCache();

            FormClosed += delegate { previewAudio.Dispose(); };
        }

        private Control BuildWorkflowBar()
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(0) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.Controls.Add(new Label { AutoSize = true, Text = "工作流方案", Font = new Font(Font, FontStyle.Bold), ForeColor = Ink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 0) }, 0, 0);
            workflowSelector = Choice(new[] { "加载中…" }, 0, SettingsAutomationCatalog.WorkflowSelector);
            workflowSelector.SelectedIndexChanged += delegate { SwitchProfile(); };
            card.Controls.Add(workflowSelector, 1, 0);
            var create = ActionButton("新建", SettingsAutomationCatalog.WorkflowCreate, Color.White); create.Click += delegate { CreateProfile(false); };
            var copy = ActionButton("复制", SettingsAutomationCatalog.WorkflowCopy, Color.White); copy.Click += delegate { CreateProfile(true); };
            var restore = ActionButton("恢复默认", SettingsAutomationCatalog.WorkflowRestoreDefault, Color.White); restore.Click += delegate { RestoreActiveProfile(); };
            card.Controls.Add(create, 2, 0); card.Controls.Add(copy, 3, 0); card.Controls.Add(restore, 4, 0);
            return card;
        }

        private void BuildPages()
        {
            var capture = NewPage("capture", "桌面与采集", "悬浮窗、选区、剪贴板、截图和模式默认值。");
            var captureCommon = Tier(capture, "常用");
            contextMode = Check("启用上下文模式", initial.ContextModeEnabled, SettingsAutomationCatalog.ContextEnabled);
            originalImageMode = Check("原图模式（仅导出 Anki）", initial.OriginalImageMode, SettingsAutomationCatalog.OriginalImage);
            contextGranularity = Choice(new[] { "句子", "段落" }, initial.ContextGranularity == "paragraph" ? 1 : 0, SettingsAutomationCatalog.ContextGranularity);
            AddWide(captureCommon, contextMode);
            AddRow(captureCommon, "上下文粒度", contextGranularity);
            AddWide(captureCommon, originalImageMode);
            var captureAdvanced = Tier(capture, "进阶");
            AddReadOnly(captureAdvanced, "原图媒体去向", "supported · 原图模式目的端固定为 Anki；普通文本/卡片仍按已保存的 RemNote/Anki 目标执行；不触发或检查 RemNote 原图能力", SettingsAutomationCatalog.OriginalMediaAvailability);
            AddUnsupported(captureAdvanced, "选区与剪贴板策略", "由桌面 capture adapter 声明后开放", SettingsAutomationCatalog.CaptureSelectionPolicy);
            AddUnsupported(Tier(capture, "开发 / 诊断"), "Windows 输入诊断", "当前由独立 Canary 验证；设置宿主只显示状态", SettingsAutomationCatalog.CaptureWindowsDiagnostics);

            var ocr = NewPage("ocr", "图像 / OCR", "OCR provider、识别语言、质量选项和凭据状态。");
            var ocrCommon = Tier(ocr, "常用");
            ocrModel = Choice(new[] { "vlm" }, 0, SettingsAutomationCatalog.OcrProvider);
            ocrLanguage = Choice(new[] { "ch", "en" }, 0, SettingsAutomationCatalog.OcrLanguage);
            ocrTable = Check("识别表格", true, SettingsAutomationCatalog.OcrTable);
            ocrFormula = Check("识别公式", true, SettingsAutomationCatalog.OcrFormula);
            AddRow(ocrCommon, "OCR provider", ocrModel); AddRow(ocrCommon, "识别语言", ocrLanguage); AddWide(ocrCommon, ocrTable); AddWide(ocrCommon, ocrFormula);
            var ocrAdvanced = Tier(ocr, "进阶");
            ocrTimeout = Number(30, 3600, 900, SettingsAutomationCatalog.OcrTimeout);
            ocrPoll = Number(1, 60, 5, SettingsAutomationCatalog.OcrPollInterval);
            ocrScript = new TextBox { Width = 430, Name = SettingsAutomationCatalog.OcrMineruScript, AccessibleName = SettingsAutomationCatalog.OcrMineruScript };
            AddRow(ocrAdvanced, "超时（秒）", ocrTimeout); AddRow(ocrAdvanced, "轮询间隔（秒）", ocrPoll); AddRow(ocrAdvanced, "MinerU 脚本路径", ocrScript);
            mineruCredential = AddCredentialRow(ocrAdvanced, "MinerU 凭据", initialCredentials.MineruApiToken, SettingsAutomationCatalog.MineruCredential, SettingsAutomationCatalog.MineruCredentialTest);
            AddReadOnly(ocrAdvanced, "凭据保护", CredentialProtectionText(initialCredentials), SettingsAutomationCatalog.MineruCredentialProtection);
            AddReadOnly(ocrAdvanced, "凭据元数据", initialCredentials.MineruMetadata().DisplayText, SettingsAutomationCatalog.MineruCredentialMetadata);
            var ocrDiagnostics = Tier(ocr, "开发 / 诊断");
            AddReadOnly(ocrDiagnostics, "能力 descriptor", capabilityHost.ImageDescriptorStatus, SettingsAutomationCatalog.OcrDescriptorStatus);
            AddImageCapabilityActions(ocrDiagnostics);
            AddReadOnly(ocrDiagnostics, "Core / 生命周期边界", "original full pipeline=unsupported；partial failed-items continuation=conditional；retention=显式评估/隔离可用、automatic=false；live=not-run", SettingsAutomationCatalog.OcrHealth);

            var content = NewPage("content", "内容处理", "内容 provider、模型、Prompt 版本和处理策略。");
            var contentCommon = Tier(content, "常用");
            AddReadOnly(contentCommon, "内容 provider", "DeepSeek · process-injected-only", SettingsAutomationCatalog.ContentProvider);
            AddReadOnly(contentCommon, "Descriptor", contentDescriptor == null ? "conditional · Content settings descriptor 不可读" : contentDescriptor.SchemaVersion, SettingsAutomationCatalog.ContentDescriptorStatus);
            var contentAdvanced = Tier(content, "进阶");
            var contentModels = contentDescriptor == null || contentDescriptor.Models.Length == 0 ? new[] { "deepseek-chat" } : contentDescriptor.Models;
            contentModel = Choice(contentModels, Math.Max(0, Array.IndexOf(contentModels, moduleSettings.ContentProviderModel)), SettingsAutomationCatalog.ContentModel);
            AddRow(contentAdvanced, "模型", contentModel);
            contentMaxRetries = Number(contentDescriptor == null ? 1 : contentDescriptor.MinimumRetries, contentDescriptor == null ? 5 : contentDescriptor.MaximumRetries, moduleSettings.ContentProviderMaxRetries, SettingsAutomationCatalog.ContentMaxRetries);
            AddRow(contentAdvanced, "最大重试", contentMaxRetries);
            initialDeepSeekEnvironment = SecretConfig.Get("DEEPSEEK_KNOWLEDGECLIPPER_API_KEY") ?? "";
            deepSeekCredential = AddCredentialRow(contentAdvanced, "DeepSeek 凭据", initialDeepSeekEnvironment, SettingsAutomationCatalog.DeepSeekCredential, SettingsAutomationCatalog.DeepSeekCredentialTest);
            AddReadOnly(contentAdvanced, "凭据来源", "Windows 用户环境变量 DEEPSEEK_KNOWLEDGECLIPPER_API_KEY", SettingsAutomationCatalog.DeepSeekCredentialProtection);
            AddReadOnly(contentAdvanced, "Prompt 身份", contentDescriptor == null ? "conditional · 等待 descriptor" : contentDescriptor.PromptIdentity, SettingsAutomationCatalog.ContentPromptIdentity);
            AddReadOnly(contentAdvanced, "分类 Profile 身份", contentDescriptor == null ? "conditional · 等待 descriptor" : contentDescriptor.ProfileIdentity, SettingsAutomationCatalog.ContentProfileIdentity);
            AddReadOnly(contentAdvanced, "受限可调项", contentDescriptor == null ? "conditional · 等待 descriptor" : contentDescriptor.RestrictedControls, SettingsAutomationCatalog.ContentRestrictedControls);
            AddPromptControls(contentAdvanced);
            AddUnsupported(Tier(content, "开发 / 诊断"), "重试、超时与 Prompt provenance", "等待共享 settings schema 与 resolver", SettingsAutomationCatalog.ContentRuntimePolicy);

            var question = NewPage("question", "提问与制卡", "初问、追问、制卡及语言练习的默认交互。");
            var questionCommon = Tier(question, "常用");
            processingProfile = Choice(new[] { "卡片（cards）", "学习剪藏（learning）" }, 0, SettingsAutomationCatalog.WorkflowProcessingProfile);
            AddRow(questionCommon, "处理方案", processingProfile);
            AddReadOnly(questionCommon, "语言制卡策略", contentDescriptor == null ? "conditional · Content descriptor 不可读" : contentDescriptor.Strategies, SettingsAutomationCatalog.LanguageCardStrategies);
            var choices = new[] { "ask", "meaning-reading", "writing", "both" };
            contentDefaultChoice = Choice(choices, Array.IndexOf(choices, moduleSettings.ContentLanguagePracticeDefault), SettingsAutomationCatalog.LanguageCardDefault);
            AddRow(questionCommon, "默认语言制卡策略", contentDefaultChoice);
            contentPromptAfterRecognition = Check("识别后弹出选择", true, SettingsAutomationCatalog.LanguageCardPromptPolicy);
            contentPromptAfterRecognition.Enabled = false; Conditional(contentPromptAfterRecognition, "Content companion=" + (contentDescriptor == null ? "unavailable" : contentDescriptor.PromptAfterRecognitionStatus) + "；等待 Core adapter；仅真实 decision artifact 可触发");
            AddWide(questionCommon, contentPromptAfterRecognition);
            var questionAdvanced = Tier(question, "进阶");
            AddUnsupported(questionAdvanced, "提问与制卡默认行为", "question workflow adapter 未提供设置字段", SettingsAutomationCatalog.QuestionDefaults);
            AddUnsupported(questionAdvanced, "卡片结构与语言练习", "运行时选择不写入 profile/snapshot", SettingsAutomationCatalog.QuestionCardStructure);
            AddUnsupported(Tier(question, "开发 / 诊断"), "question workflow 状态", "运行态由 task query 投影，不在桌面猜测", SettingsAutomationCatalog.QuestionHealth);

            var speech = NewPage("speech", "语音与播放", "朗读语言、音色和播放参数。");
            var speechCommon = Tier(speech, "常用");
            languageOverride = Choice(new[] { "自动检测", "中文", "English", "Español", "日本語", "한국어" }, LanguageIndex(initial.LanguageOverride), SettingsAutomationCatalog.SpeechLanguage);
            AddRow(speechCommon, "朗读语言", languageOverride);
            speechAutoPlay = Check("合成完成后自动播放", true, SettingsAutomationCatalog.SpeechAutoPlay); AddWide(speechCommon, speechAutoPlay);
            chinese = Voice(EdgeVoiceCatalog.Chinese, initial.ChineseVoice, SettingsAutomationCatalog.SpeechVoicePrefix + "zh");
            english = Voice(EdgeVoiceCatalog.English, initial.EnglishVoice, SettingsAutomationCatalog.SpeechVoicePrefix + "en");
            spanish = Voice(EdgeVoiceCatalog.Spanish, initial.SpanishVoice, SettingsAutomationCatalog.SpeechVoicePrefix + "es");
            japanese = Voice(EdgeVoiceCatalog.Japanese, initial.JapaneseVoice, SettingsAutomationCatalog.SpeechVoicePrefix + "ja");
            korean = Voice(EdgeVoiceCatalog.Korean, initial.KoreanVoice, SettingsAutomationCatalog.SpeechVoicePrefix + "ko");
            AddRow(speechCommon, "中文音色", chinese); AddRow(speechCommon, "英文音色", english);
            speechVoiceCatalogAvailability = AddReadOnly(speechCommon, "声音目录", speechDescriptor == null ? "conditional · descriptor 不可读" : "可刷新 · Core v2.7 公共只读动作；未运行", SettingsAutomationCatalog.SpeechVoiceCatalogAvailability);
            voiceDiscovery = ActionButton("刷新声音目录", SettingsAutomationCatalog.SpeechVoiceDiscovery, Color.White);
            voiceDiscovery.Enabled = moduleActionHost.CanRun;
            Conditional(voiceDiscovery, voiceDiscovery.Enabled ? "Core IPC 1.2 · speech.voices · provider read-only；不播放、不保存设置" : "conditional · 当前候选未提供 Core IPC 1.2 module-action");
            voiceDiscovery.Click += async delegate { await RunSpeechVoiceDiscoveryAsync(); };
            AddWide(speechCommon, voiceDiscovery);
            var speechAdvanced = Tier(speech, "进阶");
            AddRow(speechAdvanced, "西班牙语音色", spanish); AddRow(speechAdvanced, "日语音色", japanese); AddRow(speechAdvanced, "韩语音色", korean);
            rate = Number(-50, 100, initial.RatePercent, SettingsAutomationCatalog.SpeechRate);
            volume = Number(-100, 100, initial.VolumePercent, SettingsAutomationCatalog.SpeechVolume);
            pitch = Number(-50, 50, initial.PitchHz, SettingsAutomationCatalog.SpeechPitch);
            AddRow(speechAdvanced, "语速 (%)", rate); AddRow(speechAdvanced, "音量 (%)", volume); AddRow(speechAdvanced, "音调 (Hz)", pitch);
            speechPreview = ActionButton("试听当前声音", SettingsAutomationCatalog.SpeechPreview, Color.FromArgb(235, 243, 255));
            speechPreview.Enabled = false; Conditional(speechPreview, "explicit-only · physical_audio · 需先刷新声音目录；只由当前按钮点击授权一次");
            speechPreview.Click += async delegate { await RunSpeechPreviewAsync(); };
            AddWide(speechAdvanced, speechPreview);
            speechPreviewAvailability = AddReadOnly(speechAdvanced, "试听可用性", moduleActionHost.CanRun ? "等待声音目录 · 显式点击才会播放" : "conditional · Core IPC 1.2 module-action 不可用", SettingsAutomationCatalog.SpeechPreviewAvailability);
            var speechDiagnostics = Tier(speech, "开发 / 诊断");
            AddReadOnly(speechDiagnostics, "Speech descriptor", speechDescriptor == null ? "conditional · descriptor 不可读" : speechDescriptor.StatusText, SettingsAutomationCatalog.SpeechDescriptorStatus);
            AddReadOnly(speechDiagnostics, "speech.edge worker 参数", "descriptor + Core adapter 双门禁 · 保存后由 v3 task-settings-snapshot 冻结到下一任务", SettingsAutomationCatalog.SpeechWorkerDiagnostics);

            var export = NewPage("export", "导出", "导出目标可为空、单选或双选；目标模块彼此平级。");
            var exportCompatibility = new GroupBox { Text = "最终导出目标", Name = "knowledge-clipper.desktop.settings.export-targets", AccessibleName = "最终导出目标", Width = 690, AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 12) };
            exportCompatibility.Controls.Add(new Label { AutoSize = true, Text = "RemNote　　Anki\r\n桌面端不预设唯一目标；实际可选状态由导出 adapter 决定。", ForeColor = Muted, Dock = DockStyle.Fill });
            export.Controls.Add(exportCompatibility);
            var exportCommon = Tier(export, "常用");
            remnoteDestination = Check("RemNote", false, SettingsAutomationCatalog.ExportRemNote);
            ankiDestination = Check("Anki", false, SettingsAutomationCatalog.ExportAnki);
            Conditional(remnoteDestination, "普通路径使用当前 profile 已绑定目标；仅在进阶设置中按需修改");
            Conditional(ankiDestination, "需填写 Anki profile；新任务生成 SHA-256 绑定配置工件");
            AddWide(exportCommon, remnoteDestination); AddWide(exportCommon, ankiDestination);
            AddWide(exportCommon, new Label { AutoSize = true, ForeColor = Muted, Text = "两个目标均不选择即为“无导出目标”。" });
            var exportAdvanced = Tier(export, "进阶");
            remnoteRootId = new TextBox { Width = 430, ReadOnly = true, Name = SettingsAutomationCatalog.ExportRemNoteRootId, AccessibleName = SettingsAutomationCatalog.ExportRemNoteRootId, AccessibleDescription = "只读；仅由已验证的 public discovery action 更新" };
            remnoteRootTitle = new TextBox { Width = 430, Text = "剪藏", Name = SettingsAutomationCatalog.ExportRemNoteRootTitle, AccessibleName = SettingsAutomationCatalog.ExportRemNoteRootTitle };
            remnoteRootTitle.TextChanged += (_, __) => RemNoteSelectorChanged();
            remnoteSink = Choice(new[] { "mcp-direct", "markdown-zip" }, 0, SettingsAutomationCatalog.ExportRemNoteSink);
            ankiProfile = new TextBox { Width = 430, Text = moduleSettings.AnkiExpectedProfile, Name = SettingsAutomationCatalog.ExportAnkiProfile, AccessibleName = SettingsAutomationCatalog.ExportAnkiProfile };
            ankiEndpoint = new TextBox { Width = 430, Text = moduleSettings.AnkiEndpoint, Name = SettingsAutomationCatalog.ExportAnkiEndpoint, AccessibleName = SettingsAutomationCatalog.ExportAnkiEndpoint };
            ankiDeckPreset = Choice(new[] { "knowledge-clipper-default", "custom" }, moduleSettings.AnkiDeckPreset == "custom" ? 1 : 0, SettingsAutomationCatalog.ExportAnkiDeckPreset);
            ankiDeckRoutes = new TextBox { Width = 430, Text = JsonSerializer.Serialize(moduleSettings.AnkiDeckRoutes ?? new Dictionary<string, string>(), ContractJson.Options), Name = SettingsAutomationCatalog.ExportAnkiDeckRoutes, AccessibleName = SettingsAutomationCatalog.ExportAnkiDeckRoutes, AccessibleDescription = "JSON map；custom 时下一任务生效" };
            remnoteBoundTargetStatus = AddReadOnly(exportCommon, "RemNote 当前绑定目标", CurrentRemNoteTargetDisplay(), SettingsAutomationCatalog.ExportRemNoteBoundTarget);
            AddRow(exportAdvanced, "RemNote 根节点 ID（自动发现，只读）", remnoteRootId); AddRow(exportAdvanced, "RemNote 根节点标题（发现条件）", remnoteRootTitle); AddRow(exportAdvanced, "RemNote 写入方式", remnoteSink);
            remnoteDiscoverTarget = ActionButton("按标题自动发现 RemNote 目标", SettingsAutomationCatalog.ExportRemNoteDiscoverTarget, Color.White);
            remnoteDiscoverTarget.Enabled = typedApplication != null;
            Conditional(remnoteDiscoverTarget, "显式只读发现 · Core typed SDK 验证唯一目标和证据；保存后下一任务生效");
            remnoteDiscoverTarget.Click += async delegate { await RunRemNoteTargetDiscoveryAsync(); };
            AddWide(exportAdvanced, remnoteDiscoverTarget);
            remnoteDiscoverTargetStatus = AddReadOnly(exportAdvanced, "RemNote 自动发现状态", remnoteDiscoverTarget.Enabled ? "未运行 · 仅使用标题作为发现条件" : "conditional · public discovery action unavailable", SettingsAutomationCatalog.ExportRemNoteDiscoverTargetStatus);
            remnoteProbe = ActionButton("RemNote 只读连接检查", SettingsAutomationCatalog.ExportRemNoteProbe, Color.White);
            remnoteProbe.Enabled = typedApplication != null;
            Conditional(remnoteProbe, "显式只读检查 · 使用 Core verified binding；历史证据恢复不代表当前连接就绪");
            remnoteProbe.Click += async delegate { await RunRemNoteProbeAsync(); };
            AddWide(exportAdvanced, remnoteProbe);
            remnoteProbeStatus = AddReadOnly(exportAdvanced, "RemNote 检查状态", moduleActionHost.CanRun ? "未运行 · 仅显式只读检查" : "conditional · Core action unavailable", SettingsAutomationCatalog.ExportRemNoteProbeStatus);
            AddRow(exportAdvanced, "Anki profile", ankiProfile); AddRow(exportAdvanced, "AnkiConnect endpoint", ankiEndpoint); AddRow(exportAdvanced, "Anki 牌组方案（下一任务）", ankiDeckPreset); AddRow(exportAdvanced, "自定义 deckRoutes（JSON，下一任务）", ankiDeckRoutes);
            AddReadOnly(exportAdvanced, "Anki 配置工件", "每个新任务由 authority 模块模板生成并以 SHA-256 绑定", SettingsAutomationCatalog.ExportAnkiConfig);
            var semanticAnkiProbe = SemanticButton("只读连接检查", SettingsAutomationCatalog.ExportAnkiProbe, Color.White, RunAnkiProbeAsync);
            ankiProbe = semanticAnkiProbe;
            ankiProbeRoute = new ComboBox { Width = 430, DropDownStyle = ComboBoxStyle.DropDownList, Name = AnkiProbeRouteId, AccessibleName = AnkiProbeRouteId };
            ankiProbeRoute.DropDown += (_, __) => RefreshAnkiProbeRoutes();
            ankiDeckPreset.SelectedIndexChanged += (_, __) => RefreshAnkiProbeRoutes();
            ankiDeckRoutes.TextChanged += (_, __) => RefreshAnkiProbeRoutes();
            ankiProfile.TextChanged += (_, __) => RefreshAnkiProbeRoutes();
            RefreshAnkiProbeRoutes();
            AddRow(exportAdvanced, "检查目标（route key，不修改默认目标）", ankiProbeRoute);
            ankiProbe.Enabled = typedApplication != null;
            Conditional(ankiProbe, "仅经同一 typed Core SDK 做只读检查；可能弹出 Anki permission；不初始化、不写卡；保存设置不会触发检查");
            AddWide(exportAdvanced, ankiProbe);
            ankiProbeStatus = AddReadOnly(exportAdvanced, "连接检查状态", "未运行 · 多个 route 时请明确选择检查目标", SettingsAutomationCatalog.ExportAnkiProbeStatus);
            ankiInitialize = ActionButton("初始化受管资源（会写入 Anki）", SettingsAutomationCatalog.ExportAnkiInitialize, Color.FromArgb(255, 245, 224));
            ankiInitialize.Enabled = false;
            Conditional(ankiInitialize, "explicit-only · 仅创建缺失的受管牌组/模型；不写卡、不删除、不修改现有资源；普通连接检查绝不触发");
            ankiInitialize.Click += async delegate { await RunAnkiInitializationAsync(); };
            AddWide(exportAdvanced, ankiInitialize);
            ankiInitializeStatus = AddReadOnly(exportAdvanced, "初始化状态", "未启用 · 只读检查不创建牌组、模型、卡片或媒体", SettingsAutomationCatalog.ExportAnkiInitializeStatus);
            var clipDaily = Check("摘录到 RemNote：剪藏 → 当日节点", false, SettingsAutomationCatalog.ExportClipDaily);
            clipDaily.Checked = true; clipDaily.Enabled = false; Conditional(clipDaily, "固定映射为 clip-daily-route；制卡固定 card-export"); AddWide(exportAdvanced, clipDaily);
            AddUnsupported(Tier(export, "开发 / 诊断"), "Exporter 状态与回执", "等待 Core health projection；制卡设置不包含日期字段", SettingsAutomationCatalog.ExportHealth);

            var diagnostics = NewPage("diagnostics", "运行与诊断", "存储、运行时、重试策略和只读诊断信息。");
            AddReadOnly(Tier(diagnostics, "常用"), "存储与保留策略", settingsService.RetentionPolicyEvidence, SettingsAutomationCatalog.StorageRetention);
            AddUnsupported(Tier(diagnostics, "进阶"), "重试、超时和并发", "等待 Core validation；不在桌面硬编码范围", SettingsAutomationCatalog.RetryPolicy);
            var dev = Tier(diagnostics, "开发 / 诊断");
            AddReadOnly(dev, "数据目录", config.WorkDirectory, SettingsAutomationCatalog.DiagnosticsDataPath);
            AddReadOnly(dev, "任务目录", Path.Combine(config.WorkDirectory, "tasks"), SettingsAutomationCatalog.DiagnosticsJobsRoot);
            AddReadOnly(dev, "设置来源", settingsService.SettingsSourceEvidence(workflowDocument), SettingsAutomationCatalog.SettingsSource);
            AddReadOnly(dev, "生效时间", settingsService.SettingsEffectiveAtEvidence, SettingsAutomationCatalog.SettingsEffectiveAt);
            AddReadOnly(dev, "当前 revision", settingsService.CurrentRevision, SettingsAutomationCatalog.SettingsRevision);
            AddReadOnly(dev, "下一任务 snapshot", settingsService.NextTaskSnapshotEvidence(workflowDocument), SettingsAutomationCatalog.SettingsNextSnapshot);
            AddReadOnly(dev, "Authority", settingsService.AuthorityEvidence, SettingsAutomationCatalog.DiagnosticsAuthority);
            AddReadOnly(dev, "Registry", settingsService.RegistryEvidence, SettingsAutomationCatalog.DiagnosticsRegistry);
            AddReadOnly(dev, "Core checkpoint", settingsService.CheckpointEvidence, SettingsAutomationCatalog.DiagnosticsCheckpoint);
            var openData = ActionButton("打开数据目录", SettingsAutomationCatalog.DiagnosticsOpenData, Color.White);
            openData.Click += delegate { try { Directory.CreateDirectory(config.WorkDirectory); Process.Start(new ProcessStartInfo(config.WorkDirectory) { UseShellExecute = true }); } catch (Exception ex) { status.Text = "无法打开数据目录：" + ex.Message; } };
            AddWide(dev, openData);
        }

        private void AddImageCapabilityActions(TableLayoutPanel table)
        {
            var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var offline = ActionButton("离线就绪检查", SettingsAutomationCatalog.OcrOfflineReadiness, Color.White);
            var offlineAction = capabilityHost.OfflineImageAction;
            offline.Enabled = capabilityHost.CanRun(offlineAction);
            Conditional(offline, offlineAction == null ? "pending · descriptor 未声明 offline readiness action" : offline.Enabled ? "offline-safe · 不上传外部数据" : "descriptor 已声明；Desktop runner 尚未绑定");
            offline.Click += delegate
            {
                try { status.Text = "离线就绪检查：" + capabilityHost.Run(offlineAction); }
                catch (Exception ex) { status.Text = "离线就绪检查失败：" + ex.Message; }
            };
            var validate = OfflineImageButton("Validate-only", SettingsAutomationCatalog.OcrValidateOnly, capabilityHost.ValidateImageAction);
            var fake = OfflineImageButton("Fake Provider 离线测试", SettingsAutomationCatalog.OcrOfflineFakeProvider, capabilityHost.FakeImageAction);
            var live = ActionButton("实时连通性（外部上传）", SettingsAutomationCatalog.OcrLiveConnectivity, Color.White);
            live.Enabled = false;
            Conditional(live, capabilityHost.LiveImageAction == null ? "pending · external-upload · 默认不运行；descriptor 未声明 live connectivity action" : "external-upload · 默认不运行；需显式外部测试授权与公共 action runner");
            actions.Controls.Add(offline); actions.Controls.Add(validate); actions.Controls.Add(fake); actions.Controls.Add(live); AddWide(table, actions);
        }

        private Button OfflineImageButton(string text, string automationId, ModuleCapabilityAction action)
        {
            var button = ActionButton(text, automationId, Color.White);
            button.Enabled = capabilityHost.CanRun(action);
            Conditional(button, action == null ? "descriptor 未声明该离线 action" : button.Enabled ? "networkAccess=prohibited · externalUpload=false" : "descriptor 已声明；等待 authority-admitted runner");
            button.Click += delegate
            {
                try { status.Text = text + "：" + capabilityHost.Run(action); }
                catch (Exception ex) { status.Text = text + "失败：" + ex.Message; }
            };
            return button;
        }

        private void ApplyCapabilityBindings()
        {
            processingProfile.Enabled = settingsService.Capabilities.Editable("/workflow/processingProfile");
            contextMode.Enabled = settingsService.Capabilities.Editable("/capture/contextEnabled");
            contextGranularity.Enabled = settingsService.Capabilities.Editable("/capture/contextGranularity");
            originalImageMode.Enabled = settingsService.Capabilities.Editable("/capture/imageHandling");
            if (originalImageMode.Enabled) Conditional(originalImageMode, "记录到快照；下游原图理解尚未接通并失败封闭");
            chinese.Enabled = english.Enabled = spanish.Enabled = japanese.Enabled = korean.Enabled = SpeechEditable("voicesByLanguage", "/speech/voicesByLanguage");
            rate.Enabled = SpeechEditable("ratePercent", "/speech/ratePercent");
            volume.Enabled = SpeechEditable("volumePercent", "/speech/volumePercent");
            pitch.Enabled = SpeechEditable("pitchHz", "/speech/pitchHz");
            speechAutoPlay.Enabled = SpeechEditable("autoPlay", "/speech/autoPlay");
            languageOverride.Enabled = SpeechEditable("languageStrategy", "/speech/languageStrategy") && SpeechEditable("languageOverride", "/speech/languageOverride");
            if (languageOverride.Enabled) languageOverride.AccessibleDescription = "supported: descriptor + Core adapter; effectiveAt=next_task";
            if (!speechAutoPlay.Enabled) Conditional(speechAutoPlay, "Speech companion 与 Core adapter 均声明 fixed read-only true");
            var imageEditable = settingsService.Capabilities.Editable("/imageOcr/providerSettings");
            ocrModel.Enabled = imageEditable && capabilityHost.Supports("image.ocr.model-version");
            ocrLanguage.Enabled = imageEditable && capabilityHost.Supports("image.ocr.language");
            ocrTable.Enabled = imageEditable && capabilityHost.Supports("image.ocr.table-recognition");
            ocrFormula.Enabled = imageEditable && capabilityHost.Supports("image.ocr.formula-recognition");
            ocrTimeout.Enabled = imageEditable && capabilityHost.Supports("image.ocr.provider-timeout-seconds");
            ocrPoll.Enabled = imageEditable && capabilityHost.Supports("image.ocr.poll-interval-seconds");
            ocrScript.Enabled = settingsService.Capabilities.Editable("/imageOcr/mineruScriptPath") && capabilityHost.Supports("image.ocr.mineru-script-path");
            contentDefaultChoice.Enabled = contentDescriptor != null && contentDescriptor.DefaultChoiceStatus == "supported-snapshot-bound" && settingsService.Capabilities.Editable("/content/runtimePolicy/languagePracticeDefault");
            contentModel.Enabled = contentDescriptor != null && settingsService.Capabilities.Editable("/content/runtimePolicy/providerModel");
            contentMaxRetries.Enabled = contentDescriptor != null && settingsService.Capabilities.Editable("/content/runtimePolicy/providerMaxRetries");
            if (contentDefaultChoice.Enabled) contentDefaultChoice.AccessibleDescription = "supported: Content 0.7.0 companion + Core adapter v3; published in SHA-bound content-config-snapshot/1.1.0";
            if (contentModel.Enabled) contentModel.AccessibleDescription = "supported: next-task content-config-snapshot/1.1.0 runtimePolicy.provider.model";
            if (contentMaxRetries.Enabled) contentMaxRetries.AccessibleDescription = "supported: next-task content-config-snapshot/1.1.0 runtimePolicy.provider.maxRetries";
            // Editing the next-task destination is governed by the admitted settings
            // adapter. Runtime availability is validated when the immutable snapshot is
            // published; tying this control to an obsolete 2.0.0 evidence filename made
            // newer admitted exporters look unavailable in the UI.
            remnoteDestination.Enabled = settingsService.Capabilities.Editable("/remnote");
            remnoteRootId.Enabled = remnoteDestination.Enabled;
            remnoteRootTitle.Enabled = remnoteSink.Enabled = remnoteDestination.Enabled;
            ankiDestination.Enabled = settingsService.Capabilities.Editable("/anki/enabled");
            ankiProfile.Enabled = ankiEndpoint.Enabled = ankiDestination.Enabled;
        }

        private bool SpeechEditable(string descriptorId, string pointer)
        {
            return speechDescriptor != null && speechDescriptor.Editable(descriptorId) && settingsService.Capabilities.Editable(pointer);
        }

        private FlowLayoutPanel NewPage(string key, string title, string description)
        {
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.White, Name = "knowledge-clipper.desktop.settings.page." + key, AccessibleName = "knowledge-clipper.desktop.settings.page." + key };
            page.Controls.Add(new Label { AutoSize = true, Text = title, Font = new Font("微软雅黑", 16F, FontStyle.Bold), ForeColor = Ink, Margin = new Padding(0, 0, 0, 4) });
            page.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(690, 0), Text = description, ForeColor = Muted, Margin = new Padding(0, 0, 0, 12) });
            pages.Add(key, page);
            return page;
        }

        private static TableLayoutPanel Tier(FlowLayoutPanel page, string title)
        {
            var group = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(690, 0), Padding = new Padding(12), Margin = new Padding(0, 0, 0, 12), ForeColor = Ink };
            var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(650, 0), ColumnCount = 2, Location = new Point(12, 24) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            group.Controls.Add(table); page.Controls.Add(group); return table;
        }

        private static void AddRow(TableLayoutPanel table, string label, Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { AutoSize = true, Text = label, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 8), ForeColor = Ink }, 0, row);
            control.Margin = new Padding(0, 4, 0, 4); control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            table.Controls.Add(control, 1, row);
        }

        private static void AddWide(TableLayoutPanel table, Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 5, 0, 5); table.Controls.Add(control, 0, row); table.SetColumnSpan(control, 2);
        }

        private static void AddUnsupported(TableLayoutPanel table, string label, string reason, string automationId = null)
        {
            var value = new TextBox { ReadOnly = true, Enabled = false, Text = "暂不可用 · " + reason, BackColor = Color.FromArgb(246, 247, 249) };
            if (!string.IsNullOrEmpty(automationId)) value.Name = value.AccessibleName = automationId;
            Conditional(value, reason); AddRow(table, label, value);
        }

        private static TextBox AddReadOnly(TableLayoutPanel table, string label, string value, string automationId)
        {
            var box = new TextBox { ReadOnly = true, Text = value ?? "", BackColor = Color.FromArgb(248, 249, 251), Name = automationId, AccessibleName = automationId };
            box.AccessibleDescription = "read-only"; AddRow(table, label, box);
            return box;
        }

        private static TextBox AddCredentialRow(TableLayoutPanel table, string label, string value, string fieldId, string testId)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var secret = new TextBox { Width = 300, UseSystemPasswordChar = true, Text = value ?? "", Name = fieldId, AccessibleName = fieldId, AccessibleDescription = "DPAPI CurrentUser protected local credential; apply on orchestrator restart; excluded from task packages, logs, manifests, and receipts" };
            var test = ConditionalButton("测试", testId); test.AccessibleDescription = "conditional: provider test adapter unavailable";
            panel.Controls.Add(secret); panel.Controls.Add(test); AddRow(table, label, panel); return secret;
        }

        private static string CredentialProtectionText(DesktopCredentialSettings credentials)
        {
            return credentials != null && (credentials.StorageStatus == "failed-closed" || credentials.StorageStatus == "migration-failed-closed")
                ? "读取或迁移失败，已安全关闭；原文件未删除"
                : "保存在本机当前 Windows 用户凭据保护中";
        }

        private void AddPromptControls(TableLayoutPanel table)
        {
            var choices = new List<string> { "内置 open-md-v2（只读）" };
            if (!string.IsNullOrWhiteSpace(moduleSettings.ContentDraftRoot) && Directory.Exists(moduleSettings.ContentDraftRoot)) choices.Add("用户副本 · " + Path.GetFileName(moduleSettings.ContentDraftRoot));
            promptVersion = Choice(choices.ToArray(), choices.Count - 1, SettingsAutomationCatalog.PromptVersion); promptVersion.Enabled = false; AddRow(table, "Prompt 版本", promptVersion);
            var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            promptCopy = ActionButton("复制为用户副本", SettingsAutomationCatalog.PromptCopy, Color.White);
            promptOpen = ActionButton("用系统编辑器打开", SettingsAutomationCatalog.PromptOpenExternal, Color.White);
            promptValidate = ActionButton("校验", SettingsAutomationCatalog.PromptValidate, Color.White);
            promptDiff = ActionButton("查看差异", SettingsAutomationCatalog.PromptDiff, Color.White);
            promptCopy.Click += delegate { try { var root = contentSettingsHost.CopyBuiltIn(); moduleSettings = moduleSettingsStore.Load(); promptVersion.Items.Add("用户副本 · " + Path.GetFileName(root)); promptVersion.SelectedIndex = promptVersion.Items.Count - 1; status.Text = "已复制内置 Prompt；用户副本尚未写入任何任务。"; ApplyPromptAvailability(); } catch (Exception ex) { status.Text = "复制失败：" + ex.Message; } };
            promptOpen.Click += delegate { try { Process.Start(new ProcessStartInfo(Path.Combine(contentSettingsHost.DraftRoot, "prompts", "shared", "system.md")) { UseShellExecute = true }); } catch (Exception ex) { status.Text = "无法打开 Prompt：" + ex.Message; } };
            promptValidate.Click += delegate { try { using var result = contentSettingsHost.Validate(); status.Text = "Prompt 校验通过 · revision " + result.RootElement.GetProperty("settingsRevisionCandidate").GetString(); } catch (Exception ex) { status.Text = "Prompt 校验失败：" + ex.Message; } };
            promptDiff.Click += delegate { try { using var result = contentSettingsHost.Diff(); status.Text = "Prompt 差异已计算 · valid=" + result.RootElement.GetProperty("valid").GetBoolean().ToString().ToLowerInvariant(); } catch (Exception ex) { status.Text = "Prompt 差异失败：" + ex.Message; } };
            actions.Controls.Add(promptCopy); actions.Controls.Add(promptOpen); actions.Controls.Add(promptValidate); actions.Controls.Add(promptDiff);
            AddWide(table, actions);
            ApplyPromptAvailability();
        }

        private void ApplyPromptAvailability()
        {
            if (promptCopy == null) return;
            var hasDraft = !string.IsNullOrWhiteSpace(contentSettingsHost.DraftRoot) && Directory.Exists(contentSettingsHost.DraftRoot);
            promptCopy.Enabled = contentSettingsHost.Available && !hasDraft;
            promptOpen.Enabled = promptValidate.Enabled = promptDiff.Enabled = contentSettingsHost.Available && hasDraft;
            if (!contentSettingsHost.Available) Conditional(promptCopy, "integrated authority Content adapter 不可用");
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { AutoSize = true, Text = settingsService.BindingStatus, ForeColor = Muted, Name = SettingsAutomationCatalog.Status, AccessibleName = SettingsAutomationCatalog.Status, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 10, 0) };
            var buttons = new FlowLayoutPanel { AutoSize = true, MinimumSize = new Size(210, 42), WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
            var save = ActionButton("保存", SettingsAutomationCatalog.Save, Color.FromArgb(220, 244, 229));
            var cancel = ActionButton("取消", SettingsAutomationCatalog.Cancel, Color.White);
            save.Click += delegate { SaveAndClose(); }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); footer.Controls.Add(status, 0, 0); footer.Controls.Add(buttons, 1, 0); return footer;
        }

        private void ShowPage(int index)
        {
            if (index < 0 || index >= PageKeys.Length) return;
            pageHost.SuspendLayout();
            foreach (var candidate in pages.Values) candidate.Visible = false;
            var page = pages[PageKeys[index]]; page.Visible = true; page.BringToFront();
            pageHost.ResumeLayout();
        }

        private async Task RunSpeechVoiceDiscoveryAsync()
        {
            if (!moduleActionHost.CanRun) return;
            voiceDiscovery.Enabled = false; speechVoiceCatalogAvailability.Text = "刷新中 · Core 只读 provider query"; status.Text = "正在刷新声音目录…";
            try
            {
                var payload = new
                {
                    schemaVersion = "speech-settings-voice-discovery-input.v1", mode = "discover",
                    requestedLanguages = new[] { "zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR" }, cacheMaxAgeSeconds = 86400,
                    deployment = new { edgeTtsPath = OptionalAbsolutePath(config.EdgeTtsPath), ffplayPath = OptionalAbsolutePath(config.FfplayPath) },
                    catalogSnapshot = (object)null
                };
                var result = await moduleActionHost.RunAsync("speech.voices", payload);
                speechCatalogJson = result.CatalogJson ?? result.OutputJson;
                var cache = speechCatalogCacheStore.Save(result.CatalogSnapshotRef, result.CatalogSnapshotSha256, speechCatalogJson);
                using var document = JsonDocument.Parse(speechCatalogJson);
                var root = document.RootElement;
                var revision = root.GetProperty("catalogRevision").GetString(); var hash = root.GetProperty("catalogSha256").GetString();
                speechVoiceCatalogAvailability.Text = root.GetProperty("status").GetString() + " · revision=" + revision + " · sha256=" + hash + " · snapshot=" + cache.CatalogSnapshotSha256;
                ApplyDiscoveredVoices(root);
                speechPreview.Enabled = true; speechPreviewAvailability.Text = "可试听 · explicit-only · physical_audio · 当前目录 " + revision;
                status.Text = "声音目录已刷新并由 Core 持久化；未播放声音。";
            }
            catch (Exception ex)
            {
                speechVoiceCatalogAvailability.Text = "刷新失败 · " + ex.Message; speechPreview.Enabled = false;
                status.Text = "声音目录刷新失败；未播放声音。";
            }
            finally { voiceDiscovery.Enabled = moduleActionHost.CanRun; }
        }

        private void RestoreSpeechCatalogCache()
        {
            try
            {
                var cache = speechCatalogCacheStore.Load();
                if (cache == null) return;
                speechCatalogJson = cache.CatalogJson;
                using var document = JsonDocument.Parse(speechCatalogJson);
                ApplyDiscoveredVoices(document.RootElement);
                speechVoiceCatalogAvailability.Text = "cached · revision=" + cache.CatalogRevision + " · sha256=" + cache.CatalogSha256 + " · snapshot=" + cache.CatalogSnapshotSha256;
                speechPreview.Enabled = moduleActionHost.CanRun;
                speechPreviewAvailability.Text = moduleActionHost.CanRun
                    ? "可试听 · explicit-only · physical_audio · 已验证缓存 " + cache.CatalogRevision
                    : "目录缓存已验证；conditional · Core IPC 1.2 module-action 不可用";
            }
            catch (Exception ex)
            {
                speechCatalogJson = null;
                speechPreview.Enabled = false;
                speechVoiceCatalogAvailability.Text = "缓存拒绝 · " + ex.Message;
                speechPreviewAvailability.Text = "等待重新刷新声音目录；未播放声音";
            }
        }

        private async Task RunSpeechPreviewAsync()
        {
            if (!moduleActionHost.CanRun || string.IsNullOrWhiteSpace(speechCatalogJson)) return;
            speechPreview.Enabled = false; speechPreviewAvailability.Text = "试听中 · 本次点击已授权一次物理播放"; status.Text = "正在生成并播放短试听…";
            try
            {
                var draft = Collect(); var previewLanguage = draft.LanguageOverride == "auto" ? "zh-CN" : draft.LanguageOverride;
                using var catalogDocument = JsonDocument.Parse(speechCatalogJson); var root = catalogDocument.RootElement;
                var previewText = Encoding.UTF8.GetBytes("设置试听"); var previewHash = Sha256(previewText);
                var requestId = "settings-preview-" + Guid.NewGuid().ToString("N");
                var speechTemplate = new
                {
                    schemaVersion = "1.0.0", requestId, operationId = "settings-preview-operation",
                    spokenText = new { artifactRef = "settings.preview.text", path = "inputs/settings-preview/payload.txt", sha256 = previewHash, bytes = previewText.LongLength, encoding = "utf-8", mediaType = "text/plain" },
                    contextMode = "selection_only"
                };
                var payload = new
                {
                    schemaVersion = "speech-settings-speaker-preview-input.v1", scope = "project", effectiveAt = "preview_only",
                    action = "speaker.preview", explicitSideEffectAuthorization = true, previewLanguage,
                    adapterInput = new
                    {
                        schemaVersion = "speech-settings-adapter-input.v1",
                        settings = new
                        {
                            languageStrategy = draft.LanguageOverride == "auto" ? "auto" : "override", languageOverride = draft.LanguageOverride == "auto" ? null : draft.LanguageOverride,
                            voicesByLanguage = new Dictionary<string, string> { { "zh-CN", draft.ChineseVoice }, { "en-US", draft.EnglishVoice }, { "es-ES", draft.SpanishVoice }, { "ja-JP", draft.JapaneseVoice }, { "ko-KR", draft.KoreanVoice } },
                            ratePercent = draft.RatePercent, volumePercent = draft.VolumePercent, pitchHz = draft.PitchHz
                        },
                        catalog = new { provider = root.GetProperty("provider").GetString(), catalogVersion = root.GetProperty("catalogVersion").GetString(), updatedAtUtc = root.GetProperty("updatedAtUtc").GetString(), voices = root.GetProperty("voices").Clone() },
                        deployment = new { edgeTtsPath = OptionalAbsolutePath(config.EdgeTtsPath), ffplayPath = OptionalAbsolutePath(config.FfplayPath) },
                        synthesisRequest = new { payload = speechTemplate }, playbackRequest = (object)null
                    }
                };
                var result = await moduleActionHost.RunAsync("speech.preview", payload);
                if (!result.PhysicalAudio) throw new InvalidDataException("settings.speech-preview-receipt-missing-physical-audio");
                speechPreviewAvailability.Text = "已自然完成 · idle · evidence=" + result.OutputRef;
                status.Text = "试听已自然完成；未创建摘录、卡片或导出任务。";
            }
            catch (Exception ex) { speechPreviewAvailability.Text = "试听失败 · " + ex.Message; status.Text = "试听失败；未创建知识任务。"; }
            finally { speechPreview.Enabled = moduleActionHost.CanRun && !string.IsNullOrWhiteSpace(speechCatalogJson); }
        }

        private bool TryGetCurrentRemNoteTarget(out string rootId, out string rootTitle)
        {
            rootId = null;
            rootTitle = null;
            return settingsDraft != null && settingsDraft.TryGetRemNoteTarget(out rootId, out rootTitle);
        }

        private string CurrentRemNoteTargetDisplay()
        {
            string rootId, rootTitle;
            return TryGetCurrentRemNoteTarget(out rootId, out rootTitle)
                ? (settingsDraft.RemNoteBindingStatus == "verified_pending" ? "verified_pending" : "已绑定") + " · 标题=" + rootTitle + " · ID=" + MaskIdentifier(rootId) + " · 下一任务生效"
                : "未绑定 · 普通检查不会要求临时输入 Rem ID";
        }

        private static string MaskIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未设置";
            value = value.Trim();
            return value.Length <= 8 ? new string('•', value.Length) : value.Substring(0, 4) + "…" + value.Substring(value.Length - 4);
        }

        private void ApplyDiscoveredVoices(JsonElement root)
        {
            var controls = new Dictionary<string, ComboBox> { { "zh-CN", chinese }, { "en-US", english }, { "es-ES", spanish }, { "ja-JP", japanese }, { "ko-KR", korean } };
            foreach (var pair in controls)
            {
                var selected = Selected(pair.Value); var discovered = root.GetProperty("voices").EnumerateArray()
                    .Where(item => item.GetProperty("languageTag").GetString() == pair.Key && item.GetProperty("available").GetBoolean())
                    .Select(item => new VoiceChoice(item.GetProperty("voiceId").GetString(), item.TryGetProperty("displayName", out var displayName) ? displayName.GetString() : item.GetProperty("voiceId").GetString()))
                    .GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
                if (discovered.Length == 0) continue;
                pair.Value.Items.Clear(); pair.Value.Items.AddRange(discovered.Cast<object>().ToArray());
                var selectedIndex = Array.FindIndex(discovered, item => item.Id == selected);
                pair.Value.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            }
        }

        private static string Sha256(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }

        private string BuildAnkiProbeDraft()
        {
            var draft = moduleSettingsStore.Load();
            draft.AnkiExpectedProfile = ankiProfile.Text.Trim();
            draft.AnkiEndpoint = ankiEndpoint.Text.Trim();
            draft.AnkiDeckPreset = Convert.ToString(ankiDeckPreset.SelectedItem);
            if (draft.AnkiDeckPreset == "custom")
                draft.AnkiDeckRoutes = JsonSerializer.Deserialize<Dictionary<string, string>>(ankiDeckRoutes.Text, ContractJson.Options);
            return DesktopTaskSettingsArtifacts.BuildAnkiProbeConfigurationJson(settingsService, draft);
        }

        private void RefreshAnkiProbeRoutes()
        {
            if (ankiProbeRoute == null) return;
            var selected = Convert.ToString(ankiProbeRoute.SelectedItem);
            ankiProbeRoute.Items.Clear();
            try
            {
                using var document = JsonDocument.Parse(BuildAnkiProbeDraft());
                foreach (var route in document.RootElement.GetProperty("deck_routes").EnumerateObject())
                    ankiProbeRoute.Items.Add(route.Name);
                if (ankiProbeRoute.Items.Contains(selected)) ankiProbeRoute.SelectedItem = selected;
                else if (ankiProbeRoute.Items.Count == 1) ankiProbeRoute.SelectedIndex = 0;
            }
            catch { /* Invalid draft is explained on explicit probe; never save or guess a route. */ }
        }

        internal void CancelAnkiProbe() => ankiProbeCancellation?.Cancel();

        private async Task RunAnkiProbeAsync()
        {
            if (typedApplication == null || Interlocked.CompareExchange(ref ankiProbeInFlight, 1, 0) != 0) return;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ankiProbeCancellation = cancellation;
            ankiProbe.Enabled = false;
            ankiInitialize.Enabled = false;
            ankiProbeStatus.ForeColor = Muted;
            ankiProbeStatus.AccessibleDescription = "{\"ready\":false,\"state\":\"checking\"}";
            ankiProbeStatus.Text = "检查中 · 只读，可能弹出 Anki permission";
            try
            {
                var json = BuildAnkiProbeDraft();
                var route = Convert.ToString(ankiProbeRoute.SelectedItem);
                using var document = JsonDocument.Parse(json);
                if (string.IsNullOrWhiteSpace(route) || !document.RootElement.GetProperty("deck_routes").TryGetProperty(route, out _))
                    throw new InvalidOperationException("需要选择检查目标（route key）；本次未连接 Anki");
                var artifact = await typedApplication.Artifacts.PublishAsync("anki-configuration", json, cancellation.Token);
                await typedApplication.Commands.InvokeAsync(InvocationSource.Pointer,
                    new ApplicationCommand(ApplicationCommandKind.AnkiProbe)
                    {
                        Arguments = new Dictionary<string, object> { ["configuration"] = artifact, ["selectedRoute"] = route }
                    }, cancellation.Token);
                var result = typedApplication.State.Snapshot().ModuleActions[ModuleActionKind.AnkiProbe];
                if (IsDisposed || Disposing) return;
                var ready = result.Status == OperationStatus.Succeeded && result.Output != null &&
                    result.AnkiReadiness?.CurrentTargetReady == true && result.AnkiReadiness.CurrentTarget == route;
                var optional = result.AnkiReadiness?.UninitializedRoutes ?? Array.Empty<string>();
                ankiProbeStatus.Text = (ready ? "当前目标可用" : "未就绪 / 未核实") + " · route=" + route +
                    (optional.Count > 0 ? " · 其他可选路线未就绪：" + string.Join("、", optional) : "") +
                    " · " + result.Status + " · " + result.NoticeCode + " · writes=0";
                ankiProbeStatus.AccessibleDescription = JsonSerializer.Serialize(new { ready, route, terminalState = result.Status.ToString(),
                    notice = result.NoticeCode, evidence = result.Output?.Id, optionalRoutesNotReady = optional, externalMutation = false });
                ankiProbeStatus.ForeColor = ready ? Color.FromArgb(36, 130, 82) : Muted;
                status.Text = ready ? "Anki 当前检查目标可用；未初始化或写入。" : "Anki 检查未就绪；请查看目标、状态和原因。";
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && !Disposing)
                {
                    ankiProbeStatus.Text = "已取消或超时 · 未核实 · 不自动重试";
                    ankiProbeStatus.AccessibleDescription = "{\"ready\":false,\"terminalState\":\"CancelledOrTimedOut\",\"evidence\":null}";
                    status.Text = "Anki 检查已取消或超时，结果未核实。";
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                {
                    ankiProbeStatus.Text = "检查失败 · " + ex.Message;
                    ankiProbeStatus.AccessibleDescription = "{\"ready\":false,\"terminalState\":\"Failed\",\"evidence\":null}";
                    status.Text = "Anki 只读检查未就绪。";
                }
            }
            finally
            {
                ankiProbeCancellation = null;
                Interlocked.Exchange(ref ankiProbeInFlight, 0);
                if (!IsDisposed && !Disposing) ankiProbe.Enabled = typedApplication != null;
            }
        }

        private async Task RunAnkiInitializationAsync()
        {
            if (ankiProbeHost == null || !ankiProbeHost.CanInitializeManaged || !ankiInitialize.Enabled) return;
            ankiInitialize.Enabled = false;
            ankiInitializeStatus.Text = "初始化中 · 本次显式动作只创建缺失的受管牌组/模型";
            status.Text = "正在初始化 Anki 受管资源；不会写卡、删除或修改现有资源。";
            try
            {
                var result = await ankiProbeHost.InitializeManagedAsync();
                if (result.CardWrites != 0 || result.Deletes != 0 || result.ExistingResourcesModified != 0)
                    throw new InvalidDataException("settings.anki-initialize-managed-scope-violation");
                ankiInitializeStatus.Text = result.DisplayText;
                status.Text = result.Completed ? "Anki 受管资源初始化完成；请重新执行只读连接检查。" : "Anki 初始化未确认完成；不会自动重试。";
            }
            catch (Exception ex)
            {
                ankiInitializeStatus.Text = "初始化失败 · " + ex.Message + " · 不自动重试";
                status.Text = "Anki 初始化失败或状态未知；请先重新只读检查，再由用户显式决定是否重试。";
            }
        }

        private static bool IsLocalAnkiProbePackageFailure(AnkiProbeDisplayResult result)
        {
            if (result == null) return true;
            return string.Equals(result.Step, "worker-start", StringComparison.Ordinal) ||
                string.Equals(result.ReasonCode, "effect.worker_exit_unknown", StringComparison.Ordinal) ||
                (result.ReasonCode ?? "").StartsWith("settings.anki-probe-", StringComparison.Ordinal);
        }

        private void SaveAndClose()
        {
            InvalidateRemNoteObservation();
            try
            {
                CaptureControlsIntoActiveProfile();
                var committedWorkflow = settingsDraft.PrepareCommit();
                settingsService.Save(committedWorkflow);
                moduleSettings.AnkiExpectedProfile = ankiProfile.Text.Trim(); moduleSettings.AnkiEndpoint = ankiEndpoint.Text.Trim();
                moduleSettings.AnkiDeckPreset = Convert.ToString(ankiDeckPreset.SelectedItem);
                moduleSettings.AnkiDeckRoutes = JsonSerializer.Deserialize<Dictionary<string, string>>(ankiDeckRoutes.Text, ContractJson.Options) ?? new Dictionary<string, string>(StringComparer.Ordinal);
                moduleSettings.ContentLanguagePracticeDefault = Convert.ToString(contentDefaultChoice.SelectedItem);
                moduleSettings.ContentProviderModel = Convert.ToString(contentModel.SelectedItem);
                moduleSettings.ContentProviderMaxRetries = (int)contentMaxRetries.Value;
                PrepareRemNoteBindingPersistence();
                moduleSettingsStore.Save(moduleSettings);
                var value = Collect();
                if (deepSeekCredential.Text.Trim() != initialDeepSeekEnvironment) {
                    SecretConfig.SetDeepSeek(deepSeekCredential.Text.Trim());
                }
                var preservedCredentials = DesktopCredentialSettings.Load(config.WorkDirectory);
                preservedCredentials.MineruApiToken = mineruCredential.Text;
                preservedCredentials.Save(config.WorkDirectory);
                value.Save(config); settingsDraft.MarkPersisted(settingsService.CurrentRevision); SavedSettings = value; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
        }

        private void BindProfiles()
        {
            changingProfile = true;
            workflowSelector.Items.Clear();
            foreach (var pair in workflowDocument.Profiles.OrderBy(item => item.Key, StringComparer.Ordinal)) workflowSelector.Items.Add(new ProfileItem(pair.Key, pair.Value.DisplayName));
            for (var index = 0; index < workflowSelector.Items.Count; index++)
                if (((ProfileItem)workflowSelector.Items[index]).Id == workflowDocument.ActiveProfileId) workflowSelector.SelectedIndex = index;
            if (workflowSelector.SelectedIndex < 0 && workflowSelector.Items.Count > 0) workflowSelector.SelectedIndex = 0;
            changingProfile = false;
            ApplyActiveProfile();
        }

        private void SwitchProfile()
        {
            if (changingProfile || workflowSelector.SelectedItem == null) return;
            CaptureControlsIntoActiveProfile();
            var id = ((ProfileItem)workflowSelector.SelectedItem).Id;
            workflowDocument = workflowDocument with { ActiveProfileId = id };
            ApplyActiveProfile();
        }

        private void CreateProfile(bool copy)
        {
            CaptureControlsIntoActiveProfile();
            var profiles = new Dictionary<string, WorkflowSettingsProfile>(workflowDocument.Profiles, StringComparer.Ordinal);
            var sequence = 1;
            string id;
            do { id = copy ? "profile-copy-" + sequence : "profile-" + sequence; sequence++; } while (profiles.ContainsKey(id));
            WorkflowSettingsProfile value;
            if (copy)
            {
                var source = profiles[workflowDocument.ActiveProfileId];
                value = source with { DisplayName = source.DisplayName + " 副本" };
            }
            else value = DesktopWorkflowSettingsService.Defaults(UserSettings.Defaults()).Profiles["default"] with { DisplayName = "新工作流" };
            profiles.Add(id, value);
            workflowDocument = new WorkflowSettingsDocument(workflowDocument.SchemaVersion, id, profiles);
            BindProfiles();
            status.Text = copy ? "已在本次编辑中复制方案；点击保存后写入。" : "已在本次编辑中新建方案；点击保存后写入。";
        }

        private void RestoreActiveProfile()
        {
            var profiles = new Dictionary<string, WorkflowSettingsProfile>(workflowDocument.Profiles, StringComparer.Ordinal);
            var current = profiles[workflowDocument.ActiveProfileId];
            profiles[workflowDocument.ActiveProfileId] = DesktopWorkflowSettingsService.Defaults(UserSettings.Defaults()).Profiles["default"] with { DisplayName = current.DisplayName };
            workflowDocument = new WorkflowSettingsDocument(workflowDocument.SchemaVersion, workflowDocument.ActiveProfileId, profiles);
            ApplyActiveProfile();
            status.Text = "已恢复当前方案的默认值；点击保存后写入。";
        }

        private void ApplyActiveProfile()
        {
            InvalidateRemNoteObservation();
            WorkflowSettingsProfile profile;
            if (!workflowDocument.Profiles.TryGetValue(workflowDocument.ActiveProfileId, out profile)) return;
            var workflow = profile.Workflow;
            processingProfile.SelectedIndex = workflow != null && workflow.ProcessingProfile == "learning" ? 1 : 0;
            var destinations = workflow == null || workflow.Destinations == null ? Array.Empty<string>() : workflow.Destinations;
            remnoteDestination.Checked = destinations.Contains("remnote", StringComparer.Ordinal);
            ankiDestination.Checked = destinations.Contains("anki", StringComparer.Ordinal);
            var capture = profile.Capture;
            contextMode.Checked = capture != null && capture.ContextEnabled == true;
            contextGranularity.SelectedIndex = capture != null && capture.ContextGranularity == "paragraph" ? 1 : 0;
            originalImageMode.Checked = capture != null && capture.ImageHandling == "original";
            var speech = profile.Speech;
            speechAutoPlay.Checked = speech == null || speech.AutoPlay != false;
            languageOverride.SelectedIndex = LanguageIndex(speech == null || speech.LanguageStrategy != "override" ? "auto" : speech.LanguageOverride);
            SelectVoice(chinese, speech, "zh-CN", initial.ChineseVoice); SelectVoice(english, speech, "en-US", initial.EnglishVoice);
            SelectVoice(spanish, speech, "es-ES", initial.SpanishVoice); SelectVoice(japanese, speech, "ja-JP", initial.JapaneseVoice); SelectVoice(korean, speech, "ko-KR", initial.KoreanVoice);
            rate.Value = ClampDecimal(speech == null ? initial.RatePercent : speech.RatePercent ?? initial.RatePercent, rate);
            volume.Value = ClampDecimal(speech == null ? initial.VolumePercent : speech.VolumePercent ?? initial.VolumePercent, volume);
            pitch.Value = ClampDecimal(speech == null ? initial.PitchHz : speech.PitchHz ?? initial.PitchHz, pitch);
            var image = profile.ImageOcr;
            if (image != null) { ocrModel.SelectedIndex = 0; ocrLanguage.SelectedIndex = image.ProviderSettings.Language == "en" ? 1 : 0; ocrTable.Checked = image.ProviderSettings.EnableTable; ocrFormula.Checked = image.ProviderSettings.EnableFormula; ocrTimeout.Value = ClampDecimal(image.ProviderSettings.TimeoutSeconds, ocrTimeout); ocrPoll.Value = ClampDecimal(image.ProviderSettings.PollIntervalSeconds, ocrPoll); ocrScript.Text = image.MineruScriptPath ?? ""; }
            var remnote = profile.Remnote;
            if (remnote != null) { remnoteRootId.Text = remnote.Target.RootRemId ?? ""; SetRemNoteSelector(remnote.Target.RootTitle); remnoteSink.SelectedIndex = remnote.Advanced.Sink == "markdown-zip" ? 1 : 0; }
            if (remnoteBoundTargetStatus != null) remnoteBoundTargetStatus.Text = CurrentRemNoteTargetDisplay();
            if (Visible) _ = RestoreRemNoteBindingAsync();
            if (destinations.Count > 0) status.Text = "当前导出目标将在新任务中冻结为 v2 快照；未满足必要配置时失败封闭。";
        }

        private void CaptureControlsIntoActiveProfile()
        {
            WorkflowSettingsProfile current;
            if (workflowDocument == null || !workflowDocument.Profiles.TryGetValue(workflowDocument.ActiveProfileId, out current) || processingProfile == null) return;
            var destinations = new List<string>();
            if (remnoteDestination.Checked) destinations.Add("remnote");
            if (ankiDestination.Checked) destinations.Add("anki");
            var selectedLanguage = SelectedLanguage();
            var speech = new SpeechSettings(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "zh-CN", Selected(chinese) }, { "en-US", Selected(english) }, { "es-ES", Selected(spanish) },
                { "ja-JP", Selected(japanese) }, { "ko-KR", Selected(korean) }
            }, (int)rate.Value, (int)volume.Value, (int)pitch.Value, null,
                selectedLanguage == "auto" ? "auto" : "override", selectedLanguage == "auto" ? null : selectedLanguage,
                new SpeechDeploymentSettings(OptionalAbsolutePath(config.EdgeTtsPath), OptionalAbsolutePath(config.FfplayPath)));
            var updated = current with
            {
                Workflow = new WorkflowRouteSettings(processingProfile.SelectedIndex == 1 ? "learning" : "cards", destinations),
                Capture = new CaptureSettings(contextMode.Checked, contextGranularity.SelectedIndex == 1 ? "paragraph" : "sentence", originalImageMode.Checked ? "original" : "ocr"),
                Speech = speech
                ,ImageOcr = new ImageOcrSettings("image-ocr-settings.v1", new ImageOcrProviderSettings("vlm", ocrLanguage.SelectedIndex == 1 ? "en" : "ch", ocrTable.Checked, ocrFormula.Checked, (int)ocrTimeout.Value, (int)ocrPoll.Value), string.IsNullOrWhiteSpace(ocrScript.Text) ? null : ocrScript.Text.Trim())
                ,Remnote = new RemNoteDestinationSettings("remnote-destination-settings.v1", remnoteDestination.Checked,
                    current.Remnote?.Target ?? new RemNoteTargetSettings(null, "剪藏"),
                    new RemNoteAdvancedSettings(remnoteSink.SelectedIndex == 1 ? "markdown-zip" : "mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd"))
            };
            var profiles = new Dictionary<string, WorkflowSettingsProfile>(workflowDocument.Profiles, StringComparer.Ordinal) { [workflowDocument.ActiveProfileId] = updated };
            workflowDocument = new WorkflowSettingsDocument(workflowDocument.SchemaVersion, workflowDocument.ActiveProfileId, profiles);
        }

        private static void SelectVoice(ComboBox combo, SpeechSettings settings, string language, string fallback)
        {
            var id = fallback;
            string configured;
            if (settings != null && settings.VoicesByLanguage != null && settings.VoicesByLanguage.TryGetValue(language, out configured)) id = configured;
            for (var index = 0; index < combo.Items.Count; index++) if (((VoiceChoice)combo.Items[index]).Id == id) { combo.SelectedIndex = index; return; }
        }

        private static decimal ClampDecimal(int value, NumericUpDown control) { return Math.Max(control.Minimum, Math.Min(control.Maximum, value)); }

        private static string OptionalAbsolutePath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : null;
        }

        private sealed class ProfileItem
        {
            internal readonly string Id;
            private readonly string displayName;
            internal ProfileItem(string id, string displayName) { Id = id; this.displayName = displayName; }
            public override string ToString() { return displayName + " · " + Id; }
        }

        private UserSettings Collect()
        {
            return new UserSettings
            {
                LanguageOverride = SelectedLanguage(), ChineseVoice = Selected(chinese), EnglishVoice = Selected(english), SpanishVoice = Selected(spanish), JapaneseVoice = Selected(japanese), KoreanVoice = Selected(korean),
                RatePercent = (int)rate.Value, VolumePercent = (int)volume.Value, PitchHz = (int)pitch.Value,
                ContextModeEnabled = contextMode.Checked, ContextGranularity = contextGranularity.SelectedIndex == 1 ? "paragraph" : "sentence", OriginalImageMode = originalImageMode.Checked
            };
        }

        private static ComboBox Choice(string[] items, int selected, string automationId)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Name = automationId, AccessibleName = automationId };
            combo.Items.AddRange(items); combo.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, selected)); return combo;
        }

        private static ComboBox Voice(VoiceChoice[] items, string selected, string automationId)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 430, Name = automationId, AccessibleName = automationId };
            combo.Items.AddRange(items); for (var i = 0; i < items.Length; i++) if (items[i].Id == selected) combo.SelectedIndex = i; if (combo.SelectedIndex < 0 && items.Length > 0) combo.SelectedIndex = 0; return combo;
        }

        private static NumericUpDown Number(int minimum, int maximum, int value, string automationId)
        {
            return new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = Math.Max(minimum, Math.Min(maximum, value)), Width = 130, Name = automationId, AccessibleName = automationId };
        }

        private static CheckBox Check(string text, bool value, string automationId)
        {
            return new CheckBox { AutoSize = true, Text = text, Checked = value, Name = automationId, AccessibleName = automationId };
        }

        private static Button ActionButton(string text, string automationId, Color background)
        {
            return new Button { Text = text, AutoSize = true, MinimumSize = new Size(96, 34), FlatStyle = FlatStyle.Flat, BackColor = background, Name = automationId, AccessibleName = automationId, Margin = new Padding(0, 0, 8, 0) };
        }

        private SemanticActionButton SemanticButton(string text, string automationId, Color background, Func<Task> command)
        {
            var button = new SemanticActionButton { Text = text, AutoSize = true, MinimumSize = new Size(96, 34), FlatStyle = FlatStyle.Flat, BackColor = background, Name = automationId, AccessibleName = automationId, Margin = new Padding(0, 0, 8, 0) };
            button.Bind(command, DesktopSemanticActionTrace.PathFor(config.WorkDirectory));
            return button;
        }

        private static Button ConditionalButton(string text, string automationId)
        {
            var button = ActionButton(text, automationId, Color.White); button.Enabled = false; Conditional(button, "等待共享 Core settings adapter"); return button;
        }

        private static void Conditional(Control control, string reason)
        {
            control.AccessibleDescription = "conditional: " + reason; control.Tag = "conditional";
        }

        private static string Selected(ComboBox combo) { var item = combo.SelectedItem as VoiceChoice; return item == null ? "" : item.Id; }
        private static int LanguageIndex(string value) { var values = new[] { "auto", "zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR" }; for (var i = 0; i < values.Length; i++) if (values[i] == value) return i; return 0; }
        private string SelectedLanguage() { return new[] { "auto", "zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR" }[Math.Max(0, languageOverride.SelectedIndex)]; }
    }
}

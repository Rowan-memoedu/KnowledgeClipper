using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal enum DesktopLaunchView
    {
        Default,
        Settings,
        Question
    }

    internal static class DesktopLaunchOptions
    {
        internal static bool TryParse(string[] args, out DesktopLaunchView view)
            => TryParse(args, out view, out _);

        internal static bool TryParse(string[] args, out DesktopLaunchView view, out string captureFixturePath)
        {
            view = DesktopLaunchView.Default;
            captureFixturePath = null;
            if (args == null || args.Length == 0) return true;
            if (args.Length == 3 && args[0] == "--open-settings" && args[1] == "--candidate-capture-fixture" && Path.IsPathFullyQualified(args[2]))
            {
                captureFixturePath = args[2]; view = DesktopLaunchView.Settings; return true;
            }
            if (args.Length != 1) return false;
            if (string.Equals(args[0], "--open-settings", StringComparison.Ordinal)) { view = DesktopLaunchView.Settings; return true; }
            if (string.Equals(args[0], "--open-question", StringComparison.Ordinal)) { view = DesktopLaunchView.Question; return true; }
            return false;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            InitializeDpi();
            if (args != null && args.Length == 1 && string.Equals(args[0], "--startup-diagnostic", StringComparison.Ordinal))
                return StartupDiagnostics.Run(AppDomain.CurrentDomain.BaseDirectory, Console.Out);
            if (args != null && args.Length == 1 && string.Equals(args[0], "--single-instance-diagnostic", StringComparison.Ordinal))
                return SingleInstanceCoordinator.WriteProbe(Console.Out);
            if (args != null && args.Length == 2 && string.Equals(args[0], "--integration-startup-canary", StringComparison.Ordinal))
                return CandidateIntegrationCanary.Run(AppDomain.CurrentDomain.BaseDirectory, args[1], Console.Out);
            if (args != null && args.Length == 3 && string.Equals(args[0], "--candidate-read-to-anki-canary", StringComparison.Ordinal))
                return CandidateReadToAnkiProcessCanary.Run(AppDomain.CurrentDomain.BaseDirectory, args[1], args[2], Console.Out);

            DesktopLaunchView launchView;
            string captureFixturePath;
            if (!DesktopLaunchOptions.TryParse(args, out launchView, out captureFixturePath))
            {
                MessageBox.Show("不支持的启动参数。", "零碎知识剪藏", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            SingleInstanceLease lease;
            if (!SingleInstanceCoordinator.TryAcquire(out lease))
            {
                SingleInstanceCoordinator.NotifyExistingInstance(true);
                return 0;
            }
            using (lease)
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    var config = AppConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
                    ProductionStartupPolicy.Apply(config);
                    var credentials = DesktopCredentialSettings.Load(config.WorkDirectory);
                    var desktop = LegacyDesktopAdapter.CreateBoundClipperDependencies(config, captureFixturePath);
                    using (var form = new BoundClipperForm(desktop))
                    {
                        form.EnableLanguageWorkflow();
                        if (launchView != DesktopLaunchView.Default)
                            form.Shown += async delegate { await form.OpenLaunchViewAsync(launchView); };
                        Application.Run(form);
                    }
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "零碎知识剪藏", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
            return 0;
        }

        internal static void InitializeDpi()
        {
            // UI Automation/WPF can otherwise initialize system DPI awareness on
            // the first capture, after an unaware floating HWND already exists.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
    }

    internal static class ProductionStartupPolicy
    {
        internal const string RetentionStatus = "disabled-until-state-aware-retention-service";

        internal static void Apply(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            // Legacy JobStore.PurgeExpired is intentionally not called: it cannot distinguish
            // active, waiting_input, unknown_external_state, pinned, or evidence-bearing jobs.
        }
    }

    internal enum ReviewChoice { Discard, Original, Cleaned }
    internal sealed class ReviewResult { public ReviewChoice Choice; public string OriginalText; public string CleanedText; }

    internal sealed class ReviewForm : Form
    {
        private readonly TextBox originalBox, cleanedBox;
        private ReviewChoice choice;

        public ReviewForm(string original, string cleaned, Image preview, string problem)
        {
            Text = "需要人工确认"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(480, 340); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; TopMost = true; Font = new Font("微软雅黑", 9F);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { AutoSize = true, ForeColor = Color.Firebrick, Text = "审查未通过：" + (problem ?? "未知问题") }, 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            originalBox = Editor(original); cleanedBox = Editor(cleaned ?? "");
            var originalPage = new TabPage("原文");
            if (preview == null) originalPage.Controls.Add(originalBox);
            else
            {
                var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 105 };
                split.Panel1.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = (Image)preview.Clone(), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.WhiteSmoke });
                split.Panel2.Controls.Add(originalBox); originalPage.Controls.Add(split);
            }
            var cleanedPage = new TabPage("清洗稿"); cleanedPage.Controls.Add(cleanedBox); tabs.TabPages.Add(originalPage); tabs.TabPages.Add(cleanedPage); root.Controls.Add(tabs, 0, 1);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 5, 0, 0) };
            var keepOriginal = MakeButton("保留原文", Color.FromArgb(235, 240, 250)); var keepCleaned = MakeButton("保留清洗稿", Color.FromArgb(220, 244, 229)); var discard = MakeButton("放弃", Color.FromArgb(245, 230, 230));
            keepCleaned.Enabled = !string.IsNullOrWhiteSpace(cleaned);
            keepOriginal.Click += delegate { choice = ReviewChoice.Original; DialogResult = DialogResult.OK; Close(); };
            keepCleaned.Click += delegate { choice = ReviewChoice.Cleaned; DialogResult = DialogResult.OK; Close(); };
            discard.Click += delegate { choice = ReviewChoice.Discard; DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(keepOriginal); buttons.Controls.Add(keepCleaned); buttons.Controls.Add(discard); root.Controls.Add(buttons, 0, 2); Controls.Add(root);
        }

        private static TextBox Editor(string text) { return new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, AcceptsTab = true, WordWrap = true, Text = text ?? "", Font = new Font("Consolas", 9F) }; }
        private static Button MakeButton(string text, Color color) { return new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 34), BackColor = color, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0) }; }
        public ReviewResult Result { get { return new ReviewResult { Choice = choice, OriginalText = originalBox.Text, CleanedText = cleanedBox.Text }; } }
    }

    internal sealed class VoiceChoice
    {
        public readonly string Id, Label;
        public VoiceChoice(string id, string label) { Id = id; Label = label; }
        public override string ToString() { return Label + "  ·  " + Id; }
    }

    internal static class EdgeVoiceCatalog
    {
        public static VoiceChoice[] Chinese =
        {
            new VoiceChoice("zh-CN-XiaoxiaoNeural", "晓晓｜温暖女声"), new VoiceChoice("zh-CN-XiaoyiNeural", "晓伊｜活泼女声"),
            new VoiceChoice("zh-CN-YunjianNeural", "云健｜激情男声"), new VoiceChoice("zh-CN-YunxiNeural", "云希｜阳光男声"),
            new VoiceChoice("zh-CN-YunxiaNeural", "云夏｜可爱男声"), new VoiceChoice("zh-CN-YunyangNeural", "云扬｜专业男声"),
            new VoiceChoice("zh-CN-liaoning-XiaobeiNeural", "晓北｜辽宁方言"), new VoiceChoice("zh-CN-shaanxi-XiaoniNeural", "晓妮｜陕西方言")
        };
        public static VoiceChoice[] English =
        {
            new VoiceChoice("en-US-AvaMultilingualNeural", "Ava｜亲切女声"), new VoiceChoice("en-US-AndrewMultilingualNeural", "Andrew｜沉稳男声"),
            new VoiceChoice("en-US-EmmaMultilingualNeural", "Emma｜清晰女声"), new VoiceChoice("en-US-BrianMultilingualNeural", "Brian｜自然男声"),
            new VoiceChoice("en-US-JennyNeural", "Jenny｜友好女声"), new VoiceChoice("en-US-ChristopherNeural", "Christopher｜播报男声")
        };
        public static VoiceChoice[] Spanish = { new VoiceChoice("es-ES-XimenaNeural", "Ximena｜女声"), new VoiceChoice("es-ES-ElviraNeural", "Elvira｜女声"), new VoiceChoice("es-ES-AlvaroNeural", "Álvaro｜男声") };
        public static VoiceChoice[] Japanese = { new VoiceChoice("ja-JP-NanamiNeural", "七海｜女声"), new VoiceChoice("ja-JP-KeitaNeural", "圭太｜男声") };
        public static VoiceChoice[] Korean = { new VoiceChoice("ko-KR-SunHiNeural", "SunHi｜女声"), new VoiceChoice("ko-KR-HyunsuMultilingualNeural", "Hyunsu｜多语言男声"), new VoiceChoice("ko-KR-InJoonNeural", "InJoon｜男声") };
    }

    internal enum StripMode { Empty, Processing, Success, Error, Notice, Replay }
    internal sealed class ClipboardContent : IDisposable
    {
        public string Text;
        public Image Image;
        public string SourceType;
        public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
    }

    internal sealed class ClipperForm : Form
    {
        private const int ButtonAreaHeight = 48, StatusAreaHeight = 22;
        private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3, WS_EX_NOACTIVATE = 0x08000000;
        private readonly AppConfig config; private readonly RemNoteClient remnote; private readonly DeepSeekCleaner cleaner; private readonly MinerUOcr ocr; private readonly TtsService tts; private readonly NotifyIcon tray; private readonly ProcessAudio audio; private UserSettings settings;
        private bool busy, moved; private Point mouseDownScreen, formDown; private StripMode stripMode; private string stripText, lastAudioPath; private Color stripColor; private int stripVersion;

        public ClipperForm(AppConfig config)
        {
            this.config = config; settings = UserSettings.Load(config); remnote = new RemNoteClient(config); cleaner = new DeepSeekCleaner(config); ocr = new MinerUOcr(config, remnote); tts = new TtsService(config); audio = new ProcessAudio(config.FfplayPath); audio.PlaybackEnded += OnPlaybackEnded;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; ClientSize = new Size(156, ButtonAreaHeight + StatusAreaHeight); Location = LoadPosition(); BackColor = Color.White; DoubleBuffered = true; Cursor = Cursors.Hand;
            tray = new NotifyIcon { Visible = true, Text = "零碎知识剪藏", Icon = SystemIcons.Application, ContextMenuStrip = BuildMenu() };
            tray.DoubleClick += delegate { OpenSettings(); }; ContextMenuStrip = tray.ContextMenuStrip;
            MouseDown += OnMouseDown; MouseMove += OnMouseMove; MouseUp += OnMouseUp;
            Shown += async delegate { try { await remnote.GetMineruTokenAsync(); } catch { } };
            FormClosed += delegate { audio.Dispose(); tray.Visible = false; tray.Dispose(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE; return cp; } }
        protected override void WndProc(ref Message m) { if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; } base.WndProc(ref m); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White); using (var border = new Pen(Color.FromArgb(170, 180, 190))) e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            var width = ClientSize.Width / 3; using (var pen = new Pen(Color.FromArgb(215, 220, 225))) { e.Graphics.DrawLine(pen, width, 5, width, ButtonAreaHeight - 5); e.Graphics.DrawLine(pen, width * 2, 5, width * 2, ButtonAreaHeight - 5); e.Graphics.DrawLine(pen, 0, ButtonAreaHeight, ClientSize.Width, ButtonAreaHeight); }
            var labels = new[] { "摘录", "朗读", "截图" };
            using (var font = new Font("微软雅黑", 11, FontStyle.Bold, GraphicsUnit.Pixel)) using (var brush = new SolidBrush(Color.FromArgb(45, 55, 65)))
                for (var i = 0; i < 3; i++) e.Graphics.DrawString(labels[i], font, brush, new Rectangle(i * width, 0, width, ButtonAreaHeight), Center());
            var statusRect = new Rectangle(1, ButtonAreaHeight + 1, ClientSize.Width - 2, StatusAreaHeight - 2);
            if (stripMode != StripMode.Empty)
            {
                if (stripMode == StripMode.Replay)
                {
                    using (var fill = new SolidBrush(Color.FromArgb(238, 245, 255))) e.Graphics.FillRectangle(fill, statusRect);
                    using (var pen = new Pen(Color.FromArgb(130, 170, 225))) e.Graphics.DrawRectangle(pen, statusRect.X, statusRect.Y, statusRect.Width - 1, statusRect.Height - 1);
                }
                using (var font = new Font("微软雅黑", 10, FontStyle.Bold, GraphicsUnit.Pixel)) using (var brush = new SolidBrush(stripColor)) e.Graphics.DrawString(stripText ?? "", font, brush, statusRect, Center());
            }
        }

        private static StringFormat Center() { return new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; }
        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip(); var settingsItem = menu.Items.Add("设置…", null, delegate { OpenSettings(); }); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("打开数据目录", null, delegate { Process.Start(new ProcessStartInfo(config.WorkDirectory) { UseShellExecute = true }); }); menu.Items.Add("打开日志", null, delegate { OpenLog(); }); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出", null, delegate { Close(); }); return menu;
        }

        private async void OpenSettings()
        {
            using (var dialog = new SettingsForm(config, settings.Copy()))
            {
                if (dialog.ShowDialog() == DialogResult.OK && dialog.SavedSettings != null) { settings = dialog.SavedSettings; Log("SETTINGS provider=edge-tts voices-updated"); await ShowTemporaryStripAsync(StripMode.Notice, "设置已保存", Color.FromArgb(38, 116, 242), 1200, !string.IsNullOrWhiteSpace(lastAudioPath)); }
            }
        }

        private void OnMouseDown(object sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; mouseDownScreen = Cursor.Position; formDown = Location; moved = false; }
        private void OnMouseMove(object sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; var now = Cursor.Position; var dx = now.X - mouseDownScreen.X; var dy = now.Y - mouseDownScreen.Y; if (Math.Abs(dx) + Math.Abs(dy) > 4) moved = true; if (moved) Location = new Point(formDown.X + dx, formDown.Y + dy); }
        private async void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return; if (moved) { SavePosition(); return; }
            if (e.Y >= ButtonAreaHeight) { ToggleReplay(); return; }
            if (busy) return;
            var slot = Math.Min(2, Math.Max(0, e.X * 3 / Math.Max(1, ClientSize.Width)));
            if (slot == 0) await CaptureSelectionAsync(); else if (slot == 1) await ReadAloudAsync(); else await ScreenshotAsync();
        }

        private async Task CaptureSelectionAsync()
        {
            if (busy) return; busy = true; SetStrip(StripMode.Processing, "处理中…", Color.FromArgb(85, 95, 105));
            try { using (var content = await CaptureSelectionOrClipboardAsync()) { await RunClipWorkflowAsync(content.Text, content.Image == null ? null : (Image)content.Image.Clone(), content.SourceType); } }
            catch (Exception ex) { InfrastructureError("摘录", ex, false); }
            finally { busy = false; Invalidate(); }
        }

        private async Task ScreenshotAsync()
        {
            if (busy) return; busy = true; SetStrip(StripMode.Processing, "等待截图…", Color.FromArgb(85, 95, 105)); var before = NativeClipboard.SequenceNumber(); Hide();
            try
            {
                await Task.Delay(150); NativeKeyboard.SendScreenshotShortcut(); Image image = null;
                for (var i = 0; i < 360; i++)
                {
                    await Task.Delay(250); if (NativeKeyboard.IsEscapeDown()) break;
                    if (NativeClipboard.SequenceNumber() != before && Clipboard.ContainsImage()) { image = (Image)Clipboard.GetImage().Clone(); break; }
                }
                Show(); TopMost = true; if (image == null) { await ShowTemporaryStripAsync(StripMode.Notice, "已取消截图", Color.Gray, 1200, false); return; }
                SetStrip(StripMode.Processing, "处理中…", Color.FromArgb(85, 95, 105)); await RunClipWorkflowAsync(null, image, "screenshot");
            }
            catch (Exception ex) { Show(); InfrastructureError("截图", ex, false); }
            finally { if (!Visible) Show(); busy = false; Invalidate(); }
        }

        private async Task RunClipWorkflowAsync(string text, Image image, string sourceType)
        {
            var job = JobStore.Create(config); Image preview = image;
            try
            {
                text = await PrepareTextAsync(text, preview, job);
                var written = await ProcessClipTextAsync(text, preview, sourceType, job);
                if (written) await ShowSuccessAsync(false); else await ShowTemporaryStripAsync(StripMode.Notice, "已放弃摘录", Color.Gray, 1200, false);
            }
            catch (Exception ex) { InfrastructureError(sourceType, ex, false); }
            finally { if (preview != null) preview.Dispose(); }
        }

        private async Task ReadAloudAsync()
        {
            if (busy) return; busy = true; audio.Stop(); lastAudioPath = null; SetStrip(StripMode.Processing, "处理中…", Color.FromArgb(85, 95, 105));
            try
            {
                using (var content = await CaptureSelectionOrClipboardAsync())
                {
                    var job = JobStore.Create(config); var text = await PrepareTextAsync(content.Text, content.Image, job);
                    var clipTask = ProcessClipTextAsync(text, content.Image, "read-" + content.SourceType, job);
                    var speechTask = SynthesizeAndPlayAsync(text, job);
                    bool written = false; string audioPath = null; Exception clipError = null, speechError = null;
                    try { written = await clipTask; } catch (Exception ex) { clipError = ex; }
                    try { audioPath = await speechTask; } catch (Exception ex) { speechError = ex; }
                    if (!string.IsNullOrWhiteSpace(audioPath)) lastAudioPath = audioPath;
                    if (clipError != null || speechError != null)
                    {
                        var problem = clipError != null && speechError != null ? new InvalidOperationException("摘录失败：" + clipError.Message + "；朗读失败：" + speechError.Message) : (clipError ?? speechError);
                        InfrastructureError(clipError != null ? "摘录/朗读" : "朗读", problem, !string.IsNullOrWhiteSpace(lastAudioPath));
                    }
                    else if (!written) await ShowTemporaryStripAsync(StripMode.Notice, "已放弃摘录", Color.Gray, 1200, true);
                    else await ShowSuccessAsync(true);
                }
            }
            catch (Exception ex) { InfrastructureError("朗读", ex, false); }
            finally { busy = false; Invalidate(); }
        }

        private async Task<ClipboardContent> CaptureSelectionOrClipboardAsync()
        {
            var before = NativeClipboard.SequenceNumber(); NativeKeyboard.SendCopyShortcut(); await Task.Delay(350); var copied = NativeClipboard.SequenceNumber() != before;
            string text; Image image; ReadClipboard(out text, out image);
            if (string.IsNullOrWhiteSpace(text) && image == null) throw new InvalidOperationException("未选中文本，剪贴板中也没有可用的文本或图片");
            return new ClipboardContent { Text = text, Image = image, SourceType = copied && !string.IsNullOrWhiteSpace(text) ? "selection" : (image != null ? "clipboard-image" : "clipboard-text") };
        }

        private async Task<string> PrepareTextAsync(string text, Image image, string job)
        {
            if (string.IsNullOrWhiteSpace(text) && image == null) throw new InvalidOperationException("没有可处理的文本或图片");
            if (image != null) { text = await ocr.ExtractAsync(image, job); JobStore.WriteText(job, "mineru-original.md", text); }
            else JobStore.WriteText(job, "captured-original.txt", text);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("未得到可处理的文本");
            return text;
        }

        private async Task<bool> ProcessClipTextAsync(string text, Image preview, string sourceType, string job)
        {
            var outcome = await cleaner.ReviewAsync(text); JobStore.WriteText(job, "deepseek-cleaned.md", outcome.CleanedText); var chosen = outcome.CleanedText;
            if (!outcome.Success)
            {
                JobStore.WriteText(job, "deepseek-review-problem.txt", outcome.Problem);
                ReviewResult review; using (var dialog = new ReviewForm(text, outcome.CleanedText, preview, outcome.Problem)) { dialog.ShowDialog(); review = dialog.Result; }
                JobStore.WriteText(job, "review-original.md", review.OriginalText); JobStore.WriteText(job, "review-cleaned.md", review.CleanedText); JobStore.WriteText(job, "decision.txt", review.Choice.ToString());
                if (review.Choice == ReviewChoice.Discard) return false;
                chosen = review.Choice == ReviewChoice.Original ? review.OriginalText : review.CleanedText;
            }
            if (string.IsNullOrWhiteSpace(chosen)) throw new InvalidOperationException("拒绝写入空内容");
            await remnote.CheckStatusAsync(); var container = await remnote.CreateClipContainerAsync(DateTime.Now); await remnote.InsertAsync(container, chosen); JobStore.WriteText(job, "written-rem-id.txt", container);
            Log("SUCCESS source=" + sourceType + " chars=" + chosen.Length); return true;
        }

        private async Task<string> SynthesizeAndPlayAsync(string text, string job)
        {
            var path = await tts.SynthesizeAsync(text, job, settings); lastAudioPath = path; audio.Play(path); Log("TTS provider=edge-tts language=" + LanguageRouter.Detect(text) + " voice=" + settings.Voice(LanguageRouter.Detect(text)) + " chars=" + text.Length); return path;
        }

        private static void ReadClipboard(out string text, out Image image)
        {
            text = null; image = null;
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) { var value = Clipboard.GetText(TextDataFormat.UnicodeText); if (!string.IsNullOrWhiteSpace(value)) text = value; }
            if (string.IsNullOrWhiteSpace(text) && Clipboard.ContainsImage()) image = (Image)Clipboard.GetImage().Clone();
        }

        private int SetStrip(StripMode mode, string text, Color color) { stripMode = mode; stripText = text; stripColor = color; stripVersion++; Invalidate(); return stripVersion; }
        private async Task ShowTemporaryStripAsync(StripMode mode, string text, Color color, int milliseconds, bool replayAfter)
        {
            var version = SetStrip(mode, text, color); await Task.Delay(milliseconds); if (version != stripVersion) return; if (replayAfter && !string.IsNullOrWhiteSpace(lastAudioPath)) SetAudioStrip(); else SetStrip(StripMode.Empty, null, Color.Empty);
        }
        private async Task ShowSuccessAsync(bool replayAfter) { await ShowTemporaryStripAsync(StripMode.Success, "✓", Color.FromArgb(35, 166, 92), 1100, replayAfter); }
        private async void InfrastructureError(string stage, Exception ex, bool replayAfter)
        {
            Log("ERROR stage=" + stage + " " + ex.GetType().Name + ": " + ex.Message); tray.ShowBalloonTip(4500, stage + " 失败", ex.Message, ToolTipIcon.Error); await ShowTemporaryStripAsync(StripMode.Error, "! " + ShortStatus(ex.Message), Color.FromArgb(218, 68, 83), 2200, replayAfter);
        }
        private static string ShortStatus(string text) { text = (text ?? "处理失败").Replace("\r", " ").Replace("\n", " ").Trim(); return text.Length > 18 ? text.Substring(0, 18) + "…" : text; }
        private void ToggleReplay()
        {
            if (busy || string.IsNullOrWhiteSpace(lastAudioPath)) return;
            try { audio.Play(lastAudioPath); SetAudioStrip(); }
            catch (Exception ex) { InfrastructureError("重播", ex, true); }
        }
        private void SetAudioStrip() { if (string.IsNullOrWhiteSpace(lastAudioPath)) SetStrip(StripMode.Empty, null, Color.Empty); else SetStrip(StripMode.Replay, "▶ 重播", Color.FromArgb(38, 116, 242)); }
        private void OnPlaybackEnded(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!busy && stripMode != StripMode.Success && stripMode != StripMode.Error && stripMode != StripMode.Notice) SetAudioStrip(); }); } catch (InvalidOperationException) { }
        }
        private string LogPath { get { var p = Path.Combine(config.WorkDirectory, "logs"); Directory.CreateDirectory(p); return Path.Combine(p, "app.log"); } }
        private void Log(string message) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine, Encoding.UTF8); } catch { } }
        private void OpenLog() { if (!File.Exists(LogPath)) File.WriteAllText(LogPath, "", Encoding.UTF8); Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true }); }
        private Point LoadPosition() { try { var path = Path.Combine(config.WorkDirectory, "position.txt"); if (File.Exists(path)) { var parts = File.ReadAllText(path).Split(','); int x, y; if (parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y)) return new Point(x, y); } } catch { } var area = Screen.PrimaryScreen.WorkingArea; return new Point(area.Right - 176, area.Top + area.Height / 3); }
        private void SavePosition() { try { File.WriteAllText(Path.Combine(config.WorkDirectory, "position.txt"), Location.X + "," + Location.Y); } catch { } }
    }

    internal static class NativeClipboard
    {
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        internal static uint SequenceNumber() { return GetClipboardSequenceNumber(); }
    }

    internal sealed class ProcessAudio : IDisposable
    {
        private readonly string ffplayPath; private readonly object gate = new object(); private Process process;
        public ProcessAudio(string ffplayPath) { this.ffplayPath = ffplayPath; }
        public event EventHandler PlaybackEnded;
        public bool IsPlaying { get { lock (gate) { try { return process != null && !process.HasExited; } catch { return false; } } } }
        public void Play(string path)
        {
            Stop(); if (!File.Exists(ffplayPath)) throw new FileNotFoundException("找不到 ffplay", ffplayPath);
            var started = Process.Start(new ProcessStartInfo { FileName = ffplayPath, Arguments = "-nodisp -autoexit -loglevel quiet " + ProcessRunner.QuoteArgument(path), UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path) });
            if (started == null) throw new InvalidOperationException("无法启动 TTS 播放器");
            started.Exited += OnExited; lock (gate) process = started; started.EnableRaisingEvents = true;
        }
        private void OnExited(object sender, EventArgs e)
        {
            var ended = sender as Process; var notify = false;
            lock (gate) { if (ReferenceEquals(process, ended)) { process = null; notify = true; } }
            if (ended != null) { try { ended.Exited -= OnExited; ended.Dispose(); } catch { } }
            if (notify) { var handler = PlaybackEnded; if (handler != null) handler(this, EventArgs.Empty); }
        }
        public void Stop()
        {
            Process stopped; lock (gate) { stopped = process; process = null; }
            if (stopped != null) { try { stopped.Exited -= OnExited; if (!stopped.HasExited) stopped.Kill(); } catch { } try { stopped.Dispose(); } catch { } }
        }
        public void Dispose() { Stop(); }
    }
}

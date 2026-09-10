using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal sealed class ModeFramePanel : Panel
    {
        private Color frameColor = Color.FromArgb(184, 196, 207);
        private readonly Panel topBorder, bottomBorder, leftBorder, rightBorder;
        private Control content;
        internal int FrameThickness { get; set; } = 2;

        internal ModeFramePanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            topBorder = Border("top"); bottomBorder = Border("bottom"); leftBorder = Border("left"); rightBorder = Border("right");
            Controls.Add(topBorder); Controls.Add(bottomBorder); Controls.Add(leftBorder); Controls.Add(rightBorder);
        }

        private Panel Border(string edge)
        {
            return new Panel { Name = "knowledge-clipper.desktop.frame-edge." + edge, Margin = Padding.Empty, BackColor = frameColor, TabStop = false };
        }

        internal void SetContent(Control value)
        {
            content = value; value.Dock = DockStyle.None; Controls.Add(value); PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var visibleWidth = ClientSize.Width; var visibleHeight = ClientSize.Height;
            if (Parent != null)
            {
                visibleWidth = Math.Min(visibleWidth, Math.Max(0, Parent.DisplayRectangle.Right - Left));
                visibleHeight = Math.Min(visibleHeight, Math.Max(0, Parent.DisplayRectangle.Bottom - Top));
            }
            var edge = FrameThickness;
            if (content != null) content.Bounds = new Rectangle(edge, edge, Math.Max(0, visibleWidth - edge * 2), Math.Max(0, visibleHeight - edge * 2));
            topBorder.Bounds = new Rectangle(0, 0, visibleWidth, edge);
            bottomBorder.Bounds = new Rectangle(0, Math.Max(0, visibleHeight - edge), visibleWidth, edge);
            leftBorder.Bounds = new Rectangle(0, edge, edge, Math.Max(0, visibleHeight - edge * 2));
            rightBorder.Bounds = new Rectangle(Math.Max(0, visibleWidth - edge), edge, edge, Math.Max(0, visibleHeight - edge * 2));
            topBorder.BringToFront(); bottomBorder.BringToFront(); leftBorder.BringToFront(); rightBorder.BringToFront();
        }

        internal Color FrameColor
        {
            get { return frameColor; }
            set { if (frameColor == value) return; frameColor = value; topBorder.BackColor = value; bottomBorder.BackColor = value; leftBorder.BackColor = value; rightBorder.BackColor = value; Invalidate(true); }
        }
    }

    internal sealed partial class BoundClipperForm : Form, IDesktopApplicationView
    {
        private static readonly Size DefaultClientSize = new Size(344, 210);
        private const int ResizeFrameThickness = 7;
        private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3, WS_EX_NOACTIVATE = 0x08000000, GWL_EXSTYLE = -20;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
        private readonly AppConfig config;
        private readonly DesktopController controller;
        private readonly Func<UserSettings, SettingsForm> settingsFormFactory;
        private readonly IQuestionAnswerArtifactReader questionAnswers;
        private readonly IOcrHumanResolutionArtifactReader ocrHumanResolution;
        private readonly DesktopWorkflowSettingsService workflowSettingsService;
        private readonly Button contextButton, originalButton, clipButton, readButton, screenshotButton, statusButton, minimizeButton, closeButton;
        private readonly TextBox inlineQuestionInput;
        private readonly Panel dragFrame, contextSection, originalSection;
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem contextItem, originalItem;
        private readonly ToolTip toolTips;
        private UserSettings settings;
        private bool busy;
        private bool inlineInputActive, inlineActivationPending, inlineSubmitInProgress;
        private IntPtr previousForegroundWindow;
        private int statusVersion;
        private ScreenshotSessionForm screenshotSession;
        private SettingsForm settingsForm;
        private FlashcardPreviewForm flashcardPreviewForm;
        private FlashcardDraftPreviewUseCases flashcardDraftUseCases;
        private DesktopViewBinding applicationBinding;
        private QuestionUseCases questionUseCases;
        private ReadToAnkiViewProjection readToAnkiView;
        private string readToAnkiChoiceOperationId;
        private string readToAnkiResolutionOperationId;
        private long readToAnkiTerminalRevision;
        private string workflowSettingsRevision;
        private readonly List<FloatingControlMetrics> floatingMetrics = new List<FloatingControlMetrics>();
        private bool applyingContentScale, scalingForDpi;

        private sealed class FloatingControlMetrics
        {
            internal Control Control;
            internal Padding Margin, Padding;
            internal Font BaseFont, OwnedFont;
        }
        internal int ResizeGestureCount { get; private set; }
        internal int LastResizeGestureHit { get; private set; }
        internal IntPtr LastQuestionRestoreForeground { get; private set; }
        internal bool LastQuestionRestoreSucceeded { get; private set; }
        internal FlashcardDraftPreviewState CandidateCanaryFlashcardState => flashcardDraftUseCases?.Current;
        internal ReadToAnkiViewProjection CandidateCanaryReadToAnkiState => readToAnkiView;

        internal bool InvokeCandidateCanaryAction(string automationId)
        {
            if (string.Equals(automationId, AutomationCatalogV11.AutomationIds[AutomationCatalog.ReadCapture], StringComparison.Ordinal)) { readButton.PerformClick(); return true; }
            if (flashcardPreviewForm != null && !flashcardPreviewForm.IsDisposed)
            {
                var control = flashcardPreviewForm.Controls.Find(automationId, true).OfType<Button>().SingleOrDefault();
                if (control != null) { control.PerformClick(); return true; }
            }
            return false;
        }

        internal BoundClipperForm(AppConfig config) : this(LegacyDesktopAdapter.CreateBoundClipperDependencies(config)) { }

        internal BoundClipperForm(AppConfig config, DesktopController controller, bool initializeModes)
            : this(LegacyDesktopAdapter.CreateBoundClipperDependencies(config, controller, initializeModes)) { }

        internal BoundClipperForm(BoundClipperDependencies dependencies)
        {
            if (dependencies == null) throw new ArgumentNullException(nameof(dependencies));
            SuspendLayout();
            config = dependencies.Config;
            controller = dependencies.Controller;
            settings = dependencies.InitialSettings.Copy();
            workflowSettingsService = new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory);
            ReloadAuthoritativeCaptureModes();
            MigrateLegacyCaptureModeAliases();
            settingsFormFactory = dependencies.SettingsFormFactory;
            questionAnswers = dependencies.QuestionAnswers;
            ocrHumanResolution = dependencies.OcrHumanResolution;
            Name = "knowledge-clipper.desktop.floating-window"; AccessibleName = Name;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            MinimumSize = DefaultClientSize; ResizeRedraw = true; ClientSize = LoadSize(); Location = LoadPosition(); BackColor = Color.FromArgb(246, 248, 250);
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = new Padding(2), BackColor = Color.FromArgb(222, 229, 236) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // Every functional band participates in resize.  Absolute row heights made
            // a larger window look like an enlarged frame with unchanged contents.
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 13.333F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 46.667F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 14.286F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 25.714F));
            var titleBar = new TableLayoutPanel { Name = AutomationCatalogV11.WindowTitleBarAutomationId, AccessibleName = AutomationCatalogV11.WindowTitleBarAutomationId, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(2), Padding = Padding.Empty, BackColor = Color.FromArgb(246, 248, 250), Cursor = Cursors.SizeAll };
            titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80)); titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10)); titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));
            var titleLabel = new Label { Text = "零碎知识剪藏", Name = AutomationCatalogV11.WindowTitleAutomationId, AccessibleName = "零碎知识剪藏", AccessibleDescription = "桌面知识采集悬浮窗", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = Color.FromArgb(246, 248, 250), ForeColor = Color.FromArgb(79, 94, 108), Font = new Font("微软雅黑", 8.5F), Cursor = Cursors.SizeAll };
            minimizeButton = WindowButton("—", AutomationCatalogV11.WindowMinimizeAutomationId, false); minimizeButton.AccessibleDescription = "最小化到系统托盘";
            closeButton = WindowButton("×", AutomationCatalogV11.WindowCloseAutomationId, true); closeButton.AccessibleDescription = "关闭悬浮窗并退出";
            titleBar.Controls.Add(titleLabel, 0, 0); titleBar.Controls.Add(minimizeButton, 1, 0); titleBar.Controls.Add(closeButton, 2, 0);
            var sections = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(2), BackColor = Color.FromArgb(246, 248, 250) };
            sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F)); sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F)); sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            clipButton = ActionButton("摘录", AutomationCatalogV11.AutomationIds[AutomationCatalog.ClipCapture]);
            readButton = ActionButton("朗读", AutomationCatalogV11.AutomationIds[AutomationCatalog.ReadCapture]);
            screenshotButton = ActionButton("截图", AutomationCatalogV11.AutomationIds[AutomationCatalog.ScreenshotStart]);
            contextButton = ModeButton("上下文模式", AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextSet]);
            originalButton = ModeButton("原图模式", AutomationCatalogV11.AutomationIds[AutomationCatalog.OriginalImageSet]);
            contextSection = ModeSection("knowledge-clipper.desktop.section.context", 2, contextButton, clipButton, readButton);
            originalSection = ModeSection("knowledge-clipper.desktop.section.original-image", 1, originalButton, screenshotButton);
            statusButton = ActionButton("", AutomationCatalogV11.StatusAutomationId); statusButton.Font = new Font("微软雅黑", 9F, FontStyle.Bold); statusButton.BackColor = Color.FromArgb(250, 251, 253); statusButton.Cursor = Cursors.Default; statusButton.Margin = new Padding(2);
            inlineQuestionInput = new TextBox
            {
                Name = AutomationCatalogV12.InlineQuestionInputAutomationId,
                AccessibleName = AutomationCatalogV12.InlineQuestionInputAutomationId,
                AccessibleDescription = "输入问题，按 Enter 提问",
                PlaceholderText = "输入问题，按 Enter 提问",
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 4, 4, 3),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("微软雅黑", 9.5F),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(43, 57, 70),
                Multiline = false,
                AutoSize = false,
                TabStop = true,
                ShortcutsEnabled = true
            };
            sections.Controls.Add(contextSection, 0, 0); sections.SetColumnSpan(contextSection, 2); sections.Controls.Add(originalSection, 2, 0);
            root.Controls.Add(titleBar, 0, 0); root.Controls.Add(sections, 0, 1); root.Controls.Add(statusButton, 0, 2); root.Controls.Add(inlineQuestionInput, 0, 3);
            dragFrame = new Panel { Name = AutomationCatalogV11.DragFrameAutomationId, AccessibleName = AutomationCatalogV11.DragFrameAutomationId, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(6), BackColor = Color.FromArgb(156, 170, 183), Cursor = Cursors.SizeAll };
            dragFrame.Controls.Add(root); Controls.Add(dragFrame);
            CaptureFloatingMetrics(dragFrame);
            ClientSizeChanged += delegate { ApplyFloatingContentScale(); };
            Shown += delegate { ApplyFloatingContentScale(); Location = EnsureVisible(Location); };
            DpiChanged += delegate { BeginInvoke((MethodInvoker)ApplyFloatingContentScale); };
            Disposed += delegate { foreach (var metric in floatingMetrics) { metric.OwnedFont?.Dispose(); metric.BaseFont?.Dispose(); } };
            ApplyFloatingContentScale();

            clipButton.Click += async delegate
            {
                if (languageFlow != null) await languageFlow.StartAsync(false);
                else if (applicationBinding != null) await ExecuteReadToAnkiAsync(ApplicationCommandKind.ClipToAnki);
                else await ExecuteAsync(AutomationCatalog.ClipCapture, null);
            };
            readButton.Click += async delegate
            {
                if (languageFlow != null) await languageFlow.StartAsync(true);
                else if (applicationBinding != null) await ExecuteReadToAnkiAsync(ApplicationCommandKind.ReadToAnki);
                else await ExecuteAsync(AutomationCatalog.ReadCapture, null);
            };
            screenshotButton.Click += async delegate { await ExecuteScreenshotAsync(AutomationCatalog.ScreenshotStart); };
            contextButton.Click += async delegate { await SetContextModeAsync(!settings.ContextModeEnabled); };
            originalButton.Click += async delegate { await SetOriginalImageModeAsync(!settings.OriginalImageMode); };
            statusButton.Click += async delegate
            {
                if (languageFlow != null) languageFlow.ReplayAndShowRecent();
                else if (statusButton.Text == "查看答案") await OpenQuestionAsync();
                else if (applicationBinding != null && readToAnkiView != null && readToAnkiView.CanReplay && statusButton.Text.Contains("重播")) await ExecuteReadToAnkiAsync(ApplicationCommandKind.ReadToAnkiReplay);
                else if (statusButton.Text == "▶ 重播") await ExecuteAsync(AutomationCatalog.PlaybackReplay, null);
            };
            inlineQuestionInput.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || inlineInputActive) return;
                inlineActivationPending = true;
                // WM_MOUSEACTIVATE has already chosen MA_NOACTIVATE for this click. Queue the
                // real activation after the current mouse-down message instead of depending on
                // a MouseUp/Click that Windows may route to the previously foreground window.
                BeginInvoke((MethodInvoker)delegate
                {
                    if (inlineActivationPending && !inlineInputActive) BeginInlineQuestionInput();
                });
            };
            inlineQuestionInput.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || !inlineActivationPending) return;
                BeginInvoke((MethodInvoker)delegate { BeginInlineQuestionInput(); });
            };
            inlineQuestionInput.Click += delegate
            {
                if (!inlineActivationPending) return;
                BeginInvoke((MethodInvoker)delegate { BeginInlineQuestionInput(); });
            };
            inlineQuestionInput.Leave += delegate { if (!inlineSubmitInProgress) RestoreNoActivate(); };
            inlineQuestionInput.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true; e.Handled = true; inlineQuestionInput.Clear(); RestoreNoActivate(); return;
                }
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true; e.Handled = true;
                if (!string.IsNullOrWhiteSpace(inlineQuestionInput.Text)) await SubmitInlineQuestionAsync();
            };
            minimizeButton.Click += delegate { HideToTray(); };
            closeButton.Click += delegate { Close(); };
            statusButton.MouseDown += DragMouseDown; statusButton.MouseMove += DragMouseMove; statusButton.MouseUp += DragMouseUp;
            dragFrame.MouseDown += DragMouseDown; dragFrame.MouseMove += DragMouseMove; dragFrame.MouseUp += DragMouseUp;
            titleBar.MouseDown += DragMouseDown; titleBar.MouseMove += DragMouseMove; titleBar.MouseUp += DragMouseUp;
            titleLabel.MouseDown += DragMouseDown; titleLabel.MouseMove += DragMouseMove; titleLabel.MouseUp += DragMouseUp;
            toolTips = new ToolTip(); toolTips.SetToolTip(minimizeButton, "最小化到托盘"); toolTips.SetToolTip(closeButton, "关闭并退出");

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示悬浮窗", null, delegate { RestoreFromTray(); }).Name = "knowledge-clipper.desktop.menu.window.restore";
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("提问…", null, async delegate { await OpenQuestionAsync(); }).Name = AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionOpen];
            contextItem = new ToolStripMenuItem("上下文模式") { CheckOnClick = true, Checked = settings.ContextModeEnabled, Name = "knowledge-clipper.desktop.menu.mode.context" };
            originalItem = new ToolStripMenuItem("原图模式") { CheckOnClick = true, Checked = settings.OriginalImageMode, Name = "knowledge-clipper.desktop.menu.mode.original-image" };
            contextItem.Click += async delegate { await SetContextModeAsync(contextItem.Checked); };
            originalItem.Click += async delegate { await SetOriginalImageModeAsync(originalItem.Checked); };
            menu.Items.Add(contextItem); menu.Items.Add(originalItem); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("重播上一条 / 最近朗读", null, delegate { languageFlow?.ReplayAndShowRecent(); });
            var stopRead = menu.Items.Add("停止朗读", null, async delegate
            {
                if (languageFlow != null) { languageFlow.Stop(); return; }
                var capturedTarget = readToAnkiView?.PlaybackTarget;
                if (applicationBinding != null && readToAnkiView?.CanStop == true && capturedTarget != null)
                    await applicationBinding.InvokeAsync(InvocationSource.Pointer,
                        new ApplicationCommand(ApplicationCommandKind.ReadToAnkiStop) { PlaybackTarget = capturedTarget }, CancellationToken.None);
            });
            stopRead.Name = "knowledge-clipper.desktop.playback.stop";
            stopRead.AccessibleName = stopRead.Name;
            stopRead.Enabled = false;
            menu.Opening += async delegate
            {
                if (languageFlow != null) { stopRead.Enabled = languageFlow.IsPlaying; return; }
                stopRead.Enabled = false;
                if (dependencies.Application?.ReadToAnkiUseCases == null) return;
                await dependencies.Application.ReadToAnkiUseCases.RefreshPlaybackControlAsync(CancellationToken.None);
                if (!IsDisposed) stopRead.Enabled = readToAnkiView?.CanStop == true;
            };
            menu.Items.Add("最近草稿 / 朗读", null, async delegate { if (languageFlow != null) languageFlow.ShowRecent(); else await ExecuteAsync(AutomationCatalog.TaskStatusQueryRecent, null); }).Name = AutomationCatalogV11.AutomationIds[AutomationCatalog.TaskStatusQueryRecent];
            menu.Items.Add("设置…", null, delegate { OpenSettings(); }).Name = "knowledge-clipper.desktop.menu.settings";
            menu.Items.Add("其他功能设置…", null, delegate { OpenSettings(true); });
            menu.Items.Add("恢复默认大小", null, delegate { RestoreDefaultSize(); }).Name = "knowledge-clipper.desktop.menu.window.restore-size";
            menu.Items.Add("打开数据目录", null, delegate { Process.Start(new ProcessStartInfo(config.WorkDirectory) { UseShellExecute = true }); });
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出", null, delegate { Close(); });
            ContextMenuStrip = menu;
            tray = new NotifyIcon { Visible = true, Text = "零碎知识剪藏", Icon = SystemIcons.Application, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { RestoreFromTray(); };
            Deactivate += delegate { if (!inlineSubmitInProgress && !inlineActivationPending) RestoreNoActivate(); };
            ResizeEnd += delegate { SaveWindowBounds(); };
            FormClosed += delegate { applicationBinding?.Dispose(); applicationBinding = null; if (flashcardDraftUseCases != null) flashcardDraftUseCases.Changed -= RenderFlashcardDraft; if (flashcardPreviewForm != null && !flashcardPreviewForm.IsDisposed) flashcardPreviewForm.Close(); if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close(); if (screenshotSession != null) screenshotSession.CloseFromOwner(); toolTips.Dispose(); tray.Visible = false; tray.Dispose(); };
            if (dependencies.Application != null)
            {
                applicationBinding = new DesktopViewBinding(dependencies.Application, this);
                questionUseCases = dependencies.Application.QuestionUseCases;
                flashcardDraftUseCases = dependencies.Application.FlashcardDraftUseCases;
                if (flashcardDraftUseCases != null) flashcardDraftUseCases.Changed += RenderFlashcardDraft;
            }
            ApplyModeVisuals();

            if (dependencies.InitializeModes)
            {
                controller.Execute(Action(AutomationCatalog.ContextSet, new SemanticActionArguments { enabled = settings.ContextModeEnabled, granularity = settings.ContextGranularity }));
                controller.Execute(Action(AutomationCatalog.OriginalImageSet, new SemanticActionArguments { enabled = settings.OriginalImageMode }));
            }
            ResumeLayout(true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        private void CaptureFloatingMetrics(Control control)
        {
            floatingMetrics.Add(new FloatingControlMetrics
            {
                Control = control, Margin = control.Margin, Padding = control.Padding,
                BaseFont = control is Button || control is Label || control is TextBox ? (Font)control.Font.Clone() : null
            });
            foreach (Control child in control.Controls) CaptureFloatingMetrics(child);
        }

        private void ApplyFloatingContentScale()
        {
            if (applyingContentScale || scalingForDpi || IsDisposed || floatingMetrics.Count == 0 || WindowState == FormWindowState.Minimized) return;
            applyingContentScale = true;
            // Always derive from immutable design metrics, never the previous resize.
            // Pixel dimensions include monitor DPI; point fonts must not apply DPI twice.
            var pixelScale = Math.Min((float)ClientSize.Width / DefaultClientSize.Width, (float)ClientSize.Height / DefaultClientSize.Height);
            var fontScale = pixelScale / (DeviceDpi / 96F);
            foreach (var metric in floatingMetrics) metric.Control.SuspendLayout();
            try
            {
                foreach (var metric in floatingMetrics)
                {
                    metric.Control.Margin = ScaleSpacing(metric.Margin, pixelScale);
                    metric.Control.Padding = ScaleSpacing(metric.Padding, pixelScale);
                    if (metric.Control is ModeFramePanel frame) frame.FrameThickness = Math.Max(1, (int)Math.Round(2 * pixelScale));
                    if (metric.BaseFont == null) continue;
                    var size = Math.Max(1F, metric.BaseFont.Size * fontScale);
                    if (Math.Abs(metric.Control.Font.Size - size) < 0.01F) continue;
                    var previous = metric.OwnedFont;
                    metric.OwnedFont = new Font(metric.BaseFont.FontFamily, size, metric.BaseFont.Style, metric.BaseFont.Unit);
                    metric.Control.Font = metric.OwnedFont;
                    previous?.Dispose();
                }
            }
            finally
            {
                for (var index = floatingMetrics.Count - 1; index >= 0; index--) floatingMetrics[index].Control.ResumeLayout(true);
                applyingContentScale = false;
            }
        }

        private static Padding ScaleSpacing(Padding value, float scale)
        {
            return new Padding((int)Math.Round(value.Left * scale), (int)Math.Round(value.Top * scale), (int)Math.Round(value.Right * scale), (int)Math.Round(value.Bottom * scale));
        }
        protected override CreateParams CreateParams { get { var value = base.CreateParams; if (!inlineInputActive) value.ExStyle |= WS_EX_NOACTIVATE; return value; } }
        [StructLayout(LayoutKind.Sequential)]
        private struct SizingRectangle { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FloatingWindowPosition
        {
            internal IntPtr Window, InsertAfter;
            internal int X, Y, Width, Height;
            internal uint Flags;
        }

        private Size NormalizeFloatingSize(int width, int height)
        {
            var chromeWidth = Width - ClientSize.Width;
            var chromeHeight = Height - ClientSize.Height;
            var clientWidth = width - chromeWidth;
            var clientHeight = height - chromeHeight;
            // Round only once from the design size. Do not let accessibility resize,
            // restored bounds or SetWindowPos bypass the WM_SIZING zoom constraint.
            var proportional = Math.Abs(clientHeight - clientWidth * (double)DefaultClientSize.Height / DefaultClientSize.Width) <= 1;
            var widthDriven = proportional || Math.Abs(clientWidth - ClientSize.Width) / (double)DefaultClientSize.Width
                >= Math.Abs(clientHeight - ClientSize.Height) / (double)DefaultClientSize.Height;
            var scale = widthDriven ? clientWidth / (double)DefaultClientSize.Width : clientHeight / (double)DefaultClientSize.Height;
            scale = Math.Max(scale, Math.Max((MinimumSize.Width - chromeWidth) / (double)DefaultClientSize.Width,
                (MinimumSize.Height - chromeHeight) / (double)DefaultClientSize.Height));
            return new Size((int)Math.Round(DefaultClientSize.Width * scale) + chromeWidth,
                (int)Math.Round(DefaultClientSize.Height * scale) + chromeHeight);
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (!scalingForDpi && floatingMetrics != null && floatingMetrics.Count > 0 && WindowState == FormWindowState.Normal
                && (specified & BoundsSpecified.Size) != 0)
            {
                var size = NormalizeFloatingSize(width, height);
                width = size.Width; height = size.Height;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }

        protected override void SetClientSizeCore(int width, int height)
        {
            if (!scalingForDpi && floatingMetrics != null && floatingMetrics.Count > 0 && WindowState == FormWindowState.Normal)
            {
                var chromeWidth = Width - ClientSize.Width;
                var chromeHeight = Height - ClientSize.Height;
                var size = NormalizeFloatingSize(width + chromeWidth, height + chromeHeight);
                width = size.Width - chromeWidth; height = size.Height - chromeHeight;
            }
            // WinForms writes its cached client size after SetBoundsCore returns.
            // Normalize here too so that cache cannot restore the unscaled axis.
            base.SetClientSizeCore(width, height);
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            var previous = scalingForDpi;
            scalingForDpi = true;
            try { base.ScaleControl(factor, specified); }
            finally { scalingForDpi = previous; }
        }

        private void KeepWholeWindowScale(int edge, IntPtr rectangle)
        {
            var bounds = Marshal.PtrToStructure<SizingRectangle>(rectangle);
            var chromeWidth = Width - ClientSize.Width;
            var chromeHeight = Height - ClientSize.Height;
            var width = bounds.Right - bounds.Left - chromeWidth;
            var height = bounds.Bottom - bounds.Top - chromeHeight;
            // A resize is a zoom of the entire floating UI, not extra space around it.
            // For corner gestures follow the dominant axis; keep the opposite corner fixed.
            var widthDriven = edge == 1 || edge == 2 || (edge != 3 && edge != 6
                && Math.Abs(width - ClientSize.Width) / (double)DefaultClientSize.Width
                    >= Math.Abs(height - ClientSize.Height) / (double)DefaultClientSize.Height);
            var scale = widthDriven ? width / (double)DefaultClientSize.Width : height / (double)DefaultClientSize.Height;
            var minimumScale = Math.Max((MinimumSize.Width - chromeWidth) / (double)DefaultClientSize.Width,
                (MinimumSize.Height - chromeHeight) / (double)DefaultClientSize.Height);
            scale = Math.Max(minimumScale, scale);
            width = (int)Math.Round(DefaultClientSize.Width * scale) + chromeWidth;
            height = (int)Math.Round(DefaultClientSize.Height * scale) + chromeHeight;
            if (edge == 1 || edge == 4 || edge == 7) bounds.Left = bounds.Right - width;
            else bounds.Right = bounds.Left + width;
            if (edge == 3 || edge == 4 || edge == 5) bounds.Top = bounds.Bottom - height;
            else bounds.Bottom = bounds.Top + height;
            Marshal.StructureToPtr(bounds, rectangle, false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) // WM_DPICHANGED: use WinForms' suggested physical bounds.
            {
                var previous = scalingForDpi;
                scalingForDpi = true;
                try { base.WndProc(ref m); }
                finally { scalingForDpi = previous; }
                if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)ApplyFloatingContentScale);
                return;
            }
            if (!scalingForDpi && m.Msg == 0x0046 && m.LParam != IntPtr.Zero && floatingMetrics.Count > 0
                && WindowState == FormWindowState.Normal) // WM_WINDOWPOSCHANGING, including native/UIA resize
            {
                var position = Marshal.PtrToStructure<FloatingWindowPosition>(m.LParam);
                if ((position.Flags & SWP_NOSIZE) == 0 && position.Width > 0 && position.Height > 0)
                {
                    var size = NormalizeFloatingSize(position.Width, position.Height);
                    position.Width = size.Width; position.Height = size.Height;
                    Marshal.StructureToPtr(position, m.LParam, false);
                }
            }
            if (m.Msg == 0x0214 && m.LParam != IntPtr.Zero && m.WParam.ToInt32() >= 1 && m.WParam.ToInt32() <= 8) // WM_SIZING
            {
                KeepWholeWindowScale(m.WParam.ToInt32(), m.LParam);
                m.Result = new IntPtr(1); return;
            }
            if (m.Msg == WM_NCLBUTTONDOWN)
            {
                var hit = m.WParam.ToInt32();
                if (hit >= HTLEFT && hit <= HTBOTTOMRIGHT)
                {
                    ResizeGestureCount++;
                    LastResizeGestureHit = hit;
                }
            }
            if (m.Msg == WM_MOUSEACTIVATE && !inlineInputActive)
            {
                var foregroundBeforeActivation = GetForegroundWindow();
                if (foregroundBeforeActivation != IntPtr.Zero && foregroundBeforeActivation != Handle) previousForegroundWindow = foregroundBeforeActivation;
                m.Result = new IntPtr(MA_NOACTIVATE); return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (m.Result == new IntPtr(HTCLIENT))
                {
                    var packed = m.LParam.ToInt64();
                    var point = PointToClient(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
                    m.Result = new IntPtr(ResizeHitTest(point));
                }
                return;
            }
            base.WndProc(ref m);
        }

        internal static string ResolveProductionPipeName(string baseDirectory)
        {
            return LegacyDesktopAdapter.ResolveProductionPipeName(baseDirectory);
        }

        private static Button ActionButton(string text, string automationId)
        {
            return new Button { Text = text, Name = automationId, AccessibleName = automationId, Dock = DockStyle.Fill, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(237, 243, 248), MouseDownBackColor = Color.FromArgb(224, 234, 243) }, BackColor = Color.FromArgb(250, 251, 253), ForeColor = Color.FromArgb(43, 57, 70), Font = new Font("微软雅黑", 10.5F, FontStyle.Bold), TabStop = false, Cursor = Cursors.Hand };
        }

        private static Button ModeButton(string text, string automationId)
        {
            return new Button { Text = text, Name = automationId, AccessibleName = automationId, Dock = DockStyle.Fill, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(237, 243, 248), MouseDownBackColor = Color.FromArgb(224, 234, 243) }, BackColor = Color.FromArgb(250, 251, 253), ForeColor = Color.FromArgb(72, 88, 102), Font = new Font("微软雅黑", 9F), TabStop = false, Cursor = Cursors.Hand };
        }

        private static Button WindowButton(string text, string automationId, bool close)
        {
            return new Button { Text = text, Name = automationId, AccessibleName = automationId, Dock = DockStyle.Fill, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0, MouseOverBackColor = close ? Color.FromArgb(245, 221, 224) : Color.FromArgb(232, 238, 244), MouseDownBackColor = close ? Color.FromArgb(237, 202, 207) : Color.FromArgb(219, 228, 237) }, BackColor = Color.FromArgb(246, 248, 250), ForeColor = close ? Color.FromArgb(166, 69, 79) : Color.FromArgb(79, 94, 108), Font = new Font("微软雅黑", close ? 11F : 10F, FontStyle.Bold), TabStop = false, Cursor = Cursors.Hand };
        }

        private static Panel ModeSection(string name, int columns, params Button[] buttons)
        {
            var panel = new ModeFramePanel { Name = name, AccessibleName = name, Dock = DockStyle.Fill, Margin = new Padding(3), Padding = Padding.Empty, FrameColor = Color.FromArgb(184, 196, 207) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.FromArgb(250, 251, 253) };
            for (var column = 0; column < columns; column++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            grid.Controls.Add(buttons[0], 0, 0); grid.SetColumnSpan(buttons[0], columns);
            for (var index = 1; index < buttons.Length; index++) grid.Controls.Add(buttons[index], index - 1, 1);
            panel.SetContent(grid); return panel;
        }

        private async Task SetContextModeAsync(bool enabled)
        {
            if (languageFlow != null) { languageFlow.SaveSettings(languageFlow.Settings with { ContextEnabled = enabled }); return; }
            SaveAuthoritativeCaptureModes(enabled, settings.ContextGranularity, settings.OriginalImageMode);
            ApplyModeVisuals();
            await ExecuteAsync(AutomationCatalog.ContextSet, new SemanticActionArguments { enabled = enabled, granularity = settings.ContextGranularity });
        }

        private async Task SetOriginalImageModeAsync(bool enabled)
        {
            SaveAuthoritativeCaptureModes(settings.ContextModeEnabled, settings.ContextGranularity, enabled);
            ApplyModeVisuals();
            await ExecuteAsync(AutomationCatalog.OriginalImageSet, new SemanticActionArguments { enabled = enabled });
        }

        private void ReloadAuthoritativeCaptureModes()
        {
            var document = workflowSettingsService.LoadOrCreate(settings);
            var profile = document.Profiles[document.ActiveProfileId];
            var capture = profile.Capture ?? new CaptureSettings(false, "sentence", "ocr");
            settings.ContextModeEnabled = capture.ContextEnabled == true;
            settings.ContextGranularity = capture.ContextGranularity == "paragraph" ? "paragraph" : "sentence";
            settings.OriginalImageMode = capture.ImageHandling == "original";
            workflowSettingsRevision = workflowSettingsService.CurrentRevision;
        }

        private void MigrateLegacyCaptureModeAliases()
        {
            try
            {
                var path = UserSettings.PathFor(config);
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (!text.Contains("ContextModeEnabled", StringComparison.Ordinal) && !text.Contains("ContextGranularity", StringComparison.Ordinal)) return;
                settings.Save(config);
            }
            catch (Exception exception) { Log("legacy-context-settings-migration-failed " + exception.GetType().Name); }
        }

        private void SaveAuthoritativeCaptureModes(bool contextEnabled, string granularity, bool originalImage)
        {
            var document = workflowSettingsService.LoadOrCreate(settings);
            var profiles = new Dictionary<string, WorkflowSettingsProfile>(document.Profiles, StringComparer.Ordinal);
            var profile = profiles[document.ActiveProfileId];
            profiles[document.ActiveProfileId] = profile with
            {
                Capture = new CaptureSettings(contextEnabled, granularity == "paragraph" ? "paragraph" : "sentence", originalImage ? "original" : "ocr")
            };
            workflowSettingsService.Save(new WorkflowSettingsDocument(document.SchemaVersion, document.ActiveProfileId, profiles));
            ReloadAuthoritativeCaptureModes();
        }

        private void ApplyModeVisuals()
        {
            var active = Color.FromArgb(112, 177, 220); var inactive = Color.FromArgb(184, 196, 207);
            ((ModeFramePanel)contextSection).FrameColor = settings.ContextModeEnabled ? active : inactive;
            ((ModeFramePanel)originalSection).FrameColor = settings.OriginalImageMode ? active : inactive;
            contextButton.Text = settings.ContextModeEnabled ? "上下文 · 开" : "上下文 · 关";
            contextButton.AccessibleDescription = (settings.ContextModeEnabled ? "已启用" : "未启用") + "；下一操作生效；revision=" + workflowSettingsRevision;
            originalButton.Text = settings.OriginalImageMode ? "原图 · Anki" : "原图模式";
            originalButton.AccessibleDescription = settings.OriginalImageMode ? "已启用；目的端仅 Anki；不检查 RemNote 原图" : "未启用";
            if (contextItem != null) contextItem.Checked = settings.ContextModeEnabled;
            if (originalItem != null) originalItem.Checked = settings.OriginalImageMode;
        }

        private async Task ExecuteAsync(string action, SemanticActionArguments arguments)
        {
            if (busy) return;
            busy = true; SetStatus("处理中…", Color.FromArgb(85, 95, 105), Color.White); await Task.Yield();
            Exception failure = null;
            DesktopSemanticState state = null;
            try
            {
                state = controller.Execute(Action(action, arguments));
                if (state.foregroundState == "context-required")
                {
                    using (var prompt = new ContextDecisionForm())
                    {
                        if (prompt.ShowDialog() == DialogResult.OK) state = controller.Execute(Action(AutomationCatalog.ContextContinueWithout, null));
                        else { SetStatus("等待上下文决定", Color.FromArgb(180, 115, 25), Color.FromArgb(255, 249, 235)); return; }
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { busy = false; }
            if (failure != null) { await PresentErrorAsync(failure.Message); return; }
            if (state != null) await PresentStateAsync(action, state);
        }

        private async Task ExecuteReadToAnkiAsync(ApplicationCommandKind kind, LanguagePracticeChoice? choice = null)
        {
            Log("READ command=" + kind + " phase=entered binding=" + (applicationBinding != null) + " busy=" + busy);
            if (applicationBinding == null || busy) { Log("READ command=" + kind + " phase=rejected reason=" + (applicationBinding == null ? "binding-unavailable" : "ui-busy")); return; }
            busy = true;
            try
            {
                Log("READ command=" + kind + " phase=dispatch");
                await applicationBinding.InvokeAsync(
                    InvocationSource.Pointer,
                    new ApplicationCommand(kind, LanguageChoice: choice),
                    CancellationToken.None);
                Log("READ command=" + kind + " phase=dispatch-returned");
            }
            catch (Exception exception)
            {
                await PresentErrorAsync("read-to-anki-ui-" + exception.GetType().Name);
            }
            finally { busy = false; Log("READ command=" + kind + " phase=finished"); }
        }

        private void RenderFlashcardDraft(FlashcardDraftPreviewState state)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { RenderFlashcardDraft(state); }); return; }
            if (flashcardPreviewForm == null || flashcardPreviewForm.IsDisposed)
            {
                flashcardPreviewForm = new FlashcardPreviewForm(
                    async () => { await flashcardDraftUseCases.ConfirmAsync(CancellationToken.None); },
                    async () => { await flashcardDraftUseCases.CancelAsync(CancellationToken.None); },
                    async text =>
                    {
                        var draft = flashcardPreviewForm?.RenderedDraft;
                        var binding = FlashcardDraftConfirmation.From(draft);
                        var result = await questionUseCases.SubmitPreviewFollowUpAsync(text, binding, CancellationToken.None);
                        if (!result.Accepted) throw new InvalidOperationException(result.NoticeCode ?? "question.follow-up-not-accepted");
                    });
                flashcardPreviewForm.FormClosed += delegate { flashcardPreviewForm = null; };
            }
            flashcardPreviewForm.Render(state);
            if (!flashcardPreviewForm.Visible) flashcardPreviewForm.Show(this);
            flashcardPreviewForm.BringToFront();
        }

        void IDesktopApplicationView.Render(DesktopViewProjection projection)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { RenderApplicationProjection(projection); });
                return;
            }
            RenderApplicationProjection(projection);
        }

        private void RenderApplicationProjection(DesktopViewProjection projection)
        {
            if (projection == null || IsDisposed) return;
            readToAnkiView = projection.ReadToAnki;
            if (readToAnkiView == null || (readToAnkiView.Playback == ReadToAnkiPlaybackStatus.Idle && readToAnkiView.Pipeline == ReadToAnkiPipelineStatus.Idle)) return;
            var diagnosticNotice = readToAnkiView.NoticeCode ?? "none";
            if (!System.Text.RegularExpressions.Regex.IsMatch(diagnosticNotice, @"^[a-zA-Z0-9._:-]{1,160}$")) diagnosticNotice = "redacted-noncode";
            Log("READ phase=projection pipeline=" + readToAnkiView.Pipeline + " playback=" + readToAnkiView.Playback + " notice=" + diagnosticNotice);

            toolTips.SetToolTip(statusButton, readToAnkiView.NoticeCode ?? "");
            statusButton.AccessibleDescription = System.Text.Json.JsonSerializer.Serialize(new
            {
                readToAnkiView.OperationId, Playback = readToAnkiView.Playback.ToString(), Pipeline = readToAnkiView.Pipeline.ToString(),
                readToAnkiView.CanStop, readToAnkiView.CanReplay,
                ActionCommandId = readToAnkiView.PlaybackTarget?.ActionCommandId, AttemptId = readToAnkiView.PlaybackTarget?.AttemptId
            });
            if (readToAnkiView.Playback == ReadToAnkiPlaybackStatus.StopRequested)
            {
                SetStatus("已请求停止 · 制卡流程继续", Color.FromArgb(38, 116, 242), Color.FromArgb(238, 245, 255));
                return;
            }
            if (readToAnkiView.Playback == ReadToAnkiPlaybackStatus.Unknown && readToAnkiView.Pipeline != ReadToAnkiPipelineStatus.UnknownExternalState)
            {
                SetStatus("播放状态未知 · 请勿重复操作", Color.FromArgb(166, 69, 79), Color.FromArgb(255, 244, 245));
                return;
            }
            if (readToAnkiView.Pipeline == ReadToAnkiPipelineStatus.AwaitingCardConfirmation)
            {
                SetStatus(readToAnkiView.CanReplay ? "草稿待确认 · ▶ 重播" : "草稿待确认", Color.FromArgb(38, 116, 242), Color.FromArgb(238, 245, 255));
                return;
            }
            if (readToAnkiView.NeedsLanguageChoice)
            {
                SetStatus("等待练习方式…", Color.FromArgb(180, 115, 25), Color.FromArgb(255, 249, 235));
                if (!string.Equals(readToAnkiChoiceOperationId, readToAnkiView.OperationId, StringComparison.Ordinal))
                {
                    readToAnkiChoiceOperationId = readToAnkiView.OperationId;
                    BeginInvoke((MethodInvoker)async delegate { await PresentReadToAnkiLanguageChoiceAsync(); });
                }
                return;
            }
            readToAnkiChoiceOperationId = null;

            if (readToAnkiView.Pipeline == ReadToAnkiPipelineStatus.UnknownExternalState)
            {
                var interrupted = readToAnkiView.NoticeCode?.StartsWith("read-to-anki-observation-", StringComparison.Ordinal) == true;
                SetStatus(interrupted ? "状态查询中断，任务结果未知" : "写入状态未知，请检查 Anki", Color.FromArgb(166, 69, 79), Color.FromArgb(255, 244, 245));
                NotifyReadToAnkiTerminal(projection.Revision, interrupted ? "任务状态暂不可确认；请先核查，不要重复导出" : "写入状态未知", readToAnkiView.NoticeCode);
                return;
            }
            if (readToAnkiView.Pipeline == ReadToAnkiPipelineStatus.AwaitingCardResolution)
            {
                SetStatus("需要人工处理：" + ShortStatus(readToAnkiView.NoticeCode), Color.FromArgb(180, 115, 25), Color.FromArgb(255, 249, 235));
                if (readToAnkiView.CardResolution != null && !string.Equals(readToAnkiResolutionOperationId, readToAnkiView.OperationId, StringComparison.Ordinal))
                {
                    readToAnkiResolutionOperationId = readToAnkiView.OperationId;
                    BeginInvoke((MethodInvoker)async delegate
                    {
                        using var dialog = new CardResolutionForm(readToAnkiView.OperationId, readToAnkiView.CardResolution);
                        dialog.ShowDialog(this);
                        if (dialog.Selection != null)
                        {
                            SetStatus("正在处理选择…", Color.FromArgb(38, 116, 242), Color.FromArgb(238, 245, 255));
                            await applicationBinding.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.ReadToAnkiResolve) { CardResolution = dialog.Selection }, CancellationToken.None);
                        }
                    });
                }
                return;
            }
            readToAnkiResolutionOperationId = null;
            if (readToAnkiView.Pipeline == ReadToAnkiPipelineStatus.PartiallyFailed)
            {
                var speech = readToAnkiView.Playback == ReadToAnkiPlaybackStatus.Playing ? "朗读中 · " : readToAnkiView.Playback == ReadToAnkiPlaybackStatus.IdleReplayable ? "朗读完成 · " : "";
                SetStatus(speech + "制卡失败：" + (readToAnkiView.NoticeCode ?? "unknown"), Color.FromArgb(166, 69, 79), Color.FromArgb(255, 244, 245));
                NotifyReadToAnkiTerminal(projection.Revision, "制卡失败，可重新选择文本重试", readToAnkiView.NoticeCode);
                return;
            }
            if (readToAnkiView.Pipeline == ReadToAnkiPipelineStatus.Failed)
            {
                var speech = readToAnkiView.Playback == ReadToAnkiPlaybackStatus.Playing ? "朗读中 · " : readToAnkiView.Playback == ReadToAnkiPlaybackStatus.IdleReplayable ? "朗读完成 · " : "";
                SetStatus(speech + "制卡失败：" + (readToAnkiView.NoticeCode ?? "unknown"), Color.FromArgb(166, 69, 79), Color.FromArgb(255, 244, 245));
                NotifyReadToAnkiTerminal(projection.Revision, "制卡失败，可重新选择文本重试", readToAnkiView.NoticeCode);
                return;
            }
            if (readToAnkiView.Playback == ReadToAnkiPlaybackStatus.Failed)
            {
                SetStatus("朗读失败 · 制卡状态：" + ShortStatus(readToAnkiView.NoticeCode), Color.FromArgb(166, 69, 79), Color.FromArgb(255, 244, 245));
                NotifyReadToAnkiTerminal(projection.Revision, "朗读失败", readToAnkiView.NoticeCode);
                return;
            }
            if (readToAnkiView.Pipeline == ReadToAnkiPipelineStatus.Completed)
            {
                if (readToAnkiView.Playback is ReadToAnkiPlaybackStatus.Accepted or ReadToAnkiPlaybackStatus.Playing) SetStatus("朗读中 · ✓ 已写入 Anki", Color.FromArgb(38, 116, 242), Color.FromArgb(238, 245, 255));
                else if (readToAnkiView.CanReplay) SetStatus("✓ 已写入 Anki · ▶ 重播", Color.FromArgb(35, 137, 81), Color.FromArgb(239, 250, 244));
                else if (readToAnkiView.Playback == ReadToAnkiPlaybackStatus.Stopped) SetStatus("朗读已停止 · ✓ 已写入 Anki", Color.FromArgb(35, 137, 81), Color.FromArgb(239, 250, 244));
                else SetStatus("✓ 已写入 Anki", Color.FromArgb(35, 137, 81), Color.FromArgb(239, 250, 244));
                Log("READ phase=rendered-terminal pipeline=Completed playback=" + readToAnkiView.Playback + " busy=" + busy + " operationId=" + readToAnkiView.OperationId);
                return;
            }

            var stage = readToAnkiView.Pipeline switch
            {
                ReadToAnkiPipelineStatus.Reviewing => "正在清洗",
                ReadToAnkiPipelineStatus.CreatingCards => "正在制卡",
                ReadToAnkiPipelineStatus.WritingAnki => "正在写入 Anki",
                _ => "已受理"
            };
            var playback = readToAnkiView.Playback switch
            {
                ReadToAnkiPlaybackStatus.Playing => "朗读中",
                ReadToAnkiPlaybackStatus.IdleReplayable => "▶ 重播",
                ReadToAnkiPlaybackStatus.Stopped => "朗读已停止",
                _ => null
            };
            SetStatus(playback == null ? stage + "…" : playback + " · " + stage, Color.FromArgb(38, 116, 242), Color.FromArgb(238, 245, 255));
        }

        private async Task PresentReadToAnkiLanguageChoiceAsync()
        {
            if (readToAnkiView == null || !readToAnkiView.NeedsLanguageChoice) return;
            using (var form = new LanguagePracticeForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    SetStatus("等待练习方式…", Color.FromArgb(180, 115, 25), Color.FromArgb(255, 249, 235));
                    return;
                }
                var choice = form.Selection switch
                {
                    "meaning-reading" => LanguagePracticeChoice.MeaningReading,
                    "writing" => LanguagePracticeChoice.Writing,
                    "both" => LanguagePracticeChoice.Both,
                    _ => throw new InvalidOperationException("read-to-anki-language-choice-invalid")
                };
                await ExecuteReadToAnkiAsync(ApplicationCommandKind.ReadToAnkiResumeChoice, choice);
            }
        }

        private void NotifyReadToAnkiTerminal(long revision, string title, string notice)
        {
            if (readToAnkiTerminalRevision == revision) return;
            readToAnkiTerminalRevision = revision;
            tray.ShowBalloonTip(3500, title, string.IsNullOrWhiteSpace(notice) ? "请打开状态记录查看详情" : notice, ToolTipIcon.Warning);
        }

        private async Task ExecuteScreenshotAsync(string action)
        {
            if (busy) return;
            busy = true; SetStatus("等待截图…", Color.FromArgb(85, 95, 105), Color.White); await Task.Yield();
            Exception failure = null;
            DesktopSemanticState state = null;
            try
            {
                if (screenshotSession != null) screenshotSession.Hide();
                Hide(); await Task.Delay(120);
                state = controller.Execute(Action(action, null));
                Show(); TopMost = true;
                if (state.screenshotSession.active)
                {
                    EnsureScreenshotSession(); screenshotSession.UpdateState(state); screenshotSession.Show(); screenshotSession.BringToFront();
                }
                else if (screenshotSession != null) screenshotSession.Hide();
            }
            catch (Exception ex) { Show(); failure = ex; }
            finally { if (!Visible) Show(); busy = false; }
            if (failure != null) { await PresentErrorAsync(failure.Message); return; }
            if (state != null) await PresentStateAsync(action, state);
        }

        private void EnsureScreenshotSession()
        {
            if (screenshotSession != null && !screenshotSession.IsDisposed) return;
            screenshotSession = new ScreenshotSessionForm(async delegate(string action)
            {
                if (action == AutomationCatalog.ScreenshotContinue) await ExecuteScreenshotAsync(action);
                else
                {
                    var state = controller.Execute(Action(action, null));
                    screenshotSession.UpdateState(state);
                    if (!state.screenshotSession.active) screenshotSession.Hide();
                    await PresentStateAsync(action, state);
                }
            });
            screenshotSession.StartPosition = FormStartPosition.Manual; screenshotSession.Location = new Point(Location.X, Location.Y + Height + 6);
        }

        private async Task PresentStateAsync(string action, DesktopSemanticState state)
        {
            var notice = state.noticeCode ?? "desktop.unknown-state";
            Log("STATE action=" + action + " foreground=" + state.foregroundState + " notice=" + notice);
            if (notice == "capture.submitted" || notice == "screenshot.submitted" || notice == "question.submitted" || notice == "question.follow-up-submitted")
            {
                await TemporaryStatusAsync("✓", Color.FromArgb(35, 166, 92), Color.White, 1000, action == AutomationCatalog.ReadCapture);
                return;
            }
            if (notice == "screenshot.frame-captured") { SetStatus("截图已加入", Color.FromArgb(38, 116, 242), Color.White); return; }
            if (notice == "screenshot.group-question" || notice == "screenshot.group-answer" || notice == "screenshot.last-frame-undone") { SetStatus(ShortStatus(notice), Color.FromArgb(38, 116, 242), Color.White); return; }
            if (notice == "screenshot.session-cancelled" || notice == "screenshot.frame-cancelled" || notice == "screenshot.cancelled-before-input") { await TemporaryStatusAsync("已取消截图", Color.Gray, Color.White, 1000, false); return; }
            if (notice == "mode.context-updated" || notice == "mode.original-image-updated") { await TemporaryStatusAsync("设置已保存", Color.FromArgb(38, 116, 242), Color.White, 900, false); return; }
            if (action == AutomationCatalog.PlaybackReplay && state.playback != "stopped") { SetReplayStatus(); return; }
            if (action == AutomationCatalog.PlaybackStop && notice == "playback.stop-requested" && state.playback == "stop-requested") { await TemporaryStatusAsync("已请求停止", Color.Gray, Color.White, 900, false); return; }
            if (action == AutomationCatalog.PlaybackStop && notice != "playback.stage-cancel-contract-unavailable" && state.playback == "stopped") { await TemporaryStatusAsync("已停止", Color.Gray, Color.White, 800, false); return; }
            if (action == AutomationCatalog.TaskStatusQueryRecent && IsOcrHumanResolutionReady(state)) { await PresentOcrHumanResolutionAsync(state); return; }
            if (action == AutomationCatalog.TaskStatusQueryRecent && state.recentTask.available && state.recentTask.decisionKind == AutomationCatalogV11.LanguagePracticeDecisionKind && AutomationCatalogV11.IsDecisionTargetToken(state.recentTask.decisionTargetToken)) { await PresentLanguagePracticeAsync(); return; }
            if (action == AutomationCatalog.TaskStatusQueryRecent && state.recentTask.available && state.recentTask.displayState == "succeeded") { await TemporaryStatusAsync("✓", Color.FromArgb(35, 166, 92), Color.White, 1200, false); return; }
            if (action == AutomationCatalog.TaskStatusQueryRecent && state.recentTask.available) { await TemporaryStatusAsync(ShortStatus(state.recentTask.displayState), Color.FromArgb(38, 116, 242), Color.White, 1500, false); return; }
            if (notice.EndsWith("submitted", StringComparison.Ordinal) || notice == "fake.accepted") { await TemporaryStatusAsync("✓", Color.FromArgb(35, 166, 92), Color.White, 1000, action == AutomationCatalog.ReadCapture); return; }
            await PresentErrorAsync(notice);
        }

        private async Task PresentErrorAsync(string message)
        {
            Log("ERROR " + message); tray.ShowBalloonTip(3500, "操作失败", message, ToolTipIcon.Error);
            await TemporaryStatusAsync("! " + ShortStatus(message), Color.FromArgb(218, 68, 83), Color.White, 2200, false);
        }

        private int SetStatus(string text, Color foreground, Color background)
        {
            statusVersion++; statusButton.Text = text ?? ""; statusButton.ForeColor = foreground; statusButton.BackColor = background; statusButton.Cursor = (text ?? "").Contains("重播") ? Cursors.Hand : Cursors.Default; return statusVersion;
        }

        private async Task TemporaryStatusAsync(string text, Color foreground, Color background, int milliseconds, bool replayAfter)
        {
            var version = SetStatus(text, foreground, background); await Task.Delay(milliseconds); if (version != statusVersion) return;
            if (replayAfter) SetReplayStatus(); else SetStatus("", Color.Empty, Color.White);
        }

        private void SetReplayStatus() { SetStatus("▶ 重播", Color.FromArgb(38, 116, 242), Color.FromArgb(238, 245, 255)); }

        private static SemanticActionEnvelope Action(string action, SemanticActionArguments arguments)
        {
            return new SemanticActionEnvelope { schemaVersion = AutomationCatalogV13.IsKnownAction(action) ? AutomationCatalogV13.ActionSchemaVersion : AutomationCatalogV11.ActionSchemaVersion, requestId = "ui-" + Guid.NewGuid().ToString("N"), action = action, arguments = arguments };
        }

        private void OpenSettings(bool legacy = false)
        {
            if (languageFlow != null && !legacy) { OpenLanguageSettings(); return; }
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.Show(); settingsForm.BringToFront(); settingsForm.Activate(); return;
            }
            var dialog = settingsFormFactory(settings.Copy());
            settingsForm = dialog;
            dialog.FormClosed += delegate
            {
                if (ReferenceEquals(settingsForm, dialog)) settingsForm = null;
                if (dialog.DialogResult != DialogResult.OK || dialog.SavedSettings == null) { dialog.Dispose(); return; }
                settings = dialog.SavedSettings;
                ReloadAuthoritativeCaptureModes();
                contextItem.Checked = settings.ContextModeEnabled; originalItem.Checked = settings.OriginalImageMode;
                ApplyModeVisuals();
                controller.Execute(Action(AutomationCatalog.ContextSet, new SemanticActionArguments { enabled = settings.ContextModeEnabled, granularity = settings.ContextGranularity }));
                controller.Execute(Action(AutomationCatalog.OriginalImageSet, new SemanticActionArguments { enabled = settings.OriginalImageMode }));
                SetStatus("设置已保存", Color.FromArgb(38, 116, 242), Color.White);
                dialog.Dispose();
            };
            dialog.Show();
        }

        internal Task OpenLaunchViewAsync(DesktopLaunchView view)
        {
            if (view == DesktopLaunchView.Settings)
            {
                OpenSettings();
                return Task.CompletedTask;
            }
            if (view == DesktopLaunchView.Question) return OpenQuestionAsync();
            return Task.CompletedTask;
        }

        private async Task OpenQuestionAsync()
        {
            var state = controller.Snapshot();
            if (state.questionSession == null || !state.questionSession.open) state = controller.Execute(Action(AutomationCatalog.QuestionOpen, null));
            if (!state.questionSession.open)
            {
                await PresentErrorAsync(state.noticeCode ?? "question.open-failed");
                return;
            }
            await ShowQuestionFormAsync(state, false);
        }

        private Task ShowQuestionFormAsync(DesktopSemanticState state, bool preserveSessionOnClose, IntPtr preferredRestoreForeground = default(IntPtr))
        {
            var restoreForeground = preferredRestoreForeground != IntPtr.Zero ? preferredRestoreForeground : GetForegroundWindow();
            if (restoreForeground == Handle) restoreForeground = IntPtr.Zero;
            using (var form = new QuestionForm(async delegate(string action, SemanticActionArguments arguments)
            {
                var next = controller.Execute(Action(action, arguments));
                await PresentStateAsync(action, next);
                return next;
            }, questionAnswers, delegate { return Task.FromResult(controller.RefreshActiveQuestion()); }))
            {
                form.Update(state);
                var result = form.ShowDialog();
                if (result != DialogResult.OK)
                {
                    if (preserveSessionOnClose) controller.DismissQuestionDialogKeepingSession();
                    else controller.CloseQuestionInteraction();
                    if (preserveSessionOnClose) SetStatus("查看答案", Color.FromArgb(38, 116, 242), Color.FromArgb(238, 245, 255));
                    else SetStatus("", Color.FromArgb(250, 251, 253), Color.FromArgb(43, 57, 70));
                }
            }
            LastQuestionRestoreForeground = restoreForeground;
            LastQuestionRestoreSucceeded = restoreForeground != IntPtr.Zero && IsWindow(restoreForeground) && SetForegroundWindow(restoreForeground);
            return Task.CompletedTask;
        }

        private void BeginInlineQuestionInput()
        {
            if (IsDisposed) return;
            if (inlineInputActive)
            {
                inlineActivationPending = false;
                EnsureInlineQuestionFocus(0);
                return;
            }
            var foregroundBeforeActivation = GetForegroundWindow();
            if (previousForegroundWindow == IntPtr.Zero && foregroundBeforeActivation != IntPtr.Zero && foregroundBeforeActivation != Handle) previousForegroundWindow = foregroundBeforeActivation;
            inlineInputActive = true;
            SetNoActivateStyle(false);
            EnsureInlineQuestionFocus(0);
        }

        private void EnsureInlineQuestionFocus(int attempt)
        {
            if (IsDisposed || !inlineInputActive) { inlineActivationPending = false; return; }
            SetForegroundWindow(Handle); Activate(); inlineQuestionInput.Select(); inlineQuestionInput.Focus();
            inlineQuestionInput.SelectionStart = inlineQuestionInput.TextLength; inlineQuestionInput.SelectionLength = 0;
            if (GetForegroundWindow() == Handle && inlineQuestionInput.Focused)
            {
                inlineActivationPending = false;
                return;
            }
            if (attempt >= 3) { inlineActivationPending = false; RestoreNoActivate(); return; }
            BeginInvoke((MethodInvoker)delegate { EnsureInlineQuestionFocus(attempt + 1); });
        }

        private void RestoreNoActivate()
        {
            if (!inlineInputActive) return;
            inlineInputActive = false;
            if (ActiveControl == inlineQuestionInput) ActiveControl = null;
            SetNoActivateStyle(true);
            var restore = previousForegroundWindow;
            previousForegroundWindow = IntPtr.Zero;
            if (restore != IntPtr.Zero && restore != Handle && IsWindow(restore)) SetForegroundWindow(restore);
        }

        private void SetNoActivateStyle(bool enabled)
        {
            if (!IsHandleCreated || IsDisposed) return;
            var style = GetWindowLong(Handle, GWL_EXSTYLE);
            var next = enabled ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
            if (next == style) return;
            SetWindowLong(Handle, GWL_EXSTYLE, next);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private async Task SubmitInlineQuestionAsync()
        {
            if (inlineSubmitInProgress) return;
            var original = inlineQuestionInput.Text;
            if (string.IsNullOrWhiteSpace(original)) return;
            var restoreAfterQuestion = previousForegroundWindow;
            inlineSubmitInProgress = true;
            inlineQuestionInput.Enabled = false;
            SetStatus("正在提交问题…", Color.FromArgb(38, 116, 242), Color.White);
            if (questionUseCases != null)
            {
                try
                {
                    var result = await questionUseCases.SubmitInitialAsync(original, CancellationToken.None);
                    if (result.Accepted)
                    {
                        inlineQuestionInput.Clear();
                        SetStatus("答案与卡片草稿已就绪，请预览确认", Color.SeaGreen, Color.White);
                    }
                    else
                    {
                        inlineQuestionInput.Text = original;
                        SetStatus("问题未完成 · " + result.NoticeCode, Color.Firebrick, Color.White);
                    }
                }
                catch (Exception exception)
                {
                    inlineQuestionInput.Text = original;
                    SetStatus("问题未完成 · " + exception.GetType().Name, Color.Firebrick, Color.White);
                }
                finally
                {
                    inlineQuestionInput.Enabled = true;
                    inlineSubmitInProgress = false;
                    RestoreNoActivate();
                }
                return;
            }
            DesktopSemanticState submitted = null;
            try
            {
                var opened = controller.Execute(Action(AutomationCatalog.QuestionOpen, null));
                if (!opened.questionSession.open)
                {
                    submitted = opened;
                }
                else
                {
                    submitted = controller.Execute(Action(AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = original }));
                }
            }
            catch (Exception ex)
            {
                controller.CloseQuestionInteraction();
                submitted = DesktopSemanticState.CreateDefault();
                submitted.noticeCode = ex.Message;
            }
            finally
            {
                var accepted = submitted != null && (submitted.noticeCode == "question.submitted" || submitted.noticeCode == "fake.accepted");
                if (accepted) inlineQuestionInput.Clear(); else inlineQuestionInput.Text = original;
                inlineQuestionInput.Enabled = true;
                inlineSubmitInProgress = false;
                RestoreNoActivate();
            }
            if (submitted == null) return;
            if (submitted.noticeCode == "question.submitted" || submitted.noticeCode == "fake.accepted")
            {
                await PresentStateAsync(AutomationCatalog.QuestionSubmit, submitted);
                SetStatus("问题已提交，正在生成答案…", Color.FromArgb(38, 116, 242), Color.White);
                await ShowQuestionFormAsync(submitted, true, restoreAfterQuestion);
            }
            else
            {
                controller.CloseQuestionInteraction();
                await PresentErrorAsync(submitted.noticeCode ?? "question.submit-pending");
            }
        }

        private async Task PresentLanguagePracticeAsync()
        {
            using (var form = new LanguagePracticeForm())
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    await TemporaryStatusAsync("已取消选择", Color.Gray, Color.White, 800, false);
                    return;
                }
                var state = controller.Execute(Action(AutomationCatalogV11.LanguagePracticeSelect, new SemanticActionArguments { selection = form.Selection }));
                if (state.noticeCode == "language-practice.input-submitted") await TemporaryStatusAsync("✓ 已提交练习方式", Color.FromArgb(35, 166, 92), Color.White, 1000, false);
                else await PresentErrorAsync(state.noticeCode ?? "language-practice.input-submit-pending");
            }
        }

        private static bool IsOcrHumanResolutionReady(DesktopSemanticState state)
        {
            return state != null && state.recentTask != null && state.recentTask.available && state.recentTask.displayState == "awaiting-user" && state.recentTask.attentionRequired &&
                state.recentTask.decisionKind == AutomationCatalogV13.OcrDecisionKind && AutomationCatalogV11.IsDecisionTargetToken(state.recentTask.decisionTargetToken) &&
                state.recentTask.ocrHumanResolution != null && state.recentTask.ocrHumanResolution.targetToken == state.recentTask.decisionTargetToken;
        }

        private async Task PresentOcrHumanResolutionAsync(DesktopSemanticState state)
        {
            var projection = state.recentTask.ocrHumanResolution;
            var original = ocrHumanResolution.ReadText(state.recentTask.taskId, projection.originalTextRef, "ocr.original.text");
            var candidate = ocrHumanResolution.ReadText(state.recentTask.taskId, projection.candidateTextRef, "ocr.candidate.text");
            var provenance = ocrHumanResolution.ReadProvenance(state.recentTask.taskId, projection.sourceProofreadRef);
            if (!original.ready || !candidate.ready || !provenance.ready)
            {
                await PresentErrorAsync(!original.ready ? original.noticeCode : !candidate.ready ? candidate.noticeCode : provenance.noticeCode);
                return;
            }
            OcrHumanResolutionResult selection;
            using (var form = new OcrHumanResolutionForm(new OcrHumanResolutionView
            {
                OriginalText = original.text, CandidateText = candidate.text,
                MarkdownText = provenance.markdownText, StructuredText = provenance.structuredText,
                DifferenceText = projection.diffJson, UnresolvedReason = projection.reasonJson
            }))
            {
                if (form.ShowDialog(this) != DialogResult.OK || form.Selection == null) return;
                selection = form.Selection;
            }
            var action = selection.Choice == "use-original" ? AutomationCatalogV13.OcrAcceptOriginal :
                selection.Choice == "use-edited" ? AutomationCatalogV13.OcrSubmitCorrection : AutomationCatalogV13.OcrCancel;
            var arguments = action == AutomationCatalogV13.OcrSubmitCorrection ? new SemanticActionArguments { text = selection.Text, candidateText = candidate.text } : null;
            var submittedToken = projection.targetToken;
            var submitted = controller.Execute(Action(action, arguments));
            if (submitted.noticeCode != "ocr-human-resolution.input-submitted" && submitted.noticeCode != "ocr-human-resolution.cancel-submitted")
            {
                await PresentErrorAsync(submitted.noticeCode ?? "ocr-human-resolution.input-submit-pending");
                return;
            }
            if (action == AutomationCatalogV13.OcrCancel)
            {
                await TemporaryStatusAsync("已取消", Color.Gray, Color.White, 1000, false);
                return;
            }
            SetStatus("处理中…", Color.FromArgb(85, 95, 105), Color.White);
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(700);
                var next = controller.Execute(Action(AutomationCatalog.TaskStatusQueryRecent, null));
                if (IsOcrHumanResolutionReady(next))
                {
                    if (next.recentTask.decisionTargetToken == submittedToken) continue;
                    await PresentOcrHumanResolutionAsync(next);
                    return;
                }
                if (next.recentTask != null && next.recentTask.displayState == "succeeded")
                {
                    await TemporaryStatusAsync("✓", Color.FromArgb(35, 166, 92), Color.White, 1200, false);
                    return;
                }
                if (next.recentTask != null && (next.recentTask.displayState == "failed" || next.recentTask.displayState == "partial" || next.recentTask.displayState == "cancelled"))
                {
                    await PresentErrorAsync("ocr-human-resolution." + next.recentTask.displayState);
                    return;
                }
            }
            SetStatus("仍在处理，可稍后查询", Color.FromArgb(38, 116, 242), Color.White);
        }

        private Point dragStart, formStart; private bool dragging;
        private void DragMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || (ReferenceEquals(sender, statusButton) && (statusButton.Text.Contains("重播") || statusButton.Text == "查看答案"))) return;
            var resizeHit = ResizeHitTest(PointToClient(Cursor.Position));
            if (resizeHit != HTCLIENT)
            {
                ResizeGestureCount++; LastResizeGestureHit = resizeHit;
                dragging = false;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    var cursor = Cursor.Position;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(resizeHit), new IntPtr((cursor.Y << 16) | (cursor.X & 0xffff)));
                });
                return;
            }
            dragStart = Cursor.Position; formStart = Location; dragging = true;
        }
        private void DragMouseMove(object sender, MouseEventArgs e) { if (!dragging) return; var now = Cursor.Position; Location = new Point(formStart.X + now.X - dragStart.X, formStart.Y + now.Y - dragStart.Y); }
        private void DragMouseUp(object sender, MouseEventArgs e) { if (!dragging) return; dragging = false; SavePosition(); }

        internal int ResizeHitTest(Point point)
        {
            var left = point.X >= 0 && point.X < ResizeFrameThickness; var right = point.X < ClientSize.Width && point.X >= ClientSize.Width - ResizeFrameThickness;
            var top = point.Y >= 0 && point.Y < ResizeFrameThickness; var bottom = point.Y < ClientSize.Height && point.Y >= ClientSize.Height - ResizeFrameThickness;
            if (left && top) return HTTOPLEFT;
            if (right && top) return HTTOPRIGHT;
            if (left && bottom) return HTBOTTOMLEFT;
            if (right && bottom) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return HTCLIENT;
        }

        private void HideToTray() { dragging = false; Hide(); }
        private void RestoreFromTray() { if (IsDisposed) return; WindowState = FormWindowState.Normal; EnsureWindowSize(); Location = EnsureVisible(Location); Show(); TopMost = true; BringToFront(); }
        private Size LoadSize() { try { var path = Path.Combine(config.WorkDirectory, "size.txt"); if (File.Exists(path)) { var p = File.ReadAllText(path).Split(','); int width, height; if (p.Length == 2 && int.TryParse(p[0], out width) && int.TryParse(p[1], out height)) return ClampSize(new Size(width, height)); } } catch { } return DefaultClientSize; }
        private Point LoadPosition() { try { var path = Path.Combine(config.WorkDirectory, "position.txt"); if (File.Exists(path)) { var p = File.ReadAllText(path).Split(','); int x, y; if (p.Length == 2 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y)) return EnsureVisible(new Point(x, y)); } } catch { } var area = Screen.PrimaryScreen.WorkingArea; return new Point(area.Right - Width - 20, area.Top + area.Height / 3); }
        private Size ClampSize(Size value)
        {
            var bounds = SystemInformation.VirtualScreen;
            // Legacy size.txt may contain only a widened/tall frame. Convert it to
            // one zoom before controls or the HWND exist; do not clamp axes apart.
            var scale = Math.Max(1, Math.Max(value.Width / (double)DefaultClientSize.Width,
                value.Height / (double)DefaultClientSize.Height));
            var maximum = Math.Max(1, Math.Min(bounds.Width * 96.0 / DeviceDpi / DefaultClientSize.Width,
                bounds.Height * 96.0 / DeviceDpi / DefaultClientSize.Height));
            scale = Math.Min(scale, maximum);
            return new Size((int)Math.Round(DefaultClientSize.Width * scale), (int)Math.Round(DefaultClientSize.Height * scale));
        }
        private Size LogicalFloatingSize(Size pixels) => new Size((int)Math.Round(pixels.Width * 96.0 / DeviceDpi), (int)Math.Round(pixels.Height * 96.0 / DeviceDpi));
        private Size PhysicalFloatingSize(Size logical) => new Size((int)Math.Round(logical.Width * DeviceDpi / 96.0), (int)Math.Round(logical.Height * DeviceDpi / 96.0));
        private void EnsureWindowSize() { var safe = PhysicalFloatingSize(ClampSize(LogicalFloatingSize(ClientSize))); if (ClientSize != safe) ClientSize = safe; }
        internal void RestoreDefaultSize() { ClientSize = PhysicalFloatingSize(DefaultClientSize); SaveWindowBounds(); SetStatus("已恢复默认大小", Color.FromArgb(38, 116, 242), Color.White); }
        private Point EnsureVisible(Point point)
        {
            var area = Screen.FromPoint(point).WorkingArea;
            return new Point(Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
        private void SavePosition() { try { Directory.CreateDirectory(config.WorkDirectory); File.WriteAllText(Path.Combine(config.WorkDirectory, "position.txt"), Location.X + "," + Location.Y, new UTF8Encoding(false)); } catch { } }
        private void SaveWindowBounds() { try { EnsureWindowSize(); Directory.CreateDirectory(config.WorkDirectory); var logical = LogicalFloatingSize(ClientSize); File.WriteAllText(Path.Combine(config.WorkDirectory, "size.txt"), logical.Width + "," + logical.Height, new UTF8Encoding(false)); SavePosition(); } catch { } }
        private string LogPath { get { var root = Path.Combine(config.WorkDirectory, "logs"); Directory.CreateDirectory(root); return Path.Combine(root, "desktop-ui.log"); } }
        private void Log(string value) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("o") + " " + value + Environment.NewLine, new UTF8Encoding(false)); } catch { } }
        private static string ShortStatus(string value) { value = (value ?? "失败").Replace("\r", " ").Replace("\n", " ").Trim(); return value.Length > 24 ? value.Substring(0, 24) + "…" : value; }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    }

    internal sealed class ContextDecisionForm : Form
    {
        internal ContextDecisionForm()
        {
            Text = "缺少上下文"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; TopMost = true; ClientSize = new Size(360, 112); Font = new Font("微软雅黑", 9F);
            var label = new Label { Text = "当前应用未提供可靠上下文。可以仅使用选区/剪贴板继续。", Dock = DockStyle.Top, Height = 58, Padding = new Padding(12, 12, 12, 0) };
            var proceed = new Button { Text = "不带上下文继续", Name = AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextContinueWithout], AccessibleName = AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextContinueWithout], Width = 140, Height = 32, DialogResult = DialogResult.OK };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) }; buttons.Controls.Add(proceed); Controls.Add(label); Controls.Add(buttons); AcceptButton = proceed;
        }
    }

    internal sealed class ScreenshotSessionForm : Form
    {
        private readonly Label counts; private readonly Button question, answer, next, finish, undo, cancel;
        private bool ownerClosing;
        internal ScreenshotSessionForm(Func<string, Task> execute)
        {
            Text = "多次截图"; FormBorderStyle = FormBorderStyle.FixedToolWindow; TopMost = true; ShowInTaskbar = false; ClientSize = new Size(552, 82); Font = new Font("微软雅黑", 9F);
            counts = new Label { Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), WrapContents = false };
            question = Make("题图", AutomationCatalog.ScreenshotGroupQuestion); answer = Make("答案图", AutomationCatalog.ScreenshotGroupAnswer); next = Make("继续", AutomationCatalog.ScreenshotContinue); finish = Make("完成", AutomationCatalog.ScreenshotFinish); undo = Make("撤销上一张", AutomationCatalog.ScreenshotUndoLast, 92); cancel = Make("退出截图", AutomationCatalog.ScreenshotCancel, 84);
            foreach (var button in new[] { question, answer, next, finish, undo, cancel }) { var action = (string)button.Tag; button.Click += async delegate { await execute(action); }; row.Controls.Add(button); }
            Controls.Add(row); Controls.Add(counts);
            FormClosing += async delegate(object sender, FormClosingEventArgs e)
            {
                if (ownerClosing || e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing) return;
                e.Cancel = true; Hide(); await execute(AutomationCatalog.ScreenshotCancel);
            };
        }
        internal void CloseFromOwner() { ownerClosing = true; Close(); Dispose(); }
        private static Button Make(string label, string action, int width = 78) { var id = AutomationCatalogV11.AutomationIds[action]; return new Button { Text = label, Tag = action, Name = id, AccessibleName = id, Width = width, Height = 30, FlatStyle = FlatStyle.Flat }; }
        internal void UpdateState(DesktopSemanticState state)
        {
            counts.Text = "题图 " + state.screenshotSession.questionImageCount + "　答案图 " + state.screenshotSession.answerImageCount;
            question.BackColor = state.screenshotSession.activeGroup == "question" ? Color.FromArgb(220, 235, 255) : SystemColors.Control;
            answer.BackColor = state.screenshotSession.activeGroup == "answer" ? Color.FromArgb(220, 235, 255) : SystemColors.Control;
            next.Enabled = state.screenshotSession.canContinue; finish.Enabled = state.screenshotSession.canFinish; undo.Enabled = state.screenshotSession.canUndo; cancel.Enabled = state.screenshotSession.canCancel;
        }
    }

    internal sealed class QuestionForm : Form
    {
        private readonly TextBox input, answer, cardRef; private readonly Button submit, followUp, cards, refresh, cancel; private readonly Label validation, answerStatus;
        private readonly IQuestionAnswerArtifactReader answerReader;
        private readonly Func<string, SemanticActionArguments, Task<DesktopSemanticState>> execute;
        private readonly Func<Task<DesktopSemanticState>> refreshActiveQuestion;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private bool answerAvailable;
        private bool submissionInProgress;
        private bool refreshBusy;
        private int refreshAttempts;
        private int refreshEpoch;
        private bool userEditing;
        internal event Action<string> FocusTimeline;
        internal QuestionForm(Func<string, SemanticActionArguments, Task<DesktopSemanticState>> execute, IQuestionAnswerArtifactReader answerReader = null, Func<Task<DesktopSemanticState>> refreshActiveQuestion = null)
        {
            if (execute == null) throw new ArgumentNullException("execute");
            this.execute = execute; this.answerReader = answerReader; this.refreshActiveQuestion = refreshActiveQuestion ?? delegate { return Task.FromResult(DesktopSemanticState.CreateDefault()); };
            Text = "提问、追问与制卡"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(640, 500); MinimumSize = new Size(600, 460); Font = new Font("微软雅黑", 9F); TopMost = true; MinimizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            var inputLabel = new Label { Text = "问题", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(65, 76, 92) };
            input = new TextBox { Multiline = true, Dock = DockStyle.Fill, Name = AutomationCatalogV11.QuestionInputAutomationId, AccessibleName = AutomationCatalogV11.QuestionInputAutomationId, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle };
            validation = new Label { Text = "请输入问题后提交", Name = AutomationCatalogV11.QuestionValidationAutomationId, AccessibleName = AutomationCatalogV11.QuestionValidationAutomationId, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(166, 69, 79), TextAlign = ContentAlignment.MiddleLeft };
            answerStatus = new Label { Text = "等待提交问题", Name = AutomationCatalogV11.QuestionAnswerStatusAutomationId, AccessibleName = AutomationCatalogV11.QuestionAnswerStatusAutomationId, AccessibleDescription = "waiting_input", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(65, 76, 92) };
            answer = new TextBox { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, Name = AutomationCatalogV11.QuestionAnswerAutomationId, AccessibleName = AutomationCatalogV11.QuestionAnswerAutomationId, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(248, 250, 252) };
            var cardLabel = new Label { Text = "制卡结构引用（答案通过校验后可用）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(96, 106, 119) };
            cardRef = new TextBox { Dock = DockStyle.Fill, Name = AutomationCatalogV11.QuestionCardStructureAutomationId, AccessibleName = AutomationCatalogV11.QuestionCardStructureAutomationId, BorderStyle = BorderStyle.FixedSingle };
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            submit = Make("提问", AutomationCatalog.QuestionSubmit); followUp = Make("追问", AutomationCatalog.QuestionFollowUp); cards = Make("制卡", AutomationCatalog.QuestionMakeCards);
            refresh = new Button { Text = "刷新", Name = "knowledge-clipper.desktop.question.active-session.refresh", AccessibleName = "knowledge-clipper.desktop.question.active-session.refresh", Width = 88, Height = 32 };
            cancel = new Button { Text = "取消", Name = AutomationCatalogV11.QuestionCancelAutomationId, AccessibleName = AutomationCatalogV11.QuestionCancelAutomationId, Width = 82, Height = 32, DialogResult = DialogResult.Cancel };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            submit.Enabled = false; followUp.Enabled = false; cards.Enabled = false;
            submit.Click += async delegate { await SubmitTextAsync(execute, answerAvailable); };
            followUp.Click += async delegate { await SubmitTextAsync(execute, true); };
            refresh.Click += async delegate { Update(await this.refreshActiveQuestion()); };
            cards.Click += async delegate { var state = await execute(AutomationCatalog.QuestionMakeCards, new SemanticActionArguments { cardStructureRef = cardRef.Text }); Update(state); if (state != null && !state.questionSession.open) { DialogResult = DialogResult.OK; Close(); } };
            input.Enter += delegate { userEditing = true; refreshEpoch++; TraceFocus("input.enter"); };
            input.Leave += delegate { userEditing = false; TraceFocus("input.leave"); };
            input.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Escape) return;
                e.Handled = true; e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close();
            };
            input.TextChanged += delegate { if (input.Focused) userEditing = true; RefreshButtons(); };
            cardRef.TextChanged += delegate { cards.Enabled = answerAvailable; };
            row.Controls.Add(submit); row.Controls.Add(followUp); row.Controls.Add(refresh); row.Controls.Add(cards); row.Controls.Add(cancel);
            layout.Controls.Add(inputLabel, 0, 0); layout.Controls.Add(input, 0, 1); layout.Controls.Add(validation, 0, 2); layout.Controls.Add(answerStatus, 0, 3); layout.Controls.Add(answer, 0, 4); layout.Controls.Add(cardLabel, 0, 5); layout.Controls.Add(cardRef, 0, 6); layout.Controls.Add(row, 0, 7); Controls.Add(layout);
            refreshTimer = new System.Windows.Forms.Timer { Interval = 700 };
            refreshTimer.Tick += async delegate
            {
                if (userEditing || refreshBusy || refreshAttempts >= 240) { if (refreshAttempts >= 240) StopAutoRefresh(); return; }
                var epoch = refreshEpoch;
                refreshBusy = true; refreshAttempts++;
                TraceFocus("refresh.begin." + epoch);
                try
                {
                    var state = await this.refreshActiveQuestion();
                    if (!IsDisposed && epoch == refreshEpoch && !userEditing) Update(state);
                    else TraceFocus("refresh.discard." + epoch);
                }
                catch (Exception ex) { StopAutoRefresh(); SetAnswerStatus("failed", "刷新答案失败：" + ex.Message, Color.FromArgb(166, 69, 79)); RefreshButtons(); }
                finally { refreshBusy = false; }
            };
            RefreshButtons(); CancelButton = cancel;
            Activated += delegate { TraceFocus("form.activated"); };
            Deactivate += delegate { TraceFocus("form.deactivated"); };
            Shown += delegate { BeginInvoke((MethodInvoker)delegate { Activate(); input.Select(); input.Focus(); TraceFocus("form.shown.focused"); }); };
            FormClosed += delegate { refreshTimer.Stop(); refreshTimer.Dispose(); };
        }
        private async Task SubmitTextAsync(Func<string, SemanticActionArguments, Task<DesktopSemanticState>> execute, bool followUpAction)
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { validation.Text = "问题不能为空"; RefreshButtons(); return; }
            if (submissionInProgress) { validation.Text = "当前问题仍在处理中"; RefreshButtons(); return; }
            userEditing = false; refreshEpoch++;
            submissionInProgress = true; answerAvailable = false; answer.Clear(); SetAnswerStatus("loading", "正在生成答案…", Color.FromArgb(38, 116, 242)); RefreshButtons();
            var state = await execute(followUpAction ? AutomationCatalog.QuestionFollowUp : AutomationCatalog.QuestionSubmit, new SemanticActionArguments { text = input.Text });
            Update(state);
            if (state != null && (state.noticeCode == "question.submitted" || state.noticeCode == "question.follow-up-submitted" || state.noticeCode == "fake.accepted")) input.Clear();
        }
        internal void Update(DesktopSemanticState state)
        {
            answerAvailable = false;
            answer.Clear();
            if (state == null || state.questionSession == null)
            {
                submissionInProgress = false; SetAnswerStatus("failed", "读取状态失败", Color.FromArgb(166, 69, 79)); RefreshButtons(); return;
            }
            if (state.questionSession.answerArtifact != null)
            {
                var result = answerReader == null ? null : answerReader.Read(state.recentTask == null ? null : state.recentTask.taskId, state.questionSession.answerArtifact);
                if (result != null && result.ready)
                {
                    answer.Text = result.text ?? ""; answerAvailable = true; submissionInProgress = false;
                    StopAutoRefresh(); SetAnswerStatus("ready", "答案已更新（已校验）", Color.FromArgb(35, 137, 81)); RefreshButtons(); return;
                }
                submissionInProgress = false; StopAutoRefresh(); SetAnswerStatus("failed", "答案读取失败：" + (result == null ? "question.answer.reader-unavailable" : result.noticeCode), Color.FromArgb(166, 69, 79)); RefreshButtons(); return;
            }
            var answerState = state.questionSession.answerState;
            if (answerState == "loading" || state.foregroundState == "question-awaiting-answer") { submissionInProgress = true; SetAnswerStatus("loading", "正在生成答案…", Color.FromArgb(38, 116, 242)); StartAutoRefresh(); }
            else if (answerState == "failed" || (state.recentTask != null && state.recentTask.displayState == "failed")) { submissionInProgress = false; StopAutoRefresh(); SetAnswerStatus("failed", "回答失败，请检查任务状态后重试", Color.FromArgb(166, 69, 79)); }
            else if (state.questionSession.answerAvailable) { submissionInProgress = false; SetAnswerStatus("waiting_input", "答案已生成，等待可验证的答案引用", Color.FromArgb(137, 96, 18)); StartAutoRefresh(); }
            else { submissionInProgress = false; StopAutoRefresh(); SetAnswerStatus("waiting_input", "等待提交问题", Color.FromArgb(65, 76, 92)); }
            RefreshButtons();
        }
        private void StartAutoRefresh() { if (!refreshTimer.Enabled) { refreshAttempts = 0; refreshTimer.Start(); } }
        private void StopAutoRefresh() { refreshEpoch++; refreshTimer.Stop(); }
        private void TraceFocus(string transition)
        {
            FocusTimeline?.Invoke(transition + " utc=" + DateTimeOffset.UtcNow.ToString("O") + " form=0x" + Handle.ToInt64().ToString("X") + " input=0x" + input.Handle.ToInt64().ToString("X") + " focused=" + input.Focused + " containsFocus=" + ContainsFocus + " instance=" + GetHashCode().ToString("X"));
        }
        private void SetAnswerStatus(string state, string text, Color color) { answerStatus.AccessibleDescription = state; answerStatus.Text = text; answerStatus.ForeColor = color; }
        private void RefreshButtons()
        {
            var hasText = !string.IsNullOrWhiteSpace(input.Text);
            submit.Enabled = hasText && !answerAvailable && !submissionInProgress;
            followUp.Enabled = answerAvailable && hasText && !submissionInProgress;
            cards.Enabled = answerAvailable && !submissionInProgress;
            AcceptButton = answerAvailable ? (IButtonControl)followUp : submit;
            validation.Text = submissionInProgress ? "当前问题仍在处理中" : hasText ? "" : "请输入问题后提交";
        }
        private static Button Make(string label, string action) { var id = AutomationCatalogV11.AutomationIds[action]; return new Button { Text = label, Name = id, AccessibleName = id, Width = 88, Height = 32 }; }
    }

    internal sealed class LanguagePracticeForm : Form
    {
        internal string Selection { get; private set; }

        internal LanguagePracticeForm()
        {
            Text = "选择语言练习方式"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(430, 170); Font = new Font("微软雅黑", 9F); TopMost = true; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var label = new Label { Text = "当前任务需要你选择练习方式：", Dock = DockStyle.Top, Height = 48, Padding = new Padding(14, 16, 0, 0) };
            var choices = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 8, 10, 8), WrapContents = false };
            choices.Controls.Add(Choice("练习含义与读音", AutomationCatalogV11.LanguagePracticeMeaningReading, AutomationCatalogV11.LanguagePracticeMeaningReadingAutomationId, 126));
            choices.Controls.Add(Choice("练习书写", AutomationCatalogV11.LanguagePracticeWriting, AutomationCatalogV11.LanguagePracticeWritingAutomationId, 105));
            choices.Controls.Add(Choice("双向提取练习", AutomationCatalogV11.LanguagePracticeBoth, AutomationCatalogV11.LanguagePracticeBothAutomationId, 126));
            var cancel = new Button { Text = "取消", Name = AutomationCatalogV11.LanguagePracticeCancelAutomationId, AccessibleName = AutomationCatalogV11.LanguagePracticeCancelAutomationId, Width = 82, Height = 30, DialogResult = DialogResult.Cancel };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8) }; bottom.Controls.Add(cancel);
            Controls.Add(bottom); Controls.Add(choices); Controls.Add(label); CancelButton = cancel;
        }

        private Button Choice(string label, string selection, string automationId, int width)
        {
            var button = new Button { Text = label, Name = automationId, AccessibleName = automationId, AccessibleDescription = selection, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(246, 249, 252) };
            button.Click += delegate { Selection = selection; DialogResult = DialogResult.OK; Close(); };
            return button;
        }
    }

    internal sealed class OcrHumanResolutionView
    {
        internal string OriginalText { get; set; }
        internal string CandidateText { get; set; }
        internal string MarkdownText { get; set; }
        internal string StructuredText { get; set; }
        internal string DifferenceText { get; set; }
        internal string UnresolvedReason { get; set; }
    }

    internal sealed class OcrHumanResolutionResult
    {
        internal string Choice { get; set; }
        internal string Text { get; set; }
    }

    internal sealed class OcrHumanResolutionForm : Form
    {
        private readonly OcrHumanResolutionView view;
        private readonly TextBox edit;
        private readonly Label validation;
        internal OcrHumanResolutionResult Selection { get; private set; }

        internal OcrHumanResolutionForm(OcrHumanResolutionView value)
        {
            view = value ?? throw new ArgumentNullException("value");
            Text = "OCR 内容需要确认";
            Name = AccessibleName = OcrHumanResolutionAutomationCatalog.Window;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(840, 720);
            MinimumSize = new Size(740, 640);
            Font = new Font("微软雅黑", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));

            var original = ReadOnlyText(view.OriginalText, OcrHumanResolutionAutomationCatalog.OriginalText);
            var candidate = ReadOnlyText(view.CandidateText, OcrHumanResolutionAutomationCatalog.CandidateText);
            var markdown = ReadOnlyText(view.MarkdownText, OcrHumanResolutionAutomationCatalog.Markdown);
            var structured = ReadOnlyText(view.StructuredText, OcrHumanResolutionAutomationCatalog.Structured);
            var difference = ReadOnlyText(view.DifferenceText, OcrHumanResolutionAutomationCatalog.Difference);
            var evidence = new TabControl { Dock = DockStyle.Fill };
            evidence.TabPages.Add(Tab("OCR 原文", original)); evidence.TabPages.Add(Tab("AI 候选", candidate)); evidence.TabPages.Add(Tab("Markdown", markdown)); evidence.TabPages.Add(Tab("结构化结果", structured)); evidence.TabPages.Add(Tab("候选差异", difference));
            var reason = new Label
            {
                Text = "未决原因：" + (string.IsNullOrWhiteSpace(view.UnresolvedReason) ? "未提供" : view.UnresolvedReason),
                Name = OcrHumanResolutionAutomationCatalog.Reason,
                AccessibleName = OcrHumanResolutionAutomationCatalog.Reason,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(156, 94, 22),
                Padding = new Padding(8, 8, 8, 0),
                BackColor = Color.FromArgb(255, 248, 232)
            };
            edit = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(view.CandidateText) ? view.OriginalText ?? "" : view.CandidateText,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Name = OcrHumanResolutionAutomationCatalog.EditText,
                AccessibleName = OcrHumanResolutionAutomationCatalog.EditText,
                BorderStyle = BorderStyle.FixedSingle
            };
            validation = new Label { Dock = DockStyle.Fill, Name = OcrHumanResolutionAutomationCatalog.Validation, AccessibleName = OcrHumanResolutionAutomationCatalog.Validation, ForeColor = Color.FromArgb(166, 69, 79), TextAlign = ContentAlignment.MiddleLeft };
            var useOriginal = Button("使用原文继续", OcrHumanResolutionAutomationCatalog.UseOriginal, 118);
            var useEdited = Button("编辑后继续", OcrHumanResolutionAutomationCatalog.UseEdited, 106);
            var cancel = Button("取消", OcrHumanResolutionAutomationCatalog.Cancel, 82);
            useOriginal.Click += delegate { Selection = new OcrHumanResolutionResult { Choice = "use-original", Text = view.OriginalText ?? "" }; DialogResult = DialogResult.OK; Close(); };
            useEdited.Click += delegate { SubmitEdited(); };
            cancel.Click += delegate { Selection = new OcrHumanResolutionResult { Choice = "cancel", Text = null }; DialogResult = DialogResult.OK; Close(); };
            edit.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitEdited(); } };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(useEdited); buttons.Controls.Add(useOriginal);

            layout.Controls.Add(Label("已校验的 OCR 证据（只读）"), 0, 0); layout.Controls.Add(evidence, 0, 1);
            layout.Controls.Add(reason, 0, 2);
            layout.Controls.Add(Label("用户修正确认文本（Ctrl+Enter 提交）"), 0, 3); layout.Controls.Add(edit, 0, 4);
            layout.Controls.Add(validation, 0, 5); layout.Controls.Add(buttons, 0, 6);
            Controls.Add(layout);
            CancelButton = cancel;
            Shown += delegate { edit.Focus(); edit.SelectionStart = edit.TextLength; };
        }

        private void SubmitEdited()
        {
            if (string.IsNullOrWhiteSpace(edit.Text)) { validation.Text = "确认文本不能为空"; edit.Focus(); return; }
            Selection = new OcrHumanResolutionResult { Choice = "use-edited", Text = edit.Text };
            DialogResult = DialogResult.OK;
            Close();
        }

        private static Label Label(string text) { return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(65, 76, 92) }; }
        private static TabPage Tab(string text, Control control) { var page = new TabPage(text) { Padding = new Padding(6) }; page.Controls.Add(control); return page; }
        private static TextBox ReadOnlyText(string text, string automationId) { return new TextBox { Text = text ?? "", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Name = automationId, AccessibleName = automationId, BackColor = Color.FromArgb(248, 250, 252), BorderStyle = BorderStyle.FixedSingle }; }
        private static Button Button(string text, string automationId, int width) { return new Button { Text = text, Name = automationId, AccessibleName = automationId, Width = width, Height = 32, FlatStyle = FlatStyle.Flat, Margin = new Padding(6, 0, 0, 0) }; }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Core;
using KnowledgeClipper.Desktop.Tests;

namespace KnowledgeClipper
{
    internal static class WindowsUiCanary
    {
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            var root = Path.Combine(Path.GetTempPath(), "rqc-windows-ui-canary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args != null && args.Any(value => string.Equals(value, "--settings-only", StringComparison.Ordinal)))
                {
                    StartupDiagnosticCanary(root);
                    SettingsAndDialogAutomationCanary(root);
                    Console.WriteLine("WINDOWS_SETTINGS_UI_CANARY PASS assertions=" + assertions);
                    return 0;
                }
                StartupDiagnosticCanary(root);
                ProductionLaunchViewCanary(root);
                CaptureFallbackCanary(root);
                FloatingUiCanary(root);
                NamedPipeUiCanary(root);
                NamedPipeInlineQuestionCanary(root);
                SettingsAndDialogAutomationCanary(root);
                SystemScreenshotAndWindowsBoundaryCanary(root);
                Console.WriteLine("WINDOWS_UI_CANARY PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("WINDOWS_UI_CANARY FAIL " + ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void StartupDiagnosticCanary(string root)
        {
            var package = Path.Combine(root, "startup-package"); Directory.CreateDirectory(package);
            var work = Path.Combine(root, "startup-must-not-create");
            foreach (var name in new[] { "KnowledgeClipper.exe", "KnowledgeClipper.dll", "KnowledgeClipper.deps.json", "KnowledgeClipper.runtimeconfig.json", "coreclr.dll" })
                File.WriteAllBytes(Path.Combine(package, name), new byte[] { 0 });
            File.WriteAllText(Path.Combine(package, "appsettings.json"), "{\"WorkDirectory\":\"" + work.Replace("\\", "\\\\") + "\"}");
            WriteBuildReceipt(package);
            var report = new StringWriter();
            Assert(StartupDiagnostics.Run(package, report) == 0, "startup diagnostic accepts a complete x64 self-contained package fixture");
            Assert(!Directory.Exists(work), "startup diagnostic parses config without creating the production work directory");
            Assert(report.ToString().Contains("\"result\": \"PASS\"") && report.ToString().Contains("\"externalConnections\": 0"), "startup diagnostic reports PASS with zero external connections");
            using (var reportDocument = JsonDocument.Parse(report.ToString()))
                Assert(reportDocument.RootElement.GetProperty("productName").GetString() == "零碎知识剪藏" && reportDocument.RootElement.GetProperty("entrypoint").GetString() == "KnowledgeClipper.exe", "startup diagnostic exposes the Chinese product name and canonical ASCII entrypoint");
            Assert(report.ToString().Contains("\"singleInstanceAlreadyRunning\":"), "startup diagnostic reports the single-instance launch boundary");
            Assert(report.ToString().Contains("\"packageFilesVerified\": true") && report.ToString().Contains("\"packageTreeVerified\": true"), "startup diagnostic verifies package files and the receipt tree");
            var retainedJobs = Path.Combine(root, "retention-fail-safe", "jobs"); Directory.CreateDirectory(retainedJobs);
            var retainedEvidence = Path.Combine(retainedJobs, "waiting-input-evidence.txt"); File.WriteAllText(retainedEvidence, "must remain");
            Directory.SetCreationTimeUtc(retainedJobs, DateTime.UtcNow.AddDays(-120));
            ProductionStartupPolicy.Apply(new AppConfig { WorkDirectory = Path.GetDirectoryName(retainedJobs) });
            Assert(ProductionStartupPolicy.RetentionStatus == "disabled-until-state-aware-retention-service" && File.Exists(retainedEvidence), "production startup keeps legacy time-based retention disabled and preserves unknown-state evidence");
            var pipePackage = Path.Combine(root, "candidate-pipe-package"); Directory.CreateDirectory(pipePackage);
            File.WriteAllText(Path.Combine(pipePackage, "candidate-integration.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = "knowledge-clipper-candidate-integration.v1", AutoStart = true,
                OrchestratorExecutable = "authority/orchestrator.exe", IpcConfig = "authority/ipc.json", Registry = "authority/registry.json", ReleaseRoot = "authority",
                JobsRoot = "%LOCALAPPDATA%\\KnowledgeClipper\\jobs", PipeName = "KnowledgeClipper.Canary.CandidatePipe", MaxActiveTasks = 4
            }));
            Assert(BoundClipperForm.ResolveProductionPipeName(pipePackage) == "KnowledgeClipper.Canary.CandidatePipe", "production Desktop client resolves the exact candidate pipe instead of the legacy default alias");
            Assert(ModuleActionProtocolCapabilities.Parse("1.4.0") == 14 && ModuleActionProtocolCapabilities.Parse("1.5.0") == 15 && ModuleActionProtocolCapabilities.Parse("1.6.0") == 16 && ModuleActionProtocolCapabilities.Parse("1.7.0") == 17 && ModuleActionProtocolCapabilities.Parse("1.8.0") == 0 && ModuleActionProtocolCapabilities.Parse(null) == 0, "all Desktop module-action capability gates share one 1.4 through 1.7 parser and fail closed on unknown versions");
            File.WriteAllBytes(Path.Combine(package, "KnowledgeClipper.dll"), new byte[] { 1 });
            var corrupted = new StringWriter();
            Assert(StartupDiagnostics.Run(package, corrupted) == 4 && corrupted.ToString().Contains("hash-mismatch:KnowledgeClipper.dll"), "startup diagnostic fails closed on a corrupted package file");

            var previousRemNoteOverride = Environment.GetEnvironmentVariable(LoopbackProviderOverrides.RemNoteMcpUrlEnvironmentVariable);
            var previousAnkiOverride = Environment.GetEnvironmentVariable(LoopbackProviderOverrides.AnkiEndpointEnvironmentVariable);
            try
            {
                var overrideRoot = Path.Combine(root, "provider-overrides"); Directory.CreateDirectory(overrideRoot);
                var overrideConfig = Path.Combine(overrideRoot, "appsettings.json");
                File.WriteAllText(overrideConfig, JsonSerializer.Serialize(new { WorkDirectory = Path.Combine(overrideRoot, "work"), RemnoteMcpUrl = "http://127.0.0.1:43101/mcp" }));
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.RemNoteMcpUrlEnvironmentVariable, null);
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.AnkiEndpointEnvironmentVariable, null);
                Assert(AppConfig.Read(overrideConfig).RemnoteMcpUrl == "http://127.0.0.1:43101/mcp" && new DesktopModuleSettingsStore(overrideRoot).Load().AnkiEndpoint == "http://127.0.0.1:8765",
                    "provider override defaults preserve the packaged RemNote and Anki loopback endpoints exactly");

                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.RemNoteMcpUrlEnvironmentVariable, "http://127.0.0.1:18765/mcp");
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.AnkiEndpointEnvironmentVariable, "http://127.0.0.1:18766");
                var overriddenConfig = AppConfig.Read(overrideConfig);
                var overriddenModules = new DesktopModuleSettingsStore(overrideRoot).Load();
                var child = new ProcessStartInfo(); LoopbackProviderOverrides.ApplyTo(child);
                Assert(overriddenConfig.RemnoteMcpUrl == "http://127.0.0.1:18765/mcp" && overriddenModules.AnkiEndpoint == "http://127.0.0.1:18766" &&
                    child.EnvironmentVariables[LoopbackProviderOverrides.RemNoteMcpUrlEnvironmentVariable] == "http://127.0.0.1:18765/mcp" && child.EnvironmentVariables[LoopbackProviderOverrides.AnkiEndpointEnvironmentVariable] == "http://127.0.0.1:18766" &&
                    !File.Exists(new DesktopModuleSettingsStore(overrideRoot).PathValue),
                    "explicit provider overrides are value-free process settings, reach Desktop task inputs and the child Orchestrator environment, and are not persisted locally");

                var unsafeRemNoteRejected = false; var unsafeAnkiRejected = false;
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.RemNoteMcpUrlEnvironmentVariable, "http://localhost:18765/mcp");
                try { AppConfig.Read(overrideConfig); } catch (InvalidDataException ex) { unsafeRemNoteRejected = ex.Message == "provider-override.remnote-loopback-required"; }
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.RemNoteMcpUrlEnvironmentVariable, "http://127.0.0.1:18765/mcp");
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.AnkiEndpointEnvironmentVariable, "http://127.0.0.1:65536");
                try { new DesktopModuleSettingsStore(overrideRoot).Load(); } catch (InvalidDataException ex) { unsafeAnkiRejected = ex.Message == "provider-override.anki-loopback-required"; }
                Assert(unsafeRemNoteRejected && unsafeAnkiRejected, "provider overrides reject localhost aliases, non-loopback hosts, invalid ports, and non-canonical paths before any process starts");
            }
            finally
            {
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.RemNoteMcpUrlEnvironmentVariable, previousRemNoteOverride);
                Environment.SetEnvironmentVariable(LoopbackProviderOverrides.AnkiEndpointEnvironmentVariable, previousAnkiOverride);
            }
        }

        private static void ProductionLaunchViewCanary(string root)
        {
            DesktopLaunchView view;
            Assert(DesktopLaunchOptions.TryParse(Array.Empty<string>(), out view) && view == DesktopLaunchView.Default,
                "production launch parser preserves the ordinary floating-window route");
            Assert(DesktopLaunchOptions.TryParse(new[] { "--open-settings" }, out view) && view == DesktopLaunchView.Settings,
                "production launch parser accepts the settings view route");
            Assert(DesktopLaunchOptions.TryParse(new[] { "--open-question" }, out view) && view == DesktopLaunchView.Question,
                "production launch parser accepts the question view route");
            Assert(!DesktopLaunchOptions.TryParse(new[] { "--unknown" }, out view),
                "production launch parser rejects unknown arguments instead of silently opening the wrong view");

            var config = Config(Path.Combine(root, "production-launch-view"));
            var controller = new DesktopController(
                new FakeCaptureProvider(null),
                new FakeScreenshotProvider(null),
                new FakeDesktopOrchestratorPort(),
                new FakeClock(new DateTime(2026, 9, 2, 2, 30, 0, DateTimeKind.Utc)),
                new FakeIdProvider());
            using (var form = new BoundClipperForm(config, controller, false))
            {
                form.Show(); Pump(40);
                form.OpenLaunchViewAsync(DesktopLaunchView.Settings).GetAwaiter().GetResult();
                var settings = WaitForForm<SettingsForm>(1800);
                Assert(settings != null && Find<Button>(settings, "knowledge-clipper.desktop.settings.export.anki.probe-readonly") != null,
                    "production settings route exposes the stable Anki read-only probe automation ID");
                settings.Close(); Pump(40);

                QuestionForm question = null;
                TextBox questionInput = null;
                using (var cancelQuestion = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    cancelQuestion.Tick += delegate
                    {
                        question = Application.OpenForms.OfType<QuestionForm>().FirstOrDefault();
                        if (question == null) return;
                        questionInput = Find<TextBox>(question, AutomationCatalogV11.QuestionInputAutomationId);
                        var cancel = Find<Button>(question, AutomationCatalogV11.QuestionCancelAutomationId);
                        if (questionInput == null || cancel == null) return;
                        cancelQuestion.Stop();
                        cancel.PerformClick();
                    };
                    cancelQuestion.Start();
                    form.OpenLaunchViewAsync(DesktopLaunchView.Question).GetAwaiter().GetResult();
                }
                Assert(question != null && questionInput != null && !question.Visible,
                    "production question route exposes the task-scoped question input automation ID and closes through its cancel action");
                form.Close();
            }
        }

        private static void WriteBuildReceipt(string package)
        {
            var names = Directory.GetFiles(package).Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var files = names.Select(name =>
            {
                var path = Path.Combine(package, name); var bytes = new FileInfo(path).Length;
                return new { path = name, bytes, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() };
            }).ToArray();
            var material = new StringBuilder(); foreach (var file in files) material.Append(file.path).Append('\n').Append(file.sha256).Append('\n').Append(file.bytes).Append('\n');
            var receipt = new { schemaVersion = "desktop-build-receipt.v1", fileCount = files.Length, totalBytes = files.Sum(file => file.bytes), treeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant(), files };
            File.WriteAllText(Path.Combine(package, "desktop-build-receipt.v1.json"), JsonSerializer.Serialize(receipt));
        }

        private static void CaptureFallbackCanary(string root)
        {
            var snapshot = new ClipboardSnapshot { Text = "preserved clipboard" };
            var provider = new WindowsDesktopCaptureProvider(root, new FixedClipboard(snapshot), new FailedCopySender());
            var result = provider.Capture("clip", new ContextModeState { enabled = false, granularity = "sentence" });
            Assert(result.hasContent && result.source == "clipboard-text", "no-selection fallback keeps original clipboard text");
            Assert(result.artifacts.Count == 1 && File.Exists(result.artifacts[0].sourcePath), "fallback text is staged as an immutable artifact");

            var emptyProvider = new WindowsDesktopCaptureProvider(root, new FixedClipboard(new ClipboardSnapshot()), new FailedCopySender());
            var failed = false;
            try { emptyProvider.Capture("clip", new ContextModeState { enabled = false, granularity = "sentence" }); }
            catch (InvalidOperationException ex) { failed = ex.Message.StartsWith("capture.copy-shortcut-failed:", StringComparison.Ordinal); }
            Assert(failed, "copy failure without fallback produces an explicit state code");

            var contextProvider = new FixedContextProvider("同一段落的前文", "同一段落的后文");
            var contextCapture = new WindowsDesktopCaptureProvider(root, new ScriptedTextClipboard("旧剪贴板", "选区正文"), new SuccessfulCopySender(), contextProvider)
                .Capture("clip", new ContextModeState { enabled = true, granularity = "paragraph" });
            Assert(contextCapture.contextAvailable && contextCapture.contextSegments != null && contextCapture.contextSegments.captureMethod == "uia-text-pattern", "context mode consumes a reliable Windows text-pattern context projection");
            Assert(contextCapture.artifacts.Count == 3 && contextCapture.contextSegments.selectionArtifactRef == contextCapture.selectedTextArtifactRef && contextCapture.contextSegments.prefixArtifactRef != null && contextCapture.contextSegments.suffixArtifactRef != null, "context prefix, selection, and suffix are staged as explicit immutable artifacts");
        }

        private static void FloatingUiCanary(string root)
        {
            var config = Config(root);
            var port = new FakeDesktopOrchestratorPort();
            var captures = new FakeCaptureProvider(new[] { TextCapture("clip"), TextCapture("read"), TextCapture("after-screenshot-exit") });
            var screenshots = new FakeScreenshotProvider(new[] { ImageFrame("question"), ImageFrame("answer"), ImageFrame("exit") });
            var controller = new DesktopController(captures, screenshots, port, new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)), new FakeIdProvider());
            using (var form = new BoundClipperForm(config, controller, false))
            {
                form.Show(); Pump(80);
                var clip = Find<Button>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ClipCapture]);
                var read = Find<Button>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ReadCapture]);
                var screenshot = Find<Button>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ScreenshotStart]);
                var contextMode = Find<Button>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextSet]);
                var originalMode = Find<Button>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.OriginalImageSet]);
                var minimize = Find<Button>(form, AutomationCatalogV11.WindowMinimizeAutomationId);
                var close = Find<Button>(form, AutomationCatalogV11.WindowCloseAutomationId);
                var titleBar = Find<TableLayoutPanel>(form, AutomationCatalogV11.WindowTitleBarAutomationId);
                var title = Find<Label>(form, AutomationCatalogV11.WindowTitleAutomationId);
                var dragFrame = Find<Panel>(form, AutomationCatalogV11.DragFrameAutomationId);
                var contextSection = Find<Panel>(form, "knowledge-clipper.desktop.section.context");
                var originalSection = Find<Panel>(form, "knowledge-clipper.desktop.section.original-image");
                var status = Find<Button>(form, AutomationCatalogV11.StatusAutomationId);
                var inlineInput = Find<TextBox>(form, AutomationCatalogV12.InlineQuestionInputAutomationId);
                var questionMenu = form.ContextMenuStrip.Items.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionOpen], true).SingleOrDefault() as ToolStripMenuItem;
                var restoreSizeMenu = form.ContextMenuStrip.Items.Find("knowledge-clipper.desktop.menu.window.restore-size", true).SingleOrDefault() as ToolStripMenuItem;
                Assert(clip != null && read != null && screenshot != null && status != null && inlineInput != null, "floating controls expose stable automation IDs including the dedicated inline TextBox");
                Assert(contextMode != null && originalMode != null && contextSection != null && originalSection != null, "context and original-image modes are persistent visible controls");
                Assert(minimize != null && close != null && titleBar != null && title != null && title.Text == "零碎知识剪藏" && title.AccessibleName == "零碎知识剪藏" && Find<Button>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionOpen]) == null && questionMenu != null && minimize.Text == "—" && close.Text == "×" && titleBar.PointToScreen(Point.Empty).Y < contextMode.PointToScreen(Point.Empty).Y && close.Left > minimize.Left, "floating title bar contains only neutral branding, minimize and close; question remains available only through the bottom input and context menu");
                minimize.PerformClick(); Pump(50); var minimizedToTray = !form.Visible; form.Show(); Pump(50);
                Assert(minimizedToTray && form.Visible, "minimize hides the floating window and it can be restored without recreating controller state");
                Assert(dragFrame != null && dragFrame.Cursor == Cursors.SizeAll, "outer frame exposes a dedicated all-edge drag affordance");
                Assert(form.MinimumSize.Width == 344 && form.MinimumSize.Height == 210 && restoreSizeMenu != null, "floating window has a hard minimum size and an explicit tray-accessible size recovery action");
                Assert(form.ResizeHitTest(new Point(1, 1)) == 13 && form.ResizeHitTest(new Point(form.ClientSize.Width - 2, 1)) == 14 && form.ResizeHitTest(new Point(1, form.ClientSize.Height - 2)) == 16 && form.ResizeHitTest(new Point(form.ClientSize.Width - 2, form.ClientSize.Height - 2)) == 17 && form.ResizeHitTest(new Point(form.ClientSize.Width / 2, form.ClientSize.Height / 2)) == 1, "all four borderless corners expose native resize hit targets while the interior remains draggable");
                form.ClientSize = new Size(520, 320); restoreSizeMenu.PerformClick(); Pump(30);
                Assert(form.ClientSize.Width == 344 && form.ClientSize.Height == 210, "restore-size action recovers the default floating-window dimensions after a user resize");
                var inner = dragFrame.Controls.Count == 0 ? null : dragFrame.Controls[0];
                Assert(inner != null && inner.Left >= 6 && inner.Top >= 6 && inner.Right <= dragFrame.ClientSize.Width - 6 && inner.Bottom <= dragFrame.ClientSize.Height - 6, "outer drag frame visibly surrounds the complete floating-window content");
                var contextGrid = contextSection.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
                var originalGrid = originalSection.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
                var bottomEdge = Find<Panel>(contextSection, "knowledge-clipper.desktop.frame-edge.bottom");
                var visibleContextBottom = contextSection.Parent.DisplayRectangle.Bottom - contextSection.Top;
                Assert(contextSection.Margin.All >= 1 && originalSection.Margin.All >= 1 && contextGrid != null && originalGrid != null && bottomEdge != null && contextGrid.Left > 0 && contextGrid.Top > 0 && contextGrid.Right < contextSection.ClientSize.Width && contextGrid.Bottom < visibleContextBottom && bottomEdge.Bottom <= visibleContextBottom && originalGrid.Left > 0 && originalGrid.Top > 0 && originalGrid.Right < originalSection.ClientSize.Width && originalGrid.Bottom < originalSection.ClientSize.Height, "context and original-image partitions render closed four-sided frames inside the visible layout bounds");
                Assert(contextMode.Text == "上下文模式" && (originalMode.Text == "原图模式" || originalMode.Text == "原图 · Anki") && !contextMode.Text.Contains("开") && !originalMode.Text.Contains("关"), "mode controls use compact one-key labels and original mode visibly identifies its Anki-only destination when active");
                Assert(contextMode.Top < clip.Top && contextMode.Width > clip.Width && originalMode.Top < screenshot.Top, "context mode sits above clip and read while original-image mode sits above screenshot");
                Assert(clip.Top == read.Top && read.Top == screenshot.Top && status.PointToScreen(Point.Empty).Y > clip.PointToScreen(Point.Empty).Y, "clip read screenshot remain one horizontal row above the independent status strip");
                Assert(inlineInput.PlaceholderText == "输入问题，按 Enter 提问" && inlineInput.AccessibleDescription == "输入问题，按 Enter 提问" && !inlineInput.Multiline, "inline question is a real single-line TextBox with the locked hint");
                Assert(inlineInput.PointToScreen(Point.Empty).Y >= status.PointToScreen(Point.Empty).Y + status.Height && inlineInput.Bottom <= form.ClientSize.Height - dragFrame.Padding.Bottom && inlineInput.Width > status.Width * 0.85, "inline question is an independent bottom row below status without covering modes or overflowing the floating window");
                Assert(form.ContextMenuStrip.Items.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.PlaybackStop], true).Length == 1, "stop playback is bound to a semantic menu action");
                Assert(form.ContextMenuStrip.Items.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.TaskStatusQueryRecent], true).Length == 1, "recent-task query is bound to a semantic menu action");
                Assert(form.ContextMenuStrip.Items.Cast<ToolStripItem>().All(item => !item.Text.Contains("RemNote Quick Clipper") && !item.Text.Contains("检查 RemNote")), "floating and tray primary menus expose no legacy product or single-export connection route");
                Assert(form.AutoScaleMode == AutoScaleMode.Dpi, "floating window is DPI-scaled");
                Assert((GetWindowLong(form.Handle, -20) & 0x08000000) != 0, "floating window keeps source focus with WS_EX_NOACTIVATE");
                var settingsMenu = form.ContextMenuStrip.Items.Find("knowledge-clipper.desktop.menu.settings", true).Single() as ToolStripMenuItem;
                settingsMenu.PerformClick(); Pump(80);
                var modelessSettings = Application.OpenForms.OfType<SettingsForm>().SingleOrDefault();
                Assert(modelessSettings != null && modelessSettings.Visible && form.Enabled, "settings opens modelessly and never disables floating-window dragging or actions");
                var beforeResize = form.ClientSize;
                var resizeGesturesBeforeDrag = form.ResizeGestureCount;
                DragResizeWithRealMouse(form, new Point(form.ClientSize.Width - 2, form.ClientSize.Height - 2), new Point(72, 46));
                Console.WriteLine("RESIZE_GESTURE_OBSERVED count=" + form.ResizeGestureCount + " hit=" + form.LastResizeGestureHit + " before=" + beforeResize + " after=" + form.ClientSize);
                Assert(form.ResizeGestureCount > resizeGesturesBeforeDrag && form.LastResizeGestureHit == 17, "real mouse-down on the visible bottom-right frame reaches the native resize gesture handler");
                WaitUntil(delegate { return form.ClientSize.Width >= beforeResize.Width + 10 && form.ClientSize.Height >= beforeResize.Height + 10; }, 1800, "real bottom-right resize while settings remains open");
                Assert(modelessSettings.Visible && form.ClientSize.Width > beforeResize.Width && form.ClientSize.Height > beforeResize.Height, "a real corner drag resizes the floating client area even while the modeless settings window is open");
                form.RestoreDefaultSize(); Pump(40);
                Assert(form.ClientSize.Width == form.MinimumSize.Width && form.ClientSize.Height == form.MinimumSize.Height, "default-size recovery remains available after a physical corner resize");
                settingsMenu.PerformClick(); Pump(30);
                Assert(Application.OpenForms.OfType<SettingsForm>().Count() == 1, "reopening settings activates the existing modeless window instead of creating duplicates");
                modelessSettings.Close(); Pump(30);

                QuestionForm openedQuestion = null;
                using (var cancelQuestion = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    cancelQuestion.Tick += delegate
                    {
                        openedQuestion = Application.OpenForms.OfType<QuestionForm>().FirstOrDefault();
                        if (openedQuestion == null) return;
                        var cancel = Find<Button>(openedQuestion, AutomationCatalogV11.QuestionCancelAutomationId);
                        if (cancel == null) return;
                        cancelQuestion.Stop(); cancel.PerformClick();
                    };
                    cancelQuestion.Start(); questionMenu.PerformClick();
                }
                WaitUntil(delegate { return controller.Snapshot().foregroundState == "idle"; }, 1200, "question cancel restores idle");
                Assert(openedQuestion != null && !openedQuestion.Visible && port.Questions.Count == 0, "context-menu question entry opens the existing dialog and local cancel emits no task command");
                WaitUntil(delegate { return status.Text == ""; }, 1400, "question cancel status settles"); Pump(60);

                using (var previousFocus = new Form { Text = "KnowledgeClipper UI focus canary", StartPosition = FormStartPosition.Manual, Location = new Point(form.Left - 180, form.Top), Size = new Size(140, 80), ShowInTaskbar = false })
                {
                    previousFocus.Show(); previousFocus.Activate(); Pump(80);
                    var previousHandle = GetForegroundWindow();
                    ClickWithRealMouse(inlineInput); WaitForInlineEditing(form, inlineInput, "inline TextBox receives real mouse focus after mouse-up");
                    Assert((GetWindowLong(form.Handle, -20) & 0x08000000) == 0 && GetForegroundWindow() == form.Handle, "inline editing temporarily activates only the floating window and removes WS_EX_NOACTIVATE");
                    TypeWithRealKeyboard(inlineInput, "inlinefocus");
                    Console.WriteLine("INLINE_KEYBOARD_OBSERVED text=" + inlineInput.Text + " selectionStart=" + inlineInput.SelectionStart + " focused=" + inlineInput.Focused);
                    Assert(inlineInput.Text == "inlinefocus" && inlineInput.SelectionStart == inlineInput.TextLength, "actual keyboard characters reach the focused inline TextBox and leave a visible caret position");
                    QuestionForm autoAnswer = null; string statusWhileAnswerOpened = null;
                    using (var observeAnswer = new System.Windows.Forms.Timer { Interval = 20 })
                    {
                        observeAnswer.Tick += delegate
                        {
                            autoAnswer = Application.OpenForms.OfType<QuestionForm>().FirstOrDefault();
                            if (autoAnswer == null) return;
                            statusWhileAnswerOpened = status.Text;
                            var cancel = Find<Button>(autoAnswer, AutomationCatalogV11.QuestionCancelAutomationId);
                            if (cancel == null) return;
                            observeAnswer.Stop(); cancel.PerformClick();
                        };
                        observeAnswer.Start(); PressRealKey("{ENTER}");
                        WaitUntil(delegate { return autoAnswer != null && !autoAnswer.Visible && port.Questions.Count == 1 && inlineInput.Text == "" && (GetWindowLong(form.Handle, -20) & 0x08000000) != 0; }, 1800, "inline Enter submit opens visible answer workflow");
                    }
                    Assert(autoAnswer != null && statusWhileAnswerOpened == "问题已提交，正在生成答案…", "inline Enter gives immediate processing feedback and automatically opens the answer/follow-up/make-cards window");
                    Assert(port.Questions.Single() == "question:inlinefocus" && controller.Snapshot().foregroundState == "idle" && controller.Snapshot().questionSession.open, "Enter emits exactly one initial question, keeps the accepted question session associated and releases other desktop actions after the answer window is locally closed");
                    Console.WriteLine("QUESTION_FOREGROUND_RESTORE expected=" + previousHandle + " captured=" + form.LastQuestionRestoreForeground + " succeeded=" + form.LastQuestionRestoreSucceeded + " actual=" + GetForegroundWindow());
                    Assert(previousHandle == IntPtr.Zero || !IsWindow(previousHandle) || GetForegroundWindow() == previousHandle, "successful inline submit restores the previous foreground window when it is still valid");
                    WaitUntil(delegate { return status.Text == "查看答案"; }, 1800, "inline question exposes stable answer continuation");
                    QuestionForm answerContinuation = null; bool answerContinuationHadControls = false;
                    using (var cancelAnswer = new System.Windows.Forms.Timer { Interval = 20 })
                    {
                        cancelAnswer.Tick += delegate
                        {
                            answerContinuation = Application.OpenForms.OfType<QuestionForm>().FirstOrDefault();
                            if (answerContinuation == null) return;
                            var cancel = Find<Button>(answerContinuation, AutomationCatalogV11.QuestionCancelAutomationId);
                            if (cancel == null) return;
                            answerContinuationHadControls = answerContinuation.Controls.Find(AutomationCatalogV11.QuestionAnswerAutomationId, true).Length == 1 && answerContinuation.Controls.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionFollowUp], true).Length == 1 && answerContinuation.Controls.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionMakeCards], true).Length == 1;
                            cancelAnswer.Stop(); cancel.PerformClick();
                        };
                        cancelAnswer.Start(); status.PerformClick();
                        WaitUntil(delegate { return answerContinuation != null && !answerContinuation.Visible; }, 1600, "answer continuation closes locally");
                    }
                    Assert(answerContinuation != null && answerContinuationHadControls && port.Questions.Count == 1, "the existing status strip opens the stable answer/follow-up/make-cards dialog without submitting a second question");
                    WaitUntil(delegate { return status.Text == ""; }, 1600, "answer continuation cancel settles before a new inline edit");

                    ClickWithRealMouse(inlineInput); WaitForInlineEditing(form, inlineInput, "inline focus before Escape"); TypeWithRealKeyboard(inlineInput, "escapelongervalue"); PressRealKey("{ESC}"); WaitUntil(delegate { return inlineInput.Text == "" && (GetWindowLong(form.Handle, -20) & 0x08000000) != 0; }, 1000, "inline Escape restore");
                    Assert(inlineInput.Text == "" && port.Questions.Count == 1 && (GetWindowLong(form.Handle, -20) & 0x08000000) != 0, "Escape clears inline text, sends no command and restores no-activate");

                    port.AcceptSubmissions = false;
                    ClickWithRealMouse(inlineInput); WaitForInlineEditing(form, inlineInput, "inline focus before failure"); TypeWithRealKeyboard(inlineInput, "retrytext"); PressRealKey("{ENTER}");
                    WaitUntil(delegate { return port.Questions.Count == 2 && (GetWindowLong(form.Handle, -20) & 0x08000000) != 0; }, 1600, "inline failed submit");
                    Assert(inlineInput.Text == "retrytext" && port.Questions[1] == "question:retrytext" && controller.Snapshot().foregroundState == "idle", "failed inline submit preserves the original text for retry and does not leave question mode active");
                    port.AcceptSubmissions = true;
                    ClickWithRealMouse(inlineInput); WaitForInlineEditing(form, inlineInput, "inline focus to clear failed text"); PressRealKey("{ESC}"); WaitUntil(delegate { return inlineInput.Text == "" && (GetWindowLong(form.Handle, -20) & 0x08000000) != 0; }, 1000, "inline post-failure Escape restore");
                    Assert(inlineInput.Text == "" && (GetWindowLong(form.Handle, -20) & 0x08000000) != 0, "Escape after failure clears retry text and safely restores no-activate");
                    previousFocus.Close();
                }
                WaitUntil(delegate { return status.Text == ""; }, 2800, "inline failure status settles"); Pump(60);

                var uiDecisionToken = new string('c', 64);
                port.RecentTask = new RecentTaskProjection { available = true, taskId = "fake-language-task", displayState = "awaiting-user", decisionKind = AutomationCatalogV11.LanguagePracticeDecisionKind, decisionTargetToken = uiDecisionToken };
                LanguagePracticeForm openedLanguagePractice = null;
                using (var chooseBoth = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    chooseBoth.Tick += delegate
                    {
                        openedLanguagePractice = Application.OpenForms.OfType<LanguagePracticeForm>().FirstOrDefault();
                        if (openedLanguagePractice == null) return;
                        var both = Find<Button>(openedLanguagePractice, AutomationCatalogV11.LanguagePracticeBothAutomationId);
                        if (both == null) return;
                        chooseBoth.Stop(); both.PerformClick();
                    };
                    chooseBoth.Start();
                    var recentItem = form.ContextMenuStrip.Items.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.TaskStatusQueryRecent], true)[0] as ToolStripMenuItem;
                    recentItem.PerformClick();
                    WaitUntil(delegate { return port.LanguagePracticeInputs.Count == 1; }, 1600, "language-practice fake input");
                }
                Assert(openedLanguagePractice != null && port.LanguagePracticeInputs.Single() == "fake-language-task:" + uiDecisionToken + ":both" && controller.Snapshot().lastAction == AutomationCatalogV11.LanguagePracticeSelect, "explicit language-practice projection opens the three-choice dialog and forwards the opaque target token plus selection");
                var cancelledDecisionToken = new string('f', 64);
                port.RecentTask = new RecentTaskProjection { available = true, taskId = "fake-language-cancel", displayState = "awaiting-user", decisionKind = AutomationCatalogV11.LanguagePracticeDecisionKind, decisionTargetToken = cancelledDecisionToken };
                LanguagePracticeForm cancelledLanguagePractice = null;
                using (var cancelChoice = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    cancelChoice.Tick += delegate
                    {
                        cancelledLanguagePractice = Application.OpenForms.OfType<LanguagePracticeForm>().FirstOrDefault();
                        if (cancelledLanguagePractice == null) return;
                        var cancel = Find<Button>(cancelledLanguagePractice, AutomationCatalogV11.LanguagePracticeCancelAutomationId);
                        if (cancel == null) return;
                        cancelChoice.Stop(); cancel.PerformClick();
                    };
                    cancelChoice.Start();
                    var recentItem = form.ContextMenuStrip.Items.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.TaskStatusQueryRecent], true)[0] as ToolStripMenuItem;
                    recentItem.PerformClick();
                    WaitUntil(delegate { return cancelledLanguagePractice != null && !cancelledLanguagePractice.Visible; }, 1600, "language-practice cancel");
                }
                Assert(port.LanguagePracticeInputs.Count == 1 && controller.Snapshot().recentTask.decisionKind == AutomationCatalogV11.LanguagePracticeDecisionKind && controller.Snapshot().recentTask.decisionTargetToken == cancelledDecisionToken, "cancelling the language-practice dialog sends no input and preserves the pending projected decision");
                port.RecentTask = new RecentTaskProjection { available = true, taskId = "ordinary-task", displayState = "awaiting-user", decisionKind = null };
                WaitUntil(delegate { return status.Text == ""; }, 1600, "language-practice success status settles"); Pump(60);

                var inactiveFrame = ((ModeFramePanel)contextSection).FrameColor;
                contextMode.PerformClick(); WaitUntil(delegate { return controller.Snapshot().contextMode.enabled; }, 1000, "direct context-mode toggle");
                Assert(((ModeFramePanel)contextSection).FrameColor != inactiveFrame && ((ModeFramePanel)contextSection).FrameColor.B > ((ModeFramePanel)contextSection).FrameColor.R && contextMode.AccessibleDescription == "已启用", "enabled context partition uses a shallow-blue frame");
                WaitUntil(delegate { return status.Text == ""; }, 1500, "context toggle status settles"); Pump(80);
                originalMode.PerformClick(); WaitUntil(delegate { return controller.Snapshot().originalImageMode; }, 1000, "direct original-image toggle");
                Assert(((ModeFramePanel)originalSection).FrameColor.B > ((ModeFramePanel)originalSection).FrameColor.R && originalMode.Text == "原图 · Anki" && originalMode.AccessibleDescription.Contains("目的端仅 Anki") && originalMode.AccessibleDescription.Contains("不检查 RemNote 原图"), "enabled original-image partition uses a shallow-blue frame and visibly enforces its Anki-only destination");
                var persisted = UserSettings.Load(config);
                Assert(persisted.ContextModeEnabled && persisted.OriginalImageMode, "direct mode toggles persist both settings");
                WaitUntil(delegate { return status.Text == ""; }, 1500, "original-image toggle status settles"); Pump(80);
                contextMode.PerformClick(); WaitUntil(delegate { return !controller.Snapshot().contextMode.enabled; }, 1000, "direct context-mode toggle off");
                Assert(((ModeFramePanel)contextSection).FrameColor == inactiveFrame && contextMode.AccessibleDescription == "未启用", "disabled context partition restores its neutral frame");
                WaitUntil(delegate { return status.Text == ""; }, 1500, "context toggle-off status settles"); Pump(80);

                clip.PerformClick(); WaitUntil(delegate { return port.SubmittedBundles.Count >= 1; }, 1500, "clip semantic action"); WaitUntil(delegate { return status.Text == ""; }, 1800, "clip success settles to blank idle status"); Pump(100);
                read.PerformClick(); WaitUntil(delegate { return port.SubmittedBundles.Count >= 2; }, 1500, "read semantic action");
                WaitUntil(delegate { return status.Text == "▶ 重播"; }, 2500, "read success becomes replay");
                Pump(100); status.PerformClick(); WaitUntil(delegate { return controller.Snapshot().playback == "playing"; }, 1000, "replay semantic playback action");
                Assert(controller.Snapshot().playback == "playing", "replay control dispatches semantic playback action");

                screenshot.PerformClick();
                var session = WaitForForm<ScreenshotSessionForm>(1800);
                Assert(session != null, "screenshot start opens semantic session controls");
                var answer = Find<Button>(session, AutomationCatalogV11.AutomationIds[AutomationCatalog.ScreenshotGroupAnswer]);
                var next = Find<Button>(session, AutomationCatalogV11.AutomationIds[AutomationCatalog.ScreenshotContinue]);
                var finish = Find<Button>(session, AutomationCatalogV11.AutomationIds[AutomationCatalog.ScreenshotFinish]);
                var undo = Find<Button>(session, AutomationCatalogV11.AutomationIds[AutomationCatalog.ScreenshotUndoLast]);
                var cancel = Find<Button>(session, AutomationCatalogV11.AutomationIds[AutomationCatalog.ScreenshotCancel]);
                Assert(answer != null && next != null && finish != null && undo != null && cancel != null, "screenshot session IDs cover group/continue/finish/undo/cancel");
                answer.PerformClick(); Pump(80); next.PerformClick(); WaitUntil(delegate { return controller.Snapshot().screenshotSession.answerImageCount == 1; }, 1600, "answer frame capture");
                WaitUntil(delegate { return session.Visible && finish.Enabled; }, 1600, "screenshot review UI is ready after capture");
                Assert(session.Visible && finish.Enabled, "screenshot finish is enabled only after the captured frame is reflected in the visible review UI");
                finish.PerformClick(); WaitUntil(delegate { return port.SubmittedBundles.Count >= 3; }, 5000, "screenshot finish submit");
                var bundle = port.SubmittedBundles[2];
                Assert(bundle.questionImages.Count == 1 && bundle.answerImages.Count == 1, "question and answer images remain explicit ordered groups");
                screenshot.PerformClick(); WaitUntil(delegate { return controller.Snapshot().screenshotSession.active && session.Visible; }, 1800, "second screenshot session opens");
                cancel.PerformClick(); WaitUntil(delegate { return !controller.Snapshot().screenshotSession.active && !session.Visible; }, 1200, "screenshot cancel exits immediately to idle");
                Assert(controller.Snapshot().foregroundState == "idle", "exiting screenshot session restores the ordinary action state");
                clip.PerformClick(); WaitUntil(delegate { return port.SubmittedBundles.Count >= 4; }, 1800, "immediate clip after screenshot exit");
                Assert(port.SubmittedBundles[3].intent == "clip", "cross-module click is not dropped by the previous status presentation");
                WaitUntil(delegate { return status.Text == ""; }, 1800, "final cross-module clip status settles before owner close"); Pump(50);
                close.PerformClick(); Pump(50);
                Assert(form.IsDisposed, "close control exits and disposes the floating window");
            }
        }

        private static void SettingsAndDialogAutomationCanary(string root)
        {
            AuthorityBindingProjectionCanary(root);
            var config = Config(root); var settings = UserSettings.Defaults(); settings.LanguageOverride = "en-US"; settings.ContextModeEnabled = true; settings.OriginalImageMode = true;
            var contentJobs = Path.Combine(root, "content-settings-public-v5-jobs"); Directory.CreateDirectory(contentJobs);
            var contentPipe = "KnowledgeClipper.ContentSettingsV5." + Guid.NewGuid().ToString("N");
            var contentServer = new FakeNamedPipeOrchestratorServer(contentPipe, contentJobs, 3); contentServer.Start();
            var contentSettingsService = new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory);
            var contentStore = new DesktopModuleSettingsStore(config.WorkDirectory);
            var contentHost = new DesktopContentSettingsHost(contentSettingsService, contentStore, contentJobs, new NamedPipeCommonIpcClient(contentPipe, 5000), true);
            var publicProbeJobs = Path.Combine(root, "anki-public-probe-v4-pipe-jobs"); Directory.CreateDirectory(publicProbeJobs);
            var publicProbePipe = "KnowledgeClipper.AnkiProbeV4." + Guid.NewGuid().ToString("N");
            var publicProbeServer = new FakeNamedPipeOrchestratorServer(publicProbePipe, publicProbeJobs, 1); publicProbeServer.Start();
            var publicProbeHost = new DesktopAnkiProbeHost(contentSettingsService, contentStore, AnkiProbeCapability.CreateForTest(true), publicProbeJobs, new NamedPipeCommonIpcClient(publicProbePipe, 5000), 15);
            using (var form = new SettingsForm(config, settings, null, publicProbeHost, null, contentHost))
            {
                Assert(form.Name == SettingsAutomationCatalog.Window && Find<ListBox>(form, SettingsAutomationCatalog.Navigation) != null, "project settings exposes a stable window and navigation automation ID");
                Assert(SettingsForm.PageKeys.All(key => form.Controls.Find("knowledge-clipper.desktop.settings.page." + key, true).Length == 1), "project settings hosts all seven module pages without hard-coding one exporter or prompt");
                var navigation = Find<ListBox>(form, SettingsAutomationCatalog.Navigation);
                Assert(navigation.Items.Count == 7 && navigation.Items.Cast<object>().Select(value => value.ToString()).SequenceEqual(SettingsForm.PageTitles), "settings navigation preserves the seven locked module page titles");
                Assert(Find<ComboBox>(form, "knowledge-clipper.desktop.settings.tts.language-override") != null, "settings exposes language override automation ID");
                Assert(Find<ComboBox>(form, SettingsAutomationCatalog.SpeechLanguage).Enabled && Find<ComboBox>(form, SettingsAutomationCatalog.SpeechLanguage).AccessibleDescription.Contains("effectiveAt=next_task"), "Speech 1.2.4 descriptor and additive Core v3 adapter jointly enable language override for the next task");
                Assert(Find<ComboBox>(form, "knowledge-clipper.desktop.settings.tts.voice.en") != null, "settings exposes Edge voice automation ID");
                Assert(Find<CheckBox>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextSet]) != null, "settings binds context mode automation ID");
                Assert(Find<CheckBox>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.OriginalImageSet]) != null, "settings binds original-image mode automation ID");
                Assert(Find<ComboBox>(form, SettingsAutomationCatalog.ContextGranularity) != null && Find<NumericUpDown>(form, SettingsAutomationCatalog.SpeechRate) != null && Find<NumericUpDown>(form, SettingsAutomationCatalog.SpeechVolume) != null && Find<NumericUpDown>(form, SettingsAutomationCatalog.SpeechPitch) != null, "editable capture and speech fields expose stable automation IDs");
                var workflow = Find<ComboBox>(form, SettingsAutomationCatalog.WorkflowSelector);
                Assert(workflow != null && workflow.Enabled && workflow.Items.Count == 1, "workflow selector is bound to the admitted Core v2.5.1 profile store");
                Assert(!Find<ComboBox>(form, SettingsAutomationCatalog.WorkflowProcessingProfile).Enabled && !Find<CheckBox>(form, SettingsAutomationCatalog.SpeechAutoPlay).Enabled, "fields absent or fixed read-only in Core adapter v3 remain non-editable, including workflow processingProfile and Speech autoPlay");
                Assert(Find<TextBox>(form, SettingsAutomationCatalog.SpeechDescriptorStatus).Text.Contains("speech.edge 1.2.4") && Find<TextBox>(form, SettingsAutomationCatalog.SpeechDescriptorStatus).Text.Contains("next_task"), "Speech companion identity and effective time are visible as descriptor evidence");
                var voiceCatalogAvailability = Find<TextBox>(form, SettingsAutomationCatalog.SpeechVoiceCatalogAvailability);
                Assert(voiceCatalogAvailability.ReadOnly && voiceCatalogAvailability.Text.Contains("Core v2.7") && voiceCatalogAvailability.Text.Contains("未运行"), "Speech voice catalog exposes the admitted public action while preserving a not-run initial state");
                var voiceDiscovery = Find<Button>(form, SettingsAutomationCatalog.SpeechVoiceDiscovery);
                Assert(!voiceDiscovery.Enabled && voiceDiscovery.AccessibleDescription.Contains("Core IPC 1.2"), "Speech voice discovery has a stable fail-closed action ID when the packaged Core module-action endpoint is absent");
                var speechPreview = Find<Button>(form, SettingsAutomationCatalog.SpeechPreview);
                var speechPreviewAvailability = Find<TextBox>(form, SettingsAutomationCatalog.SpeechPreviewAvailability);
                Assert(!speechPreview.Enabled && speechPreview.AccessibleDescription.Contains("physical_audio") && speechPreviewAvailability.ReadOnly && speechPreviewAvailability.Text.Contains("conditional"), "Speech preview remains fail-closed until a voice catalog and Core module-action endpoint exist");
                var deepSeekSecret = Find<TextBox>(form, SettingsAutomationCatalog.DeepSeekCredential);
                var mineruSecret = Find<TextBox>(form, SettingsAutomationCatalog.MineruCredential);
                Assert(deepSeekSecret != null && mineruSecret != null && deepSeekSecret.UseSystemPasswordChar && mineruSecret.UseSystemPasswordChar && deepSeekSecret.Enabled && mineruSecret.Enabled, "credential controls are editable, masked, and isolated from workflow profile fields");
                Assert(Find<TextBox>(form, SettingsAutomationCatalog.DeepSeekCredentialProtection).Text.Contains("当前 Windows 用户凭据保护") && Find<TextBox>(form, SettingsAutomationCatalog.MineruCredentialProtection).Text.Contains("当前 Windows 用户凭据保护"), "credential pages explicitly disclose local Windows CurrentUser protection");
                var mineruMetadata = Find<TextBox>(form, SettingsAutomationCatalog.MineruCredentialMetadata);
                Assert(mineruMetadata.Text.Contains("local-credential://image-ocr/mineru-api-token") && mineruMetadata.Text.Contains("configured=") && mineruMetadata.Text.Contains("verified=") && mineruMetadata.Text.Contains("sourceType=") && !mineruMetadata.Text.Contains("MINERU_API_TOKEN="), "Image/OCR credential status exposes only the canonical value-free metadata and inherited-process boundary");
                Assert(!Find<Button>(form, SettingsAutomationCatalog.DeepSeekCredentialTest).Enabled && !Find<Button>(form, SettingsAutomationCatalog.MineruCredentialTest).Enabled, "credential test buttons fail closed until provider test adapters exist");
                var descriptorStatus = Find<TextBox>(form, SettingsAutomationCatalog.ContentDescriptorStatus);
                var strategies = Find<TextBox>(form, SettingsAutomationCatalog.LanguageCardStrategies);
                var languageDefault = Find<ComboBox>(form, SettingsAutomationCatalog.LanguageCardDefault);
                var languagePromptPolicy = Find<CheckBox>(form, SettingsAutomationCatalog.LanguageCardPromptPolicy);
                Assert(descriptorStatus.ReadOnly && descriptorStatus.Text.Contains("content-companion-descriptor/1.0.0") && strategies.ReadOnly && strategies.Text.Contains("meaning-reading") && strategies.Text.Contains("writing") && strategies.Text.Contains("both"), "Content 0.7.0 companion dynamically projects all three declared language-card strategies as read-only evidence");
                Assert(languageDefault.Enabled && languageDefault.SelectedItem.ToString() == "ask" && languageDefault.AccessibleDescription.Contains("content-config-snapshot/1.1.0") && !languagePromptPolicy.Enabled && languagePromptPolicy.AccessibleDescription.Contains("真实 decision artifact"), "Content defaultChoice is editable through the Core v3 snapshot mapping while prompt-after-recognition remains conditional runtime UI behavior");
                Assert(Find<ComboBox>(form, SettingsAutomationCatalog.ContentModel).Enabled && Find<ComboBox>(form, SettingsAutomationCatalog.ContentModel).Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(new[] { "deepseek-chat", "deepseek-reasoner" }) && Find<NumericUpDown>(form, SettingsAutomationCatalog.ContentMaxRetries).Enabled && Find<NumericUpDown>(form, SettingsAutomationCatalog.ContentMaxRetries).Value == 3, "Content model and maxRetries expose exact companion values through Core adapter v3");
                Assert(Find<TextBox>(form, SettingsAutomationCatalog.ContentPromptIdentity).ReadOnly && Find<TextBox>(form, SettingsAutomationCatalog.ContentPromptIdentity).Text.Contains("open-md-v2") && Find<TextBox>(form, SettingsAutomationCatalog.ContentProfileIdentity).Text.Contains("category-registry/2.0.0"), "Prompt and category profile identities come from the Content descriptor and remain read-only");
                var imageDescriptor = Find<TextBox>(form, SettingsAutomationCatalog.OcrDescriptorStatus);
                var imageOffline = Find<Button>(form, SettingsAutomationCatalog.OcrOfflineReadiness);
                var imageLive = Find<Button>(form, SettingsAutomationCatalog.OcrLiveConnectivity);
                Assert(imageDescriptor.ReadOnly && imageDescriptor.Text.Contains("image-ocr-capability-config-descriptor.v5") && !imageOffline.Enabled && !Find<Button>(form, SettingsAutomationCatalog.OcrValidateOnly).Enabled && !Find<Button>(form, SettingsAutomationCatalog.OcrOfflineFakeProvider).Enabled, "Image 1.2.3 descriptor renders all offline actions and value-free credential semantics but keeps execution closed until an authority-admitted runner exists");
                Assert(!imageLive.Enabled && imageLive.Text.Contains("外部上传") && imageLive.AccessibleDescription.Contains("默认不运行"), "live Image connectivity is visibly external-upload work and is disabled by default");
                Assert(Find<TextBox>(form, SettingsAutomationCatalog.SettingsSource).ReadOnly && Find<TextBox>(form, SettingsAutomationCatalog.SettingsSource).Text.Contains("taskOverride > activeProfile > moduleDefault"), "settings source evidence exposes the authoritative precedence");
                Assert(Find<TextBox>(form, SettingsAutomationCatalog.SettingsEffectiveAt).ReadOnly && Find<TextBox>(form, SettingsAutomationCatalog.SettingsEffectiveAt).Text.Contains("下一任务") && Find<TextBox>(form, SettingsAutomationCatalog.SettingsRevision).ReadOnly && Find<TextBox>(form, SettingsAutomationCatalog.SettingsRevision).Text.Length == 64, "effective time and current settings revision are visible read-only evidence");
                Assert(Find<TextBox>(form, SettingsAutomationCatalog.SettingsNextSnapshot).ReadOnly && Find<TextBox>(form, SettingsAutomationCatalog.SettingsNextSnapshot).Text.Contains("knowledge-clipper-task-settings-snapshot.v3") && Find<TextBox>(form, SettingsAutomationCatalog.DiagnosticsAuthority).ReadOnly && Find<TextBox>(form, SettingsAutomationCatalog.DiagnosticsAuthority).Text.Contains("authority unknown") && Find<TextBox>(form, SettingsAutomationCatalog.DiagnosticsRegistry).ReadOnly && Find<TextBox>(form, SettingsAutomationCatalog.DiagnosticsRegistry).Text.Contains("fail-closed") && Find<TextBox>(form, SettingsAutomationCatalog.DiagnosticsCheckpoint).Text.Contains("Core checkpoint v2.5.1") && Find<TextBox>(form, SettingsAutomationCatalog.DiagnosticsCheckpoint).Text.Contains("admitted adapter v3"), "next-task v3 snapshot and Core settings identity remain visible while an unbound development UI fails closed on authority metadata");
                var retention = Find<TextBox>(form, SettingsAutomationCatalog.StorageRetention);
                Assert(retention.ReadOnly && retention.Text.Contains("authority unknown") && retention.Text.Contains("显式评估/隔离可用") && retention.Text.Contains("自动执行关闭") && retention.Text.Contains("破坏性删除不提供") && retention.Text.Contains("生产禁用") && !retention.Text.Contains("Core v1.2.34"), "retention read-only projection rejects stale hard-coded authority versions while automatic execution and destructive delete remain unavailable");
                var originalMedia = Find<TextBox>(form, SettingsAutomationCatalog.OriginalMediaAvailability);
                Assert(originalMedia.ReadOnly && originalMedia.Text.Contains("固定为 Anki") && originalMedia.Text.Contains("不触发或检查 RemNote 原图能力"), "original-image mode visibly follows the locked Anki-only destination policy without treating RemNote media as a gap");
                var remnoteProbe = Find<Button>(form, SettingsAutomationCatalog.ExportRemNoteProbe);
                var remnoteProbeStatus = Find<TextBox>(form, SettingsAutomationCatalog.ExportRemNoteProbeStatus);
                Assert(!remnoteProbe.Enabled && remnoteProbe.AccessibleDescription.Contains("Core IPC 1.2") && remnoteProbeStatus.ReadOnly && remnoteProbeStatus.Text.Contains("conditional"), "RemNote probe remains visible and fail-closed without the packaged Core module-action endpoint");
                Assert(!Find<Button>(form, SettingsAutomationCatalog.ExportRemNoteDiscoverTarget).Enabled && Find<TextBox>(form, SettingsAutomationCatalog.ExportRemNoteDiscoverTargetStatus).Text.Contains("conditional"), "RemNote exact-title discovery remains visible but disabled until Core IPC 1.3 is authority-admitted");
                var promptCopy = Find<Button>(form, SettingsAutomationCatalog.PromptCopy); var promptOpen = Find<Button>(form, SettingsAutomationCatalog.PromptOpenExternal); var promptValidate = Find<Button>(form, SettingsAutomationCatalog.PromptValidate); var promptDiff = Find<Button>(form, SettingsAutomationCatalog.PromptDiff);
                Assert(promptCopy != null && promptCopy.Enabled && promptOpen != null && !promptOpen.Enabled && promptValidate != null && !promptValidate.Enabled && promptDiff != null && !promptDiff.Enabled, "Prompt controls consume the admitted Content adapter and keep editing disabled before a user copy exists");
                var exportTargets = Find<GroupBox>(form, "knowledge-clipper.desktop.settings.export-targets");
                Assert(form.Text == "零碎知识剪藏 · 设置" && exportTargets != null && exportTargets.Controls.OfType<Label>().Any(label => label.Text.Contains("RemNote") && label.Text.Contains("Anki") && label.Text.Contains("不预设唯一目标")), "settings uses neutral branding and presents RemNote and Anki together only as final export targets");
                Assert(Find<CheckBox>(form, SettingsAutomationCatalog.ExportRemNote).Enabled && Find<CheckBox>(form, SettingsAutomationCatalog.ExportAnki).Enabled, "peer export destinations follow their exact Core v3 mappings through /remnote and /anki/enabled");
                var ankiProbe = Find<Button>(form, SettingsAutomationCatalog.ExportAnkiProbe);
                Assert(ankiProbe.Enabled && ankiProbe.Text == "只读连接检查" && ankiProbe.AccessibleDescription.Contains("worker-owned loopback read-only probe") && Find<TextBox>(form, SettingsAutomationCatalog.ExportAnkiProbeStatus).Text.Contains("available"), "authority v1.2.12 admits the explicit Anki 1.2.1 worker-owned read-only probe action");
                Assert(!Find<Button>(form, SettingsAutomationCatalog.ExportAnkiInitialize).Enabled && Find<Button>(form, SettingsAutomationCatalog.ExportAnkiInitialize).Text.Contains("会写入 Anki") && Find<TextBox>(form, SettingsAutomationCatalog.ExportAnkiInitializeStatus).Text.Contains("conditional"), "Anki managed initialization is a separate explicit write control and stays disabled without a public Core action");
                Assert(Find<Label>(form, SettingsAutomationCatalog.Status).Text.Contains("adapter v3", StringComparison.Ordinal) && Find<Label>(form, SettingsAutomationCatalog.Status).Text.Contains("authority unknown", StringComparison.Ordinal), "settings footer shows the Core adapter and fails closed when no packaged authority binding is present");
                Assert(form.Controls.Find("knowledge-clipper.desktop.settings.cards.date", true).Length == 0, "card settings do not expose a date control");
                form.Show(); Pump(40);
                var initialProfileCount = workflow.Items.Count;
                Find<Button>(form, SettingsAutomationCatalog.WorkflowCreate).PerformClick();
                Find<Button>(form, SettingsAutomationCatalog.WorkflowCopy).PerformClick();
                Assert(workflow.Items.Count == initialProfileCount + 2, "workflow profiles support additive create and copy without a revision ledger");
                Find<Button>(form, SettingsAutomationCatalog.WorkflowRestoreDefault).PerformClick();
                Assert(Find<ComboBox>(form, SettingsAutomationCatalog.WorkflowProcessingProfile).SelectedIndex == 0, "restore-default resets the active workflow profile in the pending edit");
                navigation.SelectedIndex = 2; Pump(30);
                promptCopy.PerformClick(); Pump(120);
                var copiedDrafts = Directory.GetFiles(config.WorkDirectory, "content-settings.draft.json", SearchOption.AllDirectories);
                if (copiedDrafts.Length != 1 || !promptOpen.Enabled || !promptValidate.Enabled || !promptDiff.Enabled) throw new InvalidOperationException("Content copy diagnostic: " + Find<Label>(form, SettingsAutomationCatalog.Status).Text + " drafts=" + copiedDrafts.Length + " open=" + promptOpen.Enabled + " validate=" + promptValidate.Enabled + " diff=" + promptDiff.Enabled);
                Assert(true, "Content adapter copies the read-only built-in Prompt into one editable user version");
                promptValidate.PerformClick(); Pump(80); Assert(Find<Label>(form, SettingsAutomationCatalog.Status).Text.Contains("校验通过"), "Content user Prompt validates through the authority worker adapter");
                promptDiff.PerformClick(); Pump(80); Assert(Find<Label>(form, SettingsAutomationCatalog.Status).Text.Contains("valid=true"), "Content user Prompt diff is computed by the authority worker adapter");

                Assert(form.AutoScaleMode == AutoScaleMode.Dpi, "project settings opts into Windows DPI scaling");
                var pageHost = Find<Panel>(form, SettingsAutomationCatalog.PageHost);
                for (var pageIndex = 0; pageIndex < SettingsForm.PageKeys.Length; pageIndex++)
                {
                    navigation.SelectedIndex = pageIndex; Pump(15);
                    var page = form.Controls.Find("knowledge-clipper.desktop.settings.page." + SettingsForm.PageKeys[pageIndex], true)[0];
                    Assert(page.Visible && page.Bounds.Width <= pageHost.ClientSize.Width && page.Bounds.Height <= pageHost.ClientSize.Height, "settings page switches visibly without overflowing host: " + SettingsForm.PageKeys[pageIndex]);
                }
                Assert(Find<Button>(form, SettingsAutomationCatalog.Save).Visible && Find<Button>(form, SettingsAutomationCatalog.Cancel).Visible, "settings footer keeps both save and cancel controls visible");
                Find<CheckBox>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextSet]).Checked = false;
                Find<CheckBox>(form, SettingsAutomationCatalog.OriginalImage).Checked = true;
                Find<NumericUpDown>(form, SettingsAutomationCatalog.SpeechRate).Value = 17;
                Find<ComboBox>(form, SettingsAutomationCatalog.SpeechLanguage).SelectedIndex = 2;
                Find<ComboBox>(form, SettingsAutomationCatalog.LanguageCardDefault).SelectedItem = "both";
                Find<ComboBox>(form, SettingsAutomationCatalog.ContentModel).SelectedItem = "deepseek-reasoner";
                Find<NumericUpDown>(form, SettingsAutomationCatalog.ContentMaxRetries).Value = 5;
                Find<ComboBox>(form, SettingsAutomationCatalog.OcrLanguage).SelectedIndex = 1; Find<NumericUpDown>(form, SettingsAutomationCatalog.OcrTimeout).Value = 321;
                Find<CheckBox>(form, SettingsAutomationCatalog.ExportRemNote).Checked = true; Find<TextBox>(form, SettingsAutomationCatalog.ExportRemNoteRootId).Text = "canary-root-rem-id";
                Find<CheckBox>(form, SettingsAutomationCatalog.ExportAnki).Checked = true; Find<TextBox>(form, SettingsAutomationCatalog.ExportAnkiProfile).Text = "Canary Profile";
                var deepSeekValue = "canary-deepseek-local-only"; var mineruValue = "canary-mineru-local-only";
                deepSeekSecret.Text = deepSeekValue; mineruSecret.Text = mineruValue;
                Console.WriteLine("SETTINGS_SPEECH_INPUT voices=" + string.Join("|", new[] { "zh", "en", "es", "ja", "ko" }.Select(code => Convert.ToString(Find<ComboBox>(form, SettingsAutomationCatalog.SpeechVoicePrefix + code).SelectedItem))) + " rate=" + Find<NumericUpDown>(form, SettingsAutomationCatalog.SpeechRate).Value + " volume=" + Find<NumericUpDown>(form, SettingsAutomationCatalog.SpeechVolume).Value + " pitch=" + Find<NumericUpDown>(form, SettingsAutomationCatalog.SpeechPitch).Value);
                Find<Button>(form, SettingsAutomationCatalog.Save).PerformClick(); Pump(20);
                Console.WriteLine("SETTINGS_SAVE_OBSERVED dialog=" + form.DialogResult);
                Assert(form.DialogResult == DialogResult.OK && form.SavedSettings != null && !form.SavedSettings.ContextModeEnabled && form.SavedSettings.RatePercent == 17, "settings save collects only supported fields and closes with an explicit result");
                var persisted = UserSettings.Load(config);
                Assert(!persisted.ContextModeEnabled && persisted.RatePercent == 17, "settings save persists supported values for the next desktop operation");
                var persistedCredentials = DesktopCredentialSettings.Load(config.WorkDirectory);
                Assert(persistedCredentials.DeepSeekApiKey == deepSeekValue && persistedCredentials.MineruApiToken == mineruValue && persistedCredentials.StorageStatus == "protected-current-user", "DPAPI CurrentUser storage decrypts the exact credentials for the same Windows user");
                var credentialPath = DesktopCredentialSettings.PathFor(config.WorkDirectory); var credentialBackupPath = credentialPath + ".bak";
                var protectedDiskText = File.ReadAllText(credentialPath) + File.ReadAllText(credentialBackupPath);
                Assert(protectedDiskText.Contains("knowledge-clipper-desktop-credentials.dpapi.v1") && protectedDiskText.Contains("CurrentUser") && !protectedDiskText.Contains(deepSeekValue) && !protectedDiskText.Contains(mineruValue) && !protectedDiskText.Contains("DeepSeekApiKey") && !protectedDiskText.Contains("MineruApiToken"), "credential primary and backup contain only DPAPI envelopes and zero plaintext properties or values");
                var start = new System.Diagnostics.ProcessStartInfo(); persistedCredentials.ApplyTo(start);
                Assert(start.Environment.ContainsKey("AI_PRODUCER_API_KEY") && start.Environment.ContainsKey("MINERU_API_TOKEN"), "local credentials map only to worker-owned environment variables at process start");
                var settingsTaskRoot = Path.Combine(root, "settings-secret-boundary-jobs"); var settingsStaged = Path.Combine(root, "settings-secret-boundary.txt"); var settingsBytes = new UTF8Encoding(false).GetBytes("settings boundary capture"); File.WriteAllBytes(settingsStaged, settingsBytes);
                var settingsArtifact = WindowsDesktopCaptureProvider.Artifact("capture.text.settings-boundary", "inputs/capture/settings-boundary.txt", settingsBytes, "text/plain", "utf-8", settingsStaged);
                var settingsBundle = new CaptureBundleV1 { schemaVersion = AutomationCatalog.CaptureBundleSchemaVersion, captureId = "capture-settings-boundary", createdAt = "2026-08-28T06:00:00Z", intent = "clip", source = "clipboard", modeSnapshot = new CaptureModeSnapshot { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "ocr" }, artifacts = new List<CaptureArtifact> { settingsArtifact }, selectedTextArtifactRef = settingsArtifact.artifactRef, questionImages = new List<string>(), answerImages = new List<string>(), questionTurns = new List<QuestionTurnReference>() };
                var workflowService = new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory);
                new FileDesktopCommonCommandSource(settingsTaskRoot, new FakeClock(new DateTime(2026, 8, 28, 6, 0, 0, DateTimeKind.Utc)), new UiCommonIdProvider(), new CoreDesktopTaskSettingsPublisher(workflowService)).PrepareCapture(settingsBundle);
                var taskText = string.Join("\n", Directory.GetFiles(settingsTaskRoot, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
                Assert(!taskText.Contains(persistedCredentials.DeepSeekApiKey) && !taskText.Contains(persistedCredentials.MineruApiToken), "credentials never enter the immutable task package, manifest, or payload files");
                Assert(Directory.GetFiles(settingsTaskRoot, "task.manifest.v1.json", SearchOption.AllDirectories).Length == 1 && Directory.GetFiles(settingsTaskRoot, "task.manifest.v2.json", SearchOption.AllDirectories).Length == 1, "settings binding preserves manifest v1 and appends canonical manifest v2");
                var settingsSnapshot = Directory.GetFiles(settingsTaskRoot, "task-settings-snapshot.v1.json", SearchOption.AllDirectories).Single();
                var settingsSnapshotText = File.ReadAllText(settingsSnapshot);
                Assert(settingsSnapshotText.Contains("knowledge-clipper-task-settings-snapshot.v3") && settingsSnapshotText.Contains("\"secretReferences\": []") && settingsSnapshotText.Contains("canary-root-rem-id") && settingsSnapshotText.Contains("Canary Profile") == false, "new task freezes v3 Speech/Image/Content/RemNote/Anki identities while excluding local Anki profile text and all secrets");
                Assert(!settingsSnapshotText.Contains("\"speech\""), "card capture keeps Speech stage settings out of a task that has no speech stage");
                Assert(settingsSnapshotText.Contains("content-config-snapshot/1.1.0") && settingsSnapshotText.Contains("\"languagePracticeDefault\": \"both\"") && settingsSnapshotText.Contains("\"providerModel\": \"deepseek-reasoner\"") && settingsSnapshotText.Contains("\"providerMaxRetries\": 5"), "Core v3 task snapshot projects the SHA-bound Content 1.1 runtime policy selected in Desktop");
                Assert(settingsSnapshotText.Contains("\"language\": \"en\"") && settingsSnapshotText.Contains("\"timeoutSeconds\": 321") && settingsSnapshotText.Contains("content.config.") && settingsSnapshotText.Contains("anki.config."), "Image settings and SHA-bound Content/Anki artifact identities enter the immutable task snapshot");
                var contentSnapshotPath = Directory.GetFiles(settingsTaskRoot, "content-config-snapshot.v1.1.json", SearchOption.AllDirectories).Single();
                var contentSnapshotText = File.ReadAllText(contentSnapshotPath);
                Assert(contentSnapshotText.Contains("content-config-snapshot/1.1.0") && contentSnapshotText.Contains("\"languagePracticeDefault\": \"both\"") && contentSnapshotText.Contains("\"model\": \"deepseek-reasoner\"") && contentSnapshotText.Contains("\"maxRetries\": 5") && Directory.GetFiles(settingsTaskRoot, "anki-export.v1.json", SearchOption.AllDirectories).Length == 1, "Desktop publishes one immutable Content 1.1 runtime policy artifact and one Anki config artifact");

                var questionTaskRoot = Path.Combine(root, "settings-question-stage-jobs");
                new FileDesktopCommonCommandSource(questionTaskRoot, new FakeClock(new DateTime(2026, 8, 30, 8, 1, 0, DateTimeKind.Utc)), new UiCommonIdProvider(), new CoreDesktopTaskSettingsPublisher(workflowService)).PrepareQuestion("候选初问", false, null, null, null);
                var questionManifestText = File.ReadAllText(Directory.GetFiles(questionTaskRoot, "task.manifest.v2.json", SearchOption.AllDirectories).Single());
                Assert(questionManifestText.Contains("\"intent\": \"clip.question\"") && questionManifestText.Contains("\"destinations\": []") && Directory.GetFiles(questionTaskRoot, "anki-export.v1.json", SearchOption.AllDirectories).Length == 0, "initial question uses the authoritative no-destination workflow even when the active profile exports cards to Anki");

                var speechTaskRoot = Path.Combine(root, "settings-speech-stage-jobs");
                var speechBundle = new CaptureBundleV1 { schemaVersion = AutomationCatalog.CaptureBundleSchemaVersion, captureId = "capture-settings-speech", createdAt = "2026-08-30T08:00:00Z", intent = "read", source = "clipboard", modeSnapshot = new CaptureModeSnapshot { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "ocr" }, artifacts = new List<CaptureArtifact> { settingsArtifact }, selectedTextArtifactRef = settingsArtifact.artifactRef, questionImages = new List<string>(), answerImages = new List<string>(), questionTurns = new List<QuestionTurnReference>() };
                new FileDesktopCommonCommandSource(speechTaskRoot, new FakeClock(new DateTime(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc)), new UiCommonIdProvider(), new CoreDesktopTaskSettingsPublisher(workflowService)).PrepareCapture(speechBundle);
                var speechSnapshotText = File.ReadAllText(Directory.GetFiles(speechTaskRoot, "task-settings-snapshot.v1.json", SearchOption.AllDirectories).Single());
                Assert(speechSnapshotText.Contains("\"languageStrategy\": \"override\"") && speechSnapshotText.Contains("\"languageOverride\": \"en-US\"") && speechSnapshotText.Contains("\"autoPlay\": true"), "read-aloud task freezes descriptor-driven Speech language override while autoPlay remains fixed true");

                var originalTaskRoot = Path.Combine(root, "settings-original-anki-only-jobs");
                var originalBytes = new byte[] { 1, 2, 3, 4 }; var originalStaged = Path.Combine(root, "original-anki-only.png"); File.WriteAllBytes(originalStaged, originalBytes);
                var originalArtifact = WindowsDesktopCaptureProvider.Artifact("capture.image.original-policy", "inputs/capture/original-policy.png", originalBytes, "image/png", null, originalStaged);
                var originalBundle = new CaptureBundleV1 { schemaVersion = AutomationCatalog.CaptureBundleSchemaVersion, captureId = "capture-original-policy", createdAt = "2026-09-01T05:00:00Z", intent = "screenshot", source = "screenshot", modeSnapshot = new CaptureModeSnapshot { workflow = "card", contextEnabled = false, contextGranularity = "sentence", contextApplied = false, imageHandling = "original" }, artifacts = new List<CaptureArtifact> { originalArtifact }, selectedTextArtifactRef = null, questionImages = new List<string> { originalArtifact.artifactRef }, answerImages = new List<string>(), questionTurns = new List<QuestionTurnReference>() };
                new FileDesktopCommonCommandSource(originalTaskRoot, new FakeClock(new DateTime(2026, 9, 1, 5, 0, 0, DateTimeKind.Utc)), new UiCommonIdProvider(), new CoreDesktopTaskSettingsPublisher(workflowService)).PrepareCapture(originalBundle);
                using var originalManifest = JsonDocument.Parse(File.ReadAllBytes(Directory.GetFiles(originalTaskRoot, "task.manifest.v2.json", SearchOption.AllDirectories).Single()));
                var originalDestinations = originalManifest.RootElement.GetProperty("destinations").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert(originalDestinations.SequenceEqual(new[] { "anki" }) && Directory.GetFiles(originalTaskRoot, "anki-export.v1.json", SearchOption.AllDirectories).Length == 1, "original-image task manifest forces the exact sole destination [anki] and publishes the SHA-bound Anki config");
            }
            contentServer.Complete();
            Assert(contentServer.MessageTypes.SequenceEqual(new[] { "module-action", "module-action", "module-action" }) && contentServer.ModuleActionPayloads.All(payload => Convert.ToString(payload["schemaVersion"]) == "knowledge-clipper-module-action.v5"), "Content Prompt copy/validate/diff use only public Core IPC 1.6 raw typed outputs and never start the worker from Desktop");
            var invokedActions = new List<string>();
            var descriptorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "descriptors", "image-ocr-capability-config-descriptor.v5.json");
            var fakeCapabilityHost = new DesktopModuleCapabilityHost(ModuleCapabilityDescriptorLoader.TryLoad(descriptorPath), delegate(ModuleCapabilityAction action) { invokedActions.Add(action.ActionId); return "ready-offline"; });
            using (var capabilityForm = new SettingsForm(config, UserSettings.Load(config), fakeCapabilityHost))
            {
                capabilityForm.Show(); Pump(40);
                Find<ListBox>(capabilityForm, SettingsAutomationCatalog.Navigation).SelectedIndex = 1; Pump(30);
                var offline = Find<Button>(capabilityForm, SettingsAutomationCatalog.OcrOfflineReadiness); var validate = Find<Button>(capabilityForm, SettingsAutomationCatalog.OcrValidateOnly); var fake = Find<Button>(capabilityForm, SettingsAutomationCatalog.OcrOfflineFakeProvider); var live = Find<Button>(capabilityForm, SettingsAutomationCatalog.OcrLiveConnectivity);
                Assert(offline.Enabled && validate.Enabled && fake.Enabled && !live.Enabled && live.AccessibleDescription.Contains("external-upload"), "generic capability renderer enables all three network-prohibited Image actions and never auto-enables live upload");
                offline.PerformClick(); Pump(20); validate.PerformClick(); Pump(20); fake.PerformClick(); Pump(20); live.PerformClick(); Pump(20);
                Assert(invokedActions.SequenceEqual(new[] { "image.ocr.status.offline-readiness", "image.ocr.test.validate-only", "image.ocr.test.offline-fake-provider" }) && Find<Label>(capabilityForm, SettingsAutomationCatalog.Status).Text.Contains("ready-offline"), "Image fake runner receives the exact three offline actionIds while live external upload never runs");
                capabilityForm.Close(); Pump(20);
            }
            var fakeAnkiSettings = new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory);
            var probePackageRoot = Path.Combine(root, "anki-probe-package"); Directory.CreateDirectory(probePackageRoot);
            var probeConfigRef = new ArtifactReference("anki.config.ui-canary", "anki-export-config", "1.0.0", "artifacts/anki.config.ui-canary/artifact.json", new string('a', 64));
            DesktopAnkiProbeHost.PublishProbeTaskManifest(probePackageRoot, "settings-anki-probe-ui-canary", probeConfigRef);
            using (var probeManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(probePackageRoot, "task.manifest.v1.json"))))
            {
                var manifestRoot = probeManifest.RootElement;
                var initialConfig = manifestRoot.GetProperty("inputRefs").EnumerateArray().Single();
                Assert(manifestRoot.GetProperty("taskId").GetString() == "settings-anki-probe-ui-canary" && manifestRoot.GetProperty("intent").GetString() == "clip.card" && manifestRoot.GetProperty("retentionClass").GetString() == "evidence" && initialConfig.GetProperty("artifactRef").GetString() == "anki.config.ui-canary" && initialConfig.EnumerateObject().Count() == 4 && manifestRoot.GetProperty("requestedOutputs").EnumerateArray().Single().GetString() == "anki-connection-status" && manifestRoot.GetProperty("metadata").GetProperty("effectClass").GetString() == "provider-read-only", "Anki explicit probe publishes a strict common v1 task manifest before Core starts the worker");
            }
            using (var probeManifestV2 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(probePackageRoot, "task.manifest.v2.json"))))
            {
                var manifestRoot = probeManifestV2.RootElement;
                Assert(manifestRoot.GetProperty("schemaVersion").GetString() == "2.0.0" && manifestRoot.GetProperty("executionProfile").GetString() == "cards" && manifestRoot.GetProperty("destinations").EnumerateArray().Single().GetString() == "anki" && manifestRoot.GetProperty("retentionClass").GetString() == "evidence" && manifestRoot.GetProperty("inputRefs").EnumerateArray().Single().EnumerateObject().Count() == 4, "Anki explicit probe appends the destination-neutral v2 manifest without changing the frozen v1 worker input");
            }
            using (var probeForm = new SettingsForm(config, UserSettings.Load(config), null, publicProbeHost))
            {
                probeForm.Show(); Pump(30); Find<ListBox>(probeForm, SettingsAutomationCatalog.Navigation).SelectedIndex = 5; Pump(20);
                var probe = Find<Button>(probeForm, SettingsAutomationCatalog.ExportAnkiProbe);
                Assert(probe is SemanticActionButton && probe.Enabled && probe.AccessibleDescription.Contains("Core resolver") && probe.AccessibleDescription.Contains("禁止桌面直连"), "authority-admitted Anki probe exposes one semantic command button for pointer, keyboard and UI Automation");
                Assert(probe.AccessibilityObject.GetType().FullName == "System.Windows.Forms.Button+ButtonAccessibleObject", "authority-admitted Anki probe retains the standard WinForms ButtonAccessibleObject provider");
                var invokeTask = Task.Run(delegate
                {
                    var element = System.Windows.Automation.AutomationElement.FromHandle(probe.Handle);
                    var pattern = (System.Windows.Automation.InvokePattern)element.GetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern);
                    pattern.Invoke();
                });
                WaitUntil(delegate { return invokeTask.IsCompleted; }, 5000, "native UIAutomationClient InvokePattern returns");
                if (invokeTask.IsFaulted) throw invokeTask.Exception.InnerException;
                WaitUntil(delegate { return Find<TextBox>(probeForm, SettingsAutomationCatalog.ExportAnkiProbeStatus).Text.Contains("就绪") && probe.Enabled; }, 5000, "public Core v4 Anki probe result projection through native UIAutomationClient InvokePattern");
                Assert(Find<TextBox>(probeForm, SettingsAutomationCatalog.ExportAnkiProbeStatus).Text.Contains("module-actions/settings-anki-probe-") && Find<Label>(probeForm, SettingsAutomationCatalog.Status).Text.Contains("未执行 provision/write"), "UI Automation Invoke shares the production Anki handler, writes the immutable module action and projects only a redacted typed status plus receipt");
                var semanticTrace = File.ReadAllLines(DesktopSemanticActionTrace.PathFor(config.WorkDirectory)).Select(line => JsonDocument.Parse(line)).ToArray();
                try
                {
                    var transitions = semanticTrace.Select(document => document.RootElement.GetProperty("transition").GetString()).ToArray();
                    var allowedProperties = new HashSet<string>(new[] { "schemaVersion", "observedAtUtc", "processId", "managedThreadId", "actionId", "transition", "reason" }, StringComparer.Ordinal);
                    Assert(new[] { "invoke_received", "dispatch_queued", "dispatch_started", "handler_entered", "reset" }.All(expected => transitions.Contains(expected)) && semanticTrace.All(document => document.RootElement.EnumerateObject().All(property => allowedProperties.Contains(property.Name))), "semantic action trace durably records UI dispatch transitions without provider configuration or secret fields");
                }
                finally { foreach (var document in semanticTrace) document.Dispose(); }
                probeForm.Close(); Pump(20);
            }
            publicProbeServer.Complete();
            Assert(publicProbeServer.MessageTypes.SequenceEqual(new[] { "module-action" }) && publicProbeServer.ModuleActionPayloads.Count == 1 && Convert.ToString(publicProbeServer.ModuleActionPayloads[0]["schemaVersion"]) == "knowledge-clipper-module-action.v4" && Directory.GetFiles(publicProbeJobs, "artifact.json", SearchOption.AllDirectories).Any(path => File.ReadAllText(path).Contains("anki-connection-status")), "Anki probe uses only public IPC 1.5 and the production VerifiedArtifactReader over a raw four-file worker artifact");
            var fakeAnkiInitializeCalls = 0;
            var needsInitializationHost = new DesktopAnkiProbeHost(fakeAnkiSettings, new DesktopModuleSettingsStore(config.WorkDirectory), AnkiProbeCapability.CreateForTest(true), delegate
            {
                return Task.FromResult(new AnkiProbeDisplayResult
                {
                    Status = "needs_initialization", ApiVersion = 6, ModelsReady = 4, ModelsTotal = 4, DecksReady = 1, DecksTotal = 7,
                    MissingDecks = new[] { "language.en", "language.zh", "life", "math", "other", "question" }, InitializationWouldWrite = true,
                    Receipt = "module-actions/fake/needs-initialization.json"
                });
            }, delegate
            {
                fakeAnkiInitializeCalls++;
                return Task.FromResult(new AnkiInitializationDisplayResult { Status = "completed", CreatedModels = 0, CreatedDecks = 6, CardWrites = 0, Deletes = 0, ExistingResourcesModified = 0, Receipt = "module-actions/fake/initialized.json" });
            });
            using (var initializeForm = new SettingsForm(config, UserSettings.Load(config), null, needsInitializationHost))
            {
                initializeForm.Show(); Pump(30); Find<ListBox>(initializeForm, SettingsAutomationCatalog.Navigation).SelectedIndex = 5; Pump(20);
                var probe = Find<Button>(initializeForm, SettingsAutomationCatalog.ExportAnkiProbe); var initialize = Find<Button>(initializeForm, SettingsAutomationCatalog.ExportAnkiInitialize);
                Assert(!initialize.Enabled, "Anki managed initialization cannot run before a typed read-only probe reports needs_initialization");
                probe.PerformClick(); WaitUntil(delegate { return probe.Enabled && initialize.Enabled; }, 1200, "typed Anki needs_initialization projection");
                var typedStatus = Find<TextBox>(initializeForm, SettingsAutomationCatalog.ExportAnkiProbeStatus).Text;
                Assert(typedStatus.Contains("需要初始化") && typedStatus.Contains("question") && typedStatus.Contains("probeWrites=0") && fakeAnkiInitializeCalls == 0, "read-only Anki probe projects missing managed routes and never initializes implicitly");
                initialize.PerformClick(); WaitUntil(delegate { return Find<TextBox>(initializeForm, SettingsAutomationCatalog.ExportAnkiInitializeStatus).Text.Contains("初始化完成"); }, 1200, "explicit managed Anki initialization projection");
                var initializedStatus = Find<TextBox>(initializeForm, SettingsAutomationCatalog.ExportAnkiInitializeStatus).Text;
                Assert(fakeAnkiInitializeCalls == 1 && initializedStatus.Contains("createdDecks=6") && initializedStatus.Contains("cardWrites=0") && initializedStatus.Contains("deletes=0") && initializedStatus.Contains("modifiedExisting=0"), "one explicit initialization click creates only missing managed resources and performs zero card writes, deletes, or existing-resource changes");
                initializeForm.Close(); Pump(20);
            }
            var publicInitJobs = Path.Combine(root, "anki-public-initialize-pipe-jobs"); Directory.CreateDirectory(publicInitJobs);
            var publicInitPipe = "KnowledgeClipper.AnkiInitialize." + Guid.NewGuid().ToString("N");
            var publicInitServer = new FakeNamedPipeOrchestratorServer(publicInitPipe, publicInitJobs, 1); publicInitServer.Start();
            var publicInitStore = new DesktopModuleSettingsStore(config.WorkDirectory);
            var publicInitSettings = publicInitStore.Load(); publicInitSettings.AnkiExpectedProfile = "账户 1"; publicInitSettings.AnkiEndpoint = "http://127.0.0.1:8765"; publicInitStore.Save(publicInitSettings);
            var publicInitHost = new DesktopAnkiProbeHost(fakeAnkiSettings, publicInitStore, AnkiProbeCapability.CreateForTest(true), publicInitJobs, new NamedPipeCommonIpcClient(publicInitPipe, 5000), true);
            var publicInitResult = publicInitHost.InitializeManagedAsync().GetAwaiter().GetResult(); publicInitServer.Complete();
            var publicInitInput = Directory.GetFiles(publicInitJobs, "input.json", SearchOption.AllDirectories).Single();
            var publicInitRequest = Directory.GetFiles(publicInitJobs, "request.json", SearchOption.AllDirectories).Single();
            var publicInitConfig = Directory.GetFiles(publicInitJobs, "anki-export.v1.json", SearchOption.AllDirectories).Single();
            using (var publicInput = JsonDocument.Parse(File.ReadAllBytes(publicInitInput)))
            using (var publicRequest = JsonDocument.Parse(File.ReadAllBytes(publicInitRequest)))
            using (var publicConfig = JsonDocument.Parse(File.ReadAllBytes(publicInitConfig)))
            {
                Assert(publicInitHost.CanInitializeManaged && publicInitResult.Completed && publicInitResult.Status == "initialized" && publicInitResult.CardWrites == 0 && publicInitResult.Deletes == 0 && publicInitResult.ExistingResourcesModified == 0, "authority-admitted Anki initialization dispatches through public IPC 1.4 and projects only verified missing-resource writes");
                Assert(publicInput.RootElement.GetProperty("schemaVersion").GetString() == "anki-managed-initialization-action.v1" && publicInput.RootElement.GetProperty("authorization").GetProperty("mode").GetString() == "explicit-user-action" && publicRequest.RootElement.GetProperty("payload").GetProperty("sideEffectAuthorization").GetProperty("scope").GetString() == "managed-models-and-decks" && publicConfig.RootElement.GetProperty("provision_managed").GetBoolean(), "Desktop creates the canonical immutable package with exact SHA-bound config and explicit authorization only after the user action");
            }
            Assert(publicInitServer.ModuleActionPayloads.Single().ContainsKey("idempotencyKey") && publicInitServer.MessageTypes.SequenceEqual(new[] { "module-action" }), "Anki managed initialization uses one public module-action message and no task command or Desktop direct provider call");
            var failedAnkiHost = new DesktopAnkiProbeHost(fakeAnkiSettings, new DesktopModuleSettingsStore(config.WorkDirectory), AnkiProbeCapability.CreateForTest(true), delegate
            {
                return Task.FromResult(new AnkiProbeDisplayResult { Status = "failed", ReasonCode = "WRONG_PROFILE", Step = "getActiveProfile", Receipt = "module-actions/fake/failed-result.json" });
            });
            using (var failedProbeForm = new SettingsForm(config, UserSettings.Load(config), null, failedAnkiHost))
            {
                failedProbeForm.Show(); Pump(30); Find<ListBox>(failedProbeForm, SettingsAutomationCatalog.Navigation).SelectedIndex = 5; Pump(20);
                var probe = Find<Button>(failedProbeForm, SettingsAutomationCatalog.ExportAnkiProbe); probe.PerformClick();
                WaitUntil(delegate { return probe.Enabled && Find<TextBox>(failedProbeForm, SettingsAutomationCatalog.ExportAnkiProbeStatus).Text.Contains("WRONG_PROFILE"); }, 1200, "Anki failure reason projection");
                var failureText = Find<TextBox>(failedProbeForm, SettingsAutomationCatalog.ExportAnkiProbeStatus).Text;
                Assert(failureText.Contains("reasonCode=WRONG_PROFILE") && failureText.Contains("step=getActiveProfile") && Find<Label>(failedProbeForm, SettingsAutomationCatalog.Status).Text.Contains("reasonCode/step"), "Anki read-only probe preserves the concrete worker reasonCode and failing step instead of collapsing them to a generic popup");
                failedProbeForm.Close(); Pump(20);
            }
            var localPackageFailureHost = new DesktopAnkiProbeHost(fakeAnkiSettings, new DesktopModuleSettingsStore(config.WorkDirectory), AnkiProbeCapability.CreateForTest(true), delegate
            {
                return Task.FromResult(new AnkiProbeDisplayResult { Status = "failed", ReasonCode = "effect.worker_exit_unknown", Step = "worker-start", Receipt = "module-actions/fake/local-package-failure.json" });
            });
            using (var localPackageFailureForm = new SettingsForm(config, UserSettings.Load(config), null, localPackageFailureHost))
            {
                localPackageFailureForm.Show(); Pump(30); Find<ListBox>(localPackageFailureForm, SettingsAutomationCatalog.Navigation).SelectedIndex = 5; Pump(20);
                var probe = Find<Button>(localPackageFailureForm, SettingsAutomationCatalog.ExportAnkiProbe); probe.PerformClick();
                WaitUntil(delegate { return probe.Enabled && Find<TextBox>(localPackageFailureForm, SettingsAutomationCatalog.ExportAnkiProbeStatus).Text.Contains("effect.worker_exit_unknown"); }, 1200, "Anki local package failure projection");
                Assert(Find<Label>(localPackageFailureForm, SettingsAutomationCatalog.Status).Text.Contains("尚未连接 Anki"), "Anki package or worker-start failure is not mislabeled as a provider connection failure");
                localPackageFailureForm.Close(); Pump(20);
            }
            var publicModuleActions = new List<string>();
            var catalogJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "speech-settings-voice-discovery-output.v1", status = "ready", provider = "edge-tts", catalogVersion = "ui-fake-1",
                catalogRevision = "edge-tts-20260830T120000Z-aaaaaaaaaaaa", catalogSha256 = new string('a', 64), updatedAtUtc = "2026-08-30T12:00:00Z",
                voices = new[]
                {
                    new { languageTag = "zh-CN", voiceId = "zh-CN-XiaoxiaoNeural", displayName = "晓晓", gender = "female", natural = true, available = true, status = "available" },
                    new { languageTag = "en-US", voiceId = "en-US-AvaMultilingualNeural", displayName = "Ava", gender = "female", natural = true, available = true, status = "available" },
                    new { languageTag = "es-ES", voiceId = "es-ES-XimenaNeural", displayName = "Ximena", gender = "female", natural = true, available = true, status = "available" },
                    new { languageTag = "ja-JP", voiceId = "ja-JP-NanamiNeural", displayName = "Nanami", gender = "female", natural = true, available = true, status = "available" },
                    new { languageTag = "ko-KR", voiceId = "ko-KR-SunHiNeural", displayName = "SunHi", gender = "female", natural = true, available = true, status = "available" }
                }
            });
            var catalogSnapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalogJson))).ToLowerInvariant();
            var fakePublicModuleHost = new DesktopModuleActionHost(root, null, delegate(string operation, object payload)
            {
                publicModuleActions.Add(operation);
                var payloadText = JsonSerializer.Serialize(payload);
                if (operation == "speech.voices")
                    return Task.FromResult(new DesktopModuleActionDisplayResult
                    {
                        Operation = operation, Status = "ready", OutputRef = "module-actions/fake-voices/output.json", OutputJson = catalogJson, CatalogJson = catalogJson,
                        CatalogSnapshotRef = "module-state/speech/voice-catalog/fake-catalog.json", CatalogSnapshotSha256 = catalogSnapshotHash
                    });
                if (operation == "speech.preview")
                {
                    Assert(payloadText.Contains("\"explicitSideEffectAuthorization\":true") && payloadText.Contains("\"action\":\"speaker.preview\"") && payloadText.Contains("speech-settings-speaker-preview-input.v1"), "Speech preview UI forwards one explicit physical-audio authorization through the public Core action payload");
                    return Task.FromResult(new DesktopModuleActionDisplayResult { Operation = operation, Status = "completed", OutputRef = "module-actions/fake-preview/output.json", OutputJson = "{\"schemaVersion\":\"speech-settings-speaker-preview-output.v1\",\"status\":\"completed\"}", PhysicalAudio = true });
                }
                if (operation == "remnote.discover-target")
                {
                    Assert(payloadText.Contains("remnote-target-discovery-request.v1") && payloadText.Contains("\"match\":\"exact\"") && payloadText.Contains("\"uniqueness\":\"required\"") && !payloadText.Contains("rootRemId"), "RemNote discovery sends only the exact-title selector through the public v2 action and never supplies a guessed ID");
                    var ambiguous = payloadText.Contains("Ambiguous Root");
                    return Task.FromResult(new DesktopModuleActionDisplayResult
                    {
                        Operation = operation, Status = ambiguous ? "blocked" : "ready", OutputRef = "module-actions/fake-remnote-discovery/output.json",
                        OutputJson = ambiguous
                            ? "{\"schemaVersion\":\"remnote-target-discovery-result.v1\",\"state\":\"blocked\",\"admission\":\"blocked\",\"selection\":{\"resolution\":\"ambiguous\",\"exactReadback\":false,\"persistableBinding\":false},\"reasonCodes\":[\"target.ambiguous\"]}"
                            : "{\"schemaVersion\":\"remnote-target-discovery-result.v1\",\"state\":\"healthy\",\"admission\":\"ready\",\"selection\":{\"resolution\":\"unique\",\"resolvedRootRemId\":\"resolved-remnote-root\",\"observedTitle\":\"剪藏\",\"exactReadback\":true,\"persistableBinding\":true},\"reasonCodes\":[]}"
                    });
                }
                var expectedProbeTarget = publicModuleActions.Count(item => item == "remnote.probe") == 1 ? "canary-root-rem-id" : "resolved-remnote-root";
                Assert(operation == "remnote.probe" && payloadText.Contains("\"operations\":[\"status\",\"search\",\"list\",\"read\"]") && payloadText.Contains("configured-destination") && payloadText.Contains(expectedProbeTarget) && !payloadText.Contains("unsaved-temporary-rem-id"), "RemNote UI dispatches the persisted or verified-pending draft target through the public read-only Core probe and ignores unverified text");
                return Task.FromResult(new DesktopModuleActionDisplayResult
                {
                    Operation = operation, Status = "ready", OutputRef = "module-actions/fake-remnote/output.json",
                    OutputJson = "{\"schemaVersion\":\"remnote-public-probe-result.v2\",\"state\":\"healthy\",\"admission\":\"ready\",\"targetBinding\":{\"matched\":true}}"
                });
            });
            using (var publicActionForm = new SettingsForm(config, UserSettings.Load(config), null, publicProbeHost, fakePublicModuleHost))
            {
                publicActionForm.Show(); Pump(30); var publicNavigation = Find<ListBox>(publicActionForm, SettingsAutomationCatalog.Navigation);
                publicNavigation.SelectedIndex = 4; Pump(20);
                var voicesButton = Find<Button>(publicActionForm, SettingsAutomationCatalog.SpeechVoiceDiscovery); var previewButton = Find<Button>(publicActionForm, SettingsAutomationCatalog.SpeechPreview);
                Assert(voicesButton.Enabled && !previewButton.Enabled, "Core v2.7 makes voice discovery available while preview waits for a hash-bound catalog");
                voicesButton.PerformClick(); WaitUntil(delegate { return previewButton.Enabled && Find<TextBox>(publicActionForm, SettingsAutomationCatalog.SpeechVoiceCatalogAvailability).Text.Contains(new string('a', 64)); }, 1200, "fake public voice catalog projection");
                Assert(publicModuleActions.Count(operation => operation == "speech.voices") == 1 && publicModuleActions.All(operation => operation != "speech.preview"), "voice discovery caches one revision/hash-bound catalog without implicit audio preview");
                previewButton.PerformClick(); WaitUntil(delegate { return previewButton.Enabled && Find<TextBox>(publicActionForm, SettingsAutomationCatalog.SpeechPreviewAvailability).Text.Contains("已自然完成"); }, 1200, "fake explicit speaker preview projection");
                Assert(publicModuleActions.Count(operation => operation == "speech.preview") == 1, "one explicit preview button click dispatches exactly one physical-audio module action");
                publicNavigation.SelectedIndex = 5; Pump(20);
                var boundTarget = Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteBoundTarget);
                Assert(boundTarget.ReadOnly && boundTarget.Text.Contains("已绑定") && boundTarget.Text.Contains("标题=剪藏") && boundTarget.Text.Contains("cana…m-id"), "RemNote settings visibly project the saved active-profile target with a masked identifier");
                var rootIdControl = Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteRootId);
                Assert(rootIdControl.ReadOnly, "RemNote root ID is read-only and can only be changed by verified target discovery");
                rootIdControl.Text = "unsaved-temporary-rem-id"; Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteRootTitle).Text = "Unsaved Temporary Root";
                var remnoteButton = Find<Button>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteProbe); Assert(remnoteButton.Enabled, "authority-admitted RemNote public probe is enabled behind a stable neutral automation ID");
                remnoteButton.PerformClick(); WaitUntil(delegate { return remnoteButton.Enabled && Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteProbeStatus).Text.Contains("就绪"); }, 1200, "fake public RemNote probe projection");
                var discoveryButton = Find<Button>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteDiscoverTarget); Assert(discoveryButton.Enabled, "Core IPC 1.3 RemNote exact-title discovery is exposed behind a stable neutral automation ID");
                Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteRootTitle).Text = "剪藏";
                discoveryButton.PerformClick(); WaitUntil(delegate { return discoveryButton.Enabled && Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteDiscoverTargetStatus).Text.Contains("唯一匹配"); }, 1200, "fake exact-title RemNote discovery projection");
                Assert(Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteRootId).Text == "resolved-remnote-root" && boundTarget.Text.Contains("verified_pending"), "a unique exact-readback discovery updates the explicit pending draft binding for the next-task save");
                remnoteButton.PerformClick(); WaitUntil(delegate { return remnoteButton.Enabled && Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteProbeStatus).Text.Contains("就绪"); }, 1200, "same-window verified-pending RemNote probe projection");
                Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteRootTitle).Text = "Ambiguous Root";
                discoveryButton.PerformClick(); WaitUntil(delegate { return discoveryButton.Enabled && Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteDiscoverTargetStatus).Text.Contains("ambiguous"); }, 1200, "ambiguous RemNote discovery projection");
                Assert(Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteRootId).Text == "resolved-remnote-root" && Find<TextBox>(publicActionForm, SettingsAutomationCatalog.ExportRemNoteDiscoverTargetStatus).Text.Contains("target.ambiguous"), "ambiguous discovery preserves the last exact binding and projects the public reasonCode");
                Assert(publicModuleActions.SequenceEqual(new[] { "speech.voices", "speech.preview", "remnote.probe", "remnote.discover-target", "remnote.probe", "remnote.discover-target" }), "settings buttons dispatch only the exact public Core module operations and no private protocol");
                publicActionForm.Close(); Pump(20);
            }
            var actionCountBeforeCacheReopen = publicModuleActions.Count;
            using (var cachedCatalogForm = new SettingsForm(config, UserSettings.Load(config), null, publicProbeHost, fakePublicModuleHost))
            {
                cachedCatalogForm.Show(); Pump(30); Find<ListBox>(cachedCatalogForm, SettingsAutomationCatalog.Navigation).SelectedIndex = 4; Pump(20);
                var cachedPreview = Find<Button>(cachedCatalogForm, SettingsAutomationCatalog.SpeechPreview);
                var cachedStatus = Find<TextBox>(cachedCatalogForm, SettingsAutomationCatalog.SpeechVoiceCatalogAvailability).Text;
                Assert(cachedPreview.Enabled && cachedStatus.Contains("cached") && cachedStatus.Contains(catalogSnapshotHash), "settings reopen verifies and restores the persisted voice catalog snapshot revision and SHA without provider access");
                Assert(publicModuleActions.Count == actionCountBeforeCacheReopen, "settings open and status projection dispatch no speech preview or discovery action");
                Find<Button>(cachedCatalogForm, SettingsAutomationCatalog.Save).PerformClick(); Pump(30);
                Console.WriteLine("SPEECH_CACHE_SAVE_OBSERVED dialog=" + cachedCatalogForm.DialogResult + " actions=" + publicModuleActions.Count);
                Assert(cachedCatalogForm.DialogResult == DialogResult.OK && publicModuleActions.Count == actionCountBeforeCacheReopen, "settings save persists next-task values without implicit voice discovery or physical playback");
            }
            var pipeJobs = Path.Combine(root, "settings-module-action-pipe-jobs"); Directory.CreateDirectory(pipeJobs);
            var modulePipe = "KnowledgeClipper.SettingsModuleAction." + Guid.NewGuid().ToString("N");
            var moduleServer = new FakeNamedPipeOrchestratorServer(modulePipe, pipeJobs, 4); moduleServer.Start();
            var realModuleHost = new DesktopModuleActionHost(pipeJobs, new NamedPipeCommonIpcClient(modulePipe, 5000), null, 17);
            var moduleVoice = realModuleHost.RunAsync("speech.voices", new { schemaVersion = "speech-settings-voice-discovery-input.v1" }).GetAwaiter().GetResult();
            var modulePreview = realModuleHost.RunAsync("speech.preview", new { schemaVersion = "speech-settings-speaker-preview-input.v1", scope = "project", effectiveAt = "preview_only", action = "speaker.preview", explicitSideEffectAuthorization = true, previewLanguage = "zh-CN", adapterInput = new { } }).GetAwaiter().GetResult();
            var moduleRemNote = realModuleHost.RunAsync("remnote.probe", new { schemaVersion = "remnote-public-probe-request.v2" }).GetAwaiter().GetResult();
            var moduleDiscovery = realModuleHost.RunAsync("remnote.discover-target", new { schemaVersion = "remnote-target-discovery-request.v1" }).GetAwaiter().GetResult();
            moduleServer.Complete();
            Assert(moduleVoice.CatalogJson != null && moduleVoice.CatalogSnapshotRef.StartsWith("module-state/speech/voice-catalog/", StringComparison.Ordinal) && modulePreview.PhysicalAudio && !moduleRemNote.PhysicalAudio && !moduleDiscovery.PhysicalAudio, "Desktop module-action host separates execution from business status and hash-verifies typed output/catalog evidence");
            Assert(moduleServer.ModuleActionPayloads.Count == 4 && moduleServer.ModuleActionPayloads.Take(3).All(value => Convert.ToString(value["schemaVersion"]) == "knowledge-clipper-module-action.v6" && Convert.ToString(value["idempotencyKey"]).Length == 64) && Convert.ToString(moduleServer.ModuleActionPayloads[3]["schemaVersion"]) == "knowledge-clipper-module-action.v3", "RemNote probe and Speech actions use typed IPC 1.7 v6 while exact-title discovery retains its admitted v3 action");
            var previewInput = Directory.GetFiles(pipeJobs, "input.json", SearchOption.AllDirectories).Select(File.ReadAllText).Single(value => value.Contains("speech-settings-speaker-preview-action.v2"));
            using (var previewDocument = JsonDocument.Parse(previewInput))
            {
                var previewRoot = previewDocument.RootElement; var authorization = previewRoot.GetProperty("authorization"); var workerInput = previewRoot.GetProperty("workerInput");
                Assert(authorization.GetProperty("mode").GetString() == "explicit-user-action" && authorization.GetProperty("actionId").GetString().StartsWith("settings-speech-preview-", StringComparison.Ordinal) && authorization.GetProperty("previewLanguage").GetString() == workerInput.GetProperty("previewLanguage").GetString() && previewRoot.GetProperty("effectClass").GetString() == "physical-audio-single-attempt" && !previewRoot.GetProperty("automaticRetryAllowed").GetBoolean(), "Speech preview binds the explicit actionId/language authorization and forbids automatic retry before Core dispatch");
            }
            Assert(moduleServer.MessageTypes.SequenceEqual(new[] { "module-action", "module-action", "module-action", "module-action" }), "voice discovery, preview, RemNote probe, and exact-title discovery use only public module-action IPC and create zero knowledge task commands");
            using (var cancelForm = new SettingsForm(config, UserSettings.Load(config)))
            {
                cancelForm.Show(); Pump(30); Find<NumericUpDown>(cancelForm, SettingsAutomationCatalog.SpeechRate).Value = 31; Find<TextBox>(cancelForm, SettingsAutomationCatalog.DeepSeekCredential).Text = "cancelled-secret"; Find<Button>(cancelForm, SettingsAutomationCatalog.Cancel).PerformClick(); Pump(20);
                Assert(cancelForm.DialogResult == DialogResult.Cancel && UserSettings.Load(config).RatePercent == 17 && DesktopCredentialSettings.Load(config.WorkDirectory).DeepSeekApiKey != "cancelled-secret", "settings cancel persists neither ordinary values nor credentials");
            }
            var protectedCredentialPath = DesktopCredentialSettings.PathFor(config.WorkDirectory); File.WriteAllText(protectedCredentialPath, "{broken-json", Encoding.UTF8);
            var credentialFallback = DesktopCredentialSettings.Load(config.WorkDirectory);
            Assert(credentialFallback.StorageStatus == "protected-current-user-backup" && credentialFallback.DeepSeekApiKey == "canary-deepseek-local-only" && credentialFallback.MineruApiToken == "canary-mineru-local-only", "corrupt credential primary falls back to the verified DPAPI-protected .bak copy");
            CredentialMigrationCanary(root);
            Assert(!Directory.GetFiles(root, "*", SearchOption.AllDirectories).Any(path => FileContains(path, "canary-deepseek-local-only") || FileContains(path, "canary-mineru-local-only")), "complete Windows UI Canary evidence tree contains zero persisted credential plaintext");
            var settingsPath = UserSettings.PathFor(config); File.Copy(settingsPath, settingsPath + ".bak", true); File.WriteAllText(settingsPath, "{broken-json", Encoding.UTF8);
            Assert(UserSettings.Load(config).RatePercent == 17, "corrupt primary settings fall back to the valid .bak copy");
            var workflowSettingsPath = Path.Combine(config.WorkDirectory, "workflow-settings.v3.json"); File.WriteAllText(workflowSettingsPath, "{broken-json", Encoding.UTF8);
            Assert(new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory).LoadOrCreate(UserSettings.Defaults()).Profiles.Count >= 1, "corrupt workflow profile settings fall back to the valid local .bak copy");
            var questionActions = new List<string>();
            using (var question = new QuestionForm(delegate(string action, SemanticActionArguments arguments)
            {
                questionActions.Add(action);
                var accepted = DesktopSemanticState.CreateDefault();
                accepted.noticeCode = action == AutomationCatalog.QuestionFollowUp ? "question.follow-up-submitted" : "question.submitted";
                accepted.foregroundState = "question-awaiting-answer";
                accepted.recentTask.available = true;
                accepted.recentTask.taskId = "task-question-ui-0001";
                accepted.questionSession.open = true;
                accepted.questionSession.answerState = "loading";
                return Task.FromResult(accepted);
            }, new FileQuestionAnswerArtifactReader(Path.Combine(root, "question-jobs"))))
            {
                var input = Find<TextBox>(question, AutomationCatalogV11.QuestionInputAutomationId);
                var answerBox = Find<TextBox>(question, AutomationCatalogV11.QuestionAnswerAutomationId);
                var answerStatus = Find<Label>(question, AutomationCatalogV11.QuestionAnswerStatusAutomationId);
                var submit = Find<Button>(question, AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionSubmit]);
                var cancel = Find<Button>(question, AutomationCatalogV11.QuestionCancelAutomationId);
                Assert(submit != null && input != null && cancel != null && !submit.Enabled, "question input fails closed while empty and exposes stable submit/cancel automation IDs");
                Assert(answerBox != null && answerBox.ReadOnly && answerBox.Multiline && answerStatus != null, "question answer uses stable neutral IDs and a read-only multiline display");
                Assert(Find<Button>(question, AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionFollowUp]) != null, "follow-up has stable automation ID");
                Assert(Find<Button>(question, AutomationCatalogV11.AutomationIds[AutomationCatalog.QuestionMakeCards]) != null, "make-cards has stable automation ID");
                question.Show(); question.Activate(); Pump(40); Assert(input.Focused, "question text input receives focus when the dialog opens");
                TypeWithRealKeyboard(input, "initialquestion"); Pump(20); Assert(submit.Enabled, "non-empty question enables submission deterministically");
                PressRealKey("{ENTER}"); WaitUntil(delegate { return questionActions.Count == 1; }, 1200, "question initial Enter dispatch");
                Assert(questionActions.SequenceEqual(new[] { AutomationCatalog.QuestionSubmit }) && !submit.Enabled, "initial Enter emits exactly one question.submit and disables duplicate submission while loading");
                PressRealKey("{ENTER}"); Pump(80);
                Assert(questionActions.Count == 1, "question Enter cannot duplicate an in-flight initial submission");

                var projected = DesktopSemanticState.CreateDefault(); projected.questionSession.open = true; projected.questionSession.answerState = "loading"; projected.foregroundState = "question-awaiting-answer"; question.Update(projected);
                Assert(answerBox.Text == "" && answerStatus.AccessibleDescription == "loading" && answerStatus.Text.Contains("正在生成"), "loading is explicit and does not fabricate answer content");
                projected.foregroundState = "question-composing"; projected.questionSession.answerState = "waiting_input"; question.Update(projected);
                Assert(answerBox.Text == "" && answerStatus.AccessibleDescription == "waiting_input", "waiting_input is explicit and keeps the answer empty");
                projected.questionSession.answerState = "failed"; question.Update(projected);
                Assert(answerBox.Text == "" && answerStatus.AccessibleDescription == "failed" && answerStatus.Text.Contains("失败"), "failed is explicit and keeps the answer empty");

                projected.recentTask.available = true; projected.recentTask.taskId = "task-question-ui-0001"; projected.questionSession.answerState = "ready";
                projected.questionSession.answerArtifact = WriteQuestionAnswerFixture(Path.Combine(root, "question-jobs"), projected.recentTask.taskId, "question.answer.text.ui.0001", "第一版经过校验的答案。"); question.Update(projected);
                Assert(answerBox.Text == "第一版经过校验的答案。" && answerStatus.AccessibleDescription == "ready", "verified question.answer bytes are displayed unchanged");
                input.Focus(); TypeWithRealKeyboard(input, "followupquestion"); PressRealKey("{ENTER}"); WaitUntil(delegate { return questionActions.Count == 2; }, 1200, "question follow-up Enter dispatch");
                Assert(questionActions.SequenceEqual(new[] { AutomationCatalog.QuestionSubmit, AutomationCatalog.QuestionFollowUp }), "answer-ready Enter emits exactly one question.follow-up and never creates a second initial question");
                projected.questionSession.answerState = "ready"; projected.questionSession.answerArtifact = WriteQuestionAnswerFixture(Path.Combine(root, "question-jobs"), projected.recentTask.taskId, "question.answer.text.ui.0001b", "第一版经过校验的答案。"); question.Update(projected);
                projected.questionSession.answerArtifact = WriteQuestionAnswerFixture(Path.Combine(root, "question-jobs"), projected.recentTask.taskId, "question.answer.text.ui.0002", "追问后的第二版答案。"); question.Update(projected);
                Assert(answerBox.Text == "追问后的第二版答案。" && !answerBox.Text.Contains("第一版"), "new initial or follow-up answer replaces the current display without merging history");
                projected.questionSession.answerArtifact = projected.questionSession.answerArtifact.Copy(); projected.questionSession.answerArtifact.expectedSha256 = new string('0', 64); question.Update(projected);
                Assert(answerBox.Text == "" && answerStatus.AccessibleDescription == "failed" && answerStatus.Text.Contains("manifest-hash-mismatch"), "hash mismatch fails closed and clears previously displayed answer");
                var actionsBeforeCancel = questionActions.Count; cancel.PerformClick(); Pump(20); Assert(questionActions.Count == actionsBeforeCancel, "question dialog cancel closes locally without sending input or another command");
            }
            using (var languagePractice = new LanguagePracticeForm())
            {
                var meaning = Find<Button>(languagePractice, AutomationCatalogV11.LanguagePracticeMeaningReadingAutomationId);
                var writing = Find<Button>(languagePractice, AutomationCatalogV11.LanguagePracticeWritingAutomationId);
                var both = Find<Button>(languagePractice, AutomationCatalogV11.LanguagePracticeBothAutomationId);
                var cancel = Find<Button>(languagePractice, AutomationCatalogV11.LanguagePracticeCancelAutomationId);
                Assert(meaning != null && writing != null && both != null && cancel != null, "language-practice dialog exposes three fixed semantic choices and local cancel IDs");
            }
            Assert(
                SelectLanguagePracticeModally(AutomationCatalogV11.LanguagePracticeMeaningReadingAutomationId) == AutomationCatalogV11.LanguagePracticeMeaningReading &&
                SelectLanguagePracticeModally(AutomationCatalogV11.LanguagePracticeWritingAutomationId) == AutomationCatalogV11.LanguagePracticeWriting &&
                SelectLanguagePracticeModally(AutomationCatalogV11.LanguagePracticeBothAutomationId) == AutomationCatalogV11.LanguagePracticeBoth,
                "modal language-practice buttons return meaning-reading, writing and both unchanged");
            var resolutionView = new OcrHumanResolutionView { OriginalText = "OCR original source", CandidateText = "AI cleaned candidate", MarkdownText = "# verified markdown", StructuredText = "{\"verified\":true}", DifferenceText = "original -> candidate", UnresolvedReason = "formula token confidence is below the release threshold" };
            using (var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), ClientSize = new Size(320, 180) })
            using (var resolution = new OcrHumanResolutionForm(resolutionView))
            {
                owner.Show(); owner.Activate(); resolution.Show(owner); resolution.Activate(); Pump(60);
                var original = Find<TextBox>(resolution, OcrHumanResolutionAutomationCatalog.OriginalText);
                var candidate = Find<TextBox>(resolution, OcrHumanResolutionAutomationCatalog.CandidateText);
                var markdown = Find<TextBox>(resolution, OcrHumanResolutionAutomationCatalog.Markdown);
                var structured = Find<TextBox>(resolution, OcrHumanResolutionAutomationCatalog.Structured);
                var difference = Find<TextBox>(resolution, OcrHumanResolutionAutomationCatalog.Difference);
                var reason = Find<Label>(resolution, OcrHumanResolutionAutomationCatalog.Reason);
                var edit = Find<TextBox>(resolution, OcrHumanResolutionAutomationCatalog.EditText);
                Assert(resolution.AutoScaleMode == AutoScaleMode.Dpi && resolution.Owner == owner && resolution.TopMost && !resolution.ShowInTaskbar, "OCR resolution dialog is DPI-aware and stays in the owned modal layer");
                Assert(original.ReadOnly && candidate.ReadOnly && markdown.ReadOnly && structured.ReadOnly && difference.ReadOnly && original.Text == resolutionView.OriginalText && candidate.Text == resolutionView.CandidateText && markdown.Text == resolutionView.MarkdownText && structured.Text == resolutionView.StructuredText && difference.Text == resolutionView.DifferenceText && reason.Text.Contains(resolutionView.UnresolvedReason), "OCR resolution dialog displays hash-verified original, AI candidate, Markdown, structured result, diff and unresolved reason without fabricating content");
                edit.Clear(); resolution.Activate(); ClickWithRealMouse(edit);
                WaitUntil(delegate { return edit.Focused && GetForegroundWindow() == resolution.Handle; }, 1200, "OCR correction editor receives real mouse focus");
                edit.SelectionStart = 0; TypeWithRealKeyboard(edit, "manual correction");
                Assert(edit.Text == "manual correction" && edit.Focused, "OCR resolution edit box accepts actual keyboard input");
                Find<Button>(resolution, OcrHumanResolutionAutomationCatalog.UseEdited).PerformClick(); Pump(20);
                Assert(resolution.DialogResult == DialogResult.OK && resolution.Selection.Choice == "use-edited" && resolution.Selection.Text == "manual correction", "OCR edited continuation returns the exact user text for later durable-command binding");
                owner.Close();
            }
            using (var resolution = new OcrHumanResolutionForm(resolutionView))
            {
                resolution.Show(); Pump(30); Find<Button>(resolution, OcrHumanResolutionAutomationCatalog.UseOriginal).PerformClick(); Pump(20);
                Assert(resolution.DialogResult == DialogResult.OK && resolution.Selection.Choice == "use-original" && resolution.Selection.Text == resolutionView.OriginalText, "OCR use-original continuation returns the immutable original text unchanged");
            }
            using (var resolution = new OcrHumanResolutionForm(resolutionView))
            {
                resolution.Show(); Pump(30); Find<Button>(resolution, OcrHumanResolutionAutomationCatalog.Cancel).PerformClick(); Pump(20);
                Assert(resolution.DialogResult == DialogResult.OK && resolution.Selection != null && resolution.Selection.Choice == "cancel", "OCR resolution cancel returns the public durable cancel selection instead of faking a local-only exit");
            }
        }

        private static void AuthorityBindingProjectionCanary(string root)
        {
            var fixtureRoot = Path.Combine(root, "authority-binding-v1237");
            var metadataRoot = Path.Combine(fixtureRoot, "authority", "metadata");
            var releaseRoot = Path.Combine(fixtureRoot, "authority", "win-x64");
            Directory.CreateDirectory(metadataRoot); Directory.CreateDirectory(releaseRoot);
            File.WriteAllBytes(Path.Combine(releaseRoot, "orchestrator.exe"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(metadataRoot, "ipc.json"), "{}");
            var registryPath = Path.Combine(metadataRoot, "worker-registry.v1.json");
            File.WriteAllText(registryPath, "{\"registryVersion\":\"1.2.37\"}");
            var registrySha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(registryPath)));
            var treeSha = new string('A', 64);
            var checkpointId = "knowledge-clipper-authority-1.2.37-20260901T063200Z";
            File.WriteAllText(Path.Combine(metadataRoot, "checkpoint.json"), JsonSerializer.Serialize(new
            {
                checkpointId,
                admissionStatus = "authority-admitted",
                registry = new { registryVersion = "1.2.37", sha256 = registrySha },
                release = new { treeSha256 = treeSha }
            }));
            File.WriteAllText(Path.Combine(fixtureRoot, "candidate-integration.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = "knowledge-clipper-candidate-integration.v1", AutoStart = true,
                OrchestratorExecutable = "authority/win-x64/orchestrator.exe", IpcConfig = "authority/metadata/ipc.json",
                Registry = "authority/metadata/worker-registry.v1.json", ReleaseRoot = "authority/win-x64",
                JobsRoot = "%LOCALAPPDATA%\\KnowledgeClipper\\jobs", PipeName = "KnowledgeClipper.AuthorityProjection.Canary", MaxActiveTasks = 4,
                AuthorityCheckpointId = checkpointId, AuthorityTreeSha256 = treeSha
            }));

            var positive = DesktopAuthorityBindingProjection.Load(fixtureRoot);
            Assert(positive.IsBound && positive.Version == "1.2.37" && positive.DisplayVersion == "authority v1.2.37", "candidate-bound read-only status projects authority v1.2.37 from matching checkpoint, registry, and release tree metadata");
            var missing = DesktopAuthorityBindingProjection.Load(Path.Combine(root, "authority-binding-missing"));
            Assert(!missing.IsBound && missing.Version == "unknown" && missing.StatusCode == "candidate-integration-missing", "missing candidate authority metadata fails closed as unknown");
            File.WriteAllText(Path.Combine(metadataRoot, "checkpoint.json"), JsonSerializer.Serialize(new
            {
                checkpointId,
                admissionStatus = "authority-admitted",
                registry = new { registryVersion = "1.2.37", sha256 = registrySha },
                release = new { treeSha256 = new string('B', 64) }
            }));
            var mismatched = DesktopAuthorityBindingProjection.Load(fixtureRoot);
            Assert(!mismatched.IsBound && mismatched.Version == "unknown" && mismatched.StatusCode == "release-tree-mismatch", "inconsistent authority tree metadata fails closed instead of showing a stale or guessed version");
        }

        private static void NamedPipeUiCanary(string root)
        {
            var jobs = Path.Combine(root, "pipe-jobs"); Directory.CreateDirectory(jobs);
            var staged = Path.Combine(root, "pipe-selected.txt"); var bytes = new System.Text.UTF8Encoding(false).GetBytes("named pipe ui canary"); File.WriteAllBytes(staged, bytes);
            var artifact = WindowsDesktopCaptureProvider.Artifact("capture.text.pipe-ui", "inputs/capture/pipe-selected.txt", bytes, "text/plain", "utf-8", staged);
            var capture = new CaptureResult { hasContent = true, source = "selection", selectedTextArtifactRef = artifact.artifactRef, contextAvailable = false, artifacts = new List<CaptureArtifact> { artifact } };
            var pipeName = "RqcDesktopUiFake." + Guid.NewGuid().ToString("N");
            var server = new FakeNamedPipeOrchestratorServer(pipeName, jobs, 2); server.Start();
            var clock = new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));
            var source = new FileDesktopCommonCommandSource(jobs, clock, new UiCommonIdProvider());
            var controller = new DesktopController(new FakeCaptureProvider(new[] { capture }), new FakeScreenshotProvider(null), new NamedPipeDesktopOrchestratorPort(new NamedPipeCommonIpcClient(pipeName, 5000), source), clock, new FakeIdProvider());
            using (var form = new BoundClipperForm(Config(root), controller, false))
            {
                form.Show(); Pump(40); Find<Button>(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ClipCapture]).PerformClick();
                WaitUntil(delegate { return controller.Snapshot().recentTask.available; }, 1800, "UI submission through real named-pipe client");
                Assert(server.MessageTypes.Count == 1 && server.CommandTypes[0] == "task.submit", "real UI emits frozen task.submit envelope through fake pipe server");
                var status = Find<Button>(form, AutomationCatalogV11.StatusAutomationId);
                WaitUntil(delegate { return status.Text == ""; }, 1800, "pipe submission UI returns to idle before the next action"); Pump(80);
                var stop = form.ContextMenuStrip.Items.Find(AutomationCatalogV11.AutomationIds[AutomationCatalog.PlaybackStop], true)[0] as ToolStripMenuItem;
                stop.PerformClick(); WaitUntil(delegate { return controller.Snapshot().playback == "stop-requested"; }, 1200, "playback stop maps the accepted direct stage-control receipt to stop-requested");
                server.Complete();
                Assert(status.Text != "已停止" && controller.Snapshot().noticeCode == "playback.stop-requested", "UI reports only stop requested and never claims playback already stopped");
                Assert(server.MessageTypes.SequenceEqual(new[] { "task-command", "stage-control" }) && server.StageControlPayloads.Count == 1, "playback stop emits one protocol 1.1 stage-control request and no task.cancel");
                Assert(Directory.GetFiles(jobs, "pipe-selected.txt", SearchOption.AllDirectories).Length == 1, "staged Windows capture bytes are materialized into immutable task package");
                var bundleJson = File.ReadAllText(Directory.GetFiles(jobs, "capture-bundle.v1.json", SearchOption.AllDirectories)[0]);
                Assert(!bundleJson.Contains("sourcePath"), "net8 serializer excludes desktop-only staging metadata from capture contract");
                form.Close(); Pump(30);
            }
        }

        private static void NamedPipeInlineQuestionCanary(string root)
        {
            var jobs = Path.Combine(root, "inline-pipe-jobs"); Directory.CreateDirectory(jobs);
            var pipeName = "KnowledgeClipperInlineQuestion." + Guid.NewGuid().ToString("N");
            var server = new FakeNamedPipeOrchestratorServer(pipeName, jobs, 1); server.Start();
            var clock = new FakeClock(new DateTime(2026, 8, 26, 6, 0, 0, DateTimeKind.Utc));
            var source = new FileDesktopCommonCommandSource(jobs, clock, new UiCommonIdProvider());
            var controller = new DesktopController(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), new NamedPipeDesktopOrchestratorPort(new NamedPipeCommonIpcClient(pipeName, 5000), source), clock, new FakeIdProvider());
            using (var form = new BoundClipperForm(Config(root), controller, false))
            {
                form.Show(); Pump(60);
                var input = Find<TextBox>(form, AutomationCatalogV12.InlineQuestionInputAutomationId);
                ClickWithRealMouse(input); WaitForInlineEditing(form, input, "named-pipe second-window inline input focus without auxiliary foreground form");
                QuestionForm pipeAnswer = null;
                using (var closeAnswer = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    closeAnswer.Tick += delegate
                    {
                        pipeAnswer = Application.OpenForms.OfType<QuestionForm>().FirstOrDefault();
                        if (pipeAnswer == null) return;
                        var cancel = Find<Button>(pipeAnswer, AutomationCatalogV11.QuestionCancelAutomationId);
                        if (cancel == null) return;
                        closeAnswer.Stop(); cancel.PerformClick();
                    };
                    TypeWithRealKeyboard(input, "pipequestion"); closeAnswer.Start(); PressRealKey("{ENTER}");
                    WaitUntil(delegate { return pipeAnswer != null && !pipeAnswer.Visible && controller.Snapshot().recentTask.available && input.Text == "" && controller.Snapshot().foregroundState == "idle"; }, 2600, "named-pipe inline question submission and visible answer workflow");
                }
                server.Complete();
                Assert(server.MessageTypes.SequenceEqual(new[] { "task-command" }) && server.QuerySchemaVersions.Count == 0 && server.CommandTypes.SequenceEqual(new[] { "task.submit" }), "inline question creates a new local session and emits exactly one frozen task.submit without adopting or querying an unrelated recent task");
                var manifests = Directory.GetFiles(jobs, "task.manifest.v1.json", SearchOption.AllDirectories);
                Assert(manifests.Length == 1 && File.ReadAllText(manifests[0]).Contains("\"intent\":\"clip.question\""), "inline Named Pipe submission creates one immutable question task package without a second business path");
                Assert(pipeAnswer != null && (GetWindowLong(form.Handle, -20) & 0x08000000) != 0 && controller.Snapshot().foregroundState == "idle" && controller.Snapshot().questionSession.open, "inline Named Pipe completion visibly opens the answer workflow, restores no-activate and leaves other desktop actions available without losing task association");
                form.Close(); Pump(30);
            }
        }

        private static void CredentialMigrationCanary(string root)
        {
            var migrationRoot = Path.Combine(root, "credential-legacy-migration"); Directory.CreateDirectory(migrationRoot);
            var legacyDeepSeek = "legacy-dpapi-deepseek-value"; var legacyMineru = "legacy-dpapi-mineru-value";
            var legacyPath = DesktopCredentialSettings.PathFor(migrationRoot);
            File.WriteAllText(legacyPath, "{\"DeepSeekApiKey\":\"" + legacyDeepSeek + "\",\"MineruApiToken\":\"" + legacyMineru + "\"}", new UTF8Encoding(false));
            var migrated = DesktopCredentialSettings.Load(migrationRoot);
            var migratedDisk = File.ReadAllText(legacyPath) + File.ReadAllText(legacyPath + ".bak") + File.ReadAllText(DesktopCredentialSettings.MigrationMarkerPathFor(migrationRoot));
            Assert(migrated.StorageStatus == "migrated-to-dpapi-current-user" && migrated.DeepSeekApiKey == legacyDeepSeek && migrated.MineruApiToken == legacyMineru, "legacy plaintext credentials migrate once and read back exactly through DPAPI CurrentUser");
            Assert(!migratedDisk.Contains(legacyDeepSeek) && !migratedDisk.Contains(legacyMineru) && migratedDisk.Contains("migrated-to-dpapi-current-user") && migratedDisk.Contains("\"plaintextRetained\":false"), "successful migration replaces plaintext primary/backup and writes a secret-free migration marker");

            var failureRoot = Path.Combine(root, "credential-migration-failure"); Directory.CreateDirectory(failureRoot);
            var failurePath = DesktopCredentialSettings.PathFor(failureRoot); var failureValue = "legacy-migration-must-remain-on-failure";
            File.WriteAllText(failurePath, "{\"DeepSeekApiKey\":\"" + failureValue + "\"}", new UTF8Encoding(false));
            try
            {
                DesktopCredentialSettings.MigrationTestHook = delegate(string stage) { if (stage == "before-commit") throw new CredentialStoreException("injected-migration-failure"); };
                var failed = DesktopCredentialSettings.Load(failureRoot);
                Assert(failed.StorageStatus == "migration-failed-closed" && failed.DeepSeekApiKey == null && failed.MineruApiToken == null && File.ReadAllText(failurePath).Contains(failureValue), "migration failure fails closed, injects no credential, and preserves the unconfirmed legacy source");
            }
            finally
            {
                DesktopCredentialSettings.MigrationTestHook = null;
                try { Directory.Delete(failureRoot, true); } catch { }
            }
            Assert(!Directory.GetFiles(migrationRoot, "*", SearchOption.AllDirectories).Any(path => FileContains(path, legacyDeepSeek) || FileContains(path, legacyMineru)), "post-migration disk secret scan reports zero plaintext across primary, backup, and marker");
        }

        private static bool FileContains(string path, string value)
        {
            try { return File.ReadAllText(path, Encoding.UTF8).Contains(value); }
            catch { return false; }
        }

        private static void SystemScreenshotAndWindowsBoundaryCanary(string root)
        {
            var shortcut = new RecordingScreenshotShortcutSender();
            var provider = new WindowsScreenshotProvider(root, new ScriptedScreenshotClipboard(), shortcut, new NeverCancelScreenshotProbe(), 1000, 1);
            var captured = provider.Capture("question");
            Assert(shortcut.RequestCount == 1, "screenshot provider invokes the Windows system screenshot shortcut exactly once");
            Assert(captured.outcome == "captured" && captured.artifact != null && File.Exists(captured.artifact.sourcePath), "system screenshot clipboard image is staged as an immutable artifact");
            var cancelled = new WindowsScreenshotProvider(root, new FixedClipboard(new ClipboardSnapshot()), new RecordingScreenshotShortcutSender(), new ImmediateCancelScreenshotProbe(), 1000, 1).Capture("question");
            Assert(cancelled.outcome == "cancelled" && cancelled.noticeCode == "screenshot.frame-cancelled", "Escape cancellation returns without a custom screenshot overlay");
            Assert(typeof(WindowsScreenshotProvider).Assembly.GetType("KnowledgeClipper.ScreenshotOverlayForm") == null, "production desktop contains no custom screenshot overlay type");
            using (var host = new Form())
            {
                var handle = host.Handle;
                Assert(GlobalHotkeyCanary.RegisterAndRelease(handle), "Windows global-hotkey registration and release boundary");
            }
            Assert(Screen.AllScreens.Length >= 1 && Screen.FromPoint(SystemInformation.VirtualScreen.Location) != null, "Windows monitor topology is queryable");
        }

        private static AppConfig Config(string root)
        {
            return new AppConfig { WorkDirectory = root, EdgeTtsPath = "missing-edge-tts.exe", FfplayPath = "missing-ffplay.exe", TargetDocumentTitle = "剪藏", DateFormat = "yyyy-MM-dd", DeepSeekMaxRetries = 3 };
        }

        private static QuestionAnswerArtifactReference WriteQuestionAnswerFixture(string jobsRoot, string taskId, string artifactRef, string text)
        {
            var suffix = artifactRef.Substring(artifactRef.LastIndexOf('.') + 1);
            var payloadRelative = "runs/r1/outputs/question-answer/" + suffix + "/answer.md";
            var manifestRelative = "runs/r1/outputs/question-answer/" + suffix + "/artifact.json";
            var job = Path.Combine(jobsRoot, taskId); var payloadPath = Path.Combine(job, payloadRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath));
            var payload = new UTF8Encoding(false).GetBytes(text); File.WriteAllBytes(payloadPath, payload);
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var ancestryArtifactRef = "question.answer.structured." + suffix;
            var ancestryPayloadRelative = "runs/r1/outputs/question-answer/" + suffix + "/structured.json";
            var ancestryManifestRelative = "runs/r1/outputs/question-answer/" + suffix + "/structured-artifact.json";
            var ancestryPayload = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "question.answer.v1", answer = "structured" });
            File.WriteAllBytes(Path.Combine(job, ancestryPayloadRelative.Replace('/', Path.DirectorySeparatorChar)), ancestryPayload);
            var ancestryManifest = new
            {
                schemaVersion = "1.0.0", artifactId = ancestryArtifactRef, kind = "question.answer", contractVersion = "1.0.0", mediaType = "application/json",
                files = new[] { new { path = ancestryPayloadRelative, sha256 = Convert.ToHexString(SHA256.HashData(ancestryPayload)).ToLowerInvariant(), bytes = ancestryPayload.LongLength, mediaType = "application/json" } },
                createdAt = "2026-08-26T00:00:00Z", producer = new { stageId = "question-answer", workerId = "content.fake", workerVersion = "1.0.0", attemptId = suffix },
                inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = ancestryPayloadRelative }
            };
            var ancestryManifestBytes = JsonSerializer.SerializeToUtf8Bytes(ancestryManifest);
            File.WriteAllBytes(Path.Combine(job, ancestryManifestRelative.Replace('/', Path.DirectorySeparatorChar)), ancestryManifestBytes);
            var manifest = new
            {
                schemaVersion = "1.0.0", artifactId = artifactRef, kind = "question.answer.text", contractVersion = "1.0.0", mediaType = "text/plain",
                files = new[] { new { path = payloadRelative, sha256 = payloadHash, bytes = payload.LongLength, mediaType = "text/plain" } },
                createdAt = "2026-08-26T00:00:00Z", producer = new { stageId = "question-answer-text", workerId = "core.fake", workerVersion = "1.0.0", attemptId = suffix },
                inputRefs = new[] { new { artifactRef = ancestryArtifactRef, kind = "question.answer", contractVersion = "1.0.0", manifestPath = ancestryManifestRelative, expectedSha256 = Convert.ToHexString(SHA256.HashData(ancestryManifestBytes)).ToLowerInvariant() } }, dataClass = "personal",
                metadata = new { entrypoint = payloadRelative }
            };
            var manifestBytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(manifest));
            File.WriteAllBytes(Path.Combine(job, manifestRelative.Replace('/', Path.DirectorySeparatorChar)), manifestBytes);
            return new QuestionAnswerArtifactReference { artifactRef = artifactRef, kind = "question.answer.text", contractVersion = "1.0.0", manifestPath = manifestRelative, expectedSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant() };
        }

        private static CaptureResult TextCapture(string suffix)
        {
            var id = "capture.text." + suffix;
            return new CaptureResult { hasContent = true, source = "selection", selectedTextArtifactRef = id, contextAvailable = false, artifacts = new List<CaptureArtifact> { new CaptureArtifact { artifactRef = id, relativePath = "inputs/" + suffix + ".txt", sha256 = new string('0', 64), bytes = 1, mediaType = "text/plain", encoding = "utf-8" } } };
        }

        private static ScreenshotResult ImageFrame(string suffix)
        {
            return new ScreenshotResult { outcome = "captured", noticeCode = "screenshot.frame-captured", artifact = new CaptureArtifact { artifactRef = "capture.image." + suffix, relativePath = "inputs/" + suffix + ".png", sha256 = new string('1', 64), bytes = 1, mediaType = "image/png" } };
        }

        private static T Find<T>(Control root, string id) where T : Control
        {
            var values = root.Controls.Find(id, true); return values.Length == 0 ? null : values[0] as T;
        }

        private static T WaitForForm<T>(int timeout) where T : Form
        {
            var deadline = Environment.TickCount + timeout;
            while (Environment.TickCount < deadline)
            {
                Application.DoEvents(); foreach (Form item in Application.OpenForms) if (item is T) return (T)item; Thread.Sleep(15);
            }
            return null;
        }

        private static void WaitUntil(Func<bool> condition, int timeout, string name)
        {
            var deadline = Environment.TickCount + timeout;
            while (Environment.TickCount < deadline) { Application.DoEvents(); if (condition()) return; Thread.Sleep(15); }
            throw new InvalidOperationException("Timed out: " + name);
        }

        private static void Pump(int milliseconds) { var deadline = Environment.TickCount + milliseconds; while (Environment.TickCount < deadline) { Application.DoEvents(); Thread.Sleep(10); } }

        private static void ClickWithRealMouse(Control control)
        {
            var previous = Cursor.Position;
            var target = control.PointToScreen(new Point(Math.Max(4, control.ClientSize.Width / 2), Math.Max(4, control.ClientSize.Height / 2)));
            try
            {
                SetCursorPos(target.X, target.Y); Pump(30);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); Pump(80);
            }
            finally { SetCursorPos(previous.X, previous.Y); }
        }

        private static void DragResizeWithRealMouse(Form form, Point clientStart, Point delta)
        {
            var previous = Cursor.Position;
            var start = form.PointToScreen(clientStart);
            var finish = new Point(start.X + delta.X, start.Y + delta.Y);
            var release = Task.Run(delegate
            {
                Thread.Sleep(180);
                SetCursorPos(finish.X, finish.Y);
                mouse_event(0x0001, unchecked((uint)delta.X), unchecked((uint)delta.Y), 0, UIntPtr.Zero);
                Thread.Sleep(120);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            });
            try
            {
                SetCursorPos(start.X, start.Y); Pump(40);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                var deadline = Environment.TickCount + 1400;
                while (!release.IsCompleted && Environment.TickCount < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                release.GetAwaiter().GetResult(); Pump(120);
            }
            finally
            {
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(previous.X, previous.Y);
            }
        }

        private static void TypeWithRealKeyboard(TextBox input, string text)
        {
            var expected = input.Text;
            var form = input.FindForm();
            foreach (var character in text)
            {
                if (form == null || !input.Focused || GetForegroundWindow() != form.Handle || (GetWindowLong(form.Handle, -20) & 0x08000000) != 0)
                    throw new InvalidOperationException("Inline keyboard focus became unstable before character " + (expected.Length + 1));
                SendUnicodeCharacter(character);
                expected += character;
                var prefix = expected;
                WaitUntil(delegate { return input.Text == prefix; }, 1000, "keyboard prefix " + prefix.Length);
                if (!input.Focused || GetForegroundWindow() != form.Handle || (GetWindowLong(form.Handle, -20) & 0x08000000) != 0)
                    throw new InvalidOperationException("Inline keyboard focus became unstable after character " + expected.Length);
                Console.WriteLine("INLINE_KEY_PREFIX length=" + expected.Length + " foreground=stable noActivate=false focused=true");
            }
        }

        private static void WaitForInlineEditing(Form form, TextBox input, string name)
        {
            WaitUntil(delegate { return input.Focused && GetForegroundWindow() == form.Handle && (GetWindowLong(form.Handle, -20) & 0x08000000) == 0; }, 1200, name);
        }

        private static void PressRealKey(string key)
        {
            var virtualKey = key == "{ENTER}" ? (ushort)Keys.Enter : key == "{ESC}" ? (ushort)Keys.Escape : throw new ArgumentOutOfRangeException("key");
            var inputs = new[]
            {
                KeyboardInput(virtualKey, '\0', 0),
                KeyboardInput(virtualKey, '\0', 0x0002)
            };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) != (uint)inputs.Length) throw new InvalidOperationException("SendInput virtual key injection failed");
            Application.DoEvents();
        }

        private static void SendUnicodeCharacter(char character)
        {
            var inputs = new[]
            {
                KeyboardInput(0, character, 0x0004),
                KeyboardInput(0, character, 0x0004 | 0x0002)
            };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) != (uint)inputs.Length) throw new InvalidOperationException("SendInput Unicode injection failed");
            Application.DoEvents();
        }

        private static INPUT KeyboardInput(ushort virtualKey, char scan, uint flags)
        {
            return new INPUT { type = 1, value = new InputUnion { keyboard = new KEYBDINPUT { virtualKey = virtualKey, scanCode = scan, flags = flags, time = 0, extraInfo = UIntPtr.Zero } } };
        }

        private static string SelectLanguagePracticeModally(string automationId)
        {
            using (var form = new LanguagePracticeForm())
            using (var click = new System.Windows.Forms.Timer { Interval = 20 })
            {
                click.Tick += delegate
                {
                    var button = Find<Button>(form, automationId);
                    if (button == null || !button.CanSelect) return;
                    click.Stop(); button.PerformClick();
                };
                click.Start();
                var result = form.ShowDialog();
                Assert(result == DialogResult.OK, "modal language-practice choice closes with OK: " + automationId);
                return form.Selection;
            }
        }

        private static void Assert(bool value, string name) { if (!value) throw new InvalidOperationException("Assertion failed: " + name); assertions++; Console.WriteLine("PASS " + name); }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { internal uint type; internal InputUnion value; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] internal MOUSEINPUT mouse;
            [FieldOffset(0)] internal KEYBDINPUT keyboard;
            [FieldOffset(0)] internal HARDWAREINPUT hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { internal int dx, dy; internal uint mouseData, flags, time; internal UIntPtr extraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { internal ushort virtualKey, scanCode; internal uint flags, time; internal UIntPtr extraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { internal uint message; internal ushort parameterLow, parameterHigh; }

        private sealed class FixedClipboard : IWindowsClipboard
        {
            private readonly ClipboardSnapshot value;
            internal FixedClipboard(ClipboardSnapshot value) { this.value = value; }
            public uint SequenceNumber() { return 7; }
            public ClipboardSnapshot Read() { return new ClipboardSnapshot { Text = value.Text, Image = value.Image == null ? null : (Image)value.Image.Clone() }; }
        }

        private sealed class FailedCopySender : ICopyShortcutSender
        {
            public bool TrySend(out string error) { error = "canary.injected-copy-failure"; return false; }
        }

        private sealed class SuccessfulCopySender : ICopyShortcutSender
        {
            public bool TrySend(out string error) { error = null; return true; }
        }

        private sealed class ScriptedTextClipboard : IWindowsClipboard
        {
            private readonly string before, after; private int sequenceReads, valueReads;
            internal ScriptedTextClipboard(string before, string after) { this.before = before; this.after = after; }
            public uint SequenceNumber() { sequenceReads++; return sequenceReads == 1 ? 20u : 21u; }
            public ClipboardSnapshot Read() { valueReads++; return new ClipboardSnapshot { Text = valueReads == 1 ? before : after }; }
        }

        private sealed class FixedContextProvider : IWindowsContextProvider
        {
            private readonly string prefix, suffix;
            internal FixedContextProvider(string prefix, string suffix) { this.prefix = prefix; this.suffix = suffix; }
            public bool TryRead(string selectedText, string granularity, out WindowsContextSnapshot context) { context = new WindowsContextSnapshot { Prefix = prefix, Suffix = suffix }; return true; }
        }

        private sealed class ScriptedScreenshotClipboard : IWindowsClipboard
        {
            private int sequenceReads;
            public uint SequenceNumber() { sequenceReads++; return sequenceReads == 1 ? 10u : 11u; }
            public ClipboardSnapshot Read() { return new ClipboardSnapshot { Image = new Bitmap(6, 4) }; }
        }

        private sealed class RecordingScreenshotShortcutSender : IScreenshotShortcutSender
        {
            internal int RequestCount;
            public bool TrySend(out string error) { RequestCount++; error = null; return true; }
        }

        private sealed class NeverCancelScreenshotProbe : IScreenshotCancelProbe
        {
            public bool IsCancellationRequested() { return false; }
        }

        private sealed class ImmediateCancelScreenshotProbe : IScreenshotCancelProbe
        {
            public bool IsCancellationRequested() { return true; }
        }

        private sealed class UiCommonIdProvider : ICommonIdProvider
        {
            private int next;
            public string NewId(string prefix) { next++; return prefix + "-ui-" + next.ToString("D4"); }
        }
    }
}

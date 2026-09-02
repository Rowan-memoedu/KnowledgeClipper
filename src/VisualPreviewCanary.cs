using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal static class VisualPreviewCanary
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            var output = Path.GetFullPath(args[0]); if (Directory.Exists(output)) return 3; Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var config = new AppConfig { WorkDirectory = Path.Combine(output, "data"), EdgeTtsPath = "missing", FfplayPath = "missing", TargetDocumentTitle = "剪藏", DateFormat = "yyyy-MM-dd", DeepSeekMaxRetries = 3 };
            Directory.CreateDirectory(config.WorkDirectory);
            var captures = new FakeCaptureProvider(new[] { TextCapture("clip"), TextCapture("read") });
            var controller = new DesktopController(captures, new FakeScreenshotProvider(null), new FakeDesktopOrchestratorPort(), new FakeClock(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)), new FakeIdProvider());
            Exception failure = null;
            using (var form = new BoundClipperForm(config, controller, false))
            {
                form.Shown += async delegate
                {
                    try
                    {
                        var status = Find(form, AutomationCatalogV11.StatusAutomationId);
                        var inlineInput = form.Controls.Find(AutomationCatalogV12.InlineQuestionInputAutomationId, true)[0] as TextBox;
                        if (inlineInput == null || inlineInput.PointToScreen(Point.Empty).Y < status.PointToScreen(Point.Empty).Y + status.Height) throw new InvalidOperationException("Inline question TextBox must render below the independent status strip");
                        await Task.Delay(200); Save(form, Path.Combine(output, "floating-idle.png"));
                        inlineInput.Text = "这是一条可直接输入并按 Enter 提交的问题"; await Task.Delay(80); Save(form, Path.Combine(output, "floating-inline-input.png")); inlineInput.Clear();
                        Find(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextSet]).PerformClick();
                        await WaitUntilAsync(delegate { return controller.Snapshot().contextMode.enabled && status.Text == ""; }, 2200);
                        Find(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.OriginalImageSet]).PerformClick();
                        await WaitUntilAsync(delegate { var state = controller.Snapshot(); return state.contextMode.enabled && state.originalImageMode && status.Text == ""; }, 2200);
                        Save(form, Path.Combine(output, "floating-modes-active.png"));
                        AssertClosedFramePixels(form, "knowledge-clipper.desktop.section.context");
                        AssertClosedFramePixels(form, "knowledge-clipper.desktop.section.original-image");
                        Find(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ContextSet]).PerformClick();
                        await WaitUntilAsync(delegate { return !controller.Snapshot().contextMode.enabled && status.Text == ""; }, 2200);
                        Save(form, Path.Combine(output, "floating-original-image-active.png"));
                        Find(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ClipCapture]).PerformClick(); await Task.Delay(100); Save(form, Path.Combine(output, "floating-success.png"));
                        await Task.Delay(1100); Find(form, AutomationCatalogV11.AutomationIds[AutomationCatalog.ReadCapture]).PerformClick(); await Task.Delay(1200); Save(form, Path.Combine(output, "floating-replay.png"));
                        using (var settings = new SettingsForm(config, UserSettings.Defaults()))
                        {
                            settings.Show(); await Task.Delay(200);
                            var navigation = settings.Controls.Find(SettingsAutomationCatalog.Navigation, true)[0] as ListBox;
                            if (navigation == null || navigation.Items.Count != SettingsForm.PageKeys.Length) throw new InvalidOperationException("Settings navigation is incomplete");
                            for (var pageIndex = 0; pageIndex < SettingsForm.PageKeys.Length; pageIndex++)
                            {
                                navigation.SelectedIndex = pageIndex; Application.DoEvents(); await Task.Delay(60);
                                Save(settings, Path.Combine(output, "settings-" + SettingsForm.PageKeys[pageIndex] + ".png"));
                            }
                            settings.Close();
                        }
                        using (var question = new QuestionForm(delegate(string action, SemanticActionArguments arguments) { return Task.FromResult(DesktopSemanticState.CreateDefault()); }, new PreviewAnswerReader()))
                        {
                            question.Show(); await Task.Delay(160); var input = question.Controls.Find(AutomationCatalogV11.QuestionInputAutomationId, true)[0] as TextBox; input.Text = "请解释这段内容，并给出一个例子。";
                            var state = DesktopSemanticState.CreateDefault(); state.questionSession.open = true; state.questionSession.answerState = "ready"; state.questionSession.answerArtifact = new QuestionAnswerArtifactReference { artifactRef = "question.answer.text.preview", kind = "question.answer.text", contractVersion = "1.0.0", manifestPath = "preview/artifact.json", expectedSha256 = new string('a', 64) }; state.recentTask.available = true; state.recentTask.taskId = "task-question-preview"; question.Update(state);
                            await Task.Delay(80); Save(question, Path.Combine(output, "question.png")); question.Close();
                        }
                        using (var languagePractice = new LanguagePracticeForm()) { languagePractice.Show(); await Task.Delay(160); Save(languagePractice, Path.Combine(output, "language-practice.png")); languagePractice.Close(); }
                        using (var ocrResolution = new OcrHumanResolutionForm(new OcrHumanResolutionView
                        {
                            OriginalText = "题目：识别代码。\r\n答案：MODL。",
                            CandidateText = "题目：识别代码。\r\n答案：MODEL。",
                            MarkdownText = "# 题目\r\n\r\n识别代码：`MODL`",
                            StructuredText = "{\"blocks\":[{\"type\":\"text\",\"text\":\"MODL\"}]}",
                            DifferenceText = "[{\"operation\":\"substitution\",\"before\":\"MODL\",\"after\":\"MODEL\"}]",
                            UnresolvedReason = "insufficient_context：周边内容不足以唯一判断字符。"
                        }))
                        {
                            ocrResolution.Show(); await Task.Delay(160); Save(ocrResolution, Path.Combine(output, "ocr-human-resolution.png")); ocrResolution.Close();
                        }
                        using (var session = new ScreenshotSessionForm(delegate(string action) { return Task.FromResult(0); }))
                        {
                            var state = DesktopSemanticState.CreateDefault(); state.screenshotSession.active = true; state.screenshotSession.activeGroup = "answer"; state.screenshotSession.questionImageCount = 2; state.screenshotSession.answerImageCount = 1; state.screenshotSession.canContinue = true; state.screenshotSession.canFinish = true; state.screenshotSession.canCancel = true; state.screenshotSession.canUndo = true;
                            session.UpdateState(state); session.Show(); await Task.Delay(160); Save(session, Path.Combine(output, "screenshot-session.png")); session.Hide();
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            if (failure != null) { Console.Error.WriteLine("VISUAL_PREVIEW_CANARY FAIL " + failure); return 1; }
            Console.WriteLine("VISUAL_PREVIEW_CANARY PASS " + output); return 0;
        }

        private static CaptureResult TextCapture(string suffix)
        {
            var id = "capture.text." + suffix;
            return new CaptureResult { hasContent = true, source = "selection", selectedTextArtifactRef = id, contextAvailable = false, artifacts = new List<CaptureArtifact> { new CaptureArtifact { artifactRef = id, relativePath = "inputs/" + suffix + ".txt", sha256 = new string('0', 64), bytes = 1, mediaType = "text/plain", encoding = "utf-8" } } };
        }
        private static Button Find(Control root, string id) { return root.Controls.Find(id, true)[0] as Button; }
        private static void Save(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void AssertClosedFramePixels(Form form, string id)
        {
            var section = form.Controls.Find(id, true)[0] as ModeFramePanel;
            if (section == null || section.Parent == null) throw new InvalidOperationException("Missing mode frame: " + id);
            var origin = form.PointToClient(section.PointToScreen(Point.Empty));
            var visibleWidth = Math.Min(section.ClientSize.Width, section.Parent.DisplayRectangle.Right - section.Left);
            var visibleHeight = Math.Min(section.ClientSize.Height, section.Parent.DisplayRectangle.Bottom - section.Top);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                var expected = section.FrameColor;
                var top = bitmap.GetPixel(origin.X + (visibleWidth / 2), origin.Y + 1);
                var bottom = bitmap.GetPixel(origin.X + (visibleWidth / 2), origin.Y + visibleHeight - 1);
                var left = bitmap.GetPixel(origin.X + 1, origin.Y + (visibleHeight / 2));
                var right = bitmap.GetPixel(origin.X + visibleWidth - 1, origin.Y + (visibleHeight / 2));
                if (top.ToArgb() != expected.ToArgb() || bottom.ToArgb() != expected.ToArgb() || left.ToArgb() != expected.ToArgb() || right.ToArgb() != expected.ToArgb())
                    throw new InvalidOperationException("Mode frame is not closed in rendered pixels: " + id);
            }
        }
        private static async Task WaitUntilAsync(Func<bool> condition, int timeout)
        {
            var deadline = Environment.TickCount + timeout;
            while (Environment.TickCount < deadline) { Application.DoEvents(); if (condition()) return; await Task.Delay(15); }
            throw new InvalidOperationException("Timed out waiting for visual preview state");
        }

        private sealed class PreviewAnswerReader : IQuestionAnswerArtifactReader
        {
            public QuestionAnswerReadResult Read(string taskId, QuestionAnswerArtifactReference reference)
            {
                return new QuestionAnswerReadResult { ready = true, text = "这是仅用于视觉 Canary 的已校验示例答案。\r\n新答案会替换当前显示，历史仍保留在不可变任务包中。", noticeCode = "question.answer.verified" };
            }
        }
    }
}

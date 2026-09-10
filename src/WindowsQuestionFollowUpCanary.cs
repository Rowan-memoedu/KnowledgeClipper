using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.Core;
using KnowledgeClipper.Desktop.TestFixtures;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr QuestionKeyMessage(IntPtr hwnd, uint message, IntPtr key, IntPtr flags);
        private static void QuestionEnter(TextBox input)
        {
            QuestionKeyMessage(input.Handle, 0x100, (IntPtr)13, (IntPtr)1);
            QuestionKeyMessage(input.Handle, 0x101, (IntPtr)13, unchecked((IntPtr)0xC0000001L));
            Pump(10);
        }

        private static int QuestionFollowUpUiCanary(string root)
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("immutable-question-ui-evidence-exists");
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var fake = new QuestionFlowFixture(); var application = fake.Root();
            var controller = new DesktopController(new FakeCaptureProvider(null), new FakeScreenshotProvider(null), new FakeDesktopOrchestratorPort(),
                new FakeClock(DateTime.UtcNow), new FakeIdProvider());
            using var owner = new BoundClipperForm(LegacyDesktopAdapter.CreateBoundClipperDependencies(Config(Path.Combine(root, "local")), controller, false, application));
            try
            {
                owner.Show(); Pump(30);
                var inline = Find<TextBox>(owner, "knowledge-clipper.desktop.question.inline-input");
                inline.Text = "  "; QuestionEnter(inline);
                Assert(fake.Submissions.Count == 0, "real inline Enter whitespace dispatches zero production commands");
                inline.Text = "Offline initial question"; QuestionEnter(inline);
                WaitUntil(() => application.FlashcardDraftUseCases.Current.CanFollowUp, 2000, "initial-question-draft");
                var preview = Application.OpenForms.OfType<FlashcardPreviewForm>().Single();
                var input = Find<TextBox>(preview, FlashcardPreviewAutomation.FollowUpInput);
                var follow = Find<Button>(preview, FlashcardPreviewAutomation.FollowUp);
                var confirm = Find<Button>(preview, FlashcardPreviewAutomation.Confirm);
                var status = Find<Label>(preview, FlashcardPreviewAutomation.Status);
                var front = Find<TextBox>(preview, FlashcardPreviewAutomation.Front);
                Assert(fake.Submissions.Count == 1 && !fake.Submissions[0].FollowUp && inline.Text == "", "real inline Enter uses production Application initial once and clears on ready");
                Assert(input.Enabled && !follow.Enabled && confirm.Enabled && front.Text == "Offline front 1", "first automatic draft is visible with input enabled and no implicit follow-up or export");
                Assert(fake.MakeCardsCount == 0 && fake.Confirmations.Count == 0, "initial auto draft does not invoke make-cards or confirmation");
                var firstDraft = preview.RenderedDraft;
                input.Text = " "; QuestionEnter(input);
                Assert(fake.Submissions.Count == 1 && !follow.Enabled, "preview whitespace Enter creates zero follow-up commands");
                input.Text = "Short follow-up";
                Assert(follow.Enabled, "nonempty follow-up enables the real button");
                fake.SubmitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                QuestionEnter(input);
                Assert(fake.Submissions.Count == 2 && !input.Enabled && !follow.Enabled && !confirm.Enabled, "real Enter starts exactly one follow-up and disables old draft confirmation while pending");
                QuestionEnter(input); follow.PerformClick();
                Assert(fake.Submissions.Count == 2, "Enter plus click during pending work is single-flight");
                fake.SubmitGate.SetResult(true);
                WaitUntil(() => preview.RenderedDraft.OperationId != firstDraft.OperationId && input.Enabled, 2000, "follow-up-new-draft");
                Assert(fake.Submissions[1].SessionId == application.State.Snapshot().Question.SessionId && input.Text == "" && !follow.Enabled && confirm.Enabled && front.Text == "Offline front 2", "new draft replaces prior content and re-enables fresh input within the retained session");
                var secondDraft = preview.RenderedDraft;
                fake.SubmitGate = null;
                input.Text = "A distinctly longer follow-up through the real button"; follow.PerformClick();
                WaitUntil(() => input.Enabled && preview.RenderedDraft.OperationId != secondDraft.OperationId, 2000, "button-follow-up-new-draft");
                Assert(fake.Submissions.Count == 3 && fake.Submissions[2].FollowUp && fake.Submissions[2].SessionId == fake.Submissions[1].SessionId && input.Text == "", "button and Enter share one production follow-up use case across two different text lengths");
                var finalDraft = preview.RenderedDraft;
                confirm.PerformClick(); Pump(30);
                Assert(fake.Confirmations.Count == 1 && FlashcardDraftPreviewUseCases.SameDraft(finalDraft, fake.Confirmations.Single()), "only explicit final confirmation carries the actual final rendered draft identity");
                Assert(application.FlashcardDraftUseCases.Current.Status == FlashcardDraftStatus.Completed && !application.FlashcardDraftUseCases.Current.CommandInFlight && status.AccessibleDescription == "Completed" && !confirm.Enabled && !follow.Enabled && !input.Enabled, "real final preview is Completed not busy with follow-up and confirm disabled");
                Assert(status.Text.Contains("本次操作已结束") && !status.Text.Contains("会话未就绪") && !status.Text.Contains("绑定已失效"), "successful preview describes normal completion rather than a broken session");
                Assert(preview.RenderedDraft.OperationId == finalDraft.OperationId && front.Text == "Offline front 3", "completion preserves same operation and literal final draft rather than showing an older answer");
                confirm.PerformClick(); QuestionEnter(input);
                Assert(fake.Confirmations.Count == 1 && fake.Submissions.Count == 3 && fake.MakeCardsCount == 0, "completed draft cannot export twice or automatically create another draft");

                // Start a new session through the same production inline handler.
                inline.Text = "Another initial question"; QuestionEnter(inline);
                WaitUntil(() => input.Enabled, 2000, "new-question-ready");
                fake.Reject = "offline.rejected";
                input.Text = "Keep this rejected follow-up"; follow.PerformClick(); Pump(30);
                Assert(input.Text == "Keep this rejected follow-up" && status.Text.Contains("offline.rejected") && !confirm.Enabled, "rejected follow-up keeps actual typed text and a visible failure without reopening old confirmation");
                fake.Reject = null;
                inline.Text = "Cancellation initial"; QuestionEnter(inline);
                WaitUntil(() => input.Enabled, 2000, "cancel-question-ready");
                fake.SubmitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                input.Text = "Keep cancelled observation text"; follow.PerformClick();
                fake.SubmitGate.SetException(new OperationCanceledException());
                WaitUntil(() => status.Text.Contains("question-submit-outcome-unknown"), 2000, "interrupted-follow-up-visible");
                Assert(input.Text == "Keep cancelled observation text" && application.State.Snapshot().Question.Status == OperationStatus.Unknown && !confirm.Enabled, "interrupted submission preserves text and says unknown not Core cancelled");
                var beforeRead = fake.Submissions.Count;
                application.FlashcardDraftUseCases.Project(new(FlashcardDraftStatus.AwaitingConfirmation, QuestionFlowFixture.Draft("read-operation", 9, FlashcardDraftSource.ReadAloud), 9, "read", true)); Pump(20);
                Assert(!input.Enabled && !follow.Enabled && status.Text.Contains("摘录/朗读草稿暂不支持追问"), "read and clip preview visibly expose follow-up as unsupported");
                QuestionEnter(input); follow.PerformClick();
                Assert(fake.Submissions.Count == beforeRead, "read or clip cannot reuse the preceding Question session");
                using (var bitmap = new System.Drawing.Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, preview.ClientRectangle); bitmap.Save(Path.Combine(root, "read-follow-up-unavailable.png")); }
                File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { status = "PASS", assertions, mechanism = "real WinForms controls + WM_KEYDOWN/UP and PerformClick -> production BoundClipperForm -> QuestionUseCases -> deterministic ports", initialAndFollowUpCommands = fake.Submissions.Count, explicitConfirmations = fake.Confirmations.Count, automaticMakeCards = fake.MakeCardsCount, externalCalls = 0, physicalInput = false, actualAnkiWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("WINDOWS_QUESTION_FOLLOW_UP PASS assertions=" + assertions); return 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
            finally { owner.Close(); Pump(20); }
        }
    }
}

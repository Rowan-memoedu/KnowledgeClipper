using System;
using System.Collections.Generic;
using System.Linq;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class DesktopController
    {
        private readonly ICaptureProvider captureProvider;
        private readonly IScreenshotProvider screenshotProvider;
        private readonly IDesktopOrchestratorPort orchestrator;
        private readonly IClock clock;
        private readonly IIdProvider ids;
        private readonly DesktopSemanticState state;
        private readonly QuestionSessionModel questionSession;
        private readonly List<ScreenshotEntry> screenshotEntries = new List<ScreenshotEntry>();
        private bool screenshotOriginalImageSnapshot;
        private CaptureResult pendingCapture;
        private string pendingIntent;

        internal DesktopController(ICaptureProvider captureProvider, IScreenshotProvider screenshotProvider, IDesktopOrchestratorPort orchestrator, IClock clock, IIdProvider ids)
        {
            this.captureProvider = captureProvider;
            this.screenshotProvider = screenshotProvider;
            this.orchestrator = orchestrator;
            this.clock = clock;
            this.ids = ids;
            state = DesktopSemanticState.CreateDefault();
            questionSession = new QuestionSessionModel(state.questionSession);
        }

        internal DesktopSemanticState Snapshot() { return state.Copy(); }

        internal DesktopSemanticState CloseQuestionInteraction()
        {
            state.revision++;
            state.lastAction = "question.cancel";
            questionSession.Cancel();
            if (state.foregroundState == "question-composing" || state.foregroundState == "question-awaiting-answer" || state.foregroundState == "question-answer-ready") state.foregroundState = "idle";
            state.noticeCode = "question.dialog-cancelled";
            return Snapshot();
        }

        internal DesktopSemanticState CompleteInlineQuestionInteraction()
        {
            state.revision++;
            questionSession.Cancel();
            state.questionSession.answerState = "waiting_input";
            state.questionSession.answerArtifact = null;
            state.questionSession.questionTargetToken = null;
            if (state.foregroundState == "question-composing" || state.foregroundState == "question-awaiting-answer" || state.foregroundState == "question-answer-ready") state.foregroundState = "idle";
            return Snapshot();
        }

        internal DesktopSemanticState DismissQuestionDialogKeepingSession()
        {
            state.revision++;
            if (state.questionSession != null && state.questionSession.open)
            {
                state.foregroundState = "idle";
                state.noticeCode = "question.dialog-dismissed-session-preserved";
            }
            return Snapshot();
        }

        internal DesktopSemanticState RefreshActiveQuestion()
        {
            state.revision++;
            state.lastAction = "question.session.refresh";
            if (!state.questionSession.open || string.IsNullOrWhiteSpace(questionSession.TaskId))
            {
                state.noticeCode = "question.task-unavailable";
                return Snapshot();
            }
            ApplyRecentTaskProjection(orchestrator.QueryTask(questionSession.TaskId));
            state.noticeCode = "question.task-queried";
            return Snapshot();
        }

        private void ProvideLanguagePracticeSelection(string selection)
        {
            if (!state.recentTask.available || string.IsNullOrWhiteSpace(state.recentTask.taskId) || !string.Equals(state.recentTask.decisionKind, AutomationCatalogV11.LanguagePracticeDecisionKind, StringComparison.Ordinal) || !AutomationCatalogV11.IsDecisionTargetToken(state.recentTask.decisionTargetToken))
            {
                state.noticeCode = "language-practice.decision-not-projected";
                return;
            }
            if (selection != AutomationCatalogV11.LanguagePracticeMeaningReading && selection != AutomationCatalogV11.LanguagePracticeWriting && selection != AutomationCatalogV11.LanguagePracticeBoth)
            {
                state.noticeCode = "language-practice.selection-invalid";
                return;
            }
            var result = orchestrator.ProvideLanguagePracticeInput(state.recentTask.taskId, state.recentTask.decisionTargetToken, selection);
            if (result != null && result.accepted)
            {
                state.recentTask.decisionKind = null;
                state.recentTask.decisionTargetToken = null;
                state.recentTask.displayState = "queued";
                state.noticeCode = "language-practice.input-submitted";
            }
            else state.noticeCode = result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "language-practice.input-submit-pending" : result.noticeCode;
        }

        internal DesktopSemanticState Execute(SemanticActionEnvelope action)
        {
            state.revision++;
            state.lastAction = action == null ? null : action.action;
            state.noticeCode = null;
            var knownV1 = action != null && action.schemaVersion == AutomationCatalog.ActionSchemaVersion && AutomationCatalog.IsKnownAction(action.action);
            var knownV11 = action != null && action.schemaVersion == AutomationCatalogV11.ActionSchemaVersion && AutomationCatalogV11.IsKnownAction(action.action);
            var knownV13 = action != null && action.schemaVersion == AutomationCatalogV13.ActionSchemaVersion && AutomationCatalogV13.IsKnownAction(action.action);
            if (action == null || (!knownV1 && !knownV11 && !knownV13))
            {
                state.noticeCode = "action.unknown";
                return Snapshot();
            }

            switch (action.action)
            {
                case AutomationCatalog.ClipCapture: Capture("clip"); break;
                case AutomationCatalog.ReadCapture: Capture("read"); break;
                case AutomationCatalog.ScreenshotStart: StartScreenshot(); break;
                case AutomationCatalog.ScreenshotContinue: ContinueScreenshot(); break;
                case AutomationCatalog.ScreenshotFinish: FinishScreenshot(); break;
                case AutomationCatalog.ScreenshotCancel: CancelScreenshot(); break;
                case AutomationCatalog.ScreenshotUndoLast: UndoScreenshot(); break;
                case AutomationCatalog.ScreenshotGroupQuestion: SetScreenshotGroup("question"); break;
                case AutomationCatalog.ScreenshotGroupAnswer: SetScreenshotGroup("answer"); break;
                case AutomationCatalog.ContextSet: SetContext(action.arguments); break;
                case AutomationCatalog.OriginalImageSet: SetOriginalImage(action.arguments); break;
                case AutomationCatalog.ContextContinueWithout: ContinueWithoutContext(); break;
                case AutomationCatalog.QuestionOpen: OpenQuestion(); break;
                case AutomationCatalog.QuestionSubmit: SubmitQuestion(action.arguments, false); break;
                case AutomationCatalog.QuestionFollowUp: SubmitQuestion(action.arguments, true); break;
                case AutomationCatalog.QuestionMakeCards: MakeCards(action.arguments); break;
                case AutomationCatalog.PlaybackReplay: Replay(); break;
                case AutomationCatalog.PlaybackStop: StopPlayback(); break;
                case AutomationCatalog.TaskStatusQueryRecent: QueryRecentTask(); break;
                case AutomationCatalogV11.LanguagePracticeSelect: ProvideLanguagePracticeSelection(action.arguments == null ? null : action.arguments.selection); break;
                case AutomationCatalogV13.OcrAcceptOriginal: ProvideOcrHumanResolution("accept_original", null); break;
                case AutomationCatalogV13.OcrSubmitCorrection: ProvideOcrHumanResolution("submit_correction", action.arguments == null ? null : action.arguments.text, action.arguments == null ? null : action.arguments.candidateText); break;
                case AutomationCatalogV13.OcrCancel: ProvideOcrHumanResolution("cancel", null); break;
            }
            return Snapshot();
        }

        private void ProvideOcrHumanResolution(string action, string correctedText, string candidateText = null)
        {
            var projected = state.recentTask.ocrHumanResolution;
            if (!state.recentTask.available || state.recentTask.displayState != "awaiting-user" || !state.recentTask.attentionRequired ||
                state.recentTask.decisionKind != AutomationCatalogV13.OcrDecisionKind || !AutomationCatalogV11.IsDecisionTargetToken(state.recentTask.decisionTargetToken) ||
                projected == null || projected.targetToken != state.recentTask.decisionTargetToken)
            {
                state.noticeCode = "ocr-human-resolution.decision-not-projected";
                return;
            }
            if (projected.allowedActions == null || !projected.allowedActions.Contains(action))
            {
                state.noticeCode = "ocr-human-resolution.action-not-allowed";
                return;
            }
            if (action == "submit_correction" && string.IsNullOrWhiteSpace(correctedText))
            {
                state.noticeCode = "ocr-human-resolution.correction-empty";
                return;
            }
            var result = orchestrator.ProvideOcrHumanResolution(state.recentTask.taskId, state.recentTask.decisionTargetToken, action, correctedText, candidateText);
            if (result != null && result.accepted)
            {
                state.recentTask.attentionRequired = false;
                state.recentTask.decisionKind = null;
                state.recentTask.decisionTargetToken = null;
                state.recentTask.ocrHumanResolution = null;
                state.recentTask.displayState = action == "cancel" ? "cancelled" : "queued";
                state.noticeCode = action == "cancel" ? "ocr-human-resolution.cancel-submitted" : "ocr-human-resolution.input-submitted";
            }
            else state.noticeCode = result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "ocr-human-resolution.input-submit-pending" : result.noticeCode;
        }

        private void Capture(string intent)
        {
            if (!RequireIdle()) return;
            state.foregroundState = "capture-pending";
            var result = captureProvider.Capture(intent, new ContextModeState { enabled = state.contextMode.enabled, granularity = state.contextMode.granularity });
            if (result == null || !result.hasContent)
            {
                state.foregroundState = "idle";
                state.noticeCode = "capture.empty-input";
                return;
            }
            if (state.contextMode.enabled && !result.contextAvailable)
            {
                pendingCapture = result;
                pendingIntent = intent;
                state.foregroundState = "context-required";
                state.noticeCode = "capture.context-unavailable";
                return;
            }
            SubmitCapture(BuildCaptureBundle(intent, result, state.contextMode.enabled && result.contextAvailable));
        }

        private void ContinueWithoutContext()
        {
            if (pendingCapture == null || state.foregroundState != "context-required")
            {
                state.noticeCode = "capture.no-context-decision-pending";
                return;
            }
            var result = pendingCapture;
            var intent = pendingIntent;
            pendingCapture = null;
            pendingIntent = null;
            SubmitCapture(BuildCaptureBundle(intent, result, false));
        }

        private void SubmitCapture(CaptureBundleV1 bundle)
        {
            CaptureBundleValidator.Validate(bundle);
            var result = orchestrator.SubmitCapture(bundle);
            state.foregroundState = "idle";
            state.noticeCode = result != null && result.accepted ? "capture.submitted" : (result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "capture.submit-pending" : result.noticeCode);
            if (result != null && !string.IsNullOrWhiteSpace(result.taskId))
                state.recentTask = new RecentTaskState { available = true, taskId = result.taskId, displayState = result.accepted ? "queued" : "unknown" };
        }

        private CaptureBundleV1 BuildCaptureBundle(string intent, CaptureResult result, bool contextApplied)
        {
            var artifacts = result.artifacts == null ? new List<CaptureArtifact>() : result.artifacts.Select(item => item.Copy()).ToList();
            var images = artifacts.Where(item => item.mediaType != null && item.mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).Select(item => item.artifactRef).ToList();
            return NewBundle(intent, result.source, artifacts, result.selectedTextArtifactRef, contextApplied ? result.contextSegments : null, images, new List<string>());
        }

        private void StartScreenshot()
        {
            if (!RequireIdle()) return;
            screenshotEntries.Clear();
            screenshotOriginalImageSnapshot = state.originalImageMode;
            state.screenshotSession = ScreenshotSessionState.Empty();
            state.screenshotSession.active = true;
            state.screenshotSession.activeGroup = "question";
            RefreshScreenshotState();
            CaptureScreenshotFrame();
        }

        private void ContinueScreenshot()
        {
            if (!state.screenshotSession.active || state.foregroundState != "screenshot-review")
            {
                state.noticeCode = "screenshot.no-active-session";
                return;
            }
            CaptureScreenshotFrame();
        }

        private void CaptureScreenshotFrame()
        {
            state.foregroundState = "screenshot-awaiting-frame";
            var result = screenshotProvider.Capture(state.screenshotSession.activeGroup);
            if (result != null && string.Equals(result.outcome, "captured", StringComparison.Ordinal) && result.artifact != null)
            {
                screenshotEntries.Add(new ScreenshotEntry { Group = state.screenshotSession.activeGroup, Artifact = result.artifact.Copy() });
                state.foregroundState = "screenshot-review";
                state.noticeCode = "screenshot.frame-captured";
            }
            else if (screenshotEntries.Count == 0)
            {
                state.screenshotSession = ScreenshotSessionState.Empty();
                state.foregroundState = "idle";
                state.noticeCode = result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "screenshot.cancelled-before-input" : result.noticeCode;
            }
            else
            {
                state.foregroundState = "screenshot-review";
                state.noticeCode = result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "screenshot.frame-not-captured" : result.noticeCode;
            }
            RefreshScreenshotState();
        }

        private void SetScreenshotGroup(string group)
        {
            if (!state.screenshotSession.active)
            {
                state.noticeCode = "screenshot.no-active-session";
                return;
            }
            state.screenshotSession.activeGroup = group;
            state.noticeCode = "screenshot.group-" + group;
        }

        private void UndoScreenshot()
        {
            if (!state.screenshotSession.active || screenshotEntries.Count == 0)
            {
                state.noticeCode = "screenshot.nothing-to-undo";
                return;
            }
            screenshotEntries.RemoveAt(screenshotEntries.Count - 1);
            state.noticeCode = "screenshot.last-frame-undone";
            state.foregroundState = screenshotEntries.Count == 0 ? "screenshot-review" : "screenshot-review";
            RefreshScreenshotState();
        }

        private void FinishScreenshot()
        {
            if (!state.screenshotSession.active || screenshotEntries.Count == 0)
            {
                state.noticeCode = "screenshot.no-active-session";
                return;
            }
            var question = screenshotEntries.Where(item => item.Group == "question").Select(item => item.Artifact.artifactRef).ToList();
            if (question.Count == 0)
            {
                state.noticeCode = "screenshot.question-images-required";
                return;
            }
            var answer = screenshotEntries.Where(item => item.Group == "answer").Select(item => item.Artifact.artifactRef).ToList();
            var artifacts = screenshotEntries.Select(item => item.Artifact.Copy()).ToList();
            var bundle = NewBundle("screenshot", "screenshot", artifacts, null, null, question, answer);
            bundle.modeSnapshot.contextEnabled = false;
            bundle.modeSnapshot.contextApplied = false;
            bundle.modeSnapshot.imageHandling = screenshotOriginalImageSnapshot ? "original" : "ocr";
            CaptureBundleValidator.Validate(bundle);
            var result = orchestrator.SubmitCapture(bundle);
            screenshotEntries.Clear();
            state.screenshotSession = ScreenshotSessionState.Empty();
            state.foregroundState = "idle";
            state.noticeCode = result != null && result.accepted ? "screenshot.submitted" : (result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "screenshot.submit-pending" : result.noticeCode);
            if (result != null && !string.IsNullOrWhiteSpace(result.taskId)) state.recentTask = new RecentTaskState { available = true, taskId = result.taskId, displayState = result.accepted ? "queued" : "unknown" };
        }

        private void CancelScreenshot()
        {
            if (!state.screenshotSession.active)
            {
                state.noticeCode = "screenshot.no-active-session";
                return;
            }
            screenshotEntries.Clear();
            state.screenshotSession = ScreenshotSessionState.Empty();
            state.foregroundState = "idle";
            state.noticeCode = "screenshot.session-cancelled";
        }

        private CaptureBundleV1 NewBundle(string intent, string source, List<CaptureArtifact> artifacts, string selectedTextRef, ContextSegments context, List<string> questionImages, List<string> answerImages)
        {
            return new CaptureBundleV1
            {
                schemaVersion = AutomationCatalog.CaptureBundleSchemaVersion,
                captureId = ids.NewCaptureId(),
                createdAt = clock.UtcNow.ToString("o"),
                intent = intent,
                source = source,
                modeSnapshot = new CaptureModeSnapshot
                {
                    workflow = "card",
                    contextEnabled = state.contextMode.enabled,
                    contextGranularity = state.contextMode.granularity,
                    contextApplied = context != null,
                    imageHandling = intent == "screenshot" && state.originalImageMode ? "original" : "ocr"
                },
                artifacts = artifacts ?? new List<CaptureArtifact>(),
                selectedTextArtifactRef = selectedTextRef,
                contextSegments = context == null ? null : context.Copy(),
                questionImages = questionImages ?? new List<string>(),
                answerImages = answerImages ?? new List<string>(),
                questionTurns = new List<QuestionTurnReference>()
            };
        }

        private void SetContext(SemanticActionArguments arguments)
        {
            if (arguments == null || !arguments.enabled.HasValue || (arguments.granularity != "sentence" && arguments.granularity != "paragraph"))
            {
                state.noticeCode = "mode.context.invalid-arguments";
                return;
            }
            state.contextMode.enabled = arguments.enabled.Value;
            state.contextMode.granularity = arguments.granularity;
            state.noticeCode = "mode.context-updated";
        }

        private void SetOriginalImage(SemanticActionArguments arguments)
        {
            if (arguments == null || !arguments.enabled.HasValue)
            {
                state.noticeCode = "mode.original-image.invalid-arguments";
                return;
            }
            state.originalImageMode = arguments.enabled.Value;
            state.noticeCode = "mode.original-image-updated";
        }

        private void OpenQuestion()
        {
            if (!RequireIdle()) return;
            questionSession.OpenNew(state.revision);
            state.foregroundState = "question-composing";
            state.noticeCode = "question.opened";
        }

        private void SubmitQuestion(SemanticActionArguments arguments, bool followUp)
        {
            if (!state.questionSession.open || string.IsNullOrWhiteSpace(arguments == null ? null : arguments.text))
            {
                state.noticeCode = "question.invalid-state-or-text";
                return;
            }
            if (questionSession.IsRunning)
            {
                state.noticeCode = "question.submission-in-progress";
                return;
            }
            if (followUp && !questionSession.CanSubmitFollowUp)
            {
                state.noticeCode = "question.answer-required-before-follow-up";
                return;
            }
            if (!followUp && !questionSession.CanSubmitInitial)
            {
                state.noticeCode = state.questionSession.answerAvailable ? "question.follow-up-required" : "question.initial-already-submitted";
                return;
            }
            state.foregroundState = "question-awaiting-answer";
            questionSession.BeginSubmission();
            var result = orchestrator.SubmitQuestion(arguments.text.Trim(), followUp, questionSession.TaskId);
            if (result != null && result.accepted)
            {
                questionSession.Accepted(result.taskId, result.answerAvailable);
                state.foregroundState = result.answerAvailable ? "question-answer-ready" : "question-awaiting-answer";
                state.noticeCode = followUp ? "question.follow-up-submitted" : "question.submitted";
                if (!string.IsNullOrWhiteSpace(result.taskId)) state.recentTask = new RecentTaskState { available = true, taskId = result.taskId, displayState = result.answerAvailable ? "awaiting-user" : "running" };
            }
            else
            {
                state.foregroundState = "question-composing";
                questionSession.RejectSubmission();
                state.noticeCode = result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "question.submit-pending" : result.noticeCode;
            }
        }

        private void MakeCards(SemanticActionArguments arguments)
        {
            if (!state.questionSession.open || !state.questionSession.answerAvailable)
            {
                state.noticeCode = "question.cards-invalid-state-or-reference";
                return;
            }
            var result = orchestrator.MakeCards(questionSession.TaskId, arguments == null || string.IsNullOrWhiteSpace(arguments.cardStructureRef) ? null : arguments.cardStructureRef.Trim());
            state.noticeCode = result != null && result.accepted ? "question.cards-submitted" : (result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "question.cards-submit-pending" : result.noticeCode);
            if (result != null && result.accepted)
            {
                state.questionSession.open = false;
                state.foregroundState = "idle";
                if (!string.IsNullOrWhiteSpace(result.taskId)) state.recentTask = new RecentTaskState { available = true, taskId = result.taskId, displayState = "queued" };
            }
        }

        private void Replay()
        {
            var result = orchestrator.Replay();
            if (result != null && result.accepted && !string.IsNullOrWhiteSpace(result.state)) state.playback = result.state;
            state.noticeCode = result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "playback.replay-requested" : result.noticeCode;
        }

        private void StopPlayback()
        {
            var result = orchestrator.StopPlayback();
            if (result != null && result.accepted && !string.IsNullOrWhiteSpace(result.state)) state.playback = result.state;
            state.noticeCode = result == null || string.IsNullOrWhiteSpace(result.noticeCode) ? "playback.stop-requested" : result.noticeCode;
        }

        private void QueryRecentTask()
        {
            ApplyRecentTaskProjection(orchestrator.QueryRecentTask());
            state.noticeCode = "task.recent-queried";
        }

        private void ApplyRecentTaskProjection(RecentTaskProjection result)
        {
            state.recentTask = result == null ? new RecentTaskState { available = false, taskId = null, displayState = "unknown", decisionKind = null, decisionTargetToken = null } : new RecentTaskState
            {
                available = result.available, taskId = result.taskId, intent = result.intent, displayState = string.IsNullOrWhiteSpace(result.displayState) ? "unknown" : result.displayState,
                decisionKind = result.decisionKind, decisionTargetToken = result.decisionTargetToken, questionTargetToken = result.questionTargetToken,
                attentionRequired = result.attentionRequired, questionTurnCount = result.questionTurnCount, questionAnswerRef = result.questionAnswerRef == null ? null : result.questionAnswerRef.Copy(),
                ocrHumanResolution = result.ocrHumanResolution == null ? null : result.ocrHumanResolution.Copy()
            };
            if (!state.questionSession.open || string.IsNullOrWhiteSpace(questionSession.TaskId)) return;
            questionSession.ApplyProjection(result);
            if (state.questionSession.answerAvailable) state.foregroundState = "question-answer-ready";
            else if (questionSession.Phase == "running") state.foregroundState = "question-awaiting-answer";
            else state.foregroundState = "question-composing";
        }

        private bool RequireIdle()
        {
            if (state.foregroundState == "idle") return true;
            state.noticeCode = "desktop.foreground-busy";
            return false;
        }

        private void RefreshScreenshotState()
        {
            state.screenshotSession.questionImageCount = screenshotEntries.Count(item => item.Group == "question");
            state.screenshotSession.answerImageCount = screenshotEntries.Count(item => item.Group == "answer");
            state.screenshotSession.canContinue = state.screenshotSession.active;
            state.screenshotSession.canFinish = state.screenshotSession.active && state.screenshotSession.questionImageCount > 0;
            state.screenshotSession.canCancel = state.screenshotSession.active;
            state.screenshotSession.canUndo = state.screenshotSession.active && screenshotEntries.Count > 0;
        }

        private sealed class ScreenshotEntry
        {
            public string Group;
            public CaptureArtifact Artifact;
        }
    }
}

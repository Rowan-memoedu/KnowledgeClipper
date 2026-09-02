using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Core
{
    internal static class AutomationCatalog
    {
        internal const string ContractVersion = "1.0.0";
        internal const string ActionSchemaVersion = "desktop-automation.v1";
        internal const string StateSchemaVersion = "desktop-state.v1";
        internal const string CaptureBundleSchemaVersion = "capture-bundle.v1";

        internal const string ClipCapture = "clip.capture";
        internal const string ReadCapture = "read.capture";
        internal const string ScreenshotStart = "screenshot.session.start";
        internal const string ScreenshotContinue = "screenshot.session.continue";
        internal const string ScreenshotFinish = "screenshot.session.finish";
        internal const string ScreenshotCancel = "screenshot.session.cancel";
        internal const string ScreenshotUndoLast = "screenshot.session.undo-last";
        internal const string ScreenshotGroupQuestion = "screenshot.group.set-question";
        internal const string ScreenshotGroupAnswer = "screenshot.group.set-answer";
        internal const string ContextSet = "mode.context.set";
        internal const string OriginalImageSet = "mode.original-image.set";
        internal const string ContextContinueWithout = "capture.context.continue-without";
        internal const string QuestionOpen = "question.open";
        internal const string QuestionSubmit = "question.submit";
        internal const string QuestionFollowUp = "question.follow-up";
        internal const string QuestionMakeCards = "question.make-cards";
        internal const string PlaybackReplay = "playback.replay";
        internal const string PlaybackStop = "playback.stop";
        internal const string TaskStatusQueryRecent = "task.status.query-recent";

        internal static readonly string[] SemanticActions =
        {
            ClipCapture, ReadCapture, ScreenshotStart, ScreenshotContinue, ScreenshotFinish,
            ScreenshotCancel, ScreenshotUndoLast, ScreenshotGroupQuestion, ScreenshotGroupAnswer,
            ContextSet, OriginalImageSet, ContextContinueWithout, QuestionOpen, QuestionSubmit,
            QuestionFollowUp, QuestionMakeCards, PlaybackReplay, PlaybackStop, TaskStatusQueryRecent
        };

        internal static readonly IDictionary<string, string> AutomationIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ClipCapture, "rqc.desktop.clip" },
            { ReadCapture, "rqc.desktop.read" },
            { ScreenshotStart, "rqc.desktop.screenshot" },
            { ScreenshotContinue, "rqc.desktop.screenshot.continue" },
            { ScreenshotFinish, "rqc.desktop.screenshot.finish" },
            { ScreenshotCancel, "rqc.desktop.screenshot.cancel" },
            { ScreenshotUndoLast, "rqc.desktop.screenshot.undo-last" },
            { ScreenshotGroupQuestion, "rqc.desktop.screenshot.group.question" },
            { ScreenshotGroupAnswer, "rqc.desktop.screenshot.group.answer" },
            { ContextSet, "rqc.desktop.mode.context" },
            { OriginalImageSet, "rqc.desktop.mode.original-image" },
            { ContextContinueWithout, "rqc.desktop.context.continue-without" },
            { QuestionOpen, "rqc.desktop.question.open" },
            { QuestionSubmit, "rqc.desktop.question.submit" },
            { QuestionFollowUp, "rqc.desktop.question.follow-up.submit" },
            { QuestionMakeCards, "rqc.desktop.question.make-cards" },
            { PlaybackReplay, "rqc.desktop.playback.replay" },
            { PlaybackStop, "rqc.desktop.playback.stop" },
            { TaskStatusQueryRecent, "rqc.desktop.task.status.recent" }
        };

        internal static bool IsKnownAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            for (var i = 0; i < SemanticActions.Length; i++)
                if (string.Equals(SemanticActions[i], action, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}

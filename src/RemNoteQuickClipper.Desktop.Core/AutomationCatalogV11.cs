using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Core
{
    internal static class AutomationCatalogV11
    {
        internal const string ContractVersion = "1.1.0";
        internal const string ActionSchemaVersion = "desktop-automation.v1.1";
        internal const string StateSchemaVersion = "desktop-state.v1.1";
        internal const string LanguagePracticeSelect = "language-practice.select";
        internal const string LanguagePracticeDecisionKind = "language-practice";
        internal const string LanguagePracticeMeaningReading = "meaning-reading";
        internal const string LanguagePracticeWriting = "writing";
        internal const string LanguagePracticeBoth = "both";

        internal static readonly string[] SemanticActions =
        {
            AutomationCatalog.ClipCapture, AutomationCatalog.ReadCapture, AutomationCatalog.ScreenshotStart,
            AutomationCatalog.ScreenshotContinue, AutomationCatalog.ScreenshotFinish, AutomationCatalog.ScreenshotCancel,
            AutomationCatalog.ScreenshotUndoLast, AutomationCatalog.ScreenshotGroupQuestion, AutomationCatalog.ScreenshotGroupAnswer,
            AutomationCatalog.ContextSet, AutomationCatalog.OriginalImageSet, AutomationCatalog.ContextContinueWithout,
            AutomationCatalog.QuestionOpen, AutomationCatalog.QuestionSubmit, AutomationCatalog.QuestionFollowUp,
            AutomationCatalog.QuestionMakeCards, AutomationCatalog.PlaybackReplay, AutomationCatalog.PlaybackStop,
            AutomationCatalog.TaskStatusQueryRecent, LanguagePracticeSelect
        };

        internal static readonly IDictionary<string, string> AutomationIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { AutomationCatalog.ClipCapture, "knowledge-clipper.desktop.clip" },
            { AutomationCatalog.ReadCapture, "knowledge-clipper.desktop.read" },
            { AutomationCatalog.ScreenshotStart, "knowledge-clipper.desktop.screenshot" },
            { AutomationCatalog.ScreenshotContinue, "knowledge-clipper.desktop.screenshot.continue" },
            { AutomationCatalog.ScreenshotFinish, "knowledge-clipper.desktop.screenshot.finish" },
            { AutomationCatalog.ScreenshotCancel, "knowledge-clipper.desktop.screenshot.cancel" },
            { AutomationCatalog.ScreenshotUndoLast, "knowledge-clipper.desktop.screenshot.undo-last" },
            { AutomationCatalog.ScreenshotGroupQuestion, "knowledge-clipper.desktop.screenshot.group.question" },
            { AutomationCatalog.ScreenshotGroupAnswer, "knowledge-clipper.desktop.screenshot.group.answer" },
            { AutomationCatalog.ContextSet, "knowledge-clipper.desktop.mode.context" },
            { AutomationCatalog.OriginalImageSet, "knowledge-clipper.desktop.mode.original-image" },
            { AutomationCatalog.ContextContinueWithout, "knowledge-clipper.desktop.context.continue-without" },
            { AutomationCatalog.QuestionOpen, "knowledge-clipper.desktop.question.open" },
            { AutomationCatalog.QuestionSubmit, "knowledge-clipper.desktop.question.submit" },
            { AutomationCatalog.QuestionFollowUp, "knowledge-clipper.desktop.question.follow-up.submit" },
            { AutomationCatalog.QuestionMakeCards, "knowledge-clipper.desktop.question.make-cards" },
            { AutomationCatalog.PlaybackReplay, "knowledge-clipper.desktop.playback.replay" },
            { AutomationCatalog.PlaybackStop, "knowledge-clipper.desktop.playback.stop" },
            { AutomationCatalog.TaskStatusQueryRecent, "knowledge-clipper.desktop.task.status.recent" },
            { LanguagePracticeSelect, "knowledge-clipper.desktop.language-practice.meaning-reading" }
        };

        internal const string WindowTitleBarAutomationId = "knowledge-clipper.desktop.window.titlebar";
        internal const string WindowTitleAutomationId = "knowledge-clipper.desktop.window.title";
        internal const string WindowMinimizeAutomationId = "knowledge-clipper.desktop.window.minimize";
        internal const string WindowCloseAutomationId = "knowledge-clipper.desktop.window.close";
        internal const string DragFrameAutomationId = "knowledge-clipper.desktop.drag-frame";
        internal const string StatusAutomationId = "knowledge-clipper.desktop.status";
        internal const string QuestionInputAutomationId = "knowledge-clipper.desktop.question.input";
        internal const string QuestionFollowUpInputAutomationId = "knowledge-clipper.desktop.question.follow-up.input";
        internal const string QuestionCardStructureAutomationId = "knowledge-clipper.desktop.question.card-structure-ref";
        internal const string QuestionValidationAutomationId = "knowledge-clipper.desktop.question.validation";
        internal const string QuestionAnswerStatusAutomationId = "knowledge-clipper.desktop.question.answer.status";
        internal const string QuestionAnswerAutomationId = "knowledge-clipper.desktop.question.answer";
        internal const string QuestionCancelAutomationId = "knowledge-clipper.desktop.question.cancel";
        internal const string LanguagePracticeMeaningReadingAutomationId = "knowledge-clipper.desktop.language-practice.meaning-reading";
        internal const string LanguagePracticeWritingAutomationId = "knowledge-clipper.desktop.language-practice.writing";
        internal const string LanguagePracticeBothAutomationId = "knowledge-clipper.desktop.language-practice.both";
        internal const string LanguagePracticeCancelAutomationId = "knowledge-clipper.desktop.language-practice.cancel";

        internal static bool IsKnownAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            for (var i = 0; i < SemanticActions.Length; i++) if (string.Equals(SemanticActions[i], action, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static bool IsDecisionTargetToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= 'a' && character <= 'f') || (character >= '0' && character <= '9'))) return false;
            }
            return true;
        }
    }
}

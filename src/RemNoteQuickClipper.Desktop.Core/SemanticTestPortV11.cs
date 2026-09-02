using System;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class SemanticTestPortV11
    {
        private readonly DesktopController controller;

        internal SemanticTestPortV11(DesktopController controller) { this.controller = controller; }

        internal SemanticPortResponseV11 Execute(SemanticActionEnvelope action)
        {
            if (action == null) return Error(null, "test-port.invalid-json-action");
            if (!string.Equals(action.schemaVersion, AutomationCatalogV11.ActionSchemaVersion, StringComparison.Ordinal)) return Error(action.requestId, "test-port.unsupported-schema-version");
            if (string.IsNullOrWhiteSpace(action.requestId)) return Error(null, "test-port.request-id-required");
            if (!AutomationCatalogV11.IsKnownAction(action.action)) return Error(action.requestId, "test-port.unknown-action");
            return new SemanticPortResponseV11 { schemaVersion = "desktop-test-port.v1.1", requestId = action.requestId, ok = true, errorCode = null, state = DesktopSemanticStateV11.From(controller.Execute(action)) };
        }

        private SemanticPortResponseV11 Error(string requestId, string code)
        {
            return new SemanticPortResponseV11 { schemaVersion = "desktop-test-port.v1.1", requestId = requestId, ok = false, errorCode = code, state = DesktopSemanticStateV11.From(controller.Snapshot()) };
        }
    }

    internal sealed class SemanticPortResponseV11
    {
        public string schemaVersion { get; set; }
        public string requestId { get; set; }
        public bool ok { get; set; }
        public string errorCode { get; set; }
        public DesktopSemanticStateV11 state { get; set; }
    }

    internal sealed class DesktopSemanticStateV11
    {
        public string schemaVersion { get; set; }
        public long revision { get; set; }
        public string foregroundState { get; set; }
        public ContextModeState contextMode { get; set; }
        public bool originalImageMode { get; set; }
        public ScreenshotSessionState screenshotSession { get; set; }
        public QuestionSessionState questionSession { get; set; }
        public string playback { get; set; }
        public RecentTaskStateV11 recentTask { get; set; }
        public string lastAction { get; set; }
        public string noticeCode { get; set; }

        internal static DesktopSemanticStateV11 From(DesktopSemanticState value)
        {
            return new DesktopSemanticStateV11
            {
                schemaVersion = AutomationCatalogV11.StateSchemaVersion,
                revision = value.revision,
                foregroundState = value.foregroundState,
                contextMode = new ContextModeState { enabled = value.contextMode.enabled, granularity = value.contextMode.granularity },
                originalImageMode = value.originalImageMode,
                screenshotSession = value.screenshotSession.Copy(),
                questionSession = new QuestionSessionState { open = value.questionSession.open, turnCount = value.questionSession.turnCount, answerAvailable = value.questionSession.answerAvailable },
                playback = value.playback,
                recentTask = new RecentTaskStateV11 { available = value.recentTask.available, taskId = value.recentTask.taskId, displayState = value.recentTask.displayState, decisionKind = value.recentTask.decisionKind, decisionTargetToken = value.recentTask.decisionTargetToken },
                lastAction = value.lastAction,
                noticeCode = value.noticeCode
            };
        }
    }

    internal sealed class RecentTaskStateV11
    {
        public bool available { get; set; }
        public string taskId { get; set; }
        public string displayState { get; set; }
        public string decisionKind { get; set; }
        public string decisionTargetToken { get; set; }
    }
}

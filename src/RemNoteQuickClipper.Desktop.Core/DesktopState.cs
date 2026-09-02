namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class DesktopSemanticState
    {
        public string schemaVersion { get; set; }
        public long revision { get; set; }
        public string foregroundState { get; set; }
        public ContextModeState contextMode { get; set; }
        public bool originalImageMode { get; set; }
        public ScreenshotSessionState screenshotSession { get; set; }
        public QuestionSessionState questionSession { get; set; }
        public string playback { get; set; }
        public RecentTaskState recentTask { get; set; }
        public string lastAction { get; set; }
        public string noticeCode { get; set; }

        internal static DesktopSemanticState CreateDefault()
        {
            return new DesktopSemanticState
            {
                schemaVersion = AutomationCatalog.StateSchemaVersion,
                revision = 0,
                foregroundState = "idle",
                contextMode = new ContextModeState { enabled = false, granularity = "sentence" },
                originalImageMode = false,
                screenshotSession = ScreenshotSessionState.Empty(),
                questionSession = new QuestionSessionState { open = false, turnCount = 0, answerAvailable = false, answerState = "waiting_input", phase = "closed" },
                playback = "stopped",
                recentTask = new RecentTaskState { available = false, taskId = null, displayState = "not-queried", decisionKind = null, decisionTargetToken = null },
                lastAction = null,
                noticeCode = null
            };
        }

        internal DesktopSemanticState Copy()
        {
            return new DesktopSemanticState
            {
                schemaVersion = schemaVersion,
                revision = revision,
                foregroundState = foregroundState,
                contextMode = new ContextModeState { enabled = contextMode.enabled, granularity = contextMode.granularity },
                originalImageMode = originalImageMode,
                screenshotSession = screenshotSession.Copy(),
                questionSession = new QuestionSessionState { open = questionSession.open, turnCount = questionSession.turnCount, answerAvailable = questionSession.answerAvailable, answerState = questionSession.answerState, answerArtifact = questionSession.answerArtifact == null ? null : questionSession.answerArtifact.Copy(), questionTargetToken = questionSession.questionTargetToken, taskId = questionSession.taskId, sessionId = questionSession.sessionId, phase = questionSession.phase },
                playback = playback,
                recentTask = new RecentTaskState { available = recentTask.available, taskId = recentTask.taskId, displayState = recentTask.displayState, decisionKind = recentTask.decisionKind, decisionTargetToken = recentTask.decisionTargetToken, attentionRequired = recentTask.attentionRequired, intent = recentTask.intent, questionTargetToken = recentTask.questionTargetToken, questionTurnCount = recentTask.questionTurnCount, questionAnswerRef = recentTask.questionAnswerRef == null ? null : recentTask.questionAnswerRef.Copy(), ocrHumanResolution = recentTask.ocrHumanResolution == null ? null : recentTask.ocrHumanResolution.Copy() },
                lastAction = lastAction,
                noticeCode = noticeCode
            };
        }
    }

    internal sealed class ContextModeState
    {
        public bool enabled { get; set; }
        public string granularity { get; set; }
    }

    internal sealed class ScreenshotSessionState
    {
        public bool active { get; set; }
        public string activeGroup { get; set; }
        public int questionImageCount { get; set; }
        public int answerImageCount { get; set; }
        public bool canContinue { get; set; }
        public bool canFinish { get; set; }
        public bool canCancel { get; set; }
        public bool canUndo { get; set; }

        internal static ScreenshotSessionState Empty()
        {
            return new ScreenshotSessionState { active = false, activeGroup = "question", questionImageCount = 0, answerImageCount = 0, canContinue = false, canFinish = false, canCancel = false, canUndo = false };
        }

        internal ScreenshotSessionState Copy()
        {
            return new ScreenshotSessionState { active = active, activeGroup = activeGroup, questionImageCount = questionImageCount, answerImageCount = answerImageCount, canContinue = canContinue, canFinish = canFinish, canCancel = canCancel, canUndo = canUndo };
        }
    }

    internal sealed class QuestionSessionState
    {
        public bool open { get; set; }
        public int turnCount { get; set; }
        public bool answerAvailable { get; set; }
        internal string taskId;
        internal string sessionId;
        internal string phase;
        internal string answerState;
        internal QuestionAnswerArtifactReference answerArtifact;
        internal string questionTargetToken;
    }

    internal sealed class QuestionSessionModel
    {
        private readonly QuestionSessionState state;

        internal QuestionSessionModel(QuestionSessionState state)
        {
            if (state == null) throw new System.ArgumentNullException("state");
            this.state = state;
        }

        internal string TaskId { get { return state.taskId; } }
        internal string SessionId { get { return state.sessionId; } }
        internal string Phase { get { return state.phase; } }
        internal int Turn { get { return state.turnCount; } }
        internal QuestionAnswerArtifactReference AnswerRef { get { return state.answerArtifact; } }
        internal string TargetToken { get { return state.questionTargetToken; } }
        internal bool CanSubmitInitial { get { return state.open && string.IsNullOrWhiteSpace(state.taskId) && state.phase == "composing"; } }
        internal bool CanSubmitFollowUp { get { return state.open && !string.IsNullOrWhiteSpace(state.taskId) && state.answerAvailable && state.phase == "answer_ready"; } }
        internal bool IsRunning { get { return state.phase == "submitting" || state.phase == "running"; } }

        internal void OpenNew(long revision)
        {
            state.open = true;
            state.taskId = null;
            state.sessionId = "question-session-" + revision.ToString("D8");
            state.phase = "composing";
            state.turnCount = 0;
            state.answerAvailable = false;
            state.answerState = "waiting_input";
            state.answerArtifact = null;
            state.questionTargetToken = null;
        }

        internal void BeginSubmission()
        {
            state.phase = "submitting";
            state.answerAvailable = false;
            state.answerState = "loading";
            state.answerArtifact = null;
            state.questionTargetToken = null;
        }

        internal void Accepted(string taskId, bool answerAvailable)
        {
            if (!string.IsNullOrWhiteSpace(taskId)) state.taskId = taskId;
            state.turnCount++;
            state.answerAvailable = answerAvailable;
            state.answerState = answerAvailable ? "waiting_input" : "loading";
            state.phase = answerAvailable ? "answer_ready" : "running";
        }

        internal void ApplyProjection(RecentTaskProjection projection)
        {
            if (projection == null || !projection.available || !string.Equals(projection.taskId, state.taskId, System.StringComparison.Ordinal))
            {
                state.answerAvailable = false;
                state.answerArtifact = null;
                state.questionTargetToken = null;
                state.answerState = "failed";
                state.phase = "failed";
                return;
            }
            if (projection.answerAvailable && projection.questionAnswerRef != null)
            {
                state.answerAvailable = true;
                state.answerState = "ready";
                state.answerArtifact = projection.questionAnswerRef.Copy();
                state.questionTargetToken = projection.questionTargetToken;
                state.turnCount = projection.questionTurnCount ?? state.turnCount;
                if (!string.IsNullOrWhiteSpace(projection.questionSessionRef)) state.sessionId = projection.questionSessionRef;
                state.phase = "answer_ready";
                return;
            }
            state.answerAvailable = false;
            state.answerArtifact = null;
            state.questionTargetToken = null;
            if (projection.displayState == "failed") { state.answerState = "failed"; state.phase = "failed"; }
            else if (projection.displayState == "running" || projection.displayState == "queued") { state.answerState = "loading"; state.phase = "running"; }
            else { state.answerState = "waiting_input"; state.phase = "waiting_input"; }
        }

        internal void RejectSubmission()
        {
            state.answerState = "failed";
            state.phase = "failed";
        }

        internal void Cancel()
        {
            state.open = false;
            state.answerAvailable = false;
            state.phase = "cancelled";
        }
    }

    internal sealed class RecentTaskState
    {
        public bool available { get; set; }
        public string taskId { get; set; }
        public string displayState { get; set; }
        internal string decisionKind;
        internal string decisionTargetToken;
        internal bool attentionRequired;
        internal string intent;
        internal string questionTargetToken;
        internal int? questionTurnCount;
        internal QuestionAnswerArtifactReference questionAnswerRef;
        internal OcrHumanResolutionProjection ocrHumanResolution;
    }
}

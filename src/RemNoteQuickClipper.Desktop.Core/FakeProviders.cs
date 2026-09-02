using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class FakeCaptureProvider : ICaptureProvider
    {
        private readonly Queue<CaptureResult> results = new Queue<CaptureResult>();
        internal int CallCount { get; private set; }

        internal FakeCaptureProvider(IEnumerable<CaptureResult> scripted)
        {
            if (scripted != null) foreach (var item in scripted) results.Enqueue(item);
        }

        public CaptureResult Capture(string intent, ContextModeState contextMode)
        {
            CallCount++;
            if (results.Count == 0) return new CaptureResult { hasContent = false, source = "clipboard-text", artifacts = new List<CaptureArtifact>() };
            return results.Dequeue();
        }
    }

    internal sealed class FakeScreenshotProvider : IScreenshotProvider
    {
        private readonly Queue<ScreenshotResult> results = new Queue<ScreenshotResult>();
        internal readonly List<string> RequestedGroups = new List<string>();

        internal FakeScreenshotProvider(IEnumerable<ScreenshotResult> scripted)
        {
            if (scripted != null) foreach (var item in scripted) results.Enqueue(item);
        }

        public ScreenshotResult Capture(string group)
        {
            RequestedGroups.Add(group);
            if (results.Count == 0) return new ScreenshotResult { outcome = "cancelled", noticeCode = "fake.screenshot-script-exhausted" };
            return results.Dequeue();
        }
    }

    internal sealed class FakeDesktopOrchestratorPort : IDesktopOrchestratorPort
    {
        internal readonly List<CaptureBundleV1> SubmittedBundles = new List<CaptureBundleV1>();
        internal readonly List<string> Questions = new List<string>();
        internal readonly List<string> CardStructureRefs = new List<string>();
        internal readonly List<string> LanguagePracticeInputs = new List<string>();
        internal readonly List<string> OcrHumanResolutionInputs = new List<string>();
        private int taskCounter;

        internal bool AcceptSubmissions = true;
        internal bool AnswerImmediately = true;
        internal string ReplayState = "playing";
        internal string StopState = "stopped";
        internal RecentTaskProjection RecentTask = new RecentTaskProjection { available = false, taskId = null, displayState = "none" };

        public SubmissionResult SubmitCapture(CaptureBundleV1 bundle)
        {
            SubmittedBundles.Add(bundle);
            return Result("capture");
        }

        public QuestionResult SubmitQuestion(string text, bool followUp, string taskId)
        {
            Questions.Add((followUp ? "follow-up:" : "question:") + text);
            if (!followUp || string.IsNullOrWhiteSpace(taskId)) { taskCounter++; taskId = "fake-question-task-" + taskCounter; }
            return new QuestionResult { accepted = AcceptSubmissions, answerAvailable = AcceptSubmissions && AnswerImmediately, taskId = taskId, noticeCode = AcceptSubmissions ? "fake.accepted" : "fake.offline" };
        }

        public SubmissionResult MakeCards(string taskId, string cardStructureRef)
        {
            CardStructureRefs.Add(cardStructureRef);
            return new SubmissionResult { accepted = AcceptSubmissions, taskId = taskId, noticeCode = AcceptSubmissions ? "fake.accepted" : "fake.offline" };
        }

        public SubmissionResult ProvideLanguagePracticeInput(string taskId, string decisionTargetToken, string selection)
        {
            LanguagePracticeInputs.Add(taskId + ":" + decisionTargetToken + ":" + selection);
            return Result("language-practice");
        }

        public SubmissionResult ProvideOcrHumanResolution(string taskId, string decisionTargetToken, string action, string correctedText, string candidateText)
        {
            OcrHumanResolutionInputs.Add(taskId + ":" + decisionTargetToken + ":" + action + ":" + correctedText + ":" + candidateText);
            return Result("ocr-human-resolution");
        }

        public PlaybackResult Replay() { return new PlaybackResult { accepted = true, state = ReplayState, noticeCode = "fake.playback-replay" }; }
        public PlaybackResult StopPlayback() { return new PlaybackResult { accepted = true, state = StopState, noticeCode = "fake.playback-stop" }; }
        public RecentTaskProjection QueryRecentTask() { return RecentTask; }
        public RecentTaskProjection QueryTask(string taskId) { return RecentTask != null && RecentTask.available && RecentTask.taskId == taskId ? RecentTask : new RecentTaskProjection { available = false, taskId = taskId, displayState = "unknown" }; }

        private SubmissionResult Result(string prefix)
        {
            taskCounter++;
            return new SubmissionResult { accepted = AcceptSubmissions, taskId = "fake-" + prefix + "-task-" + taskCounter, noticeCode = AcceptSubmissions ? "fake.accepted" : "fake.offline" };
        }
    }

    internal sealed class FakeClock : IClock
    {
        internal FakeClock(DateTime value) { UtcNow = value; }
        public DateTime UtcNow { get; private set; }
    }

    internal sealed class FakeIdProvider : IIdProvider
    {
        private int next;
        public string NewCaptureId() { next++; return "fake-capture-" + next.ToString("D4"); }
    }
}

using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Desktop.Core
{
    internal interface ICaptureProvider
    {
        CaptureResult Capture(string intent, ContextModeState contextMode);
    }

    internal interface IScreenshotProvider
    {
        ScreenshotResult Capture(string group);
    }

    internal interface IDesktopOrchestratorPort
    {
        SubmissionResult SubmitCapture(CaptureBundleV1 bundle);
        QuestionResult SubmitQuestion(string text, bool followUp, string taskId);
        SubmissionResult MakeCards(string taskId, string cardStructureRef);
        SubmissionResult ProvideLanguagePracticeInput(string taskId, string decisionTargetToken, string selection);
        SubmissionResult ProvideOcrHumanResolution(string taskId, string decisionTargetToken, string action, string correctedText, string candidateText);
        PlaybackResult Replay();
        PlaybackResult StopPlayback();
        RecentTaskProjection QueryRecentTask();
        RecentTaskProjection QueryTask(string taskId);
    }

    internal interface IClock
    {
        DateTime UtcNow { get; }
    }

    internal interface IIdProvider
    {
        string NewCaptureId();
    }

    internal sealed class CaptureResult
    {
        public bool hasContent { get; set; }
        public string source { get; set; }
        public List<CaptureArtifact> artifacts { get; set; }
        public string selectedTextArtifactRef { get; set; }
        public bool contextAvailable { get; set; }
        public ContextSegments contextSegments { get; set; }
    }

    internal sealed class ScreenshotResult
    {
        public string outcome { get; set; }
        public CaptureArtifact artifact { get; set; }
        public string noticeCode { get; set; }
    }

    internal sealed class SubmissionResult
    {
        public bool accepted { get; set; }
        public string taskId { get; set; }
        public string noticeCode { get; set; }
    }

    internal sealed class QuestionResult
    {
        public bool accepted { get; set; }
        public bool answerAvailable { get; set; }
        public string taskId { get; set; }
        public string noticeCode { get; set; }
    }

    internal sealed class PlaybackResult
    {
        public bool accepted { get; set; }
        public string state { get; set; }
        public string noticeCode { get; set; }
    }

    internal sealed class RecentTaskProjection
    {
        public bool available { get; set; }
        public string taskId { get; set; }
        public string displayState { get; set; }
        public string decisionKind { get; set; }
        public string decisionTargetToken { get; set; }
        public bool attentionRequired { get; set; }
        public string intent { get; set; }
        public bool answerAvailable { get; set; }
        public string questionTargetToken { get; set; }
        public int? questionTurnCount { get; set; }
        public QuestionAnswerArtifactReference questionAnswerRef { get; set; }
        public string questionSessionRef { get; set; }
        public OcrHumanResolutionProjection ocrHumanResolution { get; set; }
    }

    internal sealed class OcrHumanResolutionProjection
    {
        public string decisionKind { get; set; }
        public string targetToken { get; set; }
        public QuestionAnswerArtifactReference sourceProofreadRef { get; set; }
        public QuestionAnswerArtifactReference originalTextRef { get; set; }
        public QuestionAnswerArtifactReference candidateTextRef { get; set; }
        public string diffJson { get; set; }
        public string reasonJson { get; set; }
        public List<string> allowedActions { get; set; }

        internal OcrHumanResolutionProjection Copy()
        {
            return new OcrHumanResolutionProjection
            {
                decisionKind = decisionKind,
                targetToken = targetToken,
                sourceProofreadRef = sourceProofreadRef == null ? null : sourceProofreadRef.Copy(),
                originalTextRef = originalTextRef == null ? null : originalTextRef.Copy(),
                candidateTextRef = candidateTextRef == null ? null : candidateTextRef.Copy(),
                diffJson = diffJson,
                reasonJson = reasonJson,
                allowedActions = allowedActions == null ? new List<string>() : new List<string>(allowedActions)
            };
        }
    }

    internal sealed class SemanticActionEnvelope
    {
        public string schemaVersion { get; set; }
        public string requestId { get; set; }
        public string action { get; set; }
        public SemanticActionArguments arguments { get; set; }
    }

    internal sealed class SemanticActionArguments
    {
        public bool? enabled { get; set; }
        public string granularity { get; set; }
        public string text { get; set; }
        public string cardStructureRef { get; set; }
        public string selection { get; set; }
        public string candidateText { get; set; }
    }
}

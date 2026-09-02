using System;
using System.Collections.Generic;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class SpeechRequestV1
    {
        public string schemaVersion { get; set; }
        public string requestId { get; set; }
        public string operationId { get; set; }
        public SpokenTextReference spokenText { get; set; }
        public string contextMode { get; set; }
        public LanguagePolicy language { get; set; }
        public VoiceSnapshot voiceSnapshot { get; set; }
        public PlaybackPolicy playbackPolicy { get; set; }
        public CachePolicy cachePolicy { get; set; }
    }

    internal sealed class SpokenTextReference
    {
        public string artifactRef { get; set; }
        public string path { get; set; }
        public string sha256 { get; set; }
        public long bytes { get; set; }
        public string encoding { get; set; }
        public string mediaType { get; set; }
    }

    internal sealed class LanguagePolicy
    {
        public string strategy { get; set; }
        public string @override { get; set; }
    }

    internal sealed class VoiceSnapshot
    {
        public string provider { get; set; }
        public Dictionary<string, string> voicesByLanguage { get; set; }
        public int ratePercent { get; set; }
        public int volumePercent { get; set; }
        public int pitchHz { get; set; }
    }

    internal sealed class PlaybackPolicy
    {
        public bool autoPlay { get; set; }
        public string activePlayback { get; set; }
        public string replay { get; set; }
        public string naturalCompletion { get; set; }
        public string stopTerminal { get; set; }
    }

    internal sealed class CachePolicy
    {
        public string scope { get; set; }
        public bool reuseSuccessfulAudio { get; set; }
        public bool crossTaskReuse { get; set; }
    }

    internal sealed class ValidatedSpeechRequest
    {
        public WorkerRequestEnvelopeV1 Envelope { get; set; }
        public SpeechRequestV1 Request { get; set; }
        public string SpokenTextPath { get; set; }
        public string SpokenText { get; set; }
        public string SpokenTextSha256 { get; set; }
    }

    internal sealed class SpeechRoute
    {
        public string LanguageTag { get; set; }
        public string Voice { get; set; }
        public string Strategy { get; set; }
    }

    internal sealed class WorkerRequestEnvelopeV1
    {
        public string schemaVersion { get; set; }
        public string requestId { get; set; }
        public string operationId { get; set; }
        public string taskId { get; set; }
        public string runId { get; set; }
        public string stageId { get; set; }
        public string attemptId { get; set; }
        public string workerId { get; set; }
        public string operation { get; set; }
        public string idempotencyKey { get; set; }
        public string attemptPath { get; set; }
        public string outputPath { get; set; }
        public string progressPath { get; set; }
        public string resultPath { get; set; }
        public WorkerCancelSignalReferenceV1 cancelSignal { get; set; }
        public List<ArtifactReferenceV1> inputRefs { get; set; }
        public Dictionary<string, object> configSnapshot { get; set; }
        public SpeechRequestV1 payload { get; set; }
    }

    internal sealed class WorkerResultV1
    {
        public string schemaVersion { get; set; }
        public string requestId { get; set; }
        public string operationId { get; set; }
        public string taskId { get; set; }
        public string runId { get; set; }
        public string stageId { get; set; }
        public string attemptId { get; set; }
        public WorkerIdentityV1 worker { get; set; }
        public string contractVersion { get; set; }
        public string idempotencyKey { get; set; }
        public string status { get; set; }
        public List<ArtifactReferenceV1> outputs { get; set; }
        public WorkerErrorV1 error { get; set; }
        public string startedAt { get; set; }
        public string finishedAt { get; set; }
        public List<string> warnings { get; set; }
        public Dictionary<string, object> payload { get; set; }
    }

    internal sealed class WorkerIdentityV1
    {
        public string id { get; set; }
        public string version { get; set; }
    }

    internal sealed class ArtifactReferenceV1
    {
        public string artifactRef { get; set; }
        public string kind { get; set; }
        public string contractVersion { get; set; }
        public string manifestPath { get; set; }
        public string expectedSha256 { get; set; }
    }

    internal sealed class PublishedArtifact
    {
        public ArtifactReferenceV1 Reference { get; set; }
        public string PayloadPath { get; set; }
        public string PayloadSha256 { get; set; }
        public long PayloadBytes { get; set; }
    }

    internal sealed class ArtifactManifestV1
    {
        public string schemaVersion { get; set; }
        public string artifactId { get; set; }
        public string kind { get; set; }
        public string contractVersion { get; set; }
        public string mediaType { get; set; }
        public List<HashedFileV1> files { get; set; }
        public string createdAt { get; set; }
        public ArtifactProducerV1 producer { get; set; }
        public List<ArtifactReferenceV1> inputRefs { get; set; }
        public string dataClass { get; set; }
        public Dictionary<string, object> metadata { get; set; }
    }

    internal sealed class HashedFileV1
    {
        public string path { get; set; }
        public string sha256 { get; set; }
        public long bytes { get; set; }
        public string mediaType { get; set; }
    }

    internal sealed class ArtifactProducerV1
    {
        public string stageId { get; set; }
        public string workerId { get; set; }
        public string workerVersion { get; set; }
        public string attemptId { get; set; }
    }

    internal sealed class WorkerErrorV1
    {
        public string code { get; set; }
        public string category { get; set; }
        public bool retryable { get; set; }
        public string detailsRef { get; set; }
        public string message { get; set; }
        public string blame { get; set; }
    }

    internal sealed class PlaybackWorkItem
    {
        public string RequestId { get; set; }
        public string OperationId { get; set; }
        public string TaskId { get; set; }
        public string RunId { get; set; }
        public string StageId { get; set; }
        public string AttemptId { get; set; }
        public string WorkerId { get; set; }
        public string IdempotencyKey { get; set; }
        public string AudioPath { get; set; }
        public string AudioSha256 { get; set; }
        public long AudioBytes { get; set; }
        public string LanguageTag { get; set; }
        public string VoiceId { get; set; }
        public string CancellationSignalPath { get; set; }
        public string CancellationSignalRef { get; set; }
        public int CancellationGracePeriodMs { get; set; }
    }

    internal sealed class PlaybackTerminal
    {
        public string Status { get; set; }
        public int? ExitCode { get; set; }
        public string Message { get; set; }
    }

    internal sealed class PlaybackCancellationSignalV1
    {
        public string schemaVersion { get; set; }
        public string attemptId { get; set; }
        public string reason { get; set; }
        public string requestedAt { get; set; }
        public string graceDeadline { get; set; }
        public string commandId { get; set; }
        public string operationId { get; set; }
    }

    internal sealed class WorkerCancelSignalReferenceV1
    {
        public string schemaVersion { get; set; }
        public string path { get; set; }
        public int gracePeriodMs { get; set; }
    }

    internal sealed class WorkerRunContext
    {
        public string RequestPath { get; set; }
        public string JobRoot { get; set; }
        public string AttemptDirectory { get; set; }
        public WorkerRequestEnvelopeV1 Envelope { get; set; }
    }
}

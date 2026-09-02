using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class PlaybackRequestReader
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };

        public PlaybackWorkItem ReadAndValidate(WorkerRunContext context)
        {
            var envelope = context.Envelope;
            SpeechRequestReader.ValidateEnvelope(context.JobRoot, context.RequestPath, envelope, "speech-playback", "speech.playback");
            if (envelope.inputRefs.Count != 1 || envelope.inputRefs[0] == null || envelope.inputRefs[0].kind != "audio.speech" || envelope.inputRefs[0].contractVersion != "1.0.0")
                throw new SpeechContractException("invalid_playback_input", "speech.playback 必须且只能引用一个 audio.speech artifact");
            var reference = envelope.inputRefs[0];
            var manifestPath = SpeechRequestReader.ResolveJobRelativePath(context.JobRoot, reference.manifestPath);
            if (!LongPathFileSystem.FileExists(manifestPath)) throw new SpeechContractException("missing_audio_manifest", "音频 artifact manifest 不存在");
            if (!SpeechRequestReader.HashFile(manifestPath).Equals(reference.expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("audio_manifest_hash_mismatch", "音频 artifact manifest SHA-256 校验失败");

            ArtifactManifestV1 manifest;
            try { manifest = Json.Deserialize<ArtifactManifestV1>(LongPathFileSystem.ReadAllText(manifestPath, new UTF8Encoding(false, true))); }
            catch (Exception ex) { throw new SpeechContractException("invalid_audio_manifest", "音频 artifact manifest 无法解析", ex); }
            if (manifest == null || manifest.schemaVersion != "1.0.0" || manifest.kind != "audio.speech" || manifest.contractVersion != "1.0.0" || manifest.mediaType != "audio/mpeg")
                throw new SpeechContractException("invalid_audio_manifest", "音频 artifact manifest 契约或 kind 无效");
            if (manifest.artifactId != reference.artifactRef || manifest.files == null || manifest.files.Count != 1)
                throw new SpeechContractException("invalid_audio_manifest", "音频 artifact 标识或文件数量无效");
            var audio = manifest.files[0];
            if (audio == null || audio.mediaType != "audio/mpeg" || audio.bytes < 1)
                throw new SpeechContractException("invalid_audio_manifest", "音频 payload 元数据无效");
            var audioPath = SpeechRequestReader.ResolveJobRelativePath(context.JobRoot, audio.path);
            if (!LongPathFileSystem.FileExists(audioPath)) throw new SpeechContractException("missing_audio", "音频 payload 不存在");
            if (LongPathFileSystem.FileLength(audioPath) != audio.bytes) throw new SpeechContractException("audio_bytes_mismatch", "音频 bytes 校验失败");
            if (!SpeechRequestReader.HashFile(audioPath).Equals(audio.sha256, StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("audio_hash_mismatch", "音频 SHA-256 校验失败");

            return new PlaybackWorkItem
            {
                RequestId = envelope.requestId, OperationId = envelope.operationId, TaskId = envelope.taskId, RunId = envelope.runId,
                StageId = envelope.stageId, AttemptId = envelope.attemptId, WorkerId = envelope.workerId, IdempotencyKey = envelope.idempotencyKey,
                AudioPath = audio.path, AudioBytes = audio.bytes, AudioSha256 = audio.sha256,
                LanguageTag = MetadataString(manifest.metadata, "languageTag", "en-US"),
                VoiceId = MetadataString(manifest.metadata, "voiceId", "unavailable"),
                CancellationSignalPath = WorkerCli.CancellationSignalPath(context),
                CancellationSignalRef = envelope.cancelSignal.path,
                CancellationGracePeriodMs = envelope.cancelSignal.gracePeriodMs
            };
        }

        private static string MetadataString(Dictionary<string, object> metadata, string name, string fallback)
        {
            object value;
            var text = metadata != null && metadata.TryGetValue(name, out value) ? value as string : null;
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
    }

    internal static class PlaybackCancellationMonitor
    {
        public static Task<string> WaitForReasonAsync(string signalPath, string attemptId, string operationId, int gracePeriodMs, Task playbackCompletion)
        {
            return Task.Run(async delegate
            {
                while (!playbackCompletion.IsCompleted)
                {
                    if (LongPathFileSystem.FileExists(signalPath)) return ReadReason(signalPath, attemptId, operationId, gracePeriodMs);
                    await Task.Delay(50);
                }
                return null;
            });
        }

        internal static string ReadReason(string signalPath, string expectedAttemptId, string expectedOperationId, int gracePeriodMs)
        {
            PlaybackCancellationSignalV1 signal;
            try
            {
                signal = new JavaScriptSerializer().Deserialize<PlaybackCancellationSignalV1>(
                    LongPathFileSystem.ReadAllText(signalPath, new UTF8Encoding(false, true)));
            }
            catch (Exception ex) { throw new SpeechContractException("invalid_cancellation_signal", "取消信号无法解析", ex); }
            DateTimeOffset requestedAt;
            DateTimeOffset graceDeadline;
            if (signal == null || signal.schemaVersion != "1.0.0" || signal.attemptId != expectedAttemptId || signal.operationId != expectedOperationId ||
                !DateTimeOffset.TryParse(signal.requestedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out requestedAt) ||
                !DateTimeOffset.TryParse(signal.graceDeadline, null, System.Globalization.DateTimeStyles.RoundtripKind, out graceDeadline) ||
                graceDeadline - requestedAt != TimeSpan.FromMilliseconds(gracePeriodMs) ||
                (signal.reason != "user_stop" && signal.reason != "superseded" && signal.reason != "task_cancel" && signal.reason != "timeout"))
                throw new SpeechContractException("invalid_cancellation_reason", "取消信号必须匹配 attempt/operation/grace deadline，且 reason 为 worker-cli-v1 四值之一");
            return signal.reason;
        }
    }
}

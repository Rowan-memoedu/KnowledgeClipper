using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class SpeechRequestReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public ValidatedSpeechRequest ReadAndValidate(string jobRoot, string requestPath)
        {
            if (string.IsNullOrWhiteSpace(jobRoot)) throw new SpeechContractException("invalid_job_root", "job root 不能为空");
            if (!LongPathFileSystem.DirectoryExists(jobRoot)) throw new SpeechContractException("missing_job_root", "job root 不存在");
            if (!LongPathFileSystem.FileExists(requestPath)) throw new SpeechContractException("missing_request", "request.json 不存在");

            WorkerRequestEnvelopeV1 envelope;
            try
            {
                envelope = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }
                    .Deserialize<WorkerRequestEnvelopeV1>(LongPathFileSystem.ReadAllText(requestPath, StrictUtf8));
            }
            catch (Exception ex)
            {
                throw new SpeechContractException("invalid_request_json", "SpeechRequest JSON 无法解析", ex);
            }

            ValidateEnvelope(jobRoot, requestPath, envelope, "speech-synthesize", "speech.synthesize");
            var request = envelope.payload;
            ValidateRequestShape(request);
            if (request.requestId != envelope.requestId || request.operationId != envelope.operationId)
                throw new SpeechContractException("request_identity_mismatch", "worker-request 与 SpeechRequest payload 的标识不一致");
            var spokenPath = ResolveJobRelativePath(jobRoot, request.spokenText.path);
            if (!LongPathFileSystem.FileExists(spokenPath)) throw new SpeechContractException("missing_spoken_text", "spokenText 工件不存在");
            var length = LongPathFileSystem.FileLength(spokenPath);
            if (length != request.spokenText.bytes) throw new SpeechContractException("spoken_text_bytes_mismatch", "spokenText bytes 校验失败");
            var actualHash = HashFile(spokenPath);
            if (!actualHash.Equals(request.spokenText.sha256, StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("spoken_text_hash_mismatch", "spokenText SHA-256 校验失败");

            string text;
            try { text = StrictUtf8.GetString(LongPathFileSystem.ReadAllBytes(spokenPath)); }
            catch (DecoderFallbackException ex) { throw new SpeechContractException("invalid_spoken_text_utf8", "spokenText 不是严格 UTF-8", ex); }
            if (string.IsNullOrWhiteSpace(text)) throw new SpeechContractException("empty_spoken_text", "spokenText 为空");

            return new ValidatedSpeechRequest
            {
                Envelope = envelope,
                Request = request,
                SpokenTextPath = spokenPath,
                SpokenText = text,
                SpokenTextSha256 = actualHash
            };
        }

        internal static WorkerRequestEnvelopeV1 TryReadEnvelope(string requestPath)
        {
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }
                    .Deserialize<WorkerRequestEnvelopeV1>(LongPathFileSystem.ReadAllText(requestPath, StrictUtf8));
            }
            catch { return null; }
        }

        internal static void ValidateRequestShape(SpeechRequestV1 request)
        {
            if (request == null) throw new SpeechContractException("empty_request", "SpeechRequest 为空");
            if (request.schemaVersion != "1.0.0") throw new SpeechContractException("unsupported_schema_version", "只支持 SpeechRequest 1.0.0");
            RequireId(request.requestId, "requestId");
            RequireId(request.operationId, "operationId");
            if (request.spokenText == null) throw new SpeechContractException("missing_spoken_text_ref", "缺少 spokenText");
            RequireId(request.spokenText.artifactRef, "spokenText.artifactRef");
            if (string.IsNullOrWhiteSpace(request.spokenText.path)) throw new SpeechContractException("missing_spoken_text_path", "缺少 spokenText.path");
            if (!Regex.IsMatch(request.spokenText.sha256 ?? "", "^[0-9a-fA-F]{64}$")) throw new SpeechContractException("invalid_spoken_text_hash", "spokenText.sha256 必须为 64 位十六进制");
            if (request.spokenText.bytes < 1) throw new SpeechContractException("invalid_spoken_text_bytes", "spokenText.bytes 必须大于 0");
            if (!string.Equals(request.spokenText.encoding, "utf-8", StringComparison.OrdinalIgnoreCase)) throw new SpeechContractException("invalid_spoken_text_encoding", "spokenText.encoding 必须为 utf-8");
            if (!string.Equals(request.spokenText.mediaType, "text/plain", StringComparison.OrdinalIgnoreCase)) throw new SpeechContractException("invalid_spoken_text_media_type", "spokenText.mediaType 必须为 text/plain");
            if (request.contextMode != "selection_only" && request.contextMode != "selection_plus_sentence" && request.contextMode != "selection_plus_paragraph")
                throw new SpeechContractException("invalid_context_mode", "contextMode 无效");
            VoiceRouter.ValidatePolicies(request);
        }

        internal static void ValidateEnvelope(string jobRoot, string requestPath, WorkerRequestEnvelopeV1 envelope, string expectedStageId, string expectedOperation)
        {
            if (envelope == null) throw new SpeechContractException("empty_worker_request", "worker-request 为空");
            if (envelope.schemaVersion != "1.0.0") throw new SpeechContractException("unsupported_worker_request", "只支持 worker-request 1.0.0");
            RequireId(envelope.requestId, "requestId"); RequireId(envelope.operationId, "operationId");
            RequireId(envelope.taskId, "taskId"); RequireId(envelope.runId, "runId"); RequireId(envelope.stageId, "stageId");
            RequireId(envelope.attemptId, "attemptId"); RequireId(envelope.workerId, "workerId"); RequireId(envelope.operation, "operation");
            if (!Regex.IsMatch(envelope.idempotencyKey ?? "", "^[0-9a-fA-F]{64}$")) throw new SpeechContractException("invalid_idempotency_key", "idempotencyKey 无效");
            if (envelope.stageId != expectedStageId || envelope.workerId != "speech.edge" || envelope.operation != expectedOperation)
                throw new SpeechContractException("wrong_worker_operation", "请求的 worker、stage 或 operation 不匹配");
            if (expectedOperation == "speech.synthesize" && envelope.payload == null) throw new SpeechContractException("missing_speech_payload", "缺少 SpeechRequest payload");
            if (envelope.inputRefs == null || envelope.configSnapshot == null) throw new SpeechContractException("incomplete_worker_request", "inputRefs/configSnapshot 缺失");
            foreach (var reference in envelope.inputRefs)
            {
                if (reference == null) throw new SpeechContractException("invalid_input_ref", "inputRefs 包含空引用");
                RequireId(reference.artifactRef, "inputRefs.artifactRef");
                if (!Regex.IsMatch(reference.kind ?? "", "^[a-z0-9][a-z0-9._-]{0,127}$")) throw new SpeechContractException("invalid_input_ref_kind", "inputRefs.kind 无效");
                if (!Regex.IsMatch(reference.contractVersion ?? "", "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$")) throw new SpeechContractException("invalid_input_ref_version", "inputRefs.contractVersion 无效");
                if (!Regex.IsMatch(reference.expectedSha256 ?? "", "^[0-9a-fA-F]{64}$")) throw new SpeechContractException("invalid_input_ref_hash", "inputRefs.expectedSha256 无效");
                ResolveJobRelativePath(jobRoot, reference.manifestPath);
            }
            var attempt = ResolveJobRelativePath(jobRoot, envelope.attemptPath);
            var actualAttempt = LongPathFileSystem.GetFullPath(Path.GetDirectoryName(requestPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!attempt.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(actualAttempt, StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("attempt_path_mismatch", "attemptPath 与 request.json 位置不一致");
            if (new DirectoryInfo(actualAttempt).Name != envelope.attemptId) throw new SpeechContractException("attempt_id_mismatch", "attemptId 与目录名不一致");
            RequireExactPath(jobRoot, envelope.outputPath, Path.Combine(actualAttempt, "outputs"), "outputPath");
            RequireExactPath(jobRoot, envelope.progressPath, Path.Combine(actualAttempt, "progress.ndjson"), "progressPath");
            RequireExactPath(jobRoot, envelope.resultPath, Path.Combine(actualAttempt, "result.json"), "resultPath");
            if (envelope.cancelSignal == null || envelope.cancelSignal.schemaVersion != "1.0.0" || envelope.cancelSignal.gracePeriodMs != 2000)
                throw new SpeechContractException("invalid_cancellation_signal", "cancelSignal 必须使用 1.0.0 与固定 2000ms grace period");
            var signal = ResolveJobRelativePath(jobRoot, envelope.cancelSignal.path);
            if (!signal.StartsWith(actualAttempt + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("unsafe_cancellation_signal", "cancelSignal.path 必须位于当前 attempt 内");
        }

        private static void RequireExactPath(string jobRoot, string relative, string expected, string field)
        {
            var actual = ResolveJobRelativePath(jobRoot, relative);
            if (!actual.Equals(LongPathFileSystem.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new SpeechContractException("worker_path_mismatch", field + " 与 attempt 布局不一致");
        }

        internal static string ResolveJobRelativePath(string jobRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new SpeechContractException("unsafe_artifact_path", "工件路径必须相对 job 根");
            var segments = relativePath.Replace('\\', '/').Split('/');
            foreach (var segment in segments)
                if (segment.Length == 0 || segment == "." || segment == "..")
                    throw new SpeechContractException("unsafe_artifact_path", "工件路径不能包含空段、. 或 ..");
            var root = LongPathFileSystem.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = LongPathFileSystem.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new SpeechContractException("unsafe_artifact_path", "工件路径越出 job 根");
            return full;
        }

        internal static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = LongPathFileSystem.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static void RequireId(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
                throw new SpeechContractException("invalid_identifier", name + " 无效");
        }
    }

    internal sealed class SpeechContractException : Exception
    {
        public string Code { get; private set; }
        public SpeechContractException(string code, string message) : base(message) { Code = code; }
        public SpeechContractException(string code, string message, Exception inner) : base(message, inner) { Code = code; }
    }
}

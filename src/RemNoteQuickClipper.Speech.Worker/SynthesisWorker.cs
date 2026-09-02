using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class SynthesisWorker
    {
        private readonly SpeechRequestReader reader;
        private readonly ISpeechSynthesizer synthesizer;

        public SynthesisWorker(SpeechRequestReader reader, ISpeechSynthesizer synthesizer)
        {
            this.reader = reader;
            this.synthesizer = synthesizer;
        }

        public async Task<WorkerResultV1> RunAsync(string jobRoot, string attemptDirectory)
        {
            var store = new AttemptStore(jobRoot, attemptDirectory);
            var startedAt = DateTime.UtcNow;
            var envelope = SpeechRequestReader.TryReadEnvelope(store.RequestPath);
            try
            {
                store.AppendProgress("validating", "校验 SpeechRequest 与 spokenText 工件");
                var validated = reader.ReadAndValidate(jobRoot, store.RequestPath);
                envelope = validated.Envelope;
                var request = validated.Request;
                var route = VoiceRouter.Route(validated);
                store.AppendProgress("synthesizing", route.LanguageTag + " / " + route.Voice);
                using (var workspace = ShortSynthesisWorkspace.Create(validated.SpokenTextPath))
                {
                    var process = await synthesizer.SynthesizeAsync(workspace.TextPath, workspace.MediaPath, route, request.voiceSnapshot);
                    if (process.ExitCode != 0) throw new SpeechWorkerException("edge_tts_failed", SanitizeProcessMessage(process), true);
                    if (!LongPathFileSystem.FileExists(workspace.MediaPath) || LongPathFileSystem.FileLength(workspace.MediaPath) == 0) throw new SpeechWorkerException("invalid_audio", "Edge TTS 未生成有效音频", true);
                    var synthesizedBytes = LongPathFileSystem.FileLength(workspace.MediaPath);
                    var synthesizedHash = SpeechRequestReader.HashFile(workspace.MediaPath);
                    var audio = store.PublishArtifact(
                        "speech-audio", "audio.speech", workspace.MediaPath, "payload.mp3", "audio/mpeg", envelope,
                        new Dictionary<string, object> { { "languageTag", route.LanguageTag }, { "voiceId", route.Voice } });
                    if (audio.PayloadBytes != synthesizedBytes || !audio.PayloadSha256.Equals(synthesizedHash, StringComparison.OrdinalIgnoreCase))
                        throw new SpeechWorkerException("published_audio_integrity_mismatch", "发布音频与已校验合成结果不一致", false);
                    var result = SuccessResult(envelope, startedAt, route, audio);
                    store.WriteResultOnce(result);
                    try { store.AppendProgress("audio_ready", audio.PayloadSha256); } catch { }
                    return result;
                }
            }
            catch (Exception ex)
            {
                var result = FailureResult(store, envelope, startedAt, ex);
                try { store.WriteResultOnce(result); } catch (SpeechContractException immutable) { if (immutable.Code != "immutable_file_exists") throw; }
                try { store.AppendProgress("failed", result.error.code); } catch { }
                return result;
            }
        }

        internal static WorkerResultV1 RunRejected(string jobRoot, string attemptDirectory, WorkerRequestEnvelopeV1 envelope, Exception exception)
        {
            var store = new AttemptStore(jobRoot, attemptDirectory);
            var result = FailureResult(store, envelope, DateTime.UtcNow, exception);
            store.WriteResultOnce(result);
            try { store.AppendProgress("rejected", result.error.code); } catch { }
            return result;
        }

        private static WorkerResultV1 SuccessResult(WorkerRequestEnvelopeV1 envelope, DateTime startedAt, SpeechRoute route, PublishedArtifact audio)
        {
            return new WorkerResultV1
            {
                schemaVersion = "1.0.0", requestId = envelope.requestId, operationId = envelope.operationId,
                taskId = envelope.taskId, runId = envelope.runId, stageId = envelope.stageId, attemptId = envelope.attemptId,
                worker = new WorkerIdentityV1 { id = envelope.workerId, version = WorkerCli.WorkerVersion }, contractVersion = "1.0.0",
                idempotencyKey = envelope.idempotencyKey, status = "completed",
                outputs = new List<ArtifactReferenceV1> { audio.Reference }, error = null,
                startedAt = startedAt.ToString("o"), finishedAt = DateTime.UtcNow.ToString("o"), warnings = new List<string>(),
                payload = new Dictionary<string, object>
                {
                    { "operationId", envelope.operationId }, { "languageTag", route.LanguageTag }, { "voiceId", route.Voice },
                    { "cacheHit", false }, { "playbackState", "idle_replayable" }
                }
            };
        }

        private static WorkerResultV1 FailureResult(AttemptStore store, WorkerRequestEnvelopeV1 envelope, DateTime startedAt, Exception ex)
        {
            var contract = ex as SpeechContractException;
            var worker = ex as SpeechWorkerException;
            var missingRuntime = contract != null && contract.Code == "missing_runtime_tool";
            var code = contract != null ? contract.Code : worker != null ? worker.Code : ex is TimeoutException ? "synthesis_timeout" : "synthesis_failed";
            var retryable = worker != null ? worker.Retryable : ex is TimeoutException || ex is IOException;
            var message = Sanitize(ex.Message);
            string detailsRef;
            try { detailsRef = store.WriteErrorDetailsOnce(code, message); }
            catch { detailsRef = MakeFallbackProgressRef(store); }
            envelope = envelope ?? UnknownEnvelope(store.AttemptId);
            return new WorkerResultV1
            {
                schemaVersion = "1.0.0", requestId = SafeId(envelope.requestId, "unknown-request"), operationId = SafeId(envelope.operationId, "unknown-operation"),
                taskId = SafeId(envelope.taskId, "unknown-task"), runId = SafeId(envelope.runId, "unknown-run"), stageId = SafeId(envelope.stageId, "speech.synthesize"),
                attemptId = SafeId(envelope.attemptId, store.AttemptId), worker = new WorkerIdentityV1 { id = SafeId(envelope.workerId, "speech-worker"), version = WorkerCli.WorkerVersion },
                contractVersion = "1.0.0", idempotencyKey = IsSha(envelope.idempotencyKey) ? envelope.idempotencyKey : new string('0', 64),
                status = "failed", outputs = new List<ArtifactReferenceV1>(),
                error = new WorkerErrorV1
                {
                    code = code, category = missingRuntime ? "dependency" : contract != null ? "contract" : retryable ? "transient" : "worker", retryable = retryable,
                    detailsRef = detailsRef, message = message, blame = missingRuntime ? "dependency" : contract != null ? "input" : "worker"
                },
                startedAt = startedAt.ToString("o"), finishedAt = DateTime.UtcNow.ToString("o"), warnings = new List<string>(),
                payload = new Dictionary<string, object>
                {
                    { "operationId", SafeId(envelope.operationId, "unknown-operation") }, { "languageTag", "en-US" },
                    { "voiceId", "unavailable" }, { "cacheHit", false }, { "playbackState", "failed" }
                }
            };
        }

        private static WorkerRequestEnvelopeV1 UnknownEnvelope(string attemptId)
        {
            return new WorkerRequestEnvelopeV1 { requestId = "unknown-request", operationId = "unknown-operation", taskId = "unknown-task", runId = "unknown-run", stageId = "speech-synthesize", attemptId = attemptId, workerId = "speech.edge", idempotencyKey = new string('0', 64) };
        }

        private static string MakeFallbackProgressRef(AttemptStore store)
        {
            var root = store.JobRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var progress = LongPathFileSystem.GetFullPath(Path.Combine(store.AttemptDirectory, "progress.ndjson"));
            return progress.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? progress.Substring(root.Length).Replace('\\', '/')
                : "runs/unknown/stages/speech-synthesize/attempts/unknown/progress.ndjson";
        }

        private static bool IsSha(string value) { return !string.IsNullOrWhiteSpace(value) && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-fA-F]{64}$"); }
        private static string SafeId(string value, string fallback)
        {
            try { SpeechRequestReader.RequireId(value, "id"); return value; } catch { return fallback; }
        }

        private static string SanitizeProcessMessage(SynthesisProcessResult result)
        {
            var value = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            return Sanitize(value);
        }

        private static string Sanitize(string value)
        {
            value = (value ?? "处理失败").Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 500 ? value.Substring(0, 500) : value;
        }
    }

    internal sealed class SpeechWorkerException : Exception
    {
        public string Code { get; private set; }
        public bool Retryable { get; private set; }
        public SpeechWorkerException(string code, string message, bool retryable) : base(message) { Code = code; Retryable = retryable; }
    }
}

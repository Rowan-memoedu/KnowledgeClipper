using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class PlaybackWorker
    {
        private readonly PlaybackCoordinator coordinator;
        public PlaybackWorker(PlaybackCoordinator coordinator) { this.coordinator = coordinator; }

        public async Task<WorkerResultV1> RunPlayAsync(string jobRoot, string attemptDirectory, PlaybackWorkItem work)
        {
            var store = new AttemptStore(jobRoot, attemptDirectory);
            var startedAt = DateTime.UtcNow;
            try
            {
                ValidateAudio(jobRoot, work);
                store.AppendProgress("starting", "启动播放");
                var immutableAudioPath = SpeechRequestReader.ResolveJobRelativePath(jobRoot, work.AudioPath);
                using (var workspace = ShortPlaybackWorkspace.Create(immutableAudioPath))
                {
                    var playback = coordinator.PlayAsync(work.AttemptId, workspace.MediaPath);
                    PlaybackTerminal terminal;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(work.CancellationSignalPath))
                        {
                            var reason = await PlaybackCancellationMonitor.WaitForReasonAsync(
                                work.CancellationSignalPath, work.AttemptId, work.OperationId, work.CancellationGracePeriodMs, playback);
                            if (reason != null) coordinator.CancelActive(reason);
                        }
                        terminal = await playback;
                    }
                    catch
                    {
                        try { coordinator.CancelActive("user_stop"); } catch { }
                        throw;
                    }
                    var result = BuildPlaybackResult(work, startedAt, terminal);
                    store.WriteResultOnce(result);
                    try { store.AppendProgress(terminal.Status, terminal.Message); } catch { }
                    return result;
                }
            }
            catch (Exception ex)
            {
                var result = BuildFailure(store, work, startedAt, ex);
                try { store.WriteResultOnce(result); } catch (SpeechContractException immutable) { if (immutable.Code != "immutable_file_exists") throw; }
                return result;
            }
        }

        public WorkerResultV1 RunStop(string jobRoot, string attemptDirectory, PlaybackWorkItem work)
        {
            var store = new AttemptStore(jobRoot, attemptDirectory);
            var stopped = coordinator.StopActive();
            var result = BuildPlaybackResult(work, DateTime.UtcNow, new PlaybackTerminal { Status = "user_stop", Message = "用户主动停止" });
            if (!stopped) result.warnings.Add("stop requested while playback was idle");
            store.WriteResultOnce(result);
            try { store.AppendProgress(stopped ? "stopped" : "idle", stopped ? "已停止活动播放" : "没有活动播放"); } catch { }
            return result;
        }

        internal static WorkerResultV1 RunRejected(string jobRoot, string attemptDirectory, WorkerRequestEnvelopeV1 envelope, Exception exception)
        {
            var store = new AttemptStore(jobRoot, attemptDirectory);
            var work = new PlaybackWorkItem
            {
                RequestId = envelope.requestId, OperationId = envelope.operationId, TaskId = envelope.taskId, RunId = envelope.runId,
                StageId = envelope.stageId, AttemptId = envelope.attemptId, WorkerId = envelope.workerId, IdempotencyKey = envelope.idempotencyKey,
                AudioPath = envelope.attemptPath + "/request.json", LanguageTag = "en-US", VoiceId = "unavailable",
                CancellationSignalRef = envelope.cancelSignal == null ? envelope.attemptPath + "/cancel.json" : envelope.cancelSignal.path
            };
            var result = BuildFailure(store, work, DateTime.UtcNow, exception);
            store.WriteResultOnce(result);
            try { store.AppendProgress("rejected", exception.Message); } catch { }
            return result;
        }

        private static void ValidateAudio(string jobRoot, PlaybackWorkItem work)
        {
            if (work == null) throw new SpeechContractException("missing_playback_work", "缺少 playback work item");
            var path = SpeechRequestReader.ResolveJobRelativePath(jobRoot, work.AudioPath);
            if (!LongPathFileSystem.FileExists(path)) throw new SpeechContractException("missing_audio", "音频工件不存在");
            if (LongPathFileSystem.FileLength(path) != work.AudioBytes) throw new SpeechContractException("audio_bytes_mismatch", "音频 bytes 校验失败");
            if (!SpeechRequestReader.HashFile(path).Equals(work.AudioSha256, StringComparison.OrdinalIgnoreCase)) throw new SpeechContractException("audio_hash_mismatch", "音频 SHA-256 校验失败");
        }

        private static WorkerResultV1 BuildPlaybackResult(PlaybackWorkItem work, DateTime startedAt, PlaybackTerminal terminal)
        {
            var failed = terminal.Status == "playback_failed" || terminal.Status == "timeout";
            var cancelled = terminal.Status == "user_stop" || terminal.Status == "superseded" || terminal.Status == "task_cancel";
            var result = BaseResult(work, failed ? "failed" : cancelled ? "cancelled" : "completed", startedAt, DateTime.UtcNow);
            result.outputs = new List<ArtifactReferenceV1>();
            var playbackState = terminal.Status == "completed" ? "idle_replayable" :
                terminal.Status == "user_stop" ? "stopped" : terminal.Status == "superseded" ? "superseded" :
                terminal.Status == "task_cancel" ? "cancelled" : terminal.Status == "timeout" ? "timed_out" : "failed";
            result.payload = SpeechPayload(work, playbackState);
            if (terminal.Status == "superseded") result.warnings.Add("superseded by newer playback attempt");
            result.error = terminal.Status == "completed" ? null : new WorkerErrorV1
            {
                code = terminal.Status == "playback_failed" ? "playback.failed" : "playback." + terminal.Status,
                category = cancelled ? "cancelled" : terminal.Status == "timeout" ? "transient" : "dependency",
                retryable = terminal.Status == "timeout" || terminal.Status == "playback_failed",
                detailsRef = cancelled || terminal.Status == "timeout" ? work.CancellationSignalRef : work.AudioPath,
                message = terminal.Message,
                blame = cancelled ? "input" : terminal.Status == "timeout" ? "dependency" : "dependency"
            };
            return result;
        }

        private static WorkerResultV1 BuildFailure(AttemptStore store, PlaybackWorkItem work, DateTime startedAt, Exception ex)
        {
            var contract = ex as SpeechContractException;
            var missingRuntime = contract != null && contract.Code == "missing_runtime_tool";
            var result = BaseResult(work, "failed", startedAt, DateTime.UtcNow);
            string detailsRef;
            try { detailsRef = store.WriteErrorDetailsOnce(contract == null ? "playback_failed" : contract.Code, ex.Message); }
            catch { detailsRef = work == null || string.IsNullOrWhiteSpace(work.AudioPath) ? "progress.ndjson" : work.AudioPath; }
            result.outputs = new List<ArtifactReferenceV1>();
            result.payload = SpeechPayload(work, "failed");
            result.error = new WorkerErrorV1
            {
                code = contract == null ? "playback_failed" : contract.Code, category = contract == null || missingRuntime ? "dependency" : "contract",
                retryable = contract == null, detailsRef = detailsRef, message = ex.Message, blame = contract == null ? "dependency" : "input"
            };
            if (missingRuntime) result.error.blame = "dependency";
            return result;
        }

        private static WorkerResultV1 BaseResult(PlaybackWorkItem work, string status, DateTime startedAt, DateTime finishedAt)
        {
            if (work == null) work = new PlaybackWorkItem
            {
                RequestId = "unknown-request", OperationId = "unknown-operation", TaskId = "unknown-task", RunId = "unknown-run",
                StageId = "speech-playback", AttemptId = "unknown-attempt", WorkerId = "speech.edge", IdempotencyKey = new string('0', 64),
                LanguageTag = "en-US", VoiceId = "unavailable"
            };
            return new WorkerResultV1
            {
                schemaVersion = "1.0.0", requestId = work.RequestId, operationId = work.OperationId, taskId = work.TaskId, runId = work.RunId,
                stageId = work.StageId, attemptId = work.AttemptId, worker = new WorkerIdentityV1 { id = work.WorkerId, version = WorkerCli.WorkerVersion },
                contractVersion = "1.0.0", idempotencyKey = work.IdempotencyKey, status = status,
                startedAt = startedAt.ToString("o"), finishedAt = finishedAt.ToString("o"), warnings = new List<string>()
            };
        }

        private static Dictionary<string, object> SpeechPayload(PlaybackWorkItem work, string playbackState)
        {
            return new Dictionary<string, object>
            {
                { "operationId", work == null || string.IsNullOrWhiteSpace(work.OperationId) ? "unknown-operation" : work.OperationId },
                { "languageTag", work == null || string.IsNullOrWhiteSpace(work.LanguageTag) ? "en-US" : work.LanguageTag },
                { "voiceId", work == null || string.IsNullOrWhiteSpace(work.VoiceId) ? "unavailable" : work.VoiceId },
                { "cacheHit", false }, { "playbackState", playbackState }
            };
        }
    }
}

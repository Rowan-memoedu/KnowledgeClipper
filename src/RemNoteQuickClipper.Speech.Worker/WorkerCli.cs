using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal static class WorkerCli
    {
        internal const string WorkerId = "speech.edge";
        internal const string WorkerVersion = "1.2.5";
        internal const string ContractVersion = "1.0.0";

        public static object Describe()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" },
                { "workerId", WorkerId },
                { "workerVersion", WorkerVersion },
                { "contractVersion", ContractVersion },
                { "stages", new[] { "speech.synthesize", "speech.playback" } },
                { "inputKinds", new[] { "text.spoken", "audio.speech" } },
                { "outputKinds", new[] { "audio.speech" } },
                { "concurrency", new Dictionary<string, object> { { "model", "serial" }, { "maxParallelAttempts", 1 } } },
                { "sideEffectLevel", "local" },
                { "idempotencyLevel", "idempotent" },
                { "cancellation", new Dictionary<string, object>
                    {
                        { "protocol", "attempt-signal-file-v1" },
                        { "supportedReasons", new[] { "user_stop", "superseded", "task_cancel", "timeout" } }
                    }
                }
            };
        }

        public static WorkerRunContext DeriveRunContext(string requestArgument)
        {
            if (string.IsNullOrWhiteSpace(requestArgument) || !Path.IsPathRooted(requestArgument))
                throw new SpeechContractException("request_path_not_absolute", "--request 必须是绝对 request.json 路径");
            var requestPath = LongPathFileSystem.GetFullPath(requestArgument);
            if (!Path.GetFileName(requestPath).Equals("request.json", StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("invalid_request_filename", "--request 必须指向 request.json");
            if (!LongPathFileSystem.FileExists(requestPath)) throw new SpeechContractException("missing_request", "request.json 不存在");

            var envelope = SpeechRequestReader.TryReadEnvelope(requestPath);
            if (envelope == null) throw new SpeechContractException("invalid_request_json", "worker-request JSON 无法解析");
            if (envelope.workerId != WorkerId) throw new SpeechContractException("wrong_worker_id", "workerId 必须为 speech.edge");
            if (envelope.operation != "speech.synthesize" && envelope.operation != "speech.playback")
                throw new SpeechContractException("unsupported_operation", "只支持 speech.synthesize 或 speech.playback");

            var parts = SafeRelativeSegments(envelope.attemptPath, "attemptPath");
            if (parts.Length != 6 || !EqualsPart(parts[0], "runs") || !EqualsPart(parts[1], envelope.runId) ||
                !EqualsPart(parts[2], "stages") || !EqualsPart(parts[3], envelope.stageId) ||
                !EqualsPart(parts[4], "attempts") || !EqualsPart(parts[5], envelope.attemptId))
                throw new SpeechContractException("invalid_attempt_layout", "attemptPath 必须为 runs/<runId>/stages/<stageId>/attempts/<attemptId>");

            var attemptDirectory = Path.GetDirectoryName(requestPath);
            var jobRoot = attemptDirectory;
            for (var i = 0; i < parts.Length; i++) jobRoot = Path.GetDirectoryName(jobRoot);
            if (string.IsNullOrWhiteSpace(jobRoot)) throw new SpeechContractException("invalid_job_boundary", "无法从 request/attemptPath 推导 job 根");
            jobRoot = LongPathFileSystem.GetFullPath(jobRoot);
            if (!new DirectoryInfo(jobRoot).Name.Equals(envelope.taskId, StringComparison.Ordinal))
                throw new SpeechContractException("task_path_mismatch", "job 根目录名与 taskId 不一致");
            var jobsDirectory = Directory.GetParent(jobRoot);
            if (jobsDirectory == null || !jobsDirectory.Name.Equals("jobs", StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("invalid_job_layout", "job 根必须位于 jobs/<taskId>");
            var derivedAttempt = SpeechRequestReader.ResolveJobRelativePath(jobRoot, envelope.attemptPath);
            if (!derivedAttempt.TrimEnd(Path.DirectorySeparatorChar).Equals(attemptDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("attempt_path_mismatch", "attemptPath 与 request.json 位置不一致");
            return new WorkerRunContext { RequestPath = requestPath, JobRoot = jobRoot, AttemptDirectory = attemptDirectory, Envelope = envelope };
        }

        public static string ConfiguredTool(WorkerRequestEnvelopeV1 envelope, string key, string environmentName, string legacyEnvironmentName, string fallback)
        {
            object configured;
            var candidate = envelope.configSnapshot != null && envelope.configSnapshot.TryGetValue(key, out configured) ? configured as string : null;
            if (string.IsNullOrWhiteSpace(candidate)) candidate = Environment.GetEnvironmentVariable(environmentName);
            if (string.IsNullOrWhiteSpace(candidate)) candidate = Environment.GetEnvironmentVariable(legacyEnvironmentName);
            if (string.IsNullOrWhiteSpace(candidate)) candidate = FindOnPath(fallback);
            if (string.IsNullOrWhiteSpace(candidate) || !LongPathFileSystem.FileExists(candidate))
                throw new SpeechContractException("missing_runtime_tool", "找不到运行时工具 " + fallback + "；请在 configSnapshot." + key + " 中固定绝对路径");
            return LongPathFileSystem.GetFullPath(candidate);
        }

        public static string CancellationSignalPath(WorkerRunContext context)
        {
            if (context.Envelope.cancelSignal == null || context.Envelope.cancelSignal.schemaVersion != "1.0.0" || context.Envelope.cancelSignal.gracePeriodMs != 2000)
                throw new SpeechContractException("invalid_cancellation_signal", "cancelSignal 必须使用 1.0.0 与固定 2000ms grace period");
            return SpeechRequestReader.ResolveJobRelativePath(context.JobRoot, context.Envelope.cancelSignal.path);
        }

        public static string Serialize(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }.Serialize(value);
        }

        private static string[] SafeRelativeSegments(string path, string field)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new SpeechContractException("unsafe_artifact_path", field + " 必须相对 job 根");
            var parts = path.Replace('\\', '/').Split('/');
            foreach (var part in parts)
                if (part.Length == 0 || part == "." || part == "..") throw new SpeechContractException("unsafe_artifact_path", field + " 包含不安全路径段");
            return parts;
        }

        private static bool EqualsPart(string left, string right) { return string.Equals(left, right, StringComparison.Ordinal); }

        private static string FindOnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return candidate;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".exe")) return candidate + ".exe";
            }
            return null;
        }
    }
}

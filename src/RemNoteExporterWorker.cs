using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper
{
    internal sealed class StableWorkerRequest
    {
        public string RequestPath;
        public string JobRoot;
        public string AttemptDirectory;
        public string RequestId;
        public string OperationId;
        public string TaskId;
        public string RunId;
        public string StageId;
        public string AttemptId;
        public string IdempotencyKey;
        public string OutputPath;
        public string ProgressPath;
        public string ResultPath;
        public string CancelPath;
        public IDictionary<string, object> Payload;
        public IDictionary<string, object> ConfigSnapshot;
        public object[] InputRefs;
    }

    internal sealed class StableWorkerRunResult
    {
        public string Status;
        public List<IDictionary<string, object>> Outputs = new List<IDictionary<string, object>>();
        public IDictionary<string, object> Error = null;
        public List<string> Warnings = new List<string>();
        public IDictionary<string, object> Payload = new Dictionary<string, object>();
        public List<IDictionary<string, object>> SideEffects = new List<IDictionary<string, object>>();
    }

    internal sealed class ExternalStateUnknownException : Exception
    {
        public ExternalStateUnknownException(string message, Exception inner = null) : base(message, inner) { }
    }

    internal sealed class StableWorkerContractException : Exception
    {
        public string Code { get; private set; }
        public StableWorkerContractException(string code, string message) : base(message) { Code = code; }
        public StableWorkerContractException(string code, string message, Exception inner) : base(message, inner) { Code = code; }
    }

    internal sealed class WorkerCancellationSignal
    {
        public string Reason;
        public DateTime RequestedAt;
        public DateTime GraceDeadline;
    }

    internal sealed class WorkerCancellationMonitor : IDisposable
    {
        private readonly StableWorkerRequest request;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Task monitor;
        private volatile WorkerCancellationSignal signal;
        private volatile Exception signalError;

        public WorkerCancellationMonitor(StableWorkerRequest request)
        {
            this.request = request;
            if (LongPathFileSystem.FileExists(request.CancelPath))
            {
                try { signal = ReadSignal(request); }
                catch (Exception ex) { signalError = ex; }
                cancellation.Cancel();
                monitor = Task.FromResult(0);
                return;
            }
            monitor = Task.Run(async delegate
            {
                while (!stop.IsCancellationRequested)
                {
                    if (LongPathFileSystem.FileExists(request.CancelPath))
                    {
                        try { signal = ReadSignal(request); }
                        catch (Exception ex) { signalError = ex; }
                        cancellation.Cancel();
                        return;
                    }
                    try { await Task.Delay(50, stop.Token); }
                    catch (OperationCanceledException) { return; }
                }
            });
        }

        public CancellationToken Token { get { return cancellation.Token; } }
        public WorkerCancellationSignal Signal { get { return signal; } }

        public void ThrowIfCancellationRequested()
        {
            if (signalError != null) throw new StableWorkerContractException("invalid_cancel_signal", signalError.Message, signalError);
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation.Token);
        }

        public void Dispose()
        {
            stop.Cancel();
            try { monitor.Wait(300); } catch { }
            stop.Dispose();
            cancellation.Dispose();
        }

        private static WorkerCancellationSignal ReadSignal(StableWorkerRequest request)
        {
            var value = StableWorkerAdapter.ReadObject(request.CancelPath, "cancel signal");
            StableWorkerAdapter.RequireOnlyKeys(value, "cancel signal", "schemaVersion", "attemptId", "operationId", "reason", "requestedAt", "graceDeadline", "commandId");
            if (StableWorkerAdapter.String(value, "schemaVersion") != "1.0.0" ||
                StableWorkerAdapter.String(value, "attemptId") != request.AttemptId ||
                StableWorkerAdapter.String(value, "operationId") != request.OperationId)
                throw new InvalidDataException("cancel signal 身份与当前 attempt 不匹配");
            var reason = StableWorkerAdapter.String(value, "reason");
            if (reason != "user_stop" && reason != "superseded" && reason != "task_cancel" && reason != "timeout")
                throw new InvalidDataException("cancel signal reason 无效");
            DateTime requestedAt, graceDeadline;
            if (!DateTime.TryParse(StableWorkerAdapter.String(value, "requestedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out requestedAt) ||
                !DateTime.TryParse(StableWorkerAdapter.String(value, "graceDeadline"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out graceDeadline) ||
                Math.Abs((graceDeadline - requestedAt).TotalMilliseconds - 2000) > 1)
                throw new InvalidDataException("cancel signal grace deadline 无效");
            return new WorkerCancellationSignal { Reason = reason, RequestedAt = requestedAt, GraceDeadline = graceDeadline };
        }
    }

    internal static class StableWorkerAdapter
    {
        internal const string WorkerId = "export.remnote";
        internal const string WorkerVersion = "2.3.0";
        internal const string ContractVersion = "1.0.0";
        internal const string StageId = "export-remnote";
        internal const string Operation = "remnote.export";
        private static readonly Regex Identifier = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
        private static readonly Regex Sha256 = new Regex("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled);

        public static IDictionary<string, object> Describe()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "workerId", WorkerId }, { "workerVersion", WorkerVersion }, { "contractVersion", ContractVersion },
                { "stages", new[] { StageId } },
                { "inputKinds", new[] { "learning.bundle", "card.bundle" } },
                { "outputKinds", new[] { "remnote.write-receipt", "remnote.import-package" } },
                { "concurrency", new Dictionary<string, object> { { "model", "serial" }, { "maxParallelAttempts", 1 } } },
                { "sideEffectLevel", "external" }, { "idempotencyLevel", "reconcilable" },
                { "cancellation", new Dictionary<string, object>
                    {
                        { "protocol", "attempt-signal-file-v1" },
                        { "supportedReasons", new[] { "user_stop", "superseded", "task_cancel", "timeout" } }
                    }
                }
            };
        }

        public static StableWorkerRequest ReadRequest(string requestArgument)
        {
            if (string.IsNullOrWhiteSpace(requestArgument) || !Path.IsPathRooted(requestArgument))
                throw new StableWorkerContractException("request_path_not_absolute", "--request 必须是绝对路径");
            var requestPath = Path.GetFullPath(requestArgument);
            if (!LongPathFileSystem.FileExists(requestPath) || !Path.GetFileName(requestPath).Equals("request.json", StringComparison.OrdinalIgnoreCase))
                throw new StableWorkerContractException("missing_request", "--request 必须指向现有 request.json");
            var value = ReadObject(requestPath, "worker request");
            RequireOnlyKeys(value, "worker request", "schemaVersion", "requestId", "operationId", "taskId", "runId", "stageId", "attemptId", "workerId", "operation", "idempotencyKey", "attemptPath", "outputPath", "progressPath", "resultPath", "cancelSignal", "inputRefs", "configSnapshot", "payload");
            if (String(value, "schemaVersion") != "1.0.0") throw new StableWorkerContractException("unsupported_contract", "只支持 worker-request 1.0.0");
            var request = new StableWorkerRequest
            {
                RequestPath = requestPath,
                RequestId = RequiredId(value, "requestId"), OperationId = RequiredId(value, "operationId"), TaskId = RequiredId(value, "taskId"),
                RunId = RequiredId(value, "runId"), StageId = RequiredId(value, "stageId"), AttemptId = RequiredId(value, "attemptId"),
                IdempotencyKey = String(value, "idempotencyKey"), Payload = Dict(Get(value, "payload")), ConfigSnapshot = Dict(Get(value, "configSnapshot")), InputRefs = Array(Get(value, "inputRefs"))
            };
            if (String(value, "workerId") != WorkerId || request.StageId != StageId || String(value, "operation") != Operation)
                throw new StableWorkerContractException("wrong_worker_operation", "workerId、stageId 或 operation 不匹配");
            if (!Sha256.IsMatch(request.IdempotencyKey ?? "")) throw new StableWorkerContractException("invalid_idempotency_key", "idempotencyKey 无效");
            if (request.Payload == null || request.ConfigSnapshot == null || request.InputRefs == null)
                throw new StableWorkerContractException("incomplete_request", "payload、configSnapshot 或 inputRefs 缺失");

            var attemptPath = String(value, "attemptPath");
            var segments = SafeSegments(attemptPath, "attemptPath");
            if (segments.Length != 6 || segments[0] != "runs" || segments[1] != request.RunId || segments[2] != "stages" || segments[3] != request.StageId || segments[4] != "attempts" || segments[5] != request.AttemptId)
                throw new StableWorkerContractException("invalid_attempt_layout", "attemptPath 必须为 runs/<runId>/stages/<stageId>/attempts/<attemptId>");
            var attemptDirectory = Path.GetDirectoryName(requestPath);
            var jobRoot = attemptDirectory;
            for (var i = 0; i < segments.Length; i++) jobRoot = Path.GetDirectoryName(jobRoot);
            if (string.IsNullOrWhiteSpace(jobRoot) || !new DirectoryInfo(jobRoot).Name.Equals(request.TaskId, StringComparison.Ordinal))
                throw new StableWorkerContractException("task_path_mismatch", "无法从 request.json 推导 jobs/<taskId> 根");
            var jobs = Directory.GetParent(jobRoot);
            if (jobs == null || !jobs.Name.Equals("jobs", StringComparison.OrdinalIgnoreCase)) throw new StableWorkerContractException("invalid_job_layout", "job 根必须位于 jobs/<taskId>");
            request.JobRoot = Path.GetFullPath(jobRoot);
            request.AttemptDirectory = Path.GetFullPath(attemptDirectory);
            RequireExactPath(request.JobRoot, attemptPath, request.AttemptDirectory, "attemptPath");
            request.OutputPath = RequireExactPath(request.JobRoot, String(value, "outputPath"), Path.Combine(request.AttemptDirectory, "outputs"), "outputPath");
            request.ProgressPath = RequireExactPath(request.JobRoot, String(value, "progressPath"), Path.Combine(request.AttemptDirectory, "progress.ndjson"), "progressPath");
            request.ResultPath = RequireExactPath(request.JobRoot, String(value, "resultPath"), Path.Combine(request.AttemptDirectory, "result.json"), "resultPath");

            var cancel = Dict(Get(value, "cancelSignal"));
            RequireOnlyKeys(cancel, "cancelSignal", "schemaVersion", "path", "gracePeriodMs");
            if (String(cancel, "schemaVersion") != "1.0.0" || Convert.ToInt32(Get(cancel, "gracePeriodMs"), CultureInfo.InvariantCulture) != 2000)
                throw new StableWorkerContractException("invalid_cancel_reference", "cancelSignal 必须为 v1 和 2000ms grace");
            request.CancelPath = ResolveJobRelative(request.JobRoot, String(cancel, "path"));
            if (!request.CancelPath.StartsWith(request.AttemptDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new StableWorkerContractException("unsafe_cancel_path", "cancelSignal.path 必须位于当前 attempt");
            return request;
        }

        public static ExportTaskPackage LoadPackage(StableWorkerRequest request)
        {
            var payloadVersion = String(request.Payload, "schemaVersion");
            if (payloadVersion == "1.0.0") RequireOnlyKeys(request.Payload, "payload", "schemaVersion", "documentTitle", "sink", "target");
            else if (payloadVersion == "2.0.0") RequireOnlyKeys(request.Payload, "payload", "schemaVersion", "documentTitle", "sink", "target", "route");
            else if (payloadVersion == "3.0.0") RequireOnlyKeys(request.Payload, "payload", "schemaVersion", "documentTitle", "sink", "target", "route", "originalMedia");
            else throw new StableWorkerContractException("unsupported_payload", "RemNote export payload 只支持 1.0.0、2.0.0 或 3.0.0");
            var sink = String(request.Payload, "sink");
            if (sink != "mcp-direct" && sink != "markdown-zip") throw new StableWorkerContractException("unsupported_sink", "sink 必须为 mcp-direct 或 markdown-zip");
            var target = Dict(Get(request.Payload, "target"));
            RequireOnlyKeys(target, "payload.target", "rootRemId", "rootTitle");
            RemNoteExportRoute route = null;
            if (payloadVersion == "2.0.0" || payloadVersion == "3.0.0")
            {
                var routeValue = Dict(Get(request.Payload, "route"));
                var kind = String(routeValue, "kind");
                if (kind == "clip-daily-route")
                {
                    RequireOnlyKeys(routeValue, "payload.route", "kind", "timeZone", "localDate", "dateFormat");
                    route = new RemNoteExportRoute
                    {
                        Kind = kind,
                        TimeZone = String(routeValue, "timeZone"),
                        LocalDate = String(routeValue, "localDate"),
                        DateFormat = String(routeValue, "dateFormat")
                    };
                }
                else
                {
                    RequireOnlyKeys(routeValue, "payload.route", "kind");
                    route = new RemNoteExportRoute { Kind = kind };
                }
            }
            var manifest = new RemNoteExportManifest
            {
                SchemaVersion = payloadVersion == "3.0.0" ? "remnote-export-task.v3" : payloadVersion == "2.0.0" ? "remnote-export-task.v2" : "remnote-export-task.v1", TaskPackageId = request.TaskId, DocumentTitle = String(request.Payload, "documentTitle"), DefaultSink = sink,
                Target = new RemNoteExportTarget { RootRemId = String(target, "rootRemId"), RootTitle = String(target, "rootTitle"), DateRouting = route != null && route.Kind == "clip-daily-route" },
                Route = route,
                OriginalMedia = payloadVersion == "3.0.0" ? LoadOriginalMedia(request.JobRoot, Dict(Get(request.Payload, "originalMedia"))) : null,
                Inputs = new RemNoteExportInputs()
            };
            LearningBundle learning = null;
            CardBundle cards = null;
            foreach (var raw in request.InputRefs)
            {
                var reference = Dict(raw);
                RequireOnlyKeys(reference, "inputRef", "artifactRef", "kind", "contractVersion", "manifestPath", "expectedSha256");
                var kind = String(reference, "kind");
                if (kind != "learning.bundle" && kind != "card.bundle") throw new StableWorkerContractException("unsupported_input_kind", "RemNote Exporter 只消费 learning.bundle/card.bundle");
                var bytes = ReadArtifactPayload(request.JobRoot, reference, kind);
                var json = new UTF8Encoding(false, true).GetString(bytes);
                if (kind == "learning.bundle")
                {
                    if (learning != null) throw new StableWorkerContractException("duplicate_learning_bundle", "只能提供一个 LearningBundle");
                    learning = ExportTaskPackage.ParseLearningStrict(json);
                }
                else
                {
                    if (cards != null) throw new StableWorkerContractException("duplicate_card_bundle", "只能提供一个 CardBundle");
                    cards = ExportTaskPackage.ParseCardsStrict(json);
                }
            }
            if (route == null || route.Kind == "card-export") manifest.DocumentTitle = RemNoteDocumentTitleStrategy.Resolve(manifest.DocumentTitle, request.TaskId, learning, cards);
            return ExportTaskPackage.CreateForWorker(request.JobRoot, manifest, learning, cards);
        }

        private static RemNoteOriginalMediaSet LoadOriginalMedia(string jobRoot, IDictionary<string, object> value)
        {
            RequireOnlyKeys(value, "payload.originalMedia", "schemaVersion", "orderingPolicy", "assets");
            var assetsRaw = Array(Get(value, "assets"));
            if (assetsRaw == null || assetsRaw.Length == 0) throw new StableWorkerContractException("invalid_original_media", "payload.originalMedia.assets 不能为空");
            var assets = new List<RemNoteOriginalMediaAsset>();
            foreach (var raw in assetsRaw)
            {
                var item = Dict(raw);
                RequireOnlyKeys(item, "payload.originalMedia.asset", "assetId", "role", "ordinal", "path", "sha256", "bytes", "mediaType");
                var relative = String(item, "path");
                var absolute = ResolveJobRelative(jobRoot, relative);
                if (!LongPathFileSystem.FileExists(absolute)) throw new StableWorkerContractException("missing_original_media", "originalMedia 文件不存在: " + relative);
                var expectedSha = String(item, "sha256");
                var expectedBytes = Convert.ToInt64(Get(item, "bytes"), CultureInfo.InvariantCulture);
                if (!Sha256.IsMatch(expectedSha ?? "") || !ExportTaskPackage.Sha256File(absolute).Equals(expectedSha, StringComparison.OrdinalIgnoreCase) || LongPathFileSystem.FileLength(absolute) != expectedBytes)
                    throw new StableWorkerContractException("original_media_integrity_mismatch", "originalMedia bytes/SHA-256 不匹配: " + relative);
                assets.Add(new RemNoteOriginalMediaAsset
                {
                    AssetId = String(item, "assetId"), Role = String(item, "role"), Ordinal = Convert.ToInt32(Get(item, "ordinal"), CultureInfo.InvariantCulture),
                    Path = relative, AbsolutePath = absolute, Sha256 = expectedSha.ToLowerInvariant(), Bytes = expectedBytes, MediaType = String(item, "mediaType")
                });
            }
            var media = new RemNoteOriginalMediaSet { SchemaVersion = String(value, "schemaVersion"), OrderingPolicy = String(value, "orderingPolicy"), Assets = assets.ToArray() };
            RemNoteOriginalMediaValidator.Validate(media);
            return media;
        }

        private static byte[] ReadArtifactPayload(string jobRoot, IDictionary<string, object> reference, string expectedKind)
        {
            var artifactRef = RequiredId(reference, "artifactRef");
            if (String(reference, "contractVersion") != "1.0.0") throw new StableWorkerContractException("unsupported_input_version", "inputRef contractVersion 必须为 1.0.0");
            var expectedManifestHash = String(reference, "expectedSha256");
            if (!Sha256.IsMatch(expectedManifestHash ?? "")) throw new StableWorkerContractException("invalid_manifest_hash", "inputRef expectedSha256 无效");
            var manifestPath = ResolveJobRelative(jobRoot, String(reference, "manifestPath"));
            var manifestBytes = LongPathFileSystem.ReadAllBytes(manifestPath);
            if (!ExportTaskPackage.Sha256File(manifestPath).Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase))
                throw new StableWorkerContractException("manifest_hash_mismatch", "input artifact manifest SHA-256 不匹配");
            var manifest = ReadObject(manifestPath, "artifact manifest");
            if (String(manifest, "schemaVersion") != "1.0.0" || String(manifest, "artifactId") != artifactRef || String(manifest, "kind") != expectedKind || String(manifest, "contractVersion") != "1.0.0")
                throw new StableWorkerContractException("artifact_identity_mismatch", "artifact manifest 身份不匹配");
            var files = Array(Get(manifest, "files"));
            if (files == null || files.Length != 1) throw new StableWorkerContractException("invalid_bundle_artifact", "bundle artifact 必须恰好包含一个文件");
            var file = Dict(files[0]);
            var payloadPath = ResolveJobRelative(jobRoot, String(file, "path"));
            var payload = LongPathFileSystem.ReadAllBytes(payloadPath);
            if (payload.LongLength != Convert.ToInt64(Get(file, "bytes"), CultureInfo.InvariantCulture) ||
                !ExportTaskPackage.Sha256File(payloadPath).Equals(String(file, "sha256"), StringComparison.OrdinalIgnoreCase))
                throw new StableWorkerContractException("bundle_integrity_mismatch", "bundle 文件 bytes/SHA-256 不匹配");
            if (!String(file, "mediaType").Equals("application/json", StringComparison.OrdinalIgnoreCase))
                throw new StableWorkerContractException("bundle_media_type", "bundle 文件必须为 application/json");
            return payload;
        }

        public static IDictionary<string, object> PublishArtifact(StableWorkerRequest request, string artifactId, string kind, string mediaType, string payloadFileName, byte[] payload, IDictionary<string, object> metadata)
        {
            var versionDirectory = Path.Combine(request.OutputPath, artifactId, "v0001");
            LongPathFileSystem.CreateDirectory(versionDirectory);
            var payloadPath = Path.Combine(versionDirectory, payloadFileName);
            AtomicWriteBytesOnce(payloadPath, payload);
            var payloadRelative = ToJobRelative(request.JobRoot, payloadPath);
            var manifestPath = Path.Combine(versionDirectory, "artifact.json");
            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "artifactId", artifactId }, { "kind", kind }, { "contractVersion", "1.0.0" }, { "mediaType", mediaType },
                { "files", new object[] { new Dictionary<string, object>
                    {
                        { "path", payloadRelative }, { "sha256", ExportTaskPackage.Sha256File(payloadPath) }, { "bytes", LongPathFileSystem.FileLength(payloadPath) }, { "mediaType", mediaType }
                    }
                } },
                { "createdAt", UtcNow() },
                { "producer", new Dictionary<string, object> { { "stageId", request.StageId }, { "workerId", WorkerId }, { "workerVersion", WorkerVersion }, { "attemptId", request.AttemptId } } },
                { "inputRefs", request.InputRefs }, { "dataClass", "personal" }, { "metadata", metadata ?? new Dictionary<string, object>() }
            };
            AtomicWriteJsonOnce(manifestPath, manifest);
            return new Dictionary<string, object>
            {
                { "artifactRef", artifactId }, { "kind", kind }, { "contractVersion", "1.0.0" },
                { "manifestPath", ToJobRelative(request.JobRoot, manifestPath) }, { "expectedSha256", ExportTaskPackage.Sha256File(manifestPath) }
            };
        }

        public static void PublishResult(StableWorkerRequest request, DateTime startedAt, StableWorkerRunResult terminal)
        {
            var result = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "requestId", request.RequestId }, { "operationId", request.OperationId }, { "taskId", request.TaskId },
                { "runId", request.RunId }, { "stageId", request.StageId }, { "attemptId", request.AttemptId },
                { "worker", new Dictionary<string, object> { { "id", WorkerId }, { "version", WorkerVersion } } },
                { "contractVersion", ContractVersion }, { "idempotencyKey", request.IdempotencyKey }, { "status", terminal.Status },
                { "outputs", terminal.Outputs.ToArray() }, { "error", terminal.Error }, { "startedAt", Format(startedAt) }, { "finishedAt", UtcNow() },
                { "warnings", terminal.Warnings.ToArray() }, { "sideEffects", terminal.SideEffects.ToArray() }, { "metrics", new Dictionary<string, object>() }, { "payload", terminal.Payload }
            };
            AtomicWriteJsonOnce(request.ResultPath, result);
        }

        public static IDictionary<string, object> WriteError(StableWorkerRequest request, string code, string category, bool retryable, string message, string blame)
        {
            var path = Path.Combine(request.AttemptDirectory, "error.json");
            AtomicWriteJsonOnce(path, new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "code", code }, { "category", category }, { "retryable", retryable }, { "message", message }, { "blame", blame }, { "recordedAt", UtcNow() }
            });
            return new Dictionary<string, object>
            {
                { "code", code }, { "category", category }, { "retryable", retryable }, { "detailsRef", ToJobRelative(request.JobRoot, path) }, { "message", message }, { "blame", blame }
            };
        }

        public static IDictionary<string, object> ReadObject(string path, string label)
        {
            try { return Dict(Json.New().DeserializeObject(LongPathFileSystem.ReadAllText(path, new UTF8Encoding(false, true)))); }
            catch (Exception ex) { throw new StableWorkerContractException("invalid_json", label + " 不是严格 UTF-8 JSON object", ex); }
        }

        public static void RequireOnlyKeys(IDictionary<string, object> value, string label, params string[] keys)
        {
            if (value == null) throw new StableWorkerContractException("missing_object", label + " 必须是 object");
            var allowed = new HashSet<string>(keys, StringComparer.Ordinal);
            var extra = value.Keys.Where(key => !allowed.Contains(key)).ToArray();
            if (extra.Length != 0) throw new StableWorkerContractException("unknown_field", label + " 含未知字段: " + string.Join(", ", extra));
            foreach (var key in keys.Where(key => key != "commandId")) if (!value.ContainsKey(key)) throw new StableWorkerContractException("missing_field", label + " 缺少 " + key);
        }

        public static IDictionary<string, object> Dict(object value) { return value as IDictionary<string, object>; }
        public static object[] Array(object value) { return value as object[]; }
        public static object Get(IDictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
        public static string String(IDictionary<string, object> value, string key) { return Convert.ToString(Get(value, key), CultureInfo.InvariantCulture); }

        private static string RequiredId(IDictionary<string, object> value, string key)
        {
            var result = String(value, key);
            if (!Identifier.IsMatch(result ?? "")) throw new StableWorkerContractException("invalid_identifier", key + " 无效");
            return result;
        }

        private static string[] SafeSegments(string relative, string field)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new StableWorkerContractException("unsafe_path", field + " 必须为 job 相对路径");
            var parts = relative.Replace('\\', '/').Split('/');
            if (parts.Any(part => part.Length == 0 || part == "." || part == "..")) throw new StableWorkerContractException("unsafe_path", field + " 含不安全路径段");
            return parts;
        }

        public static string ResolveJobRelative(string jobRoot, string relative)
        {
            SafeSegments(relative, "job-relative path");
            var root = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new StableWorkerContractException("unsafe_path", "路径越出 job 根");
            return full;
        }

        private static string RequireExactPath(string jobRoot, string relative, string expected, string field)
        {
            var actual = ResolveJobRelative(jobRoot, relative);
            if (!actual.Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new StableWorkerContractException("path_layout_mismatch", field + " 与 attempt 布局不一致");
            return actual;
        }

        public static string ToJobRelative(string jobRoot, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('\\', '/');
        }

        public static void AtomicWriteJsonOnce(string path, object value) { AtomicWriteBytesOnce(path, new UTF8Encoding(false).GetBytes(Json.New().Serialize(value))); }

        public static void AtomicWriteBytesOnce(string path, byte[] bytes)
        {
            LongPathFileSystem.CreateDirectory(Path.GetDirectoryName(path));
            if (LongPathFileSystem.FileExists(path)) throw new IOException("不可变文件已存在: " + path);
            // Keep the atomic sidecar bounded on Windows: a valid immutable output path
            // near MAX_PATH must not become invalid merely because the temporary suffix is long.
            // Each output path belongs to one immutable attempt, so a deterministic sibling is
            // sufficient; FileMode.CreateNew still fails closed if a stale sidecar exists.
            var temporary = path + ".tmp";
            try
            {
                using (var stream = LongPathFileSystem.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                LongPathFileSystem.MoveFile(temporary, path);
            }
            finally { if (LongPathFileSystem.FileExists(temporary)) try { LongPathFileSystem.DeleteFile(temporary); } catch { } }
        }

        public static string UtcNow() { return Format(DateTime.UtcNow); }
        private static string Format(DateTime value) { return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
    }

    internal sealed class RemNoteStableWorker
    {
        private sealed class NodeResolution
        {
            public string RemId;
            public string Outcome;
        }

        private readonly IRemNoteExportGateway gateway;
        public RemNoteStableWorker(IRemNoteExportGateway gateway) { this.gateway = gateway; }

        public async Task<StableWorkerRunResult> RunAsync(StableWorkerRequest request, ExportTaskPackage package, WorkerCancellationMonitor cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (package.Manifest.DefaultSink == "markdown-zip")
            {
                if (package.OriginalMedia != null) throw new StableWorkerContractException("destination.original_media_zip_unsupported", "当前 ZIP sink 不支持可验证原图附着；未生成会丢图的导入包");
                return ExportZip(request, package, cancellation);
            }
            if (package.Manifest.Route != null && package.Manifest.Route.Kind == "clip-daily-route")
                return await ExportClipDailyAsync(request, package, cancellation);
            return await ExportCardDirectAsync(request, package, cancellation);
        }

        private StableWorkerRunResult ExportZip(StableWorkerRequest request, ExportTaskPackage package, WorkerCancellationMonitor cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var markdown = RemNoteMarkdownRenderer.Render(package);
            var temporaryZip = Path.Combine(request.AttemptDirectory, "remnote-import-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var file = LongPathFileSystem.Open(temporaryZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8))
                {
                    var prefix = package.Manifest.Target.RootTitle.Trim('/') + "/";
                    if (package.Manifest.Route != null && package.Manifest.Route.Kind == "clip-daily-route") prefix += package.Manifest.Route.LocalDate + "/";
                    var entry = archive.CreateEntry(prefix + package.Manifest.DocumentTitle + ".md", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(markdown);
                }
                cancellation.ThrowIfCancellationRequested();
                var output = StableWorkerAdapter.PublishArtifact(request, "remnote-import-package", "remnote.import-package", "application/zip", "remnote-import.zip", LongPathFileSystem.ReadAllBytes(temporaryZip),
                    new Dictionary<string, object> { { "documentTitle", package.Manifest.DocumentTitle }, { "requiresManualImport", true }, { "imagesIncluded", false }, { "dateRouting", package.Manifest.Target.DateRouting }, { "route", RouteKind(package) } });
                return new StableWorkerRunResult
                {
                    Status = "completed", Outputs = new List<IDictionary<string, object>> { output },
                    Payload = new Dictionary<string, object> { { "sink", "markdown-zip" }, { "route", RouteKind(package) }, { "outcome", "prepared_for_manual_import" }, { "readbackVerified", false } }
                };
            }
            finally { if (LongPathFileSystem.FileExists(temporaryZip)) try { LongPathFileSystem.DeleteFile(temporaryZip); } catch { } }
        }

        private async Task<StableWorkerRunResult> ExportCardDirectAsync(StableWorkerRequest request, ExportTaskPackage package, WorkerCancellationMonitor cancellation)
        {
            if (gateway == null) throw new StableWorkerContractException("missing_gateway", "mcp-direct 缺少本地 RemNote gateway");
            IRemNoteOriginalMediaGateway mediaGateway = null;
            if (package.OriginalMedia != null)
            {
                mediaGateway = gateway as IRemNoteOriginalMediaGateway;
                if (mediaGateway == null) throw new StableWorkerContractException("destination.original_media_unsupported", "RemNote gateway 未声明原图附着能力；未执行任何外部写入");
                var capability = await mediaGateway.GetOriginalMediaCapabilityAsync(cancellation.Token);
                if (capability == null || !capability.Supported)
                    throw new StableWorkerContractException("destination.original_media_unsupported", "RemNote 原图写入能力不可用: " + (capability == null ? "capability.missing" : capability.ReasonCode) + "；未执行任何外部写入");
            }
            var markdown = RemNoteMarkdownRenderer.Render(package);
            var markdownPath = Path.Combine(request.AttemptDirectory, "document.md");
            StableWorkerAdapter.AtomicWriteBytesOnce(markdownPath, new UTF8Encoding(false).GetBytes(markdown));
            var mutexName = "Local\\RemNoteExporter-" + ExportTaskPackage.Sha256Text(package.Manifest.Target.RootRemId).Substring(0, 24);
            using (var mutex = new Semaphore(1, 1, mutexName))
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("等待 RemNote Exporter 单写者锁超时");
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    await gateway.CheckStatusAsync(cancellation.Token);
                    var root = await gateway.ReadDocumentAsync(package.Manifest.Target.RootRemId, cancellation.Token);
                    if (root == null || root.RemId != package.Manifest.Target.RootRemId || root.Title != package.Manifest.Target.RootTitle || root.RemType != "document")
                        throw new StableWorkerContractException("target_mismatch", "目标根 Rem ID、标题或 document 类型不匹配");
                    var matches = await gateway.FindDirectDocumentsAsync(root.RemId, package.Manifest.DocumentTitle, cancellation.Token);
                    if (matches.Count > 1) throw new StableWorkerContractException("idempotency_collision", "目标根下存在多个同名稳定文档，停止写入");
                    string documentId;
                    var outcome = "already_applied";
                    if (matches.Count == 1) documentId = matches[0].RemId;
                    else
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Exception createError = null;
                        try
                        {
                            documentId = package.OriginalMedia == null
                                ? await gateway.CreateDocumentAsync(root.RemId, package.Manifest.DocumentTitle, markdownPath, cancellation.Token)
                                : await mediaGateway.CreateDocumentWithOriginalMediaAsync(root.RemId, package.Manifest.DocumentTitle, markdownPath, package.OriginalMedia.Assets, cancellation.Token);
                        }
                        catch (Exception ex) { createError = ex; documentId = null; }
                        if (createError != null)
                        {
                            if (createError is OperationCanceledException)
                                throw new ExternalStateUnknownException("创建调用被取消，RemNote 是否已提交无法确认", createError);
                            try
                            {
                                using (var reconcile = new CancellationTokenSource(1500))
                                {
                                    matches = await gateway.FindDirectDocumentsAsync(root.RemId, package.Manifest.DocumentTitle, reconcile.Token);
                                    if (matches.Count == 1)
                                    {
                                        var recovered = await gateway.ReadDocumentAsync(matches[0].RemId, reconcile.Token);
                                        RemNoteExporterEngine.ValidateReadback(package, recovered);
                                        var recoveredMedia = await ReadAndValidateOriginalMediaAsync(package, mediaGateway, matches[0].RemId, reconcile.Token);
                                        documentId = matches[0].RemId;
                                        outcome = "recovered_after_uncertain_commit";
                                        return PublishVerifiedReceipt(request, package, documentId, outcome, markdown, recoveredMedia);
                                    }
                                }
                            }
                            catch { }
                            throw new ExternalStateUnknownException("创建调用失败且无法在 1500ms 内唯一回读确认；禁止自动重试", createError);
                        }
                        outcome = "created";
                    }
                    if (outcome == "created" && cancellation.Token.IsCancellationRequested)
                        throw new ExternalStateUnknownException("RemNote 已返回 document ID，但取消发生在写后回读前；禁止自动重试");
                    cancellation.ThrowIfCancellationRequested();
                    List<RemNoteMediaReadbackSnapshot> mediaReadback = null;
                    try
                    {
                        matches = await gateway.FindDirectDocumentsAsync(root.RemId, package.Manifest.DocumentTitle, cancellation.Token);
                        if (matches.Count != 1 || matches[0].RemId != documentId) throw new InvalidOperationException("写后稳定文档数量或 Rem ID 不一致");
                        var readback = await gateway.ReadDocumentAsync(documentId, cancellation.Token);
                        RemNoteExporterEngine.ValidateReadback(package, readback);
                        mediaReadback = await ReadAndValidateOriginalMediaAsync(package, mediaGateway, documentId, cancellation.Token);
                    }
                    catch (Exception verificationError)
                    {
                        if (outcome == "already_applied")
                            throw new StableWorkerContractException("idempotency_content_conflict", "同名稳定文档存在，但内容或卡片语义与输入 bundle 不匹配");
                        throw new ExternalStateUnknownException("RemNote 已返回 document ID，但写后回读未通过；禁止自动重试", verificationError);
                    }
                    return PublishVerifiedReceipt(request, package, documentId, outcome, markdown, mediaReadback);
                }
                finally { try { mutex.Release(); } catch { } }
            }
        }

        private static async Task<List<RemNoteMediaReadbackSnapshot>> ReadAndValidateOriginalMediaAsync(ExportTaskPackage package, IRemNoteOriginalMediaGateway mediaGateway, string documentRemId, CancellationToken cancellationToken)
        {
            if (package.OriginalMedia == null) return null;
            if (mediaGateway == null) throw new InvalidOperationException("原图 gateway 缺失");
            var readback = await mediaGateway.ReadOriginalMediaAsync(documentRemId, cancellationToken);
            RemNoteOriginalMediaValidator.ValidateReadback(package.OriginalMedia, readback);
            return readback;
        }

        private async Task<StableWorkerRunResult> ExportClipDailyAsync(StableWorkerRequest request, ExportTaskPackage package, WorkerCancellationMonitor cancellation)
        {
            if (gateway == null) throw new StableWorkerContractException("missing_gateway", "mcp-direct 缺少本地 RemNote gateway");
            var markdown = RemNoteMarkdownRenderer.Render(package);
            var markdownPath = Path.Combine(request.AttemptDirectory, "clip.md");
            StableWorkerAdapter.AtomicWriteBytesOnce(markdownPath, new UTF8Encoding(false).GetBytes(markdown));
            var lockName = "Local\\RemNoteExporter-" + ExportTaskPackage.Sha256Text(package.Manifest.Target.RootRemId).Substring(0, 24);
            using (var gate = new Semaphore(1, 1, lockName))
            {
                if (!gate.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("等待 RemNote Exporter 单写者锁超时");
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    await gateway.CheckStatusAsync(cancellation.Token);
                    var root = await gateway.ReadDocumentAsync(package.Manifest.Target.RootRemId, cancellation.Token);
                    if (root == null || root.RemId != package.Manifest.Target.RootRemId || root.Title != package.Manifest.Target.RootTitle || root.RemType != "document")
                        throw new StableWorkerContractException("target_mismatch", "摘录目标根 Rem ID、标题或 document 类型不匹配");

                    var date = await ResolveOrCreateNodeAsync(
                        root.RemId, package.Manifest.Route.LocalDate, null, false, "date_node_collision", "日期节点", package, cancellation);
                    var clip = await ResolveOrCreateNodeAsync(
                        date.RemId, package.Manifest.DocumentTitle, markdownPath, false, "idempotency_collision", "摘录节点", package, cancellation);

                    if (clip.Outcome == "created" && cancellation.Token.IsCancellationRequested)
                        throw new ExternalStateUnknownException("RemNote 已返回摘录 Rem ID，但取消发生在写后回读前；禁止自动重试");

                    try
                    {
                        await VerifyUniqueNodeAsync(root.RemId, package.Manifest.Route.LocalDate, date.RemId, "日期节点", cancellation.Token);
                        await VerifyUniqueNodeAsync(date.RemId, package.Manifest.DocumentTitle, clip.RemId, "摘录节点", cancellation.Token);
                        var clipReadback = await gateway.ReadDocumentAsync(clip.RemId, cancellation.Token);
                        ValidateClipReadback(package, date.RemId, clipReadback);
                    }
                    catch (Exception verificationError)
                    {
                        if (clip.Outcome == "reused")
                            throw new StableWorkerContractException("idempotency_content_conflict", "同名摘录存在，但内容与输入 LearningBundle 不匹配");
                        throw new ExternalStateUnknownException("RemNote 已返回摘录 Rem ID，但根→日期→摘录回读未通过；禁止自动重试", verificationError);
                    }

                    var clipOutcome = clip.Outcome == "reused" ? "already_applied" : clip.Outcome;
                    return PublishClipReceipt(request, package, date, clip.RemId, clipOutcome, markdown);
                }
                finally { try { gate.Release(); } catch { } }
            }
        }

        private async Task<NodeResolution> ResolveOrCreateNodeAsync(
            string parentRemId,
            string title,
            string contentFile,
            bool asDocument,
            string collisionCode,
            string label,
            ExportTaskPackage package,
            WorkerCancellationMonitor cancellation)
        {
            var matches = await gateway.FindDirectNodesAsync(parentRemId, title, cancellation.Token);
            if (matches.Count > 1) throw new StableWorkerContractException(collisionCode, label + "重名，停止写入");
            if (matches.Count == 1) return new NodeResolution { RemId = matches[0].RemId, Outcome = "reused" };

            cancellation.ThrowIfCancellationRequested();
            Exception createError = null;
            string remId = null;
            try { remId = await gateway.CreateNodeAsync(parentRemId, title, contentFile, asDocument, cancellation.Token); }
            catch (Exception ex) { createError = ex; }
            if (createError == null) return new NodeResolution { RemId = remId, Outcome = "created" };
            if (createError is OperationCanceledException)
                throw new ExternalStateUnknownException(label + "创建调用被取消，外部状态无法确认", createError);

            try
            {
                using (var reconcile = new CancellationTokenSource(1500))
                {
                    matches = await gateway.FindDirectNodesAsync(parentRemId, title, reconcile.Token);
                    if (matches.Count == 1)
                    {
                        if (contentFile != null)
                        {
                            var recovered = await gateway.ReadDocumentAsync(matches[0].RemId, reconcile.Token);
                            ValidateClipReadback(package, parentRemId, recovered);
                        }
                        return new NodeResolution { RemId = matches[0].RemId, Outcome = "recovered_after_uncertain_commit" };
                    }
                }
            }
            catch { }
            throw new ExternalStateUnknownException(label + "创建失败且无法在 1500ms 内唯一回读确认；禁止自动重试", createError);
        }

        private async Task VerifyUniqueNodeAsync(string parentRemId, string title, string expectedRemId, string label, CancellationToken cancellationToken)
        {
            var matches = await gateway.FindDirectNodesAsync(parentRemId, title, cancellationToken);
            if (matches.Count != 1 || matches[0].RemId != expectedRemId) throw new InvalidOperationException(label + "数量或 Rem ID 不一致");
        }

        private static void ValidateClipReadback(ExportTaskPackage package, string dateRemId, RemNoteDocumentSnapshot snapshot)
        {
            if (snapshot == null || snapshot.RemId == null || snapshot.Title != package.Manifest.DocumentTitle || snapshot.ParentRemId != dateRemId)
                throw new InvalidOperationException("摘录节点元数据回读不匹配");
            var expected = (package.Learning.Entries ?? new LearningEntry[0]).Select(item => item.Text).ToArray();
            var actual = snapshot.Children ?? new List<RemNoteNodeSnapshot>();
            if (actual.Count != expected.Length) throw new InvalidOperationException("摘录节点回读子项数量不匹配");
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index].Headline != expected[index]) throw new InvalidOperationException("摘录节点第 " + (index + 1) + " 项内容不匹配");
                if (!string.IsNullOrWhiteSpace(actual[index].CardDirection)) throw new InvalidOperationException("摘录内容被意外创建为卡片");
            }
        }

        private static StableWorkerRunResult PublishClipReceipt(
            StableWorkerRequest request,
            ExportTaskPackage package,
            NodeResolution date,
            string clipRemId,
            string clipOutcome,
            string markdown)
        {
            var receipt = new Dictionary<string, object>
            {
                { "schemaVersion", "2.0.0" }, { "sink", "mcp-direct" }, { "route", "clip-daily-route" },
                { "outcome", clipOutcome }, { "rootRemId", package.Manifest.Target.RootRemId }, { "rootTitle", package.Manifest.Target.RootTitle },
                { "rootOutcome", "reused" },
                { "dateRemId", date.RemId }, { "dateTitle", package.Manifest.Route.LocalDate }, { "dateOutcome", date.Outcome },
                { "timeZone", package.Manifest.Route.TimeZone }, { "localDate", package.Manifest.Route.LocalDate }, { "dateFormat", package.Manifest.Route.DateFormat },
                { "clipRemId", clipRemId }, { "clipTitle", package.Manifest.DocumentTitle }, { "clipOutcome", clipOutcome },
                { "idempotencyKey", request.IdempotencyKey }, { "contentSha256", ExportTaskPackage.Sha256Text(markdown) },
                { "readbackVerified", true }, { "duplicateCount", 0 }, { "dateRouting", true }, { "imagesHandled", false }
            };
            var output = StableWorkerAdapter.PublishArtifact(request, "remnote-write-receipt", "remnote.write-receipt", "application/json", "receipt.json",
                new UTF8Encoding(false).GetBytes(Json.New().Serialize(receipt)), new Dictionary<string, object>
                {
                    { "route", "clip-daily-route" }, { "rootRemId", package.Manifest.Target.RootRemId }, { "dateRemId", date.RemId },
                    { "clipRemId", clipRemId }, { "outcome", clipOutcome }
                });
            return new StableWorkerRunResult
            {
                Status = "completed", Outputs = new List<IDictionary<string, object>> { output },
                Payload = new Dictionary<string, object>
                {
                    { "sink", "mcp-direct" }, { "route", "clip-daily-route" }, { "outcome", clipOutcome },
                    { "rootRemId", package.Manifest.Target.RootRemId }, { "dateRemId", date.RemId }, { "clipRemId", clipRemId }, { "readbackVerified", true }
                },
                SideEffects = new List<IDictionary<string, object>>
                {
                    new Dictionary<string, object> { { "kind", "remnote.date-node" }, { "idempotencyKey", request.IdempotencyKey }, { "state", "verified" }, { "remId", date.RemId }, { "outcome", date.Outcome } },
                    new Dictionary<string, object> { { "kind", "remnote.clip" }, { "idempotencyKey", request.IdempotencyKey }, { "state", "verified" }, { "remId", clipRemId }, { "receiptRef", output } }
                }
            };
        }

        private static StableWorkerRunResult PublishVerifiedReceipt(StableWorkerRequest request, ExportTaskPackage package, string documentId, string outcome, string markdown, IList<RemNoteMediaReadbackSnapshot> mediaReadback = null)
        {
            var imagesHandled = package.OriginalMedia != null;
            var receipt = new Dictionary<string, object>
            {
                { "schemaVersion", package.Manifest.SchemaVersion == "remnote-export-task.v3" ? "3.0.0" : package.Manifest.SchemaVersion == "remnote-export-task.v2" ? "2.0.0" : "1.0.0" }, { "sink", "mcp-direct" }, { "outcome", outcome }, { "documentRemId", documentId },
                { "documentTitle", package.Manifest.DocumentTitle }, { "rootRemId", package.Manifest.Target.RootRemId },
                { "idempotencyKey", request.IdempotencyKey }, { "contentSha256", ExportTaskPackage.Sha256Text(markdown) },
                { "route", RouteKind(package) }, { "readbackVerified", true }, { "duplicateCount", 0 }, { "dateRouting", false }, { "imagesHandled", imagesHandled },
                { "originalMedia", imagesHandled ? RemNoteOriginalMediaValidator.ReceiptItems(mediaReadback) : new object[0] }
            };
            var output = StableWorkerAdapter.PublishArtifact(request, "remnote-write-receipt", "remnote.write-receipt", "application/json", "receipt.json",
                new UTF8Encoding(false).GetBytes(Json.New().Serialize(receipt)), new Dictionary<string, object> { { "documentRemId", documentId }, { "outcome", outcome } });
            return new StableWorkerRunResult
            {
                Status = "completed", Outputs = new List<IDictionary<string, object>> { output },
                Payload = new Dictionary<string, object> { { "sink", "mcp-direct" }, { "route", RouteKind(package) }, { "outcome", outcome }, { "documentRemId", documentId }, { "readbackVerified", true }, { "imagesHandled", imagesHandled } },
                SideEffects = new List<IDictionary<string, object>> { new Dictionary<string, object>
                    {
                        { "kind", "remnote.document" }, { "idempotencyKey", request.IdempotencyKey }, { "state", "verified" }, { "receiptRef", output }
                    }
                }
            };
        }

        private static string RouteKind(ExportTaskPackage package)
        {
            return package.Manifest.Route == null ? "card-export" : package.Manifest.Route.Kind;
        }
    }
}

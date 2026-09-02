using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper
{
    internal sealed class RemNoteProbeStatusSnapshot
    {
        public bool Connected;
        public bool AcceptWriteOperations;
        public string ServerVersion;
        public string PluginVersion;
    }

    internal interface IRemNoteReadOnlyProbeGateway
    {
        Task<RemNoteProbeStatusSnapshot> ProbeStatusAsync(int timeoutMilliseconds, CancellationToken cancellationToken);
        Task<List<RemNoteDocumentSnapshot>> ProbeSearchAsync(string exactTitle, int timeoutMilliseconds, CancellationToken cancellationToken);
        Task<List<RemNoteDocumentSnapshot>> ProbeListChildrenAsync(string parentRemId, int timeoutMilliseconds, CancellationToken cancellationToken);
        Task<RemNoteDocumentSnapshot> ProbeReadAsync(string remId, int timeoutMilliseconds, CancellationToken cancellationToken);
    }

    internal sealed class RemNoteProbeFileRef
    {
        public string Path;
        public string Sha256;
    }

    internal sealed class RemNoteReadOnlyProbeRequest
    {
        internal const string SchemaVersionValue = "remnote-readonly-probe-request.v1";
        internal const int TimeoutMilliseconds = 15000;
        public string ProbeId;
        public string Mode;
        public string AuthorizationScope;
        public string RootRemId;
        public string RootTitle;
        public RemNoteProbeFileRef SettingsSnapshot;
        public RemNoteProbeFileRef WorkerRequest;
        public RemNoteProbeFileRef WorkerResult;

        public static RemNoteReadOnlyProbeRequest Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !LongPathFileSystem.FileExists(path))
                throw new StableWorkerContractException("invalid_probe_path", "probe request 必须是现有绝对文件路径");
            IDictionary<string, object> value;
            try { value = Json.Dict(Json.New().DeserializeObject(LongPathFileSystem.ReadAllText(path, new UTF8Encoding(false, true)))); }
            catch (Exception ex) { throw new StableWorkerContractException("invalid_probe_request", "probe request 不是严格 UTF-8 JSON object", ex); }
            Strict.Object(value, "probe request");
            Strict.Only(value, "probe request", "schemaVersion", "probeId", "mode", "timeoutMs", "authorizationScope", "target", "operations", "evidence");
            if (Strict.Text(value, "schemaVersion") != SchemaVersionValue) throw new StableWorkerContractException("unsupported_probe", "不支持的 probe schemaVersion");
            var mode = Strict.Text(value, "mode");
            if (mode != "health" && mode != "reconcile") throw new StableWorkerContractException("invalid_probe_mode", "probe mode 必须是 health 或 reconcile");
            object timeout;
            if (!value.TryGetValue("timeoutMs", out timeout) || Convert.ToInt32(timeout, CultureInfo.InvariantCulture) != TimeoutMilliseconds)
                throw new StableWorkerContractException("invalid_probe_timeout", "只读 probe timeoutMs 固定为 15000");
            var scope = Strict.Text(value, "authorizationScope");
            if (scope != "configured-destination" && scope != "isolated-canary")
                throw new StableWorkerContractException("invalid_authorization_scope", "authorizationScope 无效");
            var target = Json.Dict(Strict.Get(value, "target"));
            Strict.Object(target, "probe target");
            Strict.Only(target, "probe target", "rootRemId", "rootTitle");
            var rootRemId = Strict.RequiredText(target, "rootRemId", "probe target");
            var rootTitle = Strict.RequiredText(target, "rootTitle", "probe target");
            if (scope == "isolated-canary" && rootTitle == "剪藏")
                throw new StableWorkerContractException("formal_root_forbidden", "isolated Canary 禁止使用正式“剪藏”根");
            var operations = Json.Array(Strict.Get(value, "operations")).Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).ToArray();
            if (!operations.SequenceEqual(new[] { "status", "search", "list", "read" }))
                throw new StableWorkerContractException("invalid_probe_operations", "只读 probe operations 必须严格为 status/search/list/read");
            var evidence = Json.Dict(Strict.Get(value, "evidence"));
            Strict.Object(evidence, "probe evidence");
            Strict.OnlyOptional(evidence, "probe evidence", new[] { "settingsSnapshot" }, new[] { "workerRequest", "workerResult" });
            var settings = ParseFileRef(Json.Dict(Strict.Get(evidence, "settingsSnapshot")), "settingsSnapshot");
            RemNoteProbeFileRef workerRequest = null;
            RemNoteProbeFileRef workerResult = null;
            if (evidence.ContainsKey("workerRequest")) workerRequest = ParseFileRef(Json.Dict(evidence["workerRequest"]), "workerRequest");
            if (evidence.ContainsKey("workerResult")) workerResult = ParseFileRef(Json.Dict(evidence["workerResult"]), "workerResult");
            if (mode == "reconcile" && (workerRequest == null || workerResult == null))
                throw new StableWorkerContractException("missing_reconcile_evidence", "reconcile 必须绑定 workerRequest 与 workerResult");
            if (mode == "health" && (workerRequest != null || workerResult != null))
                throw new StableWorkerContractException("unexpected_reconcile_evidence", "health probe 不接受 workerRequest/workerResult");
            return new RemNoteReadOnlyProbeRequest
            {
                ProbeId = Strict.RequiredText(value, "probeId", "probe request"), Mode = mode, AuthorizationScope = scope,
                RootRemId = rootRemId, RootTitle = rootTitle, SettingsSnapshot = settings,
                WorkerRequest = workerRequest, WorkerResult = workerResult
            };
        }

        private static RemNoteProbeFileRef ParseFileRef(IDictionary<string, object> value, string label)
        {
            Strict.Object(value, label);
            Strict.Only(value, label, "path", "sha256");
            var path = Strict.RequiredText(value, "path", label);
            var sha = Strict.RequiredText(value, "sha256", label).ToLowerInvariant();
            if (!System.IO.Path.IsPathRooted(path) || !LongPathFileSystem.FileExists(path))
                throw new StableWorkerContractException("missing_probe_evidence", label + " path 必须是现有绝对文件");
            if (sha.Length != 64 || sha.Any(ch => !Uri.IsHexDigit(ch)))
                throw new StableWorkerContractException("invalid_probe_evidence_hash", label + " sha256 无效");
            return new RemNoteProbeFileRef { Path = System.IO.Path.GetFullPath(path), Sha256 = sha };
        }
    }

    internal sealed class RemNoteProbeEvidence
    {
        public string Status;
        public string Route;
        public string LocalDate;
        public string DocumentTitle;
        public string WorkerStatus;
        public string IdempotencyKey;
        public bool UnknownExternalState;
        public string Detail;
        public IDictionary<string, object> Hashes = new Dictionary<string, object>();
    }

    internal sealed class RemNoteProbeCheck
    {
        public string Operation;
        public string Outcome;
        public long DurationMs;
        public string Detail;
        public IDictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "operation", Operation }, { "outcome", Outcome }, { "durationMs", DurationMs }, { "detail", Detail }
            };
        }
    }

    internal sealed class RemNoteReadOnlyProbeResult
    {
        public string ProbeId;
        public string Mode;
        public string State = "healthy";
        public string Admission = "ready";
        public int ExternalCallCount;
        public bool? Connected;
        public bool? AcceptWriteOperations;
        public string ServerVersion;
        public string PluginVersion;
        public string RootRemId;
        public string ExpectedRootTitle;
        public string ObservedRootTitle;
        public bool RootBindingMatched;
        public string AuthorizationScope;
        public RemNoteProbeEvidence Evidence;
        public List<RemNoteProbeCheck> Checks = new List<RemNoteProbeCheck>();
        public List<string> ObservedRemIds = new List<string>();
        public List<string> Reasons = new List<string>();

        public IDictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", "remnote-readonly-probe-result.v1" }, { "probeId", ProbeId }, { "mode", Mode },
                { "state", State }, { "admission", Admission }, { "timeoutMs", RemNoteReadOnlyProbeRequest.TimeoutMilliseconds },
                { "externalCall", true }, { "externalCallCount", ExternalCallCount }, { "externalWriteCount", 0 },
                { "connected", Connected }, { "acceptWriteOperations", AcceptWriteOperations },
                { "serverVersion", ServerVersion }, { "pluginVersion", PluginVersion },
                { "targetBinding", new Dictionary<string, object>
                    {
                        { "authority", "rootRemId+exact-readback" }, { "authorizationScope", AuthorizationScope },
                        { "rootRemId", RootRemId }, { "expectedRootTitle", ExpectedRootTitle },
                        { "observedRootTitle", ObservedRootTitle }, { "matched", RootBindingMatched }
                    }
                },
                { "toolChecks", Checks.Select(item => item.ToDictionary()).ToArray() }, { "reasons", Reasons.ToArray() },
                { "evidenceChain", new Dictionary<string, object>
                    {
                        { "status", Evidence.Status }, { "route", Evidence.Route }, { "localDate", Evidence.LocalDate },
                        { "documentTitle", Evidence.DocumentTitle }, { "workerStatus", Evidence.WorkerStatus },
                        { "idempotencyKey", Evidence.IdempotencyKey }, { "unknownExternalState", Evidence.UnknownExternalState },
                        { "automaticRetryAllowed", false }, { "detail", Evidence.Detail }, { "hashes", Evidence.Hashes }
                    }
                },
                { "observedRemIds", ObservedRemIds.ToArray() }, { "generatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
        }
    }

    internal static class RemNoteProbeEvidenceValidator
    {
        public static RemNoteProbeEvidence Validate(RemNoteReadOnlyProbeRequest request)
        {
            var evidence = new RemNoteProbeEvidence { Status = "valid", Detail = "settings snapshot bound" };
            try
            {
                var settings = LoadBound(request.SettingsSnapshot, "settingsSnapshot", evidence);
                Strict.Only(settings, "settings snapshot", "schemaVersion", "destinationType", "enabled", "target", "advanced", "secretReferences");
                if (Strict.Text(settings, "schemaVersion") != RemNoteSettingsAdapter.SnapshotSchemaVersion || Strict.Text(settings, "destinationType") != "remnote")
                    throw new StableWorkerContractException("settings_snapshot_mismatch", "settings snapshot identity 不匹配");
                object enabled;
                if (!settings.TryGetValue("enabled", out enabled) || !(enabled is bool) || !(bool)enabled)
                    throw new StableWorkerContractException("destination_disabled", "RemNote destination 未启用");
                var target = Json.Dict(Strict.Get(settings, "target"));
                Strict.Object(target, "settings snapshot target");
                Strict.Only(target, "settings snapshot target", "rootRemId", "rootTitle");
                if (Strict.Text(target, "rootRemId") != request.RootRemId || Strict.Text(target, "rootTitle") != request.RootTitle)
                    throw new StableWorkerContractException("target_snapshot_mismatch", "probe target 与 settings snapshot 不一致");
                var advanced = Json.Dict(Strict.Get(settings, "advanced"));
                Strict.Object(advanced, "settings snapshot advanced");
                Strict.Only(advanced, "settings snapshot advanced", "sink", "clipRouting", "cardRouting", "timeZone", "dateFormat");
                if (Strict.Text(advanced, "clipRouting") != "clip-daily-route" || Strict.Text(advanced, "cardRouting") != "card-export" ||
                    Strict.Text(advanced, "timeZone") != "Asia/Shanghai" || Strict.Text(advanced, "dateFormat") != "yyyy-MM-dd")
                    throw new StableWorkerContractException("settings_policy_mismatch", "settings 固定路由语义不匹配");
                if (request.Mode == "health") return evidence;

                var workerRequest = LoadBound(request.WorkerRequest, "workerRequest", evidence);
                var workerResult = LoadBound(request.WorkerResult, "workerResult", evidence);
                Strict.Object(workerRequest, "worker request evidence");
                if (Strict.Text(workerRequest, "schemaVersion") != "1.0.0" || Strict.Text(workerRequest, "workerId") != StableWorkerAdapter.WorkerId ||
                    Strict.Text(workerRequest, "operation") != StableWorkerAdapter.Operation || Strict.Text(workerRequest, "stageId") != StableWorkerAdapter.StageId)
                    throw new StableWorkerContractException("worker_request_identity_mismatch", "worker request identity 不匹配");
                var payload = Json.Dict(Strict.Get(workerRequest, "payload"));
                Strict.Object(payload, "worker request payload");
                Strict.Only(payload, "worker request payload", "schemaVersion", "documentTitle", "sink", "target", "route");
                if (Strict.Text(payload, "schemaVersion") != "2.0.0") throw new StableWorkerContractException("payload_version_mismatch", "reconcile 只接受 payload v2");
                var payloadTarget = Json.Dict(Strict.Get(payload, "target"));
                Strict.Object(payloadTarget, "worker request target");
                Strict.Only(payloadTarget, "worker request target", "rootRemId", "rootTitle");
                if (Strict.Text(payloadTarget, "rootRemId") != request.RootRemId || Strict.Text(payloadTarget, "rootTitle") != request.RootTitle)
                    throw new StableWorkerContractException("worker_target_mismatch", "worker request target 与 settings snapshot 不一致");
                var route = Json.Dict(Strict.Get(payload, "route"));
                Strict.Object(route, "worker request route");
                evidence.Route = Strict.RequiredText(route, "kind", "worker request route");
                if (evidence.Route == "clip-daily-route")
                {
                    Strict.Only(route, "worker request route", "kind", "timeZone", "localDate", "dateFormat");
                    if (Strict.Text(route, "timeZone") != "Asia/Shanghai" || Strict.Text(route, "dateFormat") != "yyyy-MM-dd")
                        throw new StableWorkerContractException("route_policy_mismatch", "clip route 时区或日期格式不匹配");
                    DateTime parsed;
                    evidence.LocalDate = Strict.RequiredText(route, "localDate", "worker request route");
                    if (!DateTime.TryParseExact(evidence.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                        throw new StableWorkerContractException("invalid_local_date", "localDate 无效");
                }
                else if (evidence.Route == "card-export") Strict.Only(route, "worker request route", "kind");
                else throw new StableWorkerContractException("unsupported_route", "不支持的 reconcile route");
                evidence.DocumentTitle = Strict.RequiredText(payload, "documentTitle", "worker request payload");
                evidence.IdempotencyKey = Strict.RequiredText(workerRequest, "idempotencyKey", "worker request evidence");

                Strict.Object(workerResult, "worker result evidence");
                if (Strict.Text(workerResult, "schemaVersion") != "1.0.0" ||
                    Strict.Text(workerResult, "requestId") != Strict.Text(workerRequest, "requestId") ||
                    Strict.Text(workerResult, "operationId") != Strict.Text(workerRequest, "operationId") ||
                    Strict.Text(workerResult, "taskId") != Strict.Text(workerRequest, "taskId") ||
                    Strict.Text(workerResult, "runId") != Strict.Text(workerRequest, "runId") ||
                    Strict.Text(workerResult, "stageId") != Strict.Text(workerRequest, "stageId") ||
                    Strict.Text(workerResult, "attemptId") != Strict.Text(workerRequest, "attemptId"))
                    throw new StableWorkerContractException("worker_result_binding_mismatch", "worker result 与 request identity 不一致");
                evidence.WorkerStatus = Strict.RequiredText(workerResult, "status", "worker result evidence");
                evidence.UnknownExternalState = evidence.WorkerStatus == "unknown_external_state";
                if (evidence.UnknownExternalState)
                {
                    if (Json.Array(Strict.Get(workerResult, "outputs")).Length != 0)
                        throw new StableWorkerContractException("unsafe_unknown_result", "unknown_external_state 禁止发布 outputs");
                    var error = Json.Dict(Strict.Get(workerResult, "error"));
                    object retryable;
                    if (error == null || !error.TryGetValue("retryable", out retryable) || !(retryable is bool) || (bool)retryable)
                        throw new StableWorkerContractException("unsafe_unknown_retry", "unknown_external_state 必须 retryable=false");
                }
                evidence.Detail = "settingsSnapshot→payload.v2→workerRequest→workerResult identity valid";
                return evidence;
            }
            catch (Exception ex)
            {
                evidence.Status = "invalid";
                evidence.Detail = ex.Message;
                return evidence;
            }
        }

        private static IDictionary<string, object> LoadBound(RemNoteProbeFileRef reference, string label, RemNoteProbeEvidence evidence)
        {
            var actual = ExportTaskPackage.Sha256File(reference.Path);
            evidence.Hashes[label] = new Dictionary<string, object> { { "path", reference.Path }, { "sha256", actual } };
            if (!string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new StableWorkerContractException("probe_evidence_hash_mismatch", label + " SHA-256 不匹配");
            try { return Json.Dict(Json.New().DeserializeObject(LongPathFileSystem.ReadAllText(reference.Path, new UTF8Encoding(false, true)))); }
            catch (Exception ex) { throw new StableWorkerContractException("invalid_probe_evidence", label + " 不是严格 UTF-8 JSON object", ex); }
        }
    }

    internal sealed class ProbeCall<T>
    {
        public bool Success;
        public bool Timeout;
        public T Value;
        public RemNoteProbeCheck Check;
    }

    internal sealed class RemNoteReadOnlyProbe
    {
        private readonly IRemNoteReadOnlyProbeGateway gateway;
        public RemNoteReadOnlyProbe(IRemNoteReadOnlyProbeGateway gateway) { this.gateway = gateway; }

        public async Task<RemNoteReadOnlyProbeResult> RunAsync(RemNoteReadOnlyProbeRequest request, CancellationToken cancellationToken)
        {
            var result = new RemNoteReadOnlyProbeResult
            {
                ProbeId = request.ProbeId, Mode = request.Mode, RootRemId = request.RootRemId,
                ExpectedRootTitle = request.RootTitle, AuthorizationScope = request.AuthorizationScope,
                Evidence = RemNoteProbeEvidenceValidator.Validate(request)
            };
            if (result.Evidence.Status != "valid")
            {
                Block(result, "evidence chain invalid: " + result.Evidence.Detail);
                AddNotRun(result, "status", "invalid evidence"); AddNotRun(result, "search", "invalid evidence");
                AddNotRun(result, "list", "invalid evidence"); AddNotRun(result, "read", "invalid evidence");
                return result;
            }

            var status = await Execute("status", () => gateway.ProbeStatusAsync(RemNoteReadOnlyProbeRequest.TimeoutMilliseconds, cancellationToken));
            result.ExternalCallCount++;
            result.Checks.Add(status.Check);
            if (!status.Success) return StopAfterFailure(result, status.Timeout, new[] { "search", "list", "read" });
            result.Connected = status.Value.Connected;
            result.AcceptWriteOperations = status.Value.AcceptWriteOperations;
            result.ServerVersion = status.Value.ServerVersion;
            result.PluginVersion = status.Value.PluginVersion;
            if (!status.Value.Connected) Block(result, "Bridge connected=false");
            if (!status.Value.AcceptWriteOperations) Block(result, "acceptWriteOperations=false");
            if (!status.Value.Connected)
            {
                AddNotRun(result, "search", "Bridge disconnected"); AddNotRun(result, "list", "Bridge disconnected"); AddNotRun(result, "read", "Bridge disconnected");
                return result;
            }

            var search = await Execute("search", () => gateway.ProbeSearchAsync(request.RootTitle, RemNoteReadOnlyProbeRequest.TimeoutMilliseconds, cancellationToken));
            result.ExternalCallCount++;
            result.Checks.Add(search.Check);
            if (!search.Success) return StopAfterFailure(result, search.Timeout, new[] { "list", "read" });
            var exactSearch = search.Value.Where(item => item.Title == request.RootTitle && item.RemId == request.RootRemId).ToArray();
            if (exactSearch.Length != 1) Block(result, "search 未唯一返回绑定 rootRemId");

            var list = await Execute("list", async () =>
            {
                result.ExternalCallCount++;
                var rootChildren = await gateway.ProbeListChildrenAsync(request.RootRemId, RemNoteReadOnlyProbeRequest.TimeoutMilliseconds, cancellationToken);
                if (result.Evidence.Route == "clip-daily-route")
                {
                    var dates = rootChildren.Where(item => item.Title == result.Evidence.LocalDate).ToArray();
                    if (dates.Length == 1)
                    {
                        result.ObservedRemIds.Add(dates[0].RemId);
                        result.ExternalCallCount++;
                        var clips = await gateway.ProbeListChildrenAsync(dates[0].RemId, RemNoteReadOnlyProbeRequest.TimeoutMilliseconds, cancellationToken);
                        foreach (var clip in clips.Where(item => item.Title == result.Evidence.DocumentTitle)) result.ObservedRemIds.Add(clip.RemId);
                    }
                }
                else if (result.Evidence.Route == "card-export")
                    foreach (var cardDocument in rootChildren.Where(item => item.Title == result.Evidence.DocumentTitle)) result.ObservedRemIds.Add(cardDocument.RemId);
                return rootChildren;
            });
            result.Checks.Add(list.Check);
            if (!list.Success) return StopAfterFailure(result, list.Timeout, new[] { "read" });

            var read = await Execute("read", async () =>
            {
                result.ExternalCallCount++;
                var root = await gateway.ProbeReadAsync(request.RootRemId, RemNoteReadOnlyProbeRequest.TimeoutMilliseconds, cancellationToken);
                result.ObservedRootTitle = root == null ? null : root.Title;
                result.RootBindingMatched = root != null && root.RemId == request.RootRemId && root.Title == request.RootTitle && root.RemType == "document";
                if (result.ObservedRemIds.Count > 0)
                {
                    result.ExternalCallCount++;
                    await gateway.ProbeReadAsync(result.ObservedRemIds[result.ObservedRemIds.Count - 1], RemNoteReadOnlyProbeRequest.TimeoutMilliseconds, cancellationToken);
                }
                return root;
            });
            result.Checks.Add(read.Check);
            if (!read.Success) return StopAfterFailure(result, read.Timeout, new string[0]);
            if (!result.RootBindingMatched) Block(result, "rootTitle 与 rootRemId 实际回读不匹配");
            if (result.Evidence.UnknownExternalState) Block(result, "unknown_external_state requires manual reconciliation; automatic retry forbidden");
            result.ObservedRemIds.Insert(0, request.RootRemId);
            return result;
        }

        private static async Task<ProbeCall<T>> Execute<T>(string operation, Func<Task<T>> action)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var value = await action();
                watch.Stop();
                return new ProbeCall<T> { Success = true, Value = value, Check = new RemNoteProbeCheck { Operation = operation, Outcome = "passed", DurationMs = watch.ElapsedMilliseconds, Detail = "read-only call completed" } };
            }
            catch (TimeoutException)
            {
                watch.Stop();
                return new ProbeCall<T> { Timeout = true, Check = new RemNoteProbeCheck { Operation = operation, Outcome = "timeout", DurationMs = watch.ElapsedMilliseconds, Detail = "15s tool request timeout" } };
            }
            catch (Exception ex)
            {
                watch.Stop();
                return new ProbeCall<T> { Check = new RemNoteProbeCheck { Operation = operation, Outcome = "failed", DurationMs = watch.ElapsedMilliseconds, Detail = Safe(ex.Message) } };
            }
        }

        private static RemNoteReadOnlyProbeResult StopAfterFailure(RemNoteReadOnlyProbeResult result, bool timeout, IEnumerable<string> remaining)
        {
            if (timeout) { result.State = "degraded"; result.Admission = "blocked"; result.Reasons.Add("15s read-only tool request timeout"); }
            else Block(result, "read-only tool call failed");
            foreach (var operation in remaining) AddNotRun(result, operation, "fail-fast after prior tool failure");
            return result;
        }

        private static void Block(RemNoteReadOnlyProbeResult result, string detail)
        {
            result.State = "blocked";
            result.Admission = "blocked";
            if (!string.IsNullOrWhiteSpace(detail) && !result.Reasons.Contains(detail)) result.Reasons.Add(detail);
            if (result.Evidence != null && string.IsNullOrWhiteSpace(result.Evidence.Detail)) result.Evidence.Detail = detail;
        }

        private static void AddNotRun(RemNoteReadOnlyProbeResult result, string operation, string detail)
        {
            result.Checks.Add(new RemNoteProbeCheck { Operation = operation, Outcome = "not_run", DurationMs = 0, Detail = detail });
        }

        private static string Safe(string value)
        {
            value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 300 ? value.Substring(0, 300) : value;
        }
    }

    internal static class Strict
    {
        public static object Get(IDictionary<string, object> value, string key) { object raw; return value != null && value.TryGetValue(key, out raw) ? raw : null; }
        public static string Text(IDictionary<string, object> value, string key) { var raw = Get(value, key); return raw == null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture); }
        public static string RequiredText(IDictionary<string, object> value, string key, string label)
        {
            var text = Text(value, key);
            if (string.IsNullOrWhiteSpace(text)) throw new StableWorkerContractException("missing_field", label + " 缺少 " + key);
            return text;
        }
        public static void Object(IDictionary<string, object> value, string label) { if (value == null) throw new StableWorkerContractException("missing_object", label + " 必须是 object"); }
        public static void Only(IDictionary<string, object> value, string label, params string[] allowed)
        {
            Object(value, label);
            var set = new HashSet<string>(allowed, StringComparer.Ordinal);
            var unknown = value.Keys.Where(key => !set.Contains(key)).ToArray();
            if (unknown.Length != 0) throw new StableWorkerContractException("unknown_field", label + " 含未知字段: " + string.Join(", ", unknown));
            foreach (var key in allowed) if (!value.ContainsKey(key)) throw new StableWorkerContractException("missing_field", label + " 缺少 " + key);
        }
        public static void OnlyOptional(IDictionary<string, object> value, string label, string[] required, string[] optional)
        {
            Object(value, label);
            var set = new HashSet<string>(required.Concat(optional), StringComparer.Ordinal);
            var unknown = value.Keys.Where(key => !set.Contains(key)).ToArray();
            if (unknown.Length != 0) throw new StableWorkerContractException("unknown_field", label + " 含未知字段: " + string.Join(", ", unknown));
            foreach (var key in required) if (!value.ContainsKey(key)) throw new StableWorkerContractException("missing_field", label + " 缺少 " + key);
        }
    }
}

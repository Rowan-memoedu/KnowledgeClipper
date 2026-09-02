using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper
{
    internal sealed class RemNotePublicProbeRequest
    {
        internal const string SchemaVersionValue = "remnote-public-probe-request.v2";
        internal const int TimeoutMilliseconds = 15000;
        public string ProbeId;
        public string AuthorizationScope;
        public string RootRemId;
        public string RootTitle;

        public static RemNotePublicProbeRequest Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !LongPathFileSystem.FileExists(path))
                throw new StableWorkerContractException("invalid_probe_path", "public probe request 必须是现有绝对文件路径");
            IDictionary<string, object> value;
            try { value = Json.Dict(Json.New().DeserializeObject(LongPathFileSystem.ReadAllText(path, new UTF8Encoding(false, true)))); }
            catch (Exception ex) { throw new StableWorkerContractException("invalid_probe_request", "public probe request 不是严格 UTF-8 JSON object", ex); }
            Strict.Only(value, "public probe request", "schemaVersion", "probeId", "authorizationScope", "timeoutMs", "operations", "target");
            if (Strict.Text(value, "schemaVersion") != SchemaVersionValue) throw new StableWorkerContractException("unsupported_probe", "不支持的 public probe schemaVersion");
            if (Convert.ToInt32(Strict.Get(value, "timeoutMs"), CultureInfo.InvariantCulture) != TimeoutMilliseconds)
                throw new StableWorkerContractException("invalid_probe_timeout", "public probe timeoutMs 固定为 15000");
            var operations = Json.Array(Strict.Get(value, "operations")).Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).ToArray();
            if (!operations.SequenceEqual(new[] { "status", "search", "list", "read" }))
                throw new StableWorkerContractException("invalid_probe_operations", "public probe operations 必须严格为 status/search/list/read");
            var scope = Strict.RequiredText(value, "authorizationScope", "public probe request");
            if (scope != "configured-destination" && scope != "isolated-canary")
                throw new StableWorkerContractException("invalid_authorization_scope", "authorizationScope 无效");
            var target = Json.Dict(Strict.Get(value, "target"));
            Strict.Only(target, "public probe target", "rootRemId", "rootTitle");
            var rootTitle = Strict.RequiredText(target, "rootTitle", "public probe target");
            if (scope == "isolated-canary" && rootTitle == "剪藏")
                throw new StableWorkerContractException("formal_root_forbidden", "isolated Canary 禁止使用正式“剪藏”根");
            return new RemNotePublicProbeRequest
            {
                ProbeId = Strict.RequiredText(value, "probeId", "public probe request"), AuthorizationScope = scope,
                RootRemId = Strict.RequiredText(target, "rootRemId", "public probe target"), RootTitle = rootTitle
            };
        }
    }

    internal sealed class RemNotePublicProbeCheck
    {
        public string Operation;
        public string Outcome;
        public long DurationMs;
        public string ReasonCode;
        public IDictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "operation", Operation }, { "outcome", Outcome }, { "durationMs", DurationMs }, { "reasonCode", ReasonCode }
            };
        }
    }

    internal sealed class RemNotePublicProbeResult
    {
        public string ProbeId;
        public string State = "healthy";
        public string Admission = "ready";
        public int ExternalCallCount;
        public bool? Connected;
        public bool? AcceptWriteOperations;
        public string ServerVersion;
        public string PluginVersion;
        public string RootRemId;
        public string ExpectedTitle;
        public string ObservedTitle;
        public string ObservedType;
        public bool Matched;
        public string AuthorizationScope;
        public readonly List<RemNotePublicProbeCheck> Checks = new List<RemNotePublicProbeCheck>();
        public readonly List<string> ReasonCodes = new List<string>();

        public IDictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", "remnote-public-probe-result.v2" }, { "probeId", ProbeId },
                { "destination", new Dictionary<string, object> { { "type", "remnote" }, { "optional", true }, { "peerDestinationTypes", new[] { "anki" } } } },
                { "worker", new Dictionary<string, object> { { "id", StableWorkerAdapter.WorkerId }, { "version", StableWorkerAdapter.WorkerVersion }, { "contractVersion", StableWorkerAdapter.ContractVersion } } },
                { "state", State }, { "admission", Admission }, { "timeoutMs", RemNotePublicProbeRequest.TimeoutMilliseconds },
                { "externalCall", true }, { "externalCallCount", ExternalCallCount }, { "externalWriteCount", 0 },
                { "connection", new Dictionary<string, object>
                    {
                        { "connected", Connected }, { "acceptWriteOperations", AcceptWriteOperations },
                        { "serverVersion", ServerVersion }, { "pluginVersion", PluginVersion }, { "endpointClass", "loopback-local" }
                    }
                },
                { "targetBinding", new Dictionary<string, object>
                    {
                        { "authority", "rootRemId+exact-readback" }, { "authorizationScope", AuthorizationScope },
                        { "rootRemId", RootRemId }, { "expectedTitle", ExpectedTitle }, { "observedTitle", ObservedTitle },
                        { "expectedType", "document" }, { "observedType", ObservedType }, { "matched", Matched },
                        { "rootTitleRole", "readback-only-not-authority" }, { "changeEffectiveAt", "next-task" }
                    }
                },
                { "routeCapabilities", new Dictionary<string, object>
                    {
                        { "clip", new Dictionary<string, object> { { "kind", "clip-daily-route" }, { "supported", true }, { "timeZone", "Asia/Shanghai" }, { "dateFormat", "yyyy-MM-dd" }, { "preflight", Matched && AcceptWriteOperations == true ? "ready" : "blocked" } } },
                        { "cards", new Dictionary<string, object> { { "kind", "card-export" }, { "supported", true }, { "dateRouting", false } } },
                        { "originalMedia", new Dictionary<string, object> { { "contractVersion", "remnote-original-media.v2" }, { "readCapability", "media.images.v1" }, { "writeCapability", null }, { "status", "unsupported" }, { "reasonCode", "mcp.media_write_unavailable" } } }
                    }
                },
                { "fixedSemantics", new Dictionary<string, object> { { "readback", "exact" }, { "idempotency", "reconcilable" }, { "unknownExternalStateRetry", "manual-only" }, { "automaticRetryAllowed", false } } },
                { "toolChecks", Checks.Select(item => item.ToDictionary()).ToArray() }, { "reasonCodes", ReasonCodes.ToArray() },
                { "sanitized", true }, { "generatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
        }
    }

    internal sealed class RemNotePublicProbe
    {
        private readonly IRemNoteReadOnlyProbeGateway gateway;
        public RemNotePublicProbe(IRemNoteReadOnlyProbeGateway gateway) { this.gateway = gateway; }

        public async Task<RemNotePublicProbeResult> RunAsync(RemNotePublicProbeRequest request, CancellationToken cancellationToken)
        {
            var result = new RemNotePublicProbeResult
            {
                ProbeId = request.ProbeId, RootRemId = request.RootRemId, ExpectedTitle = request.RootTitle,
                AuthorizationScope = request.AuthorizationScope
            };
            var status = await Call("status", () => gateway.ProbeStatusAsync(RemNotePublicProbeRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, status);
            if (!status.Success) return Stop(result, status.Timeout, new[] { "search", "list", "read" });
            result.Connected = status.Value.Connected; result.AcceptWriteOperations = status.Value.AcceptWriteOperations;
            result.ServerVersion = status.Value.ServerVersion; result.PluginVersion = status.Value.PluginVersion;
            if (!status.Value.Connected) { Block(result, "bridge.disconnected"); return Stop(result, false, new[] { "search", "list", "read" }); }
            if (!status.Value.AcceptWriteOperations) Block(result, "permission.write_operations_disabled");

            var search = await Call("search", () => gateway.ProbeSearchAsync(request.RootTitle, RemNotePublicProbeRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, search); if (!search.Success) return Stop(result, search.Timeout, new[] { "list", "read" });
            if (search.Value.Count(item => item.RemId == request.RootRemId && item.Title == request.RootTitle && item.RemType == "document") != 1)
                Block(result, "target.search_binding_not_unique");

            var list = await Call("list", () => gateway.ProbeListChildrenAsync(request.RootRemId, RemNotePublicProbeRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, list); if (!list.Success) return Stop(result, list.Timeout, new[] { "read" });

            var read = await Call("read", () => gateway.ProbeReadAsync(request.RootRemId, RemNotePublicProbeRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, read); if (!read.Success) return Stop(result, read.Timeout, new string[0]);
            result.ObservedTitle = read.Value == null ? null : read.Value.Title;
            result.ObservedType = read.Value == null ? null : read.Value.RemType;
            result.Matched = read.Value != null && read.Value.RemId == request.RootRemId && read.Value.Title == request.RootTitle && read.Value.RemType == "document";
            if (!result.Matched) Block(result, "target.exact_readback_mismatch");
            return result;
        }

        private sealed class CallResult<T>
        {
            public bool Success; public bool Timeout; public T Value; public RemNotePublicProbeCheck Check;
        }

        private static async Task<CallResult<T>> Call<T>(string operation, Func<Task<T>> action)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var value = await action(); watch.Stop();
                return new CallResult<T> { Success = true, Value = value, Check = new RemNotePublicProbeCheck { Operation = operation, Outcome = "passed", DurationMs = watch.ElapsedMilliseconds, ReasonCode = "ok" } };
            }
            catch (TimeoutException)
            {
                watch.Stop(); return new CallResult<T> { Timeout = true, Check = new RemNotePublicProbeCheck { Operation = operation, Outcome = "timeout", DurationMs = watch.ElapsedMilliseconds, ReasonCode = "tool.timeout.15s" } };
            }
            catch
            {
                watch.Stop(); return new CallResult<T> { Check = new RemNotePublicProbeCheck { Operation = operation, Outcome = "failed", DurationMs = watch.ElapsedMilliseconds, ReasonCode = "tool.failed" } };
            }
        }

        private static void Record<T>(RemNotePublicProbeResult result, CallResult<T> call) { result.ExternalCallCount++; result.Checks.Add(call.Check); }
        private static RemNotePublicProbeResult Stop(RemNotePublicProbeResult result, bool timeout, IEnumerable<string> remaining)
        {
            if (timeout) { result.State = "degraded"; result.Admission = "blocked"; AddReason(result, "tool.timeout.15s"); }
            else if (result.State == "healthy") Block(result, "tool.failed");
            foreach (var operation in remaining) result.Checks.Add(new RemNotePublicProbeCheck { Operation = operation, Outcome = "not_run", DurationMs = 0, ReasonCode = "prior_check_failed" });
            return result;
        }
        private static void Block(RemNotePublicProbeResult result, string reason)
        {
            result.State = "blocked"; result.Admission = "blocked"; AddReason(result, reason);
        }
        private static void AddReason(RemNotePublicProbeResult result, string reason) { if (!result.ReasonCodes.Contains(reason)) result.ReasonCodes.Add(reason); }
    }
}

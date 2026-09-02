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
    internal sealed class RemNoteTargetDiscoveryRequest
    {
        internal const string SchemaVersionValue = "remnote-target-discovery-request.v1";
        internal const int TimeoutMilliseconds = 15000;
        public string DiscoveryId;
        public string AuthorizationScope;
        public string RootTitle;

        public static RemNoteTargetDiscoveryRequest Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !LongPathFileSystem.FileExists(path))
                throw new StableWorkerContractException("invalid_discovery_path", "target discovery request 必须是现有绝对文件路径");
            IDictionary<string, object> value;
            try { value = Json.Dict(Json.New().DeserializeObject(LongPathFileSystem.ReadAllText(path, new UTF8Encoding(false, true)))); }
            catch (Exception ex) { throw new StableWorkerContractException("invalid_discovery_request", "target discovery request 不是严格 UTF-8 JSON object", ex); }
            Strict.Only(value, "target discovery request", "schemaVersion", "discoveryId", "authorizationScope", "timeoutMs", "operations", "selector");
            if (Strict.Text(value, "schemaVersion") != SchemaVersionValue) throw new StableWorkerContractException("unsupported_discovery", "不支持的 target discovery schemaVersion");
            if (Strict.RequiredText(value, "authorizationScope", "target discovery request") != "configured-destination")
                throw new StableWorkerContractException("invalid_authorization_scope", "target discovery 仅允许 configured-destination");
            if (Convert.ToInt32(Strict.Get(value, "timeoutMs"), CultureInfo.InvariantCulture) != TimeoutMilliseconds)
                throw new StableWorkerContractException("invalid_discovery_timeout", "target discovery timeoutMs 固定为 15000");
            var operations = Json.Array(Strict.Get(value, "operations")).Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).ToArray();
            if (!operations.SequenceEqual(new[] { "status", "search", "list", "read" }))
                throw new StableWorkerContractException("invalid_discovery_operations", "target discovery operations 必须严格为 status/search/list/read");
            var selector = Json.Dict(Strict.Get(value, "selector"));
            Strict.Only(selector, "target discovery selector", "rootTitle", "expectedType", "match", "uniqueness");
            if (Strict.RequiredText(selector, "expectedType", "target discovery selector") != "document" ||
                Strict.RequiredText(selector, "match", "target discovery selector") != "exact" ||
                Strict.RequiredText(selector, "uniqueness", "target discovery selector") != "required")
                throw new StableWorkerContractException("invalid_discovery_selector", "target discovery 仅支持 exact unique document selector");
            return new RemNoteTargetDiscoveryRequest
            {
                DiscoveryId = Strict.RequiredText(value, "discoveryId", "target discovery request"),
                AuthorizationScope = "configured-destination",
                RootTitle = Strict.RequiredText(selector, "rootTitle", "target discovery selector")
            };
        }
    }

    internal sealed class RemNoteTargetDiscoveryResult
    {
        public string DiscoveryId;
        public string RootTitle;
        public string State = "healthy";
        public string Admission = "ready";
        public int ExternalCallCount;
        public bool? Connected;
        public bool? AcceptWriteOperations;
        public string ServerVersion;
        public string PluginVersion;
        public int CandidateCount;
        public string Resolution = "unavailable";
        public string ResolvedRootRemId;
        public string ObservedTitle;
        public string ObservedType;
        public bool ExactReadback;
        public readonly List<RemNotePublicProbeCheck> Checks = new List<RemNotePublicProbeCheck>();
        public readonly List<string> ReasonCodes = new List<string>();

        public IDictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", "remnote-target-discovery-result.v1" }, { "discoveryId", DiscoveryId },
                { "destination", new Dictionary<string, object> { { "type", "remnote" }, { "optional", true }, { "peerDestinationTypes", new[] { "anki" } } } },
                { "worker", new Dictionary<string, object> { { "id", StableWorkerAdapter.WorkerId }, { "version", StableWorkerAdapter.WorkerVersion }, { "contractVersion", StableWorkerAdapter.ContractVersion } } },
                { "state", State }, { "admission", Admission }, { "timeoutMs", RemNoteTargetDiscoveryRequest.TimeoutMilliseconds },
                { "externalCall", true }, { "externalCallCount", ExternalCallCount }, { "externalWriteCount", 0 },
                { "connection", new Dictionary<string, object>
                    {
                        { "connected", Connected }, { "acceptWriteOperations", AcceptWriteOperations },
                        { "serverVersion", ServerVersion }, { "pluginVersion", PluginVersion }, { "endpointClass", "loopback-local" }
                    }
                },
                { "selection", new Dictionary<string, object>
                    {
                        { "authorizationScope", "configured-destination" },
                        { "selector", new Dictionary<string, object> { { "rootTitle", RootTitle }, { "expectedType", "document" }, { "match", "exact" }, { "uniqueness", "required" } } },
                        { "candidateCount", CandidateCount }, { "resolution", Resolution }, { "resolvedRootRemId", ResolvedRootRemId },
                        { "observedTitle", ObservedTitle }, { "observedType", ObservedType }, { "exactReadback", ExactReadback },
                        { "authority", "resolved-rootRemId+exact-readback" }, { "rootTitleRole", "discovery-selector-not-authority" },
                        { "persistableBinding", State == "healthy" && Admission == "ready" && ExactReadback }, { "changeEffectiveAt", "next-task" }
                    }
                },
                { "fixedSemantics", new Dictionary<string, object> { { "automaticRetryAllowed", false }, { "unknownExternalStateRetry", "manual-only" } } },
                { "toolChecks", Checks.Select(item => item.ToDictionary()).ToArray() }, { "reasonCodes", ReasonCodes.ToArray() },
                { "sanitized", true }, { "generatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
        }
    }

    internal sealed class RemNoteTargetDiscovery
    {
        private readonly IRemNoteReadOnlyProbeGateway gateway;
        public RemNoteTargetDiscovery(IRemNoteReadOnlyProbeGateway gateway) { this.gateway = gateway; }

        public async Task<RemNoteTargetDiscoveryResult> RunAsync(RemNoteTargetDiscoveryRequest request, CancellationToken cancellationToken)
        {
            var result = new RemNoteTargetDiscoveryResult { DiscoveryId = request.DiscoveryId, RootTitle = request.RootTitle };
            var status = await Call("status", () => gateway.ProbeStatusAsync(RemNoteTargetDiscoveryRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, status);
            if (!status.Success) return Stop(result, status.Timeout, new[] { "search", "list", "read" });
            result.Connected = status.Value.Connected; result.AcceptWriteOperations = status.Value.AcceptWriteOperations;
            result.ServerVersion = status.Value.ServerVersion; result.PluginVersion = status.Value.PluginVersion;
            if (!status.Value.Connected) { Block(result, "bridge.disconnected"); return Stop(result, false, new[] { "search", "list", "read" }); }
            if (!status.Value.AcceptWriteOperations) Block(result, "permission.write_operations_disabled");

            var search = await Call("search", () => gateway.ProbeSearchAsync(request.RootTitle, RemNoteTargetDiscoveryRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, search);
            if (!search.Success) return Stop(result, search.Timeout, new[] { "list", "read" });
            var exact = search.Value.Where(item => item.Title == request.RootTitle && item.RemType == "document" && !string.IsNullOrWhiteSpace(item.RemId)).ToList();
            result.CandidateCount = exact.Count;
            if (exact.Count == 0)
            {
                result.Resolution = "not_found"; Block(result, "target.discovery_not_found");
                return Stop(result, false, new[] { "list", "read" });
            }
            if (exact.Count != 1)
            {
                result.Resolution = "ambiguous"; Block(result, "target.discovery_ambiguous");
                return Stop(result, false, new[] { "list", "read" });
            }

            result.Resolution = "unique";
            result.ResolvedRootRemId = exact[0].RemId;
            var list = await Call("list", () => gateway.ProbeListChildrenAsync(result.ResolvedRootRemId, RemNoteTargetDiscoveryRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, list); if (!list.Success) return Stop(result, list.Timeout, new[] { "read" });
            var read = await Call("read", () => gateway.ProbeReadAsync(result.ResolvedRootRemId, RemNoteTargetDiscoveryRequest.TimeoutMilliseconds, cancellationToken));
            Record(result, read); if (!read.Success) return Stop(result, read.Timeout, new string[0]);
            result.ObservedTitle = read.Value == null ? null : read.Value.Title;
            result.ObservedType = read.Value == null ? null : read.Value.RemType;
            result.ExactReadback = read.Value != null && read.Value.RemId == result.ResolvedRootRemId && read.Value.Title == request.RootTitle && read.Value.RemType == "document";
            if (!result.ExactReadback) Block(result, "target.exact_readback_mismatch");
            return result;
        }

        private sealed class CallResult<T> { public bool Success; public bool Timeout; public T Value; public RemNotePublicProbeCheck Check; }
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

        private static void Record<T>(RemNoteTargetDiscoveryResult result, CallResult<T> call) { result.ExternalCallCount++; result.Checks.Add(call.Check); }
        private static RemNoteTargetDiscoveryResult Stop(RemNoteTargetDiscoveryResult result, bool timeout, IEnumerable<string> remaining)
        {
            if (timeout) { result.State = "degraded"; result.Admission = "blocked"; AddReason(result, "tool.timeout.15s"); }
            else if (result.State == "healthy") Block(result, "tool.failed");
            foreach (var operation in remaining) result.Checks.Add(new RemNotePublicProbeCheck { Operation = operation, Outcome = "not_run", DurationMs = 0, ReasonCode = "prior_check_failed" });
            return result;
        }
        private static void Block(RemNoteTargetDiscoveryResult result, string reason) { result.State = "blocked"; result.Admission = "blocked"; AddReason(result, reason); }
        private static void AddReason(RemNoteTargetDiscoveryResult result, string reason) { if (!result.ReasonCodes.Contains(reason)) result.ReasonCodes.Add(reason); }
    }
}

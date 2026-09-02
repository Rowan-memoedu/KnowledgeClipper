using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KnowledgeClipper
{
    internal static class RemNoteSettingsAdapter
    {
        internal const string SettingsSchemaVersion = "remnote-destination-settings.v1";
        internal const string SnapshotSchemaVersion = "remnote-destination-settings-snapshot.v1";
        internal const string DescriptorSchemaVersion = "remnote-destination-settings-descriptor.v1";
        internal const string CompanionDescriptorSchemaVersion = "remnote-destination-companion-descriptor.v1";
        internal const string CompanionDescriptorV2SchemaVersion = "remnote-destination-companion-descriptor.v2";
        internal const string CompanionDescriptorV3SchemaVersion = "remnote-destination-companion-descriptor.v3";

        public static IDictionary<string, object> Describe(AppConfig runtime)
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", DescriptorSchemaVersion },
                { "destinationType", "remnote" },
                { "displayName", "RemNote" },
                { "peerDestinationTypes", new[] { "anki" } },
                { "secretReferences", new object[0] },
                { "groups", new object[]
                    {
                        Group("common", new object[]
                        {
                            Field("enabled", "boolean", "user", false, false),
                            Field("target.rootRemId", "string", "user", false, null),
                            Field("target.rootTitle", "readback-label", "diagnostic", true, "剪藏")
                        }),
                        Group("advanced", new object[]
                        {
                            Field("advanced.sink", "enum:mcp-direct|markdown-zip", "user", false, "mcp-direct"),
                            Field("advanced.clipRouting", "const:clip-daily-route", "policy", true, "clip-daily-route"),
                            Field("advanced.cardRouting", "const:card-export", "policy", true, "card-export"),
                            Field("advanced.timeZone", "const:Asia/Shanghai", "policy", true, "Asia/Shanghai"),
                            Field("advanced.dateFormat", "const:yyyy-MM-dd", "policy", true, "yyyy-MM-dd"),
                            Field("advanced.readback", "const:exact", "policy", true, "exact"),
                            Field("advanced.idempotency", "const:reconcilable", "policy", true, "reconcilable"),
                            Field("advanced.unknownExternalStateRetry", "const:manual-only", "policy", true, "manual-only")
                        }),
                        Group("diagnostics", new object[]
                        {
                            Field("runtime.workerVersion", "semantic-version", "diagnostic", true, StableWorkerAdapter.WorkerVersion),
                            Field("runtime.contractVersion", "semantic-version", "diagnostic", true, StableWorkerAdapter.ContractVersion),
                            Field("runtime.nodePath", "absolute-path", "deployment", true, runtime == null ? null : runtime.NodePath),
                            Field("runtime.remnoteCliJsPath", "absolute-path", "deployment", true, runtime == null ? null : runtime.RemnoteCliJsPath),
                            Field("runtime.remnoteMcpUrl", "loopback-mcp-url", "deployment", true, runtime == null ? null : runtime.RemnoteMcpUrl),
                            Field("runtime.probeTimeoutMs", "const:15000", "diagnostic", true, 15000),
                            Field("runtime.probeExternalWriteCount", "const:0", "diagnostic", true, 0)
                        })
                    }
                }
            };
        }

        public static IDictionary<string, object> DescribeCompanion(AppConfig runtime)
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", CompanionDescriptorSchemaVersion },
                { "destinationType", "remnote" }, { "displayName", "RemNote" }, { "optional", true },
                { "peerDestinationTypes", new[] { "anki" } }, { "secretReferences", new object[0] },
                { "settings", new Dictionary<string, object>
                    {
                        { "inputSchemaVersion", SettingsSchemaVersion }, { "snapshotSchemaVersion", SnapshotSchemaVersion },
                        { "enabledPath", "enabled" },
                        { "target", new Dictionary<string, object>
                            {
                                { "rootRemIdPath", "target.rootRemId" }, { "rootTitlePath", "target.rootTitle" },
                                { "authority", "rootRemId+exact-readback" }, { "rootTitleRole", "readback-only-not-authority" },
                                { "isolatedCanaryRequiresAuthorizedRoot", true }, { "formalRootTitle", "剪藏" },
                                { "formalRootAllowedForCanary", false }
                            }
                        }
                    }
                },
                { "routes", new Dictionary<string, object>
                    {
                        { "clip", new Dictionary<string, object> { { "kind", "clip-daily-route" }, { "timeZone", "Asia/Shanghai" }, { "dateFormat", "yyyy-MM-dd" } } },
                        { "cards", new Dictionary<string, object> { { "kind", "card-export" }, { "dateRouting", false } } }
                    }
                },
                { "fixedSemantics", new Dictionary<string, object>
                    {
                        { "readback", "exact" }, { "idempotency", "reconcilable" },
                        { "unknownExternalStateRetry", "manual-only" }, { "automaticRetryAllowed", false }
                    }
                },
                { "diagnostics", new Dictionary<string, object>
                    {
                        { "readOnly", true }, { "workerVersion", StableWorkerAdapter.WorkerVersion },
                        { "contractVersion", StableWorkerAdapter.ContractVersion },
                        { "nodePath", runtime == null ? null : runtime.NodePath },
                        { "remnoteCliJsPath", runtime == null ? null : runtime.RemnoteCliJsPath },
                        { "remnoteMcpEndpoint", runtime == null ? null : runtime.RemnoteMcpUrl },
                        { "connectionPermissionSource", "probe.status" }
                    }
                },
                { "probe", new Dictionary<string, object>
                    {
                        { "requestSchemaVersion", RemNoteReadOnlyProbeRequest.SchemaVersionValue },
                        { "resultSchemaVersion", "remnote-readonly-probe-result.v1" },
                        { "cli", "probe --request <absolute-probe-request.json> --json" },
                        { "operations", new[] { "status", "search", "list", "read" } },
                        { "timeoutMs", RemNoteReadOnlyProbeRequest.TimeoutMilliseconds },
                        { "externalCall", true }, { "externalWriteCount", 0 },
                        { "connectedToolTimeoutState", "degraded" }, { "admissionOnDegraded", "blocked" }
                    }
                }
            };
        }

        public static IDictionary<string, object> DescribeCompanionV2()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", CompanionDescriptorV2SchemaVersion },
                { "destinationType", "remnote" }, { "displayName", "RemNote" }, { "optional", true },
                { "peerDestinationTypes", new[] { "anki" } }, { "secretReferences", new object[0] },
                { "settings", new Dictionary<string, object>
                    {
                        { "enabled", new Dictionary<string, object> { { "ownership", "user" }, { "effectiveAt", "next-task" } } },
                        { "target", new Dictionary<string, object>
                            {
                                { "rootRemIdPath", "target.rootRemId" }, { "rootTitlePath", "target.rootTitle" },
                                { "authority", "rootRemId+exact-readback" }, { "rootTitleRole", "readback-only-not-authority" },
                                { "changeEffectiveAt", "next-task" }, { "inFlightTaskMutation", false },
                                { "isolatedCanaryRequiresAuthorizedRoot", true }, { "formalRootTitle", "剪藏" }, { "formalRootAllowedForCanary", false }
                            }
                        }
                    }
                },
                { "routes", new Dictionary<string, object>
                    {
                        { "clip", new Dictionary<string, object> { { "kind", "clip-daily-route" }, { "timeZone", "Asia/Shanghai" }, { "dateFormat", "yyyy-MM-dd" } } },
                        { "cards", new Dictionary<string, object> { { "kind", "card-export" }, { "dateRouting", false } } }
                    }
                },
                { "fixedSemantics", new Dictionary<string, object>
                    {
                        { "readback", "exact" }, { "idempotency", "reconcilable" },
                        { "unknownExternalStateRetry", "manual-only" }, { "automaticRetryAllowed", false }
                    }
                },
                { "diagnostics", new Dictionary<string, object>
                    {
                        { "deploymentPath", new Dictionary<string, object> { { "readOnly", true }, { "publicValue", "redacted" } } },
                        { "mcpEndpoint", new Dictionary<string, object> { { "readOnly", true }, { "publicValue", "loopback-local" } } },
                        { "workerVersion", StableWorkerAdapter.WorkerVersion }, { "contractVersion", StableWorkerAdapter.ContractVersion }
                    }
                },
                { "publicProbe", new Dictionary<string, object>
                    {
                        { "requestSchemaVersion", RemNotePublicProbeRequest.SchemaVersionValue }, { "resultSchemaVersion", "remnote-public-probe-result.v2" },
                        { "cli", "probe-public --request <absolute-probe-request.json> --json" }, { "operations", new[] { "status", "search", "list", "read" } },
                        { "timeoutMs", RemNotePublicProbeRequest.TimeoutMilliseconds }, { "externalCall", true }, { "externalWriteCount", 0 },
                        { "sanitized", true }, { "connectedToolTimeoutState", "degraded" }, { "admissionOnDegraded", "blocked" }
                    }
                },
                { "originalMedia", new Dictionary<string, object>
                    {
                        { "contractVersion", "remnote-original-media.v2" }, { "ordering", "explicit-role-ordinal" },
                        { "readCapability", "media.images.v1" }, { "writeCapability", null },
                        { "productionStatus", "unsupported" }, { "reasonCode", "mcp.media_write_unavailable" }, { "silentDropAllowed", false }
                    }
                }
            };
        }

        public static IDictionary<string, object> DescribeCompanionV3()
        {
            var descriptor = new Dictionary<string, object>(DescribeCompanionV2());
            descriptor["schemaVersion"] = CompanionDescriptorV3SchemaVersion;
            descriptor["targetDiscovery"] = new Dictionary<string, object>
            {
                { "requestSchemaVersion", RemNoteTargetDiscoveryRequest.SchemaVersionValue },
                { "resultSchemaVersion", "remnote-target-discovery-result.v1" },
                { "cli", "discover-target-public --request <absolute-discovery-request.json> --json" },
                { "authorizationScope", "configured-destination" },
                { "selector", new Dictionary<string, object> { { "expectedType", "document" }, { "match", "exact" }, { "uniqueness", "required" } } },
                { "authorityAfterResolution", "resolved-rootRemId+exact-readback" },
                { "persistOnlyWhen", "healthy+ready+unique+exactReadback" },
                { "changeEffectiveAt", "next-task" },
                { "operations", new[] { "status", "search", "list", "read" } },
                { "timeoutMs", RemNoteTargetDiscoveryRequest.TimeoutMilliseconds }, { "externalCall", true }, { "externalWriteCount", 0 },
                { "automaticRetryAllowed", false }, { "sanitized", true }
            };
            descriptor["originalMedia"] = new Dictionary<string, object>
            {
                { "productPolicy", "by-design-unsupported" }, { "supportedDestination", "anki" },
                { "workerInvocationAllowed", false }, { "reasonCode", "destination.original_media_unsupported" }, { "silentDropAllowed", false }
            };
            return descriptor;
        }

        public static IDictionary<string, object> AdaptFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !LongPathFileSystem.FileExists(path))
                throw new StableWorkerContractException("invalid_settings_path", "settings input 必须是现有绝对文件路径");
            IDictionary<string, object> value;
            try { value = Json.Dict(Json.New().DeserializeObject(LongPathFileSystem.ReadAllText(path, new UTF8Encoding(false, true)))); }
            catch (Exception ex) { throw new StableWorkerContractException("invalid_settings", "settings input 不是严格 UTF-8 JSON object", ex); }
            return Adapt(value);
        }

        public static IDictionary<string, object> Adapt(IDictionary<string, object> value)
        {
            RequireObject(value, "settings");
            RequireOnly(value, "settings", "schemaVersion", "enabled", "target", "advanced");
            if (Text(value, "schemaVersion") != SettingsSchemaVersion) throw new StableWorkerContractException("unsupported_settings", "不支持的 RemNote settings schemaVersion");
            object enabledRaw;
            if (!value.TryGetValue("enabled", out enabledRaw) || !(enabledRaw is bool)) throw new StableWorkerContractException("invalid_settings", "enabled 必须是 boolean");
            var enabled = (bool)enabledRaw;
            var target = Json.Dict(Get(value, "target"));
            RequireObject(target, "settings.target");
            RequireOnly(target, "settings.target", "rootRemId", "rootTitle");
            var rootRemId = Text(target, "rootRemId");
            var rootTitle = Text(target, "rootTitle");
            if (string.IsNullOrWhiteSpace(rootTitle)) rootTitle = "剪藏";
            if (enabled && string.IsNullOrWhiteSpace(rootRemId)) throw new StableWorkerContractException("target_required", "启用 RemNote destination 时必须选择 target.rootRemId");

            var advanced = Json.Dict(Get(value, "advanced"));
            RequireObject(advanced, "settings.advanced");
            RequireOnly(advanced, "settings.advanced", "sink", "clipRouting", "cardRouting", "timeZone", "dateFormat");
            var sink = Text(advanced, "sink");
            if (sink != "mcp-direct" && sink != "markdown-zip") throw new StableWorkerContractException("unsupported_sink", "settings advanced.sink 无效");
            RequireConst(advanced, "clipRouting", "clip-daily-route");
            RequireConst(advanced, "cardRouting", "card-export");
            RequireConst(advanced, "timeZone", "Asia/Shanghai");
            RequireConst(advanced, "dateFormat", "yyyy-MM-dd");

            return new Dictionary<string, object>
            {
                { "schemaVersion", SnapshotSchemaVersion }, { "destinationType", "remnote" }, { "enabled", enabled },
                { "target", new Dictionary<string, object> { { "rootRemId", rootRemId }, { "rootTitle", rootTitle } } },
                { "advanced", new Dictionary<string, object>
                    {
                        { "sink", sink }, { "clipRouting", "clip-daily-route" }, { "cardRouting", "card-export" },
                        { "timeZone", "Asia/Shanghai" }, { "dateFormat", "yyyy-MM-dd" }
                    }
                },
                { "secretReferences", new object[0] }
            };
        }

        private static IDictionary<string, object> Group(string id, object[] fields)
        {
            return new Dictionary<string, object> { { "id", id }, { "fields", fields } };
        }

        private static IDictionary<string, object> Field(string path, string type, string ownership, bool readOnly, object defaultValue)
        {
            return new Dictionary<string, object>
            {
                { "path", path }, { "type", type }, { "ownership", ownership }, { "readOnly", readOnly }, { "default", defaultValue }
            };
        }

        private static void RequireConst(IDictionary<string, object> value, string key, string expected)
        {
            if (Text(value, key) != expected) throw new StableWorkerContractException("invalid_route_policy", key + " 必须为 " + expected);
        }

        private static object Get(IDictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result : null;
        }

        private static string Text(IDictionary<string, object> value, string key)
        {
            var raw = Get(value, key);
            return raw == null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
        }

        private static void RequireObject(IDictionary<string, object> value, string label)
        {
            if (value == null) throw new StableWorkerContractException("missing_object", label + " 必须是 object");
        }

        private static void RequireOnly(IDictionary<string, object> value, string label, params string[] allowed)
        {
            var set = new HashSet<string>(allowed, StringComparer.Ordinal);
            var unknown = value.Keys.Where(key => !set.Contains(key)).ToArray();
            if (unknown.Length != 0) throw new StableWorkerContractException("unknown_field", label + " 含未知字段: " + string.Join(", ", unknown));
            foreach (var key in allowed) if (!value.ContainsKey(key)) throw new StableWorkerContractException("missing_field", label + " 缺少 " + key);
        }
    }
}

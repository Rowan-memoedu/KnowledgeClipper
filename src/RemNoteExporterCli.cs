using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;

namespace KnowledgeClipper
{
    internal static class RemNoteExporterCli
    {
        private static int Main(string[] args)
        {
            LongPathFileSystem.EnsureConfigured();
            try { return RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 2; }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 2 && args[0] == "describe" && args[1] == "--json")
            {
                Console.WriteLine(Json.New().Serialize(StableWorkerAdapter.Describe()));
                return 0;
            }
            if (args.Length == 3 && args[0] == "settings" && args[1] == "describe" && args[2] == "--json")
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                Console.WriteLine(Json.New().Serialize(RemNoteSettingsAdapter.Describe(AppConfig.Load(configPath))));
                return 0;
            }
            if (args.Length == 3 && args[0] == "settings" && args[1] == "companion" && args[2] == "--json")
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                Console.WriteLine(Json.New().Serialize(RemNoteSettingsAdapter.DescribeCompanion(AppConfig.Load(configPath))));
                return 0;
            }
            if (args.Length == 5 && args[0] == "settings" && args[1] == "companion" && args[2] == "--version" && args[3] == "2" && args[4] == "--json")
            {
                Console.WriteLine(Json.New().Serialize(RemNoteSettingsAdapter.DescribeCompanionV2()));
                return 0;
            }
            if (args.Length == 5 && args[0] == "settings" && args[1] == "companion" && args[2] == "--version" && args[3] == "3" && args[4] == "--json")
            {
                Console.WriteLine(Json.New().Serialize(RemNoteSettingsAdapter.DescribeCompanionV3()));
                return 0;
            }
            if (args.Length == 5 && args[0] == "settings" && args[1] == "adapt" && args[2] == "--input" && args[4] == "--json")
            {
                Console.WriteLine(Json.New().Serialize(RemNoteSettingsAdapter.AdaptFile(args[3])));
                return 0;
            }
            if (args.Length == 3 && args[0] == "run" && args[1] == "--request") return await RunStandardAsync(args[2]);
            if (args.Length == 4 && args[0] == "probe" && args[1] == "--request" && args[3] == "--json")
                return await RunReadOnlyProbeAsync(args[2]);
            if (args.Length == 4 && args[0] == "probe-public" && args[1] == "--request" && args[3] == "--json")
                return await RunPublicProbeAsync(args[2]);
            if (args.Length == 4 && args[0] == "discover-target-public" && args[1] == "--request" && args[3] == "--json")
                return await RunTargetDiscoveryAsync(args[2]);
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                Console.WriteLine("Usage: KnowledgeClipper.RemNoteExporter.Worker.exe describe --json | run --request <absolute-request.json>");
                Console.WriteLine("Settings: settings describe --json | settings companion --json | settings companion --version 2 --json | settings companion --version 3 --json | settings adapt --input <absolute-settings.json> --json");
                Console.WriteLine("Read-only probe: probe --request <absolute-probe-request.json> --json | probe-public --request <absolute-probe-request.json> --json | discover-target-public --request <absolute-discovery-request.json> --json");
                Console.WriteLine("Legacy compatibility alias: RemNoteQuickClipper.RemNoteExporter.Worker.exe or RemNoteExporter.exe");
                Console.WriteLine("Legacy task-package mode: RemNoteExporter.exe <task-package> [--sink mcp-direct|markdown-zip] [--attempt-id <stable-id>]");
                return 0;
            }
            Console.Error.WriteLine("DEPRECATED: task-package CLI 仅保留开发兼容；registry 必须使用标准 run --request 入口。");
            var packagePath = args[0];
            string sink = null;
            string attemptId = null;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--sink" && i + 1 < args.Length) sink = args[++i];
                else if (args[i] == "--attempt-id" && i + 1 < args.Length) attemptId = args[++i];
                else throw new InvalidOperationException("未知或不完整参数: " + args[i]);
            }
            if (string.IsNullOrWhiteSpace(attemptId)) attemptId = "attempt-" + Guid.NewGuid().ToString("N");
            var package = ExportTaskPackage.Load(packagePath);
            var selected = string.IsNullOrWhiteSpace(sink) ? package.Manifest.DefaultSink : sink;
            IRemNoteExportGateway gateway = null;
            if (string.IsNullOrWhiteSpace(selected) || selected == "mcp-direct")
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                gateway = new CliRemNoteExportGateway(AppConfig.Load(configPath));
            }
            var result = await new RemNoteExporterEngine(gateway).ExportAsync(package, selected, attemptId);
            Console.WriteLine(Json.New().Serialize(result));
            return 0;
        }

        private static async Task<int> RunReadOnlyProbeAsync(string requestPath)
        {
            var request = RemNoteReadOnlyProbeRequest.Load(requestPath);
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var config = AppConfig.Load(configPath);
            config.NodePath = RequireAbsoluteFile(config.NodePath, "NodePath");
            config.RemnoteCliJsPath = RequireAbsoluteFile(config.RemnoteCliJsPath, "RemnoteCliJsPath");
            config.RemnoteMcpUrl = RequireLoopbackMcp(config.RemnoteMcpUrl);
            var result = await new RemNoteReadOnlyProbe(new CliRemNoteExportGateway(config)).RunAsync(request, System.Threading.CancellationToken.None);
            Console.WriteLine(Json.New().Serialize(result.ToDictionary()));
            return result.State == "healthy" ? 0 : 3;
        }

        private static async Task<int> RunPublicProbeAsync(string requestPath)
        {
            var request = RemNotePublicProbeRequest.Load(requestPath);
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var config = AppConfig.Load(configPath);
            config.NodePath = RequireAbsoluteFile(config.NodePath, "NodePath");
            config.RemnoteCliJsPath = RequireAbsoluteFile(config.RemnoteCliJsPath, "RemnoteCliJsPath");
            config.RemnoteMcpUrl = RequireLoopbackMcp(config.RemnoteMcpUrl);
            var result = await new RemNotePublicProbe(new CliRemNoteExportGateway(config)).RunAsync(request, System.Threading.CancellationToken.None);
            Console.WriteLine(Json.New().Serialize(result.ToDictionary()));
            return result.State == "healthy" ? 0 : 3;
        }

        private static async Task<int> RunTargetDiscoveryAsync(string requestPath)
        {
            var request = RemNoteTargetDiscoveryRequest.Load(requestPath);
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var config = AppConfig.Load(configPath);
            config.NodePath = RequireAbsoluteFile(config.NodePath, "NodePath");
            config.RemnoteCliJsPath = RequireAbsoluteFile(config.RemnoteCliJsPath, "RemnoteCliJsPath");
            config.RemnoteMcpUrl = RequireLoopbackMcp(config.RemnoteMcpUrl);
            var result = await new RemNoteTargetDiscovery(new CliRemNoteExportGateway(config)).RunAsync(request, System.Threading.CancellationToken.None);
            Console.WriteLine(Json.New().Serialize(result.ToDictionary()));
            return result.State == "healthy" ? 0 : 3;
        }

        private static async Task<int> RunStandardAsync(string requestPath)
        {
            var startedAt = DateTime.UtcNow;
            StableWorkerRequest request = null;
            try
            {
                request = StableWorkerAdapter.ReadRequest(requestPath);
                using (var cancellation = new WorkerCancellationMonitor(request))
                {
                    ExportTaskPackage package;
                    try { package = StableWorkerAdapter.LoadPackage(request); }
                    catch (Exception ex)
                    {
                        var terminal = Failure(request, ex is StableWorkerContractException ? ((StableWorkerContractException)ex).Code : "invalid_export_request", "contract", false, ex.Message, "input");
                        StableWorkerAdapter.PublishResult(request, startedAt, terminal);
                        WriteProgress(request, terminal.Status);
                        return 1;
                    }

                    StableWorkerRunResult result;
                    try
                    {
                        var worker = new RemNoteStableWorker(package.Manifest.DefaultSink == "mcp-direct" ? new CliRemNoteExportGateway(LoadWorkerConfig(request)) : null);
                        result = await worker.RunAsync(request, package, cancellation);
                    }
                    catch (ExternalStateUnknownException ex)
                    {
                        result = Failure(request, "effect.commit_unknown", "effect", false, ex.Message, "unknown");
                        result.Status = "unknown_external_state";
                        result.Warnings.Add("manual reconciliation required; do not retry automatically");
                        result.SideEffects.Add(new Dictionary<string, object> { { "kind", "remnote.document" }, { "idempotencyKey", request.IdempotencyKey }, { "state", "unknown" } });
                    }
                    catch (OperationCanceledException)
                    {
                        var signal = cancellation.Signal;
                        var reason = signal == null ? "task_cancel" : signal.Reason;
                        var timeout = reason == "timeout";
                        result = Failure(request, "cancel." + reason, timeout ? "transient" : "cancelled", timeout, "attempt 已按 " + reason + " 取消", timeout ? "orchestrator" : "user");
                        result.Status = timeout ? "failed" : "cancelled";
                        result.Payload["cancelReason"] = reason;
                    }
                    catch (StableWorkerContractException ex) { result = Failure(request, ex.Code, "contract", false, ex.Message, "input"); }
                    catch (TimeoutException ex) { result = Failure(request, "dependency.timeout", "transient", true, ex.Message, "dependency"); }
                    catch (Exception ex) { result = Failure(request, "worker.failed", "worker", false, ex.Message, "worker"); }
                    StableWorkerAdapter.PublishResult(request, startedAt, result);
                    WriteProgress(request, result.Status);
                    return result.Status == "completed" || result.Status == "cancelled" ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Json.New().Serialize(new Dictionary<string, object>
                {
                    { "schemaVersion", "1.0.0" }, { "status", "failed" },
                    { "error", new Dictionary<string, object> { { "code", ex is StableWorkerContractException ? ((StableWorkerContractException)ex).Code : "worker_cli_error" }, { "category", "contract" }, { "retryable", false }, { "message", ex.Message } } }
                }));
                return 2;
            }
        }

        private static StableWorkerRunResult Failure(StableWorkerRequest request, Exception ex, string category, bool retryable, string message, string blame)
        {
            return Failure(request, ex is StableWorkerContractException ? ((StableWorkerContractException)ex).Code : "worker.failed", category, retryable, message, blame);
        }

        private static StableWorkerRunResult Failure(StableWorkerRequest request, string code, string category, bool retryable, string message, string blame)
        {
            return new StableWorkerRunResult
            {
                Status = "failed", Error = StableWorkerAdapter.WriteError(request, code, category, retryable, message, blame),
                Payload = new Dictionary<string, object> { { "sink", StableWorkerAdapter.String(request.Payload, "sink") }, { "readbackVerified", false } }
            };
        }

        private static AppConfig LoadWorkerConfig(StableWorkerRequest request)
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var config = AppConfig.Load(configPath);
            ApplyConfig(request.ConfigSnapshot, "nodePath", delegate(string value) { config.NodePath = RequireAbsoluteFile(value, "configSnapshot.nodePath"); });
            ApplyConfig(request.ConfigSnapshot, "remnoteCliJsPath", delegate(string value) { config.RemnoteCliJsPath = RequireAbsoluteFile(value, "configSnapshot.remnoteCliJsPath"); });
            ApplyConfig(request.ConfigSnapshot, "remnoteMcpUrl", delegate(string value) { config.RemnoteMcpUrl = RequireLoopbackMcp(value); });
            config.NodePath = RequireAbsoluteFile(config.NodePath, "NodePath");
            config.RemnoteCliJsPath = RequireAbsoluteFile(config.RemnoteCliJsPath, "RemnoteCliJsPath");
            config.RemnoteMcpUrl = RequireLoopbackMcp(config.RemnoteMcpUrl);
            return config;
        }

        private static void ApplyConfig(IDictionary<string, object> snapshot, string key, Action<string> apply)
        {
            object value;
            if (snapshot != null && snapshot.TryGetValue(key, out value) && value != null) apply(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static string RequireAbsoluteFile(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || !LongPathFileSystem.FileExists(value)) throw new StableWorkerContractException("invalid_runtime_config", field + " 必须是现有绝对文件路径");
            return Path.GetFullPath(value);
        }

        private static string RequireLoopbackMcp(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.AbsolutePath.TrimEnd('/') != "/mcp")
                throw new StableWorkerContractException("nonlocal_remnote_route", "RemNote MCP 只允许本机 http loopback /mcp 路径");
            return uri.AbsoluteUri.TrimEnd('/');
        }

        private static void WriteProgress(StableWorkerRequest request, string status)
        {
            Console.WriteLine(Json.New().Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "requestId", request.RequestId }, { "operation", StableWorkerAdapter.Operation }, { "status", status }
            }));
        }
    }
}

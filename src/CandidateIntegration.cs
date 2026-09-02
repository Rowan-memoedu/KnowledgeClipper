using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal sealed class CandidateIntegrationConfig
    {
        public string SchemaVersion { get; set; }
        public bool AutoStart { get; set; }
        public string OrchestratorExecutable { get; set; }
        public string IpcConfig { get; set; }
        public string Registry { get; set; }
        public string ReleaseRoot { get; set; }
        public string JobsRoot { get; set; }
        public string PipeName { get; set; }
        public int MaxActiveTasks { get; set; }
        public string AuthorityCheckpointId { get; set; }
        public string AuthorityTreeSha256 { get; set; }

        internal static CandidateIntegrationConfig Load(string baseDirectory)
        {
            var root = Path.GetFullPath(baseDirectory);
            var path = Path.Combine(root, "candidate-integration.json");
            if (!File.Exists(path)) return null;
            var value = new JavaScriptSerializer().Deserialize<CandidateIntegrationConfig>(File.ReadAllText(path, Encoding.UTF8));
            if (value == null || value.SchemaVersion != "knowledge-clipper-candidate-integration.v1") throw new InvalidDataException("candidate-integration.schema-invalid");
            value.OrchestratorExecutable = ResolveInside(root, value.OrchestratorExecutable, "orchestrator");
            value.IpcConfig = ResolveInside(root, value.IpcConfig, "ipc-config");
            value.Registry = ResolveInside(root, value.Registry, "registry");
            value.ReleaseRoot = ResolveInside(root, value.ReleaseRoot, "release-root");
            value.JobsRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.JobsRoot ?? "%LOCALAPPDATA%\\KnowledgeClipper\\jobs"));
            value.PipeName = string.IsNullOrWhiteSpace(value.PipeName) ? NamedPipeCommonIpcClient.DefaultPipeName : value.PipeName.Trim();
            if (value.MaxActiveTasks < 1) value.MaxActiveTasks = 4;
            return value;
        }

        private static string ResolveInside(string root, string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("candidate-integration." + name + "-missing");
            var expanded = Environment.ExpandEnvironmentVariables(value);
            var path = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(root, expanded));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("candidate-integration." + name + "-outside-candidate");
            return path;
        }
    }

    internal sealed class CandidateOrchestratorHost : IDisposable
    {
        private readonly Process process;
        internal CandidateIntegrationConfig Configuration { get; private set; }
        internal bool StartedByDesktop { get { return process != null; } }
        internal int? ProcessId { get { return process == null ? (int?)null : process.Id; } }

        private CandidateOrchestratorHost(CandidateIntegrationConfig configuration, Process process)
        {
            Configuration = configuration;
            this.process = process;
        }

        internal static CandidateOrchestratorHost StartIfConfigured(string baseDirectory, string canaryJobsRoot = null, DesktopCredentialSettings credentials = null)
        {
            var configuration = CandidateIntegrationConfig.Load(baseDirectory);
            if (configuration == null || !configuration.AutoStart) return new CandidateOrchestratorHost(configuration, null);
            if (!string.IsNullOrWhiteSpace(canaryJobsRoot)) configuration.JobsRoot = Path.GetFullPath(canaryJobsRoot);
            Validate(configuration);
            Directory.CreateDirectory(configuration.JobsRoot);
            if (PipeAvailable(configuration.PipeName, 150)) return new CandidateOrchestratorHost(configuration, null);

            var start = new ProcessStartInfo
            {
                FileName = configuration.OrchestratorExecutable,
                Arguments = "serve --ipc-config " + Quote(configuration.IpcConfig) + " --jobs-root " + Quote(configuration.JobsRoot) + " --registry " + Quote(configuration.Registry) + " --release-root " + Quote(configuration.ReleaseRoot) + " --max-active-tasks " + configuration.MaxActiveTasks,
                WorkingDirectory = Path.GetDirectoryName(configuration.OrchestratorExecutable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (credentials != null) credentials.ApplyTo(start);
            LoopbackProviderOverrides.ApplyTo(start);
            var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("candidate-orchestrator.start-failed");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (process.HasExited) throw new InvalidOperationException("candidate-orchestrator.exited:" + process.ExitCode);
                if (PipeAvailable(configuration.PipeName, 100)) return new CandidateOrchestratorHost(configuration, process);
                Thread.Sleep(100);
            }
            try { if (!process.HasExited) process.Kill(); } catch { }
            throw new TimeoutException("candidate-orchestrator.pipe-timeout");
        }

        public void Dispose()
        {
            if (process == null) return;
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } } catch { }
            process.Dispose();
        }

        private static void Validate(CandidateIntegrationConfig value)
        {
            if (!File.Exists(value.OrchestratorExecutable)) throw new FileNotFoundException("candidate-orchestrator.executable-missing", value.OrchestratorExecutable);
            if (!File.Exists(value.IpcConfig)) throw new FileNotFoundException("candidate-orchestrator.ipc-config-missing", value.IpcConfig);
            if (!File.Exists(value.Registry)) throw new FileNotFoundException("candidate-orchestrator.registry-missing", value.Registry);
            if (!Directory.Exists(value.ReleaseRoot)) throw new DirectoryNotFoundException("candidate-orchestrator.release-root-missing:" + value.ReleaseRoot);
            if (string.IsNullOrWhiteSpace(value.AuthorityCheckpointId) || string.IsNullOrWhiteSpace(value.AuthorityTreeSha256)) throw new InvalidDataException("candidate-orchestrator.authority-binding-missing");
        }

        private static bool PipeAvailable(string pipeName, int timeoutMilliseconds)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(timeoutMilliseconds);
                    return pipe.IsConnected;
                }
            }
            catch { return false; }
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    }

    internal static class CandidateIntegrationCanary
    {
        internal static int Run(string baseDirectory, string jobsRoot, TextWriter output)
        {
            try
            {
                using (var host = CandidateOrchestratorHost.StartIfConfigured(baseDirectory, jobsRoot))
                {
                    if (host.Configuration == null || !host.Configuration.AutoStart || !host.StartedByDesktop) throw new InvalidOperationException("candidate-orchestrator.was-not-started-by-desktop");
                    var clock = new CandidateCanaryClock();
                    var commands = new FileDesktopCommonCommandSource(jobsRoot, clock, new SystemCommonIdProvider());
                    var result = new NamedPipeCommonIpcClient(host.Configuration.PipeName, 5000).Query(commands.PrepareRecentQuery());
                    var report = new
                    {
                        schemaVersion = "knowledge-clipper-desktop-integration-canary.v1",
                        result = "PASS",
                        orchestratorStartedByDesktop = true,
                        orchestratorProcessId = host.ProcessId,
                        authorityCheckpointId = host.Configuration.AuthorityCheckpointId,
                        authorityTreeSha256 = host.Configuration.AuthorityTreeSha256,
                        action = "task.status.query-recent",
                        queryItemCount = result == null || result.items == null ? 0 : result.items.Count,
                        namedPipeInvoked = true,
                        externalWorkerInvoked = false,
                        physicalSpeakerInvoked = false,
                        remNoteInvoked = false,
                        ankiInvoked = false,
                        mineruInvoked = false
                    };
                    output.WriteLine(new JavaScriptSerializer().Serialize(report));
                    output.Flush();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                output.WriteLine(new JavaScriptSerializer().Serialize(new { schemaVersion = "knowledge-clipper-desktop-integration-canary.v1", result = "FAIL", error = ex.Message }));
                output.Flush();
                return 5;
            }
        }

        private sealed class CandidateCanaryClock : IClock
        {
            public DateTime UtcNow { get { return DateTime.UtcNow; } }
        }
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace KnowledgeClipper
{
    internal static class DesktopIntegrationCanary
    {
        private static int assertions;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "knowledge-clipper-desktop-integration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                SingleInstanceRegression(root);
                CandidatePathRegression(root);
                Console.WriteLine("DESKTOP_INTEGRATION_CANARY PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void SingleInstanceRegression(string root)
        {
            var canonical = "Local\\KnowledgeClipper.Canary." + Guid.NewGuid().ToString("N");
            var legacy = "Local\\KnowledgeClipper.LegacyCanary." + Guid.NewGuid().ToString("N");
            var ready = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var thread = new Thread(delegate ()
            {
                bool created;
                using (var mutex = new Mutex(true, canonical, out created))
                {
                    if (!created) throw new InvalidOperationException("canary mutex collision");
                    ready.Set();
                    release.WaitOne();
                    mutex.ReleaseMutex();
                }
            });
            thread.IsBackground = true;
            thread.Start();
            if (!ready.WaitOne(5000)) throw new TimeoutException("mutex holder did not start");
            try
            {
                var probe = SingleInstanceCoordinator.Probe(canonical, legacy);
                Assert(probe.existingInstanceDetected && probe.canonicalMutexDetected, "existing canonical mutex is detected instead of silently treated as a successful launch");
                Assert(probe.userFeedbackMode == "activate-existing-window-and-message-box", "single-instance collision declares a visible feedback mode");
                var feedback = SingleInstanceCoordinator.NotifyExistingInstance(false, root);
                var log = Path.Combine(root, "single-instance.log");
                Assert(File.Exists(log), "single-instance collision writes a durable diagnostic");
                Assert(File.ReadAllText(log, Encoding.UTF8).Contains("\"result\":\"not-silent\""), "single-instance diagnostic records the non-silent result");
            }
            finally
            {
                release.Set();
                thread.Join(5000);
                ready.Dispose();
                release.Dispose();
            }
        }

        private static void CandidatePathRegression(string root)
        {
            var candidate = Path.Combine(root, "candidate");
            var orchestrator = Path.Combine(candidate, "authority", "win-x64", "orchestrator", "KnowledgeClipper.Orchestrator.exe");
            var ipc = Path.Combine(candidate, "authority", "metadata", "orchestrator-ipc.v1.1.json");
            var registry = Path.Combine(candidate, "authority", "metadata", "worker-registry.v1.json");
            var release = Path.Combine(candidate, "authority", "win-x64");
            Directory.CreateDirectory(Path.GetDirectoryName(orchestrator));
            Directory.CreateDirectory(Path.GetDirectoryName(ipc));
            File.WriteAllText(orchestrator, "fixture");
            File.WriteAllText(ipc, "{}");
            File.WriteAllText(registry, "{}");
            var fixture = new CandidateIntegrationConfig
            {
                SchemaVersion = "knowledge-clipper-candidate-integration.v1",
                AutoStart = true,
                OrchestratorExecutable = "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe",
                IpcConfig = "authority/metadata/orchestrator-ipc.v1.1.json",
                Registry = "authority/metadata/worker-registry.v1.json",
                ReleaseRoot = "authority/win-x64",
                JobsRoot = Path.Combine(root, "jobs"),
                PipeName = "RemNoteQuickClipper.Orchestrator.v1",
                MaxActiveTasks = 4,
                AuthorityCheckpointId = "checkpoint-fixture",
                AuthorityTreeSha256 = new string('a', 64)
            };
            File.WriteAllText(Path.Combine(candidate, "candidate-integration.json"), new JavaScriptSerializer().Serialize(fixture), new UTF8Encoding(false));
            var loaded = CandidateIntegrationConfig.Load(candidate);
            Assert(loaded != null && loaded.AutoStart, "candidate integration configuration enables the built-in orchestrator explicitly");
            Assert(loaded.OrchestratorExecutable == orchestrator && loaded.ReleaseRoot == release, "candidate integration paths resolve from the candidate root");
            Assert(loaded.Registry == registry && loaded.IpcConfig == ipc, "candidate registry and frozen IPC paths resolve without source-tree dependencies");

            fixture.OrchestratorExecutable = "../outside.exe";
            File.WriteAllText(Path.Combine(candidate, "candidate-integration.json"), new JavaScriptSerializer().Serialize(fixture), new UTF8Encoding(false));
            var rejected = false;
            try { CandidateIntegrationConfig.Load(candidate); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "candidate integration rejects paths that escape the immutable candidate root");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
            assertions++;
            Console.WriteLine("PASS " + message);
        }
    }
}

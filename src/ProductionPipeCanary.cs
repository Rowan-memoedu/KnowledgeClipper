using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal static class ProductionPipeCanary
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1 && !(args.Length == 2 && args[0] == "submit")) { Console.Error.WriteLine("Usage: ProductionPipeCanary [submit] <jobs-root>"); return 2; }
            try
            {
                var submit = args.Length == 2; var jobsRoot = Path.GetFullPath(args[submit ? 1 : 0]);
                var clock = new CanaryClock();
                var client = new NamedPipeCommonIpcClient(NamedPipeCommonIpcClient.DefaultPipeName, 3000);
                var source = new FileDesktopCommonCommandSource(jobsRoot, clock, new SystemCommonIdProvider());
                ICaptureProvider capture = new FakeCaptureProvider(null);
                if (submit)
                {
                    var bytes = new UTF8Encoding(false).GetBytes("production pipe submit canary"); var staged = Path.Combine(Path.GetDirectoryName(jobsRoot), "staged-submit.txt"); File.WriteAllBytes(staged, bytes);
                    var artifact = new CaptureArtifact
                    {
                        artifactRef = "capture.text.production-pipe",
                        relativePath = "inputs/capture/production-pipe.txt",
                        sha256 = Sha256(bytes),
                        bytes = bytes.LongLength,
                        mediaType = "text/plain",
                        encoding = "utf-8",
                        sourcePath = staged
                    };
                    capture = new FakeCaptureProvider(new[] { new CaptureResult { hasContent = true, source = "selection", selectedTextArtifactRef = artifact.artifactRef, contextAvailable = false, artifacts = new List<CaptureArtifact> { artifact } } });
                }
                var controller = new DesktopController(capture, new FakeScreenshotProvider(null), new NamedPipeDesktopOrchestratorPort(client, source), clock, new CanaryIdProvider());
                if (submit)
                {
                    var accepted = controller.Execute(new SemanticActionEnvelope { schemaVersion = AutomationCatalog.ActionSchemaVersion, requestId = "production-submit-canary-" + Guid.NewGuid().ToString("N"), action = AutomationCatalog.ClipCapture });
                    if (!accepted.recentTask.available) throw new InvalidOperationException("Production orchestrator did not accept desktop submit: " + accepted.noticeCode);
                    DesktopSemanticState projected = accepted;
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        Thread.Sleep(100); projected = controller.Execute(new SemanticActionEnvelope { schemaVersion = AutomationCatalog.ActionSchemaVersion, requestId = "production-submit-query-" + Guid.NewGuid().ToString("N"), action = AutomationCatalog.TaskStatusQueryRecent });
                        if (projected.recentTask.displayState == "succeeded" || projected.recentTask.displayState == "failed" || projected.recentTask.displayState == "partial" || projected.recentTask.displayState == "cancelled") break;
                    }
                    Console.WriteLine("PRODUCTION_PIPE_SUBMIT_CANARY accepted=True taskId=" + projected.recentTask.taskId + " finalDisplayState=" + projected.recentTask.displayState);
                    return projected.recentTask.displayState == "succeeded" ? 0 : 4;
                }
                var state = controller.Execute(new SemanticActionEnvelope { schemaVersion = AutomationCatalog.ActionSchemaVersion, requestId = "production-pipe-canary-" + Guid.NewGuid().ToString("N"), action = AutomationCatalog.TaskStatusQueryRecent });
                if (state.noticeCode != "task.recent-queried") throw new InvalidOperationException("Unexpected notice: " + state.noticeCode);
                if (state.recentTask.displayState == "unknown") throw new InvalidOperationException("Production orchestrator query did not return a valid IPC projection.");
                Console.WriteLine("PRODUCTION_PIPE_CANARY PASS available=" + state.recentTask.available + " displayState=" + state.recentTask.displayState);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("PRODUCTION_PIPE_CANARY FAIL " + ex); return 1; }
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class CanaryClock : IClock
        {
            public DateTime UtcNow { get { return DateTime.UtcNow; } }
        }

        private sealed class CanaryIdProvider : IIdProvider
        {
            public string NewCaptureId() { return "capture-" + Guid.NewGuid().ToString("N"); }
        }
    }
}

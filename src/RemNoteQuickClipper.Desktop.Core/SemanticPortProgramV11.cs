using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Desktop.Core
{
    internal static class SemanticPortProgramV11
    {
        private static int Main(string[] args)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length != 1 || !File.Exists(args[0])) return 2;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
                var scenario = json.Deserialize<FakeScenario>(File.ReadAllText(args[0], Encoding.UTF8)) ?? new FakeScenario();
                var orchestrator = new FakeDesktopOrchestratorPort
                {
                    AcceptSubmissions = scenario.orchestrator == null || scenario.orchestrator.acceptSubmissions,
                    AnswerImmediately = scenario.orchestrator == null || scenario.orchestrator.answerImmediately,
                    ReplayState = scenario.orchestrator == null || string.IsNullOrWhiteSpace(scenario.orchestrator.replayState) ? "playing" : scenario.orchestrator.replayState,
                    StopState = scenario.orchestrator == null || string.IsNullOrWhiteSpace(scenario.orchestrator.stopState) ? "stopped" : scenario.orchestrator.stopState,
                    RecentTask = scenario.orchestrator == null || scenario.orchestrator.recentTask == null ? new RecentTaskProjection { available = false, displayState = "none" } : scenario.orchestrator.recentTask
                };
                var clock = new FakeClock(scenario.utcNow == default(DateTime) ? new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc) : scenario.utcNow.ToUniversalTime());
                var port = new SemanticTestPortV11(new DesktopController(new FakeCaptureProvider(scenario.captures ?? new CaptureResult[0]), new FakeScreenshotProvider(scenario.screenshots ?? new ScreenshotResult[0]), orchestrator, clock, new FakeIdProvider()));
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    SemanticPortResponseV11 response;
                    try { response = port.Execute(json.Deserialize<SemanticActionEnvelope>(line)); }
                    catch { response = new SemanticPortResponseV11 { schemaVersion = "desktop-test-port.v1.1", requestId = null, ok = false, errorCode = "test-port.invalid-json-action", state = null }; }
                    Console.WriteLine(json.Serialize(response));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}

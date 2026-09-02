using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Desktop.Core
{
    internal static class SemanticPortProgram
    {
        private static int Main(string[] args)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length != 1 || !File.Exists(args[0]))
            {
                Console.Error.WriteLine("Usage: DesktopSemanticHarness.exe <fake-scenario.json>");
                return 2;
            }
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
                var scenario = json.Deserialize<FakeScenario>(File.ReadAllText(args[0], Encoding.UTF8)) ?? new FakeScenario();
                var capture = new FakeCaptureProvider(scenario.captures ?? new CaptureResult[0]);
                var screenshot = new FakeScreenshotProvider(scenario.screenshots ?? new ScreenshotResult[0]);
                var orchestrator = new FakeDesktopOrchestratorPort
                {
                    AcceptSubmissions = scenario.orchestrator == null || scenario.orchestrator.acceptSubmissions,
                    AnswerImmediately = scenario.orchestrator == null || scenario.orchestrator.answerImmediately,
                    ReplayState = scenario.orchestrator == null || string.IsNullOrWhiteSpace(scenario.orchestrator.replayState) ? "playing" : scenario.orchestrator.replayState,
                    StopState = scenario.orchestrator == null || string.IsNullOrWhiteSpace(scenario.orchestrator.stopState) ? "stopped" : scenario.orchestrator.stopState,
                    RecentTask = scenario.orchestrator == null || scenario.orchestrator.recentTask == null ? new RecentTaskProjection { available = false, taskId = null, displayState = "none" } : scenario.orchestrator.recentTask
                };
                var clock = new FakeClock(scenario.utcNow == default(DateTime) ? new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc) : scenario.utcNow.ToUniversalTime());
                var port = new SemanticTestPort(new DesktopController(capture, screenshot, orchestrator, clock, new FakeIdProvider()));
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    SemanticPortResponse response;
                    try { response = port.Execute(json.Deserialize<SemanticActionEnvelope>(line)); }
                    catch { response = new SemanticPortResponse { schemaVersion = "desktop-test-port.v1", requestId = null, ok = false, errorCode = "test-port.invalid-json-action", state = null }; }
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

    internal sealed class FakeScenario
    {
        public DateTime utcNow { get; set; }
        public CaptureResult[] captures { get; set; }
        public ScreenshotResult[] screenshots { get; set; }
        public FakeOrchestratorScenario orchestrator { get; set; }
    }

    internal sealed class FakeOrchestratorScenario
    {
        public bool acceptSubmissions { get; set; }
        public bool answerImmediately { get; set; }
        public string replayState { get; set; }
        public string stopState { get; set; }
        public RecentTaskProjection recentTask { get; set; }
    }
}

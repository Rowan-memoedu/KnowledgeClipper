using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class ValidationRetryChecks
{
    internal static async Task RunAsync(string repository, string output, bool live)
    {
        Directory.CreateDirectory(output);
        var config = ModeConfigurationLoader.Load(Path.Combine(repository, "modes"));
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        CaptureMaterial Material(string text) => new(text, null, "generated_allowed", null, "Public retry fixture", 0, DateTimeOffset.UtcNow);
        var settings = new LanguageSettings { ApiBaseUrl = "http://fixture.invalid", ContentMaxRetries = 1 };
        foreach (var sample in new[] {
            ("Curriculum", "The curriculum is useful.", "Curriculum design takes time."),
            ("in spite of", "In spite of rain, we left.", "We left in spite of rain.") })
        {
            using var handler = new Fixture([Response(sample.Item2), Response(sample.Item3)]);
            using var http = new HttpClient(handler);
            var result = await new ContentModule(http, () => "fixture-secret").ProcessAsync(Material(sample.Item1), "language-en", config, settings, default);
            var first = handler.Requests[0].GetProperty("messages");
            var retry = handler.Requests[1].GetProperty("messages");
            Check(retry.GetArrayLength() == 4 && first[1].GetProperty("content").GetString() == retry[1].GetProperty("content").GetString(), sample.Item1 + " keeps original input");
            Check(retry[2].GetProperty("content").GetString() == Response(sample.Item2) && retry[3].GetProperty("content").GetString()!.Contains("必须包含原样目标"), sample.Item1 + " receives failed candidate and exact validation error");
            Check(!retry.GetRawText().Contains("fixture-secret") && result.Fields!.GeneratedTargetStart == sample.Item3.IndexOf(sample.Item1, StringComparison.Ordinal), sample.Item1 + " corrected by model with exact program-computed position");
        }
        foreach (var sample in new[] { ("echo", "An echo follows another echo."), ("ana", "banana") })
        {
            using var handler = new Fixture([Response(sample.Item2)]);
            using var http = new HttpClient(handler);
            var result = await new ContentModule(http, () => "fixture-secret").ProcessAsync(Material(sample.Item1), "language-en", config, settings, default);
            Check(handler.Requests.Count == 1 && result.Fields!.GeneratedTargetStart == sample.Item2.IndexOf(sample.Item1, StringComparison.Ordinal), sample.Item1 + " accepts repetition or overlap without retry");
        }
        var legacy = config with { Modes = config.Modes.Select(m => m with { Definition = m.Definition with { ContentFormat = null }, Schema = File.ReadAllText(Path.Combine(repository, "modes", "language.schema.json")) }).ToArray() };
        using (var handler = new Fixture([JsonSerializer.Serialize(new { pronunciation = "/echo/", meaning = "回声", generatedSentence = "echo echo", generatedTargetStart = (int?)null })]))
        using (var http = new HttpClient(handler))
        {
            var result = await new ContentModule(http, () => "fixture-secret").ProcessAsync(Material("echo"), "language-en", legacy, settings, default);
            Check(handler.Requests.Count == 1 && result.Fields!.GeneratedTargetStart == 0, "legacy generated repetition selects first exact target");
        }
        using (var handler = new Fixture([Response("wrong"), Response("wrong")]))
        using (var http = new HttpClient(handler))
        {
            try { await new ContentModule(http, () => "fixture-secret").ProcessAsync(Material("TARGET"), "language-en", config, settings, default); throw new Exception("Expected rejection"); }
            catch (InvalidDataException) { Check(handler.Requests.Count == 2, "unchanged invalid output stops at retry limit"); }
        }
        using (var handler = new Fixture(["not-json", Response("Exact target is here.")]))
        using (var http = new HttpClient(handler))
        {
            await new ContentModule(http, () => "fixture-secret").ProcessAsync(Material("Exact target"), "language-en", config, settings, default);
            Check(handler.Requests[1].GetProperty("messages")[3].GetProperty("content").GetString()!.Contains("可解析的 JSON"), "malformed JSON gets bounded parser correction");
        }
        if (live)
        {
            settings = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
            using var trace = new Trace(Path.Combine(output, "live-trace.ndjson")) { InnerHandler = new HttpClientHandler() };
            using var http = new HttpClient(trace) { Timeout = Timeout.InfiniteTimeSpan };
            var module = new ContentModule(http, () => ContentCredentials.Resolve());
            // Read the original material only; never save or import the production draft.
            var original = WorkflowJson.Read<ClipDraft>(@"D:\AppData\KnowledgeClipper\language-drafts\f775d624af382befd4a9fac6095501c8.json");
            foreach (var item in new[] { ("original", original.Material, original.ModeId!), ("independent", Material("in spite of"), "language-writing-en") })
            {
                var result = await module.ProcessAsync(item.Item2, item.Item3, config, settings, default);
                Check(result.Fields?.Status == "ready" && result.Fields.GeneratedTargetStart == PublishedLanguage.FirstTarget(result.Fields.GeneratedSentence!, item.Item2.SelectedText), item.Item1 + " real API exact generation");
                PublishedLanguage.Validate(config.Modes.Single(m => m.Definition.Id == item.Item3).Definition, item.Item2, result.Fields!);
                WorkflowJson.Save(Path.Combine(output, item.Item1 + "-result.json"), result);
            }
        }
        WorkflowJson.Save(Path.Combine(output, "checks.json"), new { outcome = "PASS", checks, live, boundary = "No imports or production draft writes" });
    }
    private static string Response(string sentence) => JsonSerializer.Serialize(new { status = "ready", pronunciation = "/test/", generatedSentence = sentence,
        generatedTargetStart = (int?)null, unresolvedReasons = Array.Empty<string>(), explanationEn = "A public fixture.", sentenceTranslationZh = "公开测试句。", lemma = (string?)null, normalizedSentence = (string?)null });
    private sealed class Fixture(string[] responses) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(doc.RootElement.Clone());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = responses[Requests.Count - 1] } } } }) };
        }
    }
    private sealed class Trace(string path) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var response = await base.SendAsync(request, ct);
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, body, response = await response.Content.ReadAsStringAsync(ct) }) + "\n", ct);
            return response;
        }
    }
}

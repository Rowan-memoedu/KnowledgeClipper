using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static class ContentToleranceLiveChecks
{
    internal static async Task RunAsync(string repository, string output)
    {
        Directory.CreateDirectory(output);
        var config = ModeConfigurationLoader.Load(Path.Combine(repository, "modes"));
        var settings = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
        using var trace = new Trace(Path.Combine(output, "raw-responses.ndjson")) { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(trace) { Timeout = Timeout.InfiniteTimeSpan };
        var processor = new ContentModule(http, () => ContentCredentials.Resolve());
        var results = new List<object>();
        foreach (var id in new[] { "17c0bcf6af70f71aa2def17af2efa4c5", "724dce79794cf6e80d91ab344235ae68" })
        {
            var draft = WorkflowJson.Read<ClipDraft>(@"D:\AppData\KnowledgeClipper\language-drafts\" + id + ".json");
            var result = await processor.ProcessAsync(draft.Material, draft.ModeId!, config, settings, CancellationToken.None);
            WorkflowJson.Save(Path.Combine(output, id + "-result.json"), result);
            if (result.Fields?.Status != "ready") throw new InvalidDataException("Historical failure remains unresolved: " + id);
            _ = PublishedLanguage.Render(config.Modes.Single(m => m.Definition.Id == draft.ModeId).Definition, draft.Material, result.Fields);
            results.Add(new { id, outcome = "PASS", boundary = "Original logged input rerun through current prompt, schema and parser" });
            Console.WriteLine("PASS original failure " + id);
        }
        foreach (var mode in new[] { "language-en", "language-writing-en" })
        {
            var material = new CaptureMaterial("resilient", "虽然失败了，仍然 resilient", "source", 10, "Independent tolerance sample", 0, DateTimeOffset.UtcNow);
            material = material with { TargetStart = material.ContextText!.IndexOf(material.SelectedText, StringComparison.Ordinal) };
            var result = await processor.ProcessAsync(material, mode, config, settings, CancellationToken.None);
            WorkflowJson.Save(Path.Combine(output, mode + "-mixed.json"), result);
            if (result.Fields?.Status != "ready") throw new InvalidDataException("Independent mixed fragment rejected");
            var generated = material with { ContextText = null, ContextOrigin = "generated_allowed", TargetStart = null };
            var extra = await processor.ProcessAsync(generated, mode, config, settings, CancellationToken.None);
            WorkflowJson.Save(Path.Combine(output, mode + "-generated.json"), extra);
            if (extra.Fields?.Status != "ready" || extra.Fields.GeneratedSentence == null) throw new InvalidDataException("Independent generated branch rejected");
            results.Add(new { mode, outcome = "PASS", cases = "Mixed fragment plus independent generated context" });
            Console.WriteLine("PASS " + mode + " source and generated branches");
        }
        foreach (var question in new[] { "PDNF（主析取范式）是什么？给出一个逻辑公式示例。", "解释二阶矩阵的行列式，给出矩阵公式和一个数值例子。" })
        {
            var material = new CaptureMaterial(question, null, "generated_allowed", null, "Math format sample", 0, DateTimeOffset.UtcNow);
            var result = await processor.ProcessAsync(material, "question", config, settings, CancellationToken.None, question);
            var answer = result.Basic?.Back ?? throw new InvalidDataException("Missing math answer");
            var pairs = Regex.Matches(answer, @"\\\(.*?\\\)|\\\[.*?\\\]", RegexOptions.Singleline);
            if (pairs.Count == 0 || answer.Contains("$$") || answer.Contains("```") || answer.Contains(@"\\(") || answer.Contains(@"\\["))
                throw new InvalidDataException("Non-Anki math delimiter or double escaping");
            if (Regex.Matches(answer, @"(?<!\\)\\[\(\)\[\]]").Count != pairs.Count * 2)
                throw new InvalidDataException("Unpaired math delimiters");
            foreach (Match pair in pairs)
                if (pair.Value.Contains('\n') || pair.Value.Contains('\r') || pair.Value.Any(c => char.IsControl(c)))
                    throw new InvalidDataException("Control character or physical newline inside math");
            var note = AnkiNoteDraft.From(new() { Original = material, Material = material, Configuration = config, Settings = settings,
                ModeId = "question", Content = result, Input = new(Intent: "question", ExplicitQuestion: question) });
            if (!note.Fields["Back"].Contains(@"\(") && !note.Fields["Back"].Contains(@"\[")) throw new InvalidDataException("Export lost math delimiters");
            var index = results.Count;
            WorkflowJson.Save(Path.Combine(output, "math-" + index + ".json"), new { question, result, note });
            results.Add(new { question, outcome = "PASS", mathExpressions = pairs.Count });
            Console.WriteLine("PASS Anki math delimiters and export: " + question);
        }
        WorkflowJson.Save(Path.Combine(output, "tolerance-live.json"), new { outcome = "PASS", results,
            boundary = "Real content API, original failed inputs, independent samples, production schema/parser and HTML export. No Anki writes; math syntax check is not a screenshot test." });
    }
    private sealed class Trace(string path) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, rawResponse = raw }) + "\n", ct);
            return response;
        }
    }
}

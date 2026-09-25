using System.Net;
using System.Text.Json;
using KnowledgeClipper.Workflow;

var root = args[0];
var output = args[1];
Directory.CreateDirectory(output);
var prompt = File.ReadAllText(Path.Combine(root, "modes", "lookup-meaning.md"));
var promptSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
var evidence = new List<object>();
var failures = new List<string>();
var settings = new LanguageSettings { ContentMaxRetries = 0 };
CaptureMaterial Material(string sentence) => new("lead", sentence, "source", sentence.IndexOf("lead", StringComparison.Ordinal), "Public synthetic test", 0, DateTimeOffset.UtcNow);
var material = Material("They will lead the team.");
async Task Check(string name, string response, bool accept)
{
    var handler = new ReplyHandler(response);
    using var http = new HttpClient(handler);
    bool accepted = false;
    string? error = null;
    try { await new ContentModule(http, () => "isolated-test").LookupMeaningAsync(material, settings, prompt); accepted = true; }
    catch (InvalidDataException ex) { error = ex.Message; }
    var pass = accepted == accept;
    if (!pass) failures.Add(name);
    evidence.Add(new { name, pass, accepted, expectedAccept = accept, error });
}
await Check("valid IPA", "{\"meaning_zh\":\"带领\",\"pronunciation\":\"/liːd/\"}", true);
await Check("trim whitespace", "{\"meaning_zh\":\" 带领 \",\"pronunciation\":\" /liːd/ \"}", true);
await Check("missing field", "{\"meaning_zh\":\"带领\"}", false);
await Check("additional field", "{\"meaning_zh\":\"带领\",\"pronunciation\":\"/liːd/\",\"extra\":1}", false);
await Check("English meaning", "{\"meaning_zh\":\"带领 lead\",\"pronunciation\":\"/liːd/\"}", false);
await Check("multiline meaning", "{\"meaning_zh\":\"带领\\n领导\",\"pronunciation\":\"/liːd/\"}", false);
await Check("Markdown meaning", "{\"meaning_zh\":\"**带领**\",\"pronunciation\":\"/liːd/\"}", false);
await Check("empty meaning", "{\"meaning_zh\":\"\",\"pronunciation\":\"/liːd/\"}", false);
await Check("plain pronunciation", "{\"meaning_zh\":\"带领\",\"pronunciation\":\"lead\"}", false);
await Check("numeric pronunciation", "{\"meaning_zh\":\"带领\",\"pronunciation\":\"/123/\"}", false);
await Check("Chinese pronunciation", "{\"meaning_zh\":\"带领\",\"pronunciation\":\"里德\"}", false);
await Check("wrong type", "{\"meaning_zh\":5,\"pronunciation\":\"/liːd/\"}", false);
await Check("duplicate field", "{\"meaning_zh\":\"带领\",\"meaning_zh\":\"领导\"}", false);
// Semantic limits are reported separately; deterministic validation cannot decide arbitrary dictionary sense.
var limitations = new List<object>();
foreach (var sample in new[] { new { meaning = "他们将带领这个团队。", ipa = "/liːd/" } })
{
    using var http = new HttpClient(new ReplyHandler(JsonSerializer.Serialize(new { meaning_zh = sample.meaning, pronunciation = sample.ipa })));
    bool accepted = false;
    try { await new ContentModule(http, () => "isolated-test").LookupMeaningAsync(material, settings, prompt); accepted = true; } catch (InvalidDataException) { }
    limitations.Add(new { sample, accepted });
}
var live = new List<object>();
if (args.Contains("--live"))
{
    var actual = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json");
    using var http = new HttpClient();
    var content = new ContentModule(http, () => ContentCredentials.Resolve());
    foreach (var test in new[] { new { name = "verb", sentence = "They will lead the team.", ipaPattern = @"^[/\[]l[ˈˌ]?iː?d[/\]]$", sense = "领" }, new { name = "metal", sentence = "The pipe is made of lead.", ipaPattern = @"^[/\[]l[ˈˌ]?[ɛe]d[/\]]$", sense = "铅" } })
    {
        Console.WriteLine("Live lookup starting: " + test.name);
        var result = await content.LookupMeaningAsync(Material(test.sentence), actual, prompt);
        var serialized = JsonSerializer.SerializeToElement(result, WorkflowJson.Options);
        var meaning = serialized.GetProperty("meaningZh").GetString()!;
        var ipa = serialized.GetProperty("pronunciation").GetString()!;
        var pass = meaning.Contains(test.sense) && meaning.Length <= 15 && System.Text.RegularExpressions.Regex.IsMatch(ipa, test.ipaPattern);
        if (!pass) failures.Add("live " + test.name);
        live.Add(new { test.name, test.sentence, result, pass, promptSha256 });
        Console.WriteLine("Live lookup completed: " + test.name + " pass=" + pass);
        WorkflowJson.Save(Path.Combine(output, "live.json"), live);
    }
}
WorkflowJson.Save(Path.Combine(output, "structural.json"), new { evidence, limitations, failures });
Console.WriteLine($"Structural checks: {evidence.Count}; failures: {failures.Count}; live: {live.Count}");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

sealed class ReplyHandler(string reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } } } )) });
}

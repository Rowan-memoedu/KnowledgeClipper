using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static class TemplateRepairChecks
{
    internal static async Task RunAsync(ContentConfiguration configuration, LanguageSettings settings, Action<bool, string> check)
    {
        using var fixture = new Provider(); using var http = new HttpClient(fixture);
        var anki = new AnkiConnection(http, "http://127.0.0.1");
        var prepare = typeof(AnkiConnection).GetMethod("PrepareAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task Prepare(AnkiNoteDraft note) => (Task)prepare.Invoke(anki, [note, CancellationToken.None])!;
        foreach (var item in configuration.Modes.Where(m => PublishedLanguage.Applies(m.Definition)))
        {
            var mode = item.Definition;
            var en = mode.Language == "en";
            var material = new CaptureMaterial(en ? "lead" : "重", en ? "The pipe contains lead." : "这箱子很重。", "source", en ? 18 : 4, "fixture", 0, DateTimeOffset.UtcNow);
            var fields = PublishedLanguage.Parse(PublishedLanguageChecks.Ready(mode).ToJsonString(), mode, material);
            var generated = AnkiNoteDraft.From(new() { Original = material, Material = material, Configuration = configuration, Settings = settings,
                ModeId = mode.Id, Practice = mode.Practice!.Value, Content = new(mode.Id, fields) });
            // This fixture deliberately exercises migration of the pre-dictionary frozen type.
            var note = generated with { Model = PublishedLanguage.ModelName(settings.AnkiModel),
                Fields = generated.Fields.Where(pair => pair.Key != "DictionaryDetails").ToDictionary(pair => pair.Key, pair => pair.Value) };
            await Prepare(note);
            const string speech = "\n<!-- sftts-controls:start --><div>speech controls</div><!-- sftts-controls:end -->";
            foreach (var name in fixture.Templates.Keys.ToArray())
            {
                fixture.Templates[name]["Front"] = fixture.Templates[name]["Front"].Replace(speech, "") + speech;
                fixture.Templates[name]["Back"] = fixture.Templates[name]["Back"].Replace("{{FrontSide}}<hr id='answer'>", "").Replace("<hr>", "").Replace(speech, "") + speech;
            }
            var mutations = fixture.Updates;
            await Prepare(note);
            check(fixture.Updates == mutations + 1, mode.Id + " repairs exact old templates once");
            await Prepare(note);
            check(fixture.Updates == mutations + 1, mode.Id + " current template is idempotent");
            check(fixture.Templates.Values.All(t => t["Front"].EndsWith(speech) && t["Back"].EndsWith(speech)), mode.Id + " preserves installed speech control blocks");
            var template = fixture.Templates[mode.Practice == PracticeChoice.Reading ? "理解与朗读" : "书写练习"];
            var front = Render(template["Front"], note.Fields);
            var back = Render(template["Back"].Replace("{{FrontSide}}", front), note.Fields);
            check(Regex.Matches(back, "<hr>").Count == (mode.Practice == PracticeChoice.Reading ? 1 : 0),
                mode.Id + " divider appears once after pronunciation and never on writing cards");
            if (mode.Practice == PracticeChoice.Reading)
            {
                check(!note.Fields["Pronunciation"].Contains("<hr") && back.Contains("<div>" + note.Fields["Pronunciation"] + "</div><hr><div>"),
                    mode.Id + " shared template separates pronunciation from explanation for old and new notes without field edits");
            }
            var divider = back.IndexOf("<hr id='answer'>", StringComparison.Ordinal);
            var faces = note.PublishedFaces(note.Ordinals.Single());
            check(divider > 0 && back[..divider] == front && Text(front) == faces.Front && Text(back[(divider + 16)..]) == faces.Back,
                mode.Id + " rendered answer retains front above exact answer rows");
            var original = template["Front"];
            template["Front"] = original + "<div>custom</div>";
            var rejected = false;
            try { await Prepare(note); } catch (InvalidDataException) { rejected = true; }
            check(rejected && template["Front"].EndsWith("<div>custom</div>") && fixture.Updates == mutations + 1, mode.Id + " preserves customized templates");
            template["Front"] = original;
        }
    }
    private static string Render(string html, Dictionary<string, string> fields)
    {
        for (var i = 0; i < 5; i++) html = Regex.Replace(html, @"\{\{([#^])(\w+)\}\}((?:(?!\{\{[#^]).)*?)\{\{/\2\}\}", m =>
            (fields.GetValueOrDefault(m.Groups[2].Value, "").Length > 0) == (m.Groups[1].Value == "#") ? m.Groups[3].Value : "", RegexOptions.Singleline);
        return Regex.Replace(html, @"\{\{(\w+)\}\}", m => fields.GetValueOrDefault(m.Groups[1].Value, ""));
    }
    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"\s*<!-- sftts-controls:start -->.*?<!-- sftts-controls:end -->", "", RegexOptions.Singleline).Replace("</div>", "\n"), "<[^>]*>", "")).TrimEnd('\n');
    private sealed class Provider : HttpMessageHandler
    {
        internal Dictionary<string, Dictionary<string, string>> Templates = new();
        internal int Updates; private string? model; private string[] fields = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var p = doc.RootElement.GetProperty("params"); object? result;
            switch (doc.RootElement.GetProperty("action").GetString())
            {
                case "modelNames": result = model == null ? Array.Empty<string>() : new[] { model }; break;
                case "createModel":
                    model = p.GetProperty("modelName").GetString();
                    fields = p.GetProperty("inOrderFields").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    Templates = p.GetProperty("cardTemplates").EnumerateArray().ToDictionary(x => x.GetProperty("Name").GetString()!, x => new Dictionary<string, string> { ["Front"] = x.GetProperty("Front").GetString()!, ["Back"] = x.GetProperty("Back").GetString()! });
                    result = null; break;
                case "modelFieldNames": result = fields; break;
                case "modelTemplates": result = Templates; break;
                case "updateModelTemplates":
                    Templates = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(p.GetProperty("model").GetProperty("templates").GetRawText())!;
                    Updates++; result = null; break;
                case "deckNames": result = new[] { "Published fixture" }; break;
                case "createDeck": result = 1; break;
                default: throw new Exception("Unexpected Anki action");
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { result, error = (string?)null }) };
        }
    }
}

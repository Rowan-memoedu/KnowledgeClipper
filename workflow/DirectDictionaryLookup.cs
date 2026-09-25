using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

/// <summary>Public dictionary facts; never uses a content prompt or model.</summary>
public sealed class DirectDictionaryLookup(HttpClient http)
{
    public async Task<DirectDictionaryDetails> LookupAsync(string target, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target) || language is not ("zh" or "en"))
            throw new InvalidDataException("划词词典需要非空词块和明确语种。");
        target = target.Trim();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var definitionTask = DefineAsync(target, language, timeout.Token);
        var etymologyTask = EtymologyAsync(target, language, timeout.Token);
        string? etymology = null;
        DictionaryEntry[] entries = [];
        var errors = new List<string>();
        try { entries = await definitionTask; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { errors.Add("词典接口不可用或无该词条"); }
        try { etymology = await etymologyTask; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { errors.Add("词源暂不可用"); }
        var first = entries.SelectMany(e => e.Senses.Select(s => (entry: e, sense: s))).FirstOrDefault();
        return new(target, language, null, first.entry?.PartOfSpeech, first.sense?.Definition,
            entries.SelectMany(e => e.Senses).SelectMany(s => s.Examples).FirstOrDefault(),
            entries.Length != 0 ? "ready" : "unavailable",
            errors.Count == 0 ? null : string.Join("；", errors)) { Entries = entries, Etymology = etymology };
    }

    private async Task<DictionaryEntry[]> DefineAsync(string target, string language, CancellationToken ct)
    {
        var url = "https://en.wiktionary.org/api/rest_v1/page/definition/" + Uri.EscapeDataString(target);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("KnowledgeClipper/1.0 (personal learning dictionary)");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty(language, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<DictionaryEntry>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("definitions", out var definitions) || definitions.ValueKind != JsonValueKind.Array) continue;
            var pos = entry.TryGetProperty("partOfSpeech", out var part) ? Clean(part.GetString()) : "";
            var senses = new List<DictionarySense>();
            foreach (var item in definitions.EnumerateArray())
            {
                var meaning = item.TryGetProperty("definition", out var value) ? Clean(value.GetString()) : "";
                if (meaning.Length == 0) continue;
                string[] examples = [];
                if (item.TryGetProperty("examples", out var exampleItems) && exampleItems.ValueKind == JsonValueKind.Array)
                    examples = exampleItems.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => Clean(e.GetString())).Where(e => e.Length > 0).ToArray();
                senses.Add(new(meaning, examples));
            }
            if (senses.Count != 0) result.Add(new(pos.Length == 0 ? "未标注" : pos, senses.ToArray()));
        }
        return result.ToArray();
    }

    private async Task<string?> EtymologyAsync(string target, string language, CancellationToken ct)
    {
        var page = Uri.EscapeDataString(target);
        using var sections = await GetWiktionaryJsonAsync("https://en.wiktionary.org/w/api.php?action=parse&page=" + page + "&prop=sections&format=json", ct);
        if (!sections.RootElement.TryGetProperty("parse", out var parsed) ||
            !parsed.TryGetProperty("sections", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var inLanguage = false;
        string? index = null;
        foreach (var section in list.EnumerateArray())
        {
            var level = section.GetProperty("level").GetString();
            var title = section.GetProperty("line").GetString() ?? "";
            if (level == "2") { inLanguage = title == (language == "en" ? "English" : "Chinese"); continue; }
            if (inLanguage && title.StartsWith("Etymology", StringComparison.OrdinalIgnoreCase))
            {
                index = section.GetProperty("index").GetString();
                break;
            }
        }
        if (index == null) return null;
        using var text = await GetWiktionaryJsonAsync("https://en.wiktionary.org/w/api.php?action=parse&page=" + page +
            "&prop=text&section=" + Uri.EscapeDataString(index) + "&format=json", ct);
        if (!text.RootElement.TryGetProperty("parse", out var content) ||
            !content.TryGetProperty("text", out var html) || !html.TryGetProperty("*", out var value)) return null;
        var match = Regex.Match(value.GetString() ?? "", "<p(?:\\s[^>]*)?>(.*?)</p>", RegexOptions.Singleline);
        var etymology = match.Success ? Clean(match.Groups[1].Value) : "";
        return etymology.Length == 0 ? null : etymology;
    }

    private async Task<JsonDocument> GetWiktionaryJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("KnowledgeClipper/1.0 (personal learning dictionary)");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return JsonDocument.Parse("{}");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}

/// <summary>Finds an attributed, already published sentence for a context-free card.</summary>
public sealed class AuthenticExampleLookup(HttpClient http)
{
    public async Task<(string Sentence, string Source)?> FindWithFallbackAsync(string target, string language,
        DirectDictionaryDetails? dictionary, CancellationToken ct = default)
    {
        try
        {
            var corpus = await FindAsync(target, language, ct);
            if (corpus != null) return corpus;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        var example = dictionary?.Entries.SelectMany(entry => entry.Senses).SelectMany(sense => sense.Examples)
            .FirstOrDefault(sentence => Usable(sentence, target));
        return example == null ? null : (example, "Wiktionary · https://en.wiktionary.org/wiki/" + Uri.EscapeDataString(target));
    }

    public static CaptureMaterial Apply(CaptureMaterial material, (string Sentence, string Source) example)
    {
        if (material.ContextText != null) throw new InvalidDataException("已有上下文时不得替换主例句。");
        var start = example.Sentence.IndexOf(material.SelectedText, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("真实例句没有原样目标。");
        var result = material with { ContextText = example.Sentence, ContextOrigin = "source", TargetStart = start,
            SourceTitle = example.Source + " | 原选区来源：" + material.SourceTitle, LookupContextText = null };
        result.Validate();
        return result;
    }

    public async Task<(string Sentence, string Source)?> FindAsync(string target, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target) || language is not ("zh" or "en"))
            throw new InvalidDataException("真实例句需要非空词块和明确语种。");
        target = target.Trim();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var url = "https://api.tatoeba.org/v1/sentences?lang=" + (language == "en" ? "eng" : "cmn") +
            "&q=" + Uri.EscapeDataString(target) + "&sort=relevance&limit=20&is_unapproved=no&is_orphan=no";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("KnowledgeClipper/1.0 (personal learning examples)");
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!json.RootElement.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) ||
                !item.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) continue;
            var sentence = value.GetString()?.Trim();
            if (!Usable(sentence, target)) continue;
            return (sentence!, "Tatoeba #" + number + " · CC BY 2.0 FR · https://tatoeba.org/en/sentences/show/" + number);
        }
        return null;
    }

    private static bool Usable(string? sentence, string target) => sentence != null &&
        sentence.Length <= 240 && sentence.Length >= target.Length + 8 && sentence.IndexOfAny(['\r', '\n']) < 0 &&
        (sentence.EndsWith('.') || sentence.EndsWith('!') || sentence.EndsWith('?') ||
         sentence.EndsWith('。') || sentence.EndsWith('！') || sentence.EndsWith('？')) &&
        sentence.Contains(target, StringComparison.Ordinal);
}

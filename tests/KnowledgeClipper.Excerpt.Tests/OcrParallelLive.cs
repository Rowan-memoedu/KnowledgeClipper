using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static class OcrParallelLive
{
    private sealed record PublicFixture(string Path, string Text, string Sha256);
    private sealed record Manifest(string JobId, string ProviderPath, string ProviderSha256, PublicFixture[] Fixtures, string ObserverSha256);
    private sealed record ProviderInterval(string Image, string Stage, long Start, long End, bool Succeeded);
    private sealed record CallEvent(string Stage, string Image, DateTimeOffset At, string? Message = null);

    internal static async Task RunAsync(string project, string runRoot, string receipt, string providerPath)
    {
        if (!Path.GetFullPath(runRoot).StartsWith(@"D:\Caches\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase) || File.Exists(receipt))
            throw new InvalidOperationException("Use a fixed isolated cache and a new append-only receipt path.");
        var settingsPath = @"D:\AppData\KnowledgeClipper\language-settings.json";
        var settingsHash = Hash(settingsPath);
        var settings = WorkflowJson.Read<LanguageSettings>(settingsPath);
        var configuredProvider = settings.MineruScriptPath;
        if (string.IsNullOrWhiteSpace(configuredProvider))
        {
            using var app = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "appsettings.json")));
            configuredProvider = app.RootElement.GetProperty("MineruScriptPath").GetString()!;
        }
        if (!providerPath.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase) || Hash(providerPath) != Hash(configuredProvider))
            throw new InvalidDataException("The D-drive provider must match the currently configured runner byte for byte.");
        var observerPath = Path.Combine(project, "tests", "KnowledgeClipper.Excerpt.Tests", "fixtures", "observe_mineru.py");
        var store = new ImageMaterialStore(runRoot);
        var manifestPath = Path.Combine(runRoot, "manifest.json");
        Manifest manifest;
        if (File.Exists(manifestPath)) manifest = WorkflowJson.Read<Manifest>(manifestPath);
        else
        {
            var fixtures = WorkflowJson.Read<PublicFixture[]>(Path.Combine(runRoot, "inputs", "public-fixtures.json"));
            if (fixtures.Length != 3 || fixtures.Any(f => Hash(f.Path) != f.Sha256)) throw new InvalidDataException("Expected three frozen public PNG fixtures.");
            var job = store.Create(fixtures.Select(f => (f.Path, "question")), MaterialKind.OcrImages,
                settings with { MineruScriptPath = observerPath }, Path.Combine(project, "modes"), @"D:\AppData\KnowledgeClipper\modes");
            manifest = new(job.Id, providerPath, Hash(providerPath), fixtures, Hash(observerPath));
            WorkflowJson.Save(manifestPath, manifest);
        }
        if (manifest.ProviderPath != providerPath || manifest.ProviderSha256 != Hash(providerPath) || manifest.ObserverSha256 != Hash(observerPath) ||
            manifest.Fixtures.Any(f => Hash(f.Path) != f.Sha256)) throw new InvalidDataException("Frozen live inputs or observation provider changed.");
        var material = store.Find(manifest.JobId);
        if (material.Images.Length != 3 || !material.Images.Select(i => i.Sha256).SequenceEqual(manifest.Fixtures.Select(f => f.Sha256)))
            throw new InvalidDataException("The original job no longer matches its public fixture manifest.");
        var checks = new List<string>();
        var events = new ConcurrentQueue<CallEvent>();
        var priorEnvironment = Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_OBSERVED_MINERU");
        var started = DateTimeOffset.UtcNow;
        Exception? failure = null;
        string? text = null;
        var apiCalls = new ConcurrentQueue<CallEvent>();
        using var http = new HttpClient(new ObservedContent(apiCalls)) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ocr = new ObservedOcr(new MineruCli(Path.Combine(project, "helpers", "mineru_bridge.py")), events);
        var preparation = new ImagePreparation(store, ocr, new ContentModule(http, () => ContentCredentials.Resolve()));
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidDataException(label);
            checks.Add(label); Console.WriteLine("PASS " + label);
        }
        try
        {
            Environment.SetEnvironmentVariable("KNOWLEDGECLIPPER_OBSERVED_MINERU", providerPath);
            text = await preparation.TextAsync(material, message => Console.WriteLine(DateTimeOffset.UtcNow.ToString("O") + " " + message), timeout.Token);
            var saved = store.Find(manifest.JobId);
            Check(saved.Ocr.All(i => i.Review?.Verdict == "pass" && i.Error == null && i.RawText != null && i.Cleanup != null), "all three real OCR, cleanup and review pipelines completed");
            Check(saved.Ocr.Select(i => Tokens(i.RawText!)).SequenceEqual(manifest.Fixtures.Select(f => Tokens(f.Text))), "each raw OCR contains exactly the ordered public source words and numbers");
            Check(saved.Ocr.Select(i => Tokens(i.Cleanup!.CleanedText)).SequenceEqual(manifest.Fixtures.Select(f => Tokens(f.Text))) &&
                Tokens(text) == Tokens(string.Join("\n\n", manifest.Fixtures.Select(f => f.Text))), "cleaned text and combined output retain original page order without added or lost words");
            var intervals = ReadIntervals(store.DirectoryFor(manifest.JobId));
            Check(intervals.Count(i => i.Stage == "upload") == 3 && intervals.Where(i => i.Stage == "upload").All(i => i.Succeeded), "exactly three original uploads succeeded with no replacement batch submission");
            Check(MaximumOverlap(intervals.Where(i => i.Stage == "upload")) >= 2, "actual original-runner upload time intervals overlap");
            Check(MaximumOverlap(intervals.Where(i => i.Stage == "parse")) >= 2, "actual original-runner parse polling intervals overlap");
            Check(saved.DestinationId == null && saved.DestinationKind == null, "the live Canary produces no destination or import");
            var countOcr = events.Count; var countContent = apiCalls.Count;
            var attempts = Directory.GetFiles(store.DirectoryFor(manifest.JobId), "*.json", SearchOption.AllDirectories).Length;
            Check(await preparation.TextAsync(store.Find(manifest.JobId), _ => { }, timeout.Token) == text && events.Count == countOcr && apiCalls.Count == countContent &&
                Directory.GetFiles(store.DirectoryFor(manifest.JobId), "*.json", SearchOption.AllDirectories).Length == attempts,
                "reopening the same real job repeats zero OCR, model calls or attempt artifacts");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Environment.SetEnvironmentVariable("KNOWLEDGECLIPPER_OBSERVED_MINERU", priorEnvironment);
            if (Hash(settingsPath) != settingsHash) failure ??= new InvalidDataException("Production settings changed during Canary.");
            var saved = store.Find(manifest.JobId);
            var intervals = ReadIntervals(store.DirectoryFor(manifest.JobId));
            WorkflowJson.Save(receipt, new
            {
                passed = failure == null, started, finished = DateTimeOffset.UtcNow, checks, manifestPath, manifest.JobId, runRoot,
                providerPath, manifest.ProviderSha256, observerPath, manifest.ObserverSha256,
                workflowAssembly = typeof(ImagePreparation).Assembly.Location, workflowSha256 = Hash(typeof(ImagePreparation).Assembly.Location),
                settingsUnchanged = Hash(settingsPath) == settingsHash, model = saved.Settings.Model, apiHost = new Uri(saved.Settings.ApiBaseUrl).Host,
                maximumUploadOverlap = MaximumOverlap(intervals.Where(i => i.Stage == "upload")), maximumParseOverlap = MaximumOverlap(intervals.Where(i => i.Stage == "parse")),
                intervals, events = events.ToArray(), apiCalls = apiCalls.ToArray(), images = saved.Ocr, text,
                error = failure?.ToString(), scope = "three frozen public PNGs through real direct MinerU and configured content API; no imports; timestamp-only observer delegates unchanged runner functions"
            });
        }
        if (failure != null) throw new InvalidOperationException("Live OCR Canary did not pass; preserve the manifest and inspect " + receipt, failure);
    }
    private static string Hash(string path) => ImageAsset.Hash(File.ReadAllBytes(path));
    private static string Tokens(string text) => string.Join(" ", Regex.Matches(text, "[A-Za-z0-9]+", RegexOptions.CultureInvariant).Select(m => m.Value.ToLowerInvariant()));
    private static ProviderInterval[] ReadIntervals(string directory)
    {
        var intervals = new List<ProviderInterval>();
        foreach (var file in Directory.GetFiles(directory, "timeline.jsonl", SearchOption.AllDirectories))
        {
            var image = Directory.GetParent(Path.GetDirectoryName(file)!)!.Name;
            var starts = new Dictionary<string, long>();
            foreach (var line in File.ReadLines(file))
            {
                using var document = JsonDocument.Parse(line); var record = document.RootElement;
                var stage = record.GetProperty("stage").GetString()!; var time = record.GetProperty("unixNanoseconds").GetInt64();
                if (stage.EndsWith("-start", StringComparison.Ordinal)) starts[stage[..^6]] = time;
                else if (stage.EndsWith("-end", StringComparison.Ordinal) && starts.Remove(stage[..^4], out var start))
                    intervals.Add(new(image, stage[..^4], start, time, record.GetProperty("succeeded").GetBoolean()));
            }
        }
        return intervals.ToArray();
    }
    private static int MaximumOverlap(IEnumerable<ProviderInterval> intervals)
    {
        var active = 0; var maximum = 0;
        foreach (var item in intervals.SelectMany(i => new[] { (At: i.Start, Delta: 1), (At: i.End, Delta: -1) }).OrderBy(i => i.At).ThenBy(i => i.Delta))
            maximum = Math.Max(maximum, active += item.Delta);
        return maximum;
    }
    private sealed class ObservedOcr(IImageOcrExtractor inner, ConcurrentQueue<CallEvent> events) : IImageOcrExtractor
    {
        public async Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress, CancellationToken ct, string? resumeManifest = null)
        {
            var key = image.Role + "." + image.Index; events.Enqueue(new("ocr-start", key, DateTimeOffset.UtcNow));
            try { return await inner.ExtractAsync(image, settings, output, message => { events.Enqueue(new("ocr-progress", key, DateTimeOffset.UtcNow, message)); progress(message); }, ct, resumeManifest); }
            finally { events.Enqueue(new("ocr-end", key, DateTimeOffset.UtcNow)); }
        }
    }
    private sealed class ObservedContent(ConcurrentQueue<CallEvent> calls) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var stage = input.RootElement.TryGetProperty("candidate", out _) ? "review" : "cleanup";
            var id = input.RootElement.GetProperty("source_id").GetString()!;
            calls.Enqueue(new(stage + "-start", id, DateTimeOffset.UtcNow));
            try { return await base.SendAsync(request, ct); }
            finally { calls.Enqueue(new(stage + "-end", id, DateTimeOffset.UtcNow)); }
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class OcrParallelChecks
{
    internal static async Task RunAsync(string project, string receipt)
    {
        var checks = new List<string>();
        var evidence = new List<object>();
        var data = Path.Combine(@"D:\Caches\KnowledgeClipper\ocr-parallel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var source = Path.Combine(data, "public-pixel.png");
        File.WriteAllBytes(source, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
        var store = new ImageMaterialStore(data);
        var settings = new LanguageSettings { ContentMaxRetries = 0, ContentTimeoutSeconds = 30, ApiBaseUrl = "https://offline.invalid" };
        var legacyModes = Path.Combine(data, "legacy-modes"); Directory.CreateDirectory(legacyModes);
        File.WriteAllText(Path.Combine(legacyModes, "content-review.schema.json"), File.ReadAllText(Path.Combine(project, "modes", "content-review.schema.json"))
            .Replace("\"enum\": [\"pass\"]", "\"const\": \"pass\"").Replace("\"enum\": [\"fail\"]", "\"const\": \"fail\""));
        ImageMaterialJob Job(int questions, int answers = 0, bool legacy = false) => store.Create(
            Enumerable.Repeat((source, "question"), questions).Concat(Enumerable.Repeat((source, "answer"), answers)),
            MaterialKind.OcrImages, settings, Path.Combine(project, "modes"), legacy ? legacyModes : Path.Combine(data, "modes"));
        void Check(bool pass, string message)
        {
            if (!pass) throw new Exception(message);
            checks.Add(message); Console.WriteLine("PASS " + message);
        }
        static string Expected(ImageMaterialJob job) => string.Join("\n\n", job.Images.Select(Key));
        static bool Complete(ImageMaterialJob job) => job.Ocr.All(i => i.Error == null && i.Review?.Verdict == "pass" &&
            i.RawText == Key(i.Image) && i.Cleanup?.CleanedText == Key(i.Image) && i.RawSha256 == ImageAsset.Hash(Encoding.UTF8.GetBytes(i.RawText)));
        async Task ExpectFailure(Task<string> task, bool canceled, string label)
        {
            Exception? error = null;
            try { await task.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception ex) { error = ex; }
            evidence.Add(new { scenario = "expected-failure", label, error = error?.ToString() });
            Check(error != null && error is not TimeoutException && (error is OperationCanceledException) == canceled, label);
        }
        try
        {
            foreach (var legacy in new[] { false, true })
            foreach (var (verdict, hasErrors, valid) in new[] { ("pass", false, true), ("fail", true, true), ("pass", true, false), ("fail", false, false), ("unexpected", false, false) })
            {
                var job = Job(1, legacy: legacy);
                var savedPath = Path.Combine(store.DirectoryFor(job.Id), "job.json");
                var frozenBytes = File.ReadAllBytes(savedPath);
                var frozenSchema = job.ReviewMode.Schema;
                var response = new { verdict, errors = hasErrors ? new[] { new { kind = "fixture", evidence = "Public fixture error" } } : [], warnings = Array.Empty<object>() };
                using var provider = new ContentFixture((_, _, _, _) => Task.FromResult<object?>(response)); using var http = new HttpClient(provider);
                var module = new ContentModule(http, () => "offline");
                async Task<string> Review() => (await module.ReviewImageContentAsync(job, "ocr", new { cleaned_text = "Public fixture", notes = Array.Empty<object>() },
                    "Public fixture", job.CleanupMode.Schema, default)).Verdict;
                var label = (legacy ? "legacy const" : "current enum") + " review contract " + verdict + (hasErrors ? " with errors" : " without errors");
                if (valid) Check(await Review() == verdict, label + " is accepted");
                else await ExpectFailure(Review(), false, label + " is rejected");
                Check(job.ReviewMode.Schema == frozenSchema && File.ReadAllBytes(savedPath).SequenceEqual(frozenBytes) &&
                    provider.SystemPrompts.Single() == job.ReviewMode.Prompt + "\nJSON 字段约定：\n" + frozenSchema,
                    label + " leaves the frozen job, schema, and outgoing prompt unchanged");
            }
            // A UI-like, single-threaded synchronization context proves that worker
            // progress reports marshal back without opening any native window.
            await SingleThreadContext.Run(async () =>
            {
                var ownerThread = Environment.CurrentManagedThreadId;
                var progressThreads = new List<int>();
                var job = Job(4, 2);
                using var provider = new ContentFixture(); using var http = new HttpClient(provider);
                var ocr = new ControlledOcr();
                var processing = new ImagePreparation(store, ocr, new ContentModule(http, () => "offline"));
                var task = processing.TextAsync(job, _ => progressThreads.Add(Environment.CurrentManagedThreadId), default);
                await ocr.WaitStarted("question.1", "question.2", "question.3");
                Check(ocr.Active == 3 && ocr.Calls.Count == 3, "three OCR uploads overlap before any result is released");
                ocr.Release("question.3"); await ocr.WaitStarted("question.4");
                ocr.Release("question.4"); await ocr.WaitStarted("answer.1");
                ocr.Release("answer.1"); await ocr.WaitStarted("answer.2");
                ocr.Release("answer.2"); ocr.Release("question.2"); ocr.Release("question.1");
                var text = await task.WaitAsync(TimeSpan.FromSeconds(15));
                Check(ocr.Maximum == 3 && ocr.Active == 0 && ocr.Calls.Count == 6, "OCR concurrency stays bounded at three and drains all started work");
                Check(text == Expected(job) && Complete(store.Find(job.Id)), "out-of-order completions persist in original question and answer order");
                Check(progressThreads.Count > 6 && progressThreads.All(t => t == ownerThread) && ocr.ForeignProgressThreads.All(t => t != ownerThread),
                    "foreign-thread extractor progress reaches only the original UI context");
                var requests = provider.Calls.Count;
                var files = Directory.GetFiles(store.DirectoryFor(job.Id), "*.json", SearchOption.AllDirectories).Length;
                var repeated = await processing.TextAsync(store.Find(job.Id), _ => { }, default);
                Check(repeated == text && ocr.Calls.Count == 6 && provider.Calls.Count == requests &&
                    Directory.GetFiles(store.DirectoryFor(job.Id), "*.json", SearchOption.AllDirectories).Length == files,
                    "reopening a completed job makes no new OCR, cleanup, review, or attempt artifacts");
                evidence.Add(new { scenario = "ordered-parallel-ui-context", job.Id, ocr.Maximum, calls = ocr.Calls.ToArray(), progressCount = progressThreads.Count });
            });

            // No SynchronizationContext: continuations really run on pool threads.
            // This also exercises concurrent mutation plus WorkflowJson's fixed .tmp.
            {
                var job = Job(35, 15);
                using var provider = new ContentFixture(); using var http = new HttpClient(provider);
                var ocr = new ControlledOcr(autoRelease: true);
                var processing = new ImagePreparation(store, ocr, new ContentModule(http, () => "offline"));
                Check(await processing.TextAsync(job, _ => { }, default) == Expected(job) && Complete(store.Find(job.Id)),
                    "fifty images save every raw, cleanup, and review without a shared-job race");
                Check(ocr.Maximum <= 3 && provider.Calls.Count == 100 &&
                    !Directory.GetFiles(store.DirectoryFor(job.Id), "*.tmp", SearchOption.AllDirectories).Any(),
                    "thread-pool processing keeps the cap and leaves no incomplete save file");
                evidence.Add(new { scenario = "thread-pool-persistence", job.Id, imageCount = 50, ocr.Maximum, contentRequests = provider.Calls.Count });
            }

            {
                var job = Job(4);
                using var provider = new ContentFixture(); using var http = new HttpClient(provider);
                var ocr = new ControlledOcr();
                var processing = new ImagePreparation(store, ocr, new ContentModule(http, () => "offline"));
                var task = processing.TextAsync(job, _ => { }, default);
                await ocr.WaitStarted("question.1", "question.2", "question.3");
                ocr.Fail("question.2", new OcrProviderException("Offline download failure", "saved-question-2-batch"));
                await Until(() => job.Ocr[1].Error != null);
                Check(!task.IsCompleted, "one failed image waits for other in-flight images before returning");
                ocr.Release("question.3"); ocr.Release("question.1");
                await ExpectFailure(task, false, "partial failure cannot publish downstream text");
                var saved = store.Find(job.Id);
                Check(saved.Ocr[0].Review?.Verdict == "pass" && saved.Ocr[2].Review?.Verdict == "pass" &&
                    saved.Ocr[1].Error != null && saved.Ocr[1].RawManifest == "saved-question-2-batch" && saved.Ocr[3].RawText == null && ocr.Active == 0,
                    "failure preserves completed peers and the failed batch while leaving queued images untouched");
                var retry = new ControlledOcr(autoRelease: true);
                var resumed = new ImagePreparation(store, retry, new ContentModule(http, () => "offline"));
                Check(await resumed.TextAsync(saved, _ => { }, default) == Expected(job) && Complete(store.Find(job.Id)), "the same failed job resumes to the complete ordered text");
                Check(retry.Calls.Select(c => c.Image).Order().SequenceEqual(new[] { "question.2", "question.4" }) &&
                    retry.Calls.Single(c => c.Image == "question.2").Resume == "saved-question-2-batch" && provider.Count("cleanup", "question.1") == 1 && provider.Count("cleanup", "question.3") == 1,
                    "retry reuses successful peers and passes the original completed batch identity to MinerU");
                evidence.Add(new { scenario = "failure-resume", job.Id, initial = ocr.Calls.ToArray(), retry = retry.Calls.ToArray() });
            }

            {
                var job = Job(5);
                using var provider = new ContentFixture(async (stage, text, count, ct) =>
                {
                    if (stage == "review" && text == "question.2" && count == 1) await Task.Delay(Timeout.Infinite, ct);
                    return null;
                });
                using var http = new HttpClient(provider); using var cancellation = new CancellationTokenSource();
                var ocr = new ControlledOcr();
                var processing = new ImagePreparation(store, ocr, new ContentModule(http, () => "offline"));
                var task = processing.TextAsync(job, _ => { }, cancellation.Token);
                await ocr.WaitStarted("question.1", "question.2", "question.3");
                ocr.Release("question.2"); await Until(() => provider.Count("review", "question.2") == 1);
                ocr.Release("question.1"); await ocr.WaitStarted("question.4");
                cancellation.Cancel();
                await ExpectFailure(task, true, "cancellation propagates only after started image operations stop");
                var saved = store.Find(job.Id);
                Check(saved.Ocr[0].Review?.Verdict == "pass" && saved.Ocr[1].Cleanup != null && saved.Ocr[1].Review == null &&
                    saved.Ocr[2].Error != null && saved.Ocr[3].Error != null && saved.Ocr[4].RawText == null && ocr.Active == 0,
                    "cancel saves completed images and cleanup while queued images remain unstarted");
                var retry = new ControlledOcr(autoRelease: true);
                var resumed = new ImagePreparation(store, retry, new ContentModule(http, () => "offline"));
                Check(await resumed.TextAsync(saved, _ => { }, default) == Expected(job) && Complete(store.Find(job.Id)), "the same canceled job resumes without discarding any completed stage");
                Check(retry.Calls.Select(c => c.Image).Order().SequenceEqual(new[] { "question.3", "question.4", "question.5" }) &&
                    provider.Count("cleanup", "question.2") == 1 && provider.Count("review", "question.2") == 2 && provider.Count("review", "question.1") == 1,
                    "resuming interrupted review does not repeat OCR or its saved successful cleanup");
                evidence.Add(new { scenario = "cancel-resume", job.Id, initial = ocr.Calls.ToArray(), retry = retry.Calls.ToArray(), contentCalls = provider.Calls.ToArray() });
            }

            foreach (var legacy in new[] { false, true })
            {
                var job = Job(1, legacy: legacy);
                using var provider = new ContentFixture((stage, _, count, _) => Task.FromResult<object?>(
                    stage == "review" && count == 1 ? new { verdict = "fail", errors = new[] { new { kind = "fixture", evidence = "Public fixture correction required" } }, warnings = Array.Empty<object>() } : null));
                using var http = new HttpClient(provider);
                var ocr = new ControlledOcr(autoRelease: true);
                var processing = new ImagePreparation(store, ocr, new ContentModule(http, () => "offline"));
                await ExpectFailure(processing.TextAsync(job, _ => { }, default), false, "an explicit failed review still blocks downstream text");
                Check(await processing.TextAsync(store.Find(job.Id), _ => { }, default) == Expected(job) && ocr.Calls.Count == 1 &&
                    provider.Count("cleanup", "question.1") == 2 && provider.Count("review", "question.1") == 2,
                    "review rejection replaces only its cleanup candidate and retains the original OCR");
                var saved = store.Find(job.Id); saved.Ocr[0].RawText += " changed"; store.Save(saved);
                var requests = provider.Calls.Count;
                await ExpectFailure(processing.TextAsync(store.Find(job.Id), _ => { }, default), false, "saved OCR fingerprint drift remains rejected");
                Check(provider.Calls.Count == requests && ocr.Calls.Count == 1, "fingerprint failure never sends modified text or uploads the image again");
                evidence.Add(new { scenario = legacy ? "legacy-review-failure-resume" : "current-review-failure-resume", job.Id, contentCalls = provider.Calls.ToArray() });
            }

            // Real Python processes through the production bridge, with no network.
            {
                var job = Job(3);
                var processRoot = Path.Combine(data, "process-overlap"); Directory.CreateDirectory(processRoot);
                var cli = new MineruCli(Path.Combine(project, "helpers", "mineru_bridge.py"));
                var localSettings = settings with { MineruScriptPath = Path.Combine(project, "tests", "KnowledgeClipper.Excerpt.Tests", "fixtures", "parallel_mineru.py") };
                var names = new[] { "MINERU_API_TOKEN", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" };
                var previous = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
                try
                {
                    Environment.SetEnvironmentVariable("MINERU_API_TOKEN", "offline-parallel-fixture");
                    foreach (var name in names.Skip(1)) Environment.SetEnvironmentVariable(name, "http://127.0.0.1:9");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    var tasks = job.Images.Select(image => cli.ExtractAsync(image, localSettings, Path.Combine(processRoot, Key(image)), _ => { }, timeout.Token)).ToArray();
                    try
                    {
                        await Until(() => Directory.GetFiles(processRoot, "started.json", SearchOption.AllDirectories).Length == 3);
                        var started = Directory.GetFiles(processRoot, "started.json", SearchOption.AllDirectories).Select(WorkflowJson.Read<JsonElement>).ToArray();
                        Check(tasks.All(t => !t.IsCompleted) && started.Select(s => s.GetProperty("pid").GetInt32()).Distinct().Count() == 3 && started.All(s => s.GetProperty("direct").GetBoolean()),
                            "three separate MineruCli processes overlap with proxy variables removed");
                        File.WriteAllText(Path.Combine(processRoot, "release"), "public offline fixture");
                        var extracted = await Task.WhenAll(tasks);
                        var finished = Directory.GetFiles(processRoot, "finished.json", SearchOption.AllDirectories).Select(WorkflowJson.Read<JsonElement>).ToArray();
                        Check(started.Max(s => s.GetProperty("started").GetDouble()) < finished.Min(s => s.GetProperty("finished").GetDouble()) &&
                            extracted.Select(r => r.ManifestPath).Distinct().Count() == 3 && extracted.All(r => r.Text.StartsWith("Public process fixture ", StringComparison.Ordinal)),
                            "overlapping processes independently validate their own manifest, ZIP, extraction and Markdown");
                        evidence.Add(new { scenario = "actual-offline-cli-processes", started, finished, manifests = extracted.Select(r => r.ManifestPath).ToArray() });
                    }
                    finally
                    {
                        timeout.Cancel();
                        try { await Task.WhenAll(tasks); } catch { /* Original failure is retained; every process has been drained. */ }
                    }
                }
                finally { foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]); }
            }
            WorkflowJson.Save(receipt, new { passed = true, checks, evidence, dataRoot = data,
                scope = "offline OCR/content fixtures and real offline Python processes; no user material, remote API, Anki, RemNote or production settings" });
        }
        catch (Exception error)
        {
            WorkflowJson.Save(receipt, new { passed = false, checks, evidence, dataRoot = data, error = error.ToString() });
            throw;
        }
    }

    private static string Key(ImageAsset image) => image.Role + "." + image.Index;
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Offline fixture condition was not reached");
            await Task.Delay(10);
        }
    }
    private sealed record OcrCall(string Image, string? Resume);
    private sealed class ControlledOcr(bool autoRelease = false) : IImageOcrExtractor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> releases = new();
        private int active, maximum;
        internal int Active => Volatile.Read(ref active);
        internal int Maximum => Volatile.Read(ref maximum);
        internal ConcurrentQueue<OcrCall> Calls { get; } = new();
        internal ConcurrentQueue<int> ForeignProgressThreads { get; } = new();
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task WaitStarted(params string[] keys) => Task.WhenAll(keys.Select(key => started.GetOrAdd(key, _ => Signal()).Task)).WaitAsync(TimeSpan.FromSeconds(12));
        internal void Release(string key) => releases.GetOrAdd(key, _ => Signal()).TrySetResult();
        internal void Fail(string key, Exception error) => releases.GetOrAdd(key, _ => Signal()).TrySetException(error);
        public async Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress, CancellationToken ct, string? resumeManifest = null)
        {
            var key = Key(image); Calls.Enqueue(new(key, resumeManifest));
            var count = Interlocked.Increment(ref active);
            int prior;
            do { prior = Volatile.Read(ref maximum); } while (count > prior && Interlocked.CompareExchange(ref maximum, count, prior) != prior);
            started.GetOrAdd(key, _ => Signal()).TrySetResult();
            try
            {
                if (!autoRelease) await releases.GetOrAdd(key, _ => Signal()).Task.WaitAsync(ct).ConfigureAwait(false);
                else await Task.Delay(2, ct).ConfigureAwait(false);
                ForeignProgressThreads.Enqueue(Environment.CurrentManagedThreadId); progress("offline OCR progress");
                return new(key, resumeManifest ?? "offline-" + key);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private sealed record ContentCall(string Stage, string Text, int Count);
    private sealed class ContentFixture(Func<string, string, int, CancellationToken, Task<object?>>? responder = null) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<(string, string), int> counts = new();
        internal ConcurrentQueue<ContentCall> Calls { get; } = new();
        internal ConcurrentQueue<string> SystemPrompts { get; } = new();
        internal int Count(string stage, string text) => counts.GetValueOrDefault((stage, text));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            SystemPrompts.Enqueue(payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
            using var input = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var root = input.RootElement;
            var stage = root.TryGetProperty("candidate", out _) ? "review" : "cleanup";
            var text = root.GetProperty("ocr_text").GetString()!;
            var count = counts.AddOrUpdate((stage, text), 1, (_, value) => value + 1);
            Calls.Enqueue(new(stage, text, count));
            var result = responder == null ? null : await responder(stage, text, count, ct);
            result ??= stage == "review" ? new { verdict = "pass", errors = Array.Empty<object>(), warnings = Array.Empty<object>() } :
                (object)new { cleaned_text = text, notes = Array.Empty<object>() };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(result) } } } }) };
        }
    }
    private sealed class SingleThreadContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        internal static Task Run(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var context = new SingleThreadContext(); SetSynchronizationContext(context);
                context.Post(async _ =>
                {
                    try { await action(); completion.SetResult(); }
                    catch (Exception ex) { completion.SetException(ex); }
                    finally { context.queue.CompleteAdding(); }
                }, null);
                foreach (var work in context.queue.GetConsumingEnumerable()) work.Callback(work.State);
            }) { IsBackground = true, Name = "Offline OCR progress context" };
            thread.Start(); return completion.Task;
        }
    }
}

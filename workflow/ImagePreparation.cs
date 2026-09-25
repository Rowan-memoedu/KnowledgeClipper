using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public sealed record OcrExtraction(string Text, string ManifestPath);
public sealed class OcrProviderException(string message, string? manifest) : IOException(message)
{ public string? ManifestPath { get; } = manifest; }
public interface IImageOcrExtractor
{
    Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress, CancellationToken ct, string? resumeManifest = null);
}

// Reuse the installed MinerU runner, including its direct submission/upload/poll/download chain.
public sealed class MineruCli(string? bridgePath = null) : IImageOcrExtractor
{
    public async Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress, CancellationToken ct, string? resumeManifest = null)
    {
        image.ReadVerified();
        if (!File.Exists(settings.MineruScriptPath)) throw new InvalidDataException("找不到 MinerU 程序，请在图片设置中检查路径。");
        string? EnvironmentValue(string name) => Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        var token = EnvironmentValue("MINERU_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("未配置 MINERU_API_TOKEN 用户环境变量。");
        Directory.CreateDirectory(output);
        var bridge = bridgePath ?? Path.Combine(AppContext.BaseDirectory, "helpers", "mineru_bridge.py");
        if (!File.Exists(bridge)) throw new InvalidDataException("找不到 MinerU 调用桥接程序。");
        var info = new ProcessStartInfo(settings.PythonPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(settings.MineruScriptPath)! };
        foreach (var arg in new[] { bridge, settings.MineruScriptPath, image.Path, "--output-dir", output, "--model-version", "vlm", "--language", settings.MineruLanguage,
            "--timeout", settings.MineruTimeoutSeconds.ToString(), "--poll-interval", "3" }) info.ArgumentList.Add(arg);
        resumeManifest ??= FindResumeManifest(Path.GetDirectoryName(output)!, image.Path);
        if (resumeManifest != null) { info.ArgumentList.Add("--resume-manifest"); info.ArgumentList.Add(resumeManifest); progress("继续下载已完成的识别结果…"); }
        if (!settings.MineruTable) info.ArgumentList.Add("--no-table");
        if (!settings.MineruFormula) info.ArgumentList.Add("--no-formula");
        info.Environment["MINERU_API_TOKEN"] = token.Trim();
        var api = EnvironmentValue("MINERU_API_BASE"); if (!string.IsNullOrWhiteSpace(api)) info.Environment["MINERU_API_BASE"] = api.Trim();
        info.Environment["PYTHONUNBUFFERED"] = "1"; info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var name in info.Environment.Keys.Where(n => n.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) || n.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase) || n.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.Environment["NO_PROXY"] = "*";
        using var process = Process.Start(info) ?? throw new IOException("无法启动 MinerU。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(settings.MineruTimeoutSeconds + 120));
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var exit = process.WaitForExitAsync(timeout.Token);
            while (!process.HasExited)
            {
                if (await Task.WhenAny(exit, Task.Delay(5000, timeout.Token)) == exit) { await exit; break; }
                timeout.Token.ThrowIfCancellationRequested(); progress("MinerU 正在识别并下载结果…");
            }
            await Task.WhenAll(stdout, stderr);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var manifests = Directory.GetFiles(output, "manifest.json", SearchOption.AllDirectories);
        string Redact(string value) => Regex.Replace(value.Replace(token.Trim(), "[credential]"), @"https?://\S+", "[URL]");
        await File.WriteAllTextAsync(Path.Combine(output, "provider.log"), Redact((await stdout) + "\n" + (await stderr)), CancellationToken.None);
        if (process.ExitCode != 0)
        {
            var diagnostic = Redact(await stderr);
            await File.WriteAllTextAsync(Path.Combine(output, "diagnostic.txt"), diagnostic, CancellationToken.None);
            var completed = FindResumeManifest(output, image.Path);
            var downloaded = Directory.GetFiles(output, "mineru-result.zip", SearchOption.AllDirectories).Any(path =>
            {
                try { using var archive = ZipFile.OpenRead(path); return archive.Entries.Any(e => e.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
            });
            throw new OcrProviderException(downloaded ? "MinerU 识别和下载已完成，但结果解包或整理失败。可重试，不重复上传。" : completed != null ? "MinerU 识别已完成，但结果 ZIP 下载失败。点击重试会继续下载，不重复上传。" :
                $"MinerU 未完成（退出码 {process.ExitCode}），原图和诊断记录已保存，可重试。", completed ?? resumeManifest);
        }
        if (manifests.Length != 1) throw new InvalidDataException("MinerU 回执不完整。");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifests[0]));
        var results = manifest.RootElement.GetProperty("results");
        if (results.GetArrayLength() != 1 || results[0].GetProperty("state").GetString() != "done") throw new InvalidDataException("本张图片未完成 OCR。");
        var run = Path.GetDirectoryName(manifests[0])!;
        var zips = Directory.GetFiles(run, "mineru-result.zip", SearchOption.AllDirectories);
        if (zips.Length != 1) throw new InvalidDataException("MinerU 结果缺少原始 ZIP。");
        using (var archive = ZipFile.OpenRead(zips[0]))
        {
            if (!archive.Entries.Any(e => e.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("OCR ZIP 中没有正文。");
            foreach (var entry in archive.Entries.Where(e => e.Length > 0)) { using var stream = entry.Open(); await stream.CopyToAsync(Stream.Null, ct); }
        }
        var markdown = Directory.GetFiles(run, "*.md", SearchOption.TopDirectoryOnly);
        if (markdown.Length != 1) throw new InvalidDataException("MinerU 未生成唯一正文。");
        var text = await File.ReadAllTextAsync(markdown[0], ct);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("图片未识别出可用文字。");
        return new(text, manifests[0]);
    }
    public static string? FindResumeManifest(string directory, string imagePath)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.GetFiles(directory, "manifest.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path)); var root = json.RootElement;
                if (root.GetProperty("inputs").GetArrayLength() == 1 && Path.GetFullPath(root.GetProperty("inputs")[0].GetString()!) == Path.GetFullPath(imagePath) &&
                    root.GetProperty("results").GetArrayLength() == 1 && root.GetProperty("results")[0].GetProperty("state").GetString() == "done") return path;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return null;
    }
}

public sealed class ImagePreparation(ImageMaterialStore store, IImageOcrExtractor ocr, ContentModule content)
{
    public async Task<string> TextAsync(ImageMaterialJob job, Action<string> progress, CancellationToken ct)
    {
        job.Validate(); ct.ThrowIfCancellationRequested(); job.Error = null;
        var items = job.Ocr.ToArray();
        var state = new object();
        var context = SynchronizationContext.Current;
        ExceptionDispatchInfo? failure = null;
        var next = 0;
        void Report(string message)
        {
            // The desktop supplies a control callback. Extractors may report from
            // another thread, but all UI notifications belong to the caller's context.
            if (context != null && !ReferenceEquals(SynchronizationContext.Current, context))
                context.Post(_ => progress(message), null);
            else progress(message);
        }
        void Save(Action update)
        {
            // Mutate and serialize under one lock: WorkflowJson uses one temporary
            // file per job, and a snapshot must not mix partially updated items.
            lock (state) { update(); store.Save(job); }
        }
        async Task Process(ImageOcrItem item)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = (item.Image.Role == "question" ? "正面" : "反面") + " " + item.Image.Index;
            if (item.RawText == null)
            {
                Report(prefix + "：正在 OCR…");
                var output = Path.Combine(store.DirectoryFor(job.Id), "ocr", item.Image.Role + "." + item.Image.Index, Guid.NewGuid().ToString("N"));
                var extracted = await ocr.ExtractAsync(item.Image, job.Settings, output, s => Report(prefix + "：" + s), ct, item.RawManifest);
                Save(() =>
                {
                    item.RawText = extracted.Text; item.RawManifest = extracted.ManifestPath;
                    item.RawSha256 = ImageAsset.Hash(Encoding.UTF8.GetBytes(extracted.Text));
                });
            }
            var rawText = item.RawText ?? throw new InvalidDataException("图片未识别出可用文字。");
            if (ImageAsset.Hash(Encoding.UTF8.GetBytes(rawText)) != item.RawSha256) throw new InvalidDataException("已保存的 OCR 原文发生变化。");
            ct.ThrowIfCancellationRequested();
            var attempt = Path.Combine(store.DirectoryFor(job.Id), "content", Guid.NewGuid().ToString("N"));
            // An interrupted review can resume its saved cleanup. Only an explicit
            // failed review requires another cleanup candidate.
            if (item.Cleanup == null || item.Review is { Verdict: not "pass" })
            {
                Report(prefix + "：正在清洗文字…");
                var cleaned = await content.CleanOcrAsync(job.Id + "." + item.Image.Role + "." + item.Image.Index, rawText, job.CleanupMode, job.Settings, ct);
                WorkflowJson.Save(Path.Combine(attempt, "cleanup.json"), cleaned);
                Save(() => { item.Cleanup = cleaned; item.Review = null; });
            }
            ImageContentValidation.Cleanup(rawText, item.Cleanup!);
            ct.ThrowIfCancellationRequested();
            if (item.Review == null)
            {
                Report(prefix + "：正在核对清洗结果…");
                var cleaned = item.Cleanup!;
                var reviewed = await content.ReviewImageContentAsync(job, "ocr", new { cleaned_text = cleaned.CleanedText,
                    notes = cleaned.Notes.Select(n => new { source_quote = n.SourceQuote, reason = n.Reason }) }, rawText, job.CleanupMode.Schema, ct);
                WorkflowJson.Save(Path.Combine(attempt, "review.json"), reviewed);
                Save(() => item.Review = reviewed);
            }
            var review = item.Review ?? throw new InvalidDataException("清洗结果尚未核对。");
            if (review.Verdict != "pass") throw new InvalidDataException("清洗结果待修正：" + string.Join("；", review.Errors.Select(e => e.Evidence)));
        }
        async Task Worker()
        {
            while (true)
            {
                ImageOcrItem item;
                lock (state)
                {
                    if (failure != null || ct.IsCancellationRequested || next == items.Length) return;
                    item = items[next++]; item.Error = null;
                }
                try { await Process(item); }
                catch (Exception ex)
                {
                    Save(() =>
                    {
                        if (ex is OcrProviderException provider && provider.ManifestPath != null) item.RawManifest = provider.ManifestPath;
                        item.Error = ex.Message; failure ??= ExceptionDispatchInfo.Capture(ex);
                    });
                    return;
                }
            }
        }
        // Async workers retain the caller's context; do not move UI callbacks or
        // job persistence to Task.Run. Drain started work before exposing a failure.
        await Task.WhenAll(Enumerable.Range(0, Math.Min(3, items.Length)).Select(_ => Worker()));
        ct.ThrowIfCancellationRequested();
        failure?.Throw();
        if (job.Ocr.Count != job.Images.Length || string.IsNullOrWhiteSpace(job.Text)) throw new InvalidDataException("图片文字尚未完整处理。");
        return job.Text;
    }
    public async Task<(ExcerptFields Fields, ModeSnapshot? Mode)> OriginalAsync(ImageMaterialJob job, string route, Action<string> progress, CancellationToken ct)
    {
        job.Validate();
        var fields = new ExcerptFields("", "") { FrontImages = job.Questions, BackImages = job.Answers };
        // Answer images are authoritative source material: never call OCR, solve or review here.
        if (job.Answers.Length > 0 || route == "image-excerpt") return (fields, null);
        var mode = job.Modes.SingleOrDefault(m => m.Definition.Id == route) ?? throw new InvalidDataException("请先选择一个可用图片模式。");
        progress("正在按正面图片顺序生成文字反面…");
        var attempt = Path.Combine(store.DirectoryFor(job.Id), "content", Guid.NewGuid().ToString("N"));
        job.Answer = await content.AnswerImagesAsync(job, mode, ct);
        WorkflowJson.Save(Path.Combine(attempt, "answer.json"), job.Answer); store.Save(job);
        if (!job.Answer.Answered) throw new InvalidDataException(job.Answer.DisplayText);
        progress("正在核对正面与反面…");
        job.Review = await content.ReviewImageContentAsync(job, "image", new { answer_state = job.Answer.AnswerState,
            answer_text = job.Answer.AnswerText, unresolved_reasons = job.Answer.UnresolvedReasons }, "", mode.Schema, ct, job.Questions);
        WorkflowJson.Save(Path.Combine(attempt, "review.json"), job.Review); store.Save(job);
        if (job.Review.Verdict != "pass") throw new InvalidDataException("反面内容待修正：" + string.Join("；", job.Review.Errors.Select(e => e.Evidence)));
        return (fields with { Back = job.Answer.AnswerText! }, mode);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NJsonSchema;

namespace KnowledgeClipper.Workflow;

public sealed record ImageAsset(string Path, string Sha256, string Role, int Index)
{
    public string FileName => "kc_image_" + Sha256 + ".png";
    public byte[] ReadVerified()
    {
        if (!System.IO.Path.IsPathFullyQualified(Path) || !Regex.IsMatch(Sha256, "^[a-f0-9]{64}$") ||
            Role is not ("question" or "answer") || Index < 1) throw new InvalidDataException("原图标识无效。");
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length is < 24 or > 33554432) throw new InvalidDataException("图片丢失或超过 32 MiB。");
        var bytes = File.ReadAllBytes(Path);
        if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || Hash(bytes) != Sha256)
            throw new InvalidDataException("原图内容已改变或不是 PNG，请重新采集。");
        return bytes;
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public string Html => "<img src=\"" + FileName + "\" style=\"max-width:100%;height:auto\">";
}
public sealed record OcrNote(string SourceQuote, string Reason);
public sealed record OcrCleanup(string CleanedText, OcrNote[] Notes);
public sealed record ImageReviewIssue(string Kind, string Evidence);
public sealed record ImageReview(string Verdict, ImageReviewIssue[] Errors, ImageReviewIssue[] Warnings);
public sealed class ImageOcrItem
{
    public required ImageAsset Image { get; init; }
    public string? RawText { get; set; }
    public string? RawSha256 { get; set; }
    public string? RawManifest { get; set; }
    public OcrCleanup? Cleanup { get; set; }
    public ImageReview? Review { get; set; }
    public string? Error { get; set; }
}
public sealed class ImageMaterialJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required LanguageSettings Settings { get; init; }
    public required ImageAsset[] Images { get; init; }
    public required MaterialKind Kind { get; init; }
    public required ModeSnapshot[] Modes { get; init; }
    public required ModeSnapshot CleanupMode { get; init; }
    public required ModeSnapshot ReviewMode { get; init; }
    public List<ImageOcrItem> Ocr { get; set; } = [];
    public string? Route { get; set; }
    public string Entry { get; set; } = "multi";
    public bool Quick { get; set; }
    public QuestionAnswer? Answer { get; set; }
    public ImageReview? Review { get; set; }
    public string? Error { get; set; }
    public string? DestinationId { get; set; }
    public string? DestinationKind { get; set; }
    [JsonIgnore] public ImageAsset[] Questions => Images.Where(i => i.Role == "question").ToArray();
    [JsonIgnore] public ImageAsset[] Answers => Images.Where(i => i.Role == "answer").ToArray();
    [JsonIgnore] public string Text => string.Join("\n\n", Ocr.Select(i => i.Cleanup?.CleanedText ?? ""));
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Questions.Length == 0 || Images.Length > 50) throw new InvalidDataException("需至少一张正面图片，最多 50 张图片。");
        foreach (var role in new[] { "question", "answer" })
            if (!Images.Where(i => i.Role == role).Select(i => i.Index).SequenceEqual(Enumerable.Range(1, Images.Count(i => i.Role == role))))
                throw new InvalidDataException("图片顺序无效。");
        foreach (var item in Images) item.ReadVerified();
    }
}

public sealed class ImageMaterialStore(string root)
{
    public string DirectoryFor(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(root, "image-materials", id) : throw new InvalidDataException("图片任务标识无效。");
    public void Save(ImageMaterialJob job) => WorkflowJson.Save(Path.Combine(DirectoryFor(job.Id), "job.json"), job);
    public ImageMaterialJob Find(string id) => WorkflowJson.Read<ImageMaterialJob>(Path.Combine(DirectoryFor(id), "job.json"));
    public IEnumerable<ImageMaterialJob> Recent()
    {
        var directory = Path.Combine(root, "image-materials");
        if (!Directory.Exists(directory)) yield break;
        foreach (var dir in new DirectoryInfo(directory).EnumerateDirectories().OrderByDescending(d => d.CreationTimeUtc).Take(30))
        {
            ImageMaterialJob? value = null;
            try { value = Find(dir.Name); } catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
            if (value != null) yield return value;
        }
    }
    public ImageMaterialJob Create(IEnumerable<(string Path, string Role)> inputs, MaterialKind kind, LanguageSettings settings, string defaults, string overrides)
    {
        var source = inputs.ToArray();
        if (source.Length > 50 || !source.Any(i => i.Role == "question") || source.Any(i => i.Role is not ("question" or "answer")))
            throw new InvalidDataException("需至少一张正面图片，最多 50 张图片，角色须为正面或反面。");
        var id = Guid.NewGuid().ToString("N"); var destination = DirectoryFor(id);
        Directory.CreateDirectory(destination);
        var images = new List<ImageAsset>();
        foreach (var group in new[] { "question", "answer" })
        {
            var index = 0;
            foreach (var input in source.Where(i => i.Role == group))
            {
                var bytes = File.ReadAllBytes(input.Path); var hash = ImageAsset.Hash(bytes);
                var asset = new ImageAsset(Path.Combine(destination, $"{group}.{++index:D4}.png"), hash, group, index);
                File.WriteAllBytes(asset.Path, bytes); asset.ReadVerified(); images.Add(asset);
            }
        }
        var job = new ImageMaterialJob { Id = id, Settings = settings with { }, Kind = kind, Images = images.ToArray(),
            Modes = ImageModeLoader.Load(defaults, overrides),
            CleanupMode = ImageModeLoader.Stage(defaults, overrides, "ocr-cleanup", "ocr-cleanup.schema.json"),
            ReviewMode = ImageModeLoader.Stage(defaults, overrides, "content-review", "content-review.schema.json") };
        job.Validate(); job.Route = MaterialRouting.Resolve(kind, settings, imageModes: job.Modes);
        job.Ocr = images.Select(image => new ImageOcrItem { Image = image }).ToList(); Save(job); return job;
    }
}

public static class ImageModeLoader
{
    private static string Read(string defaults, string? overrides, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Split('/', '\\').Any(p => p == "..")) throw new InvalidDataException("模式文件必须位于 modes 内。");
        var custom = overrides == null ? null : Path.Combine(overrides, name);
        return File.ReadAllText(custom != null && File.Exists(custom) ? custom : Path.Combine(defaults, name));
    }
    public static ModeSnapshot[] Load(string defaults, string? overrides = null)
    {
        string registry;
        try { registry = Read(defaults, overrides, "image-modes.json"); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        var modes = JsonSerializer.Deserialize<ModeFile>(registry, WorkflowJson.Options)?.Modes ?? [];
        if (modes.Any(m => string.IsNullOrWhiteSpace(m.Id) || m.Id == "image-excerpt" || m.OutputKind != "image-answer") || modes.Select(m => m.Id).Distinct().Count() != modes.Length)
            throw new InvalidDataException("图片模式 id 必须唯一，输出类型应为 image-answer。");
        return modes.Where(m => m.Enabled).Select(m => Snapshot(defaults, overrides, m)).ToArray();
    }
    private static ModeSnapshot Snapshot(string defaults, string? overrides, ModeDefinition definition)
    {
        try { return new(definition, Read(defaults, overrides, definition.PromptFile), Read(defaults, overrides, definition.SchemaFile)); }
        catch (IOException ex) { return new(definition, "", "", ex.Message); }
    }
    public static ModeSnapshot Stage(string defaults, string? overrides, string id, string schema) => Snapshot(defaults, overrides,
        new(id, id, true, "", id + ".md", schema, id, RequiresContext: false));
}

public static class ImageContentValidation
{
    public static void MathLines(string text)
    {
        foreach (Match formula in Regex.Matches(text, @"\\\(.*?\\\)|\\\[.*?\\\]", RegexOptions.Singleline))
            if (formula.Value.IndexOfAny(['\r', '\n', '\t']) >= 0) throw new InvalidDataException("公式内部含真实换行或制表符，请重试整理。");
    }
    public static void Cleanup(string source, OcrCleanup result)
    {
        if (string.IsNullOrWhiteSpace(result.CleanedText) || result.CleanedText.Length > source.Length * 1.5 + 300 ||
            source.Length >= 200 && result.CleanedText.Length < source.Length * .45) throw new InvalidDataException("OCR 清洗为空或正文长度异常。");
        if (result.Notes.Any(n => string.IsNullOrEmpty(n.SourceQuote) || !source.Contains(n.SourceQuote, StringComparison.Ordinal)))
            throw new InvalidDataException("OCR 疑点引用未逐字对应原始识别文字。");
        MathLines(result.CleanedText);
    }
}

public sealed partial class ContentModule
{
    private async Task<JsonElement> ImageStageAsync(ModeSnapshot mode, object input, LanguageSettings settings, CancellationToken ct, IReadOnlyList<ImageAsset>? images = null,
        string? validationSchema = null)
    {
        if (mode.LoadError != null || string.IsNullOrWhiteSpace(mode.Prompt)) throw new InvalidDataException(mode.LoadError ?? "图片提示词为空。");
        var schema = await JsonSchema.FromJsonAsync(validationSchema ?? mode.Schema, ct);
        return await RequestAsync(settings, mode.Prompt + "\nJSON 字段约定：\n" + mode.Schema, input, json =>
        {
            var errors = schema.Validate(json);
            if (errors.Count != 0) throw new InvalidDataException("图片流程输出字段不符：" + string.Join("、", errors.Select(e => e.Path + ":" + e.Kind)));
            using var document = JsonDocument.Parse(json); return Task.FromResult(document.RootElement.Clone());
        }, ct, images, 8192, disableThinking: true);
    }
    public async Task<OcrCleanup> CleanOcrAsync(string id, string source, ModeSnapshot mode, LanguageSettings settings, CancellationToken ct)
    {
        var json = await ImageStageAsync(mode, new { source_id = id, ocr_text = source }, settings, ct);
        var result = new OcrCleanup(json.GetProperty("cleaned_text").GetString()!, json.GetProperty("notes").EnumerateArray()
            .Select(n => new OcrNote(n.GetProperty("source_quote").GetString()!, n.GetProperty("reason").GetString()!)).ToArray());
        ImageContentValidation.Cleanup(source, result); return result;
    }
    public async Task<QuestionAnswer> AnswerImagesAsync(ImageMaterialJob job, ModeSnapshot mode, CancellationToken ct)
    {
        job.Validate();
        if (job.Answers.Length > 0) throw new InvalidOperationException("已有反面图片，应由程序直接配对。");
        var json = await ImageStageAsync(mode, new { source_id = job.Id, instruction = "按明确标注的角色和顺序阅读全部正面图片，共同生成本次文字反面。" },
            job.Settings with { Model = job.Settings.ImageModel }, ct, job.Questions);
        var answer = new QuestionAnswer(json.GetProperty("answer_state").GetString()!, json.GetProperty("answer_text").GetString(),
            json.GetProperty("unresolved_reasons").EnumerateArray().Select(v => v.GetString()!).ToArray());
        if (answer.Answered) ImageContentValidation.MathLines(answer.AnswerText!);
        return answer;
    }
    public async Task<ImageReview> ReviewImageContentAsync(ImageMaterialJob job, string taskKind, object candidate, string source, string contract,
        CancellationToken ct, IReadOnlyList<ImageAsset>? images = null)
    {
        var json = await ImageStageAsync(job.ReviewMode, new { task_kind = taskKind, source_id = job.Id, ocr_text = source, candidate, field_contract = contract },
            images == null ? job.Settings : job.Settings with { Model = job.Settings.ImageModel }, ct, images, ReviewValidationSchema(job.ReviewMode.Schema));
        ImageReviewIssue[] Issues(string key) => json.GetProperty(key).EnumerateArray().Select(e => new ImageReviewIssue(e.GetProperty("kind").GetString()!, e.GetProperty("evidence").GetString()!)).ToArray();
        var review = new ImageReview(json.GetProperty("verdict").GetString()!, Issues("errors"), Issues("warnings"));
        // NJsonSchema treats maxItems: 0 as its unconstrained default. Keep the
        // verdict/error relationship explicit even when a schema accepts it.
        if (review.Verdict is not ("pass" or "fail") || (review.Verdict == "pass") != (review.Errors.Length == 0))
            throw new InvalidDataException("审核结论与错误列表不一致，请重试审核。");
        return review;
    }
    private static string ReviewValidationSchema(string frozen)
    {
        // Earlier jobs froze Draft 7 const verdicts. NJsonSchema ignores const,
        // so give its parser equivalent enum constraints without changing the job.
        if (string.IsNullOrWhiteSpace(frozen)) return frozen;
        var schema = JsonNode.Parse(frozen);
        if (schema is not JsonObject root || root["oneOf"] is not JsonArray branches) return frozen;
        var changed = false;
        foreach (var branch in branches.OfType<JsonObject>())
        {
            if (branch["properties"] is not JsonObject properties || properties["verdict"] is not JsonObject verdict ||
                !verdict.TryGetPropertyValue("const", out var value)) continue;
            var singleValue = new JsonArray { value?.DeepClone() };
            if (verdict.ContainsKey("enum"))
            {
                if (verdict.TryGetPropertyValue("allOf", out var existing) && existing is not JsonArray)
                    throw new InvalidDataException("审核字段约定中的 allOf 必须是数组。");
                var constraints = existing as JsonArray ?? new JsonArray();
                if (!verdict.ContainsKey("allOf")) verdict["allOf"] = constraints;
                constraints.Add(new JsonObject { ["enum"] = singleValue });
            }
            else verdict["enum"] = singleValue;
            verdict.Remove("const"); changed = true;
        }
        return changed ? schema.ToJsonString() : frozen;
    }
}

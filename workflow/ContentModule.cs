using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NJsonSchema;

namespace KnowledgeClipper.Workflow;

public static class ModeConfigurationLoader
{
    public static ContentConfiguration Load(string defaults, string? overrides = null)
    {
        string Read(string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p == "..")) throw new InvalidDataException("模式文件路径无效。");
            var user = overrides == null ? null : Path.Combine(overrides, relative);
            return File.ReadAllText(user != null && File.Exists(user) ? user : Path.Combine(defaults, relative));
        }
        var file = JsonSerializer.Deserialize<ModeFile>(Read("modes.json"), WorkflowJson.Options) ?? throw new InvalidDataException("缺少模式配置。");
        var modes = file.Modes.Where(m => m.Enabled).ToArray();
        if (modes.Length == 0 || modes.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != modes.Length) throw new InvalidDataException("请启用至少一个模式，且模式标识不得重复。");
        return new ContentConfiguration(Read("classification.md"), modes.Select(m => new ModeSnapshot(m, Read(m.PromptFile), Read(m.SchemaFile))).ToArray());
    }
}

public sealed class ModeChoiceRequiredException(string message) : Exception(message);

public static class ContentCredentials
{
    public static string Resolve(string? protectedKey = null)
    {
        if (!string.IsNullOrWhiteSpace(protectedKey)) return protectedKey;
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User })
            foreach (var name in new[] { "AI_PRODUCER_API_KEY", "DEEPSEEK_API_KEY" })
            {
                var value = Environment.GetEnvironmentVariable(name, scope);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        var legacy = @"C:\Users\fy\Documents\New project\DeepSeek API 配置.md";
        if (File.Exists(legacy))
        {
            var match = Regex.Match(File.ReadAllText(legacy), @"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{20,}");
            if (match.Success) return match.Value;
        }
        throw new InvalidOperationException("未配置 DeepSeek API Key，请在设置的安全输入框中填写。");
    }
}

public sealed class ContentModule(HttpClient http, Func<string> key)
{
    // One retry location for each necessary request; no recursive retry or reflection pipeline.
    private async Task<T> RequestAsync<T>(LanguageSettings settings, string prompt, object input,
        Func<string, Task<T>> validate, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key());
                request.Content = JsonContent.Create(new
                {
                    model = settings.Model, response_format = new { type = "json_object" }, max_tokens = 2600,
                    messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = JsonSerializer.Serialize(input, WorkflowJson.Options) } }
                });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException("内容 API 身份验证失败，请检查安全设置。");
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("内容 API 请求失败（HTTP " + (int)response.StatusCode + "）。");
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var choice = result.RootElement.GetProperty("choices")[0];
                if (choice.GetProperty("finish_reason").GetString() != "stop") throw new InvalidDataException("内容响应未完整结束。");
                var content = choice.GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("内容响应为空。");
                return await validate(content);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or HttpRequestException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                last = ex;
                if (attempt == 0) await Task.Delay(500, cancellationToken);
            }
        }
        throw new InvalidDataException("内容处理未完成，可重试本次材料。" + last?.Message, last);
    }

    public Task<string> ClassifyAsync(CaptureMaterial material, ContentConfiguration config, LanguageSettings settings, CancellationToken ct)
    {
        material.Validate();
        return RequestAsync(settings, config.ClassificationPrompt,
            new { material.SelectedText, material.ContextText, material.ContextOrigin, modes = config.Modes.Select(m => new { m.Definition.Id, m.Definition.Name, m.Definition.Description }) },
            json =>
            {
                using var doc = JsonDocument.Parse(json);
                var id = doc.RootElement.GetProperty("modeId").GetString();
                if (id == null) throw new ModeChoiceRequiredException(doc.RootElement.TryGetProperty("question", out var question) ? question.GetString() ?? "请选择本次模式。" : "请选择本次模式。");
                if (!config.Modes.Any(m => m.Definition.Id == id)) throw new InvalidDataException("返回了未启用的模式。");
                return Task.FromResult(id);
            }, ct);
    }

    public async Task<ContentResult> ProcessAsync(CaptureMaterial material, string modeId, ContentConfiguration config, LanguageSettings settings, CancellationToken ct)
    {
        material.Validate();
        if (material.NeedsContextChoice) throw new InvalidDataException("请先处理缺失的上下文。");
        var mode = config.Modes.Single(m => m.Definition.Id == modeId);
        var schema = await JsonSchema.FromJsonAsync(mode.Schema, ct);
        return await RequestAsync(settings, mode.Prompt + "\n\nJSON 输出必须符合以下字段约定：\n" + mode.Schema,
            new { material.SelectedText, material.ContextText, material.ContextOrigin, material.TargetStart },
            json =>
            {
                var errors = schema.Validate(json);
                if (errors.Count != 0) throw new InvalidDataException("内容字段校验失败：" + string.Join("、", errors.Select(e => e.Path + ":" + e.Kind)));
                var fields = JsonSerializer.Deserialize<LanguageFields>(json, WorkflowJson.Options)!;
                fields = ResolveGeneratedTarget(material, fields);
                ValidateFields(material, fields);
                return Task.FromResult(new ContentResult(modeId, fields));
            }, ct);
    }

    private static LanguageFields ResolveGeneratedTarget(CaptureMaterial material, LanguageFields fields)
    {
        if (material.ContextOrigin != "generated_allowed" || material.ContextText != null) return fields;
        var sentence = fields.GeneratedSentence;
        if (string.IsNullOrWhiteSpace(sentence)) throw new InvalidDataException("缺少生成例句。");
        var start = sentence.IndexOf(material.SelectedText, StringComparison.Ordinal);
        // Search from the next UTF-16 position so overlapping occurrences are ambiguous too.
        if (start < 0 || sentence.IndexOf(material.SelectedText, start + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("生成例句必须包含且只包含一次原样目标。");
        return fields with { GeneratedTargetStart = start };
    }

    public static void ValidateFields(CaptureMaterial material, LanguageFields fields)
    {
        if (string.IsNullOrWhiteSpace(fields.Pronunciation) || string.IsNullOrWhiteSpace(fields.Meaning)) throw new InvalidDataException("读音和语境义不能为空。");
        if (material.ContextText != null)
        {
            if (fields.GeneratedSentence != null || fields.GeneratedTargetStart != null) throw new InvalidDataException("已有原文时不能替换为生成句。");
        }
        else
        {
            var sentence = fields.GeneratedSentence;
            if (string.IsNullOrWhiteSpace(sentence) || fields.GeneratedTargetStart is not int start || start < 0 ||
                start > sentence.Length - material.SelectedText.Length || !sentence.AsSpan(start, material.SelectedText.Length).SequenceEqual(material.SelectedText))
                throw new InvalidDataException("生成例句中的目标原文或位置不正确。");
        }
    }
}

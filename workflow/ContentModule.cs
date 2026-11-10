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
        var file = LoadDefinitions(defaults, overrides);
        var concrete = file.Modes.Any(m => m.Enabled && LanguageModes.IsConcrete(m));
        var modes = file.Modes.Where(m => m.Enabled && !(concrete && m.Id == "language" && !LanguageModes.IsConcrete(m))).ToArray();
        if (modes.Length == 0 || modes.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != modes.Length) throw new InvalidDataException("请启用至少一个模式，且模式标识不得重复。");
        foreach (var mode in modes) DeckRouting.ValidatePattern(DeckRouting.PathFor(mode), mode.OutputKind);
        ModeSnapshot Snapshot(ModeDefinition mode)
        {
            string prompt = "", schema = "";
            var errors = new List<string>();
            try { prompt = Read(mode.PromptFile); if (string.IsNullOrWhiteSpace(prompt)) errors.Add("提示词为空：" + mode.PromptFile); }
            catch (IOException) { errors.Add("缺少提示词：" + mode.PromptFile); }
            try { schema = Read(mode.SchemaFile); if (string.IsNullOrWhiteSpace(schema)) errors.Add("输出定义为空：" + mode.SchemaFile); }
            catch (IOException) { errors.Add("缺少输出定义：" + mode.SchemaFile); }
            return new(mode, prompt, schema, errors.Count == 0 ? null : string.Join("；", errors));
        }
        string classification;
        try { classification = Read("classification.md"); }
        catch (IOException) { classification = ""; } // Explicit desktop entries do not depend on the legacy classifier.
        return new ContentConfiguration(classification, modes.Select(Snapshot).ToArray());
    }

    public static ModeFile LoadDefinitions(string defaults, string? overrides = null)
    {
        var defaultsFile = WorkflowJson.Read<ModeFile>(Path.Combine(defaults, "modes.json"));
        var userPath = overrides == null ? null : Path.Combine(overrides, "modes.json");
        if (userPath == null || !File.Exists(userPath)) return defaultsFile;
        var userFile = WorkflowJson.Read<ModeFile>(userPath);
        // An old personal override must not hide new built-in modes after an upgrade.
        if (userFile.Modes.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != userFile.Modes.Length)
            throw new InvalidDataException("个人模式标识重复。");
        return new ModeFile(defaultsFile.Modes.Select(m => userFile.Modes.FirstOrDefault(u => u.Id == m.Id) is { } user
                ? user with { AnkiDeckPath = user.AnkiDeckPath ?? m.AnkiDeckPath, Language = user.Language ?? m.Language, Practice = user.Practice ?? m.Practice,
                    ContentFormat = user.ContentFormat ?? m.ContentFormat } : m)
            .Concat(userFile.Modes.Where(u => !defaultsFile.Modes.Any(m => m.Id == u.Id))).ToArray());
    }

    public static ModeRoute Route(ContentConfiguration config, string modeId)
    {
        if (modeId == "language" && LanguageModes.HasConcrete(config)) return new("language", "language", false, true);
        var mode = config.Modes.Single(m => m.Definition.Id == modeId).Definition;
        if (mode.OutputKind is not ("language" or "translation" or "question"))
            throw new InvalidDataException("当前未接入此模式的输出类型：" + mode.OutputKind);
        return new(mode.Id, mode.OutputKind, mode.FollowUpDefault, mode.RequiresContext);
    }
}

public sealed class ModeChoiceRequiredException(string message) : Exception(message);

public static class ContentCredentials
{
    public static string Resolve(string? protectedKey = null)
    {
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User })
            foreach (var name in new[] { "DEEPSEEK_KNOWLEDGECLIPPER_API_KEY" })
            {
                var value = Environment.GetEnvironmentVariable(name, scope);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        throw new InvalidOperationException("未配置 DEEPSEEK_KNOWLEDGECLIPPER_API_KEY 用户环境变量。");
    }
}

public sealed partial class ContentModule(HttpClient http, Func<string> key)
{
    public static bool IsQuestion(string text) => text.TrimEnd().EndsWith('?') || text.TrimEnd().EndsWith('？');

    // One retry location for each necessary request; no recursive retry or reflection pipeline.
    private async Task<T> RequestAsync<T>(LanguageSettings settings, string prompt, object input,
        Func<string, Task<T>> validate, CancellationToken cancellationToken, IReadOnlyList<ImageAsset>? images = null,
        int maxTokens = 2600, bool disableThinking = false)
    {
        Exception? last = null;
        string? rejectedContent = null;
        string? validationFeedback = null;
        if (settings.ContentMaxRetries is < 0 or > 5 || settings.ContentTimeoutSeconds is < 1 or > 600)
            throw new InvalidDataException("内容请求的重试次数或超时超出范围。");
        for (var attempt = 0; attempt <= settings.ContentMaxRetries; attempt++)
        {
            try
            {
                var openai = OpenAiTransport.Selected(settings);
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiBaseUrl.TrimEnd('/') + (openai ? "/responses" : "/chat/completions"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiTransport.Credential(settings, key));
                object userContent = JsonSerializer.Serialize(input, WorkflowJson.Options);
                if (images is { Count: > 0 })
                {
                    var parts = new List<object> { new { type = "text", text = (string)userContent } };
                    foreach (var item in images)
                    {
                        var bytes = item.ReadVerified();
                        parts.Add(new { type = "text", text = $"image role={item.Role}, index={item.Index}, source_id={item.Sha256}" });
                        parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(bytes) } });
                    }
                    userContent = parts;
                }
                var messages = new List<object> { new { role = "system", content = prompt }, new { role = "user", content = userContent } };
                if (rejectedContent != null && validationFeedback != null)
                {
                    // Keep the original material intact and only the latest failed candidate.
                    // Transport/authentication errors are never sent back to the model.
                    messages.Add(new { role = "assistant", content = rejectedContent });
                    messages.Add(new { role = "user", content =
                        "上一份 JSON 未通过本地校验。请根据原始输入和原字段约定重新输出完整 JSON，修正以下问题：\n" + validationFeedback +
                        "\n不得改写原始输入或放宽约束。涉及目标原文时，必须保留 selectedText 的大小写、空格和全部字符；生成例句允许重复目标，由程序选择首次原样匹配。位置字段仍按原字段约定返回，不自行猜测。" });
                }
                var payload = new Dictionary<string, object> {
                    ["model"] = settings.Model, ["response_format"] = new { type = "json_object" }, ["max_tokens"] = maxTokens,
                    ["messages"] = messages
                };
                if (disableThinking) payload["thinking"] = new { type = "disabled" };
                request.Content = JsonContent.Create(openai ? OpenAiTransport.Payload(settings.Model, messages,
                    settings.Model == "gpt-6.1-sol" ? Math.Max(8192, maxTokens) : maxTokens, true, false,
                    settings.Model == "gpt-6.1-sol" ? "high" : "low") : payload);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.ContentTimeoutSeconds));
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException("内容 API 身份验证失败，请检查安全设置。");
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("内容 API 请求失败（HTTP " + (int)response.StatusCode + "）。");
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                string? content;
                if (openai) content = OpenAiTransport.Text(result.RootElement);
                else
                {
                    var choice = result.RootElement.GetProperty("choices")[0];
                    if (choice.GetProperty("finish_reason").GetString() != "stop") throw new InvalidDataException("内容响应未完整结束。");
                    content = choice.GetProperty("message").GetProperty("content").GetString();
                }
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("内容响应为空。");
                // Validate JSON with the same parser used by the workflow before a
                // schema library can throw a different, unhandled parser exception.
                try
                {
                    using var parsedContent = JsonDocument.Parse(content);
                    return await validate(content);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                {
                    rejectedContent = content;
                    validationFeedback = ex is JsonException ? "输出必须是完整、可解析的 JSON。" : ex.Message;
                    throw;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or HttpRequestException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                last = ex;
                if (attempt < settings.ContentMaxRetries) await Task.Delay(500, cancellationToken);
            }
        }
        throw new InvalidDataException("内容处理未完成，可重试本次材料。" + last?.Message, last);
    }

    public Task<string> ClassifyAsync(CaptureMaterial material, ContentConfiguration config, LanguageSettings settings, CancellationToken ct,
        InputMetadata? metadata = null)
    {
        material.Validate();
        metadata ??= new();
        // The explicit input box has a user-defined punctuation rule, before any model call.
        if (metadata.Intent == "question" || metadata.QuestionMarkRouting && IsQuestion(material.SelectedText))
        {
            var questionMode = config.Modes.SingleOrDefault(m => m.Definition.OutputKind == "question")
                ?? throw new InvalidDataException("请启用问答模式。");
            return Task.FromResult(questionMode.Definition.Id);
        }
        var choices = LanguageModes.ClassificationChoices(config);
        var candidates = metadata.Intent == "lookup" ? choices.Where(m => m.OutputKind != "question").ToArray() : choices;
        return RequestAsync(settings, config.ClassificationPrompt,
            new { material.SelectedText, material.ContextText, material.ContextOrigin, metadata.Intent,
                modes = candidates.Select(m => new { m.Id, m.Name, m.Description }) },
            json =>
            {
                using var doc = JsonDocument.Parse(json);
                var id = doc.RootElement.GetProperty("modeId").GetString();
                if (id == null) throw new ModeChoiceRequiredException(doc.RootElement.TryGetProperty("question", out var question) ? question.GetString() ?? "请选择本次模式。" : "请选择本次模式。");
                if (!candidates.Any(m => m.Id == id)) throw new InvalidDataException("返回了本次输入不适用的模式。");
                return Task.FromResult(id);
            }, ct);
    }

    public async Task<ContentResult> ProcessAsync(CaptureMaterial material, string modeId, ContentConfiguration config, LanguageSettings settings, CancellationToken ct,
        string? questionText = null)
    {
        material.Validate();
        var mode = config.Modes.Single(m => m.Definition.Id == modeId);
        if (mode.LoadError != null) throw new InvalidDataException(mode.Definition.Name + " 尚未配置：" + mode.LoadError);
        if (mode.Definition.OutputKind == "question")
        {
            var question = questionText ?? material.SelectedText;
            var answer = await AnswerAsync(material, null, [], question, config, settings, ct, modeId);
            return new ContentResult(modeId, Basic: answer.Answered ? new(question, answer.AnswerText!) : null, Answer: answer);
        }
        if (mode.Definition.RequiresContext && material.NeedsContextChoice) throw new InvalidDataException("请先处理缺失的上下文。");
        var markedContext = PublishedLanguage.Applies(mode.Definition) ? PublishedLanguage.MarkedContext(material) : null;
        var schema = await JsonSchema.FromJsonAsync(mode.Schema, ct);
        return await RequestAsync(settings, mode.Prompt + "\n\nJSON 输出必须符合以下字段约定：\n" + mode.Schema,
            new { material.SelectedText, material.ContextText, material.ContextOrigin, material.TargetStart, markedContext },
            json =>
            {
                var errors = schema.Validate(json);
                if (errors.Count != 0) throw new InvalidDataException("内容字段校验失败：" + string.Join("、", errors.Select(e => e.Path + ":" + e.Kind)));
                if (mode.Definition.OutputKind == "translation")
                {
                    using var doc = JsonDocument.Parse(json);
                    var translation = doc.RootElement.GetProperty("translation").GetString();
                    if (string.IsNullOrWhiteSpace(translation)) throw new InvalidDataException("英文翻译不能为空。");
                    return Task.FromResult(new ContentResult(modeId, Basic: new(material.SelectedText, translation)));
                }
                if (mode.Definition.OutputKind != "language") throw new InvalidDataException("模式输出类型尚未接入。");
                if (PublishedLanguage.Applies(mode.Definition))
                    return Task.FromResult(new ContentResult(modeId, PublishedLanguage.Parse(json, mode.Definition, material)));
                var fields = JsonSerializer.Deserialize<LanguageFields>(json, WorkflowJson.Options)!;
                fields = ResolveGeneratedTarget(material, fields);
                ValidateFields(material, fields);
                return Task.FromResult(new ContentResult(modeId, fields));
            }, ct);
    }

    public async Task<QuestionAnswer> AnswerAsync(CaptureMaterial originalMaterial, ContentResult? initialContent,
        IReadOnlyList<ConversationTurn> conversation, string question, ContentConfiguration config, LanguageSettings settings,
        CancellationToken ct, string? modeId = null, ContentPreview? currentPreview = null,
        string? trustedPrompt = null, IReadOnlyList<ImageAsset>? images = null, bool preserveRawAnswer = false)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new InvalidDataException("请输入追问内容。");
        var mode = modeId == null ? config.Modes.SingleOrDefault(m => m.Definition.OutputKind == "question")
            : config.Modes.SingleOrDefault(m => m.Definition.Id == modeId && m.Definition.OutputKind == "question");
        if (mode == null) throw new InvalidDataException("请启用问答模式后再追问。");
        if (mode.LoadError != null) throw new InvalidDataException(mode.Definition.Name + " 尚未配置：" + mode.LoadError);
        var schema = await JsonSchema.FromJsonAsync(mode.Schema, ct);
        return await RequestAsync(originalMaterial.VisualQuestion && images == null ? settings with { Model = settings.ImageModel } : settings,
            mode.Prompt + (trustedPrompt == null ? "" : "\n\n" + trustedPrompt) + "\n\nJSON 输出必须符合以下字段约定：\n" + mode.Schema,
            new { originalMaterial = new { originalMaterial.SelectedText, originalMaterial.ContextText, originalMaterial.ContextOrigin },
                initialContent, currentPreview, conversation, question },
            json =>
            {
                var errors = schema.Validate(json);
                if (errors.Count != 0) throw new InvalidDataException("回答字段校验失败：" + string.Join("、", errors.Select(e => e.Path + ":" + e.Kind)));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var answer = new QuestionAnswer(root.GetProperty("answer_state").GetString()!,
                    root.TryGetProperty("answer_text", out var text) ? text.GetString() : null,
                    root.GetProperty("unresolved_reasons").EnumerateArray().Select(x => x.GetString()!).ToArray());
                if (answer.AnswerState is not ("answered" or "unresolved") ||
                    answer.Answered && (string.IsNullOrWhiteSpace(answer.AnswerText) || answer.UnresolvedReasons.Length != 0) ||
                    !answer.Answered && (answer.UnresolvedReasons.Length == 0 || answer.UnresolvedReasons.Any(string.IsNullOrWhiteSpace)))
                    throw new InvalidDataException("回答状态与内容不一致。");
                return Task.FromResult(answer.Answered && !preserveRawAnswer ? answer with { AnswerText = AnkiMathText.Normalize(answer.AnswerText!) } : answer);
            }, ct, images ?? (originalMaterial.VisualQuestion ? [originalMaterial.FrontImage!] : null));
    }

    private static LanguageFields ResolveGeneratedTarget(CaptureMaterial material, LanguageFields fields)
    {
        if (material.ContextOrigin != "generated_allowed" || material.ContextText != null) return fields;
        var sentence = fields.GeneratedSentence;
        if (string.IsNullOrWhiteSpace(sentence)) throw new InvalidDataException("缺少生成例句。");
        var start = PublishedLanguage.FirstTarget(sentence, material.SelectedText);
        return fields with { GeneratedTargetStart = start };
    }

    public static void ValidateFields(CaptureMaterial material, LanguageFields fields)
    {
        if (PublishedLanguage.Applies(fields))
            throw new InvalidDataException("正式语言字段需要按具体模式校验。");
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

using System.Text.Json;
using KnowledgeClipper.Workflow;

try
{
    var options = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("用法：--input 文件 --modes 目录 [--settings 文件] [--overrides 目录] [--mode 标识] [--output 文件]");
        options.Add(args[i], args[i + 1]);
    }
    using var input = JsonDocument.Parse(File.ReadAllText(options["--input"]));
    var data = input.RootElement;
    var text = data.GetProperty("selectedText").GetString()!;
    var context = data.TryGetProperty("contextText", out var c) ? c.GetString() : null;
    var origin = data.TryGetProperty("contextOrigin", out var o) ? o.GetString()! : context == null ? "generated_allowed" : "source";
    int? start = data.TryGetProperty("targetStart", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
    var material = new CaptureMaterial(text, context, origin, start, "独立内容评估", 0, DateTimeOffset.UtcNow);
    material.Validate();
    var config = ModeConfigurationLoader.Load(options["--modes"], options.GetValueOrDefault("--overrides"));
    var settings = options.TryGetValue("--settings", out var settingsFile) ? WorkflowJson.Read<LanguageSettings>(settingsFile) : new LanguageSettings();
    settings.Validate();
    using var http = new HttpClient();
    var module = new ContentModule(http, () => ContentCredentials.Resolve());
    var metadata = data.TryGetProperty("input", out var meta) ? meta.Deserialize<InputMetadata>(WorkflowJson.Options) : new InputMetadata();
    object result;
    if (data.TryGetProperty("followUp", out var followUp))
    {
        var initial = data.TryGetProperty("initialContent", out var ic) ? ic.Deserialize<ContentResult>(WorkflowJson.Options) : null;
        var conversation = data.TryGetProperty("conversation", out var turns) ? turns.Deserialize<ConversationTurn[]>(WorkflowJson.Options)! : [];
        var preview = data.TryGetProperty("currentPreview", out var cp) ? cp.Deserialize<ContentPreview>(WorkflowJson.Options) : null;
        result = await module.AnswerAsync(material, initial, conversation, followUp.GetString()!, config, settings, CancellationToken.None, currentPreview: preview);
    }
    else
    {
        var mode = options.GetValueOrDefault("--mode") ?? await module.ClassifyAsync(material, config, settings, CancellationToken.None, metadata);
        result = await module.ProcessAsync(material, mode, config, settings, CancellationToken.None);
    }
    var json = JsonSerializer.Serialize(result, WorkflowJson.Options);
    if (options.TryGetValue("--output", out var output)) WorkflowJson.Save(output, result);
    else Console.WriteLine(json);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex is HttpRequestException or TaskCanceledException ? "内容请求连接失败或超时。" : ex.Message);
    return 1;
}

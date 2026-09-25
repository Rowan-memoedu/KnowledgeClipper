using System.Text.Json;
using NJsonSchema;

namespace KnowledgeClipper.Workflow;

public static class EntryRouting
{
    public static string ModeId(ContentConfiguration configuration, string intent)
    {
        if (intent == "language" && LanguageModes.HasConcrete(configuration)) return "language";
        if (intent is not ("language" or "translation" or "question")) throw new InvalidDataException("请明确选择语言、翻译或问答入口。");
        return configuration.Modes.FirstOrDefault(m => m.Definition.Enabled && m.Definition.OutputKind == intent)?.Definition.Id
            ?? throw new InvalidDataException("尚未启用对应模式：" + intent);
    }
}

public static class ExcerptModeLoader
{
    public const string RegistryFile = "excerpt-modes.json";
    public const string SchemaFile = "excerpt-card.schema.json";
    public static ModeSnapshot[] Load(string defaults, string? overrides = null)
    {
        string Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split('/', '\\').Any(p => p == ".."))
                throw new InvalidDataException("摘录模式文件必须位于本地 modes 目录内。");
            var custom = overrides == null ? null : Path.Combine(overrides, path);
            return File.ReadAllText(custom != null && File.Exists(custom) ? custom : Path.Combine(defaults, path));
        }
        var definitions = JsonSerializer.Deserialize<ModeFile>(Read(RegistryFile), WorkflowJson.Options)?.Modes
            ?? throw new InvalidDataException("摘录模式配置为空。");
        if (definitions.Any(m => string.IsNullOrWhiteSpace(m.Id) || m.Id == "excerpt-manual" || m.OutputKind != "excerpt") ||
            definitions.Select(m => m.Id).Distinct().Count() != definitions.Length)
            throw new InvalidDataException("摘录模式需使用独立、唯一的 id 和 excerpt 输出类型。");
        return definitions.Where(m => m.Enabled).Select(definition =>
        {
            string prompt = "", schema = ""; string? error = null;
            try
            {
                if (definition.AnkiDeckPath != null) DeckRouting.ValidatePattern(definition.AnkiDeckPath, "excerpt");
                prompt = Read(definition.PromptFile); schema = Read(SchemaFile);
                if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("提示词为空。");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { error = ex.Message; }
            return new ModeSnapshot(definition with { SchemaFile = SchemaFile, RequiresContext = false }, prompt, schema, error);
        }).ToArray();
    }
}

public sealed partial class ContentModule
{
    public async Task<ExcerptFields> ProcessExcerptAsync(CaptureMaterial original, ModeSnapshot mode, LanguageSettings settings, CancellationToken ct)
    {
        original.Validate();
        if (mode.Definition.OutputKind != "excerpt" || mode.LoadError != null)
            throw new InvalidDataException(mode.LoadError ?? "不是摘录模式。");
        var schema = await JsonSchema.FromJsonAsync(mode.Schema, ct);
        return await RequestAsync(settings, mode.Prompt + "\n\n每次返回一张卡，JSON 输出必须符合以下字段约定：\n" + mode.Schema,
            new { original.SelectedText, original.ContextText, original.SourceTitle }, json =>
            {
                var errors = schema.Validate(json);
                if (errors.Count != 0) throw new InvalidDataException("摘录输出字段不符合约定：" + string.Join("、", errors.Select(e => e.Path + ":" + e.Kind)));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var fields = new ExcerptFields(root.GetProperty("front").GetString()!, root.GetProperty("back").GetString()!,
                    root.GetProperty("cardType").GetString() switch { "basic" => ExcerptCardKind.Basic, "cloze" => ExcerptCardKind.Cloze,
                        _ => throw new InvalidDataException("卡片类型无效。") });
                ExcerptCards.Validate(fields);
                return Task.FromResult(fields);
            }, ct);
    }
}

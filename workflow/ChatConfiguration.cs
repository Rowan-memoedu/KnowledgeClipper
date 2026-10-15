using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

// Uses the same built-in / personal override convention as ModeConfigurationLoader.
public sealed class ChatConfigurationLoader(string defaults, string overrides)
{
    public string OverrideRoot => Path.GetFullPath(overrides);
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(p => p is ".." or "." or "")) throw new InvalidDataException("问答配置路径无效：" + relative);
        foreach (var root in new[] { overrides, defaults })
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            var current = new FileInfo(path) as FileSystemInfo;
            while (current != null && current.FullName.Length >= Path.GetFullPath(root).Length)
            {
                if (current.Exists && current.LinkTarget != null) throw new InvalidDataException("问答配置不能经过链接：" + relative);
                current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent;
            }
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("缺少问答配置：" + relative);
    }

    public ChatConfiguration Load()
    {
        try
        {
            var config = WorkflowJson.Read<ChatConfiguration>(Resolve("question-types.json"));
            if (config.Types == null || config.Types.Length == 0 || config.Types.Any(t => string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Label)) ||
                config.Types.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != config.Types.Length)
                throw new InvalidDataException("问题类型标识为空或重复。");
            foreach (var t in config.Types)
            {
                Resolve(t.PromptFile);
                foreach (var profile in t.Profiles ?? []) Resolve(profile);
            }
            return config;
        }
        catch (JsonException ex) { throw new InvalidDataException("question-types.json 无法解析：" + ex.Message, ex); }
    }

    public PromptSnapshot Freeze(ChatSession chat, string input, ImageAsset[] images, string? action = null)
    {
        var type = Load().Types.SingleOrDefault(t => t.Id == chat.TypeId) ?? throw new InvalidDataException("当前问题类型不存在：" + chat.TypeId);
        var files = new List<string> { "system.md", "profiles/common.md" };
        files.AddRange(type.Profiles ?? []); files.Add(type.PromptFile);
        if (chat.IncludeExamples) files.Add(type.ExamplesFile);
        var exampleImages = chat.IncludeExamples && type.ExampleAssetsFile != null
            ? WorkflowJson.Read<ImageAsset[]>(Resolve(type.ExampleAssetsFile)) : [];
        foreach (var image in exampleImages) image.ReadVerified();
        if (action != null)
        {
            if (action is not ("integrate" or "formalize" or "formalize-answer" or "formalize-all" or "formalize-annotation" or "transcribe")) throw new InvalidDataException("未知辅助动作。");
            if (action == "formalize-all") files.AddRange(["actions/formalize.md", "actions/formalize-answer.md"]);
            files.Add("actions/" + action + ".md");
        }
        var snapshot = new PromptSnapshot { Provider = chat.Binding.Provider, Model = chat.Model, Effort = chat.Effort, TypeId = type.Id,
            Action = action, Revision = chat.Revision, Input = input, Images = images, ExampleImages = exampleImages,
            Files = files.Distinct(StringComparer.Ordinal).Select(file =>
            {
                var path = Resolve(file); var content = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("问答规则为空：" + path);
                return new PromptFileSnapshot(file, path, content, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
            }).ToArray() };
        foreach (var example in snapshot.Files.Where(f => f.RelativePath.StartsWith("few-shots/", StringComparison.Ordinal)))
        {
            try { ChatMarkdown.ValidateAssets(example.Text, exampleImages); }
            catch (InvalidDataException ex) { throw new InvalidDataException("示例图片配置无效：" + example.SourcePath + "；" + ex.Message, ex); }
        }
        return snapshot;
    }

    public string Editable(string relative)
    {
        var source = Resolve(relative); var target = Path.Combine(OverrideRoot, relative);
        if (!File.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, false); }
        return target;
    }

    public QuestionTypeTarget Target(string id)
    {
        var config = Load(); var type = config.Types.Single(t => t.Id == id);
        return new(type.Id, type.Label, config.RootId, config.BaseId, type.DocumentId);
    }
}

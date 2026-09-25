namespace KnowledgeClipper.Workflow;

public static class LanguageModes
{
    public static bool IsConcrete(ModeDefinition definition) => definition.OutputKind == "language" &&
        definition.Language is "zh" or "en" && definition.Practice is PracticeChoice.Reading or PracticeChoice.Writing;

    public static bool HasConcrete(ContentConfiguration config) => config.Modes.Any(m => IsConcrete(m.Definition));

    public static ModeDefinition[] Select(ContentConfiguration config, CaptureMaterial material, PracticeChoice practice, string? selectedModeId = null)
    {
        if (material.FrontImage != null) practice = PracticeChoice.Reading;
        var explicitMode = config.Modes.FirstOrDefault(m => m.Definition.Id == selectedModeId)?.Definition;
        var language = explicitMode != null && IsConcrete(explicitMode) ? explicitMode.Language :
            material.SelectedText.Any(c => c is >= '\u3400' and <= '\u9fff') ? "zh" :
            material.SelectedText.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') ? "en" : null;
        if (language == null) throw new ModeChoiceRequiredException("请选择这次材料使用的具体语言模式。");
        PracticeChoice[] practices = practice == PracticeChoice.Both ? [PracticeChoice.Reading, PracticeChoice.Writing] : [practice];
        return practices.Select(choice =>
        {
            var matches = config.Modes.Where(m => IsConcrete(m.Definition) && m.Definition.Language == language && m.Definition.Practice == choice).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("请为本次语种和练习启用唯一的具体模式：" + language + " / " + choice);
            return matches[0].Definition;
        }).ToArray();
    }

    // The existing classifier selects a task family. Only concrete modes own processing prompts and results.
    public static ModeDefinition[] ClassificationChoices(ContentConfiguration config)
    {
        if (!HasConcrete(config)) return config.Modes.Select(m => m.Definition).ToArray();
        var language = new ModeDefinition("language", "语言学习", true,
            "普通汉语、英语和中英混合词句的学习任务。应用会按本次语种及练习选择执行具体模式；此标识仅用于路由，不生成内容。\n" +
                string.Join("\n", config.Modes.Where(m => IsConcrete(m.Definition)).Select(m => m.Definition.Name + "：" + m.Definition.Description)),
            "", "", "language");
        return new[] { language }.Concat(config.Modes.Where(m => !IsConcrete(m.Definition) && m.Definition.Id != "language").Select(m => m.Definition)).ToArray();
    }
}

using KnowledgeClipper.Workflow;

internal static class DeckRoutingChecks
{
    internal static void Run(ContentConfiguration config)
    {
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS deck routing: " + name); }
        ClipDraft Draft(string text, LanguageSettings? settings = null, ContentConfiguration? configuration = null)
        {
            var material = new CaptureMaterial(text, "中文说明：" + text, "source", 5, "fixture", 0, DateTimeOffset.UtcNow);
            return new() { Original = material, Material = material, Settings = settings ?? new() { AnkiRootDeck = "KnowledgeClipper" },
                Configuration = configuration ?? config, ModeId = "language", Practice = PracticeChoice.Both };
        }
        var english = Draft("lead");
        var routes = DeckRouting.Resolve(english, [0, 1]);
        Check(routes.CardDecks![0] == "KnowledgeClipper::语言学习::英语::理解与朗读" &&
            routes.CardDecks[1] == "KnowledgeClipper::语言学习::英语::书写练习", "English target in Chinese context has two native destinations");
        Check(DeckRouting.Resolve(Draft("恒心"), [1]).Deck == "KnowledgeClipper::语言学习::汉语::书写练习", "Chinese writing-only has no reading deck as initial target");
        Check(DeckRouting.Resolve(Draft("API 接口"), [0]).Deck.Contains("::中英混合::"), "mixed target stays separate");
        Check(DeckRouting.Resolve(Draft("こんにちは"), [0]).Deck.Contains("::其他语言::"), "unsupported script does not become English");
        var legacy = DeckRouting.Resolve(Draft("lead", new() { AnkiDeck = "旧牌组::原目标" }), [0, 1]);
        Check(legacy.Deck == "旧牌组::原目标" && legacy.CardDecks == null, "old saved draft keeps its exact deck");
        var changed = config with { Modes = config.Modes.Select(m => m.Definition.Id == "language" ? m with
            { Definition = m.Definition with { AnkiDeckPath = "自选::{格式}::{语种}::本周" } } : m).ToArray() };
        var custom = DeckRouting.Resolve(Draft("lead", new() { AnkiRootDeck = "我的学习::剪藏" }, changed), [1]);
        Check(custom.Deck == "我的学习::剪藏::自选::书写练习::英语::本周", "custom nested root and reordered placeholders are honored");
        foreach (var invalid in new[] { "", "::问答", "问答::", "问答::::课堂", "问答::{未知}", "问答\n课堂" })
        {
            var failed = false;
            try { DeckRouting.ValidatePattern(invalid, "question"); } catch (InvalidDataException) { failed = true; }
            Check(failed, "invalid path rejected: " + invalid.Replace("\n", "\\n"));
        }
        var mode = config.Modes.Single(m => m.Definition.Id == "language").Definition;
        var directory = Path.Combine(@"D:\Caches\KnowledgeClipper\deck-routing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WorkflowJson.Save(Path.Combine(directory, "modes.json"), new ModeFile([mode with { Description = "existing personal description", AnkiDeckPath = null }]));
        // LoadDefinitions needs only the definitions; keep this compatibility fixture independent of prompts.
        var defaults = Path.Combine(directory, "defaults");
        Directory.CreateDirectory(defaults);
        WorkflowJson.Save(Path.Combine(defaults, "modes.json"), new ModeFile(config.Modes.Select(m => m.Definition).ToArray()));
        var merged = ModeConfigurationLoader.LoadDefinitions(defaults, directory).Modes.Single(m => m.Id == mode.Id);
        Check(merged.AnkiDeckPath == mode.AnkiDeckPath && merged.Description == "existing personal description", "old personal override inherits new deck path without losing its edits");
    }
}

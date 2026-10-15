using KnowledgeClipper;
using KnowledgeClipper.Workflow;

internal static class DeckPreviewChecks
{
    private static T Find<T>(Form form, string suffix) where T : Control => (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();
    internal static async Task RunAsync(string root, string artifacts)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var store = new DraftStore(Path.Combine(artifacts, "deck-preview-state"));
        var configuration = ModeConfigurationLoader.Load(Path.Combine(root, "modes"));
        // This shared-result fixture represents a saved pre-concrete-mode draft and must keep that definition.
        configuration = configuration with { Modes = configuration.Modes.Where(m => m.Definition.OutputKind != "language").Prepend(new ModeSnapshot(
            new ModeDefinition("language", "旧语言模式", true, "", "language.md", "language.schema.json"), "", "")).ToArray() };
        ClipDraft Create(string mode, string text)
        {
            var material = new CaptureMaterial(text, null, "generated_allowed", null, "牌组预览 fixture", 0, DateTimeOffset.UtcNow);
            var result = mode == "language" ? new ContentResult(mode, Fields: new("pinyin", "meaning", text + "用在这句中。", 0)) :
                new ContentResult(mode, Basic: new(text, "内容预览"), Answer: mode == "question" ? new("answered", "内容预览", Array.Empty<string>()) : null);
            return new() { Original = material, Material = material, Settings = new() { AnkiRootDeck = "KnowledgeClipper", AnkiDeck = "旧的单一目标",
                ModeDestinations = new() { ["translation"] = ExportDestination.Anki, ["question"] = ExportDestination.Anki } },
                Configuration = configuration, ModeId = mode, Route = ModeConfigurationLoader.Route(configuration, mode), Practice = PracticeChoice.Both,
                Content = result, Conversation = result.Answer == null ? new() : new() { new(text, result.Answer) } };
        }
        var language = Create("language", "严谨");
        using (var preview = new LanguageDraftForm(language, store, () => throw new Exception("Deck preview must not import"), () => Task.CompletedTask, () => { }))
        {
            preview.Show(); await Task.Delay(60);
            var source = Find<Label>(preview, "source"); var practice = Find<ComboBox>(preview, "practice");
            Check(source.Text.Contains("KnowledgeClipper::语言学习::汉语") && source.Text.Contains(DeckRouting.Describe(language)), "language preview shows routed Han language targets under the configured root");
            var both = source.Text;
            practice.SelectedIndex = (int)PracticeChoice.Reading;
            Check(language.Practice == PracticeChoice.Reading && source.Text.Contains("\n导入：" + ExportRouting.Describe(language)) && source.Text != both,
                "reading-only practice immediately updates the displayed destination");
            var reading = source.Text;
            practice.SelectedIndex = (int)PracticeChoice.Writing;
            Check(language.Practice == PracticeChoice.Writing && source.Text.Contains("\n导入：" + ExportRouting.Describe(language)) && source.Text != reading,
                "writing-only practice immediately updates the displayed destination");
            var original = source.Text; var size = preview.Size;
            var opens = 0; preview.OpenChatRequested += () => opens++;
            Find<Button>(preview, "open-chat").PerformClick();
            Check(opens == 1 && source.Text == original && preview.Size == size,
                "current chat entry requests a separate conversation without changing the language target");
            Check(preview.Controls.Find("knowledge-clipper.language.follow-up-toggle", true).Length == 0,
                "retired follow-up view cannot override current preview routing");
            language.ConfirmedNote = new("旧确认目标", language.Settings.AnkiModel, new() { ["Target"] = language.Original.SelectedText }, Array.Empty<string>(), new[] { 0 });
            language.ImportState = ImportState.Unknown; preview.RefreshStatus();
            Check(source.Text.Contains("旧确认目标") && !source.Text.Contains("KnowledgeClipper::语言学习::") && !practice.Enabled,
                "frozen legacy card keeps its confirmed destination instead of the new root");
            Find<Button>(preview, "open-chat").PerformClick();
            Check(opens == 2 && source.Text.Contains("旧确认目标"), "opening chat preserves the original frozen target");
            preview.Close();
        }
        foreach (var mode in new[] { "translation", "question" })
        {
            var draft = Create(mode, mode == "question" ? "为什么？" : "循序渐进");
            using var preview = new LanguageDraftForm(draft, store, () => throw new Exception("Deck preview must not import"), () => Task.CompletedTask, () => { });
            preview.Show(); await Task.Delay(40);
            var source = Find<Label>(preview, "source");
            Check(source.Text.Contains(mode == "translation" ? "KnowledgeClipper::翻译::中译英" : "KnowledgeClipper::问答"), mode + " preview shows its independent child deck");
            if (mode == "translation")
            {
                var opens = 0; preview.OpenChatRequested += () => opens++;
                Find<Button>(preview, "open-chat").PerformClick();
                Check(opens == 1 && source.Text.Contains("KnowledgeClipper::翻译::中译英"), "translation chat entry preserves its independent import target");
            }
            preview.Close();
        }
        WorkflowJson.Save(Path.Combine(artifacts, "deck-preview-checks.json"), new { passed = true, checks, scope = "production preview routing labels and practice/view state; no content API or Anki import" });
    }
}

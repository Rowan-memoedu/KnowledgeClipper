using System.Reflection;
using KnowledgeClipper;
using KnowledgeClipper.Workflow;

internal static class MaintenancePreviewChecks
{
    private static T Find<T>(Form form, string suffix) where T : Control =>
        (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();
    private static Task Confirm(LanguageDraftForm form) =>
        (Task)typeof(LanguageDraftForm).GetMethod("ConfirmAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;

    internal static async Task RunAsync(string root, string artifacts)
    {
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        // Isolated UI configuration and saved import-state fixtures, not proof that production
        // prompt/schema files are available or that a real Anki import succeeded.
        var loaded = ModeConfigurationLoader.Load(Path.Combine(root, "modes"));
        var configuration = loaded with { Modes = loaded.Modes.Select(mode => mode with { LoadError = null }).ToArray() };
        var missingConfigPath = Path.Combine(artifacts, "missing-mode-config");
        Directory.CreateDirectory(missingConfigPath);
        WorkflowJson.Save(Path.Combine(missingConfigPath, "modes.json"), new ModeFile(loaded.Modes.Select(mode => mode.Definition).ToArray()));
        File.WriteAllText(Path.Combine(missingConfigPath, "classification.md"), loaded.ClassificationPrompt);
        // No mode prompt/schema is created here. The production loader supplies the real missing-file errors.
        var missingConfiguration = ModeConfigurationLoader.Load(missingConfigPath);
        var store = new DraftStore(Path.Combine(artifacts, "maintenance-preview-state"));
        ClipDraft Basic() {
            var material = new CaptureMaterial("测试原文", null, "generated_allowed", null, "maintenance UI fixture", 0, DateTimeOffset.UtcNow);
            return new() { Original = material, Material = material, Settings = new(), Configuration = configuration,
                ModeId = "translation", Route = new("translation", "translation", false, false), Content = new("translation", Basic: new(material.SelectedText, "Test translation.")) };
        }
        LanguageDraftForm Form(ClipDraft draft, Func<Task> importer = null) => new(draft, store, importer ?? (() => Task.CompletedTask), () => Task.CompletedTask, () => { });
        ClipDraft Modes() {
            var material = new CaptureMaterial("apple", null, "generated_allowed", null, "maintenance mode fixture", 0, DateTimeOffset.UtcNow);
            var definitions = configuration.Modes.Where(mode => mode.Definition.Language == "en" && mode.Definition.OutputKind == "language")
                .Select(mode => mode.Definition).ToArray();
            if (definitions.Length != 2) throw new Exception("Expected two configured English practice modes.");
            var parent = new ClipDraft { Original = material, Material = material, Settings = new(), Configuration = configuration,
                ModeId = definitions[0].Id, Route = new(definitions[0].Id, "language", false, true) };
            foreach (var child in LanguageModeCards.Ensure(parent, definitions, store)) {
                child.Content = new(child.ModeId, new("apple", "fruit", "apple in " + child.ModeId, 0)); store.Save(child);
            }
            return parent;
        }
        void ModeState(ClipDraft draft, string id, ImportState state) {
            var child = store.Find(draft.ModeCards[id]);
            child.ImportState = state; child.ImportError = null; store.Save(child);
        }
        ClipDraft Questions() {
            var draft = Basic(); draft.ModeId = "question"; draft.Route = new("question", "question", true, false);
            draft.Content = new("question", Answer: new("answered", "Answer one", []));
            draft.Conversation.Add(new(draft.Original.SelectedText, new("answered", "Answer one", [])));
            draft.Conversation.Add(new("Second question", new("answered", "Answer two", []))); store.Save(draft); return draft;
        }
        void QuestionState(ClipDraft parent, int index, ImportState state, string error = null) {
            var row = QuestionCards.Preview(parent, store).Single(r => r.Index == index);
            var material = new CaptureMaterial(row.Question, null, "generated_allowed", null, parent.Material.SourceTitle, 0, DateTimeOffset.UtcNow);
            var child = new ClipDraft { Original = material, Material = material, Settings = parent.Settings, Configuration = configuration,
                ParentDraftId = parent.Id, ModeId = "question", Route = new("question", "question", true, false),
                Content = new("question", Basic: new(row.Question, row.Answer, row.Context), Answer: new("answered", row.Answer, [])) };
            child.ConfirmedNote = AnkiNoteDraft.From(child); child.ImportState = state; child.ImportError = error;
            parent.FollowUpCards[index] = child.Id; store.Save(child); store.Save(parent);
        }

        {
            var draft = Basic(); draft.Content = null; draft.ModeId = null; draft.Route = null;
            using var preview = Form(draft); preview.Show(); preview.ContentRunning = true;
            Find<ComboBox>(preview, "practice").SelectedIndex = (int)PracticeChoice.Reading;
            Check(draft.Practice == PracticeChoice.Reading && store.Find(draft.Id).Practice == PracticeChoice.Reading,
                "practice selection before classification finishes is saved for the upcoming processors");
            preview.Close();
        }
        var sentenceDraft = Modes();
        using (var preview = Form(sentenceDraft)) {
            var spoken = new List<string>(); preview.PlaySentence += text => { spoken.Add(text); return Task.CompletedTask; };
            preview.Show(); preview.RefreshStatus();
            var play = Find<Button>(preview, "play-sentence"); var editor = Find<TextBox>(preview, "context");
            Check(play.Visible && play.Enabled && play.Parent.Controls.OfType<Label>().Any(label => label.Text == "句子"), "sentence play sits beside its label");
            editor.Text = "apple in an edited sentence"; play.PerformClick();
            Check(spoken.SequenceEqual(new[] { "apple in an edited sentence" }) && sentenceDraft.Original.SelectedText == "apple", "sentence play reads edited text and retains original selection");
            Find<ComboBox>(preview, "mode-preview").SelectedIndex = 1;
            var second = editor.Text; play.PerformClick();
            Check(spoken.Count == 2 && spoken[1] == second && second != spoken[0], "sentence play follows the active mode");
            preview.Close();
        }
        using (var preview = Form(Basic())) { preview.Show(); Check(!Find<Button>(preview, "play-sentence").Visible, "translation hides sentence play"); preview.Close(); }
        using (var preview = Form(Questions())) { preview.Show(); Check(!Find<Button>(preview, "play-sentence").Visible, "question view hides sentence play"); preview.Close(); }

        foreach (var state in new[] { ImportState.Ready, ImportState.Unknown, ImportState.Failed, ImportState.Verified }) {
            var draft = Basic();
            using var preview = Form(draft, () => { draft.ImportState = state; return Task.CompletedTask; });
            preview.Show(); await Confirm(preview);
            Check(preview.IsDisposed == (state == ImportState.Verified), "legacy footer close requires Verified: " + state);
            if (!preview.IsDisposed) preview.Close();
        }
        {
            var draft = Basic(); using var preview = Form(draft, () => { draft.ImportState = ImportState.Verified; throw new Exception("post-write uncertainty"); });
            preview.Show(); await Confirm(preview); Check(!preview.IsDisposed, "legacy importer exception retains window even with Verified state"); preview.Close();
        }
        foreach (var scenario in new[] { "all", "legacy-empty-selection", "pending", "missing-configuration", "unknown", "selection-changed", "binding-missing", "background-work", "import-error" }) {
            var draft = Modes(); var ids = draft.SelectedModeIds.ToArray();
            if (scenario == "legacy-empty-selection") { draft.SelectedModeIds = []; store.Save(draft); }
            if (scenario == "pending") { var child = store.Find(draft.ModeCards[ids[1]]); child.Content = null; child.ContentError = "processor unavailable"; store.Save(child); }
            if (scenario == "missing-configuration") {
                var failed = missingConfiguration.Modes.FirstOrDefault(mode => ids.Contains(mode.Definition.Id) && mode.LoadError != null);
                if (failed == null) throw new Exception("The metadata-only missing-mode-config fixture unexpectedly contains available processors.");
                var child = store.Find(draft.ModeCards[failed.Definition.Id]);
                child.Configuration = missingConfiguration; child.Content = null; child.ContentError = failed.LoadError; store.Save(child);
            }
            using var preview = Form(draft);
            preview.ImportLanguageModes += selected => {
                foreach (var id in selected) ModeState(draft, id, ImportState.Verified);
                if (scenario is "unknown" or "selection-changed") ModeState(draft, ids[1], ImportState.Unknown);
                if (scenario == "selection-changed") { draft.SelectedModeIds = [ids[0]]; store.Save(draft); }
                if (scenario == "binding-missing") { draft.ModeCards[ids[1]] = Guid.NewGuid().ToString("N"); store.Save(draft); }
                if (scenario == "import-error") throw new Exception("uncertain batch result");
                return Task.CompletedTask;
            };
            preview.Show(); if (scenario == "background-work") preview.ContentRunning = true;
            await Confirm(preview);
            Check(preview.IsDisposed == (scenario is "all" or "legacy-empty-selection"), "mode footer frozen selection close: " + scenario);
            if (!preview.IsDisposed) preview.Close();
        }
        foreach (var scenario in new[] { "verified", "unknown", "failed", "pending-sibling", "busy-sibling" }) {
            var draft = Modes(); var ids = draft.SelectedModeIds.ToArray();
            if (scenario is "pending-sibling" or "busy-sibling") {
                var pending = store.Find(draft.ModeCards[ids[1]]); pending.Content = null; pending.ContentError = "not yet processed"; store.Save(pending);
            }
            using var preview = Form(draft);
            preview.ImportLanguageModes += selected => {
                foreach (var id in selected) ModeState(draft, id,
                    scenario == "unknown" ? ImportState.Unknown : scenario == "failed" ? ImportState.Failed : ImportState.Verified);
                return Task.CompletedTask;
            };
            preview.Show();
            if (scenario == "busy-sibling") preview.ContentRunning = true;
            Find<Button>(preview, "import-current-mode").PerformClick(); await Task.Yield();
            if (scenario == "busy-sibling") {
                Check(!preview.IsDisposed, "current-mode import waits for running sibling before closing");
                preview.ContentRunning = false; await Task.Delay(30);
            }
            Check(preview.IsDisposed == (scenario is "verified" or "pending-sibling" or "busy-sibling"),
                "current-mode successful import closes and preserves incomplete siblings: " + scenario);
            Check(store.Find(draft.ModeCards[ids[1]]) != null && store.Find(draft.Id).ModeCards.Count == 2,
                "unimported sibling remains recoverable: " + scenario);
            if (!preview.IsDisposed) preview.Close();
        }
        foreach (var scenario in new[] { "all", "unknown", "selection-changed", "error", "local" }) {
            var draft = Questions(); using var preview = Form(draft);
            preview.ImportQuestionCards += selected => {
                foreach (var index in selected) QuestionState(draft, index,
                    index == 1 && scenario is "unknown" or "selection-changed" ? ImportState.Unknown : ImportState.Verified,
                    scenario == "error" ? "pending verification" : null);
                if (scenario == "selection-changed") Find<CheckBox>(preview, "question-card-1.selected").Checked = false;
                return Task.CompletedTask;
            };
            preview.Show();
            if (scenario == "local") { Find<Button>(preview, "question-card-0.import").PerformClick(); await Task.Yield(); }
            else await Confirm(preview);
            Check(preview.IsDisposed == (scenario == "all"), "question footer captured indexes and local boundary: " + scenario);
            if (!preview.IsDisposed) preview.Close();
        }
        WorkflowJson.Save(Path.Combine(artifacts, "maintenance-preview-checks.json"), new { passed = true, checks,
            scope = "Production WinForms with isolated ready-configuration and saved import-state fixtures; a separate metadata-only missing-mode-config directory obtains actual missing-file LoadError from the production loader, independently of production prompt availability. This does not validate production prompts/schemas or real Anki/content/speech requests." });
    }
}

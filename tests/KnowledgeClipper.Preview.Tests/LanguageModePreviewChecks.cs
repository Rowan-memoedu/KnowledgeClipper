using KnowledgeClipper;
using KnowledgeClipper.Workflow;

internal static class LanguageModePreviewChecks
{
    private static T Find<T>(Form form, string suffix) where T : Control =>
        (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();

    internal static async Task RunAsync(string root, string artifacts)
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var configuration = ModeConfigurationLoader.Load(Path.Combine(root, "modes"));
        // Saved legacy results retain their original card semantics.
        configuration = configuration with { Modes = configuration.Modes.Select(mode => mode with { Definition = mode.Definition with { ContentFormat = null } }).ToArray() };
        var store = new DraftStore(Path.Combine(artifacts, "language-mode-preview-state"));
        var speechCalls = 0;
        ModeDefinition[] Modes(string language, PracticeChoice practice = PracticeChoice.Both) => configuration.Modes.Select(item => item.Definition)
            .Where(mode => mode.OutputKind == "language" && mode.Language == language &&
                (practice == PracticeChoice.Both || mode.Practice == practice)).OrderBy(mode => mode.Practice).ToArray();
        ContentResult Result(ClipDraft child) => new(child.ModeId, new(child.ModeId + " pronunciation", child.ModeId + " independent meaning",
            child.Material.SelectedText + " · " + child.ModeId + " independent sentence", 0));
        ClipDraft Session(string language, PracticeChoice practice = PracticeChoice.Both)
        {
            var material = new CaptureMaterial(language == "zh" ? "严谨" : "resilient", null, "generated_allowed", null,
                "独立模式 UI fixture", 0, DateTimeOffset.UtcNow);
            var modes = Modes(language, practice);
            var parent = new ClipDraft { Original = material, Material = material, Settings = new() { AnkiRootDeck = "KnowledgeClipper UI fixture" },
                Configuration = configuration, ModeId = modes[0].Id, Route = new(modes[0].Id, "language", false, true),
                Practice = practice, Input = new(ReadRequested: true), SpeechStatus = "本次朗读状态保持在父会话" };
            foreach (var child in LanguageModeCards.Ensure(parent, modes, store)) { child.Content = Result(child); store.Save(child); }
            return parent;
        }
        LanguageDraftForm Form(ClipDraft parent) => new(parent, store,
            () => throw new Exception("Mode sessions must delegate mode IDs instead of the legacy importer."),
            () => { speechCalls++; return Task.CompletedTask; }, () => { speechCalls++; });
        ClipDraft Child(ClipDraft parent, PracticeChoice practice) => LanguageModeCards.Children(parent, store).Single(child => child.Practice == practice);
        void SaveState(ClipDraft parent, string[] ids, ImportState state)
        {
            foreach (var id in ids)
            {
                var child = store.Find(parent.ModeCards[id]);
                if (child.ConfirmedNote == null)
                {
                    child.Content = child.PreviewContent; child.EditedFields = null;
                    child.ConfirmedNote = AnkiNoteDraft.From(child);
                }
                child.ImportState = state;
                child.NoteId = state == ImportState.Verified ? 10 : null;
                child.CardIds = state == ImportState.Verified ? new long[] { 20 } : Array.Empty<long>();
                store.Save(child);
            }
        }

        var parent = Session("zh");
        parent.Conversation.Add(new("怎样运用这个词？", new("answered", "已有的父会话回答", Array.Empty<string>()))); store.Save(parent);
        using (var preview = Form(parent))
        {
            preview.Show(); preview.Size = preview.MinimumSize; await Task.Delay(80);
            var size = preview.Size;
            var selector = Find<ComboBox>(preview, "mode-preview"); var meaning = Find<TextBox>(preview, "meaning");
            var practice = Find<ComboBox>(preview, "practice"); var tabs = Find<TabControl>(preview, "faces");
            var confirm = Find<Button>(preview, "confirm"); var single = Find<Button>(preview, "import-current-mode");
            var readingId = Child(parent, PracticeChoice.Reading).ModeId; var writingId = Child(parent, PracticeChoice.Writing).ModeId;
            Check(ReferenceEquals(preview.Draft, parent) && parent.Content == null && selector.Items.Count == 2 && confirm.Enabled &&
                confirm.Text.Contains("2 个已就绪模式"), "parent with no shared content previews two independent child results");
            Check(meaning.Text.Contains(readingId) && tabs.TabPages.Count == 1 && tabs.TabPages[0].Text.Contains("理解与朗读"),
                "reading child shows its own result and only its fixed reading face");
            Check(Find<Label>(preview, "mode-status").Text.Contains("汉语 · 理解与朗读") &&
                Find<Label>(preview, "mode-status").Text.Contains("汉语 · 书写练习") && Find<Label>(preview, "mode-status").Text.Contains("::书写练习"),
                "both child states and separate routed decks remain visible in the mode summary");
            meaning.Text = "阅读模式的独立编辑"; meaning.Select(2, 3); preview.ContentReady(); preview.RefreshStatus();
            Check(meaning.SelectionStart == 2 && meaning.SelectionLength == 3 && store.Find(parent.ModeCards[readingId]).EditedFields.Meaning == meaning.Text,
                "child edits persist and status callbacks preserve the current text selection");
            selector.SelectedIndex = 1;
            Check(meaning.Text.Contains(writingId) && tabs.TabPages.Count == 1 && tabs.TabPages[0].Text.Contains("书写") &&
                Find<Label>(preview, "source").Text.Contains(DeckRouting.Describe(Child(parent, PracticeChoice.Writing))),
                "switching mode loads its independent writing content and destination");
            meaning.Text = "书写模式的独立编辑";
            selector.SelectedIndex = 0;
            Check(meaning.Text == "阅读模式的独立编辑" && Child(parent, PracticeChoice.Writing).EditedFields.Meaning == "书写模式的独立编辑" &&
                parent.EditedFields == null && parent.Content == null,
                "switching back retains both separate edits without writing content into the parent");
            var toggle = Find<Button>(preview, "follow-up-toggle"); toggle.PerformClick();
            var input = Find<TextBox>(preview, "follow-up-input"); input.Text = "继续说明"; input.Select(1, 2);
            Check(Find<TableLayoutPanel>(preview, "question-cards").Controls.Count == 1 && !selector.Visible && !single.Visible &&
                Find<Label>(preview, "source").Text.Contains(parent.Original.SelectedText),
                "follow-up view keeps one parent conversation and the captured material while hiding original mode actions");
            toggle.PerformClick(); selector.SelectedIndex = 1; toggle.PerformClick();
            Check(ReferenceEquals(input, Find<TextBox>(preview, "follow-up-input")) && input.Text == "继续说明" && input.SelectionStart == 1 &&
                parent.Conversation.Count == 1 && store.Find(parent.Id).PendingFollowUp == input.Text,
                "mode switching preserves the same follow-up input and ordered parent history");
            toggle.PerformClick();
            preview.LanguageImportRunning = true;
            Check(!confirm.Enabled && !single.Enabled && !practice.Enabled && meaning.ReadOnly,
                "Flow language import state locks conflicting edits and both import actions");
            preview.LanguageImportRunning = false;

            var calls = new List<string[]>(); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            preview.ImportLanguageModes += async ids => {
                calls.Add(ids); SaveState(parent, ids, ImportState.Ready); preview.RefreshStatus();
                if (calls.Count == 1) await release.Task;
                SaveState(parent, ids, ImportState.Verified); preview.RefreshStatus();
            };
            // Successful single-mode import closes the preview once background processing is idle.
            // Keep a sibling request active while verifying that its ready result remains importable.
            preview.ContentRunning = true;
            single.PerformClick(); single.PerformClick(); confirm.PerformClick();
            Check(calls.Count == 1 && calls[0].SequenceEqual(new[] { writingId }) && meaning.ReadOnly && !practice.Enabled,
                "current-mode confirmation emits only that mode once and respects its frozen state");
            release.SetResult(true); await Task.Delay(70);
            Check(!single.Enabled && confirm.Text.Contains("1 个已就绪模式") && Child(parent, PracticeChoice.Writing).ConfirmedNote.Ordinals.SequenceEqual(new[] { 1 }),
                "verified writing child cannot repeat and the bottom action scopes the remaining reading child");
            selector.SelectedIndex = 0; preview.ContentRunning = true;
            Check(confirm.Enabled && single.Enabled && !practice.Enabled,
                "an available child can import even while the parent reports other content processing");
            confirm.PerformClick(); await Task.Delay(60); confirm.PerformClick(); single.PerformClick();
            Check(calls.Count == 2 && calls[1].SequenceEqual(new[] { readingId }) && !confirm.Enabled &&
                parent.ConfirmedNote == null && parent.ModeCards.Count == 2 && Child(parent, PracticeChoice.Reading).ConfirmedNote.Ordinals.SequenceEqual(new[] { 0 }),
                "remaining-mode confirmation does not write a shared parent note or duplicate a completed mode");
            var host = Find<Panel>(preview, "content-scroll");
            var bounds = preview.RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle));
            var currentBounds = preview.RectangleToClient(single.RectangleToScreen(single.ClientRectangle));
            Check(preview.DeviceDpi == 192 && preview.Size == size && preview.ClientRectangle.Contains(bounds) &&
                preview.ClientRectangle.Contains(currentBounds) && !host.Contains(confirm) && host.VerticalScroll.Visible,
                "192 DPI minimum preview keeps both footer actions fully visible without scrolling them into view");
            preview.ContentRunning = false; await Task.Delay(40);
            Check(preview.IsDisposed, "completed imports close after the remaining background processing is saved");
        }

        var english = Session("en");
        using (var preview = Form(english))
        {
            preview.Show(); await Task.Delay(50); var calls = new List<string[]>();
            preview.ImportLanguageModes += ids => { calls.Add(ids); SaveState(english, ids, ImportState.Verified); return Task.CompletedTask; };
            Find<Button>(preview, "confirm").PerformClick(); await Task.Delay(40);
            Check(calls.Count == 1 && calls[0].SequenceEqual(english.SelectedModeIds) && calls[0].Length == 2 &&
                LanguageModeCards.Children(english, store).All(child => child.ConfirmedNote.Ordinals.Length == 1),
                "Both confirms the two independent English mode IDs in one event with one card each"); preview.Close();
        }

        var partial = Session("zh"); var failed = Child(partial, PracticeChoice.Writing);
        failed.Content = null; failed.ContentError = "writing fixture failure"; store.Save(failed);
        using (var preview = Form(partial))
        {
            preview.Show(); preview.ContentRunning = true; await Task.Delay(50);
            var selector = Find<ComboBox>(preview, "mode-preview"); var confirm = Find<Button>(preview, "confirm"); selector.SelectedIndex = 1;
            Check(Find<TextBox>(preview, "meaning").Text == "" && Find<TextBox>(preview, "meaning").ReadOnly &&
                Find<Label>(preview, "status").Text.Contains(failed.ContentError) && confirm.Enabled && confirm.Text.Contains("1 个已就绪模式"),
                "failed active child shows its own error without hiding or disabling the successful sibling");
            failed.Content = Result(failed); failed.ContentError = null; store.Save(failed); preview.ContentReady();
            Check(Find<TextBox>(preview, "meaning").Text.Contains(failed.ModeId) && confirm.Text.Contains("2 个已就绪模式") && partial.Content == null,
                "late independent completion fills the active child without requiring parent content");
            preview.ContentRunning = false;
            SaveState(partial, new[] { failed.ModeId }, ImportState.Unknown); preview.RefreshStatus();
            var frozen = store.Find(failed.Id); var deck = DeckRouting.Describe(frozen); var calls = new List<string[]>();
            preview.ImportLanguageModes += ids => { calls.Add(ids); return Task.CompletedTask; };
            var single = Find<Button>(preview, "import-current-mode");
            Check(single.Text.Contains("核对") && Find<TextBox>(preview, "meaning").ReadOnly &&
                Find<Label>(preview, "source").Text.Contains(deck), "unknown child is read-only and displays its frozen destination with a reconciliation action");
            partial.ImportError = "parent import fixture error"; preview.RefreshStatus();
            Check(Find<Label>(preview, "status").Text.Contains(partial.ImportError), "parent import failure is visible alongside independent child states");
            single.PerformClick(); await Task.Delay(40);
            Check(calls.Count == 1 && calls[0].SequenceEqual(new[] { failed.ModeId }) && store.Find(failed.Id).ImportState == ImportState.Unknown,
                "unknown reconciliation delegates the original mode identity without inventing a verified result"); preview.Close();
        }

        var preference = Session("en", PracticeChoice.Reading);
        using (var preview = Form(preference))
        {
            preview.Show(); await Task.Delay(40); var calls = 0;
            var reading = Child(preference, PracticeChoice.Reading); var content = reading.Content;
            Find<TextBox>(preview, "meaning").Text = "保留阅读编辑";
            preview.RetryContent += () => { calls++; LanguageModeCards.Ensure(preference, Modes("en", preference.Practice), store); preview.ContentReady(); };
            var practice = Find<ComboBox>(preview, "practice"); practice.SelectedIndex = (int)PracticeChoice.Both;
            Check(calls == 1 && preference.SelectedModeIds.Length == 2 && Child(preference, PracticeChoice.Writing).Content == null &&
                Child(preference, PracticeChoice.Reading).Content == content && Child(preference, PracticeChoice.Reading).EditedFields.Meaning == "保留阅读编辑",
                "changing Reading to Both requests missing content and never clones the existing result");
            practice.SelectedIndex = (int)PracticeChoice.Reading;
            Check(calls == 2 && preference.ModeCards.Count == 2 && preference.SelectedModeIds.Length == 1 &&
                Find<ComboBox>(preview, "mode-preview").Items.Count == 1 && store.Find(reading.Id).Practice == PracticeChoice.Reading,
                "changing the parent selection preserves stored siblings and each child's fixed practice"); preview.Close();
        }

        var original = new CaptureMaterial("正方形的四个角都是直角。", null, "generated_allowed", null, "显式问答来源", 0, DateTimeOffset.UtcNow);
        var question = new ClipDraft { Original = original, Material = original, Configuration = configuration, Settings = new(),
            ModeId = "question", Route = new("question", "question", true, false), Input = new(Intent: "question", ExplicitQuestion: "对角线为什么相等？"),
            Content = new("question", Basic: new("对角线为什么相等？", "因为相应直角三角形全等。"), Answer: new("answered", "因为相应直角三角形全等。", [])) };
        question.Conversation.Add(new(question.QuestionText, question.Content.Answer)); store.Save(question);
        using (var preview = Form(question))
        {
            preview.Show(); await Task.Delay(40);
            Check(Find<TextBox>(preview, "question-card-0.question").Text == question.QuestionText && preview.Text.Contains(question.QuestionText) &&
                Find<Label>(preview, "source").Text.Contains(original.SelectedText) && question.Original == original,
                "explicit question is the question front and title while captured source remains separate"); preview.Close();
        }

        var legacyMode = new ModeDefinition("language", "旧语言模式", true, "", "", "");
        var legacy = new ClipDraft { Original = original, Material = original, Settings = new(),
            Configuration = configuration with { Modes = configuration.Modes.Append(new ModeSnapshot(legacyMode, "", "")).ToArray() },
            ModeId = "language", Route = new("language", "language", false, true), Practice = PracticeChoice.Both,
            Content = new("language", new("旧读音", "旧释义", original.SelectedText, 0)) };
        using (var preview = new LanguageDraftForm(legacy, store, () => Task.CompletedTask, () => Task.CompletedTask, () => { }))
        {
            preview.Show(); await Task.Delay(40); Find<ComboBox>(preview, "practice").SelectedIndex = (int)PracticeChoice.Writing;
            Check(!Find<ComboBox>(preview, "mode-preview").Visible && Find<TabControl>(preview, "faces").TabPages.Count == 2 &&
                legacy.Practice == PracticeChoice.Writing && legacy.EditedFields != null && legacy.ModeCards.Count == 0,
                "legacy shared-result draft retains its original editor and practice behavior"); preview.Close();
        }
        var translation = new ClipDraft { Original = original, Material = original, Settings = new(), Configuration = configuration,
            ModeId = "translation", Route = new("translation", "translation", false, false), Content = new("translation", Basic: new(original.SelectedText, "A square has four right angles.")) };
        using (var preview = new LanguageDraftForm(translation, store, () => Task.CompletedTask, () => Task.CompletedTask, () => { }))
        {
            preview.Show(); await Task.Delay(40); Find<TextBox>(preview, "basic-back").Text = "Edited translation";
            Check(Find<TableLayoutPanel>(preview, "basic-fields").Visible && Find<TextBox>(preview, "basic-front").ReadOnly &&
                translation.EditedBasic.Back == "Edited translation" && !Find<ComboBox>(preview, "mode-preview").Visible,
                "translation stays on the existing single Basic card path"); preview.Close();
        }
        Check(speechCalls == 0, "preview switches and imports never request extra speech");
        WorkflowJson.Save(Path.Combine(artifacts, "language-mode-preview-checks.json"), new { passed = true, checks,
            scope = "real production WinForms preview, saved local child states and semantic controls; no input simulation, API, speech or Anki calls" });
    }
}

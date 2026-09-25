using KnowledgeClipper;
using KnowledgeClipper.Workflow;

internal static class PublishedPreviewChecks
{
    private static T Find<T>(Form form, string suffix) where T : Control =>
        (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();

    internal static async Task RunAsync(string root, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var config = ModeConfigurationLoader.Load(Path.Combine(root, "modes"));
        var store = new DraftStore(Path.Combine(output, "published-ui-state"));
        LanguageDraftForm Form(ClipDraft parent) => new(parent, store, () => throw new Exception("Unexpected import"), () => Task.CompletedTask, () => { });
        ClipDraft Child(ClipDraft parent, PracticeChoice practice) => LanguageModeCards.Children(parent, store).Single(c => c.Practice == practice);
        foreach (var language in new[] { "en", "zh" })
        {
            var target = language == "en" ? "lead" : "重";
            var sentence = language == "en" ? "😀 They lead,\r\nthen lead." : "😀 他重提此事，\r\n负担很重。";
            static string Display(string text) => text.Replace("\r\n", "\n");
            var material = new CaptureMaterial(target, sentence, "source", sentence.LastIndexOf(target, StringComparison.Ordinal), "Published UI fixture", 0, DateTimeOffset.UtcNow);
            var parent = new ClipDraft { Original = material, Material = material, Configuration = config,
                Settings = new LanguageSettings { ShowDictionaryInPreview = language == "zh" },
                ModeId = "language", Route = new("language", "language", false, true), Practice = PracticeChoice.Both };
            foreach (var child in LanguageModeCards.Ensure(parent, LanguageModes.Select(config, material, PracticeChoice.Both), store))
            {
                child.Content = new(child.ModeId, new(language == "en" ? "/lɛd/" : "zhòng", language == "zh" ? "分量大" : null, null, null)
                { Format = PublishedLanguage.Format, Status = "ready", ExplanationEn = language == "en" ? child.Practice == PracticeChoice.Reading ? "a heavy metal" : "a dense metal used in pipes" : null,
                    SentenceTranslationZh = language == "en" ? "整句中文译文" : null, Lemma = language == "en" && child.Practice == PracticeChoice.Reading ? "lead" : null });
                child.DictionaryDetails = new(target, language, "fixture translation", "noun", "fixture definition", "fixture example", "ready");
                store.Save(child);
            }
            using (var form = Form(parent))
            {
                form.Show(); await Task.Delay(60);
                var generationRequests = 0;
                form.GenerateExampleRequested += () => generationRequests++;
                form.ContentRunning = true;
                var generatedButton = Find<Button>(form, "generate-example");
                Check(generatedButton.Visible && generatedButton.Enabled && generatedButton.Text == "用生成例句制卡", language + " generated option remains available while original content runs");
                generatedButton.PerformClick();
                Check(generationRequests == 1 && parent.Material.ContextText == sentence, language + " generated option invokes a separate action without altering source");
                form.ContentRunning = false;
                var selector = Find<ComboBox>(form, "mode-preview"); selector.SelectedIndex = 0;
                Check(Find<TableLayoutPanel>(form, "dictionary-panel").Visible == (language == "zh") &&
                    (language != "zh" || Find<TextBox>(form, "dictionary-preview").Text.Contains("fixture translation")) &&
                    Child(parent, PracticeChoice.Reading).DictionaryDetails != null,
                    language + " setting controls only the separate preview panel, not saved dictionary data");
                var reading = Find<RichTextBox>(form, "reading-preview");
                var expectedBack = language == "en" ? "/lɛd/\na heavy metal\n整句中文译文" : "重\nzhòng\n分量大";
                Check(reading.Text == "正面\n" + Display(sentence) + "\n\n反面\n" + expectedBack, language + " reading shows exact published front and rows");
                reading.Select(3 + Display(sentence[..material.TargetStart.Value]).Length, target.Length);
                Check(reading.SelectedText == target && reading.SelectionBackColor == Color.LightBlue, language + " highlights only selected source occurrence");
                reading.Select(3 + sentence.IndexOf(target, StringComparison.Ordinal), target.Length);
                Check(reading.SelectionBackColor == Color.White, language + " leaves other matching occurrence unhighlighted");
                Check(Find<TextBox>(form, "sentence-translation").Visible == (language == "en") && Find<TextBox>(form, "lemma").Visible == (language == "en"),
                    language + " editors match published reading fields");
                if (language == "en")
                {
                    Find<TextBox>(form, "meaning").Text = "a soft metal";
                    Find<TextBox>(form, "sentence-translation").Text = "修改后的整句译文";
                    Find<TextBox>(form, "lemma").Text = "lead";
                    Check(Child(parent, PracticeChoice.Reading).PreviewContent.Fields.ExplanationEn == "a soft metal" &&
                        Child(parent, PracticeChoice.Reading).PreviewContent.Fields.SentenceTranslationZh == "修改后的整句译文" &&
                        Child(parent, PracticeChoice.Writing).PreviewContent.Fields.ExplanationEn == "a dense metal used in pipes",
                        "English explanation and translation edits persist independently per mode");
                }
                selector.SelectedIndex = 1;
                var writing = Find<RichTextBox>(form, "writing-preview");
                var before = sentence[..material.TargetStart.Value]; var after = sentence[(material.TargetStart.Value + target.Length)..];
                var front = before + (language == "en" ? "/lɛd/" : "（zhòng；分量大）") + after + (language == "en" ? "\na dense metal used in pipes" : "");
                var back = target + (language == "en" ? "\n整句中文译文" : "");
                Check(writing.Text.StartsWith("正面\n" + Display(front) + "\n\n反面\n" + back + "\n\n注意：") && !Find<TextBox>(form, "lemma").Visible,
                    language + " writing uses required rows and warns about remaining answer without adding lemma");
                var scrollParent = writing.Parent;
                while (scrollParent is not ScrollableControl { AutoScroll: true }) scrollParent = scrollParent.Parent;
                ((ScrollableControl)scrollParent).ScrollControlIntoView(writing);
                using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(output, "published-ui-" + language + ".png"));
                var writingChild = Child(parent, PracticeChoice.Writing);
                writingChild.Content = new(writingChild.ModeId, new(null, null, null, null) { Format = PublishedLanguage.Format, Status = "unresolved", UnresolvedReasons = ["当前语境不足以确定读音。"] });
                writingChild.EditedFields = null; store.Save(writingChild); form.ContentReady();
                Check(writing.Text == "内容待处理：当前语境不足以确定读音。" && Find<TextBox>(form, "meaning").ReadOnly &&
                    !Find<Button>(form, "import-current-mode").Enabled && Find<Button>(form, "confirm").Text.Contains("1 个已就绪模式"),
                    language + " unresolved state hides normal face and blocks current import while keeping ready sibling");
                var retries = 0; form.RetryContent += () => retries++;
                Find<Button>(form, "retry-content").PerformClick();
                Check(retries == 1 && Child(parent, PracticeChoice.Reading).Content.Fields.Status == "ready", language + " retry remains available without changing successful result");
                form.Close();
            }
            using (var reopened = Form(store.Find(parent.Id)))
            {
                reopened.Show(); await Task.Delay(30);
                Check(Find<TextBox>(reopened, "meaning").Text == (language == "en" ? "a soft metal" : "分量大") &&
                    (language != "en" || Find<TextBox>(reopened, "lemma").Text == "lead"), language + " reopened draft retains edited fields including hidden lemma");
                reopened.Close();
            }
        }
        var mixed = new CaptureMaterial("teamwork", "这项任务需要 teamwork，任何人都无法独自完成。", "source", 7, "fixture", 0, DateTimeOffset.UtcNow);
        var mixedParent = new ClipDraft { Original = mixed, Material = mixed, Configuration = config, Settings = new(),
            ModeId = "language", Route = new("language", "language", false, true), Practice = PracticeChoice.Both };
        foreach (var child in LanguageModeCards.Ensure(mixedParent, LanguageModes.Select(config, mixed, PracticeChoice.Both), store))
        {
            // Explicit English modes: language routing is outside the normalization test.
            child.Content = new(child.ModeId, new("/ˈtiːmwɜːrk/", null, null, null) { Format = PublishedLanguage.Format, Status = "ready",
                NormalizedSentence = "This task requires teamwork; no one can complete it alone.", ExplanationEn = "Cooperation toward a shared goal.",
                SentenceTranslationZh = "这项任务需要团队合作，任何人都无法独自完成。", Lemma = child.Practice == PracticeChoice.Reading ? "teamwork" : null });
            store.Save(child);
        }
        using (var form = Form(mixedParent))
        {
            form.Show(); await Task.Delay(30);
            Find<ComboBox>(form, "mode-preview").SelectedIndex = 1;
            Check(Find<RichTextBox>(form, "writing-preview").Text.Contains("This task requires /ˈtiːmwɜːrk/; no one can complete it alone."), "converted English preview uses new target position");
            Find<TextBox>(form, "meaning").Text = "Joint effort toward a shared goal.";
            Check(Child(mixedParent, PracticeChoice.Writing).PreviewContent.Fields.NormalizedSentence == "This task requires teamwork; no one can complete it alone." &&
                Child(mixedParent, PracticeChoice.Writing).Material == mixed, "editing clue preserves normalized example and original source");
            form.Close();
        }
        WorkflowJson.Save(Path.Combine(output, "published-ui.json"), new { outcome = "PASS", checks,
            boundary = "Production WinForms controls and draft store; synthetic published-format content, no mouse/keyboard automation and no external writes." });
    }
}

using System.Text.Json;
using KnowledgeClipper;
using KnowledgeClipper.Workflow;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var result = 0;
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += async (_, _) => {
            timer.Stop();
            try { await RunAsync(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); result = 1; }
            finally { context.ExitThread(); }
        };
        timer.Start(); Application.Run(context); return result;
    }

    static async Task RunAsync(string[] args)
    {
        try
        {
            var root = args[0]; var artifacts = args[1];
            if (args.Contains("--published-language")) { await PublishedPreviewChecks.RunAsync(root, artifacts); return; }
            if (args.Contains("--maintenance-preview")) { await MaintenancePreviewChecks.RunAsync(root, artifacts); return; }
            if (args.Contains("--language-modes")) { await LanguageModePreviewChecks.RunAsync(root, artifacts); return; }
            if (args.Contains("--deck-routing")) { await DeckPreviewChecks.RunAsync(root, artifacts); return; }
            var store = new DraftStore(Path.Combine(artifacts, "preview-state"));
            var configuration = ModeConfigurationLoader.Load(Path.Combine(root, "modes"));
            ClipDraft Create() => new() {
                Original = new("严谨", null, "generated_allowed", null, "预览组件检查", 0, DateTimeOffset.UtcNow),
                Material = new("严谨", null, "generated_allowed", null, "预览组件检查", 0, DateTimeOffset.UtcNow),
                Settings = new LanguageSettings { AnkiDeck = "KnowledgeClipper Canary::Architecture 20260910" },
                Configuration = configuration, ModeId = "language", Practice = PracticeChoice.Both,
                Content = WorkflowJson.Read<ContentResult>(Path.Combine(artifacts, "m1-zh-result.json"))
            };
            static T Find<T>(Form form, string suffix) where T : Control => (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();
            void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }

            // The production preview is used directly; this checks UI state, not capture or model quality.
            var draft = Create();
            using (var preview = new LanguageDraftForm(draft, store, () => Task.CompletedTask, () => Task.CompletedTask, () => { }))
            {
                preview.Show(); await Task.Delay(100);
                Check(Find<TextBoxBase>(preview, "reading-preview").Text.Contains("反面") && Find<TextBoxBase>(preview, "writing-preview").Text.Contains("［"), "readable card faces");
                var longMeaning = string.Concat(Enumerable.Repeat("长内容排版检查，可滚动阅读。", 40));
                Find<TextBox>(preview, "meaning").Text = longMeaning;
                Find<ComboBox>(preview, "practice").SelectedIndex = 2;
                preview.Size = preview.MinimumSize; await Task.Delay(100);
                var host = Find<Panel>(preview, "content-scroll");
                var actions = Find<TableLayoutPanel>(preview, "actions");
                var confirm = Find<Button>(preview, "confirm");
                var confirmBounds = preview.RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle));
                var actionBounds = preview.RectangleToClient(actions.RectangleToScreen(actions.ClientRectangle));
                Check(preview.DeviceDpi == 192, "current PerMonitorV2 display is 192 DPI");
                Check(preview.ClientRectangle.Contains(confirmBounds) && actionBounds.Contains(confirmBounds) && !host.Contains(confirm),
                    "confirmation is visible in fixed footer without scrolling: " + confirmBounds + " / " + preview.ClientRectangle);
                Check(host.VerticalScroll.Visible && host.DisplayRectangle.Height > host.ClientSize.Height, "long content has an independent scroll area");
                Check(Find<TabControl>(preview, "faces").Bottom <= Find<Label>(preview, "preview-note").Top,
                    "card faces and closing note do not overlap at minimum size");
                host.AutoScrollPosition = new Point(0, host.DisplayRectangle.Height); await Task.Delay(100);
                Check(preview.RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle)) == confirmBounds,
                    "content scrolling does not move the confirmation button");
                foreach (var suffix in new[] { "retry-content", "replay", "stop" })
                {
                    var control = Find<Button>(preview, suffix);
                    Check(preview.ClientRectangle.Contains(preview.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))), "secondary action stays visible: " + suffix);
                }
                using (var bitmap = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size)); bitmap.Save(Path.Combine(artifacts, "m1-preview-minimum.png")); }
                preview.Close();
                var saved = store.Recent().Single(d => d.Id == draft.Id);
                Check(saved.EditedFields.Meaning == longMeaning && saved.Practice == PracticeChoice.Writing, "unconfirmed edits persist");
                using var reopened = new LanguageDraftForm(saved, store, () => Task.CompletedTask, () => Task.CompletedTask, () => { });
                Check(Find<TextBox>(reopened, "meaning").Text == longMeaning && Find<ComboBox>(reopened, "practice").SelectedIndex == 2, "reopened preview retains edits");
            }

            // UI confirmation state uses a controlled importer, with no Anki calls.
            var confirmed = Create();
            var imports = 0;
            var releaseImport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var preview = new LanguageDraftForm(confirmed, store, async () => {
                imports++;
                await releaseImport.Task;
                confirmed.ImportState = ImportState.Verified;
                confirmed.CardIds = new long[] { 1, 2 };
            }, () => Task.CompletedTask, () => { }))
            {
                preview.Show(); await Task.Delay(100);
                var button = Find<Button>(preview, "confirm");
                Check(button.Enabled && button.Text == "确认导入 Anki", "ready draft exposes the direct card confirmation action");
                preview.ContentRunning = true; preview.RefreshStatus();
                Check(!button.Enabled && button.Text.Contains("等待内容"), "processing keeps the visible confirmation disabled");
                preview.ContentRunning = false; preview.RefreshStatus();
                button.PerformClick(); button.PerformClick();
                Check(imports == 1 && confirmed.ConfirmedNote != null && !button.Enabled, "one confirmation starts exactly one importer while pending");
                Check(Find<TextBox>(preview, "meaning").ReadOnly && !Find<ComboBox>(preview, "practice").Enabled, "pending confirmed fields remain frozen");
                releaseImport.SetResult(true); await Task.Delay(100);
                button.PerformClick(); await Task.Delay(50);
                Check(imports == 1 && confirmed.ImportState == ImportState.Verified && !button.Enabled, "repeated completed confirmation does not invoke importer again");
                preview.Close();
            }

            var unknown = Create(); unknown.ConfirmedNote = AnkiNoteDraft.From(unknown); unknown.ImportState = ImportState.Unknown;
            var originalNote = unknown.ConfirmedNote; var queries = 0;
            using (var preview = new LanguageDraftForm(unknown, store, () => { queries++; return Task.CompletedTask; }, () => Task.CompletedTask, () => { }))
            {
                preview.Show(); await Task.Delay(100);
                var button = Find<Button>(preview, "confirm");
                Check(button.Text == "核对写入结果" && Find<TextBox>(preview, "meaning").ReadOnly, "unknown result exposes reconciliation and preserves frozen content");
                button.PerformClick(); await Task.Delay(50);
                Check(queries == 1 && ReferenceEquals(unknown.ConfirmedNote, originalNote) && unknown.ImportState == ImportState.Unknown,
                    "unknown action retains the same confirmed note for the importer to reconcile");
                preview.Close();
            }

            var basicMaterial = new CaptureMaterial("保留本次原始输入。", null, "generated_allowed", null, "Basic 预览检查", 0, DateTimeOffset.UtcNow);
            var basic = new ClipDraft { Original = basicMaterial, Material = basicMaterial, Settings = new(), Configuration = configuration,
                ModeId = "translation", Route = new("translation", "translation", false, false),
                Content = new("translation", Basic: new("不采用的模型正面", "Preserve the original input.")) };
            var basicImports = 0;
            using (var preview = new LanguageDraftForm(basic, store, () => { basicImports++; basic.ImportState = ImportState.Verified; basic.CardIds = new long[] { 3 }; return Task.CompletedTask; }, () => Task.CompletedTask, () => { }))
            {
                preview.Show(); await Task.Delay(100);
                var front = Find<TextBox>(preview, "basic-front"); var back = Find<TextBox>(preview, "basic-back");
                var confirm = Find<Button>(preview, "confirm");
                Check(front.ReadOnly && front.Text == basic.Original.SelectedText && !back.ReadOnly, "Basic preview preserves original front and editable back");
                Check(!Find<TextBox>(preview, "pronunciation").Visible && !Find<ComboBox>(preview, "practice").Visible && !Find<TabControl>(preview, "faces").Visible,
                    "Basic preview hides language fields and practice choices");
                Check(!preview.Controls.Find("knowledge-clipper.language.follow-up-toggle", true).Any(), "retired follow-up toggle is absent");
                back.Text = "Edited translation."; preview.Size = preview.MinimumSize; await Task.Delay(100);
                Check(back.Text == "Edited translation." && confirm.Enabled, "translation edit and confirmation remain available");
                var openChat = Find<Button>(preview, "open-chat"); var opens = 0;
                preview.OpenChatRequested += () => opens++;
                openChat.PerformClick();
                Check(opens == 1, "conversation entry requests current chat");
                confirm.PerformClick(); await Task.Delay(50);
                var frozenBasic = basic.ConfirmedNote;
                Check(basicImports == 1 && frozenBasic.IsBasic && frozenBasic.Ordinals.SequenceEqual(new[] { 0 }) &&
                    frozenBasic.Fields["Front"] == AnkiNoteDraft.Html(basic.Original.SelectedText) && frozenBasic.Fields["Back"] == "Edited translation.",
                    "Basic confirmation still freezes exactly one edited card");
                preview.Close();
            }



            var missingContext = new CaptureMaterial("aa", null, "unavailable", null, "overlapping context fixture", 0, DateTimeOffset.UtcNow);
            var contextDraft = new ClipDraft { Original = missingContext, Material = missingContext, Settings = new(), Configuration = configuration };
            using (var preview = new LanguageDraftForm(contextDraft, store, () => Task.CompletedTask, () => Task.CompletedTask, () => { }))
            {
                preview.Show();
                var resolved = preview.ResolveContextAsync();
                var input = Find<TextBox>(preview, "user-context");
                input.Text = "😀aaa"; input.Select(0, 0);
                Find<Button>(preview, "use-context").PerformClick();
                Check(!resolved.IsCompleted, "overlapping supplied targets require the actual selection");
                input.Select(3, 2);
                Find<Button>(preview, "use-context").PerformClick();
                var material = await resolved;
                material.Validate();
                Check(material.TargetStart == 3 && material.ContextOrigin == "user" && material.ContextText == input.Text, "explicit supplied selection preserves exact UTF16 occurrence");
                preview.Close();
            }

            // Actual confirmation/import uses the unchanged live Content CLI result and production adapter.
            if (args.Contains("--anki"))
            {
                var resume = Array.IndexOf(args, "--resume");
                var actual = resume >= 0 ? WorkflowJson.Read<ClipDraft>(args[resume + 1]) : Create(); var calls = 0; var finished = false;
                actual.SpeechStatus = "朗读失败，可重新朗读"; actual.SpeechError = "界面独立性检查";
                using var http = new HttpClient();
                using var preview = new LanguageDraftForm(actual, store, async () => {
                    calls++;
                    try { await new AnkiConnection(http, actual.Settings.AnkiEndpoint).ImportAsync(actual, store); }
                    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); throw; }
                    finally { finished = true; }
                }, () => Task.CompletedTask, () => { });
                preview.Show(); await Task.Delay(100);
                using (var bitmap = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size)); bitmap.Save(Path.Combine(artifacts, "m1-preview-before-confirm.png")); }
                Check(actual.NoteId == null, "preview does not import before confirmation");
                Find<Button>(preview, "confirm").PerformClick();
                var deadline = DateTime.UtcNow.AddSeconds(100);
                while (!finished && DateTime.UtcNow < deadline) await Task.Delay(50);
                await Task.Delay(100);
                Check(actual.ImportState == ImportState.Verified, "one preview confirmation creates verified Anki cards: " + actual.ImportError);
                Find<Button>(preview, "confirm").PerformClick(); await Task.Delay(100);
                Check(calls == 1 && actual.CardIds.Length == 2, "repeated confirmation does not invoke importer");
                Check(Find<TextBox>(preview, "meaning").ReadOnly && !Find<ComboBox>(preview, "practice").Enabled, "confirmed preview remains frozen");
                WorkflowJson.Save(Path.Combine(artifacts, "m1-preview-confirmation.json"), new { actual.Id, actual.NoteId, actual.CardIds, calls, actual.ImportState, actual.ConfirmedNote, actual.Times });
                preview.Close();
            }
        }
        catch { throw; }
    }
}

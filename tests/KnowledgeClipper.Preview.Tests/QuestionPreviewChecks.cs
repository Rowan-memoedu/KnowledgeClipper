using KnowledgeClipper;
using KnowledgeClipper.Workflow;

internal static class QuestionPreviewChecks
{
    private static T Find<T>(Form form, string suffix) where T : Control =>
        (T)form.Controls.Find("knowledge-clipper.language." + suffix, true).Single();

    internal static async Task RunAsync(ContentConfiguration configuration, DraftStore store, string artifacts, Action<bool, string> check)
    {
        ClipDraft Session(bool unresolved = false, bool context = true)
        {
            var first = new ConversationTurn("第一轮问题？", new(unresolved ? "unresolved" : "answered", unresolved ? null : "第一轮回答", unresolved ? new[] { "missing_facts" } : Array.Empty<string>()));
            var material = new CaptureMaterial(first.Question, null, "generated_allowed", null, "逐轮卡片 UI fixture", 0, DateTimeOffset.UtcNow);
            return new ClipDraft { Original = material, Material = material, Settings = new() { QuestionCardIncludeContext = context }, Configuration = configuration,
                ModeId = "question", Route = new("question", "question", true, false),
                Content = new("question", Basic: unresolved ? null : new(first.Question, first.Answer.AnswerText), Answer: first.Answer), Conversation = new() { first } };
        }

        // Saved local child fixtures stand in for backend state callbacks. This test never calls Anki.
        void SavedState(ClipDraft parent, int[] indexes, ImportState state)
        {
            var rows = QuestionCards.Preview(parent, store);
            foreach (var index in indexes)
            {
                var row = rows.Single(item => item.Index == index);
                var child = parent.FollowUpCards.TryGetValue(index, out var id) ? store.Find(id) : null;
                if (child == null)
                {
                    var material = new CaptureMaterial(row.Question, null, "generated_allowed", null, parent.Material.SourceTitle, 0, DateTimeOffset.UtcNow);
                    child = new() { Original = material, Material = material, Settings = parent.Settings, Configuration = configuration,
                        ParentDraftId = parent.Id, ModeId = "question", Route = new("question", "question", true, false),
                        Content = new("question", Basic: new(row.Question, row.Answer, row.Context), Answer: new("answered", row.Answer, Array.Empty<string>())) };
                    child.ConfirmedNote = AnkiNoteDraft.From(child); parent.FollowUpCards[index] = child.Id;
                }
                child.ImportState = state;
                child.NoteId = state == ImportState.Verified ? 100 + index : null;
                child.CardIds = state == ImportState.Verified ? new long[] { 200 + index } : Array.Empty<long>();
                store.Save(child);
            }
            store.Save(parent);
        }

        var draft = Session();
        draft.Conversation.Add(new("第二轮问题？", new("answered", "第二轮回答", Array.Empty<string>())));
        draft.Conversation.Add(new("第三轮问题？", new("answered", "第三轮回答", Array.Empty<string>())));
        using (var preview = new LanguageDraftForm(draft, store, () => throw new Exception("Question view must not import the original Basic preview."), () => Task.CompletedTask, () => { }))
        {
            preview.Show(); preview.Size = preview.MinimumSize; await Task.Delay(100);
            var size = preview.Size; var confirm = Find<Button>(preview, "confirm");
            var rows = Find<TableLayoutPanel>(preview, "question-cards");
            var answer = Find<TextBox>(preview, "question-card-0.answer");
            var selection = Find<CheckBox>(preview, "question-card-0.selected");
            check(rows.Visible && rows.Controls.Count == 3 && !Find<TableLayoutPanel>(preview, "basic-fields").Visible,
                "question output shows one three-row list instead of an initial Basic answer and transcript");
            check(!Find<Button>(preview, "replay").Visible && !Find<Button>(preview, "stop").Visible, "question preview has no speech controls");
            check(Enumerable.Range(0, 3).All(i => Find<CheckBox>(preview, "question-card-" + i + ".selected").Checked) && confirm.Text.Contains("3 张问答卡"),
                "all three unanswered-import states are selected by default in one batch action");
            check(Enumerable.Range(0, 3).All(i => Find<TextBox>(preview, "question-card-" + i + ".question").ReadOnly && !Find<TextBox>(preview, "question-card-" + i + ".answer").ReadOnly),
                "each question is read-only and its unfrozen answer is editable");
            answer.Text = "修订后的第一轮回答"; answer.Focus(); answer.Select(2, 3); selection.Checked = false;
            preview.RefreshConversation();
            check(ReferenceEquals(answer, Find<TextBox>(preview, "question-card-0.answer")) && answer.SelectionStart == 2 && answer.SelectionLength == 3 && !selection.Checked,
                "refresh preserves row controls, answer selection and card selection");
            check(draft.EditedAnswers[0] == answer.Text && draft.Conversation[0].Answer.AnswerText == "第一轮回答" &&
                store.Find(draft.Id).EditedAnswers[0] == answer.Text, "answer edit persists separately without changing the original conversation");
            check(Find<TextBox>(preview, "question-card-1.context").Text.Contains(answer.Text) &&
                Find<TextBox>(preview, "question-card-2.context").Text.Contains("第二轮问题？") &&
                !Find<GroupBox>(preview, "question-card-0.context-group").Visible,
                "later auxiliary context follows edited previous turns while the first row has none");
            check(confirm.Text.Contains("2 张问答卡"), "manual deselection immediately scopes the batch");
            selection.Checked = true;
            var followInput = Find<TextBox>(preview, "follow-up-input"); followInput.Text = "继续说明";
            preview.QuestionImportRunning = true;
            check(!confirm.Enabled && !Find<Button>(preview, "question-card-1.import").Enabled && !Find<Button>(preview, "follow-up-send").Enabled,
                "external question import state blocks all import and follow-up actions");
            preview.QuestionImportRunning = false;

            var calls = new List<int[]>();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            preview.ImportQuestionCards += async indexes => {
                calls.Add(indexes); SavedState(draft, indexes, ImportState.Ready); preview.RefreshConversation();
                if (calls.Count == 1) await release.Task;
                SavedState(draft, indexes, ImportState.Verified); preview.RefreshConversation();
            };
            var single = Find<Button>(preview, "question-card-1.import");
            single.PerformClick(); single.PerformClick(); confirm.PerformClick();
            check(calls.Count == 1 && calls[0].SequenceEqual(new[] { 1 }) && !confirm.Enabled && Find<TextBox>(preview, "question-card-1.answer").ReadOnly,
                "single-row import emits its index once and freezes the backend-confirmed answer while pending");
            release.SetResult(true); await Task.Delay(100);
            var frozenContext = Find<TextBox>(preview, "question-card-1.context").Text;
            answer.Text = "再次修订第一轮回答";
            check(!single.Enabled && !Find<CheckBox>(preview, "question-card-1.selected").Checked &&
                Find<TextBox>(preview, "question-card-1.context").Text == frozenContext && confirm.Text.Contains("2 张问答卡"),
                "completed row stays frozen and unselected while only the remaining two are offered");
            confirm.PerformClick(); await Task.Delay(80); confirm.PerformClick(); single.PerformClick();
            check(calls.Count == 2 && calls[1].SequenceEqual(new[] { 0, 2 }) && !confirm.Enabled &&
                Enumerable.Range(0, 3).All(i => Find<TextBox>(preview, "question-card-" + i + ".answer").ReadOnly),
                "one bottom action imports only the two remaining indexes and completed actions cannot repeat");
            check(draft.ConfirmedNote == null && draft.ImportState == ImportState.Unconfirmed && draft.FollowUpCards.Count == 3,
                "question UI keeps one parent session and represents imported cards through saved child states");
            var host = Find<Panel>(preview, "content-scroll");
            var bounds = preview.RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle));
            check(preview.DeviceDpi == 192 && preview.Size == size && preview.ClientRectangle.Contains(bounds) && !host.Contains(confirm) && host.VerticalScroll.Visible,
                "three-row question list scrolls independently with the fixed footer fully visible at 192 DPI minimum size");
            using (var bitmap = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size)); bitmap.Save(Path.Combine(artifacts, "question-cards-minimum.png")); }
            preview.Close();
        }

        var unknown = Session(); SavedState(unknown, new[] { 0 }, ImportState.Unknown);
        using (var preview = new LanguageDraftForm(unknown, store, () => throw new Exception("Wrong importer"), () => Task.CompletedTask, () => { }))
        {
            preview.Show(); await Task.Delay(60); var calls = 0;
            preview.ImportQuestionCards += indexes => { calls++; check(indexes.SequenceEqual(new[] { 0 }), "unknown reconciliation retains the same row identity"); return Task.CompletedTask; };
            check(Find<TextBox>(preview, "question-card-0.answer").ReadOnly && Find<Button>(preview, "question-card-0.import").Text.Contains("核对") &&
                Find<Button>(preview, "confirm").Text.Contains("核对"), "unknown row exposes reconciliation with frozen answer");
            Find<Button>(preview, "question-card-0.import").PerformClick(); await Task.Delay(40);
            check(calls == 1 && QuestionCards.Preview(unknown, store)[0].State == ImportState.Unknown, "unknown action delegates once without fabricating a verified state"); preview.Close();
        }

        var unresolved = Session(true, false);
        using (var preview = new LanguageDraftForm(unresolved, store, () => throw new Exception("Unresolved answers cannot import"), () => Task.CompletedTask, () => { }))
        {
            preview.Show(); await Task.Delay(60);
            check(!Find<Button>(preview, "confirm").Enabled && !Find<Button>(preview, "question-card-0.import").Visible &&
                !Find<CheckBox>(preview, "question-card-0.selected").Visible && Find<TextBox>(preview, "question-card-0.answer").ReadOnly,
                "unresolved row displays its reason without an importable card");
            Find<TextBox>(preview, "follow-up-input").Text = "补充充分条件";
            preview.SubmitFollowUp += text => { unresolved.Conversation.Add(new(text, new("answered", "补充后已有答案", Array.Empty<string>()))); return Task.CompletedTask; };
            Find<Button>(preview, "follow-up-send").PerformClick(); await Task.Delay(60);
            check(Find<Button>(preview, "confirm").Enabled && Find<Button>(preview, "confirm").Text.Contains("1 张问答卡") &&
                Find<CheckBox>(preview, "question-card-1.selected").Checked, "answered follow-up becomes importable even when the original question remains unresolved");
            check(Find<TextBox>(preview, "question-card-1.context").Text.Length == 0 && !Find<GroupBox>(preview, "question-card-1.context-group").Visible,
                "disabled context setting leaves the auxiliary context absent"); preview.Close();
        }
    }
}

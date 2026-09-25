using System.IO;
using KnowledgeClipper.Workflow;

internal static class HistoryActionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(@"D:\Caches\KnowledgeClipper\history-action-tests", Guid.NewGuid().ToString("N"));
        var drafts = new DraftStore(root); var excerpts = new ExcerptStore(root);
        var material = new CaptureMaterial("lead", "They lead the team.", "source", 5, "Public fixture", 0, DateTimeOffset.UtcNow);
        var modes = new[] {
            new ModeDefinition("reading", "Reading", true, "", "", "", Language: "en", Practice: PracticeChoice.Reading),
            new ModeDefinition("writing", "Writing", true, "", "", "", Language: "en", Practice: PracticeChoice.Writing),
            new ModeDefinition("question", "Question", true, "", "", "", "question", RequiresContext: false) };
        var configuration = new ContentConfiguration("", modes.Select(mode => new ModeSnapshot(mode, "", "{}")).ToArray());
        ClipDraft Draft() => new() { Original = material, Material = material, Settings = new(), Configuration = configuration,
            ModeId = "reading", Route = new("reading", "language", false, true), Input = new(Intent: "language"), Practice = PracticeChoice.Reading };
        void Ready(ClipDraft draft) { draft.Content = new(draft.ModeId!, new("/liːd/", "带领", null, null)); drafts.Save(draft); }
        string Text(ClipDraft draft) => HistoryRecords.ActionText(new(Draft: draft), drafts, excerpts);
        string ExcerptText(ExcerptSession session) => HistoryRecords.ActionText(new(Excerpt: session), drafts, excerpts);
        var single = Draft(); drafts.Save(single);
        check(Text(single) == "继续制卡", "no generated or imported card cannot be labelled view");
        Ready(single);
        check(Text(single) == "继续制卡", "a ready but never imported language card remains continue");
        VerifyFixture(single, drafts);
        check(Text(single) == "查看", "one verified language card is labelled view");
        foreach (var state in new[] { ImportState.Ready, ImportState.Writing, ImportState.Unknown, ImportState.Failed })
        {
            single.ImportState = state; drafts.Save(single);
            check(Text(single) == "继续制卡", state + " does not count as a completed import");
        }
        var parent = Draft(); var children = LanguageModeCards.Ensure(parent, modes.Take(2).ToArray(), drafts);
        foreach (var child in children) Ready(child);
        VerifyFixture(children[0], drafts);
        check(Text(parent) == "继续制卡", "partial import across language modes remains continue");
        LanguageModeCards.Ensure(parent, [modes[0]], drafts);
        check(Text(parent) == "继续制卡", "deselecting a generated unimported mode does not hide it from the completion rule");
        VerifyFixture(children[1], drafts);
        check(Text(parent) == "查看", "all generated language modes imported changes the record to view");
        parent.ModeCards["missing"] = Guid.NewGuid().ToString("N"); drafts.Save(parent);
        check(Text(parent) == "继续制卡", "a missing bound card prevents a false completed label");

        var questionMaterial = new CaptureMaterial("What is a unit test?", null, "unavailable", null, "Public fixture", 0, DateTimeOffset.UtcNow);
        var answer = new QuestionAnswer("answered", "A check of a bounded behavior.", []);
        var question = new ClipDraft { Original = questionMaterial, Material = questionMaterial, Settings = new(), Configuration = configuration,
            Input = new(Intent: "question"), ModeId = "question", Route = new("question", "question", true, false),
            Content = new("question", Basic: new(questionMaterial.SelectedText, answer.AnswerText!), Answer: answer), Conversation = [new(questionMaterial.SelectedText, answer)] };
        drafts.Save(question);
        check(Text(question) == "继续制卡", "an answered question that has never been imported remains continue");
        VerifyFixture(question, drafts);
        check(Text(question) == "查看", "a verified original question is counted once and can be viewed");
        var followUp = new ConversationTurn("Why test it?", new("answered", "To detect a regression.", []));
        question.Conversation.Add(followUp); drafts.Save(question);
        check(Text(question) == "继续制卡", "a new generated follow-up returns an imported conversation to continue");
        var followMaterial = questionMaterial with { SelectedText = followUp.Question };
        var follow = new ClipDraft { Original = followMaterial, Material = followMaterial, Settings = question.Settings, Configuration = configuration,
            Input = new(Intent: "question"), ModeId = "question", Route = question.Route, ParentDraftId = question.Id,
            Content = new("question", Basic: new(followUp.Question, followUp.Answer.AnswerText!), Answer: followUp.Answer), Conversation = question.Conversation.ToList() };
        VerifyFixture(follow, drafts); question.FollowUpCards[1] = follow.Id; drafts.Save(question);
        check(Text(question) == "查看", "all original and follow-up question cards imported restores view");
        single.ImportState = ImportState.Verified; single.Conversation.Add(followUp); drafts.Save(single);
        check(Text(single) == "继续制卡", "a language record includes its generated follow-up cards in the same rule");

        var empty = new ExcerptSession { Original = material, Settings = new() };
        check(ExcerptText(empty) == "继续制卡", "an empty excerpt cannot satisfy the at-least-one-import condition");
        var excerpt = ExcerptCards.Create(material, new(), excerpts);
        var raw = excerpts.Cards.Find(excerpt.Cards[0].Id)!;
        check(ExcerptText(excerpt) == "继续制卡", "the independent original excerpt card is initially unimported");
        VerifyFixture(raw, excerpts.Cards);
        check(ExcerptText(excerpt) == "查看", "an imported original excerpt with a blank back can be viewed");
        var manual = ExcerptCards.Add(excerpt, excerpts, new("Independent card", "Answer"), "手动");
        check(ExcerptText(excerpt) == "继续制卡", "adding a manual card restores continue even after an earlier import");
        VerifyFixture(manual, excerpts.Cards);
        var first = ExcerptCards.Cloze(excerpt, excerpts, 0, 1); var second = ExcerptCards.Cloze(excerpt, excerpts, 1, 1);
        var merged = ExcerptCards.Merge(excerpt, excerpts, [first.Id, second.Id]);
        check(ExcerptText(excerpt) == "继续制卡", "the new merged cloze must also be imported");
        VerifyFixture(merged, excerpts.Cards);
        check(ExcerptText(excerpt) == "查看", "superseded cloze drafts do not block an entirely imported active card list");
    }

    // Isolated saved-state fixture, not evidence of an actual Anki import.
    internal static void VerifyFixture(ClipDraft draft, DraftStore store)
    {
        draft.ConfirmedNote = AnkiNoteDraft.From(draft); draft.ImportState = ImportState.Verified;
        draft.NoteId = 12345; draft.CardIds = [67890]; store.Save(draft);
    }
}

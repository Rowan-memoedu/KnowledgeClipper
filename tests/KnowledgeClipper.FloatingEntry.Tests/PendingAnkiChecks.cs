using System.Collections;
using System.IO;
using System.Net.Http;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static async Task PendingAnkiAsync(Form owner)
    {
        var root = Path.Combine(dataRoot, "pending-anki");
        var settings = Settings() with { QuickLanguage = true, ContextEnabled = false };
        WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
        using var language = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
        var feedback = new List<string>(); ListenQuickFeedback(language, feedback);
        using var provider = new ImageRouteProvider(); using var http = new HttpClient(provider);
        ((HttpClient)language.GetType().GetField("http", Flags)!.GetValue(language)!).Dispose();
        Field(language, "http", http);
        var configuration = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"));
        var store = new DraftStore(root);
        var observed = new HashSet<Form>();
        void Observe() { foreach (var form in Application.OpenForms.Cast<Form>().Where(f => !ReferenceEquals(f, owner))) observed.Add(form); }
        using var sampler = new System.Windows.Forms.Timer { Interval = 10 };
        sampler.Tick += (_, _) => Observe(); sampler.Start();
        var draft = Start(language, "lead", "language", settings, configuration, true);
        Observe();
        await Until(() => draft.ModeCards.Count == 2 && LanguageModeCards.Children(draft, store).All(c => c.AwaitingAnki),
            "offline quick language confirms and queues both configured modes", 18000);
        await Until(() => !((IDictionary)language.GetType().GetField("quickDrafts", Flags)!.GetValue(language)!).Contains(draft.Id),
            "pending quick task completes instead of keeping a processing window alive");
        Observe(); sampler.Stop();
        var children = LanguageModeCards.Children(draft, store);
        Check(observed.Count == 0 && Forms(language).Length == 0, "offline confirmed quick language never opens a preview");
        Check(feedback.Count == 1 && feedback[0].Contains("等待") && !feedback[0].Contains("成功"),
            "unavailable Anki emits a saved/waiting notice without claiming import success");
        Check(children.All(c => c.ConfirmedNote != null && c.ConfirmedDestination == ExportDestination.Anki &&
            c.ImportState != ImportState.Verified && c.NoteId == null && c.CardIds.Length == 0),
            "pending cards retain frozen Anki confirmation and never claim verified identities");
        using (var offline = new HttpClient())
        using (var queue = new AnkiPendingQueue(root, offline))
        {
            await queue.Ready;
            Check(queue.Count == 2, "fresh queue discovers the two durable pending cards");
            await queue.PumpAsync();
            Check(queue.Count == 2 && LanguageModeCards.Children(draft, store).All(c => c.AwaitingAnki),
                "unavailable endpoint preserves both cards across a production queue pump");
        }
        Call(language, "ContinueHistory", store.Find(draft.Id));
        var form = Forms(language).Single();
        var links = draft.ModeCards.ToDictionary(x => x.Key, x => x.Value);
        // Inject only a local import-state fixture, then exercise the real event-to-view path.
        // This does not claim Anki transport or remote readback success.
        foreach (var child in children)
        {
            child.AwaitingAnki = false; child.ImportState = ImportState.Verified;
            child.ImportError = null; child.NoteId = 1000 + Array.IndexOf(children, child);
            child.CardIds = [2000 + Array.IndexOf(children, child)]; store.Save(child);
            Call(language, "PendingImportChanged", child);
        }
        Check(Forms(language).Length == 1 && ReferenceEquals(form, Forms(language).Single()),
            "pending completion refreshes the existing language window without replacing it");
        Check(((Button)form.GetType().GetField("confirm", Flags)!.GetValue(form)!).Text.Contains("均已导入"),
            "existing language preview observes completed children after pending import invalidation");
        Check(store.Find(draft.Id)!.ModeCards.OrderBy(x => x.Key).SequenceEqual(links.OrderBy(x => x.Key)),
            "pending completion preserves original history parent-to-child links");
        form.Close();

        using var excerpt = (IDisposable)New("ExcerptDesktopFlow", owner, root, settings, (Action<string>)statuses.Add);
        var excerpts = new ExcerptStore(root);
        var session = ExcerptCards.Create(Material("Public pending excerpt source."), settings, excerpts);
        ExcerptCards.Add(session, excerpts, new ExcerptFields("Editable public front.", "Editable public back."), "editable");
        Call(excerpt, "Open", session);
        var excerptForm = Forms(excerpt).Single();
        var editId = session.Cards.Last().Id;
        var editorName = "knowledge-clipper.excerpt.back." + editId;
        var editor = (RichTextBox)excerptForm.Controls.Find(editorName, true).Single();
        editor.Text = "A public unsaved edit that must survive."; editor.Select(9, 7);
        var selectedText = editor.SelectedText; var controlsBefore = excerptForm.Controls.Find(editorName, true).Single();
        var pendingCard = excerpts.Cards.Find(session.Cards[0].Id)!;
        ExportRouting.Confirm(pendingCard, AnkiNoteDraft.From(pendingCard), ExportDestination.Anki);
        pendingCard.AwaitingAnki = true; pendingCard.ImportState = ImportState.Failed; excerpts.Cards.Save(pendingCard);
        Call(excerpt, "PendingImportChanged", pendingCard);
        Check(ReferenceEquals(editor, excerptForm.Controls.Find(editorName, true).Single()) && !editor.IsDisposed,
            "excerpt pending refresh retains the unrelated editor control instance");
        Check(editor.Text == "A public unsaved edit that must survive." && editor.SelectionStart == 9 &&
            editor.SelectionLength == 7 && editor.SelectedText == selectedText,
            "excerpt pending refresh preserves unsaved text and exact selection");
        pendingCard.AwaitingAnki = false; pendingCard.ImportState = ImportState.Verified;
        pendingCard.NoteId = 4001; pendingCard.CardIds = [4002]; excerpts.Cards.Save(pendingCard);
        Call(excerpt, "PendingImportChanged", pendingCard);
        Check(ReferenceEquals(controlsBefore, excerptForm.Controls.Find(editorName, true).Single()) &&
            editor.Text == "A public unsaved edit that must survive." && editor.SelectionStart == 9 && editor.SelectionLength == 7,
            "excerpt completion refresh also preserves the unrelated editor and selection");
        Check(!excerptForm.Controls.Find("knowledge-clipper.excerpt.import." + pendingCard.Id, true).Single().Enabled,
            "excerpt completion refresh disables repeat import for the completed card");
        Save(excerptForm, "pending-excerpt-preserved-editor.png");
        excerptForm.Close();
        Check(excerpts.Cards.Find(editId)!.Excerpt!.Back == "A public unsaved edit that must survive.",
            "closing after background state updates saves the surviving user edit");
    }
}

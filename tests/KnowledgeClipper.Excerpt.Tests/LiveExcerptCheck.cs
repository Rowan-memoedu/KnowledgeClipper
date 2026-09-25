using KnowledgeClipper.Workflow;

internal static class LiveExcerptCheck
{
    internal static async Task RunAsync(string receipt)
    {
        using var http = new HttpClient();
        var anki = new AnkiConnection(http, "http://127.0.0.1:8765");
        var version = await anki.CallAsync("version");
        if (version.GetInt32() < 6) throw new InvalidDataException("AnkiConnect v6 is required.");
        var run = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var data = Path.Combine(@"D:\Caches\KnowledgeClipper\excerpt-live-check", run);
        var store = new ExcerptStore(data);
        var deck = "KnowledgeClipper::验收::摘录改版 " + run;
        var settings = new LanguageSettings { AnkiRootDeck = deck, AnkiDeck = deck };
        var original = new CaptureMaterial("Learning connects ideas.\n练习帮助我们建立联系。", null, "unavailable", null, "摘录更新公开验收样例", 0, DateTimeOffset.UtcNow);
        var session = ExcerptCards.Create(original, settings, store);
        var rawId = session.Cards[0].Id;
        var manual = ExcerptCards.Add(session, store, new("What connects ideas?", "Learning."), "手动");
        var first = ExcerptCards.Cloze(session, store, 0, "Learning".Length);
        var second = ExcerptCards.Cloze(session, store, original.SelectedText.IndexOf("ideas", StringComparison.Ordinal), "ideas".Length);
        var merged = ExcerptCards.Merge(session, store, [first.Id, second.Id]);
        await ExcerptCards.ImportAsync(session, store, anki, [rawId]);
        await ExcerptCards.ImportAsync(session, store, anki, session.Cards.Where(c => c.SupersededBy == null).Select(c => c.Id));
        var cards = new[] { rawId, manual.Id, merged.Id }.Select(id => store.Cards.Find(id)!).ToArray();
        if (cards.Any(c => c.ImportState != ImportState.Verified || c.CardIds.Length != 1)) throw new InvalidDataException("Native card count or readback differs.");
        foreach (var card in cards) await anki.VerifyAsync(card);
        var ids = cards.Select(c => c.NoteId).ToArray();
        await ExcerptCards.ImportAsync(store.Find(session.Id)!, store, anki, [rawId, manual.Id, merged.Id]);
        var found = await anki.CallAsync("findNotes", new { query = "deck:\"" + deck + "\"" });
        if (found.GetArrayLength() != 3 || !found.EnumerateArray().Select(n => (long?)n.GetInt64()).Order().SequenceEqual(ids.Order()))
            throw new InvalidDataException("Repeat import duplicated or lost a note.");
        var info = await anki.CallAsync("cardsInfo", new { cards = cards.SelectMany(c => c.CardIds).ToArray() });
        WorkflowJson.Save(receipt, new { passed = true, deck, dataRoot = data, sessionId = session.Id,
            noteIds = ids, cardIds = cards.SelectMany(c => c.CardIds), actualCards = info,
            checks = new[] { "real raw card has original front and blank back", "real manual front/back card", "native merged cloze creates exactly one card with both blanks", "actual field, face, deck and reviewability readback", "reopen and repeated batch import add no duplicates" },
            scope = "three public fixture notes in a dedicated Anki acceptance deck; no prompts or changes to existing notes" });
        Console.WriteLine("PASS native Anki excerpt import: 3 notes, 3 cards, repeat import unchanged; " + receipt);
    }
}

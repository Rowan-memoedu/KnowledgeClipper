namespace KnowledgeClipper.Workflow;

public interface IImportedCardRevealer
{
    Task RevealAsync(ClipDraft draft, CancellationToken ct = default);
}

public sealed partial class CardImportRouter : IImportedCardRevealer
{
    public async Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
    {
        var alreadyVerified = draft.ImportState == ImportState.Verified;
        await ImportCoreAsync(draft, store, ct);
        // ExcerptCards already reveals once at the end of its batch.
        if (alreadyVerified || draft.Excerpt != null || ExportRouting.For(draft) != ExportDestination.RemNote ||
            draft.ImportState != ImportState.Verified) return;
        try { await RevealAsync(draft, ct); draft.RemNoteRevealError = null; }
        catch (Exception ex) { draft.RemNoteRevealError = ex.Message; }
        store.Save(draft);
    }

    public Task RevealAsync(ClipDraft draft, CancellationToken ct = default) =>
        ExportRouting.For(draft) == ExportDestination.RemNote
            ? new RemNoteConnection(http, draft.Settings.RemNoteSdkEndpoint).RevealAsync(draft, ct)
            : Task.CompletedTask;
}

public sealed partial class RemNoteConnection : IImportedCardRevealer
{
    public async Task RevealAsync(ClipDraft draft, CancellationToken ct = default)
    {
        if (ExportRouting.For(draft) != ExportDestination.RemNote || draft.ImportState != ImportState.Verified ||
            draft.ConfirmedRemNote is not { } plan || draft.RemNoteResult is not { } receipt)
            throw new InvalidOperationException("仅可定位已核验导入的 RemNote 内容。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A minimized RemNote renderer throttles SDK messages, especially just after startup.
        timeout.CancelAfter(TimeSpan.FromSeconds(32));
        var result = await RequestAsync("reveal", new { plan, expected = IdentityOf(receipt.Evidence) }, timeout.Token);
        if (True(result, "superseded")) return;
        Require(True(result, "revealed") && Id(result, "sourceId") == plan.SourceId &&
            Id(result, "documentRemId") == receipt.DocumentRemId && Ids(result, "remIds").SequenceEqual(receipt.RemIds),
            "导入后的页面及选中节点");
    }
}

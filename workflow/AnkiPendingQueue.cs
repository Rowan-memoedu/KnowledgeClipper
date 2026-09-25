using System.Collections.Concurrent;
using System.Text.Json;

namespace KnowledgeClipper.Workflow;

public sealed class AnkiPendingException() : Exception("内容已保存，等待 Anki 打开后自动导入。") { }

// The original confirmed drafts are the durable queue. Never regenerate a card,
// broaden the selected batch, or infer consent from an arbitrary old failure.
public sealed class AnkiPendingQueue : IDisposable
{
    private sealed record Entry(ClipDraft Draft, DraftStore Store);
    private readonly ConcurrentDictionary<string, Entry> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;
    private readonly HttpClient http;
    private readonly SemaphoreSlim pump = new(1, 1);
    private bool disposed;
    public event Action<ClipDraft, DraftStore>? Changed;
    public Task Ready { get; }
    public int Count => pending.Count;

    public AnkiPendingQueue(string root, HttpClient http)
    {
        this.root = Path.GetFullPath(root); this.http = http;
        AnkiConnection.ImportUpdated += Observe;
        Ready = Task.Run(Load);
    }

    private void Load()
    {
        foreach (var folder in new[] { "language-drafts", "excerpt-cards" })
        {
            var store = new DraftStore(root, folder);
            if (!Directory.Exists(store.DirectoryPath)) continue;
            foreach (var path in Directory.EnumerateFiles(store.DirectoryPath, "*.json"))
            {
                if (disposed) return;
                try
                {
                    var draft = store.Find(Path.GetFileNameWithoutExtension(path));
                    if (Eligible(draft)) pending.TryAdd(store.Identity(draft!.Id), new(draft, store));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool Eligible(ClipDraft? draft) => draft is { AwaitingAnki: true, ConfirmedNote: not null } &&
        draft.ImportState != ImportState.Verified && ExportRouting.For(draft) == ExportDestination.Anki;

    private void Observe(ClipDraft draft, DraftStore store)
    {
        if (disposed || !string.Equals(Path.GetDirectoryName(store.DirectoryPath), root, StringComparison.OrdinalIgnoreCase)) return;
        var key = store.Identity(draft.Id);
        if (Eligible(draft)) pending[key] = new(draft, store);
        else pending.TryRemove(key, out _);
    }

    public async Task PumpAsync(CancellationToken ct = default)
    {
        await Ready.WaitAsync(ct);
        if (disposed || !await pump.WaitAsync(0, ct)) return;
        try
        {
            foreach (var group in pending.Values.OrderBy(e => e.Draft.Times.GetValueOrDefault("confirmed"))
                .GroupBy(e => e.Draft.Settings.AnkiEndpoint))
            {
                ct.ThrowIfCancellationRequested();
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(2));
                try { _ = await new AnkiConnection(http, group.Key).DecksAsync(probe.Token); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException) { continue; }
                foreach (var entry in group)
                {
                    if (disposed) return;
                    // A manual retry may have finished while the queue was waiting.
                    var saved = entry.Store.Find(entry.Draft.Id);
                    if (!Eligible(saved)) { pending.TryRemove(entry.Store.Identity(entry.Draft.Id), out _); continue; }
                    try { await new AnkiConnection(http, group.Key).ImportAsync(saved!, entry.Store, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { /* The importer persisted the error; non-transport errors leave the queue. */ }
                    var current = entry.Store.Find(entry.Draft.Id)!;
                    Changed?.Invoke(current, entry.Store);
                    if (current.AwaitingAnki) break;
                }
            }
        }
        finally { pump.Release(); }
    }

    public void Dispose() { disposed = true; AnkiConnection.ImportUpdated -= Observe; }
}

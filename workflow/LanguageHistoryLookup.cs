using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public sealed record LanguageHistoryCandidate(string SourceId, string SessionId, LanguageLookupResult? Lookup,
    string? Pronunciation, string? ContextText, string? AudioContextText, string AudioText, LanguageSettings AudioSettings,
    string? AudioPath, string AudioSourceDraftId, bool CanPlay, bool HasCard);

public sealed record LanguageHistoryMatch(IReadOnlyList<LanguageHistoryCandidate> Candidates)
{
    public bool HasCard => Candidates.Any(candidate => candidate.HasCard);
    public bool HasPlayableAudio => Candidates.Any(candidate => candidate.CanPlay);
}

// Read once in the background; clicks only consult the selected word's in-memory bucket.
public sealed class LanguageHistoryLookup
{
    private readonly string dataRoot;
    private readonly object gate = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private Dictionary<string, ClipDraft> drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipDraft> remembered = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>> words = new(StringComparer.Ordinal);
    public Task Ready { get; }

    public LanguageHistoryLookup(string dataRoot)
    {
        this.dataRoot = dataRoot;
        Ready = Task.Run(() => RefreshAsync());
    }

    private static string Key(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ").ToUpperInvariant();

    public void Remember(ClipDraft draft)
    {
        var copy = JsonSerializer.Deserialize<ClipDraft>(JsonSerializer.Serialize(draft))!;
        lock (gate)
        {
            remembered[copy.Id] = copy;
            if (drafts.TryGetValue(copy.Id, out var old) && words.TryGetValue(Key(old.Original.SelectedText), out var oldBucket))
                oldBucket.Remove(copy.Id);
            drafts[copy.Id] = copy;
            var key = Key(copy.Original.SelectedText);
            if (!words.TryGetValue(key, out var bucket)) words[key] = bucket = new(StringComparer.Ordinal);
            bucket.Add(copy.Id);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var loaded = await Task.Run(() =>
            {
                var result = new Dictionary<string, ClipDraft>(StringComparer.Ordinal);
                var folder = Path.Combine(dataRoot, "language-drafts");
                if (!Directory.Exists(folder)) return result;
                foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var draft = WorkflowJson.Read<ClipDraft>(path);
                        if (draft.Original != null && draft.Settings != null && draft.Configuration != null &&
                            Path.GetFileNameWithoutExtension(path) == draft.Id) result[draft.Id] = draft;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
                }
                return result;
            }, ct).ConfigureAwait(false);
            lock (gate)
            {
                foreach (var pair in remembered) loaded[pair.Key] = pair.Value;
                drafts = loaded;
                remembered.Clear();
                words = loaded.Values.GroupBy(d => Key(d.Original.SelectedText)).ToDictionary(g => g.Key,
                    g => g.Select(d => d.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            }
        }
        finally { refreshGate.Release(); }
    }

    public async Task<LanguageHistoryMatch> FindAsync(string selectedText, string? excludeDraftId = null, CancellationToken ct = default)
    {
        await Ready.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selectedText)) return new([]);
        lock (gate)
        {
            if (!words.TryGetValue(Key(selectedText), out var bucket)) return new([]);
            var result = new List<LanguageHistoryCandidate>();
            foreach (var id in bucket)
            {
                var draft = drafts[id];
                if (id == excludeDraftId || !HistoryRecords.IsLanguage(draft)) continue;
                var parent = ValidParent(draft);
                if (excludeDraftId != null && parent?.Id == excludeDraftId) continue;
                var audio = draft.ReusedSpeech;
                var owner = draft;
                if (audio == null && draft.AudioPath == null && parent != null) { owner = parent; audio = parent.ReusedSpeech; }
                var audioText = audio?.Text ?? owner.Original.SelectedText;
                var settings = audio?.Settings ?? owner.Settings;
                var path = audio?.AudioPath ?? owner.AudioPath;
                var canPlay = Key(audioText) == Key(selectedText) && ValidAudio(audioText, settings, path);
                var children = draft.ModeCards.Values.Select(childId => drafts.GetValueOrDefault(childId))
                    .Where(child => child != null && ValidParent(child)?.Id == draft.Id).Cast<ClipDraft>().ToArray();
                var hasCard = HasCard(draft) || children.Any(HasCard);
                var lookup = draft.Lookup ?? parent?.Lookup;
                var source = AudioSource(owner);
                var pronunciation = source?.Lookup?.Pronunciation ?? source?.PreviewContent?.Fields?.Pronunciation;
                if (source != null && string.IsNullOrWhiteSpace(pronunciation))
                    pronunciation = source.ModeCards.Values.Select(childId => drafts.GetValueOrDefault(childId))
                        .Where(child => child != null && ValidParent(child)?.Id == source.Id)
                        .Select(child => child!.Lookup?.Pronunciation ?? child.PreviewContent?.Fields?.Pronunciation)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                result.Add(new(draft.Id, parent?.Id ?? draft.Id, lookup, pronunciation, draft.Original.ContextText, source?.Original.ContextText,
                    audioText, settings, path, audio?.SourceDraftId ?? owner.Id, canPlay, hasCard));
            }
            return new(result.OrderByDescending(c => drafts[c.SourceId].Original.CapturedAt).ThenBy(c => c.SourceId, StringComparer.Ordinal).ToArray());
        }
    }

    private ClipDraft? AudioSource(ClipDraft owner)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (owner.ReusedSpeech is { } reference)
        {
            if (!visited.Add(owner.Id) || !drafts.TryGetValue(reference.SourceDraftId, out var source)) return null;
            var sourceReference = source.ReusedSpeech;
            if (!string.Equals(reference.AudioPath, sourceReference?.AudioPath ?? source.AudioPath, StringComparison.OrdinalIgnoreCase) ||
                reference.Text != (sourceReference?.Text ?? source.Original.SelectedText) ||
                StreamingSpeech.AudioCacheKey(reference.Text, SpeechPurpose.Selection, reference.Settings) !=
                StreamingSpeech.AudioCacheKey(sourceReference?.Text ?? source.Original.SelectedText, SpeechPurpose.Selection, sourceReference?.Settings ?? source.Settings)) return null;
            owner = source;
        }
        return owner;
    }

    private ClipDraft? ValidParent(ClipDraft child)
    {
        if (child.ModeParentId == null || child.ParentDraftId != child.ModeParentId || child.ModeId == null ||
            !drafts.TryGetValue(child.ModeParentId, out var parent) ||
            !parent.ModeCards.TryGetValue(child.ModeId, out var id) || id != child.Id ||
            child.Route?.ModeId != child.ModeId || child.Route.OutputKind != "language" ||
            child.Original != parent.Original) return null;
        return parent;
    }

    private bool ValidAudio(string text, LanguageSettings settings, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var file = new FileInfo(path);
            return Path.IsPathFullyQualified(path) && file.Exists && file.Length > 0 &&
                (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                file.Name.EndsWith("-" + StreamingSpeech.AudioCacheKey(text, SpeechPurpose.Selection, settings) + ".mp3", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool HasCard(ClipDraft draft) => draft.ConfirmedNote != null || draft.ConfirmedRemNote != null || draft.NoteId != null ||
        draft.ImportState != ImportState.Unconfirmed || draft.PreviewContent?.Fields is { IsUnresolved: false };
}

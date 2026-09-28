using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed partial class LanguageDesktopFlow
{
    private readonly LanguageHistoryLookup historyLookup;
    private MeaningPopup meaningPopup;
    private string meaningDraftId;
    private Point meaningAnchor;
    private long languageEntryEpoch;
    private readonly SemaphoreSlim historyEntryGate = new(1, 1);
    private readonly HashSet<string> pendingHistoryEntries = new(StringComparer.Ordinal);

    internal void HideMeaning() { meaningDraftId = null; meaningPopup?.HideMeaning(); }

    private void BeginMeaningPopup(ClipDraft draft, Point anchor)
    {
        if (meaningPopup == null)
        {
            meaningPopup = new MeaningPopup();
            meaningPopup.ReplayRequested += ReplayLastLanguage;
        }
        meaningDraftId = draft.Id; meaningAnchor = anchor;
        meaningPopup.ShowLoading(anchor);
        PublishMeaning(draft);
    }

    private void PublishMeaning(ClipDraft draft)
    {
        if (owner.IsDisposed || meaningDraftId != draft.Id || meaningPopup?.IsRequestVisible != true) return;
        if (draft.Lookup != null) meaningPopup.ShowMeaning(draft.Lookup.MeaningZh, meaningAnchor);
        else if (draft.LookupError != null) meaningPopup.ShowError(draft.LookupError, meaningAnchor);
    }

    private async Task<LanguageLookupResult> ResolveMeaningAsync(ClipDraft draft, LanguageHistoryMatch match)
    {
        try
        {
            var context = draft.Original.LookupContextText ?? draft.Original.ContextText;
            if (string.IsNullOrWhiteSpace(context)) { draft.LookupError = "未识别到当前句子"; return null; }
            var cached = match.Candidates.Select(item => item.Lookup)
                .FirstOrDefault(item => item != null && item.ContextText == context);
            if (cached != null) draft.Lookup = cached;
            else
            {
                var overridePath = Path.Combine(ModeOverrides, "lookup-meaning.md");
                var prompt = File.ReadAllText(File.Exists(overridePath) ? overridePath : Path.Combine(ModeDefaults, "lookup-meaning.md"));
                var content = new ContentModule(http, () => ContentCredentials.Resolve(DesktopCredentialSettings.Load(dataRoot).DeepSeekApiKey));
                draft.Lookup = await content.LookupMeaningAsync(draft.Original, draft.Settings, prompt, lifetime.Token);
            }
            draft.LookupError = null;
            return draft.Lookup;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        catch (Exception) { draft.LookupError = "暂时无法获取本句词义"; return null; }
        finally
        {
            if (!lifetime.IsCancellationRequested && !owner.IsDisposed)
            {
                store.Save(draft); historyLookup.Remember(draft); PublishMeaning(draft);
            }
        }
    }

    private static string PronunciationKey(string value) => new string((value ?? "").Where(c => !char.IsWhiteSpace(c) && c is not ('/' or '[' or ']')).ToArray());

    private async Task PlayEntryAsync(ClipDraft draft, LanguageHistoryMatch match, Task<LanguageLookupResult> meaning, long epoch)
    {
        if (!draft.Input.ReadRequested) return;
        // Reserve playback before any content task can try to start it a second time.
        draft.Times["speechRequested"] = DateTimeOffset.UtcNow;
        var candidates = match.Candidates.Where(item => item.CanPlay).ToArray();
        LanguageHistoryCandidate recorded = null;
        if (!draft.Settings.ContextEnabled) recorded = candidates.FirstOrDefault();
        else
        {
            recorded = candidates.FirstOrDefault(item => !string.IsNullOrWhiteSpace(draft.Original.ContextText) &&
                item.AudioContextText == draft.Original.ContextText);
            if (recorded == null && candidates.Length > 0)
            {
                var currentMeaning = await meaning;
                if (currentMeaning?.Pronunciation != null)
                    recorded = candidates.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Pronunciation) &&
                        PronunciationKey(item.Pronunciation) == PronunciationKey(currentMeaning.Pronunciation));
            }
        }
        if (epoch != languageEntryEpoch || lifetime.IsCancellationRequested)
        { draft.SpeechStatus = "已由后续朗读替代"; return; }
        if (recorded != null)
        {
            draft.ReusedSpeech = new(recorded.AudioSourceDraftId, recorded.AudioText, recorded.AudioSettings, recorded.AudioPath);
            draft.AudioPath = recorded.AudioPath;
        }
        await PlayAsync(draft, false);
    }

    private async Task CompleteLanguageEntryAsync(ClipDraft draft, bool quick, LanguageDraftForm form, long epoch)
    {
        try
        {
            LanguageHistoryMatch match;
            await historyEntryGate.WaitAsync(lifetime.Token);
            try
            {
                var found = await historyLookup.FindAsync(draft.Original.SelectedText, draft.Id, lifetime.Token);
                // The startup scan may already contain later saved selections. Only
                // registered predecessors count as history for an in-flight entry.
                match = new(found.Candidates.Where(item => !pendingHistoryEntries.Contains(item.SourceId) &&
                    !pendingHistoryEntries.Contains(item.SessionId)).ToArray());
                draft.MatchedHistoryId = match.Candidates.FirstOrDefault()?.SourceId;
                draft.HistoryReplayOnly = quick && draft.MatchedHistoryId != null;
                historyLookup.Remember(draft);
                pendingHistoryEntries.Remove(draft.Id);
            }
            finally { historyEntryGate.Release(); }
            var needMeaning = draft.Input.Source == "selection-dot" || draft.Settings.ContextEnabled && match.Candidates.Any(item => item.CanPlay);
            var meaning = needMeaning ? ResolveMeaningAsync(draft, match) : Task.FromResult<LanguageLookupResult>(null);
            var playback = PlayEntryAsync(draft, match, meaning, epoch);
            store.Save(draft);
            if (!quick) { _ = ProcessAsync(draft, form); await playback; }
            else if (draft.HistoryReplayOnly)
            {
                // A fresh session stores this selection/context. It owns no copied card identity.
                await playback;
                if (draft.SpeechError != null) throw new InvalidOperationException(draft.SpeechError);
                status("已复用历史记录；可从历史继续制卡");
            }
            else await CompleteQuickAsync(draft, playback);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (owner.IsDisposed || lifetime.IsCancellationRequested) return;
            draft.ImportError = ex.Message;
            if (quick) OpenDraft(draft).ContentReady();
            else form?.RefreshStatus();
        }
        finally
        {
            pendingHistoryEntries.Remove(draft.Id);
            quickDrafts.Remove(draft.Id);
            if (!owner.IsDisposed && !lifetime.IsCancellationRequested) Changed(draft, null);
        }
    }
}

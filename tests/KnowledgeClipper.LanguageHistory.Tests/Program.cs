using KnowledgeClipper.Workflow;

var root = @"D:\Caches\KnowledgeClipper\language-history-tests\" + Guid.NewGuid().ToString("N");
Directory.CreateDirectory(root);
var store = new DraftStore(root);
var configuration = new ContentConfiguration("fixture", []);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
ClipDraft Draft(string word = "lead", string context = "They lead the team.")
{
    var material = new CaptureMaterial(word, context, "fixture", 0, "fixture", 0, DateTimeOffset.UtcNow);
    return new() { Original = material, Material = material, Settings = new(), Configuration = configuration, Input = new(Intent: "language") };
}
void Audio(ClipDraft draft, SpeechPurpose purpose = SpeechPurpose.Selection)
{
    draft.AudioPath = Path.Combine(root, draft.Id + "-" + StreamingSpeech.AudioCacheKey(draft.Original.SelectedText, purpose, draft.Settings) + ".mp3");
    File.WriteAllBytes(draft.AudioPath, [1, 2, 3]);
}
var original = Draft(); original.Lookup = new("带领", "/liːd/", original.Original.ContextText); Audio(original); store.Save(original);
var index = new LanguageHistoryLookup(root);
var early = Draft("early"); index.Remember(early);
await index.Ready;
Check((await index.FindAsync("early")).Candidates.Count == 1, "Remember before initial scan completion survives publication");
var match = await index.FindAsync("  LEAD  ");
Check(match.HasPlayableAudio && match.Candidates.Single().Lookup?.MeaningZh == "带领", "normalized word hit preserves current Chinese lookup");
Check(!(await index.FindAsync("lead", original.Id)).Candidates.Any(), "exclude own draft");
var translation = Draft("lead"); translation.ModeId = "translation"; translation.Content = new("translation", null); store.Save(translation);
var late = Draft("later"); store.Save(late);
Check(!(await index.FindAsync("later")).Candidates.Any(), "click does not scan new disk records");
await index.RefreshAsync();
Check((await index.FindAsync("later")).Candidates.Count == 1 && (await index.FindAsync("lead")).Candidates.Count == 1, "explicit refresh loads disk changes and excludes translation");
var wrongPurpose = Draft("purpose"); Audio(wrongPurpose, SpeechPurpose.Sentence); index.Remember(wrongPurpose);
Check(!(await index.FindAsync("purpose")).HasPlayableAudio, "sentence cache cannot pass as selected-word audio");
var missing = Draft("missing"); missing.Content = new("language", new("/m/", "old English explanation", null, null)); missing.AudioPath = Path.Combine(root, "missing.mp3"); index.Remember(missing);
match = await index.FindAsync("missing");
Check(match.HasCard && !match.HasPlayableAudio && match.Candidates.Single().Lookup == null, "card dedup survives missing audio without promoting English meaning");
var reused = Draft("lead", "The lead pipe is heavy."); reused.Lookup = new("铅", "/lɛd/", reused.Original.ContextText);
reused.ReusedSpeech = new(original.Id, original.Original.SelectedText, original.Settings, original.AudioPath!); index.Remember(reused);
var candidate = (await index.FindAsync("lead")).Candidates.Single(c => c.SourceId == reused.Id);
Check(candidate.CanPlay && candidate.Lookup?.MeaningZh == "铅" && candidate.Pronunciation == "/liːd/" && candidate.AudioContextText == original.Original.ContextText && candidate.ContextText == reused.Original.ContextText, "forced reuse retains source pronunciation and audio context separately from current lookup");
reused.ReusedSpeech = reused.ReusedSpeech with { SourceDraftId = Guid.NewGuid().ToString("N") }; index.Remember(reused);
candidate = (await index.FindAsync("lead")).Candidates.Single(c => c.SourceId == reused.Id);
Check(candidate.Pronunciation == null && candidate.AudioContextText == null, "unavailable source cannot claim current lookup describes its audio");
File.WriteAllBytes(original.AudioPath!, []);
Check(!(await index.FindAsync("lead")).HasPlayableAudio, "query detects deleted or empty cached audio");
var parent = Draft("bind"); Audio(parent);
var child = new ClipDraft { Original = parent.Original, Material = parent.Material, Settings = parent.Settings, Configuration = configuration,
    Input = new(Intent: "language"), ModeId = "en-reading", ModeParentId = parent.Id, ParentDraftId = parent.Id,
    Route = new("en-reading", "language", false, false), Content = new("en-reading", new("/baɪnd/", "English only", null, null)) };
parent.ModeCards[child.ModeId] = child.Id; index.Remember(parent); index.Remember(child);
match = await index.FindAsync("bind");
Check(match.Candidates.All(c => c.HasCard && c.CanPlay && c.SessionId == parent.Id), "bidirectional mode binding combines parent audio and child card");
parent.ModeCards.Clear(); index.Remember(parent);
match = await index.FindAsync("bind");
Check(!match.Candidates.Single(c => c.SourceId == parent.Id).HasCard && !match.Candidates.Single(c => c.SourceId == child.Id).CanPlay, "broken binding cannot borrow card or audio");
using var canceled = new CancellationTokenSource(); canceled.Cancel();
try { await index.FindAsync("lead", ct: canceled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { Check(true, "cancellation honored"); }
Console.WriteLine($"PASS {checks} checks; fixture root: {root}");

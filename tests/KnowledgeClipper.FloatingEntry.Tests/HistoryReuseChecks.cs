using System.Collections;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static async Task HistoryReuseAsync(Form owner)
    {
        await PendingHistoryEntriesAsync(owner);
        foreach (var scenario in new[] { (Name: "context-off", Context: false, Pronunciation: "/lɛd/", Quick: true),
            (Name: "context-on-same-sound", Context: true, Pronunciation: "/liːd/", Quick: true),
            (Name: "context-on-other-sound", Context: true, Pronunciation: "/lɛd/", Quick: true),
            (Name: "context-on-superseded", Context: true, Pronunciation: "/liːd/", Quick: true),
            (Name: "context-on-translation-superseded", Context: true, Pronunciation: "/liːd/", Quick: true),
            (Name: "main-language-panel", Context: false, Pronunciation: "/liːd/", Quick: false) })
        {
            var root = Path.Combine(dataRoot, scenario.Name); Directory.CreateDirectory(root);
            using var importer = new QuickLanguageImportFixture();
            var settings = Settings() with { ContextEnabled = scenario.Context, QuickLanguage = true, LanguageAutoRead = true,
                SpeechAutoPlay = true, PythonPath = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe",
                RemNoteSdkEndpoint = importer.Url, ModeDestinations = new() { ["language-en"] = ExportDestination.RemNote } };
            var config = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"));
            var store = new DraftStore(root);
            var oldMaterial = new CaptureMaterial("lead", "They lead the team.", "source", 5, "Public reuse fixture", 0, DateTimeOffset.UtcNow.AddDays(-1));
            var old = new ClipDraft { Original = oldMaterial, Material = oldMaterial, Settings = settings, Configuration = config,
                Input = new("selection-dot", "language"), ModeId = "language", Lookup = new("带领", "/liːd/", oldMaterial.ContextText),
                Content = new("language", new LanguageFields("/liːd/", "带领", null, null)) };
            var audioRoot = Path.Combine(root, "audio"); Directory.CreateDirectory(audioRoot);
            old.AudioPath = Path.Combine(audioRoot, old.Id + "-" + StreamingSpeech.AudioCacheKey("lead", SpeechPurpose.Selection, settings) + ".mp3");
            File.WriteAllBytes(old.AudioPath, Encoding.UTF8.GetBytes("ID3 preserved public audio fixture"));
            var oldBytes = File.ReadAllBytes(old.AudioPath); store.Save(old);
            WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
            using var flow = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
            ((StreamingSpeech)flow.GetType().GetField("speech", Flags)!.GetValue(flow)!).Dispose();
            var helper = Path.Combine(root, "recorded-fixture.py"); File.WriteAllText(helper, RecordedEntryHelper);
            using var speech = new StreamingSpeech(helper, audioRoot); Field(flow, "speech", speech); speech.Prepare(settings); await speech.PreparationReady;
            using var provider = new ReuseMeaningProvider(scenario.Pronunciation); using var http = new HttpClient(provider);
            ((HttpClient)flow.GetType().GetField("http", Flags)!.GetValue(flow)!).Dispose(); Field(flow, "http", http);
            var lookup = (LanguageHistoryLookup)flow.GetType().GetField("historyLookup", Flags)!.GetValue(flow)!; await lookup.Ready;
            var sentence = scenario.Pronunciation == "/lɛd/" ? "The pipe contains lead." : "We lead a new team.";
            var material = new CaptureMaterial("lead", scenario.Context ? sentence : null, scenario.Context ? "source" : "generated_allowed",
                scenario.Context ? sentence.IndexOf("lead", StringComparison.Ordinal) : null, "Public reuse fixture", 0, DateTimeOffset.UtcNow)
                { LookupContextText = sentence };
            var draft = (ClipDraft)Call(flow, "StartMaterial", material, new InputMetadata(scenario.Quick ? "selection-dot" : "selection-button", "language", ReadRequested: true),
                settings, config, DateTimeOffset.UtcNow, scenario.Quick)!;
            if (scenario.Quick) Call(flow, "BeginMeaningPopup", draft, new Point(250, 200));
            var requests = Path.Combine(root, "requests.jsonl");
            if (!scenario.Context)
            {
                await Until(() => File.Exists(requests), "recorded playback starts before pending contextual meaning completes", 5000);
                Check(!provider.Released, "context-disabled reuse never waits for meaning response");
            }
            else
            {
                await Until(() => provider.MeaningRequests > 0, "context-sensitive reading requests the current meaning and pronunciation");
                await Task.Delay(120);
                Check(!File.Exists(requests), "different source context is checked before selecting an old recording");
            }
            if (scenario.Name is "context-on-superseded" or "context-on-translation-superseded")
            {
                var translation = scenario.Name == "context-on-translation-superseded";
                var newer = (ClipDraft)Call(flow, "StartMaterial", material, new InputMetadata("selection-button", translation ? "translation" : "language", ReadRequested: true),
                    settings with { ContextEnabled = false }, config, DateTimeOffset.UtcNow, !translation)!;
                await Until(() => newer.Times.ContainsKey("playbackComplete"), "a later context-disabled click plays while the older lookup is pending");
                provider.Released = true; provider.Gate.TrySetResult();
                await Until(() => draft.SpeechStatus == "已由后续朗读替代", "late lookup cannot steal playback from the newer click");
                Check(File.ReadLines(requests).Count() == 1 && !draft.Times.ContainsKey("playbackComplete"), "only the latest click sends a playback request");
                Call(flow, "HideMeaning");
                foreach (var form in Forms(flow)) form.Close();
                continue;
            }
            provider.Released = true; provider.Gate.TrySetResult();
            await Until(() => draft.Times.ContainsKey("playbackComplete"), scenario.Name + ": playback completes", 8000);
            var request = JsonDocument.Parse(File.ReadLines(requests).Single()).RootElement;
            var shouldReuse = !scenario.Context || scenario.Pronunciation == "/liːd/";
            Check(request.GetProperty("replay").GetBoolean() == shouldReuse, scenario.Name + ": replay or synthesis follows the context rule");
            Check(File.ReadAllBytes(old.AudioPath).SequenceEqual(oldBytes), "old recording bytes are never overwritten");
            if (scenario.Quick)
            {
                await Until(() => draft.Lookup != null, "new contextual Chinese meaning is stored independently of old audio");
                await Until(() => !((IDictionary)flow.GetType().GetField("quickDrafts", Flags)!.GetValue(flow)!).Contains(draft.Id), "repeat visit finishes without card generation");
                Check(Forms(flow).Length == 0 && importer.ExportCount == 0 && provider.Inner.LanguageTargets.Count == 0,
                    "a repeated floating read opens no card preview and sends no card-generation or import request");
                Check(draft.Id != old.Id && draft.HistoryReplayOnly && draft.ModeCards.Count == 0 && draft.ConfirmedNote == null && draft.MatchedHistoryId == old.Id,
                    "repeat selection owns a fresh history entry with no copied card identity");
                Check(draft.Lookup!.MeaningZh == "本句中的具体含义" && draft.Lookup.ContextText == sentence,
                    "popup stores the current sentence meaning rather than old sentence meaning");
                var popup = (Form)flow.GetType().GetField("meaningPopup", Flags)!.GetValue(flow)!;
                Check(popup.Visible, "the small Chinese meaning popup is shown beside the selection");
                Save(popup, scenario.Name + "-meaning.png");
                Call(flow, "ContinueHistory", draft);
                await Until(() => provider.Inner.LanguageTargets.Count > 0, "continue from the repeat history entry explicitly generates its cards");
                Check(Forms(flow).Length == 1 && ReferenceEquals(Get(Forms(flow).Single(), "Draft"), draft), "manual continuation opens the same repeat draft");
                if (shouldReuse)
                {
                    File.Move(old.AudioPath, old.AudioPath + ".unavailable");
                    Call(flow, "PlayHistory", draft);
                    Check(draft.SpeechError != null && draft.SpeechStatus == "历史录音不可用", "missing reused audio becomes a draft error rather than escaping the history click");
                    Check(File.ReadLines(requests).Count() == 1, "missing recorded audio does not silently synthesize another recording");
                }
            }
            else
            {
                await Until(() => provider.Inner.LanguageTargets.Count > 0, "main language entry still generates cards in its panel");
                Check(Forms(flow).Length == 1 && !draft.HistoryReplayOnly && importer.ExportCount == 0,
                    "main language panel remains manual even when recorded audio is reused");
            }
            foreach (var form in Forms(flow)) form.Close();
            Call(flow, "HideMeaning");
        }
    }

    private static async Task PendingHistoryEntriesAsync(Form owner)
    {
        foreach (var scanPending in new[] { false, true })
        {
            var root = Path.Combine(dataRoot, "pending-repeat-" + scanPending); Directory.CreateDirectory(root);
            using var importer = new QuickLanguageImportFixture();
            var settings = Settings() with { ContextEnabled = false, LanguageAutoRead = false, QuickLanguage = true, Practice = PracticeChoice.Reading,
                RemNoteSdkEndpoint = importer.Url, ModeDestinations = new() { ["language-en"] = ExportDestination.RemNote } };
            WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
            using var flow = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
            using var provider = new ReuseMeaningProvider("/liːd/", holdContent: true); using var http = new HttpClient(provider);
            ((HttpClient)flow.GetType().GetField("http", Flags)!.GetValue(flow)!).Dispose(); Field(flow, "http", http);
            var lookup = (LanguageHistoryLookup)flow.GetType().GetField("historyLookup", Flags)!.GetValue(flow)!;
            await lookup.Ready;
            var gate = (System.Threading.SemaphoreSlim)flow.GetType().GetField("historyEntryGate", Flags)!.GetValue(flow)!;
            if (scanPending) await gate.WaitAsync();
            var config = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"));
            var material = new CaptureMaterial("lead", null, "generated_allowed", null, "Pending repeat fixture", 0, DateTimeOffset.UtcNow)
                { LookupContextText = "They lead the team." };
            ClipDraft StartPending() => (ClipDraft)Call(flow, "StartMaterial", material, new InputMetadata("selection-dot", "language"),
                settings, config, DateTimeOffset.UtcNow, true)!;
            var first = StartPending();
            var second = StartPending();
            if (scanPending)
            {
                // Deterministically simulate the initial background scan seeing both
                // persisted drafts before either history decision has completed.
                await lookup.RefreshAsync(); gate.Release();
            }
            await Until(() => second.HistoryReplayOnly && provider.MeaningRequests == 2, "pending same-word entry is registered before content or meaning completes");
            var matchedSession = second.MatchedHistoryId == first.Id || new DraftStore(root).Find(second.MatchedHistoryId!)?.ModeParentId == first.Id;
            Check(!first.HistoryReplayOnly && first.MatchedHistoryId == null && matchedSession,
                "only the later pending selection is classified as history, including when both are already scanned");
            Check(first.ModeCards.Count > 0 && second.ModeCards.Count == 0 && second.Content == null,
                "a repeated pending word starts no second card generation");
            provider.Released = true; provider.Gate.TrySetResult();
            await Until(() => !((IDictionary)flow.GetType().GetField("quickDrafts", Flags)!.GetValue(flow)!).Contains(first.Id),
                "the first pending word finishes its single card flow", 12000);
            Check(importer.ExportCount == first.ModeCards.Count && importer.ExportCount > 0,
                "only the first pending selection exports cards");
            var store = new DraftStore(root);
            Check(store.Find(first.Id) != null && store.Find(second.Id)?.HistoryReplayOnly == true,
                "both pending selections retain independent history records");
            foreach (var form in Forms(flow)) form.Close();
        }
    }

    private sealed class ReuseMeaningProvider(string pronunciation, bool holdContent = false) : HttpMessageHandler
    {
        internal readonly ImageRouteProvider Inner = new();
        internal readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int MeaningRequests;
        internal bool Released;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                using var read = new HttpMessageInvoker(Inner, false);
                return await read.SendAsync(request, ct);
            }
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (body.Contains("meaning_zh", StringComparison.Ordinal))
            {
                MeaningRequests++; await Gate.Task.WaitAsync(ct);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
                    new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { meaning_zh = "本句中的具体含义", pronunciation }) } } } })) };
            }
            using var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("\"translation\"", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
                    new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { translation = "Guide the team." }) } } } })) };
            if (holdContent) await Gate.Task.WaitAsync(ct);
            using var invoker = new HttpMessageInvoker(Inner, false);
            return await invoker.SendAsync(request, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) { Gate.TrySetCanceled(); Inner.Dispose(); } base.Dispose(disposing); }
    }

    private const string RecordedEntryHelper = """
        import json, pathlib, sys
        root = pathlib.Path(__file__).parent
        if '--prepare' in sys.argv:
            print(json.dumps({'event':'helperReady'}), flush=True)
        body = sys.stdin.read()
        if not body:
            sys.exit(0)
        req = json.loads(body)
        with (root / 'requests.jsonl').open('a', encoding='utf8') as output:
            output.write(json.dumps(req)+'\n')
        if not req['replay']:
            pathlib.Path(req['audio']).write_bytes(b'ID3 newly synthesized offline fixture')
        for name in (('playbackComplete',) if req['replay'] else ('audioComplete','playbackComplete')):
            print(json.dumps({'event':name}), flush=True)
        """;
}

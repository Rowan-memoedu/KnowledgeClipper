using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static async Task MeaningReplayAsync(Form owner)
    {
        foreach (var scenario in new[] { "early", "bound", "hidden", "missing", "reused", "superseded" })
        {
            var root = Path.Combine(dataRoot, scenario); Directory.CreateDirectory(root);
            var settings = Settings() with { ContextEnabled = false, LanguageAutoRead = true, SpeechAutoPlay = true,
                PythonPath = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe",
                Voice = "Mia", EnglishVoice = "Mia", RatePercent = -3, VolumePercent = -18 };
            var config = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"));
            var audioRoot = Path.Combine(root, "audio"); Directory.CreateDirectory(audioRoot);
            var store = new DraftStore(root);
            var material = new CaptureMaterial("patience", "Practice patience today.", "source", 9, "Public audio fixture", 0, DateTimeOffset.UtcNow);
            ClipDraft? original = null;
            if (scenario == "reused")
            {
                var oldSettings = settings with { Voice = "Dylan", EnglishVoice = "Dylan", RatePercent = 20 };
                original = new ClipDraft { Original = material, Material = material, Settings = oldSettings,
                    Configuration = config, Input = new("selection-dot", "language"), ModeId = "language" };
                original.AudioPath = Path.Combine(audioRoot, original.Id + "-" + StreamingSpeech.AudioCacheKey("patience", SpeechPurpose.Selection, oldSettings) + ".mp3");
                File.WriteAllBytes(original.AudioPath, "ID3 old public recording"u8.ToArray()); store.Save(original);
            }
            WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
            using var flow = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
            ((StreamingSpeech)flow.GetType().GetField("speech", Flags)!.GetValue(flow)!).Dispose();
            var helper = Path.Combine(root, "meaning-replay.py"); File.WriteAllText(helper, MeaningReplayHelper);
            using var speech = new StreamingSpeech(helper, audioRoot); Field(flow, "speech", speech);
            speech.Prepare(settings); await speech.PreparationReady;
            using var provider = new ReuseMeaningProvider("/test/", holdContent: true);
            using var http = new HttpClient(provider);
            ((HttpClient)flow.GetType().GetField("http", Flags)!.GetValue(flow)!).Dispose(); Field(flow, "http", http);
            await ((LanguageHistoryLookup)flow.GetType().GetField("historyLookup", Flags)!.GetValue(flow)!).Ready;
            var draft = (ClipDraft)Call(flow, "StartMaterial", material, new InputMetadata("selection-dot", "language", ReadRequested: true),
                settings, config, DateTimeOffset.UtcNow, false)!;
            Call(flow, "BeginMeaningPopup", draft, new Point(250, 200));
            var popup = flow.GetType().GetField("meaningPopup", Flags)!.GetValue(flow)!;
            void Click() => popup.GetType().GetMethod("OnMouseClick", Flags)!.Invoke(popup,
                [new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)]);
            var log = Path.Combine(root, "requests.jsonl");
            int Count() => File.Exists(log) ? File.ReadAllLines(log).Length : 0;
            JsonElement[] Requests() => File.ReadAllLines(log).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
            await Until(() => Count() == 1, scenario + ": initial playback reaches helper");
            Check(Requests()[0].GetProperty("losslessReplay").GetBoolean(), scenario + ": requests lossless replay transport");
            Check(Requests()[0].GetProperty("preconnect").GetBoolean(), scenario + ": first reading enables the prepared transport");
            if (scenario is "early" or "hidden" or "superseded")
            {
                Click(); Click(); await Task.Delay(150);
                Check(Count() == 1 && !draft.Times.ContainsKey("playbackComplete"), scenario + ": early repeated clicks preserve the initial synthesis");
                if (scenario == "hidden") Call(flow, "HideMeaning");
                if (scenario == "superseded")
                {
                    var next = material with { SelectedText = "courage", ContextText = "Practice courage today." };
                    Call(flow, "StartMaterial", next, new InputMetadata("selection-button", "language", ReadRequested: true), settings, config, DateTimeOffset.UtcNow, false);
                    await Until(() => Count() == 2, "new reading supersedes the queued replay");
                }
                File.WriteAllText(Path.Combine(root, "release"), "ready");
                if (scenario == "early") await Until(() => Count() == 2 && draft.SpeechStatus == "朗读完成，可重播", "queued replay uses the saved recording");
                else
                {
                    await Task.Delay(450);
                    Check(Count() == (scenario == "hidden" ? 1 : 2), scenario + ": dismissed or superseded replay never starts later");
                }
            }
            else
            {
                File.WriteAllText(Path.Combine(root, "release"), "ready");
                await Until(() => draft.SpeechStatus == "朗读完成，可重播", scenario + ": first reading finishes");
                if (scenario == "missing")
                {
                    File.Delete(draft.AudioPath!); Click();
                    await Until(() => draft.SpeechError != null, "missing first recording gives an explicit error");
                    Check(Count() == 1, "missing recording never causes another synthesis");
                }
                else
                {
                    // Another global replay target must not change the popup's recording.
                    Field(flow, "lastSpeech", new ClipDraft { Original = material with { SelectedText = "different" },
                        Material = material, Settings = settings, Configuration = config });
                    Click(); await Until(() => Count() == 2 && draft.SpeechStatus == "朗读完成，可重播", scenario + ": popup replays its own recording");
                }
            }
            if (scenario is "early" or "bound" or "reused")
            {
                var requests = Requests();
                Check(requests[1].GetProperty("replay").GetBoolean() && requests[1].GetProperty("audio").GetString() == draft.AudioPath,
                    scenario + ": second request uses the exact saved recording path");
                Check(requests[1].GetProperty("text").GetString() == "patience", scenario + ": global lastSpeech does not redirect the meaning click");
                if (original != null)
                    Check(requests.All(r => r.GetProperty("voice").GetString() == "Dylan" && r.GetProperty("rate").GetInt32() == 20) && draft.AudioPath == original.AudioPath,
                        "history reuse preserves the actual source voice, settings and file");
                else
                    Check(File.ReadAllBytes(draft.AudioPath! + ".pcm").SequenceEqual("public raw PCM fixture"u8.ToArray()),
                        scenario + ": original PCM sidecar is published with the MP3");
            }
            Call(flow, "HideMeaning"); foreach (var form in Forms(flow)) form.Close();
        }
    }

    private const string MeaningReplayHelper = """
        import json, pathlib, sys, time
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
            print(json.dumps({'event':'firstAudioChunk'}), flush=True)
            while not (root / 'release').exists():
                time.sleep(.02)
            pathlib.Path(req['audio']).write_bytes(b'ID3 original audio fixture')
            pathlib.Path(req['audio']+'.pcm').write_bytes(b'public raw PCM fixture')
            print(json.dumps({'event':'audioComplete'}), flush=True)
        print(json.dumps({'event':'playbackComplete'}), flush=True)
        """;
}

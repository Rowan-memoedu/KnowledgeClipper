using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static async Task QuickLanguageSpeechAsync(Form owner)
    {
        Check(new LanguageSettings().QuickLanguage && new LanguageSettings().QuickExcerpt &&
            !new LanguageSettings().QuickQuestion && !new LanguageSettings().QuickScreenshot,
            "language quick import is enabled by default without changing other floating defaults");
        var explicitOff = JsonSerializer.Deserialize<LanguageSettings>("{\"quickLanguage\":false}", WorkflowJson.Options)!;
        Check(!explicitOff.QuickLanguage, "explicit saved language quick opt-out overrides the new default");
        foreach (var test in new[] { (Name: "quick-speech-success", Quick: true, Read: true, FailSpeech: false, Prepared: false),
            (Name: "quick-speech-failure", Quick: true, Read: true, FailSpeech: true, Prepared: false),
            (Name: "normal-preview-speech", Quick: false, Read: true, FailSpeech: false, Prepared: false),
            (Name: "prepared-quick-speech", Quick: true, Read: true, FailSpeech: false, Prepared: true),
            (Name: "prepared-quick-muted", Quick: true, Read: false, FailSpeech: false, Prepared: true) })
        {
            var root = Path.Combine(dataRoot, test.Name); Directory.CreateDirectory(root);
            using var importer = new QuickLanguageImportFixture();
            var settings = Settings() with { QuickLanguage = test.Quick, LanguageAutoRead = test.Read, SpeechAutoPlay = true,
                PythonPath = @"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\python.exe",
                Practice = PracticeChoice.Reading, RemNoteSdkEndpoint = importer.Url,
                ModeDestinations = new() { ["language-en"] = ExportDestination.RemNote } };
            WorkflowJson.Save(Path.Combine(root, "language-settings.json"), settings);
            using var language = (IDisposable)New("LanguageDesktopFlow", owner, root, (Action<string>)statuses.Add, Initial);
            ((StreamingSpeech)language.GetType().GetField("speech", Flags)!.GetValue(language)!).Dispose();
            var helper = Path.Combine(root, "speech-fixture.py"); File.WriteAllText(helper, SpeechFixture);
            if (test.FailSpeech) File.WriteAllText(Path.Combine(root, "fail-speech"), "deliberate fixture failure");
            var audioRoot = Path.Combine(root, "audio");
            using var speech = new StreamingSpeech(helper, audioRoot); Field(language, "speech", speech);
            using var provider = new ImageRouteProvider(); using var contentHttp = new HttpClient(provider);
            ((HttpClient)language.GetType().GetField("http", Flags)!.GetValue(language)!).Dispose(); Field(language, "http", contentHttp);
            var configuration = ModeConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "modes"), Path.Combine(root, "modes"));
            var store = new DraftStore(root);
            var allWindows = new HashSet<Form>(); var samples = 0;
            void Observe()
            {
                samples++;
                foreach (var form in Application.OpenForms.Cast<Form>().Where(form => !ReferenceEquals(form, owner)).Concat(Forms(language)))
                    allWindows.Add(form);
            }
            using var sampler = new System.Windows.Forms.Timer { Interval = 15 };
            sampler.Tick += (_, _) => Observe(); sampler.Start();
            try
            {
                ClipDraft draft;
                if (test.Prepared)
                {
                    var id = (string)Call(language, "StartPreparedMaterial", Material("lead"), settings, "language", test.Quick)!;
                    draft = (ClipDraft)((IDictionary)language.GetType().GetField("quickDrafts", Flags)!.GetValue(language)!)[id]!;
                }
                else draft = (ClipDraft)Call(language, "StartMaterial", Material("lead"),
                    new InputMetadata("selection-dot", "language", ReadRequested: test.Read), settings, configuration, DateTimeOffset.UtcNow, test.Quick)!;
                Observe();
                Check(draft.Input.ReadRequested == test.Read, test.Name + ": the production language entry retains the auto-read preference");
                if (test.Quick) Check(allWindows.Count == 0, test.Name + ": quick processing starts without a preview");
                else Check(Forms(language).Length == 1 && ReferenceEquals(Get(Forms(language).Single(), "Draft"), draft),
                    test.Name + ": disabling quick mode immediately opens the ordinary original preview");
                if (test.Read)
                {
                    var inputPath = Path.Combine(root, "speech-requests.jsonl");
                    await Until(() => File.Exists(inputPath), test.Name + ": production speech helper receives a reading request");
                    using var request = JsonDocument.Parse(File.ReadAllLines(inputPath).Single());
                    Check(request.RootElement.GetProperty("text").GetString() == "lead" && request.RootElement.GetProperty("play").GetBoolean() &&
                        draft.Times.ContainsKey("speechRequested"), test.Name + ": the original word is requested for immediate playback exactly once");
                }
                if (test.Quick)
                {
                    await Until(() => draft.ModeCards.Count == 1 && LanguageModeCards.Children(draft, store).All(child => child.ImportState == ImportState.Verified),
                        test.Name + ": the one configured reading card finishes background import", 12000);
                    Observe();
                    Check(importer.ExportCount == 1 && provider.LanguageTargets.SequenceEqual(new[] { "lead" }) &&
                        LanguageModeCards.Children(draft, store).Single().Practice == PracticeChoice.Reading,
                        test.Name + ": one content request and one default card export are retained");
                    if (test.Read) Check(((IDictionary)language.GetType().GetField("quickDrafts", Flags)!.GetValue(language)!).Contains(draft.Id) && allWindows.Count == 0,
                        test.Name + ": import finishes silently while quick completion still waits for reading");
                }
                else await Until(() => draft.ModeCards.Count == 1 && LanguageModeCards.Children(draft, store).All(child => child.Content != null),
                    test.Name + ": normal preview still generates its configured card");
                File.WriteAllText(Path.Combine(root, "release-speech"), "release");
                if (test.Read) await Until(() => test.FailSpeech ? draft.SpeechError != null : draft.Times.ContainsKey("playbackComplete"),
                    test.Name + ": the production speech result is recorded", 12000);
                if (test.Quick) await Until(() => !((IDictionary)language.GetType().GetField("quickDrafts", Flags)!.GetValue(language)!).Contains(draft.Id),
                    test.Name + ": the quick task completes without abandoning the original draft");
                await Task.Delay(100); Observe();
                if (test.FailSpeech)
                {
                    Check(Forms(language).Length == 1 && ReferenceEquals(Get(Forms(language).Single(), "Draft"), draft) && draft.SpeechError != null,
                        "failed reading returns to the same parent draft preview with its speech error");
                    Check(importer.ExportCount == 1 && LanguageModeCards.Children(draft, store).All(child => child.ImportState == ImportState.Verified),
                        "reading failure preserves the already verified card identity without a duplicate export");
                }
                else if (test.Quick) Check(allWindows.Count == 0 && Forms(language).Length == 0 && draft.ImportError == null,
                    test.Name + ": successful reading and import never open a preview");
                else Check(importer.ExportCount == 0 && LanguageModeCards.Children(draft, store).All(child => child.ImportState == ImportState.Unconfirmed),
                    "disabling quick mode preserves manual import and does not affect automatic reading");
                if (!test.Read) Check(!File.Exists(Path.Combine(root, "speech-requests.jsonl")) && draft.AudioPath == null &&
                    !draft.Times.ContainsKey("speechRequested"), "disabling automatic reading keeps quick import without requesting audio");
                WorkflowJson.Save(Path.Combine(output, test.Name + ".json"), new { passed = true, draft.Id, draft.Input, draft.SpeechError,
                    exports = importer.ExportCount, languageRequests = provider.LanguageTargets.Count, windowSamples = samples,
                    windowCount = allWindows.Count, cards = LanguageModeCards.Children(draft, store).Select(child => new {
                        child.Id, child.ModeId, child.ImportState, remIds = child.RemNoteResult?.RemIds, child.Practice }).ToArray(),
                    scope = "Production flow and real local helper process; speech bytes, model response and RemNote endpoint are offline fixtures. No audible output, real API, clipboard or user-data write." });
            }
            finally { sampler.Stop(); foreach (var form in Forms(language)) form.Close(); }
        }
    }

    private const string SpeechFixture = """
        import json, pathlib, sys, time
        root = pathlib.Path(__file__).parent
        if '--prepare' in sys.argv:
            print(json.dumps({'event': 'helperReady'}), flush=True)
        payload = sys.stdin.read()
        if not payload:
            sys.exit(0)
        request = json.loads(payload)
        with (root / 'speech-requests.jsonl').open('a', encoding='utf-8') as log:
            log.write(json.dumps({'text': request['text'], 'play': request['play']}) + '\n')
        deadline = time.monotonic() + 25
        while not (root / 'release-speech').exists():
            if time.monotonic() > deadline:
                raise RuntimeError('Fixture speech release timed out')
            time.sleep(0.02)
        if (root / 'fail-speech').exists():
            print('Deliberate offline speech fixture failure', file=sys.stderr)
            sys.exit(7)
        audio = pathlib.Path(request['audio'])
        audio.parent.mkdir(parents=True, exist_ok=True)
        audio.write_bytes(b'ID3-public-offline-fixture')
        for event in ('firstAudioChunk', 'audioComplete', 'playbackComplete'):
            print(json.dumps({'event': event}), flush=True)
        """;

    private sealed class QuickLanguageImportFixture : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        internal string Url { get; }
        internal int ExportCount;
        internal QuickLanguageImportFixture()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Url = "http://127.0.0.1:" + port; listener.Prefixes.Add(Url + "/"); listener.Start(); _ = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    object response;
                    try
                    {
                        var operation = context.Request.Url!.Segments.Last();
                        if (operation == "health") response = new { connected = true, supported = true, acceptWriteOperations = true };
                        else if (operation == "export")
                        {
                            using var input = new StreamReader(context.Request.InputStream);
                            using var payload = JsonDocument.Parse(await input.ReadToEndAsync());
                            var plan = payload.RootElement.GetProperty("plan").Deserialize<RemNoteExportPlan>(WorkflowJson.Options)!;
                            if (plan.Cards.Length != 1 || plan.Cards[0].Kind != "basic") throw new InvalidDataException("Expected exactly one reading card.");
                            ExportCount++; response = BasicReceipt(plan);
                        }
                        else throw new InvalidDataException("Unexpected quick language fixture operation: " + operation);
                    }
                    catch (Exception ex) { context.Response.StatusCode = 500; response = new { error = ex.Message }; }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(response, WorkflowJson.Options);
                    context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, stop.Token); context.Response.Close();
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        private static object BasicReceipt(RemNoteExportPlan plan)
        {
            var card = plan.Cards.Single(); var front = "front-" + plan.SourceId; var review = "review-" + plan.SourceId;
            var children = card.Back.Select((_, index) => "back-" + index + "-" + plan.SourceId).ToArray();
            object[] Text(RemNoteRichPart[] parts) => parts.Select(part => part.Kind == "text" ? (object)part.Text! :
                part.Kind == "latex" ? new { i = "x", text = part.Text, block = part.Block ?? false } :
                throw new InvalidDataException("Only public reading text/math is supported by this fixture.")).ToArray();
            object Node(string id, string parent, RemNoteRichPart[] parts, bool answer) => new {
                remId = id, parentId = parent, text = Text(parts), backText = Array.Empty<string>(), media = Array.Empty<object>(),
                remType = 0, isDocument = false, isCardItem = answer, isExtraCardDetail = false,
                practiceEnabled = !answer, practiceDirection = answer ? "none" : "forward",
                children = answer ? Array.Empty<string>() : children, cardIds = answer ? Array.Empty<string>() : new[] { review },
                cardTypes = answer ? Array.Empty<string>() : new[] { "forward" }
            };
            return new { schemaVersion = plan.SchemaVersion, sourceId = plan.SourceId, rootRemId = "fixture-root", documentRemId = "fixture-language",
                cards = new[] { new { key = card.Key, kind = card.Kind, remIds = new[] { front }, cardIds = new[] { review }, childRemIds = children,
                    nodes = new[] { Node(front, "fixture-language", card.Front, false) }.Concat(card.Back.Select((parts, index) => Node(children[index], front, parts, true))).ToArray() } } };
        }
        public void Dispose() { stop.Cancel(); listener.Close(); stop.Dispose(); }
    }
}

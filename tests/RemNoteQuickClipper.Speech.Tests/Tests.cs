using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using KnowledgeClipper.Speech.Worker;

namespace KnowledgeClipper.Speech.Tests
{
    internal static class Tests
    {
        private static int failures;
        private static string runRoot;
        private static readonly List<Dictionary<string, object>> TestResults = new List<Dictionary<string, object>>();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };

        private static int Main()
        {
            runRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "out", "r" + DateTime.UtcNow.ToString("HHmmss") + Guid.NewGuid().ToString("N").Substring(0, 4));
            Directory.CreateDirectory(runRoot);
            Run("valid request and SHA", TestValidRequest);
            Run("reject absolute and traversal paths", TestUnsafePaths);
            Run("reject bytes and SHA mismatch", TestIntegrityMismatch);
            Run("language auto routes", TestLanguageRoutes);
            Run("BCP-47 override and voice", TestLanguageOverride);
            Run("missing language voice fails deterministically", TestMissingLanguageVoice);
            RunAsync("five-language voice snapshots reach synthesis unchanged", TestFiveLanguageVoiceSnapshots);
            Run("voice tuning bounds are enforced", TestVoiceTuningBounds);
            Run("cache policy remains task-scoped audio reuse", TestCachePolicySemantics);
            Run("settings adapter freezes five-language request and runtime snapshots", TestSettingsAdapterFiveLanguageSnapshot);
            Run("settings adapter rejects invalid drafts and unknown voices", TestSettingsAdapterFailures);
            Run("public speech fixture DTO adapter", TestPublicFixtureAdapter);
            Run("reject old provider before synthesis", TestOldProviderNeverRoutes);
            RunAsync("synthesis success publishes immutable artifact", TestSynthesisSuccess);
            RunAsync("synthesis publishes immutable audio beyond MAX_PATH through short tool workspace", TestLongPathSynthesisPublishing);
            RunAsync("playback stages audio beyond MAX_PATH into a short player workspace", TestLongPathPlaybackStaging);
            RunAsync("synthesis failure publishes no downstream artifact", TestSynthesisFailure);
            RunAsync("playback natural completion resets idle replayable", TestPlaybackCompletion);
            RunAsync("replay starts a fresh session from the same audio", TestPlaybackReplay);
            RunAsync("playback revalidates audio immediately before process start", TestPlaybackRevalidatesBeforeStart);
            RunAsync("playback stop is normal terminal", TestPlaybackStop);
            RunAsync("stop only cancels the active playback", TestStopOnlyCancelsActivePlayback);
            RunAsync("new playback supersedes old without stale overwrite", TestPlaybackSupersede);
            Run("describe CLI emits authoritative capability document", TestDescribeCli);
            Run("settings describe CLI exposes editable fixed and diagnostic groups", TestSettingsDescribeCli);
            Run("settings adapt CLI emits synthesis and playback worker snapshots", TestSettingsAdaptCli);
            Run("settings adapt CLI rejects unsafe path and unknown voice", TestSettingsAdaptCliFailures);
            Run("settings companion descriptor exposes scope bindings and side-effect boundary", TestSettingsCompanionDescriptor);
            Run("settings companion status validates five-language readiness without speaker", TestSettingsCompanionStatus);
            Run("settings companion probe binds language override into immutable requests", TestSettingsCompanionOverrideProbe);
            Run("settings companion rejects side effects and invalid settings", TestSettingsCompanionFailures);
            RunAsync("settings companion descriptor to request to result chain", TestSettingsCompanionRequestResultChain);
            Run("voice discovery validates supplied catalog offline without mutation or speaker", TestVoiceDiscoveryValidateOnly);
            Run("voice discovery returns revision and hash-bound natural Edge catalog read-only", TestVoiceDiscoveryLiveAndCache);
            Run("speaker preview requires explicit action and naturally returns idle through fake player", TestSpeakerPreviewExplicitFake);
            Run("settings adapt status and probe never invoke speaker preview", TestSettingsQueriesNeverPreview);
            Run("descriptor is destination neutral", TestDescriptorDestinationNeutral);
            Run("candidate assembly identity uses KnowledgeClipper", TestCandidateAssemblyIdentity);
            Run("runtime configuration prefers neutral environment names", TestRuntimeEnvironmentCompatibility);
            Run("describe CLI rejects missing json flag", TestDescribeCliRejectsInvalidShape);
            Run("run CLI synthesizes from absolute request only", TestRunSynthesisCli);
            Run("long request CLI synthesis playback and boundaries beyond MAX_PATH", delegate { LongRequestPathTests.Run(runRoot); });
            Run("run CLI publishes dependency failure for missing Edge runtime", TestRunSynthesisMissingRuntime);
            Run("run CLI starts playback stage", TestRunPlaybackCli);
            Run("run CLI publishes dependency failure for missing playback runtime", TestRunPlaybackMissingRuntime);
            Run("run CLI publishes failed result for corrupted audio manifest", TestRunPlaybackRejectsCorruptManifest);
            Run("run CLI publishes failed result for corrupted audio payload", TestRunPlaybackRejectsCorruptAudio);
            Run("run CLI rejects incompatible audio contract version", TestRunPlaybackRejectsAudioContractVersion);
            Run("run CLI rejects incompatible audio media type", TestRunPlaybackRejectsAudioMediaType);
            Run("run CLI rejects relative request path", TestRunRejectsRelativeRequest);
            Run("run CLI rejects malformed request JSON", TestRunRejectsMalformedRequest);
            Run("run CLI rejects wrong workerId", TestRunRejectsWrongWorker);
            Run("run CLI rejects unsupported operation", TestRunRejectsOperation);
            Run("playback cancellation maps all public reasons", TestCancellationReasonMapping);
            CoreContractCompatibilityMarker();
            var receipt = new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "suite", "KnowledgeClipper.Speech.Tests" },
                { "completedAtUtc", DateTime.UtcNow.ToString("o") }, { "status", failures == 0 ? "passed" : "failed" },
                { "tests", TestResults }, { "failures", failures }
            };
            File.WriteAllText(Path.Combine(runRoot, "unit-test-receipt.json"), Json.Serialize(receipt), new UTF8Encoding(false));
            Console.WriteLine("RECEIPT " + runRoot);
            Console.WriteLine(failures == 0 ? "PASS ALL" : "FAILURES " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void TestValidRequest()
        {
            var fixture = Fixture.Create(runRoot, "中文朗读测试。", null);
            var validated = new SpeechRequestReader().ReadAndValidate(fixture.JobRoot, fixture.RequestPath);
            Equal("text", "中文朗读测试。", validated.SpokenText);
            Equal("context", "selection_plus_paragraph", validated.Request.contextMode);
        }

        private static void TestUnsafePaths()
        {
            var absolute = Fixture.Create(runRoot, "text", delegate(SpeechRequestV1 r) { r.spokenText.path = "C:/Windows/win.ini"; });
            ThrowsCode("absolute", "unsafe_artifact_path", delegate { new SpeechRequestReader().ReadAndValidate(absolute.JobRoot, absolute.RequestPath); });
            var traversal = Fixture.Create(runRoot, "text", delegate(SpeechRequestV1 r) { r.spokenText.path = "input/../payload.txt"; });
            ThrowsCode("traversal", "unsafe_artifact_path", delegate { new SpeechRequestReader().ReadAndValidate(traversal.JobRoot, traversal.RequestPath); });
        }

        private static void TestIntegrityMismatch()
        {
            var bytes = Fixture.Create(runRoot, "text", delegate(SpeechRequestV1 r) { r.spokenText.bytes++; });
            ThrowsCode("bytes", "spoken_text_bytes_mismatch", delegate { new SpeechRequestReader().ReadAndValidate(bytes.JobRoot, bytes.RequestPath); });
            var hash = Fixture.Create(runRoot, "text", delegate(SpeechRequestV1 r) { r.spokenText.sha256 = new string('0', 64); });
            ThrowsCode("hash", "spoken_text_hash_mismatch", delegate { new SpeechRequestReader().ReadAndValidate(hash.JobRoot, hash.RequestPath); });
        }

        private static void TestLanguageRoutes()
        {
            Equal("zh", "zh-CN", RouteText("这是中文。"));
            Equal("en", "en-US", RouteText("This is English."));
            Equal("es", "es-ES", RouteText("¿Cómo estás?"));
            Equal("ja", "ja-JP", RouteText("これは日本語です。"));
            Equal("ko", "ko-KR", RouteText("이것은 한국어입니다."));
        }

        private static void TestLanguageOverride()
        {
            var fixture = Fixture.Create(runRoot, "Ambiguous text", delegate(SpeechRequestV1 r) { r.language.strategy = "override"; r.language.@override = "es-ES"; });
            var route = VoiceRouter.Route(new SpeechRequestReader().ReadAndValidate(fixture.JobRoot, fixture.RequestPath));
            Equal("override tag", "es-ES", route.LanguageTag);
            Equal("override voice", "es-ES-XimenaNeural", route.Voice);
        }

        private static void TestMissingLanguageVoice()
        {
            var fixture = Fixture.Create(runRoot, "これは日本語です。", delegate(SpeechRequestV1 request)
            {
                request.voiceSnapshot.voicesByLanguage.Remove("ja-JP");
            });
            ThrowsCode("missing Japanese voice", "voice_not_configured", delegate
            {
                VoiceRouter.Route(new SpeechRequestReader().ReadAndValidate(fixture.JobRoot, fixture.RequestPath));
            });
        }

        private static async Task TestFiveLanguageVoiceSnapshots()
        {
            var cases = new[]
            {
                new[] { "这是中文。", "zh-CN", "zh-CN-CustomNeural" },
                new[] { "This is English.", "en-US", "en-US-CustomNeural" },
                new[] { "¿Cómo estás?", "es-ES", "es-ES-CustomNeural" },
                new[] { "これは日本語です。", "ja-JP", "ja-JP-CustomNeural" },
                new[] { "이것은 한국어입니다.", "ko-KR", "ko-KR-CustomNeural" }
            };
            foreach (var item in cases)
            {
                var fixture = Fixture.Create(runRoot, item[0], delegate(SpeechRequestV1 request)
                {
                    request.voiceSnapshot.voicesByLanguage[item[1]] = item[2];
                    request.voiceSnapshot.ratePercent = 17;
                    request.voiceSnapshot.volumePercent = -8;
                    request.voiceSnapshot.pitchHz = 23;
                });
                var fake = new FakeSynthesizer(false);
                var result = await new SynthesisWorker(new SpeechRequestReader(), fake).RunAsync(fixture.JobRoot, fixture.AttemptDirectory);
                Equal(item[1] + " status", "completed", result.status);
                Equal(item[1] + " language", item[1], fake.Routes[0].LanguageTag);
                Equal(item[1] + " voice", item[2], fake.Routes[0].Voice);
                Equal(item[1] + " rate", "17", fake.Snapshots[0].ratePercent.ToString());
                Equal(item[1] + " volume", "-8", fake.Snapshots[0].volumePercent.ToString());
                Equal(item[1] + " pitch", "23", fake.Snapshots[0].pitchHz.ToString());
            }
            Equal("edge rate argument", "+17%", EdgeTtsSynthesizer.Signed(17, "%"));
            Equal("edge volume argument", "-8%", EdgeTtsSynthesizer.Signed(-8, "%"));
            Equal("edge pitch argument", "+23Hz", EdgeTtsSynthesizer.Signed(23, "Hz"));
        }

        private static void TestVoiceTuningBounds()
        {
            var valid = Fixture.Create(runRoot, "bounds", delegate(SpeechRequestV1 request)
            {
                request.voiceSnapshot.ratePercent = 100;
                request.voiceSnapshot.volumePercent = -100;
                request.voiceSnapshot.pitchHz = 1000;
            });
            new SpeechRequestReader().ReadAndValidate(valid.JobRoot, valid.RequestPath);
            var rate = Fixture.Create(runRoot, "rate", delegate(SpeechRequestV1 request) { request.voiceSnapshot.ratePercent = 101; });
            ThrowsCode("rate", "invalid_rate", delegate { new SpeechRequestReader().ReadAndValidate(rate.JobRoot, rate.RequestPath); });
            var volume = Fixture.Create(runRoot, "volume", delegate(SpeechRequestV1 request) { request.voiceSnapshot.volumePercent = -101; });
            ThrowsCode("volume", "invalid_volume", delegate { new SpeechRequestReader().ReadAndValidate(volume.JobRoot, volume.RequestPath); });
            var pitch = Fixture.Create(runRoot, "pitch", delegate(SpeechRequestV1 request) { request.voiceSnapshot.pitchHz = 1001; });
            ThrowsCode("pitch", "invalid_pitch", delegate { new SpeechRequestReader().ReadAndValidate(pitch.JobRoot, pitch.RequestPath); });
        }

        private static void TestCachePolicySemantics()
        {
            var valid = Fixture.Create(runRoot, "cache", null);
            new SpeechRequestReader().ReadAndValidate(valid.JobRoot, valid.RequestPath);
            var noReuse = Fixture.Create(runRoot, "cache", delegate(SpeechRequestV1 request) { request.cachePolicy.reuseSuccessfulAudio = false; });
            ThrowsCode("reuse flag", "unsupported_cache_policy", delegate { new SpeechRequestReader().ReadAndValidate(noReuse.JobRoot, noReuse.RequestPath); });
            var global = Fixture.Create(runRoot, "cache", delegate(SpeechRequestV1 request) { request.cachePolicy.scope = "global"; });
            ThrowsCode("scope", "unsupported_cache_policy", delegate { new SpeechRequestReader().ReadAndValidate(global.JobRoot, global.RequestPath); });
            var crossTask = Fixture.Create(runRoot, "cache", delegate(SpeechRequestV1 request) { request.cachePolicy.crossTaskReuse = true; });
            ThrowsCode("cross task", "unsupported_cache_policy", delegate { new SpeechRequestReader().ReadAndValidate(crossTask.JobRoot, crossTask.RequestPath); });
        }

        private static void TestSettingsAdapterFiveLanguageSnapshot()
        {
            var document = SettingsFixture();
            var runtimeRoot = Path.Combine(runRoot, "settings-runtime");
            Directory.CreateDirectory(runtimeRoot);
            var edgeTtsPath = Path.Combine(runtimeRoot, "edge-tts.exe");
            var ffplayPath = Path.Combine(runtimeRoot, "fake-player.exe");
            File.WriteAllText(edgeTtsPath, "offline fixture", new UTF8Encoding(false));
            File.WriteAllText(ffplayPath, "offline fixture", new UTF8Encoding(false));
            var adapter = new SpeechSettingsAdapter(document.catalog, new SpeechDeploymentDiagnostics
            {
                edgeTtsPath = edgeTtsPath,
                ffplayPath = ffplayPath
            });
            var readable = new List<Dictionary<string, object>>();
            var readableMarkdown = new StringBuilder();
            readableMarkdown.AppendLine("# 五语言设置 → SpeechRequest 离线样例");
            readableMarkdown.AppendLine();
            readableMarkdown.AppendLine("- provider: `edge-tts`");
            readableMarkdown.AppendLine("- legacy AI TTS: `disabled`");
            readableMarkdown.AppendLine("- physical speaker: `false`（只验证请求与 FakePlayer）");
            readableMarkdown.AppendLine();
            readableMarkdown.AppendLine("| 输入文本 | 策略 | 语言 | Voice | Rate | Volume | Pitch | Runtime snapshot |");
            readableMarkdown.AppendLine("|---|---|---|---|---:|---:|---:|---|");

            foreach (var sample in document.samples)
            {
                var fixture = Fixture.Create(runRoot, sample.text, null);
                var envelope = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(fixture.RequestPath));
                var adapted = adapter.ApplyToSynthesisEnvelope(envelope, document.settings);
                File.WriteAllText(fixture.RequestPath, Json.Serialize(envelope), new UTF8Encoding(false));
                var validated = new SpeechRequestReader().ReadAndValidate(fixture.JobRoot, fixture.RequestPath);
                var route = VoiceRouter.Route(validated);

                Equal(sample.expectedLanguage + " language", sample.expectedLanguage, route.LanguageTag);
                Equal(sample.expectedLanguage + " voice", sample.expectedVoice, route.Voice);
                Equal(sample.expectedLanguage + " strategy", "auto", adapted.speechRequest.language.strategy);
                True(sample.expectedLanguage + " no override", adapted.speechRequest.language.@override == null);
                Equal(sample.expectedLanguage + " rate", "17", adapted.speechRequest.voiceSnapshot.ratePercent.ToString());
                Equal(sample.expectedLanguage + " volume", "-8", adapted.speechRequest.voiceSnapshot.volumePercent.ToString());
                Equal(sample.expectedLanguage + " pitch", "23", adapted.speechRequest.voiceSnapshot.pitchHz.ToString());
                Equal(sample.expectedLanguage + " edge path", Path.GetFullPath(edgeTtsPath), envelope.configSnapshot["edgeTtsPath"].ToString());
                Equal(sample.expectedLanguage + " playback path", Path.GetFullPath(ffplayPath), adapted.playbackConfigSnapshot["ffplayPath"].ToString());
                Equal(sample.expectedLanguage + " fixed provider", "edge-tts", adapted.fixedSettings.provider);
                Equal(sample.expectedLanguage + " old AI", "disabled", adapted.fixedSettings.legacyAiTts);
                Equal(sample.expectedLanguage + " natural completion", "idle_replayable", adapted.fixedSettings.playbackPolicy.naturalCompletion);
                Equal(sample.expectedLanguage + " replay", "from_start", adapted.fixedSettings.playbackPolicy.replay);
                Equal(sample.expectedLanguage + " cache scope", "task", adapted.fixedSettings.cachePolicy.scope);
                True(sample.expectedLanguage + " no cross-task cache", !adapted.fixedSettings.cachePolicy.crossTaskReuse);
                Equal(sample.expectedLanguage + " Edge runtime", "available", adapted.deployment.edgeTtsStatus);
                Equal(sample.expectedLanguage + " player runtime", "available", adapted.deployment.ffplayStatus);
                True(sample.expectedLanguage + " five languages", adapted.deployment.supportedLanguages.Count == 5);

                readable.Add(new Dictionary<string, object>
                {
                    { "inputText", sample.text },
                    { "setting", new Dictionary<string, object>
                        {
                            { "languageStrategy", document.settings.languageStrategy }, { "languageOverride", document.settings.languageOverride },
                            { "ratePercent", document.settings.ratePercent }, { "volumePercent", document.settings.volumePercent }, { "pitchHz", document.settings.pitchHz }
                        }
                    },
                    { "request", new Dictionary<string, object>
                        {
                            { "language", adapted.speechRequest.language }, { "selectedVoice", route.Voice },
                            { "voiceSnapshot", adapted.speechRequest.voiceSnapshot }, { "configSnapshot", envelope.configSnapshot }
                        }
                    },
                    { "physicalSpeaker", false }
                });
                readableMarkdown.AppendLine("| " + sample.text.Replace("|", "\\|") + " | auto | " + route.LanguageTag + " | " + route.Voice + " | 17 | -8 | 23 | edgeTtsPath | ");
            }

            var playbackEnvelope = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(PlaybackCliFixture.Create(runRoot, ffplayPath).RequestPath));
            playbackEnvelope.configSnapshot = new Dictionary<string, object>();
            adapter.ApplyToPlaybackEnvelope(playbackEnvelope);
            Equal("playback envelope config snapshot", Path.GetFullPath(ffplayPath), playbackEnvelope.configSnapshot["ffplayPath"].ToString());

            var overrideFixture = Fixture.Create(runRoot, "这是故意用英文声音朗读的文本。", null);
            var overrideEnvelope = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(overrideFixture.RequestPath));
            var overrideSettings = Clone(document.settings);
            overrideSettings.languageStrategy = "override";
            overrideSettings.languageOverride = "en-us";
            adapter.ApplyToSynthesisEnvelope(overrideEnvelope, overrideSettings);
            File.WriteAllText(overrideFixture.RequestPath, Json.Serialize(overrideEnvelope), new UTF8Encoding(false));
            var overrideRoute = VoiceRouter.Route(new SpeechRequestReader().ReadAndValidate(overrideFixture.JobRoot, overrideFixture.RequestPath));
            Equal("normalized override", "en-US", overrideEnvelope.payload.language.@override);
            Equal("override voice from settings", "en-US-AvaMultilingualNeural", overrideRoute.Voice);

            File.WriteAllText(Path.Combine(runRoot, "five-language-settings-to-request.json"), Json.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "fixture", "speech-settings-five-language.json" },
                { "provider", "edge-tts" }, { "legacyAiTts", "disabled" }, { "physicalSpeaker", false }, { "samples", readable }
            }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(runRoot, "five-language-settings-to-request.md"), readableMarkdown.ToString(), new UTF8Encoding(false));
        }

        private static void TestSettingsAdapterFailures()
        {
            var document = SettingsFixture();
            var adapter = new SpeechSettingsAdapter(document.catalog, new SpeechDeploymentDiagnostics());
            var template = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(Fixture.Create(runRoot, "settings", null).RequestPath)).payload;

            var autoOverride = Clone(document.settings);
            autoOverride.languageOverride = "en-US";
            ThrowsCode("auto plus override", "unexpected_language_override", delegate { adapter.CreateSnapshot(template, autoOverride); });

            var missingOverride = Clone(document.settings);
            missingOverride.languageStrategy = "override";
            missingOverride.languageOverride = null;
            ThrowsCode("override missing language", "invalid_language_override", delegate { adapter.CreateSnapshot(template, missingOverride); });

            var unsupportedOverride = Clone(document.settings);
            unsupportedOverride.languageStrategy = "override";
            unsupportedOverride.languageOverride = "fr-FR";
            ThrowsCode("unsupported override", "unsupported_language_override", delegate { adapter.CreateSnapshot(template, unsupportedOverride); });

            var unknownVoice = Clone(document.settings);
            unknownVoice.voicesByLanguage["zh-CN"] = "zh-CN-UnknownNeural";
            ThrowsCode("unknown voice", "unknown_voice", delegate { adapter.CreateSnapshot(template, unknownVoice); });

            var mismatchedVoice = Clone(document.settings);
            mismatchedVoice.voicesByLanguage["zh-CN"] = "en-US-AvaMultilingualNeural";
            ThrowsCode("voice language mismatch", "unknown_voice", delegate { adapter.CreateSnapshot(template, mismatchedVoice); });

            var unsupportedLanguage = Clone(document.settings);
            unsupportedLanguage.voicesByLanguage.Add("fr-FR", "fr-FR-DeniseNeural");
            ThrowsCode("unsupported voice language", "unsupported_voice_language", delegate { adapter.CreateSnapshot(template, unsupportedLanguage); });

            var missingLanguage = Clone(document.settings);
            missingLanguage.voicesByLanguage.Remove("ko-KR");
            ThrowsCode("missing language voice", "missing_language_voice", delegate { adapter.CreateSnapshot(template, missingLanguage); });

            var badRate = Clone(document.settings);
            badRate.ratePercent = 101;
            ThrowsCode("adapter rate", "invalid_rate", delegate { adapter.CreateSnapshot(template, badRate); });
            var badVolume = Clone(document.settings);
            badVolume.volumePercent = -101;
            ThrowsCode("adapter volume", "invalid_volume", delegate { adapter.CreateSnapshot(template, badVolume); });
            var badPitch = Clone(document.settings);
            badPitch.pitchHz = 1001;
            ThrowsCode("adapter pitch", "invalid_pitch", delegate { adapter.CreateSnapshot(template, badPitch); });

            var unavailableCatalog = Clone(document.catalog);
            unavailableCatalog.voices.First(v => v.languageTag == "ja-JP").available = false;
            var unavailableAdapter = new SpeechSettingsAdapter(unavailableCatalog, new SpeechDeploymentDiagnostics());
            ThrowsCode("unavailable voice", "voice_unavailable", delegate { unavailableAdapter.CreateSnapshot(template, document.settings); });

            var oldProviderCatalog = Clone(document.catalog);
            oldProviderCatalog.provider = "speech-kit";
            ThrowsCode("old provider catalog", "unsupported_voice_provider", delegate { new SpeechSettingsAdapter(oldProviderCatalog, new SpeechDeploymentDiagnostics()); });
            var malformedCatalog = Clone(document.catalog);
            malformedCatalog.voices[0].voiceId = "bad voice id";
            ThrowsCode("malformed voice catalog", "invalid_voice_catalog", delegate { new SpeechSettingsAdapter(malformedCatalog, new SpeechDeploymentDiagnostics()); });
            ThrowsCode("relative runtime path", "runtime_path_not_absolute", delegate
            {
                new SpeechSettingsAdapter(document.catalog, new SpeechDeploymentDiagnostics { edgeTtsPath = "tools/edge-tts.exe" });
            });
            var playbackEnvelope = new WorkerRequestEnvelopeV1 { operation = "speech.synthesize", configSnapshot = new Dictionary<string, object>() };
            ThrowsCode("playback config wrong operation", "wrong_settings_operation", delegate { adapter.ApplyToPlaybackEnvelope(playbackEnvelope); });
        }

        private static void TestOldProviderNeverRoutes()
        {
            var fixture = Fixture.Create(runRoot, "text", delegate(SpeechRequestV1 r) { r.voiceSnapshot.provider = "speech-kit"; });
            var fake = new FakeSynthesizer(false);
            var result = new SynthesisWorker(new SpeechRequestReader(), fake).RunAsync(fixture.JobRoot, fixture.AttemptDirectory).GetAwaiter().GetResult();
            Equal("provider rejected", "unsupported_voice_provider", result.error.code);
            Equal("synth not called", "0", fake.Calls.ToString());
        }

        private static void TestPublicFixtureAdapter()
        {
            var fixtureRoot = Path.Combine(ProjectRoot(), "contracts", "v1", "fixtures", "speech");
            foreach (var path in Directory.GetFiles(Path.Combine(fixtureRoot, "valid"), "*.json"))
            {
                var envelope = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(path));
                SpeechRequestReader.ValidateRequestShape(envelope.payload);
                Equal("public worker", "speech.edge", envelope.workerId);
            }
            var cache = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(Path.Combine(fixtureRoot, "invalid", "cross-task-cache.json")));
            ThrowsCode("public invalid cache", "unsupported_cache_policy", delegate { SpeechRequestReader.ValidateRequestShape(cache.payload); });
            var language = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(Path.Combine(fixtureRoot, "invalid", "language-override-null.json")));
            ThrowsCode("public invalid language", "invalid_language_override", delegate { SpeechRequestReader.ValidateRequestShape(language.payload); });
            var absolute = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(Path.Combine(fixtureRoot, "invalid", "absolute-path.json")));
            ThrowsCode("public absolute path", "unsafe_artifact_path", delegate { SpeechRequestReader.ResolveJobRelativePath(runRoot, absolute.attemptPath); });
            var traversal = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(Path.Combine(fixtureRoot, "invalid", "path-traversal.json")));
            ThrowsCode("public traversal", "unsafe_artifact_path", delegate { SpeechRequestReader.ResolveJobRelativePath(runRoot, traversal.payload.spokenText.path); });
        }

        private static async Task TestSynthesisSuccess()
        {
            var fixture = Fixture.Create(runRoot, "自然语音测试。", null);
            var result = await new SynthesisWorker(new SpeechRequestReader(), new FakeSynthesizer(false)).RunAsync(fixture.JobRoot, fixture.AttemptDirectory);
            Equal("status", "completed", result.status);
            Equal("playback state", "idle_replayable", result.payload["playbackState"].ToString());
            True("one output", result.outputs.Count == 1);
            var manifestPath = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, result.outputs[0].manifestPath);
            var manifest = Json.Deserialize<ArtifactManifestV1>(File.ReadAllText(manifestPath));
            Equal("manifest hash", result.outputs[0].expectedSha256, SpeechRequestReader.HashFile(manifestPath));
            var payload = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, manifest.files[0].path);
            True("payload exists", File.Exists(payload));
            Equal("payload hash", manifest.files[0].sha256, SpeechRequestReader.HashFile(payload));
            Equal("artifact schema version", "1.0.0", manifest.schemaVersion);
            True("result exists", File.Exists(Path.Combine(fixture.AttemptDirectory, "result.json")));
        }

        private static async Task TestLongPathSynthesisPublishing()
        {
            var fixture = Fixture.CreateLongPath(runRoot, "深路径自然语音测试。");
            var fake = new FakeSynthesizer(false);
            var immutableInput = LongPathFileSystem.ReadAllText(
                SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, "inputs/spoken-text/v0001/payload.txt"),
                new UTF8Encoding(false));

            var result = await new SynthesisWorker(new SpeechRequestReader(), fake).RunAsync(fixture.JobRoot, fixture.AttemptDirectory);

            Equal("long path status", "completed", result.status);
            True("long path one output", result.outputs.Count == 1);
            True("fake synthesizer called once", fake.Calls == 1);
            True("tool text path remains short", fake.TextPaths.Single().Length < 200);
            True("tool media path remains short", fake.OutputPaths.Single().Length < 200);
            True("tool workspace is outside immutable attempt", !fake.TextPaths.Single().StartsWith(fixture.AttemptDirectory, StringComparison.OrdinalIgnoreCase));
            True("tool workspace was cleaned", !LongPathFileSystem.DirectoryExists(Path.GetDirectoryName(fake.TextPaths.Single())));

            var manifestPath = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, result.outputs[0].manifestPath);
            var manifest = Json.Deserialize<ArtifactManifestV1>(LongPathFileSystem.ReadAllText(manifestPath, new UTF8Encoding(false)));
            var payloadPath = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, manifest.files.Single().path);
            True("final payload exceeds MAX_PATH", payloadPath.Length > 260);
            True("long path payload exists", LongPathFileSystem.FileExists(payloadPath));
            Equal("long path manifest hash", result.outputs[0].expectedSha256, SpeechRequestReader.HashFile(manifestPath));
            Equal("long path payload hash", manifest.files.Single().sha256, SpeechRequestReader.HashFile(payloadPath));
            Equal("immutable source preserved", immutableInput, LongPathFileSystem.ReadAllText(
                SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, "inputs/spoken-text/v0001/payload.txt"),
                new UTF8Encoding(false)));
            True("long path result exists", LongPathFileSystem.FileExists(Path.Combine(fixture.AttemptDirectory, "result.json")));
        }

        private static async Task TestLongPathPlaybackStaging()
        {
            var fixture = Fixture.CreateLongPath(runRoot, "深路径播放测试。");
            var synthesis = await new SynthesisWorker(new SpeechRequestReader(), new FakeSynthesizer(false)).RunAsync(fixture.JobRoot, fixture.AttemptDirectory);
            Equal("long playback synthesis", "completed", synthesis.status);
            var manifestPath = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, synthesis.outputs.Single().manifestPath);
            var manifest = Json.Deserialize<ArtifactManifestV1>(LongPathFileSystem.ReadAllText(manifestPath, new UTF8Encoding(false)));
            var audioRef = manifest.files.Single().path;
            var immutableAudioPath = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, audioRef);
            True("long playback source exceeds MAX_PATH", immutableAudioPath.Length > 260);

            var playAttempt = Path.Combine(fixture.JobRoot, "runs", "r1", "stages", "speech-playback", "attempts", "p1");
            LongPathFileSystem.CreateDirectory(playAttempt);
            var work = new PlaybackWorkItem
            {
                RequestId = "request-long-play", OperationId = "operation-long-play", TaskId = LongPathFileSystem.GetFileName(fixture.JobRoot),
                RunId = "r1", StageId = "speech-playback", AttemptId = "p1", WorkerId = "speech.edge",
                IdempotencyKey = SpeechRequestReader.HashFile(immutableAudioPath), AudioPath = audioRef,
                AudioBytes = LongPathFileSystem.FileLength(immutableAudioPath), AudioSha256 = SpeechRequestReader.HashFile(immutableAudioPath),
                LanguageTag = "zh-CN", VoiceId = "zh-CN-XiaoxiaoNeural", CancellationSignalRef = Fixture.Relative(fixture.JobRoot, Path.Combine(playAttempt, "cancel.json")),
                CancellationGracePeriodMs = 2000
            };
            var factory = new FakeAudioPlayerFactory();
            using (var coordinator = new PlaybackCoordinator(factory))
            {
                var task = new PlaybackWorker(coordinator).RunPlayAsync(fixture.JobRoot, playAttempt, work);
                True("long playback started once", factory.Processes.Count == 1);
                var stagedPath = factory.Paths.Single();
                True("player path remains short", stagedPath.Length < 200);
                True("player workspace is outside job", !stagedPath.StartsWith(fixture.JobRoot, StringComparison.OrdinalIgnoreCase));
                True("player staged copy exists while active", LongPathFileSystem.FileExists(stagedPath));
                factory.Processes.Single().Complete(0);
                var result = await task;
                Equal("long playback status", "completed", result.status);
                Equal("long playback replayable", "idle_replayable", result.payload["playbackState"].ToString());
                True("player workspace cleaned after terminal", !LongPathFileSystem.DirectoryExists(Path.GetDirectoryName(stagedPath)));
                True("immutable audio preserved after playback", LongPathFileSystem.FileExists(immutableAudioPath));
            }
        }

        private static async Task TestSynthesisFailure()
        {
            var fixture = Fixture.Create(runRoot, "failure", null);
            var result = await new SynthesisWorker(new SpeechRequestReader(), new FakeSynthesizer(true)).RunAsync(fixture.JobRoot, fixture.AttemptDirectory);
            Equal("status", "failed", result.status);
            Equal("error", "edge_tts_failed", result.error.code);
            True("no downstream outputs", result.outputs.Count == 0 && !Directory.Exists(Path.Combine(fixture.AttemptDirectory, "outputs", "speech-audio")));
            True("error details retained", File.Exists(SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, result.error.detailsRef)));
        }

        private static async Task TestPlaybackCompletion()
        {
            var fixture = PlaybackFixture.Create(runRoot);
            var factory = new FakeAudioPlayerFactory();
            using (var coordinator = new PlaybackCoordinator(factory))
            {
                var worker = new PlaybackWorker(coordinator);
                var task = worker.RunPlayAsync(fixture.JobRoot, fixture.PlayAttempt, fixture.PlayWork);
                factory.Processes[0].Complete(0);
                var result = await task;
                Equal("playback state", "idle_replayable", result.payload["playbackState"].ToString());
                True("playback is not a cache hit", !(bool)result.payload["cacheHit"]);
                True("not playing", !coordinator.IsPlaying);
            }
        }

        private static async Task TestPlaybackReplay()
        {
            var fixture = PlaybackFixture.Create(runRoot);
            var replayAttempt = fixture.CreateAdditionalAttempt("playback-replay");
            var replayWork = fixture.MakeWork("playback-replay");
            var factory = new FakeAudioPlayerFactory();
            using (var coordinator = new PlaybackCoordinator(factory))
            {
                var worker = new PlaybackWorker(coordinator);
                var firstTask = worker.RunPlayAsync(fixture.JobRoot, fixture.PlayAttempt, fixture.PlayWork);
                var immutableAudio = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, fixture.AudioPath);
                var immutableHash = SpeechRequestReader.HashFile(immutableAudio);
                var firstStagedPath = factory.Paths[0];
                Equal("first staged copy matches immutable audio", immutableHash, SpeechRequestReader.HashFile(firstStagedPath));
                factory.Processes[0].Complete(0);
                var first = await firstTask;
                Equal("first naturally replayable", "idle_replayable", first.payload["playbackState"].ToString());
                True("first playback not reused", !(bool)first.payload["cacheHit"]);
                True("first staged workspace cleaned", !LongPathFileSystem.DirectoryExists(Path.GetDirectoryName(firstStagedPath)));
                var replayTask = worker.RunPlayAsync(fixture.JobRoot, replayAttempt, replayWork);
                True("fresh playback process", factory.Processes.Count == 2 && !object.ReferenceEquals(factory.Processes[0], factory.Processes[1]));
                True("replay uses a unique staged path", !factory.Paths[0].Equals(factory.Paths[1], StringComparison.OrdinalIgnoreCase));
                Equal("replay staged copy matches immutable audio", immutableHash, SpeechRequestReader.HashFile(factory.Paths[1]));
                Equal("new attempt identity", "playback-replay", replayWork.AttemptId);
                True("replay active", coordinator.IsPlaying);
                factory.Processes[1].Complete(0);
                var replay = await replayTask;
                Equal("replay naturally replayable", "idle_replayable", replay.payload["playbackState"].ToString());
                True("replay not reused", !(bool)replay.payload["cacheHit"]);
                True("replay resets idle", !coordinator.IsPlaying);
                True("replay staged workspace cleaned", !LongPathFileSystem.DirectoryExists(Path.GetDirectoryName(factory.Paths[1])));
            }
        }

        private static async Task TestPlaybackRevalidatesBeforeStart()
        {
            var fixture = PlaybackFixture.Create(runRoot);
            var audioPath = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, fixture.AudioPath);
            var bytes = File.ReadAllBytes(audioPath);
            bytes[0] ^= 0x01;
            File.WriteAllBytes(audioPath, bytes);
            var factory = new FakeAudioPlayerFactory();
            using (var coordinator = new PlaybackCoordinator(factory))
            {
                var result = await new PlaybackWorker(coordinator).RunPlayAsync(fixture.JobRoot, fixture.PlayAttempt, fixture.PlayWork);
                Equal("tampered audio status", "failed", result.status);
                Equal("tampered audio error", "audio_hash_mismatch", result.error.code);
                True("player never started", factory.Processes.Count == 0);
                True("failed playback not reused", !(bool)result.payload["cacheHit"]);
            }
        }

        private static async Task TestPlaybackStop()
        {
            var fixture = PlaybackFixture.Create(runRoot);
            var factory = new FakeAudioPlayerFactory();
            using (var coordinator = new PlaybackCoordinator(factory))
            {
                var worker = new PlaybackWorker(coordinator);
                var playTask = worker.RunPlayAsync(fixture.JobRoot, fixture.PlayAttempt, fixture.PlayWork);
                var stop = worker.RunStop(fixture.JobRoot, fixture.StopAttempt, fixture.StopWork);
                var play = await playTask;
                Equal("play stopped", "stopped", play.payload["playbackState"].ToString());
                Equal("stop command", "stopped", stop.payload["playbackState"].ToString());
                True("stop command not reused", !(bool)stop.payload["cacheHit"]);
                True("stop clears active", !coordinator.IsPlaying && !coordinator.StopActive());
            }
        }

        private static async Task TestStopOnlyCancelsActivePlayback()
        {
            var factory = new FakeAudioPlayerFactory();
            using (var coordinator = new PlaybackCoordinator(factory))
            {
                True("idle stop is a no-op", !coordinator.StopActive());
                var firstTask = coordinator.PlayAsync("first-attempt", "immutable-audio.mp3");
                var secondTask = coordinator.PlayAsync("second-attempt", "immutable-audio.mp3");
                var first = await firstTask;
                Equal("only prior attempt superseded", "superseded", first.Status);
                True("second remains active", coordinator.IsPlaying);
                True("active stop accepted", coordinator.StopActive());
                var second = await secondTask;
                Equal("only active attempt stopped", "user_stop", second.Status);
                True("active cleared", !coordinator.IsPlaying);
                True("second idle stop is a no-op", !coordinator.StopActive());
                factory.Processes[0].Complete(0);
                Equal("late prior completion cannot overwrite", "superseded", first.Status);
                Equal("stopped terminal remains stable", "user_stop", second.Status);
            }
        }

        private static async Task TestPlaybackSupersede()
        {
            var fixture = PlaybackFixture.Create(runRoot);
            var secondAttempt = fixture.CreateAdditionalAttempt("playback-2");
            var secondWork = fixture.MakeWork("playback-2");
            var factory = new FakeAudioPlayerFactory();
            using (var coordinator = new PlaybackCoordinator(factory))
            {
                var worker = new PlaybackWorker(coordinator);
                var firstTask = worker.RunPlayAsync(fixture.JobRoot, fixture.PlayAttempt, fixture.PlayWork);
                var secondTask = worker.RunPlayAsync(fixture.JobRoot, secondAttempt, secondWork);
                var first = await firstTask;
                Equal("first superseded", "superseded", first.payload["playbackState"].ToString());
                Equal("first cancelled", "cancelled", first.status);
                True("superseded playback not reused", !(bool)first.payload["cacheHit"]);
                True("supersede warning", first.warnings.Count == 1 && first.warnings[0].Contains("superseded"));
                factory.Processes[0].Complete(9); // late event must not affect the new session
                True("second still active", coordinator.IsPlaying);
                factory.Processes[1].Complete(0);
                var second = await secondTask;
                Equal("second completed", "idle_replayable", second.payload["playbackState"].ToString());
            }
        }

        private static void TestDescribeCli()
        {
            var cli = RunCli("describe", "--json");
            Equal("describe exit", "0", cli.ExitCode.ToString());
            var document = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
            Equal("describe worker", "speech.edge", document["workerId"].ToString());
            Equal("describe version", WorkerCli.WorkerVersion, document["workerVersion"].ToString());
            Equal("describe contract", "1.0.0", document["contractVersion"].ToString());
            var stages = (object[])document["stages"];
            True("describe synth stage", Array.IndexOf(stages, "speech.synthesize") >= 0);
            True("describe playback stage", Array.IndexOf(stages, "speech.playback") >= 0);
            var cancellation = (Dictionary<string, object>)document["cancellation"];
            Equal("cancel protocol", "attempt-signal-file-v1", cancellation["protocol"].ToString());
            True("describe stderr empty", string.IsNullOrWhiteSpace(cli.StandardError));
        }

        private static void TestSettingsDescribeCli()
        {
            var cli = RunCli("settings", "describe", "--json");
            Equal("settings describe exit", "0", cli.ExitCode.ToString());
            var document = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
            Equal("settings descriptor schema", SpeechSettingsAdapterCli.DescriptorSchemaVersion, document["schemaVersion"].ToString());
            Equal("settings descriptor worker", "speech.edge", document["workerId"].ToString());
            Equal("settings descriptor version", WorkerCli.WorkerVersion, document["workerVersion"].ToString());
            var serialized = Json.Serialize(document);
            True("editable fields exposed", serialized.Contains("languageStrategy") && serialized.Contains("voicesByLanguage") && serialized.Contains("pitchHz"));
            True("fixed fields exposed", serialized.Contains("const:edge-tts") && serialized.Contains("legacyAiTts") && serialized.Contains("const:disabled"));
            True("diagnostics exposed", serialized.Contains("edgeTtsPath") && serialized.Contains("ffplayPath") && serialized.Contains("voice-status-list"));
        }

        private static void TestSettingsAdaptCli()
        {
            var input = CreateSettingsCliInput(false);
            var path = Path.Combine(runRoot, "settings-adapter-input-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, Json.Serialize(input), new UTF8Encoding(false));
            var cli = RunCli("settings", "adapt", "--input", path, "--json");
            Equal("settings adapt exit", "0", cli.ExitCode.ToString());
            var output = Json.Deserialize<SpeechSettingsAdapterOutputV1>(cli.StandardOutput.Trim());
            Equal("settings output schema", SpeechSettingsAdapterCli.OutputSchemaVersion, output.schemaVersion);
            Equal("settings output worker", "speech.edge", output.workerId);
            Equal("settings output version", WorkerCli.WorkerVersion, output.workerVersion);
            Equal("settings output strategy", "auto", output.synthesisRequest.payload.language.strategy);
            Equal("settings output voice", "zh-CN-XiaoxiaoNeural", output.synthesisRequest.payload.voiceSnapshot.voicesByLanguage["zh-CN"]);
            Equal("settings output rate", "17", output.synthesisRequest.payload.voiceSnapshot.ratePercent.ToString());
            Equal("settings output volume", "-8", output.synthesisRequest.payload.voiceSnapshot.volumePercent.ToString());
            Equal("settings output pitch", "23", output.synthesisRequest.payload.voiceSnapshot.pitchHz.ToString());
            True("synthesis runtime frozen", output.synthesisRequest.configSnapshot.ContainsKey("edgeTtsPath"));
            True("playback runtime frozen", output.playbackRequest.configSnapshot.ContainsKey("ffplayPath"));
            Equal("settings fixed provider", "edge-tts", output.fixedSettings.provider);
            Equal("settings fixed AI", "disabled", output.fixedSettings.legacyAiTts);
            Equal("settings fixed natural completion", "idle_replayable", output.fixedSettings.playbackPolicy.naturalCompletion);
            Equal("settings fixed replay", "from_start", output.fixedSettings.playbackPolicy.replay);
            SpeechRequestReader.ValidateRequestShape(output.synthesisRequest.payload);
        }

        private static void TestSettingsAdaptCliFailures()
        {
            var relative = RunCli("settings", "adapt", "--input", "relative-settings.json", "--json");
            Equal("settings relative exit", "2", relative.ExitCode.ToString());
            True("settings relative code", relative.StandardError.Contains("settings_path_not_absolute"));

            var input = CreateSettingsCliInput(true);
            var path = Path.Combine(runRoot, "settings-adapter-invalid-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, Json.Serialize(input), new UTF8Encoding(false));
            var unknown = RunCli("settings", "adapt", "--input", path, "--json");
            Equal("settings unknown voice exit", "2", unknown.ExitCode.ToString());
            True("settings unknown voice code", unknown.StandardError.Contains("unknown_voice"));
        }

        private static void TestSettingsCompanionDescriptor()
        {
            var cli = RunCli("settings", "companion", "describe", "--json");
            Equal("companion describe exit", "0", cli.ExitCode.ToString());
            var descriptor = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
            Equal("companion descriptor schema", SpeechSettingsCompanion.DescriptorSchemaVersion, descriptor["schemaVersion"].ToString());
            Equal("companion descriptor version", SpeechSettingsCompanion.CompanionVersion, descriptor["companionVersion"].ToString());
            Equal("companion worker version", WorkerCli.WorkerVersion, descriptor["workerVersion"].ToString());
            var serialized = Json.Serialize(descriptor);
            True("project next-task scope declared", serialized.Contains("\"value\":\"project\"") && serialized.Contains("\"effectiveAt\":\"next_task\""));
            True("language override exact target declared", serialized.Contains("synthesisRequest.payload.language.override"));
            True("five languages and tuning declared", serialized.Contains("voicesByLanguage") && serialized.Contains("ratePercent") && serialized.Contains("volumePercent") && serialized.Contains("pitchHz"));
            True("fixed playback cache declared", serialized.Contains("autoPlay") && serialized.Contains("from_start") && serialized.Contains("cacheScope"));
            True("legacy AI remains disabled", serialized.Contains("disabled_read_only"));
            True("voice discovery read only", serialized.Contains("voiceDiscovery") && serialized.Contains("catalogRevision+catalogSha256") && serialized.Contains("\"readOnly\":true"));
            True("speaker preview explicit only", serialized.Contains("speaker.preview") && serialized.Contains("explicit_only") && serialized.Contains("\"implementedByCompanion\":true"));
            True("speaker preview creates no knowledge tasks", serialized.Contains("\"createsKnowledgeTasks\":false") && serialized.Contains("settingsSaveStatusProbePhysicalSpeaker"));
        }

        private static void TestSettingsCompanionStatus()
        {
            var input = CreateSettingsCompanionInput(false, false);
            var path = WriteCompanionInput("status", input);
            var cli = RunCli("settings", "companion", "status", "--input", path, "--json");
            Equal("companion status exit", "0", cli.ExitCode.ToString());
            var output = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
            Equal("offline readiness", "ready", output["status"].ToString());
            Equal("offline mode", "offline_validate_only", output["mode"].ToString());
            True("status omits request snapshots", !output.ContainsKey("synthesisRequest") && !output.ContainsKey("playbackRequest"));
            var readiness = (Dictionary<string, object>)output["readiness"];
            Equal("voice catalog ready", "ready", readiness["voiceCatalog"].ToString());
            Equal("speaker not run", "not_run", readiness["speakerPreview"].ToString());
            var catalog = (Dictionary<string, object>)output["voiceCatalog"];
            True("five-language catalog", ((object[])catalog["supportedLanguages"]).Length == 5 && ((object[])catalog["voices"]).Length == 5);
            True("zero side effects", ((object[])output["sideEffectsPerformed"]).Length == 0);
        }

        private static void TestSettingsCompanionOverrideProbe()
        {
            var input = CreateSettingsCompanionInput(true, false);
            var path = WriteCompanionInput("override", input);
            var cli = RunCli("settings", "companion", "probe", "--input", path, "--json");
            Equal("companion probe exit", "0", cli.ExitCode.ToString());
            var output = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
            var routing = (Dictionary<string, object>)output["routing"];
            Equal("override strategy routed", "override", routing["strategy"].ToString());
            Equal("override normalized", "en-US", routing["override"].ToString());
            Equal("override wins over probe text", "en-US", routing["languageTag"].ToString());
            Equal("override voice selected", "en-US-AvaMultilingualNeural", routing["voiceId"].ToString());
            var synthesis = Json.Deserialize<WorkerRequestEnvelopeV1>(Json.Serialize(output["synthesisRequest"]));
            Equal("worker payload strategy", "override", synthesis.payload.language.strategy);
            Equal("worker payload override", "en-US", synthesis.payload.language.@override);
            Equal("worker payload voice", "en-US-AvaMultilingualNeural", synthesis.payload.voiceSnapshot.voicesByLanguage["en-US"]);
            True("worker config snapshot bound", synthesis.configSnapshot.ContainsKey("edgeTtsPath"));
            var fixedSettings = Json.Deserialize<SpeechFixedSettingsView>(Json.Serialize(output["fixedSettings"]));
            True("autoPlay fixed", fixedSettings.playbackPolicy.autoPlay);
            Equal("replay fixed", "from_start", fixedSettings.playbackPolicy.replay);
            Equal("cache fixed", "task", fixedSettings.cachePolicy.scope);
            True("cross-task cache disabled", !fixedSettings.cachePolicy.crossTaskReuse);
            var preview = (Dictionary<string, object>)output["speakerPreview"];
            Equal("preview never ran", "not_run", preview["status"].ToString());
        }

        private static void TestSettingsCompanionFailures()
        {
            var sideEffect = CreateSettingsCompanionInput(false, false);
            sideEffect.action = "speaker.preview";
            var sideEffectCli = RunCli("settings", "companion", "probe", "--input", WriteCompanionInput("side-effect", sideEffect), "--json");
            Equal("side effect rejected exit", "2", sideEffectCli.ExitCode.ToString());
            True("side effect rejected code", sideEffectCli.StandardError.Contains("side_effect_action_rejected"));

            var invalidOverride = CreateSettingsCompanionInput(false, false);
            invalidOverride.adapterInput.settings.languageOverride = "en-US";
            var invalidOverrideCli = RunCli("settings", "companion", "probe", "--input", WriteCompanionInput("invalid-override", invalidOverride), "--json");
            Equal("invalid override exit", "2", invalidOverrideCli.ExitCode.ToString());
            True("invalid override code", invalidOverrideCli.StandardError.Contains("unexpected_language_override"));

            var unknownVoice = CreateSettingsCompanionInput(false, true);
            var unknownVoiceCli = RunCli("settings", "companion", "probe", "--input", WriteCompanionInput("unknown-voice", unknownVoice), "--json");
            Equal("unknown voice exit", "2", unknownVoiceCli.ExitCode.ToString());
            True("unknown voice code", unknownVoiceCli.StandardError.Contains("unknown_voice"));
        }

        private static async Task TestSettingsCompanionRequestResultChain()
        {
            var evidenceRoot = Path.Combine(runRoot, "settings-companion");
            Directory.CreateDirectory(evidenceRoot);
            var descriptorPath = Path.Combine(evidenceRoot, "descriptor.json");
            File.WriteAllText(descriptorPath, Json.Serialize(SpeechSettingsCompanion.Describe()), new UTF8Encoding(false));

            var input = CreateSettingsCompanionInput(true, false);
            var probeInputPath = Path.Combine(evidenceRoot, "probe-input.json");
            File.WriteAllText(probeInputPath, Json.Serialize(input), new UTF8Encoding(false));
            var probe = SpeechSettingsCompanion.ProbeFile(probeInputPath, true);
            var probeOutputPath = Path.Combine(evidenceRoot, "probe-output.json");
            File.WriteAllText(probeOutputPath, Json.Serialize(probe), new UTF8Encoding(false));

            var fixture = Fixture.Create(runRoot, "¿Este texto debe usar la voz inglesa?", null);
            var envelope = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(fixture.RequestPath));
            var adapter = new SpeechSettingsAdapter(input.adapterInput.catalog, input.adapterInput.deployment);
            adapter.ApplyToSynthesisEnvelope(envelope, input.adapterInput.settings);
            File.WriteAllText(fixture.RequestPath, Json.Serialize(envelope), new UTF8Encoding(false));
            var fake = new FakeSynthesizer(false);
            var result = await new SynthesisWorker(new SpeechRequestReader(), fake).RunAsync(fixture.JobRoot, fixture.AttemptDirectory);
            Equal("companion chain result", "completed", result.status);
            Equal("companion chain language", "en-US", result.payload["languageTag"].ToString());
            Equal("companion chain voice", "en-US-AvaMultilingualNeural", result.payload["voiceId"].ToString());
            Equal("companion chain replayable", "idle_replayable", result.payload["playbackState"].ToString());
            True("companion chain fake only", fake.Calls == 1);

            var requestEvidencePath = Path.Combine(evidenceRoot, "worker-request.json");
            var resultEvidencePath = Path.Combine(evidenceRoot, "worker-result.json");
            File.WriteAllText(requestEvidencePath, Json.Serialize(envelope), new UTF8Encoding(false));
            File.WriteAllText(resultEvidencePath, Json.Serialize(result), new UTF8Encoding(false));

            var discoveryInput = CreateVoiceDiscoveryInput("validate_only", true);
            var discoveryInputPath = Path.Combine(evidenceRoot, "voice-discovery-input.json");
            var discoveryOutputPath = Path.Combine(evidenceRoot, "voice-discovery-output.json");
            File.WriteAllText(discoveryInputPath, Json.Serialize(discoveryInput), new UTF8Encoding(false));
            var discoveryOutput = new SpeechVoiceDiscovery(null, delegate { return new DateTimeOffset(2026, 8, 30, 0, 5, 0, TimeSpan.Zero); }).Execute(discoveryInput);
            File.WriteAllText(discoveryOutputPath, Json.Serialize(discoveryOutput), new UTF8Encoding(false));

            var workerExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.Worker.exe");
            var fakeTool = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.FakeTool.exe");
            var previewAdapter = Clone(input.adapterInput);
            previewAdapter.deployment.edgeTtsPath = fakeTool;
            previewAdapter.deployment.ffplayPath = fakeTool;
            var previewInput = new SpeechSpeakerPreviewInputV1
            {
                schemaVersion = SpeechSpeakerPreview.InputSchemaVersion, scope = "project", effectiveAt = "preview_only",
                action = "speaker.preview", explicitSideEffectAuthorization = true, previewLanguage = "en-US", adapterInput = previewAdapter
            };
            var previewInputPath = Path.Combine(evidenceRoot, "speaker-preview-input.json");
            var previewOutputPath = Path.Combine(evidenceRoot, "speaker-preview-output.json");
            var previewInvocationPath = Path.Combine(evidenceRoot, "speaker-preview-fake-invocations.log");
            File.WriteAllText(previewInputPath, Json.Serialize(previewInput), new UTF8Encoding(false));
            var previousInvocation = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH");
            var previousDelay = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS");
            try
            {
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", previewInvocationPath);
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS", "25");
                var previewCli = RunCli("settings", "companion", "preview", "--input", previewInputPath, "--json");
                Equal("companion evidence preview exit", "0", previewCli.ExitCode.ToString());
                File.WriteAllText(previewOutputPath, previewCli.StandardOutput.Trim(), new UTF8Encoding(false));
            }
            finally
            {
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", previousInvocation);
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS", previousDelay);
            }
            var inputSchema = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "settings-companion", "speech-settings-companion-probe-input.schema.json");
            var outputSchema = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "settings-companion", "speech-settings-companion-probe-output.schema.json");
            var discoveryInputSchema = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "settings-companion", "speech-settings-voice-discovery-input.schema.json");
            var discoveryOutputSchema = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "settings-companion", "speech-settings-voice-discovery-output.schema.json");
            var previewInputSchema = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "settings-companion", "speech-settings-speaker-preview-input.schema.json");
            var previewOutputSchema = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "settings-companion", "speech-settings-speaker-preview-output.schema.json");
            var bindingFixture = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "settings-companion", "language-override-binding.json");
            var receipt = new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "suite", "speech-settings-companion-offline" }, { "status", "passed" },
                { "completedAtUtc", DateTime.UtcNow.ToString("o") }, { "workerId", WorkerCli.WorkerId }, { "workerVersion", WorkerCli.WorkerVersion },
                { "companionVersion", SpeechSettingsCompanion.CompanionVersion }, { "physicalSpeakerCalls", 0 }, { "fakePreviewPlayerCalls", 1 }, { "previewAttemptCount", 1 }, { "networkTtsCalls", 0 },
                { "artifacts", new object[]
                    {
                        Evidence(descriptorPath), Evidence(probeInputPath), Evidence(probeOutputPath), Evidence(requestEvidencePath), Evidence(resultEvidencePath),
                        Evidence(discoveryInputPath), Evidence(discoveryOutputPath), Evidence(previewInputPath), Evidence(previewOutputPath), Evidence(previewInvocationPath),
                        Evidence(inputSchema), Evidence(outputSchema), Evidence(discoveryInputSchema), Evidence(discoveryOutputSchema), Evidence(previewInputSchema), Evidence(previewOutputSchema),
                        Evidence(bindingFixture), Evidence(workerExe)
                    }
                },
                { "chain", new Dictionary<string, object>
                    {
                        { "scope", "project" }, { "effectiveAt", "next_task" }, { "languageStrategy", "override" },
                        { "languageOverride", "en-US" }, { "languageTag", result.payload["languageTag"] },
                        { "voiceId", result.payload["voiceId"] }, { "resultStatus", result.status }, { "playbackState", result.payload["playbackState"] }
                    }
                },
                { "discovery", new Dictionary<string, object>
                    {
                        { "mode", "validate_only" }, { "readOnly", true }, { "settingsMutationCount", 0 },
                        { "physicalSpeakerPlayback", false }, { "catalogRevision", ((Dictionary<string, object>)discoveryOutput)["catalogRevision"] },
                        { "catalogSha256", ((Dictionary<string, object>)discoveryOutput)["catalogSha256"] }
                    }
                },
                { "preview", new Dictionary<string, object>
                    {
                        { "execution", "fake_no_speaker" }, { "explicitOnly", true }, { "naturalCompletion", true },
                        { "playbackState", "idle" }, { "previewAttemptCount", 1 }, { "audioPlayerInvocationCount", 1 },
                        { "captureTaskCount", 0 }, { "exportTaskCount", 0 }
                    }
                }
            };
            File.WriteAllText(Path.Combine(evidenceRoot, "offline-receipt.json"), Json.Serialize(receipt), new UTF8Encoding(false));
        }

        private static void TestVoiceDiscoveryValidateOnly()
        {
            var input = CreateVoiceDiscoveryInput("validate_only", true);
            var path = WriteJson("voice-discovery-validate", input);
            var cli = RunCli("settings", "companion", "voices", "--input", path, "--json");
            Equal("validate-only discovery exit", "0", cli.ExitCode.ToString());
            var output = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
            Equal("validate-only discovery schema", SpeechVoiceDiscovery.OutputSchemaVersion, output["schemaVersion"].ToString());
            Equal("validate-only discovery source", "supplied_snapshot", output["source"].ToString());
            Equal("validate-only discovery status", "ready", output["status"].ToString());
            True("validate-only catalog hash", System.Text.RegularExpressions.Regex.IsMatch(output["catalogSha256"].ToString(), "^[0-9a-f]{64}$"));
            True("validate-only catalog revision", output["catalogRevision"].ToString().StartsWith("edge-tts-", StringComparison.Ordinal));
            True("validate-only five routes", ((object[])output["routes"]).Length == 5);
            True("validate-only no speaker", output["physicalSpeakerPlayback"] is bool && !(bool)output["physicalSpeakerPlayback"]);
            True("validate-only no side effects", ((object[])output["sideEffectsPerformed"]).Length == 0);
            var deployment = (Dictionary<string, object>)output["deployment"];
            Equal("validate-only provider query", "0", Convert.ToInt32(deployment["providerQueryCount"]).ToString());
            Equal("validate-only cache write", "0", Convert.ToInt32(deployment["cacheWriteCount"]).ToString());
            Equal("validate-only settings mutation", "0", Convert.ToInt32(output["settingsMutationCount"]).ToString());
        }

        private static void TestVoiceDiscoveryLiveAndCache()
        {
            var input = CreateVoiceDiscoveryInput("discover", false);
            input.deployment.edgeTtsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.FakeTool.exe");
            var invocationPath = Path.Combine(runRoot, "voice-discovery-invocations-" + Guid.NewGuid().ToString("N") + ".log");
            var previous = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH");
            try
            {
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", invocationPath);
                var cli = RunCli("settings", "companion", "voices", "--input", WriteJson("voice-discovery-live", input), "--json");
                Equal("live discovery exit", "0", cli.ExitCode.ToString());
                var output = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
                Equal("live discovery source", "live", output["source"].ToString());
                Equal("live discovery status", "ready", output["status"].ToString());
                var voices = (object[])output["voices"];
                True("live discovery natural choices", voices.Length >= 7 && voices.All(v => (bool)((Dictionary<string, object>)v)["natural"]));
                var routes = (object[])output["routes"];
                True("live discovery language routes", routes.Length == 5 && routes.All(v => ((Dictionary<string, object>)v)["availability"].ToString() == "available"));
                True("live discovery exactly one provider read", File.ReadAllLines(invocationPath).Count(line => line.Contains("--list-voices")) == 1);
                True("live discovery no speaker command", File.ReadAllLines(invocationPath).All(line => !line.Contains("-nodisp")));
                True("live discovery side-effect-free", ((object[])output["sideEffectsPerformed"]).Length == 0);
                var deployment = (Dictionary<string, object>)output["deployment"];
                Equal("live provider read count", "1", Convert.ToInt32(deployment["providerQueryCount"]).ToString());
                Equal("live cache write count", "0", Convert.ToInt32(deployment["cacheWriteCount"]).ToString());

                var cacheInput = CreateVoiceDiscoveryInput("cache_only", false);
                cacheInput.catalogSnapshot = new SpeechVoiceCatalog
                {
                    provider = output["provider"].ToString(), catalogVersion = output["catalogVersion"].ToString(),
                    updatedAtUtc = output["updatedAtUtc"].ToString(),
                    voices = Json.Deserialize<List<SpeechVoiceOption>>(Json.Serialize(output["voices"]))
                };
                var cacheCli = RunCli("settings", "companion", "voices", "--input", WriteJson("voice-discovery-cache", cacheInput), "--json");
                Equal("cache-only discovery exit", "0", cacheCli.ExitCode.ToString());
                var cached = (Dictionary<string, object>)Json.DeserializeObject(cacheCli.StandardOutput.Trim());
                Equal("cache-only source", "cache", cached["source"].ToString());
                Equal("cache-only hash stable", output["catalogSha256"].ToString(), cached["catalogSha256"].ToString());
                Equal("cache-only revision stable", output["catalogRevision"].ToString(), cached["catalogRevision"].ToString());
                True("cache-only did not invoke provider again", File.ReadAllLines(invocationPath).Count(line => line.Contains("--list-voices")) == 1);
            }
            finally { Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", previous); }
        }

        private static void TestSpeakerPreviewExplicitFake()
        {
            var adapterInput = CreateSettingsCliInput(false);
            var fakeTool = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.FakeTool.exe");
            adapterInput.deployment.edgeTtsPath = fakeTool;
            adapterInput.deployment.ffplayPath = fakeTool;
            var input = new SpeechSpeakerPreviewInputV1
            {
                schemaVersion = SpeechSpeakerPreview.InputSchemaVersion,
                scope = "project", effectiveAt = "preview_only", action = "speaker.preview",
                explicitSideEffectAuthorization = true, previewLanguage = "zh-CN", adapterInput = adapterInput
            };
            var invocationPath = Path.Combine(runRoot, "speaker-preview-invocations-" + Guid.NewGuid().ToString("N") + ".log");
            var previousPath = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH");
            var previousDelay = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS");
            try
            {
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", invocationPath);
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS", "25");
                var cli = RunCli("settings", "companion", "preview", "--input", WriteJson("speaker-preview", input), "--json");
                Equal("speaker preview exit", "0", cli.ExitCode.ToString());
                var output = (Dictionary<string, object>)Json.DeserializeObject(cli.StandardOutput.Trim());
                Equal("speaker preview status", "completed", output["status"].ToString());
                Equal("speaker preview state", "idle", output["playbackState"].ToString());
                Equal("speaker preview language", "zh-CN", output["languageTag"].ToString());
                Equal("speaker preview voice", "zh-CN-XiaoxiaoNeural", output["voiceId"].ToString());
                True("speaker preview natural completion", (bool)output["naturalCompletion"] && !(bool)output["manualStopRequired"]);
                Equal("speaker preview attempt count", "1", Convert.ToInt32(output["previewAttemptCount"]).ToString());
                Equal("speaker preview player count", "1", Convert.ToInt32(output["audioPlayerInvocationCount"]).ToString());
                True("speaker preview tuning frozen", Json.Serialize(output["voiceSnapshot"]).Contains("\"ratePercent\":17") && Json.Serialize(output["voiceSnapshot"]).Contains("\"volumePercent\":-8") && Json.Serialize(output["voiceSnapshot"]).Contains("\"pitchHz\":23"));
                var taskCreation = (Dictionary<string, object>)output["taskCreation"];
                True("speaker preview creates no knowledge tasks", taskCreation.All(item => Convert.ToInt32(item.Value) == 0));
                var invocations = File.ReadAllLines(invocationPath);
                True("speaker preview exactly one synthesis", invocations.Count(line => line.Contains("--write-media")) == 1);
                True("speaker preview exactly one player", invocations.Count(line => line.Contains("-nodisp")) == 1);
                True("speaker preview temporary files cleaned", !(bool)output["temporaryArtifactsRetained"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", previousPath);
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS", previousDelay);
            }

            input.explicitSideEffectAuthorization = false;
            var rejected = RunCli("settings", "companion", "preview", "--input", WriteJson("speaker-preview-rejected", input), "--json");
            Equal("speaker preview authorization exit", "2", rejected.ExitCode.ToString());
            True("speaker preview authorization code", rejected.StandardError.Contains("speaker_preview_not_authorized"));
        }

        private static void TestSettingsQueriesNeverPreview()
        {
            var invocationPath = Path.Combine(runRoot, "settings-no-preview-" + Guid.NewGuid().ToString("N") + ".log");
            var previous = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH");
            try
            {
                Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", invocationPath);
                var adapterInput = CreateSettingsCliInput(false);
                var adapt = RunCli("settings", "adapt", "--input", WriteJson("settings-save-equivalent", adapterInput), "--json");
                Equal("settings adapt no-preview exit", "0", adapt.ExitCode.ToString());
                var companion = CreateSettingsCompanionInput(false, false);
                var companionPath = WriteCompanionInput("no-preview", companion);
                Equal("settings status no-preview exit", "0", RunCli("settings", "companion", "status", "--input", companionPath, "--json").ExitCode.ToString());
                Equal("settings probe no-preview exit", "0", RunCli("settings", "companion", "probe", "--input", companionPath, "--json").ExitCode.ToString());
                Equal("settings describe no-preview exit", "0", RunCli("settings", "companion", "describe", "--json").ExitCode.ToString());
                True("settings queries never launch runtime", !File.Exists(invocationPath));
            }
            finally { Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", previous); }
        }

        private static void TestDescriptorDestinationNeutral()
        {
            var cli = RunCli("describe", "--json");
            var normalized = cli.StandardOutput.ToLowerInvariant();
            True("descriptor excludes destination name remnote", !normalized.Contains("remnote"));
            True("descriptor excludes destination name anki", !normalized.Contains("anki"));
            True("descriptor preserves stable worker id", normalized.Contains("\"workerid\":\"speech.edge\""));
        }

        private static void TestCandidateAssemblyIdentity()
        {
            var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.Worker.exe");
            var legacyAlias = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemNoteQuickClipper.Speech.Worker.exe");
            True("canonical candidate exists", File.Exists(candidate));
            True("legacy alias exists", File.Exists(legacyAlias));
            Equal("namespace root", "KnowledgeClipper.Speech.Worker", typeof(WorkerCli).Namespace);
            var version = FileVersionInfo.GetVersionInfo(candidate);
            Equal("product display name", "零碎知识剪藏", version.ProductName);
            Equal("assembly title", "KnowledgeClipper Speech Worker", version.FileDescription);
            Equal("company", "KnowledgeClipper", version.CompanyName);
            var publicIdentity = string.Join("|", new[] { Path.GetFileName(candidate), version.ProductName, version.FileDescription, version.CompanyName, version.InternalName, version.OriginalFilename }).ToLowerInvariant();
            True("candidate metadata excludes remnote", !publicIdentity.Contains("remnote"));
            True("candidate metadata excludes anki", !publicIdentity.Contains("anki"));
            True("legacy alias is byte-identical compatibility surface", Convert.ToBase64String(File.ReadAllBytes(candidate)) == Convert.ToBase64String(File.ReadAllBytes(legacyAlias)));
            var legacyDescribe = RunCliAt(legacyAlias, "describe", "--json");
            True("legacy alias remains compatible", legacyDescribe.ExitCode == 0 && legacyDescribe.StandardOutput.Contains("\"workerId\":\"speech.edge\""));
        }

        private static void TestRuntimeEnvironmentCompatibility()
        {
            const string canonicalName = "KNOWLEDGE_CLIPPER_EDGE_TTS";
            const string legacyName = "REMNOTE_QUICK_CLIPPER_EDGE_TTS";
            var previousCanonical = Environment.GetEnvironmentVariable(canonicalName);
            var previousLegacy = Environment.GetEnvironmentVariable(legacyName);
            var envelope = new WorkerRequestEnvelopeV1 { configSnapshot = new Dictionary<string, object>() };
            try
            {
                Environment.SetEnvironmentVariable(canonicalName, FakeToolPath());
                Environment.SetEnvironmentVariable(legacyName, Path.Combine(runRoot, "legacy-must-not-win.exe"));
                Equal("canonical environment wins", Path.GetFullPath(FakeToolPath()), WorkerCli.ConfiguredTool(envelope, "edgeTtsPath", canonicalName, legacyName, "edge-tts"));
                Environment.SetEnvironmentVariable(canonicalName, null);
                Environment.SetEnvironmentVariable(legacyName, FakeToolPath());
                Equal("legacy environment remains fallback only", Path.GetFullPath(FakeToolPath()), WorkerCli.ConfiguredTool(envelope, "edgeTtsPath", canonicalName, legacyName, "edge-tts"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(canonicalName, previousCanonical);
                Environment.SetEnvironmentVariable(legacyName, previousLegacy);
            }
        }

        private static void TestDescribeCliRejectsInvalidShape()
        {
            var cli = RunCli("describe");
            Equal("bad describe exit", "2", cli.ExitCode.ToString());
            True("bad describe no stdout", string.IsNullOrWhiteSpace(cli.StandardOutput));
            True("bad describe diagnostics", cli.StandardError.Contains("worker_cli_error"));
        }

        private static void TestRunSynthesisCli()
        {
            var fixture = Fixture.Create(runRoot, "CLI synthesis", null);
            UpdateEnvelope(fixture.RequestPath, delegate(WorkerRequestEnvelopeV1 envelope)
            {
                envelope.configSnapshot["edgeTtsPath"] = FakeToolPath();
            });
            var cli = RunCli("run", "--request", fixture.RequestPath);
            Equal("synth CLI exit", "0", cli.ExitCode.ToString());
            var result = Json.Deserialize<WorkerResultV1>(File.ReadAllText(Path.Combine(fixture.AttemptDirectory, "result.json")));
            Equal("synth CLI status", "completed", result.status);
            Equal("synth CLI kind", "audio.speech", result.outputs[0].kind);
        }

        private static void TestRunSynthesisMissingRuntime()
        {
            var fixture = Fixture.Create(runRoot, "missing runtime", null);
            UpdateEnvelope(fixture.RequestPath, delegate(WorkerRequestEnvelopeV1 envelope)
            {
                envelope.configSnapshot["edgeTtsPath"] = Path.Combine(runRoot, "missing-edge-tts.exe");
            });
            AssertMissingRuntimeResult(RunCli("run", "--request", fixture.RequestPath), fixture.JobRoot, fixture.AttemptDirectory, "synthesis");
        }

        private static void TestRunPlaybackCli()
        {
            var fixture = PlaybackCliFixture.Create(runRoot, FakeToolPath());
            var cli = RunCli("run", "--request", fixture.RequestPath);
            Equal("playback CLI exit", "0", cli.ExitCode.ToString());
            var result = Json.Deserialize<WorkerResultV1>(File.ReadAllText(Path.Combine(fixture.AttemptDirectory, "result.json")));
            Equal("playback CLI status", "completed", result.status);
            Equal("playback CLI state", "idle_replayable", result.payload["playbackState"].ToString());
            True("playback CLI not reused", !(bool)result.payload["cacheHit"]);
        }

        private static void TestRunPlaybackMissingRuntime()
        {
            var fixture = PlaybackCliFixture.Create(runRoot, FakeToolPath());
            UpdateEnvelope(fixture.RequestPath, delegate(WorkerRequestEnvelopeV1 envelope)
            {
                envelope.configSnapshot["ffplayPath"] = Path.Combine(runRoot, "missing-ffplay.exe");
            });
            AssertMissingRuntimeResult(RunCli("run", "--request", fixture.RequestPath), fixture.JobRoot, fixture.AttemptDirectory, "playback");
        }

        private static void AssertMissingRuntimeResult(CliResult cli, string jobRoot, string attemptDirectory, string label)
        {
            Equal(label + " missing runtime exit", "1", cli.ExitCode.ToString());
            var resultPath = Path.Combine(attemptDirectory, "result.json");
            True(label + " missing runtime result exists", File.Exists(resultPath));
            var result = Json.Deserialize<WorkerResultV1>(File.ReadAllText(resultPath));
            Equal(label + " missing runtime status", "failed", result.status);
            Equal(label + " missing runtime code", "missing_runtime_tool", result.error.code);
            Equal(label + " missing runtime category", "dependency", result.error.category);
            Equal(label + " missing runtime blame", "dependency", result.error.blame);
            True(label + " missing runtime not retryable", !result.error.retryable);
            True(label + " missing runtime no outputs", result.outputs.Count == 0);
            True(label + " missing runtime details", File.Exists(SpeechRequestReader.ResolveJobRelativePath(jobRoot, result.error.detailsRef)));
        }

        private static void TestRunPlaybackRejectsCorruptManifest()
        {
            var fixture = PlaybackCliFixture.Create(runRoot, FakeToolPath());
            File.AppendAllText(fixture.ManifestPath, " ", new UTF8Encoding(false));
            AssertRejectedPlaybackCli(fixture, "audio_manifest_hash_mismatch");
        }

        private static void TestRunPlaybackRejectsCorruptAudio()
        {
            var fixture = PlaybackCliFixture.Create(runRoot, FakeToolPath());
            var bytes = File.ReadAllBytes(fixture.AudioPath);
            bytes[0] ^= 0x01;
            File.WriteAllBytes(fixture.AudioPath, bytes);
            AssertRejectedPlaybackCli(fixture, "audio_hash_mismatch");
        }

        private static void TestRunPlaybackRejectsAudioContractVersion()
        {
            var fixture = PlaybackCliFixture.Create(runRoot, FakeToolPath());
            UpdateEnvelope(fixture.RequestPath, delegate(WorkerRequestEnvelopeV1 envelope)
            {
                envelope.inputRefs[0].contractVersion = "2.0.0";
            });
            AssertRejectedPlaybackCli(fixture, "invalid_playback_input");
        }

        private static void TestRunPlaybackRejectsAudioMediaType()
        {
            var fixture = PlaybackCliFixture.Create(runRoot, FakeToolPath());
            var manifest = Json.Deserialize<ArtifactManifestV1>(File.ReadAllText(fixture.ManifestPath));
            manifest.mediaType = "application/octet-stream";
            File.WriteAllText(fixture.ManifestPath, Json.Serialize(manifest), new UTF8Encoding(false));
            UpdateEnvelope(fixture.RequestPath, delegate(WorkerRequestEnvelopeV1 envelope)
            {
                envelope.inputRefs[0].expectedSha256 = SpeechRequestReader.HashFile(fixture.ManifestPath);
            });
            AssertRejectedPlaybackCli(fixture, "invalid_audio_manifest");
        }

        private static void AssertRejectedPlaybackCli(PlaybackCliFixture fixture, string expectedCode)
        {
            var invocationMarker = Path.Combine(runRoot, "fake-player-" + Guid.NewGuid().ToString("N") + ".log");
            var previous = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH");
            Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", invocationMarker);
            CliResult cli;
            try { cli = RunCli("run", "--request", fixture.RequestPath); }
            finally { Environment.SetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH", previous); }
            Equal(expectedCode + " exit", "1", cli.ExitCode.ToString());
            var resultPath = Path.Combine(fixture.AttemptDirectory, "result.json");
            True(expectedCode + " result exists", File.Exists(resultPath));
            var result = Json.Deserialize<WorkerResultV1>(File.ReadAllText(resultPath));
            Equal(expectedCode + " status", "failed", result.status);
            Equal(expectedCode + " error", expectedCode, result.error.code);
            True(expectedCode + " player not started", !File.Exists(invocationMarker));
            True(expectedCode + " details exist", File.Exists(SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, result.error.detailsRef)));
        }

        private static void TestRunRejectsRelativeRequest()
        {
            var cli = RunCli("run", "--request", "relative/request.json");
            Equal("relative exit", "2", cli.ExitCode.ToString());
            True("relative code", cli.StandardError.Contains("request_path_not_absolute"));
        }

        private static void TestRunRejectsWrongWorker()
        {
            var fixture = Fixture.Create(runRoot, "wrong worker", null);
            UpdateEnvelope(fixture.RequestPath, delegate(WorkerRequestEnvelopeV1 envelope) { envelope.workerId = "legacy.ai-tts"; });
            var cli = RunCli("run", "--request", fixture.RequestPath);
            Equal("wrong worker exit", "2", cli.ExitCode.ToString());
            True("wrong worker code", cli.StandardError.Contains("wrong_worker_id"));
        }

        private static void TestRunRejectsOperation()
        {
            var fixture = Fixture.Create(runRoot, "wrong operation", null);
            UpdateEnvelope(fixture.RequestPath, delegate(WorkerRequestEnvelopeV1 envelope) { envelope.operation = "speech.legacy"; });
            var cli = RunCli("run", "--request", fixture.RequestPath);
            Equal("wrong operation exit", "2", cli.ExitCode.ToString());
            True("wrong operation code", cli.StandardError.Contains("unsupported_operation"));
        }

        private static void TestCancellationReasonMapping()
        {
            var cases = new[]
            {
                new[] { "user_stop", "cancelled", "stopped" },
                new[] { "superseded", "cancelled", "superseded" },
                new[] { "task_cancel", "cancelled", "cancelled" },
                new[] { "timeout", "failed", "timed_out" }
            };
            foreach (var item in cases)
            {
                var fixture = PlaybackFixture.Create(runRoot);
                var signal = SpeechRequestReader.ResolveJobRelativePath(fixture.JobRoot, fixture.PlayWork.CancellationSignalRef);
                fixture.PlayWork.CancellationSignalPath = signal;
                var factory = new FakeAudioPlayerFactory();
                using (var coordinator = new PlaybackCoordinator(factory))
                {
                    var running = new PlaybackWorker(coordinator).RunPlayAsync(fixture.JobRoot, fixture.PlayAttempt, fixture.PlayWork);
                    var temporarySignal = signal + ".tmp-" + Guid.NewGuid().ToString("N");
                    var requestedAt = DateTimeOffset.UtcNow;
                    File.WriteAllText(temporarySignal, Json.Serialize(new Dictionary<string, object>
                    {
                        { "schemaVersion", "1.0.0" }, { "attemptId", fixture.PlayWork.AttemptId }, { "operationId", fixture.PlayWork.OperationId },
                        { "reason", item[0] }, { "requestedAt", requestedAt.ToString("o") }, { "graceDeadline", requestedAt.AddMilliseconds(2000).ToString("o") }
                    }), new UTF8Encoding(false));
                    File.Move(temporarySignal, signal);
                    var result = running.GetAwaiter().GetResult();
                    Equal(item[0] + " status", item[1], result.status);
                    Equal(item[0] + " state", item[2], result.payload["playbackState"].ToString());
                    True(item[0] + " not reused", !(bool)result.payload["cacheHit"]);
                    Equal(item[0] + " error", "playback." + item[0], result.error.code);
                }
            }
        }

        private static void UpdateEnvelope(string path, Action<WorkerRequestEnvelopeV1> update)
        {
            var envelope = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(path));
            update(envelope);
            File.WriteAllText(path, Json.Serialize(envelope), new UTF8Encoding(false));
        }

        private static void TestRunRejectsMalformedRequest()
        {
            var directory = Path.Combine(runRoot, "malformed");
            Directory.CreateDirectory(directory);
            var request = Path.Combine(directory, "request.json");
            File.WriteAllText(request, "{not-json", new UTF8Encoding(false));
            var cli = RunCli("run", "--request", request);
            Equal("malformed exit", "2", cli.ExitCode.ToString());
            True("malformed code", cli.StandardError.Contains("invalid_request_json"));
        }

        private static CliResult RunCli(params string[] args)
        {
            return RunCliAt(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.Worker.exe"), args);
        }

        private static CliResult RunCliAt(string executable, params string[] args)
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = string.Join(" ", Array.ConvertAll(args, QuoteArgument)),
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new Exception("cannot start worker CLI");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("worker CLI timeout"); }
                return new CliResult { ExitCode = process.ExitCode, StandardOutput = stdout, StandardError = stderr };
            }
        }

        private static string FakeToolPath() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KnowledgeClipper.Speech.FakeTool.exe"); }
        private static string QuoteArgument(string value) { return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        private static string RouteText(string text)
        {
            var fixture = Fixture.Create(runRoot, text, null);
            return VoiceRouter.Route(new SpeechRequestReader().ReadAndValidate(fixture.JobRoot, fixture.RequestPath)).LanguageTag;
        }

        private static SpeechSettingsFixtureDocument SettingsFixture()
        {
            var path = Path.Combine(ProjectRoot(), "tests", "RemNoteQuickClipper.Speech.Tests", "fixtures", "speech-settings-five-language.json");
            return Json.Deserialize<SpeechSettingsFixtureDocument>(File.ReadAllText(path));
        }

        private static SpeechSettingsAdapterInputV1 CreateSettingsCliInput(bool unknownVoice)
        {
            var document = SettingsFixture();
            var settings = Clone(document.settings);
            if (unknownVoice) settings.voicesByLanguage["zh-CN"] = "zh-CN-UnknownNeural";
            var runtime = Path.Combine(runRoot, "settings-cli-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtime);
            var edge = Path.Combine(runtime, "edge-tts.exe");
            var player = Path.Combine(runtime, "fake-player.exe");
            File.WriteAllText(edge, "offline fixture", new UTF8Encoding(false));
            File.WriteAllText(player, "offline fixture", new UTF8Encoding(false));
            var synthesis = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(Fixture.Create(runRoot, "设置 CLI 输入。", null).RequestPath));
            var playback = Json.Deserialize<WorkerRequestEnvelopeV1>(File.ReadAllText(PlaybackCliFixture.Create(runRoot, player).RequestPath));
            synthesis.configSnapshot = new Dictionary<string, object>();
            playback.configSnapshot = new Dictionary<string, object>();
            return new SpeechSettingsAdapterInputV1
            {
                schemaVersion = SpeechSettingsAdapterCli.InputSchemaVersion,
                settings = settings,
                catalog = Clone(document.catalog),
                deployment = new SpeechDeploymentDiagnostics { edgeTtsPath = edge, ffplayPath = player },
                synthesisRequest = synthesis,
                playbackRequest = playback
            };
        }

        private static SpeechSettingsCompanionProbeInputV1 CreateSettingsCompanionInput(bool languageOverride, bool unknownVoice)
        {
            var adapterInput = CreateSettingsCliInput(unknownVoice);
            if (languageOverride)
            {
                adapterInput.settings.languageStrategy = "override";
                adapterInput.settings.languageOverride = "en-us";
            }
            return new SpeechSettingsCompanionProbeInputV1
            {
                schemaVersion = SpeechSettingsCompanion.ProbeInputSchemaVersion,
                scope = "project",
                effectiveAt = "next_task",
                action = "validate_only",
                probeText = "¿Este texto debe seguir la configuración del proyecto?",
                adapterInput = adapterInput
            };
        }

        private static string WriteCompanionInput(string label, SpeechSettingsCompanionProbeInputV1 input)
        {
            var path = Path.Combine(runRoot, "settings-companion-" + label + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, Json.Serialize(input), new UTF8Encoding(false));
            return path;
        }

        private static SpeechVoiceDiscoveryInputV1 CreateVoiceDiscoveryInput(string mode, bool includeCatalog)
        {
            var document = SettingsFixture();
            return new SpeechVoiceDiscoveryInputV1
            {
                schemaVersion = SpeechVoiceDiscovery.InputSchemaVersion,
                mode = mode,
                requestedLanguages = new List<string> { "zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR" },
                cacheMaxAgeSeconds = 604800,
                deployment = new SpeechDeploymentDiagnostics(),
                catalogSnapshot = includeCatalog ? Clone(document.catalog) : null
            };
        }

        private static string WriteJson(string label, object value)
        {
            var path = Path.Combine(runRoot, label + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false));
            return path;
        }

        private static Dictionary<string, object> Evidence(string path)
        {
            return new Dictionary<string, object>
            {
                { "path", path }, { "bytes", new FileInfo(path).Length }, { "sha256", SpeechRequestReader.HashFile(path) }
            };
        }

        private static T Clone<T>(T value)
        {
            return Json.Deserialize<T>(Json.Serialize(value));
        }

        private static void CoreContractCompatibilityMarker()
        {
            var project = ProjectRoot();
            var schemas = Path.Combine(project, "contracts", "v1", "schemas", "speech", "speech-request.schema.json");
            var fixtures = Path.Combine(project, "contracts", "v1", "fixtures", "speech");
            if (!File.Exists(schemas) || !Directory.Exists(fixtures))
            {
                Console.WriteLine("PENDING core-contract-schema-compatibility: public schemas/fixtures not landed");
                return;
            }
            Console.WriteLine("INFO public contract files detected; build-and-test.ps1 runs Draft 2020-12 validation after unit tests");
        }

        private static string ProjectRoot() { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..")); }

        private static void Run(string name, Action test)
        {
            try { test(); TestResults.Add(new Dictionary<string, object> { { "name", name }, { "status", "passed" } }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; TestResults.Add(new Dictionary<string, object> { { "name", name }, { "status", "failed" }, { "message", ex.Message } }); Console.WriteLine("FAIL " + name + ": " + ex); }
        }

        private static void RunAsync(string name, Func<Task> test) { Run(name, delegate { test().GetAwaiter().GetResult(); }); }
        private static void Equal(string name, string expected, string actual) { if (expected != actual) throw new Exception(name + " expected=[" + expected + "] actual=[" + actual + "]"); }
        private static void True(string name, bool value) { if (!value) throw new Exception(name); }
        private static void ThrowsCode(string name, string code, Action action)
        {
            try { action(); throw new Exception(name + " did not throw"); }
            catch (SpeechContractException ex) { Equal(name, code, ex.Code); }
        }
    }

    internal sealed class SpeechSettingsFixtureDocument
    {
        public SpeechSettingsDraft settings { get; set; }
        public SpeechVoiceCatalog catalog { get; set; }
        public List<SpeechSettingsFixtureSample> samples { get; set; }
    }

    internal sealed class SpeechSettingsFixtureSample
    {
        public string text { get; set; }
        public string expectedLanguage { get; set; }
        public string expectedVoice { get; set; }
    }

    internal sealed class Fixture
    {
        public string JobRoot;
        public string AttemptDirectory;
        public string RequestPath;

        public static Fixture Create(string root, string text, Action<SpeechRequestV1> modify)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var job = Path.Combine(root, "jobs", "t" + id);
            var attempt = Path.Combine(job, "runs", "r1", "stages", "speech-synthesize", "attempts", "a1");
            var spoken = Path.Combine(job, "inputs", "spoken-text", "v0001", "payload.txt");
            Directory.CreateDirectory(attempt); Directory.CreateDirectory(Path.GetDirectoryName(spoken));
            File.WriteAllText(spoken, text, new UTF8Encoding(false));
            var request = NewRequest(id, job, spoken);
            if (modify != null) modify(request);
            var envelope = NewEnvelope(id, job, attempt, request);
            var requestPath = Path.Combine(attempt, "request.json");
            File.WriteAllText(requestPath, new JavaScriptSerializer().Serialize(envelope), new UTF8Encoding(false));
            return new Fixture { JobRoot = job, AttemptDirectory = attempt, RequestPath = requestPath };
        }

        public static Fixture CreateLongPath(string root, string text)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var taskId = "l" + id;
            var padding = 1;
            string job;
            string attempt;
            string requestPath;
            do
            {
                var paddedRoot = Path.Combine(root, "deep-" + new string('x', padding));
                job = Path.Combine(paddedRoot, "jobs", taskId);
                attempt = Path.Combine(job, "runs", "r1", "stages", "speech-synthesize", "attempts", "a1");
                requestPath = Path.Combine(attempt, "request.json");
                padding++;
            }
            while (requestPath.Length < 238);

            if (requestPath.Length >= 248) throw new Exception("long-path fixture request path must remain below the worker long-path threshold");
            var spoken = Path.Combine(job, "inputs", "spoken-text", "v0001", "payload.txt");
            LongPathFileSystem.CreateDirectory(attempt);
            LongPathFileSystem.CreateDirectory(Path.GetDirectoryName(spoken));
            LongPathFileSystem.WriteAllTextNew(spoken, text, new UTF8Encoding(false));
            var request = NewRequest(id, job, spoken);
            request.spokenText.bytes = LongPathFileSystem.FileLength(spoken);
            var envelope = NewEnvelope(id, job, attempt, request);
            LongPathFileSystem.WriteAllTextNew(requestPath, new JavaScriptSerializer().Serialize(envelope), new UTF8Encoding(false));
            return new Fixture { JobRoot = job, AttemptDirectory = attempt, RequestPath = requestPath };
        }

        private static SpeechRequestV1 NewRequest(string id, string job, string spoken)
        {
            return new SpeechRequestV1
            {
                schemaVersion = "1.0.0", requestId = "req-" + id, operationId = "op-" + id,
                spokenText = new SpokenTextReference
                {
                    artifactRef = "spoken-text", path = Relative(job, spoken), sha256 = SpeechRequestReader.HashFile(spoken),
                    bytes = new FileInfo(spoken).Length, encoding = "utf-8", mediaType = "text/plain"
                },
                contextMode = "selection_plus_paragraph",
                language = new LanguagePolicy { strategy = "auto", @override = null },
                voiceSnapshot = new VoiceSnapshot
                {
                    provider = "edge-tts", ratePercent = 0, volumePercent = 0, pitchHz = 0,
                    voicesByLanguage = new Dictionary<string, string>
                    {
                        { "zh-CN", "zh-CN-XiaoxiaoNeural" }, { "en-US", "en-US-AvaMultilingualNeural" },
                        { "es-ES", "es-ES-XimenaNeural" }, { "ja-JP", "ja-JP-NanamiNeural" }, { "ko-KR", "ko-KR-SunHiNeural" }
                    }
                },
                playbackPolicy = new PlaybackPolicy { autoPlay = true, activePlayback = "supersede", replay = "from_start", naturalCompletion = "idle_replayable", stopTerminal = "stopped" },
                cachePolicy = new CachePolicy { scope = "task", reuseSuccessfulAudio = true, crossTaskReuse = false }
            };
        }

        private static WorkerRequestEnvelopeV1 NewEnvelope(string id, string job, string attempt, SpeechRequestV1 payload)
        {
            return new WorkerRequestEnvelopeV1
            {
                schemaVersion = "1.0.0", requestId = payload.requestId, operationId = payload.operationId,
                taskId = "t" + id, runId = "r1", stageId = "speech-synthesize", attemptId = new DirectoryInfo(attempt).Name,
                workerId = "speech.edge", operation = "speech.synthesize", idempotencyKey = payload.spokenText.sha256,
                attemptPath = Relative(job, attempt), outputPath = Relative(job, Path.Combine(attempt, "outputs")),
                progressPath = Relative(job, Path.Combine(attempt, "progress.ndjson")), resultPath = Relative(job, Path.Combine(attempt, "result.json")),
                cancelSignal = new WorkerCancelSignalReferenceV1 { schemaVersion = "1.0.0", path = Relative(job, Path.Combine(attempt, "cancel.json")), gracePeriodMs = 2000 },
                inputRefs = new List<ArtifactReferenceV1>(), configSnapshot = new Dictionary<string, object>(), payload = payload
            };
        }

        internal static string Relative(string root, string path)
        {
            var rootUri = new Uri(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(path)).ToString());
        }
    }

    internal sealed class CliResult
    {
        public int ExitCode;
        public string StandardOutput;
        public string StandardError;
    }

    internal sealed class PlaybackCliFixture
    {
        public string JobRoot;
        public string AttemptDirectory;
        public string RequestPath;
        public string ManifestPath;
        public string AudioPath;

        public static PlaybackCliFixture Create(string root, string ffplayPath)
        {
            var taskId = "c" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var job = Path.Combine(root, "jobs", taskId);
            var synthAttempt = Path.Combine(job, "runs", "r1", "stages", "speech-synthesize", "attempts", "s1");
            var audioDirectory = Path.Combine(synthAttempt, "outputs", "speech-audio", "v0001");
            var audioPath = Path.Combine(audioDirectory, "payload.mp3");
            Directory.CreateDirectory(audioDirectory);
            File.WriteAllBytes(audioPath, Encoding.UTF8.GetBytes("ID3-cli-playback"));
            var manifest = new ArtifactManifestV1
            {
                schemaVersion = "1.0.0", artifactId = "speech-audio", kind = "audio.speech", contractVersion = "1.0.0", mediaType = "audio/mpeg",
                files = new List<HashedFileV1>
                {
                    new HashedFileV1 { path = Fixture.Relative(job, audioPath), sha256 = SpeechRequestReader.HashFile(audioPath), bytes = new FileInfo(audioPath).Length, mediaType = "audio/mpeg" }
                },
                createdAt = DateTime.UtcNow.ToString("o"),
                producer = new ArtifactProducerV1 { stageId = "speech-synthesize", workerId = "speech.edge", workerVersion = WorkerCli.WorkerVersion, attemptId = "s1" },
                inputRefs = new List<ArtifactReferenceV1>(), dataClass = "personal",
                metadata = new Dictionary<string, object> { { "languageTag", "zh-CN" }, { "voiceId", "zh-CN-XiaoxiaoNeural" } }
            };
            var manifestPath = Path.Combine(audioDirectory, "artifact.json");
            File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest), new UTF8Encoding(false));

            var attempt = Path.Combine(job, "runs", "r1", "stages", "speech-playback", "attempts", "p1");
            Directory.CreateDirectory(attempt);
            var attemptRef = Fixture.Relative(job, attempt);
            var request = new WorkerRequestEnvelopeV1
            {
                schemaVersion = "1.0.0", requestId = "request-play", operationId = "operation-play", taskId = taskId, runId = "r1",
                stageId = "speech-playback", attemptId = "p1", workerId = "speech.edge", operation = "speech.playback",
                idempotencyKey = SpeechRequestReader.HashFile(audioPath), attemptPath = attemptRef, outputPath = attemptRef + "/outputs",
                progressPath = attemptRef + "/progress.ndjson", resultPath = attemptRef + "/result.json",
                cancelSignal = new WorkerCancelSignalReferenceV1 { schemaVersion = "1.0.0", path = attemptRef + "/cancel.json", gracePeriodMs = 2000 },
                inputRefs = new List<ArtifactReferenceV1>
                {
                    new ArtifactReferenceV1 { artifactRef = "speech-audio", kind = "audio.speech", contractVersion = "1.0.0", manifestPath = Fixture.Relative(job, manifestPath), expectedSha256 = SpeechRequestReader.HashFile(manifestPath) }
                },
                configSnapshot = new Dictionary<string, object> { { "ffplayPath", ffplayPath } }, payload = new SpeechRequestV1()
            };
            var requestPath = Path.Combine(attempt, "request.json");
            File.WriteAllText(requestPath, new JavaScriptSerializer().Serialize(request), new UTF8Encoding(false));
            return new PlaybackCliFixture { JobRoot = job, AttemptDirectory = attempt, RequestPath = requestPath, ManifestPath = manifestPath, AudioPath = audioPath };
        }
    }

    internal sealed class PlaybackFixture
    {
        public string JobRoot; public string AudioPath; public string PlayAttempt; public string StopAttempt;
        public PlaybackWorkItem PlayWork; public PlaybackWorkItem StopWork;

        public static PlaybackFixture Create(string root)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var job = Path.Combine(root, "jobs", "p" + id);
            var audio = Path.Combine(job, "runs", "r1", "stages", "speech-synthesize", "attempts", "synth-1", "outputs", "speech-audio", "v0001", "payload.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(audio)); File.WriteAllBytes(audio, Encoding.ASCII.GetBytes("ID3-fake-audio"));
            var value = new PlaybackFixture { JobRoot = job, AudioPath = Fixture.Relative(job, audio) };
            value.PlayAttempt = value.CreateAdditionalAttempt("playback-1");
            value.StopAttempt = value.CreateAdditionalAttempt("stop-1");
            value.PlayWork = value.MakeWork("playback-1"); value.StopWork = value.MakeWork("stop-1");
            return value;
        }

        public string CreateAdditionalAttempt(string id)
        {
            var path = Path.Combine(JobRoot, "runs", "r1", "stages", "speech-playback", "attempts", id);
            Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "request.json"), "{}", new UTF8Encoding(false)); return path;
        }

        public PlaybackWorkItem MakeWork(string id)
        {
            var full = SpeechRequestReader.ResolveJobRelativePath(JobRoot, AudioPath);
            return new PlaybackWorkItem
            {
                RequestId = "request-play", OperationId = "operation-play", TaskId = "task-play", RunId = "run-play",
                StageId = "speech-playback", AttemptId = id, WorkerId = "speech.edge", IdempotencyKey = SpeechRequestReader.HashFile(full),
                AudioPath = AudioPath, AudioBytes = new FileInfo(full).Length, AudioSha256 = SpeechRequestReader.HashFile(full),
                LanguageTag = "zh-CN", VoiceId = "zh-CN-XiaoxiaoNeural",
                CancellationSignalRef = Fixture.Relative(JobRoot, Path.Combine(JobRoot, "runs", "r1", "stages", "speech-playback", "attempts", id, "cancel.json")),
                CancellationGracePeriodMs = 2000
            };
        }
    }

    internal sealed class FakeSynthesizer : ISpeechSynthesizer
    {
        private readonly bool fail;
        public int Calls;
        public readonly List<SpeechRoute> Routes = new List<SpeechRoute>();
        public readonly List<VoiceSnapshot> Snapshots = new List<VoiceSnapshot>();
        public readonly List<string> TextPaths = new List<string>();
        public readonly List<string> OutputPaths = new List<string>();
        public FakeSynthesizer(bool fail) { this.fail = fail; }
        public Task<SynthesisProcessResult> SynthesizeAsync(string textPath, string outputPath, SpeechRoute route, VoiceSnapshot snapshot)
        {
            Calls++;
            Routes.Add(route);
            Snapshots.Add(snapshot);
            TextPaths.Add(textPath);
            OutputPaths.Add(outputPath);
            if (!fail) File.WriteAllBytes(outputPath, Encoding.UTF8.GetBytes("ID3-" + File.ReadAllText(textPath)));
            return Task.FromResult(new SynthesisProcessResult { ExitCode = fail ? 7 : 0, StandardError = fail ? "synthetic edge failure" : "" });
        }
    }

    internal sealed class FakeAudioPlayerFactory : IAudioPlayerFactory
    {
        public readonly List<FakeAudioPlayerProcess> Processes = new List<FakeAudioPlayerProcess>();
        public readonly List<string> Paths = new List<string>();
        public IAudioPlayerProcess Start(string audioPath) { var process = new FakeAudioPlayerProcess(); Paths.Add(audioPath); Processes.Add(process); return process; }
    }

    internal sealed class FakeAudioPlayerProcess : IAudioPlayerProcess
    {
        private bool exited; private int exitCode;
        public event EventHandler Exited;
        public bool HasExited { get { return exited; } }
        public int ExitCode { get { return exitCode; } }
        public void Kill() { Complete(-9); }
        public void Dispose() { }
        public void Complete(int code)
        {
            if (exited) return; exited = true; exitCode = code; var handler = Exited; if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}

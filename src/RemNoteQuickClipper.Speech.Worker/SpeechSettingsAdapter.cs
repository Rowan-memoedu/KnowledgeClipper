using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class SpeechSettingsDraft
    {
        public string languageStrategy { get; set; }
        public string languageOverride { get; set; }
        public Dictionary<string, string> voicesByLanguage { get; set; }
        public int ratePercent { get; set; }
        public int volumePercent { get; set; }
        public int pitchHz { get; set; }
    }

    internal sealed class SpeechVoiceCatalog
    {
        public string provider { get; set; }
        public string catalogVersion { get; set; }
        public string updatedAtUtc { get; set; }
        public List<SpeechVoiceOption> voices { get; set; }
    }

    internal sealed class SpeechVoiceOption
    {
        public string languageTag { get; set; }
        public string voiceId { get; set; }
        public string displayName { get; set; }
        public string gender { get; set; }
        public bool natural { get; set; }
        public bool available { get; set; }
        public string status { get; set; }
    }

    internal sealed class SpeechDeploymentDiagnostics
    {
        public string edgeTtsPath { get; set; }
        public string ffplayPath { get; set; }
    }

    internal sealed class SpeechDeploymentView
    {
        public string workerId { get; set; }
        public string workerVersion { get; set; }
        public string edgeTtsPath { get; set; }
        public string edgeTtsStatus { get; set; }
        public string ffplayPath { get; set; }
        public string ffplayStatus { get; set; }
        public List<string> supportedLanguages { get; set; }
        public List<SpeechVoiceOption> voices { get; set; }
    }

    internal sealed class SpeechFixedSettingsView
    {
        public string provider { get; set; }
        public PlaybackPolicy playbackPolicy { get; set; }
        public CachePolicy cachePolicy { get; set; }
        public string legacyAiTts { get; set; }
    }

    internal sealed class SpeechSettingsAdapterResult
    {
        public SpeechRequestV1 speechRequest { get; set; }
        public Dictionary<string, object> synthesisConfigSnapshot { get; set; }
        public Dictionary<string, object> playbackConfigSnapshot { get; set; }
        public SpeechFixedSettingsView fixedSettings { get; set; }
        public SpeechDeploymentView deployment { get; set; }
    }

    internal sealed class SpeechSettingsAdapterInputV1
    {
        public string schemaVersion { get; set; }
        public SpeechSettingsDraft settings { get; set; }
        public SpeechVoiceCatalog catalog { get; set; }
        public SpeechDeploymentDiagnostics deployment { get; set; }
        public WorkerRequestEnvelopeV1 synthesisRequest { get; set; }
        public WorkerRequestEnvelopeV1 playbackRequest { get; set; }
    }

    internal sealed class SpeechSettingsAdapterOutputV1
    {
        public string schemaVersion { get; set; }
        public string workerId { get; set; }
        public string workerVersion { get; set; }
        public WorkerRequestEnvelopeV1 synthesisRequest { get; set; }
        public WorkerRequestEnvelopeV1 playbackRequest { get; set; }
        public SpeechFixedSettingsView fixedSettings { get; set; }
        public SpeechDeploymentView deployment { get; set; }
    }

    /// <summary>
    /// Pure module adapter used by Core/Desktop to turn a validated settings draft into
    /// immutable speech request/config snapshots. It performs no persistence, discovery,
    /// synthesis, playback, or external calls.
    /// </summary>
    internal sealed class SpeechSettingsAdapter
    {
        internal const string Provider = "edge-tts";
        private readonly SpeechVoiceCatalog catalog;
        private readonly SpeechDeploymentDiagnostics diagnostics;

        public SpeechSettingsAdapter(SpeechVoiceCatalog catalog, SpeechDeploymentDiagnostics diagnostics)
        {
            this.catalog = catalog;
            this.diagnostics = diagnostics ?? new SpeechDeploymentDiagnostics();
            ValidateCatalog(catalog);
            ValidateDiagnosticPath(this.diagnostics.edgeTtsPath, "edgeTtsPath");
            ValidateDiagnosticPath(this.diagnostics.ffplayPath, "ffplayPath");
        }

        public SpeechSettingsAdapterResult CreateSnapshot(SpeechRequestV1 template, SpeechSettingsDraft draft)
        {
            if (template == null) throw new SpeechContractException("missing_speech_template", "缺少 SpeechRequest 模板");
            ValidateDraft(draft);

            var voices = NormalizeVoiceSelections(draft.voicesByLanguage);
            var languageOverride = draft.languageStrategy == "override" ? NormalizeTag(draft.languageOverride) : null;
            if (draft.languageStrategy == "override" && FindSelectedVoice(voices, languageOverride) == null)
                throw new SpeechContractException("unsupported_language_override", "override 语言没有可用声音: " + languageOverride);

            var request = new SpeechRequestV1
            {
                schemaVersion = template.schemaVersion,
                requestId = template.requestId,
                operationId = template.operationId,
                spokenText = Clone(template.spokenText),
                contextMode = template.contextMode,
                language = new LanguagePolicy { strategy = draft.languageStrategy, @override = languageOverride },
                voiceSnapshot = new VoiceSnapshot
                {
                    provider = Provider,
                    voicesByLanguage = voices,
                    ratePercent = draft.ratePercent,
                    volumePercent = draft.volumePercent,
                    pitchHz = draft.pitchHz
                },
                playbackPolicy = FrozenPlaybackPolicy(),
                cachePolicy = FrozenCachePolicy()
            };
            SpeechRequestReader.ValidateRequestShape(request);

            return new SpeechSettingsAdapterResult
            {
                speechRequest = request,
                synthesisConfigSnapshot = ToolSnapshot("edgeTtsPath", diagnostics.edgeTtsPath),
                playbackConfigSnapshot = ToolSnapshot("ffplayPath", diagnostics.ffplayPath),
                fixedSettings = new SpeechFixedSettingsView
                {
                    provider = Provider,
                    playbackPolicy = FrozenPlaybackPolicy(),
                    cachePolicy = FrozenCachePolicy(),
                    legacyAiTts = "disabled"
                },
                deployment = DeploymentView()
            };
        }

        public SpeechSettingsAdapterResult ApplyToSynthesisEnvelope(WorkerRequestEnvelopeV1 envelope, SpeechSettingsDraft draft)
        {
            if (envelope == null) throw new SpeechContractException("missing_worker_request", "缺少 WorkerRequest");
            if (envelope.operation != "speech.synthesize") throw new SpeechContractException("wrong_settings_operation", "设置 adapter 只能写入 speech.synthesize 请求");
            var result = CreateSnapshot(envelope.payload, draft);
            envelope.payload = result.speechRequest;
            envelope.configSnapshot = CopySnapshot(envelope.configSnapshot, result.synthesisConfigSnapshot);
            return result;
        }

        public Dictionary<string, object> ApplyToPlaybackEnvelope(WorkerRequestEnvelopeV1 envelope)
        {
            if (envelope == null) throw new SpeechContractException("missing_worker_request", "缺少 WorkerRequest");
            if (envelope.operation != "speech.playback") throw new SpeechContractException("wrong_settings_operation", "播放部署设置只能写入 speech.playback 请求");
            var snapshot = ToolSnapshot("ffplayPath", diagnostics.ffplayPath);
            envelope.configSnapshot = CopySnapshot(envelope.configSnapshot, snapshot);
            return snapshot;
        }

        private void ValidateDraft(SpeechSettingsDraft draft)
        {
            if (draft == null) throw new SpeechContractException("missing_speech_settings", "缺少语音设置");
            if (draft.languageStrategy != "auto" && draft.languageStrategy != "override")
                throw new SpeechContractException("invalid_language_strategy", "languageStrategy 只能是 auto 或 override");
            if (draft.languageStrategy == "auto" && !string.IsNullOrWhiteSpace(draft.languageOverride))
                throw new SpeechContractException("unexpected_language_override", "auto 模式不能携带 languageOverride");
            if (draft.languageStrategy == "override" && !VoiceRouter.IsBcp47(draft.languageOverride))
                throw new SpeechContractException("invalid_language_override", "override 模式必须提供 BCP-47 languageOverride");
            if (draft.voicesByLanguage == null || draft.voicesByLanguage.Count == 0)
                throw new SpeechContractException("missing_voice_map", "每个支持语言都必须选择声音");
            if (draft.ratePercent < -100 || draft.ratePercent > 100) throw new SpeechContractException("invalid_rate", "ratePercent 超出 -100..100");
            if (draft.volumePercent < -100 || draft.volumePercent > 100) throw new SpeechContractException("invalid_volume", "volumePercent 超出 -100..100");
            if (draft.pitchHz < -1000 || draft.pitchHz > 1000) throw new SpeechContractException("invalid_pitch", "pitchHz 超出 -1000..1000");
        }

        private Dictionary<string, string> NormalizeVoiceSelections(Dictionary<string, string> selections)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in selections)
            {
                if (!VoiceRouter.IsBcp47(item.Key)) throw new SpeechContractException("invalid_voice_language", "声音设置包含无效语言标签");
                var language = CatalogLanguage(item.Key);
                if (language == null) throw new SpeechContractException("unsupported_voice_language", "不支持的声音语言: " + item.Key);
                var option = catalog.voices.FirstOrDefault(v =>
                    string.Equals(v.voiceId, item.Value, StringComparison.Ordinal) &&
                    string.Equals(NormalizeTag(v.languageTag), language, StringComparison.OrdinalIgnoreCase));
                if (option == null) throw new SpeechContractException("unknown_voice", "声音不在当前目录或与语言不匹配: " + item.Value);
                if (!option.available) throw new SpeechContractException("voice_unavailable", "声音当前不可用: " + item.Value);
                if (normalized.ContainsKey(language)) throw new SpeechContractException("duplicate_voice_language", "同一语言重复配置声音: " + language);
                normalized.Add(language, option.voiceId);
            }

            foreach (var language in SupportedLanguages())
                if (!normalized.ContainsKey(language)) throw new SpeechContractException("missing_language_voice", "缺少支持语言的声音: " + language);
            return normalized;
        }

        private SpeechDeploymentView DeploymentView()
        {
            return new SpeechDeploymentView
            {
                workerId = WorkerCli.WorkerId,
                workerVersion = WorkerCli.WorkerVersion,
                edgeTtsPath = diagnostics.edgeTtsPath,
                edgeTtsStatus = PathStatus(diagnostics.edgeTtsPath),
                ffplayPath = diagnostics.ffplayPath,
                ffplayStatus = PathStatus(diagnostics.ffplayPath),
                supportedLanguages = SupportedLanguages(),
                voices = catalog.voices.Select(Clone).ToList()
            };
        }

        private List<string> SupportedLanguages()
        {
            return catalog.voices.Select(v => NormalizeTag(v.languageTag)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.Ordinal).ToList();
        }

        private string CatalogLanguage(string language)
        {
            var normalized = NormalizeTag(language);
            return SupportedLanguages().FirstOrDefault(v => string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static string FindSelectedVoice(Dictionary<string, string> voices, string language)
        {
            string voice;
            if (voices.TryGetValue(language, out voice)) return voice;
            var baseLanguage = language.Split('-')[0];
            foreach (var item in voices)
                if (string.Equals(item.Key.Split('-')[0], baseLanguage, StringComparison.OrdinalIgnoreCase)) return item.Value;
            return null;
        }

        private static Dictionary<string, object> ToolSnapshot(string key, string path)
        {
            var snapshot = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(path)) snapshot.Add(key, Path.GetFullPath(path));
            return snapshot;
        }

        private static Dictionary<string, object> CopySnapshot(Dictionary<string, object> existing, Dictionary<string, object> additions)
        {
            var copy = existing == null ? new Dictionary<string, object>(StringComparer.Ordinal) : new Dictionary<string, object>(existing, StringComparer.Ordinal);
            foreach (var item in additions) copy[item.Key] = item.Value;
            return copy;
        }

        private static PlaybackPolicy FrozenPlaybackPolicy()
        {
            return new PlaybackPolicy { autoPlay = true, activePlayback = "supersede", replay = "from_start", naturalCompletion = "idle_replayable", stopTerminal = "stopped" };
        }

        private static CachePolicy FrozenCachePolicy()
        {
            return new CachePolicy { scope = "task", reuseSuccessfulAudio = true, crossTaskReuse = false };
        }

        private static void ValidateCatalog(SpeechVoiceCatalog value)
        {
            if (value == null || !string.Equals(value.provider, Provider, StringComparison.Ordinal))
                throw new SpeechContractException("unsupported_voice_provider", "设置目录只允许 edge-tts");
            if (value.voices == null || value.voices.Count == 0) throw new SpeechContractException("empty_voice_catalog", "声音目录不能为空");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var voice in value.voices)
            {
                if (voice == null || !VoiceRouter.IsBcp47(voice.languageTag) || string.IsNullOrWhiteSpace(voice.voiceId) || !Regex.IsMatch(voice.voiceId, "^[A-Za-z0-9-]+$"))
                    throw new SpeechContractException("invalid_voice_catalog", "声音目录条目无效");
                if (!ids.Add(voice.voiceId)) throw new SpeechContractException("duplicate_voice_catalog_entry", "声音目录包含重复 voiceId: " + voice.voiceId);
            }
        }

        private static void ValidateDiagnosticPath(string path, string name)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path))
                throw new SpeechContractException("runtime_path_not_absolute", name + " 必须是绝对路径");
        }

        private static string PathStatus(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "not_configured";
            return File.Exists(path) ? "available" : "missing";
        }

        private static string NormalizeTag(string value)
        {
            var parts = value.Split('-');
            if (parts.Length == 1) return parts[0].ToLowerInvariant();
            return parts[0].ToLowerInvariant() + "-" + string.Join("-", parts.Skip(1).Select(p => p.Length == 2 ? p.ToUpperInvariant() : p));
        }

        private static SpokenTextReference Clone(SpokenTextReference value)
        {
            if (value == null) return null;
            return new SpokenTextReference
            {
                artifactRef = value.artifactRef,
                path = value.path,
                sha256 = value.sha256,
                bytes = value.bytes,
                encoding = value.encoding,
                mediaType = value.mediaType
            };
        }

        private static SpeechVoiceOption Clone(SpeechVoiceOption value)
        {
            return new SpeechVoiceOption
            {
                languageTag = value.languageTag,
                voiceId = value.voiceId,
                displayName = value.displayName,
                gender = value.gender,
                natural = value.natural,
                available = value.available,
                status = value.status
            };
        }
    }

    internal static class SpeechSettingsAdapterCli
    {
        internal const string InputSchemaVersion = "speech-settings-adapter-input.v1";
        internal const string OutputSchemaVersion = "speech-settings-adapter-snapshot.v1";
        internal const string DescriptorSchemaVersion = "speech-settings-adapter-descriptor.v1";

        public static object Describe()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", DescriptorSchemaVersion },
                { "workerId", WorkerCli.WorkerId },
                { "workerVersion", WorkerCli.WorkerVersion },
                { "inputSchemaVersion", InputSchemaVersion },
                { "outputSchemaVersion", OutputSchemaVersion },
                { "groups", new object[]
                    {
                        Group("common", new object[]
                        {
                            Field("languageStrategy", "enum:auto|override", "user", false),
                            Field("languageOverride", "nullable:bcp47", "user", false),
                            Field("voicesByLanguage", "catalog-voice-map", "user", false),
                            Field("ratePercent", "integer:-100..100", "user", false),
                            Field("volumePercent", "integer:-100..100", "user", false),
                            Field("pitchHz", "integer:-1000..1000", "user", false)
                        }),
                        Group("fixed", new object[]
                        {
                            Field("provider", "const:edge-tts", "policy", true),
                            Field("playbackPolicy", "const:speech-v1", "policy", true),
                            Field("cachePolicy", "const:speech-v1", "policy", true),
                            Field("legacyAiTts", "const:disabled", "policy", true)
                        }),
                        Group("diagnostics", new object[]
                        {
                            Field("workerId", "const:speech.edge", "diagnostic", true),
                            Field("workerVersion", "semantic-version", "diagnostic", true),
                            Field("edgeTtsPath", "absolute-path", "deployment", true),
                            Field("ffplayPath", "absolute-path", "deployment", true),
                            Field("supportedLanguages", "bcp47-list", "diagnostic", true),
                            Field("voices", "voice-status-list", "diagnostic", true)
                        })
                    }
                }
            };
        }

        public static SpeechSettingsAdapterOutputV1 AdaptFile(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !Path.IsPathRooted(inputPath))
                throw new SpeechContractException("settings_path_not_absolute", "settings adapt 输入必须是绝对 JSON 文件路径");
            var fullPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullPath)) throw new SpeechContractException("missing_settings_input", "settings adapt 输入不存在");
            SpeechSettingsAdapterInputV1 input;
            try
            {
                var json = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
                input = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }.Deserialize<SpeechSettingsAdapterInputV1>(json);
            }
            catch (Exception ex) { throw new SpeechContractException("invalid_settings_input", "settings adapt 输入不是严格 UTF-8 JSON", ex); }
            if (input == null || input.schemaVersion != InputSchemaVersion)
                throw new SpeechContractException("unsupported_settings_input", "不支持的 settings adapter schemaVersion");
            if (input.synthesisRequest == null || input.playbackRequest == null)
                throw new SpeechContractException("missing_settings_request_template", "必须提供 synthesisRequest 与 playbackRequest 模板");
            RequireIdentity(input.synthesisRequest, "speech.synthesize");
            RequireIdentity(input.playbackRequest, "speech.playback");

            var adapter = new SpeechSettingsAdapter(input.catalog, input.deployment);
            var adapted = adapter.ApplyToSynthesisEnvelope(input.synthesisRequest, input.settings);
            adapter.ApplyToPlaybackEnvelope(input.playbackRequest);
            return new SpeechSettingsAdapterOutputV1
            {
                schemaVersion = OutputSchemaVersion,
                workerId = WorkerCli.WorkerId,
                workerVersion = WorkerCli.WorkerVersion,
                synthesisRequest = input.synthesisRequest,
                playbackRequest = input.playbackRequest,
                fixedSettings = adapted.fixedSettings,
                deployment = adapted.deployment
            };
        }

        private static void RequireIdentity(WorkerRequestEnvelopeV1 envelope, string operation)
        {
            if (envelope.workerId != WorkerCli.WorkerId || envelope.operation != operation)
                throw new SpeechContractException("wrong_settings_operation", "settings request 模板 workerId/operation 不匹配");
            if (operation == "speech.synthesize" && (envelope.payload == null || envelope.requestId != envelope.payload.requestId || envelope.operationId != envelope.payload.operationId))
                throw new SpeechContractException("request_identity_mismatch", "synthesisRequest 外壳与 SpeechRequest 身份不一致");
        }

        private static Dictionary<string, object> Group(string id, object[] fields)
        {
            return new Dictionary<string, object> { { "id", id }, { "fields", fields } };
        }

        private static Dictionary<string, object> Field(string path, string type, string ownership, bool readOnly)
        {
            return new Dictionary<string, object>
            {
                { "path", path }, { "type", type }, { "ownership", ownership }, { "readOnly", readOnly }
            };
        }
    }
}

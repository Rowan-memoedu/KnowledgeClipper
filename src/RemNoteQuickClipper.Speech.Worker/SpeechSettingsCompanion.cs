using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class SpeechSettingsCompanionProbeInputV1
    {
        public string schemaVersion { get; set; }
        public string scope { get; set; }
        public string effectiveAt { get; set; }
        public string action { get; set; }
        public string probeText { get; set; }
        public SpeechSettingsAdapterInputV1 adapterInput { get; set; }
    }

    internal static class SpeechSettingsCompanion
    {
        internal const string CompanionVersion = "1.1.0";
        internal const string DescriptorSchemaVersion = "speech-settings-companion-descriptor.v1";
        internal const string ProbeInputSchemaVersion = "speech-settings-companion-probe-input.v1";
        internal const string ProbeOutputSchemaVersion = "speech-settings-companion-probe-output.v1";

        public static object Describe()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", DescriptorSchemaVersion },
                { "companionVersion", CompanionVersion },
                { "workerId", WorkerCli.WorkerId },
                { "workerVersion", WorkerCli.WorkerVersion },
                { "scope", new Dictionary<string, object>
                    {
                        { "value", "project" },
                        { "persistenceOwner", "desktop-core" },
                        { "effectiveAt", "next_task" },
                        { "currentTaskEffect", "none_snapshot_frozen" }
                    }
                },
                { "commands", new object[]
                    {
                        Command("settings companion describe --json", false, "静态能力与绑定描述"),
                        Command("settings companion status --input <absolute-json> --json", false, "离线 readiness 与声音目录状态"),
                        Command("settings companion probe --input <absolute-json> --json", false, "离线设置到请求快照与路由验证"),
                        Command("settings companion voices --input <absolute-json> --json", false, "只读声音目录发现、缓存快照验证与 revision/hash 绑定"),
                        Command("settings companion preview --input <absolute-json> --json", true, "显式短样例合成与自然播放预览")
                    }
                },
                { "settings", SettingsDescriptor() },
                { "voiceDiscovery", new Dictionary<string, object>
                    {
                        { "inputSchemaVersion", SpeechVoiceDiscovery.InputSchemaVersion },
                        { "outputSchemaVersion", SpeechVoiceDiscovery.OutputSchemaVersion },
                        { "modes", new object[] { "discover", "cache_only", "validate_only" } },
                        { "readOnly", true }, { "physicalSpeakerPlayback", false },
                        { "catalogIdentity", "catalogRevision+catalogSha256" },
                        { "cachePersistenceOwner", "desktop-core" },
                        { "runtimePathOwnership", "deployment_read_only" }
                    }
                },
                { "configSnapshotBindings", new object[]
                    {
                        Binding("deployment.edgeTtsPath", "synthesisRequest.configSnapshot.edgeTtsPath", "adapter", "implemented"),
                        Binding("deployment.ffplayPath", "playbackRequest.configSnapshot.ffplayPath", "adapter", "implemented")
                    }
                },
                { "actions", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "id", "speaker.preview" }, { "sideEffect", "physical_audio" },
                            { "invocation", "explicit_only" }, { "implementedByCompanion", true },
                            { "status", "implemented" }, { "probeBehavior", "never_run" },
                            { "inputSchemaVersion", SpeechSpeakerPreview.InputSchemaVersion },
                            { "outputSchemaVersion", SpeechSpeakerPreview.OutputSchemaVersion },
                            { "explicitAuthorizationRequired", true }, { "previewAttemptCount", 1 },
                            { "audioPlayerInvocationCount", 1 }, { "naturalCompletion", "idle" },
                            { "createsKnowledgeTasks", false }
                        }
                    }
                },
                { "conditionalIntegrations", new object[]
                    {
                        Conditional("desktop.projectSettingsPersistence", "Desktop/Core must persist the project draft and call the adapter for each new task."),
                        Conditional("desktop.languageOverrideWiring", "UI must set settings.languageStrategy=override and settings.languageOverride=<BCP-47> before calling adapt/probe."),
                        Conditional("desktop.voiceCatalogPersistence", "Desktop/Core persists the returned revision/hash-bound catalog; discovery never writes project settings."),
                        Conditional("desktop.speakerPreviewGesture", "Desktop must invoke preview only from an explicit user gesture; settings save/status/probe never route to preview.")
                    }
                },
                { "boundaries", new Dictionary<string, object>
                    {
                        { "provider", "edge-tts" }, { "legacyAiTts", "disabled_read_only" },
                        { "commonContracts", "frozen_read_only" }, { "authorityRegistry", "read_only" },
                        { "settingsSaveStatusProbePhysicalSpeaker", "never" },
                        { "voiceDiscovery", "read_only_provider_query_no_speaker" },
                        { "physicalSpeaker", "speaker.preview_explicit_only" },
                        { "knowledgeTaskCreation", "never" }
                    }
                }
            };
        }

        public static object ProbeFile(string inputPath, bool includeSnapshots)
        {
            var input = ReadInput(inputPath);
            ValidateWrapper(input);
            var adapterInput = input.adapterInput;
            if (adapterInput == null) throw new SpeechContractException("missing_settings_adapter_input", "缺少 adapterInput");
            if (adapterInput.synthesisRequest == null || adapterInput.playbackRequest == null)
                throw new SpeechContractException("missing_settings_request_template", "adapterInput 必须包含 synthesisRequest 与 playbackRequest");

            var adapter = new SpeechSettingsAdapter(adapterInput.catalog, adapterInput.deployment);
            var adapted = adapter.ApplyToSynthesisEnvelope(adapterInput.synthesisRequest, adapterInput.settings);
            adapter.ApplyToPlaybackEnvelope(adapterInput.playbackRequest);
            var route = VoiceRouter.Route(new ValidatedSpeechRequest
            {
                Envelope = adapterInput.synthesisRequest,
                Request = adapterInput.synthesisRequest.payload,
                SpokenText = input.probeText,
                SpokenTextPath = "offline-probe",
                SpokenTextSha256 = adapterInput.synthesisRequest.payload.spokenText.sha256
            });
            var deployment = adapted.deployment;
            var selectedVoicesReady = deployment.voices != null && deployment.voices
                .Where(v => adapted.speechRequest.voiceSnapshot.voicesByLanguage.Values.Contains(v.voiceId))
                .All(v => v.available);
            var synthesisReady = deployment.edgeTtsStatus == "available";
            var playbackConfigReady = deployment.ffplayStatus == "available";
            var status = synthesisReady && playbackConfigReady && selectedVoicesReady ? "ready" : "conditional";

            var output = new Dictionary<string, object>
            {
                { "schemaVersion", ProbeOutputSchemaVersion },
                { "companionVersion", CompanionVersion },
                { "workerId", WorkerCli.WorkerId },
                { "workerVersion", WorkerCli.WorkerVersion },
                { "status", status },
                { "mode", "offline_validate_only" },
                { "scope", "project" },
                { "effectiveAt", "next_task" },
                { "currentTaskEffect", "none_snapshot_frozen" },
                { "readiness", new Dictionary<string, object>
                    {
                        { "settingsAdapter", "ready" },
                        { "voiceCatalog", selectedVoicesReady ? "ready" : "conditional" },
                        { "synthesisRuntime", synthesisReady ? "ready" : "conditional" },
                        { "playbackConfiguration", playbackConfigReady ? "ready" : "conditional" },
                        { "speakerPreview", "not_run" }
                    }
                },
                { "voiceCatalog", new Dictionary<string, object>
                    {
                        { "provider", adapterInput.catalog.provider },
                        { "catalogVersion", adapterInput.catalog.catalogVersion },
                        { "supportedLanguages", deployment.supportedLanguages },
                        { "voices", deployment.voices }
                    }
                },
                { "routing", new Dictionary<string, object>
                    {
                        { "strategy", adapted.speechRequest.language.strategy },
                        { "override", adapted.speechRequest.language.@override },
                        { "languageTag", route.LanguageTag },
                        { "voiceId", route.Voice }
                    }
                },
                { "fixedSettings", adapted.fixedSettings },
                { "bindings", BindingStatus(adapted.speechRequest) },
                { "sideEffectsPerformed", new object[0] },
                { "speakerPreview", new Dictionary<string, object>
                    {
                        { "actionId", "speaker.preview" }, { "status", "not_run" },
                        { "sideEffect", "physical_audio" }, { "requiresExplicitInvocation", true }
                    }
                }
            };
            if (includeSnapshots)
            {
                output.Add("synthesisRequest", adapterInput.synthesisRequest);
                output.Add("playbackRequest", adapterInput.playbackRequest);
                output.Add("resultContractProjection", new Dictionary<string, object>
                {
                    { "status", "completed" },
                    { "payload", new Dictionary<string, object>
                        {
                            { "languageTag", route.LanguageTag }, { "voiceId", route.Voice },
                            { "cacheHit", false }, { "playbackState", "idle_replayable" }
                        }
                    }
                });
            }
            return output;
        }

        private static SpeechSettingsCompanionProbeInputV1 ReadInput(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !Path.IsPathRooted(inputPath))
                throw new SpeechContractException("settings_path_not_absolute", "companion 输入必须是绝对 JSON 文件路径");
            var fullPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullPath)) throw new SpeechContractException("missing_settings_input", "companion 输入不存在");
            try
            {
                var json = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }
                    .Deserialize<SpeechSettingsCompanionProbeInputV1>(json);
            }
            catch (Exception ex) { throw new SpeechContractException("invalid_settings_input", "companion 输入不是严格 UTF-8 JSON", ex); }
        }

        private static void ValidateWrapper(SpeechSettingsCompanionProbeInputV1 input)
        {
            if (input == null || input.schemaVersion != ProbeInputSchemaVersion)
                throw new SpeechContractException("unsupported_settings_companion_input", "不支持的 companion schemaVersion");
            if (input.scope != "project") throw new SpeechContractException("unsupported_settings_scope", "companion 只接受 project scope");
            if (input.effectiveAt != "next_task") throw new SpeechContractException("unsupported_settings_effective_at", "设置只能从 next_task 生效");
            if (input.action != "validate_only") throw new SpeechContractException("side_effect_action_rejected", "本 companion 只接受 validate_only，不执行 speaker preview");
            if (string.IsNullOrWhiteSpace(input.probeText)) throw new SpeechContractException("empty_probe_text", "probeText 不能为空");
        }

        private static object[] SettingsDescriptor()
        {
            return new object[]
            {
                Setting("languageStrategy", true, "enum:auto|override", "synthesisRequest.payload.language.strategy", "implemented"),
                Setting("languageOverride", true, "nullable:bcp47; required when strategy=override", "synthesisRequest.payload.language.override", "implemented"),
                Setting("voicesByLanguage", true, "five-language catalog voice map", "synthesisRequest.payload.voiceSnapshot.voicesByLanguage", "implemented"),
                Setting("ratePercent", true, "integer:-100..100", "synthesisRequest.payload.voiceSnapshot.ratePercent", "implemented"),
                Setting("volumePercent", true, "integer:-100..100", "synthesisRequest.payload.voiceSnapshot.volumePercent", "implemented"),
                Setting("pitchHz", true, "integer:-1000..1000", "synthesisRequest.payload.voiceSnapshot.pitchHz", "implemented"),
                Setting("provider", false, "const:edge-tts", "synthesisRequest.payload.voiceSnapshot.provider", "implemented_read_only"),
                Setting("autoPlay", false, "const:true", "synthesisRequest.payload.playbackPolicy.autoPlay", "implemented_read_only"),
                Setting("replay", false, "const:from_start", "synthesisRequest.payload.playbackPolicy.replay", "implemented_read_only"),
                Setting("activePlayback", false, "const:supersede", "synthesisRequest.payload.playbackPolicy.activePlayback", "implemented_read_only"),
                Setting("naturalCompletion", false, "const:idle_replayable", "synthesisRequest.payload.playbackPolicy.naturalCompletion", "implemented_read_only"),
                Setting("stopTerminal", false, "const:stopped", "synthesisRequest.payload.playbackPolicy.stopTerminal", "implemented_read_only"),
                Setting("cacheScope", false, "const:task", "synthesisRequest.payload.cachePolicy.scope", "implemented_read_only"),
                Setting("reuseSuccessfulAudio", false, "const:true", "synthesisRequest.payload.cachePolicy.reuseSuccessfulAudio", "implemented_read_only"),
                Setting("crossTaskReuse", false, "const:false", "synthesisRequest.payload.cachePolicy.crossTaskReuse", "implemented_read_only"),
                Setting("legacyAiTts", false, "const:disabled", null, "disabled_read_only")
            };
        }

        private static object[] BindingStatus(SpeechRequestV1 request)
        {
            return new object[]
            {
                Bound("settings.languageStrategy", "synthesisRequest.payload.language.strategy", request.language.strategy),
                Bound("settings.languageOverride", "synthesisRequest.payload.language.override", request.language.@override),
                Bound("settings.voicesByLanguage", "synthesisRequest.payload.voiceSnapshot.voicesByLanguage", request.voiceSnapshot.voicesByLanguage),
                Bound("settings.ratePercent", "synthesisRequest.payload.voiceSnapshot.ratePercent", request.voiceSnapshot.ratePercent),
                Bound("settings.volumePercent", "synthesisRequest.payload.voiceSnapshot.volumePercent", request.voiceSnapshot.volumePercent),
                Bound("settings.pitchHz", "synthesisRequest.payload.voiceSnapshot.pitchHz", request.voiceSnapshot.pitchHz),
                Bound("fixed.playbackPolicy", "synthesisRequest.payload.playbackPolicy", request.playbackPolicy),
                Bound("fixed.cachePolicy", "synthesisRequest.payload.cachePolicy", request.cachePolicy)
            };
        }

        private static Dictionary<string, object> Command(string syntax, bool sideEffect, string description)
        {
            return new Dictionary<string, object> { { "syntax", syntax }, { "sideEffect", sideEffect }, { "description", description } };
        }

        private static Dictionary<string, object> Setting(string id, bool editable, string validation, string target, string support)
        {
            return new Dictionary<string, object>
            {
                { "id", id }, { "editable", editable }, { "scope", "project" }, { "effectiveAt", "next_task" },
                { "validation", validation }, { "target", target }, { "support", support }
            };
        }

        private static Dictionary<string, object> Binding(string source, string target, string owner, string status)
        {
            return new Dictionary<string, object> { { "source", source }, { "target", target }, { "owner", owner }, { "status", status } };
        }

        private static Dictionary<string, object> Bound(string source, string target, object value)
        {
            return new Dictionary<string, object> { { "source", source }, { "target", target }, { "status", "bound" }, { "value", value } };
        }

        private static Dictionary<string, object> Conditional(string id, string reason)
        {
            return new Dictionary<string, object> { { "id", id }, { "status", "conditional" }, { "reason", reason } };
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class SpeechSpeakerPreviewInputV1
    {
        public string schemaVersion { get; set; }
        public string scope { get; set; }
        public string effectiveAt { get; set; }
        public string action { get; set; }
        public bool explicitSideEffectAuthorization { get; set; }
        public string previewLanguage { get; set; }
        public SpeechSettingsAdapterInputV1 adapterInput { get; set; }
    }

    internal static class SpeechSpeakerPreview
    {
        internal const string InputSchemaVersion = "speech-settings-speaker-preview-input.v1";
        internal const string OutputSchemaVersion = "speech-settings-speaker-preview-output.v1";
        private static readonly Dictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "zh-CN", "这是当前语音设置的自然声音预览。" },
            { "en-US", "This is a natural voice preview of the current settings." },
            { "es-ES", "Esta es una vista previa de la voz natural configurada." },
            { "ja-JP", "これは現在の自然音声設定のプレビューです。" },
            { "ko-KR", "현재 자연 음성 설정을 미리 듣는 예시입니다." }
        };

        public static object ExecuteFile(string inputPath)
        {
            var input = ReadInput(inputPath);
            ValidateInput(input);
            var adapter = new SpeechSettingsAdapter(input.adapterInput.catalog, input.adapterInput.deployment);
            var snapshot = adapter.CreateSnapshot(input.adapterInput.synthesisRequest.payload, input.adapterInput.settings);
            var edgePath = input.adapterInput.deployment.edgeTtsPath;
            var playerPath = input.adapterInput.deployment.ffplayPath;
            if (string.IsNullOrWhiteSpace(edgePath) || !File.Exists(edgePath))
                throw new SpeechContractException("edge_tts_not_available", "speaker.preview 找不到 Edge TTS 运行时");
            if (string.IsNullOrWhiteSpace(playerPath) || !File.Exists(playerPath))
                throw new SpeechContractException("playback_runtime_not_available", "speaker.preview 找不到播放器运行时");
            return ExecuteAsync(
                input,
                snapshot,
                new EdgeTtsSynthesizer(edgePath, 300000),
                new FfplayAudioPlayerFactory(playerPath),
                Path.Combine(Path.GetTempPath(), "kcsp", Guid.NewGuid().ToString("N")))
                .GetAwaiter().GetResult();
        }

        internal static async Task<object> ExecuteAsync(
            SpeechSpeakerPreviewInputV1 input,
            SpeechSettingsAdapterResult snapshot,
            ISpeechSynthesizer synthesizer,
            IAudioPlayerFactory playerFactory,
            string temporaryRoot)
        {
            ValidateInput(input);
            if (snapshot == null || snapshot.speechRequest == null) throw new SpeechContractException("missing_preview_snapshot", "缺少预览设置快照");
            var language = NormalizeTag(input.previewLanguage);
            string voice = null;
            foreach (var item in snapshot.speechRequest.voiceSnapshot.voicesByLanguage)
                if (string.Equals(NormalizeTag(item.Key), language, StringComparison.OrdinalIgnoreCase)) { voice = item.Value; break; }
            if (string.IsNullOrWhiteSpace(voice)) throw new SpeechContractException("preview_voice_not_configured", "previewLanguage 没有已选择的声音");
            string sample;
            if (!Samples.TryGetValue(language, out sample)) throw new SpeechContractException("preview_language_not_supported", "speaker.preview 暂不支持该语言");

            Directory.CreateDirectory(temporaryRoot);
            var textPath = Path.Combine(temporaryRoot, "preview.txt");
            var audioPath = Path.Combine(temporaryRoot, "preview.mp3");
            File.WriteAllText(textPath, sample, new UTF8Encoding(false));
            var route = new SpeechRoute { LanguageTag = language, Voice = voice, Strategy = "preview" };
            var started = DateTimeOffset.UtcNow;
            try
            {
                var synthesis = await synthesizer.SynthesizeAsync(textPath, audioPath, route, snapshot.speechRequest.voiceSnapshot).ConfigureAwait(false);
                if (synthesis == null || synthesis.ExitCode != 0)
                    throw new SpeechContractException("preview_synthesis_failed", "speaker.preview 合成失败" + (synthesis == null ? string.Empty : ": " + synthesis.StandardError));
                if (!File.Exists(audioPath) || new FileInfo(audioPath).Length <= 0)
                    throw new SpeechContractException("preview_audio_missing", "speaker.preview 未生成有效音频");
                PlaybackTerminal terminal;
                using (var coordinator = new PlaybackCoordinator(playerFactory))
                    terminal = await coordinator.PlayAsync("preview-" + Guid.NewGuid().ToString("N"), audioPath).ConfigureAwait(false);
                if (terminal == null || terminal.Status != "completed")
                    throw new SpeechContractException("preview_playback_failed", "speaker.preview 未自然完成");

                return new Dictionary<string, object>
                {
                    { "schemaVersion", OutputSchemaVersion }, { "companionVersion", SpeechSettingsCompanion.CompanionVersion },
                    { "workerId", WorkerCli.WorkerId }, { "workerVersion", WorkerCli.WorkerVersion },
                    { "status", "completed" }, { "action", "speaker.preview" },
                    { "scope", "project" }, { "effectiveAt", "preview_only" },
                    { "explicitAuthorizationConsumed", true }, { "sampleSource", "worker_built_in_short_sample" },
                    { "sampleText", sample }, { "languageTag", language }, { "voiceId", voice },
                    { "voiceSnapshot", snapshot.speechRequest.voiceSnapshot },
                    { "naturalCompletion", true }, { "playbackState", "idle" }, { "activePlayback", false },
                    { "manualStopRequired", false }, { "temporaryArtifactsRetained", false },
                    { "previewAttemptCount", 1 }, { "audioPlayerInvocationCount", 1 },
                    { "taskCreation", new Dictionary<string, object>
                        {
                            { "job", 0 }, { "attempt", 0 }, { "capture", 0 }, { "excerpt", 0 },
                            { "card", 0 }, { "export", 0 }
                        }
                    },
                    { "sideEffectsPerformed", new object[] { "edge_tts.preview_synthesis", "audio_player.preview" } },
                    { "startedAtUtc", started.ToString("o") }, { "finishedAtUtc", DateTimeOffset.UtcNow.ToString("o") }
                };
            }
            finally
            {
                try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true); } catch { }
            }
        }

        private static SpeechSpeakerPreviewInputV1 ReadInput(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new SpeechContractException("settings_path_not_absolute", "speaker.preview 输入必须是绝对 JSON 文件路径");
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new SpeechContractException("missing_settings_input", "speaker.preview 输入不存在");
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }
                    .Deserialize<SpeechSpeakerPreviewInputV1>(File.ReadAllText(full, new UTF8Encoding(false, true)));
            }
            catch (Exception ex) { throw new SpeechContractException("invalid_settings_input", "speaker.preview 输入不是严格 UTF-8 JSON", ex); }
        }

        private static void ValidateInput(SpeechSpeakerPreviewInputV1 input)
        {
            if (input == null || input.schemaVersion != InputSchemaVersion)
                throw new SpeechContractException("unsupported_speaker_preview_input", "不支持的 speaker.preview schemaVersion");
            if (input.scope != "project" || input.effectiveAt != "preview_only" || input.action != "speaker.preview")
                throw new SpeechContractException("invalid_speaker_preview_action", "speaker.preview 必须使用 project/preview_only/explicit action");
            if (!input.explicitSideEffectAuthorization)
                throw new SpeechContractException("speaker_preview_not_authorized", "speaker.preview 需要显式副作用授权");
            if (!VoiceRouter.IsBcp47(input.previewLanguage))
                throw new SpeechContractException("invalid_preview_language", "previewLanguage 必须是 BCP-47 标签");
            if (input.adapterInput == null || input.adapterInput.synthesisRequest == null)
                throw new SpeechContractException("missing_settings_adapter_input", "speaker.preview 缺少当前设置草案与合成模板");
        }

        private static string NormalizeTag(string value)
        {
            var parts = value.Split('-');
            if (parts.Length == 1) return parts[0].ToLowerInvariant();
            return parts[0].ToLowerInvariant() + "-" + string.Join("-", parts.Skip(1).Select(p => p.Length == 2 ? p.ToUpperInvariant() : p));
        }
    }
}

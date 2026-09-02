using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Speech.Worker
{
    internal static class VoiceRouter
    {
        internal static void ValidatePolicies(SpeechRequestV1 request)
        {
            if (request.language == null) throw new SpeechContractException("missing_language_policy", "缺少 language");
            if (request.language.strategy != "auto" && request.language.strategy != "override") throw new SpeechContractException("invalid_language_strategy", "language.strategy 无效");
            if (request.language.strategy == "auto" && !string.IsNullOrWhiteSpace(request.language.@override)) throw new SpeechContractException("unexpected_language_override", "auto 策略不能携带 override");
            if (request.language.strategy == "override" && !IsBcp47(request.language.@override)) throw new SpeechContractException("invalid_language_override", "override 必须是 BCP-47 标签");

            var voice = request.voiceSnapshot;
            if (voice == null) throw new SpeechContractException("missing_voice_snapshot", "缺少 voiceSnapshot");
            if (!string.Equals(voice.provider, "edge-tts", StringComparison.Ordinal)) throw new SpeechContractException("unsupported_voice_provider", "首版只允许 edge-tts");
            if (voice.voicesByLanguage == null || voice.voicesByLanguage.Count == 0) throw new SpeechContractException("missing_voice_map", "voicesByLanguage 不能为空");
            foreach (var item in voice.voicesByLanguage)
            {
                if (!IsBcp47(item.Key)) throw new SpeechContractException("invalid_voice_language", "voicesByLanguage 包含无效语言标签");
                if (string.IsNullOrWhiteSpace(item.Value) || !Regex.IsMatch(item.Value, "^[A-Za-z0-9-]+$")) throw new SpeechContractException("invalid_voice_name", "声音名称无效");
            }
            if (voice.ratePercent < -100 || voice.ratePercent > 100) throw new SpeechContractException("invalid_rate", "ratePercent 超出 -100..100");
            if (voice.volumePercent < -100 || voice.volumePercent > 100) throw new SpeechContractException("invalid_volume", "volumePercent 超出 -100..100");
            if (voice.pitchHz < -1000 || voice.pitchHz > 1000) throw new SpeechContractException("invalid_pitch", "pitchHz 超出 -1000..1000");

            var playback = request.playbackPolicy;
            if (playback == null || !playback.autoPlay || playback.activePlayback != "supersede" || playback.replay != "from_start" || playback.naturalCompletion != "idle_replayable" || playback.stopTerminal != "stopped")
                throw new SpeechContractException("unsupported_playback_policy", "playbackPolicy 与冻结的 v1 语义不一致");
            var cache = request.cachePolicy;
            if (cache == null || cache.scope != "task" || !cache.reuseSuccessfulAudio || cache.crossTaskReuse)
                throw new SpeechContractException("unsupported_cache_policy", "cachePolicy 与冻结的 v1 语义不一致");
        }

        internal static SpeechRoute Route(ValidatedSpeechRequest validated)
        {
            var policy = validated.Request.language;
            var tag = policy.strategy == "override" ? NormalizeTag(policy.@override) : Detect(validated.SpokenText);
            var voice = FindVoice(validated.Request.voiceSnapshot.voicesByLanguage, tag);
            if (voice == null) throw new SpeechContractException("voice_not_configured", "没有为语言 " + tag + " 配置声音");
            return new SpeechRoute { LanguageTag = tag, Voice = voice, Strategy = policy.strategy };
        }

        internal static string Detect(string text)
        {
            if (Regex.IsMatch(text, "[\\u3040-\\u30ff]")) return "ja-JP";
            if (Regex.IsMatch(text, "[\\uac00-\\ud7af]")) return "ko-KR";
            var han = Regex.Matches(text, "[\\u3400-\\u9fff]").Count;
            var latin = Regex.Matches(text, "[A-Za-z]").Count;
            if (han > 0 && han >= latin / 3) return "zh-CN";
            if (Regex.IsMatch(text, "[áéíóúñ¿¡]", RegexOptions.IgnoreCase) || Regex.IsMatch(text, @"\b(el|la|los|las|que|para|una|por|con|como)\b", RegexOptions.IgnoreCase)) return "es-ES";
            return "en-US";
        }

        internal static bool IsBcp47(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$");
        }

        private static string NormalizeTag(string value)
        {
            var parts = value.Split('-');
            if (parts.Length == 1) return parts[0].ToLowerInvariant();
            return parts[0].ToLowerInvariant() + "-" + string.Join("-", parts.Skip(1).Select(p => p.Length == 2 ? p.ToUpperInvariant() : p));
        }

        private static string FindVoice(Dictionary<string, string> voices, string tag)
        {
            foreach (var item in voices) if (string.Equals(item.Key, tag, StringComparison.OrdinalIgnoreCase)) return item.Value;
            var baseLanguage = tag.Split('-')[0];
            foreach (var item in voices) if (string.Equals(item.Key.Split('-')[0], baseLanguage, StringComparison.OrdinalIgnoreCase)) return item.Value;
            return null;
        }
    }
}

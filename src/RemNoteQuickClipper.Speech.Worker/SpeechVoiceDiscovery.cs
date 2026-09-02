using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class SpeechVoiceDiscoveryInputV1
    {
        public string schemaVersion { get; set; }
        public string mode { get; set; }
        public List<string> requestedLanguages { get; set; }
        public int cacheMaxAgeSeconds { get; set; }
        public SpeechDeploymentDiagnostics deployment { get; set; }
        public SpeechVoiceCatalog catalogSnapshot { get; set; }
    }

    internal interface IEdgeVoiceCatalogSource
    {
        string ListVoices(string edgeTtsPath);
    }

    internal sealed class ProcessEdgeVoiceCatalogSource : IEdgeVoiceCatalogSource
    {
        public string ListVoices(string edgeTtsPath)
        {
            if (string.IsNullOrWhiteSpace(edgeTtsPath) || !File.Exists(edgeTtsPath))
                throw new SpeechContractException("edge_tts_not_available", "voice discovery 找不到 Edge TTS 运行时");
            var start = new ProcessStartInfo
            {
                FileName = edgeTtsPath,
                Arguments = "--list-voices",
                WorkingDirectory = Path.GetDirectoryName(edgeTtsPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("无法启动 Edge TTS voice discovery");
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Edge TTS voice discovery 超时");
                }
                System.Threading.Tasks.Task.WaitAll(standardOutput, standardError);
                if (process.ExitCode != 0)
                    throw new SpeechContractException("voice_discovery_failed", "Edge TTS voice discovery 失败: " + standardError.Result.Trim());
                return standardOutput.Result;
            }
        }
    }

    internal sealed class SpeechVoiceDiscovery
    {
        internal const string InputSchemaVersion = "speech-settings-voice-discovery-input.v1";
        internal const string OutputSchemaVersion = "speech-settings-voice-discovery-output.v1";
        private static readonly string[] DefaultLanguages = { "zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR" };
        private readonly IEdgeVoiceCatalogSource source;
        private readonly Func<DateTimeOffset> utcNow;

        public SpeechVoiceDiscovery(IEdgeVoiceCatalogSource source, Func<DateTimeOffset> utcNow)
        {
            this.source = source;
            this.utcNow = utcNow ?? delegate { return DateTimeOffset.UtcNow; };
        }

        public static object ExecuteFile(string inputPath)
        {
            return new SpeechVoiceDiscovery(new ProcessEdgeVoiceCatalogSource(), delegate { return DateTimeOffset.UtcNow; })
                .Execute(ReadInput(inputPath));
        }

        internal object Execute(SpeechVoiceDiscoveryInputV1 input)
        {
            ValidateInput(input);
            var now = utcNow();
            var languages = NormalizeLanguages(input.requestedLanguages);
            var cached = CloneCatalog(input.catalogSnapshot);
            string cacheReadStatus = cached == null ? "miss" : "stale";

            SpeechVoiceCatalog catalog;
            string resultSource;
            string status = "ready";
            var readOperations = new List<object>();
            var providerQueryCount = 0;
            var cacheFresh = IsFresh(cached, now, input.cacheMaxAgeSeconds);

            if (input.mode == "validate_only")
            {
                catalog = CloneCatalog(input.catalogSnapshot);
                new SpeechSettingsAdapter(catalog, new SpeechDeploymentDiagnostics());
                resultSource = "supplied_snapshot";
                cacheReadStatus = cacheFresh ? "validated_fresh" : "validated_stale";
            }
            else if (input.mode == "cache_only")
            {
                if (cached == null)
                {
                    catalog = EmptyCatalog();
                    resultSource = "none";
                    status = "conditional";
                }
                else
                {
                    catalog = CloneCatalog(cached);
                    resultSource = "cache";
                    status = cacheFresh ? "ready" : "conditional";
                    cacheReadStatus = cacheFresh ? "hit_fresh" : "hit_stale";
                }
            }
            else if (cacheFresh)
            {
                catalog = CloneCatalog(cached);
                resultSource = "cache";
                cacheReadStatus = "hit_fresh";
            }
            else
            {
                try
                {
                    providerQueryCount = 1;
                    readOperations.Add("provider.voice_catalog_query");
                    var raw = source.ListVoices(input.deployment.edgeTtsPath);
                    catalog = Parse(raw, now);
                    resultSource = "live";
                    cacheReadStatus = cached == null ? "refreshed_from_miss" : "refreshed_from_stale";
                }
                catch
                {
                    if (cached == null) throw;
                    catalog = CloneCatalog(cached);
                    resultSource = "stale_cache_fallback";
                    status = "conditional";
                    cacheReadStatus = "fallback_stale";
                }
            }

            var selected = catalog.voices == null ? new List<SpeechVoiceOption>() : catalog.voices
                .Where(v => languages.Contains(NormalizeTag(v.languageTag), StringComparer.OrdinalIgnoreCase))
                .OrderBy(v => NormalizeTag(v.languageTag), StringComparer.Ordinal)
                .ThenBy(v => v.voiceId, StringComparer.Ordinal)
                .Select(CloneVoice).ToList();
            var routes = languages.Select(language =>
            {
                var available = selected.Count(v => string.Equals(NormalizeTag(v.languageTag), language, StringComparison.OrdinalIgnoreCase) && v.available && v.natural);
                if (available == 0) status = "conditional";
                return new Dictionary<string, object>
                {
                    { "languageTag", language }, { "availability", available > 0 ? "available" : "unavailable" },
                    { "naturalVoiceCount", available },
                    { "voices", selected.Where(v => string.Equals(NormalizeTag(v.languageTag), language, StringComparison.OrdinalIgnoreCase)).ToArray() }
                };
            }).ToArray();
            DateTimeOffset updated;
            var hasUpdated = DateTimeOffset.TryParse(catalog.updatedAtUtc, out updated);
            var expires = hasUpdated ? updated.AddSeconds(input.cacheMaxAgeSeconds) : (DateTimeOffset?)null;
            var ageSeconds = hasUpdated ? Math.Max(0, (long)(now - updated).TotalSeconds) : (long?)null;
            var catalogSha256 = HashCatalog(catalog);
            var catalogRevision = hasUpdated ? "edge-tts-" + updated.UtcDateTime.ToString("yyyyMMddTHHmmssZ") + "-" + catalogSha256.Substring(0, 12) : "edge-tts-unversioned-" + catalogSha256.Substring(0, 12);

            return new Dictionary<string, object>
            {
                { "schemaVersion", OutputSchemaVersion }, { "companionVersion", SpeechSettingsCompanion.CompanionVersion },
                { "workerId", WorkerCli.WorkerId }, { "workerVersion", WorkerCli.WorkerVersion },
                { "status", status }, { "mode", input.mode }, { "source", resultSource },
                { "provider", SpeechSettingsAdapter.Provider }, { "catalogVersion", catalog.catalogVersion },
                { "catalogRevision", catalogRevision }, { "catalogSha256", catalogSha256 },
                { "updatedAtUtc", catalog.updatedAtUtc }, { "requestedLanguages", languages.ToArray() },
                { "routes", routes }, { "voices", selected.ToArray() },
                { "cache", new Dictionary<string, object>
                    {
                        { "status", cacheReadStatus }, { "persistenceOwner", "desktop-core" }, { "workerWrites", 0 },
                        { "maxAgeSeconds", input.cacheMaxAgeSeconds }, { "ageSeconds", ageSeconds },
                        { "isFresh", hasUpdated && expires.Value > now }, { "expiresAtUtc", expires.HasValue ? expires.Value.ToString("o") : null }
                    }
                },
                { "deployment", new Dictionary<string, object>
                    {
                        { "edgeTtsPath", input.deployment == null ? null : input.deployment.edgeTtsPath },
                        { "edgeTtsStatus", input.deployment == null || string.IsNullOrWhiteSpace(input.deployment.edgeTtsPath) ? "not_configured" : File.Exists(input.deployment.edgeTtsPath) ? "available" : "missing" },
                        { "ownership", "deployment_read_only" }, { "providerQueryCount", providerQueryCount }, { "cacheWriteCount", 0 }
                    }
                },
                { "settingsMutationCount", 0 },
                { "taskCreation", new Dictionary<string, object> { { "job", 0 }, { "attempt", 0 }, { "capture", 0 }, { "excerpt", 0 }, { "card", 0 }, { "export", 0 } } },
                { "physicalSpeakerPlayback", false }, { "readOperationsPerformed", readOperations.ToArray() },
                { "sideEffectsPerformed", new object[0] }
            };
        }

        private static SpeechVoiceDiscoveryInputV1 ReadInput(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new SpeechContractException("settings_path_not_absolute", "voice discovery 输入必须是绝对 JSON 文件路径");
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new SpeechContractException("missing_settings_input", "voice discovery 输入不存在");
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }
                    .Deserialize<SpeechVoiceDiscoveryInputV1>(File.ReadAllText(full, new UTF8Encoding(false, true)));
            }
            catch (Exception ex) { throw new SpeechContractException("invalid_settings_input", "voice discovery 输入不是严格 UTF-8 JSON", ex); }
        }

        private static void ValidateInput(SpeechVoiceDiscoveryInputV1 input)
        {
            if (input == null || input.schemaVersion != InputSchemaVersion)
                throw new SpeechContractException("unsupported_voice_discovery_input", "不支持的 voice discovery schemaVersion");
            if (input.mode != "discover" && input.mode != "cache_only" && input.mode != "validate_only")
                throw new SpeechContractException("invalid_voice_discovery_mode", "voice discovery mode 必须为 discover、cache_only 或 validate_only");
            if (input.cacheMaxAgeSeconds < 60 || input.cacheMaxAgeSeconds > 604800)
                throw new SpeechContractException("invalid_voice_cache_age", "cacheMaxAgeSeconds 必须在 60..604800");
            NormalizeLanguages(input.requestedLanguages);
            if (input.mode == "validate_only" && input.catalogSnapshot == null)
                throw new SpeechContractException("missing_voice_catalog", "validate_only 必须提供 catalogSnapshot");
            if (input.mode == "discover")
            {
                if (input.deployment == null || string.IsNullOrWhiteSpace(input.deployment.edgeTtsPath) || !Path.IsPathRooted(input.deployment.edgeTtsPath))
                    throw new SpeechContractException("runtime_path_not_absolute", "discover 的 deployment.edgeTtsPath 必须是绝对部署路径");
            }
            if (input.deployment == null) input.deployment = new SpeechDeploymentDiagnostics();
        }

        private static List<string> NormalizeLanguages(List<string> languages)
        {
            var values = languages == null || languages.Count == 0 ? DefaultLanguages.ToList() : languages;
            var result = new List<string>();
            foreach (var value in values)
            {
                if (!VoiceRouter.IsBcp47(value)) throw new SpeechContractException("invalid_voice_language", "requestedLanguages 包含无效 BCP-47 标签");
                var normalized = NormalizeTag(value);
                if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) result.Add(normalized);
            }
            return result;
        }

        private static SpeechVoiceCatalog Parse(string raw, DateTimeOffset now)
        {
            var voices = new List<SpeechVoiceOption>();
            foreach (var line in (raw ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line.Trim(), "^(?<voice>[A-Za-z]{2,3}-[A-Za-z0-9]{2,8}-[A-Za-z0-9-]+Neural)\\s+(?<gender>Female|Male)(?:\\s+.*)?$");
                if (!match.Success) continue;
                var id = match.Groups["voice"].Value;
                var parts = id.Split('-');
                if (parts.Length < 3) continue;
                var language = NormalizeTag(parts[0] + "-" + parts[1]);
                var display = string.Join("-", parts.Skip(2)).Replace("Neural", string.Empty);
                voices.Add(new SpeechVoiceOption
                {
                    languageTag = language, voiceId = id, displayName = display,
                    gender = match.Groups["gender"].Value.ToLowerInvariant(), natural = true,
                    available = true, status = "available"
                });
            }
            if (voices.Count == 0) throw new SpeechContractException("empty_voice_discovery", "Edge TTS 未返回可解析的自然声音");
            var updated = now.ToString("o");
            return new SpeechVoiceCatalog
            {
                provider = SpeechSettingsAdapter.Provider,
                catalogVersion = "edge-tts-" + HashText(raw).Substring(0, 16),
                updatedAtUtc = updated,
                voices = voices.GroupBy(v => v.voiceId, StringComparer.Ordinal).Select(g => g.First()).ToList()
            };
        }

        private static bool IsFresh(SpeechVoiceCatalog cached, DateTimeOffset now, int maxAgeSeconds)
        {
            DateTimeOffset updated;
            return cached != null && DateTimeOffset.TryParse(cached.updatedAtUtc, out updated) && updated <= now && now - updated <= TimeSpan.FromSeconds(maxAgeSeconds);
        }

        private static SpeechVoiceCatalog EmptyCatalog()
        {
            return new SpeechVoiceCatalog { provider = SpeechSettingsAdapter.Provider, catalogVersion = null, updatedAtUtc = null, voices = new List<SpeechVoiceOption>() };
        }

        private static SpeechVoiceCatalog CloneCatalog(SpeechVoiceCatalog value)
        {
            if (value == null) return null;
            return new SpeechVoiceCatalog
            {
                provider = value.provider, catalogVersion = value.catalogVersion, updatedAtUtc = value.updatedAtUtc,
                voices = value.voices == null ? new List<SpeechVoiceOption>() : value.voices.Select(CloneVoice).ToList()
            };
        }

        private static SpeechVoiceOption CloneVoice(SpeechVoiceOption value)
        {
            return new SpeechVoiceOption
            {
                languageTag = value.languageTag, voiceId = value.voiceId, displayName = value.displayName,
                gender = value.gender, natural = value.natural, available = value.available, status = value.status
            };
        }

        private static string NormalizeTag(string value)
        {
            var parts = value.Split('-');
            if (parts.Length == 1) return parts[0].ToLowerInvariant();
            return parts[0].ToLowerInvariant() + "-" + string.Join("-", parts.Skip(1).Select(p => p.Length == 2 ? p.ToUpperInvariant() : p));
        }

        private static string HashText(string value)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string HashCatalog(SpeechVoiceCatalog catalog)
        {
            var material = string.Join("\n", (catalog.voices ?? new List<SpeechVoiceOption>())
                .OrderBy(v => NormalizeTag(v.languageTag), StringComparer.Ordinal)
                .ThenBy(v => v.voiceId, StringComparer.Ordinal)
                .Select(v => string.Join("|", new[]
                {
                    NormalizeTag(v.languageTag), v.voiceId ?? string.Empty, v.displayName ?? string.Empty,
                    v.gender ?? string.Empty, v.natural ? "natural" : "non_natural",
                    v.available ? "available" : "unavailable", v.status ?? string.Empty
                })));
            return HashText(material);
        }
    }
}

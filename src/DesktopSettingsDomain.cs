using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KnowledgeClipper
{
    internal static class ContractJson
    {
        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true
        };
    }

    internal sealed record ArtifactReference(string ArtifactRef, string Kind, string ContractVersion, string ManifestPath, string ExpectedSha256);
    internal sealed record WorkflowSettingsDocument(string SchemaVersion, string ActiveProfileId, IReadOnlyDictionary<string, WorkflowSettingsProfile> Profiles);
    internal sealed record WorkflowSettingsProfile(string DisplayName, WorkflowRouteSettings Workflow = null, CaptureSettings Capture = null, SpeechSettings Speech = null, RemNoteDestinationSettings Remnote = null, ImageOcrSettings ImageOcr = null, ContentSettingsReference Content = null);
    internal sealed record WorkflowRouteSettings(string ProcessingProfile = null, IReadOnlyList<string> Destinations = null);
    internal sealed record CaptureSettings(bool? ContextEnabled = null, string ContextGranularity = null, string ImageHandling = null);
    internal sealed record ImageOcrSettings(string SchemaVersion, ImageOcrProviderSettings ProviderSettings, string MineruScriptPath = null);
    internal sealed record ImageOcrProviderSettings(string ModelVersion, string Language, bool EnableTable, bool EnableFormula, int TimeoutSeconds, int PollIntervalSeconds);
    internal sealed record ContentSettingsReference(string ConfigArtifactRef, string ExpectedSha256, string SettingsRevisionId);
    internal sealed record SpeechSettings(IReadOnlyDictionary<string, string> VoicesByLanguage = null, int? RatePercent = null, int? VolumePercent = null, int? PitchHz = null, bool? AutoPlay = null, string LanguageStrategy = null, string LanguageOverride = null, SpeechDeploymentSettings Deployment = null);
    internal sealed record SpeechDeploymentSettings(string EdgeTtsPath = null, string FfplayPath = null);
    internal sealed record RemNoteDestinationSettings(string SchemaVersion, bool Enabled, RemNoteTargetSettings Target, RemNoteAdvancedSettings Advanced);
    internal sealed record RemNoteTargetSettings(string RootRemId, string RootTitle);
    internal sealed record RemNoteAdvancedSettings(string Sink, string ClipRouting, string CardRouting, string TimeZone, string DateFormat);

    internal sealed record WorkflowSettingsLoadResult(WorkflowSettingsDocument Document, string Source, string BackupPath);

    internal static class SettingsControlValidator
    {
        private static readonly HashSet<string> ProcessingProfiles = new HashSet<string>(new[] { "learning", "cards" }, StringComparer.Ordinal);
        private static readonly HashSet<string> Destinations = new HashSet<string>(new[] { "remnote", "anki" }, StringComparer.Ordinal);

        internal static void Validate(WorkflowSettingsDocument document)
        {
            if (document == null || document.SchemaVersion != "knowledge-clipper-workflow-settings.v3" || document.Profiles == null || document.Profiles.Count == 0 || !document.Profiles.ContainsKey(document.ActiveProfileId))
                throw new InvalidDataException("settings.document-invalid");
            foreach (var pair in document.Profiles)
            {
                var profile = pair.Value;
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64 || profile == null || string.IsNullOrWhiteSpace(profile.DisplayName)) throw new InvalidDataException("settings.profile-invalid");
                if (profile.Workflow != null)
                {
                    if (!ProcessingProfiles.Contains(profile.Workflow.ProcessingProfile ?? "cards")) throw new InvalidDataException("settings.processing-profile-invalid");
                    var destinations = profile.Workflow.Destinations ?? Array.Empty<string>();
                    if (destinations.Distinct(StringComparer.Ordinal).Count() != destinations.Count || destinations.Any(item => !Destinations.Contains(item))) throw new InvalidDataException("settings.destinations-invalid");
                }
                if (profile.Capture != null && profile.Capture.ContextGranularity != "sentence" && profile.Capture.ContextGranularity != "paragraph") throw new InvalidDataException("settings.capture-invalid");
                if (profile.Capture != null && profile.Capture.ImageHandling != "ocr" && profile.Capture.ImageHandling != "original") throw new InvalidDataException("settings.capture-invalid");
                if (profile.Speech != null)
                {
                    if (profile.Speech.AutoPlay != null || profile.Speech.RatePercent is < -100 or > 100 || profile.Speech.VolumePercent is < -100 or > 100 || profile.Speech.PitchHz is < -100 or > 100) throw new InvalidDataException("settings.speech-invalid");
                    var strategy = profile.Speech.LanguageStrategy ?? "auto";
                    if (strategy != "auto" && strategy != "override") throw new InvalidDataException("settings.speech-invalid");
                    if (strategy == "override" && string.IsNullOrWhiteSpace(profile.Speech.LanguageOverride)) throw new InvalidDataException("settings.speech-invalid");
                }
                if (profile.Remnote != null && (profile.Remnote.SchemaVersion != "remnote-destination-settings.v1" || string.IsNullOrWhiteSpace(profile.Remnote.Target?.RootTitle) || profile.Remnote.Enabled && string.IsNullOrWhiteSpace(profile.Remnote.Target?.RootRemId))) throw new InvalidDataException("settings.remnote-invalid");
                if (profile.ImageOcr != null && (profile.ImageOcr.SchemaVersion != "image-ocr-settings.v1" || profile.ImageOcr.ProviderSettings == null)) throw new InvalidDataException("settings.image-ocr-invalid");
            }
        }
    }

    internal sealed class WorkflowSettingsStore
    {
        internal System.Threading.Tasks.Task<WorkflowSettingsLoadResult> LoadAsync(string path)
        {
            return System.Threading.Tasks.Task.Run(delegate
            {
                try { return new WorkflowSettingsLoadResult(Read(path), "primary", null); }
                catch
                {
                    var backupRoot = Path.Combine(Path.GetDirectoryName(path), "backups");
                    if (!Directory.Exists(backupRoot)) throw;
                    foreach (var backup in Directory.GetFiles(backupRoot, Path.GetFileName(path) + ".*.bak.json").OrderByDescending(item => item, StringComparer.Ordinal))
                    {
                        try { return new WorkflowSettingsLoadResult(Read(backup), "backup", backup); } catch { }
                    }
                    throw;
                }
            });
        }

        internal System.Threading.Tasks.Task<string> SaveAsync(string path, WorkflowSettingsDocument document)
        {
            return System.Threading.Tasks.Task.Run(delegate
            {
                SettingsControlValidator.Validate(document);
                path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path));
                string backup = null;
                if (File.Exists(path))
                {
                    var backupRoot = Path.Combine(Path.GetDirectoryName(path), "backups"); Directory.CreateDirectory(backupRoot);
                    backup = Path.Combine(backupRoot, Path.GetFileName(path) + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "." + Guid.NewGuid().ToString("N") + ".bak.json");
                    File.Copy(path, backup, false);
                }
                var temporary = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, ContractJson.Options));
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return backup;
            });
        }

        private static WorkflowSettingsDocument Read(string path)
        {
            var document = JsonSerializer.Deserialize<WorkflowSettingsDocument>(File.ReadAllBytes(path), ContractJson.Options) ?? throw new InvalidDataException("settings.document-empty");
            SettingsControlValidator.Validate(document); return document;
        }
    }
}

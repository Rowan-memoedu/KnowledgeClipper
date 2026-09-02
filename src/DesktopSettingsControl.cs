using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal sealed class DesktopSettingsCapabilities
    {
        private readonly Dictionary<string, string> moduleStatus;
        private readonly HashSet<string> editablePointers;

        internal DesktopSettingsCapabilities(Dictionary<string, string> moduleStatus, HashSet<string> editablePointers)
        {
            this.moduleStatus = moduleStatus;
            this.editablePointers = editablePointers;
        }

        internal string Status(string moduleId) { string value; return moduleStatus.TryGetValue(moduleId, out value) ? value : "unsupported"; }
        internal bool Editable(string pointer) { return editablePointers.Contains(pointer); }
    }

    internal sealed class DesktopAuthorityBindingProjection
    {
        private DesktopAuthorityBindingProjection(bool isBound, string version, string statusCode, string checkpointId, string treeSha256, string registrySha256)
        {
            IsBound = isBound;
            Version = version;
            StatusCode = statusCode;
            CheckpointId = checkpointId;
            TreeSha256 = treeSha256;
            RegistrySha256 = registrySha256;
        }

        internal bool IsBound { get; }
        internal string Version { get; }
        internal string StatusCode { get; }
        internal string CheckpointId { get; }
        internal string TreeSha256 { get; }
        internal string RegistrySha256 { get; }
        internal string DisplayVersion { get { return IsBound ? "authority v" + Version : "authority unknown（绑定不可用：" + StatusCode + "）"; } }

        internal static DesktopAuthorityBindingProjection Load(string applicationBaseDirectory)
        {
            try
            {
                var integration = CandidateIntegrationConfig.Load(applicationBaseDirectory);
                if (integration == null) return Unbound("candidate-integration-missing");
                if (string.IsNullOrWhiteSpace(integration.AuthorityCheckpointId)) return Unbound("checkpoint-id-missing");
                if (!IsSha256(integration.AuthorityTreeSha256)) return Unbound("release-tree-sha256-invalid");
                if (!File.Exists(integration.Registry)) return Unbound("registry-missing");
                var metadataRoot = Path.GetDirectoryName(integration.Registry);
                var checkpointPath = Path.Combine(metadataRoot ?? string.Empty, "checkpoint.json");
                if (!File.Exists(checkpointPath)) return Unbound("checkpoint-missing");

                using var registryDocument = JsonDocument.Parse(File.ReadAllBytes(integration.Registry));
                using var checkpointDocument = JsonDocument.Parse(File.ReadAllBytes(checkpointPath));
                var registryVersion = registryDocument.RootElement.GetProperty("registryVersion").GetString();
                var checkpoint = checkpointDocument.RootElement;
                if (!string.Equals(checkpoint.GetProperty("checkpointId").GetString(), integration.AuthorityCheckpointId, StringComparison.Ordinal))
                    return Unbound("checkpoint-id-mismatch");
                if (!string.Equals(checkpoint.GetProperty("admissionStatus").GetString(), "authority-admitted", StringComparison.Ordinal))
                    return Unbound("checkpoint-not-admitted");
                var checkpointRegistry = checkpoint.GetProperty("registry");
                var checkpointRelease = checkpoint.GetProperty("release");
                if (!string.Equals(checkpointRegistry.GetProperty("registryVersion").GetString(), registryVersion, StringComparison.Ordinal))
                    return Unbound("registry-version-mismatch");
                if (!string.Equals(checkpointRelease.GetProperty("treeSha256").GetString(), integration.AuthorityTreeSha256, StringComparison.OrdinalIgnoreCase))
                    return Unbound("release-tree-mismatch");
                var registrySha256 = Sha256(integration.Registry);
                if (!string.Equals(checkpointRegistry.GetProperty("sha256").GetString(), registrySha256, StringComparison.OrdinalIgnoreCase))
                    return Unbound("registry-sha256-mismatch");
                var checkpointVersion = VersionFromCheckpointId(integration.AuthorityCheckpointId);
                if (checkpointVersion == null || !string.Equals(checkpointVersion, registryVersion, StringComparison.Ordinal))
                    return Unbound("authority-version-mismatch");
                return new DesktopAuthorityBindingProjection(true, registryVersion, "bound", integration.AuthorityCheckpointId,
                    integration.AuthorityTreeSha256.ToUpperInvariant(), registrySha256.ToUpperInvariant());
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is KeyNotFoundException)
            {
                return Unbound("metadata-invalid");
            }
        }

        private static DesktopAuthorityBindingProjection Unbound(string statusCode)
        {
            return new DesktopAuthorityBindingProjection(false, "unknown", statusCode, null, null, null);
        }

        private static string VersionFromCheckpointId(string checkpointId)
        {
            const string marker = "knowledge-clipper-authority-";
            if (string.IsNullOrWhiteSpace(checkpointId) || !checkpointId.StartsWith(marker, StringComparison.Ordinal)) return null;
            var remainder = checkpointId.Substring(marker.Length);
            var separator = remainder.IndexOf('-');
            if (separator <= 0) return null;
            var version = remainder.Substring(0, separator);
            return version.Split('.').Length == 3 && version.All(value => char.IsDigit(value) || value == '.') ? version : null;
        }

        private static bool IsSha256(string value)
        {
            return value != null && value.Length == 64 && value.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'));
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }
    }

    internal sealed class DesktopWorkflowSettingsService
    {
        internal const string ContractCheckpointVersion = "2.5.1";
        internal const string ContractCheckpointSha256 = "62996ef471f46470090be40259bf95acd84eb7640084a1fcd4ec85e680aee4f3";
        internal const string AdapterVersion = "3";
        internal const string AdapterSha256 = "c59c4234072fb40b538c4c3275b10ae827ea130ac70b5157f3140149408c16bc";
        private readonly WorkflowSettingsStore store = new WorkflowSettingsStore();
        private readonly string settingsPath;
        private readonly string legacySettingsV2Path;
        private readonly string legacySettingsV1Path;
        private readonly string portableBackupPath;
        private readonly string applicationBaseDirectory;

        internal string ContractRoot { get; private set; }
        internal string ResolverConfigurationPath { get; private set; }
        internal string SettingsPath { get { return settingsPath; } }
        internal string WorkDirectory { get { return Path.GetDirectoryName(settingsPath); } }
        internal string AuthorityReleaseRoot { get; private set; }
        internal string AuthorityRegistryPath { get { return FindAuthorityRegistryPath(); } }
        internal string AuthorityVersion { get; private set; }
        internal DesktopAuthorityBindingProjection AuthorityBinding { get; private set; }
        internal string BindingStatus { get { return "Core settings checkpoint v" + ContractCheckpointVersion + " / adapter v" + AdapterVersion + " / " + AuthorityBinding.DisplayVersion + "；仅真实映射字段可编辑。"; } }
        internal string RetentionPolicyEvidence { get { return AuthorityBinding.DisplayVersion + "：显式评估/隔离可用；自动执行关闭（automatic=false）；破坏性删除不提供。旧按目录时间删除器保持生产禁用。"; } }
        internal DesktopSettingsCapabilities Capabilities { get; private set; }

        internal DesktopWorkflowSettingsService(string workDirectory, string applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(workDirectory)) throw new ArgumentException("workDirectory");
            this.applicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory);
            settingsPath = Path.Combine(Path.GetFullPath(workDirectory), "workflow-settings.v3.json");
            legacySettingsV2Path = Path.Combine(Path.GetFullPath(workDirectory), "workflow-settings.v2.json");
            legacySettingsV1Path = Path.Combine(Path.GetFullPath(workDirectory), "workflow-settings.v1.json");
            portableBackupPath = settingsPath + ".bak";
            ContractRoot = FindRelative(applicationBaseDirectory, Path.Combine("contracts", "core", "v2.4", "settings-control"), true);
            ResolverConfigurationPath = FindRelative(applicationBaseDirectory, Path.Combine("contracts", "core", "v2.1", "profile-resolver.v2.1.json"), false);
            AuthorityReleaseRoot = FindAuthorityReleaseRoot(applicationBaseDirectory);
            AuthorityVersion = ReadAuthorityVersion(applicationBaseDirectory, AuthorityReleaseRoot);
            AuthorityBinding = DesktopAuthorityBindingProjection.Load(applicationBaseDirectory);
            VerifyContract();
            Capabilities = ReadCapabilities();
        }

        internal WorkflowSettingsDocument LoadOrCreate(UserSettings seed)
        {
            if (!File.Exists(settingsPath))
            {
                var legacyPath = File.Exists(legacySettingsV2Path) ? legacySettingsV2Path : legacySettingsV1Path;
                if (File.Exists(legacyPath))
                {
                    var legacy = System.Threading.Tasks.Task.Run(() => store.LoadAsync(legacyPath)).GetAwaiter().GetResult().Document;
                    var upgraded = UpgradeToV3(legacy);
                    Save(upgraded);
                    return upgraded;
                }
                var created = Defaults(seed);
                Save(created);
                return created;
            }
            try { return System.Threading.Tasks.Task.Run(() => store.LoadAsync(settingsPath)).GetAwaiter().GetResult().Document; }
            catch
            {
                if (!File.Exists(portableBackupPath)) throw;
                var recovered = JsonSerializer.Deserialize<WorkflowSettingsDocument>(File.ReadAllBytes(portableBackupPath), ContractJson.Options);
                if (recovered == null) throw new InvalidDataException("settings.backup-empty");
                SettingsControlValidator.Validate(recovered);
                return recovered;
            }
        }

        internal void Save(WorkflowSettingsDocument document)
        {
            SettingsControlValidator.Validate(document);
            System.Threading.Tasks.Task.Run(() => store.SaveAsync(settingsPath, document)).GetAwaiter().GetResult();
            File.Copy(settingsPath, portableBackupPath, true);
        }

        internal string CurrentRevision
        {
            get { return File.Exists(settingsPath) ? Sha256(settingsPath) : "not-persisted"; }
        }

        internal string SettingsSourceEvidence(WorkflowSettingsDocument document)
        {
            return "active-profile:" + document.ActiveProfileId + "；优先级 taskOverride > activeProfile > moduleDefault";
        }

        internal string SettingsEffectiveAtEvidence
        {
            get { return "保存后用于下一任务；capture 模式用于下一采集会话；运行中任务不热改"; }
        }

        internal string NextTaskSnapshotEvidence(WorkflowSettingsDocument document)
        {
            return "knowledge-clipper-task-settings-snapshot.v3；source.activeProfileId=" + document.ActiveProfileId + "；source.settingsSha256=" + CurrentRevision;
        }

        internal string AuthorityEvidence
        {
            get
            {
                if (AuthorityBinding.IsBound) return AuthorityBinding.CheckpointId + "；tree=" + AuthorityBinding.TreeSha256;
                return AuthorityBinding.DisplayVersion;
            }
        }

        internal string RegistryEvidence
        {
            get
            {
                if (AuthorityBinding.IsBound) return "registryVersion=" + AuthorityBinding.Version + "；sha256=" + AuthorityBinding.RegistrySha256;
                return "registryVersion=unknown；fail-closed=" + AuthorityBinding.StatusCode;
            }
        }

        internal string CheckpointEvidence
        {
            get { return "Core checkpoint v" + ContractCheckpointVersion + "；sha256=" + ContractCheckpointSha256 + "；admitted adapter v" + AdapterVersion + " sha256=" + AdapterSha256; }
        }

        internal bool RemNoteExporterV2Available()
        {
            try
            {
                var evidence = Path.Combine(AuthorityReleaseRoot, "evidence", "modules", "remnote-v2-offline-receipt.json");
                if (!File.Exists(evidence)) return false;
                using var document = JsonDocument.Parse(File.ReadAllBytes(evidence));
                var worker = document.RootElement.GetProperty("worker");
                return worker.GetProperty("workerId").GetString() == "export.remnote" && worker.GetProperty("workerVersion").GetString() == "2.0.0";
            }
            catch { }
            return false;
        }

        internal static WorkflowSettingsDocument Defaults(UserSettings seed)
        {
            seed = seed ?? UserSettings.Defaults();
            var profile = new WorkflowSettingsProfile(
                "默认工作流",
                new WorkflowRouteSettings("cards", Array.Empty<string>()),
                new CaptureSettings(seed.ContextModeEnabled, seed.ContextGranularity, seed.OriginalImageMode ? "original" : "ocr"),
                new SpeechSettings(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "zh-CN", seed.ChineseVoice }, { "en-US", seed.EnglishVoice }, { "es-ES", seed.SpanishVoice },
                    { "ja-JP", seed.JapaneseVoice }, { "ko-KR", seed.KoreanVoice }
                }, seed.RatePercent, seed.VolumePercent, seed.PitchHz, null,
                    seed.LanguageOverride == "auto" ? "auto" : "override",
                    seed.LanguageOverride == "auto" ? null : seed.LanguageOverride),
                null,
                new ImageOcrSettings("image-ocr-settings.v1", new ImageOcrProviderSettings("vlm", "ch", true, true, 900, 5)));
            return new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v3", "default",
                new Dictionary<string, WorkflowSettingsProfile>(StringComparer.Ordinal) { { "default", profile } });
        }

        private static WorkflowSettingsDocument UpgradeToV3(WorkflowSettingsDocument legacy)
        {
            var profiles = new Dictionary<string, WorkflowSettingsProfile>(StringComparer.Ordinal);
            foreach (var pair in legacy.Profiles)
            {
                var speech = pair.Value.Speech;
                if (speech != null)
                {
                    speech = speech with
                    {
                        AutoPlay = null,
                        LanguageStrategy = string.IsNullOrWhiteSpace(speech.LanguageStrategy) ? "auto" : speech.LanguageStrategy,
                        LanguageOverride = string.IsNullOrWhiteSpace(speech.LanguageStrategy) ? null : speech.LanguageOverride
                    };
                }
                profiles.Add(pair.Key, pair.Value with
                {
                    Speech = speech,
                    ImageOcr = pair.Value.ImageOcr ?? new ImageOcrSettings("image-ocr-settings.v1", new ImageOcrProviderSettings("vlm", "ch", true, true, 900, 5))
                });
            }
            return new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v3", legacy.ActiveProfileId, profiles);
        }

        private void VerifyContract()
        {
            var checkpoint = FindRelative(applicationBaseDirectory, Path.Combine("contracts", "core", "v2.5", "checkpoints", "v2.5.1-20260830T075400Z", "checkpoint.json"), false);
            var adapter = Path.Combine(ContractRoot, "module-settings-adapter.v3.json");
            if (!string.Equals(Sha256(checkpoint), ContractCheckpointSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("settings-control.checkpoint-sha256-mismatch");
            if (!string.Equals(Sha256(adapter), AdapterSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("settings-control.adapter-sha256-mismatch");
        }

        private DesktopSettingsCapabilities ReadCapabilities()
        {
            var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
            var pointers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var adapterName in new[] { "module-settings-adapter.v3.json" })
            {
                using (var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContractRoot, adapterName))))
                {
                    var version = document.RootElement.GetProperty("schemaVersion").GetString();
                    if (version != "knowledge-clipper-module-settings-adapter.v3") throw new InvalidDataException("settings-control.adapter-version-unsupported");
                    foreach (var module in document.RootElement.GetProperty("modules").EnumerateArray())
                    {
                        var id = module.GetProperty("moduleId").GetString();
                        statuses[id] = module.GetProperty("status").GetString();
                        foreach (var field in module.GetProperty("fields").EnumerateArray())
                            if (field.GetProperty("editable").GetBoolean()) pointers.Add(field.GetProperty("settingPointer").GetString());
                    }
                }
            }
            return new DesktopSettingsCapabilities(statuses, pointers);
        }

        private static string FindRelative(string start, string relative, bool directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, relative);
                if (directory ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
                current = current.Parent;
            }
            var integration = CandidateIntegrationConfig.Load(start);
            if (integration != null)
            {
                var candidate = Path.Combine(integration.ReleaseRoot, relative);
                if (directory ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("settings-control.contract-path-missing", relative);
        }

        private static string FindAuthorityReleaseRoot(string start)
        {
            var integration = CandidateIntegrationConfig.Load(start);
            if (integration != null && Directory.Exists(integration.ReleaseRoot)) return integration.ReleaseRoot;
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "releases", "knowledge-clipper-authority-v1.2.12-20260830T075600Z", "win-x64");
                if (Directory.Exists(candidate)) return candidate;
                current = current.Parent;
            }
            return Path.Combine(Path.GetFullPath(start), "authority", "win-x64");
        }

        private static string ReadAuthorityVersion(string start, string authorityReleaseRoot)
        {
            var integration = CandidateIntegrationConfig.Load(start);
            var identity = integration == null ? null : integration.AuthorityCheckpointId;
            if (string.IsNullOrWhiteSpace(identity)) identity = Directory.GetParent(authorityReleaseRoot)?.Name;
            const string marker = "authority-";
            var markerIndex = (identity ?? string.Empty).IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var versionStart = markerIndex + marker.Length;
                if (versionStart < identity.Length && (identity[versionStart] == 'v' || char.IsDigit(identity[versionStart])))
                {
                    if (identity[versionStart] == 'v') versionStart++;
                    var versionEnd = identity.IndexOf('-', versionStart);
                    if (versionEnd < 0) versionEnd = identity.Length;
                    var version = identity.Substring(versionStart, versionEnd - versionStart);
                    if (!string.IsNullOrWhiteSpace(version)) return version;
                }
            }
            return "unknown";
        }

        private string FindAuthorityRegistryPath()
        {
            var integration = CandidateIntegrationConfig.Load(applicationBaseDirectory);
            if (integration != null && File.Exists(integration.Registry)) return integration.Registry;
            var current = new DirectoryInfo(applicationBaseDirectory);
            while (current != null)
            {
                var root = Path.Combine(current.FullName, "contracts", "authority", "worker-registry");
                if (Directory.Exists(root))
                {
                    var matches = Directory.GetDirectories(root, "v" + AuthorityVersion + "-*", SearchOption.TopDirectoryOnly)
                        .Select(path => Path.Combine(path, "worker-registry.v1.json")).Where(File.Exists).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                    if (matches.Length == 1) return matches[0];
                }
                current = current.Parent;
            }
            return string.Empty;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
    }

    internal sealed class CoreDesktopTaskSettingsPublisher : IDesktopTaskSettingsPublisher
    {
        private readonly DesktopWorkflowSettingsService settings;
        private readonly DesktopModuleSettingsStore localStore;
        internal CoreDesktopTaskSettingsPublisher(DesktopWorkflowSettingsService settings) { this.settings = settings ?? throw new ArgumentNullException("settings"); localStore = new DesktopModuleSettingsStore(settings.WorkDirectory); }

        public IReadOnlyList<DesktopAdditionalInput> PrepareInputs(string taskRoot, string taskId, string intent)
        {
            var document = settings.LoadOrCreate(UserSettings.Defaults());
            var active = EffectiveProfile(document.Profiles[document.ActiveProfileId], intent);
            if (string.Equals(intent, "clip.card", StringComparison.Ordinal))
                active = ApplyOriginalImageDestinationPolicy(active, active.Capture?.ImageHandling);
            return DesktopTaskSettingsArtifacts.Publish(taskRoot, taskId, active, settings, localStore, DateTimeOffset.UtcNow)
                .Select(item => new DesktopAdditionalInput
                {
                    ArtifactRef = item.Reference.ArtifactRef, Kind = item.Reference.Kind, ContractVersion = item.Reference.ContractVersion,
                    ManifestPath = item.Reference.ManifestPath, ExpectedSha256 = item.Reference.ExpectedSha256,
                    SettingsRevisionId = item.ContentSelection == null ? null : item.ContentSelection.SettingsRevisionId
                }).ToArray();
        }

        public string Publish(string taskRoot, string taskId, string intent, CaptureBundleV1 bundle, string captureArtifactRef, string captureManifestPath, string captureManifestSha256, IReadOnlyList<DesktopAdditionalInput> additionalInputs)
        {
            var document = settings.LoadOrCreate(UserSettings.Defaults());
            var storedActive = document.Profiles[document.ActiveProfileId];
            var active = EffectiveProfile(storedActive, intent);
            active = ApplyOriginalImageDestinationPolicy(active, bundle?.modeSnapshot?.imageHandling);
            if (!ReferenceEquals(active, storedActive))
                document = new WorkflowSettingsDocument(document.SchemaVersion, document.ActiveProfileId,
                    document.Profiles.ToDictionary(item => item.Key, item => item.Key == document.ActiveProfileId ? active : item.Value, StringComparer.Ordinal));
            if (active.Workflow?.Destinations?.Contains("remnote", StringComparer.Ordinal) == true && !settings.RemNoteExporterV2Available())
                throw new InvalidOperationException("settings.remnote-authority-exporter-v2-unavailable");
            var contentInput = additionalInputs.SingleOrDefault(item => item.Kind == "content-config-snapshot");
            var ankiInput = additionalInputs.SingleOrDefault(item => item.Kind == "anki-export-config");
            var destinations = (active.Workflow?.Destinations ?? Array.Empty<string>()).ToArray();
            if (destinations.Contains("anki", StringComparer.Ordinal) && ankiInput == null) throw new InvalidOperationException("settings.anki-conditional-unavailable");
            var frozenAt = DateTimeOffset.UtcNow;
            var modules = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["capture"] = new { contextEnabled = bundle.modeSnapshot.contextEnabled, contextGranularity = bundle.modeSnapshot.contextGranularity, imageHandling = bundle.modeSnapshot.imageHandling },
                ["content"] = BuildContentSnapshot(taskRoot, contentInput)
            };
            if (intent == "clip.read-aloud") modules["speech"] = BuildSpeechSnapshot(active.Speech);
            if (destinations.Contains("remnote", StringComparer.Ordinal)) modules["remnote"] = BuildRemNoteSnapshot(active.Remnote, active.Workflow?.ProcessingProfile, frozenAt);
            if (destinations.Contains("anki", StringComparer.Ordinal)) modules["anki"] = new { configArtifactRef = ankiInput.ArtifactRef, expectedSha256 = ankiInput.ExpectedSha256.ToLowerInvariant(), secretReferences = Array.Empty<string>() };
            if (bundle.modeSnapshot.imageHandling == "ocr") modules["imageOcr"] = new { schemaVersion = active.ImageOcr.SchemaVersion, providerSettings = active.ImageOcr.ProviderSettings, mineruScriptPath = active.ImageOcr.MineruScriptPath };
            var settingsSha = Sha(JsonSerializer.SerializeToUtf8Bytes(document, ContractJson.Options));
            var snapshot = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = "knowledge-clipper-task-settings-snapshot.v3", ["taskId"] = taskId, ["frozenAt"] = frozenAt,
                ["source"] = new { activeProfileId = document.ActiveProfileId, settingsSha256 = settingsSha, precedence = new[] { "taskOverride", "activeProfile", "moduleDefault" } },
                ["workflow"] = new { processingProfile = active.Workflow?.ProcessingProfile ?? "cards", destinations }, ["modules"] = modules,
                ["secretReferences"] = Array.Empty<string>()
            };
            var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, ContractJson.Options);
            var snapshotDigest = Sha(snapshotBytes); var snapshotId = "task-settings-snapshot-" + snapshotDigest.Substring(0, 24);
            var snapshotRoot = "artifacts/" + snapshotId; var snapshotPayload = snapshotRoot + "/task-settings-snapshot.v1.json"; var snapshotManifest = snapshotRoot + "/artifact.json";
            WriteOnce(Path.Combine(taskRoot, snapshotPayload.Replace('/', Path.DirectorySeparatorChar)), snapshotBytes);
            var snapshotInputs = additionalInputs.Where(item => item.Kind == "content-config-snapshot" || item.Kind == "anki-export-config").Select(item => new { artifactRef = item.ArtifactRef, kind = item.Kind, contractVersion = item.ContractVersion, manifestPath = item.ManifestPath, expectedSha256 = item.ExpectedSha256 }).ToArray();
            var snapshotManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0.0", artifactId = snapshotId, kind = "task-settings-snapshot", contractVersion = "1.0.0", mediaType = "application/vnd.knowledge-clipper.task-settings-snapshot+json",
                files = new[] { new { path = snapshotPayload, sha256 = snapshotDigest, bytes = snapshotBytes.LongLength, mediaType = "application/json" } }, createdAt = frozenAt,
                producer = new { stageId = "task-settings", workerId = "desktop.settings-adapter", workerVersion = "1.0.0", attemptId = "settings-" + snapshotId }, inputRefs = snapshotInputs,
                dataClass = "sensitive", metadata = new { entrypoint = snapshotPayload, sourceSettingsSha256 = settingsSha }
            }, ContractJson.Options);
            WriteOnce(Path.Combine(taskRoot, snapshotManifest.Replace('/', Path.DirectorySeparatorChar)), snapshotManifestBytes);
            return AppendCanonicalManifest(taskRoot, taskId, active.Workflow?.ProcessingProfile ?? "cards", destinations, snapshotId, Sha(snapshotManifestBytes));
        }

        private static object BuildContentSnapshot(string taskRoot, DesktopAdditionalInput contentInput)
        {
            if (contentInput == null) return new { promptSet = "open-md-v2", categoryRegistry = "category-registry/2.0.0", providerMode = "live" };
            var manifestPath = Path.Combine(taskRoot, contentInput.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var entrypoint = manifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString();
            using var content = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(taskRoot, entrypoint.Replace('/', Path.DirectorySeparatorChar))));
            var root = content.RootElement; var prompt = root.GetProperty("promptSet"); var category = root.GetProperty("categoryRegistry"); var runtime = root.GetProperty("runtimePolicy"); var provider = runtime.GetProperty("provider");
            return new
            {
                promptSet = prompt.GetProperty("id").GetString(), categoryRegistry = category.GetProperty("id").GetString(), providerMode = "live",
                configArtifactRef = contentInput.ArtifactRef, expectedSha256 = contentInput.ExpectedSha256.ToLowerInvariant(), settingsRevisionId = root.GetProperty("settingsRevisionId").GetString(),
                configSchemaVersion = root.GetProperty("schemaVersion").GetString(), promptSetSha256 = prompt.GetProperty("sha256").GetString(), categoryRegistrySha256 = category.GetProperty("sha256").GetString(),
                runtimePolicy = new { profileId = runtime.GetProperty("profileId").GetString(), profileVersion = runtime.GetProperty("profileVersion").GetString(), languagePracticeDefault = runtime.GetProperty("languagePracticeDefault").GetString(), providerModel = provider.GetProperty("model").GetString(), providerMaxRetries = provider.GetProperty("maxRetries").GetInt32() }
            };
        }

        private static object BuildSpeechSnapshot(SpeechSettings speech)
        {
            speech = speech ?? new SpeechSettings(new Dictionary<string, string>(), 0, 0, 0, null, "auto", null);
            return new { voicesByLanguage = speech.VoicesByLanguage ?? new Dictionary<string, string>(), ratePercent = speech.RatePercent ?? 0, volumePercent = speech.VolumePercent ?? 0, pitchHz = speech.PitchHz ?? 0, autoPlay = true, languageStrategy = speech.LanguageStrategy ?? "auto", languageOverride = speech.LanguageOverride, deployment = speech.Deployment };
        }

        private static object BuildRemNoteSnapshot(RemNoteDestinationSettings remnote, string processingProfile, DateTimeOffset frozenAt)
        {
            if (remnote == null || !remnote.Enabled || string.IsNullOrWhiteSpace(remnote.Target?.RootRemId)) throw new InvalidOperationException("settings.remnote-conditional-unavailable");
            object route = processingProfile == "learning" ? new { kind = "clip-daily-route", timeZone = "Asia/Shanghai", localDate = frozenAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"), dateFormat = "yyyy-MM-dd" } : new { kind = "card-export" };
            return new { settings = new { schemaVersion = "remnote-destination-settings-snapshot.v1", destinationType = "remnote", enabled = true, target = remnote.Target, advanced = remnote.Advanced, secretReferences = Array.Empty<string>() }, route };
        }

        private static string AppendCanonicalManifest(string taskRoot, string taskId, string processingProfile, IReadOnlyList<string> destinations, string snapshotId, string snapshotManifestSha)
        {
            var legacyPath = Path.Combine(taskRoot, "task.manifest.v1.json"); using var legacy = JsonDocument.Parse(File.ReadAllBytes(legacyPath)); var root = legacy.RootElement;
            var inputs = root.GetProperty("inputRefs").EnumerateArray().Select(item => (object)item.Clone()).ToList();
            inputs.Add(new { artifactRef = snapshotId, kind = "task-settings-snapshot", contractVersion = "1.0.0", expectedSha256 = snapshotManifestSha });
            var path = Path.Combine(taskRoot, "task.manifest.v2.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "2.0.0", taskId, createdAt = root.GetProperty("createdAt").GetDateTimeOffset(), createdBy = new { application = "KnowledgeClipper", version = "desktop-application" },
                intent = root.GetProperty("intent").GetString(), executionProfile = processingProfile, destinations, inputRefs = inputs,
                requestedOutputs = root.GetProperty("requestedOutputs").Clone(), initialPlanRef = root.GetProperty("initialPlanRef").GetString(), retentionClass = root.GetProperty("retentionClass").GetString(),
                metadata = new { migratedFrom = "task.manifest.v1.json", settingsSnapshotArtifactRef = snapshotId }
            }, ContractJson.Options);
            WriteOnce(path, bytes); return path;
        }

        private static void WriteOnce(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        private static string Sha(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }

        private static WorkflowSettingsProfile EffectiveProfile(WorkflowSettingsProfile active, string intent)
        {
            if (intent != "clip.question") return active;
            return active with { Workflow = new WorkflowRouteSettings("cards", Array.Empty<string>()), Remnote = null };
        }

        private static WorkflowSettingsProfile ApplyOriginalImageDestinationPolicy(WorkflowSettingsProfile active, string imageHandling)
        {
            if (!string.Equals(imageHandling, "original", StringComparison.Ordinal)) return active;
            return active with
            {
                Workflow = new WorkflowRouteSettings(active.Workflow?.ProcessingProfile ?? "cards", new[] { "anki" }),
                Remnote = null
            };
        }
    }
}

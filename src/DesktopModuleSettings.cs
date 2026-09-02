using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeClipper.Desktop.Application;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal static class ModuleActionProtocolCapabilities
    {
        private static readonly IReadOnlyDictionary<string, int> SupportedVersions = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "1.2.0", 12 },
            { "1.3.0", 13 },
            { "1.4.0", 14 },
            { "1.5.0", 15 },
            { "1.6.0", 16 },
            { "1.7.0", 17 }
        };

        internal static int Parse(string schemaVersion)
        {
            return schemaVersion != null && SupportedVersions.TryGetValue(schemaVersion, out var protocol) ? protocol : 0;
        }

        internal static int Read(string ipcConfigPath)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(ipcConfigPath));
                return Parse(document.RootElement.GetProperty("schemaVersion").GetString());
            }
            catch { return 0; }
        }
    }

    internal sealed class ContentSettingsDescriptorView
    {
        internal string SchemaVersion { get; set; }
        internal string Strategies { get; set; }
        internal string DefaultChoice { get; set; }
        internal string DefaultChoiceStatus { get; set; }
        internal string PromptAfterRecognitionStatus { get; set; }
        internal string[] Models { get; set; } = Array.Empty<string>();
        internal string DefaultModel { get; set; }
        internal int MinimumRetries { get; set; }
        internal int MaximumRetries { get; set; }
        internal int DefaultRetries { get; set; }
        internal string PromptIdentity { get; set; }
        internal string ProfileIdentity { get; set; }
        internal string RestrictedControls { get; set; }
    }

    internal sealed class SpeechSettingsDescriptorView
    {
        private readonly HashSet<string> editableIds;
        internal string SchemaVersion { get; private set; }
        internal string WorkerVersion { get; private set; }
        internal string EffectiveAt { get; private set; }
        internal string VoiceCatalogStatus { get; private set; }
        internal string PreviewStatus { get; private set; }
        internal string StatusText { get { return SchemaVersion + " · speech.edge " + WorkerVersion + " · " + EffectiveAt; } }

        private SpeechSettingsDescriptorView(string schemaVersion, string workerVersion, string effectiveAt, HashSet<string> editableIds, string voiceCatalogStatus, string previewStatus)
        {
            SchemaVersion = schemaVersion; WorkerVersion = workerVersion; EffectiveAt = effectiveAt; this.editableIds = editableIds;
            VoiceCatalogStatus = voiceCatalogStatus; PreviewStatus = previewStatus;
        }

        internal bool Editable(string id) { return editableIds.Contains(id); }

        internal static SpeechSettingsDescriptorView Load(string applicationBaseDirectory)
        {
            var path = Path.Combine(applicationBaseDirectory, "descriptors", "speech-settings-companion.v1.json");
            VerifySha256(path, "31197fb21f8c101e73282496f3be19732bc23245a3ad40be059ffdbc5a4e0f71", "speech-settings-companion-sha256-mismatch");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != "speech-settings-companion-descriptor.v1") throw new InvalidDataException("speech-settings-companion-version-invalid");
            var editable = root.GetProperty("settings").EnumerateArray()
                .Where(item => item.GetProperty("editable").GetBoolean() && item.GetProperty("support").GetString() == "implemented")
                .Select(item => item.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
            var voiceDiscovery = root.GetProperty("conditionalIntegrations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "runtime.voiceDiscovery");
            var preview = root.GetProperty("actions").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "speaker.preview");
            var voiceCatalogStatus = voiceDiscovery.GetProperty("status").GetString() + " · 可用状态由 Core v2.7 公共只读动作与 SHA 绑定目录缓存共同门禁";
            var previewStatus = preview.GetProperty("status").GetString() + " · explicit_only · physical_audio · 仅由独立按钮单次授权；保存和状态检查不播放";
            return new SpeechSettingsDescriptorView(root.GetProperty("schemaVersion").GetString(), root.GetProperty("workerVersion").GetString(), root.GetProperty("scope").GetProperty("effectiveAt").GetString(), editable, voiceCatalogStatus, previewStatus);
        }

        private static void VerifySha256(string path, string expected, string code)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(code, path);
            using var sha = SHA256.Create();
            var actual = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new InvalidDataException(code);
        }
    }

    internal sealed class AnkiProbeCapability
    {
        internal string DescriptorVersion { get; private set; }
        internal string MinimumWorkerVersion { get; private set; }
        internal bool AuthorityAdmitted { get; private set; }
        internal string StatusText { get { return AuthorityAdmitted ? "available · worker-owned loopback read-only probe" : "conditional · Anki 1.2.1 probe 尚未由当前 authority 收录"; } }

        private AnkiProbeCapability(string descriptorVersion, string minimumWorkerVersion, bool authorityAdmitted)
        {
            DescriptorVersion = descriptorVersion; MinimumWorkerVersion = minimumWorkerVersion; AuthorityAdmitted = authorityAdmitted;
        }

        internal static AnkiProbeCapability CreateForTest(bool authorityAdmitted) { return new AnkiProbeCapability("1.1.0", "1.2.1", authorityAdmitted); }

        internal static AnkiProbeCapability Load(string applicationBaseDirectory, string authorityReleaseRoot)
        {
            var packaged = Path.Combine(applicationBaseDirectory, "descriptors", "anki-destination-settings-descriptor.v1.json");
            VerifySha256(packaged, "8beb5a4dbea6ad4d59e49daaa8325d57dc5a661d8ac726856ed7135ec6ad0bc8", "anki-settings-descriptor-sha256-mismatch");
            using var document = JsonDocument.Parse(File.ReadAllBytes(packaged));
            var root = document.RootElement;
            var probe = root.GetProperty("tests").GetProperty("probeConnection");
            if (probe.GetProperty("operation").GetString() != "probe" || probe.GetProperty("effectClass").GetString() != "provider-read-only" || probe.GetProperty("externalMutation").GetBoolean())
                throw new InvalidDataException("anki-probe-descriptor-not-read-only");
            var minimum = root.GetProperty("workerBinding").GetProperty("minimumVersion").GetString();
            var authorityDescriptor = Path.Combine(authorityReleaseRoot, "workers", "anki", "contracts", "anki-destination-settings-descriptor.v1.json");
            var admitted = File.Exists(authorityDescriptor) && string.Equals(Sha256(authorityDescriptor), Sha256(packaged), StringComparison.Ordinal);
            return new AnkiProbeCapability(root.GetProperty("descriptorVersion").GetString(), minimum, admitted);
        }

        private static void VerifySha256(string path, string expected, string code)
        {
            if (!File.Exists(path) || !string.Equals(Sha256(path), expected, StringComparison.Ordinal)) throw new InvalidDataException(code);
        }

        private static string Sha256(string path)
        {
            using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
    }

    // Compatibility name retained for existing automation/reflection consumers. The state itself is
    // the typed Desktop.Application projection and no longer owns an independent parser/state model.
    internal sealed class AnkiProbeDisplayResult : AnkiProbeViewState { }

    internal sealed class AnkiInitializationDisplayResult
    {
        internal string Status { get; set; }
        internal int CreatedModels { get; set; }
        internal int CreatedDecks { get; set; }
        internal int CardWrites { get; set; }
        internal int Deletes { get; set; }
        internal int ExistingResourcesModified { get; set; }
        internal bool UnknownExternalState { get; set; }
        internal string Receipt { get; set; }
        internal string ReasonCode { get; set; }
        internal bool Completed { get { return (Status == "completed" || Status == "initialized" || Status == "already_current") && string.IsNullOrWhiteSpace(ReasonCode) && !UnknownExternalState; } }
        internal string DisplayText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ReasonCode)) return "初始化失败 · reasonCode=" + ReasonCode + " · 不自动重试 · receipt=" + Receipt;
                return (Completed ? "初始化完成" : "状态未知") + " · createdModels=" + CreatedModels + " · createdDecks=" + CreatedDecks + " · cardWrites=" + CardWrites + " · deletes=" + Deletes + " · modifiedExisting=" + ExistingResourcesModified + " · receipt=" + Receipt;
            }
        }
    }

    internal sealed class DesktopModuleActionDisplayResult
    {
        internal string Operation { get; set; }
        internal string Status { get; set; }
        internal string OutputRef { get; set; }
        internal string OutputSha256 { get; set; }
        internal string CatalogSnapshotRef { get; set; }
        internal string CatalogSnapshotSha256 { get; set; }
        internal bool PhysicalAudio { get; set; }
        internal string OutputJson { get; set; }
        internal string CatalogJson { get; set; }
        internal string DisplayText { get { return Status + " · evidence=" + OutputRef; } }
    }

    internal sealed class DesktopSpeechCatalogCacheDocument
    {
        public string SchemaVersion { get; set; } = "knowledge-clipper-desktop-speech-catalog-cache.v1";
        public string CatalogSnapshotRef { get; set; }
        public string CatalogSnapshotSha256 { get; set; }
        public string CatalogRevision { get; set; }
        public string CatalogSha256 { get; set; }
        public string CatalogJson { get; set; }
    }

    internal sealed class DesktopSpeechCatalogCacheStore
    {
        private readonly string path;

        internal DesktopSpeechCatalogCacheStore(string workDirectory)
        {
            path = Path.Combine(Path.GetFullPath(workDirectory), "desktop-speech-catalog-cache.v1.json");
        }

        internal DesktopSpeechCatalogCacheDocument Load()
        {
            if (!File.Exists(path)) return null;
            try { return ReadAndValidate(path); }
            catch
            {
                var backup = path + ".bak";
                if (!File.Exists(backup)) throw;
                return ReadAndValidate(backup);
            }
        }

        internal DesktopSpeechCatalogCacheDocument Save(string snapshotRef, string snapshotSha256, string catalogJson)
        {
            using var catalog = JsonDocument.Parse(catalogJson ?? throw new InvalidDataException("settings.speech-catalog-json-missing"));
            var root = catalog.RootElement;
            var value = new DesktopSpeechCatalogCacheDocument
            {
                CatalogSnapshotRef = snapshotRef,
                CatalogSnapshotSha256 = snapshotSha256,
                CatalogRevision = root.GetProperty("catalogRevision").GetString(),
                CatalogSha256 = root.GetProperty("catalogSha256").GetString(),
                CatalogJson = catalogJson
            };
            Validate(value);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options));
            File.Move(temporary, path, true);
            File.Copy(path, path + ".bak", true);
            return value;
        }

        private static DesktopSpeechCatalogCacheDocument ReadAndValidate(string source)
        {
            var value = JsonSerializer.Deserialize<DesktopSpeechCatalogCacheDocument>(File.ReadAllBytes(source), ContractJson.Options)
                ?? throw new InvalidDataException("settings.speech-catalog-cache-empty");
            Validate(value);
            return value;
        }

        private static void Validate(DesktopSpeechCatalogCacheDocument value)
        {
            if (value.SchemaVersion != "knowledge-clipper-desktop-speech-catalog-cache.v1") throw new InvalidDataException("settings.speech-catalog-cache-version-invalid");
            if (string.IsNullOrWhiteSpace(value.CatalogSnapshotRef) || Path.IsPathRooted(value.CatalogSnapshotRef) || value.CatalogSnapshotRef.Split('/', '\\').Contains("..") || !value.CatalogSnapshotRef.StartsWith("module-state/speech/voice-catalog/", StringComparison.Ordinal))
                throw new InvalidDataException("settings.speech-catalog-cache-ref-invalid");
            if (!Hash(value.CatalogSnapshotSha256) || !Hash(value.CatalogSha256) || string.IsNullOrWhiteSpace(value.CatalogRevision) || string.IsNullOrWhiteSpace(value.CatalogJson))
                throw new InvalidDataException("settings.speech-catalog-cache-identity-invalid");
            var bytes = new UTF8Encoding(false, true).GetBytes(value.CatalogJson);
            if (!string.Equals(Sha(bytes), value.CatalogSnapshotSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("settings.speech-catalog-cache-snapshot-sha256-mismatch");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("catalogRevision").GetString(), value.CatalogRevision, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("catalogSha256").GetString(), value.CatalogSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("settings.speech-catalog-cache-projection-drift");
        }

        private static bool Hash(string value) { return value != null && value.Length == 64 && value.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f'); }
        private static string Sha(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }
    }

    internal sealed class DesktopModuleActionHost
    {
        private readonly string jobsRoot;
        private readonly ICommonIpcClient client;
        private readonly Func<string, object, System.Threading.Tasks.Task<DesktopModuleActionDisplayResult>> injectedRunner;
        private readonly int moduleActionProtocol;
        internal bool CanRun { get { return injectedRunner != null || (client != null && !string.IsNullOrWhiteSpace(jobsRoot)); } }
        internal bool CanDiscoverRemNoteTarget { get { return injectedRunner != null || CanRun && moduleActionProtocol >= 13; } }

        internal DesktopModuleActionHost(string jobsRoot, ICommonIpcClient client, Func<string, object, System.Threading.Tasks.Task<DesktopModuleActionDisplayResult>> injectedRunner = null, bool protocol13Available = false, bool protocol14Available = false)
            : this(jobsRoot, client, injectedRunner, protocol14Available ? 14 : protocol13Available ? 13 : 12) { }

        internal DesktopModuleActionHost(string jobsRoot, ICommonIpcClient client, Func<string, object, System.Threading.Tasks.Task<DesktopModuleActionDisplayResult>> injectedRunner, int moduleActionProtocol)
        {
            this.jobsRoot = string.IsNullOrWhiteSpace(jobsRoot) ? null : Path.GetFullPath(jobsRoot);
            this.client = client;
            this.injectedRunner = injectedRunner;
            this.moduleActionProtocol = injectedRunner != null ? 14 : moduleActionProtocol;
        }

        internal static DesktopModuleActionHost Load(string applicationBaseDirectory)
        {
            try
            {
                var integration = CandidateIntegrationConfig.Load(applicationBaseDirectory);
                if (integration == null) return new DesktopModuleActionHost(null, null);
                return new DesktopModuleActionHost(integration.JobsRoot, new NamedPipeCommonIpcClient(integration.PipeName, 75000), null, ModuleActionProtocolCapabilities.Read(integration.IpcConfig));
            }
            catch { return new DesktopModuleActionHost(null, null); }
        }

        internal System.Threading.Tasks.Task<DesktopModuleActionDisplayResult> RunAsync(string operation, object payload)
        {
            if (!CanRun) throw new InvalidOperationException("settings.module-action-unavailable");
            if (!new[] { "speech.voices", "speech.preview", "remnote.probe", "remnote.discover-target" }.Contains(operation, StringComparer.Ordinal)) throw new InvalidDataException("settings.module-action-operation-invalid");
            if (operation == "remnote.discover-target" && !CanDiscoverRemNoteTarget) throw new InvalidOperationException("settings.remnote-target-discovery-unavailable");
            return injectedRunner != null ? injectedRunner(operation, payload) : System.Threading.Tasks.Task.Run(delegate { return Run(operation, payload); });
        }

        private DesktopModuleActionDisplayResult Run(string operation, object payload)
        {
            Directory.CreateDirectory(jobsRoot);
            var actionId = "settings-" + operation.Replace('.', '-') + "-" + Guid.NewGuid().ToString("N");
            var payloadRef = "module-actions/" + actionId + "/input.json";
            var inputPath = Contained(jobsRoot, payloadRef, false);
            if (moduleActionProtocol >= 17 && operation == "speech.preview") payload = WrapSpeechPreview(actionId, payload);
            var inputBytes = JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options);
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath));
            using (var stream = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { stream.Write(inputBytes, 0, inputBytes.Length); stream.Flush(true); }
            var inputHash = Sha(inputBytes);
            if (moduleActionProtocol >= 17 && new[] { "remnote.probe", "speech.voices", "speech.preview" }.Contains(operation, StringComparer.Ordinal)) return RunV6(actionId, operation, payloadRef, inputHash);
            if (moduleActionProtocol >= 14) return RunV3(actionId, operation, payloadRef, inputHash);
            if (operation == "remnote.discover-target") return RunV2(actionId, operation, payloadRef, inputHash);
            var receipt = client.SendModuleAction(new CommonModuleActionRequestV1
            {
                schemaVersion = "knowledge-clipper-module-action.v1", actionId = actionId, operation = operation,
                payloadRef = payloadRef, idempotencyKey = inputHash
            });
            if (receipt == null || receipt.schemaVersion != "knowledge-clipper-module-action-receipt.v1" || receipt.actionId != actionId || receipt.operation != operation)
                throw new InvalidDataException("settings.module-action-receipt-identity-invalid");
            if (!receipt.accepted)
                throw new InvalidDataException("settings.module-action-rejected:" + (receipt.error == null ? receipt.status : receipt.error.code));
            if (receipt.externalWriteCount != 0 || receipt.automaticRetryAllowed || receipt.inputRef != payloadRef || !EqualHash(receipt.inputSha256, inputHash) || !EqualHash(receipt.idempotencyKey, inputHash))
                throw new InvalidDataException("settings.module-action-boundary-invalid");
            if (receipt.physicalAudio != (operation == "speech.preview")) throw new InvalidDataException("settings.module-action-physical-audio-drift");
            var outputJson = VerifiedText(receipt.outputRef, receipt.outputSha256);
            var catalogJson = string.IsNullOrWhiteSpace(receipt.catalogSnapshotRef) ? null : VerifiedText(receipt.catalogSnapshotRef, receipt.catalogSnapshotSha256);
            return new DesktopModuleActionDisplayResult
            {
                Operation = operation, Status = receipt.status, OutputRef = receipt.outputRef, OutputSha256 = receipt.outputSha256,
                CatalogSnapshotRef = receipt.catalogSnapshotRef, CatalogSnapshotSha256 = receipt.catalogSnapshotSha256,
                PhysicalAudio = receipt.physicalAudio, OutputJson = outputJson, CatalogJson = catalogJson
            };
        }

        private DesktopModuleActionDisplayResult RunV2(string actionId, string operation, string payloadRef, string inputHash)
        {
            var receipt = client.SendModuleActionV2(new CommonModuleActionRequestV2
            {
                schemaVersion = "knowledge-clipper-module-action.v2", actionId = actionId, operation = operation,
                payloadRef = payloadRef, idempotencyKey = inputHash
            });
            if (receipt == null || receipt.schemaVersion != "knowledge-clipper-module-action-receipt.v2" || receipt.actionId != actionId || receipt.operation != operation)
                throw new InvalidDataException("settings.module-action-v2-receipt-identity-invalid");
            if (!receipt.accepted)
                throw new InvalidDataException("settings.module-action-v2-rejected:" + (receipt.error == null ? receipt.status : receipt.error.code));
            if (receipt.externalWriteCount != 0 || receipt.automaticRetryAllowed || receipt.physicalAudio || receipt.inputRef != payloadRef || !EqualHash(receipt.inputSha256, inputHash) || !EqualHash(receipt.idempotencyKey, inputHash))
                throw new InvalidDataException("settings.module-action-v2-boundary-invalid");
            return new DesktopModuleActionDisplayResult
            {
                Operation = operation, Status = receipt.status, OutputRef = receipt.outputRef, OutputSha256 = receipt.outputSha256,
                CatalogSnapshotRef = receipt.catalogSnapshotRef, CatalogSnapshotSha256 = receipt.catalogSnapshotSha256,
                PhysicalAudio = false, OutputJson = VerifiedText(receipt.outputRef, receipt.outputSha256), CatalogJson = null
            };
        }

        private DesktopModuleActionDisplayResult RunV3(string actionId, string operation, string payloadRef, string inputHash)
        {
            var receipt = client.SendModuleActionV3(new CommonModuleActionRequestV3
            {
                schemaVersion = "knowledge-clipper-module-action.v3", actionId = actionId, operation = operation,
                payloadRef = payloadRef, idempotencyKey = inputHash
            });
            if (receipt == null || receipt.schemaVersion != "knowledge-clipper-module-action-receipt.v3" || receipt.actionId != actionId || receipt.operation != operation)
                throw new InvalidDataException("settings.module-action-v3-receipt-identity-invalid");
            if (!receipt.accepted)
                throw new InvalidDataException("settings.module-action-v3-rejected:" + (receipt.error == null ? receipt.status : receipt.error.code));
            var expectedAudio = operation == "speech.preview";
            if (receipt.automaticRetryAllowed || receipt.physicalAudio != expectedAudio || receipt.inputRef != payloadRef || !EqualHash(receipt.inputSha256, inputHash) || !EqualHash(receipt.idempotencyKey, inputHash))
                throw new InvalidDataException("settings.module-action-v3-boundary-invalid");
            if (operation != "anki.initialize-managed" && receipt.externalWriteCount != 0)
                throw new InvalidDataException("settings.module-action-v3-unexpected-write");
            return new DesktopModuleActionDisplayResult
            {
                Operation = operation, Status = receipt.status, OutputRef = receipt.outputRef, OutputSha256 = receipt.outputSha256,
                CatalogSnapshotRef = receipt.catalogSnapshotRef, CatalogSnapshotSha256 = receipt.catalogSnapshotSha256,
                PhysicalAudio = receipt.physicalAudio, OutputJson = VerifiedText(receipt.outputRef, receipt.outputSha256),
                CatalogJson = string.IsNullOrWhiteSpace(receipt.catalogSnapshotRef) ? null : VerifiedText(receipt.catalogSnapshotRef, receipt.catalogSnapshotSha256)
            };
        }

        private DesktopModuleActionDisplayResult RunV6(string actionId, string operation, string payloadRef, string inputHash)
        {
            var receipt = client.SendModuleActionV6(new CommonModuleActionRequestV6 { schemaVersion = "knowledge-clipper-module-action.v6", actionId = actionId, operation = operation, payloadRef = payloadRef, idempotencyKey = inputHash });
            if (receipt == null || receipt.schemaVersion != "knowledge-clipper-module-action-receipt.v6" || receipt.actionId != actionId || receipt.operation != operation)
                throw new InvalidDataException("settings.module-action-v6-receipt-identity-invalid");
            if (!receipt.accepted || receipt.executionStatus != "completed")
                throw new InvalidDataException("settings.module-action-v6-rejected:" + (receipt.error == null ? receipt.status : receipt.error.code));
            var expectedAudio = operation == "speech.preview";
            if (receipt.externalWriteCount != 0 || receipt.automaticRetryAllowed || receipt.physicalAudio != expectedAudio || receipt.inputRef != payloadRef || !EqualHash(receipt.inputSha256, inputHash) || !EqualHash(receipt.idempotencyKey, inputHash) || receipt.outputRefs == null || receipt.outputRefs.Count != 1)
                throw new InvalidDataException("settings.module-action-v6-boundary-invalid");
            var output = receipt.outputRefs[0];
            var expectedKind = operation == "remnote.probe" ? "remnote-public-probe-result" : operation == "speech.voices" ? "speech-voice-discovery-result" : "speech-speaker-preview-result";
            if (output.referenceType != "file" || output.kind != expectedKind || output.rootRef != "jobs-root" || output.path != receipt.outputRef || !EqualHash(output.expectedSha256, receipt.outputSha256))
                throw new InvalidDataException("settings.module-action-v6-output-identity-invalid");
            var outputJson = VerifiedText(output.path, output.expectedSha256);
            return new DesktopModuleActionDisplayResult
            {
                Operation = operation, Status = receipt.status, OutputRef = output.path, OutputSha256 = output.expectedSha256,
                CatalogSnapshotRef = receipt.catalogSnapshotRef, CatalogSnapshotSha256 = receipt.catalogSnapshotSha256,
                PhysicalAudio = receipt.physicalAudio, OutputJson = outputJson, CatalogJson = operation == "speech.voices" ? outputJson : null
            };
        }

        private static object WrapSpeechPreview(string actionId, object workerInput)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(workerInput, ContractJson.Options);
            using var document = JsonDocument.Parse(bytes);
            var language = document.RootElement.GetProperty("previewLanguage").GetString();
            return new
            {
                schemaVersion = "speech-settings-speaker-preview-action.v2", workerInput,
                authorization = new { mode = "explicit-user-action", operation = "speaker.preview", actionId, previewLanguage = language, authorizedAt = DateTimeOffset.UtcNow },
                effectClass = "physical-audio-single-attempt", automaticRetryAllowed = false
            };
        }

        private string VerifiedText(string relative, string expectedSha256)
        {
            var path = Contained(jobsRoot, relative, true);
            var bytes = File.ReadAllBytes(path);
            if (!EqualHash(Sha(bytes), expectedSha256)) throw new InvalidDataException("settings.module-action-output-sha256-mismatch");
            using (JsonDocument.Parse(bytes)) { }
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static string Contained(string root, string relative, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) throw new InvalidDataException("settings.module-action-path-invalid");
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (mustExist && !File.Exists(path))) throw new InvalidDataException("settings.module-action-path-outside-jobs-root");
            return path;
        }

        private static bool EqualHash(string left, string right) { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        private static string Sha(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }
    }

    internal sealed class DesktopAnkiProbeHost
    {
        private readonly DesktopWorkflowSettingsService settings;
        private readonly DesktopModuleSettingsStore localStore;
        private readonly Func<System.Threading.Tasks.Task<AnkiProbeDisplayResult>> injectedRunner;
        private readonly Func<System.Threading.Tasks.Task<AnkiInitializationDisplayResult>> injectedInitializer;
        private readonly string jobsRoot;
        private readonly ICommonIpcClient moduleActionClient;
        private readonly bool protocol14Available;
        private readonly bool protocol15Available;
        internal AnkiProbeCapability Capability { get; private set; }
        internal bool CanRun { get { return Capability != null && Capability.AuthorityAdmitted && (injectedRunner != null || protocol15Available && moduleActionClient != null && !string.IsNullOrWhiteSpace(jobsRoot)); } }
        internal bool CanInitializeManaged { get { return Capability != null && Capability.AuthorityAdmitted && (injectedInitializer != null || protocol14Available && moduleActionClient != null && !string.IsNullOrWhiteSpace(jobsRoot)); } }

        internal DesktopAnkiProbeHost(DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, AnkiProbeCapability capability, Func<System.Threading.Tasks.Task<AnkiProbeDisplayResult>> injectedRunner = null, Func<System.Threading.Tasks.Task<AnkiInitializationDisplayResult>> injectedInitializer = null)
        {
            this.settings = settings; this.localStore = localStore; Capability = capability; this.injectedRunner = injectedRunner; this.injectedInitializer = injectedInitializer;
        }

        internal DesktopAnkiProbeHost(DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, AnkiProbeCapability capability, string jobsRoot, ICommonIpcClient moduleActionClient, bool protocol14Available)
            : this(settings, localStore, capability)
        {
            this.jobsRoot = string.IsNullOrWhiteSpace(jobsRoot) ? null : Path.GetFullPath(jobsRoot);
            this.moduleActionClient = moduleActionClient;
            this.protocol14Available = protocol14Available;
        }

        internal DesktopAnkiProbeHost(DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, AnkiProbeCapability capability, string jobsRoot, ICommonIpcClient moduleActionClient, int moduleActionProtocol)
            : this(settings, localStore, capability)
        {
            this.jobsRoot = string.IsNullOrWhiteSpace(jobsRoot) ? null : Path.GetFullPath(jobsRoot);
            this.moduleActionClient = moduleActionClient;
            protocol14Available = moduleActionProtocol == 14;
            protocol15Available = moduleActionProtocol >= 15;
        }

        internal static DesktopAnkiProbeHost Load(DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore)
        {
            AnkiProbeCapability capability;
            try { capability = AnkiProbeCapability.Load(AppDomain.CurrentDomain.BaseDirectory, settings.AuthorityReleaseRoot); } catch { capability = null; }
            try
            {
                var integration = CandidateIntegrationConfig.Load(AppDomain.CurrentDomain.BaseDirectory);
                var protocol = integration == null ? 0 : ModuleActionProtocolCapabilities.Read(integration.IpcConfig);
                if (integration != null && protocol >= 14)
                    return new DesktopAnkiProbeHost(settings, localStore, capability, integration.JobsRoot, new NamedPipeCommonIpcClient(integration.PipeName, 75000), protocol);
            }
            catch { }
            return new DesktopAnkiProbeHost(settings, localStore, capability);
        }

        internal System.Threading.Tasks.Task<AnkiProbeDisplayResult> RunAsync()
        {
            if (!CanRun) throw new InvalidOperationException("settings.anki-probe-unavailable");
            return injectedRunner != null ? injectedRunner() : RunCoreAsync(null, null);
        }

        internal System.Threading.Tasks.Task<AnkiProbeDisplayResult> RunAsync(string expectedProfile, string endpoint)
        {
            if (!CanRun) throw new InvalidOperationException("settings.anki-probe-unavailable");
            return injectedRunner != null ? injectedRunner() : RunCoreAsync(expectedProfile, endpoint);
        }

        internal System.Threading.Tasks.Task<AnkiInitializationDisplayResult> InitializeManagedAsync()
        {
            if (!CanInitializeManaged) throw new InvalidOperationException("settings.anki-initialize-managed-unavailable");
            return injectedInitializer != null ? injectedInitializer() : System.Threading.Tasks.Task.Run(RunPublicManagedInitialization);
        }

        private AnkiInitializationDisplayResult RunPublicManagedInitialization()
        {
            var actionId = "settings-anki-initialize-managed-" + Guid.NewGuid().ToString("N");
            var actionRoot = Path.Combine(jobsRoot, "module-actions", actionId);
            var packageRoot = Path.Combine(actionRoot, "package");
            var attemptRelative = "runs/run-init/stages/anki-export/attempts/attempt-1";
            var configArtifactId = "anki.config.managed." + Guid.NewGuid().ToString("N");
            var local = localStore.Load();
            if (string.IsNullOrWhiteSpace(local.AnkiExpectedProfile)) throw new InvalidDataException("settings.anki-profile-required");
            if (!Uri.TryCreate(local.AnkiEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1") throw new InvalidDataException("settings.anki-loopback-endpoint-required");
            var template = Path.Combine(settings.AuthorityReleaseRoot, "workers", "anki", "config", "anki-export.v1.example.json");
            var configNode = JsonNode.Parse(File.ReadAllBytes(template)).AsObject();
            configNode["expected_profile"] = local.AnkiExpectedProfile.Trim();
            configNode["endpoint"] = local.AnkiEndpoint.Trim();
            configNode["provision_managed"] = true;
            var configPayloadRelative = "artifacts/" + configArtifactId + "/anki-export.v1.json";
            var configPayloadPath = PackagePath(packageRoot, configPayloadRelative);
            var configPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(configNode, ContractJson.Options);
            WriteOnce(configPayloadPath, configPayloadBytes);
            var configPayloadSha = Sha(configPayloadBytes);
            var createdAt = DateTimeOffset.UtcNow;
            var configManifestRelative = "artifacts/" + configArtifactId + "/artifact.json";
            var configManifestPath = PackagePath(packageRoot, configManifestRelative);
            var configManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0.0", artifactId = configArtifactId, kind = "anki-export-config", contractVersion = "1.0.0", mediaType = "application/vnd.knowledge-clipper.anki-export-config+json",
                files = new[] { new { path = configPayloadRelative, sha256 = configPayloadSha, bytes = configPayloadBytes.LongLength, mediaType = "application/json" } },
                createdAt, producer = new { stageId = "desktop-settings", workerId = "desktop.settings", workerVersion = "1.0.0", attemptId = "settings-1" },
                inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint = configPayloadRelative }
            }, ContractJson.Options);
            WriteOnce(configManifestPath, configManifestBytes);
            var configManifestSha = Sha(configManifestBytes);
            var taskId = "task-" + actionId;
            var taskManifestPath = Path.Combine(packageRoot, "task.manifest.v1.json");
            var taskManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0.0", taskId, createdAt, createdBy = new { application = "KnowledgeClipper", version = "1.0.0" }, intent = "settings.initialize-managed",
                executionProfile = "cards-anki", inputRefs = new[] { new { artifactRef = configArtifactId, kind = "anki-export-config", contractVersion = "1.0.0", expectedSha256 = configManifestSha } },
                requestedOutputs = new[] { "anki-managed-initialization-status" }, initialPlanRef = "plans/initial-plan.json", retentionClass = "evidence"
            }, ContractJson.Options);
            WriteOnce(taskManifestPath, taskManifestBytes);
            var authorization = new
            {
                schemaVersion = "1.0.0", authorizationId = "authorization-" + actionId, mode = "explicit-user-action", operation = "initialize-managed",
                scope = "managed-models-and-decks", taskId, configArtifactId, authorizedAt = createdAt
            };
            var idempotencyKey = Sha(Encoding.UTF8.GetBytes(actionId + "\n" + configManifestSha));
            var requestPath = PackagePath(packageRoot, attemptRelative + "/request.json");
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0.0", requestId = "request-" + actionId, operationId = "operation-" + actionId, taskId, runId = "run-init", stageId = "anki-export", attemptId = "attempt-1",
                workerId = "export.anki", operation = "initialize-managed", attemptPath = attemptRelative,
                inputRefs = new[] { new { artifactRef = configArtifactId, kind = "anki-export-config", contractVersion = "1.0.0", manifestPath = configManifestRelative, expectedSha256 = configManifestSha } },
                outputPath = attemptRelative + "/outputs", resultPath = attemptRelative + "/result.json", progressPath = attemptRelative + "/progress.ndjson",
                cancelSignal = new { schemaVersion = "1.0.0", path = attemptRelative + "/cancel.json", gracePeriodMs = 2000 },
                idempotencyKey, configSnapshot = new { ankiConfigArtifactId = configArtifactId }, payload = new { sideEffectAuthorization = authorization }
            }, ContractJson.Options);
            WriteOnce(requestPath, requestBytes);
            var packageRef = "module-actions/" + actionId + "/package";
            var payloadRef = "module-actions/" + actionId + "/input.json";
            var inputBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "anki-managed-initialization-action.v1", workerId = "export.anki", workerVersion = "1.4.0", packageRootRef = packageRef,
                taskManifestRef = packageRef + "/task.manifest.v1.json", taskManifestSha256 = Sha(taskManifestBytes),
                requestRef = packageRef + "/" + attemptRelative + "/request.json", requestSha256 = Sha(requestBytes),
                configManifestRef = packageRef + "/" + configManifestRelative, configManifestSha256 = configManifestSha,
                configPayloadRef = packageRef + "/" + configPayloadRelative, configPayloadSha256 = configPayloadSha,
                authorization
            }, ContractJson.Options);
            WriteOnce(Path.Combine(actionRoot, "input.json"), inputBytes);
            var receipt = moduleActionClient.SendModuleActionV3(new CommonModuleActionRequestV3
            {
                schemaVersion = "knowledge-clipper-module-action.v3", actionId = actionId, operation = "anki.initialize-managed", payloadRef = payloadRef, idempotencyKey = idempotencyKey
            });
            var receiptRef = "module-actions/" + actionId + "/receipt.json";
            if (receipt == null || receipt.schemaVersion != "knowledge-clipper-module-action-receipt.v3" || receipt.actionId != actionId || receipt.operation != "anki.initialize-managed" ||
                receipt.inputRef != payloadRef || !string.Equals(receipt.inputSha256, Sha(inputBytes), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.idempotencyKey, idempotencyKey, StringComparison.OrdinalIgnoreCase) || receipt.automaticRetryAllowed || receipt.physicalAudio)
                throw new InvalidDataException("settings.anki-initialize-managed-receipt-boundary-invalid");
            if (!receipt.accepted)
                return new AnkiInitializationDisplayResult { Status = receipt.status, UnknownExternalState = receipt.status == "unknown_external_state", ReasonCode = receipt.error == null ? receipt.status : receipt.error.code, Receipt = receiptRef };
            var output = ReadVerifiedActionOutput(receipt.outputRef, receipt.outputSha256);
            var status = output.GetProperty("status").GetString();
            var writes = output.GetProperty("writes");
            if (output.GetProperty("schemaVersion").GetString() != "anki-managed-initialization-status.v1" || status is not ("initialized" or "already_current") ||
                output.GetProperty("automaticRetryAllowed").GetBoolean() || output.GetProperty("notesMediaAndExistingResourcesModified").GetBoolean() ||
                output.GetProperty("forbiddenActionsObserved").GetArrayLength() != 0)
                throw new InvalidDataException("settings.anki-initialize-managed-output-boundary-invalid");
            return new AnkiInitializationDisplayResult
            {
                Status = status, CreatedModels = writes.GetProperty("modelsCreated").GetInt32(), CreatedDecks = writes.GetProperty("decksCreated").GetInt32(),
                CardWrites = 0, Deletes = 0, ExistingResourcesModified = 0, Receipt = receiptRef
            };
        }

        private JsonElement ReadVerifiedActionOutput(string relative, string expectedSha256)
        {
            var path = Path.Combine(jobsRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var fullRoot = Path.GetFullPath(jobsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidDataException("settings.anki-initialize-managed-output-path-invalid");
            var bytes = File.ReadAllBytes(path);
            if (!string.Equals(Sha(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("settings.anki-initialize-managed-output-sha256-mismatch");
            using var document = JsonDocument.Parse(bytes); return document.RootElement.Clone();
        }

        private static string PackagePath(string root, string relative)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("settings.anki-initialize-managed-package-path-invalid");
            return path;
        }

        private static void WriteOnce(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }

        private static string Sha(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }

        private System.Threading.Tasks.Task<AnkiProbeDisplayResult> RunCoreAsync(string expectedProfile, string endpoint)
        {
            return System.Threading.Tasks.Task.Run(delegate
            {
                if (!protocol15Available || moduleActionClient == null || string.IsNullOrWhiteSpace(jobsRoot)) throw new InvalidOperationException("settings.anki-probe-ipc-v1.5-unavailable");
                var actionId = "settings-anki-probe-" + Guid.NewGuid().ToString("N");
                var actionRoot = Path.Combine(jobsRoot, "module-actions", actionId);
                var packageRoot = Path.Combine(actionRoot, "package");
                Directory.CreateDirectory(packageRoot);
                var taskId = "task-" + actionId;
                var configReference = DesktopTaskSettingsArtifacts.PublishAnkiProbeConfig(packageRoot, taskId, settings, localStore, DateTimeOffset.UtcNow, expectedProfile, endpoint);
                PublishProbeTaskManifest(packageRoot, taskId, configReference);
                var packageRootRef = "module-actions/" + actionId + "/package";
                var payloadRef = "module-actions/" + actionId + "/input.json";
                var inputBytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = "anki-readonly-probe-action.v1",
                    taskId,
                    packageRootRef,
                    configRef = configReference,
                    effectClass = "provider-read-only",
                    externalMutation = false
                }, ContractJson.Options);
                WriteOnce(Path.Combine(actionRoot, "input.json"), inputBytes);
                var inputHash = Sha(inputBytes);
                var receipt = moduleActionClient.SendModuleActionV4(new CommonModuleActionRequestV4
                {
                    schemaVersion = "knowledge-clipper-module-action.v4", actionId = actionId, operation = "anki.probe", payloadRef = payloadRef, idempotencyKey = inputHash
                });
                var receiptRef = "module-actions/" + actionId + "/receipt.json";
                if (receipt == null || receipt.schemaVersion != "knowledge-clipper-module-action-receipt.v4" || receipt.actionId != actionId || receipt.operation != "anki.probe" ||
                    receipt.inputRef != payloadRef || !string.Equals(receipt.inputSha256, inputHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(receipt.idempotencyKey, inputHash, StringComparison.OrdinalIgnoreCase) || receipt.automaticRetryAllowed || receipt.physicalAudio || receipt.externalWriteCount != 0)
                    throw new InvalidDataException("settings.anki-probe-v4-receipt-boundary-invalid");
                if (!receipt.accepted || receipt.executionStatus != "completed")
                    return new AnkiProbeDisplayResult { Status = receipt.status, ReasonCode = receipt.error == null ? receipt.executionStatus : receipt.error.code, Step = "core-module-action", Receipt = receiptRef };
                if (receipt.outputRefs == null || receipt.outputRefs.Count != 1 || receipt.outputRefs[0].referenceType != "artifact" || receipt.outputRefs[0].kind != "anki-connection-status" ||
                    receipt.outputRefs[0].rootRef != receipt.artifactRootRef || receipt.artifactRootRef != packageRootRef)
                    throw new InvalidDataException("settings.anki-probe-v4-output-reference-invalid");
                var workerResult = ReadVerifiedActionOutput(receipt.outputRef, receipt.outputSha256);
                if (workerResult.GetProperty("schemaVersion").GetString() != "1.0.0" || workerResult.GetProperty("status").GetString() != "completed" ||
                    workerResult.GetProperty("taskId").GetString() != receipt.taskId || workerResult.GetProperty("runId").GetString() != receipt.runId)
                    throw new InvalidDataException("settings.anki-probe-v4-worker-result-invalid");
                var output = receipt.outputRefs[0];
                var verified = new VerifiedArtifactReader(packageRoot).ReadBoundOutput(output.path, output.expectedSha256, output.kind, output.contractVersion, "anki-connection-status.v1");
                var manifestArtifactId = VerifiedArtifactReader.Value(verified.Manifest, "artifactId");
                var display = ReadVerifiedStatus(packageRoot, new DesktopArtifactReference(manifestArtifactId, output.kind, output.contractVersion, output.path, output.expectedSha256));
                display.Status = receipt.status;
                display.Receipt = receiptRef;
                return display;
            });
        }

        internal static void PublishProbeTaskManifest(string jobRoot, string taskId, ArtifactReference configReference)
        {
            if (configReference == null) throw new ArgumentNullException("configReference");
            var planPath = Path.Combine(jobRoot, "plans", "anki-probe.v1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(planPath));
            WriteJsonOnce(planPath, new
            {
                schemaVersion = "desktop-module-action-plan.v1",
                taskId,
                intent = "settings.anki.probe-readonly",
                operation = "probe",
                effectClass = "provider-read-only",
                inputRefs = new[] { configReference },
                requestedOutputs = new[] { "anki-connection-status" }
            });
            var initialConfig = new
            {
                artifactRef = configReference.ArtifactRef,
                kind = configReference.Kind,
                contractVersion = configReference.ContractVersion,
                expectedSha256 = configReference.ExpectedSha256
            };
            var createdAt = DateTimeOffset.UtcNow;
            WriteJsonOnce(Path.Combine(jobRoot, "task.manifest.v1.json"), new
            {
                schemaVersion = "1.0.0",
                taskId,
                createdAt,
                createdBy = new { application = "KnowledgeClipper", version = "1.0.0" },
                intent = "clip.card",
                executionProfile = "cards-anki",
                inputRefs = new[] { initialConfig },
                requestedOutputs = new[] { "anki-connection-status" },
                initialPlanRef = "plans/anki-probe.v1.json",
                retentionClass = "evidence",
                metadata = new { purpose = "settings.anki.probe-readonly", effectClass = "provider-read-only", externalMutation = false }
            });
            WriteJsonOnce(Path.Combine(jobRoot, "task.manifest.v2.json"), new
            {
                schemaVersion = "2.0.0",
                taskId,
                createdAt,
                createdBy = new { application = "KnowledgeClipper", version = "1.0.0" },
                intent = "clip.card",
                executionProfile = "cards",
                destinations = new[] { "anki" },
                inputRefs = new[] { initialConfig },
                requestedOutputs = new[] { "anki-connection-status" },
                initialPlanRef = "plans/anki-probe.v1.json",
                retentionClass = "evidence",
                metadata = new { purpose = "settings.anki.probe-readonly", effectClass = "provider-read-only", externalMutation = false }
            });
        }

        private static void WriteJsonOnce(string path, object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        internal static AnkiProbeDisplayResult ReadVerifiedStatus(string jobRoot, DesktopArtifactReference reference)
        {
            var state = AnkiProbeProjection.FromArtifact(jobRoot, reference);
            return new AnkiProbeDisplayResult
            {
                Status = state.Status,
                ApiVersion = state.ApiVersion,
                ModelsReady = state.ModelsReady,
                ModelsTotal = state.ModelsTotal,
                DecksReady = state.DecksReady,
                DecksTotal = state.DecksTotal,
                MissingModels = state.MissingModels,
                MissingDecks = state.MissingDecks,
                MigrationModels = state.MigrationModels,
                InitializationWouldWrite = state.InitializationWouldWrite,
                MigrationRequired = state.MigrationRequired
            };
        }

        private static string ReadLastProbeStep(string jobRoot, string runId)
        {
            var paths = Directory.GetFiles(Path.Combine(jobRoot, "runs", runId), "operations.ndjson", SearchOption.AllDirectories);
            if (paths.Length == 0) return "worker-start";
            var lines = File.ReadAllLines(paths[0]).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            if (lines.Length == 0) return "worker-start";
            try
            {
                using var document = JsonDocument.Parse(lines[lines.Length - 1]);
                return document.RootElement.TryGetProperty("action", out var action) ? action.GetString() ?? "unknown" : "unknown";
            }
            catch { return "unknown"; }
        }

        private static string Contained(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) throw new InvalidDataException("settings.anki-probe-path-invalid");
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidDataException("settings.anki-probe-path-outside-job");
            return full;
        }

        private static string Sha256(string path)
        {
            using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
    }

    internal sealed class DesktopModuleCapabilityHost
    {
        private readonly Func<ModuleCapabilityAction, string> runner;
        internal ModuleCapabilityDescriptor ImageDescriptor { get; private set; }

        internal DesktopModuleCapabilityHost(ModuleCapabilityDescriptor imageDescriptor, Func<ModuleCapabilityAction, string> runner = null)
        {
            ImageDescriptor = imageDescriptor;
            this.runner = runner;
        }

        internal static DesktopModuleCapabilityHost Load(string authorityReleaseRoot)
        {
            var path = Path.Combine(authorityReleaseRoot, "workers", "image-ocr", "image-ocr-capability-config-descriptor.v5.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "descriptors", "image-ocr-capability-config-descriptor.v5.json");
                if (File.Exists(path) && Sha256File(path) != "7d6d0e2d0e2438efe76d2f1857524b125236785a120287286522b9b6d42dcc08") throw new InvalidDataException("image-ocr-capability-descriptor-v5-sha256-mismatch");
            }
            if (!File.Exists(path))
            {
                path = Path.Combine(authorityReleaseRoot, "workers", "image-ocr", "image-ocr-capability-config-descriptor.v4.json");
                if (!File.Exists(path)) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "descriptors", "image-ocr-capability-config-descriptor.v4.json");
                if (File.Exists(path) && Sha256File(path) != "6dafc948d563a9991c640bc7b674015ff88cc5a70f6550e90456b7b00b0ebe7c") throw new InvalidDataException("image-ocr-capability-descriptor-v4-sha256-mismatch");
            }
            return new DesktopModuleCapabilityHost(ModuleCapabilityDescriptorLoader.TryLoad(path));
        }

        internal ModuleCapabilityAction OfflineImageAction
        {
            get { return Action("image.ocr.status.offline-readiness"); }
        }

        internal ModuleCapabilityAction ValidateImageAction { get { return Action("image.ocr.test.validate-only"); } }
        internal ModuleCapabilityAction FakeImageAction { get { return Action("image.ocr.test.offline-fake-provider"); } }

        internal ModuleCapabilityAction LiveImageAction
        {
            get { return Action("image.ocr.test.live-connectivity"); }
        }

        internal bool Supports(string capabilityId) { return ImageDescriptor != null && ImageDescriptor.AvailableCapabilityIds.Contains(capabilityId); }

        private ModuleCapabilityAction Action(string actionId) { return ImageDescriptor == null ? null : ImageDescriptor.Actions.FirstOrDefault(item => item.ActionId == actionId); }

        internal bool CanRun(ModuleCapabilityAction action) { return action != null && runner != null; }

        internal string Run(ModuleCapabilityAction action)
        {
            if (!CanRun(action)) throw new InvalidOperationException("module-capability-action-runner-unavailable");
            return runner(action);
        }

        internal string ImageDescriptorStatus
        {
            get
            {
                if (ImageDescriptor == null) return "conditional · authority 尚无 additive 静态 descriptor / test action";
                return ImageDescriptor.SchemaVersion + " · actions=" + ImageDescriptor.Actions.Count;
            }
        }

        private static string Sha256File(string path) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant(); }
    }

    internal sealed class DesktopModuleSettingsDocument
    {
        public string SchemaVersion { get; set; } = "knowledge-clipper-desktop-module-settings.v1";
        public string ContentDraftRoot { get; set; }
        public string ContentLanguagePracticeDefault { get; set; } = "ask";
        public string ContentProviderModel { get; set; } = "deepseek-chat";
        public int ContentProviderMaxRetries { get; set; } = 3;
        public string AnkiExpectedProfile { get; set; } = "账户 1";
        public string AnkiEndpoint { get; set; } = "http://127.0.0.1:8765";
    }

    internal sealed class DesktopModuleSettingsStore
    {
        private readonly string path;
        internal string PathValue { get { return path; } }
        internal DesktopModuleSettingsStore(string workDirectory) { path = Path.Combine(Path.GetFullPath(workDirectory), "desktop-module-settings.v1.json"); }

        internal DesktopModuleSettingsDocument Load()
        {
            try
            {
                if (!File.Exists(path)) return ApplyProviderOverrides(new DesktopModuleSettingsDocument());
                var value = JsonSerializer.Deserialize<DesktopModuleSettingsDocument>(File.ReadAllBytes(path), ContractJson.Options);
                if (value == null || value.SchemaVersion != "knowledge-clipper-desktop-module-settings.v1") throw new InvalidDataException("desktop-module-settings.version-invalid");
                return ApplyProviderOverrides(value);
            }
            catch
            {
                var backup = path + ".bak";
                if (!File.Exists(backup)) throw;
                return ApplyProviderOverrides(JsonSerializer.Deserialize<DesktopModuleSettingsDocument>(File.ReadAllBytes(backup), ContractJson.Options) ?? throw new InvalidDataException("desktop-module-settings.backup-empty"));
            }
        }

        private static DesktopModuleSettingsDocument ApplyProviderOverrides(DesktopModuleSettingsDocument value)
        {
            value.AnkiEndpoint = LoopbackProviderOverrides.ResolveAnkiEndpoint(value.AnkiEndpoint);
            return value;
        }

        internal void Save(DesktopModuleSettingsDocument value)
        {
            if (value == null || value.SchemaVersion != "knowledge-clipper-desktop-module-settings.v1") throw new InvalidDataException("desktop-module-settings.version-invalid");
            if (!new[] { "ask", "meaning-reading", "writing", "both" }.Contains(value.ContentLanguagePracticeDefault, StringComparer.Ordinal)) throw new InvalidDataException("settings.content-language-practice-default-invalid");
            if (!new[] { "deepseek-chat", "deepseek-reasoner" }.Contains(value.ContentProviderModel, StringComparer.Ordinal)) throw new InvalidDataException("settings.content-provider-model-invalid");
            if (value.ContentProviderMaxRetries < 1 || value.ContentProviderMaxRetries > 5) throw new InvalidDataException("settings.content-provider-max-retries-invalid");
            if (string.IsNullOrWhiteSpace(value.AnkiExpectedProfile)) throw new InvalidDataException("settings.anki-profile-required");
            if (!Uri.TryCreate(value.AnkiEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Host != "127.0.0.1" || endpoint.Scheme != "http") throw new InvalidDataException("settings.anki-loopback-endpoint-required");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options));
            File.Move(temporary, path, true);
            File.Copy(path, path + ".bak", true);
        }
    }

    internal sealed class DesktopContentSettingsHost
    {
        private readonly DesktopWorkflowSettingsService settings;
        private readonly DesktopModuleSettingsStore localStore;
        private readonly string jobsRoot;
        private readonly ICommonIpcClient client;
        private readonly bool protocol16Available;
        internal DesktopContentSettingsHost(DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, string jobsRoot = null, ICommonIpcClient client = null, bool protocol16Available = false)
        {
            this.settings = settings; this.localStore = localStore; this.jobsRoot = string.IsNullOrWhiteSpace(jobsRoot) ? null : Path.GetFullPath(jobsRoot); this.client = client; this.protocol16Available = protocol16Available;
        }

        internal static DesktopContentSettingsHost Load(DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, string applicationBaseDirectory)
        {
            try
            {
                var integration = CandidateIntegrationConfig.Load(applicationBaseDirectory);
                if (integration == null) return new DesktopContentSettingsHost(settings, localStore);
                using var config = JsonDocument.Parse(File.ReadAllBytes(integration.IpcConfig));
                var available = config.RootElement.GetProperty("schemaVersion").GetString() == "1.6.0";
                return new DesktopContentSettingsHost(settings, localStore, integration.JobsRoot, available ? new NamedPipeCommonIpcClient(integration.PipeName, 75000) : null, available);
            }
            catch { return new DesktopContentSettingsHost(settings, localStore); }
        }

        internal bool Available { get { return protocol16Available && client != null && !string.IsNullOrWhiteSpace(jobsRoot); } }

        internal string CopyBuiltIn()
        {
            var local = localStore.Load();
            if (!string.IsNullOrWhiteSpace(local.ContentDraftRoot) && Directory.Exists(local.ContentDraftRoot)) throw new IOException("settings.content-user-copy-already-exists");
            var root = Path.Combine(Path.GetDirectoryName(localStore.PathValue), "content-settings", "user-copy-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
            using var result = Run("content.settings.copy", new
            {
                schemaVersion = "content-settings-action.v1", action = "content.settings.copy", targetRoot = root,
                effectClass = "local-filesystem-create-once", externalMutation = true,
                authorization = new { mode = "explicit-user-action", operation = "copy", targetRoot = root, createOnly = true }
            }, "content-settings-copy-result");
            var projectedRoot = result.RootElement.GetProperty("targetRoot").GetString();
            if (!string.Equals(Path.GetFullPath(projectedRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root)) throw new InvalidDataException("settings.content-copy-root-drift");
            SaveValidation(result.RootElement.GetProperty("validation"));
            local.ContentDraftRoot = root;
            localStore.Save(local);
            return root;
        }

        internal JsonDocument Validate()
        {
            var root = RequireDraftRoot();
            var document = Run("content.settings.validate", new { schemaVersion = "content-settings-action.v1", action = "content.settings.validate", root, effectClass = "local-filesystem-read-only", externalMutation = false }, "content-settings-validation");
            if (!document.RootElement.GetProperty("valid").GetBoolean()) throw new InvalidDataException("settings.content-validation-failed");
            SaveValidation(document.RootElement);
            return document;
        }

        internal JsonDocument Diff()
        {
            var root = RequireDraftRoot();
            return Run("content.settings.diff", new { schemaVersion = "content-settings-action.v1", action = "content.settings.diff", root, effectClass = "local-filesystem-read-only", externalMutation = false }, "content-settings-diff");
        }
        internal ContentSettingsDescriptorView Describe()
        {
            var companionPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "descriptors", "content-companion-descriptor.v1.json");
            if (File.Exists(companionPath)) return DescribeCompanion(companionPath);
            using var document = Run("content.settings.describe", new { schemaVersion = "content-settings-action.v1", action = "content.settings.describe", effectClass = "local-filesystem-read-only", externalMutation = false }, "content-settings-description");
            var root = document.RootElement;
            var cards = root.GetProperty("processing").GetProperty("cards").GetProperty("languagePracticeMode");
            var strategies = string.Join("；", cards.GetProperty("values").EnumerateArray().Select(item => item.GetProperty("label").GetString() + "（" + item.GetProperty("id").GetString() + "）"));
            var prompt = root.GetProperty("resources").GetProperty("promptSet");
            var profile = root.GetProperty("resources").GetProperty("categoryRegistry");
            var provider = root.GetProperty("provider");
            return new ContentSettingsDescriptorView
            {
                SchemaVersion = root.GetProperty("schemaVersion").GetString(),
                Strategies = strategies,
                DefaultChoice = "ask",
                DefaultChoiceStatus = "conditional-core-adapter",
                PromptAfterRecognitionStatus = "conditional-core-adapter",
                Models = new[] { "deepseek-chat", "deepseek-reasoner" },
                DefaultModel = "deepseek-chat",
                MinimumRetries = 1,
                MaximumRetries = 5,
                DefaultRetries = 3,
                PromptIdentity = prompt.GetProperty("id").GetString() + " · " + prompt.GetProperty("status").GetString() + " · sha256=" + prompt.GetProperty("sha256").GetString(),
                ProfileIdentity = profile.GetProperty("id").GetString() + " · " + profile.GetProperty("status").GetString() + " · sha256=" + profile.GetProperty("sha256").GetString(),
                RestrictedControls = "model=" + provider.GetProperty("model").GetProperty("status").GetString() + "；baseUrl=" + provider.GetProperty("baseUrl").GetProperty("status").GetString() + "；maxRetries=" + provider.GetProperty("maxRetries").GetProperty("status").GetString() + "；Core adapter 未映射，当前只读"
            };
        }

        private static ContentSettingsDescriptorView DescribeCompanion(string path)
        {
            const string expected = "fc3104eef1f0fd326308ae6750ec4d3700205c99395016c620cb0b6b0ad39235";
            using (var sha = SHA256.Create())
            {
                var actual = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new InvalidDataException("content-companion-descriptor-sha256-mismatch");
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != "content-companion-descriptor/1.0.0") throw new InvalidDataException("content-companion-descriptor-version-invalid");
            var practice = root.GetProperty("languagePractice");
            var choice = practice.GetProperty("defaultChoice");
            var promptAfter = practice.GetProperty("promptAfterRecognition");
            var strategies = string.Join("；", practice.GetProperty("choices").EnumerateArray().Select(item => item.GetProperty("label").GetString() + "（" + item.GetProperty("id").GetString() + "）"));
            var prompt = root.GetProperty("promptProfile");
            var provider = root.GetProperty("providerParameters");
            var model = provider.GetProperty("model");
            var retries = provider.GetProperty("maxRetries");
            return new ContentSettingsDescriptorView
            {
                SchemaVersion = root.GetProperty("schemaVersion").GetString(), Strategies = strategies,
                DefaultChoice = choice.GetProperty("default").GetString(), DefaultChoiceStatus = choice.GetProperty("status").GetString(),
                PromptAfterRecognitionStatus = promptAfter.GetProperty("status").GetString(),
                Models = model.GetProperty("values").EnumerateArray().Select(item => item.GetString()).ToArray(), DefaultModel = model.GetProperty("default").GetString(),
                MinimumRetries = retries.GetProperty("minimum").GetInt32(), MaximumRetries = retries.GetProperty("maximum").GetInt32(), DefaultRetries = retries.GetProperty("default").GetInt32(),
                PromptIdentity = prompt.GetProperty("id").GetString() + "@" + prompt.GetProperty("version").GetString() + " · " + prompt.GetProperty("status").GetString() + " · sha256=" + prompt.GetProperty("sha256").GetString(),
                ProfileIdentity = prompt.GetProperty("categoryRegistry").GetProperty("id").GetString() + " · sha256=" + prompt.GetProperty("categoryRegistry").GetProperty("sha256").GetString(),
                RestrictedControls = "defaultChoice=" + choice.GetProperty("status").GetString() + "；model=" + model.GetProperty("status").GetString() + "；maxRetries=" + retries.GetProperty("status").GetString() + "；由 Core adapter v3 双门禁"
            };
        }
        internal string DraftRoot { get { return localStore.Load().ContentDraftRoot; } }

        internal JsonDocument ReadCachedValidation()
        {
            var path = ValidationCachePath();
            if (!File.Exists(path)) throw new FileNotFoundException("settings.content-validation-cache-missing", path);
            var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var draft = RequireDraftRoot();
            if (!root.GetProperty("valid").GetBoolean() || !string.Equals(Path.GetFullPath(root.GetProperty("draftRoot").GetString()), draft, StringComparison.OrdinalIgnoreCase))
            {
                document.Dispose(); throw new InvalidDataException("settings.content-validation-cache-invalid");
            }
            return document;
        }

        private string RequireDraftRoot()
        {
            var root = localStore.Load().ContentDraftRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new DirectoryNotFoundException("settings.content-user-copy-missing");
            return Path.GetFullPath(root);
        }

        private void SaveValidation(JsonElement validation)
        {
            if (!validation.GetProperty("valid").GetBoolean()) throw new InvalidDataException("settings.content-validation-failed");
            var bytes = new UTF8Encoding(false).GetBytes(validation.GetRawText());
            var path = ValidationCachePath(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true);
        }

        private string ValidationCachePath() { return Path.Combine(Path.GetDirectoryName(localStore.PathValue), "content-settings-validation.v1.json"); }

        private JsonDocument Run(string operation, object payload, string expectedKind)
        {
            if (!Available) throw new InvalidOperationException("settings.content-core-ipc-v1.6-unavailable");
            Directory.CreateDirectory(jobsRoot);
            var actionId = "content-settings-" + operation.Substring("content.settings.".Length).Replace('.', '-') + "-" + Guid.NewGuid().ToString("N");
            var payloadRef = "module-actions/" + actionId + "/input.json";
            var inputPath = Contained(jobsRoot, payloadRef, false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options);
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath));
            using (var stream = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            var hash = Sha(bytes);
            var receipt = client.SendModuleActionV5(new CommonModuleActionRequestV5 { schemaVersion = "knowledge-clipper-module-action.v5", actionId = actionId, operation = operation, payloadRef = payloadRef, idempotencyKey = hash });
            if (receipt == null || receipt.schemaVersion != "knowledge-clipper-module-action-receipt.v5" || receipt.actionId != actionId || receipt.operation != operation || !receipt.accepted || receipt.status != "completed" || receipt.executionStatus != "completed")
                throw new InvalidDataException("settings.content-module-action-rejected:" + (receipt == null || receipt.error == null ? "invalid-receipt" : receipt.error.code));
            if (receipt.externalWriteCount != 0 || receipt.physicalAudio || receipt.automaticRetryAllowed || receipt.inputRef != payloadRef || !EqualHash(receipt.inputSha256, hash) || !EqualHash(receipt.idempotencyKey, hash) || receipt.outputRefs == null || receipt.outputRefs.Count != 1)
                throw new InvalidDataException("settings.content-module-action-boundary-invalid");
            var output = receipt.outputRefs[0];
            if (output.referenceType != "file" || output.kind != expectedKind || output.contractVersion != "1.0.0" || output.rootRef != "jobs-root" || output.path != receipt.outputRef || !EqualHash(output.expectedSha256, receipt.outputSha256))
                throw new InvalidDataException("settings.content-module-action-output-identity-invalid");
            var outputPath = Contained(jobsRoot, output.path, true); var outputBytes = File.ReadAllBytes(outputPath);
            if (!EqualHash(Sha(outputBytes), output.expectedSha256)) throw new InvalidDataException("settings.content-module-action-output-sha256-mismatch");
            return JsonDocument.Parse(outputBytes);
        }

        private static string Contained(string root, string relative, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) throw new InvalidDataException("settings.content-module-action-path-invalid");
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || mustExist && !File.Exists(path)) throw new InvalidDataException("settings.content-module-action-path-outside-jobs-root");
            return path;
        }
        private static bool EqualHash(string left, string right) { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        private static string Sha(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }
    }

    internal sealed class DesktopPublishedTaskInput
    {
        internal ArtifactReference Reference { get; set; }
        internal ContentSettingsReference ContentSelection { get; set; }
    }

    internal static class DesktopTaskSettingsArtifacts
    {
        internal static IReadOnlyList<DesktopPublishedTaskInput> Publish(string taskRoot, string taskId, WorkflowSettingsProfile active, DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, DateTimeOffset createdAt)
        {
            var results = new List<DesktopPublishedTaskInput>();
            var local = localStore.Load();
            if (!string.IsNullOrWhiteSpace(local.ContentDraftRoot)) results.Add(PublishContent(taskRoot, taskId, settings, localStore, createdAt));
            if (active.Workflow?.Destinations?.Contains("anki", StringComparer.Ordinal) == true) results.Add(PublishAnki(taskRoot, taskId, settings, local, createdAt));
            return results;
        }

        internal static ArtifactReference PublishAnkiProbeConfig(string taskRoot, string taskId, DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, DateTimeOffset createdAt, string expectedProfile = null, string endpoint = null)
        {
            var local = localStore.Load();
            if (!string.IsNullOrWhiteSpace(expectedProfile)) local.AnkiExpectedProfile = expectedProfile.Trim();
            if (!string.IsNullOrWhiteSpace(endpoint)) local.AnkiEndpoint = endpoint.Trim();
            if (string.IsNullOrWhiteSpace(local.AnkiExpectedProfile)) throw new InvalidDataException("settings.anki-profile-required");
            if (!Uri.TryCreate(local.AnkiEndpoint, UriKind.Absolute, out var parsed) || parsed.Host != "127.0.0.1" || parsed.Scheme != "http") throw new InvalidDataException("settings.anki-loopback-endpoint-required");
            return PublishAnki(taskRoot, taskId, settings, local, createdAt).Reference;
        }

        private static DesktopPublishedTaskInput PublishContent(string taskRoot, string taskId, DesktopWorkflowSettingsService settings, DesktopModuleSettingsStore localStore, DateTimeOffset createdAt)
        {
            using var validation = new DesktopContentSettingsHost(settings, localStore).ReadCachedValidation();
            var root = validation.RootElement;
            var local = localStore.Load();
            var runtimePolicy = new
            {
                profileId = "desktop-user-content",
                profileVersion = "1.0.0",
                languagePracticeDefault = local.ContentLanguagePracticeDefault,
                provider = new { model = local.ContentProviderModel, maxRetries = local.ContentProviderMaxRetries }
            };
            var policyBytes = JsonSerializer.SerializeToUtf8Bytes(runtimePolicy, ContractJson.Options);
            var revision = Sha(Encoding.UTF8.GetBytes(root.GetProperty("settingsRevisionCandidate").GetString() + "\n" + Sha(policyBytes)));
            var artifactId = "content.config." + revision;
            var relativeRoot = "artifacts/" + artifactId;
            var draftRoot = local.ContentDraftRoot;
            var files = new List<object>();
            var promptFiles = new List<object>();
            foreach (var file in root.GetProperty("promptSet").GetProperty("files").EnumerateArray())
            {
                var path = file.GetProperty("path").GetString().Replace('\\', '/');
                var source = Path.Combine(draftRoot, "prompts", path.Replace('/', Path.DirectorySeparatorChar));
                var packagePath = relativeRoot + "/prompts/" + path;
                CopyOnce(source, Path.Combine(taskRoot, packagePath.Replace('/', Path.DirectorySeparatorChar)));
                promptFiles.Add(new { path, packagePath, sha256 = file.GetProperty("sha256").GetString(), bytes = file.GetProperty("bytes").GetInt64() });
                files.Add(new { path = packagePath, sha256 = file.GetProperty("sha256").GetString(), bytes = file.GetProperty("bytes").GetInt64(), mediaType = "text/markdown" });
            }
            var registry = root.GetProperty("categoryRegistry");
            var registryPath = relativeRoot + "/config/categories.v2.json";
            CopyOnce(Path.Combine(draftRoot, "config", "categories.v2.json"), Path.Combine(taskRoot, registryPath.Replace('/', Path.DirectorySeparatorChar)));
            files.Add(new { path = registryPath, sha256 = registry.GetProperty("sha256").GetString(), bytes = registry.GetProperty("bytes").GetInt64(), mediaType = "application/json" });
            var snapshotPath = relativeRoot + "/content-config-snapshot.v1.1.json";
            var snapshot = new
            {
                schemaVersion = "content-config-snapshot/1.1.0", settingsRevisionId = revision,
                workerCompatibility = new { workerId = "content.deepseek", minimumVersion = "0.7.0" },
                promptSet = new { id = "user-" + revision, source = "user-copy", promptRootPath = relativeRoot + "/prompts", sha256 = root.GetProperty("promptSet").GetProperty("sha256").GetString(), files = promptFiles },
                categoryRegistry = new { id = "category-registry/2.0.0", schemaVersion = "category-registry/2.0.0", packagePath = registryPath, sha256 = registry.GetProperty("sha256").GetString(), bytes = registry.GetProperty("bytes").GetInt64() },
                providerBoundary = new { provider = "deepseek", credentials = "process-injected-only" },
                runtimePolicy
            };
            var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, ContractJson.Options);
            WriteOnce(Path.Combine(taskRoot, snapshotPath.Replace('/', Path.DirectorySeparatorChar)), snapshotBytes);
            files.Insert(0, new { path = snapshotPath, sha256 = Sha(snapshotBytes), bytes = snapshotBytes.LongLength, mediaType = "application/json" });
            var manifestPath = relativeRoot + "/artifact.json";
            var manifest = ArtifactManifest(artifactId, "content-config-snapshot", "application/vnd.knowledge-clipper.content-config-snapshot+json", files, snapshotPath, createdAt, "content-settings-adapter", "content.deepseek.settings-adapter", "1.1.0");
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options);
            WriteOnce(Path.Combine(taskRoot, manifestPath.Replace('/', Path.DirectorySeparatorChar)), manifestBytes);
            var reference = new ArtifactReference(artifactId, "content-config-snapshot", "1.0.0", manifestPath, Sha(manifestBytes));
            return new DesktopPublishedTaskInput { Reference = reference, ContentSelection = new ContentSettingsReference(artifactId, reference.ExpectedSha256, revision) };
        }

        private static DesktopPublishedTaskInput PublishAnki(string taskRoot, string taskId, DesktopWorkflowSettingsService settings, DesktopModuleSettingsDocument local, DateTimeOffset createdAt)
        {
            if (string.IsNullOrWhiteSpace(local.AnkiExpectedProfile)) throw new InvalidDataException("settings.anki-profile-required");
            var template = Path.Combine(settings.AuthorityReleaseRoot, "workers", "anki", "config", "anki-export.v1.example.json");
            var node = JsonNode.Parse(File.ReadAllBytes(template)).AsObject();
            node["expected_profile"] = local.AnkiExpectedProfile.Trim(); node["endpoint"] = local.AnkiEndpoint.Trim(); node["provision_managed"] = false;
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(node, ContractJson.Options);
            var artifactId = "anki.config." + Sha(payloadBytes).Substring(0, 24);
            var relativeRoot = "artifacts/" + artifactId;
            var payloadPath = relativeRoot + "/anki-export.v1.json";
            WriteOnce(Path.Combine(taskRoot, payloadPath.Replace('/', Path.DirectorySeparatorChar)), payloadBytes);
            var files = new List<object> { new { path = payloadPath, sha256 = Sha(payloadBytes), bytes = payloadBytes.LongLength, mediaType = "application/json" } };
            var manifestPath = relativeRoot + "/artifact.json";
            var manifest = ArtifactManifest(artifactId, "anki-export-config", "application/vnd.knowledge-clipper.anki-export-config+json", files, payloadPath, createdAt, "anki-settings-adapter", "export.anki.settings-adapter", "1.0.0");
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options);
            WriteOnce(Path.Combine(taskRoot, manifestPath.Replace('/', Path.DirectorySeparatorChar)), manifestBytes);
            return new DesktopPublishedTaskInput { Reference = new ArtifactReference(artifactId, "anki-export-config", "1.0.0", manifestPath, Sha(manifestBytes)) };
        }

        private static object ArtifactManifest(string id, string kind, string mediaType, IReadOnlyList<object> files, string entrypoint, DateTimeOffset createdAt, string stageId, string workerId, string workerVersion) => new
        {
            schemaVersion = "1.0.0", artifactId = id, kind, contractVersion = "1.0.0", mediaType, files,
            createdAt, producer = new { stageId, workerId, workerVersion, attemptId = "desktop-settings-" + Guid.NewGuid().ToString("N") },
            inputRefs = Array.Empty<object>(), dataClass = "personal", metadata = new { entrypoint }
        };

        private static void CopyOnce(string source, string destination) { if (!File.Exists(source)) throw new FileNotFoundException("settings.resource-missing", source); Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, false); }
        private static void WriteOnce(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        private static string Sha(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }
    }
}

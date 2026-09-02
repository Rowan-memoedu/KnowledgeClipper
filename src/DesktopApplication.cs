using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace KnowledgeClipper.Desktop.Application
{
    internal enum ExecutionState
    {
        Idle,
        Submitting,
        Running,
        WaitingInput,
        Completed,
        Failed
    }

    internal enum BusinessState
    {
        None,
        Ready,
        NeedsInitialization,
        MigrationRequired,
        AnswerReady,
        Unresolved
    }

    internal enum PersistenceState
    {
        Transient,
        VerifiedPending,
        Persisted,
        Rejected
    }

    internal enum InteractionState
    {
        Ready,
        Busy,
        AttentionRequired,
        Blocked
    }

    internal abstract class DesktopApplicationViewState
    {
        internal ExecutionState Execution { get; private protected set; } = ExecutionState.Idle;
        internal BusinessState Business { get; private protected set; } = BusinessState.None;
        internal PersistenceState Persistence { get; private protected set; } = PersistenceState.Transient;
        internal InteractionState Interaction { get; private protected set; } = InteractionState.Ready;
    }

    internal class AnkiProbeViewState : DesktopApplicationViewState
    {
        private string status;
        private string reasonCode;

        internal string Status
        {
            get { return status; }
            set { status = value; RefreshStateDimensions(); }
        }

        internal string Receipt { get; set; }
        internal string ReasonCode
        {
            get { return reasonCode; }
            set { reasonCode = value; RefreshStateDimensions(); }
        }
        internal string Step { get; set; }
        internal int? ApiVersion { get; set; }
        internal int ModelsReady { get; set; }
        internal int ModelsTotal { get; set; }
        internal int DecksReady { get; set; }
        internal int DecksTotal { get; set; }
        internal string[] MissingModels { get; set; } = Array.Empty<string>();
        internal string[] MissingDecks { get; set; } = Array.Empty<string>();
        internal string[] MigrationModels { get; set; } = Array.Empty<string>();
        internal bool InitializationWouldWrite { get; set; }
        internal bool MigrationRequired { get; set; }
        internal bool IsReady { get { return Status == "ready" && string.IsNullOrWhiteSpace(ReasonCode); } }
        internal bool NeedsInitialization { get { return Status == "needs_initialization" && string.IsNullOrWhiteSpace(ReasonCode); } }

        internal string DisplayText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ReasonCode)) return "失败 · reasonCode=" + ReasonCode + " · step=" + (Step ?? "unknown") + " · writes=0 · receipt=" + Receipt;
                var label = Status == "ready" ? "就绪" : Status == "needs_initialization" ? "需要初始化" : Status == "migration_required" ? "需要迁移" : Status;
                var missing = MissingModels.Length + MissingDecks.Length == 0 ? "" : " · missingModels=" + string.Join(",", MissingModels) + " · missingDecks=" + string.Join(",", MissingDecks);
                var migration = MigrationModels.Length == 0 ? "" : " · migrationModels=" + string.Join(",", MigrationModels);
                return label + " · apiVersion=" + (ApiVersion.HasValue ? ApiVersion.Value.ToString() : "unknown") + " · models=" + ModelsReady + "/" + ModelsTotal + " · decks=" + DecksReady + "/" + DecksTotal + missing + migration + " · initializationWouldWrite=" + InitializationWouldWrite.ToString().ToLowerInvariant() + " · probeWrites=0 · receipt=" + Receipt;
            }
        }

        private void RefreshStateDimensions()
        {
            if (!string.IsNullOrWhiteSpace(reasonCode) || string.Equals(status, "failed", StringComparison.Ordinal))
            {
                Execution = ExecutionState.Failed;
                Business = BusinessState.Unresolved;
                Persistence = PersistenceState.Rejected;
                Interaction = InteractionState.Blocked;
                return;
            }
            if (string.Equals(status, "ready", StringComparison.Ordinal))
            {
                Execution = ExecutionState.Completed;
                Business = BusinessState.Ready;
                Persistence = PersistenceState.Persisted;
                Interaction = InteractionState.Ready;
                return;
            }
            if (string.Equals(status, "needs_initialization", StringComparison.Ordinal))
            {
                Execution = ExecutionState.Completed;
                Business = BusinessState.NeedsInitialization;
                Persistence = PersistenceState.Persisted;
                Interaction = InteractionState.AttentionRequired;
                return;
            }
            if (string.Equals(status, "migration_required", StringComparison.Ordinal))
            {
                Execution = ExecutionState.Completed;
                Business = BusinessState.MigrationRequired;
                Persistence = PersistenceState.Persisted;
                Interaction = InteractionState.AttentionRequired;
                return;
            }
            Execution = ExecutionState.Running;
            Business = BusinessState.None;
            Persistence = PersistenceState.Transient;
            Interaction = InteractionState.Busy;
        }
    }

    internal static class AnkiProbeProjection
    {
        internal static AnkiProbeViewState FromArtifact(string jobRoot, DesktopArtifactReference reference)
        {
            var verified = new VerifiedArtifactReader(jobRoot).Read(reference, "anki-connection-status", "anki-connection-status.v1");
            var root = verified.Entrypoint;
            object rawExternalMutation;
            if (root == null || Value(root, "operation") != "probe" || Value(root, "effectClass") != "provider-read-only" ||
                !root.TryGetValue("externalMutation", out rawExternalMutation) || Convert.ToBoolean(rawExternalMutation))
                throw new InvalidDataException("settings.anki-probe-output-boundary-invalid");
            object rawMutating;
            var mutatingActions = root.TryGetValue("mutatingActionsObserved", out rawMutating) && rawMutating is IEnumerable ? ((IEnumerable)rawMutating).Cast<object>().ToArray() : null;
            if (mutatingActions == null || mutatingActions.Length != 0)
                throw new InvalidDataException("settings.anki-probe-mutating-action-observed");
            var models = Objects(root, "models");
            var decks = Objects(root, "decks");
            return new AnkiProbeViewState
            {
                Status = Value(root, "status"),
                ApiVersion = Convert.ToInt32(root["apiVersion"]),
                ModelsReady = models.Count(item => Value(item, "status") == "ready"),
                ModelsTotal = models.Length,
                DecksReady = decks.Count(item => Value(item, "status") == "ready"),
                DecksTotal = decks.Length,
                MissingModels = models.Where(item => Value(item, "status") == "missing").Select(item => Value(item, "noteTypeKey")).ToArray(),
                MissingDecks = decks.Where(item => Value(item, "status") == "missing").Select(item => Value(item, "routeKey")).ToArray(),
                MigrationModels = models.Where(item => Value(item, "status") == "migration_required").Select(item => Value(item, "noteTypeKey")).ToArray(),
                InitializationWouldWrite = Convert.ToBoolean(root["initializationWouldWrite"]),
                MigrationRequired = Convert.ToBoolean(root["migrationRequired"])
            };
        }

        private static IDictionary<string, object>[] Objects(IDictionary<string, object> source, string key)
        {
            object raw;
            return source.TryGetValue(key, out raw) && raw is IEnumerable ? ((IEnumerable)raw).Cast<object>().Select(item => item as IDictionary<string, object>).Where(item => item != null).ToArray() : new IDictionary<string, object>[0];
        }

        private static string Value(IDictionary<string, object> source, string key) { object raw; return source != null && source.TryGetValue(key, out raw) && raw != null ? Convert.ToString(raw) : null; }
    }

    internal sealed class SettingsDraft : DesktopApplicationViewState
    {
        private readonly WorkflowSettingsDocument persistedDocument;
        private string persistedRevision;

        private SettingsDraft(WorkflowSettingsDocument document, string revision)
        {
            Document = Clone(document);
            persistedDocument = Clone(document);
            persistedRevision = revision;
            Execution = ExecutionState.Completed;
            Business = BusinessState.Ready;
            Persistence = PersistenceState.Persisted;
            Interaction = InteractionState.Ready;
        }

        internal WorkflowSettingsDocument Document { get; private set; }
        internal string PersistedRevision { get { return persistedRevision; } }
        internal bool HasPendingChanges { get { return Persistence == PersistenceState.VerifiedPending; } }
        internal string RemNoteBindingStatus { get; private set; } = "persisted";

        internal static SettingsDraft Open(WorkflowSettingsDocument document, string revision)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return new SettingsDraft(document, revision);
        }

        internal void Replace(WorkflowSettingsDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            Document = Clone(document);
            Persistence = PersistenceState.VerifiedPending;
        }

        internal void StageVerifiedRemNoteBinding(string rootRemId, string rootTitle)
        {
            if (string.IsNullOrWhiteSpace(rootRemId) || string.IsNullOrWhiteSpace(rootTitle)) throw new InvalidDataException("settings.remnote-draft-binding-empty");
            if (!Document.Profiles.TryGetValue(Document.ActiveProfileId, out var active)) throw new InvalidDataException("settings.remnote-draft-active-profile-missing");
            var existing = active.Remnote;
            var enabled = existing?.Enabled == true;
            var advanced = existing?.Advanced ?? new RemNoteAdvancedSettings("mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd");
            var remnote = new RemNoteDestinationSettings("remnote-destination-settings.v1", enabled,
                new RemNoteTargetSettings(rootRemId.Trim(), rootTitle.Trim()), advanced);
            var profiles = new Dictionary<string, WorkflowSettingsProfile>(Document.Profiles, StringComparer.Ordinal)
            {
                [Document.ActiveProfileId] = active with { Remnote = remnote }
            };
            Document = new WorkflowSettingsDocument(Document.SchemaVersion, Document.ActiveProfileId, profiles);
            RemNoteBindingStatus = "verified_pending";
            Execution = ExecutionState.Completed;
            Business = BusinessState.Ready;
            Persistence = PersistenceState.VerifiedPending;
            Interaction = InteractionState.Ready;
        }

        internal bool TryGetRemNoteTarget(out string rootRemId, out string rootTitle)
        {
            rootRemId = null;
            rootTitle = null;
            if (!Document.Profiles.TryGetValue(Document.ActiveProfileId, out var active) || active.Remnote?.Target == null || string.IsNullOrWhiteSpace(active.Remnote.Target.RootRemId)) return false;
            rootRemId = active.Remnote.Target.RootRemId.Trim();
            rootTitle = string.IsNullOrWhiteSpace(active.Remnote.Target.RootTitle) ? "剪藏" : active.Remnote.Target.RootTitle.Trim();
            return true;
        }

        internal void RecordRemNoteDiscoveryFailure()
        {
            Execution = ExecutionState.Completed;
            Interaction = InteractionState.Ready;
        }

        internal WorkflowSettingsDocument PrepareCommit()
        {
            SettingsControlValidator.Validate(Document);
            Execution = ExecutionState.Submitting;
            Interaction = InteractionState.Busy;
            return Clone(Document);
        }

        internal void MarkPersisted(string revision)
        {
            persistedRevision = revision;
            RemNoteBindingStatus = "persisted";
            Execution = ExecutionState.Completed;
            Persistence = PersistenceState.Persisted;
            Interaction = InteractionState.Ready;
        }

        internal WorkflowSettingsDocument Discard()
        {
            Execution = ExecutionState.Completed;
            Persistence = PersistenceState.Rejected;
            Interaction = InteractionState.Ready;
            return Clone(persistedDocument);
        }

        private static WorkflowSettingsDocument Clone(WorkflowSettingsDocument document)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ContractJson.Options);
            return JsonSerializer.Deserialize<WorkflowSettingsDocument>(bytes, ContractJson.Options) ?? throw new InvalidDataException("settings.draft-clone-empty");
        }
    }
}

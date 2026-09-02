using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class ModuleCapabilityAction
    {
        internal string ModuleId;
        internal string CapabilityId;
        internal string ActionId;
        internal string Title;
        internal string Tier;
        internal string Availability;
        internal string EffectiveAt;
        internal string Kind;
        internal string NetworkAccess;
        internal string Protocol;
        internal string ResultSchemaVersion;
        internal bool ExternalUpload;
        internal bool RequiresConsent;
        internal bool OfflineSafe { get { return !ExternalUpload && !RequiresConsent; } }
    }

    internal sealed class ModuleCapabilityDescriptor
    {
        internal string SchemaVersion;
        internal string ModuleId;
        internal string SourcePath;
        internal readonly List<ModuleCapabilityAction> Actions = new List<ModuleCapabilityAction>();
        internal readonly Dictionary<string, string> Statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly HashSet<string> AvailableCapabilityIds = new HashSet<string>(StringComparer.Ordinal);
    }

    internal static class ModuleCapabilityDescriptorLoader
    {
        internal static ModuleCapabilityDescriptor TryLoad(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("module-capability-descriptor.root-invalid");
            var descriptor = new ModuleCapabilityDescriptor
            {
                SchemaVersion = Text(root, "schemaVersion"),
                ModuleId = Text(root, "moduleId"),
                SourcePath = Path.GetFullPath(path)
            };
            if (string.IsNullOrWhiteSpace(descriptor.SchemaVersion) || string.IsNullOrWhiteSpace(descriptor.ModuleId))
                throw new InvalidDataException("module-capability-descriptor.identity-missing");

            var semantics = Object(root, "statusSemantics");
            if (semantics != null)
                foreach (var pair in semantics)
                {
                    var value = pair.Value as Dictionary<string, object>;
                    if (value != null) descriptor.Statuses[pair.Key] = Text(value, "state") ?? "unknown";
                }

            var actionDefinitions = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var declaredActions = Array(root, "actions");
            if (declaredActions != null)
                foreach (var item in declaredActions)
                {
                    var action = item as Dictionary<string, object>;
                    var id = Text(action, "actionId");
                    if (!string.IsNullOrWhiteSpace(id)) actionDefinitions[id] = action;
                }

            var capabilities = Array(root, "capabilities");
            if (capabilities == null) return descriptor;
            var added = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in capabilities)
            {
                var capability = item as Dictionary<string, object>;
                if (capability == null) continue;
                var availability = Object(capability, "availability");
                var capabilityId = Text(capability, "capabilityId");
                if (availability != null && Text(availability, "state") == "available" && !string.IsNullOrWhiteSpace(capabilityId)) descriptor.AvailableCapabilityIds.Add(capabilityId);
                var probe = Object(capability, "statusProbe");
                if (probe == null) continue;
                var actionId = Text(probe, "actionId");
                if (string.IsNullOrWhiteSpace(actionId) || !added.Add(actionId)) continue;
                var display = Object(capability, "display");
                var sideEffects = Object(capability, "sideEffects");
                Dictionary<string, object> action;
                actionDefinitions.TryGetValue(actionId, out action);
                var actionAvailability = Object(action, "availability") ?? availability;
                var actionSideEffects = Object(action, "sideEffects") ?? sideEffects;
                var invocation = Object(action, "invocation");
                descriptor.Actions.Add(new ModuleCapabilityAction
                {
                    ModuleId = descriptor.ModuleId,
                    CapabilityId = capabilityId,
                    ActionId = actionId,
                    Title = Text(action, "title") ?? (display == null ? capabilityId : Text(display, "title")),
                    Tier = display == null ? "developer-diagnostics" : Text(display, "tier"),
                    Availability = actionAvailability == null ? "unknown" : Text(actionAvailability, "state"),
                    EffectiveAt = Text(capability, "effectiveAt"),
                    Kind = Text(action, "kind"),
                    NetworkAccess = Text(action, "networkAccess"),
                    Protocol = invocation == null ? null : Text(invocation, "protocol"),
                    ResultSchemaVersion = Text(action, "resultSchemaVersion"),
                    ExternalUpload = Boolean(actionSideEffects, "externalUpload"),
                    RequiresConsent = Boolean(actionSideEffects, "requiresConsent")
                });
            }
            return descriptor;
        }

        private static string Text(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }

        private static bool Boolean(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static object[] Array(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as object[] : null;
        }
    }
}

using System;

namespace KnowledgeClipper.Desktop.Core
{
    internal sealed class SemanticTestPort
    {
        private readonly DesktopController controller;

        internal SemanticTestPort(DesktopController controller) { this.controller = controller; }

        internal SemanticPortResponse Execute(SemanticActionEnvelope action)
        {
            if (action == null) return Error(null, "test-port.invalid-json-action");
            if (!string.Equals(action.schemaVersion, AutomationCatalog.ActionSchemaVersion, StringComparison.Ordinal)) return Error(action.requestId, "test-port.unsupported-schema-version");
            if (string.IsNullOrWhiteSpace(action.requestId)) return Error(null, "test-port.request-id-required");
            if (!AutomationCatalog.IsKnownAction(action.action)) return Error(action.requestId, "test-port.unknown-action");
            return new SemanticPortResponse { schemaVersion = "desktop-test-port.v1", requestId = action.requestId, ok = true, errorCode = null, state = controller.Execute(action) };
        }

        private SemanticPortResponse Error(string requestId, string code)
        {
            return new SemanticPortResponse { schemaVersion = "desktop-test-port.v1", requestId = requestId, ok = false, errorCode = code, state = controller.Snapshot() };
        }
    }

    internal sealed class SemanticPortResponse
    {
        public string schemaVersion { get; set; }
        public string requestId { get; set; }
        public bool ok { get; set; }
        public string errorCode { get; set; }
        public DesktopSemanticState state { get; set; }
    }
}

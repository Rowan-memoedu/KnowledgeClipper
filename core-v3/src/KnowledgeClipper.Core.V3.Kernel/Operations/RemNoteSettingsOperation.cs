using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal sealed class RemNoteSettingsOperation(string operationId) : IOperationHandler
{
    public string OperationId => operationId;
    public async Task<OperationExecutionResult> ExecuteAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        var discover = operationId == "remnote.discover-target";
        Require(command.Body.Schema == (discover ? "kc.remnote-discovery/1" : "kc.remnote-probe/1") && command.Authorization is null, "remnote_request_invalid");
        RemNoteBindingReference? binding = null;
        string title;
        if (discover)
        {
            Require(command.Inputs.Count == 0, "discovery_inputs_invalid");
            title = command.Body.Data.GetProperty("rootTitle").GetString()!;
            Require(!string.IsNullOrWhiteSpace(title) && title == title.Trim(), "discovery_title_invalid");
        }
        else
        {
            binding = command.Body.Data.GetProperty("binding").Deserialize<RemNoteBindingReference>(KernelJson.Options)!;
            Require(command.Inputs.SequenceEqual(new[] { binding.Evidence }), "probe_input_mismatch");
            await VerifyBindingAsync(binding, context, cancellationToken).ConfigureAwait(false);
            title = binding.RootTitle;
        }
        var input = discover ? StrictJson.CloneElement(new
        {
            schemaVersion = "remnote-target-discovery-request.v1", discoveryId = command.OperationId, authorizationScope = "configured-destination", timeoutMs = 15000,
            operations = new[] { "status", "search", "list", "read" }, selector = new { rootTitle = title, expectedType = "document", match = "exact", uniqueness = "required" }
        }) : StrictJson.CloneElement(new
        {
            schemaVersion = "remnote-public-probe-request.v2", probeId = command.OperationId, authorizationScope = "configured-destination", timeoutMs = 15000,
            operations = new[] { "status", "search", "list", "read" }, target = new { rootRemId = binding!.RootRemId, rootTitle = title }
        });
        var worker = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(command.OperationId,
            context.WorkerRuntime.GetAdmittedBinding("export.remnote"), StrictJson.CloneElement(new { phase = discover ? "discover-target" : "probe", input, command.IdempotencyKey }),
            TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
        Require(worker.Status == "completed" && worker.ExternalStateKnown && worker.ExternalWriteCount == 0, "remnote_readonly_violation");
        var value = worker.Payload;
        ValidateCommon(value, discover ? "remnote-target-discovery-result.v1" : "remnote-public-probe-result.v2");
        Require(Text(value, discover ? "discoveryId" : "probeId") == command.OperationId, "remnote_operation_mismatch");
        var evidence = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command,
            discover ? "remnote-discovery" : "remnote-probe", "1.0.0", discover ? "remnote-target-discovery-result.v1" : "remnote-public-probe-result.v2",
            value, command.Inputs, cancellationToken).ConfigureAwait(false);
        object projection;
        if (discover)
        {
            var selection = value.GetProperty("selection");
            Require(Text(selection.GetProperty("selector"), "rootTitle") == title, "discovery_selector_mismatch");
            var valid = IsPersistable(value);
            Require(selection.GetProperty("persistableBinding").GetBoolean() == valid, "discovery_persistable_mismatch");
            projection = new { discovery = new { state = Text(value, "state"), reasonCodes = value.GetProperty("reasonCodes"), candidateCount = selection.GetProperty("candidateCount").GetInt32(),
                persistableBinding = valid, binding = valid ? new RemNoteBindingReference(command.OperationId, evidence, Text(selection, "resolvedRootRemId"), title) : null } };
        }
        else
        {
            var target = value.GetProperty("targetBinding");
            Require(Text(target, "rootRemId") == binding!.RootRemId && Text(target, "expectedTitle") == title, "probe_target_mismatch");
            var exact = Bool(target, "matched") && Text(target, "observedTitle") == title && Text(target, "observedType") == "document";
            var connection = value.GetProperty("connection");
            projection = new { remNoteReadiness = new { connected = Bool(connection, "connected"), writePermission = Bool(connection, "acceptWriteOperations"), exactTargetMatch = exact,
                readyForRequestedAction = exact && Bool(connection, "connected") && Bool(connection, "acceptWriteOperations") && Text(value, "state") == "healthy" && Text(value, "admission") == "ready", reasonCodes = value.GetProperty("reasonCodes") } };
        }
        return new(StrictJson.CloneElement(projection), new[] { evidence }, 0, true);
    }

    internal static async Task VerifyBindingAsync(RemNoteBindingReference binding, OperationContext context, CancellationToken ct)
    {
        var events = context.EventStore.ReadStream(binding.DiscoveryOperationId);
        var terminal = events.LastOrDefault(x => x.EventType.StartsWith("operation.", StringComparison.Ordinal) && x.EventType != "operation.started");
        Require(terminal is { EventType: "operation.completed", Operation: "remnote.discover-target" } && terminal.StreamId == binding.DiscoveryOperationId &&
            terminal.Artifacts.SequenceEqual(new[] { binding.Evidence }) && events.Any(x => x.EventType == "command.accepted" && x.CorrelationId == terminal.CorrelationId), "binding_owner_invalid");
        var verified = await context.ArtifactStore.OpenVerifiedAsync(binding.Evidence, "remnote-discovery", "1.0.0", ct).ConfigureAwait(false);
        using var doc = StrictJson.Parse(verified.Primary.Content, "RemNote discovery proof");
        var value = doc.RootElement;
        ValidateCommon(value, "remnote-target-discovery-result.v1");
        var selection = value.GetProperty("selection");
        Require(IsPersistable(value) && Text(value, "discoveryId") == binding.DiscoveryOperationId && Text(selection, "resolvedRootRemId") == binding.RootRemId &&
            Text(selection.GetProperty("selector"), "rootTitle") == binding.RootTitle && Text(selection, "observedTitle") == binding.RootTitle, "binding_target_invalid");
    }
    private static bool IsPersistable(JsonElement value)
    {
        var s = value.GetProperty("selection"); var selector = s.GetProperty("selector"); var c = value.GetProperty("connection");
        return Text(value, "state") == "healthy" && Text(value, "admission") == "ready" && Bool(c, "connected") && Bool(c, "acceptWriteOperations") &&
            s.GetProperty("candidateCount").GetInt32() == 1 && Text(s, "resolution") == "unique" && Bool(s, "exactReadback") &&
            !string.IsNullOrWhiteSpace(Text(s, "resolvedRootRemId")) && Text(s, "observedType") == "document" && Text(s, "observedTitle") == Text(selector, "rootTitle") &&
            Text(selector, "expectedType") == "document" && Text(selector, "match") == "exact" && Text(selector, "uniqueness") == "required";
    }
    private static void ValidateCommon(JsonElement value, string schema)
    {
        Require(Text(value, "schemaVersion") == schema && Text(value.GetProperty("worker"), "id") == "export.remnote" && value.GetProperty("externalWriteCount").GetInt32() == 0 &&
            Bool(value, "sanitized") && !Bool(value.GetProperty("fixedSemantics"), "automaticRetryAllowed"), "remnote_evidence_invalid");
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
    private static bool Bool(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.True;
    private static void Require(bool condition, string code) { if (!condition) throw new InvalidDataException(code); }
}

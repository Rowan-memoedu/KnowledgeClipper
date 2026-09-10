using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal static class CardDestinations
{
    public static bool IsNeutral(CommandEnvelope command) => command.Body.Data.TryGetProperty("destinationMode", out var mode) && mode.GetString() == "confirmed-cards";
    public static bool SchemaMatches(CommandEnvelope command, string legacy) => command.Body.Schema == (IsNeutral(command) ? DestinationSchemas.Command : legacy);
    public static bool IsConfiguration(ArtifactReference reference) => reference.Kind is "anki-configuration" or "anki-export-config" or "destination-snapshot";
    public static bool HasActionAuthority(CommandEnvelope command, OperationContext context)
    {
        if (!IsNeutral(command)) return command.Authorization is { Policy: "isolated-canary-deck" };
        Require(command.Authorization is null && command.Body.Data.TryGetProperty("sourceOperationId", out var source) && source.GetString() == command.OperationId, "destination_action_owner_invalid");
        var terminal = context.EventStore.ReadStream(command.OperationId).LastOrDefault(x => x.EventType == "operation.completed");
        Require(terminal is not null && terminal.Artifacts.Count(x => x.Kind == "destination-snapshot") == 1, "destination_action_source_missing");
        // Token/revision/order checks remain in the specific action handler.
        return true;
    }
    public static DestinationSelection[] Selections(CommandEnvelope command)
    {
        Require(IsNeutral(command) && !command.Body.Data.TryGetProperty("configuration", out _), "destination_mode_conflict");
        foreach (var item in command.Body.Data.GetProperty("destinations").EnumerateArray())
            Require(item.EnumerateObject().All(x => x.Name is "kind" or "configuration" or "routeKey" or "binding"), "destination_selection_field_unknown");
        var values = command.Body.Data.GetProperty("destinations").Deserialize<DestinationSelection[]>(KernelJson.Options)!;
        Require(values.Length <= 2 && values.Select(x => x.Kind).Distinct().Count() == values.Length && values.All(x => x.Kind is "anki" or "remnote"), "destination_set_invalid");
        return values;
    }
    public static IEnumerable<ArtifactReference> Inputs(IEnumerable<DestinationSelection> values)
        => values.SelectMany(x => x.Binding is null ? new[] { x.Configuration } : new[] { x.Configuration, x.Binding.Evidence });

    public static async Task<ArtifactReference> FreezeAsync(CommandEnvelope command, OperationContext context, CancellationToken ct)
    {
        var selections = Selections(command);
        Require(command.Authorization is null && Inputs(selections).All(command.Inputs.Contains), "destination_initial_inputs_invalid");
        var frozen = new List<FrozenDestination>();
        foreach (var selection in selections)
        {
            var config = await context.ArtifactStore.OpenVerifiedAsync(selection.Configuration,
                selection.Kind == "anki" ? "anki-export-config" : "remnote-settings", "1.0.0", ct).ConfigureAwait(false);
            using var document = StrictJson.Parse(config.Primary.Content, "destination settings");
            var value = document.RootElement;
            string target;
            if (selection.Kind == "anki")
            {
                Require(selection.Binding is null && !string.IsNullOrWhiteSpace(selection.RouteKey), "anki_selection_invalid");
                target = value.GetProperty("deck_routes").GetProperty(selection.RouteKey!).GetString()!;
                Require(!string.IsNullOrWhiteSpace(target) && (!value.TryGetProperty("provision_managed", out var provision) || provision.ValueKind == JsonValueKind.False), "anki_config_invalid");
            }
            else
            {
                Require(selection.Binding is not null && selection.RouteKey is null, "remnote_selection_invalid");
                await RemNoteSettingsOperation.VerifyBindingAsync(selection.Binding!, context, ct).ConfigureAwait(false);
                Require(value.EnumerateObject().All(x => x.Name is "schemaVersion" or "enabled" or "target" or "advanced") && value.GetProperty("schemaVersion").GetString() == "remnote-destination-settings.v1" && value.GetProperty("enabled").GetBoolean(), "remnote_settings_invalid");
                var configured = value.GetProperty("target");
                Require(configured.EnumerateObject().All(x => x.Name is "rootRemId" or "rootTitle"), "remnote_settings_target_invalid");
                Require(configured.GetProperty("rootRemId").GetString() == selection.Binding!.RootRemId && configured.GetProperty("rootTitle").GetString() == selection.Binding.RootTitle, "remnote_settings_target_mismatch");
                Require(value.GetProperty("advanced").GetProperty("cardRouting").GetString() == "card-export", "remnote_card_route_invalid");
                var advanced = value.GetProperty("advanced");
                Require(advanced.EnumerateObject().All(x => x.Name is "sink" or "clipRouting" or "cardRouting" or "timeZone" or "dateFormat") &&
                    advanced.GetProperty("clipRouting").GetString() == "clip-daily-route" && advanced.GetProperty("timeZone").GetString() == "Asia/Shanghai" &&
                    advanced.GetProperty("dateFormat").GetString() == "yyyy-MM-dd" && advanced.GetProperty("sink").GetString() == "mcp-direct", "remnote_settings_unsupported");
                target = selection.Binding.RootRemId;
            }
            frozen.Add(new(selection.Kind, selection.Kind, selection.Configuration, target, selection.RouteKey, selection.Binding));
        }
        return await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "destination-snapshot", "1.0.0", "kc.destination-snapshot/1",
            new DestinationSnapshot(command.OperationId, frozen), Inputs(selections).ToArray(), ct).ConfigureAwait(false);
    }

    public static async Task<OperationExecutionResult> ConfirmAsync(CommandEnvelope command, OperationContext context,
        JsonElement sourceResult, IReadOnlyList<ArtifactReference> sourceArtifacts, CancellationToken ct)
    {
        Require(IsNeutral(command) && HasActionAuthority(command, context) && command.Inputs.Count == 0, "destination_confirmation_invalid");
        FlashcardDraftProjection.RequireBinding(command.Body.Data, sourceResult.GetProperty("cardDraft"));
        var snapshotRef = sourceArtifacts.Single(x => x.Kind == "destination-snapshot");
        Require(command.Body.Data.GetProperty("destinationSnapshot").Deserialize<ArtifactReference>(KernelJson.Options) == snapshotRef, "destination_snapshot_mismatch");
        var snapshot = await LoadAsync(snapshotRef, context, ct).ConfigureAwait(false);
        var grants = command.Body.Data.GetProperty("grants").Deserialize<DestinationWriteGrant[]>(KernelJson.Options)!;
        Require(grants.Length == snapshot.Destinations.Count && grants.Select(x => x.Kind).Distinct().Count() == grants.Length, "destination_grants_invalid");
        // Verify EVERY grant and config before dispatching ANY destination.
        foreach (var destination in snapshot.Destinations)
        {
            var grant = grants.SingleOrDefault(x => x.Kind == destination.Kind);
            Require(grant is not null && !string.IsNullOrWhiteSpace(grant.AuthorizationId) && grant.TargetIdentity == destination.TargetIdentity &&
                grant.Scope == (destination.Kind == "anki" ? "isolated-canary-deck" : "confirmed-remnote-root"), "destination_authorization_mismatch");
            if (destination.Kind == "anki") Require(destination.TargetIdentity == "零碎知识剪藏::Canary" || destination.TargetIdentity.StartsWith("零碎知识剪藏::Canary::", StringComparison.Ordinal), "anki_scope_mismatch");
            _ = await context.ArtifactStore.OpenVerifiedAsync(destination.Configuration, destination.Configuration.Kind, destination.Configuration.Contract, ct).ConfigureAwait(false);
            if (destination.Binding is not null) await RemNoteSettingsOperation.VerifyBindingAsync(destination.Binding, context, ct).ConfigureAwait(false);
        }
        var card = sourceArtifacts.Single(x => x.Kind is "card-bundle" or "question-card-bundle");
        var cards = await context.ArtifactStore.OpenVerifiedAsync(card, card.Kind, card.Contract, ct).ConfigureAwait(false);
        using var doc = StrictJson.Parse(cards.Primary.Content, "confirmed cards");
        var values = doc.RootElement.GetProperty("cards");
        var results = new List<DestinationResult>();
        foreach (var destination in snapshot.Destinations)
        {
            var stream = Stream(command.OperationId, snapshotRef, card, destination.DestinationId);
            var old = context.EventStore.ReadStream(stream);
            if (old.Count != 0)
            {
                results.Add(ReadResult(old, destination, card, snapshotRef));
                continue;
            }
            var configJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, destination.Configuration, ct).ConfigureAwait(false);
            var original = sourceArtifacts.FirstOrDefault(x => x.Kind == "capture-text");
            var originalText = original is null ? "" : await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, original, ct).ConfigureAwait(false);
            var binding = context.WorkerRuntime.GetAdmittedBinding(destination.Kind == "anki" ? "export.anki" : "export.remnote");
            var started = new DestinationResult(destination.DestinationId, destination.Kind, "unknown", "dispatch_not_terminal", card, snapshotRef, null, false, null);
            try { Append(context, command, stream, 0, "destination.started", started); }
            catch (EventStoreConcurrencyException) { results.Add(ReadResult(context.EventStore.ReadStream(stream), destination, card, snapshotRef)); continue; }
            DestinationResult result;
            try
            {
                var worker = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(command.OperationId, binding,
                    StrictJson.CloneElement(new { phase = "export", originalText, configurationJson = configJson, cards = values, sourceCardArtifact = card,
                        destination = destination.Kind, targetIdentity = destination.TargetIdentity, selectedRoute = destination.RouteKey,
                        cardBundleSha256 = card.ManifestDigest.Value, idempotencyKey = Hash(stream), route = new { kind = "card-export" },
                        sideEffectAuthorization = new { mode = "explicit-user-action", scope = grants.Single(x => x.Kind == destination.Kind).Scope,
                            authorizationId = grants.Single(x => x.Kind == destination.Kind).AuthorizationId, targetIdentity = destination.TargetIdentity } }),
                    TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), ct).ConfigureAwait(false);
                var known = worker.ExternalStateKnown;
                var countKnown = !worker.Payload.TryGetProperty("externalWriteCountKnown", out var countFlag) || countFlag.ValueKind == JsonValueKind.True;
                var succeeded = known && worker.Status == "completed" && worker.Payload.TryGetProperty("exactReadback", out var exact) && exact.ValueKind == JsonValueKind.True;
                var evidence = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command with { IdempotencyKey = Hash(stream) },
                    "destination-evidence", "1.0.0", "kc.destination-evidence/1", new { destinationId = destination.DestinationId, sourceCardArtifact = card, snapshot = snapshotRef, result = worker.Payload },
                    new[] { card, snapshotRef, destination.Configuration }, ct).ConfigureAwait(false);
                result = new(destination.DestinationId, destination.Kind, succeeded ? "succeeded" : known && worker.Status != "completed" ? "failed" : "unknown",
                    succeeded ? null : "destination_not_verified", card, snapshotRef, evidence, succeeded || known && worker.Status != "completed", countKnown && (succeeded || known && worker.Status != "completed") ? worker.ExternalWriteCount : null);
            }
            catch (Exception ex)
            {
                result = new(destination.DestinationId, destination.Kind, "unknown", ex is WorkerExternalStateUnknownException unknown ? unknown.Code : "dispatch_interrupted",
                    card, snapshotRef, null, false, null);
            }
            Append(context, command, stream, 1, "destination.terminal", result);
            results.Add(result);
        }
        var fields = sourceResult.Deserialize<Dictionary<string, JsonElement>>(KernelJson.Options)!;
        fields["pipeline"] = StrictJson.CloneElement(results.All(x => x.State == "succeeded") ? "completed" : "partially_failed");
        fields["status"] = StrictJson.CloneElement(results.All(x => x.State == "succeeded") ? "succeeded" : "partial");
        fields["destinations"] = StrictJson.CloneElement(results);
        fields["attentionRequired"] = StrictJson.CloneElement(results.Any(x => x.State != "succeeded"));
        return new(StrictJson.CloneElement(fields), sourceArtifacts.Concat(results.Where(x => x.Evidence is not null).Select(x => x.Evidence!)).ToArray(),
            results.Sum(x => x.ExternalWriteCount ?? 0), results.All(x => x.ExternalStateKnown)) { ExternalWriteCountKnown = results.All(x => x.ExternalWriteCount.HasValue) };
    }
    public static async Task<DestinationSnapshot> LoadAsync(ArtifactReference reference, OperationContext context, CancellationToken ct)
    {
        var verified = await context.ArtifactStore.OpenVerifiedAsync(reference, "destination-snapshot", "1.0.0", ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DestinationSnapshot>(verified.Primary.Content, KernelJson.Options) ?? throw new InvalidDataException("destination_snapshot_invalid");
    }
    public static string Stream(string operationId, ArtifactReference snapshot, ArtifactReference card, string destinationId)
        => "destination-" + Hash(operationId + "\n" + snapshot.ManifestDigest.Value + "\n" + card.ManifestDigest.Value + "\n" + destinationId);
    public static DestinationResult ReadResult(IReadOnlyList<KernelEvent> events, FrozenDestination destination, ArtifactReference card, ArtifactReference snapshot)
        => events.Count == 0 ? new(destination.DestinationId, destination.Kind, "not_started", null, card, snapshot, null, true, 0)
            : events.Last().Data.Deserialize<DestinationResult>(KernelJson.Options) ?? throw new InvalidDataException("destination_result_invalid");
    private static void Append(OperationContext context, CommandEnvelope command, string stream, long expected, string type, DestinationResult result)
        => context.EventStore.Append(stream, expected, new[] { new KernelEvent(KernelSchemas.Event, "evt-" + Guid.NewGuid().ToString("N"), stream, 0, type, 1,
            DateTimeOffset.UtcNow, command.CommandId, command.CommandId, command.Operation, "external-write", StrictJson.CloneElement(result), result.Evidence is null ? Array.Empty<ArtifactReference>() : new[] { result.Evidence }) });
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string code) { if (!value) throw new InvalidDataException(code); }
}

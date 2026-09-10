using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal sealed partial class CoreWorkerTaskPackageAdapter
{
    public async Task<PublicWorkerCommandPackage> PrepareRemNotePublicAsync(WorkerInvocation invocation, CancellationToken ct)
    {
        var command = GetString(invocation.Request, "phase") switch { "discover-target" => "discover-target-public", "probe" => "probe-public", _ => throw new InvalidDataException("Unsupported RemNote public command.") };
        var root = ResolveWithin(_root, "public/" + Hash(invocation.OperationId + "\n" + GetString(invocation.Request, "idempotencyKey")));
        var path = Path.Combine(root, "request.json");
        await WriteCreateOnceAsync(path, JsonSerializer.SerializeToUtf8Bytes(invocation.Request.GetProperty("input"), KernelJson.Options), ct).ConfigureAwait(false);
        return new(command, path, Path.Combine(root, "stdout.json"), Path.Combine(root, "stderr.log"));
    }
    private static JsonElement BuildRemNotePayload(JsonElement request)
    {
        using var config = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(GetString(request, "configurationJson")), "RemNote settings");
        var settings = config.RootElement;
        var target = settings.GetProperty("target");
        var authorization = request.GetProperty("sideEffectAuthorization");
        if (GetString(settings, "schemaVersion") != "remnote-destination-settings.v1" || !settings.GetProperty("enabled").GetBoolean() ||
            GetString(settings.GetProperty("advanced"), "cardRouting") != "card-export" || GetString(settings.GetProperty("advanced"), "sink") != "mcp-direct" ||
            GetString(target, "rootRemId") != GetString(request, "targetIdentity") || GetString(authorization, "mode") != "explicit-user-action" ||
            GetString(authorization, "scope") != "confirmed-remnote-root" || GetString(authorization, "targetIdentity") != GetString(target, "rootRemId"))
            throw new InvalidDataException("RemNote frozen settings/grant do not authorize the selected card root.");
        return StrictJson.CloneElement(new { schemaVersion = "2.0.0", documentTitle = "Cards " + GetString(request, "cardBundleSha256")[..24], sink = "mcp-direct", target,
            route = new { kind = "card-export" } });
    }
    private static async Task<PreparedInputs> PublishRemNoteInputsAsync(string jobRoot, string runId, string attemptId, JsonElement request, CancellationToken ct)
    {
        _ = BuildRemNotePayload(request); // Validate BEFORE publishing any dispatch package.
        var source = request.GetProperty("sourceCardArtifact");
        if (source.GetProperty("manifestDigest").GetProperty("value").GetString() != GetString(request, "cardBundleSha256"))
            throw new InvalidDataException("Confirmed source CardBundle digest mismatch.");
        var input = await PublishJsonArtifactAsync(jobRoot, "confirmed-" + attemptId[8..], "card-bundle", "confirmed-cards.json",
            StrictJson.CloneElement(new { sourceCardArtifact = source, cards = request.GetProperty("cards") }), ct).ConfigureAwait(false);
        var cards = request.GetProperty("cards").EnumerateArray().Select(card =>
        {
            if (GetString(card, "template") is not ("basic_qa" or "language_context" or "language_writing")) throw new InvalidDataException("RemNote card template unsupported.");
            return new { cardId = GetString(card, "cardId"), cardType = "basic", direction = "forward", front = GetString(card, "front"), back = GetString(card, "back") };
        }).ToArray();
        if (cards.Length is < 1 or > 2) throw new InvalidDataException("Confirmed CardBundle cardinality unsupported.");
        var id = "remnote-cards-" + attemptId[8..];
        var prefix = $"runs/{runId}/adapters/export-remnote/{id}/v0001";
        var payloadPath = prefix + "/bundle.json";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "card-bundle.v1", bundleId = "bundle-" + GetString(request, "cardBundleSha256")[..24], cards }, KernelJson.Options);
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, payloadPath), payload, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "1.0.0", artifactId = id, kind = "card.bundle", contractVersion = "1.0.0", mediaType = "application/json",
            files = new[] { new { path = payloadPath, sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), bytes = payload.LongLength, mediaType = "application/json" } },
            createdAt = DateTimeOffset.UnixEpoch, producer = new { stageId = "core-adapter", workerId = "orchestrator.core-adapter", workerVersion = "1.0.0", attemptId = "adapter-" + id },
            inputRefs = new[] { input }, dataClass = "personal", metadata = new { entrypoint = payloadPath, sourceCardArtifact = source } }, KernelJson.Options);
        var manifestPath = prefix + "/artifact.json";
        await WriteCreateOnceAsync(ResolveWithin(jobRoot, manifestPath), manifest, ct).ConfigureAwait(false);
        return new(new[] { new LegacyArtifactReference(id, "card.bundle", "1.0.0", manifestPath, Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant()) }, new[] { input });
    }
}

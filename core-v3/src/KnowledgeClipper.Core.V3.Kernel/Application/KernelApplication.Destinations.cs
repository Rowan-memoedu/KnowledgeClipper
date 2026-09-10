using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Operations;

namespace KnowledgeClipper.Core.V3.Kernel.Application;

internal sealed partial class KernelApplication
{
    private static JsonElement ReadDestinationProjection(JsonElement source, bool neutral)
    {
        var data = source.Deserialize<Dictionary<string, JsonElement>>(KernelJson.Options)!;
        var outputs = data["outputs"].Deserialize<Dictionary<string, JsonElement>>(KernelJson.Options)!;
        // Discovery/probe have their own projection. Keep the legacy closed v4 shape unchanged.
        outputs.Remove("remNoteDiscoveryArtifact"); outputs.Remove("remNoteProbeArtifact");
        if (!neutral) { data.Remove("destinations"); outputs.Remove("destinationSnapshotArtifact"); }
        data["outputs"] = StrictJson.CloneElement(outputs);
        return StrictJson.CloneElement(data);
    }
    private async Task<IReadOnlyList<DestinationResult>?> ProjectDestinationsAsync(string operationId, KernelEvent? source, CancellationToken ct)
    {
        if (source is null) return null;
        var snapshot = source.Artifacts.Single(x => x.Kind == "destination-snapshot");
        var card = source.Artifacts.SingleOrDefault(x => x.Kind is "card-bundle" or "question-card-bundle");
        if (card is null) return Array.Empty<DestinationResult>();
        var verified = await _artifactStore.OpenVerifiedAsync(snapshot, "destination-snapshot", "1.0.0", ct).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<DestinationSnapshot>(verified.Primary.Content, KernelJson.Options)!;
        var results = value.Destinations.Select(x => CardDestinations.ReadResult(
            _eventStore.ReadStream(CardDestinations.Stream(operationId, snapshot, card, x.DestinationId)), x, card, snapshot)).ToArray();
        foreach (var result in results.Where(x => x.Evidence is not null))
            _ = await _artifactStore.OpenVerifiedAsync(result.Evidence!, "destination-evidence", "1.0.0", ct).ConfigureAwait(false);
        return results;
    }
    private static JsonElement? MergeDestinationResults(JsonElement? source, IReadOnlyList<DestinationResult>? destinations)
    {
        if (destinations is null) return source;
        var value = source?.Deserialize<Dictionary<string, JsonElement>>(KernelJson.Options) ?? new();
        value["destinations"] = StrictJson.CloneElement(destinations);
        return StrictJson.CloneElement(value);
    }
}

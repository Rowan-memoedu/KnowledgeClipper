using System.Collections.Generic;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    internal static class CardResolutionEvidenceMapper
    {
        internal static SdkCardResolutionPrompt Convert(CardResolutionPrompt value)
        {
            var items = new List<SdkCardResolutionOccurrence>();
            if (value.Evidence.ValueKind == JsonValueKind.Object && value.Evidence.TryGetProperty("candidatePositions", out var positions))
                foreach (var item in positions.EnumerateArray())
                    items.Add(new SdkCardResolutionOccurrence(item.GetProperty("segment_id").GetString(), item.GetProperty("order").GetInt32(), item.GetProperty("start").GetInt32(), item.GetProperty("end").GetInt32(), item.GetProperty("exact_text").GetString(), item.GetProperty("source_sha256").GetString()));
            return new SdkCardResolutionPrompt(value.TargetToken, value.Revision, value.DecisionId, value.ReasonCode, value.Actions, items);
        }
    }
}

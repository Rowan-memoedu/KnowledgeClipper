using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    // Lifetime binding for the public neutral question API. The legacy Anki-only
    // bridge remains separate for callers deliberately using its compatible API.
    internal sealed class DestinationQuestionSessionSdkBridge
    {
        private sealed record Binding(IReadOnlyList<CardDestinationSelection> Destinations, string OperationId);
        private readonly ConcurrentDictionary<string, Binding> sessions = new(StringComparer.Ordinal);
        private readonly Func<CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freeze;
        private readonly Func<DestinationQuestionInitialRequest, CancellationToken, Task<QuestionOperationSubmission>> initial;
        private readonly Func<DestinationQuestionFollowUpRequest, CancellationToken, Task<QuestionOperationSubmission>> followUp;
        private readonly Func<DestinationQuestionMakeCardsRequest, CancellationToken, Task<QuestionOperationSubmission>> makeCards;

        internal DestinationQuestionSessionSdkBridge(Func<CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freeze,
            Func<DestinationQuestionInitialRequest, CancellationToken, Task<QuestionOperationSubmission>> initial,
            Func<DestinationQuestionFollowUpRequest, CancellationToken, Task<QuestionOperationSubmission>> followUp,
            Func<DestinationQuestionMakeCardsRequest, CancellationToken, Task<QuestionOperationSubmission>> makeCards)
        { this.freeze = freeze; this.initial = initial; this.followUp = followUp; this.makeCards = makeCards; }

        internal async Task<QuestionOperationSubmission> InitialAsync(string requestId, TypedArtifactHandle question, IReadOnlyList<TypedArtifactHandle> context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var selected = (await freeze(ct).ConfigureAwait(false)).ToArray();
            var result = await initial(new DestinationQuestionInitialRequest(requestId, question, context, selected), ct).ConfigureAwait(false);
            if (result.Accepted && (string.IsNullOrWhiteSpace(result.SessionId) || string.IsNullOrWhiteSpace(result.OperationId) ||
                !sessions.TryAdd(result.SessionId, new Binding(selected, result.OperationId)))) throw new InvalidOperationException("question-destination-session-conflict");
            return result;
        }
        internal async Task<QuestionOperationSubmission> FollowUpAsync(string requestId, string sessionId, TypedArtifactHandle question, CancellationToken ct)
        {
            var binding = Require(sessionId);
            var result = await followUp(new DestinationQuestionFollowUpRequest(requestId, sessionId, question), ct).ConfigureAwait(false);
            Advance(sessionId, binding, result); return result;
        }
        internal async Task<QuestionOperationSubmission> MakeCardsAsync(string requestId, string sessionId, TypedArtifactHandle answer, CancellationToken ct)
        {
            var binding = Require(sessionId);
            var result = await makeCards(new DestinationQuestionMakeCardsRequest(requestId, sessionId, answer), ct).ConfigureAwait(false);
            Advance(sessionId, binding, result); return result;
        }
        internal IReadOnlyList<CardDestinationSelection> SubmittedDestinations(string operationId)
        {
            var values = sessions.Values.Where(x => x.OperationId == operationId).ToArray();
            if (values.Length != 1) throw new InvalidOperationException("question-destination-operation-unbound");
            return values[0].Destinations;
        }
        private Binding Require(string id) => id != null && sessions.TryGetValue(id, out var value) ? value : throw new InvalidOperationException("question-destination-session-unbound");
        private void Advance(string id, Binding before, QuestionOperationSubmission result)
        {
            if (result.Accepted && (result.SessionId != id || string.IsNullOrWhiteSpace(result.OperationId) ||
                !sessions.TryUpdate(id, before with { OperationId = result.OperationId }, before))) throw new InvalidOperationException("question-destination-session-conflict");
        }
    }
}

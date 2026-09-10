using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    // Desktop-local lifetime binding; no new wire contract or invented session IDs.
    internal sealed class QuestionSessionSdkBridge
    {
        private sealed record Binding(TypedArtifactHandle Configuration, string OperationId);
        private readonly ConcurrentDictionary<string, Binding> sessions = new(StringComparer.Ordinal);
        private readonly Func<string> configuration;
        private readonly Func<string, CancellationToken, Task<TypedArtifactHandle>> publish;
        private readonly Func<QuestionInitialRequest, CancellationToken, Task<QuestionOperationSubmission>> initial;
        private readonly Func<QuestionFollowUpRequest, CancellationToken, Task<QuestionOperationSubmission>> followUp;
        private readonly Func<QuestionMakeCardsRequest, CancellationToken, Task<QuestionOperationSubmission>> makeCards;

        internal QuestionSessionSdkBridge(Func<string> configuration,
            Func<string, CancellationToken, Task<TypedArtifactHandle>> publish,
            Func<QuestionInitialRequest, CancellationToken, Task<QuestionOperationSubmission>> initial,
            Func<QuestionFollowUpRequest, CancellationToken, Task<QuestionOperationSubmission>> followUp,
            Func<QuestionMakeCardsRequest, CancellationToken, Task<QuestionOperationSubmission>> makeCards)
        {
            this.configuration = configuration; this.publish = publish;
            this.initial = initial; this.followUp = followUp; this.makeCards = makeCards;
        }

        internal async Task<QuestionOperationSubmission> InitialAsync(string requestId, TypedArtifactHandle question, IReadOnlyList<TypedArtifactHandle> context, CancellationToken token)
        {
            var json = configuration?.Invoke();
            if (string.IsNullOrWhiteSpace(json)) throw new SdkDomainException("question.session-configuration-unavailable", null);
            var frozen = await publish(json, token).ConfigureAwait(false);
            // The frozen capability policies for initial/follow-up are "none".
            // Passing write authorization here is rejected as not applicable.
            var result = await initial(new QuestionInitialRequest(requestId, question, context, frozen, null), token).ConfigureAwait(false);
            if (result.Accepted)
            {
                if (string.IsNullOrWhiteSpace(result.SessionId) || string.IsNullOrWhiteSpace(result.OperationId)) throw new SdkDomainException("question.invalid-session-receipt", null);
                if (!sessions.TryAdd(result.SessionId, new Binding(frozen, result.OperationId))) throw new SdkDomainException("question.session-already-bound", null);
            }
            return result;
        }

        internal async Task<QuestionOperationSubmission> FollowUpAsync(string requestId, string sessionId, TypedArtifactHandle input, CancellationToken token)
        {
            var binding = Require(sessionId);
            var result = await followUp(new QuestionFollowUpRequest(requestId, sessionId, input, binding.Configuration, null), token).ConfigureAwait(false);
            Advance(sessionId, binding, result);
            return result;
        }

        internal async Task<QuestionOperationSubmission> MakeCardsAsync(string requestId, string sessionId, TypedArtifactHandle answer, CancellationToken token)
        {
            var binding = Require(sessionId);
            var authorization = AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("desktop-question-make-cards-" + requestId);
            var result = await makeCards(new QuestionMakeCardsRequest(requestId, sessionId, answer, binding.Configuration, authorization), token).ConfigureAwait(false);
            Advance(sessionId, binding, result);
            return result;
        }

        private Binding Require(string sessionId) => sessionId != null && sessions.TryGetValue(sessionId, out var binding)
            ? binding : throw new SdkDomainException("question.session-binding-unavailable", null);

        private void Advance(string sessionId, Binding previous, QuestionOperationSubmission result)
        {
            if (!result.Accepted) return;
            if (result.SessionId != sessionId || string.IsNullOrWhiteSpace(result.OperationId) || !sessions.TryUpdate(sessionId, previous with { OperationId = result.OperationId }, previous))
                throw new SdkDomainException("question.session-receipt-conflict", null);
        }

        internal AnkiWriteAuthorization AuthorizationForOperation(string operationId, string requestId)
        {
            foreach (var binding in sessions.Values)
                if (binding.OperationId == operationId) return AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck("desktop-question-confirm-" + requestId);
            throw new SdkDomainException("question.operation-binding-unavailable", null);
        }
    }
}

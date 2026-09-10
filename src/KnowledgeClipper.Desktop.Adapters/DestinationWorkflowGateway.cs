using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    // Injectable calls use the public SDK's exact types; no second wire contract.
    internal sealed class DestinationSdkCalls
    {
        internal Func<PublishTextArtifactRequest, CancellationToken, Task<TypedArtifactHandle>> Publish;
        internal Func<TypedArtifactHandle, CancellationToken, Task<byte[]>> ReadArtifact;
        internal Func<ReadCardsRequest, CancellationToken, Task<OperationSubmission>> Read;
        internal Func<DestinationQuestionInitialRequest, CancellationToken, Task<QuestionOperationSubmission>> Initial;
        internal Func<DestinationQuestionFollowUpRequest, CancellationToken, Task<QuestionOperationSubmission>> FollowUp;
        internal Func<DestinationQuestionMakeCardsRequest, CancellationToken, Task<QuestionOperationSubmission>> MakeCards;
        internal Func<string, CancellationToken, Task<ReadAloudCardsProjectionSnapshot>> ReadProjection;
        internal Func<string, CancellationToken, Task<QuestionProjectionSnapshot>> QuestionProjection;
        internal Func<DestinationSnapshotReference, CancellationToken, Task<FrozenDestinationSnapshot>> Frozen;
        internal Func<RemNoteBindingReference, CancellationToken, Task<BindingRestoreResult>> RestoreRemNote;
        internal Func<ConfirmedCardsRequest, CancellationToken, Task<OperationSubmission>> Confirm;
        internal Func<FlashcardDraftCancellationRequest, CancellationToken, Task<OperationSubmission>> Cancel;
        internal Func<DestinationLanguageChoiceRequest, CancellationToken, Task<OperationSubmission>> Choice;
        internal Func<DestinationResolutionRequest, CancellationToken, Task<OperationSubmission>> Resolution;
        internal Func<DestinationReplayRequest, CancellationToken, Task<OperationSubmission>> Replay;

        internal static DestinationSdkCalls From(CoreV3Client client) => new()
        {
            Publish = client.PublishTextArtifactAsync,
            ReadArtifact = async (handle, ct) => (await client.ReadArtifactAsync(handle, null, ct).ConfigureAwait(false)).Content,
            Read = client.ReadCardsAsync, Initial = client.StartDestinationQuestionAsync,
            FollowUp = client.FollowUpDestinationQuestionAsync, MakeCards = client.MakeDestinationQuestionCardsAsync,
            ReadProjection = client.GetReadAloudCardsProjectionAsync, QuestionProjection = client.GetQuestionProjectionAsync,
            Frozen = client.GetFrozenDestinationsAsync, RestoreRemNote = client.RestoreRemNoteBindingAsync, Confirm = client.ConfirmCardsAsync,
            Cancel = client.CancelDestinationCardDraftAsync,
            Choice = client.ChooseDestinationLanguageCardsAsync, Resolution = client.ResolveDestinationCardsAsync,
            Replay = client.ReplayDestinationReadAsync
        };
    }

    internal sealed partial class SdkDomainFacade
    {
        private DestinationSdkCalls destinationCalls;
        private Func<CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freezeDestinations;
        private DestinationQuestionSessionSdkBridge destinationQuestions;
        private readonly ConcurrentDictionary<string, TypedArtifactHandle> projectedDestinationSnapshots = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> projectedCursors = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, IReadOnlyList<DestinationProjection>> projectedDestinations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<SdkDraftCommand>>> destinationConfirmations = new(StringComparer.Ordinal);
        public bool UsesDestinationSettings => destinationCalls != null;

        private void InitializeDestinations(Func<CoreV3Client, CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freeze)
            => InitializeDestinations(DestinationSdkCalls.From(client), ct => freeze(client, ct));

        // Same facade used by the production gateway; only SDK call dependencies differ.
        internal SdkDomainFacade(DestinationSdkCalls calls, Func<CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freeze)
            => InitializeDestinations(calls, freeze);

        private void InitializeDestinations(DestinationSdkCalls calls, Func<CancellationToken, Task<IReadOnlyList<CardDestinationSelection>>> freeze)
        {
            destinationCalls = calls ?? throw new ArgumentNullException(nameof(calls));
            freezeDestinations = freeze ?? throw new ArgumentNullException(nameof(freeze));
            destinationQuestions = new DestinationQuestionSessionSdkBridge(freeze, calls.Initial, calls.FollowUp, calls.MakeCards);
        }

        private void CacheDestinationProjection(string operationId, TypedArtifactHandle snapshot, long cursor, IReadOnlyList<DestinationProjection> destinations)
        {
            if (snapshot != null) projectedDestinationSnapshots[operationId] = snapshot;
            else projectedDestinationSnapshots.TryRemove(operationId, out _);
            projectedCursors[operationId] = cursor;
            projectedDestinations[operationId] = (destinations ?? Array.Empty<DestinationProjection>()).ToArray();
        }

        private Task<SdkDraftCommand> ConfirmDestinationsAsync(FlashcardDraftConfirmation confirmation, CancellationToken ct)
        {
            var draft = ResolveNative(confirmation);
            if (!projectedDestinationSnapshots.TryGetValue(confirmation.OperationId, out var snapshot) ||
                confirmation.DestinationSnapshot?.Id != snapshot.Token || confirmation.DestinationSnapshot.Kind != "destination-snapshot")
                throw new SdkDomainException("destination-snapshot-stale-or-unprojected", null);
            // One explicit confirmation per rendered draft/snapshot. An interrupted or
            // unknown result stays query-only; another click must not issue new writes.
            var key = confirmation.OperationId + "\n" + draft.Token + "\n" + draft.Revision + "\n" + snapshot.Token;
            var renderedDestinations = projectedDestinations[confirmation.OperationId];
            return destinationConfirmations.GetOrAdd(key, _ => new Lazy<Task<SdkDraftCommand>>(async () =>
            {
                var frozen = await destinationCalls.Frozen(new(confirmation.OperationId, snapshot.Token), ct).ConfigureAwait(false);
                if (frozen.OperationId != confirmation.OperationId || frozen.Snapshot.Token != snapshot.Token ||
                    frozen.Destinations.Count > 2 || frozen.Destinations.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Count() != frozen.Destinations.Count)
                    throw new SdkDomainException("destination-frozen-binding-invalid", null);
                // Core projects the aggregate snapshot here, not the per-target config.
                // GetFrozenDestinationsAsync separately SHA-verifies each Configuration.
                if (!frozen.Destinations.Select(x => (x.DestinationId, x.Kind)).SequenceEqual(
                    renderedDestinations.Select(x => (x.DestinationId, x.Kind))) ||
                    renderedDestinations.Any(x => x.ConfigurationSnapshot?.Token != snapshot.Token))
                    throw new SdkDomainException("destination-frozen-projection-mismatch", null);
                var grants = new List<CardDestinationAuthorization>();
                foreach (var target in frozen.Destinations)
                {
                    if (target.Kind == "anki")
                    {
                        if (target.RouteKey != "canary" || !(target.TargetIdentity == "零碎知识剪藏::Canary" || target.TargetIdentity.StartsWith("零碎知识剪藏::Canary::", StringComparison.Ordinal)))
                            throw new SdkDomainException("destination-anki-outside-admitted-scope", null);
                        grants.Add(new AnkiCardAuthorization(AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck(NextRequestId()), target.TargetIdentity));
                    }
                    else if (target.Kind == "remnote" && target.RemNoteBinding != null && target.RemNoteBinding.RootRemId == target.TargetIdentity)
                        grants.Add(new RemNoteCardAuthorization(NextRequestId(), target.TargetIdentity));
                    else throw new SdkDomainException("destination-kind-or-binding-unavailable", null);
                }
                ct.ThrowIfCancellationRequested();
                OperationSubmission value;
                try { value = await destinationCalls.Confirm(new(NextRequestId(), confirmation.OperationId, draft, snapshot, grants.ToArray()), ct).ConfigureAwait(false); }
                catch (CoreV3ClientException exception) { return new SdkDraftCommand(false, FlashcardDraftStatus.Unknown, exception.Code); }
                catch (OperationCanceledException) { return new SdkDraftCommand(false, FlashcardDraftStatus.Unknown, "flashcard-draft.confirm-observation-cancelled"); }
                return new SdkDraftCommand(value.Accepted, value.Accepted ? FlashcardDraftStatus.Exporting : FlashcardDraftStatus.Failed,
                    value.Accepted ? "flashcard-draft.confirm-accepted" : "flashcard-draft.confirm-rejected", value.CommandId, value.AcceptedSequence);
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
    }
}

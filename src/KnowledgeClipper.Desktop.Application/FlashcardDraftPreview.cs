using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal enum FlashcardDraftSource { ReadAloud, Question, FollowUp, MakeCards }
    internal enum FlashcardDraftStatus { Processing, AwaitingConfirmation, Confirming, Exporting, Completed, Failed, Cancelled, Unknown }

    internal sealed record FlashcardDraftCard(string CardId, string Revision, string Sha256, string Front, string Back, string Template = "basic_qa");

    internal sealed record FlashcardDraftRevision(
        string OperationId,
        string DraftToken,
        string Revision,
        string Sha256,
        FlashcardDraftSource Source,
        IReadOnlyList<FlashcardDraftCard> Cards,
        IReadOnlyList<string> Destinations)
    {
        internal ArtifactHandle DestinationSnapshot { get; init; }
        internal IReadOnlyList<CardDestinationOutcome> DestinationOutcomes { get; init; } = Array.Empty<CardDestinationOutcome>();
    }

    // UI projection only. Identities and nullable write counts come from the typed SDK.
    internal sealed record CardDestinationOutcome(string DestinationId, string Kind, string State, string ReasonCode,
        ArtifactHandle SourceCard, ArtifactHandle Configuration, ArtifactHandle Evidence, bool ExternalStateKnown, int? ExternalWriteCount);

    internal sealed record FlashcardDraftProjection(
        FlashcardDraftStatus Status,
        FlashcardDraftRevision Draft,
        long Cursor,
        string NoticeCode,
        bool AttentionRequired);

    internal sealed record FlashcardDraftConfirmation(
        string OperationId,
        string DraftToken,
        string Revision,
        string Sha256)
    {
        internal ArtifactHandle DestinationSnapshot { get; init; }
        internal static FlashcardDraftConfirmation From(FlashcardDraftRevision draft) => draft == null ? null :
            new(draft.OperationId, draft.DraftToken, draft.Revision, draft.Sha256) { DestinationSnapshot = draft.DestinationSnapshot };
    }

    internal sealed record FlashcardDraftCommandResult(
        bool Accepted,
        FlashcardDraftStatus Status,
        string NoticeCode)
    {
        internal string CommandId { get; init; }
        internal long AcceptedSequence { get; init; }
    }

    internal interface IFlashcardDraftPort
    {
        Task<FlashcardDraftCommandResult> ConfirmAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken);
        Task<FlashcardDraftCommandResult> CancelAsync(FlashcardDraftConfirmation confirmation, CancellationToken cancellationToken);
        Task<FlashcardDraftProjection> GetAsync(string operationId, CancellationToken cancellationToken);
    }

    internal sealed class FlashcardDraftPreviewState
    {
        internal static FlashcardDraftPreviewState Empty { get; } = new(
            FlashcardDraftStatus.Processing, null, 0, "flashcard-draft.awaiting-projection", false, false);

        internal FlashcardDraftPreviewState(
            FlashcardDraftStatus status,
            FlashcardDraftRevision draft,
            long cursor,
            string noticeCode,
            bool attentionRequired,
            bool commandInFlight,
            string followUpUnavailableReason = "question.preview-session-binding-unavailable")
        {
            Status = status;
            Draft = draft;
            Cursor = cursor;
            NoticeCode = noticeCode;
            AttentionRequired = attentionRequired;
            CommandInFlight = commandInFlight;
            FollowUpUnavailableReason = followUpUnavailableReason;
        }

        internal FlashcardDraftStatus Status { get; }
        internal FlashcardDraftRevision Draft { get; }
        internal long Cursor { get; }
        internal string NoticeCode { get; }
        internal bool AttentionRequired { get; }
        internal bool CommandInFlight { get; }
        internal string FollowUpUnavailableReason { get; }
        internal bool CanConfirm => Status == FlashcardDraftStatus.AwaitingConfirmation && Draft != null && !CommandInFlight;
        internal bool CanFollowUp => Draft != null && !CommandInFlight && Status == FlashcardDraftStatus.AwaitingConfirmation && FollowUpUnavailableReason == null;
    }

    internal sealed class FlashcardDraftPreviewReducer
    {
        internal FlashcardDraftPreviewState ApplyProjection(FlashcardDraftPreviewState current, FlashcardDraftProjection projection)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            current ??= FlashcardDraftPreviewState.Empty;
            if (projection.Draft != null) Validate(projection.Draft);
            if (current.Draft != null && projection.Draft != null &&
                string.Equals(current.Draft.OperationId, projection.Draft.OperationId, StringComparison.Ordinal) &&
                projection.Cursor < current.Cursor)
            {
                return new FlashcardDraftPreviewState(current.Status, current.Draft, current.Cursor, "flashcard-draft.stale-projection-rejected", true, current.CommandInFlight);
            }
            return new FlashcardDraftPreviewState(projection.Status, projection.Draft ?? current.Draft, projection.Cursor, projection.NoticeCode, projection.AttentionRequired, false);
        }

        internal FlashcardDraftPreviewState BeginConfirm(FlashcardDraftPreviewState current)
        {
            if (current == null || !current.CanConfirm)
                return new FlashcardDraftPreviewState(current?.Status ?? FlashcardDraftStatus.Failed, current?.Draft, current?.Cursor ?? 0, "flashcard-draft.confirm-not-available", true, current?.CommandInFlight ?? false);
            return new FlashcardDraftPreviewState(FlashcardDraftStatus.Confirming, current.Draft, current.Cursor, "flashcard-draft.confirming", false, true);
        }

        internal FlashcardDraftPreviewState CompleteConfirm(FlashcardDraftPreviewState current, FlashcardDraftCommandResult result)
        {
            if (current == null || !current.CommandInFlight) throw new InvalidOperationException("flashcard-draft.no-confirm-in-flight");
            if (result == null) throw new ArgumentNullException(nameof(result));
            return new FlashcardDraftPreviewState(result.Status, current.Draft, current.Cursor, result.NoticeCode, !result.Accepted, false);
        }

        internal FlashcardDraftPreviewState Cancel(FlashcardDraftPreviewState current)
        {
            current ??= FlashcardDraftPreviewState.Empty;
            return new FlashcardDraftPreviewState(FlashcardDraftStatus.Cancelled, current.Draft, current.Cursor, "flashcard-draft.cancelled-without-export", false, false);
        }

        private static void Validate(FlashcardDraftRevision draft)
        {
            if (string.IsNullOrWhiteSpace(draft.OperationId)) throw new InvalidOperationException("flashcard-draft.operation-required");
            if (string.IsNullOrWhiteSpace(draft.DraftToken)) throw new InvalidOperationException("flashcard-draft.token-required");
            if (string.IsNullOrWhiteSpace(draft.Revision)) throw new InvalidOperationException("flashcard-draft.revision-invalid");
            if (draft.Sha256 == null || draft.Sha256.Length != 64 || draft.Sha256.Any(character => !Uri.IsHexDigit(character))) throw new InvalidOperationException("flashcard-draft.sha256-invalid");
            if (draft.Cards == null || draft.Cards.Count == 0) throw new InvalidOperationException("flashcard-draft.cards-required");
            if (draft.Cards.Any(card => card == null || string.IsNullOrWhiteSpace(card.CardId) || string.IsNullOrWhiteSpace(card.Revision) || card.Sha256 == null || card.Sha256.Length != 64 || card.Sha256.Any(character => !Uri.IsHexDigit(character)) || string.IsNullOrWhiteSpace(card.Front) || string.IsNullOrWhiteSpace(card.Back)))
                throw new InvalidOperationException("flashcard-draft.card-invalid");
        }
    }

    internal sealed class FlashcardDraftPreviewUseCases
    {
        private readonly IFlashcardDraftPort port;
        private readonly FlashcardDraftPreviewReducer reducer;
        private readonly object gate = new();
        private FlashcardDraftPreviewState state = FlashcardDraftPreviewState.Empty;

        internal FlashcardDraftPreviewUseCases(IFlashcardDraftPort port, FlashcardDraftPreviewReducer reducer = null)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.reducer = reducer ?? new FlashcardDraftPreviewReducer();
        }

        internal event Action<FlashcardDraftPreviewState> Changed;
        internal Func<FlashcardDraftRevision, string> FollowUpEligibility { get; set; }
        internal Func<FlashcardDraftConfirmation, FlashcardDraftCommandResult, bool> ConfirmationObserver { get; set; }
        internal FlashcardDraftPreviewState Current { get { lock (gate) return WithEligibility(state); } }

        private FlashcardDraftPreviewState WithEligibility(FlashcardDraftPreviewState value) => new(value.Status, value.Draft, value.Cursor, value.NoticeCode, value.AttentionRequired, value.CommandInFlight,
            value.Draft?.Source == FlashcardDraftSource.ReadAloud ? "question.read-clip-follow-up-unsupported" : FollowUpEligibility == null ? "question.preview-session-binding-unavailable" : FollowUpEligibility(value.Draft));

        internal static bool SameDraft(FlashcardDraftRevision draft, FlashcardDraftConfirmation binding) => draft != null && binding != null &&
            draft.OperationId == binding.OperationId && draft.DraftToken == binding.DraftToken && draft.Revision == binding.Revision && string.Equals(draft.Sha256, binding.Sha256, StringComparison.OrdinalIgnoreCase)
            && Equals(draft.DestinationSnapshot, binding.DestinationSnapshot);

        internal bool TryBeginFollowUp(FlashcardDraftConfirmation expected)
        {
            lock (gate)
            {
                if (!WithEligibility(state).CanFollowUp || !SameDraft(state.Draft, expected)) return false;
                state = new(FlashcardDraftStatus.Processing, state.Draft, state.Cursor, "question.follow-up-submitting", false, true);
            }
            Changed?.Invoke(Current);
            return true;
        }

        internal void BeginQuestionWork() => Update(value => new(FlashcardDraftStatus.Processing, value.Draft, value.Cursor, "question.submitting", false, true));
        internal void QuestionWorkUnsettled(string notice, bool unknown) => Update(value => new(unknown ? FlashcardDraftStatus.Unknown : FlashcardDraftStatus.Failed, value.Draft, value.Cursor, notice, true, false));

        internal void Project(FlashcardDraftProjection projection)
        {
            Update(current => reducer.ApplyProjection(current, projection));
        }

        internal async Task<FlashcardDraftCommandResult> CancelAsync(CancellationToken cancellationToken)
        {
            FlashcardDraftConfirmation binding;
            lock (gate)
            {
                if (!state.CanConfirm) return new FlashcardDraftCommandResult(false, state.Status, "flashcard-draft.cancel-not-available");
                binding = FlashcardDraftConfirmation.From(state.Draft);
                state = new(FlashcardDraftStatus.Processing, state.Draft, state.Cursor, "flashcard-draft.cancel-requested", false, true);
            }
            Changed?.Invoke(Current);
            FlashcardDraftCommandResult result;
            try { result = await port.CancelAsync(binding, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = new(false, FlashcardDraftStatus.Unknown, "flashcard-draft.cancel-observation-cancelled"); }
            catch { result = new(false, FlashcardDraftStatus.Unknown, "flashcard-draft.cancel-outcome-unconfirmed"); }
            if (result.Accepted)
            {
                if (result.Status == FlashcardDraftStatus.Cancelled) Update(reducer.Cancel);
                else
                {
                    Update(value => new(result.Status, value.Draft, value.Cursor, result.NoticeCode, false, false));
                    _ = ObserveAfterConfirmAsync(binding.OperationId);
                }
            }
            else Update(value => new(result.Status, value.Draft, value.Cursor, result.NoticeCode, true, false));
            return result;
        }

        internal async Task<FlashcardDraftCommandResult> ConfirmAsync(CancellationToken cancellationToken)
        {
            FlashcardDraftConfirmation confirmation;
            lock (gate)
            {
                if (!state.CanConfirm) return new FlashcardDraftCommandResult(false, state.Status, "flashcard-draft.confirm-not-available");
                state = reducer.BeginConfirm(state);
                confirmation = FlashcardDraftConfirmation.From(state.Draft);
            }
            Changed?.Invoke(Current);
            FlashcardDraftCommandResult result;
            try { result = await port.ConfirmAsync(confirmation, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = new FlashcardDraftCommandResult(false, FlashcardDraftStatus.Unknown, "flashcard-draft.confirm-observation-cancelled"); }
            catch { result = new FlashcardDraftCommandResult(false, FlashcardDraftStatus.Unknown, "flashcard-draft.confirm-outcome-unconfirmed"); }
            Update(current => reducer.CompleteConfirm(current, result));
            var coordinated = result.Accepted && ConfirmationObserver?.Invoke(confirmation, result) == true;
            if (!coordinated && result.Accepted && result.Status is FlashcardDraftStatus.Exporting or FlashcardDraftStatus.Processing) _ = ObserveAfterConfirmAsync(confirmation.OperationId);
            return result;
        }

        private async Task ObserveAfterConfirmAsync(string operationId)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                if (attempt > 0) await Task.Delay(Math.Min(2000, 100 * (attempt + 1))).ConfigureAwait(false);
                FlashcardDraftProjection projection;
                try { projection = await port.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false); }
                catch { Update(current => new FlashcardDraftPreviewState(FlashcardDraftStatus.Failed, current.Draft, current.Cursor, "flashcard-draft.observation-failed", true, false)); return; }
                Project(projection);
                if (projection.Status is FlashcardDraftStatus.Completed or FlashcardDraftStatus.Failed or FlashcardDraftStatus.Cancelled or FlashcardDraftStatus.Unknown) return;
            }
            Update(current => new FlashcardDraftPreviewState(FlashcardDraftStatus.Failed, current.Draft, current.Cursor, "flashcard-draft.observation-timeout", true, false));
        }

        private void Update(Func<FlashcardDraftPreviewState, FlashcardDraftPreviewState> update)
        {
            FlashcardDraftPreviewState next;
            lock (gate) { state = update(state); next = state; }
            Changed?.Invoke(WithEligibility(next));
        }
    }
}

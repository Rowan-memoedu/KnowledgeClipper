using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed record ReadToAnkiObservationPolicy(int MaxAttempts, TimeSpan InitialDelay, TimeSpan MaximumDelay)
    {
        internal static ReadToAnkiObservationPolicy Default => new(80, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));

        internal void Validate()
        {
            if (MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
            if (InitialDelay < TimeSpan.Zero || MaximumDelay < InitialDelay) throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        }
    }

    internal sealed partial class ReadToAnkiUseCases
    {
        private readonly ICurrentTextCapturePort capture;
        private readonly IReadToAnkiInputArtifactPort artifacts;
        private readonly IReadToAnkiPort core;
        private readonly DesktopStateStore state;
        private readonly ReadToAnkiObservationPolicy policy;
        private readonly Action<FlashcardDraftProjection> projectFlashcardDraft;
        private int commandInFlight;
        private long observationGeneration;
        private Task observationCompletion = Task.CompletedTask;
        private long playbackObservationGeneration;
        private Task playbackObservationCompletion = Task.CompletedTask;

        internal ReadToAnkiUseCases(
            ICurrentTextCapturePort capture,
            IReadToAnkiInputArtifactPort artifacts,
            IReadToAnkiPort core,
            DesktopStateStore state,
            ReadToAnkiObservationPolicy policy = null,
            Action<FlashcardDraftProjection> projectFlashcardDraft = null)
        {
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.policy = policy ?? ReadToAnkiObservationPolicy.Default;
            this.projectFlashcardDraft = projectFlashcardDraft;
            this.policy.Validate();
        }

        internal Task ObservationCompletion => Task.WhenAll(observationCompletion, playbackObservationCompletion, stopObservationCompletion);

        internal bool ObserveConfirmedDraft(FlashcardDraftConfirmation confirmation, FlashcardDraftCommandResult result)
        {
            var current = state.Snapshot().ReadToAnki;
            if (!result.Accepted || string.IsNullOrWhiteSpace(result.CommandId) || result.AcceptedSequence <= 0 ||
                !string.Equals(current.OperationId, confirmation.OperationId, StringComparison.Ordinal) || current.Pipeline != ReadToAnkiPipelineStatus.AwaitingCardConfirmation) return false;
            var generation = Interlocked.Increment(ref observationGeneration);
            var accepted = new ReadToAnkiSubmissionResult(true, confirmation.OperationId, "read-to-anki.confirm-accepted") { CommandId = result.CommandId, AcceptedSequence = result.AcceptedSequence };
            state.Reduce(value => ReadToAnkiReducers.ResumeSubmitted(value, accepted));
            observationCompletion = ObserveUntilPauseOrTerminalAsync(accepted, generation, CancellationToken.None);
            return true;
        }

        internal async Task<ReadToAnkiSubmissionResult> StartAsync(CancellationToken cancellationToken)
            => await StartAsync(true, cancellationToken).ConfigureAwait(false);

        internal async Task<ReadToAnkiSubmissionResult> StartAsync(bool includeSpeech, CancellationToken cancellationToken)
        {
            var current = state.Snapshot().ReadToAnki;
            if (IsActive(current.Pipeline) || Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new ReadToAnkiSubmissionResult(false, current.OperationId, "read-to-anki-operation-active");

            var generation = Interlocked.Increment(ref observationGeneration);
            state.Reduce(ReadToAnkiReducers.Started);
            try
            {
                var captured = await capture.CaptureOnceAsync(cancellationToken).ConfigureAwait(false);
                if (captured == null || string.IsNullOrWhiteSpace(captured.Text))
                {
                    const string notice = "read-to-anki-text-required";
                    state.Reduce(value => ReadToAnkiReducers.Failed(value, notice));
                    return new ReadToAnkiSubmissionResult(false, null, notice);
                }

                var source = await artifacts.PublishCaptureTextAsync(new ImmutablePlainText(captured.Text), cancellationToken).ConfigureAwait(false);
                var context = new List<ArtifactHandle>();
                var roleBoundContext = new List<ContextArtifactSegment>();
                foreach (var segment in captured.OrderedContextSegments.OrderBy(item => item.Order))
                {
                    if (segment.Role is not ("prefix" or "suffix")) throw new InvalidOperationException("read-to-anki-context-role-required");
                    var artifact = await artifacts.PublishCaptureTextAsync(new ImmutablePlainText(segment.Text, IsContext: true), cancellationToken).ConfigureAwait(false);
                    context.Add(artifact);
                    roleBoundContext.Add(new ContextArtifactSegment(artifact, segment.Role, segment.Order));
                }
                if (source == null)
                {
                    const string notice = "read-to-anki-source-artifact-required";
                    state.Reduce(value => ReadToAnkiReducers.Failed(value, notice));
                    return new ReadToAnkiSubmissionResult(false, null, notice);
                }

                var result = await core.SubmitAsync(new ReadToAnkiSubmission(source, includeSpeech)
                {
                    TargetText = captured.TargetText,
                    OrderedContextSegments = context,
                    RoleBoundContextSegments = roleBoundContext,
                    ContextRequested = captured.ContextRequested,
                    ContextGranularity = captured.ContextGranularity,
                    SourceWindow = captured.SourceWindow
                }, cancellationToken).ConfigureAwait(false);
                state.Reduce(value => ReadToAnkiReducers.Submitted(value, result));
                if (result.Accepted)
                    observationCompletion = ObserveUntilPauseOrTerminalAsync(result, generation, CancellationToken.None);
                return result;
            }
            catch (OperationCanceledException)
            {
                const string notice = "read-to-anki-submit-cancelled";
                state.Reduce(value => ReadToAnkiReducers.Failed(value, notice));
                return new ReadToAnkiSubmissionResult(false, null, notice);
            }
            catch (Exception exception)
            {
                // Preserve only known capture failure codes, never arbitrary exception
                // text (which may contain a selection, provider response or credential).
                var notice = exception.Message == "context.unavailable" ? "context.unavailable"
                    : exception.Message == "read-to-anki-text-required" ? "read-to-anki-text-required"
                    : exception.Message.StartsWith("read-to-anki-copy-shortcut-failed:", StringComparison.Ordinal) ? "read-to-anki-copy-shortcut-failed"
                    : "read-to-anki-submit-" + exception.GetType().Name;
                state.Reduce(value => ReadToAnkiReducers.Failed(value, notice));
                return new ReadToAnkiSubmissionResult(false, null, notice);
            }
            finally
            {
                Interlocked.Exchange(ref commandInFlight, 0);
            }
        }

        internal async Task<ReadToAnkiSubmissionResult> ResumeLanguageChoiceAsync(LanguagePracticeChoice choice, CancellationToken cancellationToken)
        {
            var current = state.Snapshot().ReadToAnki;
            if (current.Pipeline != ReadToAnkiPipelineStatus.WaitingUserChoice || string.IsNullOrWhiteSpace(current.OperationId))
                return new ReadToAnkiSubmissionResult(false, current.OperationId, "read-to-anki-choice-not-requested");
            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new ReadToAnkiSubmissionResult(false, current.OperationId, "read-to-anki-operation-active");

            try
            {
                var generation = Interlocked.Increment(ref observationGeneration);
                var result = await core.ResumeLanguageChoiceAsync(new ReadToAnkiChoiceSubmission(current.OperationId, choice), cancellationToken).ConfigureAwait(false);
                state.Reduce(value => ReadToAnkiReducers.ResumeSubmitted(value, result));
                if (result.Accepted)
                    observationCompletion = ObserveUntilPauseOrTerminalAsync(result, generation, CancellationToken.None);
                return result;
            }
            catch (Exception exception)
            {
                var notice = "read-to-anki-choice-" + exception.GetType().Name;
                state.Reduce(value => ReadToAnkiReducers.Failed(value, notice));
                return new ReadToAnkiSubmissionResult(false, current.OperationId, notice);
            }
            finally
            {
                Interlocked.Exchange(ref commandInFlight, 0);
            }
        }

        internal async Task<ReadToAnkiPlaybackCommandResult> ReplayAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref commandInFlight) != 0)
                return new ReadToAnkiPlaybackCommandResult(false, "read-to-anki-operation-active");
            var current = state.Snapshot().ReadToAnki;
            if (!CanReplay(current) || string.IsNullOrWhiteSpace(current.OperationId))
                return new ReadToAnkiPlaybackCommandResult(false, "read-to-anki-replay-unavailable");
            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new ReadToAnkiPlaybackCommandResult(false, "read-to-anki-operation-active");
            var generation = Interlocked.Increment(ref playbackObservationGeneration);
            try
            {
                current = state.Snapshot().ReadToAnki;
                if (!CanReplay(current) || string.IsNullOrWhiteSpace(current.OperationId))
                    return new ReadToAnkiPlaybackCommandResult(false, "read-to-anki-replay-unavailable");
                TryReducePlayback(generation, current.OperationId, value => value with
                { ReadToAnki = value.ReadToAnki with { Playback = ReadToAnkiPlaybackStatus.Accepted, PlaybackOwnedLocally = true, PlaybackCommandId = null, PlaybackAcceptedSequence = 0, PlaybackTarget = null, CanStop = false, VerifiedPlaybackTerminal = false, NoticeCode = "read-to-anki.replay-submitting" } });
                var result = await core.ReplayAsync(current.OperationId, cancellationToken).ConfigureAwait(false);
                if (result.Accepted && (!string.Equals(result.OperationId, current.OperationId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(result.CommandId) || result.AcceptedSequence <= 0))
                    result = result with { Accepted = false, OutcomeUnknown = true, NoticeCode = "read-to-anki.replay-identity-unavailable" };
                var applied = TryReducePlayback(generation, current.OperationId, value => ReadToAnkiReducers.ReplayCompleted(value, result));
                if (result.Accepted && applied)
                    playbackObservationCompletion = ObserveReplayAsync(result, generation);
                return result;
            }
            catch (Exception exception)
            {
                var result = new ReadToAnkiPlaybackCommandResult(false, "read-to-anki-replay-" + exception.GetType().Name) { OutcomeUnknown = true };
                TryReducePlayback(generation, current.OperationId, value => ReadToAnkiReducers.ReplayCompleted(value, result));
                return result;
            }
            finally
            {
                Interlocked.Exchange(ref commandInFlight, 0);
            }
        }

        private bool TryReducePlayback(long generation, string operationId, Func<DesktopApplicationState, DesktopApplicationState> reducer) =>
            state.TryReduce(value => Volatile.Read(ref playbackObservationGeneration) == generation &&
                string.Equals(value.ReadToAnki.OperationId, operationId, StringComparison.Ordinal), reducer);

        private async Task ObserveReplayAsync(ReadToAnkiPlaybackCommandResult accepted, long generation)
        {
            var delay = policy.InitialDelay;
            for (var attempt = 0; attempt < policy.MaxAttempts; attempt++)
            {
                if (attempt > 0 && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = TimeSpan.FromTicks(Math.Min(policy.MaximumDelay.Ticks, Math.Max(delay.Ticks + 1, delay.Ticks * 2)));
                }
                if (Volatile.Read(ref playbackObservationGeneration) != generation || state.Snapshot().ReadToAnki.OperationId != accepted.OperationId) return;
                ReadToAnkiProjection projection;
                try { projection = await core.GetProjectionAsync(accepted.OperationId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception)
                {
                    SetPlayback(ReadToAnkiPlaybackStatus.Unknown, "read-to-anki.replay-observation-unavailable");
                    return;
                }
                if (projection == null || projection.OperationId != accepted.OperationId) continue;
                var terminal = projection.ActionState is "completed" or "failed" or "cancelled" or "unknown_external_state";
                if (terminal)
                {
                    if (projection.TerminalCommandId != accepted.CommandId || projection.TerminalSequence < accepted.AcceptedSequence) continue;
                    PlaybackControlSnapshot proof = null;
                    if (core is IReadPlaybackControlPort controls)
                    {
                        try { proof = await controls.GetPlaybackControlAsync(accepted.OperationId, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception) { }
                        if (proof?.OperationId != accepted.OperationId || proof.Target?.ActionCommandId != accepted.CommandId)
                        { SetPlayback(ReadToAnkiPlaybackStatus.Unknown, "read-to-anki.replay-target-unverified"); return; }
                        // A natural replay may finish without opening the Stop menu. Publish its
                        // typed attempt identity here, not only in the menu's refresh path.
                        TryReducePlayback(generation, accepted.OperationId, value => value with
                        { ReadToAnki = value.ReadToAnki with { PlaybackTarget = proof.Target, CanStop = false } });
                    }
                    var playback = projection.ActionState == "unknown_external_state" || projection.ActionState == "cancelled" ? ReadToAnkiPlaybackStatus.Unknown
                        : projection.ActionState == "failed" ? ReadToAnkiPlaybackStatus.Failed : projection.Playback;
                    // Acceptance/cancellation alone is not evidence of a stopped player.
                    if (projection.ActionState == "cancelled" && proof != null)
                    {
                        if (proof.OperationId == accepted.OperationId && proof.Target?.ActionCommandId == accepted.CommandId &&
                            proof.State is ReadToAnkiPlaybackStatus.Stopped or ReadToAnkiPlaybackStatus.IdleReplayable)
                        {
                            playback = proof.State;
                            TryReducePlayback(generation, accepted.OperationId, value => value with
                            { ReadToAnki = value.ReadToAnki with { PlaybackTarget = proof.Target, VerifiedPlaybackTerminal = true } });
                        }
                    }
                    if (playback is ReadToAnkiPlaybackStatus.IdleReplayable or ReadToAnkiPlaybackStatus.Failed or ReadToAnkiPlaybackStatus.Stopped or ReadToAnkiPlaybackStatus.Unknown)
                    { SetPlayback(playback, projection.NoticeCode); return; }
                }
                else if (projection.ActionState == "running" && projection.ActiveCommandId == accepted.CommandId)
                    SetPlayback(projection.Playback == ReadToAnkiPlaybackStatus.Playing ? ReadToAnkiPlaybackStatus.Playing : ReadToAnkiPlaybackStatus.Accepted, projection.NoticeCode);
            }
            SetPlayback(ReadToAnkiPlaybackStatus.Unknown, "read-to-anki.replay-observation-timeout");

            void SetPlayback(ReadToAnkiPlaybackStatus playback, string notice) =>
                TryReducePlayback(generation, accepted.OperationId, value => value with
                { ReadToAnki = value.ReadToAnki with { Playback = playback, CanStop = playback == ReadToAnkiPlaybackStatus.Playing && value.ReadToAnki.CanStop, NoticeCode = notice } });
        }

        internal async Task<ReadToAnkiSubmissionResult> ResolveCardAsync(CardResolutionSubmission submission, CancellationToken cancellationToken)
        {
            var current = state.Snapshot().ReadToAnki;
            if (current.Pipeline != ReadToAnkiPipelineStatus.AwaitingCardResolution || current.CardResolution == null || submission == null || !string.Equals(current.OperationId, submission.OperationId, StringComparison.Ordinal))
                return new ReadToAnkiSubmissionResult(false, current.OperationId, "read-to-anki-card-resolution-not-requested");
            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
                return new ReadToAnkiSubmissionResult(false, current.OperationId, "read-to-anki-operation-active");
            try
            {
                var generation = Interlocked.Increment(ref observationGeneration);
                var result = await core.ResolveCardAsync(submission, cancellationToken).ConfigureAwait(false);
                state.Reduce(value => ReadToAnkiReducers.ResumeSubmitted(value, result));
                if (result.Accepted) observationCompletion = ObserveUntilPauseOrTerminalAsync(result, generation, CancellationToken.None);
                return result;
            }
            catch (Exception exception)
            {
                var notice = "read-to-anki-card-resolution-" + exception.GetType().Name;
                state.Reduce(value => ReadToAnkiReducers.Failed(value, notice));
                return new ReadToAnkiSubmissionResult(false, current.OperationId, notice);
            }
            finally { Interlocked.Exchange(ref commandInFlight, 0); }
        }

        private async Task ObserveUntilPauseOrTerminalAsync(ReadToAnkiSubmissionResult submission, long generation, CancellationToken cancellationToken)
        {
            var operationId = submission.OperationId;
            var delay = policy.InitialDelay;
            for (var attempt = 0; attempt < policy.MaxAttempts; attempt++)
            {
                if (attempt > 0 && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    var nextTicks = Math.Min(policy.MaximumDelay.Ticks, Math.Max(delay.Ticks + 1, delay.Ticks * 2));
                    delay = TimeSpan.FromTicks(nextTicks);
                }

                ReadToAnkiProjection projection;
                try { projection = await core.GetProjectionAsync(operationId, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    TryReduceOwned(generation, operationId, value => ReadToAnkiReducers.ObservationInterrupted(value,
                        exception is OperationCanceledException ? "read-to-anki-observation-cancelled" : "read-to-anki-observation-unavailable"));
                    return;
                }

                if (!OwnsObservation(generation, operationId)) return;

                if (projection == null || !string.Equals(projection.OperationId, operationId, StringComparison.Ordinal))
                {
                    // The query stream may still contain a late projection from the previous
                    // operation. It must never overwrite or fail the newly accepted operation.
                    continue;
                }

                if (!MatchesSubmittedAction(projection, submission)) continue;

                var applied = TryReduceOwned(generation, operationId, value =>
                {
                    var next = ReadToAnkiReducers.Projected(value, projection);
                    if (projection.FlashcardDraft != null) projectFlashcardDraft?.Invoke(projection.FlashcardDraft);
                    return next;
                });
                if (!applied) return;
                if (projection.Pipeline is ReadToAnkiPipelineStatus.WaitingUserChoice or ReadToAnkiPipelineStatus.AwaitingCardResolution or ReadToAnkiPipelineStatus.AwaitingCardConfirmation || IsFullySettled(projection)) return;
            }

            TryReduceOwned(generation, operationId, value => ReadToAnkiReducers.Failed(value, "read-to-anki-observation-timeout"));
        }

        private bool OwnsObservation(long generation, string operationId) =>
            Volatile.Read(ref observationGeneration) == generation && string.Equals(state.Snapshot().ReadToAnki.OperationId, operationId, StringComparison.Ordinal);

        private bool TryReduceOwned(long generation, string operationId, Func<DesktopApplicationState, DesktopApplicationState> reducer) =>
            state.TryReduce(
                current => Volatile.Read(ref observationGeneration) == generation && string.Equals(current.ReadToAnki.OperationId, operationId, StringComparison.Ordinal),
                reducer);

        private static bool MatchesSubmittedAction(ReadToAnkiProjection projection, ReadToAnkiSubmissionResult submission)
        {
            if (string.IsNullOrWhiteSpace(submission.CommandId)) return true;
            if (string.Equals(projection.ActionState, "running", StringComparison.Ordinal))
                return string.Equals(projection.ActiveCommandId, submission.CommandId, StringComparison.Ordinal);
            if (projection.ActionState is "completed" or "failed" or "cancelled" or "unknown_external_state")
                return string.Equals(projection.TerminalCommandId, submission.CommandId, StringComparison.Ordinal) && projection.TerminalSequence >= submission.AcceptedSequence;
            return false;
        }

        private static bool IsActive(ReadToAnkiPipelineStatus status) =>
            status is ReadToAnkiPipelineStatus.Accepted or ReadToAnkiPipelineStatus.Reviewing or ReadToAnkiPipelineStatus.WaitingUserChoice or ReadToAnkiPipelineStatus.AwaitingCardResolution or ReadToAnkiPipelineStatus.AwaitingCardConfirmation or ReadToAnkiPipelineStatus.CreatingCards or ReadToAnkiPipelineStatus.WritingAnki;

        private static bool IsFullySettled(ReadToAnkiProjection projection)
        {
            if (projection.Pipeline == ReadToAnkiPipelineStatus.UnknownExternalState) return true;
            var pipelineSettled = projection.Pipeline is ReadToAnkiPipelineStatus.Completed or ReadToAnkiPipelineStatus.PartiallyFailed or ReadToAnkiPipelineStatus.Failed or ReadToAnkiPipelineStatus.UnknownExternalState;
            var playbackSettled = projection.Playback is ReadToAnkiPlaybackStatus.NotRequested or ReadToAnkiPlaybackStatus.IdleReplayable or ReadToAnkiPlaybackStatus.Failed or ReadToAnkiPlaybackStatus.Stopped or ReadToAnkiPlaybackStatus.Unknown;
            return pipelineSettled && playbackSettled;
        }
    }
}

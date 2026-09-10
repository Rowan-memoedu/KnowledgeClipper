using System;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed partial class ReadToAnkiUseCases
    {
        private int stopInFlight;
        private Task stopObservationCompletion = Task.CompletedTask;

        internal static bool CanReplay(ReadToAnkiFeatureState value) =>
            (value.Playback == ReadToAnkiPlaybackStatus.IdleReplayable ||
             value.Playback == ReadToAnkiPlaybackStatus.Stopped && value.VerifiedPlaybackTerminal) &&
            value.Pipeline is ReadToAnkiPipelineStatus.AwaitingCardConfirmation or ReadToAnkiPipelineStatus.WritingAnki or ReadToAnkiPipelineStatus.Completed or ReadToAnkiPipelineStatus.PartiallyFailed;

        internal async Task RefreshPlaybackControlAsync(CancellationToken cancellationToken)
        {
            if (core is not IReadPlaybackControlPort port) return;
            var current = state.Snapshot().ReadToAnki;
            if (string.IsNullOrWhiteSpace(current.OperationId) || current.Playback == ReadToAnkiPlaybackStatus.NotRequested) return;
            var generation = Volatile.Read(ref playbackObservationGeneration);
            try
            {
                var value = await port.GetPlaybackControlAsync(current.OperationId, cancellationToken).ConfigureAwait(false);
                if (value.OperationId != current.OperationId || value.Target?.OperationId != current.OperationId)
                {
                    TryReducePlayback(generation,current.OperationId,s=>s with {ReadToAnki=s.ReadToAnki with {CanStop=false}});
                    return;
                }
                state.TryReduce(s => Volatile.Read(ref playbackObservationGeneration) == generation && s.ReadToAnki.OperationId == current.OperationId &&
                    s.ReadToAnki.Playback != ReadToAnkiPlaybackStatus.StopRequested &&
                    (!s.ReadToAnki.PlaybackOwnedLocally ||
                     !string.IsNullOrWhiteSpace(s.ReadToAnki.PlaybackCommandId) && value.Target.ActionCommandId == s.ReadToAnki.PlaybackCommandId ||
                     s.ReadToAnki.VerifiedPlaybackTerminal && s.ReadToAnki.PlaybackTarget == value.Target),
                    s => s with { ReadToAnki = s.ReadToAnki with
                { PlaybackTarget = value.Target, CanStop = value.CanStop, Playback = value.State,
                  VerifiedPlaybackTerminal = value.State is ReadToAnkiPlaybackStatus.Stopped or ReadToAnkiPlaybackStatus.IdleReplayable } });
            }
            catch (Exception)
            {
                TryReducePlayback(generation, current.OperationId, s => s with { ReadToAnki = s.ReadToAnki with { CanStop = false } });
            }
        }

        internal async Task<PlaybackStopAcceptance> StopAsync(PlaybackTarget capturedTarget, CancellationToken cancellationToken)
        {
            var current = state.Snapshot().ReadToAnki;
            if (core is not IReadPlaybackControlPort port || capturedTarget == null || !current.CanStop ||
                capturedTarget != current.PlaybackTarget || current.OperationId != capturedTarget.OperationId ||
                Interlocked.CompareExchange(ref stopInFlight, 1, 0) != 0) return new(false, null, 0, capturedTarget);
            var generation = Interlocked.Increment(ref playbackObservationGeneration);
            try
            {
                Apply(ReadToAnkiPlaybackStatus.StopRequested, "playback.stop-requested");
                var accepted = await port.StopPlaybackAsync(capturedTarget, cancellationToken).ConfigureAwait(false);
                if (!accepted.Accepted || accepted.Target != capturedTarget || string.IsNullOrWhiteSpace(accepted.CommandId) || accepted.AcceptedSequence <= 0)
                { await ResolveRejectedAsync(); return accepted with { Accepted = false }; }
                // Wait runs independently of Read/Clip business observation; acceptance is not stopped.
                stopObservationCompletion = Observe();
                return accepted;

                async Task Observe()
                {
                    try
                    {
                        var terminal = await port.WaitPlaybackStopAsync(accepted, CancellationToken.None).ConfigureAwait(false);
                        if (terminal.Target != capturedTarget || terminal.OperationId != capturedTarget.OperationId)
                        { Apply(ReadToAnkiPlaybackStatus.Unknown, "playback.stop-target-changed"); return; }
                        if (terminal.State is ReadToAnkiPlaybackStatus.Stopped or ReadToAnkiPlaybackStatus.IdleReplayable)
                            Apply(terminal.State, terminal.State == ReadToAnkiPlaybackStatus.Stopped ? "playback.stopped" : "playback.naturally-completed", true);
                        else Apply(ReadToAnkiPlaybackStatus.Unknown, "playback.stop-outcome-unknown");
                    }
                    catch (Exception) { Apply(ReadToAnkiPlaybackStatus.Unknown, "playback.stop-observation-unavailable"); }
                }
            }
            catch (Exception)
            {
                await ResolveRejectedAsync();
                return new(false, null, 0, capturedTarget);
            }
            finally { Interlocked.Exchange(ref stopInFlight, 0); }

            async Task ResolveRejectedAsync()
            {
                // One read-only same-target check handles natural completion before signal acceptance.
                // Never resend Stop or adopt another round's terminal.
                try
                {
                    var proof = await port.GetPlaybackControlAsync(capturedTarget.OperationId, CancellationToken.None).ConfigureAwait(false);
                    if (proof.OperationId == capturedTarget.OperationId && proof.Target == capturedTarget &&
                        proof.State is ReadToAnkiPlaybackStatus.Stopped or ReadToAnkiPlaybackStatus.IdleReplayable)
                    { Apply(proof.State, proof.State == ReadToAnkiPlaybackStatus.Stopped ? "playback.stopped" : "playback.naturally-completed", true); return; }
                }
                catch (Exception) { }
                Apply(ReadToAnkiPlaybackStatus.Unknown, "playback.stop-outcome-unknown");
            }

            void Apply(ReadToAnkiPlaybackStatus playback, string notice, bool verified = false) =>
                state.TryReduce(s => Volatile.Read(ref playbackObservationGeneration) == generation &&
                    s.ReadToAnki.OperationId == capturedTarget.OperationId && s.ReadToAnki.PlaybackTarget == capturedTarget,
                    s => s with { ReadToAnki = s.ReadToAnki with { Playback = playback, CanStop = false, PlaybackOwnedLocally = true, VerifiedPlaybackTerminal = verified, NoticeCode = notice } });
        }
    }
}

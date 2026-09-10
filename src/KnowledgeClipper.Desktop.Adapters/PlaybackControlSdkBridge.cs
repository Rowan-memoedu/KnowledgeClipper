using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    internal sealed partial class NamedPipeCoreGateway
    {
        private IReadPlaybackControlPort PlaybackPort => sdk as IReadPlaybackControlPort ?? throw new InvalidOperationException("playback.typed-control-unavailable");
        public Task<PlaybackControlSnapshot> GetPlaybackControlAsync(string operationId, CancellationToken cancellationToken) => PlaybackPort.GetPlaybackControlAsync(operationId, cancellationToken);
        public Task<PlaybackStopAcceptance> StopPlaybackAsync(PlaybackTarget target, CancellationToken cancellationToken) => PlaybackPort.StopPlaybackAsync(target, cancellationToken);
        public Task<PlaybackControlSnapshot> WaitPlaybackStopAsync(PlaybackStopAcceptance accepted, CancellationToken cancellationToken) => PlaybackPort.WaitPlaybackStopAsync(accepted, cancellationToken);
    }

    internal sealed partial class SdkDomainFacade
    {
        private readonly ConcurrentDictionary<string, ReadPlaybackStopSubmission> stopSubmissions = new(StringComparer.Ordinal);
        public async Task<PlaybackControlSnapshot> GetPlaybackControlAsync(string operationId, CancellationToken cancellationToken) =>
            MapPlayback(await client.GetReadPlaybackControlAsync(operationId, cancellationToken).ConfigureAwait(false));

        public async Task<PlaybackStopAcceptance> StopPlaybackAsync(PlaybackTarget target, CancellationToken cancellationToken)
        {
            var native = new ReadPlaybackTarget(target.OperationId, target.TargetToken, target.ActionCommandId, target.AttemptId);
            var accepted = await client.StopReadPlaybackAsync(new ReadPlaybackStopRequest(NextRequestId(), native), cancellationToken).ConfigureAwait(false);
            if (accepted.Command.Accepted) stopSubmissions[accepted.Command.CommandId] = accepted;
            return new(accepted.Command.Accepted, accepted.Command.CommandId, accepted.Command.AcceptedSequence, MapTarget(accepted.Target));
        }

        public async Task<PlaybackControlSnapshot> WaitPlaybackStopAsync(PlaybackStopAcceptance accepted, CancellationToken cancellationToken)
        {
            if (!stopSubmissions.TryGetValue(accepted.CommandId, out var native) || MapTarget(native.Target) != accepted.Target || native.Command.AcceptedSequence != accepted.AcceptedSequence)
                throw new InvalidOperationException("playback.unbound-stop-submission");
            try { return MapPlayback(await client.WaitReadPlaybackStopAsync(native, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)); }
            finally { stopSubmissions.TryRemove(accepted.CommandId, out _); }
        }

        private static PlaybackTarget MapTarget(ReadPlaybackTarget value) => value == null ? null : new(value.OperationId, value.TargetToken, value.ActionCommandId, value.AttemptId);
        internal static PlaybackControlSnapshot MapPlayback(ReadPlaybackControlSnapshot value) => new(value.OperationId, value.State switch
        {
            ReadPlaybackControlState.Playing => ReadToAnkiPlaybackStatus.Playing,
            ReadPlaybackControlState.StopRequested or ReadPlaybackControlState.Completing => ReadToAnkiPlaybackStatus.StopRequested,
            ReadPlaybackControlState.Stopped => ReadToAnkiPlaybackStatus.Stopped,
            ReadPlaybackControlState.IdleReplayable => ReadToAnkiPlaybackStatus.IdleReplayable,
            _ => ReadToAnkiPlaybackStatus.Unknown
        }, value.CanStop, MapTarget(value.Target), value.Sequence);
    }
}

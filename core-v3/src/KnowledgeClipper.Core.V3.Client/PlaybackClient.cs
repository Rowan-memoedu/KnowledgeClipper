namespace KnowledgeClipper.Core.V3.Client;

public enum ReadPlaybackControlState { Unknown, Unsupported, Playing, StopRequested, Stopped, IdleReplayable, Ambiguous, Completing }
public sealed record ReadPlaybackTarget(string OperationId, string TargetToken, string ActionCommandId, string AttemptId);
public sealed record ReadPlaybackControlSnapshot(string OperationId, ReadPlaybackControlState State, bool CanStop,
    ReadPlaybackTarget? Target, long Sequence);
public sealed record ReadPlaybackStopRequest(string RequestId, ReadPlaybackTarget Target);
// Accepted acknowledges the signal request, never the speaker's terminal state.
public sealed record ReadPlaybackStopSubmission(OperationSubmission Command, ReadPlaybackTarget Target);

public sealed partial class CoreV3Client
{
    public async Task<ReadPlaybackControlSnapshot> GetReadPlaybackControlAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var response = await QueryAsync("read-playback.projection", new { operationId }, cancellationToken).ConfigureAwait(false);
        var data = response.Data;
        var state = GetOptionalString(data, "state") switch
        {
            "playing" => ReadPlaybackControlState.Playing,
            "stop_requested" => ReadPlaybackControlState.StopRequested,
            "completing" => ReadPlaybackControlState.Completing,
            "stopped" => ReadPlaybackControlState.Stopped,
            "idle_replayable" => ReadPlaybackControlState.IdleReplayable,
            "ambiguous" => ReadPlaybackControlState.Ambiguous,
            "unsupported" => ReadPlaybackControlState.Unsupported,
            _ => ReadPlaybackControlState.Unknown
        };
        var token = GetOptionalString(data, "targetToken");
        var target = token is null ? null : new ReadPlaybackTarget(operationId, token,
            GetOptionalString(data, "actionCommandId") ?? "", GetOptionalString(data, "attemptId") ?? "");
        return new(operationId, state, data.GetProperty("canStop").GetBoolean(), target, data.GetProperty("sequence").GetInt64());
    }

    public async Task<ReadPlaybackStopSubmission> StopReadPlaybackAsync(ReadPlaybackStopRequest request, CancellationToken cancellationToken = default)
    {
        var command = await SubmitDomainOperationAsync(request.RequestId, "clip.read-aloud-cards", "kc.read-playback-stop/1",
            new { action = "stop_playback", targetToken = request.Target.TargetToken }, Array.Empty<Contracts.ArtifactReference>(),
            cancellationToken, authorization: null, request.Target.OperationId).ConfigureAwait(false);
        return new(command, request.Target);
    }

    public async Task<ReadPlaybackControlSnapshot> WaitReadPlaybackStopAsync(ReadPlaybackStopSubmission submission,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await GetReadPlaybackControlAsync(submission.Target.OperationId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Target != submission.Target)
                throw new InvalidOperationException("playback.observation_target_changed: refusing another attempt's terminal.");
            if (snapshot.State is ReadPlaybackControlState.Stopped or ReadPlaybackControlState.IdleReplayable or ReadPlaybackControlState.Unknown)
                return snapshot;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("playback.stop_terminal_timeout: acceptance is not stopped.");
    }
}

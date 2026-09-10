using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace KnowledgeClipper.Workflow;

public sealed record PlaybackObservation(DateTimeOffset? FirstOutputAt, float Peak,
    bool EndpointMuted, float EndpointVolume, bool SessionMuted, float SessionVolume, string? Error)
{
    public DateTimeOffset? FirstStreamOutputAt { get; init; }
}

/// <summary>
/// Observes only the named process's render session. No samples are captured or saved.
/// A nonzero, unmuted Windows stream is evidence of output, not physical speaker audibility.
/// </summary>
public static class WindowsPlaybackMonitor
{
    public static Task<PlaybackObservation> FirstOutputAsync(int processId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(new PlaybackObservation(null, 0, false, 0, false, 0, "unsupported_os"));
        return Task.Run(() => Observe(processId, cancellationToken), CancellationToken.None);
    }

    private static PlaybackObservation Observe(int processId, CancellationToken cancellationToken)
    {
        PlaybackObservation latest = new(null, 0, false, 0, false, 0, null);
        DateTimeOffset? firstStreamOutputAt = null;
        float peakObserved = 0;
        var devices = new List<MMDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            devices.AddRange(enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));
            if (devices.Count == 0) return latest with { Error = "no_output_device" };
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var device in devices)
                {
                    try
                    {
                        device.AudioSessionManager.RefreshSessions();
                        var sessions = device.AudioSessionManager.Sessions;
                        for (var i = 0; i < sessions.Count; i++)
                        {
                            using var session = sessions[i];
                            if (session.GetProcessID != processId) continue;
                            var peak = session.AudioMeterInformation.MasterPeakValue;
                            if (peak > 0.00001f) firstStreamOutputAt ??= DateTimeOffset.UtcNow;
                            peakObserved = Math.Max(peakObserved, peak);
                            latest = new(null, peakObserved, device.AudioEndpointVolume.Mute,
                                device.AudioEndpointVolume.MasterVolumeLevelScalar, session.SimpleAudioVolume.Mute,
                                session.SimpleAudioVolume.Volume, null) { FirstStreamOutputAt = firstStreamOutputAt };
                            if (peak > 0.00001f && !latest.EndpointMuted && latest.EndpointVolume > 0 &&
                                !latest.SessionMuted && latest.SessionVolume > 0)
                                return latest with { FirstOutputAt = DateTimeOffset.UtcNow };
                        }
                    }
                    catch (COMException) { /* A disconnected endpoint must not stop playback or other devices. */ }
                }
                cancellationToken.WaitHandle.WaitOne(25);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            latest = latest with { Error = ex.GetType().Name };
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
        return latest;
    }
}

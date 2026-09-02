using System;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal sealed record WindowActivationSnapshot(long NativeWindowId, string FocusedElementRuntimeId, string Selection, uint ClipboardSequence);

    internal sealed class CaptureSession
    {
        private CaptureSession(string sessionId, DateTimeOffset startedAt, WindowActivationSnapshot frozenWindow)
        {
            SessionId = sessionId;
            StartedAt = startedAt;
            FrozenWindow = frozenWindow;
        }

        internal string SessionId { get; }
        internal DateTimeOffset StartedAt { get; }
        internal WindowActivationSnapshot FrozenWindow { get; }

        internal static CaptureSession Start(string sessionId, DateTimeOffset startedAt, WindowActivationSnapshot frozenWindow)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("capture-session-id-required", nameof(sessionId));
            if (frozenWindow == null || frozenWindow.NativeWindowId == 0 || string.IsNullOrWhiteSpace(frozenWindow.FocusedElementRuntimeId))
                throw new ArgumentException("capture-session-window-snapshot-required", nameof(frozenWindow));
            return new CaptureSession(sessionId, startedAt, frozenWindow with { });
        }
    }
}

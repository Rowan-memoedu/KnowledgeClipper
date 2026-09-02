using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace KnowledgeClipper.Speech.Worker
{
    internal interface IAudioPlayerFactory
    {
        IAudioPlayerProcess Start(string audioPath);
    }

    internal interface IAudioPlayerProcess : IDisposable
    {
        event EventHandler Exited;
        bool HasExited { get; }
        int ExitCode { get; }
        void Kill();
    }

    internal sealed class FfplayAudioPlayerFactory : IAudioPlayerFactory
    {
        private readonly string ffplayPath;
        public FfplayAudioPlayerFactory(string ffplayPath) { this.ffplayPath = ffplayPath; }

        public IAudioPlayerProcess Start(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(ffplayPath) || !File.Exists(ffplayPath)) throw new FileNotFoundException("找不到 ffplay", ffplayPath);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffplayPath,
                Arguments = "-nodisp -autoexit -loglevel quiet " + EdgeTtsSynthesizer.Quote(audioPath),
                WorkingDirectory = Path.GetDirectoryName(audioPath),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) throw new InvalidOperationException("无法启动 ffplay");
            process.EnableRaisingEvents = true;
            return new SystemAudioPlayerProcess(process);
        }
    }

    internal sealed class SystemAudioPlayerProcess : IAudioPlayerProcess
    {
        private readonly Process process;
        public SystemAudioPlayerProcess(Process process) { this.process = process; this.process.Exited += ForwardExited; }
        public event EventHandler Exited;
        public bool HasExited { get { try { return process.HasExited; } catch { return true; } } }
        public int ExitCode { get { return process.ExitCode; } }
        public void Kill() { if (!HasExited) process.Kill(); }
        public void Dispose() { process.Exited -= ForwardExited; process.Dispose(); }
        private void ForwardExited(object sender, EventArgs args) { var handler = Exited; if (handler != null) handler(this, EventArgs.Empty); }
    }

    internal sealed class PlaybackCoordinator : IDisposable
    {
        private readonly object gate = new object();
        private readonly IAudioPlayerFactory factory;
        private PlaybackSession active;

        public PlaybackCoordinator(IAudioPlayerFactory factory) { this.factory = factory; }

        public Task<PlaybackTerminal> PlayAsync(string sessionId, string audioPath)
        {
            PlaybackSession previous;
            PlaybackSession current;
            lock (gate)
            {
                previous = active;
                current = new PlaybackSession(sessionId, factory.Start(audioPath), OnExited);
                active = current;
            }
            if (previous != null) previous.Complete("superseded", null, "被新的播放取代", true);
            current.ObserveAlreadyExited();
            return current.Completion;
        }

        public bool StopActive()
        {
            return CancelActive("user_stop");
        }

        public bool CancelActive(string reason)
        {
            if (reason != "user_stop" && reason != "superseded" && reason != "task_cancel" && reason != "timeout")
                throw new SpeechContractException("invalid_cancellation_reason", "取消原因必须为 worker-cli-v1 四值之一");
            PlaybackSession session;
            lock (gate) { session = active; active = null; }
            if (session == null) return false;
            var message = reason == "superseded" ? "被新的播放取代" : reason == "user_stop" ? "用户主动停止" : reason == "timeout" ? "播放超时" : "任务取消";
            return session.Complete(reason, null, message, true);
        }

        public bool IsPlaying
        {
            get { lock (gate) { return active != null && !active.IsCompleted; } }
        }

        private void OnExited(PlaybackSession session)
        {
            int? exitCode = null;
            try { exitCode = session.Process.ExitCode; } catch { }
            var status = exitCode.GetValueOrDefault(-1) == 0 ? "completed" : "playback_failed";
            session.Complete(status, exitCode, status == "completed" ? null : "ffplay 异常退出", false);
            lock (gate) { if (ReferenceEquals(active, session)) active = null; }
        }

        public void Dispose()
        {
            PlaybackSession session;
            lock (gate) { session = active; active = null; }
            if (session != null) session.Complete("task_cancel", null, "播放器已释放", true);
        }

        private sealed class PlaybackSession
        {
            private readonly object sessionGate = new object();
            private readonly TaskCompletionSource<PlaybackTerminal> completion = new TaskCompletionSource<PlaybackTerminal>();
            private readonly Action<PlaybackSession> exitHandler;
            private bool completed;

            public PlaybackSession(string id, IAudioPlayerProcess process, Action<PlaybackSession> exitHandler)
            {
                Id = id; Process = process; this.exitHandler = exitHandler;
                Process.Exited += OnProcessExited;
            }

            public string Id { get; private set; }
            public IAudioPlayerProcess Process { get; private set; }
            public Task<PlaybackTerminal> Completion { get { return completion.Task; } }
            public bool IsCompleted { get { lock (sessionGate) { return completed; } } }

            public void ObserveAlreadyExited() { if (Process.HasExited) exitHandler(this); }

            public bool Complete(string status, int? exitCode, string message, bool kill)
            {
                lock (sessionGate)
                {
                    if (completed) return false;
                    completed = true;
                }
                Process.Exited -= OnProcessExited;
                if (kill) { try { Process.Kill(); } catch { } }
                try { Process.Dispose(); } catch { }
                completion.TrySetResult(new PlaybackTerminal { Status = status, ExitCode = exitCode, Message = message });
                return true;
            }

            private void OnProcessExited(object sender, EventArgs e) { exitHandler(this); }
        }
    }
}

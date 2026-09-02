using System;
using System.IO;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class ShortPlaybackWorkspace : IDisposable
    {
        private const int MaximumToolPathLength = 200;
        private readonly string directory;
        private bool disposed;

        private ShortPlaybackWorkspace(string directory)
        {
            this.directory = directory;
            MediaPath = Path.Combine(directory, "p.mp3");
        }

        public string DirectoryPath { get { return directory; } }
        public string MediaPath { get; private set; }

        public static ShortPlaybackWorkspace Create(string immutableAudioPath)
        {
            var parent = Path.Combine(Path.GetTempPath(), "kcsp");
            LongPathFileSystem.CreateDirectory(parent);
            ShortPlaybackWorkspace workspace = null;
            for (var i = 0; i < 5; i++)
            {
                var candidate = Path.Combine(parent, Guid.NewGuid().ToString("N"));
                if (LongPathFileSystem.DirectoryExists(candidate)) continue;
                LongPathFileSystem.CreateDirectory(candidate);
                workspace = new ShortPlaybackWorkspace(candidate);
                break;
            }
            if (workspace == null) throw new SpeechWorkerException("short_temp_unavailable", "无法创建唯一的播放器临时工作区", true);
            try
            {
                if (workspace.MediaPath.Length > MaximumToolPathLength)
                    throw new SpeechWorkerException("short_temp_path_too_long", "系统临时路径过长，无法安全调用播放器", false);
                var sourceBytes = LongPathFileSystem.FileLength(immutableAudioPath);
                var sourceHash = SpeechRequestReader.HashFile(immutableAudioPath);
                LongPathFileSystem.CopyFileNew(immutableAudioPath, workspace.MediaPath);
                if (LongPathFileSystem.FileLength(workspace.MediaPath) != sourceBytes ||
                    !SpeechRequestReader.HashFile(workspace.MediaPath).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                    throw new SpeechWorkerException("playback_stage_integrity_mismatch", "播放器短路径副本与不可变音频不一致", false);
                return workspace;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kcsp")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(directory);
                if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) LongPathFileSystem.DeleteDirectory(full, true);
            }
            catch { }
        }
    }
}

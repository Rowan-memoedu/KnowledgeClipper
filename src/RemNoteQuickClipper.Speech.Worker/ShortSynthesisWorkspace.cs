using System;
using System.IO;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class ShortSynthesisWorkspace : IDisposable
    {
        private const int MaximumToolPathLength = 200;
        private readonly string directory;
        private bool disposed;

        private ShortSynthesisWorkspace(string directory)
        {
            this.directory = directory;
            TextPath = Path.Combine(directory, "t.txt");
            MediaPath = Path.Combine(directory, "m.mp3");
        }

        public string DirectoryPath { get { return directory; } }
        public string TextPath { get; private set; }
        public string MediaPath { get; private set; }

        public static ShortSynthesisWorkspace Create(string immutableSpokenTextPath)
        {
            var parent = Path.Combine(Path.GetTempPath(), "kcsp");
            LongPathFileSystem.CreateDirectory(parent);
            ShortSynthesisWorkspace workspace = null;
            for (var i = 0; i < 5; i++)
            {
                var candidate = Path.Combine(parent, Guid.NewGuid().ToString("N"));
                if (LongPathFileSystem.DirectoryExists(candidate)) continue;
                LongPathFileSystem.CreateDirectory(candidate);
                workspace = new ShortSynthesisWorkspace(candidate);
                break;
            }
            if (workspace == null) throw new SpeechWorkerException("short_temp_unavailable", "无法创建唯一的 Edge TTS 临时工作区", true);
            try
            {
                if (workspace.TextPath.Length > MaximumToolPathLength || workspace.MediaPath.Length > MaximumToolPathLength)
                    throw new SpeechWorkerException("short_temp_path_too_long", "系统临时路径过长，无法安全调用 Edge TTS", false);
                LongPathFileSystem.CopyFileNew(immutableSpokenTextPath, workspace.TextPath);
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

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KnowledgeClipper.Speech.Worker
{
    internal static class LongPathFileSystem
    {
        private const int LongPathThreshold = 248;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint CreateNew = 1;
        private const uint OpenExisting = 3;
        private const uint OpenAlways = 4;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint InvalidFileAttributes = 0xffffffff;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint MoveFileWriteThrough = 0x00000008;
        private const int ErrorAlreadyExists = 183;

        internal static bool FileExists(string path)
        {
            if (!IsLong(path)) return File.Exists(path);
            var attributes = GetFileAttributesW(NativePath(path));
            return attributes != InvalidFileAttributes && (attributes & FileAttributeDirectory) == 0;
        }

        internal static string GetFullPath(string path)
        {
            if (Path.DirectorySeparatorChar != '\\') return Path.GetFullPath(path);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", "path");
            var capacity = 512;
            while (capacity <= 32768)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFullPathNameW(path, (uint)capacity, buffer, IntPtr.Zero);
                if (length == 0) ThrowLastWin32("无法解析 Windows 路径");
                if (length < capacity) return buffer.ToString();
                capacity = checked((int)length + 1);
            }
            throw new PathTooLongException("Windows 路径超过 32767 字符");
        }

        internal static string GetDirectoryName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var separator = Math.Max(trimmed.LastIndexOf(Path.DirectorySeparatorChar), trimmed.LastIndexOf(Path.AltDirectorySeparatorChar));
            return separator > 0 ? trimmed.Substring(0, separator) : null;
        }

        internal static string GetFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var separator = Math.Max(trimmed.LastIndexOf(Path.DirectorySeparatorChar), trimmed.LastIndexOf(Path.AltDirectorySeparatorChar));
            return separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
        }

        internal static bool DirectoryExists(string path)
        {
            if (!IsLong(path)) return Directory.Exists(path);
            var attributes = GetFileAttributesW(NativePath(path));
            return attributes != InvalidFileAttributes && (attributes & FileAttributeDirectory) != 0;
        }

        internal static long FileLength(string path)
        {
            using (var stream = OpenRead(path)) return stream.Length;
        }

        internal static Stream OpenRead(string path)
        {
            if (!IsLong(path)) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var handle = CreateFileW(NativePath(path), GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (handle.IsInvalid) ThrowLastWin32("无法打开长路径文件");
            return new FileStream(handle, FileAccess.Read, 4096, false);
        }

        internal static byte[] ReadAllBytes(string path)
        {
            using (var stream = OpenRead(path))
            using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
        }

        internal static string ReadAllText(string path, Encoding encoding)
        {
            using (var stream = OpenRead(path))
            using (var reader = new StreamReader(stream, encoding, true)) return reader.ReadToEnd();
        }

        internal static void WriteAllBytesNew(string path, byte[] bytes)
        {
            using (var stream = OpenWriteNew(path)) stream.Write(bytes, 0, bytes.Length);
        }

        internal static void WriteAllTextNew(string path, string value, Encoding encoding)
        {
            using (var stream = OpenWriteNew(path))
            using (var writer = new StreamWriter(stream, encoding)) writer.Write(value);
        }

        internal static void AppendAllText(string path, string value, Encoding encoding)
        {
            if (!IsLong(path)) { File.AppendAllText(path, value, encoding); return; }
            var handle = CreateFileW(NativePath(path), GenericWrite, FileShareRead, IntPtr.Zero, OpenAlways, FileAttributeNormal, IntPtr.Zero);
            if (handle.IsInvalid) ThrowLastWin32("无法打开长路径追加文件");
            using (var stream = new FileStream(handle, FileAccess.Write, 4096, false))
            {
                stream.Seek(0, SeekOrigin.End);
                using (var writer = new StreamWriter(stream, encoding)) writer.Write(value);
            }
        }

        internal static void CopyFileNew(string source, string destination)
        {
            using (var input = OpenRead(source))
            using (var output = OpenWriteNew(destination)) input.CopyTo(output);
        }

        internal static void MoveFile(string source, string destination) { MoveCreateOnce(source, destination); }
        internal static void MoveDirectory(string source, string destination)
        {
            // A directory path can be shorter than MAX_PATH while one of its
            // children is not. Directory.Move then enumerates that child and
            // fails before the create-once rename. Use the Unicode Win32
            // directory rename on Windows regardless of the parent length.
            if (Path.DirectorySeparatorChar == '\\')
            {
                if (!MoveFileExW(NativePath(source), NativePath(destination), MoveFileWriteThrough))
                    ThrowLastWin32("无法原子发布长路径工件目录");
                return;
            }
            Directory.Move(source, destination);
        }

        internal static void CreateDirectory(string path)
        {
            if (!IsLong(path)) { Directory.CreateDirectory(path); return; }
            var phase = "resolve";
            try
            {
                var full = GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                phase = "root";
                var root = WindowsRoot(full);
                var current = root;
                phase = "segments";
                foreach (var segment in full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar))
                {
                    current = current + Path.DirectorySeparatorChar + segment;
                    phase = "exists";
                    if (DirectoryExists(current)) continue;
                    phase = "create";
                    if (!CreateDirectoryW(NativePath(current), IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorAlreadyExists)
                        ThrowLastWin32("无法创建长路径目录");
                }
            }
            catch (Exception ex) { throw new IOException("长路径目录创建失败（" + phase + ", " + ex.GetType().Name + "）：" + ex.Message, ex); }
        }

        internal static void DeleteFile(string path)
        {
            if (!FileExists(path)) return;
            if (!IsLong(path)) { File.Delete(path); return; }
            if (!DeleteFileW(NativePath(path))) ThrowLastWin32("无法删除长路径文件");
        }

        internal static void DeleteDirectory(string path, bool recursive)
        {
            if (!DirectoryExists(path)) return;
            if (!IsLong(path)) { Directory.Delete(path, recursive); return; }
            if (recursive)
            {
                var knownFiles = new[] { Path.Combine(path, "v0001", "payload.mp3"), Path.Combine(path, "v0001", "artifact.json") };
                foreach (var file in knownFiles) try { DeleteFile(file); } catch { }
                var version = Path.Combine(path, "v0001");
                if (DirectoryExists(version) && !RemoveDirectoryW(NativePath(version))) ThrowLastWin32("无法清理长路径版本目录");
            }
            if (!RemoveDirectoryW(NativePath(path))) ThrowLastWin32("无法清理长路径目录");
        }

        internal static string Extended(string path) { return NativePath(path); }

        private static Stream OpenWriteNew(string path)
        {
            if (!IsLong(path)) return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var handle = CreateFileW(NativePath(path), GenericWrite, 0, IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
            if (handle.IsInvalid) ThrowLastWin32("无法创建长路径文件");
            return new FileStream(handle, FileAccess.Write, 4096, false);
        }

        private static void MoveCreateOnce(string source, string destination)
        {
            if (!IsLong(source) && !IsLong(destination))
            {
                if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
                return;
            }
            if (!MoveFileExW(NativePath(source), NativePath(destination), MoveFileWriteThrough)) ThrowLastWin32("无法原子发布长路径工件");
        }

        private static bool IsLong(string path) { return Path.DirectorySeparatorChar == '\\' && GetFullPath(path).Length >= LongPathThreshold; }

        private static string WindowsRoot(string fullPath)
        {
            if (fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == Path.DirectorySeparatorChar)
                return fullPath.Substring(0, 2);
            if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                var serverEnd = fullPath.IndexOf(Path.DirectorySeparatorChar, 2);
                var shareEnd = serverEnd < 0 ? -1 : fullPath.IndexOf(Path.DirectorySeparatorChar, serverEnd + 1);
                if (serverEnd > 2 && shareEnd > serverEnd + 1) return fullPath.Substring(0, shareEnd);
            }
            throw new IOException("无法识别 Windows 路径根");
        }

        private static string NativePath(string path)
        {
            var full = GetFullPath(path);
            if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }

        private static void ThrowLastWin32(string message) { throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error())); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFullPathNameW(string fileName, uint bufferLength, StringBuilder buffer, IntPtr filePart);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string existing, string destination, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string path);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KnowledgeClipper
{
    internal static class LongPathFileSystem
    {
        static LongPathFileSystem()
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }

        public static void EnsureConfigured() { }

        public static string ForIo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

        public static bool FileExists(string path) { return File.Exists(ForIo(path)); }
        public static bool DirectoryExists(string path) { return Directory.Exists(ForIo(path)); }
        public static void CreateDirectory(string path) { Directory.CreateDirectory(ForIo(path)); }
        public static void DeleteDirectory(string path, bool recursive) { Directory.Delete(ForIo(path), recursive); }
        public static string ReadAllText(string path, Encoding encoding) { return File.ReadAllText(ForIo(path), encoding); }
        public static byte[] ReadAllBytes(string path) { return File.ReadAllBytes(ForIo(path)); }
        public static string[] ReadAllLines(string path, Encoding encoding) { return File.ReadAllLines(ForIo(path), encoding); }
        public static FileStream OpenRead(string path) { return File.OpenRead(ForIo(path)); }
        public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) { return new FileStream(ForIo(path), mode, access, share); }
        public static FileAttributes GetAttributes(string path) { return File.GetAttributes(ForIo(path)); }
        public static long FileLength(string path) { return new FileInfo(ForIo(path)).Length; }
        public static void MoveFile(string source, string destination) { File.Move(ForIo(source), ForIo(destination)); }
        public static void DeleteFile(string path) { File.Delete(ForIo(path)); }
        public static void WriteAllText(string path, string value, Encoding encoding)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)) CreateDirectory(parent);
            File.WriteAllText(ForIo(path), value, encoding);
        }
    }

    // Exporter-owned runtime primitives. Keeping these out of the desktop Core
    // prevents unrelated UI/settings changes from changing the worker binary.
    internal sealed class AppConfig
    {
        public string NodePath { get; set; }
        public string RemnoteCliJsPath { get; set; }
        public string RemnoteMcpUrl { get; set; }

        public static AppConfig Load(string path)
        {
            if (!LongPathFileSystem.FileExists(path)) throw new FileNotFoundException("找不到配置文件", path);
            var config = Json.New().Deserialize<AppConfig>(LongPathFileSystem.ReadAllText(path, Encoding.UTF8));
            if (config == null) throw new InvalidDataException("Exporter 配置为空");
            return config;
        }
    }

    internal static class Json
    {
        public static JavaScriptSerializer New()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        }

        public static IDictionary<string, object> Dict(object value) { return value as IDictionary<string, object>; }
        public static object[] Array(object value) { return value as object[] ?? new object[0]; }

        public static string String(IDictionary<string, object> value, params string[] keys)
        {
            if (value == null) return null;
            foreach (var key in keys)
            {
                object item;
                if (value.TryGetValue(key, out item) && item != null) return Convert.ToString(item, CultureInfo.InvariantCulture);
            }
            return null;
        }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode;
        public string StandardOutput;
        public string StandardError;
    }

    internal static class ProcessRunner
    {
        public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, int timeoutMilliseconds, IDictionary<string, string> environment)
        {
            return RunAsync(fileName, arguments, workingDirectory, timeoutMilliseconds, environment, CancellationToken.None);
        }

        public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, int timeoutMilliseconds, IDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            return Task.Run(delegate
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var start = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                if (environment != null)
                    foreach (var pair in environment) start.EnvironmentVariables[pair.Key] = pair.Value;
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) stderr.AppendLine(e.Data); };
                    if (!process.Start()) throw new InvalidOperationException("无法启动进程: " + fileName);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
                    while (!process.WaitForExit(100))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            try { process.Kill(); } catch { }
                            throw new TimeoutException("进程超时: " + Path.GetFileName(fileName));
                        }
                    }
                    process.WaitForExit();
                    return new ProcessResult { ExitCode = process.ExitCode, StandardOutput = stdout.ToString(), StandardError = stderr.ToString() };
                }
            });
        }

        internal static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && !value.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return value;
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else { result.Append('\\', slashes).Append(ch); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }
    }
}

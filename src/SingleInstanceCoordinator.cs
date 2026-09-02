using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KnowledgeClipper
{
    internal sealed class SingleInstanceLease : IDisposable
    {
        private readonly Mutex canonical;
        private readonly Mutex legacy;

        internal SingleInstanceLease(Mutex canonical, Mutex legacy)
        {
            this.canonical = canonical;
            this.legacy = legacy;
        }

        public void Dispose()
        {
            if (legacy != null) { try { legacy.ReleaseMutex(); } catch { } legacy.Dispose(); }
            if (canonical != null) { try { canonical.ReleaseMutex(); } catch { } canonical.Dispose(); }
        }
    }

    internal sealed class SingleInstanceProbe
    {
        public bool existingInstanceDetected { get; set; }
        public bool canonicalMutexDetected { get; set; }
        public bool legacyMutexDetected { get; set; }
        public int? existingProcessId { get; set; }
        public string existingProcessPath { get; set; }
        public long existingWindowHandle { get; set; }
        public string userFeedbackMode { get; set; }
    }

    internal static class SingleInstanceCoordinator
    {
        internal const string CanonicalMutexName = "Local\\KnowledgeClipper.SingleInstance";
        internal const string LegacyMutexName = "Local\\RemNoteQuickClipper.SingleInstance";
        private const int SwRestore = 9;

        internal static bool TryAcquire(out SingleInstanceLease lease)
        {
            lease = null;
            bool canonicalCreated;
            var canonical = new Mutex(true, CanonicalMutexName, out canonicalCreated);
            if (!canonicalCreated) { canonical.Dispose(); return false; }

            bool legacyCreated;
            var legacy = new Mutex(true, LegacyMutexName, out legacyCreated);
            if (!legacyCreated)
            {
                try { canonical.ReleaseMutex(); } catch { }
                canonical.Dispose();
                legacy.Dispose();
                return false;
            }
            lease = new SingleInstanceLease(canonical, legacy);
            return true;
        }

        internal static SingleInstanceProbe Probe()
        {
            return Probe(CanonicalMutexName, LegacyMutexName);
        }

        internal static SingleInstanceProbe Probe(string canonicalName, string legacyName)
        {
            var canonicalDetected = Exists(canonicalName);
            var legacyDetected = Exists(legacyName);
            var process = FindExistingProcess();
            var handle = process == null ? IntPtr.Zero : FindTopLevelWindow(process.Id);
            return new SingleInstanceProbe
            {
                existingInstanceDetected = canonicalDetected || legacyDetected || process != null,
                canonicalMutexDetected = canonicalDetected,
                legacyMutexDetected = legacyDetected,
                existingProcessId = process == null ? (int?)null : process.Id,
                existingProcessPath = SafeProcessPath(process),
                existingWindowHandle = handle.ToInt64(),
                userFeedbackMode = "activate-existing-window-and-message-box"
            };
        }

        internal static object NotifyExistingInstance(bool showDialog, string logRoot = null)
        {
            var probe = Probe();
            var handle = probe.existingProcessId.HasValue ? FindTopLevelWindow(probe.existingProcessId.Value) : IntPtr.Zero;
            var activated = false;
            if (handle != IntPtr.Zero)
            {
                try { ShowWindow(handle, SwRestore); activated = SetForegroundWindow(handle); } catch { }
            }
            var path = WriteDiagnostic(probe, activated, logRoot);
            if (showDialog)
            {
                MessageBox.Show(
                    activated
                        ? "零碎知识剪藏已在运行，已尝试唤醒现有窗口。"
                        : "零碎知识剪藏已在运行。如窗口未显示，请检查系统托盘后退出旧版本，再打开当前候选。",
                    "零碎知识剪藏",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return new { probe.existingInstanceDetected, probe.canonicalMutexDetected, probe.legacyMutexDetected, probe.existingProcessId, probe.existingProcessPath, existingWindowHandle = handle.ToInt64(), activationAttempted = handle != IntPtr.Zero, activated, diagnosticPath = path, userFeedbackShown = showDialog };
        }

        internal static int WriteProbe(TextWriter output)
        {
            var probe = Probe();
            output.WriteLine(new JavaScriptSerializer().Serialize(probe));
            output.Flush();
            return 0;
        }

        private static bool Exists(string name)
        {
            bool created;
            using (var mutex = new Mutex(false, name, out created)) { return !created; }
        }

        private static Process FindExistingProcess()
        {
            var current = Process.GetCurrentProcess().Id;
            foreach (var process in Process.GetProcessesByName("KnowledgeClipper"))
            {
                if (process.Id != current) return process;
                process.Dispose();
            }
            return null;
        }

        private static string SafeProcessPath(Process process)
        {
            if (process == null) return null;
            try { return process.MainModule == null ? null : process.MainModule.FileName; }
            catch { return null; }
        }

        private static IntPtr FindTopLevelWindow(int processId)
        {
            var result = IntPtr.Zero;
            EnumWindows(delegate (IntPtr window, IntPtr parameter)
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != processId) return true;
                result = window;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        private static string WriteDiagnostic(SingleInstanceProbe probe, bool activated, string logRoot)
        {
            var root = string.IsNullOrWhiteSpace(logRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KnowledgeClipper", "logs")
                : Path.GetFullPath(logRoot);
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "single-instance.log");
            var record = new Dictionary<string, object>
            {
                { "timestamp", DateTime.UtcNow.ToString("o") },
                { "event", "existing-instance-detected" },
                { "canonicalMutexDetected", probe.canonicalMutexDetected },
                { "legacyMutexDetected", probe.legacyMutexDetected },
                { "existingProcessId", probe.existingProcessId },
                { "existingProcessPath", probe.existingProcessPath },
                { "existingWindowHandle", probe.existingWindowHandle },
                { "activationSucceeded", activated },
                { "result", "not-silent" }
            };
            File.AppendAllText(path, new JavaScriptSerializer().Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
            return path;
        }

        private delegate bool EnumWindowsDelegate(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    }
}

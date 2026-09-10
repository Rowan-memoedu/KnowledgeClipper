using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

internal sealed record SelectionSnapshot(string Text, long Window, string Source, int X, int Y, DateTimeOffset CapturedAt);

internal sealed class LanguageDesktopCapture(string helper)
{
    public async Task<CaptureMaterial> CaptureAsync(LanguageSettings settings, SelectionSnapshot frozen = null)
    {
        var inputTime = LastInputTime();
        var before = frozen?.Window ?? GetForegroundWindow().ToInt64();
        var title = frozen?.Source ?? WindowTitle(new IntPtr(before));
        using var input = await CaptureInputMonitor.StartAsync();
        if (inputTime != LastInputTime()) throw new InvalidOperationException("取词期间检测到新的输入，请重新选择。");
        void CheckSource()
        {
            if (input.UserInput) throw new InvalidOperationException("取词期间检测到新的输入，请重新选择。");
            if (GetForegroundWindow().ToInt64() != before) throw new InvalidOperationException("来源窗口已变化，请重新选择文字。");
        }
        CheckSource();
        string text = frozen?.Text;
        if (frozen == null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var info = new ProcessStartInfo(settings.NodePath) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            info.ArgumentList.Add(helper); info.ArgumentList.Add("--once");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动取词组件。");
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException ex) { throw new InvalidOperationException("取词组件响应超时，请重试取词。", ex); }
            if (process.ExitCode != 0) throw new InvalidOperationException("取词组件启动失败，请检查 selection-hook 运行依赖。");
            foreach (var line in (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var result = JsonDocument.Parse(line);
                if (result.RootElement.GetProperty("event").GetString() == "selection" && result.RootElement.GetProperty("data").ValueKind == JsonValueKind.Object)
                    text = result.RootElement.GetProperty("data").GetProperty("text").GetString();
            }
            CheckSource();
            if (string.IsNullOrWhiteSpace(text)) text = await OnStaAsync(token => CopySelection(before, input, token));
        }
        CheckSource();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("未取得本次选中文字。");
        var capturedText = text;
        var material = await OnStaAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            CheckSource();
            WindowsContextSnapshot context = null;
            if (settings.ContextEnabled)
                new WindowsUiAutomationContextProvider().TryRead(capturedText, settings.ContextGranularity, out context);
            token.ThrowIfCancellationRequested();
            CheckSource();
            var material = new CaptureMaterial(capturedText, context == null ? null : context.Prefix + capturedText + context.Suffix,
                context != null ? "source" : settings.ContextEnabled ? "unavailable" : "generated_allowed",
                context == null ? null : context.Prefix.Length, title, before, frozen?.CapturedAt ?? DateTimeOffset.UtcNow);
            material.Validate();
            return material;
        });
        CheckSource();
        return material;
    }

    private static async Task<T> OnStaAsync<T>(Func<CancellationToken, T> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var token = timeout.Token;
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.TrySetResult(action(token)); } catch (Exception ex) { result.TrySetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { return await result.Task.WaitAsync(token); }
        catch (OperationCanceledException ex) { throw new InvalidOperationException("取词或上下文读取超时，请重试。", ex); }
    }

    private static string CopySelection(long sourceWindow, CaptureInputMonitor input, CancellationToken token)
    {
        // A real, non-activating owner is required by EmptyClipboard/SetClipboardData.
        // Holding the clipboard lock also makes the sequence check and restore atomic
        // with respect to another application's copy operation.
        using var clipboardOwner = new Control();
        var ownerWindow = clipboardOwner.Handle;
        using var saved = ClipboardState.Take(ownerWindow, token);
        GetWindowThreadProcessId(new IntPtr(sourceWindow), out var sourcePid);
        uint copied = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            if (input.UserInput) throw new InvalidOperationException("取词期间检测到新的输入，请重新选择。");
            if (GetForegroundWindow().ToInt64() != sourceWindow) throw new InvalidOperationException("来源窗口已变化。");
            if (GetClipboardSequenceNumber() != saved.Sequence) throw new InvalidOperationException("剪贴板已更新，请重试取词。");
            if (!new SystemCopyShortcutSender().TrySend(out _)) throw new InvalidOperationException("无法取得本次选区。");
            for (var i = 0; i < 25; i++)
            {
                Thread.Sleep(20);
                token.ThrowIfCancellationRequested();
                if (input.UserInput) throw new InvalidOperationException("取词期间检测到新的输入，请重新选择。");
                if (GetForegroundWindow().ToInt64() != sourceWindow) throw new InvalidOperationException("来源窗口已变化。");
                if (GetClipboardSequenceNumber() == saved.Sequence) continue;
                var text = ClipboardState.ReadCopiedText(ownerWindow, saved.Sequence, sourcePid, token, out copied);
                if (!string.IsNullOrWhiteSpace(text)) return text;
                break;
            }
            throw new InvalidOperationException("未取得新的选中文字；不会使用旧剪贴板内容。");
        }
        finally
        {
            if (copied != 0 && !input.UserInput) saved.RestoreIfCurrent(ownerWindow, copied, sourcePid);
        }
    }

    // No caller thread is held in a UIA/native wait. The independent message pump
    // keeps low-level input hooks live while Node or a UIA provider is working.
    private sealed class CaptureInputMonitor : IDisposable
    {
        private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConcurrentInputGuard guard;
        private uint threadId;
        private int disposed;
        public bool UserInput => guard?.UserInput == true;
        public static async Task<CaptureInputMonitor> StartAsync()
        {
            var monitor = new CaptureInputMonitor();
            var thread = new Thread(() =>
            {
                try
                {
                    using var dispatcher = new Control();
                    _ = dispatcher.Handle;
                    monitor.threadId = GetCurrentThreadId();
                    using var guard = new ConcurrentInputGuard();
                    monitor.guard = guard;
                    monitor.ready.TrySetResult(true);
                    if (Volatile.Read(ref monitor.disposed) != 0) return;
                    Application.Run();
                }
                catch (Exception ex) { monitor.ready.TrySetException(ex); }
            }) { IsBackground = true, Name = "KnowledgeClipper.CaptureInputGuard" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await monitor.ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); return monitor; }
            catch { monitor.Dispose(); throw; }
        }
        public void Dispose() { Volatile.Write(ref disposed, 1); if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero); }
    }

    private sealed class ClipboardState : IDisposable
    {
        private readonly List<(uint Format, IntPtr Handle)> entries = new();
        public uint Sequence { get; private set; }

        public static ClipboardState Take(IntPtr ownerWindow, CancellationToken token)
        {
            var state = new ClipboardState();
            Open(ownerWindow, token);
            try
            {
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    // These application-owned handle types cannot be restored safely
                    // without the original application's custom rendering contract.
                    if (format == 0x80 || format is >= 0x200 and <= 0x3ff)
                        throw new InvalidOperationException("当前剪贴板含应用专用格式，已停止复制兜底以保留原内容。");
                    var original = GetClipboardData(format);
                    var copy = original == IntPtr.Zero ? IntPtr.Zero : format is 14 or 0x8e ? CopyEnhMetaFile(original, null) :
                        OleDuplicateData(original, format == 0x82 ? 2u : format == 0x83 ? 3u : format, 2);
                    if (copy == IntPtr.Zero) throw new InvalidOperationException("无法完整保留剪贴板格式，已停止复制兜底。");
                    state.entries.Add((format, copy));
                }
                state.Sequence = GetClipboardSequenceNumber();
                return state;
            }
            catch { state.Dispose(); throw; }
            finally { CloseClipboard(); }
        }

        public static string ReadCopiedText(IntPtr ownerWindow, uint previousSequence, uint sourcePid, CancellationToken token, out uint sequence)
        {
            sequence = 0;
            Open(ownerWindow, token);
            try
            {
                var observed = GetClipboardSequenceNumber();
                if (observed == previousSequence) return null;
                GetWindowThreadProcessId(GetClipboardOwner(), out var ownerPid);
                if (ownerPid != sourcePid) throw new InvalidOperationException("剪贴板已被其他应用更新，请重试取词。");
                // Unicode conversion may be provided lazily by the original owner.
                var handle = GetClipboardData(13);
                sequence = GetClipboardSequenceNumber();
                if (handle == IntPtr.Zero) return null;
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return null;
                try
                {
                    var length = GlobalSize(handle).ToUInt64() / 2;
                    if (length > 1_000_000) throw new InvalidOperationException("选中文字过长，请缩小选区。");
                    return Marshal.PtrToStringUni(pointer, checked((int)length)).TrimEnd('\0');
                }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }

        public bool RestoreIfCurrent(IntPtr ownerWindow, uint copiedSequence, uint sourcePid)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Open(ownerWindow, timeout.Token);
            try
            {
                GetWindowThreadProcessId(GetClipboardOwner(), out var currentPid);
                if (GetClipboardSequenceNumber() != copiedSequence || currentPid != sourcePid) return false;
                if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复原剪贴板。");
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    if (SetClipboardData(entry.Format, entry.Handle) == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复原剪贴板格式。");
                    // The operating system owns each successfully transferred handle.
                    entries[index] = (entry.Format, IntPtr.Zero);
                }
                return true;
            }
            finally { CloseClipboard(); }
        }

        private static void Open(IntPtr ownerWindow, CancellationToken token)
        {
            for (var i = 0; i < 20; i++)
            {
                token.ThrowIfCancellationRequested();
                if (OpenClipboard(ownerWindow)) return;
                Thread.Sleep(15);
            }
            throw new InvalidOperationException("剪贴板正被使用，请稍后重试。");
        }

        public void Dispose()
        {
            foreach (var entry in entries)
            {
                if (entry.Handle == IntPtr.Zero) continue;
                if (entry.Format is 2 or 9 or 0x82) DeleteObject(entry.Handle);
                else if (entry.Format is 14 or 0x8e) DeleteEnhMetaFile(entry.Handle);
                else if (entry.Format is 3 or 0x83)
                {
                    var pointer = GlobalLock(entry.Handle);
                    if (pointer != IntPtr.Zero)
                    {
                        DeleteMetaFile(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12));
                        GlobalUnlock(entry.Handle);
                    }
                    GlobalFree(entry.Handle);
                }
                else GlobalFree(entry.Handle);
            }
            entries.Clear();
        }
    }

    private sealed class ConcurrentInputGuard : IDisposable
    {
        private readonly Hook keyboard, mouse;
        private readonly IntPtr keyboardHandle, mouseHandle;
        private int userInput;
        public bool UserInput => Volatile.Read(ref userInput) != 0;
        public ConcurrentInputGuard()
        {
            keyboard = (code, message, data) => { if (code >= 0 && (Marshal.ReadInt32(data, 8) & 0x10) == 0) Volatile.Write(ref userInput, 1); return CallNextHookEx(IntPtr.Zero, code, message, data); };
            mouse = (code, message, data) => { if (code >= 0 && message.ToInt64() != 0x200 && (Marshal.ReadInt32(data, 12) & 1) == 0) Volatile.Write(ref userInput, 1); return CallNextHookEx(IntPtr.Zero, code, message, data); };
            keyboardHandle = SetWindowsHookEx(13, keyboard, GetModuleHandle(null), 0);
            mouseHandle = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0);
            if (keyboardHandle == IntPtr.Zero || mouseHandle == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("无法保护并发剪贴板操作，已停止取词兜底。"); }
        }
        public void Dispose() { if (keyboardHandle != IntPtr.Zero) UnhookWindowsHookEx(keyboardHandle); if (mouseHandle != IntPtr.Zero) UnhookWindowsHookEx(mouseHandle); }
    }

    public static string WindowTitle(IntPtr handle) { var value = new StringBuilder(1024); GetWindowText(handle, value, value.Capacity); return value.ToString(); }
    internal static bool IsOwnWindow(IntPtr window) { GetWindowThreadProcessId(window, out var pid); return pid == Environment.ProcessId; }
    internal static long Foreground => GetForegroundWindow().ToInt64();
    private static uint LastInputTime() { var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() }; return GetLastInputInfo(ref info) ? info.Time : 0; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Time; }
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo value);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr value);
    [DllImport("ole32.dll")] private static extern IntPtr OleDuplicateData(IntPtr source, uint format, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteMetaFile(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CopyEnhMetaFile(IntPtr source, string fileName);
}

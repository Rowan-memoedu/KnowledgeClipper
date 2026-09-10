using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
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

internal sealed record SelectionSnapshot(string Text, long Window, string Source, int X, int Y, DateTimeOffset CapturedAt,
    long Epoch = 0, long Session = 0, uint ProcessId = 0)
{
    internal SelectionCaptureLease Lease { get; init; }
}

internal sealed class SelectionCaptureLease
{
    private int invalidated;
    internal bool IsValid => Volatile.Read(ref invalidated) == 0;
    internal void Invalidate() => Interlocked.Exchange(ref invalidated, 1);
}

internal sealed record DesktopInput(bool Keyboard, int Message, int Key, int X, int Y, long Window, uint ProcessId, long Timestamp);
internal sealed class SelectionUnavailableException(string message) : InvalidOperationException(message);

internal sealed class LanguageDesktopCapture(string helper)
{
    private static readonly SemaphoreSlim captureGate = new(1, 1);

    public async Task<CaptureMaterial> CaptureAsync(LanguageSettings settings, SelectionSnapshot frozen = null)
    {
        var source = frozen?.Window ?? Foreground;
        var inputTime = LastInputTime();
        await captureGate.WaitAsync();
        try
        {
            if (source != Foreground || inputTime != LastInputTime())
                throw new InvalidOperationException("等待取词期间选区已变化，请重新选择。");
            return await CaptureCoreAsync(settings, frozen);
        }
        finally { captureGate.Release(); }
    }

    private async Task<CaptureMaterial> CaptureCoreAsync(LanguageSettings settings, SelectionSnapshot frozen)
    {
        var inputTime = LastInputTime();
        var before = frozen?.Window ?? GetForegroundWindow().ToInt64();
        var title = frozen?.Source ?? WindowTitle(new IntPtr(before));
        using var input = await CaptureInputMonitor.StartAsync();
        if (inputTime != LastInputTime()) throw new InvalidOperationException("取词期间检测到新的输入，请重新选择。");
        void CheckSource()
        {
            if (frozen?.Lease is { IsValid: false }) throw new InvalidOperationException("选区已变化，请重新选择文字。");
            if (input.UserInput) throw new InvalidOperationException("取词期间检测到新的输入，请重新选择。");
            if (GetForegroundWindow().ToInt64() != before) throw new InvalidOperationException("来源窗口已变化，请重新选择文字。");
        }
        CheckSource();
        string text = frozen?.Text;
        if (frozen == null)
        {
            text = (await ReadNativeSelectionAsync(settings, CancellationToken.None))?.Text;
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
            // Word completion is capture semantics, independent of whether source
            // context is enabled for card generation. The provider verifies the
            // native selection range (or an unambiguous complete document).
            new WindowsUiAutomationContextProvider().TryRead(capturedText, settings.ContextGranularity, out context);
            token.ThrowIfCancellationRequested();
            CheckSource();
            return BuildMaterial(capturedText, context, settings.ContextEnabled, title, before,
                frozen?.CapturedAt ?? DateTimeOffset.UtcNow);
        });
        CheckSource();
        return material;
    }

    internal static CaptureMaterial BuildMaterial(string text, WindowsContextSnapshot context, bool contextEnabled,
        string source, long window, DateTimeOffset capturedAt)
    {
        var completed = LanguageSelectionBoundary.Complete(text, context?.Prefix, context?.Suffix);
        var includeContext = contextEnabled && context != null;
        var material = new CaptureMaterial(completed.Text,
            includeContext ? completed.Prefix + completed.Text + completed.Suffix : null,
            includeContext ? "source" : contextEnabled ? "unavailable" : "generated_allowed",
            includeContext ? completed.Prefix.Length : null, source, window, capturedAt);
        material.Validate();
        return material;
    }

    // No context, speech, content request or draft is created while merely selecting.
    internal async Task<SelectionSnapshot> ReadSelectionAsync(LanguageSettings settings, long window, Point anchor,
        CancellationToken cancellation)
    {
        await captureGate.WaitAsync(cancellation);
        try
        {
            using var input = await CaptureInputMonitor.StartAsync();
            void Check()
            {
                cancellation.ThrowIfCancellationRequested();
                if (input.UserInput || Foreground != window || IsOwnWindow(new IntPtr(window)))
                    throw new OperationCanceledException("选区已变化。", cancellation);
            }
            Check();
            var sourcePid = WindowProcessId(window);
            var mouseWindow = WindowAt(anchor);
            var native = await ReadNativeSelectionAsync(settings, cancellation);
            Check();
            string text = native?.Text;
            // getCurrentSelection() targets the mouse window, whereas its UIA path
            // reads global focus. If those sources differ, obtain source-owned text
            // through the guarded copy path; programName cannot prove an HWND.
            var pointerMatchesSource = mouseWindow == window && WindowAt(CursorPosition) == window;
            if (string.IsNullOrWhiteSpace(text) || !pointerMatchesSource)
                text = await OnStaAsync(token => CopySelection(window, input, token), cancellation);
            Check();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var point = native != null && native.Text == text && native.PositionLevel >= 3 && native.PositionValid
                ? native.End : anchor;
            if (GetWindowRect(new IntPtr(window), out var bounds) && !Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom).Contains(point))
                point = new Point(bounds.Left + Math.Min(40, Math.Max(0, bounds.Right - bounds.Left - 1)), bounds.Top + Math.Min(60, Math.Max(0, bounds.Bottom - bounds.Top - 1)));
            return new SelectionSnapshot(text, window, WindowTitle(new IntPtr(window)), point.X, point.Y, DateTimeOffset.UtcNow,
                ProcessId: sourcePid);
        }
        finally { captureGate.Release(); }
    }

    private sealed record NativeSelection(string Text, int PositionLevel, Point End, bool PositionValid);

    private async Task<NativeSelection> ReadNativeSelectionAsync(LanguageSettings settings, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var info = new ProcessStartInfo(settings.NodePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(helper); info.ArgumentList.Add("--once");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动取词组件。");
        using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
        }
        catch (OperationCanceledException ex)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new InvalidOperationException("取词组件响应超时，请重试取词。", ex);
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("取词组件启动失败，请检查 selection-hook 运行依赖。");
        NativeSelection selection = null;
        foreach (var line in (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var result = JsonDocument.Parse(line);
            if (result.RootElement.GetProperty("event").GetString() != "selection" ||
                result.RootElement.GetProperty("data").ValueKind != JsonValueKind.Object) continue;
            var data = result.RootElement.GetProperty("data");
            var text = data.GetProperty("text").GetString();
            var level = data.TryGetProperty("posLevel", out var position) ? position.GetInt32() : 0;
            var valid = data.TryGetProperty("endBottom", out var end) && end.TryGetProperty("x", out var x) &&
                end.TryGetProperty("y", out var y) && x.TryGetInt32(out _) && y.TryGetInt32(out _);
            var point = valid ? new Point(end.GetProperty("x").GetInt32(), end.GetProperty("y").GetInt32()) : Point.Empty;
            valid &= Math.Abs((long)point.X) < 100_000 && Math.Abs((long)point.Y) < 100_000 && point.X != -99999 && point.Y != -99999;
            selection = new NativeSelection(text, level, point, valid);
        }
        return selection;
    }

    private static async Task<T> OnStaAsync<T>(Func<CancellationToken, T> action, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var token = timeout.Token;
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.TrySetResult(action(token)); } catch (Exception ex) { result.TrySetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { return await result.Task.WaitAsync(token); }
        catch (OperationCanceledException ex) { cancellation.ThrowIfCancellationRequested(); throw new InvalidOperationException("取词或上下文读取超时，请重试。", ex); }
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
            throw new SelectionUnavailableException("未取得新的选中文字；不会使用旧剪贴板内容。");
        }
        finally
        {
            if (copied != 0 && !input.UserInput) saved.RestoreIfCurrent(ownerWindow, copied, sourcePid);
        }
    }

    // No caller thread is held in a UIA/native wait. The independent message pump
    // keeps low-level input hooks live while Node or a UIA provider is working.
    internal sealed class CaptureInputMonitor : IDisposable
    {
        private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConcurrentInputGuard guard;
        private uint threadId;
        private int disposed;
        public bool UserInput => guard?.UserInput == true;
        public static async Task<CaptureInputMonitor> StartAsync(Action<DesktopInput> observer = null)
        {
            var monitor = new CaptureInputMonitor();
            var thread = new Thread(() =>
            {
                try
                {
                    using var dispatcher = new Control();
                    _ = dispatcher.Handle;
                    monitor.threadId = GetCurrentThreadId();
                    using var guard = new ConcurrentInputGuard(observer);
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
        public ConcurrentInputGuard(Action<DesktopInput> observer = null)
        {
            void Notify(bool isKeyboard, IntPtr message, IntPtr data)
            {
                if (observer == null) return;
                var window = Foreground;
                try { observer(new DesktopInput(isKeyboard, (int)message, isKeyboard ? Marshal.ReadInt32(data) : 0,
                    isKeyboard ? 0 : Marshal.ReadInt32(data), isKeyboard ? 0 : Marshal.ReadInt32(data, 4),
                    window, WindowProcessId(window), Environment.TickCount64)); } catch { }
            }
            keyboard = (code, message, data) => { if (code >= 0 && (Marshal.ReadInt32(data, 8) & 0x10) == 0) { Volatile.Write(ref userInput, 1); Notify(true, message, data); } return CallNextHookEx(IntPtr.Zero, code, message, data); };
            mouse = (code, message, data) => { if (code >= 0 && message.ToInt64() != 0x200 && (Marshal.ReadInt32(data, 12) & 1) == 0) { Volatile.Write(ref userInput, 1); Notify(false, message, data); } return CallNextHookEx(IntPtr.Zero, code, message, data); };
            keyboardHandle = SetWindowsHookEx(13, keyboard, GetModuleHandle(null), 0);
            mouseHandle = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0);
            if (keyboardHandle == IntPtr.Zero || mouseHandle == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("无法保护并发剪贴板操作，已停止取词兜底。"); }
        }
        public void Dispose() { if (keyboardHandle != IntPtr.Zero) UnhookWindowsHookEx(keyboardHandle); if (mouseHandle != IntPtr.Zero) UnhookWindowsHookEx(mouseHandle); }
    }

    public static string WindowTitle(IntPtr handle) { var value = new StringBuilder(1024); GetWindowText(handle, value, value.Capacity); return value.ToString(); }
    internal static bool IsOwnWindow(IntPtr window) { GetWindowThreadProcessId(window, out var pid); return pid == Environment.ProcessId; }
    internal static long Foreground => GetForegroundWindow().ToInt64();
    internal static uint WindowProcessId(long window) { GetWindowThreadProcessId(new IntPtr(window), out var pid); return pid; }
    internal static long WindowAt(Point point) { var window = WindowFromPoint(point); return window == IntPtr.Zero ? 0 : GetAncestor(window, 2).ToInt64(); }
    internal static Point CursorPosition => GetPhysicalCursorPos(out var point) ? point : Point.Empty;
    private static uint LastInputTime() { var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() }; return GetLastInputInfo(ref info) ? info.Time : 0; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
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

using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using KnowledgeClipper.Workflow;
using Forms = System.Windows.Forms;

// Semantic input values exercise the production reducer without SendInput, mouse
// simulation, or fabricated claims about a real application's selection.
internal static class Program
{
    private static readonly Assembly Desktop = Assembly.Load("KnowledgeClipper");
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        try
        {
            CheckState();
            CheckGestures();
            CheckInputLifecycle();
            CheckFaults(Path.GetFullPath(args[0]));
            CheckHelperDpi(Path.GetFullPath(args[0]));
            Console.WriteLine($"PASS {checks} local selection checks; real application gestures remain a separate observation.");
            return 0;
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
    private static Type Type(string name) => Desktop.GetType("KnowledgeClipper." + name, true)!;
    private static object New(string type, params object[] args) => Activator.CreateInstance(Type(type), All, null, args, null)!;
    private static object? Call(object value, string method, params object?[] args) => value.GetType().GetMethod(method, All)!.Invoke(value, args);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value);
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
    private static object Snapshot(long hwnd = 10, uint pid = 20, string text = "selected") => New("SelectionSnapshot", text, hwnd, "fixture", -40, 80, DateTimeOffset.UtcNow, 0L, 0L, pid);
    private static object Ticket(object state, long hwnd = 10, uint pid = 20) => Call(state, "BeginRead", hwnd, pid, new Point(-40, 80))!;
    private static object? Publish(object state, object ticket, object? snapshot = null, long hwnd = 10, uint pid = 20, long now = 100) => Call(state, "Publish", ticket, snapshot ?? Snapshot(), hwnd, pid, now);
    private static bool Consume(object state, object snapshot, long now = 100) => (bool)Call(state, "TryConsume", snapshot, 10L, 20u, now)!;
    private static void CheckState()
    {
        var state = New("SelectionWatchState");
        Call(state, "Start");
        var old = Ticket(state);
        Call(state, "Invalidate");
        Check(Publish(state, old) == null, "same-window new input discards a delayed old result");
        var current = Ticket(state);
        Check(Publish(state, current, hwnd: 11) == null, "foreground change rejects source rebinding");
        Check(Publish(state, current, pid: 21) == null, "reused HWND with different process is rejected");
        Check(Publish(state, current, Snapshot(11)) == null, "payload source must match the captured request");
        Check(Publish(state, current, Snapshot(text: " ")) == null, "empty selection cannot show a dot");
        var shown = Publish(state, current)!;
        Check(shown != null && (int)Property(shown, "X")! == -40, "published snapshot retains physical negative screen coordinate");
        Check(!Consume(state, Snapshot()), "equal-looking external snapshot cannot bypass the monitor");
        Check(Consume(state, shown!) && !Consume(state, shown!), "a current snapshot can be consumed exactly once");
        Check(!(bool)Call(state, "Expire", 20000L)!, "a consumed snapshot does not expire during its capture");
        var lease = Property(shown!, "Lease")!;
        Check((bool)Property(lease, "IsValid")!, "consumption keeps the frozen capture lease valid");
        Call(state, "Invalidate");
        Check(!(bool)Property(lease, "IsValid")!, "new input invalidates a consumed capture before material is read");
        shown = Publish(state, Ticket(state), now: 500)!;
        Check(!Consume(state, shown, 12500) && (bool)Call(state, "Expire", 12500L)!, "selection expiry rejects clicks and hides the dot");
        old = Ticket(state);
        Call(state, "Stop"); Call(state, "Start");
        Check(Publish(state, old) == null, "stop and restart reject results from the previous session");
    }
    private static bool Input(object tracker, bool keyboard, int message, int key, int x, int y, long time, long window = 10, bool own = false)
    {
        var input = New("DesktopInput", keyboard, message, key, x, y, window, 20u, time);
        return (bool)Call(tracker, "Observe", input, window, own)!;
    }
    private static void CheckGestures()
    {
        var tracker = New("SelectionGestureTracker");
        Input(tracker, false, 0x201, 0, 10, 10, 100);
        Call(tracker, "ForegroundChanged", 10L);
        Check(Input(tracker, false, 0x202, 0, 80, 10, 250), "mouse drag produces a read candidate");
        Input(tracker, false, 0x201, 0, 10, 10, 1000);
        Check(!Input(tracker, false, 0x202, 0, 10, 10, 1050), "single click only cancels the previous selection");
        Input(tracker, false, 0x201, 0, 10, 10, 1100);
        Check(Input(tracker, false, 0x202, 0, 10, 10, 1150), "double click produces a read candidate");
        Call(tracker, "Reset");
        Input(tracker, true, 0x100, 0xa0, 0, 0, 2000);
        Input(tracker, true, 0x100, 0x27, 0, 0, 2010);
        Check(Input(tracker, true, 0x101, 0x27, 0, 0, 2020), "Shift plus arrow queries the current selection");
        Check(Input(tracker, true, 0x101, 0xa0, 0, 0, 2030), "Shift release preserves the latest keyboard candidate");
        Call(tracker, "Reset");
        Input(tracker, true, 0x100, 0xa2, 0, 0, 3000);
        Input(tracker, true, 0x100, 0x41, 0, 0, 3010);
        Check(Input(tracker, true, 0x101, 0x41, 0, 0, 3020), "Ctrl+A queries the current selection");
        Check(Input(tracker, true, 0x101, 0xa2, 0, 0, 3030), "Control release does not drop Ctrl+A before debounce");
        Check(!Input(tracker, true, 0x101, 0x1b, 0, 0, 3040), "Escape cancels without starting a read");
        Check(!Input(tracker, true, 0x101, 0x27, 0, 0, 3050), "plain arrow cancels without starting a read");
        Input(tracker, false, 0x201, 0, 10, 10, 4000, own: true);
        Check(!Input(tracker, false, 0x202, 0, 80, 10, 4050, own: true), "own editors do not trigger selection reads");
        Input(tracker, false, 0x201, 0, 10, 10, 5000);
        Check(!Input(tracker, false, 0x202, 0, 80, 10, 5050, window: 11), "cross-window drag does not bind a new source");
    }
    private static void CheckFaults(string root)
    {
        var sequence = GetClipboardSequenceNumber();
        var timer = Stopwatch.StartNew();
        foreach (var (path, cancel, expected) in new[]
        {
            (Path.Combine(root, "tools", "KnowledgeClipper.CaptureCheck", "fixtures", "absent.cjs"), false, "启动失败"),
            (Path.Combine(root, "tools", "KnowledgeClipper.CaptureCheck", "fixtures", "hanging.cjs"), false, "响应超时"),
            (Path.Combine(root, "tools", "KnowledgeClipper.CaptureCheck", "fixtures", "hanging.cjs"), true, "canceled")
        })
        {
            var capture = New("LanguageDesktopCapture", path);
            using var cancellation = new CancellationTokenSource();
            var started = timer.ElapsedMilliseconds;
            var task = (Task)Call(capture, "ReadNativeSelectionAsync", new LanguageSettings(), cancellation.Token)!;
            if (cancel) cancellation.CancelAfter(100);
            var pulses = 0;
            while (!task.IsCompleted && timer.ElapsedMilliseconds - started < 9000) { Forms.Application.DoEvents(); pulses++; Thread.Sleep(10); }
            string? failure = null;
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { failure = "canceled"; }
            catch (Exception ex) { failure = ex.Message; }
            Check(failure?.Contains(expected) == true && pulses > 1 && timer.ElapsedMilliseconds - started < 7000,
                $"helper {expected}: caller stays responsive ({pulses} pulses, {timer.ElapsedMilliseconds - started} ms)");
        }
        Check(GetClipboardSequenceNumber() == sequence, "helper failures and cancellation preserve the clipboard");
    }
    private static void CheckInputLifecycle()
    {
        var monitorType = Type("LanguageDesktopCapture").GetNestedType("CaptureInputMonitor", BindingFlags.NonPublic)!;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var starting = (Task)monitorType.GetMethod("StartAsync", All)!.Invoke(null, [null])!;
            while (!starting.IsCompleted) { Forms.Application.DoEvents(); Thread.Sleep(10); }
            starting.GetAwaiter().GetResult();
            var monitor = starting.GetType().GetProperty("Result")!.GetValue(starting)!;
            var thread = (uint)monitorType.GetField("threadId", All)!.GetValue(monitor)!;
            var handle = OpenThread(0x100000, false, thread);
            try
            {
                Check(handle != IntPtr.Zero, $"native input guard session {attempt + 1} starts its own message pump");
                ((IDisposable)monitor).Dispose();
                var watch = Stopwatch.StartNew();
                while (WaitForSingleObject(handle, 0) == 0x102 && watch.ElapsedMilliseconds < 2000)
                { Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check(WaitForSingleObject(handle, 0) == 0, $"native input guard session {attempt + 1} releases its hooks and exits");
            }
            finally { ((IDisposable)monitor).Dispose(); if (handle != IntPtr.Zero) CloseHandle(handle); }
        }
    }
    private static void CheckHelperDpi(string root)
    {
        const string probe = "const Hook=require(process.argv[1]);const hook=new Hook();if(!hook.start({selectionPassiveMode:true,enableClipboard:false}))process.exit(2);console.log('ready');process.stdin.resume();process.stdin.on('end',()=>hook.cleanup());";
        var info = new ProcessStartInfo(new LanguageSettings().NodePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-e"); info.ArgumentList.Add(probe);
        info.ArgumentList.Add(Path.Combine(root, "helpers", "node_modules", "selection-hook"));
        using var process = Process.Start(info)!;
        try
        {
            var line = process.StandardOutput.ReadLineAsync();
            var watch = Stopwatch.StartNew();
            while (!line.IsCompleted && watch.ElapsedMilliseconds < 6000) { Forms.Application.DoEvents(); Thread.Sleep(10); }
            Check(line.IsCompletedSuccessfully && line.Result == "ready", "installed native helper starts in passive mode without reading a selection");
            Check(GetProcessDpiAwareness(process.Handle, out var awareness) == 0 && awareness == 2,
                "real helper process is per-monitor DPI aware; native screen coordinates are not DPI virtualized");
            process.StandardInput.Close();
            Check(process.WaitForExit(3000) && process.ExitCode == 0, "passive native helper shuts down cleanly");
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } }
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);
}

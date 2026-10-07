using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;
using Forms = System.Windows.Forms;

// Semantic input values exercise the production reducer without SendInput, mouse
// simulation, or fabricated claims about a real application's selection.
internal static class Program
{
    private static readonly Assembly Desktop = Assembly.Load("KnowledgeClipper");
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;
    private static readonly List<string> passedChecks = [];
    private static object? fixtureSnapshot;
    [STAThread]
    private static int Main(string[] args)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        string? failure = null;
        try
        {
            CheckState();
            CheckHoverState();
            CheckHoverMonitor();
            CheckReadRecovery();
            CheckPointerEndpoint();
            CheckWordBoundaries();
            CheckGestures();
            CheckWheelGestures();
            CheckInputLifecycle();
            CheckFaults(Path.GetFullPath(args[0]));
            CheckNativeAnchor(Path.GetFullPath(args[0]));
            CheckHelperDpi(Path.GetFullPath(args[0]));
            Console.WriteLine($"PASS {checks} local selection checks; real application gestures remain a separate observation.");
            return 0;
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            failure = ex.ToString();
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (args.Length > 1)
            {
                var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new
                {
                    passed = failure == null, checks = passedChecks, error = failure, desktop = Desktop.Location,
                    desktopSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Desktop.Location))).ToLowerInvariant(),
                    scope = "Production selection state, hover expiry, source identity and native watcher lifecycle on isolated Windows desktop; no simulated mouse or keyboard input."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
    private static Type Type(string name) => Desktop.GetType("KnowledgeClipper." + name, true)!;
    private static object New(string type, params object[] args) => Activator.CreateInstance(Type(type), All, null, args, null)!;
    private static object? Call(object value, string method, params object?[] args) => value.GetType().GetMethod(method, All)!.Invoke(value, args);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value);
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; passedChecks.Add(name); Console.WriteLine("PASS " + name); }
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
        Check(!(bool)Call(state, "Expire", 2499L)! && !Consume(state, shown, 2500) && (bool)Call(state, "Expire", 2500L)!,
            "unhovered text selection receives exactly two seconds before expiry");
        old = Ticket(state);
        Call(state, "Stop"); Call(state, "Start");
        Check(Publish(state, old) == null, "stop and restart reject results from the previous session");
    }
    private static void CheckHoverState()
    {
        var state = New("SelectionWatchState"); Call(state, "Start");
        var shown = Publish(state, Ticket(state), now: 100)!;
        Call(state, "ObservePointer", true, 500L);
        Check(!(bool)Call(state, "Expire", 50000L)! && Consume(state, shown, 50000),
            "hover keeps text selection consumable beyond the former twelve-second limit");
        Check((bool)Property(Property(shown, "Lease")!, "IsValid")!, "consumed hover selection keeps its frozen capture lease");

        shown = Publish(state, Ticket(state), now: 51000)!;
        Call(state, "ObservePointer", true, 52000L); Call(state, "ObservePointer", false, 53000L);
        Check(!(bool)Call(state, "Expire", 54999L)!, "text hover departure keeps the snapshot for a complete two seconds");
        Call(state, "ObservePointer", true, 54999L);
        Check(!(bool)Call(state, "Expire", 70000L)!, "re-entering text dot cancels its prior expiry deadline");
        Call(state, "ObservePointer", false, 71000L);
        Check(!(bool)Call(state, "Expire", 72999L)! && !Consume(state, shown, 73000) && (bool)Call(state, "Expire", 73000L)!,
            "text selection expires exactly two seconds after the final departure");
        Check(!(bool)Property(Property(shown, "Lease")!, "IsValid")!, "text hover expiry invalidates its source lease");

        shown = Publish(state, Ticket(state), now: 74000)!;
        Call(state, "ObservePointer", true, 74001L); Call(state, "Invalidate");
        Check(!Consume(state, shown, 74002) && !(bool)Property(Property(shown, "Lease")!, "IsValid")!,
            "new selection input overrides hover protection immediately");
        var replacement = Publish(state, Ticket(state), now: 75000)!;
        Check(!(bool)Call(state, "Expire", 76999L)! && (bool)Call(state, "Expire", 77000L)!,
            "replacement selection does not inherit the previous hover state");
        shown = Publish(state, Ticket(state), now: 78000)!;
        Call(state, "ObservePointer", true, 78001L);
        Check(!(bool)Call(state, "TryConsume", shown, 11L, 20u, 90000L)! && !(bool)Call(state, "TryConsume", shown, 10L, 21u, 90000L)!,
            "hover never bypasses foreground window or process identity validation");
        Call(state, "Stop");
        Check(!Consume(state, shown, 90000), "stopping text monitoring rejects a hovered snapshot");
    }
    private static void CheckHoverMonitor()
    {
        using var dispatcher = new Forms.Form(); _ = dispatcher.Handle;
        var snapshotType = Type("SelectionSnapshot");
        var callbackType = typeof(Action<>).MakeGenericType(snapshotType);
        var ignored = System.Linq.Expressions.Expression.Parameter(snapshotType);
        var callback = System.Linq.Expressions.Expression.Lambda(callbackType, System.Linq.Expressions.Expression.Empty(), ignored).Compile();
        using var monitor = (IDisposable)New("LanguageSelectionMonitor", dispatcher, "unused.cjs",
            (Func<Point, bool>)(_ => true), callback, (Action)(() => { }), (Action<string>)(_ => { }));
        var state = monitor.GetType().GetField("state", All)!.GetValue(monitor)!;
        Call(state, "Start");
        var window = (long)Type("LanguageDesktopCapture").GetProperty("Foreground", All)!.GetValue(null)!;
        var pid = (uint)Type("LanguageDesktopCapture").GetMethod("WindowProcessId", All)!.Invoke(null, [window])!;
        monitor.GetType().GetField("running", All)!.SetValue(monitor, true);
        monitor.GetType().GetField("inputStarting", All)!.SetValue(monitor, true);
        monitor.GetType().GetField("observedWindow", All)!.SetValue(monitor, window);
        monitor.GetType().GetField("observedPid", All)!.SetValue(monitor, pid);
        object Published() => Publish(state, Ticket(state, window, pid), Snapshot(window, pid), window, pid, Environment.TickCount64 - 15_000)!;
        var shown = Published();
        Call(monitor, "Tick", (object?)null);
        Check(!(bool)Call(state, "Expire", Environment.TickCount64)!, "text monitor samples hover before its timer expiry check");
        Check((bool)Call(monitor, "TryConsume", shown)!, "text monitor consumes its hovered snapshot through the normal entry point");
        shown = Published();
        Check((bool)Call(monitor, "TryConsume", shown)!, "text monitor also samples hover at click time before a delayed timer tick");
    }
    private static bool Input(object tracker, bool keyboard, int message, int key, int x, int y, long time, long window = 10, bool own = false)
    {
        var input = New("DesktopInput", keyboard, message, key, x, y, window, 20u, time);
        return (bool)Call(tracker, "Observe", input, window, own)!;
    }
    private static void CheckReadRecovery()
    {
        using var dispatcher = new Forms.Form();
        var snapshotType = Type("SelectionSnapshot");
        var callbackType = typeof(Action<>).MakeGenericType(snapshotType);
        var ignored = System.Linq.Expressions.Expression.Parameter(snapshotType);
        var callback = System.Linq.Expressions.Expression.Lambda(callbackType,
            System.Linq.Expressions.Expression.Empty(), ignored).Compile();
        using var monitor = (IDisposable)New("LanguageSelectionMonitor", dispatcher, "unused.cjs",
            (Func<Point, bool>)(_ => false), callback, (Action)(() => { }), (Action<string>)(_ => { }));
        var state = monitor.GetType().GetField("state", All)!.GetValue(monitor)!;
        Call(state, "Start");
        monitor.GetType().GetField("running", All)!.SetValue(monitor, true);
        var session = Property(state, "Session");
        var failed = Ticket(state);
        // Exercise the production ReadAsync catch/finally, replacing only the
        // external reader with a bounded failure. No native gesture is claimed.
        var reader = monitor.GetType().GetField("readSelection", All)!;
        reader.SetValue(monitor, typeof(Program).GetMethod(nameof(FailSelection), All)!
            .MakeGenericMethod(snapshotType).CreateDelegate(reader.FieldType));
        ((Task)Call(monitor, "ReadAsync", failed, new LanguageSettings(), new CancellationTokenSource())!).GetAwaiter().GetResult();
        Check((bool)Property(monitor, "IsRunning")! && Equals(session, Property(state, "Session")),
            "one helper failure preserves the enabled watcher session");
        var next = Publish(state, Ticket(state))!;
        Check(next != null && Consume(state, next), "next selection remains consumable without importing or restarting");
        Call(state, "Invalidate");
        var newer = Publish(state, Ticket(state))!;
        Call(monitor, "ReadFailed", failed, "delayed old failure");
        Check(Consume(state, newer), "delayed failure cannot invalidate a newer selection");
        var currentRead = new CancellationTokenSource();
        var reading = monitor.GetType().GetField("reading", All)!;
        var inFlight = monitor.GetType().GetField("readInFlight", All)!;
        reading.SetValue(monitor, currentRead); inFlight.SetValue(monitor, true);
        ((Task)Call(monitor, "ReadAsync", failed, new LanguageSettings(), new CancellationTokenSource())!).GetAwaiter().GetResult();
        Check(ReferenceEquals(reading.GetValue(monitor), currentRead) && (bool)inFlight.GetValue(monitor)!,
            "an old read completion cannot clear a newer read after restart");
        ((Task)Call(monitor, "ReadAsync", Ticket(state), new LanguageSettings(), currentRead)!).GetAwaiter().GetResult();
        Check(reading.GetValue(monitor) == null && !(bool)inFlight.GetValue(monitor)!, "the owning read releases its in-flight slot after failure");
        reader.SetValue(monitor, typeof(Program).GetMethod(nameof(UnresponsiveSelection), All)!
            .MakeGenericMethod(snapshotType).CreateDelegate(reader.FieldType));
        var canceled = new CancellationTokenSource(); canceled.Cancel();
        reading.SetValue(monitor, canceled); inFlight.SetValue(monitor, true);
        var canceledRead = (Task)Call(monitor, "ReadAsync", Ticket(state), new LanguageSettings(), canceled)!;
        Check(canceledRead.Wait(1000) && reading.GetValue(monitor) == null && !(bool)inFlight.GetValue(monitor)!,
            "cancellation releases the watcher even when an external reader does not complete");
    }

    private static Task<T> FailSelection<T>(LanguageSettings settings, long window, Point anchor, CancellationToken cancellation)
        => Task.FromException<T>(new InvalidOperationException("bounded helper failure"));

    private static Task<T> UnresponsiveSelection<T>(LanguageSettings settings, long window, Point anchor, CancellationToken cancellation)
        => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    private static Task<T> ReturnSelection<T>(LanguageSettings settings, long window, Point anchor, CancellationToken cancellation)
        => Task.FromResult((T)fixtureSnapshot!);

    private static void CheckPointerEndpoint()
    {
        using var dispatcher = new Forms.Form(); _ = dispatcher.Handle;
        var snapshotType = Type("SelectionSnapshot");
        var callbackType = typeof(Action<>).MakeGenericType(snapshotType);
        var parameter = System.Linq.Expressions.Expression.Parameter(snapshotType);
        var callback = System.Linq.Expressions.Expression.Lambda(callbackType,
            System.Linq.Expressions.Expression.Empty(), parameter).Compile();
        using var monitor = (IDisposable)New("LanguageSelectionMonitor", dispatcher, "unused.cjs",
            (Func<Point, bool>)(_ => false), callback, (Action)(() => { }), (Action<string>)(_ => { }));
        var monitorType = monitor.GetType();
        var state = monitorType.GetField("state", All)!.GetValue(monitor)!;
        Call(state, "Start");
        var capture = Type("LanguageDesktopCapture");
        var window = (long)capture.GetProperty("Foreground", All)!.GetValue(null)!;
        var pid = (uint)capture.GetMethod("WindowProcessId", All)!.Invoke(null, [window])!;
        monitorType.GetField("running", All)!.SetValue(monitor, true);
        monitorType.GetField("inputStarting", All)!.SetValue(monitor, true);
        monitorType.GetField("observedWindow", All)!.SetValue(monitor, window);
        monitorType.GetField("observedPid", All)!.SetValue(monitor, pid);
        monitorType.GetField("lifetime", All)!.SetValue(monitor, new CancellationTokenSource());
        monitorType.GetField("settings", All)!.SetValue(monitor, new LanguageSettings());
        var reader = monitorType.GetField("readSelection", All)!;
        reader.SetValue(monitor, typeof(Program).GetMethod(nameof(ReturnSelection), All)!
            .MakeGenericMethod(snapshotType).CreateDelegate(reader.FieldType));
        var buttonHeld = true;
        monitorType.GetField("leftButtonDown", All)!.SetValue(monitor, (Func<bool>)(() => buttonHeld));
        var pending = monitorType.GetField("pending", All)!;
        var due = monitorType.GetField("due", All)!;
        var current = state.GetType().GetField("current", All)!;
        foreach (var endpoint in new[] { new Point(73, 157), new Point(73, 17) })
        {
            Call(state, "Invalidate");
            fixtureSnapshot = Snapshot(window, pid); // UIA's logical end deliberately disagrees with mouse-up.
            var ticket = Call(state, "BeginRead", window, pid, endpoint)!;
            ticket.GetType().GetProperty("PreferPointerAnchor", All)!.SetValue(ticket, true);
            pending.SetValue(monitor, ticket);
            due.SetValue(monitor, Environment.TickCount64 - 1);
            buttonHeld = true;
            Call(monitor, "Tick", (object?)null);
            Check(ReferenceEquals(pending.GetValue(monitor), ticket) && current.GetValue(state) == null,
                "dragging does not start a selection read or show the dot");
            buttonHeld = false;
            Call(monitor, "Tick", (object?)null);
            Check(SpinWait.SpinUntil(() => current.GetValue(state) != null, 2000) && pending.GetValue(monitor) == null,
                "mouse release starts the settled selection read");
            var shown = current.GetValue(state)!;
            Check((int)Property(shown, "X")! == endpoint.X && (int)Property(shown, "Y")! == endpoint.Y,
                "mouse selection dot follows the final pointer endpoint at y=" + endpoint.Y);
        }
        Call(state, "Invalidate");
        var keyboardTicket = Call(state, "BeginRead", window, pid, new Point(73, 17))!;
        pending.SetValue(monitor, keyboardTicket);
        due.SetValue(monitor, Environment.TickCount64 - 1);
        Call(monitor, "Tick", (object?)null);
        Check(SpinWait.SpinUntil(() => current.GetValue(state) != null, 2000) &&
            (int)Property(current.GetValue(state)!, "X")! == -40 &&
            (int)Property(current.GetValue(state)!, "Y")! == 80,
            "keyboard selection retains its accessibility anchor");
        fixtureSnapshot = null;
    }

    private static void CheckNativeAnchor(string root)
    {
        var capture = New("LanguageDesktopCapture", Path.Combine(root, "tools", "KnowledgeClipper.CaptureCheck", "fixtures", "coordinates.cjs"));
        var task = (Task)Call(capture, "ReadNativeSelectionAsync", new LanguageSettings(), CancellationToken.None)!;
        while (!task.IsCompleted) { Forms.Application.DoEvents(); Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Check((Point)Property(result, "Anchor")! == new Point(-40, 120) && (bool)Property(result, "PositionValid")!,
            "native parser uses the selection top-right corner and preserves negative monitor coordinates");
    }

    private static void CheckWordBoundaries()
    {
        foreach (var (selected, prefix, suffix, expected, expectedPrefix) in new[] {
            ("ell", "Say h", "o now.", "hello", "Say "),
            ("ick brown fo", "The qu", "x jumps.", "quick brown fox", "The "),
            ("world", "hello ", ".", "world", "hello "),
            ("can", "", "'t stop.", "can't", ""),
            ("per", "a co-o", "ative effort", "co-operative", "a "),
            ("中文", "before ", " after", "中文", "before "),
            ("ell", "h", "o", "hello", ""),
            ("hello world.", "First. ", " Next.", "hello world.", "First. "),
            ("x", new string('a', 4000), " ", "x", new string('a', 4000))
        })
        {
            var result = Type("LanguageSelectionBoundary").GetMethod("Complete", All)!.Invoke(null, [selected, prefix, suffix])!;
            var actual = (string)Property(result, "Text")!;
            var before = (string)Property(result, "Prefix")!;
            var after = (string)Property(result, "Suffix")!;
            Check(actual == expected && before == expectedPrefix, "verified edge completion: " + selected);
            Check(before + actual + after == prefix + selected + suffix, "completion preserves source and exact offset: " + selected);
        }
        var unavailable = Type("LanguageSelectionBoundary").GetMethod("Complete", All)!.Invoke(null, ["ell", null, null])!;
        Check((string)Property(unavailable, "Text")! == "ell", "unverified source never guesses missing letters");
        var context = New("WindowsContextSnapshot");
        context.GetType().GetField("Prefix", All)!.SetValue(context, "Say h");
        context.GetType().GetField("Suffix", All)!.SetValue(context, "o now.");
        foreach (var enabled in new[] { true, false })
        {
            var material = (CaptureMaterial)Type("LanguageDesktopCapture").GetMethod("BuildMaterial", All)!
                .Invoke(null, ["ell", context, enabled, "verified fixture", 42L, DateTimeOffset.UtcNow, true])!;
            Check(material.SelectedText == "hello" && (enabled
                ? material.ContextText == "Say hello now." && material.TargetStart == 4 && material.ContextOrigin == "source"
                : material.ContextText == null && material.TargetStart == null && material.ContextOrigin == "generated_allowed"),
                "capture completion preserves context setting " + enabled + " and source offset");
            Check(material.LookupContextText == "Say hello now.", "meaning receives verified source context independently of card context mode " + enabled);
        }
    }

    private static void CheckGestures()
    {
        var tracker = New("SelectionGestureTracker");
        Check(!Input(tracker, false, 0x201, 0, 10, 10, 100), "press does not show a selection dot");
        Check(!Input(tracker, false, 0x200, 0, 50, 10, 180), "drag movement does not show a selection dot");
        Call(tracker, "ForegroundChanged", 10L);
        Check(Input(tracker, false, 0x202, 0, 80, 10, 250), "mouse drag produces a read candidate");
        Call(tracker, "Reset");
        Check(!Input(tracker, false, 0x201, 0, 100, 150, 500) &&
            !Input(tracker, false, 0x200, 0, 100, 100, 550) &&
            Input(tracker, false, 0x202, 0, 100, 70, 600),
            "upward drag produces a read candidate only after mouse release");
        Call(tracker, "Reset");
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
    private static void CheckWheelGestures()
    {
        foreach (var wheel in new[] { 0x20a, 0x20e })
        foreach (var endpoint in new[] { new Point(10, 10), new Point(10, 180), new Point(10, -80) })
        {
            var tracker = New("SelectionGestureTracker");
            Input(tracker, false, 0x201, 0, 10, 10, 100);
            Check(!Input(tracker, false, wheel, 0, 10, 10, 200) &&
                !Input(tracker, false, wheel, 0, endpoint.X, endpoint.Y, 300),
                $"wheel {wheel:x} never publishes during a held drag ending at {endpoint}");
            Check(Input(tracker, false, 0x202, 0, endpoint.X, endpoint.Y, 400),
                $"wheel {wheel:x} preserves selection until release at {endpoint}");
            Check(!Input(tracker, false, 0x202, 0, endpoint.X, endpoint.Y, 450),
                $"wheel {wheel:x} cannot publish twice after release at {endpoint}");
        }
        foreach (var wheel in new[] { 0x20a, 0x20e })
        {
            var tracker = New("SelectionGestureTracker");
            Check(!Input(tracker, false, wheel, 0, 10, 10, 100) &&
                !Input(tracker, false, 0x202, 0, 10, 10, 200),
                $"wheel {wheel:x} without a selection gesture does not create a candidate");
            Input(tracker, false, 0x201, 0, 10, 10, 300);
            Input(tracker, false, wheel, 0, 10, 10, 400, window: 11);
            Check(!Input(tracker, false, 0x202, 0, 80, 10, 500),
                $"wheel {wheel:x} in another source cancels the held selection");
            Input(tracker, false, 0x201, 0, 10, 10, 600);
            Input(tracker, false, wheel, 0, 10, 10, 700, own: true);
            Check(!Input(tracker, false, 0x202, 0, 80, 10, 800),
                $"wheel {wheel:x} in this application's controls cancels the selection");
            Input(tracker, false, 0x201, 0, 10, 10, 900);
            Input(tracker, false, wheel, 0, 10, 10, 1000);
            Input(tracker, false, 0x204, 0, 10, 10, 1100);
            Check(!Input(tracker, false, 0x202, 0, 80, 10, 1200),
                $"a different button cancels a wheel {wheel:x} selection");
            Input(tracker, false, 0x201, 0, 10, 10, 1300);
            Input(tracker, false, wheel, 0, 10, 10, 1400);
            Call(tracker, "ForegroundChanged", 11L);
            Check(!Input(tracker, false, 0x202, 0, 80, 10, 1500),
                $"foreground change cancels a wheel {wheel:x} selection");
            Input(tracker, false, 0x201, 0, 10, 10, 1600);
            Input(tracker, false, wheel, 0, 10, 10, 1700);
            Check(!Input(tracker, false, 0x202, 0, 10, 10, 32000),
                $"wheel {wheel:x} retains the existing gesture timeout");
            Input(tracker, false, 0x201, 0, 10, 10, 33000);
            Check(!Input(tracker, false, 0x202, 0, 10, 10, 33100),
                $"a new plain click does not inherit wheel {wheel:x} displacement");
        }
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
            var parameter = System.Linq.Expressions.Expression.Parameter(Type("DesktopInput"));
            var observer = System.Linq.Expressions.Expression.Lambda(typeof(Action<>).MakeGenericType(parameter.Type),
                System.Linq.Expressions.Expression.Empty(), parameter).Compile();
            var starting = (Task)monitorType.GetMethod("StartAsync", All)!.Invoke(null, [observer])!;
            while (!starting.IsCompleted) { Forms.Application.DoEvents(); Thread.Sleep(10); }
            starting.GetAwaiter().GetResult();
            var monitor = starting.GetType().GetProperty("Result")!.GetValue(starting)!;
            var thread = (uint)monitorType.GetField("threadId", All)!.GetValue(monitor)!;
            var handle = OpenThread(0x100000, false, thread);
            try
            {
                Check(handle != IntPtr.Zero, $"native input guard session {attempt + 1} starts its own message pump");
                var oldCount = (int)Property(monitor, "RearmCount")!;
                ((Task)Call(monitor, "RefreshAsync")!).GetAwaiter().GetResult();
                Check((bool)Property(monitor, "IsAlive")! && (int)Property(monitor, "RearmCount")! == oldCount + 1,
                    "real keyboard and mouse hooks can be renewed on the existing pump without restarting a selection session");
                var expiredGuard = monitorType.GetField("guard", All)!.GetValue(monitor)!;
                ((IDisposable)expiredGuard).Dispose();
                monitorType.GetField("installedAt", All)!.SetValue(monitor, Environment.TickCount64 - 31_000L);
                var recovery = Stopwatch.StartNew();
                while ((int)Property(monitor, "RearmCount")! == oldCount + 1 && recovery.ElapsedMilliseconds < 3500)
                { Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check((int)Property(monitor, "RearmCount")! > oldCount + 1 &&
                    !ReferenceEquals(expiredGuard, monitorType.GetField("guard", All)!.GetValue(monitor)),
                    "watchdog automatically repairs removed native hooks while the pump remains alive");
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

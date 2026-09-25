using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Workflow;

namespace KnowledgeClipper;

// Input values arrive in order on a worker. Node, clipboard and UIA work always
// run outside the hook message pump and the application's UI thread.
internal sealed class LanguageSelectionMonitor : IDisposable
{
    private readonly object gate = new();
    private readonly Form dispatcher;
    private readonly LanguageDesktopCapture capture;
    private readonly Func<LanguageSettings, long, Point, CancellationToken, Task<SelectionSnapshot>> readSelection;
    private readonly Func<Point, bool> isDotPoint;
    private readonly Action<SelectionSnapshot> selected;
    private readonly Action invalidated;
    private readonly Action<string> error;
    private readonly SelectionGestureTracker gestures = new();
    private readonly SelectionWatchState state = new();
    private Func<bool> leftButtonDown = () => LanguageDesktopCapture.LeftButtonDown;
    private LanguageDesktopCapture.CaptureInputMonitor input;
    private System.Threading.Timer timer;
    private CancellationTokenSource lifetime, reading;
    private LanguageSettings settings;
    private SelectionReadTicket pending;
    private bool running, dotClick, readInFlight;
    private bool inputStarting;
    private int inputFailures;
    private long due, observedWindow, inputRetryAt;
    private uint observedPid;
    private const int SettleMilliseconds = 220;

    internal LanguageSelectionMonitor(Form dispatcher, string helperPath, Func<Point, bool> isDotPoint,
        Action<SelectionSnapshot> selected, Action invalidated, Action<string> error)
    {
        this.dispatcher = dispatcher;
        capture = new LanguageDesktopCapture(helperPath);
        readSelection = capture.ReadSelectionAsync;
        this.isDotPoint = isDotPoint;
        this.selected = selected;
        this.invalidated = invalidated;
        this.error = error;
    }

    internal bool IsRunning { get { lock (gate) return running; } }

    internal void Start(LanguageSettings value)
    {
        Stop();
        long session;
        lock (gate)
        {
            settings = value;
            lifetime = new CancellationTokenSource();
            running = true;
            state.Start();
            session = state.Session;
            observedWindow = LanguageDesktopCapture.Foreground;
            observedPid = LanguageDesktopCapture.WindowProcessId(observedWindow);
            gestures.Reset();
            inputStarting = true; inputFailures = 0; inputRetryAt = 0;
            timer = new System.Threading.Timer(Tick, null, 80, 80);
        }
        _ = StartInputAsync(session);
    }

    private async Task StartInputAsync(long session)
    {
        try
        {
            var started = await LanguageDesktopCapture.CaptureInputMonitor.StartAsync(value => Observe(session, value));
            lock (gate)
            {
                if (!running || state.Session != session) { started.Dispose(); return; }
                input = started; inputStarting = false; inputFailures = 0;
            }
        }
        catch (Exception ex)
        {
            long epoch; bool report;
            lock (gate)
            {
                if (!running || state.Session != session) return;
                inputStarting = false; inputRetryAt = Environment.TickCount64 + 2000;
                report = ++inputFailures == 1; epoch = state.Epoch;
            }
            if (report) Post(session, epoch, () => error("选词监听暂时不可用，正在自动重连：" + ex.Message));
        }
    }

    internal void Stop()
    {
        long session, epoch;
        lock (gate)
        {
            running = false;
            state.Stop();
            session = state.Session; epoch = state.Epoch;
            pending = null; dotClick = false; inputStarting = false;
            timer?.Dispose(); timer = null;
            input?.Dispose(); input = null;
            if (lifetime != null) { _ = CancelLifetimeAsync(lifetime); lifetime = null; }
            CancelRead();
            reading = null; readInFlight = false;
            gestures.Reset();
        }
        Post(session, epoch, invalidated, requireRunning: false);
    }

    internal bool TryConsume(SelectionSnapshot snapshot)
    {
        long session, epoch;
        lock (gate)
        {
            if (!running) return false;
            var window = LanguageDesktopCapture.Foreground;
            var now = Environment.TickCount64;
            state.ObservePointer(isDotPoint(LanguageDesktopCapture.CursorPosition), now);
            if (!state.TryConsume(snapshot, window, LanguageDesktopCapture.WindowProcessId(window), now)) return false;
            pending = null;
            session = state.Session; epoch = state.Epoch;
        }
        Post(session, epoch, invalidated);
        return true;
    }

    private void Observe(long inputSession, DesktopInput value)
    {
        long session, epoch;
        lock (gate)
        {
            if (!running || state.Session != inputSession) return;
            var point = new Point(value.X, value.Y);
            // Ignore exactly the click that consumes our non-activating dot. Other
            // clicks in this process invalidate the selection without reading it.
            if (!value.Keyboard && value.Message == 0x201 && isDotPoint(point) &&
                LanguageDesktopCapture.IsOwnWindow(new IntPtr(LanguageDesktopCapture.WindowAt(point)))) { dotClick = true; return; }
            if (!value.Keyboard && value.Message == 0x202 && dotClick) { dotClick = false; return; }
            dotClick = false;
            var target = value.Keyboard ? value.Window : LanguageDesktopCapture.WindowAt(point);
            var own = target == 0 || LanguageDesktopCapture.IsOwnWindow(new IntPtr(target));
            var candidate = gestures.Observe(value, target, own);
            state.Invalidate();
            pending = null;
            CancelRead();
            session = state.Session; epoch = state.Epoch;
            observedWindow = value.Window; observedPid = value.ProcessId;
            if (candidate && !own && value.Window != 0 && value.ProcessId != Environment.ProcessId)
            {
                var anchor = value.Keyboard ? LanguageDesktopCapture.CursorPosition : point;
                pending = state.BeginRead(value.Window, value.ProcessId, anchor) with { PreferPointerAnchor = !value.Keyboard };
                due = value.Timestamp + SettleMilliseconds;
            }
        }
        Post(session, epoch, invalidated);
    }

    private void Tick(object unused)
    {
        SelectionReadTicket ticket = null;
        CancellationTokenSource cancellation = null;
        LanguageSettings configuration = null;
        long session = 0, epoch = 0;
        var hide = false;
        var restartInput = false;
        lock (gate)
        {
            if (!running) return;
            var window = LanguageDesktopCapture.Foreground;
            var pid = LanguageDesktopCapture.WindowProcessId(window);
            var now = Environment.TickCount64;
            if (input != null && !input.IsAlive)
            {
                input.Dispose(); input = null; inputRetryAt = 0;
                state.Invalidate(); pending = null; CancelRead(); gestures.Reset(); hide = true;
            }
            if (input == null && !inputStarting && now >= inputRetryAt)
            { inputStarting = true; restartInput = true; }
            if (window != observedWindow || pid != observedPid)
            {
                observedWindow = window; observedPid = pid;
                state.Invalidate(); pending = null; CancelRead(); gestures.ForegroundChanged(window); hide = true;
            }
            state.ObservePointer(isDotPoint(LanguageDesktopCapture.CursorPosition), now);
            if (state.Expire(now)) hide = true;
            session = state.Session; epoch = state.Epoch;
            if (pending != null && now >= due && !readInFlight && !leftButtonDown())
            {
                ticket = pending; pending = null;
                readInFlight = true;
                cancellation = reading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                configuration = settings;
            }
        }
        if (hide) Post(session, epoch, invalidated);
        if (restartInput) _ = StartInputAsync(session);
        if (ticket != null) _ = ReadAsync(ticket, configuration, cancellation);
    }

    private async Task ReadAsync(SelectionReadTicket ticket, LanguageSettings configuration, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await readSelection(configuration, ticket.Window, ticket.Anchor, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(12), cancellation.Token);
            if (result == null) return;
            // UIA can report the document's logical end, which is the opposite
            // end of an upward drag. The mouse-up point is the user's endpoint.
            if (ticket.PreferPointerAnchor)
                result = result with { X = ticket.Anchor.X, Y = ticket.Anchor.Y };
            SelectionSnapshot snapshot;
            lock (gate)
            {
                var window = LanguageDesktopCapture.Foreground;
                if (cancellation.IsCancellationRequested || !running || leftButtonDown()) return;
                snapshot = state.Publish(ticket, result, window, LanguageDesktopCapture.WindowProcessId(window), Environment.TickCount64);
            }
            if (snapshot != null) Post(snapshot.Session, snapshot.Epoch, () => selected(snapshot));
        }
        catch (OperationCanceledException) { }
        catch (SelectionUnavailableException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) ReadFailed(ticket, ex.Message);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(reading, cancellation)) { reading = null; readInFlight = false; }
            }
            if (!cancellation.IsCancellationRequested) await cancellation.CancelAsync();
            cancellation.Dispose();
        }
    }

    private void CancelRead()
    {
        // Kill/clipboard cancellation callbacks must never run inside a low-level hook.
        if (reading != null) _ = reading.CancelAsync();
    }

    private void ReadFailed(SelectionReadTicket ticket, string message)
    {
        long epoch;
        lock (gate)
        {
            // A helper/clipboard failure belongs to one read, not the user's
            // enabled watcher. A delayed failure must not cancel a newer gesture.
            if (!running || state.Session != ticket.Session || state.Epoch != ticket.Epoch) return;
            state.Invalidate();
            pending = null;
            epoch = state.Epoch;
        }
        Post(ticket.Session, epoch, () => {
            invalidated();
            error("本次选词未完成（监视仍开启）：" + message);
        });
    }

    private static async Task CancelLifetimeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync(); }
        finally { cancellation.Dispose(); }
    }

    private void Post(long session, long epoch, Action callback, bool requireRunning = true)
    {
        try
        {
            if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
            dispatcher.BeginInvoke((Action)(() =>
            {
                lock (gate) { if (state.Session != session || state.Epoch != epoch || requireRunning && !running) return; }
                callback();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void PostError(string message)
    {
        try { if (!dispatcher.IsDisposed && dispatcher.IsHandleCreated) dispatcher.BeginInvoke((Action)(() => error(message))); }
        catch (InvalidOperationException) { }
    }

    public void Dispose() => Stop();
}

internal sealed record SelectionReadTicket(long Session, long Epoch, long Window, uint ProcessId, Point Anchor)
{
    internal bool PreferPointerAnchor { get; init; }
}

// Shared by the native observer and its bounded semantic regression checks.
internal sealed class SelectionWatchState
{
    private static long nextSession;
    private SelectionSnapshot current;
    private SelectionCaptureLease lease;
    private readonly FloatingDotLifetime display = new();
    internal long Session { get; private set; }
    internal long Epoch { get; private set; }
    internal void Start() { Invalidate(); Session = Interlocked.Increment(ref nextSession); }
    internal void Stop() { Invalidate(); Session = Interlocked.Increment(ref nextSession); }
    internal void Invalidate() { Epoch++; lease?.Invalidate(); lease = null; current = null; display.Clear(); }
    internal SelectionReadTicket BeginRead(long window, uint pid, Point anchor) => new(Session, Epoch, window, pid, anchor);
    internal SelectionSnapshot Publish(SelectionReadTicket request, SelectionSnapshot value, long foreground, uint pid, long now)
    {
        if (request.Session != Session || request.Epoch != Epoch || request.Window != foreground || request.ProcessId != pid ||
            value.Window != request.Window || value.ProcessId != pid || string.IsNullOrWhiteSpace(value.Text)) return null;
        lease?.Invalidate();
        lease = new SelectionCaptureLease();
        display.Start(now);
        return current = value with { Session = Session, Epoch = Epoch, Lease = lease };
    }
    internal bool TryConsume(SelectionSnapshot value, long foreground, uint pid, long now)
    {
        if (current == null || !ReferenceEquals(current, value) || value.Epoch != Epoch || value.Session != Session ||
            !value.Lease.IsValid || foreground != value.Window || pid != value.ProcessId || display.IsExpired(now)) return false;
        current = null;
        display.Clear();
        return true;
    }
    internal void ObservePointer(bool inside, long now) { if (current != null) display.ObservePointer(inside, now); }
    internal bool Expire(long now) { if (current == null || !display.IsExpired(now)) return false; Invalidate(); return true; }
}

internal sealed class SelectionGestureTracker
{
    private readonly HashSet<int> pressed = new();
    private Point down, lastUp;
    private long downAt, downWindow, lastClickAt, lastClickWindow, keyboardSelectionAt;
    private bool leftDown, eligible;
    internal void Reset() { pressed.Clear(); leftDown = eligible = false; lastClickAt = keyboardSelectionAt = 0; }
    internal void ForegroundChanged(long window)
    {
        // The first drag can activate its source after mouse-down. Preserve that
        // gesture only when the new foreground is the exact mouse-down target.
        if (!leftDown || downWindow != window) Reset();
    }
    internal bool Observe(DesktopInput input, long target, bool own)
    {
        if (own) { Reset(); return false; }
        if (input.Keyboard)
        {
            var key = input.Key;
            var downEvent = input.Message is 0x100 or 0x104;
            if (downEvent) { pressed.Add(key); return false; }
            var shift = pressed.Contains(0x10) || pressed.Contains(0xa0) || pressed.Contains(0xa1);
            var control = pressed.Contains(0x11) || pressed.Contains(0xa2) || pressed.Contains(0xa3);
            var alt = pressed.Contains(0x12) || pressed.Contains(0xa4) || pressed.Contains(0xa5);
            pressed.Remove(key);
            var selection = !alt && ((key is >= 0x21 and <= 0x28 && shift) || key == 0x41 && control);
            var modifier = key is 0x10 or 0x11 or 0xa0 or 0xa1 or 0xa2 or 0xa3;
            if (selection) keyboardSelectionAt = input.Timestamp;
            else if (!modifier) keyboardSelectionAt = 0;
            return selection || modifier && keyboardSelectionAt != 0 && input.Timestamp - keyboardSelectionAt < 1_000;
        }
        keyboardSelectionAt = 0;
        var point = new Point(input.X, input.Y);
        if (input.Message == 0x200) return false;
        if (input.Message == 0x201)
        {
            down = point; downAt = input.Timestamp; downWindow = target; leftDown = true; eligible = true;
            return false;
        }
        if (input.Message != 0x202) { leftDown = eligible = false; return false; }
        var duration = input.Timestamp - downAt;
        var sameWindow = eligible && leftDown && target == downWindow && input.Window == target;
        var drag = Math.Abs(point.X - down.X) >= SystemInformation.DragSize.Width || Math.Abs(point.Y - down.Y) >= SystemInformation.DragSize.Height;
        var doubleClick = lastClickWindow == target && input.Timestamp - lastClickAt <= SystemInformation.DoubleClickTime &&
            Math.Abs(point.X - lastUp.X) <= SystemInformation.DoubleClickSize.Width && Math.Abs(point.Y - lastUp.Y) <= SystemInformation.DoubleClickSize.Height;
        var shiftClick = pressed.Contains(0x10) || pressed.Contains(0xa0) || pressed.Contains(0xa1);
        var result = sameWindow && duration is >= 0 and < 30_000 && (drag || doubleClick || shiftClick);
        lastClickAt = sameWindow && !drag ? input.Timestamp : 0;
        lastClickWindow = target; lastUp = point; leftDown = eligible = false;
        return result;
    }
}

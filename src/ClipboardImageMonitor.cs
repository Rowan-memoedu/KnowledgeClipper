using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KnowledgeClipper;

// Clipboard notifications and presentation run on the owner's UI thread. OLE
// image decoding runs on a separate STA so an unresponsive clipboard producer
// cannot hold the floating window's message pump.
internal sealed class ClipboardImageMonitor : IDisposable
{
    internal const int DisplayMilliseconds = FloatingDotLifetime.LeaveMilliseconds;
    private const int RetryMilliseconds = 60;
    private const int MaximumAttempts = 8;
    private readonly Form dispatcher;
    private readonly Action<ClipboardImageSnapshot> selected;
    private readonly Action invalidated;
    private readonly Action<string> error;
    private readonly IClipboardImageSource source;
    private readonly Func<Point, bool> isDotPoint;
    private readonly Func<Action, IDisposable> registerListener;
    private readonly Func<Uri, CancellationToken, Task<(byte[] Png, ClipboardImageOriginal Original)>> loadLink;
    private readonly FloatingDotLifetime display = new();
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private readonly System.Windows.Forms.Timer timer;
    private IDisposable listener;
    private ReadTicket pending;
    private ClipboardImageSnapshot current;
    private uint observedSequence;
    private long retryAt;
    private int suppressions;
    private bool running, disposed, readInFlight;

    internal ClipboardImageMonitor(Form dispatcher, Action<ClipboardImageSnapshot> selected,
        Action invalidated, Action<string> error = null, Func<Point, bool> isDotPoint = null,
        Func<Uri, CancellationToken, Task<(byte[] Png, ClipboardImageOriginal Original)>> loadLink = null)
        : this(dispatcher, selected, invalidated, error, new SystemClipboardImageSource(), isDotPoint: isDotPoint, loadLink: loadLink) { }

    internal ClipboardImageMonitor(Form dispatcher, Action<ClipboardImageSnapshot> selected,
        Action invalidated, Action<string> error, IClipboardImageSource source,
        Func<Action, IDisposable> registerListener = null, Func<Point, bool> isDotPoint = null,
        Func<Uri, CancellationToken, Task<(byte[] Png, ClipboardImageOriginal Original)>> loadLink = null)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.selected = selected ?? throw new ArgumentNullException(nameof(selected));
        this.invalidated = invalidated ?? throw new ArgumentNullException(nameof(invalidated));
        this.error = error;
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.isDotPoint = isDotPoint ?? (_ => false);
        this.registerListener = registerListener ?? (changed => new Listener(changed));
        this.loadLink = loadLink;
        timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => Tick();
    }

    internal bool IsRunning => running;

    internal void Start()
    {
        RequireUiThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running) return;
        // Existing clipboard contents are not a new copy. Enabling the setting
        // must not show yesterday's image or reset its display lifetime.
        observedSequence = source.SequenceNumber;
        running = true;
        try { listener = registerListener(ObserveClipboardChange); }
        catch (Win32Exception ex)
        {
            // Sequence polling below is also available when Windows temporarily
            // refuses listener registration. Keep settings saves and Form.Shown
            // alive, and report this once instead of requiring a manual toggle.
            listener = null;
            error?.Invoke("剪贴板通知暂时不可用，已改用自动检测：" + ex.Message);
        }
        timer.Start();
    }

    internal void Stop()
    {
        RequireUiThread();
        if (disposed) return;
        running = false;
        timer.Stop();
        listener?.Dispose(); listener = null;
        Invalidate();
    }

    // Screenshot collection already owns the copied image. Suppress its system
    // snip notifications until collection finishes, without disabling monitoring
    // or consuming a later independent user copy.
    internal IDisposable Suppress()
    {
        RequireUiThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        suppressions++;
        observedSequence = source.SequenceNumber;
        Invalidate();
        return new Suppression(this);
    }

    internal bool TryConsume(ClipboardImageSnapshot snapshot)
    {
        RequireUiThread();
        var now = Environment.TickCount64;
        if (current != null) display.ObservePointer(isDotPoint(source.CursorPosition), now);
        var valid = !disposed && running && suppressions == 0 && snapshot != null &&
            ReferenceEquals(current, snapshot) && !display.IsExpired(now) &&
            source.SequenceNumber == snapshot.SequenceNumber;
        if (ReferenceEquals(current, snapshot)) Invalidate();
        return valid;
    }

    private void Tick()
    {
        if (!running || disposed) return;
        // A cheap sequence check also recovers a missed notification. A new
        // sequence invalidates the old image before any asynchronous read begins.
        ObserveClipboardChange();
        if (current != null)
        {
            var now = Environment.TickCount64;
            display.ObservePointer(isDotPoint(source.CursorPosition), now);
            if (display.IsExpired(now)) Invalidate();
        }
        TryStartRead();
    }

    private void ObserveClipboardChange()
    {
        if (!running || disposed) return;
        var sequence = source.SequenceNumber;
        if (sequence == observedSequence) return;
        var anchor = source.CursorPosition;
        var observedAt = DateTimeOffset.UtcNow;
        observedSequence = sequence;
        Invalidate();
        // Native selection restoration owns its clipboard via an HWND in this
        // process. Ignoring that owner avoids resurrecting an earlier image each
        // time a text-only application needs Ctrl+C selection capture.
        if (suppressions != 0 || source.IsOwnedByCurrentProcess) return;
        pending = new ReadTicket(sequence, anchor, observedAt);
        retryAt = 0;
        TryStartRead();
    }

    private void TryStartRead()
    {
        if (!running || disposed || suppressions != 0 || readInFlight || pending == null || pending.Resolving ||
            Environment.TickCount64 < retryAt) return;
        var ticket = pending;
        ticket.Attempts++;
        readInFlight = true;
        _ = ReadAsync(ticket);
    }

    private async Task ReadAsync(ReadTicket ticket)
    {
        var result = new TaskCompletionSource<(byte[] Png, Uri Link)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Thread(() =>
        {
            try
            {
                if (source.SequenceNumber != ticket.Sequence || source.IsOwnedByCurrentProcess)
                    result.TrySetResult((null, null));
                else
                {
                    var pixels = source.ReadPng();
                    result.TrySetResult((pixels, pixels == null && loadLink != null ? source.ReadImageLink() : null));
                }
            }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true, Name = "KnowledgeClipper clipboard image" };
        reader.SetApartmentState(ApartmentState.STA);
        reader.Start();
        byte[] png = null;
        Uri link = null;
        Exception failure = null;
        try { (png, link) = await result.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        Post(() => FinishRead(ticket, png, link, failure));
    }

    private void FinishRead(ReadTicket ticket, byte[] png, Uri link, Exception failure)
    {
        readInFlight = false;
        if (disposed || !running) { ticket.Cancellation.Dispose(); return; }
        ObserveClipboardChange();
        if (!ReferenceEquals(ticket, pending)) { ticket.Cancellation.Dispose(); TryStartRead(); return; }
        if (suppressions != 0 || source.SequenceNumber != ticket.Sequence || source.IsOwnedByCurrentProcess)
        { Invalidate(); ticket.Cancellation.Dispose(); return; }
        if (failure is ExternalException && ticket.Attempts < MaximumAttempts)
        { retryAt = Environment.TickCount64 + RetryMilliseconds; return; }
        if (failure == null && png == null && link != null)
        {
            // Download outside the clipboard STA and its two-second read limit.
            // A newer normal image can be read immediately while this is cancelled.
            ticket.Resolving = true;
            _ = ResolveLinkAsync(ticket, link);
            return;
        }
        FinishImage(ticket, png, null, failure);
    }

    private async Task ResolveLinkAsync(ReadTicket ticket, Uri link)
    {
        byte[] png = null; ClipboardImageOriginal original = null; Exception failure = null;
        try { (png, original) = await Task.Run(() => loadLink(link, ticket.Cancellation.Token)).ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        Post(() => FinishImage(ticket, png, original, failure));
    }

    private void FinishImage(ReadTicket ticket, byte[] png, ClipboardImageOriginal original, Exception failure)
    {
        ticket.Cancellation.Dispose();
        if (disposed || !running || !ReferenceEquals(ticket, pending)) return;
        if (suppressions != 0 || source.SequenceNumber != ticket.Sequence || source.IsOwnedByCurrentProcess)
        { pending = null; Invalidate(); return; }
        pending = null;
        if (failure != null)
        {
            error?.Invoke("暂时无法读取剪贴板图片，请重新复制：" + failure.Message);
            return;
        }
        if (png == null || png.Length == 0) return;
        var snapshot = new ClipboardImageSnapshot(ticket.Sequence, ticket.Anchor, ticket.ObservedAt, png, original);
        current = snapshot;
        display.Start(Environment.TickCount64);
        selected(snapshot);
        if (ReferenceEquals(current, snapshot))
        {
            // Native window creation can itself take seconds. The user receives
            // the full existing lifetime after the surface is actually shown.
            display.Start(Environment.TickCount64);
            display.ObservePointer(isDotPoint(source.CursorPosition), Environment.TickCount64);
        }
    }

    private void Invalidate()
    {
        pending?.Cancellation.Cancel();
        pending = null;
        current = null;
        display.Clear();
        invalidated();
    }

    private void ReleaseSuppression()
    {
        if (disposed) return;
        if (Environment.CurrentManagedThreadId != uiThread) { Post(ReleaseSuppression); return; }
        if (suppressions == 0) return;
        suppressions--;
        // Drain the exact sequence produced during collection. A queued update
        // for that sequence must not show a dot after the collector closes.
        observedSequence = source.SequenceNumber;
        Invalidate();
    }

    private void Post(Action action)
    {
        if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
        try { dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void RequireUiThread()
    {
        if (Environment.CurrentManagedThreadId != uiThread)
            throw new InvalidOperationException("剪贴板浮点监听必须在窗口线程启停。");
    }

    public void Dispose()
    {
        if (disposed) return;
        RequireUiThread();
        Stop();
        disposed = true;
        timer.Dispose();
    }

    private sealed class ReadTicket(uint sequence, Point anchor, DateTimeOffset observedAt)
    {
        internal uint Sequence { get; } = sequence;
        internal Point Anchor { get; } = anchor;
        internal DateTimeOffset ObservedAt { get; } = observedAt;
        internal int Attempts { get; set; }
        internal bool Resolving { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed class Suppression(ClipboardImageMonitor owner) : IDisposable
    {
        private ClipboardImageMonitor owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseSuppression();
    }

    private sealed class Listener : NativeWindow, IDisposable
    {
        private readonly Action changed;
        internal Listener(Action changed)
        {
            this.changed = changed;
            CreateHandle(new CreateParams { Caption = "KnowledgeClipper clipboard image listener", Parent = new IntPtr(-3) });
            if (!AddClipboardFormatListener(Handle))
            {
                var failure = new Win32Exception(Marshal.GetLastWin32Error(), "无法监听剪贴板图片。");
                DestroyHandle();
                throw failure;
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x031D) changed();
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr window);
    }
}

internal interface IClipboardImageSource
{
    uint SequenceNumber { get; }
    Point CursorPosition { get; }
    bool IsOwnedByCurrentProcess { get; }
    byte[] ReadPng();
    Uri ReadImageLink() => null;
}

internal sealed class SystemClipboardImageSource : IClipboardImageSource
{
    public uint SequenceNumber => GetClipboardSequenceNumber();
    public Point CursorPosition => Cursor.Position;
    public bool IsOwnedByCurrentProcess
    {
        get
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return false;
            GetWindowThreadProcessId(owner, out var pid);
            return pid == Environment.ProcessId;
        }
    }

    public byte[] ReadPng()
        => EncodeDataObject(Clipboard.GetDataObject());
    public Uri ReadImageLink() => ClipboardImageLink.FromDataObject(Clipboard.GetDataObject());

    internal static byte[] EncodeDataObject(IDataObject data)
    {
        if (data == null) return null;
        // Applications can expose PNG in addition to DIB. Prefer it to retain
        // transparency, then use WinForms' existing bitmap/DIB conversion.
        if (data.GetDataPresent("PNG", false))
        {
            var png = data.GetData("PNG", false);
            if (png is byte[] bytes)
            {
                using var input = new MemoryStream(bytes, false);
                using var image = Image.FromStream(input);
                return Encode(image);
            }
            if (png is Stream stream)
            {
                using (stream)
                {
                    if (stream.CanSeek) stream.Position = 0;
                    using var image = Image.FromStream(stream);
                    return Encode(image);
                }
            }
        }
        if (!data.GetDataPresent(DataFormats.Bitmap, true)) return null;
        using var bitmap = data.GetData(DataFormats.Bitmap, true) as Image;
        return bitmap == null ? null : Encode(bitmap);
    }

    private static byte[] Encode(Image image)
    {
        using var output = new MemoryStream();
        image.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}

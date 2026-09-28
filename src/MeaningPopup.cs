using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KnowledgeClipper;

internal sealed class MeaningPopupLifetime
{
    internal const int LoadingMilliseconds = 60000;
    internal const int ResultMilliseconds = 6000;
    private bool loading, hovered;
    private long expiresAt;
    internal void Start(bool isLoading, long now)
    {
        loading = isLoading; hovered = false;
        expiresAt = now + (loading ? LoadingMilliseconds : ResultMilliseconds);
    }
    internal bool Observe(bool inside, long now)
    {
        if (loading) return now >= expiresAt;
        if (inside) { hovered = true; return false; }
        if (hovered) { hovered = false; expiresAt = now + ResultMilliseconds; }
        return now >= expiresAt;
    }
}

internal readonly record struct MeaningPopupFocus(IntPtr Window, IntPtr Control);

// Owns presentation only. The caller owns request generations and discards
// responses once IsRequestVisible becomes false.
internal sealed class MeaningPopup : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly MeaningPopupLifetime lifetime = new();
    private readonly Func<MeaningPopupFocus> readFocus;
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    private MouseHook mouseHookCallback;
    private IntPtr mouseHook;
    private sealed record HitArea(Rectangle Bounds);
    private HitArea hitArea;
    private string meaning = "";
    private Point anchor;
    private bool requestVisible;
    private MeaningPopupFocus focusWhenShown;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, MouseHook callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    internal bool IsRequestVisible => !IsDisposed && requestVisible && Visible;
    internal event Action ReplayRequested;
    internal bool ContainsScreenPoint(Point point) => Volatile.Read(ref hitArea)?.Bounds.Contains(point) == true;

    internal MeaningPopup(Func<MeaningPopupFocus> readFocus = null)
    {
        this.readFocus = readFocus ?? CaptureNativeFocus;
        Name = AccessibleName = "knowledge-clipper.language.meaning-popup";
        Text = "当前句意";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; TabStop = false;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(231, 237, 245);
        ForeColor = Color.FromArgb(38, 90, 150);
        Font = new Font("Microsoft YaHei UI", 9, FontStyle.Regular, GraphicsUnit.Point);
        DoubleBuffered = true;
        timer.Tick += (_, _) =>
        {
            if (FocusMoved() || lifetime.Observe(ContainsScreenPoint(Cursor.Position), Environment.TickCount64)) HideMeaning();
        };
        LocationChanged += (_, _) => PublishHitArea();
        VisibleChanged += (_, _) => { if (!Visible) { requestVisible = false; timer.Stop(); } PublishHitArea(); };
        DpiChanged += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)(() => { if (!IsDisposed) LayoutMeaning(); })); };
    }

    internal void ShowLoading(Point point) => Present("正在查询…", point, true);
    internal void ShowMeaning(string text, Point point) => Present(text, point, false);
    internal void ShowError(string text, Point point) => Present(text, point, false);
    internal void HideMeaning()
    {
        requestVisible = false; timer.Stop(); focusWhenShown = default;
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        Volatile.Write(ref hitArea, null);
        if (!IsDisposed) Hide();
    }

    private void Present(string text, Point point, bool loading)
    {
        if (IsDisposed) return;
        meaning = string.IsNullOrWhiteSpace(text) ? "暂无释义" : text.Trim();
        AccessibleDescription = meaning;
        anchor = point;
        LayoutMeaning();
        if (!Visible)
        {
            focusWhenShown = readFocus();
            Show();
        }
        LayoutMeaning();
        requestVisible = true;
        lifetime.Start(loading, Environment.TickCount64);
        PublishHitArea(); StartOutsideClickWatcher(); timer.Start(); Invalidate();
    }

    private void StartOutsideClickWatcher()
    {
        if (mouseHook != IntPtr.Zero) return;
        mouseHookCallback ??= (code, message, data) =>
        {
            try
            {
                if (code >= 0 && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
                    PointerDown(new Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4)));
            }
            catch (InvalidOperationException) { /* The popup handle was closed during this mouse event. */ }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        mouseHook = SetWindowsHookEx(14, mouseHookCallback, GetModuleHandle(null), 0);
    }

    internal void PointerDown(Point point)
    {
        if (IsRequestVisible && !ContainsScreenPoint(point) && IsHandleCreated)
            BeginInvoke((Action)(() => { if (!IsDisposed && IsRequestVisible) HideMeaning(); }));
    }

    private static IntPtr ForegroundFocus(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var thread = GetWindowThreadProcessId(foreground, out _);
        if (thread == 0) return IntPtr.Zero;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    private static MeaningPopupFocus CaptureNativeFocus()
    {
        var foreground = GetForegroundWindow();
        return new(foreground, ForegroundFocus(foreground));
    }

    private bool FocusMoved()
    {
        if (focusWhenShown.Window == IntPtr.Zero) return false;
        var current = readFocus();
        if (current.Window != IntPtr.Zero && current.Window != focusWhenShown.Window) return true;
        return focusWhenShown.Control != IntPtr.Zero && current.Control != IntPtr.Zero &&
            current.Control != focusWhenShown.Control;
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96.0));
    private void LayoutMeaning()
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        var padding = Scale(10);
        var maxWidth = Math.Max(1, Math.Min(Scale(320), area.Width) - 2 * padding);
        var measured = TextRenderer.MeasureText(meaning, Font, new Size(maxWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        ClientSize = new Size(Math.Min(area.Width, Math.Max(Scale(70), measured.Width + 2 * padding)),
            Math.Min(area.Height, Math.Max(Scale(32), measured.Height + 2 * padding)));
        var gap = Scale(3);
        var x = anchor.X + gap; var y = anchor.Y - Height - gap;
        if (x + Width > area.Right) x = anchor.X - Width - gap;
        if (y < area.Top) y = anchor.Y + gap;
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        using var path = new GraphicsPath();
        var diameter = Math.Min(Scale(12), Math.Min(Width, Height));
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(Width - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(Width - diameter, Height - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, Height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        var old = Region; Region = new Region(path); old?.Dispose();
        PublishHitArea();
    }

    private void PublishHitArea() => Volatile.Write(ref hitArea, IsRequestVisible ? new HitArea(Bounds) : null);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var padding = Scale(10);
        TextRenderer.DrawText(e.Graphics, meaning, Font,
            Rectangle.Inflate(ClientRectangle, -padding, -padding), ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && IsRequestVisible) ReplayRequested?.Invoke();
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            requestVisible = false; Volatile.Write(ref hitArea, null);
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            timer.Stop(); timer.Dispose(); Font.Dispose();
        }
        base.Dispose(disposing);
    }
}

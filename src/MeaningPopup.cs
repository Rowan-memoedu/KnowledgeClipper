using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

// Owns presentation only. The caller owns request generations and discards
// responses once IsRequestVisible becomes false.
internal sealed class MeaningPopup : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly MeaningPopupLifetime lifetime = new();
    private sealed record HitArea(Rectangle Bounds);
    private HitArea hitArea;
    private string meaning = "";
    private Point anchor;
    private bool requestVisible;
    internal bool IsRequestVisible => !IsDisposed && requestVisible && Visible;
    internal bool ContainsScreenPoint(Point point) => Volatile.Read(ref hitArea)?.Bounds.Contains(point) == true;

    internal MeaningPopup()
    {
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
            if (lifetime.Observe(ContainsScreenPoint(Cursor.Position), Environment.TickCount64)) HideMeaning();
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
        requestVisible = false; timer.Stop(); Volatile.Write(ref hitArea, null);
        if (!IsDisposed) Hide();
    }

    private void Present(string text, Point point, bool loading)
    {
        if (IsDisposed) return;
        meaning = string.IsNullOrWhiteSpace(text) ? "暂无释义" : text.Trim();
        AccessibleDescription = meaning;
        anchor = point;
        LayoutMeaning();
        if (!Visible) Show();
        LayoutMeaning();
        requestVisible = true;
        lifetime.Start(loading, Environment.TickCount64);
        PublishHitArea(); timer.Start(); Invalidate();
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
        if (disposing) { requestVisible = false; Volatile.Write(ref hitArea, null); timer.Stop(); timer.Dispose(); Font.Dispose(); }
        base.Dispose(disposing);
    }
}

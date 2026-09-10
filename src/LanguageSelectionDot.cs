using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace KnowledgeClipper;

internal sealed class LanguageSelectionDot : Form
{
    private readonly ToolTip tip = new();
    private sealed record HitArea(Rectangle Bounds);
    private HitArea hitArea;
    internal SelectionSnapshot Selection { get; private set; }
    internal bool ContainsScreenPoint(Point point) => Volatile.Read(ref hitArea)?.Bounds.Contains(point) == true;

    internal LanguageSelectionDot(Action<SelectionSnapshot> read)
    {
        Name = AccessibleName = "knowledge-clipper.language.selection-dot";
        Text = "选词朗读";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(36, 36);
        var button = new Button {
            Text = "读", Name = "knowledge-clipper.language.selection-dot.read",
            AccessibleName = "knowledge-clipper.language.selection-dot.read",
            Dock = DockStyle.Fill, TabStop = false, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 90, 150), ForeColor = Color.White,
            Font = new Font("微软雅黑", 11, FontStyle.Bold), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        tip.SetToolTip(button, "朗读本次选区并准备语言卡");
        button.Click += (_, _) => { if (Selection is { } selection) read(selection); };
        Controls.Add(button);
        SizeChanged += (_, _) => { RoundWindow(); PublishHitArea(); };
        LocationChanged += (_, _) => PublishHitArea();
        VisibleChanged += (_, _) => PublishHitArea();
        DpiChanged += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)PlaceAtSelection); };
        RoundWindow();
    }

    internal void ShowSelection(SelectionSnapshot selection)
    {
        if (IsDisposed) return;
        Selection = selection;
        PlaceAtSelection();
        if (!Visible) Show();
        // Showing on another display may change the physical size.
        PlaceAtSelection();
    }

    internal void HideSelection()
    {
        Selection = null;
        Volatile.Write(ref hitArea, null);
        if (!IsDisposed) Hide();
    }

    private void PublishHitArea() => Volatile.Write(ref hitArea,
        !IsDisposed && Visible && Selection != null ? new HitArea(Bounds) : null);

    private void PlaceAtSelection()
    {
        if (Selection == null || IsDisposed) return;
        var anchor = new Point(Selection.X, Selection.Y);
        var area = Screen.FromPoint(anchor).WorkingArea;
        var gap = Math.Max(6, 8 * DeviceDpi / 96);
        var x = anchor.X + gap; var y = anchor.Y + gap;
        if (x + Width > area.Right) x = anchor.X - Width - gap;
        if (y + Height > area.Bottom) y = anchor.Y - Height - gap;
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void RoundWindow()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = new GraphicsPath(); path.AddEllipse(ClientRectangle);
        var old = Region; Region = new Region(path); old?.Dispose();
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
        if (disposing) { Volatile.Write(ref hitArea, null); tip.Dispose(); }
        base.Dispose(disposing);
    }
}

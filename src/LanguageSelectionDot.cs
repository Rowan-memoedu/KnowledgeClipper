using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace KnowledgeClipper;

internal enum SelectionDotAction { Read, Excerpt, Question }

internal sealed class LanguageSelectionDot : Form
{
    private readonly ToolTip tip = new();
    private readonly Button left;
    private readonly Button question;
    private SelectionDotAction leftAction;
    private sealed record HitArea(Rectangle Bounds);
    private HitArea hitArea;
    internal SelectionSnapshot Selection { get; private set; }
    internal bool ContainsScreenPoint(Point point) => Volatile.Read(ref hitArea)?.Bounds.Contains(point) == true;

    internal SelectionDotAction LeftAction
    {
        get => leftAction;
        set
        {
            if (value is not (SelectionDotAction.Read or SelectionDotAction.Excerpt))
                throw new ArgumentOutOfRangeException(nameof(value));
            leftAction = value;
            left.Text = value == SelectionDotAction.Read ? "读" : "摘";
            tip.SetToolTip(left, value == SelectionDotAction.Read ? "朗读本次选区" : "摘录本次选区（不朗读）");
        }
    }

    internal LanguageSelectionDot(Action<SelectionSnapshot> read)
        : this((selection, _) => read(selection)) { }

    internal LanguageSelectionDot(Action<SelectionSnapshot, SelectionDotAction> invoke)
    {
        Name = AccessibleName = "knowledge-clipper.language.selection-dot";
        Text = "选词操作";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // This tiny custom surface owns all pixel bounds; use a single DPI
        // conversion instead of mixing WinForms autoscaling with manual layout.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(231, 237, 245);
        ClientSize = new Size(90, 46);
        left = new CircleButton("读", "read");
        question = new CircleButton("问", "question");
        LeftAction = SelectionDotAction.Read;
        tip.SetToolTip(question, "解释本次选区并继续问答");
        left.Click += (_, _) => { if (Selection is { } selection) invoke(selection, LeftAction); };
        question.Click += (_, _) => { if (Selection is { } selection) invoke(selection, SelectionDotAction.Question); };
        Controls.Add(left); Controls.Add(question);
        SizeChanged += (_, _) => { LayoutActions(); RoundWindow(); PublishHitArea(); };
        LocationChanged += (_, _) => PublishHitArea();
        VisibleChanged += (_, _) => PublishHitArea();
        DpiChanged += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)(() => { ApplyDpiMetrics(); PlaceAtSelection(); })); };
        ApplyDpiMetrics();
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
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = new GraphicsPath();
        var diameter = Math.Min(ClientSize.Width, ClientSize.Height);
        path.AddArc(0, 0, diameter, diameter, 90, 180);
        path.AddArc(ClientSize.Width - diameter, 0, diameter, diameter, 270, 180);
        path.CloseFigure();
        var old = Region; Region = new Region(path); old?.Dispose();
    }

    private void LayoutActions()
    {
        if (left == null || question == null) return;
        var inset = Math.Max(3, (int)Math.Round(4.0 * DeviceDpi / 96));
        var diameter = Math.Min(ClientSize.Height - 2 * inset, (ClientSize.Width - 3 * inset) / 2);
        if (diameter <= 0) return;
        var top = (ClientSize.Height - diameter) / 2;
        left.Bounds = new Rectangle(inset, top, diameter, diameter);
        question.Bounds = new Rectangle(ClientSize.Width - inset - diameter, top, diameter, diameter);
    }

    private void ApplyDpiMetrics()
    {
        if (IsDisposed) return;
        ClientSize = new Size((int)Math.Round(90.0 * DeviceDpi / 96), (int)Math.Round(46.0 * DeviceDpi / 96));
        LayoutActions(); RoundWindow(); PublishHitArea();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpiMetrics();
    }

    private sealed class CircleButton : Button
    {
        private bool hover;
        private bool pressed;
        internal CircleButton(string text, string action)
        {
            Text = text;
            Name = AccessibleName = "knowledge-clipper.language.selection-dot." + action;
            TabStop = false; FlatStyle = FlatStyle.Flat;
            BackColor = Color.FromArgb(38, 90, 150); ForeColor = Color.White;
            Font = new Font("宋体", 13, FontStyle.Regular, GraphicsUnit.Point);
            Cursor = Cursors.Hand;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0 || Height <= 0) return;
            using var path = new GraphicsPath(); path.AddEllipse(ClientRectangle);
            var old = Region; Region = new Region(path); old?.Dispose();
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(pressed ? Color.FromArgb(25, 65, 112) : hover ? Color.FromArgb(48, 107, 175) : BackColor);
            e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
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

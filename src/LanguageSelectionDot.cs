using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace KnowledgeClipper;

internal enum SelectionDotAction { Read, Excerpt, Question, Ask }
internal enum ClipboardImageDotAction { Image, Excerpt, Read, Question, Ask }

// Both floating surfaces keep their snapshot while hovered. Only uninterrupted
// time outside the surface counts towards expiry; a new snapshot starts fresh.
internal sealed class FloatingDotLifetime
{
    internal const int LeaveMilliseconds = 2000;
    private bool hovered;
    private long expiresAt;
    internal void Start(long now) { hovered = false; expiresAt = now + LeaveMilliseconds; }
    internal void Clear() { hovered = false; expiresAt = 0; }
    internal void ObservePointer(bool inside, long now)
    {
        if (inside) { hovered = true; return; }
        if (hovered) { hovered = false; expiresAt = now + LeaveMilliseconds; }
    }
    internal bool IsExpired(long now) => !hovered && now >= expiresAt;
}

// Each notification owns encoded pixels rather than a live clipboard handle.
// Continuing a capture after another copy therefore keeps the original image.
internal sealed class ClipboardImageSnapshot
{
    private readonly byte[] png;
    internal uint SequenceNumber { get; }
    internal Point Anchor { get; }
    internal DateTimeOffset CapturedAt { get; }
    internal ReadOnlyMemory<byte> PngBytes => png;

    internal ClipboardImageSnapshot(uint sequenceNumber, Point anchor, DateTimeOffset capturedAt, byte[] png)
    {
        if (png == null || png.Length == 0) throw new ArgumentException("图片内容为空。", nameof(png));
        SequenceNumber = sequenceNumber; Anchor = anchor; CapturedAt = capturedAt;
        this.png = (byte[])png.Clone();
    }

    internal void SavePng(string path) => File.WriteAllBytes(path, png);
}

internal sealed class LanguageSelectionDot : Form
{
    private readonly ToolTip tip = new();
    private readonly Button left;
    private readonly Button read;
    private readonly Button question;
    private readonly Button excerpt;
    private readonly Button ask;
    private readonly bool imageMode;
    private SelectionDotAction leftAction;
    private sealed record HitArea(Rectangle Bounds);
    private HitArea hitArea;
    internal SelectionSnapshot Selection { get; private set; }
    internal ClipboardImageSnapshot ImageSelection { get; private set; }
    internal bool ContainsScreenPoint(Point point) => Volatile.Read(ref hitArea)?.Bounds.Contains(point) == true;

    internal SelectionDotAction LeftAction
    {
        get => leftAction;
        set
        {
            if (imageMode) throw new InvalidOperationException("图片浮点不使用文字操作。");
            if (value is not (SelectionDotAction.Read or SelectionDotAction.Excerpt))
                throw new ArgumentOutOfRangeException(nameof(value));
            leftAction = value;
            left.Text = value == SelectionDotAction.Read ? "读" : "摘";
            tip.SetToolTip(left, value == SelectionDotAction.Read ? "语言学习（是否朗读由设置决定）" : "摘录本次选区");
        }
    }

    internal LanguageSelectionDot(Action<SelectionSnapshot> read)
        : this((selection, _) => read(selection)) { }

    internal LanguageSelectionDot(Action<SelectionSnapshot, SelectionDotAction> invoke)
        : this(invoke, null) { }

    internal static LanguageSelectionDot ForImages(Action<ClipboardImageSnapshot, ClipboardImageDotAction> invoke)
        => new(null, invoke ?? throw new ArgumentNullException(nameof(invoke)));

    private LanguageSelectionDot(Action<SelectionSnapshot, SelectionDotAction> invoke,
        Action<ClipboardImageSnapshot, ClipboardImageDotAction> imageInvoke)
    {
        imageMode = imageInvoke != null;
        Name = AccessibleName = imageMode ? "knowledge-clipper.image.selection-dot" : "knowledge-clipper.language.selection-dot";
        Text = imageMode ? "图片操作" : "选词操作";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // This tiny custom surface owns all pixel bounds; use a single DPI
        // conversion instead of mixing WinForms autoscaling with manual layout.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(231, 237, 245);
        ClientSize = new Size(imageMode ? 110 : 88, 23);
        left = new CircleButton(imageMode ? "图" : "读", imageMode ? "image" : "read", Name + ".");
        read = imageMode ? new CircleButton("读", "read", Name + ".") : null;
        excerpt = new CircleButton("摘", "excerpt", Name + ".");
        question = new CircleButton("卡", "question", Name + ".");
        ask = new CircleButton("问", "ask", Name + ".");
        tip.SetToolTip(ask, "用默认 API 模型和快速问答直接提问");
        if (imageMode)
        {
            left.Click += (_, _) => { if (ImageSelection is { } snapshot) imageInvoke(snapshot, ClipboardImageDotAction.Image); };
            read.Click += (_, _) => { if (ImageSelection is { } snapshot) imageInvoke(snapshot, ClipboardImageDotAction.Read); };
            question.Click += (_, _) => { if (ImageSelection is { } snapshot) imageInvoke(snapshot, ClipboardImageDotAction.Question); };
            ask.Click += (_, _) => { if (ImageSelection is { } snapshot) imageInvoke(snapshot, ClipboardImageDotAction.Ask); };
            excerpt.Click += (_, _) => { if (ImageSelection is { } snapshot) imageInvoke(snapshot, ClipboardImageDotAction.Excerpt); };
            ApplyQuickModes(false, false, false, false);
        }
        else
        {
            LeftAction = SelectionDotAction.Read;
            tip.SetToolTip(question, "解释本次选区并继续问答");
            tip.SetToolTip(excerpt, "摘录原文并制卡");
            left.Click += (_, _) => { if (Selection is { } selection) invoke(selection, LeftAction); };
            excerpt.Click += (_, _) => { if (Selection is { } selection) invoke(selection, SelectionDotAction.Excerpt); };
            question.Click += (_, _) => { if (Selection is { } selection) invoke(selection, SelectionDotAction.Question); };
            ask.Click += (_, _) => { if (Selection is { } selection) invoke(selection, SelectionDotAction.Ask); };
        }
        Controls.Add(left); if (imageMode) Controls.Add(read); Controls.Add(question); Controls.Add(excerpt);
        Controls.Add(ask);
        SizeChanged += (_, _) => { LayoutActions(); RoundWindow(); PublishHitArea(); };
        LocationChanged += (_, _) => PublishHitArea();
        VisibleChanged += (_, _) => PublishHitArea();
        DpiChanged += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)(() => { ApplyDpiMetrics(); PlaceAtSelection(); })); };
        ApplyDpiMetrics();
    }

    internal void ShowSelection(SelectionSnapshot selection)
    {
        if (IsDisposed) return;
        if (imageMode) throw new InvalidOperationException("图片浮点需要图片快照。");
        Selection = selection;
        PlaceAtSelection();
        if (!Visible) Show();
        // Showing on another display may change the physical size.
        PlaceAtSelection();
    }

    internal void ShowImage(ClipboardImageSnapshot snapshot)
    {
        if (IsDisposed) return;
        if (!imageMode) throw new InvalidOperationException("文字浮点不显示图片操作。");
        ImageSelection = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        PlaceAtSelection();
        if (!Visible) Show();
        PlaceAtSelection();
    }

    internal void HideSelection()
    {
        Selection = null;
        ImageSelection = null;
        Volatile.Write(ref hitArea, null);
        if (!IsDisposed) Hide();
    }

    internal void HideImage() => HideSelection();

    internal void ApplyQuickModes(bool language, bool question, bool excerpt, bool screenshot)
    {
        if (imageMode)
        {
            // The legacy screenshot shortcut setting is retained in storage;
            // this action always opens collection with the frozen first image.
            tip.SetToolTip(left, "用这张图片进入多图模式");
            tip.SetToolTip(read, language ? "后台处理这张图片并导入语言卡" : "用这张图片进行语言学习（是否朗读由设置决定）");
            tip.SetToolTip(this.question, question ? "后台处理这张图片并导入一轮回答" : "用这张图片提问");
            tip.SetToolTip(this.excerpt, excerpt ? "后台摘录这张图片并导入" : "摘录这张图片并制卡");
            return;
        }
        tip.SetToolTip(left, LeftAction == SelectionDotAction.Excerpt
            ? excerpt ? "后台直接导入摘录原文" : "摘录本次选区"
            : language ? "后台生成默认语言卡并导入" : "语言学习（是否朗读由设置决定）");
        tip.SetToolTip(this.question, question ? "后台生成一轮回答并导入" : "解释本次选区并继续问答");
        tip.SetToolTip(this.excerpt, excerpt ? "后台直接导入摘录原文" : "摘录原文并制卡");
    }

    private void PublishHitArea() => Volatile.Write(ref hitArea,
        !IsDisposed && Visible && (Selection != null || ImageSelection != null) ? new HitArea(Bounds) : null);

    private void PlaceAtSelection()
    {
        if ((Selection == null && ImageSelection == null) || IsDisposed) return;
        var anchor = imageMode ? ImageSelection.Anchor : new Point(Selection.X, Selection.Y);
        var area = Screen.FromPoint(anchor).WorkingArea;
        var gap = Math.Max(2, 2 * DeviceDpi / 96);
        var x = anchor.X + gap; var y = anchor.Y - Height - gap;
        if (x + Width > area.Right) x = anchor.X - Width - gap;
        if (y < area.Top) y = anchor.Y + gap;
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
        if (left == null || excerpt == null || question == null || ask == null) return;
        var inset = Math.Max(1, (int)Math.Round(2.0 * DeviceDpi / 96));
        var count = imageMode ? 5 : 4;
        var diameter = Math.Min(ClientSize.Height - 2 * inset, (ClientSize.Width - (count + 1) * inset) / count);
        if (diameter <= 0) return;
        var top = (ClientSize.Height - diameter) / 2;
        var actions = imageMode ? new[] { left, read, question, excerpt, ask } : new[] { left, question, excerpt, ask };
        for (var index = 0; index < actions.Length; index++)
            actions[index].Bounds = new Rectangle(inset + index * (ClientSize.Width - 2 * inset - diameter) / (count - 1), top, diameter, diameter);
    }

    private void ApplyDpiMetrics()
    {
        if (IsDisposed) return;
        ClientSize = new Size((int)Math.Round((imageMode ? 110.0 : 88.0) * DeviceDpi / 96), (int)Math.Round(23.0 * DeviceDpi / 96));
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
        internal CircleButton(string text, string action, string prefix = "knowledge-clipper.language.selection-dot.")
        {
            Text = text;
            Name = AccessibleName = prefix + action;
            TabStop = false; FlatStyle = FlatStyle.Flat;
            BackColor = Color.FromArgb(38, 90, 150); ForeColor = Color.White;
            Font = new Font("宋体", 7, FontStyle.Regular, GraphicsUnit.Point);
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

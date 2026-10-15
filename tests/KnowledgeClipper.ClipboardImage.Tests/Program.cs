using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using KnowledgeClipper;

namespace KnowledgeClipper
{
    internal sealed record SelectionSnapshot(string Text, int X, int Y);
}

internal static class Program
{
    private static readonly List<string> checks = new();
    private static readonly List<string> screenshots = new();
    private static string output;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
            throw new InvalidOperationException("Use tools/Invoke-IsolatedDesktopTest.ps1; no user desktop interaction is permitted.");
        output = args.Single();
        Directory.CreateDirectory(output);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        var exit = 0;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            string error = null;
            try { await RunAsync(); }
            catch (Exception ex) { error = ex.ToString(); exit = 1; Console.Error.WriteLine(ex); }
            finally
            {
                File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new
                {
                    passed = exit == 0, utc = DateTimeOffset.UtcNow, checks, screenshots, error,
                    scope = "Production floating surfaces and native clipboard message listener. Controlled image source exercises races and busy clipboard retries. Real Windows clipboard is neither read for content nor written; isolated desktop only. No application activation, model calls or exports."
                }, new JsonSerializerOptions { WriteIndented = true }));
                context.ExitThread();
            }
        };
        timer.Start();
        Application.Run(context);
        return exit;
    }

    private static async Task RunAsync()
    {
        var png = Png(Color.CornflowerBlue);
        var red = Png(Color.DarkRed);
        ScreenshotControllerChecks.Run(output, png, Check);
        DataObjectChecks(png, red);
        await SurfaceChecks(png);
        await MonitorChecks(png, red);
        await HoverMonitorChecks(png, red);
    }

    private static void DataObjectChecks(byte[] png, byte[] red)
    {
        Check(SystemClipboardImageSource.EncodeDataObject(null) == null, "empty clipboard data contains no image");
        var text = new DataObject(); text.SetText("public fixture");
        Check(SystemClipboardImageSource.EncodeDataObject(text) == null, "text clipboard data does not become an image");
        var bitmap = new DataObject();
        using var picture = Bitmap(red);
        bitmap.SetData(DataFormats.Bitmap, picture);
        Check(Pixel(SystemClipboardImageSource.EncodeDataObject(bitmap)) == Color.DarkRed.ToArgb(), "bitmap clipboard format freezes as PNG");
        var bytes = new DataObject(); bytes.SetData("PNG", false, png);
        Check(Pixel(SystemClipboardImageSource.EncodeDataObject(bytes)) == Color.CornflowerBlue.ToArgb(), "PNG byte format preserves pixels");
        var stream = new DataObject(); stream.SetData("PNG", false, new MemoryStream(png));
        stream.SetData(DataFormats.Bitmap, picture);
        Check(Pixel(SystemClipboardImageSource.EncodeDataObject(stream)) == Color.CornflowerBlue.ToArgb(), "PNG stream takes precedence over converted bitmap");
        var owned = (byte[])png.Clone();
        var snapshot = new ClipboardImageSnapshot(3, new Point(10, 20), DateTimeOffset.UtcNow, owned);
        Array.Fill<byte>(owned, 0);
        Check(Pixel(snapshot.PngBytes.ToArray()) == Color.CornflowerBlue.ToArgb(), "snapshot owns frozen bytes independent of clipboard storage");
        var path = Path.Combine(output, "frozen-image.png"); snapshot.SavePng(path);
        Check(File.ReadAllBytes(path).SequenceEqual(png), "first screenshot can be staged losslessly from the frozen PNG");
    }

    private static async Task SurfaceChecks(byte[] png)
    {
        var textCalls = new List<(SelectionSnapshot Selection, SelectionDotAction Action)>();
        var imageCalls = new List<(ClipboardImageSnapshot Image, ClipboardImageDotAction Action)>();
        using var dot = new LanguageSelectionDot((snapshot, action) => textCalls.Add((snapshot, action)));
        using var imageDot = LanguageSelectionDot.ForImages((snapshot, action) => imageCalls.Add((snapshot, action)));
        var screenIndex = 0;
        foreach (var screen in Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var anchor = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
            var snapshot = new SelectionSnapshot("public selection fixture", anchor.X, anchor.Y);
            var foreground = GetForegroundWindow();
            dot.ShowSelection(snapshot); await Task.Delay(70);
            var buttons = Buttons(dot);
            Check(string.Join(" ", buttons.Select(b => b.Text)) == "读 卡 摘 问", "text surface order is 读 卡 摘 问 on display " + screenIndex);
            Check(dot.ClientSize == new Size((int)Math.Round(88.0 * dot.DeviceDpi / 96), (int)Math.Round(23.0 * dot.DeviceDpi / 96)), "four text actions preserve height and diameter at actual DPI");
            Check(GetForegroundWindow() == foreground, "text surface does not activate");
            foreach (var button in buttons) button.PerformClick();
            Check(textCalls.TakeLast(4).Select(c => c.Action).SequenceEqual(new[] { SelectionDotAction.Read, SelectionDotAction.Question, SelectionDotAction.Excerpt, SelectionDotAction.Ask }) && textCalls.TakeLast(4).All(c => ReferenceEquals(c.Selection, snapshot)), "all text controls retain original selection and dispatch exact actions");
            dot.ApplyQuickModes(true, true, true, true);
            Check(Tip(dot, buttons[0]).Contains("默认语言卡") && Tip(dot, buttons[1]).Contains("一轮回答") && Tip(dot, buttons[2]).Contains("直接导入摘录原文"), "text quick tooltips describe each background action");
            dot.ApplyQuickModes(false, false, false, false);
            Check(Tip(dot, buttons[0]).Contains("是否朗读由设置决定") && Tip(dot, buttons[1]).Contains("继续问答") && Tip(dot, buttons[2]).Contains("原文并制卡"), "disabling shortcuts restores normal text tooltips");
            SaveSurface(dot, "text-" + screenIndex + ".png");
            dot.HideSelection();
            Check(!dot.Visible && dot.Selection == null, "text surface clears its stored selection when hidden");

            var image = new ClipboardImageSnapshot(12, anchor, DateTimeOffset.UtcNow, png);
            imageDot.ShowImage(image); await Task.Delay(70);
            buttons = Buttons(imageDot);
            Check(string.Join(" ", buttons.Select(b => b.Text)) == "图 读 卡 摘 问" && buttons.Length == 5, "image surface shows 图 读 卡 摘 问 in order");
            imageDot.ApplyQuickModes(true, true, true, true);
            Check(Tip(imageDot, buttons[0]).Contains("多图模式") && !Tip(imageDot, buttons[0]).Contains("后台") &&
                Tip(imageDot, buttons[1]).Contains("后台处理") && Tip(imageDot, buttons[2]).Contains("一轮回答") && Tip(imageDot, buttons[3]).Contains("后台摘录"),
                "image shortcuts describe single-image processing while 图 always opens multi-image collection");
            imageDot.ApplyQuickModes(false, false, false, false);
            Check(Tip(imageDot, buttons[0]).Contains("多图模式") && Tip(imageDot, buttons[1]).Contains("语言学习") &&
                Tip(imageDot, buttons[2]).Contains("提问") && Tip(imageDot, buttons[3]).Contains("摘录") && buttons.All(button => !Tip(imageDot, button).Contains("后台")),
                "disabling image shortcuts restores normal language question and excerpt descriptions");
            Check(imageDot.ClientSize == new Size((int)Math.Round(110.0 * imageDot.DeviceDpi / 96), (int)Math.Round(23.0 * imageDot.DeviceDpi / 96)) && imageDot.Height == dot.Height,
                "five image actions retain existing surface height at actual DPI");
            Check(buttons.All(button => button.Size == dot.Controls.OfType<Button>().First().Size) &&
                buttons.Zip(buttons.Skip(1)).All(pair => pair.First.Right < pair.Second.Left),
                "image actions retain text-button size without overlapping");
            Check(buttons.Select(button => button.Name).SequenceEqual(new[] { "image", "read", "question", "excerpt", "ask" }.Select(action => "knowledge-clipper.image.selection-dot." + action)),
                "all five image controls expose distinct image action identifiers");
            Check(area.Contains(imageDot.Bounds) && imageDot.Left > anchor.X && imageDot.Bottom < anchor.Y, "image surface sits close to the notification anchor");
            Check(GetForegroundWindow() == foreground && SendMessage(imageDot.Handle, 0x21, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3, "image surface and mouse activation preserve foreground");
            Check(imageDot.ContainsScreenPoint(new Point(imageDot.Left + imageDot.Width / 2, imageDot.Top + imageDot.Height / 2)), "image surface publishes clickable bounds");
            foreach (var button in buttons)
            {
                Check(button.Width == button.Height && button.Region.IsVisible(button.Width / 2, button.Height / 2) && !button.Region.IsVisible(0, 0), "image button " + button.Text + " uses the text surface's circular hit area");
                button.PerformClick();
            }
            Check(imageCalls.TakeLast(5).Select(c => c.Action).SequenceEqual(new[] { ClipboardImageDotAction.Image, ClipboardImageDotAction.Read, ClipboardImageDotAction.Question, ClipboardImageDotAction.Excerpt, ClipboardImageDotAction.Ask }) &&
                imageCalls.TakeLast(5).All(c => ReferenceEquals(c.Image, image)), "all five image actions dispatch the exact frozen image");
            SaveSurface(imageDot, "image-" + screenIndex + ".png");
            foreach (var point in new[] { area.Location, new Point(area.Right - 1, area.Bottom - 1) })
            {
                imageDot.ShowImage(new ClipboardImageSnapshot(13, point, DateTimeOffset.UtcNow, png)); await Task.Delay(30);
                Check(area.Contains(imageDot.Bounds), "image surface stays within display at " + point);
            }
            imageDot.HideImage();
            Check(!imageDot.Visible && imageDot.ImageSelection == null && !imageDot.ContainsScreenPoint(anchor), "hiding image surface clears frozen image and clickable bounds");
            var before = imageCalls.Count;
            foreach (var button in buttons) button.PerformClick();
            Check(imageCalls.Count == before, "hidden image controls cannot redispatch an old image");
            screenIndex++;
        }
    }

    private static async Task MonitorChecks(byte[] png, byte[] red)
    {
        using var owner = new Form(); _ = owner.Handle;
        using var imageDot = LanguageSelectionDot.ForImages((_, _) => { });
        var source = new FakeSource(png);
        var shown = new List<ClipboardImageSnapshot>();
        var failures = new List<string>();
        var uiThread = Environment.CurrentManagedThreadId;
        using var monitor = new ClipboardImageMonitor(owner, snapshot => { shown.Add(snapshot); imageDot.ShowImage(snapshot); }, imageDot.HideImage,
            failures.Add, source, isDotPoint: imageDot.ContainsScreenPoint);
        monitor.Start();
        var handle = ListenerHandle(monitor);
        Check(monitor.IsRunning && IsWindow(handle), "monitor registers an actual native clipboard listener");
        await Task.Delay(90);
        Check(source.ReadCount == 0 && shown.Count == 0, "enabling monitor ignores pre-existing clipboard images");

        var anchor = new Point(320, 260); source.Copy(png, anchor);
        Notify(monitor); source.Cursor = new Point(700, 650);
        await Until(() => shown.Count == 1, "first copied image appears");
        Check(shown[0].Anchor == anchor, "copied image freezes cursor location at notification time");
        Check(Pixel(shown[0].PngBytes.ToArray()) == Color.CornflowerBlue.ToArgb(), "copied image snapshot keeps exact pixels");
        var initialLocation = imageDot.Location;
        var clock = Stopwatch.StartNew();
        await Task.Delay(1150); Notify(monitor);
        Check(shown.Count == 1 && imageDot.Visible && imageDot.Location == initialLocation, "duplicate clipboard notifications neither move nor replace the visible image dot");
        await Until(() => !imageDot.Visible, "image dot expires after two seconds", 1500);
        Check(clock.ElapsedMilliseconds >= 1800 && clock.ElapsedMilliseconds < 2800 && shown.Count == 1, "image dot lifetime is two seconds and same-sequence updates do not extend it");
        Check(!monitor.TryConsume(shown[0]), "expired image snapshot cannot dispatch");

        source.Copy(red, anchor); Notify(monitor);
        await Until(() => shown.Count == 2, "second copied image appears");
        Check(monitor.TryConsume(shown[^1]) && !monitor.TryConsume(shown[^1]) && !imageDot.Visible, "click consumes an image exactly once and hides the surface");
        source.Copy(png, anchor); Notify(monitor);
        await Until(() => shown.Count == 3, "image before text update appears");
        source.Copy(null, new Point(120, 160)); Notify(monitor);
        Check(!imageDot.Visible && !monitor.TryConsume(shown[^1]), "non-image clipboard updates immediately clear prior image actions");
        await Task.Delay(120);
        Check(shown.Count == 3, "text clipboard update does not publish an image");

        source.BusyAttempts = 2;
        source.Copy(red, anchor); Notify(monitor); source.Cursor = new Point(300, 400);
        await Until(() => shown.Count == 4, "clipboard busy retry publishes image");
        Check(shown[^1].Anchor == anchor && Pixel(shown[^1].PngBytes.ToArray()) == Color.DarkRed.ToArgb() && failures.Count == 0, "brief clipboard ownership retries preserve initial anchor and image");
        Check(source.ReadThreads.All(t => t.Thread != uiThread && t.Apartment == ApartmentState.STA), "all OLE image reads run on independent STA threads");

        var priorReads = source.ReadCount;
        using (monitor.Suppress())
        {
            Check(!imageDot.Visible, "starting owned screenshot collection clears existing image dot");
            source.Copy(png, anchor); Notify(monitor); await Task.Delay(90);
            Check(source.ReadCount == priorReads && shown.Count == 4, "owned screenshot clipboard update is suppressed");
            using (monitor.Suppress()) { source.Copy(red, anchor); Notify(monitor); }
            source.Copy(png, anchor); Notify(monitor); await Task.Delay(60);
            Check(source.ReadCount == priorReads, "nested screenshot suppression remains until final scope exits");
        }
        Notify(monitor); await Task.Delay(90);
        Check(shown.Count == 4, "late notification from screenshot collection does not resurrect the dot");
        source.Copy(red, anchor); Notify(monitor);
        await Until(() => shown.Count == 5, "independent copy after screenshot resumes monitoring");

        priorReads = source.ReadCount;
        source.Owned = true; source.Copy(png, anchor); Notify(monitor); await Task.Delay(90);
        Check(source.ReadCount == priorReads && !imageDot.Visible && shown.Count == 5, "application-owned selection restoration never reopens an old image dot");
        source.Owned = false;

        using (var held = new ManualResetEventSlim(false))
        {
            source.Gate = held; source.Copy(png, anchor); Notify(monitor);
            await Until(() => source.ReadCount > priorReads, "delayed image read starts");
            source.Copy(red, new Point(540, 460)); Notify(monitor);
            source.Gate = null; held.Set();
            await Until(() => shown.Count == 6, "newer clipboard sequence replaces delayed read");
            Check(shown[^1].SequenceNumber == source.SequenceNumber && Pixel(shown[^1].PngBytes.ToArray()) == Color.DarkRed.ToArgb() && shown[^1].Anchor == new Point(540, 460), "stale asynchronous image never replaces a newer clipboard copy");
        }

        source.Copy(png, anchor); Notify(monitor);
        await Until(() => shown.Count == 7, "fresh image before stale click appears");
        var stale = shown[^1];
        source.Copy(red, anchor);
        Check(!monitor.TryConsume(stale), "clipboard sequence change invalidates click before notification is delivered");
        Notify(monitor); await Until(() => shown.Count == 8, "new sequence remains available after stale click rejection");

        priorReads = source.ReadCount;
        using (var held = new ManualResetEventSlim(false))
        {
            source.Gate = held; source.Copy(png, anchor); Notify(monitor);
            await Until(() => source.ReadCount > priorReads, "in-flight read before stop starts");
            monitor.Stop();
            Check(!monitor.IsRunning && !IsWindow(handle) && !imageDot.Visible, "stopping monitor removes native listener and visible image");
            source.Gate = null; held.Set(); await Task.Delay(100);
            Check(shown.Count == 8, "stopped monitor discards late OLE image result");
        }
        monitor.Start(); await Task.Delay(90);
        Check(shown.Count == 8, "restart does not treat existing clipboard data as a new image");
        source.Copy(red, anchor); Notify(monitor);
        await Until(() => shown.Count == 9, "new image works after stop and restart");

        priorReads = source.ReadCount; source.BusyAttempts = 30; source.Copy(png, anchor); Notify(monitor);
        await Until(() => failures.Count == 1, "permanently busy clipboard reports bounded failure");
        await Task.Delay(100);
        Check(source.ReadCount - priorReads == 8 && !imageDot.Visible && shown.Count == 9, "busy clipboard stops after eight attempts without a stale dot or repeat error");
        source.BusyAttempts = 0; source.Copy(red, anchor); Notify(monitor);
        await Until(() => shown.Count == 10, "independent copy recovers after a failed clipboard sequence");
        monitor.Dispose();
        source.Copy(png, anchor); await Task.Delay(90);
        Check(!monitor.IsRunning && !imageDot.Visible && shown.Count == 10, "disposed monitor releases timers and cannot publish further images");

        var fallbackSource = new FakeSource(png);
        var fallbackShown = new List<ClipboardImageSnapshot>();
        var fallbackErrors = new List<string>();
        using var fallback = new ClipboardImageMonitor(owner, fallbackShown.Add, () => { }, fallbackErrors.Add, fallbackSource,
            _ => throw new Win32Exception("controlled listener registration failure"));
        fallback.Start();
        Check(fallback.IsRunning && fallbackErrors.Count == 1, "native listener registration failure preserves enabled monitor with one diagnostic");
        fallbackSource.Copy(red, anchor);
        await Until(() => fallbackShown.Count == 1, "sequence polling still discovers copied image after listener registration failure");
        Check(Pixel(fallbackShown[0].PngBytes.ToArray()) == Color.DarkRed.ToArgb() && fallback.TryConsume(fallbackShown[0]),
            "fallback image keeps correct pixels and can still be consumed");
        await Task.Delay(100);
        Check(fallbackErrors.Count == 1, "fallback listener diagnostic does not repeat on every polling tick");
    }

    private static async Task HoverMonitorChecks(byte[] png, byte[] red)
    {
        using var owner = new Form(); _ = owner.Handle;
        using var imageDot = LanguageSelectionDot.ForImages((_, _) => { });
        var source = new FakeSource(png);
        var shown = new List<ClipboardImageSnapshot>();
        using var monitor = new ClipboardImageMonitor(owner, snapshot => { shown.Add(snapshot); imageDot.ShowImage(snapshot); }, imageDot.HideImage,
            null, source, isDotPoint: imageDot.ContainsScreenPoint);
        monitor.Start();
        var foreground = GetForegroundWindow();
        var anchor = new Point(320, 260);
        void Pointer(bool inside)
        {
            source.Cursor = inside ? new Point(imageDot.Left + imageDot.Width / 2, imageDot.Top + imageDot.Height / 2) : anchor;
            typeof(ClipboardImageMonitor).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(monitor, null);
        }

        source.Copy(png, anchor); Notify(monitor);
        await Until(() => shown.Count == 1, "image hover fixture publishes a new snapshot");
        Pointer(true);
        await Task.Delay(ClipboardImageMonitor.DisplayMilliseconds + 150);
        Check(imageDot.Visible && ReferenceEquals(imageDot.ImageSelection, shown[0]) && GetForegroundWindow() == foreground,
            "hover keeps the image visible beyond its initial deadline without activating a window");
        Pointer(false);
        await Task.Delay(1100);
        Check(imageDot.Visible, "leaving a hovered image starts a complete two-second grace period");
        Pointer(true);
        await Task.Delay(1150);
        Check(imageDot.Visible, "re-entering before expiry cancels the old departure deadline");
        Pointer(false); var departed = Stopwatch.StartNew();
        await Until(() => !imageDot.Visible, "image hides two seconds after its final pointer departure", 2800);
        Check(departed.ElapsedMilliseconds >= 1900 && departed.ElapsedMilliseconds < 2800 && !monitor.TryConsume(shown[0]),
            "last departure receives its own full deadline and expired image cannot be consumed");

        source.Copy(red, anchor); Notify(monitor);
        await Until(() => shown.Count == 2, "new image appears after a hover expiry");
        // Block this isolated UI thread across the old deadline, then enter at
        // click time before its queued timer tick. TryConsume must sample hover.
        Thread.Sleep(ClipboardImageMonitor.DisplayMilliseconds + 50);
        source.Cursor = new Point(imageDot.Left + imageDot.Width / 2, imageDot.Top + imageDot.Height / 2);
        Check(monitor.TryConsume(shown[^1]) && !monitor.TryConsume(shown[^1]) && !imageDot.Visible,
            "hovered click samples the pointer before timeout validation and still consumes only once");

        source.Copy(png, anchor); Notify(monitor);
        await Until(() => shown.Count == 3, "image before hovered clipboard replacement appears");
        Pointer(true); var stale = shown[^1];
        source.Copy(red, source.Cursor); Notify(monitor);
        Check(!imageDot.Visible && !monitor.TryConsume(stale), "new clipboard sequence invalidates a hovered old image immediately");
        await Until(() => shown.Count == 4, "replacement image still appears after invalidating a hovered snapshot");
        Check(Pixel(shown[^1].PngBytes.ToArray()) == Color.DarkRed.ToArgb() && shown[^1].SequenceNumber == source.SequenceNumber,
            "hover cannot rebind old actions to replacement clipboard pixels");
        Pointer(true); stale = shown[^1];
        source.Copy(null, source.Cursor); Notify(monitor);
        Check(!imageDot.Visible && !monitor.TryConsume(stale), "a text clipboard copy overrides hover protection and clears image actions");
        source.Copy(png, anchor); Notify(monitor);
        await Until(() => shown.Count == 5, "image before hovered monitor shutdown appears");
        Pointer(true); monitor.Stop();
        Check(!imageDot.Visible && !monitor.TryConsume(shown[^1]), "disabling the image monitor clears even a hovered snapshot");
    }

    private sealed class FakeSource(byte[] initial) : IClipboardImageSource
    {
        private readonly object gate = new();
        private uint sequence = 1;
        private byte[] png = initial;
        internal Point Cursor = new(100, 100);
        internal bool Owned;
        internal int BusyAttempts;
        internal ManualResetEventSlim Gate;
        internal int ReadCount;
        internal readonly List<(int Thread, ApartmentState Apartment)> ReadThreads = new();
        public uint SequenceNumber { get { lock (gate) return sequence; } }
        public Point CursorPosition { get { lock (gate) return Cursor; } }
        public bool IsOwnedByCurrentProcess => Owned;
        internal void Copy(byte[] value, Point point) { lock (gate) { png = value; Cursor = point; sequence++; } }
        public byte[] ReadPng()
        {
            byte[] value; ManualResetEventSlim held;
            lock (gate)
            {
                ReadThreads.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
                Interlocked.Increment(ref ReadCount);
                if (BusyAttempts > 0) { BusyAttempts--; throw new ExternalException("controlled clipboard ownership"); }
                value = png; held = Gate;
            }
            held?.Wait(TimeSpan.FromSeconds(5));
            return value;
        }
    }

    private static Button[] Buttons(Form form) => form.Controls.OfType<Button>().Where(b => b.Visible).OrderBy(b => b.Left).ToArray();
    private static string Tip(LanguageSelectionDot form, Control control) => ((ToolTip)typeof(LanguageSelectionDot).GetField("tip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!).GetToolTip(control);
    private static IntPtr ListenerHandle(ClipboardImageMonitor monitor) => ((NativeWindow)typeof(ClipboardImageMonitor).GetField("listener", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(monitor)).Handle;
    private static void Notify(ClipboardImageMonitor monitor) => SendMessage(ListenerHandle(monitor), 0x31D, IntPtr.Zero, IntPtr.Zero);
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks.Add(description); Console.WriteLine("PASS " + description);
    }
    private static async Task Until(Func<bool> predicate, string description, int timeout = 2500)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < timeout) await Task.Delay(15);
        Check(predicate(), description);
    }
    private static byte[] Png(Color color)
    {
        using var bitmap = new Bitmap(5, 4);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var output = new MemoryStream(); bitmap.Save(output, ImageFormat.Png); return output.ToArray();
    }
    private static Bitmap Bitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var image = Image.FromStream(stream); return new Bitmap(image);
    }
    private static int Pixel(byte[] bytes) { using var bitmap = Bitmap(bytes); return bitmap.GetPixel(1, 1).ToArgb(); }
    private static void SaveSurface(Form form, string name)
    {
        var path = Path.Combine(output, name); using var painted = new Bitmap(form.Width, form.Height); form.DrawToBitmap(painted, form.ClientRectangle);
        using var cropped = new Bitmap(painted.Width, painted.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(cropped)) { graphics.Clear(Color.Transparent); graphics.SetClip(form.Region, System.Drawing.Drawing2D.CombineMode.Replace); graphics.DrawImageUnscaled(painted, Point.Empty); }
        cropped.Save(path, ImageFormat.Png); screenshots.Add(path);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
}

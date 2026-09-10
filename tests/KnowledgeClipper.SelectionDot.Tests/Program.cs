using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using KnowledgeClipper;

// This is a production-control layout/dispatch test. The snapshot is a minimal
// fixture, not the capture implementation, and no content or speech service runs.
namespace KnowledgeClipper
{
    internal sealed record SelectionSnapshot(string Text, int X, int Y);
}

internal static class Program
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly List<string> checks = new();
    private static readonly List<object> displays = new();
    private static readonly List<string> screenshots = new();
    private static readonly string Output = @"D:\Artifacts\KnowledgeClipper\maintenance\dot";

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks.Add(description);
        Console.WriteLine("PASS " + description);
    }

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(Output);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        var exit = 0;
        timer.Tick += async (_, _) => {
            timer.Stop();
            string error = null;
            try { await RunAsync(); }
            catch (Exception ex) { error = ex.ToString(); exit = 1; Console.Error.WriteLine(ex); }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "validation.json"), JsonSerializer.Serialize(new {
                    passed = exit == 0, utc = DateTimeOffset.UtcNow, checks, displays, screenshots, error,
                    scope = "Real WinForms production LanguageSelectionDot linked directly. Minimal snapshot fixture; verifies layout and semantic callbacks only, not capture, TTS or question E2E. No production process started or stopped."
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
        var calls = new List<(SelectionSnapshot Selection, SelectionDotAction Action)>();
        using var dot = new LanguageSelectionDot((selection, action) => calls.Add((selection, action)));
        var left = (Button)dot.Controls.Find("knowledge-clipper.language.selection-dot.read", true).Single();
        var question = (Button)dot.Controls.Find("knowledge-clipper.language.selection-dot.question", true).Single();
        Check(dot.LeftAction == SelectionDotAction.Read && left.Text == "读" && question.Text == "问", "defaults expose Read and Question");
        var screenIndex = 0;
        foreach (var screen in Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var anchor = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
            var selection = new SelectionSnapshot("partial selection fixture", anchor.X, anchor.Y);
            var foreground = GetForegroundWindow();
            dot.LeftAction = SelectionDotAction.Read;
            dot.ShowSelection(selection);
            await Task.Delay(120);
            Check(dot.Visible && screen.WorkingArea.Contains(dot.Bounds), "dot fits screen " + screenIndex);
            Check(GetForegroundWindow() == foreground, "show preserves foreground on screen " + screenIndex);
            Check(SendMessage(dot.Handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3, "mouse activation stays nonactivating");
            Check(dot.Width > dot.Height * 1.7 && dot.Region.IsVisible(dot.Width / 2, dot.Height / 2) && !dot.Region.IsVisible(0, 0), "outer region is an elongated rounded capsule");
            Check(dot.ContainsScreenPoint(new Point(dot.Left + dot.Width / 2, dot.Top + dot.Height / 2)) && !dot.ContainsScreenPoint(new Point(dot.Right + 1, dot.Bottom + 1)), "published hit bounds contain only window rectangle");
            foreach (var button in new[] { left, question })
            {
                Check(button.Width == button.Height && button.Region != null && !button.Region.IsVisible(0, 0) && button.Region.IsVisible(button.Width / 2, button.Height / 2), button.Text + " button has circular hit region");
                using var graphics = button.CreateGraphics();
                var textSize = TextRenderer.MeasureText(graphics, button.Text, button.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Check(textSize.Width < button.Width && textSize.Height < button.Height, button.Text + " glyph " + textSize + " fits " + button.Size + " circle at " + dot.DeviceDpi + " DPI (window " + dot.Size + ", font " + button.Font.SizeInPoints + "pt)");
                Check(button.Font.Name == "宋体", button.Text + " uses installed serif font");
            }
            Check(!left.Bounds.IntersectsWith(question.Bounds), "circular buttons do not overlap");
            Screenshot(dot, "screen-" + screenIndex + "-" + dot.DeviceDpi + "dpi-read.png");
            left.PerformClick(); question.PerformClick();
            Check(calls.Count >= 2 && calls[^2].Action == SelectionDotAction.Read && calls[^1].Action == SelectionDotAction.Question && ReferenceEquals(calls[^1].Selection, selection) && ReferenceEquals(calls[^2].Selection, selection), "Read and Question forward original selection exactly once per click");
            dot.LeftAction = SelectionDotAction.Excerpt;
            await Task.Delay(30);
            Check(left.Text == "摘", "Excerpt setting updates left label");
            Screenshot(dot, "screen-" + screenIndex + "-" + dot.DeviceDpi + "dpi-excerpt.png");
            left.PerformClick();
            Check(calls[^1].Action == SelectionDotAction.Excerpt && ReferenceEquals(calls[^1].Selection, selection), "Excerpt forwards distinct non-read action");
            displays.Add(new { screen = screen.DeviceName, dpi = dot.DeviceDpi, windowWidth = dot.Width, windowHeight = dot.Height, buttonWidth = left.Width, buttonHeight = left.Height, font = left.Font.Name, fontPoints = left.Font.SizeInPoints });
            foreach (var edge in new[] { area.Location, new Point(area.Right - 1, area.Bottom - 1) })
            {
                dot.ShowSelection(new SelectionSnapshot("edge", edge.X, edge.Y));
                await Task.Delay(30);
                Check(area.Contains(dot.Bounds), "window stays inside display at " + edge);
            }
            dot.HideSelection();
            Check(!dot.Visible && dot.Selection == null && !dot.ContainsScreenPoint(anchor), "hide clears selection and hit snapshot");
            var beforeHiddenClick = calls.Count;
            left.PerformClick(); question.PerformClick();
            Check(calls.Count == beforeHiddenClick, "hidden cleared selection cannot dispatch");
            screenIndex++;
        }
    }

    private static void Screenshot(Form dot, string name)
    {
        var path = Path.Combine(Output, name);
        using var painted = new Bitmap(dot.ClientSize.Width, dot.ClientSize.Height);
        dot.DrawToBitmap(painted, dot.ClientRectangle);
        // DrawToBitmap ignores a top-level HWND region. Apply that exact runtime
        // region so the saved render includes the actual transparent corners.
        using var bitmap = new Bitmap(painted.Width, painted.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SetClip(dot.Region, System.Drawing.Drawing2D.CombineMode.Replace);
            graphics.DrawImageUnscaled(painted, Point.Empty);
        }
        bitmap.Save(path, ImageFormat.Png);
        screenshots.Add(path);
        Check(Enumerable.Range(0, bitmap.Width).Any(x => Enumerable.Range(0, bitmap.Height).Any(y => { var c = bitmap.GetPixel(x, y); return c.R > 245 && c.G > 245 && c.B > 245; })), "render contains visible white glyph pixels: " + name);
    }
}

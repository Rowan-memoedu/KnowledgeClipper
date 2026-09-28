using System.Reflection;
using System.Runtime.InteropServices;
using KnowledgeClipper;

internal static class Program
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    [STAThread]
    private static int Main()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
                throw new Exception("Requires Invoke-IsolatedDesktopTest.ps1.");
            var lifetime = new MeaningPopupLifetime();
            lifetime.Start(true, 100);
            Check(!lifetime.Observe(false, 60099), "Loading remains before deadline");
            Check(lifetime.Observe(true, 60100), "Loading expires even while hovered");
            lifetime.Start(false, 0);
            Check(!lifetime.Observe(true, 5999), "Result hover");
            Check(!lifetime.Observe(true, 50000), "Hover keeps result");
            Check(!lifetime.Observe(false, 50000), "Leave resets duration");
            Check(!lifetime.Observe(false, 55999), "Full six seconds after leave");
            Check(lifetime.Observe(false, 56000), "Result expires after leave");
            lifetime.Start(false, 0);
            Check(lifetime.Observe(false, 6000), "Unhovered result expires");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var source = new Form { Text = "Isolated source", Size = new Size(300, 200) };
            using var first = new TextBox { Location = new Point(10, 10), Width = 120 };
            using var second = new TextBox { Location = new Point(10, 50), Width = 120 };
            source.Controls.Add(first); source.Controls.Add(second);
            source.Show(); source.Activate(); first.Focus(); Application.DoEvents();
            Check(first.Focused, "Source field owns initial focus");
            var before = GetForegroundWindow();
            var observedFocus = new MeaningPopupFocus(source.Handle, first.Handle);
            using var popup = new MeaningPopup(() => observedFocus);
            var replays = 0;
            popup.ReplayRequested += () => replays++;
            var area = Screen.PrimaryScreen!.WorkingArea;
            popup.ShowLoading(new Point(area.Right - 1, area.Top)); Application.DoEvents();
            Check(popup.IsRequestVisible, "Loading visible");
            Check(area.Contains(popup.Bounds), "Edge placement stays in work area");
            Check(GetForegroundWindow() == before, "Showing loading preserves focus");
            popup.ShowMeaning("这里指投入时间和精力。", new Point(area.Left, area.Bottom - 1)); Application.DoEvents();
            Check(GetForegroundWindow() == before, "Showing meaning preserves focus");
            Check(popup.AccessibleDescription == "这里指投入时间和精力。", "Only requested meaning displayed");
            Check((IntPtr)typeof(MeaningPopup).GetField("mouseHook", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(popup)! != IntPtr.Zero,
                "Outside-click watcher is registered while meaning is visible");
            Check(area.Contains(popup.Bounds), "Result stays in work area");
            Check(popup.ContainsScreenPoint(new Point(popup.Left + popup.Width / 2, popup.Top + popup.Height / 2)), "Visible bounds hit test");
            popup.PointerDown(new Point(popup.Left + popup.Width / 2, popup.Top + popup.Height / 2)); Application.DoEvents();
            Check(popup.IsRequestVisible, "Click inside meaning keeps it visible");
            var mouseClick = typeof(MeaningPopup).GetMethod("OnMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
            mouseClick.Invoke(popup, [new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0)]);
            Check(replays == 1 && popup.IsRequestVisible, "Left click requests one replay and keeps meaning visible");
            mouseClick.Invoke(popup, [new MouseEventArgs(MouseButtons.Right, 1, 10, 10, 0)]);
            Check(replays == 1, "Right click does not request replay");
            popup.PointerDown(new Point(area.Left, area.Top)); Application.DoEvents();
            Check(!popup.IsRequestVisible, "Click outside meaning hides it even without a focus change");
            Check(replays == 1, "Outside click does not request replay");
            Check((IntPtr)typeof(MeaningPopup).GetField("mouseHook", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(popup)! == IntPtr.Zero,
                "Outside-click watcher is removed when meaning hides");
            popup.ShowMeaning("重新显示", new Point(area.Left + 80, area.Top + 80));
            second.Focus(); Application.DoEvents();
            Check(second.Focused, "Second source field receives focus");
            observedFocus = new(source.Handle, second.Handle);
            var deadline = Environment.TickCount64 + 1000;
            while (popup.IsRequestVisible && Environment.TickCount64 < deadline)
            { Thread.Sleep(15); Application.DoEvents(); }
            Check(!popup.IsRequestVisible, "Focus change within the source window hides meaning");
            popup.ShowMeaning("再次显示", new Point(area.Left + 80, area.Top + 80));
            using (var other = new Form { Text = "Another isolated window", Size = new Size(200, 120) })
            {
                other.Show(); other.Activate(); Application.DoEvents();
                observedFocus = new(other.Handle, other.Handle);
                deadline = Environment.TickCount64 + 1000;
                while (popup.IsRequestVisible && Environment.TickCount64 < deadline)
                { Thread.Sleep(15); Application.DoEvents(); }
                Check(!popup.IsRequestVisible, "Switching to another window hides meaning");
            }
            popup.HideMeaning();
            Check(!popup.IsRequestVisible && !popup.ContainsScreenPoint(popup.Location), "Hide clears request and hit test");
            popup.ShowError("暂时无法查询", new Point(area.Left + 100, area.Top + 100));
            Check(popup.IsRequestVisible, "New error can display");
            var timer = (System.Windows.Forms.Timer)typeof(MeaningPopup).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(popup)!;
            popup.Dispose();
            Check(!timer.Enabled && !popup.IsRequestVisible, "Dispose stops timer and invalidates request");
            Console.WriteLine($"PASS {checks} meaning popup checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

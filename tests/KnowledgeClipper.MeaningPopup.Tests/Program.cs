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
            source.Show(); source.Activate(); Application.DoEvents();
            var before = GetForegroundWindow();
            using var popup = new MeaningPopup();
            var area = Screen.PrimaryScreen!.WorkingArea;
            popup.ShowLoading(new Point(area.Right - 1, area.Top)); Application.DoEvents();
            Check(popup.IsRequestVisible, "Loading visible");
            Check(area.Contains(popup.Bounds), "Edge placement stays in work area");
            Check(GetForegroundWindow() == before, "Showing loading preserves focus");
            popup.ShowMeaning("这里指投入时间和精力。", new Point(area.Left, area.Bottom - 1)); Application.DoEvents();
            Check(GetForegroundWindow() == before, "Showing meaning preserves focus");
            Check(popup.AccessibleDescription == "这里指投入时间和精力。", "Only requested meaning displayed");
            Check(area.Contains(popup.Bounds), "Result stays in work area");
            Check(popup.ContainsScreenPoint(new Point(popup.Left + popup.Width / 2, popup.Top + popup.Height / 2)), "Visible bounds hit test");
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

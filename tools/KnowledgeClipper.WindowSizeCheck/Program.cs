using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using KnowledgeClipper.Workflow;
using Forms = System.Windows.Forms;

internal static class Program
{
    private static readonly List<object> Observations = new();
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "probe")
            {
                Observations.Add(Snapshot(new nint(long.Parse(args[1]))));
            }
            else
            {
                if (args.Length != 2 || args[0] is not ("legacy" or "fixed")) throw new ArgumentException("WindowSizeCheck probe <hwnd> | legacy|fixed <project-root>");
                if (args[0] != "legacy")
                {
                    var program = Assembly.Load("KnowledgeClipper").GetType("KnowledgeClipper.Program", true)!;
                    program.GetMethod("InitializeDpi", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                }
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                Check(args[1], args[0]);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", observations = Observations }, WorkflowJson.Options));
            return 0;
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            Console.WriteLine(JsonSerializer.Serialize(new { status = "FAIL", error = ex.Message, observations = Observations }, WorkflowJson.Options));
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Check(string projectRoot, string mode)
    {
        var assembly = Assembly.Load("KnowledgeClipper");
        var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\window-size-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        // Exercise the user's persisted legacy size, only within disposable test data.
        File.WriteAllText(Path.Combine(dataRoot, "size.txt"), "348,212");
        var startupArea = Forms.Screen.PrimaryScreen!.WorkingArea;
        File.WriteAllText(Path.Combine(dataRoot, "position.txt"), $"{startupArea.Right - 360},{startupArea.Top + 20}");
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(projectRoot, "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke(new object?[5]);
        var formType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        Forms.Form MakeForm() => (Forms.Form)formType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        using (var form = MakeForm())
        {
            form.Show(); Pump();
            Require(Forms.Screen.FromControl(form).WorkingArea.Contains(form.Bounds), "initial-scaled-window-stays-fully-visible");
            var before = Snapshot(form.Handle);
            Observe("initial", form, before);
            QueryOwnAccessibility(form.Handle);
            Pump();
            Observe("after-first-uia", form);
            ChangeStatus(formType, form, "正在处理内容…");
            Pump();
            Observe("after-status", form);
            var material = new CaptureMaterial("resilient", "A resilient plant survived.", "source", 2, "Synthetic window-size source", 0, DateTimeOffset.UtcNow);
            var draft = new ClipDraft { Original = material, Material = material, Settings = new LanguageSettings(), Configuration = new ContentConfiguration("", []) };
            using (var preview = (Forms.Form)assembly.GetType("KnowledgeClipper.LanguageDraftForm", true)!.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
                .Invoke([draft, new DraftStore(dataRoot), (Func<Task>)(() => Task.CompletedTask), (Func<Task>)(() => Task.CompletedTask), (Action)(() => {})]))
            {
                preview.Show(form); Pump();
                ChangeStatus(formType, form, "内容已就绪，请查看预览");
                Pump();
                Observe("preview-open", form);
                preview.Close(); Pump();
            }
            var after = Snapshot(form.Handle);
            Observe("preview-closed", form, after);
            Require(before.Width == after.Width && before.Height == after.Height, "physical-size-stable-through-uia-status-preview");
            Require(after.WindowDpi == form.DeviceDpi, "managed-and-native-dpi-agree");
            var originalLocation = form.Location;
            var logicalWidth = form.ClientSize.Width * 96.0 / form.DeviceDpi;
            var logicalHeight = form.ClientSize.Height * 96.0 / form.DeviceDpi;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                // Only move this checker-owned form through the real display bounds.
                form.Location = screen.WorkingArea.Location; Pump();
                Observe("display-" + screen.DeviceName, form);
                Require(Math.Abs(form.ClientSize.Width * 96.0 / form.DeviceDpi - logicalWidth) <= 2 &&
                    Math.Abs(form.ClientSize.Height * 96.0 / form.DeviceDpi - logicalHeight) <= 2, "logical-size-stable-on-available-display");
            }
            form.Location = originalLocation; Pump();
            var minimum = form.MinimumSize;
            Invoke(formType, form, "RestoreDefaultSize"); Pump();
            var baseline = form.ClientSize;
            var read = form.Controls.Find("knowledge-clipper.desktop.action.read-capture", true).FirstOrDefault()
                ?? Descendants(form).OfType<Forms.Button>().Single(button => button.Text == "朗读");
            var initialFont = read.Font.Size;
            foreach (var zoom in new[] { 1.5, 2.0, 1.25, 1.0 })
            {
                form.ClientSize = new Size((int)Math.Round(baseline.Width * zoom), (int)Math.Round(baseline.Height * zoom)); Pump();
                Require(Math.Abs(read.Font.Size / initialFont - zoom) < .03, "custom-zoom-font-scale-" + zoom);
                Require(Math.Abs(form.ClientSize.Width / 344.0 - form.ClientSize.Height / 210.0) < .015, "custom-zoom-aspect-" + zoom);
            }
            form.ClientSize = new Size(baseline.Width * 2, baseline.Height); Pump();
            Require(Math.Abs(form.ClientSize.Height / (double)baseline.Height - 2) < .015, "single-axis-resize-keeps-whole-window-scale");
            Invoke(formType, form, "SaveWindowBounds");
            var savedSize = form.ClientSize;
            form.Close();
            using var restored = MakeForm();
            restored.Show(); Pump();
            Observe("restored-user-zoom", restored);
            Require(Math.Abs(restored.ClientSize.Width - savedSize.Width) <= 2 && Math.Abs(restored.ClientSize.Height - savedSize.Height) <= 2, "saved-zoom-roundtrip-without-dpi-multiplication");
            restored.Close();
            Observations.Add(new { mode, minimum = minimum.ToString(), baseline = baseline.ToString(), finalSaved = File.ReadAllText(Path.Combine(dataRoot, "size.txt")), dataRoot });
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void QueryOwnAccessibility(nint handle)
    {
        var element = System.Windows.Automation.AutomationElement.FromHandle(handle);
        _ = element.Current.BoundingRectangle;
    }
    private static void ChangeStatus(Type type, object form, string value) => type.GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [value, Color.Navy, Color.White]);
    private static void Invoke(Type type, object form, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    private static IEnumerable<Forms.Control> Descendants(Forms.Control root) { foreach (Forms.Control child in root.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void Require(bool value, string name) { Observations.Add(new { name, passed = value }); if (!value) throw new InvalidOperationException(name); }
    private static void Observe(string phase, Forms.Form form, WindowSnapshot? window = null) => Observations.Add(new { phase, client = form.ClientSize.ToString(), form.DeviceDpi, autoScale = form.AutoScaleDimensions.ToString(), currentAutoScale = form.CurrentAutoScaleDimensions.ToString(), window = window ?? Snapshot(form.Handle) });
    private static void Pump() { var clock = Stopwatch.StartNew(); do { Forms.Application.DoEvents(); Thread.Sleep(10); } while (clock.ElapsedMilliseconds < 120); }

    private sealed record WindowSnapshot(long Handle, uint WindowDpi, int WindowAwareness, int CallerAwareness, int Width, int Height, int VirtualWidth, int VirtualHeight);
    private static WindowSnapshot Snapshot(nint handle)
    {
        var caller = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        GetWindowRect(handle, out var virtualRect);
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            if (!GetWindowRect(handle, out var physical)) throw new InvalidOperationException("window-is-unavailable");
            return new(handle.ToInt64(), GetDpiForWindow(handle), GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(handle)), caller,
                physical.Right - physical.Left, physical.Bottom - physical.Top, virtualRect.Right - virtualRect.Left, virtualRect.Bottom - virtualRect.Top);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using KnowledgeClipper.Workflow;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? played;
    private static readonly List<string> Checks = new();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);
    private static void Selected(object value) => played = value;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Checks.Add(name); Console.WriteLine("PASS " + name); }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--excerpt") || args.Contains("--images") || args.Contains("--export-destinations") || args.Contains("--floating-settings") || args.Contains("--multi-image") || args.Contains("--original-image-previews"))
        {
            if (Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") != "1")
                throw new InvalidOperationException("Run excerpt UI checks with tools/Invoke-IsolatedDesktopTest.ps1.");
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        var exit = 0;
        timer.Tick += async (_, _) => {
            timer.Stop();
            try { await RunAsync(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { context.ExitThread(); }
        };
        timer.Start(); Application.Run(context); return exit;
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Length == 3 && args[2] == "--deck-settings") { await DeckSettingsChecks.RunAsync(args[0], args[1]); return; }
        if (args.Length == 3 && args[2] == "--all-settings") { await AllSettingsChecks.RunAsync(args[0], args[1]); return; }
        if (args.Length == 3 && args[2] == "--excerpt") { await ExcerptUiChecks.RunAsync(args[0], args[1]); return; }
        if (args.Length == 3 && args[2] == "--images") { await ImageUiChecks.RunAsync(args[0], args[1]); return; }
        if (args.Length == 3 && args[2] == "--export-destinations") { await ExportDestinationChecks.RunAsync(args[0], args[1]); return; }
        if (args.Length == 3 && args[2] == "--floating-settings") { await FloatingSettingsChecks.RunAsync(args[0], args[1]); return; }
        if (args.Length == 3 && args[2] == "--multi-image") { await MultiImageUiChecks.RunAsync(args[1]); return; }
        if (args.Length == 3 && args[2] == "--original-image-previews") { await OriginalImagePreviewChecks.RunAsync(args[0], args[1]); return; }
        if (args.Length != 2) throw new ArgumentException("SelectionUi.Tests <project-root> <receipt-path>");
        var assembly = Assembly.Load("KnowledgeClipper");
        var snapshotType = assembly.GetType("KnowledgeClipper.SelectionSnapshot", true)!;
        object Snapshot(Point anchor)
        {
            var constructor = snapshotType.GetConstructors().Single();
            var values = constructor.GetParameters().Select(p => p.DefaultValue).ToArray();
            values[0] = "尚未过期的选区"; values[1] = GetForegroundWindow().ToInt64(); values[2] = "Selection UI test";
            values[3] = anchor.X; values[4] = anchor.Y; values[5] = DateTimeOffset.UtcNow;
            return constructor.Invoke(values);
        }
        object? Get(object target, string field) => target.GetType().GetField(field, Instance)!.GetValue(target);
        object? Call(object target, string method, params object[] values) => target.GetType().GetMethod(method, Instance)!.Invoke(target, values);
        Button Button(Form form, string name) => (Button)form.Controls.Find(name, true).Single();
        var parameter = Expression.Parameter(snapshotType);
        var action = Expression.Lambda(typeof(Action<>).MakeGenericType(snapshotType),
            Expression.Call(typeof(Program).GetMethod(nameof(Selected), BindingFlags.Static | BindingFlags.NonPublic)!, Expression.Convert(parameter, typeof(object))), parameter).Compile();
        using (var dot = (Form)assembly.GetType("KnowledgeClipper.LanguageSelectionDot", true)!.GetConstructors(Instance).Single(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == action.GetType()).Invoke([action]))
        {
            foreach (var screen in Screen.AllScreens)
            {
                var area = screen.WorkingArea;
                var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
                foreach (var anchor in new[] { area.Location, new Point(area.Right - 1, area.Bottom - 1), center })
                {
                    var snapshot = Snapshot(anchor); var foreground = GetForegroundWindow();
                    Call(dot, "ShowSelection", snapshot); await Task.Delay(60);
                    Check(dot.Visible && screen.WorkingArea.Contains(dot.Bounds), "dot stays in actual display work area at " + anchor);
                    Check(dot.ClientSize == new Size((int)Math.Round(66.0 * dot.DeviceDpi / 96), (int)Math.Round(23.0 * dot.DeviceDpi / 96)),
                        "three independent actions keep the compact height at actual display DPI");
                    if (anchor == center)
                    {
                        Check(dot.Left > anchor.X && dot.Bottom < anchor.Y && anchor.Y - dot.Bottom <= Math.Max(2, 2 * dot.DeviceDpi / 96),
                            "dot sits close to the upper right of the selection anchor");
                        using var picture = new Bitmap(dot.Width, dot.Height);
                        dot.DrawToBitmap(picture, dot.ClientRectangle);
                        picture.Save(Path.Combine(Path.GetDirectoryName(args[1])!, "selection-dot.png"));
                    }
                    Check(GetForegroundWindow() == foreground, "showing the dot preserves foreground focus");
                    Check(SendMessage(dot.Handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3, "dot mouse activation preserves the source");
                    Button(dot, "knowledge-clipper.language.selection-dot.read").PerformClick();
                    Check(ReferenceEquals(played, snapshot), "dot click forwards the exact displayed selection");
                    Call(dot, "HideSelection");
                    Check(!dot.Visible && dot.GetType().GetProperty("Selection", Instance)!.GetValue(dot) == null, "hidden dot clears its selection");
                }
            }
        }
        var dataRoot = Path.Combine(@"D:\Caches\KnowledgeClipper\selection-ui-tests", Guid.NewGuid().ToString("N"));
        WorkflowJson.Save(Path.Combine(dataRoot, "language-settings.json"), new LanguageSettings {
            GlobalSelection = false, ApiBaseUrl = "http://127.0.0.1:1", AnkiEndpoint = "http://127.0.0.1:1"
        });
        var configType = assembly.GetType("KnowledgeClipper.AppConfig", true)!;
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(args[0], "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        var controllerType = assembly.GetType("KnowledgeClipper.Desktop.Core.DesktopController", true)!;
        var controller = controllerType.GetConstructors(Instance).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        var mainType = assembly.GetType("KnowledgeClipper.BoundClipperForm", true)!;
        using var main = (Form)mainType.GetConstructor(Instance, null, [configType, controllerType, typeof(bool)], null)!.Invoke([config, controller, false]);
        Call(main, "EnableLanguageWorkflow"); main.Show(); await Task.Delay(80);
        var initialSize = main.Size;
        var flow = Get(main, "languageFlow")!; var monitor = Get(main, "selectionMonitor")!;
        bool Running() => (bool)monitor.GetType().GetProperty("IsRunning", Instance)!.GetValue(monitor)!;
        bool InputInstalled() => Get(monitor, "input") != null;
        Check(!Running(), "global selection starts disabled without a watcher");
        foreach (var choice in Enum.GetValues<PracticeChoice>())
        {
            using var settings = (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Instance).Single().Invoke([flow, dataRoot]);
            settings.Show(main); await Task.Delay(30);
            var practice = (ComboBox)settings.Controls.Find("knowledge-clipper.language.practice", true).Single();
            var table = (TableLayoutPanel)practice.Parent!;
            Check(table.GetRow(practice) == 0 && practice.Visible &&
                table.Parent!.RectangleToScreen(table.Parent.ClientRectangle).Contains(practice.RectangleToScreen(practice.ClientRectangle)),
                "default card format is visible at the top of the first settings page");
            practice.SelectedIndex = (int)choice;
            Button(settings, "knowledge-clipper.language.save-settings").PerformClick();
            Check(WorkflowJson.Read<LanguageSettings>(Path.Combine(dataRoot, "language-settings.json")).Practice == choice &&
                ((LanguageSettings)flow.GetType().GetProperty("Settings", Instance)!.GetValue(flow)!).Practice == choice,
                "default card format saves and immediately applies " + choice);
        }
        async Task Toggle(bool enabled)
        {
            using var settings = (Form)assembly.GetType("KnowledgeClipper.LanguageSettingsForm", true)!.GetConstructors(Instance).Single().Invoke([flow, dataRoot]);
            settings.Show(main);
            ((CheckBox)settings.Controls.Find("knowledge-clipper.language.global-selection", true).Single()).Checked = enabled;
            Button(settings, "knowledge-clipper.language.save-settings").PerformClick();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((Running() != enabled || InputInstalled() != enabled) && DateTime.UtcNow < deadline) await Task.Delay(25);
            Check(Running() == enabled && InputInstalled() == enabled, "saving global selection immediately " + (enabled ? "starts" : "stops") + " the watcher");
            Check(WorkflowJson.Read<LanguageSettings>(Path.Combine(dataRoot, "language-settings.json")).GlobalSelection == enabled, "selection switch persists " + enabled);
        }
        await Toggle(true);
        var failedInput = Get(monitor, "input")!;
        ((IDisposable)failedInput).Dispose();
        var recoveryDeadline = DateTime.UtcNow.AddSeconds(6);
        while ((Get(monitor, "input") == null || ReferenceEquals(failedInput, Get(monitor, "input"))) && DateTime.UtcNow < recoveryDeadline)
            await Task.Delay(30);
        Check(Running() && Get(monitor, "input") != null && !ReferenceEquals(failedInput, Get(monitor, "input")),
            "an exited input pump automatically restarts without toggling the user's selection setting");
        await Toggle(false);
        Check(main.Size == initialSize, "watcher and settings changes preserve floating window size");
        var connectedDot = (Form)Get(main, "selectionDot")!;
        Call(connectedDot, "ShowSelection", Snapshot(Screen.FromControl(main).WorkingArea.Location));
        Button(connectedDot, "knowledge-clipper.language.selection-dot.read").PerformClick();
        Check(!connectedDot.Visible && !Directory.Exists(Path.Combine(dataRoot, "language-drafts")), "disabled or stale dot cannot create a draft or call content services");
        Call(flow, "OpenInput");
        var questionInput = (Form)Get(flow, "inputForm")!;
        ((TextBox)questionInput.Controls.Find("knowledge-clipper.text-input.text", true).Single()).Text = "解释勾股定理";
        Button(questionInput, "knowledge-clipper.text-input.submit").PerformClick();
        await Task.Delay(50);
        var explicitQuestion = (ClipDraft)Get(flow, "latest")!;
        Check(explicitQuestion.Input.Intent == "question" && explicitQuestion.Route?.OutputKind == "question" &&
            explicitQuestion.QuestionText == "解释勾股定理" && !explicitQuestion.Input.ReadRequested,
            "explicit question dialog routes punctuation-free input directly to question without speech");
        questionInput.Close();
        await Toggle(true);
        main.Close();
        Check(!main.Visible && !main.IsDisposed && Running() && !connectedDot.IsDisposed, "closing the main window hides it and preserves the enabled watcher");
        Call(main, "RestoreFromTray");
        Check(main.Visible && main.Size == initialSize && Running(), "tray restores the same window and running watcher");
        Call(main, "ExitApplication");
        Check(!Running() && connectedDot.IsDisposed, "explicit exit disposes its watcher and dot");
        WorkflowJson.Save(args[1], new { passed = true, checks = Checks, dataRoot, scope = "production UI through semantic controls; no simulated mouse or keyboard input and no external content, TTS or Anki request" });
    }
}

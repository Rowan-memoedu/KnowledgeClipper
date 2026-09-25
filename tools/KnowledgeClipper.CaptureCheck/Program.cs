using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using KnowledgeClipper.Workflow;
using Forms = System.Windows.Forms;

// A bounded check of the production capture API. The source owns its ordinary
// native Edit control and message pump; no capture result or context is injected.
internal static partial class Program
{
    private static readonly Type CaptureType = Assembly.Load("KnowledgeClipper").GetType("KnowledgeClipper.LanguageDesktopCapture", true)!;
    private static readonly Type ClipboardType = CaptureType.GetNestedType("ClipboardState", BindingFlags.NonPublic)!;
    private static readonly List<object> Checks = new();
    [STAThread]
    private static int Main(string[] args)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        if (args.Length == 0 || args[0] is not ("source" or "clipboard" or "faults" or "context" or "plain" or "plain-blocked" or "selection-range" or "chrome-range" or "notepad-range"))
        {
            Console.Error.WriteLine("Usage: CaptureCheck source <project-root> | clipboard | faults <project-root> | context | selection-range | chrome-range <chrome.exe> <fixture.html> <isolated-profile> | plain <project-root> <receipt.json> <desktop.dll>");
            return 2;
        }
        if (args[0] is "plain" or "plain-blocked") return RunPlain(args);
        try
        {
            if (args[0] == "clipboard") CheckClipboard();
            else if (args[0] == "context") CheckContext();
            else if (args[0] == "selection-range") CheckSelectionRange();
            else if (args[0] == "chrome-range") CheckChromeRange(args);
            else if (args[0] == "notepad-range") CheckNotepadRange(args);
            else if (args[0] == "faults") CheckFaults(Path.GetFullPath(args.Length > 1 ? args[1] : Environment.CurrentDirectory));
            else CheckSource(Path.GetFullPath(args.Length > 1 ? args[1] : Environment.CurrentDirectory));
            Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", checks = Checks }, WorkflowJson.Options));
            return 0;
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            Console.WriteLine(JsonSerializer.Serialize(new { status = "INCOMPLETE", error = ex.Message, checks = Checks }, WorkflowJson.Options));
            return 1;
        }
    }

    private static void CheckContext()
    {
        var providerType = CaptureType.Assembly.GetType("KnowledgeClipper.WindowsUiAutomationContextProvider", true)!;
        var provider = Activator.CreateInstance(providerType)!;
        var read = providerType.GetMethod("TryReadFromPattern", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string tail = " In spring, the old plant was resilient, and the new one was resilient too.";
        foreach (var (text, target, expectedContext) in new[]
        {
            (new string('x', 200_100) + "." + tail, "resilient", tail),
            ("😀 严谨的人也会反复检查，而严谨的流程能保留原文。", "严谨", "😀 严谨的人也会反复检查，而严谨的流程能保留原文。")
        })
        {
            var start = text.LastIndexOf(target, StringComparison.Ordinal);
            using var source = new SourceWindow(text, start, target.Length);
            var element = AutomationElement.FromHandle(source.EditWindow);
            Require(element.TryGetCurrentPattern(TextPattern.Pattern, out var raw), "native-edit-exposes-text-pattern");
            var pattern = (TextPattern)raw;
            Require(pattern.GetSelection().Single().GetText(-1) == target, "actual-native-selection-is-test-target");
            var arguments = new object?[] { pattern, target, "sentence", null };
            var elapsed = Stopwatch.StartNew();
            Require((bool)read.Invoke(provider, arguments)!, "production-context-range-available");
            var context = arguments[3]!;
            var prefix = (string)context.GetType().GetField("Prefix", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            var suffix = (string)context.GetType().GetField("Suffix", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            Require(prefix + target + suffix == expectedContext && prefix.Length == expectedContext.LastIndexOf(target, StringComparison.Ordinal), "exact-adjacent-context-and-repeated-target-position");
            Checks.Add(new { name = "production-context-range", documentChars = text.Length, actualSelectionStart = start, contextTargetStart = prefix.Length, elapsedMs = elapsed.ElapsedMilliseconds, captureCompleted = false });
        }
        using var ambiguousSource = new SourceWindow("resilient " + new string('x', 200_100) + " resilient", 0, 0);
        var ambiguousElement = AutomationElement.FromHandle(ambiguousSource.EditWindow);
        Require(ambiguousElement.TryGetCurrentPattern(TextPattern.Pattern, out var ambiguousPattern), "native-edit-exposes-text-pattern-without-selected-range");
        var withoutRange = new object?[] { (TextPattern)ambiguousPattern, "resilient", "sentence", null };
        Require(!(bool)read.Invoke(provider, withoutRange)! && withoutRange[3] == null, "truncated-document-cannot-prove-unique-target");
    }

    private static void CheckSelectionRange()
    {
        var providerType = CaptureType.Assembly.GetType("KnowledgeClipper.WindowsUiAutomationContextProvider", true)!;
        var read = providerType.GetMethod("ReadCurrentSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var provider = Activator.CreateInstance(providerType)!;
        const string document = "First public passage. Selected public words. Final passage.";
        const string selected = "Selected public words";
        using var source = new SourceWindow(document, document.IndexOf(selected, StringComparison.Ordinal), selected.Length);
        SetForegroundWindow(source.Window);
        Thread.Sleep(100);
        var first = (string?)read.Invoke(provider, [source.Window.ToInt64()]);
        if (first != selected)
        {
            var root = AutomationElement.FromHandle(source.Window);
            var focus = AutomationElement.FocusedElement;
            var thread = GetWindowThreadProcessId(source.Window, out _);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            var hasGui = GetGUIThreadInfo(thread, ref info);
            var chain = new List<object>();
            var depth = 0;
            for (var element = focus; element != null && depth++ < 12;)
            {
                chain.Add(new { element.Current.Name, element.Current.NativeWindowHandle, sameRoot = element.Equals(root),
                    hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out _) });
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { returned = first, foreground = GetForegroundWindow().ToInt64(), sourceWindow = source.Window.ToInt64(), editWindow = source.EditWindow.ToInt64(), hasGui, guiFocus = info.Focus.ToInt64(), guiActive = info.Active.ToInt64(), chain }));
        }
        Require(first == selected,
            "live-uia-selection-reads-exact-focused-source-range-without-copy");
        using var other = new SourceWindow("Unrelated focused selection", 0, 9);
        SetForegroundWindow(other.Window);
        Thread.Sleep(100);
        Require((string?)read.Invoke(provider, [source.Window.ToInt64()]) == selected,
            "live-uia-selection-remains-bound-to-the-requested-source-window");
        Require((string?)read.Invoke(provider, [other.Window.ToInt64()]) == "Unrelated",
            "live-uia-selection-can-read-the-new-focused-source-only");
        using var collapsed = new SourceWindow("Collapsed range", 3, 0);
        SetForegroundWindow(collapsed.Window);
        Thread.Sleep(100);
        Require(read.Invoke(provider, [collapsed.Window.ToInt64()]) == null,
            "live-uia-selection-rejects-a-collapsed-range");
        using var whitespace = new SourceWindow("Left.   Right.", 5, 3);
        SetForegroundWindow(whitespace.Window);
        Thread.Sleep(100);
        Require(read.Invoke(provider, [whitespace.Window.ToInt64()]) == null,
            "live-uia-selection-rejects-whitespace-only-ranges");
    }

    private static void CheckChromeRange(string[] args)
    {
        if (args.Length is not (4 or 5) || args.Length == 5 && args[4] != "--default-accessibility" ||
            !Path.GetFullPath(args[3]).StartsWith(@"D:\Caches\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chrome selection fixture requires an isolated D: cache profile.");
        var previousForeground = GetForegroundWindow();
        Directory.CreateDirectory(args[3]);
        var start = new ProcessStartInfo(Path.GetFullPath(args[1])) { UseShellExecute = false, CreateNoWindow = false };
        start.ArgumentList.Add("--user-data-dir=" + Path.GetFullPath(args[3]));
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--disable-extensions");
        start.ArgumentList.Add("--disable-background-networking");
        if (args.Length == 4) start.ArgumentList.Add("--force-renderer-accessibility=complete");
        start.ArgumentList.Add("--app=" + new Uri(Path.GetFullPath(args[2])).AbsoluteUri);
        using var chrome = Process.Start(start) ?? throw new InvalidOperationException("isolated Chrome did not start");
        try
        {
            var timer = Stopwatch.StartNew();
            nint window = 0;
            while (timer.Elapsed < TimeSpan.FromSeconds(15) && window == 0)
            {
                chrome.Refresh(); window = chrome.MainWindowHandle;
                if (window == 0) Thread.Sleep(100);
            }
            Require(window != 0 && !chrome.HasExited, "isolated Chrome exposes its fixture window");
            if (previousForeground != 0) SetForegroundWindow(window);
            Thread.Sleep(1500);
            var providerType = CaptureType.Assembly.GetType("KnowledgeClipper.WindowsUiAutomationContextProvider", true)!;
            var provider = Activator.CreateInstance(providerType)!;
            var read = providerType.GetMethod("ReadCurrentSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var selection = (string?)read.Invoke(provider, [window.ToInt64()]);
            if (selection != "The browser still knows these selected words.")
            {
                var root = AutomationElement.FromHandle(window);
                var textElements = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
                var details = new List<object>();
                for (var i = 0; i < Math.Min(textElements.Count, 12); i++)
                {
                    var element = textElements[i];
                    var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                    details.Add(new { element.Current.Name, type = element.Current.ControlType.ProgrammaticName,
                        element.Current.IsOffscreen, selected = pattern.GetSelection()?.Select(range => range.GetText(200)).ToArray() });
                }
                var thread = GetWindowThreadProcessId(window, out _);
                var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
                var hasGui = GetGUIThreadInfo(thread, ref gui);
                var documentRead = providerType.GetMethod("ReadVisibleDocumentSelection", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [root]);
                var secondRead = (string?)read.Invoke(provider, [window.ToInt64()]);
                Console.WriteLine(JsonSerializer.Serialize(new { returned = selection, window = window.ToInt64(), hasGui,
                    guiFocus = gui.Focus.ToInt64(), guiRoot = GetAncestor(gui.Focus, 2).ToInt64(), documentRead, secondRead,
                    textElements = details }));
            }
            Require(selection == "The browser still knows these selected words.",
                "copy-blocked Chrome page exposes the exact live selected text without clipboard access");
        }
        finally
        {
            if (!chrome.HasExited) chrome.Kill(entireProcessTree: true);
            if (previousForeground != 0) SetForegroundWindow(previousForeground);
        }
    }

    private static void CheckNotepadRange(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var processId))
            throw new ArgumentException("notepad-range <isolated-notepad-pid>");
        const string expected = "Notepad can expose these selected words.";
        var previousForeground = GetForegroundWindow();
        using var notepad = Process.GetProcessById(processId);
        try
        {
            var timer = Stopwatch.StartNew();
            nint window = 0;
            while (timer.Elapsed < TimeSpan.FromSeconds(15) && window == 0 && !notepad.HasExited)
            {
                notepad.Refresh(); window = notepad.MainWindowHandle;
                if (window == 0) Thread.Sleep(100);
            }
            if (window == 0 || notepad.HasExited)
                Console.WriteLine(JsonSerializer.Serialize(new { notepad.Id, notepad.HasExited,
                    exitCode = notepad.HasExited ? notepad.ExitCode : (int?)null, window = window.ToInt64() }));
            Require(window != 0 && !notepad.HasExited, "independent Notepad exposes its fixture window");
            SetForegroundWindow(window);
            Thread.Sleep(500);
            var root = AutomationElement.FromHandle(window);
            var documents = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            TextPatternRange? target = null;
            var diagnostics = new List<object>();
            foreach (AutomationElement element in documents)
            {
                var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                TextPatternRange? range = null;
                string? error = null;
                try { range = pattern.DocumentRange.FindText(expected, backward: false, ignoreCase: false); }
                catch (NotSupportedException ex) { error = ex.Message; }
                diagnostics.Add(new { element.Current.Name, type = element.Current.ControlType.ProgrammaticName,
                    element.Current.ClassName, element.Current.NativeWindowHandle,
                    element.Current.IsOffscreen, hasTarget = range != null, error });
                if (range != null && target == null) target = range;
            }
            if (target == null) Console.WriteLine(JsonSerializer.Serialize(new { diagnostics }));
            Require(target != null, "Notepad exposes exact fixture text through UIA TextPattern");
            try { target!.Select(); }
            catch (NotSupportedException)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { diagnostics }));
                throw;
            }
            Thread.Sleep(250);
            var providerType = CaptureType.Assembly.GetType("KnowledgeClipper.WindowsUiAutomationContextProvider", true)!;
            var provider = Activator.CreateInstance(providerType)!;
            var read = providerType.GetMethod("ReadCurrentSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var selected = (string?)read.Invoke(provider, [window.ToInt64()]);
            if (selected != expected) Console.WriteLine(JsonSerializer.Serialize(new { selected, diagnostics }));
            Require(selected == expected, "Notepad live selected range is readable without Ctrl+C");
        }
        finally
        {
            if (previousForeground != 0) SetForegroundWindow(previousForeground);
        }
    }

    private static void CheckFaults(string root)
    {
        var sequence = GetClipboardSequenceNumber();
        var settings = new LanguageSettings { ContextEnabled = false };
        foreach (var (helper, expected) in new[]
        {
            (Path.Combine(root, "tools", "KnowledgeClipper.CaptureCheck", "fixtures", "absent.cjs"), "启动失败"),
            (Path.Combine(root, "tools", "KnowledgeClipper.CaptureCheck", "fixtures", "hanging.cjs"), "响应超时")
        })
        {
            var capture = Activator.CreateInstance(CaptureType, helper)!;
            var timer = Stopwatch.StartNew();
            var task = (Task<CaptureMaterial>)CaptureType.GetMethod("CaptureAsync")!.Invoke(capture, [settings, null])!;
            var pulses = 0;
            while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(12))
            {
                Forms.Application.DoEvents();
                pulses++;
                Thread.Sleep(10);
            }
            string? error = null;
            try { _ = task.GetAwaiter().GetResult(); }
            catch (Exception ex) { error = ex.Message; }
            Require(error?.Contains(expected) == true, expected);
            Require(pulses > 1 && timer.Elapsed < TimeSpan.FromSeconds(9), "capture-failure-does-not-block-caller");
            Require(GetClipboardSequenceNumber() == sequence, "capture-failure-left-clipboard-untouched");
            Checks.Add(new { name = expected, elapsedMs = timer.ElapsedMilliseconds, pulses });
        }
    }

    private static void CheckSource(string root)
    {
        const string sourceText = "They remain resilient when plans change, and resilient when progress is slow.";
        const string selectedText = "resilient";
        var start = sourceText.LastIndexOf(selectedText, StringComparison.Ordinal);
        using var source = new SourceWindow(sourceText, start, selectedText.Length);
        Checks.Add(new { name = "native-source-window", source.VisibleBeforeShow, visibleAfterShow = IsWindowVisible(source.Window), foregroundIsSource = GetForegroundWindow() == source.Window, window = source.Window.ToInt64() });
        Require(GetForegroundWindow() == source.Window, "source-foreground-not-acquired; no production capture attempted");
        var settings = new LanguageSettings { ContextEnabled = true, ContextGranularity = "sentence" };
        var capture = Activator.CreateInstance(CaptureType, Path.Combine(root, "helpers", "selection.cjs"))!;
        var timer = Stopwatch.StartNew();
        var task = (Task<CaptureMaterial>)CaptureType.GetMethod("CaptureAsync")!.Invoke(capture, [settings, null])!;
        var pulses = 0;
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(16))
        {
            // The same STA can still service normal UI work while capture awaits
            // Node/UIA; this does not claim latency for every external provider.
            Forms.Application.DoEvents();
            pulses++;
            Thread.Sleep(10);
        }
        var material = task.GetAwaiter().GetResult();
        Require(material.SelectedText == selectedText, "production-selected-text");
        Require(material.ContextOrigin == "source", "production-real-uia-context");
        Require(material.ContextText == sourceText && material.TargetStart == start, "second-identical-word-position");
        Require(pulses > 1, "caller-message-pump-remained-responsive");
        Checks.Add(new { name = "production-capture", elapsedMs = timer.ElapsedMilliseconds, pulses, source = "synthetic-native-edit", targetStart = material.TargetStart });
    }

    private static void CheckClipboard()
    {
        using var owner = new Forms.Control();
        var handle = owner.Handle;
        var original = Snapshot(handle);
        uint restoreSequence = 0;
        try
        {
            var formats = new Forms.DataObject();
            formats.SetText("原始 clipboard 😀", Forms.TextDataFormat.UnicodeText);
            formats.SetText("<b>保留格式</b>", Forms.TextDataFormat.Html);
            formats.SetData("KnowledgeClipper.CaptureCheck.binary", false, new MemoryStream([0, 1, 2, 0, 255]));
            formats.SetFileDropList(new System.Collections.Specialized.StringCollection { @"D:\Artifacts\KnowledgeClipper\validation\m1-source.txt" });
            using var bitmap = new System.Drawing.Bitmap(3, 2);
            bitmap.SetPixel(1, 1, System.Drawing.Color.CornflowerBlue);
            formats.SetData(Forms.DataFormats.Bitmap, bitmap);
            Forms.Clipboard.SetDataObject(formats, true);
            Require(Forms.Clipboard.GetFileDropList().Count == 1, "file-drop-fixture-is-readable");
            var saved = Snapshot(handle);
            try
            {
                Forms.Clipboard.SetText("test-current-selection");
                var copiedSequence = GetClipboardSequenceNumber();
                Require(Restore(saved, handle, copiedSequence), "native-conditional-restore");
                var after = Forms.Clipboard.GetDataObject()!;
                Require(Forms.Clipboard.GetText() == "原始 clipboard 😀", "unicode-format-roundtrip");
                Require((string)after.GetData(Forms.DataFormats.Html)! == "<b>保留格式</b>", "html-format-roundtrip");
                Require(((MemoryStream)after.GetData("KnowledgeClipper.CaptureCheck.binary", false)!).ToArray().SequenceEqual(new byte[] { 0, 1, 2, 0, 255 }), "binary-format-roundtrip");
                Require(Forms.Clipboard.GetFileDropList().Count == 1 && Forms.Clipboard.GetFileDropList()[0]!.EndsWith("m1-source.txt"), "file-drop-format-roundtrip");
                using var image = Forms.Clipboard.GetImage();
                Require(image is System.Drawing.Bitmap restored && restored.GetPixel(1, 1).ToArgb() == bitmap.GetPixel(1, 1).ToArgb(), "bitmap-format-roundtrip");
            }
            finally { ((IDisposable)saved).Dispose(); }

            Forms.Clipboard.SetText("prior-selection");
            var prior = Snapshot(handle);
            try
            {
                Forms.Clipboard.SetText("captured-selection");
                var capturedSequence = GetClipboardSequenceNumber();
                Forms.Clipboard.SetText("new-independent-copy");
                Require(!Restore(prior, handle, capturedSequence), "concurrent-copy-rejects-stale-restore");
                Require(Forms.Clipboard.GetText() == "new-independent-copy", "concurrent-copy-retained");
            }
            finally { ((IDisposable)prior).Dispose(); }

            var arguments = new object?[] { handle, GetClipboardSequenceNumber(), (uint)Environment.ProcessId, CancellationToken.None, (uint)0 };
            var unchanged = ClipboardType.GetMethod("ReadCopiedText")!.Invoke(null, arguments);
            Require(unchanged == null && (uint)arguments[4]! == 0, "unchanged-clipboard-is-never-selection");
            arguments = [handle, GetClipboardSequenceNumber() - 1, uint.MaxValue, CancellationToken.None, (uint)0];
            var ownerRejected = false;
            try { ClipboardType.GetMethod("ReadCopiedText")!.Invoke(null, arguments); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { ownerRejected = true; }
            Require(ownerRejected && Forms.Clipboard.GetText() == "new-independent-copy", "different-owner-is-never-selection");
        }
        finally
        {
            // Restore only if this process still owns the exact last test value.
            restoreSequence = GetClipboardSequenceNumber();
            var restored = Restore(original, handle, restoreSequence);
            ((IDisposable)original).Dispose();
            if (!restored) Checks.Add(new { name = "user-clipboard-restore", status = "new-external-copy-retained" });
        }
    }

    private static object Snapshot(nint owner) => ClipboardType.GetMethod("Take")!.Invoke(null, [owner, CancellationToken.None])!;
    private static bool Restore(object snapshot, nint owner, uint sequence) => (bool)ClipboardType.GetMethod("RestoreIfCurrent")!.Invoke(snapshot, [owner, sequence, (uint)Environment.ProcessId])!;
    private static void Require(bool valid, string name)
    {
        Checks.Add(new { name, passed = valid });
        if (!valid) throw new InvalidOperationException(name);
    }

    private sealed class SourceWindow : IDisposable
    {
        private readonly Thread thread;
        private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Forms.Form? form;
        public nint EditWindow { get; private set; }
        public bool VisibleBeforeShow { get; private set; }
        public nint Window => ready.Task.GetAwaiter().GetResult();
        public SourceWindow(string text, int start, int length)
        {
            thread = new Thread(() =>
            {
                try
                {
                    using var window = new Forms.Form { Text = "KnowledgeClipper CaptureCheck synthetic source", Width = 720, Height = 230 };
                    using var edit = new Forms.TextBox { Multiline = true, Dock = Forms.DockStyle.Fill, Text = text };
                    form = window;
                    window.Controls.Add(edit);
                    window.Shown += (_, _) => window.BeginInvoke(new Action(() =>
                    {
                        VisibleBeforeShow = IsWindowVisible(window.Handle);
                        if (!VisibleBeforeShow) ShowWindow(window.Handle, 5);
                        window.Activate();
                        edit.Focus();
                        edit.Select(start, length);
                        EditWindow = edit.Handle;
                        ready.TrySetResult(window.Handle);
                    }));
                    Forms.Application.Run(window);
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true, Name = "CaptureCheck.Source" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("source-message-pump-start-timeout");
        }
        public void Dispose()
        {
            if (form is { IsDisposed: false, IsHandleCreated: true }) form.BeginInvoke(new Action(form.Close));
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public System.Drawing.Rectangle CaretRect;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}

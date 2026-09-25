using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ClipboardSentinel = "KnowledgeClipper public capture-check clipboard";
    private static readonly byte[] ClipboardBinary = [0, 17, 0, 93, 255];
    private static readonly List<object> PlainCases = [];

    private static int RunPlain(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("plain <project-root> <receipt.json> <desktop.dll>");
        var receipt = Path.GetFullPath(args[2]);
        if (!receipt.StartsWith(@"D:\Artifacts\KnowledgeClipper\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use an isolated D:\\Artifacts\\KnowledgeClipper receipt.");
        var status = "INCOMPLETE";
        string? error = null;
        try
        {
            Require(Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST") == "1" &&
                CurrentDesktopName().StartsWith("KnowledgeClipperUiTests_", StringComparison.Ordinal),
                "plain-check-runs-on-private-test-desktop");
            var desktopPath = Path.GetFullPath(args[3]);
            Require(HashFile(CaptureType.Assembly.Location) == HashFile(desktopPath), "loaded-desktop-matches-requested-production-sha256");
            Require(HashFile(typeof(LanguageSettings).Assembly.Location) == HashFile(Path.Combine(Path.GetDirectoryName(desktopPath)!, "KnowledgeClipper.Workflow.dll")),
                "loaded-workflow-matches-requested-production-sha256");
            CheckPlain(Path.GetFullPath(args[1]), args[0] == "plain-blocked");
            status = "PASS";
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            error = ex.GetType().Name + ": " + ex.Message;
        }
        var result = new { status, error, checks = Checks, cases = PlainCases,
            desktopSha256 = HashFile(CaptureType.Assembly.Location), workflowSha256 = HashFile(typeof(LanguageSettings).Assembly.Location),
            scope = "Isolated native Edit controls. Injected dependencies are limited to the known test source HWND, a WM_COPY delegate restricted to that HWND and this process, and UIA discovery via AutomationElement.FromHandle(testEdit). The production TryReadFromPattern validates the actual native selection and context. The full production capture policy, guarded clipboard transaction, PID and input checks, and English completion are exercised. System foreground/focus discovery and the actual SendInput shortcut are not tested. No browser access, real user selection capture, model calls, imports or user settings writes. Original clipboard formats are restored conditionally; concurrent external copies are never overwritten." };
        WorkflowJson.Save(receipt, result);
        Console.WriteLine(JsonSerializer.Serialize(result, WorkflowJson.Options));
        return status == "PASS" ? 0 : 1;
    }

    private static void CheckPlain(string root, bool blockedOnly)
    {
        using var owner = new Forms.Control();
        var handle = owner.Handle;
        using var original = (IDisposable)Snapshot(handle);
        var originalFormats = SnapshotFormats(original);
        var sources = new List<SourceWindow>();
        uint ownedSequence = 0, ownedPid = 0;
        try
        {
            var formats = new Forms.DataObject();
            formats.SetText(ClipboardSentinel, Forms.TextDataFormat.UnicodeText);
            formats.SetText("<p>Public capture check</p>", Forms.TextDataFormat.Html);
            formats.SetData("KnowledgeClipper.CaptureCheck.plain", false, new MemoryStream(ClipboardBinary));
            Require(GetClipboardSequenceNumber() == (uint)ClipboardType.GetProperty("Sequence")!.GetValue(original)!,
                "clipboard-has-not-changed-before-isolated-fixture-setup");
            Forms.Clipboard.SetDataObject(formats, true);
            ownedSequence = GetClipboardSequenceNumber();
            GetWindowThreadProcessId(GetClipboardOwner(), out ownedPid);
            RememberFixtureClipboard();
            const string paragraph = "抽象是把复杂实现封装在明确的接口之后。可编程性是开发者通过某种\r\n接口改变系统行为的能力。传统应用主要用程序代码表达行为，编译器将代码转换为可执行指令。";
            const string target = "可编程性是开发者通过某种\r\n接口改变系统行为的能力";
            if (!blockedOnly)
            {
            foreach (var (name, frozen) in new[]
            {
                ("over-selected-uia-candidate", target + "。传统应用主要用程序代码表达行为，编译器将代码转换为可执行指令"),
                ("under-selected-uia-candidate", "接口改变系统行为的能力"),
                ("wrong-neighbor-uia-candidate", "抽象是把复杂实现封装在明确的接口之后"),
                ("no-frozen-candidate", (string?)null)
            })
                CaptureCase(name, paragraph, paragraph.IndexOf(target, StringComparison.Ordinal), target.Length, frozen, target, false);

            const string whitespaceDocument = "上一句。\r\n  选中的原文保留空白。\r\n下一句。";
            const string whitespaceTarget = "\r\n  选中的原文保留空白。\r\n";
            CaptureCase("selected-whitespace-and-linebreaks-preserved", whitespaceDocument,
                whitespaceDocument.IndexOf(whitespaceTarget, StringComparison.Ordinal), whitespaceTarget.Length, "原文", whitespaceTarget, false);
            const string english = "They greet hello warmly, and say hello again.";
            var englishStart = english.LastIndexOf("hello", StringComparison.Ordinal);
            foreach (var enabled in new[] { false, true })
                CaptureCase("english-partial-keeps-verified-completion-context-" + enabled, english, englishStart + 1, 3,
                    "unrelated prior English selection", "hello", enabled, expectedContext: enabled ? english : null,
                    expectedStart: enabled ? englishStart : null);

            CaptureCase("collapsed-selection-rejects-old-candidate", paragraph, 8, 0, target, null, false);
            CaptureCase("empty-edit-rejects-old-candidate", "", 0, 0, target, null, false);
            CaptureCase("whitespace-only-selection-rejects-old-candidate", "Left.   Right.", 5, 3, target, null, false);
            CaptureCase("wrong-source-pid-rejected-before-copy", paragraph, paragraph.IndexOf(target, StringComparison.Ordinal),
                target.Length, target, null, false, wrongPid: true);
            }
            if (blockedOnly)
            {
            CaptureCase("uia-primary-reads-live-selected-range-without-copy", paragraph, paragraph.IndexOf(target, StringComparison.Ordinal),
                target.Length, "wrong old candidate", target, false, blockCopy: true);
            CaptureCase("uia-unavailable-uses-protected-copy", paragraph, paragraph.IndexOf(target, StringComparison.Ordinal),
                target.Length, "wrong old candidate", target, false, suppressUia: true);
            CaptureCase("uia-and-copy-unavailable-rejects-old-candidate", paragraph, paragraph.IndexOf(target, StringComparison.Ordinal),
                target.Length, "wrong old candidate", null, false, blockCopy: true, suppressUia: true);
            }
            void CaptureCase(string name, string document, int start, int length, string? frozenText, string? expected,
                bool contextEnabled, string? expectedContext = null, int? expectedStart = null, bool wrongPid = false,
                bool blockCopy = false, bool suppressUia = false)
            {
                var source = new SourceWindow(document, start, length); sources.Add(source);
                Require(IsWindowVisible(source.Window), name + ": exact-native-source-is-visible-on-private-desktop");
                var copies = 0; var contextReads = 0;
                Func<long, bool> copy = window =>
                {
                    if (window != source.Window.ToInt64()) throw new InvalidOperationException("test-copy-target-was-not-the-exact-native-source");
                    GetWindowThreadProcessId(source.EditWindow, out var pid);
                    if (pid != (uint)Environment.ProcessId) throw new InvalidOperationException("test-copy-target-left-this-process");
                    Interlocked.Increment(ref copies);
                    if (blockCopy) return true; // The source ignores Ctrl+C; the old clipboard remains untouched.
                    return SendMessageTimeout(source.EditWindow, 0x0301, nint.Zero, nint.Zero, 0x0002, 2000, out _) != nint.Zero;
                };
                Func<long> foreground = () =>
                {
                    GetWindowThreadProcessId(source.Window, out var pid);
                    if (pid != (uint)Environment.ProcessId) throw new InvalidOperationException("test-source-left-this-process");
                    return source.Window.ToInt64();
                };
                var contextType = CaptureType.Assembly.GetType("KnowledgeClipper.WindowsContextSnapshot", true)!;
                var contextDelegateType = typeof(Func<,,>).MakeGenericType(typeof(string), typeof(string), contextType);
                Func<string, string, object?> context = (text, granularity) =>
                {
                    Interlocked.Increment(ref contextReads);
                    return ReadNativeContext(source, text, granularity);
                };
                var selectedParameter = Expression.Parameter(typeof(string));
                var granularityParameter = Expression.Parameter(typeof(string));
                var contextDelegate = Expression.Lambda(contextDelegateType,
                    Expression.Convert(Expression.Invoke(Expression.Constant(context), selectedParameter, granularityParameter), contextType),
                    selectedParameter, granularityParameter).Compile();
                // The legacy cases force UIA unavailable to retain their copy-path checks.
                // The new cases use the production UIA provider unless explicitly suppressed.
                var helper = Path.Combine(root, "tools", "KnowledgeClipper.CaptureCheck", "fixtures", "absent.cjs");
                var copyOnly = !blockedOnly || suppressUia;
                var capture = copyOnly
                    ? CaptureType.GetConstructor(AllInstance, null, [typeof(string), typeof(Func<long, bool>), typeof(Func<long>), contextDelegateType, typeof(Func<long, string>)], null)!
                        .Invoke([helper, copy, foreground, contextDelegate, (Func<long, string>)(_ => null!)])
                    : CaptureType.GetConstructor(AllInstance, null, [typeof(string), typeof(Func<long, bool>), typeof(Func<long>), contextDelegateType], null)!
                        .Invoke([helper, copy, foreground, contextDelegate]);
                var frozen = frozenText == null ? null : Frozen(source, frozenText, wrongPid ? uint.MaxValue : (uint)Environment.ProcessId);
                if (blockedOnly) Thread.Sleep(300); // Let the private desktop's new focus/input messages settle.
                var timer = Stopwatch.StartNew();
                var task = (Task<CaptureMaterial>)CaptureType.GetMethod("CapturePlainTextAsync")!
                    .Invoke(capture, [new LanguageSettings { ContextEnabled = contextEnabled, ContextGranularity = "sentence" }, frozen])!;
                var pulses = 0;
                while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(14))
                { Forms.Application.DoEvents(); pulses++; Thread.Sleep(10); }
                Require(task.IsCompleted, name + ": bounded-production-capture-completed");
                CaptureMaterial? actual = null;
                string? failure = null;
                try { actual = task.GetAwaiter().GetResult(); }
                catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
                PlainCases.Add(new { name, selectedText = document.Substring(start, length), frozenText,
                    actual = actual?.SelectedText, failure, copies, contextReads, elapsedMs = timer.ElapsedMilliseconds, pulses });
                RememberFixtureClipboard();
                Require(copies == (wrongPid || !copyOnly ? 0 : 1), name + ": guarded-copy-action-count");
                Require(contextReads == (expected == null ? 0 : 1), name + ": native-context-query-count");
                Require(expected == null ? actual == null && failure != null : actual?.SelectedText == expected && failure == null,
                    name + (expected == null ? ": does-not-fall-back-to-frozen-or-clipboard-text" : ": actual-native-selection-wins"));
                if (expected != null)
                {
                    Require(actual!.ContextText == expectedContext && actual.TargetStart == expectedStart,
                        name + ": context-and-target-offset-match-verified-selection");
                    Require(actual.SourceWindow == source.Window.ToInt64(), name + ": material-keeps-exact-source-window");
                }
                Console.WriteLine("PASS plain " + name);
            }

            void RememberFixtureClipboard()
            {
                var sequence = GetClipboardSequenceNumber();
                GetWindowThreadProcessId(GetClipboardOwner(), out var pid);
                var data = Forms.Clipboard.GetDataObject()!;
                Require(Forms.Clipboard.GetText() == ClipboardSentinel &&
                    (string?)data.GetData(Forms.DataFormats.Html) == "<p>Public capture check</p>" &&
                    data.GetData("KnowledgeClipper.CaptureCheck.plain", false) is MemoryStream binary && binary.ToArray().SequenceEqual(ClipboardBinary),
                    "guarded-capture-preserves-text-html-and-binary-clipboard-formats");
                Require(sequence == GetClipboardSequenceNumber() && (pid == 0 || pid == (uint)Environment.ProcessId),
                    "clipboard-fixture-remains-the-same-test-owned-value");
                ownedSequence = sequence; ownedPid = pid;
            }
        }
        finally
        {
            try
            {
                if (ownedSequence != 0)
                {
                    var restored = (bool)ClipboardType.GetMethod("RestoreIfCurrent")!
                        .Invoke(original, [handle, ownedSequence, ownedPid])!;
                    Checks.Add(new { name = "original-user-clipboard-restoration", restored, concurrentCopyRetained = !restored });
                    if (restored)
                    {
                        using var after = (IDisposable)Snapshot(handle);
                        Require(SnapshotFormats(after).SequenceEqual(originalFormats), "all-original-clipboard-format-ids-restored");
                    }
                }
            }
            finally { foreach (var source in sources) source.Dispose(); }
        }
    }

    private static object? ReadNativeContext(SourceWindow source, string selected, string granularity)
    {
        var element = System.Windows.Automation.AutomationElement.FromHandle(source.EditWindow);
        Require(element.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var raw),
            "own-native-edit-exposes-real-uia-text-pattern");
        var pattern = (System.Windows.Automation.TextPattern)raw;
        Require(pattern.GetSelection().Single().GetText(-1) == selected,
            "real-native-uia-range-exactly-matches-the-newly-copied-text");
        var type = CaptureType.Assembly.GetType("KnowledgeClipper.WindowsUiAutomationContextProvider", true)!;
        var arguments = new object?[] { pattern, selected, granularity, null };
        var found = (bool)type.GetMethod("TryReadFromPattern", AllInstance)!.Invoke(Activator.CreateInstance(type), arguments)!;
        return found ? arguments[3] : null;
    }

    private static object Frozen(SourceWindow source, string text, uint pid)
    {
        var type = CaptureType.Assembly.GetType("KnowledgeClipper.SelectionSnapshot", true)!;
        return type.GetConstructors(AllInstance).Single(item => item.GetParameters().Length == 9)
            .Invoke([text, source.Window.ToInt64(), "CaptureCheck private native Edit", 0, 0, DateTimeOffset.UtcNow, 0L, 0L, pid]);
    }
    private static uint[] SnapshotFormats(object snapshot) => ((IEnumerable)ClipboardType.GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(snapshot)!).Cast<object>().Select(entry => (uint)entry.GetType().GetField("Item1")!.GetValue(entry)!).Order().ToArray();
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string CurrentDesktopName()
    {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, text, text.Capacity * 2, out _))
            throw new InvalidOperationException("cannot-verify-private-test-desktop");
        return text.ToString();
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}

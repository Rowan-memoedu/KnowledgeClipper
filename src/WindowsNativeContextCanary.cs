using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        // Input-only: real UIA TextPattern and production capture adapter. No Desktop
        // composition, Core, provider, SendInput, system clipboard write or export.
        private static int NativeContextCanary(string root)
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("immutable-native-context-root-exists");
            Directory.CreateDirectory(root);
            var cases = new List<object>();
            var failures = new List<string>();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var source = new Form { Text = "KnowledgeClipper controlled source", Width = 720, Height = 240 };
            using var decoy = new Form { Text = "KnowledgeClipper separate source", Width = 720, Height = 240 };
            using var empty = new Form { Text = "KnowledgeClipper no-document source", Width = 360, Height = 160 };
            var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, HideSelection = false };
            var other = new TextBox { Multiline = true, Dock = DockStyle.Fill, HideSelection = false, Text = "FOREIGN_PREFIX deliberate FOREIGN_SUFFIX" };
            var button = new Button { Text = "No document", Dock = DockStyle.Fill };
            source.Controls.Add(text); decoy.Controls.Add(other); empty.Controls.Add(button);
            source.Show(); decoy.Show(); empty.Show();
            other.Select(other.Text.IndexOf("deliberate", StringComparison.Ordinal), "deliberate".Length);

            void Run(string name, string document, string selected, string granularity, string prefix, string suffix, bool available, string clipboardOverride = null, bool noDocument = false, bool collapseSelection = false)
            {
                text.Text = document;
                var index = document.IndexOf(selected, StringComparison.Ordinal);
                source.Activate(); text.Focus();
                if (noDocument) { empty.Activate(); button.Focus(); }
                var expectedHandle = noDocument ? button.Handle : text.Handle;
                var focus = Task.Run(() => System.Windows.Automation.AutomationElement.FocusedElement?.Current.NativeWindowHandle ?? 0);
                WaitUntil(() => focus.IsCompleted, 5000, "controlled source UIA focus lookup");
                if (focus.GetAwaiter().GetResult() != expectedHandle.ToInt32()) throw new InvalidOperationException("controlled-source-focus-not-established");
                // Set the range after the activation/focus transaction has drained;
                // first activation of a TextBox may otherwise replace its selection.
                text.Select(Math.Max(0, index), collapseSelection ? 0 : selected.Length);
                // Simulate only Ctrl+C transport: selected text comes from the real
                // control's programmatic selection. Context is NEVER supplied here.
                var selectedFromControl = clipboardOverride ?? (collapseSelection ? selected : text.SelectedText);
                var nativeSelectionRead = Task.Run(() =>
                {
                    var element = System.Windows.Automation.AutomationElement.FocusedElement;
                    if (!element.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var raw)) return new string[0];
                    return ((System.Windows.Automation.TextPattern)raw).GetSelection().Select(range => range.GetText(-1)).ToArray();
                });
                WaitUntil(() => nativeSelectionRead.IsCompleted, 5000, "native selection observation");
                var nativeSelections = nativeSelectionRead.GetAwaiter().GetResult();
                Console.WriteLine("NATIVE_SELECTION " + name + " " + JsonSerializer.Serialize(new { selectedFromControl, nativeSelections }));
                var clipboard = new ScriptedTextClipboard("stale unrelated clipboard", selectedFromControl);
                var adapter = new WindowsCurrentTextCapturePort(clipboard, new SuccessfulCopySender(), 2, 1,
                    new WindowsUiAutomationContextProvider(), () => new ContextModeState { enabled = true, granularity = granularity });
                var capture = adapter.CaptureOnceAsync(CancellationToken.None);
                WaitUntil(() => capture.IsCompleted, 8000, "native context extraction finishes");
                string actualText = null, actualPrefix = null, actualSuffix = null, error = null;
                try
                {
                    var result = capture.GetAwaiter().GetResult();
                    actualText = result.Text;
                    actualPrefix = result.OrderedContextSegments.FirstOrDefault(segment => segment.Role == "prefix")?.Text ?? "";
                    actualSuffix = result.OrderedContextSegments.FirstOrDefault(segment => segment.Role == "suffix")?.Text ?? "";
                }
                catch (InvalidOperationException exception) { error = exception.Message; }
                var passed = available ? error == null && actualText == selected && actualPrefix == prefix && actualSuffix == suffix : error == "context.unavailable";
                cases.Add(new { name, document, selectedFromControl, nativeSelections, granularity, expected = new { available, prefix, suffix }, actual = new { text = actualText, prefix = actualPrefix, suffix = actualSuffix, error }, passed });
                Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(new { actualText, actualPrefix, actualSuffix, error }));
                if (!passed) failures.Add(name);
            }

            try
            {
                Run("english-exact-boundaries", "A deterministic external sentence with deliberate context.", "deliberate", "sentence", "A deterministic external sentence with ", " context.", true);
                Run("chinese-paragraph", "前段。\r\n本段包含审慎选择的说明。\r\n后段。", "审慎", "paragraph", "本段包含", "选择的说明。", true);
                Run("selected-only-unavailable", "deliberate", "deliberate", "sentence", null, null, false);
                Run("other-window-not-searched", "local unrelated document", "local", "sentence", null, null, false, "deliberate", noDocument: true);
                Run("clipboard-selection-mismatch", "The selected alpha has context.", "alpha", "sentence", null, null, false, "unrelated clipboard");
                Run("ambiguous-collapsed-selection", "Repeated alpha and alpha remain.", "alpha", "sentence", null, null, false, collapseSelection: true);
                Run("independent-owner-excludes-decoy", "本窗口目标前文和后文", "目标", "paragraph", "本窗口", "前文和后文", true);
                Run("unique-collapsed-selection", "Unique needle in own source.", "needle", "sentence", "Unique ", " in own source.", true, collapseSelection: true);
            }
            catch (Exception exception) { failures.Add(exception.Message); Console.WriteLine("NATIVE_CONTEXT_FAILURE " + exception); }
            finally { empty.Close(); decoy.Close(); source.Close(); }

            var receipt = new { schemaVersion = "desktop-native-context-boundary.v1", status = failures.Count == 0 ? "PASS" : "FAIL", cases, failures,
                mechanism = "production WindowsCurrentTextCapturePort + WindowsUiAutomationContextProvider; real controlled WinForms TextPattern; programmatic selection/focus; simulated Ctrl+C transport only",
                sendInput = 0, systemClipboardWrites = 0, workers = 0, anki = 0, speaker = 0, physicalCaptureValidated = false,
                boundaries = new[] { "controlled TextBox only, not arbitrary browser/editor support", "no third-party focus, physical keyboard or Ctrl+C validation", "no candidate rebuilt or business run" } };
            File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine("NATIVE_CONTEXT " + receipt.status + " cases=" + cases.Count + " failures=" + failures.Count);
            return failures.Count == 0 ? 0 : 1;
        }
    }
}

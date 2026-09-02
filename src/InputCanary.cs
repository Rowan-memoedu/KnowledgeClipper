using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KnowledgeClipper
{
    internal static class InputCanary
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            IDataObject originalClipboard = null;
            try { originalClipboard = Clipboard.GetDataObject(); } catch { }
            const string marker = "KNOWLEDGE_CLIPPER_CTRL_C_CANARY";
            var form = new Form { Width = 520, Height = 120, StartPosition = FormStartPosition.CenterScreen, TopMost = true, Text = "KnowledgeClipper Ctrl+C Canary" };
            var box = new TextBox { Dock = DockStyle.Fill, Text = marker };
            form.Controls.Add(box);
            var stage = 0;
            var timer = new Timer { Interval = 500 };
            timer.Tick += delegate
            {
                if (stage == 0)
                {
                    var foreground = GetForegroundWindow(); var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero); var currentThread = GetCurrentThreadId();
                    if (foregroundThread != 0 && foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, true);
                    form.Activate(); form.BringToFront(); SetForegroundWindow(form.Handle); box.Focus(); box.SelectAll();
                    if (foregroundThread != 0 && foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, false);
                    stage = 1; return;
                }
                if (stage == 1)
                {
                    if (!box.Focused || !form.ContainsFocus || GetForegroundWindow() != form.Handle) { Console.WriteLine("FAIL Canary window did not receive foreground focus"); Environment.ExitCode = 1; timer.Stop(); form.Close(); return; }
                    NativeKeyboard.SendCopyShortcut(); stage = 2; return;
                }
                timer.Stop();
                var hasText = Clipboard.ContainsText();
                var clipboardText = hasText ? Clipboard.GetText() : "";
                var passed = hasText && clipboardText == marker;
                Console.WriteLine(passed ? "PASS Ctrl+C selected-text canary" : "FAIL Ctrl+C did not copy selected text");
                if (!passed) Console.WriteLine("DIAG hasText=" + hasText + " length=" + clipboardText.Length + " boxFocused=" + box.Focused + " containsFocus=" + form.ContainsFocus);
                Environment.ExitCode = passed ? 0 : 1;
                form.Close();
            };
            form.Shown += delegate { box.Focus(); box.SelectAll(); timer.Start(); };
            form.FormClosed += delegate { if (originalClipboard != null) try { Clipboard.SetDataObject(originalClipboard, true); } catch { } };
            Application.Run(form);
        }
    }
}

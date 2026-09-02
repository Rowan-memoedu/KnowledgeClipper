using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal sealed class SystemDesktopClock : IClock
    {
        public DateTime UtcNow { get { return DateTime.UtcNow; } }
    }

    internal sealed class SystemDesktopIdProvider : IIdProvider
    {
        public string NewCaptureId() { return "capture-" + Guid.NewGuid().ToString("N"); }
    }

    internal sealed class WindowsDesktopCaptureProvider : ICaptureProvider
    {
        private readonly string stagingRoot;
        private readonly IWindowsClipboard clipboard;
        private readonly ICopyShortcutSender copySender;
        private readonly IWindowsContextProvider contextProvider;

        internal WindowsDesktopCaptureProvider(string workDirectory) : this(workDirectory, new SystemWindowsClipboard(), new SystemCopyShortcutSender(), new WindowsUiAutomationContextProvider()) { }

        internal WindowsDesktopCaptureProvider(string workDirectory, IWindowsClipboard clipboard, ICopyShortcutSender copySender) : this(workDirectory, clipboard, copySender, new WindowsUiAutomationContextProvider()) { }

        internal WindowsDesktopCaptureProvider(string workDirectory, IWindowsClipboard clipboard, ICopyShortcutSender copySender, IWindowsContextProvider contextProvider)
        {
            stagingRoot = Path.Combine(workDirectory, "capture-staging");
            this.clipboard = clipboard; this.copySender = copySender; this.contextProvider = contextProvider;
            Directory.CreateDirectory(stagingRoot);
        }

        public CaptureResult Capture(string intent, ContextModeState contextMode)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("capture.clipboard-requires-sta");

            ClipboardSnapshot before = clipboard.Read();
            var sequenceBefore = clipboard.SequenceNumber();
            string sendError;
            var sent = copySender.TrySend(out sendError);
            ClipboardSnapshot latest = null;
            var changed = false;
            for (var attempt = 0; attempt < (sent ? 25 : 0); attempt++)
            {
                Thread.Sleep(40);
                changed = clipboard.SequenceNumber() != sequenceBefore;
                if (!changed) continue;
                latest = clipboard.Read();
                if (latest.HasContent) break;
            }

            var chosen = latest != null && latest.HasContent ? latest : before;
            if (chosen == null || !chosen.HasContent)
            {
                if (!sent) throw new InvalidOperationException("capture.copy-shortcut-failed:" + sendError);
                return new CaptureResult { hasContent = false, source = "clipboard-empty", artifacts = new List<CaptureArtifact>() };
            }

            var artifacts = new List<CaptureArtifact>();
            string selectedTextRef = null;
            ContextSegments contextSegments = null;
            string source;
            if (!string.IsNullOrWhiteSpace(chosen.Text))
            {
                var artifact = StageText(chosen.Text);
                artifacts.Add(artifact);
                if (changed && ReferenceEquals(chosen, latest)) selectedTextRef = artifact.artifactRef;
                source = selectedTextRef == null ? "clipboard-text" : "selection";
                WindowsContextSnapshot context;
                if (contextMode != null && contextMode.enabled && selectedTextRef != null && contextProvider != null && contextProvider.TryRead(chosen.Text, contextMode.granularity, out context))
                {
                    CaptureArtifact prefix = null, suffix = null;
                    if (!string.IsNullOrWhiteSpace(context.Prefix)) { prefix = StageText(context.Prefix, "context.prefix"); artifacts.Add(prefix); }
                    if (!string.IsNullOrWhiteSpace(context.Suffix)) { suffix = StageText(context.Suffix, "context.suffix"); artifacts.Add(suffix); }
                    if (prefix != null || suffix != null)
                        contextSegments = new ContextSegments { prefixArtifactRef = prefix == null ? null : prefix.artifactRef, selectionArtifactRef = selectedTextRef, suffixArtifactRef = suffix == null ? null : suffix.artifactRef, captureMethod = "uia-text-pattern" };
                }
            }
            else
            {
                artifacts.Add(StageImage(chosen.Image));
                source = "clipboard-image";
            }

            if (latest != null && !ReferenceEquals(chosen, latest)) latest.Dispose();
            if (before != null && !ReferenceEquals(chosen, before)) before.Dispose();
            chosen.Dispose();
            return new CaptureResult
            {
                hasContent = true,
                source = source,
                artifacts = artifacts,
                selectedTextArtifactRef = selectedTextRef,
                contextAvailable = contextSegments != null,
                contextSegments = contextSegments
            };
        }

        private CaptureArtifact StageText(string value, string role = "text")
        {
            var id = "capture." + role + "." + Guid.NewGuid().ToString("N");
            var relative = "inputs/capture/" + id + ".txt";
            var bytes = new UTF8Encoding(false).GetBytes(value);
            var path = StageOnce(id + ".txt", bytes);
            return Artifact(id, relative, bytes, "text/plain", "utf-8", path);
        }

        private CaptureArtifact StageImage(Image image)
        {
            if (image == null) throw new InvalidOperationException("capture.image-missing");
            var id = "capture.image." + Guid.NewGuid().ToString("N");
            var relative = "inputs/capture/" + id + ".png";
            byte[] bytes;
            using (var memory = new MemoryStream()) { image.Save(memory, ImageFormat.Png); bytes = memory.ToArray(); }
            var path = StageOnce(id + ".png", bytes);
            return Artifact(id, relative, bytes, "image/png", null, path);
        }

        private string StageOnce(string name, byte[] bytes)
        {
            var path = Path.Combine(stagingRoot, name);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            return path;
        }

        internal static CaptureArtifact Artifact(string id, string relative, byte[] bytes, string mediaType, string encoding, string sourcePath)
        {
            return new CaptureArtifact { artifactRef = id, relativePath = relative, sha256 = Sha256(bytes), bytes = bytes.LongLength, mediaType = mediaType, encoding = encoding, sourcePath = sourcePath };
        }

        internal static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                var digest = hash.ComputeHash(bytes); var result = new StringBuilder(digest.Length * 2);
                foreach (var item in digest) result.Append(item.ToString("x2"));
                return result.ToString();
            }
        }
    }

    internal interface IWindowsClipboard
    {
        uint SequenceNumber();
        ClipboardSnapshot Read();
    }

    internal interface ICopyShortcutSender
    {
        bool TrySend(out string error);
    }

    internal interface IWindowsContextProvider
    {
        bool TryRead(string selectedText, string granularity, out WindowsContextSnapshot context);
    }

    internal sealed class WindowsContextSnapshot
    {
        internal string Prefix;
        internal string Suffix;
    }

    internal sealed class WindowsUiAutomationContextProvider : IWindowsContextProvider
    {
        private const int MaximumDocumentCharacters = 200000;
        private const int MaximumSegmentCharacters = 4000;

        public bool TryRead(string selectedText, string granularity, out WindowsContextSnapshot context)
        {
            context = null;
            if (string.IsNullOrWhiteSpace(selectedText)) return false;
            try
            {
                var element = AutomationElement.FocusedElement;
                TextPattern pattern = null;
                AutomationElement topLevel = element;
                while (element != null)
                {
                    object raw;
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out raw)) { pattern = raw as TextPattern; break; }
                    var parent = TreeWalker.ControlViewWalker.GetParent(element);
                    if (parent == null || AutomationElement.RootElement.Equals(parent)) { topLevel = element; break; }
                    topLevel = parent;
                    element = parent;
                }
                if (pattern == null && topLevel != null)
                {
                    var documentCondition = new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                        new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
                    var documentElement = topLevel.FindFirst(TreeScope.Descendants, documentCondition);
                    object raw;
                    if (documentElement != null && documentElement.TryGetCurrentPattern(TextPattern.Pattern, out raw)) pattern = raw as TextPattern;
                }
                if (pattern == null && topLevel != null)
                {
                    var textElement = topLevel.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
                    object raw;
                    if (textElement != null && textElement.TryGetCurrentPattern(TextPattern.Pattern, out raw)) pattern = raw as TextPattern;
                }
                if (pattern == null) return false;
                var document = pattern.DocumentRange;
                var documentText = document.GetText(MaximumDocumentCharacters);
                if (string.IsNullOrWhiteSpace(documentText)) return false;

                var ranges = pattern.GetSelection();
                TextPatternRange selection = ranges == null ? null : ranges.FirstOrDefault(range => !string.IsNullOrWhiteSpace(range.GetText(-1)));
                string prefix, suffix;
                if (selection != null)
                {
                    var before = document.Clone();
                    before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
                    prefix = before.GetText(MaximumDocumentCharacters);
                    var after = document.Clone();
                    after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
                    suffix = after.GetText(MaximumDocumentCharacters);
                }
                else
                {
                    var index = documentText.IndexOf(selectedText, StringComparison.Ordinal);
                    if (index < 0 || documentText.IndexOf(selectedText, index + selectedText.Length, StringComparison.Ordinal) >= 0) return false;
                    prefix = documentText.Substring(0, index);
                    suffix = documentText.Substring(index + selectedText.Length);
                }
                prefix = SlicePrefix(prefix, granularity);
                suffix = SliceSuffix(suffix, granularity);
                if (string.IsNullOrWhiteSpace(prefix) && string.IsNullOrWhiteSpace(suffix)) return false;
                context = new WindowsContextSnapshot { Prefix = prefix, Suffix = suffix };
                return true;
            }
            catch (ElementNotAvailableException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (COMException) { return false; }
        }

        private static string SlicePrefix(string value, string granularity)
        {
            value = LimitTail(value ?? string.Empty);
            var delimiters = granularity == "paragraph" ? new[] { '\n', '\r' } : new[] { '。', '！', '？', '.', '!', '?', '\n', '\r' };
            var index = value.LastIndexOfAny(delimiters);
            return (index >= 0 ? value.Substring(index + 1) : value).Trim();
        }

        private static string SliceSuffix(string value, string granularity)
        {
            value = LimitHead(value ?? string.Empty);
            var delimiters = granularity == "paragraph" ? new[] { '\n', '\r' } : new[] { '。', '！', '？', '.', '!', '?', '\n', '\r' };
            var index = value.IndexOfAny(delimiters);
            return (index >= 0 ? value.Substring(0, index + 1) : value).Trim();
        }

        private static string LimitTail(string value) { return value.Length <= MaximumSegmentCharacters ? value : value.Substring(value.Length - MaximumSegmentCharacters); }
        private static string LimitHead(string value) { return value.Length <= MaximumSegmentCharacters ? value : value.Substring(0, MaximumSegmentCharacters); }
    }

    internal interface IScreenshotShortcutSender
    {
        bool TrySend(out string error);
    }

    internal interface IScreenshotCancelProbe
    {
        bool IsCancellationRequested();
    }

    internal sealed class SystemWindowsClipboard : IWindowsClipboard
    {
        public uint SequenceNumber() { return NativeClipboard.SequenceNumber(); }
        public ClipboardSnapshot Read() { return ClipboardSnapshot.ReadWithRetry(); }
    }

    internal sealed class SystemCopyShortcutSender : ICopyShortcutSender
    {
        public bool TrySend(out string error) { return NativeKeyboard.TrySendCopyShortcut(out error); }
    }

    internal sealed class SystemScreenshotShortcutSender : IScreenshotShortcutSender
    {
        public bool TrySend(out string error) { return NativeKeyboard.TrySendScreenshotShortcut(out error); }
    }

    internal sealed class SystemScreenshotCancelProbe : IScreenshotCancelProbe
    {
        public bool IsCancellationRequested() { return NativeKeyboard.WasEscapePressed(); }
    }

    internal sealed class WindowsScreenshotProvider : IScreenshotProvider
    {
        private readonly string stagingRoot;
        private readonly IWindowsClipboard clipboard;
        private readonly IScreenshotShortcutSender shortcut;
        private readonly IScreenshotCancelProbe cancellation;
        private readonly int timeoutMilliseconds;
        private readonly int pollMilliseconds;

        internal WindowsScreenshotProvider(string workDirectory) : this(workDirectory, new SystemWindowsClipboard(), new SystemScreenshotShortcutSender(), new SystemScreenshotCancelProbe(), 90000, 25) { }

        internal WindowsScreenshotProvider(string workDirectory, IWindowsClipboard clipboard, IScreenshotShortcutSender shortcut, IScreenshotCancelProbe cancellation, int timeoutMilliseconds, int pollMilliseconds)
        {
            stagingRoot = Path.Combine(workDirectory, "capture-staging");
            this.clipboard = clipboard ?? throw new ArgumentNullException("clipboard");
            this.shortcut = shortcut ?? throw new ArgumentNullException("shortcut");
            this.cancellation = cancellation ?? throw new ArgumentNullException("cancellation");
            this.timeoutMilliseconds = Math.Max(1, timeoutMilliseconds);
            this.pollMilliseconds = Math.Max(1, pollMilliseconds);
            Directory.CreateDirectory(stagingRoot);
        }

        public ScreenshotResult Capture(string group)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("screenshot.clipboard-requires-sta");
            var sequenceBefore = clipboard.SequenceNumber();
            cancellation.IsCancellationRequested();
            string error;
            if (!shortcut.TrySend(out error)) throw new InvalidOperationException("screenshot.system-shortcut-failed:" + error);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (cancellation.IsCancellationRequested())
                    return new ScreenshotResult { outcome = "cancelled", noticeCode = "screenshot.frame-cancelled" };
                if (clipboard.SequenceNumber() != sequenceBefore)
                {
                    using (var snapshot = clipboard.Read())
                    {
                        if (snapshot != null && snapshot.Image != null) return Stage(snapshot.Image);
                    }
                }
                Thread.Sleep(pollMilliseconds);
            }
            return new ScreenshotResult { outcome = "cancelled", noticeCode = "screenshot.system-capture-timeout" };
        }

        private ScreenshotResult Stage(Image image)
        {
            byte[] bytes;
            using (var memory = new MemoryStream()) { image.Save(memory, ImageFormat.Png); bytes = memory.ToArray(); }
            var id = "capture.screenshot." + Guid.NewGuid().ToString("N");
            var relative = "inputs/capture/" + id + ".png";
            var path = Path.Combine(stagingRoot, id + ".png");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            return new ScreenshotResult { outcome = "captured", artifact = WindowsDesktopCaptureProvider.Artifact(id, relative, bytes, "image/png", null, path), noticeCode = "screenshot.frame-captured" };
        }
    }

    internal sealed class ClipboardSnapshot : IDisposable
    {
        internal string Text;
        internal Image Image;
        internal bool HasContent { get { return !string.IsNullOrWhiteSpace(Text) || Image != null; } }

        internal static ClipboardSnapshot ReadWithRetry()
        {
            Exception last = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    var value = new ClipboardSnapshot();
                    if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    {
                        var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (!string.IsNullOrWhiteSpace(text)) value.Text = text;
                    }
                    if (string.IsNullOrWhiteSpace(value.Text) && Clipboard.ContainsImage()) value.Image = (Image)Clipboard.GetImage().Clone();
                    return value;
                }
                catch (ExternalException ex) { last = ex; Thread.Sleep(25); }
            }
            throw new InvalidOperationException("capture.clipboard-read-failed", last);
        }

        public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
    }

    internal static class GlobalHotkeyCanary
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
        internal static bool RegisterAndRelease(IntPtr window)
        {
            const int id = 0x5243; const uint controlAltShiftNoRepeat = 0x0002 | 0x0001 | 0x0004 | 0x4000; const uint f24 = 0x87;
            if (!RegisterHotKey(window, id, controlAltShiftNoRepeat, f24)) return false;
            return UnregisterHotKey(window, id);
        }
    }
}

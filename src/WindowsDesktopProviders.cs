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
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using KnowledgeClipper.Desktop.Application.V3;
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

    // The v3 read-to-Anki use case captures text once without staging a second
    // legacy capture artifact. Clipboard access stays on a dedicated STA thread
    // so the borderless UI remains responsive while the external selection keeps focus.
    internal sealed class WindowsCurrentTextCapturePort : ICurrentTextCapturePort
    {
        private readonly IWindowsClipboard clipboard;
        private readonly ICopyShortcutSender copySender;
        private readonly int attempts;
        private readonly int pollMilliseconds;
        private readonly IWindowsContextProvider contextProvider;
        private readonly Func<ContextModeState> readContextMode;
        private readonly Action<string> tracePhase;

        internal WindowsCurrentTextCapturePort()
            : this(new SystemWindowsClipboard(), new SystemCopyShortcutSender(), 25, 40, new WindowsUiAutomationContextProvider(), null) { }

        internal WindowsCurrentTextCapturePort(IWindowsClipboard clipboard, ICopyShortcutSender copySender, int attempts = 25, int pollMilliseconds = 40, IWindowsContextProvider contextProvider = null, Func<ContextModeState> readContextMode = null, Action<string> tracePhase = null)
        {
            this.clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
            this.copySender = copySender ?? throw new ArgumentNullException(nameof(copySender));
            this.attempts = Math.Max(0, attempts);
            this.pollMilliseconds = Math.Max(1, pollMilliseconds);
            this.contextProvider = contextProvider;
            this.readContextMode = readContextMode;
            this.tracePhase = tracePhase;
        }

        public Task<CapturedPlainText> CaptureOnceAsync(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<CapturedPlainText>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => Capture(completion, cancellationToken)) { IsBackground = true, Name = "KnowledgeClipper.TextCapture" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        private void Capture(TaskCompletionSource<CapturedPlainText> completion, CancellationToken cancellationToken)
        {
            ClipboardSnapshot before = null;
            ClipboardSnapshot latest = null;
            try
            {
                Trace("entered");
                cancellationToken.ThrowIfCancellationRequested();
                Trace("clipboard-before");
                before = clipboard.Read();
                var sequenceBefore = clipboard.SequenceNumber();
                string sendError;
                var sent = copySender.TrySend(out sendError);
                Trace(sent ? "copy-sent" : "copy-send-failed");
                for (var attempt = 0; sent && attempt < attempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(pollMilliseconds);
                    if (clipboard.SequenceNumber() == sequenceBefore) continue;
                    latest?.Dispose();
                    latest = clipboard.Read();
                    if (!string.IsNullOrWhiteSpace(latest?.Text)) break;
                }

                var text = !string.IsNullOrWhiteSpace(latest?.Text) ? latest.Text : before?.Text;
                Trace(!string.IsNullOrWhiteSpace(latest?.Text) ? "latest-text-selected" : "original-clipboard-fallback");
                if (string.IsNullOrWhiteSpace(text))
                {
                    var notice = sent ? "read-to-anki-text-required" : "read-to-anki-copy-shortcut-failed:" + sendError;
                    throw new InvalidOperationException(notice);
                }
                var mode = readContextMode?.Invoke();
                var contextRequested = mode?.enabled == true;
                WindowsContextSnapshot context = null;
                Trace(contextRequested ? "context-read-before" : "context-not-requested");
                if (contextRequested && (contextProvider == null || !contextProvider.TryRead(text, mode.granularity, out context)))
                    throw new InvalidOperationException("context.unavailable");
                Trace("context-read-after");
                var segments = new List<CapturedTextSegment>();
                if (!string.IsNullOrWhiteSpace(context?.Prefix)) segments.Add(new CapturedTextSegment(context.Prefix, 0, "prefix"));
                if (!string.IsNullOrWhiteSpace(context?.Suffix)) segments.Add(new CapturedTextSegment(context.Suffix, 1, "suffix"));
                var result = new CapturedPlainText(text)
                {
                    TargetText = text,
                    OrderedContextSegments = segments,
                    ContextRequested = contextRequested,
                    ContextGranularity = mode?.granularity,
                    SourceWindow = ForegroundWindowTitle()
                };
                Trace("completed");
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { Trace("cancelled"); completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { Trace("failed-" + exception.GetType().Name); completion.TrySetException(exception); }
            finally
            {
                latest?.Dispose();
                before?.Dispose();
            }
        }

        private void Trace(string phase) { try { tracePhase?.Invoke(phase); } catch { } }

        private static string ForegroundWindowTitle()
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return "unknown";
            var length = GetWindowTextLength(handle);
            if (length <= 0) return "unknown";
            var value = new StringBuilder(Math.Min(length + 1, 1024));
            return GetWindowText(handle, value, value.Capacity) > 0 ? value.ToString() : "unknown";
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    }

    // Explicit candidate CLI only. No ambient environment override and no Core
    // command injection: this replaces only the existing text capture input port.
    internal sealed class CandidateCaptureFixture : ICurrentTextCapturePort
    {
        private sealed record Input(string Selected, string Prefix, string Suffix);
        private readonly Input[] inputs;
        private readonly string fixturePath, fixtureSha256;
        private readonly bool isQueue;
        private readonly object gate = new object();
        private readonly Func<ContextModeState> readMode;
        private readonly Action<string> trace;
        private int consumed;
        private bool drifted;

        internal CandidateCaptureFixture(string path, Func<ContextModeState> readMode, Action<string> trace = null)
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length > 64000) throw new InvalidDataException("candidate-capture-fixture-path-or-size-invalid");
            var bytes = File.ReadAllBytes(path);
            using var doc = System.Text.Json.JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)) throw new InvalidDataException("candidate-capture-fixture-version-invalid");
            isQueue = version.GetString() == "desktop-test-capture-queue.v1";
            if (isQueue)
            {
                RequireFields(root, "inputs", "schemaVersion");
                var values = root.GetProperty("inputs");
                if (values.ValueKind != System.Text.Json.JsonValueKind.Array || values.GetArrayLength() < 1 || values.GetArrayLength() > 16) throw new InvalidDataException("candidate-capture-fixture-queue-size-invalid");
                inputs = values.EnumerateArray().Select(value => { RequireFields(value, "prefix", "selectedText", "suffix"); return ReadInput(value); }).ToArray();
            }
            else
            {
                RequireFields(root, "prefix", "schemaVersion", "selectedText", "suffix");
                if (version.GetString() != "desktop-test-capture.v1") throw new InvalidDataException("candidate-capture-fixture-version-invalid");
                inputs = new[] { ReadInput(root) };
            }
            fixturePath = path; fixtureSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            this.readMode = readMode ?? throw new ArgumentNullException(nameof(readMode)); this.trace = trace;
            trace?.Invoke("fixture-loaded-sha256-" + fixtureSha256);
        }

        private static void RequireFields(System.Text.Json.JsonElement value, params string[] expected)
        {
            if (value.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("candidate-capture-fixture-fields-invalid");
            var names = value.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != expected.Length || !names.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(expected)) throw new InvalidDataException("candidate-capture-fixture-fields-invalid");
        }

        private static Input ReadInput(System.Text.Json.JsonElement value)
        {
            foreach (var name in new[] { "selectedText", "prefix", "suffix" }) if (value.GetProperty(name).ValueKind != System.Text.Json.JsonValueKind.String) throw new InvalidDataException("candidate-capture-fixture-text-invalid");
            var selected = value.GetProperty("selectedText").GetString(); var prefix = value.GetProperty("prefix").GetString(); var suffix = value.GetProperty("suffix").GetString();
            if (string.IsNullOrWhiteSpace(selected) || selected.Length > 5000 || prefix.Length > 10000 || suffix.Length > 10000) throw new InvalidDataException("candidate-capture-fixture-text-invalid");
            return new Input(selected, prefix, suffix);
        }

        public Task<CapturedPlainText> CaptureOnceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Input input;
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (drifted) throw new InvalidOperationException("candidate-capture-fixture-drift");
                try
                {
                    if (!File.Exists(fixturePath) || new FileInfo(fixturePath).Length > 64000 || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))) != fixtureSha256)
                    { drifted = true; throw new InvalidOperationException("candidate-capture-fixture-drift"); }
                }
                catch (IOException) { drifted = true; throw new InvalidOperationException("candidate-capture-fixture-drift"); }
                catch (UnauthorizedAccessException) { drifted = true; throw new InvalidOperationException("candidate-capture-fixture-drift"); }
                if (consumed >= inputs.Length) throw new InvalidOperationException(isQueue ? "candidate-capture-fixture-queue-exhausted" : "candidate-capture-fixture-already-consumed");
                input = inputs[consumed++];
                trace?.Invoke("fixture-input-consumed-" + consumed);
            }
            var selected = input.Selected; var prefix = input.Prefix; var suffix = input.Suffix;
            var mode = readMode();
            var segments = new List<CapturedTextSegment>();
            if (mode?.enabled == true)
            {
                if (!string.IsNullOrWhiteSpace(prefix)) segments.Add(new CapturedTextSegment(prefix, segments.Count, "prefix"));
                if (!string.IsNullOrWhiteSpace(suffix)) segments.Add(new CapturedTextSegment(suffix, segments.Count, "suffix"));
                if (segments.Count == 0) throw new InvalidOperationException("context.unavailable");
            }
            trace?.Invoke("fixture-capture-completed");
            return Task.FromResult(new CapturedPlainText(selected) { TargetText = selected, OrderedContextSegments = segments, ContextRequested = mode?.enabled == true, ContextGranularity = mode?.granularity ?? "sentence", SourceWindow = "deterministic-capture-fixture" });
        }
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
                var selectedRanges = ranges == null ? new TextPatternRange[0] : ranges.Where(range => !string.IsNullOrWhiteSpace(range.GetText(-1))).ToArray();
                // Never bind stale clipboard text to a different UIA selection (or
                // silently choose one range from a multiple selection).
                if (selectedRanges.Length > 1) return false;
                TextPatternRange selection = selectedRanges.FirstOrDefault();
                if (selection != null && !string.Equals(selection.GetText(-1), selectedText, StringComparison.Ordinal)) return false;
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
            // These are source segments, not display strings. Boundary whitespace
            // is required to reconstruct prefix + selected + suffix losslessly.
            return index >= 0 ? value.Substring(index + 1) : value;
        }

        private static string SliceSuffix(string value, string granularity)
        {
            value = LimitHead(value ?? string.Empty);
            var delimiters = granularity == "paragraph" ? new[] { '\n', '\r' } : new[] { '。', '！', '？', '.', '!', '?', '\n', '\r' };
            var index = value.IndexOfAny(delimiters);
            // Retain sentence punctuation but exclude the next paragraph boundary.
            // Horizontal whitespace next to the selected text remains untouched.
            return index >= 0 ? value.Substring(0, index + (value[index] == '\r' || value[index] == '\n' ? 0 : 1)) : value;
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

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System.Threading;
using System.Runtime.InteropServices;
using KnowledgeClipper.Workflow;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper;

internal sealed partial class BoundClipperForm
{
    private LanguageDesktopFlow languageFlow;
    private ExcerptDesktopFlow excerptFlow;
    private ImageDesktopFlow imageFlow;
    private RecordHistoryFlow historyFlow;
    private MeaningPopup quickFeedbackPopup;
    private LanguageSelectionMonitor selectionMonitor;
    private LanguageSelectionDot selectionDot;
    private ClipboardImageMonitor clipboardImageMonitor;
    private LanguageSelectionDot clipboardImageDot;
    private ToolStripMenuItem textDotItem, imageDotItem;
    private bool languageWindowShown;
    private LanguageSettingsForm languageSettingsForm;
    private ExcerptDestinationForm excerptDestinationForm;
    private const int ExcerptDestinationHotkeyId = 0x4B43;
    private const int ReplayLastHotkeyId = 0x4B44;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    private bool excerptDestinationHotkeyRegistered;
    private IntPtr excerptDestinationHotkeyHandle;
    private bool replayLastHotkeyRegistered;
    private IntPtr replayLastHotkeyHandle;
    private AnkiPendingQueue ankiPending;
    private readonly HttpClient pendingAnkiHttp = new();
    private readonly CancellationTokenSource pendingAnkiLifetime = new();
    private readonly System.Windows.Forms.Timer pendingAnkiTimer = new() { Interval = 5000 };
    internal void EnableLanguageWorkflow()
    {
        languageFlow = new LanguageDesktopFlow(this, config.WorkDirectory,
            message => SetStatus(message, Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)), settings);
        excerptFlow = new ExcerptDesktopFlow(this, config.WorkDirectory, languageFlow.Settings,
            message => SetStatus(message, Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)));
        languageFlow.QuickFeedback += ShowQuickFeedback;
        excerptFlow.QuickFeedback += ShowQuickFeedback;
        imageFlow = new ImageDesktopFlow(this, config, languageFlow, excerptFlow);
        historyFlow = new RecordHistoryFlow(this, config.WorkDirectory, languageFlow, excerptFlow);
        ankiPending = new AnkiPendingQueue(config.WorkDirectory, pendingAnkiHttp);
        ankiPending.Changed += (draft, store) =>
        {
            if (IsDisposed) return;
            if (store.DirectoryPath.EndsWith("excerpt-cards", StringComparison.OrdinalIgnoreCase)) excerptFlow.PendingImportChanged(draft);
            else languageFlow.PendingImportChanged(draft);
            SetStatus(draft.AwaitingAnki ? "内容已保存，等待 Anki 打开。" : draft.ImportState == ImportState.Verified ? "待导入内容已自动导入 Anki 并核验。" : draft.ImportError,
                Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255));
        };
        pendingAnkiTimer.Tick += async (_, _) =>
        {
            try { await ankiPending.PumpAsync(pendingAnkiLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) SetStatus("待导入检查未完成：" + ex.Message, Color.DarkRed, Color.MistyRose); }
        };
        pendingAnkiTimer.Start();
        FormClosed += (_, _) => { pendingAnkiTimer.Stop(); pendingAnkiTimer.Dispose(); pendingAnkiLifetime.Cancel(); ankiPending.Dispose(); pendingAnkiHttp.Dispose(); };
        ((RecordHistoryButton)statusButton).SeparateHistoryClick = true;
        controller.ScreenshotSubmission = imageFlow.Submit;
        selectionDot = new LanguageSelectionDot((selection, action) => {
            var current = selectionMonitor.TryConsume(selection);
            selectionDot.HideSelection();
            languageFlow.HideMeaning();
            if (current) _ = action switch {
                SelectionDotAction.Question => languageFlow.StartQuestionAsync(selection, languageFlow.Settings.QuickQuestion),
                SelectionDotAction.Excerpt => excerptFlow.StartAsync(selection, languageFlow.Settings.QuickExcerpt),
                _ => languageFlow.StartLanguageAsync(selection, languageFlow.Settings.QuickLanguage)
            };
            else SetStatus("选区已变化，请重新选择文字", Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255));
        });
        selectionMonitor = new LanguageSelectionMonitor(this, Path.Combine(AppContext.BaseDirectory, "helpers", "selection.cjs"),
            point => selectionDot.ContainsScreenPoint(point),
            selection => selectionDot.ShowSelection(selection), () => selectionDot.HideSelection(),
            message => SetStatus("自动选词未完成：" + message, Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)));
        clipboardImageDot = LanguageSelectionDot.ForImages((snapshot, action) => _ = HandleClipboardImageAsync(snapshot, action));
        clipboardImageMonitor = new ClipboardImageMonitor(this,
            snapshot => { selectionDot.HideSelection(); clipboardImageDot.ShowImage(snapshot); },
            () => clipboardImageDot.HideImage(),
            message => SetStatus("图片浮点未显示：" + message, Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)),
            isDotPoint: point => clipboardImageDot.ContainsScreenPoint(point));
        textDotItem = new ToolStripMenuItem("文字浮点") { CheckOnClick = true, Name = "knowledge-clipper.desktop.menu.dot.text" };
        imageDotItem = new ToolStripMenuItem("图片浮点") { CheckOnClick = true, Name = "knowledge-clipper.desktop.menu.dot.image" };
        textDotItem.Click += (_, _) => languageFlow.SaveSettings(languageFlow.Settings with { GlobalSelection = textDotItem.Checked });
        imageDotItem.Click += (_, _) => languageFlow.SaveSettings(languageFlow.Settings with { ClipboardImageDotEnabled = imageDotItem.Checked });
        var destinationItem = new ToolStripMenuItem("原文摘录目标…") { Name = "knowledge-clipper.desktop.menu.excerpt-destination" };
        destinationItem.Click += (_, _) => ShowExcerptDestination();
        var index = ContextMenuStrip.Items.IndexOf(originalItem) + 1;
        ContextMenuStrip.Items.Insert(index, textDotItem); ContextMenuStrip.Items.Insert(index + 1, imageDotItem);
        ContextMenuStrip.Items.Insert(index + 2, destinationItem);
        HandleCreated += (_, _) => BindExcerptDestinationHotkey();
        HandleDestroyed += (_, _) => UnbindExcerptDestinationHotkey();
        languageFlow.SettingsChanged += ApplyLanguageSettings;
        Shown += (_, _) => { languageWindowShown = true; ApplyGlobalSelectionSetting(); };
        ApplyLanguageSettings();
        FormClosed += (_, _) => { UnbindExcerptDestinationHotkey(); if (excerptDestinationForm != null && !excerptDestinationForm.IsDisposed) excerptDestinationForm.Close(); clipboardImageMonitor.Dispose(); clipboardImageDot.Dispose(); selectionMonitor.Dispose(); selectionDot.Dispose(); historyFlow.Dispose(); imageFlow.Dispose(); languageFlow.Dispose(); excerptFlow.Dispose(); quickFeedbackPopup?.Dispose(); };
        SetStatus("选中文字后点语言或摘录", Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255));
    }

    private void ShowQuickFeedback(string message)
    {
        if (IsDisposed) return;
        quickFeedbackPopup ??= new MeaningPopup { Name = "knowledge-clipper.quick.feedback-popup", Text = "快捷处理结果" };
        // Same presentation and dismissal as meaning, without a replay handler.
        quickFeedbackPopup.ShowMeaning(message.TakeText(140), Cursor.Position);
    }

    private void ApplyLanguageSettings()
    {
        settings.ContextModeEnabled = languageFlow.Settings.ContextEnabled;
        settings.ContextGranularity = languageFlow.Settings.ContextGranularity;
        excerptFlow.UpdateSettings(languageFlow.Settings);
        BindExcerptDestinationHotkey();
        var original = languageFlow.Settings.OriginalImageMode == true;
        if (settings.OriginalImageMode != original)
        {
            SaveAuthoritativeCaptureModes(settings.ContextModeEnabled, settings.ContextGranularity, original);
            controller.Execute(Action(AutomationCatalog.OriginalImageSet, new SemanticActionArguments { enabled = original }));
        }
        inlineQuestionInput.PlaceholderText = KnowledgeClipper.Workflow.MaterialRouting.TypedPlaceholder(languageFlow.Settings);
        inlineQuestionInput.AccessibleDescription = inlineQuestionInput.PlaceholderText;
        textDotItem.Checked = languageFlow.Settings.GlobalSelection;
        imageDotItem.Checked = languageFlow.Settings.ClipboardImageDotEnabled;
        var current = languageFlow.Settings;
        selectionDot.ApplyQuickModes(current.QuickLanguage, current.QuickQuestion, current.QuickExcerpt, current.QuickScreenshot);
        clipboardImageDot.ApplyQuickModes(current.QuickLanguage, current.QuickQuestion, current.QuickExcerpt, current.QuickScreenshot);
        ApplyModeVisuals();
        if (languageWindowShown) ApplyGlobalSelectionSetting();
    }

    private void BindExcerptDestinationHotkey()
    {
        if (languageFlow == null || !IsHandleCreated || IsDisposed) return;
        UnbindExcerptDestinationHotkey();
        var gesture = ExcerptHotkey.Parse(languageFlow.Settings.OriginalExcerptHotkey);
        var replayGesture = ExcerptHotkey.Parse(languageFlow.Settings.ReplayLastHotkey);
        excerptDestinationHotkeyRegistered = gesture.VirtualKey != 0 && RegisterHotKey(Handle, ExcerptDestinationHotkeyId, gesture.Modifiers | 0x4000, gesture.VirtualKey);
        if (excerptDestinationHotkeyRegistered) excerptDestinationHotkeyHandle = Handle;
        replayLastHotkeyRegistered = replayGesture.VirtualKey != 0 && RegisterHotKey(Handle, ReplayLastHotkeyId, replayGesture.Modifiers | 0x4000, replayGesture.VirtualKey);
        if (replayLastHotkeyRegistered) replayLastHotkeyHandle = Handle;
        var failed = new List<string>();
        if (gesture.VirtualKey != 0 && !excerptDestinationHotkeyRegistered) failed.Add("RemNote 导入目标");
        if (replayGesture.VirtualKey != 0 && !replayLastHotkeyRegistered) failed.Add("重读上一条");
        if (failed.Count != 0)
            SetStatus(string.Join("、", failed) + "快捷键已被占用，请在设置 → 快捷键修改；托盘入口仍可用。", Color.DarkRed, Color.MistyRose);
    }

    private void UnbindExcerptDestinationHotkey()
    {
        if (excerptDestinationHotkeyRegistered) UnregisterHotKey(excerptDestinationHotkeyHandle, ExcerptDestinationHotkeyId);
        excerptDestinationHotkeyRegistered = false;
        excerptDestinationHotkeyHandle = IntPtr.Zero;
        if (replayLastHotkeyRegistered) UnregisterHotKey(replayLastHotkeyHandle, ReplayLastHotkeyId);
        replayLastHotkeyRegistered = false;
        replayLastHotkeyHandle = IntPtr.Zero;
    }

    private void ShowExcerptDestination()
    {
        if (languageFlow == null) return;
        if (excerptDestinationForm != null && !excerptDestinationForm.IsDisposed)
        { excerptDestinationForm.Show(); excerptDestinationForm.Activate(); return; }
        var form = new ExcerptDestinationForm(languageFlow);
        excerptDestinationForm = form;
        form.FormClosed += (_, _) => { if (ReferenceEquals(excerptDestinationForm, form)) excerptDestinationForm = null; form.Dispose(); };
        form.Show(); form.Activate();
    }

    private void ApplyGlobalSelectionSetting()
    {
        if (languageFlow.Settings.GlobalSelection) selectionMonitor.Start(languageFlow.Settings);
        else { selectionMonitor.Stop(); selectionDot.HideSelection(); }
        if (languageFlow.Settings.ClipboardImageDotEnabled) clipboardImageMonitor.Start();
        else { clipboardImageMonitor.Stop(); clipboardImageDot.HideImage(); }
    }

    private async Task HandleClipboardImageAsync(ClipboardImageSnapshot snapshot, ClipboardImageDotAction action)
    {
        if (!clipboardImageMonitor.TryConsume(snapshot)) return;
        clipboardImageDot.HideImage();
        try
        {
            var png = snapshot.PngBytes.ToArray();
            if (action == ClipboardImageDotAction.Excerpt) imageFlow.StartClipboardImage(png, "excerpt", languageFlow.Settings.QuickExcerpt);
            else if (action == ClipboardImageDotAction.Read) imageFlow.StartClipboardImage(png, "language", languageFlow.Settings.QuickLanguage);
            else if (action == ClipboardImageDotAction.Question) imageFlow.StartClipboardImage(png, "question", languageFlow.Settings.QuickQuestion);
            else
            {
                if (busy) { SetStatus("正在截图，请完成当前采集后继续", Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)); return; }
                var state = controller.StartScreenshotFromImage(imageFlow.StageClipboardImage(png));
                if (state.screenshotSession.active)
                {
                    EnsureScreenshotSession(); screenshotSession.UpdateState(state); screenshotSession.Show();
                }
                await PresentStateAsync("screenshot.clipboard-start", state);
            }
        }
        catch (Exception ex)
        {
            await PresentErrorAsync(ex.Message);
            if (action == ClipboardImageDotAction.Excerpt && languageFlow.Settings.QuickExcerpt ||
                action == ClipboardImageDotAction.Read && languageFlow.Settings.QuickLanguage ||
                action == ClipboardImageDotAction.Question && languageFlow.Settings.QuickQuestion)
                ShowQuickFeedback("图片处理失败：" + ex.Message);
        }
    }

    private void OpenLanguageSettings()
    {
        if (languageSettingsForm != null && !languageSettingsForm.IsDisposed)
        {
            languageSettingsForm.Show(); languageSettingsForm.BringToFront(); languageSettingsForm.Activate(); return;
        }
        var dialog = new LanguageSettingsForm(languageFlow, config.WorkDirectory) { OpenExcerptDestination = ShowExcerptDestination };
        languageSettingsForm = dialog;
        dialog.FormClosed += (_, _) => { if (ReferenceEquals(languageSettingsForm, dialog)) languageSettingsForm = null; dialog.Dispose(); };
        dialog.Show(this);
    }
}

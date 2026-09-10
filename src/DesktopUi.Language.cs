using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace KnowledgeClipper;

internal sealed partial class BoundClipperForm
{
    private LanguageDesktopFlow languageFlow;
    private LanguageSelectionMonitor selectionMonitor;
    private LanguageSelectionDot selectionDot;
    private bool languageWindowShown;
    internal void EnableLanguageWorkflow()
    {
        languageFlow = new LanguageDesktopFlow(this, config.WorkDirectory,
            message => SetStatus(message, Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)), settings);
        selectionDot = new LanguageSelectionDot(selection => {
            var current = selectionMonitor.TryConsume(selection);
            selectionDot.HideSelection();
            if (current) _ = languageFlow.StartAsync(true, selection);
            else SetStatus("选区已变化，请重新选择文字", Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255));
        });
        selectionMonitor = new LanguageSelectionMonitor(this, Path.Combine(AppContext.BaseDirectory, "helpers", "selection.cjs"),
            point => selectionDot.ContainsScreenPoint(point),
            selection => selectionDot.ShowSelection(selection), () => selectionDot.HideSelection(),
            message => SetStatus("自动选词未完成：" + message, Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)));
        languageFlow.SettingsChanged += ApplyLanguageSettings;
        Shown += (_, _) => { languageWindowShown = true; ApplyGlobalSelectionSetting(); };
        ApplyLanguageSettings();
        FormClosed += (_, _) => { selectionMonitor.Dispose(); selectionDot.Dispose(); languageFlow.Dispose(); };
        SetStatus("选中文字后点朗读或摘录", Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255));
    }

    private void ApplyLanguageSettings()
    {
        settings.ContextModeEnabled = languageFlow.Settings.ContextEnabled;
        settings.ContextGranularity = languageFlow.Settings.ContextGranularity;
        ApplyModeVisuals();
        if (languageWindowShown) ApplyGlobalSelectionSetting();
    }

    private void ApplyGlobalSelectionSetting()
    {
        if (languageFlow.Settings.GlobalSelection) selectionMonitor.Start(languageFlow.Settings);
        else { selectionMonitor.Stop(); selectionDot.HideSelection(); }
    }

    private void OpenLanguageSettings()
    {
        using var dialog = new LanguageSettingsForm(languageFlow, config.WorkDirectory);
        dialog.ShowDialog(this);
    }
}

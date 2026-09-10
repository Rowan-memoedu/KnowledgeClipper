using System;
using System.Drawing;
using System.Windows.Forms;

namespace KnowledgeClipper;

internal sealed partial class BoundClipperForm
{
    private LanguageDesktopFlow languageFlow;
    internal void EnableLanguageWorkflow()
    {
        languageFlow = new LanguageDesktopFlow(this, config.WorkDirectory,
            message => SetStatus(message, Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255)), settings);
        languageFlow.SettingsChanged += ApplyLanguageSettings;
        ApplyLanguageSettings();
        FormClosed += (_, _) => languageFlow.Dispose();
        SetStatus("选中文字后点朗读或摘录", Color.FromArgb(38, 90, 150), Color.FromArgb(238, 245, 255));
    }

    private void ApplyLanguageSettings()
    {
        settings.ContextModeEnabled = languageFlow.Settings.ContextEnabled;
        settings.ContextGranularity = languageFlow.Settings.ContextGranularity;
        ApplyModeVisuals();
    }

    private void OpenLanguageSettings()
    {
        using var dialog = new LanguageSettingsForm(languageFlow, config.WorkDirectory);
        dialog.ShowDialog(this);
    }
}

using System;

namespace KnowledgeClipper.Desktop.Core
{
    internal static class AutomationCatalogV13
    {
        internal const string ContractVersion = "1.3.0";
        internal const string ActionSchemaVersion = "desktop-automation.v1.3";
        internal const string OcrDecisionKind = "ocr-human-resolution";
        internal const string OcrAcceptOriginal = "ocr-human-resolution.accept-original";
        internal const string OcrSubmitCorrection = "ocr-human-resolution.submit-correction";
        internal const string OcrCancel = "ocr-human-resolution.cancel";

        internal static bool IsKnownAction(string action)
        {
            return string.Equals(action, OcrAcceptOriginal, StringComparison.Ordinal) ||
                   string.Equals(action, OcrSubmitCorrection, StringComparison.Ordinal) ||
                   string.Equals(action, OcrCancel, StringComparison.Ordinal);
        }
    }
}

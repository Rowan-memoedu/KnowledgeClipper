using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    internal sealed class CandidateReadToAnkiCanaryInput
    {
        public string Text { get; set; }
        public string LanguageChoice { get; set; }
        public string AnkiConfigurationJson { get; set; }
        public string JobsRoot { get; set; }
        public int TimeoutSeconds { get; set; } = 300;
    }

    internal static class CandidateReadToAnkiProcessCanary
    {
        internal static int Run(string baseDirectory, string inputPath, string outputPath, TextWriter output)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                var input = JsonSerializer.Deserialize<CandidateReadToAnkiCanaryInput>(File.ReadAllText(inputPath)) ?? throw new InvalidDataException("candidate-canary.input-invalid");
                if (string.IsNullOrWhiteSpace(input.Text) || string.IsNullOrWhiteSpace(input.AnkiConfigurationJson) || string.IsNullOrWhiteSpace(input.JobsRoot)) throw new InvalidDataException("candidate-canary.input-incomplete");
                var choice = input.LanguageChoice switch { "meaning-reading" => "meaning-reading", "writing" => "writing", "both" => "both", _ => throw new InvalidDataException("candidate-canary.language-choice-invalid") };
                var expectedCardCount = choice == "both" ? 2 : 1;
                var config = AppConfig.Load(Path.Combine(baseDirectory, "appsettings.json"));
                var credentials = DesktopCredentialSettings.Load(config.WorkDirectory);
                using var host = CandidateOrchestratorHost.StartIfConfigured(baseDirectory, Path.GetFullPath(input.JobsRoot), credentials);
                var gateway = new NamedPipeCoreGateway(host.Configuration.PipeName, () => input.AnkiConfigurationJson, () => new SdkLanguageCardSettings("candidate-process-canary.v1", choice));
                var support = new ReadToAnkiOnlyApplicationSupportPorts();
                var application = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, support, support, support, null,
                    new DeterministicTextCapture(input.Text), gateway, gateway, null, gateway);
                var standard = LegacyDesktopAdapter.CreateBoundClipperDependencies(config);
                var dependencies = new BoundClipperDependencies(config, standard.Controller, standard.InitialSettings, standard.SettingsFormFactory, standard.QuestionAnswers, standard.OcrHumanResolution, false, application);
                using var form = new BoundClipperForm(dependencies);
                form.Show(); Application.DoEvents();
                if (!form.InvokeCandidateCanaryAction(AutomationCatalogV11.AutomationIds[AutomationCatalog.ReadCapture])) throw new InvalidOperationException("candidate-canary.read-action-not-found");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, input.TimeoutSeconds));
                WaitForPreview(form, deadline);
                var preview = form.CandidateCanaryFlashcardState;
                var before = SnapshotAnkiEvidence(input.JobsRoot);
                if (!form.InvokeCandidateCanaryAction(FlashcardPreviewAutomation.Confirm)) throw new InvalidOperationException("candidate-canary.confirm-action-not-found");
                WaitUntil(form, deadline, state => state?.Status is FlashcardDraftStatus.Completed or FlashcardDraftStatus.Failed or FlashcardDraftStatus.Unknown, "candidate-canary.confirm-timeout");
                var terminal = form.CandidateCanaryFlashcardState;
                var after = SnapshotAnkiEvidence(input.JobsRoot);
                form.InvokeCandidateCanaryAction(FlashcardPreviewAutomation.Confirm);
                for (var pump = 0; pump < 10; pump++) { Application.DoEvents(); Thread.Sleep(50); }
                var afterDuplicateConfirm = SnapshotAnkiEvidence(input.JobsRoot);
                var readback = ReadExactReadback(input.JobsRoot, preview.Draft);
                var exactReadback = readback != null && readback.Exact;
                var duplicateConfirmCreatedNoWrite = afterDuplicateConfirm.Attempts == after.Attempts && afterDuplicateConfirm.ReadbackFiles == after.ReadbackFiles;
                var receipt = new
                {
                    schema = "kc.desktop-candidate-read-to-anki-process-canary/1", startedAt = started, completedAt = DateTimeOffset.UtcNow,
                    status = terminal.Status == FlashcardDraftStatus.Completed && preview.Draft.Cards.Count == expectedCardCount && before.Attempts == 0 && before.ReadbackFiles == 0 && after.Attempts == 1 && after.ReadbackFiles == 1 && exactReadback && duplicateConfirmCreatedNoWrite ? "PASS" : "FAIL",
                    actualProcess = "KnowledgeClipper.exe", semanticAction = AutomationCatalogV11.AutomationIds[AutomationCatalog.ReadCapture],
                    path = new[] { "BoundClipperForm", "readButton.PerformClick", "ApplicationCommandKind.ReadToAnki", "ReadToAnkiUseCases", "NamedPipeCoreGateway" },
                    languageChoice = choice, expectedCardCount, previewCardCount = preview.Draft.Cards.Count, previewCards = preview.Draft.Cards.Select(card => new { card.Template, card.Front, card.Back }).ToArray(),
                    beforeConfirm = before, afterConfirm = after, afterDuplicateConfirm,
                    duplicateConfirmCreatedNoWrite, exactReadback, readback, terminal = terminal.Status.ToString(), terminal.NoticeCode,
                    legacyRuntimeEnvironment = new { edgeTts = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_EDGE_TTS"), ffplay = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FFPLAY") }
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                File.WriteAllText(outputPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                output.WriteLine(JsonSerializer.Serialize(receipt));
                return receipt.status == "PASS" ? 0 : 5;
            }
            catch (Exception exception)
            {
                var failure = JsonSerializer.Serialize(new { schema = "kc.desktop-candidate-read-to-anki-process-canary/1", status = "FAIL", error = exception.Message });
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))); File.WriteAllText(outputPath, failure); output.WriteLine(failure); return 5;
            }
        }

        private static void WaitUntil(BoundClipperForm form, DateTimeOffset deadline, Func<FlashcardDraftPreviewState, bool> predicate, string error)
        {
            while (DateTimeOffset.UtcNow < deadline) { Application.DoEvents(); if (predicate(form.CandidateCanaryFlashcardState)) return; Thread.Sleep(50); }
            throw new TimeoutException(error);
        }

        private static void WaitForPreview(BoundClipperForm form, DateTimeOffset deadline)
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                Application.DoEvents();
                if (form.CandidateCanaryFlashcardState?.Status == FlashcardDraftStatus.AwaitingConfirmation) return;
                var read = form.CandidateCanaryReadToAnkiState;
                if (read != null && (read.Pipeline is ReadToAnkiPipelineStatus.Failed or ReadToAnkiPipelineStatus.PartiallyFailed or ReadToAnkiPipelineStatus.UnknownExternalState || read.Playback == ReadToAnkiPlaybackStatus.Failed))
                    throw new InvalidOperationException($"candidate-canary.preview-unavailable:{read.Pipeline}:{read.Playback}:{read.NoticeCode}");
                Thread.Sleep(50);
            }
            throw new TimeoutException("candidate-canary.preview-timeout");
        }

        private static string[] FindAnkiReadbacks(string root) => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "readback.json", SearchOption.AllDirectories).Where(path => { var text = File.ReadAllText(path); return text.Contains("\"cardsInfo\"", StringComparison.Ordinal) && text.Contains("\"notesInfo\"", StringComparison.Ordinal); }).ToArray()
            : Array.Empty<string>();

        private sealed record AnkiEvidenceSnapshot(int Attempts, int ReadbackFiles);
        private sealed record AnkiReadbackCard(int Ordinal, long NoteId, long[] CardIds, string DeckName, string Front, string Back, bool Exact);
        private sealed record AnkiReadbackEvidence(string Path, int CardCount, bool Exact, AnkiReadbackCard[] Cards);

        private static AnkiEvidenceSnapshot SnapshotAnkiEvidence(string root)
        {
            var attempts = Directory.Exists(root) ? Directory.EnumerateDirectories(root, "attempt-*", SearchOption.AllDirectories).Count(path => path.Replace('\\', '/').Contains("/stages/anki-export/attempts/", StringComparison.OrdinalIgnoreCase)) : 0;
            return new AnkiEvidenceSnapshot(attempts, FindAnkiReadbacks(root).Length);
        }

        private static AnkiReadbackEvidence ReadExactReadback(string root, FlashcardDraftRevision draft)
        {
            var path = FindAnkiReadbacks(root).SingleOrDefault();
            if (path == null) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var cards = document.RootElement.GetProperty("cards").EnumerateArray().OrderBy(value => value.GetProperty("ordinal").GetInt32()).ToArray();
            var projected = new AnkiReadbackCard[cards.Length];
            for (var index = 0; index < cards.Length; index++)
            {
                var notes = cards[index].GetProperty("notesInfo");
                var info = cards[index].GetProperty("cardsInfo").EnumerateArray().Single();
                var fields = notes.GetProperty("requiredFields");
                var front = fields.GetProperty("Front").GetString(); var back = fields.GetProperty("Back").GetString();
                var exact = index < draft.Cards.Count && front == draft.Cards[index].Front && back == draft.Cards[index].Back;
                projected[index] = new AnkiReadbackCard(cards[index].GetProperty("ordinal").GetInt32(), notes.GetProperty("noteId").GetInt64(), notes.GetProperty("cardIds").EnumerateArray().Select(value => value.GetInt64()).ToArray(), info.GetProperty("deckName").GetString(), front, back, exact);
            }
            return new AnkiReadbackEvidence(path, cards.Length, cards.Length == draft.Cards.Count && projected.All(card => card.Exact), projected);
        }

        private sealed class DeterministicTextCapture : ICurrentTextCapturePort
        {
            private readonly string text;
            internal DeterministicTextCapture(string text) { this.text = text; }
            public Task<CapturedPlainText> CaptureOnceAsync(CancellationToken cancellationToken) => Task.FromResult(new CapturedPlainText(text));
        }
    }
}

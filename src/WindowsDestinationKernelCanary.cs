using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        private static int DestinationKernelOnly(string root, string pipe)
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("immutable-evidence-exists");
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var client = new CoreV3Client(pipe, new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(25)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var ct = deadline.Token;
            var discovery = new RemNoteSettingsSdkBridge(client).DiscoverAsync(new("desktop-kernel-discover-" + Guid.NewGuid().ToString("N"), "Fixture root"), ct).GetAwaiter().GetResult();
            foreach (var choices in new[] { Array.Empty<string>(), new[] { "anki" }, new[] { "remnote" }, new[] { "anki", "remnote" } })
            {
                var config = new RemNoteDestinationSettings("remnote-destination-settings.v1", true, new(discovery.Binding.RootRemId, discovery.Binding.RootTitle),
                    new("mcp-direct", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd"));
                var workflow = new WorkflowSettingsDocument("knowledge-clipper-workflow-settings.v3", "profile", new Dictionary<string, WorkflowSettingsProfile> {
                    ["profile"] = new("Saved", new("cards", choices), Remnote: config) });
                var local = new DesktopModuleSettingsDocument(); local.RemNoteBindings["profile"] = new(config, CoreV3Client.ExportRemNoteBinding(discovery.Binding));
                var reads = 0; var ankiBuilds = 0;
                var publisher = new DesktopDestinationSettingsPublisher(() => { reads++; return workflow; }, () => local,
                    _ => { ankiBuilds++; return "{\"schema_version\":\"anki-export-config.v1\",\"provision_managed\":false,\"deck_routes\":{\"canary\":\"零碎知识剪藏::Canary::Offline\"}}"; });
                // No injected SDK calls: exact production factory/client/Named Pipe.
                var gateway = publisher.CreateGateway(pipe, () => new("kernel-canary", "both"));
                var capture = gateway.PublishCaptureTextAsync(new("Immutable synthetic source."), ct).GetAwaiter().GetResult();
                var submitted = gateway.SubmitAsync(new(capture, false) { TargetText = "Immutable synthetic source.", ContextRequested = false,
                    ContextGranularity = "sentence", SourceWindow = "offline-fixture" }, ct).GetAwaiter().GetResult();
                Assert(submitted.Accepted, "actual SDK/Kernel initial accepted: " + string.Join("+", choices));
                var projection = WaitDestinationKernel(gateway, submitted.OperationId, ct, false);
                Assert(projection.Status == FlashcardDraftStatus.AwaitingConfirmation && projection.Draft.DestinationSnapshot != null,
                    "real Kernel projection reaches shared preview with frozen snapshot");
                var binding = FlashcardDraftConfirmation.From(projection.Draft);
                var frozen = client.GetFrozenDestinationsAsync(new(submitted.OperationId, binding.DestinationSnapshot.Id), ct).GetAwaiter().GetResult();
                Assert(frozen.Destinations.Count == choices.Length && projection.Draft.DestinationOutcomes.All(x => x.Configuration.Id == binding.DestinationSnapshot.Id)
                    && frozen.Destinations.All(x => x.Configuration.Token != binding.DestinationSnapshot.Id),
                    "actual Kernel aggregate ConfigurationSnapshot differs from target configs, each SHA-read by SDK");
                workflow = workflow with { Profiles = new Dictionary<string, WorkflowSettingsProfile> { ["profile"] = workflow.Profiles["profile"] with { Workflow = new("cards", Array.Empty<string>()) } } };
                gateway = publisher.CreateGateway(pipe, () => new("changed-settings", "ask"));
                var reopened = WaitDestinationKernel(gateway, submitted.OperationId, ct, false);
                Assert(reopened.Draft.DestinationSnapshot.Id == binding.DestinationSnapshot.Id && reopened.Draft.Cards.SequenceEqual(projection.Draft.Cards) && reads == 1,
                    "new gateway restores frozen draft/targets from real Kernel evidence without rereading current settings");
                var cases = new FlashcardDraftPreviewUseCases(gateway); cases.Project(projection);
                using var window = new FlashcardPreviewForm(async () => { await cases.ConfirmAsync(ct); }, async () => { await cases.CancelAsync(ct); }, _ => Task.CompletedTask);
                cases.Changed += window.Render; window.Render(cases.Current); window.Show(); Pump(20);
                Assert(Find<TextBox>(window, FlashcardPreviewAutomation.Front).Text == projection.Draft.Cards[0].Front
                    && Find<TextBox>(window, FlashcardPreviewAutomation.Back).Text == projection.Draft.Cards[0].Back,
                    "actual Kernel literal card content is rendered without replacement/oracle");
                Find<Button>(window, FlashcardPreviewAutomation.Confirm).PerformClick();
                var watch = Stopwatch.StartNew();
                while (cases.Current.Status is FlashcardDraftStatus.AwaitingConfirmation or FlashcardDraftStatus.Confirming or FlashcardDraftStatus.Exporting or FlashcardDraftStatus.Processing)
                { ct.ThrowIfCancellationRequested(); if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("kernel-preview-confirm-timeout"); Pump(25); }
                var terminal = WaitDestinationKernel(gateway, submitted.OperationId, ct, true); cases.Project(terminal); Pump(20);
                Assert(terminal.Status == FlashcardDraftStatus.Completed && terminal.Draft.DestinationOutcomes.Count == choices.Length
                    && terminal.Draft.DestinationOutcomes.All(x => x.State == "succeeded" && x.Evidence != null && x.ExternalStateKnown),
                    "actual preview click dispatches SDK ConfirmCards to Kernel and each Fake exporter result returns");
                Assert(reads == 1 && ankiBuilds == (choices.Contains("anki") ? 1 : 0) && terminal.Draft.DestinationSnapshot.Id == binding.DestinationSnapshot.Id,
                    "real confirmation ignores changed settings, keeps task snapshot, zero unnecessary Anki config");
                Assert(terminal.Draft.Cards.SequenceEqual(projection.Draft.Cards) && !Find<Button>(window, FlashcardPreviewAutomation.Confirm).Enabled,
                    "same card draft remains visible and terminal confirmation cannot repeat");
                var duplicate = gateway.ConfirmAsync(binding, ct).GetAwaiter().GetResult();
                var after = gateway.GetAsync(submitted.OperationId, ct).GetAwaiter().GetResult();
                Assert(duplicate.Accepted && after.Cursor == terminal.Cursor && after.Draft.DestinationOutcomes.SequenceEqual(terminal.Draft.DestinationOutcomes),
                    "duplicate UI gateway call is query-only: real Kernel cursor/evidence unchanged");
                var name = choices.Length == 0 ? "none" : string.Join("-", choices);
                File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(new { input = "Immutable synthetic source.", selected = choices,
                    before = projection, destinationSnapshot = binding.DestinationSnapshot, beforeOutcomes = projection.Draft.DestinationOutcomes,
                    after = terminal, afterOutcomes = terminal.Draft.DestinationOutcomes, reads, ankiBuilds }));
                using (var image = new System.Drawing.Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, window.Width, window.Height)); image.Save(Path.Combine(root, name + ".png")); }
                cases.Changed -= window.Render;
            }
            File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { status = "PASS-DESKTOP-SDK-KERNEL-NAMEDPIPE-PREVIEW-CONFIRM", assertions,
                clientDll = typeof(CoreV3Client).Assembly.Location, realSdkNamedPipeKernel = true, typedCallsInjected = false, workers = "Core-owned Fake runtime",
                externalCalls = 0, physicalAudio = 0, physicalInput = 0, productionCandidateModified = false }));
            Console.WriteLine("DESTINATION_KERNEL_UI PASS assertions=" + assertions);
            return 0;
        }

        private static FlashcardDraftProjection WaitDestinationKernel(NamedPipeCoreGateway gateway, string op, CancellationToken ct, bool terminal)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(25))
            {
                ct.ThrowIfCancellationRequested();
                var result = gateway.GetAsync(op, ct).GetAwaiter().GetResult();
                if (result.Status is FlashcardDraftStatus.Failed or FlashcardDraftStatus.Unknown or FlashcardDraftStatus.Cancelled)
                    throw new InvalidOperationException("kernel-projection-" + result.Status + ":" + result.NoticeCode);
                if (terminal ? result.Status == FlashcardDraftStatus.Completed : result.Status == FlashcardDraftStatus.AwaitingConfirmation) return result;
                Pump(25);
            }
            throw new TimeoutException("kernel-destination-projection-timeout");
        }
    }
}

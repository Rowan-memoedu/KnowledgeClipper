using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.Core;
using KnowledgeClipper.Desktop.CoreGateway.Tests;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        private static int RemNoteSettingsOnly(string root)
        {
            if (Directory.Exists(root)) throw new Exception("immutable-evidence-exists");
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            const string baseline = "D:\\Artifacts\\KnowledgeClipper\\desktop-read-to-anki\\desktop-whole-window-zoom-20260909-01";
            var config = Config(Path.Combine(root, "data"));
            var fake = new RemNoteSettingsFixture();
            var gateway = new NamedPipeCoreGateway(fake);
            var local = new ReadToAnkiOnlyApplicationSupportPorts();
            var app = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, local, local, local);
            SettingsForm Open()
            {
                var d = LegacyDesktopAdapter.CreateSettingsDependencies(config, UserSettings.Defaults(), null, null, null, null, app, baseline);
                var f = new SettingsForm(d); f.Show(); Pump(10);
                Find<ListBox>(f, SettingsAutomationCatalog.Navigation).SelectedIndex = 5;
                return f;
            }
            void Complete(SettingsForm f)
            { WaitUntil(() => Find<Button>(f, SettingsAutomationCatalog.ExportRemNoteDiscoverTarget).Enabled && Find<Button>(f, SettingsAutomationCatalog.ExportRemNoteProbe).Enabled, 4000, "RemNote observation released"); Pump(10); }
            TextBox Title(SettingsForm f) => Find<TextBox>(f, SettingsAutomationCatalog.ExportRemNoteRootTitle);
            TextBox ProbeStatus(SettingsForm f) => Find<TextBox>(f, SettingsAutomationCatalog.ExportRemNoteProbeStatus);
            void Discover(SettingsForm f, string title)
            { Title(f).Text = title; Find<Button>(f, SettingsAutomationCatalog.ExportRemNoteDiscoverTarget).PerformClick(); Complete(f); }
            void Probe(SettingsForm f)
            { Find<Button>(f, SettingsAutomationCatalog.ExportRemNoteProbe).PerformClick(); Complete(f); }
            string Digest(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
            var moduleStore = new DesktopModuleSettingsStore(config.WorkDirectory);
            var service = new DesktopWorkflowSettingsService(config.WorkDirectory, baseline);
            var seeded = service.LoadOrCreate(UserSettings.Defaults());
            service.Save(seeded with { Profiles = new Dictionary<string, WorkflowSettingsProfile>(seeded.Profiles) { ["second"] = seeded.Profiles["default"] with { DisplayName = "Second" } } });

            using (var first = Open())
            {
                Discover(first, "A");
                Assert(!Find<CheckBox>(first, SettingsAutomationCatalog.ExportRemNote).Checked && !File.Exists(moduleStore.PathValue), "disabled unsaved discovery stays pending and does not save");
                Probe(first);
                Assert(fake.Discoveries == 1 && fake.Probes == 1 && ProbeStatus(first).AccessibleDescription.Contains("ready=true"), "same-window disabled unsaved verified binding can probe");
                Find<Button>(first, SettingsAutomationCatalog.Save).PerformClick(); Pump(10);
                Assert(first.IsDisposed && fake.Probes == 1, "save succeeds without another probe");
            }
            var savedA = moduleStore.Load().RemNoteBindings["default"];
            Assert(savedA.Settings.Target.RootTitle == "A" && !savedA.Settings.Enabled && savedA.Reference != null, "atomic local record stores exact config and SDK reference, not unverified selector");
            Assert(service.LoadOrCreate(UserSettings.Defaults()).Profiles["default"].Remnote.Target.RootTitle == "A", "unverified title never replaces public business target");
            var beforeCancel = Digest(moduleStore.PathValue);

            using (var second = Open())
            {
                Complete(second);
                Assert(fake.Probes == 1 && Find<TextBox>(second, SettingsAutomationCatalog.ExportRemNoteBoundTarget).AccessibleDescription.Contains("verified_historical") && !ProbeStatus(second).AccessibleDescription.Contains("ready=true"), "restart restores historical evidence without live readiness or probe");
                Discover(second, "uncommitted");
                Title(second).Text = "unverified-title";
                Probe(second);
                Assert(fake.Probes == 1 && !ProbeStatus(second).AccessibleDescription.Contains("ready=true") && Find<TextBox>(second, SettingsAutomationCatalog.ExportRemNoteRootId).Text == "root-A",
                    "new selector invalidates pending binding and restores saved A without rediscovery or probe");
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                fake.NextDiscoveryGate = gate; Title(second).Text = "late-A";
                Find<Button>(second, SettingsAutomationCatalog.ExportRemNoteDiscoverTarget).PerformClick(); Pump(10);
                var oldDiscoveryCount = fake.Discoveries;
                Find<ComboBox>(second, SettingsAutomationCatalog.WorkflowSelector).SelectedIndex = 1;
                Discover(second, "B"); Probe(second);
                var bText = ProbeStatus(second).Text;
                var bEvidence = ProbeStatus(second).AccessibleDescription;
                var reads = fake.EvidenceReads;
                gate.SetResult(true); WaitUntil(() => fake.LastLateCompletion != null, 2000, "A actually completes after B"); Pump(20);
                Assert(fake.Discoveries == oldDiscoveryCount + 1 && fake.EvidenceReads == reads && ProbeStatus(second).Text == bText && ProbeStatus(second).AccessibleDescription == bEvidence, "old A discovery ignores cancellation but cannot mutate B UI or evidence");
                Assert(Find<TextBox>(second, SettingsAutomationCatalog.ExportRemNoteRootId).Text == "root-B" && Title(second).Text == "B", "profile/title/generation binding guards preserve B target");

                var probeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                fake.NextProbeGate = probeGate; fake.LastLateCompletion = null;
                Find<Button>(second, SettingsAutomationCatalog.ExportRemNoteProbe).PerformClick(); Pump(10);
                second.InvalidateRemNoteObservation(); fake.Ready = false; Probe(second);
                var nonReady = ProbeStatus(second).AccessibleDescription;
                probeGate.SetResult(true); WaitUntil(() => fake.LastLateCompletion != null, 2000, "old probe actually completes"); Pump(20);
                Assert(ProbeStatus(second).AccessibleDescription == nonReady && nonReady.Contains("ready=false"), "old healthy probe cannot replace newer blocked result");
                fake.Ready = true;

                // Force an actual atomic module-settings replacement failure; no fabricated result.
                using (var held = new FileStream(moduleStore.PathValue, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Find<Button>(second, SettingsAutomationCatalog.Save).PerformClick(); Pump(10);
                    Assert(!second.IsDisposed && Find<Label>(second, SettingsAutomationCatalog.Status).Text.StartsWith("保存失败"), "save failure leaves editable window and verified draft for retry");
                    Assert(Digest(moduleStore.PathValue) == beforeCancel, "failed replace preserves prior config/evidence bytes");
                }
                var probesBeforeSave = fake.Probes;
                Find<Button>(second, SettingsAutomationCatalog.Save).PerformClick(); Pump(10);
                Assert(second.IsDisposed && fake.Probes == probesBeforeSave, "retry saves without probing or granting write permission");
            }
            var saved = moduleStore.Load();
            Assert(saved.RemNoteBindings["default"].Reference == savedA.Reference && saved.RemNoteBindings["second"].Settings.Target.RootTitle == "B", "profile B save leaves A binding and evidence unchanged");
            var current = service.LoadOrCreate(UserSettings.Defaults());
            Assert(saved.RemNoteBindings["second"].Matches(current.Profiles["second"].Remnote), "next-task prerequisite uses matching saved config/reference pair");
            var stableHash = Digest(moduleStore.PathValue);
            using (var cancelled = Open())
            {
                Complete(cancelled); Discover(cancelled, "discarded");
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                fake.NextDiscoveryGate = gate; Title(cancelled).Text = "closed-late";
                Find<Button>(cancelled, SettingsAutomationCatalog.ExportRemNoteDiscoverTarget).PerformClick(); Pump(10);
                Find<Button>(cancelled, SettingsAutomationCatalog.Cancel).PerformClick(); Pump(10);
                using var replacement = Open(); Complete(replacement); Probe(replacement);
                var state = ProbeStatus(replacement).AccessibleDescription;
                fake.LastLateCompletion = null; gate.SetResult(true);
                WaitUntil(() => fake.LastLateCompletion != null, 2000, "closed-window callback actually returns"); Pump(20);
                Assert(ProbeStatus(replacement).AccessibleDescription == state && Digest(moduleStore.PathValue) == stableHash, "closed cancelled window cannot change replacement window or saved binding");
                replacement.Close();
            }
            fake.RestoreInvalid = true;
            using (var unavailable = Open())
            {
                Complete(unavailable); var probes = fake.Probes; Probe(unavailable);
                Assert(fake.Probes == probes && Find<TextBox>(unavailable, SettingsAutomationCatalog.ExportRemNoteBoundTarget).Text.Contains("未核实"), "missing historical evidence cannot restore a trusted binding from cached strings");
                unavailable.Close();
            }
            Assert(Digest(moduleStore.PathValue) == stableHash, "restore failure does not destroy saved references");
            var verifiedSaved = moduleStore.Load(); moduleStore.Save(verifiedSaved);
            File.WriteAllText(moduleStore.PathValue, "{broken-fixture");
            var recovered = moduleStore.Load();
            Assert(recovered.RemNoteBindings["second"] == verifiedSaved.RemNoteBindings["second"], "corrupt local primary falls back to paired config/reference backup");
            moduleStore.Save(recovered);
            var receipt = new { status = "PASS-DESKTOP-REMNOTE-TYPED-SETTINGS-FAKE", assertions, fake.Discoveries, fake.Probes, fake.Restores,
                externalCalls = 0, workerRuns = 0, physicalInput = 0, physicalAudio = 0, userCandidateGenerated = false,
                scope = "actual SettingsForm semantic controls -> Application -> Gateway -> typed SDK bridge with fake SDK functions; saved reference is not live readiness" };
            using (var stream = new FileStream(Path.Combine(root, "receipt.json"), FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(stream, receipt, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine("REMNOTE_SETTINGS_UI PASS assertions=" + assertions);
            return 0;
        }
    }
}

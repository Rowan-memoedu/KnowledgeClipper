using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;

namespace KnowledgeClipper
{
    internal sealed class DesktopDestinationSettingsPublisher
    {
        private readonly Func<WorkflowSettingsDocument> workflow;
        private readonly Func<DesktopModuleSettingsDocument> modules;
        private readonly Func<DesktopModuleSettingsDocument, string> ankiConfiguration;

        internal DesktopDestinationSettingsPublisher(Func<WorkflowSettingsDocument> workflow,
            Func<DesktopModuleSettingsDocument> modules, Func<DesktopModuleSettingsDocument, string> ankiConfiguration)
        { this.workflow = workflow; this.modules = modules; this.ankiConfiguration = ankiConfiguration; }

        internal Task<IReadOnlyList<CardDestinationSelection>> FreezeAsync(CoreV3Client client, CancellationToken ct) => FreezeAsync(
            (purpose, json, token) => client.PublishTextArtifactAsync(new PublishTextArtifactRequest("desktop-destination-" + Guid.NewGuid().ToString("N"), purpose, json), token),
            client.RestoreRemNoteBindingAsync, ct);

        internal NamedPipeCoreGateway CreateGateway(string pipeName, Func<SdkLanguageCardSettings> languageCards, DestinationSdkCalls offlineCalls = null)
        {
            if (offlineCalls == null) return new NamedPipeCoreGateway(pipeName, readLanguageCardSettings: languageCards, freezeDestinations: FreezeAsync);
            return new NamedPipeCoreGateway(new SdkDomainFacade(offlineCalls, ct => FreezeAsync(
                (purpose, text, token) => offlineCalls.Publish(new PublishTextArtifactRequest("desktop-destination-" + Guid.NewGuid().ToString("N"), purpose, text), token),
                offlineCalls.RestoreRemNote, ct)), readLanguageCardSettings: languageCards);
        }

        internal async Task<IReadOnlyList<CardDestinationSelection>> FreezeAsync(
            Func<ArtifactPurpose, string, CancellationToken, Task<TypedArtifactHandle>> publish,
            Func<RemNoteBindingReference, CancellationToken, Task<BindingRestoreResult>> restore, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Read saved settings once, before awaiting. Follow-up and confirmation must
            // never call this publisher again; Core owns the resulting frozen snapshot.
            var document = workflow();
            var profile = document.Profiles[document.ActiveProfileId];
            if ((profile.Workflow?.ProcessingProfile ?? "cards") != "cards") throw new InvalidOperationException("destination-cards-only");
            var selected = (profile.Workflow?.Destinations ?? Array.Empty<string>()).ToArray();
            if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length || selected.Any(x => x is not ("anki" or "remnote")))
                throw new InvalidOperationException("destination-selection-invalid");
            if (selected.Length == 0) return Array.Empty<CardDestinationSelection>();
            var local = modules();
            var ankiJson = selected.Contains("anki", StringComparer.Ordinal) ? ankiConfiguration(local) : null;
            DesktopRemNoteSavedBinding saved = null;
            string remnoteJson = null;
            if (selected.Contains("remnote", StringComparer.Ordinal))
            {
                if (profile.Remnote?.Enabled != true || local.RemNoteBindings == null ||
                    !local.RemNoteBindings.TryGetValue(document.ActiveProfileId, out saved) || !saved.Matches(profile.Remnote))
                    throw new InvalidOperationException("destination-remnote-verified-reference-required");
                remnoteJson = JsonSerializer.Serialize(profile.Remnote, ContractJson.Options);
            }
            // The admitted Anki authorization remains isolated-canary-deck. No broader
            // route is inferred from settings; the compatible route must exist explicitly.
            if (ankiJson != null)
            {
                using var json = JsonDocument.Parse(ankiJson);
                if (!json.RootElement.GetProperty("deck_routes").TryGetProperty("canary", out var route) || string.IsNullOrWhiteSpace(route.GetString()))
                    throw new InvalidOperationException("destination-anki-canary-route-required");
            }
            VerifiedRemNoteBinding binding = null;
            if (saved != null)
            {
                var restored = await restore(saved.Reference, ct).WaitAsync(ct).ConfigureAwait(false);
                binding = restored?.Binding;
                if (restored?.Status != "verified_historical" || binding == null || binding.Evidence.Token != saved.Reference.EvidenceToken ||
                    binding.DiscoveryOperationId != saved.Reference.DiscoveryOperationId || binding.RootRemId != saved.Settings.Target.RootRemId || binding.RootTitle != saved.Settings.Target.RootTitle)
                    throw new InvalidOperationException("destination-remnote-binding-unverified");
            }
            ct.ThrowIfCancellationRequested();
            var result = new List<CardDestinationSelection>();
            foreach (var target in selected)
            {
                if (target == "anki") result.Add(new AnkiSelection(await publish(ArtifactPurpose.AnkiConfiguration, ankiJson, ct).ConfigureAwait(false), "canary"));
                else result.Add(new RemNoteSelection(await publish(ArtifactPurpose.RemNoteSettings, remnoteJson, ct).ConfigureAwait(false), binding));
            }
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    internal interface IRemNoteSettingsSdkFacade
    {
        Task<RemNoteDiscoverySummary> DiscoverRemNoteAsync(RemNoteDiscoveryRequest request, CancellationToken ct);
        Task<RemNoteReadinessSummary> ProbeRemNoteAsync(RemNoteProbeRequest request, CancellationToken ct);
        Task<BindingRestoreResult> RestoreRemNoteAsync(RemNoteBindingReference reference, CancellationToken ct);
    }
    internal sealed partial class SdkDomainFacade : IRemNoteSettingsSdkFacade
    {
        public Task<RemNoteDiscoverySummary> DiscoverRemNoteAsync(RemNoteDiscoveryRequest request, CancellationToken ct)
            => new RemNoteSettingsSdkBridge(client).DiscoverAsync(request, ct);
        public Task<RemNoteReadinessSummary> ProbeRemNoteAsync(RemNoteProbeRequest request, CancellationToken ct)
            => new RemNoteSettingsSdkBridge(client).ProbeAsync(request, ct);
        public Task<BindingRestoreResult> RestoreRemNoteAsync(RemNoteBindingReference reference, CancellationToken ct)
            => new RemNoteSettingsSdkBridge(client).RestoreAsync(reference, ct);
    }
    internal sealed partial class NamedPipeCoreGateway
    {
        private async Task<ModuleActionResult> ExecuteRemNoteAsync(ModuleActionRequest request, CancellationToken ct)
        {
            if (sdk is not IRemNoteSettingsSdkFacade remnote) return ModuleRejected("remnote-typed-sdk-unavailable");
            if (request.Arguments == null || !request.Arguments.TryGetValue("request", out var value)) return ModuleRejected("remnote-typed-request-required");
            try
            {
                object result;
                TypedArtifactHandle evidence;
                if (request.Kind == ModuleActionKind.RemNoteDiscoverTarget && value is RemNoteDiscoveryRequest discovery)
                {
                    var summary = await remnote.DiscoverRemNoteAsync(discovery, ct).ConfigureAwait(false);
                    result = summary; evidence = summary.Evidence;
                }
                else if (request.Kind == ModuleActionKind.RemNoteProbe && value is RemNoteProbeRequest probe)
                {
                    var summary = await remnote.ProbeRemNoteAsync(probe, ct).ConfigureAwait(false);
                    result = summary; evidence = summary.Evidence;
                }
                else if (request.Kind == ModuleActionKind.RemNoteRestoreBinding && value is RemNoteBindingReference reference)
                {
                    var restored = await remnote.RestoreRemNoteAsync(reference, ct).ConfigureAwait(false);
                    result = restored; evidence = restored.Binding?.Evidence;
                    if (restored.Status != "verified_historical" || evidence == null) return ModuleRejected(restored.ReasonCode ?? "remnote-binding-not-restored");
                }
                else return ModuleRejected("remnote-typed-request-mismatch");
                ct.ThrowIfCancellationRequested();
                if (evidence == null) return ModuleRejected("remnote-evidence-missing");
                return new ModuleActionResult(true, OperationStatus.Succeeded, "remnote-typed-observation", new Dictionary<string, object> { ["result"] = result },
                    new ArtifactHandle(evidence.Token, "remnote-settings-evidence"), false, false);
            }
            catch (OperationCanceledException) { return ModuleRejected("remnote-observation-cancelled", OperationStatus.Cancelled); }
            catch (CoreV3ClientException ex) { return ModuleRejected(ex.Code); }
            catch (InvalidOperationException ex) { return ModuleRejected(ex.Message); }
        }
    }
}

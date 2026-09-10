using System;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    // Public SDK only; no worker CLI, transport DTO or file-path fallback.
    internal sealed class RemNoteSettingsSdkBridge
    {
        private readonly Func<RemNoteDiscoveryRequest, CancellationToken, Task<OperationSubmission>> discover;
        private readonly Func<RemNoteProbeRequest, CancellationToken, Task<OperationSubmission>> probe;
        private readonly Func<string, CancellationToken, Task<OperationProjectionSnapshot>> query;
        private readonly Func<TypedArtifactHandle, CancellationToken, Task<byte[]>> read;
        private readonly Func<RemNoteBindingReference, CancellationToken, Task<BindingRestoreResult>> restore;
        private readonly TimeSpan timeout, interval;

        internal RemNoteSettingsSdkBridge(CoreV3Client client)
            : this(client.DiscoverRemNoteTargetAsync, client.ProbeRemNoteAsync, client.GetOperationProjectionAsync,
                async (handle, ct) => (await client.ReadArtifactAsync(handle, null, ct).ConfigureAwait(false)).Content,
                client.RestoreRemNoteBindingAsync) { }

        // Typed function injection for offline tests; exactly the public SDK types.
        internal RemNoteSettingsSdkBridge(
            Func<RemNoteDiscoveryRequest, CancellationToken, Task<OperationSubmission>> discover,
            Func<RemNoteProbeRequest, CancellationToken, Task<OperationSubmission>> probe,
            Func<string, CancellationToken, Task<OperationProjectionSnapshot>> query,
            Func<TypedArtifactHandle, CancellationToken, Task<byte[]>> read,
            Func<RemNoteBindingReference, CancellationToken, Task<BindingRestoreResult>> restore,
            TimeSpan? timeout = null, TimeSpan? interval = null)
        {
            this.discover = discover ?? throw new ArgumentNullException(nameof(discover));
            this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
            this.query = query ?? throw new ArgumentNullException(nameof(query));
            this.read = read ?? throw new ArgumentNullException(nameof(read));
            this.restore = restore ?? throw new ArgumentNullException(nameof(restore));
            this.timeout = timeout ?? TimeSpan.FromSeconds(60);
            this.interval = interval ?? TimeSpan.FromMilliseconds(250);
        }

        internal async Task<RemNoteDiscoverySummary> DiscoverAsync(RemNoteDiscoveryRequest request, CancellationToken ct)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.RootTitle))
                throw new InvalidOperationException("remnote-discovery-selector-required");
            using var bound = Bound(ct);
            var accepted = await discover(request, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            var projection = await ObserveAsync(accepted, bound.Token).ConfigureAwait(false);
            var summary = projection.RemNoteDiscovery;
            var binding = summary?.Binding;
            if (summary == null || !summary.PersistableBinding || summary.CandidateCount != 1 || binding == null ||
                summary.Evidence == null || binding.DiscoveryOperationId != projection.OperationId ||
                binding.RootTitle != request.RootTitle || string.IsNullOrWhiteSpace(binding.RootRemId) ||
                binding.Evidence.Token != summary.Evidence.Token)
                throw new InvalidOperationException("remnote-discovery-binding-unverified");
            await VerifyEvidenceAsync(summary.Evidence, bound.Token).ConfigureAwait(false);
            return summary;
        }

        internal async Task<RemNoteReadinessSummary> ProbeAsync(RemNoteProbeRequest request, CancellationToken ct)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId) || request.Target == null)
                throw new InvalidOperationException("remnote-probe-binding-required");
            using var bound = Bound(ct);
            var restored = await RestoreAsync(CoreV3Client.ExportRemNoteBinding(request.Target), bound.Token).ConfigureAwait(false);
            if (restored.Binding.RootRemId != request.Target.RootRemId || restored.Binding.RootTitle != request.Target.RootTitle)
                throw new InvalidOperationException("remnote-probe-binding-mismatch");
            var accepted = await probe(request with { Target = restored.Binding }, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            var projection = await ObserveAsync(accepted, bound.Token).ConfigureAwait(false);
            var summary = projection.RemNoteReadiness;
            if (summary?.Evidence == null) throw new InvalidOperationException("remnote-probe-evidence-missing");
            await VerifyEvidenceAsync(summary.Evidence, bound.Token).ConfigureAwait(false);
            // Keep the SDK's independent booleans. Connected alone never promotes readiness.
            return summary;
        }

        internal async Task<BindingRestoreResult> RestoreAsync(RemNoteBindingReference reference, CancellationToken ct)
        {
            if (reference == null || string.IsNullOrWhiteSpace(reference.DiscoveryOperationId) || string.IsNullOrWhiteSpace(reference.EvidenceToken))
                throw new InvalidOperationException("remnote-restore-reference-required");
            using var bound = Bound(ct);
            var result = await restore(reference, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            if (result?.Status != "verified_historical" || result.Binding == null ||
                result.Binding.DiscoveryOperationId != reference.DiscoveryOperationId || result.Binding.Evidence.Token != reference.EvidenceToken)
                throw new InvalidOperationException(result?.ReasonCode ?? "remnote-restore-evidence-unverified");
            // Historical binding integrity is not fresh connectivity or a write grant.
            return result;
        }

        internal static bool IsReady(RemNoteReadinessSummary value) => value?.Evidence != null &&
            value.Connected && value.WritePermission && value.ExactTargetMatch && value.ReadyForRequestedAction;

        private CancellationTokenSource Bound(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var result = CancellationTokenSource.CreateLinkedTokenSource(ct);
            result.CancelAfter(timeout);
            return result;
        }

        private async Task<OperationProjectionSnapshot> ObserveAsync(OperationSubmission accepted, CancellationToken ct)
        {
            if (accepted == null || !accepted.Accepted || string.IsNullOrWhiteSpace(accepted.OperationId))
                throw new InvalidOperationException("remnote-settings-submit-rejected");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var projection = await query(accepted.OperationId, ct).WaitAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (projection == null || projection.OperationId != accepted.OperationId)
                    throw new InvalidOperationException("remnote-settings-operation-mismatch");
                if (projection.State == OperationProjectionState.Running)
                { await Task.Delay(interval, ct).ConfigureAwait(false); continue; }
                if (projection.State != OperationProjectionState.Succeeded)
                    throw new InvalidOperationException(projection.Notice ?? "remnote-settings-not-succeeded");
                return projection;
            }
        }

        private async Task VerifyEvidenceAsync(TypedArtifactHandle evidence, CancellationToken ct)
        {
            var bytes = await read(evidence, ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (bytes == null || bytes.Length == 0) throw new InvalidOperationException("remnote-settings-evidence-empty");
        }
    }
}

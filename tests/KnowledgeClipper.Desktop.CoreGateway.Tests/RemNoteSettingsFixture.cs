using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;

namespace KnowledgeClipper.Desktop.CoreGateway.Tests
{
    internal sealed class RemNoteSettingsFixture : AnkiProbeFixture, IRemNoteSettingsSdkFacade
    {
        internal int Discoveries, Probes, Restores, EvidenceReads;
        internal bool Ready = true, RestoreInvalid;
        internal TaskCompletionSource<bool> NextDiscoveryGate, NextProbeGate;
        internal string LastLateCompletion;
        private readonly Dictionary<string, VerifiedRemNoteBinding> bindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<OperationProjectionSnapshot>> results = new(StringComparer.Ordinal);
        private long sequence;
        private RemNoteSettingsSdkBridge Bridge() => new(
            (request, ct) =>
            {
                Discoveries++;
                var id = "discovery-" + ++sequence;
                var binding = FakeBinding(id, request.RootTitle);
                bindings[id] = binding;
                var gate = NextDiscoveryGate; NextDiscoveryGate = null;
                results[id] = Complete(id, new OperationProjectionSnapshot(id, OperationProjectionState.Succeeded, 2, null, null)
                { RemNoteDiscovery = new("unique", Array.Empty<string>(), 1, true, binding, binding.Evidence) }, gate);
                return Task.FromResult(new OperationSubmission(id, 1, true));
            },
            (request, ct) =>
            {
                Probes++; var id = "probe-" + ++sequence;
                var gate = NextProbeGate; NextProbeGate = null;
                results[id] = Complete(id, new OperationProjectionSnapshot(id, OperationProjectionState.Succeeded, 2, null, null)
                { RemNoteReadiness = new(true, Ready, Ready, Ready, Ready ? Array.Empty<string>() : new[] { "not-ready" }, request.Target.Evidence) }, gate);
                return Task.FromResult(new OperationSubmission(id, 1, true));
            },
            (id, ct) => results[id],
            (handle, ct) => { EvidenceReads++; return Task.FromResult(new byte[] { 1 }); },
            (reference, ct) =>
            {
                Restores++;
                return Task.FromResult(!RestoreInvalid && bindings.TryGetValue(reference.DiscoveryOperationId, out var binding) && binding.Evidence.Token == reference.EvidenceToken
                    ? new BindingRestoreResult("verified_historical", binding, null)
                    : new BindingRestoreResult("invalid", null, "binding-evidence-unavailable"));
            }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1));

        public Task<RemNoteDiscoverySummary> DiscoverRemNoteAsync(RemNoteDiscoveryRequest request, CancellationToken ct) => Bridge().DiscoverAsync(request, ct);
        public Task<RemNoteReadinessSummary> ProbeRemNoteAsync(RemNoteProbeRequest request, CancellationToken ct) => Bridge().ProbeAsync(request, ct);
        public Task<BindingRestoreResult> RestoreRemNoteAsync(RemNoteBindingReference reference, CancellationToken ct) => Bridge().RestoreAsync(reference, ct);
        private async Task<OperationProjectionSnapshot> Complete(string id, OperationProjectionSnapshot value, TaskCompletionSource<bool> gate)
        { if (gate != null) { await gate.Task; LastLateCompletion = id; } return value; }
        private static VerifiedRemNoteBinding FakeBinding(string operation, string title)
        {
            // Test fixture only; no reflection/DTO fabrication in production.
            var assembly = Assembly.Load("KnowledgeClipper.Core.V3.Contracts");
            var evidence = Activator.CreateInstance(assembly.GetType("KnowledgeClipper.Core.V3.Contracts.ArtifactReference"), "fixture", operation, "remnote-discovery", "fixture", null, "fixture");
            var binding = Activator.CreateInstance(assembly.GetType("KnowledgeClipper.Core.V3.Contracts.RemNoteBindingReference"), operation, evidence, "root-" + title, title);
            return (VerifiedRemNoteBinding)Activator.CreateInstance(typeof(VerifiedRemNoteBinding), BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { binding }, null);
        }
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.Adapters.V3
{
    // Typed SDK observations only; no protocol, worker, log, or filesystem fallback.
    internal sealed class AnkiProbeSdkObserver
    {
        private readonly Func<string, CancellationToken, Task<OperationProjectionSnapshot>> query;
        private readonly Func<TypedArtifactHandle, CancellationToken, Task<byte[]>> read;
        private readonly TimeSpan timeout, interval;
        internal AnkiProbeSdkObserver(Func<string, CancellationToken, Task<OperationProjectionSnapshot>> query,
            Func<TypedArtifactHandle, CancellationToken, Task<byte[]>> read, TimeSpan? timeout = null, TimeSpan? interval = null)
        { this.query = query; this.read = read; this.timeout = timeout ?? TimeSpan.FromSeconds(60); this.interval = interval ?? TimeSpan.FromMilliseconds(250); }

        internal async Task<SdkAnkiReadiness> ObserveAsync(string operationId, string route, CancellationToken ct)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    bound.Token.ThrowIfCancellationRequested();
                    var p = await query(operationId, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
                    if (p == null || p.OperationId != operationId) return Unverified("anki-probe-operation-mismatch");
                    if (p.State == OperationProjectionState.Running) { await Task.Delay(interval, bound.Token).ConfigureAwait(false); continue; }
                    var evidence = p.AnkiConnectionStatusArtifact == null ? null : new SdkArtifact(p.AnkiConnectionStatusArtifact.Token, p.AnkiConnectionStatusArtifact);
                    if (p.State != OperationProjectionState.Succeeded)
                        return new(p.State == OperationProjectionState.Cancelled ? OperationStatus.Cancelled : p.State == OperationProjectionState.Failed ? OperationStatus.Failed : OperationStatus.Unknown,
                            p.Notice ?? "anki-probe-not-succeeded", false, false, route, Array.Empty<string>(), evidence);
                    var r = p.AnkiReadiness;
                    if (r == null || evidence == null) return Unverified("anki-probe-evidence-missing");
                    if (string.IsNullOrWhiteSpace(route) || r.SelectedRoute != route) return Unverified("anki-probe-route-mismatch");
                    if (r.NonBlockingIssues == null || r.BlockingIssues == null) return Unverified("anki-probe-readiness-invalid");
                    // SDK validates the immutable representation bytes/hash. Do not parse its opaque token.
                    var bytes = await read(p.AnkiConnectionStatusArtifact, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
                    if (bytes == null || bytes.Length == 0) return Unverified("anki-probe-evidence-empty");
                    var ready = r.ConnectionReady && r.SelectedRouteReady && r.ReadyForRequestedAction && r.BlockingIssues.Count == 0 &&
                        (r.OverallState == "ready" || r.OverallState == "partially_ready");
                    var optional = r.NonBlockingIssues.Where(x => x != null && x.StartsWith("route_not_ready:", StringComparison.Ordinal))
                        .Select(x => x.Substring("route_not_ready:".Length)).ToArray();
                    return new(OperationStatus.Succeeded, ready ? "anki-probe.current-target-ready" : "anki-probe.target-not-ready:" + string.Join(",", r.BlockingIssues),
                        ready, optional.Length > 0, route, optional, evidence);
                }
            }
            catch (OperationCanceledException) { return new(ct.IsCancellationRequested ? OperationStatus.Cancelled : OperationStatus.Unknown,
                ct.IsCancellationRequested ? "anki-probe-cancelled" : "anki-probe-observation-timeout", false, false, route, Array.Empty<string>(), null); }
        }
        private static SdkAnkiReadiness Unverified(string code) => new(OperationStatus.Unknown, code, false, false, null, Array.Empty<string>(), null);
    }
}

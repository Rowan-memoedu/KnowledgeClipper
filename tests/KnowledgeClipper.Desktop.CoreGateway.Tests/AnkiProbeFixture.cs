using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.CoreGateway.Tests
{
    internal class AnkiProbeFixture : ISdkDomainFacade
    {
        internal int Submits, Publishes, Reads, Queries;
        internal string Configuration, Route, Scenario = "ready";
        internal TaskCompletionSource<bool> QueryGate { get; set; }
        internal bool IgnoreQueryCancellation { get; set; }
        internal string LastCompletedQuery { get; private set; }
        internal string PublishFailure { get; set; }
        // Test-only opaque handle; production observer never constructs/decodes one.
        private readonly TypedArtifactHandle evidence = (TypedArtifactHandle)Activator.CreateInstance(typeof(TypedArtifactHandle), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { null }, null);
        public Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken ct)
        { if (purpose != SdkArtifactPurpose.AnkiConfiguration) throw new Exception("unexpected-purpose"); Publishes++; if(PublishFailure!=null)throw new OperationCanceledException(PublishFailure); Configuration=text; return Task.FromResult(new SdkArtifact("test-config", evidence)); }
        public Task<byte[]> ReadArtifactAsync(SdkArtifact a, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetBytes("immutable evidence"));
        public Task<SdkSubmission> AnkiProbeAsync(SdkArtifact a, string route, CancellationToken ct)
        { Submits++;Route=route;return Task.FromResult(new SdkSubmission("probe-"+Submits,null,Scenario != "rejected")); }
        public Task<SdkAnkiReadiness> ObserveAnkiReadinessAsync(string id, CancellationToken ct)
        {
            var submittedRoute = Route;
            var submittedScenario = Scenario;
            var gate = QueryGate;
            var ignoreCancellation = IgnoreQueryCancellation;
            var observer = new AnkiProbeSdkObserver(async (operation, token) =>
            {
                Queries++;
                if (gate != null) { if(ignoreCancellation) await gate.Task; else await gate.Task.WaitAsync(token); }
                if (ignoreCancellation)
                {
                    LastCompletedQuery = operation;
                    return new OperationProjectionSnapshot(operation, OperationProjectionState.Succeeded, 4, "late-"+submittedScenario, evidence)
                    { AnkiReadiness = new KnowledgeClipper.Core.V3.Client.AnkiReadinessSummary(true, submittedRoute, true, true, "ready", Array.Empty<string>(), Array.Empty<string>()) };
                }
                if (Scenario == "timeout") await Task.Delay(Timeout.Infinite, token);
                var state = Scenario switch { "failed" => OperationProjectionState.Failed, "unknown" => OperationProjectionState.Degraded, "cancelled" => OperationProjectionState.Cancelled, _ => OperationProjectionState.Succeeded };
                var blocked = Scenario is "empty-target" or "duplicate-target" or "target-not-ready";
                return new OperationProjectionSnapshot(Scenario == "operation-mismatch" ? "wrong-operation" : operation, state, 4, "fixture-"+Scenario, Scenario == "missing-evidence" ? null : evidence)
                { AnkiReadiness = Scenario == "missing-readiness" ? null : new KnowledgeClipper.Core.V3.Client.AnkiReadinessSummary(true, Scenario == "route-mismatch" ? "other" : Route,
                    !blocked, !blocked, blocked ? "blocked" : "partially_ready", new[] { "route_not_ready:optional-a", "route_not_ready:optional-b" }, blocked ? new[] { "selected_route_not_ready" } : Array.Empty<string>()) };
            }, (_, __) => { Reads++; if (Scenario == "evidence-invalid") throw new InvalidOperationException("artifact_sha256_mismatch"); return Task.FromResult(Scenario == "evidence-empty" ? Array.Empty<byte>() : Encoding.UTF8.GetBytes("hash-verified SDK evidence")); }, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1));
            return observer.ObserveAsync(id, Route, ct);
        }
        public Task<SdkSubmission> QuestionInitialAsync(SdkArtifact a,IReadOnlyList<SdkArtifact> b,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkSubmission> QuestionFollowUpAsync(string id,SdkArtifact a,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkSubmission> QuestionMakeCardsAsync(string id,SdkArtifact a,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkQuestionProjection> GetQuestionAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkProjection> GetTaskAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkReadToAnkiSubmission> ReadToAnkiAsync(SdkArtifact a,IReadOnlyList<SdkArtifact>b,IReadOnlyList<SdkContextSegment> c,string d,bool e,string f,string g,SdkArtifact h,SdkLanguageCardSettings i,bool j,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkReadToAnkiSubmission> ChooseLanguageCardAsync(string id,LanguagePracticeChoice c,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkReadToAnkiSubmission> ResolveCardAsync(CardResolutionSubmission s,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkReadToAnkiProjection> GetReadToAnkiProjectionAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkReadToAnkiSubmission> ReplayReadToAnkiAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkDraftCommand> ConfirmDraftAsync(FlashcardDraftConfirmation s,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SdkDraftCommand> CancelDraftAsync(FlashcardDraftConfirmation s,CancellationToken ct)=>throw new NotSupportedException();
    }
}

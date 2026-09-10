using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.CoreGateway.Tests
{
    internal static partial class Program
    {
        private static async Task AnkiProbeTests()
        {
            foreach(var route in new[]{"canary","language-writing"})
            foreach(var scenario in new[]{"ready","rejected","failed","unknown","cancelled","empty-target","duplicate-target","target-not-ready","missing-evidence","missing-readiness","route-mismatch","operation-mismatch","evidence-empty","evidence-invalid","timeout"})
            {
                var fake=new AnkiProbeFixture{Scenario=scenario};var gateway=new NamedPipeCoreGateway(fake);
                var state=new DesktopStateStore();var useCase=new ModuleActionUseCases(gateway,state);
                var artifact=await gateway.PublishAsync("anki-configuration","{\"provision_managed\":false}",CancellationToken.None);
                var result=await useCase.ExecuteAsync(ModuleActionKind.AnkiProbe,new Dictionary<string,object>{{"configuration",artifact},{"selectedRoute",route}},CancellationToken.None);
                var ready=result.Status==OperationStatus.Succeeded&&result.Output!=null&&result.AnkiReadiness?.CurrentTargetReady==true;
                Assert(ready==(scenario=="ready"),"typed observer "+route+" "+scenario);
                Assert(fake.Submits==1&&fake.Route==route,"exactly one explicitly routed probe");
                if(ready)Assert(result.AnkiReadiness.UninitializedRoutes.Count==2,"multiple non-current routes do not block ready target");
            }
            var cancelFake=new AnkiProbeFixture{Scenario="timeout"};var cg=new NamedPipeCoreGateway(cancelFake);
            var ca=await cg.PublishAsync("anki-configuration","{}",CancellationToken.None);
            using var cancel=new CancellationTokenSource(20);
            var cancelled=await cg.ExecuteAsync(new ModuleActionRequest(ModuleActionKind.AnkiProbe,new Dictionary<string,object>{{"configuration",ca},{"selectedRoute","second-route"}}),cancel.Token);
            Assert(cancelled.Status==OperationStatus.Cancelled&&!cancelled.AnkiReadiness.CurrentTargetReady,"caller cancellation never green");
            var missing=await cg.ExecuteAsync(new ModuleActionRequest(ModuleActionKind.AnkiProbe,new Dictionary<string,object>{{"configuration",ca}}),CancellationToken.None);
            Assert(!missing.Accepted&&cancelFake.Submits==1,"missing route never submits/defaults");
        }
    }
}

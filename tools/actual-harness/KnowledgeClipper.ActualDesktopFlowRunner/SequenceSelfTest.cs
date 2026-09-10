using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed class SequenceFixturePorts(IReadOnlyList<FlowStep> steps,int failAt=-1,bool duplicateOperation=false,bool wrongSource=false) : ICandidateSurface,ITaskObserver,IAnkiReadback
{
    internal readonly List<string> Calls=[];
    internal int Captures,Confirms,Starts,FinalChecks;
    int current=-1;bool ready=true,completed;string command="";long sequence;
    readonly HashSet<long> notes=[];
    FlowStep Step=>steps[current];string Op=>duplicateOperation?"op-0":"op-"+current;
    public string Identity=>"SequenceOfflineFixture";
    public void StartOwned(string exe,string args,string root,string? fixture=null){Starts++;Calls.Add("start");}
    public void AttachExact(string exe,string root) { }
    public void CreateExternalTextPatternSource(string text,int start,int length)=>throw new Exception("sequence-explicit-source-required");
    public void Invoke(string id)
    {
        Calls.Add("invoke:"+id);
        if(id is "read" or "clip")
        {
            if(!ready)throw new Exception("capture-before-own-ui-restored");
            current++;Captures++;ready=false;completed=false;command="capture-"+current;
            if(id!=(Step.IncludeSpeech?"read":"clip"))throw new Exception("wrong-capture-control");
        }
        if(id=="confirm"){Confirms++;completed=true;for(var i=0;i<Step.CardCount;i++)notes.Add(current*100+i+1);}
    }
    public void SetValue(string id,string value){if(id=="routes"&&!value.Contains("canary"))throw new Exception("route-not-canary");}
    public void SelectItem(string id,string value) { }
    public void AssertValue(string id,string value) { }
    public SavedAnkiDestinationEvidence VerifySavedDestination(string root,string deck)=>new("fake",new('a',64),"custom","canary",deck,SequenceSource.Hash(deck));
    public IReadOnlyList<PreviewCard> ReadPreviewCards(string list,string front,string back,RenderedPreviewIdentity expected,IReadOnlyList<PreviewCard> expectedCards)=>Cards();
    IReadOnlyList<PreviewCard> Cards()=>Enumerable.Range(0,Step.CardCount).Select(i=>new PreviewCard(i,"front-"+current+"-"+i,"back-"+current+"-"+i,Step.LanguageMode=="writing"||i==1?"language_writing":"language_context")).ToArray();
    public UiTerminalCheckpoint BeginUiTerminalObservation(string root)=>new("fake",current,DateTimeOffset.UtcNow);
    public UiTerminalEvidence WaitUiTerminal(UiTerminalCheckpoint checkpoint,string operationId,bool includeSpeech=true)
    {
        if(!completed||operationId!=Op||includeSpeech!=Step.IncludeSpeech)throw new Exception("wrong-ui-terminal");
        ready=true;Calls.Add("ui-ready:"+Op);return new("fake",Op,current,current,DateTimeOffset.UtcNow,checkpoint.NotBeforeUtc,new('a',64));
    }
    public object Begin(string marker)=>marker;
    public AcceptedOperationIdentity Discover(object checkpoint)=>new(Op,command,++sequence);
    public RunnerProjection WaitInitial(AcceptedOperationIdentity identity)=>Projection("WaitingLanguageCardChoice");
    public ActionCheckpoint BeginAction(string operationId,string action)=>new(operationId,action,sequence,null,command,sequence);
    public ActionTerminal WaitAction(ActionCheckpoint checkpoint)
    {
        command="command-"+(++sequence);var state=completed?"Completed":"AwaitingCardConfirmation";
        if(!completed&&checkpoint.SemanticAction!="language-practice.select:"+Step.LanguageMode)throw new Exception("wrong-mode-choice");
        if(current==failAt)state="Unknown";
        return new(Op,command,sequence,Projection(state));
    }
    public RunnerProjection WaitAggregate(string operationId,string finalCommandId,int expectedCardCount=2,bool includeSpeech=true)
    {if(expectedCardCount!=Step.CardCount||includeSpeech!=Step.IncludeSpeech)throw new Exception("wrong-aggregate-expectation");return Projection("Completed");}
    public void AssertNoAdditionalOperation(object checkpoint,AcceptedOperationIdentity identity,string finalCommandId){FinalChecks++;if(!ready||Captures!=4||Confirms!=4)throw new Exception("final-readonly-before-complete");}
    RunnerProjection Projection(string state)
    {
        var cards=Cards();
        var source=Step.Source!;
        var context=new RunnerContextEvidence("context",new('a',64),"capture",new('b',64),wrongSource?new('0',64):source.SelectedSha256,source.SelectedSha256,source.CompleteSha256,"contextual",true,true,"applied","sentence",new[]{new RunnerContextSegment(0,"prefix","prefix",new('c',64)),new RunnerContextSegment(1,"suffix","suffix",new('d',64))});
        return new(Op,state,Step.IncludeSpeech?"IdleReplayable":"NotRequested",null,command,sequence,sequence,state=="WaitingLanguageCardChoice",false,new('e',64),cards,
            completed?cards.Select(c=>new RunnerSemanticCard(current*100+c.Ordinal+1,new long[]{current*100+c.Ordinal+11},c.Front,c.Back,"零碎知识剪藏::Canary::SequenceOffline")).ToArray():Array.Empty<RunnerSemanticCard>(),
            "snapshot",new('f',64),"config",new('1',64),"canary","零碎知识剪藏::Canary::SequenceOffline",new('2',64),Step.IncludeSpeech,Step.IncludeSpeech,context);
    }
    public IReadOnlySet<long> CaptureBaseline(string deck)=>notes.ToHashSet();
    public AnkiReadback ReadNew(string deck,IReadOnlySet<long> baseline){var added=notes.Where(n=>!baseline.Contains(n)).ToArray();return new(added,added.Length,deck);}
    public void Dispose() { }
}

internal static class SequenceSelfTest
{
    internal static int Run()
    {
        var inputs=Enumerable.Range(0,4).Select(i=>new{prefix="Independent source "+i+": ",selectedText="word"+i,suffix="  context."}).ToArray();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion="desktop-test-capture-queue.v1",inputs});
        var plan=SequencePlan.Parse(bytes);
        var keys=new[]{"read.capture","clip.capture","language-practice.select:meaning-reading","language-practice.select:writing","language-practice.select:both","mode.context.set","settings.page.select:export","settings.export.anki.deck-preset.select:custom","settings.export.anki.deck-routes.edit","settings.save","flashcard-preview.cards.observe","flashcard-preview.front.observe","flashcard-preview.back.observe","flashcard-preview.confirm","card-resolution.accept-candidate"};
        var ids=new[]{"read","clip","meaning","writing","both","context","export","preset","routes","save","cards","front","back","confirm","accept"};
        var binding=new DesktopAutomationBinding("root",keys.Zip(ids).ToDictionary(x=>x.First,x=>x.Second),Array.Empty<object>());
        var checks=0;
        using(var ports=new SequenceFixturePorts(plan))
        {
            new FlowRunner(ports,ports,ports,_=>{}).Run("fixture.exe","isolated","零碎知识剪藏::Canary::SequenceOffline",binding,"queue-fixture.json",plan);
            if(ports.Starts!=1||ports.Captures!=4||ports.Confirms!=4||ports.FinalChecks!=1||ports.Calls.Count(c=>c=="invoke:context")!=1||ports.Calls.Count(c=>c.StartsWith("ui-ready:"))!=4)throw new Exception("sequence-single-process-order-failed");
            checks++;
        }
        foreach(var failure in new[]{"unknown","duplicate","source"})
        {
            using var ports=new SequenceFixturePorts(plan,failAt:failure=="unknown"?1:-1,duplicateOperation:failure=="duplicate",wrongSource:failure=="source");
            var rejected=false;
            try{new FlowRunner(ports,ports,ports,_=>{}).Run("fixture.exe","isolated","零碎知识剪藏::Canary::SequenceOffline",binding,"queue-fixture.json",plan);}
            catch(InvalidOperationException){rejected=true;}
            if(!rejected||ports.Starts!=1||ports.Captures>2||ports.Confirms>1)throw new Exception("sequence-failure-did-not-stop");checks++;
        }
        foreach(var mode in new[]{"","invalid","BOTH"}){try{new FlowStep("read.capture",mode).Validate();throw new Exception("invalid-mode-accepted");}catch(InvalidOperationException){checks++;}}
        foreach(var invalid in new[]{JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion="desktop-test-capture-queue.v1",inputs=inputs.Take(3)}),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion="desktop-test-capture-queue.v1",inputs,result="injected"})})
        {try{SequencePlan.Parse(invalid);throw new Exception("invalid-queue-accepted");}catch(InvalidOperationException){checks++;}}
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",sequenceCases=checks,singleProcess=true,captureCounts=new[]{1,1,1,1},cardCounts=new[]{1,1,2,2},speechExpected=new[]{true,true,false,true},contextToggleCount=1,ownUiRestoreBeforeNextCapture=true,stopOnFailure=true,finalReadonlyNoAdditionalAction=true,externalCalls=0,businessTasks=0}));
        SequenceWorkerLedger.RequireCounts(true,1,1,1);SequenceWorkerLedger.RequireCounts(false,0,0,1);
        foreach(var counts in new[]{(false,1,1,1),(true,2,1,1),(true,1,0,1),(true,1,1,2)})
        {try{SequenceWorkerLedger.RequireCounts(counts.Item1,counts.Item2,counts.Item3,counts.Item4);throw new Exception("invalid-worker-count-accepted");}catch(InvalidOperationException){}}
        return checks;
    }
}

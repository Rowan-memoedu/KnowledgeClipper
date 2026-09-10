using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal record Step(string Name, string AutomationId, string Operation);
internal record AnkiReadback(IReadOnlyList<long> NoteIds, int CardCount, string Deck,IReadOnlyList<RunnerSemanticCard>? Cards=null);
internal record PreviewCard(int Ordinal, string Front, string Back,string? Template=null);
internal sealed record RunnerSemanticCard(long NoteId,IReadOnlyList<long> CardIds,string Front,string Back,string Deck);
internal sealed record RunnerContextSegment(int Order,string Role,string ArtifactRef,string ArtifactSha256);
internal sealed record RunnerContextEvidence(string ContextSnapshotRef,string ContextSnapshotSha256,string CaptureRef,string CaptureSha256,string SelectedTextSha256,string TargetTextSha256,string SpokenTextSha256,string SpeechContextMode,bool ContextRequested,bool ContextApplied,string ContextStatus,string ContextGranularity,IReadOnlyList<RunnerContextSegment> Segments);
internal sealed record RunnerProjection(string OperationId,string Pipeline,string Playback,string? ActiveCommandId,string? TerminalCommandId,long TerminalSequence,long Cursor,bool HasLanguageChoice,bool HasCardResolution,string? DraftSha256,IReadOnlyList<PreviewCard> DraftCards,IReadOnlyList<RunnerSemanticCard> SemanticCards,string SnapshotRef,string SnapshotSha256,string ConfigRef,string ConfigSha256,string RouteKey,string DeckIdentity,string DeckSha256,bool HasSynthesisArtifact,bool HasPlaybackArtifact,RunnerContextEvidence? ContextEvidence=null)
{ public string? DraftRevision {get;init;} }
internal sealed record ActionCheckpoint(string OperationId,string SemanticAction,long Cursor,string? ActiveCommandId,string? TerminalCommandId,long TerminalSequence,object? NativeCheckpoint=null);
internal sealed record ActionTerminal(string OperationId,string CommandId,long AcceptedCursor,RunnerProjection Projection);
internal sealed record TopLevelWindowCandidate(nint Handle, int OwnerProcessId, bool IsVisible, int UiaProcessId, string AutomationId, string ControlType, int NativeWindowHandle);
internal sealed record ChildWindowCandidate(string TopLevelAutomationId, int ProcessId, string AutomationId, nint NativeWindowHandle, string? RuntimeId = null);
internal static class ExactTopLevelWindowSelector
{
    internal const string WindowControlType = "ControlType.Window";
    public static TopLevelWindowCandidate Select(IEnumerable<TopLevelWindowCandidate> candidates, int expectedProcessId, string expectedAutomationId)
    {
        var matches=candidates.Where(x=>x.OwnerProcessId==expectedProcessId&&x.IsVisible&&x.UiaProcessId==expectedProcessId&&string.Equals(x.AutomationId,expectedAutomationId,StringComparison.Ordinal)&&string.Equals(x.ControlType,WindowControlType,StringComparison.Ordinal)&&x.NativeWindowHandle==unchecked((int)x.Handle)).ToArray();
        if(matches.Length==0)throw new InvalidOperationException("candidate.root-not-found");
        if(matches.Length!=1)throw new InvalidOperationException("candidate.root-ambiguous");
        return matches[0];
    }
}
internal static class ExactChildWindowSelector
{
    public static string Identity(ChildWindowCandidate candidate)
    {
        if(candidate.NativeWindowHandle!=0)return $"{candidate.ProcessId}:hwnd:{candidate.NativeWindowHandle}";
        if(!string.IsNullOrWhiteSpace(candidate.RuntimeId))return $"{candidate.ProcessId}:runtime:{candidate.RuntimeId}";
        throw new InvalidOperationException("uia.element-identity-unavailable:"+candidate.AutomationId);
    }
    public static ChildWindowCandidate Select(IEnumerable<ChildWindowCandidate> candidates,int expectedProcessId,string expectedAutomationId)
    {
        var identities=new Dictionary<string,ChildWindowCandidate>(StringComparer.Ordinal);
        foreach(var candidate in candidates.Where(x=>x.ProcessId==expectedProcessId&&string.Equals(x.AutomationId,expectedAutomationId,StringComparison.Ordinal)))
            identities.TryAdd(Identity(candidate),candidate);
        var matches=identities.Values.ToArray();
        if(matches.Length==0)throw new InvalidOperationException("uia.element-not-found:"+expectedAutomationId);
        if(matches.Length!=1)throw new InvalidOperationException("uia.element-ambiguous:"+expectedAutomationId);
        return matches[0];
    }
}
internal static class ChildWindowObservation
{
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint handle,out uint processId);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(nint handle);
    internal static ChildWindowCandidate Read(string rootId,AutomationElement element)
    {
        var current=element.Current;var handle=new nint(current.NativeWindowHandle);
        if(handle!=0&&(!IsWindow(handle)||GetWindowThreadProcessId(handle,out var owner)==0||owner!=(uint)current.ProcessId))handle=0;
        var runtime=handle==0?element.GetRuntimeId():null;
        return new(rootId,current.ProcessId,current.AutomationId,handle,runtime is {Length:>0}?string.Join(",",runtime):null);
    }
}
internal sealed class HeartbeatJournal(Action<object> append)
{
    long sequence;
    public void Beat(string stage)
    {
        var heartbeat=new {schemaVersion="knowledge-clipper-actual-runner-heartbeat.v1",sequence=Interlocked.Increment(ref sequence),stage,atUtc=DateTimeOffset.UtcNow};
        append(heartbeat);Console.WriteLine(JsonSerializer.Serialize(heartbeat));
    }
}
internal interface ICandidateSurface : IDisposable
{
    string Identity { get; }
    void StartOwned(string executable, string argument, string isolatedDataRoot, string? captureFixture = null);
    void AttachExact(string executable, string rootAutomationId);
    void CreateExternalTextPatternSource(string text, int start, int length);
    void Invoke(string automationId);
    void SetValue(string automationId, string value);
    void SelectItem(string automationId, string value);
    void AssertValue(string automationId, string value);
    SavedAnkiDestinationEvidence VerifySavedDestination(string dataRoot,string deck);
    IReadOnlyList<PreviewCard> ReadPreviewCards(string listAutomationId,string frontAutomationId,string backAutomationId,RenderedPreviewIdentity expected,IReadOnlyList<PreviewCard> expectedCards);
    UiTerminalCheckpoint BeginUiTerminalObservation(string dataRoot);
    UiTerminalEvidence WaitUiTerminal(UiTerminalCheckpoint checkpoint,string operationId,bool includeSpeech=true);
}
internal interface ITaskObserver : IDisposable { string Identity { get; } object Begin(string marker); AcceptedOperationIdentity Discover(object checkpoint); RunnerProjection WaitInitial(AcceptedOperationIdentity identity); ActionCheckpoint BeginAction(string operationId,string semanticAction); ActionTerminal WaitAction(ActionCheckpoint checkpoint); RunnerProjection WaitAggregate(string operationId,string finalCommandId,int expectedCardCount=2,bool includeSpeech=true); void AssertNoAdditionalOperation(object checkpoint,AcceptedOperationIdentity identity,string finalCommandId); }
internal interface IAnkiReadback : IDisposable { string Identity { get; } IReadOnlySet<long> CaptureBaseline(string deck); AnkiReadback ReadNew(string deck,IReadOnlySet<long> baseline); }

internal sealed class FlowRunner(ICandidateSurface surface, ITaskObserver observer, IAnkiReadback anki, Action<object> append)
{
    public IReadOnlyList<CompletedFlowStep> Run(string executable, string dataRoot, string deck, DesktopAutomationBinding binding, string? captureFixture = null,IReadOnlyList<FlowStep>? sequence=null)
    {
        var steps=sequence??new[]{new FlowStep("read.capture","both")};
        Require(steps.Count>0,"sequence.empty");foreach(var step in steps){step.Validate();_=binding.Require(step.CaptureAction);_=binding.Require("language-practice.select:"+step.LanguageMode);}
        Event("pending-receipt-required-before-start");
        surface.StartOwned(executable, "--open-settings", dataRoot, captureFixture);
        surface.AttachExact(executable, binding.FloatingRoot);
        surface.SelectItem(binding.Require("settings.page.select:export"), "导出");
        surface.SelectItem(binding.Require("settings.export.anki.deck-preset.select:custom"), "custom");
        var routes=JsonSerializer.Serialize(new Dictionary<string,string>{{"canary",deck}});
        surface.SetValue(binding.Require("settings.export.anki.deck-routes.edit"), routes);
        surface.AssertValue(binding.Require("settings.export.anki.deck-routes.edit"), routes);
        surface.Invoke(binding.Require("settings.save"));
        var savedDestination=surface.VerifySavedDestination(dataRoot,deck);
        append(new{atUtc=DateTimeOffset.UtcNow,state="settings-exact-destination-before-capture",evidence=savedDestination,source="actual-saved-desktop-settings-read-only"});
        surface.Invoke(binding.Require("mode.context.set"));
        var operations=new HashSet<string>(StringComparer.Ordinal);var writtenNotes=new HashSet<long>();var completedSteps=new List<CompletedFlowStep>();
        for(var stepIndex=0;stepIndex<steps.Count;stepIndex++)
        {
        var step=steps[stepIndex];var expectedCount=step.CardCount;
        var baseline=anki.CaptureBaseline(deck); Event("anki-readonly-baseline");
        append(new{atUtc=DateTimeOffset.UtcNow,state="sequence-action-start",ordinal=stepIndex,step.CaptureAction,step.LanguageMode,expectedCardCount=expectedCount,sourceSelectedSha256=step.Source?.SelectedSha256,sourceCompleteSha256=step.Source?.CompleteSha256});
        if(captureFixture is null) surface.CreateExternalTextPatternSource("A deterministic external sentence with deliberate context.", 39, 10);
        else Event("deterministic-capture-fixture-external-textpattern-skipped");
        var checkpoint=observer.Begin("actual-ui-"+Guid.NewGuid().ToString("N"));
        surface.Invoke(binding.Require(step.CaptureAction));
        var identity=observer.Discover(checkpoint);
        Require(operations.Add(identity.OperationId),"sequence.operation-reused");
        var projected=observer.WaitInitial(identity);RecordAction(step.CaptureAction,identity.OperationId,identity.CommandId,projected);
        if(sequence is not null)Require(projected.Pipeline=="WaitingLanguageCardChoice","sequence.recognition-did-not-require-mode-choice");
        if(projected.Pipeline=="WaitingLanguageCardChoice") projected=InvokeAndWait("language-practice.select:"+step.LanguageMode,projected.OperationId);
        if(projected.HasCardResolution||projected.Pipeline=="AwaitingCardResolution") projected=InvokeAndWait("card-resolution.accept-candidate",projected.OperationId);
        Require(!projected.HasCardResolution&&projected.Pipeline=="AwaitingCardConfirmation","preview.not-ready-after-required-decisions");
        var expectedPreviewIdentity=new RenderedPreviewIdentity(projected.OperationId,projected.DraftSha256??"",projected.DraftRevision??"");
        var preview=surface.ReadPreviewCards(binding.Require("flashcard-preview.cards.observe"),binding.Require("flashcard-preview.front.observe"),binding.Require("flashcard-preview.back.observe"),expectedPreviewIdentity,projected.DraftCards);
        Require(preview.Count==expectedCount&&preview.All(card=>!string.IsNullOrWhiteSpace(card.Front)&&!string.IsNullOrWhiteSpace(card.Back)),"preview.card-count-or-content-invalid");
        Require(projected.DraftCards.Count==expectedCount&&SemanticEqual(preview,projected.DraftCards),"preview.content-does-not-match-typed-draft");
        if(sequence is not null)Require(projected.DraftCards.Select(c=>c.Template).SequenceEqual(step.LanguageMode switch{"meaning-reading"=>new[]{"language_context"},"writing"=>new[]{"language_writing"},_=>new[]{"language_context","language_writing"}}),"sequence.language-template-mismatch-before-confirm");
        Require(projected.SnapshotSha256.Length==64,"observer.snapshot-sha-missing-before-confirm");
        Require(projected.ConfigSha256.Length==64&&projected.RouteKey=="canary"&&projected.DeckIdentity==deck&&projected.DeckSha256.Length==64,"observer.settings-evidence-invalid-before-confirm");
        if(step.Source is not null)Require(projected.ContextEvidence is {ContextRequested:true,ContextApplied:true} sourceContext&&string.Equals(sourceContext.SelectedTextSha256,step.Source.SelectedSha256,StringComparison.OrdinalIgnoreCase)&&string.Equals(sourceContext.SpokenTextSha256,step.Source.CompleteSha256,StringComparison.OrdinalIgnoreCase),"sequence.capture-source-sha-mismatch-before-confirm");
        var previewEvidence=preview.Select(card=>new{card.Ordinal,frontSha256=Sha(card.Front),backSha256=Sha(card.Back)}).ToArray();
        append(new { atUtc=DateTimeOffset.UtcNow,state=expectedCount==2?"preview-exact-two-before-confirm":"preview-exact-one-before-confirm",operationId=projected.OperationId,draftSha256=projected.DraftSha256,cardCount=preview.Count,cards=previewEvidence,source="external-black-box" });
        var uiCheckpoint=surface.BeginUiTerminalObservation(dataRoot);
        projected=InvokeAndWait("flashcard-preview.confirm",projected.OperationId);
        Require(projected.Pipeline=="Completed","confirm.did-not-reach-completed");
        Require(!string.IsNullOrWhiteSpace(projected.TerminalCommandId),"confirm.terminal-command-missing");
        projected=observer.WaitAggregate(projected.OperationId,projected.TerminalCommandId!,expectedCount,step.IncludeSpeech);
        Require(projected.SnapshotSha256.Length==64,"observer.snapshot-sha-missing");
        Require(projected.ConfigSha256.Length==64&&projected.RouteKey=="canary"&&projected.DeckIdentity==deck&&projected.DeckSha256.Length==64,"observer.settings-evidence-invalid");
        var context=projected.ContextEvidence??throw new InvalidOperationException("observer.context-evidence-missing");
        Require(context.ContextRequested&&context.ContextApplied&&context.SelectedTextSha256.Length==64&&context.TargetTextSha256.Length==64&&context.SpokenTextSha256.Length==64,"observer.context-evidence-invalid");
        Require(context.Segments.Select(x=>x.Role).SequenceEqual(new[]{"prefix","suffix"})&&context.Segments.Select(x=>x.Order).SequenceEqual(new[]{0,1})&&context.Segments.All(x=>x.ArtifactSha256.Length==64),"observer.context-role-order-invalid");
        if(step.Source is not null)Require(string.Equals(context.SelectedTextSha256,step.Source.SelectedSha256,StringComparison.OrdinalIgnoreCase)&&string.Equals(context.SpokenTextSha256,step.Source.CompleteSha256,StringComparison.OrdinalIgnoreCase),"sequence.capture-source-sha-mismatch");
        Require(SpeechComplete(projected,step.IncludeSpeech),"speech.natural-completion-or-not-requested-not-proven");
        Require(projected.SemanticCards.Count==expectedCount&&SemanticEqual(preview,projected.SemanticCards.Select((card,index)=>new PreviewCard(index,card.Front,card.Back)).ToArray())&&projected.SemanticCards.All(card=>card.Deck==deck&&card.CardIds.Count>0),"anki.semantic-readback-mismatch");
        var readback=anki.ReadNew(deck,baseline); Require(readback.NoteIds.Count==expectedCount&&readback.CardCount==expectedCount&&readback.Deck==deck,"anki.readback-count-or-deck-mismatch");
        Require(readback.NoteIds.Order().SequenceEqual(projected.SemanticCards.Select(card=>card.NoteId).Order()),"anki.semantic-note-ids-do-not-match-live-readback");
        if(readback.Cards is not null)Require(readback.Cards.Count==expectedCount&&projected.SemanticCards.All(expected=>readback.Cards.Any(actual=>actual.NoteId==expected.NoteId&&actual.CardIds.Order().SequenceEqual(expected.CardIds.Order())&&actual.Front==expected.Front&&actual.Back==expected.Back&&actual.Deck==deck)),"anki.independent-notes-cards-readback-mismatch");
        Require(readback.NoteIds.All(writtenNotes.Add),"sequence.note-id-reused");
        append(new { atUtc=DateTimeOffset.UtcNow,state="final-command-preview-semantic-readback",operationId=projected.OperationId,finalCommandId=projected.TerminalCommandId,terminalSequence=projected.TerminalSequence,draftSha256=projected.DraftSha256,preview=previewEvidence,noteCount=readback.NoteIds.Count,cardCount=readback.CardCount,noteIds=readback.NoteIds,independentNotesInfoCardsInfo=readback.Cards is not null,semanticCards=projected.SemanticCards.Select((card,index)=>new{ordinal=index,card.NoteId,cardIds=card.CardIds,frontSha256=Sha(card.Front),backSha256=Sha(card.Back),card.Deck}),source="anki-module-semantic-readback+anki-read-only" });
        var uiTerminal=surface.WaitUiTerminal(uiCheckpoint,projected.OperationId,step.IncludeSpeech);
        append(new { atUtc=DateTimeOffset.UtcNow,state="desktop-rendered-terminal-proven",operationId=projected.OperationId,finalCommandId=projected.TerminalCommandId,terminalSequence=projected.TerminalSequence,evidence=uiTerminal,source="same-run-desktop-ui-log-after-set-status" });
        append(new{atUtc=DateTimeOffset.UtcNow,state="sequence-action-completed",ordinal=stepIndex,operationId=projected.OperationId,step.CaptureAction,step.LanguageMode,expectedCardCount=expectedCount});
        completedSteps.Add(new(projected.OperationId,step.IncludeSpeech,expectedCount));
        if(sequence is not null&&stepIndex==steps.Count-1){observer.AssertNoAdditionalOperation(checkpoint,identity,projected.TerminalCommandId!);append(new{atUtc=DateTimeOffset.UtcNow,state="sequence-final-readonly-no-additional-operation",operationId=projected.OperationId,finalCommandId=projected.TerminalCommandId,additionalUiInvocations=0});}
        }
        Event("completed");
        return completedSteps;

        RunnerProjection InvokeAndWait(string semanticAction,string operationId){var action=observer.BeginAction(operationId,semanticAction);surface.Invoke(binding.Require(semanticAction));var terminal=observer.WaitAction(action);RecordAction(semanticAction,terminal.OperationId,terminal.CommandId,terminal.Projection);return terminal.Projection;}
        void RecordAction(string semanticAction,string operationId,string commandId,RunnerProjection terminal)=>append(new{atUtc=DateTimeOffset.UtcNow,state="ui-action-own-terminal",semanticAction,operationId,commandId,terminalSequence=terminal.TerminalSequence,pipeline=terminal.Pipeline,source="typed-core-projection"});
    }
    void Event(string state) => append(new { atUtc=DateTimeOffset.UtcNow, state, source="external-black-box" });
    internal static bool SpeechComplete(RunnerProjection value,bool requested)=>requested?value.Playback=="IdleReplayable"&&value.HasSynthesisArtifact&&value.HasPlaybackArtifact:value.Playback=="NotRequested"&&!value.HasSynthesisArtifact&&!value.HasPlaybackArtifact;
    static string Sha(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static bool SemanticEqual(IReadOnlyList<PreviewCard> left,IReadOnlyList<PreviewCard> right)=>left.Count==right.Count&&left.Select((card,index)=>card.Ordinal==index&&right[index].Ordinal==index&&Sha(card.Front)==Sha(right[index].Front)&&Sha(card.Back)==Sha(right[index].Back)).All(value=>value);
    static void Require(bool value,string code){if(!value)throw new InvalidOperationException(code);}
}

internal sealed class FakeSurface(int previewCount=2,bool missingUiTerminal=false,bool settingsPersisted=true) : ICandidateSurface
{
    public string Identity => "FakeOsUiaSurface"; public List<string> Calls { get; }=[];
    public string[] LaunchArguments { get; private set; } = Array.Empty<string>();
    public UiTerminalCheckpoint BeginUiTerminalObservation(string dataRoot){Calls.Add("ui-checkpoint");return new("fake-ui-log",0,DateTimeOffset.UtcNow);}
    public UiTerminalEvidence WaitUiTerminal(UiTerminalCheckpoint checkpoint,string operationId,bool includeSpeech=true){Calls.Add("ui-terminal");if(missingUiTerminal)throw new InvalidOperationException("desktop-ui-rendered-terminal-timeout");return new(checkpoint.LogPath,operationId,checkpoint.Offset,0,DateTimeOffset.UtcNow,checkpoint.NotBeforeUtc,new('a',64));}
    public void StartOwned(string a,string b,string c,string? captureFixture=null){LaunchArguments=CandidateLaunchArguments.Build(b,captureFixture);Calls.Add("start");} public void AttachExact(string a,string b)=>Calls.Add("attach");
    public void CreateExternalTextPatternSource(string a,int b,int c)=>Calls.Add("source"); public void Invoke(string id)=>Calls.Add("invoke:"+id);
    string? preset;string? routes;
    public void SetValue(string id,string value){Calls.Add("value:"+id);if(id=="deck-routes")routes=value;} public void SelectItem(string id,string value){Calls.Add("select:"+id);if(id=="deck-preset")preset=value;}
    public SavedAnkiDestinationEvidence VerifySavedDestination(string dataRoot,string deck){Calls.Add("verify-settings");if(!settingsPersisted)throw new InvalidOperationException("settings.saved-anki-destination-mismatch-before-capture");return AnkiSettingsEvidence.Validate(JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion="knowledge-clipper-desktop-module-settings.v1",ankiDeckPreset=preset,ankiDeckRoutes=JsonSerializer.Deserialize<Dictionary<string,string>>(routes??"{}")}),"fake-settings",deck);}
    public void AssertValue(string id,string value)=>Calls.Add("assert:"+id); public void Dispose()=>Calls.Add("dispose");
    public IReadOnlyList<PreviewCard> ReadPreviewCards(string list,string front,string back,RenderedPreviewIdentity expected,IReadOnlyList<PreviewCard> expectedCards){Calls.Add("preview:"+list+":"+front+":"+back);return Enumerable.Range(0,previewCount).Select(i=>new PreviewCard(i,"front-"+i,"back-"+i)).ToArray();}
}
internal sealed class FakeObserver(bool hasResolution=false,bool wrongSemanticContent=false,bool hasLanguageChoice=true,bool delayedPlayback=false,bool wrongDestination=false) : ITaskObserver
{
    public void AssertNoAdditionalOperation(object checkpoint,AcceptedOperationIdentity identity,string finalCommandId) { }
    int action;public int AggregateWaits{get;private set;} public string Identity=>"FakeTypedPipeObserver";public object Begin(string marker)=>new{marker,cursor=1L};public AcceptedOperationIdentity Discover(object _)=>new("op","capture-command",1);
    public RunnerProjection WaitInitial(AcceptedOperationIdentity identity)=>hasLanguageChoice?Projection("WaitingLanguageCardChoice",identity.CommandId,1,language:true):Projection(hasResolution?"AwaitingCardResolution":"AwaitingCardConfirmation",identity.CommandId,1,draft:!hasResolution);
    public ActionCheckpoint BeginAction(string operationId,string semanticAction)=>new(operationId,semanticAction,action+2,null,action==0?"capture-command":"action-"+action,action+1);
    public ActionTerminal WaitAction(ActionCheckpoint checkpoint){action++;var pipeline=checkpoint.SemanticAction switch{"language-practice.select:both" when hasResolution=>"AwaitingCardResolution","language-practice.select:both"=>"AwaitingCardConfirmation","card-resolution.accept-candidate"=>"AwaitingCardConfirmation","flashcard-preview.confirm"=>"Completed",_=>throw new InvalidOperationException("fake.unexpected-action")};var command="action-"+action;return new("op",command,checkpoint.Cursor+1,Projection(pipeline,command,checkpoint.TerminalSequence+1,draft:pipeline is "AwaitingCardConfirmation" or "Completed",completed:pipeline=="Completed",playbackReady:pipeline=="Completed"&&!delayedPlayback));}
    public RunnerProjection WaitAggregate(string operationId,string finalCommandId,int expectedCardCount=2,bool includeSpeech=true){AggregateWaits++;return Projection("Completed",finalCommandId,action+20,draft:true,completed:true,playbackReady:true);}
    RunnerProjection Projection(string pipeline,string command,long sequence,bool language=false,bool draft=false,bool completed=false,bool playbackReady=false){var draftCards=draft?new[]{new PreviewCard(0,"front-0","back-0"),new PreviewCard(1,"front-1","back-1")}:Array.Empty<PreviewCard>();var semantic=completed?draftCards.Select((card,index)=>new RunnerSemanticCard(index+1,new long[]{index+11},wrongSemanticContent&&index==1?"WRONG":card.Front,card.Back,"零碎知识剪藏::Canary::Desktop-V14")).ToArray():Array.Empty<RunnerSemanticCard>();var context=new RunnerContextEvidence("artifacts/context/artifact.json",new('e',64),"artifacts/capture/artifact.json",new('f',64),new('1',64),new('2',64),new('3',64),"contextual",true,true,"applied","sentence",new[]{new RunnerContextSegment(0,"prefix","artifacts/prefix/artifact.json",new('4',64)),new RunnerContextSegment(1,"suffix","artifacts/suffix/artifact.json",new('5',64))});return new("op",pipeline,playbackReady?"IdleReplayable":completed?"Playing":"Idle",null,command,sequence,sequence+10,language,hasResolution&&pipeline=="AwaitingCardResolution",draft?new('d',64):null,draftCards,semantic,"artifacts/task-settings-snapshot/artifact.json",new('a',64),"artifacts/anki-export-config/artifact.json",new('b',64),"canary",wrongDestination?"WRONG-DECK":"零碎知识剪藏::Canary::Desktop-V14",new('c',64),completed,playbackReady,context);}
    public void Dispose(){}
}
internal sealed class FakeAnki(int count=2) : IAnkiReadback { public int ReadCalls {get;private set;} public string Identity=>"FakeAnkiReadback"; public IReadOnlySet<long> CaptureBaseline(string _)=>new HashSet<long>(); public AnkiReadback ReadNew(string deck,IReadOnlySet<long> _){ReadCalls++;return new(Enumerable.Range(1,count).Select(i=>(long)i).ToArray(),count,deck);} public void Dispose(){} }

internal sealed class OwnedCandidateProcessPort(HeartbeatJournal heartbeat) : ICandidateSurface
{
    delegate bool EnumWindowsProc(nint hwnd,nint lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback,nint lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(nint hwnd);
    Process? process; AutomationElement? root; ExternalTextSource? source; public string Identity=>"OwnedCandidateProcess+ExternalWindowsUia";
    public void StartOwned(string executable,string argument,string isolatedDataRoot,string? captureFixture=null)
    {
        heartbeat.Beat("candidate.start.before");
        var expected=Path.GetFullPath(executable); var info=new ProcessStartInfo(expected){UseShellExecute=false}; foreach(var item in CandidateLaunchArguments.Build(argument,captureFixture))info.ArgumentList.Add(item); info.Environment["LOCALAPPDATA"]=Path.GetFullPath(isolatedDataRoot);
        process=Process.Start(info)??throw new InvalidOperationException("candidate.start-failed");
        if(!string.Equals(process.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("candidate.executable-drift");
        heartbeat.Beat("candidate.start.after");
    }
    public void AttachExact(string executable,string rootAutomationId)
    {
        heartbeat.Beat("root.lookup.before");
        if(process is null)throw new InvalidOperationException("candidate.not-started"); var deadline=DateTime.UtcNow.AddSeconds(30);
        do { Application.DoEvents(); var elements=new Dictionary<nint,AutomationElement>();var candidates=new List<TopLevelWindowCandidate>();EnumWindows((handle,_)=>{GetWindowThreadProcessId(handle,out var owner);if(owner!=(uint)process.Id||!IsWindowVisible(handle))return true;try{var element=AutomationElement.FromHandle(handle);elements[handle]=element;candidates.Add(new(handle,(int)owner,true,element.Current.ProcessId,element.Current.AutomationId,element.Current.ControlType.ProgrammaticName,element.Current.NativeWindowHandle));}catch(ElementNotAvailableException){}catch(InvalidOperationException){}return true;},0);try{var selected=ExactTopLevelWindowSelector.Select(candidates,process.Id,rootAutomationId);root=elements[selected.Handle];heartbeat.Beat("root.lookup.after");return;}catch(InvalidOperationException ex)when(ex.Message=="candidate.root-not-found"){}Thread.Sleep(100); } while(DateTime.UtcNow<deadline);
        throw new InvalidOperationException("candidate.root-not-found");
    }
    public void CreateExternalTextPatternSource(string text,int start,int length)
    {
        heartbeat.Beat("external-source.create.before");
        source=new ExternalTextSource(text,start,length);
        heartbeat.Beat("external-source.create.after");
    }
    public UiTerminalCheckpoint BeginUiTerminalObservation(string dataRoot)=>UiTerminalLog.Begin(dataRoot);
    public UiTerminalEvidence WaitUiTerminal(UiTerminalCheckpoint checkpoint,string operationId,bool includeSpeech=true)=>UiTerminalLog.Wait(checkpoint,operationId,TimeSpan.FromSeconds(10),()=>heartbeat.Beat("ui.rendered-terminal.wait.poll"),includeSpeech);
    IReadOnlyList<(AutomationElement Root,TopLevelWindowCandidate Candidate)> StrictTopLevelWindows()
    {
        if(process is null)throw new InvalidOperationException("candidate.not-started");var result=new List<(AutomationElement,TopLevelWindowCandidate)>();EnumWindows((handle,_)=>{GetWindowThreadProcessId(handle,out var owner);if(owner!=(uint)process.Id||!IsWindowVisible(handle))return true;try{var element=AutomationElement.FromHandle(handle);var candidate=new TopLevelWindowCandidate(handle,(int)owner,true,element.Current.ProcessId,element.Current.AutomationId,element.Current.ControlType.ProgrammaticName,element.Current.NativeWindowHandle);if(candidate.UiaProcessId==process.Id&&candidate.ControlType==ExactTopLevelWindowSelector.WindowControlType&&candidate.NativeWindowHandle==unchecked((int)handle))result.Add((element,candidate));}catch(ElementNotAvailableException){}catch(InvalidOperationException){}return true;},0);return result;
    }
    AutomationElement Find(string id,TimeSpan? timeout=null)
    {
        if(process is null)throw new InvalidOperationException("candidate.not-started");
        heartbeat.Beat("control.lookup.before");var deadline=DateTime.UtcNow+(timeout??TimeSpan.FromSeconds(45));
        do
        {
            Application.DoEvents();
            var elements=new Dictionary<string,AutomationElement>(StringComparer.Ordinal);
            var candidates=new List<ChildWindowCandidate>();
            foreach(var window in StrictTopLevelWindows())
            {
                var condition=new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,process.Id),new PropertyCondition(AutomationElement.AutomationIdProperty,id));
                var matches=window.Root.FindAll(TreeScope.Descendants,condition);
                for(var i=0;i<matches.Count;i++)
                {
                    var candidate=ChildWindowObservation.Read(window.Candidate.AutomationId,matches[i]);
                    candidates.Add(candidate);
                    elements.TryAdd(ExactChildWindowSelector.Identity(candidate),matches[i]);
                }
            }
            try
            {
                var selected=ExactChildWindowSelector.Select(candidates,process.Id,id);
                heartbeat.Beat("control.lookup.after");
                return elements[ExactChildWindowSelector.Identity(selected)];
            }
            catch(InvalidOperationException ex)when(ex.Message.StartsWith("uia.element-not-found:",StringComparison.Ordinal)){}
            Thread.Sleep(100);
        }while(DateTime.UtcNow<deadline);
        throw new InvalidOperationException("uia.element-not-found:"+id);
    }
    public void Invoke(string automationId){heartbeat.Beat("control.invoke.before");var element=Find(automationId);if(!element.TryGetCurrentPattern(InvokePattern.Pattern,out var value))throw new InvalidOperationException("uia.invoke-pattern-missing:"+automationId);((InvokePattern)value).Invoke();heartbeat.Beat("control.invoke.after");}
    public void SetValue(string automationId,string value){heartbeat.Beat("control.set-value.before");var element=Find(automationId);if(!element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern))throw new InvalidOperationException("uia.value-pattern-missing:"+automationId);((ValuePattern)pattern).SetValue(value);heartbeat.Beat("control.set-value.after");}
    public void SelectItem(string automationId,string value){heartbeat.Beat("control.select.before");var element=Find(automationId);if(element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out var expand)&&((ExpandCollapsePattern)expand).Current.ExpandCollapseState==ExpandCollapseState.Collapsed)((ExpandCollapsePattern)expand).Expand();var choice=element.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,value))??throw new InvalidOperationException("uia.choice-not-found:"+value);if(!choice.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var pattern))throw new InvalidOperationException("uia.select-pattern-missing:"+automationId);((SelectionItemPattern)pattern).Select();heartbeat.Beat("control.select.after");}
    public void AssertValue(string automationId,string value){heartbeat.Beat("control.assert.before");var element=Find(automationId);if(!element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)||((ValuePattern)pattern).Current.Value!=value)throw new InvalidOperationException("uia.value-mismatch:"+automationId);heartbeat.Beat("control.assert.after");}
    internal AutomationElement FindQuestionControl(string id)=>Find(id,TimeSpan.FromSeconds(2));
#if PLAYBACK_REGRESSION
    internal PlaybackStatusObservation ReadOwnedPlaybackStatus()
    {
        if(process is null)throw new InvalidOperationException("candidate.not-started");
        return PlaybackStatusReader.Read(Find(PlaybackStatusReader.StatusId,TimeSpan.FromSeconds(2)),process.Id);
    }
    [DllImport("user32.dll",SetLastError=true)] static extern bool PostMessage(nint hwnd,uint msg,nint wparam,nint lparam);
    internal void OpenPlaybackContextMenu()
    {
        if(root is null)throw new InvalidOperationException("playback.root-unattached");
        var hwnd=new nint(root.Current.NativeWindowHandle);
        if(!PostMessage(hwnd,0x007B,hwnd,new nint(-1)))throw new InvalidOperationException("playback.context-menu-dispatch-failed");
    }
    internal (AutomationElement? Element,PlaybackMenuObservation? Observation) FindOwnedPlaybackMenu(string exactName)
    {
        if(process is null)throw new InvalidOperationException("candidate.not-started");
        var matches=new Dictionary<string,AutomationElement>(StringComparer.Ordinal);
        var observations=new List<PlaybackMenuObservation>();
        EnumWindows((hwnd,unused)=>{
            GetWindowThreadProcessId(hwnd,out var owner);if(owner!=(uint)process.Id||!IsWindowVisible(hwnd))return true;
            try {
                var menuRoot=AutomationElement.FromHandle(hwnd);
                var rootState=menuRoot.Current;
                if(rootState.ProcessId!=process.Id||rootState.ControlType!=ControlType.Menu||rootState.IsOffscreen)return true;
                var nodes=menuRoot.FindAll(TreeScope.Descendants,new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty,process.Id),
                    new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.NameProperty,exactName)));
                for(var i=0;i<nodes.Count;i++){
                    var node=nodes[i];var state=node.Current;
                    var identity=ExactChildWindowSelector.Identity(ChildWindowObservation.Read(rootState.AutomationId,node));
                    observations.Add(new(identity,state.ProcessId,rootState.ProcessId,true,true,state.ControlType==ControlType.MenuItem,
                        !state.IsOffscreen,state.Name,state.IsEnabled,node.TryGetCurrentPattern(InvokePattern.Pattern,out _)));
                    matches.TryAdd(identity,node);
                }
            } catch(ElementNotAvailableException) { }
            return true;
        },0);
        var selected=PlaybackMenuSelector.Select(observations,process.Id,exactName);
        return (selected is null?null:matches[selected.Identity],selected);
    }
#endif
    public SavedAnkiDestinationEvidence VerifySavedDestination(string dataRoot,string deck)=>AnkiSettingsEvidence.Wait(dataRoot,deck,()=>heartbeat.Beat("settings.saved-destination.read"));
    public IReadOnlyList<PreviewCard> ReadPreviewCards(string listAutomationId,string frontAutomationId,string backAutomationId,RenderedPreviewIdentity expected,IReadOnlyList<PreviewCard> expectedCards)
    {
        heartbeat.Beat("preview.read.before");
        var clock=Stopwatch.StartNew();var budget=TimeSpan.FromSeconds(45);
        AutomationElement Lookup(string id){var remaining=budget-clock.Elapsed;if(remaining<=TimeSpan.Zero)throw new InvalidOperationException("preview.rendered-identity-timeout");return Find(id,remaining);}
        var source=new UiaPreviewSource(Lookup,listAutomationId,frontAutomationId,backAutomationId);
        var cards=PreviewReadBarrier.Read(source,expected,expectedCards,budget,heartbeat.Beat);
        heartbeat.Beat("preview.read.after");return cards;
    }
    string ReadValue(string automationId){heartbeat.Beat("control.read.before");var element=Find(automationId);if(!element.TryGetCurrentPattern(ValuePattern.Pattern,out var value))throw new InvalidOperationException("uia.preview-value-pattern-missing:"+automationId);var result=((ValuePattern)value).Current.Value;heartbeat.Beat("control.read.after");return result;}
    public void Dispose(){source?.Dispose();if(process is { HasExited:false }){if(!process.CloseMainWindow()||!process.WaitForExit(3000)){process.Kill(true);process.WaitForExit();}}process?.Dispose();}
}
internal sealed class ActionIdentityTracker(ActionCheckpoint checkpoint)
{
    string? commandId;long acceptedCursor;
    public ActionTerminal? Observe(RunnerProjection projection)
    {
        if(projection.OperationId!=checkpoint.OperationId)throw new InvalidOperationException("typed.action-operation-mismatch");
        if(projection.Cursor<=checkpoint.Cursor)return null;
        if(commandId is null&&projection.ActiveCommandId is not null&&projection.ActiveCommandId!=checkpoint.ActiveCommandId&&projection.ActiveCommandId!=checkpoint.TerminalCommandId){commandId=projection.ActiveCommandId;acceptedCursor=projection.Cursor;}
        if(commandId is null&&projection.TerminalCommandId is not null&&projection.TerminalCommandId!=checkpoint.TerminalCommandId&&projection.TerminalSequence>checkpoint.TerminalSequence){commandId=projection.TerminalCommandId;acceptedCursor=projection.Cursor;}
        if(commandId is not null&&projection.ActiveCommandId is null&&projection.TerminalCommandId==commandId&&projection.TerminalSequence>checkpoint.TerminalSequence)return new(projection.OperationId,commandId,acceptedCursor,projection);
        return null;
    }
}
internal sealed class TypedNamedPipeTaskObserver(string pipeName,HeartbeatJournal heartbeat) : ITaskObserver
{
    public void AssertNoAdditionalOperation(object checkpoint,AcceptedOperationIdentity identity,string finalCommandId)
    {
        if(checkpoint is not ReadToAnkiDiscoveryCheckpoint typed)throw new InvalidOperationException("typed.discovery-checkpoint-required");
        heartbeat.Beat("typed.final-readonly.before");
        var accepted=client.DiscoverAcceptedReadToAnkiAsync(typed).GetAwaiter().GetResult();
        var projection=Map(client.GetReadAloudCardsProjectionAsync(identity.OperationId).GetAwaiter().GetResult());
        if(accepted!=identity||projection.ActiveCommandId is not null||projection.TerminalCommandId!=finalCommandId||projection.Pipeline!="Completed")throw new InvalidOperationException("sequence.additional-operation-or-action-observed");
        heartbeat.Beat("typed.final-readonly.after");
    }
    readonly CoreV3Client client=new(pipeName); public string Identity=>"FrozenCoreV3TypedDiscoverySdkObserver";
    public object Begin(string marker){heartbeat.Beat("typed.begin.before");var result=client.BeginReadToAnkiDiscoveryAsync(marker).GetAwaiter().GetResult();heartbeat.Beat("typed.begin.after");return result;}
    public AcceptedOperationIdentity Discover(object checkpoint)
    {
        if(checkpoint is not ReadToAnkiDiscoveryCheckpoint typed)throw new InvalidOperationException("typed.discovery-checkpoint-required");
        heartbeat.Beat("typed.accepted-wait.before");var result=client.WaitForAcceptedReadToAnkiAsync(typed).GetAwaiter().GetResult();heartbeat.Beat("typed.accepted-wait.after");return result;
    }
    public RunnerProjection WaitInitial(AcceptedOperationIdentity identity)
    {
        heartbeat.Beat("typed.initial-terminal-wait.before");var deadline=DateTime.UtcNow.AddSeconds(180);do{heartbeat.Beat("typed.initial-terminal-wait.poll");var projection=Map(client.GetReadAloudCardsProjectionAsync(identity.OperationId).GetAwaiter().GetResult());if(projection.ActiveCommandId is null&&projection.TerminalCommandId==identity.CommandId&&projection.TerminalSequence>=identity.AcceptedSequence){heartbeat.Beat("typed.initial-terminal-wait.after");return projection;}Thread.Sleep(150);}while(DateTime.UtcNow<deadline);throw new InvalidOperationException("typed.initial-terminal-timeout");
    }
    public ActionCheckpoint BeginAction(string operationId,string semanticAction)
    {
        heartbeat.Beat("typed.action-discovery.begin.before");
        var marker="actual-action-"+Guid.NewGuid().ToString("N");
        var native=client.BeginReadToAnkiActionDiscoveryAsync(operationId,marker).GetAwaiter().GetResult();
        heartbeat.Beat("typed.action-discovery.begin.after");
        return new(operationId,semanticAction,native.Cursor,null,null,0,native);
    }
    public ActionTerminal WaitAction(ActionCheckpoint checkpoint)
    {
        if(checkpoint.NativeCheckpoint is not ReadToAnkiActionDiscoveryCheckpoint native)throw new InvalidOperationException("typed.action-discovery-checkpoint-required");
        heartbeat.Beat("typed.action-accepted-wait.before");
        var identity=client.WaitForAcceptedReadToAnkiActionAsync(native).GetAwaiter().GetResult();
        heartbeat.Beat("typed.action-accepted-wait.after");
        if(identity.OperationId!=checkpoint.OperationId)throw new InvalidOperationException("typed.action-operation-mismatch");
        heartbeat.Beat("typed.action-terminal-wait.before");
        var projection=Map(client.WaitReadAloudCardsActionAsync(identity).GetAwaiter().GetResult());
        heartbeat.Beat("typed.action-terminal-wait.after");
        if(projection.OperationId!=identity.OperationId||projection.TerminalCommandId!=identity.CommandId||projection.TerminalSequence<identity.AcceptedSequence)throw new InvalidOperationException("typed.action-terminal-identity-mismatch");
        return new(identity.OperationId,identity.CommandId,identity.AcceptedSequence,projection);
    }
    public RunnerProjection WaitAggregate(string operationId,string finalCommandId,int expectedCardCount=2,bool includeSpeech=true){heartbeat.Beat("typed.aggregate-wait.before");var deadline=DateTime.UtcNow.AddSeconds(180);do{heartbeat.Beat("typed.aggregate-wait.poll");var projection=Map(client.GetReadAloudCardsProjectionAsync(operationId).GetAwaiter().GetResult());if(projection.Pipeline=="Completed"&&projection.TerminalCommandId==finalCommandId&&projection.SemanticCards.Count==expectedCardCount&&FlowRunner.SpeechComplete(projection,includeSpeech)){heartbeat.Beat("typed.aggregate-wait.after");return projection;}Thread.Sleep(150);}while(DateTime.UtcNow<deadline);throw new InvalidOperationException("typed.aggregate-completion-timeout");}
    static RunnerProjection Map(ReadAloudCardsProjectionSnapshot snapshot)
    {
        var evidence=snapshot.SettingsEvidence;static void Manifest(string path){if(Path.IsPathRooted(path)||path.Contains("..",StringComparison.Ordinal)||!path.Replace('\\','/').EndsWith("/artifact.json",StringComparison.Ordinal))throw new InvalidOperationException("typed.manifest-path-invalid");}
        if(evidence is not null){Manifest(evidence.SettingsSnapshot.ManifestPath);Manifest(evidence.AnkiConfiguration.ManifestPath);if(string.IsNullOrWhiteSpace(evidence.PackageProvenance.Producer)||string.IsNullOrWhiteSpace(evidence.PackageProvenance.InputArtifactId)||evidence.PackageProvenance.InputManifestSha256.Length!=64)throw new InvalidOperationException("typed.package-provenance-invalid");}
        var draft=snapshot.CardDraft;var cards=draft?.Cards.Select((card,index)=>new PreviewCard(index,card.Front,card.Back,card.Template)).ToArray()??Array.Empty<PreviewCard>();var semantic=snapshot.AnkiSemanticReadback?.Cards.Select(card=>new RunnerSemanticCard(card.NoteId,card.CardIds,card.Front,card.Back,card.DeckName)).ToArray()??Array.Empty<RunnerSemanticCard>();
        RunnerContextEvidence? context=null;if(snapshot.ContextEvidence is { } ce){Manifest(ce.ContextSnapshot.ManifestPath);Manifest(ce.Capture.ManifestPath);foreach(var item in ce.ContextSegments)Manifest(item.Artifact.ManifestPath);context=new(ce.ContextSnapshot.ManifestPath,ce.ContextSnapshot.Sha256,ce.Capture.ManifestPath,ce.Capture.Sha256,ce.SelectedTextSha256,ce.TargetTextSha256,ce.SpokenTextSha256,ce.SpeechContextMode,ce.ContextRequested,ce.ContextApplied,ce.ContextStatus,ce.ContextGranularity,ce.ContextSegments.Select(item=>new RunnerContextSegment(item.Order,item.Role,item.Artifact.ManifestPath,item.Artifact.Sha256)).ToArray());}
        return new(snapshot.OperationId,snapshot.Pipeline.ToString(),snapshot.Playback.ToString(),snapshot.ActiveCommandId,snapshot.TerminalCommandId,snapshot.TerminalSequence,snapshot.Cursor,snapshot.LanguageCardChoice is not null,snapshot.CardResolution is not null,draft?.DraftSha256,cards,semantic,evidence?.SettingsSnapshot.ManifestPath??"",evidence?.SettingsSnapshot.Sha256??"",evidence?.AnkiConfiguration.ManifestPath??"",evidence?.AnkiConfiguration.Sha256??"",evidence?.Destination.RouteKey??"",evidence?.Destination.DeckIdentity??"",evidence?.Destination.DeckSha256??"",snapshot.SynthesisArtifact is not null,snapshot.PlaybackArtifact is not null,context){DraftRevision=draft?.Revision};
    }
    public void Dispose(){}
}
internal sealed class AnkiConnectReadOnlyClient(string endpoint) : IAnkiReadback
{
    readonly HttpClient http=new(){BaseAddress=new Uri(endpoint)}; public string Identity=>"AnkiConnectReadOnlyClient";
    public IReadOnlySet<long> CaptureBaseline(string deck)=>FindNotes(deck).ToHashSet();
    public AnkiReadback ReadNew(string deck,IReadOnlySet<long> baseline)
    {
        var noteIds=FindNotes(deck).Where(id=>!baseline.Contains(id)).ToArray(); if(noteIds.Length==0)return new(noteIds,0,deck);
        var cards=Invoke("findCards",new{query=string.Join(" or ",noteIds.Select(id=>$"nid:{id}"))}).EnumerateArray().Select(x=>x.GetInt64()).ToArray();
        var noteInfo=Invoke("notesInfo",new{notes=noteIds}).EnumerateArray().ToArray();
        var cardInfo=Invoke("cardsInfo",new{cards}).EnumerateArray().ToArray();
        if(noteInfo.Length!=noteIds.Length||cardInfo.Length!=cards.Length)throw new InvalidOperationException("anki.independent-readback-count-mismatch");
        var semantic=new List<RunnerSemanticCard>();
        foreach(var note in noteInfo)
        {
            var id=note.GetProperty("noteId").GetInt64();var noteCards=note.GetProperty("cards").EnumerateArray().Select(c=>c.GetInt64()).ToArray();
            if(!noteIds.Contains(id)||noteCards.Length==0||noteCards.Any(cid=>!cards.Contains(cid)||!cardInfo.Any(c=>c.GetProperty("cardId").GetInt64()==cid&&c.GetProperty("note").GetInt64()==id&&c.GetProperty("deckName").GetString()==deck)))throw new InvalidOperationException("anki.independent-readback-deck-or-id-mismatch");
            var fields=note.GetProperty("fields");semantic.Add(new(id,noteCards,fields.GetProperty("Front").GetProperty("value").GetString()!,fields.GetProperty("Back").GetProperty("value").GetString()!,deck));
        }
        return new(noteIds,cards.Length,deck,semantic);
    }
    long[] FindNotes(string deck)=>Invoke("findNotes",new{query=$"deck:\"{deck.Replace("\"","\\\"")}\""}).EnumerateArray().Select(x=>x.GetInt64()).ToArray();
    JsonElement Invoke(string action,object parameters)
    {
        var body=JsonSerializer.Serialize(new{action,version=6,@params=parameters}); using var content=new StringContent(body,Encoding.UTF8,"application/json"); using var response=http.PostAsync("",content).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode(); using var doc=JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        if(doc.RootElement.GetProperty("error").ValueKind!=JsonValueKind.Null)throw new InvalidOperationException("anki.readonly-error"); return doc.RootElement.GetProperty("result").Clone();
    }
    public void Dispose()=>http.Dispose();
}
internal sealed record DesktopAutomationBinding(string FloatingRoot,IReadOnlyDictionary<string,string> SemanticActions,IReadOnlyList<object> Contracts)
{
    public string Require(string action)=>SemanticActions.TryGetValue(action,out var id)?id:throw new InvalidOperationException("desktop-contract-semantic-action-missing:"+action);
    public static DesktopAutomationBinding Load(string candidate)
    {
        var root=Path.Combine(candidate,"contracts","desktop-automation");var actions=new Dictionary<string,(Version version,string id)>(StringComparer.Ordinal);var roles=new HashSet<string>(StringComparer.Ordinal);var contracts=new List<object>();
        foreach(var path in Directory.GetFiles(root,"desktop-automation.contract.json",SearchOption.AllDirectories))
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var version=Version.Parse(doc.RootElement.GetProperty("contractVersion").GetString()!);var schema=doc.RootElement.GetProperty("schemaVersion").GetString();var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));contracts.Add(new{path=Path.GetRelativePath(candidate,path).Replace('\\','/'),sha256=hash,contractVersion=version.ToString(),schemaVersion=schema});
            if(doc.RootElement.TryGetProperty("controlRoles",out var cr)&&cr.TryGetProperty("floatingRoot",out var fr)&&!string.IsNullOrWhiteSpace(fr.GetString()))roles.Add(fr.GetString()!);
            if(doc.RootElement.TryGetProperty("automationIds",out var ids))foreach(var item in ids.EnumerateArray()){if(!item.TryGetProperty("semanticAction",out var sa)||sa.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(sa.GetString())||!item.TryGetProperty("automationId",out var ai)||ai.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(ai.GetString()))continue;var action=sa.GetString()!;if(item.TryGetProperty("selection",out var selection)&&selection.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(selection.GetString()))action+=":"+selection.GetString();if(!actions.TryGetValue(action,out var old)||version>old.version)actions[action]=(version,ai.GetString()!);}
        }
        if(roles.Count!=1)throw new InvalidOperationException("desktop-contract-floating-root-not-unique");return new(roles.Single(),actions.ToDictionary(x=>x.Key,x=>x.Value.id,StringComparer.Ordinal),contracts);
    }
}

internal static class CandidateLaunchArguments
{
    internal static string[] Build(string argument,string? captureFixture)
    {
        if(captureFixture is null)return new[]{argument};
        if(!Path.IsPathFullyQualified(captureFixture))throw new ArgumentException("capture-fixture-absolute-path-required");
        return new[]{argument,"--candidate-capture-fixture",captureFixture};
    }
    internal static string? ParseFixture(string[] args)
    {
        var indices=Enumerable.Range(0,args.Length).Where(i=>args[i]=="--capture-fixture").ToArray();
        if(indices.Length==0)return null;
        if(indices.Length!=1||indices[0]+1>=args.Length)throw new ArgumentException("capture-fixture-single-path-required");
        var path=args[indices[0]+1];
        _=Build("--open-settings",path);
        if(!File.Exists(path))throw new FileNotFoundException("capture-fixture-file-missing",path);
        return path;
    }
}

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
#if PLAYBACK_REGRESSION
        if(args.Contains("--playback-regression-plan")){Console.WriteLine(JsonSerializer.Serialize(PlaybackRegression.Plan,new JsonSerializerOptions{WriteIndented=true}));return 0;}
        if(args.Contains("--playback-regression-self-test"))return PlaybackRegression.SelfTest(args);
        if(args.Contains("--actual")&&args.Contains("--playback-regression"))return PlaybackRegression.Run(args);
#endif
        if(args.Length==3&&args[0]=="--probe-child-identities")
        {
            try{return ExternalTextSourceCanary.ProbeChildIdentities(int.Parse(args[1]),args[2]);}
            catch(Exception ex){Console.Error.WriteLine(ex);return 20;}
        }
        if((args.Length==2&&args[0]=="--probe-text-source")||args.Contains("--source-loop-self-test",StringComparer.Ordinal))
        {
            try { return args[0]=="--probe-text-source" ? ExternalTextSourceCanary.Probe(new nint(long.Parse(args[1]))) : ExternalTextSourceCanary.Run(); }
            catch(Exception ex) { Console.Error.WriteLine(ex); return 20; }
        }
        if(args.Contains("--describe-candidate",StringComparer.Ordinal))
        {
            var index=Array.IndexOf(args,"--describe-candidate");if(index+1>=args.Length)throw new ArgumentException("candidate-path-required");var candidate=Path.GetFullPath(args[index+1]);var binding=DesktopAutomationBinding.Load(candidate);
            var required=new[]{"mode.context.set","read.capture","language-practice.select:both","card-resolution.accept-candidate","settings.page.select:export","settings.export.anki.deck-preset.select:custom","settings.export.anki.deck-routes.edit","settings.save","flashcard-preview.confirm","flashcard-preview.cards.observe","flashcard-preview.front.observe","flashcard-preview.back.observe"};foreach(var action in required)_=binding.Require(action);
            Console.WriteLine(JsonSerializer.Serialize(new{schemaVersion="knowledge-clipper-actual-runner-candidate-binding.v1",sideEffects="none",candidate,floatingRoot=binding.FloatingRoot,contracts=binding.Contracts,resolvedActions=required.ToDictionary(x=>x,binding.Require),implementationIds=Array.Empty<string>(),legacyHardcodedRootAccepted=false}));return 0;
        }
        if(args.Contains("--describe",StringComparer.Ordinal)) { Console.WriteLine(JsonSerializer.Serialize(new {schemaVersion="knowledge-clipper-actual-runner-plan.v1",sideEffects="none",referencesDesktopInternals=false,injectsBusinessInput=false,ports=new[]{"OwnedCandidateProcess+ExternalWindowsUia","FrozenCoreV3TypedDiscoverySdkObserver","AnkiConnectReadOnlyClient"},sdkSequence=new[]{"BeginReadToAnkiDiscoveryAsync","WaitForAcceptedReadToAnkiAsync","WaitReadAloudCardsActionAsync","ResolveCardResolutionAsync","ConfirmFlashcardDraftAsync","SettingsEvidence"},semanticFixtureVersion="desktop-actual-runner-semantic-cases.v1",postConfirmPlaybackWait="typed-until-idle-replayable",ankiReadback="exact-note-card-front-back-deck-ids",ankiMethods=new[]{"findNotes","findCards"},ankiWriteMethods=Array.Empty<string>(),pendingAdapters=Array.Empty<string>()})); return 0; }
        if(args.Length==3&&args[0]=="--preview-race-probe")return PreviewRaceProbe.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));
        if(args.Contains("--preview-barrier-self-test",StringComparer.Ordinal)){var cases=PreviewReadBarrier.SelfTest();Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",previewBarrierCases=cases,externalCalls=0,businessTasks=0}));return 0;}
        if(args.Contains("--self-test",StringComparer.Ordinal)) return SelfTest();
        if(args.Contains("--question-regression-plan",StringComparer.Ordinal)){Console.WriteLine(JsonSerializer.Serialize(QuestionRegression.Plan,new JsonSerializerOptions{WriteIndented=true}));return 0;}
        if(args.Contains("--question-regression-self-test",StringComparer.Ordinal))return QuestionRegression.SelfTest();
        if(args.Length==2&&args[0]=="--question-disk-proof-self-test")return QuestionDiskProof.SelfTest(Path.GetFullPath(args[1]));
        if(args.Contains("--actual",StringComparer.Ordinal)&&args.Contains("--question-regression",StringComparer.Ordinal))return QuestionRegression.Run(args);
        if(args.Contains("--actual",StringComparer.Ordinal)) return RunActual(args);
        Console.Error.WriteLine("actual-run-requires-quality-authorization-and-complete-adapters"); return 20;
    }
    static int RunActual(string[] args)
    {
        string Get(string name){var i=Array.IndexOf(args,name);if(i<0||i+1>=args.Length)throw new ArgumentException("argument-required:"+name);return args[i+1];}
        var candidate=Path.GetFullPath(Get("--candidate"));var evidence=Path.GetFullPath(Get("--evidence-root"));var authorizationPath=Path.GetFullPath(Get("--authorization"));
        if(Directory.Exists(evidence))throw new IOException("append-only-evidence-root-exists");Directory.CreateDirectory(evidence);
        var receipt=Path.Combine(evidence,"actual-run.receipt.json");var events=Path.Combine(evidence,"events.ndjson");
        WriteAtomic(receipt,new{schemaVersion="knowledge-clipper-actual-run-receipt.v1",status="pending",createdAtUtc=DateTimeOffset.UtcNow,candidate});
        var manifest=Path.Combine(candidate,"candidate-file-manifest.v1.json");string? manifestSha=null;
        void Append(object value)=>File.AppendAllText(events,JsonSerializer.Serialize(value)+Environment.NewLine,new UTF8Encoding(false));
        try
        {
            var captureFixture=CandidateLaunchArguments.ParseFixture(args);
            var sequence=args.Contains("--sequence-matrix",StringComparer.Ordinal)?SequencePlan.Load(captureFixture):null;
            if(captureFixture is not null) Append(new {atUtc=DateTimeOffset.UtcNow,state="capture-fixture-opt-in",path=captureFixture,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(captureFixture))),physicalCaptureValidated=false,scope="desktop-capture-port-only"});
            manifestSha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
            using var auth=JsonDocument.Parse(File.ReadAllText(authorizationPath));var root=auth.RootElement;
            if(!root.GetProperty("mayRunOneActual").GetBoolean()||!string.Equals(root.GetProperty("candidateManifestSha256").GetString(),manifestSha,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("quality-authorization-invalid");
            var exe=Directory.GetFiles(candidate,"KnowledgeClipper.exe",SearchOption.AllDirectories);if(exe.Length!=1)throw new InvalidOperationException("candidate-executable-not-unique");
            var heartbeat=new HeartbeatJournal(Append);using var surface=new OwnedCandidateProcessPort(heartbeat);using var observer=new TypedNamedPipeTaskObserver(Get("--pipe"),heartbeat);using var anki=new AnkiConnectReadOnlyClient(Get("--anki-endpoint"));
            var binding=DesktopAutomationBinding.Load(candidate);
            var completedSteps=new FlowRunner(surface,observer,anki,Append).Run(exe[0],Get("--data-root"),Get("--deck"),binding,captureFixture,sequence);
            if(sequence is not null)Append(new{atUtc=DateTimeOffset.UtcNow,state="sequence-worker-ledger-qualified",evidence=SequenceWorkerLedger.Verify(Get("--data-root"),completedSteps)});
            WriteAtomic(receipt,new{schemaVersion="knowledge-clipper-actual-run-receipt.v1",status="passed",completedAtUtc=DateTimeOffset.UtcNow,candidate,manifestSha256=manifestSha,captureMode=captureFixture is null?"external-textpattern":"deterministic-capture-fixture",sequenceMatrix=sequence is not null,actionCount=sequence?.Count??1,events="events.ndjson"});return 0;
        }
        catch(Exception ex){Append(new{atUtc=DateTimeOffset.UtcNow,state="failed-closed",code=ex.Message});WriteAtomic(receipt,new{schemaVersion="knowledge-clipper-actual-run-receipt.v1",status="failed-closed",completedAtUtc=DateTimeOffset.UtcNow,candidate,manifestSha256=manifestSha,failureCode=ex.Message,events="events.ndjson"});return 20;}
    }
    static void WriteAtomic(string path,object value){var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));File.Move(temp,path,true);}
    static int SelfTest()
    {
        _=SequenceSelfTest.Run();
        var mapping=new Dictionary<string,string>{{"mode.context.set","context"},{"read.capture","read"},{"language-practice.select:both","both"},{"card-resolution.accept-candidate","accept"},{"settings.page.select:export","settings-nav"},{"settings.export.anki.deck-preset.select:custom","deck-preset"},{"settings.export.anki.deck-routes.edit","deck-routes"},{"settings.save","settings-save"},{"flashcard-preview.confirm","preview-confirm"},{"flashcard-preview.cards.observe","preview-cards"},{"flashcard-preview.front.observe","preview-front"},{"flashcard-preview.back.observe","preview-back"}};var binding=new DesktopAutomationBinding("knowledge-clipper.desktop.floating-window",mapping,Array.Empty<object>());
        var settingsDestinationCases=AnkiSettingsEvidence.SelfTest();
        using(var surface=new FakeSurface(settingsPersisted:false))using(var observer=new FakeObserver())using(var anki=new FakeAnki())
        {
            var stopped=false;
            try{new FlowRunner(surface,observer,anki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);}
            catch(InvalidOperationException ex)when(ex.Message=="settings.saved-anki-destination-mismatch-before-capture")
            {stopped=surface.Calls.All(x=>x!="invoke:read"&&x!="source"&&x!="invoke:preview-confirm")&&anki.ReadCalls==0;}
            if(!stopped)throw new InvalidOperationException("settings-not-checked-before-capture");
            settingsDestinationCases++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",settingsDestinationCases,settingsBeforeContentAndTts=true,externalCalls=0}));
        var captureFixtureCases=TestCaptureFixtureRouting(binding);
        var uiTerminalCases=UiTerminalLog.SelfTest();
        using(var surface=new FakeSurface(missingUiTerminal:true))using(var observer=new FakeObserver())using(var anki=new FakeAnki())
        {
            var rejected=false;
            try{new FlowRunner(surface,observer,anki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);}
            catch(InvalidOperationException ex)when(ex.Message=="desktop-ui-rendered-terminal-timeout"){rejected=observer.AggregateWaits==1&&anki.ReadCalls==1&&surface.Calls.Count(x=>x=="invoke:preview-confirm")==1&&surface.Calls.Last()=="ui-terminal";}
            if(!rejected)throw new InvalidOperationException("ui-terminal-required-after-backend-self-test-failed");
            uiTerminalCases++;
        }
        var accepted=0;for(var preview=0;preview<=3;preview++)for(var delta=0;delta<=3;delta++){var events=new List<object>();using var surface=new FakeSurface(preview);using var observer=new FakeObserver();using var anki=new FakeAnki(delta);try{new FlowRunner(surface,observer,anki,events.Add).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);if(preview!=2||delta!=2)return 1;accepted++;}catch(InvalidOperationException){if(preview==2&&delta==2)return 1;}if(surface.Calls.Any(x=>x.Contains("selectedText",StringComparison.OrdinalIgnoreCase)))throw new Exception("business-input-injected");}
        var exact=new TopLevelWindowCandidate(101,42,true,42,"knowledge-clipper.desktop.floating-window",ExactTopLevelWindowSelector.WindowControlType,101);
        static bool Rejected(IEnumerable<TopLevelWindowCandidate> values,string code){try{ExactTopLevelWindowSelector.Select(values,42,"knowledge-clipper.desktop.floating-window");return false;}catch(InvalidOperationException ex){return ex.Message==code;}}
        if(ExactTopLevelWindowSelector.Select(new[]{exact},42,"knowledge-clipper.desktop.floating-window")!=exact)return 1;
        var locatorFixtures=new[]{
            Rejected(Array.Empty<TopLevelWindowCandidate>(),"candidate.root-not-found"),
            Rejected(new[]{exact,exact with{Handle=102,NativeWindowHandle=102}},"candidate.root-ambiguous"),
            Rejected(new[]{exact with{OwnerProcessId=41}},"candidate.root-not-found"),
            Rejected(new[]{exact with{UiaProcessId=41}},"candidate.root-not-found"),
            Rejected(new[]{exact with{AutomationId="wrong"}},"candidate.root-not-found"),
            Rejected(new[]{exact with{ControlType="ControlType.Pane"}},"candidate.root-not-found"),
            Rejected(new[]{exact with{NativeWindowHandle=999}},"candidate.root-not-found")};
        if(locatorFixtures.Any(x=>!x))return 1;
        var child=new ChildWindowCandidate("root",42,"target",501);var childFixtures=new[]{ExactChildWindowSelector.Select(new[]{child},42,"target")==child,RejectedChild(Array.Empty<ChildWindowCandidate>(),"uia.element-not-found:target"),RejectedChild(new[]{child,child with{TopLevelAutomationId="other",NativeWindowHandle=502}},"uia.element-ambiguous:target"),RejectedChild(new[]{child with{ProcessId=41}},"uia.element-not-found:target")};if(childFixtures.Any(x=>!x))return 1;
        var virtualChild=child with{NativeWindowHandle=0,RuntimeId="42,7,1"};
        var childIdentityFixtures=new[]{
            ExactChildWindowSelector.Select(new[]{child,child with{TopLevelAutomationId="owned-dialog"}},42,"target")==child,
            ExactChildWindowSelector.Select(new[]{virtualChild,virtualChild with{TopLevelAutomationId="owned-dialog"}},42,"target")==virtualChild,
            RejectedChild(new[]{virtualChild,virtualChild with{RuntimeId="42,7,2"}},"uia.element-ambiguous:target"),
            RejectedChild(new[]{child with{NativeWindowHandle=0}},"uia.element-identity-unavailable:target"),
            ExactChildWindowSelector.Select(new[]{child,child with{ProcessId=41,TopLevelAutomationId="other-process"}},42,"target")==child,
            RejectedChild(new[]{child,child with{NativeWindowHandle=502},child with{TopLevelAutomationId="owned-dialog"}},"uia.element-ambiguous:target")
        };
        if(childIdentityFixtures.Any(x=>!x))return 1;
        var beats=new List<object>();var journal=new HeartbeatJournal(beats.Add);journal.Beat("fixture.before");journal.Beat("fixture.after");var heartbeatJson=JsonSerializer.Serialize(beats);if(!heartbeatJson.Contains("\"sequence\":1",StringComparison.Ordinal)||!heartbeatJson.Contains("\"sequence\":2",StringComparison.Ordinal)||heartbeatJson.Contains("front-",StringComparison.Ordinal)||heartbeatJson.Contains("back-",StringComparison.Ordinal))return 1;
        var semanticAccepted=0;
        using(var surface=new FakeSurface())using(var observer=new FakeObserver(hasLanguageChoice:false))using(var anki=new FakeAnki()){new FlowRunner(surface,observer,anki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);if(surface.Calls.Any(x=>x=="invoke:accept"))return 1;semanticAccepted++;}
        using(var surface=new FakeSurface())using(var observer=new FakeObserver(hasResolution:true))using(var anki=new FakeAnki()){new FlowRunner(surface,observer,anki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);if(surface.Calls.Count(x=>x=="invoke:accept")!=1)return 1;semanticAccepted++;}
        var delayedCheckpoint=new ActionCheckpoint("op","flashcard-preview.confirm",90,null,"read-3",90);var delayed=new ActionIdentityTracker(delayedCheckpoint);if(delayed.Observe(FixtureProjection("op",91,"confirm-3",null,90)) is not null)return 1;if(delayed.Observe(FixtureProjection("op",100,"confirm-3",null,90)) is not null)return 1;var delayedTerminal=delayed.Observe(FixtureProjection("op",110,null,"confirm-3",110));if(delayedTerminal?.CommandId!="confirm-3")return 1;semanticAccepted++;
        var staleCheckpoint=new ActionCheckpoint("op","flashcard-preview.confirm",130,null,"read-4",130);var stale=new ActionIdentityTracker(staleCheckpoint);var staleCommandRejected=stale.Observe(FixtureProjection("op",140,null,"read-4",140)) is null;
        var contentMismatchRejected=false;var negativeAnkiWrites=0;using(var surface=new FakeSurface())using(var observer=new FakeObserver(wrongSemanticContent:true,hasLanguageChoice:false))using(var anki=new FakeAnki()){try{new FlowRunner(surface,observer,anki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);}catch(InvalidOperationException ex)when(ex.Message=="anki.semantic-readback-mismatch"){contentMismatchRejected=true;}negativeAnkiWrites=anki.ReadCalls;}
        var wrongDestinationRejectedBeforeConfirm=false;using(var surface=new FakeSurface())using(var observer=new FakeObserver(hasLanguageChoice:false,wrongDestination:true))using(var anki=new FakeAnki()){try{new FlowRunner(surface,observer,anki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);}catch(InvalidOperationException ex)when(ex.Message=="observer.settings-evidence-invalid-before-confirm"){wrongDestinationRejectedBeforeConfirm=surface.Calls.All(x=>x!="invoke:preview-confirm")&&anki.ReadCalls==0;}}
        var delayedPlaybackCompletionAfterCards=false;using(var surface=new FakeSurface())using(var observer=new FakeObserver(hasLanguageChoice:false,delayedPlayback:true))using(var anki=new FakeAnki()){new FlowRunner(surface,observer,anki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);delayedPlaybackCompletionAfterCards=observer.AggregateWaits==1&&surface.Calls.Count(x=>x=="invoke:preview-confirm")==1&&surface.Calls.All(x=>!x.Contains("replay",StringComparison.OrdinalIgnoreCase));}
        if(!staleCommandRejected||!contentMismatchRejected||negativeAnkiWrites!=0||!wrongDestinationRejectedBeforeConfirm||!delayedPlaybackCompletionAfterCards)return 1;
        Console.WriteLine(JsonSerializer.Serialize(new {status="PASS",matrixCases=16,acceptedCases=accepted,locatorFixtures=8,childLocatorFixtures=4,childIdentityFixtures=childIdentityFixtures.Length,heartbeatFixtures=1,semanticCases=6,semanticAcceptedCases=semanticAccepted,captureFixtureCases,uiTerminalCases,staleCommandRejected,contentMismatchRejected,negativeAnkiWrites,wrongDestinationRejectedBeforeConfirm,delayedPlaybackCompletionAfterCards,synthesisStarts=1,playbackStarts=1,replayStarts=0,locatorPolicy="strict-top-level-window-subtrees+exact-pid-id+unique-physical-identity",previewCounts=new[]{0,1,2,3},ankiDeltas=new[]{0,1,2,3},externalConnections=0,businessTasks=0})); return accepted==1&&semanticAccepted==3?0:1;
    }
    static int TestCaptureFixtureRouting(DesktopAutomationBinding binding)
    {
        var fixturePath=@"D:\Artifacts\KnowledgeClipper\fixture with spaces.json";
        using var normal=new FakeSurface();using var fixture=new FakeSurface();
        using var normalObserver=new FakeObserver();using var fixtureObserver=new FakeObserver();
        using var normalAnki=new FakeAnki();using var fixtureAnki=new FakeAnki();
        var events=new List<object>();
        new FlowRunner(normal,normalObserver,normalAnki,_=>{}).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding);
        new FlowRunner(fixture,fixtureObserver,fixtureAnki,events.Add).Run("candidate.exe","isolated","零碎知识剪藏::Canary::Desktop-V14",binding,fixturePath);
        var checks=new List<bool>{
            normal.LaunchArguments.SequenceEqual(new[]{"--open-settings"})&&normal.Calls.Count(x=>x=="source")==1,
            fixture.LaunchArguments.SequenceEqual(new[]{"--open-settings","--candidate-capture-fixture",fixturePath})&&fixture.Calls.All(x=>x!="source"),
            normal.Calls.Where(x=>x!="source").SequenceEqual(fixture.Calls)&&fixture.Calls.Count(x=>x=="invoke:read")==1&&fixture.Calls.Count(x=>x=="invoke:preview-confirm")==1,
            JsonSerializer.Serialize(events).Contains("deterministic-capture-fixture-external-textpattern-skipped",StringComparison.Ordinal),
            CandidateLaunchArguments.ParseFixture(Array.Empty<string>()) is null,
            CandidateLaunchArguments.ParseFixture(new[]{"--capture-fixture",typeof(Program).Assembly.Location})==typeof(Program).Assembly.Location
        };
        foreach(var invalid in new[]{new[]{"--capture-fixture"},new[]{"--capture-fixture","relative.json"},new[]{"--capture-fixture",fixturePath,"--capture-fixture",fixturePath},new[]{"--capture-fixture",@"D:\Artifacts\KnowledgeClipper\missing-"+Guid.NewGuid().ToString("N")+".json"}})
        {
            try { _=CandidateLaunchArguments.ParseFixture(invalid); checks.Add(false); }
            catch(Exception ex)when(ex is ArgumentException or FileNotFoundException){checks.Add(true);}
        }
        if(checks.Any(x=>!x))throw new InvalidOperationException("capture-fixture-routing-self-test-failed");
        return checks.Count;
    }
    static RunnerProjection FixtureProjection(string operationId,long cursor,string? active,string? terminal,long terminalSequence)=>new(operationId,"Completed","Idle",active,terminal,terminalSequence,cursor,false,false,null,Array.Empty<PreviewCard>(),Array.Empty<RunnerSemanticCard>(),"",new('a',64),"",new('b',64),"","",new('c',64),false,false);
    static bool RejectedChild(IEnumerable<ChildWindowCandidate> values,string code){try{ExactChildWindowSelector.Select(values,42,"target");return false;}catch(InvalidOperationException ex){return ex.Message==code;}}
}

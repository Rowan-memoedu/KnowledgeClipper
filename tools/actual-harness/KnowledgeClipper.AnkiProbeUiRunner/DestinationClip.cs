using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;
using KnowledgeClipper.Core.V3.Client;

// Package-external actual-process driver. All business actions go through UIA;
// the identical packaged SDK is a read-only observer, never a submit surrogate.
internal static class DestinationClip
{
    const string RootId="8zNzfX45XLCSKoPSJ", Title="KnowledgeClipper PhaseB Canary 20260828T081109Z";
    const string Deck="零碎知识剪藏::Canary::PhaseB-20260828T081109Z";
    const string Prefix="knowledge-clipper.desktop.", Preview=Prefix+"flashcard-preview.";
    const string Pipe="KnowledgeClipper.CoreV3.desktop-destinations-20260909-02";
    const string Capture="D:\\Artifacts\\KnowledgeClipper\\remnote-destinations\\desktop-live-d-20260909-02\\capture.json";
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    delegate bool EnumWindowsProc(nint hwnd,nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback,nint parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static string Sha(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void Save(string root,string name,object value){using var file=new FileStream(Path.Combine(root,name),FileMode.CreateNew);JsonSerializer.Serialize(file,value,Json);}
    static AutomationElement? Window(int pid,string id){var result=new List<AutomationElement>();EnumWindows((hwnd,_)=>{GetWindowThreadProcessId(hwnd,out var owner);if(owner==(uint)pid){try{var e=AutomationElement.FromHandle(hwnd);if(e.Current.AutomationId==id)result.Add(e);}catch(ElementNotAvailableException){}}return true;},0);Require(result.Count<=1,"ambiguous-window:"+id);return result.SingleOrDefault();}
    static AutomationElement Find(AutomationElement window,string id)=>window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id))??throw new InvalidOperationException("missing-control:"+id);
    static string Value(AutomationElement e)=>e.TryGetCurrentPattern(ValuePattern.Pattern,out var p)?((ValuePattern)p).Current.Value:e.Current.Name;
    static void Invoke(AutomationElement e){Require(e.Current.IsEnabled,"disabled-control:"+e.Current.AutomationId);((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();}
    static void Wait(Func<bool> predicate,int seconds,string reason){var timer=Stopwatch.StartNew();while(timer.Elapsed.TotalSeconds<seconds){if(predicate())return;Thread.Sleep(150);}throw new TimeoutException(reason);}
    static void Close(AutomationElement? window){if(window!=null&&window.TryGetCurrentPattern(WindowPattern.Pattern,out var p))((WindowPattern)p).Close();}
    static JsonElement Anki(string action,object args)
    {
        Require(new[]{"getActiveProfile","findNotes","notesInfo","cardsInfo"}.Contains(action),"read-only-anki-action-required");
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(18)};
        using var response=http.PostAsync("http://127.0.0.1:8765",new StringContent(JsonSerializer.Serialize(new{action,version=6,@params=args}),Encoding.UTF8,"application/json")).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Require(doc.RootElement.GetProperty("error").ValueKind==JsonValueKind.Null,"anki-read-error");return doc.RootElement.GetProperty("result").Clone();
    }
    static long[] DeckNotes()=>Anki("findNotes",new{query="deck:\""+Deck+"\""}).EnumerateArray().Select(x=>x.GetInt64()).Order().ToArray();
    static object Requests(string data)
    {
        var jobs=Path.Combine(data,"KnowledgeClipper","core-v3","worker-process-v3","jobs");
        return Directory.GetFiles(jobs,"request.json",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p=>new{path=Path.GetRelativePath(data,p),sha256=Sha(p)}).ToArray();
    }
    static int ExportRequests(string data)
    {
        var jobs=Path.Combine(data,"KnowledgeClipper","core-v3","worker-process-v3","jobs");
        return Directory.GetFiles(jobs,"request.json",SearchOption.AllDirectories).Count(path=>{
            using var doc=JsonDocument.Parse(File.ReadAllBytes(path));var r=doc.RootElement;
            return (r.GetProperty("workerId").GetString()??"").StartsWith("export.",StringComparison.Ordinal)
                && r.GetProperty("operation").GetString()!="probe";
        });
    }
    static void DumpArtifact(CoreV3Client client,string evidence,string name,TypedArtifactHandle? handle)
    {
        if(handle==null)return;var payload=client.ReadArtifactAsync(handle).GetAwaiter().GetResult();
        var path=Path.Combine(evidence,name+".payload");using(var file=new FileStream(path,FileMode.CreateNew))file.Write(payload.Content);
        Save(evidence,name+".identity.json",new{handle.Token,payload.RepresentationId,payload.MediaType,payload.ContractSchema,bytes=payload.Content.Length,sha256=Sha(path)});
    }
    static ReadAloudCardsProjectionSnapshot Observe(CoreV3Client client,string operation,string evidence,string phase,bool initial)
    {
        var timer=Stopwatch.StartNew();ReadAloudCardsProjectionSnapshot? result=null;string? previous=null;int sequence=0;
        Wait(()=>{
            result=client.GetReadAloudCardsProjectionAsync(operation).GetAwaiter().GetResult();
            var state=$"{result.Pipeline}:{result.ActionState}:{result.TerminalCommandId}:{result.Cursor}";
            if(state!=previous){previous=state;Save(evidence,$"{phase}-{sequence++:D3}.json",result);Console.WriteLine(phase+" "+state);}
            if(result.Pipeline is ReadAloudCardsPipelineState.Failed or ReadAloudCardsPipelineState.UnknownExternalState or ReadAloudCardsPipelineState.PartiallyFailed or ReadAloudCardsPipelineState.Cancelled)throw new InvalidOperationException("real-pipeline-terminal:"+state);
            if(initial && result.Pipeline is ReadAloudCardsPipelineState.WaitingLanguageCardChoice or ReadAloudCardsPipelineState.AwaitingCardResolution)throw new InvalidOperationException("real-manual-decision-no-oracle:"+state);
            return initial?result.Pipeline==ReadAloudCardsPipelineState.AwaitingCardConfirmation:result.Pipeline==ReadAloudCardsPipelineState.Completed;
        },210,phase+"-timeout-no-retry");return result!;
    }
    internal static int Run(string candidate,string data,string evidence)
    {
        candidate=Path.GetFullPath(candidate);data=Path.GetFullPath(data);evidence=Path.GetFullPath(evidence);
        Require(Directory.Exists(Path.Combine(data,"KnowledgeClipper","core-v3","artifact-store-v3")),"reuse-original-proof-store-required");
        Require(!Directory.Exists(evidence),"append-only-evidence-required");Directory.CreateDirectory(evidence);
        string phase="preflight",status="FAILED",failure="";Process? process=null;int clip=0,confirm=0;bool duplicateBlocked=false;string? operation=null;
        try{
            Require(Sha(Path.Combine(candidate,"candidate-file-manifest.v1.json"))=="5F4588CFAC939F555A0942B8D9F6C2CF1308FB98C78131B3BCF12DF5AC6DC51C","sealed-manifest-mismatch");
            Require(Sha(Path.Combine(candidate,"KnowledgeClipper.dll"))=="825A2B8716111907E83BECD949BFD858CE1C636AF9F51531D876785CF21E9120","sealed-desktop-mismatch");
            Require(Sha(typeof(CoreV3Client).Assembly.Location)==Sha(Path.Combine(candidate,"KnowledgeClipper.Core.V3.Client.dll")),"observer-sdk-not-candidate-exact");
            Require(Process.GetProcessesByName("KnowledgeClipper").Length==0,"user-desktop-running-do-not-interfere");
            var settings=Path.Combine(data,"KnowledgeClipper","workflow-settings.v3.json");
            Save(evidence,"existing-store.json",new{data,settingsSha256=Sha(settings),capture=Capture,captureSha256=Sha(Capture),requests=Requests(data)});
            Require(Anki("getActiveProfile",new{}).GetString()=="账户 1","anki-profile-outside-authorization");var before=DeckNotes();Save(evidence,"anki-before.json",new{deck=Deck,noteIds=before});
            var start=new ProcessStartInfo(Path.Combine(candidate,"KnowledgeClipper.exe")){UseShellExecute=false};
            start.ArgumentList.Add("--open-settings");start.ArgumentList.Add("--candidate-capture-fixture");start.ArgumentList.Add(Capture);start.Environment["LOCALAPPDATA"]=data;
            phase="launch-owned-desktop";process=Process.Start(start)??throw new Exception("desktop-start-failed");Save(evidence,"process.json",new{process.Id,path=process.MainModule?.FileName,candidate,data,pipe=Pipe});
            AutomationElement? settingsWindow=null;Wait(()=>{settingsWindow=Window(process.Id,Prefix+"settings.window");return settingsWindow!=null;},35,"owned-settings-window-missing");
            // Retain the previously UI-saved configuration and proof store unchanged.
            Close(settingsWindow);Wait(()=>Window(process.Id,Prefix+"settings.window")==null,10,"settings-did-not-close");
            Require(Sha(settings)==JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence,"existing-store.json"))).RootElement.GetProperty("settingsSha256").GetString(),"settings-close-mutated-profile");
            var client=new CoreV3Client(Pipe,new CoreV3ClientOptions(TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(45),TimeSpan.FromMilliseconds(200)));
            phase="begin-observer-checkpoint";var checkpoint=client.BeginReadToAnkiDiscoveryAsync("actual-dual-d-"+Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
            phase="actual-clip-click";Console.WriteLine("BUSINESS_SUBMIT actual UI clip once");Invoke(Find(Window(process.Id,Prefix+"floating-window")!,Prefix+"clip"));clip++;
            var accepted=client.WaitForAcceptedReadToAnkiAsync(checkpoint).GetAwaiter().GetResult();operation=accepted.OperationId;Save(evidence,"accepted.json",accepted);
            phase="wait-real-content-preview";var preview=Observe(client,operation,evidence,"before-confirm",true);
            Require(preview.Playback==ReadAloudCardsPlaybackState.NotRequested&&preview.SynthesisArtifact==null&&preview.PlaybackArtifact==null,"clip-must-not-request-speaker");
            Require(preview.CardDraft is {Cards.Count:>0}&&preview.DestinationSnapshot!=null,"missing-real-draft-or-frozen-destinations");
            var frozen=client.GetFrozenDestinationsAsync(new DestinationSnapshotReference(operation,preview.DestinationSnapshot!.Token)).GetAwaiter().GetResult();Save(evidence,"frozen-destinations.json",frozen);
            Require(frozen.Destinations.Count==2&&frozen.Destinations.Count(d=>d.Kind=="anki")==1&&frozen.Destinations.Count(d=>d.Kind=="remnote")==1,"exact-two-destinations-required");
            foreach(var d in frozen.Destinations){
                DumpArtifact(client,evidence,"configuration-"+d.Kind,d.Configuration);
                if(d.Kind=="remnote")Require(d.RemNoteBinding?.RootRemId==RootId&&d.RemNoteBinding.RootTitle==Title,"frozen-remnote-outside-canary");
                if(d.Kind=="anki"){
                    Require(d.RouteKey=="canary","anki-route-outside-canary");using var cfg=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidence,"configuration-anki.payload")));
                    Require(cfg.RootElement.GetProperty("expected_profile").GetString()=="账户 1"&&!cfg.RootElement.GetProperty("provision_managed").GetBoolean()&&cfg.RootElement.GetProperty("deck_routes").GetProperty("canary").GetString()==Deck,"anki-frozen-config-outside-authorization");
                }
            }
            DumpArtifact(client,evidence,"capture",preview.CaptureArtifact);DumpArtifact(client,evidence,"review",preview.ReviewArtifact);DumpArtifact(client,evidence,"card-bundle",preview.CardBundleArtifact);
            Save(evidence,"before-confirm-requests.json",Requests(data));Require(DeckNotes().SequenceEqual(before),"anki-mutated-before-confirm");
            Require(ExportRequests(data)==0,"export-worker-invoked-before-confirm");
            Require(preview.Destinations.All(d=>d.ExternalWriteCount is null or 0 && d.Evidence==null),"destination-write-evidence-before-confirm");
            AutomationElement? window=null;Wait(()=>{window=Window(process.Id,Preview+"window");return window!=null;},15,"real-shared-preview-window-missing");
            var list=Find(window!,Preview+"cards");var rows=list.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Cast<AutomationElement>().ToArray();
            Require(rows.Length==preview.CardDraft!.Cards.Count,"ui-preview-card-count-mismatch");var displayed=new List<object>();
            for(int i=0;i<rows.Length;i++){((SelectionItemPattern)rows[i].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();var expected=preview.CardDraft.Cards[i];Wait(()=>Value(Find(window!,Preview+"front"))==expected.Front&&Value(Find(window!,Preview+"back"))==expected.Back,5,"ui-front-back-mismatch");displayed.Add(new{expected.CardId,front=Value(Find(window!,Preview+"front")),back=Value(Find(window!,Preview+"back"))});}
            Save(evidence,"actual-ui-preview.json",new{operation,cards=displayed,destinations=Value(Find(window!,Preview+"destinations")),source="actual external-process UIA values compared with SDK CardDraft"});
            phase="actual-dual-confirm-once";var confirmButton=Find(window!,Preview+"confirm");Console.WriteLine("CONFIRM actual shared preview once, authorized isolated targets only");Invoke(confirmButton);confirm++;
            phase="wait-dual-terminal";var completed=Observe(client,operation,evidence,"after-confirm",false);Save(evidence,"completed.json",completed);
            Require(completed.Destinations.Count==2&&completed.Destinations.All(d=>d.ExternalStateKnown&&d.ExternalWriteCount==preview.CardDraft.Cards.Count),"dual-destination-exact-count-not-proved");
            Require(completed.Destinations.Select(d=>d.SourceCardArtifact.Token).Distinct().Count()==1,"destinations-did-not-share-card-bundle");
            foreach(var d in completed.Destinations)DumpArtifact(client,evidence,"destination-"+d.Kind,d.Evidence);
            Save(evidence,"after-confirm-requests.json",Requests(data));
            phase="independent-anki-readback";Require(Anki("getActiveProfile",new{}).GetString()=="账户 1","anki-profile-changed");var after=DeckNotes();var additions=after.Except(before).ToArray();
            Require(additions.Length==preview.CardDraft.Cards.Count,"anki-exact-addition-count-mismatch");var notes=Anki("notesInfo",new{notes=additions});Save(evidence,"anki-independent-notes.json",notes);
            var actualFaces=notes.EnumerateArray().Select(n=>(n.GetProperty("fields").GetProperty("Front").GetProperty("value").GetString(),n.GetProperty("fields").GetProperty("Back").GetProperty("value").GetString())).ToArray();
            // Exporter HTML normalization is inspected independently after this run;
            // preserve exact provider bytes, never rewrite a generated card to fit.
            var ids=notes.EnumerateArray().SelectMany(n=>n.GetProperty("cards").EnumerateArray().Select(c=>c.GetInt64())).ToArray();var cards=Anki("cardsInfo",new{cards=ids});Save(evidence,"anki-independent-cards.json",cards);
            Require(cards.EnumerateArray().All(c=>c.GetProperty("deckName").GetString()==Deck),"anki-readback-deck-mismatch");
            Save(evidence,"anki-after.json",new{deck=Deck,noteIds=after,added=additions});
            phase="duplicate-ui-confirm-blocked";Wait(()=>!confirmButton.Current.IsEnabled,10,"completed-confirm-not-disabled");
            try{((InvokePattern)confirmButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();}catch(ElementNotEnabledException){duplicateBlocked=true;}
            Require(duplicateBlocked,"disabled-ui-confirm-did-not-reject");Require(DeckNotes().SequenceEqual(after),"duplicate-confirm-added-notes");
            var repeat=client.GetReadAloudCardsProjectionAsync(operation).GetAwaiter().GetResult();Require(repeat.TerminalCommandId==completed.TerminalCommandId&&repeat.Cursor==completed.Cursor,"duplicate-ui-created-new-command");Save(evidence,"duplicate-confirm.json",new{duplicateBlocked,repeat.TerminalCommandId,repeat.Cursor,ankiAdditions=0,requests=Requests(data)});
            status="PASS-ACTUAL-DESKTOP-DUAL-CONFIRM-AWAITING-INDEPENDENT-REMNOTE-READBACK";
        }catch(Exception ex){failure=phase+":"+ex.GetType().Name+":"+ex.Message;Console.WriteLine(failure);File.WriteAllText(Path.Combine(evidence,"failure.txt"),ex.ToString());}
        finally{
            if(process is {HasExited:false}){Close(Window(process.Id,Preview+"window"));Close(Window(process.Id,Prefix+"settings.window"));Close(Window(process.Id,Prefix+"floating-window"));if(!process.WaitForExit(6000)){status="FAILED";failure+=";owned-desktop-not-exited";}}
            Save(evidence,"receipt.json",new{status,phase,failure,candidate,data,operation,actualDesktopProcess=true,clipInvocations=clip,confirmationInvocations=confirm,duplicateBlocked,readinessRepeated=false,physicalInput=0,speaker=0,ownedDesktopExited=process==null||process.HasExited});process?.Dispose();
        }
        Console.WriteLine(status);return status.StartsWith("PASS")?0:1;
    }
}

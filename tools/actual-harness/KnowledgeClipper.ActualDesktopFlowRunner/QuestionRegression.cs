using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

// Test-only orchestration. All commands travel through the production Desktop UI;
// the SDK and on-disk ledger are strictly observers, never submission fallbacks.
internal static class QuestionRegression
{
    internal const string InitialInput="knowledge-clipper.desktop.question.inline-input";
    internal const string FollowInput="knowledge-clipper.desktop.flashcard-preview.follow-up-input";
    internal const string FollowButton="knowledge-clipper.desktop.flashcard-preview.follow-up";
    internal const string Cards="knowledge-clipper.desktop.flashcard-preview.cards";
    internal const string Front="knowledge-clipper.desktop.flashcard-preview.front";
    internal const string Back="knowledge-clipper.desktop.flashcard-preview.back";
    internal const string Confirm="knowledge-clipper.desktop.flashcard-preview.confirm";
    internal const string Status="knowledge-clipper.desktop.flashcard-preview.status";
    internal const string CanaryDeck="零碎知识剪藏::Canary::PhaseB-20260828T081109Z";
    internal static readonly string[] Inputs=[
        "在读写数据时，什么是幂等操作？为什么网络超时后直接重试可能产生重复记录？",
        "请用提交学习笔记的例子，比较安全重试和重复写入。",
        "怎样用唯一请求标识和结果查询，把这个流程整理成三个可执行步骤？"];
    internal static object Plan=>new{mode="--actual --question-regression",offline="--question-regression-self-test",inputs=Inputs.Select((text,i)=>new{ordinal=i,text,sha256=Hash(Encoding.UTF8.GetBytes(text)),trigger=i==2?"InvokePattern":"WM_KEYDOWN/UP Return"}),controls=new{InitialInput,FollowInput,FollowButton,Cards,Front,Back,Confirm,Status},expected=new{questionOperations=3,exporterBeforeConfirm=0,finalExporter=1,finalCardCount="actual final draft count, 1..2",speech=0,ocr=0,remnote=0},target=CanaryDeck,configuration="first-session immutable config reused in all three operations",physicalKeyboard=false,liveExecuted=false};

    [DllImport("user32.dll",SetLastError=true)] static extern bool PostMessage(nint hwnd,uint message,nuint wparam,nint lparam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint SendMessageTimeout(nint hwnd,uint message,nuint wparam,StringBuilder text,uint flags,uint milliseconds,out nuint result);
    static void Enter(OwnedCandidateProcessPort surface,string id)
    {
        var element=surface.FindQuestionControl(id);
        Require(element.Current.IsEnabled,"question.input-disabled");
        var hwnd=new nint(element.Current.NativeWindowHandle);
        Require(hwnd!=0,"question.input-native-handle-missing");
        // Exactly one event pair; never resend on timeout or uncertain completion.
        Require(PostMessage(hwnd,0x100,13,1),"question.enter-dispatch-failed");
        Require(PostMessage(hwnd,0x101,13,unchecked((nint)(long)0xC0000001)),"question.enter-keyup-unknown");
    }

    internal static int Run(string[] args)
    {
        string Arg(string name){var i=Array.IndexOf(args,name);if(i<0||i+1>=args.Length)throw new ArgumentException("argument-required:"+name);return args[i+1];}
        Require(!args.Contains("--sequence-matrix")&&!args.Contains("--capture-fixture"),"question.mode-mix-forbidden");
        var candidate=Path.GetFullPath(Arg("--candidate"));var evidence=Path.GetFullPath(Arg("--evidence-root"));
        var data=Path.GetFullPath(Arg("--data-root"));var deck=Arg("--deck");var endpoint=new Uri(Arg("--anki-endpoint"));
        Require(deck==CanaryDeck,"question.exact-approved-deck-required");
        Require(endpoint.IsLoopback&&endpoint.Scheme=="http","question.anki-loopback-required");
        Require(!Directory.Exists(evidence)&&!Directory.Exists(Path.Combine(data,"KnowledgeClipper","core-v3")),"question.new-evidence-and-task-root-required");
        var manifest=Path.Combine(candidate,"candidate-file-manifest.v1.json");var manifestHash=FileHash(manifest);
        using(var auth=JsonDocument.Parse(File.ReadAllBytes(Arg("--authorization"))))
            Require(auth.RootElement.GetProperty("mayRunOneActual").GetBoolean()&&string.Equals(auth.RootElement.GetProperty("candidateManifestSha256").GetString(),manifestHash,StringComparison.OrdinalIgnoreCase),"quality-authorization-invalid");
        Directory.CreateDirectory(evidence);
        void Save(string name,object value){using var file=new FileStream(Path.Combine(evidence,name),FileMode.CreateNew);JsonSerializer.Serialize(file,value,new JsonSerializerOptions{WriteIndented=true});}
        void Append(object value)=>File.AppendAllText(Path.Combine(evidence,"events.ndjson"),JsonSerializer.Serialize(value)+Environment.NewLine,new UTF8Encoding(false));
        Save("pending.json",new{candidate,manifestHash,Plan,deck,createdAt=DateTimeOffset.UtcNow});
        try
        {
            var executable=Directory.GetFiles(candidate,"KnowledgeClipper.exe",SearchOption.AllDirectories).Single();
            var heartbeat=new HeartbeatJournal(Append);
            using var surface=new OwnedCandidateProcessPort(heartbeat);
            using var anki=new AnkiConnectReadOnlyClient(endpoint.ToString());
            var sdk=new CoreV3Client(Arg("--pipe"));
            var binding=DesktopAutomationBinding.Load(candidate);
            surface.StartOwned(executable,"--open-settings",data);surface.AttachExact(executable,binding.FloatingRoot);
            surface.SelectItem(binding.Require("settings.page.select:export"),"导出");
            surface.SelectItem(binding.Require("settings.export.anki.deck-preset.select:custom"),"custom");
            var routes=JsonSerializer.Serialize(new Dictionary<string,string>{{"canary",deck}});
            surface.SetValue(binding.Require("settings.export.anki.deck-routes.edit"),routes);
            surface.AssertValue(binding.Require("settings.export.anki.deck-routes.edit"),routes);
            surface.Invoke(binding.Require("settings.save"));
            var saved=surface.VerifySavedDestination(data,deck);Save("settings.json",saved);
            var baseline=anki.CaptureBaseline(deck);Save("baseline.json",new{deck,noteIds=baseline});
            var gate=new QuestionSequenceGate();var rows=new List<object>();
            QuestionProjectionSnapshot? current=null;IReadOnlyList<PreviewCard> preview=[];
            for(var turn=0;turn<Inputs.Length;turn++)
            {
                var previous=current?.OperationId;
                var id=turn==0?InitialInput:FollowInput;
                WaitEnabled(surface,id,heartbeat);
                surface.SetValue(id,Inputs[turn]);surface.AssertValue(id,Inputs[turn]);
                if(turn>0)WaitEnabled(surface,FollowButton,heartbeat);
                Append(new{state="question-ui-submit-once",turn,text=Inputs[turn],trigger=turn==2?"InvokePattern":"WM_KEYDOWN/UP",previousOperation=previous});
                if(turn==2)surface.Invoke(FollowButton);else Enter(surface,id);
                var rendered=WaitNewRender(surface,previous,heartbeat);
                current=sdk.GetQuestionProjectionAsync(rendered.OperationId).GetAwaiter().GetResult();
                Require(current.State==QuestionProjectionState.WaitingInput&&current.CardDraft!=null&&current.TerminalCommandId!=null,"question.own-draft-not-ready");
                var draft=current.CardDraft!;
                var expected=new RenderedPreviewIdentity(current.OperationId,draft.DraftSha256,draft.Revision);
                var typed=draft.Cards.Select((card,i)=>new PreviewCard(i,card.Front,card.Back,card.Template)).ToArray();
                preview=surface.ReadPreviewCards(Cards,Front,Back,expected,typed);
                WaitEnabled(surface,FollowInput,heartbeat);
                var proof=QuestionDiskProof.Read(data,current.OperationId,Inputs[turn],deck);
                var ledger=QuestionDiskProof.Workers(data,gate.Operations.Append(current.OperationId).ToArray(),0);
                Require(anki.CaptureBaseline(deck).SetEquals(baseline),"question.export-before-confirm");
                gate.Accept(current.OperationId,draft.Revision,draft.DraftSha256,preview.Count,proof.SessionId,proof.ConfigIdentity);
                var answer=current.AnswerArtifact==null?null:Encoding.UTF8.GetString(sdk.ReadArtifactAsync(current.AnswerArtifact,null).GetAwaiter().GetResult().Content);
                rows.Add(new{turn,input=Inputs[turn],current.OperationId,current.Cursor,current.TerminalCommandId,rendered,draftRevision=draft.Revision,draftSha256=draft.DraftSha256,actualUiCards=preview,answer,proof,ledger});
                Save($"turn-{turn}.json",rows[^1]);
            }
            var target=current!;var beforeCommand=target.TerminalCommandId;var beforeCursor=target.Cursor;
            // Re-read current rendered faces immediately before the one confirm.
            preview=surface.ReadPreviewCards(Cards,Front,Back,new(target.OperationId,target.CardDraft!.DraftSha256,target.CardDraft.Revision),preview);
            gate.BeginConfirm(target.OperationId);surface.Invoke(Confirm);
            var clock=Stopwatch.StartNew();QuestionProjectionSnapshot terminal;
            while(true)
            {
                terminal=sdk.GetQuestionProjectionAsync(target.OperationId).GetAwaiter().GetResult();
                if(terminal.TerminalCommandId!=null&&terminal.TerminalCommandId!=beforeCommand&&terminal.Cursor>beforeCursor&&terminal.State!=QuestionProjectionState.Running)break;
                Require(clock.Elapsed<TimeSpan.FromMinutes(4),"question.confirm-own-terminal-timeout-no-retry");
                heartbeat.Beat("question.confirm.observe");Thread.Sleep(250);
            }
            Require(terminal.State==QuestionProjectionState.Succeeded&&terminal.AnkiSemanticReadback!=null,"question.confirm-failed-or-unknown-no-retry");
            var semantic=terminal.AnkiSemanticReadback!.Cards;
            var readback=anki.ReadNew(deck,baseline);
            Require(semantic.Count==preview.Count&&readback.NoteIds.Count==preview.Count&&readback.CardCount==preview.Count&&readback.Cards!=null,"question.final-card-count-mismatch");
            for(var i=0;i<preview.Count;i++)
            {
                var card=semantic[i];var face=preview[i];
                Require(card.Front==face.Front&&card.Back==face.Back&&card.DeckName==deck,"question.final-semantic-preview-mismatch");
                Require(readback.Cards!.Any(x=>x.NoteId==card.NoteId&&x.CardIds.Order().SequenceEqual(card.CardIds.Order())&&x.Front==face.Front&&x.Back==face.Back&&x.Deck==deck),"question.independent-readback-mismatch");
            }
            var finalLedger=QuestionDiskProof.Workers(data,gate.Operations,1);
            var actualUiTerminal=WaitUiCompleted(surface,new(target.OperationId,target.CardDraft!.DraftSha256,target.CardDraft.Revision),heartbeat);
            Save("actual-run.receipt.json",new{status="passed",scope="production-Desktop-question-UI",candidate,manifestHash,runnerSha256=FileHash(typeof(QuestionRegression).Assembly.Location),inputs=Inputs,turns=rows,finalOperation=terminal.OperationId,terminal.TerminalCommandId,terminal.Cursor,readback,finalLedger,actualUiTerminal,physicalKeyboard=false,autoRetries=0,ocr=0,remnote=0,speaker=0});
            return 0;
        }
        catch(Exception ex){Save("actual-run.receipt.json",new{status="failed-closed",candidate,manifestHash,failure=ex.Message,automaticRetry=false,effectState="consult-preserved-worker-ledger"});return 20;}
    }

    static void WaitEnabled(OwnedCandidateProcessPort surface,string id,HeartbeatJournal heartbeat)
    {
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(15)){if(surface.FindQuestionControl(id).Current.IsEnabled)return;heartbeat.Beat("question.input-ready.wait");Thread.Sleep(100);}
        throw new InvalidOperationException("question.control-not-reenabled:"+id);
    }
    static object WaitUiCompleted(OwnedCandidateProcessPort surface,RenderedPreviewIdentity expected,HeartbeatJournal heartbeat)
    {
        var source=new UiaPreviewSource(surface.FindQuestionControl,Cards,Front,Back);var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(15))
        {
            var identity=source.ReadIdentity();var label=surface.FindQuestionControl(Status);var text=new StringBuilder(4096);
            var hwnd=new nint(label.Current.NativeWindowHandle);
            Require(hwnd!=0,"question.status-native-handle-missing");
            Require(SendMessageTimeout(hwnd,0x000D,(nuint)text.Capacity,text,2,1000,out _)!=0,"question.status-read-timeout");
            var confirm=surface.FindQuestionControl(Confirm).Current.IsEnabled;
            var follow=surface.FindQuestionControl(FollowButton).Current.IsEnabled;
            var input=surface.FindQuestionControl(FollowInput).Current.IsEnabled;
            if(IsUiCompleted(identity,expected,text.ToString(),confirm,follow,input))
                return new{identity,statusText=text.ToString(),statusHwnd=hwnd.ToInt64(),confirmEnabled=confirm,followUpEnabled=follow,followInputEnabled=input,source="actual-UIA-identity+native-label-WM_GETTEXT",backendSubstitution=false};
            heartbeat.Beat("question.actual-ui-completed.wait");Thread.Sleep(100);
        }
        throw new InvalidOperationException("question.actual-ui-completed-not-proven");
    }
    internal static bool IsUiCompleted(RenderedPreviewIdentity? observed,RenderedPreviewIdentity expected,string text,bool confirm,bool follow,bool input)
        => observed is {Ready:false}&&observed.SameDraft(expected)&&text.StartsWith("制卡与目标写入已完成。",StringComparison.Ordinal)&&!confirm&&!follow&&!input;

    static RenderedPreviewIdentity WaitNewRender(OwnedCandidateProcessPort surface,string? old,HeartbeatJournal heartbeat)
    {
        var source=new UiaPreviewSource(surface.FindQuestionControl,Cards,Front,Back);var clock=Stopwatch.StartNew();
        while(clock.Elapsed<TimeSpan.FromMinutes(4))
        {
            try {var identity=source.ReadIdentity();if(identity is {Ready:true}&&identity.OperationId!=old)return identity;}
            catch(InvalidOperationException ex)when(ex.Message.StartsWith("uia.element-not-found:",StringComparison.Ordinal)){}
            heartbeat.Beat("question.new-render.wait");Thread.Sleep(250);
        }
        throw new InvalidOperationException("question.new-render-timeout-no-resubmit");
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    internal static string FileHash(string path)=>Hash(File.ReadAllBytes(path));
    internal static void Require(bool value,string code){if(!value)throw new InvalidOperationException(code);}

    internal static int SelfTest()
    {
        var count=0;
        foreach(var cardinality in new[]{1,2}){var g=new QuestionSequenceGate();for(var i=0;i<3;i++)g.Accept("op"+i,"rev"+i,new string((char)('a'+i),64),cardinality,"session","config");g.BeginConfirm("op2");count++;}
        void Reject(Action action){try{action();throw new Exception("question-negative-accepted");}catch(InvalidOperationException){count++;}}
        QuestionSequenceGate One(){var g=new QuestionSequenceGate();g.Accept("op0","rev0",new('a',64),1,"session","config");return g;}
        Reject(()=>One().Accept("op0","rev1",new('b',64),1,"session","config"));
        Reject(()=>One().Accept("op1","rev0",new('b',64),1,"session","config"));
        Reject(()=>One().Accept("op1","rev1",new('a',64),1,"session","config"));
        Reject(()=>One().Accept("op1","rev1",new('b',64),1,"other","config"));
        Reject(()=>One().Accept("op1","rev1",new('b',64),1,"session","drift"));
        Reject(()=>One().Accept("op1","rev1",new('b',64),0,"session","config"));
        Reject(()=>One().Accept("op1","rev1",new('b',64),3,"session","config"));
        Reject(()=>One().BeginConfirm("op0"));
        var complete=One();complete.Accept("op1","rev1",new('b',64),1,"session","config");complete.Accept("op2","rev2",new('c',64),1,"session","config");
        Reject(()=>complete.BeginConfirm("op1"));complete.BeginConfirm("op2");Reject(()=>complete.BeginConfirm("op2"));
        var identity=new RenderedPreviewIdentity("op",new('a',64),"revision",Ready:false);
        Require(IsUiCompleted(identity,identity,"制卡与目标写入已完成。",false,false,false),"question.ui-completed-positive");count++;
        foreach(var changed in new[]{identity with{OperationId="old"},identity with{Revision="old"},identity with{Ready=true}}){Require(!IsUiCompleted(changed,identity,"制卡与目标写入已完成。",false,false,false),"question.ui-completed-negative");count++;}
        foreach(var flags in new[]{(true,false,false),(false,true,false),(false,false,true)}){Require(!IsUiCompleted(identity,identity,"制卡与目标写入已完成。",flags.Item1,flags.Item2,flags.Item3),"question.ui-disabled-negative");count++;}
        Require(!IsUiCompleted(identity,identity,"已确认，正在写入目标…",false,false,false),"question.ui-busy-negative");count++;
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",questionCases=count,previewBarrierCases=PreviewReadBarrier.SelfTest(),externalCalls=0,uiInvocations=0,businessTasks=0,liveExecuted=false}));return 0;
    }
}

internal sealed class QuestionSequenceGate
{
    readonly List<string> operations=[];readonly HashSet<string> revisions=[];readonly HashSet<string> hashes=[];
    string? session,config;bool confirmed;
    internal IReadOnlyList<string> Operations=>operations;
    internal void Accept(string operation,string revision,string sha,int cards,string sessionId,string configuration)
    {
        QuestionRegression.Require(!confirmed&&operations.Count<3&&!operations.Contains(operation)&&!revisions.Contains(revision)&&!hashes.Contains(sha)&&sha.Length==64&&cards is >=1 and <=2,"question.new-draft-identity-invalid");
        QuestionRegression.Require(operations.Count==0||(session==sessionId&&config==configuration),"question.session-or-config-drift");
        session=sessionId;config=configuration;operations.Add(operation);revisions.Add(revision);hashes.Add(sha);
    }
    internal void BeginConfirm(string operation){QuestionRegression.Require(!confirmed&&operations.Count==3&&operations[^1]==operation,"question.confirm-only-current-final-draft");confirmed=true;}
}

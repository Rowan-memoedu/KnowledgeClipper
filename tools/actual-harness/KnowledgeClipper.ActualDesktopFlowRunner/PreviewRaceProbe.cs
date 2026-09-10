using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal static class PreviewRaceProbe
{
    internal static int Run(string hostRoot,string receiptPath)
    {
        var rows=new List<object>();var commandSequence=0;
        var readyPath=Path.Combine(hostRoot,"ready.json");
        using var ready=JsonDocument.Parse(File.ReadAllBytes(readyPath));
        var pid=ready.RootElement.GetProperty("pid").GetInt32();var hwnd=ready.RootElement.GetProperty("hwnd").GetInt64();
        var window=AutomationElement.FromHandle(new nint(hwnd));
        if(window.Current.ProcessId!=pid||window.Current.NativeWindowHandle!=unchecked((int)hwnd))throw new InvalidOperationException("preview-host-identity-invalid");
        const string list="knowledge-clipper.desktop.flashcard-preview.cards",front="knowledge-clipper.desktop.flashcard-preview.front",back="knowledge-clipper.desktop.flashcard-preview.back";
        var expected=new RenderedPreviewIdentity("fixture-new-operation",new('b',64),"2");
        var cards=new[]{new PreviewCard(0,"New front one","New back one"),new PreviewCard(1,"New front two","New back two")};
        AutomationElement Find(string id)
        {
            var found=window.FindAll(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,pid),new PropertyCondition(AutomationElement.AutomationIdProperty,id)));
            if(found.Count!=1)throw new InvalidOperationException("preview-host-control-not-unique:"+id);
            return found[0];
        }
        void Send(string fixture)
        {
            var number=++commandSequence;var target=Path.Combine(hostRoot,$"render-{number:D4}.json");var temporary=target+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(new{fixture}),new UTF8Encoding(false));File.Move(temporary,target);
            var ack=Path.Combine(hostRoot,$"rendered-{number:D4}.json");var deadline=DateTime.UtcNow.AddSeconds(5);
            while(!File.Exists(ack)&&DateTime.UtcNow<deadline)Thread.Sleep(20);
            if(!File.Exists(ack))throw new InvalidOperationException("preview-host-render-ack-timeout");
            // Only synchronization: actual identities and card text are read through UIA.
        }
        try
        {
            var delayed=false;
            var source=new Counting(new UiaPreviewSource(Find,list,front,back),identity=>{if(!delayed&&identity?.OperationId=="fixture-old-operation"){delayed=true;Send("new");}});
            var actual=PreviewReadBarrier.Read(source,expected,cards,TimeSpan.FromSeconds(10),_=>{});
            if(!delayed||source.Reads!=2)throw new Exception("delayed-new-operation-not-exercised");
            rows.Add(new{scenario="delayed-new-operation",status="PASS",source.Reads,source.Identities,cards=actual});

            var refreshed=false;var stable=new UiaPreviewSource(Find,list,front,back,ordinal=>{if(ordinal==0&&!refreshed){refreshed=true;Send("new-refresh");}});
            var before=stable.ReadIdentity();actual=PreviewReadBarrier.Read(stable,expected,cards,TimeSpan.FromSeconds(10),_=>{});var after=stable.ReadIdentity();
            if(!refreshed||before!=after)throw new Exception("same-draft-refresh-changed-identity");
            rows.Add(new{scenario="same-draft-status-refresh",status="PASS",before,after,cards=actual});

            var changed=false;var replacement=new UiaPreviewSource(Find,list,front,back,ordinal=>{if(ordinal==0&&!changed){changed=true;Send("replacement");}});
            ExpectReject(replacement,"preview.rendered-draft-changed-during-read",TimeSpan.FromSeconds(10));
            rows.Add(new{scenario="replacement-during-card-read",status="PASS",rejected="preview.rendered-draft-changed-during-read",actualIdentity=replacement.ReadIdentity()});

            Send("old");var never=new Counting(new UiaPreviewSource(Find,list,front,back));
            ExpectReject(never,"preview.rendered-identity-timeout",TimeSpan.FromMilliseconds(400));
            if(never.Reads!=0)throw new Exception("never-ready-selected-card");
            rows.Add(new{scenario="never-ready",status="PASS",selectedCards=never.Reads,rejected="preview.rendered-identity-timeout"});

            Send("new");Send("new-status");var completed=new Counting(new UiaPreviewSource(Find,list,front,back));
            ExpectReject(completed,"preview.rendered-identity-timeout",TimeSpan.FromMilliseconds(400));
            if(completed.Reads!=0)throw new Exception("non-ready-status-selected-card");
            rows.Add(new{scenario="completed-status-not-confirm-ready",status="PASS",selectedCards=completed.Reads});
            var receipt=new{status="PASS",scope="real-Form-preview-race-only",hostReady=new{path=readyPath,sha256=Hash(readyPath),pid,hwnd},runner=new{path=typeof(PreviewRaceProbe).Assembly.Location,sha256=Hash(typeof(PreviewRaceProbe).Assembly.Location)},scenarios=rows,externalCalls=0,businessInvocations=0,confirmInvocations=0,actualRun=false};
            using var output=new FileStream(receiptPath,FileMode.CreateNew,FileAccess.Write);JsonSerializer.Serialize(output,receipt,new JsonSerializerOptions{WriteIndented=true});
            Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",scenarios=rows.Count,receiptPath}));return 0;
        }
        finally {File.WriteAllText(Path.Combine(hostRoot,"stop"),"offline-probe-complete",new UTF8Encoding(false));}
        void ExpectReject(IRenderedPreviewSource source,string code,TimeSpan timeout)
        {try{PreviewReadBarrier.Read(source,expected,cards,timeout,_=>{});throw new Exception("preview-real-form-negative-accepted");}catch(InvalidOperationException ex)when(ex.Message==code){}}
    }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    sealed class Counting(IRenderedPreviewSource source,Action<RenderedPreviewIdentity?>? observed=null):IRenderedPreviewSource
    {
        internal int Reads;internal readonly List<RenderedPreviewIdentity?> Identities=[];
        public RenderedPreviewIdentity? ReadIdentity(){var value=source.ReadIdentity();Identities.Add(value);observed?.Invoke(value);return value;}
        public int ReadCount()=>source.ReadCount();
        public PreviewCard ReadCard(int ordinal){Reads++;return source.ReadCard(ordinal);}
    }
}

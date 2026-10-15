using System.IO;
using KnowledgeClipper.Workflow;

internal static class SnapshotChecks
{
    internal static void Run(string root,Action<bool,string> check)
    {
        var store=new DraftStore(root);var excerpts=new ExcerptStore(root);
        var material=new CaptureMaterial("公开快照回归",null,"generated_allowed",null,"",0,DateTimeOffset.UtcNow);
        var parent=new ClipDraft{Original=material,Material=material,Settings=new(),Configuration=new("",[]),Input=new(Intent:"question"),Chat=new()};
        var child=new ClipDraft{Original=material,Material=material,Settings=new(),Configuration=new("",[]),ParentDraftId=parent.Id,ImportState=ImportState.Unknown};
        parent.Chat.Compositions.Add(new(){Front="题目",Back="答案",ExportDraftId=child.Id});
        store.Save(child);store.Save(parent);var frozen=File.ReadAllText(store.Identity(parent.Id));
        var snapshot=store.CreateReadSnapshot();
        using(var locked=File.Open(store.Identity(child.Id),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var rows=HistoryRecords.Read(snapshot,excerpts);
            check(rows.Length==1&&rows[0].Draft!.Id==parent.Id,"snapshot retains original conversation grouping without rereading export files");
            check(HistoryRecords.ActionText(rows[0],snapshot,excerpts)=="继续制卡"&&HistoryRecords.ChatStatus(rows[0].Draft!,snapshot).Contains("待核对"),"snapshot preserves Unknown status and action");
        }
        var projected=snapshot.Find(parent.Id)!;projected.Chat!.Input="read projection only";
        check(File.ReadAllText(store.Identity(parent.Id))==frozen&&store.Find(parent.Id)!.Chat!.Input!="read projection only","projection changes cannot mutate saved or live drafts");
        var rejected=false;try{snapshot.Save(projected);}catch(InvalidOperationException){rejected=true;}
        check(rejected,"read snapshot cannot accidentally save projected state");
        child.ImportState=ImportState.Verified;store.Save(child);var next=store.CreateReadSnapshot();
        check(snapshot.Find(child.Id)!.ImportState==ImportState.Unknown&&next.Find(child.Id)!.ImportState==ImportState.Verified,"fresh refresh observes completion without changing an in-flight snapshot");
        check(HistoryRecords.ActionText(new(Draft:next.Find(parent.Id)),next,excerpts)=="查看","all created cards Verified still gives the exact view action");
        var session=new ExcerptSession{Original=material,Settings=new()};excerpts.Save(session);
        rejected=false;try{excerpts.CreateReadSnapshot().Save(session);}catch(InvalidOperationException){rejected=true;}
        check(rejected,"excerpt presentation store also rejects writes");
    }
}

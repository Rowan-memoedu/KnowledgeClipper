namespace KnowledgeClipper.ActualDesktopFlowRunner;

// Test-runner observations only. Name is the measured WinForms/MSAA menu identity;
// AutomationId is intentionally not an identity channel for this menu.
internal sealed record PlaybackMenuObservation(string Identity,int OwnerPid,int RootOwnerPid,
    bool VisibleRoot,bool RootIsMenu,bool IsMenuItem,bool VisibleItem,string Name,
    bool Enabled,bool InvokeAvailable);

internal static class PlaybackMenuSelector
{
    internal const string StopName="knowledge-clipper.desktop.playback.stop";
    internal static PlaybackMenuObservation? Select(IEnumerable<PlaybackMenuObservation> observations,int ownedPid,string exactName)
    {
        if(ownedPid<=0||exactName!=StopName)throw new InvalidOperationException("playback.menu-selector-scope-invalid");
        var matches=observations.Where(x=>x.OwnerPid==ownedPid&&x.RootOwnerPid==ownedPid&&
            x.VisibleRoot&&x.RootIsMenu&&x.IsMenuItem&&x.VisibleItem&&
            string.Equals(x.Name,exactName,StringComparison.Ordinal)).GroupBy(x=>x.Identity,StringComparer.Ordinal).ToArray();
        if(matches.Length>1)throw new InvalidOperationException("playback.menu-ambiguous");
        if(matches.Length==0)return null;
        var copies=matches[0].ToArray();
        if(string.IsNullOrWhiteSpace(copies[0].Identity)||copies.Any(x=>x!=copies[0]))
            throw new InvalidOperationException("playback.menu-observation-conflict");
        return copies[0];
    }
    internal static string Availability(PlaybackMenuObservation? item)=>item is null?"menu-missing":
        !item.Enabled?"menu-disabled":!item.InvokeAvailable?"menu-invoke-pattern-missing":"menu-ready";

    internal static int SelfTest()
    {
        var cases=0;
        void Check(bool pass){if(!pass)throw new Exception("menu-selector-test-failed");cases++;}
        void Reject(Action action){try{action();throw new Exception("menu-negative-accepted");}catch(InvalidOperationException){cases++;}}
        var item=new PlaybackMenuObservation("17:runtime:1.2",17,17,true,true,true,true,StopName,true,true);
        Check(Select(new[]{item},17,StopName)==item); // measured Name with empty AutomationId
        Check(Select(new[]{item,item},17,StopName)==item); // same logical item, two observations
        foreach(var invalid in new[]{item with{OwnerPid=18},item with{RootOwnerPid=18},item with{VisibleRoot=false},
            item with{RootIsMenu=false},item with{IsMenuItem=false},item with{VisibleItem=false},
            item with{Name="停止朗读"},item with{Name=StopName+".other"}})
            Check(Select(new[]{invalid},17,StopName) is null);
        Reject(()=>Select(new[]{item,item with{Identity="17:runtime:1.3"}},17,StopName));
        Reject(()=>Select(new[]{item,item with{Enabled=false}},17,StopName));
        Reject(()=>Select(new[]{item},17,"停止朗读"));
        Check(Availability(Select(new[]{item with{Enabled=false}},17,StopName))=="menu-disabled");
        Check(Availability(Select(new[]{item with{InvokeAvailable=false}},17,StopName))=="menu-invoke-pattern-missing");
        Check(Availability(null)=="menu-missing");
        Check(Availability(item)=="menu-ready");
        return cases;
    }
}

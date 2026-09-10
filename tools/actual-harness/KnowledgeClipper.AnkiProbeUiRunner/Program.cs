using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

internal static class Program
{
    const string Prefix="knowledge-clipper.desktop.settings.";
    const string ExpectedDll="D142D07EC125D34150317747F9CF5C2CAD7824C53B1D9EC1EA1CEA5CE9F63FBA";
    const string ExpectedManifest="6BAC404621750CC9D7049736BED23274AAB1DB88012EE7B5AC898107C0DE0561";
    const string Deck="零碎知识剪藏::Canary::PhaseB-20260828T081109Z";
    static int invocations;
    static string phase="preflight";
    [DllImport("UIAutomationCore.dll")] static extern int UiaNodeFromHandle(nint hwnd,out nint node);
    [DllImport("UIAutomationCore.dll")] static extern int UiaGetPropertyValue(nint node,int id,[MarshalAs(UnmanagedType.Struct)]out object value);
    [DllImport("UIAutomationCore.dll")] static extern bool UiaNodeRelease(nint node);
    static string Sha(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void Require(bool condition,string error){if(!condition)throw new InvalidOperationException(error);}
    static void Save(string root,string name,object value)=>File.WriteAllText(Path.Combine(root,name),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
    static AutomationElement? Root(int pid,string id)=>AutomationElement.RootElement.FindFirst(TreeScope.Children,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,pid),new PropertyCondition(AutomationElement.AutomationIdProperty,id)));
    static AutomationElement Find(AutomationElement root,string id)=>root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id))??throw new InvalidOperationException("missing-semantic-control:"+id);
    static void Wait(Func<bool> ready,int seconds,string error){var until=DateTime.UtcNow.AddSeconds(seconds);while(DateTime.UtcNow<until){if(ready())return;Thread.Sleep(100);}throw new TimeoutException(error);}
    static string Value(AutomationElement e)=>((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    static string Description(AutomationElement e)
    {
        Marshal.ThrowExceptionForHR(UiaNodeFromHandle(new nint(e.Current.NativeWindowHandle),out var node));
        try{Marshal.ThrowExceptionForHR(UiaGetPropertyValue(node,30094,out var value));return value as string??"";}finally{UiaNodeRelease(node);}
    }
    static object UiState(AutomationElement status)=>new {text=Value(status),description=Description(status),automationId=status.Current.AutomationId,ownerPid=status.Current.ProcessId,observedAt=DateTimeOffset.UtcNow};
    static JsonElement ReadOnly(string action,object? parameters=null)
    {
        Require(new[]{"getActiveProfile","deckNames","modelNames","findNotes","findCards"}.Contains(action),"read-only-allowlist");
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};
        var content=new StringContent(JsonSerializer.Serialize(new{action,version=6,@params=parameters??new{}}),Encoding.UTF8,"application/json");
        using var response=http.PostAsync("http://127.0.0.1:8765",content).GetAwaiter().GetResult();response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Require(json.RootElement.GetProperty("error").ValueKind==JsonValueKind.Null,"anki-read-only-error");return json.RootElement.GetProperty("result").Clone();
    }
    static string Inventory()
    {
        var profile=ReadOnly("getActiveProfile").GetString();Require(profile=="账户 1","active-profile-mismatch");
        var decks=ReadOnly("deckNames").EnumerateArray().Select(x=>x.GetString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        Require(decks.Contains(Deck),"authorized-existing-deck-missing");
        var models=ReadOnly("modelNames").EnumerateArray().Select(x=>x.GetString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var notes=ReadOnly("findNotes",new{query=""}).EnumerateArray().Select(x=>x.GetInt64()).Order().ToArray();
        var cards=ReadOnly("findCards",new{query=""}).EnumerateArray().Select(x=>x.GetInt64()).Order().ToArray();
        return JsonSerializer.Serialize(new{profile,decks,models,notes,cards});
    }
    [STAThread]
    static int Main(string[] args)
    {
        if(args.Length==4 && args[0]=="--destination-readiness") return DestinationReadiness.Run(args[1],args[2],args[3]);
        if(args.Length==4 && args[0]=="--destination-clip") return DestinationClip.Run(args[1],args[2],args[3]);
        if(args.Length!=4)throw new ArgumentException("candidate data evidence seed-root required");
        var candidate=Path.GetFullPath(args[0]);var data=Path.GetFullPath(args[1]);var evidence=Path.GetFullPath(args[2]);var seed=Path.GetFullPath(args[3]);
        Require(!Directory.Exists(data)&&!Directory.Exists(evidence),"append-only-output-exists");
        Directory.CreateDirectory(evidence);Process? process=null;string? before=null;string result="FAILED";string? failure=null;
        try
        {
            Require(Sha(Path.Combine(candidate,"KnowledgeClipper.dll"))==ExpectedDll&&Sha(Path.Combine(candidate,"candidate-file-manifest.v1.json"))==ExpectedManifest,"sealed-candidate-mismatch");
            Require(Process.GetProcessesByName("KnowledgeClipper").Length==0,"user-desktop-running-do-not-interfere");
            before=Inventory();File.WriteAllText(Path.Combine(evidence,"before.json"),before);
            var configRoot=Path.Combine(data,"KnowledgeClipper");Directory.CreateDirectory(configRoot);
            foreach(var file in new[]{"settings.json","workflow-settings.v3.json","desktop-module-settings.v1.json"})File.Copy(Path.Combine(seed,file),Path.Combine(configRoot,file));
            using(var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(configRoot,"desktop-module-settings.v1.json"))))
                Require(settings.RootElement.GetProperty("ankiExpectedProfile").GetString()=="账户 1"&&settings.RootElement.GetProperty("ankiDeckRoutes").GetProperty("canary").GetString()==Deck,"seed-target-mismatch");
            var start=new ProcessStartInfo(Path.Combine(candidate,"KnowledgeClipper.exe")){UseShellExecute=false};start.ArgumentList.Add("--open-settings");start.Environment["LOCALAPPDATA"]=data;
            process=Process.Start(start)??throw new Exception("desktop-start-failed");
            Save(evidence,"process.json",new{process.Id,path=process.MainModule?.FileName,dll=ExpectedDll,manifest=ExpectedManifest});
            AutomationElement? settingsRoot=null;
            Wait(()=>{settingsRoot=Root(process.Id,Prefix+"window");return settingsRoot!=null;},30,"settings-window-unavailable");
            var navigation=Find(settingsRoot!,Prefix+"navigation");
            var export=navigation.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"导出"))??throw new Exception("export-page-seam-missing");
            phase="select-export";
            ((SelectionItemPattern)export.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var route=Find(settingsRoot!,Prefix+"export.anki.probe-route");
            phase="select-explicit-route";
            if(route.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out var expansion))((ExpandCollapsePattern)expansion).Expand();
            var choice=route.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"canary"))??throw new Exception("canary-route-item-seam-missing");
            ((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Require(((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,"explicit-canary-route-required");
            if(expansion!=null)((ExpandCollapsePattern)expansion).Collapse();
            var status=Find(settingsRoot!,Prefix+"export.anki.probe-status");var button=Find(settingsRoot!,Prefix+"export.anki.probe-readonly");
            phase="observe-before";
            Save(evidence,"ui-before.json",UiState(status));
            Require(button.Current.IsEnabled,"probe-disabled");
            phase="invoke-probe";var invoke=(InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern);
            invocations++;invoke.Invoke();
            phase="observe-terminal";
            Wait(()=>Description(status).Contains("terminalState",StringComparison.Ordinal),70,"probe-terminal-timeout-no-retry");
            Save(evidence,"ui-after.json",UiState(status));
            using var terminal=JsonDocument.Parse(Description(status));var state=terminal.RootElement;
            Require(state.GetProperty("ready").GetBoolean()&&state.GetProperty("route").GetString()=="canary"&&state.GetProperty("terminalState").GetString()=="Succeeded"&&state.GetProperty("evidence").ValueKind==JsonValueKind.String,"production-ui-nonready-see-ui-after");
            Require(button.Current.IsEnabled,"probe-busy-not-released");
            result="PASS";
        }
        catch(Exception ex){failure=phase+":"+ex.GetType().Name+":"+ex.Message;Console.WriteLine(failure);File.WriteAllText(Path.Combine(evidence,"failure.txt"),ex.ToString());}
        finally
        {
            if(process is {HasExited:false})
            {
                var floating=Root(process.Id,"knowledge-clipper.desktop.floating-window");
                if(floating!=null&&floating.TryGetCurrentPattern(WindowPattern.Pattern,out var window))((WindowPattern)window).Close();
                if(!process.WaitForExit(5000)){result="FAILED";failure=(failure??"")+";owned-desktop-did-not-exit";}
            }
            try{if(before!=null){var after=Inventory();File.WriteAllText(Path.Combine(evidence,"after.json"),after);Require(after==before,"read-only-inventory-changed");}}catch(Exception ex){result="FAILED";failure=(failure??"")+";"+ex.Message;}
            Save(evidence,"receipt.json",new{status=result,failure,semanticButtonInvocations=invocations,actualDesktopProcess=true,physicalKeyboardMouse=0,workerWriteActions=0,speaker=0,candidate,dll=ExpectedDll,manifest=ExpectedManifest,ownedDesktopExited=process==null||process.HasExited});
            process?.Dispose();
        }
        return result=="PASS"?0:1;
    }
}

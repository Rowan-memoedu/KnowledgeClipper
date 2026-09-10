using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;

// External test driver only. Real sealed EXE owns controls, settings, SDK and Host.
internal static class DestinationReadiness
{
    const string Prefix="knowledge-clipper.desktop.settings.";
    const string RootId="8zNzfX45XLCSKoPSJ";
    const string Title="KnowledgeClipper PhaseB Canary 20260828T081109Z";
    const string Deck="零碎知识剪藏::Canary::PhaseB-20260828T081109Z";
    [DllImport("UIAutomationCore.dll")] static extern int UiaNodeFromHandle(nint hwnd,out nint node);
    [DllImport("UIAutomationCore.dll")] static extern int UiaGetPropertyValue(nint node,int id,[MarshalAs(UnmanagedType.Struct)]out object value);
    [DllImport("UIAutomationCore.dll")] static extern bool UiaNodeRelease(nint node);
    delegate bool EnumWindowsProc(nint hwnd,nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback,nint parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    static void Require(bool ok,string error){if(!ok)throw new InvalidOperationException(error);}
    static string Sha(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static AutomationElement? Root(int pid,string id){var found=new List<AutomationElement>();EnumWindows((hwnd,_)=>{GetWindowThreadProcessId(hwnd,out var owner);if(owner!=(uint)pid)return true;try{var element=AutomationElement.FromHandle(hwnd);if(element.Current.ProcessId==pid&&element.Current.AutomationId==id)found.Add(element);}catch(ElementNotAvailableException){}return true;},0);Require(found.Count<=1,"ambiguous-owned-window:"+id);return found.SingleOrDefault();}
    static AutomationElement Find(AutomationElement root,string id)=>root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,Prefix+id))??throw new InvalidOperationException("missing-control:"+id);
    static string Value(AutomationElement e)=>e.TryGetCurrentPattern(ValuePattern.Pattern,out var v)?((ValuePattern)v).Current.Value:e.Current.Name;
    static void Set(AutomationElement e,string value)=>((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    static void Invoke(AutomationElement e){Require(e.Current.IsEnabled,"control-disabled:"+e.Current.AutomationId);((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();}
    static void Wait(Func<bool> condition,int seconds,string error){var deadline=DateTime.UtcNow.AddSeconds(seconds);while(DateTime.UtcNow<deadline){if(condition())return;Thread.Sleep(100);}throw new TimeoutException(error);}
    static string Description(AutomationElement e){Marshal.ThrowExceptionForHR(UiaNodeFromHandle(new nint(e.Current.NativeWindowHandle),out var node));try{Marshal.ThrowExceptionForHR(UiaGetPropertyValue(node,30094,out var value));return value as string??"";}finally{UiaNodeRelease(node);}}
    static object State(AutomationElement e)=>new{text=Value(e),description=Description(e),automationId=e.Current.AutomationId};
    static void Save(string root,string name,object value){using var stream=new FileStream(Path.Combine(root,name),FileMode.CreateNew);JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true});}
    static void Choose(AutomationElement combo,string name){if(combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out var p))((ExpandCollapsePattern)p).Expand();var item=combo.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,name))??throw new Exception("missing-choice:"+name);((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();if(p!=null)((ExpandCollapsePattern)p).Collapse();}
    static void Check(AutomationElement box){var toggle=(TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern);if(toggle.Current.ToggleState!=ToggleState.On)toggle.Toggle();Require(toggle.Current.ToggleState==ToggleState.On,"target-checkbox-not-on");}
    internal static int Run(string candidate,string data,string evidence)
    {
        candidate=Path.GetFullPath(candidate);data=Path.GetFullPath(data);evidence=Path.GetFullPath(evidence);
        Require(!Directory.Exists(data)&&!Directory.Exists(evidence),"append-only-roots-required");Directory.CreateDirectory(evidence);
        string phase="preflight",status="FAILED",failure="";Process? process=null;AutomationElement? settings=null;int discover=0,probe=0,ankiProbe=0;
        try{
            Require(Sha(Path.Combine(candidate,"candidate-file-manifest.v1.json"))=="5F4588CFAC939F555A0942B8D9F6C2CF1308FB98C78131B3BCF12DF5AC6DC51C","sealed-manifest-mismatch");
            Require(Sha(Path.Combine(candidate,"KnowledgeClipper.dll"))=="825A2B8716111907E83BECD949BFD858CE1C636AF9F51531D876785CF21E9120","sealed-dll-mismatch");
            Require(Process.GetProcessesByName("KnowledgeClipper").Length==0,"user-desktop-running-do-not-interfere");
            Directory.CreateDirectory(data);
            var source="植物的根有什么作用？根把植物固定在土壤中，并吸收水分和无机盐。";
            Save(evidence,"capture.json",new{schemaVersion="desktop-test-capture.v1",selectedText=source,prefix="",suffix=""});
            // This is the explicitly requested visible test UI, not a background
            // helper. Match the existing Actual runner's normal GUI startup.
            var start=new ProcessStartInfo(Path.Combine(candidate,"KnowledgeClipper.exe")){UseShellExecute=false};
            start.ArgumentList.Add("--open-settings");start.ArgumentList.Add("--candidate-capture-fixture");start.ArgumentList.Add(Path.Combine(evidence,"capture.json"));start.Environment["LOCALAPPDATA"]=data;
            phase="launch-settings";process=Process.Start(start)??throw new Exception("start-failed");Save(evidence,"process.json",new{process.Id,path=process.MainModule?.FileName,candidate,data});
            Wait(()=>{settings=Root(process.Id,Prefix+"window");return settings!=null;},35,"settings-window-missing");
            var navigation=Find(settings!,"navigation");var export=navigation.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"导出"))??throw new Exception("export-page-missing");((SelectionItemPattern)export.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Set(Find(settings!,"export.remnote.root-title"),Title);
            var discoverButton=Find(settings!,"export.remnote.discover-target");var discovered=Find(settings!,"export.remnote.discover-target-status");
            phase="remnote-discover-once";discover++;Invoke(discoverButton);
            Wait(()=>Value(discovered).Contains("唯一目标与证据已核实")||Value(discovered).StartsWith("未核实"),75,"discovery-timeout-no-retry");
            Save(evidence,"remnote-discovery-ui.json",State(discovered));
            Require(Value(discovered).Contains("唯一目标与证据已核实"),"remnote-discovery-not-verified-see-evidence");
            Require(Value(Find(settings!,"export.remnote.root-id"))==RootId,"discovered-root-outside-authorized-target");
            var probeButton=Find(settings!,"export.remnote.probe-readonly");var ready=Find(settings!,"export.remnote.probe-status");
            phase="remnote-probe-once";probe++;Invoke(probeButton);
            Wait(()=>Description(ready).StartsWith("ready=true")||(!Description(ready).Contains("observation=checking")&&Value(ready).StartsWith("未就绪")),75,"remnote-probe-timeout-no-retry");
            Save(evidence,"remnote-readiness-ui.json",State(ready));Require(Description(ready).StartsWith("ready=true"),"remnote-not-ready");
            phase="anki-draft-target";Set(Find(settings!,"export.anki.profile"),"账户 1");Choose(Find(settings!,"export.anki.deck-preset"),"custom");Set(Find(settings!,"export.anki.deck-routes"),JsonSerializer.Serialize(new Dictionary<string,string>{{"canary",Deck}}));Choose(Find(settings!,"export.anki.probe-route"),"canary");
            var ankiReady=Find(settings!,"export.anki.probe-status");phase="anki-probe-once";ankiProbe++;Invoke(Find(settings!,"export.anki.probe-readonly"));
            Wait(()=>Description(ankiReady).Contains("terminalState"),75,"anki-probe-timeout-no-retry");Save(evidence,"anki-readiness-ui.json",State(ankiReady));
            using(var result=JsonDocument.Parse(Description(ankiReady)))Require(result.RootElement.GetProperty("ready").GetBoolean()&&result.RootElement.GetProperty("route").GetString()=="canary","anki-not-ready");
            phase="save-verified-targets";Check(Find(settings!,"export.remnote"));Check(Find(settings!,"export.anki"));Invoke(Find(settings!,"save"));
            Wait(()=>Root(process.Id,Prefix+"window")==null,10,"settings-save-did-not-close");settings=null;
            status="PASS-REAL-DESKTOP-READINESS-VERIFIED-SAVE";
        }catch(Exception ex){failure=phase+":"+ex.GetType().Name+":"+ex.Message;Console.WriteLine(failure);File.WriteAllText(Path.Combine(evidence,"failure.txt"),ex.ToString());}
        finally{
            if(process is {HasExited:false}){
                if(settings!=null && settings.TryGetCurrentPattern(WindowPattern.Pattern,out var settingsWindow))((WindowPattern)settingsWindow).Close();
                var floating=Root(process.Id,"knowledge-clipper.desktop.floating-window");
                if(floating!=null && floating.TryGetCurrentPattern(WindowPattern.Pattern,out var window))((WindowPattern)window).Close();
                if(!process.WaitForExit(6000)){status="FAILED";failure+=";owned-desktop-did-not-exit";}
            }
            Save(evidence,"receipt.json",new{status,phase,failure,candidate,data,actualDesktopExe=true,remnoteDiscoveryInvocations=discover,remnoteProbeInvocations=probe,ankiProbeInvocations=ankiProbe,businessCaptureSubmitted=0,confirmationInvocations=0,externalWritesRequested=0,speaker=0,physicalInput=0,ownedDesktopExited=process==null||process.HasExited});process?.Dispose();
        }
        Console.WriteLine(status);return status.StartsWith("PASS")?0:1;
    }
}

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record PlaybackStatusObservation(
    [property:JsonPropertyName("source")]string Source,
    [property:JsonPropertyName("rawHelpText")]string RawHelpText,
    [property:JsonPropertyName("rawLegacyDescription")]string RawLegacyDescription,
    [property:JsonPropertyName("legacyPatternAvailable")]bool LegacyPatternAvailable,
    [property:JsonPropertyName("nativeText")]string NativeText,
    [property:JsonPropertyName("enabled")]bool Enabled,
    [property:JsonPropertyName("hwnd")]long Hwnd,
    [property:JsonPropertyName("ownerPid")]int OwnerPid,
    [property:JsonPropertyName("observedAt")]DateTimeOffset ObservedAt,
    [property:JsonPropertyName("status")]JsonElement Status,
    [property:JsonPropertyName("backendSubstitution")]bool BackendSubstitution=false);

internal static class PlaybackStatusReader
{
    internal const string StatusId="knowledge-clipper.desktop.status";
    internal const string Channel="actual-owned-UIAutomationCore-UiaGetPropertyValue(30090,30094)+native-WM_GETTEXT";
    [DllImport("UIAutomationCore.dll")]static extern int UiaNodeFromHandle(nint hwnd,out nint node);
    [DllImport("UIAutomationCore.dll")]static extern int UiaGetPropertyValue(nint node,int propertyId,[MarshalAs(UnmanagedType.Struct)]out object value);
    [DllImport("UIAutomationCore.dll")]static extern bool UiaNodeRelease(nint node);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(nint hwnd,out uint owner);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern nint SendMessageTimeout(nint hwnd,uint msg,nint wparam,StringBuilder text,uint flags,uint timeout,out nint length);
    static void Require(bool condition,string code){if(!condition)throw new InvalidOperationException(code);}

    internal static void RequireIdentity(int ownedPid,int uiaPid,int nativePid,long hwnd,string automationId,bool isButton,bool visible)
    {
        Require(ownedPid>0&&ownedPid==uiaPid&&ownedPid==nativePid&&hwnd!=0&&automationId==StatusId&&isButton&&visible,
            "playback.ui-status-owner-or-control-mismatch");
    }
    internal static PlaybackStatusObservation Read(AutomationElement element,int ownedPid)
    {
        var current=element.Current;var hwnd=new nint(current.NativeWindowHandle);
        GetWindowThreadProcessId(hwnd,out var nativeOwner);
        RequireIdentity(ownedPid,current.ProcessId,(int)nativeOwner,(long)hwnd,current.AutomationId,
            current.ControlType==ControlType.Button,!current.IsOffscreen&&IsWindowVisible(hwnd));
        Marshal.ThrowExceptionForHR(UiaNodeFromHandle(hwnd,out var node));
        Require(node!=0,"playback.ui-status-native-node-unavailable");
        try{
            Marshal.ThrowExceptionForHR(UiaGetPropertyValue(node,30090,out var available));
            Require(available is true,"playback.ui-status-legacy-pattern-missing");
            Marshal.ThrowExceptionForHR(UiaGetPropertyValue(node,30094,out var description));
            var status=ParseLegacyDescription(available,description);
            var text=new StringBuilder(8192);
            Require(SendMessageTimeout(hwnd,0x000D,new nint(text.Capacity),text,2,2000,out _)!=0,"playback.ui-status-native-text-unavailable");
            var after=element.Current;GetWindowThreadProcessId(hwnd,out var afterOwner);
            RequireIdentity(ownedPid,after.ProcessId,(int)afterOwner,after.NativeWindowHandle,after.AutomationId,
                after.ControlType==ControlType.Button,!after.IsOffscreen&&IsWindowVisible(hwnd));
            Require(after.NativeWindowHandle==current.NativeWindowHandle,"playback.ui-status-hwnd-changed");
            return new(Channel,current.HelpText,(string)description,true,text.ToString(),after.IsEnabled,(long)hwnd,ownedPid,
                DateTimeOffset.UtcNow,status);
        }finally{UiaNodeRelease(node);}
    }
    internal static JsonElement ParseLegacyDescription(object? available,object? description)
    {
        Require(available is true,"playback.ui-status-legacy-pattern-missing");
        Require(description is string,"playback.ui-status-legacy-description-type-invalid");
        var raw=(string)description!;Require(!string.IsNullOrWhiteSpace(raw),"playback.ui-status-legacy-description-empty");
        try{
            using var json=JsonDocument.Parse(raw);var value=json.RootElement;
            Require(value.ValueKind==JsonValueKind.Object,"playback.ui-status-object-required");
            foreach(var name in new[]{"OperationId","Playback","Pipeline"})
                Require(value.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(field.GetString()),"playback.ui-status-field-invalid:"+name);
            // The real Desktop clears its playback target during Replay refresh. This is
            // a valid pending snapshot, not terminal proof. Both fields must still exist.
            foreach(var name in new[]{"ActionCommandId","AttemptId"})
                Require(value.TryGetProperty(name,out var field)&&(field.ValueKind==JsonValueKind.Null||
                    field.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(field.GetString())),"playback.ui-status-field-invalid:"+name);
            Require((value.GetProperty("ActionCommandId").ValueKind==JsonValueKind.Null)==
                (value.GetProperty("AttemptId").ValueKind==JsonValueKind.Null),"playback.ui-status-partial-target");
            foreach(var name in new[]{"CanStop","CanReplay"})
                Require(value.TryGetProperty(name,out var field)&&field.ValueKind is JsonValueKind.True or JsonValueKind.False,"playback.ui-status-field-invalid:"+name);
            return value.Clone();
        }catch(JsonException ex){throw new InvalidOperationException("playback.ui-status-legacy-description-invalid-json",ex);}
    }
    internal static int SelfTest()
    {
        var cases=0;void Check(bool condition){if(!condition)throw new Exception("status-reader-test-failed");cases++;}
        void Reject(Action action){try{action();throw new Exception("status-reader-negative-accepted");}catch(InvalidOperationException){cases++;}}
        var raw=JsonSerializer.Serialize(new{OperationId="op",ActionCommandId="cmd",AttemptId="attempt",Playback="Playing",Pipeline="CreatingCards",CanStop=true,CanReplay=false});
        var observed=ParseLegacyDescription(true,raw);Check(observed.GetProperty("CanStop").GetBoolean());
        // Empty HelpText is retained, never treated as the Legacy value or used as fallback.
        var record=new PlaybackStatusObservation(Channel,"",raw,true,"朗读中 · 正在制卡",true,42,17,DateTimeOffset.UtcNow,observed);
        Check(record.RawHelpText==""&&record.RawLegacyDescription==raw&&!record.BackendSubstitution);
        Reject(()=>ParseLegacyDescription(false,raw));Reject(()=>ParseLegacyDescription("true",raw));
        Reject(()=>ParseLegacyDescription(true,null));Reject(()=>ParseLegacyDescription(true,""));
        Reject(()=>ParseLegacyDescription(true," "));Reject(()=>ParseLegacyDescription(true,"{"));
        Reject(()=>ParseLegacyDescription(true,"[]"));Reject(()=>ParseLegacyDescription(true,"{}"));
        Reject(()=>ParseLegacyDescription(true,raw.Replace("\"CanStop\":true","\"CanStop\":\"true\"",StringComparison.Ordinal)));
        RequireIdentity(17,17,17,42,StatusId,true,true);cases++;
        Reject(()=>RequireIdentity(17,18,17,42,StatusId,true,true));Reject(()=>RequireIdentity(17,17,18,42,StatusId,true,true));
        Reject(()=>RequireIdentity(17,17,17,0,StatusId,true,true));Reject(()=>RequireIdentity(17,17,17,42,"other",true,true));
        Reject(()=>RequireIdentity(17,17,17,42,StatusId,false,true));Reject(()=>RequireIdentity(17,17,17,42,StatusId,true,false));
        var pending=raw.Replace("\"cmd\"","null",StringComparison.Ordinal).Replace("\"attempt\"","null",StringComparison.Ordinal);
        Check(ParseLegacyDescription(true,pending).GetProperty("AttemptId").ValueKind==JsonValueKind.Null);
        Reject(()=>ParseLegacyDescription(true,raw.Replace("\"cmd\"","null",StringComparison.Ordinal)));
        Reject(()=>ParseLegacyDescription(true,raw.Replace("\"ActionCommandId\":\"cmd\",","",StringComparison.Ordinal)));
        Reject(()=>ParseLegacyDescription(true,raw.Replace("\"AttemptId\":\"attempt\"","\"AttemptId\":9",StringComparison.Ordinal)));
        return cases;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record UiTerminalCheckpoint(string LogPath,long Offset,DateTimeOffset NotBeforeUtc);
internal sealed record UiTerminalEvidence(string LogPath,string OperationId,long BaselineOffset,long LineOffset,DateTimeOffset RenderedAt,DateTimeOffset NotBeforeUtc,string LineSha256);
internal static class UiTerminalLog
{
    internal static UiTerminalCheckpoint Begin(string dataRoot)
    {
        var path=Path.Combine(Path.GetFullPath(dataRoot),"KnowledgeClipper","logs","desktop-ui.log");
        return new(path,File.Exists(path)?new FileInfo(path).Length:0,DateTimeOffset.UtcNow);
    }
    internal static UiTerminalEvidence? Read(UiTerminalCheckpoint checkpoint,string operationId,bool includeSpeech=true)
    {
        if(!File.Exists(checkpoint.LogPath))return null;
        using var stream=new FileStream(checkpoint.LogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var length=stream.Length;
        if(length<checkpoint.Offset)throw new InvalidOperationException("desktop-ui-log-truncated");
        if(length-checkpoint.Offset>4*1024*1024)throw new InvalidOperationException("desktop-ui-log-tail-limit");
        stream.Position=checkpoint.Offset;
        var bytes=new byte[(int)(length-checkpoint.Offset)];stream.ReadExactly(bytes);
        var start=0;
        var marker="READ phase=rendered-terminal pipeline=Completed playback="+(includeSpeech?"IdleReplayable":"NotRequested")+" busy=False operationId="+operationId;
        for(var index=0;index<bytes.Length;index++)
        {
            if(bytes[index]!=(byte)'\n')continue;
            var line=Encoding.UTF8.GetString(bytes,start,index-start).TrimEnd('\r');
            var separator=line.IndexOf(' ');
            if(separator>0&&line[(separator+1)..]==marker&&DateTimeOffset.TryParseExact(line[..separator],"o",CultureInfo.InvariantCulture,DateTimeStyles.None,out var timestamp)&&timestamp>=checkpoint.NotBeforeUtc&&timestamp<=DateTimeOffset.UtcNow)
                return new(checkpoint.LogPath,operationId,checkpoint.Offset,checkpoint.Offset+start,timestamp,checkpoint.NotBeforeUtc,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))));
            start=index+1;
        }
        return null;
    }
    internal static UiTerminalEvidence Wait(UiTerminalCheckpoint checkpoint,string operationId,TimeSpan timeout,Action heartbeat,bool includeSpeech=true)
    {
        var clock=Stopwatch.StartNew();
        do
        {
            var evidence=Read(checkpoint,operationId,includeSpeech);if(evidence is not null)return evidence;
            heartbeat();Thread.Sleep(100);
        }while(clock.Elapsed<timeout);
        throw new InvalidOperationException("desktop-ui-rendered-terminal-timeout");
    }
    internal static int SelfTest()
    {
        var root=Path.Combine(@"D:\Artifacts\KnowledgeClipper\read-to-anki-runner\ui-terminal-tests",DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"KnowledgeClipper","logs","desktop-ui.log");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string operation="offline-operation";
        static string Line(string operation,string playback="IdleReplayable",string busy="False",DateTimeOffset? timestamp=null)=>(timestamp??DateTimeOffset.UtcNow).ToString("o")+" READ phase=rendered-terminal pipeline=Completed playback="+playback+" busy="+busy+" operationId="+operation+Environment.NewLine;
        var checks=new List<bool>();
        File.WriteAllText(path,Line(operation),new UTF8Encoding(false));
        var checkpoint=Begin(root);checks.Add(Read(checkpoint,operation) is null);
        File.AppendAllText(path,Line("other-operation"));checks.Add(Read(checkpoint,operation) is null);
        File.AppendAllText(path,Line(operation,busy:"True"));checks.Add(Read(checkpoint,operation) is null);
        File.AppendAllText(path,Line(operation,playback:"Playing"));checks.Add(Read(checkpoint,operation) is null);
        File.AppendAllText(path,Line(operation,timestamp:checkpoint.NotBeforeUtc.AddMinutes(-1)));checks.Add(Read(checkpoint,operation) is null);
        var complete=Line(operation);File.AppendAllText(path,complete.TrimEnd('\r','\n'));checks.Add(Read(checkpoint,operation) is null);
        File.AppendAllText(path,Environment.NewLine);var found=Read(checkpoint,operation);
        checks.Add(found is not null&&found.LineOffset>=checkpoint.Offset&&found.OperationId==operation&&found.LineSha256.Length==64);
        checks.Add(Read(Begin(Path.Combine(root,"other-root")),operation) is null);
        try{_=Wait(Begin(root),operation,TimeSpan.FromMilliseconds(150),()=>{});checks.Add(false);}catch(InvalidOperationException ex){checks.Add(ex.Message=="desktop-ui-rendered-terminal-timeout");}
        var beforeTruncate=Begin(root);File.WriteAllText(path,"");
        try{_=Read(beforeTruncate,operation);checks.Add(false);}catch(InvalidOperationException ex){checks.Add(ex.Message=="desktop-ui-log-truncated");}
        if(checks.Any(value=>!value))throw new InvalidOperationException("desktop-ui-terminal-log-self-test-failed");
        var clipCheckpoint=Begin(root);
        File.AppendAllText(path,Line("previous-read"));
        if(Read(clipCheckpoint,"clip",false) is not null)throw new Exception("clip-accepted-prior-read-terminal");
        File.AppendAllText(path,Line("clip"));
        if(Read(clipCheckpoint,"clip",false) is not null)throw new Exception("clip-accepted-audio-terminal");
        File.AppendAllText(path,Line("clip",playback:"NotRequested"));
        if(Read(clipCheckpoint,"clip",false) is null||Read(Begin(root),"clip",false) is not null)throw new Exception("clip-own-ui-terminal-check-failed");
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",clipUiTerminalCases=4,physicalPlayback=false}));
        return checks.Count;
    }
}

// The observation thread may block in a pipe query. The external application's
// native edit/TextPattern provider must retain its own continuously pumped STA.
internal sealed class ExternalTextSource : IDisposable
{
    readonly Thread thread;
    readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Form? form;
    public nint EditHandle => ready.Task.GetAwaiter().GetResult();

    public ExternalTextSource(string text, int start, int length)
    {
        thread = new Thread(() =>
        {
            try
            {
                using var window = new Form { Text = "KnowledgeClipper Actual External Source", Width = 700, Height = 220 };
                using var edit = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = text };
                form = window;
                window.Controls.Add(edit);
                window.Shown += (_, _) => window.BeginInvoke(new Action(() =>
                {
                    window.Activate(); edit.Focus(); edit.Select(start, length);
                    ready.TrySetResult(edit.Handle);
                }));
                Application.Run(window);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "KnowledgeClipper.ExternalSource" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("source.message-loop-start-timeout");
        _ = EditHandle;
    }

    public void Dispose()
    {
        if (form is { IsDisposed: false, IsHandleCreated: true }) form.BeginInvoke(new Action(form.Close));
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("source.message-loop-close-timeout");
    }
}

internal static class ExternalTextSourceCanary
{
    delegate bool EnumWindowsProc(nint handle,nint state);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback,nint state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint handle,out uint processId);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint SendMessageTimeout(nint handle, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nint result);

    // Read-only probe of an explicitly supplied offline window-host PID. It does
    // not invoke buttons or access Desktop/Core business interfaces.
    internal static int ProbeChildIdentities(int processId,string automationId)
    {
        var candidates=new List<ChildWindowCandidate>();
        var observations=new List<object>();
        EnumWindows((handle,state)=>
        {
            GetWindowThreadProcessId(handle,out var owner);
            if(owner!=(uint)processId||!IsWindowVisible(handle))return true;
            var root=AutomationElement.FromHandle(handle);var current=root.Current;
            if(current.ProcessId!=processId||current.ControlType!=ControlType.Window||current.NativeWindowHandle!=unchecked((int)handle))return true;
            var condition=new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,processId),new PropertyCondition(AutomationElement.AutomationIdProperty,automationId));
            var matches=root.FindAll(TreeScope.Descendants,condition);
            for(var index=0;index<matches.Count;index++)
            {
                var candidate=ChildWindowObservation.Read(current.AutomationId,matches[index]);
                candidates.Add(candidate);
                var match=matches[index];var matchInfo=match.Current;
                var parent=TreeWalker.ControlViewWalker.GetParent(match);
                observations.Add(new{rootHandle=handle.ToInt64(),rootAutomationId=current.AutomationId,candidate.ProcessId,candidate.AutomationId,controlType=matchInfo.ControlType.ProgrammaticName,hwnd=candidate.NativeWindowHandle.ToInt64(),runtimeId=match.GetRuntimeId(),identity=ExactChildWindowSelector.Identity(candidate),visible=!matchInfo.IsOffscreen,enabled=matchInfo.IsEnabled,hasInvokePattern=match.TryGetCurrentPattern(InvokePattern.Pattern,out _),parent=parent is null?null:new{automationId=parent.Current.AutomationId,hwnd=parent.Current.NativeWindowHandle,runtimeId=parent.GetRuntimeId()}});
            }
            return true;
        },0);
        var unique=candidates.Select(ExactChildWindowSelector.Identity).Distinct(StringComparer.Ordinal).Count();
        string? rejection=null;
        try{_=ExactChildWindowSelector.Select(candidates,processId,automationId);}
        catch(InvalidOperationException ex){rejection=ex.Message;}
        Console.WriteLine(JsonSerializer.Serialize(new{status=rejection is null?"PASS":"REJECTED",processId,automationId,rawObservations=candidates.Count,uniqueIdentities=unique,duplicateObservations=candidates.Count-unique,observations,rejection,invocations=0,externalCalls=0,businessSubmissions=0}));
        return rejection is null?0:21;
    }

    internal static int Probe(nint handle)
    {
        if (SendMessageTimeout(handle, 0x000E, 0, 0, 2, 700, out _) == 0)
        { Console.WriteLine("source-message-loop-unresponsive"); return 21; }
        var element = AutomationElement.FromHandle(handle);
        var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
        var selection = pattern.GetSelection().Single();
        var prefix = pattern.DocumentRange.Clone(); prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        var suffix = pattern.DocumentRange.Clone(); suffix.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
        Console.WriteLine(JsonSerializer.Serialize(new { prefix = prefix.GetText(-1), selection = selection.GetText(-1), suffix = suffix.GetText(-1) }));
        return 0;
    }

    static (int Code, string Output) RunProbe(nint handle)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("--probe-text-source"); info.ArgumentList.Add(handle.ToInt64().ToString());
        using var process = Process.Start(info)!;
        // A managed STA wait can dispatch COM/window calls and accidentally make
        // the legacy host responsive. A native wait deliberately does not pump.
        if (WaitForSingleObject(process.Handle, 8000) != 0) { process.Kill(); throw new TimeoutException("source-probe-timeout"); }
        var error = process.StandardError.ReadToEnd();
        if (error.Length != 0) Console.Error.WriteLine(error);
        return (process.ExitCode, process.StandardOutput.ReadToEnd());
    }

    internal static int Run()
    {
        using (var legacy = new Form())
        {
            var edit = new TextBox { Text = "blocked", Multiline = true }; legacy.Controls.Add(edit);
            legacy.Show(); Application.DoEvents();
            var blocked = RunProbe(edit.Handle);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "legacy-unpumped", exitCode = blocked.Code, output = blocked.Output }));
            if (blocked.Code != 21) throw new InvalidOperationException("legacy-unpumped-source-regression-not-reproduced");
            legacy.Close();
        }
        foreach (var target in new[] { "deliberate", "consideration" })
        {
            var prefix = "A reader gives "; var suffix = " to this sentence.";
            using var source = new ExternalTextSource(prefix + target + suffix, prefix.Length, target.Length);
            // Deliberately block this (runner/observer) STA while a different process
            // calls the real Windows provider; no DoEvents can rescue this test.
            var observed = RunProbe(source.EditHandle);
            if (observed.Code != 0) throw new InvalidOperationException("pumped-source-probe-failed:" + observed.Output);
            using var result = JsonDocument.Parse(observed.Output);
            if (result.RootElement.GetProperty("prefix").GetString() != prefix || result.RootElement.GetProperty("selection").GetString() != target || result.RootElement.GetProperty("suffix").GetString() != suffix)
                throw new InvalidOperationException("source-role-content-mismatch");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "independent-sta", prefix, selection = target, suffix, exactMatch = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", legacyUnpumpedSourceRejected = true, independentStaSourceCases = 2, realCrossProcessTextPattern = true, externalCalls = 0, businessSubmissions = 0 }));
        return 0;
    }
}

param(
 [Parameter(Mandatory=$true)][string]$Candidate,
 [Parameter(Mandatory=$true)][string]$EvidenceRoot
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if(Test-Path -LiteralPath $EvidenceRoot){throw 'append-only-evidence-root-exists'}
[IO.Directory]::CreateDirectory($EvidenceRoot)|Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$native=@'
using System;using System.Collections.Generic;using System.Runtime.InteropServices;
public static class CandidateUiaNative{
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc p,IntPtr l);
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr h);
 public static IntPtr[] Top(){var x=new List<IntPtr>();EnumWindows((h,l)=>{x.Add(h);return true;},IntPtr.Zero);return x.ToArray();}
}
'@
Add-Type -TypeDefinition $native
$candidate=[IO.Path]::GetFullPath($Candidate);$exe=Join-Path $candidate 'KnowledgeClipper.exe';if(!(Test-Path $exe)){throw 'candidate-exe-missing'}
$data=Join-Path $EvidenceRoot 'localappdata';[IO.Directory]::CreateDirectory($data)|Out-Null
$psi=[Diagnostics.ProcessStartInfo]::new($exe);$psi.UseShellExecute=$false;$psi.ArgumentList.Add('--open-settings');$psi.Environment['LOCALAPPDATA']=$data
$process=[Diagnostics.Process]::Start($psi);if($null-eq$process){throw 'candidate-start-failed'}
function Uia([IntPtr]$h){try{[Windows.Automation.AutomationElement]::FromHandle($h)}catch{$null}}
function Node([Windows.Automation.AutomationElement]$e,[int]$depth){
 $c=$e.Current;$type=$c.ControlType.ProgrammaticName;$name=if($type-match'Edit|Document'){'[redacted-control-name]'}else{$c.Name}
 $o=[ordered]@{depth=$depth;name=$name;automationId=$c.AutomationId;className=$c.ClassName;controlType=$type;nativeWindowHandle=$c.NativeWindowHandle;processId=$c.ProcessId}
 if($depth-lt3){$children=@();$child=[Windows.Automation.TreeWalker]::ControlViewWalker.GetFirstChild($e);while($null-ne$child){$children+=,(Node $child ($depth+1));$child=[Windows.Automation.TreeWalker]::ControlViewWalker.GetNextSibling($child)};$o.children=$children}
 [pscustomobject]$o
}
try{
 Start-Sleep -Seconds 4;$launcherPid=$process.Id
 $samePath=@(Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath-and[IO.Path]::GetFullPath($_.ExecutablePath)-ieq$exe}|ForEach-Object{[ordered]@{pid=[int]$_.ProcessId;parentPid=[int]$_.ParentProcessId;executablePath=$_.ExecutablePath;commandLine=$_.CommandLine}})
 $candidatePids=@($samePath|ForEach-Object{$_.pid});if($launcherPid-notin$candidatePids){$candidatePids+=,$launcherPid}
 $windows=@();foreach($h in [CandidateUiaNative]::Top()){[uint32]$ownerProcessId=0;[void][CandidateUiaNative]::GetWindowThreadProcessId($h,[ref]$ownerProcessId);if([int]$ownerProcessId-notin$candidatePids){continue};$e=Uia $h;if($null-eq$e){continue};$c=$e.Current;$windows+=,[ordered]@{hwnd=('0x'+$h.ToInt64().ToString('X'));ownerPid=[int]$ownerProcessId;visible=[CandidateUiaNative]::IsWindowVisible($h);name=$c.Name;automationId=$c.AutomationId;className=$c.ClassName;controlType=$c.ControlType.ProgrammaticName;nativeWindowHandle=$c.NativeWindowHandle;uiaProcessId=$c.ProcessId;subtree=(Node $e 0)}}
 $allTop=@();$root=[Windows.Automation.AutomationElement]::RootElement;$top=[Windows.Automation.TreeWalker]::ControlViewWalker.GetFirstChild($root);while($null-ne$top){$c=$top.Current;if($c.ProcessId-in$candidatePids){$allTop+=,[ordered]@{name=$c.Name;automationId=$c.AutomationId;className=$c.ClassName;controlType=$c.ControlType.ProgrammaticName;nativeWindowHandle=$c.NativeWindowHandle;processId=$c.ProcessId}};$top=[Windows.Automation.TreeWalker]::ControlViewWalker.GetNextSibling($top)}
 $result=[ordered]@{schemaVersion='knowledge-clipper-targeted-uia-pid-diagnostic.v1';candidate=$candidate;launcherPid=$launcherPid;launcherExited=$process.HasExited;samePathProcesses=$samePath;candidatePids=$candidatePids;enumWindows=$windows;uiaTopLevel=$allTop;expected=[ordered]@{formName='knowledge-clipper.desktop.floating-window';accessibleName='knowledge-clipper.desktop.floating-window';contractAutomationId='knowledge-clipper.desktop.floating-window'};businessActions=0;externalCalls=0}
 $path=Join-Path $EvidenceRoot 'diagnostic.json';[IO.File]::WriteAllText($path,($result|ConvertTo-Json -Depth 12)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false));Write-Output $path
}finally{
 if(!$process.HasExited){$process.CloseMainWindow()|Out-Null;if(!$process.WaitForExit(3000)){$process.Kill($true);$process.WaitForExit()}}
}

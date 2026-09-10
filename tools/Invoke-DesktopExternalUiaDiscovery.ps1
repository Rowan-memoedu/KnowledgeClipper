param([Parameter(Mandatory=$true)][string]$CandidateRoot,[Parameter(Mandatory=$true)][string]$DataRoot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class KcWindowEnum {
 public delegate bool EnumWindowsProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h,uint c);
 public static string[] ForPid(int pid){var r=new List<string>();EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid){var t=new StringBuilder(512);var c=new StringBuilder(256);GetWindowText(h,t,t.Capacity);GetClassName(h,c,c.Capacity);r.Add($"hwnd={h.ToInt64()} visible={IsWindowVisible(h)} owner={GetWindow(h,4).ToInt64()} class={c} title={t}");}return true;},IntPtr.Zero);return r.ToArray();}
}
'@
function Dump([Windows.Automation.AutomationElement]$e,[int]$depth,[int]$limit,[ref]$count){if($null-eq $e-or $depth-gt 7-or $count.Value-ge $limit){return};$count.Value++;[ordered]@{depth=$depth;controlType=$e.Current.ControlType.ProgrammaticName;name=$e.Current.Name;automationId=$e.Current.AutomationId;hwnd=$e.Current.NativeWindowHandle}|ConvertTo-Json -Compress;foreach($child in $e.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)){Dump $child ($depth+1) $limit $count}}
[IO.Directory]::CreateDirectory($DataRoot)|Out-Null
$psi=[Diagnostics.ProcessStartInfo]::new((Join-Path $CandidateRoot 'KnowledgeClipper.exe'));$psi.UseShellExecute=$false;$psi.Environment['LOCALAPPDATA']=$DataRoot;$p=[Diagnostics.Process]::Start($psi)
try{$root=[Windows.Automation.AutomationElement]::RootElement;$wins=$null;for($wait=0;$wait-lt 20;$wait++){Start-Sleep -Seconds 1;$wins=$root.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id));if($wins.Count-gt 0){break}};"PID=$($p.Id)";[KcWindowEnum]::ForPid($p.Id);"UIA_TOP_LEVEL_COUNT=$($wins.Count)";for($i=0;$i-lt $wins.Count;$i++){"UIA_WINDOW_INDEX=$i";$count=0;Dump $wins[$i] 0 120 ([ref]$count)}}finally{if(-not $p.HasExited){if(-not $p.CloseMainWindow()-or-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}}

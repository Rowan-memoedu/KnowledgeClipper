[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string[]]$Arguments, [int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IsolatedUiTest {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct Startup {
        public int cb; public string reserved, desktop, title; public int x,y,width,height,xChars,yChars,fill,flags;
        public short show,reservedSize; public IntPtr reservedPointer,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] public struct ProcessInfo { public IntPtr process,thread; public int id,threadId; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,int flags,int access,IntPtr security);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool CreateProcess(string app,System.Text.StringBuilder args,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,int flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo info);
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle,uint ms);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr process,out int code);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr process,int code);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
function Quote-Argument([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
$name = 'KnowledgeClipperUiTests_' + [guid]::NewGuid().ToString('N')
$desktop = [IsolatedUiTest]::CreateDesktop($name, [IntPtr]::Zero, [IntPtr]::Zero, 0, 0x01ff, [IntPtr]::Zero)
if ($desktop -eq [IntPtr]::Zero) { throw [ComponentModel.Win32Exception]::new() }
$startup = [IsolatedUiTest+Startup]::new()
$startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
$startup.desktop = $name
$startup.flags = 0x100
$startup.input = [IsolatedUiTest]::GetStdHandle(-10)
$startup.output = [IsolatedUiTest]::GetStdHandle(-11)
$startup.error = [IsolatedUiTest]::GetStdHandle(-12)
$process = [IsolatedUiTest+ProcessInfo]::new()
$previous = $env:KNOWLEDGECLIPPER_ISOLATED_UI_TEST
try {
    $env:KNOWLEDGECLIPPER_ISOLATED_UI_TEST = '1'
    $quoted = @((Quote-Argument $Executable)) + @($Arguments | ForEach-Object { Quote-Argument $_ })
    $commandLine = [Text.StringBuilder]::new(($quoted -join ' '))
    if (-not [IsolatedUiTest]::CreateProcess($Executable, $commandLine, [IntPtr]::Zero, [IntPtr]::Zero, $true, 0x08000000, [IntPtr]::Zero, (Get-Location).Path, [ref]$startup, [ref]$process)) {
        throw [ComponentModel.Win32Exception]::new()
    }
    if ([IsolatedUiTest]::WaitForSingleObject($process.process, $TimeoutSeconds * 1000) -ne 0) {
        [IsolatedUiTest]::TerminateProcess($process.process, 124) | Out-Null
        throw "UI test exceeded $TimeoutSeconds seconds."
    }
    $code = 0
    [IsolatedUiTest]::GetExitCodeProcess($process.process, [ref]$code) | Out-Null
    exit $code
} finally {
    $env:KNOWLEDGECLIPPER_ISOLATED_UI_TEST = $previous
    if ($process.thread -ne [IntPtr]::Zero) { [IsolatedUiTest]::CloseHandle($process.thread) | Out-Null }
    if ($process.process -ne [IntPtr]::Zero) { [IsolatedUiTest]::CloseHandle($process.process) | Out-Null }
    [IsolatedUiTest]::CloseDesktop($desktop) | Out-Null
}

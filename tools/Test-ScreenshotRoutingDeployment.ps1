[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Receipt,
    [Parameter(Mandatory)][string]$ExpectedSettingsSha256
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public static class ClipperDeploymentWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    private delegate bool EachWindow(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EachWindow callback,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent,EachWindow callback,IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out int process);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    public static IntPtr[] Windows(int process) { var result=new List<IntPtr>(); EnumWindows((window,parameter)=>{ int found; GetWindowThreadProcessId(window,out found); if(found==process) result.Add(window); return true; },IntPtr.Zero); return result.ToArray(); }
    public static IntPtr[] Children(IntPtr parent) { var result=new List<IntPtr>(); EnumChildWindows(parent,(window,parameter)=>{result.Add(window);return true;},IntPtr.Zero);return result.ToArray(); }
    public static string Text(int handle) { var text = new StringBuilder(512); GetWindowText((IntPtr)handle,text,text.Capacity); return text.ToString(); }
}
'@
$running = @(Get-Process -Name KnowledgeClipper -ErrorAction SilentlyContinue)
if ($running.Count -ne 1 -or $running[0].Path -ne $Executable) { throw 'The expected deployment must be the only running KnowledgeClipper process.' }
$clipperProcess = $running[0]
$main = $null
for ($attempt = 0; $attempt -lt 20 -and $null -eq $main; $attempt++) {
    $windows = @([ClipperDeploymentWindow]::Windows($clipperProcess.Id) | ForEach-Object { [Windows.Automation.AutomationElement]::FromHandle($_) })
    foreach ($window in $windows) {
        if ($window.Current.AutomationId -eq 'knowledge-clipper.desktop.floating-window' -or $window.Current.Name -eq 'knowledge-clipper.desktop.floating-window') { $main = $window; break }
    }
    if ($null -eq $main) { Start-Sleep -Milliseconds 500 }
}
if ($null -eq $main) { throw 'The running floating window was not found.' }
$windowHandle = [IntPtr]$main.Current.NativeWindowHandle
# Native children remain readable when the user has hidden the floating window to the tray.
$controls = @([ClipperDeploymentWindow]::Children($windowHandle) | ForEach-Object { [Windows.Automation.AutomationElement]::FromHandle($_) } | ForEach-Object {
    [pscustomobject]@{Id=$_.Current.AutomationId; Name=$_.Current.Name; Caption=[ClipperDeploymentWindow]::Text($_.Current.NativeWindowHandle); Type=$_.Current.ControlType.ProgrammaticName; Help=$_.Current.HelpText; Enabled=$_.Current.IsEnabled}
})
foreach ($name in @('摘录','语言','截图')) { if ($name -notin $controls.Caption) { throw "Missing actual button: $name" } }
if (@($controls | Where-Object Type -eq 'ControlType.ComboBox').Count -ne 0) { throw 'The obsolete inline route selector is still present.' }
if (-not ($controls | Where-Object { $_.Type -eq 'ControlType.Edit' -and $_.Help -like '*问号结尾提问，否则翻译*' })) { throw 'The default typed-input instruction is not present.' }
$settingsPath = 'D:\AppData\KnowledgeClipper\language-settings.json'
$settingsHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
if ($settingsHash -ne $ExpectedSettingsSha256) { throw 'Existing personal settings changed during deployment.' }
$wsh = New-Object -ComObject WScript.Shell
try { $shortcut = $wsh.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'KnowledgeClipper.lnk')); $target = $shortcut.TargetPath }
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($wsh) }
if ($target -ne $Executable) { throw 'Desktop shortcut does not point to the running build.' }
$visible = [ClipperDeploymentWindow]::IsWindowVisible($windowHandle)
$screenshot = $null
if ($visible) {
    $rect = [ClipperDeploymentWindow+Rect]::new()
    if (-not [ClipperDeploymentWindow]::GetWindowRect($windowHandle,[ref]$rect)) { throw 'Cannot read window bounds.' }
    $bitmap = [Drawing.Bitmap]::new(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { if (-not [ClipperDeploymentWindow]::PrintWindow($windowHandle,$dc,2)) { throw 'Cannot read the running window render.' } }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    $screenshot = Join-Path (Split-Path -Parent $Receipt) 'deployed-window.png'
    try { $bitmap.Save($screenshot,[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
$base = Split-Path -Parent $Executable
$provider = (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).mineruScriptPath
if ([string]::IsNullOrWhiteSpace($provider)) {
    $provider = (Get-Content -LiteralPath (Join-Path $base 'appsettings.json') -Raw | ConvertFrom-Json).MineruScriptPath
}
$provider = [Environment]::ExpandEnvironmentVariables($provider)
if ([string]::IsNullOrWhiteSpace($provider) -or -not (Test-Path -LiteralPath $provider -PathType Leaf)) {
    throw 'The configured MinerU runner cannot be verified.'
}
[ordered]@{
    passed=$true; observedAt=(Get-Date).ToString('o'); processId=$clipperProcess.Id; executable=$clipperProcess.Path;
    shortcutTarget=$target; settingsUnchanged=$true; settingsSha256=$settingsHash;
    windowVisible=$visible;
    assemblySha256=(Get-FileHash -LiteralPath (Join-Path $base 'KnowledgeClipper.dll') -Algorithm SHA256).Hash;
    workflowSha256=(Get-FileHash -LiteralPath (Join-Path $base 'KnowledgeClipper.Workflow.dll') -Algorithm SHA256).Hash;
    mineruRunner=$provider; mineruRunnerSha256=(Get-FileHash -LiteralPath $provider -Algorithm SHA256).Hash;
    mineruBridgeSha256=(Get-FileHash -LiteralPath (Join-Path $base 'helpers\mineru_bridge.py') -Algorithm SHA256).Hash;
    mineruDirectSha256=(Get-FileHash -LiteralPath (Join-Path $base 'helpers\mineru_direct.py') -Algorithm SHA256).Hash;
    controls=$controls; screenshot=$screenshot;
    scope='Read-only inspection of the running process, native controls, shortcut and preserved settings. Rendering is captured only when already visible; a hidden window is not activated. No clicks or input.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Receipt -Encoding utf8
Write-Output "PASS deployed screenshot routing: process, controls, placeholder, shortcut and unchanged personal settings"

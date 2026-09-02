[CmdletBinding()]
param(
    [string]$ReleaseRoot = "releases/1.0.0-rc.1-preseal.5/win-x64",
    [string]$BuildReceipt = "dist/desktop-win-x64/desktop-v1-20260825T160000Z/desktop-build-receipt.v1.json",
    [string]$ReceiptPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
function Resolve-ProjectPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}
function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}
function Get-RelativeProjectPath([string]$Path) {
    return [IO.Path]::GetRelativePath($projectRoot, $Path).Replace('\', '/')
}

if (-not ("DesktopClickabilityNative" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopClickabilityNative
{
    public sealed class WindowInfo
    {
        public long Handle { get; set; }
        public int ProcessId { get; set; }
        public string Title { get; set; }
        public bool Visible { get; set; }
        public bool NoActivate { get; set; }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);

    public static WindowInfo[] VisibleTopLevelWindows(int processId)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        var result = new List<WindowInfo>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr unused) {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner != (uint)processId || !IsWindowVisible(hWnd)) return true;
            int length = GetWindowTextLength(hWnd);
            var text = new StringBuilder(length + 1);
            GetWindowText(hWnd, text, text.Capacity);
            result.Add(new WindowInfo {
                Handle = hWnd.ToInt64(),
                ProcessId = processId,
                Title = text.ToString(),
                Visible = true,
                NoActivate = (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0
            });
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@
}

$root = Resolve-ProjectPath $ReleaseRoot
$buildReceiptPath = Resolve-ProjectPath $BuildReceipt
$desktopRoot = Join-Path $root "desktop"
$exe = Join-Path $desktopRoot "RemNoteQuickClipper.exe"
$appSettingsPath = Join-Path $desktopRoot "appsettings.json"
$sourceProgram = Join-Path $projectRoot "src/Program.cs"

foreach ($required in @($root, $desktopRoot, $exe, $appSettingsPath, $buildReceiptPath, $sourceProgram)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "required path missing: $required" }
}

$expectedExeSha256 = "D6A478C72462BB86916F3C5899DB0DBED13F56FD0A7D154A778A08F265108772"
$expectedBuildReceiptSha256 = "071A05F1CDFB9D8DB03AD396046D78140508184A2059C6194B66BC51C269BAC1"
$exeSha256 = Get-Sha256 $exe
$buildReceiptSha256 = Get-Sha256 $buildReceiptPath
if ($exeSha256 -ne $expectedExeSha256) { throw "desktop executable hash mismatch" }
if ($buildReceiptSha256 -ne $expectedBuildReceiptSha256) { throw "desktop build receipt hash mismatch" }

$bytes = [IO.File]::ReadAllBytes($exe)
if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw "desktop entrypoint is not a PE executable" }
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
if ($peOffset -lt 0 -or $peOffset + 96 -ge $bytes.Length) { throw "invalid PE header offset" }
$machineValue = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
$optionalHeader = $peOffset + 24
$optionalMagic = [BitConverter]::ToUInt16($bytes, $optionalHeader)
$subsystemValue = [BitConverter]::ToUInt16($bytes, $optionalHeader + 68)
$machine = if ($machineValue -eq 0x8664) { "Amd64" } else { "0x{0:X4}" -f $machineValue }
$subsystem = if ($subsystemValue -eq 2) { "WindowsGui" } else { "value-$subsystemValue" }
if ($machine -ne "Amd64" -or $subsystem -ne "WindowsGui" -or $optionalMagic -ne 0x20b) {
    throw "unexpected desktop PE target: machine=$machine subsystem=$subsystem magic=$optionalMagic"
}

$build = Get-Content -LiteralPath $buildReceiptPath -Raw | ConvertFrom-Json
$missing = [Collections.Generic.List[object]]::new()
$mismatches = [Collections.Generic.List[object]]::new()
foreach ($file in $build.files) {
    $candidate = Join-Path $desktopRoot ([string]$file.path)
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        $missing.Add([ordered]@{ path = $file.path })
        continue
    }
    $actualHash = Get-Sha256 $candidate
    if ($actualHash -ne ([string]$file.sha256).ToUpperInvariant()) {
        $classification = if ($file.path -eq "appsettings.json") { "expected-preseal-runtime-path-migration" } else { "unexpected" }
        $mismatches.Add([ordered]@{ path = $file.path; classification = $classification; candidateSha256 = $actualHash })
    }
}
$unexpectedMismatches = @($mismatches | Where-Object { $_.classification -eq "unexpected" })
if ($missing.Count -ne 0 -or $unexpectedMismatches.Count -ne 0 -or $mismatches.Count -ne 1 -or $mismatches[0].path -ne "appsettings.json") {
    throw "desktop dependency inventory differs beyond the expected appsettings migration"
}

$settings = Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json
$runtimeProperties = @("NodePath", "RemnoteCliJsPath", "PythonPath", "EdgeTtsPath", "FfplayPath")
$runtimeChecks = foreach ($property in $runtimeProperties) {
    $value = [string]$settings.$property
    $full = [IO.Path]::GetFullPath($value)
    $inside = $full.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
    [ordered]@{
        property = $property
        path = $full
        exists = Test-Path -LiteralPath $full -PathType Leaf
        insideReleaseRoot = $inside
    }
}
if (@($runtimeChecks | Where-Object { -not $_.exists -or -not $_.insideReleaseRoot }).Count -ne 0) {
    throw "configured desktop runtime dependency is missing or outside the release root"
}

$sourceText = Get-Content -LiteralPath $sourceProgram -Raw
$mutexSourceVerified = $sourceText.Contains('Local\\RemNoteQuickClipper.SingleInstance')
if (-not $mutexSourceVerified) { throw "single-instance source boundary not found" }

function Get-DesktopProcesses {
    $items = [Collections.Generic.List[object]]::new()
    foreach ($process in @(Get-Process -Name "RemNoteQuickClipper" -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $process.Path } catch { }
        $windows = @([DesktopClickabilityNative]::VisibleTopLevelWindows($process.Id))
        $items.Add([ordered]@{
            processId = $process.Id
            path = $path
            exactCandidate = ($path -and ([IO.Path]::GetFullPath($path) -eq [IO.Path]::GetFullPath($exe)))
            visibleWindows = $windows
        })
    }
    return @($items)
}

$preExisting = @(Get-DesktopProcesses)
$started = $null
$reachedInputIdle = $false
$ownedWindows = @()
$candidateExited = $false
$candidateExitCode = $null
$startedProcessStopped = $false
$classification = "failed"
$status = "failed"

$existingExact = @($preExisting | Where-Object { $_.exactCandidate -and @($_.visibleWindows).Count -gt 0 })
if ($existingExact.Count -gt 0) {
    $classification = "exact-candidate-instance-already-visible"
    $status = "passed"
    $ownedWindows = @($existingExact[0].visibleWindows)
} else {
    try {
        $started = Start-Process -FilePath $exe -WorkingDirectory $desktopRoot -PassThru -WindowStyle Normal
        try { $reachedInputIdle = $started.WaitForInputIdle(5000) } catch { }
        Start-Sleep -Milliseconds 1500
        $started.Refresh()
        $candidateExited = $started.HasExited
        if ($candidateExited) {
            $candidateExitCode = $started.ExitCode
        } else {
            $ownedWindows = @([DesktopClickabilityNative]::VisibleTopLevelWindows($started.Id))
        }

        if ($ownedWindows.Count -gt 0) {
            $classification = "candidate-owned-visible-window-observed"
            $status = "passed"
        } elseif ($candidateExited -and $preExisting.Count -gt 0) {
            $classification = "conditional-existing-global-single-instance"
            $status = "conditional"
        } elseif ($candidateExited) {
            $classification = "candidate-exited-without-preexisting-instance"
        } else {
            $classification = "candidate-running-without-visible-window"
        }
    } finally {
        if ($started) {
            try {
                $started.Refresh()
                if (-not $started.HasExited) {
                    [void]$started.CloseMainWindow()
                    if (-not $started.WaitForExit(3000)) { $started.Kill($true); $started.WaitForExit(3000) }
                    $startedProcessStopped = $started.HasExited
                }
            } catch {
                if (-not $started.HasExited) { $started.Kill($true); $started.WaitForExit(3000) }
                $startedProcessStopped = $started.HasExited
            }
        }
    }
}

$result = [ordered]@{
    schemaVersion = "desktop-clickability-canary-receipt.v1"
    createdAt = [DateTime]::UtcNow.ToString("o")
    status = $status
    scope = "exact unsealed preseal desktop entrypoint; no product behavior or production API"
    releaseRoot = Get-RelativeProjectPath $root
    desktopExecutable = [ordered]@{
        path = Get-RelativeProjectPath $exe
        bytes = (Get-Item -LiteralPath $exe).Length
        sha256 = $exeSha256
        machine = $machine
        subsystem = $subsystem
        authenticodeStatus = (Get-AuthenticodeSignature -LiteralPath $exe).Status.ToString()
    }
    dependencies = [ordered]@{
        buildReceiptPath = Get-RelativeProjectPath $buildReceiptPath
        buildReceiptSha256 = $buildReceiptSha256
        filesChecked = @($build.files).Count
        missingFiles = $missing.Count
        hashMismatches = @($mismatches)
        runtimePathsChecked = @($runtimeChecks).Count
        runtimePathsExistingInsideRoot = @($runtimeChecks | Where-Object { $_.exists -and $_.insideReleaseRoot }).Count
        runtimeChecks = @($runtimeChecks)
    }
    launchProbe = [ordered]@{
        classification = $classification
        candidateProcessStarted = $null -ne $started
        candidateProcessId = if ($started) { $started.Id } else { $null }
        reachedInputIdle = $reachedInputIdle
        candidateExitedDuringProbe = $candidateExited
        candidateExitCode = $candidateExitCode
        candidateOwnedVisibleWindows = @($ownedWindows)
        preExistingInstances = @($preExisting)
        singleInstanceSourceVerified = $mutexSourceVerified
        preExistingProcessesStopped = $false
        startedProcessStoppedAfterProbe = $startedProcessStopped
    }
    independentCandidateWindow = if ($status -eq "passed") { "verified" } else { "not-verified-while-another-instance-owns-global-mutex" }
    releaseManifest = $null
    candidateHash = $null
    productBehaviorModified = $false
    externalProductionApisInvoked = $false
}

$json = $result | ConvertTo-Json -Depth 12
if ($ReceiptPath) {
    $absoluteReceipt = Resolve-ProjectPath $ReceiptPath
    $parent = Split-Path -Parent $absoluteReceipt
    if (-not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent) }
    $utf8 = [Text.UTF8Encoding]::new($false)
    $stream = [IO.File]::Open($absoluteReceipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, $utf8)
        try { $writer.Write($json + "`n") } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

$json
if ($status -eq "failed") { exit 1 }

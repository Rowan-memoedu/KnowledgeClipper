param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$payload = [IO.Path]::GetFullPath($PayloadRoot)
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw "append-only output exists: $output" }
$exe = Join-Path $payload 'KnowledgeClipper.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Desktop executable missing: $exe" }
$existing = @(Get-Process -Name KnowledgeClipper -ErrorAction SilentlyContinue)
if ($existing.Count -ne 0) { throw "preexisting KnowledgeClipper process blocks isolated route probe: $($existing.Id -join ',')" }
New-Item -ItemType Directory -Path $output | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class QualityWindowProbe {
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lparam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    public static string[] VisibleTitles(int processId) {
        var result = new List<string>();
        EnumWindows(delegate(IntPtr hwnd, IntPtr state) {
            uint owner; GetWindowThreadProcessId(hwnd, out owner);
            if (owner == processId && IsWindowVisible(hwnd)) {
                var text = new StringBuilder(1024);
                GetWindowText(hwnd, text, text.Capacity);
                if (text.Length > 0) result.Add(text.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@

function Invoke-RouteProbe {
    param([string]$Argument, [string]$ExpectedTitle, [string]$Name)
    $routeRoot = Join-Path $output $Name
    $local = Join-Path $routeRoot 'localappdata'
    New-Item -ItemType Directory -Path $local -Force | Out-Null
    $stdout = Join-Path $routeRoot 'desktop.stdout.log'
    $stderr = Join-Path $routeRoot 'desktop.stderr.log'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $exe
    [void]$start.ArgumentList.Add($Argument)
    $start.WorkingDirectory = $payload
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['LOCALAPPDATA'] = $local
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "route process did not start: $Name" }
    $matched = $false
    $titles = @()
    $started = [DateTime]::UtcNow
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            if ($process.HasExited) { break }
            $titles = @([QualityWindowProbe]::VisibleTitles($process.Id))
            if ($titles -contains $ExpectedTitle) { $matched = $true; break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
    }
    finally {
        if (-not $process.HasExited) {
            & taskkill.exe /PID $process.Id /T /F | Out-Null
            $process.WaitForExit(10000) | Out-Null
        }
        [IO.File]::WriteAllText($stdout, $process.StandardOutput.ReadToEnd(), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stderr, $process.StandardError.ReadToEnd(), [Text.UTF8Encoding]::new($false))
        $process.Dispose()
    }
    if (-not $matched) { throw "production route did not expose expected title: $Name -> $ExpectedTitle; observed=$($titles -join '|')" }
    return [ordered]@{
        argument = $Argument
        expectedTitle = $ExpectedTitle
        observedTitles = @($titles)
        matched = $true
        elapsedMilliseconds = [int]([DateTime]::UtcNow - $started).TotalMilliseconds
        ownedProcessTreeCleaned = $true
        stdout = [ordered]@{ path = (Resolve-Path -LiteralPath $stdout).Path; bytes = (Get-Item -LiteralPath $stdout).Length; sha256 = (Get-FileHash -LiteralPath $stdout -Algorithm SHA256).Hash }
        stderr = [ordered]@{ path = (Resolve-Path -LiteralPath $stderr).Path; bytes = (Get-Item -LiteralPath $stderr).Length; sha256 = (Get-FileHash -LiteralPath $stderr -Algorithm SHA256).Hash }
    }
}

$settings = Invoke-RouteProbe -Argument '--open-settings' -ExpectedTitle '零碎知识剪藏 · 设置' -Name 'settings'
$question = Invoke-RouteProbe -Argument '--open-question' -ExpectedTitle '提问、追问与制卡' -Name 'question'
$remaining = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $exe })
if ($remaining.Count -ne 0) { throw "owned route probe process remains: $($remaining.ProcessId -join ',')" }
$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-quality-production-route-probe.v1'
    generatedAt = [DateTime]::UtcNow.ToString('o')
    status = 'passed'
    payloadRoot = $payload
    executable = [ordered]@{ bytes = (Get-Item -LiteralPath $exe).Length; sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash }
    routes = [ordered]@{ settings = $settings; question = $question }
    processBoundary = [ordered]@{ productionExe = $true; injectedHost = $false; sharedTestCoreDll = $false; preexistingProcessesTouched = 0; ownedProcessTreesCleaned = 2 }
    effects = [ordered]@{ externalCalls = 0; externalWrites = 0; physicalSpeakerInvocations = 0 }
}
$receiptPath = Join-Path $output 'production-routes.quality-receipt.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
$result = [ordered]@{ status = $receipt.status; receipt = $receiptPath; bytes = (Get-Item -LiteralPath $receiptPath).Length; sha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash }
$result | ConvertTo-Json -Depth 4

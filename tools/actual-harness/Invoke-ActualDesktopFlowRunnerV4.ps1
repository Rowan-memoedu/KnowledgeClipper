param(
    [switch]$SelfTest,
    [string]$Runner,
    [string[]]$RunnerArguments,
    [ValidateRange(10,600)][int]$HeartbeatTimeoutSeconds = 75,
    [ValidateRange(60,3600)][int]$WholeRunTimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

function Get-WatchdogDecision([datetime]$Now,[datetime]$Started,[datetime]$LastHeartbeat,[timespan]$HeartbeatTimeout,[timespan]$WholeRunTimeout) {
    if (($Now - $Started) -ge $WholeRunTimeout) { return 'whole-run-timeout' }
    if (($Now - $LastHeartbeat) -ge $HeartbeatTimeout) { return 'heartbeat-timeout' }
    return 'continue'
}

function Read-Heartbeat([string]$Line,[ref]$LastSequence) {
    try {
        $item = $Line | ConvertFrom-Json -ErrorAction Stop
        $names = @($item.psobject.Properties.Name | Sort-Object)
        if (($names -join ',') -ne 'atUtc,schemaVersion,sequence,stage') { return $false }
        if ($item.schemaVersion -ne 'knowledge-clipper-actual-runner-heartbeat.v1' -or ([string]$item.stage) -notmatch '^[a-z0-9.-]+$') { return $false }
        $timestamp = [datetimeoffset]::MinValue
        if (-not [datetimeoffset]::TryParse([string]$item.atUtc,[ref]$timestamp)) { return $false }
        [long]$sequence = $item.sequence
        if ($sequence -le [long]$LastSequence.Value) { return $false }
        $LastSequence.Value = $sequence
        return $true
    } catch { return $false }
}

function Quote-ProcessArgument([string]$Value) {
    if ($null -eq $Value) { return '""' }
    return '"' + $Value.Replace('"','\"') + '"'
}

function Stop-OwnedProcessTree([int]$RootPid) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId)
    $ordered = [System.Collections.Generic.List[int]]::new()
    function Add-Children([int]$Parent) {
        foreach ($child in $all | Where-Object { $_.ParentProcessId -eq $Parent }) { Add-Children ([int]$child.ProcessId); $ordered.Add([int]$child.ProcessId) }
    }
    Add-Children $RootPid
    foreach ($ownedProcessId in $ordered) { Stop-Process -Id $ownedProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $RootPid -Force -ErrorAction SilentlyContinue
}

if ($SelfTest) {
    $origin = [datetime]'2026-01-01T00:00:00Z'
    $heartbeat = [timespan]::FromSeconds(75)
    $whole = [timespan]::FromSeconds(900)
    $cases = @()
    $cases += ((Get-WatchdogDecision ($origin.AddSeconds(30)) $origin $origin $heartbeat $whole) -eq 'continue')
    $cases += ((Get-WatchdogDecision ($origin.AddSeconds(75)) $origin $origin $heartbeat $whole) -eq 'heartbeat-timeout')
    $cases += ((Get-WatchdogDecision ($origin.AddSeconds(899)) $origin ($origin.AddSeconds(850)) $heartbeat $whole) -eq 'continue')
    $cases += ((Get-WatchdogDecision ($origin.AddSeconds(900)) $origin ($origin.AddSeconds(899)) $heartbeat $whole) -eq 'whole-run-timeout')
    [long]$sequence = 0
    $cases += (Read-Heartbeat '{"schemaVersion":"knowledge-clipper-actual-runner-heartbeat.v1","sequence":1,"stage":"lookup.before","atUtc":"2026-01-01T00:00:01Z"}' ([ref]$sequence))
    $cases += (-not (Read-Heartbeat '{"schemaVersion":"knowledge-clipper-actual-runner-heartbeat.v1","sequence":1,"stage":"lookup.after","atUtc":"2026-01-01T00:00:02Z"}' ([ref]$sequence)))
    $cases += (-not (Read-Heartbeat '{"schemaVersion":"knowledge-clipper-actual-runner-heartbeat.v1","sequence":2,"stage":"lookup.after","atUtc":"2026-01-01T00:00:02Z","content":"forbidden"}' ([ref]$sequence)))
    if ($cases -contains $false) { throw 'watchdog-self-test-failed' }
    [ordered]@{status='PASS';fixtures=7;heartbeatTimeout='PASS';wholeRunTimeout='PASS';strictMonotonicHeartbeat='PASS';heartbeatFieldAllowlist='PASS';externalConnections=0;businessTasks=0} | ConvertTo-Json -Compress
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Runner) -or -not (Test-Path -LiteralPath $Runner)) { throw 'runner-missing' }
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledge-clipper-actual-watchdog-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$stdout = Join-Path $temporaryRoot 'stdout.ndjson'
$stderr = Join-Path $temporaryRoot 'stderr.log'
$started = [datetime]::UtcNow
$lastHeartbeat = $started
$lastSequence = [long]0
$observedLines = 0
$failure = $null
try {
    $argumentLine = (($RunnerArguments | ForEach-Object { Quote-ProcessArgument $_ }) -join ' ')
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Runner)) -ArgumentList $argumentLine -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not $process.HasExited) {
        Start-Sleep -Milliseconds 250
        $lines = if (Test-Path -LiteralPath $stdout) { @(Get-Content -LiteralPath $stdout) } else { @() }
        while ($observedLines -lt $lines.Count) {
            $line = $lines[$observedLines++]
            Write-Output $line
            if (Read-Heartbeat $line ([ref]$lastSequence)) { $lastHeartbeat = [datetime]::UtcNow }
        }
        $decision = Get-WatchdogDecision ([datetime]::UtcNow) $started $lastHeartbeat ([timespan]::FromSeconds($HeartbeatTimeoutSeconds)) ([timespan]::FromSeconds($WholeRunTimeoutSeconds))
        if ($decision -ne 'continue') { $failure=$decision;Stop-OwnedProcessTree $process.Id;break }
        $process.Refresh()
    }
    $process.WaitForExit()
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr | ForEach-Object { [Console]::Error.WriteLine($_) } }
    if ($failure) { [Console]::Error.WriteLine('actual-runner-watchdog:'+$failure);exit 124 }
    exit $process.ExitCode
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}

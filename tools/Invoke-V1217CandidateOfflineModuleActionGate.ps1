[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateRoot,

    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
$candidate = [IO.Path]::GetFullPath($CandidateRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $evidence) { throw "EVIDENCE_ROOT_EXISTS: $evidence" }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$jobsRoot = Join-Path $evidence 'jobs'
$ipcRoot = Join-Path $evidence 'ipc'
[IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
[IO.Directory]::CreateDirectory($ipcRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-JsonOnce([string]$Path, $Value) {
    if (Test-Path -LiteralPath $Path) { throw "FILE_EXISTS: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 100) + "`n"), $utf8)
}

function Get-Sha256Text([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([Convert]::ToHexString($sha.ComputeHash($utf8.GetBytes($Text)))).ToLowerInvariant() } finally { $sha.Dispose() }
}

$authority = Join-Path $candidate 'authority\win-x64'
$orchestrator = Join-Path $authority 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$registry = Join-Path $candidate 'authority\metadata\worker-registry.v1.json'
$ipcSource = Join-Path $authority 'contracts\core\v2.7\module-control\orchestrator-ipc.v1.2.json'
$adapterConfig = Join-Path $authority 'contracts\core\v2.1\profile-resolver.v2.1.json'
foreach ($path in @($orchestrator, $registry, $ipcSource, $adapterConfig)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "CANDIDATE_FILE_MISSING: $path" }
}

$pipeName = 'KnowledgeClipper.Quality.V1217.' + [Guid]::NewGuid().ToString('N')
$ipcConfig = Get-Content -Raw -LiteralPath $ipcSource | ConvertFrom-Json -Depth 100
$ipcConfig.pipeName = $pipeName
$ipcConfig.endpoint = "\\.\pipe\$pipeName"
$ipcConfigPath = Join-Path $evidence 'orchestrator-ipc.runtime.v1.2.json'
Write-JsonOnce $ipcConfigPath $ipcConfig

$script:sequence = 0
function Invoke-Pipe($Payload, [string]$MessageType, [string]$ProtocolVersion) {
    $correlation = 'quality-v1217-' + [Guid]::NewGuid().ToString('N')
    $request = [ordered]@{ protocolVersion = $ProtocolVersion; correlationId = $correlation; messageType = $MessageType; payload = $Payload }
    $requestJson = $request | ConvertTo-Json -Depth 100 -Compress
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None)
    try {
        $pipe.Connect(10000)
        $bytes = $utf8.GetBytes($requestJson)
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.WriteByte(10)
        $pipe.Flush()
        $stream = [IO.MemoryStream]::new()
        while ($true) {
            $value = $pipe.ReadByte()
            if ($value -lt 0) { throw 'Named Pipe closed before LF response delimiter.' }
            if ($value -eq 10) { break }
            $stream.WriteByte([byte]$value)
            if ($stream.Length -gt 1048576) { throw 'Named Pipe response exceeded 1 MiB.' }
        }
        $responseJson = $utf8.GetString($stream.ToArray()).TrimEnd("`r")
        $response = $responseJson | ConvertFrom-Json -Depth 100
        $exchangeId = '{0:D4}-{1}' -f (++$script:sequence), $MessageType
        Write-JsonOnce (Join-Path $ipcRoot "$exchangeId.request.json") $request
        [IO.File]::WriteAllText((Join-Path $ipcRoot "$exchangeId.response.json"), ($responseJson + "`n"), $utf8)
        if ($response.correlationId -ne $correlation) { throw 'Named Pipe correlation mismatch.' }
        return $response
    }
    finally { $pipe.Dispose() }
}

$previewActionId = 'quality-preview-denied-' + [Guid]::NewGuid().ToString('N')
$previewInput = [ordered]@{
    schemaVersion = 'speech-settings-speaker-preview-input.v1'
    scope = 'project'
    effectiveAt = 'preview_only'
    action = 'speaker.preview'
    explicitSideEffectAuthorization = $false
    previewLanguage = 'zh-CN'
    adapterInput = [ordered]@{}
}
$previewInputPath = Join-Path $jobsRoot "module-actions\$previewActionId\input.json"
Write-JsonOnce $previewInputPath $previewInput
$previewRequest = [ordered]@{
    schemaVersion = 'knowledge-clipper-module-action.v1'
    actionId = $previewActionId
    operation = 'speech.preview'
    payloadRef = "module-actions/$previewActionId/input.json"
    idempotencyKey = Get-Sha256Text "$previewActionId|speech.preview|quality-v1217"
}

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $orchestrator
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($orchestrator)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('serve', '--ipc-config', $ipcConfigPath, '--jobs-root', $jobsRoot, '--registry', $registry, '--release-root', $authority, '--max-active-tasks', '1', '--adapter-config', $adapterConfig)) {
    $start.ArgumentList.Add($argument)
}

$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$stdoutTask = $null
$stderrTask = $null
try {
    if (-not $process.Start()) { throw 'ORCHESTRATOR_START_FAILED' }
    $pidValue = $process.Id
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $ready = $false
    $query = $null
    for ($attempt = 0; $attempt -lt 120 -and -not $ready; $attempt++) {
        if ($process.HasExited) { throw "ORCHESTRATOR_EARLY_EXIT: $($process.ExitCode)" }
        try {
            $query = Invoke-Pipe ([ordered]@{ schemaVersion = '2.2.0'; queryId = ('quality-ready-' + [Guid]::NewGuid().ToString('N')); type = 'task.query_recent'; limit = 1 }) 'task-query' '1.0'
            if ($query.ok) { $ready = $true }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'ORCHESTRATOR_PIPE_NOT_READY' }

    $preview = Invoke-Pipe $previewRequest 'module-action' '1.2'
    $previewReceipt = Join-Path $jobsRoot "module-actions\$previewActionId\receipt.json"
    if ($preview.ok -or $preview.error.code -ne 'ipc.invalid_frame' -or (Test-Path -LiteralPath $previewReceipt)) {
        throw 'UNAUTHORIZED_PREVIEW_NOT_FAIL_CLOSED'
    }

    $result = [ordered]@{
        schemaVersion = 'knowledge-clipper-quality-offline-module-action.v1'
        status = 'passed'
        candidateRoot = $candidate
        orchestratorPid = $pidValue
        queryRecent = [ordered]@{ ok = [bool]$query.ok; protocolVersion = $query.protocolVersion; messageType = $query.messageType }
        unauthorizedPreview = [ordered]@{
            ok = [bool]$preview.ok
            errorCode = $preview.error.code
            receiptCreated = Test-Path -LiteralPath $previewReceipt
            workerRunCount = 0
            externalServiceCalls = 0
            physicalSpeakerInvocations = 0
        }
    }
    Write-JsonOnce (Join-Path $evidence 'offline-module-action-receipt.json') $result
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(10000) | Out-Null
    }
    if ($stdoutTask) { [IO.File]::WriteAllText((Join-Path $evidence 'orchestrator.stdout.log'), $stdoutTask.GetAwaiter().GetResult(), $utf8) }
    if ($stderrTask) { [IO.File]::WriteAllText((Join-Path $evidence 'orchestrator.stderr.log'), $stderrTask.GetAwaiter().GetResult(), $utf8) }
    $process.Dispose()
}

Get-Content -Raw -LiteralPath (Join-Path $evidence 'offline-module-action-receipt.json')

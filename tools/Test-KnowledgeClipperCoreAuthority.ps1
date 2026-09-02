[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$RegistryVersion,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$orchestrator = Join-Path $ReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$registry = Join-Path $AuthorityDirectory 'worker-registry.v1.json'
$dependency = Join-Path $AuthorityDirectory 'dependency-manifest.v1.json'
$ipcSource = Join-Path $ReleaseRoot 'contracts\core\v2.3\playback-control\orchestrator-ipc.v1.1.json'

function Write-Utf8JsonOnce([string]$Path, [object]$Value, [int]$Depth = 32) {
    if (Test-Path -LiteralPath $Path) { throw "Append-only target already exists: $Path" }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Invoke-PipeJson([string]$PipeName, [object]$Request) {
    $client = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect(10000)
        $json = $Request | ConvertTo-Json -Depth 32 -Compress
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json + "`n")
        $client.Write($bytes, 0, $bytes.Length)
        $client.Flush()
        $reader = [IO.StreamReader]::new($client, [Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
        try {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { throw 'Named Pipe response was empty.' }
            return $line | ConvertFrom-Json
        }
        finally { $reader.Dispose() }
    }
    finally { $client.Dispose() }
}

foreach ($required in @($orchestrator, $registry, $dependency, $ipcSource)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required authority probe input is missing: $required" }
}
if (Test-Path -LiteralPath $EvidenceRoot) { throw "Append-only evidence root already exists: $EvidenceRoot" }
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null

$validationText = (& $orchestrator validate-registry --registry $registry --release-root $ReleaseRoot 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw "validate-registry failed: $validationText" }
$validation = $validationText | ConvertFrom-Json
if ($validation.status -ne 'valid' -or $validation.registryVersion -ne $RegistryVersion -or $validation.workers -ne 5) {
    throw 'validate-registry returned an unexpected result.'
}
[IO.File]::WriteAllText((Join-Path $EvidenceRoot 'validate-registry.json'), $validationText.TrimEnd() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$pipeName = 'KnowledgeClipper.AuthorityV124.' + [Guid]::NewGuid().ToString('N')
$ipc = Get-Content -LiteralPath $ipcSource -Raw | ConvertFrom-Json
$ipc.pipeName = $pipeName
$ipc.endpoint = "\\.\pipe\$pipeName"
$ipcPath = Join-Path $EvidenceRoot 'orchestrator-ipc.probe.v1.1.json'
Write-Utf8JsonOnce $ipcPath $ipc

$jobsRoot = Join-Path $EvidenceRoot 'jobs'
$taskId = 'task-authority-stage-control-probe'
$runId = 'run-probe'
$stageId = 'speech-playback'
$attemptId = 'attempt-probe'
$operationId = 'operation-probe'
$attemptRelative = "runs/$runId/stages/$stageId/attempts/$attemptId"
$jobRoot = Join-Path $jobsRoot $taskId
$requestPath = Join-Path $jobRoot ($attemptRelative.Replace('/', '\') + '\request.json')
$workerRequest = [ordered]@{
    schemaVersion = '1.0.0'
    requestId = 'request-probe'
    operationId = $operationId
    taskId = $taskId
    runId = $runId
    stageId = $stageId
    attemptId = $attemptId
    workerId = 'speech.edge'
    operation = 'speech.playback'
    idempotencyKey = 'idempotency-authority-stage-control-probe'
    attemptPath = $attemptRelative
    outputPath = "$attemptRelative/outputs"
    progressPath = "$attemptRelative/progress.ndjson"
    resultPath = "$attemptRelative/result.json"
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRelative/cancel.json"; gracePeriodMs = 2000 }
    inputRefs = @()
    configSnapshot = [ordered]@{}
    payload = [ordered]@{}
}
[IO.Directory]::CreateDirectory($jobRoot) | Out-Null

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $orchestrator
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('serve', '--ipc-config', $ipcPath, '--jobs-root', $jobsRoot, '--registry', $registry, '--release-root', $ReleaseRoot, '--max-active-tasks', '1')) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
if ($null -eq $process) { throw 'Failed to start authority Orchestrator.' }
$stageResponse = $null
$queryResponse = $null
$commandResponse = $null
try {
    $queryResponse = Invoke-PipeJson $pipeName ([ordered]@{
        protocolVersion = '1.0'; correlationId = 'correlation-query-probe'; messageType = 'task-query'
        payload = [ordered]@{ schemaVersion = '1.0.0'; queryId = 'query-probe'; type = 'task.query_recent'; limit = 1 }
    })
    Write-Utf8JsonOnce (Join-Path $EvidenceRoot 'task-query-response.v1.json') $queryResponse
    if (-not $queryResponse.ok -or $queryResponse.protocolVersion -ne '1.0' -or $queryResponse.messageType -ne 'task-query-result') {
        throw 'Protocol 1.0 task-query regression failed.'
    }

    $commandResponse = Invoke-PipeJson $pipeName ([ordered]@{
        protocolVersion = '1.0'; correlationId = 'correlation-command-probe'; messageType = 'task-command'
        payload = [ordered]@{
            schemaVersion = '1.0.0'; commandId = 'command-probe'; operationId = 'operation-command-probe'; taskId = $taskId
            type = 'task.provide_input'; issuedAt = [DateTimeOffset]::UtcNow.ToString('o'); idempotencyKey = 'idempotency-command-probe'
            payloadRef = 'commands/probe/input.json'; expectedState = 'queued'
        }
    })
    Write-Utf8JsonOnce (Join-Path $EvidenceRoot 'task-command-response.v1.json') $commandResponse
    if (-not $commandResponse.ok -or $commandResponse.protocolVersion -ne '1.0' -or $commandResponse.messageType -ne 'task-command-receipt' -or
        $commandResponse.payload.accepted -or $commandResponse.payload.error.code -ne 'contract.expected_state_mismatch') {
        throw 'Protocol 1.0 task-command regression failed.'
    }

    Write-Utf8JsonOnce $requestPath $workerRequest
    $stageResponse = Invoke-PipeJson $pipeName ([ordered]@{
        protocolVersion = '1.1'; correlationId = 'correlation-stage-control-probe'; messageType = 'stage-control'
        payload = [ordered]@{ taskId = $taskId; operation = 'speech.playback'; selector = 'current-active'; reason = 'user_stop' }
    })
    Write-Utf8JsonOnce (Join-Path $EvidenceRoot 'stage-control-response.v1.1.json') $stageResponse
    if (-not $stageResponse.ok -or $stageResponse.protocolVersion -ne '1.1' -or $stageResponse.messageType -ne 'stage-control-receipt' -or
        -not $stageResponse.payload.accepted -or $stageResponse.payload.schemaVersion -ne '2.3.0' -or
        $stageResponse.payload.resolved.runId -ne $runId -or $stageResponse.payload.resolved.stageId -ne $stageId -or
        $stageResponse.payload.resolved.attemptId -ne $attemptId -or $stageResponse.payload.resolved.operationId -ne $operationId) {
        throw 'Protocol 1.1 stage-control probe failed.'
    }
}
finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'orchestrator.stdout.log'), $stdout, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'orchestrator.stderr.log'), $stderr, [Text.UTF8Encoding]::new($false))
    $process.Dispose()
}

$cancelPath = Join-Path $jobRoot ($attemptRelative.Replace('/', '\') + '\cancel.json')
$resultPath = Join-Path $jobRoot ($attemptRelative.Replace('/', '\') + '\result.json')
$runCancelPath = Join-Path $jobRoot "runs\$runId\cancel-requested.json"
if (-not (Test-Path -LiteralPath $cancelPath -PathType Leaf) -or (Test-Path -LiteralPath $resultPath) -or (Test-Path -LiteralPath $runCancelPath)) {
    throw 'Stage-control durable effect boundary failed.'
}
$cancel = Get-Content -LiteralPath $cancelPath -Raw | ConvertFrom-Json
if ($cancel.schemaVersion -ne '1.0.0' -or $cancel.attemptId -ne $attemptId -or $cancel.operationId -ne $operationId -or $cancel.reason -ne 'user_stop') {
    throw 'Published worker-cancel-signal identity drifted.'
}
$events = @(Get-ChildItem -LiteralPath (Join-Path $jobRoot "runs\$runId\events") -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
$controlEvents = @($events | Where-Object type -eq 'stage.control_requested')
if ($controlEvents.Count -ne 1 -or $controlEvents[0].payload.resolved.operationId -ne $operationId) {
    throw 'Durable stage.control_requested event is missing or ambiguous.'
}
$receiptPath = Join-Path $jobRoot ($stageResponse.payload.receiptRef.Replace('/', '\'))
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'Durable stage-control receipt is missing.' }

$driftRoot = Join-Path $EvidenceRoot 'dependency-drift-fixture'
[IO.Directory]::CreateDirectory($driftRoot) | Out-Null
[IO.File]::Copy($registry, (Join-Path $driftRoot 'worker-registry.v1.json'), $false)
$drift = Get-Content -LiteralPath $dependency -Raw | ConvertFrom-Json
$drift.treeSha256 = ('0' * 64)
Write-Utf8JsonOnce (Join-Path $driftRoot 'dependency-manifest.v1.json') $drift
$driftRejected = $false
$driftMessage = $null
try {
    & (Join-Path $ProjectRoot 'tools\New-KnowledgeClipperAuthorityCheckpoint.ps1') `
        -ReleaseRoot $ReleaseRoot -AuthorityDirectory $driftRoot -RegistryVersion $RegistryVersion `
        -AdmissionStatus authority-admitted -ValidateOnly | Out-String | Out-Null
}
catch {
    $driftMessage = $_.Exception.Message
    $driftRejected = $driftMessage.StartsWith('Dependency manifest drift:', [StringComparison]::Ordinal)
}
if (-not $driftRejected) { throw "Dependency drift negative gate did not reject as expected: $driftMessage" }
[IO.File]::WriteAllText((Join-Path $EvidenceRoot 'dependency-drift.log'), $driftMessage + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$result = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-authority-runtime-gates.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    releaseRoot = $ReleaseRoot
    registryVersion = $RegistryVersion
    validateRegistry = [ordered]@{ status = $validation.status; workers = $validation.workers }
    protocol10 = [ordered]@{
        taskQuery = [ordered]@{ status = 'passed'; messageType = $queryResponse.messageType }
        taskCommand = [ordered]@{ status = 'passed'; messageType = $commandResponse.messageType; accepted = $commandResponse.payload.accepted; rejectionCode = $commandResponse.payload.error.code }
    }
    protocol11 = [ordered]@{
        stageControl = [ordered]@{ status = 'passed'; messageType = $stageResponse.messageType; receiptSchemaVersion = $stageResponse.payload.schemaVersion; resolved = $stageResponse.payload.resolved }
    }
    durableEffects = [ordered]@{
        workerCancelSignal = 'published'
        stageControlEventCount = $controlEvents.Count
        receipt = $stageResponse.payload.receiptRef
        workerResultWritten = $false
        runCancelRequestedWritten = $false
    }
    dependencyDriftNegative = [ordered]@{ status = 'passed'; rejected = $driftRejected; message = $driftMessage }
    workerRunInvocations = 0
    physicalSpeakerInvocations = 0
    externalWrites = 0
}
$resultPath = Join-Path $EvidenceRoot 'runtime-gates.json'
Write-Utf8JsonOnce $resultPath $result
$result | ConvertTo-Json -Depth 32

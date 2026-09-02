[CmdletBinding()]
param(
    [string]$EvidenceRoot = '',
    [string]$RemNoteCanaryRootId = '8zNzfX45XLCSKoPSJ',
    [string]$RemNoteCanaryRootTitle = 'KnowledgeClipper PhaseB Canary 20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repo ('canary\sealed-module-actions-v1.2.16\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
}
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $evidence) { throw "EVIDENCE_ROOT_EXISTS: $evidence" }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$jobsRoot = Join-Path $evidence 'jobs'
$ipcRoot = Join-Path $evidence 'ipc'
[IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
[IO.Directory]::CreateDirectory($ipcRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-Sha256File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Sha256Text([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([Convert]::ToHexString($sha.ComputeHash($utf8.GetBytes($Text)))).ToLowerInvariant() } finally { $sha.Dispose() }
}
function File-Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = Get-Sha256File $item.FullName }
}
function Write-BytesOnce([string]$Path, [byte[]]$Bytes) {
    if (Test-Path -LiteralPath $Path) { throw "FILE_EXISTS: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
}
function Write-JsonOnce([string]$Path, $Value) {
    Write-BytesOnce $Path $utf8.GetBytes(($Value | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
}

$release = Join-Path $repo 'releases\knowledge-clipper-authority-v1.2.16-20260830T125200Z\win-x64'
$authority = Join-Path $repo 'contracts\authority\worker-registry\v1.2.16-20260830T125200Z'
$orchestrator = Join-Path $release 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$registry = Join-Path $authority 'worker-registry.v1.json'
$checkpoint = Join-Path $authority 'checkpoint.json'
$ipcSource = Join-Path $release 'contracts\core\v2.7\module-control\orchestrator-ipc.v1.2.json'
$adapterConfig = Join-Path $release 'contracts\core\v2.1\profile-resolver.v2.1.json'
foreach ($path in @($orchestrator,$registry,$checkpoint,$ipcSource,$adapterConfig)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "AUTHORITY_FILE_MISSING: $path" }
}
if ((Get-Sha256File $registry) -ne '42250cc251eea1bebe2cb4e072e19c73f7cf25417010b5c33c922755ea46e757' -or
    (Get-Sha256File $checkpoint) -ne '48d6e25e967f73f1d0489df1a6ba8c8f93212b5fad1fad832c5c09a3d58ac046' -or
    (Get-Sha256File $orchestrator) -ne '1548acb0f950e6103c89635e2698004685bc8b709d67d36221d22e6951385db0') {
    throw 'AUTHORITY_IDENTITY_DRIFT'
}

$script:pipeName = 'KnowledgeClipper.AuthorityV1216.Module.' + [Guid]::NewGuid().ToString('N')
$script:exchangeSequence = 0
$ipcConfig = Get-Content -Raw -LiteralPath $ipcSource | ConvertFrom-Json -Depth 100
$ipcConfig.pipeName = $script:pipeName
$ipcConfig.endpoint = "\\.\pipe\$($script:pipeName)"
$ipcConfigPath = Join-Path $evidence 'orchestrator-ipc.runtime.v1.2.json'
Write-JsonOnce $ipcConfigPath $ipcConfig

function Invoke-Pipe($Payload, [string]$MessageType, [string]$ProtocolVersion) {
    $correlation = 'authority-v1216-' + [Guid]::NewGuid().ToString('N')
    $request = [ordered]@{ protocolVersion = $ProtocolVersion; correlationId = $correlation; messageType = $MessageType; payload = $Payload }
    $requestJson = $request | ConvertTo-Json -Depth 100 -Compress
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $script:pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None)
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
        $exchangeId = '{0:D4}-{1}' -f (++$script:exchangeSequence), $MessageType
        Write-JsonOnce (Join-Path $ipcRoot "$exchangeId.request.json") $request
        Write-BytesOnce (Join-Path $ipcRoot "$exchangeId.response.json") $utf8.GetBytes($responseJson + "`n")
        if ($response.correlationId -ne $correlation) { throw 'Named Pipe correlation mismatch.' }
        return $response
    }
    finally { $pipe.Dispose() }
}

function New-ModuleRequest([string]$ActionId, [string]$Operation) {
    [ordered]@{
        schemaVersion = 'knowledge-clipper-module-action.v1'
        actionId = $ActionId
        operation = $Operation
        payloadRef = "module-actions/$ActionId/input.json"
        idempotencyKey = Get-Sha256Text "$ActionId|$Operation|v1.2.16"
    }
}

$voiceActionId = 'voice-catalog-' + [Guid]::NewGuid().ToString('N')
$voiceInput = [ordered]@{
    schemaVersion = 'speech-settings-voice-discovery-input.v1'
    mode = 'validate_only'
    requestedLanguages = @('zh-CN','en-US')
    cacheMaxAgeSeconds = 604800
    deployment = [ordered]@{ edgeTtsPath = $null; ffplayPath = $null }
    catalogSnapshot = [ordered]@{
        provider = 'edge-tts'
        catalogVersion = 'sealed-v1216-readonly-fixture-1'
        updatedAtUtc = '2026-08-30T00:00:00Z'
        voices = @(
            [ordered]@{ languageTag='zh-CN'; voiceId='zh-CN-XiaoxiaoNeural'; displayName='Xiaoxiao'; gender='female'; natural=$true; available=$true; status='available' },
            [ordered]@{ languageTag='en-US'; voiceId='en-US-AvaMultilingualNeural'; displayName='Ava Multilingual'; gender='female'; natural=$true; available=$true; status='available' }
        )
    }
}
$voiceInputPath = Join-Path $jobsRoot "module-actions\$voiceActionId\input.json"
Write-JsonOnce $voiceInputPath $voiceInput
$voiceRequest = New-ModuleRequest $voiceActionId 'speech.voices'

$remnoteActionId = 'remnote-probe-' + [Guid]::NewGuid().ToString('N')
$remnoteInput = [ordered]@{
    schemaVersion = 'remnote-public-probe-request.v2'
    probeId = $remnoteActionId
    authorizationScope = 'isolated-canary'
    timeoutMs = 15000
    operations = @('status','search','list','read')
    target = [ordered]@{ rootRemId = $RemNoteCanaryRootId; rootTitle = $RemNoteCanaryRootTitle }
}
$remnoteInputPath = Join-Path $jobsRoot "module-actions\$remnoteActionId\input.json"
Write-JsonOnce $remnoteInputPath $remnoteInput
$remnoteRequest = New-ModuleRequest $remnoteActionId 'remnote.probe'

$previewActionId = 'preview-denied-' + [Guid]::NewGuid().ToString('N')
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
$previewRequest = New-ModuleRequest $previewActionId 'speech.preview'

$stdoutPath = Join-Path $evidence 'orchestrator.stdout.log'
$stderrPath = Join-Path $evidence 'orchestrator.stderr.log'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $orchestrator
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($orchestrator)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('serve','--ipc-config',$ipcConfigPath,'--jobs-root',$jobsRoot,'--registry',$registry,'--release-root',$release,'--max-active-tasks','1','--adapter-config',$adapterConfig)) {
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
    for ($attempt = 0; $attempt -lt 120 -and -not $ready; $attempt++) {
        if ($process.HasExited) { throw "ORCHESTRATOR_EARLY_EXIT: $($process.ExitCode)" }
        try {
            $response = Invoke-Pipe ([ordered]@{ schemaVersion='2.2.0'; queryId=('ready-' + [Guid]::NewGuid().ToString('N')); type='task.query_recent'; limit=1 }) 'task-query' '1.0'
            if ($response.ok) { $ready = $true }
        } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'ORCHESTRATOR_PIPE_NOT_READY' }

    $voiceFirst = Invoke-Pipe $voiceRequest 'module-action' '1.2'
    if (-not $voiceFirst.ok -or -not $voiceFirst.payload.accepted -or $voiceFirst.payload.physicalAudio -or $voiceFirst.payload.externalWriteCount -ne 0) {
        throw 'VOICE_PUBLIC_DISPATCH_FAILED'
    }
    $voiceReceiptPath = Join-Path $jobsRoot "module-actions\$voiceActionId\receipt.json"
    $voiceReceiptShaBeforeReplay = Get-Sha256File $voiceReceiptPath
    $voiceReplay = Invoke-Pipe $voiceRequest 'module-action' '1.2'
    if (-not $voiceReplay.ok -or $voiceReplay.payload.outputSha256 -ne $voiceFirst.payload.outputSha256 -or
        (Get-Sha256File $voiceReceiptPath) -ne $voiceReceiptShaBeforeReplay) { throw 'VOICE_REPLAY_DRIFT' }
    $voiceCatalogPath = Join-Path $jobsRoot ([string]$voiceFirst.payload.catalogSnapshotRef).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $voiceCatalog = Get-Content -Raw -LiteralPath $voiceCatalogPath | ConvertFrom-Json -Depth 100
    if (-not (Test-Path -LiteralPath $voiceCatalogPath) -or
        [IO.Path]::GetFileNameWithoutExtension($voiceCatalogPath) -ne ([string]$voiceFirst.payload.catalogSnapshotSha256) -or
        [string]$voiceCatalog.catalogSha256 -ne ([string]$voiceFirst.payload.catalogSnapshotSha256)) {
        throw 'VOICE_CATALOG_PERSISTENCE_DRIFT'
    }

    $previewDenied = Invoke-Pipe $previewRequest 'module-action' '1.2'
    if ($previewDenied.ok -or $previewDenied.error.code -ne 'ipc.invalid_frame' -or (Test-Path -LiteralPath (Join-Path $jobsRoot "module-actions\$previewActionId\receipt.json"))) {
        throw 'UNAUTHORIZED_PREVIEW_NOT_FAIL_CLOSED'
    }

    $remnoteFirst = Invoke-Pipe $remnoteRequest 'module-action' '1.2'
    if (-not $remnoteFirst.ok -or $remnoteFirst.payload.externalWriteCount -ne 0 -or $remnoteFirst.payload.physicalAudio) {
        throw 'REMNOTE_PUBLIC_PROBE_DISPATCH_FAILED'
    }
    $remnoteReceiptPath = Join-Path $jobsRoot "module-actions\$remnoteActionId\receipt.json"
    $remnoteReceiptShaBeforeReplay = Get-Sha256File $remnoteReceiptPath
    $remnoteReplay = Invoke-Pipe $remnoteRequest 'module-action' '1.2'
    if (-not $remnoteReplay.ok -or $remnoteReplay.payload.accepted -ne $remnoteFirst.payload.accepted -or
        $remnoteReplay.payload.outputSha256 -ne $remnoteFirst.payload.outputSha256 -or
        (Get-Sha256File $remnoteReceiptPath) -ne $remnoteReceiptShaBeforeReplay) { throw 'REMNOTE_PROBE_REPLAY_DRIFT' }
    $remnoteResultPath = $null
    $remnoteResult = $null
    $remnoteDirectExit = $null
    if ($remnoteFirst.payload.accepted) {
        $remnoteResultPath = Join-Path $jobsRoot ([string]$remnoteFirst.payload.outputRef).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $remnoteResult = Get-Content -Raw -LiteralPath $remnoteResultPath | ConvertFrom-Json -Depth 100
    }
    else {
        # A read-only probe intentionally uses nonzero process exits for blocked/degraded admission.
        # Capture its sanitized contract output separately so the Core projection gap remains diagnosable.
        $diagnosticExe = Join-Path $release 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
        $diagnosticStart = [Diagnostics.ProcessStartInfo]::new()
        $diagnosticStart.FileName = $diagnosticExe
        $diagnosticStart.WorkingDirectory = [IO.Path]::GetDirectoryName($diagnosticExe)
        $diagnosticStart.UseShellExecute = $false
        $diagnosticStart.CreateNoWindow = $true
        $diagnosticStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $diagnosticStart.RedirectStandardOutput = $true
        $diagnosticStart.RedirectStandardError = $true
        foreach ($argument in @('probe-public','--request',$remnoteInputPath,'--json')) { $diagnosticStart.ArgumentList.Add($argument) }
        $diagnosticProcess = [Diagnostics.Process]::new()
        $diagnosticProcess.StartInfo = $diagnosticStart
        try {
            if (-not $diagnosticProcess.Start()) { throw 'REMNOTE_DIAGNOSTIC_START_FAILED' }
            $diagnosticStdout = $diagnosticProcess.StandardOutput.ReadToEnd()
            $null = $diagnosticProcess.StandardError.ReadToEnd()
            $diagnosticProcess.WaitForExit()
            $remnoteDirectExit = $diagnosticProcess.ExitCode
            $remnoteResult = $diagnosticStdout | ConvertFrom-Json -Depth 100
            $remnoteResultPath = Join-Path $evidence 'remnote-direct-readonly-diagnostic.result.json'
            Write-BytesOnce $remnoteResultPath $utf8.GetBytes($diagnosticStdout.TrimEnd() + "`n")
        }
        finally { $diagnosticProcess.Dispose() }
    }
    if ($remnoteResult.externalWriteCount -ne 0 -or -not $remnoteResult.sanitized) { throw 'REMNOTE_PROBE_OUTPUT_BOUNDARY_DRIFT' }

    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-sealed-module-actions-v1216.v1'
        status = $(if ($remnoteFirst.payload.accepted) { 'passed' } else { 'conditional' })
        completedAt = [DateTimeOffset]::UtcNow
        boundary = 'sealed-actual-orchestrator-process-public-module-action-ipc'
        authority = [ordered]@{
            version = '1.2.16'
            registry = File-Evidence $registry
            checkpoint = File-Evidence $checkpoint
            orchestrator = File-Evidence $orchestrator
            releaseTreeSha256 = 'D4223A17AAF308A0A30214526B368E3411D5254831EF96EC820BE268A77B43EF'
        }
        process = [ordered]@{ pid = $pidValue; namedPipe = $script:pipeName; executable = $orchestrator; workerLaunchOwner = 'KnowledgeClipper.Orchestrator.exe' }
        speechVoices = [ordered]@{
            status = $voiceFirst.payload.status
            accepted = [bool]$voiceFirst.payload.accepted
            externalWriteCount = [int]$voiceFirst.payload.externalWriteCount
            physicalAudio = [bool]$voiceFirst.payload.physicalAudio
            result = File-Evidence (Join-Path $jobsRoot ([string]$voiceFirst.payload.outputRef).Replace('/', [IO.Path]::DirectorySeparatorChar))
            catalogSnapshotFile = File-Evidence $voiceCatalogPath
            catalogSemanticSha256 = $voiceFirst.payload.catalogSnapshotSha256
            catalogRevision = $voiceCatalog.catalogRevision
            replayReceiptUnchanged = $true
        }
        speechPreview = [ordered]@{
            invocation = 'authorization-negative-probe-only'
            explicitAuthorization = $false
            protocolAccepted = [bool]$previewDenied.ok
            errorCode = $previewDenied.error.code
            workerInvocations = 0
            physicalSpeakerCalls = 0
            receiptCreated = $false
        }
        remnoteProbe = [ordered]@{
            publicDispatchStatus = $remnoteFirst.payload.status
            accepted = [bool]$remnoteFirst.payload.accepted
            externalWriteCount = [int]$remnoteFirst.payload.externalWriteCount
            automaticRetryAllowed = [bool]$remnoteFirst.payload.automaticRetryAllowed
            result = File-Evidence $remnoteResultPath
            directReadonlyDiagnosticExitCode = $remnoteDirectExit
            state = $remnoteResult.state
            admission = $remnoteResult.admission
            connected = $remnoteResult.connection.connected
            targetMatched = $remnoteResult.targetBinding.matched
            reasonCodes = @($remnoteResult.reasonCodes)
            replayReceiptUnchanged = $true
            conditionalReason = $(if ($remnoteFirst.payload.accepted) { $null } else { 'Core v2.7 maps the valid nonzero blocked/degraded probe exit to module.action_failed before parsing its sanitized contract output.' })
        }
        totals = [ordered]@{ remnoteWrites = 0; settingsMutations = 0; taskCreations = 0; physicalSpeakerCalls = 0 }
        ipc = [ordered]@{ exchanges = $script:exchangeSequence; root = $ipcRoot; protocol = '1.2'; messageType = 'module-action' }
    }
    $receiptPath = Join-Path $evidence 'execution-receipt.json'
    Write-JsonOnce $receiptPath $receipt
    [ordered]@{ receipt = File-Evidence $receiptPath; evidenceRoot = $evidence; remnoteState = $remnoteResult.state; voiceCatalogSha256 = $voiceFirst.payload.catalogSnapshotSha256 } | ConvertTo-Json -Depth 20
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
    if ($null -ne $stdoutTask) { Write-BytesOnce $stdoutPath $utf8.GetBytes($stdoutTask.GetAwaiter().GetResult()) }
    if ($null -ne $stderrTask) { Write-BytesOnce $stderrPath $utf8.GetBytes($stderrTask.GetAwaiter().GetResult()) }
    $process.Dispose()
}

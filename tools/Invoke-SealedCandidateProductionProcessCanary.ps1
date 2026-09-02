[CmdletBinding()]
param(
    [string]$CandidateRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\releases\kc-integrated-settings-v1212-20260830T183500Z\win-x64',
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-orchestrator-process-v1.2.12\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$candidate = [IO.Path]::GetFullPath($CandidateRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $evidence) { throw "Append-only evidence root already exists: $evidence" }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$jobsRoot = Join-Path $evidence 'jobs'
[IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
$ipcRoot = Join-Path $evidence 'ipc'
[IO.Directory]::CreateDirectory($ipcRoot) | Out-Null

function Get-Sha256Bytes([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-Sha256File([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-BytesOnce([string]$Path, [byte[]]$Bytes) {
    if (Test-Path -LiteralPath $Path) { throw "Append-only file already exists: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Write-JsonOnce([string]$Path, $Value) {
    $json = ($Value | ConvertTo-Json -Depth 100)
    Write-BytesOnce $Path $utf8.GetBytes($json + "`n")
}

function New-ArtifactManifest(
    [string]$JobRoot,
    [string]$ArtifactId,
    [string]$Kind,
    [string]$MediaType,
    [array]$Files,
    [string]$StageId,
    [string]$WorkerId,
    [string]$WorkerVersion,
    [string]$Entrypoint,
    [string]$DataClass = 'personal'
) {
    $relative = "artifacts/$ArtifactId/artifact.json"
    $path = Join-Path $JobRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $manifest = [ordered]@{
        schemaVersion = '1.0.0'
        artifactId = $ArtifactId
        kind = $Kind
        contractVersion = '1.0.0'
        mediaType = $MediaType
        files = $Files
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        producer = [ordered]@{ stageId = $StageId; workerId = $WorkerId; workerVersion = $WorkerVersion; attemptId = ('client-' + [Guid]::NewGuid().ToString('N')) }
        inputRefs = @()
        dataClass = $DataClass
        metadata = [ordered]@{ entrypoint = $Entrypoint }
    }
    Write-JsonOnce $path $manifest
    [ordered]@{ artifactRef = $ArtifactId; kind = $Kind; contractVersion = '1.0.0'; expectedSha256 = (Get-Sha256File $path); manifestPath = $relative }
}

function New-CaptureArtifact([string]$JobRoot, [string]$TaskId, [string]$Text, [bool]$Question) {
    $artifactId = 'capture-bundle'
    $artifactRoot = Join-Path $JobRoot "artifacts\$artifactId"
    [IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
    $textRelative = if ($Question) { "artifacts/$artifactId/question-turn-0001.txt" } else { "artifacts/$artifactId/selection.txt" }
    $textPath = Join-Path $JobRoot ($textRelative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $textBytes = $utf8.GetBytes($Text)
    Write-BytesOnce $textPath $textBytes
    $sourceRef = if ($Question) { 'capture.question.user.0001' } else { 'capture.text.selection' }
    $artifacts = @([ordered]@{
        artifactRef = $sourceRef
        relativePath = $textRelative
        sha256 = Get-Sha256Bytes $textBytes
        bytes = $textBytes.LongLength
        mediaType = 'text/plain'
        encoding = 'utf-8'
    })
    $capture = [ordered]@{
        schemaVersion = 'capture-bundle.v1'
        captureId = "capture-$TaskId"
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        intent = if ($Question) { 'question' } else { 'card' }
        source = if ($Question) { 'question' } else { 'selection' }
        modeSnapshot = [ordered]@{ workflow = 'card'; contextEnabled = $false; contextGranularity = 'sentence'; contextApplied = $false; imageHandling = 'original' }
        artifacts = $artifacts
        selectedTextArtifactRef = if ($Question) { $null } else { $sourceRef }
        contextSegments = $null
        questionImages = @()
        answerImages = @()
        questionTurns = [object[]]@(if ($Question) { [ordered]@{ turnIndex = 1; role = 'user'; artifactRef = $sourceRef } })
    }
    $captureRelative = "artifacts/$artifactId/capture-bundle.v1.json"
    $capturePath = Join-Path $JobRoot ($captureRelative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    Write-JsonOnce $capturePath $capture
    $files = @(
        [ordered]@{ path = $captureRelative; sha256 = Get-Sha256File $capturePath; bytes = (Get-Item -LiteralPath $capturePath).Length; mediaType = 'application/vnd.rqc.capture-bundle+json' },
        [ordered]@{ path = $textRelative; sha256 = Get-Sha256Bytes $textBytes; bytes = $textBytes.LongLength; mediaType = 'text/plain' }
    )
    New-ArtifactManifest $JobRoot $artifactId 'capture-bundle' 'application/vnd.rqc.capture-bundle+json' $files 'desktop-capture' 'desktop.capture' '1.0.0' $captureRelative 'sensitive'
}

function New-AnkiConfigArtifact([string]$JobRoot, [string]$ReleaseRoot, [string]$Deck, [string]$ExpectedProfile) {
    $template = Join-Path $ReleaseRoot 'workers\anki\config\anki-export.v1.example.json'
    $config = Get-Content -LiteralPath $template -Raw | ConvertFrom-Json -Depth 100
    $config.expected_profile = $ExpectedProfile
    $config.endpoint = 'http://127.0.0.1:8765'
    $config.provision_managed = $false
    foreach ($property in @($config.deck_routes.PSObject.Properties)) { $config.deck_routes.($property.Name) = $Deck }
    $artifactId = 'anki-config'
    $payloadRelative = "artifacts/$artifactId/anki-export.v1.json"
    $payloadPath = Join-Path $JobRoot ($payloadRelative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    Write-JsonOnce $payloadPath $config
    $files = @([ordered]@{ path = $payloadRelative; sha256 = Get-Sha256File $payloadPath; bytes = (Get-Item -LiteralPath $payloadPath).Length; mediaType = 'application/json' })
    New-ArtifactManifest $JobRoot $artifactId 'anki-export-config' 'application/json' $files 'desktop-settings' 'desktop.settings' '1.0.0' $payloadRelative
}

function New-TaskPackage([string]$TaskId, [string]$Text, [bool]$Question, [string]$ReleaseRoot, [string]$ExpectedProfile) {
    $jobRoot = Join-Path $jobsRoot $TaskId
    if (Test-Path -LiteralPath $jobRoot) { throw "Immutable task already exists: $TaskId" }
    [IO.Directory]::CreateDirectory((Join-Path $jobRoot 'plans')) | Out-Null
    $captureRef = New-CaptureArtifact $jobRoot $TaskId $Text $Question
    $inputs = @($captureRef)
    if (-not $Question) { $inputs += New-AnkiConfigArtifact $jobRoot $releaseRoot $AnkiDeck $ExpectedProfile }
    $plan = [ordered]@{
        schemaVersion = 'desktop-intent.v1'
        taskId = $TaskId
        intent = if ($Question) { 'clip.question' } else { 'clip.card' }
        inputArtifactRef = $captureRef.artifactRef
        requestedOutputs = @($(if ($Question) { 'question.answer' } else { 'card-bundle' }))
    }
    $planPath = Join-Path $jobRoot 'plans\desktop-intent.v1.json'
    Write-JsonOnce $planPath $plan
    $manifest = [ordered]@{
        schemaVersion = '1.0.0'
        taskId = $TaskId
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        createdBy = [ordered]@{ application = 'KnowledgeClipper.SealedProcessCanaryClient'; version = '1.0.0' }
        intent = if ($Question) { 'clip.question' } else { 'clip.card' }
        executionProfile = if ($Question) { 'cards-remnote' } else { 'cards-anki' }
        inputRefs = @($inputs | ForEach-Object { [ordered]@{ artifactRef = $_.artifactRef; kind = $_.kind; contractVersion = $_.contractVersion; expectedSha256 = $_.expectedSha256 } })
        requestedOutputs = @($(if ($Question) { 'question.answer' } else { 'card-bundle' }))
        initialPlanRef = 'plans/desktop-intent.v1.json'
        retentionClass = 'evidence'
        metadata = [ordered]@{ canary = 'sealed-orchestrator-process'; authorizedAnkiDeck = if ($Question) { $null } else { $AnkiDeck } }
    }
    $manifestPath = Join-Path $jobRoot 'task.manifest.v1.json'
    Write-JsonOnce $manifestPath $manifest
    [ordered]@{ taskId = $TaskId; jobRoot = $jobRoot; manifestPath = $manifestPath; manifestRelative = "$TaskId/task.manifest.v1.json"; manifestSha256 = Get-Sha256File $manifestPath }
}

function New-OcrHandoffTask(
    [string]$TaskId,
    [string]$HandoffPath,
    [string]$MarkdownPath,
    [string]$StructuredPath,
    [string]$ReleaseRoot,
    [string]$ExpectedProfile
) {
    $jobRoot = Join-Path $jobsRoot $TaskId
    if (Test-Path -LiteralPath $jobRoot) { throw "Immutable task already exists: $TaskId" }
    [IO.Directory]::CreateDirectory((Join-Path $jobRoot 'plans')) | Out-Null
    $artifactId = 'capture-bundle'
    $artifactRoot = Join-Path $jobRoot "artifacts\$artifactId"
    [IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
    $markdownBytes = [IO.File]::ReadAllBytes($MarkdownPath)
    $structuredBytes = [IO.File]::ReadAllBytes($StructuredPath)
    $handoffBytes = [IO.File]::ReadAllBytes($HandoffPath)
    $markdownRelative = "artifacts/$artifactId/ocr-content.md"
    $structuredRelative = "artifacts/$artifactId/ocr-structured.json"
    $handoffRelative = "artifacts/$artifactId/ocr-consumer-handoff.json"
    Write-BytesOnce (Join-Path $jobRoot $markdownRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)) $markdownBytes
    Write-BytesOnce (Join-Path $jobRoot $structuredRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)) $structuredBytes
    Write-BytesOnce (Join-Path $jobRoot $handoffRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)) $handoffBytes
    $capture = [ordered]@{
        schemaVersion = 'capture-bundle.v1'
        captureId = "capture-$TaskId"
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        intent = 'card'
        source = 'ocr-handoff'
        modeSnapshot = [ordered]@{ workflow = 'card'; contextEnabled = $false; contextGranularity = 'sentence'; contextApplied = $false; imageHandling = 'original' }
        artifacts = @(
            [ordered]@{ artifactRef = 'ocr.capture.image.question.0001.markdown'; relativePath = $markdownRelative; sha256 = Get-Sha256Bytes $markdownBytes; bytes = $markdownBytes.LongLength; mediaType = 'text/markdown'; encoding = 'utf-8' },
            [ordered]@{ artifactRef = 'ocr.capture.image.question.0001.structured'; relativePath = $structuredRelative; sha256 = Get-Sha256Bytes $structuredBytes; bytes = $structuredBytes.LongLength; mediaType = 'application/json'; encoding = 'utf-8' },
            [ordered]@{ artifactRef = 'ocr.consumer.evidence'; relativePath = $handoffRelative; sha256 = Get-Sha256Bytes $handoffBytes; bytes = $handoffBytes.LongLength; mediaType = 'application/json'; encoding = 'utf-8' }
        )
        selectedTextArtifactRef = 'ocr.capture.image.question.0001.markdown'
        contextSegments = $null
        questionImages = @()
        answerImages = @()
        questionTurns = @()
    }
    $captureRelative = "artifacts/$artifactId/capture-bundle.v1.json"
    $capturePath = Join-Path $jobRoot $captureRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $capturePath $capture
    $files = @(
        [ordered]@{ path = $captureRelative; sha256 = Get-Sha256File $capturePath; bytes = (Get-Item $capturePath).Length; mediaType = 'application/vnd.rqc.capture-bundle+json' },
        [ordered]@{ path = $markdownRelative; sha256 = Get-Sha256Bytes $markdownBytes; bytes = $markdownBytes.LongLength; mediaType = 'text/markdown' },
        [ordered]@{ path = $structuredRelative; sha256 = Get-Sha256Bytes $structuredBytes; bytes = $structuredBytes.LongLength; mediaType = 'application/json' },
        [ordered]@{ path = $handoffRelative; sha256 = Get-Sha256Bytes $handoffBytes; bytes = $handoffBytes.LongLength; mediaType = 'application/json' }
    )
    $captureRef = New-ArtifactManifest $jobRoot $artifactId 'capture-bundle' 'application/vnd.rqc.capture-bundle+json' $files 'desktop-ocr-handoff' 'desktop.capture' '1.0.0' $captureRelative 'sensitive'
    $ankiRef = New-AnkiConfigArtifact $jobRoot $ReleaseRoot $AnkiDeck $ExpectedProfile
    $plan = [ordered]@{ schemaVersion = 'desktop-intent.v1'; taskId = $TaskId; intent = 'clip.card'; inputArtifactRef = $captureRef.artifactRef; requestedOutputs = @('card-bundle') }
    Write-JsonOnce (Join-Path $jobRoot 'plans\desktop-intent.v1.json') $plan
    $manifest = [ordered]@{
        schemaVersion = '1.0.0'; taskId = $TaskId; createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        createdBy = [ordered]@{ application = 'KnowledgeClipper.SealedProcessCanaryClient'; version = '1.0.0' }
        intent = 'clip.card'; executionProfile = 'cards-anki'
        inputRefs = @(@($captureRef, $ankiRef) | ForEach-Object { [ordered]@{ artifactRef = $_.artifactRef; kind = $_.kind; contractVersion = $_.contractVersion; expectedSha256 = $_.expectedSha256 } })
        requestedOutputs = @('card-bundle'); initialPlanRef = 'plans/desktop-intent.v1.json'; retentionClass = 'evidence'
        metadata = [ordered]@{ canary = 'sealed-orchestrator-process-ocr-handoff'; sourceHandoffSha256 = Get-Sha256Bytes $handoffBytes; authorizedAnkiDeck = $AnkiDeck; mineruRerun = $false }
    }
    $manifestPath = Join-Path $jobRoot 'task.manifest.v1.json'
    Write-JsonOnce $manifestPath $manifest
    [ordered]@{ taskId = $TaskId; jobRoot = $jobRoot; manifestPath = $manifestPath; manifestRelative = "$TaskId/task.manifest.v1.json"; manifestSha256 = Get-Sha256File $manifestPath; handoffPath = $HandoffPath; handoffSha256 = Get-Sha256Bytes $handoffBytes; markdownSha256 = Get-Sha256Bytes $markdownBytes; structuredSha256 = Get-Sha256Bytes $structuredBytes }
}

function Invoke-Anki([string]$Action, $Parameters = @{}) {
    $body = [ordered]@{ action = $Action; version = 6; params = $Parameters } | ConvertTo-Json -Depth 30 -Compress
    $response = Invoke-RestMethod -Uri 'http://127.0.0.1:8765' -Method Post -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 15
    if ($null -ne $response.error) { throw "AnkiConnect $Action failed: $($response.error)" }
    $response.result
}

function Invoke-Pipe($Payload, [string]$MessageType, [string]$ProtocolVersion = '1.0') {
    $correlation = 'sealed-process-' + [Guid]::NewGuid().ToString('N')
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
        if ($response.correlationId -ne $correlation) { throw 'Named Pipe correlation mismatch.' }
        $exchangeId = '{0:D4}-{1}' -f (++$script:exchangeSequence), $MessageType
        Write-JsonOnce (Join-Path $ipcRoot "$exchangeId.request.json") $request
        Write-BytesOnce (Join-Path $ipcRoot "$exchangeId.response.json") $utf8.GetBytes($responseJson + "`n")
        if (-not $response.ok) { throw "Named Pipe protocol error: $($response.error.code) $($response.error.message)" }
        $response
    }
    finally { $pipe.Dispose() }
}

function New-SubmitPayload($Task) {
    $commandId = 'command-' + [Guid]::NewGuid().ToString('N')
    $operationId = 'operation-' + [Guid]::NewGuid().ToString('N')
    [ordered]@{
        schemaVersion = '1.0.0'
        commandId = $commandId
        operationId = $operationId
        taskId = $null
        type = 'task.submit'
        issuedAt = [DateTimeOffset]::UtcNow.ToString('o')
        idempotencyKey = Get-Sha256Bytes $utf8.GetBytes("$($Task.manifestRelative)|$($Task.manifestSha256)")
        payloadRef = $Task.manifestRelative
    }
}

function Get-TaskProjection([string]$TaskId) {
    $query = [ordered]@{ schemaVersion = '2.2.0'; queryId = ('query-' + [Guid]::NewGuid().ToString('N')); type = 'task.get'; taskId = $TaskId }
    $response = Invoke-Pipe $query 'task-query'
    if (@($response.payload.items).Count -eq 0) { return $null }
    @($response.payload.items)[0]
}

function Wait-Task([string]$TaskId, [string[]]$States, [int]$TimeoutSeconds = 600) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $projection = Get-TaskProjection $TaskId
        if ($null -ne $projection -and $States -contains [string]$projection.status) { return $projection }
        Start-Sleep -Milliseconds 1000
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Task $TaskId did not reach $($States -join ',') within $TimeoutSeconds seconds."
}

function Get-WorkerResults([string]$JobRoot) {
    @(Get-ChildItem -LiteralPath $JobRoot -Recurse -Filter 'result.json' | ForEach-Object {
        try {
            $value = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -Depth 100
            if ($null -ne $value.worker.id) { [ordered]@{ path = $_.FullName; sha256 = Get-Sha256File $_.FullName; value = $value } }
        } catch { }
    })
}

function Read-ArtifactPayload([string]$JobRoot, $Reference) {
    $manifestPath = Join-Path $JobRoot ([string]$Reference.manifestPath).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
    $file = @($manifest.files)[0]
    if ($null -ne $manifest.metadata.entrypoint) { $file = @($manifest.files | Where-Object { $_.path -eq $manifest.metadata.entrypoint })[0] }
    $payloadPath = Join-Path $JobRoot ([string]$file.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $raw = Get-Content -LiteralPath $payloadPath -Raw
    try { $value = $raw | ConvertFrom-Json -Depth 100 } catch { $value = $raw }
    [ordered]@{ manifestPath = $manifestPath; manifestSha256 = Get-Sha256File $manifestPath; payloadPath = $payloadPath; payloadSha256 = Get-Sha256File $payloadPath; mediaType = [string]$file.mediaType; value = $value }
}

$orchestratorExe = Join-Path $candidate 'authority\win-x64\orchestrator\KnowledgeClipper.Orchestrator.exe'
$registryPath = Join-Path $candidate 'authority\metadata\worker-registry.v1.json'
$releaseRoot = Join-Path $candidate 'authority\win-x64'
$candidateManifest = Join-Path $candidate 'candidate-file-manifest.v1.json'
$checkpointPath = Join-Path $candidate 'authority\metadata\checkpoint.json'
$sourceIpc = Join-Path $candidate 'authority\metadata\orchestrator-ipc.candidate.v1.1.json'
foreach ($path in @($orchestratorExe, $registryPath, $candidateManifest, $checkpointPath, $sourceIpc)) { if (-not (Test-Path -LiteralPath $path)) { throw "Required candidate file is missing: $path" } }
if ((Get-Sha256File $candidateManifest) -ne '5d03c90d42fa2a1145ff6dcc4874bd059ac3ee072628f0a8dc81fb23983b4f3c') { throw 'Candidate manifest identity drifted.' }
if ((Get-Sha256File $registryPath) -ne 'fc105ab65fd80b72779d1601e2c4b6faa7eccaf5f5c1badc774060d8f685495a') { throw 'Authority registry identity drifted.' }

$preflightActions = [Collections.Generic.List[string]]::new()
$preflightActions.Add('version'); $ankiVersion = Invoke-Anki 'version'
$preflightActions.Add('getActiveProfile'); $ankiProfile = Invoke-Anki 'getActiveProfile'
$preflightActions.Add('deckNames'); $ankiDecks = @(Invoke-Anki 'deckNames')
if ($ankiVersion -ne 6 -or [string]::IsNullOrWhiteSpace([string]$ankiProfile) -or $ankiDecks -notcontains $AnkiDeck) { throw 'Anki read-only preflight did not confirm API v6, active profile, and the authorized deck.' }
$preflight = [ordered]@{ schemaVersion = 'knowledge-clipper-sealed-process-preflight.v1'; generatedAt = [DateTimeOffset]::UtcNow.ToString('o'); status = 'passed'; actions = @($preflightActions); anki = [ordered]@{ version = $ankiVersion; activeProfile = $ankiProfile; authorizedDeck = $AnkiDeck; deckPresent = $true; deckCount = $ankiDecks.Count }; deepSeekCredential = [ordered]@{ processPresent = [bool][Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY', 'Process'); userPresent = [bool][Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY', 'User'); secretPersisted = $false } }
Write-JsonOnce (Join-Path $evidence 'preflight.json') $preflight

$script:pipeName = 'KnowledgeClipper.SealedProcess.' + [Guid]::NewGuid().ToString('N')
$script:exchangeSequence = 0
$ipcConfig = Get-Content -LiteralPath $sourceIpc -Raw | ConvertFrom-Json -Depth 100
$ipcConfig.pipeName = $script:pipeName
$ipcConfig.endpoint = "\\.\pipe\$($script:pipeName)"
$ipcConfigPath = Join-Path $evidence 'orchestrator-ipc.runtime.v1.1.json'
Write-JsonOnce $ipcConfigPath $ipcConfig

$processStdout = Join-Path $evidence 'orchestrator.stdout.log'
$processStderr = Join-Path $evidence 'orchestrator.stderr.log'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $orchestratorExe
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($orchestratorExe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($arg in @('serve','--ipc-config',$ipcConfigPath,'--jobs-root',$jobsRoot,'--registry',$registryPath,'--release-root',$releaseRoot,'--max-active-tasks','2')) { $start.ArgumentList.Add($arg) }
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$ordinary = $null
$ocrTask = $null
$question = $null
$questionMakeCards = $null
$orchestratorStartedAt = [DateTimeOffset]::UtcNow
$stdoutTask = $null
$stderrTask = $null
try {
    if (-not $process.Start()) { throw 'Failed to start sealed Orchestrator process.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt = 0; $attempt -lt 100 -and -not $ready; $attempt++) {
        if ($process.HasExited) { throw "Sealed Orchestrator exited during startup: $($process.ExitCode)" }
        try {
            $query = [ordered]@{ schemaVersion = '2.2.0'; queryId = ('query-' + [Guid]::NewGuid().ToString('N')); type = 'task.query_recent'; limit = 1 }
            $null = Invoke-Pipe $query 'task-query'
            $ready = $true
        } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Sealed Orchestrator Named Pipe did not become ready.' }

    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')
    $ordinaryText = "问题：为什么不可变任务包能保持重试可审计？`n答案：因为每次重试都创建新的 attempt，并保留先前的请求、结果与哈希。"
    $ordinary = New-TaskPackage "process-text-$stamp" $ordinaryText $false $releaseRoot ([string]$ankiProfile)
    $ordinarySubmit = Invoke-Pipe (New-SubmitPayload $ordinary) 'task-command'
    $ordinaryProjection = Wait-Task $ordinary.taskId @('succeeded','partial','failed','cancelled') 900
    if ($ordinaryProjection.status -ne 'succeeded') { throw "Ordinary production process task ended as $($ordinaryProjection.status)." }

    $ocrHandoffPath = Join-Path $projectRoot 'canary\image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2\content-consumer-evidence.v1.json'
    if ((Get-Sha256File $ocrHandoffPath) -ne '2834a225c66e22072f4e49bd829ec1d803cf0a4052b462dd6dbeb018b3cb7d87') { throw 'OCR consumer handoff identity drifted.' }
    $ocrHandoffRoot = [IO.Path]::GetDirectoryName($ocrHandoffPath)
    $ocrMarkdownPath = Get-ChildItem -LiteralPath $ocrHandoffRoot -Recurse -Filter 'content.md' | Where-Object { (Get-Sha256File $_.FullName) -eq '1b43e9f82b60c3ca998a587fd5ac577aaa98e69c51e4a238347c27579649acaa' } | Select-Object -First 1 -ExpandProperty FullName
    $ocrStructuredPath = Get-ChildItem -LiteralPath $ocrHandoffRoot -Recurse -Filter 'content.json' | Where-Object { (Get-Sha256File $_.FullName) -eq 'fe73ea63db375aa596b44d01c14f714e75f4ab0414a9f02c272dd18affa8fe5b' } | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($ocrMarkdownPath) -or [string]::IsNullOrWhiteSpace($ocrStructuredPath)) { throw 'OCR handoff payload files are missing.' }
    $ocrTask = New-OcrHandoffTask "process-ocr-handoff-$stamp" $ocrHandoffPath $ocrMarkdownPath $ocrStructuredPath $releaseRoot ([string]$ankiProfile)
    $ocrSubmit = Invoke-Pipe (New-SubmitPayload $ocrTask) 'task-command'
    $ocrProjection = Wait-Task $ocrTask.taskId @('succeeded','partial','failed','cancelled') 900
    if ($ocrProjection.status -ne 'succeeded') { throw "OCR handoff production process task ended as $($ocrProjection.status)." }

    $questionText = '为什么可审计系统必须让每次重试生成新的 attempt，而不能覆盖旧证据？请用两点说明。'
    $question = New-TaskPackage "process-question-$stamp" $questionText $true $releaseRoot ([string]$ankiProfile)
    $questionSubmit = Invoke-Pipe (New-SubmitPayload $question) 'task-command'
    $questionProjection = Wait-Task $question.taskId @('waiting_input','partial','failed','cancelled') 900
    if ($questionProjection.status -ne 'waiting_input' -or -not $questionProjection.answerAvailable) { throw "Question production process task did not publish an answer: $($questionProjection.status)." }
    $answerArtifact = Read-ArtifactPayload $question.jobRoot $questionProjection.questionAnswerRef
    $answerText = if ($answerArtifact.value -is [string]) { [string]$answerArtifact.value } else { [string]$answerArtifact.value.turns[-1].answer_text }

    $makeCardsCommandId = 'command-' + [Guid]::NewGuid().ToString('N')
    $makeCardsOperationId = 'operation-' + [Guid]::NewGuid().ToString('N')
    $makeCardsRelative = "commands/$makeCardsCommandId/question-action.v2.json"
    $makeCardsPath = Join-Path $question.jobRoot ($makeCardsRelative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $makeCardsPayload = [ordered]@{ schemaVersion = 'knowledge-clipper-question-action.v2'; action = 'question.make-cards'; targetToken = [string]$questionProjection.questionTargetToken }
    Write-JsonOnce $makeCardsPath $makeCardsPayload
    $questionMakeCards = [ordered]@{
        schemaVersion = '1.0.0'; commandId = $makeCardsCommandId; operationId = $makeCardsOperationId; taskId = $question.taskId; type = 'task.provide_input'; issuedAt = [DateTimeOffset]::UtcNow.ToString('o')
        idempotencyKey = Get-Sha256Bytes $utf8.GetBytes("task.provide_input|$($question.taskId)|$makeCardsRelative|$(Get-Sha256File $makeCardsPath)")
        payloadRef = $makeCardsRelative
    }
    $makeCardsReceipt = Invoke-Pipe $questionMakeCards 'task-command'
    $questionFinalProjection = Wait-Task $question.taskId @('succeeded','partial','failed','cancelled') 900
    if ($questionFinalProjection.status -ne 'succeeded') { throw "Question make-cards task ended as $($questionFinalProjection.status)." }

    $ordinaryResults = Get-WorkerResults $ordinary.jobRoot
    $contentResult = @($ordinaryResults | Where-Object { $_.value.worker.id -eq 'content.deepseek' })
    $ankiResult = @($ordinaryResults | Where-Object { $_.value.worker.id -eq 'export.anki' })
    if ($contentResult.Count -ne 1 -or $ankiResult.Count -ne 1) { throw 'Ordinary task must contain one Content result and one Anki result.' }
    $cardBundlePath = Get-ChildItem -LiteralPath $ordinary.jobRoot -Recurse -Filter 'card-bundle.schema-v1.json' | Select-Object -First 1 -ExpandProperty FullName
    $learningBundlePath = Get-ChildItem -LiteralPath $ordinary.jobRoot -Recurse -Filter 'learning-bundle.schema-v1.json' | Select-Object -First 1 -ExpandProperty FullName
    $itemsPath = Get-ChildItem -LiteralPath $ordinary.jobRoot -Recurse -Filter 'items.ndjson' | Where-Object { $_.FullName -match 'anki-export' } | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($cardBundlePath) -or [string]::IsNullOrWhiteSpace($learningBundlePath) -or [string]::IsNullOrWhiteSpace($itemsPath)) { throw 'Ordinary output artifacts are incomplete.' }
    $cardBundle = Get-Content -LiteralPath $cardBundlePath -Raw | ConvertFrom-Json -Depth 100
    $learningBundle = Get-Content -LiteralPath $learningBundlePath -Raw | ConvertFrom-Json -Depth 100
    $items = @(Get-Content -LiteralPath $itemsPath | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
    if ($items.Count -lt 1 -or @($cardBundle.cards).Count -ne $items.Count) { throw 'Anki write evidence does not match CardBundle.' }
    $notes = @(Invoke-Anki 'notesInfo' @{ notes = @($items | ForEach-Object { [long]$_.note_id }) })
    $cardIds = @($notes | ForEach-Object { @($_.cards) } | ForEach-Object { $_ })
    $cards = @(Invoke-Anki 'cardsInfo' @{ cards = $cardIds })
    if ($notes.Count -ne $items.Count -or $cards.Count -ne $cardIds.Count -or @($cards | Where-Object { $_.deckName -ne $AnkiDeck }).Count -ne 0) { throw 'Independent Anki readback identity/deck mismatch.' }
    $readback = @()
    foreach ($item in $items) {
        $bundleCard = @($cardBundle.cards | Where-Object { [string]$_.card_id -eq [string]$item.card_id })
        $note = @($notes | Where-Object { [long]$_.noteId -eq [long]$item.note_id })
        if ($bundleCard.Count -ne 1 -or $note.Count -ne 1) { throw 'Independent Anki readback item identity mismatch.' }
        $front = [string](@($bundleCard[0].front_blocks.text) -join "`n")
        $back = [string](@($bundleCard[0].back_blocks.text) -join "`n")
        if ([string]$note[0].fields.Front.value -ne $front -or [string]$note[0].fields.Back.value -ne $back) { throw 'Independent Anki front/back readback mismatch.' }
        $readback += [ordered]@{ contentCardId = [string]$item.card_id; noteId = [long]$item.note_id; ankiCardIds = @($note[0].cards); deck = $AnkiDeck; front = $front; back = $back; tags = @($note[0].tags); frontExact = $true; backExact = $true; deckExact = $true }
    }

    $questionResults = Get-WorkerResults $question.jobRoot
    $questionContentResults = @($questionResults | Where-Object { $_.value.worker.id -eq 'content.deepseek' })
    $questionAnkiResults = @($questionResults | Where-Object { $_.value.worker.id -eq 'export.anki' })
    $questionCardBundlePath = Get-ChildItem -LiteralPath $question.jobRoot -Recurse -Filter 'card-bundle.schema-v1.json' | Select-Object -Last 1 -ExpandProperty FullName
    $questionCardBundle = if ($questionCardBundlePath) { Get-Content -LiteralPath $questionCardBundlePath -Raw | ConvertFrom-Json -Depth 100 } else { $null }
    if ($questionContentResults.Count -ne 2 -or $null -eq $questionCardBundle) { throw 'Question answer and make-cards must produce two Content attempts and one CardBundle.' }

    $ocrResults = Get-WorkerResults $ocrTask.jobRoot
    $ocrContentResults = @($ocrResults | Where-Object { $_.value.worker.id -eq 'content.deepseek' })
    $ocrAnkiResults = @($ocrResults | Where-Object { $_.value.worker.id -eq 'export.anki' })
    $ocrImageResults = @($ocrResults | Where-Object { $_.value.worker.id -eq 'image.ocr.mineru' })
    if ($ocrContentResults.Count -ne 1 -or $ocrAnkiResults.Count -ne 1 -or $ocrImageResults.Count -ne 0) { throw 'OCR handoff must run exactly Content+Anki and must not rerun MinerU.' }
    $ocrCardBundlePath = Get-ChildItem -LiteralPath $ocrTask.jobRoot -Recurse -Filter 'card-bundle.schema-v1.json' | Select-Object -First 1 -ExpandProperty FullName
    $ocrLearningBundlePath = Get-ChildItem -LiteralPath $ocrTask.jobRoot -Recurse -Filter 'learning-bundle.schema-v1.json' | Select-Object -First 1 -ExpandProperty FullName
    $ocrItemsPath = Get-ChildItem -LiteralPath $ocrTask.jobRoot -Recurse -Filter 'items.ndjson' | Where-Object { $_.FullName -match 'anki-export' } | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($ocrCardBundlePath) -or [string]::IsNullOrWhiteSpace($ocrLearningBundlePath) -or [string]::IsNullOrWhiteSpace($ocrItemsPath)) { throw 'OCR handoff downstream artifacts are incomplete.' }
    $ocrCardBundle = Get-Content -LiteralPath $ocrCardBundlePath -Raw | ConvertFrom-Json -Depth 100
    $ocrLearningBundle = Get-Content -LiteralPath $ocrLearningBundlePath -Raw | ConvertFrom-Json -Depth 100
    $ocrItems = @(Get-Content -LiteralPath $ocrItemsPath | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
    if ($ocrItems.Count -lt 1 -or @($ocrCardBundle.cards).Count -ne $ocrItems.Count) { throw 'OCR handoff Anki evidence does not match CardBundle.' }
    $ocrNotes = @(Invoke-Anki 'notesInfo' @{ notes = @($ocrItems | ForEach-Object { [long]$_.note_id }) })
    $ocrCardIds = @($ocrNotes | ForEach-Object { @($_.cards) } | ForEach-Object { $_ })
    $ocrCards = @(Invoke-Anki 'cardsInfo' @{ cards = $ocrCardIds })
    if ($ocrNotes.Count -ne $ocrItems.Count -or $ocrCards.Count -ne $ocrCardIds.Count -or @($ocrCards | Where-Object { $_.deckName -ne $AnkiDeck }).Count -ne 0) { throw 'OCR handoff independent Anki readback identity/deck mismatch.' }
    $ocrReadback = @()
    foreach ($item in $ocrItems) {
        $bundleCard = @($ocrCardBundle.cards | Where-Object { [string]$_.card_id -eq [string]$item.card_id })
        $note = @($ocrNotes | Where-Object { [long]$_.noteId -eq [long]$item.note_id })
        if ($bundleCard.Count -ne 1 -or $note.Count -ne 1) { throw 'OCR handoff Anki readback item identity mismatch.' }
        $front = [string](@($bundleCard[0].front_blocks.text) -join "`n")
        $back = [string](@($bundleCard[0].back_blocks.text) -join "`n")
        if ([string]$note[0].fields.Front.value -ne $front -or [string]$note[0].fields.Back.value -ne $back) { throw 'OCR handoff Anki front/back readback mismatch.' }
        $ocrReadback += [ordered]@{ contentCardId = [string]$item.card_id; noteId = [long]$item.note_id; ankiCardIds = @($note[0].cards); deck = $AnkiDeck; front = $front; back = $back; tags = @($note[0].tags); frontExact = $true; backExact = $true; deckExact = $true }
    }

    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-sealed-orchestrator-process-canary.v1'
        generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        status = 'passed-with-question-export-unsupported'
        boundary = 'sealed-candidate-actual-orchestrator-process-worker-bound'
        candidate = [ordered]@{ id = 'kc-integrated-settings-v1212-20260830T183500Z'; root = $candidate; manifestPath = $candidateManifest; manifestSha256 = Get-Sha256File $candidateManifest; sealedTreeSha256 = '4f1021038635984154290467011fbb25ab9d88dd7a1a035f059cff309231416a' }
        authority = [ordered]@{ registryVersion = '1.2.12'; registryPath = $registryPath; registrySha256 = Get-Sha256File $registryPath; checkpointPath = $checkpointPath; checkpointSha256 = Get-Sha256File $checkpointPath; releaseRoot = $releaseRoot }
        process = [ordered]@{ executable = $orchestratorExe; executableSha256 = Get-Sha256File $orchestratorExe; processId = $process.Id; startedAt = $orchestratorStartedAt.ToString('o'); independentProcess = $true; namedPipe = $script:pipeName; runtimeIpcConfig = $ipcConfigPath; runtimeIpcConfigSha256 = Get-Sha256File $ipcConfigPath; workerLaunchAuthority = 'OrchestratorHost queue accepted durable task.submit and created worker request/result attempts'; stoppedByHarnessAfterTerminalEvidence = $true }
        ordinaryText = [ordered]@{
            status = [string]$ordinaryProjection.status; taskId = $ordinary.taskId; original = $ordinaryText; immutableTaskRoot = $ordinary.jobRoot; manifestPath = $ordinary.manifestPath; manifestSha256 = $ordinary.manifestSha256
            submitReceipt = $ordinarySubmit.payload
            cleanedText = [string]$learningBundle.items[0].cleaning.cleaned_text
            classification = @($learningBundle.items[0].units | ForEach-Object { [ordered]@{ categoryId = $_.category_id; subtype = $_.subtype; riskState = $_.risk_state; state = $_.state } })
            cards = @($cardBundle.cards | ForEach-Object { [ordered]@{ cardId = $_.card_id; template = $_.template; categoryId = $_.category_id; front = [string](@($_.front_blocks.text) -join "`n"); back = [string](@($_.back_blocks.text) -join "`n") } })
            workers = @($ordinaryResults | ForEach-Object { [ordered]@{ workerId = $_.value.worker.id; workerVersion = $_.value.worker.version; operation = $_.value.operation; status = $_.value.status; resultPath = $_.path; resultSha256 = $_.sha256 } })
            deepSeekCalls = @($learningBundle.ai_trace).Count
            anki = [ordered]@{ worker = 'export.anki@1.2.1'; authorizedDeck = $AnkiDeck; writeCount = $items.Count; noteIds = @($readback.noteId); cardIds = @($readback.ankiCardIds | ForEach-Object { $_ }); readbackActions = @('notesInfo','cardsInfo'); exactReadback = $readback }
            artifacts = [ordered]@{ learningBundlePath = $learningBundlePath; learningBundleSha256 = Get-Sha256File $learningBundlePath; cardBundlePath = $cardBundlePath; cardBundleSha256 = Get-Sha256File $cardBundlePath; itemsPath = $itemsPath; itemsSha256 = Get-Sha256File $itemsPath }
        }
        ocrHandoff = [ordered]@{
            status = [string]$ocrProjection.status; taskId = $ocrTask.taskId; immutableTaskRoot = $ocrTask.jobRoot; manifestPath = $ocrTask.manifestPath; manifestSha256 = $ocrTask.manifestSha256; submitReceipt = $ocrSubmit.payload
            source = [ordered]@{ handoffPath = $ocrTask.handoffPath; handoffSha256 = $ocrTask.handoffSha256; markdownSha256 = $ocrTask.markdownSha256; structuredSha256 = $ocrTask.structuredSha256; mineruRerun = $false; mineruCalls = 0 }
            cleanedText = [string]$ocrLearningBundle.items[0].cleaning.cleaned_text
            classification = @($ocrLearningBundle.items[0].units | ForEach-Object { [ordered]@{ categoryId = $_.category_id; subtype = $_.subtype; riskState = $_.risk_state; state = $_.state } })
            cards = @($ocrCardBundle.cards | ForEach-Object { [ordered]@{ cardId = $_.card_id; template = $_.template; categoryId = $_.category_id; front = [string](@($_.front_blocks.text) -join "`n"); back = [string](@($_.back_blocks.text) -join "`n") } })
            workers = @($ocrResults | ForEach-Object { [ordered]@{ workerId = $_.value.worker.id; workerVersion = $_.value.worker.version; operation = $_.value.operation; status = $_.value.status; resultPath = $_.path; resultSha256 = $_.sha256 } })
            deepSeekCalls = @($ocrLearningBundle.ai_trace).Count
            anki = [ordered]@{ worker = 'export.anki@1.2.1'; authorizedDeck = $AnkiDeck; writeCount = $ocrItems.Count; noteIds = @($ocrReadback.noteId); cardIds = @($ocrReadback.ankiCardIds | ForEach-Object { $_ }); readbackActions = @('notesInfo','cardsInfo'); exactReadback = $ocrReadback }
            artifacts = [ordered]@{ learningBundlePath = $ocrLearningBundlePath; learningBundleSha256 = Get-Sha256File $ocrLearningBundlePath; cardBundlePath = $ocrCardBundlePath; cardBundleSha256 = Get-Sha256File $ocrCardBundlePath; itemsPath = $ocrItemsPath; itemsSha256 = Get-Sha256File $ocrItemsPath }
        }
        inlineQuestion = [ordered]@{
            status = [string]$questionFinalProjection.status; taskId = $question.taskId; originalQuestion = $questionText; immutableTaskRoot = $question.jobRoot; manifestPath = $question.manifestPath; manifestSha256 = $question.manifestSha256
            submitReceipt = $questionSubmit.payload; initialProjection = $questionProjection; answer = $answerText; answerArtifact = $answerArtifact
            provideInputAction = 'question.make-cards'; makeCardsReceipt = $makeCardsReceipt.payload
            cards = @($questionCardBundle.cards | ForEach-Object { [ordered]@{ cardId = $_.card_id; template = $_.template; categoryId = $_.category_id; front = [string](@($_.front_blocks.text) -join "`n"); back = [string](@($_.back_blocks.text) -join "`n") } })
            workers = @($questionResults | ForEach-Object { [ordered]@{ workerId = $_.value.worker.id; workerVersion = $_.value.worker.version; operation = $_.value.operation; status = $_.value.status; resultPath = $_.path; resultSha256 = $_.sha256 } })
            anki = [ordered]@{ status = 'unsupported-existing-public-chain'; writeCount = 0; readbackCount = 0; exactGap = 'ResolveQuestionResumeAsync emits only question-make-cards content.deepseek stage with routing destinations=[]; no public question-card export action/stage is present.'; userActionRequired = $false; futureCoreChangeRequired = $true }
            cardBundlePath = $questionCardBundlePath; cardBundleSha256 = Get-Sha256File $questionCardBundlePath
        }
        ipc = [ordered]@{ protocol = '1.0 task-command/task-query over append-only IPC v1.1 configuration'; taskSubmitCount = 3; taskProvideInputCount = 1; exchangeCount = $script:exchangeSequence; evidenceRoot = $ipcRoot }
        externalEffects = [ordered]@{ deepSeekCalls = @($learningBundle.ai_trace).Count + @($ocrLearningBundle.ai_trace).Count + 2; ankiWrites = $items.Count + $ocrItems.Count; ankiReadbackCalls = 4; otherAnkiDeckWrites = 0; remNoteWrites = 0; mineruCalls = 0; physicalSpeakerCalls = 0; secretsPersisted = $false; automaticUnknownExternalStateRetry = $false }
    }
    $receiptPath = Join-Path $evidence 'execution-receipt.json'
    Write-JsonOnce $receiptPath $receipt
    $readable = @"
# Sealed candidate actual Orchestrator process Canary

- status: passed-with-question-export-unsupported
- boundary: sealed-candidate-actual-orchestrator-process-worker-bound
- Orchestrator PID: $($process.Id)
- Orchestrator EXE SHA-256: $(Get-Sha256File $orchestratorExe)
- candidate manifest SHA-256: $(Get-Sha256File $candidateManifest)
- authority registry SHA-256: $(Get-Sha256File $registryPath)
- Named Pipe: $script:pipeName

## Ordinary text -> Content 0.7.0 -> Anki 1.2.1

Original:

$ordinaryText

Cleaned:

$([string]$learningBundle.items[0].cleaning.cleaned_text)

Classification: $([string]$learningBundle.items[0].units[0].category_id) / $([string]$learningBundle.items[0].units[0].subtype)

Cards and exact Anki readback:

$(@($readback | ForEach-Object { "- noteId=$($_.noteId); cardIds=$($_.ankiCardIds -join ','); deck=$($_.deck)`n  - front: $($_.front)`n  - back: $($_.back)`n  - tags: $($_.tags -join ', ')" }) -join "`n")

## Existing OCR artifacts -> Content 0.7.0 -> Anki 1.2.1

- OCR consumer handoff SHA-256: $($ocrTask.handoffSha256)
- OCR Markdown SHA-256: $($ocrTask.markdownSha256)
- OCR structured SHA-256: $($ocrTask.structuredSha256)
- MinerU rerun/calls: false / 0

Cleaned:

$([string]$ocrLearningBundle.items[0].cleaning.cleaned_text)

Cards and exact Anki readback:

$(@($ocrReadback | ForEach-Object { "- noteId=$($_.noteId); cardIds=$($_.ankiCardIds -join ','); deck=$($_.deck)`n  - front: $($_.front)`n  - back: $($_.back)`n  - tags: $($_.tags -join ', ')" }) -join "`n")

## Inline question -> answer -> provide_input/make_cards

Question:

$questionText

Answer:

$answerText

Generated cards:

$(@($questionCardBundle.cards | ForEach-Object { "- $([string](@($_.front_blocks.text) -join ' / ')) -> $([string](@($_.back_blocks.text) -join ' / '))" }) -join "`n")

Anki boundary: the existing public question resume plan ends after the Content `question.make-cards` stage and declares `destinations=[]`; it exposes no Anki export stage/action. No Anki write was attempted for the question task. This is a Core capability gap, not a user setup problem.
"@
    Write-BytesOnce (Join-Path $evidence 'READABLE.md') $utf8.GetBytes($readable)
    [ordered]@{ status = $receipt.status; evidenceRoot = $evidence; receipt = $receiptPath; receiptSha256 = Get-Sha256File $receiptPath; readable = (Join-Path $evidence 'READABLE.md'); readableSha256 = Get-Sha256File (Join-Path $evidence 'READABLE.md'); ordinaryTaskId = $ordinary.taskId; ordinaryNoteIds = @($readback.noteId); ocrTaskId = $ocrTask.taskId; ocrNoteIds = @($ocrReadback.noteId); questionTaskId = $question.taskId; questionAnkiStatus = 'unsupported-existing-public-chain'; orchestratorProcessId = $process.Id } | ConvertTo-Json -Depth 20
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($false)
        $null = $process.WaitForExit(10000)
    }
    if ($null -ne $stdoutTask) { [IO.File]::WriteAllText($processStdout, $stdoutTask.GetAwaiter().GetResult(), $utf8) }
    if ($null -ne $stderrTask) { [IO.File]::WriteAllText($processStderr, $stderrTask.GetAwaiter().GetResult(), $utf8) }
    if ($null -ne $process) { $process.Dispose() }
}

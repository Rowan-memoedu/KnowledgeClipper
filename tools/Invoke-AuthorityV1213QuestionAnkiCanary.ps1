[CmdletBinding()]
param(
    [string]$ReleaseRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\releases\knowledge-clipper-authority-v1.2.13-20260830T100000Z\win-x64',
    [string]$AuthorityRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\contracts\authority\worker-registry\v1.2.13-20260830T100000Z',
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-question-anki-v1.2.13\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$release = [IO.Path]::GetFullPath($ReleaseRoot)
$authority = [IO.Path]::GetFullPath($AuthorityRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $evidence) { throw "Append-only evidence root already exists: $evidence" }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$jobsRoot = Join-Path $evidence 'jobs'
$ipcRoot = Join-Path $evidence 'ipc'
[IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
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
    Write-BytesOnce $Path $utf8.GetBytes((($Value | ConvertTo-Json -Depth 100) + "`n"))
}

function Get-Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = Get-Sha256File $item.FullName }
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
    [string]$DataClass = 'personal',
    [array]$InputRefs = @()
) {
    $relative = "artifacts/$ArtifactId/artifact.json"
    $path = Join-Path $JobRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $manifest = [ordered]@{
        schemaVersion = '1.0.0'
        artifactId = $ArtifactId
        kind = $Kind
        contractVersion = '1.0.0'
        mediaType = $MediaType
        files = $Files
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        producer = [ordered]@{ stageId = $StageId; workerId = $WorkerId; workerVersion = $WorkerVersion; attemptId = ('client-' + [Guid]::NewGuid().ToString('N')) }
        inputRefs = $InputRefs
        dataClass = $DataClass
        metadata = [ordered]@{ entrypoint = $Entrypoint }
    }
    Write-JsonOnce $path $manifest
    [ordered]@{ artifactRef = $ArtifactId; kind = $Kind; contractVersion = '1.0.0'; manifestPath = $relative; expectedSha256 = Get-Sha256File $path }
}

function New-CaptureArtifact([string]$JobRoot, [string]$TaskId, [string]$Text) {
    $artifactId = 'capture-bundle'
    $textRelative = "artifacts/$artifactId/question-turn-0001.txt"
    $textPath = Join-Path $JobRoot $textRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $textBytes = $utf8.GetBytes($Text)
    Write-BytesOnce $textPath $textBytes
    $capture = [ordered]@{
        schemaVersion = 'capture-bundle.v1'
        captureId = "capture-$TaskId"
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        intent = 'question'
        source = 'question'
        modeSnapshot = [ordered]@{ workflow = 'card'; contextEnabled = $false; contextGranularity = 'sentence'; contextApplied = $false; imageHandling = 'original' }
        artifacts = @([ordered]@{ artifactRef = 'capture.question.user.0001'; relativePath = $textRelative; sha256 = Get-Sha256Bytes $textBytes; bytes = $textBytes.LongLength; mediaType = 'text/plain'; encoding = 'utf-8' })
        selectedTextArtifactRef = $null
        contextSegments = $null
        questionImages = @()
        answerImages = @()
        questionTurns = @([ordered]@{ turnIndex = 1; role = 'user'; artifactRef = 'capture.question.user.0001' })
    }
    $captureRelative = "artifacts/$artifactId/capture-bundle.v1.json"
    $capturePath = Join-Path $JobRoot $captureRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $capturePath $capture
    New-ArtifactManifest $JobRoot $artifactId 'capture-bundle' 'application/vnd.rqc.capture-bundle+json' @(
        [ordered]@{ path = $captureRelative; sha256 = Get-Sha256File $capturePath; bytes = (Get-Item -LiteralPath $capturePath).Length; mediaType = 'application/vnd.rqc.capture-bundle+json' },
        [ordered]@{ path = $textRelative; sha256 = Get-Sha256Bytes $textBytes; bytes = $textBytes.LongLength; mediaType = 'text/plain' }
    ) 'desktop-capture' 'desktop.capture' '1.0.0' $captureRelative 'personal'
}

function New-AnkiConfigArtifact([string]$JobRoot, [string]$ExpectedProfile) {
    $template = Join-Path $release 'workers\anki\config\anki-export.v1.example.json'
    $config = Get-Content -LiteralPath $template -Raw | ConvertFrom-Json -Depth 100
    $config.expected_profile = $ExpectedProfile
    $config.endpoint = 'http://127.0.0.1:8765'
    $config.provision_managed = $false
    foreach ($property in @($config.deck_routes.PSObject.Properties)) { $config.deck_routes.($property.Name) = $AnkiDeck }
    $artifactId = 'anki-config'
    $payloadRelative = "artifacts/$artifactId/anki-export.v1.json"
    $payloadPath = Join-Path $JobRoot $payloadRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $payloadPath $config
    New-ArtifactManifest $JobRoot $artifactId 'anki-export-config' 'application/json' @(
        [ordered]@{ path = $payloadRelative; sha256 = Get-Sha256File $payloadPath; bytes = (Get-Item -LiteralPath $payloadPath).Length; mediaType = 'application/json' }
    ) 'desktop-settings' 'desktop.settings' '1.0.0' $payloadRelative
}

function New-TaskSettingsArtifact([string]$JobRoot, [string]$TaskId, $AnkiReference) {
    $source = [ordered]@{
        schemaVersion = 'knowledge-clipper-workflow-settings.v3'
        activeProfileId = 'question-anki-canary'
        workflow = [ordered]@{ processingProfile = 'cards'; destinations = @('anki') }
        capture = [ordered]@{ contextEnabled = $false; contextGranularity = 'sentence'; imageHandling = 'original' }
        content = [ordered]@{ promptSet = 'open-md-v2'; categoryRegistry = 'category-registry/2.0.0'; providerMode = 'live' }
        ankiConfigArtifactRef = $AnkiReference.artifactRef
        ankiConfigSha256 = $AnkiReference.expectedSha256
    }
    $sourceSha = Get-Sha256Bytes $utf8.GetBytes(($source | ConvertTo-Json -Depth 30 -Compress))
    $snapshot = [ordered]@{
        schemaVersion = 'knowledge-clipper-task-settings-snapshot.v3'
        taskId = $TaskId
        frozenAt = [DateTimeOffset]::UtcNow.ToString('o')
        source = [ordered]@{ activeProfileId = 'question-anki-canary'; settingsSha256 = $sourceSha; precedence = @('taskOverride','activeProfile','moduleDefault') }
        workflow = [ordered]@{ processingProfile = 'cards'; destinations = @('anki') }
        modules = [ordered]@{
            capture = [ordered]@{ contextEnabled = $false; contextGranularity = 'sentence'; imageHandling = 'original' }
            content = [ordered]@{ promptSet = 'open-md-v2'; categoryRegistry = 'category-registry/2.0.0'; providerMode = 'live' }
            anki = [ordered]@{ configArtifactRef = $AnkiReference.artifactRef; expectedSha256 = $AnkiReference.expectedSha256; secretReferences = @() }
        }
        secretReferences = @()
    }
    $snapshotBytes = $utf8.GetBytes((($snapshot | ConvertTo-Json -Depth 100) + "`n"))
    $artifactId = 'task-settings-snapshot-' + (Get-Sha256Bytes $snapshotBytes).Substring(0, 24)
    $payloadRelative = "artifacts/$artifactId/task-settings-snapshot.v1.json"
    $payloadPath = Join-Path $JobRoot $payloadRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-BytesOnce $payloadPath $snapshotBytes
    $reference = New-ArtifactManifest $JobRoot $artifactId 'task-settings-snapshot' 'application/vnd.knowledge-clipper.task-settings-snapshot+json' @(
        [ordered]@{ path = $payloadRelative; sha256 = Get-Sha256File $payloadPath; bytes = (Get-Item -LiteralPath $payloadPath).Length; mediaType = 'application/json' }
    ) 'task-settings' 'orchestrator.settings-adapter' '1.0.0' $payloadRelative 'sensitive' @($AnkiReference)
    [ordered]@{ reference = $reference; payloadPath = $payloadPath; sourceSha256 = $sourceSha }
}

function New-QuestionTask([string]$TaskId, [string]$Question, [string]$ExpectedProfile) {
    $jobRoot = Join-Path $jobsRoot $TaskId
    [IO.Directory]::CreateDirectory((Join-Path $jobRoot 'plans')) | Out-Null
    $captureReference = New-CaptureArtifact $jobRoot $TaskId $Question
    $ankiReference = New-AnkiConfigArtifact $jobRoot $ExpectedProfile
    $settings = New-TaskSettingsArtifact $jobRoot $TaskId $ankiReference
    $intent = [ordered]@{ schemaVersion = 'desktop-intent.v1'; taskId = $TaskId; intent = 'clip.question'; inputArtifactRef = $captureReference.artifactRef; requestedOutputs = @('question.answer') }
    $intentPath = Join-Path $jobRoot 'plans\desktop-intent.v1.json'
    Write-JsonOnce $intentPath $intent
    $refs = @($captureReference, $ankiReference, $settings.reference)
    $inputRefs = @($refs | ForEach-Object { [ordered]@{ artifactRef = $_.artifactRef; kind = $_.kind; contractVersion = $_.contractVersion; expectedSha256 = $_.expectedSha256 } })
    $legacyManifest = [ordered]@{
        schemaVersion = '1.0.0'
        taskId = $TaskId
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        createdBy = [ordered]@{ application = 'KnowledgeClipper.AuthorityV1213QuestionAnkiCanary'; version = '1.0.0' }
        intent = 'clip.question'
        executionProfile = 'cards-anki'
        inputRefs = $inputRefs
        requestedOutputs = @('question.answer')
        initialPlanRef = 'plans/desktop-intent.v1.json'
        retentionClass = 'evidence'
        metadata = [ordered]@{ compatibilityRole = 'frozen-v1-worker-loader'; authoritativeManifest = 'task.manifest.v2.json'; authorizedAnkiDeck = $AnkiDeck }
    }
    Write-JsonOnce (Join-Path $jobRoot 'task.manifest.v1.json') $legacyManifest
    $manifest = [ordered]@{
        schemaVersion = '2.0.0'
        taskId = $TaskId
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        createdBy = [ordered]@{ application = 'KnowledgeClipper.AuthorityV1213QuestionAnkiCanary'; version = '1.0.0' }
        intent = 'clip.question'
        executionProfile = 'cards'
        destinations = @('anki')
        inputRefs = $inputRefs
        requestedOutputs = @('question.answer')
        initialPlanRef = 'plans/desktop-intent.v1.json'
        retentionClass = 'evidence'
        metadata = [ordered]@{ canary = 'sealed-actual-orchestrator-question-anki-v1.2.13'; authorizedAnkiDeck = $AnkiDeck; remnote = $false; imageOcr = $false; speech = $false }
    }
    $manifestPath = Join-Path $jobRoot 'task.manifest.v2.json'
    Write-JsonOnce $manifestPath $manifest
    [ordered]@{
        taskId = $TaskId; jobRoot = $jobRoot; manifestPath = $manifestPath; manifestRelative = "$TaskId/task.manifest.v2.json"
        manifestSha256 = Get-Sha256File $manifestPath; captureReference = $captureReference; ankiReference = $ankiReference
        settingsReference = $settings.reference; settingsPayloadPath = $settings.payloadPath
    }
}

function New-QuestionTurnArtifact([string]$JobRoot, [string]$CommandId, [string]$Text) {
    $artifactId = 'question.turn.0002'
    $root = "commands/$CommandId/question-turn"
    $payloadRelative = "$root/payload.txt"
    $payloadPath = Join-Path $JobRoot $payloadRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $bytes = $utf8.GetBytes($Text)
    Write-BytesOnce $payloadPath $bytes
    New-ArtifactManifest $JobRoot $artifactId 'question.turn' 'text/plain' @(
        [ordered]@{ path = $payloadRelative; sha256 = Get-Sha256Bytes $bytes; bytes = $bytes.LongLength; mediaType = 'text/plain' }
    ) 'desktop-question' 'desktop.capture' '1.0.0' $payloadRelative 'personal'
}

function Invoke-Anki([string]$Action, $Parameters = @{}) {
    $body = [ordered]@{ action = $Action; version = 6; params = $Parameters } | ConvertTo-Json -Depth 30 -Compress
    $response = Invoke-RestMethod -Uri 'http://127.0.0.1:8765' -Method Post -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 15
    if ($null -ne $response.error) { throw "AnkiConnect $Action failed: $($response.error)" }
    $response.result
}

function Invoke-Pipe($Payload, [string]$MessageType) {
    $correlation = 'authority-v1213-' + [Guid]::NewGuid().ToString('N')
    $request = [ordered]@{ protocolVersion = '1.0'; correlationId = $correlation; messageType = $MessageType; payload = $Payload }
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
        if (-not $response.ok) { throw "Named Pipe protocol error: $($response.error.code) $($response.error.message)" }
        $response
    }
    finally { $pipe.Dispose() }
}

function New-Command([string]$Type, [string]$TaskId, [string]$PayloadRef, [string]$ExpectedState = '') {
    $commandId = 'command-' + [Guid]::NewGuid().ToString('N')
    $value = [ordered]@{
        schemaVersion = '1.0.0'; commandId = $commandId; operationId = ('operation-' + [Guid]::NewGuid().ToString('N'))
        taskId = $(if ($Type -eq 'task.submit') { $null } else { $TaskId }); type = $Type; issuedAt = [DateTimeOffset]::UtcNow.ToString('o')
        idempotencyKey = Get-Sha256Bytes $utf8.GetBytes("$Type|$TaskId|$PayloadRef")
        payloadRef = $PayloadRef
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedState)) { $value.expectedState = $ExpectedState }
    $value
}

function Get-TaskProjection([string]$TaskId) {
    $query = [ordered]@{ schemaVersion = '2.2.0'; queryId = ('query-' + [Guid]::NewGuid().ToString('N')); type = 'task.get'; taskId = $TaskId }
    $response = Invoke-Pipe $query 'task-query'
    if (@($response.payload.items).Count -eq 0) { return $null }
    @($response.payload.items)[0]
}

function Wait-Task([string]$TaskId, [string[]]$States, [int]$TimeoutSeconds = 900, [string]$ExcludedTargetToken = '') {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $projection = Get-TaskProjection $TaskId
        if ($null -ne $projection -and $States -contains [string]$projection.status) {
            if ([string]::IsNullOrWhiteSpace($ExcludedTargetToken) -or [string]$projection.questionTargetToken -ne $ExcludedTargetToken) { return $projection }
        }
        Start-Sleep -Milliseconds 750
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
    if ((Get-Sha256File $manifestPath) -ne ([string]$Reference.expectedSha256).ToLowerInvariant()) { throw "Artifact manifest hash mismatch: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
    $file = @($manifest.files)[0]
    if ($null -ne $manifest.metadata.entrypoint) { $file = @($manifest.files | Where-Object { $_.path -eq $manifest.metadata.entrypoint })[0] }
    $payloadPath = Join-Path $JobRoot ([string]$file.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if ((Get-Sha256File $payloadPath) -ne ([string]$file.sha256).ToLowerInvariant()) { throw "Artifact payload hash mismatch: $payloadPath" }
    $raw = Get-Content -LiteralPath $payloadPath -Raw
    try { $value = $raw | ConvertFrom-Json -Depth 100 } catch { $value = $raw }
    [ordered]@{ manifestPath = $manifestPath; manifestSha256 = Get-Sha256File $manifestPath; payloadPath = $payloadPath; payloadSha256 = Get-Sha256File $payloadPath; value = $value }
}

$orchestratorExe = Join-Path $release 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$registryPath = Join-Path $authority 'worker-registry.v1.json'
$checkpointPath = Join-Path $authority 'checkpoint.json'
$ipcSource = Join-Path $release 'contracts\core\v2.3\playback-control\orchestrator-ipc.v1.1.json'
$adapterConfig = Join-Path $release 'contracts\core\v2.1\profile-resolver.v2.1.json'
foreach ($path in @($orchestratorExe, $registryPath, $checkpointPath, $ipcSource, $adapterConfig)) { if (-not (Test-Path -LiteralPath $path)) { throw "Required authority file is missing: $path" } }
if ((Get-Sha256File $registryPath) -ne '63bd9525e97e309ed9d8047e36f23668d6f7ae3cebdeea62157494cef7293cec') { throw 'Authority v1.2.13 registry identity drifted.' }
if ((Get-Sha256File $checkpointPath) -ne 'af9d8494a47ace7cf28acad561cca648d1d4ec7bdcbabb305a3fe31fca608be7') { throw 'Authority v1.2.13 checkpoint identity drifted.' }
if ((Get-Sha256File $orchestratorExe) -ne '7566601ed455e7fe1c48cfab29b2a4074ec4b05cebad30bae8ac269153e75a1b') { throw 'Authority v1.2.13 Orchestrator identity drifted.' }

$ankiVersion = Invoke-Anki 'version'
$ankiProfile = Invoke-Anki 'getActiveProfile'
$ankiDecks = @(Invoke-Anki 'deckNames')
if ($ankiVersion -ne 6 -or [string]::IsNullOrWhiteSpace([string]$ankiProfile) -or $ankiDecks -notcontains $AnkiDeck) { throw 'Anki preflight did not confirm API v6, active profile, and authorized deck.' }
$deepSeekKey = [Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY', 'Process')
$deepSeekSource = 'process'
if ([string]::IsNullOrWhiteSpace($deepSeekKey)) { $deepSeekKey = [Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY', 'User'); $deepSeekSource = 'user' }
if ([string]::IsNullOrWhiteSpace($deepSeekKey)) {
    $configPath = 'C:\Users\fy\Documents\New project\DeepSeek API 配置.md'
    if (Test-Path -LiteralPath $configPath) {
        $match = [regex]::Match((Get-Content -LiteralPath $configPath -Raw), '(?im)^\s*(?:DeepSeek\s*)?API\s*Key\s*[=:]\s*["'']?([^\s"'']+)')
        if ($match.Success) { $deepSeekKey = $match.Groups[1].Value; $deepSeekSource = 'local-config' }
    }
}
if ([string]::IsNullOrWhiteSpace($deepSeekKey)) { throw 'DeepSeek credential unavailable through the admitted local discovery sequence.' }
Write-JsonOnce (Join-Path $evidence 'preflight.json') ([ordered]@{
    schemaVersion = 'knowledge-clipper-authority-v1213-question-anki-preflight.v1'; status = 'passed'; generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    anki = [ordered]@{ version = $ankiVersion; activeProfile = $ankiProfile; authorizedDeck = $AnkiDeck; deckPresent = $true; deckCount = $ankiDecks.Count; actions = @('version','getActiveProfile','deckNames'); externalMutation = $false }
    deepSeek = [ordered]@{ credentialPresent = $true; source = $deepSeekSource; valueRecorded = $false; taskPackageSecretReferences = @() }
})

$script:pipeName = 'KnowledgeClipper.AuthorityV1213.' + [Guid]::NewGuid().ToString('N')
$script:exchangeSequence = 0
$ipcConfig = Get-Content -LiteralPath $ipcSource -Raw | ConvertFrom-Json -Depth 100
$ipcConfig.pipeName = $script:pipeName
$ipcConfig.endpoint = "\\.\pipe\$($script:pipeName)"
$ipcConfigPath = Join-Path $evidence 'orchestrator-ipc.runtime.v1.1.json'
Write-JsonOnce $ipcConfigPath $ipcConfig

$stdoutPath = Join-Path $evidence 'orchestrator.stdout.log'
$stderrPath = Join-Path $evidence 'orchestrator.stderr.log'
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $orchestratorExe
$startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($orchestratorExe)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.Environment['AI_PRODUCER_API_KEY'] = $deepSeekKey
foreach ($argument in @('serve','--ipc-config',$ipcConfigPath,'--jobs-root',$jobsRoot,'--registry',$registryPath,'--release-root',$release,'--max-active-tasks','1','--adapter-config',$adapterConfig)) { $startInfo.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$stdoutTask = $null
$stderrTask = $null
$task = $null
$status = 'failed'
$failure = $null
$orchestratorStartedAt = [DateTimeOffset]::UtcNow
try {
    if (-not $process.Start()) { throw 'Failed to start authority v1.2.13 Orchestrator.' }
    $orchestratorPid = $process.Id
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt = 0; $attempt -lt 120 -and -not $ready; $attempt++) {
        if ($process.HasExited) { throw "Orchestrator exited during startup: $($process.ExitCode)" }
        try {
            $null = Invoke-Pipe ([ordered]@{ schemaVersion = '2.2.0'; queryId = ('ready-' + [Guid]::NewGuid().ToString('N')); type = 'task.query_recent'; limit = 1 }) 'task-query'
            $ready = $true
        } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Orchestrator Named Pipe did not become ready.' }

    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')
    $initialQuestion = '为什么可审计系统必须让每次重试生成新的 attempt，而不能覆盖旧证据？请用两点说明。'
    $followUpQuestion = '如果要学习一个新概念，怎样把主动回忆落实为一个可执行的三步流程？'
    $task = New-QuestionTask "question-anki-v1213-$stamp" $initialQuestion ([string]$ankiProfile)
    $submitCommand = New-Command 'task.submit' $task.taskId $task.manifestRelative
    $submitReceipt = Invoke-Pipe $submitCommand 'task-command'
    $initialProjection = Wait-Task $task.taskId @('waiting_input','partial','failed','cancelled') 900
    if ($initialProjection.status -ne 'waiting_input' -or -not $initialProjection.answerAvailable) { throw "Initial question ended as $($initialProjection.status)." }
    $initialAnswerArtifact = Read-ArtifactPayload $task.jobRoot $initialProjection.questionAnswerRef
    $initialAnswer = if ($initialAnswerArtifact.value -is [string]) { ([string]$initialAnswerArtifact.value).Trim() } else { [string]$initialAnswerArtifact.value.turns[-1].answer_text }
    $afterInitial = Get-WorkerResults $task.jobRoot
    if (@($afterInitial | Where-Object { $_.value.worker.id -eq 'export.anki' }).Count -ne 0) { throw 'Initial question wrote to Anki before make_cards.' }

    $followCommandId = 'command-' + [Guid]::NewGuid().ToString('N')
    $followTurn = New-QuestionTurnArtifact $task.jobRoot $followCommandId $followUpQuestion
    $followActionRelative = "commands/$followCommandId/question-action.v2.json"
    $followActionPath = Join-Path $task.jobRoot $followActionRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $followActionPath ([ordered]@{ schemaVersion = 'knowledge-clipper-question-action.v2'; action = 'question.follow-up'; targetToken = [string]$initialProjection.questionTargetToken; turnIndex = 1; questionTurnRef = $followTurn })
    $followCommand = New-Command 'task.provide_input' $task.taskId $followActionRelative 'waiting_input'
    $followCommand.commandId = $followCommandId
    $followReceipt = Invoke-Pipe $followCommand 'task-command'
    $followProjection = Wait-Task $task.taskId @('waiting_input','partial','failed','cancelled') 900 ([string]$initialProjection.questionTargetToken)
    if ($followProjection.status -ne 'waiting_input' -or [int]$followProjection.questionTurnCount -ne 2) { throw "Follow-up did not publish turn 2: $($followProjection.status)." }
    $followAnswerArtifact = Read-ArtifactPayload $task.jobRoot $followProjection.questionAnswerRef
    $followAnswer = if ($followAnswerArtifact.value -is [string]) { ([string]$followAnswerArtifact.value).Trim() } else { [string]$followAnswerArtifact.value.turns[-1].answer_text }
    $afterFollow = Get-WorkerResults $task.jobRoot
    if (@($afterFollow | Where-Object { $_.value.worker.id -eq 'export.anki' }).Count -ne 0) { throw 'Follow-up wrote to Anki before make_cards.' }

    $makeCardsCommandId = 'command-' + [Guid]::NewGuid().ToString('N')
    $makeCardsRelative = "commands/$makeCardsCommandId/question-action.v2.json"
    $makeCardsPath = Join-Path $task.jobRoot $makeCardsRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $makeCardsPath ([ordered]@{ schemaVersion = 'knowledge-clipper-question-action.v2'; action = 'question.make-cards'; targetToken = [string]$followProjection.questionTargetToken })
    $makeCardsCommand = New-Command 'task.provide_input' $task.taskId $makeCardsRelative 'waiting_input'
    $makeCardsCommand.commandId = $makeCardsCommandId
    $makeCardsReceipt = Invoke-Pipe $makeCardsCommand 'task-command'
    $finalProjection = Wait-Task $task.taskId @('succeeded','partial','failed','cancelled') 900
    if ($finalProjection.status -ne 'succeeded') { throw "make_cards ended as $($finalProjection.status)." }

    $resultsBeforeReplay = Get-WorkerResults $task.jobRoot
    $contentResults = @($resultsBeforeReplay | Where-Object { $_.value.worker.id -eq 'content.deepseek' })
    $ankiResults = @($resultsBeforeReplay | Where-Object { $_.value.worker.id -eq 'export.anki' })
    $otherResults = @($resultsBeforeReplay | Where-Object { $_.value.worker.id -notin @('content.deepseek','export.anki') })
    if ($contentResults.Count -ne 3 -or $ankiResults.Count -ne 1 -or $otherResults.Count -ne 0) { throw "Expected Content=3, Anki=1, other=0; got $($contentResults.Count), $($ankiResults.Count), $($otherResults.Count)." }
    $cardBundlePath = Get-ChildItem -LiteralPath $task.jobRoot -Recurse -Filter 'card-bundle.schema-v1.json' | Sort-Object LastWriteTimeUtc | Select-Object -Last 1 -ExpandProperty FullName
    $itemsPath = Get-ChildItem -LiteralPath $task.jobRoot -Recurse -Filter 'items.ndjson' | Where-Object { $_.FullName -match 'anki-export' } | Select-Object -Last 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($cardBundlePath) -or [string]::IsNullOrWhiteSpace($itemsPath)) { throw 'Question CardBundle or Anki items evidence is missing.' }
    $cardBundle = Get-Content -LiteralPath $cardBundlePath -Raw | ConvertFrom-Json -Depth 100
    $items = @(Get-Content -LiteralPath $itemsPath | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
    if ($items.Count -lt 1 -or @($cardBundle.cards).Count -ne $items.Count) { throw 'Anki write count does not match CardBundle.' }
    $noteIds = @($items | ForEach-Object { [long]$_.note_id })
    $notes = @(Invoke-Anki 'notesInfo' @{ notes = $noteIds })
    $ankiCardIds = @($notes | ForEach-Object { @($_.cards) } | ForEach-Object { [long]$_ })
    $cardsInfo = @(Invoke-Anki 'cardsInfo' @{ cards = $ankiCardIds })
    if ($notes.Count -ne $items.Count -or $cardsInfo.Count -ne $ankiCardIds.Count -or @($cardsInfo | Where-Object { $_.deckName -ne $AnkiDeck }).Count -ne 0) { throw 'Anki notesInfo/cardsInfo readback count or deck mismatch.' }
    $readback = @()
    foreach ($item in $items) {
        $bundleCard = @($cardBundle.cards | Where-Object { [string]$_.card_id -eq [string]$item.card_id })
        $note = @($notes | Where-Object { [long]$_.noteId -eq [long]$item.note_id })
        if ($bundleCard.Count -ne 1 -or $note.Count -ne 1) { throw 'Anki item identity does not match CardBundle.' }
        $front = [string](@($bundleCard[0].front_blocks.text) -join "`n")
        $back = [string](@($bundleCard[0].back_blocks.text) -join "`n")
        if ([string]$note[0].fields.Front.value -ne $front -or [string]$note[0].fields.Back.value -ne $back) { throw 'Anki exact Front/Back readback mismatch.' }
        $readback += [ordered]@{ contentCardId = [string]$item.card_id; noteId = [long]$item.note_id; cardIds = @($note[0].cards); deck = $AnkiDeck; front = $front; back = $back; tags = @($note[0].tags); frontExact = $true; backExact = $true; deckExact = $true }
    }

    $replayReceipt = Invoke-Pipe $makeCardsCommand 'task-command'
    Start-Sleep -Seconds 2
    $resultsAfterReplay = Get-WorkerResults $task.jobRoot
    $ankiAfterReplay = @($resultsAfterReplay | Where-Object { $_.value.worker.id -eq 'export.anki' })
    $notesAfterReplay = @(Invoke-Anki 'notesInfo' @{ notes = $noteIds })
    if ($resultsAfterReplay.Count -ne $resultsBeforeReplay.Count -or $ankiAfterReplay.Count -ne 1 -or $notesAfterReplay.Count -ne $notes.Count) { throw 'Exact make_cards command replay created duplicate work or notes.' }

    $learningBundles = @(Get-ChildItem -LiteralPath $task.jobRoot -Recurse -Filter 'learning-bundle.schema-v1.json' | Sort-Object FullName)
    $providerCalls = 0
    foreach ($bundleFile in $learningBundles) {
        $bundle = Get-Content -LiteralPath $bundleFile.FullName -Raw | ConvertFrom-Json -Depth 100
        if ($null -ne $bundle.ai_trace.attempts) { $providerCalls += [int]$bundle.ai_trace.attempts }
    }
    $readablePath = Join-Path $evidence 'READABLE.md'
    $readable = @("# authority v1.2.13 真实 Orchestrator 问题链", '', "- 任务：``$($task.taskId)``", "- 状态：``waiting_input -> waiting_input -> succeeded``", "- Anki 目标：``$AnkiDeck``", "- initial/follow_up Anki 写入：``0 / 0``", "- make_cards 写入：``$($items.Count)`` 条 note", "- 同一 make_cards 命令重放新增：``0``", '', '## 初问', '', $initialQuestion, '', '### 回答', '', $initialAnswer, '', '## 追问', '', $followUpQuestion, '', '### 回答', '', $followAnswer, '', '## 最终卡片')
    foreach ($entry in $readback) { $readable += @('', "### note $($entry.noteId)", '', "- Front：$($entry.front)", "- Back：$($entry.back)", "- Tags：$(@($entry.tags) -join ', ')", "- Card IDs：$(@($entry.cardIds) -join ', ')") }
    Write-BytesOnce $readablePath $utf8.GetBytes(($readable -join "`n") + "`n")
    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-authority-v1213-question-anki-canary.v1'; status = 'passed'; completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        boundary = 'sealed-actual-orchestrator-process'; backendHarness = $false; desktopProcessE2E = $false
        authority = [ordered]@{ version = '1.2.13'; releaseRoot = $release; releaseTreeSha256 = '0a82124f5408d5239414c4f995afcc81aec7dc29c04723f8d8ba7bc3fde507fb'; registry = Get-Evidence $registryPath; checkpoint = Get-Evidence $checkpointPath; orchestrator = Get-Evidence $orchestratorExe }
        process = [ordered]@{ pid = $orchestratorPid; startedAt = $orchestratorStartedAt.ToString('o'); executable = $orchestratorExe; namedPipe = $script:pipeName; ipcProtocol = '1.0'; messageTypes = @('task-command','task-query'); workerLaunchOwner = 'KnowledgeClipper.Orchestrator.exe' }
        task = [ordered]@{ taskId = $task.taskId; jobRoot = $task.jobRoot; manifest = Get-Evidence $task.manifestPath; taskSettingsSnapshot = Get-Evidence $task.settingsPayloadPath; ankiConfigManifestSha256 = $task.ankiReference.expectedSha256; initialQuestion = $initialQuestion; followUpQuestion = $followUpQuestion }
        states = @('waiting_input','waiting_input','succeeded')
        initial = [ordered]@{ answer = $initialAnswer; answerArtifact = $initialAnswerArtifact; destinationWrites = 0 }
        followUp = [ordered]@{ answer = $followAnswer; answerArtifact = $followAnswerArtifact; destinationWrites = 0; targetTokenRotated = ([string]$initialProjection.questionTargetToken -ne [string]$followProjection.questionTargetToken) }
        makeCards = [ordered]@{ worker = 'content.deepseek@0.7.0'; cardBundle = Get-Evidence $cardBundlePath; cards = @($readback | ForEach-Object { [ordered]@{ contentCardId = $_.contentCardId; front = $_.front; back = $_.back; tags = $_.tags } }) }
        anki = [ordered]@{ worker = 'export.anki@1.2.1'; deck = $AnkiDeck; writeCount = $items.Count; noteIds = $noteIds; cardIds = $ankiCardIds; readbackActions = @('notesInfo','cardsInfo'); exactReadback = $readback; exactReadbackPassed = $true }
        replay = [ordered]@{ sameCommandId = $makeCardsCommand.commandId; sameIdempotencyKey = $makeCardsCommand.idempotencyKey; newContentAttempts = 0; newAnkiAttempts = 0; newNotes = 0; passed = $true; receipt = $replayReceipt.payload }
        workers = [ordered]@{ contentAttempts = $contentResults.Count; ankiAttempts = $ankiResults.Count; imageAttempts = 0; remnoteAttempts = 0; speechAttempts = 0; providerCalls = $providerCalls; resultEvidence = @($resultsBeforeReplay | ForEach-Object { [ordered]@{ worker = "$($_.value.worker.id)@$($_.value.worker.version)"; stageId = $_.value.stageId; attemptId = $_.value.attemptId; result = Get-Evidence $_.path } }) }
        sideEffects = [ordered]@{ deepSeekCalls = $providerCalls; ankiWrites = $items.Count; ankiExactReadbackCalls = 2; remnoteCalls = 0; mineruCalls = 0; physicalSpeakerInvocations = 0 }
        ipc = [ordered]@{ exchangeCount = $script:exchangeSequence; root = $ipcRoot; submit = $submitReceipt.payload; followUp = $followReceipt.payload; makeCards = $makeCardsReceipt.payload }
        readable = Get-Evidence $readablePath
    }
    $receiptPath = Join-Path $evidence 'execution-receipt.json'
    Write-JsonOnce $receiptPath $receipt
    $status = 'passed'
    Write-Output ([ordered]@{ status = $status; evidenceRoot = $evidence; taskId = $task.taskId; receiptPath = $receiptPath; receiptSha256 = Get-Sha256File $receiptPath; readablePath = $readablePath; notes = $items.Count } | ConvertTo-Json -Depth 20)
}
catch {
    $failure = $_
    $failurePath = Join-Path $evidence 'failure-receipt.json'
    if (-not (Test-Path -LiteralPath $failurePath)) {
        Write-JsonOnce $failurePath ([ordered]@{ schemaVersion = 'knowledge-clipper-authority-v1213-question-anki-failure.v1'; status = 'failed'; failedAt = [DateTimeOffset]::UtcNow.ToString('o'); errorType = $_.Exception.GetType().FullName; error = $_.Exception.Message; taskId = $task.taskId; authorityVersion = '1.2.13'; orchestrator = $orchestratorExe })
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true) | Out-Null
        $process.WaitForExit(15000) | Out-Null
    }
    if ($null -ne $stdoutTask) { Write-BytesOnce $stdoutPath $utf8.GetBytes($stdoutTask.GetAwaiter().GetResult()) }
    if ($null -ne $stderrTask) { Write-BytesOnce $stderrPath $utf8.GetBytes($stderrTask.GetAwaiter().GetResult()) }
    $deepSeekKey = $null
}
if ($null -ne $failure) { throw $failure }

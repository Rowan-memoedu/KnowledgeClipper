param(
    [string]$BuiltWorkerExe,
    [string]$FakeToolExe
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($BuiltWorkerExe)) { $BuiltWorkerExe = Join-Path $testRoot 'bin\KnowledgeClipper.Speech.Worker.exe' }
if ([string]::IsNullOrWhiteSpace($FakeToolExe)) { $FakeToolExe = Join-Path $testRoot 'bin\KnowledgeClipper.Speech.FakeTool.exe' }
$builtWorker = (Resolve-Path -LiteralPath $BuiltWorkerExe).Path
$fakeTool = (Resolve-Path -LiteralPath $FakeToolExe).Path

function Assert-Equal([string]$label, $expected, $actual) {
    if ($expected -cne $actual) { throw "$label expected=[$expected] actual=[$actual]" }
}

function Get-Sha([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-Relative([string]$root, [string]$path) {
    return [Uri]::UnescapeDataString(([Uri]($root.TrimEnd('\') + '\')).MakeRelativeUri([Uri]$path).ToString())
}

function Write-JsonOnce([string]$path, $value) {
    if (Test-Path -LiteralPath $path) { throw "Immutable JSON already exists: $path" }
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    $temporary = Join-Path $directory ('.tmp-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $path)
}

function Get-ReleaseTree([string]$root) {
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = Get-Relative $root $_.FullName
            bytes = $_.Length
            sha256 = Get-Sha $_.FullName
        }
    })
    $material = ($files | ForEach-Object { "$($_.path)|$($_.bytes)|$($_.sha256)" }) -join "`n"
    [long]$totalBytes = 0
    foreach ($file in $files) { $totalBytes += [long]$file.bytes }
    return [ordered]@{
        algorithm = 'sha256(utf8(join(sorted(path|bytes|sha256),LF)))'
        files = $files
        fileCount = $files.Count
        totalBytes = $totalBytes
        treeSha256 = Get-TextSha $material
    }
}

$descriptorText = (& $builtWorker describe --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Built worker describe failed' }
$descriptor = $descriptorText | ConvertFrom-Json
Assert-Equal 'workerId' 'speech.edge' $descriptor.workerId
Assert-Equal 'workerVersion' '1.2.5' $descriptor.workerVersion
Assert-Equal 'contractVersion' '1.0.0' $descriptor.contractVersion
$workerHash = Get-Sha $builtWorker
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$formalParent = Join-Path $testRoot "formal\r$stamp-$($workerHash.Substring(0,8))"
$releaseRoot = Join-Path $formalParent 'win-x64'
$workerDirectory = Join-Path $releaseRoot 'workers\speech'
$publishedWorker = Join-Path $workerDirectory 'KnowledgeClipper.Speech.Worker.exe'
$descriptorDirectory = Join-Path $workerDirectory 'descriptors'
$schemaDirectory = Join-Path $workerDirectory 'schemas\settings-companion'
$fixtureDirectory = Join-Path $workerDirectory 'fixtures\settings-companion'
$evidenceRoot = Join-Path $formalParent 'e'
if (Test-Path -LiteralPath $formalParent) { throw "Append-only formal root already exists: $formalParent" }
New-Item -ItemType Directory -Path $workerDirectory, $descriptorDirectory, $schemaDirectory, $fixtureDirectory, $evidenceRoot | Out-Null
$temporaryWorker = Join-Path $workerDirectory '.tmp-KnowledgeClipper.Speech.Worker.exe'
Copy-Item -LiteralPath $builtWorker -Destination $temporaryWorker
[IO.File]::Move($temporaryWorker, $publishedWorker)
Assert-Equal 'formal EXE hash' $workerHash (Get-Sha $publishedWorker)

Push-Location -LiteralPath $releaseRoot
try {
    $releaseDescriptorText = (& $publishedWorker describe --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Formal release-root describe failed' }
    $releaseDescriptor = $releaseDescriptorText | ConvertFrom-Json
}
finally { Pop-Location }
Assert-Equal 'release descriptor worker' 'speech.edge' $releaseDescriptor.workerId
Assert-Equal 'release descriptor version' '1.2.5' $releaseDescriptor.workerVersion
Write-JsonOnce (Join-Path $evidenceRoot 'describe.json') $releaseDescriptor
Write-JsonOnce (Join-Path $descriptorDirectory 'worker-describe.json') $releaseDescriptor

Push-Location -LiteralPath $releaseRoot
try {
    $companionDescriptorText = (& $publishedWorker settings companion describe --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Formal companion describe failed' }
    $companionDescriptor = $companionDescriptorText | ConvertFrom-Json
}
finally { Pop-Location }
Assert-Equal 'companion descriptor schema' 'speech-settings-companion-descriptor.v1' $companionDescriptor.schemaVersion
Assert-Equal 'companion version' '1.1.0' $companionDescriptor.companionVersion
Assert-Equal 'companion worker version' '1.2.5' $companionDescriptor.workerVersion
$speakerPreview = @($companionDescriptor.actions | Where-Object id -eq 'speaker.preview')
if ($speakerPreview.Count -ne 1 -or $speakerPreview[0].sideEffect -ne 'physical_audio' -or
    $speakerPreview[0].invocation -ne 'explicit_only' -or $speakerPreview[0].implementedByCompanion -ne $true) {
    throw 'speaker.preview is not an implemented explicit-only side-effect action'
}
Write-JsonOnce (Join-Path $descriptorDirectory 'settings-companion-describe.json') $companionDescriptor

$companionFixtureRoot = Join-Path $testRoot 'fixtures\settings-companion'
$inputSchemaSource = Join-Path $companionFixtureRoot 'speech-settings-companion-probe-input.schema.json'
$outputSchemaSource = Join-Path $companionFixtureRoot 'speech-settings-companion-probe-output.schema.json'
$voiceDiscoveryInputSchemaSource = Join-Path $companionFixtureRoot 'speech-settings-voice-discovery-input.schema.json'
$voiceDiscoveryOutputSchemaSource = Join-Path $companionFixtureRoot 'speech-settings-voice-discovery-output.schema.json'
$speakerPreviewInputSchemaSource = Join-Path $companionFixtureRoot 'speech-settings-speaker-preview-input.schema.json'
$speakerPreviewOutputSchemaSource = Join-Path $companionFixtureRoot 'speech-settings-speaker-preview-output.schema.json'
$overrideFixtureSource = Join-Path $companionFixtureRoot 'language-override-binding.json'
foreach ($requiredSource in @($inputSchemaSource, $outputSchemaSource, $voiceDiscoveryInputSchemaSource, $voiceDiscoveryOutputSchemaSource, $speakerPreviewInputSchemaSource, $speakerPreviewOutputSchemaSource, $overrideFixtureSource)) {
    if (-not (Test-Path -LiteralPath $requiredSource)) { throw "Missing companion release source: $requiredSource" }
}
Copy-Item -LiteralPath $inputSchemaSource -Destination (Join-Path $schemaDirectory 'speech-settings-companion-probe-input.schema.json')
Copy-Item -LiteralPath $outputSchemaSource -Destination (Join-Path $schemaDirectory 'speech-settings-companion-probe-output.schema.json')
Copy-Item -LiteralPath $voiceDiscoveryInputSchemaSource -Destination (Join-Path $schemaDirectory 'speech-settings-voice-discovery-input.schema.json')
Copy-Item -LiteralPath $voiceDiscoveryOutputSchemaSource -Destination (Join-Path $schemaDirectory 'speech-settings-voice-discovery-output.schema.json')
Copy-Item -LiteralPath $speakerPreviewInputSchemaSource -Destination (Join-Path $schemaDirectory 'speech-settings-speaker-preview-input.schema.json')
Copy-Item -LiteralPath $speakerPreviewOutputSchemaSource -Destination (Join-Path $schemaDirectory 'speech-settings-speaker-preview-output.schema.json')
Copy-Item -LiteralPath $overrideFixtureSource -Destination (Join-Path $fixtureDirectory 'language-override-binding.json')

$releaseBoundFiles = @(
    'workers/speech/KnowledgeClipper.Speech.Worker.exe'
    'workers/speech/descriptors/worker-describe.json'
    'workers/speech/descriptors/settings-companion-describe.json'
    'workers/speech/schemas/settings-companion/speech-settings-companion-probe-input.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-companion-probe-output.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-speaker-preview-input.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-speaker-preview-output.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-voice-discovery-input.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-voice-discovery-output.schema.json'
    'workers/speech/fixtures/settings-companion/language-override-binding.json'
)
$releaseManifest = [ordered]@{
    schemaVersion='speech-edge-release-manifest.v1'; workerId='speech.edge'; workerVersion='1.2.5'; runtimeIdentifier='win-x64'
    entrypoint='workers/speech/KnowledgeClipper.Speech.Worker.exe'
    bundledFiles=@($releaseBoundFiles | ForEach-Object {
        $full = Join-Path $releaseRoot $_.Replace('/', '\')
        [ordered]@{ path=$_; bytes=(Get-Item -LiteralPath $full).Length; sha256=Get-Sha $full }
    })
    dependencies=[ordered]@{
        managedRuntime='Microsoft .NET Framework 4.x (host-provided)'
        bundledAssemblies=@()
        external=@(
            [ordered]@{ id='edge-tts'; requiredFor='speech.synthesize'; binding='WorkerRequest.configSnapshot.edgeTtsPath'; bundled=$false }
            [ordered]@{ id='ffplay'; requiredFor='speech.playback'; binding='WorkerRequest.configSnapshot.ffplayPath'; bundled=$false }
        )
    }
    companion=[ordered]@{
        version='1.1.0'; descriptor='workers/speech/descriptors/settings-companion-describe.json'
        inputSchema='workers/speech/schemas/settings-companion/speech-settings-companion-probe-input.schema.json'
        outputSchema='workers/speech/schemas/settings-companion/speech-settings-companion-probe-output.schema.json'
        languageOverrideFixture='workers/speech/fixtures/settings-companion/language-override-binding.json'
        voiceDiscoveryInputSchema='workers/speech/schemas/settings-companion/speech-settings-voice-discovery-input.schema.json'
        voiceDiscoveryOutputSchema='workers/speech/schemas/settings-companion/speech-settings-voice-discovery-output.schema.json'
        speakerPreviewInputSchema='workers/speech/schemas/settings-companion/speech-settings-speaker-preview-input.schema.json'
        speakerPreviewOutputSchema='workers/speech/schemas/settings-companion/speech-settings-speaker-preview-output.schema.json'
        voiceDiscovery='read_only_revision_and_sha_bound'
        speakerPreview='explicit_only_implemented'
    }
    authorityMutation=$false
}
Write-JsonOnce (Join-Path $workerDirectory 'release-manifest.json') $releaseManifest

$taskId = 'sf'
$runId = 'r1'
$jobRoot = Join-Path $evidenceRoot "jobs\$taskId"
$spokenText = '这是离线正式发布设置快照验证。'
$spokenPath = Join-Path $jobRoot 'inputs\spoken-text\v0001\payload.txt'
$synthAttempt = Join-Path $jobRoot "runs\$runId\stages\speech-synthesize\attempts\s1"
$playAttempt = Join-Path $jobRoot "runs\$runId\stages\speech-playback\attempts\p1"
$longestOutputProbe = Join-Path $synthAttempt 'outputs\speech-audio\v0001\payload.mp3'
if ($longestOutputProbe.Length -ge 248) { throw "Formal evidence layout exceeds legacy Windows path budget: $($longestOutputProbe.Length)" }
New-Item -ItemType Directory -Path (Split-Path -Parent $spokenPath), $synthAttempt, $playAttempt | Out-Null
[IO.File]::WriteAllText($spokenPath, $spokenText, [Text.UTF8Encoding]::new($false))
$spokenHash = Get-Sha $spokenPath
$synthAttemptRef = Get-Relative $jobRoot $synthAttempt
$playAttemptRef = Get-Relative $jobRoot $playAttempt
$voices = [ordered]@{
    'zh-CN' = 'zh-CN-XiaoxiaoNeural'
    'en-US' = 'en-US-AvaMultilingualNeural'
    'es-ES' = 'es-ES-XimenaNeural'
    'ja-JP' = 'ja-JP-NanamiNeural'
    'ko-KR' = 'ko-KR-SunHiNeural'
}
$catalogVoices = @(
    [ordered]@{ languageTag='zh-CN'; voiceId='zh-CN-XiaoxiaoNeural'; displayName='晓晓'; gender='female'; natural=$true; available=$true; status='available' }
    [ordered]@{ languageTag='en-US'; voiceId='en-US-AvaMultilingualNeural'; displayName='Ava Multilingual'; gender='female'; natural=$true; available=$true; status='available' }
    [ordered]@{ languageTag='es-ES'; voiceId='es-ES-XimenaNeural'; displayName='Ximena'; gender='female'; natural=$true; available=$true; status='available' }
    [ordered]@{ languageTag='ja-JP'; voiceId='ja-JP-NanamiNeural'; displayName='Nanami'; gender='female'; natural=$true; available=$true; status='available' }
    [ordered]@{ languageTag='ko-KR'; voiceId='ko-KR-SunHiNeural'; displayName='SunHi'; gender='female'; natural=$true; available=$true; status='available' }
)
$synthesisTemplate = [ordered]@{
    schemaVersion='1.0.0'; requestId='settings-synth-request'; operationId='settings-synth-operation'
    taskId=$taskId; runId=$runId; stageId='speech-synthesize'; attemptId='s1'; workerId='speech.edge'; operation='speech.synthesize'
    idempotencyKey=$spokenHash; attemptPath=$synthAttemptRef; outputPath="$synthAttemptRef/outputs"
    progressPath="$synthAttemptRef/progress.ndjson"; resultPath="$synthAttemptRef/result.json"
    cancelSignal=[ordered]@{ schemaVersion='1.0.0'; path="$synthAttemptRef/cancel.json"; gracePeriodMs=2000 }
    inputRefs=@(); configSnapshot=[ordered]@{}
    payload=[ordered]@{
        schemaVersion='1.0.0'; requestId='settings-synth-request'; operationId='settings-synth-operation'
        spokenText=[ordered]@{ artifactRef='spoken-text'; path=(Get-Relative $jobRoot $spokenPath); sha256=$spokenHash; bytes=(Get-Item -LiteralPath $spokenPath).Length; encoding='utf-8'; mediaType='text/plain' }
        contextMode='selection_only'; language=[ordered]@{ strategy='auto'; override=$null }
        voiceSnapshot=[ordered]@{ provider='edge-tts'; voicesByLanguage=$voices; ratePercent=0; volumePercent=0; pitchHz=0 }
        playbackPolicy=[ordered]@{ autoPlay=$true; activePlayback='supersede'; replay='from_start'; naturalCompletion='idle_replayable'; stopTerminal='stopped' }
        cachePolicy=[ordered]@{ scope='task'; reuseSuccessfulAudio=$true; crossTaskReuse=$false }
    }
}
$playbackTemplate = [ordered]@{
    schemaVersion='1.0.0'; requestId='settings-play-request'; operationId='settings-play-operation'
    taskId=$taskId; runId=$runId; stageId='speech-playback'; attemptId='p1'; workerId='speech.edge'; operation='speech.playback'
    idempotencyKey=$spokenHash; attemptPath=$playAttemptRef; outputPath="$playAttemptRef/outputs"
    progressPath="$playAttemptRef/progress.ndjson"; resultPath="$playAttemptRef/result.json"
    cancelSignal=[ordered]@{ schemaVersion='1.0.0'; path="$playAttemptRef/cancel.json"; gracePeriodMs=2000 }
    inputRefs=@(); configSnapshot=[ordered]@{}; payload=[ordered]@{}
}
$adapterInput = [ordered]@{
    schemaVersion='speech-settings-adapter-input.v1'
    settings=[ordered]@{ languageStrategy='auto'; languageOverride=$null; voicesByLanguage=$voices; ratePercent=17; volumePercent=-8; pitchHz=23 }
    catalog=[ordered]@{ provider='edge-tts'; catalogVersion='formal-offline-fixture-2'; updatedAtUtc='2026-08-30T00:00:00Z'; voices=$catalogVoices }
    deployment=[ordered]@{ edgeTtsPath=$fakeTool; ffplayPath=$fakeTool }
    synthesisRequest=$synthesisTemplate; playbackRequest=$playbackTemplate
}
$adapterInputPath = Join-Path $evidenceRoot 'settings-adapter-input.json'
Write-JsonOnce $adapterInputPath $adapterInput
Push-Location -LiteralPath $releaseRoot
try {
    $adapterOutputText = (& $publishedWorker settings adapt --input $adapterInputPath --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Formal settings adapt failed' }
}
finally { Pop-Location }
$adapted = $adapterOutputText | ConvertFrom-Json
Assert-Equal 'adapter output schema' 'speech-settings-adapter-snapshot.v1' $adapted.schemaVersion
Assert-Equal 'adapter provider' 'edge-tts' $adapted.fixedSettings.provider
Assert-Equal 'adapter legacy AI TTS' 'disabled' $adapted.fixedSettings.legacyAiTts
Assert-Equal 'adapter rate' 17 $adapted.synthesisRequest.payload.voiceSnapshot.ratePercent
Assert-Equal 'adapter volume' -8 $adapted.synthesisRequest.payload.voiceSnapshot.volumePercent
Assert-Equal 'adapter pitch' 23 $adapted.synthesisRequest.payload.voiceSnapshot.pitchHz
Assert-Equal 'adapter Edge path' $fakeTool $adapted.synthesisRequest.configSnapshot.edgeTtsPath
Assert-Equal 'adapter playback path' $fakeTool $adapted.playbackRequest.configSnapshot.ffplayPath
$adapterOutputPath = Join-Path $evidenceRoot 'settings-adapter-snapshot.json'
Write-JsonOnce $adapterOutputPath $adapted

$companionAdapterInput = ($adapterInput | ConvertTo-Json -Depth 40 | ConvertFrom-Json)
$companionAdapterInput.settings.languageStrategy = 'override'
$companionAdapterInput.settings.languageOverride = 'en-US'
$companionInput = [ordered]@{
    schemaVersion='speech-settings-companion-probe-input.v1'; scope='project'; effectiveAt='next_task'; action='validate_only'
    probeText='¿Este texto debe usar la voz inglesa configurada?'; adapterInput=$companionAdapterInput
}
$companionInputPath = Join-Path $evidenceRoot 'settings-companion-probe-input.json'
Write-JsonOnce $companionInputPath $companionInput
Push-Location -LiteralPath $releaseRoot
try {
    $companionOutputText = (& $publishedWorker settings companion probe --input $companionInputPath --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Formal settings companion probe failed' }
}
finally { Pop-Location }
$companionOutput = $companionOutputText | ConvertFrom-Json
Assert-Equal 'companion output schema' 'speech-settings-companion-probe-output.v1' $companionOutput.schemaVersion
Assert-Equal 'companion probe status' 'ready' $companionOutput.status
Assert-Equal 'companion scope' 'project' $companionOutput.scope
Assert-Equal 'companion effectiveAt' 'next_task' $companionOutput.effectiveAt
Assert-Equal 'companion override' 'en-US' $companionOutput.routing.override
Assert-Equal 'companion language route' 'en-US' $companionOutput.routing.languageTag
Assert-Equal 'companion voice route' 'en-US-AvaMultilingualNeural' $companionOutput.routing.voiceId
Assert-Equal 'companion speaker state' 'not_run' $companionOutput.speakerPreview.status
if ($companionOutput.sideEffectsPerformed.Count -ne 0) { throw 'Companion probe performed an unexpected side effect' }
$companionOutputPath = Join-Path $evidenceRoot 'settings-companion-probe-output.json'
Write-JsonOnce $companionOutputPath $companionOutput

$voiceDiscoveryInput = [ordered]@{
    schemaVersion='speech-settings-voice-discovery-input.v1'; mode='validate_only'
    requestedLanguages=@('zh-CN','en-US','es-ES','ja-JP','ko-KR'); cacheMaxAgeSeconds=604800
    deployment=[ordered]@{ edgeTtsPath=$fakeTool; ffplayPath=$fakeTool }
    catalogSnapshot=$adapterInput.catalog
}
$voiceDiscoveryInputPath = Join-Path $evidenceRoot 'voice-discovery-validate-input.json'
Write-JsonOnce $voiceDiscoveryInputPath $voiceDiscoveryInput
Push-Location -LiteralPath $releaseRoot
try {
    $voiceDiscoveryOutputText = (& $publishedWorker settings companion voices --input $voiceDiscoveryInputPath --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Formal validate-only voice discovery failed' }
}
finally { Pop-Location }
$voiceDiscoveryOutput = $voiceDiscoveryOutputText | ConvertFrom-Json
Assert-Equal 'voice discovery output schema' 'speech-settings-voice-discovery-output.v1' $voiceDiscoveryOutput.schemaVersion
Assert-Equal 'voice discovery source' 'supplied_snapshot' $voiceDiscoveryOutput.source
Assert-Equal 'voice discovery status' 'ready' $voiceDiscoveryOutput.status
Assert-Equal 'voice discovery settings mutation' 0 $voiceDiscoveryOutput.settingsMutationCount
Assert-Equal 'voice discovery speaker' $false $voiceDiscoveryOutput.physicalSpeakerPlayback
if ($voiceDiscoveryOutput.sideEffectsPerformed.Count -ne 0 -or $voiceDiscoveryOutput.catalogSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'Validate-only voice discovery is not read-only/hash-bound'
}
$voiceDiscoveryOutputPath = Join-Path $evidenceRoot 'voice-discovery-validate-output.json'
Write-JsonOnce $voiceDiscoveryOutputPath $voiceDiscoveryOutput

$fakeInvocationPath = Join-Path $evidenceRoot 'fake-tool-invocations.log'
$previousInvocationPath = $env:KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH
$previousPlaybackDelay = $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS
$env:KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH = $fakeInvocationPath
$env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = '25'
try {
    $synthRequestPath = Join-Path $synthAttempt 'request.json'
    Write-JsonOnce $synthRequestPath $adapted.synthesisRequest
    Push-Location -LiteralPath $releaseRoot
    try {
        $synthCli = (& $publishedWorker run --request $synthRequestPath | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Formal synthesis WorkerRequest failed' }
    }
    finally { Pop-Location }
    $synthResultPath = Join-Path $synthAttempt 'result.json'
    $synthResult = Get-Content -Raw -LiteralPath $synthResultPath | ConvertFrom-Json
    Assert-Equal 'synthesis status' 'completed' $synthResult.status
    Assert-Equal 'synthesis language' 'zh-CN' $synthResult.payload.languageTag
    Assert-Equal 'synthesis voice' 'zh-CN-XiaoxiaoNeural' $synthResult.payload.voiceId
    if ($synthResult.outputs.Count -ne 1) { throw 'Synthesis did not publish exactly one audio artifact' }

    $adapted.playbackRequest.inputRefs = @($synthResult.outputs[0])
    $adapted.playbackRequest.idempotencyKey = Get-TextSha ($synthResult.outputs[0].expectedSha256 + '|p1')
    $playRequestPath = Join-Path $playAttempt 'request.json'
    Write-JsonOnce $playRequestPath $adapted.playbackRequest
    Push-Location -LiteralPath $releaseRoot
    try {
        $playCli = (& $publishedWorker run --request $playRequestPath | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Formal playback WorkerRequest failed' }
    }
    finally { Pop-Location }
    $playResultPath = Join-Path $playAttempt 'result.json'
    $playResult = Get-Content -Raw -LiteralPath $playResultPath | ConvertFrom-Json
    Assert-Equal 'playback status' 'completed' $playResult.status
    Assert-Equal 'natural completion' 'idle_replayable' $playResult.payload.playbackState
    Assert-Equal 'playback cache' $false $playResult.payload.cacheHit
}
finally {
    $env:KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH = $previousInvocationPath
    $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = $previousPlaybackDelay
}
if (-not (Test-Path -LiteralPath $fakeInvocationPath)) { throw 'Fake tool invocation evidence is missing' }
$invocations = @(Get-Content -LiteralPath $fakeInvocationPath)
if ($invocations.Count -ne 2 -or $invocations[0] -notmatch '--write-media' -or $invocations[1] -notmatch '-nodisp') {
    throw 'Expected one fake synthesis and one fake playback invocation'
}

$previewInput = [ordered]@{
    schemaVersion='speech-settings-speaker-preview-input.v1'; scope='project'; effectiveAt='preview_only'; action='speaker.preview'
    explicitSideEffectAuthorization=$true; previewLanguage='en-US'; adapterInput=$companionAdapterInput
}
$previewInputPath = Join-Path $evidenceRoot 'speaker-preview-input.json'
$previewOutputPath = Join-Path $evidenceRoot 'speaker-preview-output.json'
$previewInvocationPath = Join-Path $evidenceRoot 'speaker-preview-fake-invocations.log'
Write-JsonOnce $previewInputPath $previewInput
$previousInvocationPath = $env:KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH
$previousPlaybackDelay = $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS
$env:KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH = $previewInvocationPath
$env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = '25'
try {
    Push-Location -LiteralPath $releaseRoot
    try {
        $previewOutputText = (& $publishedWorker settings companion preview --input $previewInputPath --json | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Formal explicit fake speaker preview failed' }
    }
    finally { Pop-Location }
}
finally {
    $env:KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH = $previousInvocationPath
    $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = $previousPlaybackDelay
}
$previewOutput = $previewOutputText | ConvertFrom-Json
Assert-Equal 'preview status' 'completed' $previewOutput.status
Assert-Equal 'preview natural idle' 'idle' $previewOutput.playbackState
Assert-Equal 'preview attempt count' 1 $previewOutput.previewAttemptCount
Assert-Equal 'preview player count' 1 $previewOutput.audioPlayerInvocationCount
Assert-Equal 'preview manual stop' $false $previewOutput.manualStopRequired
if (@($previewOutput.taskCreation.psobject.Properties | Where-Object { [int]$_.Value -ne 0 }).Count -ne 0) { throw 'speaker.preview created a knowledge task' }
Write-JsonOnce $previewOutputPath $previewOutput
$previewInvocations = @(Get-Content -LiteralPath $previewInvocationPath)
if ($previewInvocations.Count -ne 2 -or @($previewInvocations | Where-Object { $_ -match '--write-media' }).Count -ne 1 -or @($previewInvocations | Where-Object { $_ -match '-nodisp' }).Count -ne 1) {
    throw 'speaker.preview did not make exactly one fake synth and one fake player invocation'
}

$releaseTree = Get-ReleaseTree $releaseRoot
$expectedReleasePaths = @(
    'workers/speech/KnowledgeClipper.Speech.Worker.exe'
    'workers/speech/descriptors/settings-companion-describe.json'
    'workers/speech/descriptors/worker-describe.json'
    'workers/speech/fixtures/settings-companion/language-override-binding.json'
    'workers/speech/release-manifest.json'
    'workers/speech/schemas/settings-companion/speech-settings-companion-probe-input.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-companion-probe-output.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-speaker-preview-input.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-speaker-preview-output.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-voice-discovery-input.schema.json'
    'workers/speech/schemas/settings-companion/speech-settings-voice-discovery-output.schema.json'
)
$actualReleasePaths = @($releaseTree.files.path | Sort-Object)
if ($releaseTree.fileCount -ne $expectedReleasePaths.Count -or (Compare-Object ($expectedReleasePaths | Sort-Object) $actualReleasePaths)) {
    throw 'Formal win-x64 release-root is not the expected minimal companion-bound tree'
}
$unitRoot = (Get-ChildItem -LiteralPath (Join-Path $testRoot 'bin\out') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$unitReceiptPath = Join-Path $unitRoot 'unit-test-receipt.json'
$publicReceiptPath = Join-Path $unitRoot 'public-contract-validation-receipt.json'

& (Join-Path $testRoot 'validate-destination-neutral-identity.ps1') -WorkerExe $publishedWorker -LegacyWorkerAlias (Join-Path $testRoot 'bin\RemNoteQuickClipper.Speech.Worker.exe')
if ($LASTEXITCODE -ne 0) { throw 'Formal release-root destination-neutral validation failed' }
$identityRoot = (Get-ChildItem -LiteralPath (Join-Path $testRoot 'bin\identity') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$identityReceiptPath = Join-Path $identityRoot 'destination-neutral-identity-receipt.json'

$canonicalRuntime = 'D:\Applications\KnowledgeClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
$legacyCompatibilityRuntime = 'D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
$liveEdgeTts = if (Test-Path -LiteralPath $canonicalRuntime) { $canonicalRuntime } else { $legacyCompatibilityRuntime }
if (-not (Test-Path -LiteralPath $liveEdgeTts)) { throw "Missing Edge TTS for formal live Canary: $liveEdgeTts" }
$liveVoiceDiscoveryInput = [ordered]@{
    schemaVersion='speech-settings-voice-discovery-input.v1'; mode='discover'
    requestedLanguages=@('zh-CN','en-US','es-ES','ja-JP','ko-KR'); cacheMaxAgeSeconds=86400
    deployment=[ordered]@{ edgeTtsPath=$liveEdgeTts; ffplayPath=$fakeTool }; catalogSnapshot=$null
}
$liveVoiceDiscoveryInputPath = Join-Path $evidenceRoot 'voice-discovery-live-input.json'
Write-JsonOnce $liveVoiceDiscoveryInputPath $liveVoiceDiscoveryInput
Push-Location -LiteralPath $releaseRoot
try {
    $liveVoiceDiscoveryOutputText = (& $publishedWorker settings companion voices --input $liveVoiceDiscoveryInputPath --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Formal live read-only voice discovery failed' }
}
finally { Pop-Location }
$liveVoiceDiscoveryOutput = $liveVoiceDiscoveryOutputText | ConvertFrom-Json
Assert-Equal 'live voice discovery source' 'live' $liveVoiceDiscoveryOutput.source
Assert-Equal 'live voice discovery status' 'ready' $liveVoiceDiscoveryOutput.status
Assert-Equal 'live voice discovery speaker' $false $liveVoiceDiscoveryOutput.physicalSpeakerPlayback
Assert-Equal 'live voice discovery settings mutation' 0 $liveVoiceDiscoveryOutput.settingsMutationCount
Assert-Equal 'live voice discovery cache writes' 0 $liveVoiceDiscoveryOutput.deployment.cacheWriteCount
if ($liveVoiceDiscoveryOutput.sideEffectsPerformed.Count -ne 0 -or $liveVoiceDiscoveryOutput.routes.Count -ne 5) { throw 'Live voice discovery violated read-only/five-language boundary' }
$liveVoiceDiscoveryOutputPath = Join-Path $evidenceRoot 'voice-discovery-live-output.json'
Write-JsonOnce $liveVoiceDiscoveryOutputPath $liveVoiceDiscoveryOutput
& (Join-Path $testRoot 'run-live-canary.ps1') -WorkerExe $publishedWorker -EdgeTtsPath $liveEdgeTts -LanguageStrategy override -RatePercent 17 -VolumePercent -8 -PitchHz 23 -RequireDeepOutput
if ($LASTEXITCODE -ne 0) { throw 'Formal release-root live deep-path Edge Canary failed' }
$liveCanaryReceiptPath = (Get-ChildItem -LiteralPath (Join-Path $testRoot 'canary') -Recurse -Filter 'canary-receipt.json' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$liveCanaryRoot = Split-Path -Parent $liveCanaryReceiptPath
$liveCanary = Get-Content -Raw -LiteralPath $liveCanaryReceiptPath | ConvertFrom-Json
Assert-Equal 'live Canary worker hash' $workerHash $liveCanary.workerExeSha256
Assert-Equal 'live Canary strategy' 'override' $liveCanary.languageStrategy
Assert-Equal 'live Canary deep path' $true $liveCanary.deepPathPassed
Assert-Equal 'live Canary speaker boundary' $false $liveCanary.physicalSpeakerPlayback
Assert-Equal 'live Canary old AI route count' 0 $liveCanary.oldAiTtsRouteCount
if ($liveCanary.results.Count -ne 5 -or @($liveCanary.results | Where-Object { $_.status -ne 'completed' -or -not $_.deepPathVerified }).Count -ne 0) {
    throw 'Formal live Canary lacks five completed >260 audio artifacts'
}

$releaseContractRoot = Join-Path $evidenceRoot 'release-public-contracts'
$releaseContractSentinel = Join-Path $releaseContractRoot 'sentinel'
New-Item -ItemType Directory -Path $releaseContractSentinel | Out-Null
$firstLiveResult = Get-ChildItem -LiteralPath $liveCanaryRoot -Recurse -Filter 'result.json' -File | Select-Object -First 1
Copy-Item -LiteralPath $firstLiveResult.FullName -Destination (Join-Path $releaseContractSentinel 'result.json')
python (Join-Path $testRoot 'validate-public-contracts.py') --project-root $projectRoot --canary-root $liveCanaryRoot --unit-root $releaseContractSentinel --worker-exe $publishedWorker
if ($LASTEXITCODE -ne 0) { throw 'Formal release-root public contract validation failed' }
$releasePublicReceiptPath = Join-Path $releaseContractSentinel 'public-contract-validation-receipt.json'

foreach ($required in @($unitReceiptPath, $publicReceiptPath, $identityReceiptPath, $releasePublicReceiptPath, $liveCanaryReceiptPath, $adapterInputPath, $adapterOutputPath, $companionInputPath, $companionOutputPath, $voiceDiscoveryInputPath, $voiceDiscoveryOutputPath, $liveVoiceDiscoveryInputPath, $liveVoiceDiscoveryOutputPath, $previewInputPath, $previewOutputPath, $previewInvocationPath, $synthRequestPath, $synthResultPath, $playRequestPath, $playResultPath, $fakeInvocationPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing formal evidence: $required" }
}
$unitReceipt = Get-Content -Raw -LiteralPath $unitReceiptPath | ConvertFrom-Json
if ($unitReceipt.status -ne 'passed' -or $unitReceipt.failures -ne 0 -or $unitReceipt.tests.Count -lt 53) { throw 'Current unit receipt is insufficient' }
$longPlaybackTest = @($unitReceipt.tests | Where-Object name -eq 'playback stages audio beyond MAX_PATH into a short player workspace')
if ($longPlaybackTest.Count -ne 1 -or $longPlaybackTest[0].status -ne 'passed') { throw 'Current unit receipt lacks deep-path playback evidence' }
$receiptPath = Join-Path $formalParent 'speech-formal-win-x64-release-receipt.json'
$releaseManifestPath = Join-Path $workerDirectory 'release-manifest.json'
$packagedCompanionDescriptorPath = Join-Path $descriptorDirectory 'settings-companion-describe.json'
$packagedInputSchemaPath = Join-Path $schemaDirectory 'speech-settings-companion-probe-input.schema.json'
$packagedOutputSchemaPath = Join-Path $schemaDirectory 'speech-settings-companion-probe-output.schema.json'
$packagedVoiceDiscoveryInputSchemaPath = Join-Path $schemaDirectory 'speech-settings-voice-discovery-input.schema.json'
$packagedVoiceDiscoveryOutputSchemaPath = Join-Path $schemaDirectory 'speech-settings-voice-discovery-output.schema.json'
$packagedSpeakerPreviewInputSchemaPath = Join-Path $schemaDirectory 'speech-settings-speaker-preview-input.schema.json'
$packagedSpeakerPreviewOutputSchemaPath = Join-Path $schemaDirectory 'speech-settings-speaker-preview-output.schema.json'
$packagedOverrideFixturePath = Join-Path $fixtureDirectory 'language-override-binding.json'
$receipt = [ordered]@{
    schemaVersion=1; receiptType='speech-formal-win-x64-release-v1'; status='passed'
    completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); admissionState='formal_module_candidate_for_core_admission'; authorityMutated=$false; runtimeIdentifier='win-x64'
    workerId='speech.edge'; workerVersion=$descriptor.workerVersion; contractVersion=$descriptor.contractVersion
    formalReleaseRoot=$releaseRoot; entrypoint=$publishedWorker; descriptor=$releaseDescriptor
    releaseTree=$releaseTree; exe=[ordered]@{ bytes=(Get-Item -LiteralPath $publishedWorker).Length; sha256=Get-Sha $publishedWorker }
    releaseManifest=[ordered]@{ path=$releaseManifestPath; sha256=Get-Sha $releaseManifestPath; schemaVersion='speech-edge-release-manifest.v1' }
    settingsAdapter=[ordered]@{
        inputSchemaVersion='speech-settings-adapter-input.v1'; outputSchemaVersion='speech-settings-adapter-snapshot.v1'
        languageStrategy='auto'; languageOverride=$null; selectedLanguage='zh-CN'; selectedVoice='zh-CN-XiaoxiaoNeural'
        ratePercent=17; volumePercent=-8; pitchHz=23; provider='edge-tts'; legacyAiTts='disabled'
        edgeTtsPath=$fakeTool; ffplayPath=$fakeTool
    }
    settingsCompanion=[ordered]@{
        schemaVersion='speech-settings-companion-descriptor.v1'; companionVersion='1.1.0'
        scope='project'; effectiveAt='next_task'; action='validate_only'
        languageStrategy='override'; languageOverride='en-US'; selectedLanguage='en-US'; selectedVoice='en-US-AvaMultilingualNeural'
        inputSchema=$packagedInputSchemaPath; outputSchema=$packagedOutputSchemaPath
        voiceDiscoveryInputSchema=$packagedVoiceDiscoveryInputSchemaPath; voiceDiscoveryOutputSchema=$packagedVoiceDiscoveryOutputSchemaPath
        speakerPreviewInputSchema=$packagedSpeakerPreviewInputSchemaPath; speakerPreviewOutputSchema=$packagedSpeakerPreviewOutputSchemaPath
        languageOverrideFixture=$packagedOverrideFixturePath
        voiceDiscovery=[ordered]@{
            readOnly=$true; persistenceOwner='desktop-core'; speakerPlayback=$false
            offlineCatalogRevision=$voiceDiscoveryOutput.catalogRevision; offlineCatalogSha256=$voiceDiscoveryOutput.catalogSha256
            liveCatalogRevision=$liveVoiceDiscoveryOutput.catalogRevision; liveCatalogSha256=$liveVoiceDiscoveryOutput.catalogSha256
            liveUpdatedAtUtc=$liveVoiceDiscoveryOutput.updatedAtUtc; liveVoiceCount=$liveVoiceDiscoveryOutput.voices.Count
            liveProviderQueryCount=$liveVoiceDiscoveryOutput.deployment.providerQueryCount
            cacheWriteCount=$liveVoiceDiscoveryOutput.deployment.cacheWriteCount; settingsMutationCount=$liveVoiceDiscoveryOutput.settingsMutationCount
        }
        speakerPreview=[ordered]@{
            invocation='explicit_only'; sideEffect='physical_audio'; implemented=$true; testMode='fake_no_speaker'
            status=$previewOutput.status; playbackState=$previewOutput.playbackState; naturalCompletion=$previewOutput.naturalCompletion
            previewAttemptCount=$previewOutput.previewAttemptCount; audioPlayerInvocationCount=$previewOutput.audioPlayerInvocationCount
            physicalSpeakerPlayback=$false; knowledgeTaskCreation=0
        }
        sideEffectsPerformed=@()
    }
    offlineWorkerRequestCanary=[ordered]@{
        synthStatus=$synthResult.status; synthLanguage=$synthResult.payload.languageTag; synthVoice=$synthResult.payload.voiceId
        playbackStatus=$playResult.status; playbackState=$playResult.payload.playbackState; playbackCacheHit=$playResult.payload.cacheHit
        fakeInvocationCount=$invocations.Count; physicalSpeakerPlayback=$false; networkTtsCalls=0; externalProductionWrites=0
    }
    liveDeepPathEdgeCanary=[ordered]@{
        receipt=$liveCanaryReceiptPath; receiptSha256=Get-Sha $liveCanaryReceiptPath
        languageStrategy=$liveCanary.languageStrategy; voiceSettings=$liveCanary.voiceSettings
        languages=@($liveCanary.results | ForEach-Object { $_.language })
        completedAudio=@($liveCanary.results | ForEach-Object {
            [ordered]@{ language=$_.language; voice=$_.voice; bytes=$_.audioBytes; sha256=$_.audioSha256; fullPathLength=$_.audioFullPathLength; deepPathVerified=$_.deepPathVerified }
        })
        networkTtsCalls=5; deepPathRequired=$true; deepPathPassed=$liveCanary.deepPathPassed
        physicalSpeakerPlayback=$liveCanary.physicalSpeakerPlayback; oldAiTtsRouteCount=$liveCanary.oldAiTtsRouteCount; externalProductionWrites=0
    }
    evidence=@(
        [ordered]@{ kind='unit-tests'; path=$unitReceiptPath; sha256=Get-Sha $unitReceiptPath }
        [ordered]@{ kind='public-contracts'; path=$publicReceiptPath; sha256=Get-Sha $publicReceiptPath }
        [ordered]@{ kind='release-public-contracts'; path=$releasePublicReceiptPath; sha256=Get-Sha $releasePublicReceiptPath }
        [ordered]@{ kind='destination-neutral-identity'; path=$identityReceiptPath; sha256=Get-Sha $identityReceiptPath }
        [ordered]@{ kind='describe'; path=(Join-Path $evidenceRoot 'describe.json'); sha256=Get-Sha (Join-Path $evidenceRoot 'describe.json') }
        [ordered]@{ kind='release-manifest'; path=$releaseManifestPath; sha256=Get-Sha $releaseManifestPath }
        [ordered]@{ kind='companion-descriptor'; path=$packagedCompanionDescriptorPath; sha256=Get-Sha $packagedCompanionDescriptorPath }
        [ordered]@{ kind='companion-input-schema'; path=$packagedInputSchemaPath; sha256=Get-Sha $packagedInputSchemaPath }
        [ordered]@{ kind='companion-output-schema'; path=$packagedOutputSchemaPath; sha256=Get-Sha $packagedOutputSchemaPath }
        [ordered]@{ kind='voice-discovery-input-schema'; path=$packagedVoiceDiscoveryInputSchemaPath; sha256=Get-Sha $packagedVoiceDiscoveryInputSchemaPath }
        [ordered]@{ kind='voice-discovery-output-schema'; path=$packagedVoiceDiscoveryOutputSchemaPath; sha256=Get-Sha $packagedVoiceDiscoveryOutputSchemaPath }
        [ordered]@{ kind='speaker-preview-input-schema'; path=$packagedSpeakerPreviewInputSchemaPath; sha256=Get-Sha $packagedSpeakerPreviewInputSchemaPath }
        [ordered]@{ kind='speaker-preview-output-schema'; path=$packagedSpeakerPreviewOutputSchemaPath; sha256=Get-Sha $packagedSpeakerPreviewOutputSchemaPath }
        [ordered]@{ kind='language-override-fixture'; path=$packagedOverrideFixturePath; sha256=Get-Sha $packagedOverrideFixturePath }
        [ordered]@{ kind='settings-input'; path=$adapterInputPath; sha256=Get-Sha $adapterInputPath }
        [ordered]@{ kind='settings-snapshot'; path=$adapterOutputPath; sha256=Get-Sha $adapterOutputPath }
        [ordered]@{ kind='companion-probe-input'; path=$companionInputPath; sha256=Get-Sha $companionInputPath }
        [ordered]@{ kind='companion-probe-output'; path=$companionOutputPath; sha256=Get-Sha $companionOutputPath }
        [ordered]@{ kind='voice-discovery-validate-input'; path=$voiceDiscoveryInputPath; sha256=Get-Sha $voiceDiscoveryInputPath }
        [ordered]@{ kind='voice-discovery-validate-output'; path=$voiceDiscoveryOutputPath; sha256=Get-Sha $voiceDiscoveryOutputPath }
        [ordered]@{ kind='voice-discovery-live-input'; path=$liveVoiceDiscoveryInputPath; sha256=Get-Sha $liveVoiceDiscoveryInputPath }
        [ordered]@{ kind='voice-discovery-live-output'; path=$liveVoiceDiscoveryOutputPath; sha256=Get-Sha $liveVoiceDiscoveryOutputPath }
        [ordered]@{ kind='speaker-preview-input'; path=$previewInputPath; sha256=Get-Sha $previewInputPath }
        [ordered]@{ kind='speaker-preview-output'; path=$previewOutputPath; sha256=Get-Sha $previewOutputPath }
        [ordered]@{ kind='speaker-preview-fake-invocations'; path=$previewInvocationPath; sha256=Get-Sha $previewInvocationPath }
        [ordered]@{ kind='synthesis-request'; path=$synthRequestPath; sha256=Get-Sha $synthRequestPath }
        [ordered]@{ kind='synthesis-result'; path=$synthResultPath; sha256=Get-Sha $synthResultPath }
        [ordered]@{ kind='playback-request'; path=$playRequestPath; sha256=Get-Sha $playRequestPath }
        [ordered]@{ kind='playback-result'; path=$playResultPath; sha256=Get-Sha $playResultPath }
        [ordered]@{ kind='fake-tool-invocations'; path=$fakeInvocationPath; sha256=Get-Sha $fakeInvocationPath }
        [ordered]@{ kind='live-deep-edge-canary'; path=$liveCanaryReceiptPath; sha256=Get-Sha $liveCanaryReceiptPath }
    )
}
Write-JsonOnce $receiptPath $receipt
python (Join-Path $testRoot 'validate-formal-win-x64-release.py') --receipt $receiptPath
if ($LASTEXITCODE -ne 0) { throw 'Independent formal win-x64 validation failed' }
Write-Output "PASS SPEECH FORMAL WIN-X64 RELEASE $releaseRoot"
Write-Output "TREE SHA256 $($releaseTree.treeSha256)"
Write-Output "EXE SHA256 $workerHash"
Write-Output "DESCRIBE $releaseDescriptorText"
Write-Output "RECEIPT $receiptPath"

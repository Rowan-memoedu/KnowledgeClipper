param(
    [string]$WorkerExe,
    [string]$EdgeTtsPath,
    [string]$FakePlayerExe
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($WorkerExe)) {
    $WorkerExe = (Get-ChildItem -LiteralPath (Join-Path $testRoot 'publish\speech.edge\v1.2.0') -Filter 'KnowledgeClipper.Speech.Worker.exe' -Recurse -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if ([string]::IsNullOrWhiteSpace($EdgeTtsPath)) {
    $canonicalRuntime = 'D:\Applications\KnowledgeClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $legacyCompatibilityRuntime = 'D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $EdgeTtsPath = if (Test-Path -LiteralPath $canonicalRuntime) { $canonicalRuntime } else { $legacyCompatibilityRuntime }
}
if ([string]::IsNullOrWhiteSpace($FakePlayerExe)) { $FakePlayerExe = Join-Path $testRoot 'bin\KnowledgeClipper.Speech.FakeTool.exe' }
$worker = (Resolve-Path -LiteralPath $WorkerExe).Path
$edgeTts = (Resolve-Path -LiteralPath $EdgeTtsPath).Path
$fakePlayer = (Resolve-Path -LiteralPath $FakePlayerExe).Path

function Get-RelativePath([string]$root, [string]$path) {
    return [Uri]::UnescapeDataString(([Uri]($root.TrimEnd('\') + '\')).MakeRelativeUri([Uri]$path).ToString())
}

function Get-StringSha256([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Write-JsonOnce([string]$path, $value) {
    if (Test-Path -LiteralPath $path) { throw "Immutable file exists: $path" }
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    $temporary = Join-Path $directory ('.tmp-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Depth 50 -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $path)
}

function New-HashedFile([string]$jobRoot, [string]$path, [string]$mediaType) {
    return [ordered]@{
        path = Get-RelativePath $jobRoot $path
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $path).Length
        mediaType = $mediaType
    }
}

function Assert-ContentLiveSource($source) {
    $bundle = Get-Content -Raw -LiteralPath $source.BundlePath | ConvertFrom-Json
    $trace = @($bundle.ai_trace | Where-Object stage -eq 'cleaning')
    if ($trace.Count -ne 1 -or $trace[0].mode -ne 'live' -or $trace[0].provider -ne 'deepseek') {
        throw "$($source.Language) Content source is not a DeepSeek live cleaning artifact"
    }
    if ($bundle.items.Count -ne 1 -or $bundle.items[0].cleaning.state -ne 'ready') {
        throw "$($source.Language) Content source is not ready"
    }
    $source.Bundle = $bundle
    $source.CleanedText = [string]$bundle.items[0].cleaning.cleaned_text
    $source.ProviderEvidence = $trace[0]
}

$sourceDefinitions = @(
    [pscustomobject]@{
        Language = 'zh-CN'; Key = 'zh'; OriginalPath = Join-Path $projectRoot 'content-worker\canary\jobs\20260825T233924768Z-offline-structured-cleaning-live-9d9f3b51\inputs\content\materials\source.md'
        BundlePath = Join-Path $projectRoot 'content-worker\canary\jobs\20260825T233924768Z-offline-structured-cleaning-live-9d9f3b51\runs\run-22f28408f6d40c19b027\stages\content.understand-and-card\attempts\attempt-22f28408f6d40c19b027\outputs\learning-bundle.schema-v1.json'
        CanaryReceiptPath = Join-Path $projectRoot 'content-worker\canary\jobs\20260825T233924768Z-offline-structured-cleaning-live-9d9f3b51\canary-receipt.json'
        Bundle = $null; CleanedText = $null; ProviderEvidence = $null
    }
    [pscustomobject]@{
        Language = 'en-US'; Key = 'en'; OriginalPath = Join-Path $projectRoot 'content-worker\canary\jobs\20260826T014742460Z-offline-language-en-live-a0479adf\inputs\content\materials\clip.md'
        BundlePath = Join-Path $projectRoot 'content-worker\canary\jobs\20260826T014742460Z-offline-language-en-live-a0479adf\runs\run-0b411a2036b8e917ea5f\stages\content.understand-and-card\attempts\attempt-0b411a2036b8e917ea5f\outputs\learning-bundle.schema-v1.json'
        CanaryReceiptPath = Join-Path $projectRoot 'content-worker\canary\jobs\20260826T014742460Z-offline-language-en-live-a0479adf\canary-receipt.json'
        Bundle = $null; CleanedText = $null; ProviderEvidence = $null
    }
)
foreach ($source in $sourceDefinitions) {
    foreach ($required in @($source.OriginalPath, $source.BundlePath, $source.CanaryReceiptPath)) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Missing Content evidence: $required" }
    }
    Assert-ContentLiveSource $source
}

$runRoot = Join-Path $testRoot ('bc\r' + (Get-Date).ToUniversalTime().ToString('HHmmss') + [guid]::NewGuid().ToString('N').Substring(0, 4))
$taskId = 'sbc'
$runId = 'r1'
$jobRoot = Join-Path $runRoot "jobs\$taskId"
New-Item -ItemType Directory -Path $jobRoot | Out-Null
$createdAt = [DateTimeOffset]::UtcNow.ToString('o')
$sourceEvidence = @()
$spokenArtifacts = @{}

foreach ($source in $sourceDefinitions) {
    $contentDirectory = Join-Path $jobRoot "inputs\c$($source.Key)\v1"
    $spokenDirectory = Join-Path $jobRoot "inputs\s$($source.Key)\v1"
    New-Item -ItemType Directory -Path $contentDirectory, $spokenDirectory | Out-Null
    $originalCopy = Join-Path $contentDirectory 'original.md'
    $bundleCopy = Join-Path $contentDirectory 'learning-bundle.json'
    $contentReceiptCopy = Join-Path $contentDirectory 'content-canary-receipt.json'
    Copy-Item -LiteralPath $source.OriginalPath -Destination $originalCopy
    Copy-Item -LiteralPath $source.BundlePath -Destination $bundleCopy
    Copy-Item -LiteralPath $source.CanaryReceiptPath -Destination $contentReceiptCopy
    $contentManifestPath = Join-Path $contentDirectory 'artifact.json'
    $contentManifest = [ordered]@{
        schemaVersion = '1.0.0'; artifactId = "content-live-$($source.Key)"; kind = 'learning-bundle'; contractVersion = '1.0.0'; mediaType = 'application/json'
        files = @(
            New-HashedFile $jobRoot $originalCopy 'text/markdown'
            New-HashedFile $jobRoot $bundleCopy 'application/json'
            New-HashedFile $jobRoot $contentReceiptCopy 'application/json'
        )
        createdAt = $source.Bundle.created_at
        producer = [ordered]@{ stageId = 'content.understand-and-card'; workerId = 'content.deepseek'; workerVersion = $source.Bundle.worker.version; attemptId = $source.Bundle.attempt_id }
        inputRefs = @(); dataClass = 'public'
        metadata = [ordered]@{
            provider = 'deepseek'; providerMode = 'live'; model = $source.ProviderEvidence.model
            providerResponseSha256 = $source.ProviderEvidence.response_sha256
            sourceCanaryId = (Get-Content -Raw -LiteralPath $source.CanaryReceiptPath | ConvertFrom-Json).canaryId
        }
    }
    Write-JsonOnce $contentManifestPath $contentManifest
    $contentManifestHash = (Get-FileHash -LiteralPath $contentManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $spokenPath = Join-Path $spokenDirectory 'payload.txt'
    [IO.File]::WriteAllText($spokenPath, $source.CleanedText, [Text.UTF8Encoding]::new($false))
    $spokenManifestPath = Join-Path $spokenDirectory 'artifact.json'
    $spokenManifest = [ordered]@{
        schemaVersion = '1.0.0'; artifactId = "spoken-$($source.Key)"; kind = 'text.spoken'; contractVersion = '1.0.0'; mediaType = 'text/plain'
        files = @(New-HashedFile $jobRoot $spokenPath 'text/plain')
        createdAt = $createdAt
        producer = [ordered]@{ stageId = 'spoken-text-adapter'; workerId = 'speech.canary.fixture-adapter'; workerVersion = '1.0.0'; attemptId = "adapt-$($source.Key)" }
        inputRefs = @([ordered]@{ artifactRef = "content-live-$($source.Key)"; kind = 'learning-bundle'; contractVersion = '1.0.0'; manifestPath = Get-RelativePath $jobRoot $contentManifestPath; expectedSha256 = $contentManifestHash })
        dataClass = 'public'
        metadata = [ordered]@{ language = $source.Language; sourceField = 'items[0].cleaning.cleaned_text'; assembly = 'content-live-cleaned-text-adapted-without-recleaning' }
    }
    Write-JsonOnce $spokenManifestPath $spokenManifest
    $spokenArtifacts[$source.Key] = [ordered]@{
        ArtifactId = "spoken-$($source.Key)"; PayloadPath = $spokenPath; ManifestPath = $spokenManifestPath
        ManifestSha256 = (Get-FileHash -LiteralPath $spokenManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        File = $spokenManifest.files[0]
    }
    $sourceEvidence += [ordered]@{
        language = $source.Language; provider = 'deepseek'; providerMode = 'live'; model = $source.ProviderEvidence.model
        original = New-HashedFile $jobRoot $originalCopy 'text/markdown'
        cleanedSpokenText = $spokenManifest.files[0]
        contentLearningBundle = New-HashedFile $jobRoot $bundleCopy 'application/json'
        contentCanaryReceipt = New-HashedFile $jobRoot $contentReceiptCopy 'application/json'
        spokenArtifactManifest = [ordered]@{ path = Get-RelativePath $jobRoot $spokenManifestPath; sha256 = $spokenArtifacts[$source.Key].ManifestSha256; bytes = (Get-Item $spokenManifestPath).Length }
    }
}

$voices = [ordered]@{
    'zh-CN' = 'zh-CN-XiaoxiaoNeural'; 'en-US' = 'en-US-AvaMultilingualNeural'
    'es-ES' = 'es-ES-XimenaNeural'; 'ja-JP' = 'ja-JP-NanamiNeural'; 'ko-KR' = 'ko-KR-SunHiNeural'
}
$synthesisCases = @()
$audioRefs = @{}
foreach ($case in @(
    [ordered]@{ name = 'zh-auto'; key = 'zh'; language = 'zh-CN'; strategy = 'auto'; override = $null }
    [ordered]@{ name = 'zh-override'; key = 'zh'; language = 'zh-CN'; strategy = 'override'; override = 'zh-CN' }
    [ordered]@{ name = 'en-auto'; key = 'en'; language = 'en-US'; strategy = 'auto'; override = $null }
    [ordered]@{ name = 'en-override'; key = 'en'; language = 'en-US'; strategy = 'override'; override = 'en-US' }
)) {
    $attemptId = "s$($case.key)$($case.strategy.Substring(0, 1))"
    $attempt = Join-Path $jobRoot "runs\$runId\stages\speech-synthesize\attempts\$attemptId"
    New-Item -ItemType Directory -Path $attempt | Out-Null
    $attemptRef = Get-RelativePath $jobRoot $attempt
    $spoken = $spokenArtifacts[$case.key]
    $request = [ordered]@{
        schemaVersion = '1.0.0'; requestId = "request-$($case.name)"; operationId = "operation-$($case.name)"
        taskId = $taskId; runId = $runId; stageId = 'speech-synthesize'; attemptId = $attemptId
        workerId = 'speech.edge'; operation = 'speech.synthesize'; idempotencyKey = Get-StringSha256 "$($spoken.File.sha256)|$($case.name)"
        attemptPath = $attemptRef; outputPath = "$attemptRef/outputs"; progressPath = "$attemptRef/progress.ndjson"; resultPath = "$attemptRef/result.json"
        cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRef/cancel.json"; gracePeriodMs = 2000 }
        inputRefs = @([ordered]@{ artifactRef = $spoken.ArtifactId; kind = 'text.spoken'; contractVersion = '1.0.0'; manifestPath = Get-RelativePath $jobRoot $spoken.ManifestPath; expectedSha256 = $spoken.ManifestSha256 })
        configSnapshot = [ordered]@{ edgeTtsPath = $edgeTts }
        payload = [ordered]@{
            schemaVersion = '1.0.0'; requestId = "request-$($case.name)"; operationId = "operation-$($case.name)"
            spokenText = [ordered]@{ artifactRef = $spoken.ArtifactId; path = $spoken.File.path; sha256 = $spoken.File.sha256; bytes = $spoken.File.bytes; encoding = 'utf-8'; mediaType = 'text/plain' }
            contextMode = 'selection_plus_paragraph'; language = [ordered]@{ strategy = $case.strategy; override = $case.override }
            voiceSnapshot = [ordered]@{ provider = 'edge-tts'; voicesByLanguage = $voices; ratePercent = 0; volumePercent = 0; pitchHz = 0 }
            playbackPolicy = [ordered]@{ autoPlay = $true; activePlayback = 'supersede'; replay = 'from_start'; naturalCompletion = 'idle_replayable'; stopTerminal = 'stopped' }
            cachePolicy = [ordered]@{ scope = 'task'; reuseSuccessfulAudio = $true; crossTaskReuse = $false }
        }
    }
    $requestPath = Join-Path $attempt 'request.json'
    Write-JsonOnce $requestPath $request
    Push-Location -LiteralPath ([IO.Path]::GetTempPath())
    try { & $worker run --request $requestPath | Out-Host; if ($LASTEXITCODE -ne 0) { throw "Synthesis failed: $($case.name)" } }
    finally { Pop-Location }
    $resultPath = Join-Path $attempt 'result.json'
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($result.status -ne 'completed' -or $result.payload.languageTag -ne $case.language -or $result.payload.cacheHit -ne $false) { throw "Unexpected synthesis result: $($case.name)" }
    $audioRef = $result.outputs[0]
    $audioManifestPath = Join-Path $jobRoot ($audioRef.manifestPath.Replace('/', '\'))
    if ((Get-FileHash -LiteralPath $audioManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $audioRef.expectedSha256) { throw "Audio manifest mismatch: $($case.name)" }
    $audioManifest = Get-Content -Raw -LiteralPath $audioManifestPath | ConvertFrom-Json
    $audioPath = Join-Path $jobRoot ($audioManifest.files[0].path.Replace('/', '\'))
    $audioHash = (Get-FileHash -LiteralPath $audioPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($audioHash -ne $audioManifest.files[0].sha256 -or (Get-Item -LiteralPath $audioPath).Length -ne $audioManifest.files[0].bytes -or $audioManifest.files[0].bytes -lt 1000) { throw "Audio payload mismatch: $($case.name)" }
    $audioRefs[$case.name] = $audioRef
    $synthesisCases += [ordered]@{
        case = $case.name; strategy = $case.strategy; override = $case.override; languageTag = $result.payload.languageTag; voiceId = $result.payload.voiceId; cacheHit = $result.payload.cacheHit
        requestPath = Get-RelativePath $runRoot $requestPath; resultPath = Get-RelativePath $runRoot $resultPath
        voiceSnapshot = $request.payload.voiceSnapshot
        audio = [ordered]@{ path = Get-RelativePath $runRoot $audioPath; bytes = $audioManifest.files[0].bytes; sha256 = $audioHash; mediaType = $audioManifest.files[0].mediaType }
    }
}

function Invoke-PlaybackCase([string]$caseName, [string]$reason) {
    $attemptIds = @{ 'natural-completion' = 'pn'; 'replay' = 'pr'; 'user-stop' = 'pu'; 'superseded' = 'ps'; 'task-cancel' = 'pc'; 'timeout' = 'pt' }
    $attemptId = $attemptIds[$caseName]
    $attempt = Join-Path $jobRoot "runs\$runId\stages\speech-playback\attempts\$attemptId"
    New-Item -ItemType Directory -Path $attempt | Out-Null
    $attemptRef = Get-RelativePath $jobRoot $attempt
    $audioRef = $audioRefs['zh-auto']
    $request = [ordered]@{
        schemaVersion = '1.0.0'; requestId = "request-$caseName"; operationId = "operation-$caseName"
        taskId = $taskId; runId = $runId; stageId = 'speech-playback'; attemptId = $attemptId
        workerId = 'speech.edge'; operation = 'speech.playback'; idempotencyKey = Get-StringSha256 "$($audioRef.expectedSha256)|$attemptId"
        attemptPath = $attemptRef; outputPath = "$attemptRef/outputs"; progressPath = "$attemptRef/progress.ndjson"; resultPath = "$attemptRef/result.json"
        cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRef/cancel.json"; gracePeriodMs = 2000 }
        inputRefs = @($audioRef); configSnapshot = [ordered]@{ ffplayPath = $fakePlayer }; payload = [ordered]@{}
    }
    $requestPath = Join-Path $attempt 'request.json'
    Write-JsonOnce $requestPath $request
    $previousDelay = $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS
    $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = $(if ($reason) { '1500' } else { '250' })
    try {
        if ([string]::IsNullOrWhiteSpace($reason)) {
            Push-Location -LiteralPath ([IO.Path]::GetTempPath())
            try { & $worker run --request $requestPath | Out-Host }
            finally { Pop-Location }
            $exitCode = $LASTEXITCODE
        }
        else {
            $stdout = Join-Path $attempt 'cli-stdout.ndjson'; $stderr = Join-Path $attempt 'cli-stderr.log'
            $process = Start-Process -FilePath $worker -ArgumentList @('run', '--request', ('"' + $requestPath + '"')) -WorkingDirectory ([IO.Path]::GetTempPath()) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            Start-Sleep -Milliseconds 350
            $requestedAt = [DateTimeOffset]::UtcNow
            Write-JsonOnce (Join-Path $attempt 'cancel.json') ([ordered]@{
                schemaVersion = '1.0.0'; attemptId = $attemptId; operationId = "operation-$caseName"; reason = $reason
                requestedAt = $requestedAt.ToString('o'); graceDeadline = $requestedAt.AddMilliseconds(2000).ToString('o')
            })
            $process.WaitForExit(); $exitCode = $process.ExitCode
        }
    }
    finally { $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = $previousDelay }
    $resultPath = Join-Path $attempt 'result.json'
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $expectedStatus = if ($reason -eq 'timeout') { 'failed' } elseif ($reason) { 'cancelled' } else { 'completed' }
    if ($result.status -ne $expectedStatus -or $result.payload.cacheHit -ne $false) { throw "Unexpected playback result: $caseName" }
    return [ordered]@{
        case = $caseName; attemptId = $attemptId; cancellationReason = $(if ($reason) { $reason } else { $null }); processExitCode = $exitCode
        status = $result.status; playbackState = $result.payload.playbackState; cacheHit = $result.payload.cacheHit
        errorCode = $(if ($result.error) { $result.error.code } else { $null })
        requestPath = Get-RelativePath $runRoot $requestPath; resultPath = Get-RelativePath $runRoot $resultPath
        audioManifestSha256 = $audioRef.expectedSha256
    }
}

$playbackCases = @()
$playbackCases += Invoke-PlaybackCase 'natural-completion' $null
$playbackCases += Invoke-PlaybackCase 'replay' $null
$playbackCases += Invoke-PlaybackCase 'user-stop' 'user_stop'
$playbackCases += Invoke-PlaybackCase 'superseded' 'superseded'
$playbackCases += Invoke-PlaybackCase 'task-cancel' 'task_cancel'
$playbackCases += Invoke-PlaybackCase 'timeout' 'timeout'

$receiptPath = Join-Path $runRoot 'backend-content-speech-canary-receipt.json'
$receipt = [ordered]@{
    schemaVersion = 1; canary = 'speech-backend-content-live-edge-tts'; canaryMode = 'backend-no-speaker'; completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); status = 'passed'
    worker = [ordered]@{ path = $worker; bytes = (Get-Item $worker).Length; sha256 = (Get-FileHash $worker -Algorithm SHA256).Hash.ToLowerInvariant(); workerId = 'speech.edge'; workerVersion = '1.2.0' }
    edgeTts = [ordered]@{ path = $edgeTts; bytes = (Get-Item $edgeTts).Length; sha256 = (Get-FileHash $edgeTts -Algorithm SHA256).Hash.ToLowerInvariant(); provider = 'edge-tts'; realSynthesisCalls = 4 }
    fakePlayer = [ordered]@{ path = $fakePlayer; sha256 = (Get-FileHash $fakePlayer -Algorithm SHA256).Hash.ToLowerInvariant(); physicalSpeakerPlayback = $false }
    contentSources = $sourceEvidence; synthesisCases = $synthesisCases; playbackCases = $playbackCases
    semantics = [ordered]@{ spokenTextRecleanedBySpeech = $false; replayUsesNewAttempt = $true; playbackResultReuse = $false; oldAiTtsRouteCount = 0; externalProductionWrites = 0 }
}
Write-JsonOnce $receiptPath $receipt
Write-Output "PASS BACKEND CONTENT SPEECH CANARY $receiptPath"
exit 0

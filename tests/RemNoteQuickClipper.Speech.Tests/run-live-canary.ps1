param(
    [string]$EdgeTtsPath,
    [string]$WorkerExe,
    [ValidateSet('auto', 'override')]
    [string]$LanguageStrategy = 'auto',
    [ValidateRange(-100, 100)]
    [int]$RatePercent = 0,
    [ValidateRange(-100, 100)]
    [int]$VolumePercent = 0,
    [ValidateRange(-1000, 1000)]
    [int]$PitchHz = 0,
    [switch]$RequireDeepOutput
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($WorkerExe)) { $WorkerExe = Join-Path $testRoot 'bin\KnowledgeClipper.Speech.Worker.exe' }
if (-not (Test-Path -LiteralPath $WorkerExe)) { throw "Build the worker first: $WorkerExe" }
$workerExe = (Resolve-Path -LiteralPath $WorkerExe).Path
if ([string]::IsNullOrWhiteSpace($EdgeTtsPath)) {
    $canonicalRuntime = 'D:\Applications\KnowledgeClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $legacyCompatibilityRuntime = 'D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $EdgeTtsPath = if (Test-Path -LiteralPath $canonicalRuntime) { $canonicalRuntime } else { $legacyCompatibilityRuntime }
}
if (-not (Test-Path -LiteralPath $EdgeTtsPath)) { throw "Missing Edge TTS: $EdgeTtsPath" }

$runRoot = Join-Path $testRoot ('canary\r' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + $LanguageStrategy.Substring(0, 1) + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
if ($RequireDeepOutput) { $runRoot = Join-Path $runRoot ('deep-' + ('x' * 36)) }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$samples = [ordered]@{
    'zh-CN' = '这是中文自然语音测试。'
    'en-US' = 'This is an English natural voice test.'
    'es-ES' = 'Esta es una prueba de voz natural en español.'
    'ja-JP' = 'これは日本語の自然な音声テストです。'
    'ko-KR' = '이것은 한국어 자연스러운 음성 테스트입니다.'
}
$voices = [ordered]@{
    'zh-CN' = 'zh-CN-XiaoxiaoNeural'
    'en-US' = 'en-US-AvaMultilingualNeural'
    'es-ES' = 'es-ES-XimenaNeural'
    'ja-JP' = 'ja-JP-NanamiNeural'
    'ko-KR' = 'ko-KR-SunHiNeural'
}
$receipt = @()

foreach ($entry in $samples.GetEnumerator()) {
    $short = $entry.Key.Replace('-', '').ToLowerInvariant()
    $job = Join-Path $runRoot "jobs\t$short"
    $spoken = Join-Path $job 'inputs\spoken\v0001\payload.txt'
    $attempt = Join-Path $job 'runs\r1\stages\speech-synthesize\attempts\a1'
    New-Item -ItemType Directory -Path (Split-Path -Parent $spoken), $attempt -Force | Out-Null
    [IO.File]::WriteAllText($spoken, $entry.Value, [Text.UTF8Encoding]::new($false))
    $relativeSpoken = [Uri]::UnescapeDataString(([Uri]($job.TrimEnd('\') + '\')).MakeRelativeUri([Uri]$spoken).ToString())
    $spokenSha = (Get-FileHash -LiteralPath $spoken -Algorithm SHA256).Hash.ToLowerInvariant()
    $speechRequest = [ordered]@{
        schemaVersion = '1.0.0'
        requestId = "canary-$short"
        operationId = "canary-$short"
        spokenText = [ordered]@{
            artifactRef = "spoken-$short"
            path = $relativeSpoken
            sha256 = $spokenSha
            bytes = (Get-Item -LiteralPath $spoken).Length
            encoding = 'utf-8'
            mediaType = 'text/plain'
        }
        contextMode = 'selection_only'
        language = [ordered]@{ strategy = $LanguageStrategy; override = $(if ($LanguageStrategy -eq 'override') { $entry.Key } else { $null }) }
        voiceSnapshot = [ordered]@{
            provider = 'edge-tts'; voicesByLanguage = $voices
            ratePercent = $RatePercent; volumePercent = $VolumePercent; pitchHz = $PitchHz
        }
        playbackPolicy = [ordered]@{
            autoPlay = $true; activePlayback = 'supersede'; replay = 'from_start'
            naturalCompletion = 'idle_replayable'; stopTerminal = 'stopped'
        }
        cachePolicy = [ordered]@{ scope = 'task'; reuseSuccessfulAudio = $true; crossTaskReuse = $false }
    }
    $attemptRelative = [Uri]::UnescapeDataString(([Uri]($job.TrimEnd('\') + '\')).MakeRelativeUri([Uri]$attempt).ToString())
    $request = [ordered]@{
        schemaVersion = '1.0.0'; requestId = $speechRequest.requestId; operationId = $speechRequest.operationId
        taskId = "t$short"; runId = 'r1'; stageId = 'speech-synthesize'; attemptId = 'a1'
        workerId = 'speech.edge'; operation = 'speech.synthesize'; idempotencyKey = $spokenSha
        attemptPath = $attemptRelative; outputPath = "$attemptRelative/outputs"
        progressPath = "$attemptRelative/progress.ndjson"; resultPath = "$attemptRelative/result.json"
        cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRelative/cancel.json"; gracePeriodMs = 2000 }
        inputRefs = @(); configSnapshot = [ordered]@{ edgeTtsPath = $EdgeTtsPath }; payload = $speechRequest
    }
    [IO.File]::WriteAllText((Join-Path $attempt 'request.json'), ($request | ConvertTo-Json -Depth 20 -Compress), [Text.UTF8Encoding]::new($false))
    & $workerExe run --request (Join-Path $attempt 'request.json')
    if ($LASTEXITCODE -ne 0) { throw "Worker failed for $($entry.Key)" }
    $result = Get-Content -LiteralPath (Join-Path $attempt 'result.json') -Raw | ConvertFrom-Json
    $manifestPath = Join-Path $job ($result.outputs[0].manifestPath.Replace('/', '\'))
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $result.outputs[0].expectedSha256) { throw "Manifest hash mismatch for $($entry.Key)" }
    $audio = Join-Path $job ($manifest.files[0].path.Replace('/', '\'))
    if ($result.status -ne 'completed' -or -not (Test-Path -LiteralPath $audio) -or (Get-Item -LiteralPath $audio).Length -lt 1000) { throw "Invalid audio for $($entry.Key)" }
    $receipt += [ordered]@{
        inputText = $entry.Value; inputTextPath = $relativeSpoken; inputTextSha256 = $spokenSha
        strategy = $LanguageStrategy; override = $(if ($LanguageStrategy -eq 'override') { $entry.Key } else { $null })
        language = $entry.Key; detectedLanguage = $result.payload.languageTag; status = $result.status; voice = $result.payload.voiceId
        voiceSnapshot = $speechRequest.voiceSnapshot
        audioBytes = (Get-Item -LiteralPath $audio).Length
        audioSha256 = (Get-FileHash -LiteralPath $audio -Algorithm SHA256).Hash.ToLowerInvariant()
        audioFullPathLength = $audio.Length
        deepPathVerified = ($audio.Length -gt 260)
        audioPath = [Uri]::UnescapeDataString(([Uri]($runRoot.TrimEnd('\') + '\')).MakeRelativeUri([Uri]$audio).ToString())
        resultPath = [Uri]::UnescapeDataString(([Uri]($runRoot.TrimEnd('\') + '\')).MakeRelativeUri([Uri](Join-Path $attempt 'result.json')).ToString())
    }
}

if ($RequireDeepOutput -and @($receipt | Where-Object { -not $_.deepPathVerified }).Count -ne 0) { throw 'Deep-path Canary did not publish every audio payload beyond 260 characters' }
$final = [ordered]@{
    schemaVersion = 1; canary = 'speech-worker-five-language-edge-tts'; completedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    workerExe = $workerExe; workerExeBytes = (Get-Item -LiteralPath $workerExe).Length
    workerExeSha256 = (Get-FileHash -LiteralPath $workerExe -Algorithm SHA256).Hash.ToLowerInvariant()
    languageStrategy = $LanguageStrategy
    voiceSettings = [ordered]@{ ratePercent = $RatePercent; volumePercent = $VolumePercent; pitchHz = $PitchHz }
    physicalSpeakerPlayback = $false; oldAiTtsRouteCount = 0
    deepPathRequired = [bool]$RequireDeepOutput
    deepPathPassed = (-not $RequireDeepOutput) -or (@($receipt | Where-Object { $_.deepPathVerified }).Count -eq $receipt.Count)
    results = $receipt
}
$receiptPath = Join-Path $runRoot 'canary-receipt.json'
[IO.File]::WriteAllText($receiptPath, ($final | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Write-Output "PASS LIVE CANARY $receiptPath"

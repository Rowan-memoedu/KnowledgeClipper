param(
    [string]$WorkerExe,
    [string]$EdgeTtsPath,
    [string]$FfplayPath = 'D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe',
    [ValidateSet('physical', 'synthetic')]
    [string]$CanaryMode = 'physical',
    [switch]$RequireDeepOutput
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($WorkerExe)) { $WorkerExe = Join-Path $testRoot 'bin\KnowledgeClipper.Speech.Worker.exe' }
$workerExe = [IO.Path]::GetFullPath($WorkerExe)
if ([string]::IsNullOrWhiteSpace($EdgeTtsPath)) {
    $canonicalRuntime = 'D:\Applications\KnowledgeClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $legacyCompatibilityRuntime = 'D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $EdgeTtsPath = if (Test-Path -LiteralPath $canonicalRuntime) { $canonicalRuntime } else { $legacyCompatibilityRuntime }
}
foreach ($required in @($workerExe, $EdgeTtsPath, $FfplayPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing runtime: $required" }
}

function Get-RelativePath([string]$root, [string]$path) {
    return [Uri]::UnescapeDataString(([Uri]($root.TrimEnd('\') + '\')).MakeRelativeUri([Uri]$path).ToString())
}

function Get-TextSha256([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Write-JsonOnce([string]$path, $value) {
    if (Test-Path -LiteralPath $path) { throw "Immutable file exists: $path" }
    $temporary = Join-Path (Split-Path -Parent $path) ('.tmp-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Depth 30 -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $path)
}

$runRoot = Join-Path $testRoot ('pc\r' + (Get-Date).ToUniversalTime().ToString('HHmmss') + [guid]::NewGuid().ToString('N').Substring(0, 3))
if ($RequireDeepOutput) { $runRoot = Join-Path $runRoot ('deep-' + ('x' * 50)) }
$taskId = 'tpc'
$runId = 'r1'
$jobRoot = Join-Path $runRoot "jobs\$taskId"
$spokenText = '这是语音模块真实播放与取消测试。'
$spokenPath = Join-Path $jobRoot 'inputs\spoken-text\v0001\payload.txt'
$synthAttempt = Join-Path $jobRoot "runs\$runId\stages\speech-synthesize\attempts\s1"
New-Item -ItemType Directory -Path (Split-Path -Parent $spokenPath), $synthAttempt -Force | Out-Null
[IO.File]::WriteAllText($spokenPath, $spokenText, [Text.UTF8Encoding]::new($false))
$spokenHash = (Get-FileHash -LiteralPath $spokenPath -Algorithm SHA256).Hash.ToLowerInvariant()
$synthAttemptRef = Get-RelativePath $jobRoot $synthAttempt
$voices = [ordered]@{
    'zh-CN' = 'zh-CN-XiaoxiaoNeural'; 'en-US' = 'en-US-AvaMultilingualNeural'
    'es-ES' = 'es-ES-XimenaNeural'; 'ja-JP' = 'ja-JP-NanamiNeural'; 'ko-KR' = 'ko-KR-SunHiNeural'
}
$synthRequest = [ordered]@{
    schemaVersion = '1.0.0'; requestId = 'request-synth-001'; operationId = 'operation-synth-001'
    taskId = $taskId; runId = $runId; stageId = 'speech-synthesize'; attemptId = 's1'
    workerId = 'speech.edge'; operation = 'speech.synthesize'; idempotencyKey = $spokenHash
    attemptPath = $synthAttemptRef; outputPath = "$synthAttemptRef/outputs"
    progressPath = "$synthAttemptRef/progress.ndjson"; resultPath = "$synthAttemptRef/result.json"
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$synthAttemptRef/cancel.json"; gracePeriodMs = 2000 }
    inputRefs = @(); configSnapshot = [ordered]@{ edgeTtsPath = $EdgeTtsPath }
    payload = [ordered]@{
        schemaVersion = '1.0.0'; requestId = 'request-synth-001'; operationId = 'operation-synth-001'
        spokenText = [ordered]@{ artifactRef = 'spoken-text'; path = (Get-RelativePath $jobRoot $spokenPath); sha256 = $spokenHash; bytes = (Get-Item -LiteralPath $spokenPath).Length; encoding = 'utf-8'; mediaType = 'text/plain' }
        contextMode = 'selection_only'; language = [ordered]@{ strategy = 'auto'; override = $null }
        voiceSnapshot = [ordered]@{ provider = 'edge-tts'; voicesByLanguage = $voices; ratePercent = 0; volumePercent = 0; pitchHz = 0 }
        playbackPolicy = [ordered]@{ autoPlay = $true; activePlayback = 'supersede'; replay = 'from_start'; naturalCompletion = 'idle_replayable'; stopTerminal = 'stopped' }
        cachePolicy = [ordered]@{ scope = 'task'; reuseSuccessfulAudio = $true; crossTaskReuse = $false }
    }
}
$synthRequestPath = Join-Path $synthAttempt 'request.json'
Write-JsonOnce $synthRequestPath $synthRequest
& $workerExe run --request $synthRequestPath
if ($LASTEXITCODE -ne 0) { throw 'Synthesis stage failed' }
$synthResult = Get-Content -Raw -LiteralPath (Join-Path $synthAttempt 'result.json') | ConvertFrom-Json
$audioRef = $synthResult.outputs[0]
$audioManifestPath = Join-Path $jobRoot ($audioRef.manifestPath.Replace('/', '\'))
$audioManifest = Get-Content -Raw -LiteralPath $audioManifestPath | ConvertFrom-Json
$audioPath = Join-Path $jobRoot ($audioManifest.files[0].path.Replace('/', '\'))
if ($RequireDeepOutput -and $audioPath.Length -le 260) { throw "Deep playback Canary audio path is not beyond MAX_PATH: $($audioPath.Length)" }

function Invoke-PlaybackCase([string]$caseName, [string]$reason) {
    $shortId = (Get-TextSha256 $caseName).Substring(0, 6)
    $attemptId = "p$shortId"
    $operationId = "op-$shortId"
    $attempt = Join-Path $jobRoot "runs\$runId\stages\speech-playback\attempts\$attemptId"
    New-Item -ItemType Directory -Path $attempt -Force | Out-Null
    $attemptRef = Get-RelativePath $jobRoot $attempt
    $request = [ordered]@{
        schemaVersion = '1.0.0'; requestId = "request-$caseName"; operationId = $operationId
        taskId = $taskId; runId = $runId; stageId = 'speech-playback'; attemptId = $attemptId
        workerId = 'speech.edge'; operation = 'speech.playback'; idempotencyKey = (Get-TextSha256 "$($audioManifest.files[0].sha256)|$attemptId")
        attemptPath = $attemptRef; outputPath = "$attemptRef/outputs"
        progressPath = "$attemptRef/progress.ndjson"; resultPath = "$attemptRef/result.json"
        cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRef/cancel.json"; gracePeriodMs = 2000 }
        inputRefs = @($audioRef); configSnapshot = [ordered]@{ ffplayPath = $FfplayPath }; payload = [ordered]@{}
    }
    $requestPath = Join-Path $attempt 'request.json'
    Write-JsonOnce $requestPath $request
    $startedAt = [DateTimeOffset]::UtcNow
    if ([string]::IsNullOrWhiteSpace($reason)) {
        $cliOutput = & $workerExe run --request $requestPath
        $exitCode = $LASTEXITCODE
        if (-not $cliOutput) { throw "$caseName worker emitted no terminal progress" }
    }
    else {
        $stdout = Join-Path $attempt 'cli-stdout.ndjson'
        $stderr = Join-Path $attempt 'cli-stderr.log'
        $process = Start-Process -FilePath $workerExe -ArgumentList @('run', '--request', ('"' + $requestPath + '"')) -WorkingDirectory (Get-Location).Path -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        Start-Sleep -Milliseconds 350
        $requestedAt = [DateTimeOffset]::UtcNow
        $signal = [ordered]@{
            schemaVersion = '1.0.0'; attemptId = $attemptId; operationId = $operationId; reason = $reason
            requestedAt = $requestedAt.ToString('o'); graceDeadline = $requestedAt.AddMilliseconds(2000).ToString('o')
        }
        Write-JsonOnce (Join-Path $attempt 'cancel.json') $signal
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    $result = Get-Content -Raw -LiteralPath (Join-Path $attempt 'result.json') | ConvertFrom-Json
    $expectedStatus = if ($reason -eq 'timeout') { 'failed' } elseif ($reason) { 'cancelled' } else { 'completed' }
    if ($result.status -ne $expectedStatus) { throw "$caseName expected $expectedStatus, received $($result.status)" }
    if ($result.payload.cacheHit -ne $false) { throw "$caseName must be a fresh playback attempt with cacheHit=false" }
    return [ordered]@{
        case = $caseName; cancellationReason = $(if ($reason) { $reason } else { $null }); processExitCode = $exitCode
        status = $result.status; playbackState = $result.payload.playbackState; cacheHit = $result.payload.cacheHit
        errorCode = $(if ($result.error) { $result.error.code } else { $null })
        elapsedMs = [math]::Round(([DateTimeOffset]::UtcNow - $startedAt).TotalMilliseconds)
        requestPath = Get-RelativePath $runRoot $requestPath; resultPath = Get-RelativePath $runRoot (Join-Path $attempt 'result.json')
    }
}

$previousSyntheticPlaybackMs = $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS
if ($CanaryMode -eq 'synthetic') { $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = '1500' }
try {
    $cases = @()
    $cases += Invoke-PlaybackCase 'natural-completion' $null
    $cases += Invoke-PlaybackCase 'user-stop' 'user_stop'
    $cases += Invoke-PlaybackCase 'superseded' 'superseded'
    $cases += Invoke-PlaybackCase 'task-cancel' 'task_cancel'
    $cases += Invoke-PlaybackCase 'timeout' 'timeout'

    $receipt = [ordered]@{
        schemaVersion = 1
        canary = $(if ($CanaryMode -eq 'physical') { 'speech-worker-real-ffplay-and-cancellation' } else { 'speech-worker-synthetic-player-and-cancellation' })
        canaryMode = $CanaryMode; completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        status = 'passed'; inputText = $spokenText; language = 'zh-CN'; voice = $synthResult.payload.voiceId
        audio = [ordered]@{ bytes = (Get-Item -LiteralPath $audioPath).Length; sha256 = (Get-FileHash -LiteralPath $audioPath -Algorithm SHA256).Hash.ToLowerInvariant(); path = Get-RelativePath $runRoot $audioPath; absolutePathLength = $audioPath.Length }
        deepPathRequired = [bool]$RequireDeepOutput; deepPathPassed = (-not $RequireDeepOutput) -or ($audioPath.Length -gt 260)
        workerExeSha256 = (Get-FileHash -LiteralPath $workerExe -Algorithm SHA256).Hash.ToLowerInvariant()
        ffplayPath = $FfplayPath; cases = $cases
    }
    $receiptPath = Join-Path $runRoot 'playback-canary-receipt.json'
    Write-JsonOnce $receiptPath $receipt
    Write-Output "PASS LIVE PLAYBACK CANARY $receiptPath"
}
finally {
    $env:KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS = $previousSyntheticPlaybackMs
}

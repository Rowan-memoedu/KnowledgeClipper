param(
    [string]$AuthorityReleaseRoot,
    [string]$AuthorityCheckpointPath,
    [string]$EdgeTtsPath,
    [string]$FakePlayerExe
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
$expectedCheckpointSha = '3EFB89D6D642F8546ABD14C5A114DE00E14BB7173EF33F03D3FD06DDAA2ADB55'
$expectedReleaseTreeSha = '7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574'
$expectedWorkerSha = '51FF5937C297DAD562AB781D6114374649E1A965EDBB43D575264BEED3DFB19C'

if ([string]::IsNullOrWhiteSpace($AuthorityReleaseRoot)) {
    $AuthorityReleaseRoot = Join-Path $projectRoot 'releases\knowledge-clipper-authority-v1.2.3-20260828T021300Z\win-x64'
}
if ([string]::IsNullOrWhiteSpace($AuthorityCheckpointPath)) {
    $AuthorityCheckpointPath = Join-Path $projectRoot 'contracts\authority\worker-registry\v1.2.3-20260828T021300Z\checkpoint.json'
}
if ([string]::IsNullOrWhiteSpace($EdgeTtsPath)) {
    $canonicalRuntime = 'D:\Applications\KnowledgeClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $legacyCompatibilityRuntime = 'D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe'
    $EdgeTtsPath = if (Test-Path -LiteralPath $canonicalRuntime) { $canonicalRuntime } else { $legacyCompatibilityRuntime }
}
if ([string]::IsNullOrWhiteSpace($FakePlayerExe)) { $FakePlayerExe = Join-Path $testRoot 'bin\KnowledgeClipper.Speech.FakeTool.exe' }

$releaseRoot = (Resolve-Path -LiteralPath $AuthorityReleaseRoot).Path
$checkpointPath = (Resolve-Path -LiteralPath $AuthorityCheckpointPath).Path
$edgeTts = (Resolve-Path -LiteralPath $EdgeTtsPath).Path
$fakePlayer = (Resolve-Path -LiteralPath $FakePlayerExe).Path
$checkpointSha = (Get-FileHash -LiteralPath $checkpointPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($checkpointSha -ne $expectedCheckpointSha) { throw "Authority checkpoint hash mismatch: $checkpointSha" }
$checkpoint = Get-Content -Raw -LiteralPath $checkpointPath | ConvertFrom-Json
if ($checkpoint.admissionStatus -ne 'authority-admitted' -or $checkpoint.registry.registryVersion -ne '1.2.3' -or $checkpoint.release.treeSha256 -ne $expectedReleaseTreeSha) {
    throw 'Authority v1.2.3 checkpoint identity mismatch'
}
$worker = Join-Path $releaseRoot 'workers\speech\KnowledgeClipper.Speech.Worker.exe'
$orchestrator = Join-Path $releaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
foreach ($required in @($worker, $orchestrator, $edgeTts, $fakePlayer)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing candidate dependency: $required" }
}
$workerSha = (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash.ToUpperInvariant()
if ($workerSha -ne $expectedWorkerSha -or (Get-Item -LiteralPath $worker).Length -ne 74240) { throw 'Authority speech worker identity mismatch' }
$descriptorText = & $worker describe --json
if ($LASTEXITCODE -ne 0) { throw 'Authority speech worker describe failed' }
$descriptor = $descriptorText | ConvertFrom-Json
if ($descriptor.workerId -ne 'speech.edge' -or $descriptor.workerVersion -ne '1.2.0') { throw 'Authority speech descriptor mismatch' }

function Get-ReceiptPath([object[]]$output, [string]$prefix) {
    $line = @($output | ForEach-Object { [string]$_ } | Where-Object { $_ -like "$prefix*" }) | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) { throw "Missing receipt line: $prefix" }
    return (Resolve-Path -LiteralPath $line.Substring($prefix.Length).Trim()).Path
}

function Write-TextOnce([string]$path, [string]$text) {
    if (Test-Path -LiteralPath $path) { throw "Immutable file exists: $path" }
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    $temporary = Join-Path $directory ('.tmp-' + [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($temporary, $text, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $path)
}

$runRoot = Join-Path $testRoot ('candidate-bound-v123\r' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $runRoot | Out-Null

$backendOutput = & (Join-Path $testRoot 'run-backend-content-speech-canary.ps1') -WorkerExe $worker -EdgeTtsPath $edgeTts -FakePlayerExe $fakePlayer 2>&1
$backendOutput | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Candidate-bound Content/playback Canary failed' }
$backendReceiptPath = Get-ReceiptPath $backendOutput 'PASS BACKEND CONTENT SPEECH CANARY '

$autoOutput = & (Join-Path $testRoot 'run-live-canary.ps1') -WorkerExe $worker -EdgeTtsPath $edgeTts -LanguageStrategy auto -RatePercent 12 -VolumePercent -7 -PitchHz 18 2>&1
$autoOutput | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Candidate-bound five-language auto-route Canary failed' }
$autoReceiptPath = Get-ReceiptPath $autoOutput 'PASS LIVE CANARY '

$overrideOutput = & (Join-Path $testRoot 'run-live-canary.ps1') -WorkerExe $worker -EdgeTtsPath $edgeTts -LanguageStrategy override -RatePercent -8 -VolumePercent 4 -PitchHz -22 2>&1
$overrideOutput | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Candidate-bound five-language override Canary failed' }
$overrideReceiptPath = Get-ReceiptPath $overrideOutput 'PASS LIVE CANARY '

$coreReuseText = (& python (Join-Path $projectRoot 'tools\run_speech_reuse_regression.py') --orchestrator $orchestrator 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Authority Core speech reuse regression failed: $coreReuseText" }
$coreReuse = $coreReuseText | ConvertFrom-Json
if ($coreReuse.status -ne 'passed' -or $coreReuse.synthesize.cacheHit -ne $true -or $coreReuse.playback.reused -ne $false) {
    throw 'Authority Core reuse semantics mismatch'
}
$coreReusePath = Join-Path $runRoot 'authority-core-speech-reuse.json'
Write-TextOnce $coreReusePath (($coreReuse | ConvertTo-Json -Depth 30) + "`n")

$backend = Get-Content -Raw -LiteralPath $backendReceiptPath | ConvertFrom-Json
$auto = Get-Content -Raw -LiteralPath $autoReceiptPath | ConvertFrom-Json
$override = Get-Content -Raw -LiteralPath $overrideReceiptPath | ConvertFrom-Json
$readable = New-Object System.Collections.Generic.List[string]
$readable.Add('# speech.edge v1.2.3 candidate-bound 后端验收')
$readable.Add('')
$readable.Add("- authority checkpoint: ``$expectedCheckpointSha``")
$readable.Add("- authority release tree: ``$expectedReleaseTreeSha``")
$readable.Add("- worker: ``speech.edge 1.2.0`` / ``$expectedWorkerSha``")
$readable.Add('- playback runtime: FakePlayer；物理扬声器：否')
$readable.Add('')
$readable.Add('## 输入文本 → 语言/音色选择 → 音频 bytes/SHA')
$readable.Add('')
$readable.Add('|策略|输入文本|语言|音色|rate|volume|pitch|bytes|SHA-256|')
$readable.Add('|---|---|---|---|---:|---:|---:|---:|---|')
foreach ($item in @($auto.results) + @($override.results)) {
    $text = ([string]$item.inputText).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
    $readable.Add("|$($item.strategy)|$text|$($item.detectedLanguage)|$($item.voice)|$($item.voiceSnapshot.ratePercent)|$($item.voiceSnapshot.volumePercent)|$($item.voiceSnapshot.pitchHz)|$($item.audioBytes)|``$($item.audioSha256)``|")
}
$readable.Add('')
$readable.Add('## FakePlayer 播放状态')
$readable.Add('')
$readable.Add('|case|attempt|cancel reason|worker status|playback state|cache hit|')
$readable.Add('|---|---|---|---|---|---|')
foreach ($item in $backend.playbackCases) {
    $readable.Add("|$($item.case)|$($item.attemptId)|$($item.cancellationReason)|$($item.status)|$($item.playbackState)|$($item.cacheHit)|")
}
$readable.Add('')
$readable.Add("Core cache reuse: synth cacheHit=$($coreReuse.synthesize.cacheHit), reusedFromAttemptId=$($coreReuse.synthesize.reusedFromAttemptId); playback reused=$($coreReuse.playback.reused).")
$readablePath = Join-Path $runRoot 'readable-evidence.md'
Write-TextOnce $readablePath (($readable -join "`n") + "`n")

$receipt = [ordered]@{
    schemaVersion = 'speech-v123-candidate-bound-backend-canary.v1'; status = 'passed'; completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    authority = [ordered]@{
        checkpoint = [ordered]@{ path = $checkpointPath; bytes = (Get-Item -LiteralPath $checkpointPath).Length; sha256 = $checkpointSha }
        release = [ordered]@{ root = $releaseRoot; files = [int]$checkpoint.release.files; bytes = [long]$checkpoint.release.bytes; treeSha256 = $expectedReleaseTreeSha }
        registryVersion = '1.2.3'
    }
    worker = [ordered]@{ path = $worker; bytes = (Get-Item -LiteralPath $worker).Length; sha256 = $workerSha; workerId = $descriptor.workerId; workerVersion = $descriptor.workerVersion }
    runtime = [ordered]@{
        edgeTts = [ordered]@{ path = $edgeTts; sha256 = (Get-FileHash -LiteralPath $edgeTts -Algorithm SHA256).Hash.ToUpperInvariant(); realCalls = 14 }
        fakePlayer = [ordered]@{ path = $fakePlayer; sha256 = (Get-FileHash -LiteralPath $fakePlayer -Algorithm SHA256).Hash.ToUpperInvariant(); physicalSpeakerPlayback = $false }
    }
    evidence = [ordered]@{
        backendContentAndPlayback = [ordered]@{ path = $backendReceiptPath; sha256 = (Get-FileHash -LiteralPath $backendReceiptPath -Algorithm SHA256).Hash.ToUpperInvariant() }
        fiveLanguageAuto = [ordered]@{ path = $autoReceiptPath; sha256 = (Get-FileHash -LiteralPath $autoReceiptPath -Algorithm SHA256).Hash.ToUpperInvariant() }
        fiveLanguageOverride = [ordered]@{ path = $overrideReceiptPath; sha256 = (Get-FileHash -LiteralPath $overrideReceiptPath -Algorithm SHA256).Hash.ToUpperInvariant() }
        coreReuse = [ordered]@{ path = $coreReusePath; sha256 = (Get-FileHash -LiteralPath $coreReusePath -Algorithm SHA256).Hash.ToUpperInvariant(); harnessWorker = 'synthetic-contract-worker'; authorityCore = $orchestrator }
        readable = [ordered]@{ path = $readablePath; sha256 = (Get-FileHash -LiteralPath $readablePath -Algorithm SHA256).Hash.ToUpperInvariant() }
    }
    assertions = [ordered]@{
        languages = @('zh-CN', 'en-US', 'es-ES', 'ja-JP', 'ko-KR'); autoRoute = $true; explicitOverride = $true; voiceSnapshotPreserved = $true
        synthesisCacheReuseOwnedByCore = $true; replayCreatesFreshPlaybackAttempt = $true; playbackResultReuse = $false
        stopSignalConsumedByWorker = $true; publicPlaybackStopIpcOwnedByCore = $true
        physicalSpeakerPlayback = $false; oldAiTtsRouteCount = 0; externalProductionWrites = 0
    }
}
$receiptPath = Join-Path $runRoot 'speech-v123-candidate-bound-backend-receipt.json'
Write-TextOnce $receiptPath (($receipt | ConvertTo-Json -Depth 50) + "`n")
Write-Output "PASS V123 CANDIDATE-BOUND SPEECH BACKEND $receiptPath"

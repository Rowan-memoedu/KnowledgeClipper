[CmdletBinding()]
param([string]$OutputRoot)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$authority = Join-Path $project 'releases\knowledge-clipper-authority-v1.2.12-20260830T075600Z\win-x64'
$worker = Join-Path $authority 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$registry = Join-Path $project 'contracts\authority\worker-registry\v1.2.12-20260830T075600Z\worker-registry.v1.json'
$checkpoint = Join-Path $project 'contracts\authority\worker-registry\v1.2.12-20260830T075600Z\checkpoint.json'
$sourceBase = Join-Path $project 'canary\phase-b-candidate-v1.2.12\20260830T084700Z'
$rootRemId = '8zNzfX45XLCSKoPSJ'
$rootTitle = 'KnowledgeClipper PhaseB Canary 20260828T081109Z'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $project "canary\remnote-exporter-v2.1-live-replay\$stamp"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Append-only output already exists: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Write-Json([string]$Path, $Value) {
    Write-Utf8NoBom $Path (($Value | ConvertTo-Json -Depth 30) + "`n")
}

function Hash-Record([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    [ordered]@{ path = $resolved; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash; bytes = (Get-Item -LiteralPath $resolved).Length }
}

function Assert-Hash([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if ($actual -ne $Expected) { throw "Hash mismatch: $Path expected=$Expected actual=$actual" }
}

function Invoke-Bounded([string]$FileName, [string[]]$Arguments, [int]$TimeoutMilliseconds, [string]$Label) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$psi.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "$Label failed to start" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try { $process.Kill($true) } catch { }
        throw "$Label timed out after $TimeoutMilliseconds ms"
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

function Invoke-Worker([string[]]$Arguments, [string]$Label, [int]$TimeoutMilliseconds = 90000) {
    $result = Invoke-Bounded $worker $Arguments $TimeoutMilliseconds $Label
    if ($result.ExitCode -ne 0) { throw "$Label failed exit=$($result.ExitCode) stderr=$($result.Stderr)" }
    return $result
}

function Invoke-Read([string]$RemId, [string]$OutputPath) {
    $config = Get-Content -Raw (Join-Path $authority 'workers\remnote\appsettings.json') | ConvertFrom-Json
    $arguments = @(
        $config.RemnoteCliJsPath, '--mcp-url', $config.RemnoteMcpUrl,
        'read', $RemId, '--content-mode', 'structured', '--depth', '2', '--child-limit', '500', '--view', 'full', '--ancestor-depth', '3'
    )
    $result = Invoke-Bounded $config.NodePath $arguments 15000 "read $RemId"
    if ($result.ExitCode -ne 0) { throw "read $RemId failed exit=$($result.ExitCode) stderr=$($result.Stderr)" }
    $parsed = $result.Stdout | ConvertFrom-Json
    Write-Json $OutputPath $parsed
    return $parsed
}

function New-ReplayRequest([string]$SourceRequestPath, [string]$SourceJobRoot, [string]$RouteName) {
    $source = Get-Content -Raw -LiteralPath $SourceRequestPath | ConvertFrom-Json
    if ($source.workerId -ne 'export.remnote' -or $source.payload.target.rootRemId -ne $rootRemId -or $source.payload.target.rootTitle -ne $rootTitle) {
        throw "Source request target mismatch: $SourceRequestPath"
    }
    if ($source.payload.sink -ne 'mcp-direct' -or $source.payload.route.kind -ne $RouteName) {
        throw "Source request route mismatch: $SourceRequestPath"
    }
    $jobRoot = Join-Path $OutputRoot (Join-Path 'jobs' $source.taskId)
    New-Item -ItemType Directory -Path $jobRoot -Force | Out-Null
    foreach ($inputRef in $source.inputRefs) {
        $relativeManifest = $inputRef.manifestPath.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $sourceManifest = Join-Path $SourceJobRoot $relativeManifest
        Assert-Hash $sourceManifest $inputRef.expectedSha256.ToUpperInvariant()
        $sourceDirectory = Split-Path -Parent $sourceManifest
        $destinationDirectory = Join-Path $jobRoot (Split-Path -Parent $relativeManifest)
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $destinationDirectory -Recurse -Force
        Assert-Hash (Join-Path $jobRoot $relativeManifest) $inputRef.expectedSha256.ToUpperInvariant()
    }
    $runId = "run-live-replay-$($RouteName.Replace('-', '_'))-$stamp"
    $attemptId = "attempt-live-replay-$([Guid]::NewGuid().ToString('N'))"
    $attemptRelative = "runs/$runId/stages/export-remnote/attempts/$attemptId"
    $source.requestId = "request-$([Guid]::NewGuid().ToString('N'))"
    $source.operationId = "operation-$([Guid]::NewGuid().ToString('N'))"
    $source.runId = $runId
    $source.attemptId = $attemptId
    $source.attemptPath = $attemptRelative
    $source.outputPath = "$attemptRelative/outputs"
    $source.progressPath = "$attemptRelative/progress.ndjson"
    $source.resultPath = "$attemptRelative/result.json"
    $source.cancelSignal.path = "$attemptRelative/cancel.json"
    $attemptDirectory = Join-Path $jobRoot $attemptRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path $attemptDirectory -Force | Out-Null
    $requestPath = Join-Path $attemptDirectory 'request.json'
    Write-Json $requestPath $source
    [pscustomobject]@{ Request = $source; RequestPath = $requestPath; ResultPath = Join-Path $attemptDirectory 'result.json'; JobRoot = $jobRoot }
}

Assert-Hash $registry 'FC105AB65FD80B72779D1601E2C4B6FAA7ECCAF5F5C1BADC774060D8F685495A'
Assert-Hash $checkpoint '552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580'
Assert-Hash $worker '443835ECA91E5F68C97070777C250FD95E4A2E4873C9EFC7FBF45A528EF2CAD2'
$describe = (Invoke-Worker @('describe', '--json') 'authority describe' 15000).Stdout | ConvertFrom-Json
if ($describe.workerId -ne 'export.remnote' -or $describe.workerVersion -ne '2.1.0') { throw 'Authority worker identity mismatch' }

$settingsPath = Join-Path $OutputRoot 'settings-snapshot.json'
$settings = [ordered]@{
    schemaVersion = 'remnote-destination-settings-snapshot.v1'; destinationType = 'remnote'; enabled = $true
    target = [ordered]@{ rootRemId = $rootRemId; rootTitle = $rootTitle }
    advanced = [ordered]@{ sink = 'mcp-direct'; clipRouting = 'clip-daily-route'; cardRouting = 'card-export'; timeZone = 'Asia/Shanghai'; dateFormat = 'yyyy-MM-dd' }
    secretReferences = @()
}
Write-Json $settingsPath $settings
$settingsHash = (Get-FileHash -Algorithm SHA256 $settingsPath).Hash
$healthRequestPath = Join-Path $OutputRoot 'health-probe-request.json'
$healthRequest = [ordered]@{
    schemaVersion = 'remnote-readonly-probe-request.v1'; probeId = "v1212-live-$stamp"; mode = 'health'; timeoutMs = 15000
    authorizationScope = 'isolated-canary'; target = [ordered]@{ rootRemId = $rootRemId; rootTitle = $rootTitle }
    operations = @('status', 'search', 'list', 'read')
    evidence = [ordered]@{ settingsSnapshot = [ordered]@{ path = $settingsPath; sha256 = $settingsHash } }
}
Write-Json $healthRequestPath $healthRequest
$healthResultPath = Join-Path $OutputRoot 'health-probe-result.json'
$healthInvocation = Invoke-Worker @('probe', '--request', $healthRequestPath, '--json') 'health probe' 90000
$health = $healthInvocation.Stdout | ConvertFrom-Json
Write-Json $healthResultPath $health
if ($health.state -ne 'healthy' -or $health.admission -ne 'ready' -or -not $health.targetBinding.matched -or $health.targetBinding.rootRemId -ne $rootRemId -or $health.externalWriteCount -ne 0) {
    throw 'Health probe did not authorize the isolated root'
}

$cardSourceRequest = Join-Path $sourceBase 'a\jobs\pba084335\runs\run-initial\stages\export-remnote\attempts\attempt-20260830084346959-6eee249c5b404501b41561ecbb29de58\request.json'
$cardSourceJob = Join-Path $sourceBase 'a\jobs\pba084335'
$clipSourceRequest = Join-Path $sourceBase 'b\jobs\pbb084335\runs\run-remnote-replay\stages\export-remnote\attempts\attempt-replay-8a040eb801d84ec7adcf145bfe4ba585\request.json'
$clipSourceJob = Join-Path $sourceBase 'b\jobs\pbb084335'
$cardRun = New-ReplayRequest $cardSourceRequest $cardSourceJob 'card-export'
$clipRun = New-ReplayRequest $clipSourceRequest $clipSourceJob 'clip-daily-route'

[void](Invoke-Worker @('run', '--request', $clipRun.RequestPath) 'clip replay' 120000)
[void](Invoke-Worker @('run', '--request', $cardRun.RequestPath) 'card replay' 120000)
$clipResult = Get-Content -Raw $clipRun.ResultPath | ConvertFrom-Json
$cardResult = Get-Content -Raw $cardRun.ResultPath | ConvertFrom-Json
if ($clipResult.status -ne 'completed' -or $clipResult.worker.version -ne '2.1.0' -or $clipResult.payload.outcome -ne 'already_applied') { throw 'Clip replay did not complete as already_applied' }
if ($cardResult.status -ne 'completed' -or $cardResult.worker.version -ne '2.1.0' -or $cardResult.payload.outcome -ne 'already_applied') { throw 'Card replay did not complete as already_applied' }

function Get-OutputReceipt($Result, [string]$JobRoot) {
    $manifestPath = Join-Path $JobRoot $Result.outputs[0].manifestPath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Assert-Hash $manifestPath $Result.outputs[0].expectedSha256.ToUpperInvariant()
    $manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
    $receiptPath = Join-Path $JobRoot $manifest.files[0].path.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Assert-Hash $receiptPath $manifest.files[0].sha256.ToUpperInvariant()
    [pscustomobject]@{ ManifestPath = $manifestPath; ReceiptPath = $receiptPath; Receipt = (Get-Content -Raw $receiptPath | ConvertFrom-Json) }
}
$clipOutput = Get-OutputReceipt $clipResult $clipRun.JobRoot
$cardOutput = Get-OutputReceipt $cardResult $cardRun.JobRoot

$china = [TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')
$localDate = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $china).ToString('yyyy-MM-dd')
if ($clipOutput.Receipt.rootRemId -ne $rootRemId -or $clipOutput.Receipt.localDate -ne $localDate -or $clipOutput.Receipt.timeZone -ne 'Asia/Shanghai' -or $clipOutput.Receipt.dateFormat -ne 'yyyy-MM-dd' -or $clipOutput.Receipt.dateOutcome -ne 'reused' -or $clipOutput.Receipt.clipOutcome -ne 'already_applied' -or -not $clipOutput.Receipt.dateRouting -or -not $clipOutput.Receipt.readbackVerified -or $clipOutput.Receipt.duplicateCount -ne 0) {
    throw 'Clip replay receipt semantics mismatch'
}
if ($cardOutput.Receipt.rootRemId -ne $rootRemId -or $cardOutput.Receipt.outcome -ne 'already_applied' -or $cardOutput.Receipt.dateRouting -or -not $cardOutput.Receipt.readbackVerified -or $cardOutput.Receipt.duplicateCount -ne 0) {
    throw 'Card replay receipt semantics mismatch'
}

function Invoke-ReconcileProbe([string]$Name, $Run, $Result) {
    $requestPath = Join-Path $OutputRoot "$Name-reconcile-probe-request.json"
    $resultPath = Join-Path $OutputRoot "$Name-reconcile-probe-result.json"
    $request = [ordered]@{
        schemaVersion = 'remnote-readonly-probe-request.v1'; probeId = "$Name-reconcile-$stamp"; mode = 'reconcile'; timeoutMs = 15000
        authorizationScope = 'isolated-canary'; target = [ordered]@{ rootRemId = $rootRemId; rootTitle = $rootTitle }
        operations = @('status', 'search', 'list', 'read')
        evidence = [ordered]@{
            settingsSnapshot = [ordered]@{ path = $settingsPath; sha256 = $settingsHash }
            workerRequest = [ordered]@{ path = $Run.RequestPath; sha256 = (Get-FileHash -Algorithm SHA256 $Run.RequestPath).Hash }
            workerResult = [ordered]@{ path = $Run.ResultPath; sha256 = (Get-FileHash -Algorithm SHA256 $Run.ResultPath).Hash }
        }
    }
    Write-Json $requestPath $request
    $invocation = Invoke-Worker @('probe', '--request', $requestPath, '--json') "$Name reconcile probe" 120000
    $probe = $invocation.Stdout | ConvertFrom-Json
    Write-Json $resultPath $probe
    if ($probe.state -ne 'healthy' -or $probe.admission -ne 'ready' -or $probe.externalWriteCount -ne 0 -or $probe.evidenceChain.status -ne 'valid' -or $probe.evidenceChain.automaticRetryAllowed) {
        throw "$Name reconcile probe failed"
    }
    [pscustomobject]@{ RequestPath = $requestPath; ResultPath = $resultPath; Result = $probe }
}
$clipReconcile = Invoke-ReconcileProbe 'clip' $clipRun $clipResult
$cardReconcile = Invoke-ReconcileProbe 'card' $cardRun $cardResult

$dateReadPath = Join-Path $OutputRoot 'readback-date.json'
$clipReadPath = Join-Path $OutputRoot 'readback-clip.json'
$cardReadPath = Join-Path $OutputRoot 'readback-card-document.json'
$dateRead = Invoke-Read $clipOutput.Receipt.dateRemId $dateReadPath
$clipRead = Invoke-Read $clipOutput.Receipt.clipRemId $clipReadPath
$cardRead = Invoke-Read $cardOutput.Receipt.documentRemId $cardReadPath
if ($dateRead.parentRemId -ne $rootRemId -or $dateRead.title -ne $localDate) { throw 'Date readback hierarchy mismatch' }
if ($clipRead.parentRemId -ne $clipOutput.Receipt.dateRemId) { throw 'Clip readback hierarchy mismatch' }
if ($cardRead.parentRemId -ne $rootRemId -or $cardRead.remType -ne 'document') { throw 'Card document unexpectedly used a date node' }

$cardLearningPath = Join-Path $sourceBase 'a\jobs\pba084335\runs\run-initial\stages\content.understand-and-card\attempts\attempt-20260830084343245-159e1aa8210d4f05b9ceaf4e2d1451cc\outputs\learning-bundle.schema-v1.json'
$cardBundlePath = Join-Path $sourceBase 'a\jobs\pba084335\runs\run-initial\stages\content.understand-and-card\attempts\attempt-20260830084343245-159e1aa8210d4f05b9ceaf4e2d1451cc\outputs\card-bundle.schema-v1.json'
$clipLearningPath = Join-Path $sourceBase 'b\jobs\pbb084335\runs\run-initial\stages\content.understand-and-card\attempts\attempt-20260830084353233-b9e712b1962b4e2d95d5ced09b6e8329\outputs\learning-bundle.schema-v1.json'
$cardLearning = Get-Content -Raw $cardLearningPath | ConvertFrom-Json
$cardBundle = Get-Content -Raw $cardBundlePath | ConvertFrom-Json
$clipLearning = Get-Content -Raw $clipLearningPath | ConvertFrom-Json
$cardOriginal = $cardLearning.items[0].source_segments[0].text
$cardCleaned = $cardLearning.items[0].cleaning.cleaned_text
$cardFront = $cardBundle.cards[0].front_blocks[0].text
$cardBack = $cardBundle.cards[0].back_blocks[0].text
$clipOriginal = $clipLearning.items[0].source_segments[0].text
$clipCleaned = $clipLearning.items[0].cleaning.cleaned_text
if ($cardLearning.worker.version -ne '0.7.0' -or $clipLearning.worker.version -ne '0.7.0' -or $cardOriginal -ne $cardCleaned -or $clipOriginal -ne $clipCleaned) { throw 'Content 0.7.0 source/cleaned evidence mismatch' }
if ($cardRead.contentStructured.Count -ne 1 -or $cardRead.contentStructured[0].title -ne $cardFront -or $cardRead.contentStructured[0].cardDirection -ne 'forward' -or -not $cardRead.contentStructured[0].headline.Contains($cardBack)) { throw 'Card exact readback mismatch' }
$clipLines = @($clipRead.contentStructured | ForEach-Object { $_.title })
if (($clipLines -join "`n") -ne $clipCleaned) { throw 'Clip exact readback mismatch' }

$originalClipReceiptPath = Join-Path $sourceBase 'b\jobs\pbb084335\runs\run-initial\stages\export-remnote\attempts\attempt-20260830084356796-00d965206146444a95afab45086fa9d9\outputs\remnote-write-receipt\v0001\receipt.json'
$originalCardReceiptPath = Join-Path $sourceBase 'a\jobs\pba084335\runs\run-initial\stages\export-remnote\attempts\attempt-20260830084346959-6eee249c5b404501b41561ecbb29de58\outputs\remnote-write-receipt\v0001\receipt.json'
$originalClipReceipt = Get-Content -Raw $originalClipReceiptPath | ConvertFrom-Json
$originalCardReceipt = Get-Content -Raw $originalCardReceiptPath | ConvertFrom-Json
if ($originalClipReceipt.outcome -ne 'created' -or $originalClipReceipt.dateOutcome -ne 'created' -or $originalClipReceipt.clipOutcome -ne 'created') { throw 'Original clip creation evidence mismatch' }
if ($originalCardReceipt.outcome -ne 'created' -or $originalCardReceipt.dateRouting) { throw 'Original card creation evidence mismatch' }

$reportPath = Join-Path $OutputRoot 'readable-report.md'
$report = @(
    '# RemNote Exporter 2.1.0 authority v1.2.12 isolated live replay',
    '',
    "- Isolated root: $rootRemId / $rootTitle",
    '- Formal “剪藏” touched: no',
    '- OCR calls: 0',
    '- New DeepSeek calls: 0; reused immutable real Content 0.7.0 outputs',
    '',
    '## Clip daily route',
    '',
    'Original/cleaned text (identical):',
    '',
    '```text',
    $clipCleaned,
    '```',
    '',
    "- Route: clip-daily-route / Asia/Shanghai / $localDate / yyyy-MM-dd",
    '- Original: date created, clip created',
    '- Current replay: date reused, clip already_applied, duplicateCount=0',
    "- Hierarchy: $rootRemId -> $($clipOutput.Receipt.dateRemId) -> $($clipOutput.Receipt.clipRemId)",
    "- Exact children: $($clipLines -join ' | ')",
    '',
    '## Card export',
    '',
    'Original/cleaned text (identical):',
    '',
    '```text',
    $cardCleaned,
    '```',
    '',
    "- Front: $cardFront",
    "- Back: $cardBack",
    '- Route: card-export; dateRouting=false',
    '- Original: document created',
    '- Current replay: already_applied, duplicateCount=0',
    "- Hierarchy: $rootRemId -> $($cardOutput.Receipt.documentRemId); no date ancestor",
    '- Readback: forward card, exact front/back'
) -join "`n"
Write-Utf8NoBom $reportPath $report

$receiptPath = Join-Path $OutputRoot 'receipt.json'
$receipt = [ordered]@{
    schemaVersion = 'remnote-exporter-v2.1-authority-v1.2.12-isolated-live-replay.v1'; status = 'passed'; generatedAt = [DateTime]::UtcNow.ToString('o')
    authority = [ordered]@{ version = '1.2.12'; registry = Hash-Record $registry; checkpoint = Hash-Record $checkpoint; worker = Hash-Record $worker; workerId = $describe.workerId; workerVersion = $describe.workerVersion }
    safety = [ordered]@{ isolatedRootRemId = $rootRemId; isolatedRootTitle = $rootTitle; formalClipRootTouched = $false; deletes = 0; ocrCalls = 0; newDeepSeekCalls = 0 }
    health = [ordered]@{ state = $health.state; admission = $health.admission; rootMatched = $health.targetBinding.matched; externalWriteCount = $health.externalWriteCount; request = Hash-Record $healthRequestPath; result = Hash-Record $healthResultPath }
    clip = [ordered]@{
        contentWorkerVersion = $clipLearning.worker.version; sourceMode = 'immutable-real-content-output-replay'; originalText = $clipOriginal; cleanedText = $clipCleaned
        route = 'clip-daily-route'; timeZone = 'Asia/Shanghai'; localDate = $localDate; dateFormat = 'yyyy-MM-dd'
        rootRemId = $rootRemId; dateRemId = $clipOutput.Receipt.dateRemId; clipRemId = $clipOutput.Receipt.clipRemId
        originalOutcome = $originalClipReceipt.outcome; originalDateOutcome = $originalClipReceipt.dateOutcome; replayOutcome = $clipOutput.Receipt.outcome; replayDateOutcome = $clipOutput.Receipt.dateOutcome
        duplicateCount = $clipOutput.Receipt.duplicateCount; readbackVerified = $clipOutput.Receipt.readbackVerified; exactReadback = $true
        sourceLearningBundle = Hash-Record $clipLearningPath; originalReceipt = Hash-Record $originalClipReceiptPath; request = Hash-Record $clipRun.RequestPath; result = Hash-Record $clipRun.ResultPath; replayReceipt = Hash-Record $clipOutput.ReceiptPath
        reconcileRequest = Hash-Record $clipReconcile.RequestPath; reconcileResult = Hash-Record $clipReconcile.ResultPath; dateReadback = Hash-Record $dateReadPath; clipReadback = Hash-Record $clipReadPath
    }
    card = [ordered]@{
        contentWorkerVersion = $cardLearning.worker.version; sourceMode = 'immutable-real-content-output-replay'; originalText = $cardOriginal; cleanedText = $cardCleaned; front = $cardFront; back = $cardBack
        route = 'card-export'; dateRouting = $false; rootRemId = $rootRemId; documentRemId = $cardOutput.Receipt.documentRemId
        originalOutcome = $originalCardReceipt.outcome; replayOutcome = $cardOutput.Receipt.outcome; duplicateCount = $cardOutput.Receipt.duplicateCount; readbackVerified = $cardOutput.Receipt.readbackVerified; exactReadback = $true
        sourceLearningBundle = Hash-Record $cardLearningPath; sourceCardBundle = Hash-Record $cardBundlePath; originalReceipt = Hash-Record $originalCardReceiptPath; request = Hash-Record $cardRun.RequestPath; result = Hash-Record $cardRun.ResultPath; replayReceipt = Hash-Record $cardOutput.ReceiptPath
        reconcileRequest = Hash-Record $cardReconcile.RequestPath; reconcileResult = Hash-Record $cardReconcile.ResultPath; documentReadback = Hash-Record $cardReadPath
    }
    report = Hash-Record $reportPath
}
Write-Json $receiptPath $receipt
Write-Output $receiptPath

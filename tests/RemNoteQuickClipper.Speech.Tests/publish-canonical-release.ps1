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

function Assert-Equal([string]$label, [string]$expected, [string]$actual) {
    if ($expected -cne $actual) { throw "$label expected=[$expected] actual=[$actual]" }
}

function Get-DescribeFrom([string]$worker, [string]$workingDirectory) {
    Push-Location -LiteralPath $workingDirectory
    try {
        $text = (& $worker describe --json | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw "describe failed from $workingDirectory with exit code $LASTEXITCODE" }
        return $text | ConvertFrom-Json
    }
    finally { Pop-Location }
}

$initialDescriptor = Get-DescribeFrom $builtWorker $projectRoot
Assert-Equal 'workerId' 'speech.edge' $initialDescriptor.workerId
Assert-Equal 'workerVersion' '1.2.3' $initialDescriptor.workerVersion
Assert-Equal 'contractVersion' '1.0.0' $initialDescriptor.contractVersion
$workerHash = (Get-FileHash -LiteralPath $builtWorker -Algorithm SHA256).Hash.ToLowerInvariant()
$workerBytes = (Get-Item -LiteralPath $builtWorker).Length
$candidateRoot = Join-Path $testRoot "publish\speech.edge\v$($initialDescriptor.workerVersion)\candidate-$workerHash"
$canonicalDirectory = Join-Path $candidateRoot 'workers\speech'
$publishedWorker = Join-Path $canonicalDirectory 'KnowledgeClipper.Speech.Worker.exe'

if (Test-Path -LiteralPath $publishedWorker) {
    Assert-Equal 'existing candidate hash' $workerHash ((Get-FileHash -LiteralPath $publishedWorker -Algorithm SHA256).Hash.ToLowerInvariant())
}
else {
    if (Test-Path -LiteralPath $candidateRoot) { throw "Candidate root already exists without the canonical entrypoint: $candidateRoot" }
    New-Item -ItemType Directory -Path $canonicalDirectory | Out-Null
    Copy-Item -LiteralPath $builtWorker -Destination $publishedWorker
}

Assert-Equal 'published hash' $workerHash ((Get-FileHash -LiteralPath $publishedWorker -Algorithm SHA256).Hash.ToLowerInvariant())
Assert-Equal 'published bytes' $workerBytes (Get-Item -LiteralPath $publishedWorker).Length
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedWorker)
Assert-Equal 'product name' '零碎知识剪藏' $versionInfo.ProductName
Assert-Equal 'file description' 'KnowledgeClipper Speech Worker' $versionInfo.FileDescription

$cwdChecks = @()
foreach ($cwd in @($projectRoot, $testRoot, [IO.Path]::GetTempPath())) {
    $resolvedCwd = (Resolve-Path -LiteralPath $cwd).Path
    $descriptor = Get-DescribeFrom $publishedWorker $resolvedCwd
    Assert-Equal "describe workerId from $resolvedCwd" 'speech.edge' $descriptor.workerId
    Assert-Equal "describe version from $resolvedCwd" '1.2.3' $descriptor.workerVersion
    $cwdChecks += [ordered]@{ cwd = $resolvedCwd; command = 'describe --json'; status = 'passed' }
}

$unitRoot = (Get-ChildItem -LiteralPath (Join-Path $testRoot 'bin\out') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$unitReceiptPath = Join-Path $unitRoot 'unit-test-receipt.json'
$publicReceiptPath = Join-Path $unitRoot 'public-contract-validation-receipt.json'
$identityRoot = (Get-ChildItem -LiteralPath (Join-Path $testRoot 'bin\identity') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$identityReceiptPath = Join-Path $identityRoot 'destination-neutral-identity-receipt.json'
foreach ($requiredReceipt in @($unitReceiptPath, $publicReceiptPath, $identityReceiptPath)) {
    if (-not (Test-Path -LiteralPath $requiredReceipt)) { throw "Missing prerequisite receipt: $requiredReceipt" }
}
$unitReceipt = Get-Content -Raw -LiteralPath $unitReceiptPath | ConvertFrom-Json
$requiredUnitTests = @(
    'reject old provider before synthesis'
    'synthesis publishes immutable audio beyond MAX_PATH through short tool workspace'
    'playback stages audio beyond MAX_PATH into a short player workspace'
    'replay starts a fresh session from the same audio'
    'playback natural completion resets idle replayable'
    'playback cancellation maps all public reasons'
)
if ($unitReceipt.status -ne 'passed' -or $unitReceipt.failures -ne 0) { throw 'Unit receipt is not passing' }
foreach ($requiredUnitTest in $requiredUnitTests) {
    $matchedTest = @($unitReceipt.tests | Where-Object name -eq $requiredUnitTest)
    if ($matchedTest.Count -ne 1 -or $matchedTest[0].status -ne 'passed') {
        throw "Unit receipt does not prove required behavior: $requiredUnitTest"
    }
}
$identityReceipt = Get-Content -Raw -LiteralPath $identityReceiptPath | ConvertFrom-Json
Assert-Equal 'identity candidate hash' $workerHash $identityReceipt.candidate.sha256.ToLowerInvariant()

$tempCwd = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
Push-Location -LiteralPath $tempCwd
try {
    & (Join-Path $testRoot 'run-live-playback-canary.ps1') -WorkerExe $publishedWorker -EdgeTtsPath $fakeTool -FfplayPath $fakeTool -CanaryMode synthetic
    if (-not $?) { throw 'published worker synthetic Canary failed' }
}
finally { Pop-Location }
$canaryRoot = (Get-ChildItem -LiteralPath (Join-Path $testRoot 'pc') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$canaryReceiptPath = Join-Path $canaryRoot 'playback-canary-receipt.json'
python (Join-Path $testRoot 'validate-live-playback-canary.py') --project-root $projectRoot --canary-root $canaryRoot
if ($LASTEXITCODE -ne 0) { throw "published worker public contract Canary validation failed with exit code $LASTEXITCODE" }
$canaryContractReceiptPath = Join-Path $canaryRoot 'playback-contract-validation-receipt.json'
$canaryReceipt = Get-Content -Raw -LiteralPath $canaryReceiptPath | ConvertFrom-Json
Assert-Equal 'Canary worker hash' $workerHash $canaryReceipt.workerExeSha256

$expectedStates = [ordered]@{
    'natural-completion' = 'idle_replayable'
    'user-stop' = 'stopped'
    'superseded' = 'superseded'
    'task-cancel' = 'cancelled'
    'timeout' = 'timed_out'
}
foreach ($caseName in $expectedStates.Keys) {
    $case = @($canaryReceipt.cases | Where-Object case -eq $caseName)
    if ($case.Count -ne 1) { throw "Missing Canary case: $caseName" }
    Assert-Equal "$caseName playback state" $expectedStates[$caseName] $case[0].playbackState
    if ($case[0].cacheHit -ne $false) { throw "$caseName playback result was incorrectly reused" }
}
$cwdChecks += [ordered]@{ cwd = $tempCwd; command = 'run --request <absolute-request.json>'; status = 'passed'; operations = @('speech.synthesize', 'speech.playback') }

$tree = @(
    Get-ChildItem -LiteralPath (Join-Path $candidateRoot 'workers') -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [Uri]::UnescapeDataString(([Uri]($candidateRoot.TrimEnd('\') + '\')).MakeRelativeUri([Uri]$_.FullName).ToString())
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)
if ($tree.Count -ne 1 -or $tree[0].path -ne 'workers/speech/KnowledgeClipper.Speech.Worker.exe') {
    throw 'Canonical workers/speech directory is not minimal'
}

$receiptPath = Join-Path $candidateRoot 'speech-canonical-release-receipt.json'
if (Test-Path -LiteralPath $receiptPath) { throw "Immutable release receipt already exists: $receiptPath" }
$receipt = [ordered]@{
    schemaVersion = 1
    receiptType = 'speech-canonical-release-v1'
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    productDisplayName = '零碎知识剪藏'
    workerId = $initialDescriptor.workerId
    workerVersion = $initialDescriptor.workerVersion
    contractVersion = $initialDescriptor.contractVersion
    canonicalPublishRoot = $candidateRoot
    qualityCopyRoot = $canonicalDirectory
    releaseTree = $tree
    descriptor = $initialDescriptor
    fileMetadata = [ordered]@{
        productName = $versionInfo.ProductName
        fileDescription = $versionInfo.FileDescription
        fileVersion = $versionInfo.FileVersion
        originalFilename = $versionInfo.OriginalFilename
    }
    arbitraryCwdChecks = $cwdChecks
    semantics = [ordered]@{
        naturalCompletion = 'idle_replayable'
        replay = 'fresh-playback-attempt-from-start'
        playbackResultReuse = $false
        cancellationReasons = @('user_stop', 'superseded', 'task_cancel', 'timeout')
        oldAiTtsRouteCount = 0
        externalProductionWrites = 0
        physicalSpeakerPlayback = $false
    }
    evidence = @(
        [ordered]@{ kind = 'unit-tests'; path = $unitReceiptPath; sha256 = (Get-FileHash -LiteralPath $unitReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        [ordered]@{ kind = 'public-contracts'; path = $publicReceiptPath; sha256 = (Get-FileHash -LiteralPath $publicReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        [ordered]@{ kind = 'destination-neutral-identity'; path = $identityReceiptPath; sha256 = (Get-FileHash -LiteralPath $identityReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        [ordered]@{ kind = 'synthetic-playback-canary'; path = $canaryReceiptPath; sha256 = (Get-FileHash -LiteralPath $canaryReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        [ordered]@{ kind = 'canary-public-contracts'; path = $canaryContractReceiptPath; sha256 = (Get-FileHash -LiteralPath $canaryContractReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    )
}
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
python (Join-Path $testRoot 'validate-canonical-release.py') --receipt $receiptPath
if ($LASTEXITCODE -ne 0) { throw "independent canonical release validation failed with exit code $LASTEXITCODE" }
Write-Output "PASS SPEECH CANONICAL RELEASE $candidateRoot"
Write-Output "QUALITY COPY ROOT $canonicalDirectory"
Write-Output "RECEIPT $receiptPath"
exit 0

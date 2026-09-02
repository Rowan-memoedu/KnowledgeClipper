param(
    [Parameter(Mandatory = $true)][string]$RootRemId,
    [string]$RootTitle = '零碎知识剪藏 Canary',
    [ValidateSet('created', 'already_applied')][string]$ExpectedFirstOutcome = 'created',
    [string]$BundlePath,
    [string]$JobIdentity = 'authority-language-both',
    [string]$SourceLabel = 'content-0.5-authority-replay',
    [string]$DocumentTitlePrefix,
    [ValidatePattern('^[0-9a-f]{64}$')][string]$IdempotencyKey = ('9' * 64)
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
$worker = Join-Path $projectRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$authorityRoot = Join-Path $projectRoot 'content-worker\canary\jobs\20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e'
$authorityProjectedBundle = Join-Path $projectRoot 'canary\rwa\260826041745\jobs\tlw-0\runs\rlw-0\adapters\export-remnote\remnote-card-bundle-a3e20618c78d4ee9106fe702\v0001\bundle.json'
$isAuthoritySource = [string]::IsNullOrWhiteSpace($BundlePath)
$projectedBundle = if ($isAuthoritySource) { $authorityProjectedBundle } else { (Resolve-Path -LiteralPath $BundlePath).Path }
$bundleObject = Get-Content -LiteralPath $projectedBundle -Raw | ConvertFrom-Json
$bundleKind = switch ($bundleObject.schemaVersion) {
    'card-bundle.v1' { 'card.bundle' }
    'learning-bundle.v1' { 'learning.bundle' }
    default { throw "Unsupported stable bundle schemaVersion: $($bundleObject.schemaVersion)" }
}
$contentAttempt = Join-Path $authorityRoot 'runs\run-200a433f248b2eab5eba\stages\content.understand-and-card\attempts\attempt-200a433f248b2eab5eba\outputs'
$sourceCardBundle = Join-Path $contentAttempt 'card-bundle.schema-v1.json'
$sourceLearningBundle = Join-Path $contentAttempt 'learning-bundle.schema-v1.json'
$authorityReceipt = Join-Path $authorityRoot 'canary-receipt.json'
$stamp = (Get-Date).ToUniversalTime().ToString('yyMMddHHmmss')
$canaryRoot = Join-Path $projectRoot (Join-Path 'canary\rw-live-isolated' $stamp)
$jobRoot = Join-Path $canaryRoot (Join-Path 'jobs' $JobIdentity)
$taskId = $JobIdentity
if ([string]::IsNullOrWhiteSpace($DocumentTitlePrefix)) { $DocumentTitlePrefix = $RootTitle }

function Write-JsonOnce([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable file exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40 -Compress), [Text.UTF8Encoding]::new($false))
}

function Write-BytesOnce([string]$Path, [byte[]]$Bytes) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable file exists: $Path" }
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Hash-File([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Relative-Path([string]$Path) {
    $Path.Substring($projectRoot.Length + 1).Replace('\', '/')
}

function New-BundleArtifact {
    $payloadRelative = 'inputs/bundle/v0001/payload.json'
    $payloadPath = Join-Path $jobRoot $payloadRelative
    Write-BytesOnce $payloadPath ([IO.File]::ReadAllBytes($projectedBundle))
    $manifestRelative = 'inputs/bundle/v0001/artifact.json'
    $manifestPath = Join-Path $jobRoot $manifestRelative
    $sourceMetadata = [ordered]@{
        sourceLabel = $SourceLabel
        projectedBundleSha256 = Hash-File $projectedBundle
    }
    if ($isAuthoritySource) {
        $sourceMetadata.authorityReceiptSha256 = Hash-File $authorityReceipt
        $sourceMetadata.sourceCardBundleSha256 = Hash-File $sourceCardBundle
        $sourceMetadata.sourceLearningBundleSha256 = Hash-File $sourceLearningBundle
    }
    $artifactId = "$JobIdentity-bundle"
    Write-JsonOnce $manifestPath ([ordered]@{
        schemaVersion = '1.0.0'
        artifactId = $artifactId
        kind = $bundleKind
        contractVersion = '1.0.0'
        mediaType = 'application/json'
        files = @([ordered]@{
            path = $payloadRelative
            sha256 = (Hash-File $payloadPath).ToLowerInvariant()
            bytes = (Get-Item -LiteralPath $payloadPath).Length
            mediaType = 'application/json'
        })
        createdAt = (Get-Date).ToUniversalTime().ToString('o')
        producer = [ordered]@{
            stageId = $(if ($isAuthoritySource) { 'core.content-to-remnote' } else { 'fixture' })
            workerId = $(if ($isAuthoritySource) { 'orchestrator.core-adapter' } else { 'fixture.builder' })
            workerVersion = $(if ($isAuthoritySource) { '2.1.0' } else { '1.0.0' })
            attemptId = "$JobIdentity-input"
        }
        inputRefs = @()
        dataClass = 'personal'
        metadata = $sourceMetadata
    })
    return [ordered]@{
        artifactRef = $artifactId
        kind = $bundleKind
        contractVersion = '1.0.0'
        manifestPath = $manifestRelative
        expectedSha256 = (Hash-File $manifestPath).ToLowerInvariant()
    }
}

function New-LiveRequest([string]$RunId, [string]$AttemptId, [object]$InputRef) {
    $attemptRelative = "runs/$RunId/stages/export-remnote/attempts/$AttemptId"
    $requestPath = Join-Path $jobRoot "$attemptRelative/request.json"
    Write-JsonOnce $requestPath ([ordered]@{
        schemaVersion = '1.0.0'
        requestId = "request-$AttemptId"
        operationId = "operation-$AttemptId"
        taskId = $taskId
        runId = $RunId
        stageId = 'export-remnote'
        attemptId = $AttemptId
        workerId = 'export.remnote'
        operation = 'remnote.export'
        idempotencyKey = $IdempotencyKey
        attemptPath = $attemptRelative
        outputPath = "$attemptRelative/outputs"
        progressPath = "$attemptRelative/progress.ndjson"
        resultPath = "$attemptRelative/result.json"
        cancelSignal = [ordered]@{
            schemaVersion = '1.0.0'
            path = "$attemptRelative/cancel.json"
            gracePeriodMs = 2000
        }
        inputRefs = @($InputRef)
        configSnapshot = @{}
        payload = [ordered]@{
            schemaVersion = '1.0.0'
            documentTitle = "$DocumentTitlePrefix · $taskId"
            sink = 'mcp-direct'
            target = [ordered]@{
                rootRemId = $RootRemId
                rootTitle = $RootTitle
            }
        }
    })
    return $requestPath
}

if (-not (Test-Path -LiteralPath $worker)) { throw "Worker not built: $worker" }
$requiredInputs = @($projectedBundle)
if ($isAuthoritySource) { $requiredInputs += @($sourceCardBundle, $sourceLearningBundle, $authorityReceipt) }
foreach ($required in $requiredInputs) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Authority input missing: $required" }
}

New-Item -ItemType Directory -Force -Path $canaryRoot | Out-Null
$describePath = Join-Path $canaryRoot 'describe.json'
& $worker describe --json | Set-Content -LiteralPath $describePath -Encoding utf8NoBOM
if ($LASTEXITCODE -ne 0) { throw 'describe failed' }

$inputRef = New-BundleArtifact
$firstRequest = New-LiveRequest 'run-1' 'attempt-1' $inputRef
& $worker run --request $firstRequest
if ($LASTEXITCODE -ne 0) { throw 'first live run failed or became uncertain; inspect result.json and do not retry automatically' }
$firstResultPath = Join-Path (Split-Path $firstRequest) 'result.json'
$firstResult = Get-Content -Raw -LiteralPath $firstResultPath | ConvertFrom-Json
if ($firstResult.status -ne 'completed' -or $firstResult.payload.outcome -ne $ExpectedFirstOutcome -or -not $firstResult.payload.readbackVerified) {
    throw "first live run was not a readback-verified $ExpectedFirstOutcome"
}

$secondRequest = New-LiveRequest 'run-2' 'attempt-2' $inputRef
& $worker run --request $secondRequest
if ($LASTEXITCODE -ne 0) { throw 'second live run failed or became uncertain; inspect result.json and do not retry automatically' }
$secondResultPath = Join-Path (Split-Path $secondRequest) 'result.json'
$secondResult = Get-Content -Raw -LiteralPath $secondResultPath | ConvertFrom-Json
if ($secondResult.status -ne 'completed' -or $secondResult.payload.outcome -ne 'already_applied' -or -not $secondResult.payload.readbackVerified -or $secondResult.payload.documentRemId -ne $firstResult.payload.documentRemId) {
    throw 'second run did not prove readback-verified idempotent reuse'
}

python (Join-Path $testRoot 'validate-public-contracts.py') --project-root $projectRoot --canary-root $canaryRoot --describe $describePath --expected-result-count 2 --expected-statuses completed --suite remnote-exporter-isolated-authority-live-canary
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$sourceEvidence = [ordered]@{
    label = $SourceLabel
    bundle = [ordered]@{ path = Relative-Path $projectedBundle; sha256 = Hash-File $projectedBundle; kind = $bundleKind }
}
if ($isAuthoritySource) {
    $sourceEvidence.contentReceipt = [ordered]@{ path = Relative-Path $authorityReceipt; sha256 = Hash-File $authorityReceipt }
    $sourceEvidence.sourceLearningBundle = [ordered]@{ path = Relative-Path $sourceLearningBundle; sha256 = Hash-File $sourceLearningBundle }
    $sourceEvidence.sourceCardBundle = [ordered]@{ path = Relative-Path $sourceCardBundle; sha256 = Hash-File $sourceCardBundle }
}
$receiptPath = Join-Path $canaryRoot 'worker-live-receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = '1.0.0'
    suite = 'remnote-exporter-isolated-live-idempotency'
    status = 'passed'
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    target = [ordered]@{ rootRemId = $RootRemId; rootTitle = $RootTitle }
    worker = [ordered]@{ path = Relative-Path $worker; sha256 = Hash-File $worker }
    source = $sourceEvidence
    observed = [ordered]@{
        documentRemId = $firstResult.payload.documentRemId
        outcomes = @($firstResult.payload.outcome, $secondResult.payload.outcome)
        readbackVerified = @($firstResult.payload.readbackVerified, $secondResult.payload.readbackVerified)
    }
    results = @((Relative-Path $firstResultPath), (Relative-Path $secondResultPath))
})

Write-Host "PASS isolated RemNote live Canary: $receiptPath"
Write-Host "SHA256 $(Hash-File $receiptPath)"

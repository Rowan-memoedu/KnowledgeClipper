[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$AuthorityReleaseRoot,
    [Parameter(Mandatory = $true)][string]$CanaryRoot,
    [Parameter(Mandatory = $true)][string]$ExpectedIsolatedRootRemId,
    [Parameter(Mandatory = $true)][string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$AuthorityReleaseRoot = [IO.Path]::GetFullPath($AuthorityReleaseRoot)
$CanaryRoot = [IO.Path]::GetFullPath($CanaryRoot)
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
if (Test-Path -LiteralPath $ReceiptPath) { throw "Append-only receipt already exists: $ReceiptPath" }

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-FileMap([string]$Root) {
    $map = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $map[$relative] = [ordered]@{ bytes = $file.Length; sha256 = Get-Sha256 $file.FullName }
    }
    $map
}

function Assert-ArtifactReference([string]$JobRoot, [object]$Reference) {
    $manifestPath = [IO.Path]::GetFullPath((Join-Path $JobRoot ($Reference.manifestPath.Replace('/', '\'))))
    $jobPrefix = [IO.Path]::GetFullPath($JobRoot).TrimEnd('\') + '\'
    if (-not $manifestPath.StartsWith($jobPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact manifest escaped the job root: $($Reference.manifestPath)"
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Artifact manifest is missing: $manifestPath" }
    if ((Get-Sha256 $manifestPath) -ne $Reference.expectedSha256) { throw "Artifact manifest hash mismatch: $($Reference.artifactRef)" }
}

$checkpointPath = Join-Path $AuthorityDirectory 'checkpoint.json'
$registryPath = Join-Path $AuthorityDirectory 'worker-registry.v1.json'
$dependencyPath = Join-Path $AuthorityDirectory 'dependency-manifest.v1.json'
$resultPath = Join-Path $CanaryRoot 'result.remnote.json'
$idempotencyPath = Join-Path $CanaryRoot 'result.remnote-idempotency.json'
$readablePath = Join-Path $CanaryRoot 'readable-output.remnote.md'
foreach ($required in @($checkpointPath, $registryPath, $dependencyPath, $resultPath, $idempotencyPath, $readablePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Qualification input is missing: $required" }
}

$checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
$idempotency = Get-Content -LiteralPath $idempotencyPath -Raw | ConvertFrom-Json
if ($checkpoint.admissionStatus -ne 'authority-admitted' -or $checkpoint.validation.status -ne 'valid' -or $checkpoint.validation.workers -ne 5) {
    throw 'Authority checkpoint is not admitted.'
}
if ($result.status -ne 'succeeded' -or $result.remnote.readbackVerified -ne $true -or $result.remnote.isolatedParentRemId -ne $ExpectedIsolatedRootRemId) {
    throw 'RemNote Canary did not succeed within the expected isolated root.'
}
if ($idempotency.status -ne 'completed' -or $idempotency.outcome -ne 'already_applied' -or
    $idempotency.documentRemId -ne $result.remnote.documentRemId -or $idempotency.readbackVerified -ne $true) {
    throw 'RemNote idempotency replay evidence is invalid.'
}

$canaryRegistry = Get-Content -LiteralPath $result.registry.path -Raw | ConvertFrom-Json
if (($canaryRegistry.profiles | ConvertTo-Json -Compress) -ne ($registry.profiles | ConvertTo-Json -Compress) -or
    ($canaryRegistry.workers | ConvertTo-Json -Depth 16 -Compress) -ne ($registry.workers | ConvertTo-Json -Depth 16 -Compress)) {
    throw 'Canary and authority worker registry sets differ.'
}

$authorityFiles = Get-FileMap $AuthorityReleaseRoot
$canaryReleaseRoot = [IO.Path]::GetFullPath($result.releaseRoot)
$canaryFiles = Get-FileMap $canaryReleaseRoot
$releaseDifferences = @(
    @($authorityFiles.Keys + $canaryFiles.Keys) | Sort-Object -Unique | Where-Object {
        -not $authorityFiles.ContainsKey($_) -or -not $canaryFiles.ContainsKey($_) -or
        $authorityFiles[$_].bytes -ne $canaryFiles[$_].bytes -or $authorityFiles[$_].sha256 -ne $canaryFiles[$_].sha256
    })
if ($releaseDifferences.Count -ne 0) { throw "Canary release differs from authority release: $($releaseDifferences -join ', ')" }

$jobRoot = Join-Path $CanaryRoot ("jobs\$($result.taskId)")
Assert-ArtifactReference $jobRoot $result.input.captureArtifact
foreach ($output in $result.outputs) { Assert-ArtifactReference $jobRoot $output }

$learningPath = @(Get-ChildItem -LiteralPath $jobRoot -File -Recurse -Filter 'learning-bundle.schema-v1.json')
$cardPath = @(Get-ChildItem -LiteralPath $jobRoot -File -Recurse -Filter 'card-bundle.schema-v1.json')
$remNoteReceiptPath = @(Get-ChildItem -LiteralPath $jobRoot -File -Recurse -Filter 'receipt.json' | Where-Object FullName -Like '*run-001*remnote-write-receipt*')
if ($learningPath.Count -ne 1 -or $cardPath.Count -ne 1 -or $remNoteReceiptPath.Count -ne 1) {
    throw 'Expected exactly one learning bundle, card bundle, and initial RemNote receipt.'
}
$learning = Get-Content -LiteralPath $learningPath[0].FullName -Raw | ConvertFrom-Json
$cards = Get-Content -LiteralPath $cardPath[0].FullName -Raw | ConvertFrom-Json
$remNoteReceipt = Get-Content -LiteralPath $remNoteReceiptPath[0].FullName -Raw | ConvertFrom-Json
if ($remNoteReceipt.rootRemId -ne $ExpectedIsolatedRootRemId -or $remNoteReceipt.documentRemId -ne $result.remnote.documentRemId -or
    $remNoteReceipt.outcome -ne 'created' -or $remNoteReceipt.readbackVerified -ne $true -or $remNoteReceipt.dateRouting -ne $false) {
    throw 'Initial RemNote write receipt is invalid.'
}

$files = @(Get-ChildItem -LiteralPath $CanaryRoot -File -Recurse)
$longPaths = @($files | ForEach-Object { $_.FullName.Length } | Where-Object { $_ -gt 260 } | Sort-Object)
if ($files.Count -ne 43 -or $longPaths.Count -ne 24 -or $longPaths[0] -ne 304 -or $longPaths[-1] -ne 361) {
    throw "Long-path matrix drift: files=$($files.Count), long=$($longPaths.Count), min=$($longPaths[0]), max=$($longPaths[-1])"
}

$cleaningTrace = $learning.ai_trace | Where-Object stage -eq 'cleaning' | Select-Object -First 1
$classificationTrace = $learning.ai_trace | Where-Object stage -eq 'classification' | Select-Object -First 1
$card = $cards.cards | Select-Object -First 1
$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-e2e-qualification-receipt.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    authority = [ordered]@{
        checkpoint = [ordered]@{ path = $checkpointPath; sha256 = Get-Sha256 $checkpointPath }
        registry = [ordered]@{ version = $registry.registryVersion; path = $registryPath; sha256 = Get-Sha256 $registryPath }
        dependencyManifest = [ordered]@{ path = $dependencyPath; sha256 = Get-Sha256 $dependencyPath }
        releaseRoot = $AuthorityReleaseRoot
        releaseFiles = $authorityFiles.Count
        canaryReleaseDifferences = 0
    }
    pipeline = [ordered]@{
        originalText = $result.input.text
        cleanedText = $cleaningTrace.response.cleaned_text
        classification = [ordered]@{
            categoryId = $classificationTrace.response.units[0].category_id
            template = $classificationTrace.response.units[0].subtype
        }
        card = [ordered]@{
            cardId = $card.card_id
            front = $card.front_blocks[0].text
            back = $card.back_blocks[0].text
        }
        attempts = @($result.attempts | ForEach-Object { [ordered]@{ stageId = $_.stageId; workerId = $_.workerId; workerVersion = $_.workerVersion; attemptId = $_.attemptId; status = $_.status } })
        invocationCounts = $result.invocationCounts
    }
    remnote = [ordered]@{
        isolatedRootRemId = $remNoteReceipt.rootRemId
        documentRemId = $remNoteReceipt.documentRemId
        documentTitle = $remNoteReceipt.documentTitle
        initialOutcome = $remNoteReceipt.outcome
        readbackVerified = $remNoteReceipt.readbackVerified
        replayOutcome = $idempotency.outcome
        replayDocumentRemId = $idempotency.documentRemId
        deletedOrCleaned = $false
    }
    longPath = [ordered]@{ files = $files.Count; over260 = $longPaths.Count; shortest = $longPaths[0]; longest = $longPaths[-1] }
    evidence = [ordered]@{
        result = [ordered]@{ path = $resultPath; sha256 = Get-Sha256 $resultPath }
        idempotency = [ordered]@{ path = $idempotencyPath; sha256 = Get-Sha256 $idempotencyPath }
        readableOutput = [ordered]@{ path = $readablePath; sha256 = Get-Sha256 $readablePath }
        learningBundle = [ordered]@{ path = $learningPath[0].FullName; sha256 = Get-Sha256 $learningPath[0].FullName }
        cardBundle = [ordered]@{ path = $cardPath[0].FullName; sha256 = Get-Sha256 $cardPath[0].FullName }
        remNoteWriteReceipt = [ordered]@{ path = $remNoteReceiptPath[0].FullName; sha256 = Get-Sha256 $remNoteReceiptPath[0].FullName }
    }
}

$json = $receipt | ConvertTo-Json -Depth 24
$receiptDirectory = Split-Path -Parent $ReceiptPath
[IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
$temporary = Join-Path $receiptDirectory ('.' + [IO.Path]::GetFileName($ReceiptPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $ReceiptPath)
}
finally {
    if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
}
$json

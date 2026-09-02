param(
    [switch]$PrepareOnly,
    [ValidateSet('ocr','original')]
    [string]$ImageHandling = 'ocr',
    [string]$SourceImage,
    [string]$SourceReceipt
)

$ErrorActionPreference = 'Stop'
$workerRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $workerRoot '..\..')).Path
$worker = (Resolve-Path (Join-Path $workerRoot 'candidates\1.2.4\KnowledgeClipper.ImageOcr.Worker.exe')).Path
$mineruScript = (Resolve-Path (Join-Path $projectRoot '..\MinerU OCR Integration\mineru_ocr.py')).Path
$importHistoricalSource = -not [string]::IsNullOrWhiteSpace($SourceImage)
if ($importHistoricalSource -ne (-not [string]::IsNullOrWhiteSpace($SourceReceipt))) {
    throw 'SourceImage and SourceReceipt must be supplied together'
}
if ($importHistoricalSource -and $ImageHandling -ne 'ocr') {
    throw 'Historical source import is only valid for OCR mode'
}
$resolvedSourceImage = $null
$resolvedSourceReceipt = $null
$sourceReceiptValue = $null
$sourceGroundTruth = $null
if ($importHistoricalSource) {
    $resolvedSourceImage = (Resolve-Path -LiteralPath $SourceImage).Path
    $resolvedSourceReceipt = (Resolve-Path -LiteralPath $SourceReceipt).Path
    $sourceReceiptValue = Get-Content -LiteralPath $resolvedSourceReceipt -Raw | ConvertFrom-Json -Depth 50
    $sourceGroundTruth = @($sourceReceiptValue.input.readableText | ForEach-Object { [string]$_ }) -join ' '
    if ([string]::IsNullOrWhiteSpace($sourceGroundTruth)) { throw 'Source receipt has no readable input ground truth' }
}
$canaryPrefix = if ($importHistoricalSource) { 'image-v124-historical-source-ocr' } else { 'image-v124-core-' + $ImageHandling }
$canaryId = $canaryPrefix + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$canaryRoot = Join-Path $projectRoot ('canary\' + $canaryId)
$taskId = 'task-' + $canaryId
$package = Join-Path $canaryRoot ('jobs\' + $taskId)
$inputs = Join-Path $package 'inputs'
$assets = Join-Path $package 'assets\original'
New-Item -ItemType Directory -Path $inputs,$assets -ErrorAction Stop | Out-Null

function Write-JsonNew([string]$Path, [object]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Refusing to overwrite $Path" }
    $json = $Value | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function New-CanaryImage([string]$Path, [string[]]$Lines) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new(1400, 360)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $font = [Drawing.Font]::new('Arial', 42, [Drawing.FontStyle]::Bold)
        try {
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                $graphics.DrawString($Lines[$index], $font, [Drawing.Brushes]::Black, 35, 35 + $index * 105)
            }
        }
        finally { $font.Dispose() }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Artifact([string]$Ref, [string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        artifactRef = $Ref
        relativePath = [IO.Path]::GetRelativePath($package, $item.FullName).Replace('\','/')
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $item.Length
        mediaType = 'image/png'
    }
}

function File-Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = [IO.Path]::GetRelativePath($canaryRoot, $item.FullName).Replace('\','/')
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$questionSources = @()
$answerSources = @()
$sourcePaths = @()
if ($importHistoricalSource) {
    $questionPath1 = Join-Path $assets 'question-001.png'
    Copy-Item -LiteralPath $resolvedSourceImage -Destination $questionPath1 -ErrorAction Stop
    $questionSources = @(Artifact 'capture.image.question.0001' $questionPath1)
    $sourcePaths = @($questionPath1)
}
else {
    $questionPath1 = Join-Path $assets 'question-001.png'
    $questionPath2 = Join-Path $assets 'question-002.png'
    $answerPath1 = Join-Path $assets 'answer-001.png'
    $answerPath2 = Join-Path $assets 'answer-002.png'
    New-CanaryImage $questionPath1 @('KNOWLEDGECLIPPER SYNTHETIC CANARY', 'QUESTION 1 OF 2: COMPUTE 12 + 30')
    New-CanaryImage $questionPath2 @('KNOWLEDGECLIPPER SYNTHETIC CANARY', 'QUESTION 2 OF 2: NAME A THREE-SIDED SHAPE')
    New-CanaryImage $answerPath1 @('KNOWLEDGECLIPPER SYNTHETIC CANARY', 'ANSWER 1 OF 2: 12 + 30 = 42')
    New-CanaryImage $answerPath2 @('KNOWLEDGECLIPPER SYNTHETIC CANARY', 'ANSWER 2 OF 2: TRIANGLE')
    $questionSources = @(
        (Artifact 'capture.image.question.0001' $questionPath1),
        (Artifact 'capture.image.question.0002' $questionPath2)
    )
    $answerSources = @(
        (Artifact 'capture.image.answer.0001' $answerPath1),
        (Artifact 'capture.image.answer.0002' $answerPath2)
    )
    $sourcePaths = @($questionPath1,$questionPath2,$answerPath1,$answerPath2)
}
$allSources = @($questionSources) + @($answerSources)
$captureArtifacts = if ($importHistoricalSource) { @($allSources) } else { @($answerSources[1],$questionSources[1],$answerSources[0],$questionSources[0]) }

$capture = [ordered]@{
    schemaVersion = 'capture-bundle.v1'
    captureId = $canaryId + '-capture'
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    intent = 'screenshot'
    source = 'screenshot'
    modeSnapshot = [ordered]@{
        workflow = 'card'
        contextEnabled = $false
        contextGranularity = 'sentence'
        contextApplied = $false
        imageHandling = $ImageHandling
    }
    # Deliberately differs from role order: the adapter must use the explicit arrays below.
    artifacts = @($captureArtifacts)
    selectedTextArtifactRef = $null
    contextSegments = $null
    questionImages = @($questionSources | ForEach-Object artifactRef)
    answerImages = @($answerSources | ForEach-Object artifactRef)
    questionTurns = @()
}
$capturePath = Join-Path $inputs 'capture-bundle.v1.json'
Write-JsonNew $capturePath $capture
$captureItem = Get-Item -LiteralPath $capturePath
$captureArtifactId = 'capture.bundle.' + $canaryId
$captureManifestPath = Join-Path $package ('artifacts\' + $captureArtifactId + '\artifact.json')
New-Item -ItemType Directory -Path (Split-Path -Parent $captureManifestPath) -ErrorAction Stop | Out-Null
$captureRelative = [IO.Path]::GetRelativePath($package, $capturePath).Replace('\','/')
$captureManifest = [ordered]@{
    schemaVersion = '1.0.0'
    artifactId = $captureArtifactId
    kind = 'capture-bundle'
    contractVersion = '1.0.0'
    mediaType = 'application/vnd.rqc.capture-bundle+json'
    files = @([ordered]@{
        path = $captureRelative
        sha256 = (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $captureItem.Length
        mediaType = 'application/vnd.rqc.capture-bundle+json'
    })
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    producer = [ordered]@{
        stageId = 'capture'
        workerId = 'desktop-capture'
        workerVersion = '1.0.0'
        attemptId = 'capture-attempt-0001'
    }
    inputRefs = @()
    dataClass = 'sensitive'
    metadata = [ordered]@{ entrypoint = $captureRelative }
}
Write-JsonNew $captureManifestPath $captureManifest
$captureRef = [ordered]@{
    artifactRef = $captureArtifactId
    kind = 'capture-bundle'
    contractVersion = '1.0.0'
    manifestPath = [IO.Path]::GetRelativePath($package, $captureManifestPath).Replace('\','/')
    expectedSha256 = (Get-FileHash -LiteralPath $captureManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$runId = 'run-' + $canaryId
$attemptId = 'attempt-0001'
$attemptRelative = 'runs/' + $runId + '/stages/image-ocr/attempts/' + $attemptId
$attemptRoot = Join-Path $package ($attemptRelative.Replace('/', '\'))
New-Item -ItemType Directory -Path $attemptRoot -ErrorAction Stop | Out-Null
$operationId = $canaryId + '-operation'
$idempotencyText = [Text.Encoding]::UTF8.GetBytes($operationId + ':' + $attemptId)
$idempotencyHash = [Security.Cryptography.SHA256]::HashData($idempotencyText)
$idempotencyKey = [Convert]::ToHexString($idempotencyHash).ToLowerInvariant()
$request = [ordered]@{
    schemaVersion = '1.0.0'
    requestId = $canaryId + '-request'
    operationId = $operationId
    taskId = $taskId
    runId = $runId
    stageId = 'image-ocr'
    attemptId = $attemptId
    workerId = 'image.ocr.mineru'
    operation = 'process-images'
    idempotencyKey = $idempotencyKey
    attemptPath = $attemptRelative
    outputPath = $attemptRelative + '/outputs'
    progressPath = $attemptRelative + '/progress.ndjson'
    resultPath = $attemptRelative + '/result.json'
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = $attemptRelative + '/cancel.json'; gracePeriodMs = 2000 }
    inputRefs = @($captureRef)
    configSnapshot = [ordered]@{
        mineruScriptPath = $mineruScript
        providerSettings = [ordered]@{
            modelVersion = 'vlm'
            language = 'ch'
            enableTable = $true
            enableFormula = $true
            timeoutSeconds = 900
            pollIntervalSeconds = 5
        }
    }
    payload = [ordered]@{}
}
$requestPath = Join-Path $attemptRoot 'request.json'
Write-JsonNew $requestPath $request

if ($PrepareOnly) {
    $preparationReceipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-image-ocr-live-canary-preparation.v1'
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        status = 'prepared'
        productDisplayName = '零碎知识剪藏'
        candidateEntrypoint = 'workers/image-ocr/candidates/1.2.4/KnowledgeClipper.ImageOcr.Worker.exe'
        workerId = 'image.ocr.mineru'
        canaryId = $canaryId
        imageHandling = $ImageHandling
        jobRoot = [IO.Path]::GetRelativePath($projectRoot, $package).Replace('\','/')
        requestPath = [IO.Path]::GetRelativePath($projectRoot, $requestPath).Replace('\','/')
        providerCalled = $false
        credentialRead = $false
    }
    $preparationReceiptPath = Join-Path $canaryRoot 'preparation-receipt.json'
    Write-JsonNew $preparationReceiptPath $preparationReceipt
    [pscustomobject]@{
        CanaryId = $canaryId
        Status = 'prepared'
        Request = $requestPath
        Receipt = $preparationReceiptPath
    } | Format-List
    exit 0
}

$token = [Environment]::GetEnvironmentVariable('MINERU_API_TOKEN', 'Process')
if ($ImageHandling -eq 'ocr' -and ([string]::IsNullOrWhiteSpace($token) -or $token.Length -lt 20)) {
    throw 'MINERU_API_TOKEN must be supplied by the caller process for OCR mode'
}
$stdoutPath = Join-Path $attemptRoot 'worker.stdout.ndjson'
$stderrPath = Join-Path $attemptRoot 'worker.stderr.txt'
$oldToken = [Environment]::GetEnvironmentVariable('MINERU_API_TOKEN', 'Process')
$oldNewScript = [Environment]::GetEnvironmentVariable('KNOWLEDGE_CLIPPER_MINERU_SCRIPT', 'Process')
$oldLegacyScript = [Environment]::GetEnvironmentVariable('RQC_MINERU_SCRIPT', 'Process')
$proxyNames = @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','http_proxy','https_proxy','all_proxy')
$oldProxyValues = @{}
try {
    foreach ($name in $proxyNames) {
        $oldProxyValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    if ($ImageHandling -eq 'original') {
        [Environment]::SetEnvironmentVariable('MINERU_API_TOKEN', $null, 'Process')
        [Environment]::SetEnvironmentVariable('KNOWLEDGE_CLIPPER_MINERU_SCRIPT', $null, 'Process')
        [Environment]::SetEnvironmentVariable('RQC_MINERU_SCRIPT', $null, 'Process')
    }
    & $worker run --request $requestPath 1> $stdoutPath 2> $stderrPath
    $workerExit = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable('MINERU_API_TOKEN', $oldToken, 'Process')
    [Environment]::SetEnvironmentVariable('KNOWLEDGE_CLIPPER_MINERU_SCRIPT', $oldNewScript, 'Process')
    [Environment]::SetEnvironmentVariable('RQC_MINERU_SCRIPT', $oldLegacyScript, 'Process')
    foreach ($name in $proxyNames) { [Environment]::SetEnvironmentVariable($name, $oldProxyValues[$name], 'Process') }
}

$resultPath = Join-Path $attemptRoot 'result.json'
if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Image/OCR worker produced no result.json' }
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
$moduleResultPath = Join-Path $attemptRoot 'module-result.json'
if (-not (Test-Path -LiteralPath $moduleResultPath)) { throw 'Image/OCR worker produced no module-result.json' }
$moduleResult = Get-Content -LiteralPath $moduleResultPath -Raw | ConvertFrom-Json
$orderedRefs = @($result.itemDiagnostics | ForEach-Object itemId)
$expectedRefs = @($allSources | ForEach-Object artifactRef)
$orderPassed = (($orderedRefs -join '|') -eq ($expectedRefs -join '|'))
$markerChecks = @()
$observedMarkdownTexts = @()
if ($ImageHandling -eq 'ocr') {
    foreach ($item in $moduleResult.details.items) {
        if ($item.status -ne 'completed') { $markerChecks += $false; continue }
        $markdownRef = $item.ocr.markdownArtifactRef
        $descriptor = $moduleResult.outputs | Where-Object artifactRef -eq $markdownRef | Select-Object -First 1
        if ($null -eq $descriptor) { $markerChecks += $false; continue }
        $markdown = Get-Content -LiteralPath (Join-Path $package $descriptor.path) -Raw
        $observedMarkdownTexts += $markdown.Trim()
        $markerChecks += ($markdown -match '(?i)KNOWLEDGECLIPPER|QUESTION|ANSWER|12\s*\+\s*30|TRIANGLE')
    }
}
$markersPassed = $ImageHandling -eq 'original' -or ($markerChecks.Count -eq $expectedRefs.Count -and -not ($markerChecks -contains $false))
$sourceGroundTruthMismatch = $false
if ($importHistoricalSource -and $ImageHandling -eq 'ocr') {
    $expectedNormalized = (($sourceGroundTruth -replace '\s+', ' ').Trim()).ToUpperInvariant()
    $observedNormalized = ((($observedMarkdownTexts -join ' ') -replace '\s+', ' ').Trim()).ToUpperInvariant()
    $sourceGroundTruthMismatch = $observedNormalized -ne $expectedNormalized
}
$humanReviewRecommended = $ImageHandling -eq 'ocr' -and ((-not $markersPassed) -or $sourceGroundTruthMismatch)

$attemptSubmissionPath = Join-Path $attemptRoot 'submission.json'
$attemptResumePath = Join-Path $attemptRoot 'resume.json'
$attemptRawPath = Join-Path $attemptRoot 'raw\mineru'
$providerCalled = (Test-Path -LiteralPath $attemptSubmissionPath) -or (Test-Path -LiteralPath $attemptResumePath) -or (Test-Path -LiteralPath $attemptRawPath)
$sourceByRef = @{}
foreach ($source in $allSources) { $sourceByRef[$source.artifactRef] = $source }
$moduleOriginals = @($moduleResult.outputs | Where-Object origin -eq 'input')
$commonOriginals = @($result.outputs | Where-Object kind -eq 'image.original')
$originalHashesPassed = $moduleOriginals.Count -eq $allSources.Count -and $commonOriginals.Count -eq $allSources.Count
foreach ($output in $moduleOriginals) {
    if (-not $sourceByRef.ContainsKey($output.artifactRef) -or $sourceByRef[$output.artifactRef].sha256 -ne $output.sha256 -or $sourceByRef[$output.artifactRef].bytes -ne $output.bytes -or $sourceByRef[$output.artifactRef].mediaType -ne $output.mediaType) {
        $originalHashesPassed = $false
    }
}
$provenanceDescriptor = $moduleResult.outputs | Where-Object artifactRef -eq 'image.processing.provenance' | Select-Object -First 1
$provenanceBindingPassed = $null -ne $provenanceDescriptor
$provenance = $null
if ($null -ne $provenanceDescriptor) {
    $provenance = Get-Content -LiteralPath (Join-Path $package $provenanceDescriptor.path) -Raw | ConvertFrom-Json
    if (($provenance.sourceOrder.questionImages -join '|') -ne ($capture.questionImages -join '|') -or ($provenance.sourceOrder.answerImages -join '|') -ne ($capture.answerImages -join '|')) { $provenanceBindingPassed = $false }
    foreach ($item in $provenance.items) {
        if (-not $sourceByRef.ContainsKey($item.artifactRef)) { $provenanceBindingPassed = $false; continue }
        $source = $sourceByRef[$item.artifactRef]
        $original = $item.representations.original
        if ($original.artifactRef -ne $source.artifactRef -or $original.sha256 -ne $source.sha256 -or $original.bytes -ne $source.bytes -or $original.mediaType -ne $source.mediaType -or $item.role -ne $sourceByRef[$item.artifactRef].artifactRef.Split('.')[2]) { $provenanceBindingPassed = $false }
    }
}
$submissionCount = @(Get-ChildItem -LiteralPath $attemptRoot -File -Recurse -Filter 'submission.json').Count
$providerCalls = [int]$moduleResult.details.providerCalls

$readablePath = $null
$handoffPath = $null
if ($ImageHandling -eq 'ocr' -and $result.status -eq 'completed') {
    $readablePath = Join-Path $canaryRoot 'readable-ocr.md'
    $readable = [Text.StringBuilder]::new()
    [void]$readable.AppendLine('# Synthetic image OCR Canary')
    [void]$readable.AppendLine()
    $handoffItems = @()
    foreach ($item in $moduleResult.details.items) {
        $markdownDescriptor = $moduleResult.outputs | Where-Object artifactRef -eq $item.ocr.markdownArtifactRef | Select-Object -First 1
        $structuredDescriptor = $moduleResult.outputs | Where-Object artifactRef -eq $item.ocr.structuredArtifactRef | Select-Object -First 1
        $markdownPath = Join-Path $package $markdownDescriptor.path
        $markdown = Get-Content -LiteralPath $markdownPath -Raw
        $roleLabel = if ($item.role -eq 'question') { 'Question image' } else { 'Answer image' }
        [void]$readable.AppendLine(('## {0} {1}' -f $roleLabel, ($item.imageIndex + 1)))
        [void]$readable.AppendLine()
        [void]$readable.AppendLine($markdown.Trim())
        [void]$readable.AppendLine()
        $handoffItems += [ordered]@{
            itemId = $item.artifactRef
            role = $item.role
            roleIndex = $item.imageIndex
            originalArtifactRef = $item.artifactRef
            originalSha256 = $sourceByRef[$item.artifactRef].sha256
            markdownArtifactRef = $item.ocr.markdownArtifactRef
            markdownPath = $markdownDescriptor.path
            markdownSha256 = $markdownDescriptor.sha256
            structuredArtifactRef = $item.ocr.structuredArtifactRef
            structuredPath = $structuredDescriptor.path
            structuredSha256 = $structuredDescriptor.sha256
            rawArtifactsRoot = $item.ocr.rawArtifactsRoot
        }
    }
    [IO.File]::WriteAllText($readablePath, $readable.ToString(), [Text.UTF8Encoding]::new($false))
    $handoffPath = Join-Path $canaryRoot 'content-adapter-handoff.json'
    Write-JsonNew $handoffPath ([ordered]@{
        schemaVersion = 'knowledge-clipper-image-to-content-handoff.v1'
        status = if ($humanReviewRecommended) { 'unresolved' } else { 'ready' }
        producerWorkerId = 'image.ocr.mineru'
        consumerBoundary = 'Core content-input.v1 adapter'
        adapterRequired = $true
        sourceCaptureArtifactRef = $captureRef
        explicitOrder = $expectedRefs
        imageProcessingProvenance = $result.outputs | Where-Object kind -eq 'image.processing.provenance' | Select-Object -First 1
        originalImages = $commonOriginals
        items = $handoffItems
        readableText = [ordered]@{
            path = [IO.Path]::GetRelativePath($canaryRoot, $readablePath).Replace('\','/')
            sha256 = (Get-FileHash -LiteralPath $readablePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        humanReviewRecommended = $humanReviewRecommended
        sourceGroundTruthMismatch = $sourceGroundTruthMismatch
        downstreamAllowedOnlyWhen = 'worker result status is completed'
        destinationWrites = @()
    })
}

$receiptFiles = Get-ChildItem -LiteralPath $canaryRoot -File -Recurse
$secretLeak = $false
if ($ImageHandling -eq 'ocr') { foreach ($file in $receiptFiles) {
    if ($file.Extension -in @('.json','.md','.txt','.ndjson')) {
        $value = Get-Content -LiteralPath $file.FullName -Raw
        if ($null -ne $value -and $value.Contains($token)) { $secretLeak = $true; break }
    }
} }

$preservedFiles = @()
foreach ($file in Get-ChildItem -LiteralPath $canaryRoot -File -Recurse) {
    if ($file.FullName -notmatch 'canary-receipt\.json$') { $preservedFiles += File-Evidence $file.FullName }
}
$originalEvidence = @()
foreach ($path in $sourcePaths) { $originalEvidence += File-Evidence $path }

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-image-path-canary-receipt.v3'
    productDisplayName = '零碎知识剪藏'
    candidateEntrypoint = 'workers/image-ocr/candidates/1.2.4/KnowledgeClipper.ImageOcr.Worker.exe'
    candidateSha256 = (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash.ToLowerInvariant()
    workerVersion = $result.worker.version
    canaryId = $canaryId
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    dataClass = 'synthetic-non-private'
    sourceImport = if ($importHistoricalSource) { [ordered]@{
        imagePath = [IO.Path]::GetRelativePath($projectRoot, $resolvedSourceImage).Replace('\','/')
        imageSha256 = (Get-FileHash -LiteralPath $resolvedSourceImage -Algorithm SHA256).Hash.ToLowerInvariant()
        receiptPath = [IO.Path]::GetRelativePath($projectRoot, $resolvedSourceReceipt).Replace('\','/')
        receiptSha256 = (Get-FileHash -LiteralPath $resolvedSourceReceipt -Algorithm SHA256).Hash.ToLowerInvariant()
        groundTruth = $sourceGroundTruth
    } } else { $null }
    imageHandling = $ImageHandling
    packagePath = [IO.Path]::GetRelativePath($projectRoot, $package).Replace('\','/')
    workerExitCode = $workerExit
    status = $result.status
    resultSha256 = (Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash.ToLowerInvariant()
    moduleResultSha256 = (Get-FileHash -LiteralPath $moduleResultPath -Algorithm SHA256).Hash.ToLowerInvariant()
    commonContractAdapterPassed = ($result.schemaVersion -eq '1.0.0' -and $result.worker.id -eq 'image.ocr.mineru')
    explicitRoleOrderPassed = $orderPassed
    ocrMarkersPassed = $markersPassed
    humanReviewRecommended = $humanReviewRecommended
    sourceGroundTruthMismatch = $sourceGroundTruthMismatch
    originalHashesPassed = $originalHashesPassed
    provenanceOriginalBindingPassed = $provenanceBindingPassed
    providerCalled = $providerCalled
    providerCalls = $providerCalls
    providerSubmissionCount = $submissionCount
    submissionPublished = Test-Path -LiteralPath $attemptSubmissionPath
    resumePublished = Test-Path -LiteralPath $attemptResumePath
    rawMineruPreserved = Test-Path -LiteralPath $attemptRawPath
    partialBlocksDownstream = $result.payload.partialBlocksDownstream
    readableText = if ($null -ne $readablePath) { File-Evidence $readablePath } else { $null }
    contentAdapterHandoff = if ($null -ne $handoffPath) { File-Evidence $handoffPath } else { $null }
    originals = $originalEvidence
    preservedFiles = $preservedFiles
    destinationWrites = @()
    secretLeakDetected = $secretLeak
    itemDiagnostics = @($result.itemDiagnostics)
    resultOutputs = @($result.outputs)
    attemptPath = [IO.Path]::GetRelativePath($package, $attemptRoot).Replace('\','/')
}
$receiptPath = Join-Path $canaryRoot 'canary-receipt.json'
Write-JsonNew $receiptPath $receipt

$modePassed = if ($ImageHandling -eq 'original') { -not $providerCalled -and $providerCalls -eq 0 -and $originalHashesPassed } else { $providerCalled -and $providerCalls -eq 1 -and $submissionCount -eq 1 -and -not (Test-Path -LiteralPath $attemptResumePath) -and $null -ne $handoffPath -and $null -ne $readablePath }
if ($workerExit -ne 0 -or $result.status -ne 'completed' -or -not $receipt.commonContractAdapterPassed -or -not $orderPassed -or -not $originalHashesPassed -or -not $provenanceBindingPassed -or -not $modePassed -or $secretLeak) {
    throw "Image/OCR live Canary failed. Receipt: $receiptPath"
}

[pscustomobject]@{
    CanaryId = $canaryId
    Status = $result.status
    OrderedItems = $orderedRefs.Count
    ImageHandling = $ImageHandling
    ResultSha256 = $receipt.resultSha256
    Receipt = $receiptPath
} | Format-List

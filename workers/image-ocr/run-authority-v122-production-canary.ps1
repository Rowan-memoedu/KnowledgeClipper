param(
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
$releaseRelative = 'releases/knowledge-clipper-authority-v1.2.2-20260828T012500Z/win-x64'
$checkpointRelative = 'contracts/authority/worker-registry/v1.2.2-20260828T012500Z/checkpoint.json'
$releaseRoot = (Resolve-Path (Join-Path $projectRoot $releaseRelative)).Path
$checkpointPath = (Resolve-Path (Join-Path $projectRoot $checkpointRelative)).Path
$imageWorker = (Resolve-Path (Join-Path $releaseRoot 'workers\image-ocr\KnowledgeClipper.ImageOcr.Worker.exe')).Path
$contentWorker = (Resolve-Path (Join-Path $releaseRoot 'workers\content\KnowledgeClipper.Content.Worker.exe')).Path
$mineruScript = (Resolve-Path (Join-Path $projectRoot '..\MinerU OCR Integration\mineru_ocr.py')).Path
$expectedReleaseTree = 'F15F21E5489B4002086B0664144606A7E2EF5BD40E1AA02AB90F34EC277C2503'
$expectedImageSha = '3487C4D0FF8E5C5CA81ED2D2F85D957720DCAFE93F848337EE53EAAD4EC01BAD'
$expectedContentSha = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'
$canaryId = 'auth-v122-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$canaryRoot = Join-Path $projectRoot ('canary\' + $canaryId)
$taskId = 't-' + $canaryId.Substring($canaryId.Length - 8)
$runId = 'r-' + $canaryId.Substring($canaryId.Length - 8)
$jobRoot = Join-Path $canaryRoot ('jobs\' + $taskId)
$inputsRoot = Join-Path $jobRoot 'inputs'
$imagesRoot = Join-Path $inputsRoot 'images'
New-Item -ItemType Directory -Path $imagesRoot -ErrorAction Stop | Out-Null

function Write-JsonNew([string]$Path, [object]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Refusing to overwrite $Path" }
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -ErrorAction Stop | Out-Null }
    $json = $Value | ConvertTo-Json -Depth 40
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Write-TextNew([string]$Path, [string]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Refusing to overwrite $Path" }
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -ErrorAction Stop | Out-Null }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Get-Sha([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Relative([string]$Base, [string]$Path) {
    return [IO.Path]::GetRelativePath($Base, (Get-Item -LiteralPath $Path).FullName).Replace('\','/')
}

function Get-FileEvidence([string]$Base, [string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{ path = Get-Relative $Base $item.FullName; bytes = $item.Length; sha256 = Get-Sha $item.FullName }
}

function Get-TreeEvidence([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse)
    $rows = foreach ($file in $files) {
        $relative = Get-Relative $Root $file.FullName
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative`t$($file.Length)`t$hash"
    }
    $rows = [string[]]$rows
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    $bytes = [long]0
    foreach ($file in $files) {
        $bytes += $file.Length
    }
    $digest = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))
    return [ordered]@{ files = $files.Count; bytes = $bytes; treeSha256 = [Convert]::ToHexString($digest) }
}

function New-CanaryImage([string]$Path, [string[]]$Lines) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new(1500, 420)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $font = [Drawing.Font]::new('Arial', 42, [Drawing.FontStyle]::Bold)
        try {
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                $graphics.DrawString($Lines[$index], $font, [Drawing.Brushes]::Black, 35, 38 + $index * 110)
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

function New-ImageDescriptor([string]$ArtifactRef, [string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        artifactRef = $ArtifactRef
        relativePath = Get-Relative $jobRoot $item.FullName
        sha256 = Get-Sha $item.FullName
        bytes = $item.Length
        mediaType = 'image/png'
        encoding = $null
    }
}

function New-IdempotencyKey([string]$OperationId, [string]$AttemptId) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($OperationId + ':' + $AttemptId)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Invoke-CandidateWorker([string]$Executable, [string]$RequestPath, [string]$AttemptRoot) {
    $stdoutPath = Join-Path $AttemptRoot 'worker.stdout.ndjson'
    $stderrPath = Join-Path $AttemptRoot 'worker.stderr.txt'
    & $Executable run --request $RequestPath 1> $stdoutPath 2> $stderrPath
    $exitCode = $LASTEXITCODE
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
    $resultPath = Join-Path $jobRoot $request.resultPath
    if (-not (Test-Path -LiteralPath $resultPath)) { throw "Worker published no result.json: $Executable" }
    return [ordered]@{
        exitCode = $exitCode
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        resultPath = $resultPath
        result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    }
}

$checkpointSha = Get-Sha $checkpointPath
$checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json
$releaseBefore = Get-TreeEvidence $releaseRoot
if ($releaseBefore.files -ne 79 -or $releaseBefore.treeSha256 -ne $expectedReleaseTree) { throw 'Authority v1.2.2 release tree mismatch before execution' }
if ((Get-FileHash -LiteralPath $imageWorker -Algorithm SHA256).Hash -ne $expectedImageSha) { throw 'Authority image worker hash mismatch' }
if ((Get-FileHash -LiteralPath $contentWorker -Algorithm SHA256).Hash -ne $expectedContentSha) { throw 'Authority content worker hash mismatch' }
$imageDescribeRaw = (& $imageWorker describe --json) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Authority image worker describe failed' }
$contentDescribeRaw = (& $contentWorker describe --json) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Authority content worker describe failed' }
$imageDescribe = $imageDescribeRaw | ConvertFrom-Json
$contentDescribe = $contentDescribeRaw | ConvertFrom-Json
if ($imageDescribe.workerId -ne 'image.ocr.mineru' -or $imageDescribe.workerVersion -ne '1.1.0') { throw 'Authority image worker identity mismatch' }
if ($contentDescribe.workerId -ne 'content.deepseek' -or $contentDescribe.workerVersion -ne '0.5.2') { throw 'Authority content worker identity mismatch' }

$q1Path = Join-Path $imagesRoot 'question-001.png'
$q2Path = Join-Path $imagesRoot 'question-002.png'
$a1Path = Join-Path $imagesRoot 'answer-001.png'
$a2Path = Join-Path $imagesRoot 'answer-002.png'
New-CanaryImage $q1Path @('KNOWLEDGECLIPPER AUTHORITY V1.2.2', 'QUESTION 1: WHAT IS 18 + 24?')
New-CanaryImage $q2Path @('KNOWLEDGECLIPPER AUTHORITY V1.2.2', 'QUESTION 2: WHICH POLYGON HAS THREE SIDES?')
New-CanaryImage $a1Path @('KNOWLEDGECLIPPER AUTHORITY V1.2.2', 'ANSWER 1: 18 + 24 = 42.')
New-CanaryImage $a2Path @('KNOWLEDGECLIPPER AUTHORITY V1.2.2', 'ANSWER 2: A TRIANGLE HAS THREE SIDES.')
$q1 = New-ImageDescriptor 'capture.image.question.0001' $q1Path
$q2 = New-ImageDescriptor 'capture.image.question.0002' $q2Path
$a1 = New-ImageDescriptor 'capture.image.answer.0001' $a1Path
$a2 = New-ImageDescriptor 'capture.image.answer.0002' $a2Path

$capture = [ordered]@{
    schemaVersion = 'capture-bundle.v1'
    captureId = 'capture-' + $canaryId
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    intent = 'screenshot'
    source = 'screenshot'
    modeSnapshot = [ordered]@{ workflow = 'card'; contextEnabled = $false; contextGranularity = 'sentence'; contextApplied = $false; imageHandling = 'ocr' }
    artifacts = @($a2, $q2, $a1, $q1)
    selectedTextArtifactRef = $null
    contextSegments = $null
    questionImages = @($q1.artifactRef, $q2.artifactRef)
    answerImages = @($a1.artifactRef, $a2.artifactRef)
    questionTurns = @()
}
$capturePath = Join-Path $inputsRoot 'capture-bundle.v1.json'
Write-JsonNew $capturePath $capture
$captureArtifactId = 'capture.bundle.' + $canaryId
$captureManifestPath = Join-Path $jobRoot ('artifacts\' + $captureArtifactId + '\artifact.json')
$captureRelative = Get-Relative $jobRoot $capturePath
$captureManifest = [ordered]@{
    schemaVersion = '1.0.0'; artifactId = $captureArtifactId; kind = 'capture-bundle'; contractVersion = '1.0.0'
    mediaType = 'application/vnd.rqc.capture-bundle+json'
    files = @([ordered]@{ path = $captureRelative; sha256 = Get-Sha $capturePath; bytes = (Get-Item $capturePath).Length; mediaType = 'application/vnd.rqc.capture-bundle+json' })
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    producer = [ordered]@{ stageId = 'capture'; workerId = 'candidate-canary.capture'; workerVersion = '1.0.0'; attemptId = 'capture-attempt-0001' }
    inputRefs = @(); dataClass = 'public'; metadata = [ordered]@{ entrypoint = $captureRelative; synthetic = $true }
}
Write-JsonNew $captureManifestPath $captureManifest
$captureRef = [ordered]@{ artifactRef = $captureArtifactId; kind = 'capture-bundle'; contractVersion = '1.0.0'; manifestPath = Get-Relative $jobRoot $captureManifestPath; expectedSha256 = Get-Sha $captureManifestPath }

$imageAttemptId = 'attempt-image-0001'
$imageAttemptRelative = 'runs/' + $runId + '/stages/image-ocr/attempts/' + $imageAttemptId
$imageAttemptRoot = Join-Path $jobRoot $imageAttemptRelative
New-Item -ItemType Directory -Path $imageAttemptRoot -ErrorAction Stop | Out-Null
$imageOperationId = 'op-image-' + $canaryId
$imageRequest = [ordered]@{
    schemaVersion = '1.0.0'; requestId = 'req-image-' + $canaryId; operationId = $imageOperationId; taskId = $taskId; runId = $runId
    stageId = 'image-ocr'; attemptId = $imageAttemptId; workerId = 'image.ocr.mineru'; operation = 'process-images'
    idempotencyKey = New-IdempotencyKey $imageOperationId $imageAttemptId
    attemptPath = $imageAttemptRelative; outputPath = $imageAttemptRelative + '/outputs'; progressPath = $imageAttemptRelative + '/progress.ndjson'; resultPath = $imageAttemptRelative + '/result.json'
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = $imageAttemptRelative + '/cancel.json'; gracePeriodMs = 2000 }
    inputRefs = @($captureRef)
    configSnapshot = [ordered]@{ mineruScriptPath = $mineruScript; providerSettings = [ordered]@{ modelVersion = 'vlm'; language = 'ch'; enableTable = $true; enableFormula = $true; timeoutSeconds = 900; pollIntervalSeconds = 5 } }
    payload = [ordered]@{}
}
$imageRequestPath = Join-Path $imageAttemptRoot 'request.json'
Write-JsonNew $imageRequestPath $imageRequest

if ($PrepareOnly) {
    $preparePath = Join-Path $canaryRoot 'preparation-receipt.json'
    Write-JsonNew $preparePath ([ordered]@{
        schemaVersion = 'knowledge-clipper-authority-v122-image-content-preparation.v1'; status = 'prepared'; canaryId = $canaryId
        checkpoint = [ordered]@{ path = $checkpointRelative; sha256 = $checkpointSha }
        release = [ordered]@{ root = $releaseRelative; files = $releaseBefore.files; bytes = $releaseBefore.bytes; treeSha256 = $releaseBefore.treeSha256 }
        imageWorker = [ordered]@{ path = Get-Relative $projectRoot $imageWorker; sha256 = Get-Sha $imageWorker; descriptor = $imageDescribe }
        contentWorker = [ordered]@{ path = Get-Relative $projectRoot $contentWorker; sha256 = Get-Sha $contentWorker; descriptor = $contentDescribe }
        requestPath = Get-Relative $projectRoot $imageRequestPath; providerCalled = $false
    })
    [pscustomobject]@{ CanaryId = $canaryId; Status = 'prepared'; Receipt = $preparePath } | Format-List
    exit 0
}

if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('MINERU_API_TOKEN','Process'))) { throw 'MINERU_API_TOKEN must be supplied by the caller process' }
$imageRun = Invoke-CandidateWorker $imageWorker $imageRequestPath $imageAttemptRoot
if ($imageRun.exitCode -ne 0 -or $imageRun.result.status -ne 'completed') { throw "Authority image candidate failed: $($imageRun.result.status)" }
$imageModulePath = Join-Path $imageAttemptRoot 'module-result.json'
$imageModule = Get-Content -LiteralPath $imageModulePath -Raw | ConvertFrom-Json
$expectedOrder = @($q1.artifactRef, $q2.artifactRef, $a1.artifactRef, $a2.artifactRef)
$observedOrder = @($imageRun.result.itemDiagnostics | ForEach-Object itemId)
if (($expectedOrder -join '|') -ne ($observedOrder -join '|')) { throw 'Authority image candidate changed explicit role order' }

$materialParts = @()
$ocrEvidence = @()
foreach ($item in $imageModule.details.items) {
    if ($item.status -ne 'completed' -or $null -eq $item.ocr) { throw "OCR item incomplete: $($item.artifactRef)" }
    $markdownDescriptor = $imageModule.outputs | Where-Object artifactRef -eq $item.ocr.markdownArtifactRef | Select-Object -First 1
    $structuredDescriptor = $imageModule.outputs | Where-Object artifactRef -eq $item.ocr.structuredArtifactRef | Select-Object -First 1
    if ($null -eq $markdownDescriptor -or $null -eq $structuredDescriptor) { throw "OCR output descriptor missing: $($item.artifactRef)" }
    $markdownPath = Join-Path $jobRoot $markdownDescriptor.path
    $markdownText = (Get-Content -LiteralPath $markdownPath -Raw).Trim()
    $roleLabel = if ($item.role -eq 'question') { 'Question image' } else { 'Answer image' }
    $materialParts += ('## {0} {1}' -f $roleLabel, ($item.imageIndex + 1)) + "`n`n" + $markdownText
    $ocrEvidence += [ordered]@{
        itemId = $item.artifactRef; role = $item.role; roleIndex = $item.imageIndex; markdownText = $markdownText
        markdown = Get-FileEvidence $jobRoot $markdownPath
        structured = Get-FileEvidence $jobRoot (Join-Path $jobRoot $structuredDescriptor.path)
        rawArtifactsRoot = $item.ocr.rawArtifactsRoot
    }
}
$materialText = $materialParts -join "`n`n"
$materialDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($materialText))).ToLowerInvariant()
$contentArtifactId = 'content-input-' + $materialDigest.Substring(0, 24)
$adapterRelative = 'runs/' + $runId + '/adapters/content.understand-and-card/' + $contentArtifactId + '/v0001'
$adapterRoot = Join-Path $jobRoot $adapterRelative
New-Item -ItemType Directory -Path $adapterRoot -ErrorAction Stop | Out-Null
$materialRelative = $adapterRelative + '/material.md'
$materialPath = Join-Path $jobRoot $materialRelative
Write-TextNew $materialPath $materialText
$contentInputRelative = $adapterRelative + '/content-input.v1.json'
$contentInputPath = Join-Path $jobRoot $contentInputRelative
$contentInput = [ordered]@{
    schema_version = 'standard-input/1.0.0'; package_id = $contentArtifactId; task_id = $taskId; input_version = '1.0.0'; mode = 'card'
    items = @([ordered]@{ item_id = 'item-0001'; source = [ordered]@{ kind = 'text'; material_path = $materialRelative } })
}
Write-JsonNew $contentInputPath $contentInput
$contentSources = @($captureRef)
foreach ($item in $imageRun.result.itemDiagnostics) {
    $contentSources += @($item.outputs | Where-Object kind -eq 'image-ocr.markdown')
}
$contentManifestPath = Join-Path $adapterRoot 'artifact.json'
$contentManifestRelative = $adapterRelative + '/artifact.json'
$contentManifest = [ordered]@{
    schemaVersion = '1.0.0'; artifactId = $contentArtifactId; kind = 'content-input'; contractVersion = '1.0.0'; mediaType = 'application/vnd.rqc.content-input+json'
    files = @(
        [ordered]@{ path = $contentInputRelative; sha256 = Get-Sha $contentInputPath; bytes = (Get-Item $contentInputPath).Length; mediaType = 'application/vnd.rqc.content-input+json' },
        [ordered]@{ path = $materialRelative; sha256 = Get-Sha $materialPath; bytes = (Get-Item $materialPath).Length; mediaType = 'text/markdown' }
    )
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    producer = [ordered]@{ stageId = 'core-adapter'; workerId = 'orchestrator.core-adapter'; workerVersion = '1.0.0'; attemptId = 'adapter-' + $contentArtifactId }
    inputRefs = $contentSources; dataClass = 'public'
    metadata = [ordered]@{ entrypoint = $contentInputRelative; sourceKinds = @($contentSources | ForEach-Object kind); candidateBoundCanary = $true }
}
Write-JsonNew $contentManifestPath $contentManifest
$contentRef = [ordered]@{ artifactRef = $contentArtifactId; kind = 'content-input'; contractVersion = '1.0.0'; manifestPath = $contentManifestRelative; expectedSha256 = Get-Sha $contentManifestPath }

$contentAttemptId = 'attempt-content-0001'
$contentAttemptRelative = 'runs/' + $runId + '/stages/content.understand-and-card/attempts/' + $contentAttemptId
$contentAttemptRoot = Join-Path $jobRoot $contentAttemptRelative
New-Item -ItemType Directory -Path $contentAttemptRoot -ErrorAction Stop | Out-Null
$contentOperationId = 'op-content-' + $canaryId
$contentRequest = [ordered]@{
    schemaVersion = '1.0.0'; requestId = 'req-content-' + $canaryId; operationId = $contentOperationId; taskId = $taskId; runId = $runId
    stageId = 'content.understand-and-card'; attemptId = $contentAttemptId; workerId = 'content.deepseek'; operation = 'understand-and-card'
    idempotencyKey = New-IdempotencyKey $contentOperationId $contentAttemptId
    attemptPath = $contentAttemptRelative; outputPath = $contentAttemptRelative + '/outputs'; progressPath = $contentAttemptRelative + '/progress.ndjson'; resultPath = $contentAttemptRelative + '/result.json'
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = $contentAttemptRelative + '/cancel.json'; gracePeriodMs = 2000 }
    inputRefs = @($contentRef)
    configSnapshot = [ordered]@{ checkpointId = $checkpoint.checkpointId; authorityRegistryVersion = '1.2.2'; destinationNeutral = $true; promptSet = 'open-md-v2' }
    payload = [ordered]@{ schemaVersion = 'content-run/1.0.0'; contentInputPath = $contentInputRelative; providerMode = 'live' }
}
$contentRequestPath = Join-Path $contentAttemptRoot 'request.json'
Write-JsonNew $contentRequestPath $contentRequest
$contentRun = Invoke-CandidateWorker $contentWorker $contentRequestPath $contentAttemptRoot
if ($contentRun.exitCode -ne 0 -or $contentRun.result.status -ne 'completed') { throw "Authority content candidate failed: $($contentRun.result.status)" }
$learningRef = $contentRun.result.outputs | Where-Object kind -eq 'learning-bundle' | Select-Object -First 1
$cardRef = $contentRun.result.outputs | Where-Object kind -eq 'card-bundle' | Select-Object -First 1
if ($null -eq $learningRef -or $null -eq $cardRef) { throw 'Authority content candidate did not publish both learning-bundle and card-bundle' }
$learningManifestPath = Join-Path $jobRoot $learningRef.manifestPath
$cardManifestPath = Join-Path $jobRoot $cardRef.manifestPath
$learningManifest = Get-Content -LiteralPath $learningManifestPath -Raw | ConvertFrom-Json
$cardManifest = Get-Content -LiteralPath $cardManifestPath -Raw | ConvertFrom-Json
$learningPayloadPath = Join-Path $jobRoot $learningManifest.files[0].path
$cardPayloadPath = Join-Path $jobRoot $cardManifest.files[0].path
$learning = Get-Content -LiteralPath $learningPayloadPath -Raw | ConvertFrom-Json
$cards = Get-Content -LiteralPath $cardPayloadPath -Raw | ConvertFrom-Json
if (@($cards.cards).Count -lt 1) { throw 'Authority content candidate produced no structured cards' }

$readablePath = Join-Path $canaryRoot 'readable-image-to-content.md'
$readable = [Text.StringBuilder]::new()
[void]$readable.AppendLine('# Authority v1.2.2 image-to-content Canary')
[void]$readable.AppendLine()
[void]$readable.AppendLine('## Original synthetic image text and roles')
foreach ($row in @(
    @('question',1,'WHAT IS 18 + 24?'), @('question',2,'WHICH POLYGON HAS THREE SIDES?'),
    @('answer',1,'18 + 24 = 42.'), @('answer',2,'A TRIANGLE HAS THREE SIDES.')
)) { [void]$readable.AppendLine(('- {0}[{1}]: {2}' -f $row[0],$row[1],$row[2])) }
[void]$readable.AppendLine()
[void]$readable.AppendLine('## MinerU OCR text')
foreach ($item in $ocrEvidence) {
    [void]$readable.AppendLine(('### {0}[{1}]' -f $item.role,($item.roleIndex + 1)))
    [void]$readable.AppendLine()
    [void]$readable.AppendLine($item.markdownText)
    [void]$readable.AppendLine()
}
[void]$readable.AppendLine('## DeepSeek reviewed structured output')
[void]$readable.AppendLine()
[void]$readable.AppendLine(('- LearningBundle release_state: `{0}`' -f $learning.release_state))
[void]$readable.AppendLine(('- CardBundle release_state: `{0}`' -f $cards.release_state))
foreach ($item in $learning.items) {
    foreach ($unit in $item.units) {
        [void]$readable.AppendLine()
        [void]$readable.AppendLine(('### Unit `{0}`' -f $unit.unit_id))
        [void]$readable.AppendLine(('- state/category: `{0}` / `{1}`' -f $unit.state,$unit.category_id))
        if ($unit.question) { [void]$readable.AppendLine(('- question: {0}' -f $unit.question)) }
        if ($unit.supplied_answer) { [void]$readable.AppendLine(('- reviewed answer: {0}' -f $unit.supplied_answer)) }
    }
}
[void]$readable.AppendLine()
[void]$readable.AppendLine('## Final cards')
foreach ($card in $cards.cards) {
    $front = @($card.front_blocks | ForEach-Object text) -join "`n"
    $back = @($card.back_blocks | ForEach-Object text) -join "`n"
    [void]$readable.AppendLine()
    [void]$readable.AppendLine(('### {0} / {1}' -f $card.template,$card.category_id))
    [void]$readable.AppendLine(('Front: {0}' -f $front))
    [void]$readable.AppendLine(('Back: {0}' -f $back))
}
Write-TextNew $readablePath $readable.ToString()

$secretLeak = $false
$secrets = @(
    [Environment]::GetEnvironmentVariable('MINERU_API_TOKEN','Process'),
    [Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY','Process'),
    [Environment]::GetEnvironmentVariable('DEEPSEEK_API_KEY','Process')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_.Length -ge 10 }
foreach ($file in Get-ChildItem -LiteralPath $canaryRoot -File -Recurse | Where-Object { $_.Extension -in @('.json','.md','.txt','.ndjson') }) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ($null -eq $text) { $text = '' }
    foreach ($secret in $secrets) { if ($text.Contains($secret)) { $secretLeak = $true } }
}
if ($secretLeak) { throw 'Credential leak detected in Canary text artifacts' }

$releaseAfter = Get-TreeEvidence $releaseRoot
if ($releaseAfter.treeSha256 -ne $releaseBefore.treeSha256) { throw 'Authority release tree changed during Canary' }
$rawZips = @(Get-ChildItem -LiteralPath $imageAttemptRoot -File -Recurse -Filter 'mineru-result.zip')
$receiptPath = Join-Path $canaryRoot 'canary-receipt.json'
$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-v122-image-content-canary.v1'; status = 'passed'; canaryId = $canaryId; createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    binding = [ordered]@{
        checkpoint = [ordered]@{ path = $checkpointRelative; sha256 = $checkpointSha; checkpointId = $checkpoint.checkpointId }
        release = [ordered]@{ root = $releaseRelative; files = $releaseBefore.files; bytes = $releaseBefore.bytes; treeSha256 = $releaseBefore.treeSha256; unchanged = ($releaseBefore.treeSha256 -eq $releaseAfter.treeSha256) }
        verifierCaveat = 'tools/verify_authority_registry_checkpoint.py requires registry-candidate/v1 inside the release, but authority v1.2.2 release tree does not contain that directory; checkpoint, dependency tree, live descriptors and exact entrypoint hashes were verified independently.'
    }
    workers = [ordered]@{
        image = [ordered]@{ workerId = $imageDescribe.workerId; version = $imageDescribe.workerVersion; entrypoint = Get-Relative $projectRoot $imageWorker; bytes = (Get-Item $imageWorker).Length; sha256 = Get-Sha $imageWorker; request = Get-FileEvidence $canaryRoot $imageRequestPath; result = Get-FileEvidence $canaryRoot $imageRun.resultPath }
        content = [ordered]@{ workerId = $contentDescribe.workerId; version = $contentDescribe.workerVersion; entrypoint = Get-Relative $projectRoot $contentWorker; bytes = (Get-Item $contentWorker).Length; sha256 = Get-Sha $contentWorker; request = Get-FileEvidence $canaryRoot $contentRequestPath; result = Get-FileEvidence $canaryRoot $contentRun.resultPath }
    }
    input = [ordered]@{ dataClass = 'synthetic-non-private'; questionImages = @($q1,$q2); answerImages = @($a1,$a2); artifactsArrayDeliberatelyNotRoleOrdered = $true }
    ocr = [ordered]@{ status = $imageRun.result.status; orderedItems = $observedOrder; commonOutputs = @($imageRun.result.outputs).Count; rawZipCount = $rawZips.Count; rawZips = @($rawZips | ForEach-Object { Get-FileEvidence $canaryRoot $_.FullName }); items = $ocrEvidence }
    continuation = [ordered]@{ adapter = 'existing Core content-input.v1 semantics'; artifact = Get-FileEvidence $canaryRoot $contentManifestPath; entrypoint = Get-FileEvidence $canaryRoot $contentInputPath; material = Get-FileEvidence $canaryRoot $materialPath; ancestryRefs = $contentSources.Count }
    content = [ordered]@{
        status = $contentRun.result.status; moduleReleaseState = $contentRun.result.payload.moduleReleaseState; commonOutputs = @($contentRun.result.outputs).Count
        learningBundle = [ordered]@{ reference = $learningRef; manifest = Get-FileEvidence $canaryRoot $learningManifestPath; payload = Get-FileEvidence $canaryRoot $learningPayloadPath; releaseState = $learning.release_state }
        cardBundle = [ordered]@{ reference = $cardRef; manifest = Get-FileEvidence $canaryRoot $cardManifestPath; payload = Get-FileEvidence $canaryRoot $cardPayloadPath; releaseState = $cards.release_state; cards = @($cards.cards).Count }
    }
    readable = Get-FileEvidence $canaryRoot $readablePath
    privacy = [ordered]@{ credentialLeakDetected = $secretLeak; syntheticOnly = $true }
    destinationWrites = [ordered]@{ remnote = $false; anki = $false }
}
Write-JsonNew $receiptPath $receipt
[pscustomobject]@{
    CanaryId = $canaryId; Status = 'passed'; OcrItems = $observedOrder.Count; Cards = @($cards.cards).Count
    ReleaseTreeSha256 = $releaseAfter.treeSha256; ReceiptSha256 = Get-Sha $receiptPath; Receipt = $receiptPath; Readable = $readablePath
} | Format-List

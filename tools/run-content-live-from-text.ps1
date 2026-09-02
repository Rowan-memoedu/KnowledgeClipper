param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$InputTextPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$SourceReceiptPath
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$InputTextPath = [IO.Path]::GetFullPath($InputTextPath)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$SourceReceiptPath = [IO.Path]::GetFullPath($SourceReceiptPath)

function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-JsonOnce([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable output already exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}
function Relative([string]$Path) { [IO.Path]::GetRelativePath($OutputRoot, $Path).Replace('\', '/') }

if (-not (Test-Path -LiteralPath $InputTextPath -PathType Leaf)) { throw "Input text missing: $InputTextPath" }
if (-not (Test-Path -LiteralPath $SourceReceiptPath -PathType Leaf)) { throw "Source receipt missing: $SourceReceiptPath" }
if (Test-Path -LiteralPath $OutputRoot) { throw "Immutable output root already exists: $OutputRoot" }

$release = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'content-worker\releases') -Directory |
    Where-Object Name -like 'content.deepseek-0.5.2-*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $release) { throw 'Content 0.5.2 release root is missing' }
$worker = Join-Path $release.FullName 'KnowledgeClipper.Content.Worker.exe'
if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) { throw "Content worker missing: $worker" }

$short = [guid]::NewGuid().ToString('N').Substring(0, 20)
$taskId = "task-ocr-content-$short"
$runId = "run-$short"
$attemptId = "attempt-$short"
$inputRoot = Join-Path $OutputRoot 'inputs\content'
$materialPath = Join-Path $inputRoot 'materials\ocr.md'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $materialPath) | Out-Null
Copy-Item -LiteralPath $InputTextPath -Destination $materialPath

$standardPath = Join-Path $inputRoot 'standard-input.v1.json'
Write-JsonOnce $standardPath ([ordered]@{
    schema_version = 'standard-input/1.0.0'
    package_id = "ocr-content-$short"
    task_id = $taskId
    input_version = '1.0.0'
    mode = 'card'
    items = @([ordered]@{
        item_id = 'item-ocr-readable'
        source = [ordered]@{ kind = 'text'; material_path = 'inputs/content/materials/ocr.md' }
        hints = [ordered]@{
            category_id = 'general_learning'
            answer_state = 'provided'
            context_state = 'provided'
            risk_state = 'ordinary'
            requested_templates = @('concept_transfer')
        }
    })
})

$manifestPath = Join-Path $inputRoot 'artifact.json'
Write-JsonOnce $manifestPath ([ordered]@{
    schemaVersion = '1.0.0'; artifactId = "content-input-$short"; kind = 'content-input'; contractVersion = '1.0.0'; mediaType = 'application/json'
    files = @(
        [ordered]@{ path = (Relative $standardPath); sha256 = (Hash-File $standardPath); bytes = (Get-Item $standardPath).Length; mediaType = 'application/json' },
        [ordered]@{ path = (Relative $materialPath); sha256 = (Hash-File $materialPath); bytes = (Get-Item $materialPath).Length; mediaType = 'text/markdown; charset=utf-8' }
    )
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    producer = [ordered]@{ stageId = 'image-ocr'; workerId = 'image.ocr.mineru'; workerVersion = '1.0.0'; attemptId = 'source-live-mineru' }
    inputRefs = @(); dataClass = 'personal'
    metadata = [ordered]@{
        destinationNeutral = $true; entrypoint = (Relative $standardPath); moduleRole = 'content-understanding-and-card-generation'
        moduleSchema = 'standard-input/1.0.0'; productDisplayName = '零碎知识剪藏'; productId = 'KnowledgeClipper'
        sourceReceipt = (Relative $SourceReceiptPath); sourceReceiptSha256 = (Hash-File $SourceReceiptPath)
    }
})

$attemptRelative = "runs/$runId/stages/content.understand-and-card/attempts/$attemptId"
$attemptRoot = Join-Path $OutputRoot $attemptRelative.Replace('/', '\')
$requestPath = Join-Path $attemptRoot 'request.json'
$inputRef = [ordered]@{ artifactRef = "content-input-$short"; kind = 'content-input'; contractVersion = '1.0.0'; manifestPath = (Relative $manifestPath); expectedSha256 = (Hash-File $manifestPath) }
Write-JsonOnce $requestPath ([ordered]@{
    schemaVersion = '1.0.0'; requestId = "request-$short"; operationId = "operation-$short"; taskId = $taskId; runId = $runId
    stageId = 'content.understand-and-card'; attemptId = $attemptId; workerId = 'content.deepseek'; operation = 'understand-and-card'
    idempotencyKey = (Hash-File $manifestPath); attemptPath = $attemptRelative; outputPath = "$attemptRelative/outputs"
    progressPath = "$attemptRelative/progress.ndjson"; resultPath = "$attemptRelative/result.json"
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRelative/cancel.json"; gracePeriodMs = 2000 }
    inputRefs = @($inputRef)
    configSnapshot = [ordered]@{ categoryRegistry = 'category-registry/2.0.0'; checkpointId = 'contracts-v1-20260825T115938Z'; destinationNeutral = $true; productDisplayName = '零碎知识剪藏'; productId = 'KnowledgeClipper'; promptSet = 'open-md-v2' }
    payload = [ordered]@{ schemaVersion = 'content-run/1.0.0'; contentInputPath = (Relative $standardPath); providerMode = 'live' }
})

$stdoutPath = Join-Path $attemptRoot 'worker.stdout.ndjson'
$stderrPath = Join-Path $attemptRoot 'worker.stderr.log'
$quotedRequestPath = '"' + $requestPath.Replace('"', '\"') + '"'
$process = Start-Process -FilePath $worker -ArgumentList "run --request $quotedRequestPath" -WorkingDirectory $release.FullName -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
$resultPath = Join-Path $attemptRoot 'result.json'
if (-not (Test-Path -LiteralPath $resultPath)) { throw "Content worker published no result; exit=$($process.ExitCode)" }
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $result.status -ne 'completed') { throw "Content live path failed; exit=$($process.ExitCode); status=$($result.status)" }
$learningRef = @($result.outputs | Where-Object kind -eq 'learning-bundle')
if ($learningRef.Count -ne 1) { throw 'Content result has no unique LearningBundle' }
$learningManifestPath = Join-Path $OutputRoot $learningRef[0].manifestPath.Replace('/', '\')
$learningManifest = Get-Content -LiteralPath $learningManifestPath -Raw | ConvertFrom-Json
$learningRelative = if ([string]::IsNullOrWhiteSpace([string]$learningManifest.metadata.entrypoint)) { [string]$learningManifest.files[0].path } else { [string]$learningManifest.metadata.entrypoint }
$learningPath = Join-Path $OutputRoot $learningRelative.Replace('/', '\')
$learning = Get-Content -LiteralPath $learningPath -Raw | ConvertFrom-Json
$providerEvidence = @($learning.ai_trace | ForEach-Object { [ordered]@{ provider=$_.provider; mode=$_.mode; model=$_.model; attempts=$_.attempts; promptSha256=$_.prompt_sha256; responseSha256=$_.response_sha256 } })

$receiptPath = Join-Path $OutputRoot 'canary-receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = 'quality-content-live-from-text/1.0.0'; status = 'passed'; generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    source = [ordered]@{ path = $InputTextPath; sha256 = (Hash-File $InputTextPath); bytes = (Get-Item $InputTextPath).Length; receipt = $SourceReceiptPath; receiptSha256 = (Hash-File $SourceReceiptPath) }
    worker = [ordered]@{ id = 'content.deepseek'; version = '0.5.2'; path = $worker; sha256 = (Hash-File $worker) }
    request = [ordered]@{ path = (Relative $requestPath); sha256 = (Hash-File $requestPath) }
    result = [ordered]@{ path = (Relative $resultPath); sha256 = (Hash-File $resultPath); status = $result.status }
    learningBundle = [ordered]@{ path = (Relative $learningPath); sha256 = (Hash-File $learningPath); bytes = (Get-Item $learningPath).Length }
    externalCalls = [ordered]@{ deepseek = ($providerEvidence | Measure-Object -Property attempts -Sum).Sum }
    providerEvidence = $providerEvidence
})

[ordered]@{ status='passed'; root=$OutputRoot; receipt=$receiptPath; receiptSha256=(Hash-File $receiptPath); learningBundle=$learningPath; learningBundleSha256=(Hash-File $learningPath) } | ConvertTo-Json -Depth 6

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Resolve-Path (Join-Path $testRoot '..\..')
$worker = Join-Path $projectRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$stamp = (Get-Date).ToUniversalTime().ToString('yyMMddHHmmss')
$canaryRoot = Join-Path $projectRoot (Join-Path 'canary\rw-live' $stamp)
$jobRoot = Join-Path $canaryRoot 'jobs\rl'
$documentTitle = 'RemNote Exporter Stable Worker Canary · rne-worker-v1'

function Write-JsonOnce([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable file exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30 -Compress), [Text.UTF8Encoding]::new($false))
}
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function New-CardArtifact {
    $payloadRelative = 'inputs/cards/v0001/payload.json'
    $payloadPath = Join-Path $jobRoot $payloadRelative
    Write-JsonOnce $payloadPath ([ordered]@{
        schemaVersion = 'card-bundle.v1'; bundleId = 'rne-worker-card-v1'
        cards = @([ordered]@{ cardId = 'worker-forward-card-v1'; cardType = 'basic'; direction = 'forward'; front = 'RemNote Stable Worker Canary：5 + 6 等于多少？'; back = '11' })
    })
    $manifestRelative = 'inputs/cards/v0001/artifact.json'
    $manifestPath = Join-Path $jobRoot $manifestRelative
    Write-JsonOnce $manifestPath ([ordered]@{
        schemaVersion = '1.0.0'; artifactId = 'cards'; kind = 'card.bundle'; contractVersion = '1.0.0'; mediaType = 'application/json'
        files = @([ordered]@{ path = $payloadRelative; sha256 = Hash-File $payloadPath; bytes = (Get-Item $payloadPath).Length; mediaType = 'application/json' })
        createdAt = (Get-Date).ToUniversalTime().ToString('o')
        producer = [ordered]@{ stageId = 'fixture'; workerId = 'fixture.builder'; workerVersion = '1.0.0'; attemptId = 'fixture-001' }
        inputRefs = @(); dataClass = 'personal'; metadata = @{}
    })
    return [ordered]@{ artifactRef = 'cards'; kind = 'card.bundle'; contractVersion = '1.0.0'; manifestPath = $manifestRelative; expectedSha256 = Hash-File $manifestPath }
}

function New-LiveRequest([string]$RunId, [string]$AttemptId, [object]$InputRef) {
    $attemptRelative = "runs/$RunId/stages/export-remnote/attempts/$AttemptId"
    $requestPath = Join-Path $jobRoot "$attemptRelative/request.json"
    Write-JsonOnce $requestPath ([ordered]@{
        schemaVersion = '1.0.0'; requestId = "request-$AttemptId"; operationId = "operation-$AttemptId"; taskId = 'rl'; runId = $RunId
        stageId = 'export-remnote'; attemptId = $AttemptId; workerId = 'export.remnote'; operation = 'remnote.export'
        idempotencyKey = ('8' * 64); attemptPath = $attemptRelative; outputPath = "$attemptRelative/outputs"; progressPath = "$attemptRelative/progress.ndjson"; resultPath = "$attemptRelative/result.json"
        cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRelative/cancel.json"; gracePeriodMs = 2000 }
        inputRefs = @($InputRef); configSnapshot = @{}
        payload = [ordered]@{ schemaVersion = '1.0.0'; documentTitle = $documentTitle; sink = 'mcp-direct'; target = [ordered]@{ rootRemId = 'lqeDb4aS9TRgapF6X'; rootTitle = '剪藏' } }
    })
    return $requestPath
}

if (-not (Test-Path -LiteralPath $worker)) { throw "Worker not built: $worker" }
New-Item -ItemType Directory -Force -Path $canaryRoot | Out-Null
$describePath = Join-Path $canaryRoot 'describe.json'
& $worker describe --json | Set-Content -LiteralPath $describePath -Encoding utf8NoBOM
if ($LASTEXITCODE -ne 0) { throw 'describe failed' }
$inputRef = New-CardArtifact
$firstRequest = New-LiveRequest 'r1' 'a1' $inputRef
& $worker run --request $firstRequest
if ($LASTEXITCODE -ne 0) { throw 'first live run failed or became uncertain; inspect result.json and do not retry automatically' }
$firstResult = Get-Content -Raw (Join-Path (Split-Path $firstRequest) 'result.json') | ConvertFrom-Json
if ($firstResult.status -ne 'completed' -or -not $firstResult.payload.readbackVerified) { throw 'first live run was not readback-verified' }

$secondRequest = New-LiveRequest 'r2' 'a2' $inputRef
& $worker run --request $secondRequest
if ($LASTEXITCODE -ne 0) { throw 'second live run failed or became uncertain; inspect result.json and do not retry automatically' }
$secondResult = Get-Content -Raw (Join-Path (Split-Path $secondRequest) 'result.json') | ConvertFrom-Json
if ($secondResult.status -ne 'completed' -or $secondResult.payload.outcome -ne 'already_applied' -or $secondResult.payload.documentRemId -ne $firstResult.payload.documentRemId) {
    throw 'second run did not prove idempotent reuse of one RemNote document'
}

python (Join-Path $testRoot 'validate-public-contracts.py') --project-root $projectRoot --canary-root $canaryRoot --describe $describePath --expected-result-count 2 --expected-statuses completed --suite remnote-exporter-live-mcp-canary
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$liveReceipt = [ordered]@{
    schemaVersion = '1.0.0'; suite = 'remnote-exporter-live-mcp-idempotency'; status = 'passed'; documentTitle = $documentTitle
    documentRemId = $firstResult.payload.documentRemId; firstOutcome = $firstResult.payload.outcome; secondOutcome = $secondResult.payload.outcome
    readbackVerified = $true; directMatchCount = 1; dateNodesCreated = $false; imagesHandled = $false
}
Write-JsonOnce (Join-Path $canaryRoot 'live-canary-receipt.json') $liveReceipt
Write-Host "PASS RemNote live MCP canary: $canaryRoot"

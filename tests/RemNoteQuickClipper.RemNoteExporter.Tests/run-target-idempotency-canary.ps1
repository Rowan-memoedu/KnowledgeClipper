$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
$worker = Join-Path $projectRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$fakeCli = Join-Path $testRoot 'fake-stateful-remnote-cli.js'
$node = 'C:\Program Files\nodejs\node.exe'
$runId = (Get-Date).ToUniversalTime().ToString('yyMMddHHmmss')
$canaryRoot = Join-Path $projectRoot (Join-Path 'canary\rwt' $runId)
$statePath = Join-Path $canaryRoot 'fake-remnote-state.json'

function Write-JsonOnce([string]$Path, [object]$Value) {
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable file exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30 -Compress), [Text.UTF8Encoding]::new($false))
}

function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function New-Input([string]$JobRoot) {
    $payloadRelative = 'inputs/cards/v0001/payload.json'
    $payloadPath = Join-Path $JobRoot $payloadRelative
    Write-JsonOnce $payloadPath ([ordered]@{
        schemaVersion = 'card-bundle.v1'; bundleId = 'cards-stable-target-v1'
        cards = @([ordered]@{ cardId = 'card-001'; cardType = 'basic'; direction = 'forward'; front = '稳定目标是什么？'; back = '同一 bundle 复用同一文档' })
    })
    $manifestRelative = 'inputs/cards/v0001/artifact.json'
    $manifestPath = Join-Path $JobRoot $manifestRelative
    Write-JsonOnce $manifestPath ([ordered]@{
        schemaVersion = '1.0.0'; artifactId = 'cards'; kind = 'card.bundle'; contractVersion = '1.0.0'; mediaType = 'application/json'
        files = @([ordered]@{ path = $payloadRelative; sha256 = Hash-File $payloadPath; bytes = (Get-Item -LiteralPath $payloadPath).Length; mediaType = 'application/json' })
        createdAt = '2026-08-25T00:00:00Z'; producer = [ordered]@{ stageId = 'fixture'; workerId = 'fixture.builder'; workerVersion = '1.0.0'; attemptId = 'fixture' }
        inputRefs = @(); dataClass = 'personal'; metadata = @{}
    })
    return [ordered]@{ artifactRef = 'cards'; kind = 'card.bundle'; contractVersion = '1.0.0'; manifestPath = $manifestRelative; expectedSha256 = Hash-File $manifestPath }
}

function Invoke-Worker([string]$TaskId, [string]$AttemptId) {
    $jobRoot = Join-Path $canaryRoot "jobs\$TaskId"
    $inputRef = New-Input $jobRoot
    $attemptRelative = "runs/run-001/stages/export-remnote/attempts/$AttemptId"
    $requestPath = Join-Path $jobRoot "$attemptRelative/request.json"
    Write-JsonOnce $requestPath ([ordered]@{
        schemaVersion = '1.0.0'; requestId = "request-$AttemptId"; operationId = "operation-$AttemptId"; taskId = $TaskId; runId = 'run-001'
        stageId = 'export-remnote'; attemptId = $AttemptId; workerId = 'export.remnote'; operation = 'remnote.export'; idempotencyKey = ('a' * 64)
        attemptPath = $attemptRelative; outputPath = "$attemptRelative/outputs"; progressPath = "$attemptRelative/progress.ndjson"; resultPath = "$attemptRelative/result.json"
        cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRelative/cancel.json"; gracePeriodMs = 2000 }
        inputRefs = @($inputRef)
        configSnapshot = [ordered]@{ nodePath = $node; remnoteCliJsPath = $fakeCli; remnoteMcpUrl = 'http://127.0.0.1:43102/mcp' }
        payload = [ordered]@{ schemaVersion = '1.0.0'; documentTitle = "剪藏 · $TaskId"; sink = 'mcp-direct'; target = [ordered]@{ rootRemId = 'root-rem-id'; rootTitle = '剪藏' } }
    })
    & $worker run --request $requestPath
    if ($LASTEXITCODE -ne 0) { throw "worker run failed for $TaskId with $LASTEXITCODE" }
    $resultPath = Join-Path $jobRoot "$attemptRelative/result.json"
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $artifactPath = Join-Path $jobRoot $result.outputs[0].manifestPath
    $artifact = Get-Content -Raw -LiteralPath $artifactPath | ConvertFrom-Json
    $receiptPath = Join-Path $jobRoot $artifact.files[0].path
    return [ordered]@{ resultPath = $resultPath; receiptPath = $receiptPath; receipt = (Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json) }
}

if (-not (Test-Path -LiteralPath $worker)) { throw "Worker not built: $worker" }
if (-not (Test-Path -LiteralPath $fakeCli)) { throw "Fake CLI missing: $fakeCli" }
New-Item -ItemType Directory -Force -Path $canaryRoot | Out-Null
$oldState = $env:REMNOTE_EXPORTER_FAKE_STATE
try {
    $env:REMNOTE_EXPORTER_FAKE_STATE = $statePath
    $first = Invoke-Worker 'task-alpha' 'attempt-alpha'
    $second = Invoke-Worker 'task-beta' 'attempt-beta'
} finally {
    $env:REMNOTE_EXPORTER_FAKE_STATE = $oldState
}

$state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
$expectedTitle = '剪藏 · cards-stable-target-v1'
if ($first.receipt.outcome -ne 'created' -or $second.receipt.outcome -ne 'already_applied') { throw 'Idempotency outcomes did not match created/already_applied' }
if ($first.receipt.documentTitle -ne $expectedTitle -or $second.receipt.documentTitle -ne $expectedTitle) { throw 'Transient task ID was not replaced by bundle identity' }
if ($state.createCount -ne 1 -or $state.documents.Count -ne 1 -or $state.documents[0].title -ne $expectedTitle) { throw 'Fake RemNote did not contain exactly one stable document' }

$receiptPath = Join-Path $canaryRoot 'receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = '1.0.0'; suite = 'remnote-exporter-target-idempotency'; status = 'passed'; generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    worker = [ordered]@{ path = $worker.Substring($projectRoot.Length + 1).Replace('\', '/'); sha256 = Hash-File $worker }
    input = [ordered]@{ bundleId = 'cards-stable-target-v1'; taskIds = @('task-alpha', 'task-beta'); requestedTitles = @('剪藏 · task-alpha', '剪藏 · task-beta') }
    observed = [ordered]@{ documentTitle = $expectedTitle; outcomes = @($first.receipt.outcome, $second.receipt.outcome); createCount = $state.createCount; documentCount = $state.documents.Count; documentRemId = $state.documents[0].remId; readbackVerified = @($first.receipt.readbackVerified, $second.receipt.readbackVerified) }
    results = @($first.resultPath.Substring($projectRoot.Length + 1).Replace('\', '/'), $second.resultPath.Substring($projectRoot.Length + 1).Replace('\', '/'))
})
Write-Host "PASS target/idempotency Canary: $receiptPath"
Write-Host "SHA256 $(Hash-File $receiptPath)"

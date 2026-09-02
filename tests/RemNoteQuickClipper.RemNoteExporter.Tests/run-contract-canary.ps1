$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Resolve-Path (Join-Path $testRoot '..\..')
$worker = Join-Path $projectRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$fakeCli = Join-Path $testRoot 'fake-remnote-cli.js'
$node = 'C:\Program Files\nodejs\node.exe'
$runId = (Get-Date).ToUniversalTime().ToString('yyMMddHHmmss')
$canaryRoot = Join-Path $projectRoot (Join-Path 'canary\rw' $runId)

function Write-JsonOnce([string]$Path, [object]$Value) {
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable file exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30 -Compress), [Text.UTF8Encoding]::new($false))
}

function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function New-BundleArtifact([string]$JobRoot, [string]$ArtifactId, [string]$Kind, [object]$Bundle) {
    $version = "inputs/$ArtifactId/v0001"
    $payloadRelative = "$version/payload.json"
    $payloadPath = Join-Path $JobRoot $payloadRelative
    Write-JsonOnce $payloadPath $Bundle
    $manifestRelative = "$version/artifact.json"
    $manifestPath = Join-Path $JobRoot $manifestRelative
    $manifest = [ordered]@{
        schemaVersion = '1.0.0'; artifactId = $ArtifactId; kind = $Kind; contractVersion = '1.0.0'; mediaType = 'application/json'
        files = @([ordered]@{ path = $payloadRelative; sha256 = Hash-File $payloadPath; bytes = (Get-Item -LiteralPath $payloadPath).Length; mediaType = 'application/json' })
        createdAt = (Get-Date).ToUniversalTime().ToString('o')
        producer = [ordered]@{ stageId = 'fixture'; workerId = 'fixture.builder'; workerVersion = '1.0.0'; attemptId = 'fixture-001' }
        inputRefs = @(); dataClass = 'personal'; metadata = @{}
    }
    Write-JsonOnce $manifestPath $manifest
    return [ordered]@{ artifactRef = $ArtifactId; kind = $Kind; contractVersion = '1.0.0'; manifestPath = $manifestRelative; expectedSha256 = Hash-File $manifestPath }
}

function New-Request([string]$TaskId, [string]$AttemptId, [string]$Sink, [object[]]$InputRefs, [hashtable]$ConfigSnapshot) {
    $jobRoot = Join-Path $canaryRoot "jobs\$TaskId"
    $attemptRelative = "runs/run-001/stages/export-remnote/attempts/$AttemptId"
    $attempt = Join-Path $jobRoot $attemptRelative
    New-Item -ItemType Directory -Force -Path $attempt | Out-Null
    $request = [ordered]@{
        schemaVersion = '1.0.0'; requestId = "request-$AttemptId"; operationId = "operation-$AttemptId"; taskId = $TaskId; runId = 'run-001'
        stageId = 'export-remnote'; attemptId = $AttemptId; workerId = 'export.remnote'; operation = 'remnote.export'
        idempotencyKey = ('{0:x64}' -f ([uint64]($AttemptId.GetHashCode() -band 0x7fffffff)))
        attemptPath = $attemptRelative; outputPath = "$attemptRelative/outputs"; progressPath = "$attemptRelative/progress.ndjson"; resultPath = "$attemptRelative/result.json"
        cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRelative/cancel.json"; gracePeriodMs = 2000 }
        inputRefs = $InputRefs; configSnapshot = $ConfigSnapshot
        payload = [ordered]@{ schemaVersion = '1.0.0'; documentTitle = "Stable Worker Fixture $AttemptId"; sink = $Sink; target = [ordered]@{ rootRemId = 'root-rem-id'; rootTitle = '剪藏' } }
    }
    $requestPath = Join-Path $attempt 'request.json'
    Write-JsonOnce $requestPath $request
    return [ordered]@{ JobRoot = $jobRoot; Attempt = $attempt; RequestPath = $requestPath; Request = $request }
}

if (-not (Test-Path -LiteralPath $worker)) { throw "Worker not built: $worker" }
New-Item -ItemType Directory -Force -Path $canaryRoot | Out-Null
$describePath = Join-Path $canaryRoot 'describe.json'
& $worker describe --json | Set-Content -LiteralPath $describePath -Encoding utf8NoBOM
if ($LASTEXITCODE -ne 0) { throw 'describe failed' }

$learning = [ordered]@{ schemaVersion = 'learning-bundle.v1'; bundleId = 'learning-fixture-v1'; entries = @([ordered]@{ itemId = 'entry-001'; text = '公共契约适配条目' }) }
$cards = [ordered]@{ schemaVersion = 'card-bundle.v1'; bundleId = 'cards-fixture-v1'; cards = @([ordered]@{ cardId = 'card-001'; cardType = 'basic'; direction = 'forward'; front = '幂等键的作用是什么？'; back = '防止重复副作用' }) }

$zipJob = Join-Path $canaryRoot 'jobs\tz'
$zipRefs = @((New-BundleArtifact $zipJob 'learning-fixture' 'learning.bundle' $learning), (New-BundleArtifact $zipJob 'cards-fixture' 'card.bundle' $cards))
$zip = New-Request 'tz' 'az' 'markdown-zip' $zipRefs @{}
& $worker run --request $zip.RequestPath
if ($LASTEXITCODE -ne 0) { throw 'zip worker run failed' }

$cancelJob = Join-Path $canaryRoot 'jobs\tc'
$cancelRefs = @((New-BundleArtifact $cancelJob 'cards-cancel' 'card.bundle' $cards))
$cancel = New-Request 'tc' 'ac' 'markdown-zip' $cancelRefs @{}
$requested = (Get-Date).ToUniversalTime()
Write-JsonOnce (Join-Path $cancel.Attempt 'cancel.json') ([ordered]@{ schemaVersion = '1.0.0'; attemptId = 'ac'; operationId = 'operation-ac'; reason = 'user_stop'; requestedAt = $requested.ToString('o'); graceDeadline = $requested.AddMilliseconds(2000).ToString('o') })
& $worker run --request $cancel.RequestPath
if ($LASTEXITCODE -ne 0) { throw 'cancel worker run failed' }

$unknownJob = Join-Path $canaryRoot 'jobs\tu'
$unknownRefs = @((New-BundleArtifact $unknownJob 'cards-unknown' 'card.bundle' $cards))
$unknown = New-Request 'tu' 'au' 'mcp-direct' $unknownRefs @{ nodePath = $node; remnoteCliJsPath = $fakeCli; remnoteMcpUrl = 'http://127.0.0.1:43101/mcp' }
& $worker run --request $unknown.RequestPath
if ($LASTEXITCODE -ne 1) { throw "unknown-state worker exit should be 1, got $LASTEXITCODE" }

python (Join-Path $testRoot 'validate-public-contracts.py') --project-root $projectRoot --canary-root $canaryRoot --describe $describePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "PASS RemNote Exporter contract canary: $canaryRoot"

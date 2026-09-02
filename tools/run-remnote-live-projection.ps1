param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$MatrixRoot,
    [Parameter(Mandatory = $true)][string]$ProjectionPath,
    [Parameter(Mandatory = $true)][string]$ContentReceiptPath,
    [Parameter(Mandatory = $true)][string]$TargetRootRemId,
    [Parameter(Mandatory = $true)][string]$TargetRootTitle,
    [Parameter(Mandatory = $true)][string]$DocumentTitle
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$MatrixRoot = [IO.Path]::GetFullPath($MatrixRoot)
$ProjectionPath = [IO.Path]::GetFullPath($ProjectionPath)
$ContentReceiptPath = [IO.Path]::GetFullPath($ContentReceiptPath)
$jobsDirectory = Join-Path $MatrixRoot 'jobs'
$jobRoot = if (Test-Path -LiteralPath $jobsDirectory -PathType Container) {
    $jobs = @(Get-ChildItem -LiteralPath $jobsDirectory -Directory)
    if ($jobs.Count -ne 1) { throw 'Expected exactly one standard jobs/<taskId> root' }
    $jobs[0].FullName
} else { Join-Path $MatrixRoot 'job' }
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Relative([string]$Path) { [IO.Path]::GetRelativePath($jobRoot, $Path).Replace('\', '/') }
function Write-JsonOnce([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable output already exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}

$projection = Get-Content -LiteralPath $ProjectionPath -Raw | ConvertFrom-Json
$inputRef = $projection.projected
if ($projection.status -ne 'passed' -or $inputRef.kind -ne 'card.bundle') { throw 'Passed RemNote card projection required' }
$worker = Join-Path $ProjectRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$attemptRelative = 'runs/run-live/stages/export-remnote/attempts/attempt-live'
$attemptRoot = Join-Path $jobRoot $attemptRelative.Replace('/', '\')
$requestPath = Join-Path $attemptRoot 'request.json'
$idempotencyKey = (Hash-File (Join-Path $jobRoot $inputRef.manifestPath.Replace('/', '\')))
Write-JsonOnce $requestPath ([ordered]@{
    schemaVersion = '1.0.0'; requestId = 'request-path-matrix-A'; operationId = 'operation-path-matrix-A'; taskId = (Split-Path $jobRoot -Leaf)
    runId = 'run-live'; stageId = 'export-remnote'; attemptId = 'attempt-live'; workerId = 'export.remnote'; operation = 'remnote.export'
    idempotencyKey = $idempotencyKey; attemptPath = $attemptRelative; outputPath = "$attemptRelative/outputs"; progressPath = "$attemptRelative/progress.ndjson"; resultPath = "$attemptRelative/result.json"
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$attemptRelative/cancel.json"; gracePeriodMs = 2000 }
    inputRefs = @($inputRef); configSnapshot = @{}
    payload = [ordered]@{ schemaVersion = '1.0.0'; documentTitle = $DocumentTitle; sink = 'mcp-direct'; target = [ordered]@{ rootRemId = $TargetRootRemId; rootTitle = $TargetRootTitle } }
})
$stdoutPath = Join-Path $attemptRoot 'worker.stdout.ndjson'
$stderrPath = Join-Path $attemptRoot 'worker.stderr.log'
$quotedRequest = '"' + $requestPath.Replace('"','\"') + '"'
$process = Start-Process -FilePath $worker -ArgumentList "run --request $quotedRequest" -WorkingDirectory (Split-Path -Parent $worker) -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
$resultPath = Join-Path $attemptRoot 'result.json'
if (-not (Test-Path -LiteralPath $resultPath)) { throw "RemNote worker published no result; exit=$($process.ExitCode)" }
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $result.status -ne 'completed' -or -not $result.payload.readbackVerified) { throw "RemNote path is not safely completed; exit=$($process.ExitCode); status=$($result.status)" }

$cli = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules\remnote-mcp-server\dist\remnote-cli\index.js'
$readbackPath = Join-Path $MatrixRoot 'independent-readback.json'
$readbackText = & node $cli --mcp-url http://127.0.0.1:43101/mcp read $result.payload.documentRemId --content-mode structured --view full --depth 3
if ($LASTEXITCODE -ne 0) { throw 'Independent RemNote readback failed' }
[IO.File]::WriteAllText($readbackPath, ($readbackText -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
$readback = Get-Content -LiteralPath $readbackPath -Raw | ConvertFrom-Json
$writeManifestPath = Join-Path $jobRoot $result.outputs[0].manifestPath.Replace('/', '\')
$writeManifest = Get-Content -LiteralPath $writeManifestPath -Raw | ConvertFrom-Json
$writeReceiptPath = Join-Path $jobRoot $writeManifest.files[0].path.Replace('/', '\')
$writeReceipt = Get-Content -LiteralPath $writeReceiptPath -Raw | ConvertFrom-Json
if ($readback.remId -ne $result.payload.documentRemId -or $readback.title -ne $writeReceipt.documentTitle -or $readback.parentRemId -ne $TargetRootRemId) { throw 'Independent RemNote identity/title/parent readback mismatch' }
$contentReceipt = Get-Content -LiteralPath $ContentReceiptPath -Raw | ConvertFrom-Json
$deepSeekCalls = (@($contentReceipt.providerEvidence) | Measure-Object -Property attempts -Sum).Sum
$receiptPath = Join-Path $MatrixRoot 'path-A-receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = 'quality-real-path/1.0.0'; pathId = 'A'; status = 'passed'; generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    route = 'text -> real DeepSeek -> Core content-to-remnote.v1 projection -> real RemNote Canary document'
    isolation = [ordered]@{ rootRemId = $TargetRootRemId; rootTitle = $TargetRootTitle; formalClipRootTouched = $false; deleted = $false }
    originalInput = [ordered]@{ contentReceipt = $ContentReceiptPath; contentReceiptSha256 = (Hash-File $ContentReceiptPath) }
    projection = [ordered]@{ path = $ProjectionPath; sha256 = (Hash-File $ProjectionPath); projectedPayloadSha256 = $projection.payloadSha256 }
    worker = [ordered]@{ id = 'export.remnote'; path = $worker; sha256 = (Hash-File $worker) }
    result = [ordered]@{ path = (Relative $resultPath); sha256 = (Hash-File $resultPath); outcome = $result.payload.outcome; documentRemId = $result.payload.documentRemId; readbackVerified = $result.payload.readbackVerified }
    independentReadback = [ordered]@{ path = $readbackPath; sha256 = (Hash-File $readbackPath); requestedTitle = $DocumentTitle; effectiveTitle = $readback.title; parentRemId = $readback.parentRemId }
    externalCalls = [ordered]@{ deepseek = $deepSeekCalls; remnoteExporterInvocations = 1; independentRemnoteReadbacks = 1 }
    noDelete = $true
})
[ordered]@{ status='passed'; receipt=$receiptPath; sha256=(Hash-File $receiptPath); documentRemId=$result.payload.documentRemId; readback=$readbackPath } | ConvertTo-Json -Depth 5

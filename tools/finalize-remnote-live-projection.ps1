param(
    [Parameter(Mandatory = $true)][string]$MatrixRoot,
    [Parameter(Mandatory = $true)][string]$ContentReceiptPath
)

$ErrorActionPreference = 'Stop'
$MatrixRoot = [IO.Path]::GetFullPath($MatrixRoot)
$ContentReceiptPath = [IO.Path]::GetFullPath($ContentReceiptPath)
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-JsonOnce([string]$Path, [object]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Immutable receipt already exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}
$jobs = @(Get-ChildItem -LiteralPath (Join-Path $MatrixRoot 'jobs') -Directory)
if ($jobs.Count -ne 1) { throw 'Expected one jobs/<taskId> root' }
$jobRoot = $jobs[0].FullName
$resultFiles = @(Get-ChildItem -LiteralPath $jobRoot -Recurse -Filter result.json -File)
if ($resultFiles.Count -ne 1) { throw 'Expected one exporter result' }
$resultPath = $resultFiles[0].FullName
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($result.status -ne 'completed' -or $result.worker.id -ne 'export.remnote' -or -not $result.payload.readbackVerified) { throw 'Completed verified export.remnote result required' }
$projectionPath = Join-Path $MatrixRoot 'projection.json'
$projection = Get-Content -LiteralPath $projectionPath -Raw | ConvertFrom-Json
if ($projection.status -ne 'passed') { throw 'Passed Core projection required' }
$readbackPath = Join-Path $MatrixRoot 'independent-readback.json'
$readback = Get-Content -LiteralPath $readbackPath -Raw | ConvertFrom-Json
$manifestPath = Join-Path $jobRoot $result.outputs[0].manifestPath.Replace('/', '\')
if ((Hash-File $manifestPath) -ne ([string]$result.outputs[0].expectedSha256).ToLowerInvariant()) { throw 'Write receipt artifact manifest drifted' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$writeReceiptPath = Join-Path $jobRoot $manifest.files[0].path.Replace('/', '\')
if ((Hash-File $writeReceiptPath) -ne ([string]$manifest.files[0].sha256).ToLowerInvariant()) { throw 'Write receipt payload drifted' }
$writeReceipt = Get-Content -LiteralPath $writeReceiptPath -Raw | ConvertFrom-Json
if ($readback.remId -ne $result.payload.documentRemId -or $readback.title -ne $writeReceipt.documentTitle -or $readback.parentRemId -ne $writeReceipt.rootRemId) { throw 'Independent RemNote identity/title/parent readback mismatch' }
$contentReceipt = Get-Content -LiteralPath $ContentReceiptPath -Raw | ConvertFrom-Json
$deepSeekCalls = (@($contentReceipt.providerEvidence) | Measure-Object -Property attempts -Sum).Sum
$receiptPath = Join-Path $MatrixRoot 'path-A-receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = 'quality-real-path/1.0.0'; pathId = 'A'; status = 'passed'; generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    route = 'text -> real DeepSeek -> Core content-to-remnote.v1 projection -> real RemNote Canary document'
    isolation = [ordered]@{ rootRemId = $writeReceipt.rootRemId; formalClipRootTouched = $false; deleted = $false }
    originalInput = [ordered]@{ contentReceipt = $ContentReceiptPath; contentReceiptSha256 = (Hash-File $ContentReceiptPath) }
    projection = [ordered]@{ path = $projectionPath; sha256 = (Hash-File $projectionPath); projectedPayloadSha256 = $projection.payloadSha256 }
    result = [ordered]@{ path = $resultPath; sha256 = (Hash-File $resultPath); outcome = $result.payload.outcome; documentRemId = $result.payload.documentRemId; readbackVerified = $true }
    writeReceipt = [ordered]@{ path = $writeReceiptPath; sha256 = (Hash-File $writeReceiptPath); contentSha256 = $writeReceipt.contentSha256 }
    independentReadback = [ordered]@{ path = $readbackPath; sha256 = (Hash-File $readbackPath); effectiveTitle = $readback.title; parentRemId = $readback.parentRemId }
    externalCalls = [ordered]@{ deepseek = $deepSeekCalls; remnoteExporterInvocations = 1; independentRemnoteReadbacks = 1 }
    noDelete = $true
})
[ordered]@{ status='passed'; receipt=$receiptPath; sha256=(Hash-File $receiptPath); documentRemId=$result.payload.documentRemId; effectiveTitle=$readback.title } | ConvertTo-Json -Depth 5

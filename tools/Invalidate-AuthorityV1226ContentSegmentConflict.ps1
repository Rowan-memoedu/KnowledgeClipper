[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$authority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.26-20260901T013500Z'
$checkpoint = Join-Path $authority 'checkpoint.json'
$initialReceipt = Join-Path $ProjectRoot 'canary\sealed-live-ocr-anki-v1.2.26\20260901T015500Z\initial-receipt.json'
$output = Join-Path $authority 'invalidation-receipt.v1.json'
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
foreach ($path in @($checkpoint, $initialReceipt)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing v1.2.26 invalidation evidence: $path" } }
if (Test-Path -LiteralPath $output) { throw "Append-only v1.2.26 invalidation already exists: $output" }
$document = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-invalidation-receipt.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'invalidated-after-failed-canary-not-for-reuse'
    authority = [ordered]@{registryVersion='1.2.26';checkpoint=$checkpoint;checkpointSha256=Sha $checkpoint}
    failedCanary = [ordered]@{receipt=$initialReceipt;receiptSha256=Sha $initialReceipt;taskId='live-ocr-anki-v1226-20260901013322790';failureCode='binding.ocr_proofread_segment_invalid';workerStarted=[ordered]@{imageOcr=1;content=1;anki=0};sideEffects=[ordered]@{mineruSubmissions=1;deepSeekCalls='recorded-by-content-attempt';ankiWrites=0;remnoteWrites=0;physicalSpeakerInvocations=0}}
    reason = 'Content 0.9.2 emitted the same pseg identifier in active source_segments and proofread audit segments; Core strict collision guard correctly blocked Anki before worker start. Supersede only with a formal Content producer fix.'
    frozenV1Modified = $false
}
[IO.File]::WriteAllText($output, (($document | ConvertTo-Json -Depth 32) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{status=$document.status;receipt=$output;sha256=Sha $output}|ConvertTo-Json -Depth 10

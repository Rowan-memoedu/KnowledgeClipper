param(
    [Parameter(Mandatory = $true)][string]$ConfigurationPath,
    [Parameter(Mandatory = $true)][string]$QualityDecisionPath,
    [Parameter(Mandatory = $true)][string]$AuthorizationId,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\releases\20260902T122133Z-worker-process-runtime.1'))
$checkpointSha = 'ACACC82EE64CCD3011D252A4258BA6029E32000D210F120875B9AF7B80C768AC'
$packetSha = 'B441AB5C6E3A790809CF30BF5D8192E1248179FBCFFD126529E3686AE75BB9D9'
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$project = Join-Path $PSScriptRoot '..\qualification-consumers\KnowledgeClipper.Core.V3.RealCanary\KnowledgeClipper.Core.V3.RealCanary.csproj'

if (-not $Execute) { throw 'Real Canary is locked. Pass -Execute only after the total-control thread explicitly releases the Quality gate.' }
if (-not [System.IO.File]::Exists($ConfigurationPath)) { throw "Anki configuration does not exist: $ConfigurationPath" }
if (-not [System.IO.File]::Exists($QualityDecisionPath)) { throw "Independent Quality decision does not exist: $QualityDecisionPath" }
if (-not [System.IO.File]::Exists($dotnet)) { throw "Pinned .NET SDK does not exist: $dotnet" }
if ((Get-FileHash -LiteralPath (Join-Path $releaseRoot 'checkpoint.json') -Algorithm SHA256).Hash -ne $checkpointSha) { throw 'Immutable checkpoint hash mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $releaseRoot 'typed-sdk-slice-packet.json') -Algorithm SHA256).Hash -ne $packetSha) { throw 'Immutable packet hash mismatch.' }

$qualityDecision = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($QualityDecisionPath))
if ($qualityDecision -notmatch [regex]::Escape($checkpointSha) -or $qualityDecision -notmatch [regex]::Escape($packetSha) -or $qualityDecision -notmatch '(?i)\b(PASS|GO)\b') {
    throw 'Independent Quality decision is not a positive decision bound to this exact checkpoint and packet.'
}

$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$receiptRoot = [System.IO.Path]::GetFullPath("D:\CodexTemp\KnowledgeClipper.Core.V3.RealCanary\$timestamp")
if ([System.IO.Directory]::Exists($receiptRoot)) { throw "Create-once Canary root already exists: $receiptRoot" }
[System.IO.Directory]::CreateDirectory($receiptRoot) | Out-Null
$receipt = Join-Path $receiptRoot 'receipt.json'

& $dotnet run --project $project -c Release -p:CoreV3ReleaseRoot=$releaseRoot -- `
    --release-root $releaseRoot `
    --configuration ([System.IO.Path]::GetFullPath($ConfigurationPath)) `
    --receipt $receipt `
    --authorization-id $AuthorizationId
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) { throw "Real question-to-Anki Canary failed closed. Do not retry. Receipt: $receipt" }
Write-Host "REAL_CANARY_RECEIPT $receipt"
Write-Host "REAL_CANARY_RECEIPT_SHA256 $((Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash)"

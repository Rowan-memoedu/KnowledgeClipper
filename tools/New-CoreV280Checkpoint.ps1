[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative = 'contracts/core/v2.7/checkpoints/v2.7.20-20260901T043300Z/checkpoint.json'
$targetRelative = 'contracts/core/v2.8/checkpoints/v2.8.0-20260901T053600Z/checkpoint.json'
$source = Join-Path $ProjectRoot $sourceRelative.Replace('/','\')
$target = Join-Path $ProjectRoot $targetRelative.Replace('/','\')
$runtimeRelative = 'dist/orchestrator/win-x64/v2.8.0/KnowledgeClipper.Orchestrator.exe'
$runtime = Join-Path $ProjectRoot $runtimeRelative.Replace('/','\')
$receiptRelative = 'canary/core-v2.8.0-offline/20260901T053500Z/core-adapter-and-module-action.receipt.json'
$receipt = Join-Path $ProjectRoot $receiptRelative.Replace('/','\')
if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $runtime -PathType Leaf) -or -not (Test-Path -LiteralPath $receipt -PathType Leaf) -or (Test-Path -LiteralPath $target)) {
    throw 'Append-only Core v2.8.0 checkpoint precondition failed.'
}

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Entry([string]$Relative) {
    $path = Join-Path $ProjectRoot $Relative.Replace('/','\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checkpoint input missing: $Relative" }
    [ordered]@{ path=$Relative; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Sha $path).ToLowerInvariant() }
}
function Tree([object[]]$Files) {
    $rows = [string[]]@($Files | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
    [Array]::Sort($rows,[StringComparer]::Ordinal)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($rows -join "`n") + "`n")))).ToLowerInvariant()
}

$doc = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$files = @($doc.tree.files | ForEach-Object { [ordered]@{ path=$_.path; bytes=[long]$_.bytes; sha256=[string]$_.sha256 } })
$updates = @(
    'orchestrator/Core/DesktopIntentResolver.cs',
    'orchestrator/Core/ModuleActionControl.cs',
    'orchestrator/Core/CommandAndIpc.cs',
    'tests/FakeWorker/Program.cs',
    'tests/CoreAdapter.IntegrationTests/Program.cs',
    'tools/validate_contracts.py',
    'tools/New-CoreV280ValidationReceipt.ps1',
    'tools/New-CoreV280Checkpoint.ps1',
    'contracts/core/v2.8/module-control/orchestrator-ipc.v1.3.json',
    'contracts/core/v2.8/module-control/schemas/ipc-request.v1.3.schema.json',
    'contracts/core/v2.8/module-control/schemas/ipc-response.v1.3.schema.json',
    'contracts/core/v2.8/module-control/schemas/module-action-request.v2.schema.json',
    'contracts/core/v2.8/module-control/schemas/module-action-receipt.v2.schema.json',
    'contracts/core/v2.8/module-control/fixtures/module-action-request/valid/remnote-discover-target.json',
    'contracts/core/v2.8/module-control/fixtures/module-action-request/invalid/discover-on-v1.json',
    'contracts/fixtures/remnote-target-discovery-request/valid/exact-document.json',
    'contracts/fixtures/remnote-target-discovery-request/invalid/ui-root-id.json',
    'contracts/fixtures/remnote-target-discovery-result/valid/unique.json',
    'contracts/fixtures/remnote-target-discovery-result/invalid/ambiguous-persistable.json',
    $receiptRelative,
    $runtimeRelative
)
foreach ($relative in $updates) {
    $files = @($files | Where-Object { $_.path -ne $relative })
    $files += Entry $relative
}

$doc.checkpointId = 'core-v2.8.0-20260901T053600Z'
$doc.checkpointVersion = '2.8.0'
$doc.createdAt = '2026-09-01T05:36:00+08:00'
$doc.generatedAt = $doc.createdAt
$doc.status = 'passed-offline-authority-admission-ready'
$doc.supersedes = [ordered]@{ path=$sourceRelative; sha256=Sha $source }
$describe = ((& $runtime describe 2>&1) -join "`n") | ConvertFrom-Json -Depth 20
if ($LASTEXITCODE -ne 0 -or $describe.version -notlike '2.8.0*') { throw 'Core v2.8.0 runtime identity failed.' }
$doc.core = [ordered]@{ version='2.8.0'; describedVersion=$describe.version; executable=$runtimeRelative; bytes=(Get-Item -LiteralPath $runtime).Length; sha256=Sha $runtime }
$doc.publicVersions.moduleAction = 'knowledge-clipper-module-action.v2'
$doc.publicVersions.ipc = '1.3.0'
$doc.validation.contracts = (Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json -Depth 50).contracts
$doc.validation.coreAdapter = [ordered]@{ path=$receiptRelative; sha256=Sha $receipt; status='passed'; groups=16; remnoteTargetDiscoveryV13=$true; originalMediaAnkiOnly=$true; externalCalls=0; externalWrites=0 }
$doc.authorityBoundary = [ordered]@{ currentAuthority='1.2.34'; intendedAuthority='1.2.35'; promotion='pending-append-only-authority-assembly' }
$doc.requirements.originalMedia = [ordered]@{ destinations=@('anki'); remnote='by-design-unsupported-nonblocking'; remnoteRejectedBeforeWorker=$true; exportRemnoteStageAllowed=$false }
$doc.requirements.remnoteExactTitleDiscovery = [ordered]@{ operation='remnote.discover-target'; moduleActionSchema='knowledge-clipper-module-action.v2'; ipc='1.3.0'; persistableOnlyWhen=@('healthy','ready','unique','exactReadback','persistableBinding'); externalWrites=0; automaticRetryAllowed=$false }
$doc.tree = [ordered]@{ fileCount=$files.Count; totalBytes=[long](($files | Measure-Object bytes -Sum).Sum); treeSha256=(Tree $files); files=$files }
[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
[IO.File]::WriteAllText($target,(($doc | ConvertTo-Json -Depth 100) + "`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{ status='passed'; checkpoint=$targetRelative; sha256=Sha $target; tree=$doc.tree; runtime=$doc.core } | ConvertTo-Json -Depth 24

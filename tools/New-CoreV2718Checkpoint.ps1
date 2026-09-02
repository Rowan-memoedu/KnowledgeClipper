[CmdletBinding()]
param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative='contracts/core/v2.7/checkpoints/v2.7.17-20260901T033000Z/checkpoint.json'
$targetRelative='contracts/core/v2.7/checkpoints/v2.7.18-20260901T040700Z/checkpoint.json'
$source=Join-Path $ProjectRoot $sourceRelative.Replace('/','\')
$target=Join-Path $ProjectRoot $targetRelative.Replace('/','\')
if(!(Test-Path -LiteralPath $source -PathType Leaf) -or (Test-Path -LiteralPath $target)) { throw 'Append-only Core v2.7.18 checkpoint precondition failed.' }

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Entry([string]$Relative) {
  $path=Join-Path $ProjectRoot $Relative.Replace('/','\')
  if(!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing checkpoint input: $Relative" }
  $item=Get-Item -LiteralPath $path
  [ordered]@{path=$Relative;bytes=[long]$item.Length;sha256=(Sha $path).ToLowerInvariant()}
}

$receipt='canary/core-v2.7.17-offline/20260901T032000Z/core-adapter.receipt.json'
$runtimeRelative='dist/orchestrator/win-x64/v2.7.9/KnowledgeClipper.Orchestrator.exe'
$receiptDoc=Get-Content -LiteralPath (Join-Path $ProjectRoot $receipt.Replace('/','\')) -Raw | ConvertFrom-Json -Depth 100
if($receiptDoc.status -ne 'passed' -or @($receiptDoc.results).Count -ne 15) { throw 'Core adapter validation did not pass all 15 groups.' }
$runtime=Join-Path $ProjectRoot $runtimeRelative.Replace('/','\')
$describe=((& $runtime describe 2>&1)-join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
if($LASTEXITCODE -ne 0 -or $describe.version -ne '2.7.9.0') { throw 'Core v2.7.9 runtime identity failed.' }

$doc=Get-Content -LiteralPath $source -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$files=@(foreach($file in $doc.tree.files){ Entry ([string]$file.path) })
foreach($relative in @('tools/New-KnowledgeClipperAuthorityRelease.ps1','tools/New-CoreV2718Checkpoint.ps1')) {
  $existing=@($files | Where-Object path -eq $relative)
  if($existing.Count -eq 0) { $files += Entry $relative } else { $files=@($files | Where-Object path -ne $relative); $files += Entry $relative }
}
$rows=[string[]]@($files | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
[Array]::Sort($rows,[StringComparer]::Ordinal)
$tree=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($rows -join "`n")+"`n")))).ToLowerInvariant()
$bytes=[long]0; foreach($file in $files){$bytes += [long]$file.bytes}

$doc.checkpointId='core-v2.7.18-20260901T040700Z'
$doc.checkpointVersion='2.7.18'
$doc.createdAt='2026-09-01T04:07:00+08:00'
$doc.generatedAt='2026-09-01T04:07:00+08:00'
$doc.supersedes=[ordered]@{path=$sourceRelative;sha256=Sha $source}
$doc.core=[ordered]@{version='2.7.9';describedVersion=$describe.version;executable=$runtimeRelative;bytes=(Get-Item $runtime).Length;sha256=Sha $runtime}
$doc.authorityBoundary.currentAuthority='1.2.29'
$doc.authorityBoundary.intendedAuthority='1.2.32'
$doc.authorityBoundary.promotion='pending-append-only-authority-assembly'
$doc.validation.coreAdapter=[ordered]@{path=$receipt;sha256=Sha (Join-Path $ProjectRoot $receipt.Replace('/','\'));status='passed';groups=15;ocrInteractiveCommandRoot=$true;rawCorrectionExcludedFromAdapterInputRefs=$true;tamperFailClosed=$true}
$doc.requirements.interactiveOcrCorrection=[ordered]@{status='core-command-root-bound';rawCorrectionArtifactInputRef=$false;acceptedAndResolvedEventsRequired=$true;correctionPayloadHashRequired=$true;diffHashRequired=$true;oldFailedAttemptRetried=$false}
$doc.requirements.portableSchemaClosure=[ordered]@{status='release-dependency-closure-bound';frozenArtifactSchema='contracts/v1/schemas/common/artifact.schema.json';frozenSchemaModified=$false;closureGate='portable-schema-closure-gate.v1.json'}
$doc.tree=[ordered]@{fileCount=$files.Count;totalBytes=$bytes;treeSha256=$tree;files=$files}
[IO.Directory]::CreateDirectory((Split-Path -Parent $target))|Out-Null
[IO.File]::WriteAllText($target,(($doc|ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{status='passed';checkpoint=$targetRelative;sha256=Sha $target;tree=$doc.tree;core=$doc.core;coreAdapter=$doc.validation.coreAdapter}|ConvertTo-Json -Depth 30

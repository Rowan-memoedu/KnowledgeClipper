[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$validBaseRelative = 'contracts/core/v2.8/checkpoints/v2.8.1-20260901T054000Z/checkpoint.json'
$predecessorRelative = 'contracts/core/v2.9/checkpoints/v2.9.2-20260901T061900Z/checkpoint.json'
$targetRelative = 'contracts/core/v2.9/checkpoints/v2.9.3-20260901T062500Z/checkpoint.json'
$scriptRelative = 'tools/New-CoreV293Checkpoint.ps1'
$validBase = Join-Path $ProjectRoot $validBaseRelative.Replace('/','\')
$predecessor = Join-Path $ProjectRoot $predecessorRelative.Replace('/','\')
$target = Join-Path $ProjectRoot $targetRelative.Replace('/','\')

if (-not (Test-Path -LiteralPath $validBase -PathType Leaf) -or
    -not (Test-Path -LiteralPath $predecessor -PathType Leaf) -or
    (Test-Path -LiteralPath $target)) {
    throw 'Append-only Core v2.9.3 checkpoint precondition failed.'
}

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Entry([string]$Relative) {
    $path = Join-Path $ProjectRoot $Relative.Replace('/','\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checkpoint input missing: $Relative" }
    [ordered]@{ path=$Relative; bytes=[long](Get-Item -LiteralPath $path).Length; sha256=(Sha $path).ToLowerInvariant() }
}
function Tree([object[]]$Files) {
    $rows = [string[]]@($Files | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
    [Array]::Sort($rows,[StringComparer]::Ordinal)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($rows -join "`n") + "`n")))).ToLowerInvariant()
}

$doc = Get-Content -LiteralPath $predecessor -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$files = @($doc.tree.files | ForEach-Object { [ordered]@{ path=[string]$_['path']; bytes=[long]$_['bytes']; sha256=[string]$_['sha256'] } })
foreach ($relative in @($predecessorRelative,$scriptRelative)) {
    $files = @($files | Where-Object { $_['path'] -ne $relative })
    $files += Entry $relative
}
[long]$totalBytes = 0
foreach ($file in $files) { $totalBytes += [long]$file['bytes'] }
if ($totalBytes -le 0) { throw 'Core v2.9.3 checkpoint totalBytes must be positive.' }

$priorInvalidations = @($doc.invalidates)
$doc.checkpointId = 'core-v2.9.3-20260901T062500Z'
$doc.checkpointVersion = '2.9.3'
$doc.createdAt = '2026-09-01T06:25:00Z'
$doc.generatedAt = $doc.createdAt
$doc.status = 'passed-offline-authority-admission-ready'
$doc.supersedes = [ordered]@{ path=$validBaseRelative; sha256=Sha $validBase; immutable=$true }
$doc.invalidates = @($priorInvalidations) + @(
    [ordered]@{
        path=$predecessorRelative
        sha256=Sha $predecessor
        reason='contracts-validation-admission-fields-omitted-never-authority-bound'
        authorityBound=$false
        immutableEvidenceRetained=$true
    }
)
$doc.validation.contracts.status = 'passed'
$doc.validation.contracts.frozenV1Modified = $false
$doc.authorityBoundary = [ordered]@{
    currentAuthority='1.2.34'
    intendedAuthority='1.2.36'
    promotion='pending-append-only-authority-assembly'
    skippedUnadmittedCandidate='1.2.35'
    invalidCheckpointsNeverAuthorityBound=@(
        'contracts/core/v2.9/checkpoints/v2.9.0-20260901T061600Z/checkpoint.json',
        'contracts/core/v2.9/checkpoints/v2.9.1-20260901T061700Z/checkpoint.json',
        $predecessorRelative
    )
}
$doc.tree = [ordered]@{ fileCount=$files.Count; totalBytes=$totalBytes; treeSha256=Tree $files; files=$files }

[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
[IO.File]::WriteAllText($target,(($doc | ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false))
$readback = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json -AsHashtable -Depth 100
if ($readback.validation.contracts.status -ne 'passed' -or $readback.validation.contracts.frozenV1Modified -ne $false -or
    [long]$readback.tree.totalBytes -ne $totalBytes -or [int]$readback.tree.fileCount -ne $files.Count -or
    [string]$readback.tree.treeSha256 -ne (Tree $files)) {
    throw 'Core v2.9.3 checkpoint readback mismatch.'
}

[ordered]@{
    status='passed'
    checkpoint=$targetRelative
    sha256=Sha $target
    invalidatedCheckpoint=[ordered]@{path=$predecessorRelative;sha256=Sha $predecessor}
    tree=[ordered]@{fileCount=$readback.tree.fileCount;totalBytes=$readback.tree.totalBytes;treeSha256=$readback.tree.treeSha256}
    runtime=$readback.core
} | ConvertTo-Json -Depth 12

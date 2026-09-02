[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$validPredecessorRelative = 'contracts/core/v2.8/checkpoints/v2.8.1-20260901T054000Z/checkpoint.json'
$invalidPredecessorRelative = 'contracts/core/v2.9/checkpoints/v2.9.0-20260901T061600Z/checkpoint.json'
$targetRelative = 'contracts/core/v2.9/checkpoints/v2.9.1-20260901T061700Z/checkpoint.json'
$scriptRelative = 'tools/New-CoreV291Checkpoint.ps1'
$validPredecessor = Join-Path $ProjectRoot $validPredecessorRelative.Replace('/','\')
$invalidPredecessor = Join-Path $ProjectRoot $invalidPredecessorRelative.Replace('/','\')
$target = Join-Path $ProjectRoot $targetRelative.Replace('/','\')

if (-not (Test-Path -LiteralPath $validPredecessor -PathType Leaf) -or
    -not (Test-Path -LiteralPath $invalidPredecessor -PathType Leaf) -or
    (Test-Path -LiteralPath $target)) {
    throw 'Append-only Core v2.9.1 checkpoint precondition failed.'
}

function Sha([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Entry([string]$Relative) {
    $path = Join-Path $ProjectRoot $Relative.Replace('/','\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Checkpoint input missing: $Relative"
    }
    [ordered]@{
        path = $Relative
        bytes = [long](Get-Item -LiteralPath $path).Length
        sha256 = (Sha $path).ToLowerInvariant()
    }
}

function Tree([object[]]$Files) {
    $rows = [string[]]@($Files | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes((($rows -join "`n") + "`n"))
        )
    ).ToLowerInvariant()
}

$doc = Get-Content -LiteralPath $invalidPredecessor -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$files = @($doc.tree.files | ForEach-Object {
    [ordered]@{ path = [string]$_['path']; bytes = [long]$_['bytes']; sha256 = [string]$_['sha256'] }
})
foreach ($relative in @($invalidPredecessorRelative, $scriptRelative)) {
    $files = @($files | Where-Object { $_['path'] -ne $relative })
    $files += Entry $relative
}

[long]$totalBytes = 0
foreach ($file in $files) {
    $totalBytes += [long]$file['bytes']
}
if ($totalBytes -le 0) {
    throw 'Corrected checkpoint totalBytes must be positive.'
}

$doc.checkpointId = 'core-v2.9.1-20260901T061700Z'
$doc.checkpointVersion = '2.9.1'
$doc.createdAt = '2026-09-01T06:17:00Z'
$doc.generatedAt = $doc.createdAt
$doc.status = 'passed-offline-authority-admission-ready'
$doc.supersedes = [ordered]@{
    path = $validPredecessorRelative
    sha256 = Sha $validPredecessor
    immutable = $true
}
$doc.invalidates = @(
    [ordered]@{
        path = $invalidPredecessorRelative
        sha256 = Sha $invalidPredecessor
        reason = 'tree.totalBytes-zero-never-authority-bound'
        authorityBound = $false
        immutableEvidenceRetained = $true
    }
)
$doc.authorityBoundary = [ordered]@{
    currentAuthority = '1.2.34'
    intendedAuthority = '1.2.35'
    promotion = 'pending-append-only-authority-assembly'
    invalidCheckpointNeverAuthorityBound = $invalidPredecessorRelative
}
$doc.tree = [ordered]@{
    fileCount = $files.Count
    totalBytes = $totalBytes
    treeSha256 = Tree $files
    files = $files
}

[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
[IO.File]::WriteAllText(
    $target,
    (($doc | ConvertTo-Json -Depth 100) + "`n"),
    [Text.UTF8Encoding]::new($false)
)

$readback = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json -AsHashtable -Depth 100
if ([long]$readback.tree.totalBytes -ne $totalBytes -or
    [int]$readback.tree.fileCount -ne $files.Count -or
    [string]$readback.tree.treeSha256 -ne (Tree $files)) {
    throw 'Core v2.9.1 checkpoint readback mismatch.'
}

[ordered]@{
    status = 'passed'
    checkpoint = $targetRelative
    sha256 = Sha $target
    invalidatedCheckpoint = [ordered]@{ path = $invalidPredecessorRelative; sha256 = Sha $invalidPredecessor }
    tree = $readback.tree
    runtime = $readback.core
} | ConvertTo-Json -Depth 12

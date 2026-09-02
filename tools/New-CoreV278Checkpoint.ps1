[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative = 'contracts/core/v2.7/checkpoints/v2.7.7-20260831T100400Z/checkpoint.json'
$targetRelative = 'contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json'
$playbackRelative = 'contracts/authority/core-workflows/v2.3.0-20260828T030800Z/checkpoint.json'
$source = Join-Path $ProjectRoot $sourceRelative
$target = Join-Path $ProjectRoot $targetRelative
$playback = Join-Path $ProjectRoot $playbackRelative
foreach ($required in @($source, $playback)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing checkpoint input: $required" } }
if (Test-Path -LiteralPath $target) { throw "Append-only checkpoint already exists: $target" }

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$document = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json -AsHashtable -Depth 100
if ($document.schemaVersion -ne 'knowledge-clipper-core-integration-checkpoint.v2' -or $document.checkpointVersion -ne '2.7.7') { throw 'Unexpected source Core checkpoint.' }

$totalBytes = [long]0
$rows = [string[]]@(foreach ($file in $document.tree.files) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $ProjectRoot ([string]$file.path)))
    $prefix = $ProjectRoot.TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
        throw "Core checkpoint file is missing or escaped the project root: $($file.path)"
    }
    $actualBytes = (Get-Item -LiteralPath $absolute).Length
    $actualSha = (Get-Sha256 $absolute).ToLowerInvariant()
    if ($actualBytes -ne [long]$file.bytes -or $actualSha -ne ([string]$file.sha256).ToLowerInvariant()) {
        throw "Core checkpoint file drifted: $($file.path)"
    }
    $totalBytes += $actualBytes
    "$($file.path)`t$actualBytes`t$actualSha"
})
[Array]::Sort($rows, [StringComparer]::Ordinal)
$treeText = ($rows -join "`n") + "`n"
$treeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($treeText))).ToLowerInvariant()

$playbackDocument = Get-Content -LiteralPath $playback -Raw | ConvertFrom-Json -Depth 100
if ($playbackDocument.status -ne 'stable' -or $playbackDocument.scope.ipcProtocolVersion -ne '1.1' -or $playbackDocument.scope.receiptSchemaVersion -ne '2.3.0') {
    throw 'Playback-control authority is not the required stable v2.3 checkpoint.'
}

$document.checkpointId = 'core-v2.7.8-20260831T111800Z'
$document.checkpointVersion = '2.7.8'
$document.createdAt = '2026-08-31T11:18:00+08:00'
$document.generatedAt = '2026-08-31T11:18:00+08:00'
$document.supersedes = [ordered]@{ path = $sourceRelative; sha256 = Get-Sha256 $source }
$document.authorityBoundary.currentAuthority = '1.2.20'
$document.authorityBoundary.intendedAuthority = '1.2.23'
$document.authorityBoundary.promotion = 'pending-append-only-authority-assembly'
$document.publicAuthority = [ordered]@{
    playbackControl = [ordered]@{
        checkpointId = $playbackDocument.checkpointId
        path = $playbackRelative
        sha256 = Get-Sha256 $playback
        ipcProtocolVersion = $playbackDocument.scope.ipcProtocolVersion
        receiptSchemaVersion = $playbackDocument.scope.receiptSchemaVersion
    }
}
$document.tree.fileCount = @($document.tree.files).Count
$document.tree.totalBytes = $totalBytes
$document.tree.treeSha256 = $treeSha

[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
$json = $document | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($target, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
[ordered]@{
    status = 'passed'
    checkpoint = $targetRelative
    sha256 = Get-Sha256 $target
    treeSha256 = $treeSha
    files = @($document.tree.files).Count
    bytes = $totalBytes
    playbackControlAuthority = $document.publicAuthority.playbackControl
} | ConvertTo-Json -Depth 20

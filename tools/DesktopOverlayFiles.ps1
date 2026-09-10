#Requires -Version 7.0
# File-only helpers shared by the append-only desktop overlay assembler and gate.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OverlaySha([string]$Path) {
    $stream=[IO.File]::OpenRead($Path)
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
}
function Get-OverlayIdentity([string]$Path) {
    [ordered]@{path=[IO.Path]::GetFullPath($Path);bytes=([IO.FileInfo]::new($Path)).Length;sha256=(Get-OverlaySha $Path)}
}
function Resolve-OverlayFile([string]$Root,[string]$Relative) {
    if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains('\') -or $Relative.Contains(':') -or @($Relative.Split('/')|Where-Object {$_ -in @('','.','..')}).Count){throw 'overlay-path-invalid'}
    $path=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if(!$path.StartsWith([IO.Path]::GetFullPath($Root).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'overlay-path-escape'}
    $path
}
function Test-OverlayFile([string]$Path,$Identity) {
    if(![IO.File]::Exists($Path) -or ([IO.FileInfo]::new($Path)).Length -ne [long]$Identity.bytes -or (Get-OverlaySha $Path) -cne ([string]$Identity.sha256).ToUpperInvariant()){throw ('overlay-file-mismatch: '+$Path)}
}
function Get-OverlayInventory([string]$Root,[string[]]$Exclude=@()) {
    $paths=[string[]]@([IO.Directory]::EnumerateFiles($Root,'*',[IO.SearchOption]::AllDirectories)|ForEach-Object {[IO.Path]::GetRelativePath($Root,$_).Replace('\','/')}|Where-Object {$_ -cnotin $Exclude})
    [Array]::Sort($paths,[StringComparer]::Ordinal)
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $count=0
    foreach($relative in $paths){
        if(!$seen.Add($relative)){throw 'overlay-case-collision'}
        $path=Resolve-OverlayFile $Root $relative
        [ordered]@{path=$relative;bytes=([IO.FileInfo]::new($path)).Length;sha256=(Get-OverlaySha $path)}
        $count++;if($count%500 -eq 0){Write-Host ('INVENTORY '+$count+' '+$Root)}
    }
}
function Get-OverlayTree($Rows) {
    $text=[Text.StringBuilder]::new();$bytes=0L
    foreach($row in $Rows){[void]$text.Append($row.path).Append([char]0).Append($row.bytes).Append([char]0).Append(([string]$row.sha256).ToUpperInvariant()).Append("`n");$bytes += [long]$row.bytes}
    [ordered]@{files=@($Rows).Count;bytes=$bytes;treeSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString())))}
}
function Write-OverlayJson([string]$Path,$Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null
    $stream=[IO.File]::Open($Path,[IO.FileMode]::CreateNew)
    try{$bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 100)+"`n");$stream.Write($bytes)}finally{$stream.Dispose()}
}
function Assert-OverlayManifest([string]$Root,[string]$ManifestPath,[string]$ExpectedSha) {
    if((Get-OverlaySha $ManifestPath) -cne $ExpectedSha){throw 'overlay-manifest-sha-mismatch'}
    $m=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json -Depth 100
    $rows=@(Get-OverlayInventory $Root @('candidate-file-manifest.v1.json'))
    $byPath=@{};foreach($r in $m.files){if($byPath.ContainsKey($r.path)){throw 'duplicate-declared-file'};$byPath[$r.path]=$r}
    if($rows.Count -ne $byPath.Count){throw 'overlay-physical-inventory-mismatch'}
    foreach($r in $rows){if(!$byPath.ContainsKey($r.path) -or $r.bytes -ne $byPath[$r.path].bytes -or $r.sha256 -cne $byPath[$r.path].sha256){throw ('overlay-manifest-file-mismatch: '+$r.path)}}
    $tree=Get-OverlayTree $rows
    if($tree.files -ne $m.payload.files -or $tree.bytes -ne $m.payload.bytes -or $tree.treeSha256 -cne $m.payload.treeSha256){throw 'overlay-tree-mismatch'}
    $m
}

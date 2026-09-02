param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspaceRoot,

    [Parameter(Mandatory = $true)]
    [string]$SourceReceiptPath,

    [string]$ExpectedTreeSha256
)

$ErrorActionPreference = 'Stop'

function Get-Sha256Lower([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$receipt = Get-Content -LiteralPath $SourceReceiptPath -Raw | ConvertFrom-Json -Depth 100
$sourceEntries = @($receipt.sourceSnapshot.entries)
if ($sourceEntries.Count -eq 0) { throw 'Source receipt contains no sourceSnapshot entries.' }

[string[]]$paths = @($sourceEntries | ForEach-Object { ([string]$_.path).Replace('\', '/') })
if (($paths | Select-Object -Unique).Count -ne $paths.Count) { throw 'Source snapshot contains duplicate paths.' }
[Array]::Sort($paths, [StringComparer]::Ordinal)

$entries = [Collections.Generic.List[object]]::new()
[long]$totalBytes = 0
foreach ($relativePath in $paths) {
    if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath.Split('/') -contains '..') {
        throw "Source path must be workspace-relative and traversal-free: $relativePath"
    }
    $absolutePath = [IO.Path]::GetFullPath((Join-Path $workspace $relativePath))
    $prefix = $workspace.TrimEnd('\') + '\'
    if (-not $absolutePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source path escapes workspace: $relativePath"
    }
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) { throw "Source file is missing: $relativePath" }
    $file = Get-Item -LiteralPath $absolutePath
    $sha256 = Get-Sha256Lower $absolutePath
    $declared = $sourceEntries | Where-Object { ([string]$_.path).Replace('\', '/') -ceq $relativePath } | Select-Object -First 1
    if ($null -eq $declared -or [long]$declared.bytes -ne [long]$file.Length -or ([string]$declared.sha256).ToLowerInvariant() -cne $sha256) {
        throw "Source entry bytes/SHA mismatch: $relativePath"
    }
    $entries.Add([ordered]@{ path = $relativePath; bytes = [long]$file.Length; sha256 = $sha256 })
    $totalBytes += [long]$file.Length
}

$builder = [Text.StringBuilder]::new()
foreach ($entry in $entries) {
    [void]$builder.Append([string]$entry.path)
    [void]$builder.Append([char]0)
    [void]$builder.Append(([long]$entry.bytes).ToString([Globalization.CultureInfo]::InvariantCulture))
    [void]$builder.Append([char]0)
    [void]$builder.Append([string]$entry.sha256)
    [void]$builder.Append("`n")
}
$treeBytes = [Text.Encoding]::UTF8.GetBytes($builder.ToString())
$treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($treeBytes)).ToLowerInvariant()

if (-not [string]::IsNullOrWhiteSpace($ExpectedTreeSha256) -and
    $treeSha256 -cne $ExpectedTreeSha256.ToLowerInvariant()) {
    throw "Source snapshot tree mismatch: expected=$($ExpectedTreeSha256.ToLowerInvariant()) actual=$treeSha256"
}

[ordered]@{
    algorithm = 'SHA-256 over UTF-8 records sorted by StringComparer.Ordinal path; each record is path NUL decimal-bytes NUL lowercase-sha256 LF; final record includes LF'
    files = $entries.Count
    bytes = $totalBytes
    treeSha256 = $treeSha256
    entries = $entries
} | ConvertTo-Json -Depth 8

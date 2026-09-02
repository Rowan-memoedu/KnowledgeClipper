[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ReleaseStamp = '20260831T112000Z'
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelease = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.22-20260831T110500Z\win-x64'
$sourceAuthority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.22-20260831T110500Z'
$coreCheckpointRelative = 'contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json'
$coreCheckpoint = Join-Path $ProjectRoot ($coreCheckpointRelative.Replace('/', '\'))
$coreCheckpointSha = '88713A6638BE74587F371287E83ED11056E13CC389F11E052DFCC5C719B5BE0C'
$releaseId = "knowledge-clipper-authority-v1.2.23-$ReleaseStamp"
$releaseContainer = Join-Path $ProjectRoot "releases\$releaseId"
$releaseRoot = Join-Path $releaseContainer 'win-x64'
$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.23-$ReleaseStamp"
$releaseParent = Split-Path -Parent $releaseContainer
$authorityParent = Split-Path -Parent $authorityRoot
$stagingRelease = Join-Path $releaseParent ('.' + $releaseId + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$stagingRoot = Join-Path $stagingRelease 'win-x64'
$stagingAuthority = Join-Path $authorityParent ('.v1.2.23-' + $ReleaseStamp + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Copy-Tree([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in [IO.Directory]::EnumerateDirectories($Source, '*', [IO.SearchOption]::AllDirectories)) {
        [IO.Directory]::CreateDirectory((Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $directory)))) | Out-Null
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        [IO.File]::Copy($file, (Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $file))), $false)
    }
}
function Get-Tree([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse)
    $rows = [string[]]@(foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        "$relative`t$($file.Length)`t$((Get-Sha256 $file.FullName).ToLowerInvariant())"
    })
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    [ordered]@{ files = $files.Count; bytes = [long](($files | Measure-Object Length -Sum).Sum); treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
}
function Write-Json([string]$Path, [object]$Value) { [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false)) }
function Remove-Staging([string]$Path, [string]$Parent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = [IO.Path]::GetFullPath($Path); $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('.')) { throw "Refusing staging cleanup: $resolved" }
    [IO.Directory]::Delete($resolved, $true)
}

foreach ($required in @($sourceRelease, (Join-Path $sourceAuthority 'worker-registry.v1.json'), $coreCheckpoint)) { if (-not (Test-Path -LiteralPath $required)) { throw "Missing v1.2.23 input: $required" } }
if ((Get-Sha256 $coreCheckpoint) -ne $coreCheckpointSha) { throw 'Core v2.7.8 checkpoint drifted.' }
foreach ($target in @($releaseContainer, $authorityRoot)) { if (Test-Path -LiteralPath $target) { throw "Append-only target exists: $target" } }

try {
    Copy-Tree $sourceRelease $stagingRoot
    $releaseCheckpoint = Join-Path $stagingRoot ($coreCheckpointRelative.Replace('/', '\'))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $releaseCheckpoint)) | Out-Null
    [IO.File]::Copy($coreCheckpoint, $releaseCheckpoint, $false)
    [IO.Directory]::CreateDirectory($stagingAuthority) | Out-Null

    $registry = Get-Content -LiteralPath (Join-Path $sourceAuthority 'worker-registry.v1.json') -Raw | ConvertFrom-Json -Depth 50
    if ($registry.registryVersion -ne '1.2.22' -or @($registry.workers).Count -ne 5) { throw 'Unexpected source registry.' }
    $registry.registryVersion = '1.2.23'
    Write-Json (Join-Path $stagingAuthority 'worker-registry.v1.json') $registry

    $tree = Get-Tree $stagingRoot
    $dependency = [ordered]@{
        schemaVersion = 'knowledge-clipper-release-dependency-manifest.v1'
        releaseId = $releaseId
        releaseRoot = "releases/$releaseId/win-x64"
        files = $tree.files
        bytes = $tree.bytes
        treeHashAlgorithm = 'sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF; SHA-256 UTF-8'
        treeSha256 = $tree.treeSha256
        keyFiles = @([ordered]@{ path = $coreCheckpointRelative; bytes = (Get-Item -LiteralPath $releaseCheckpoint).Length; sha256 = Get-Sha256 $releaseCheckpoint })
        workerOperations = $null
        legacyEntrypointsExcluded = $true
        baseAuthority = [ordered]@{ registryVersion = '1.2.20'; checkpointSha256 = '9644D9FD80EB29A57D4B16ED5FCD1FE16475D8101B2C93DF1B21B7530C5F7509' }
        coreIntegration = [ordered]@{ checkpoint = $coreCheckpointRelative; checkpointSha256 = $coreCheckpointSha; version = '2.7.8'; coreVersion = '2.7.4' }
    }
    Write-Json (Join-Path $stagingAuthority 'dependency-manifest.v1.json') $dependency

    $orchestrator = Join-Path $stagingRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
    $validationText = (& $orchestrator validate-registry --registry (Join-Path $stagingAuthority 'worker-registry.v1.json') --release-root $stagingRoot 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "validate-registry failed: $validationText" }
    $validation = $validationText | ConvertFrom-Json
    if ($validation.status -ne 'valid' -or $validation.registryVersion -ne '1.2.23' -or $validation.workers -ne 5) { throw 'Unexpected registry validation.' }

    [IO.Directory]::Move($stagingRelease, $releaseContainer)
    [IO.Directory]::Move($stagingAuthority, $authorityRoot)
    [ordered]@{
        status = 'candidate-built'
        registryVersion = '1.2.23'
        releaseRoot = $releaseRoot
        authorityRoot = $authorityRoot
        release = $tree
        registrySha256 = Get-Sha256 (Join-Path $authorityRoot 'worker-registry.v1.json')
        dependencyManifestSha256 = Get-Sha256 (Join-Path $authorityRoot 'dependency-manifest.v1.json')
        coreCheckpointSha256 = $coreCheckpointSha
        validateRegistry = $validation
        workerRuns = 0
        externalWrites = 0
    } | ConvertTo-Json -Depth 20
}
finally { Remove-Staging $stagingRelease $releaseParent; Remove-Staging $stagingAuthority $authorityParent }

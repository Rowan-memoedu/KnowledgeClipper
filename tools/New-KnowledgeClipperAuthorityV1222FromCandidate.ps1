[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ReleaseStamp = '20260831T110500Z'
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelease = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.21-20260831T105000Z\win-x64'
$sourceAuthority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.21-20260831T105000Z'
$releaseId = "knowledge-clipper-authority-v1.2.22-$ReleaseStamp"
$releaseContainer = Join-Path $ProjectRoot "releases\$releaseId"
$releaseRoot = Join-Path $releaseContainer 'win-x64'
$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.22-$ReleaseStamp"
$releaseParent = Split-Path -Parent $releaseContainer
$authorityParent = Split-Path -Parent $authorityRoot
$stagingRelease = Join-Path $releaseParent ('.' + $releaseId + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$stagingRoot = Join-Path $stagingRelease 'win-x64'
$stagingAuthority = Join-Path $authorityParent ('.v1.2.22-' + $ReleaseStamp + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')

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
    [ordered]@{
        files = $files.Count
        bytes = [long](($files | Measure-Object Length -Sum).Sum)
        treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
}

function Write-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}

function Remove-VerifiedStaging([string]$Path, [string]$ExpectedParent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('.')) {
        throw "Refusing to remove unexpected staging directory: $resolved"
    }
    [IO.Directory]::Delete($resolved, $true)
}

foreach ($required in @($sourceRelease, (Join-Path $sourceAuthority 'worker-registry.v1.json'), (Join-Path $sourceAuthority 'dependency-manifest.v1.json'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required candidate input is missing: $required" }
}
foreach ($target in @($releaseContainer, $authorityRoot)) {
    if (Test-Path -LiteralPath $target) { throw "Append-only target already exists: $target" }
}

try {
    Copy-Tree $sourceRelease $stagingRoot
    [IO.Directory]::CreateDirectory($stagingAuthority) | Out-Null

    $registry = Get-Content -LiteralPath (Join-Path $sourceAuthority 'worker-registry.v1.json') -Raw | ConvertFrom-Json -Depth 50
    if ($registry.registryVersion -ne '1.2.21' -or @($registry.workers).Count -ne 5) { throw 'Source registry is not the expected v1.2.21 five-worker candidate.' }
    $registry.registryVersion = '1.2.22'
    Write-Json (Join-Path $stagingAuthority 'worker-registry.v1.json') $registry

    $tree = Get-Tree $stagingRoot
    $sourceDependency = Get-Content -LiteralPath (Join-Path $sourceAuthority 'dependency-manifest.v1.json') -Raw | ConvertFrom-Json -Depth 50
    if ($tree.files -ne [int]$sourceDependency.files -or $tree.bytes -ne [long]$sourceDependency.bytes -or $tree.treeSha256 -ne $sourceDependency.treeSha256) {
        throw 'Source candidate release tree drifted before v1.2.22 assembly.'
    }
    $dependency = [ordered]@{
        schemaVersion = 'knowledge-clipper-release-dependency-manifest.v1'
        releaseId = $releaseId
        releaseRoot = "releases/$releaseId/win-x64"
        files = $tree.files
        bytes = $tree.bytes
        treeHashAlgorithm = $sourceDependency.treeHashAlgorithm
        treeSha256 = $tree.treeSha256
        keyFiles = @()
        workerOperations = $null
        legacyEntrypointsExcluded = $true
        baseAuthority = [ordered]@{
            registryVersion = '1.2.20'
            checkpointSha256 = '9644D9FD80EB29A57D4B16ED5FCD1FE16475D8101B2C93DF1B21B7530C5F7509'
            sourceCandidate = '1.2.21-invalidated-never-promoted'
        }
    }
    Write-Json (Join-Path $stagingAuthority 'dependency-manifest.v1.json') $dependency

    $orchestrator = Join-Path $stagingRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
    $validationText = (& $orchestrator validate-registry --registry (Join-Path $stagingAuthority 'worker-registry.v1.json') --release-root $stagingRoot 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "validate-registry failed: $validationText" }
    $validation = $validationText | ConvertFrom-Json
    if ($validation.status -ne 'valid' -or $validation.registryVersion -ne '1.2.22' -or $validation.workers -ne 5) { throw 'validate-registry returned an unexpected result.' }

    [IO.Directory]::Move($stagingRelease, $releaseContainer)
    [IO.Directory]::Move($stagingAuthority, $authorityRoot)
    [ordered]@{
        schemaVersion = 'knowledge-clipper-authority-reassembly-receipt.v1'
        status = 'candidate-built'
        registryVersion = '1.2.22'
        releaseRoot = $releaseRoot
        authorityRoot = $authorityRoot
        release = $tree
        registrySha256 = Get-Sha256 (Join-Path $authorityRoot 'worker-registry.v1.json')
        dependencyManifestSha256 = Get-Sha256 (Join-Path $authorityRoot 'dependency-manifest.v1.json')
        validateRegistry = $validation
        externalWrites = 0
        workerRuns = 0
    } | ConvertTo-Json -Depth 20
}
finally {
    Remove-VerifiedStaging $stagingRelease $releaseParent
    Remove-VerifiedStaging $stagingAuthority $authorityParent
}

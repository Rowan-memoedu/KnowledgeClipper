param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$CoreContractRoot,
    [Parameter(Mandatory = $true)][string]$CoreCheckpointPath,
    [Parameter(Mandatory = $true)][string]$ExpectedCoreCheckpointSha256,
    [Parameter(Mandatory = $true)][string]$OrchestratorPublishRoot,
    [Parameter(Mandatory = $true)][string]$ExpectedOrchestratorVersion,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$CoreContractRoot = [IO.Path]::GetFullPath($CoreContractRoot)
$CoreCheckpointPath = [IO.Path]::GetFullPath($CoreCheckpointPath)
$OrchestratorPublishRoot = [IO.Path]::GetFullPath($OrchestratorPublishRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$ExpectedCoreCheckpointSha256 = $ExpectedCoreCheckpointSha256.ToUpperInvariant()

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Read-Json([string]$Path, [string]$Description) {
    try { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100 }
    catch { throw "$Description is not valid JSON: $Path" }
}

function Write-Utf8Json([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Get-ProjectRelativePath([string]$Path, [string]$Description) {
    $full = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath($ProjectRoot, $full).Replace('\', '/')
    if ($relative -eq '..' -or $relative.StartsWith('../', [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
        throw "$Description is outside ProjectRoot: $full"
    }
    $relative
}

function Get-JsonSchemaReferences([object]$Node) {
    if ($null -eq $Node) { return }
    if ($Node -is [Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            $value = $Node[$key]
            if ($key -eq '$ref' -and $value -is [string]) { Write-Output $value }
            Get-JsonSchemaReferences $value
        }
        return
    }
    if ($Node -is [Collections.IEnumerable] -and -not ($Node -is [string])) {
        foreach ($value in $Node) { Get-JsonSchemaReferences $value }
        return
    }
    foreach ($property in $Node.PSObject.Properties) {
        if ($property.Name -eq '$ref' -and $property.Value -is [string]) { Write-Output $property.Value }
        Get-JsonSchemaReferences $property.Value
    }
}

function Get-LocalSchemaDependencyClosure([object[]]$Seeds) {
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($seed in $Seeds) {
        $path = [string]$seed.path
        $known.Add($path) | Out-Null
        $seedPaths.Add($path) | Out-Null
        $queue.Enqueue($path)
    }
    $dependencies = @()
    while ($queue.Count -gt 0) {
        $sourceRelative = $queue.Dequeue()
        $sourcePath = Join-Path $ProjectRoot ($sourceRelative.Replace('/', '\'))
        $document = Read-Json $sourcePath 'Core schema dependency document'
        foreach ($reference in @(Get-JsonSchemaReferences $document)) {
            $fragmentOffset = $reference.IndexOf('#')
            $referenceFile = if ($fragmentOffset -ge 0) { $reference.Substring(0, $fragmentOffset) } else { $reference }
            if ([string]::IsNullOrWhiteSpace($referenceFile) -or $referenceFile -match '^[A-Za-z][A-Za-z0-9+.-]*:') { continue }
            $candidate = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $sourcePath) $referenceFile))
            $candidateRelative = Get-ProjectRelativePath $candidate 'Core schema dependency'
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Core schema dependency is missing: $candidateRelative" }
            if (-not $known.Add($candidateRelative)) { continue }
            $dependency = [ordered]@{
                path = $candidateRelative
                bytes = [long](Get-Item -LiteralPath $candidate).Length
                sha256 = Get-Sha256 $candidate
                referencedBy = $sourceRelative
            }
            if (-not $seedPaths.Contains($candidateRelative)) { $dependencies += $dependency }
            $queue.Enqueue($candidateRelative)
        }
    }
    @($dependencies | Sort-Object path)
}

function Get-DirectoryInventory([string]$Root) {
    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    if ((Get-Item -LiteralPath $resolvedRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Runtime payload root is a reparse point: $resolvedRoot"
    }
    $files = @(Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse -Force)
    $relativePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $records = @()
    $rows = foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Runtime payload contains a reparse-point file: $($file.FullName)"
        }
        $relative = [IO.Path]::GetRelativePath($resolvedRoot, $file.FullName).Replace('\', '/')
        if ($relative.StartsWith('../', [StringComparison]::Ordinal) -or -not $relativePaths.Add($relative)) {
            throw "Runtime payload path escaped or collided: $relative"
        }
        $sha256 = Get-Sha256 $file.FullName
        $records += [ordered]@{ path = $relative; bytes = [long]$file.Length; sha256 = $sha256 }
        "$relative`t$($file.Length)`t$($sha256.ToLowerInvariant())"
    }
    $rows = [string[]]$rows
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    [ordered]@{
        fileCount = $files.Count
        totalBytes = [long](($files | Measure-Object Length -Sum).Sum)
        treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        files = @($records | Sort-Object path)
    }
}

foreach ($required in @($ProjectRoot, $CoreContractRoot, $CoreCheckpointPath, $OrchestratorPublishRoot)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required authority input is missing: $required" }
}
if (Test-Path -LiteralPath $OutputDirectory) { throw "Append-only output already exists: $OutputDirectory" }
if ((Get-Sha256 $CoreCheckpointPath) -ne $ExpectedCoreCheckpointSha256) { throw 'Core checkpoint hash drifted.' }

$checkpoint = Read-Json $CoreCheckpointPath 'Core checkpoint'
if ($checkpoint.schemaVersion -ne 'knowledge-clipper-core-checkpoint.v1' -or -not ([string]$checkpoint.status).StartsWith('passed', [StringComparison]::Ordinal)) {
    throw 'Core checkpoint identity/status is invalid.'
}
$checkpointRelative = Get-ProjectRelativePath $CoreCheckpointPath 'Core checkpoint'
$contractRootRelative = Get-ProjectRelativePath $CoreContractRoot 'Core contract root'
$bindingPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$packageBindings = @()
foreach ($binding in @($checkpoint.fileBindings)) {
    $relative = [string]$binding.path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative.Contains('..') -or -not $bindingPaths.Add($relative)) {
        throw "Invalid or duplicate checkpoint binding: $relative"
    }
    $source = Join-Path $ProjectRoot ($relative.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Sha256 $source) -ne ([string]$binding.sha256).ToUpperInvariant()) {
        throw "Checkpoint binding drifted: $relative"
    }
    if (($relative + '/').StartsWith($contractRootRelative.TrimEnd('/') + '/', [StringComparison]::Ordinal)) { $packageBindings += $binding }
}
if ($packageBindings.Count -eq 0) { throw 'Checkpoint binds no files inside CoreContractRoot.' }
$dependencies = @(Get-LocalSchemaDependencyClosure $packageBindings)

$inventory = Get-DirectoryInventory $OrchestratorPublishRoot
$requiredRuntimeFiles = @(
    'KnowledgeClipper.Orchestrator.exe',
    'KnowledgeClipper.Orchestrator.dll',
    'KnowledgeClipper.Orchestrator.deps.json',
    'KnowledgeClipper.Orchestrator.runtimeconfig.json')
$runtimePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in @($inventory.files)) { $runtimePaths.Add([string]$record.path) | Out-Null }
foreach ($required in $requiredRuntimeFiles) {
    if (-not $runtimePaths.Contains($required)) { throw "Runtime payload is missing: $required" }
}
$runtimeConfig = Read-Json (Join-Path $OrchestratorPublishRoot 'KnowledgeClipper.Orchestrator.runtimeconfig.json') 'Orchestrator runtimeconfig'
if (@($runtimeConfig.runtimeOptions.includedFrameworks).Count -eq 0) { throw 'Runtime payload is not self-contained.' }
$deps = Read-Json (Join-Path $OrchestratorPublishRoot 'KnowledgeClipper.Orchestrator.deps.json') 'Orchestrator deps'
if (-not ([string]$deps.runtimeTarget.name).EndsWith('/win-x64', [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime payload is not win-x64.' }
$orchestratorPath = Join-Path $OrchestratorPublishRoot 'KnowledgeClipper.Orchestrator.exe'
$describeOutput = & $orchestratorPath describe 2>&1
if ($LASTEXITCODE -ne 0) { throw "Orchestrator describe failed: $($describeOutput -join [Environment]::NewLine)" }
$describe = ($describeOutput -join [Environment]::NewLine) | ConvertFrom-Json
$actualVersion = [string]$describe.version
if ([string]::IsNullOrWhiteSpace($actualVersion)) { $actualVersion = [string]$describe.component.version }
if ($actualVersion -ne $ExpectedOrchestratorVersion) { throw "Orchestrator version mismatch: expected=$ExpectedOrchestratorVersion actual=$actualVersion" }

$schemaManifest = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-schema-closure-manifest.v1'
    checkpoint = [ordered]@{ path = $checkpointRelative; sha256 = $ExpectedCoreCheckpointSha256 }
    dependencies = @($dependencies)
}
$runtimeManifest = [ordered]@{
    schemaVersion = 'knowledge-clipper-orchestrator-runtime-manifest.v1'
    checkpoint = [ordered]@{ path = $checkpointRelative; sha256 = $ExpectedCoreCheckpointSha256 }
    component = [ordered]@{ id = 'knowledge-clipper.orchestrator'; version = $ExpectedOrchestratorVersion }
    selfContained = $true
    runtimeIdentifier = 'win-x64'
    entrypoint = 'KnowledgeClipper.Orchestrator.exe'
    treeAlgorithm = 'sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)'
    fileCount = $inventory.fileCount
    totalBytes = $inventory.totalBytes
    treeSha256 = $inventory.treeSha256
    files = $inventory.files
}

$parent = Split-Path -Parent $OutputDirectory
[IO.Directory]::CreateDirectory($parent) | Out-Null
$staging = Join-Path $parent ('.' + [IO.Path]::GetFileName($OutputDirectory) + '.staging-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    $schemaPath = Join-Path $staging 'schema-closure-manifest.v1.json'
    $runtimePath = Join-Path $staging 'orchestrator-runtime-manifest.v1.json'
    Write-Utf8Json $schemaPath $schemaManifest
    Write-Utf8Json $runtimePath $runtimeManifest
    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-authority-v3-input-generation-receipt.v1'
        status = 'generated'
        generatedAt = [DateTime]::UtcNow.ToString('o')
        checkpoint = [ordered]@{ path = $checkpointRelative; sha256 = $ExpectedCoreCheckpointSha256; boundFiles = @($checkpoint.fileBindings).Count }
        schemaClosure = [ordered]@{ file = 'schema-closure-manifest.v1.json'; sha256 = Get-Sha256 $schemaPath; dependencies = $dependencies.Count }
        runtime = [ordered]@{ file = 'orchestrator-runtime-manifest.v1.json'; sha256 = Get-Sha256 $runtimePath; files = $inventory.fileCount; totalBytes = $inventory.totalBytes; treeSha256 = $inventory.treeSha256 }
        externalWrites = 0
    }
    Write-Utf8Json (Join-Path $staging 'input-generation.receipt.json') $receipt
    [IO.Directory]::Move($staging, $OutputDirectory)
}
finally {
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = [IO.Path]::GetFullPath($staging)
        $resolvedParent = [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\'
        if (-not $resolvedStaging.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolvedStaging)).StartsWith('.')) {
            throw "Refusing to remove unexpected staging directory: $resolvedStaging"
        }
        [IO.Directory]::Delete($resolvedStaging, $true)
    }
}

Get-Content -LiteralPath (Join-Path $OutputDirectory 'input-generation.receipt.json') -Raw

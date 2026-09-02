[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseReleaseRoot,
    [Parameter(Mandatory = $true)][string]$BaseAuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$NewRegistryVersion,
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [Parameter(Mandatory = $true)][string]$OrchestratorPublishRoot,
    [Parameter(Mandatory = $true)][string]$OrchestratorRuntimeManifestPath,
    [Parameter(Mandatory = $true)][string]$ExpectedOrchestratorRuntimeManifestSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedOrchestratorVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedOrchestratorSha256,
    [Parameter(Mandatory = $true)][string]$CoreContractRoot,
    [Parameter(Mandatory = $true)][string]$CoreCheckpointPath,
    [Parameter(Mandatory = $true)][string]$ExpectedCoreCheckpointSha256,
    [string]$ExpectedIpcProtocolVersion,
    [string]$ExpectedRequestContract,
    [string]$ExpectedReceiptContract,
    [switch]$PreflightOnly,
    [switch]$StagingValidationOnly,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$BaseReleaseRoot = [IO.Path]::GetFullPath($BaseReleaseRoot)
$BaseAuthorityDirectory = [IO.Path]::GetFullPath($BaseAuthorityDirectory)
$OrchestratorPublishRoot = [IO.Path]::GetFullPath($OrchestratorPublishRoot)
$OrchestratorRuntimeManifestPath = [IO.Path]::GetFullPath($OrchestratorRuntimeManifestPath)
$CoreContractRoot = [IO.Path]::GetFullPath($CoreContractRoot)
$CoreCheckpointPath = [IO.Path]::GetFullPath($CoreCheckpointPath)
$releaseId = "knowledge-clipper-authority-v$NewRegistryVersion-$ReleaseStamp"
$releaseParent = Join-Path $ProjectRoot 'releases'
$authorityParent = Join-Path $ProjectRoot 'contracts\authority\worker-registry'
$finalReleaseContainer = Join-Path $releaseParent $releaseId
$finalReleaseRoot = Join-Path $finalReleaseContainer 'win-x64'
$finalAuthorityDirectory = Join-Path $authorityParent ("v$NewRegistryVersion-$ReleaseStamp")
$stagingReleaseContainer = Join-Path $releaseParent ('.' + $releaseId + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$stagingReleaseRoot = Join-Path $stagingReleaseContainer 'win-x64'
$stagingAuthorityDirectory = Join-Path $authorityParent ('.v' + $NewRegistryVersion + '-' + $ReleaseStamp + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')

if ($PreflightOnly -and $StagingValidationOnly) {
    throw 'PreflightOnly and StagingValidationOnly cannot be combined.'
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Get-ProjectRelativePath([string]$Path, [string]$Description) {
    $relative = [IO.Path]::GetRelativePath($ProjectRoot, [IO.Path]::GetFullPath($Path)).Replace('\', '/')
    if ($relative -eq '..' -or $relative.StartsWith('../', [StringComparison]::Ordinal)) {
        throw "$Description must be inside the project root: $Path"
    }
    $relative
}

function Assert-ExpectedValue([string]$Expected, [string]$Actual, [string]$Description) {
    if (-not [string]::IsNullOrWhiteSpace($Expected) -and $Expected -ne $Actual) {
        throw "$Description mismatch: expected=$Expected actual=$Actual"
    }
}

function Read-Json([string]$Path, [string]$Description) {
    try {
        Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100
    }
    catch {
        throw "$Description is not valid JSON: $Path"
    }
}

function Write-Json([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 40
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
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
                bytes = (Get-Item -LiteralPath $candidate).Length
                sha256 = Get-Sha256 $candidate
                referencedBy = $sourceRelative
            }
            if (-not $seedPaths.Contains($candidateRelative)) { $dependencies += $dependency }
            $queue.Enqueue($candidateRelative)
        }
    }
    @($dependencies | Sort-Object path)
}

function Copy-Directory([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in [IO.Directory]::EnumerateDirectories($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($Source, $directory)
        [IO.Directory]::CreateDirectory((Join-Path $Destination $relative)) | Out-Null
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($Source, $file)
        [IO.File]::Copy($file, (Join-Path $Destination $relative), $false)
    }
}

function Get-DirectoryInventory([string]$Root) {
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $directories = @(Get-ChildItem -LiteralPath $resolvedRoot -Directory -Recurse -Force)
    foreach ($directory in $directories) {
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Runtime payload contains a reparse-point directory: $($directory.FullName)"
        }
    }
    $files = @(Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse -Force)
    $records = @()
    $relativePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $rows = foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Runtime payload contains a reparse-point file: $($file.FullName)"
        }
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
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

function Get-ReleaseTree([string]$Root) {
    $inventory = Get-DirectoryInventory $Root
    [ordered]@{ files = $inventory.fileCount; bytes = $inventory.totalBytes; treeSha256 = $inventory.treeSha256 }
}

function Remove-VerifiedStagingDirectory([string]$Path, [string]$ExpectedParent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('.')) {
        throw "Refusing to remove unexpected staging directory: $resolved"
    }
    [IO.Directory]::Delete($resolved, $true)
}

foreach ($required in @(
    $BaseReleaseRoot,
    (Join-Path $BaseAuthorityDirectory 'worker-registry.v1.json'),
    (Join-Path $BaseAuthorityDirectory 'dependency-manifest.v1.json'),
    $OrchestratorPublishRoot,
    $OrchestratorRuntimeManifestPath,
    $CoreContractRoot,
    $CoreCheckpointPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required Core authority input is missing: $required" }
}
foreach ($target in @($finalReleaseContainer, $finalAuthorityDirectory)) {
    if (Test-Path -LiteralPath $target) { throw "Append-only target already exists: $target" }
}
if ((Get-Sha256 $CoreCheckpointPath) -ne $ExpectedCoreCheckpointSha256) { throw 'Core workflow checkpoint hash drifted.' }

$coreCheckpoint = Read-Json $CoreCheckpointPath 'Core workflow checkpoint'
if ($coreCheckpoint.schemaVersion -ne 'knowledge-clipper-core-checkpoint.v1') { throw 'Unsupported Core checkpoint schema.' }
if ([string]::IsNullOrWhiteSpace($coreCheckpoint.checkpointVersion)) { throw 'Core checkpoint version is missing.' }
if ([string]::IsNullOrWhiteSpace($coreCheckpoint.status) -or -not $coreCheckpoint.status.StartsWith('passed', [StringComparison]::Ordinal)) {
    throw "Core checkpoint did not pass: $($coreCheckpoint.status)"
}

$contractRootRelative = Get-ProjectRelativePath $CoreContractRoot 'Core contract root'
if ($contractRootRelative -notmatch '^contracts/core/v[^/]+$') {
    throw "Core contract root is not a versioned contracts/core subtree: $contractRootRelative"
}
$contractRootVersion = $contractRootRelative.Split('/')[-1].Substring(1)
if (-not ([string]$coreCheckpoint.checkpointVersion -eq $contractRootVersion -or
    ([string]$coreCheckpoint.checkpointVersion).StartsWith($contractRootVersion + '.', [StringComparison]::Ordinal))) {
    throw "Core checkpoint version does not match its contract root: checkpoint=$($coreCheckpoint.checkpointVersion) root=$contractRootVersion"
}
$checkpointRelative = Get-ProjectRelativePath $CoreCheckpointPath 'Core checkpoint'
if (-not ($checkpointRelative + '/').StartsWith($contractRootRelative.TrimEnd('/') + '/', [StringComparison]::Ordinal)) {
    throw "Core checkpoint is outside the supplied Core contract root: $checkpointRelative"
}

$ipc = $coreCheckpoint.authority.ipc
if ($null -eq $ipc) { throw 'Core checkpoint authority.ipc metadata is missing.' }
$ipcProtocolVersion = [string]$ipc.protocolVersion
$ipcSchemaVersion = [string]$ipc.schemaVersion
$ipcConfigurationRelative = [string]$ipc.configuration
$requestContract = [string]$ipc.request
$receiptContract = [string]$ipc.receipt
foreach ($requiredValue in @(
    @{ name = 'authority.ipc.protocolVersion'; value = $ipcProtocolVersion },
    @{ name = 'authority.ipc.schemaVersion'; value = $ipcSchemaVersion },
    @{ name = 'authority.ipc.configuration'; value = $ipcConfigurationRelative },
    @{ name = 'authority.ipc.request'; value = $requestContract },
    @{ name = 'authority.ipc.receipt'; value = $receiptContract })) {
    if ([string]::IsNullOrWhiteSpace($requiredValue.value)) { throw "Core checkpoint $($requiredValue.name) is missing." }
}
Assert-ExpectedValue $ExpectedIpcProtocolVersion $ipcProtocolVersion 'IPC protocol version'
Assert-ExpectedValue $ExpectedRequestContract $requestContract 'Module-action request contract'
Assert-ExpectedValue $ExpectedReceiptContract $receiptContract 'Module-action receipt contract'

$bindings = @($coreCheckpoint.fileBindings)
if ($bindings.Count -eq 0) { throw 'Core checkpoint fileBindings are missing.' }
$bindingPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$packageBindings = @()
foreach ($binding in $bindings) {
    $bindingPath = [string]$binding.path
    $bindingSha256 = [string]$binding.sha256
    if ([string]::IsNullOrWhiteSpace($bindingPath) -or $bindingPath.StartsWith('/') -or $bindingPath.Contains('..') -or
        $bindingSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or -not $bindingPaths.Add($bindingPath)) {
        throw "Invalid or duplicate Core checkpoint file binding: $bindingPath"
    }
    $source = Join-Path $ProjectRoot ($bindingPath.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Sha256 $source) -ne $bindingSha256) {
        throw "Core checkpoint file binding drifted: $bindingPath"
    }
    if (($bindingPath + '/').StartsWith($contractRootRelative.TrimEnd('/') + '/', [StringComparison]::Ordinal)) {
        $packageBindings += $binding
    }
}
if ($packageBindings.Count -eq 0) { throw 'Core checkpoint binds no files inside the supplied Core contract root.' }
if (-not $bindingPaths.Contains($ipcConfigurationRelative)) { throw 'IPC configuration is not checkpoint-bound.' }

$ipcConfigurationPath = Join-Path $ProjectRoot ($ipcConfigurationRelative.Replace('/', '\'))
if (-not (($ipcConfigurationRelative + '/').StartsWith($contractRootRelative.TrimEnd('/') + '/', [StringComparison]::Ordinal))) {
    throw 'IPC configuration is outside the supplied Core contract root.'
}
$ipcConfiguration = Read-Json $ipcConfigurationPath 'IPC configuration'
if ([string]$ipcConfiguration.schemaVersion -ne $ipcSchemaVersion) {
    throw "IPC configuration schemaVersion drifted: expected=$ipcSchemaVersion actual=$($ipcConfiguration.schemaVersion)"
}
$ipcSchemaDocuments = @{}
foreach ($schemaReference in @([string]$ipcConfiguration.requestSchema, [string]$ipcConfiguration.responseSchema)) {
    if ([string]::IsNullOrWhiteSpace($schemaReference)) { throw 'IPC configuration schema reference is missing.' }
    $schemaPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ipcConfigurationPath) $schemaReference))
    $schemaRelative = Get-ProjectRelativePath $schemaPath 'IPC schema'
    if (-not $bindingPaths.Contains($schemaRelative)) { throw "IPC schema is not checkpoint-bound: $schemaRelative" }
    $ipcSchemaDocuments[$schemaReference] = Read-Json $schemaPath 'IPC schema'
}
if ([string]$ipcSchemaDocuments[[string]$ipcConfiguration.requestSchema].'$id' -ne "knowledge-clipper-ipc-request.v$ipcProtocolVersion") {
    throw 'IPC request schema identity does not match the checkpoint protocolVersion.'
}
if ([string]$ipcSchemaDocuments[[string]$ipcConfiguration.responseSchema].'$id' -ne "knowledge-clipper-ipc-response.v$ipcProtocolVersion") {
    throw 'IPC response schema identity does not match the checkpoint protocolVersion.'
}

$requestContractBinding = $null
$receiptContractBinding = $null
foreach ($binding in $packageBindings) {
    if (-not ([string]$binding.path).EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $document = Read-Json (Join-Path $ProjectRoot (([string]$binding.path).Replace('/', '\'))) 'Core contract document'
    if ([string]$document.'$id' -eq $requestContract) { $requestContractBinding = $binding }
    if ([string]$document.'$id' -eq $receiptContract) { $receiptContractBinding = $binding }
}
if ($null -eq $requestContractBinding) { throw "Checkpoint-bound request contract schema was not found: $requestContract" }
if ($null -eq $receiptContractBinding) { throw "Checkpoint-bound receipt contract schema was not found: $receiptContract" }
$schemaDependencyFiles = Get-LocalSchemaDependencyClosure $packageBindings

$baseRegistryPath = Join-Path $BaseAuthorityDirectory 'worker-registry.v1.json'
$baseDependencyPath = Join-Path $BaseAuthorityDirectory 'dependency-manifest.v1.json'
$baseRegistryPreflight = Read-Json $baseRegistryPath 'Base authority registry'
$baseDependencyPreflight = Read-Json $baseDependencyPath 'Base authority dependency manifest'
if ([string]::IsNullOrWhiteSpace([string]$baseRegistryPreflight.registryVersion)) { throw 'Base authority registryVersion is missing.' }
if ([string]::IsNullOrWhiteSpace([string]$baseDependencyPreflight.releaseId) -or
    [string]::IsNullOrWhiteSpace([string]$baseDependencyPreflight.treeHashAlgorithm) -or
    [string]::IsNullOrWhiteSpace([string]$baseDependencyPreflight.treeSha256)) {
    throw 'Base authority dependency identity/tree metadata is incomplete.'
}

if ((Get-Sha256 $OrchestratorRuntimeManifestPath) -ne $ExpectedOrchestratorRuntimeManifestSha256) {
    throw 'Orchestrator runtime manifest hash drifted.'
}
$manifestRelativeToPublishRoot = [IO.Path]::GetRelativePath($OrchestratorPublishRoot, $OrchestratorRuntimeManifestPath).Replace('\', '/')
if (-not ($manifestRelativeToPublishRoot -eq '..' -or $manifestRelativeToPublishRoot.StartsWith('../', [StringComparison]::Ordinal))) {
    throw 'Orchestrator runtime manifest must be outside the publish payload root.'
}
$runtimeManifest = Read-Json $OrchestratorRuntimeManifestPath 'Orchestrator runtime manifest'
if ($runtimeManifest.schemaVersion -ne 'knowledge-clipper-orchestrator-runtime-manifest.v1') { throw 'Unsupported Orchestrator runtime manifest schema.' }
if ([string]$runtimeManifest.checkpoint.path -ne $checkpointRelative -or
    [string]$runtimeManifest.checkpoint.sha256 -ne $ExpectedCoreCheckpointSha256) {
    throw 'Orchestrator runtime manifest is bound to a different Core checkpoint.'
}
if ([string]$runtimeManifest.component.id -ne 'knowledge-clipper.orchestrator' -or
    [string]$runtimeManifest.component.version -ne $ExpectedOrchestratorVersion) {
    throw 'Orchestrator runtime manifest component identity/version mismatch.'
}
if ($runtimeManifest.selfContained -ne $true -or [string]$runtimeManifest.runtimeIdentifier -ne 'win-x64') {
    throw 'Orchestrator runtime manifest must declare a self-contained win-x64 payload.'
}
if ([string]$runtimeManifest.entrypoint -ne 'KnowledgeClipper.Orchestrator.exe') {
    throw 'Orchestrator runtime manifest entrypoint is invalid.'
}
$runtimeTreeAlgorithm = 'sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)'
if ([string]$runtimeManifest.treeAlgorithm -ne $runtimeTreeAlgorithm) { throw 'Orchestrator runtime manifest tree algorithm is unsupported.' }

$manifestFiles = @($runtimeManifest.files)
$manifestFilePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifestFiles) {
    $relative = [string]$file.path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or $relative.StartsWith('/') -or
        $relative -match '(^|/)\.\.?(/|$)' -or $relative.Contains(':') -or -not $manifestFilePaths.Add($relative) -or
        [long]$file.bytes -lt 0 -or [string]$file.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Invalid, escaped, or duplicate Orchestrator runtime manifest path: $relative"
    }
}
foreach ($requiredRuntimeFile in @(
    'KnowledgeClipper.Orchestrator.exe',
    'KnowledgeClipper.Orchestrator.dll',
    'KnowledgeClipper.Orchestrator.deps.json',
    'KnowledgeClipper.Orchestrator.runtimeconfig.json')) {
    if (-not $manifestFilePaths.Contains($requiredRuntimeFile)) {
        throw "Orchestrator runtime manifest is missing required payload: $requiredRuntimeFile"
    }
}

$runtimeInventory = Get-DirectoryInventory $OrchestratorPublishRoot
if ($runtimeInventory.fileCount -ne [int]$runtimeManifest.fileCount -or
    $runtimeInventory.totalBytes -ne [long]$runtimeManifest.totalBytes -or
    $runtimeInventory.treeSha256 -ne [string]$runtimeManifest.treeSha256 -or
    $runtimeInventory.fileCount -ne $manifestFiles.Count) {
    throw "Orchestrator runtime payload aggregate/tree drifted from its manifest: expectedFiles=$($runtimeManifest.fileCount) actualFiles=$($runtimeInventory.fileCount) expectedBytes=$($runtimeManifest.totalBytes) actualBytes=$($runtimeInventory.totalBytes) expectedTree=$($runtimeManifest.treeSha256) actualTree=$($runtimeInventory.treeSha256) manifestFiles=$($manifestFiles.Count)"
}
$manifestByPath = @{}
foreach ($file in $manifestFiles) { $manifestByPath[[string]$file.path] = $file }
foreach ($file in @($runtimeInventory.files)) {
    $expected = $manifestByPath[[string]$file.path]
    if ($null -eq $expected -or [long]$expected.bytes -ne [long]$file.bytes -or [string]$expected.sha256 -ne [string]$file.sha256) {
        throw "Orchestrator runtime payload file drifted from its manifest: $($file.path)"
    }
}

$OrchestratorPath = Join-Path $OrchestratorPublishRoot 'KnowledgeClipper.Orchestrator.exe'
if ((Get-Sha256 $OrchestratorPath) -ne $ExpectedOrchestratorSha256) { throw 'Orchestrator entrypoint hash drifted.' }
$runtimeConfig = Read-Json (Join-Path $OrchestratorPublishRoot 'KnowledgeClipper.Orchestrator.runtimeconfig.json') 'Orchestrator runtimeconfig'
if (@($runtimeConfig.runtimeOptions.includedFrameworks).Count -eq 0) { throw 'Orchestrator runtimeconfig does not prove a self-contained payload.' }
$deps = Read-Json (Join-Path $OrchestratorPublishRoot 'KnowledgeClipper.Orchestrator.deps.json') 'Orchestrator deps'
if (-not ([string]$deps.runtimeTarget.name).EndsWith('/win-x64', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Orchestrator deps runtime target is not win-x64.'
}
$describeOutput = & $OrchestratorPath describe 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Orchestrator describe failed.' }
$describe = ($describeOutput -join [Environment]::NewLine) | ConvertFrom-Json
if ($describe.componentId -ne 'knowledge-clipper.orchestrator' -or $describe.version -ne $ExpectedOrchestratorVersion) {
    throw "Orchestrator identity/version mismatch: $($describe.componentId) $($describe.version)"
}

$checkpointIdentity = if (-not [string]::IsNullOrWhiteSpace([string]$coreCheckpoint.checkpointId)) {
    [string]$coreCheckpoint.checkpointId
}
else {
    "knowledge-clipper-core-v$($coreCheckpoint.checkpointVersion)"
}
$preflight = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-authority-tool-preflight.v1'
    status = 'passed'
    checkpointId = $checkpointIdentity
    checkpointVersion = [string]$coreCheckpoint.checkpointVersion
    checkpointPath = $checkpointRelative
    checkpointSha256 = $ExpectedCoreCheckpointSha256
    contractRoot = $contractRootRelative
    releaseContractRoot = $contractRootRelative
    boundFiles = $bindings.Count
    packagedBoundFiles = $packageBindings.Count
    schemaDependencyFiles = $schemaDependencyFiles.Count
    schemaDependencies = $schemaDependencyFiles
    orchestratorRuntime = [ordered]@{
        manifestPath = $OrchestratorRuntimeManifestPath
        manifestSha256 = $ExpectedOrchestratorRuntimeManifestSha256
        entrypoint = [string]$runtimeManifest.entrypoint
        entrypointSha256 = $ExpectedOrchestratorSha256
        version = $ExpectedOrchestratorVersion
        selfContained = $true
        runtimeIdentifier = 'win-x64'
        fileCount = $runtimeInventory.fileCount
        totalBytes = $runtimeInventory.totalBytes
        treeSha256 = $runtimeInventory.treeSha256
    }
    ipc = [ordered]@{
        schemaVersion = $ipcSchemaVersion
        protocolVersion = $ipcProtocolVersion
        configuration = $ipcConfigurationRelative
        requestContract = $requestContract
        receiptContract = $receiptContract
    }
}
if ($PreflightOnly) {
    $preflight | ConvertTo-Json -Depth 20
    return
}

try {
    Copy-Directory $BaseReleaseRoot $stagingReleaseRoot
    [IO.Directory]::CreateDirectory($stagingAuthorityDirectory) | Out-Null

    $releaseOrchestratorRoot = Join-Path $stagingReleaseRoot 'orchestrator'
    if (Test-Path -LiteralPath $releaseOrchestratorRoot) {
        $resolvedRuntimeTarget = [IO.Path]::GetFullPath($releaseOrchestratorRoot)
        $stagingPrefix = [IO.Path]::GetFullPath($stagingReleaseRoot).TrimEnd('\') + '\'
        if (-not $resolvedRuntimeTarget.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace Orchestrator outside staging release root: $resolvedRuntimeTarget"
        }
        [IO.Directory]::Delete($resolvedRuntimeTarget, $true)
    }
    Copy-Directory $OrchestratorPublishRoot $releaseOrchestratorRoot
    $publishedRuntimeInventory = Get-DirectoryInventory $releaseOrchestratorRoot
    if ($publishedRuntimeInventory.fileCount -ne $runtimeInventory.fileCount -or
        $publishedRuntimeInventory.totalBytes -ne $runtimeInventory.totalBytes -or
        $publishedRuntimeInventory.treeSha256 -ne $runtimeInventory.treeSha256) {
        throw 'Published Orchestrator runtime payload tree drifted during atomic copy.'
    }
    for ($runtimeIndex = 0; $runtimeIndex -lt $runtimeInventory.files.Count; $runtimeIndex++) {
        $sourceRuntimeFile = $runtimeInventory.files[$runtimeIndex]
        $publishedRuntimeFile = $publishedRuntimeInventory.files[$runtimeIndex]
        if ($sourceRuntimeFile.path -ne $publishedRuntimeFile.path -or
            $sourceRuntimeFile.bytes -ne $publishedRuntimeFile.bytes -or
            $sourceRuntimeFile.sha256 -ne $publishedRuntimeFile.sha256) {
            throw "Published Orchestrator runtime file drifted: $($sourceRuntimeFile.path)"
        }
    }
    $releaseOrchestrator = Join-Path $releaseOrchestratorRoot 'KnowledgeClipper.Orchestrator.exe'
    if ((Get-Sha256 $releaseOrchestrator) -ne $ExpectedOrchestratorSha256) { throw 'Published Orchestrator entrypoint hash drifted.' }
    $publishedRuntimeManifestPath = Join-Path $stagingAuthorityDirectory 'orchestrator-runtime-manifest.v1.json'
    [IO.File]::Copy($OrchestratorRuntimeManifestPath, $publishedRuntimeManifestPath, $false)
    if ((Get-Sha256 $publishedRuntimeManifestPath) -ne $ExpectedOrchestratorRuntimeManifestSha256) { throw 'Published Orchestrator runtime manifest drifted.' }

    $releaseCoreContracts = Join-Path $stagingReleaseRoot ($contractRootRelative.Replace('/', '\'))
    if (Test-Path -LiteralPath $releaseCoreContracts) { throw "Base release unexpectedly contains Core contract root: $contractRootRelative" }
    [IO.Directory]::CreateDirectory($releaseCoreContracts) | Out-Null
    foreach ($contract in $packageBindings) {
        $source = Join-Path $ProjectRoot ($contract.path.Replace('/', '\'))
        $destination = Join-Path $stagingReleaseRoot ($contract.path.Replace('/', '\'))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $false)
        if ((Get-Sha256 $destination) -ne $contract.sha256) { throw "Published Core contract drifted: $($contract.path)" }
    }
    foreach ($dependency in $schemaDependencyFiles) {
        $source = Join-Path $ProjectRoot ($dependency.path.Replace('/', '\'))
        $destination = Join-Path $stagingReleaseRoot ($dependency.path.Replace('/', '\'))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        if (Test-Path -LiteralPath $destination) {
            if ((Get-Sha256 $destination) -ne $dependency.sha256) { throw "Published Core schema dependency conflicts with the base release: $($dependency.path)" }
        }
        else {
            [IO.File]::Copy($source, $destination, $false)
        }
        if ((Get-Sha256 $destination) -ne $dependency.sha256) { throw "Published Core schema dependency drifted: $($dependency.path)" }
    }
    $releaseCheckpointPath = Join-Path $stagingReleaseRoot ($checkpointRelative.Replace('/', '\'))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($releaseCheckpointPath)) | Out-Null
    [IO.File]::Copy($CoreCheckpointPath, $releaseCheckpointPath, $false)
    if ((Get-Sha256 $releaseCheckpointPath) -ne $ExpectedCoreCheckpointSha256) { throw 'Published Core checkpoint drifted.' }

    $registry = Get-Content -LiteralPath (Join-Path $BaseAuthorityDirectory 'worker-registry.v1.json') -Raw | ConvertFrom-Json
    $registry.registryVersion = $NewRegistryVersion
    $registryPath = Join-Path $stagingAuthorityDirectory 'worker-registry.v1.json'
    Write-Json $registryPath $registry

    $tree = Get-ReleaseTree $stagingReleaseRoot
    $baseDependency = Get-Content -LiteralPath (Join-Path $BaseAuthorityDirectory 'dependency-manifest.v1.json') -Raw | ConvertFrom-Json
    $keyFiles = @()
    foreach ($key in $baseDependency.keyFiles) {
        $path = Join-Path $stagingReleaseRoot ($key.path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Key release file is missing: $($key.path)" }
        $keyFiles += [ordered]@{ path = $key.path; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    }
    foreach ($contract in $packageBindings) {
        $path = Join-Path $stagingReleaseRoot ($contract.path.Replace('/', '\'))
        $keyFiles += [ordered]@{ path = $contract.path; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    }
    foreach ($dependency in $schemaDependencyFiles) {
        $path = Join-Path $stagingReleaseRoot ($dependency.path.Replace('/', '\'))
        $keyFiles += [ordered]@{ path = $dependency.path; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    }
    $keyFiles += [ordered]@{ path = $checkpointRelative; bytes = (Get-Item -LiteralPath $releaseCheckpointPath).Length; sha256 = Get-Sha256 $releaseCheckpointPath }
    foreach ($runtimeKey in @(
        'orchestrator/KnowledgeClipper.Orchestrator.exe',
        'orchestrator/KnowledgeClipper.Orchestrator.dll',
        'orchestrator/KnowledgeClipper.Orchestrator.deps.json',
        'orchestrator/KnowledgeClipper.Orchestrator.runtimeconfig.json')) {
        $runtimeKeyPath = Join-Path $stagingReleaseRoot ($runtimeKey.Replace('/', '\'))
        $keyFiles += [ordered]@{ path = $runtimeKey; bytes = (Get-Item -LiteralPath $runtimeKeyPath).Length; sha256 = Get-Sha256 $runtimeKeyPath }
    }
    $keyFiles = @($keyFiles | Sort-Object path -Unique)
    $dependency = [ordered]@{
        schemaVersion = 'knowledge-clipper-release-dependency-manifest.v1'
        releaseId = $releaseId
        releaseRoot = "releases/$releaseId/win-x64"
        files = $tree.files
        bytes = $tree.bytes
        treeHashAlgorithm = $baseDependency.treeHashAlgorithm
        treeSha256 = $tree.treeSha256
        keyFiles = $keyFiles
        workerOperations = $baseDependency.workerOperations
        legacyEntrypointsExcluded = $true
        baseAuthority = [ordered]@{ registryVersion = $baseDependency.releaseId.Split('-v')[-1].Split('-')[0]; releaseId = $baseDependency.releaseId; treeSha256 = $baseDependency.treeSha256 }
        coreWorkflow = [ordered]@{
            checkpointId = $checkpointIdentity
            checkpointVersion = [string]$coreCheckpoint.checkpointVersion
            checkpointSha256 = $ExpectedCoreCheckpointSha256
            contractRoot = $contractRootRelative
            ipcSchemaVersion = $ipcSchemaVersion
            ipcProtocolVersion = $ipcProtocolVersion
            requestContract = $requestContract
            receiptContract = $receiptContract
            orchestratorRuntimeManifestSha256 = $ExpectedOrchestratorRuntimeManifestSha256
            orchestratorRuntimeTreeSha256 = $runtimeInventory.treeSha256
            schemaDependencyFiles = $schemaDependencyFiles
        }
    }
    $dependencyPath = Join-Path $stagingAuthorityDirectory 'dependency-manifest.v1.json'
    Write-Json $dependencyPath $dependency

    $validationOutput = & $releaseOrchestrator validate-registry --registry $registryPath --release-root $stagingReleaseRoot 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Runtime registry admission failed: $($validationOutput -join [Environment]::NewLine)" }
    $validation = ($validationOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($validation.status -ne 'valid' -or $validation.workers -ne 5 -or $validation.registryVersion -ne $NewRegistryVersion) {
        throw 'Runtime registry admission returned an unexpected result.'
    }

    if ($StagingValidationOnly) {
        [ordered]@{
            schemaVersion = 'knowledge-clipper-core-authority-staging-validation-receipt.v1'
            status = 'staging-validated'
            releaseId = $releaseId
            release = $tree
            runtime = [ordered]@{
                manifestSha256 = $ExpectedOrchestratorRuntimeManifestSha256
                files = $publishedRuntimeInventory.fileCount
                totalBytes = $publishedRuntimeInventory.totalBytes
                treeSha256 = $publishedRuntimeInventory.treeSha256
            }
            validation = [ordered]@{ status = $validation.status; workers = $validation.workers; registryVersion = $validation.registryVersion }
            authorityTargetsCreated = 0
            candidateTargetsCreated = 0
        } | ConvertTo-Json -Depth 20
        return
    }

    [IO.Directory]::Move($stagingReleaseContainer, $finalReleaseContainer)
    [IO.Directory]::Move($stagingAuthorityDirectory, $finalAuthorityDirectory)
    [ordered]@{
        schemaVersion = 'knowledge-clipper-core-authority-release-build-receipt.v1'
        status = 'candidate-built'
        releaseId = $releaseId
        releaseRoot = $finalReleaseRoot
        authorityDirectory = $finalAuthorityDirectory
        registry = [ordered]@{ version = $NewRegistryVersion; path = (Join-Path $finalAuthorityDirectory 'worker-registry.v1.json'); sha256 = Get-Sha256 (Join-Path $finalAuthorityDirectory 'worker-registry.v1.json') }
        dependencyManifest = [ordered]@{ path = (Join-Path $finalAuthorityDirectory 'dependency-manifest.v1.json'); sha256 = Get-Sha256 (Join-Path $finalAuthorityDirectory 'dependency-manifest.v1.json') }
        release = $tree
        orchestrator = [ordered]@{
            version = $ExpectedOrchestratorVersion
            entrypoint = 'orchestrator/KnowledgeClipper.Orchestrator.exe'
            bytes = (Get-Item -LiteralPath (Join-Path $finalReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe')).Length
            sha256 = $ExpectedOrchestratorSha256
            runtimeManifest = (Join-Path $finalAuthorityDirectory 'orchestrator-runtime-manifest.v1.json')
            runtimeManifestSha256 = $ExpectedOrchestratorRuntimeManifestSha256
            selfContained = $true
            runtimeIdentifier = 'win-x64'
            files = $runtimeInventory.fileCount
            totalBytes = $runtimeInventory.totalBytes
            treeSha256 = $runtimeInventory.treeSha256
        }
        coreContracts = [ordered]@{
            checkpointId = $checkpointIdentity
            checkpointVersion = [string]$coreCheckpoint.checkpointVersion
            checkpointSha256 = $ExpectedCoreCheckpointSha256
            root = $contractRootRelative
            boundFiles = $bindings.Count
            packagedBoundFiles = $packageBindings.Count
            schemaDependencyFiles = $schemaDependencyFiles.Count
            ipcProtocolVersion = $ipcProtocolVersion
            requestContract = $requestContract
            receiptContract = $receiptContract
        }
        validation = [ordered]@{ status = $validation.status; workers = $validation.workers }
    } | ConvertTo-Json -Depth 20
}
finally {
    Remove-VerifiedStagingDirectory $stagingReleaseContainer $releaseParent
    Remove-VerifiedStagingDirectory $stagingAuthorityDirectory $authorityParent
}

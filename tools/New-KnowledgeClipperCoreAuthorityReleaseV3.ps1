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
    [Parameter(Mandatory = $true)][string]$SchemaClosureManifestPath,
    [Parameter(Mandatory = $true)][string]$ExpectedSchemaClosureManifestSha256,
    [string]$ExpectedIpcProtocolVersion,
    [string]$ExpectedRequestContract,
    [string]$ExpectedReceiptContract,
    [switch]$PreflightOnly,
    [switch]$StagingValidationOnly,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$SchemaClosureManifestPath = [IO.Path]::GetFullPath($SchemaClosureManifestPath)
$baseToolPath = Join-Path $ProjectRoot 'tools\New-KnowledgeClipperCoreAuthorityRelease.ps1'
$expectedBaseToolSha256 = 'A108BABF46A0D8B904F2256816F38DC90FBAFF98B605DF314143566F1E3BDBAD'

if ($PreflightOnly -and $StagingValidationOnly) { throw 'PreflightOnly and StagingValidationOnly cannot be combined.' }

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Assert-BaseToolFrozen {
    if (-not (Test-Path -LiteralPath $baseToolPath -PathType Leaf)) { throw "Pinned base assembler is missing: $baseToolPath" }
    $actual = Get-Sha256 $baseToolPath
    if ($actual -ne $expectedBaseToolSha256) { throw "Pinned base assembler drifted: expected=$expectedBaseToolSha256 actual=$actual" }
}

function Read-Json([string]$Path, [string]$Description) {
    try { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100 }
    catch { throw "$Description is not valid JSON: $Path" }
}

function Get-SafeProjectPath([string]$Relative, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains('\') -or $Relative.StartsWith('/') -or
        $Relative.Contains(':') -or $Relative -match '(^|/)\.\.?(/|$)') {
        throw "$Description contains an invalid or escaped path: $Relative"
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $ProjectRoot ($Relative.Replace('/', '\'))))
    $prefix = $ProjectRoot.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$Description escaped the project root: $Relative" }
    $resolved
}

function Get-BaseArguments([switch]$ForPreflight, [switch]$ForStaging) {
    $arguments = @{
        BaseReleaseRoot = $BaseReleaseRoot
        BaseAuthorityDirectory = $BaseAuthorityDirectory
        NewRegistryVersion = $NewRegistryVersion
        ReleaseStamp = $ReleaseStamp
        OrchestratorPublishRoot = $OrchestratorPublishRoot
        OrchestratorRuntimeManifestPath = $OrchestratorRuntimeManifestPath
        ExpectedOrchestratorRuntimeManifestSha256 = $ExpectedOrchestratorRuntimeManifestSha256
        ExpectedOrchestratorVersion = $ExpectedOrchestratorVersion
        ExpectedOrchestratorSha256 = $ExpectedOrchestratorSha256
        CoreContractRoot = $CoreContractRoot
        CoreCheckpointPath = $CoreCheckpointPath
        ExpectedCoreCheckpointSha256 = $ExpectedCoreCheckpointSha256
        ProjectRoot = $ProjectRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedIpcProtocolVersion)) { $arguments.ExpectedIpcProtocolVersion = $ExpectedIpcProtocolVersion }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedRequestContract)) { $arguments.ExpectedRequestContract = $ExpectedRequestContract }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedReceiptContract)) { $arguments.ExpectedReceiptContract = $ExpectedReceiptContract }
    if ($ForPreflight) { $arguments.PreflightOnly = $true }
    if ($ForStaging) { $arguments.StagingValidationOnly = $true }
    $arguments
}

function Invoke-BaseJson([hashtable]$Arguments, [string]$Description) {
    Assert-BaseToolFrozen
    $text = (& $baseToolPath @Arguments 2>&1) -join [Environment]::NewLine
    $exitCode = $LASTEXITCODE
    Assert-BaseToolFrozen
    if ($exitCode -ne 0) { throw "$Description failed: $text" }
    try { $text | ConvertFrom-Json -Depth 100 }
    catch { throw "$Description returned invalid JSON: $text" }
}

Assert-BaseToolFrozen
if (-not (Test-Path -LiteralPath $SchemaClosureManifestPath -PathType Leaf)) { throw "Schema closure manifest is missing: $SchemaClosureManifestPath" }
if ((Get-Sha256 $SchemaClosureManifestPath) -ne $ExpectedSchemaClosureManifestSha256) { throw 'Schema closure manifest hash drifted.' }
$closureManifest = Read-Json $SchemaClosureManifestPath 'Schema closure manifest'
if ($closureManifest.schemaVersion -ne 'knowledge-clipper-core-schema-closure-manifest.v1') { throw 'Unsupported schema closure manifest.' }
$checkpointRelative = [IO.Path]::GetRelativePath($ProjectRoot, [IO.Path]::GetFullPath($CoreCheckpointPath)).Replace('\', '/')
if ([string]$closureManifest.checkpoint.path -ne $checkpointRelative -or
    [string]$closureManifest.checkpoint.sha256 -ne $ExpectedCoreCheckpointSha256) {
    throw 'Schema closure manifest is bound to a different Core checkpoint.'
}

$baseToolText = Get-Content -LiteralPath $baseToolPath -Raw
$legacyCodePatterns = @(
    'contracts[\\/]core[\\/]v2\.3',
    "ipcProtocolVersion\s*=\s*'1\.1'",
    "receiptSchemaVersion\s*=\s*'2\.3\.0'")
$legacyCodeHits = @($legacyCodePatterns | Where-Object { $baseToolText -match $_ })
if ($legacyCodeHits.Count -ne 0) { throw "Pinned base assembler contains old-version code hardcoding: $($legacyCodeHits -join ', ')" }

$preflight = Invoke-BaseJson (Get-BaseArguments -ForPreflight) 'Pinned base assembler preflight'
if ($preflight.status -ne 'passed' -or [string]$preflight.checkpointSha256 -ne $ExpectedCoreCheckpointSha256) {
    throw 'Pinned base assembler preflight identity/status mismatch.'
}
$observedDependencies = @($preflight.schemaDependencies)
$expectedDependencies = @($closureManifest.dependencies)
if ($observedDependencies.Count -ne $expectedDependencies.Count) {
    throw "Schema dependency closure count drifted: observed=$($observedDependencies.Count) preflight=$($preflight.schemaDependencyFiles) expected=$($expectedDependencies.Count) observedPaths=$(@($observedDependencies.path) -join ',') expectedPaths=$(@($expectedDependencies.path) -join ',')"
}
$expectedByPath = @{}
foreach ($dependency in $expectedDependencies) {
    $path = [string]$dependency.path
    if ($expectedByPath.ContainsKey($path)) { throw "Schema closure manifest contains a duplicate path: $path" }
    $resolved = Get-SafeProjectPath $path 'Schema closure manifest'
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Schema closure dependency is missing: $path" }
    if ((Get-Item -LiteralPath $resolved).Length -ne [long]$dependency.bytes -or
        (Get-Sha256 $resolved) -ne [string]$dependency.sha256) {
        throw "Schema closure dependency drifted: $path"
    }
    $expectedByPath[$path] = $dependency
}
$observedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($dependency in $observedDependencies) {
    $path = [string]$dependency.path
    if (-not $observedPaths.Add($path) -or -not $expectedByPath.ContainsKey($path)) { throw "Observed schema dependency is unbound or duplicated: $path" }
    $expected = $expectedByPath[$path]
    if ([long]$dependency.bytes -ne [long]$expected.bytes -or [string]$dependency.sha256 -ne [string]$expected.sha256) {
        throw "Observed schema dependency evidence drifted: $path"
    }
}

$v3Preflight = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-authority-assembler-v3-preflight.v1'
    status = 'passed'
    baseAssembler = [ordered]@{ path = 'tools/New-KnowledgeClipperCoreAuthorityRelease.ps1'; sha256 = $expectedBaseToolSha256 }
    checkpoint = [ordered]@{ path = $checkpointRelative; sha256 = $ExpectedCoreCheckpointSha256 }
    runtime = $preflight.orchestratorRuntime
    ipc = $preflight.ipc
    schemaClosure = [ordered]@{
        manifestPath = $SchemaClosureManifestPath
        manifestSha256 = $ExpectedSchemaClosureManifestSha256
        mode = 'checkpoint-bound-local-ref-closure'
        dependencies = $observedDependencies
        dependencyFiles = $observedDependencies.Count
        baseReportedDependencyCount = [int]$preflight.schemaDependencyFiles
        codeLegacyHardcodeHits = 0
        checkpointBoundLegacyDependenciesAllowed = $true
    }
}
if ($PreflightOnly) { $v3Preflight | ConvertTo-Json -Depth 100; return }

$inner = if ($StagingValidationOnly) {
    Invoke-BaseJson (Get-BaseArguments -ForStaging) 'Pinned base assembler staging validation'
}
else {
    Invoke-BaseJson (Get-BaseArguments) 'Pinned base assembler formal assembly'
}
if ($StagingValidationOnly) {
    if ($inner.status -ne 'staging-validated' -or $inner.authorityTargetsCreated -ne 0 -or $inner.candidateTargetsCreated -ne 0 -or
        $inner.validation.status -ne 'valid' -or $inner.validation.workers -ne 5) {
        throw 'Pinned base assembler staging validation returned unexpected evidence.'
    }
}
elseif ($inner.status -ne 'candidate-built' -or $inner.validation.status -ne 'valid' -or $inner.validation.workers -ne 5) {
    throw 'Pinned base assembler formal assembly returned unexpected evidence.'
}
Assert-BaseToolFrozen
[ordered]@{
    schemaVersion = if ($StagingValidationOnly) { 'knowledge-clipper-core-authority-assembler-v3-staging-receipt.v1' } else { 'knowledge-clipper-core-authority-assembler-v3-build-receipt.v1' }
    status = if ($StagingValidationOnly) { 'staging-validated' } else { 'candidate-built' }
    baseAssemblerSha256 = $expectedBaseToolSha256
    schemaClosureManifestSha256 = $ExpectedSchemaClosureManifestSha256
    schemaDependencyFiles = $observedDependencies.Count
    runtimeTreeSha256 = $preflight.orchestratorRuntime.treeSha256
    validation = $inner.validation
    authorityTargetsCreated = if ($StagingValidationOnly) { 0 } else { 1 }
    candidateTargetsCreated = 0
    innerReceipt = $inner
} | ConvertTo-Json -Depth 100

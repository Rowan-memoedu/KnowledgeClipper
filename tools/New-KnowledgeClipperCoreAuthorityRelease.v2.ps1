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
$implementation = Join-Path $PSScriptRoot 'New-KnowledgeClipperCoreAuthorityRelease.ps1'
$expectedImplementationSha256 = 'A108BABF46A0D8B904F2256816F38DC90FBAFF98B605DF314143566F1E3BDBAD'
if (-not (Test-Path -LiteralPath $implementation -PathType Leaf)) {
    throw "Pinned authority assembler implementation is missing: $implementation"
}
$actualImplementationSha256 = (Get-FileHash -LiteralPath $implementation -Algorithm SHA256).Hash
if ($actualImplementationSha256 -ne $expectedImplementationSha256) {
    throw "Pinned authority assembler implementation drifted: expected=$expectedImplementationSha256 actual=$actualImplementationSha256"
}
$source = Get-Content -LiteralPath $implementation -Raw
if ($source -match '(?i)contracts[\\/]core[\\/]v2\.3' -or
    $source -match '(?i)ipcProtocolVersion\s*=\s*["'']1\.1["'']' -or
    $source -match '(?i)receiptSchemaVersion\s*=\s*["'']2\.3\.0["'']') {
    throw 'Pinned authority assembler contains a forbidden legacy-version literal; schema references must be discovered from checkpoint-bound documents.'
}

$arguments = @{} + $PSBoundParameters
$arguments.ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
& $implementation @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

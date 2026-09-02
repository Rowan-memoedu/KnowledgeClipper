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
    [Parameter(Mandatory = $true)][string]$ExpectedIpcProtocolVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedRequestContract,
    [Parameter(Mandatory = $true)][string]$ExpectedReceiptContract,
    [Parameter(Mandatory = $true)][string]$SchemaClosureManifestPath,
    [Parameter(Mandatory = $true)][string]$ExpectedSchemaClosureManifestSha256,
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
$v3Path = Join-Path $PSScriptRoot 'New-KnowledgeClipperCoreAuthorityReleaseV3.ps1'
$expectedV3Sha256 = '69D6F6A2E0EB911A1D5A02C25820E89C0AFCAB20F5AC52ABBAE2DC89C486FF37'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Utf8Json([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

if (Test-Path -LiteralPath $ReceiptPath) { throw "Append-only receipt already exists: $ReceiptPath" }
if ((Get-Sha256 $v3Path) -ne $expectedV3Sha256) { throw 'V3 authority assembler hash drifted before formal invocation.' }
$receiptParent = Split-Path -Parent $ReceiptPath
[IO.Directory]::CreateDirectory($receiptParent) | Out-Null

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
    ExpectedIpcProtocolVersion = $ExpectedIpcProtocolVersion
    ExpectedRequestContract = $ExpectedRequestContract
    ExpectedReceiptContract = $ExpectedReceiptContract
    SchemaClosureManifestPath = $SchemaClosureManifestPath
    ExpectedSchemaClosureManifestSha256 = $ExpectedSchemaClosureManifestSha256
    ProjectRoot = $ProjectRoot
}
$startedAt = [DateTime]::UtcNow.ToString('o')
$output = & $v3Path @arguments 2>&1
if ($LASTEXITCODE -ne 0) { throw "V3 formal assembly failed: $($output -join [Environment]::NewLine)" }
try { $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100 }
catch { throw "V3 formal assembly returned invalid JSON: $($output -join [Environment]::NewLine)" }
if ((Get-Sha256 $v3Path) -ne $expectedV3Sha256) { throw 'V3 authority assembler hash drifted after formal invocation.' }
if ($result.status -ne 'candidate-built' -or $result.authorityTargetsCreated -ne 1 -or $result.candidateTargetsCreated -ne 0 -or
    $result.validation.status -ne 'valid' -or $result.validation.workers -ne 5) {
    throw 'V3 formal assembly returned unexpected evidence.'
}
$expectedReleaseId = "knowledge-clipper-authority-v$NewRegistryVersion-$ReleaseStamp"
if ([string]$result.innerReceipt.releaseId -ne $expectedReleaseId) { throw 'V3 formal assembly release identity drifted.' }
foreach ($target in @([string]$result.innerReceipt.releaseRoot, [string]$result.innerReceipt.authorityDirectory)) {
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "V3 formal target is missing: $target" }
}

$wrapper = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-v3-formal-invocation-receipt.v1'
    status = 'candidate-built'
    startedAt = $startedAt
    completedAt = [DateTime]::UtcNow.ToString('o')
    v3Assembler = [ordered]@{ path = 'tools/New-KnowledgeClipperCoreAuthorityReleaseV3.ps1'; sha256Before = $expectedV3Sha256; sha256After = Get-Sha256 $v3Path }
    result = $result
    externalWrites = 0
}
$temporaryReceipt = $ReceiptPath + '.tmp-' + [Guid]::NewGuid().ToString('N')
try {
    Write-Utf8Json $temporaryReceipt $wrapper
    [IO.File]::Move($temporaryReceipt, $ReceiptPath)
}
finally {
    if (Test-Path -LiteralPath $temporaryReceipt) { [IO.File]::Delete($temporaryReceipt) }
}
Get-Content -LiteralPath $ReceiptPath -Raw

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Get-Location).Path)
$stamp = '20260902T012900Z'
$version = '1.2.40'
$releaseTarget = Join-Path $root "releases\knowledge-clipper-authority-v$version-$stamp"
$authorityTarget = Join-Path $root "contracts\authority\worker-registry\v$version-$stamp"
if ((Test-Path -LiteralPath $releaseTarget) -or (Test-Path -LiteralPath $authorityTarget)) {
    throw 'v1.2.40 target became occupied before formal assembly.'
}
$v3 = Join-Path $root 'tools\New-KnowledgeClipperCoreAuthorityReleaseV3.ps1'
$base = Join-Path $root 'tools\New-KnowledgeClipperCoreAuthorityRelease.ps1'
if ((Get-FileHash -LiteralPath $v3 -Algorithm SHA256).Hash -ne '69D6F6A2E0EB911A1D5A02C25820E89C0AFCAB20F5AC52ABBAE2DC89C486FF37') { throw 'V3 hash drifted.' }
if ((Get-FileHash -LiteralPath $base -Algorithm SHA256).Hash -ne 'A108BABF46A0D8B904F2256816F38DC90FBAFF98B605DF314143566F1E3BDBAD') { throw 'Base assembler hash drifted.' }
$inputRoot = Join-Path $root "contracts\authority\assembler-inputs\v3\v$version-$stamp"
$receipt = Join-Path $root "receipts\authority-assembly\$stamp\knowledge-clipper-authority-v$version-$stamp.receipt.json"
& (Join-Path $root 'tools\Invoke-KnowledgeClipperAuthorityV3FormalAssembly.ps1') `
  -BaseReleaseRoot (Join-Path $root 'releases\knowledge-clipper-authority-v1.2.37-20260901T063000Z\win-x64') `
  -BaseAuthorityDirectory (Join-Path $root 'contracts\authority\worker-registry\v1.2.37-20260901T063000Z') `
  -NewRegistryVersion $version `
  -ReleaseStamp $stamp `
  -OrchestratorPublishRoot (Join-Path $root 'artifacts\core-v2.10.3\20260902T010816Z\orchestrator') `
  -OrchestratorRuntimeManifestPath (Join-Path $inputRoot 'orchestrator-runtime-manifest.v1.json') `
  -ExpectedOrchestratorRuntimeManifestSha256 'D87637194C45054424996059E5AA26A1C108D02CCEA3ABCB66AEF7C1088696CA' `
  -ExpectedOrchestratorVersion '2.10.3.0' `
  -ExpectedOrchestratorSha256 'DD84BEDDCE083D8F62284189408A97C0549B7332390DFF69B7E2B4586D75C82C' `
  -CoreContractRoot (Join-Path $root 'contracts\core\v2.10') `
  -CoreCheckpointPath (Join-Path $root 'contracts\core\v2.10\checkpoints\v2.10.5-20260902T012600Z\checkpoint.json') `
  -ExpectedCoreCheckpointSha256 'D313242ED851AD93AEFBE64BDDB7C6ADA767630936AA63ACE0ECB65718693FCD' `
  -ExpectedIpcProtocolVersion '1.7' `
  -ExpectedRequestContract 'knowledge-clipper-module-action.v6' `
  -ExpectedReceiptContract 'knowledge-clipper-module-action-receipt.v6' `
  -SchemaClosureManifestPath (Join-Path $inputRoot 'schema-closure-manifest.v1.json') `
  -ExpectedSchemaClosureManifestSha256 '6A34946C923D1C43C4C0F6C57E1CFE51219B7CBC7985BACADC8E6AD3D8861E79' `
  -ProjectRoot $root `
  -ReceiptPath $receipt
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "FORMAL_RECEIPT=$receipt"

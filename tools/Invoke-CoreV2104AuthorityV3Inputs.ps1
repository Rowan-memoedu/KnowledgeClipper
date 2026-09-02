$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Get-Location).Path)
$output = Join-Path $root 'contracts\authority\assembler-inputs\v3\v1.2.40-20260902T012900Z'
& (Join-Path $root 'tools\New-KnowledgeClipperAuthorityV3Inputs.ps1') `
  -ProjectRoot $root `
  -CoreContractRoot (Join-Path $root 'contracts\core\v2.10') `
  -CoreCheckpointPath (Join-Path $root 'contracts\core\v2.10\checkpoints\v2.10.5-20260902T012600Z\checkpoint.json') `
  -ExpectedCoreCheckpointSha256 'D313242ED851AD93AEFBE64BDDB7C6ADA767630936AA63ACE0ECB65718693FCD' `
  -OrchestratorPublishRoot (Join-Path $root 'artifacts\core-v2.10.3\20260902T010816Z\orchestrator') `
  -ExpectedOrchestratorVersion '2.10.3.0' `
  -OutputDirectory $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "OUTPUT=$output"

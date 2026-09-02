[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1216.ps1') -Raw
$replacements = [ordered]@{
    "RegistryVersion = ''1.2.16''" = "RegistryVersion = ''1.2.17''"
    "CoreVersion = ''2.7.0''" = "CoreVersion = ''2.7.1''"
    'contracts/core/v2.7/checkpoints/v2.7.1-20260830T125000Z/checkpoint.json' = 'contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json'
    'releases/knowledge-clipper-authority-v1.2.15-20260830T121200Z/win-x64' = 'releases/knowledge-clipper-authority-v1.2.16-20260830T125200Z/win-x64'
    'contracts/authority/worker-registry/v1.2.15-20260830T121200Z/worker-registry.v1.json' = 'contracts/authority/worker-registry/v1.2.16-20260830T125200Z/worker-registry.v1.json'
    'B6909AFC256F299E6D9B3128C58B90B99988E412D4481C47AC2671B42440E9E1' = 'B22CA9746905AC923CBFFEF913A7CAEB3D7FF0EDDE8507145E6A7B99BBD09F65'
    'canary/core-v2.7.0-offline/20260830T124700Z/settings-control.receipt.json' = 'canary/core-v2.7.1-offline/20260831T062200Z/settings-control.receipt.json'
    'canary/core-v2.7.0-offline/20260830T124700Z/core-adapter.receipt.json' = 'canary/core-v2.7.1-offline/20260831T062200Z/core-adapter.receipt.json'
    'canary/core-v2.7.0-offline/20260830T124700Z/orchestrator-contract-tests.receipt.json' = 'canary/core-v2.7.1-offline/20260831T062200Z/orchestrator-contract-tests.receipt.json'
    'canary/core-v2.7.0-offline/20260830T124700Z/question-workflow.receipt.json' = 'canary/core-v2.7.1-offline/20260831T062200Z/question-workflow.receipt.json'
    'canary/core-v2.7.0-offline/20260830T124700Z/public-contracts.receipt.json' = 'canary/core-v2.7.1-offline/20260831T062200Z/public-contracts.receipt.json'
    "baseAuthority = '1.2.15'; supersedesCheckpointSha256 = 'B5F1EF63EF812D86C360D0AFB46ECDFB220C96C24CD6CF2252E18411D82084A1'" = "baseAuthority = '1.2.16'; supersedesCheckpointSha256 = '48D6E25E967F73F1D0489DF1A6BA8C8F93212B5FAD1FAD832C5C09A3D58AC046'"
    "remnoteProbe = 'public-readonly-dispatch'" = "remnoteProbe = 'public-readonly-typed-healthy-degraded-blocked'"
    "Authority v1.2.16 assembly failed" = "Authority v1.2.17 assembly failed"
    ".New-KnowledgeClipperAuthorityV1216." = ".New-KnowledgeClipperAuthorityV1217."
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) {
        throw "Authority v1.2.17 wrapper marker is missing: $($pair.Key)"
    }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1217.wrapper.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.17 wrapper failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$templatePath = Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1212.ps1'
$template = Get-Content -LiteralPath $templatePath -Raw
$replacements = [ordered]@{
    "[string]`$RegistryVersion = '1.2.12'" = "[string]`$RegistryVersion = '1.2.13'"
    "[string]`$CoreVersion = '2.5.0'" = "[string]`$CoreVersion = '2.5.1'"
    "[string]`$CoreCheckpoint = 'contracts/core/v2.5/checkpoints/v2.5.1-20260830T075400Z/checkpoint.json'" = "[string]`$CoreCheckpoint = 'contracts/core/v2.5/checkpoints/v2.5.2-20260830T095700Z/checkpoint.json'"
    "'contracts/authority/worker-registry/v1.2.11-20260828T092800Z/worker-registry.v1.json'" = "'contracts/authority/worker-registry/v1.2.12-20260830T075600Z/worker-registry.v1.json'"
    "'62996EF471F46470090BE40259BF95ACD84EB7640084A1FCD4EC85E680AEE4F3'" = "'EDB990471C2FEA3A45460C4A2202F09F2247F652E1EB764C65E578E8D9C1C58A'"
    "'canary/core-v2.5-offline/20260830T075000Z/settings-control.receipt.json'" = "'canary/core-v2.5.2-offline/20260830T095400Z/settings-control.receipt.json'"
    "'canary/core-v2.5-offline/20260830T075000Z/core-adapter.receipt.json'" = "'canary/core-v2.5.2-offline/20260830T095400Z/core-adapter.receipt.json'"
    "'canary/core-v2.5-offline/20260830T075000Z/orchestrator-contract-tests.receipt.json'" = "'canary/core-v2.5.2-offline/20260830T095400Z/orchestrator-contract-tests.receipt.json'"
    "'core-contracts.json' = 'canary/core-v2.5.2-offline/20260830T095400Z/orchestrator-contract-tests.receipt.json'" = "'core-contracts.json' = 'canary/core-v2.5.2-offline/20260830T095400Z/orchestrator-contract-tests.receipt.json'`r`n    'core-question-workflow.json' = 'canary/core-v2.5.2-offline/20260830T095400Z/question-workflow.receipt.json'`r`n    'core-public-contracts.json' = 'canary/core-v2.5.2-offline/20260830T095400Z/public-contracts.receipt.json'"
    "baseAuthority = '1.2.11'; supersedesCheckpointSha256 = 'A01D0E65DEABA5078D7A57EF57FFFFF547DB3F5BBD03074247135DF463CA7F1C'" = "baseAuthority = '1.2.12'; supersedesCheckpointSha256 = '552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580'"
    "remnoteProbe = 'conditional-cli-only'" = "remnoteProbe = 'conditional-cli-only'; questionMakeCards = 'original-destinations-frozen-export-once'"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority template marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1213.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.13 assembly failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

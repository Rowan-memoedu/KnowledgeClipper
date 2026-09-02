[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV279Checkpoint.ps1') -Raw
$required = @(
    'contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json',
    'contracts/core/v2.7/checkpoints/v2.7.10-20260901T010000Z/checkpoint.json',
    "'2.7.8'", "'2.7.10'", 'v2.7.6', 'core-v2.7.6-offline/20260901T000500Z',
    "currentAuthority='1.2.23';`$document.authorityBoundary.intendedAuthority='1.2.24'"
)
foreach ($marker in $required) {
    if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw "Core v2.7.11 checkpoint marker is missing: $marker" }
}
$template = $template.Replace('contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json', '__SOURCE_CHECKPOINT__', [StringComparison]::Ordinal)
$template = $template.Replace('contracts/core/v2.7/checkpoints/v2.7.10-20260901T010000Z/checkpoint.json', '__TARGET_CHECKPOINT__', [StringComparison]::Ordinal)
$template = $template.Replace("'2.7.8'", "'__SOURCE_VERSION__'", [StringComparison]::Ordinal)
$template = $template.Replace("'2.7.10'", "'__TARGET_VERSION__'", [StringComparison]::Ordinal)
$template = $template.Replace('v2.7.6', 'v2.7.7', [StringComparison]::Ordinal)
$receiptNames = @('settings-control.receipt.json', 'core-adapter.receipt.json', 'orchestrator-contract-tests.receipt.json', 'question-workflow.receipt.json', 'public-contracts.receipt.json')
foreach ($receiptName in $receiptNames) {
    $oldPair = "'canary/core-v2.7.4-offline/20260831T100200Z/$receiptName'='canary/core-v2.7.7-offline/20260901T000500Z/$receiptName'"
    $newPair = "'canary/core-v2.7.6-offline/20260901T000500Z/$receiptName'='canary/core-v2.7.7-offline/20260901T000500Z/$receiptName'"
    if (-not $template.Contains($oldPair, [StringComparison]::Ordinal)) { throw "Core v2.7.11 receipt mapping is missing: $receiptName" }
    $template = $template.Replace($oldPair, $newPair, [StringComparison]::Ordinal)
}
$oldRuntimePair = "'dist/orchestrator/win-x64/v2.7.4/KnowledgeClipper.Orchestrator.exe'='dist/orchestrator/win-x64/v2.7.7/KnowledgeClipper.Orchestrator.exe'"
$newRuntimePair = "'dist/orchestrator/win-x64/v2.7.6/KnowledgeClipper.Orchestrator.exe'='dist/orchestrator/win-x64/v2.7.7/KnowledgeClipper.Orchestrator.exe'"
if (-not $template.Contains($oldRuntimePair, [StringComparison]::Ordinal)) { throw 'Core v2.7.11 runtime mapping is missing.' }
$template = $template.Replace($oldRuntimePair, $newRuntimePair, [StringComparison]::Ordinal)
$template = $template.Replace('core-v2.7.6-offline/20260901T000500Z', 'core-v2.7.7-offline/20260901T011300Z', [StringComparison]::Ordinal)
$receiptNames | ForEach-Object {
    $badPair = "'canary/core-v2.7.7-offline/20260901T000500Z/$_'='canary/core-v2.7.7-offline/20260901T000500Z/$_'"
    $correctPair = "'canary/core-v2.7.6-offline/20260901T000500Z/$_'='canary/core-v2.7.7-offline/20260901T011300Z/$_'"
    if (-not $template.Contains($badPair, [StringComparison]::Ordinal)) { throw "Core v2.7.11 receipt source mapping drifted: $_" }
    $template = $template.Replace($badPair, $correctPair, [StringComparison]::Ordinal)
}
$badRuntimePair = "'dist/orchestrator/win-x64/v2.7.7/KnowledgeClipper.Orchestrator.exe'='dist/orchestrator/win-x64/v2.7.7/KnowledgeClipper.Orchestrator.exe'"
$correctRuntimePair = "'dist/orchestrator/win-x64/v2.7.6/KnowledgeClipper.Orchestrator.exe'='dist/orchestrator/win-x64/v2.7.7/KnowledgeClipper.Orchestrator.exe'"
if (-not $template.Contains($badRuntimePair, [StringComparison]::Ordinal)) { throw 'Core v2.7.11 runtime source mapping drifted.' }
$template = $template.Replace($badRuntimePair, $correctRuntimePair, [StringComparison]::Ordinal)
$template = $template.Replace("currentAuthority='1.2.23';`$document.authorityBoundary.intendedAuthority='1.2.24'", "currentAuthority='1.2.24';`$document.authorityBoundary.intendedAuthority='1.2.25'", [StringComparison]::Ordinal)
$template = $template.Replace('__SOURCE_CHECKPOINT__', 'contracts/core/v2.7/checkpoints/v2.7.10-20260901T010000Z/checkpoint.json', [StringComparison]::Ordinal)
$template = $template.Replace('__TARGET_CHECKPOINT__', 'contracts/core/v2.7/checkpoints/v2.7.11-20260901T011300Z/checkpoint.json', [StringComparison]::Ordinal)
$template = $template.Replace('__SOURCE_VERSION__', '2.7.10', [StringComparison]::Ordinal)
$template = $template.Replace('__TARGET_VERSION__', '2.7.11', [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-CoreV2711Checkpoint.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.11 checkpoint failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

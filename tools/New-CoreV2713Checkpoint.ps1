[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV2712Checkpoint.ps1') -Raw
$replacements = [ordered]@{
    "'contracts/core/v2.7/checkpoints/v2.7.12-20260901T013000Z/checkpoint.json'" = "'contracts/core/v2.7/checkpoints/v2.7.13-20260901T014500Z/checkpoint.json'"
    "'core-v2.7.12-20260901T013000Z'" = "'core-v2.7.13-20260901T014500Z'"
    "'2.7.12'" = "'2.7.13'"
    "'contracts/core/v2.7/checkpoints/v2.7.11-20260901T011300Z/checkpoint.json'" = "'contracts/core/v2.7/checkpoints/v2.7.12-20260901T013000Z/checkpoint.json'"
    "'2026-09-01T01:30:00+08:00'" = "'2026-09-01T01:45:00+08:00'"
    "'1.2.26'" = "'1.2.27'"
    "'content-worker/receipts/content-card-reference-integrity-0.9.2-admission-20260901T012500Z.json'" = "'content-worker/receipts/content-ocr-segment-identity-0.9.3-admission-20260901T014300Z.json'"
    "'content-worker/receipts/content-ocr-coordinate-recovery-0.9.1-admission-20260831T093500Z.json'" = "'content-worker/receipts/content-card-reference-integrity-0.9.2-admission-20260901T012500Z.json'"
    "'content-worker\releases\content.deepseek-0.9.2-win-x64-formal-20260901T012100Z'" = "'content-worker\releases\content.deepseek-0.9.3-win-x64-formal-20260901T013945Z'"
    "'0.9.2'" = "'0.9.3'"
    "'169229AF9641B56A1005F5CC12D845EBEC3A94FAF1883254E5AD888F69BC9625'" = "'18EC576E40756FFB7EF932BE9575C900DEF4B7ECD81BA28F93FA7434998C6075'"
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "Core v2.7.13 template marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-CoreV2713Checkpoint.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.13 checkpoint failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

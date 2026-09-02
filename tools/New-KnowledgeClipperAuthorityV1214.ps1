[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$templatePath = Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1212.ps1'
$template = Get-Content -LiteralPath $templatePath -Raw
$replacements = [ordered]@{
    "[string]`$RegistryVersion = '1.2.12'" = "[string]`$RegistryVersion = '1.2.14'"
    "[string]`$CoreVersion = '2.5.0'" = "[string]`$CoreVersion = '2.5.2'"
    "[string]`$CoreCheckpoint = 'contracts/core/v2.5/checkpoints/v2.5.1-20260830T075400Z/checkpoint.json'" = "[string]`$CoreCheckpoint = 'contracts/core/v2.5/checkpoints/v2.5.3-20260830T184500Z/checkpoint.json'"
    "'releases/knowledge-clipper-authority-v1.2.11-20260828T092800Z/win-x64'" = "'releases/knowledge-clipper-authority-v1.2.13-20260830T100000Z/win-x64'"
    "'contracts/authority/worker-registry/v1.2.11-20260828T092800Z/worker-registry.v1.json'" = "'contracts/authority/worker-registry/v1.2.13-20260830T100000Z/worker-registry.v1.json'"
    "'62996EF471F46470090BE40259BF95ACD84EB7640084A1FCD4EC85E680AEE4F3'" = "'16F39BAB63346AADEA83D1C66B1BE2520998BFE1A8341667D121CFCB84838D9A'"
    "'content.deepseek' = [ordered]@{ version = '0.7.0'; source = 'content-worker/releases/content.deepseek-0.7.0-win-x64-formal-20260828T103735Z'; destination = 'workers/content'; executable = 'workers/content/KnowledgeClipper.Content.Worker.exe'; sha256 = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'; treeSha256 = '724F2D2732FA13FA236825F7E82E8E8E8DAF4DC1354E36093AB8B79262EDFBEC' }" = "'content.deepseek' = [ordered]@{ version = '0.8.0'; source = 'content-worker/releases/content.deepseek-0.8.0-win-x64-formal-20260830T095330Z'; destination = 'workers/content'; executable = 'workers/content/KnowledgeClipper.Content.Worker.exe'; sha256 = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'; treeSha256 = '9A6A50F4A31EF2D2F194B2F69F9FAE7B22192DC013CDD2FCBD6B82A423139E45' }"
    "'export.anki' = [ordered]@{ version = '1.2.1'; source = 'releases/knowledge-clipper-anki-exporter-v1.2.1-20260828T104000Z/win-x64/workers/anki'; destination = 'workers/anki'; executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = '135B7BC31DA93389819E4F802226600CD3879F6527ED4194555D613A32CC4EE1'; treeSha256 = 'E7EBA348C344865B0F403D34187831BABDEADC4818451084ACB86CC37A3E161D' }" = "'export.anki' = [ordered]@{ version = '1.2.2'; source = 'releases/knowledge-clipper-anki-exporter-v1.2.2-20260830T182623Z/win-x64/workers/anki'; destination = 'workers/anki'; executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = '0D6F18DD6CD10828C0139183D7D84712378FCDDE0AFFCABFCAFD19C228316449'; treeSha256 = 'F9FF222B9144F1CF56E0059225E0E1448639D23C06852733B4ADB3DB865CB66C' }"
    "'content-core-v3-compatibility.json' = 'content-worker/receipts/core-v3-desktop-compatibility/20260830T074659078Z/receipt.v1.json'" = "'content-ocr-admission.json' = 'content-worker/receipts/content-ocr-proofreading-0.8.0-admission-20260830T101000Z.json'`r`n    'content-formal-release.json' = 'content-worker/canary/formal-release/content.deepseek-0.8.0-win-x64-formal-20260830T095330Z/20260830T095352921Z-settings-snapshot-replay/formal-release-offline-receipt.v1.json'"
    "'anki-shared-consumer.json' = 'workers/anki-exporter/canary/shared-consumer-compatibility/20260830T074135Z-28e90aac/receipt.json'" = "'anki-cross-run-admission.json' = 'canary/anki-v122-xrun/20260830T182803Z/receipt.json'"
    "'anki-offline-probe.json' = 'workers/anki-exporter/canary/formal-release-settings-probe/20260830T074140Z-2b55687b/receipt.json'" = "'anki-offline-probe.json' = 'workers/anki-exporter/canary/formal-release-settings-probe/20260830T074140Z-2b55687b/receipt.json'"
    "'core-settings.json' = 'canary/core-v2.5-offline/20260830T075000Z/settings-control.receipt.json'" = "'core-settings.json' = 'canary/core-v2.5.3-offline/20260830T184300Z/settings-control.receipt.json'"
    "'core-adapter.json' = 'canary/core-v2.5-offline/20260830T075000Z/core-adapter.receipt.json'" = "'core-adapter.json' = 'canary/core-v2.5.3-offline/20260830T184300Z/core-adapter.receipt.json'"
    "'core-contracts.json' = 'canary/core-v2.5-offline/20260830T075000Z/orchestrator-contract-tests.receipt.json'" = "'core-contracts.json' = 'canary/core-v2.5.3-offline/20260830T184300Z/orchestrator-contract-tests.receipt.json'`r`n    'core-question-workflow.json' = 'canary/core-v2.5.3-offline/20260830T184300Z/question-workflow.receipt.json'`r`n    'core-public-contracts.json' = 'canary/core-v2.5.3-offline/20260830T184300Z/public-contracts.receipt.json'"
    "[IO.File]::Copy(`$coreExecutable, (Join-Path `$stagingRelease 'orchestrator/KnowledgeClipper.Orchestrator.exe'), `$true)" = "ReplaceDirectory (Join-Path `$ProjectRoot `"dist/orchestrator/win-x64/v`$CoreVersion`") (Join-Path `$stagingRelease 'orchestrator')"
    "[IO.File]::Copy(`$source, `$target, `$false)" = "[IO.File]::Copy(`$source, `$target, `$true)"
    "baseAuthority = '1.2.11'; supersedesCheckpointSha256 = 'A01D0E65DEABA5078D7A57EF57FFFFF547DB3F5BBD03074247135DF463CA7F1C'" = "baseAuthority = '1.2.13'; supersedesCheckpointSha256 = 'AF9D8494A47ACE7CF28ACAD561CCA648D1D4EC7BDCBABB305A3FE31FCA608BE7'"
    "remnoteProbe = 'conditional-cli-only'" = "remnoteProbe = 'conditional-cli-only'; questionMakeCards = 'interactive-command-root-and-original-destinations-bound'; ocrUnresolved = 'waiting-input-export-blocked'"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority template marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1214.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.14 assembly failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

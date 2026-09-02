[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$templatePath = Join-Path $PSScriptRoot 'New-CoreV252Checkpoint.ps1'
$template = Get-Content -LiteralPath $templatePath -Raw
$replacements = [ordered]@{
    "[string]`$RuntimeVersion = '2.5.1'" = "[string]`$RuntimeVersion = '2.5.2'"
    "[string]`$CheckpointVersion = '2.5.2'" = "[string]`$CheckpointVersion = '2.5.3'"
    "[string]`$ReceiptRoot = 'canary/core-v2.5.2-offline/20260830T095400Z'" = "[string]`$ReceiptRoot = 'canary/core-v2.5.3-offline/20260830T184300Z'"
    "`$adapter.results.Count -ne 11" = "`$adapter.results.Count -ne 12"
    "content = Describe 'content-worker/releases/content.deepseek-0.7.0-win-x64-formal-20260828T103735Z' 'KnowledgeClipper.Content.Worker.exe' 'content.deepseek' '0.7.0' 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'" = "content = Describe 'content-worker/releases/content.deepseek-0.8.0-win-x64-formal-20260830T095330Z' 'KnowledgeClipper.Content.Worker.exe' 'content.deepseek' '0.8.0' 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'"
    "anki = Describe 'releases/knowledge-clipper-anki-exporter-v1.2.1-20260828T104000Z/win-x64/workers/anki' 'KnowledgeClipper.AnkiExporter.Worker.exe' 'export.anki' '1.2.1' '135B7BC31DA93389819E4F802226600CD3879F6527ED4194555D613A32CC4EE1'" = "anki = Describe 'releases/knowledge-clipper-anki-exporter-v1.2.2-20260830T182623Z/win-x64/workers/anki' 'KnowledgeClipper.AnkiExporter.Worker.exe' 'export.anki' '1.2.2' '0D6F18DD6CD10828C0139183D7D84712378FCDDE0AFFCABFCAFD19C228316449'"
    "content = [ordered]@{ path='content-worker/receipts/core-v3-desktop-compatibility/20260830T074659078Z/receipt.v1.json'; sha256='BF933C31A6D0D16C51313CDD6B71D58EB493A11D23F5A01DCFA8A762FCF9513C'; treeSha256='724F2D2732FA13FA236825F7E82E8E8E8DAF4DC1354E36093AB8B79262EDFBEC' }" = "content = [ordered]@{ path='content-worker/receipts/content-ocr-proofreading-0.8.0-admission-20260830T101000Z.json'; sha256='347C18E0131B3A3BC7D61BA711DE39E9332085C76DD555A0088EF46C98E015D1'; treeSha256='9A6A50F4A31EF2D2F194B2F69F9FAE7B22192DC013CDD2FCBD6B82A423139E45' }"
    "anki = [ordered]@{ path='workers/anki-exporter/canary/shared-consumer-compatibility/20260830T074135Z-28e90aac/receipt.json'; sha256='3474DF53E79744A69F49CEB8F2009EC9B76A1120C77EB8BD2400227A2D20EB5F'; treeSha256='E7EBA348C344865B0F403D34187831BABDEADC4818451084ACB86CC37A3E161D' }" = "anki = [ordered]@{ path='canary/anki-v122-xrun/20260830T182803Z/receipt.json'; sha256='4E0E8444E5D55F20EFF6947067FD28E22F4003A2E00AE0210CFFC98ACA16518A'; treeSha256='F9FF222B9144F1CF56E0059225E0E1448639D23C06852733B4ADB3DB865CB66C' }"
    "'tools/validate_contracts.py','tools/test-desktop-brand-surface.ps1','tools/New-CoreV252Checkpoint.ps1'," = "'tools/validate_contracts.py','tools/test-desktop-brand-surface.ps1','tools/New-CoreV253ValidationReceipts.ps1','tools/New-CoreV253Checkpoint.ps1',"
    "'contracts/core/v2.5/checkpoints/v2.5.1-20260830T075400Z/checkpoint.json'" = "'contracts/core/v2.5/checkpoints/v2.5.2-20260830T095700Z/checkpoint.json'"
    "contentSettings=[ordered]@{ configSchemaVersion='content-config-snapshot/1.1.0'; runtimePolicyFrozen=`$true; promptAndCategoryShaFrozen=`$true }" = "contentSettings=[ordered]@{ configSchemaVersion='content-config-snapshot/1.1.0'; runtimePolicyFrozen=`$true; promptAndCategoryShaFrozen=`$true; ocrInput='standard-input/4.0.0'; unresolvedProjection='waiting_input-attentionRequired-export-blocked' }`r`n        interactiveQuestionRoot=[ordered]@{ status='durable-command-root'; canonicalQuestionTurn=`$true; actionHashBound=`$true; acceptedAndResolvedEventsBound=`$true; arbitraryDesktopArtifactsRejected=`$true; workerStartsOnInvalidRoot=0 }"
    "currentAuthority='1.2.12'; intendedAuthority='1.2.13'" = "currentAuthority='1.2.13'; intendedAuthority='1.2.14'"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Checkpoint template marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-CoreV253Checkpoint.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.5.3 checkpoint failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

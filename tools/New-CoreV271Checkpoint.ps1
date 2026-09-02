[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV260Checkpoint.ps1') -Raw
$replacements = [ordered]@{
    '"[string]`$RuntimeVersion = ''2.6.0''"' = '"[string]`$RuntimeVersion = ''2.7.1''"'
    '"[string]`$CheckpointVersion = ''2.6.0''"' = '"[string]`$CheckpointVersion = ''2.7.2''"'
    '"[string]`$ReceiptRoot = ''canary/core-v2.6.0-offline/20260830T120500Z''"' = '"[string]`$ReceiptRoot = ''canary/core-v2.7.1-offline/20260831T062200Z''"'
    "'contracts/core/v2.6/checkpoints/v`$CheckpointVersion-`$ReleaseStamp/checkpoint.json'" = "'contracts/core/v2.7/checkpoints/v`$CheckpointVersion-`$ReleaseStamp/checkpoint.json'"
    '"`$adapter.results.Count -ne 13"' = '"`$adapter.results.Count -ne 14"'
    '"`$public.schemasChecked -ne 69 -or `$public.validFixturesAccepted -ne 74 -or `$public.invalidFixturesRejected -ne 61"' = '"`$public.schemasChecked -ne 73 -or `$public.validFixturesAccepted -ne 76 -or `$public.invalidFixturesRejected -ne 62"'
    "anki = Describe 'releases/knowledge-clipper-anki-exporter-v1.3.0-20260830T113059Z/win-x64/workers/anki' 'KnowledgeClipper.AnkiExporter.Worker.exe' 'export.anki' '1.3.0' 'E38ED8ECB84785A555214C855B083DE1F5277225A197ED49254E15A2DE73C3C7'" = "anki = Describe 'releases/knowledge-clipper-anki-exporter-v1.3.1-20260830T123155Z/win-x64/workers/anki' 'KnowledgeClipper.AnkiExporter.Worker.exe' 'export.anki' '1.3.1' '72F9F1F41A502430FEDCDF6B058C4CE7DC6FF8F092C2BB1888603FCCE79FF5DD'"
    "anki = [ordered]@{ path='workers/anki-exporter/canary/original-media-v1.3.0/20260830T113121Z/qualification-receipt.json'; sha256='0597A29F680114E522D6F8D7D345715A7F8FAF850165279DFE62A3296F7CAAC2'; treeSha256='3784600C634987F25A84E319809E831A645311EFDD49D26231002EBC7F7D44A0' }" = "anki = [ordered]@{ path='workers/anki-exporter/canary/sealed-initial-alias-v1.3.1/20260830T123343Z/qualification-receipt.json'; sha256='AB668B16BF089F6D0A70F451709D1A257B8FB323FB0C00483CF615FFA93FDB3D'; treeSha256='653DABD25F5B17B4FA647739281FD3E29D52E74494FD20242AFBC22456232389' }"
    "'tools/validate_contracts.py','tools/test-desktop-brand-surface.ps1','tools/New-CoreV260ValidationReceipts.ps1','tools/New-CoreV260Checkpoint.ps1'," = "'tools/validate_contracts.py','tools/test-desktop-brand-surface.ps1','tools/New-CoreV271ValidationReceipts.ps1','tools/New-CoreV271Checkpoint.ps1',"
    "'contracts/core/v2.6') -Recurse -File | Where-Object FullName -NotMatch '[\\/]checkpoints[\\/]'" = "'contracts/core/v2.6') -Recurse -File | Where-Object FullName -NotMatch '[\\/]checkpoints[\\/]'`r`n    Get-ChildItem -LiteralPath (Join-Path ```$ProjectRoot 'contracts/core/v2.7') -Recurse -File | Where-Object FullName -NotMatch '[\\/]checkpoints[\\/]'"
    'contracts/core/v2.5/checkpoints/v2.5.3-20260830T184500Z/checkpoint.json' = 'contracts/core/v2.7/checkpoints/v2.7.1-20260830T125000Z/checkpoint.json'
    "questionAction='knowledge-clipper-question-action.v2'; ocrHumanResolutionAction='knowledge-clipper-ocr-human-resolution-action/1.0.0'; taskQuery='2.3.0'; mediaBundle='knowledge-clipper-media-bundle/1.0.0'" = "questionAction='knowledge-clipper-question-action.v2'; ocrHumanResolutionAction='knowledge-clipper-ocr-human-resolution-action/1.0.0'; taskQuery='2.3.0'; mediaBundle='knowledge-clipper-media-bundle/1.0.0'; moduleAction='knowledge-clipper-module-action.v1'; ipc='1.2.0'"
    'readyOnlyContinues=`$true }"' = 'readyOnlyContinues=`$true }`r`n        moduleActions=[ordered]@{ protocol=''1.2''; speechVoices=''read-only-catalog-persisted''; speechPreview=''explicit-side-effect-authorization-required''; remnoteProbe=''sanitized-typed-healthy-degraded-blocked-preserved''; nonzeroNegativeExit=''admitted-only-after-strict-validation''; invalidTypedOutput=''generic-fail-closed''; replay=''exact-idempotent'' }"'
    "speakerPreview='conditional-explicit-physical-audio-not-run'; voiceCatalog='module-admitted-core-persistence-conditional'" = "speakerPreview='explicit-public-action-authorization-required-not-run'; voiceCatalog='public-dispatch-persisted-read-only'"
    "currentAuthority='1.2.14'; intendedAuthority='1.2.15'" = "currentAuthority='1.2.16'; intendedAuthority='1.2.17'"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Checkpoint wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-CoreV271Checkpoint.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.1 checkpoint failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

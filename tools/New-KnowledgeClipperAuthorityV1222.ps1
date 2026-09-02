[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1220.ps1') -Raw
$replacements = [ordered]@{
    "RegistryVersion = ''1.2.20''" = "RegistryVersion = ''1.2.22''"
    'releases/knowledge-clipper-authority-v1.2.19-20260831T094000Z/win-x64' = 'releases/knowledge-clipper-authority-v1.2.20-20260831T102000Z/win-x64'
    'contracts/authority/worker-registry/v1.2.19-20260831T094000Z/worker-registry.v1.json' = 'contracts/authority/worker-registry/v1.2.20-20260831T102000Z/worker-registry.v1.json'
    "baseAuthority = '1.2.19'; supersedesCheckpointSha256 = 'EFC57676C0BEB4A129F319EAF7BADD4E96B883FF67D066EDB9F4A0C6197E9B89'" = "baseAuthority = '1.2.20'; supersedesCheckpointSha256 = '9644D9FD80EB29A57D4B16ED5FCD1FE16475D8101B2C93DF1B21B7530C5F7509'"
    'Authority v1.2.20 assembly failed' = 'Authority v1.2.22 assembly failed'
    '.New-KnowledgeClipperAuthorityV1220.' = '.New-KnowledgeClipperAuthorityV1222.'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.22 wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

# Replace only the formally admitted Anki worker payload and its matching module
# qualification.  The generated assembly retains all existing authority, describe,
# dependency-tree, frozen-contract and Core 2.7.4 gates from v1.2.20.
$marker = '`$template = `$template.Replace(`$contentReceiptSource, `$contentReceiptTarget, [StringComparison]::Ordinal)'
$injection = @'
`$template = `$template.Replace(`$contentReceiptSource, `$contentReceiptTarget, [StringComparison]::Ordinal)
`$ankiSource = "'export.anki' = [ordered]@{ version = '1.3.1'; source = 'releases/knowledge-clipper-anki-exporter-v1.3.1-20260830T123155Z/win-x64/workers/anki'; destination = 'workers/anki'; executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = '72F9F1F41A502430FEDCDF6B058C4CE7DC6FF8F092C2BB1888603FCCE79FF5DD'; treeSha256 = '653DABD25F5B17B4FA647739281FD3E29D52E74494FD20242AFBC22456232389' }"
`$ankiTarget = "'export.anki' = [ordered]@{ version = '1.3.2'; source = 'releases/knowledge-clipper-anki-exporter-v1.3.2-20260831T104520Z/win-x64/workers/anki'; destination = 'workers/anki'; executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = '473007F52678A5233AE6023D52BC0C6D3F28452C00244DEDAFB88131714346F3'; treeSha256 = 'A360A3381E3A4CC33C0AE2ECFDD4F29950EC2467E290AEC69E4163CD6BF1FD2C' }"
if (-not `$template.Contains(`$ankiSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.22 Anki 1.3.1 source marker is missing.' }
`$template = `$template.Replace(`$ankiSource, `$ankiTarget, [StringComparison]::Ordinal)
`$ankiReceiptSource = "'anki-initial-alias-admission.json' = 'workers/anki-exporter/canary/sealed-initial-alias-v1.3.1/20260830T123343Z/qualification-receipt.json'"
`$ankiReceiptTarget = "'anki-versioned-flat-path-admission.json' = 'workers/anki-exporter/canary/sealed-versioned-flat-path-v1.3.2/20260831T104616Z/qualification-receipt.json'"
if (-not `$template.Contains(`$ankiReceiptSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.22 Anki qualification marker is missing.' }
`$template = `$template.Replace(`$ankiReceiptSource, `$ankiReceiptTarget, [StringComparison]::Ordinal)
'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Authority v1.2.22 innermost marker is missing.' }
$template = $template.Replace($marker, $injection, [StringComparison]::Ordinal)

$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1222.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.22 wrapper failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

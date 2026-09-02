[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1219.ps1') -Raw
$replacements = [ordered]@{
    "RegistryVersion = ''1.2.19''" = "RegistryVersion = ''1.2.20''"
    'releases/knowledge-clipper-authority-v1.2.18-20260831T085000Z/win-x64' = 'releases/knowledge-clipper-authority-v1.2.19-20260831T094000Z/win-x64'
    'contracts/authority/worker-registry/v1.2.18-20260831T085000Z/worker-registry.v1.json' = 'contracts/authority/worker-registry/v1.2.19-20260831T094000Z/worker-registry.v1.json'
    "baseAuthority = '1.2.18'; supersedesCheckpointSha256 = '8D61B39DFF413A063E09145C2CBC6CC26B679F7CEEC67BE85CBF5B6796B639E0'" = "baseAuthority = '1.2.19'; supersedesCheckpointSha256 = 'EFC57676C0BEB4A129F319EAF7BADD4E96B883FF67D066EDB9F4A0C6197E9B89'"
    'Authority v1.2.19 assembly failed' = 'Authority v1.2.20 assembly failed'
    '.New-KnowledgeClipperAuthorityV1219.' = '.New-KnowledgeClipperAuthorityV1220.'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.20 wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

# Inject the already-qualified Core 2.7.4 checkpoint into the innermost assembly
# body.  The outer wrappers only rewrite text; the final generated assembly still
# performs the normal path, hash, describe, registry and dependency gates.
$marker = '`$template = `$template.Replace(`$contentReceiptSource, `$contentReceiptTarget, [StringComparison]::Ordinal)'
$injection = @'
`$template = `$template.Replace(`$contentReceiptSource, `$contentReceiptTarget, [StringComparison]::Ordinal)
`$coreVersionSource = "CoreVersion = '2.7.2'"
`$coreVersionTarget = "CoreVersion = '2.7.4'"
if (-not `$template.Contains(`$coreVersionSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.20 Core version marker is missing.' }
`$template = `$template.Replace(`$coreVersionSource, `$coreVersionTarget, [StringComparison]::Ordinal)
`$coreCheckpointSource = 'contracts/core/v2.7/checkpoints/v2.7.3-20260831T084500Z/checkpoint.json'
`$coreCheckpointTarget = 'contracts/core/v2.7/checkpoints/v2.7.7-20260831T100400Z/checkpoint.json'
if (-not `$template.Contains(`$coreCheckpointSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.20 Core checkpoint marker is missing.' }
`$template = `$template.Replace(`$coreCheckpointSource, `$coreCheckpointTarget, [StringComparison]::Ordinal)
`$coreHashSource = '7F468B39DBB55F7D719D786D8ED248850CD7C38CD3BDB60AB05E88D15BF99545'
`$coreHashTarget = '97EE259998C8DC875BF4CBF438E58F8E963C083B0125EC94A4C46EBF4FEDF498'
if (-not `$template.Contains(`$coreHashSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.20 Core checkpoint hash marker is missing.' }
`$template = `$template.Replace(`$coreHashSource, `$coreHashTarget, [StringComparison]::Ordinal)
`$coreReceiptReplacements = [ordered]@{
    'canary/core-v2.7.2-offline/20260831T084500Z/settings-control.receipt.json' = 'canary/core-v2.7.4-offline/20260831T100200Z/settings-control.receipt.json'
    'canary/core-v2.7.2-offline/20260831T084500Z/core-adapter.receipt.json' = 'canary/core-v2.7.4-offline/20260831T100200Z/core-adapter.receipt.json'
    'canary/core-v2.7.2-offline/20260831T084500Z/orchestrator-contract-tests.receipt.json' = 'canary/core-v2.7.4-offline/20260831T100200Z/orchestrator-contract-tests.receipt.json'
    'canary/core-v2.7.2-offline/20260831T084500Z/question-workflow.receipt.json' = 'canary/core-v2.7.4-offline/20260831T100200Z/question-workflow.receipt.json'
    'canary/core-v2.7.2-offline/20260831T084500Z/public-contracts.receipt.json' = 'canary/core-v2.7.4-offline/20260831T100200Z/public-contracts.receipt.json'
}
foreach (`$pair in `$coreReceiptReplacements.GetEnumerator()) {
    if (-not `$template.Contains(`$pair.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.20 Core receipt marker is missing: `$(`$pair.Key)" }
    `$template = `$template.Replace(`$pair.Key, `$pair.Value, [StringComparison]::Ordinal)
}
'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Authority v1.2.20 innermost marker is missing.' }
$template = $template.Replace($marker, $injection, [StringComparison]::Ordinal)

$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1220.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.20 wrapper failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

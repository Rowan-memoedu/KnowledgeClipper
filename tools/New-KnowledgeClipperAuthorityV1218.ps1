[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1217.ps1') -Raw
$replacements = [ordered]@{
    "RegistryVersion = ''1.2.17''" = "RegistryVersion = ''1.2.18''"
    "CoreVersion = ''2.7.1''" = "CoreVersion = ''2.7.2''"
    'contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json' = 'contracts/core/v2.7/checkpoints/v2.7.3-20260831T084500Z/checkpoint.json'
    'releases/knowledge-clipper-authority-v1.2.16-20260830T125200Z/win-x64' = 'releases/knowledge-clipper-authority-v1.2.17-20260831T062800Z/win-x64'
    'contracts/authority/worker-registry/v1.2.16-20260830T125200Z/worker-registry.v1.json' = 'contracts/authority/worker-registry/v1.2.17-20260831T062800Z/worker-registry.v1.json'
    'B22CA9746905AC923CBFFEF913A7CAEB3D7FF0EDDE8507145E6A7B99BBD09F65' = '7F468B39DBB55F7D719D786D8ED248850CD7C38CD3BDB60AB05E88D15BF99545'
    'canary/core-v2.7.1-offline/20260831T062200Z/settings-control.receipt.json' = 'canary/core-v2.7.2-offline/20260831T084500Z/settings-control.receipt.json'
    'canary/core-v2.7.1-offline/20260831T062200Z/core-adapter.receipt.json' = 'canary/core-v2.7.2-offline/20260831T084500Z/core-adapter.receipt.json'
    'canary/core-v2.7.1-offline/20260831T062200Z/orchestrator-contract-tests.receipt.json' = 'canary/core-v2.7.2-offline/20260831T084500Z/orchestrator-contract-tests.receipt.json'
    'canary/core-v2.7.1-offline/20260831T062200Z/question-workflow.receipt.json' = 'canary/core-v2.7.2-offline/20260831T084500Z/question-workflow.receipt.json'
    'canary/core-v2.7.1-offline/20260831T062200Z/public-contracts.receipt.json' = 'canary/core-v2.7.2-offline/20260831T084500Z/public-contracts.receipt.json'
    "baseAuthority = '1.2.16'; supersedesCheckpointSha256 = '48D6E25E967F73F1D0489DF1A6BA8C8F93212B5FAD1FAD832C5C09A3D58AC046'" = "baseAuthority = '1.2.17'; supersedesCheckpointSha256 = '862ED5C41EC6BAA5F8B1446C9AA68DD131CEA7784D9EA8985CE54D42F2ACF95D'"
    'Authority v1.2.17 assembly failed' = 'Authority v1.2.18 assembly failed'
    '.New-KnowledgeClipperAuthorityV1217.' = '.New-KnowledgeClipperAuthorityV1218.'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) {
        throw "Authority v1.2.18 wrapper marker is missing: $($pair.Key)"
    }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

# v1.2.17 and v1.2.16 are both wrappers.  Insert the Image replacement into
# v1.2.16's runtime body, after it has materialised the v1.2.15 assembly template.
$nestedMarker = '$temporary = Join-Path $PSScriptRoot (''.New-KnowledgeClipperAuthorityV1218.'
$nestedReplacement = @'
$innerMarker = '$temporary = Join-Path $PSScriptRoot (''.New-KnowledgeClipperAuthorityV1218.'
$innerPatch = @"
`$imageSource = "version = '1.2.3'; source = 'workers/image-ocr/candidates/1.2.3'; destination = 'workers/image-ocr'; executable = 'workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe'; sha256 = '61E10D6B89B6BE29EB653D526FED9872ADC9C3A376FFFB1857AF10F2EBB82C9A'; treeSha256 = 'D41726200D01DEE7F9288EB77E2F326AE2B5812EFE5B6DC4AC320D46A34159E5'"
`$imageTarget = "version = '1.2.4'; source = 'workers/image-ocr/candidates/1.2.4'; destination = 'workers/image-ocr'; executable = 'workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe'; sha256 = '2E0C8D2ED9741FDBC9F482330EB4181719A5877F425145FEE8672CD119C86AC4'; treeSha256 = 'F88730086D1237BB099EE816864FA2034E4B28206362D0362D7A82F4C50D05C3'"
if (-not `$template.Contains(`$imageSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.18 Image 1.2.3 source marker is missing.' }
`$template = `$template.Replace(`$imageSource, `$imageTarget, [StringComparison]::Ordinal)

"@
if (-not $template.Contains($innerMarker, [StringComparison]::Ordinal)) { throw 'Authority v1.2.18 v1.2.16 template marker is missing.' }
$template = $template.Replace($innerMarker, ($innerPatch + $innerMarker), [StringComparison]::Ordinal)

'@
if (-not $template.Contains($nestedMarker, [StringComparison]::Ordinal)) { throw 'Authority v1.2.18 nested template marker is missing.' }
$template = $template.Replace($nestedMarker, ($nestedReplacement + $nestedMarker), [StringComparison]::Ordinal)

$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1218.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.18 wrapper failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV271Checkpoint.ps1') -Raw
$replacements = [ordered]@{
    "RuntimeVersion = ''2.7.1''" = "RuntimeVersion = ''2.7.2''"
    "CheckpointVersion = ''2.7.2''" = "CheckpointVersion = ''2.7.3''"
    "ReceiptRoot = ''canary/core-v2.7.1-offline/20260831T062200Z''" = "ReceiptRoot = ''canary/core-v2.7.2-offline/20260831T084500Z''"
    'contracts/core/v2.7/checkpoints/v2.7.1-20260830T125000Z/checkpoint.json' = 'contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json'
    'tools/New-CoreV271ValidationReceipts.ps1' = 'tools/New-CoreV272ValidationReceipts.ps1'
    'tools/New-CoreV271Checkpoint.ps1' = 'tools/New-CoreV272Checkpoint.ps1'
    "currentAuthority='1.2.16'; intendedAuthority='1.2.17'" = "currentAuthority='1.2.17'; intendedAuthority='1.2.18'"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Checkpoint v2.7.3 wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$nestedMarker = '$temporary = Join-Path $PSScriptRoot (''.New-CoreV271Checkpoint.'
$nestedReplacement = @'
$imageSource = "image = Describe 'workers/image-ocr/candidates/1.2.3' 'KnowledgeClipper.ImageOcr.Worker.exe' 'image.ocr.mineru' '1.2.3' '61E10D6B89B6BE29EB653D526FED9872ADC9C3A376FFFB1857AF10F2EBB82C9A'"
$imageTarget = "image = Describe 'workers/image-ocr/candidates/1.2.4' 'KnowledgeClipper.ImageOcr.Worker.exe' 'image.ocr.mineru' '1.2.4' '2E0C8D2ED9741FDBC9F482330EB4181719A5877F425145FEE8672CD119C86AC4'"
$receiptSource = "image = [ordered]@{ path='workers/image-ocr/candidates/1.2.3/evidence/core-continuation-contract-evidence.v4.json'; sha256='39F798EC218D0704586168E1C91C658DD115694D2F96557571D6DA34B93999C9'; treeSha256='D41726200D01DEE7F9288EB77E2F326AE2B5812EFE5B6DC4AC320D46A34159E5' }"
$receiptTarget = "image = [ordered]@{ path='workers/image-ocr/candidates/1.2.4/evidence/core-continuation-contract-evidence.v5.json'; sha256='35A70D90BC899FD520C987FFF6C5652089D7734E30B925FA392EB48529C8F46A'; treeSha256='F88730086D1237BB099EE816864FA2034E4B28206362D0362D7A82F4C50D05C3' }"
$imageReplacements = [ordered]@{ $imageSource=$imageTarget; $receiptSource=$receiptTarget }
foreach ($pair in $imageReplacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Core v2.7.3 nested Image marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

'@
if (-not $template.Contains($nestedMarker, [StringComparison]::Ordinal)) { throw 'Core v2.7.3 nested template marker is missing.' }
$template = $template.Replace($nestedMarker, ($nestedReplacement + $nestedMarker), [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-CoreV272Checkpoint.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.2 checkpoint failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

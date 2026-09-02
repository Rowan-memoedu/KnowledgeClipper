[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV272Checkpoint.ps1') -Raw
$replacements = [ordered]@{
    "RuntimeVersion = ''2.7.2''" = "RuntimeVersion = ''2.7.3''"
    "CheckpointVersion = ''2.7.3''" = "CheckpointVersion = ''2.7.4''"
    "ReceiptRoot = ''canary/core-v2.7.2-offline/20260831T084500Z''" = "ReceiptRoot = ''canary/core-v2.7.3-offline/20260831T100000Z''"
    'contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json' = 'contracts/core/v2.7/checkpoints/v2.7.3-20260831T084500Z/checkpoint.json'
    'tools/New-CoreV272ValidationReceipts.ps1' = 'tools/New-CoreV273ValidationReceipts.ps1'
    'tools/New-CoreV272Checkpoint.ps1' = 'tools/New-CoreV273Checkpoint.ps1'
    '"`$adapter.results.Count -ne 14"' = '"`$adapter.results.Count -ne 15"'
    "currentAuthority='1.2.17'; intendedAuthority='1.2.18'" = "currentAuthority='1.2.19'; intendedAuthority='1.2.20'"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Checkpoint v2.7.4 wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

# This wrapper reaches the original v2.6 checkpoint builder through two earlier
# append-only wrappers.  Replace the formal Content input at that innermost
# assembly point, so the checkpoint binds Content 0.9.1 rather than merely
# recording a source-tree change beside the older 0.9.0 packet.
$marker = '`$template = `$template.Replace(`$imageSource, `$imageTarget, [StringComparison]::Ordinal)'
$injection = @'
`$template = `$template.Replace(`$imageSource, `$imageTarget, [StringComparison]::Ordinal)
`$contentSource = "content = Describe 'content-worker/releases/content.deepseek-0.9.0-win-x64-formal-20260830T113459Z' 'KnowledgeClipper.Content.Worker.exe' 'content.deepseek' '0.9.0' 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'"
`$contentTarget = "content = Describe 'content-worker/releases/content.deepseek-0.9.1-win-x64-formal-20260831T092939Z' 'KnowledgeClipper.Content.Worker.exe' 'content.deepseek' '0.9.1' 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'"
if (-not `$template.Contains(`$contentSource, [StringComparison]::Ordinal)) { throw 'Core v2.7.4 Content source marker is missing.' }
`$template = `$template.Replace(`$contentSource, `$contentTarget, [StringComparison]::Ordinal)
`$contentReceiptSource = "content = [ordered]@{ path='content-worker/receipts/content-ocr-human-resolution-0.9.0-admission-20260830T114000Z.json'; sha256='6AF5DDBFBA86BC6C4FAA256998500AF4CCBF2FD1B6335B2BCA82BC489BDEC4A0'; treeSha256='7269891A09766D4D6F0CAEE8D97B862784A4F71E4EF7DE95A3A7088544587EF0' }"
`$contentReceiptTarget = "content = [ordered]@{ path='content-worker/receipts/content-ocr-coordinate-recovery-0.9.1-admission-20260831T093500Z.json'; sha256='6B3F8A6F2EC922CE4174E4321165A7CBAB54D6E5A3B68AA7E478E98A1474DE19'; treeSha256='9C4B332E0EB81D9B039AFEA8CB3CE9FE392CF96781FEE490F8A5880A518894A0' }"
if (-not `$template.Contains(`$contentReceiptSource, [StringComparison]::Ordinal)) { throw 'Core v2.7.4 Content receipt marker is missing.' }
`$template = `$template.Replace(`$contentReceiptSource, `$contentReceiptTarget, [StringComparison]::Ordinal)
'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Core v2.7.4 nested template marker is missing.' }
$template = $template.Replace($marker, $injection, [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-CoreV273Checkpoint.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.4 checkpoint failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

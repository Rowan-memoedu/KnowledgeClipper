[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ReleaseStamp, [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV271Checkpoint.ps1') -Raw
$replacements = [ordered]@{
    "[string]`$RuntimeVersion = '2.6.0'" = "[string]`$RuntimeVersion = '2.7.4'"
    "[string]`$CheckpointVersion = '2.6.0'" = "[string]`$CheckpointVersion = '2.7.5'"
    "[string]`$ReceiptRoot = 'canary/core-v2.6.0-offline/20260830T120500Z'" = "[string]`$ReceiptRoot = 'canary/core-v2.7.4-offline/20260831T100200Z'"
    'contracts/core/v2.6/checkpoints/v$CheckpointVersion-$ReleaseStamp/checkpoint.json' = 'contracts/core/v2.7/checkpoints/v$CheckpointVersion-$ReleaseStamp/checkpoint.json'
    '"`$adapter.results.Count -ne 13"' = '"`$adapter.results.Count -ne 15"'
    '"`$public.schemasChecked -ne 69 -or `$public.validFixturesAccepted -ne 74 -or `$public.invalidFixturesRejected -ne 61"' = '"`$public.schemasChecked -ne 73 -or `$public.validFixturesAccepted -ne 76 -or `$public.invalidFixturesRejected -ne 62"'
    'contracts/core/v2.7/checkpoints/v2.7.1-20260830T125000Z/checkpoint.json' = 'contracts/core/v2.7/checkpoints/v2.7.3-20260831T084500Z/checkpoint.json'
    'tools/New-CoreV271ValidationReceipts.ps1' = 'tools/New-CoreV274ValidationReceipts.ps1'
    'tools/New-CoreV271Checkpoint.ps1' = 'tools/New-CoreV274Checkpoint.ps1'
    "currentAuthority='1.2.16'; intendedAuthority='1.2.17'" = "currentAuthority='1.2.19'; intendedAuthority='1.2.20'"
}
foreach ($pair in $replacements.GetEnumerator()) { if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Core v2.7.5 wrapper marker is missing: $($pair.Key)" }; $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal) }

# The wrapper just loaded will transform the stable v2.6 checkpoint template.
# Amend its worker inputs there, before it emits the new append-only checkpoint.
$marker = 'foreach ($pair in $replacements.GetEnumerator()) {'
$injection = @'
$imageSource = "image = Describe 'workers/image-ocr/candidates/1.2.3' 'KnowledgeClipper.ImageOcr.Worker.exe' 'image.ocr.mineru' '1.2.3' '61E10D6B89B6BE29EB653D526FED9872ADC9C3A376FFFB1857AF10F2EBB82C9A'"
$imageTarget = "image = Describe 'workers/image-ocr/candidates/1.2.4' 'KnowledgeClipper.ImageOcr.Worker.exe' 'image.ocr.mineru' '1.2.4' '2E0C8D2ED9741FDBC9F482330EB4181719A5877F425145FEE8672CD119C86AC4'"
$contentSource = "content = Describe 'content-worker/releases/content.deepseek-0.9.0-win-x64-formal-20260830T113459Z' 'KnowledgeClipper.Content.Worker.exe' 'content.deepseek' '0.9.0' 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'"
$contentTarget = "content = Describe 'content-worker/releases/content.deepseek-0.9.1-win-x64-formal-20260831T092939Z' 'KnowledgeClipper.Content.Worker.exe' 'content.deepseek' '0.9.1' 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'"
$receiptSource = "content = [ordered]@{ path='content-worker/receipts/content-ocr-human-resolution-0.9.0-admission-20260830T114000Z.json'; sha256='6AF5DDBFBA86BC6C4FAA256998500AF4CCBF2FD1B6335B2BCA82BC489BDEC4A0'; treeSha256='7269891A09766D4D6F0CAEE8D97B862784A4F71E4EF7DE95A3A7088544587EF0' }"
$receiptTarget = "content = [ordered]@{ path='content-worker/receipts/content-ocr-coordinate-recovery-0.9.1-admission-20260831T093500Z.json'; sha256='6B3F8A6F2EC922CE4174E4321165A7CBAB54D6E5A3B68AA7E478E98A1474DE19'; treeSha256='9C4B332E0EB81D9B039AFEA8CB3CE9FE392CF96781FEE490F8A5880A518894A0' }"
foreach ($pair in [ordered]@{ $imageSource=$imageTarget; $contentSource=$contentTarget; $receiptSource=$receiptTarget }.GetEnumerator()) { if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Core v2.7.5 worker marker is missing: $($pair.Key)" }; $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal) }

'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Core v2.7.5 injection marker is missing.' }
$template = $template.Replace($marker, ($injection + $marker), [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-CoreV274Checkpoint.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try { [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false)); & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot; if ($LASTEXITCODE -ne 0) { throw "Core v2.7.5 checkpoint failed with exit code $LASTEXITCODE." } }
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

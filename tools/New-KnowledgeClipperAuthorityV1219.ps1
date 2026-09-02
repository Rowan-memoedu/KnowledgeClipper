[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1218.ps1') -Raw
$replacements = [ordered]@{
    "RegistryVersion = ''1.2.18''" = "RegistryVersion = ''1.2.19''"
    'releases/knowledge-clipper-authority-v1.2.17-20260831T062800Z/win-x64' = 'releases/knowledge-clipper-authority-v1.2.18-20260831T085000Z/win-x64'
    'contracts/authority/worker-registry/v1.2.17-20260831T062800Z/worker-registry.v1.json' = 'contracts/authority/worker-registry/v1.2.18-20260831T085000Z/worker-registry.v1.json'
    "baseAuthority = '1.2.17'; supersedesCheckpointSha256 = '862ED5C41EC6BAA5F8B1446C9AA68DD131CEA7784D9EA8985CE54D42F2ACF95D'" = "baseAuthority = '1.2.18'; supersedesCheckpointSha256 = '8D61B39DFF413A063E09145C2CBC6CC26B679F7CEEC67BE85CBF5B6796B639E0'"
    'Authority v1.2.18 assembly failed' = 'Authority v1.2.19 assembly failed'
    '.New-KnowledgeClipperAuthorityV1218.' = '.New-KnowledgeClipperAuthorityV1219.'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.19 wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

# The v1.2.18 wrapper injects its Image replacement into the original v1.2.15
# assembly body.  Add Content 0.9.1 at that same innermost point so every copied
# release file and its dependency manifest are regenerated from the formal root.
$marker = '`$template = `$template.Replace(`$imageSource, `$imageTarget, [StringComparison]::Ordinal)'
$injection = @'
`$template = `$template.Replace(`$imageSource, `$imageTarget, [StringComparison]::Ordinal)
`$contentSource = "'content.deepseek' = [ordered]@{ version = '0.9.0'; source = 'content-worker/releases/content.deepseek-0.9.0-win-x64-formal-20260830T113459Z'; destination = 'workers/content'; executable = 'workers/content/KnowledgeClipper.Content.Worker.exe'; sha256 = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'; treeSha256 = '7269891A09766D4D6F0CAEE8D97B862784A4F71E4EF7DE95A3A7088544587EF0' }"
`$contentTarget = "'content.deepseek' = [ordered]@{ version = '0.9.1'; source = 'content-worker/releases/content.deepseek-0.9.1-win-x64-formal-20260831T092939Z'; destination = 'workers/content'; executable = 'workers/content/KnowledgeClipper.Content.Worker.exe'; sha256 = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'; treeSha256 = '9C4B332E0EB81D9B039AFEA8CB3CE9FE392CF96781FEE490F8A5880A518894A0' }"
if (-not `$template.Contains(`$contentSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.19 Content 0.9.0 source marker is missing.' }
`$template = `$template.Replace(`$contentSource, `$contentTarget, [StringComparison]::Ordinal)
`$contentReceiptSource = "'content-ocr-human-resolution-admission.json' = 'content-worker/receipts/content-ocr-human-resolution-0.9.0-admission-20260830T114000Z.json'"
`$contentReceiptTarget = "'content-ocr-coordinate-recovery-admission.json' = 'content-worker/receipts/content-ocr-coordinate-recovery-0.9.1-admission-20260831T093500Z.json'"
if (-not `$template.Contains(`$contentReceiptSource, [StringComparison]::Ordinal)) { throw 'Authority v1.2.19 Content receipt marker is missing.' }
`$template = `$template.Replace(`$contentReceiptSource, `$contentReceiptTarget, [StringComparison]::Ordinal)
'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Authority v1.2.19 innermost marker is missing.' }
$template = $template.Replace($marker, $injection, [StringComparison]::Ordinal)

$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1219.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.19 wrapper failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

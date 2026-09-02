[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1227.ps1') -Raw
$replacements = [ordered]@{
    "v2.7.14-20260901T015000Z/checkpoint.json" = "v2.7.15-20260901T021500Z/checkpoint.json"
    "1738972159A6074F9184D4D67E46D47491C554584AE7A8C259E14D6495B71548" = "8D4BA1363C13FEB03F9EE5A7F2B69D03FE5731188CE93A109AA407B2FD3D295B"
    "v1.2.27" = "v1.2.28"
    "1.2.27" = "1.2.28"
    "20260901T020000Z" = "20260901T022000Z"
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.28 template marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1228.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.28 assembly failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

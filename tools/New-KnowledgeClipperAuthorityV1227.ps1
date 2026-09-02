[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1226.ps1') -Raw
$replacements = [ordered]@{
    "v2.7.12-20260901T013000Z/checkpoint.json" = "v2.7.14-20260901T015000Z/checkpoint.json"
    "50C13D1968B4466E5C9AA5E59397DB5912D20253EA5FCCA400B5CA6E469747A5" = "1738972159A6074F9184D4D67E46D47491C554584AE7A8C259E14D6495B71548"
    "v1.2.26" = "v1.2.27"
    "1.2.26" = "1.2.27"
    "20260901T013500Z" = "20260901T020000Z"
    "content.deepseek-0.9.2-win-x64-formal-20260901T012100Z" = "content.deepseek-0.9.3-win-x64-formal-20260901T013945Z"
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.27 template marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1227.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.27 assembly failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

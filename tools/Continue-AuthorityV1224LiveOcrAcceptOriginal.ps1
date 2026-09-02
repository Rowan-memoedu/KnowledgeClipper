[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Continue-AuthorityV1223LiveOcrAcceptOriginal.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.23-20260831T112000Z' = 'v1.2.24-20260901T010500Z'
    'v1223' = 'v1224'
    '1.2.23' = '1.2.24'
    'c342d7309f5c0ff0ecac5754dd38e62eef463664597b7cc399f1346de9f1201c' = 'cb9f3291625cb6e7309aa3f677ce783fc713a18b54c29f65a553aa590c8187d7'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) {
        throw "v1.2.24 continuation marker missing: $($replacement.Key)"
    }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporaryScript = Join-Path $PSScriptRoot ('.Continue-AuthorityV1224.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporaryScript, $template, [Text.UTF8Encoding]::new($false))
    & $temporaryScript -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE) { throw "v1.2.24 continuation failed: $LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $temporaryScript) { [IO.File]::Delete($temporaryScript) }
}

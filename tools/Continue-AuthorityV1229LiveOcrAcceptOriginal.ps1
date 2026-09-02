[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Continue-AuthorityV1223LiveOcrAcceptOriginal.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.23-20260831T112000Z' = 'v1.2.29-20260901T023500Z'
    'AuthorityV1223' = 'AuthorityV1229'
    'v1223' = 'v1229'
    '1.2.23' = '1.2.29'
    'c342d7309f5c0ff0ecac5754dd38e62eef463664597b7cc399f1346de9f1201c' = '584b76d471374d96a6732afbf22d3fd2a08ffbb7ddec92c5d7733983747a9735'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) {
        throw "v1.2.29 continuation runner marker missing: $($replacement.Key)"
    }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$pathMarker = "`$ErrorActionPreference = 'Stop'"
if (-not $template.Contains($pathMarker, [StringComparison]::Ordinal)) {
    throw 'v1.2.29 continuation runner absolute-path insertion marker missing.'
}
$continuationMarker = '$continuation = Join-Path $EvidenceRoot ''continuation'''
if (-not $template.Contains($continuationMarker, [StringComparison]::Ordinal)) {
    throw 'v1.2.29 continuation runner root marker missing.'
}
$template = $template.Replace($continuationMarker, '$continuation = Join-Path $EvidenceRoot ''continuation-retry-absolute-ipc''', [StringComparison]::Ordinal)
$actionMarker = @'
JsonOnce $actionPath ([ordered]@{
    schemaVersion='knowledge-clipper-ocr-human-resolution-action.v1'
    decisionKind='ocr-human-resolution'
    targetToken=$targetToken
    action='accept_original'
    reason='The source image itself visibly contains CANAR; preserve the source text without guessing or rerunning OCR.'
})
'@
$actionReplacement = @'
$expectedAction = [ordered]@{
    schemaVersion='knowledge-clipper-ocr-human-resolution-action.v1'
    decisionKind='ocr-human-resolution'
    targetToken=$targetToken
    action='accept_original'
    reason='The source image itself visibly contains CANAR; preserve the source text without guessing or rerunning OCR.'
}
if (Test-Path -LiteralPath $actionPath -PathType Leaf) {
    $existingAction = Get-Content -LiteralPath $actionPath -Raw | ConvertFrom-Json -Depth 20
    if ($existingAction.schemaVersion -ne $expectedAction.schemaVersion -or
        $existingAction.decisionKind -ne $expectedAction.decisionKind -or
        $existingAction.targetToken -ne $targetToken -or
        $existingAction.action -ne $expectedAction.action -or
        $existingAction.reason -ne $expectedAction.reason) {
        throw 'Existing OCR human-resolution action does not match the immutable projected decision.'
    }
}
else {
    JsonOnce $actionPath $expectedAction
}
'@
if (-not $template.Contains($actionMarker, [StringComparison]::Ordinal)) {
    throw 'v1.2.29 continuation runner action marker missing.'
}
$template = $template.Replace($actionMarker, $actionReplacement.TrimEnd(), [StringComparison]::Ordinal)
$template = $template.Replace($pathMarker, ($pathMarker + "`r`n`$EvidenceRoot = [IO.Path]::GetFullPath(`$EvidenceRoot)"), [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.Continue-AuthorityV1229.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "v1.2.29 live continuation failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

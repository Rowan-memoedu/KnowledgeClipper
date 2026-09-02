[CmdletBinding()]
param(
    [string]$EvidenceRoot = '',
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$templatePath = Join-Path $PSScriptRoot 'Invoke-AuthorityV1213QuestionAnkiCanary.ps1'
$template = Get-Content -LiteralPath $templatePath -Raw
$replacements = [ordered]@{
    'knowledge-clipper-authority-v1.2.13-20260830T100000Z' = 'knowledge-clipper-authority-v1.2.14-20260830T184700Z'
    'v1.2.13-20260830T100000Z' = 'v1.2.14-20260830T184700Z'
    'sealed-question-anki-v1.2.13' = 'sealed-question-anki-v1.2.14'
    'AuthorityV1213' = 'AuthorityV1214'
    'authority-v1213' = 'authority-v1214'
    'v1213' = 'v1214'
    'v1.2.13' = 'v1.2.14'
    '1.2.13' = '1.2.14'
    '63bd9525e97e309ed9d8047e36f23668d6f7ae3cebdeea62157494cef7293cec' = 'a1a95ee7fcbbbb3051777088e66389ec2c6578b30ad7562cfb0992de4729897c'
    'af9d8494a47ace7cf28acad561cca648d1d4ec7bdcbabb305a3fe31fca608be7' = 'c2dd2e6c2099e9d7a2d4c742a53bede28ea229cef50d849a464f7c20314121d4'
    '7566601ed455e7fe1c48cfab29b2a4074ec4b05cebad30bae8ac269153e75a1b' = '1491d70a3cce57ec37672c209c9da5d0738236acc2d8bddc3f2067530c917ff3'
    '0a82124f5408d5239414c4f995afcc81aec7dc29c04723f8d8ba7bc3fde507fb' = '30f1a8e65a335811545114474573196b689657da8bafb511e7a2f84638db9d3e'
    'content.deepseek@0.7.0' = 'content.deepseek@0.8.0'
    'export.anki@1.2.1' = 'export.anki@1.2.2'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Canary template marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1214QuestionAnkiCanary.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    $arguments = @{ AnkiDeck = $AnkiDeck }
    if (-not [string]::IsNullOrWhiteSpace($EvidenceRoot)) { $arguments.EvidenceRoot = $EvidenceRoot }
    & $temporary @arguments
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

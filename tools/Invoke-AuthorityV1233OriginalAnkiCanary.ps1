[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-original-anki-v1.2.33\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1215OriginalAnkiCanary.ps1') -Raw
$replacements = [ordered]@{
    'releases\knowledge-clipper-authority-v1.2.15-20260830T121200Z\win-x64' = 'releases\knowledge-clipper-authority-v1.2.33-20260901T041000Z\win-x64'
    'contracts\authority\worker-registry\v1.2.15-20260830T121200Z' = 'contracts\authority\worker-registry\v1.2.33-20260901T041000Z'
    '0ea0131dda8a13667fbd9f758e05c27c8c8aca7ed982939ccbe6e042d8a35830' = '82c168a61eb2261f25cb85ec0b9d4a32c96cf4aa6d5b3a17fd5891ead7bb7f6c'
    'b5f1ef63ef812d86c360d0afb46ecdfb220c96c24cd6cf2252e18411d82084a1' = '83f7a5d86f22e665eda4c346150df3a9ee4235f0b8e1cfeff513997da81ae13d'
    '1ee66639d12c0fa9ebe809117c7bc02789a562c3a1063cbcae874a073c38524a' = 'a7bba2667f1d1ee5d51ae8cd6b5e20054a32eb87b8ab0526946b6ad92d4e35b0'
    'E5067C1B93A47B5650A2B1A049F974A73F66E31C3A5CEFC5DBD4D33B623190D4' = '4DBA693C35F2DFB36B4808672976B1AA1B56CC95446AE5D289E8FCD52D08675F'
    'v1.2.15' = 'v1.2.33'
    "version='1.2.15'" = "version='1.2.33'"
    'v1215' = 'v1233'
    "worker='export.anki@1.3.0'" = "worker='export.anki@1.3.2'"
    "worker='image.ocr.mineru@1.2.3'" = "worker='image.ocr.mineru@1.2.4'"
    'Core v2.6.0 Orchestrator drifted.' = 'Core v2.7.9 Orchestrator drifted.'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "v1.2.33 original-media runner marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1233OriginalAnki.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "v1.2.33 original Anki Canary failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }

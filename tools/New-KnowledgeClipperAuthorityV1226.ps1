[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1223.ps1') -Raw
$replacements = [ordered]@{
    "'releases\knowledge-clipper-authority-v1.2.22-20260831T110500Z\win-x64'" = "'releases\knowledge-clipper-authority-v1.2.24-20260901T010500Z\win-x64'"
    "'contracts\authority\worker-registry\v1.2.22-20260831T110500Z'" = "'contracts\authority\worker-registry\v1.2.24-20260901T010500Z'"
    "'contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json'" = "'contracts/core/v2.7/checkpoints/v2.7.12-20260901T013000Z/checkpoint.json'"
    "'88713A6638BE74587F371287E83ED11056E13CC389F11E052DFCC5C719B5BE0C'" = "'50C13D1968B4466E5C9AA5E59397DB5912D20253EA5FCCA400B5CA6E469747A5'"
    '$releaseId = "knowledge-clipper-authority-v1.2.23-$ReleaseStamp"' = '$releaseId = "knowledge-clipper-authority-v1.2.26-$ReleaseStamp"'
    '$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.23-$ReleaseStamp"' = '$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.26-$ReleaseStamp"'
    '$stagingAuthority = Join-Path $authorityParent (''.v1.2.23-'' + $ReleaseStamp' = '$stagingAuthority = Join-Path $authorityParent (''.v1.2.26-'' + $ReleaseStamp'
    "if (`$registry.registryVersion -ne '1.2.22'" = "if (`$registry.registryVersion -ne '1.2.24'"
    "`$registry.registryVersion = '1.2.23'" = "`$registry.registryVersion = '1.2.26'"
    "`$validation.registryVersion -ne '1.2.23'" = "`$validation.registryVersion -ne '1.2.26'"
    "registryVersion = '1.2.23'" = "registryVersion = '1.2.26'"
    "'1.2.20'; checkpointSha256 = '9644D9FD80EB29A57D4B16ED5FCD1FE16475D8101B2C93DF1B21B7530C5F7509'" = "'1.2.24'; checkpointSha256 = 'B2103C2C6ECCA92EC38695BDCE4E10EDEB18A7F981C580677B6F4B357D5D4AAF'"
    "version = '2.7.8'; coreVersion = '2.7.4'" = "version = '2.7.12'; coreVersion = '2.7.8'"
    "[string]`$ReleaseStamp = '20260831T112000Z'" = "[string]`$ReleaseStamp = '20260901T013500Z'"
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.26 template marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$marker = '    Copy-Tree $sourceRelease $stagingRoot'
$injection = @'
    Copy-Tree $sourceRelease $stagingRoot
    $newRuntime = Join-Path $ProjectRoot 'dist\orchestrator\win-x64\v2.7.8'
    $stagedRuntime = Join-Path $stagingRoot 'orchestrator'
    $contentRelease = Join-Path $ProjectRoot 'content-worker\releases\content.deepseek-0.9.2-win-x64-formal-20260901T012100Z'
    $stagedContent = Join-Path $stagingRoot 'workers\content'
    foreach ($required in @($newRuntime, $contentRelease)) {
        if (-not (Test-Path -LiteralPath $required -PathType Container)) { throw "Missing authority v1.2.26 staged input: $required" }
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($newRuntime, '*', [IO.SearchOption]::AllDirectories)) {
        $destination = Join-Path $stagedRuntime ([IO.Path]::GetRelativePath($newRuntime, $file))
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($file, $destination, $true)
    }
    [IO.Directory]::Delete($stagedContent, $true)
    Copy-Tree $contentRelease $stagedContent
'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Authority content/runtime replacement marker missing.' }
$template = $template.Replace($marker, $injection.TrimEnd(), [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1226.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.26 assembly failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

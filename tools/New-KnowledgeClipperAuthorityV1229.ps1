[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1223.ps1') -Raw
$replacements = [ordered]@{
    "'releases\knowledge-clipper-authority-v1.2.22-20260831T110500Z\win-x64'" = "'releases\knowledge-clipper-authority-v1.2.28-20260901T022000Z\win-x64'"
    "'contracts\authority\worker-registry\v1.2.22-20260831T110500Z'" = "'contracts\authority\worker-registry\v1.2.28-20260901T022000Z'"
    "'contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json'" = "'contracts/core/v2.7/checkpoints/v2.7.16-20260901T023000Z/checkpoint.json'"
    "'88713A6638BE74587F371287E83ED11056E13CC389F11E052DFCC5C719B5BE0C'" = "'779F9DCD4E3B13644C4C7FC1DA2A2ED8ECDB8B8759DE07AC42CA0EADF23B19D9'"
    '$releaseId = "knowledge-clipper-authority-v1.2.23-$ReleaseStamp"' = '$releaseId = "knowledge-clipper-authority-v1.2.29-$ReleaseStamp"'
    '$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.23-$ReleaseStamp"' = '$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.29-$ReleaseStamp"'
    '$stagingAuthority = Join-Path $authorityParent (''.v1.2.23-'' + $ReleaseStamp' = '$stagingAuthority = Join-Path $authorityParent (''.v1.2.29-'' + $ReleaseStamp'
    "if (`$registry.registryVersion -ne '1.2.22'" = "if (`$registry.registryVersion -ne '1.2.28'"
    "`$registry.registryVersion = '1.2.23'" = "`$registry.registryVersion = '1.2.29'"
    "`$validation.registryVersion -ne '1.2.23'" = "`$validation.registryVersion -ne '1.2.29'"
    "registryVersion = '1.2.23'" = "registryVersion = '1.2.29'"
    "'1.2.20'; checkpointSha256 = '9644D9FD80EB29A57D4B16ED5FCD1FE16475D8101B2C93DF1B21B7530C5F7509'" = "'1.2.28'; checkpointSha256 = '1797D375B750FE8A754BD9DDB9E55ED684E73B6D01333E010BD38F63630AE75B'"
    "version = '2.7.8'; coreVersion = '2.7.4'" = "version = '2.7.16'; coreVersion = '2.7.8'"
    "[string]`$ReleaseStamp = '20260831T112000Z'" = "[string]`$ReleaseStamp = '20260901T023500Z'"
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) {
        throw "Authority v1.2.29 template marker missing: $($replacement.Key)"
    }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$marker = '    Copy-Tree $sourceRelease $stagingRoot'
$injection = @'
    Copy-Tree $sourceRelease $stagingRoot
    $newRuntime = Join-Path $ProjectRoot 'dist\orchestrator\win-x64\v2.7.8'
    $stagedRuntime = Join-Path $stagingRoot 'orchestrator'
    $contentRelease = Join-Path $ProjectRoot 'content-worker\releases\content.deepseek-0.9.5-win-x64-formal-20260901T021446Z'
    $stagedContent = Join-Path $stagingRoot 'workers\content'
    foreach ($required in @($newRuntime, $contentRelease)) {
        if (-not (Test-Path -LiteralPath $required -PathType Container)) { throw "Missing authority v1.2.29 staged input: $required" }
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($newRuntime, '*', [IO.SearchOption]::AllDirectories)) {
        $destination = Join-Path $stagedRuntime ([IO.Path]::GetRelativePath($newRuntime, $file))
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($file, $destination, $true)
    }
    [IO.Directory]::Delete($stagedContent, $true)
    Copy-Tree $contentRelease $stagedContent
'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Authority v1.2.29 runtime/content replacement marker missing.' }
$template = $template.Replace($marker, $injection.TrimEnd(), [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1229.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.29 assembly failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

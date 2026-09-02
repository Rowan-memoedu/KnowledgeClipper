[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1223.ps1') -Raw
$replacements = [ordered]@{
    "'releases\knowledge-clipper-authority-v1.2.22-20260831T110500Z\win-x64'" = "'releases\knowledge-clipper-authority-v1.2.24-20260901T010500Z\win-x64'"
    "'contracts\authority\worker-registry\v1.2.22-20260831T110500Z'" = "'contracts\authority\worker-registry\v1.2.24-20260901T010500Z'"
    "'contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json'" = "'contracts/core/v2.7/checkpoints/v2.7.11-20260901T011300Z/checkpoint.json'"
    "'88713A6638BE74587F371287E83ED11056E13CC389F11E052DFCC5C719B5BE0C'" = "'37E9FB59453EE212AE43E989B9CC8A080E1A44C47E4BD85F5AF3E02C58287197'"
    '$releaseId = "knowledge-clipper-authority-v1.2.23-$ReleaseStamp"' = '$releaseId = "knowledge-clipper-authority-v1.2.25-$ReleaseStamp"'
    '$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.23-$ReleaseStamp"' = '$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.25-$ReleaseStamp"'
    '$stagingAuthority = Join-Path $authorityParent (''.v1.2.23-'' + $ReleaseStamp' = '$stagingAuthority = Join-Path $authorityParent (''.v1.2.25-'' + $ReleaseStamp'
    "if (`$registry.registryVersion -ne '1.2.22'" = "if (`$registry.registryVersion -ne '1.2.24'"
    "`$registry.registryVersion = '1.2.23'" = "`$registry.registryVersion = '1.2.25'"
    "`$validation.registryVersion -ne '1.2.23'" = "`$validation.registryVersion -ne '1.2.25'"
    "registryVersion = '1.2.23'" = "registryVersion = '1.2.25'"
    "'1.2.20'; checkpointSha256 = '9644D9FD80EB29A57D4B16ED5FCD1FE16475D8101B2C93DF1B21B7530C5F7509'" = "'1.2.24'; checkpointSha256 = 'B2103C2C6ECCA92EC38695BDCE4E10EDEB18A7F981C580677B6F4B357D5D4AAF'"
    "version = '2.7.8'; coreVersion = '2.7.4'" = "version = '2.7.11'; coreVersion = '2.7.7'"
    "[string]`$ReleaseStamp = '20260831T112000Z'" = "[string]`$ReleaseStamp = '20260901T011500Z'"
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.25 template marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$marker = '    Copy-Tree $sourceRelease $stagingRoot'
$injection = @'
    Copy-Tree $sourceRelease $stagingRoot
    $newRuntime = Join-Path $ProjectRoot 'dist\orchestrator\win-x64\v2.7.7'
    $stagedRuntime = Join-Path $stagingRoot 'orchestrator'
    if (-not (Test-Path -LiteralPath $newRuntime -PathType Container)) { throw "Missing Core v2.7.7 runtime: $newRuntime" }
    foreach ($file in [IO.Directory]::EnumerateFiles($newRuntime, '*', [IO.SearchOption]::AllDirectories)) {
        $destination = Join-Path $stagedRuntime ([IO.Path]::GetRelativePath($newRuntime, $file))
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($file, $destination, $true)
    }
'@
if (-not $template.Contains($marker, [StringComparison]::Ordinal)) { throw 'Authority runtime replacement marker missing.' }
$template = $template.Replace($marker, $injection.TrimEnd(), [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1225.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.25 assembly failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

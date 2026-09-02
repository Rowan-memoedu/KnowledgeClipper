[CmdletBinding()]
param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference='Stop'
$templatePath=Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1223.ps1'
$template=Get-Content -LiteralPath $templatePath -Raw
$replacements=[ordered]@{
  "'releases\knowledge-clipper-authority-v1.2.22-20260831T110500Z\win-x64'"="'releases\knowledge-clipper-authority-v1.2.23-20260831T112000Z\win-x64'"
  "'contracts\authority\worker-registry\v1.2.22-20260831T110500Z'"="'contracts\authority\worker-registry\v1.2.23-20260831T112000Z'"
  "'contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json'"="'contracts/core/v2.7/checkpoints/v2.7.10-20260901T010000Z/checkpoint.json'"
  "'88713A6638BE74587F371287E83ED11056E13CC389F11E052DFCC5C719B5BE0C'"="'EB46B286C3B569BA40016241BB032D694084D59E0A95AF4959FDC297F68EF052'"
  '$releaseId = "knowledge-clipper-authority-v1.2.23-$ReleaseStamp"'='$releaseId = "knowledge-clipper-authority-v1.2.24-$ReleaseStamp"'
  '$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.23-$ReleaseStamp"'='$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v1.2.24-$ReleaseStamp"'
  '$stagingAuthority = Join-Path $authorityParent (''.v1.2.23-'' + $ReleaseStamp'='$stagingAuthority = Join-Path $authorityParent (''.v1.2.24-'' + $ReleaseStamp'
  "if (`$registry.registryVersion -ne '1.2.22'"="if (`$registry.registryVersion -ne '1.2.23'"
  "`$registry.registryVersion = '1.2.23'"="`$registry.registryVersion = '1.2.24'"
  "`$validation.registryVersion -ne '1.2.23'"="`$validation.registryVersion -ne '1.2.24'"
  "registryVersion = '1.2.23'"="registryVersion = '1.2.24'"
  "'1.2.20'; checkpointSha256 = '9644D9FD80EB29A57D4B16ED5FCD1FE16475D8101B2C93DF1B21B7530C5F7509'"="'1.2.23'; checkpointSha256 = 'F696B3C898FAB11BDEE3D0AFE2A40D417B37CE7BAC280F5BB8E16C9427F588E9'"
  "version = '2.7.8'; coreVersion = '2.7.4'"="version = '2.7.10'; coreVersion = '2.7.6'"
  "[string]`$ReleaseStamp = '20260831T112000Z'"="[string]`$ReleaseStamp = '20260901T010500Z'"
}
foreach($pair in $replacements.GetEnumerator()){if(-not $template.Contains($pair.Key,[StringComparison]::Ordinal)){throw "Authority v1.2.24 template marker missing: $($pair.Key)"};$template=$template.Replace($pair.Key,$pair.Value,[StringComparison]::Ordinal)}
$marker='    Copy-Tree $sourceRelease $stagingRoot'
$injection=@'
    Copy-Tree $sourceRelease $stagingRoot
    $newRuntime = Join-Path $ProjectRoot 'dist\orchestrator\win-x64\v2.7.6'
    $stagedRuntime = Join-Path $stagingRoot 'orchestrator'
    if (-not (Test-Path -LiteralPath $newRuntime -PathType Container)) { throw "Missing Core v2.7.6 runtime: $newRuntime" }
    foreach ($file in [IO.Directory]::EnumerateFiles($newRuntime, '*', [IO.SearchOption]::AllDirectories)) {
        $destination = Join-Path $stagedRuntime ([IO.Path]::GetRelativePath($newRuntime, $file))
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($file, $destination, $true)
    }
'@
if(-not $template.Contains($marker,[StringComparison]::Ordinal)){throw 'Authority runtime replacement marker missing.'}
$template=$template.Replace($marker,$injection.TrimEnd(),[StringComparison]::Ordinal)
$temporary=Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1224.'+[Guid]::NewGuid().ToString('N')+'.tmp.ps1')
try{[IO.File]::WriteAllText($temporary,$template,[Text.UTF8Encoding]::new($false));& $temporary -ProjectRoot $ProjectRoot;if($LASTEXITCODE -ne 0){throw "Authority v1.2.24 assembly failed: $LASTEXITCODE"}}
finally{if(Test-Path -LiteralPath $temporary -PathType Leaf){[IO.File]::Delete($temporary)}}

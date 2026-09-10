#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ProjectRoot = 'D:\Projects\RemNote Quick Clipper',
    [string]$BaseCandidate = 'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-observation-20260908T062524Z-505ab282',
    [string]$BaseManifestSha256 = '67B513DF538EB77732E0CED67D572123D45D68489DF1D21DB3FA8EB8E56A6F38',
    [Parameter(Mandatory)][string]$RegressionReceipt,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CanonicalTreeHash.ps1')
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Require([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Json([string]$Path,[object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
function Inventory([string]$Root) { @(Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=Hash $_.FullName} }) }
$final=[IO.Path]::GetFullPath($OutputRoot)
$stage=$final+'.pending-'+[guid]::NewGuid().ToString('N')
Require (-not (Test-Path -LiteralPath $final)) 'Append-only bundle already exists.'
$baseManifest=Join-Path $BaseCandidate 'candidate-manifest.json'
Require ((Hash $baseManifest) -eq $BaseManifestSha256) 'Sealed base manifest drifted.'
$base=Get-Content -LiteralPath $baseManifest -Raw|ConvertFrom-Json -Depth 100
$index=0
foreach($entry in $base.files) {
    Require (-not [IO.Path]::IsPathRooted($entry.path) -and $entry.path -notmatch '(^|/)\.\.(/|$)') 'Unsafe inherited path.'
    $path=Join-Path $BaseCandidate $entry.path
    Require ((Get-Item -LiteralPath $path).Length -eq $entry.bytes -and (Hash $path) -eq $entry.sha256) "Base file drift: $($entry.path)"
    $index++;if($index%500 -eq 0){Write-Output "BASE_VERIFIED $index/$($base.files.Count)"}
}
$test=Get-Content -LiteralPath $RegressionReceipt -Raw|ConvertFrom-Json -Depth 100
Require ($test.status -eq 'PASS' -and $test.physicalSpeakerPlayback -eq $false -and $test.externalServiceCalls -eq 0) 'Offline regression is not PASS.'
New-Item -ItemType Directory -Path $stage,(Join-Path $stage 'sdk'),(Join-Path $stage 'evidence'),(Join-Path $stage 'source') | Out-Null
# Buildable source snapshot, not a second active workspace. Only code/project files.
$sourceEntries=@(Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'core-v3/src') -Recurse -File | Where-Object {$_.Extension -in '.cs','.csproj' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]'} | ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($ProjectRoot,$_.FullName)
    $destination=Join-Path $stage ('source/'+$relative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force|Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
    [ordered]@{path=$relative.Replace('\','/');bytes=$_.Length;sha256=Hash $_.FullName}
})
$dotnet='D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
& $dotnet publish (Join-Path $ProjectRoot 'core-v3/src/KnowledgeClipper.Core.V3.Host/KnowledgeClipper.Core.V3.Host.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o (Join-Path $stage 'runtime') --nologo
Require ($LASTEXITCODE -eq 0) 'Snapshot Core publish failed.'
& $dotnet build (Join-Path $ProjectRoot 'core-v3/src/KnowledgeClipper.Core.V3.Client/KnowledgeClipper.Core.V3.Client.csproj') -c Release --nologo
Require ($LASTEXITCODE -eq 0) 'Snapshot SDK build failed.'
foreach($name in @('KnowledgeClipper.Core.V3.Client.dll','KnowledgeClipper.Core.V3.Client.pdb','KnowledgeClipper.Core.V3.Contracts.dll')) {
    Copy-Item -LiteralPath (Join-Path $ProjectRoot ('core-v3/src/KnowledgeClipper.Core.V3.Client/bin/Release/net8.0/'+$name)) -Destination (Join-Path $stage ('sdk/'+$name))
}
Require ((Hash (Join-Path $stage 'sdk/KnowledgeClipper.Core.V3.Contracts.dll')) -eq (Hash (Join-Path $stage 'runtime/KnowledgeClipper.Core.V3.Contracts.dll'))) 'SDK/runtime Contracts drift.'
Copy-Item -LiteralPath $RegressionReceipt -Destination (Join-Path $stage 'evidence/offline-regression.json')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'core-v3/docs/PLAYBACK-CONTROL-SDK.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'contracts/core/v3/operations/clip.read-aloud-cards/playback-stop.v1.schema.json') -Destination (Join-Path $stage 'sdk/playback-stop.v1.schema.json')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'contracts/core/v3/operations/clip.read-aloud-cards/playback-control-projection.v1.schema.json') -Destination (Join-Path $stage 'sdk/playback-control-projection.v1.schema.json')
$validation=& python (Join-Path $ProjectRoot 'tools/validate_contracts.py')
Require ($LASTEXITCODE -eq 0) 'Public contracts failed.'
Json (Join-Path $stage 'evidence/public-contracts.json') ($validation|ConvertFrom-Json -Depth 100)
Json (Join-Path $stage 'sdk/sdk-binding.v1.json') ([ordered]@{schema='kc.playback-control-sdk-binding/1';clientSha256=Hash (Join-Path $stage 'sdk/KnowledgeClipper.Core.V3.Client.dll');contractsSha256=Hash (Join-Path $stage 'sdk/KnowledgeClipper.Core.V3.Contracts.dll');baseCandidateManifestSha256=$BaseManifestSha256;authorityUnchanged=$true})
# Source snapshot excludes caches. No historical runtime/authority is rebuilt.
$payload=Inventory $stage
Json (Join-Path $stage 'bundle-manifest.json') ([ordered]@{schema='kc.playback-control-sdk-runtime-bundle/1';packageVersion='1.0.0';createdAt=[DateTime]::UtcNow.ToString('o');baseCandidate=[ordered]@{root=$BaseCandidate;manifestSha256=$BaseManifestSha256;registryVersion=$base.authority.registryVersion;authorityUnchanged=$true};runtime='runtime/KnowledgeClipper.Core.V3.Host.exe';sdk='sdk/KnowledgeClipper.Core.V3.Client.dll';sourceBindings=$sourceEntries;files=$payload;payload=[ordered]@{files=$payload.Count;bytes=Get-KcCanonicalTreeTotalBytes $payload;treeSha256=Get-KcCanonicalTreeHash $payload};boundary='offline module; no Desktop Actual or speaker'})
[IO.Directory]::Move($stage,$final)
foreach($entry in $payload){$path=Join-Path $final $entry.path;Require ((Get-Item -LiteralPath $path).Length -eq $entry.bytes -and (Hash $path) -eq $entry.sha256) "Final bundle drift: $($entry.path)"}
foreach($entry in $sourceEntries){Require ((Hash (Join-Path $ProjectRoot $entry.path)) -eq $entry.sha256) "Source drift during build: $($entry.path)"}
Require ((Hash $baseManifest) -eq $BaseManifestSha256) 'Old manifest changed.'
Write-Output "BUNDLE $final"
Write-Output "MANIFEST_SHA256 $(Hash (Join-Path $final 'bundle-manifest.json'))"
Write-Output "SDK_SHA256 $(Hash (Join-Path $final 'sdk/KnowledgeClipper.Core.V3.Client.dll'))"
Write-Output "KERNEL_SHA256 $(Hash (Join-Path $final 'runtime/KnowledgeClipper.Core.V3.Kernel.dll'))"

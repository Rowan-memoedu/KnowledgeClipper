#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputRoot=('D:\Artifacts\KnowledgeClipper\read-to-anki-runner\playback-regression-'+[datetime]::UtcNow.ToString('yyyyMMddTHHmmssZ')))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($OutputRoot)
if(-not $root.StartsWith('D:\Artifacts\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $root)){throw 'playback-release-root-must-be-new-D-artifact'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$dotnet='D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$sdk='D:\Artifacts\KnowledgeClipper\playback-control\sdk-runtime-20260908-01\sdk'
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function WriteJson([string]$path,[object]$value){$value|ConvertTo-Json -Depth 60|Set-Content -LiteralPath $path -Encoding utf8NoBOM}
$product='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-final-resize-speech-20260908T172000Z'
$productManifest=Join-Path $product 'candidate-file-manifest.v1.json'
$productSha='7087B3084EB7B66D6CB0208B01E57BC16EA2406C0A73F3F9361764A57FD4832C'
$previousManifest='D:\Artifacts\KnowledgeClipper\read-to-anki-runner\playback-regression-20260908T164049Z\runner-playback-release-manifest.v1.json'
$previousSha='D63E3015CEDC9FC6B4A502BFE3C5F2578EF13DEE3BFE2EDEFCB0447DADC54D55'
$menuProbe='D:\Artifacts\KnowledgeClipper\playback-control\menu-exposure-20260909-01\observation-final\wm-uia.json'
if((Hash $productManifest)-ne$productSha-or(Hash $previousManifest)-ne$previousSha){throw 'sealed-input-drift'}
$probeSha=Hash $menuProbe
$statusProbe='D:\Artifacts\KnowledgeClipper\playback-control\menu-exposure-20260909-01\observation-status\status-channels.json'
$statusProbeSha='479962000FE029E6AB7239F3B409D5F9D2B1120EA28DC7F9F5DFB00012D2BA00'
if((Hash $statusProbe)-ne$statusProbeSha){throw 'status-channel-evidence-drift'}
$sources=@(Get-ChildItem -LiteralPath $PSScriptRoot -File|Where-Object Extension -In '.cs','.csproj','.ps1','.md','.json'|ForEach-Object{[ordered]@{path=$_.FullName;sha256=Hash $_.FullName}})
try {
    Write-Output 'PHASE publish single-task stop/replay runner; no Actual'
    & $dotnet publish (Join-Path $PSScriptRoot 'KnowledgeClipper.ActualDesktopFlowRunner.csproj') -c Release -r win-x64 --self-contained true --no-restore --nologo -o (Join-Path $root 'app') /p:PublishSingleFile=false /p:EnablePlaybackRegression=true "/p:CoreV3SdkRoot=$sdk"
    if($LASTEXITCODE -ne 0){throw 'playback-runner-publish-failed'}
    $exe=Join-Path $root 'app/KnowledgeClipper.ActualDesktopFlowRunner.exe'
    $evidence=Join-Path $root 'evidence';[IO.Directory]::CreateDirectory($evidence)|Out-Null
    & $exe --playback-regression-plan 1> (Join-Path $evidence 'plan.json') 2> (Join-Path $evidence 'plan.stderr.log')
    if($LASTEXITCODE -ne 0){throw 'playback-plan-failed'}
    Copy-Item -LiteralPath $menuProbe -Destination (Join-Path $evidence 'recorded-menu-observation.json')
    Copy-Item -LiteralPath $statusProbe -Destination (Join-Path $evidence 'recorded-status-observation.json')
    & $exe --playback-regression-self-test --menu-observation (Join-Path $evidence 'recorded-menu-observation.json') --status-observation (Join-Path $evidence 'recorded-status-observation.json') 1> (Join-Path $evidence 'self-test.json') 2> (Join-Path $evidence 'self-test.stderr.log')
    if($LASTEXITCODE -ne 0){throw 'playback-offline-self-test-failed'}
    $test=Get-Content -LiteralPath (Join-Path $evidence 'self-test.json') -Raw|ConvertFrom-Json
    if($test.status -ne 'PASS' -or $test.playbackCases -ne 20 -or $test.menuSelectorCases -ne 17 -or $test.statusReaderCases -ne 22 -or $test.recordedProbeCases -ne 3 -or $test.previewBarrierCases -ne 9 -or $test.externalCalls -ne 0){throw 'playback-self-test-gate-failed'}
    if((Hash $productManifest)-ne$productSha-or(Hash $previousManifest)-ne$previousSha-or(Hash $menuProbe)-ne$probeSha-or(Hash $statusProbe)-ne$statusProbeSha){throw 'sealed-input-drift-after-build'}
    foreach($source in $sources){if((Hash $source.path)-ne$source.sha256){throw 'playback-source-drift-during-publish'}}
    $client=Join-Path $root 'app/KnowledgeClipper.Core.V3.Client.dll';$contracts=Join-Path $root 'app/KnowledgeClipper.Core.V3.Contracts.dll'
    if((Hash $client)-ne'1C25A618536D30A0F5CE31C5A0FE1050C821C7E2790B5F70AC04AEAC226C842F' -or (Hash $contracts)-ne'EAFEED544C951E625D09CE92B8578200E3DA24F1F89F55FDE2B547E52CBD12D6'){throw 'playback-sdk-drift'}
    $runnerSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PlaybackRegression.cs') -Raw
    if($runnerSource -match '\.(StopReadPlaybackAsync|ReplayReadAloudCardsAsync|ConfirmFlashcardDraftAsync|ReadAloudCardsAsync|PublishTextArtifactAsync)\s*\('){throw 'playback-sdk-command-substitution-forbidden'}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PLAYBACK-REGRESSION.md') -Destination (Join-Path $root 'PLAYBACK-REGRESSION.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'playback-capture-fixture.v1.json') -Destination (Join-Path $root 'capture-fixture.json')
    $files=@(Get-ChildItem -LiteralPath $root -Recurse -File|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=Hash $_.FullName}}|Sort-Object path)
    [long]$bytes=0;foreach($file in $files){$bytes+=$file.bytes}
    WriteJson (Join-Path $root 'runner-playback-release-manifest.v1.json') ([ordered]@{
        schemaVersion='knowledge-clipper-playback-runner-release.v1';status='offline-prepared-awaiting-control-review';createdAt=[datetimeoffset]::UtcNow;runtime='win-x64-self-contained';entrypoint='app/KnowledgeClipper.ActualDesktopFlowRunner.exe'
        productBinding=[ordered]@{path=$product;manifestPath=$productManifest;manifestSha256=$productSha;productCopied=$false;productModified=$false}
        previousRunner=[ordered]@{manifestPath=$previousManifest;manifestSha256=$previousSha;preserved=$true}
        menuExposureEvidence=[ordered]@{path=$menuProbe;sha256=$probeSha;selector='exact Name + owned PID + visible Menu root + visible MenuItem + unique identity + enabled InvokePattern'}
        statusChannelEvidence=[ordered]@{path=$statusProbe;sha256=$statusProbeSha;channel='UIAutomationCore UiaNodeFromHandle + UiaGetPropertyValue(30090,30094)';helpTextIsStatusSource=$false;sdkSubstitution=$false}
        runnerDllSha256=Hash (Join-Path $root 'app/KnowledgeClipper.ActualDesktopFlowRunner.dll');sdkClientSha256=Hash $client;sdkContractsSha256=Hash $contracts
        qualification=[ordered]@{playbackCases=20;menuSelectorCases=17;statusReaderCases=22;recordedProbeCases=3;previewBarrierCases=9;statusChannelEvidenceBound=$true;actualDispatch='pending-control-review';liveExecuted=$false;uiEventExecutionValidated=$false;fullMatrixReexecuted=$false;externalCalls=0;sdkCommandInvocations=0}
        sources=$sources;payload=[ordered]@{files=$files.Count;bytes=$bytes};files=$files
    })
    foreach($file in $files){$path=Join-Path $root $file.path;if((Get-Item $path).Length -ne $file.bytes -or (Hash $path)-ne$file.sha256){throw 'playback-final-payload-drift'}}
    Write-Output ('STATUS OFFLINE_PREPARED '+$root)
    Write-Output ('MANIFEST SHA256 '+(Hash (Join-Path $root 'runner-playback-release-manifest.v1.json')))
} catch {Write-Warning ('Unpromoted failure evidence retained: '+$root);throw}

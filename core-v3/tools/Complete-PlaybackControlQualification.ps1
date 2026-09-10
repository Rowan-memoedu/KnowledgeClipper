#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ProjectRoot='D:\Projects\RemNote Quick Clipper',
    [string]$Bundle='D:\Artifacts\KnowledgeClipper\playback-control\sdk-runtime-20260908-01',
    [string]$EvidenceRoot='D:\Artifacts\KnowledgeClipper\playback-control\qualification-20260908-01'
)
$ErrorActionPreference='Stop'
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Bind([string]$Path){[ordered]@{path=$Path;bytes=(Get-Item -LiteralPath $Path).Length;sha256=Hash $Path}}
$out=Join-Path $EvidenceRoot 'qualification-receipt.json'
if(Test-Path -LiteralPath $out){throw 'Append-only qualification exists.'}
$manifestPath=Join-Path $Bundle 'bundle-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -Depth 100
foreach($entry in $manifest.files){
    $path=Join-Path $Bundle $entry.path
    if((Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Hash $path) -ne $entry.sha256){throw "Bundle drift: $($entry.path)"}
}
if(@(Get-ChildItem -LiteralPath $Bundle -Recurse -File).Count -ne $manifest.files.Count+1){throw 'Bundle has undeclared files.'}
$startupPath=Join-Path $EvidenceRoot 'startup-receipt.json'
$startup=Get-Content -LiteralPath $startupPath -Raw|ConvertFrom-Json
if($startup.status -ne 'PASS' -or $startup.workerRunInvocations -ne 0){throw 'Startup gate failed.'}
$testPath='D:\Artifacts\KnowledgeClipper\playback-control\offline-20260908-02\receipt.json'
$test=Get-Content -LiteralPath $testPath -Raw|ConvertFrom-Json
if($test.status -ne 'PASS'){throw 'Regression gate failed.'}
$testKernel=Join-Path $ProjectRoot 'core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/bin/Release/net8.0/KnowledgeClipper.Core.V3.Kernel.dll'
$testClient=Join-Path $ProjectRoot 'core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/bin/Release/net8.0/KnowledgeClipper.Core.V3.Client.dll'
if((Hash $testKernel) -ne (Hash (Join-Path $Bundle 'runtime/KnowledgeClipper.Core.V3.Kernel.dll')) -or (Hash $testClient) -ne (Hash (Join-Path $Bundle 'sdk/KnowledgeClipper.Core.V3.Client.dll'))){throw 'Test/runtime/SDK identity drift.'}
$sourceBindings=@('core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/PlaybackControlTests.cs','core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/Program.cs','core-v3/tools/New-PlaybackControlBundle.ps1','core-v3/tools/Complete-PlaybackControlQualification.ps1','core-v3/docs/PLAYBACK-CONTROL-SDK.md','tools/validate_contracts.py'|ForEach-Object{Bind (Join-Path $ProjectRoot $_)})
$receipt=[ordered]@{
    schema='kc.playback-control-delivery-qualification/1';status='PASS-MODULE';createdAt=[DateTime]::UtcNow.ToString('o')
    bundle=Bind $manifestPath;payload=$manifest.payload;baseCandidate=$manifest.baseCandidate
    runtimeKernel=Bind (Join-Path $Bundle 'runtime/KnowledgeClipper.Core.V3.Kernel.dll');sdkClient=Bind (Join-Path $Bundle 'sdk/KnowledgeClipper.Core.V3.Client.dll');sdkContracts=Bind (Join-Path $Bundle 'sdk/KnowledgeClipper.Core.V3.Contracts.dll')
    regression=Bind $testPath;regressionCaseCount=$test.caseCount;startup=Bind $startupPath
    publicContracts=Bind (Join-Path $Bundle 'evidence/public-contracts.json');sourceBindings=$sourceBindings
    gates=@('build-zero-warnings-errors','test-runtime-sdk-sha-match','typed-NamedPipe-stop','three-fake-worker-child-processes','delayed-old-target-rejected','accepted-not-stopped','content-pending-continues','replay-only-playback-two-cards','draft-confirm-export-unaffected','old-terminal-rejected','recovery-no-resignal','new-host-startup-query','sealed-inventory-hash-match')
    baselineFailure=Bind 'D:\Artifacts\KnowledgeClipper\playback-control\offline-20260908-01\broad-phase2-environment-failure.json'
    baselineFailureReason='Old full Phase2 runner references absent pre-migration releases/v1.2.41; preserved, not repaired or claimed PASS.'
    boundaries=[ordered]@{externalServiceCalls=0;physicalSpeakerPlayback=$false;desktopActualRerun=$false;oldAuthorityMutation=$false;oldCandidateMutation=$false;desktopIntegration='next-step';actualStopReplay='not-run';replayDuringBusinessInFlight='wait-for-current-business-result'}
}
$receipt|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $out -Encoding utf8NoBOM
Write-Output "RECEIPT $out"
Write-Output "SHA256 $(Hash $out)"

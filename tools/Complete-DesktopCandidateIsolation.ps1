#Requires -Version 7.0
param([Parameter(Mandatory)][string]$CandidateRoot,[Parameter(Mandatory)][string]$EvidenceRoot)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$a=Get-Content (Join-Path $EvidenceRoot 'assembly.json') -Raw|ConvertFrom-Json -Depth 100
[void](Assert-OverlayManifest $CandidateRoot $a.manifest.path $a.manifest.sha256)
$b=$a.bindings.inheritedBase
[void](Assert-OverlayManifest $b.root (Join-Path $b.root 'candidate-file-manifest.v1.json') $b.manifestSha256)
$isolation=Get-Content (Join-Path $EvidenceRoot 'pipe-isolation.json') -Raw|ConvertFrom-Json
$gates=Get-Content (Join-Path $EvidenceRoot 'package-gates.json') -Raw|ConvertFrom-Json
if($isolation.status -cne 'PASS-CANDIDATE-PIPE-ISOLATION' -or $isolation.oldPipeConnections -ne 0 -or !$isolation.newHostExited -or !$isolation.actualNamedPipeSdkQuery -or $isolation.catalogItems -ne 10){throw 'isolation-not-passed'}
if($gates.status -cne 'PASS-OFFLINE-FINAL-ROOT' -or $gates.manifestSha256 -cne $a.manifest.sha256){throw 'package-gates-not-passed'}
$inheritedReceipt='D:\Artifacts\KnowledgeClipper\remnote-destinations\desktop-integrated-20260909-01\receipt.json'
if((Get-OverlaySha $inheritedReceipt) -cne '423DE413799CD31704AB76A1EA04BD0D900996837EF7BCD8FB9AFDEF6164EE0D'){throw 'inherited-receipt-drift'}
$evidence=@();foreach($name in @('assembly.json','pipe-isolation.json','pipe-isolation.log','package-gates.json','package-gates.log','startup-diagnostic.log','single-instance-diagnostic.log','integration-startup.log')){$evidence+=Get-OverlayIdentity (Join-Path $EvidenceRoot $name)}
$receipt=[ordered]@{
    schemaVersion='knowledge-clipper-desktop-integrated-qualification.v1';status='PASS-OFFLINE-INTEGRATED-PIPE-ISOLATED-AWAITING-QUALITY'
    candidate=$CandidateRoot;entrypoint=(Join-Path $CandidateRoot 'KnowledgeClipper.exe');manifest=$a.manifest;payload=$a.payload;authority=$a.authority
    desktopDll=(Get-OverlayIdentity (Join-Path $CandidateRoot 'KnowledgeClipper.dll'));desktopExe=(Get-OverlayIdentity (Join-Path $CandidateRoot 'KnowledgeClipper.exe'))
    bindings=$a.bindings;changedDeploymentFiles=$isolation.changedDeploymentFiles;unchangedFiles=$isolation.unchangedPayloadFiles
    isolation=$isolation;evidence=$evidence;inheritedUiAndModuleGates=(Get-OverlayIdentity $inheritedReceipt)
    tools=@((Get-OverlayIdentity $PSCommandPath),(Get-OverlayIdentity (Join-Path $PSScriptRoot 'Test-DesktopCandidatePipeIsolation.ps1')),(Get-OverlayIdentity (Join-Path $PSScriptRoot 'Test-DesktopDestinationsPackage.py')))
    boundaries=[ordered]@{userHandTestEntrypoint=$false;qualityPending=$true;oldPipeOccupant='exclusive Fake listener, not a legacy production Host';legacyHostWasNotRun=$true;oldCandidateUnchanged=$true;allProductionBinariesUnchanged=$true;businessE2E='not-run';uiMatrixRerun=$false;moduleMatrixRerun=$false;productionSourceChanged=$false;externalCalls=0;workerRun=0;publicExternalActions=0;speaker=0;userProcessesTerminated=0;serverIdentityFrameworkAdded=$false;arbitraryRelocationSupported=$false}
}
Write-OverlayJson (Join-Path $EvidenceRoot 'receipt.json') $receipt
Get-OverlayIdentity (Join-Path $EvidenceRoot 'receipt.json')|ConvertTo-Json

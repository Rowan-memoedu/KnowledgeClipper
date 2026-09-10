#Requires -Version 7.0
param([Parameter(Mandatory)][string]$CandidateRoot,[Parameter(Mandatory)][string]$EvidenceRoot)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$assembly=Get-Content (Join-Path $EvidenceRoot 'assembly.json') -Raw|ConvertFrom-Json -Depth 100
$manifest=Assert-OverlayManifest $CandidateRoot $assembly.manifest.path $assembly.manifest.sha256
$startup=Get-Content (Join-Path $EvidenceRoot 'startup-diagnostic.log') -Raw|ConvertFrom-Json
$ipc=Get-Content (Join-Path $EvidenceRoot 'integration-startup.log') -Raw|ConvertFrom-Json
$gates=Get-Content (Join-Path $EvidenceRoot 'package-gates.json') -Raw|ConvertFrom-Json
$ui=Get-Content (Join-Path $EvidenceRoot 'ui-smoke-02/receipt.json') -Raw|ConvertFrom-Json
if($startup.result -cne 'PASS' -or !$startup.packageFilesVerified -or !$startup.packageTreeVerified -or !$startup.candidateRuntimeToolsVerified){throw 'startup gate'}
if($ipc.result -cne 'PASS' -or !$ipc.namedPipeInvoked -or !$ipc.orchestratorStartedByDesktop -or $ipc.action -cne 'capabilities.get' -or $ipc.queryItemCount -lt 1 -or $ipc.externalWorkerInvoked -or $ipc.physicalSpeakerInvoked){throw 'ipc gate'}
if(Get-Process -Id $ipc.orchestratorProcessId -ErrorAction SilentlyContinue){throw 'own host remains'}
if($gates.status -cne 'PASS-OFFLINE-FINAL-ROOT' -or $gates.manifestSha256 -cne $assembly.manifest.sha256 -or $gates.nonemptySecretFields -ne 0){throw 'offline gate'}
if($ui.status -cne 'PASS-INJECTED-SEALED-DESKTOP-SMOKE' -or $ui.assertions -ne 9 -or $ui.desktopDllSha256 -cne (Get-OverlaySha (Join-Path $CandidateRoot 'KnowledgeClipper.dll'))){throw 'ui gate'}
$evidence=@()
foreach($relative in @('assembly.json','package-gates.json','package-gates.log','startup-diagnostic.log','single-instance-diagnostic.log','integration-startup.log','ui-smoke-01.log','ui-smoke-02.log','ui-smoke-02/receipt.json','ui-smoke-02/integrated-smoke.png','smoke-build-final.log')){
    $evidence+=Get-OverlayIdentity (Join-Path $EvidenceRoot $relative)
}
$receipt=[ordered]@{
    schemaVersion='knowledge-clipper-desktop-integrated-qualification.v1';status='PASS-OFFLINE-INTEGRATED-AWAITING-QUALITY'
    candidate=$CandidateRoot;entrypoint=(Join-Path $CandidateRoot 'KnowledgeClipper.exe');manifest=$assembly.manifest;payload=$assembly.payload
    desktopDll=(Get-OverlayIdentity (Join-Path $CandidateRoot 'KnowledgeClipper.dll'));desktopExe=(Get-OverlayIdentity (Join-Path $CandidateRoot 'KnowledgeClipper.exe'))
    authority=$assembly.authority;bindings=$assembly.bindings;unchangedBaseFiles=$assembly.unchangedFiles;changedBaseFiles=@($assembly.changed).Count;addedFiles=@($assembly.added).Count
    gates=[ordered]@{startup='PASS';singleInstanceDiagnostic='PASS-no-instance-present';coreStartedByDesktop=$true;namedPipeCatalogItems=$ipc.queryItemCount;ownedCoreExited=$true;recursiveSchemaRefs=$gates.schemaReferences;packageConfigSecretFields=0;credentialStores=0;injectedSealedDesktopUiAssertions=$ui.assertions}
    evidence=$evidence
    tools=@((Get-OverlayIdentity $PSCommandPath),(Get-OverlayIdentity (Join-Path $PSScriptRoot 'Test-DesktopDestinationsPackage.py')),(Get-OverlayIdentity (Join-Path $PSScriptRoot 'desktop-destinations-smoke/Program.cs')),(Get-OverlayIdentity 'D:\Artifacts\KnowledgeClipper\remnote-destinations\desktop-integrated-smoke-runner-20260909-02\KnowledgeClipper.DesktopDestinationsSmoke.dll'))
    preservedFailures=@(
        'New smoke runner initial build CS0103 File/Path/Directory: missing System.IO import; runner-only corrected; final build 0 warnings/errors.',
        'ui-smoke-01: external reflection runner AppDomain base pointed at runner not candidate, settings-control.contract-path-missing; corrected runner application base to sealed candidate. No production or candidate mutation.'
    )
    boundaries=[ordered]@{userHandTestEntrypoint=$false;qualityPending=$true;businessWorkerRuns=0;publicExternalActions=0;liveServices=0;externalWrites=0;physicalSpeaker=0;physicalKeyboardMouse=0;userProcessesTerminated=0;productionSourceChanges=0;historicalRegistryCheckpointPromoted=$false;arbitraryRelocationSupported=$false;inheritedTestRunners='historical-only-not-executed';priorBusinessMatrices='inherited module receipts not rerun'}
}
Write-OverlayJson (Join-Path $EvidenceRoot 'receipt.json') $receipt
Get-OverlayIdentity (Join-Path $EvidenceRoot 'receipt.json')|ConvertTo-Json

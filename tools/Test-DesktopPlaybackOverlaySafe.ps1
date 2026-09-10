#Requires -Version 7.0
param([Parameter(Mandatory)][string]$CandidateRoot,[Parameter(Mandatory)][string]$ManifestSha,[Parameter(Mandatory)][string]$EvidenceRoot)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$candidate=[IO.Path]::GetFullPath($CandidateRoot);$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
if([IO.Directory]::Exists($evidence)){throw 'append-only-evidence-exists'}
[IO.Directory]::CreateDirectory($evidence)|Out-Null
$base='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-playback-20260908T155328Z'
$baseSha='64F9AC64910BAD44CDD610C7717D185E80E5C60B64EF88F8401A00F6BDC84AE2'
$baseManifest=Assert-OverlayManifest $base (Join-Path $base 'candidate-file-manifest.v1.json') $baseSha
$manifestPath=Join-Path $candidate 'candidate-file-manifest.v1.json'
$m=Assert-OverlayManifest $candidate $manifestPath $ManifestSha
$admissionPath='D:\Artifacts\KnowledgeClipper\playback-control\speech126-overlay-20260909-01\module-overlay-admission.json'
$admissionSha='F2D1847C5156CD3BE5FC2C4F2EDFB4CDC883102B22F3289C6F4118A5C07D5565'
if((Get-OverlaySha $admissionPath) -cne $admissionSha){throw 'admission-drift'}
$a=Get-Content -LiteralPath $admissionPath -Raw|ConvertFrom-Json -Depth 100
$allowed=@{}
foreach($r in $a.moduleCopies){$allowed[$r.destination]=$r;Test-OverlayFile (Resolve-OverlayFile $candidate $r.destination) $r}
foreach($r in $a.metadataCopies){$allowed[$r.path]=$r;Test-OverlayFile (Resolve-OverlayFile $candidate $r.path) $r}
$resizeRoot='D:\Artifacts\KnowledgeClipper\desktop-scale\20260909-native-edge'
foreach($pair in @(
 @('KnowledgeClipper.dll',(Join-Path $resizeRoot 'ui-module/KnowledgeClipper.dll'),'577FDBA22723E4397F33E553B57519EDF88C795DB1D4DEC7367ED1C0D5520BB4'),
 @('module-overlays/resize/module-receipt.json',(Join-Path $resizeRoot 'module-receipt-final.json'),'A12234BB2FB38DBBB3926F2E8909A257360A250537B179D3E3142E57EF6FCD11'),
 @('module-overlays/speech-1.2.6/admission.json',$admissionPath,$admissionSha),
 @('module-overlays/speech-1.2.6/release-tree.json','D:\Artifacts\KnowledgeClipper\speech\r20260908T170550Z\release-tree.json','EF7DAFB0A5F4386BC869700A268194F2A7CD1B34D5478A612E54E267878DEF0D')
)){
 $r=Get-OverlayIdentity $pair[1];if($r.sha256 -cne $pair[2]){throw 'source-pin-drift'}
 Test-OverlayFile (Resolve-OverlayFile $candidate $pair[0]) $r;$allowed[$pair[0]]=$r
}
$original=Get-Content -LiteralPath (Join-Path $base 'candidate-integration.json') -Raw|ConvertFrom-Json -AsHashtable
$current=Get-Content -LiteralPath (Join-Path $candidate 'candidate-integration.json') -Raw|ConvertFrom-Json -AsHashtable
if($current.Count -ne $original.Count){throw 'integration-fields-drift'}
foreach($key in $original.Keys){if($key -notin @('AuthorityCheckpointId','AuthorityTreeSha256') -and ($current[$key]|ConvertTo-Json -Depth 100 -Compress) -cne ($original[$key]|ConvertTo-Json -Depth 100 -Compress)){throw ('integration-drift: '+$key)}}
$authority=Get-OverlayTree @(Get-OverlayInventory (Join-Path $candidate 'authority/win-x64'))
if($current.AuthorityCheckpointId -cne ('module-overlay:'+ $a.overlayId) -or $current.AuthorityTreeSha256 -cne $authority.treeSha256 -or $m.bindings.moduleOverlay.authorityTreeSha256 -cne $authority.treeSha256){throw 'authority-overlay-identity-mismatch'}
if($m.bindings.inheritedBase.role -cne 'inherited-base-not-current-authority-tree' -or $m.bindings.inheritedBase.authorityCheckpointId -cne $original.AuthorityCheckpointId -or $m.bindings.inheritedRunner.role -cne 'historical-evidence-only-do-not-execute'){throw 'inherited-authority-misrepresented'}
$allowed['candidate-integration.json']=Get-OverlayIdentity (Join-Path $candidate 'candidate-integration.json')
$old=@{};foreach($r in $baseManifest.files){$old[$r.path]=$r}
$new=@{};$changed=@();$added=@();$unchanged=0
foreach($r in $m.files){
 $new[$r.path]=$r
 if($old.ContainsKey($r.path) -and $old[$r.path].bytes -eq $r.bytes -and $old[$r.path].sha256 -ceq $r.sha256){$unchanged++;continue}
 if(!$allowed.ContainsKey($r.path)){throw ('unapproved-file-drift: '+$r.path)}
 Test-OverlayFile (Resolve-OverlayFile $candidate $r.path) $allowed[$r.path]
 if($old.ContainsKey($r.path)){$changed+=$r.path}else{$added+=$r.path}
}
foreach($r in $baseManifest.files){if(!$new.ContainsKey($r.path)){throw ('removed-file: '+$r.path)}}
if($added.Count -ne 3 -or $changed.Count -ne 9){throw 'unexpected-delta-cardinality'}
& 'D:\Projects\RemNote Quick Clipper\core-v3\tools\Test-Speech126ModuleOverlay.ps1' -AdmissionPath $admissionPath -CandidateRoot $candidate | Tee-Object -FilePath (Join-Path $evidence 'core-overlay-verification.json')
if(!$?){throw 'core-overlay-verification-failed'}
$speechExe=Join-Path $candidate 'authority/win-x64/workers/speech/KnowledgeClipper.Speech.Worker.exe'
& $speechExe describe --json 2>&1|Tee-Object -FilePath (Join-Path $evidence 'speech-describe.json')
if($LASTEXITCODE -ne 0){throw 'speech-describe-failed'}
$description=Get-Content -LiteralPath (Join-Path $evidence 'speech-describe.json') -Raw|ConvertFrom-Json
if($description.workerId -cne 'speech.edge' -or $description.workerVersion -cne '1.2.6'){throw 'speech-describe-identity-failed'}
$data=Join-Path 'D:\AppData\KnowledgeClipper' ('safe-overlay-'+$m.candidateId)
if([IO.Directory]::Exists($data)){throw 'safe-data-not-fresh'}
$priorLocal=$env:LOCALAPPDATA;$env:LOCALAPPDATA=$data
try{
 $exe=Join-Path $candidate 'KnowledgeClipper.exe'
 foreach($mode in @('startup-diagnostic','single-instance-diagnostic')){
  & $exe ('--'+$mode) 2>&1|Tee-Object -FilePath (Join-Path $evidence ($mode+'.log'))
  if($LASTEXITCODE -ne 0){throw ('diagnostic-failed: '+$mode)}
 }
 & $exe --integration-startup-canary (Join-Path $data 'core-v3') 2>&1|Tee-Object -FilePath (Join-Path $evidence 'integration-startup.log')
 if($LASTEXITCODE -ne 0){throw 'integration-startup-failed'}
}finally{$env:LOCALAPPDATA=$priorLocal}
$ipc=Get-Content -LiteralPath (Join-Path $evidence 'integration-startup.log') -Raw|ConvertFrom-Json
if($ipc.result -cne 'PASS' -or !$ipc.namedPipeInvoked -or !$ipc.orchestratorStartedByDesktop -or $ipc.action -cne 'capabilities.get' -or $ipc.externalWorkerInvoked -or $ipc.physicalSpeakerInvoked -or $ipc.ankiInvoked){throw 'unsafe-or-failed-ipc-gate'}
if(Get-Process -Id $ipc.orchestratorProcessId -ErrorAction SilentlyContinue){throw 'owned-core-remains'}
[void](Assert-OverlayManifest $candidate $manifestPath $ManifestSha)
Write-OverlayJson (Join-Path $evidence 'safe-gates.json') ([ordered]@{
 status='PASS-SAFE-RESIZE-SPEECH-OVERLAY-NOT-ACTUAL';candidate=$candidate;manifest=(Get-OverlayIdentity $manifestPath);payload=$m.payload
 desktopDll=(Get-OverlayIdentity (Join-Path $candidate 'KnowledgeClipper.dll'));authority=$authority
 unchangedFiles=$unchanged;changedFiles=$changed;addedFiles=$added;removedFiles=@();coreRuntimeEqualityFiles=191;sdkEqualityFiles=6
 historicalCheckpointInheritedOnly=$true;oldEmbeddedRunnerUnchangedNeverExecuted=$true;externalRunnerRequired=$true
 pipe=$current.PipeName;integration=(Get-OverlayIdentity (Join-Path $candidate 'candidate-integration.json'))
 startup=(Get-OverlayIdentity (Join-Path $evidence 'startup-diagnostic.log'));singleInstance=(Get-OverlayIdentity (Join-Path $evidence 'single-instance-diagnostic.log'));readonlyIpc=(Get-OverlayIdentity (Join-Path $evidence 'integration-startup.log'))
 coreOverlay=(Get-OverlayIdentity (Join-Path $evidence 'core-overlay-verification.json'));speechDescribe=(Get-OverlayIdentity (Join-Path $evidence 'speech-describe.json'))
 ownedCoreExited=$true;dataRoot=$data;businessWorkerRuns=0;externalWrites=0;speaker=0;actualRun=0;userHandTestEntrypoint=$false;source=(Get-OverlayIdentity $PSCommandPath)
})
Get-OverlayIdentity (Join-Path $evidence 'safe-gates.json')|ConvertTo-Json

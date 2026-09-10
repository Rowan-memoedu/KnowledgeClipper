#Requires -Version 7.0
param([Parameter(Mandatory)][string]$CandidateRoot,[Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
function Sha($p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Row($p){$f=Get-Item -LiteralPath $p;[ordered]@{path=$f.FullName;bytes=$f.Length;sha256=(Sha $p)}}
function Verify($p,$r){if((Sha $p)-ne $r.sha256-or(Get-Item -LiteralPath $p).Length-ne $r.bytes){throw ('file identity drift: '+$p)}}
function Save($p,$v){$b=[Text.UTF8Encoding]::new($false).GetBytes(($v|ConvertTo-Json -Depth 20)+"`n");$s=[IO.File]::Open($p,[IO.FileMode]::CreateNew);try{$s.Write($b,0,$b.Length)}finally{$s.Dispose()}}
$candidate=[IO.Path]::GetFullPath($CandidateRoot);$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
if(Test-Path -LiteralPath $evidence){throw 'immutable-evidence-root-exists'}
New-Item -ItemType Directory -Path $evidence|Out-Null
$manifestPath=Join-Path $candidate 'candidate-file-manifest.v1.json';$m=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -Depth 100
if($m.regressionSlice-ne 'Playback'){throw 'wrong candidate slice'}
$index=@{};$tree=[Text.StringBuilder]::new();[long]$bytes=0;$i=0
foreach($r in $m.files){
 if([IO.Path]::IsPathRooted($r.path)-or $r.path.Split('/')-contains '..'-or $index.ContainsKey($r.path)){throw 'invalid manifest path'}
 Verify (Join-Path $candidate $r.path) $r;$index[$r.path]=$r;$bytes+=$r.bytes
 [void]$tree.Append($r.path).Append([char]0).Append($r.bytes).Append([char]0).Append($r.sha256).Append("`n")
 $i++;if($i%500-eq 0){Write-Output ('verified '+$i+' candidate files')}
}
$treeHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($tree.ToString())))
if($i-ne $m.payload.files-or $bytes-ne $m.payload.bytes-or $treeHash-ne $m.payload.treeSha256){throw 'candidate manifest tree mismatch'}
if(@(Get-ChildItem -LiteralPath $candidate -Recurse -File).Count-ne ($i+1)){throw 'candidate has unlisted files'}
$bundlePath=Join-Path $candidate $m.bindings.sdkFreezeManifest.path
if((Sha $bundlePath)-ne 'FBBDFD7C25B7116E99B1B668C46511B900B04183E217E490D3E351F4942921B7'){throw 'bundle binding mismatch'}
$b=Get-Content -LiteralPath $bundlePath -Raw|ConvertFrom-Json -Depth 100
# Source is frozen provenance, not runtime payload. Validate it at the original
# bundle root while verifying every deployed SDK/runtime file inside the candidate.
$bundleSourceRoot='D:\Artifacts\KnowledgeClipper\playback-control\sdk-runtime-20260908-01'
if((Sha (Join-Path $bundleSourceRoot 'bundle-manifest.json'))-ne (Sha $bundlePath)){throw 'source bundle provenance mismatch'}
$mappedBundleFiles=0;$sourceEvidenceFiles=0
foreach($r in $b.files){
 if($r.path.StartsWith('source/')){Verify (Join-Path $bundleSourceRoot $r.path) $r;$sourceEvidenceFiles++;continue}
 if(!$r.path.StartsWith('runtime/')-and !$r.path.StartsWith('sdk/')-and !$r.path.StartsWith('evidence/')-and $r.path-ne 'README.md'){throw 'unknown bundle deployment category'}
 $relative=if($r.path.StartsWith('runtime/')){'core-v3/'+$r.path.Substring(8)}else{'playback-control/'+$r.path}
 Verify (Join-Path $candidate $relative) $r;$mappedBundleFiles++
}
$runtimeExpected=@($b.files|Where-Object{$_.path.StartsWith('runtime/')})
$runtimeActual=@($m.files|Where-Object{$_.path.StartsWith('core-v3/')})
if($runtimeExpected.Count-ne $runtimeActual.Count){throw 'runtime replacement has missing or stale files'}
$base='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-question-20260908T121038Z\candidate-file-manifest.v1.json'
if((Sha $base)-ne '315DB23774C66B580B5C00437862A761208DFB22E9A3C67774C81E94D8DDDB82'){throw 'sealed base identity drift'}
$inherited=@((Get-Content -LiteralPath $base -Raw|ConvertFrom-Json -Depth 100).files|Where-Object{$_.path.StartsWith('authority/')-or $_.path.StartsWith('contracts/')})
foreach($r in $inherited){Verify (Join-Path $candidate $r.path) $r}
$prebuiltPath=$m.bindings.prebuiltDesktopReceipt.path
if((Sha $prebuiltPath)-ne $m.bindings.prebuiltDesktopReceipt.sha256){throw 'prebuilt receipt drift'}
$prebuilt=Get-Content -LiteralPath $prebuiltPath -Raw|ConvertFrom-Json -Depth 100
foreach($r in $prebuilt.files){Verify (Join-Path $candidate $r.path) $r}
$runnerPath=Join-Path $candidate $m.bindings.runnerReleaseManifest.path
if((Sha $runnerPath)-ne $m.bindings.runnerReleaseManifest.sha256){throw 'runner manifest drift'}
$runner=Get-Content -LiteralPath $runnerPath -Raw|ConvertFrom-Json -Depth 100
foreach($r in $runner.files){Verify (Join-Path $candidate ('actual-runner/'+$r.path)) $r}
foreach($r in $runner.sources){if((Sha $r.path)-ne $r.sha256){throw 'runner source drift'}}
foreach($prefix in @('','actual-runner/app/','core-v3/')){
 if($prefix-ne 'core-v3/'-and (Sha (Join-Path $candidate ($prefix+'KnowledgeClipper.Core.V3.Client.dll')))-ne '1C25A618536D30A0F5CE31C5A0FE1050C821C7E2790B5F70AC04AEAC226C842F'){throw 'SDK client mismatch'}
 if((Sha (Join-Path $candidate ($prefix+'KnowledgeClipper.Core.V3.Contracts.dll')))-ne 'EAFEED544C951E625D09CE92B8578200E3DA24F1F89F55FDE2B547E52CBD12D6'){throw 'SDK contracts mismatch'}
}
Write-Output 'PASS exact Desktop/runtime/SDK/runner and inherited authority/contracts equality'
$data=Join-Path 'D:\AppData\KnowledgeClipper' ('safe-'+$m.candidateId)
if(Test-Path -LiteralPath $data){throw 'safe data root not fresh'}
$priorLocal=$env:LOCALAPPDATA;$env:LOCALAPPDATA=$data
try{
 $exe=Join-Path $candidate 'KnowledgeClipper.exe'
 foreach($mode in @('startup-diagnostic','single-instance-diagnostic')){
  & $exe ('--'+$mode) 2>&1|Tee-Object -FilePath (Join-Path $evidence ($mode+'.log'))
  if($LASTEXITCODE-ne 0){throw ('diagnostic failed '+$mode)}
 }
 $jobs=Join-Path $data 'core-v3'
 & $exe --integration-startup-canary $jobs 2>&1|Tee-Object -FilePath (Join-Path $evidence 'integration-startup.log')
 if($LASTEXITCODE-ne 0){throw 'integration startup failed'}
}finally{$env:LOCALAPPDATA=$priorLocal}
$ipc=Get-Content -LiteralPath (Join-Path $evidence 'integration-startup.log') -Raw|ConvertFrom-Json
if($ipc.result-ne 'PASS'-or !$ipc.namedPipeInvoked-or !$ipc.orchestratorStartedByDesktop-or $ipc.action-ne 'capabilities.get'-or $ipc.externalWorkerInvoked-or $ipc.physicalSpeakerInvoked-or $ipc.ankiInvoked){throw 'unsafe or failed IPC gate'}
if(Get-Process -Id $ipc.orchestratorProcessId -ErrorAction SilentlyContinue){throw 'owned Core process remains'}
foreach($r in $m.files){Verify (Join-Path $candidate $r.path) $r}
Save (Join-Path $evidence 'safe-gates.json') ([ordered]@{status='PASS-SAFE-PLAYBACK-INTEGRATED-NOT-ACTUAL';candidate=$candidate;manifest=(Row $manifestPath);fileCount=$i;bytes=$bytes;treeSha256=$treeHash;desktopEqualityFiles=$prebuilt.files.Count;bundleEqualityFiles=$mappedBundleFiles;externalBundleSourceEvidenceFiles=$sourceEvidenceFiles;externalBundleSourceRoot=$bundleSourceRoot;runtimeEqualityFiles=$runtimeActual.Count;runtimeExtraFiles=0;inheritedAuthorityContractFiles=$inherited.Count;runnerEqualityFiles=$runner.files.Count;desktopDll=(Row (Join-Path $candidate 'KnowledgeClipper.dll'));runnerDll=(Row (Join-Path $candidate 'actual-runner/app/KnowledgeClipper.ActualDesktopFlowRunner.dll'));kernel=(Row (Join-Path $candidate 'core-v3/KnowledgeClipper.Core.V3.Kernel.dll'));bundle=(Row $bundlePath);runnerManifest=(Row $runnerPath);startup=(Row (Join-Path $evidence 'startup-diagnostic.log'));singleInstance=(Row (Join-Path $evidence 'single-instance-diagnostic.log'));readonlyIpc=(Row (Join-Path $evidence 'integration-startup.log'));ownedCoreExited=$true;dataRoot=$data;workerRuns=0;externalWrites=0;speaker=0;actualRun=0;userHandTestEntrypoint=$false;source=(Row $PSCommandPath);assembler=(Row (Join-Path $PSScriptRoot 'New-DesktopReadToAnkiCandidate.ps1'))})
Row (Join-Path $evidence 'safe-gates.json')|ConvertTo-Json
Write-Output 'STATUS VALIDATED'

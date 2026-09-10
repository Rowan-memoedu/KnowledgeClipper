#Requires -Version 7.0
param([Parameter(Mandatory)][string]$CandidateRoot,[Parameter(Mandatory)][string]$EvidenceRoot)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$assembly=Get-Content (Join-Path $EvidenceRoot 'assembly.json') -Raw|ConvertFrom-Json -Depth 100
$base=$assembly.bindings.inheritedBase.root
$old=Get-Content (Join-Path $base 'candidate-integration.json') -Raw|ConvertFrom-Json
$new=Get-Content (Join-Path $CandidateRoot 'candidate-integration.json') -Raw|ConvertFrom-Json
$allowed=@('candidate-integration.json','authority/dependency-manifest.v1.json','authority/win-x64/workers/remnote/appsettings.json')
if(@($assembly.added).Count -ne 0 -or @($assembly.removed).Count -ne 0 -or @($assembly.changed).Count -ne 3){throw 'config-only-delta-count'}
foreach($delta in $assembly.changed){if($delta.path -cnotin $allowed){throw 'non-deployment-file-changed'}}
if($new.PipeName -ceq $old.PipeName -or $new.PipeName -cne ('KnowledgeClipper.CoreV3.'+[IO.Path]::GetFileName($CandidateRoot))){throw 'pipe-not-candidate-scoped'}
$manifest=Assert-OverlayManifest $CandidateRoot $assembly.manifest.path $assembly.manifest.sha256
$baseManifest=Get-Content (Join-Path $base 'candidate-file-manifest.v1.json') -Raw|ConvertFrom-Json -Depth 100
if((Get-OverlaySha (Join-Path $base 'candidate-file-manifest.v1.json')) -cne $assembly.bindings.inheritedBase.manifestSha256){throw 'old-manifest-changed'}
$before=@{};foreach($r in $baseManifest.files){$before[$r.path]=$r}
foreach($r in $manifest.files){if($r.path -cnotin $allowed -and ($r.sha256 -cne $before[$r.path].sha256 -or $r.bytes -ne $before[$r.path].bytes)){throw ('payload-drift: '+$r.path)}}
Write-Host ('PAYLOAD EQUALITY '+($manifest.files.Count-3)+' unchanged / 3 deployment configs')
$oldLocal=$env:LOCALAPPDATA
$listener=$null;$cancellation=[Threading.CancellationTokenSource]::new()
try {
    # FirstPipeInstance refuses to share/replace an existing real user's server.
    # This listener speaks no business protocol: any connection is a gate failure.
    $options=[IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::FirstPipeInstance
    $listener=[IO.Pipes.NamedPipeServerStream]::new($old.PipeName,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,$options)
    $accept=$listener.WaitForConnectionAsync($cancellation.Token)
    if($accept.IsCompleted){throw 'unexpected-old-listener-connection-before-launch'}
    Write-Host ('OWNED OLD PIPE LISTENING '+$old.PipeName)
    $env:LOCALAPPDATA=Join-Path 'D:\AppData\KnowledgeClipper' ([IO.Path]::GetFileName($CandidateRoot)+'-isolation-safe')
    foreach($mode in @('startup-diagnostic','single-instance-diagnostic')) {
        & (Join-Path $CandidateRoot 'KnowledgeClipper.exe') ('--'+$mode) 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot ($mode+'.log'))
        if($LASTEXITCODE -ne 0){throw ('diagnostic-failed: '+$mode)}
    }
    & (Join-Path $CandidateRoot 'KnowledgeClipper.exe') --integration-startup-canary (Join-Path $env:LOCALAPPDATA 'core-v3') 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'integration-startup.log')
    if($LASTEXITCODE -ne 0){throw 'integration-startup-failed'}
    $ipc=Get-Content (Join-Path $EvidenceRoot 'integration-startup.log') -Raw|ConvertFrom-Json
    if($ipc.result -cne 'PASS' -or !$ipc.orchestratorStartedByDesktop -or !$ipc.namedPipeInvoked -or $ipc.queryItemCount -ne 10 -or $ipc.action -cne 'capabilities.get' -or $ipc.externalWorkerInvoked -or $ipc.physicalSpeakerInvoked){throw 'real-candidate-ipc-failed'}
    if($accept.IsCompleted -or $listener.IsConnected){throw 'candidate-connected-to-old-pipe'}
    if(Get-Process -Id $ipc.orchestratorProcessId -ErrorAction SilentlyContinue){throw 'owned-new-host-remains'}
    $result=[ordered]@{status='PASS-CANDIDATE-PIPE-ISOLATION';candidate=$CandidateRoot;oldPipe=$old.PipeName;newPipe=$new.PipeName;oldPipeOwner='test-owned-exclusive-listener-no-protocol';oldPipeConnections=0;newHostStartedByDesktop=$true;newHostPid=$ipc.orchestratorProcessId;newHostExited=$true;actualNamedPipeSdkQuery=$true;catalogItems=10;physicalLegacyHostUsed=$false;userProcessesTerminated=0;externalCalls=0;physicalSpeaker=0;unchangedPayloadFiles=$manifest.files.Count-3;changedDeploymentFiles=$allowed;userHandTestEntrypoint=$false}
} finally {
    $cancellation.Cancel()
    if($null -ne $listener){$listener.Dispose()}
    $cancellation.Dispose();$env:LOCALAPPDATA=$oldLocal
}
Write-OverlayJson (Join-Path $EvidenceRoot 'pipe-isolation.json') $result
Write-Host 'PIPE ISOLATION PASS; old listener untouched by candidate; own listener disposed'

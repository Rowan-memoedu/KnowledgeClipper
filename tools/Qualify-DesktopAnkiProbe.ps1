#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Candidate,[Parameter(Mandatory)][string]$DataRoot,[Parameter(Mandatory)][string]$EvidenceRoot)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$ui=Get-Content (Join-Path $EvidenceRoot 'ui-after.json') -Raw|ConvertFrom-Json
$state=$ui.description|ConvertFrom-Json
$run=Get-Content (Join-Path $EvidenceRoot 'receipt.json') -Raw|ConvertFrom-Json
if($run.status -cne 'PASS' -or $run.semanticButtonInvocations -ne 1 -or !$state.ready -or $state.route -cne 'canary' -or $state.terminalState -cne 'Succeeded'){throw 'actual-ui-not-ready'}
$core=Join-Path $DataRoot 'KnowledgeClipper/core-v3'
$requests=@([IO.Directory]::EnumerateFiles((Join-Path $core 'worker-process-v3'),'request.json',[IO.SearchOption]::AllDirectories))
if($requests.Count -ne 1){throw 'expected-exactly-one-worker-request'}
$requestPath=$requests[0];$attempt=Split-Path $requestPath;$request=Get-Content $requestPath -Raw|ConvertFrom-Json
$resultPath=Join-Path $attempt 'result.json';$result=Get-Content $resultPath -Raw|ConvertFrom-Json
$executionPath=Join-Path $attempt 'worker-execution.evidence.v1.json';$execution=Get-Content $executionPath -Raw|ConvertFrom-Json
$statusPath=Join-Path $attempt 'outputs/connection-status.json';$status=Get-Content $statusPath -Raw|ConvertFrom-Json
$workerReceiptPath=Join-Path $attempt 'outputs/receipt.json';$workerReceipt=Get-Content $workerReceiptPath -Raw|ConvertFrom-Json
if($request.workerId -cne 'export.anki' -or $request.operation -cne 'probe' -or $result.status -cne 'completed' -or @($result.sideEffects).Count -ne 0){throw 'worker-not-read-only-completed'}
if($status.externalMutation -or @($status.mutatingActionsObserved).Count -ne 0 -or $status.operation -cne 'probe' -or $status.networkScope -cne 'loopback-only'){throw 'worker-mutation-observed'}
if($workerReceipt.operation_effect.external_mutation -or $workerReceipt.operation_effect.effect_class -cne 'provider-read-only'){throw 'worker-receipt-side-effect-mismatch'}
$worker=Resolve-OverlayFile $Candidate $execution.startedEntrypoint.candidateRelativePath
Test-OverlayFile $worker $execution.startedEntrypoint
if($execution.process.exitCode -ne 0 -or $execution.process.timedOut -or $execution.process.cancelled){throw 'worker-process-failed'}
$job=Join-Path $core ('worker-process-v3/jobs/'+$request.taskId)
$configRef=$request.inputRefs[0];$configManifest=Resolve-OverlayFile $job $configRef.manifestPath
if((Get-OverlaySha $configManifest) -cne $configRef.expectedSha256.ToUpperInvariant()){throw 'config-manifest-sha-mismatch'}
$configPath=Join-Path (Split-Path $configManifest) 'anki-export.v1.json';$config=Get-Content $configPath -Raw|ConvertFrom-Json
if($config.provision_managed -or $config.expected_profile -cne '账户 1' -or $config.deck_routes.canary -cne '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'){throw 'config-target-or-provision-mismatch'}
if((Get-OverlaySha $configPath) -cne $workerReceipt.inputs.config_sha256.ToUpperInvariant() -or (Get-OverlaySha $requestPath) -cne $workerReceipt.inputs.worker_request_sha256.ToUpperInvariant()){throw 'worker-config-request-binding-mismatch'}
$eventDirectory=Join-Path $core ('events-v3/streams/'+$request.operationId)
$completed=@(Get-ChildItem $eventDirectory -Filter '*.ndjson'|ForEach-Object {Get-Content $_.FullName|ForEach-Object {$_|ConvertFrom-Json}}|Where-Object eventType -CEQ 'operation.completed')
if($completed.Count -ne 1 -or $completed[0].operation -cne 'anki.probe'){throw 'completion-event-mismatch'}
$artifact=$completed[0].artifacts[0]
$artifactManifest=Join-Path $core ('artifact-store-v3/artifacts/'+$artifact.artifactId+'/artifact.json')
if((Get-OverlaySha $artifactManifest) -cne $artifact.manifestDigest.value){throw 'core-artifact-manifest-mismatch'}
$manifest=Get-Content $artifactManifest -Raw|ConvertFrom-Json
$representation=$manifest.representations[0];$artifactData=Resolve-OverlayFile (Split-Path $artifactManifest) $representation.path
Test-OverlayFile $artifactData @{bytes=$representation.bytes;sha256=$representation.digest.value}
$typed=Get-Content $artifactData -Raw|ConvertFrom-Json
if(!$typed.readiness.connectionReady -or !$typed.readiness.selectedRouteReady -or !$typed.readiness.readyForRequestedAction -or $typed.readiness.selectedRoute -cne 'canary' -or @($typed.readiness.blockingIssues).Count -ne 0 -or $typed.externalMutation -or $typed.externalWriteCount -ne 0 -or @($typed.mutatingActionsObserved).Count -ne 0){throw 'typed-status-not-ready-or-mutating'}
if((Get-OverlaySha (Join-Path $EvidenceRoot 'before.json')) -cne (Get-OverlaySha (Join-Path $EvidenceRoot 'after.json'))){throw 'independent-inventory-drift'}
Write-OverlayJson (Join-Path $EvidenceRoot 'qualification.json') ([ordered]@{
 status='PASS-ACTUAL-SETTINGS-READONLY-PROBE';candidate=$Candidate;manifest=(Get-OverlayIdentity (Join-Path $Candidate 'candidate-file-manifest.v1.json'))
 desktopDll=(Get-OverlayIdentity (Join-Path $Candidate 'KnowledgeClipper.dll'));ui=(Get-OverlayIdentity (Join-Path $EvidenceRoot 'ui-after.json'));rawUiText=$ui.text
 operationId=$request.operationId;request=(Get-OverlayIdentity $requestPath);result=(Get-OverlayIdentity $resultPath);workerExecution=(Get-OverlayIdentity $executionPath)
 workerEntrypoint=(Get-OverlayIdentity $worker);workerVersion=$execution.startedEntrypoint.workerVersion;configuration=(Get-OverlayIdentity $configPath)
 workerStatus=(Get-OverlayIdentity $statusPath);workerReceipt=(Get-OverlayIdentity $workerReceiptPath)
 coreStatusManifest=(Get-OverlayIdentity $artifactManifest);coreStatus=(Get-OverlayIdentity $artifactData);typedReadiness=$typed.readiness
 workerRequests=1;semanticButtonInvocations=1;externalMutation=$typed.externalMutation;mutatingActionsObserved=$status.mutatingActionsObserved;resultSideEffects=$result.sideEffects
 independentBefore=(Get-OverlayIdentity (Join-Path $EvidenceRoot 'before.json'));independentAfter=(Get-OverlayIdentity (Join-Path $EvidenceRoot 'after.json'))
 physicalInput=0;scope='read-only probe only; no content/image/remnote/speech worker request';ownedDesktopExited=$run.ownedDesktopExited
 qualifier=(Get-OverlayIdentity $PSCommandPath)
})
Get-OverlayIdentity (Join-Path $EvidenceRoot 'qualification.json')|ConvertTo-Json

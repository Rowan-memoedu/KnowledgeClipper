[CmdletBinding()]
param([string]$RepositoryRoot='D:\Projects\RemNote Quick Clipper',[string]$CandidateRoot='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-20260904T104202Z',[string]$EvidenceRoot=('D:\Artifacts\KnowledgeClipper\actual-harness-offline\'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$harness=Join-Path $RepositoryRoot 'tools\actual-harness\Invoke-KnowledgeClipperActualJourney.ps1'
$source=[IO.File]::ReadAllText($harness)
foreach($name in @('Host','HOME','home','CODEX_HOME','PID','Error','Args','Input','Matches','MyInvocation','PSScriptRoot')){if([regex]::IsMatch($source,'(?im)\$'+[regex]::Escape($name)+'\s*=')){throw "reserved-variable-assignment:$name"}}
foreach($needle in @('Set-StrictMode -Version Latest','$candidateProcess = $null','$uiaRoot = $null','$pipeClient = $null','$ankiAdapter = $null','actual-harness.log','actual-harness.receipt.json','--open-settings','cleanup.candidate-process')){if(-not$source.Contains($needle,[StringComparison]::Ordinal)){throw "static-gate-missing:$needle"}}
foreach($needle in @('New-ActualPortGraph','OwnedCandidateProcessPort','ExternalWindowsUiaSettingsPort','NamedPipeTaskObserverPort','AnkiConnectReadOnlyEvidencePort','TaskProducedSnapshotEvidencePort','actual-external-uia-runner-not-yet-implemented')){if(-not$source.Contains($needle,[StringComparison]::Ordinal)){throw "actual-factory-static-gate-missing:$needle"}}
[IO.Directory]::CreateDirectory($EvidenceRoot)|Out-Null
$settingsUiLog=Join-Path $EvidenceRoot 'actual-settings-ui-builder.log';$settingsUiRoot=Join-Path $EvidenceRoot 'actual-settings-ui-evidence'
$dotnet='D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
& $dotnet run --project (Join-Path $RepositoryRoot 'desktop\RemNoteQuickClipper.Desktop.WindowsCanary.csproj') --configuration Release -- --actual-harness-settings-evidence $settingsUiRoot *> $settingsUiLog
if($LASTEXITCODE-ne0){throw 'actual-settings-ui-builder-canary-failed'}
$settingsUiText=[IO.File]::ReadAllText($settingsUiLog)
foreach($requiredEvidence in @('Anki custom deck route saves and reloads through the descriptor-owned next-task settings fields','new task freezes the validated custom Anki deck route in its SHA-bound config artifact with zero secrets','WINDOWS_ACTUAL_HARNESS_SETTINGS_EVIDENCE PASS')){if(-not$settingsUiText.Contains($requiredEvidence,[StringComparison]::Ordinal)){throw 'actual-settings-ui-evidence-missing'}}
$settingsEvidenceReceipt=Join-Path $settingsUiRoot 'actual-settings-ui-evidence.receipt.json';if(-not(Test-Path $settingsEvidenceReceipt)){throw 'actual-settings-ui-receipt-missing'}
$planRoot=Join-Path $EvidenceRoot 'actual-plan-describe'
& pwsh -NoLogo -NoProfile -File $harness -Mode Actual -DescribeActualPlan -CandidateRoot $CandidateRoot -RunRoot $planRoot -RepositoryRoot $RepositoryRoot *> (Join-Path $EvidenceRoot 'actual-plan-describe.console.log')
if($LASTEXITCODE-ne0){throw 'actual-plan-describe-failed'}
$planPath=Join-Path $planRoot 'actual-plan.json';if(-not(Test-Path $planPath)){throw 'actual-plan-missing'}
$plan=Get-Content -Raw $planPath|ConvertFrom-Json -Depth 30
$portValues=@($plan.ports.PSObject.Properties|Where-Object{$_.Name-ne'schemaVersion'}|ForEach-Object{$_.Value})
$portTypes=@($portValues|ForEach-Object{$_.type})
if(@($portTypes|Where-Object{$_ -like '*Fake*'}).Count-ne0){throw 'actual-plan-contains-fake'}
if(@($portTypes|Where-Object{$_ -in @('OwnedCandidateProcessPort','ExternalWindowsUiaSettingsPort','NamedPipeTaskObserverPort','AnkiConnectReadOnlyEvidencePort','TaskProducedSnapshotEvidencePort')}).Count-ne5){throw 'actual-plan-real-port-identity-missing'}
if(@($portValues|Where-Object{$_.connectsDuringDescribe-ne$false}).Count-ne0){throw 'actual-plan-connected-during-describe'}
$scenarios=@('success','candidate-invalid','child-start','window-attach','pipe-connect','settings-open','settings-save','snapshot-bind','pre-task-count','selection-capture','preview','anki-readback','cleanup-close-error','child-close-timeout')
$rows=[Collections.Generic.List[object]]::new()
foreach($scenario in $scenarios){
    $run=Join-Path $EvidenceRoot $scenario
    & pwsh -NoLogo -NoProfile -File $harness -Mode OfflineFake -CandidateRoot $CandidateRoot -RunRoot $run -RepositoryRoot $RepositoryRoot -SettingsEvidenceReceipt $settingsEvidenceReceipt -OfflineScenario $scenario *> (Join-Path $EvidenceRoot ($scenario+'.console.log'))
    $exit=$LASTEXITCODE;$receiptPath=Join-Path $run 'actual-harness.receipt.json';if(-not(Test-Path $receiptPath)){throw "receipt-missing:$scenario"};$receipt=Get-Content -Raw $receiptPath|ConvertFrom-Json -Depth 30
    if($scenario-in@('success','cleanup-close-error','child-close-timeout')){if($exit-ne0-or$receipt.status-ne'passed-offline-fake'){throw "success-scenario-failed:$scenario"}}elseif($exit-ne20-or$receipt.status-ne'failed-closed'){throw "failure-scenario-not-closed:$scenario"}
    $expectedCleanupErrors=if($scenario-eq'cleanup-close-error'){1}else{0}
    if($receipt.externalCalls-ne0-or$receipt.ankiWrites-ne0-or$receipt.businessTasks-ne0-or$receipt.ownedProcessesRemaining-ne0-or@($receipt.cleanupErrors).Count-ne$expectedCleanupErrors){throw "side-effect-or-cleanup-gate:$scenario"}
    if($scenario-eq'cleanup-close-error' -and (-not$receipt.cleanupState.pipeClientClosed-or-not$receipt.cleanupState.uiaRootClosed-or-not$receipt.cleanupState.candidateProcessClosed)){throw 'cleanup-did-not-continue'}
    if($scenario-eq'child-close-timeout' -and -not$receipt.cleanupState.killFallbackUsed){throw 'child-kill-fallback-not-used'}
    if(-not(Test-Path (Join-Path $run 'actual-harness.log'))){throw "durable-log-missing:$scenario"}
    $rows.Add([ordered]@{scenario=$scenario;status=$receipt.status;failureCode=$receipt.failureCode;receiptSha256=(Get-FileHash $receiptPath -Algorithm SHA256).Hash})
}
$success=Get-Content -Raw (Join-Path $EvidenceRoot 'success\actual-harness.receipt.json')|ConvertFrom-Json -Depth 30
if($success.settingsEvidence.deck-ne'零碎知识剪藏::Canary::Desktop-V14'-or$success.settingsEvidence.uiRequestCanOverrideDestination-ne$false){throw 'settings-binding-gate'}
$final=[ordered]@{schemaVersion='knowledge-clipper-actual-harness-offline-receipt.v1';status='PASS';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');harnessPath=$harness;harnessSha256=(Get-FileHash $harness -Algorithm SHA256).Hash;candidateRoot=$CandidateRoot;candidateManifestSha256=(Get-FileHash (Join-Path $CandidateRoot 'candidate-file-manifest.v1.json') -Algorithm SHA256).Hash;scenarios=$rows;scenarioCount=$rows.Count;externalCalls=0;ankiWrites=0;businessTasks=0;ownedProcessesRemaining=0;settingsSnapshotSha256=$success.settingsEvidence.snapshotSha256;ankiConfigSha256=$success.settingsEvidence.configSha256;uiRequestCanOverrideDestination=$false;actualSettingsUiBuilder=[ordered]@{log=$settingsUiLog;logSha256=(Get-FileHash $settingsUiLog -Algorithm SHA256).Hash;status='PASS';usesActualSettingsForm=$true;usesDesktopTaskSettingsArtifacts=$true};actualFactoryGraph=[ordered]@{plan=$planPath;planSha256=(Get-FileHash $planPath -Algorithm SHA256).Hash;portTypes=$portTypes;containsFake=$false;connectionsDuringDescribe=0;executionStatus='fail-closed-not-implemented'};actualRequiresQualityAuthorization=$true;mayRunActual=$false}
$finalPath=Join-Path $EvidenceRoot 'offline-qualification.receipt.json';[IO.File]::WriteAllText($finalPath,(ConvertTo-Json $final -Depth 30)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false));Write-Output $finalPath
exit 0

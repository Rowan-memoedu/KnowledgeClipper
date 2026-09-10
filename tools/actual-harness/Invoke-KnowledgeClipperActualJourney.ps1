[CmdletBinding()]
param(
    [ValidateSet('OfflineFake','Actual')][string]$Mode = 'OfflineFake',
    [Parameter(Mandatory=$true)][string]$CandidateRoot,
    [Parameter(Mandatory=$true)][string]$RunRoot,
    [string]$RepositoryRoot = 'D:\Projects\RemNote Quick Clipper',
    [string]$CanaryDeck = '零碎知识剪藏::Canary::Desktop-V14',
    [string]$QualityAuthorization,
    [string]$SettingsEvidenceReceipt,
    [switch]$DescribeActualPlan,
    [ValidateSet('success','candidate-invalid','child-start','window-attach','pipe-connect','settings-open','settings-save','snapshot-bind','pre-task-count','selection-capture','preview','anki-readback','cleanup-close-error','child-close-timeout')][string]$OfflineScenario = 'success'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$candidateProcess = $null
$uiaRoot = $null
$pipeClient = $null
$ankiAdapter = $null
$sourceWindow = $null
$logWriter = $null
$result = 'pending'
$failureCode = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$externalCalls = 0
$ankiWrites = 0
$businessTasks = 0
$ownedProcessesRemaining = 0

function Write-JsonAtomic([string]$Path,[object]$Value){$temporaryPath=$Path+'.tmp';[IO.File]::WriteAllText($temporaryPath,(ConvertTo-Json $Value -Depth 30)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false));Move-Item -LiteralPath $temporaryPath -Destination $Path -Force}
function Get-Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()}
function Log([string]$Message){$line=([DateTimeOffset]::UtcNow.ToString('O')+' '+$Message);$script:logWriter.WriteLine($line);$script:logWriter.Flush()}
function Require([bool]$Condition,[string]$Code){if(-not$Condition){throw $Code}}
function Resolve-SettingsArgument([string]$Root){
    $programPath=Join-Path $Root 'src\Program.cs';Require (Test-Path -LiteralPath $programPath) 'launch-contract-source-missing'
    $source=[IO.File]::ReadAllText($programPath);$argumentMatches=[regex]::Matches($source,'string\.Equals\(args\[0\],\s*"(?<argument>--open-settings)"')
    Require ($argumentMatches.Count -eq 1) 'launch-contract-open-settings-not-unique'
    return $argumentMatches[0].Groups['argument'].Value
}
function Test-CandidateManifest([string]$Root){
    $manifestPath=Join-Path $Root 'candidate-file-manifest.v1.json';Require (Test-Path $manifestPath) 'candidate-manifest-missing'
    $manifest=Get-Content -Raw $manifestPath|ConvertFrom-Json -Depth 100
    $physical=@(Get-ChildItem -LiteralPath $Root -File -Recurse|ForEach-Object{[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/')}|Where-Object{$_-ne'candidate-file-manifest.v1.json'}|Sort-Object -CaseSensitive)
    $declared=@($manifest.files.path|Sort-Object -CaseSensitive)
    Require (($physical -join "`n") -ceq ($declared -join "`n")) 'candidate-physical-declared-drift'
    return $manifest
}
function Read-BoundSettingsEvidence([string]$ReceiptPath,[string]$Deck){
    Require (Test-Path -LiteralPath $ReceiptPath) 'settings-evidence-receipt-missing';$receipt=Get-Content -Raw $ReceiptPath|ConvertFrom-Json -Depth 30
    Require ($receipt.status-eq'PASS'-and$receipt.settingsUi-eq'actual-SettingsForm'-and$receipt.publisher-eq'DesktopTaskSettingsArtifacts/CoreDesktopTaskSettingsPublisher') 'settings-evidence-not-production-path'
    Require ($receipt.ankiConfig.deck-eq$Deck-and$receipt.uiRequestCanOverrideDestination-eq$false) 'settings-evidence-deck-binding-invalid'
    $evidenceRoot=Split-Path -Parent ([IO.Path]::GetFullPath($ReceiptPath));$snapshotPath=Join-Path $evidenceRoot $receipt.taskSnapshot.path;$configPath=Join-Path $evidenceRoot $receipt.ankiConfig.path
    Require ((Get-Sha $snapshotPath)-eq$receipt.taskSnapshot.sha256) 'settings-snapshot-sha-mismatch';Require ((Get-Sha $configPath)-eq$receipt.ankiConfig.sha256) 'anki-config-sha-mismatch'
    return [ordered]@{receiptPath=[IO.Path]::GetFullPath($ReceiptPath);receiptSha256=Get-Sha $ReceiptPath;configPath=$configPath;configSha256=$receipt.ankiConfig.sha256;snapshotPath=$snapshotPath;snapshotSha256=$receipt.taskSnapshot.sha256;uiRequestCanOverrideDestination=$false;deck=$Deck}
}
function New-FakeResource([string]$Kind,[bool]$ThrowOnClose=$false){[pscustomobject]@{kind=$Kind;closed=$false;throwOnClose=$ThrowOnClose}}
function Close-FakeResource([object]$Resource,[string]$Kind){if($null-eq$Resource){return};try{if($Resource.throwOnClose){throw ('fake.cleanup.'+$Kind)};$Resource.closed=$true;Log ('cleanup.'+$Kind+'.ok')}catch{$script:cleanupErrors.Add($Kind+':'+$_.Exception.Message);Log ('cleanup.'+$Kind+'.failed-but-continuing')}}
function Stop-OwnedProcess([object]$ProcessValue){
    if($null-eq$ProcessValue){return}
    try{
        if($ProcessValue.PSObject.Properties.Name -contains 'closed'){
            $killFallback=$false
            if($ProcessValue.PSObject.Properties.Name -contains 'closeMainWindowResult' -and -not$ProcessValue.closeMainWindowResult){$ProcessValue.killFallbackUsed=$true;$killFallback=$true}
            $ProcessValue.closed=$true;Log ('cleanup.candidate-process.fake.ok killFallback='+$killFallback);return
        }
        if(-not$ProcessValue.HasExited){$closed=$ProcessValue.CloseMainWindow();if(-not$closed-or-not$ProcessValue.WaitForExit(3000)){$ProcessValue.Kill($true);$ProcessValue.WaitForExit()}};Log 'cleanup.candidate-process.ok'
    }catch{$script:cleanupErrors.Add('candidateProcess:'+$_.Exception.Message)}
}
function Read-QualityAuthorization([string]$Path,[string]$ManifestSha){Require (-not[string]::IsNullOrWhiteSpace($Path)) 'quality-authorization-required';Require (Test-Path -LiteralPath $Path) 'quality-authorization-missing';$authorization=Get-Content -Raw $Path|ConvertFrom-Json -Depth 20;Require ($authorization.mayRunOneActual-eq$true) 'quality-authorization-denied';Require ([string]$authorization.candidateManifestSha256-eq$ManifestSha) 'quality-authorization-candidate-mismatch';return $authorization}

function New-ActualPortGraph([string]$Root){
    $exe=@(Get-ChildItem -LiteralPath $Root -Filter KnowledgeClipper.exe -File -Recurse)
    Require ($exe.Count -eq 1) 'actual-candidate-executable-not-unique'
    [ordered]@{
        schemaVersion='knowledge-clipper-actual-port-graph.v1'
        candidateLauncher=[ordered]@{type='OwnedCandidateProcessPort';executable=$exe[0].FullName;launchArgument='--open-settings';ownsExactPid=$true;connectsDuringDescribe=$false}
        settingsUi=[ordered]@{type='ExternalWindowsUiaSettingsPort';windowTitle='零碎知识剪藏 · 设置';saveThroughUia=$true;directFormConstruction=$false;connectsDuringDescribe=$false}
        taskUi=[ordered]@{type='ExternalWindowsUiaTaskPort';captureAction='knowledge-clipper.desktop.capture.read';inputInjection='external-selection-only';connectsDuringDescribe=$false}
        taskObserver=[ordered]@{type='NamedPipeTaskObserverPort';protocol='public-candidate-config';mutation=$false;connectsDuringDescribe=$false}
        ankiReadback=[ordered]@{type='AnkiConnectReadOnlyEvidencePort';deck=$CanaryDeck;writeMethodsProhibited=$true;connectsDuringDescribe=$false}
        snapshotEvidence=[ordered]@{type='TaskProducedSnapshotEvidencePort';source='immutable-task-package';prebuiltSettingsReceiptProhibited=$true;connectsDuringDescribe=$false}
    }
}
function Write-ActualPlan([string]$Path,[object]$Graph){Write-JsonAtomic $Path ([ordered]@{schemaVersion='knowledge-clipper-actual-plan.v1';mode='Actual';sideEffects='none-describe-only';authorizationRequiredBeforeConstruction=$true;ports=$Graph;steps=@('validate sealed candidate','validate Quality authorization','start exact candidate PID with isolated data root','attach external UIA to settings window','save Desktop-V14 through UIA','read task-produced public snapshot evidence','select text in external source window','invoke actual capture button through UIA','observe candidate Named Pipe/task state','read back exact Canary deck through read-only AnkiConnect','cleanup every owned resource')})}

# Actual is intentionally a separate factory/runner. It must never call New-FakeResource,
# consume prebuilt SettingsEvidenceReceipt, or accept injected selected/context/final text.
function Invoke-ActualJourney([object]$PortGraph,[object]$Authorization){
    Require ($null-ne$Authorization) 'quality-authorization-required-before-actual-factory'
    $actualTypes=@($PortGraph.GetEnumerator()|ForEach-Object{$_.Value}|Where-Object{$_ -is [Collections.IDictionary]}|ForEach-Object{$_.type})
    Require (@($actualTypes|Where-Object{$_ -like '*Fake*'}).Count -eq 0) 'actual-port-graph-contains-fake'
    # The executable/UIA/pipe/readback adapters are constructed only after authorization.
    # Until the external UIA adapter is landed, fail closed before starting any process;
    # this prevents the former passed-offline-fake false positive.
    throw 'actual-external-uia-runner-not-yet-implemented'
}

$absoluteRunRoot=[IO.Path]::GetFullPath($RunRoot)
Require (-not(Test-Path -LiteralPath $absoluteRunRoot)) 'immutable-run-root-exists'
[IO.Directory]::CreateDirectory($absoluteRunRoot)|Out-Null
$logPath=Join-Path $absoluteRunRoot 'actual-harness.log'
$receiptPath=Join-Path $absoluteRunRoot 'actual-harness.receipt.json'
$pending=[ordered]@{schemaVersion='knowledge-clipper-actual-harness-receipt.v1';status='pending';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');mode=$Mode;scenario=$OfflineScenario;candidateRoot=[IO.Path]::GetFullPath($CandidateRoot);runRoot=$absoluteRunRoot;businessTasks=0;externalCalls=0;ankiWrites=0}
Write-JsonAtomic $receiptPath $pending
$logWriter=[IO.StreamWriter]::new($logPath,$false,[Text.UTF8Encoding]::new($false));Log 'receipt.pending.created-before-launch'

$settingsEvidence=$null
$launchArgument=$null
try{
    $launchArgument=Resolve-SettingsArgument $RepositoryRoot;Require ($launchArgument -ceq '--open-settings') 'launch-contract-drift';Log ('launch.argument='+$launchArgument)
    if($OfflineScenario-eq'candidate-invalid' -and $Mode-eq'OfflineFake'){throw 'fake.candidate-invalid'}
    $manifest=Test-CandidateManifest ([IO.Path]::GetFullPath($CandidateRoot));Log ('candidate.inventory.files='+$manifest.payload.files)
    if($Mode-eq'Actual'){
        $actualGraph=New-ActualPortGraph ([IO.Path]::GetFullPath($CandidateRoot))
        if($DescribeActualPlan){Write-ActualPlan (Join-Path $absoluteRunRoot 'actual-plan.json') $actualGraph;$result='described-actual-plan';Log 'actual.plan.described-with-zero-connections'}
        else{$authorization=Read-QualityAuthorization $QualityAuthorization (Get-Sha (Join-Path $CandidateRoot 'candidate-file-manifest.v1.json'));Log ('quality.authorization='+$authorization.authorizationId);Invoke-ActualJourney $actualGraph $authorization}
        return
    }
    Require (-not[string]::IsNullOrWhiteSpace($SettingsEvidenceReceipt)) 'offline-settings-evidence-required'
    if($OfflineScenario-eq'child-start'){throw 'fake.child-start'}
    if($OfflineScenario-eq'child-close-timeout'){$candidateProcess=[pscustomobject]@{kind='child';closed=$false;closeMainWindowResult=$false;killFallbackUsed=$false}}else{$candidateProcess=New-FakeResource 'child'}
    if($OfflineScenario-eq'window-attach'){throw 'fake.window-attach'};$uiaRoot=New-FakeResource 'window'
    if($OfflineScenario-eq'pipe-connect'){throw 'fake.pipe-connect'};$pipeClient=New-FakeResource 'named-pipe'
    if($OfflineScenario-eq'settings-open'){throw 'fake.settings-open'}
    if($OfflineScenario-eq'settings-save'){throw 'fake.settings-save'}
    if($OfflineScenario-eq'snapshot-bind'){throw 'fake.snapshot-bind'}
    $settingsEvidence=Read-BoundSettingsEvidence $SettingsEvidenceReceipt $CanaryDeck
    Require ($businessTasks -eq 0 -and $externalCalls -eq 0 -and $ankiWrites -eq 0) 'pre-task-count-nonzero';Log 'pre-task-count business=0 external=0 ankiWrites=0'
    if($OfflineScenario-eq'pre-task-count'){throw 'fake.pre-task-count'}
    if($OfflineScenario-eq'selection-capture'){throw 'fake.selection-capture'};$sourceWindow=New-FakeResource 'external-window'
    if($OfflineScenario-eq'preview'){throw 'fake.preview'}
    $ankiAdapter=New-FakeResource 'anki-adapter' ($OfflineScenario-eq'cleanup-close-error')
    if($OfflineScenario-eq'anki-readback'){throw 'fake.anki-readback'}
    $result='passed-offline-fake'
}
catch{$failureCode=$_.Exception.Message;$result='failed-closed';Log ('failure='+$failureCode)}
finally{
    Close-FakeResource $ankiAdapter 'anki-adapter'
    Close-FakeResource $pipeClient 'named-pipe'
    Close-FakeResource $uiaRoot 'uia-root'
    Close-FakeResource $sourceWindow 'source-window'
    Stop-OwnedProcess $candidateProcess
    $ownedProcessesRemaining=0
    $final=[ordered]@{schemaVersion='knowledge-clipper-actual-harness-receipt.v1';status=$result;createdAtUtc=$pending.createdAtUtc;completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');mode=$Mode;scenario=$OfflineScenario;candidateRoot=$pending.candidateRoot;runRoot=$absoluteRunRoot;launchArgument=$launchArgument;failureCode=$failureCode;settingsEvidence=$settingsEvidence;businessTasks=$businessTasks;externalCalls=$externalCalls;ankiWrites=$ankiWrites;ownedProcessesRemaining=$ownedProcessesRemaining;cleanupErrors=@($cleanupErrors);cleanupState=[ordered]@{candidateProcessClosed=($null-eq$candidateProcess-or$candidateProcess.closed);uiaRootClosed=($null-eq$uiaRoot-or$uiaRoot.closed);pipeClientClosed=($null-eq$pipeClient-or$pipeClient.closed);sourceWindowClosed=($null-eq$sourceWindow-or$sourceWindow.closed);ankiAdapterClosed=($null-eq$ankiAdapter-or$ankiAdapter.closed);killFallbackUsed=($null-ne$candidateProcess-and$candidateProcess.PSObject.Properties.Name-contains'killFallbackUsed'-and$candidateProcess.killFallbackUsed)};log='actual-harness.log'}
    Write-JsonAtomic $receiptPath $final
    if($null-ne$logWriter){$logWriter.Dispose();$logWriter=$null}
}
if($result-ne'passed-offline-fake' -and $result-ne'described-actual-plan'){exit 20}
Write-Output $receiptPath

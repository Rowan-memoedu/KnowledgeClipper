[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\state-aware-retention-production\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
)

$ErrorActionPreference='Stop'
$utf8=[Text.UTF8Encoding]::new($false)
$project=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
if(Test-Path -LiteralPath $evidence){throw "Append-only evidence root exists: $evidence"}
$jobs=Join-Path $evidence 'jobs';$quarantine=Join-Path $evidence 'quarantine';[IO.Directory]::CreateDirectory($jobs)|Out-Null
$orchestrator=Join-Path $project 'orchestrator\bin\Release\net8.0\KnowledgeClipper.Orchestrator.dll'
if(-not(Test-Path -LiteralPath $orchestrator -PathType Leaf)){throw 'Release Orchestrator DLL is missing.'}
$dotnet='D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
if(-not(Test-Path -LiteralPath $dotnet -PathType Leaf)){throw 'Pinned .NET runtime is missing.'}
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function WriteOnce([string]$Path,[byte[]]$Bytes){if(Test-Path -LiteralPath $Path){throw "Append-only target exists: $Path"};[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null;[IO.File]::WriteAllBytes($Path,$Bytes)}
function JsonOnce([string]$Path,$Value){WriteOnce $Path $utf8.GetBytes((($Value|ConvertTo-Json -Depth 40)+"`n"))}
function Evidence([string]$Path){$item=Get-Item -LiteralPath $Path;[ordered]@{path=$item.FullName;bytes=$item.Length;sha256=Sha $Path}}
function Seed([string]$TaskId,[string]$RetentionClass,[string]$Status,[bool]$Safe,[bool]$Unknown=$false){
  $job=Join-Path $jobs $TaskId;[IO.Directory]::CreateDirectory($job)|Out-Null
  JsonOnce (Join-Path $job 'task.manifest.v2.json') ([ordered]@{schemaVersion='2.0.0';taskId=$TaskId;createdAt='2020-01-01T00:00:00Z';createdBy=[ordered]@{application='retention-production-canary';version='1.0.0'};intent='clip.card';executionProfile='cards';destinations=@();inputRefs=@();requestedOutputs=@();initialPlanRef='plans/none.json';retentionClass=$RetentionClass})
  $events=Join-Path $job 'runs\run-retention\events';[IO.Directory]::CreateDirectory($events)|Out-Null
  $states=@('validating','queued','running');if($Status -ne 'running'){$states += $Status};$sequence=0
  foreach($state in $states){$sequence++;JsonOnce (Join-Path $events ('{0:D6}-task-state.json' -f $sequence)) ([ordered]@{schemaVersion='1.0.0';sequence=$sequence;eventId="event-$sequence";timestamp=('2020-01-0{0}T00:00:00Z' -f [Math]::Min($sequence,9));type='task.state';taskId=$TaskId;runId='run-retention';stageId=$null;attemptId=$null;commandId=$null;idempotencyKey=$null;payload=[ordered]@{status=$state}})}
  if($Safe){$sequence++;JsonOnce (Join-Path $events ('{0:D6}-retention-safe-to-delete.json' -f $sequence)) ([ordered]@{schemaVersion='1.0.0';sequence=$sequence;eventId="event-$sequence";timestamp='2020-01-09T00:00:00Z';type='retention.safe_to_delete';taskId=$TaskId;runId='run-retention';stageId=$null;attemptId=$null;commandId=$null;idempotencyKey=$null;payload=[ordered]@{status=$Status;reconciled=$true}})}
  if($Unknown){JsonOnce (Join-Path $job 'runs\run-retention\stages\export\attempts\attempt-1\result.json') ([ordered]@{status='unknown_external_state'})}
}

Seed 'retention-terminal-ephemeral' 'ephemeral' 'succeeded' $true
Seed 'retention-evidence' 'evidence' 'succeeded' $true
Seed 'retention-pinned' 'pinned' 'succeeded' $true
Seed 'retention-active' 'ephemeral' 'running' $true
Seed 'retention-waiting' 'ephemeral' 'waiting_input' $true
Seed 'retention-unknown' 'ephemeral' 'failed' $true $true
$policy=Join-Path $evidence 'retention-policy.snapshot.v1.json';JsonOnce $policy ([ordered]@{schemaVersion='knowledge-clipper-retention-policy.v1';policyId='isolated-canary-policy';action='quarantine';automatic=$false;minimumTerminalAgeHours=1;settingsSnapshot='explicit-isolated-canary';createdAt=[DateTimeOffset]::UtcNow.ToString('o')})
$evaluate=(& $dotnet $orchestrator retention-evaluate --jobs-root $jobs --minimum-terminal-age-hours 1 2>&1)-join [Environment]::NewLine
if($LASTEXITCODE -ne 0){throw "Retention evaluate failed: $evaluate"};$evaluatePath=Join-Path $evidence 'retention-evaluate.json';WriteOnce $evaluatePath $utf8.GetBytes($evaluate.TrimEnd()+"`n")
$quarantineResult=(& $dotnet $orchestrator retention-quarantine --jobs-root $jobs --minimum-terminal-age-hours 1 --retention-policy-snapshot $policy --quarantine-root $quarantine 2>&1)-join [Environment]::NewLine
if($LASTEXITCODE -ne 0){throw "Retention quarantine failed: $quarantineResult"};$quarantinePath=Join-Path $evidence 'retention-quarantine.json';WriteOnce $quarantinePath $utf8.GetBytes($quarantineResult.TrimEnd()+"`n")
$evaluation=$evaluate|ConvertFrom-Json -Depth 60;$result=$quarantineResult|ConvertFrom-Json -Depth 60
if(@($result.quarantined).Count -ne 1 -or $result.quarantined[0].taskId -ne 'retention-terminal-ephemeral'){throw 'Only the explicitly reconciled terminal ephemeral task may quarantine.'}
if((Test-Path -LiteralPath (Join-Path $jobs 'retention-terminal-ephemeral')) -or -not(Test-Path -LiteralPath $result.quarantined[0].quarantineJobRoot)){throw 'Eligible isolated task was not recoverably quarantined.'}
foreach($preserved in 'retention-evidence','retention-pinned','retention-active','retention-waiting','retention-unknown'){if(-not(Test-Path -LiteralPath (Join-Path $jobs $preserved))){throw "Protected task was moved: $preserved"}}
$receipt=Join-Path $evidence 'receipt.json';JsonOnce $receipt ([ordered]@{schemaVersion='knowledge-clipper-state-aware-retention-production-canary.v1';status='passed';boundary='isolated-jobs-root-only';orchestrator=Evidence $orchestrator;policy=Evidence $policy;evaluate=Evidence $evaluatePath;quarantine=Evidence $quarantinePath;quarantined=@($result.quarantined);preserved=@('retention-evidence','retention-pinned','retention-active','retention-waiting','retention-unknown');destructiveDeletes=0;automaticProductionInvocation=$false;legacyJobStorePurgeInvoked=$false})
[ordered]@{status='passed';evidenceRoot=$evidence;receipt=$receipt;receiptSha256=Sha $receipt;quarantined=$result.quarantined;preserved=@('retention-evidence','retention-pinned','retention-active','retention-waiting','retention-unknown')}|ConvertTo-Json -Depth 30

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)

$ErrorActionPreference='Stop'
$utf8=[Text.UTF8Encoding]::new($false)
$project=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
$jobs=Join-Path $evidence 'jobs'
$authority=Join-Path $project 'contracts\authority\worker-registry\v1.2.33-20260901T041000Z'
$release=Join-Path $project 'releases\knowledge-clipper-authority-v1.2.33-20260901T041000Z\win-x64'
$receipt=Join-Path $evidence 'initial-receipt.json'
if(Test-Path -LiteralPath $receipt){throw "Append-only receipt already exists: $receipt"}
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function WriteOnce([string]$Path,[byte[]]$Bytes){if(Test-Path -LiteralPath $Path){throw "Append-only file exists: $Path"};[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null;[IO.File]::WriteAllBytes($Path,$Bytes)}
function JsonOnce([string]$Path,$Value){WriteOnce $Path $utf8.GetBytes((($Value|ConvertTo-Json -Depth 100)+"`n"))}
function Evidence([string]$Path){$item=Get-Item -LiteralPath $Path;[ordered]@{path=$item.FullName;bytes=$item.Length;sha256=Sha $Path}}
function Pipe($Payload,[string]$Type){
  $correlation='v1233-recover-'+[Guid]::NewGuid().ToString('N')
  $request=[ordered]@{protocolVersion='1.0';correlationId=$correlation;messageType=$Type;payload=$Payload}
  $client=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut)
  try{$client.Connect(10000);$bytes=$utf8.GetBytes(($request|ConvertTo-Json -Depth 100 -Compress)+"`n");$client.Write($bytes,0,$bytes.Length);$client.Flush();$reader=[IO.StreamReader]::new($client,$utf8,$false,4096,$true);$line=$reader.ReadLine();$reader.Dispose();if([string]::IsNullOrWhiteSpace($line)){throw 'Named Pipe response was empty.'};$response=$line|ConvertFrom-Json -Depth 100;if(-not $response.ok){throw "Named Pipe rejected: $($response.error.code) $($response.error.message)"};$response}finally{$client.Dispose()}
}

$job=Get-ChildItem -LiteralPath $jobs -Directory | Select-Object -First 1
if($null -eq $job){throw 'No task job exists for recovery.'}
$taskId=$job.Name
$registry=Join-Path $authority 'worker-registry.v1.json';$checkpoint=Join-Path $authority 'checkpoint.json';$exe=Join-Path $release 'orchestrator\KnowledgeClipper.Orchestrator.exe';$ipcSource=Join-Path $release 'contracts\core\v2.3\playback-control\orchestrator-ipc.v1.1.json';$adapter=Join-Path $release 'contracts\core\v2.1\profile-resolver.v2.1.json'
foreach($path in @($registry,$checkpoint,$exe,$ipcSource,$adapter,(Join-Path $job.FullName 'task.manifest.v2.json'))){if(-not(Test-Path -LiteralPath $path)){throw "Required recovery input missing: $path"}}
$ipc=Get-Content -LiteralPath $ipcSource -Raw|ConvertFrom-Json -Depth 30;$script:pipeName='KnowledgeClipper.AuthorityV1233.Recover.'+[Guid]::NewGuid().ToString('N');$ipc.pipeName=$script:pipeName;$ipc.endpoint="\\.\pipe\$script:pipeName";$runtimeIpc=Join-Path $evidence 'orchestrator-ipc.recovery.v1.1.json';JsonOnce $runtimeIpc $ipc
$process=$null;$stdout=$null;$stderr=$null
try {
  $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$exe;$start.WorkingDirectory=[IO.Path]::GetDirectoryName($exe);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  foreach($arg in @('serve','--ipc-config',$runtimeIpc,'--jobs-root',$jobs,'--registry',$registry,'--release-root',$release,'--max-active-tasks','1','--adapter-config',$adapter)){$start.ArgumentList.Add($arg)}
  $process=[Diagnostics.Process]::new();$process.StartInfo=$start;if(-not $process.Start()){throw 'Failed to start sealed authority recovery Orchestrator.'};$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  $ready=$false;for($i=0;$i -lt 120 -and -not $ready;$i++){if($process.HasExited){throw "Orchestrator exited during recovery startup: $($process.ExitCode)"};try{$null=Pipe ([ordered]@{schemaVersion='2.3.0';queryId=('ready-'+[Guid]::NewGuid().ToString('N'));type='task.query_recent';limit=1}) 'task-query';$ready=$true}catch{Start-Sleep -Milliseconds 100}}
  if(-not $ready){throw 'Recovery Orchestrator Named Pipe did not become ready.'}
  $query=Pipe ([ordered]@{schemaVersion='2.3.0';queryId=('query-'+[Guid]::NewGuid().ToString('N'));type='task.get';taskId=$taskId}) 'task-query'
  $projection=@($query.payload.items)|Select-Object -First 1
  if($null -eq $projection -or [string]$projection.status -ne 'waiting_input' -or [string]$projection.decisionTargetToken -notmatch '^[0-9a-f]{64}$'){throw 'Durable task is not an OCR waiting_input projection with a target token.'}
  $results=@(Get-ChildItem -LiteralPath $job.FullName -Recurse -Filter result.json|ForEach-Object{$value=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json -Depth 100;if($null -ne $value.worker.id){[ordered]@{path=$_.FullName;sha256=Sha $_.FullName;worker=$value.worker;status=$value.status;stageId=$value.stageId;attemptId=$value.attemptId}}})
  $image=@($results|Where-Object {$_.worker.id -eq 'image.ocr.mineru'});$content=@($results|Where-Object {$_.worker.id -eq 'content.deepseek'});$anki=@($results|Where-Object {$_.worker.id -eq 'export.anki'})
  if($image.Count -ne 1 -or $content.Count -ne 1 -or $anki.Count -ne 0){throw "Unexpected durable inventory image=$($image.Count) content=$($content.Count) anki=$($anki.Count)"}
  $manifest=Join-Path $job.FullName 'task.manifest.v2.json'
  JsonOnce $receipt ([ordered]@{schemaVersion='sealed-live-ocr-anki-v1233-initial-recovery.v1';status='waiting_input';recoveredFromDurableState=$true;authority=[ordered]@{version='1.2.33';registry=Evidence $registry;checkpoint=Evidence $checkpoint;releaseRoot=$release;orchestrator=Evidence $exe};process=[ordered]@{pid=$process.Id;namedPipe=$script:pipeName;actualOrchestrator=$true;workerLaunchOwner='KnowledgeClipper.Orchestrator.exe';workerRunInvocationsDuringRecovery=0};task=[ordered]@{taskId=$taskId;jobRoot=$job.FullName;manifest=Evidence $manifest};projection=$projection;workers=$results;sideEffects=[ordered]@{mineruSubmissions=$image.Count;ankiWrites=0;remnoteWrites=0;speakerInvocations=0};next='submit_correction through task.provide_input using the durable OCR target token; no MinerU rerun'})
  [ordered]@{status='waiting_input';evidenceRoot=$evidence;taskId=$taskId;targetToken=$projection.decisionTargetToken;receipt=$receipt;receiptSha256=Sha $receipt}|ConvertTo-Json -Depth 20
} finally { if($null -ne $process -and -not $process.HasExited){$process.Kill($true)|Out-Null;$process.WaitForExit(15000)|Out-Null}; if($null -ne $stdout){WriteOnce (Join-Path $evidence 'orchestrator.recovery.stdout.log') $utf8.GetBytes($stdout.GetAwaiter().GetResult())};if($null -ne $stderr){WriteOnce (Join-Path $evidence 'orchestrator.recovery.stderr.log') $utf8.GetBytes($stderr.GetAwaiter().GetResult())} }

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$TaskId
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$project = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$jobRoot = Join-Path $evidence (Join-Path 'jobs' $TaskId)
$replayRoot = Join-Path $evidence 'same-submit-replay-v2'
$authority = Join-Path $project 'contracts/authority/worker-registry/v1.2.28-20260901T022000Z'
$release = Join-Path $project 'releases/knowledge-clipper-authority-v1.2.28-20260901T022000Z/win-x64'
$registry = Join-Path $authority 'worker-registry.v1.json'
$orchestrator = Join-Path $release 'orchestrator/KnowledgeClipper.Orchestrator.exe'
$ipcSource = Join-Path $release 'contracts/core/v2.3/playback-control/orchestrator-ipc.v1.1.json'
$adapter = Join-Path $release 'contracts/core/v2.1/profile-resolver.v2.1.json'

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function WriteOnce([string]$Path,[byte[]]$Bytes) { if (Test-Path -LiteralPath $Path) { throw "Append-only target exists: $Path" }; [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null; [IO.File]::WriteAllBytes($Path,$Bytes) }
function JsonOnce([string]$Path,$Value) { WriteOnce $Path $utf8.GetBytes((($Value | ConvertTo-Json -Depth 100) + "`n")) }
function CountWorkerResults() { @(Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter result.json | ForEach-Object { try { $v=Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json -Depth 100; if($null -ne $v.worker.id){$_.FullName} } catch {} }).Count }
function Anki([string]$Action,$Params=@{}) { $body=([ordered]@{action=$Action;version=6;params=$Params}|ConvertTo-Json -Depth 20 -Compress);$response=Invoke-RestMethod -Uri 'http://127.0.0.1:8765' -Method Post -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 15;if($null -ne $response.error){throw "AnkiConnect $Action failed: $($response.error)"};$response.result }

foreach($path in @($jobRoot,$authority,$release,$registry,$orchestrator,$ipcSource,$adapter)) { if(-not(Test-Path -LiteralPath $path)){throw "Required input missing: $path"} }
if((Sha $registry) -ne 'F7989A49A2F348031EFEA488FA7DE8885894494F8773E71A241C5698A7A66CB4'){throw 'Authority registry identity drift.'}
if(Test-Path -LiteralPath $replayRoot){throw "Append-only replay root exists: $replayRoot"};New-Item -ItemType Directory -Path $replayRoot,(Join-Path $replayRoot 'ipc') | Out-Null

$originalRequestPath=Join-Path $evidence 'ipc/0002-task-command.request.json'
$originalRequest=Get-Content -Raw -LiteralPath $originalRequestPath | ConvertFrom-Json -Depth 100
$command=$originalRequest.payload
if($command.type -ne 'task.submit' -or $command.commandId -ne 'command-e81944a534c64808a19c204ee946ac65' -or $command.idempotencyKey -ne 'e4c99edb71df8592f9d16aa13612f8a266b319f9010bc992962f30738cc41a4a'){throw 'Original submit command identity drift.'}
$items=Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter items.ndjson | Where-Object FullName -match '\\stages\\anki-export\\attempts\\' | Select-Object -First 1
$initialItems=@(Get-Content -LiteralPath $items.FullName | Where-Object {$_.Trim()} | ForEach-Object {$_|ConvertFrom-Json})
if($initialItems.Count -ne 1){throw 'Expected exactly one existing Anki item.'};$noteId=[int64]$initialItems[0].note_id
$before=[ordered]@{workerResults=CountWorkerResults;ankiItems=$initialItems.Count;commandEvents=@(Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter '*command*.json').Count}

$script:pipeName='KnowledgeClipper.AuthorityV1228.Replay.'+[Guid]::NewGuid().ToString('N');$script:exchange=0
$config=Get-Content -Raw -LiteralPath $ipcSource|ConvertFrom-Json -Depth 100;$config.pipeName=$script:pipeName;$config.endpoint="\\.\pipe\$($script:pipeName)";$runtimeIpc=Join-Path $replayRoot 'orchestrator-ipc.runtime.v1.1.json';JsonOnce $runtimeIpc $config
function Pipe($Payload,[string]$Type){$correlation='v1228-replay-'+[Guid]::NewGuid().ToString('N');$request=[ordered]@{protocolVersion='1.0';correlationId=$correlation;messageType=$Type;payload=$Payload};$json=$request|ConvertTo-Json -Depth 100 -Compress;$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut);try{$pipe.Connect(10000);$bytes=$utf8.GetBytes($json);$pipe.Write($bytes,0,$bytes.Length);$pipe.WriteByte(10);$pipe.Flush();$buffer=[IO.MemoryStream]::new();while($true){$byte=$pipe.ReadByte();if($byte -lt 0){throw 'Pipe closed before response.'};if($byte -eq 10){break};$buffer.WriteByte([byte]$byte);if($buffer.Length -gt 1048576){throw 'Pipe response exceeds 1MiB'}};$text=$utf8.GetString($buffer.ToArray()).TrimEnd("`r");$response=$text|ConvertFrom-Json -Depth 100;$n='{0:D4}-{1}' -f (++$script:exchange),$Type;JsonOnce (Join-Path $replayRoot "ipc/$n.request.json") $request;WriteOnce (Join-Path $replayRoot "ipc/$n.response.json") $utf8.GetBytes($text+"`n");if($response.correlationId -ne $correlation -or -not $response.ok){throw "Named Pipe rejected: $($response.error.code) $($response.error.message)"};$response}finally{$pipe.Dispose()}}

$process=$null;$stdoutTask=$null;$stderrTask=$null;$failure=$null
try{
  $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$orchestrator;$start.WorkingDirectory=[IO.Path]::GetDirectoryName($orchestrator);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;foreach($arg in @('serve','--ipc-config',$runtimeIpc,'--jobs-root',(Join-Path $evidence 'jobs'),'--registry',$registry,'--release-root',$release,'--max-active-tasks','1','--adapter-config',$adapter)){$start.ArgumentList.Add($arg)};$process=[Diagnostics.Process]::new();$process.StartInfo=$start;if(-not $process.Start()){throw 'Could not start sealed Orchestrator.'};$stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
  $ready=$false;for($i=0;$i -lt 120 -and -not $ready;$i++){if($process.HasExited){throw "Orchestrator exited during startup: $($process.ExitCode)"};try{$null=Pipe ([ordered]@{schemaVersion='2.3.0';queryId=('ready-'+[Guid]::NewGuid().ToString('N'));type='task.query_recent';limit=1}) 'task-query';$ready=$true}catch{Start-Sleep -Milliseconds 100}};if(-not $ready){throw 'Named Pipe did not become ready.'}
  $replayReceipt=Pipe $command 'task-command'
  $projection=(Pipe ([ordered]@{schemaVersion='2.3.0';queryId=('query-'+[Guid]::NewGuid().ToString('N'));type='task.get';taskId=$TaskId}) 'task-query').payload.items[0]
  $notesInfo=Anki 'notesInfo' @{notes=@($noteId)};$cardIds=@($notesInfo[0].cards);$cardsInfo=Anki 'cardsInfo' @{cards=$cardIds}
  $after=[ordered]@{workerResults=CountWorkerResults;ankiItems=@(Get-Content -LiteralPath $items.FullName | Where-Object {$_.Trim()}).Count;commandEvents=@(Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter '*command*.json').Count}
  if($after.workerResults -ne $before.workerResults -or $after.ankiItems -ne $before.ankiItems -or $after.commandEvents -ne $before.commandEvents){throw 'Idempotent submit replay created new task effects or command events.'}
  JsonOnce (Join-Path $replayRoot 'replay-receipt.json') ([ordered]@{schemaVersion='sealed-live-ocr-anki-v1228-same-submit-replay.v1';status='passed';authority=[ordered]@{version='1.2.28';registrySha256=Sha $registry;orchestratorSha256=Sha $orchestrator};originalCommand=[ordered]@{path=$originalRequestPath;sha256=Sha $originalRequestPath;commandId=$command.commandId;operationId=$command.operationId;idempotencyKey=$command.idempotencyKey};process=[ordered]@{pid=$process.Id;namedPipe=$script:pipeName;actualOrchestrator=$true};replayReceipt=$replayReceipt.payload;projection=$projection;effectCounts=[ordered]@{before=$before;after=$after;newWorkerAttempts=0;newAnkiNotes=0};independentAnkiReadback=[ordered]@{noteId=$noteId;notesInfo=$notesInfo;cardsInfo=$cardsInfo;actions=@('notesInfo','cardsInfo')};remnoteWrites=0;speakerInvocations=0})
}catch{$failure=$_;JsonOnce (Join-Path $replayRoot 'failure-receipt.json') ([ordered]@{schemaVersion='sealed-live-ocr-anki-v1228-same-submit-replay-failure.v1';status='failed';error=$_.Exception.Message;externalState='no automatic retry'})}finally{if($null -ne $process -and -not $process.HasExited){$process.Kill($true)|Out-Null;$process.WaitForExit(15000)|Out-Null};if($null -ne $stdoutTask){WriteOnce (Join-Path $replayRoot 'orchestrator.stdout.log') $utf8.GetBytes($stdoutTask.GetAwaiter().GetResult())};if($null -ne $stderrTask){WriteOnce (Join-Path $replayRoot 'orchestrator.stderr.log') $utf8.GetBytes($stderrTask.GetAwaiter().GetResult())}}
if($null -ne $failure){throw $failure}
Write-Output (Join-Path $replayRoot 'replay-receipt.json')
Write-Output (Sha (Join-Path $replayRoot 'replay-receipt.json'))

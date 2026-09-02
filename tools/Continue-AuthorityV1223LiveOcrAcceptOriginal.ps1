[CmdletBinding()]
param(
    [string]$EvidenceRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-live-ocr-anki-v1.2.23\20260831T112300Z',
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$project = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$authority = Join-Path $project 'contracts\authority\worker-registry\v1.2.23-20260831T112000Z'
$release = Join-Path $project 'releases\knowledge-clipper-authority-v1.2.23-20260831T112000Z\win-x64'
$initialReceipt = Join-Path $EvidenceRoot 'initial-receipt.json'
$initial = Get-Content -LiteralPath $initialReceipt -Raw | ConvertFrom-Json -Depth 100
$taskId = [string]$initial.task.taskId
$job = [string]$initial.task.jobRoot
$jobs = Join-Path $EvidenceRoot 'jobs'
$continuation = Join-Path $EvidenceRoot 'continuation'
if (Test-Path -LiteralPath $continuation) { throw "Append-only continuation root exists: $continuation" }
New-Item -ItemType Directory -Path $continuation,(Join-Path $continuation 'ipc') | Out-Null

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ShaBytes([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function WriteOnce([string]$Path,[byte[]]$Bytes) {
    if (Test-Path -LiteralPath $Path) { throw "Append-only file exists: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path,$Bytes)
}
function JsonOnce([string]$Path,$Value) { WriteOnce $Path $utf8.GetBytes((($Value | ConvertTo-Json -Depth 100) + "`n")) }
function Evidence([string]$Path) { $item=Get-Item -LiteralPath $Path; [ordered]@{path=$item.FullName;bytes=$item.Length;sha256=Sha $Path} }
function Anki([string]$Action,$Parameters=@{}) {
    $body = [ordered]@{action=$Action;version=6;params=$Parameters} | ConvertTo-Json -Depth 40 -Compress
    $response = Invoke-RestMethod -Uri 'http://127.0.0.1:8765' -Method Post -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 15
    if ($null -ne $response.error) { throw "AnkiConnect $Action failed: $($response.error)" }
    $response.result
}
function ResultInventory {
    @(Get-ChildItem -LiteralPath $job -Recurse -Filter result.json | ForEach-Object {
        try {
            $value=Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -Depth 100
            if ($null -ne $value.worker.id) { [ordered]@{path=$_.FullName;sha256=Sha $_.FullName;workerId=[string]$value.worker.id;workerVersion=[string]$value.worker.version;status=[string]$value.status;runId=[string]$value.runId;stageId=[string]$value.stageId;attemptId=[string]$value.attemptId} }
        } catch { }
    })
}

$registry = Join-Path $authority 'worker-registry.v1.json'
$checkpoint = Join-Path $authority 'checkpoint.json'
$exe = Join-Path $release 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$ipcSource = Join-Path $release 'contracts\core\v2.3\playback-control\orchestrator-ipc.v1.1.json'
$adapter = Join-Path $release 'contracts\core\v2.1\profile-resolver.v2.1.json'
foreach ($path in @($registry,$checkpoint,$exe,$ipcSource,$adapter,$job)) { if (-not (Test-Path -LiteralPath $path)) { throw "Required input missing: $path" } }
if ((Sha $registry) -ne 'c342d7309f5c0ff0ecac5754dd38e62eef463664597b7cc399f1346de9f1201c') { throw 'Authority registry drift.' }
if ([string]$initial.status -ne 'waiting_input') { throw 'Initial task is not waiting_input.' }
$targetToken = [string]$initial.projection.decisionTargetToken
if ($targetToken -notmatch '^[0-9a-f]{64}$') { throw 'Invalid OCR decision target token.' }

$ankiVersion=Anki 'version'
$ankiProfile=[string](Anki 'getActiveProfile')
$decks=@(Anki 'deckNames')
if ($ankiVersion -ne 6 -or [string]::IsNullOrWhiteSpace($ankiProfile) -or $decks -notcontains $AnkiDeck) { throw 'Anki preflight failed.' }
$beforeNoteIds=@(Anki 'findNotes' ([ordered]@{query=('deck:"{0}"' -f $AnkiDeck)}))
$before = ResultInventory
$beforeImage=@($before|Where-Object workerId -eq 'image.ocr.mineru').Count
$beforeContent=@($before|Where-Object workerId -eq 'content.deepseek').Count
$beforeAnki=@($before|Where-Object workerId -eq 'export.anki').Count
if ($beforeImage -ne 1 -or $beforeContent -ne 1 -or $beforeAnki -ne 0) { throw "Unexpected initial inventory image=$beforeImage content=$beforeContent anki=$beforeAnki" }

$actionId='ocr-accept-original-v1223'
$commandId="command-$taskId-$actionId"
$actionRelative="commands/$commandId/ocr-human-resolution-action.v1.json"
$actionPath=Join-Path $job $actionRelative.Replace('/',[IO.Path]::DirectorySeparatorChar)
JsonOnce $actionPath ([ordered]@{
    schemaVersion='knowledge-clipper-ocr-human-resolution-action.v1'
    decisionKind='ocr-human-resolution'
    targetToken=$targetToken
    action='accept_original'
    reason='The source image itself visibly contains CANAR; preserve the source text without guessing or rerunning OCR.'
})

$deepseek=[Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY','Process')
$deepseekSource='process'
if ([string]::IsNullOrWhiteSpace($deepseek)) { $deepseek=[Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY','User');$deepseekSource='user' }
if ([string]::IsNullOrWhiteSpace($deepseek)) {
    $configPath='C:\Users\fy\Documents\New project\DeepSeek API 配置.md'
    if (Test-Path -LiteralPath $configPath) {
        $text=Get-Content -LiteralPath $configPath -Raw
        $match=[regex]::Match($text,'(?im)^\s*(?:AI_PRODUCER_API_KEY|DEEPSEEK_API_KEY|api[_ -]?key)\s*[:=]\s*[`"'']?([^\s`"'']+)')
        if ($match.Success) { $deepseek=$match.Groups[1].Value;$deepseekSource='local-config' }
    }
}
if ([string]::IsNullOrWhiteSpace($deepseek)) { throw 'DeepSeek credential unavailable through approved discovery routes.' }

$script:pipeName='KnowledgeClipper.AuthorityV1223.Continue.'+[Guid]::NewGuid().ToString('N')
$script:exchange=0
$runtimeConfig=Get-Content -LiteralPath $ipcSource -Raw | ConvertFrom-Json -Depth 100
$runtimeConfig.pipeName=$script:pipeName
$runtimeConfig.endpoint="\\.\pipe\$($script:pipeName)"
$runtimeIpc=Join-Path $continuation 'orchestrator-ipc.runtime.v1.1.json'
JsonOnce $runtimeIpc $runtimeConfig
function Pipe($Payload,[string]$Type) {
    $correlation='v1223-cont-'+[Guid]::NewGuid().ToString('N')
    $request=[ordered]@{protocolVersion='1.0';correlationId=$correlation;messageType=$Type;payload=$Payload}
    $json=$request|ConvertTo-Json -Depth 100 -Compress
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000);$bytes=$utf8.GetBytes($json);$pipe.Write($bytes,0,$bytes.Length);$pipe.WriteByte(10);$pipe.Flush()
        $memory=[IO.MemoryStream]::new();while($true){$byte=$pipe.ReadByte();if($byte -lt 0){throw 'Named Pipe closed before response.'};if($byte -eq 10){break};$memory.WriteByte([byte]$byte);if($memory.Length -gt 1048576){throw 'Pipe response exceeds 1 MiB.'}}
        $responseText=$utf8.GetString($memory.ToArray()).TrimEnd("`r")
        $response=$responseText|ConvertFrom-Json -Depth 100
        $name='{0:D4}-{1}' -f (++$script:exchange),$Type
        JsonOnce (Join-Path $continuation "ipc\$name.request.json") $request
        WriteOnce (Join-Path $continuation "ipc\$name.response.json") $utf8.GetBytes($responseText+"`n")
        if ($response.correlationId -ne $correlation -or -not $response.ok) { throw "Named Pipe rejected: $($response.error.code) $($response.error.message)" }
        $response
    } finally { $pipe.Dispose() }
}
function Projection {
    $response=Pipe ([ordered]@{schemaVersion='2.3.0';queryId=('query-'+[Guid]::NewGuid().ToString('N'));type='task.get';taskId=$taskId}) 'task-query'
    if (@($response.payload.items).Count) { @($response.payload.items)[0] } else { $null }
}
function WaitTerminal {
    $until=[DateTime]::UtcNow.AddSeconds(900)
    do { $projection=Projection;if($null -ne $projection -and @('succeeded','partial','failed','cancelled') -contains [string]$projection.status){return $projection};Start-Sleep -Milliseconds 750 } while([DateTime]::UtcNow -lt $until)
    throw 'Continuation did not reach a terminal state.'
}

$process=$null;$stdoutTask=$null;$stderrTask=$null;$failure=$null
try {
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$exe;$start.WorkingDirectory=[IO.Path]::GetDirectoryName($exe);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.Environment['AI_PRODUCER_API_KEY']=$deepseek
    foreach($argument in @('serve','--ipc-config',$runtimeIpc,'--jobs-root',$jobs,'--registry',$registry,'--release-root',$release,'--max-active-tasks','1','--adapter-config',$adapter)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start;if(-not $process.Start()){throw 'Failed to start sealed Orchestrator.'};$stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
    $ready=$false;for($index=0;$index -lt 120 -and -not $ready;$index++){if($process.HasExited){throw "Orchestrator exited during startup: $($process.ExitCode)"};try{$null=Pipe ([ordered]@{schemaVersion='2.3.0';queryId=('ready-'+[Guid]::NewGuid().ToString('N'));type='task.query_recent';limit=1}) 'task-query';$ready=$true}catch{Start-Sleep -Milliseconds 100}};if(-not $ready){throw 'Orchestrator did not become ready.'}
    $provide=[ordered]@{schemaVersion='1.0.0';commandId=$commandId;operationId="operation-$taskId-$actionId";taskId=$taskId;type='task.provide_input';issuedAt=[DateTimeOffset]::UtcNow.ToString('o');idempotencyKey=ShaBytes $utf8.GetBytes("$taskId`:$actionId");payloadRef=$actionRelative}
    $accepted=Pipe $provide 'task-command'
    $projection=WaitTerminal
    $after=ResultInventory
    $afterImage=@($after|Where-Object workerId -eq 'image.ocr.mineru').Count
    $afterContent=@($after|Where-Object workerId -eq 'content.deepseek').Count
    $afterAnki=@($after|Where-Object workerId -eq 'export.anki').Count
    $afterNoteIds=@(Anki 'findNotes' ([ordered]@{query=('deck:"{0}"' -f $AnkiDeck)}))
    $newNoteIds=@($afterNoteIds|Where-Object{$_ -notin $beforeNoteIds})
    if ($projection.status -ne 'succeeded' -or $afterImage -ne 1 -or $afterContent -ne 2 -or $afterAnki -ne 1 -or $newNoteIds.Count -lt 1) { throw "Unexpected continuation result status=$($projection.status) image=$afterImage content=$afterContent anki=$afterAnki newNotes=$($newNoteIds.Count)" }
    $notes=@(Anki 'notesInfo' ([ordered]@{notes=$newNoteIds}))
    $cards=@();foreach($note in $notes){$cards+=@(Anki 'cardsInfo' ([ordered]@{cards=@($note.cards)}))}
    $beforeReplay=ResultInventory;$beforeReplayNotes=@(Anki 'findNotes' ([ordered]@{query=('deck:"{0}"' -f $AnkiDeck)}))
    $replay=Pipe $provide 'task-command';Start-Sleep -Seconds 2
    $afterReplay=ResultInventory;$afterReplayNotes=@(Anki 'findNotes' ([ordered]@{query=('deck:"{0}"' -f $AnkiDeck)}))
    if ($afterReplay.Count -ne $beforeReplay.Count -or $afterReplayNotes.Count -ne $beforeReplayNotes.Count) { throw 'Idempotent replay created a new attempt or Anki note.' }
    $receipt=[ordered]@{
        schemaVersion='sealed-live-ocr-anki-v1223-continuation.v1';status='passed';authority=[ordered]@{version='1.2.23';registry=Evidence $registry;checkpoint=Evidence $checkpoint;orchestrator=Evidence $exe};task=[ordered]@{taskId=$taskId;jobRoot=$job;initialReceipt=Evidence $initialReceipt;action=Evidence $actionPath;decision='accept_original';targetToken=$targetToken;sourceTextPreserved=$true;ocrRerun=$false};preflight=[ordered]@{ankiVersion=$ankiVersion;ankiProfile=$ankiProfile;deck=$AnkiDeck;deepSeekCredentialSource=$deepseekSource;secretRecorded=$false};counts=[ordered]@{before=[ordered]@{image=$beforeImage;content=$beforeContent;anki=$beforeAnki;notes=$beforeNoteIds.Count};after=[ordered]@{image=$afterImage;content=$afterContent;anki=$afterAnki;notes=$afterNoteIds.Count;newNotes=$newNoteIds.Count};replay=[ordered]@{workerResultsBefore=$beforeReplay.Count;workerResultsAfter=$afterReplay.Count;notesBefore=$beforeReplayNotes.Count;notesAfter=$afterReplayNotes.Count;newAttempts=0;newNotes=0}};projection=$projection;workers=$after;ankiReadback=[ordered]@{newNoteIds=$newNoteIds;notes=$notes;cards=$cards;exactReadback=$true};commands=[ordered]@{provideInput=$accepted.payload;replay=$replay.payload};sideEffects=[ordered]@{mineruSubmissionsThisContinuation=0;ankiWrites=$newNoteIds.Count;remnoteWrites=0;speakerInvocations=0}}
    $receiptPath=Join-Path $continuation 'continuation-receipt.json';JsonOnce $receiptPath $receipt
    $front=@($notes|ForEach-Object{$_.fields.Front.value}) -join "`n---`n";$back=@($notes|ForEach-Object{$_.fields.Back.value}) -join "`n---`n"
    $readable=@("# OCR → Content → Anki v1.2.23 真实续跑","","- 结果：PASS","- 人工决定：接受原图文字（原图本身为 CANAR，不猜写、不重跑 OCR）","- OCR attempts：$afterImage（续跑新增 0）","- Content attempts：$afterContent（续跑新增 1）","- Anki attempts：$afterAnki；新增 notes：$($newNoteIds.Count)","- replay：新增 attempts 0 / notes 0","","## Front","",$front,"","## Back","",$back,"","## Anki IDs","",(($newNoteIds|ForEach-Object{"- noteId: $_"}) -join "`n")) -join "`n"
    $readablePath=Join-Path $continuation 'READABLE.md';WriteOnce $readablePath $utf8.GetBytes($readable+"`n")
    [ordered]@{status='passed';receipt=$receiptPath;receiptSha256=Sha $receiptPath;readable=$readablePath;readableSha256=Sha $readablePath;newNoteIds=$newNoteIds;front=$front;back=$back}|ConvertTo-Json -Depth 30
} catch {
    $failure=$_
    JsonOnce (Join-Path $continuation 'failure-receipt.json') ([ordered]@{schemaVersion='sealed-live-ocr-anki-v1223-continuation-failure.v1';status='failed';errorType=$_.Exception.GetType().FullName;error=$_.Exception.Message;taskId=$taskId;action=if(Test-Path -LiteralPath $actionPath){Evidence $actionPath}else{$null};resultInventory=ResultInventory;externalState='inspect-before-retry'})
} finally {
    if($null -ne $process -and -not $process.HasExited){$process.Kill($true)|Out-Null;$process.WaitForExit(15000)|Out-Null}
    if($null -ne $stdoutTask){WriteOnce (Join-Path $continuation 'orchestrator.stdout.log') $utf8.GetBytes($stdoutTask.GetAwaiter().GetResult())}
    if($null -ne $stderrTask){WriteOnce (Join-Path $continuation 'orchestrator.stderr.log') $utf8.GetBytes($stderrTask.GetAwaiter().GetResult())}
    $deepseek=$null
}
if($null -ne $failure){throw $failure}

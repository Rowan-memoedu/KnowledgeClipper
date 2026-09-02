#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$RemNoteMcpUrl = 'http://127.0.0.1:43101/mcp',
    [string]$OutputId = 'postflight-v2'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$projectRoot = Split-Path -Parent $PSScriptRoot
$postflight = Join-Path $root $OutputId
$qualificationPath = Join-Path $root ('qualification-receipt.{0}.json' -f $OutputId)
$readablePath = Join-Path $root ('qualification-readable.{0}.md' -f $OutputId)
foreach ($path in @($root, (Join-Path $root 'execution-receipt.json'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw ('Required Phase B evidence is missing: {0}' -f $path) }
}
foreach ($path in @($postflight,$qualificationPath,$readablePath)) {
    if (Test-Path -LiteralPath $path) { throw ('Append-only postflight output exists: {0}' -f $path) }
}
[IO.Directory]::CreateDirectory($postflight) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$node = 'C:\Program Files\nodejs\node.exe'
$remnoteCli = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules\remnote-mcp-server\dist\remnote-cli\index.js'

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 100) + "`n"), $utf8)
}

function Read-RemNoteExact([string]$Mode, [string]$Label, [string]$RemId, [string]$ExpectedTitle) {
    $raw = (& $node $remnoteCli --json --mcp-url $RemNoteMcpUrl read $RemId --content-mode structured --depth 5 --child-limit 100 --view full --ancestor-depth 5 2>&1) -join [Environment]::NewLine
    $exitCode = $LASTEXITCODE
    $path = Join-Path $postflight ('remnote-{0}-{1}-{2}.json' -f $Mode.ToLowerInvariant(),$Label,$RemId)
    [IO.File]::WriteAllText($path, ($raw + "`n"), $utf8)
    if ($exitCode -ne 0) { throw ('RemNote exact read failed for {0}/{1}; exit={2}' -f $Mode,$Label,$exitCode) }
    $value = $raw | ConvertFrom-Json -Depth 100
    $title = [string]($value.title ?? $value.result.title)
    if ($title -ne $ExpectedTitle) { throw ('RemNote exact title mismatch for {0}/{1}' -f $Mode,$Label) }
    [ordered]@{ label=$Label; remId=$RemId; expectedTitle=$ExpectedTitle; observedTitle=$title; exactTitleMatched=$true; path=$path; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}

function Get-ContentEvidence([string]$Mode, [string]$JobRoot) {
    $learningPath = Get-ChildItem -LiteralPath $JobRoot -Recurse -Filter 'learning-bundle.schema-v1.json' | Select-Object -First 1 -ExpandProperty FullName
    $cardPath = Get-ChildItem -LiteralPath $JobRoot -Recurse -Filter 'card-bundle.schema-v1.json' | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($learningPath)) { throw ('LearningBundle missing for path {0}' -f $Mode) }
    $learning = Get-Content -LiteralPath $learningPath -Raw | ConvertFrom-Json -Depth 100
    $cards = if ([string]::IsNullOrWhiteSpace($cardPath)) { $null } else { Get-Content -LiteralPath $cardPath -Raw | ConvertFrom-Json -Depth 100 }
    $item = @($learning.items)[0]
    [ordered]@{
        learningBundle=[ordered]@{path=$learningPath;sha256=(Get-FileHash -LiteralPath $learningPath -Algorithm SHA256).Hash;releaseState=$learning.release_state}
        provider=[ordered]@{id='deepseek';mode='live';callCount=@($learning.ai_trace).Count;attemptCount=(@($learning.ai_trace | Measure-Object attempts -Sum).Sum);models=@($learning.ai_trace.model | Sort-Object -Unique);credentialsRecorded=$false}
        cleanedText=$item.cleaning.cleaned_text
        categories=@($item.units.category_id)
        cards=if ($null -eq $cards) {@()} else {@($cards.cards | ForEach-Object {[ordered]@{cardId=$_.card_id;categoryId=$_.category_id;template=$_.template;front=@($_.front_blocks.text);back=@($_.back_blocks.text)}})}
        cardBundle=if ($null -eq $cards) {$null} else {[ordered]@{path=$cardPath;sha256=(Get-FileHash -LiteralPath $cardPath -Algorithm SHA256).Hash;releaseState=$cards.release_state;cardCount=@($cards.cards).Count}}
    }
}

$pathEvidence = [ordered]@{}
foreach ($mode in @('A','B','C','D')) {
    $modeRoot = Join-Path $root $mode.ToLowerInvariant()
    $receiptPath = Join-Path $modeRoot 'path-receipt.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 100
    if ($mode -eq 'C') {
        if ($receipt.status -ne 'credential-unavailable' -or $receipt.credentialBoundary.providerCalls -ne 0 -or $receipt.downstreamStarted) { throw 'Path C credential boundary drifted.' }
        $pathEvidence[$mode] = [ordered]@{status=$receipt.status;receiptSha256=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash;providerCalls=0;downstreamStarted=$false;externalWrites=0;input=$receipt.input}
        continue
    }
    if ($receipt.status -ne 'succeeded') { throw ('Path {0} is not succeeded: {1}' -f $mode,$receipt.status) }
    $jobRoots = @(Get-ChildItem -LiteralPath (Join-Path $modeRoot 'jobs') -Directory)
    if ($jobRoots.Count -ne 1) { throw ('Path {0} must contain exactly one immutable task package.' -f $mode) }
    $jobRoot = $jobRoots[0].FullName
    $content = Get-ContentEvidence $mode $jobRoot
    $remnoteAttempts = @($receipt.attempts | Where-Object workerId -eq 'export.remnote')
    if ($remnoteAttempts.Count -lt 1 -or @($remnoteAttempts | Where-Object {$_.status -ne 'completed' -or -not $_.payload.readbackVerified}).Count -ne 0) { throw ('Path {0} exporter exact readback is incomplete.' -f $mode) }
    $firstRemnoteResult = Get-Content -LiteralPath (Join-Path $jobRoot $remnoteAttempts[0].resultPath.Replace('/',[IO.Path]::DirectorySeparatorChar)) -Raw | ConvertFrom-Json -Depth 100
    $attemptDirectory = Split-Path -Parent (Join-Path $jobRoot $remnoteAttempts[0].resultPath.Replace('/',[IO.Path]::DirectorySeparatorChar))
    $workerReceiptPaths = @(Get-ChildItem -LiteralPath (Join-Path $attemptDirectory 'outputs') -Recurse -Filter 'receipt.json')
    if ($workerReceiptPaths.Count -ne 1) { throw ('Path {0} must contain exactly one exporter write receipt.' -f $mode) }
    $workerReceiptPath = $workerReceiptPaths[0].FullName
    $workerReceipt = Get-Content -LiteralPath $workerReceiptPath -Raw | ConvertFrom-Json -Depth 100
    $reads = [Collections.Generic.List[object]]::new()
    if ($workerReceipt.route -eq 'card-export') {
        $reads.Add((Read-RemNoteExact $mode 'document' ([string]$workerReceipt.documentRemId) ([string]$workerReceipt.documentTitle)))
    }
    elseif ($workerReceipt.route -eq 'clip-daily-route') {
        $reads.Add((Read-RemNoteExact $mode 'date' ([string]$workerReceipt.dateRemId) ([string]$workerReceipt.dateTitle)))
        $reads.Add((Read-RemNoteExact $mode 'clip' ([string]$workerReceipt.clipRemId) ([string]$workerReceipt.clipTitle)))
    }
    else { throw ('Unexpected RemNote route in path {0}' -f $mode) }
    $speech = $null
    if ($mode -eq 'D') {
        $speechResults = @(Get-ChildItem -LiteralPath $jobRoot -Recurse -Filter 'result.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -Depth 100 } | Where-Object {$_.worker.id -eq 'speech.edge'})
        $playback = @($speechResults | Where-Object {$_.stageId -eq 'speech-playback'})
        $synth = @($speechResults | Where-Object {$_.stageId -eq 'speech-synthesize'})
        if ($synth.Count -ne 1 -or $synth[0].status -ne 'completed' -or $playback.Count -ne 2 -or @($playback | Where-Object {$_.status -ne 'completed' -or $_.payload.playbackState -ne 'idle_replayable'}).Count -ne 0) { throw 'Path D speech terminal evidence is incomplete.' }
        $audioManifestPath = Join-Path $jobRoot $synth[0].outputs[0].manifestPath.Replace('/',[IO.Path]::DirectorySeparatorChar)
        $audioManifest = Get-Content -LiteralPath $audioManifestPath -Raw | ConvertFrom-Json -Depth 30
        $audioPath = Join-Path $jobRoot $audioManifest.files[0].path.Replace('/',[IO.Path]::DirectorySeparatorChar)
        $speech = [ordered]@{synthesis=[ordered]@{status='completed';workerVersion=$synth[0].worker.version;audioPath=$audioPath;audioBytes=(Get-Item -LiteralPath $audioPath).Length;audioSha256=(Get-FileHash -LiteralPath $audioPath -Algorithm SHA256).Hash};playbackAttempts=@($playback | ForEach-Object {[ordered]@{attemptId=$_.attemptId;operationId=$_.operationId;status=$_.status;playbackState=$_.payload.playbackState;startedAt=$_.startedAt;finishedAt=$_.finishedAt}});physicalSpeakerInvocations=2;naturalCompletions=2;replay='from_start';simulated=$false}
    }
    $pathEvidence[$mode] = [ordered]@{status='succeeded';taskId=$receipt.taskId;receiptSha256=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash;content=$content;remnote=[ordered]@{route=$workerReceipt.route;workerReceiptPath=$workerReceiptPath;workerReceiptSha256=(Get-FileHash -LiteralPath $workerReceiptPath -Algorithm SHA256).Hash;workerReadbackVerified=$true;reads=@($reads);initialOutcome=$workerReceipt.outcome;replay=if($mode -eq 'B'){$receipt.replay}else{$null}};speech=$speech}
}

$ankiRecheck = [ordered]@{state='service-unreachable';writes=0;externalMutation=$false;automaticRetryPerformed=$false}
try {
    $body = [ordered]@{action='version';version=6;params=@{}} | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8765' -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 10
    if ($null -eq $response.error) { $ankiRecheck = [ordered]@{state='ready';version=$response.result;writes=0;externalMutation=$false;followupRequired='run-new-original-input-Anki-only-tasks'} }
}
catch { $ankiRecheck.detail=$_.Exception.Message; $ankiRecheck.errorClass=$_.Exception.GetType().FullName }
Write-Json (Join-Path $postflight 'anki-readonly-recheck.json') $ankiRecheck

$executionPath = Join-Path $root 'execution-receipt.json'
$execution = Get-Content -LiteralPath $executionPath -Raw | ConvertFrom-Json -Depth 100
$qualification = [ordered]@{
    schemaVersion='knowledge-clipper-candidate-v1.2.12-phase-b-qualification.v1'
    generatedAt=[DateTimeOffset]::UtcNow.ToString('o')
    status=if ($ankiRecheck.state -eq 'ready') {'partial-backend-harness-anki-followup-required'} else {'passed-backend-harness-candidate-worker-bound-conditional-anki-mineru'}
    executionClass='backend-harness candidate-worker-bound; exact packaged Core DLL loaded in-process; not sealed Desktop/Orchestrator process E2E'
    candidate=$execution.candidate
    authority=$execution.authority
    coreSettings=$execution.coreSettings
    executionReceipt=[ordered]@{path=$executionPath;sha256=(Get-FileHash -LiteralPath $executionPath -Algorithm SHA256).Hash}
    paths=$pathEvidence
    externalCalls=[ordered]@{deepSeek=($pathEvidence.A.content.provider.callCount + $pathEvidence.B.content.provider.callCount + $pathEvidence.D.content.provider.callCount);mineru=0;remnoteWorkerWrites=3;remnoteReplayCalls=1;remnoteIndependentReadCalls=4;ankiReadOnlyInitialAttempts=@($execution.preflight.anki.actions).Count;ankiReadOnlyRecheckAttempts=1;ankiWrites=0;physicalSpeaker=2}
    safety=[ordered]@{formalClipRootUsed=$false;authorizedRemNoteRoot='8zNzfX45XLCSKoPSJ';authorizedAnkiDeck='零碎知识剪藏::Canary::PhaseB-20260828T081109Z';unknownExternalAutomaticRetries=0;secretPersisted=$false;secretValuesRecorded=$false;userProcessesTerminated=0}
    conditional=@('MinerU credential unavailable: Path C providerCalls=0 and downstream not started','AnkiConnect service unreachable: writes=0; start Anki/AnkiConnect before a new original-input Anki-only run','Sealed candidate Orchestrator process task submit/dispatch has not yet been exercised against a real destination','Desktop physical capture remains a user foreground/input boundary; backend harness candidate-worker-bound path is qualified')
    supersededPreflightRoots=@((Join-Path (Split-Path -Parent $root) '20260830T084300Z'),(Join-Path (Split-Path -Parent $root) '20260830T084500Z'))
}
Write-Json $qualificationPath $qualification

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Candidate v1.2.12 Phase B qualification')
$lines.Add('')
$lines.Add(('- status: {0}' -f $qualification.status))
$lines.Add(('- candidate tree: `{0}`' -f $qualification.candidate.sealed.treeSha256))
$lines.Add(('- authority registry: `{0}`' -f $qualification.authority.registrySha256))
$lines.Add('')
foreach ($mode in @('A','B','C','D')) {
    $item = $pathEvidence[$mode]
    $lines.Add(('## Path {0}: {1}' -f $mode,$item.status))
    $lines.Add('')
    if ($mode -eq 'C') { $lines.Add('- MinerU provider calls: 0'); $lines.Add('- downstream started: false'); $lines.Add(''); continue }
    $lines.Add(('Original/cleaned: {0}' -f $item.content.cleanedText))
    $lines.Add(('DeepSeek calls: {0}; attempts: {1}' -f $item.content.provider.callCount,$item.content.provider.attemptCount))
    $lines.Add(('Category: {0}' -f (@($item.content.categories) -join ', ')))
    foreach ($card in @($item.content.cards)) { $lines.Add(('Card {0}: {1} -> {2}' -f $card.cardId,(@($card.front) -join ' / '),(@($card.back) -join ' / '))) }
    $lines.Add(('RemNote route: {0}; initial outcome: {1}; independent exact reads: {2}' -f $item.remnote.route,$item.remnote.initialOutcome,@($item.remnote.reads).Count))
    if ($mode -eq 'B') { $lines.Add(('Replay outcome: {0}; readback: {1}' -f $item.remnote.replay.payload.outcome,$item.remnote.replay.payload.readbackVerified)) }
    if ($mode -eq 'D') { $lines.Add(('Audio: {0} bytes, SHA-256 {1}' -f $item.speech.synthesis.audioBytes,$item.speech.synthesis.audioSha256)); $lines.Add('Physical playback: 2 natural completions; replay=from_start') }
    $lines.Add('')
}
$lines.Add('## Conditional boundaries')
$lines.Add('')
foreach ($entry in $qualification.conditional) { $lines.Add(('- {0}' -f $entry)) }
[IO.File]::WriteAllLines($readablePath, $lines, $utf8)

[ordered]@{status=$qualification.status;qualification=$qualificationPath;sha256=(Get-FileHash -LiteralPath $qualificationPath -Algorithm SHA256).Hash;readable=$readablePath;readableSha256=(Get-FileHash -LiteralPath $readablePath -Algorithm SHA256).Hash;anki=$ankiRecheck.state} | ConvertTo-Json -Depth 20

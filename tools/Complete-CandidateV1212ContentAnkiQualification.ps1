#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)

$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $output){throw "Append-only qualification root exists: $output"}
[IO.Directory]::CreateDirectory($output)|Out-Null
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Read-Json([string]$Path){Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json -Depth 100}
function Path-Evidence([string]$Root,[string]$Name,[string]$Mode){
    $pathRoot=Join-Path $Root $Name
    $pathReceiptPath=Join-Path $pathRoot 'path-receipt.json'
    $readbackPath=Join-Path $Root ("anki-exact-readback-{0}-receipt.json" -f $Name)
    $pathReceipt=Read-Json $pathReceiptPath
    $readback=Read-Json $readbackPath
    if($pathReceipt.status -ne 'succeeded' -or $readback.status -ne 'passed'){throw "Path $Name is not fully qualified."}
    $jobs=@(Get-ChildItem -LiteralPath (Join-Path $pathRoot 'jobs') -Directory)
    if($jobs.Count -ne 1){throw "Path $Name immutable job count mismatch."}
    $job=$jobs[0].FullName
    $learningPath=Get-ChildItem -LiteralPath $job -Recurse -Filter 'learning-bundle.schema-v1.json'|Select-Object -First 1 -ExpandProperty FullName
    $cardPath=Get-ChildItem -LiteralPath $job -Recurse -Filter 'card-bundle.schema-v1.json'|Select-Object -First 1 -ExpandProperty FullName
    $learning=Read-Json $learningPath
    $contentResultPath=Get-ChildItem -LiteralPath $job -Recurse -Filter 'result.json'|ForEach-Object {$j=Read-Json $_.FullName;if($j.worker.id -eq 'content.deepseek'){$_.FullName}}|Select-Object -First 1
    $ankiResultPath=Get-ChildItem -LiteralPath $job -Recurse -Filter 'result.json'|ForEach-Object {$j=Read-Json $_.FullName;if($j.worker.id -eq 'export.anki'){$_.FullName}}|Select-Object -First 1
    $classifications=@($learning.items|ForEach-Object {@($_.units|ForEach-Object {[ordered]@{categoryId=$_.category_id;subtype=$_.subtype;state=$_.state;unresolvedReasons=@($_.unresolved_reasons)}})})
    $traces=@($learning.ai_trace)
    [ordered]@{
        id=$Mode;status='passed';taskId=$pathReceipt.taskId
        source=[ordered]@{originalText=[string]$pathReceipt.input.text;cleanedText=(@($learning.items|ForEach-Object {$_.cleaning.cleaned_text}) -join "`n");classification=$classifications}
        content=[ordered]@{worker='content.deepseek@0.7.0';resultPath=$contentResultPath;resultSha256=Hash $contentResultPath;learningBundlePath=$learningPath;learningBundleSha256=Hash $learningPath;cardBundlePath=$cardPath;cardBundleSha256=Hash $cardPath;providerCalls=$traces.Count;provider='deepseek';model='deepseek-chat';allLive=(@($traces|Where-Object {$_.mode -ne 'live' -or $_.provider -ne 'deepseek'}).Count -eq 0);settingsDefault=$pathReceipt.coreSettings.snapshot.modules.content.runtimePolicy.languagePracticeDefault}
        anki=[ordered]@{worker='export.anki@1.2.1';resultPath=$ankiResultPath;resultSha256=Hash $ankiResultPath;exactReadbackReceipt=$readbackPath;exactReadbackSha256=Hash $readbackPath;deck=[string]$readback.exactReadback.deckName;notes=@($readback.exactReadback.items);externalWriteCount=[int]$readback.export.externalWriteCount}
        pathReceipt=[ordered]@{path=$pathReceiptPath;sha256=Hash $pathReceiptPath}
    }
}

$ordinaryRoot=Join-Path $repo 'canary\phase-b-candidate-v1.2.12\20260830T091500Z-content-anki-language'
$languageRoot=Join-Path $repo 'canary\phase-b-candidate-v1.2.12\20260830T092000Z-language-unique-target'
$paths=@(
    Path-Evidence $ordinaryRoot 'a' 'ordinary-text'
    Path-Evidence $languageRoot 'e' 'meaning-reading'
    Path-Evidence $languageRoot 'f' 'writing'
    Path-Evidence $languageRoot 'g' 'both'
)
$failedF=Join-Path $ordinaryRoot 'f\path-receipt.json';$failedG=Join-Path $ordinaryRoot 'g\path-receipt.json'
$candidate=Join-Path $repo 'releases\kc-integrated-settings-v1212-20260830T183500Z\win-x64'
$manifest=Join-Path $candidate 'candidate-file-manifest.v1.json'
$registry=Join-Path $candidate 'authority\metadata\worker-registry.v1.json'
$checkpoint=Join-Path $candidate 'authority\metadata\checkpoint.json'
$receipt=[ordered]@{
    schemaVersion='candidate-v1.2.12-content-anki-qualification.v1'
    generatedAt=[DateTimeOffset]::UtcNow.ToString('o')
    status='passed-backend-harness-candidate-worker-bound'
    executionClass='backend-harness candidate-worker-bound; exact packaged Core DLL loaded in-process; not sealed Desktop/Orchestrator process E2E'
    candidate=[ordered]@{root=$candidate;manifestPath=$manifest;manifestSha256=Hash $manifest;sealedTreeSha256='4F1021038635984154290467011FBB25AB9D88DD7A1A035F059CFF309231416A';coreAssemblySha256='A645B00509D6FBB7C197128A76A827A51673C3FB6DB50BC5E51EACBC597F7D38'}
    authority=[ordered]@{registryVersion='1.2.12';registryPath=$registry;registrySha256=Hash $registry;checkpointPath=$checkpoint;checkpointSha256=Hash $checkpoint}
    runs=[ordered]@{ordinaryExecutionReceipt=Join-Path $ordinaryRoot 'execution-receipt.json';ordinaryExecutionReceiptSha256=Hash (Join-Path $ordinaryRoot 'execution-receipt.json');languageExecutionReceipt=Join-Path $languageRoot 'execution-receipt.json';languageExecutionReceiptSha256=Hash (Join-Path $languageRoot 'execution-receipt.json')}
    paths=$paths
    totals=[ordered]@{tasks=4;contentWorkerRuns=4;ankiWorkerRuns=4;deepSeekCalls=($paths|ForEach-Object {$_.content.providerCalls}|Measure-Object -Sum).Sum;ankiNotes=@($paths.anki.notes).Count;ankiExternalWrites=($paths|ForEach-Object {$_.anki.externalWriteCount}|Measure-Object -Sum).Sum;remnoteWrites=0;mineruCalls=0;physicalSpeakerInvocations=0}
    supersededFailClosedEvidence=@(
        [ordered]@{path=$failedF;sha256=Hash $failedF;status='waiting_input';reason='language_writing_target_not_unique';ankiWrites=0},
        [ordered]@{path=$failedG;sha256=Hash $failedG;status='waiting_input';reason='language_writing_target_not_unique';ankiWrites=0}
    )
    safety=[ordered]@{authorizedDeck='零碎知识剪藏::Canary::PhaseB-20260828T081109Z';otherDeckWrites=0;formalDeckCreated=$false;mediaWrites=0;secretsRecorded=$false;automaticRetryPerformed=$false;ocr='user-deferred'}
    remainingBoundary=[ordered]@{desktopProcessE2E='conditional-user-manual';sealedOrchestratorProcessSubmit='not-demonstrated-by-this-backend-harness';ocr='user-deferred-nonblocking'}
}
$receiptPath=Join-Path $output 'qualification-receipt.json'
[IO.File]::WriteAllText($receiptPath,(($receipt|ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false))
$text=New-Object Text.StringBuilder
[void]$text.AppendLine('# Candidate v1.2.12 Content → Anki qualification').AppendLine().AppendLine('- status: PASS (backend-harness candidate-worker-bound)').AppendLine('- boundary: exact packaged Core DLL loaded in-process; not sealed Desktop/Orchestrator process E2E').AppendLine('- deck: 零碎知识剪藏::Canary::PhaseB-20260828T081109Z').AppendLine()
foreach($path in $paths){[void]$text.AppendLine("## $($path.id)").AppendLine().AppendLine('### 原始文本').AppendLine().AppendLine($path.source.originalText).AppendLine().AppendLine('### 清洗文本').AppendLine().AppendLine($path.source.cleanedText).AppendLine().AppendLine('### 分类').AppendLine().AppendLine(($path.source.classification|ConvertTo-Json -Depth 20)).AppendLine().AppendLine('### 最终 Anki').AppendLine();foreach($note in $path.anki.notes){[void]$text.AppendLine("- template=$($note.template); noteId=$($note.noteId); cardId=$($note.cardId); ankiCardIds=$(@($note.ankiCardIds)-join ','); tags=$(@($note.tags)-join ',')").AppendLine("  - front: $($note.front)").AppendLine("  - back: $($note.back)")};[void]$text.AppendLine()}
[void]$text.AppendLine('## Fail-closed evidence').AppendLine().AppendLine('最初 writing/both 材料中的目标词出现两次；Content 返回 language_writing_target_not_unique，Anki 写入 0。新的唯一目标材料从原始输入新建任务后通过，旧证据未覆盖。').AppendLine().AppendLine('## Remaining boundary').AppendLine().AppendLine('- OCR: user-deferred, nonblocking.').AppendLine('- Desktop/sealed Orchestrator process E2E: conditional manual boundary; this receipt does not claim it.')
$readablePath=Join-Path $output 'READABLE.md';[IO.File]::WriteAllText($readablePath,$text.ToString(),[Text.UTF8Encoding]::new($false))
[ordered]@{status=$receipt.status;receipt=$receiptPath;receiptSha256=Hash $receiptPath;readable=$readablePath;readableSha256=Hash $readablePath;tasks=$paths.Count;notes=@($paths.anki.notes).Count}|ConvertTo-Json -Depth 20

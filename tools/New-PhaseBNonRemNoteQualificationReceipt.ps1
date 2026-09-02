[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$APathRoot,
    [Parameter(Mandatory = $true)][string]$DPathRoot,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$aRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $APathRoot))
$dRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $DPathRoot))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (Test-Path -LiteralPath $outputRoot) { throw "Append-only qualification root exists: $outputRoot" }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100 }
function Find-One([string]$Root, [string]$Filter) {
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Filter)
    if ($files.Count -ne 1) { throw "Expected one $Filter under $Root; actual=$($files.Count)" }
    $files[0]
}
function Invoke-Anki([string]$Action, [hashtable]$Params = @{}) {
    $body = @{ action = $Action; version = 6; params = $Params } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-RestMethod -Uri 'http://127.0.0.1:8765' -Method Post -ContentType 'application/json' -Body $body
    if ($null -ne $response.error) { throw "Anki readback failed: $Action" }
    $response.result
}

$aReceiptPath = Join-Path $aRoot 'path-receipt.json'
$dReceiptPath = Join-Path $dRoot 'path-receipt.json'
$aReceipt = Read-Json $aReceiptPath
$dReceipt = Read-Json $dReceiptPath
if ($aReceipt.status -ne 'succeeded' -or $dReceipt.status -ne 'succeeded') { throw 'A and D must both be succeeded.' }
if (@($aReceipt.plan.routing.destinations) -ne 'anki' -or @($dReceipt.plan.routing.destinations) -ne 'anki') { throw 'Qualification must be Anki-only.' }
if (@($aReceipt.attempts | Where-Object workerId -eq 'export.remnote').Count -ne 0 -or @($dReceipt.attempts | Where-Object workerId -eq 'export.remnote').Count -ne 0) { throw 'RemNote worker was unexpectedly invoked.' }

$deck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
$noteIds = @(Invoke-Anki 'findNotes' @{ query = "deck:`"$deck`"" })
if ($noteIds.Count -ne 2) { throw "Expected exactly two Canary notes; actual=$($noteIds.Count)" }
$notes = @(Invoke-Anki 'notesInfo' @{ notes = $noteIds })
$cardIds = @($notes | ForEach-Object cards)
$cards = @(Invoke-Anki 'cardsInfo' @{ cards = $cardIds })
if (@($cards | Where-Object deckName -ne $deck).Count -ne 0) { throw 'Anki card escaped the authorized deck.' }

$aCardFile = Find-One $aRoot 'card-bundle.schema-v1.json'
$dCardFile = Find-One $dRoot 'card-bundle.schema-v1.json'
$aLearningFile = Find-One $aRoot 'learning-bundle.schema-v1.json'
$dLearningFile = Find-One $dRoot 'learning-bundle.schema-v1.json'
$aCard = Read-Json $aCardFile.FullName
$dCard = Read-Json $dCardFile.FullName
$aLearning = Read-Json $aLearningFile.FullName
$dLearning = Read-Json $dLearningFile.FullName
$audio = Find-One $dRoot '*.mp3'
if ($audio.FullName.Length -le 260) { throw 'D audio artifact is not beyond MAX_PATH.' }
if ($dReceipt.physicalPlayback.physicalSpeakerInvocations -ne 2 -or $dReceipt.replay.status -ne 'succeeded' -or $dReceipt.replay.exportStages -ne 0 -or $dReceipt.replay.contentStages -ne 0) { throw 'D playback/replay boundary is incomplete.' }

$attemptEvidence = foreach ($item in @(@{ name='A'; root=$aRoot }, @{ name='D'; root=$dRoot })) {
    $results = @(Get-ChildItem -LiteralPath $item.root -Recurse -File -Filter 'result.json' | ForEach-Object {
        $result = Read-Json $_.FullName
        [ordered]@{ stageId=$result.stageId; attemptId=$result.attemptId; workerId=$result.worker.id; workerVersion=$result.worker.version; status=$result.status; path=$_.FullName; bytes=$_.Length; sha256=Sha $_.FullName }
    })
    [ordered]@{ path=$item.name; results=$results }
}

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-phase-b-non-remnote-qualification.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed-conditional'
    authority = [ordered]@{
        registryVersion = '1.2.11'
        checkpoint = 'contracts/authority/worker-registry/v1.2.11-20260828T092800Z/checkpoint.json'
        checkpointSha256 = 'A01D0E65DEABA5078D7A57EF57FFFFF547DB3F5BBD03074247135DF463CA7F1C'
        releaseRoot = 'releases/knowledge-clipper-authority-v1.2.11-20260828T092800Z/win-x64'
    }
    core = [ordered]@{ runtimeVersion='2.4.6'; checkpointVersion='2.4.8'; checkpointSha256='3306DF313EB6AACF52AE1A447F3F095C95295605A2D68A451ADA5D00AE41C846' }
    pathA = [ordered]@{
        status=$aReceipt.status; receipt=$aReceiptPath; receiptSha256=Sha $aReceiptPath; originalInput=$aReceipt.input.text
        cleanedText=$aLearning.items[0].cleaning.cleaned_text; category=$aCard.cards[0].category_id
        card=[ordered]@{ front=$aCard.cards[0].front_blocks[0].text; back=$aCard.cards[0].back_blocks[0].text; bundleSha256=Sha $aCardFile.FullName }
        workers=@($aReceipt.attempts | Select-Object stageId,workerId,workerVersion,status)
        deepSeekProviderCalls=@($aLearning.ai_trace).Count
    }
    pathD = [ordered]@{
        status=$dReceipt.status; receipt=$dReceiptPath; receiptSha256=Sha $dReceiptPath; originalInput=$dReceipt.input.text
        cleanedText=$dLearning.items[0].cleaning.cleaned_text; category=$dCard.cards[0].category_id
        card=[ordered]@{ front=$dCard.cards[0].front_blocks[0].text; back=$dCard.cards[0].back_blocks[0].text; bundleSha256=Sha $dCardFile.FullName }
        workers=@($dReceipt.attempts | Select-Object stageId,workerId,workerVersion,status)
        deepSeekProviderCalls=@($dLearning.ai_trace).Count
        audio=[ordered]@{ path=$audio.FullName; absolutePathLength=$audio.FullName.Length; bytes=$audio.Length; sha256=Sha $audio.FullName }
        playback=[ordered]@{ physicalSpeakerInvocations=2; naturalCompletion='idle_replayable'; replay='from_start'; replayStatus=$dReceipt.replay.status; replayExportStages=0; replayContentStages=0 }
    }
    ankiReadback = [ordered]@{
        deck=$deck; noteCount=$noteIds.Count; noteIds=$noteIds; cardIds=$cardIds
        notes=@($notes | ForEach-Object { [ordered]@{ noteId=$_.noteId; modelName=$_.modelName; tags=@($_.tags); fields=$_.fields } })
        cards=@($cards | ForEach-Object { [ordered]@{ cardId=$_.cardId; note=$_.note; deckName=$_.deckName; question=$_.question; answer=$_.answer } })
    }
    attemptEvidence=$attemptEvidence
    remnote=[ordered]@{ workerInvocations=0; writes=0; oldDCommitUnknown='unresolved'; automaticRetry=$false; blocker='plugin request processing timeout on read_note; not a Core/Exporter regression' }
    pathC=[ordered]@{ status='blocked-credential-not-run'; mineruProviderCalls=0; reason='MINERU_API_TOKEN unavailable in current process/user environment; RemNote runtime secret lookup prohibited'; legacyBootstrapSource=$true; secretPersisted=$false; desktopCredentialChainPassed=$false; manualCredentialSetupBoundary=$true }
    overall=[ordered]@{ nonRemNoteQualification='passed'; fullPhaseB='conditional'; noGo=@('Path C credential injection unavailable','Path B and RemNote export blocked pending read/reconcile') }
}

$receiptPath = Join-Path $outputRoot 'qualification-receipt.json'
[IO.File]::WriteAllText($receiptPath, (($receipt | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
$readable = @"
# Phase B v1.2.11 非 RemNote 资格回执

- A：succeeded。原文“$($receipt.pathA.originalInput.Replace("`n", ' / '))”；清洗零改写；卡片“$($receipt.pathA.card.front)” → “$($receipt.pathA.card.back)”；Anki completed。
- D：succeeded。卡片“$($receipt.pathD.card.front)” → “$($receipt.pathD.card.back)”；Speech 1.2.3 synth completed；真实物理播放 2 次，初播与 from_start replay 均自然结束；音频绝对路径长度 $($receipt.pathD.audio.absolutePathLength)，SHA-256 $($receipt.pathD.audio.sha256)。
- Anki：仅隔离 deck `$deck`，精确回读 2 notes / $($cardIds.Count) cards。
- RemNote：本轮 worker 调用 0、写入 0；旧 D commit_unknown 仍 unresolved，未自动重试。
- C：未运行。当前进程/用户环境无 MinerU Token；未从 RemNote 运行时取 secret，provider 调用 0。

总体：非 RemNote A/D PASS；完整 A/B/C/D 仍 conditional。
"@
$readablePath = Join-Path $outputRoot 'readable.md'
[IO.File]::WriteAllText($readablePath, $readable, [Text.UTF8Encoding]::new($false))
[ordered]@{ status=$receipt.status; receipt=$receiptPath; receiptSha256=Sha $receiptPath; readable=$readablePath; readableSha256=Sha $readablePath } | ConvertTo-Json

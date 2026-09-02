#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$PathName = 'a',
    [string]$ExpectedDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$output = Join-Path $root ("anki-exact-readback-{0}-receipt.json" -f $PathName.ToLowerInvariant())
if (Test-Path -LiteralPath $output) { throw ('Append-only readback exists: {0}' -f $output) }
$pathReceipt = Get-Content -LiteralPath (Join-Path $root ("{0}\path-receipt.json" -f $PathName.ToLowerInvariant())) -Raw | ConvertFrom-Json -Depth 100
if ($pathReceipt.status -ne 'succeeded') { throw ('Anki path {0} is not succeeded.' -f $PathName) }
$jobRoots = @(Get-ChildItem -LiteralPath (Join-Path $root ("{0}\jobs" -f $PathName.ToLowerInvariant())) -Directory)
if ($jobRoots.Count -ne 1) { throw 'Anki path must contain exactly one immutable task package.' }
$jobRoot = $jobRoots[0].FullName
$itemsPath = Get-ChildItem -LiteralPath $jobRoot -Recurse -Filter 'items.ndjson' | Where-Object {$_.FullName -match 'anki-export'} | Select-Object -First 1 -ExpandProperty FullName
$cardBundlePath = Get-ChildItem -LiteralPath $jobRoot -Recurse -Filter 'card-bundle.schema-v1.json' | Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($itemsPath) -or [string]::IsNullOrWhiteSpace($cardBundlePath)) { throw 'Anki evidence inputs are missing.' }
$itemLines = @(Get-Content -LiteralPath $itemsPath | Where-Object {$_.Trim().Length -gt 0})
if ($itemLines.Count -lt 1) { throw 'Anki item evidence must contain at least one item.' }
$items = @($itemLines | ForEach-Object { $_ | ConvertFrom-Json })
$bundle = Get-Content -LiteralPath $cardBundlePath -Raw | ConvertFrom-Json -Depth 100
$bundleCards = @($bundle.cards)
if ($bundleCards.Count -ne $items.Count) { throw 'CardBundle and exporter item count mismatch.' }
$actions = [Collections.Generic.List[string]]::new()
function Invoke-Anki([string]$Action, [hashtable]$Parameters) {
    $actions.Add($Action)
    $body = [ordered]@{action=$Action;version=6;params=$Parameters} | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8765' -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 15
    if ($null -ne $response.error) { throw ('AnkiConnect {0} failed: {1}' -f $Action,$response.error) }
    $response.result
}
$notes = @(Invoke-Anki 'notesInfo' @{notes=@($items | ForEach-Object {[long]$_.note_id})})
if ($notes.Count -ne $items.Count) { throw 'Exact Anki note readback count mismatch.' }
$cardIds = @($notes | ForEach-Object {@($_.cards)} | ForEach-Object {$_})
if ($cardIds.Count -lt $items.Count) { throw 'Exact Anki notes have missing cards.' }
$cards = @(Invoke-Anki 'cardsInfo' @{cards=$cardIds})
if ($cards.Count -ne $cardIds.Count) { throw 'Exact Anki card readback count mismatch.' }
if (@($cards | Where-Object {$_.deckName -ne $ExpectedDeck}).Count -ne 0) { throw 'Exact Anki card deck readback mismatch.' }
$readbackItems = @()
foreach ($item in $items) {
    $bundleCard = @($bundleCards | Where-Object {[string]$_.card_id -eq [string]$item.card_id})
    if ($bundleCard.Count -ne 1) { throw 'CardBundle and exporter item identity mismatch.' }
    $note = @($notes | Where-Object {[long]$_.noteId -eq [long]$item.note_id})
    if ($note.Count -ne 1) { throw 'Exact Anki note identity mismatch.' }
    $front = [string](@($bundleCard[0].front_blocks.text) -join "`n")
    $back = [string](@($bundleCard[0].back_blocks.text) -join "`n")
    $observedFront = [string]$note[0].fields.Front.value
    $observedBack = [string]$note[0].fields.Back.value
    if ($observedFront -ne $front -or $observedBack -ne $back) { throw 'Exact Anki front/back readback mismatch.' }
    $readbackItems += [ordered]@{cardId=[string]$item.card_id;noteId=[long]$item.note_id;status=[string]$item.status;revision=$item.revision;template=[string]$bundleCard[0].template;front=$observedFront;back=$observedBack;tags=@($note[0].tags);ankiCardIds=@($note[0].cards);frontExact=$true;backExact=$true;deckExact=$true}
}
$workerResultPaths = @(Get-ChildItem -LiteralPath $jobRoot -Recurse -Filter 'result.json' | ForEach-Object { $j=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json -Depth 100; if($j.worker.id -eq 'export.anki'){[pscustomobject]@{Path=$_.FullName;Value=$j}} })
if ($workerResultPaths.Count -ne 1) { throw 'Anki worker result must be unique.' }
$workerResultPath = $workerResultPaths[0]
$receipt = [ordered]@{
    schemaVersion='knowledge-clipper-candidate-v1.2.12-anki-exact-readback.v1'
    generatedAt=[DateTimeOffset]::UtcNow.ToString('o')
    status='passed'
    executionClass='backend-harness candidate-worker-bound; exact packaged Core DLL loaded in-process'
    candidate=$pathReceipt.candidate
    authority=$pathReceipt.authority
    taskId=$pathReceipt.taskId
    immutableTaskRoot=$jobRoot
    export=[ordered]@{worker='export.anki@1.2.1';resultPath=$workerResultPath.Path;resultSha256=(Get-FileHash -LiteralPath $workerResultPath.Path -Algorithm SHA256).Hash;status=$workerResultPath.Value.status;externalWriteCount=$items.Count;createdCount=@($items | Where-Object {$_.status -eq 'CREATED'}).Count;items=$readbackItems}
    exactReadback=[ordered]@{actions=@($actions);noteCount=$notes.Count;cardCount=$cards.Count;deckName=$ExpectedDeck;items=$readbackItems;notesInfoSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($notes|ConvertTo-Json -Depth 50 -Compress))));cardsInfoSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($cards|ConvertTo-Json -Depth 50 -Compress))))}
    content=[ordered]@{cardBundlePath=$cardBundlePath;cardBundleSha256=(Get-FileHash -LiteralPath $cardBundlePath -Algorithm SHA256).Hash;cardCount=$bundleCards.Count}
    safety=[ordered]@{authorizedDeck=$ExpectedDeck;formalDeckCreated=$false;otherDeckWrites=0;mediaWrites=0;secretValuesRecorded=$false;automaticRetryPerformed=$false}
}
[IO.File]::WriteAllText($output,(($receipt|ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{status=$receipt.status;receipt=$output;sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;noteIds=@($readbackItems.noteId);cardCount=$receipt.exactReadback.cardCount;deck=$ExpectedDeck}|ConvertTo-Json -Depth 20

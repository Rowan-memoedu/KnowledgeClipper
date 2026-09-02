param(
    [Parameter(Mandatory = $true)][string]$ModuleReceiptPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$ModuleReceiptPath = [IO.Path]::GetFullPath($ModuleReceiptPath)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Hash-Text([string]$Value) { $sha=[Security.Cryptography.SHA256]::Create(); try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() } }
function Invoke-Anki([string]$Action, [hashtable]$Params=@{}) {
    $body = @{ action=$Action; version=6; params=$Params } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-RestMethod -Uri 'http://127.0.0.1:8765' -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if ($null -ne $response.error) { throw "AnkiConnect $Action failed: $($response.error)" }
    $response.result
}
function Write-JsonOnce([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable output already exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}

if (Test-Path -LiteralPath $OutputRoot) { throw "Immutable output root already exists: $OutputRoot" }
$module = Get-Content -LiteralPath $ModuleReceiptPath -Raw | ConvertFrom-Json
if ($module.status -ne 'passed' -or -not $module.checks.isolatedDeckOnly -or -not $module.checks.frontBackExactReadback -or $module.deck -ne '零碎知识剪藏 Canary') { throw 'Passed isolated Anki live module receipt required' }
$noteIds = @($module.readback.noteId)
$cardIds = @($module.readback | ForEach-Object { $_.cardIds[0] })
$profile = [string](Invoke-Anki 'getActiveProfile')
if ((Hash-Text $profile) -ne ([string]$module.configuration.activeProfileSha256).ToLowerInvariant()) { throw 'Active profile changed since live write' }
$notes = @(Invoke-Anki 'notesInfo' @{ notes=$noteIds })
$cards = @(Invoke-Anki 'cardsInfo' @{ cards=$cardIds })
if ($notes.Count -ne 4 -or $cards.Count -ne 4) { throw 'Expected exactly four isolated Canary notes/cards' }
foreach ($expected in $module.readback) {
    $note = $notes | Where-Object noteId -eq $expected.noteId
    $card = $cards | Where-Object cardId -eq $expected.cardIds[0]
    if ($null -eq $note -or $null -eq $card) { throw "Missing Anki readback for $($expected.cardId)" }
    if ($note.fields.Front.value -ne $expected.ankiFront -or $note.fields.Back.value -ne $expected.ankiBack -or $card.deckName -ne $module.deck) { throw "Anki readback drifted for $($expected.cardId)" }
}
$readbackPath = Join-Path $OutputRoot 'independent-readback.json'
Write-JsonOnce $readbackPath ([ordered]@{
    profileSha256 = (Hash-Text $profile); deck = $module.deck
    notes = $notes | ForEach-Object { [ordered]@{ noteId=$_.noteId; front=$_.fields.Front.value; back=$_.fields.Back.value; tags=$_.tags } }
    cards = $cards | ForEach-Object { [ordered]@{ cardId=$_.cardId; note=$_.note; deckName=$_.deckName } }
})
$receiptPath = Join-Path $OutputRoot 'path-B-receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = 'quality-real-path/1.0.0'; pathId = 'B'; status = 'passed'; generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    route = 'text/language cards -> real DeepSeek -> Core content-to-anki.v1 projection -> real Anki Canary deck'
    source = [ordered]@{ moduleReceipt=$ModuleReceiptPath; moduleReceiptSha256=(Hash-File $ModuleReceiptPath); contentReceipt=$module.source.receiptPath; contentReceiptSha256=$module.source.receiptSha256 }
    isolation = [ordered]@{ deck=$module.deck; allDeckRoutesPinnedToCanaryDeck=$true; dailyDeckTouched=$false; deleted=$false }
    observed = [ordered]@{ notes=$notes.Count; cards=$cards.Count; firstWrite=$module.attempts.firstWrite.counts; sameRevision=$module.attempts.sameRevisionRerun.counts; orderedTemplates=$module.cardSemantics.orderedTemplates }
    independentReadback = [ordered]@{ path=$readbackPath; sha256=(Hash-File $readbackPath); frontBackExact=$true }
    externalCalls = [ordered]@{ deepseek=$module.apiCalls.contentProvider; ankiConnectOriginal=$module.apiCalls.ankiConnectTotal; ankiConnectIndependentReadback=3 }
    noDelete = $true
})
[ordered]@{ status='passed'; receipt=$receiptPath; sha256=(Hash-File $receiptPath); readback=$readbackPath } | ConvertTo-Json -Depth 5

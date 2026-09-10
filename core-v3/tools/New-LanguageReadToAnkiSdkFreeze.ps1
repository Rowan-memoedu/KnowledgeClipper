[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$RealReceipt,
    [string]$OutputRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki-sdk'
)
$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
$candidate = [IO.Path]::GetFullPath($Candidate); $receiptPath = [IO.Path]::GetFullPath($RealReceipt)
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 100
if ($receipt.schema -notin @('kc.read-to-anki-language-real-matrix/1','kc.read-to-anki-language-real-matrix/2') -or $receipt.status -ne 'PASS') { throw 'A passing real language matrix receipt is required.' }
$expected = @{ 'meaning-reading'=@('language_context'); 'writing'=@('language_writing'); 'both'=@('language_context','language_writing') }
foreach ($row in $receipt.matrix) {
    if (@($row.expectedTemplates).Count -ne @($expected[$row.mode]).Count -or (Compare-Object @($row.expectedTemplates) @($expected[$row.mode]) -SyncWindow 0)) { throw "Language template matrix drifted: $($row.mode)" }
    if (@($row.semanticReadback.cards).Count -ne @($row.expectedTemplates).Count) { throw "Language readback cardinality drifted: $($row.mode)" }
}
if ($receipt.ask.viaAsk -ne $true -or @($receipt.ask.semanticReadback.cards).Count -ne 1) { throw 'Ask choice path is not complete.' }
$id = 'rta-language-sdk-' + (Get-Date -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$out = Join-Path $OutputRoot $id; New-Item -ItemType Directory -Path $out | Out-Null
$client = Join-Path $candidate 'core-v3\KnowledgeClipper.Core.V3.Client.dll'; $manifest = Join-Path $candidate 'candidate-manifest.json'
WriteJson (Join-Path $out 'sdk-contract.v2.json') ([ordered]@{
    schema='kc.read-to-anki-language-sdk-freeze/2'; assembly=[ordered]@{path='candidate/core-v3/KnowledgeClipper.Core.V3.Client.dll';sha256=Hash $client}
    request=[ordered]@{type='ReadAloudCardsRequest';languageCards='LanguageCardSettings(revision,defaultChoice)';choices=@('ask','meaning-reading','writing','both');includeSpeech='true for read, false via ClipCardsAsync';effectiveAt='operation-submit'}
    ask=[ordered]@{pipeline='waiting_language_card_choice';projection='LanguageCardChoice(targetToken,settingsRevision,choices)';resume='ChooseLanguageCardTypeAsync(LanguageCardChoiceRequest)';singleUse=$true}
    draft=[ordered]@{cardType='FlashcardDraftCard';fields=@('cardId','revision','cardSha256','front','back','template');templates=@('basic_qa','language_context','language_writing');confirm='ConfirmFlashcardDraftAsync';cancel='CancelFlashcardDraftAsync'}
    resolution=[ordered]@{pipeline='awaiting_card_resolution';projection='CardResolution(targetToken,revision,reasonCode,evidence,actions)';unresolvedCardBundleRequired=$false;ankiAttempts=0;cancel='cancel_card_resolution'}
    invariants=[ordered]@{preConfirmAnki=0;bothOrder=@('language_context','language_writing');staleDoubleCancel='fail-closed-before-export';speechIndependent=$true;cardsOnlyPlayback='not_requested';runtimeTools='candidate-relative-sha-admitted';developerEnvironmentRequired=$false}
})
WriteJson (Join-Path $out 'real-language-matrix.v1.json') ([ordered]@{schema='kc.read-to-anki-language-sdk-real-evidence/1';source=[ordered]@{candidateManifestSha256=Hash $manifest;receiptSha256=Hash $receiptPath};original=$receipt.original;matrix=$receipt.matrix;ask=$receipt.ask;workers=[ordered]@{content=$receipt.contentWorker;anki=$receipt.ankiWorker};physicalSpeakerPlaybackCount=$receipt.physicalSpeakerPlaybackCount})
$readable = "# Language card SDK freeze`n`n- meaning-reading: 1 x language_context`n- writing: 1 x language_writing`n- both: language_context -> language_writing`n- ask: waiting_language_card_choice -> target-token choice -> draft`n- Each confirmation uses the isolated Canary deck and exact semantic readback.`n"
Set-Content -LiteralPath (Join-Path $out 'README.md') -Value $readable -Encoding utf8NoBOM
$files=Get-ChildItem $out -File|Where-Object Name -ne 'freeze-manifest.json'|ForEach-Object{[ordered]@{path=$_.Name;bytes=$_.Length;sha256=Hash $_.FullName}}|Sort-Object path
WriteJson (Join-Path $out 'freeze-manifest.json') ([ordered]@{schema='kc.read-to-anki-language-sdk-freeze-manifest/1';freezeId=$id;createdAt=(Get-Date).ToUniversalTime().ToString('o');source=[ordered]@{candidate=$candidate;candidateManifestSha256=Hash $manifest;realReceipt=$receiptPath;realReceiptSha256=Hash $receiptPath};files=$files})
Write-Output "SDK_FREEZE $out"; Write-Output "MANIFEST_SHA256 $(Hash (Join-Path $out 'freeze-manifest.json'))"

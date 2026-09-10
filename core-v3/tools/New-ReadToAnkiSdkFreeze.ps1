[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$RealReceipt,
    [string]$OutputRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki-sdk'
)

$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) {
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

$candidateRoot = [IO.Path]::GetFullPath($Candidate)
$receiptPath = [IO.Path]::GetFullPath($RealReceipt)
if (-not (Test-Path -LiteralPath $candidateRoot) -or -not (Test-Path -LiteralPath $receiptPath)) { throw 'Candidate or real receipt is missing.' }
$candidateManifest = Join-Path $candidateRoot 'candidate-manifest.json'
if (-not (Test-Path -LiteralPath $candidateManifest)) { throw 'Candidate manifest is missing.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 100
if ($receipt.status -ne 'PASS' -or $receipt.replayWorkerAttemptDelta.content -ne 0 -or $receipt.replayWorkerAttemptDelta.synthesis -ne 0 -or $receipt.replayWorkerAttemptDelta.anki -ne 0 -or $receipt.replayWorkerAttemptDelta.playback -ne 1 -or $receipt.replayExternalWriteDelta -ne 0) {
    throw 'SDK freeze requires a passing replay receipt with only one fresh playback attempt and zero replay writes.'
}

$id = 'rta-sdk-' + (Get-Date -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$out = Join-Path $OutputRoot $id
if (Test-Path -LiteralPath $out) { throw "Freeze root already exists: $out" }
New-Item -ItemType Directory -Path $out | Out-Null

$clientDll = Join-Path $candidateRoot 'core-v3\KnowledgeClipper.Core.V3.Client.dll'
if (-not (Test-Path -LiteralPath $clientDll)) { throw 'Candidate does not contain the public Core V3 Client assembly.' }
$projection = $receipt.draftRecoveryProjection

WriteJson (Join-Path $out 'sdk-contract.v1.json') ([ordered]@{
    schema = 'kc.read-to-anki-sdk-freeze/1'
    sdk = [ordered]@{
        assembly = 'candidate/core-v3/KnowledgeClipper.Core.V3.Client.dll'
        sha256 = Hash $clientDll
        clientType = 'KnowledgeClipper.Core.V3.Client.CoreV3Client'
        operation = 'clip.read-aloud-cards'
        methods = @(
            [ordered]@{ name = 'ReadAloudCardsAsync'; requestType = 'ReadAloudCardsRequest'; effect = 'content+synthesis+one-playback+typed destination-neutral card draft; exporter=0 until confirmation' },
            [ordered]@{ name = 'ConfirmFlashcardDraftAsync'; requestType = 'FlashcardDraftConfirmationRequest'; effect = 'single-use operationId+token+revision+draftSha256 confirmation; then authorized destination export' },
            [ordered]@{ name = 'CancelFlashcardDraftAsync'; requestType = 'FlashcardDraftCancellationRequest'; effect = 'zero destination requests, attempts, and writes' },
            [ordered]@{ name = 'ReplayReadAloudCardsAsync'; requestType = 'ReadAloudCardsReplayRequest'; effect = 'same-operation; existing-synthesis-only; one fresh playback; no content; no Anki write' },
            [ordered]@{ name = 'GetReadAloudCardsProjectionAsync'; requestType = 'operationId'; effect = 'read-only durable projection' }
        )
        requestTypes = [ordered]@{
            ReadAloudCardsRequest = @('requestId','capture:TypedArtifactHandle','ankiConfiguration:TypedArtifactHandle','authorization:AnkiWriteAuthorization')
            FlashcardDraftConfirmationRequest = @('requestId','operationId','draft:FlashcardDraft','authorization:AnkiWriteAuthorization')
            FlashcardDraftCancellationRequest = @('requestId','operationId','draft:FlashcardDraft')
            FlashcardDraft = @('token','revision','draftSha256','cards[]')
            FlashcardDraftCard = @('cardId','revision','cardSha256','front','back')
            ReadAloudCardsReplayRequest = @('requestId','sourceOperationId','authorization:AnkiWriteAuthorization')
            AnkiWriteAuthorization = @('authorizationId','scope=isolated-canary-deck')
        }
        projectionType = [ordered]@{
            name = 'ReadAloudCardsProjectionSnapshot'
            fields = @('operationId','playback','pipeline','cursor','notice','captureArtifact','reviewArtifact','synthesisArtifact','playbackArtifact','cardBundleArtifact','ankiReadbackArtifact','cardDraft','ankiSemanticReadback','attentionRequired')
            playbackStates = @('accepted','playing','idle_replayable','failed','stopped','unknown')
            pipelineStates = @('accepted','reviewing','awaiting_card_confirmation','writing_anki','completed','cancelled','partially_failed','failed','unknown_external_state')
        }
    }
    cardPreview = [ordered]@{
        state = 'awaiting_card_confirmation'; confirmBinding = @('operationId','draft.token','draft.revision','draft.draftSha256');
        preConfirmExporter = [ordered]@{ requests = 0; attempts = 0; writes = 0 }; staleOrDoubleConfirm = 'fail-closed-before-export'; restart = 'restores same draft; never auto-confirms'
    }
    desktopRule = 'Use CoreV3Client typed methods and projection only. Do not parse canary receipts, worker result files, or task-package paths.'
})

WriteJson (Join-Path $out 'projection-example.v1.json') ([ordered]@{
    schema = 'kc.read-to-anki-projection-example/1'
    source = [ordered]@{ candidateManifestSha256 = Hash $candidateManifest; realReceiptSha256 = Hash $receiptPath; operationId = $receipt.operationId }
    projection = $projection
    replayInvariant = [ordered]@{
        sourceOperationId = $receipt.operationId
        contentAttemptDelta = $receipt.replayWorkerAttemptDelta.content
        synthesisAttemptDelta = $receipt.replayWorkerAttemptDelta.synthesis
        playbackAttemptDelta = $receipt.replayWorkerAttemptDelta.playback
        ankiAttemptDelta = $receipt.replayWorkerAttemptDelta.anki
        ankiWriteDelta = $receipt.replayExternalWriteDelta
        restartQueryWorkerAttemptDelta = $receipt.restartRecovery.workerAttemptDelta
    }
    cardPreviewInvariant = [ordered]@{
        preConfirmExporter = $receipt.preConfirmExporter
        recoveredDraftToken = $receipt.draftRecoveryProjection.cardDraft.token
        confirmedReadback = $receipt.initialProjection.ankiSemanticReadback
    }
})

WriteJson (Join-Path $out 'isolated-anki-boundary.v1.json') ([ordered]@{
    schema = 'kc.read-to-anki-isolated-anki-boundary/1'
    authorization = [ordered]@{ policy = 'isolated-canary-deck'; mode = 'explicit-user-action'; fields = @('authorizationId','scope') }
    destination = [ordered]@{ deck = $receipt.card.deck; formalDeckAllowed = $false; externalWriteCountInitial = $receipt.ankiEvidence.externalWriteCount; exactReadback = $receipt.ankiEvidence.exactReadback }
    replay = [ordered]@{ externalWriteDelta = $receipt.replayExternalWriteDelta; ankiAttemptDelta = $receipt.replayWorkerAttemptDelta.anki; contentAttemptDelta = $receipt.replayWorkerAttemptDelta.content }
})

$readable = @"
# Read-to-Anki SDK freeze

Desktop binds `CoreV3Client`, not canary receipts. Submit `ReadAloudCardsAsync`, poll `GetReadAloudCardsProjectionAsync` until `awaiting_card_confirmation`, render the typed Front/Back draft, then call `ConfirmFlashcardDraftAsync`. `CancelFlashcardDraftAsync` is a zero-export terminal. Use `ReplayReadAloudCardsAsync` only after a completed confirmation, with the same `sourceOperationId`.

- Raw: $($receipt.rawText)
- Cleaned: $($receipt.cleanedText)
- Front: $($receipt.card.front)
- Back: $($receipt.card.back)
- Deck: $($receipt.card.deck)
- Pre-confirm: requests=$($receipt.preConfirmExporter.requests), attempts=$($receipt.preConfirmExporter.attempts), writes=$($receipt.preConfirmExporter.writes).
- Replay: Content $($receipt.replayWorkerAttemptDelta.content), synthesis $($receipt.replayWorkerAttemptDelta.synthesis), playback +$($receipt.replayWorkerAttemptDelta.playback), Anki $($receipt.replayWorkerAttemptDelta.anki), Anki writes +$($receipt.replayExternalWriteDelta).
"@
Set-Content -LiteralPath (Join-Path $out 'README.md') -Value $readable -Encoding utf8NoBOM

$files = Get-ChildItem -LiteralPath $out -File | Where-Object Name -ne 'freeze-manifest.json' | ForEach-Object {
    [ordered]@{ path = $_.Name; bytes = $_.Length; sha256 = Hash $_.FullName }
} | Sort-Object path
WriteJson (Join-Path $out 'freeze-manifest.json') ([ordered]@{
    schema = 'kc.read-to-anki-sdk-freeze-manifest/1'; freezeId = $id; createdAt = (Get-Date).ToUniversalTime().ToString('o')
    source = [ordered]@{ candidate = $candidateRoot; candidateManifestSha256 = Hash $candidateManifest; realReceipt = $receiptPath; realReceiptSha256 = Hash $receiptPath }
    files = $files
})
Write-Output "SDK_FREEZE $out"
Write-Output "MANIFEST_SHA256 $(Hash (Join-Path $out 'freeze-manifest.json'))"

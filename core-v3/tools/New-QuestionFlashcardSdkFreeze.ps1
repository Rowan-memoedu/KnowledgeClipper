[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$FakeReceipt,
    [string]$OutputRoot = 'D:\Artifacts\KnowledgeClipper\question-flashcard-sdk'
)

$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }

$candidateRoot = [IO.Path]::GetFullPath($Candidate)
$receiptPath = [IO.Path]::GetFullPath($FakeReceipt)
$candidateManifest = Join-Path $candidateRoot 'candidate-manifest.json'
$clientDll = Join-Path $candidateRoot 'core-v3\KnowledgeClipper.Core.V3.Client.dll'
foreach ($required in @($candidateManifest, $clientDll, $receiptPath)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing freeze input: $required" } }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 100
if ($receipt.status -ne 'PASS' -or -not $receipt.question.staleTokenRejectedBeforeExporter -or -not $receipt.question.doubleConfirmRejected -or [int]$receipt.question.cancelled.state -ne 4) {
    throw 'Question SDK freeze requires the passing stale/double/cancel/restart Fake gate.'
}
if ($receipt.question.beforeQuestionConfirmation.ankiExportAttempts -ne 1 -or $receipt.question.afterQuestionConfirmation.ankiExportAttempts -ne 1) {
    throw 'Question SDK freeze detected a pre-confirm or duplicate destination attempt.'
}

$id = 'question-flashcard-sdk-' + (Get-Date -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$out = Join-Path $OutputRoot $id
if (Test-Path -LiteralPath $out) { throw "Freeze root already exists: $out" }
New-Item -ItemType Directory -Path $out | Out-Null

WriteJson (Join-Path $out 'sdk-contract.v1.json') ([ordered]@{
    schema = 'kc.question-flashcard-sdk-freeze/1'
    assembly = [ordered]@{ path = 'candidate/core-v3/KnowledgeClipper.Core.V3.Client.dll'; sha256 = Hash $clientDll; type = 'KnowledgeClipper.Core.V3.Client.CoreV3Client' }
    workflow = [ordered]@{
        operation = 'question.make-cards'
        methods = @('QuestionInitialAsync','QuestionFollowUpAsync','QuestionMakeCardsAsync','ConfirmFlashcardDraftAsync','CancelFlashcardDraftAsync','WaitQuestionProjectionAsync','WaitFlashcardDraftActionAsync')
        draftType = 'FlashcardDraft'
        draftFields = @('sourceOperation','token','revision','draftSha256','cards[]')
        cardFields = @('cardId','revision','cardSha256','front','back')
        projectionType = 'QuestionProjectionSnapshot'
        projectionFields = @('operationId','state','cursor','notice','answerArtifact','cardBundleArtifact','destinationEvidenceArtifact','terminalCommandId','cardDraft','ankiSemanticReadback')
    }
    invariants = [ordered]@{
        initial = 'answer only; exporter=0'
        followUp = 'new immutable answer revision only; exporter=0'
        makeCards = 'shared destination-neutral FlashcardDraft; exporter=0'
        confirm = 'operationId+token+revision+draftSha256; explicit destination authorization; exactly one bounded export'
        cancel = 'zero destination requests, attempts, and writes'
        restart = 'same durable draft token, revision, digest, and cards'
        staleOrDoubleConfirm = 'fail-closed before exporter'
        desktop = 'render Front/Back from CardDraft; never compose card fields in UI'
    }
})

WriteJson (Join-Path $out 'fake-gate-binding.v1.json') ([ordered]@{
    schema = 'kc.question-flashcard-fake-gate-binding/1'
    source = [ordered]@{ candidate = $candidateRoot; candidateManifestSha256 = Hash $candidateManifest; receipt = $receiptPath; receiptSha256 = Hash $receiptPath }
    draft = $receipt.question.draft.cardDraft
    recoveredDraft = $receipt.question.recoveredDraft.cardDraft
    confirmed = $receipt.question.confirmed
    staleTokenRejectedBeforeExporter = $receipt.question.staleTokenRejectedBeforeExporter
    doubleConfirmRejected = $receipt.question.doubleConfirmRejected
    cancelled = $receipt.question.cancelled
})

$readable = @"
# Question Flashcard SDK freeze

Desktop uses the same `FlashcardDraft` preview component as read/朗读. `QuestionInitialAsync` and `QuestionFollowUpAsync` only update immutable answer artifacts. `QuestionMakeCardsAsync` stops at `WaitingInput` with `CardDraft`; it does not export. Render the typed draft, then call `ConfirmFlashcardDraftAsync` or `CancelFlashcardDraftAsync`, and await the action with `WaitFlashcardDraftActionAsync` so an older draft terminal cannot win the observation race.

The UI must not construct Front/Back. Confirmation is bound to operationId, token, revision, and draftSha256. Restart restores the same draft. Stale and duplicate confirmation fail before the exporter; cancellation has zero destination effects.
"@
Set-Content -LiteralPath (Join-Path $out 'README.md') -Value $readable -Encoding utf8NoBOM

$files = Get-ChildItem -LiteralPath $out -File | Where-Object Name -ne 'freeze-manifest.json' | ForEach-Object {
    [ordered]@{ path = $_.Name; bytes = $_.Length; sha256 = Hash $_.FullName }
} | Sort-Object path
WriteJson (Join-Path $out 'freeze-manifest.json') ([ordered]@{
    schema = 'kc.question-flashcard-sdk-freeze-manifest/1'; freezeId = $id; createdAt = (Get-Date).ToUniversalTime().ToString('o')
    source = [ordered]@{ candidate = $candidateRoot; candidateManifestSha256 = Hash $candidateManifest; fakeReceipt = $receiptPath; fakeReceiptSha256 = Hash $receiptPath }
    files = $files
})
Write-Output "SDK_FREEZE $out"
Write-Output "MANIFEST_SHA256 $(Hash (Join-Path $out 'freeze-manifest.json'))"

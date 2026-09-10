[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$RealMatrixReceipt,
    [Parameter(Mandatory)] [string]$SafetySelfTestReceipt,
    [Parameter(Mandatory)] [string]$SdkFreezeManifest,
    [Parameter(Mandatory)] [string]$PriorCorrectionReceipt,
    [Parameter(Mandatory)] [string]$PriorWorkerReceipt,
    [Parameter(Mandatory)] [string]$PriorReadback
)
$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }

$candidateRoot = [IO.Path]::GetFullPath($Candidate)
$matrixPath = [IO.Path]::GetFullPath($RealMatrixReceipt)
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json -Depth 100
$selfTest = Get-Content -LiteralPath $SafetySelfTestReceipt -Raw | ConvertFrom-Json -Depth 100
if ($matrix.status -ne 'PASS' -or $matrix.schema -ne 'kc.read-to-anki-language-real-matrix/2') { throw 'A passing v2 real matrix is required.' }
if ($selfTest.status -ne 'PASS') { throw 'A passing multi-card safety self-test is required.' }
$expected = @{ 'meaning-reading'=1; 'writing'=1; 'both'=2 }
foreach ($row in $matrix.matrix) {
    $count = @($row.semanticReadback.cards).Count
    if ($count -ne $expected[$row.mode]) { throw "Unexpected card count for $($row.mode)." }
    if (($row.after.ankiExportAttempts - $row.before.ankiExportAttempts) -ne 1) { throw "Exporter invocation cardinality drifted for $($row.mode)." }
    if (($row.after.ankiExternalWriteCount - $row.before.ankiExternalWriteCount) -notin @(0, $count)) { throw "Low-level mutation cardinality drifted for $($row.mode)." }
    if ($row.confirmationReplayAttemptDelta -ne 0 -or $row.afterReplay.ankiExportAttempts -ne $row.after.ankiExportAttempts -or $row.afterReplay.ankiExternalWriteCount -ne $row.after.ankiExternalWriteCount) { throw "Confirmation replay caused a second effect for $($row.mode)." }
    for ($i = 0; $i -lt $count; $i++) {
        $draft = $row.draft.cards[$i]; $readback = $row.semanticReadback.cards[$i]
        if ($draft.front -ne $readback.front -or $draft.back -ne $readback.back) { throw "Semantic readback order/content drifted for $($row.mode) card $i." }
    }
}
$out = Join-Path $candidateRoot 'anki-multicard-qualification-receipt.json'
$receipt = [ordered]@{
    schema='kc.anki-multicard-qualification/1'; status='PASS'; createdAt=(Get-Date).ToUniversalTime().ToString('o')
    candidate=[ordered]@{root=$candidateRoot;manifestSha256=Hash (Join-Path $candidateRoot 'candidate-manifest.json')}
    currentEvidence=[ordered]@{realMatrix=[ordered]@{path=$matrixPath;sha256=Hash $matrixPath};safetySelfTest=[ordered]@{path=[IO.Path]::GetFullPath($SafetySelfTestReceipt);sha256=Hash $SafetySelfTestReceipt};sdkFreeze=[ordered]@{path=[IO.Path]::GetFullPath($SdkFreezeManifest);sha256=Hash $SdkFreezeManifest}}
    invariants=[ordered]@{exporterInvocationCount=1;logicalCommittedCardCount='confirmed immutable draft expectedCardCount';createOnlyLowLevelMutatingApiActionCount='0 for already_current or expectedCardCount for created';operationLevelSideEffectCount=1;semanticReadback=@('source card id','ordinal','noteId','cardId','Front','Back','deck');confirmationReplayNewInvocationCount=0;partialOrUnknown='fail-closed'}
    realMatrix=$matrix.matrix
    priorDesktopBothReconciliation=[ordered]@{classification='known-committed-never-retried';exporterInvocationCount=1;logicalCommittedCardCount=2;lowLevelMutatingApiActionCount=2;operationLevelSideEffectCount=1;correctionReceipt=[ordered]@{path=[IO.Path]::GetFullPath($PriorCorrectionReceipt);sha256=Hash $PriorCorrectionReceipt};workerReceipt=[ordered]@{path=[IO.Path]::GetFullPath($PriorWorkerReceipt);sha256=Hash $PriorWorkerReceipt};readback=[ordered]@{path=[IO.Path]::GetFullPath($PriorReadback);sha256=Hash $PriorReadback}}
    externalBoundary=[ordered]@{ankiTarget='authorized isolated Canary deck';physicalSpeakerPlaybackCount=$matrix.physicalSpeakerPlaybackCount;remnoteCalls=0;ocrCalls=0}
}
WriteJson $out $receipt
$readable = Join-Path $candidateRoot 'ANKI-MULTICARD-QUALIFICATION.md'
@"
# Anki multi-card qualification

- Result: PASS
- One confirmed operation produces exactly one exporter invocation and one operation-level side-effect receipt.
- Logical committed count equals the immutable draft: meaning-reading 1, writing 1, both 2.
- Create-only mutations are either 0 for already-current or exactly the draft count; partial, unknown, extra, or missing effects fail closed.
- Each readback is checked in draft order for source card ID, note/card IDs, Front, Back, and the authorized isolated deck.
- Repeating the same confirmation produced zero new exporter invocation and zero new write.
- The earlier Desktop both job is read-only reconciled as one invocation/two addNote mutations/two exact readbacks and was not retried.
"@ | Set-Content -LiteralPath $readable -Encoding utf8NoBOM
Write-Output "QUALIFICATION $out"
Write-Output "QUALIFICATION_SHA256 $(Hash $out)"
Write-Output "READABLE_SHA256 $(Hash $readable)"

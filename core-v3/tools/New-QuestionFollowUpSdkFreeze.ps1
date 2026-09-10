[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$RealReceipt,
    [string]$OutputRoot = 'D:\Artifacts\KnowledgeClipper\question-followup-sdk'
)
$ErrorActionPreference='Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path,[object]$Value) { $Value|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
$candidate=[IO.Path]::GetFullPath($Candidate);$receiptPath=[IO.Path]::GetFullPath($RealReceipt)
$receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json -Depth 100
if($receipt.status-ne'PASS'-or $receipt.invariants.initialDestinationWrites-ne 0-or $receipt.invariants.followUpDestinationWrites-ne 0-or $receipt.invariants.confirmAnkiAttemptDelta-ne 1-or $receipt.invariants.confirmReplayAttemptDelta-ne 0-or $receipt.invariants.confirmReplayNoteDelta-ne 0){throw 'Real question follow-up invariants did not pass.'}
$manifest=Join-Path $candidate 'candidate-manifest.json';$client=Join-Path $candidate 'core-v3\KnowledgeClipper.Core.V3.Client.dll'
$id='question-followup-sdk-'+(Get-Date -Format 'yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8);$out=Join-Path $OutputRoot $id;New-Item -ItemType Directory -Path $out|Out-Null
WriteJson (Join-Path $out 'sdk-contract.v2.json') ([ordered]@{
 schema='kc.question-followup-flashcard-sdk-freeze/2';assembly=[ordered]@{path='candidate/core-v3/KnowledgeClipper.Core.V3.Client.dll';sha256=Hash $client}
 workflow=[ordered]@{initial='QuestionInitialAsync(question,context,ankiConfiguration) -> Answer + CardDraft';followUp='QuestionFollowUpAsync(session,followUp,ankiConfiguration) -> Answer + newer CardDraft';enterBehavior='submit follow-up text';confirm='ConfirmFlashcardDraftAsync(latest operationId,draft,explicit authorization)';replay='same confirmation request is idempotent'}
 invariants=[ordered]@{initialExporterAttempts=0;followUpExporterAttempts=0;oldDraft='stale after newer session revision';confirmAnkiAttempts=1;confirmReplayNewAttempts=0;confirmReplayNewNotes=0;configuration='SHA-bound immutable artifact';uiComposesCards=$false}
})
WriteJson (Join-Path $out 'real-evidence.v1.json') ([ordered]@{schema='kc.question-followup-flashcard-real-evidence/1';candidateManifestSha256=Hash $manifest;receiptSha256=Hash $receiptPath;sessionId=$receipt.sessionId;original=$receipt.original;initialAnswer=$receipt.initialAnswer;initialDraft=$receipt.initialDraft;followUp=$receipt.followUp;followUpAnswer=$receipt.followUpAnswer;updatedDraft=$receipt.updatedDraft;semanticReadback=$receipt.semanticReadback;metrics=$receipt.metrics})
Set-Content -LiteralPath (Join-Path $out 'README.md') -Encoding utf8NoBOM -Value "# Question follow-up SDK freeze`n`nEnter submits a follow-up command and replaces the preview with a new immutable draft revision. Initial and follow-up never write Anki. Only explicit confirmation of the latest draft writes once; replay creates no attempt or note.`n"
$files=Get-ChildItem $out -File|Where-Object Name -ne 'freeze-manifest.json'|ForEach-Object{[ordered]@{path=$_.Name;bytes=$_.Length;sha256=Hash $_.FullName}}|Sort-Object path
WriteJson (Join-Path $out 'freeze-manifest.json') ([ordered]@{schema='kc.question-followup-flashcard-sdk-freeze-manifest/1';freezeId=$id;createdAt=(Get-Date).ToUniversalTime().ToString('o');source=[ordered]@{candidate=$candidate;candidateManifestSha256=Hash $manifest;realReceipt=$receiptPath;realReceiptSha256=Hash $receiptPath};files=$files})
Write-Output "SDK_FREEZE $out";Write-Output "MANIFEST_SHA256 $(Hash (Join-Path $out 'freeze-manifest.json'))"

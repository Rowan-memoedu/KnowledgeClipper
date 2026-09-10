[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$BranchMatrixReceipt,
    [Parameter(Mandatory)] [string]$PriorPositiveRealMatrix,
    [Parameter(Mandatory)] [string]$DesktopFailureRoot
)
$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
$candidateRoot=[IO.Path]::GetFullPath($Candidate); $branchPath=[IO.Path]::GetFullPath($BranchMatrixReceipt); $realPath=[IO.Path]::GetFullPath($PriorPositiveRealMatrix)
$branch=Get-Content -LiteralPath $branchPath -Raw|ConvertFrom-Json -Depth 100; $real=Get-Content -LiteralPath $realPath -Raw|ConvertFrom-Json -Depth 100
if($branch.status -ne 'PASS' -or @($branch.cases).Count -ne 4){throw 'Passing four-case branch matrix required.'}
if($real.status -ne 'PASS'){throw 'Passing predecessor positive real matrix required.'}
$out=Join-Path $candidateRoot 'read-aloud-branch-qualification-receipt.json'
WriteJson $out ([ordered]@{
 schema='kc.read-aloud-branch-qualification/1';status='PASS';createdAt=(Get-Date).ToUniversalTime().ToString('o')
 candidate=[ordered]@{root=$candidateRoot;manifestSha256=Hash (Join-Path $candidateRoot 'candidate-manifest.json')}
 branchMatrix=[ordered]@{path=$branchPath;sha256=Hash $branchPath;cases=$branch.cases;stateFeatures=$branch.stateFeatures;externalCalls=0;physicalSpeakerPlayback=$false}
 semantics=[ordered]@{speechFailure=[ordered]@{playback='failed';pipeline='awaiting_card_confirmation when Content ready';draftPreserved=$true};contentFailure=[ordered]@{pipeline='partially_failed';preview=$false;exporterAttempts=0};unresolved=[ordered]@{pipeline='awaiting_card_resolution';attentionRequired=$true;staleLanguageChoiceCleared=$true;exporterAttempts=0};cardsOnly=[ordered]@{publicEntry='ClipCardsAsync';playback='not_requested'};confirmCancelReplayRegression=$true}
 predecessorPositiveMatrix=[ordered]@{classification='carried-forward-positive-evidence';path=$realPath;sha256=Hash $realPath;physicalSpeakerPlaybackCount=$real.physicalSpeakerPlaybackCount}
 triggeringDesktopFailure=[ordered]@{root=[IO.Path]::GetFullPath($DesktopFailureRoot);classification='preserved-not-retried';ankiWrites=0}
})
Write-Output "QUALIFICATION $out";Write-Output "QUALIFICATION_SHA256 $(Hash $out)"

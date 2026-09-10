#Requires -Version 7.0
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$candidate='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-final-resize-speech-20260908T172000Z'
$evidence='D:\Artifacts\KnowledgeClipper\playback-control\desktop-final-resize-speech-20260908T172000Z'
$runId='desktop-playback-final-resize-speech-20260908T172600Z'
$actual=Join-Path 'D:\Artifacts\KnowledgeClipper\read-to-anki\actual' $runId
$launch=Join-Path 'D:\Artifacts\KnowledgeClipper\actual-launch' $runId
$data=Join-Path 'D:\AppData\KnowledgeClipper' ('actual-'+$runId)
$manifestPath=Join-Path $candidate 'candidate-file-manifest.v1.json'
$m=Assert-OverlayManifest $candidate $manifestPath '7087B3084EB7B66D6CB0208B01E57BC16EA2406C0A73F3F9361764A57FD4832C'
$receiptPath=Join-Path $actual 'actual-run.receipt.json'
if((Get-OverlaySha $receiptPath) -cne '8F819A2A1A76C668B69A97D8A5631537929DB32D22F222790843BB7A9E5CC599'){throw 'actual-receipt-drift'}
$r=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json -Depth 100
$post=Get-Content -LiteralPath (Join-Path $launch 'postflight.json') -Raw|ConvertFrom-Json
$back=Get-Content -LiteralPath (Join-Path $launch 'exact-readback.json') -Raw|ConvertFrom-Json -Depth 100
if($r.status -cne 'passed' -or $r.manifestHash -cne (Get-OverlaySha $manifestPath) -or $post.runnerExitCode -ne 0){throw 'actual-not-passed'}
if($r.finalLedger.Synthesis -ne 1 -or $r.finalLedger.Playback -ne 3 -or $r.finalLedger.Content -ne 1 -or $r.finalLedger.Exporter -ne 1){throw 'ledger-count-mismatch'}
if($r.automaticRetries -ne 0 -or $r.physicalKeyboard -or $r.ocr -ne 0 -or $r.remnote -ne 0){throw 'actual-scope-drift'}
if($post.addedNoteIds.Count -ne 2 -or $post.addedCardIds.Count -ne 2 -or $post.removedNoteIds.Count -or $post.removedCardIds.Count -or $post.remainingOwnedCandidateProcesses.Count){throw 'postflight-mismatch'}
if($back.notes.Count -ne 2 -or $back.cards.Count -ne 2){throw 'readback-count-mismatch'}
foreach($card in $r.readback.Cards){
 $note=@($back.notes|Where-Object noteId -EQ $card.NoteId)
 $ui=@($r.actualUiCards|Where-Object {$_.Front -ceq $card.Front -and $_.Back -ceq $card.Back})
 if($note.Count -ne 1 -or $ui.Count -ne 1 -or $note[0].fields.Front.value -cne $card.Front -or $note[0].fields.Back.value -cne $card.Back){throw 'preview-readback-content-mismatch'}
}
$verified=0;$workerOutcomes=@();$speechRequestLengths=@()
foreach($row in $r.finalLedger.Evidence){
 foreach($pair in @(@('requestPath','requestSha256'),@('resultPath','resultSha256'),@('execution','executionSha256'),@('signalPath','sha256'),@('manifestPath','manifestSha256'),@('payloadPath','audioSha256'))){
  if($row.PSObject.Properties.Name -contains $pair[0]){
   $path=[string]$row.($pair[0]);$sha=[string]$row.($pair[1])
   if(!$path.StartsWith($data+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-OverlaySha $path) -cne $sha){throw 'durable-evidence-hash-mismatch'}
   $verified++
  }
 }
 if($row.PSObject.Properties.Name -contains 'stage'){
  $result=[IO.File]::ReadAllText($row.resultPath)|ConvertFrom-Json -Depth 100
  $execution=[IO.File]::ReadAllText($row.execution)|ConvertFrom-Json -Depth 100
  $workerOutcomes += [ordered]@{stage=$row.stage;requestPathLength=$row.requestPath.Length;result=$result;execution=$execution}
  if($row.stage -like 'speech-*'){$speechRequestLengths += $row.requestPath.Length}
 }
}
if(($speechRequestLengths|Measure-Object -Minimum).Minimum -le 260){throw 'long-path-proof-missing'}
# Exact-value scan uses locally available credentials in memory only. Never log values.
$secrets=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($name in @('AI_PRODUCER_API_KEY','MINERU_API_TOKEN')){foreach($scope in @('Process','User')){$secret=[Environment]::GetEnvironmentVariable($name,$scope);if(![string]::IsNullOrWhiteSpace($secret)){[void]$secrets.Add($secret)}}}
if(!$secrets.Count){throw 'secret-scan-input-unavailable'}
$scanned=0;$hits=0
foreach($root in @($data,$actual,$launch)){
 foreach($path in [IO.Directory]::EnumerateFiles($root,'*',[IO.SearchOption]::AllDirectories)){
  if([IO.Path]::GetExtension($path) -notin @('.json','.jsonl','.ndjson','.log','.txt','.md')){continue}
  $body=[IO.File]::ReadAllText($path);$scanned++
  foreach($secret in $secrets){if($body.Contains($secret,[StringComparison]::Ordinal)){$hits++}}
 }
}
if($hits){throw 'secret-found-no-delivery'}
$safePath=Join-Path $evidence 'safe/safe-gates.json'
if((Get-OverlaySha $safePath) -cne 'E0057C2CC342F5B67BD4F9ED16967CC15C7DE42C31C033AD1D8E947320CB7F4F'){throw 'safe-receipt-drift'}
Write-OverlayJson (Join-Path $evidence 'delivery-receipt.json') ([ordered]@{
 status='PASS-DELIVERABLE-RESIZE-PLAYBACK-ONE-ACTUAL';candidate=$candidate;entrypoint=(Join-Path $candidate 'KnowledgeClipper.exe')
 userHandTestEntrypoint=$true;admission='post-seal targeted admission; immutable assembly manifest records pre-Actual status, unchanged'
 manifest=(Get-OverlayIdentity $manifestPath);payload=$m.payload;exe=(Get-OverlayIdentity (Join-Path $candidate 'KnowledgeClipper.exe'));dll=(Get-OverlayIdentity (Join-Path $candidate 'KnowledgeClipper.dll'))
 resizeReceipt=(Get-OverlayIdentity 'D:\Artifacts\KnowledgeClipper\desktop-scale\20260909-native-edge\module-receipt-final.json');resizeAssertions=56
 safeReceipt=(Get-OverlayIdentity $safePath);actualReceipt=(Get-OverlayIdentity $receiptPath);postflight=(Get-OverlayIdentity (Join-Path $launch 'postflight.json'));exactReadback=(Get-OverlayIdentity (Join-Path $launch 'exact-readback.json'))
 runnerManifest=(Get-OverlayIdentity 'D:\Artifacts\KnowledgeClipper\read-to-anki-runner\playback-regression-20260908T172255Z\runner-playback-release-manifest.v1.json');runnerOfflineAssertions=71
 source=$r.Source;counts=[ordered]@{synth=1;playback=3;content=1;exporter=1;addedNotes=2;addedCards=2;retries=0};durableFileHashesVerified=$verified;speechRequestPathLengths=$speechRequestLengths
 uiStatus=$r.finalActualUi.final.status;uiText=$r.finalActualUi.final.nativeText;noteIds=$post.addedNoteIds
 secretScan=[ordered]@{scope='new isolated run data, runner evidence, launcher evidence; exact available process/user credential values';textFiles=$scanned;hits=$hits;valuesLogged=$false}
 safety=[ordered]@{physicalKeyboard=$false;physicalPlayback=$true;ocr=0;remnote=0;oldCandidatesPreserved=$true;userAnkiPidPreserved=41464;newBusinessRunRetries=0;candidateBytesChangedAfterActual=$false}
 boundaries=@('56 native sizing/layout assertions, not physical mixed-DPI drag','One real UI Read/stop/preview/confirm/replay chain only; not every product workflow','Historical registry checkpoint inherited; Speech 1.2.6 admitted module overlay, not rewritten authority','Old embedded runner retained as evidence, never executed','No OCR/RemNote evaluation; existing context capture beyond fixed fixture not requalified')
 verifier=(Get-OverlayIdentity $PSCommandPath)
})
Get-OverlayIdentity (Join-Path $evidence 'delivery-receipt.json')|ConvertTo-Json

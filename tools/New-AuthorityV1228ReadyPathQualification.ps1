[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference='Stop';$utf8=[Text.UTF8Encoding]::new($false)
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()}
$root=[IO.Path]::GetFullPath($EvidenceRoot)
$reconciliation=Join-Path $root 'ready-path-reconciliation-receipt.json'
$replay=Join-Path $root 'same-submit-replay-v2/replay-receipt.json'
$failure=Join-Path $root 'failure-receipt.json'
foreach($path in @($reconciliation,$replay,$failure)){if(-not(Test-Path -LiteralPath $path)){throw "Evidence missing: $path"}}
$r=Get-Content -Raw -LiteralPath $reconciliation|ConvertFrom-Json -Depth 100
$p=Get-Content -Raw -LiteralPath $replay|ConvertFrom-Json -Depth 100
if($r.status -ne 'succeeded' -or $p.status -ne 'passed' -or $p.effectCounts.newWorkerAttempts -ne 0 -or $p.effectCounts.newAnkiNotes -ne 0 -or -not $p.independentAnkiReadback.notesInfo -or -not $p.independentAnkiReadback.cardsInfo){throw 'Ready-path reconciliation evidence does not meet qualification requirements.'}
$out=Join-Path $root 'superseding-ready-path-qualification-receipt.json';if(Test-Path -LiteralPath $out){throw "Append-only target exists: $out"}
$value=[ordered]@{schemaVersion='sealed-live-ocr-anki-v1228-ready-path-qualification.v1';status='conditional';generatedAtUtc=[DateTime]::UtcNow.ToString('o');authority=[ordered]@{version='1.2.28';registrySha256='F7989A49A2F348031EFEA488FA7DE8885894494F8773E71A241C5698A7A66CB4';checkpointSha256='1797D375B750FE8A754BD9DDB9E55ED684E73B6D01333E010BD38F63630AE75B'};actualReadyPath=[ordered]@{status='succeeded';mineruSubmissions=1;content='content.deepseek@0.9.3 ready';anki='export.anki@1.3.2 addNote=1 with initial and independent notesInfo/cardsInfo';remnoteWrites=0;speakerInvocations=0};idempotency=[ordered]@{sameOriginalSubmitCommand=$true;replayReceipt=[ordered]@{path=$replay;sha256=Sha $replay};newWorkerAttempts=0;newAnkiNotes=0};semanticBoundary=[ordered]@{status='conditional';reason='The frozen CANAR live image did not cause Content 0.9.3 to report unresolved; it preserved CANAR and produced ready output.';humanResolutionAcceptOriginalCovered=$false;contentQualityPass=$false;forbiddenClaims=@('human-resolution continuation passed','CANAR was corrected','live AI ambiguity behavior is stable')};supersedesHarnessFailureOnly=[ordered]@{path=$failure;sha256=Sha $failure;meaning='The initial script incorrectly required unresolved and therefore exited after a completed ready task; it does not invalidate observed worker effects.'};evidence=[ordered]@{readyPathReconciliation=[ordered]@{path=$reconciliation;sha256=Sha $reconciliation}}}
[IO.File]::WriteAllText($out,(($value|ConvertTo-Json -Depth 100)+"`n"),$utf8);Write-Output $out;Write-Output(Sha $out)

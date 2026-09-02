[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative='contracts/core/v2.7/checkpoints/v2.7.14-20260901T015000Z/checkpoint.json';$targetRelative='contracts/core/v2.7/checkpoints/v2.7.15-20260901T021500Z/checkpoint.json'
$source=Join-Path $ProjectRoot $sourceRelative.Replace('/','\');$target=Join-Path $ProjectRoot $targetRelative.Replace('/','\')
if(!(Test-Path -LiteralPath $source -PathType Leaf) -or (Test-Path -LiteralPath $target)){throw 'Core v2.7.15 checkpoint precondition failed.'}
function Sha([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()}
function Entry([string]$relative){$path=Join-Path $ProjectRoot $relative.Replace('/','\');if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing checkpoint tree input: $relative"};$file=Get-Item -LiteralPath $path;[ordered]@{path=$relative;bytes=[long]$file.Length;sha256=(Sha $path).ToLowerInvariant()}}
$doc=Get-Content -LiteralPath $source -Raw|ConvertFrom-Json -AsHashtable -Depth 100
$oldAnkiReceipt='workers/anki-exporter/canary/sealed-initial-alias-v1.3.1/20260830T123343Z/qualification-receipt.json';$newAnkiReceipt='workers/anki-exporter/canary/sealed-versioned-flat-path-v1.3.2/20260831T104616Z/qualification-receipt.json'
$files=@(foreach($sourceFile in $doc.tree.files){$relative=[string]$sourceFile.path;if($relative -eq $oldAnkiReceipt){$relative=$newAnkiReceipt};Entry $relative})
$rows=[string[]]@($files|ForEach-Object{"$($_.path)`t$($_.bytes)`t$($_.sha256)"});[Array]::Sort($rows,[StringComparer]::Ordinal)
$treeSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($rows -join "`n")+"`n")))).ToLowerInvariant();$bytes=[long]0;foreach($file in $files){$bytes += [long]$file.bytes}
$ankiRelative='releases/knowledge-clipper-anki-exporter-v1.3.2-20260831T104520Z/win-x64/workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe';$ankiExe=Join-Path $ProjectRoot $ankiRelative.Replace('/','\');$ankiText=(& $ankiExe describe --json 2>&1)-join [Environment]::NewLine;$ankiDescribe=$ankiText|ConvertFrom-Json -Depth 50
if($LASTEXITCODE -ne 0 -or $ankiDescribe.workerId -ne 'export.anki' -or $ankiDescribe.workerVersion -ne '1.3.2'){throw 'Anki 1.3.2 formal describe identity failed.'}
$doc.checkpointId='core-v2.7.15-20260901T021500Z';$doc.checkpointVersion='2.7.15';$doc.createdAt='2026-09-01T02:15:00+08:00';$doc.generatedAt='2026-09-01T02:15:00+08:00';$doc.supersedes=[ordered]@{path=$sourceRelative;sha256=Sha $source}
$doc.authorityBoundary.currentAuthority='1.2.24';$doc.authorityBoundary.intendedAuthority='1.2.28';$doc.authorityBoundary.promotion='pending-append-only-authority-assembly'
$doc.modules.anki=[ordered]@{workerId='export.anki';workerVersion='1.3.2';executable=$ankiRelative;bytes=(Get-Item -LiteralPath $ankiExe).Length;sha256=Sha $ankiExe;describe=$ankiDescribe}
$doc.moduleReceipts.anki=[ordered]@{path=$newAnkiReceipt;sha256=Sha (Join-Path $ProjectRoot $newAnkiReceipt.Replace('/','\'));treeSha256='A360A3381E3A4CC33C0AE2ECFDD4F29950EC2467E290AEC69E4163CD6BF1FD2C'}
$doc.requirements.ocrProofreadSegments=[ordered]@{status='content-0.9.3-segment-identity-bound';contentProducerPairIntegrity='content.deepseek-0.9.3-admitted';sourceAndProofreadDisjoint=$true;unitAndCardReference='active-rseg-or-hseg-only';tamperMissingDuplicateFailClosed=$true;destinationWorkerStarted=$false}
$doc.tree=[ordered]@{fileCount=$files.Count;totalBytes=$bytes;treeSha256=$treeSha;files=$files}
[IO.Directory]::CreateDirectory((Split-Path -Parent $target))|Out-Null;[IO.File]::WriteAllText($target,(($doc|ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{status='passed';checkpoint=$targetRelative;sha256=Sha $target;tree=[ordered]@{files=$files.Count;bytes=$bytes;sha256=$treeSha};content=$doc.modules.content;anki=$doc.modules.anki}|ConvertTo-Json -Depth 30

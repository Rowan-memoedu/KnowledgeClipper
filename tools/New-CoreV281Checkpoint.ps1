[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'; $ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative='contracts/core/v2.8/checkpoints/v2.8.0-20260901T053600Z/checkpoint.json'; $targetRelative='contracts/core/v2.8/checkpoints/v2.8.1-20260901T054000Z/checkpoint.json'
$source=Join-Path $ProjectRoot $sourceRelative.Replace('/','\'); $target=Join-Path $ProjectRoot $targetRelative.Replace('/','\')
if(!(Test-Path -LiteralPath $source -PathType Leaf) -or (Test-Path -LiteralPath $target)){throw 'Append-only Core v2.8.1 checkpoint precondition failed.'}
function Sha($p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant()}
function Tree($files){$rows=[string[]]@($files|ForEach-Object{"$($_.path)`t$($_.bytes)`t$($_.sha256)"});[Array]::Sort($rows,[StringComparer]::Ordinal);[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($rows -join "`n")+"`n")))).ToLowerInvariant()}
$doc=Get-Content -LiteralPath $source -Raw|ConvertFrom-Json -AsHashtable -Depth 100;$files=@($doc.tree.files|ForEach-Object{[ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=[string]$_.sha256}})
$doc.checkpointId='core-v2.8.1-20260901T054000Z';$doc.checkpointVersion='2.8.1';$doc.createdAt='2026-09-01T05:40:00+08:00';$doc.generatedAt=$doc.createdAt;$doc.supersedes=[ordered]@{path=$sourceRelative;sha256=Sha $source};$doc.status='passed-offline-authority-admission-ready'
$total=[long]0;foreach($file in $files){$total+=[long]$file.bytes};if($total -le 0){throw 'Checkpoint tree byte accumulation failed.'};$doc.tree=[ordered]@{fileCount=$files.Count;totalBytes=$total;treeSha256=(Tree $files);files=$files}
[IO.Directory]::CreateDirectory((Split-Path -Parent $target))|Out-Null;[IO.File]::WriteAllText($target,(($doc|ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false));[ordered]@{status='passed';checkpoint=$targetRelative;sha256=Sha $target;tree=$doc.tree}|ConvertTo-Json -Depth 12

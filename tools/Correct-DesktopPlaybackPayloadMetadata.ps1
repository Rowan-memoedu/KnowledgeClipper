#Requires -Version 7.0
param([string]$Root='D:\Artifacts\KnowledgeClipper\playback-control\desktop-offline-20260908-01')
$ErrorActionPreference='Stop'
function Sha($p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function SaveNew($name,$value){$p=Join-Path $Root $name;$b=[Text.UTF8Encoding]::new($false).GetBytes(($value|ConvertTo-Json -Depth 50)+"`n");$s=[IO.File]::Open($p,[IO.FileMode]::CreateNew);try{$s.Write($b,0,$b.Length)}finally{$s.Dispose()};@{path=$p;sha256=(Sha $p)}}
$old=Join-Path $Root 'payload-verification.v1.json'
if((Sha $old)-ne '27549E9390C734122BDE8068117B3835D2D11BB58558603C297F12E57B2CBF71'){throw 'original receipt mismatch'}
$v=Get-Content -LiteralPath $old -Raw|ConvertFrom-Json -AsHashtable
[long]$total=0
foreach($r in $v.files){$p=Join-Path $v.payloadRoot $r.path;if((Get-Item -LiteralPath $p).Length-ne $r.bytes-or(Sha $p)-ne $r.sha256){throw 'original payload drift'};$total += [long]$r.bytes}
if(@(Get-ChildItem -LiteralPath $v.payloadRoot -File -Recurse).Count-ne $v.fileCount){throw 'unlisted payload file'}
$v.totalBytes=$total
$v.metadataCorrection=@{supersedes=@{path=$old;sha256=(Sha $old)};reason='null totalBytes corrected with explicit Int64 per-file sum';payloadChanged=$false;testsRerun=$false}
$corrected=SaveNew 'payload-verification.corrected.v2.json' $v
$modulePath=Join-Path $Root 'module-receipt.json';$module=Get-Content -LiteralPath $modulePath -Raw|ConvertFrom-Json -AsHashtable
$module.payload=@{path=$corrected.path;bytes=(Get-Item -LiteralPath $corrected.path).Length;sha256=$corrected.sha256}
$module.metadataCorrection=@{supersedes=@{path=$modulePath;sha256=(Sha $modulePath)};generator=@{path=$PSCommandPath;sha256=(Sha $PSCommandPath)};payloadChanged=$false;testsRerun=$false;fileCount=$v.fileCount;totalBytes=$total}
$moduleCorrection=SaveNew 'module-receipt.corrected.v2.json' $module
$corrected|ConvertTo-Json;$moduleCorrection|ConvertTo-Json
Write-Host ('STATUS VALIDATED totalBytes='+$total)

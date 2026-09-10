#Requires -Version 7.0
[CmdletBinding()]
param(
 [string]$AdmissionPath='D:\Artifacts\KnowledgeClipper\playback-control\speech126-overlay-20260909-01\module-overlay-admission.json',
 [string]$CandidateRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Sha([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Check([bool]$ok,[string]$why){if(-not $ok){throw $why}}
function Under([string]$root,[string]$relative){
 Check (-not[IO.Path]::IsPathRooted($relative)-and $relative.Replace('\','/').Split('/') -notcontains '..') 'unsafe-relative-path'
 $p=[IO.Path]::GetFullPath((Join-Path $root $relative))
 Check ($p.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) 'path-escape'
 $p
}
function Verify([string]$path,[object]$row){Check ((Get-Item -LiteralPath $path).Length-eq$row.bytes-and(Sha $path)-eq$row.sha256) ('file-drift:'+ $path)}
Check ((Sha $AdmissionPath)-eq'F2D1847C5156CD3BE5FC2C4F2EDFB4CDC883102B22F3289C6F4118A5C07D5565') 'admission-receipt-drift'
$a=Get-Content -LiteralPath $AdmissionPath -Raw|ConvertFrom-Json -Depth 100
$target=if([string]::IsNullOrWhiteSpace($CandidateRoot)){$null}else{[IO.Path]::GetFullPath($CandidateRoot)}
Check ((Sha (Join-Path $a.baseline.root 'candidate-file-manifest.v1.json'))-eq$a.baseline.manifestSha256) 'historical-base-drift'
foreach($m in $a.moduleCopies){Verify $m.source $m;if($target){Verify (Under $target $m.destination) $m}}
foreach($m in $a.metadataCopies){Verify (Under $a.metadataRoot $m.path) $m;if($target){Verify (Under $target $m.path) $m}}
foreach($m in $a.unchangedBindings){Verify (Under $a.baseline.root $m.path) $m;if($target){Verify (Under $target $m.path) $m}}
$catalog=Get-Content -LiteralPath (Under $a.metadataRoot 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json') -Raw|ConvertFrom-Json -Depth 100
$binding=Get-Content -LiteralPath (Under $a.metadataRoot 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json') -Raw|ConvertFrom-Json -Depth 100
Check ((Sha (Under $a.metadataRoot $binding.catalogPath))-eq$binding.catalogSha256) 'catalog-binding-drift'
$speech=@($catalog.operations|Where-Object operationId -EQ 'speech.runtime.internal')
Check ($speech.Count-eq1-and$speech[0].workerBinding.versionRange-eq'=1.2.6'-and$speech[0].workerBinding.authority.exactWorkerVersion-eq'1.2.6') 'speech-version-drift'
Check ($speech[0].workerBinding.authority.registrySha256-eq$a.baseline.registrySha256) 'registry-binding-drift'
$worker=@($a.moduleCopies|Where-Object destination -EQ 'authority/win-x64/workers/speech/KnowledgeClipper.Speech.Worker.exe')
Check ($worker.Count-eq1-and$speech[0].workerBinding.authority.entrypointSha256-eq$worker[0].sha256) 'entrypoint-binding-drift'
[ordered]@{status='PASS-READONLY-OVERLAY';admissionSha256=Sha $AdmissionPath;moduleRows=$a.moduleCopies.Count;metadataRows=$a.metadataCopies.Count;unchangedRows=$a.unchangedBindings.Count;assembledCandidate=$target;workerInvocations=0;externalCalls=0;filesWritten=0;productWholeTreeGate='Desktop-owned; not replaced by this overlay subset check'}|ConvertTo-Json -Depth 5

[CmdletBinding()]
param(
 [string]$DesktopSource='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-20260904T104202Z',
 [string]$CoreSource='D:\Artifacts\KnowledgeClipper\read-to-anki\rta-observation-20260904T122256Z-33da404d',
 [string]$Target='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-observation-20260904T124500Z',
 [string]$Receipt='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\receipts\desktop-rta-observation-20260904T124500Z.receipt.json'
)
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop'
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant()}
function Json([string]$p,[object]$v){$parent=Split-Path -Parent $p;[IO.Directory]::CreateDirectory($parent)|Out-Null;[IO.File]::WriteAllText($p,($v|ConvertTo-Json -Depth 100)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))}
function Inventory([string]$root,[string[]]$exclude){$records=@((Get-ChildItem -LiteralPath $root -File -Recurse)|ForEach-Object{$rel=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/');if($rel-notin$exclude){[pscustomobject]@{path=$rel;bytes=[int64]$_.Length;sha256=Hash $_.FullName}}});[Array]::Sort($records,[Comparison[object]]{param($left,$right)[StringComparer]::Ordinal.Compare([string]$left.path,[string]$right.path)});$records}
function Normalize-InventoryRecord([object]$record){
 $path=[string]$record.path;$sha=[string]$record.sha256;$bytes=[Convert]::ToInt64($record.bytes,[Globalization.CultureInfo]::InvariantCulture)
 if([string]::IsNullOrWhiteSpace($path)-or$bytes-lt0-or$sha-notmatch'^[A-Fa-f0-9]{64}$'){throw 'inventory-record-invalid'}
 [pscustomobject]@{path=$path;bytes=[int64]$bytes;sha256=$sha.ToUpperInvariant()}
}
function Sum-InventoryBytes([object[]]$records){[int64]$sum=0;foreach($record in @($records)){[int64]$value=(Normalize-InventoryRecord $record).bytes;if($value-gt([int64]::MaxValue-$sum)){throw 'inventory-byte-sum-overflow'};$sum+=$value};$sum}
if(Test-Path -LiteralPath $Target){throw 'append-only-target-exists'};if(Test-Path -LiteralPath $Receipt){throw 'append-only-receipt-exists'}
$desktopManifest=Join-Path $DesktopSource 'candidate-file-manifest.v1.json';$coreManifest=Join-Path $CoreSource 'candidate-manifest.json'
if((Hash $desktopManifest)-ne'135F782ECDEA09239D24D335896FE70BC21DE872B0D8A375B9C6B5E154024FE7'){throw 'desktop-source-manifest-drift'}
if((Hash $coreManifest)-ne'D4D06466007470125BAEA46CF89E3F70070E5E64C457D2533A65E4C24911DB34'){throw 'core-source-manifest-drift'}
[IO.Directory]::CreateDirectory($Target)|Out-Null
Copy-Item -Path (Join-Path $DesktopSource '*') -Destination $Target -Recurse -Force
foreach($relative in @('authority','core-v3')){$path=Join-Path $Target $relative;$resolved=[IO.Path]::GetFullPath($path);if(!$resolved.StartsWith([IO.Path]::GetFullPath($Target)+[IO.Path]::DirectorySeparatorChar)){throw 'unsafe-rebase-target'};Remove-Item -LiteralPath $resolved -Recurse -Force;Copy-Item -LiteralPath (Join-Path $CoreSource $relative) -Destination $resolved -Recurse}
Copy-Item -Path (Join-Path $CoreSource 'contracts\*') -Destination (Join-Path $Target 'contracts') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $CoreSource 'sdk\KnowledgeClipper.Core.V3.Client.dll') -Destination (Join-Path $Target 'KnowledgeClipper.Core.V3.Client.dll') -Force
Copy-Item -LiteralPath (Join-Path $CoreSource 'sdk\KnowledgeClipper.Core.V3.Contracts.dll') -Destination (Join-Path $Target 'KnowledgeClipper.Core.V3.Contracts.dll') -Force
Remove-Item -LiteralPath (Join-Path $Target 'candidate-file-manifest.v1.json'),(Join-Path $Target 'core-read-to-anki-candidate-manifest.json'),(Join-Path $Target 'core-read-to-anki-sdk-freeze-manifest.json') -Force
Copy-Item -LiteralPath $coreManifest -Destination (Join-Path $Target 'core-observation-candidate-manifest.json')
Copy-Item -LiteralPath (Join-Path $CoreSource 'dependency-manifest.v2.json') -Destination (Join-Path $Target 'core-observation-dependency-manifest.v2.json')
Copy-Item -LiteralPath (Join-Path $CoreSource 'sdk\sdk-binding.v1.json') -Destination (Join-Path $Target 'core-observation-sdk-binding.v1.json')
$core=Get-Content -Raw $coreManifest|ConvertFrom-Json -Depth 100
$integration=Get-Content -Raw (Join-Path $Target 'candidate-integration.json')|ConvertFrom-Json -Depth 30
$integration.PipeName='KnowledgeClipper.CoreV3.desktop.rta.observation.20260904T124500Z';$integration.AuthorityCheckpointId=$core.candidateId;$integration.AuthorityTreeSha256=$core.authority.treeSha256
Json (Join-Path $Target 'candidate-integration.json') $integration
$files=Inventory $Target @('candidate-file-manifest.v1.json')
$lines=$files|ForEach-Object{"$($_.path)`t$($_.bytes)`t$($_.sha256)`n"};$tree=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($lines-join''))))
$manifest=[ordered]@{schemaVersion='knowledge-clipper-integrated-desktop-candidate.v1';candidateId='desktop-rta-observation-20260904T124500Z';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');supersedes='desktop-rta-20260904T104202Z';desktopSource=[ordered]@{root=$DesktopSource;manifestSha256=Hash $desktopManifest};coreSource=[ordered]@{root=$CoreSource;manifestSha256=Hash $coreManifest;candidateId=$core.candidateId;payloadTreeSha256=$core.payload.treeSha256;sdkFreezeManifestSha256=$core.observationSdk.freezeManifestSha256};payload=[ordered]@{files=$files.Count;bytes=Sum-InventoryBytes $files;treeSha256=$tree;treeAlgorithm='sha256(utf8 ordinal(path TAB int64(bytes) TAB uppercase(sha256) LF))'};files=$files;userHandTestEntrypoint=$false;actualExecuted=$false}
Json (Join-Path $Target 'candidate-file-manifest.v1.json') $manifest
$physical=Inventory $Target @('candidate-file-manifest.v1.json');if(($physical.path-join"`n")-cne($files.path-join"`n")){throw 'physical-declared-drift'}
$receiptObject=[ordered]@{schemaVersion='knowledge-clipper-desktop-observation-rebase-receipt.v1';status='PASS-SEALED-NOT-RUN';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');candidateRoot=$Target;manifestSha256=Hash (Join-Path $Target 'candidate-file-manifest.v1.json');payload=$manifest.payload;desktopSourceManifestSha256=$manifest.desktopSource.manifestSha256;coreSourceManifestSha256=$manifest.coreSource.manifestSha256;clientSha256=Hash (Join-Path $Target 'KnowledgeClipper.Core.V3.Client.dll');contractsSha256=Hash (Join-Path $Target 'KnowledgeClipper.Core.V3.Contracts.dll');hostSha256=Hash (Join-Path $Target 'core-v3\KnowledgeClipper.Core.V3.Host.exe');schemas=@('discovery-checkpoint.v1.schema.json','discovery-result.v1.schema.json','settings-evidence.v1.schema.json','projection.v3.schema.json')|ForEach-Object{$p=Join-Path $Target ('contracts\core\v3\operations\clip.read-aloud-cards\'+$_);[ordered]@{path=[IO.Path]::GetRelativePath($Target,$p).Replace('\','/');sha256=Hash $p}};inventory=[ordered]@{missing=0;undeclared=0;hashMismatch=0};actualExecuted=$false;externalCalls=0}
Json $Receipt $receiptObject
Write-Output $Receipt

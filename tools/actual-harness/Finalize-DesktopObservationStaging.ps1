[CmdletBinding()]param(
 [Parameter(Mandatory)][string]$Staging,
 [Parameter(Mandatory)][string]$ExpectedStaging,
 [Parameter(Mandatory)][string]$Receipt,
 [Parameter(Mandatory)][string]$MergeReceipt,
 [Parameter(Mandatory)][string]$IndependentMergeReceipt,
 [Parameter(Mandatory)][string]$DesktopSourceManifest,
 [Parameter(Mandatory)][string]$CoreSourceManifest,
 [Parameter(Mandatory)][string]$ExpectedDesktopSourceManifestSha256,
 [Parameter(Mandatory)][string]$ExpectedCoreSourceManifestSha256,
 [Parameter(Mandatory)][string]$ExpectedDesktopCandidateId,
 [Parameter(Mandatory)][string]$ExpectedCoreCandidateId,
 [Parameter(Mandatory)][string]$ExpectedCorePayloadTreeSha256,
 [Parameter(Mandatory)][string]$ExpectedSdkFreezeManifestSha256,
 [Parameter(Mandatory)][string]$ExpectedMergeReceiptSha256,
 [Parameter(Mandatory)][string]$ExpectedIndependentReceiptSha256,
 [int]$ExpectedPayloadFiles=2997,
 [switch]$ValidateOnly)
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop'
function Get-Sha256($p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant()}
function Get-Inventory($root){$rows=@(Get-ChildItem -LiteralPath $root -File -Recurse|Where-Object{$_.Name-ne'candidate-file-manifest.v1.json'}|ForEach-Object{[pscustomobject]@{path=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/');bytes=[int64]$_.Length;sha256=Get-Sha256 $_.FullName}});[Array]::Sort($rows,[Comparison[object]]{param($a,$b)[StringComparer]::Ordinal.Compare($a.path,$b.path)});$rows}
function Get-Tree($rows){$text=($rows|ForEach-Object{"$($_.path)`t$($_.bytes)`t$($_.sha256)`n"})-join'';[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))}
function Get-Bytes($rows){[int64]$sum=0;foreach($row in $rows){if($row.bytes-gt([int64]::MaxValue-$sum)){throw'inventory-byte-sum-overflow'};$sum+=[int64]$row.bytes};$sum}
$root=[IO.Path]::GetFullPath($Staging);if($root-ne[IO.Path]::GetFullPath($ExpectedStaging)){throw 'staging-target-mismatch'};$manifest=Join-Path $root 'candidate-file-manifest.v1.json';$receiptPath=[IO.Path]::GetFullPath($Receipt)
if(!(Test-Path -LiteralPath $root -PathType Container)){throw'staging-missing'};if(Test-Path -LiteralPath $manifest){throw'manifest-already-exists'};if(Test-Path -LiteralPath $receiptPath){throw'receipt-already-exists'}
if((Get-Sha256 $MergeReceipt)-ne$ExpectedMergeReceiptSha256.ToUpperInvariant()){throw'merge-receipt-sha-mismatch'};if((Get-Sha256 $IndependentMergeReceipt)-ne$ExpectedIndependentReceiptSha256.ToUpperInvariant()){throw'independent-receipt-sha-mismatch'}
$desktopSourceSha=Get-Sha256 $DesktopSourceManifest;$coreSourceSha=Get-Sha256 $CoreSourceManifest
if($desktopSourceSha-ne$ExpectedDesktopSourceManifestSha256.ToUpperInvariant()){throw'desktop-source-manifest-sha-mismatch'};if($coreSourceSha-ne$ExpectedCoreSourceManifestSha256.ToUpperInvariant()){throw'core-source-manifest-sha-mismatch'}
$desktopSource=Get-Content -Raw $DesktopSourceManifest|ConvertFrom-Json -Depth 100;$coreSource=Get-Content -Raw $CoreSourceManifest|ConvertFrom-Json -Depth 100
if($desktopSource.candidateId-ne$ExpectedDesktopCandidateId){throw'desktop-source-candidate-id-mismatch'};if($coreSource.candidateId-ne$ExpectedCoreCandidateId){throw'core-source-candidate-id-mismatch'}
if($coreSource.payload.treeSha256-ne$ExpectedCorePayloadTreeSha256.ToUpperInvariant()){throw'core-source-tree-mismatch'};if($coreSource.observationSdk.freezeManifestSha256-ne$ExpectedSdkFreezeManifestSha256.ToUpperInvariant()){throw'core-sdk-freeze-mismatch'}
$merge=Get-Content -Raw $MergeReceipt|ConvertFrom-Json;$independent=Get-Content -Raw $IndependentMergeReceipt|ConvertFrom-Json
foreach($proof in @($merge,$independent)){if($proof.status-ne'PASS'-or$proof.files-ne$ExpectedPayloadFiles-or@($proof.missing).Count-ne0-or@($proof.extra).Count-ne0-or@($proof.hashMismatch).Count-ne0-or-not$proof.integrationValid-or$proof.candidateManifestPresent-or$proof.actualExecuted-or$proof.externalCalls-ne0){throw'merge-proof-invalid'}}
$before=Get-Inventory $root;if($before.Count-ne$ExpectedPayloadFiles){throw'payload-file-count-drift'};$tree=Get-Tree $before;$bytes=Get-Bytes $before
$provenance=[ordered]@{desktopSource=[ordered]@{manifestPath=[IO.Path]::GetFullPath($DesktopSourceManifest);manifestSha256=$desktopSourceSha;candidateId=$desktopSource.candidateId;payloadTreeSha256=$desktopSource.payload.treeSha256};coreSource=[ordered]@{manifestPath=[IO.Path]::GetFullPath($CoreSourceManifest);manifestSha256=$coreSourceSha;candidateId=$coreSource.candidateId;payloadTreeSha256=$coreSource.payload.treeSha256;sdkFreezeManifestSha256=$coreSource.observationSdk.freezeManifestSha256}}
$candidate=[ordered]@{schemaVersion='knowledge-clipper-integrated-desktop-candidate.v1';candidateId=[IO.Path]::GetFileName($root);createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');sources=$provenance;payload=[ordered]@{files=$before.Count;bytes=$bytes;treeSha256=$tree;treeAlgorithm='sha256(utf8 ordinal(path TAB int64(bytes) TAB uppercase(sha256) LF))'};files=$before;userHandTestEntrypoint=$false;actualExecuted=$false;finalizedFromExistingStaging=$true}
if($ValidateOnly){$candidate|ConvertTo-Json -Depth 5;exit 0}
$manifestJson=($candidate|ConvertTo-Json -Depth 20)+[Environment]::NewLine;[IO.File]::WriteAllText($manifest,$manifestJson,[Text.UTF8Encoding]::new($false))
$after=Get-Inventory $root;if((Get-Tree $after)-ne$tree-or$after.Count-ne$before.Count){throw'payload-mutated-during-finalize'}
$final=[ordered]@{schemaVersion='knowledge-clipper-desktop-existing-staging-finalize-receipt.v1';status='PASS-SEALED-NOT-RUN';candidateRoot=$root;manifestSha256=Get-Sha256 $manifest;sources=$provenance;payload=$candidate.payload;mergeReceiptSha256=Get-Sha256 $MergeReceipt;independentMergeReceiptSha256=Get-Sha256 $IndependentMergeReceipt;payloadMutationCount=0;actualExecuted=$false;externalCalls=0}
[IO.Directory]::CreateDirectory((Split-Path -Parent $receiptPath))|Out-Null;[IO.File]::WriteAllText($receiptPath,(ConvertTo-Json $final -Depth 10)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false));Write-Output $receiptPath

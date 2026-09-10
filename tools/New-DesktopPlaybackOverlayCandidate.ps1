#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$OverlayId,
    [Parameter(Mandatory)][string]$CatalogFile,[Parameter(Mandatory)][string]$CatalogSha,
    [Parameter(Mandatory)][string]$BindingFile,[Parameter(Mandatory)][string]$BindingSha,
    [Parameter(Mandatory)][string]$DependencyFile,[Parameter(Mandatory)][string]$DependencySha,
    [Parameter(Mandatory)][string]$AdmissionFile,[Parameter(Mandatory)][string]$AdmissionSha
)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$base='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-playback-20260908T155328Z'
$baseSha='64F9AC64910BAD44CDD610C7717D185E80E5C60B64EF88F8401A00F6BDC84AE2'
$resize='D:\Artifacts\KnowledgeClipper\desktop-scale\20260909-native-edge'
$resizeSha='577FDBA22723E4397F33E553B57519EDF88C795DB1D4DEC7367ED1C0D5520BB4'
$speech='D:\Artifacts\KnowledgeClipper\speech\r20260908T170550Z'
$candidate=[IO.Path]::GetFullPath($CandidateRoot);$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
if(!$candidate.StartsWith('D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\',[StringComparison]::OrdinalIgnoreCase) -or $candidate -eq $base){throw 'new-candidate-boundary-invalid'}
if([IO.Directory]::Exists($candidate) -or [IO.Directory]::Exists($evidence)){throw 'append-only-output-already-exists'}
$baseManifest=Assert-OverlayManifest $base (Join-Path $base 'candidate-file-manifest.v1.json') $baseSha
$replacements=@{};$sources=@{}
function Add-Replacement([string]$Relative,[string]$Source,[string]$Sha) {
    if($replacements.ContainsKey($Relative)){throw 'duplicate-overlay-path'}
    [void](Resolve-OverlayFile $candidate $Relative)
    $id=Get-OverlayIdentity $Source
    if($id.sha256 -cne $Sha.ToUpperInvariant()){throw ('overlay-source-drift: '+$Source)}
    $replacements[$Relative]=[ordered]@{path=$Relative;bytes=$id.bytes;sha256=$id.sha256}
    $sources[$Relative]=$Source
}
Add-Replacement 'KnowledgeClipper.dll' (Join-Path $resize 'ui-module/KnowledgeClipper.dll') $resizeSha
if((Get-OverlaySha (Join-Path $resize 'module-receipt-final.json')) -cne 'A12234BB2FB38DBBB3926F2E8909A257360A250537B179D3E3142E57EF6FCD11'){throw 'resize-receipt-drift'}
$speechTree=Get-Content -LiteralPath (Join-Path $speech 'release-tree.json') -Raw|ConvertFrom-Json -Depth 100
if($speechTree.treeSha256.ToUpperInvariant() -cne 'ADD6E339138766BE84958F105334CAF5FFFC594123068569A2B290FF3FA8E189' -or $speechTree.files.Count -ne 11){throw 'speech-closure-mismatch'}
$material=[Collections.Generic.List[string]]::new()
foreach($r in $speechTree.files){
    if(!$r.path.StartsWith('workers/speech/',[StringComparison]::Ordinal)){throw 'speech-closure-path-invalid'}
    $path=Resolve-OverlayFile (Join-Path $speech 'win-x64') $r.path
    Test-OverlayFile $path $r
    $material.Add($r.path+'|'+$r.bytes+'|'+$r.sha256)
    Add-Replacement ('authority/win-x64/'+$r.path) $path $r.sha256
}
$speechActual=[IO.Directory]::GetFiles((Join-Path $speech 'win-x64/workers/speech'),'*',[IO.SearchOption]::AllDirectories)
if($speechActual.Count -ne 11){throw 'speech-extra-files'}
if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($material -join "`n")))) -cne $speechTree.treeSha256.ToUpperInvariant()){throw 'speech-tree-drift'}
Add-Replacement 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json' $CatalogFile $CatalogSha
Add-Replacement 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json' $BindingFile $BindingSha
Add-Replacement 'authority/dependency-manifest.v1.json' $DependencyFile $DependencySha
Add-Replacement 'module-overlays/speech-1.2.6/admission.json' $AdmissionFile $AdmissionSha
Add-Replacement 'module-overlays/speech-1.2.6/release-tree.json' (Join-Path $speech 'release-tree.json') (Get-OverlaySha (Join-Path $speech 'release-tree.json'))
Add-Replacement 'module-overlays/resize/module-receipt.json' (Join-Path $resize 'module-receipt-final.json') 'A12234BB2FB38DBBB3926F2E8909A257360A250537B179D3E3142E57EF6FCD11'
[IO.Directory]::CreateDirectory($candidate)|Out-Null
[IO.Directory]::CreateDirectory($evidence)|Out-Null
$seen=@{};$count=0
foreach($r in $baseManifest.files){
    $relative=[string]$r.path;$seen[$relative]=$r
    if($relative -eq 'candidate-integration.json'){continue}
    $source=if($sources.ContainsKey($relative)){$sources[$relative]}else{Resolve-OverlayFile $base $relative}
    $destination=Resolve-OverlayFile $candidate $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
    [IO.File]::Copy($source,$destination,$false)
    $count++;if($count%500 -eq 0){Write-Host ('COPIED '+$count)}
}
foreach($relative in $sources.Keys){if(!$seen.ContainsKey($relative)){
    $destination=Resolve-OverlayFile $candidate $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
    [IO.File]::Copy($sources[$relative],$destination,$false)
}}
$authorityRows=@(Get-OverlayInventory (Join-Path $candidate 'authority/win-x64'))
$authorityTree=Get-OverlayTree $authorityRows
$integration=Get-Content -LiteralPath (Join-Path $base 'candidate-integration.json') -Raw|ConvertFrom-Json
$inheritedCheckpoint=$integration.AuthorityCheckpointId
$integration.AuthorityCheckpointId='module-overlay:'+ $OverlayId
$integration.AuthorityTreeSha256=$authorityTree.treeSha256
# Keep the product pipe explicit and stable. A test run ID never determines it.
Write-OverlayJson (Join-Path $candidate 'candidate-integration.json') $integration
$integrationIdentity=Get-OverlayIdentity (Join-Path $candidate 'candidate-integration.json')
$replacements['candidate-integration.json']=[ordered]@{path='candidate-integration.json';bytes=$integrationIdentity.bytes;sha256=$integrationIdentity.sha256}
$rows=@(Get-OverlayInventory $candidate);$tree=Get-OverlayTree $rows
$delta=@();$unchanged=0
foreach($r in $rows){
    $old=if($seen.ContainsKey($r.path)){$seen[$r.path]}else{$null}
    if($null -ne $old -and $old.bytes -eq $r.bytes -and $old.sha256 -ceq $r.sha256){$unchanged++;continue}
    if(!$replacements.ContainsKey($r.path)){throw ('non-whitelisted-change: '+$r.path)}
    Test-OverlayFile (Resolve-OverlayFile $candidate $r.path) $replacements[$r.path]
    $delta += [ordered]@{path=$r.path;before=$old;after=$r}
}
if($rows.Count -ne $baseManifest.files.Count+@($sources.Keys|Where-Object {!$seen.ContainsKey($_)}).Count){throw 'file-count-delta-mismatch'}
$manifest=[ordered]@{
    schemaVersion=$baseManifest.schemaVersion;candidateId=[IO.Path]::GetFileName($candidate);createdAtUtc=[DateTime]::UtcNow.ToString('o')
    product='零碎知识剪藏';entrypoint='KnowledgeClipper.exe';architecture='win-x64';desktopSelfContained=$true;regressionSlice='Playback';payload=$tree
    bindings=[ordered]@{
        inheritedBase=[ordered]@{root=$base;manifestSha256=$baseSha;authorityCheckpointId=$inheritedCheckpoint;role='inherited-base-not-current-authority-tree'}
        moduleOverlay=[ordered]@{overlayId=$OverlayId;admission=$replacements['module-overlays/speech-1.2.6/admission.json'];resize=$replacements['module-overlays/resize/module-receipt.json'];allowedFiles=@($replacements.Values);authorityTreeSha256=$authorityTree.treeSha256}
        sdkFreezeManifest=$baseManifest.bindings.sdkFreezeManifest;runtimeOverlay=$baseManifest.bindings.runtimeOverlay;runtimeToolsDescriptor=$baseManifest.bindings.runtimeToolsDescriptor
        inheritedRunner=[ordered]@{binding=$baseManifest.bindings.runnerReleaseManifest;role='historical-evidence-only-do-not-execute';newRunnerMustBeExternal=$true}
    }
    safety=[ordered]@{actualRun=0;externalWrites=0;speaker=0;userHandTestEntrypoint=$false;productionRetention='safe-disabled'}
    files=$rows
}
Write-OverlayJson (Join-Path $candidate 'candidate-file-manifest.v1.json') $manifest
$sealed=Get-OverlayIdentity (Join-Path $candidate 'candidate-file-manifest.v1.json')
Write-OverlayJson (Join-Path $evidence 'assembly.json') ([ordered]@{status='SEALED-AWAITING-SAFE-GATE';candidate=$candidate;manifest=$sealed;payload=$tree;unchangedFiles=$unchanged;delta=$delta;removedFiles=@();coreRecompiled=$false;desktopRecompiled=$false;runnerEmbedded=$false;inheritedRunnerPreserved=$true;authority=$authorityTree;pipe=$integration.PipeName;assembler=(Get-OverlayIdentity $PSCommandPath)})
$sealed|ConvertTo-Json

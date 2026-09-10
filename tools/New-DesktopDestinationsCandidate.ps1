#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [string]$BaseRoot='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-whole-window-zoom-20260909-01',
    [string]$BaseManifestSha='6BAC404621750CC9D7049736BED23274AAB1DB88012EE7B5AC898107C0DE0561'
)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
$base=$BaseRoot
$baseSha=$BaseManifestSha
$formal='D:\Artifacts\KnowledgeClipper\remnote-destinations\formal-runtime-20260909-01'
$overlay=Join-Path $formal 'overlay'
$module='D:\Artifacts\KnowledgeClipper\remnote-destinations\desktop-workflow-20260909-01'
foreach($path in @($CandidateRoot,$EvidenceRoot)) {
    if(Test-Path -LiteralPath $path){throw 'append-only-output-exists'}
    if(![IO.Path]::GetFullPath($path).StartsWith('D:\Artifacts\KnowledgeClipper\',[StringComparison]::OrdinalIgnoreCase)){throw 'artifact-root-required'}
}
$bindings=[ordered]@{}
foreach($spec in @(
    @('coreQualification',(Join-Path $formal 'qualification-receipt.json'),'BC7F12E3577F3818C9D65CD0885AB3A6DEDEF708AE8DB7D61DCE868C2FFA96F0'),
    @('coreManifest',(Join-Path $overlay 'candidate-manifest.json'),'602C7C28A9B9A89DB9C90B678184C8DB8E66046736302FE44DA0ED63CF59868F'),
    @('coreAdmission',(Join-Path $formal 'module-overlay-admission.json'),'D34253558D6908A9DC484B0D9D3063F8F72D46810D015E82F0D25E8C17E425A7'),
    @('desktopModule',(Join-Path $module 'workflow-module.receipt.json'),'648FF5589CD5ABCB2B12FBD9BB55733CD8AE107BF1649FF89B117619419F2E64'),
    @('desktopRuntimeBinding',(Join-Path $module 'workflow-runtime-binding.receipt.json'),'7E591474912A9B5BDFBB2F32AB6CAC11B32AE06E40A74C626E2D2D8CC3552BDA')
)) {
    $id=Get-OverlayIdentity $spec[1]
    if($id.sha256 -cne $spec[2]){throw ('source-receipt-drift: '+$spec[0])}
    $bindings[$spec[0]]=$id
}
$baseManifest=Assert-OverlayManifest $base (Join-Path $base 'candidate-file-manifest.v1.json') $baseSha
$coreManifest=Get-Content $bindings.coreManifest.path -Raw|ConvertFrom-Json -Depth 100
$physical=@(Get-OverlayInventory $overlay @('candidate-manifest.json'))
$coreRows=@{};foreach($r in $coreManifest.files){$coreRows[$r.path]=$r;Test-OverlayFile (Resolve-OverlayFile $overlay $r.path) $r}
if($physical.Count -ne $coreRows.Count){throw 'formal-overlay-extra-file'}
foreach($r in $physical){if(!$coreRows.ContainsKey($r.path)){throw 'formal-overlay-extra-path'}}
# Core's tree serialization is tab-delimited; Desktop's established tree is NUL-delimited.
$material=[Text.StringBuilder]::new()
foreach($r in $physical){[void]$material.Append($r.path).Append("`t").Append($r.bytes).Append("`t").Append($r.sha256).Append("`n")}
$coreTree=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material.ToString())))
if($coreTree -cne $coreManifest.payload.treeSha256 -or $coreTree -cne '71522B2134A02FF9DA7E08FCCC9715C8422555FF2280315E1E2C0D84AD38F29A'){throw 'core-tree-drift'}
$moduleReceipt=Get-Content $bindings.desktopModule.path -Raw|ConvertFrom-Json -Depth 100
foreach($r in $moduleReceipt.files){Test-OverlayFile $r.path $r}
$sources=@{};$expected=@{}
foreach($r in $baseManifest.files){$sources[$r.path]=Resolve-OverlayFile $base $r.path;$expected[$r.path]=$r}
foreach($r in $coreManifest.files){$sources[$r.path]=Resolve-OverlayFile $overlay $r.path;$expected[$r.path]=$r}
foreach($r in $coreManifest.files|Where-Object {$_.path.StartsWith('sdk/',[StringComparison]::Ordinal)}) {
    $relative=$r.path.Substring(4);$sources[$relative]=Resolve-OverlayFile $overlay $r.path;$expected[$relative]=$r
}
foreach($name in @('KnowledgeClipper.exe','KnowledgeClipper.dll','KnowledgeClipper.deps.json','KnowledgeClipper.runtimeconfig.json')) {
    $source=Join-Path $module ('desktop-closeout/'+$name)
    $sources[$name]=$source;$expected[$name]=Get-OverlayIdentity $source
}
if($expected['KnowledgeClipper.dll'].sha256 -cne '825A2B8716111907E83BECD949BFD858CE1C636AF9F51531D876785CF21E9120'){throw 'desktop-dll-drift'}
$transformed=@('candidate-integration.json','authority/dependency-manifest.v1.json','authority/win-x64/workers/remnote/appsettings.json')
New-Item -ItemType Directory -Path $CandidateRoot,$EvidenceRoot|Out-Null
$count=0
foreach($relative in $sources.Keys) {
    if($relative -in $transformed){continue}
    $target=Resolve-OverlayFile $CandidateRoot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
    [IO.File]::Copy($sources[$relative],$target,$false)
    Test-OverlayFile $target $expected[$relative]
    $count++;if($count%500 -eq 0){Write-Host ('COPIED '+$count)}
}
$configRelative='authority/win-x64/workers/remnote/appsettings.json'
$config=Get-Content (Join-Path $overlay $configRelative) -Raw|ConvertFrom-Json
$config.NodePath=Resolve-OverlayFile $CandidateRoot 'authority/win-x64/workers/remnote/runtime/node.exe'
$config.RemnoteCliJsPath=Resolve-OverlayFile $CandidateRoot 'authority/win-x64/workers/remnote/runtime/remnote-cli/node_modules/remnote-mcp-server/dist/remnote-cli/index.js'
Write-OverlayJson (Join-Path $CandidateRoot $configRelative) $config
$configId=Get-OverlayIdentity (Join-Path $CandidateRoot $configRelative)
$dependency=Get-Content (Join-Path $overlay 'authority/dependency-manifest.v1.json') -Raw|ConvertFrom-Json -Depth 100
$dependency.moduleInputs.remnote.configuration.sha256=$configId.sha256
$dependency.generatedAt=[DateTime]::UtcNow.ToString('o')
Write-OverlayJson (Join-Path $CandidateRoot 'authority/dependency-manifest.v1.json') $dependency
$authorityTree=Get-OverlayTree @(Get-OverlayInventory (Join-Path $CandidateRoot 'authority/win-x64'))
$integration=Get-Content (Join-Path $base 'candidate-integration.json') -Raw|ConvertFrom-Json
$candidateId=[IO.Path]::GetFileName([IO.Path]::GetFullPath($CandidateRoot).TrimEnd('\'))
if($candidateId -cnotmatch '^[a-z0-9][a-z0-9-]{0,100}$'){throw 'candidate-identity-invalid'}
$integration.AuthorityCheckpointId='module-overlay:'+$candidateId
$integration.AuthorityTreeSha256=$authorityTree.treeSha256
# Separate admitted candidates by deterministic deployment identity. Never reuse an
# inherited pipe that can still be owned by an older executable/catalog.
$integration.PipeName='KnowledgeClipper.CoreV3.'+$candidateId
Write-OverlayJson (Join-Path $CandidateRoot 'candidate-integration.json') $integration
$bindings.inheritedBase=[ordered]@{root=$base;manifestSha256=$baseSha;role='historical-base-provenance-not-current-authority'}
$bindings.deployment=[ordered]@{configuration=$configId;changedFields=@('NodePath','RemnoteCliJsPath');fixedRoot=$CandidateRoot;arbitraryRelocationSupported=$false;standardRunConfigSnapshot='empty: shared worker appsettings';authority=$authorityTree;dependency=(Get-OverlayIdentity (Join-Path $CandidateRoot 'authority/dependency-manifest.v1.json'))}
$bindings.desktopPayload=$expected['KnowledgeClipper.dll']
$rows=@(Get-OverlayInventory $CandidateRoot);$tree=Get-OverlayTree $rows
$old=@{};foreach($r in $baseManifest.files){$old[$r.path]=$r}
$changed=@();$added=@();$unchanged=0
foreach($r in $rows){
    if(!$old.ContainsKey($r.path)){$added+=$r.path}
    elseif($r.sha256 -ceq $old[$r.path].sha256){$unchanged++}
    else{$changed+=[ordered]@{path=$r.path;before=$old[$r.path].sha256;after=$r.sha256}}
}
$manifest=[ordered]@{
    schemaVersion=$baseManifest.schemaVersion;candidateId=[IO.Path]::GetFileName($CandidateRoot);createdAtUtc=[DateTime]::UtcNow.ToString('o')
    product='零碎知识剪藏';entrypoint='KnowledgeClipper.exe';architecture='win-x64';desktopSelfContained=$true
    payload=$tree;bindings=$bindings
    safety=[ordered]@{userHandTestEntrypoint=$false;externalWrites=0;speaker=0;productionRetention='safe-disabled';historicalRegistryVersion='1.2.44';historicalCheckpointPromoted=$false}
    files=$rows
}
Write-OverlayJson (Join-Path $CandidateRoot 'candidate-file-manifest.v1.json') $manifest
$manifestId=Get-OverlayIdentity (Join-Path $CandidateRoot 'candidate-file-manifest.v1.json')
[void](Assert-OverlayManifest $CandidateRoot $manifestId.path $manifestId.sha256)
Write-OverlayJson (Join-Path $EvidenceRoot 'assembly.json') ([ordered]@{
    status='SEALED-AWAITING-SAFE-GATES';candidate=$CandidateRoot;manifest=$manifestId;payload=$tree;authority=$authorityTree
    unchangedFiles=$unchanged;changed=$changed;added=$added;removed=@();bindings=$bindings
    userHandTestEntrypoint=$false;assembler=(Get-OverlayIdentity $PSCommandPath)
})
$manifestId|ConvertTo-Json

#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputRoot='D:\Artifacts\KnowledgeClipper\playback-control\speech126-overlay-20260909-01')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$base='D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-playback-20260908T155328Z'
$module='D:\Artifacts\KnowledgeClipper\speech\r20260908T170550Z'
$release=Join-Path $module 'win-x64'
$root=[IO.Path]::GetFullPath($OutputRoot)
if(-not $root.StartsWith('D:\Artifacts\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $root)){throw 'new-D-artifact-root-required'}
function Sha([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function ReadJson([string]$p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json -Depth 100}
function JsonOnce([string]$p,[object]$v){
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))|Out-Null
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($v|ConvertTo-Json -Depth 100)+"`n")
 $stream=[IO.File]::Open($p,[IO.FileMode]::CreateNew)
 try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
}
function Check([bool]$ok,[string]$why){if(-not $ok){throw $why}}
function Under([string]$parent,[string]$relative){
 Check (-not[IO.Path]::IsPathRooted($relative)-and $relative.Replace('\','/').Split('/') -notcontains '..') 'unsafe-relative-path'
 $p=[IO.Path]::GetFullPath((Join-Path $parent $relative))
 Check ($p.StartsWith($parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) 'path-escape'
 $p
}
function Row([string]$p,[string]$relative){[ordered]@{path=$relative;bytes=(Get-Item -LiteralPath $p).Length;sha256=Sha $p}}
$baseManifest=Join-Path $base 'candidate-file-manifest.v1.json'
Check ((Sha $baseManifest)-eq'64F9AC64910BAD44CDD610C7717D185E80E5C60B64EF88F8401A00F6BDC84AE2') 'base-manifest-drift'
$ownerReceipt=Join-Path $module 'speech-request-path-fix-receipt.json'
Check ((Sha $ownerReceipt)-eq'B849363325E695EFEC0CCFCE6C90E6D16E3710131E20C362C0357D4F66D503F2') 'module-receipt-drift'
$treePath=Join-Path $module 'release-tree.json';$tree=ReadJson $treePath
Check ($tree.files.Count-eq11-and $tree.treeSha256-eq'ADD6E339138766BE84958F105334CAF5FFFC594123068569A2B290FF3FA8E189') 'module-inventory-identity-drift'
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$moduleRows=@();[long]$moduleBytes=0
foreach($f in $tree.files){
 Check ($seen.Add($f.path)-and $f.path.StartsWith('workers/speech/',[StringComparison]::Ordinal)) 'module-path-not-unique-or-outside-speech'
 $p=Under $release $f.path
 Check ((Get-Item -LiteralPath $p).Length-eq$f.bytes-and(Sha $p)-eq$f.sha256) ('module-file-drift:'+ $f.path)
 $moduleBytes+=$f.bytes
 $moduleRows+= [ordered]@{source=$p;destination=('authority/win-x64/'+$f.path);bytes=$f.bytes;sha256=$f.sha256.ToUpperInvariant()}
}
Check ($moduleBytes-eq160651) 'module-total-bytes-drift'
$exe=Join-Path $release 'workers/speech/KnowledgeClipper.Speech.Worker.exe'
Check ((Sha $exe)-eq'46F73309D94528C52ACF03B044AC2D39794AB502096A52FF435B4E791D2B88AA') 'module-exe-drift'
[IO.Directory]::CreateDirectory($root)|Out-Null
Write-Output 'PHASE describe only; worker run / network / speaker prohibited'
& $exe describe --json 1> (Join-Path $root 'describe.json') 2> (Join-Path $root 'describe.stderr.log')
$describeExit=$LASTEXITCODE
Check ($describeExit-eq0) 'module-describe-failed'
$describe=ReadJson (Join-Path $root 'describe.json')
$oldDescribe=ReadJson (Join-Path $base 'authority/win-x64/workers/speech/descriptors/worker-describe.json')
Check ($describe.workerVersion-eq'1.2.6') 'wrong-module-version'
foreach($name in @('schemaVersion','workerId','contractVersion','stages','inputKinds','outputKinds','concurrency','sideEffectLevel','idempotencyLevel','cancellation')){
 Check (($describe.$name|ConvertTo-Json -Depth 30 -Compress)-eq($oldDescribe.$name|ConvertTo-Json -Depth 30 -Compress)) ('describe-compatibility-drift:'+ $name)
}
$registryPath=Join-Path $base 'authority/worker-registry.v1.json'
$registrySha=Sha $registryPath
Check ($registrySha-eq'A3DBF614253ACD3D6C2C26389C2D6745D4C134BE8EC9883590860968D56558D0') 'registry-drift'
$catalogRelative='contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json'
$bindingRelative='contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json'
$dependencyRelative='authority/dependency-manifest.v1.json'
$catalog=ReadJson (Join-Path $base $catalogRelative)
$speech=@($catalog.operations|Where-Object operationId -EQ 'speech.runtime.internal')
Check ($speech.Count-eq1-and$speech[0].workerBinding.workerId-eq'speech.edge') 'speech-capability-not-unique'
$speech[0].workerBinding.versionRange='=1.2.6'
$speech[0].workerBinding.authority.exactWorkerVersion='1.2.6'
$speech[0].workerBinding.authority.entrypointSha256=Sha $exe
$overlayRoot=Join-Path $root 'overlay'
JsonOnce (Under $overlayRoot $catalogRelative) $catalog
$binding=ReadJson (Join-Path $base $bindingRelative)
$binding.catalogSha256=Sha (Under $overlayRoot $catalogRelative)
JsonOnce (Under $overlayRoot $bindingRelative) $binding
$dependency=ReadJson (Join-Path $base $dependencyRelative)
$dependency.generatedAt=[datetime]::UtcNow.ToString('o')
$dependency.moduleInputs|Add-Member -MemberType NoteProperty -Name speech -Value ([ordered]@{
 worker='speech.edge@1.2.6';copiedPath='authority/win-x64/workers/speech'
 releaseManifestPath='authority/win-x64/workers/speech/release-manifest.json';releaseManifestSha256=Sha (Join-Path $release 'workers/speech/release-manifest.json')
 payloadTreeSha256=$tree.treeSha256.ToUpperInvariant();entrypointSha256=Sha $exe
 sourceRoot=$release;sourceTreeManifest=[ordered]@{path=$treePath;sha256=Sha $treePath};sourceReceipt=[ordered]@{path=$ownerReceipt;sha256=Sha $ownerReceipt}
 admission='module-overlay-on-inherited-registry-1.2.44-not-historical-checkpoint-promotion'
})
JsonOnce (Under $overlayRoot $dependencyRelative) $dependency
Write-Output 'PHASE prospective capability and unchanged runtime/SDK bindings'
$baseFiles=(ReadJson $baseManifest).files
$unchanged=@($baseFiles|Where-Object{$_.path.StartsWith('core-v3/')-or $_.path.StartsWith('playback-control/sdk/')-or $_.path-in@('KnowledgeClipper.Core.V3.Client.dll','KnowledgeClipper.Core.V3.Contracts.dll','authority/worker-registry.v1.json','authority/runtime-tools.v1.json','descriptors/speech-settings-companion.v1.json')})
foreach($f in $unchanged){$p=Under $base $f.path;Check ((Get-Item -LiteralPath $p).Length-eq$f.bytes-and(Sha $p)-eq$f.sha256) ('unchanged-binding-drift:'+ $f.path)}
foreach($operation in $catalog.operations|Where-Object{$_.workerBinding.mode-eq'worker'-and$_.available}){
 $a=$operation.workerBinding.authority
 Check ($a.registrySha256-eq$registrySha-and$operation.workerBinding.versionRange-eq('='+$a.exactWorkerVersion)) 'prospective-version-registry-drift'
 $entry=if($operation.workerBinding.workerId-eq'speech.edge'){$exe}else{Under $base ($a.releaseRoot+'/'+$a.entrypointPath)}
 Check ((Sha $entry)-eq$a.entrypointSha256) 'prospective-entrypoint-hash-drift'
}
Check ((ReadJson $registryPath).registryVersion-eq$catalog.authorityAnchor.authorityVersion) 'registry-anchor-drift'
Check (($binding.authorityAnchor|ConvertTo-Json -Compress)-eq($catalog.authorityAnchor|ConvertTo-Json -Compress)) 'catalog-anchor-drift'
Check ((Sha (Under $base $binding.authorityAnchor.checkpointPath))-eq$binding.authorityAnchor.checkpointSha256) 'historical-checkpoint-drift'
$metadata=@($catalogRelative,$bindingRelative,$dependencyRelative|ForEach-Object{Row (Under $overlayRoot $_) $_})
JsonOnce (Join-Path $root 'module-overlay-admission.json') ([ordered]@{
 schema='kc.module-overlay-admission/1';overlayId=[IO.Path]::GetFileName($root);status='prepared-offline-not-integrated';createdAt=[datetime]::UtcNow.ToString('o')
 baseline=[ordered]@{root=$base;manifestSha256=Sha $baseManifest;registryVersion='1.2.44';registrySha256=$registrySha;historicalCheckpointUnchanged=$true;historicalCheckpointContainsSpeech126=$false}
 module=[ordered]@{root=$release;workerId='speech.edge';workerVersion='1.2.6';manifestSha256=Sha (Join-Path $release 'workers/speech/release-manifest.json');inventoryPath=$treePath;inventorySha256=Sha $treePath;declaredTreeSha256=$tree.treeSha256.ToUpperInvariant();files=11;bytes=$moduleBytes;filesVerified=11;fileMismatches=0;describe=Row (Join-Path $root 'describe.json') 'describe.json';ownerReceiptSha256=Sha $ownerReceipt}
 moduleCopies=$moduleRows;metadataCopies=$metadata;metadataRoot=$overlayRoot
 unchangedBindings=$unchanged
 assemblyRequirements=@('use new Desktop candidate only; never overwrite baseline','replace Speech closure from moduleCopies; no assumed bundledFiles member','replace only three metadataCopies plus approved resize KnowledgeClipper.dll','recompute authority/win-x64 tree and candidate-integration AuthorityTreeSha256','keep inherited base checkpoints/manifests as provenance; record explicit Speech overlay','seal full product files/bytes/tree including resize receipt','read PipeName from final candidate-integration, never synthesize from Actual runId','keep T164049 runner frozen; rebind external successor only after final product manifest; no hash cycle')
 evidenceBoundary=[ordered]@{coreSdkRebuilt=$false;registryModified=$false;oldProductModified=$false;describeInvocations=1;workerRunInvocations=0;externalCalls=0;physicalSpeakerInvocations=0;destinationWrites=0;fullMatrixRerun=$false;actualRuns=0;integrationStartupPending=$true}
 generator=Row $PSCommandPath $PSCommandPath
})
Write-Output ('STATUS PREPARED '+$root)
Write-Output ('ADMISSION SHA256 '+(Sha (Join-Path $root 'module-overlay-admission.json')))

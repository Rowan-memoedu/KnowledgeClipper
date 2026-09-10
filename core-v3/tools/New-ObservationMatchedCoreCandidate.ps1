[CmdletBinding()]
param(
    [string]$OldCandidate = 'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-observation-20260908T020606Z-f9d943ec',
    [string]$ExpectedOldManifestSha256 = '06F3DE990E9A5DE0BAF22741E35807347BAC05C403042FCEC9D4EC8F153D7CDF',
    [string]$SdkFreeze = 'D:\Artifacts\KnowledgeClipper\read-to-anki-sdk\actual-desktop-flow-observation-sdk-20260908T020355Z',
    [string]$ProjectRoot = 'D:\Projects\RemNote Quick Clipper',
    [Parameter(Mandatory)][string[]]$RegressionReceipts,
    [string]$ContentReleaseRoot,
    [string]$ContentReleaseManifestSha256,
    [string]$ContentReleaseTreeSha256,
    [string]$ContentWorkerVersion,
    [string]$NextRegistryVersion,
    [string]$OutputRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki',
    [string]$StagingRoot = 'D:\Artifacts\KnowledgeClipper\staging\read-to-anki',
    [string]$EvidenceRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki\qualifications'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CanonicalTreeHash.ps1')
function Require([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function WriteJson([string]$Path,[object]$Value){$Value|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $Path -Encoding utf8NoBOM}
function SafeRelative([string]$Path){Require (-not [IO.Path]::IsPathRooted($Path) -and $Path -notmatch '(^|/|\\)\.\.($|/|\\)') "Unsafe relative path: $Path"}
function GetLocalRefs([string]$SchemaRoot){
    $refs=@()
    foreach($schemaFile in @(Get-ChildItem -LiteralPath $SchemaRoot -Recurse -File -Filter '*.schema.json')){
        $json=Get-Content -LiteralPath $schemaFile.FullName -Raw|ConvertFrom-Json -Depth 100
        $queue=[Collections.Generic.Queue[object]]::new();$queue.Enqueue($json)
        while($queue.Count -gt 0){
            $node=$queue.Dequeue()
            if($null -eq $node){continue}
            if($node -is [pscustomobject]){
                foreach($property in $node.PSObject.Properties){
                    if($property.Name -eq '$ref' -and $property.Value -is [string] -and -not $property.Value.StartsWith('#') -and $property.Value -notmatch '^[A-Za-z][A-Za-z0-9+.-]*:'){
                        $relative=$property.Value.Split('#')[0]
                        $resolved=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $schemaFile.FullName) $relative))
                        $boundary=[IO.Path]::GetFullPath($SchemaRoot).TrimEnd('\')+'\'
                        Require ($resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) "Schema ref escapes candidate contracts: $($schemaFile.FullName)"
                        Require (Test-Path -LiteralPath $resolved -PathType Leaf) "Schema ref missing: $($schemaFile.FullName) -> $relative"
                        $refs += [ordered]@{schema=[IO.Path]::GetRelativePath($SchemaRoot,$schemaFile.FullName).Replace('\','/');reference=$property.Value;resolvedPath=[IO.Path]::GetRelativePath($SchemaRoot,$resolved).Replace('\','/');sha256=Sha $resolved}
                    }
                    if($property.Value -is [pscustomobject] -or ($property.Value -is [Collections.IEnumerable] -and -not ($property.Value -is [string]))){$queue.Enqueue($property.Value)}
                }
            } elseif($node -is [Collections.IEnumerable] -and -not ($node -is [string])){foreach($item in $node){$queue.Enqueue($item)}}
        }
    }
    @($refs|Sort-Object schema,reference,resolvedPath)
}

$old=[IO.Path]::GetFullPath($OldCandidate);$sdk=[IO.Path]::GetFullPath($SdkFreeze);$project=[IO.Path]::GetFullPath($ProjectRoot)
$oldManifestPath=Join-Path $old 'candidate-manifest.json';$sdkManifestPath=Join-Path $sdk 'freeze-manifest.json';$sdkReceiptPath=Join-Path $sdk 'qualification-receipt.json'
Require (Test-Path $oldManifestPath -PathType Leaf) 'Old candidate manifest is missing.'
Require ((Sha $oldManifestPath) -eq $ExpectedOldManifestSha256) 'Old candidate manifest drifted.'
Require (Test-Path $sdkManifestPath -PathType Leaf) 'SDK freeze manifest is missing.'
$sdkManifest=Get-Content $sdkManifestPath -Raw|ConvertFrom-Json -Depth 100
$sdkReceipt=Get-Content $sdkReceiptPath -Raw|ConvertFrom-Json -Depth 100
Require ($sdkReceipt.status -eq 'PASS') 'SDK qualification receipt is not PASS.'
foreach($entry in @($sdkManifest.files)){
    SafeRelative $entry.path
    $path=Join-Path $sdk ($entry.path -replace '/', [IO.Path]::DirectorySeparatorChar)
    Require (Test-Path $path -PathType Leaf) "SDK freeze file missing: $($entry.path)"
    Require ((Get-Item $path).Length -eq [long]$entry.bytes -and (Sha $path) -eq $entry.sha256) "SDK freeze file drifted: $($entry.path)"
}
$oldManifest=Get-Content $oldManifestPath -Raw|ConvertFrom-Json -Depth 100
$registryVersion=if($oldManifest.authority){$oldManifest.authority.registryVersion}else{$oldManifest.registryVersion}
Require ($registryVersion -eq '1.2.43') 'Old candidate registry version drifted.'
$upgradeContent=-not [string]::IsNullOrWhiteSpace($ContentReleaseRoot)
$contentUpgrade=$null
if($upgradeContent){
    Require ([version]$NextRegistryVersion -gt [version]$registryVersion) 'Content promotion requires a monotonic registry version.'
    $contentSource=[IO.Path]::GetFullPath($ContentReleaseRoot)
    $contentManifestPath=Join-Path $contentSource 'release-manifest.v1.json'
    Require ((Sha $contentManifestPath) -eq $ContentReleaseManifestSha256) 'Content formal manifest drifted.'
    $contentManifest=Get-Content -LiteralPath $contentManifestPath -Raw|ConvertFrom-Json -Depth 100
    Require ($contentManifest.worker.workerId -eq 'content.deepseek' -and $contentManifest.worker.workerVersion -eq $ContentWorkerVersion) 'Content formal identity drifted.'
    $moduleMaterial=[Text.StringBuilder]::new()
    foreach($entry in $contentManifest.files){
        SafeRelative $entry.path;$p=Join-Path $contentSource $entry.path
        Require ((Get-Item -LiteralPath $p).Length -eq [long]$entry.bytes -and (Sha $p) -eq $entry.sha256) "Content formal file drifted: $($entry.path)"
        [void]$moduleMaterial.Append([string]$entry.path).Append([char]0).Append([long]$entry.bytes).Append([char]0).Append([string]$entry.sha256).Append("`n")
    }
    $actualModuleTree=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($moduleMaterial.ToString())))
    Require ($actualModuleTree -eq $ContentReleaseTreeSha256 -and $contentManifest.payloadTreeSha256 -eq $ContentReleaseTreeSha256) 'Content formal tree drifted.'
    Require (@(Get-ChildItem -LiteralPath $contentSource -Recurse -File).Count -eq @($contentManifest.files).Count+1) 'Content formal inventory contains undeclared files.'
    $contentUpgrade=[ordered]@{workerVersion=$ContentWorkerVersion;sourceRoot=$contentSource;manifestSha256=Sha $contentManifestPath;payloadTreeSha256=$actualModuleTree;files=@($contentManifest.files).Count}
    $registryVersion=$NextRegistryVersion
}
$regressionEvidence=@(foreach($receipt in $RegressionReceipts){
    $p=[IO.Path]::GetFullPath($receipt);$doc=Get-Content -LiteralPath $p -Raw|ConvertFrom-Json -Depth 100
    Require ($doc.status -eq 'PASS') "Regression receipt is not PASS: $p"
    [ordered]@{path=$p;bytes=(Get-Item -LiteralPath $p).Length;sha256=Sha $p}
})
Require ($regressionEvidence.Count -gt 0) 'Regression evidence is required.'
$sourceFiles=@(Get-ChildItem -LiteralPath (Join-Path $project 'core-v3/src') -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.props')})
$sourceFiles+=Get-Item -LiteralPath (Join-Path $project 'core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/Program.cs'),(Join-Path $project 'core-v3/tools/Test-CompleteContextConsumer.mjs'),(Join-Path $project 'core-v3/tools/Test-ClassificationPreference.mjs'),(Join-Path $project 'core-v3/tools/Invoke-CoreContentReplay.mjs'),$PSCommandPath
$sourceEvidence=@($sourceFiles|ForEach-Object{[ordered]@{path=$_.FullName;bytes=$_.Length;sha256=Sha $_.FullName}})

$oldDeclared=@($oldManifest.files);$oldPaths=@{}
foreach($entry in $oldDeclared){
    SafeRelative $entry.path;$path=Join-Path $old ($entry.path -replace '/', [IO.Path]::DirectorySeparatorChar)
    Require (Test-Path $path -PathType Leaf) "Old candidate file missing: $($entry.path)"
    Require ((Get-Item $path).Length -eq [long]$entry.bytes -and (Sha $path) -eq $entry.sha256) "Old candidate file drifted: $($entry.path)"
    $oldPaths[$entry.path]=$true
}
$oldActual=@(Get-ChildItem -LiteralPath $old -Recurse -File|ForEach-Object{[IO.Path]::GetRelativePath($old,$_.FullName).Replace('\','/')})
Require (@($oldActual|Where-Object{$_ -ne 'candidate-manifest.json' -and -not $oldPaths.ContainsKey($_)}).Count -eq 0) 'Old candidate contains undeclared files.'

$id='rta-observation-'+(Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$stage=Join-Path ([IO.Path]::GetFullPath($StagingRoot)) ($id+'.tmp');$final=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $id;$evidence=Join-Path ([IO.Path]::GetFullPath($EvidenceRoot)) $id
Require (-not (Test-Path $stage) -and -not (Test-Path $final) -and -not (Test-Path $evidence)) 'Append-only target already exists.'
New-Item -ItemType Directory -Path $stage,$evidence -Force|Out-Null

# Authority and worker payload are copied byte-for-byte from the prior sealed candidate.
if($upgradeContent){
    foreach($entry in @($oldDeclared|Where-Object{$_.path -like 'authority/*' -and $_.path -notlike 'authority/win-x64/workers/content/*'})){
        $target=Join-Path $stage $entry.path;New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null
        Copy-Item -LiteralPath (Join-Path $old $entry.path) -Destination $target
    }
    Copy-Item -LiteralPath $contentSource -Destination (Join-Path $stage 'authority/win-x64/workers/content') -Recurse
    foreach($entry in $contentManifest.files){
        $copied=Join-Path $stage ('authority/win-x64/workers/content/'+$entry.path)
        Require ((Get-Item -LiteralPath $copied).Length -eq [long]$entry.bytes -and (Sha $copied) -eq $entry.sha256) "Copied Content file drifted: $($entry.path)"
    }
    Require ((Sha (Join-Path $stage 'authority/win-x64/workers/content/release-manifest.v1.json')) -eq $ContentReleaseManifestSha256) 'Copied Content manifest drifted.'
}else{Copy-Item -LiteralPath (Join-Path $old 'authority') -Destination (Join-Path $stage 'authority') -Recurse}
Copy-Item -LiteralPath (Join-Path $old 'contracts') -Destination (Join-Path $stage 'contracts') -Recurse
if($upgradeContent){
    $registryPath=Join-Path $stage 'authority/worker-registry.v1.json';$registry=Get-Content $registryPath -Raw|ConvertFrom-Json -Depth 100
    $registry.registryVersion=$registryVersion;WriteJson $registryPath $registry
    $catalogPath=Join-Path $stage 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json'
    $catalog=Get-Content $catalogPath -Raw|ConvertFrom-Json -Depth 100;$catalog.authorityAnchor.authorityVersion=$registryVersion
    foreach($op in $catalog.operations){
        if($op.workerBinding.authority){$op.workerBinding.authority.registrySha256=Sha $registryPath}
        if($op.workerBinding.workerId -eq 'content.deepseek'){
            $op.workerBinding.versionRange='='+$ContentWorkerVersion;$op.workerBinding.authority.exactWorkerVersion=$ContentWorkerVersion
            $op.workerBinding.authority.entrypointSha256=Sha (Join-Path $stage 'authority/win-x64/workers/content/KnowledgeClipper.Content.Worker.exe')
        }
    }
    WriteJson $catalogPath $catalog
    $bindingPath=Join-Path $stage 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json';$binding=Get-Content $bindingPath -Raw|ConvertFrom-Json -Depth 100
    $binding.catalogSha256=Sha $catalogPath;$binding.authorityAnchor=$catalog.authorityAnchor;WriteJson $bindingPath $binding
    $dependency=Join-Path $stage 'authority/dependency-manifest.v1.json';$dep=Get-Content $dependency -Raw|ConvertFrom-Json -Depth 100
    $dep.generatedAt=[DateTime]::UtcNow.ToString('o');$dep.moduleInputs.content.worker='content.deepseek@'+$ContentWorkerVersion
    $dep.moduleInputs.content.releaseManifestSha256=$ContentReleaseManifestSha256;$dep.moduleInputs.content.payloadTreeSha256=$ContentReleaseTreeSha256
    $dep.moduleInputs.content.entrypointSha256=Sha (Join-Path $stage 'authority/win-x64/workers/content/KnowledgeClipper.Content.Worker.exe');WriteJson $dependency $dep
}

$dotnet='D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$hostProject=Join-Path $project 'core-v3\src\KnowledgeClipper.Core.V3.Host\KnowledgeClipper.Core.V3.Host.csproj'
$hostOut=Join-Path $stage 'core-v3'
& $dotnet publish $hostProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false --nologo -o $hostOut
Require ($LASTEXITCODE -eq 0) 'Matching Core Host publish failed.'
Require ((Sha (Join-Path $hostOut 'KnowledgeClipper.Core.V3.Contracts.dll')) -eq (Sha (Join-Path $sdk 'KnowledgeClipper.Core.V3.Contracts.dll'))) 'Published runtime Contracts no longer matches frozen SDK.'

$sdkTarget=Join-Path $stage 'sdk';New-Item -ItemType Directory -Path $sdkTarget|Out-Null
foreach($name in @('KnowledgeClipper.Core.V3.Client.dll','KnowledgeClipper.Core.V3.Contracts.dll','sdk-contract.v4.json')){Copy-Item -LiteralPath (Join-Path $sdk $name) -Destination (Join-Path $sdkTarget $name)}
$sdkBinding=Join-Path $sdkTarget 'sdk-binding.v1.json'
WriteJson $sdkBinding ([ordered]@{schema='kc.actual-desktop-flow-observation-sdk-binding/2';freezeManifest=[ordered]@{sourceRoot=$sdk;sha256=Sha $sdkManifestPath};qualificationReceiptSha256=Sha $sdkReceiptPath;clientSha256=Sha (Join-Path $sdk 'KnowledgeClipper.Core.V3.Client.dll');contractsSha256=Sha (Join-Path $sdk 'KnowledgeClipper.Core.V3.Contracts.dll');contractSha256=Sha (Join-Path $sdk 'sdk-contract.v4.json')})

$schemaSource=Join-Path $project 'contracts\core\v3\operations\clip.read-aloud-cards';$schemaTarget=Join-Path $stage 'contracts\core\v3\operations\clip.read-aloud-cards';New-Item -ItemType Directory -Path $schemaTarget -Force|Out-Null
$schemaNames=@('discovery-checkpoint.v1.schema.json','discovery-result.v1.schema.json','action-discovery-checkpoint.v1.schema.json','action-discovery-result.v1.schema.json','context-evidence.v1.schema.json','settings-evidence.v1.schema.json','projection.v4.schema.json')
foreach($name in $schemaNames){Copy-Item -LiteralPath (Join-Path $schemaSource $name) -Destination (Join-Path $schemaTarget $name)}
$refs=GetLocalRefs (Join-Path $stage 'contracts')
$authorityContractRoot=Join-Path $stage 'authority\win-x64\contracts'
Require (Test-Path -LiteralPath $authorityContractRoot -PathType Container) 'Authority contract root is missing.'
$authorityRefs=GetLocalRefs $authorityContractRoot
$frozenCommonArtifactSchema=Join-Path $authorityContractRoot 'v1\schemas\common\artifact.schema.json'
Require (Test-Path -LiteralPath $frozenCommonArtifactSchema -PathType Leaf) 'Authority portable closure lacks frozen common artifact.schema.json.'

$inheritedAuthority=@($oldDeclared|Where-Object{$_.path -like 'authority/*' -and (-not $upgradeContent -or ($_.path -notlike 'authority/win-x64/workers/content/*' -and $_.path -notin @('authority/worker-registry.v1.json','authority/dependency-manifest.v1.json')))})
foreach($entry in $inheritedAuthority){$target=Join-Path $stage ($entry.path -replace '/', [IO.Path]::DirectorySeparatorChar);Require ((Get-Item $target).Length -eq [long]$entry.bytes -and (Sha $target) -eq $entry.sha256) "Authority inherited payload drifted: $($entry.path)"}
$authorityEntries=@(Get-ChildItem -LiteralPath (Join-Path $stage 'authority') -Recurse -File|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=Sha $_.FullName}})
$authorityActual=@(Get-ChildItem -LiteralPath (Join-Path $stage 'authority') -Recurse -File)
$expectedAuthorityCount=if($upgradeContent){$inheritedAuthority.Count+@($contentManifest.files).Count+3}else{$inheritedAuthority.Count}
Require ($authorityActual.Count -eq $expectedAuthorityCount) 'Authority payload file count drifted.'
$contentLocalRefs=GetLocalRefs (Join-Path $stage 'authority/win-x64/workers/content/app')
$authorityTree=Get-KcCanonicalTreeHash -Files $authorityEntries

$dependencyPath=Join-Path $stage 'dependency-manifest.v3.json'
$schemaEvidence=@($schemaNames|ForEach-Object{$p=Join-Path $schemaTarget $_;[ordered]@{path=('contracts/core/v3/operations/clip.read-aloud-cards/'+$_);bytes=(Get-Item $p).Length;sha256=Sha $p}})
WriteJson $dependencyPath ([ordered]@{
    schema='kc.read-to-anki-observation-candidate-dependencies/3';oldCandidate=[ordered]@{id=$oldManifest.candidateId;manifestSha256=Sha $oldManifestPath}
    authority=[ordered]@{registryVersion=$registryVersion;files=$authorityEntries.Count;bytes=Get-KcCanonicalTreeTotalBytes -Files $authorityEntries;treeSha256=$authorityTree;treeAlgorithm='sha256(utf8 ordinal(path TAB int64(bytes) TAB uppercase(sha256) LF))';copiedExactly=(-not $upgradeContent);inheritedFiles=$inheritedAuthority.Count;mismatches=0;contentUpgrade=$contentUpgrade;contentLocalRefs=$contentLocalRefs}
    core=[ordered]@{host='core-v3/KnowledgeClipper.Core.V3.Host.exe';hostSha256=Sha (Join-Path $hostOut 'KnowledgeClipper.Core.V3.Host.exe');kernelSha256=Sha (Join-Path $hostOut 'KnowledgeClipper.Core.V3.Kernel.dll')}
    sdk=[ordered]@{binding='sdk/sdk-binding.v1.json';bindingSha256=Sha $sdkBinding;freezeManifestSha256=Sha $sdkManifestPath}
    schemas=$schemaEvidence;portableRecursiveSchemaValidation='passed';localRefs=$refs;authorityLocalRefs=$authorityRefs;frozenCommonArtifactSchema=[ordered]@{path='authority/win-x64/contracts/v1/schemas/common/artifact.schema.json';bytes=(Get-Item $frozenCommonArtifactSchema).Length;sha256=Sha $frozenCommonArtifactSchema}
})

$payloadFiles=@(Get-ChildItem -LiteralPath $stage -Recurse -File|Sort-Object{[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')}|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=Sha $_.FullName}})
$payloadTree=Get-KcCanonicalTreeHash -Files $payloadFiles
$manifestPath=Join-Path $stage 'candidate-manifest.json'
WriteJson $manifestPath ([ordered]@{
    schema='kc.read-to-anki-candidate-manifest/4';candidateId=$id;createdAt=(Get-Date).ToUniversalTime().ToString('o');supersedes=$oldManifest.candidateId
    authority=[ordered]@{registryVersion=$registryVersion;copiedExactly=(-not $upgradeContent);files=$authorityEntries.Count;treeSha256=$authorityTree;contentUpgrade=$contentUpgrade}
    runtime=[ordered]@{host='core-v3/KnowledgeClipper.Core.V3.Host.exe';matchingObservationServer=$true;selfContained=$true}
    observationSdk=[ordered]@{binding='sdk/sdk-binding.v1.json';contract='sdk/sdk-contract.v4.json';freezeManifestSha256=Sha $sdkManifestPath}
    dependencyManifest='dependency-manifest.v3.json';portableRecursiveSchemaValidation='passed';payload=[ordered]@{files=$payloadFiles.Count;bytes=Get-KcCanonicalTreeTotalBytes -Files $payloadFiles;treeSha256=$payloadTree;treeAlgorithm='sha256(utf8 ordinal(path TAB int64(bytes) TAB uppercase(sha256) LF))'};files=$payloadFiles
})
$manifestSha=Sha $manifestPath
[IO.Directory]::Move($stage,$final)

$testsDll=Join-Path $project 'core-v3\tests\KnowledgeClipper.Core.V3.Phase2Tests\bin\Release\net8.0\KnowledgeClipper.Core.V3.Phase2Tests.dll'
$readOnlyReceipt=Join-Path $evidence 'candidate-readonly-receipt.json'
& $dotnet $testsDll --candidate-observation-readonly $final --receipt $readOnlyReceipt
Require ($LASTEXITCODE -eq 0) 'Final-root matching Host read-only query gate failed.'
$readOnly=Get-Content $readOnlyReceipt -Raw|ConvertFrom-Json -Depth 100;Require ($readOnly.status -eq 'PASS' -and $readOnly.businessCommandsSubmitted -eq 0 -and $readOnly.workerRunInvocations -eq 0) 'Read-only candidate receipt boundary drifted.'

$finalManifest=Join-Path $final 'candidate-manifest.json';Require ((Sha $finalManifest) -eq $manifestSha) 'Final candidate manifest drifted after atomic seal.'
$finalDocument=Get-Content $finalManifest -Raw|ConvertFrom-Json -Depth 100
foreach($entry in $finalDocument.files){$p=Join-Path $final ($entry.path -replace '/',[IO.Path]::DirectorySeparatorChar);Require (Test-Path $p -PathType Leaf) "Final file missing: $($entry.path)";Require ((Get-Item $p).Length -eq [long]$entry.bytes -and (Sha $p) -eq $entry.sha256) "Final file drifted: $($entry.path)"}
Require ((Sha $oldManifestPath) -eq $ExpectedOldManifestSha256) 'Old candidate changed during packaging.'
foreach($entry in @($sourceEvidence)+@($regressionEvidence)){Require ((Get-Item -LiteralPath $entry.path).Length -eq $entry.bytes -and (Sha $entry.path) -eq $entry.sha256) "Source/regression evidence drifted during packaging: $($entry.path)"}

$receiptPath=Join-Path $evidence 'qualification-receipt.json'
WriteJson $receiptPath ([ordered]@{
    schema='kc.read-to-anki-observation-matched-candidate-qualification/1';createdAt=(Get-Date).ToUniversalTime().ToString('o');status='PASS'
    candidate=[ordered]@{id=$id;root=$final;manifest=[ordered]@{bytes=(Get-Item $finalManifest).Length;sha256=$manifestSha};payload=$finalDocument.payload;sealedFiles=($finalDocument.payload.files+1)}
    inheritance=[ordered]@{oldCandidate=$oldManifest.candidateId;oldManifestSha256=Sha $oldManifestPath;authorityFiles=$authorityEntries.Count;authorityTreeSha256=$authorityTree;mismatches=0;registryVersion=$registryVersion}
    regressionEvidence=$regressionEvidence;sourceEvidence=$sourceEvidence
    contentUpgrade=$contentUpgrade;inheritedAuthorityFiles=$inheritedAuthority.Count
    runtime=[ordered]@{hostSha256=Sha (Join-Path $final 'core-v3\KnowledgeClipper.Core.V3.Host.exe');kernelSha256=Sha (Join-Path $final 'core-v3\KnowledgeClipper.Core.V3.Kernel.dll');sdkClientSha256=Sha (Join-Path $final 'sdk\KnowledgeClipper.Core.V3.Client.dll');readOnlyReceipt=[ordered]@{path=$readOnlyReceipt;sha256=Sha $readOnlyReceipt};workerDescribeInvocations=3;businessWorkerRuns=0}
    contracts=[ordered]@{schemas=$schemaEvidence;portableLocalRefs=$refs.Count;authorityPortableLocalRefs=$authorityRefs.Count;frozenCommonArtifactSchemaPresent=$true;portableClosure='passed'}
    gates=@('release-publish-pass','sha-bound-offline-regressions-pass','sdk-runtime-contracts-exact','final-root-host-discovery-query-pass','unchanged-authority-files-byte-for-byte','formal-content-release-hash-bound','portable-schema-closure','authority-frozen-common-dependency-closure','manifest-inventory-hash-pass','atomic-stage-to-final-seal','old-candidate-unchanged')
    boundaries=[ordered]@{businessWorkerRuns=0;ankiCalls=0;remNoteCalls=0;ocrCalls=0;speakerCalls=0;externalWrites=0;sealedCandidateMutation=$false}
})
Write-Output "CANDIDATE $final"
Write-Output "MANIFEST_SHA256 $manifestSha"
Write-Output "PAYLOAD_TREE_SHA256 $payloadTree"
Write-Output "AUTHORITY_TREE_SHA256 $authorityTree"
Write-Output "RECEIPT $receiptPath"
Write-Output "RECEIPT_SHA256 $(Sha $receiptPath)"

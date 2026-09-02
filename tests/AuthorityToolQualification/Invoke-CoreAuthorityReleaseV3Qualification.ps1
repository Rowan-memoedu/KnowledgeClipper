[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$QualificationOutputPath
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$wrapperPath = Join-Path $ProjectRoot 'tools\New-KnowledgeClipperCoreAuthorityReleaseV3.ps1'
$baseToolSource = Join-Path $ProjectRoot 'tools\New-KnowledgeClipperCoreAuthorityRelease.ps1'
$checkpointSource = Join-Path $ProjectRoot 'contracts\core\v2.10\checkpoints\v2.10.2-20260901T100127Z\checkpoint.json'
$runtimeRoot = Join-Path $ProjectRoot 'orchestrator\bin\Release\net8.0\win-x64'
$baseRelease = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.37-20260901T063000Z\win-x64'
$baseAuthority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.37-20260901T063000Z'
$checkpointSha = '954F1D28DA9F64E15099FD55527FC158A0C3E5E34668A3F2385A09A8266F1039'
$baseToolSha = 'A108BABF46A0D8B904F2256816F38DC90FBAFF98B605DF314143566F1E3BDBAD'
$dependencyRelative = 'contracts/core/v2.3/playback-control/schemas/stage-control-request.v1.schema.json'

function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Write-Utf8([string]$Path, [string]$Text) { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null; [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false)) }
function Inventory([string]$Root) {
    $records=@();$rows=@();[long]$bytes=0
    foreach($file in @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)){
        $relative=[IO.Path]::GetRelativePath($Root,$file.FullName).Replace('\','/');$sha=Hash $file.FullName
        $records += [ordered]@{path=$relative;bytes=[long]$file.Length;sha256=$sha};$rows += "$relative`t$($file.Length)`t$($sha.ToLowerInvariant())";$bytes += $file.Length
    }
    $rows=[string[]]$rows;[Array]::Sort($rows,[StringComparer]::Ordinal);$material=[Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    [ordered]@{fileCount=$records.Count;totalBytes=$bytes;treeSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($material));files=@($records|Sort-Object path)}
}
function RuntimeManifest([string]$Root,[string]$CheckpointPath,[string]$CheckpointHash,[string]$Version){
    $tree=Inventory $Root
    [ordered]@{schemaVersion='knowledge-clipper-orchestrator-runtime-manifest.v1';checkpoint=[ordered]@{path=$CheckpointPath;sha256=$CheckpointHash};component=[ordered]@{id='knowledge-clipper.orchestrator';version=$Version};selfContained=$true;runtimeIdentifier='win-x64';entrypoint='KnowledgeClipper.Orchestrator.exe';treeAlgorithm='sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)';fileCount=$tree.fileCount;totalBytes=$tree.totalBytes;treeSha256=$tree.treeSha256;files=$tree.files}
}
function Invoke-Rejected([hashtable]$Arguments,[string]$Pattern,[string]$Name){
    try{$unexpected=& $wrapperPath @Arguments 2>&1;throw "$Name unexpectedly passed: $($unexpected -join [Environment]::NewLine)"}
    catch{$message=$_.Exception.Message;if($message -notmatch $Pattern){throw "$Name failed for the wrong reason: $message"};[ordered]@{name=$Name;status='passed-rejected';error=$message}}
}

foreach($required in @($wrapperPath,$baseToolSource,$checkpointSource,$runtimeRoot,$baseRelease,$baseAuthority)){if(-not(Test-Path -LiteralPath $required)){throw "Missing qualification input: $required"}}
if((Hash $baseToolSource)-ne $baseToolSha){throw 'Pinned base assembler SHA drifted before V3 qualification.'}
if((Hash $checkpointSource)-ne $checkpointSha){throw 'Pinned v2.10.2 checkpoint SHA drifted.'}

$fixture=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kcv3-'+[Guid]::NewGuid().ToString('N').Substring(0,8))))
$tempPrefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
if(-not $fixture.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Unsafe fixture root: $fixture"}
$cases=@()
try{
    $project=Join-Path $fixture 'p';[IO.Directory]::CreateDirectory($project)|Out-Null
    $checkpointDocument=Get-Content -LiteralPath $checkpointSource -Raw|ConvertFrom-Json -Depth 100
    foreach($binding in @($checkpointDocument.fileBindings)){$source=Join-Path $ProjectRoot ($binding.path.Replace('/','\'));$destination=Join-Path $project ($binding.path.Replace('/','\'));[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null;[IO.File]::Copy($source,$destination,$false)}
    $checkpointRelative='contracts/core/v2.10/checkpoints/v2.10.2-20260901T100127Z/checkpoint.json'
    $checkpointPath=Join-Path $project ($checkpointRelative.Replace('/','\'));[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($checkpointPath))|Out-Null;[IO.File]::Copy($checkpointSource,$checkpointPath,$false)
    $dependencySource=Join-Path $ProjectRoot ($dependencyRelative.Replace('/','\'));$dependencyPath=Join-Path $project ($dependencyRelative.Replace('/','\'));[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dependencyPath))|Out-Null;[IO.File]::Copy($dependencySource,$dependencyPath,$false)
    $fixtureBaseTool=Join-Path $project 'tools\New-KnowledgeClipperCoreAuthorityRelease.ps1';[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fixtureBaseTool))|Out-Null;[IO.File]::Copy($baseToolSource,$fixtureBaseTool,$false)

    $emptyRelease=Join-Path $fixture 'empty-release';$emptyAuthority=Join-Path $fixture 'empty-authority';[IO.Directory]::CreateDirectory($emptyRelease)|Out-Null;[IO.Directory]::CreateDirectory($emptyAuthority)|Out-Null
    Write-Utf8 (Join-Path $emptyAuthority 'worker-registry.v1.json') "{`"registryVersion`":`"fixture`",`"workers`":[]}`n"
    Write-Utf8 (Join-Path $emptyAuthority 'dependency-manifest.v1.json') "{`"releaseId`":`"fixture`",`"treeHashAlgorithm`":`"fixture`",`"treeSha256`":`"fixture`",`"keyFiles`":[],`"workerOperations`":[]}`n"

    $orchestrator=Join-Path $runtimeRoot 'KnowledgeClipper.Orchestrator.exe';$describeText=(& $orchestrator describe 2>&1)-join [Environment]::NewLine;if($LASTEXITCODE -ne 0){throw $describeText};$version=[string](($describeText|ConvertFrom-Json).version)
    $runtimeManifestPath=Join-Path $fixture 'runtime.json';$runtimeManifest=RuntimeManifest $runtimeRoot $checkpointRelative $checkpointSha $version;Write-Utf8 $runtimeManifestPath (($runtimeManifest|ConvertTo-Json -Depth 100)+[Environment]::NewLine)
    $closureManifestPath=Join-Path $fixture 'schema-closure.json';$closureManifest=[ordered]@{schemaVersion='knowledge-clipper-core-schema-closure-manifest.v1';checkpoint=[ordered]@{path=$checkpointRelative;sha256=$checkpointSha};dependencies=@([ordered]@{path=$dependencyRelative;bytes=(Get-Item $dependencyPath).Length;sha256=Hash $dependencyPath;referencedBy='contracts/core/v2.10/module-control/schemas/ipc-request.v1.7.schema.json'})};Write-Utf8 $closureManifestPath (($closureManifest|ConvertTo-Json -Depth 20)+[Environment]::NewLine)

    $arguments=@{BaseReleaseRoot=$emptyRelease;BaseAuthorityDirectory=$emptyAuthority;NewRegistryVersion='qualification-'+[Guid]::NewGuid().ToString('N');ReleaseStamp='fixture';OrchestratorPublishRoot=$runtimeRoot;OrchestratorRuntimeManifestPath=$runtimeManifestPath;ExpectedOrchestratorRuntimeManifestSha256=Hash $runtimeManifestPath;ExpectedOrchestratorVersion=$version;ExpectedOrchestratorSha256=Hash $orchestrator;CoreContractRoot=(Join-Path $project 'contracts\core\v2.10');CoreCheckpointPath=$checkpointPath;ExpectedCoreCheckpointSha256=$checkpointSha;SchemaClosureManifestPath=$closureManifestPath;ExpectedSchemaClosureManifestSha256=Hash $closureManifestPath;ExpectedIpcProtocolVersion='1.7';ExpectedRequestContract='knowledge-clipper-module-action.v6';ExpectedReceiptContract='knowledge-clipper-module-action-receipt.v6';PreflightOnly=$true;ProjectRoot=$project}
    $preflightText=(& $wrapperPath @arguments 2>&1)-join [Environment]::NewLine;$preflight=$preflightText|ConvertFrom-Json -Depth 100
    if($preflight.status-ne'passed'-or$preflight.schemaClosure.dependencyFiles-ne1-or$preflight.schemaClosure.codeLegacyHardcodeHits-ne0-or-not$preflight.schemaClosure.checkpointBoundLegacyDependenciesAllowed-or@($preflight.schemaClosure.dependencies.path)-notcontains$dependencyRelative){throw "V3 preflight mismatch: $preflightText"}
    $cases += [ordered]@{name='v3-checkpoint-bound-schema-and-runtime-closure';status='passed-accepted';schemaDependency=$dependencyRelative;runtimeFiles=$preflight.runtime.fileCount;runtimeTreeSha256=$preflight.runtime.treeSha256;codeLegacyHardcodeHits=0}

    $missingClosure=Get-Content $closureManifestPath -Raw|ConvertFrom-Json -Depth 100;$missingClosure.dependencies=@();$path=Join-Path $fixture 'closure-missing.json';Write-Utf8 $path (($missingClosure|ConvertTo-Json -Depth 20)+"`n");$a=$arguments.Clone();$a.SchemaClosureManifestPath=$path;$a.ExpectedSchemaClosureManifestSha256=Hash $path;$cases+=Invoke-Rejected $a 'closure count drifted' 'schema-closure-missing'
    $mixedClosure=Get-Content $closureManifestPath -Raw|ConvertFrom-Json -Depth 100;$mixedClosure.checkpoint.sha256='0'*64;$path=Join-Path $fixture 'closure-mixed-checkpoint.json';Write-Utf8 $path (($mixedClosure|ConvertTo-Json -Depth 20)+"`n");$a=$arguments.Clone();$a.SchemaClosureManifestPath=$path;$a.ExpectedSchemaClosureManifestSha256=Hash $path;$cases+=Invoke-Rejected $a 'different Core checkpoint' 'schema-closure-mixed-checkpoint'
    $escapedClosure=Get-Content $closureManifestPath -Raw|ConvertFrom-Json -Depth 100;$escapedClosure.dependencies[0].path='../escape.json';$path=Join-Path $fixture 'closure-path.json';Write-Utf8 $path (($escapedClosure|ConvertTo-Json -Depth 20)+"`n");$a=$arguments.Clone();$a.SchemaClosureManifestPath=$path;$a.ExpectedSchemaClosureManifestSha256=Hash $path;$cases+=Invoke-Rejected $a 'invalid or escaped path' 'schema-closure-path-traversal'

    [IO.File]::AppendAllText($dependencyPath,"`n",[Text.UTF8Encoding]::new($false));$cases+=Invoke-Rejected $arguments 'dependency drifted|evidence drifted' 'schema-closure-payload-drift';[IO.File]::Copy($dependencySource,$dependencyPath,$true)

    [IO.File]::AppendAllText($fixtureBaseTool,"`n# drift",[Text.UTF8Encoding]::new($false));$cases+=Invoke-Rejected $arguments 'Pinned base assembler drifted' 'base-assembler-drift';[IO.File]::Copy($baseToolSource,$fixtureBaseTool,$true)

    $missingRuntime=Get-Content $runtimeManifestPath -Raw|ConvertFrom-Json -Depth 100;$missingRuntime.files=@($missingRuntime.files|Where-Object { $_.path -ne 'KnowledgeClipper.Orchestrator.dll' });$missingRuntime.fileCount=@($missingRuntime.files).Count;$path=Join-Path $fixture 'runtime-missing.json';Write-Utf8 $path (($missingRuntime|ConvertTo-Json -Depth 100)+"`n");$a=$arguments.Clone();$a.OrchestratorRuntimeManifestPath=$path;$a.ExpectedOrchestratorRuntimeManifestSha256=Hash $path;$cases+=Invoke-Rejected $a 'missing required payload: KnowledgeClipper\.Orchestrator\.dll' 'runtime-required-dll-missing'
    $mixedRuntime=Get-Content $runtimeManifestPath -Raw|ConvertFrom-Json -Depth 100;$mixedRuntime.component.version='0.0.0-mixed';$path=Join-Path $fixture 'runtime-mixed.json';Write-Utf8 $path (($mixedRuntime|ConvertTo-Json -Depth 100)+"`n");$a=$arguments.Clone();$a.OrchestratorRuntimeManifestPath=$path;$a.ExpectedOrchestratorRuntimeManifestSha256=Hash $path;$cases+=Invoke-Rejected $a 'component identity/version mismatch' 'runtime-mixed-version'
    $escapedRuntime=Get-Content $runtimeManifestPath -Raw|ConvertFrom-Json -Depth 100;$escapedRuntime.files[0].path='../escape.dll';$path=Join-Path $fixture 'runtime-path.json';Write-Utf8 $path (($escapedRuntime|ConvertTo-Json -Depth 100)+"`n");$a=$arguments.Clone();$a.OrchestratorRuntimeManifestPath=$path;$a.ExpectedOrchestratorRuntimeManifestSha256=Hash $path;$cases+=Invoke-Rejected $a 'Invalid, escaped, or duplicate' 'runtime-path-traversal'

    $runtimeDriftRoot=Join-Path $fixture 'runtime-drift';[IO.Directory]::CreateDirectory($runtimeDriftRoot)|Out-Null
    foreach($name in @('KnowledgeClipper.Orchestrator.exe','KnowledgeClipper.Orchestrator.dll','KnowledgeClipper.Orchestrator.deps.json','KnowledgeClipper.Orchestrator.runtimeconfig.json')){[IO.File]::Copy((Join-Path $runtimeRoot $name),(Join-Path $runtimeDriftRoot $name),$false)}
    $runtimeDriftManifestPath=Join-Path $fixture 'runtime-drift.json';$runtimeDriftManifest=RuntimeManifest $runtimeDriftRoot $checkpointRelative $checkpointSha $version;Write-Utf8 $runtimeDriftManifestPath (($runtimeDriftManifest|ConvertTo-Json -Depth 100)+"`n");[IO.File]::AppendAllText((Join-Path $runtimeDriftRoot 'KnowledgeClipper.Orchestrator.dll'),'drift',[Text.UTF8Encoding]::new($false));$a=$arguments.Clone();$a.OrchestratorPublishRoot=$runtimeDriftRoot;$a.OrchestratorRuntimeManifestPath=$runtimeDriftManifestPath;$a.ExpectedOrchestratorRuntimeManifestSha256=Hash $runtimeDriftManifestPath;$cases+=Invoke-Rejected $a 'aggregate/tree drifted' 'runtime-payload-drift'

    $stagingArgs=$arguments.Clone();$stagingArgs.BaseReleaseRoot=$baseRelease;$stagingArgs.BaseAuthorityDirectory=$baseAuthority;$stagingArgs.NewRegistryVersion='1.2.998';$stagingArgs.Remove('PreflightOnly');$stagingArgs.StagingValidationOnly=$true
    $stagingText=(& $wrapperPath @stagingArgs 2>&1)-join [Environment]::NewLine;$staging=$stagingText|ConvertFrom-Json -Depth 100
    if($staging.status-ne'staging-validated'-or$staging.validation.status-ne'valid'-or$staging.validation.workers-ne5-or$staging.authorityTargetsCreated-ne0-or$staging.candidateTargetsCreated-ne0){throw "V3 staging mismatch: $stagingText"}
    $finalRelease=Join-Path $project 'releases\knowledge-clipper-authority-v1.2.998-fixture';$finalAuthority=Join-Path $project 'contracts\authority\worker-registry\v1.2.998-fixture';$leftovers=@(Get-ChildItem (Join-Path $project 'releases') -Directory -Force -ErrorAction SilentlyContinue|Where-Object { $_.Name -like '.*.tmp' })+@(Get-ChildItem (Join-Path $project 'contracts\authority\worker-registry') -Directory -Force -ErrorAction SilentlyContinue|Where-Object { $_.Name -like '.*.tmp' })
    if((Test-Path $finalRelease)-or(Test-Path $finalAuthority)-or$leftovers.Count){throw 'V3 staging validation promoted or leaked targets.'}
    $cases += [ordered]@{name='v3-staging-validation-only';status='passed-accepted';registryStatus=$staging.validation.status;workers=$staging.validation.workers;formalTargets=0;stagingDirectories=0}

    $receipt=[ordered]@{schemaVersion='knowledge-clipper-core-authority-assembler-v3-qualification.v1';status='passed';createdAt=(Get-Date).ToUniversalTime().ToString('o');assembler=[ordered]@{path='tools/New-KnowledgeClipperCoreAuthorityReleaseV3.ps1';sha256=Hash $wrapperPath;baseAssemblerPath='tools/New-KnowledgeClipperCoreAuthorityRelease.ps1';baseAssemblerSha256=$baseToolSha};qualification=[ordered]@{path='tests/AuthorityToolQualification/Invoke-CoreAuthorityReleaseV3Qualification.ps1';sha256=Hash $PSCommandPath;externalServicesCalled=0;formalAuthorityTargetsCreated=0;candidateTargetsCreated=0};checkpoint=[ordered]@{path=$checkpointRelative;sha256=$checkpointSha};schemaClosure=[ordered]@{dependencies=1;legacyDependency=$dependencyRelative;legacyDependencySha256=Hash $dependencySource;codeLegacyHardcodeHits=0};runtime=[ordered]@{files=$runtimeManifest.fileCount;bytes=$runtimeManifest.totalBytes;treeSha256=$runtimeManifest.treeSha256};cases=$cases}
    if(-not[string]::IsNullOrWhiteSpace($QualificationOutputPath)){$QualificationOutputPath=[IO.Path]::GetFullPath($QualificationOutputPath);if(Test-Path $QualificationOutputPath){throw "Append-only receipt exists: $QualificationOutputPath"};Write-Utf8 $QualificationOutputPath (($receipt|ConvertTo-Json -Depth 100)+"`n")}
    $receipt|ConvertTo-Json -Depth 100
}
finally{if(Test-Path $fixture){$resolved=[IO.Path]::GetFullPath($fixture);if(-not$resolved.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Unsafe cleanup: $resolved"};[IO.Directory]::Delete($resolved,$true)}}

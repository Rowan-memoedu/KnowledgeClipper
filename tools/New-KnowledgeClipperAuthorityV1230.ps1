[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$version = '1.2.30'; $stamp = '20260901T034000Z'
$baseRelease = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.29-20260901T023500Z\win-x64'
$baseAuthority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.29-20260901T023500Z'
$releaseContainer = Join-Path $ProjectRoot "releases\knowledge-clipper-authority-v$version-$stamp"
$releaseRoot = Join-Path $releaseContainer 'win-x64'
$authorityRoot = Join-Path $ProjectRoot "contracts\authority\worker-registry\v$version-$stamp"
$stagingRelease = Join-Path $ProjectRoot ('releases\.' + [guid]::NewGuid().ToString('N') + '.tmp')
$stagingRoot = Join-Path $stagingRelease 'win-x64'
$stagingAuthority = Join-Path $ProjectRoot ('contracts\authority\worker-registry\.v' + $version + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
$runtime = Join-Path $ProjectRoot 'dist\orchestrator\win-x64\v2.7.9'
$coreRelative = 'contracts/core/v2.7/checkpoints/v2.7.17-20260901T033000Z/checkpoint.json'
$coreCheckpoint = Join-Path $ProjectRoot $coreRelative.Replace('/','\')

function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }
function WriteJson([string]$path, [object]$value) { [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null; [IO.File]::WriteAllText($path, (($value | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false)) }
function CopyTree([string]$source, [string]$destination) { [IO.Directory]::CreateDirectory($destination) | Out-Null; foreach($file in [IO.Directory]::EnumerateFiles($source,'*',[IO.SearchOption]::AllDirectories)) { $target=Join-Path $destination ([IO.Path]::GetRelativePath($source,$file)); [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null; [IO.File]::Copy($file,$target,$false) } }
function ReleaseTree([string]$root) { $files=@(Get-ChildItem -LiteralPath $root -Recurse -File); $rows=[string[]]@($files | ForEach-Object { $relative=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/'); "$relative`t$($_.Length)`t$((Hash $_.FullName).ToLowerInvariant())" }); [array]::Sort($rows,[StringComparer]::Ordinal); $payload=[Text.Encoding]::UTF8.GetBytes((($rows -join "`n")+"`n")); return [ordered]@{ files=$files.Count; bytes=[long](($files | Measure-Object Length -Sum).Sum); treeSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)) } }

if ((Test-Path -LiteralPath $releaseContainer) -or (Test-Path -LiteralPath $authorityRoot)) { throw 'Append-only v1.2.30 target already exists.' }
foreach($required in @($baseRelease,$baseAuthority,$runtime,$coreCheckpoint)) { if(-not(Test-Path -LiteralPath $required)) { throw "Missing v1.2.30 input: $required" } }

try {
 CopyTree $baseRelease $stagingRoot
 Remove-Item -LiteralPath (Join-Path $stagingRoot 'orchestrator') -Recurse -Force
 CopyTree $runtime (Join-Path $stagingRoot 'orchestrator')
 $stagedCheckpoint=Join-Path $stagingRoot $coreRelative.Replace('/','\'); [IO.Directory]::CreateDirectory((Split-Path -Parent $stagedCheckpoint))|Out-Null; [IO.File]::Copy($coreCheckpoint,$stagedCheckpoint,$false)
 $frozen=Join-Path $ProjectRoot 'contracts\v1\schemas\common\artifact.schema.json'; $packed=Join-Path $stagingRoot 'contracts\v1\schemas\common\artifact.schema.json'
 if((-not(Test-Path -LiteralPath $packed)) -or ((Hash $frozen) -ne (Hash $packed))) { throw 'Frozen schema was not packed unchanged.' }
 [IO.Directory]::CreateDirectory($stagingAuthority)|Out-Null
 $registry=Get-Content (Join-Path $baseAuthority 'worker-registry.v1.json') -Raw | ConvertFrom-Json -Depth 100; $registry.registryVersion=$version; WriteJson (Join-Path $stagingAuthority 'worker-registry.v1.json') $registry
 $oldCheckpoint=Get-Content (Join-Path $baseAuthority 'checkpoint.json') -Raw | ConvertFrom-Json -Depth 100; $monotonic=@()
 foreach($worker in $registry.workers) { $exe=Join-Path $stagingRoot $worker.entrypoint.path; $current=((& $exe describe --json 2>&1)-join "`n")|ConvertFrom-Json; $old=$oldCheckpoint.workers | Where-Object workerId -eq $worker.workerId | Select-Object -First 1; if($null -eq $old -or $current.workerId -ne $worker.workerId -or [version]$current.workerVersion -lt [version]$old.workerVersion -or ($current.workerVersion -eq $old.workerVersion -and (Hash $exe) -ne $old.sha256)) { throw "Worker monotonicity rejected: $($worker.workerId)" }; WriteJson (Join-Path $stagingAuthority ('describe\'+$worker.workerId+'.describe.json')) $current; $monotonic += [ordered]@{workerId=$worker.workerId;previousVersion=$old.workerVersion;previousSha256=$old.sha256;currentVersion=$current.workerVersion;currentSha256=Hash $exe;status='passed'} }
 $tree=ReleaseTree $stagingRoot
 $dependency=[ordered]@{schemaVersion='knowledge-clipper-release-dependency-manifest.v1';releaseId="knowledge-clipper-authority-v$version-$stamp";releaseRoot="releases/knowledge-clipper-authority-v$version-$stamp/win-x64";files=$tree.files;bytes=$tree.bytes;treeHashAlgorithm='sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF; SHA-256 UTF-8';treeSha256=$tree.treeSha256;keyFiles=@([ordered]@{path=$coreRelative;bytes=(Get-Item $stagedCheckpoint).Length;sha256=Hash $stagedCheckpoint},[ordered]@{path='contracts/v1/schemas/common/artifact.schema.json';bytes=(Get-Item $packed).Length;sha256=Hash $packed})}; WriteJson (Join-Path $stagingAuthority 'dependency-manifest.v1.json') $dependency
 $closure=(& python (Join-Path $ProjectRoot 'tools\validate_release_schema_closure.py') --release-root $stagingRoot 2>&1)|ConvertFrom-Json; if($LASTEXITCODE -ne 0 -or $closure.status -ne 'passed'){throw 'Portable schema closure failed.'}; WriteJson (Join-Path $stagingAuthority 'portable-schema-closure-gate.v1.json') ([ordered]@{schemaVersion='knowledge-clipper-portable-schema-closure-gate.v1';status='passed';schemasChecked=$closure.schemasChecked;localRefsChecked=$closure.localRefsChecked;failures=@();frozenArtifactSchemaPath='contracts/v1/schemas/common/artifact.schema.json';frozenArtifactSchemaSha256=Hash $packed;frozenArtifactSchemaPackedUnchanged=$true}); WriteJson (Join-Path $stagingAuthority 'worker-monotonicity-gate.v1.json') ([ordered]@{schemaVersion='knowledge-clipper-authority-worker-monotonicity-gate.v1';status='passed';baseRegistryVersion='1.2.29';registryVersion=$version;workers=$monotonic})
 $validation=((& (Join-Path $stagingRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe') validate-registry --registry (Join-Path $stagingAuthority 'worker-registry.v1.json') --release-root $stagingRoot 2>&1)-join "`n")|ConvertFrom-Json; if($LASTEXITCODE -ne 0 -or $validation.status -ne 'valid' -or $validation.workers -ne 5){throw 'validate-registry failed.'}
 [IO.Directory]::Move($stagingRelease,$releaseContainer);[IO.Directory]::Move($stagingAuthority,$authorityRoot)
 [ordered]@{status='candidate-built';releaseRoot=$releaseRoot;authorityRoot=$authorityRoot;release=$tree;validateRegistry=$validation;coreCheckpointSha256=Hash $coreCheckpoint;workers=$monotonic}|ConvertTo-Json -Depth 30
} finally { if(Test-Path -LiteralPath $stagingRelease){Remove-Item -LiteralPath $stagingRelease -Recurse -Force}; if(Test-Path -LiteralPath $stagingAuthority){Remove-Item -LiteralPath $stagingAuthority -Recurse -Force} }

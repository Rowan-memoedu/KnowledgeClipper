[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$authority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.26-20260901T013500Z'
$release = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.26-20260901T013500Z\win-x64'
$evidence = Join-Path $ProjectRoot 'canary\authority-v1.2.26-offline\20260901T015000Z'
$registryPath = Join-Path $authority 'worker-registry.v1.json'
$dependencyPath = Join-Path $authority 'dependency-manifest.v1.json'
$checkpointPath = Join-Path $authority 'checkpoint.json'
$closureGatePath = Join-Path $authority 'portable-schema-closure-gate.v1.json'
$runtimeGatePath = Join-Path $ProjectRoot 'canary\core-authority-v1.2.26\20260901T014000Z\runtime-gates.json'
$coreCheckpointPath = Join-Path $ProjectRoot 'contracts\core\v2.7\checkpoints\v2.7.12-20260901T013000Z\checkpoint.json'

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Tree([string]$root) {
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
    $rows = [string[]]@($files | ForEach-Object { "$([IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/'))`t$($_.Length)`t$((Sha $_.FullName).ToLowerInvariant())" })
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    [ordered]@{files=$files.Count;bytes=[long](($files | Measure-Object Length -Sum).Sum);treeSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))))}
}
foreach ($path in @($registryPath,$dependencyPath,$checkpointPath,$closureGatePath,$runtimeGatePath,$coreCheckpointPath)) { Require (Test-Path -LiteralPath $path -PathType Leaf) "Qualification input missing: $path" }
if (Test-Path -LiteralPath $evidence) { throw "Append-only authority qualification root exists: $evidence" }
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -Depth 100
$dependency = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json -Depth 100
$checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json -Depth 100
$closureGate = Get-Content -LiteralPath $closureGatePath -Raw | ConvertFrom-Json -Depth 100
$runtimeGate = Get-Content -LiteralPath $runtimeGatePath -Raw | ConvertFrom-Json -Depth 100
Require ($registry.registryVersion -eq '1.2.26' -and @($registry.workers).Count -eq 5) 'Registry identity is invalid.'
Require ($checkpoint.admissionStatus -eq 'authority-admitted' -and $checkpoint.portableSchemaClosure.status -eq 'passed') 'Checkpoint does not bind the portable schema closure gate.'
Require ($runtimeGate.status -eq 'passed' -and $runtimeGate.workerRunInvocations -eq 0 -and $runtimeGate.externalWrites -eq 0) 'Runtime gate is invalid.'
Require ($closureGate.status -eq 'passed' -and $closureGate.registrySha256 -eq (Sha $registryPath) -and $closureGate.dependencyManifestSha256 -eq (Sha $dependencyPath) -and @($closureGate.failures).Count -eq 0) 'Portable schema closure gate drifted.'
$orchestrator = Join-Path $release 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$validationText = (& $orchestrator validate-registry --registry $registryPath --release-root $release 2>&1) -join [Environment]::NewLine
$validation = $validationText | ConvertFrom-Json -Depth 40
Require ($LASTEXITCODE -eq 0 -and $validation.status -eq 'valid' -and $validation.registryVersion -eq '1.2.26' -and $validation.workers -eq 5) "Registry validation failed: $validationText"
$describes = @()
foreach ($worker in $registry.workers) {
    $exe = Join-Path $release $worker.entrypoint.path
    $text = (& $exe describe --json 2>&1) -join [Environment]::NewLine
    $describe = $text | ConvertFrom-Json -Depth 50
    Require ($LASTEXITCODE -eq 0 -and $describe.workerId -eq $worker.workerId) "Worker describe failed: $($worker.workerId)"
    if ($worker.workerId -eq 'content.deepseek') { Require ($describe.workerVersion -eq '0.9.2') 'Content authority entrypoint is not 0.9.2.' }
    $describes += [ordered]@{workerId=$worker.workerId;workerVersion=$describe.workerVersion;entrypoint=$worker.entrypoint.path;sha256=Sha $exe}
}
$closureText = (& python (Join-Path $PSScriptRoot 'validate_release_schema_closure.py') --release-root $release 2>&1) -join [Environment]::NewLine
$closure = $closureText | ConvertFrom-Json -Depth 100
Require ($LASTEXITCODE -eq 0 -and $closure.status -eq 'passed' -and $closure.schemasChecked -eq $closureGate.schemasChecked -and $closure.localRefsChecked -eq $closureGate.localRefsChecked -and @($closure.failures).Count -eq 0) 'Portable schema closure validation failed.'
$packedArtifact = Join-Path $release 'contracts\v1\schemas\common\artifact.schema.json'
$frozenArtifact = Join-Path $ProjectRoot 'contracts\v1\schemas\common\artifact.schema.json'
Require ((Sha $packedArtifact) -eq (Sha $frozenArtifact) -and (Sha $packedArtifact) -eq $closureGate.frozenArtifactSchemaSha256) 'Packed frozen artifact schema changed or is missing.'
$tree = Tree $release
Require ($tree.files -eq $dependency.files -and $tree.bytes -eq $dependency.bytes -and $tree.treeSha256 -eq $dependency.treeSha256) 'Dependency manifest tree drifted.'
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$receipt = [ordered]@{
    schemaVersion='knowledge-clipper-authority-v1.2.26-offline-qualification.v1';generatedAt=[DateTimeOffset]::UtcNow.ToString('o');status='passed-offline-authority-qualified'
    authority=[ordered]@{registryVersion='1.2.26';registry=[ordered]@{path=$registryPath;sha256=Sha $registryPath};checkpoint=[ordered]@{path=$checkpointPath;sha256=Sha $checkpointPath};dependencyManifest=[ordered]@{path=$dependencyPath;sha256=Sha $dependencyPath};portableSchemaClosureGate=[ordered]@{path=$closureGatePath;sha256=Sha $closureGatePath}}
    release=[ordered]@{root=$release;files=$tree.files;bytes=$tree.bytes;treeSha256=$tree.treeSha256}
    core=[ordered]@{checkpoint=[ordered]@{path=$coreCheckpointPath;sha256=Sha $coreCheckpointPath};orchestrator=[ordered]@{path=$orchestrator;sha256=Sha $orchestrator}}
    validation=[ordered]@{registry=$validation;portableSchemaClosure=$closure;frozenArtifactSchema=[ordered]@{path='contracts/v1/schemas/common/artifact.schema.json';sha256=Sha $packedArtifact;unchanged=$true}}
    workers=$describes;boundFixes=[ordered]@{content='content.deepseek@0.9.2';anki='export.anki@1.3.2';portableFrozenSchemaClosure=$true};boundaries=[ordered]@{workerRunInvocations=0;externalCalls=0;mineruCalls=0;ankiWrites=0;remnoteWrites=0;physicalSpeakerInvocations=0}
}
$receiptPath = Join-Path $evidence 'qualification-receipt.json'
[IO.File]::WriteAllText($receiptPath, (($receipt | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{status=$receipt.status;receipt=$receiptPath;sha256=Sha $receiptPath;release=$tree;content=$describes | Where-Object workerId -eq 'content.deepseek'} | ConvertTo-Json -Depth 40

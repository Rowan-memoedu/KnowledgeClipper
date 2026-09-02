[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$authority = Join-Path $ProjectRoot 'contracts/authority/worker-registry/v1.2.18-20260831T085000Z'
$release = Join-Path $ProjectRoot 'releases/knowledge-clipper-authority-v1.2.18-20260831T085000Z/win-x64'
$receiptPath = Join-Path $EvidenceRoot 'qualification-receipt.json'
if (Test-Path -LiteralPath $EvidenceRoot) { throw "Append-only evidence root exists: $EvidenceRoot" }

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Tree([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File)
    $rows = [string[]]@($files | ForEach-Object {
        "$([IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\','/'))`t$($_.Length)`t$((Sha $_.FullName).ToLowerInvariant())"
    })
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    [ordered]@{
        files = $files.Count
        bytes = [long](($files | Measure-Object Length -Sum).Sum)
        treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))))
    }
}

$registryPath = Join-Path $authority 'worker-registry.v1.json'
$checkpointPath = Join-Path $authority 'checkpoint.json'
$dependencyPath = Join-Path $authority 'dependency-manifest.v1.json'
$assemblyPath = Join-Path $authority 'assembly-receipt.v1.json'
$pathGatePath = Join-Path $authority 'assembly-path-gate.v1.json'
$closureGatePath = Join-Path $authority 'portable-schema-closure-gate.v1.json'
$coreCheckpointPath = Join-Path $ProjectRoot 'contracts/core/v2.7/checkpoints/v2.7.3-20260831T084500Z/checkpoint.json'
$runtimeReceiptPath = Join-Path $ProjectRoot 'canary/core-v2.7.2-offline/20260831T084500Z/core-adapter.receipt.json'
foreach ($path in @($registryPath,$checkpointPath,$dependencyPath,$assemblyPath,$pathGatePath,$closureGatePath,$coreCheckpointPath,$runtimeReceiptPath)) {
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Qualification input missing: $path"
}

$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -Depth 100
$checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json -Depth 100
$dependency = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json -Depth 100
$pathGate = Get-Content -LiteralPath $pathGatePath -Raw | ConvertFrom-Json -Depth 100
$closureGate = Get-Content -LiteralPath $closureGatePath -Raw | ConvertFrom-Json -Depth 100
$coreCheckpoint = Get-Content -LiteralPath $coreCheckpointPath -Raw | ConvertFrom-Json -Depth 100
$runtimeReceipt = Get-Content -LiteralPath $runtimeReceiptPath -Raw | ConvertFrom-Json -Depth 100

Require ($registry.registryVersion -eq '1.2.18' -and $registry.workers.Count -eq 5) 'Registry identity mismatch.'
Require ($checkpoint.admissionStatus -eq 'authority-admitted') 'Authority is not admitted.'
Require ($coreCheckpoint.core.version -eq '2.7.2' -and $coreCheckpoint.status -eq 'passed-offline-authority-admission-ready') 'Core checkpoint mismatch.'
Require ($pathGate.status -eq 'passed' -and $pathGate.pathCount -eq 5 -and $pathGate.allExist -and $pathGate.allBytesAndHashesMatch) 'Assembly path gate failed.'
Require ($runtimeReceipt.status -eq 'passed') 'Core adapter runtime receipt failed.'

$orchestrator = Join-Path $release 'orchestrator/KnowledgeClipper.Orchestrator.exe'
$validateText = ((& $orchestrator validate-registry --registry $registryPath --release-root $release 2>&1) -join [Environment]::NewLine)
$validateExit = $LASTEXITCODE
$validate = $validateText | ConvertFrom-Json -Depth 30
Require ($validateExit -eq 0 -and $validate.status -eq 'valid' -and $validate.registryVersion -eq '1.2.18' -and $validate.workers -eq 5) "validate-registry failed: $validateText"

$expected = [ordered]@{
    'image.ocr.mineru'='1.2.4'; 'content.deepseek'='0.9.0'; 'speech.edge'='1.2.5'; 'export.remnote'='2.2.0'; 'export.anki'='1.3.1'
}
$describes = @()
foreach ($worker in $registry.workers) {
    $exe = Join-Path $release $worker.entrypoint.path
    $text = ((& $exe describe --json 2>&1) -join [Environment]::NewLine)
    $exit = $LASTEXITCODE
    $describe = $text | ConvertFrom-Json -Depth 50
    Require ($exit -eq 0 -and $describe.workerId -eq $worker.workerId -and $describe.workerVersion -eq $expected[$worker.workerId]) "Worker describe mismatch: $($worker.workerId)"
    $describes += [ordered]@{ workerId=$worker.workerId; workerVersion=$describe.workerVersion; executable=$worker.entrypoint.path; executableSha256=Sha $exe }
}

$closureText = ((& python (Join-Path $PSScriptRoot 'validate_release_schema_closure.py') --release-root $release 2>&1) -join [Environment]::NewLine)
$closureExit = $LASTEXITCODE
$closure = $closureText | ConvertFrom-Json -Depth 100
Require ($closureExit -eq 0 -and $closure.status -eq 'passed' -and $closure.failures.Count -eq 0 -and $closure.schemasChecked -eq $closureGate.schemasChecked -and $closure.localRefsChecked -eq $closureGate.localRefsChecked) "Portable schema closure failed: $closureText"
$frozenSource = Join-Path $ProjectRoot 'contracts/v1/schemas/common/artifact.schema.json'
$frozenPacked = Join-Path $release 'contracts/v1/schemas/common/artifact.schema.json'
Require ((Sha $frozenSource) -eq (Sha $frozenPacked)) 'Packed frozen artifact schema content drifted.'

$tree = Tree $release
Require ($tree.files -eq $dependency.files -and $tree.bytes -eq $dependency.bytes -and $tree.treeSha256 -eq $dependency.treeSha256) 'Release tree drifted from dependency manifest.'

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-v1.2.18-offline-qualification.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed-offline-authority-qualified'
    authority = [ordered]@{ registryVersion='1.2.18'; root=$authority; registry=[ordered]@{path=$registryPath;sha256=Sha $registryPath}; checkpoint=[ordered]@{path=$checkpointPath;sha256=Sha $checkpointPath}; dependencyManifest=[ordered]@{path=$dependencyPath;sha256=Sha $dependencyPath}; assemblyReceipt=[ordered]@{path=$assemblyPath;sha256=Sha $assemblyPath}; assemblyPathGate=[ordered]@{path=$pathGatePath;sha256=Sha $pathGatePath} }
    release = [ordered]@{ root=$release; files=$tree.files; bytes=$tree.bytes; treeSha256=$tree.treeSha256 }
    core = [ordered]@{ version='2.7.2'; checkpoint=[ordered]@{path=$coreCheckpointPath;sha256=Sha $coreCheckpointPath}; runtimeEvidence=[ordered]@{path=$runtimeReceiptPath;sha256=Sha $runtimeReceiptPath} }
    workers = $describes
    validation = [ordered]@{ registry=$validate; portableSchemaClosure=[ordered]@{status=$closure.status;schemasChecked=$closure.schemasChecked;localRefsChecked=$closure.localRefsChecked;failures=$closure.failures;gate=[ordered]@{path=$closureGatePath;sha256=Sha $closureGatePath}}; frozenArtifactSchema=[ordered]@{source=$frozenSource;packed=$frozenPacked;sha256=Sha $frozenPacked;modified=$false} }
    boundaries = [ordered]@{ workerRunInvocations=0; externalCalls=0; mineruCalls=0; ankiWrites=0; remnoteWrites=0; physicalSpeakerInvocations=0 }
}
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
[IO.File]::WriteAllText($receiptPath, (($receipt | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{receipt=$receiptPath;sha256=Sha $receiptPath;status=$receipt.status;release=$tree;portableSchemaClosure=$receipt.validation.portableSchemaClosure} | ConvertTo-Json -Depth 30

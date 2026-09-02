[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$release = Join-Path $ProjectRoot 'releases/knowledge-clipper-authority-v1.2.17-20260831T062800Z/win-x64'
$authority = Join-Path $ProjectRoot 'contracts/authority/worker-registry/v1.2.17-20260831T062800Z'
$registryPath = Join-Path $authority 'worker-registry.v1.json'
$checkpointPath = Join-Path $authority 'checkpoint.json'
$dependencyPath = Join-Path $authority 'dependency-manifest.v1.json'
$closureGatePath = Join-Path $authority 'portable-schema-closure-gate.v1.json'
$coreCheckpointPath = Join-Path $ProjectRoot 'contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json'
$adapterReceiptPath = Join-Path $ProjectRoot 'canary/core-v2.7.1-offline/20260831T062200Z/core-adapter.receipt.json'
$receiptPath = Join-Path $EvidenceRoot 'qualification-receipt.json'
if (Test-Path -LiteralPath $EvidenceRoot) { throw "Append-only evidence root exists: $EvidenceRoot" }

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
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

foreach ($required in @($registryPath,$checkpointPath,$dependencyPath,$closureGatePath,$coreCheckpointPath,$adapterReceiptPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Qualification input missing: $required" }
}
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -Depth 100
$checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json -Depth 100
$dependency = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json -Depth 100
$closureGate = Get-Content -LiteralPath $closureGatePath -Raw | ConvertFrom-Json -Depth 100
$coreCheckpoint = Get-Content -LiteralPath $coreCheckpointPath -Raw | ConvertFrom-Json -Depth 100
$adapterReceipt = Get-Content -LiteralPath $adapterReceiptPath -Raw | ConvertFrom-Json -Depth 100

if ($registry.registryVersion -ne '1.2.17' -or $registry.workers.Count -ne 5 -or $checkpoint.admissionStatus -ne 'authority-admitted') { throw 'Authority identity/admission mismatch.' }
if ($coreCheckpoint.core.version -ne '2.7.1' -or $coreCheckpoint.status -ne 'passed-offline-authority-admission-ready') { throw 'Core checkpoint is not the admitted v2.7.1 checkpoint.' }
if ((Sha $coreCheckpointPath) -ne 'B22CA9746905AC923CBFFEF913A7CAEB3D7FF0EDDE8507145E6A7B99BBD09F65') { throw 'Core checkpoint hash drifted.' }
$moduleAction = @($adapterReceipt.results | Where-Object name -eq 'module-action-public-dispatch')
if ($moduleAction.Count -ne 1 -or $moduleAction[0].status -ne 'passed' -or $moduleAction[0].remnoteProbeWrites -ne 0) { throw 'Core module-action regression evidence is missing.' }
$typedStates = @($moduleAction[0].remnoteTypedStates)
foreach ($state in @('degraded','blocked')) {
    $entry = @($typedStates | Where-Object state -eq $state)
    if ($entry.Count -ne 1 -or -not $entry[0].accepted -or -not $entry[0].typedResultPreserved -or $entry[0].externalWrites -ne 0 -or -not $entry[0].replayStable) { throw "Typed RemNote state was not preserved: $state" }
}
$malformed = @($typedStates | Where-Object state -eq 'malformed')
if ($malformed.Count -ne 1 -or $malformed[0].accepted -or $malformed[0].typedResultPreserved -or $malformed[0].externalWrites -ne 0) { throw 'Malformed nonzero output did not fail closed.' }

$orchestrator = Join-Path $release 'orchestrator/KnowledgeClipper.Orchestrator.exe'
$validationText = ((& $orchestrator validate-registry --registry $registryPath --release-root $release 2>&1) -join [Environment]::NewLine)
$validationExit = $LASTEXITCODE
$validation = $validationText | ConvertFrom-Json -Depth 30
if ($validationExit -ne 0 -or $validation.status -ne 'valid' -or $validation.registryVersion -ne '1.2.17' -or $validation.workers -ne 5) { throw "validate-registry failed: $validationText" }

$expected = [ordered]@{
    'image.ocr.mineru'='1.2.3'
    'content.deepseek'='0.9.0'
    'speech.edge'='1.2.5'
    'export.remnote'='2.2.0'
    'export.anki'='1.3.1'
}
$describes = @()
foreach ($worker in $registry.workers) {
    $exe = Join-Path $release $worker.entrypoint.path
    $describeText = ((& $exe describe --json 2>&1) -join [Environment]::NewLine)
    $describeExit = $LASTEXITCODE
    $describe = $describeText | ConvertFrom-Json -Depth 50
    if ($describeExit -ne 0 -or $describe.workerId -ne $worker.workerId -or $describe.workerVersion -ne $expected[$worker.workerId]) { throw "Worker describe mismatch: $($worker.workerId)" }
    $describes += [ordered]@{ workerId=$worker.workerId; workerVersion=$describe.workerVersion; executable=$worker.entrypoint.path; executableSha256=Sha $exe }
}

$closureText = ((& python (Join-Path $PSScriptRoot 'validate_release_schema_closure.py') --release-root $release 2>&1) -join [Environment]::NewLine)
$closureExit = $LASTEXITCODE
$closure = $closureText | ConvertFrom-Json -Depth 100
if ($closureExit -ne 0 -or $closure.status -ne 'passed' -or $closure.failures.Count -ne 0 -or $closure.schemasChecked -ne $closureGate.schemasChecked -or $closure.localRefsChecked -ne $closureGate.localRefsChecked) { throw "Portable schema closure failed: $closureText" }
$frozenSource = Join-Path $ProjectRoot 'contracts/v1/schemas/common/artifact.schema.json'
$frozenPacked = Join-Path $release 'contracts/v1/schemas/common/artifact.schema.json'
if ((Sha $frozenSource) -ne (Sha $frozenPacked)) { throw 'Packed frozen artifact schema content drifted.' }

$tree = Tree $release
if ($tree.files -ne $dependency.files -or $tree.bytes -ne $dependency.bytes -or $tree.treeSha256 -ne $dependency.treeSha256) { throw 'Release tree drifted from dependency manifest.' }

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-v1.2.17-offline-qualification.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed-offline'
    authority = [ordered]@{
        registryVersion='1.2.17'; root=$authority
        registry=[ordered]@{ path=$registryPath; sha256=Sha $registryPath }
        checkpoint=[ordered]@{ path=$checkpointPath; sha256=Sha $checkpointPath }
        dependencyManifest=[ordered]@{ path=$dependencyPath; sha256=Sha $dependencyPath }
    }
    release = [ordered]@{ root=$release; files=$tree.files; bytes=$tree.bytes; treeSha256=$tree.treeSha256 }
    core = [ordered]@{
        version='2.7.1'; checkpoint=[ordered]@{ path=$coreCheckpointPath; sha256=Sha $coreCheckpointPath }
        executable=[ordered]@{ path=$orchestrator; sha256=Sha $orchestrator }
        moduleAction=[ordered]@{
            healthy='accepted-typed'; degraded='accepted-typed-even-on-nonzero-exit'; blocked='accepted-typed-even-on-nonzero-exit'
            malformed='generic-fail-closed'; remnoteWrites=0; replay='exact-idempotent'; evidence=[ordered]@{ path=$adapterReceiptPath; sha256=Sha $adapterReceiptPath }
        }
    }
    validation = [ordered]@{
        registry=$validation; workerDescribe=$describes
        portableSchemaClosure=[ordered]@{ status=$closure.status; schemasChecked=$closure.schemasChecked; localRefsChecked=$closure.localRefsChecked; failures=$closure.failures; gate=[ordered]@{ path=$closureGatePath; sha256=Sha $closureGatePath } }
        frozenArtifactSchema=[ordered]@{ source=$frozenSource; packed=$frozenPacked; sha256=Sha $frozenPacked; modified=$false }
    }
    boundaries = [ordered]@{
        workerRunInvocations=0; externalCalls=0; remnoteProbeInvocations=0; remnoteWrites=0; ankiWrites=0; mineruCalls=0; speakerInvocations=0
        healthyLiveEvidenceReusedOnly='canary/sealed-module-actions-v1.2.16/20260831T060620Z/execution-receipt.json'
        latestOwnerProbeReusedOnly='canary/remnote-exporter-v2.2-live-readonly-probe/20260831T060451Z/receipt.json'
    }
}
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
[IO.File]::WriteAllText($receiptPath, (($receipt | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ receipt=$receiptPath; sha256=Sha $receiptPath; status=$receipt.status; release=$receipt.release; closure=$receipt.validation.portableSchemaClosure } | ConvertTo-Json -Depth 30

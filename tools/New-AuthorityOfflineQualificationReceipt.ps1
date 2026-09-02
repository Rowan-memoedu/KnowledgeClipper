[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$RuntimeGates,
    [Parameter(Mandatory = $true)][string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$RuntimeGates = [IO.Path]::GetFullPath($RuntimeGates)
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
if (Test-Path -LiteralPath $ReceiptPath) { throw "Append-only receipt exists: $ReceiptPath" }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Tree([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File)
    $rows = [string[]]@($files | ForEach-Object { "$([IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\','/'))`t$($_.Length)`t$((Sha $_.FullName).ToLowerInvariant())" })
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    [ordered]@{ files = $files.Count; bytes = [long](($files | Measure-Object Length -Sum).Sum); treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))) }
}
$checkpointPath = Join-Path $AuthorityDirectory 'checkpoint.json'
$registryPath = Join-Path $AuthorityDirectory 'worker-registry.v1.json'
$dependencyPath = Join-Path $AuthorityDirectory 'dependency-manifest.v1.json'
$assemblyPath = Join-Path $AuthorityDirectory 'assembly-receipt.v1.json'
$pathGatePath = Join-Path $AuthorityDirectory 'assembly-path-gate.v1.json'
foreach ($path in @($checkpointPath,$registryPath,$dependencyPath,$assemblyPath,$pathGatePath,$RuntimeGates)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Qualification input missing: $path" } }
$checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json -Depth 100
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -Depth 100
$dependency = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json -Depth 100
$pathGate = Get-Content -LiteralPath $pathGatePath -Raw | ConvertFrom-Json -Depth 100
$runtime = Get-Content -LiteralPath $RuntimeGates -Raw | ConvertFrom-Json -Depth 100
if ($checkpoint.admissionStatus -ne 'authority-admitted' -or $registry.registryVersion -ne '1.2.12' -or $registry.workers.Count -ne 5) { throw 'Authority checkpoint/registry is not admitted.' }
if ($pathGate.status -ne 'passed' -or $pathGate.pathCount -ne 5 -or -not $pathGate.allExist -or -not $pathGate.allBytesAndHashesMatch) { throw 'Assembly path gate failed.' }
if ($runtime.status -ne 'passed' -or $runtime.registryVersion -ne '1.2.12' -or $runtime.validateRegistry.status -ne 'valid' -or $runtime.validateRegistry.workers -ne 5 -or $runtime.protocol10.taskQuery.status -ne 'passed' -or $runtime.protocol10.taskCommand.status -ne 'passed' -or $runtime.protocol11.stageControl.status -ne 'passed' -or -not $runtime.dependencyDriftNegative.rejected -or $runtime.workerRunInvocations -ne 0 -or $runtime.externalWrites -ne 0 -or $runtime.physicalSpeakerInvocations -ne 0) { throw 'Runtime protocol gates failed.' }
$tree = Tree $ReleaseRoot
if ($tree.files -ne $dependency.files -or $tree.bytes -ne $dependency.bytes -or $tree.treeSha256 -ne $dependency.treeSha256) { throw 'Release tree drifted from dependency manifest.' }
$expected = [ordered]@{ 'image.ocr.mineru'='1.2.2'; 'content.deepseek'='0.7.0'; 'speech.edge'='1.2.4'; 'export.remnote'='2.1.0'; 'export.anki'='1.2.1' }
foreach ($worker in $registry.workers) {
    $exe = Join-Path $ReleaseRoot $worker.entrypoint.path
    $describe = ((& $exe describe --json 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    if ($LASTEXITCODE -ne 0 -or $describe.workerId -ne $worker.workerId -or $describe.workerVersion -ne $expected[$worker.workerId]) { throw "Worker describe drifted: $($worker.workerId)" }
}
$validation = ((& (Join-Path $ReleaseRoot 'orchestrator/KnowledgeClipper.Orchestrator.exe') validate-registry --registry $registryPath --release-root $ReleaseRoot 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
if ($LASTEXITCODE -ne 0 -or $validation.status -ne 'valid' -or $validation.workers -ne 5) { throw 'Fresh final validate-registry failed.' }
$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-offline-qualification-receipt.v1'; generatedAt = [DateTimeOffset]::UtcNow.ToString('o'); status = 'passed-offline-authority-qualified'
    authority = [ordered]@{ registryVersion = '1.2.12'; directory = $AuthorityDirectory; checkpoint = [ordered]@{ path=$checkpointPath; sha256=Sha $checkpointPath }; registry = [ordered]@{ path=$registryPath; sha256=Sha $registryPath }; dependencyManifest = [ordered]@{ path=$dependencyPath; sha256=Sha $dependencyPath }; assemblyReceipt = [ordered]@{ path=$assemblyPath; sha256=Sha $assemblyPath }; assemblyPathGate = [ordered]@{ path=$pathGatePath; sha256=Sha $pathGatePath } }
    release = [ordered]@{ root=$ReleaseRoot; files=$tree.files; bytes=$tree.bytes; treeSha256=$tree.treeSha256 }
    core = [ordered]@{ version='2.5.0'; checkpoint='contracts/core/v2.5/checkpoints/v2.5.1-20260830T075400Z/checkpoint.json'; checkpointSha256='62996EF471F46470090BE40259BF95ACD84EB7640084A1FCD4EC85E680AEE4F3' }
    workers = $expected; validation = $validation; runtimeGates = [ordered]@{ path=$RuntimeGates; sha256=Sha $RuntimeGates; protocol10=$runtime.protocol10; protocol11=$runtime.protocol11; dependencyDriftRejected=$runtime.dependencyDriftNegative.rejected }
    boundaries = [ordered]@{ externalCalls=0; workerRunInvocations=0; mineruCalls=0; remnoteWrites=0; ankiWrites=0; physicalSpeakerInvocations=0; integratedExternalCanary='conditional-separate-authorization-and-credentials' }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ReceiptPath)) | Out-Null
[IO.File]::WriteAllText($ReceiptPath, (($receipt | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ receipt=$ReceiptPath; sha256=Sha $ReceiptPath; status=$receipt.status; release=$tree } | ConvertTo-Json -Depth 20

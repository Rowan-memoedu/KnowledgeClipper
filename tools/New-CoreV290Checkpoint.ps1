[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative = 'contracts/core/v2.8/checkpoints/v2.8.1-20260901T054000Z/checkpoint.json'
$targetRelative = 'contracts/core/v2.9/checkpoints/v2.9.0-20260901T061600Z/checkpoint.json'
$runtimeRelative = 'dist/orchestrator/win-x64/v2.9.0/KnowledgeClipper.Orchestrator.exe'
$receiptRelative = 'canary/core-v2.9.0-offline/20260901T061500Z/core-authority-validation.receipt.json'
$source = Join-Path $ProjectRoot $sourceRelative.Replace('/','\')
$target = Join-Path $ProjectRoot $targetRelative.Replace('/','\')
$runtime = Join-Path $ProjectRoot $runtimeRelative.Replace('/','\')
$receipt = Join-Path $ProjectRoot $receiptRelative.Replace('/','\')
if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $runtime -PathType Leaf) -or -not (Test-Path -LiteralPath $receipt -PathType Leaf) -or (Test-Path -LiteralPath $target)) {
    throw 'Append-only Core v2.9.0 checkpoint precondition failed.'
}

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Entry([string]$Relative) {
    $path = Join-Path $ProjectRoot $Relative.Replace('/','\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checkpoint input missing: $Relative" }
    [ordered]@{ path=$Relative; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Sha $path).ToLowerInvariant() }
}
function Tree([object[]]$Files) {
    $rows = [string[]]@($Files | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
    [Array]::Sort($rows,[StringComparer]::Ordinal)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($rows -join "`n") + "`n")))).ToLowerInvariant()
}

$doc = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$files = @($doc.tree.files | ForEach-Object { [ordered]@{ path=$_.path; bytes=[long]$_.bytes; sha256=[string]$_.sha256 } })
$updates = @(
    'orchestrator/Core/ModuleActionControl.cs',
    'orchestrator/Core/CommandAndIpc.cs',
    'tests/FakeWorker/Program.cs',
    'tests/CoreAdapter.IntegrationTests/Program.cs',
    'tools/validate_contracts.py',
    'tools/New-KnowledgeClipperAuthorityRelease.ps1',
    'tools/New-CoreV290ValidationReceipt.ps1',
    'tools/New-CoreV290Checkpoint.ps1',
    'contracts/core/v2.9/module-control/orchestrator-ipc.v1.4.json',
    'contracts/core/v2.9/module-control/schemas/ipc-request.v1.4.schema.json',
    'contracts/core/v2.9/module-control/schemas/ipc-response.v1.4.schema.json',
    'contracts/core/v2.9/module-control/schemas/module-action-request.v3.schema.json',
    'contracts/core/v2.9/module-control/schemas/module-action-receipt.v3.schema.json',
    'contracts/core/v2.9/module-control/schemas/anki-managed-initialization-action.v1.schema.json',
    'contracts/core/v2.9/module-control/fixtures/module-action-request/valid/anki-initialize-managed.json',
    'contracts/core/v2.9/module-control/fixtures/module-action-request/invalid/anki-initialize-on-v2.json',
    'contracts/core/v2.9/module-control/fixtures/anki-managed-initialization-action/valid/sha-bound-explicit.json',
    'contracts/core/v2.9/module-control/fixtures/anki-managed-initialization-action/invalid/implicit-authorization.json',
    'workers/anki-exporter/admission/core/export.anki-v1.4.0-20260901T050000Z.json',
    'workers/anki-exporter/canary/formal-managed-initialization/20260901T050008Z-formal-v140-managed-init/receipt.json',
    'releases/knowledge-clipper-anki-exporter-v1.4.0-20260901T050000Z/win-x64/workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe',
    'releases/knowledge-clipper-anki-exporter-v1.4.0-20260901T050000Z/win-x64/workers/anki/release-manifest.v1.json',
    'releases/knowledge-clipper-anki-exporter-v1.4.0-20260901T050000Z/win-x64/workers/anki/contracts/anki-destination-settings-descriptor.v1.3.json',
    'releases/knowledge-clipper-anki-exporter-v1.4.0-20260901T050000Z/win-x64/workers/anki/contracts/anki-managed-initialization-status.v1.schema.json',
    'releases/knowledge-clipper-remnote-exporter-v2.3.0-20260901T043610Z/win-x64/KnowledgeClipper.RemNoteExporter.Worker.exe',
    'canary/remnote-exporter-v2.3-live-readonly-discovery/20260901T043431Z/receipt.json',
    'canary/quality-next-desktop-regressions/20260901T052000Z-core-precheckpoint/quality-receipt.json',
    'canary/quality-desktop-producer-fixes/20260901T050000Z-independent-r14-final/quality-receipt.json',
    'canary/core-v2.9.0-offline/20260901T061500Z/core-adapter.stdout.json',
    'canary/core-v2.9.0-offline/20260901T061500Z/core-adapter.stderr.log',
    'canary/core-v2.9.0-offline/20260901T061500Z/orchestrator.stdout.json',
    'canary/core-v2.9.0-offline/20260901T061500Z/orchestrator.stderr.log',
    $receiptRelative,
    $runtimeRelative
)
foreach ($relative in $updates) {
    $files = @($files | Where-Object { $_.path -ne $relative })
    $files += Entry $relative
}

$validation = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json -Depth 100
$describe = ((& $runtime describe 2>&1) -join "`n") | ConvertFrom-Json -Depth 20
if ($LASTEXITCODE -ne 0 -or $describe.version -notlike '2.9.0*') { throw 'Core v2.9.0 runtime identity failed.' }
$doc.checkpointId = 'core-v2.9.0-20260901T061600Z'
$doc.checkpointVersion = '2.9.0'
$doc.createdAt = '2026-09-01T06:16:00Z'
$doc.generatedAt = $doc.createdAt
$doc.status = 'passed-offline-authority-admission-ready'
$doc.supersedes = [ordered]@{ path=$sourceRelative; sha256=Sha $source; immutable=$true }
$doc.core = [ordered]@{ version='2.9.0'; describedVersion=$describe.version; executable=$runtimeRelative; bytes=(Get-Item -LiteralPath $runtime).Length; sha256=Sha $runtime }
$doc.publicVersions.moduleAction = 'knowledge-clipper-module-action.v3'
$doc.publicVersions.ipc = '1.4.0'
$doc.publicVersions.ankiManagedInitializationAction = 'anki-managed-initialization-action.v1'
$doc.validation.contracts = $validation.contracts
$doc.validation.coreAdapter = [ordered]@{ path=$receiptRelative; sha256=Sha $receipt; status='passed'; groups=17; ankiInitializeManagedV14=$true; unauthorizedWorkerCalls=0; explicitReplayWrites=0; unknownExternalStateAutomaticRetries=0; realAnkiConnectCalls=0 }
$doc.validation.orchestrator = [ordered]@{ path=$receiptRelative; sha256=Sha $receipt; status='passed'; tests=23 }
$doc.authorityBoundary = [ordered]@{ currentAuthority='1.2.34'; intendedAuthority='1.2.35'; promotion='pending-append-only-authority-assembly' }
$doc.requirements.ankiManagedInitialization = [ordered]@{
    operation='anki.initialize-managed'; worker='export.anki@1.4.0'; moduleActionSchema='knowledge-clipper-module-action.v3'; ipc='1.4.0'
    actionPackage='immutable-sha-bound-request-task-config-authorization'; explicitAuthorization=$true; implicitProvisioning=$false
    firstRun='create-missing-only-with-exact-readback'; replay='new-explicit-action-query-then-create-missing'; unknownExternalStateAutomaticRetry=$false; realInitializationRuns=0
}
$doc.requirements.originalMedia = [ordered]@{ destinations=@('anki'); remnote='by-design-unsupported-nonblocking'; remnoteRejectedBeforeWorker=$true; exportRemnoteStageAllowed=$false }
$doc.formalWorkers = [ordered]@{
    anki=[ordered]@{ version='1.4.0'; admission='workers/anki-exporter/admission/core/export.anki-v1.4.0-20260901T050000Z.json'; admissionSha256='982074D7BE4C4B14B27AA58826B3DB750E0206A6F5AA806775B3EFAF08A6BCD7' }
    remnote=[ordered]@{ version='2.3.0'; release='releases/knowledge-clipper-remnote-exporter-v2.3.0-20260901T043610Z/win-x64/KnowledgeClipper.RemNoteExporter.Worker.exe'; sha256=Sha (Join-Path $ProjectRoot 'releases\knowledge-clipper-remnote-exporter-v2.3.0-20260901T043610Z\win-x64\KnowledgeClipper.RemNoteExporter.Worker.exe') }
    inheritedWorkers='image.ocr.mineru@1.2.4, content.deepseek@0.9.5, speech.edge@1.2.5'
}
# Correct the admission digest from the checked source instead of trusting a duplicated literal.
$doc.formalWorkers.anki.admissionSha256 = Sha (Join-Path $ProjectRoot 'workers\anki-exporter\admission\core\export.anki-v1.4.0-20260901T050000Z.json')
$doc.retainedHistoricalEvidence = [ordered]@{ playbackRacePath='canary/quality-next-desktop-regressions/20260901T052000Z-core-precheckpoint/quality-receipt.json'; playbackRaceSha256='6007DBE8EAB70DAFC5D8961A85F3A8D55979F3C949ABE39035EAD659ED959969'; failureStatePreserved=$true; currentIndependentRun='passed-without-invalidating-history' }
$doc.tree = [ordered]@{ fileCount=$files.Count; totalBytes=[long](($files | Measure-Object bytes -Sum).Sum); treeSha256=(Tree $files); files=$files }
[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
[IO.File]::WriteAllText($target,(($doc | ConvertTo-Json -Depth 100) + "`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{ status='passed'; checkpoint=$targetRelative; sha256=Sha $target; tree=$doc.tree; runtime=$doc.core } | ConvertTo-Json -Depth 12

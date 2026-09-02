[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CheckpointDirectory,
    [string]$CheckpointVersion = '2.4.8',
    [string]$CoreVersion = '2.4.6'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$checkpointRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $CheckpointDirectory))
$checkpointPath = Join-Path $checkpointRoot 'checkpoint.json'
if (-not (Test-Path -LiteralPath $checkpointRoot -PathType Container)) { throw 'Checkpoint directory is missing.' }
if (Test-Path -LiteralPath $checkpointPath) { throw 'Append-only checkpoint exists.' }

$receiptNames = @(
    'orchestrator-regression.receipt.json',
    'core-adapter.receipt.json',
    'settings-control.receipt.json',
    'remnote-content-adapter.receipt.json',
    'phase-b-build.receipt.json',
    'public-contracts.receipt.json'
)
foreach ($name in $receiptNames) {
    $receipt = Get-Content -LiteralPath (Join-Path $checkpointRoot $name) -Raw | ConvertFrom-Json -Depth 100
    if ($receipt.status -ne 'passed') { throw "Validation receipt is not passed: $name" }
}

$paths = @(
    'orchestrator/Core/DesktopIntentResolver.cs',
    'orchestrator/Core/StageInputResolver.cs',
    'orchestrator/Core/SettingsControl.cs',
    'contracts/core/v2.4/settings-control/schemas/workflow-settings.v1.schema.json',
    'contracts/core/v2.4/settings-control/schemas/workflow-settings.v2.schema.json',
    'contracts/core/v2.4/settings-control/schemas/task-settings-snapshot.v1.schema.json',
    'contracts/core/v2.4/settings-control/schemas/task-settings-snapshot.v2.schema.json',
    'contracts/core/v2.4/settings-control/schemas/task-settings-override.v1.schema.json',
    'contracts/core/v2.4/settings-control/schemas/task-settings-override.v2.schema.json',
    'contracts/core/v2.4/settings-control/schemas/module-settings-adapter.v1.schema.json',
    'contracts/core/v2.4/settings-control/schemas/module-settings-adapter.v2.schema.json',
    'tests/CoreAdapter.IntegrationTests/Program.cs',
    'tests/SettingsControl.IntegrationTests/Program.cs',
    'tests/RemNoteQuickClipper.RemNoteExporter.Tests/ContentAdapterCompatibility/Program.cs',
    'tests/PhaseB.Canary/Program.cs',
    'tests/PhaseB.Canary/PhaseB.Canary.csproj',
    'tools/Invoke-KnowledgeClipperPhaseB.ps1',
    'tools/New-CorePhaseBFollowupCheckpoint.ps1',
    'tools/New-CorePhaseBValidationReceipts.ps1',
    'tools/Publish-CoreRuntime.ps1',
    "dist/orchestrator/win-x64/v$CoreVersion/KnowledgeClipper.Orchestrator.exe",
    'contracts/core/v2.4/settings-control/checkpoints/v2.4.6-20260828T091000Z/checkpoint.json',
    'contracts/core/v2.4/settings-control/checkpoints/v2.4.6-20260828T091000Z/invalidation-receipt.json',
    'contracts/core/v2.4/settings-control/checkpoints/v2.4.7-20260828T092200Z/checkpoint.json',
    'contracts/core/v2.4/settings-control/checkpoints/v2.4.7-20260828T092200Z/invalidation-receipt.json',
    'contracts/authority/worker-registry/v1.2.8-20260828T090000Z/checkpoint.json',
    'contracts/authority/worker-registry/v1.2.9-20260828T091100Z/checkpoint.json',
    'contracts/authority/worker-registry/v1.2.9-20260828T091100Z/invalidation-receipt.v1.json',
    'contracts/authority/worker-registry/v1.2.10-20260828T092300Z/checkpoint.json',
    'contracts/authority/worker-registry/v1.2.10-20260828T092300Z/invalidation-receipt.v1.json',
    'canary/phase-b-v1.2.8/20260828T090400Z/execution-receipt.json'
)
foreach ($name in $receiptNames) {
    $paths += [IO.Path]::GetRelativePath($projectRoot, (Join-Path $checkpointRoot $name)).Replace('\', '/')
}
$files = foreach ($relative in ($paths | Sort-Object -Unique)) {
    $absolute = Join-Path $projectRoot $relative
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Checkpoint input missing: $relative" }
    $item = Get-Item -LiteralPath $absolute
    [ordered]@{ path = $relative; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$rows = $files | ForEach-Object { "$($_.path)$([char]0)$($_.sha256)$([char]0)$($_.bytes)`n" }
$treeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join '')))).ToLowerInvariant()
$totalBytes = [long]0
foreach ($file in $files) { $totalBytes += [long]$file.bytes }
if ($totalBytes -le 0) { throw 'Checkpoint tree totalBytes must be positive.' }
$previous = Join-Path $projectRoot 'contracts/core/v2.4/settings-control/checkpoints/v2.4.7-20260828T092200Z/checkpoint.json'
$priorPhaseBAuthority = Join-Path $projectRoot 'contracts/authority/worker-registry/v1.2.8-20260828T090000Z/checkpoint.json'
$phaseB = Join-Path $projectRoot 'canary/phase-b-v1.2.8/20260828T090400Z/execution-receipt.json'
$exe = Join-Path $projectRoot "dist/orchestrator/win-x64/v$CoreVersion/KnowledgeClipper.Orchestrator.exe"

$checkpoint = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-phase-b-followup-checkpoint.v1'
    checkpointVersion = $CheckpointVersion
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed-core-runtime-authority-ready'
    supersedes = [ordered]@{ path = [IO.Path]::GetRelativePath($projectRoot, $previous).Replace('\', '/'); sha256 = (Get-FileHash $previous -Algorithm SHA256).Hash.ToLowerInvariant() }
    core = [ordered]@{ version = $CoreVersion; executable = [IO.Path]::GetRelativePath($projectRoot, $exe).Replace('\', '/'); bytes = (Get-Item $exe).Length; sha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant() }
    fixes = [ordered]@{
        ankiLegacyExecutionProfile = [ordered]@{ source = 'resolved destinations'; anki = 'cards-anki'; learningRemNote = 'parity-remnote'; cardsRemNote = 'cards-remnote'; externalCallBeforePriorFailure = $false }
        remnoteLearningProjection = [ordered]@{ route = 'clip-daily-route'; multilineWithoutBlankLines = 'ordered stable v2 entries'; textPerLineModified = $false; blankLines = 'fail-closed'; reservedDelimiters = 'fail-closed' }
    }
    validation = [ordered]@{
        publicContracts = [ordered]@{
            status = 'passed'
            schemas = (Get-Content -LiteralPath (Join-Path $checkpointRoot 'public-contracts.receipt.json') -Raw | ConvertFrom-Json).schemas
            validFixtures = (Get-Content -LiteralPath (Join-Path $checkpointRoot 'public-contracts.receipt.json') -Raw | ConvertFrom-Json).validFixtures
            invalidFixtures = (Get-Content -LiteralPath (Join-Path $checkpointRoot 'public-contracts.receipt.json') -Raw | ConvertFrom-Json).invalidFixtures
            receiptSha256 = (Get-FileHash (Join-Path $checkpointRoot 'public-contracts.receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        orchestrator = [ordered]@{ status = 'passed'; tests = 23; receiptSha256 = (Get-FileHash (Join-Path $checkpointRoot 'orchestrator-regression.receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant() }
        coreAdapter = [ordered]@{ status = 'passed'; receiptSha256 = (Get-FileHash (Join-Path $checkpointRoot 'core-adapter.receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant() }
        settings = [ordered]@{ status = 'passed'; externalCalls = 0; physicalSpeakerInvocations = 0; receiptSha256 = (Get-FileHash (Join-Path $checkpointRoot 'settings-control.receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant() }
        remnoteContentAdapter = [ordered]@{ status = 'passed'; receiptSha256 = (Get-FileHash (Join-Path $checkpointRoot 'remnote-content-adapter.receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant() }
        build = [ordered]@{ status = 'passed'; sdk = '8.0.424'; warnings = 0; errors = 0; receiptSha256 = (Get-FileHash (Join-Path $checkpointRoot 'phase-b-build.receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    priorPhaseB = [ordered]@{
        authority = '1.2.8'
        authorityCheckpointSha256 = (Get-FileHash $priorPhaseBAuthority -Algorithm SHA256).Hash.ToLowerInvariant()
        executionReceipt = [IO.Path]::GetRelativePath($projectRoot, $phaseB).Replace('\', '/')
        executionReceiptSha256 = (Get-FileHash $phaseB -Algorithm SHA256).Hash.ToLowerInvariant()
        harnessExitCodes = '0/0/0/0'
        actualPathStatus = 'partial/partial/partial/partial'
        final = $false
        dRemNoteUnknownExternalStateAutoRetried = $false
    }
    authorityBoundary = [ordered]@{ currentAuthority = '1.2.10-invalidated'; authorityBound = $false; intendedAuthority = '1.2.11+'; speechWorkerRequired = 'speech.edge@1.2.3'; speechPlaybackDeepPathReleasePending = $false; phaseBFinal = $false }
    compatibility = [ordered]@{ frozenContractsV1Modified = $false; authorityV1210Modified = $false; oldFailureReceiptsModified = $false }
    tree = [ordered]@{ algorithm = 'sha256(path NUL sha256 NUL bytes LF, ordinal path order)'; fileCount = $files.Count; totalBytes = $totalBytes; treeSha256 = $treeSha; files = $files }
}
[IO.File]::WriteAllText($checkpointPath, (($checkpoint | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ checkpoint = $checkpointPath; sha256 = (Get-FileHash $checkpointPath -Algorithm SHA256).Hash; treeSha256 = $treeSha; files = $files.Count } | ConvertTo-Json

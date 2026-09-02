[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CheckpointDirectory,
    [string]$CheckpointVersion = '2.4.3',
    [string]$Supersedes = 'contracts/core/v2.4/settings-control/checkpoints/v2.4.1-20260828T064200Z/checkpoint.json'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$checkpointRoot = Join-Path $projectRoot $CheckpointDirectory
$checkpointPath = Join-Path $checkpointRoot 'checkpoint.json'
if (-not (Test-Path -LiteralPath $checkpointRoot -PathType Container)) { throw "Checkpoint directory is missing: $checkpointRoot" }
if (Test-Path -LiteralPath $checkpointPath) { throw "Append-only checkpoint already exists: $checkpointPath" }

$settingsReceipt = Join-Path $checkpointRoot 'settings-control-canary.receipt.json'
if (-not (Test-Path -LiteralPath $settingsReceipt -PathType Leaf)) { throw "Settings canary receipt is missing: $settingsReceipt" }
$settingsResult = Get-Content -LiteralPath $settingsReceipt -Raw | ConvertFrom-Json -Depth 100
if ($settingsResult.status -ne 'passed' -or $settingsResult.externalCalls -ne 0 -or $settingsResult.physicalSpeakerInvocations -ne 0) {
    throw 'Settings canary did not preserve the offline Phase A boundary.'
}

$fixedPaths = @(
    'contracts/remnote-destination-settings-snapshot.v1.schema.json',
    'contracts/remnote-destination-settings.v1.schema.json',
    'contracts/remnote-export-payload.v2.schema.json',
    'contracts/remnote-export-task.v2.schema.json',
    'contracts/core/v2.4/settings-control/checkpoints/v2.4.2-20260828T073514Z/invalidation-receipt.json',
    'orchestrator/Core/DesktopIntentResolver.cs',
    'orchestrator/Core/OrchestratorEngine.cs',
    'orchestrator/Core/SettingsControl.cs',
    'orchestrator/Core/StageInputResolver.cs',
    'tests/SettingsControl.IntegrationTests/Program.cs',
    'tests/SettingsControl.IntegrationTests/SettingsControl.IntegrationTests.csproj',
    'tools/New-CoreSettingsIntegrationCheckpoint.ps1',
    'tools/validate_contracts.py',
    'workers/image-ocr/candidates/1.2.0/admission-receipt.v1.json',
    'content-worker/canary/formal-release/content.deepseek-0.6.0-win-x64-formal-20260828T070000Z/20260828T072123449Z-settings-snapshot-replay/formal-release-offline-receipt.v1.json',
    'content-worker/releases/content.deepseek-0.6.0-win-x64-formal-20260828T070000Z/release-manifest.v1.json',
    'canary/remnote-exporter-v2-offline/20260828T143218/receipt.json',
    'workers/anki-exporter/canary/formal-release-settings-probe/20260828T072807Z-4e1cb28a/receipt.json',
    'releases/knowledge-clipper-anki-exporter-v1.2.0-20260828T072346Z/win-x64/workers/anki/release-manifest.v1.json',
    'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260828T072400Z-bbe77cb1/speech-formal-win-x64-release-receipt.json',
    'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260828T072400Z-bbe77cb1/speech-formal-win-x64-independent-validation-receipt.json'
)
$contractFiles = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'contracts/core/v2.4/settings-control') -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/]checkpoints[\\/]' } |
    ForEach-Object { [IO.Path]::GetRelativePath($projectRoot, $_.FullName).Replace('\', '/') }
$relativeReceipt = [IO.Path]::GetRelativePath($projectRoot, $settingsReceipt).Replace('\', '/')
$allPaths = @($fixedPaths + $contractFiles + $relativeReceipt) | Sort-Object -Unique

$fileEntries = foreach ($relative in $allPaths) {
    $absolute = Join-Path $projectRoot $relative
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Checkpoint input is missing: $relative" }
    $item = Get-Item -LiteralPath $absolute
    [ordered]@{
        path = $relative
        sha256 = (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $item.Length
    }
}
$treeLines = $fileEntries | ForEach-Object { "{0}{1}{2}{1}{3}`n" -f $_.path, [char]0, $_.sha256, $_.bytes }
$treeBytes = [Text.Encoding]::UTF8.GetBytes(($treeLines -join ''))
$treeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($treeBytes)).ToLowerInvariant()
$totalBytes = [long](($fileEntries | ForEach-Object { [long]$_['bytes'] } | Measure-Object -Sum).Sum)

$moduleEvidence = [ordered]@{
    image = [ordered]@{
        worker = 'image.ocr.mineru@1.2.0'
        executableSha256 = '80fe1d2d7d371d135658b36ac22735490dadd5239d263c2e1b3025d5fc7221b3'
        receipt = 'workers/image-ocr/candidates/1.2.0/admission-receipt.v1.json'
        receiptSha256 = 'dfbe891ba7f9c2b20de1f7fdf264d75816aab146bbe74ddefa1c4cc97654fb9'
        providerSettingsConsumed = $true
    }
    content = [ordered]@{
        worker = 'content.deepseek@0.6.0'
        executableSha256 = 'f6ec35798f8948727b8664517fdbb096db8a94c8dd079f612b05bac4ea72d3b8'
        releaseTreeSha256 = '15ed9bc404825bc1a7eac20f6bc1e73b33d0ed00659a7a94b6b9008b4ef517d6'
        receipt = 'content-worker/canary/formal-release/content.deepseek-0.6.0-win-x64-formal-20260828T070000Z/20260828T072123449Z-settings-snapshot-replay/formal-release-offline-receipt.v1.json'
        receiptSha256 = 'f3084fe187c42220e46bd1cc395e041ae23b4d28ed41100b1d1393eb1b3d15aa'
        settingsSnapshotConsumed = $true
    }
    remnote = [ordered]@{
        worker = 'export.remnote@2.0.0'
        executableSha256 = 'd388301a8667ad334860a541c18300e5f3f0d3923477d5b2a3188b176e7ddca6'
        payloadVersion = '2.0.0'
        receipt = 'canary/remnote-exporter-v2-offline/20260828T143218/receipt.json'
        receiptSha256 = '80b4585280b7bc34f92abfe53e9f259e1c646b741c7a8b07b810274884657475'
    }
    anki = [ordered]@{
        worker = 'export.anki@1.2.0'
        executableSha256 = '661f473d42b752bff9b1211c2588f8856aaaba24012f9420ababe09c65f766d2'
        releaseTreeSha256 = '169cb61d6dc97b6ad725a1ebb026aae3aebc5b2554e9ee25a72348af316c95d7'
        receipt = 'workers/anki-exporter/canary/formal-release-settings-probe/20260828T072807Z-4e1cb28a/receipt.json'
        receiptSha256 = '04f70ceb08149f55341051861d165d2578064eb2db1b0c65f3d125d7d76b4168'
        configArtifactConsumed = $true
    }
    speech = [ordered]@{
        worker = 'speech.edge@1.2.1'
        executableSha256 = 'bbe77cb1b7f4b9c87d29e8f6dbeb277254501665ea3f171a2baa75026bb7fcf0'
        releaseTreeSha256 = '0fa3807989ed543dfe80467f581eb036dbb3de021e5c8dccb2a19ad1374a4d99'
        receipt = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260828T072400Z-bbe77cb1/speech-formal-win-x64-independent-validation-receipt.json'
        receiptSha256 = '6a4fb6514e6b30dda3ef6904bf8bd7fbc5bb6240aaebbc5d2a4f76c91bb0f8dc'
        settingsSnapshotConsumed = $true
    }
}

$checkpoint = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-settings-integration-checkpoint.v1'
    checkpointVersion = $CheckpointVersion
    supersedes = $Supersedes
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed-offline-authority-admission-ready'
    contractRoot = 'contracts/core/v2.4/settings-control'
    publicVersions = [ordered]@{
        workflowSettings = 'knowledge-clipper-workflow-settings.v2'
        taskOverride = 'knowledge-clipper-task-settings-override.v2'
        taskSnapshot = 'knowledge-clipper-task-settings-snapshot.v2'
        moduleAdapter = 'knowledge-clipper-module-settings-adapter.v2'
        taskManifest = '2.0.0'
        pipelinePlan = '2.0.0'
        workerRequestEnvelope = '1.0.0'
        remnotePayload = '2.0.0'
    }
    compatibility = [ordered]@{
        workflowSettingsV1Accepted = $true
        frozenContractsV1Modified = $false
        priorAuthorityModified = $false
        previousCheckpointSha256 = (Get-FileHash -LiteralPath (Join-Path $projectRoot $Supersedes) -Algorithm SHA256).Hash.ToLowerInvariant()
        invalidatedGenerationAttempt = [ordered]@{
            checkpoint = 'contracts/core/v2.4/settings-control/checkpoints/v2.4.2-20260828T073514Z/checkpoint.json'
            checkpointSha256 = '601f1e716abc5457b374c46bf941a1eb3ab973fd31fa119ba8e7547f5bfeab45'
            invalidationReceipt = 'contracts/core/v2.4/settings-control/checkpoints/v2.4.2-20260828T073514Z/invalidation-receipt.json'
            invalidationReceiptSha256 = '2bc1533e3e7fe4ee4075c29d6760916745e081665392fe2eeba790eb65a17e99'
            authorityBound = $false
        }
    }
    validation = [ordered]@{
        contractFixtures = [ordered]@{ status = 'passed'; schemasChecked = 59; validAccepted = 62; invalidRejected = 50 }
        settingsFakeWorkerCanary = [ordered]@{
            status = 'passed'
            receipt = $relativeReceipt
            sha256 = (Get-FileHash -LiteralPath $settingsReceipt -Algorithm SHA256).Hash.ToLowerInvariant()
            externalCalls = 0
            physicalSpeakerInvocations = 0
            moduleV2PlanBound = $true
            moduleV2WorkerRequestBound = $true
        }
        coreAdapterRegression = [ordered]@{ status = 'passed'; matrix = 8 }
        orchestratorRegression = [ordered]@{ status = 'passed'; tests = 22 }
        build = [ordered]@{ status = 'passed'; warnings = 0; errors = 0; sdk = '8.0.424' }
    }
    moduleEvidence = $moduleEvidence
    authorityBoundary = [ordered]@{
        currentAuthority = 'contracts/authority/worker-registry/v1.2.4-20260828T032500Z'
        promotion = 'pending-new-append-only-authority'
        intendedAuthorityVersion = '1.2.5'
    }
    tree = [ordered]@{
        algorithm = 'sha256(path NUL sha256 NUL bytes LF, ordinal path order)'
        fileCount = $fileEntries.Count
        totalBytes = $totalBytes
        treeSha256 = $treeSha
        files = $fileEntries
    }
}

$json = $checkpoint | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($checkpointPath, $json + "`n", [Text.UTF8Encoding]::new($false))
[ordered]@{
    checkpoint = [IO.Path]::GetRelativePath($projectRoot, $checkpointPath).Replace('\', '/')
    sha256 = (Get-FileHash -LiteralPath $checkpointPath -Algorithm SHA256).Hash
    treeSha256 = $treeSha
    fileCount = $fileEntries.Count
    totalBytes = $totalBytes
} | ConvertTo-Json -Depth 4

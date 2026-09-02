[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$RegistryVersion,
    [Parameter(Mandatory = $true)][ValidateSet('candidate-admitted', 'authority-admitted')][string]$AdmissionStatus,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$CheckpointStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'),
    [string]$RuntimeGatesPath,
    [string]$PortableSchemaClosureGatePath,
    [string]$CoreWorkflowCheckpointPath,
    [string]$CoreSettingsCheckpointPath,
    [string]$AssemblyPathGatePath,
    [string]$SupersedesCheckpointPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$frozenReceiptPath = Join-Path $ProjectRoot 'receipts\contracts-v1-stable-checkpoint-20260825T115938Z.json'
$registryPath = Join-Path $AuthorityDirectory 'worker-registry.v1.json'
$dependencyManifestPath = Join-Path $AuthorityDirectory 'dependency-manifest.v1.json'
$orchestratorPath = Join-Path $ReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$RuntimeGatesPath = if ([string]::IsNullOrWhiteSpace($RuntimeGatesPath)) { $null } else { [IO.Path]::GetFullPath($RuntimeGatesPath) }
$PortableSchemaClosureGatePath = if ([string]::IsNullOrWhiteSpace($PortableSchemaClosureGatePath)) { $null } else { [IO.Path]::GetFullPath($PortableSchemaClosureGatePath) }
$CoreWorkflowCheckpointPath = if ([string]::IsNullOrWhiteSpace($CoreWorkflowCheckpointPath)) { $null } else { [IO.Path]::GetFullPath($CoreWorkflowCheckpointPath) }
$CoreSettingsCheckpointPath = if ([string]::IsNullOrWhiteSpace($CoreSettingsCheckpointPath)) { $null } else { [IO.Path]::GetFullPath($CoreSettingsCheckpointPath) }
$AssemblyPathGatePath = if ([string]::IsNullOrWhiteSpace($AssemblyPathGatePath)) { $null } else { [IO.Path]::GetFullPath($AssemblyPathGatePath) }
$SupersedesCheckpointPath = if ([string]::IsNullOrWhiteSpace($SupersedesCheckpointPath)) { $null } else { [IO.Path]::GetFullPath($SupersedesCheckpointPath) }

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-JsonOnce([string]$Path, [object]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Append-only target already exists: $Path" }
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $json = $Value | ConvertTo-Json -Depth 32
        [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
    }
}

function Write-TextOnce([string]$Path, [string]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Append-only target already exists: $Path" }
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, $Value, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
    }
}

foreach ($required in @($frozenReceiptPath, $registryPath, $dependencyManifestPath, $orchestratorPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required authority input is missing: $required" }
}
foreach ($optionalRequired in @($RuntimeGatesPath, $PortableSchemaClosureGatePath, $CoreWorkflowCheckpointPath, $CoreSettingsCheckpointPath, $AssemblyPathGatePath, $SupersedesCheckpointPath)) {
    if (-not [string]::IsNullOrWhiteSpace($optionalRequired) -and -not (Test-Path -LiteralPath $optionalRequired -PathType Leaf)) {
        throw "Required optional authority evidence is missing: $optionalRequired"
    }
}

$frozenReceipt = Get-Content -LiteralPath $frozenReceiptPath -Raw | ConvertFrom-Json
$frozenMismatches = @()
foreach ($entry in $frozenReceipt.files) {
    if (-not (Test-Path -LiteralPath $entry.path -PathType Leaf)) {
        $frozenMismatches += [ordered]@{ path = $entry.path; expected = $entry.sha256; actual = 'missing' }
        continue
    }
    $actual = Get-Sha256 $entry.path
    if ($actual -ne $entry.sha256) { $frozenMismatches += [ordered]@{ path = $entry.path; expected = $entry.sha256; actual = $actual } }
}
if ($frozenReceipt.files.Count -ne 21 -or $frozenMismatches.Count -ne 0) {
    throw "Frozen v1 verification failed: files=$($frozenReceipt.files.Count), mismatches=$($frozenMismatches.Count)"
}

$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
if ($registry.registryVersion -ne $RegistryVersion) { throw "Registry version mismatch: expected=$RegistryVersion actual=$($registry.registryVersion)" }
if ($registry.workers.Count -ne 5) { throw "Authority admission requires exactly five workers; actual=$($registry.workers.Count)" }

$validationOutput = & $orchestratorPath validate-registry --registry $registryPath --release-root $ReleaseRoot 2>&1
if ($LASTEXITCODE -ne 0) { throw "validate-registry failed: $($validationOutput -join [Environment]::NewLine)" }
$validation = ($validationOutput -join [Environment]::NewLine) | ConvertFrom-Json
if ($validation.status -ne 'valid' -or $validation.registryVersion -ne $RegistryVersion -or $validation.workers -ne 5) {
    throw 'validate-registry returned an unexpected result.'
}

$workerEvidence = @()
$describeSnapshots = @()
foreach ($worker in $registry.workers) {
    $entrypoint = Join-Path $ReleaseRoot ($worker.entrypoint.path.Replace('/', '\'))
    $describeOutput = & $entrypoint @($worker.entrypoint.args) describe --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Worker describe failed: $($worker.workerId)" }
    $describe = ($describeOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($describe.workerId -ne $worker.workerId) { throw "Worker describe identity mismatch: $($worker.workerId)" }
    $describePath = Join-Path $AuthorityDirectory ("describe\$($worker.workerId).describe.json")
    $describeText = ($describeOutput -join "`n").TrimEnd("`r", "`n") + "`n"
    if (Test-Path -LiteralPath $describePath -PathType Leaf) {
        $existingText = Get-Content -LiteralPath $describePath -Raw
        $existing = $existingText | ConvertFrom-Json -Depth 100
        $liveCanonical = $describe | ConvertTo-Json -Depth 100 -Compress
        $existingCanonical = $existing | ConvertTo-Json -Depth 100 -Compress
        if ($existingCanonical -ne $liveCanonical) { throw "Existing describe snapshot differs from live describe: $($worker.workerId)" }
        $describeText = $existingText
    }
    $describeBytes = [Text.Encoding]::UTF8.GetBytes($describeText)
    $describeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($describeBytes))
    $describeSnapshots += [ordered]@{ path = $describePath; text = $describeText }
    $workerEvidence += [ordered]@{
        workerId = $worker.workerId
        workerVersion = $describe.workerVersion
        entrypoint = $worker.entrypoint.path
        bytes = (Get-Item -LiteralPath $entrypoint).Length
        sha256 = Get-Sha256 $entrypoint
        stages = @($describe.stages)
        inputKinds = @($describe.inputKinds)
        outputKinds = @($describe.outputKinds)
        operations = if ($worker.workerId -eq 'content.deepseek') { @('understand-and-card', 'question.answer', 'question.make-cards') } elseif ($worker.workerId -eq 'speech.edge') { @('speech.synthesize', 'speech.playback') } elseif ($worker.workerId -eq 'export.remnote') { @('remnote.export') } elseif ($worker.workerId -eq 'export.anki') { @('export') } else { @('image-ocr') }
        describeSnapshot = [ordered]@{ path = $describePath; bytes = $describeBytes.Length; sha256 = $describeSha256 }
    }
}

$releaseFiles = @(Get-ChildItem -LiteralPath $ReleaseRoot -File -Recurse)
$treeRows = foreach ($file in $releaseFiles) {
    $relative = [IO.Path]::GetRelativePath($ReleaseRoot, $file.FullName).Replace('\', '/')
    $hash = (Get-Sha256 $file.FullName).ToLowerInvariant()
    "$relative`t$($file.Length)`t$hash"
}
$treeRows = [string[]]$treeRows
[Array]::Sort($treeRows, [StringComparer]::Ordinal)
$treeBytes = [Text.Encoding]::UTF8.GetBytes(($treeRows -join "`n"))
$treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($treeBytes))
$releaseBytes = ($releaseFiles | Measure-Object Length -Sum).Sum
$dependencyManifest = Get-Content -LiteralPath $dependencyManifestPath -Raw | ConvertFrom-Json
if ($dependencyManifest.files -ne $releaseFiles.Count -or $dependencyManifest.bytes -ne $releaseBytes -or $dependencyManifest.treeSha256 -ne $treeSha256) {
    throw "Dependency manifest drift: actual files=$($releaseFiles.Count), bytes=$releaseBytes, tree=$treeSha256"
}

$runtimeGates = $null
if (-not [string]::IsNullOrWhiteSpace($RuntimeGatesPath)) {
    $runtimeGateDocument = Get-Content -LiteralPath $RuntimeGatesPath -Raw | ConvertFrom-Json
    if ($runtimeGateDocument.status -ne 'passed' -or $runtimeGateDocument.registryVersion -ne $RegistryVersion -or
        [IO.Path]::GetFullPath($runtimeGateDocument.releaseRoot) -ne $ReleaseRoot -or
        $runtimeGateDocument.validateRegistry.status -ne 'valid' -or $runtimeGateDocument.validateRegistry.workers -ne 5 -or
        $runtimeGateDocument.protocol10.taskQuery.status -ne 'passed' -or $runtimeGateDocument.protocol10.taskCommand.status -ne 'passed' -or
        $runtimeGateDocument.protocol11.stageControl.status -ne 'passed' -or $runtimeGateDocument.protocol11.stageControl.receiptSchemaVersion -ne '2.3.0' -or
        -not $runtimeGateDocument.dependencyDriftNegative.rejected -or $runtimeGateDocument.workerRunInvocations -ne 0 -or
        $runtimeGateDocument.physicalSpeakerInvocations -ne 0 -or $runtimeGateDocument.externalWrites -ne 0) {
        throw 'Runtime gates do not prove the required authority boundary.'
    }
    $runtimeGates = [ordered]@{
        path = $RuntimeGatesPath
        sha256 = Get-Sha256 $RuntimeGatesPath
        status = $runtimeGateDocument.status
        protocol10 = [ordered]@{ taskQuery = $runtimeGateDocument.protocol10.taskQuery.status; taskCommand = $runtimeGateDocument.protocol10.taskCommand.status }
        protocol11 = [ordered]@{ stageControl = $runtimeGateDocument.protocol11.stageControl.status; receiptSchemaVersion = $runtimeGateDocument.protocol11.stageControl.receiptSchemaVersion }
        dependencyDriftRejected = [bool]$runtimeGateDocument.dependencyDriftNegative.rejected
        workerRunInvocations = $runtimeGateDocument.workerRunInvocations
        physicalSpeakerInvocations = $runtimeGateDocument.physicalSpeakerInvocations
        externalWrites = $runtimeGateDocument.externalWrites
    }
}

$portableSchemaClosure = $null
if (-not [string]::IsNullOrWhiteSpace($PortableSchemaClosureGatePath)) {
    $closureGateDocument = Get-Content -LiteralPath $PortableSchemaClosureGatePath -Raw | ConvertFrom-Json -Depth 100
    $expectedPackedArtifact = Join-Path $ReleaseRoot 'contracts\v1\schemas\common\artifact.schema.json'
    $expectedFrozenArtifact = Join-Path $ProjectRoot 'contracts\v1\schemas\common\artifact.schema.json'
    if ($closureGateDocument.schemaVersion -ne 'knowledge-clipper-portable-schema-closure-gate.v1' -or
        $closureGateDocument.status -ne 'passed' -or
        [IO.Path]::GetFullPath($closureGateDocument.releaseRoot) -ne $ReleaseRoot -or
        $closureGateDocument.schemasChecked -lt 1 -or $closureGateDocument.localRefsChecked -lt 1 -or
        @($closureGateDocument.failures).Count -ne 0 -or
        $closureGateDocument.registrySha256 -ne (Get-Sha256 $registryPath) -or
        $closureGateDocument.dependencyManifestSha256 -ne (Get-Sha256 $dependencyManifestPath) -or
        -not (Test-Path -LiteralPath $expectedPackedArtifact -PathType Leaf) -or
        (Get-Sha256 $expectedPackedArtifact) -ne (Get-Sha256 $expectedFrozenArtifact) -or
        $closureGateDocument.frozenArtifactSchemaSha256 -ne (Get-Sha256 $expectedFrozenArtifact)) {
        throw 'Portable schema closure gate does not prove the packed frozen dependency closure.'
    }
    $portableSchemaClosure = [ordered]@{
        path = $PortableSchemaClosureGatePath
        sha256 = Get-Sha256 $PortableSchemaClosureGatePath
        status = 'passed'
        schemasChecked = $closureGateDocument.schemasChecked
        localRefsChecked = $closureGateDocument.localRefsChecked
        frozenArtifactSchemaSha256 = $closureGateDocument.frozenArtifactSchemaSha256
    }
}

$coreWorkflow = $null
if (-not [string]::IsNullOrWhiteSpace($CoreWorkflowCheckpointPath)) {
    $coreWorkflowDocument = Get-Content -LiteralPath $CoreWorkflowCheckpointPath -Raw | ConvertFrom-Json
    if ($coreWorkflowDocument.status -ne 'stable' -or $coreWorkflowDocument.scope.ipcProtocolVersion -ne '1.1' -or
        $coreWorkflowDocument.scope.receiptSchemaVersion -ne '2.3.0') {
        throw 'Core workflow checkpoint does not identify the required playback-control authority.'
    }
    foreach ($contract in $coreWorkflowDocument.publicContracts) {
        $releaseContract = Join-Path $ReleaseRoot ($contract.path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $releaseContract -PathType Leaf) -or (Get-Sha256 $releaseContract) -ne $contract.sha256) {
            throw "Release-root Core contract drifted: $($contract.path)"
        }
    }
    $coreWorkflow = [ordered]@{
        checkpointId = $coreWorkflowDocument.checkpointId
        path = $CoreWorkflowCheckpointPath
        sha256 = Get-Sha256 $CoreWorkflowCheckpointPath
        ipcProtocolVersion = $coreWorkflowDocument.scope.ipcProtocolVersion
        receiptSchemaVersion = $coreWorkflowDocument.scope.receiptSchemaVersion
        publicContracts = @($coreWorkflowDocument.publicContracts).Count
    }
}

$coreSettings = $null
if (-not [string]::IsNullOrWhiteSpace($CoreSettingsCheckpointPath)) {
    $coreSettingsDocument = Get-Content -LiteralPath $CoreSettingsCheckpointPath -Raw | ConvertFrom-Json -Depth 100
    $coreCheckpointDirectory = Split-Path -Parent $CoreSettingsCheckpointPath
    if ((Test-Path -LiteralPath (Join-Path $coreCheckpointDirectory 'invalidation-receipt.json') -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $coreCheckpointDirectory 'invalidation-receipt.v1.json') -PathType Leaf)) {
        throw 'Core settings/admission checkpoint is explicitly invalidated.'
    }
    $isSettingsIntegration =
        $coreSettingsDocument.schemaVersion -eq 'knowledge-clipper-core-settings-integration-checkpoint.v1' -and
        $coreSettingsDocument.checkpointVersion -eq '2.4.3' -and
        $coreSettingsDocument.status -eq 'passed-offline-authority-admission-ready' -and
        $coreSettingsDocument.publicVersions.workflowSettings -eq 'knowledge-clipper-workflow-settings.v2' -and
        $coreSettingsDocument.publicVersions.taskSnapshot -eq 'knowledge-clipper-task-settings-snapshot.v2' -and
        $coreSettingsDocument.validation.settingsFakeWorkerCanary.status -eq 'passed' -and
        $coreSettingsDocument.validation.settingsFakeWorkerCanary.externalCalls -eq 0 -and
        $coreSettingsDocument.validation.settingsFakeWorkerCanary.physicalSpeakerInvocations -eq 0 -and
        -not [string]::IsNullOrWhiteSpace($coreSettingsDocument.tree.treeSha256) -and
        $null -ne $coreSettingsDocument.tree.totalBytes
    $isFinalAdmission =
        $coreSettingsDocument.schemaVersion -eq 'knowledge-clipper-core-authority-admission-checkpoint.v1' -and
        $coreSettingsDocument.checkpointVersion -eq '2.4.5' -and
        $coreSettingsDocument.status -eq 'passed-authority-admission-ready' -and
        $coreSettingsDocument.core.version -eq '2.4.4' -and
        $coreSettingsDocument.content.worker -eq 'content.deepseek@0.6.1' -and
        $coreSettingsDocument.content.liveGatePassed -eq $true -and
        $coreSettingsDocument.speech.worker -eq 'speech.edge@1.2.2' -and
        $coreSettingsDocument.speech.deepPath.status -eq 'passed' -and
        $coreSettingsDocument.speech.deepPath.absolutePayloadPathLength -gt 260 -and
        $coreSettingsDocument.speech.deepPath.physicalSpeakerPlayback -eq $false -and
        $coreSettingsDocument.authorityBoundary.speechDeepPathReleasePending -eq $false -and
        $coreSettingsDocument.compatibility.frozenContractsV1Modified -eq $false -and
        $coreSettingsDocument.compatibility.authorityV126Modified -eq $false
    $isPhaseBFollowup =
        $coreSettingsDocument.schemaVersion -eq 'knowledge-clipper-core-phase-b-followup-checkpoint.v1' -and
        $coreSettingsDocument.checkpointVersion -eq '2.4.6' -and
        $coreSettingsDocument.status -eq 'passed-core-fixes-awaiting-speech-authority' -and
        $coreSettingsDocument.core.version -eq '2.4.5' -and
        $coreSettingsDocument.fixes.ankiLegacyExecutionProfile.anki -eq 'cards-anki' -and
        $coreSettingsDocument.fixes.ankiLegacyExecutionProfile.externalCallBeforePriorFailure -eq $false -and
        $coreSettingsDocument.fixes.remnoteLearningProjection.route -eq 'clip-daily-route' -and
        $coreSettingsDocument.fixes.remnoteLearningProjection.textPerLineModified -eq $false -and
        $coreSettingsDocument.validation.orchestrator.status -eq 'passed' -and
        $coreSettingsDocument.validation.orchestrator.tests -ge 23 -and
        $coreSettingsDocument.validation.coreAdapter.status -eq 'passed' -and
        $coreSettingsDocument.validation.settings.externalCalls -eq 0 -and
        $coreSettingsDocument.validation.settings.physicalSpeakerInvocations -eq 0 -and
        $coreSettingsDocument.priorPhaseB.final -eq $false -and
        $coreSettingsDocument.priorPhaseB.dRemNoteUnknownExternalStateAutoRetried -eq $false -and
        $coreSettingsDocument.compatibility.frozenContractsV1Modified -eq $false -and
        $coreSettingsDocument.compatibility.authorityV128Modified -eq $false
    $isCorrectedPhaseBFollowup =
        $coreSettingsDocument.schemaVersion -eq 'knowledge-clipper-core-phase-b-followup-checkpoint.v1' -and
        $coreSettingsDocument.checkpointVersion -eq '2.4.8' -and
        $coreSettingsDocument.status -eq 'passed-core-runtime-authority-ready' -and
        $coreSettingsDocument.core.version -eq '2.4.6' -and
        $coreSettingsDocument.fixes.ankiLegacyExecutionProfile.anki -eq 'cards-anki' -and
        $coreSettingsDocument.fixes.ankiLegacyExecutionProfile.externalCallBeforePriorFailure -eq $false -and
        $coreSettingsDocument.fixes.remnoteLearningProjection.route -eq 'clip-daily-route' -and
        $coreSettingsDocument.fixes.remnoteLearningProjection.textPerLineModified -eq $false -and
        $coreSettingsDocument.validation.orchestrator.status -eq 'passed' -and
        $coreSettingsDocument.validation.orchestrator.tests -ge 23 -and
        $coreSettingsDocument.validation.coreAdapter.status -eq 'passed' -and
        $coreSettingsDocument.validation.settings.externalCalls -eq 0 -and
        $coreSettingsDocument.validation.settings.physicalSpeakerInvocations -eq 0 -and
        $coreSettingsDocument.priorPhaseB.final -eq $false -and
        $coreSettingsDocument.priorPhaseB.dRemNoteUnknownExternalStateAutoRetried -eq $false -and
        $coreSettingsDocument.priorPhaseB.authority -eq '1.2.8' -and
        $coreSettingsDocument.priorPhaseB.authorityCheckpointSha256 -eq '4d2997b2abee89e41dc3f77815ae43122079e2dafea7c773a22fcb29c5bbae11' -and
        $coreSettingsDocument.priorPhaseB.executionReceipt -eq 'canary/phase-b-v1.2.8/20260828T090400Z/execution-receipt.json' -and
        $coreSettingsDocument.authorityBoundary.speechPlaybackDeepPathReleasePending -eq $false -and
        $coreSettingsDocument.tree.totalBytes -gt 0 -and
        $coreSettingsDocument.compatibility.frozenContractsV1Modified -eq $false -and
        $coreSettingsDocument.compatibility.authorityV1210Modified -eq $false
    $isCoreIntegrationV2 =
        $coreSettingsDocument.schemaVersion -eq 'knowledge-clipper-core-integration-checkpoint.v2' -and
        $coreSettingsDocument.status -eq 'passed-offline-authority-admission-ready' -and
        ([string]$coreSettingsDocument.checkpointVersion) -match '^2\.[5-9]\.\d+$' -and
        ([string]$coreSettingsDocument.core.version) -match '^2\.[5-9]\.\d+$' -and
        $coreSettingsDocument.validation.contracts.status -eq 'passed' -and
        $coreSettingsDocument.validation.contracts.frozenV1Modified -eq $false -and
        $coreSettingsDocument.validation.settings.status -eq 'passed' -and
        $coreSettingsDocument.validation.settings.externalCalls -eq 0 -and
        $coreSettingsDocument.validation.settings.physicalSpeakerInvocations -eq 0 -and
        $coreSettingsDocument.validation.coreAdapter.status -eq 'passed' -and
        $coreSettingsDocument.validation.orchestrator.status -eq 'passed' -and
        $coreSettingsDocument.validation.orchestrator.tests -ge 23 -and
        $coreSettingsDocument.validation.questionWorkflow.status -eq 'passed' -and
        $coreSettingsDocument.validation.questionWorkflow.initialWrites -eq 0 -and
        $coreSettingsDocument.validation.questionWorkflow.followUpWrites -eq 0 -and
        $coreSettingsDocument.validation.questionWorkflow.replayWrites -eq 0 -and
        $coreSettingsDocument.compatibility.frozenContractsV1Modified -eq $false -and
        $coreSettingsDocument.compatibility.priorAuthorityModified -eq $false -and
        $coreSettingsDocument.compatibility.externalCalls -eq 0 -and
        $coreSettingsDocument.compatibility.mineruCalls -eq 0 -and
        $coreSettingsDocument.compatibility.remnoteWrites -eq 0 -and
        $coreSettingsDocument.compatibility.ankiWrites -eq 0 -and
        $coreSettingsDocument.compatibility.physicalSpeakerInvocations -eq 0 -and
        $coreSettingsDocument.authorityBoundary.promotion -eq 'pending-append-only-authority-assembly' -and
        @($coreSettingsDocument.tree.files).Count -eq [int]$coreSettingsDocument.tree.fileCount -and
        [long]$coreSettingsDocument.tree.totalBytes -gt 0 -and
        -not [string]::IsNullOrWhiteSpace([string]$coreSettingsDocument.tree.treeSha256)
    if (-not $isSettingsIntegration -and -not $isFinalAdmission -and -not $isPhaseBFollowup -and -not $isCorrectedPhaseBFollowup -and -not $isCoreIntegrationV2) {
        throw 'Core settings/admission checkpoint does not prove an accepted offline authority boundary.'
    }
    if (($isPhaseBFollowup -or $isCorrectedPhaseBFollowup) -and @($workerEvidence | Where-Object workerId -eq 'speech.edge')[0].workerVersion -ne '1.2.3') {
        throw 'Core Phase B follow-up checkpoint requires speech.edge 1.2.3 to close the pending deep-path playback boundary.'
    }
    if ($isCorrectedPhaseBFollowup) {
        $coreTotalBytes = [long]0
        $coreTreeRows = @()
        foreach ($file in $coreSettingsDocument.tree.files) {
            $absolute = [IO.Path]::GetFullPath((Join-Path $ProjectRoot ([string]$file.path)))
            $projectPrefix = $ProjectRoot.TrimEnd('\') + '\'
            if (-not $absolute.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
                throw "Core checkpoint tree path is missing or escaped the project root: $($file.path)"
            }
            $actualBytes = (Get-Item -LiteralPath $absolute).Length
            $actualSha = (Get-Sha256 $absolute).ToLowerInvariant()
            if ($actualBytes -ne [long]$file.bytes -or $actualSha -ne ([string]$file.sha256).ToLowerInvariant()) {
                throw "Core checkpoint tree file drifted: $($file.path)"
            }
            $coreTotalBytes += $actualBytes
            $coreTreeRows += "$($file.path)$([char]0)$(([string]$file.sha256).ToLowerInvariant())$([char]0)$($file.bytes)`n"
        }
        $actualCoreTreeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($coreTreeRows -join '')))).ToLowerInvariant()
        if ($coreTotalBytes -ne [long]$coreSettingsDocument.tree.totalBytes -or $actualCoreTreeSha -ne ([string]$coreSettingsDocument.tree.treeSha256).ToLowerInvariant()) {
            throw 'Core checkpoint tree aggregate bytes/hash drifted.'
        }
        if ((Get-Sha256 $orchestratorPath).ToLowerInvariant() -ne ([string]$coreSettingsDocument.core.sha256).ToLowerInvariant()) {
            throw 'Authority Orchestrator executable does not match the corrected Core checkpoint.'
        }
    }
    if ($isCoreIntegrationV2) {
        $coreTotalBytes = [long]0
        $coreTreeRows = [string[]]@(foreach ($file in $coreSettingsDocument.tree.files) {
            $absolute = [IO.Path]::GetFullPath((Join-Path $ProjectRoot ([string]$file.path)))
            $projectPrefix = $ProjectRoot.TrimEnd('\') + '\'
            if (-not $absolute.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
                throw "Core integration checkpoint tree path is missing or escaped the project root: $($file.path)"
            }
            $actualBytes = (Get-Item -LiteralPath $absolute).Length
            $actualSha = (Get-Sha256 $absolute).ToLowerInvariant()
            if ($actualBytes -ne [long]$file.bytes -or $actualSha -ne ([string]$file.sha256).ToLowerInvariant()) {
                throw "Core integration checkpoint tree file drifted: $($file.path)"
            }
            $coreTotalBytes += $actualBytes
            "$($file.path)`t$actualBytes`t$actualSha"
        })
        [Array]::Sort($coreTreeRows, [StringComparer]::Ordinal)
        $actualCoreTreeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($coreTreeRows -join "`n") + "`n")))).ToLowerInvariant()
        if ($coreTotalBytes -ne [long]$coreSettingsDocument.tree.totalBytes -or $actualCoreTreeSha -ne ([string]$coreSettingsDocument.tree.treeSha256).ToLowerInvariant()) {
            throw 'Core integration checkpoint tree aggregate bytes/hash drifted.'
        }
        if ((Get-Sha256 $orchestratorPath).ToLowerInvariant() -ne ([string]$coreSettingsDocument.core.sha256).ToLowerInvariant()) {
            throw 'Authority Orchestrator executable does not match the Core integration checkpoint.'
        }
    }
    $relativeCoreSettings = [IO.Path]::GetRelativePath($ProjectRoot, $CoreSettingsCheckpointPath).Replace('\', '/')
    $releaseCoreSettings = Join-Path $ReleaseRoot $relativeCoreSettings
    if (-not (Test-Path -LiteralPath $releaseCoreSettings -PathType Leaf) -or (Get-Sha256 $releaseCoreSettings) -ne (Get-Sha256 $CoreSettingsCheckpointPath)) {
        throw 'Release-root Core settings checkpoint drifted.'
    }
    if ($isSettingsIntegration) {
        $coreSettings = [ordered]@{
            checkpointVersion = $coreSettingsDocument.checkpointVersion
            path = $CoreSettingsCheckpointPath
            sha256 = Get-Sha256 $CoreSettingsCheckpointPath
            workflowSettings = $coreSettingsDocument.publicVersions.workflowSettings
            taskSnapshot = $coreSettingsDocument.publicVersions.taskSnapshot
            treeSha256 = $coreSettingsDocument.tree.treeSha256
            moduleEvidence = $coreSettingsDocument.moduleEvidence
        }
    }
    elseif ($isFinalAdmission) {
        $coreSettings = [ordered]@{
            checkpointVersion = $coreSettingsDocument.checkpointVersion
            path = $CoreSettingsCheckpointPath
            sha256 = Get-Sha256 $CoreSettingsCheckpointPath
            coreVersion = $coreSettingsDocument.core.version
            coreExecutableSha256 = $coreSettingsDocument.core.sha256
            content = $coreSettingsDocument.content
            speech = $coreSettingsDocument.speech
            supersedes = $coreSettingsDocument.supersedes
            speechDeepPathReleasePending = [bool]$coreSettingsDocument.authorityBoundary.speechDeepPathReleasePending
        }
    }
    elseif ($isCoreIntegrationV2) {
        $coreSettings = [ordered]@{
            schemaVersion = $coreSettingsDocument.schemaVersion
            checkpointVersion = $coreSettingsDocument.checkpointVersion
            path = $CoreSettingsCheckpointPath
            sha256 = Get-Sha256 $CoreSettingsCheckpointPath
            coreVersion = $coreSettingsDocument.core.version
            coreExecutableSha256 = $coreSettingsDocument.core.sha256
            treeFileCount = [int]$coreSettingsDocument.tree.fileCount
            treeTotalBytes = [long]$coreSettingsDocument.tree.totalBytes
            treeSha256 = $coreSettingsDocument.tree.treeSha256
            validation = $coreSettingsDocument.validation
            authorityBoundary = $coreSettingsDocument.authorityBoundary
        }
    }
    else {
        $coreSettings = [ordered]@{
            checkpointVersion = $coreSettingsDocument.checkpointVersion
            path = $CoreSettingsCheckpointPath
            sha256 = Get-Sha256 $CoreSettingsCheckpointPath
            coreVersion = $coreSettingsDocument.core.version
            coreExecutableSha256 = $coreSettingsDocument.core.sha256
            fixes = $coreSettingsDocument.fixes
            validation = $coreSettingsDocument.validation
            priorPhaseB = $coreSettingsDocument.priorPhaseB
            speechPlaybackDeepPathReleasePending = [bool]$coreSettingsDocument.authorityBoundary.speechPlaybackDeepPathReleasePending
        }
    }
}

$assemblyPathGate = $null
if (-not [string]::IsNullOrWhiteSpace($AssemblyPathGatePath)) {
    $pathGateDocument = Get-Content -LiteralPath $AssemblyPathGatePath -Raw | ConvertFrom-Json -Depth 100
    $assemblyReceiptPath = Join-Path $AuthorityDirectory 'assembly-receipt.v1.json'
    if ($pathGateDocument.status -ne 'passed' -or $pathGateDocument.pathCount -ne 5 -or
        -not $pathGateDocument.allExist -or -not $pathGateDocument.allWithinFinalRoots -or
        -not $pathGateDocument.allBytesAndHashesMatch -or $pathGateDocument.assemblyReceipt.containsTempPath -or
        [IO.Path]::GetFullPath($pathGateDocument.authorityDirectory) -ne $AuthorityDirectory -or
        [IO.Path]::GetFullPath($pathGateDocument.releaseRoot) -ne $ReleaseRoot -or
        [IO.Path]::GetFullPath($pathGateDocument.assemblyReceipt.path) -ne $assemblyReceiptPath -or
        $pathGateDocument.assemblyReceipt.sha256 -ne (Get-Sha256 $assemblyReceiptPath)) {
        throw 'Assembly path gate does not prove final-root path/hash/bytes integrity.'
    }
    $assemblyPathGate = [ordered]@{ path = $AssemblyPathGatePath; sha256 = Get-Sha256 $AssemblyPathGatePath; status = 'passed'; paths = 5; tempPaths = 0 }
}

$supersedes = $null
if (-not [string]::IsNullOrWhiteSpace($SupersedesCheckpointPath)) {
    $superseded = Get-Content -LiteralPath $SupersedesCheckpointPath -Raw | ConvertFrom-Json
    $supersedes = [ordered]@{
        checkpointId = $superseded.checkpointId
        registryVersion = $superseded.registry.registryVersion
        path = $SupersedesCheckpointPath
        sha256 = Get-Sha256 $SupersedesCheckpointPath
        immutable = $true
    }
}

$summary = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-checkpoint.v1'
    checkpointId = "knowledge-clipper-authority-$RegistryVersion-$CheckpointStamp"
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    admissionStatus = $AdmissionStatus
    frozenV1 = [ordered]@{ receipt = $frozenReceiptPath; files = 21; mismatches = 0; sha256 = Get-Sha256 $frozenReceiptPath }
    registry = [ordered]@{ path = $registryPath; registryVersion = $RegistryVersion; workers = 5; sha256 = Get-Sha256 $registryPath }
    release = [ordered]@{ root = $ReleaseRoot; files = $releaseFiles.Count; bytes = $releaseBytes; treeSha256 = $treeSha256; dependencyManifest = $dependencyManifestPath; dependencyManifestSha256 = Get-Sha256 $dependencyManifestPath }
    validation = [ordered]@{ command = 'KnowledgeClipper.Orchestrator.exe validate-registry'; exitCode = 0; status = 'valid'; workers = 5 }
    workers = $workerEvidence
    coreWorkflow = $coreWorkflow
    coreSettings = $coreSettings
    assemblyPathGate = $assemblyPathGate
    runtimeGates = $runtimeGates
    portableSchemaClosure = $portableSchemaClosure
    supersedes = $supersedes
    caveat = if ($AdmissionStatus -eq 'candidate-admitted') { 'Candidate admission is not authority or end-to-end readiness.' } else { $null }
}

if ($ValidateOnly) {
    $summary | ConvertTo-Json -Depth 32
    return
}

$checkpointPath = Join-Path $AuthorityDirectory 'checkpoint.json'
$receiptPath = Join-Path $ProjectRoot "receipts\knowledge-clipper-authority-$RegistryVersion-$CheckpointStamp.json"
foreach ($snapshot in $describeSnapshots) {
    if (Test-Path -LiteralPath $snapshot.path -PathType Leaf) {
        $existingText = Get-Content -LiteralPath $snapshot.path -Raw
        if ($existingText -ne $snapshot.text) { throw "Existing describe snapshot drifted: $($snapshot.path)" }
    }
    else {
        Write-TextOnce $snapshot.path $snapshot.text
    }
}
Write-JsonOnce $checkpointPath $summary
$checkpointSha256 = Get-Sha256 $checkpointPath
$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-receipt.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    admissionStatus = $AdmissionStatus
    checkpoint = [ordered]@{ path = $checkpointPath; sha256 = $checkpointSha256 }
    registry = $summary.registry
    release = $summary.release
    frozenV1 = $summary.frozenV1
    validation = $summary.validation
    coreWorkflow = $summary.coreWorkflow
    coreSettings = $summary.coreSettings
    assemblyPathGate = $summary.assemblyPathGate
    runtimeGates = $summary.runtimeGates
    portableSchemaClosure = $summary.portableSchemaClosure
    supersedes = $summary.supersedes
}
Write-JsonOnce $receiptPath $receipt
[ordered]@{ status = 'written'; checkpoint = $checkpointPath; checkpointSha256 = $checkpointSha256; receipt = $receiptPath; receiptSha256 = Get-Sha256 $receiptPath } | ConvertTo-Json -Depth 8

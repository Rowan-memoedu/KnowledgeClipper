[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$NewRegistryVersion = '1.2.11',
    [string]$CoreVersion = '2.4.6',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$baseReleaseRoot = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.6-20260828T074944Z\win-x64'
$baseAuthority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.6-20260828T074944Z'
$coreExecutable = Join-Path $ProjectRoot "dist\orchestrator\win-x64\v$CoreVersion\KnowledgeClipper.Orchestrator.exe"
$coreCheckpointRelative = 'contracts/core/v2.4/settings-control/checkpoints/v2.4.8-20260828T092700Z/checkpoint.json'
$coreCheckpoint = Join-Path $ProjectRoot $coreCheckpointRelative
$releaseId = "knowledge-clipper-authority-v$NewRegistryVersion-$ReleaseStamp"
$releaseParent = Join-Path $ProjectRoot 'releases'
$authorityParent = Join-Path $ProjectRoot 'contracts\authority\worker-registry'
$finalReleaseContainer = Join-Path $releaseParent $releaseId
$finalReleaseRoot = Join-Path $finalReleaseContainer 'win-x64'
$finalAuthority = Join-Path $authorityParent "v$NewRegistryVersion-$ReleaseStamp"
$stagingReleaseContainer = Join-Path $releaseParent ('.' + $releaseId + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$stagingReleaseRoot = Join-Path $stagingReleaseContainer 'win-x64'
$stagingAuthority = Join-Path $authorityParent ('.v' + $NewRegistryVersion + '-' + $ReleaseStamp + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')

$moduleSources = [ordered]@{
    'image.ocr.mineru' = [ordered]@{
        version = '1.2.0'; source = 'workers/image-ocr/candidates/1.2.0'; destination = 'workers/image-ocr'
        executable = 'workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe'; sha256 = '80FE1D2D7D371D135658B36AC22735490DADD5239D263C2E1B3025D5FC7221B3'
        operations = @('image-ocr')
    }
    'content.deepseek' = [ordered]@{
        version = '0.6.1'; source = 'content-worker/releases/content.deepseek-0.6.1-win-x64-formal-20260828T082100Z'; destination = 'workers/content'
        executable = 'workers/content/KnowledgeClipper.Content.Worker.exe'; sha256 = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'
        operations = @('understand-and-card', 'question.answer', 'question.make-cards')
    }
    'speech.edge' = [ordered]@{
        version = '1.2.3'; source = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260828T090958Z-88d3f9d3/win-x64/workers/speech'; destination = 'workers/speech'
        executable = 'workers/speech/KnowledgeClipper.Speech.Worker.exe'; sha256 = '88D3F9D3BD6BFDC9893098903E65ED7AEB2977FF95CA57C421CC2A443B5D8C1A'
        operations = @('speech.synthesize', 'speech.playback')
    }
    'export.remnote' = [ordered]@{
        version = '2.0.0'; source = 'workers/remnote'; destination = 'workers/remnote'
        executable = 'workers/remnote/KnowledgeClipper.RemNoteExporter.Worker.exe'; sha256 = 'D388301A8667AD334860A541C18300E5F3F0D3923477D5B2A3188B176E7DDCA6'
        operations = @('remnote.export')
    }
    'export.anki' = [ordered]@{
        version = '1.2.0'; source = 'releases/knowledge-clipper-anki-exporter-v1.2.0-20260828T072346Z/win-x64/workers/anki'; destination = 'workers/anki'
        executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = '661F473D42B752BFF9B1211C2588F8856AAABA24012F9420ABABE09C65F766D2'
        operations = @('export')
    }
}

$evidenceFiles = [ordered]@{
    'image-admission-receipt.json' = 'workers/image-ocr/candidates/1.2.0/admission-receipt.v1.json'
    'content-formal-release-receipt.json' = 'content-worker/canary/formal-release/content.deepseek-0.6.1-win-x64-formal-20260828T082100Z/20260828T082055252Z-settings-snapshot-replay/formal-release-offline-receipt.v1.json'
    'content-live-phase-b-receipt.json' = 'content-worker/canary/jobs/20260828T081953675Z-offline-phase-b-generated-content-v1-live-3bd60c10/canary-receipt.json'
    'remnote-v2-offline-receipt.json' = 'canary/remnote-exporter-v2-offline/20260828T143218/receipt.json'
    'anki-formal-release-receipt.json' = 'workers/anki-exporter/canary/formal-release-settings-probe/20260828T072807Z-4e1cb28a/receipt.json'
    'speech-formal-release-receipt.json' = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260828T090958Z-88d3f9d3/speech-formal-win-x64-release-receipt.json'
    'speech-independent-validation-receipt.json' = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260828T090958Z-88d3f9d3/speech-formal-win-x64-independent-validation-receipt.json'
    'speech-deep-path-canary-receipt.json' = 'tests/RemNoteQuickClipper.Speech.Tests/pc/r090937d6c/deep-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx/playback-canary-receipt.json'
    'speech-deep-path-contract-validation-receipt.json' = 'tests/RemNoteQuickClipper.Speech.Tests/pc/r090937d6c/deep-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx/playback-contract-validation-receipt.json'
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Write-Json([string]$Path, [object]$Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
}
function Copy-Directory([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Source directory is missing: $Source" }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in [IO.Directory]::EnumerateDirectories($Source, '*', [IO.SearchOption]::AllDirectories)) {
        [IO.Directory]::CreateDirectory((Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $directory)))) | Out-Null
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $target = Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $file))
        [IO.File]::Copy($file, $target, $false)
    }
}
function Assert-StagingPath([string]$Path, [string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escaped staging root: $resolved" }
}
function Replace-StagingDirectory([string]$Source, [string]$Destination) {
    Assert-StagingPath $Destination $stagingReleaseRoot
    if (Test-Path -LiteralPath $Destination) { [IO.Directory]::Delete($Destination, $true) }
    Copy-Directory $Source $Destination
}
function Remove-VerifiedStaging([string]$Path, [string]$ExpectedParent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('.')) {
        throw "Refusing to remove unexpected staging directory: $resolved"
    }
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            [IO.Directory]::Delete($resolved, $true)
            return
        }
        catch {
            if ($attempt -eq 5) {
                Write-Warning "Verified staging directory remains for forensic cleanup: $resolved"
                return
            }
            Start-Sleep -Milliseconds 500
        }
    }
}
function Get-ReleaseTree([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File)
    $rows = foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        "$relative`t$($file.Length)`t$((Get-Sha256 $file.FullName).ToLowerInvariant())"
    }
    $rows = [string[]]$rows
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    $treeBytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    [ordered]@{
        files = $files.Count
        bytes = [long](($files | Measure-Object Length -Sum).Sum)
        treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($treeBytes))
    }
}

foreach ($required in @($baseReleaseRoot, $baseAuthority, $coreExecutable, $coreCheckpoint)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required authority input is missing: $required" }
}
$coreCheckpointDocument = Get-Content -LiteralPath $coreCheckpoint -Raw | ConvertFrom-Json -Depth 100
$coreCheckpointSha256 = Get-Sha256 $coreCheckpoint
if ($coreCheckpointDocument.checkpointVersion -ne '2.4.8' -or
    $coreCheckpointDocument.status -ne 'passed-core-runtime-authority-ready' -or
    $coreCheckpointDocument.core.version -ne $CoreVersion -or
    $coreCheckpointDocument.tree.totalBytes -le 0 -or
    $coreCheckpointDocument.authorityBoundary.speechPlaybackDeepPathReleasePending -ne $false) {
    throw 'Core settings checkpoint does not prove the corrected runtime authority boundary.'
}
if ((Get-Sha256 $coreExecutable) -ne $coreCheckpointDocument.core.sha256) { throw 'Core executable SHA drifted from its checkpoint.' }
foreach ($module in $moduleSources.Values) {
    $sourceRoot = Join-Path $ProjectRoot $module.source
    $sourceExecutable = Join-Path $sourceRoot ([IO.Path]::GetFileName($module.executable))
    if ($module.destination -eq 'workers/content') { $sourceExecutable = Join-Path $sourceRoot 'KnowledgeClipper.Content.Worker.exe' }
    elseif ($module.destination -eq 'workers/anki') { $sourceExecutable = Join-Path $sourceRoot 'KnowledgeClipper.AnkiExporter.Worker.exe' }
    elseif ($module.destination -eq 'workers/image-ocr') { $sourceExecutable = Join-Path $sourceRoot 'KnowledgeClipper.ImageOcr.Worker.exe' }
    elseif ($module.destination -eq 'workers/speech') { $sourceExecutable = Join-Path $sourceRoot 'KnowledgeClipper.Speech.Worker.exe' }
    elseif ($module.destination -eq 'workers/remnote') { $sourceExecutable = Join-Path $sourceRoot 'KnowledgeClipper.RemNoteExporter.Worker.exe' }
    if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf) -or (Get-Sha256 $sourceExecutable) -ne $module.sha256) {
        throw "Module source executable drifted: $($module.destination)"
    }
}
foreach ($target in @($finalReleaseContainer, $finalAuthority)) { if (Test-Path -LiteralPath $target) { throw "Append-only target exists: $target" } }

try {
    Copy-Directory $baseReleaseRoot $stagingReleaseRoot
    [IO.Directory]::CreateDirectory($stagingAuthority) | Out-Null
    [IO.File]::Copy($coreExecutable, (Join-Path $stagingReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'), $true)

    foreach ($module in $moduleSources.Values) {
        Replace-StagingDirectory (Join-Path $ProjectRoot $module.source) (Join-Path $stagingReleaseRoot $module.destination)
    }
    foreach ($pair in $evidenceFiles.GetEnumerator()) {
        $source = Join-Path $ProjectRoot $pair.Value
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Evidence is missing: $($pair.Value)" }
        $destination = Join-Path $stagingReleaseRoot "evidence\modules\$($pair.Key)"
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $true)
    }

    $contractDirectories = [ordered]@{
        'contracts/core/v2.4/settings-control' = 'contracts/core/v2.4/settings-control'
        'workers/image-ocr/schemas' = 'contracts/modules/image-ocr'
        'content-worker/schemas' = 'contracts/modules/content'
        'workers/anki-exporter/contracts' = 'contracts/modules/anki'
    }
    foreach ($pair in $contractDirectories.GetEnumerator()) {
        Replace-StagingDirectory (Join-Path $ProjectRoot $pair.Key) (Join-Path $stagingReleaseRoot $pair.Value)
    }
    foreach ($relative in @(
        'contracts/remnote-destination-settings-descriptor.v1.schema.json',
        'contracts/remnote-destination-settings-snapshot.v1.schema.json',
        'contracts/remnote-destination-settings.v1.schema.json',
        'contracts/remnote-export-payload.v2.schema.json',
        'contracts/remnote-export-task.v2.schema.json',
        'contracts/remnote-write-receipt.v2.schema.json')) {
        $destination = Join-Path $stagingReleaseRoot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy((Join-Path $ProjectRoot $relative), $destination, $true)
    }

    $registry = Get-Content -LiteralPath (Join-Path $baseAuthority 'worker-registry.v1.json') -Raw | ConvertFrom-Json -Depth 100
    $registry.registryVersion = $NewRegistryVersion
    $describeDirectory = Join-Path $stagingAuthority 'describe'
    [IO.Directory]::CreateDirectory($describeDirectory) | Out-Null
    $descriptors = [ordered]@{}
    foreach ($entry in $registry.workers) {
        $module = $moduleSources[$entry.workerId]
        $executable = Join-Path $stagingReleaseRoot $module.executable
        if ((Get-Sha256 $executable) -ne $module.sha256) { throw "Staged executable hash drifted: $($entry.workerId)" }
        $describeOutput = & $executable describe --json 2>&1
        if ($LASTEXITCODE -ne 0) { throw "describe failed for $($entry.workerId): $($describeOutput -join [Environment]::NewLine)" }
        $describeText = $describeOutput -join [Environment]::NewLine
        $descriptor = $describeText | ConvertFrom-Json -Depth 100
        if ($descriptor.workerId -ne $entry.workerId -or $descriptor.workerVersion -ne $module.version -or $descriptor.contractVersion -ne '1.0.0') {
            throw "Worker identity drifted: $($entry.workerId)"
        }
        $entry.stages = @($descriptor.stages)
        $entry.inputKinds = @($descriptor.inputKinds)
        $entry.outputKinds = @($descriptor.outputKinds)
        $entry.sideEffectLevel = $descriptor.sideEffectLevel
        $entry.idempotencyLevel = $descriptor.idempotencyLevel
        $describePath = Join-Path $describeDirectory "$($entry.workerId).describe.json"
        Write-Json $describePath $descriptor
        $finalDescribePath = Join-Path $finalAuthority "describe\$($entry.workerId).describe.json"
        $descriptors[$entry.workerId] = [ordered]@{
            workerVersion = $descriptor.workerVersion
            path = $finalDescribePath
            bytes = (Get-Item -LiteralPath $describePath).Length
            sha256 = Get-Sha256 $describePath
        }
    }
    $registryPath = Join-Path $stagingAuthority 'worker-registry.v1.json'
    Write-Json $registryPath $registry
    $validationOutput = & (Join-Path $stagingReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe') validate-registry --registry $registryPath --release-root $stagingReleaseRoot 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Runtime registry admission failed: $($validationOutput -join [Environment]::NewLine)" }
    $validation = ($validationOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
    if ($validation.status -ne 'valid' -or $validation.workers -ne 5 -or $validation.registryVersion -ne $NewRegistryVersion) { throw 'Unexpected registry validation result.' }

    $tree = Get-ReleaseTree $stagingReleaseRoot
    $keyFiles = @([ordered]@{ path = 'orchestrator/KnowledgeClipper.Orchestrator.exe'; bytes = (Get-Item (Join-Path $stagingReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe')).Length; sha256 = Get-Sha256 (Join-Path $stagingReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe') })
    foreach ($module in $moduleSources.Values) {
        $file = Join-Path $stagingReleaseRoot $module.executable
        $keyFiles += [ordered]@{ path = $module.executable; bytes = (Get-Item $file).Length; sha256 = Get-Sha256 $file }
    }
    $releaseCoreCheckpoint = Join-Path $stagingReleaseRoot $coreCheckpointRelative
    $keyFiles += [ordered]@{ path = $coreCheckpointRelative; bytes = (Get-Item $releaseCoreCheckpoint).Length; sha256 = Get-Sha256 $releaseCoreCheckpoint }
    $operations = [ordered]@{}
    foreach ($pair in $moduleSources.GetEnumerator()) { $operations[$pair.Key] = $pair.Value.operations }
    $dependency = [ordered]@{
        schemaVersion = 'knowledge-clipper-release-dependency-manifest.v1'
        releaseId = $releaseId
        releaseRoot = "releases/$releaseId/win-x64"
        files = $tree.files
        bytes = $tree.bytes
        treeHashAlgorithm = 'sort Windows relative paths ordinal; emit path<TAB>bytes<TAB>lowercase SHA-256; join LF; SHA-256 UTF-8'
        treeSha256 = $tree.treeSha256
        keyFiles = $keyFiles
        workerOperations = $operations
        legacyEntrypointsExcluded = $true
        baseAuthority = [ordered]@{ registryVersion = '1.2.6'; releaseId = 'knowledge-clipper-authority-v1.2.6-20260828T074944Z'; treeSha256 = '1DF64264BB69F4039C3B58956AA9612B98E45C2DF38B12D396B4813377E512EE' }
        supersedesAuthority = [ordered]@{ registryVersion = '1.2.10'; checkpoint = 'contracts/authority/worker-registry/v1.2.10-20260828T092300Z/checkpoint.json'; checkpointSha256 = 'C19995D2AB6BDCAE3D35C61E3305554A15ADB906A4C3F98BED9015FEAFDD95CF'; invalidationReceipt = 'contracts/authority/worker-registry/v1.2.10-20260828T092300Z/invalidation-receipt.v1.json'; invalidatedNeverPromoted = $true }
        coreSettings = [ordered]@{ version = $CoreVersion; checkpoint = $coreCheckpointRelative; checkpointSha256 = $coreCheckpointSha256 }
        moduleEvidence = $evidenceFiles
    }
    $dependencyPath = Join-Path $stagingAuthority 'dependency-manifest.v1.json'
    Write-Json $dependencyPath $dependency
    $assemblyReceiptPath = Join-Path $stagingAuthority 'assembly-receipt.v1.json'
    Write-Json $assemblyReceiptPath ([ordered]@{
        schemaVersion = 'knowledge-clipper-authority-assembly-receipt.v1'
        status = 'candidate-built-offline'
        releaseId = $releaseId
        releaseRoot = $finalReleaseRoot
        authorityDirectory = $finalAuthority
        registryVersion = $NewRegistryVersion
        release = $tree
        registrySha256 = Get-Sha256 $registryPath
        dependencyManifestSha256 = Get-Sha256 $dependencyPath
        core = [ordered]@{ version = $CoreVersion; executableSha256 = Get-Sha256 (Join-Path $stagingReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'); checkpointSha256 = $coreCheckpointSha256 }
        workers = $descriptors
        registryValidation = $validation
        externalWrites = 0
        physicalSpeakerInvocations = 0
    })

    [IO.Directory]::Move($stagingReleaseContainer, $finalReleaseContainer)
    [IO.Directory]::Move($stagingAuthority, $finalAuthority)
    $finalAssemblyReceipt = Join-Path $finalAuthority 'assembly-receipt.v1.json'
    $assemblyText = Get-Content -LiteralPath $finalAssemblyReceipt -Raw
    if ($assemblyText.Contains('.tmp', [StringComparison]::OrdinalIgnoreCase)) { throw 'Final assembly receipt contains a staging path.' }
    $assembly = $assemblyText | ConvertFrom-Json -Depth 100
    foreach ($workerProperty in $assembly.workers.PSObject.Properties) {
        $workerEvidence = $workerProperty.Value
        $evidencePath = [IO.Path]::GetFullPath([string]$workerEvidence.path)
        $authorityPrefix = $finalAuthority.TrimEnd('\') + '\'
        $releasePrefix = $finalReleaseRoot.TrimEnd('\') + '\'
        if (-not ($evidencePath.StartsWith($authorityPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                  $evidencePath.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase))) {
            throw "Assembly evidence path escaped the final authority/release roots: $evidencePath"
        }
        if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { throw "Assembly evidence path is missing: $evidencePath" }
        $evidenceFile = Get-Item -LiteralPath $evidencePath
        if ($evidenceFile.Length -ne $workerEvidence.bytes -or (Get-Sha256 $evidencePath) -ne $workerEvidence.sha256) {
            throw "Assembly evidence bytes/hash drifted: $evidencePath"
        }
    }
    $pathGateOutput = & (Join-Path $PSScriptRoot 'Test-KnowledgeClipperAuthorityAssemblyPaths.ps1') -AuthorityDirectory $finalAuthority -ReleaseRoot $finalReleaseRoot
    if ($LASTEXITCODE -ne 0) { throw 'Durable assembly path gate failed.' }
    $pathGate = ($pathGateOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
    [ordered]@{
        releaseId = $releaseId
        releaseRoot = $finalReleaseRoot
        authorityDirectory = $finalAuthority
        registrySha256 = Get-Sha256 (Join-Path $finalAuthority 'worker-registry.v1.json')
        dependencyManifestSha256 = Get-Sha256 (Join-Path $finalAuthority 'dependency-manifest.v1.json')
        assemblyReceiptSha256 = Get-Sha256 $finalAssemblyReceipt
        assemblyPathGate = [ordered]@{ status = $pathGate.status; path = $pathGate.gate; sha256 = $pathGate.sha256; paths = $pathGate.paths; tempPaths = 0 }
        release = $tree
    } | ConvertTo-Json -Depth 20
}
finally {
    Remove-VerifiedStaging $stagingReleaseContainer $releaseParent
    Remove-VerifiedStaging $stagingAuthority $authorityParent
}

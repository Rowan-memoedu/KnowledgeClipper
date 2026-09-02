[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseReleaseRoot,
    [Parameter(Mandatory = $true)][string]$BaseAuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$NewRegistryVersion,
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [Parameter(Mandatory = $true)][string]$RemNoteExporterPath,
    [Parameter(Mandatory = $true)][string]$ExpectedRemNoteExporterVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedRemNoteExporterSha256,
    [string]$AnkiExporterPath,
    [string]$ExpectedAnkiExporterVersion,
    [string]$ExpectedAnkiExporterSha256,
    [long]$ExpectedAnkiExporterBytes,
    [string]$CoreSettingsCheckpointPath,
    [string]$CoreRuntimePath,
    [string]$ExpectedCoreRuntimeVersion,
    [string]$ExpectedCoreRuntimeSha256,
    [long]$ExpectedCoreRuntimeBytes,
    [string]$CoreAssetsPath,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$BaseReleaseRoot = [IO.Path]::GetFullPath($BaseReleaseRoot)
$BaseAuthorityDirectory = [IO.Path]::GetFullPath($BaseAuthorityDirectory)
$RemNoteExporterPath = [IO.Path]::GetFullPath($RemNoteExporterPath)
$CoreSettingsCheckpointPath = if ([string]::IsNullOrWhiteSpace($CoreSettingsCheckpointPath)) { $null } else { [IO.Path]::GetFullPath($CoreSettingsCheckpointPath) }
$CoreRuntimePath = if ([string]::IsNullOrWhiteSpace($CoreRuntimePath)) { $null } else { [IO.Path]::GetFullPath($CoreRuntimePath) }
$CoreAssetsPath = if ([string]::IsNullOrWhiteSpace($CoreAssetsPath)) { $null } else { [IO.Path]::GetFullPath($CoreAssetsPath) }
$ankiConfigured = -not [string]::IsNullOrWhiteSpace($AnkiExporterPath) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedAnkiExporterVersion) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedAnkiExporterSha256) -or
    $ExpectedAnkiExporterBytes -gt 0
if ($ankiConfigured) {
    if ([string]::IsNullOrWhiteSpace($AnkiExporterPath) -or
        [string]::IsNullOrWhiteSpace($ExpectedAnkiExporterVersion) -or
        [string]::IsNullOrWhiteSpace($ExpectedAnkiExporterSha256) -or
        $ExpectedAnkiExporterBytes -le 0) {
        throw 'Anki replacement requires path, version, SHA-256, and positive byte length.'
    }
    $AnkiExporterPath = [IO.Path]::GetFullPath($AnkiExporterPath)
}
$releaseParent = Join-Path $ProjectRoot 'releases'
$authorityParent = Join-Path $ProjectRoot 'contracts\authority\worker-registry'
$releaseId = "knowledge-clipper-authority-v$NewRegistryVersion-$ReleaseStamp"
$finalReleaseContainer = Join-Path $releaseParent $releaseId
$finalReleaseRoot = Join-Path $finalReleaseContainer 'win-x64'
$finalAuthorityDirectory = Join-Path $authorityParent ("v$NewRegistryVersion-$ReleaseStamp")
$stagingReleaseContainer = Join-Path $releaseParent ('.' + $releaseId + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$stagingReleaseRoot = Join-Path $stagingReleaseContainer 'win-x64'
$stagingAuthorityDirectory = Join-Path $authorityParent ('.v' + $NewRegistryVersion + '-' + $ReleaseStamp + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-Json([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Copy-Directory([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in [IO.Directory]::EnumerateDirectories($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($Source, $directory)
        [IO.Directory]::CreateDirectory((Join-Path $Destination $relative)) | Out-Null
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($Source, $file)
        [IO.File]::Copy($file, (Join-Path $Destination $relative), $false)
    }
}

function Copy-DirectoryReplace([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in [IO.Directory]::EnumerateDirectories($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($Source, $directory)
        [IO.Directory]::CreateDirectory((Join-Path $Destination $relative)) | Out-Null
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($Source, $file)
        [IO.Directory]::CreateDirectory((Split-Path -Parent (Join-Path $Destination $relative))) | Out-Null
        [IO.File]::Copy($file, (Join-Path $Destination $relative), $true)
    }
}

function Copy-FileReplaceRetried([string]$Source, [string]$Destination) {
    $attempts = 20
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            [IO.File]::Copy($Source, $Destination, $true)
            return
        }
        catch [IO.IOException] {
            if ($attempt -eq $attempts) { throw }
            Start-Sleep -Milliseconds 250
        }
        catch [UnauthorizedAccessException] {
            if ($attempt -eq $attempts) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Get-ReleaseTree([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse)
    $rows = foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $hash = (Get-Sha256 $file.FullName).ToLowerInvariant()
        "$relative`t$($file.Length)`t$hash"
    }
    $rows = [string[]]$rows
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    [ordered]@{
        files = $files.Count
        bytes = ($files | Measure-Object Length -Sum).Sum
        treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
}

function Remove-VerifiedStagingDirectory([string]$Path, [string]$ExpectedParent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('.')) {
        throw "Refusing to remove unexpected staging directory: $resolved"
    }
    [IO.Directory]::Delete($resolved, $true)
}

foreach ($required in @(
    $BaseReleaseRoot,
    (Join-Path $BaseAuthorityDirectory 'worker-registry.v1.json'),
    (Join-Path $BaseAuthorityDirectory 'dependency-manifest.v1.json'),
    $RemNoteExporterPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required release input is missing: $required" }
}
if ($ankiConfigured -and -not (Test-Path -LiteralPath $AnkiExporterPath -PathType Leaf)) {
    throw "Required Anki exporter input is missing: $AnkiExporterPath"
}
if ($null -ne $CoreSettingsCheckpointPath -and -not (Test-Path -LiteralPath $CoreSettingsCheckpointPath -PathType Leaf)) {
    throw "Required Core settings checkpoint input is missing: $CoreSettingsCheckpointPath"
}
if ($null -ne $CoreRuntimePath -and (-not (Test-Path -LiteralPath $CoreRuntimePath -PathType Leaf) -or [string]::IsNullOrWhiteSpace($ExpectedCoreRuntimeVersion) -or [string]::IsNullOrWhiteSpace($ExpectedCoreRuntimeSha256) -or $ExpectedCoreRuntimeBytes -le 0)) {
    throw 'Core runtime replacement requires path, version, SHA-256, and positive byte length.'
}
if ($null -ne $CoreAssetsPath -and -not (Test-Path -LiteralPath $CoreAssetsPath -PathType Container)) { throw "Core asset directory is missing: $CoreAssetsPath" }
foreach ($target in @($finalReleaseContainer, $finalAuthorityDirectory)) {
    if (Test-Path -LiteralPath $target) { throw "Append-only target already exists: $target" }
}

$sourceHash = Get-Sha256 $RemNoteExporterPath
if ($sourceHash -ne $ExpectedRemNoteExporterSha256) {
    throw "RemNote exporter hash drift: expected=$ExpectedRemNoteExporterSha256 actual=$sourceHash"
}
$sourceDescribeOutput = & $RemNoteExporterPath describe --json 2>&1
if ($LASTEXITCODE -ne 0) { throw 'RemNote exporter describe failed.' }
$sourceDescribe = ($sourceDescribeOutput -join [Environment]::NewLine) | ConvertFrom-Json
if ($sourceDescribe.workerId -ne 'export.remnote' -or $sourceDescribe.workerVersion -ne $ExpectedRemNoteExporterVersion) {
    throw "RemNote exporter identity/version mismatch: $($sourceDescribe.workerId) $($sourceDescribe.workerVersion)"
}
$ankiDescribe = $null
if ($ankiConfigured) {
    $ankiItem = Get-Item -LiteralPath $AnkiExporterPath
    if ($ankiItem.Length -ne $ExpectedAnkiExporterBytes) {
        throw "Anki exporter byte length drift: expected=$ExpectedAnkiExporterBytes actual=$($ankiItem.Length)"
    }
    $ankiHash = Get-Sha256 $AnkiExporterPath
    if ($ankiHash -ne $ExpectedAnkiExporterSha256) {
        throw "Anki exporter hash drift: expected=$ExpectedAnkiExporterSha256 actual=$ankiHash"
    }
    $ankiDescribeOutput = & $AnkiExporterPath describe --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Anki exporter describe failed.' }
    $ankiDescribe = ($ankiDescribeOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($ankiDescribe.workerId -ne 'export.anki' -or $ankiDescribe.workerVersion -ne $ExpectedAnkiExporterVersion) {
        throw "Anki exporter identity/version mismatch: $($ankiDescribe.workerId) $($ankiDescribe.workerVersion)"
    }
}

try {
    Copy-Directory $BaseReleaseRoot $stagingReleaseRoot
    if ($null -ne $CoreRuntimePath) {
        $runtimeItem = Get-Item -LiteralPath $CoreRuntimePath
        if ($runtimeItem.Length -ne $ExpectedCoreRuntimeBytes -or (Get-Sha256 $CoreRuntimePath) -ne $ExpectedCoreRuntimeSha256) { throw 'Pinned Core runtime does not match the source candidate.' }
        $runtimeDescribe = ((& $CoreRuntimePath describe 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
        if ($LASTEXITCODE -ne 0 -or $runtimeDescribe.version -notlike "$ExpectedCoreRuntimeVersion*") { throw 'Pinned Core runtime identity failed.' }
        $releaseRuntime = Join-Path $stagingReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
        Copy-FileReplaceRetried $CoreRuntimePath $releaseRuntime
        if ((Get-Item -LiteralPath $releaseRuntime).Length -ne $ExpectedCoreRuntimeBytes -or (Get-Sha256 $releaseRuntime) -ne $ExpectedCoreRuntimeSha256) { throw 'Pinned Core runtime hash verification failed.' }
    }
    if ($null -ne $CoreAssetsPath) {
        $relativeAssets = [IO.Path]::GetRelativePath($ProjectRoot, $CoreAssetsPath)
        if ($relativeAssets.StartsWith('..')) { throw 'Core asset directory must be inside the project root.' }
        Copy-Directory $CoreAssetsPath (Join-Path $stagingReleaseRoot $relativeAssets)
    }
    if ($null -ne $CoreSettingsCheckpointPath) {
        $relativeCheckpoint = [IO.Path]::GetRelativePath($ProjectRoot, $CoreSettingsCheckpointPath)
        if ($relativeCheckpoint.StartsWith('..')) { throw 'Core settings checkpoint must be inside the project root.' }
        $releaseCheckpoint = Join-Path $stagingReleaseRoot $relativeCheckpoint
        [IO.Directory]::CreateDirectory((Split-Path -Parent $releaseCheckpoint)) | Out-Null
        [IO.File]::Copy($CoreSettingsCheckpointPath, $releaseCheckpoint, $true)
        if ((Get-Sha256 $releaseCheckpoint) -ne (Get-Sha256 $CoreSettingsCheckpointPath)) {
            throw 'Packed Core settings checkpoint hash verification failed.'
        }
    }
    # Core v2 task-query schemas use a relative ref into frozen contracts/v1.
    # A release is portable only when that immutable dependency is packed under
    # the same relative path; copy bytes verbatim, never rewrite frozen schema.
    $frozenArtifactRelative = 'contracts\v1\schemas\common\artifact.schema.json'
    $frozenArtifactSource = Join-Path $ProjectRoot $frozenArtifactRelative
    $frozenArtifactStaged = Join-Path $stagingReleaseRoot $frozenArtifactRelative
    if (-not (Test-Path -LiteralPath $frozenArtifactSource -PathType Leaf)) {
        throw "Frozen artifact schema source is missing: $frozenArtifactSource"
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $frozenArtifactStaged)) | Out-Null
    if (Test-Path -LiteralPath $frozenArtifactStaged -PathType Leaf) {
        if ((Get-Sha256 $frozenArtifactStaged) -ne (Get-Sha256 $frozenArtifactSource)) {
            throw 'Packed frozen artifact schema drifted from the immutable source.'
        }
    }
    else {
        [IO.File]::Copy($frozenArtifactSource, $frozenArtifactStaged, $false)
    }
    if ((Get-Sha256 $frozenArtifactStaged) -ne (Get-Sha256 $frozenArtifactSource)) {
        throw 'Packed frozen artifact schema hash verification failed.'
    }
    [IO.Directory]::CreateDirectory($stagingAuthorityDirectory) | Out-Null

    $releaseExporter = Join-Path $stagingReleaseRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
    [IO.File]::Copy($RemNoteExporterPath, $releaseExporter, $true)
    if ((Get-Sha256 $releaseExporter) -ne $ExpectedRemNoteExporterSha256) {
        throw 'Pinned RemNote exporter hash does not match the source candidate.'
    }
    if ($ankiConfigured) {
        $releaseAnkiDirectory = Join-Path $stagingReleaseRoot 'workers\anki'
        Copy-DirectoryReplace (Split-Path -Parent $AnkiExporterPath) $releaseAnkiDirectory
        $releaseAnkiExporter = Join-Path $releaseAnkiDirectory 'KnowledgeClipper.AnkiExporter.Worker.exe'
        if ((Get-Item -LiteralPath $releaseAnkiExporter).Length -ne $ExpectedAnkiExporterBytes -or
            (Get-Sha256 $releaseAnkiExporter) -ne $ExpectedAnkiExporterSha256) {
            throw 'Pinned Anki exporter does not match the source candidate.'
        }
    }

    $registry = Get-Content -LiteralPath (Join-Path $BaseAuthorityDirectory 'worker-registry.v1.json') -Raw | ConvertFrom-Json
    $registry.registryVersion = $NewRegistryVersion
    if ($ankiConfigured) {
        $ankiRegistryEntry = @($registry.workers | Where-Object workerId -eq 'export.anki')
        if ($ankiRegistryEntry.Count -ne 1) { throw 'Authority registry must contain exactly one export.anki entry.' }
        $ankiRegistryEntry[0].stages = @($ankiDescribe.stages)
        $ankiRegistryEntry[0].inputKinds = @($ankiDescribe.inputKinds)
        $ankiRegistryEntry[0].outputKinds = @($ankiDescribe.outputKinds)
    }
    $registryPath = Join-Path $stagingAuthorityDirectory 'worker-registry.v1.json'
    Write-Json $registryPath $registry

    $tree = Get-ReleaseTree $stagingReleaseRoot
    $baseDependency = Get-Content -LiteralPath (Join-Path $BaseAuthorityDirectory 'dependency-manifest.v1.json') -Raw | ConvertFrom-Json
    $keyFiles = @(@($baseDependency.keyFiles) | ForEach-Object {
        $key = $_
        $path = Join-Path $stagingReleaseRoot ($key.path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Key release file is missing: $($key.path)" }
        [ordered]@{ path = $key.path; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 $path }
    })
    $keyFiles += [ordered]@{ path = $frozenArtifactRelative.Replace('\\', '/'); bytes = (Get-Item -LiteralPath $frozenArtifactStaged).Length; sha256 = Get-Sha256 $frozenArtifactStaged }
    $dependency = [ordered]@{
        schemaVersion = 'knowledge-clipper-release-dependency-manifest.v1'
        releaseId = $releaseId
        releaseRoot = "releases/$releaseId/win-x64"
        files = $tree.files
        bytes = $tree.bytes
        treeHashAlgorithm = $baseDependency.treeHashAlgorithm
        treeSha256 = $tree.treeSha256
        keyFiles = @($keyFiles)
        workerOperations = $baseDependency.workerOperations
        legacyEntrypointsExcluded = $true
    }
    $dependencyPath = Join-Path $stagingAuthorityDirectory 'dependency-manifest.v1.json'
    Write-Json $dependencyPath $dependency

    $closureText = (& python (Join-Path $PSScriptRoot 'validate_release_schema_closure.py') --release-root $stagingReleaseRoot 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "Portable schema closure validation failed: $closureText" }
    $closure = $closureText | ConvertFrom-Json -Depth 50
    if ($closure.status -ne 'passed' -or @($closure.failures).Count -ne 0) { throw 'Portable schema closure gate did not pass.' }
    Write-Json (Join-Path $stagingAuthorityDirectory 'portable-schema-closure-gate.v1.json') ([ordered]@{
        schemaVersion = 'knowledge-clipper-portable-schema-closure-gate.v1'
        status = 'passed'
        releaseRoot = $finalReleaseRoot
        registrySha256 = Get-Sha256 $registryPath
        dependencyManifestSha256 = Get-Sha256 $dependencyPath
        schemasChecked = [int]$closure.schemasChecked
        localRefsChecked = [int]$closure.localRefsChecked
        failures = @()
        frozenArtifactSchemaPath = $frozenArtifactRelative.Replace('\\', '/')
        frozenArtifactSchemaSha256 = Get-Sha256 $frozenArtifactStaged
        frozenArtifactSchemaPackedUnchanged = $true
    })

    # Authority promotion may update a worker, but may never silently roll one
    # back.  Compare the staged executable's actual describe identity and hash
    # with the immediately preceding authority snapshot.  Same version requires
    # byte identity; a changed payload must carry a strictly newer version.
    $monotonicWorkers = @()
    foreach ($worker in $registry.workers) {
        $workerId = [string]$worker.workerId
        $entrypoint = Join-Path $stagingReleaseRoot ([string]$worker.entrypoint.path).Replace('/', '\\')
        $previousDescribePath = Join-Path $BaseAuthorityDirectory ("describe\\$workerId.describe.json")
        if (-not (Test-Path -LiteralPath $entrypoint -PathType Leaf) -or -not (Test-Path -LiteralPath $previousDescribePath -PathType Leaf)) {
            throw "Worker monotonicity input missing for $workerId"
        }
        $previous = Get-Content -LiteralPath $previousDescribePath -Raw | ConvertFrom-Json -Depth 30
        $describeText = (& $entrypoint describe --json 2>&1) -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) { throw "Worker describe failed during monotonicity gate: $workerId" }
        $current = $describeText | ConvertFrom-Json -Depth 30
        $currentHash = Get-Sha256 $entrypoint
        $previousHash = $null
        if ($previous.workerVersion -eq $current.workerVersion) {
            # The preceding authority describes are paired with the release
            # checkpoint; read its recorded worker SHA as the binding evidence.
            $previousWorker = (Get-Content -LiteralPath (Join-Path $BaseAuthorityDirectory 'checkpoint.json') -Raw | ConvertFrom-Json -Depth 80).workers | Where-Object { $_.workerId -eq $workerId } | Select-Object -First 1
            $previousHash = if ($null -ne $previousWorker) { [string]$previousWorker.sha256 } else { $null }
        }
        if ($current.workerId -ne $workerId -or [version]$current.workerVersion -lt [version]$previous.workerVersion -or
            ($current.workerVersion -eq $previous.workerVersion -and ([string]::IsNullOrWhiteSpace($previousHash) -or $currentHash -ne $previousHash))) {
            throw "Worker monotonicity gate rejected $workerId (previous=$($previous.workerVersion)/$previousHash current=$($current.workerVersion)/$currentHash)."
        }
        $monotonicWorkers += [ordered]@{ workerId=$workerId; previousVersion=$previous.workerVersion; previousSha256=$previousHash; currentVersion=$current.workerVersion; currentSha256=$currentHash; status='passed' }
    }
    Write-Json (Join-Path $stagingAuthorityDirectory 'worker-monotonicity-gate.v1.json') ([ordered]@{
        schemaVersion='knowledge-clipper-authority-worker-monotonicity-gate.v1';status='passed';baseRegistryVersion=(Get-Content -LiteralPath (Join-Path $BaseAuthorityDirectory 'worker-registry.v1.json') -Raw | ConvertFrom-Json -Depth 20).registryVersion;registryVersion=$NewRegistryVersion;workers=$monotonicWorkers
    })

    $orchestrator = Join-Path $stagingReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
    $validationOutput = & $orchestrator validate-registry --registry $registryPath --release-root $stagingReleaseRoot 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Runtime registry admission failed: $($validationOutput -join [Environment]::NewLine)" }
    $validation = ($validationOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($validation.status -ne 'valid' -or $validation.workers -ne 5 -or $validation.registryVersion -ne $NewRegistryVersion) {
        throw 'Runtime registry admission returned an unexpected result.'
    }

    [IO.Directory]::Move($stagingReleaseContainer, $finalReleaseContainer)
    [IO.Directory]::Move($stagingAuthorityDirectory, $finalAuthorityDirectory)
    [ordered]@{
        schemaVersion = 'knowledge-clipper-authority-release-build-receipt.v1'
        status = 'candidate-built'
        releaseId = $releaseId
        releaseRoot = $finalReleaseRoot
        authorityDirectory = $finalAuthorityDirectory
        registry = [ordered]@{ version = $NewRegistryVersion; path = (Join-Path $finalAuthorityDirectory 'worker-registry.v1.json'); sha256 = Get-Sha256 (Join-Path $finalAuthorityDirectory 'worker-registry.v1.json') }
        dependencyManifest = [ordered]@{ path = (Join-Path $finalAuthorityDirectory 'dependency-manifest.v1.json'); sha256 = Get-Sha256 (Join-Path $finalAuthorityDirectory 'dependency-manifest.v1.json') }
        release = $tree
        remNoteExporter = [ordered]@{ workerVersion = $ExpectedRemNoteExporterVersion; bytes = (Get-Item -LiteralPath (Join-Path $finalReleaseRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe')).Length; sha256 = $ExpectedRemNoteExporterSha256 }
        ankiExporter = if ($ankiConfigured) { [ordered]@{ workerVersion = $ExpectedAnkiExporterVersion; bytes = $ExpectedAnkiExporterBytes; sha256 = $ExpectedAnkiExporterSha256 } } else { $null }
        validation = [ordered]@{ status = $validation.status; workers = $validation.workers }
    } | ConvertTo-Json -Depth 16
}
finally {
    Remove-VerifiedStagingDirectory $stagingReleaseContainer $releaseParent
    Remove-VerifiedStagingDirectory $stagingAuthorityDirectory $authorityParent
}

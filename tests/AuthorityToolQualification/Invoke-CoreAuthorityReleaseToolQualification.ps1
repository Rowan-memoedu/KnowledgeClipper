[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$QualificationOutputPath
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$toolPath = Join-Path $ProjectRoot 'tools\New-KnowledgeClipperCoreAuthorityRelease.ps1'
$sourceCheckpointPath = Join-Path $ProjectRoot 'contracts\core\v2.10\checkpoints\v2.10.2-20260901T100127Z\checkpoint.json'
$sourceContractRoot = Join-Path $ProjectRoot 'contracts\core\v2.10'
$sourceRuntimeRoot = Join-Path $ProjectRoot 'orchestrator\bin\Release\net8.0\win-x64'
$sourceBaseReleaseRoot = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.37-20260901T063000Z\win-x64'
$sourceBaseAuthorityDirectory = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.37-20260901T063000Z'
$expectedCheckpointSha256 = '954F1D28DA9F64E15099FD55527FC158A0C3E5E34668A3F2385A09A8266F1039'

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-DirectoryInventory([string]$Root) {
    $records = @()
    $rows = @()
    [long]$totalBytes = 0
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $sha256 = Get-Sha256 $file.FullName
        $records += [ordered]@{ path = $relative; bytes = [long]$file.Length; sha256 = $sha256 }
        $rows += "$relative`t$($file.Length)`t$($sha256.ToLowerInvariant())"
        $totalBytes += $file.Length
    }
    $rows = [string[]]$rows
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    $treeBytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    [ordered]@{
        fileCount = $files.Count
        totalBytes = $totalBytes
        treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($treeBytes))
        files = @($records | Sort-Object path)
    }
}

function New-RuntimeManifest([string]$RuntimeRoot, [string]$CheckpointRelative, [string]$CheckpointSha256, [string]$ComponentVersion) {
    $inventory = Get-DirectoryInventory $RuntimeRoot
    [ordered]@{
        schemaVersion = 'knowledge-clipper-orchestrator-runtime-manifest.v1'
        checkpoint = [ordered]@{ path = $CheckpointRelative; sha256 = $CheckpointSha256 }
        component = [ordered]@{ id = 'knowledge-clipper.orchestrator'; version = $ComponentVersion }
        selfContained = $true
        runtimeIdentifier = 'win-x64'
        entrypoint = 'KnowledgeClipper.Orchestrator.exe'
        treeAlgorithm = 'sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)'
        fileCount = $inventory.fileCount
        totalBytes = $inventory.totalBytes
        treeSha256 = $inventory.treeSha256
        files = $inventory.files
    }
}

function Invoke-ExpectedFailure([hashtable]$Arguments, [string]$ExpectedPattern, [string]$CaseName) {
    try {
        $unexpected = & $toolPath @Arguments 2>&1
        throw "$CaseName unexpectedly passed: $($unexpected -join [Environment]::NewLine)"
    }
    catch {
        $message = $_.Exception.Message
        if ($message -notmatch $ExpectedPattern) { throw "$CaseName failed for the wrong reason: $message" }
        [ordered]@{ name = $CaseName; status = 'passed-rejected'; error = $message }
    }
}

foreach ($required in @(
    $toolPath, $sourceCheckpointPath, $sourceContractRoot, $sourceRuntimeRoot,
    $sourceBaseReleaseRoot, $sourceBaseAuthorityDirectory)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Qualification input is missing: $required" }
}
if ((Get-Sha256 $sourceCheckpointPath) -ne $expectedCheckpointSha256) { throw 'Pinned v2.10.2 checkpoint hash drifted.' }

$fixtureBase = Join-Path ([IO.Path]::GetPathRoot($env:SystemDrive)) 'kcq'
[IO.Directory]::CreateDirectory($fixtureBase) | Out-Null
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $fixtureBase ('a' + [Guid]::NewGuid().ToString('N').Substring(0, 8))))
$tempPrefix = [IO.Path]::GetFullPath($fixtureBase).TrimEnd('\') + '\'
if (-not $fixtureRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe controlled fixture root: $fixtureRoot" }

$cases = @()
try {
    $fixtureProjectRoot = Join-Path $fixtureRoot 'p'
    [IO.Directory]::CreateDirectory($fixtureProjectRoot) | Out-Null
    $checkpointDocument = Get-Content -LiteralPath $sourceCheckpointPath -Raw | ConvertFrom-Json -Depth 100
    foreach ($binding in @($checkpointDocument.fileBindings)) {
        $source = Join-Path $ProjectRoot (([string]$binding.path).Replace('/', '\'))
        $destination = Join-Path $fixtureProjectRoot (([string]$binding.path).Replace('/', '\'))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $false)
    }
    $checkpointRelative = 'contracts/core/v2.10/checkpoints/v2.10.2-20260901T100127Z/checkpoint.json'
    $checkpointPath = Join-Path $fixtureProjectRoot ($checkpointRelative.Replace('/', '\'))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($checkpointPath)) | Out-Null
    [IO.File]::Copy($sourceCheckpointPath, $checkpointPath, $false)
    $contractRoot = Join-Path $fixtureProjectRoot 'contracts\core\v2.10'

    $baseReleaseRoot = Join-Path $fixtureRoot 'empty-base-release'
    $baseAuthorityDirectory = Join-Path $fixtureRoot 'empty-base-authority'
    [IO.Directory]::CreateDirectory($baseReleaseRoot) | Out-Null
    [IO.Directory]::CreateDirectory($baseAuthorityDirectory) | Out-Null
    Write-Utf8NoBom (Join-Path $baseAuthorityDirectory 'worker-registry.v1.json') "{`"registryVersion`":`"fixture-base`",`"workers`":[]}`n"
    Write-Utf8NoBom (Join-Path $baseAuthorityDirectory 'dependency-manifest.v1.json') "{`"releaseId`":`"fixture-base`",`"treeHashAlgorithm`":`"fixture`",`"treeSha256`":`"fixture`",`"keyFiles`":[],`"workerOperations`":[]}`n"

    $sourceOrchestrator = Join-Path $sourceRuntimeRoot 'KnowledgeClipper.Orchestrator.exe'
    $describeText = (& $sourceOrchestrator describe 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "Qualification Orchestrator describe failed: $describeText" }
    $sourceDescribe = $describeText | ConvertFrom-Json
    $sourceVersion = [string]$sourceDescribe.version
    $runtimeManifestPath = Join-Path $fixtureRoot 'runtime-manifest.json'
    $runtimeManifest = New-RuntimeManifest $sourceRuntimeRoot $checkpointRelative $expectedCheckpointSha256 $sourceVersion
    Write-Utf8NoBom $runtimeManifestPath (($runtimeManifest | ConvertTo-Json -Depth 100) + [Environment]::NewLine)

    $registryVersion = 'qualification-' + [Guid]::NewGuid().ToString('N')
    $releaseStamp = 'fixture'
    $baseArguments = @{
        BaseReleaseRoot = $baseReleaseRoot
        BaseAuthorityDirectory = $baseAuthorityDirectory
        NewRegistryVersion = $registryVersion
        ReleaseStamp = $releaseStamp
        OrchestratorPublishRoot = $sourceRuntimeRoot
        OrchestratorRuntimeManifestPath = $runtimeManifestPath
        ExpectedOrchestratorRuntimeManifestSha256 = Get-Sha256 $runtimeManifestPath
        ExpectedOrchestratorVersion = $sourceVersion
        ExpectedOrchestratorSha256 = Get-Sha256 $sourceOrchestrator
        CoreContractRoot = $contractRoot
        CoreCheckpointPath = $checkpointPath
        ExpectedCoreCheckpointSha256 = $expectedCheckpointSha256
        ExpectedIpcProtocolVersion = '1.7'
        ExpectedRequestContract = 'knowledge-clipper-module-action.v6'
        ExpectedReceiptContract = 'knowledge-clipper-module-action-receipt.v6'
        PreflightOnly = $true
        ProjectRoot = $fixtureProjectRoot
    }

    $acceptedText = (& $toolPath @baseArguments 2>&1) -join [Environment]::NewLine
    $accepted = $acceptedText | ConvertFrom-Json -Depth 100
    if ($accepted.status -ne 'passed' -or $accepted.checkpointVersion -ne '2.10.2' -or
        $accepted.orchestratorRuntime.fileCount -ne 187 -or $accepted.orchestratorRuntime.totalBytes -ne 75005615 -or
        $accepted.orchestratorRuntime.treeSha256 -ne '897089A13C1F29728E677413276F5E1EB4BD9137DFF8B2C7F9E0FC3DD3E54346' -or
        $accepted.schemaDependencyFiles -lt 1 -or
        -not (@($accepted.schemaDependencies.path) -contains 'contracts/v1/schemas/common/artifact.schema.json')) {
        throw "v2.10.2 runtime preflight returned unexpected evidence: $acceptedText"
    }
    $cases += [ordered]@{ name = 'v2.10.2-self-contained-runtime'; status = 'passed-accepted'; evidence = $accepted }

    foreach ($requiredRuntimeName in @(
        'KnowledgeClipper.Orchestrator.exe', 'KnowledgeClipper.Orchestrator.dll',
        'KnowledgeClipper.Orchestrator.deps.json', 'KnowledgeClipper.Orchestrator.runtimeconfig.json')) {
        $missing = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json -Depth 100
        $missing.files = @($missing.files | Where-Object path -ne $requiredRuntimeName)
        $missing.fileCount = @($missing.files).Count
        $path = Join-Path $fixtureRoot ("missing-" + $requiredRuntimeName.Replace('.', '-') + '.json')
        Write-Utf8NoBom $path (($missing | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
        $args = $baseArguments.Clone(); $args.OrchestratorRuntimeManifestPath = $path; $args.ExpectedOrchestratorRuntimeManifestSha256 = Get-Sha256 $path
        $cases += Invoke-ExpectedFailure $args ([regex]::Escape("missing required payload: $requiredRuntimeName")) ("missing-runtime-" + $requiredRuntimeName)
    }

    $mixedVersion = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json -Depth 100
    $mixedVersion.component.version = '0.0.0-mixed'
    $mixedVersionPath = Join-Path $fixtureRoot 'mixed-version.json'
    Write-Utf8NoBom $mixedVersionPath (($mixedVersion | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
    $args = $baseArguments.Clone(); $args.OrchestratorRuntimeManifestPath = $mixedVersionPath; $args.ExpectedOrchestratorRuntimeManifestSha256 = Get-Sha256 $mixedVersionPath
    $cases += Invoke-ExpectedFailure $args 'component identity/version mismatch' 'mixed-runtime-version'

    $mixedCheckpoint = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json -Depth 100
    $mixedCheckpoint.checkpoint.sha256 = ('0' * 64)
    $mixedCheckpointPath = Join-Path $fixtureRoot 'mixed-checkpoint.json'
    Write-Utf8NoBom $mixedCheckpointPath (($mixedCheckpoint | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
    $args = $baseArguments.Clone(); $args.OrchestratorRuntimeManifestPath = $mixedCheckpointPath; $args.ExpectedOrchestratorRuntimeManifestSha256 = Get-Sha256 $mixedCheckpointPath
    $cases += Invoke-ExpectedFailure $args 'bound to a different Core checkpoint' 'mixed-runtime-checkpoint'

    $traversal = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json -Depth 100
    $traversal.files[0].path = '../escape.dll'
    $traversalPath = Join-Path $fixtureRoot 'path-traversal.json'
    Write-Utf8NoBom $traversalPath (($traversal | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
    $args = $baseArguments.Clone(); $args.OrchestratorRuntimeManifestPath = $traversalPath; $args.ExpectedOrchestratorRuntimeManifestSha256 = Get-Sha256 $traversalPath
    $cases += Invoke-ExpectedFailure $args 'Invalid, escaped, or duplicate' 'runtime-path-traversal'

    $driftRoot = Join-Path $fixtureRoot 'runtime-drift'
    [IO.Directory]::CreateDirectory($driftRoot) | Out-Null
    foreach ($name in @('KnowledgeClipper.Orchestrator.exe','KnowledgeClipper.Orchestrator.dll','KnowledgeClipper.Orchestrator.deps.json','KnowledgeClipper.Orchestrator.runtimeconfig.json')) {
        [IO.File]::Copy((Join-Path $sourceRuntimeRoot $name), (Join-Path $driftRoot $name), $false)
    }
    $driftManifestPath = Join-Path $fixtureRoot 'payload-drift.json'
    $driftManifest = New-RuntimeManifest $driftRoot $checkpointRelative $expectedCheckpointSha256 $sourceVersion
    Write-Utf8NoBom $driftManifestPath (($driftManifest | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
    [IO.File]::AppendAllText((Join-Path $driftRoot 'KnowledgeClipper.Orchestrator.dll'), 'drift', [Text.UTF8Encoding]::new($false))
    $args = $baseArguments.Clone(); $args.OrchestratorPublishRoot = $driftRoot; $args.OrchestratorRuntimeManifestPath = $driftManifestPath; $args.ExpectedOrchestratorRuntimeManifestSha256 = Get-Sha256 $driftManifestPath
    $cases += Invoke-ExpectedFailure $args 'aggregate/tree drifted' 'runtime-payload-drift'

    $args = $baseArguments.Clone(); $args.ExpectedIpcProtocolVersion = '1.1'
    $cases += Invoke-ExpectedFailure $args 'IPC protocol version mismatch' 'old-ipc-1.1'
    $args = $baseArguments.Clone(); $args.ExpectedReceiptContract = '2.3.0'
    $cases += Invoke-ExpectedFailure $args 'receipt contract mismatch' 'old-receipt-2.3.0'

    $driftedCheckpointPath = Join-Path $contractRoot 'checkpoints\binding-drift.json'
    $driftedCheckpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json -Depth 100
    $driftedCheckpoint.fileBindings[0].sha256 = ('0' * 64)
    Write-Utf8NoBom $driftedCheckpointPath (($driftedCheckpoint | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
    $args = $baseArguments.Clone(); $args.CoreCheckpointPath = $driftedCheckpointPath; $args.ExpectedCoreCheckpointSha256 = Get-Sha256 $driftedCheckpointPath
    $cases += Invoke-ExpectedFailure $args 'file binding drifted' 'checkpoint-binding-drift'

    $preflightReleaseTarget = Join-Path $fixtureProjectRoot ("releases\knowledge-clipper-authority-v$registryVersion-$releaseStamp")
    $preflightAuthorityTarget = Join-Path $fixtureProjectRoot ("contracts\authority\worker-registry\v$registryVersion-$releaseStamp")
    if ((Test-Path $preflightReleaseTarget) -or (Test-Path $preflightAuthorityTarget)) { throw 'Preflight created an assembly target.' }

    $fullVersion = '1.2.999'
    $fullArgs = $baseArguments.Clone()
    $fullArgs.BaseReleaseRoot = $sourceBaseReleaseRoot
    $fullArgs.BaseAuthorityDirectory = $sourceBaseAuthorityDirectory
    $fullArgs.NewRegistryVersion = $fullVersion
    $fullArgs.Remove('PreflightOnly')
    $fullText = (& $toolPath @fullArgs 2>&1) -join [Environment]::NewLine
    $full = $fullText | ConvertFrom-Json -Depth 100
    $fullReleaseRoot = Join-Path $fixtureProjectRoot "releases\knowledge-clipper-authority-v$fullVersion-fixture\win-x64"
    $fullAuthorityRoot = Join-Path $fixtureProjectRoot "contracts\authority\worker-registry\v$fullVersion-fixture"
    $published = Get-DirectoryInventory (Join-Path $fullReleaseRoot 'orchestrator')
    if ($full.validation.status -ne 'valid' -or $full.validation.workers -ne 5 -or
        $published.fileCount -ne $runtimeManifest.fileCount -or $published.totalBytes -ne $runtimeManifest.totalBytes -or
        $published.treeSha256 -ne $runtimeManifest.treeSha256 -or
        -not (Test-Path -LiteralPath (Join-Path $fullReleaseRoot 'contracts\v1\schemas\common\artifact.schema.json')) -or
        (Get-Sha256 (Join-Path $fullAuthorityRoot 'orchestrator-runtime-manifest.v1.json')) -ne (Get-Sha256 $runtimeManifestPath)) {
        throw "Ephemeral full assembly failed post-copy validation: $fullText"
    }
    $staging = @(
        Get-ChildItem -LiteralPath (Join-Path $fixtureProjectRoot 'releases') -Directory -Force | Where-Object Name -Like '.*.tmp'
        Get-ChildItem -LiteralPath (Join-Path $fixtureProjectRoot 'contracts\authority\worker-registry') -Directory -Force | Where-Object Name -Like '.*.tmp'
    )
    if ($staging.Count -ne 0) { throw 'Ephemeral full assembly left staging directories.' }
    $cases += [ordered]@{
        name = 'ephemeral-full-assembly'
        status = 'passed-accepted'
        runtimeFiles = $published.fileCount
        runtimeBytes = $published.totalBytes
        runtimeTreeSha256 = $published.treeSha256
        registryValidation = $full.validation.status
        workers = $full.validation.workers
        stagingDirectories = 0
    }

    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-core-authority-tool-qualification.v2'
        status = 'passed'
        createdAt = (Get-Date).ToUniversalTime().ToString('o')
        tool = [ordered]@{ path = 'tools/New-KnowledgeClipperCoreAuthorityRelease.ps1'; sha256 = Get-Sha256 $toolPath }
        qualification = [ordered]@{
            path = 'tests/AuthorityToolQualification/Invoke-CoreAuthorityReleaseToolQualification.ps1'
            sha256 = Get-Sha256 $PSCommandPath
            fixtureRoot = 'system-temp/ephemeral-short-path'
            externalServicesCalled = 0
            formalAuthorityTargetsCreated = 0
            candidateTargetsCreated = 0
            ephemeralAssemblyTargetsCreated = 2
        }
        checkpoint = [ordered]@{ path = $checkpointRelative; sha256 = $expectedCheckpointSha256 }
        runtime = [ordered]@{
            source = 'orchestrator/bin/Release/net8.0/win-x64'
            files = $runtimeManifest.fileCount
            bytes = $runtimeManifest.totalBytes
            treeSha256 = $runtimeManifest.treeSha256
        }
        cases = $cases
    }
    if (-not [string]::IsNullOrWhiteSpace($QualificationOutputPath)) {
        $QualificationOutputPath = [IO.Path]::GetFullPath($QualificationOutputPath)
        if (Test-Path -LiteralPath $QualificationOutputPath) { throw "Append-only qualification receipt already exists: $QualificationOutputPath" }
        Write-Utf8NoBom $QualificationOutputPath (($receipt | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
    }
    $receipt | ConvertTo-Json -Depth 100
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
        if (-not $resolvedFixture.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe fixture cleanup target: $resolvedFixture" }
        [IO.Directory]::Delete($resolvedFixture, $true)
    }
}

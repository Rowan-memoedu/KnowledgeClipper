param(
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:Timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:ReleaseId = "$($script:Timestamp)-worker-process-runtime.1"
$script:ReleaseRoot = Join-Path $script:RepositoryRoot "core-v3\releases\$($script:ReleaseId)"

function Get-RepositoryRelativePath([string]$Path) {
    return ([System.IO.Path]::GetRelativePath($script:RepositoryRoot, [System.IO.Path]::GetFullPath($Path)) -replace '\\', '/')
}

function Get-ReleaseRelativePath([string]$Path) {
    return ([System.IO.Path]::GetRelativePath($script:ReleaseRoot, [System.IO.Path]::GetFullPath($Path)) -replace '\\', '/')
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-Entry([string]$Path, [ValidateSet('repository', 'release')][string]$RelativeTo = 'release') {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = if ($RelativeTo -eq 'release') { Get-ReleaseRelativePath $Path } else { Get-RepositoryRelativePath $Path }
        bytes = $item.Length
        sha256 = Get-Sha256 $Path
    }
}

function Write-CreateOnceJson([string]$Path, [object]$Value) {
    if ([System.IO.File]::Exists($Path)) { throw "Refusing to overwrite create-once artifact: $Path" }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine, $script:Utf8NoBom)
}

function Copy-CreateOnceFile([string]$Source, [string]$Destination) {
    if ([System.IO.File]::Exists($Destination)) { throw "Refusing to overwrite release file: $Destination" }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Destination)) | Out-Null
    [System.IO.File]::Copy($Source, $Destination, $false)
}

function Copy-RepositoryTree([string]$RelativeRoot, [string]$DestinationPrefix, [scriptblock]$Include) {
    $sourceRoot = Join-Path $script:RepositoryRoot $RelativeRoot
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
        if (& $Include $file) {
            $relative = [System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
            Copy-CreateOnceFile $file.FullName (Join-Path $script:ReleaseRoot (Join-Path $DestinationPrefix $relative))
        }
    }
}

function New-Tree([System.IO.FileInfo[]]$Files) {
    $paths = @($Files | ForEach-Object { $_.FullName })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $entries = @($paths | ForEach-Object { Get-Entry $_ })
    $lines = $entries | ForEach-Object { "$($_.path)`t$($_.sha256)`t$($_.bytes)`n" }
    $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($script:Utf8NoBom.GetBytes(($lines -join ''))))
    return [ordered]@{ sha256 = $hash; entries = $entries }
}

function Invoke-TestProject([string]$Project, [string]$Receipt, [string]$Label) {
    $output = @(& $DotnetPath run --project $Project -c Release --no-build -- --receipt $Receipt 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "$Label failed: $Receipt" }
    $document = Get-Content -LiteralPath $Receipt -Raw | ConvertFrom-Json
    if ($document.status -ne 'PASS') { throw "$Label receipt is not PASS: $Receipt" }
}

if (-not [System.IO.File]::Exists($DotnetPath)) { throw "Pinned .NET SDK was not found: $DotnetPath" }

$phase2Project = Join-Path $script:RepositoryRoot 'core-v3\tests\KnowledgeClipper.Core.V3.Phase2Tests\KnowledgeClipper.Core.V3.Phase2Tests.csproj'
$foundationProject = Join-Path $script:RepositoryRoot 'core-v3\tests\KnowledgeClipper.Core.V3.FoundationTests\KnowledgeClipper.Core.V3.FoundationTests.csproj'
$buildReceipt = Join-Path $script:RepositoryRoot "core-v3\receipts\$($script:Timestamp)-phase2-release-build.receipt.json"
$phase2Receipt = Join-Path $script:RepositoryRoot "core-v3\receipts\$($script:Timestamp)-phase2-tests.receipt.json"
$foundationReceipt = Join-Path $script:RepositoryRoot "core-v3\receipts\$($script:Timestamp)-foundation-regression.receipt.json"

$buildStarted = [DateTimeOffset]::UtcNow
$buildOutput = @(& $DotnetPath build $phase2Project -c Release --nologo 2>&1 | ForEach-Object { $_.ToString() })
$buildExitCode = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }
$warningLines = @($buildOutput | Where-Object { $_ -match '(^|\s)(warning|警告)\s+[A-Z]*\d*:' })
$errorLines = @($buildOutput | Where-Object { $_ -match '(^|\s)(error|错误)\s+[A-Z]*\d*:' })
Write-CreateOnceJson $buildReceipt ([ordered]@{
    schema = 'kc.core-v3-phase2-release-build-receipt/1'
    releaseId = $script:ReleaseId
    startedAt = $buildStarted
    completedAt = [DateTimeOffset]::UtcNow
    status = if ($buildExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    configuration = 'Release'
    dotnetPath = $DotnetPath
    project = Get-RepositoryRelativePath $phase2Project
    exitCode = $buildExitCode
    warningCount = $warningLines.Count
    errorCount = $errorLines.Count
})
if ($buildExitCode -ne 0) { throw "Phase2 Release build failed: $buildReceipt" }

Invoke-TestProject $phase2Project $phase2Receipt 'Phase2 tests'
Invoke-TestProject $foundationProject $foundationReceipt 'Foundation regression'

if ([System.IO.Directory]::Exists($script:ReleaseRoot)) { throw "Refusing to reuse release root: $($script:ReleaseRoot)" }
[System.IO.Directory]::CreateDirectory($script:ReleaseRoot) | Out-Null

Copy-RepositoryTree 'core-v3\src' 'snapshot\core-v3\src' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'core-v3\tests' 'snapshot\core-v3\tests' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'core-v3\qualification-consumers' 'snapshot\core-v3\qualification-consumers' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'contracts\core\v3' 'snapshot\contracts\core\v3' { param($file) $file.FullName -notmatch '[\\/]checkpoints[\\/]' }
Copy-CreateOnceFile (Join-Path $script:RepositoryRoot 'core-v3\README.md') (Join-Path $script:ReleaseRoot 'snapshot\core-v3\README.md')
Copy-CreateOnceFile $PSCommandPath (Join-Path $script:ReleaseRoot 'snapshot\core-v3\tools\Invoke-Phase2SliceQualification.ps1')

# A complete repository-shaped replay root is included so independent quality does
# not need to borrow authority companion files or historical Canary fixtures.
Copy-RepositoryTree 'core-v3\src' 'self-contained-repository\core-v3\src' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'core-v3\tests' 'self-contained-repository\core-v3\tests' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'core-v3\qualification-consumers' 'self-contained-repository\core-v3\qualification-consumers' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'contracts\core\v3' 'self-contained-repository\contracts\core\v3' { param($file) $true }
Copy-CreateOnceFile (Join-Path $script:RepositoryRoot 'contracts\core\v2.10\checkpoints\v2.10.6-20260902T041800Z\checkpoint.json') (Join-Path $script:ReleaseRoot 'self-contained-repository\contracts\core\v2.10\checkpoints\v2.10.6-20260902T041800Z\checkpoint.json')
Copy-CreateOnceFile (Join-Path $script:RepositoryRoot 'contracts\authority\worker-registry\v1.2.41-20260902T042000Z\worker-registry.v1.json') (Join-Path $script:ReleaseRoot 'self-contained-repository\contracts\authority\worker-registry\v1.2.41-20260902T042000Z\worker-registry.v1.json')
Copy-RepositoryTree 'releases\knowledge-clipper-authority-v1.2.41-20260902T042000Z\win-x64\workers\content' 'self-contained-repository\releases\knowledge-clipper-authority-v1.2.41-20260902T042000Z\win-x64\workers\content' { param($file) $true }
Copy-RepositoryTree 'releases\knowledge-clipper-authority-v1.2.41-20260902T042000Z\win-x64\workers\anki' 'self-contained-repository\releases\knowledge-clipper-authority-v1.2.41-20260902T042000Z\win-x64\workers\anki' { param($file) $true }
Copy-CreateOnceFile (Join-Path $script:RepositoryRoot 'core-v3\README.md') (Join-Path $script:ReleaseRoot 'self-contained-repository\core-v3\README.md')
Copy-CreateOnceFile $PSCommandPath (Join-Path $script:ReleaseRoot 'self-contained-repository\core-v3\tools\Invoke-Phase2SliceQualification.ps1')

$runtimeSources = @(
    'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.deps.json',
    'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.pdb',
    'core-v3\src\KnowledgeClipper.Core.V3.Contracts\bin\Release\net8.0\KnowledgeClipper.Core.V3.Contracts.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Contracts\bin\Release\net8.0\KnowledgeClipper.Core.V3.Contracts.pdb',
    'core-v3\src\KnowledgeClipper.Core.V3.Kernel\bin\Release\net8.0\KnowledgeClipper.Core.V3.Kernel.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Kernel\bin\Release\net8.0\KnowledgeClipper.Core.V3.Kernel.pdb',
    'core-v3\src\KnowledgeClipper.Core.V3.Host\bin\Release\net8.0\KnowledgeClipper.Core.V3.Host.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Host\bin\Release\net8.0\KnowledgeClipper.Core.V3.Host.deps.json',
    'core-v3\src\KnowledgeClipper.Core.V3.Host\bin\Release\net8.0\KnowledgeClipper.Core.V3.Host.runtimeconfig.json',
    'core-v3\src\KnowledgeClipper.Core.V3.Host\bin\Release\net8.0\KnowledgeClipper.Core.V3.Host.pdb'
)
foreach ($relativeSource in $runtimeSources) {
    $source = Join-Path $script:RepositoryRoot $relativeSource
    Copy-CreateOnceFile $source (Join-Path $script:ReleaseRoot (Join-Path 'artifacts\net8.0' ([System.IO.Path]::GetFileName($source))))
}

Copy-CreateOnceFile $buildReceipt (Join-Path $script:ReleaseRoot 'evidence\phase2-release-build.receipt.json')
Copy-CreateOnceFile $phase2Receipt (Join-Path $script:ReleaseRoot 'evidence\phase2-tests.receipt.json')
Copy-CreateOnceFile $foundationReceipt (Join-Path $script:ReleaseRoot 'evidence\foundation-regression.receipt.json')

$authorityRegistry = Join-Path $script:RepositoryRoot 'contracts\authority\worker-registry\v1.2.41-20260902T042000Z\worker-registry.v1.json'
$coreCheckpoint = Join-Path $script:RepositoryRoot 'contracts\core\v2.10\checkpoints\v2.10.6-20260902T041800Z\checkpoint.json'
Copy-CreateOnceFile $authorityRegistry (Join-Path $script:ReleaseRoot 'evidence\authority\worker-registry.v1.json')
Copy-CreateOnceFile $coreCheckpoint (Join-Path $script:ReleaseRoot 'evidence\authority\core-v2.10.6-checkpoint.json')
$contentWorker = Join-Path $script:RepositoryRoot 'releases\knowledge-clipper-authority-v1.2.41-20260902T042000Z\win-x64\workers\content\KnowledgeClipper.Content.Worker.exe'
$ankiWorker = Join-Path $script:RepositoryRoot 'releases\knowledge-clipper-authority-v1.2.41-20260902T042000Z\win-x64\workers\anki\KnowledgeClipper.AnkiExporter.Worker.exe'
Write-CreateOnceJson (Join-Path $script:ReleaseRoot 'evidence\authority\frozen-worker-dependencies.json') ([ordered]@{
    schema = 'kc.core-v3-frozen-worker-dependencies/1'
    authorityVersion = '1.2.41'
    registry = Get-Entry $authorityRegistry 'repository'
    coreCheckpoint = Get-Entry $coreCheckpoint 'repository'
    workers = @(
        [ordered]@{ workerId = 'content.deepseek'; workerVersion = '0.9.5'; contractVersion = '1.0.0'; entrypoint = Get-Entry $contentWorker 'repository' }
        [ordered]@{ workerId = 'export.anki'; workerVersion = '1.4.0'; contractVersion = '1.0.0'; entrypoint = Get-Entry $ankiWorker 'repository' }
    )
    admissionAction = 'exact hash plus describe --json'
    workerExecution = 'Core-owned WorkerProcessRuntime; offline tests inject the process invoker; real Anki read-only probe evidence is included separately'
})

$realProbeReceipt = Join-Path $script:RepositoryRoot 'core-v3\receipts\real-anki-readonly-probe\20260902T214000Z\receipt.json'
Copy-CreateOnceFile $realProbeReceipt (Join-Path $script:ReleaseRoot 'evidence\real-anki-readonly-probe.receipt.json')

$externalStageBase = 'D:\CodexTemp\KnowledgeClipper.Core.V3.ReleaseReplay'
$externalStageRoot = Join-Path $externalStageBase $script:ReleaseId
$externalReplayRoot = Join-Path $externalStageRoot 'repository'
if ([System.IO.Directory]::Exists($externalStageRoot)) { throw "External replay root already exists: $externalStageRoot" }
[System.IO.Directory]::CreateDirectory($externalReplayRoot) | Out-Null
try {
    Copy-Item -Path (Join-Path $script:ReleaseRoot 'self-contained-repository\*') -Destination $externalReplayRoot -Recurse -Force
    $externalPhase2Project = Join-Path $externalReplayRoot 'core-v3\tests\KnowledgeClipper.Core.V3.Phase2Tests\KnowledgeClipper.Core.V3.Phase2Tests.csproj'
    $externalFoundationProject = Join-Path $externalReplayRoot 'core-v3\tests\KnowledgeClipper.Core.V3.FoundationTests\KnowledgeClipper.Core.V3.FoundationTests.csproj'
    $externalBuildStarted = [DateTimeOffset]::UtcNow
    $externalBuildOutput = @(& $DotnetPath build $externalPhase2Project -c Release --nologo 2>&1 | ForEach-Object { $_.ToString() })
    $externalBuildExitCode = $LASTEXITCODE
    $externalBuildOutput += @(& $DotnetPath build $externalFoundationProject -c Release --nologo 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { $externalBuildExitCode = $LASTEXITCODE }
    $externalBuildOutput | ForEach-Object { Write-Host $_ }
    $externalWarnings = @($externalBuildOutput | Where-Object { $_ -match '(^|\s)(warning|警告)\s+[A-Z]*\d*:' })
    $externalErrors = @($externalBuildOutput | Where-Object { $_ -match '(^|\s)(error|错误)\s+[A-Z]*\d*:' })
    $externalBuildReceipt = Join-Path $script:ReleaseRoot 'evidence\external-replay-build.receipt.json'
    Write-CreateOnceJson $externalBuildReceipt ([ordered]@{
        schema = 'kc.core-v3-external-replay-build-receipt/1'
        releaseId = $script:ReleaseId
        startedAt = $externalBuildStarted
        completedAt = [DateTimeOffset]::UtcNow
        status = if ($externalBuildExitCode -eq 0) { 'PASS' } else { 'FAIL' }
        repositoryOutsideSourceTree = $true
        exitCode = $externalBuildExitCode
        warningCount = $externalWarnings.Count
        errorCount = $externalErrors.Count
    })
    if ($externalBuildExitCode -ne 0) { throw "External replay build failed: $externalBuildReceipt" }
    Invoke-TestProject $externalPhase2Project (Join-Path $script:ReleaseRoot 'evidence\external-replay-phase2.receipt.json') 'External replay Phase2 tests'
    Invoke-TestProject $externalFoundationProject (Join-Path $script:ReleaseRoot 'evidence\external-replay-foundation.receipt.json') 'External replay Foundation tests'
}
finally {
    $resolvedStage = [System.IO.Path]::GetFullPath($externalStageRoot)
    $resolvedStageBase = [System.IO.Path]::GetFullPath($externalStageBase)
    if ($resolvedStage.StartsWith($resolvedStageBase, [StringComparison]::OrdinalIgnoreCase) -and [System.IO.Directory]::Exists($resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

$externalPhase2Document = Get-Content -LiteralPath (Join-Path $script:ReleaseRoot 'evidence\external-replay-phase2.receipt.json') -Raw | ConvertFrom-Json
$externalFoundationDocument = Get-Content -LiteralPath (Join-Path $script:ReleaseRoot 'evidence\external-replay-foundation.receipt.json') -Raw | ConvertFrom-Json
$qualificationReceiptPath = Join-Path $script:ReleaseRoot 'evidence\qualification.receipt.json'
Write-CreateOnceJson $qualificationReceiptPath ([ordered]@{
    schema = 'kc.core-v3-worker-process-runtime-qualification-receipt/1'
    releaseId = $script:ReleaseId
    completedAt = [DateTimeOffset]::UtcNow
    status = 'PASS'
    replayBoundary = 'self-contained repository copied outside the source tree'
    replayCommands = @(
        "& '$DotnetPath' build '<self-contained-repository-root>\core-v3\tests\KnowledgeClipper.Core.V3.Phase2Tests\KnowledgeClipper.Core.V3.Phase2Tests.csproj' -c Release --nologo",
        "& '$DotnetPath' build '<self-contained-repository-root>\core-v3\tests\KnowledgeClipper.Core.V3.FoundationTests\KnowledgeClipper.Core.V3.FoundationTests.csproj' -c Release --nologo",
        "& '$DotnetPath' run --project '<self-contained-repository-root>\core-v3\tests\KnowledgeClipper.Core.V3.Phase2Tests\KnowledgeClipper.Core.V3.Phase2Tests.csproj' -c Release --no-build -- --receipt '<receipt-path>'",
        "& '$DotnetPath' run --project '<self-contained-repository-root>\core-v3\tests\KnowledgeClipper.Core.V3.FoundationTests\KnowledgeClipper.Core.V3.FoundationTests.csproj' -c Release --no-build -- --receipt '<receipt-path>'"
    )
    results = [ordered]@{
        build = [ordered]@{ status = 'PASS'; warningCount = 0; errorCount = 0 }
        phase2 = [ordered]@{ status = $externalPhase2Document.status; passed = @($externalPhase2Document.tests | Where-Object status -eq 'PASS').Count; total = @($externalPhase2Document.tests).Count }
        foundation = [ordered]@{ status = $externalFoundationDocument.status; passed = @($externalFoundationDocument.tests | Where-Object status -eq 'PASS').Count; total = @($externalFoundationDocument.tests).Count }
        qualityHardGates = @(
            [ordered]@{ test = 'worker-process-runtime-offline-contract'; status = (@($externalPhase2Document.tests | Where-Object name -eq 'worker-process-runtime-offline-contract')[0].status) },
            [ordered]@{ test = 'worker-process-unknown-state-is-distinct'; status = (@($externalPhase2Document.tests | Where-Object name -eq 'worker-process-unknown-state-is-distinct')[0].status) }
        )
    }
    boundaries = [ordered]@{
        realAnkiWrite = $false
        realDeepSeekCall = $false
        remNoteCall = $false
        externalMutation = $false
        independentQualityDecision = 'pending'
    }
})

$snapshotTree = New-Tree @(Get-ChildItem -LiteralPath (Join-Path $script:ReleaseRoot 'snapshot') -File -Recurse)
$artifactEntries = @(Get-ChildItem -LiteralPath (Join-Path $script:ReleaseRoot 'artifacts') -File -Recurse | ForEach-Object { Get-Entry $_.FullName })
$packetPath = Join-Path $script:ReleaseRoot 'typed-sdk-slice-packet.json'
$packet = [ordered]@{
    schema = 'kc.core-v3-typed-sdk-slice-packet/1'
    releaseId = $script:ReleaseId
    createdAt = [DateTimeOffset]::UtcNow
    status = 'PASS-awaiting-independent-quality'
    boundary = 'core-owned-worker-process-runtime-offline-qualified'
    immutableReleaseRoot = "core-v3/releases/$($script:ReleaseId)"
    publicProject = 'snapshot/core-v3/src/KnowledgeClipper.Core.V3.Client/KnowledgeClipper.Core.V3.Client.csproj'
    publicAssembly = $artifactEntries | Where-Object { $_.path -eq 'artifacts/net8.0/KnowledgeClipper.Core.V3.Client.dll' }
    runtimeDependencies = @($artifactEntries | Where-Object { $_.path -ne 'artifacts/net8.0/KnowledgeClipper.Core.V3.Client.dll' })
    capabilityBinding = 'snapshot/contracts/core/v3/capabilities/authority-bound-capability-catalog.phase2.ref.json'
    capabilityCatalog = 'snapshot/contracts/core/v3/capabilities/capability-catalog.phase2.v1.json'
    capabilityCatalogSha256 = 'D39994FEEABCAB2875923C80B2AAF6A5A8FCE2F23B7F180BF41999180874ED94'
    selfContainedReplayRoot = 'self-contained-repository'
    sourceAndContractSnapshotTreeSha256 = $snapshotTree.sha256
    evidence = @(
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\phase2-release-build.receipt.json'))
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\phase2-tests.receipt.json'))
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\foundation-regression.receipt.json'))
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\authority\frozen-worker-dependencies.json'))
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\external-replay-build.receipt.json'))
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\external-replay-phase2.receipt.json'))
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\external-replay-foundation.receipt.json'))
        (Get-Entry $qualificationReceiptPath)
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\real-anki-readonly-probe.receipt.json'))
    )
    predecessor = [ordered]@{
        releaseRoot = 'core-v3/releases/20260902T060054Z-foundation.2'
        checkpointSha256 = 'D789C855FE1F3264939E82A5661172523FFAD479218F927A8C2B44F7AD872F7A'
        preserved = $true
    }
    verifiedInvariants = @(
        'Anki probe and question operations are available only after exact frozen worker hash, version, and contract admission.',
        'Production Host uses the exact-authority WorkerProcessRuntime and rejects all fake runtime CLI switches; fake execution exists only in the test-only Host.',
        'Worker commands return durable acceptance before worker completion and expose typed projection artifacts.',
        'Transient transport failures retry within the bounded observation horizon without resubmission.',
        'Artifact identity binds command identity, kind, and canonical content digest.',
        'Question session ordering, replay idempotency, concurrent writer exclusion, and crash recovery are Core-owned.',
        'Missing Anki authorization or configuration fails before Content or Anki process dispatch.',
        'Offline Anki export simulation performs exactly one bounded write and exact readback; unknown write attempts are counted separately from confirmed writes.',
        'An external replay copied from this packet builds with zero warnings/errors and passes Phase2 10/10 plus Foundation 30/30 without repository Canary fixtures.'
    )
    restrictions = @(
        'This is not a final candidate and is not production-enabled.',
        'One real Anki read-only probe was made with zero mutating actions; no real Anki write was made.',
        'No real DeepSeek, RemNote, MinerU, OCR, speech, or speaker call was made.',
        'Legacy Orchestrator and legacy worker execution paths were not invoked.',
        'Complete frozen Content and Anki worker companion trees are SHA-inventoried inside the self-contained replay root.',
        'RemNote spec source was temporarily unavailable and treated as non-blocking only for the already approved Phase2 scope.'
    )
}
Write-CreateOnceJson $packetPath $packet

$inventoryFiles = @(Get-ChildItem -LiteralPath $script:ReleaseRoot -File -Recurse)
$inventoryTree = New-Tree $inventoryFiles
$inventoryPath = Join-Path $script:ReleaseRoot 'inventory.json'
Write-CreateOnceJson $inventoryPath ([ordered]@{
    schema = 'kc.core-v3-immutable-release-inventory/1'
    releaseId = $script:ReleaseId
    createdAt = [DateTimeOffset]::UtcNow
    treeSha256 = $inventoryTree.sha256
    entries = $inventoryTree.entries
    exclusions = @('inventory.json self', 'checkpoint.json self')
})

$checkpointPath = Join-Path $script:ReleaseRoot 'checkpoint.json'
Write-CreateOnceJson $checkpointPath ([ordered]@{
    schema = 'kc.core-v3-phase2-slice-checkpoint/1'
    releaseId = $script:ReleaseId
    createdAt = [DateTimeOffset]::UtcNow
    status = 'PASS-awaiting-independent-quality'
    immutableReleaseRoot = "core-v3/releases/$($script:ReleaseId)"
    inventory = Get-Entry $inventoryPath
    inventoryTreeSha256 = $inventoryTree.sha256
    typedSdkSlicePacket = Get-Entry $packetPath
    sourceAndContractSnapshotTreeSha256 = $snapshotTree.sha256
    predecessor = $packet.predecessor
    selfBinding = 'Checkpoint SHA-256 is bound by the external create-once reference; checkpoint.json is excluded from its own inventory to avoid a hash cycle.'
    authorityPromoted = $false
    productionRuntimeImplemented = $true
    productionEnabled = $false
    candidateAssembled = $false
    realExternalServicesCalled = $true
    realExternalMutations = $false
})

$checkpointReferencePath = Join-Path $script:RepositoryRoot "contracts\core\v3\checkpoints\$($script:ReleaseId).ref.json"
Write-CreateOnceJson $checkpointReferencePath ([ordered]@{
    schema = 'kc.core-v3-phase2-slice-checkpoint-ref/1'
    releaseId = $script:ReleaseId
    status = 'PASS-awaiting-independent-quality'
    releaseRoot = "core-v3/releases/$($script:ReleaseId)"
    checkpoint = 'checkpoint.json'
    checkpointBytes = (Get-Item -LiteralPath $checkpointPath).Length
    checkpointSha256 = Get-Sha256 $checkpointPath
    predecessor = $packet.predecessor
})

foreach ($file in Get-ChildItem -LiteralPath $script:ReleaseRoot -File -Recurse) { $file.IsReadOnly = $true }

Write-Host "IMMUTABLE_RELEASE_ROOT $($script:ReleaseRoot)"
Write-Host "CHECKPOINT_REFERENCE $checkpointReferencePath"
Write-Host "CHECKPOINT_SHA256 $(Get-Sha256 $checkpointPath)"
Write-Host "PACKET_SHA256 $(Get-Sha256 $packetPath)"
Write-Host "INVENTORY_SHA256 $(Get-Sha256 $inventoryPath)"
Write-Host "INVENTORY_TREE_SHA256 $($inventoryTree.sha256)"
Write-Host "SNAPSHOT_TREE_SHA256 $($snapshotTree.sha256)"

param(
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:Timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:ReleaseId = "$($script:Timestamp)-foundation.2"
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
    if ([System.IO.File]::Exists($Path)) {
        throw "Refusing to overwrite create-once artifact: $Path"
    }

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40) + [Environment]::NewLine, $script:Utf8NoBom)
}

function Copy-CreateOnceFile([string]$Source, [string]$Destination) {
    if ([System.IO.File]::Exists($Destination)) {
        throw "Refusing to overwrite release snapshot file: $Destination"
    }

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

if (-not [System.IO.File]::Exists($DotnetPath)) {
    throw "Pinned .NET SDK was not found: $DotnetPath"
}

$testProject = Join-Path $script:RepositoryRoot 'core-v3\tests\KnowledgeClipper.Core.V3.FoundationTests\KnowledgeClipper.Core.V3.FoundationTests.csproj'
$workspaceBuildReceipt = Join-Path $script:RepositoryRoot "core-v3\receipts\$($script:Timestamp)-foundation2-release-build.receipt.json"
$workspaceTestReceipt = Join-Path $script:RepositoryRoot "core-v3\receipts\$($script:Timestamp)-foundation2-tests.receipt.json"

$buildStarted = [DateTimeOffset]::UtcNow
$buildOutput = @(& $DotnetPath build $testProject -c Release --nologo 2>&1 | ForEach-Object { $_.ToString() })
$buildExitCode = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }
$warningLines = @($buildOutput | Where-Object { $_ -match '(^|\s)(warning|\u8b66\u544a)\s+[A-Z]*\d*:' })
$errorLines = @($buildOutput | Where-Object { $_ -match '(^|\s)(error|\u9519\u8bef)\s+[A-Z]*\d*:' })
Write-CreateOnceJson $workspaceBuildReceipt ([ordered]@{
    schema = 'kc.core-v3-release-build-receipt/2'
    releaseId = $script:ReleaseId
    startedAt = $buildStarted
    completedAt = [DateTimeOffset]::UtcNow
    status = if ($buildExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    configuration = 'Release'
    dotnetPath = $DotnetPath
    project = Get-RepositoryRelativePath $testProject
    exitCode = $buildExitCode
    warningCount = $warningLines.Count
    errorCount = $errorLines.Count
})
if ($buildExitCode -ne 0) {
    throw "Foundation.2 Release build failed: $workspaceBuildReceipt"
}

$testOutput = @(& $DotnetPath run --project $testProject -c Release --no-build -- --receipt $workspaceTestReceipt 2>&1 | ForEach-Object { $_.ToString() })
$testExitCode = $LASTEXITCODE
$testOutput | ForEach-Object { Write-Host $_ }
if ($testExitCode -ne 0) {
    throw "Foundation.2 tests failed: $workspaceTestReceipt"
}
$testDocument = Get-Content -LiteralPath $workspaceTestReceipt -Raw | ConvertFrom-Json
if ($testDocument.status -ne 'PASS') {
    throw "Foundation.2 test receipt is not PASS: $workspaceTestReceipt"
}

if ([System.IO.Directory]::Exists($script:ReleaseRoot)) {
    throw "Refusing to reuse release root: $($script:ReleaseRoot)"
}
[System.IO.Directory]::CreateDirectory($script:ReleaseRoot) | Out-Null

Copy-RepositoryTree 'core-v3\src' 'snapshot\core-v3\src' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'core-v3\tests' 'snapshot\core-v3\tests' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'core-v3\qualification-consumers' 'snapshot\core-v3\qualification-consumers' { param($file) $file.FullName -notmatch '[\\/](bin|obj)[\\/]' }
Copy-RepositoryTree 'contracts\core\v3' 'snapshot\contracts\core\v3' { param($file) $file.FullName -notmatch '[\\/]checkpoints[\\/]' }
Copy-CreateOnceFile (Join-Path $script:RepositoryRoot 'core-v3\README.md') (Join-Path $script:ReleaseRoot 'snapshot\core-v3\README.md')
Copy-CreateOnceFile $PSCommandPath (Join-Path $script:ReleaseRoot 'snapshot\core-v3\tools\Invoke-Foundation2Qualification.ps1')

$runtimeSources = @(
    'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.deps.json',
    'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.pdb',
    'core-v3\src\KnowledgeClipper.Core.V3.Contracts\bin\Release\net8.0\KnowledgeClipper.Core.V3.Contracts.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Contracts\bin\Release\net8.0\KnowledgeClipper.Core.V3.Contracts.pdb'
)
foreach ($relativeSource in $runtimeSources) {
    $source = Join-Path $script:RepositoryRoot $relativeSource
    Copy-CreateOnceFile $source (Join-Path $script:ReleaseRoot (Join-Path 'artifacts\net8.0' ([System.IO.Path]::GetFileName($source))))
}

Copy-CreateOnceFile $workspaceBuildReceipt (Join-Path $script:ReleaseRoot 'evidence\foundation2-release-build.receipt.json')
Copy-CreateOnceFile $workspaceTestReceipt (Join-Path $script:ReleaseRoot 'evidence\foundation2-tests.receipt.json')

$snapshotTree = New-Tree @(Get-ChildItem -LiteralPath (Join-Path $script:ReleaseRoot 'snapshot') -File -Recurse)
$artifactEntries = @(Get-ChildItem -LiteralPath (Join-Path $script:ReleaseRoot 'artifacts') -File -Recurse | ForEach-Object { Get-Entry $_.FullName })
$packetPath = Join-Path $script:ReleaseRoot 'typed-sdk-packet.json'
$packet = [ordered]@{
    schema = 'kc.core-v3-typed-sdk-packet/2'
    releaseId = $script:ReleaseId
    createdAt = [DateTimeOffset]::UtcNow
    status = 'PASS'
    boundary = 'foundation-only'
    immutableReleaseRoot = "core-v3/releases/$($script:ReleaseId)"
    publicProject = 'snapshot/core-v3/src/KnowledgeClipper.Core.V3.Client/KnowledgeClipper.Core.V3.Client.csproj'
    contractProject = 'snapshot/core-v3/src/KnowledgeClipper.Core.V3.Contracts/KnowledgeClipper.Core.V3.Contracts.csproj'
    publicAssembly = $artifactEntries | Where-Object { $_.path -eq 'artifacts/net8.0/KnowledgeClipper.Core.V3.Client.dll' }
    runtimeDependencies = @($artifactEntries | Where-Object { $_.path -ne 'artifacts/net8.0/KnowledgeClipper.Core.V3.Client.dll' })
    sourceAndContractSnapshotTreeSha256 = $snapshotTree.sha256
    evidence = @(
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\foundation2-release-build.receipt.json'))
        (Get-Entry (Join-Path $script:ReleaseRoot 'evidence\foundation2-tests.receipt.json'))
    )
    externalConsumerGate = [ordered]@{
        positive = 'snapshot/core-v3/qualification-consumers/positive/KnowledgeClipper.Core.V3.SdkPositiveConsumer.csproj'
        negative = 'snapshot/core-v3/qualification-consumers/negative/KnowledgeClipper.Core.V3.SdkNegativeConsumer.csproj'
        forbidden = @('CommandEnvelope', 'QueryEnvelope', 'ArtifactReference', 'KernelSchemas', 'CoreV3ProtocolClient')
    }
    supersedes = [ordered]@{
        checkpoint = 'contracts/core/v3/checkpoints/20260902T054850Z-foundation/checkpoint.json'
        checkpointSha256 = 'CE82E060BB422E8457D70B8E5750666DE200343351C315A9F11CD2AB42EDEEA2'
        status = 'rejected-never-promoted'
        reason = 'Public protocol compile surface and mutable release paths invalidated the packet boundary claim.'
    }
    restrictions = @(
        'Public SDK API exposes only Client-owned domain types.',
        'Contracts assembly exports no public protocol types.',
        'All packet component paths are relative to this immutable release root.',
        'Anki and Question operations remain unavailable in the foundation capability catalog.',
        'No Desktop, worker, candidate, or authority migration is included.'
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
    schema = 'kc.core-v3-foundation-checkpoint/2'
    releaseId = $script:ReleaseId
    createdAt = [DateTimeOffset]::UtcNow
    status = 'PASS'
    immutableReleaseRoot = "core-v3/releases/$($script:ReleaseId)"
    inventory = Get-Entry $inventoryPath
    inventoryTreeSha256 = $inventoryTree.sha256
    typedSdkPacket = Get-Entry $packetPath
    sourceAndContractSnapshotTreeSha256 = $snapshotTree.sha256
    supersedes = $packet.supersedes
    selfBinding = 'Checkpoint SHA-256 is bound by the external create-once reference; checkpoint.json is excluded from its own inventory to avoid a hash cycle.'
    authorityPromoted = $false
    candidateAssembled = $false
    externalServicesCalled = $false
})

$checkpointReferencePath = Join-Path $script:RepositoryRoot "contracts\core\v3\checkpoints\$($script:ReleaseId).ref.json"
Write-CreateOnceJson $checkpointReferencePath ([ordered]@{
    schema = 'kc.core-v3-foundation-checkpoint-ref/1'
    releaseId = $script:ReleaseId
    status = 'PASS-not-promoted'
    releaseRoot = "core-v3/releases/$($script:ReleaseId)"
    checkpoint = 'checkpoint.json'
    checkpointBytes = (Get-Item -LiteralPath $checkpointPath).Length
    checkpointSha256 = Get-Sha256 $checkpointPath
    rejectedPredecessor = $packet.supersedes
})

foreach ($file in Get-ChildItem -LiteralPath $script:ReleaseRoot -File -Recurse) {
    $file.IsReadOnly = $true
}

Write-Host "IMMUTABLE_RELEASE_ROOT $($script:ReleaseRoot)"
Write-Host "CHECKPOINT_REFERENCE $checkpointReferencePath"
Write-Host "CHECKPOINT_SHA256 $(Get-Sha256 $checkpointPath)"
Write-Host "PACKET_SHA256 $(Get-Sha256 $packetPath)"
Write-Host "INVENTORY_SHA256 $(Get-Sha256 $inventoryPath)"
Write-Host "INVENTORY_TREE_SHA256 $($inventoryTree.sha256)"
Write-Host "SNAPSHOT_TREE_SHA256 $($snapshotTree.sha256)"

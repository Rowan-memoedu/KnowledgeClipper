param(
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:Timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Get-RelativePath([string]$Path) {
    return ([System.IO.Path]::GetRelativePath($script:RepositoryRoot, [System.IO.Path]::GetFullPath($Path)) -replace '\\', '/')
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-CreateOnceJson([string]$Path, [object]$Value) {
    if ([System.IO.File]::Exists($Path)) {
        throw "Refusing to overwrite create-once qualification artifact: $Path"
    }

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    $json = $Value | ConvertTo-Json -Depth 30
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $script:Utf8NoBom)
}

function Get-FileEntry([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = Get-RelativePath $Path
        bytes = $item.Length
        sha256 = Get-Sha256 $Path
    }
}

if (-not [System.IO.File]::Exists($DotnetPath)) {
    throw "Pinned .NET SDK was not found: $DotnetPath"
}

$testProject = Join-Path $script:RepositoryRoot 'core-v3\tests\KnowledgeClipper.Core.V3.FoundationTests\KnowledgeClipper.Core.V3.FoundationTests.csproj'
$testReceipt = Join-Path $script:RepositoryRoot "core-v3\receipts\$($script:Timestamp)-formal-foundation-tests.receipt.json"
$buildReceipt = Join-Path $script:RepositoryRoot "core-v3\receipts\$($script:Timestamp)-formal-release-build.receipt.json"

$buildStarted = [DateTimeOffset]::UtcNow
$buildOutput = @(& $DotnetPath build $testProject -c Release --nologo 2>&1 | ForEach-Object { $_.ToString() })
$buildExitCode = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }
$buildSucceeded = $buildExitCode -eq 0

$binaryPaths = @(
    'core-v3\src\KnowledgeClipper.Core.V3.Contracts\bin\Release\net8.0\KnowledgeClipper.Core.V3.Contracts.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Kernel\bin\Release\net8.0\KnowledgeClipper.Core.V3.Kernel.dll',
    'core-v3\src\KnowledgeClipper.Core.V3.Host\bin\Release\net8.0\KnowledgeClipper.Core.V3.Host.dll',
    'core-v3\tests\KnowledgeClipper.Core.V3.FoundationTests\bin\Release\net8.0\KnowledgeClipper.Core.V3.FoundationTests.dll'
) | ForEach-Object { Join-Path $script:RepositoryRoot $_ }

$binaryEntries = if ($buildSucceeded) { @($binaryPaths | ForEach-Object { Get-FileEntry $_ }) } else { @() }
$warningMatch = @($buildOutput | Where-Object { $_ -match '(^|\s)(warning|\u8b66\u544a)\s+[A-Z]*\d*:' })
$errorMatch = @($buildOutput | Where-Object { $_ -match '(^|\s)(error|\u9519\u8bef)\s+[A-Z]*\d*:' })
Write-CreateOnceJson $buildReceipt ([ordered]@{
    schema = 'kc.core-v3-release-build-receipt/1'
    startedAt = $buildStarted
    completedAt = [DateTimeOffset]::UtcNow
    status = if ($buildSucceeded) { 'PASS' } else { 'FAIL' }
    configuration = 'Release'
    dotnetPath = $DotnetPath
    project = Get-RelativePath $testProject
    exitCode = $buildExitCode
    warningCount = $warningMatch.Count
    errorCount = $errorMatch.Count
    artifacts = $binaryEntries
})

if (-not $buildSucceeded) {
    throw "Formal Release build failed; receipt: $buildReceipt"
}

$testOutput = @(& $DotnetPath run --project $testProject -c Release --no-build -- --receipt $testReceipt 2>&1 | ForEach-Object { $_.ToString() })
$testExitCode = $LASTEXITCODE
$testOutput | ForEach-Object { Write-Host $_ }
if ($testExitCode -ne 0) {
    throw "Formal foundation qualification failed; receipt: $testReceipt"
}

$testDocument = Get-Content -LiteralPath $testReceipt -Raw | ConvertFrom-Json
if ($testDocument.status -ne 'PASS') {
    throw "Formal test receipt is not PASS: $testReceipt"
}

$treeCandidates = @(
    Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'core-v3\src') -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'core-v3\tests') -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'core-v3\tools') -File -Recurse
    Get-Item -LiteralPath (Join-Path $script:RepositoryRoot 'core-v3\README.md')
    Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'contracts\core\v3') -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/]checkpoints[\\/]' }
)
$treePaths = @($treeCandidates | ForEach-Object { $_.FullName } | Select-Object -Unique)
[Array]::Sort($treePaths, [StringComparer]::Ordinal)
$treeEntries = @($treePaths | ForEach-Object { Get-FileEntry $_ })
$treeLines = $treeEntries | ForEach-Object { "$($_.path)`t$($_.sha256)`t$($_.bytes)`n" }
$treeHashBytes = [System.Security.Cryptography.SHA256]::HashData($script:Utf8NoBom.GetBytes(($treeLines -join '')))
$treeHash = [Convert]::ToHexString($treeHashBytes)

$checkpointDirectory = Join-Path $script:RepositoryRoot "contracts\core\v3\checkpoints\$($script:Timestamp)-foundation"
[System.IO.Directory]::CreateDirectory($checkpointDirectory) | Out-Null
$sdkPacketPath = Join-Path $checkpointDirectory 'typed-sdk-packet.json'
$checkpointPath = Join-Path $checkpointDirectory 'checkpoint.json'

$clientSourcePaths = @($treePaths | Where-Object { (Get-RelativePath $_).StartsWith('core-v3/src/KnowledgeClipper.Core.V3.Client/', [StringComparison]::Ordinal) })
$sdkPacket = [ordered]@{
    schema = 'kc.core-v3-typed-sdk-packet/1'
    createdAt = [DateTimeOffset]::UtcNow
    boundary = 'foundation-only'
    sourceTreeSha256 = $treeHash
    publicProject = 'core-v3/src/KnowledgeClipper.Core.V3.Client/KnowledgeClipper.Core.V3.Client.csproj'
    contractProject = 'core-v3/src/KnowledgeClipper.Core.V3.Contracts/KnowledgeClipper.Core.V3.Contracts.csproj'
    publicAssembly = Get-FileEntry (Join-Path $script:RepositoryRoot 'core-v3\src\KnowledgeClipper.Core.V3.Client\bin\Release\net8.0\KnowledgeClipper.Core.V3.Client.dll')
    contractAssembly = Get-FileEntry (Join-Path $script:RepositoryRoot 'core-v3\src\KnowledgeClipper.Core.V3.Contracts\bin\Release\net8.0\KnowledgeClipper.Core.V3.Contracts.dll')
    publicSources = @($clientSourcePaths | ForEach-Object { Get-FileEntry $_ })
    capabilityBinding = Get-FileEntry (Join-Path $script:RepositoryRoot 'contracts\core\v3\capabilities\authority-bound-capability-catalog.ref.json')
    capabilityCatalog = Get-FileEntry (Join-Path $script:RepositoryRoot 'contracts\core\v3\capabilities\capability-catalog.v1.json')
    buildReceipt = Get-FileEntry $buildReceipt
    testReceipt = Get-FileEntry $testReceipt
    restrictions = @(
        'Low-level command/query protocol types remain internal to the SDK.',
        'Artifact references remain opaque to SDK consumers.',
        'Anki and Question operations are prepared but unavailable in the foundation catalog.',
        'No Desktop, worker, candidate, or authority migration is included.'
    )
}
Write-CreateOnceJson $sdkPacketPath $sdkPacket

$checkpoint = [ordered]@{
    schema = 'kc.core-v3-foundation-checkpoint/1'
    createdAt = [DateTimeOffset]::UtcNow
    status = 'PASS'
    sourceTreeSha256 = $treeHash
    sourceTree = $treeEntries
    typedSdkPacket = Get-FileEntry $sdkPacketPath
    buildReceipt = Get-FileEntry $buildReceipt
    testReceipt = Get-FileEntry $testReceipt
    authorityPromoted = $false
    candidateAssembled = $false
    externalServicesCalled = $false
}
Write-CreateOnceJson $checkpointPath $checkpoint

Write-Host "FORMAL_BUILD_RECEIPT $buildReceipt"
Write-Host "FORMAL_TEST_RECEIPT $testReceipt"
Write-Host "TYPED_SDK_PACKET $sdkPacketPath"
Write-Host "CHECKPOINT $checkpointPath"
Write-Host "SOURCE_TREE_SHA256 $treeHash"
Write-Host "CHECKPOINT_SHA256 $(Get-Sha256 $checkpointPath)"

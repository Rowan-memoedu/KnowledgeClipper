[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CanaryReceipt,
    [string]$ReleaseRoot
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$canaryReceiptPath = (Resolve-Path -LiteralPath $CanaryReceipt).Path
$canary = Get-Content -Raw -LiteralPath $canaryReceiptPath | ConvertFrom-Json
if ($canary.status -ne 'passed' -or $canary.worker.workerId -ne 'export.remnote' -or $canary.worker.workerVersion -ne '2.1.0') {
    throw 'Only a passed export.remnote 2.1.0 offline Canary receipt can be packaged'
}
if ($canary.realRemNoteCalled -or $canary.realMcpCalled) {
    throw 'This release packager only accepts the Fake MCP/offline Canary'
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $project "releases\knowledge-clipper-remnote-exporter-v2.1.0-$stamp\win-x64"
}
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
if (Test-Path -LiteralPath $ReleaseRoot) { throw "Append-only release root already exists: $ReleaseRoot" }
New-Item -ItemType Directory -Path $ReleaseRoot | Out-Null

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Hash-Record([string]$Path, [string]$RelativeTo) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    [ordered]@{
        path = [IO.Path]::GetRelativePath($RelativeTo, $resolved).Replace('\', '/')
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash
        bytes = (Get-Item -LiteralPath $resolved).Length
    }
}

$copyMap = [ordered]@{
    'KnowledgeClipper.RemNoteExporter.Worker.exe' = 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
    'appsettings.json' = 'workers\remnote\appsettings.json'
    'README.md' = 'workers\remnote\README.md'
    'contracts/remnote-export-payload.v2.schema.json' = 'contracts\remnote-export-payload.v2.schema.json'
    'contracts/remnote-export-task.v2.schema.json' = 'contracts\remnote-export-task.v2.schema.json'
    'contracts/remnote-write-receipt.v2.schema.json' = 'contracts\remnote-write-receipt.v2.schema.json'
    'contracts/remnote-destination-settings.v1.schema.json' = 'contracts\remnote-destination-settings.v1.schema.json'
    'contracts/remnote-destination-settings-snapshot.v1.schema.json' = 'contracts\remnote-destination-settings-snapshot.v1.schema.json'
    'contracts/remnote-destination-settings-descriptor.v1.schema.json' = 'contracts\remnote-destination-settings-descriptor.v1.schema.json'
    'contracts/remnote-destination-companion-descriptor.v1.schema.json' = 'contracts\remnote-destination-companion-descriptor.v1.schema.json'
    'contracts/remnote-readonly-probe-request.v1.schema.json' = 'contracts\remnote-readonly-probe-request.v1.schema.json'
    'contracts/remnote-readonly-probe-result.v1.schema.json' = 'contracts\remnote-readonly-probe-result.v1.schema.json'
}
foreach ($entry in $copyMap.GetEnumerator()) {
    $source = Join-Path $project $entry.Value
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Release source missing: $source" }
    $destination = Join-Path $ReleaseRoot $entry.Key
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination
}

$evidenceDir = Join-Path $ReleaseRoot 'evidence'
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
Copy-Item -LiteralPath $canaryReceiptPath -Destination (Join-Path $evidenceDir 'offline-canary-receipt.json')
$canaryDir = Split-Path -Parent $canaryReceiptPath
foreach ($name in @('readable-report.md', 'fake-remnote-tree.md', 'settings-companion-descriptor.json', 'worker-describe.json', 'additive-schema-validation.log', 'public-contract-validation.json')) {
    $source = Join-Path $canaryDir $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Canary evidence missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $evidenceDir $name)
}

$packagedWorker = Join-Path $ReleaseRoot 'KnowledgeClipper.RemNoteExporter.Worker.exe'
$packagedWorkerHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $packagedWorker).Hash
if ($packagedWorkerHash -ne $canary.worker.executable.sha256) {
    throw 'Packaged worker does not match the admitted offline Canary executable'
}

$payloadFiles = @(Get-ChildItem -LiteralPath $ReleaseRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    Hash-Record $_.FullName $ReleaseRoot
})
$treeMaterial = ($payloadFiles | ForEach-Object { "$($_.path)`t$($_.sha256)`t$($_.bytes)" }) -join "`n"
$treeBytes = [Text.Encoding]::UTF8.GetBytes($treeMaterial)
$treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($treeBytes))

$manifestPath = Join-Path $ReleaseRoot 'release-manifest.json'
$manifest = [ordered]@{
    schemaVersion = 'knowledge-clipper-remnote-exporter-release-manifest.v1'
    releaseId = "knowledge-clipper-remnote-exporter-v2.1.0-$stamp"
    workerId = 'export.remnote'
    workerVersion = '2.1.0'
    platform = 'win-x64'
    additive = $true
    frozenCommonV1Modified = $false
    realRemNoteCalled = $false
    realMcpCalled = $false
    payloadTreeSha256 = $treeHash
    files = $payloadFiles
}
Write-Utf8NoBom $manifestPath (($manifest | ConvertTo-Json -Depth 10) + "`n")

$releaseReceiptPath = Join-Path $ReleaseRoot 'release-receipt.json'
$releaseReceipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-remnote-exporter-release-receipt.v1'
    status = 'passed'
    releaseId = $manifest.releaseId
    releaseRoot = $ReleaseRoot
    worker = [ordered]@{ workerId = 'export.remnote'; workerVersion = '2.1.0'; executableSha256 = $packagedWorkerHash }
    contracts = [ordered]@{
        companionDescriptor = 'remnote-destination-companion-descriptor.v1'
        probeRequest = 'remnote-readonly-probe-request.v1'
        probeResult = 'remnote-readonly-probe-result.v1'
    }
    sourceCanary = [ordered]@{ path = $canaryReceiptPath; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $canaryReceiptPath).Hash }
    manifest = [ordered]@{ path = $manifestPath; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash }
    payloadTreeSha256 = $treeHash
    authorityRegistryAdmission = 'pending-core-owned-checkpoint'
    realReadOnlyProbe = 'not-run-in-offline-release'
    createdAt = [DateTime]::UtcNow.ToString('o')
}
Write-Utf8NoBom $releaseReceiptPath (($releaseReceipt | ConvertTo-Json -Depth 8) + "`n")
Write-Output $releaseReceiptPath


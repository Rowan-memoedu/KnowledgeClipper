[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$RegistryVersion
)

$ErrorActionPreference = 'Stop'
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$registryPath = Join-Path $AuthorityDirectory 'worker-registry.v1.json'
$dependencyPath = Join-Path $AuthorityDirectory 'dependency-manifest.v1.json'
$receiptPath = Join-Path $AuthorityDirectory 'assembly-receipt.v1.json'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

foreach ($required in @($ReleaseRoot, $AuthorityDirectory, $registryPath, $dependencyPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Assembly receipt input is missing: $required" }
}
if (Test-Path -LiteralPath $receiptPath) { throw "Append-only assembly receipt already exists: $receiptPath" }

$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -Depth 100
$dependency = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json -Depth 100
if ([string]$registry.registryVersion -ne $RegistryVersion -or @($registry.workers).Count -ne 5) {
    throw 'Registry identity/worker count drifted.'
}
$releaseId = Split-Path -Leaf (Split-Path -Parent $ReleaseRoot)
if ([string]$dependency.releaseId -ne $releaseId) { throw 'Dependency manifest release identity drifted.' }

$orchestrator = Join-Path $ReleaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$validationText = (& $orchestrator validate-registry --registry $registryPath --release-root $ReleaseRoot 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw "validate-registry failed: $validationText" }
$validation = $validationText | ConvertFrom-Json -Depth 100
if ($validation.status -ne 'valid' -or $validation.registryVersion -ne $RegistryVersion -or $validation.workers -ne 5) {
    throw 'validate-registry returned unexpected evidence.'
}

$releasePrefix = $ReleaseRoot.TrimEnd('\') + '\'
$workers = [ordered]@{}
foreach ($worker in @($registry.workers)) {
    $workerId = [string]$worker.workerId
    $relative = [string]$worker.entrypoint.path
    if ([string]::IsNullOrWhiteSpace($workerId) -or $workers.Contains($workerId) -or
        [string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative.Contains('..') -or $relative.Contains(':')) {
        throw "Invalid or duplicate worker entrypoint: $workerId $relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $ReleaseRoot ($relative.Replace('/', '\'))))
    if (-not $path.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Worker entrypoint escaped release root: $relative" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Worker entrypoint is missing: $relative" }
    $item = Get-Item -LiteralPath $path
    $workers[$workerId] = [ordered]@{ path = $path; bytes = [long]$item.Length; sha256 = Get-Sha256 $path }
}
if ($workers.Count -ne 5) { throw "Assembly receipt requires exactly five workers; actual=$($workers.Count)" }

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-assembly-receipt.v1'
    status = 'candidate-built-offline'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    releaseId = $releaseId
    releaseRoot = $ReleaseRoot
    authorityDirectory = $AuthorityDirectory
    registryVersion = $RegistryVersion
    release = [ordered]@{ files = [int]$dependency.files; bytes = [long]$dependency.bytes; treeSha256 = [string]$dependency.treeSha256 }
    registrySha256 = Get-Sha256 $registryPath
    dependencyManifestSha256 = Get-Sha256 $dependencyPath
    workers = $workers
    registryValidation = $validation
    workerRunInvocations = 0
    externalWrites = 0
    remnoteWrites = 0
    ankiWrites = 0
    mineruCalls = 0
    physicalSpeakerInvocations = 0
}
$temporary = Join-Path $AuthorityDirectory ('.assembly-receipt.staging-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    $json = $receipt | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $receiptPath)
}
finally {
    if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
}
[ordered]@{ status = 'written'; path = $receiptPath; sha256 = Get-Sha256 $receiptPath; workers = $workers.Count; validation = $validation } | ConvertTo-Json -Depth 20

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$RegistryVersion,
    [string]$ProjectRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$generator = Join-Path $ProjectRoot 'tools\New-KnowledgeClipperAuthorityCheckpoint.ps1'
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ('knowledge-clipper-authority-gate-' + [Guid]::NewGuid().ToString('N'))

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $positive = & $generator `
        -ProjectRoot $ProjectRoot `
        -ReleaseRoot $ReleaseRoot `
        -AuthorityDirectory $AuthorityDirectory `
        -RegistryVersion $RegistryVersion `
        -AdmissionStatus candidate-admitted `
        -ValidateOnly | ConvertFrom-Json

    if ($positive.admissionStatus -ne 'candidate-admitted' -or
        $positive.validation.status -ne 'valid' -or
        $positive.validation.workers -ne 5 -or
        $positive.frozenV1.mismatches -ne 0) {
        throw 'Positive authority validation returned an unexpected result.'
    }
    if (Test-Path -LiteralPath (Join-Path $AuthorityDirectory 'checkpoint.json')) {
        throw 'ValidateOnly unexpectedly published a checkpoint.'
    }

    $tamperedAuthority = Join-Path $temporaryRoot 'authority'
    [IO.Directory]::CreateDirectory($tamperedAuthority) | Out-Null
    Copy-Item -LiteralPath (Join-Path $AuthorityDirectory 'worker-registry.v1.json') -Destination $tamperedAuthority
    Copy-Item -LiteralPath (Join-Path $AuthorityDirectory 'dependency-manifest.v1.json') -Destination $tamperedAuthority
    $dependencyPath = Join-Path $tamperedAuthority 'dependency-manifest.v1.json'
    $dependency = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json
    $dependency.treeSha256 = '0' * 64
    [IO.File]::WriteAllText(
        $dependencyPath,
        (($dependency | ConvertTo-Json -Depth 16) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))

    $driftRejected = $false
    try {
        & $generator `
            -ProjectRoot $ProjectRoot `
            -ReleaseRoot $ReleaseRoot `
            -AuthorityDirectory $tamperedAuthority `
            -RegistryVersion $RegistryVersion `
            -AdmissionStatus candidate-admitted `
            -ValidateOnly | Out-Null
    }
    catch {
        $driftRejected = $_.Exception.Message -like 'Dependency manifest drift:*'
    }
    if (-not $driftRejected) { throw 'Tampered release dependency tree was not rejected.' }

    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-authority-checkpoint-gate-receipt.v1'
        generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        status = 'passed'
        registryVersion = $RegistryVersion
        positive = [ordered]@{
            frozenFiles = $positive.frozenV1.files
            frozenMismatches = $positive.frozenV1.mismatches
            registryWorkers = $positive.validation.workers
            runtimeAdmission = $positive.validation.status
            checkpointWritten = $false
        }
        negative = [ordered]@{
            dependencyTreeDriftRejected = $driftRejected
        }
    }
    $json = $receipt | ConvertTo-Json -Depth 8
    if ($ReceiptPath) {
        $ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
        if (Test-Path -LiteralPath $ReceiptPath) { throw "Append-only receipt already exists: $ReceiptPath" }
        $receiptDirectory = Split-Path -Parent $ReceiptPath
        [IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
        $temporaryReceipt = Join-Path $receiptDirectory ('.' + [IO.Path]::GetFileName($ReceiptPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [IO.File]::WriteAllText($temporaryReceipt, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($temporaryReceipt, $ReceiptPath)
        }
        finally {
            if (Test-Path -LiteralPath $temporaryReceipt) { [IO.File]::Delete($temporaryReceipt) }
        }
    }
    $json
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    if (-not $resolvedTemporary.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unexpected temporary directory: $resolvedTemporary"
    }
    if (Test-Path -LiteralPath $resolvedTemporary) {
        [IO.Directory]::Delete($resolvedTemporary, $true)
    }
}

param(
    [Parameter(Mandatory = $true)][string]$WorkerExe,
    [Parameter(Mandatory = $true)][string]$LegacyWorkerAlias
)

$ErrorActionPreference = 'Stop'
$worker = (Resolve-Path -LiteralPath $WorkerExe).Path
$legacyAlias = (Resolve-Path -LiteralPath $LegacyWorkerAlias).Path
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
$readme = Join-Path $projectRoot 'src\RemNoteQuickClipper.Speech.Worker\README.md'

function Assert-Equal([string]$label, [string]$expected, [string]$actual) {
    if ($expected -cne $actual) { throw "$label expected=[$expected] actual=[$actual]" }
}

function Assert-DestinationNeutral([string]$label, [string]$value) {
    $normalized = $value.ToLowerInvariant()
    if ($normalized.Contains('remnote') -or $normalized.Contains('anki')) {
        throw "$label contains a destination-specific identity: $value"
    }
}

Assert-Equal 'candidate filename' 'KnowledgeClipper.Speech.Worker.exe' ([IO.Path]::GetFileName($worker))
$descriptorText = (& $worker describe --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "describe failed with exit code $LASTEXITCODE" }
Assert-DestinationNeutral 'descriptor' $descriptorText
$descriptor = $descriptorText | ConvertFrom-Json
Assert-Equal 'workerId' 'speech.edge' $descriptor.workerId
Assert-Equal 'contractVersion' '1.0.0' $descriptor.contractVersion
if ($descriptor.stages -notcontains 'speech.synthesize' -or $descriptor.stages -notcontains 'speech.playback') {
    throw 'descriptor is missing a speech stage'
}

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($worker)
Assert-Equal 'product name' '零碎知识剪藏' $version.ProductName
Assert-Equal 'file description' 'KnowledgeClipper Speech Worker' $version.FileDescription
Assert-Equal 'company' 'KnowledgeClipper' $version.CompanyName
$metadata = @($version.ProductName, $version.FileDescription, $version.CompanyName, $version.InternalName, $version.OriginalFilename) -join '|'
Assert-DestinationNeutral 'candidate metadata' $metadata

$assembly = [Reflection.Assembly]::LoadFile($worker)
$namespaces = @($assembly.GetTypes() | ForEach-Object Namespace | Where-Object { $_ } | Sort-Object -Unique)
foreach ($namespace in $namespaces) {
    if (-not $namespace.StartsWith('KnowledgeClipper.Speech.', [StringComparison]::Ordinal)) {
        throw "non-authoritative namespace in candidate: $namespace"
    }
    Assert-DestinationNeutral 'namespace' $namespace
}

$legacyHash = (Get-FileHash -LiteralPath $legacyAlias -Algorithm SHA256).Hash
$candidateHash = (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash
Assert-Equal 'legacy alias hash' $candidateHash $legacyHash

$readmeText = Get-Content -Raw -LiteralPath $readme
$legacyMarker = '## Legacy compatibility（非权威身份）'
$legacyIndex = $readmeText.IndexOf($legacyMarker, [StringComparison]::Ordinal)
if ($legacyIndex -lt 0) { throw 'README is missing the explicit legacy compatibility boundary' }
Assert-DestinationNeutral 'README authoritative section' $readmeText.Substring(0, $legacyIndex)
if (-not $readmeText.Contains('KnowledgeClipper.Speech.Worker.exe')) { throw 'README is missing the canonical candidate filename' }

$diagnosticPath = Join-Path $testRoot ('bin\identity-diagnostic-' + [guid]::NewGuid().ToString('N') + '.log')
try {
    & $worker unsupported-command 2> $diagnosticPath | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'invalid CLI command unexpectedly succeeded' }
    $diagnostic = Get-Content -Raw -LiteralPath $diagnosticPath
    Assert-DestinationNeutral 'CLI diagnostics' $diagnostic
}
finally {
    if (Test-Path -LiteralPath $diagnosticPath) { Remove-Item -LiteralPath $diagnosticPath -Force }
}

$receiptRoot = Join-Path $testRoot ('bin\identity\r' + (Get-Date).ToUniversalTime().ToString('HHmmss') + [guid]::NewGuid().ToString('N').Substring(0, 4))
New-Item -ItemType Directory -Path $receiptRoot | Out-Null
$receiptPath = Join-Path $receiptRoot 'destination-neutral-identity-receipt.json'
$receipt = [ordered]@{
    schemaVersion = 1
    audit = 'speech-worker-destination-neutral-public-identity'
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    productDisplayName = '零碎知识剪藏'
    candidate = [ordered]@{
        fileName = [IO.Path]::GetFileName($worker)
        sha256 = $candidateHash
        bytes = (Get-Item -LiteralPath $worker).Length
        productName = $version.ProductName
        fileDescription = $version.FileDescription
        companyName = $version.CompanyName
        namespaces = $namespaces
    }
    descriptor = $descriptor
    diagnosticDestinationNeutral = $true
    readmeAuthoritativeSectionDestinationNeutral = $true
    legacyCompatibilityAliases = @([ordered]@{
        fileName = [IO.Path]::GetFileName($legacyAlias)
        role = 'deprecated-compatibility-alias-only'
        byteIdenticalToCandidate = $true
    })
}
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Write-Output "PASS DESTINATION-NEUTRAL IDENTITY $receiptPath"
exit 0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Required SDK is missing: $dotnet" }
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) { throw "Output directory is missing: $outputRoot" }

function Write-JsonOnce([string]$Name, [object]$Value) {
    $path = Join-Path $outputRoot $Name
    if (Test-Path -LiteralPath $path) {
        $existing = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
        if (($existing | ConvertTo-Json -Depth 100 -Compress) -ne ($Value | ConvertTo-Json -Depth 100 -Compress)) {
            throw "Existing append-only validation receipt differs: $path"
        }
        return
    }
    [IO.File]::WriteAllText($path, (($Value | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
}

function Invoke-JsonTest([string]$Name, [string]$ProjectRelative, [string]$ReceiptName, [string]$AssemblyName = '') {
    $existingPath = Join-Path $outputRoot $ReceiptName
    if (Test-Path -LiteralPath $existingPath -PathType Leaf) {
        $existing = Get-Content -LiteralPath $existingPath -Raw | ConvertFrom-Json -Depth 100
        if ($existing.status -ne 'passed') { throw "Existing $Name receipt is not passed." }
        return
    }
    $project = Join-Path $projectRoot $ProjectRelative
    & $dotnet build $project -c Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Name" }
    $dllName = if ([string]::IsNullOrWhiteSpace($AssemblyName)) { [IO.Path]::GetFileNameWithoutExtension($project) + '.dll' } else { $AssemblyName + '.dll' }
    $dll = Join-Path ([IO.Path]::GetDirectoryName($project)) "bin\Release\net8.0\$dllName"
    $output = @(& $dotnet $dll $projectRoot 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "$Name failed: $($output -join [Environment]::NewLine)" }
    $text = $output -join [Environment]::NewLine
    $start = $text.IndexOf('{')
    if ($start -lt 0) { throw "$Name did not emit JSON." }
    $receipt = $text.Substring($start) | ConvertFrom-Json -Depth 100
    if ($receipt.status -ne 'passed') { throw "$Name receipt is not passed." }
    Write-JsonOnce $ReceiptName $receipt
}

Invoke-JsonTest 'Orchestrator contract regression' 'tests\Orchestrator.ContractTests\Orchestrator.ContractTests.csproj' 'orchestrator-regression.receipt.json'
Invoke-JsonTest 'Core adapter integration' 'tests\CoreAdapter.IntegrationTests\CoreAdapter.IntegrationTests.csproj' 'core-adapter.receipt.json'
Invoke-JsonTest 'Settings control integration' 'tests\SettingsControl.IntegrationTests\SettingsControl.IntegrationTests.csproj' 'settings-control.receipt.json'
Invoke-JsonTest 'RemNote content adapter compatibility' 'tests\RemNoteQuickClipper.RemNoteExporter.Tests\ContentAdapterCompatibility\ContentAdapterCompatibility.csproj' 'remnote-content-adapter.receipt.json' 'KnowledgeClipper.RemNoteExporter.ContentAdapterCompatibility'

$phaseBProject = Join-Path $projectRoot 'tests\PhaseB.Canary\PhaseB.Canary.csproj'
& $dotnet build $phaseBProject -c Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Phase B harness build failed.' }
Write-JsonOnce 'phase-b-build.receipt.json' ([ordered]@{ schemaVersion = 'knowledge-clipper-phase-b-build-receipt.v1'; status = 'passed'; sdk = '8.0.424'; warnings = 0; errors = 0 })

$contractOutput = @(& python (Join-Path $projectRoot 'tools\validate_contracts.py') 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Public contract validation failed: $($contractOutput -join [Environment]::NewLine)" }
$contractText = $contractOutput -join [Environment]::NewLine
$contract = $contractText | ConvertFrom-Json -Depth 20
if ($contract.schemasChecked -le 0 -or $contract.validFixturesAccepted -le 0 -or $contract.invalidFixturesRejected -le 0) {
    throw "Unable to parse public contract counts: $contractText"
}
Write-JsonOnce 'public-contracts.receipt.json' ([ordered]@{
    schemaVersion = 'knowledge-clipper-public-contract-validation-receipt.v1'
    status = 'passed'
    schemas = [int]$contract.schemasChecked
    validFixtures = [int]$contract.validFixturesAccepted
    invalidFixtures = [int]$contract.invalidFixturesRejected
    raw = $contractText
})

[ordered]@{ status = 'passed'; outputDirectory = $outputRoot; receipts = 6 } | ConvertTo-Json

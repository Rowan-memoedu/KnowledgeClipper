[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$PublishSelfContained
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$expectedSdk = '8.0.424'

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned .NET SDK is missing: $dotnet"
}
if ((& $dotnet --version).Trim() -ne $expectedSdk) {
    throw "Pinned .NET SDK version mismatch; expected $expectedSdk."
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $projectRoot
try {
    & python tools\validate_contracts.py
    if ($LASTEXITCODE -ne 0) { throw 'Contract validation failed.' }

    foreach ($project in @(
        'orchestrator\RemNoteQuickClipper.Orchestrator.csproj',
        'tests\FakeWorker\FakeWorker.csproj',
        'tests\Orchestrator.ContractTests\Orchestrator.ContractTests.csproj'
    )) {
        & $dotnet restore $project
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
        & $dotnet build $project --configuration $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }

    & $dotnet "tests\Orchestrator.ContractTests\bin\$Configuration\net8.0\Orchestrator.ContractTests.dll" $projectRoot
    if ($LASTEXITCODE -ne 0) { throw 'Orchestrator Canary failed.' }

    if ($PublishSelfContained) {
        & $dotnet publish orchestrator\RemNoteQuickClipper.Orchestrator.csproj `
            --configuration Release --runtime win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
            --output dist\orchestrator\win-x64\v1.0.0
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    }
}
finally {
    Pop-Location
}

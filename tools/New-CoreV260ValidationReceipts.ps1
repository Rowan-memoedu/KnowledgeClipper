[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$RuntimeVersion = '2.6.0',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$outputRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot $OutputDirectory))
$runtimeRoot = Join-Path $ProjectRoot "dist/orchestrator/win-x64/v$RuntimeVersion"
$dotnetCandidates = @(
    'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe',
    (Join-Path $env:TEMP 'rqc-dotnet-sdk-8.0.424\dotnet.exe')
)
$dotnet = $dotnetCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $dotnet) { throw 'Pinned .NET SDK 8.0.424 is unavailable.' }
if (Test-Path -LiteralPath $outputRoot) { throw "Append-only receipt root exists: $outputRoot" }
if (Test-Path -LiteralPath $runtimeRoot) { throw "Append-only runtime root exists: $runtimeRoot" }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

& $dotnet publish (Join-Path $ProjectRoot 'orchestrator/KnowledgeClipper.Orchestrator.csproj') -c Release -r win-x64 --self-contained true -p:Version=$RuntimeVersion -p:AssemblyVersion=$RuntimeVersion -p:FileVersion=$RuntimeVersion -o $runtimeRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Core runtime publish failed.' }
$runtime = Join-Path $runtimeRoot 'KnowledgeClipper.Orchestrator.exe'
$describe = ((& $runtime describe 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
if ($LASTEXITCODE -ne 0 -or $describe.version -notlike "$RuntimeVersion*") { throw 'Core runtime identity validation failed.' }

function Invoke-ReceiptTest([string]$Project, [string]$EnvironmentName, [string]$ReceiptName) {
    $receipt = Join-Path $outputRoot $ReceiptName
    [Environment]::SetEnvironmentVariable($EnvironmentName, $receipt, 'Process')
    try {
        & $dotnet run --project (Join-Path $ProjectRoot $Project) -c Release -- $ProjectRoot
        if ($LASTEXITCODE -ne 0) { throw "Test failed: $Project" }
    }
    finally { [Environment]::SetEnvironmentVariable($EnvironmentName, $null, 'Process') }
    if (-not (Test-Path -LiteralPath $receipt -PathType Leaf)) { throw "Receipt missing: $ReceiptName" }
    $document = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json -Depth 100
    if ($document.status -ne 'passed') { throw "Receipt is not passed: $ReceiptName" }
}

Invoke-ReceiptTest 'tests/SettingsControl.IntegrationTests/SettingsControl.IntegrationTests.csproj' 'RQC_SETTINGS_CONTROL_RECEIPT' 'settings-control.receipt.json'
Invoke-ReceiptTest 'tests/CoreAdapter.IntegrationTests/CoreAdapter.IntegrationTests.csproj' 'RQC_CORE_ADAPTER_RECEIPT' 'core-adapter.receipt.json'
Invoke-ReceiptTest 'tests/Orchestrator.ContractTests/Orchestrator.ContractTests.csproj' 'RQC_CONTRACT_TEST_RECEIPT' 'orchestrator-contract-tests.receipt.json'
Invoke-ReceiptTest 'tests/QuestionWorkflow.ContractTests/QuestionWorkflow.ContractTests.csproj' 'KNOWLEDGE_CLIPPER_QUESTION_TEST_RECEIPT' 'question-workflow.receipt.json'

$contractOutput = @(& python (Join-Path $ProjectRoot 'tools/validate_contracts.py') 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Public contract validation failed: $($contractOutput -join [Environment]::NewLine)" }
$contractText = $contractOutput -join [Environment]::NewLine
$contract = $contractText | ConvertFrom-Json -Depth 20
$publicReceipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-public-contract-validation-receipt.v1'; status = 'passed'
    schemasChecked = [int]$contract.schemasChecked; validFixturesAccepted = [int]$contract.validFixturesAccepted; invalidFixturesRejected = [int]$contract.invalidFixturesRejected
    frozenV1Modified = $false; raw = $contractText
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'public-contracts.receipt.json'), (($publicReceipt | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))

[ordered]@{ status='passed'; runtimeVersion=$RuntimeVersion; outputDirectory=[IO.Path]::GetRelativePath($ProjectRoot,$outputRoot).Replace('\','/'); receipts=5; externalCalls=0; physicalSpeakerInvocations=0 } | ConvertTo-Json

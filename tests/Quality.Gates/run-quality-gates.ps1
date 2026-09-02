$ErrorActionPreference = 'Stop'

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Resolve-Path (Join-Path $testRoot '..\..')
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned .NET SDK is missing: $dotnet"
}

python -m unittest discover -s $testRoot -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $projectRoot 'orchestrator\RemNoteQuickClipper.Orchestrator.csproj') --configuration Release --no-restore --nologo -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $projectRoot 'tests\RemNoteQuickClipper.RemNoteExporter.Tests\ContentAdapterCompatibility\ContentAdapterCompatibility.csproj') --configuration Release --no-restore --nologo -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $projectRoot 'tests\RemNoteQuickClipper.Desktop.Tests\build-and-test.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $projectRoot 'tests\FakeWorker\FakeWorker.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $projectRoot 'tests\Orchestrator.ContractTests\Orchestrator.ContractTests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet (Join-Path $projectRoot 'tests\Orchestrator.ContractTests\bin\Release\net8.0\Orchestrator.ContractTests.dll') $projectRoot
exit $LASTEXITCODE

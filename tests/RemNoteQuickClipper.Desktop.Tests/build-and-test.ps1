$ErrorActionPreference = 'Stop'

$tests = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = (Resolve-Path (Join-Path $tests '..\..')).Path
$core = Join-Path $project 'src\RemNoteQuickClipper.Desktop.Core'
$contract = Join-Path $project 'contracts\desktop-automation\v1'
$contractV11 = Join-Path $project 'contracts\desktop-automation\v1.1'
$contractV12 = Join-Path $project 'contracts\desktop-automation\v1.2'
$output = Join-Path $project 'obj\desktop-automation-tests'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = Get-ChildItem -LiteralPath $core -Filter '*.cs' | Select-Object -ExpandProperty FullName
$testSources = Get-ChildItem -LiteralPath $tests -Filter '*.cs' | Select-Object -ExpandProperty FullName
$references = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll')

& $csc /nologo /target:exe /main:KnowledgeClipper.Desktop.Tests.Program "/out:$output\DesktopCoreTests.exe" @references @sources @testSources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $output 'DesktopCoreTests.exe') (Join-Path $contract 'desktop-automation.contract.json') (Join-Path $contractV11 'desktop-automation.contract.json') (Join-Path $contractV12 'desktop-automation.contract.json')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $csc /nologo /target:exe /main:KnowledgeClipper.Desktop.Core.SemanticPortProgram "/out:$output\DesktopSemanticHarness.exe" @references @sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $csc /nologo /target:exe /main:KnowledgeClipper.Desktop.Core.SemanticPortProgramV11 "/out:$output\DesktopSemanticHarnessV11.exe" @references @sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

py -3 (Join-Path $tests 'validate_contracts.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
py -3 (Join-Path $tests 'validate_contracts_v11.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
py -3 (Join-Path $tests 'validate_contracts_v12.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
py -3 (Join-Path $tests 'validate_contracts_v13.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-Content -LiteralPath (Join-Path $contract 'fixtures\semantic-actions.valid.ndjson') |
    & (Join-Path $output 'DesktopSemanticHarness.exe') (Join-Path $contract 'fixtures\fake-scenario.v1.json') |
    py -3 (Join-Path $tests 'validate_harness.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-Content -LiteralPath (Join-Path $contractV11 'fixtures\semantic-actions.valid.ndjson') |
    & (Join-Path $output 'DesktopSemanticHarnessV11.exe') (Join-Path $contractV11 'fixtures\fake-scenario.v1.1.json') |
    py -3 (Join-Path $tests 'validate_harness_v11.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $tests 'verify_source_snapshot.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output 'PASS desktop automation build and tests'

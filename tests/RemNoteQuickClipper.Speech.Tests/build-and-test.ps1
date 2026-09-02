$ErrorActionPreference = 'Stop'

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Resolve-Path (Join-Path $testRoot '..\..')
$sourceRoot = Join-Path $projectRoot 'src\RemNoteQuickClipper.Speech.Worker'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputRoot = Join-Path $testRoot 'bin'

if (-not (Test-Path -LiteralPath $compiler)) { throw "Missing compiler: $compiler" }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | ForEach-Object FullName)
$tests = @(Get-ChildItem -LiteralPath $testRoot -Filter '*.cs' -File | ForEach-Object FullName)
$workerExe = Join-Path $outputRoot 'KnowledgeClipper.Speech.Worker.exe'
$legacyWorkerAlias = Join-Path $outputRoot 'RemNoteQuickClipper.Speech.Worker.exe'
$testsExe = Join-Path $outputRoot 'KnowledgeClipper.Speech.Tests.exe'
$fakeToolExe = Join-Path $outputRoot 'KnowledgeClipper.Speech.FakeTool.exe'
$refs = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll')

& $compiler /nologo /optimize+ /target:exe "/out:$workerExe" @refs @sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath $workerExe -Destination $legacyWorkerAlias -Force
& $compiler /nologo /optimize+ /target:exe /main:KnowledgeClipper.Speech.Tests.Tests "/out:$testsExe" @refs @sources @tests
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $compiler /nologo /optimize+ /target:exe /main:KnowledgeClipper.Speech.Tests.FakeWorkerTool "/out:$fakeToolExe" @refs (Join-Path $testRoot 'FakeWorkerTool.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $testsExe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$unitRoot = (Get-ChildItem -LiteralPath (Join-Path $outputRoot 'out') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
python (Join-Path $testRoot 'validate-settings-companion.py') --project-root $projectRoot --unit-root $unitRoot --worker-exe $workerExe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python (Join-Path $testRoot 'validate-public-contracts.py') --project-root $projectRoot --unit-root $unitRoot --worker-exe $workerExe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $testRoot 'validate-destination-neutral-identity.ps1') -WorkerExe $workerExe -LegacyWorkerAlias $legacyWorkerAlias
exit $LASTEXITCODE

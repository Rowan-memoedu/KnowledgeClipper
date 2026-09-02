$ErrorActionPreference = 'Stop'

# These core executable names belong to the frozen legacy checkpoint only. They are
# compatibility inputs and are not the public identity of the speech candidate.

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
$contractExe = Join-Path $projectRoot 'tests\Orchestrator.ContractTests\bin\Release\net8.0\Orchestrator.ContractTests.exe'
$contractDll = Join-Path $projectRoot 'tests\Orchestrator.ContractTests\bin\Release\net8.0\Orchestrator.ContractTests.dll'
$orchestratorDll = Join-Path $projectRoot 'tests\Orchestrator.ContractTests\bin\Release\net8.0\RemNoteQuickClipper.Orchestrator.dll'
$fakeWorker = Join-Path $projectRoot 'tests\FakeWorker\bin\Release\net8.0\RemNoteQuickClipper.FakeWorker.exe'
$validator = Join-Path $testRoot 'validate-core-reuse-compatibility.py'

foreach ($required in @($contractExe, $contractDll, $orchestratorDll, $fakeWorker, $validator)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing compatibility dependency: $required" }
}

function Assert-BinaryFresh([string]$label, [string]$binary, [IO.FileInfo[]]$sources) {
    $binaryTime = (Get-Item -LiteralPath $binary).LastWriteTimeUtc
    foreach ($source in $sources) {
        if ($source.LastWriteTimeUtc -gt $binaryTime) {
            throw "$label binary is stale relative to source: $($source.FullName)"
        }
    }
}

$contractSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests\Orchestrator.ContractTests') -File |
    Where-Object { $_.Extension -in @('.cs', '.csproj') })
$orchestratorSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'orchestrator') -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs', '.csproj') })
$fakeWorkerSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests\FakeWorker') -File |
    Where-Object { $_.Extension -in @('.cs', '.csproj') })
Assert-BinaryFresh 'Orchestrator.ContractTests' $contractDll $contractSources
Assert-BinaryFresh 'RemNoteQuickClipper.Orchestrator' $orchestratorDll $orchestratorSources
Assert-BinaryFresh 'RemNoteQuickClipper.FakeWorker' $fakeWorker $fakeWorkerSources

$runId = 'r' + (Get-Date -AsUTC -Format 'HHmmss') + ([guid]::NewGuid().ToString('N').Substring(0, 4))
$evidenceRoot = Join-Path $testRoot (Join-Path 'bin\core-reuse-compatibility' $runId)
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$previousReceipt = $env:RQC_CONTRACT_TEST_RECEIPT
$previousEvidence = $env:RQC_REUSE_EVIDENCE_DIR
try {
    $env:RQC_CONTRACT_TEST_RECEIPT = Join-Path $evidenceRoot 'orchestrator-contract-receipt.json'
    $env:RQC_REUSE_EVIDENCE_DIR = $evidenceRoot
    & $contractExe $projectRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    python $validator --evidence-root $evidenceRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    $env:RQC_CONTRACT_TEST_RECEIPT = $previousReceipt
    $env:RQC_REUSE_EVIDENCE_DIR = $previousEvidence
}

Write-Output "EVIDENCE_ROOT $evidenceRoot"
exit 0

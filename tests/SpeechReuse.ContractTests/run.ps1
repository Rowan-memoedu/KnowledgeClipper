[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe',
    [Parameter(Mandatory = $true)][string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
if (Test-Path -LiteralPath $ReceiptPath) { throw "Append-only receipt already exists: $ReceiptPath" }

function Invoke-JsonTest([string]$Project) {
    $output = & $DotnetPath run --project $Project -c Release --no-build --no-restore -- $ProjectRoot 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $Project`n$($output -join [Environment]::NewLine)" }
    ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

$orchestrator = Invoke-JsonTest (Join-Path $ProjectRoot 'tests\Orchestrator.ContractTests\Orchestrator.ContractTests.csproj')
$adapter = Invoke-JsonTest (Join-Path $ProjectRoot 'tests\CoreAdapter.IntegrationTests\CoreAdapter.IntegrationTests.csproj')
$reuse = $orchestrator.tests | Where-Object name -eq 'speech-synthesize-reuse-and-playback-no-reuse'
$effect = $orchestrator.tests | Where-Object name -eq 'speech-synthesis-reuse-and-playback-effect'
$replay = $adapter.results | Where-Object name -eq 'stage-scoped-playback-stop-and-replay'
if ($orchestrator.status -ne 'passed' -or $adapter.status -ne 'passed' -or
    $reuse.status -ne 'passed' -or $effect.status -ne 'passed' -or $replay.status -ne 'passed') {
    throw 'Speech reuse or replay evidence is incomplete.'
}

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-speech-reuse-receipt.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    synthesize = [ordered]@{
        firstAttemptId = $effect.synthesis.firstAttemptId
        reusedAttemptId = $effect.synthesis.reusedAttemptId
        envelopeOperationId = $effect.synthesis.envelopeOperationId
        payloadOperationId = $effect.synthesis.payloadOperationId
        cacheHit = $effect.synthesis.cacheHit
        reusedFromAttemptId = $effect.synthesis.reusedFromAttemptId
        workerStarts = $effect.synthesis.workerStarts
        artifactRef = $effect.synthesis.artifactRef
        artifactSha256 = $effect.synthesis.artifactSha256
    }
    playback = [ordered]@{
        firstAttemptId = $effect.playback.firstAttemptId
        secondAttemptId = $effect.playback.secondAttemptId
        workerStarts = $effect.playback.workerStarts
        fromStart = $effect.playback.fromStart
        resultReuse = $effect.playback.resultReuse
    }
    replay = [ordered]@{
        runId = $replay.replay.runId
        attemptId = $replay.replay.attemptId
        stages = @($replay.replay.stages)
        workerStarts = $replay.replay.workerStarts
        synthesisRuns = $replay.replay.synthesisRuns
        contentRuns = $replay.replay.contentRuns
        exportRuns = $replay.replay.exportRuns
    }
    isolation = [ordered]@{
        nonSpeechPayloadUntouched = $effect.nonSpeechPayloadUntouched
    }
    source = [ordered]@{
        engine = [ordered]@{
            path = (Join-Path $ProjectRoot 'orchestrator\Core\OrchestratorEngine.cs')
            sha256 = (Get-FileHash -LiteralPath (Join-Path $ProjectRoot 'orchestrator\Core\OrchestratorEngine.cs') -Algorithm SHA256).Hash
        }
        host = [ordered]@{
            path = (Join-Path $ProjectRoot 'orchestrator\Core\OrchestratorHost.cs')
            sha256 = (Get-FileHash -LiteralPath (Join-Path $ProjectRoot 'orchestrator\Core\OrchestratorHost.cs') -Algorithm SHA256).Hash
        }
    }
}

$json = $receipt | ConvertTo-Json -Depth 16
$receiptDirectory = Split-Path -Parent $ReceiptPath
[IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
$temporary = Join-Path $receiptDirectory ('.' + [IO.Path]::GetFileName($ReceiptPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $ReceiptPath)
}
finally {
    if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
}
$json

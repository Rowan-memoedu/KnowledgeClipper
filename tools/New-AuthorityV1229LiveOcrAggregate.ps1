[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$EvidenceRoot = 'canary\sealed-live-ocr-anki-v1.2.29\20260901T025000Z',
    [string]$AdditionalWaitingEvidenceRoot = 'canary\sealed-live-ocr-anki-v1.2.29\20260901T030000Z'
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$EvidenceRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot $EvidenceRoot))
$AdditionalWaitingEvidenceRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot $AdditionalWaitingEvidenceRoot))
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = Sha $item.FullName }
}
function ReadJson([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100 }

$authority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.29-20260901T023500Z'
$release = Join-Path $ProjectRoot 'releases\knowledge-clipper-authority-v1.2.29-20260901T023500Z\win-x64'
$registry = Join-Path $authority 'worker-registry.v1.json'
$checkpoint = Join-Path $authority 'checkpoint.json'
$initialPath = Join-Path $EvidenceRoot 'initial-receipt.json'
$startupFailurePath = Join-Path $EvidenceRoot 'continuation\failure-receipt.json'
$continuationPath = Join-Path $EvidenceRoot 'continuation-retry-absolute-ipc\continuation-receipt.json'
$readablePath = Join-Path $EvidenceRoot 'continuation-retry-absolute-ipc\READABLE.md'
$additionalInitialPath = Join-Path $AdditionalWaitingEvidenceRoot 'initial-receipt.json'
$output = Join-Path $EvidenceRoot 'aggregate-receipt.json'
foreach ($path in @($registry,$checkpoint,$initialPath,$startupFailurePath,$continuationPath,$readablePath,$additionalInitialPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required aggregation input missing: $path" }
}
if (Test-Path -LiteralPath $output) { throw "Append-only aggregate already exists: $output" }

$initial = ReadJson $initialPath
$startupFailure = ReadJson $startupFailurePath
$continuation = ReadJson $continuationPath
$additionalInitial = ReadJson $additionalInitialPath
if ($initial.status -ne 'waiting_input' -or $initial.sideEffects.mineruSubmissions -ne 1 -or $initial.sideEffects.ankiWrites -ne 0) {
    throw 'Initial unresolved boundary is not intact.'
}
if ($startupFailure.error -notmatch 'orchestrator exited during startup' -or @($startupFailure.resultInventory).Count -ne 2) {
    throw 'The preserved startup failure is not the expected pre-command harness failure.'
}
if ($continuation.status -ne 'passed' -or $continuation.task.taskId -ne $initial.task.taskId -or
    $continuation.task.ocrRerun -ne $false -or $continuation.counts.after.image -ne 1 -or
    $continuation.counts.after.content -ne 2 -or $continuation.counts.after.anki -ne 1 -or
    $continuation.counts.after.newNotes -ne 1 -or $continuation.counts.replay.newAttempts -ne 0 -or
    $continuation.counts.replay.newNotes -ne 0 -or $continuation.sideEffects.remnoteWrites -ne 0 -or
    $continuation.sideEffects.speakerInvocations -ne 0) {
    throw 'Continuation/replay evidence is incomplete or drifted.'
}
if ($additionalInitial.status -ne 'waiting_input' -or $additionalInitial.sideEffects.ankiWrites -ne 0) {
    throw 'Additional waiting task must remain destination-write-free.'
}

$resultEvidence = @(
    Get-ChildItem -LiteralPath $initial.task.jobRoot -Recurse -Filter result.json |
        ForEach-Object {
            $result = ReadJson $_.FullName
            if ($null -ne $result.worker.id) {
                [ordered]@{ workerId=$result.worker.id; workerVersion=$result.worker.version; status=$result.status; runId=$result.runId; stageId=$result.stageId; attemptId=$result.attemptId; result=Evidence $_.FullName }
            }
        }
)
$receipt = [ordered]@{
    schemaVersion = 'sealed-live-ocr-human-resolution-anki-v1.2.29.aggregate.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    scope = 'sealed-authority-orchestrator-process; synthetic image; isolated Anki deck; no RemNote or speaker'
    authority = [ordered]@{
        version = '1.2.29'
        registry = Evidence $registry
        checkpoint = Evidence $checkpoint
        releaseRoot = $release
        portableSchemaClosure = Evidence (Join-Path $authority 'portable-schema-closure-gate.v1.json')
    }
    originalTask = [ordered]@{
        initial = Evidence $initialPath
        taskId = $initial.task.taskId
        statusBeforeHumanDecision = $initial.status
        projection = $initial.projection
        sideEffects = $initial.sideEffects
    }
    preservedHarnessFailure = [ordered]@{
        receipt = Evidence $startupFailurePath
        classification = 'pre-command local relative-ipc-path failure; no task.provide_input accepted; no destination effect'
    }
    humanContinuation = [ordered]@{
        receipt = Evidence $continuationPath
        readable = Evidence $readablePath
        decision = $continuation.task.decision
        targetToken = $continuation.task.targetToken
        sourceTextPreserved = $continuation.task.sourceTextPreserved
        ocrRerun = $continuation.task.ocrRerun
        resultInventory = $resultEvidence
        ankiExactReadback = $continuation.ankiReadback
        replay = $continuation.counts.replay
        sideEffects = $continuation.sideEffects
    }
    additionalWaitingTask = [ordered]@{
        receipt = Evidence $additionalInitialPath
        taskId = $additionalInitial.task.taskId
        status = 'waiting_input'
        reason = 'Created before the first-task reuse instruction was received; retained as append-only evidence and never continued.'
        destinationWrites = 0
    }
    boundaries = [ordered]@{
        remnoteWrites = 0
        speakerInvocations = 0
        unknownExternalStateAutomaticallyRetried = $false
        desktopUiE2E = 'not asserted; this is a sealed Orchestrator backend process run'
    }
}
[IO.File]::WriteAllText($output, (($receipt | ConvertTo-Json -Depth 100) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[ordered]@{status=$receipt.status;receipt=$output;sha256=Sha $output;taskId=$initial.task.taskId;noteIds=$continuation.ankiReadback.newNoteIds} | ConvertTo-Json -Depth 20

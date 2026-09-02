[CmdletBinding()]
param(
    [string]$EvidenceRoot = (Join-Path $PSScriptRoot '..\canary\sealed-original-anki-v1.2.16\20260830T130000Z')
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$originalReceiptPath = Join-Path $evidence 'execution-receipt.json'
$supplementalPath = Join-Path $evidence 'execution-receipt.identity-and-side-effects-correction.v1.json'

if (Test-Path -LiteralPath $supplementalPath) {
    throw "SUPPLEMENTAL_RECEIPT_EXISTS: $supplementalPath"
}

function File-Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $item.FullName
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$original = Get-Content -Raw -LiteralPath $originalReceiptPath | ConvertFrom-Json -Depth 100
$operationsPath = Get-ChildItem -LiteralPath (Join-Path $evidence 'jobs') -Recurse -Filter 'operations.ndjson' -File -ErrorAction Stop |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $operationsPath) { throw 'OPERATIONS_MISSING' }

$operations = @(Get-Content -LiteralPath $operationsPath | ForEach-Object { $_ | ConvertFrom-Json })
$succeeded = @($operations | Where-Object status -eq 'SUCCEEDED')
$mutationNames = @('createModel','storeMediaFile','addNote','createDeck','updateNoteFields','changeDeck','deleteNotes','deleteMediaFile')
$mutations = @($succeeded | Where-Object { $_.action -in $mutationNames })
$mutationCounts = [ordered]@{}
foreach ($name in $mutationNames) {
    $count = @($mutations | Where-Object action -eq $name).Count
    if ($count -gt 0) { $mutationCounts[$name] = $count }
}

$registryPath = Join-Path $repo 'contracts\authority\worker-registry\v1.2.16-20260830T125200Z\worker-registry.v1.json'
$checkpointPath = Join-Path $repo 'contracts\authority\worker-registry\v1.2.16-20260830T125200Z\checkpoint.json'
$orchestratorPath = Join-Path $repo 'releases\knowledge-clipper-authority-v1.2.16-20260830T125200Z\win-x64\orchestrator\KnowledgeClipper.Orchestrator.exe'

$expected = [ordered]@{
    registry = '42250cc251eea1bebe2cb4e072e19c73f7cf25417010b5c33c922755ea46e757'
    checkpoint = '48d6e25e967f73f1d0489df1a6ba8c8f93212b5fad1fad832c5c09a3d58ac046'
    orchestrator = '1548acb0f950e6103c89635e2698004685bc8b709d67d36221d22e6951385db0'
}
$registry = File-Evidence $registryPath
$checkpoint = File-Evidence $checkpointPath
$orchestrator = File-Evidence $orchestratorPath
if ($registry.sha256 -ne $expected.registry -or $checkpoint.sha256 -ne $expected.checkpoint -or $orchestrator.sha256 -ne $expected.orchestrator) {
    throw 'AUTHORITY_IDENTITY_DRIFT'
}
if ($original.anki.noteIds.Count -ne 1 -or $original.anki.cardIds.Count -ne 1 -or -not $original.anki.exactOrderReadback) {
    throw 'ORIGINAL_READBACK_NOT_EXACT'
}
if ($mutations.Count -ne 5 -or $mutationCounts.createModel -ne 1 -or $mutationCounts.storeMediaFile -ne 3 -or $mutationCounts.addNote -ne 1) {
    throw 'ANKI_MUTATION_COUNT_DRIFT'
}

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-evidence-correction.v1'
    status = 'passed'
    correctionKind = 'append-only-metadata-correction'
    originalReceipt = File-Evidence $originalReceiptPath
    corrections = [ordered]@{
        authorityVersion = [ordered]@{
            recorded = $original.authority.version
            corrected = '1.2.16'
            reason = 'The original receipt embedded the prior literal version while every bound registry/checkpoint/orchestrator path and digest was v1.2.16.'
        }
        ankiExternalMutations = [ordered]@{
            recorded = $original.sideEffects.ankiWrites
            correctedTotal = $mutations.Count
            countsByAction = $mutationCounts
            reason = 'The original count represented added notes only; this correction records every successful mutating AnkiConnect action.'
        }
    }
    authority = [ordered]@{
        version = '1.2.16'
        registry = $registry
        checkpoint = $checkpoint
        orchestrator = $orchestrator
        releaseTreeSha256 = $original.authority.releaseTreeSha256
    }
    operations = [ordered]@{
        evidence = File-Evidence $operationsPath
        succeededTotal = $succeeded.Count
        mutatingTotal = $mutations.Count
        countsByAction = $mutationCounts
    }
    exactReadback = [ordered]@{
        deck = $original.anki.deck
        noteIds = @($original.anki.noteIds)
        cardIds = @($original.anki.cardIds)
        mediaOrderPassed = [bool]$original.anki.exactOrderReadback
        replayNewAttempts = [int]$original.replay.newAttempts
        replayNewNotes = [int]$original.replay.newNotes
    }
    unchangedBoundaries = [ordered]@{
        remnoteWrites = [int]$original.sideEffects.remnoteWrites
        mineruCalls = [int]$original.sideEffects.mineruCalls
        deepSeekCalls = [int]$original.sideEffects.deepSeekCalls
        speakerCalls = [int]$original.sideEffects.speaker
    }
    completedAt = [DateTimeOffset]::UtcNow
}

$json = $receipt | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($supplementalPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$result = File-Evidence $supplementalPath
$result | ConvertTo-Json -Depth 10

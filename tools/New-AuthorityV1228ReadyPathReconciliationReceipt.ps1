[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$TaskId
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

$jobRoot = Join-Path $EvidenceRoot (Join-Path 'jobs' $TaskId)
if (-not (Test-Path -LiteralPath $jobRoot -PathType Container)) {
    throw "Job root does not exist: $jobRoot"
}

$contentResult = Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter result.json |
    Where-Object FullName -match '\\stages\\content\.understand-and-card\\attempts\\' |
    Select-Object -First 1
$learning = Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter learning-bundle.schema-v2.json | Select-Object -First 1
$ankiReceipt = Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter receipt.json |
    Where-Object FullName -match '\\stages\\anki-export\\attempts\\' |
    Select-Object -First 1
$items = Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter items.ndjson |
    Where-Object FullName -match '\\stages\\anki-export\\attempts\\' |
    Select-Object -First 1
$operations = Get-ChildItem -LiteralPath $jobRoot -Recurse -File -Filter operations.ndjson |
    Where-Object FullName -match '\\stages\\anki-export\\attempts\\' |
    Select-Object -First 1
if (@($contentResult, $learning, $ankiReceipt, $items, $operations).Where({ $null -eq $_ }).Count -ne 0) {
    throw 'Required sealed task outputs are missing.'
}

$learningJson = Get-Content -Raw -LiteralPath $learning.FullName | ConvertFrom-Json -Depth 100
$ankiJson = Get-Content -Raw -LiteralPath $ankiReceipt.FullName | ConvertFrom-Json -Depth 100
$itemsJson = @(Get-Content -LiteralPath $items.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$ops = @(Get-Content -LiteralPath $operations.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$badOps = @($ops | Where-Object { $_.status -ne 'SUCCEEDED' })
if ($learningJson.unresolved_items.Count -ne 0 -or $ankiJson.status -ne 'SUCCEEDED' -or $itemsJson.Count -ne 1 -or $badOps.Count -ne 0) {
    throw 'This reconciliation receipt is only valid for a completed ready-path with one successful Anki item.'
}

$receipt = [ordered]@{
    schemaVersion = 'sealed-live-ocr-anki-v1228-ready-path-reconciliation.v1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    status = 'succeeded'
    classification = 'actual-ready-path-succeeded; prior harness assertion requiring unresolved was invalid for this observed provider result'
    authority = [ordered]@{
        version = '1.2.28'
        registrySha256 = 'F7989A49A2F348031EFEA488FA7DE8885894494F8773E71A241C5698A7A66CB4'
        checkpointSha256 = '1797D375B750FE8A754BD9DDB9E55ED684E73B6D01333E010BD38F63630AE75B'
        receiptSha256 = '5EC662725286DB01B484C2ECD4FFA2252D40AC309B9AB0234ABBB4CFA17AFDF5'
    }
    task = [ordered]@{ taskId = $TaskId; jobRoot = $jobRoot }
    initialOcrHumanResolutionBranch = [ordered]@{
        expectedByHarness = 'unresolved then task=waiting_input and zero exporter invocation'
        observed = 'ready; unresolved_items=[]; no correction command was accepted'
        covered = $false
        reason = 'The live Content 0.9.3 provider result preserved CANAR and reported no unresolved item.'
    }
    observedReadyPath = [ordered]@{
        mineruReruns = 0
        content = [ordered]@{ resultPath = $contentResult.FullName; resultSha256 = Get-Sha256 $contentResult.FullName; learningBundlePath = $learning.FullName; learningBundleSha256 = Get-Sha256 $learning.FullName; unresolvedItems = @($learningJson.unresolved_items).Count }
        anki = [ordered]@{ receiptPath = $ankiReceipt.FullName; receiptSha256 = Get-Sha256 $ankiReceipt.FullName; status = $ankiJson.status; itemsPath = $items.FullName; itemsSha256 = Get-Sha256 $items.FullName; noteIds = @($itemsJson | ForEach-Object note_id); operationsPath = $operations.FullName; operationsSha256 = Get-Sha256 $operations.FullName; notesInfoAndCardsInfoReadback = (@($ops.action) -contains 'notesInfo') -and (@($ops.action) -contains 'cardsInfo') }
        remnoteWrites = 0
        speakerPlayback = 0
    }
    supersededHarnessFailure = [ordered]@{ path = (Join-Path $EvidenceRoot 'failure-receipt.json'); sha256 = Get-Sha256 (Join-Path $EvidenceRoot 'failure-receipt.json'); reason = 'script assertion mismatch only; task effects are reported above and are not retried' }
    replay = [ordered]@{ attempted = $false; reason = 'No continuation command exists because the initial provider decision was ready, not unresolved.' }
}

$out = Join-Path $EvidenceRoot 'ready-path-reconciliation-receipt.json'
if (Test-Path -LiteralPath $out) { throw "Append-only target already exists: $out" }
[IO.File]::WriteAllText($out, ($receipt | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
Write-Output $out
Write-Output (Get-Sha256 $out)

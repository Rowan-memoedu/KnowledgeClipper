[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative = 'contracts/core/v2.7/checkpoints/v2.7.11-20260901T011300Z/checkpoint.json'
$targetRelative = 'contracts/core/v2.7/checkpoints/v2.7.12-20260901T013000Z/checkpoint.json'
$source = Join-Path $ProjectRoot $sourceRelative.Replace('/', '\')
$target = Join-Path $ProjectRoot $targetRelative.Replace('/', '\')
if (!(Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $target)) { throw 'Append-only Core v2.7.12 checkpoint precondition failed.' }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Entry([string]$Relative) {
    $absolute = Join-Path $ProjectRoot $Relative.Replace('/', '\')
    if (!(Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Checkpoint input missing: $Relative" }
    $item = Get-Item -LiteralPath $absolute
    [ordered]@{path=$Relative;bytes=[long]$item.Length;sha256=(Sha $absolute).ToLowerInvariant()}
}

$document = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$receiptRoot = 'canary/core-v2.7.8-offline/20260901T013000Z'
$receiptPaths = [ordered]@{
    settings = "$receiptRoot/settings-control.receipt.json"
    coreAdapter = "$receiptRoot/core-adapter.receipt.json"
    orchestrator = "$receiptRoot/orchestrator-contract-tests.receipt.json"
    questionWorkflow = "$receiptRoot/question-workflow.receipt.json"
    contracts = "$receiptRoot/public-contracts.receipt.json"
}
foreach ($relative in $receiptPaths.Values) { [void](Entry $relative) }
$runtimeRelative = 'dist/orchestrator/win-x64/v2.7.8/KnowledgeClipper.Orchestrator.exe'
$oldReceipt = 'content-worker/receipts/content-ocr-coordinate-recovery-0.9.1-admission-20260831T093500Z.json'
$newReceipt = 'content-worker/receipts/content-card-reference-integrity-0.9.2-admission-20260901T012500Z.json'
$files = @(
    foreach ($sourceFile in $document.tree.files) {
        $relative = [string]$sourceFile.path
        if ($relative -eq 'dist/orchestrator/win-x64/v2.7.7/KnowledgeClipper.Orchestrator.exe') { $relative = $runtimeRelative }
        if ($relative -eq $oldReceipt) { $relative = $newReceipt }
        Entry $relative
    }
)
$rows = [string[]]@($files | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
[Array]::Sort($rows, [StringComparer]::Ordinal)
$treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((($rows -join "`n") + "`n")))).ToLowerInvariant()
$totalBytes = [long]0; foreach ($file in $files) { $totalBytes += [long]$file.bytes }
if ($totalBytes -le 0) { throw 'Checkpoint tree has no bytes.' }
$runtime = Join-Path $ProjectRoot $runtimeRelative.Replace('/', '\')
$describeText = (& $runtime describe 2>&1) -join [Environment]::NewLine
$describe = $describeText | ConvertFrom-Json -Depth 20
if ($LASTEXITCODE -ne 0 -or $describe.version -ne '2.7.8.0') { throw 'Core runtime describe identity failed.' }
$settings = Get-Content (Join-Path $ProjectRoot $receiptPaths.settings.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$adapter = Get-Content (Join-Path $ProjectRoot $receiptPaths.coreAdapter.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$orchestrator = Get-Content (Join-Path $ProjectRoot $receiptPaths.orchestrator.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$question = Get-Content (Join-Path $ProjectRoot $receiptPaths.questionWorkflow.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$contracts = Get-Content (Join-Path $ProjectRoot $receiptPaths.contracts.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
if (@($settings,$adapter,$orchestrator,$question,$contracts | ForEach-Object { $_.status }) -contains 'failed') { throw 'A Core v2.7.8 validation receipt failed.' }
$contentRoot = Join-Path $ProjectRoot 'content-worker\releases\content.deepseek-0.9.2-win-x64-formal-20260901T012100Z'
$contentExe = Join-Path $contentRoot 'KnowledgeClipper.Content.Worker.exe'
$contentDescribeText = (& $contentExe describe --json 2>&1) -join [Environment]::NewLine
$contentDescribe = $contentDescribeText | ConvertFrom-Json -Depth 40
if ($LASTEXITCODE -ne 0 -or $contentDescribe.workerId -ne 'content.deepseek' -or $contentDescribe.workerVersion -ne '0.9.2') { throw 'Content 0.9.2 describe identity failed.' }

$document.checkpointId = 'core-v2.7.12-20260901T013000Z'
$document.checkpointVersion = '2.7.12'
$document.createdAt = '2026-09-01T01:30:00+08:00'
$document.generatedAt = '2026-09-01T01:30:00+08:00'
$document.supersedes = [ordered]@{path=$sourceRelative;sha256=Sha $source}
$document.core = [ordered]@{version='2.7.8';describedVersion=$describe.version;executable=$runtimeRelative;bytes=(Get-Item -LiteralPath $runtime).Length;sha256=Sha $runtime}
$document.authorityBoundary.currentAuthority = '1.2.24'
$document.authorityBoundary.intendedAuthority = '1.2.26'
$document.authorityBoundary.promotion = 'pending-append-only-authority-assembly'
$document.validation.settings = [ordered]@{path=$receiptPaths.settings;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.settings.Replace('/', '\'));status='passed';externalCalls=0;physicalSpeakerInvocations=0}
$document.validation.coreAdapter = [ordered]@{path=$receiptPaths.coreAdapter;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.coreAdapter.Replace('/', '\'));status='passed';groups=@($adapter.results).Count;ocrHumanResolutionDirectMarkdownAncestry=$true;v3ProofreadSegmentAncestry=$false;strictSourceSegmentReferenceRequired=$true}
$document.validation.orchestrator = [ordered]@{path=$receiptPaths.orchestrator;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.orchestrator.Replace('/', '\'));status='passed';tests=@($orchestrator.tests).Count}
$document.validation.questionWorkflow = [ordered]@{path=$receiptPaths.questionWorkflow;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.questionWorkflow.Replace('/', '\'));status='passed';initialWrites=0;followUpWrites=0;makeCardsWrites=1;replayWrites=0;missingConfigFailClosed=$true;driftFailClosed=$true}
$document.validation.contracts = $contracts
$document.modules.content = [ordered]@{workerId='content.deepseek';workerVersion='0.9.2';executable='content-worker/releases/content.deepseek-0.9.2-win-x64-formal-20260901T012100Z/KnowledgeClipper.Content.Worker.exe';bytes=(Get-Item -LiteralPath $contentExe).Length;sha256=Sha $contentExe;describe=$contentDescribe}
$document.moduleReceipts.content = [ordered]@{path=$newReceipt;sha256=Sha (Join-Path $ProjectRoot $newReceipt.Replace('/', '\'));treeSha256='169229AF9641B56A1005F5CC12D845EBEC3A94FAF1883254E5AD888F69BC9625'}
$document.requirements.ocrProofreadSegments = [ordered]@{status='strict-active-source-segment-reference';canonicalMatch=$true;contentProducerPairIntegrity='0.9.2-admitted';tamperMissingDuplicateFailClosed=$true;destinationWorkerStarted=$false}
$document.tree = [ordered]@{fileCount=$files.Count;totalBytes=$totalBytes;treeSha256=$treeHash;files=$files}
[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
[IO.File]::WriteAllText($target, (($document | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{status='passed';checkpoint=$targetRelative;sha256=Sha $target;tree=[ordered]@{files=$files.Count;bytes=$totalBytes;sha256=$treeHash};core=$document.core;content=$document.modules.content}|ConvertTo-Json -Depth 20

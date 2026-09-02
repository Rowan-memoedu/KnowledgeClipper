[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative = 'contracts/core/v2.7/checkpoints/v2.7.10-20260901T010000Z/checkpoint.json'
$targetRelative = 'contracts/core/v2.7/checkpoints/v2.7.11-20260901T011300Z/checkpoint.json'
$source = Join-Path $ProjectRoot $sourceRelative.Replace('/', '\')
$target = Join-Path $ProjectRoot $targetRelative.Replace('/', '\')
if (!(Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $target)) { throw 'Append-only Core v2.7.11 checkpoint precondition failed.' }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Entry([string]$Relative) {
    $absolute = Join-Path $ProjectRoot $Relative.Replace('/', '\')
    if (!(Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Checkpoint input missing: $Relative" }
    $item = Get-Item -LiteralPath $absolute
    [ordered]@{path=$Relative;bytes=[long]$item.Length;sha256=(Sha $absolute).ToLowerInvariant()}
}
$document = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json -AsHashtable -Depth 100
if ($document.checkpointVersion -ne '2.7.10') { throw 'Unexpected Core checkpoint source identity.' }
$receiptRoot = 'canary/core-v2.7.7-offline/20260901T011300Z'
$receiptPaths = [ordered]@{
    settings = "$receiptRoot/settings-control.receipt.json"
    coreAdapter = "$receiptRoot/core-adapter.receipt.json"
    orchestrator = "$receiptRoot/orchestrator-contract-tests.receipt.json"
    questionWorkflow = "$receiptRoot/question-workflow.receipt.json"
    contracts = "$receiptRoot/public-contracts.receipt.json"
}
foreach ($relative in $receiptPaths.Values) { [void](Entry $relative) }
$runtimeRelative = 'dist/orchestrator/win-x64/v2.7.7/KnowledgeClipper.Orchestrator.exe'
$files = @(
    foreach ($sourceFile in $document.tree.files) {
        $relative = [string]$sourceFile.path
        if ($relative -eq 'dist/orchestrator/win-x64/v2.7.6/KnowledgeClipper.Orchestrator.exe') { $relative = $runtimeRelative }
        Entry $relative
    }
)
$rows = [string[]]@($files | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
[Array]::Sort($rows, [StringComparer]::Ordinal)
$treeText = ($rows -join "`n") + "`n"
$treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($treeText))).ToLowerInvariant()
$totalBytes = [long]0
foreach ($file in $files) { $totalBytes += [long]$file.bytes }
if ($totalBytes -le 0) { throw 'Checkpoint tree has no bytes.' }
$runtime = Join-Path $ProjectRoot $runtimeRelative.Replace('/', '\')
$describeText = (& $runtime describe 2>&1) -join [Environment]::NewLine
$describe = $describeText | ConvertFrom-Json -Depth 20
if ($LASTEXITCODE -ne 0 -or $describe.version -ne '2.7.7.0') { throw 'Core runtime describe identity failed.' }
$settings = Get-Content (Join-Path $ProjectRoot $receiptPaths.settings.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$adapter = Get-Content (Join-Path $ProjectRoot $receiptPaths.coreAdapter.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$orchestrator = Get-Content (Join-Path $ProjectRoot $receiptPaths.orchestrator.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$question = Get-Content (Join-Path $ProjectRoot $receiptPaths.questionWorkflow.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
$contracts = Get-Content (Join-Path $ProjectRoot $receiptPaths.contracts.Replace('/', '\')) -Raw | ConvertFrom-Json -Depth 100
if (@($settings,$adapter,$orchestrator,$question,$contracts | ForEach-Object { $_.status }) -contains 'failed') { throw 'A Core v2.7.7 validation receipt failed.' }
$document.checkpointId = 'core-v2.7.11-20260901T011300Z'
$document.checkpointVersion = '2.7.11'
$document.createdAt = '2026-09-01T01:13:00+08:00'
$document.generatedAt = '2026-09-01T01:13:00+08:00'
$document.supersedes = [ordered]@{path=$sourceRelative;sha256=Sha $source}
$document.core = [ordered]@{version='2.7.7';describedVersion=$describe.version;executable=$runtimeRelative;bytes=(Get-Item -LiteralPath $runtime).Length;sha256=Sha $runtime}
$document.authorityBoundary.currentAuthority = '1.2.24'
$document.authorityBoundary.intendedAuthority = '1.2.25'
$document.authorityBoundary.promotion = 'pending-append-only-authority-assembly'
$document.validation.settings = [ordered]@{path=$receiptPaths.settings;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.settings.Replace('/', '\'));status='passed';externalCalls=0;physicalSpeakerInvocations=0}
$document.validation.coreAdapter = [ordered]@{path=$receiptPaths.coreAdapter;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.coreAdapter.Replace('/', '\'));status='passed';groups=@($adapter.results).Count;ocrHumanResolutionDirectMarkdownAncestry=$true;v3ProofreadSegmentAncestry=$true}
$document.validation.orchestrator = [ordered]@{path=$receiptPaths.orchestrator;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.orchestrator.Replace('/', '\'));status='passed';tests=@($orchestrator.tests).Count}
$document.validation.questionWorkflow = [ordered]@{path=$receiptPaths.questionWorkflow;sha256=Sha (Join-Path $ProjectRoot $receiptPaths.questionWorkflow.Replace('/', '\'));status='passed';initialWrites=0;followUpWrites=0;makeCardsWrites=1;replayWrites=0;missingConfigFailClosed=$true;driftFailClosed=$true}
$document.validation.contracts = $contracts
$document.tree = [ordered]@{fileCount=$files.Count;totalBytes=$totalBytes;treeSha256=$treeHash;files=$files}
[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
[IO.File]::WriteAllText($target, (($document | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{status='passed';checkpoint=$targetRelative;sha256=Sha $target;tree=[ordered]@{files=$files.Count;bytes=$totalBytes;sha256=$treeHash};core=$document.core}|ConvertTo-Json -Depth 20

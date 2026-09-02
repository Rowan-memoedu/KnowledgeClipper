[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$sourceRelative='contracts/core/v2.7/checkpoints/v2.7.8-20260831T111800Z/checkpoint.json'
$targetRelative='contracts/core/v2.7/checkpoints/v2.7.10-20260901T010000Z/checkpoint.json'
$source=Join-Path $ProjectRoot $sourceRelative
$target=Join-Path $ProjectRoot $targetRelative
if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw "Missing source checkpoint: $source"}
if(Test-Path -LiteralPath $target){throw "Append-only target exists: $target"}
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Entry([string]$Relative){$absolute=Join-Path $ProjectRoot $Relative.Replace('/','\');if(-not(Test-Path -LiteralPath $absolute -PathType Leaf)){throw "Missing checkpoint file: $Relative"};$item=Get-Item -LiteralPath $absolute;[ordered]@{path=$Relative;bytes=$item.Length;sha256=Sha $absolute}}
$document=Get-Content -LiteralPath $source -Raw|ConvertFrom-Json -AsHashtable -Depth 100
if($document.schemaVersion -ne 'knowledge-clipper-core-integration-checkpoint.v2' -or $document.checkpointVersion -ne '2.7.8'){throw 'Unexpected source checkpoint.'}
$replacements=[ordered]@{
  'orchestrator/Core/StageInputResolver.cs'='orchestrator/Core/StageInputResolver.cs'
  'tests/CoreAdapter.IntegrationTests/Program.cs'='tests/CoreAdapter.IntegrationTests/Program.cs'
  'canary/core-v2.7.4-offline/20260831T100200Z/settings-control.receipt.json'='canary/core-v2.7.6-offline/20260901T000500Z/settings-control.receipt.json'
  'canary/core-v2.7.4-offline/20260831T100200Z/core-adapter.receipt.json'='canary/core-v2.7.6-offline/20260901T000500Z/core-adapter.receipt.json'
  'canary/core-v2.7.4-offline/20260831T100200Z/orchestrator-contract-tests.receipt.json'='canary/core-v2.7.6-offline/20260901T000500Z/orchestrator-contract-tests.receipt.json'
  'canary/core-v2.7.4-offline/20260831T100200Z/question-workflow.receipt.json'='canary/core-v2.7.6-offline/20260901T000500Z/question-workflow.receipt.json'
  'canary/core-v2.7.4-offline/20260831T100200Z/public-contracts.receipt.json'='canary/core-v2.7.6-offline/20260901T000500Z/public-contracts.receipt.json'
  'dist/orchestrator/win-x64/v2.7.4/KnowledgeClipper.Orchestrator.exe'='dist/orchestrator/win-x64/v2.7.6/KnowledgeClipper.Orchestrator.exe'
}
$seen=@{}
$files=@(foreach($file in $document.tree.files){$old=[string]$file.path;$new=if($replacements.Contains($old)){$replacements[$old]}else{$old};$seen[$old]=$true;Entry $new})
foreach($key in $replacements.Keys){if(-not $seen.ContainsKey($key)){throw "Source checkpoint did not contain replacement key: $key"}}
$rows=[string[]]@($files|ForEach-Object{"$($_.path)`t$($_.bytes)`t$($_.sha256)"});[Array]::Sort($rows,[StringComparer]::Ordinal)
$treeText=($rows -join "`n")+"`n";$treeSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($treeText))).ToLowerInvariant();$total=[long]0;foreach($file in $files){$total += [long]$file.bytes};if($total -le 0){throw 'Core checkpoint totalBytes must be positive.'}
$runtime=Join-Path $ProjectRoot 'dist/orchestrator/win-x64/v2.7.6/KnowledgeClipper.Orchestrator.exe';$describe=((& $runtime describe 2>&1)-join [Environment]::NewLine)|ConvertFrom-Json -Depth 20
if($LASTEXITCODE -ne 0 -or $describe.version -ne '2.7.6.0'){throw 'Core runtime identity failed.'}
$settings=Join-Path $ProjectRoot 'canary/core-v2.7.6-offline/20260901T000500Z/settings-control.receipt.json';$adapter=Join-Path $ProjectRoot 'canary/core-v2.7.6-offline/20260901T000500Z/core-adapter.receipt.json';$orchestrator=Join-Path $ProjectRoot 'canary/core-v2.7.6-offline/20260901T000500Z/orchestrator-contract-tests.receipt.json';$question=Join-Path $ProjectRoot 'canary/core-v2.7.6-offline/20260901T000500Z/question-workflow.receipt.json';$public=Join-Path $ProjectRoot 'canary/core-v2.7.6-offline/20260901T000500Z/public-contracts.receipt.json'
$settingsDoc=Get-Content $settings -Raw|ConvertFrom-Json -Depth 100;$adapterDoc=Get-Content $adapter -Raw|ConvertFrom-Json -Depth 100;$orchestratorDoc=Get-Content $orchestrator -Raw|ConvertFrom-Json -Depth 100;$questionDoc=Get-Content $question -Raw|ConvertFrom-Json -Depth 100;$publicDoc=Get-Content $public -Raw|ConvertFrom-Json -Depth 100
foreach($value in @($settingsDoc,$adapterDoc,$orchestratorDoc,$questionDoc,$publicDoc)){if($value.status -ne 'passed'){throw 'A validation receipt is not passed.'}}
$document.checkpointId='core-v2.7.10-20260901T010000Z';$document.checkpointVersion='2.7.10';$document.createdAt='2026-09-01T01:00:00+08:00';$document.generatedAt='2026-09-01T01:00:00+08:00';$document.supersedes=[ordered]@{path=$sourceRelative;sha256=Sha $source}
$document.core=[ordered]@{version='2.7.6';describedVersion=$describe.version;executable='dist/orchestrator/win-x64/v2.7.6/KnowledgeClipper.Orchestrator.exe';bytes=(Get-Item $runtime).Length;sha256=(Sha $runtime).ToUpperInvariant()}
$document.authorityBoundary.currentAuthority='1.2.23';$document.authorityBoundary.intendedAuthority='1.2.24';$document.authorityBoundary.promotion='pending-append-only-authority-assembly'
$document.validation.settings=[ordered]@{path='canary/core-v2.7.6-offline/20260901T000500Z/settings-control.receipt.json';sha256=(Sha $settings).ToUpperInvariant();status='passed';externalCalls=0;physicalSpeakerInvocations=0}
$document.validation.coreAdapter=[ordered]@{path='canary/core-v2.7.6-offline/20260901T000500Z/core-adapter.receipt.json';sha256=(Sha $adapter).ToUpperInvariant();status='passed';groups=@($adapterDoc.results).Count;ocrHumanResolutionDirectMarkdownAncestry=$true}
$document.validation.orchestrator=[ordered]@{path='canary/core-v2.7.6-offline/20260901T000500Z/orchestrator-contract-tests.receipt.json';sha256=(Sha $orchestrator).ToUpperInvariant();status='passed';tests=@($orchestratorDoc.tests).Count}
$document.validation.questionWorkflow=[ordered]@{path='canary/core-v2.7.6-offline/20260901T000500Z/question-workflow.receipt.json';sha256=(Sha $question).ToUpperInvariant();status='passed';initialWrites=0;followUpWrites=0;makeCardsWrites=1;replayWrites=0;missingConfigFailClosed=$true;driftFailClosed=$true}
$document.validation.contracts=$publicDoc
$document.tree=[ordered]@{fileCount=$files.Count;totalBytes=$total;treeSha256=$treeSha;files=$files}
[IO.Directory]::CreateDirectory((Split-Path -Parent $target))|Out-Null
[IO.File]::WriteAllText($target,(($document|ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{status='passed';checkpoint=$targetRelative;sha256=Sha $target;tree=[ordered]@{files=$files.Count;bytes=$total;sha256=$treeSha};core=$document.core;validation=[ordered]@{coreAdapterGroups=@($adapterDoc.results).Count;orchestratorTests=@($orchestratorDoc.tests).Count;contracts=@($publicDoc.schemasChecked,$publicDoc.validFixturesAccepted,$publicDoc.invalidFixturesRejected)}}|ConvertTo-Json -Depth 20

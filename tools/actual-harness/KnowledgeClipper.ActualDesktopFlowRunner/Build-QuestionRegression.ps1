#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputRoot=('D:\Artifacts\KnowledgeClipper\read-to-anki-runner\question-regression-'+[datetime]::UtcNow.ToString('yyyyMMddTHHmmssZ')))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($OutputRoot)
if(-not$root.StartsWith('D:\Artifacts\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $root)){throw 'question-release-root-must-be-new-D-artifact'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$dotnet='D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$project=Join-Path $PSScriptRoot 'KnowledgeClipper.ActualDesktopFlowRunner.csproj'
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function WriteJson([string]$path,[object]$value){$json=ConvertTo-Json $value -Depth 40;[IO.File]::WriteAllText($path,$json+"`n",[Text.UTF8Encoding]::new($false))}
$sources=@(Get-ChildItem -LiteralPath $PSScriptRoot -File|Where-Object Extension -In '.cs','.csproj','.ps1','.md'|ForEach-Object{[ordered]@{path=$_.FullName;sha256=Hash $_.FullName}})
try {
    Write-Output 'PHASE publish test-only Question runner'
    & $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore --nologo -o (Join-Path $root 'app') /p:PublishSingleFile=false
    if($LASTEXITCODE-ne0){throw 'question-runner-publish-failed'}
    $exe=Join-Path $root 'app/KnowledgeClipper.ActualDesktopFlowRunner.exe'
    $evidence=Join-Path $root 'evidence';[IO.Directory]::CreateDirectory($evidence)|Out-Null
    & $exe --question-regression-plan 1> (Join-Path $evidence 'plan.json') 2> (Join-Path $evidence 'plan.stderr.log')
    if($LASTEXITCODE-ne0){throw 'question-plan-failed'}
    Write-Output 'PHASE Question and rendered identity offline cases'
    & $exe --question-regression-self-test 1> (Join-Path $evidence 'self-test.json') 2> (Join-Path $evidence 'self-test.stderr.log')
    if($LASTEXITCODE-ne0){throw 'question-self-test-failed'}
    $test=Get-Content -Raw (Join-Path $evidence 'self-test.json')|ConvertFrom-Json
    if($test.status-ne'PASS'-or$test.questionCases-ne20-or$test.previewBarrierCases-ne9-or$test.externalCalls-ne0){throw 'question-self-test-gate-failed'}
    Write-Output ('PASS Question cases '+$test.questionCases+', preview barrier '+$test.previewBarrierCases)
    & $exe --question-disk-proof-self-test (Join-Path $evidence 'disk-proof') 1> (Join-Path $evidence 'disk-proof.json') 2> (Join-Path $evidence 'disk-proof.stderr.log')
    if($LASTEXITCODE-ne0){throw 'question-disk-proof-test-failed'}
    $disk=Get-Content -Raw (Join-Path $evidence 'disk-proof.json')|ConvertFrom-Json
    if($disk.status-ne'PASS'-or$disk.diskProofCases-ne7-or$disk.externalCalls-ne0){throw 'question-disk-proof-gate-failed'}
    Write-Output 'PASS disk proof 7 cases'
    foreach($source in $sources){if((Hash $source.path)-ne$source.sha256){throw 'question-source-drift-during-publish'}}
    if((Hash (Join-Path $root 'app/KnowledgeClipper.Core.V3.Client.dll'))-ne'FBE7A8732D155EEC1CDF1F6D2F950D5E3653D1033AA5A34C5B98439768949FB5'){throw 'question-sdk-drift'}
    if((Hash (Join-Path $root 'app/KnowledgeClipper.Core.V3.Contracts.dll'))-ne'EAFEED544C951E625D09CE92B8578200E3DA24F1F89F55FDE2B547E52CBD12D6'){throw 'question-contract-drift'}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'QUESTION-REGRESSION.md') -Destination (Join-Path $root 'QUESTION-REGRESSION.md')
    $files=@(Get-ChildItem -LiteralPath $root -Recurse -File|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=Hash $_.FullName}}|Sort-Object path)
    [long]$bytes=0;foreach($file in $files){$bytes+=$file.bytes}
    WriteJson (Join-Path $root 'runner-question-release-manifest.v1.json') ([ordered]@{schemaVersion='knowledge-clipper-question-runner-release.v1';status='offline-prepared';createdAt=[datetimeoffset]::UtcNow;runtime='win-x64-self-contained';entrypoint='app/KnowledgeClipper.ActualDesktopFlowRunner.exe';runnerDllSha256=Hash (Join-Path $root 'app/KnowledgeClipper.ActualDesktopFlowRunner.dll');qualification=[ordered]@{questionCases=20;previewBarrierCases=9;diskProofCases=7;liveExecuted=$false;uiEventExecutionValidated=$false;fullReadClipMatrixReexecuted=$false;externalCalls=0};sources=$sources;payload=[ordered]@{files=$files.Count;bytes=$bytes};files=$files})
    Write-Output ('STATUS OFFLINE_PREPARED '+$root)
    Write-Output ('MANIFEST SHA256 '+(Hash (Join-Path $root 'runner-question-release-manifest.v1.json')))
} catch {Write-Warning ('Unpromoted failure evidence retained: '+$root);throw}

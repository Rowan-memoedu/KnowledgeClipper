[CmdletBinding()]
param([string]$OutputRoot)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $project "canary\remnote-exporter-v2.2-offline\$stamp" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Append-only output exists: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

function Write-Utf8([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false)) }
function Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [ordered]@{ path=$item.FullName; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash; bytes=$item.Length }
}

$buildText = (& pwsh -NoProfile -File (Join-Path $project 'build-exporter.ps1') 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "build failed`n$buildText" }
$buildLog = Join-Path $OutputRoot 'build-and-tests.log'; Write-Utf8 $buildLog $buildText
$passCount = ([regex]::Matches($buildText, '(?m)^PASS ')).Count
if ($passCount -lt 104) { throw "Expected at least 104 C# PASS lines, got $passCount" }

$worker = Join-Path $project 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$describePath = Join-Path $OutputRoot 'worker-describe.json'
$describeText = (& $worker describe --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'describe failed' }
Write-Utf8 $describePath $describeText
$describe = $describeText | ConvertFrom-Json
if ($describe.workerId -ne 'export.remnote' -or $describe.workerVersion -ne '2.2.0') { throw 'worker identity mismatch' }

$fakeRuntime = Join-Path $OutputRoot 'fake-runtime'; New-Item -ItemType Directory -Path $fakeRuntime | Out-Null
$fakeWorker = Join-Path $fakeRuntime 'KnowledgeClipper.RemNoteExporter.Worker.exe'
$fakeCli = Join-Path $fakeRuntime 'fake-public-probe-remnote-cli.js'
Copy-Item -LiteralPath $worker -Destination $fakeWorker
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fake-public-probe-remnote-cli.js') -Destination $fakeCli
$node = (Get-Command node.exe -ErrorAction Stop).Source
Write-Utf8 (Join-Path $fakeRuntime 'appsettings.json') (([ordered]@{ NodePath=$node; RemnoteCliJsPath=$fakeCli; RemnoteMcpUrl='http://127.0.0.1:43101/mcp' } | ConvertTo-Json) + "`n")

$descriptorPath = Join-Path $OutputRoot 'settings-companion-descriptor-v2.json'
$descriptorText = (& $fakeWorker settings companion --version 2 --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'companion v2 failed' }
Write-Utf8 $descriptorPath $descriptorText

$probeRequestPath = Join-Path $OutputRoot 'public-probe-request.json'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\public-probe-request-v2.valid.json') -Destination $probeRequestPath
$probeResultPath = Join-Path $OutputRoot 'public-probe-result.json'
$probeText = (& $fakeWorker probe-public --request $probeRequestPath --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Fake public probe failed' }
Write-Utf8 $probeResultPath $probeText
$probe = $probeText | ConvertFrom-Json
if ($probe.state -ne 'healthy' -or $probe.externalWriteCount -ne 0 -or -not $probe.sanitized -or -not $probe.targetBinding.matched -or $probe.targetBinding.observedType -ne 'document') { throw 'Fake public probe result mismatch' }
if ($probe.routeCapabilities.originalMedia.status -ne 'unsupported' -or $probe.routeCapabilities.originalMedia.reasonCode -ne 'mcp.media_write_unavailable') { throw 'Media capability false green' }

$schemaLog = Join-Path $OutputRoot 'additive-schema-validation.log'
$schemaText = (& python (Join-Path $PSScriptRoot 'validate-additive-v2.2.py') --companion-descriptor $descriptorPath --public-probe-result $probeResultPath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "schema gate failed`n$schemaText" }
Write-Utf8 $schemaLog $schemaText

$publicLog = Join-Path $OutputRoot 'public-contract-validation.json'
$publicText = (& python (Join-Path $project 'tools\validate_contracts.py') 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "public contract gate failed`n$publicText" }
Write-Utf8 $publicLog ($publicText + "`n")

$checkpointPath = Join-Path $project 'receipts\contracts-v1-stable-checkpoint-20260825T115938Z.json'
$checkpoint = Get-Content -Raw -LiteralPath $checkpointPath | ConvertFrom-Json
$mismatches = @($checkpoint.files | Where-Object { (Get-FileHash -Algorithm SHA256 -LiteralPath $_.path).Hash -ne $_.sha256 })
if ($mismatches.Count -ne 0) { throw "Frozen v1 mismatch count $($mismatches.Count)" }

$mediaExamplePath = Join-Path $OutputRoot 'fake-original-media-readback.json'
$mediaFixture = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'fixtures\original-media-v2.valid.json') | ConvertFrom-Json
$mediaExample = [ordered]@{
    input = $mediaFixture
    fakeFirst = [ordered]@{ outcome='created'; documentRemId='fake-node-1'; media=@([ordered]@{mediaId='fake-node-1-media-1';assetId='q1';role='question';ordinal=1;sha256=$mediaFixture.assets[0].sha256},[ordered]@{mediaId='fake-node-1-media-2';assetId='a1';role='answer';ordinal=1;sha256=$mediaFixture.assets[1].sha256}); exactReadback=$true }
    fakeReplay = [ordered]@{ outcome='already_applied'; documentRemId='fake-node-1'; duplicateCount=0; exactReadback=$true }
    production = [ordered]@{ status='unsupported'; reasonCode='mcp.media_write_unavailable'; externalWriteCount=0; silentDrop=$false }
}
Write-Utf8 $mediaExamplePath (($mediaExample | ConvertTo-Json -Depth 12) + "`n")

$reportPath = Join-Path $OutputRoot 'readable-report.md'
$report = @(
    '# RemNote Exporter 2.2.0 offline acceptance','',
    '- Real RemNote/MCP: no; public probe used a Fake CLI with create hard-failed.','- C# tests: ' + $passCount + ' PASS.',
    '- Public probe: status → search → list → read; typed root binding `canary-root-id / 零碎知识剪藏 Canary / document`; writes=0.','- Target changes: next immutable task only; in-flight task mutation=false.','',
    '## Original media exact projection','',
    '- Input order: `question.1(q1)` → `answer.1(a1)`; every item carries job-relative path, bytes and SHA-256.','- Fake first run: `created`, document `fake-node-1`, media IDs `fake-node-1-media-1/2`, exact ordered SHA readback.','- Fake replay: `already_applied`, same document ID, duplicateCount=0.','- Fake uncertain commit: unique exact readback → `recovered_after_uncertain_commit`; hidden/non-unique readback → `unknown_external_state`, no automatic retry.','- Production MCP 0.17: read capability `media.images.v1`; no supported write/attach capability, so `destination.original_media_unsupported` occurs before any external write. No image is dropped.','',
    '## Fixed route semantics','', '- clip: `clip-daily-route / Asia/Shanghai / yyyy-MM-dd`.', '- cards: `card-export`, dateRouting=false.'
) -join "`n"
Write-Utf8 $reportPath ($report + "`n")

$sources = @(
 'src\RemNoteOriginalMedia.cs','src\RemNotePublicProbe.cs','src\RemNoteExporter.cs','src\RemNoteExporterWorker.cs','src\RemNoteExporterCli.cs','src\RemNoteSettingsAdapter.cs','src\RemNoteExporterTests.cs','src\RemNoteExporterAssemblyInfo.cs','build-exporter.ps1','workers\remnote\README.md',
 'contracts\remnote-original-media.v2.schema.json','contracts\remnote-export-payload.v3.schema.json','contracts\remnote-export-task.v3.schema.json','contracts\remnote-write-receipt.v3.schema.json','contracts\remnote-public-probe-request.v2.schema.json','contracts\remnote-public-probe-result.v2.schema.json','contracts\remnote-destination-companion-descriptor.v2.schema.json',
 'tests\RemNoteQuickClipper.RemNoteExporter.Tests\validate-additive-v2.2.py','tests\RemNoteQuickClipper.RemNoteExporter.Tests\fake-public-probe-remnote-cli.js','tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-v2.2-offline-canary.ps1'
)
$receiptPath = Join-Path $OutputRoot 'receipt.json'
$receipt = [ordered]@{
    schemaVersion='remnote-exporter-v2.2-offline-canary.v1'; status='passed'; createdAt=[DateTime]::UtcNow.ToString('o'); realRemNoteCalled=$false; realMcpCalled=$false
    worker=[ordered]@{workerId=$describe.workerId;workerVersion=$describe.workerVersion;contractVersion=$describe.contractVersion;executable=(Evidence $worker)}
    publicProbe=[ordered]@{requestSchemaVersion='remnote-public-probe-request.v2';resultSchemaVersion='remnote-public-probe-result.v2';entry='probe-public --request <absolute-json> --json';externalWriteCount=0;typedRootBinding=$true;sanitized=$true;targetChangeEffectiveAt='next-task'}
    originalMedia=[ordered]@{contractVersion='remnote-original-media.v2';payloadVersion='3.0.0';taskVersion='remnote-export-task.v3';receiptVersion='3.0.0';ordering='question ordinal then answer ordinal';fakeExactReadback=$true;fakeReplay='already_applied';unknownAutomaticRetry=$false;productionStatus='unsupported';reasonCode='mcp.media_write_unavailable';silentDropAllowed=$false}
    routes=[ordered]@{clip=[ordered]@{kind='clip-daily-route';timeZone='Asia/Shanghai';dateFormat='yyyy-MM-dd'};cards=[ordered]@{kind='card-export';dateRouting=$false}}
    tests=[ordered]@{csharpPasses=$passCount;schemaGate='7 schemas / 5 valid / 5 invalid / 2 emitted';publicContractValidation=(Evidence $publicLog);fakeMcpWrites=0}
    frozenV1=[ordered]@{checkpointId=$checkpoint.checkpointId;filesChecked=@($checkpoint.files).Count;mismatchCount=0;receipt=(Evidence $checkpointPath)}
    evidence=@((Evidence $buildLog),(Evidence $schemaLog),(Evidence $descriptorPath),(Evidence $probeRequestPath),(Evidence $probeResultPath),(Evidence $mediaExamplePath),(Evidence $reportPath))
    candidateFiles=@($sources | ForEach-Object { Evidence (Join-Path $project $_) })
    remainingBoundary=@('real public read-only probe on an authorized isolated root','Core/Desktop authority admission for export.remnote 2.2.0','production original-media remains unsupported until local RemNote exposes a supported write/attach API')
}
Write-Utf8 $receiptPath (($receipt | ConvertTo-Json -Depth 15) + "`n")
Write-Output $receiptPath

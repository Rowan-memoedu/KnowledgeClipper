[CmdletBinding()]
param([string]$OutputRoot)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $project "canary\remnote-exporter-v2.3-offline\$stamp" }
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
if ($passCount -lt 114) { throw "Expected at least 114 C# PASS lines, got $passCount" }

$worker = Join-Path $project 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$describePath = Join-Path $OutputRoot 'worker-describe.json'
$describeText = (& $worker describe --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'describe failed' }
Write-Utf8 $describePath $describeText
$describe = $describeText | ConvertFrom-Json
if ($describe.workerId -ne 'export.remnote' -or $describe.workerVersion -ne '2.3.0') { throw 'worker identity mismatch' }

$fakeRuntime = Join-Path $OutputRoot 'fake-runtime'; New-Item -ItemType Directory -Path $fakeRuntime | Out-Null
$fakeWorker = Join-Path $fakeRuntime 'KnowledgeClipper.RemNoteExporter.Worker.exe'
$fakeCli = Join-Path $fakeRuntime 'fake-public-probe-remnote-cli.js'
Copy-Item -LiteralPath $worker -Destination $fakeWorker
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fake-public-probe-remnote-cli.js') -Destination $fakeCli
$node = (Get-Command node.exe -ErrorAction Stop).Source
Write-Utf8 (Join-Path $fakeRuntime 'appsettings.json') (([ordered]@{ NodePath=$node; RemnoteCliJsPath=$fakeCli; RemnoteMcpUrl='http://127.0.0.1:43101/mcp' } | ConvertTo-Json) + "`n")

$descriptorPath = Join-Path $OutputRoot 'settings-companion-descriptor-v3.json'
$descriptorText = (& $fakeWorker settings companion --version 3 --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'companion v3 failed' }
Write-Utf8 $descriptorPath $descriptorText

$requestPath = Join-Path $OutputRoot 'target-discovery-request.json'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\target-discovery-request-v1.valid.json') -Destination $requestPath
$resultPath = Join-Path $OutputRoot 'target-discovery-result.json'
$resultText = (& $fakeWorker discover-target-public --request $requestPath --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Fake target discovery failed' }
Write-Utf8 $resultPath $resultText
$result = $resultText | ConvertFrom-Json
if ($result.state -ne 'healthy' -or $result.admission -ne 'ready' -or $result.externalWriteCount -ne 0 -or -not $result.sanitized -or $result.selection.resolution -ne 'unique' -or -not $result.selection.exactReadback -or -not $result.selection.persistableBinding -or $result.selection.resolvedRootRemId -ne 'canary-root-id') { throw 'Fake target discovery result mismatch' }

$schemaLog = Join-Path $OutputRoot 'additive-schema-validation.log'
$schemaText = (& python (Join-Path $PSScriptRoot 'validate-additive-v2.3.py') --descriptor $descriptorPath --result $resultPath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "schema gate failed`n$schemaText" }
Write-Utf8 $schemaLog $schemaText

$publicLog = Join-Path $OutputRoot 'public-contract-validation.log'
$publicText = (& python (Join-Path $project 'tools\validate_contracts.py') 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "public contract gate failed`n$publicText" }
Write-Utf8 $publicLog $publicText

$checkpointPath = Join-Path $project 'receipts\contracts-v1-stable-checkpoint-20260825T115938Z.json'
$checkpoint = Get-Content -Raw -LiteralPath $checkpointPath | ConvertFrom-Json
$mismatches = @($checkpoint.files | Where-Object { (Get-FileHash -Algorithm SHA256 -LiteralPath $_.path).Hash -ne $_.sha256 })
if ($mismatches.Count -ne 0) { throw "Frozen v1 mismatch count $($mismatches.Count)" }

$reportPath = Join-Path $OutputRoot 'readable-report.md'
$report = @(
    '# RemNote Exporter 2.3.0 offline target discovery acceptance','',
    '- Trigger: active profile may have `rootTitle` but no saved `rootRemId`; the user is not asked to type an ID.',
    '- Discovery selector: exact title + expectedType=document + uniqueness=required.',
    '- Fake input: `零碎知识剪藏 Canary`; result: candidateCount=1 → `canary-root-id` → exact readback → persistableBinding=true.',
    '- 0 matches, multiple matches, 15s timeout and write-permission-disabled all fail closed; automaticRetryAllowed=false.',
    '- status/search/list/read only; fake CLI hard-fails create; externalWriteCount=0.',
    '- Existing `remnote-public-probe-request.v2` remains the verification path after a binding is persisted.',
    '- Original-media policy: RemNote is by-design unsupported; original-image mode routes only to Anki.'
) -join "`n"
Write-Utf8 $reportPath ($report + "`n")

$sources = @(
 'src\RemNoteTargetDiscovery.cs','src\RemNotePublicProbe.cs','src\RemNoteExporterWorker.cs','src\RemNoteExporterCli.cs','src\RemNoteSettingsAdapter.cs','src\RemNoteExporterTests.cs','build-exporter.ps1',
 'contracts\remnote-target-discovery-request.v1.schema.json','contracts\remnote-target-discovery-result.v1.schema.json','contracts\remnote-destination-companion-descriptor.v3.schema.json',
 'tests\RemNoteQuickClipper.RemNoteExporter.Tests\validate-additive-v2.3.py','tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-v2.3-offline-canary.ps1',
 'tests\RemNoteQuickClipper.RemNoteExporter.Tests\fixtures\target-discovery-request-v1.valid.json','tests\RemNoteQuickClipper.RemNoteExporter.Tests\fixtures\target-discovery-request-v1.spoofed-id.invalid.json','tests\RemNoteQuickClipper.RemNoteExporter.Tests\fixtures\target-discovery-result-v1.false-green.invalid.json'
)
$receiptPath = Join-Path $OutputRoot 'receipt.json'
$receipt = [ordered]@{
    schemaVersion='remnote-exporter-v2.3-offline-canary.v1'; status='passed'; createdAt=[DateTime]::UtcNow.ToString('o'); realRemNoteCalled=$false; realMcpCalled=$false
    worker=[ordered]@{workerId=$describe.workerId;workerVersion=$describe.workerVersion;contractVersion=$describe.contractVersion;executable=(Evidence $worker)}
    targetDiscovery=[ordered]@{requestSchemaVersion='remnote-target-discovery-request.v1';resultSchemaVersion='remnote-target-discovery-result.v1';entry='discover-target-public --request <absolute-json> --json';selector='rootTitle+document+exact+unique';resolvedField='selection.resolvedRootRemId';persistGate='state=healthy,admission=ready,resolution=unique,exactReadback=true,persistableBinding=true';effectiveAt='next-task';externalWriteCount=0;automaticRetryAllowed=$false}
    existingBoundProbe=[ordered]@{requestSchemaVersion='remnote-public-probe-request.v2';resultSchemaVersion='remnote-public-probe-result.v2';entry='probe-public --request <absolute-json> --json';unchanged=$true}
    originalMedia=[ordered]@{productPolicy='by-design-unsupported';supportedDestination='anki';workerInvocationAllowed=$false;reasonCode='destination.original_media_unsupported'}
    tests=[ordered]@{csharpPasses=$passCount;schemaGate='3 schemas / 1 valid / 2 invalid / 2 emitted';publicContractValidation=(Evidence $publicLog);fakeMcpWrites=0}
    frozenV1=[ordered]@{checkpointId=$checkpoint.checkpointId;filesChecked=@($checkpoint.files).Count;mismatchCount=0;receipt=(Evidence $checkpointPath)}
    evidence=@((Evidence $buildLog),(Evidence $schemaLog),(Evidence $descriptorPath),(Evidence $requestPath),(Evidence $resultPath),(Evidence $reportPath))
    candidateFiles=@($sources | ForEach-Object { Evidence (Join-Path $project $_) })
    remainingBoundary=@('Core module-action admission for discover-target-public','Desktop persists returned typed binding only after the explicit user discovery action','new append-only authority registry/release-root')
}
Write-Utf8 $receiptPath (($receipt | ConvertTo-Json -Depth 15) + "`n")
Write-Output $receiptPath

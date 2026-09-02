[CmdletBinding()]
param([string]$OutputRoot)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = (Get-Date).ToString('yyyyMMddTHHmmss')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $project "canary\remnote-exporter-v2.1-offline\$stamp"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Append-only canary output already exists: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Hash-Record([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    [ordered]@{
        path = $resolved
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolved).Hash
        bytes = (Get-Item -LiteralPath $resolved).Length
    }
}

$retiredDraft = Join-Path $PSScriptRoot 'run-v124-authority-offline-live-canary.ps1'
$retirementMarker = "$retiredDraft.RETIRED.md"
if (-not (Test-Path -LiteralPath $retiredDraft) -or -not (Test-Path -LiteralPath $retirementMarker)) {
    throw 'Retired v1.2.4 draft or its retirement marker is missing'
}
$activeEntrypoints = @(
    (Join-Path $project 'build-exporter.ps1'),
    (Join-Path $PSScriptRoot 'validate-additive-v2.py'),
    $PSCommandPath
) | ForEach-Object { [IO.Path]::GetFullPath($_) }
if ($activeEntrypoints -contains [IO.Path]::GetFullPath($retiredDraft)) {
    throw 'Retired v1.2.4 draft was adopted by an active acceptance entrypoint'
}
$draftTokens = $null
$draftParseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($retiredDraft, [ref]$draftTokens, [ref]$draftParseErrors) | Out-Null
if ($draftParseErrors.Count -ne 1 -or $draftParseErrors[0].Extent.StartLineNumber -ne 504) {
    throw 'Retired v1.2.4 draft no longer matches the preserved parse-failure evidence'
}

$buildOutput = (& pwsh -NoProfile -File (Join-Path $project 'build-exporter.ps1') 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Exporter build/test failed: $LASTEXITCODE`n$buildOutput" }
$buildLog = Join-Path $OutputRoot 'build-and-tests.log'
Write-Utf8NoBom $buildLog $buildOutput

$worker = Join-Path $project 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$describePath = Join-Path $OutputRoot 'worker-describe.json'
$settingsDescriptorPath = Join-Path $OutputRoot 'settings-descriptor.json'
$settingsSnapshotPath = Join-Path $OutputRoot 'settings-snapshot.json'
$companionDescriptorPath = Join-Path $OutputRoot 'settings-companion-descriptor.json'
$describe = (& $worker describe --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Worker describe failed' }
$settingsDescriptor = (& $worker settings describe --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Settings describe failed' }
$settingsSnapshot = (& $worker settings adapt --input (Join-Path $PSScriptRoot 'fixtures\settings-v1.valid.json') --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Settings adapt failed' }
$companionDescriptor = (& $worker settings companion --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Settings companion descriptor failed' }
Write-Utf8NoBom $describePath $describe
Write-Utf8NoBom $settingsDescriptorPath $settingsDescriptor
Write-Utf8NoBom $settingsSnapshotPath $settingsSnapshot
Write-Utf8NoBom $companionDescriptorPath $companionDescriptor

$schemaOutput = (& python (Join-Path $PSScriptRoot 'validate-additive-v2.py') --settings-descriptor $settingsDescriptorPath --settings-snapshot $settingsSnapshotPath --companion-descriptor $companionDescriptorPath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Additive schema validation failed: $LASTEXITCODE`n$schemaOutput" }
$schemaLog = Join-Path $OutputRoot 'additive-schema-validation.log'
Write-Utf8NoBom $schemaLog $schemaOutput

$publicContractOutput = (& python (Join-Path $project 'tools\validate_contracts.py') 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Public contract validation failed: $LASTEXITCODE`n$publicContractOutput" }
$publicContract = $publicContractOutput | ConvertFrom-Json
if ($publicContract.authoritativeRegistryEntries -ne 0 -or -not $publicContract.destinationNeutral) { throw 'Unexpected public contract/registry state' }
$publicContractLog = Join-Path $OutputRoot 'public-contract-validation.json'
Write-Utf8NoBom $publicContractLog $publicContractOutput

$describeObject = $describe | ConvertFrom-Json
$snapshotObject = $settingsSnapshot | ConvertFrom-Json
if ($describeObject.workerId -ne 'export.remnote' -or $describeObject.workerVersion -ne '2.1.0') { throw 'Unexpected worker identity' }
if ($snapshotObject.advanced.clipRouting -ne 'clip-daily-route' -or $snapshotObject.advanced.cardRouting -ne 'card-export') { throw 'Unexpected settings route snapshot' }
if (@($snapshotObject.secretReferences).Count -ne 0) { throw 'Settings snapshot unexpectedly contains secret references' }

$treeSource = Join-Path $PSScriptRoot 'fixtures\clip-daily-route-readable-tree.md'
$treePath = Join-Path $OutputRoot 'fake-remnote-tree.md'
Copy-Item -LiteralPath $treeSource -Destination $treePath

$checkpointPath = Join-Path $project 'receipts\contracts-v1-stable-checkpoint-20260825T115938Z.json'
$checkpoint = Get-Content -Raw -LiteralPath $checkpointPath | ConvertFrom-Json
$mismatches = @()
foreach ($file in $checkpoint.files) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.path).Hash
    if ($actual -ne $file.sha256) {
        $mismatches += [ordered]@{ path = $file.path; expected = $file.sha256; actual = $actual }
    }
}
if ($mismatches.Count -ne 0) { throw "Frozen v1 mismatch count: $($mismatches.Count)" }

$sourcePaths = @(
    'src\RemNoteExporter.cs',
    'src\RemNoteExporterWorker.cs',
    'src\RemNoteReadOnlyProbe.cs',
    'src\RemNoteExporterCli.cs',
    'src\RemNoteSettingsAdapter.cs',
    'src\RemNoteExporterTests.cs',
    'contracts\remnote-export-payload.v2.schema.json',
    'contracts\remnote-export-task.v2.schema.json',
    'contracts\remnote-write-receipt.v2.schema.json',
    'contracts\remnote-destination-settings.v1.schema.json',
    'contracts\remnote-destination-settings-snapshot.v1.schema.json',
    'contracts\remnote-destination-settings-descriptor.v1.schema.json',
    'contracts\remnote-destination-companion-descriptor.v1.schema.json',
    'contracts\remnote-readonly-probe-request.v1.schema.json',
    'contracts\remnote-readonly-probe-result.v1.schema.json',
    'tests\RemNoteQuickClipper.RemNoteExporter.Tests\validate-additive-v2.py',
    'tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-v2-offline-canary.ps1',
    'tests\RemNoteQuickClipper.RemNoteExporter.Tests\package-v2.1-release.ps1',
    'tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-v124-authority-offline-live-canary.ps1.RETIRED.md',
    'workers\remnote\README.md'
)
$candidateFiles = @($sourcePaths | ForEach-Object { Hash-Record (Join-Path $project $_) })
$csharpPassCount = ([regex]::Matches($buildOutput, '(?m)^PASS ')).Count
$schemaPassCount = ([regex]::Matches($schemaOutput, '(?m)^PASS ')).Count
if ($csharpPassCount -lt 89 -or $schemaPassCount -ne 24) { throw "Unexpected gate counts: CSharp=$csharpPassCount schemaLines=$schemaPassCount" }

$reportPath = Join-Path $OutputRoot 'readable-report.md'
$treeText = Get-Content -Raw -LiteralPath $treePath
$report = @(
    '# RemNote Exporter v2.1 离线验收',
    '',
    '- 真实 RemNote 调用：否',
    '- Worker：`export.remnote` 2.1.0',
    '- 摘录路线：`clip-daily-route`',
    '- 制卡路线：`card-export`（无日期节点）',
    '- 时区/日期格式：`Asia/Shanghai` / `yyyy-MM-dd`',
    '- 真实 MCP 调用：否；Fake MCP probe 仅含 status/search/list/read，externalWriteCount=0',
    '',
    '## 可读输入',
    '',
    '- target：rootRemId=`root-rem-id`，rootTitle=`剪藏`',
    '- localDate：`2026-08-28`',
    '- LearningBundle：`第一条摘录`',
    '- clip title：`摘录 · fixture-v2`',
    '',
    '## Fake RemNote 精确树',
    '',
    $treeText.TrimEnd(),
    '',
    '## 结果语义',
    '',
    '- 首次：root=reused，date=created，clip=created，readbackVerified=true',
    '- 重放：root=reused，date=reused，clip=already_applied，duplicateCount=0',
    '- 重名日期、内容碰撞、kind-only、错误时区、制卡日期字段泄漏：显式拒绝',
    '- 写后断线且可唯一回读：recovered_after_uncertain_commit；无法唯一回读：unknown_external_state',
    '- 只读 probe：connected 后 tool request 超时 => degraded + admission blocked，绝不绿色 healthy',
    '- rootTitle 仅为 rootRemId 的 exact readback 期望；UI 文本不构成 authority；正式“剪藏”根禁止 isolated Canary',
    '- reconcile：settingsSnapshot → payload.v2 → WorkerRequest → WorkerResult → root/date/document readback；unknown_external_state automaticRetryAllowed=false'
) -join "`n"
Write-Utf8NoBom $reportPath ($report + "`n")

$receiptPath = Join-Path $OutputRoot 'receipt.json'
$receipt = [ordered]@{
    schemaVersion = 'remnote-exporter-v2.1-offline-canary.v1'
    status = 'passed'
    createdAt = [DateTime]::UtcNow.ToString('o')
    realRemNoteCalled = $false
    realMcpCalled = $false
    worker = [ordered]@{
        workerId = $describeObject.workerId
        workerVersion = $describeObject.workerVersion
        contractVersion = $describeObject.contractVersion
        executable = Hash-Record $worker
    }
    additiveContracts = @(
        [ordered]@{ path = (Join-Path $project 'contracts\remnote-export-payload.v2.schema.json'); schemaVersion = '2.0.0' },
        [ordered]@{ path = (Join-Path $project 'contracts\remnote-export-task.v2.schema.json'); schemaVersion = 'remnote-export-task.v2' },
        [ordered]@{ path = (Join-Path $project 'contracts\remnote-write-receipt.v2.schema.json'); schemaVersion = '2.0.0' },
        [ordered]@{ path = (Join-Path $project 'contracts\remnote-destination-settings.v1.schema.json'); schemaVersion = 'remnote-destination-settings.v1' },
        [ordered]@{ path = (Join-Path $project 'contracts\remnote-destination-companion-descriptor.v1.schema.json'); schemaVersion = 'remnote-destination-companion-descriptor.v1' },
        [ordered]@{ path = (Join-Path $project 'contracts\remnote-readonly-probe-request.v1.schema.json'); schemaVersion = 'remnote-readonly-probe-request.v1' },
        [ordered]@{ path = (Join-Path $project 'contracts\remnote-readonly-probe-result.v1.schema.json'); schemaVersion = 'remnote-readonly-probe-result.v1' }
    )
    workerRequestMapping = [ordered]@{
        payloadSchemaVersion = '2.0.0'
        clipRouteFields = @('payload.route.kind', 'payload.route.timeZone', 'payload.route.localDate', 'payload.route.dateFormat')
        cardRouteFields = @('payload.route.kind')
        configSnapshotDateRoutingFields = @()
    }
    tests = [ordered]@{
        csharpPasses = $csharpPassCount
        additiveSchemaPassLines = $schemaPassCount
        additiveSchemaFixtures = [ordered]@{ validAccepted = 10; invalidRejected = 10 }
        publicContracts = [ordered]@{
            schemasChecked = $publicContract.schemasChecked
            validFixturesAccepted = $publicContract.validFixturesAccepted
            invalidFixturesRejected = $publicContract.invalidFixturesRejected
            authoritativeRegistryEntries = $publicContract.authoritativeRegistryEntries
            destinationNeutral = $publicContract.destinationNeutral
        }
        fakeCoverage = @('date-absent', 'date-existing', 'date-duplicate', 'cross-day', 'replay', 'concurrency', 'card-no-date', 'uncertain-reconcile', 'unknown-external-state', 'legacy-v2-fail-closed', 'probe-status-search-list-read', 'connected-tool-timeout-degraded', 'permission-blocked', 'root-title-spoof-blocked', 'formal-root-canary-forbidden', 'settings-payload-result-reconcile', 'unknown-no-auto-retry', 'probe-external-write-zero')
    }
    readableExample = [ordered]@{ report = $reportPath; tree = $treePath }
    frozenV1 = [ordered]@{
        checkpointId = $checkpoint.checkpointId
        receipt = Hash-Record $checkpointPath
        filesChecked = @($checkpoint.files).Count
        mismatchCount = $mismatches.Count
    }
    retiredDraft = [ordered]@{
        status = 'retired-unexecuted-parse-failed'
        executable = $false
        parseErrorCount = $draftParseErrors.Count
        parseErrorLine = $draftParseErrors[0].Extent.StartLineNumber
        original = Hash-Record $retiredDraft
        marker = Hash-Record $retirementMarker
        activeEntrypoints = $activeEntrypoints
        admittedAsEvidence = $false
    }
    candidateFiles = $candidateFiles
    evidenceFiles = @(
        (Hash-Record $buildLog)
        (Hash-Record $schemaLog)
        (Hash-Record $publicContractLog)
        (Hash-Record $describePath)
        (Hash-Record $settingsDescriptorPath)
        (Hash-Record $settingsSnapshotPath)
        (Hash-Record $companionDescriptorPath)
        (Hash-Record $treePath)
        (Hash-Record $reportPath)
    )
    readOnlyProbeEntry = [ordered]@{ executable = $worker; arguments = 'probe --request <absolute-probe-request.json> --json'; timeoutMsPerTool = 15000; externalWriteCount = 0 }
    remainingBoundary = @('authority registry checkpoint for worker 2.1.0', 'controller-triggered real read-only probe on an authorized isolated root', 'no live write Canary in this module round')
}
Write-Utf8NoBom $receiptPath (($receipt | ConvertTo-Json -Depth 12) + "`n")
Write-Output $receiptPath

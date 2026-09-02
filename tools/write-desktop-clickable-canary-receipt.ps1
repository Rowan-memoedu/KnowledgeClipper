param(
    [Parameter(Mandatory=$true)][string]$BuildId,
    [string]$ReceiptPrefix = 'desktop-ui-clickable-canary'
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$package = Join-Path $project (Join-Path 'dist\desktop-win-x64' $BuildId)
$regression = Join-Path $project (Join-Path 'canary\desktop-regression' $BuildId)
$visual = Join-Path $project (Join-Path 'canary\desktop-visual' $BuildId)
$output = Join-Path $project (Join-Path 'receipts' ("$ReceiptPrefix-$BuildId.json"))
if (Test-Path -LiteralPath $output) { throw "Immutable receipt already exists: $output" }

$buildReceiptPath = Join-Path $package 'desktop-build-receipt.v1.json'
$build = Get-Content -LiteralPath $buildReceiptPath -Raw | ConvertFrom-Json
foreach ($file in $build.files) {
    $path = Join-Path $package $file.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package file missing: $($file.path)" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $file.sha256 -or (Get-Item -LiteralPath $path).Length -ne $file.bytes) { throw "Package file mismatch: $($file.path)" }
}
$actual = @(Get-ChildItem -LiteralPath $package -File | Where-Object Name -ne 'desktop-build-receipt.v1.json')
if ($actual.Count -ne $build.fileCount -or [long](($actual | Measure-Object Length -Sum).Sum) -ne $build.totalBytes) { throw 'Package count or byte total mismatch.' }

[string[]]$names = @($build.files | ForEach-Object path)
[Array]::Sort[string]($names, [Comparison[string]]{ param($left, $right) [StringComparer]::Ordinal.Compare($left, $right) })
$byName = @{}; foreach ($file in $build.files) { $byName[$file.path] = $file }
$material = [string]::Concat(($names | ForEach-Object { $item=$byName[$_]; $item.path + "`n" + $item.sha256 + "`n" + $item.bytes + "`n" }))
$sha = [Security.Cryptography.SHA256]::Create()
try { $independentTree = [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant() }
finally { $sha.Dispose() }
if ($independentTree -ne $build.treeSha256) { throw 'Independent package tree mismatch.' }

$semanticLog = Join-Path $regression 'semantic-contract-regression.log'
$uiLog = Join-Path $regression 'windows-ui-canary.log'
$visualLog = Join-Path $regression 'visual-preview-canary.log'
$brandLog = Join-Path $regression 'brand-surface-canary.log'
$startupPath = Join-Path $regression 'startup-diagnostic.json'
$semanticText = Get-Content -LiteralPath $semanticLog -Raw
$uiText = Get-Content -LiteralPath $uiLog -Raw
$visualText = Get-Content -LiteralPath $visualLog -Raw
$brandText = Get-Content -LiteralPath $brandLog -Raw
$startup = Get-Content -LiteralPath $startupPath -Raw | ConvertFrom-Json
if (-not $semanticText.Contains('PASS semantic harness responses=17') -or -not $semanticText.Contains('PASS semantic harness v1.1 responses=18 opaqueTargetForwarded=true') -or -not $semanticText.Contains('PASS desktop automation v1.2 additiveIds=1 semanticActions=0') -or -not $semanticText.Contains('PASS inline-question-reuses-authoritative-flow') -or -not $semanticText.Contains('PASS question-answer-artifact-reader') -or -not $semanticText.Contains('PASS named-pipe-semantic-actions') -or -not $semanticText.Contains('PASS named-pipe-question-cancel') -or -not $semanticText.Contains('PASS desktop automation build and tests')) { throw 'Semantic regression log is incomplete.' }
if (-not $uiText.Contains('WINDOWS_UI_CANARY PASS assertions=84') -or -not $uiText.Contains('PASS inline question uses v2.2 open preflight plus exactly one frozen task.submit over the real Named Pipe client')) { throw 'Windows UI Canary log is incomplete.' }
if (-not $visualText.Contains('VISUAL_PREVIEW_CANARY PASS')) { throw 'Visual preview Canary log is incomplete.' }
if (-not $brandText.Contains('DESKTOP_BRAND_SURFACE_CANARY PASS product=零碎知识剪藏 assembly=KnowledgeClipper entrypoint=KnowledgeClipper.exe')) { throw 'Brand surface Canary log is incomplete.' }
if ($startup.result -ne 'PASS' -or $startup.externalConnections -ne 0 -or $startup.namedPipeInvoked -or $startup.remNoteInvoked -or $startup.mineruInvoked) { throw 'Startup diagnostic safety assertion failed.' }

$contractReceiptPath = Join-Path $project 'contracts\desktop-automation\v1\contract-receipt.json'
$contract = Get-Content -LiteralPath $contractReceiptPath -Raw | ConvertFrom-Json
$contractSha = (Get-FileHash -LiteralPath $contractReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($contractSha -ne 'a326242a31a55088ec747891ba7ffe553b1250b24281f72b8738a6506c97ef06' -or $contract.treeSha256 -ne 'd4d0cca8d1210a90f484a6d31643a8312f67ab6f1e53fc2367308af009194a6e') { throw 'Frozen desktop contract changed.' }
$contractV11ReceiptPath = Join-Path $project 'contracts\desktop-automation\v1.1\contract-receipt.json'
$contractV11 = Get-Content -LiteralPath $contractV11ReceiptPath -Raw | ConvertFrom-Json
if ($contractV11.contractId -ne 'knowledge-clipper.desktop-automation' -or $contractV11.contractVersion -ne '1.1.0') { throw 'Additive desktop contract identity mismatch.' }
$v11Root = Split-Path -Parent $contractV11ReceiptPath
$v11Names = @($contractV11.files | ForEach-Object path); [Array]::Sort[string]($v11Names, [Comparison[string]]{ param($left, $right) [StringComparer]::Ordinal.Compare($left, $right) })
$v11ByName = @{}; foreach ($file in $contractV11.files) { $v11ByName[$file.path] = $file; $path=Join-Path $v11Root $file.path; if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256 -or (Get-Item -LiteralPath $path).Length -ne $file.bytes) { throw "Additive desktop contract file mismatch: $($file.path)" } }
$v11Material = [string]::Concat(($v11Names | ForEach-Object { $item=$v11ByName[$_]; $item.path + '|' + $item.sha256 + "`n" }))
$v11Sha = [Security.Cryptography.SHA256]::Create(); try { $v11Tree = [Convert]::ToHexString($v11Sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($v11Material))).ToLowerInvariant() } finally { $v11Sha.Dispose() }
if ($v11Tree -ne $contractV11.treeSha256) { throw 'Additive desktop contract tree mismatch.' }
$contractV11Sha = (Get-FileHash -LiteralPath $contractV11ReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($contractV11Sha -ne 'b0d491b99a37315583efc27a22592c5af13ae150bd9f10f96bdb7a3d1d353953' -or $contractV11.treeSha256 -ne 'a05c8720c0ee8a493c403225957f497072e97fe36f836dc566c7d584d44c2276') { throw 'Frozen desktop v1.1 contract changed.' }
$contractV12ReceiptPath = Join-Path $project 'contracts\desktop-automation\v1.2\contract-receipt.json'
$contractV12 = Get-Content -LiteralPath $contractV12ReceiptPath -Raw | ConvertFrom-Json
$contractV12Sha = (Get-FileHash -LiteralPath $contractV12ReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($contractV12.contractId -ne 'knowledge-clipper.desktop-automation' -or $contractV12.contractVersion -ne '1.2.0') { throw 'Desktop v1.2 contract identity mismatch.' }
$v12Root = Split-Path -Parent $contractV12ReceiptPath
foreach ($file in $contractV12.files) { $path=Join-Path $v12Root $file.path; if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256 -or (Get-Item -LiteralPath $path).Length -ne $file.bytes) { throw "Desktop v1.2 contract file mismatch: $($file.path)" } }
$contentReceiptPath = Join-Path $project 'receipts\content-worker-v0.5.0-authoritative-20260826T041900Z.json'
$contentReceiptSha = (Get-FileHash -LiteralPath $contentReceiptPath -Algorithm SHA256).Hash.ToUpperInvariant()
$contentReceipt = Get-Content -LiteralPath $contentReceiptPath -Raw | ConvertFrom-Json
if ($contentReceiptSha -ne '3476492F2B6FF69014B9AFC854F25A671C1D96BC5720DBC43446BF28C9BF43A4' -or $contentReceipt.worker.workerId -ne 'content.deepseek' -or $contentReceipt.worker.workerVersion -ne '0.5.0') { throw 'Current Content worker authority mismatch.' }
if ([string]::Join(',', $contentReceipt.worker.operations) -ne 'understand-and-card,question.answer,question.make-cards' -or [string]::Join(',', $contentReceipt.worker.outputKinds) -ne 'learning-bundle,card-bundle,language-practice-decision,question.answer') { throw 'Current Content worker descriptor capabilities mismatch.' }
$questionContractRoot = Join-Path $project 'contracts\core\v2.2\question-workflow\schemas'
$questionContracts = @('question-action.v2.schema.json','task-query.v2.2.schema.json','task-query-result.v2.2.schema.json') | ForEach-Object { $path=Join-Path $questionContractRoot $_; [ordered]@{ path=('contracts/core/v2.2/question-workflow/schemas/' + $_); sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } }

$visualFiles = @(Get-ChildItem -LiteralPath $visual -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ path=('canary/desktop-visual/' + $BuildId + '/' + $_.Name); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$receipt = [ordered]@{
    schemaVersion = 'desktop-ui-clickable-canary.v1'
    buildId = $BuildId
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    productName = '零碎知识剪藏'
    startEntrypoint = ('dist/desktop-win-x64/' + $BuildId + '/KnowledgeClipper.exe')
    startupDiagnosticInvocation = 'KnowledgeClipper.exe --startup-diagnostic'
    package = [ordered]@{
        buildReceiptPath = ('dist/desktop-win-x64/' + $BuildId + '/desktop-build-receipt.v1.json')
        buildReceiptSha256 = (Get-FileHash -LiteralPath $buildReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
        fileCount = $build.fileCount
        totalBytes = $build.totalBytes
        treeSha256 = $build.treeSha256
        independentTreeSha256 = $independentTree
        executableSha256 = $build.executableSha256
        applicationDllSha256 = $build.applicationDllSha256
        selfContained = $build.selfContained
        runtimeIdentifier = $build.runtimeIdentifier
        sdkVersion = $build.sdkVersion
    }
    automatedEvidence = [ordered]@{
        semanticContractRegression = [ordered]@{ status='PASS'; semanticHarnessResponses=17; log=('canary/desktop-regression/' + $BuildId + '/semantic-contract-regression.log'); sha256=(Get-FileHash $semanticLog -Algorithm SHA256).Hash.ToLowerInvariant() }
        windowsUiCanary = [ordered]@{ status='PASS'; assertions=84; log=('canary/desktop-regression/' + $BuildId + '/windows-ui-canary.log'); sha256=(Get-FileHash $uiLog -Algorithm SHA256).Hash.ToLowerInvariant() }
        visualPreviewCanary = [ordered]@{ status='PASS'; log=('canary/desktop-regression/' + $BuildId + '/visual-preview-canary.log'); sha256=(Get-FileHash $visualLog -Algorithm SHA256).Hash.ToLowerInvariant(); files=$visualFiles }
        brandSurfaceCanary = [ordered]@{ status='PASS'; productName='零碎知识剪藏'; assemblyName='KnowledgeClipper'; entrypoint='KnowledgeClipper.exe'; log=('canary/desktop-regression/' + $BuildId + '/brand-surface-canary.log'); sha256=(Get-FileHash $brandLog -Algorithm SHA256).Hash.ToLowerInvariant() }
        startupDiagnostic = [ordered]@{ status=$startup.result; processArchitecture=$startup.processArchitecture; selfContainedHost=$startup.selfContainedHost; configParsed=$startup.configParsed; packageReceiptParsed=$startup.packageReceiptParsed; packageFilesVerified=$startup.packageFilesVerified; packageTreeVerified=$startup.packageTreeVerified; packageFileCount=$startup.packageFileCount; packageTotalBytes=$startup.packageTotalBytes; packageTreeSha256=$startup.packageTreeSha256; buildReceiptSha256=$startup.buildReceiptSha256; singleInstanceAlreadyRunning=$startup.singleInstanceAlreadyRunning; productionWorkDirectoryCreated=$startup.productionWorkDirectoryCreated; externalConnections=$startup.externalConnections; namedPipeInvoked=$startup.namedPipeInvoked; remNoteInvoked=$startup.remNoteInvoked; mineruInvoked=$startup.mineruInvoked; report=('canary/desktop-regression/' + $BuildId + '/startup-diagnostic.json'); sha256=(Get-FileHash $startupPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    frozenDesktopContract = [ordered]@{ version='1.0.0'; receiptSha256=$contractSha; treeSha256=$contract.treeSha256; changed=$false }
    additiveDesktopContracts = @(
        [ordered]@{ version='1.1.0'; receiptSha256=$contractV11Sha; treeSha256=$contractV11.treeSha256; verified=$true; frozen=$true },
        [ordered]@{ version='1.2.0'; receiptSha256=$contractV12Sha; treeSha256=$contractV12.treeSha256; verified=$true; extendsVersion='1.1.0' }
    )
    questionWorkflowDependencies = [ordered]@{
        coreContractVersion = '2.2.0'
        schemas = $questionContracts
        contentAuthority = [ordered]@{ workerId='content.deepseek'; workerVersion='0.5.0'; receiptPath='receipts/content-worker-v0.5.0-authoritative-20260826T041900Z.json'; receiptSha256=$contentReceiptSha.ToLowerInvariant(); operations=$contentReceipt.worker.operations; outputKinds=$contentReceipt.worker.outputKinds }
        legacyDesktopQuestionActionUsed = $false
    }
    realWindowsBoundaries = [ordered]@{
        externalApplicationSelectionThroughNoActivateButton = 'PENDING_USER_EXPERIENCE'
        realImageClipboard = 'PENDING_USER_EXPERIENCE'
        windowsSystemScreenshotPhysicalSelection = 'PENDING_USER_EXPERIENCE'
        windowsSystemScreenshotEscapeCancellation = 'PENDING_USER_EXPERIENCE'
        crossMonitorDifferentDpiDrag = 'PENDING_ENVIRONMENT'
        productionOrchestratorProcess = 'NOT_RUN_SAFE_BOUNDARY'
        desktopToRealRemNoteE2E = 'NOT_RUN'
        remNoteInvoked = $false
        mineruInvoked = $false
    }
}
$temporary = $output + '.tmp-' + [Guid]::NewGuid().ToString('N')
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
Move-Item -LiteralPath $temporary -Destination $output
Write-Output $output

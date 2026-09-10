#Requires -Version 7.0
param(
    [string]$RepositoryRoot = 'D:\Projects\RemNote Quick Clipper',
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (-not $evidence.StartsWith('D:\Artifacts\KnowledgeClipper\question-follow-up\', [StringComparison]::OrdinalIgnoreCase)) { throw 'desktop-evidence-root-outside-approved-scope' }
$receiptPath = Join-Path $evidence 'module-receipt.json'
$payload = Join-Path $evidence 'desktop-payload'
if ((Test-Path -LiteralPath $receiptPath) -or (Test-Path -LiteralPath $payload)) { throw 'immutable-module-output-already-exists' }
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Row([string]$path) { $f = Get-Item -LiteralPath $path; [ordered]@{ path = $f.FullName; bytes = [long]$f.Length; sha256 = Sha $path } }
function JsonNew([string]$path, [object]$value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 30) + "`n")
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    $null = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
}
$checks = [ordered]@{
    'gateway.log' = 'DESKTOP_CORE_GATEWAY_TESTS PASS assertions=64'
    'application.log' = 'DESKTOP_V3_APPLICATION_TESTS PASS assertions=86'
    'windows-ui.log' = 'WINDOWS_QUESTION_FOLLOW_UP PASS assertions=18'
    'preview-lifecycle.log' = 'WINDOWS_PREVIEW_LIFECYCLE PASS assertions=15'
    'preview.log' = 'WINDOWS_FLASHCARD_PREVIEW_UI_CANARY PASS assertions=8'
    'scale.log' = 'WINDOWS_CONTENT_SCALE_CANARY PASS assertions=16'
}
$testEvidence = @()
foreach ($name in $checks.Keys) {
    $path = Join-Path $evidence $name
    $text = Get-Content -LiteralPath $path -Raw
    if (-not $text.Contains($checks[$name]) -or $text -match 'Unhandled exception|Assertion failed|TESTS FAIL|CANARY FAIL') { throw ('test-output-invalid: ' + $name) }
    $testEvidence += Row $path
}
foreach ($name in @('desktop-build.log', 'windows-build.log')) {
    $path = Join-Path $evidence $name
    $text = Get-Content -LiteralPath $path -Raw
    if ($text -match 'warning [A-Z]+\d+|error [A-Z]+\d+|生成失败|Build FAILED') { throw ('build-output-invalid: ' + $name) }
    $testEvidence += Row $path
}
$ui = Get-Content -LiteralPath (Join-Path $evidence 'windows-ui\receipt.json') -Raw | ConvertFrom-Json
if ($ui.status -ne 'PASS' -or $ui.assertions -ne 18 -or $ui.externalCalls -ne 0 -or $ui.actualAnkiWrites -ne 0) { throw 'ui-receipt-invalid' }
$sourcePaths = @('src\DesktopUi.cs', 'src\FlashcardPreviewUi.cs', 'src\WindowsQuestionFollowUpCanary.cs', 'src\WindowsUiCanary.cs', 'src\WindowsPreviewLifecycleCanary.cs', 'desktop\RemNoteQuickClipper.Desktop.csproj', 'desktop\RemNoteQuickClipper.Desktop.WindowsCanary.csproj', 'tests\KnowledgeClipper.Desktop.CoreGateway.Tests\Program.cs', 'tests\KnowledgeClipper.Desktop.CoreGateway.Tests\QuestionFlowFixture.cs', 'tests\KnowledgeClipper.Desktop.CoreGateway.Tests\QuestionFollowUpTests.cs', 'tests\KnowledgeClipper.Desktop.CoreGateway.Tests\KnowledgeClipper.Desktop.CoreGateway.Tests.csproj', 'tests\KnowledgeClipper.Desktop.Application.Tests\Program.cs', 'tools\Complete-DesktopQuestionFollowUpModule.ps1')
$sourcePaths += @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\KnowledgeClipper.Desktop.Application') -Filter '*.cs' -File | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) })
$sourcePaths += @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\KnowledgeClipper.Desktop.Adapters') -Filter '*.cs' -File | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) })
$sources = @($sourcePaths | Sort-Object -Unique | ForEach-Object { Row (Join-Path $repo $_) })
Push-Location $repo
try {
    & $DotnetPath publish 'desktop\RemNoteQuickClipper.Desktop.csproj' -c Release -r win-x64 --self-contained true --no-restore -o $payload --nologo 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'publish.log')
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) { throw ('desktop-publish-failed: ' + $publishExit) }
} finally { Pop-Location }
foreach ($source in $sources) { if ((Sha $source.path) -ne $source.sha256) { throw 'source-changed-during-publish' } }
$buildRoot = Join-Path $repo 'desktop\bin\RemNoteQuickClipper.Desktop\Release\net8.0-windows\win-x64'
foreach ($name in @('KnowledgeClipper.exe', 'KnowledgeClipper.dll', 'KnowledgeClipper.deps.json', 'KnowledgeClipper.runtimeconfig.json')) {
    if ((Sha (Join-Path $buildRoot $name)) -ne (Sha (Join-Path $payload $name))) { throw ('publish-build-drift: ' + $name) }
}
$sdkExpected = @{
    'KnowledgeClipper.Core.V3.Client.dll' = 'FBE7A8732D155EEC1CDF1F6D2F950D5E3653D1033AA5A34C5B98439768949FB5'
    'KnowledgeClipper.Core.V3.Contracts.dll' = 'EAFEED544C951E625D09CE92B8578200E3DA24F1F89F55FDE2B547E52CBD12D6'
}
foreach ($name in $sdkExpected.Keys) { if ((Sha (Join-Path $payload $name)) -ne $sdkExpected[$name]) { throw ('frozen-sdk-drift: ' + $name) } }
$paths = [string[]]@(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/') })
[Array]::Sort($paths, [StringComparer]::Ordinal)
$rows = @(); [long]$total = 0; $treeText = [Text.StringBuilder]::new()
foreach ($relative in $paths) {
    $file = Get-Item -LiteralPath (Join-Path $payload $relative)
    $row = [ordered]@{ path = $relative; bytes = [long]$file.Length; sha256 = Sha $file.FullName }
    $rows += $row; $total += $row.bytes
    [void]$treeText.Append($row.path).Append([char]0).Append($row.bytes).Append([char]0).Append($row.sha256).Append("`n")
}
$tree = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($treeText.ToString())))
$payloadReceipt = Join-Path $evidence 'payload-verification.v1.json'
JsonNew $payloadReceipt ([ordered]@{ schemaVersion = 'knowledge-clipper-desktop-question-payload.v1'; status = 'PASS-PUBLISH-AND-EQUALITY-NOT-INTEGRATED'; payloadRoot = $payload; fileCount = $rows.Count; totalBytes = $total; treeSha256 = $tree; treeAlgorithm = 'ordinal relative path NUL decimal bytes NUL uppercase SHA256 LF'; files = $rows; sourceFiles = $sources; userHandTestEntrypoint = $false; sdkFreeze = '020355' })
$oldSuccess = 'D:\Artifacts\KnowledgeClipper\actual-launch\desktop-rta-20260908T083307Z\final-receipt.json'
if ((Sha $oldSuccess) -ne '88C20A438889FD84A1739E4C69C3E936A54F2C8F6E15AE183233507C899ED8EB') { throw 'prior-success-receipt-drift' }
JsonNew $receiptPath ([ordered]@{
    schemaVersion = 'knowledge-clipper-desktop-question-follow-up-module.v1'; status = 'PASS-OFFLINE-MODULE-NOT-ACTUAL'
    payload = Row $payloadReceipt; productionDll = Row (Join-Path $payload 'KnowledgeClipper.dll'); productionExe = Row (Join-Path $payload 'KnowledgeClipper.exe')
    sourceFiles = $sources; tests = $testEvidence; uiReceipt = Row (Join-Path $evidence 'windows-ui\receipt.json')
    priorSuccessUnchanged = Row $oldSuccess; externalCalls = 0; physicalSpeaker = 0; actualAnkiWrites = 0; userHandTestEntrypoint = $false
    authorization = 'initial/follow-up=null; independent make-cards and final confirm use fresh public typed builder; no session borrowing'
    evidenceBoundary = 'real Forms and production callbacks/use cases; deterministic ports and synthetic opaque SDK handles; WM_KEYDOWN/UP and PerformClick are simulated control events, not physical keyboard/mouse'
    corrections = @('full-window content scaling retained and reverified', 'inline initial uses v3 Application instead of legacy controller', 'current rendered draft plus retained response session guards follow-up', 'missing session has no fallback', 'observer interruption/timeout is Unknown not Core cancelled/failed', 'late result guarded by generation+operation+session', 'follow-up async completion marshalled to window thread and closed-window guarded')
    failedAttemptsPreserved = @([ordered]@{ path = 'D:\Artifacts\KnowledgeClipper\question-follow-up\offline-20260908-1\windows-ui.log'; verdict = 'FAILED despite earlier receipt PASS and exit 0: unhandled asynchronous UI teardown exception; superseded by offline-2 and offline-3' })
    pending = @('no sealed Question Desktop/Core process Actual this batch', 'no external provider or destination calls authorized', 'runner prepared separately; root review required before future Actual', 'mixed-monitor physical DPI not claimed')
})
Write-Output ('STATUS VALIDATED module=' + $receiptPath + ' sha256=' + (Sha $receiptPath))
Write-Output ('PAYLOAD files=' + $rows.Count + ' bytes=' + $total + ' tree=' + $tree + ' receiptSha256=' + (Sha $payloadReceipt))

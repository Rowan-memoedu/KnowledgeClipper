#Requires -Version 7.0
param([Parameter(Mandatory)][string]$CandidateRoot, [Parameter(Mandatory)][string]$EvidenceRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$candidate = [IO.Path]::GetFullPath($CandidateRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (-not $candidate.StartsWith('D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-question-', [StringComparison]::OrdinalIgnoreCase)) { throw 'question-candidate-boundary-required' }
if (-not $evidence.StartsWith('D:\Artifacts\KnowledgeClipper\question-follow-up\', [StringComparison]::OrdinalIgnoreCase)) { throw 'safe-evidence-boundary-required' }
if (Test-Path -LiteralPath (Join-Path $evidence 'safe-gates.json')) { throw 'immutable-safe-receipt-exists' }
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function RequireSha([string]$path, [string]$expected) { if ((Sha $path) -ne $expected) { throw ('identity-drift: ' + $path) } }
function VerifyRow([string]$path, [object]$row) { if ((Get-Item -LiteralPath $path).Length -ne $row.bytes) { throw 'file-bytes-drift' }; RequireSha $path $row.sha256 }
function Binding([string]$path) { [ordered]@{ path = $path; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Sha $path } }
function SaveNew([string]$path, [object]$value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($value | ConvertTo-Json -Depth 20) + "`n")
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
$manifestPath = Join-Path $candidate 'candidate-file-manifest.v1.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
if ($manifest.regressionSlice -ne 'Question') { throw 'not-question-candidate' }
$index = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$builder = [Text.StringBuilder]::new(); [long]$bytes = 0
$previousPath = $null
foreach ($row in $manifest.files) {
    $relative = [string]$row.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or $relative.Split('/') -contains '..' -or $index.ContainsKey($relative)) { throw 'candidate-path-invalid' }
    if ($null -ne $previousPath -and [StringComparer]::Ordinal.Compare($previousPath, $relative) -ge 0) { throw 'manifest-not-ordinal' }
    $previousPath = $relative
    VerifyRow (Join-Path $candidate $relative) $row
    $index.Add($relative, $row); $bytes += [long]$row.bytes
    [void]$builder.Append($relative).Append([char]0).Append($row.bytes).Append([char]0).Append($row.sha256).Append("`n")
}
$tree = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($builder.ToString())))
if ($index.Count -ne $manifest.payload.files -or $bytes -ne $manifest.payload.bytes -or $tree -ne $manifest.payload.treeSha256 -or @(Get-ChildItem -LiteralPath $candidate -File -Recurse).Count -ne $index.Count + 1) { throw 'candidate-tree-drift-or-extra-files' }
Write-Output ('CANDIDATE identity PASS files=' + $index.Count)
$modulePath = 'D:\Artifacts\KnowledgeClipper\question-follow-up\offline-20260908-3\module-receipt.json'
RequireSha $modulePath '0DACAF0B887D4DD1F7F94606204A7162E7F06C1F76EF8B530F9B9D479231AF74'
$module = Get-Content -LiteralPath $modulePath -Raw | ConvertFrom-Json -Depth 100
foreach ($source in $module.sourceFiles) { VerifyRow $source.path $source }
$prebuiltPath = $module.payload.path
RequireSha $prebuiltPath 'FB166844CDBC239B89157E2B21D226FFD62450B29E21B7DA4D39EE206FBE1148'
$prebuilt = Get-Content -LiteralPath $prebuiltPath -Raw | ConvertFrom-Json -Depth 100
foreach ($row in $prebuilt.files) { if (-not $index.ContainsKey($row.path) -or $index[$row.path].sha256 -ne $row.sha256 -or $index[$row.path].bytes -ne $row.bytes) { throw 'prebuilt-desktop-drift' } }
$corePath = Join-Path $candidate 'core-read-to-anki-candidate-manifest.json'
RequireSha $corePath '67B513DF538EB77732E0CED67D572123D45D68489DF1D21DB3FA8EB8E56A6F38'
$core = Get-Content -LiteralPath $corePath -Raw | ConvertFrom-Json -Depth 100
if ($core.candidateId -ne 'rta-observation-20260908T062524Z-505ab282' -or $core.authority.contentUpgrade.workerVersion -ne '0.12.0') { throw 'core-content-binding-drift' }
$coreRuntimeFiles = @($core.files | Where-Object { $_.path -match '^(authority|contracts|core-v3)/' })
$notDeployed = @($core.files | Where-Object { $_.path -notmatch '^(authority|contracts|core-v3)/' } | ForEach-Object { $_.path })
$packagingOnly = @('dependency-manifest.v3.json', 'sdk/KnowledgeClipper.Core.V3.Client.dll', 'sdk/KnowledgeClipper.Core.V3.Contracts.dll', 'sdk/sdk-binding.v1.json', 'sdk/sdk-contract.v4.json')
if (@($notDeployed | Where-Object { $_ -notin $packagingOnly }).Count) { throw 'unknown-core-packaging-boundary' }
foreach ($row in $coreRuntimeFiles) { if (-not $index.ContainsKey($row.path) -or $index[$row.path].sha256 -ne $row.sha256 -or $index[$row.path].bytes -ne $row.bytes) { throw ('core-runtime-drift: ' + $row.path) } }
RequireSha (Join-Path $candidate 'core-read-to-anki-sdk-freeze-manifest.json') '032990357E529C6B539AE97E41BCBD8F63281840A0CD8C74904A311205661289'
$runnerPath = Join-Path $candidate 'actual-runner\runner-question-release-manifest.v1.json'
RequireSha $runnerPath '93F1A4E7FF0924AB80025A62B2992E848CBA350224383805925A1BC3FE6EF5AF'
$runner = Get-Content -LiteralPath $runnerPath -Raw | ConvertFrom-Json -Depth 100
foreach ($row in $runner.files) { if ($index['actual-runner/' + $row.path].sha256 -ne $row.sha256 -or $index['actual-runner/' + $row.path].bytes -ne $row.bytes) { throw 'question-runner-drift' } }
foreach ($source in $runner.sources) { RequireSha $source.path $source.sha256 }
RequireSha (Join-Path $candidate 'actual-runner\app\KnowledgeClipper.ActualDesktopFlowRunner.dll') 'D0032C052245B4B01B17CBC632F1A5FBAC7CC325819185143485B53B794542AF'
foreach ($prefix in @('', 'actual-runner/app/')) {
    RequireSha (Join-Path $candidate ($prefix + 'KnowledgeClipper.Core.V3.Client.dll')) 'FBE7A8732D155EEC1CDF1F6D2F950D5E3653D1033AA5A34C5B98439768949FB5'
    RequireSha (Join-Path $candidate ($prefix + 'KnowledgeClipper.Core.V3.Contracts.dll')) 'EAFEED544C951E625D09CE92B8578200E3DA24F1F89F55FDE2B547E52CBD12D6'
}
Write-Output 'EXACT prebuilt/Desktop source/Core/Content/SDK/Question runner PASS; no compilation'
$exe = Join-Path $candidate 'KnowledgeClipper.exe'
foreach ($mode in @('startup-diagnostic', 'single-instance-diagnostic')) {
    $log = Join-Path $evidence ($mode + '.log')
    if (Test-Path -LiteralPath $log) { throw 'immutable-diagnostic-log-exists' }
    & $exe ('--' + $mode) 2>&1 | Tee-Object -FilePath $log
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw ('diagnostic-nonzero: ' + $mode) }
    $text = Get-Content -LiteralPath $log -Raw
    if ($text -match 'Unhandled exception|Assertion failed') { throw 'diagnostic-unhandled-exception' }
}
$jobs = Join-Path 'D:\AppData\KnowledgeClipper' ('safe-' + $manifest.candidateId + '\core-v3')
if (Test-Path -LiteralPath $jobs) { throw 'safe-core-data-root-not-fresh' }
$integrationLog = Join-Path $evidence 'integration-startup.log'
if (Test-Path -LiteralPath $integrationLog) { throw 'immutable-integration-log-exists' }
& $exe --integration-startup-canary $jobs 2>&1 | Tee-Object -FilePath $integrationLog
$integrationExit = $LASTEXITCODE
if ($integrationExit -ne 0) { throw 'integration-startup-nonzero' }
$integration = Get-Content -LiteralPath $integrationLog -Raw | ConvertFrom-Json
if ($integration.result -ne 'PASS' -or $integration.action -ne 'capabilities.get' -or $integration.namedPipeInvoked -ne $true -or $integration.orchestratorStartedByDesktop -ne $true -or $integration.externalWorkerInvoked -ne $false -or $integration.physicalSpeakerInvoked -ne $false -or $integration.ankiInvoked -ne $false) { throw 'integration-not-safe' }
if (Get-Process -Id $integration.orchestratorProcessId -ErrorAction SilentlyContinue) { throw 'owned-core-not-cleaned' }
$runtimeFiles = @(Get-ChildItem -LiteralPath $jobs -File -Recurse -ErrorAction SilentlyContinue)
if (@($runtimeFiles | Where-Object { $_.Name -match 'worker-request|worker-result' }).Count) { throw 'unexpected-worker-artifact-in-readonly-gate' }
# No task submit is sent in this script; actual execution is deliberately separate.
$oldReceipt = 'D:\Artifacts\KnowledgeClipper\actual-launch\desktop-rta-20260908T083307Z\final-receipt.json'
RequireSha $oldReceipt '88C20A438889FD84A1739E4C69C3E936A54F2C8F6E15AE183233507C899ED8EB'
foreach ($row in $manifest.files) { VerifyRow (Join-Path $candidate $row.path) $row }
SaveNew (Join-Path $evidence 'safe-gates.json') ([ordered]@{
    status = 'PASS-SAFE-INTEGRATED-QUESTION-NOT-ACTUAL'; candidate = $candidate; manifest = Binding $manifestPath
    files = $index.Count; bytes = $bytes; treeSha256 = $tree; prebuiltEqualityFiles = $prebuilt.files.Count
    desktopSourceBindings = $module.sourceFiles.Count; coreRuntimeEqualityFiles = $coreRuntimeFiles.Count; coreSourcePackagingMetadataNotDeployed = $notDeployed
    questionRunnerEqualityFiles = $runner.files.Count; questionRunnerSourceBindings = $runner.sources.Count; module = Binding $modulePath
    sourceAssembler = Binding (Join-Path $PSScriptRoot 'New-DesktopReadToAnkiCandidate.ps1'); safeGateSource = Binding $PSCommandPath
    desktopDll = Binding (Join-Path $candidate 'KnowledgeClipper.dll'); questionRunnerDll = Binding (Join-Path $candidate 'actual-runner\app\KnowledgeClipper.ActualDesktopFlowRunner.dll')
    coreManifest = Binding $corePath; runnerManifest = Binding $runnerPath; sdkFreezeManifest = Binding (Join-Path $candidate 'core-read-to-anki-sdk-freeze-manifest.json')
    startupDiagnostic = Binding (Join-Path $evidence 'startup-diagnostic.log'); singleInstanceDiagnostic = Binding (Join-Path $evidence 'single-instance-diagnostic.log'); readonlyIpc = Binding $integrationLog
    readonlyAction = $integration.action; safeCorePid = $integration.orchestratorProcessId; safeCoreExited = $true; safeDataRoot = $jobs
    workerRuns = 0; externalWrites = 0; physicalSpeaker = 0; actualStarted = $false; userHandTestEntrypoint = $false
    priorFourActionSuccess = Binding $oldReceipt; pending = 'await one Question Actual instruction; no OCR/RemNote/Read-Clip matrix or stop-replay expansion'
})
Write-Output ('STATUS VALIDATED ' + (Join-Path $evidence 'safe-gates.json') + ' SHA256=' + (Sha (Join-Path $evidence 'safe-gates.json')))

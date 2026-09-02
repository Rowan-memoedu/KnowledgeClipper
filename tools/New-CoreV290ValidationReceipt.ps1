[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$outputRoot = Join-Path $ProjectRoot 'canary\core-v2.9.0-offline\20260901T061500Z'
$receiptPath = Join-Path $outputRoot 'core-authority-validation.receipt.json'
if (Test-Path -LiteralPath $outputRoot) { throw "Append-only validation output already exists: $outputRoot" }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Evidence([string]$Relative) {
    $path = Join-Path $ProjectRoot $Relative.Replace('/','\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required evidence missing: $Relative" }
    [ordered]@{ path=$Relative; bytes=(Get-Item -LiteralPath $path).Length; sha256=Sha $path }
}
function WriteJson([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 80) + "`n"), [Text.UTF8Encoding]::new($false))
}
function RunJson([string]$Exe, [string[]]$Arguments, [string]$StdoutName, [string]$StderrName) {
    $stdoutPath = Join-Path $outputRoot $StdoutName
    $stderrPath = Join-Path $outputRoot $StderrName
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Exe
    $start.WorkingDirectory = $ProjectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Failed to start $Exe" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [IO.File]::WriteAllText($stdoutPath, $stdoutTask.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($stderrPath, $stderrTask.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
    if ($process.ExitCode -ne 0) { throw "$Exe failed with exit code $($process.ExitCode); preserved output: $stderrPath" }
    [ordered]@{ document=(Get-Content -LiteralPath $stdoutPath -Raw | ConvertFrom-Json -Depth 100); stdout=(Evidence ([IO.Path]::GetRelativePath($ProjectRoot,$stdoutPath).Replace('\','/'))); stderr=(Evidence ([IO.Path]::GetRelativePath($ProjectRoot,$stderrPath).Replace('\','/'))) }
}

$sourceCheckpoint = Evidence 'contracts/core/v2.8/checkpoints/v2.8.1-20260901T054000Z/checkpoint.json'
if ($sourceCheckpoint.sha256 -ne '08FD86C88819D2A6BDDCFF690EE7AA913BB3299A33BE4051F608B24DA52D3A41') { throw 'Core v2.8.1 source checkpoint drifted.' }
$ankiAdmission = Evidence 'workers/anki-exporter/admission/core/export.anki-v1.4.0-20260901T050000Z.json'
if ($ankiAdmission.sha256 -ne '982074D7BE4C4B14B27AA58826B3DB750E0206A6F5AA806775B3EFAF08A6BCD7') { throw 'Anki 1.4.0 admission packet drifted.' }
$ankiQualification = Evidence 'workers/anki-exporter/canary/formal-managed-initialization/20260901T050008Z-formal-v140-managed-init/receipt.json'
$ankiQualificationDocument = Get-Content -LiteralPath (Join-Path $ProjectRoot $ankiQualification.path.Replace('/','\')) -Raw | ConvertFrom-Json -Depth 80
if ($ankiQualificationDocument.status -ne 'passed' -or $ankiQualificationDocument.authorizationNegative.providerCalls -ne 0 -or
    $ankiQualificationDocument.firstRun.writes.modelsCreated -ne 1 -or $ankiQualificationDocument.firstRun.writes.decksCreated -ne 1 -or
    $ankiQualificationDocument.explicitReplay.writes.modelsCreated -ne 0 -or $ankiQualificationDocument.explicitReplay.writes.decksCreated -ne 0 -or
    $ankiQualificationDocument.realAnkiConnectCalls -ne 0 -or $ankiQualificationDocument.safety.automaticRetryAfterUnknownState) {
    throw 'Anki 1.4.0 formal Fake qualification gate failed.'
}
$remnoteRelease = Evidence 'releases/knowledge-clipper-remnote-exporter-v2.3.0-20260901T043610Z/win-x64/KnowledgeClipper.RemNoteExporter.Worker.exe'
$desktopQuality = Evidence 'canary/quality-desktop-producer-fixes/20260901T050000Z-independent-r14-final/quality-receipt.json'
if ($desktopQuality.sha256 -ne '88AF0CEDFDA80A5843D6DF4DB32F3D09FF2F3AEB9794720E5B9296C0064B2C4F') { throw 'Desktop producer r14 quality receipt drifted.' }
$historicalPlayback = Evidence 'canary/quality-next-desktop-regressions/20260901T052000Z-core-precheckpoint/quality-receipt.json'
if ($historicalPlayback.sha256 -ne '6007DBE8EAB70DAFC5D8961A85F3A8D55979F3C949ABE39035EAD659ED959969') { throw 'Historical playback-race preservation receipt drifted.' }

$contractsText = (& python (Join-Path $ProjectRoot 'tools\validate_contracts.py') 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Contract validation failed: $contractsText" }
$contracts = $contractsText | ConvertFrom-Json -Depth 40

$coreAdapterExe = Join-Path $ProjectRoot 'tests\CoreAdapter.IntegrationTests\bin\Release\net8.0\CoreAdapter.IntegrationTests.exe'
$orchestratorTestsExe = Join-Path $ProjectRoot 'tests\Orchestrator.ContractTests\bin\Release\net8.0\Orchestrator.ContractTests.exe'
$coreAdapter = RunJson $coreAdapterExe @($ProjectRoot) 'core-adapter.stdout.json' 'core-adapter.stderr.log'
$orchestrator = RunJson $orchestratorTestsExe @($ProjectRoot) 'orchestrator.stdout.json' 'orchestrator.stderr.log'
if ($coreAdapter.document.status -ne 'passed' -or $orchestrator.document.status -ne 'passed') { throw 'Aggregate Core test status is not passed.' }
$ankiGate = @($coreAdapter.document.results | Where-Object name -eq 'anki-managed-initialization-v14')
$playbackGate = @($coreAdapter.document.results | Where-Object name -eq 'stage-scoped-playback-stop-and-replay')
$originalGate = @($coreAdapter.document.results | Where-Object name -eq 'original-media-anki-only')
$remnoteGate = @($coreAdapter.document.results | Where-Object name -eq 'remnote-target-discovery-v13')
if ($ankiGate.Count -ne 1 -or $ankiGate[0].unauthorizedWorkerCalls -ne 0 -or $ankiGate[0].firstRun.modelsCreated -ne 1 -or $ankiGate[0].firstRun.decksCreated -ne 1 -or
    -not $ankiGate[0].firstRun.exactReadback -or $ankiGate[0].explicitReplay.modelsCreated -ne 0 -or $ankiGate[0].explicitReplay.decksCreated -ne 0 -or
    $ankiGate[0].unknownExternalStateAutomaticRetries -ne 0 -or $ankiGate[0].realAnkiConnectCalls -ne 0) { throw 'Core Anki initialize-managed gate failed.' }
if ($playbackGate.Count -ne 1 -or $playbackGate[0].stop.runCancelRequested -or $originalGate.Count -ne 1 -or -not $originalGate[0].remnoteRejectedBeforeWorker -or $originalGate[0].remnoteStarted -or
    $remnoteGate.Count -ne 1 -or -not $remnoteGate[0].uniquePersistableBinding -or $remnoteGate[0].externalWrites -ne 0) { throw 'Existing Core regression gate failed.' }

WriteJson $receiptPath ([ordered]@{
    schemaVersion='knowledge-clipper-core-v2.9-authority-validation.v1'; status='passed'; generatedAt='2026-09-01T06:15:00Z'
    sourceCheckpoint=$sourceCheckpoint
    contracts=[ordered]@{ schemasChecked=$contracts.schemasChecked; validFixturesAccepted=$contracts.validFixturesAccepted; invalidFixturesRejected=$contracts.invalidFixturesRejected; frozenContractsV1='unchanged-by-this-release' }
    coreAdapter=[ordered]@{ groups=@($coreAdapter.document.results).Count; stdout=$coreAdapter.stdout; stderr=$coreAdapter.stderr; ankiInitializeManaged=$ankiGate[0]; playbackStopCurrentRun='passed'; originalMediaAnkiOnly='passed'; remnoteExactTitleDiscovery='passed' }
    orchestrator=[ordered]@{ tests=@($orchestrator.document.tests).Count; stdout=$orchestrator.stdout; stderr=$orchestrator.stderr; status='passed' }
    formalWorkers=[ordered]@{ ankiAdmission=$ankiAdmission; ankiManagedInitialization=$ankiQualification; remnote230=$remnoteRelease }
    retainedEvidence=[ordered]@{ historicalPlaybackRace=$historicalPlayback; historicalFailureStatePreserved=$true; desktopProducerR14=$desktopQuality }
    safety=[ordered]@{ realAnkiInitializationRuns=0; realAnkiConnectCalls=0; remnoteWrites=0; externalWrites=0; physicalSpeakerInvocations=0; automaticRetryAfterUnknownExternalState=$false }
})

[ordered]@{ status='passed'; path=[IO.Path]::GetRelativePath($ProjectRoot,$receiptPath).Replace('\','/'); sha256=Sha $receiptPath } | ConvertTo-Json -Depth 8

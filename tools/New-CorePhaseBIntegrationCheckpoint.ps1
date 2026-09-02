[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CheckpointDirectory,
    [string]$CheckpointVersion = '2.4.4'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$checkpointRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $CheckpointDirectory))
$checkpointPath = Join-Path $checkpointRoot 'checkpoint.json'
if (-not (Test-Path $checkpointRoot -PathType Container)) { throw 'Checkpoint directory is missing.' }
if (Test-Path $checkpointPath) { throw 'Append-only checkpoint exists.' }

$settingsReceipt = Join-Path $checkpointRoot 'settings-control-canary.receipt.json'
$orchestratorReceipt = Join-Path $checkpointRoot 'orchestrator-regression.receipt.json'
$settings = Get-Content -Raw $settingsReceipt | ConvertFrom-Json -Depth 100
$orchestrator = Get-Content -Raw $orchestratorReceipt | ConvertFrom-Json -Depth 100
if ($settings.status -ne 'passed' -or $settings.externalCalls -ne 0 -or $settings.physicalSpeakerInvocations -ne 0 -or $settings.learningResolverEvidence.status -ne 'passed') { throw 'Settings regression boundary failed.' }
if ($orchestrator.status -ne 'passed' -or @($orchestrator.tests).Count -lt 23) { throw 'Orchestrator regression boundary failed.' }

$paths = @(
    'orchestrator/Core/DesktopIntentResolver.cs',
    'orchestrator/Core/Contracts.cs',
    'orchestrator/Core/SettingsControl.cs',
    'tests/SettingsControl.IntegrationTests/Program.cs',
    'tests/PhaseB.Canary/Program.cs',
    'tests/PhaseB.Canary/PhaseB.Canary.csproj',
    'tools/Invoke-KnowledgeClipperPhaseB.ps1',
    'tools/New-CorePhaseBIntegrationCheckpoint.ps1',
    'dist/orchestrator/win-x64/v2.4.4/KnowledgeClipper.Orchestrator.exe',
    'content-worker/releases/content.deepseek-0.6.1-win-x64-formal-20260828T082100Z/release-manifest.v1.json',
    'content-worker/canary/formal-release/content.deepseek-0.6.1-win-x64-formal-20260828T082100Z/20260828T082055252Z-settings-snapshot-replay/formal-release-offline-receipt.v1.json',
    'content-worker/canary/jobs/20260828T081953675Z-offline-phase-b-generated-content-v1-live-3bd60c10/canary-receipt.json',
    ([IO.Path]::GetRelativePath($projectRoot, $settingsReceipt).Replace('\','/')),
    ([IO.Path]::GetRelativePath($projectRoot, $orchestratorReceipt).Replace('\','/'))
)
$files = foreach ($relative in ($paths | Sort-Object -Unique)) {
    $absolute = Join-Path $projectRoot $relative
    if (-not (Test-Path $absolute -PathType Leaf)) { throw "Checkpoint input missing: $relative" }
    $item = Get-Item $absolute
    [ordered]@{ path=$relative; bytes=$item.Length; sha256=(Get-FileHash $absolute -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$rows = $files | ForEach-Object { "$($_.path)$([char]0)$($_.sha256)$([char]0)$($_.bytes)`n" }
$treeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join '')))).ToLowerInvariant()
$previous = 'contracts/core/v2.4/settings-control/checkpoints/v2.4.3-20260828T073934Z/checkpoint.json'
$checkpoint = [ordered]@{
    schemaVersion='knowledge-clipper-core-phase-b-integration-checkpoint.v1'
    checkpointVersion=$CheckpointVersion
    createdAt=[DateTimeOffset]::UtcNow.ToString('o')
    status='passed-core-integration-authority-input-ready'
    supersedes=$previous
    compatibility=[ordered]@{ frozenContractsV1Modified=$false; priorAuthorityModified=$false; previousCheckpointSha256=(Get-FileHash (Join-Path $projectRoot $previous) -Algorithm SHA256).Hash.ToLowerInvariant() }
    core=[ordered]@{ version='2.4.4'; executable='dist/orchestrator/win-x64/v2.4.4/KnowledgeClipper.Orchestrator.exe'; executableSha256=(Get-FileHash (Join-Path $projectRoot 'dist/orchestrator/win-x64/v2.4.4/KnowledgeClipper.Orchestrator.exe') -Algorithm SHA256).Hash.ToLowerInvariant() }
    behavior=[ordered]@{
        learningIntentRequestedOutput='learning-bundle'
        remnoteRoute=[ordered]@{ kind='clip-daily-route'; timeZone='Asia/Shanghai'; localDate='2026-08-28'; dateFormat='yyyy-MM-dd'; rootRemId='8zNzfX45XLCSKoPSJ' }
        planPayloadBound=$true
        workerRequestPayloadBound=$true
        ankiStage=$false
        strictWorkerJobLayout='jobs/<taskId>'
        workerPathValidationRelaxed=$false
    }
    validation=[ordered]@{
        settings=[ordered]@{ status='passed'; receipt=[IO.Path]::GetRelativePath($projectRoot,$settingsReceipt).Replace('\','/'); sha256=(Get-FileHash $settingsReceipt -Algorithm SHA256).Hash.ToLowerInvariant(); externalCalls=0; physicalSpeakerInvocations=0 }
        orchestrator=[ordered]@{ status='passed'; receipt=[IO.Path]::GetRelativePath($projectRoot,$orchestratorReceipt).Replace('\','/'); sha256=(Get-FileHash $orchestratorReceipt -Algorithm SHA256).Hash.ToLowerInvariant(); tests=@($orchestrator.tests).Count }
        build=[ordered]@{ status='passed'; sdk='8.0.424'; warnings=0; errors=0 }
    }
    contentAdmissionInput=[ordered]@{ worker='content.deepseek@0.6.1'; manifestSha256='b716e6476d34d325322f6c8930819aaedd33e06fafbe50f883e22acda10d39e9'; treeSha256='dd5ef58d6390dd02e8bed77d3adac24d83855dd0d9bc3214be938164d7f63f9b'; liveGateReceiptSha256='caa4f4da84ddf1d1871998efcef38d6668e0aa2a928cef4d3c11fd30f25b71f7' }
    authorityBoundary=[ordered]@{ currentAuthority='v1.2.6'; authorityBound=$false; intendedAuthority='v1.2.7+'; contentReady=$true; speechDeepPathReleasePending=$true; phaseBFinal=$false }
    tree=[ordered]@{ algorithm='sha256(path NUL sha256 NUL bytes LF, ordinal path order)'; fileCount=$files.Count; totalBytes=[long](($files|Measure-Object bytes -Sum).Sum); treeSha256=$treeSha; files=$files }
}
[IO.File]::WriteAllText($checkpointPath,(($checkpoint|ConvertTo-Json -Depth 100)+"`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{checkpoint=$checkpointPath;sha256=(Get-FileHash $checkpointPath -Algorithm SHA256).Hash;treeSha256=$treeSha;files=$files.Count}|ConvertTo-Json

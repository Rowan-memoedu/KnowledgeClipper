[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$RuntimeVersion = '2.5.0',
    [string]$CheckpointVersion = '2.5.1',
    [string]$ReceiptRoot = 'canary/core-v2.5-offline/20260830T075000Z',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$checkpointRelative = "contracts/core/v2.5/checkpoints/v$CheckpointVersion-$ReleaseStamp/checkpoint.json"
$checkpointPath = Join-Path $ProjectRoot $checkpointRelative
if (Test-Path -LiteralPath $checkpointPath) { throw "Append-only checkpoint exists: $checkpointPath" }

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Relative([string]$Path) { [IO.Path]::GetRelativePath($ProjectRoot, $Path).Replace('\', '/') }
function RequireFile([string]$RelativePath) {
    $path = Join-Path $ProjectRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checkpoint input is missing: $RelativePath" }
    $path
}
function ReadJson([string]$RelativePath) { Get-Content -LiteralPath (RequireFile $RelativePath) -Raw | ConvertFrom-Json -Depth 100 }
function Describe([string]$RelativeRoot, [string]$Executable, [string]$WorkerId, [string]$Version, [string]$ExpectedSha) {
    $path = RequireFile ((Join-Path $RelativeRoot $Executable).Replace('\', '/'))
    if ((Sha $path) -ne $ExpectedSha) { throw "Worker executable drifted: $WorkerId" }
    $document = (& $path describe --json 2>&1) -join [Environment]::NewLine | ConvertFrom-Json -Depth 100
    if ($LASTEXITCODE -ne 0 -or $document.workerId -ne $WorkerId -or $document.workerVersion -ne $Version -or $document.contractVersion -ne '1.0.0') {
        throw "Worker describe admission failed: $WorkerId"
    }
    [ordered]@{ workerId = $WorkerId; workerVersion = $Version; executable = Relative $path; bytes = (Get-Item $path).Length; sha256 = Sha $path; describe = $document }
}

$settingsPath = "$ReceiptRoot/settings-control.receipt.json"
$adapterPath = "$ReceiptRoot/core-adapter.receipt.json"
$contractPath = "$ReceiptRoot/orchestrator-contract-tests.receipt.json"
$settings = ReadJson $settingsPath
$adapter = ReadJson $adapterPath
$contract = ReadJson $contractPath
if ($settings.status -ne 'passed' -or $settings.externalCalls -ne 0 -or $settings.physicalSpeakerInvocations -ne 0) { throw 'Settings receipt failed its offline boundary.' }
if ($settings.moduleV3Evidence.contentConfigSchemaVersion -ne 'content-config-snapshot/1.1.0' -or $settings.moduleV3Evidence.language.strategy -ne 'override' -or -not $settings.moduleV3Evidence.activeTaskFrozen) { throw 'Settings v3 receipt does not prove Content/Speech binding.' }
if ($settings.ankiProbeEvidence.status -ne 'passed' -or $settings.ankiProbeEvidence.externalMutation) { throw 'Anki probe receipt is not read-only and passed.' }
if ($adapter.status -ne 'passed' -or $adapter.results.Count -ne 11) { throw 'CoreAdapter receipt is incomplete.' }
$original = $adapter.results | Where-Object name -eq 'original-media-destination-neutral-boundary'
$partial = $adapter.results | Where-Object name -eq 'image-partial-failed-items-only'
$unknown = $adapter.results | Where-Object name -eq 'image-unknown-external-no-auto-retry'
if ($original.taskStatus -ne 'partial' -or $original.providerCalls -ne 0 -or $original.downstreamStarted -or $partial.attempts -ne 2 -or -not $partial.downstreamAfterMergedCompletion -or $unknown.attempts -ne 1) { throw 'Image orchestration receipt failed.' }
if ($contract.status -ne 'passed' -or $contract.tests.Count -ne 23) { throw 'Orchestrator contract receipt is incomplete.' }

$contractValidation = ((& python (Join-Path $ProjectRoot 'tools/validate_contracts.py') 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
if ($LASTEXITCODE -ne 0 -or $contractValidation.schemasChecked -ne 64 -or $contractValidation.validFixturesAccepted -ne 67 -or $contractValidation.invalidFixturesRejected -ne 55) { throw 'Public contract validation failed.' }

$program = Get-Content -LiteralPath (RequireFile 'src/Program.cs') -Raw
$startup = [regex]::Match($program, 'internal static class Program[\s\S]*?internal enum ReviewChoice').Value
$retentionDisabled = $startup.Contains('RetentionStatus = "disabled-until-state-aware-retention-service"') -and -not [regex]::IsMatch($startup, '(?m)^\s*JobStore\.PurgeExpired\s*\(')
if (-not $retentionDisabled) { throw 'Production retention is not fail-closed.' }

$runtime = RequireFile "dist/orchestrator/win-x64/v$RuntimeVersion/KnowledgeClipper.Orchestrator.exe"
$runtimeDescribe = (& $runtime describe | ConvertFrom-Json -Depth 20)
if ($runtimeDescribe.version -notlike "$RuntimeVersion*") { throw 'Runtime version identity drifted.' }

$modules = [ordered]@{
    image = Describe 'workers/image-ocr/candidates/1.2.2' 'KnowledgeClipper.ImageOcr.Worker.exe' 'image.ocr.mineru' '1.2.2' '8F99C403AF250E0BD2BB62CEECAA74A768860C10B3C582236BA0A6B3B0D73F78'
    content = Describe 'content-worker/releases/content.deepseek-0.7.0-win-x64-formal-20260828T103735Z' 'KnowledgeClipper.Content.Worker.exe' 'content.deepseek' '0.7.0' 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'
    speech = Describe 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260830T073825Z-c0980038/win-x64/workers/speech' 'KnowledgeClipper.Speech.Worker.exe' 'speech.edge' '1.2.4' 'C098003823B5217C3A4F7267A57E69128AE5F6DF7BCC8083BA92B4CC56C34EE2'
    remnote = Describe 'releases/knowledge-clipper-remnote-exporter-v2.1.0-20260830T073620Z/win-x64' 'KnowledgeClipper.RemNoteExporter.Worker.exe' 'export.remnote' '2.1.0' '443835ECA91E5F68C97070777C250FD95E4A2E4873C9EFC7FBF45A528EF2CAD2'
    anki = Describe 'releases/knowledge-clipper-anki-exporter-v1.2.1-20260828T104000Z/win-x64/workers/anki' 'KnowledgeClipper.AnkiExporter.Worker.exe' 'export.anki' '1.2.1' '135B7BC31DA93389819E4F802226600CD3879F6527ED4194555D613A32CC4EE1'
}

$moduleReceipts = [ordered]@{
    image = [ordered]@{ path = 'workers/image-ocr/canary/core-admission-1.2.2-20260830T074207Z/core-admission-receipt.v1.json'; sha256 = '8833618214090F8871A86F2693B74B3C996E7DCD0570441D2D17C0214228FBA7'; treeSha256 = 'F0F8FA5D5BC3210D8CA01CEC8707B29891A8ECFFBE6B533982990BE15FC03B89' }
    content = [ordered]@{ path = 'content-worker/receipts/core-v3-desktop-compatibility/20260830T074659078Z/receipt.v1.json'; sha256 = 'BF933C31A6D0D16C51313CDD6B71D58EB493A11D23F5A01DCFA8A762FCF9513C'; releaseManifest = 'content-worker/releases/content.deepseek-0.7.0-win-x64-formal-20260828T103735Z/release-manifest.v1.json'; releaseManifestSha256 = '552F29D7BD1EF848DA1D7587EE664DD1F68DD23EA6E8228E52198C9BF6D2CCB9'; treeSha256 = '724F2D2732FA13FA236825F7E82E8E8E8DAF4DC1354E36093AB8B79262EDFBEC' }
    speech = [ordered]@{ path = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260830T073825Z-c0980038/speech-formal-win-x64-release-receipt.json'; sha256 = '91250CAB1E4441EBC5EE9324B114A27D1CE3D84BC3B95C26F374003CDA3B7E70'; treeSha256 = '985E678EEFD7335F772F21483BF39763544B57260E4449DFC3C017B2E31FB38' }
    remnote = [ordered]@{ path = 'releases/knowledge-clipper-remnote-exporter-v2.1.0-20260830T073620Z/win-x64/release-receipt.json'; sha256 = 'B290E8FFBA1011130CD740DEB806AC52D76B1179FD0CD51DF9CEA5A89A52A1A2'; treeSha256 = 'A2C5F932B79F2528F2F1B8E30E9B68D3C15AEE07FE7F3AE8E834BF698C675CA8' }
    anki = [ordered]@{ path = 'workers/anki-exporter/canary/shared-consumer-compatibility/20260830T074135Z-28e90aac/receipt.json'; sha256 = '3474DF53E79744A69F49CEB8F2009EC9B76A1120C77EB8BD2400227A2D20EB5F'; treeSha256 = 'E7EBA348C344865B0F403D34187831BABDEADC4818451084ACB86CC37A3E161D' }
}
foreach ($receipt in $moduleReceipts.Values) {
    $path = RequireFile $receipt.path
    if ((Sha $path) -ne $receipt.sha256) { throw "Module receipt drifted: $($receipt.path)" }
    if ($receipt.releaseManifest) {
        $manifest = RequireFile $receipt.releaseManifest
        if ((Sha $manifest) -ne $receipt.releaseManifestSha256) { throw "Module release manifest drifted: $($receipt.releaseManifest)" }
    }
}

$fixedFiles = @(
    'orchestrator/KnowledgeClipper.Orchestrator.csproj', 'orchestrator/Program.cs',
    'tests/FakeWorker/FakeWorker.csproj', 'tests/FakeWorker/Program.cs',
    'tests/SettingsControl.IntegrationTests/SettingsControl.IntegrationTests.csproj', 'tests/SettingsControl.IntegrationTests/Program.cs',
    'tests/CoreAdapter.IntegrationTests/CoreAdapter.IntegrationTests.csproj', 'tests/CoreAdapter.IntegrationTests/Program.cs',
    'tests/Orchestrator.ContractTests/Orchestrator.ContractTests.csproj', 'tests/Orchestrator.ContractTests/Program.cs',
    'tools/validate_contracts.py', 'tools/test-desktop-brand-surface.ps1', 'tools/New-CoreV25Checkpoint.ps1',
    'src/Program.cs', 'receipts/knowledge-clipper-requirement-audit-20260828T103725Z.md',
    'contracts/core/v2.5/checkpoints/v2.5.0-20260830T075200Z/invalidation-receipt.v1.json',
    $settingsPath, $adapterPath, $contractPath,
    "dist/orchestrator/win-x64/v$RuntimeVersion/KnowledgeClipper.Orchestrator.exe"
)
$sourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'orchestrator/Core') -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'contracts/core/v2.4/settings-control') -Recurse -File | Where-Object FullName -NotMatch '[\\/]checkpoints[\\/]'
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'contracts/core/v2.5') -Recurse -File | Where-Object FullName -NotMatch '[\\/]checkpoints[\\/]'
)
$allPaths = @($fixedFiles + ($sourceFiles | ForEach-Object { Relative $_.FullName }) + ($moduleReceipts.Values | ForEach-Object { $_.path }) + ($moduleReceipts.Values | ForEach-Object { if ($_.releaseManifest) { $_.releaseManifest } })) | Sort-Object -Unique
$entries = foreach ($relative in $allPaths) {
    $path = RequireFile $relative
    [ordered]@{ path = $relative; bytes = (Get-Item $path).Length; sha256 = (Sha $path).ToLowerInvariant() }
}
$treeRows = $entries | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" }
$treeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($treeRows -join "`n"))))
$totalBytes = [long](($entries | ForEach-Object { [long]$_['bytes'] } | Measure-Object -Sum).Sum)

$checkpoint = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-integration-checkpoint.v2'
    checkpointVersion = $CheckpointVersion
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed-offline-authority-admission-ready'
    supersedes = [ordered]@{ path = 'contracts/core/v2.4/settings-control/checkpoints/v2.4.8-20260828T092700Z/checkpoint.json'; sha256 = '3306DF313EB6AACF52AE1A447F3F095C95295605A2D68A451ADA5D00AE41C846' }
    core = [ordered]@{ version = $RuntimeVersion; describedVersion = $runtimeDescribe.version; executable = "dist/orchestrator/win-x64/v$RuntimeVersion/KnowledgeClipper.Orchestrator.exe"; bytes = (Get-Item $runtime).Length; sha256 = Sha $runtime }
    publicVersions = [ordered]@{ workflowSettings = 'knowledge-clipper-workflow-settings.v3'; taskOverride = 'knowledge-clipper-task-settings-override.v3'; taskSnapshot = 'knowledge-clipper-task-settings-snapshot.v3'; moduleAdapter = 'knowledge-clipper-module-settings-adapter.v3'; pipelinePlan = '2.1.0'; workerRequestEnvelope = '1.0.0' }
    validation = [ordered]@{
        contracts = $contractValidation
        settings = [ordered]@{ path = $settingsPath; sha256 = Sha (RequireFile $settingsPath); status = $settings.status; externalCalls = 0; physicalSpeakerInvocations = 0 }
        coreAdapter = [ordered]@{ path = $adapterPath; sha256 = Sha (RequireFile $adapterPath); status = $adapter.status; groups = $adapter.results.Count }
        orchestrator = [ordered]@{ path = $contractPath; sha256 = Sha (RequireFile $contractPath); status = $contract.status; tests = $contract.tests.Count }
        productionRetention = [ordered]@{ status = 'disabled-fail-closed'; legacyStartupInvocations = 0; stateAwareRetentionService = 'conditional-not-implemented' }
    }
    requirements = [ordered]@{
        originalImage = [ordered]@{ status = 'terminal-unsupported'; worker = 'image.ocr.mineru'; outputKind = 'image.original'; providerCalls = 0; downstreamStarted = $false }
        imagePartial = [ordered]@{ status = 'failed-items-only'; wholeCardBlocksDownstreamUntilMergedCompletion = $true; successfulItemsReusedByHash = $true; unknownExternalAutomaticRetry = $false }
        contentSettings = [ordered]@{ configSchemaVersion = 'content-config-snapshot/1.1.0'; runtimePolicyFrozen = $true; promptAndCategoryShaFrozen = $true; promptAfterRecognition = 'conditional-core-ui' }
        speechSettings = [ordered]@{ languageOverrideBound = $true; deploymentPathsStageScoped = $true; effectiveAt = 'next_task'; speakerPreview = 'conditional-explicit-physical-audio-not-run' }
        ankiProbe = [ordered]@{ status = 'supported'; invocation = 'explicit_only'; effectClass = 'provider-read-only'; externalMutation = $false }
        remnoteProbe = [ordered]@{ status = 'conditional-cli-only'; externalMutation = $false }
    }
    modules = $modules
    moduleReceipts = $moduleReceipts
    compatibility = [ordered]@{
        frozenContractsV1Modified = $false; priorAuthorityModified = $false; externalCalls = 0; mineruCalls = 0; remnoteWrites = 0; ankiWrites = 0; physicalSpeakerInvocations = 0
        invalidatedGenerationAttempt = [ordered]@{ path = 'contracts/core/v2.5/checkpoints/v2.5.0-20260830T075200Z/checkpoint.json'; sha256 = '52119B00F9D548B70B09FF5F5B54904B66DAB85D674D75E9CB8FE9A2D68B4B42'; authorityBound = $false }
    }
    authorityBoundary = [ordered]@{ currentAuthority = '1.2.11'; intendedAuthority = '1.2.12'; promotion = 'pending-append-only-authority-assembly' }
    tree = [ordered]@{ algorithm = 'ordinal relative path; path TAB bytes TAB lowercase sha256; LF; SHA-256 UTF-8'; fileCount = $entries.Count; totalBytes = $totalBytes; treeSha256 = $treeSha; files = $entries }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($checkpointPath)) | Out-Null
[IO.File]::WriteAllText($checkpointPath, (($checkpoint | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ checkpoint = $checkpointRelative; sha256 = Sha $checkpointPath; treeSha256 = $treeSha; fileCount = $entries.Count; totalBytes = $totalBytes } | ConvertTo-Json -Depth 10

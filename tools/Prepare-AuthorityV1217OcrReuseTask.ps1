[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePackageRoot,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$ReleaseRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\releases\knowledge-clipper-authority-v1.2.17-20260831T062800Z\win-x64',
    [string]$AuthorityRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\contracts\authority\worker-registry\v1.2.17-20260831T062800Z',
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$source = [IO.Path]::GetFullPath($SourcePackageRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$release = [IO.Path]::GetFullPath($ReleaseRoot)
$authority = [IO.Path]::GetFullPath($AuthorityRoot)
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Source Image package is missing: $source" }
if (Test-Path -LiteralPath $evidence) { throw "Append-only evidence root already exists: $evidence" }

function Get-Sha256Bytes([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Get-Sha256File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-BytesOnce([string]$Path, [byte[]]$Bytes) {
    if (Test-Path -LiteralPath $Path) { throw "Append-only file already exists: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
}
function Write-JsonOnce([string]$Path, $Value) { Write-BytesOnce $Path $utf8.GetBytes((($Value | ConvertTo-Json -Depth 100) + "`n")) }
function New-ArtifactManifest([string]$JobRoot, [string]$ArtifactId, [string]$Kind, [string]$MediaType, [array]$Files, [string]$Entrypoint, [array]$InputRefs = @()) {
    $relative = "artifacts/$ArtifactId/artifact.json"
    $path = Join-Path $JobRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $path ([ordered]@{
        schemaVersion = '1.0.0'; artifactId = $ArtifactId; kind = $Kind; contractVersion = '1.0.0'; mediaType = $MediaType
        files = $Files; createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        producer = [ordered]@{ stageId = 'task-settings'; workerId = 'orchestrator.settings-adapter'; workerVersion = '1.0.0'; attemptId = ('settings-' + [Guid]::NewGuid().ToString('N')) }
        inputRefs = $InputRefs; dataClass = 'sensitive'; metadata = [ordered]@{ entrypoint = $Entrypoint }
    })
    [ordered]@{ artifactRef = $ArtifactId; kind = $Kind; contractVersion = '1.0.0'; manifestPath = $relative; expectedSha256 = Get-Sha256File $path }
}

$sourceJob = @(Get-ChildItem -LiteralPath (Join-Path $source 'jobs') -Directory)
if ($sourceJob.Count -ne 1) { throw 'Source Image package must contain exactly one job.' }
$priorRequestPath = @(Get-ChildItem -LiteralPath $sourceJob[0].FullName -Recurse -Filter request.json | Where-Object FullName -Match '[\\/]stages[\\/]image-ocr[\\/]attempts[\\/]attempt-0001[\\/]request.json$')
if ($priorRequestPath.Count -ne 1) { throw 'Source Image package must contain exactly one attempt-0001 Image request.' }
$priorRequest = Get-Content -LiteralPath $priorRequestPath[0].FullName -Raw | ConvertFrom-Json -Depth 100
$priorResultPath = Join-Path $priorRequestPath[0].DirectoryName 'result.json'
$priorResult = Get-Content -LiteralPath $priorResultPath -Raw | ConvertFrom-Json -Depth 100
if ($priorRequest.taskId -ne $sourceJob[0].Name -or $priorRequest.workerId -ne 'image.ocr.mineru' -or $priorResult.status -ne 'completed') {
    throw 'Source Image request/result identity is not a completed reusable Image attempt.'
}
$captureReference = @($priorRequest.inputRefs | Where-Object kind -eq 'capture-bundle')
if ($captureReference.Count -ne 1) { throw 'Source Image request must identify exactly one CaptureBundle.' }

[IO.Directory]::CreateDirectory($evidence) | Out-Null
$jobsRoot = Join-Path $evidence 'jobs'
[IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
$jobRoot = Join-Path $jobsRoot ([string]$priorRequest.taskId)
Copy-Item -LiteralPath $sourceJob[0].FullName -Destination $jobRoot -Recurse
foreach ($manifestName in @('task.manifest.v1.json','task.manifest.v2.json')) {
    if (Test-Path -LiteralPath (Join-Path $jobRoot $manifestName)) { throw "Copied source unexpectedly contains $manifestName." }
}
[IO.Directory]::CreateDirectory((Join-Path $jobRoot 'plans')) | Out-Null

$ankiTemplate = Join-Path $release 'workers\anki\config\anki-export.v1.example.json'
$ankiConfig = Get-Content -LiteralPath $ankiTemplate -Raw | ConvertFrom-Json -Depth 100
$ankiConfig.endpoint = 'http://127.0.0.1:8765'
$ankiConfig.expected_profile = '__PREFLIGHT_PROFILE__'
$ankiConfig.provision_managed = $false
foreach ($property in @($ankiConfig.deck_routes.PSObject.Properties)) { $ankiConfig.deck_routes.($property.Name) = $AnkiDeck }
$ankiId = 'anki-config-authority-v1217'
$ankiPayloadRelative = "artifacts/$ankiId/anki-export.v1.json"
$ankiPayloadPath = Join-Path $jobRoot $ankiPayloadRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
Write-JsonOnce $ankiPayloadPath $ankiConfig
$ankiReference = New-ArtifactManifest $jobRoot $ankiId 'anki-export-config' 'application/json' @(
    [ordered]@{ path = $ankiPayloadRelative; sha256 = Get-Sha256File $ankiPayloadPath; bytes = (Get-Item -LiteralPath $ankiPayloadPath).Length; mediaType = 'application/json' }
) $ankiPayloadRelative

$sourceSettings = [ordered]@{
    schemaVersion = 'knowledge-clipper-workflow-settings.v3'; activeProfileId = 'sealed-ocr-ready-anki-v1217'
    workflow = [ordered]@{ processingProfile = 'cards'; destinations = @('anki') }
    capture = [ordered]@{ contextEnabled = $false; contextGranularity = 'sentence'; imageHandling = 'ocr' }
    imageOcr = [ordered]@{ schemaVersion = 'image-ocr-settings.v1'; providerSettings = $priorRequest.configSnapshot.providerSettings; mineruScriptPath = [string]$priorRequest.configSnapshot.mineruScriptPath }
    content = [ordered]@{ promptSet = 'open-md-v2'; categoryRegistry = 'category-registry/2.0.0'; providerMode = 'live' }
    ankiConfigArtifactRef = $ankiReference.artifactRef; ankiConfigSha256 = $ankiReference.expectedSha256
}
$sourceSettingsSha = Get-Sha256Bytes $utf8.GetBytes(($sourceSettings | ConvertTo-Json -Depth 100 -Compress))
$snapshot = [ordered]@{
    schemaVersion = 'knowledge-clipper-task-settings-snapshot.v3'; taskId = [string]$priorRequest.taskId; frozenAt = [DateTimeOffset]::UtcNow.ToString('o')
    source = [ordered]@{ activeProfileId = 'sealed-ocr-ready-anki-v1217'; settingsSha256 = $sourceSettingsSha; precedence = @('taskOverride','activeProfile','moduleDefault') }
    workflow = [ordered]@{ processingProfile = 'cards'; destinations = @('anki') }
    modules = [ordered]@{
        capture = [ordered]@{ contextEnabled = $false; contextGranularity = 'sentence'; imageHandling = 'ocr' }
        imageOcr = [ordered]@{ schemaVersion = 'image-ocr-settings.v1'; providerSettings = $priorRequest.configSnapshot.providerSettings; mineruScriptPath = [string]$priorRequest.configSnapshot.mineruScriptPath }
        content = [ordered]@{ promptSet = 'open-md-v2'; categoryRegistry = 'category-registry/2.0.0'; providerMode = 'live' }
        anki = [ordered]@{ configArtifactRef = $ankiReference.artifactRef; expectedSha256 = $ankiReference.expectedSha256; secretReferences = @() }
    }
    secretReferences = @()
}
$snapshotBytes = $utf8.GetBytes((($snapshot | ConvertTo-Json -Depth 100) + "`n"))
$snapshotId = 'task-settings-snapshot-' + (Get-Sha256Bytes $snapshotBytes).Substring(0,24)
$snapshotRelative = "artifacts/$snapshotId/task-settings-snapshot.v3.json"
$snapshotPath = Join-Path $jobRoot $snapshotRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
Write-BytesOnce $snapshotPath $snapshotBytes
$settingsReference = New-ArtifactManifest $jobRoot $snapshotId 'task-settings-snapshot' 'application/vnd.knowledge-clipper.task-settings-snapshot+json' @(
    [ordered]@{ path = $snapshotRelative; sha256 = Get-Sha256File $snapshotPath; bytes = (Get-Item -LiteralPath $snapshotPath).Length; mediaType = 'application/json' }
) $snapshotRelative @($ankiReference)

$intentRelative = 'plans/desktop-intent.v1.json'
$intentPath = Join-Path $jobRoot $intentRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
Write-JsonOnce $intentPath ([ordered]@{ schemaVersion = 'desktop-intent.v1'; taskId = [string]$priorRequest.taskId; intent = 'clip.card'; inputArtifactRef = [string]$captureReference[0].artifactRef; requestedOutputs = @('card-bundle') })
$refs = @($captureReference[0], $ankiReference, $settingsReference)
$inputRefs = @($refs | ForEach-Object { [ordered]@{ artifactRef = $_.artifactRef; kind = $_.kind; contractVersion = $_.contractVersion; expectedSha256 = $_.expectedSha256 } })
$createdAt = [DateTimeOffset]::UtcNow.ToString('o')
Write-JsonOnce (Join-Path $jobRoot 'task.manifest.v1.json') ([ordered]@{
    schemaVersion = '1.0.0'; taskId = [string]$priorRequest.taskId; createdAt = $createdAt
    createdBy = [ordered]@{ application = 'KnowledgeClipper.AuthorityV1217OcrReuseCanary'; version = '1.0.0' }
    intent = 'clip.card'; executionProfile = 'cards-anki'; inputRefs = $inputRefs; requestedOutputs = @('card-bundle')
    initialPlanRef = $intentRelative; retentionClass = 'evidence'
    metadata = [ordered]@{ compatibilityRole = 'frozen-v1-worker-loader'; authoritativeManifest = 'task.manifest.v2.json'; sourceImagePackage = $source; authorizedAnkiDeck = $AnkiDeck }
})
$manifestPath = Join-Path $jobRoot 'task.manifest.v2.json'
Write-JsonOnce $manifestPath ([ordered]@{
    schemaVersion = '2.0.0'; taskId = [string]$priorRequest.taskId; createdAt = $createdAt
    createdBy = [ordered]@{ application = 'KnowledgeClipper.AuthorityV1217OcrReuseCanary'; version = '1.0.0' }
    intent = 'clip.card'; executionProfile = 'cards'; destinations = @('anki'); inputRefs = $inputRefs; requestedOutputs = @('card-bundle')
    initialPlanRef = $intentRelative; retentionClass = 'evidence'
    metadata = [ordered]@{ canary = 'sealed-actual-orchestrator-ocr-reuse-anki-v1.2.17'; sourceImagePackage = $source; authorizedAnkiDeck = $AnkiDeck; remnote = $false; speech = $false; imageWorkerRerunAllowed = $false }
})

$registryPath = Join-Path $authority 'worker-registry.v1.json'
$adapterConfig = Join-Path $release 'contracts\core\v2.1\profile-resolver.v2.1.json'
$preflightDll = Join-Path $PSScriptRoot 'ReuseKeyPreflight\bin\Release\net8.0\ReuseKeyPreflight.dll'
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$preflightRunId = 'run-reuse-preflight-' + [Guid]::NewGuid().ToString('N')
$copiedPriorRequest = Join-Path $jobRoot ([IO.Path]::GetRelativePath($sourceJob[0].FullName, $priorRequestPath[0].FullName))
$preflightText = & $dotnet $preflightDll $jobRoot $intentPath $registryPath $adapterConfig $preflightRunId $copiedPriorRequest
if ($LASTEXITCODE -ne 0) { throw "Image reuse-key preflight rejected the staged task (exit $LASTEXITCODE).`n$preflightText" }
$preflight = $preflightText | ConvertFrom-Json -Depth 100
if ($preflight.status -ne 'passed' -or $preflight.computedIdempotencyKey -ne $preflight.priorRequestIdempotencyKey) { throw 'Image reuse-key preflight did not prove a cache hit.' }
Write-BytesOnce (Join-Path $evidence 'reuse-key-preflight.json') $utf8.GetBytes(($preflightText.Trim() + "`n"))
Write-JsonOnce (Join-Path $evidence 'preparation-receipt.json') ([ordered]@{
    schemaVersion = 'knowledge-clipper-authority-v1217-ocr-reuse-preparation.v1'; status = 'passed'; preparedAt = [DateTimeOffset]::UtcNow.ToString('o')
    sourcePackage = [ordered]@{ root = $source; taskId = [string]$priorRequest.taskId; runId = [string]$priorRequest.runId; requestSha256 = Get-Sha256File $priorRequestPath[0].FullName; resultSha256 = Get-Sha256File $priorResultPath }
    stagedTask = [ordered]@{ jobRoot = $jobRoot; manifestPath = $manifestPath; manifestSha256 = Get-Sha256File $manifestPath; ankiConfigManifestSha256 = $ankiReference.expectedSha256; settingsManifestSha256 = $settingsReference.expectedSha256 }
    reuse = $preflight
    safety = [ordered]@{ orchestratorStarted = $false; imageWorkerStarts = 0; mineruProviderSubmissions = 0; deepSeekCalls = 0; ankiWrites = 0; remnoteCalls = 0 }
})

[ordered]@{ status = 'passed'; evidenceRoot = $evidence; jobRoot = $jobRoot; taskId = [string]$priorRequest.taskId; manifestPath = $manifestPath; reuseKey = $preflight.computedIdempotencyKey } | ConvertTo-Json -Depth 20

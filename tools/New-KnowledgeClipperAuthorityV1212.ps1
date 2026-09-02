[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$RegistryVersion = '1.2.12',
    [string]$CoreVersion = '2.5.0',
    [string]$CoreCheckpoint = 'contracts/core/v2.5/checkpoints/v2.5.1-20260830T075400Z/checkpoint.json',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$releaseId = "knowledge-clipper-authority-v$RegistryVersion-$ReleaseStamp"
$releaseContainer = Join-Path $ProjectRoot "releases/$releaseId"
$releaseRoot = Join-Path $releaseContainer 'win-x64'
$authorityRoot = Join-Path $ProjectRoot "contracts/authority/worker-registry/v$RegistryVersion-$ReleaseStamp"
$stagingContainer = Join-Path $ProjectRoot ("releases/.$releaseId." + [Guid]::NewGuid().ToString('N') + '.tmp')
$stagingRelease = Join-Path $stagingContainer 'win-x64'
$stagingAuthority = Join-Path $ProjectRoot ("contracts/authority/worker-registry/.v$RegistryVersion-$ReleaseStamp." + [Guid]::NewGuid().ToString('N') + '.tmp')
$baseRelease = Join-Path $ProjectRoot 'releases/knowledge-clipper-authority-v1.2.11-20260828T092800Z/win-x64'
$baseRegistry = Join-Path $ProjectRoot 'contracts/authority/worker-registry/v1.2.11-20260828T092800Z/worker-registry.v1.json'
$coreExecutable = Join-Path $ProjectRoot "dist/orchestrator/win-x64/v$CoreVersion/KnowledgeClipper.Orchestrator.exe"
$coreCheckpointPath = Join-Path $ProjectRoot $CoreCheckpoint

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Relative([string]$Path) { [IO.Path]::GetRelativePath($ProjectRoot, $Path).Replace('\', '/') }
function WriteJson([string]$Path, [object]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Append-only file exists: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
}
function AssertWithin([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escaped staging root: $full" }
}
function CopyDirectory([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Source directory missing: $Source" }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in [IO.Directory]::EnumerateDirectories($Source, '*', [IO.SearchOption]::AllDirectories)) {
        [IO.Directory]::CreateDirectory((Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $directory)))) | Out-Null
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $target = Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $file))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file, $target, $false)
    }
}
function ReplaceDirectory([string]$Source, [string]$Destination) {
    AssertWithin $Destination $stagingRelease
    if (Test-Path -LiteralPath $Destination) { [IO.Directory]::Delete([IO.Path]::GetFullPath($Destination), $true) }
    CopyDirectory $Source $Destination
}
function RemoveStaging([string]$Path, [string]$Parent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\.'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not $full.EndsWith('.tmp', [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing staging cleanup: $full" }
    [IO.Directory]::Delete($full, $true)
}
function Tree([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse)
    $rows = [string[]]@($files | ForEach-Object { "$([IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\','/'))`t$($_.Length)`t$((Sha $_.FullName).ToLowerInvariant())" })
    [Array]::Sort($rows, [StringComparer]::Ordinal)
    [ordered]@{ files = $files.Count; bytes = [long](($files | Measure-Object Length -Sum).Sum); treeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))) }
}

$modules = [ordered]@{
    'image.ocr.mineru' = [ordered]@{ version = '1.2.2'; source = 'workers/image-ocr/candidates/1.2.2'; destination = 'workers/image-ocr'; executable = 'workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe'; sha256 = '8F99C403AF250E0BD2BB62CEECAA74A768860C10B3C582236BA0A6B3B0D73F78'; treeSha256 = 'F0F8FA5D5BC3210D8CA01CEC8707B29891A8ECFFBE6B533982990BE15FC03B89' }
    'content.deepseek' = [ordered]@{ version = '0.7.0'; source = 'content-worker/releases/content.deepseek-0.7.0-win-x64-formal-20260828T103735Z'; destination = 'workers/content'; executable = 'workers/content/KnowledgeClipper.Content.Worker.exe'; sha256 = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'; treeSha256 = '724F2D2732FA13FA236825F7E82E8E8E8DAF4DC1354E36093AB8B79262EDFBEC' }
    'speech.edge' = [ordered]@{ version = '1.2.4'; source = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260830T073825Z-c0980038/win-x64/workers/speech'; destination = 'workers/speech'; executable = 'workers/speech/KnowledgeClipper.Speech.Worker.exe'; sha256 = 'C098003823B5217C3A4F7267A57E69128AE5F6DF7BCC8083BA92B4CC56C34EE2'; treeSha256 = '985E678EEFD7335F772F21483BF39763544B57260E4449DFC3C017B2E31FB38' }
    'export.remnote' = [ordered]@{ version = '2.1.0'; source = 'releases/knowledge-clipper-remnote-exporter-v2.1.0-20260830T073620Z/win-x64'; destination = 'workers/remnote'; executable = 'workers/remnote/KnowledgeClipper.RemNoteExporter.Worker.exe'; sha256 = '443835ECA91E5F68C97070777C250FD95E4A2E4873C9EFC7FBF45A528EF2CAD2'; treeSha256 = 'A2C5F932B79F2528F2F1B8E30E9B68D3C15AEE07FE7F3AE8E834BF698C675CA8' }
    'export.anki' = [ordered]@{ version = '1.2.1'; source = 'releases/knowledge-clipper-anki-exporter-v1.2.1-20260828T104000Z/win-x64/workers/anki'; destination = 'workers/anki'; executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = '135B7BC31DA93389819E4F802226600CD3879F6527ED4194555D613A32CC4EE1'; treeSha256 = 'E7EBA348C344865B0F403D34187831BABDEADC4818451084ACB86CC37A3E161D' }
}
$evidence = [ordered]@{
    'image-core-admission.json' = 'workers/image-ocr/canary/core-admission-1.2.2-20260830T074207Z/core-admission-receipt.v1.json'
    'content-core-v3-compatibility.json' = 'content-worker/receipts/core-v3-desktop-compatibility/20260830T074659078Z/receipt.v1.json'
    'speech-formal-release.json' = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260830T073825Z-c0980038/speech-formal-win-x64-release-receipt.json'
    'speech-independent-validation.json' = 'tests/RemNoteQuickClipper.Speech.Tests/formal/r20260830T073825Z-c0980038/speech-formal-win-x64-independent-validation-receipt.json'
    'remnote-formal-release.json' = 'releases/knowledge-clipper-remnote-exporter-v2.1.0-20260830T073620Z/win-x64/release-receipt.json'
    'remnote-offline-canary.json' = 'canary/remnote-exporter-v2.1-offline/20260830T153548/receipt.json'
    'anki-shared-consumer.json' = 'workers/anki-exporter/canary/shared-consumer-compatibility/20260830T074135Z-28e90aac/receipt.json'
    'anki-offline-probe.json' = 'workers/anki-exporter/canary/formal-release-settings-probe/20260830T074140Z-2b55687b/receipt.json'
    'core-settings.json' = 'canary/core-v2.5-offline/20260830T075000Z/settings-control.receipt.json'
    'core-adapter.json' = 'canary/core-v2.5-offline/20260830T075000Z/core-adapter.receipt.json'
    'core-contracts.json' = 'canary/core-v2.5-offline/20260830T075000Z/orchestrator-contract-tests.receipt.json'
}

foreach ($required in @($baseRelease, $baseRegistry, $coreExecutable, $coreCheckpointPath)) { if (-not (Test-Path -LiteralPath $required)) { throw "Required input missing: $required" } }
if ((Sha $coreCheckpointPath) -ne '62996EF471F46470090BE40259BF95ACD84EB7640084A1FCD4EC85E680AEE4F3') { throw 'Core checkpoint drifted.' }
$coreDocument = Get-Content -LiteralPath $coreCheckpointPath -Raw | ConvertFrom-Json -Depth 100
if ($coreDocument.status -ne 'passed-offline-authority-admission-ready' -or $coreDocument.tree.totalBytes -le 0 -or $coreDocument.core.version -ne $CoreVersion) { throw 'Core checkpoint is not admissible.' }
foreach ($target in @($releaseContainer, $authorityRoot)) { if (Test-Path -LiteralPath $target) { throw "Append-only target exists: $target" } }

$frozenReceiptPath = Join-Path $ProjectRoot 'receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json'
$frozen = Get-Content -LiteralPath $frozenReceiptPath -Raw | ConvertFrom-Json -Depth 30
$frozenMismatches = 0
foreach ($file in $frozen.files) { if (-not (Test-Path -LiteralPath $file.path) -or (Sha $file.path) -ne ([string]$file.sha256).ToUpperInvariant()) { $frozenMismatches++ } }
if ($frozen.files.Count -ne 21 -or $frozenMismatches -ne 0) { throw 'Frozen contracts/v1 drifted.' }

try {
    CopyDirectory $baseRelease $stagingRelease
    [IO.Directory]::CreateDirectory($stagingAuthority) | Out-Null
    [IO.File]::Copy($coreExecutable, (Join-Path $stagingRelease 'orchestrator/KnowledgeClipper.Orchestrator.exe'), $true)
    foreach ($module in $modules.Values) { ReplaceDirectory (Join-Path $ProjectRoot $module.source) (Join-Path $stagingRelease $module.destination) }
    ReplaceDirectory (Join-Path $ProjectRoot 'contracts/core/v2.4/settings-control') (Join-Path $stagingRelease 'contracts/core/v2.4/settings-control')
    ReplaceDirectory (Join-Path $ProjectRoot 'contracts/core/v2.5') (Join-Path $stagingRelease 'contracts/core/v2.5')
    foreach ($relative in @('contracts/remnote-readonly-probe-request.v1.schema.json','contracts/remnote-readonly-probe-result.v1.schema.json','contracts/remnote-destination-settings.v1.schema.json','contracts/remnote-destination-settings-snapshot.v1.schema.json','contracts/remnote-export-payload.v2.schema.json','contracts/remnote-export-task.v2.schema.json','contracts/remnote-write-receipt.v2.schema.json')) {
        $target = Join-Path $stagingRelease $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy((Join-Path $ProjectRoot $relative), $target, $true)
    }
    foreach ($pair in $evidence.GetEnumerator()) {
        $source = Join-Path $ProjectRoot $pair.Value
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Evidence missing: $($pair.Value)" }
        $target = Join-Path $stagingRelease "evidence/modules/$($pair.Key)"
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($source, $target, $false)
    }

    $registry = Get-Content -LiteralPath $baseRegistry -Raw | ConvertFrom-Json -Depth 100
    $registry.registryVersion = $RegistryVersion
    $workers = [ordered]@{}
    foreach ($entry in $registry.workers) {
        $module = $modules[$entry.workerId]
        $executable = Join-Path $stagingRelease $module.executable
        if ((Sha $executable) -ne $module.sha256) { throw "Staged worker hash drifted: $($entry.workerId)" }
        $describe = ((& $executable describe --json 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
        if ($LASTEXITCODE -ne 0 -or $describe.workerId -ne $entry.workerId -or $describe.workerVersion -ne $module.version -or $describe.contractVersion -ne '1.0.0') { throw "Worker describe failed: $($entry.workerId)" }
        $entry.stages = @($describe.stages); $entry.inputKinds = @($describe.inputKinds); $entry.outputKinds = @($describe.outputKinds); $entry.sideEffectLevel = $describe.sideEffectLevel; $entry.idempotencyLevel = $describe.idempotencyLevel
        $describePath = Join-Path $stagingAuthority "describe/$($entry.workerId).describe.json"
        WriteJson $describePath $describe
        $workers[$entry.workerId] = [ordered]@{ workerVersion = $describe.workerVersion; path = (Join-Path $authorityRoot "describe/$($entry.workerId).describe.json"); bytes = (Get-Item $describePath).Length; sha256 = Sha $describePath; executablePath = $module.executable; executableBytes = (Get-Item $executable).Length; executableSha256 = Sha $executable }
    }
    $registryPath = Join-Path $stagingAuthority 'worker-registry.v1.json'
    WriteJson $registryPath $registry
    $validation = ((& (Join-Path $stagingRelease 'orchestrator/KnowledgeClipper.Orchestrator.exe') validate-registry --registry $registryPath --release-root $stagingRelease 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
    if ($LASTEXITCODE -ne 0 -or $validation.status -ne 'valid' -or $validation.workers -ne 5 -or $validation.registryVersion -ne $RegistryVersion) { throw 'Runtime validate-registry failed.' }

    $releaseTree = Tree $stagingRelease
    $dependencyPath = Join-Path $stagingAuthority 'dependency-manifest.v1.json'
    WriteJson $dependencyPath ([ordered]@{
        schemaVersion = 'knowledge-clipper-release-dependency-manifest.v2'; releaseId = $releaseId; releaseRoot = "releases/$releaseId/win-x64"
        files = $releaseTree.files; bytes = $releaseTree.bytes; treeSha256 = $releaseTree.treeSha256
        treeHashAlgorithm = 'sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF; SHA-256 UTF-8'
        core = [ordered]@{ version = $CoreVersion; checkpoint = $CoreCheckpoint; checkpointSha256 = Sha $coreCheckpointPath; executableSha256 = Sha $coreExecutable }
        modules = $modules; moduleEvidence = $evidence; baseAuthority = '1.2.11'; supersedesCheckpointSha256 = 'A01D0E65DEABA5078D7A57EF57FFFFF547DB3F5BBD03074247135DF463CA7F1C'
    })
    $assemblyPath = Join-Path $stagingAuthority 'assembly-receipt.v1.json'
    WriteJson $assemblyPath ([ordered]@{
        schemaVersion = 'knowledge-clipper-authority-assembly-receipt.v1'; status = 'candidate-built-offline'; registryVersion = $RegistryVersion; releaseId = $releaseId
        releaseRoot = $releaseRoot; authorityDirectory = $authorityRoot; release = $releaseTree; registrySha256 = Sha $registryPath; dependencyManifestSha256 = Sha $dependencyPath
        core = [ordered]@{ version = $CoreVersion; checkpointVersion = $coreDocument.checkpointVersion; checkpointSha256 = Sha $coreCheckpointPath; executableSha256 = Sha $coreExecutable }
        workers = $workers; registryValidation = $validation; externalWrites = 0; mineruCalls = 0; physicalSpeakerInvocations = 0
    })

    [IO.Directory]::Move($stagingContainer, $releaseContainer)
    [IO.Directory]::Move($stagingAuthority, $authorityRoot)
    $finalRegistry = Join-Path $authorityRoot 'worker-registry.v1.json'
    $finalDependency = Join-Path $authorityRoot 'dependency-manifest.v1.json'
    $finalAssembly = Join-Path $authorityRoot 'assembly-receipt.v1.json'
    $assemblyText = Get-Content -LiteralPath $finalAssembly -Raw
    if ($assemblyText.Contains('.tmp', [StringComparison]::OrdinalIgnoreCase)) { throw 'Final assembly receipt contains a staging path.' }
    $pathGate = ((& (Join-Path $PSScriptRoot 'Test-KnowledgeClipperAuthorityAssemblyPaths.ps1') -AuthorityDirectory $authorityRoot -ReleaseRoot $releaseRoot 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
    if ($LASTEXITCODE -ne 0 -or $pathGate.status -ne 'passed' -or $pathGate.paths -ne 5) { throw 'Final assembly path gate failed.' }
    $finalValidation = ((& (Join-Path $releaseRoot 'orchestrator/KnowledgeClipper.Orchestrator.exe') validate-registry --registry $finalRegistry --release-root $releaseRoot 2>&1) -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
    if ($LASTEXITCODE -ne 0 -or $finalValidation.status -ne 'valid' -or $finalValidation.workers -ne 5) { throw 'Final validate-registry failed.' }

    $checkpointPath = Join-Path $authorityRoot 'checkpoint.json'
    WriteJson $checkpointPath ([ordered]@{
        schemaVersion = 'knowledge-clipper-authority-checkpoint.v2'; checkpointId = "knowledge-clipper-authority-$RegistryVersion-$ReleaseStamp"; generatedAt = [DateTimeOffset]::UtcNow.ToString('o'); admissionStatus = 'authority-admitted'
        frozenV1 = [ordered]@{ receipt = $frozenReceiptPath; files = 21; mismatches = 0; sha256 = Sha $frozenReceiptPath }
        registry = [ordered]@{ path = $finalRegistry; registryVersion = $RegistryVersion; workers = 5; sha256 = Sha $finalRegistry }
        release = [ordered]@{ root = $releaseRoot; files = $releaseTree.files; bytes = $releaseTree.bytes; treeSha256 = $releaseTree.treeSha256; dependencyManifest = $finalDependency; dependencyManifestSha256 = Sha $finalDependency }
        validation = $finalValidation; core = [ordered]@{ version = $CoreVersion; checkpoint = $CoreCheckpoint; checkpointSha256 = Sha $coreCheckpointPath }
        workers = $workers; assemblyPathGate = [ordered]@{ path = $pathGate.gate; sha256 = $pathGate.sha256; paths = $pathGate.paths }
        capabilities = [ordered]@{ originalImage = 'terminal-unsupported-provider-zero'; imagePartial = 'failed-items-only'; contentSettings = '1.1.0'; speechSettings = 'language-override-bound'; ankiProbe = 'explicit-read-only'; remnoteProbe = 'conditional-cli-only' }
        externalWrites = 0; mineruCalls = 0; ankiWrites = 0; remnoteWrites = 0; physicalSpeakerInvocations = 0
    })
    $receiptPath = Join-Path $ProjectRoot "receipts/knowledge-clipper-authority-$RegistryVersion-$ReleaseStamp.json"
    WriteJson $receiptPath ([ordered]@{
        schemaVersion = 'knowledge-clipper-authority-release-receipt.v2'; status = 'passed-authority-admitted-offline'; registryVersion = $RegistryVersion; releaseId = $releaseId
        releaseRoot = $releaseRoot; authorityRoot = $authorityRoot; registry = [ordered]@{ path = $finalRegistry; sha256 = Sha $finalRegistry }; checkpoint = [ordered]@{ path = $checkpointPath; sha256 = Sha $checkpointPath }
        dependencyManifest = [ordered]@{ path = $finalDependency; sha256 = Sha $finalDependency }; assemblyReceipt = [ordered]@{ path = $finalAssembly; sha256 = Sha $finalAssembly }; assemblyPathGate = [ordered]@{ path = $pathGate.gate; sha256 = $pathGate.sha256 }
        release = $releaseTree; coreCheckpoint = [ordered]@{ path = $CoreCheckpoint; sha256 = Sha $coreCheckpointPath }; validation = $finalValidation
        externalWrites = 0; mineruCalls = 0; remnoteWrites = 0; ankiWrites = 0; physicalSpeakerInvocations = 0
    })
    [ordered]@{ releaseRoot = $releaseRoot; authorityRoot = $authorityRoot; registrySha256 = Sha $finalRegistry; checkpointSha256 = Sha $checkpointPath; receipt = Relative $receiptPath; receiptSha256 = Sha $receiptPath; release = $releaseTree } | ConvertTo-Json -Depth 20
}
finally {
    RemoveStaging $stagingContainer (Join-Path $ProjectRoot 'releases')
    RemoveStaging $stagingAuthority (Join-Path $ProjectRoot 'contracts/authority/worker-registry')
}

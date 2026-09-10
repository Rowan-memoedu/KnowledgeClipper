param(
    [string]$RepositoryRoot = 'D:\Projects\RemNote Quick Clipper',
    [string]$CoreCandidateRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-observation-20260908T020606Z-f9d943ec',
    [string]$SdkFreezeRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki-sdk\actual-desktop-flow-observation-sdk-20260908T020355Z',
    [string]$RunnerReleaseRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki-runner\actual-desktop-flow-runner-v5-20260908T022528Z',
    [string]$ArtifactsRoot = 'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki',
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe',
    [string]$DesktopPayloadReceipt = '',
    [string]$ExpectedDesktopPayloadReceiptSha256 = '',
    [ValidateSet('ReadToAnki', 'Question', 'Playback')][string]$RegressionSlice = 'ReadToAnki',
    [string]$ExpectedRunnerReleaseManifestSha256 = '',
    [string]$PlaybackBundleRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Sha([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Utf8Json([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Inventory([string]$Root, [string[]]$ExcludedRelativePaths) {
    $excluded = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $ExcludedRelativePaths) { [void]$excluded.Add($item.Replace('\', '/')) }
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if (-not $excluded.Contains($relative)) { $paths.Add($relative) }
    }
    $ordered = $paths.ToArray()
    [Array]::Sort($ordered, [System.StringComparer]::Ordinal)
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in $ordered) {
        $file = Get-Item -LiteralPath (Join-Path $Root $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $rows.Add([ordered]@{ path = $relative; bytes = $file.Length; sha256 = Sha $file.FullName })
    }
    return $rows
}

function TreeHash([object[]]$Rows) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($row in $Rows) {
        [void]$builder.Append($row.path).Append([char]0).Append($row.bytes).Append([char]0).Append($row.sha256).Append("`n")
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($builder.ToString())
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
}

function StartupReceiptTreeHash([object[]]$Rows) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($row in $Rows) {
        [void]$builder.Append($row.path).Append("`n").Append($row.sha256.ToLowerInvariant()).Append("`n").Append($row.bytes).Append("`n")
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($builder.ToString())
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Merge-Directory([string]$Source, [string]$Destination) {
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source) {
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) { Merge-Directory $item.FullName $target }
        elseif (Test-Path -LiteralPath $target) {
            if ((Sha $item.FullName) -ne (Sha $target)) { throw "candidate-file-collision: $target" }
        }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target }
    }
}

$repo = [System.IO.Path]::GetFullPath($RepositoryRoot)
$core = [System.IO.Path]::GetFullPath($CoreCandidateRoot)
$freeze = [System.IO.Path]::GetFullPath($SdkFreezeRoot)
$runnerRelease = [System.IO.Path]::GetFullPath($RunnerReleaseRoot)
$artifacts = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$coreManifest = Join-Path $core 'candidate-manifest.json'
$freezeManifest = Join-Path $freeze 'freeze-manifest.json'
$sdkManifestTargetName = 'core-read-to-anki-sdk-freeze-manifest.json'
$runtimeSource = Join-Path $core 'core-v3'
if ($RegressionSlice -eq 'Playback') {
    if ([string]::IsNullOrWhiteSpace($PlaybackBundleRoot)) { throw 'playback-bundle-required' }
    $PlaybackBundleRoot = [IO.Path]::GetFullPath($PlaybackBundleRoot)
    $freeze = Join-Path $PlaybackBundleRoot 'sdk'
    $freezeManifest = Join-Path $PlaybackBundleRoot 'bundle-manifest.json'
    $runtimeSource = Join-Path $PlaybackBundleRoot 'runtime'
    $sdkManifestTargetName = 'playback-control-bundle-manifest.json'
    if ((Sha $freezeManifest) -ne 'FBBDFD7C25B7116E99B1B668C46511B900B04183E217E490D3E351F4942921B7') { throw 'playback-bundle-manifest-drift' }
    $bundle = Get-Content -LiteralPath $freezeManifest -Raw | ConvertFrom-Json -Depth 100
    if ((Sha $coreManifest) -ne $bundle.baseCandidate.manifestSha256) { throw 'playback-inherited-core-manifest-drift' }
    foreach ($row in $bundle.files) {
        if ([IO.Path]::IsPathRooted($row.path) -or $row.path.Split('/') -contains '..') { throw 'playback-bundle-path-invalid' }
        $p = Join-Path $PlaybackBundleRoot $row.path
        if ((Sha $p) -ne $row.sha256 -or (Get-Item -LiteralPath $p).Length -ne $row.bytes) { throw 'playback-bundle-file-drift' }
    }
}
$runnerManifestName = if ($RegressionSlice -eq 'Playback') { 'runner-playback-release-manifest.v1.json' } elseif ($RegressionSlice -eq 'Question') { 'runner-question-release-manifest.v1.json' } else { 'runner-release-manifest.v5.json' }
$runnerReleaseManifestPath = Join-Path $runnerRelease $runnerManifestName
$bindingRelative = 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json'
$binding = Join-Path $core $bindingRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
$runtimeDescriptorRelative = 'authority/runtime-tools.v1.json'
$runtimeDescriptor = Join-Path $core $runtimeDescriptorRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
foreach ($required in @($DotnetPath, $coreManifest, $freezeManifest, $runnerReleaseManifestPath, $binding, $runtimeDescriptor, (Join-Path $core 'core-v3\KnowledgeClipper.Core.V3.Host.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "required-input-missing: $required" }
}
$coreCandidateManifest = Get-Content -Raw -LiteralPath $coreManifest | ConvertFrom-Json -Depth 100
if ([string]::IsNullOrWhiteSpace([string]$coreCandidateManifest.candidateId)) { throw 'core-candidate-id-missing' }
$runnerReleaseManifest = Get-Content -Raw -LiteralPath $runnerReleaseManifestPath | ConvertFrom-Json -Depth 100
if ($RegressionSlice -in @('Question','Playback')) {
    if ([string]::IsNullOrWhiteSpace($DesktopPayloadReceipt)) { throw 'question-candidate-requires-reviewed-prebuilt-desktop' }
    if ([string]::IsNullOrWhiteSpace($ExpectedRunnerReleaseManifestSha256) -or (Sha $runnerReleaseManifestPath) -ne $ExpectedRunnerReleaseManifestSha256.ToUpperInvariant()) { throw 'question-runner-manifest-drift' }
    $expectedRunnerSchema = if ($RegressionSlice -eq 'Playback') { 'knowledge-clipper-playback-runner-release.v1' } else { 'knowledge-clipper-question-runner-release.v1' }
    if ($runnerReleaseManifest.schemaVersion -ne $expectedRunnerSchema -or $runnerReleaseManifest.runtime -ne 'win-x64-self-contained' -or $runnerReleaseManifest.status -ne 'offline-prepared') { throw 'runner-runtime-model-invalid' }
    $runnerRequiredFiles = @('app/KnowledgeClipper.ActualDesktopFlowRunner.exe', 'app/KnowledgeClipper.ActualDesktopFlowRunner.dll', 'app/KnowledgeClipper.ActualDesktopFlowRunner.runtimeconfig.json', 'app/KnowledgeClipper.ActualDesktopFlowRunner.deps.json', 'app/coreclr.dll', 'app/hostpolicy.dll', 'app/System.Private.CoreLib.dll', 'app/System.Windows.Forms.dll', 'app/KnowledgeClipper.Core.V3.Client.dll', 'app/KnowledgeClipper.Core.V3.Contracts.dll')
    foreach ($source in $runnerReleaseManifest.sources) { if ((Sha $source.path) -ne $source.sha256) { throw 'question-runner-source-drift' } }
    if ((Sha (Join-Path $runnerRelease 'app/KnowledgeClipper.ActualDesktopFlowRunner.dll')) -ne $runnerReleaseManifest.runnerDllSha256) { throw 'question-runner-code-drift' }
}
else {
    if ($runnerReleaseManifest.schemaVersion -ne 'knowledge-clipper-actual-runner-release.v5' -or $runnerReleaseManifest.runtime.model -ne 'self-contained' -or $runnerReleaseManifest.runtime.rid -ne 'win-x64' -or $runnerReleaseManifest.runtime.singleFile -ne $false) { throw 'runner-release-runtime-model-invalid' }
    $runnerRequiredFiles = @($runnerReleaseManifest.requiredRuntimeFiles | ForEach-Object { [string]$_ })
}
if ([string]::IsNullOrWhiteSpace([string]$runnerReleaseManifest.entrypoint)) { throw 'runner-release-entrypoint-missing' }

$timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$candidateId = if ($RegressionSlice -eq 'Playback') { "desktop-playback-$timestamp" } elseif ($RegressionSlice -eq 'Question') { "desktop-question-$timestamp" } else { "desktop-rta-$timestamp" }
[System.IO.Directory]::CreateDirectory($artifacts) | Out-Null
$staging = Join-Path $artifacts ('.staging-' + $candidateId + '-' + [Guid]::NewGuid().ToString('N'))
$candidate = Join-Path $artifacts $candidateId
if (Test-Path -LiteralPath $candidate) { throw "candidate-already-exists: $candidate" }
[System.IO.Directory]::CreateDirectory($staging) | Out-Null

try {
    if (-not [string]::IsNullOrWhiteSpace($DesktopPayloadReceipt)) {
        if ([string]::IsNullOrWhiteSpace($ExpectedDesktopPayloadReceiptSha256) -or (Sha $DesktopPayloadReceipt) -ne $ExpectedDesktopPayloadReceiptSha256.ToUpperInvariant()) { throw 'prebuilt-desktop-receipt-drift' }
        $prebuilt = Get-Content -Raw -LiteralPath $DesktopPayloadReceipt | ConvertFrom-Json -Depth 100
        if ($prebuilt.status -ne 'PASS-PUBLISH-AND-EQUALITY-NOT-INTEGRATED') { throw 'prebuilt-desktop-not-qualified' }
        $prebuiltRoot = [System.IO.Path]::GetFullPath([string]$prebuilt.payloadRoot)
        $prebuiltRows = @(Inventory $prebuiltRoot @())
        if ($prebuiltRows.Count -ne $prebuilt.fileCount -or (TreeHash $prebuiltRows) -ne $prebuilt.treeSha256) { throw 'prebuilt-desktop-tree-drift' }
        [long]$prebuiltBytes = 0
        foreach ($row in $prebuiltRows) { $prebuiltBytes += [long]$row.bytes }
        if ($prebuiltBytes -ne $prebuilt.totalBytes) { throw 'prebuilt-desktop-bytes-drift' }
        Merge-Directory $prebuiltRoot $staging
        if ((TreeHash @(Inventory $staging @())) -ne $prebuilt.treeSha256) { throw 'prebuilt-desktop-copy-drift' }
        Write-Output 'PREBUILT_DESKTOP verified and reused; no compilation'
    }
    else {
        & $DotnetPath publish (Join-Path $repo 'desktop\RemNoteQuickClipper.Desktop.csproj') -c Release -r win-x64 --self-contained true --no-restore --nologo -o $staging "/p:CoreV3ReadToAnkiSdkRoot=$freeze" "/p:CoreV3ReadToAnkiContractsRoot=$freeze"
        if ($LASTEXITCODE -ne 0) { throw "desktop-publish-failed: $LASTEXITCODE" }
    }

    $desktopClient = Join-Path $staging 'KnowledgeClipper.Core.V3.Client.dll'
    $desktopContracts = Join-Path $staging 'KnowledgeClipper.Core.V3.Contracts.dll'
    if ((Sha $desktopClient) -ne (Sha (Join-Path $freeze 'KnowledgeClipper.Core.V3.Client.dll'))) { throw 'desktop-core-v3-client-drift' }
    if ((Sha $desktopContracts) -ne (Sha (Join-Path $core 'core-v3\KnowledgeClipper.Core.V3.Contracts.dll'))) { throw 'desktop-core-v3-contracts-drift' }

    foreach ($name in @('authority', 'contracts', 'core-v3')) {
        $sourceDirectory = if ($name -eq 'core-v3') { $runtimeSource } else { Join-Path $core $name }
        $targetDirectory = Join-Path $staging $name
        Merge-Directory $sourceDirectory $targetDirectory
    }
    Copy-Item -LiteralPath $coreManifest -Destination (Join-Path $staging 'core-read-to-anki-candidate-manifest.json')
    Copy-Item -LiteralPath $freezeManifest -Destination (Join-Path $staging $sdkManifestTargetName)
    if ($RegressionSlice -eq 'Playback') {
        Merge-Directory (Join-Path $PlaybackBundleRoot 'sdk') (Join-Path $staging 'playback-control/sdk')
        Merge-Directory (Join-Path $PlaybackBundleRoot 'evidence') (Join-Path $staging 'playback-control/evidence')
        Copy-Item -LiteralPath (Join-Path $PlaybackBundleRoot 'README.md') -Destination (Join-Path $staging 'playback-control/README.md')
    }

    $v16Receipt = Join-Path $staging 'contracts\desktop-automation\v1.6\contract-receipt.json'
    if ((Sha $v16Receipt) -ne '6D729FB84E4C9DC4344FDF01AB9C387CF0C7998A2470280DF778429A61F2D923') { throw 'desktop-automation-v16-receipt-drift' }
    $runnerTargetRoot = Join-Path $staging 'actual-runner'
    [System.IO.Directory]::CreateDirectory($runnerTargetRoot) | Out-Null
    $runnerSourceBoundary = $runnerRelease.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $runnerTargetBoundary = [System.IO.Path]::GetFullPath($runnerTargetRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $runnerSeen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    [long]$runnerBytes = 0
    foreach ($runnerFile in $runnerReleaseManifest.files) {
        $runnerRelative = ([string]$runnerFile.path).Replace('\', '/')
        if ([System.IO.Path]::IsPathRooted($runnerRelative) -or $runnerRelative.Split('/') -contains '..' -or -not $runnerSeen.Add($runnerRelative)) { throw 'runner-release-path-invalid-or-duplicate' }
        $relative = $runnerRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $runnerRelease $relative))
        $targetPath = [System.IO.Path]::GetFullPath((Join-Path $runnerTargetRoot $relative))
        if (-not $sourcePath.StartsWith($runnerSourceBoundary, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'runner-manifest-source-outside-boundary' }
        if (-not $targetPath.StartsWith($runnerTargetBoundary, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'runner-manifest-target-outside-boundary' }
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'runner-release-source-missing' }
        if ((Get-Item -LiteralPath $sourcePath).Length -ne [long]$runnerFile.bytes -or (Sha $sourcePath) -ne ([string]$runnerFile.sha256).ToUpperInvariant()) { throw 'runner-release-source-drift' }
        $runnerBytes += [long]$runnerFile.bytes
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $targetPath)) | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath
    }
    if ($runnerSeen.Count -ne [int]$runnerReleaseManifest.payload.files -or $runnerBytes -ne [long]$runnerReleaseManifest.payload.bytes) { throw 'runner-release-payload-summary-drift' }
    foreach ($requiredRuntime in @([string]$runnerReleaseManifest.entrypoint) + $runnerRequiredFiles) { if (-not $runnerSeen.Contains($requiredRuntime)) { throw 'runner-release-runtime-closure-missing' } }
    Copy-Item -LiteralPath $runnerReleaseManifestPath -Destination (Join-Path $runnerTargetRoot $runnerManifestName)
    if ($RegressionSlice -eq 'Playback') {
        foreach ($name in @('KnowledgeClipper.Core.V3.Client.dll','KnowledgeClipper.Core.V3.Contracts.dll')) {
            if ((Sha (Join-Path $runnerTargetRoot ('app/'+$name))) -ne (Sha (Join-Path $freeze $name))) { throw 'playback-runner-sdk-drift' }
        }
    }
    foreach ($runnerFile in $runnerReleaseManifest.files) {
        $targetPath = [System.IO.Path]::GetFullPath((Join-Path $runnerTargetRoot (([string]$runnerFile.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
        if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { throw 'runner-release-target-missing' }
        if ((Get-Item -LiteralPath $targetPath).Length -ne [long]$runnerFile.bytes -or (Sha $targetPath) -ne ([string]$runnerFile.sha256).ToUpperInvariant()) { throw 'runner-release-target-drift' }
    }

    $authorityRows = Inventory (Join-Path $staging 'authority\win-x64') @()
    $authorityTree = TreeHash $authorityRows
    $pipeName = 'KnowledgeClipper.CoreV3.' + $candidateId.Replace('-', '.')
    $integration = [ordered]@{
        SchemaVersion = 'knowledge-clipper-candidate-integration.v3'
        RuntimeKind = 'core-v3'
        AutoStart = $true
        OrchestratorExecutable = 'core-v3/KnowledgeClipper.Core.V3.Host.exe'
        RepositoryRoot = '.'
        Binding = $bindingRelative
        IpcConfig = $bindingRelative
        Registry = 'authority/worker-registry.v1.json'
        ReleaseRoot = 'authority/win-x64'
        JobsRoot = '%LOCALAPPDATA%\KnowledgeClipper\core-v3'
        PipeName = $pipeName
        MaxActiveTasks = 4
        AuthorityCheckpointId = [string]$coreCandidateManifest.candidateId
        AuthorityTreeSha256 = $authorityTree
        RuntimeToolsDescriptor = $runtimeDescriptorRelative
        RuntimeToolsDescriptorSha256 = Sha (Join-Path $staging $runtimeDescriptorRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    }
    Write-Utf8Json (Join-Path $staging 'candidate-integration.json') $integration

    $rows = Inventory $staging @('candidate-file-manifest.v1.json')
    $tree = TreeHash $rows
    [long]$payloadBytes = 0
    foreach ($row in $rows) { $payloadBytes += [long]$row.bytes }
    $manifest = [ordered]@{
        schemaVersion = 'knowledge-clipper-desktop-read-to-anki-candidate-manifest.v1'
        candidateId = $candidateId
        createdAtUtc = [DateTime]::UtcNow.ToString('O')
        product = '零碎知识剪藏'
        entrypoint = 'KnowledgeClipper.exe'
        architecture = 'win-x64'
        desktopSelfContained = $true
        regressionSlice = $RegressionSlice
        payload = [ordered]@{ files = $rows.Count; bytes = $payloadBytes; treeSha256 = $tree }
        bindings = [ordered]@{
            prebuiltDesktopReceipt = if ([string]::IsNullOrWhiteSpace($DesktopPayloadReceipt)) { $null } else { [ordered]@{ path = [System.IO.Path]::GetFullPath($DesktopPayloadReceipt); sha256 = Sha $DesktopPayloadReceipt } }
            coreCandidateManifest = [ordered]@{ path = 'core-read-to-anki-candidate-manifest.json'; sha256 = Sha (Join-Path $staging 'core-read-to-anki-candidate-manifest.json'); role = if ($RegressionSlice -eq 'Playback') { 'inherited-authority-base-runtime-superseded-by-playback-bundle' } else { 'core-candidate' } }
            sdkFreezeManifest = [ordered]@{ path = $sdkManifestTargetName; sha256 = Sha (Join-Path $staging $sdkManifestTargetName) }
            runtimeOverlay = if ($RegressionSlice -eq 'Playback') { [ordered]@{ bundleManifest = $sdkManifestTargetName; sourcePrefix = 'runtime/'; candidatePrefix = 'core-v3/'; mode = 'exact-directory-replacement'; kernelSha256 = Sha (Join-Path $staging 'core-v3/KnowledgeClipper.Core.V3.Kernel.dll') } } else { $null }
            capabilityBinding = [ordered]@{ path = $bindingRelative; sha256 = Sha (Join-Path $staging $bindingRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)) }
            authorityTreeSha256 = $authorityTree
            runtimeToolsDescriptor = [ordered]@{ path = $runtimeDescriptorRelative; sha256 = Sha (Join-Path $staging $runtimeDescriptorRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)) }
            runnerReleaseManifest = [ordered]@{ path = 'actual-runner/' + $runnerManifestName; sha256 = Sha (Join-Path $runnerTargetRoot $runnerManifestName); sourceRoot = $runnerRelease; entrypoint = 'actual-runner/' + ([string]$runnerReleaseManifest.entrypoint) }
        }
        safety = [ordered]@{ remnote = 'not-in-slice'; ocr = 'not-in-slice'; question = if ($RegressionSlice -eq 'Question') { 'offline-qualified-actual-not-authorized' } else { 'not-in-slice' }; languageChoice = 'conditional-not-dispatched'; retention = 'disabled' }
        files = $rows
    }
    Write-Utf8Json (Join-Path $staging 'candidate-file-manifest.v1.json') $manifest

    $receiptRows = Inventory $staging @('candidate-file-manifest.v1.json')
    [long]$receiptBytes = 0
    foreach ($row in $receiptRows) { $receiptBytes += [long]$row.bytes; $row.sha256 = $row.sha256.ToLowerInvariant() }
    $desktopReceipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-desktop-build-receipt.v1'
        status = 'PASS'
        candidateId = $candidateId
        createdAtUtc = [DateTime]::UtcNow.ToString('O')
        fileCount = $receiptRows.Count
        totalBytes = $receiptBytes
        treeSha256 = StartupReceiptTreeHash $receiptRows
        files = $receiptRows
    }
    $candidateBoundary = $artifacts.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not [System.IO.Path]::GetFullPath($staging).StartsWith($candidateBoundary, [StringComparison]::OrdinalIgnoreCase) -or -not [System.IO.Path]::GetFullPath($candidate).StartsWith($candidateBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'candidate-move-outside-artifacts' }
    Move-Item -LiteralPath $staging -Destination $candidate
    $sealedRunnerRoot = Join-Path $candidate 'actual-runner'
    $sealedRunnerManifest = Get-Content -Raw -LiteralPath (Join-Path $sealedRunnerRoot $runnerManifestName) | ConvertFrom-Json -Depth 100
    $sealedSeen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    [long]$sealedRunnerBytes = 0
    foreach ($runnerFile in $sealedRunnerManifest.files) {
        $relative = ([string]$runnerFile.path).Replace('\', '/')
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..' -or -not $sealedSeen.Add($relative)) { throw 'sealed-runner-path-invalid-or-duplicate' }
        $path = [System.IO.Path]::GetFullPath((Join-Path $sealedRunnerRoot $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
        if (-not $path.StartsWith(([System.IO.Path]::GetFullPath($sealedRunnerRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) { throw 'sealed-runner-path-outside-boundary' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item $path).Length -ne [long]$runnerFile.bytes -or (Sha $path) -ne ([string]$runnerFile.sha256).ToUpperInvariant()) { throw 'sealed-runner-closure-drift' }
        $sealedRunnerBytes += [long]$runnerFile.bytes
    }
    if ($sealedSeen.Count -ne [int]$sealedRunnerManifest.payload.files -or $sealedRunnerBytes -ne [long]$sealedRunnerManifest.payload.bytes) { throw 'sealed-runner-payload-summary-drift' }
    $receiptDirectory = Join-Path $artifacts 'receipts'
    [System.IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
    Write-Utf8Json (Join-Path $receiptDirectory ($candidateId + '.desktop-build-receipt.v1.json')) $desktopReceipt
    Write-Output ($candidate | ConvertTo-Json -Compress)
}
catch {
    $stagingFull = [System.IO.Path]::GetFullPath($staging)
    $artifactsPrefix = [System.IO.Path]::GetFullPath($artifacts).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($stagingFull.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and ((Split-Path -Leaf $stagingFull).StartsWith('.staging-desktop-rta-', [System.StringComparison]::Ordinal) -or (Split-Path -Leaf $stagingFull).StartsWith('.staging-desktop-question-', [System.StringComparison]::Ordinal))) {
        if (Test-Path -LiteralPath $stagingFull) { Write-Warning "Failed staging preserved for diagnosis: $stagingFull" }
    }
    throw
}

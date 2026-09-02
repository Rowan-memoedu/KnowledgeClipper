param(
    [Parameter(Mandatory=$true)][string]$CandidateId,
    [Parameter(Mandatory=$true)][string]$PreviousCandidateId,
    [Parameter(Mandatory=$true)][string]$AuthorityVersion,
    [Parameter(Mandatory=$true)][string]$CheckpointId,
    [Parameter(Mandatory=$true)][string]$AuthorityReceiptName,
    [Parameter(Mandatory=$true)][string]$ExpectedAuthorityTree,
    [Parameter(Mandatory=$true)][string]$ExpectedCheckpointSha,
    [Parameter(Mandatory=$true)][string]$ExpectedRegistrySha,
    [string]$PriorWindowsUiReceipt='canary\desktop-windows\integrated-v122-20260828T015302Z\receipt.json',
    [string]$SettingsCheckpointRelativePath='',
    [string]$ExpectedSettingsCheckpointSha='',
    [string[]]$InvalidationReceiptRelativePaths=@(),
    [string[]]$BackendCandidateBoundaries=@(),
    [string[]]$AdditionalNoGoBoundaries=@()
)

$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$candidate=Join-Path $project (Join-Path 'releases' (Join-Path $CandidateId 'win-x64'))
$previous=Join-Path $project (Join-Path 'releases' (Join-Path $PreviousCandidateId 'win-x64'))
$canary=Join-Path $project (Join-Path 'canary\desktop-integrated' $CandidateId)
$output=Join-Path $project (Join-Path 'receipts' ("desktop-integrated-candidate-$CandidateId.json"))
if (Test-Path -LiteralPath $output) { throw "Immutable receipt already exists: $output" }

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Get-Inventory([string]$Root, [string[]]$Exclude=@()) {
    $records=@(); $rows=@(); [long]$bytes=0; $byRelative=@{}
    [string[]]$relativeNames=@(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object {
        $relative=[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/')
        if ($Exclude -notcontains $relative) { $byRelative[$relative]=$_.FullName; $relative }
    })
    [Array]::Sort[string]($relativeNames,[StringComparer]::Ordinal)
    foreach($relative in $relativeNames) {
        $file=Get-Item -LiteralPath $byRelative[$relative]
        $hash=Get-Hash $file.FullName
        $records += [ordered]@{path=$relative;bytes=$file.Length;sha256=$hash}
        $rows += "$relative`t$($file.Length)`t$($hash.ToLowerInvariant())"
        $bytes += $file.Length
    }
    $material=[Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $tree=[Convert]::ToHexString($sha.ComputeHash($material)) } finally { $sha.Dispose() }
    [ordered]@{
        files=$records;fileCount=$records.Count;totalBytes=$bytes;treeSha256=$tree
        treeAlgorithm='sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)'
    }
}

foreach($root in @($candidate,$previous,$canary)) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Required root missing: $root" }
}

$manifestPath=Join-Path $candidate 'candidate-file-manifest.v1.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 'knowledge-clipper-integrated-candidate-manifest.v1' -or $manifest.candidateId -ne $CandidateId) { throw 'Candidate manifest identity mismatch.' }
foreach($entry in $manifest.payload.files) {
    $path=Join-Path $candidate ($entry.path.Replace('/','\'))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Candidate payload missing: $($entry.path)" }
    if ((Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-Hash $path) -ne $entry.sha256) { throw "Candidate payload mismatch: $($entry.path)" }
}
$payloadInventory=Get-Inventory $candidate @('candidate-file-manifest.v1.json')
if ($payloadInventory.fileCount -ne $manifest.payload.fileCount -or $payloadInventory.totalBytes -ne $manifest.payload.totalBytes -or $payloadInventory.treeSha256 -ne $manifest.payload.treeSha256) { throw 'Manifest payload aggregate mismatch.' }
$inventory=Get-Inventory $candidate

$desktopReceiptPath=Join-Path $candidate 'desktop-build-receipt.v1.json'
$previousDesktopReceiptPath=Join-Path $previous 'desktop-build-receipt.v1.json'
if ((Get-Hash $desktopReceiptPath) -ne (Get-Hash $previousDesktopReceiptPath)) { throw 'Desktop build receipt changed between candidates.' }
$desktopReceipt=Get-Content -LiteralPath $desktopReceiptPath -Raw | ConvertFrom-Json
$desktopEquality=@()
foreach($entry in @($desktopReceipt.files | Sort-Object path)) {
    $currentPath=Join-Path $candidate $entry.path
    $previousPath=Join-Path $previous $entry.path
    if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf) -or -not (Test-Path -LiteralPath $previousPath -PathType Leaf)) { throw "Desktop payload file missing: $($entry.path)" }
    $currentHash=Get-Hash $currentPath
    $previousHash=Get-Hash $previousPath
    $currentBytes=(Get-Item -LiteralPath $currentPath).Length
    $previousBytes=(Get-Item -LiteralPath $previousPath).Length
    $receiptHash=([string]$entry.sha256).ToUpperInvariant()
    if ($currentBytes -ne $entry.bytes -or $previousBytes -ne $entry.bytes -or $currentHash -ne $receiptHash -or $previousHash -ne $receiptHash) { throw "Desktop payload differs from previous candidate or receipt: $($entry.path)" }
    $desktopEquality += [ordered]@{path=$entry.path;bytes=$entry.bytes;sha256=$currentHash;previousCandidateSame=$true}
}
if ($desktopEquality.Count -ne $desktopReceipt.fileCount) { throw 'Desktop payload equality count mismatch.' }

$authorityRoot=Join-Path $candidate 'authority\win-x64'
$authorityInventory=Get-Inventory $authorityRoot
if ($authorityInventory.treeSha256 -ne $ExpectedAuthorityTree.ToUpperInvariant()) { throw 'Copied authority tree mismatch.' }
$checkpointPath=Join-Path $candidate 'authority\metadata\checkpoint.json'
$registryPath=Join-Path $candidate 'authority\metadata\worker-registry.v1.json'
$authorityReceiptPath=Join-Path $candidate (Join-Path 'authority\metadata' $AuthorityReceiptName)
if ((Get-Hash $checkpointPath) -ne $ExpectedCheckpointSha.ToUpperInvariant()) { throw 'Copied checkpoint SHA mismatch.' }
if ((Get-Hash $registryPath) -ne $ExpectedRegistrySha.ToUpperInvariant()) { throw 'Copied registry SHA mismatch.' }

$startupPath=Join-Path $canary 'startup-diagnostic.stdout.log'
$singlePath=Join-Path $canary 'single-instance-diagnostic.stdout.log'
$integrationPath=Join-Path $canary 'integration-startup-canary.stdout.log'
$authorityValidationPath=Join-Path $canary 'authority-validate-registry.log'
$manifestGatePath=Join-Path $canary 'manifest-tree-gate.json'
$desktopEqualityPath=Join-Path $canary 'desktop-payload-equality.json'
foreach($path in @($startupPath,$singlePath,$integrationPath,$authorityValidationPath,$manifestGatePath,$desktopEqualityPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence missing: $path" }
}
$startup=Get-Content -LiteralPath $startupPath -Raw | ConvertFrom-Json
$single=Get-Content -LiteralPath $singlePath -Raw | ConvertFrom-Json
$integration=Get-Content -LiteralPath $integrationPath -Raw | ConvertFrom-Json
$authorityValidation=Get-Content -LiteralPath $authorityValidationPath -Raw | ConvertFrom-Json
$manifestGate=Get-Content -LiteralPath $manifestGatePath -Raw | ConvertFrom-Json
$desktopEqualityEvidence=Get-Content -LiteralPath $desktopEqualityPath -Raw | ConvertFrom-Json
if ($startup.result -ne 'PASS' -or -not $startup.candidateOrchestratorAutoStart -or -not $startup.candidateOrchestratorPathsVerified -or $startup.authorityCheckpointId -ne $CheckpointId -or $startup.authorityTreeSha256 -ne $ExpectedAuthorityTree.ToUpperInvariant() -or $startup.externalConnections -ne 0 -or $startup.namedPipeInvoked) { throw 'Safe startup diagnostic failed.' }
if (-not $single.existingInstanceDetected -or $single.userFeedbackMode -ne 'activate-existing-window-and-message-box' -or -not $single.existingProcessId -or [string]::IsNullOrWhiteSpace($single.existingProcessPath)) { throw 'Existing-instance non-silent regression failed.' }
if ($integration.result -ne 'PASS' -or -not $integration.orchestratorStartedByDesktop -or -not $integration.namedPipeInvoked -or $integration.authorityCheckpointId -ne $CheckpointId -or $integration.authorityTreeSha256 -ne $ExpectedAuthorityTree.ToUpperInvariant() -or $integration.externalWorkerInvoked -or $integration.physicalSpeakerInvoked -or $integration.remNoteInvoked -or $integration.ankiInvoked -or $integration.mineruInvoked) { throw 'Built-in orchestrator/Named Pipe integration failed.' }
if ($authorityValidation.status -ne 'valid' -or $authorityValidation.registryVersion -ne $AuthorityVersion -or $authorityValidation.workers -ne 5) { throw 'Candidate authority registry validation failed.' }
if ($manifestGate.status -ne 'PASS' -or $manifestGate.candidateTreeSha256 -ne $inventory.treeSha256 -or $manifestGate.authorityTreeSha256 -ne $ExpectedAuthorityTree.ToUpperInvariant()) { throw 'Manifest/tree gate failed.' }
if ($desktopEqualityEvidence.status -ne 'PASS' -or $desktopEqualityEvidence.fileCount -ne $desktopReceipt.fileCount -or $desktopEqualityEvidence.changedFiles -ne 0) { throw 'Desktop payload equality evidence failed.' }
if (Get-Process -Id $integration.orchestratorProcessId -ErrorAction SilentlyContinue) { throw 'Canary-owned orchestrator process was not stopped.' }

$previousWindowsReceipt=if ([IO.Path]::IsPathRooted($PriorWindowsUiReceipt)) { $PriorWindowsUiReceipt } else { Join-Path $project $PriorWindowsUiReceipt }
if (-not (Test-Path -LiteralPath $previousWindowsReceipt -PathType Leaf)) { throw "Prior Windows UI receipt missing: $previousWindowsReceipt" }

$settingsCheckpointEvidence=$null
if (-not [string]::IsNullOrWhiteSpace($SettingsCheckpointRelativePath)) {
    $settingsCheckpointPath=if ([IO.Path]::IsPathRooted($SettingsCheckpointRelativePath)) { $SettingsCheckpointRelativePath } else { Join-Path $project $SettingsCheckpointRelativePath }
    if (-not (Test-Path -LiteralPath $settingsCheckpointPath -PathType Leaf)) { throw "Settings checkpoint missing: $settingsCheckpointPath" }
    $settingsCheckpointHash=Get-Hash $settingsCheckpointPath
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSettingsCheckpointSha) -and $settingsCheckpointHash -ne $ExpectedSettingsCheckpointSha.ToUpperInvariant()) { throw 'Settings checkpoint SHA mismatch.' }
    $settingsCheckpointDocument=Get-Content -LiteralPath $settingsCheckpointPath -Raw | ConvertFrom-Json
    if ($settingsCheckpointDocument.status -notlike 'passed-*') { throw 'Settings checkpoint did not pass.' }
    $settingsCheckpointEvidence=[ordered]@{
        path=[IO.Path]::GetRelativePath($project,$settingsCheckpointPath).Replace('\','/')
        sha256=$settingsCheckpointHash
        checkpointVersion=$settingsCheckpointDocument.checkpointVersion
        status=$settingsCheckpointDocument.status
        coreRuntime=$settingsCheckpointDocument.core.version
        phaseBFinal=$settingsCheckpointDocument.authorityBoundary.phaseBFinal
    }
}

$invalidatedAuthorities=@()
foreach($relative in $InvalidationReceiptRelativePaths) {
    $path=if ([IO.Path]::IsPathRooted($relative)) { $relative } else { Join-Path $project $relative }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Invalidation receipt missing: $path" }
    $document=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($document.status -ne 'invalidated-never-promoted') { throw "Authority is not invalidated-never-promoted: $path" }
    $invalidatedAuthorities += [ordered]@{
        registryVersion=$document.registryVersion
        status=$document.status
        usedByCandidate=$false
        path=[IO.Path]::GetRelativePath($project,$path).Replace('\','/')
        sha256=Get-Hash $path
    }
}
$receipt=[ordered]@{
    schemaVersion='knowledge-clipper-integrated-desktop-candidate-receipt.v1'
    candidateId=$CandidateId
    createdAt=(Get-Date).ToUniversalTime().ToString('o')
    status='PASS-AUTOMATED-AUTHORITY-REBASE'
    productName='零碎知识剪藏'
    entrypoint=('releases/'+$CandidateId+'/win-x64/KnowledgeClipper.exe')
    entrypointSha256=Get-Hash (Join-Path $candidate 'KnowledgeClipper.exe')
    candidate=[ordered]@{root=('releases/'+$CandidateId+'/win-x64');manifest='candidate-file-manifest.v1.json';manifestSha256=Get-Hash $manifestPath;fileCount=$inventory.fileCount;totalBytes=$inventory.totalBytes;treeSha256=$inventory.treeSha256;treeAlgorithm=$inventory.treeAlgorithm;files=$inventory.files}
    desktop=[ordered]@{
        unchangedFromCandidate=$PreviousCandidateId
        buildId=$desktopReceipt.buildId
        buildReceiptSha256=Get-Hash $desktopReceiptPath
        payloadFiles=$desktopEquality.Count
        payloadBytes=$desktopReceipt.totalBytes
        payloadTreeSha256=$desktopReceipt.treeSha256.ToUpperInvariant()
        exeSha256=Get-Hash (Join-Path $candidate 'KnowledgeClipper.exe')
        dllSha256=Get-Hash (Join-Path $candidate 'KnowledgeClipper.dll')
        perFileEquality=$desktopEquality
        priorWindowsUiReceipt=[IO.Path]::GetRelativePath($project,$previousWindowsReceipt).Replace('\','/')
        priorWindowsUiReceiptSha256=Get-Hash $previousWindowsReceipt
        uiRetestRequired=$false
        uiRetestReason="All $($desktopEquality.Count) receipt-bound desktop payload files are byte-for-byte identical; only the embedded authority subtree and integration metadata changed."
    }
    authority=[ordered]@{checkpointId=$CheckpointId;checkpointSha256=Get-Hash $checkpointPath;authorityReceiptSha256=Get-Hash $authorityReceiptPath;registryVersion=$AuthorityVersion;registrySha256=Get-Hash $registryPath;releaseFiles=$authorityInventory.fileCount;releaseBytes=$authorityInventory.totalBytes;releaseTreeSha256=$authorityInventory.treeSha256;copiedExactly=$true}
    settingsCheckpoint=$settingsCheckpointEvidence
    invalidatedAuthorities=$invalidatedAuthorities
    backendCandidateBoundaries=$BackendCandidateBoundaries
    automatedCanaries=[ordered]@{
        safeStartup=[ordered]@{status=$startup.result;candidatePathsVerified=$startup.candidateOrchestratorPathsVerified;externalConnections=$startup.externalConnections;log='startup-diagnostic.stdout.log';sha256=Get-Hash $startupPath}
        existingInstance=[ordered]@{status='PASS-NOT-SILENT';observedProcessId=$single.existingProcessId;observedProcessPath=$single.existingProcessPath;existingWindowHandle=$single.existingWindowHandle;feedbackMode=$single.userFeedbackMode;oldProcessTerminated=$false;log='single-instance-diagnostic.stdout.log';sha256=Get-Hash $singlePath}
        builtInOrchestrator=[ordered]@{status=$integration.result;startedByDesktop=$integration.orchestratorStartedByDesktop;processId=$integration.orchestratorProcessId;processStoppedAfterCanary=$true;authorityCheckpointId=$integration.authorityCheckpointId;namedPipeAction=$integration.action;queryItemCount=$integration.queryItemCount;externalWorkerInvoked=$integration.externalWorkerInvoked;log='integration-startup-canary.stdout.log';sha256=Get-Hash $integrationPath}
        authorityRegistry=[ordered]@{status=$authorityValidation.status;workers=$authorityValidation.workers;log='authority-validate-registry.log';sha256=Get-Hash $authorityValidationPath}
        manifestTree=[ordered]@{status=$manifestGate.status;log='manifest-tree-gate.json';sha256=Get-Hash $manifestGatePath}
        desktopPayloadEquality=[ordered]@{status=$desktopEqualityEvidence.status;files=$desktopEqualityEvidence.fileCount;changedFiles=$desktopEqualityEvidence.changedFiles;log='desktop-payload-equality.json';sha256=Get-Hash $desktopEqualityPath}
        safety=[ordered]@{physicalSpeakerInvoked=$false;remNoteInvoked=$false;ankiInvoked=$false;mineruInvoked=$false;externalWorkersInvoked=$false;externalProductionApisInvoked=$false}
    }
    userHandTestEntrypoint=$false
    userHandTestNode=[ordered]@{requiredForAuthorityRebase=$false;reason="The receipt-bound Desktop EXE, DLL and all $($desktopEquality.Count) UI/runtime payload files are unchanged; Core final candidate-bound and Quality gates remain pending."}
    noGoBoundaries=@('desktop-to-real RemNote/Anki export E2E not run','physical speaker not invoked by Desktop rebase Canary','physical system screenshot selection and cross-monitor mixed-DPI drag remain user/environment checks','real external application selection/clipboard remains a user experience check','no sealed installer or code signing') + $AdditionalNoGoBoundaries
}
$temporary=$output+'.tmp-'+[Guid]::NewGuid().ToString('N')
$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
Move-Item -LiteralPath $temporary -Destination $output
Write-Output $output

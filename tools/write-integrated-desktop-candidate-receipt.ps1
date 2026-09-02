param(
    [Parameter(Mandatory=$true)][string]$CandidateId,
    [Parameter(Mandatory=$true)][string]$DesktopBuildId,
    [Parameter(Mandatory=$true)][string]$WindowsCanaryId
)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$candidate=Join-Path $project (Join-Path 'releases' (Join-Path $CandidateId 'win-x64'))
$canary=Join-Path $project (Join-Path 'canary\desktop-integrated' $CandidateId)
$windowsReceipt=Join-Path $project (Join-Path 'canary\desktop-windows' (Join-Path $WindowsCanaryId 'receipt.json'))
$output=Join-Path $project (Join-Path 'receipts' ("desktop-integrated-candidate-$CandidateId.json"))
if (Test-Path -LiteralPath $output) { throw "Immutable receipt already exists: $output" }

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Get-Inventory([string]$Root) {
    $records=@(); $rows=@(); [long]$bytes=0
    $byRelative=@{}; [string[]]$relativeNames=@(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object { $relative=[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/'); $byRelative[$relative]=$_.FullName; $relative })
    [Array]::Sort[string]($relativeNames,[StringComparer]::Ordinal)
    foreach($relative in $relativeNames) {
        $file=Get-Item -LiteralPath $byRelative[$relative]
        $hash=(Get-Hash $file.FullName)
        $records += [ordered]@{path=$relative;bytes=$file.Length;sha256=$hash}
        $rows += "$relative`t$($file.Length)`t$($hash.ToLowerInvariant())"
        $bytes += $file.Length
    }
    $material=[Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    $sha=[Security.Cryptography.SHA256]::Create()
    try {$tree=[Convert]::ToHexString($sha.ComputeHash($material))} finally {$sha.Dispose()}
    [ordered]@{files=$records;fileCount=$records.Count;totalBytes=$bytes;treeSha256=$tree;treeAlgorithm='sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)'}
}

$manifestPath=Join-Path $candidate 'candidate-file-manifest.v1.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 'knowledge-clipper-integrated-candidate-manifest.v1' -or $manifest.candidateId -ne $CandidateId) { throw 'Candidate manifest identity mismatch.' }
foreach($entry in $manifest.payload.files) {
    $path=Join-Path $candidate ($entry.path.Replace('/','\'))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Candidate payload missing: $($entry.path)" }
    if ((Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-Hash $path) -ne $entry.sha256) { throw "Candidate payload mismatch: $($entry.path)" }
}
$inventory=Get-Inventory $candidate
if ($inventory.fileCount -ne ($manifest.payload.fileCount+1) -or $inventory.totalBytes -ne ($manifest.payload.totalBytes+(Get-Item -LiteralPath $manifestPath).Length)) { throw 'Candidate sealed inventory aggregate mismatch.' }

$authorityRoot=Join-Path $candidate 'authority\win-x64'
$authorityInventory=Get-Inventory $authorityRoot
if ($authorityInventory.fileCount -ne 79 -or $authorityInventory.totalBytes -ne 248977927 -or $authorityInventory.treeSha256 -ne 'F15F21E5489B4002086B0664144606A7E2EF5BD40E1AA02AB90F34EC277C2503') { throw 'Copied authority v1.2.2 tree mismatch.' }

$startupPath=Join-Path $canary 'startup-diagnostic.stdout.log'
$singlePath=Join-Path $canary 'single-instance-diagnostic.stdout.log'
$integrationPath=Join-Path $canary 'integration-startup-canary.stdout.log'
$authorityValidationPath=Join-Path $canary 'authority-validate-registry.log'
$desktopTestsPath=Join-Path $canary 'desktop-build-and-test.log'
$integrationUnitPath=Join-Path $canary 'desktop-integration-canary.log'
$visualPath=Join-Path $canary 'visual-preview-canary.log'
$brandPath=Join-Path $canary 'brand-surface-canary.log'
foreach($path in @($startupPath,$singlePath,$integrationPath,$authorityValidationPath,$desktopTestsPath,$integrationUnitPath,$visualPath,$brandPath,$windowsReceipt)) { if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Evidence missing: $path"} }

$startup=Get-Content -LiteralPath $startupPath -Raw | ConvertFrom-Json
$single=Get-Content -LiteralPath $singlePath -Raw | ConvertFrom-Json
$integration=Get-Content -LiteralPath $integrationPath -Raw | ConvertFrom-Json
$authorityValidation=Get-Content -LiteralPath $authorityValidationPath -Raw | ConvertFrom-Json
$windows=Get-Content -LiteralPath $windowsReceipt -Raw | ConvertFrom-Json
$desktopTests=Get-Content -LiteralPath $desktopTestsPath -Raw
$integrationUnit=Get-Content -LiteralPath $integrationUnitPath -Raw
$visual=Get-Content -LiteralPath $visualPath -Raw
$brand=Get-Content -LiteralPath $brandPath -Raw
if ($startup.result -ne 'PASS' -or -not $startup.candidateIntegrationConfigured -or -not $startup.candidateOrchestratorAutoStart -or -not $startup.candidateOrchestratorPathsVerified -or $startup.externalConnections -ne 0 -or $startup.namedPipeInvoked) { throw 'Safe startup diagnostic failed.' }
if (-not $single.existingInstanceDetected -or $single.userFeedbackMode -ne 'activate-existing-window-and-message-box' -or -not $single.existingProcessId -or [string]::IsNullOrWhiteSpace($single.existingProcessPath)) { throw 'Existing-instance non-silent regression failed.' }
if ($integration.result -ne 'PASS' -or -not $integration.orchestratorStartedByDesktop -or -not $integration.namedPipeInvoked -or $integration.externalWorkerInvoked -or $integration.physicalSpeakerInvoked -or $integration.remNoteInvoked -or $integration.ankiInvoked -or $integration.mineruInvoked) { throw 'Built-in orchestrator/Named Pipe integration failed.' }
if ($authorityValidation.status -ne 'valid' -or $authorityValidation.registryVersion -ne '1.2.2' -or $authorityValidation.workers -ne 5) { throw 'Candidate authority registry validation failed.' }
if ($windows.automatedWindowsUiCanary.status -ne 'PASS' -or $windows.automatedWindowsUiCanary.assertions -ne 84) { throw 'Windows UI Canary failed.' }
if (-not $desktopTests.Contains('PASS desktop automation build and tests') -or -not $integrationUnit.Contains('DESKTOP_INTEGRATION_CANARY PASS assertions=8') -or -not $visual.Contains('VISUAL_PREVIEW_CANARY PASS') -or -not $brand.Contains('DESKTOP_BRAND_SURFACE_CANARY PASS')) { throw 'Desktop regression evidence incomplete.' }
if (Get-Process -Id $integration.orchestratorProcessId -ErrorAction SilentlyContinue) { throw 'Canary-owned orchestrator process was not stopped.' }

$checkpointPath=Join-Path $candidate 'authority\metadata\checkpoint.json'
$authorityReceiptPath=Join-Path $candidate 'authority\metadata\knowledge-clipper-authority-1.2.2-20260828T012800Z.json'
$desktopBuildReceiptPath=Join-Path $candidate 'desktop-build-receipt.v1.json'
$receipt=[ordered]@{
    schemaVersion='knowledge-clipper-integrated-desktop-candidate-receipt.v1'
    candidateId=$CandidateId
    createdAt=(Get-Date).ToUniversalTime().ToString('o')
    status='PASS-AUTOMATED-USER-HAND-TEST-READY'
    productName='零碎知识剪藏'
    entrypoint=('releases/'+$CandidateId+'/win-x64/KnowledgeClipper.exe')
    entrypointSha256=Get-Hash (Join-Path $candidate 'KnowledgeClipper.exe')
    candidate=[ordered]@{root=('releases/'+$CandidateId+'/win-x64');manifest='candidate-file-manifest.v1.json';manifestSha256=Get-Hash $manifestPath;fileCount=$inventory.fileCount;totalBytes=$inventory.totalBytes;treeSha256=$inventory.treeSha256;treeAlgorithm=$inventory.treeAlgorithm;files=$inventory.files}
    desktop=[ordered]@{buildId=$DesktopBuildId;buildReceiptSha256=Get-Hash $desktopBuildReceiptPath;payloadTreeSha256=$startup.packageTreeSha256.ToUpperInvariant();selfContained=$startup.selfContainedHost;windowsUiAssertions=84;integrationAssertions=8;brand='KnowledgeClipper/零碎知识剪藏'}
    authority=[ordered]@{checkpointId='knowledge-clipper-authority-1.2.2-20260828T012800Z';checkpointSha256=Get-Hash $checkpointPath;authorityReceiptSha256=Get-Hash $authorityReceiptPath;registryVersion='1.2.2';releaseFiles=$authorityInventory.fileCount;releaseBytes=$authorityInventory.totalBytes;releaseTreeSha256=$authorityInventory.treeSha256;copiedExactly=$true}
    automatedCanaries=[ordered]@{
        safeStartup=[ordered]@{status=$startup.result;candidatePathsVerified=$startup.candidateOrchestratorPathsVerified;externalConnections=$startup.externalConnections;log='startup-diagnostic.stdout.log';sha256=Get-Hash $startupPath}
        existingInstance=[ordered]@{status='PASS-NOT-SILENT';observedProcessId=$single.existingProcessId;observedProcessPath=$single.existingProcessPath;existingWindowHandle=$single.existingWindowHandle;feedbackMode=$single.userFeedbackMode;oldProcessTerminated=$false;log='single-instance-diagnostic.stdout.log';sha256=Get-Hash $singlePath}
        builtInOrchestrator=[ordered]@{status=$integration.result;startedByDesktop=$integration.orchestratorStartedByDesktop;processId=$integration.orchestratorProcessId;processStoppedAfterCanary=$true;authorityCheckpointId=$integration.authorityCheckpointId;namedPipeAction=$integration.action;queryItemCount=$integration.queryItemCount;externalWorkerInvoked=$integration.externalWorkerInvoked;log='integration-startup-canary.stdout.log';sha256=Get-Hash $integrationPath}
        authorityRegistry=[ordered]@{status=$authorityValidation.status;workers=$authorityValidation.workers;log='authority-validate-registry.log';sha256=Get-Hash $authorityValidationPath}
        desktopContracts=[ordered]@{status='PASS';log='desktop-build-and-test.log';sha256=Get-Hash $desktopTestsPath}
        desktopIntegration=[ordered]@{status='PASS';assertions=8;log='desktop-integration-canary.log';sha256=Get-Hash $integrationUnitPath}
        windowsUi=[ordered]@{status='PASS';assertions=84;receipt=[IO.Path]::GetRelativePath($project,$windowsReceipt).Replace('\','/');receiptSha256=Get-Hash $windowsReceipt}
        visual=[ordered]@{status='PASS';log='visual-preview-canary.log';sha256=Get-Hash $visualPath;preview='visual-bound/floating-inline-input.png'}
        safety=[ordered]@{physicalSpeakerInvoked=$false;remNoteInvoked=$false;ankiInvoked=$false;mineruInvoked=$false;externalProductionApisInvoked=$false}
    }
    userHandTestNode=[ordered]@{ready=$true;requiresOldProcessExitBeforeNewWindow=$true;reason='The currently running older build still owns the canonical/legacy mutex. The new candidate now reports this explicitly and attempts activation; it does not terminate the old process.'}
    noGoBoundaries=@('desktop-to-real RemNote/Anki export E2E not run','physical speaker not invoked','physical system screenshot selection and cross-monitor mixed-DPI drag remain user/environment checks','real external application selection/clipboard remains a user experience check','no sealed installer or code signing')
}
$temporary=$output+'.tmp-'+[Guid]::NewGuid().ToString('N')
$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
Move-Item -LiteralPath $temporary -Destination $output
Write-Output $output

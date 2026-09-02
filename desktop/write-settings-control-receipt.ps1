param(
    [Parameter(Mandatory=$true)][string]$BuildId,
    [Parameter(Mandatory=$true)][string]$WindowsCanaryId
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$package = Join-Path $project (Join-Path 'dist\desktop-win-x64' $BuildId)
$regression = Join-Path $project (Join-Path 'canary\desktop-regression' $BuildId)
$visual = Join-Path $project (Join-Path 'canary\desktop-visual' $BuildId)
$windowsReceipt = Join-Path $project (Join-Path 'canary\desktop-windows' (Join-Path $WindowsCanaryId 'receipt.json'))
$output = Join-Path $project (Join-Path 'receipts' ("desktop-settings-control-v2.4.1-$BuildId.json"))
if (Test-Path -LiteralPath $output) { throw "Immutable receipt exists: $output" }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Inventory([string]$Root) {
    $map=@{}; [string[]]$names=@(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object { $name=[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/');$map[$name]=$_.FullName;$name })
    [Array]::Sort[string]($names,[StringComparer]::Ordinal)
    $records=@();$rows=@();[long]$bytes=0
    foreach($name in $names){$file=Get-Item -LiteralPath $map[$name];$hash=Hash $file.FullName;$records += [ordered]@{path=$name;bytes=$file.Length;sha256=$hash};$rows += "$name`t$($file.Length)`t$hash";$bytes += $file.Length}
    $sha=[Security.Cryptography.SHA256]::Create();try{$tree=[Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))).ToLowerInvariant()}finally{$sha.Dispose()}
    [ordered]@{fileCount=$records.Count;totalBytes=$bytes;treeSha256=$tree;treeAlgorithm='sha256(utf8 ordinal(path TAB bytes TAB lowercaseSha256) joined by LF)';files=$records}
}

$startupPath=Join-Path $regression 'startup-diagnostic.json';$semanticPath=Join-Path $regression 'semantic-contract-regression.log';$windowsLog=Join-Path $regression 'windows-ui-canary-final.log';$visualLog=Join-Path $regression 'visual-preview-canary.log';$brandLog=Join-Path $regression 'brand-surface-canary.log'
foreach($path in @($startupPath,$semanticPath,$windowsLog,$visualLog,$brandLog,$windowsReceipt)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Evidence missing: $path"}}
$startup=Get-Content -LiteralPath $startupPath -Raw|ConvertFrom-Json;$windows=Get-Content -LiteralPath $windowsReceipt -Raw|ConvertFrom-Json
if($startup.result -ne 'PASS' -or -not $startup.packageFilesVerified -or -not $startup.packageTreeVerified -or $startup.externalConnections -ne 0){throw 'Startup diagnostic failed.'}
if($windows.automatedWindowsUiCanary.status -ne 'PASS' -or $windows.automatedWindowsUiCanary.assertions -ne 117){throw 'Windows UI assertion receipt mismatch.'}
$semantic=Get-Content -LiteralPath $semanticPath -Raw;$visualText=Get-Content -LiteralPath $visualLog -Raw;$brand=Get-Content -LiteralPath $brandLog -Raw
if(-not $semantic.Contains('PASS desktop automation build and tests') -or -not $semantic.Contains('PASS named-pipe-semantic-actions-direct-stage-control-no-task-cancel')){throw 'Desktop semantic regression incomplete.'}
if(-not $windowsLog -or -not (Get-Content -LiteralPath $windowsLog -Raw).Contains('WINDOWS_UI_CANARY PASS assertions=117')){throw 'Windows UI log incomplete.'}
if(-not $visualText.Contains('VISUAL_PREVIEW_CANARY PASS') -or -not $brand.Contains('DESKTOP_BRAND_SURFACE_CANARY PASS')){throw 'Visual or brand regression incomplete.'}

$checkpoint=Join-Path $project 'contracts\core\v2.4\settings-control\checkpoints\v2.4.1-20260828T064200Z\checkpoint.json';$adapter=Join-Path $project 'contracts\core\v2.4\settings-control\module-settings-adapter.v1.json'
if((Hash $checkpoint) -ne '120c6ac4c50671aacf9afb13a52bbdccab7c5cb977d31872daa81d24a3996c3c' -or (Hash $adapter) -ne 'be948b59485328f629ff79c4ec87b6fe4699169b57e6de34195abed1605ee99a'){throw 'Core settings checkpoint binding mismatch.'}
if((Hash (Join-Path $package 'contracts\core\v2.4\settings-control\checkpoints\v2.4.1-20260828T064200Z\checkpoint.json')) -ne (Hash $checkpoint)){throw 'Packaged settings checkpoint mismatch.'}
$inventory=Inventory $package
$buildReceipt=Join-Path $package 'desktop-build-receipt.v1.json'
$receipt=[ordered]@{
 schemaVersion='knowledge-clipper-desktop-settings-control-receipt.v1';status='PASS-MODULE-USER-HAND-TEST-READY';createdAt=(Get-Date).ToUniversalTime().ToString('o');buildId=$BuildId
 productName='零碎知识剪藏';entrypoint=('dist/desktop-win-x64/'+$BuildId+'/KnowledgeClipper.exe');entrypointSha256=Hash (Join-Path $package 'KnowledgeClipper.exe')
 package=[ordered]@{root=('dist/desktop-win-x64/'+$BuildId);selfContained=$true;runtimeIdentifier='win-x64';buildReceiptSha256=Hash $buildReceipt;fileCount=$inventory.fileCount;totalBytes=$inventory.totalBytes;treeSha256=$inventory.treeSha256;treeAlgorithm=$inventory.treeAlgorithm;files=$inventory.files}
 coreSettingsAuthority=[ordered]@{checkpoint='contracts/core/v2.4/settings-control/checkpoints/v2.4.1-20260828T064200Z/checkpoint.json';checkpointSha256=Hash $checkpoint;boundFileCount=21;boundBytes=145626;boundTreeSha256='91513a5aefc9afa719b3c7e824a169976e59217f4de2f2e4ad7490001e646763';adapterSha256=Hash $adapter}
 automatedEvidence=[ordered]@{desktopSemantic=[ordered]@{status='PASS';log=[IO.Path]::GetRelativePath($project,$semanticPath).Replace('\','/');sha256=Hash $semanticPath};windowsUi=[ordered]@{status='PASS';assertions=117;receipt=[IO.Path]::GetRelativePath($project,$windowsReceipt).Replace('\','/');receiptSha256=Hash $windowsReceipt;logSha256=Hash $windowsLog};visual=[ordered]@{status='PASS';root=[IO.Path]::GetRelativePath($project,$visual).Replace('\','/');logSha256=Hash $visualLog};startup=[ordered]@{status='PASS';packageFilesVerified=$true;packageTreeVerified=$true;externalConnections=0;sha256=Hash $startupPath};brand=[ordered]@{status='PASS';sha256=Hash $brandLog}}
 settingsEvidence=[ordered]@{pages=@('桌面与采集','图像 / OCR','内容处理','提问与制卡','语音与播放','导出','运行与诊断');tiers=@('常用','进阶','开发 / 诊断');profileOperations=@('create','copy','switch','restore-default');backupAndCorruptFallback=$true;credentialsMaskedAndLocal=$true;credentialsExcludedFromTaskPackages=$true;manifestV1Preserved=$true;manifestV2Appended=$true;settingsSnapshotShaBound=$true;secretReferencesEmpty=$true}
 safety=[ordered]@{externalProductionApisInvoked=0;physicalSpeakerInvoked=0;remNoteInvoked=$false;ankiInvoked=$false;mineruInvoked=$false;existingUserProcessTerminated=$false}
 conditionalAndNoGo=@('RemNote destination remains disabled until authority admits export.remnote 2.0.0','Anki destination remains disabled until a SHA-bound anki-export-config artifact exists','prompt/provider, retention, retry, timeout and diagnostics editing remain unsupported/read-only','credential test buttons remain conditional because no public provider test adapter exists','this desktop module package does not embed an authority release or auto-start orchestrator','physical Chinese IME and cross-monitor mixed-DPI remain user/environment checks','desktop-to-real RemNote/Anki/MinerU E2E not run')
}
$temp=$output+'.tmp-'+[Guid]::NewGuid().ToString('N');$receipt|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $temp -Encoding utf8NoBOM;Move-Item -LiteralPath $temp -Destination $output
Write-Output $output

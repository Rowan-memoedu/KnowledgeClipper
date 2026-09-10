#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$BaseRoot,
    [Parameter(Mandatory)][string]$BaseManifestSha,
    [Parameter(Mandatory)][string]$DesktopDll,
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$EvidenceRoot
)
. (Join-Path $PSScriptRoot 'DesktopOverlayFiles.ps1')
foreach($path in @($CandidateRoot,$EvidenceRoot)) {
    if(Test-Path -LiteralPath $path){throw ('append-only-output-exists: '+$path)}
    if(![IO.Path]::GetFullPath($path).StartsWith('D:\Artifacts\KnowledgeClipper\',[StringComparison]::OrdinalIgnoreCase)){throw 'artifact-root-required'}
}
$baseManifestPath=Join-Path $BaseRoot 'candidate-file-manifest.v1.json'
$baseManifest=Assert-OverlayManifest $BaseRoot $baseManifestPath $BaseManifestSha
$dllIdentity=Get-OverlayIdentity $DesktopDll
New-Item -ItemType Directory -Path $CandidateRoot,$EvidenceRoot | Out-Null
$unchanged=0
foreach($row in $baseManifest.files) {
    $source=if($row.path -ceq 'KnowledgeClipper.dll'){$DesktopDll}else{Resolve-OverlayFile $BaseRoot $row.path}
    $target=Resolve-OverlayFile $CandidateRoot $row.path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
    [IO.File]::Copy($source,$target,$false)
    if($row.path -ceq 'KnowledgeClipper.dll'){Test-OverlayFile $target $dllIdentity}
    else{Test-OverlayFile $target $row;$unchanged++}
    if($unchanged%500 -eq 0){Write-Host ('COPY '+$unchanged)}
}
$rows=@(Get-OverlayInventory $CandidateRoot)
$tree=Get-OverlayTree $rows
$manifest=[ordered]@{
    schemaVersion=$baseManifest.schemaVersion;candidateId=[IO.Path]::GetFileName($CandidateRoot)
    createdAtUtc=[DateTime]::UtcNow.ToString('o');product='零碎知识剪藏';entrypoint='KnowledgeClipper.exe'
    architecture='win-x64';desktopSelfContained=$true;regressionSlice='WholeWindowZoom';payload=$tree
    bindings=[ordered]@{
        inheritedBase=[ordered]@{root=$BaseRoot;manifestSha256=$BaseManifestSha;bindings=$baseManifest.bindings;role='historical-bindings-runtime-files-unchanged'}
        desktopUiFix=[ordered]@{dll=$dllIdentity;allowedChangedFiles=@('KnowledgeClipper.dll');scope='whole-window zoom plus previously tested settings probe source; no new external qualification'}
    }
    safety=[ordered]@{externalWrites=0;speaker=0;productionRetention='safe-disabled';userHandTestEntrypoint=$true;handTestScope='window zoom only; other runtime capabilities inherited, not requalified'}
    files=$rows
}
$manifestPath=Join-Path $CandidateRoot 'candidate-file-manifest.v1.json'
Write-OverlayJson $manifestPath $manifest
$manifestIdentity=Get-OverlayIdentity $manifestPath
[void](Assert-OverlayManifest $CandidateRoot $manifestPath $manifestIdentity.sha256)
$data=Join-Path 'D:\AppData\KnowledgeClipper' ([IO.Path]::GetFileName($CandidateRoot)+'-safe')
$oldLocal=$env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA=$data
    $exe=Join-Path $CandidateRoot 'KnowledgeClipper.exe'
    foreach($mode in @('startup-diagnostic','single-instance-diagnostic')) {
        & $exe ('--'+$mode) 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot ($mode+'.log'))
        if($LASTEXITCODE -ne 0){throw ('diagnostic-failed: '+$mode)}
    }
    & $exe --integration-startup-canary (Join-Path $data 'core-v3') 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'integration-startup.log')
    if($LASTEXITCODE -ne 0){throw 'integration-startup-failed'}
} finally {$env:LOCALAPPDATA=$oldLocal}
$ipc=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'integration-startup.log') -Raw|ConvertFrom-Json
if($ipc.result -cne 'PASS' -or !$ipc.namedPipeInvoked -or !$ipc.orchestratorStartedByDesktop -or $ipc.action -cne 'capabilities.get' -or $ipc.externalWorkerInvoked -or $ipc.physicalSpeakerInvoked -or $ipc.ankiInvoked){throw 'unsafe-or-failed-ipc-gate'}
if(Get-Process -Id $ipc.orchestratorProcessId -ErrorAction SilentlyContinue){throw 'owned-core-remains'}
[void](Assert-OverlayManifest $CandidateRoot $manifestPath $manifestIdentity.sha256)
Write-OverlayJson (Join-Path $EvidenceRoot 'receipt.json') ([ordered]@{
    status='PASS-WHOLE-WINDOW-ZOOM-DELIVERY';candidate=$CandidateRoot;entrypoint=(Join-Path $CandidateRoot 'KnowledgeClipper.exe')
    manifest=$manifestIdentity;payload=$tree;desktopDll=(Get-OverlayIdentity (Join-Path $CandidateRoot 'KnowledgeClipper.dll'))
    exe=(Get-OverlayIdentity (Join-Path $CandidateRoot 'KnowledgeClipper.exe'));unchangedFiles=$unchanged
    changedFiles=@('KnowledgeClipper.dll');removedFiles=@();coreSdkWorkersRecompiled=$false
    startup=(Get-OverlayIdentity (Join-Path $EvidenceRoot 'startup-diagnostic.log'))
    singleInstance=(Get-OverlayIdentity (Join-Path $EvidenceRoot 'single-instance-diagnostic.log'))
    ipc=(Get-OverlayIdentity (Join-Path $EvidenceRoot 'integration-startup.log'));ownedCoreExited=$true
    externalWorkerRuns=0;externalWrites=0;physicalSpeaker=0;userProcessesTerminated=0
    userHandTestEntrypoint=$true;handTestScope='whole-window zoom';mixedDpiPhysicalTest='not-run'
    assembler=(Get-OverlayIdentity $PSCommandPath)
})
Get-OverlayIdentity (Join-Path $EvidenceRoot 'receipt.json')|ConvertTo-Json

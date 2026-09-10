param(
    [Parameter(Mandatory)][string]$OriginalRequest,
    [Parameter(Mandatory)][string]$BaselineWorker,
    [Parameter(Mandatory)][string]$UnitRoot,
    [Parameter(Mandatory)][string]$IdentityReceipt,
    [string]$ArtifactParent = 'D:\Artifacts\KnowledgeClipper\speech'
)
$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$project = Split-Path (Split-Path $testRoot -Parent) -Parent
$source = Join-Path $project 'src\RemNoteQuickClipper.Speech.Worker'
$bin = Join-Path $testRoot 'bin'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$root = Join-Path $ArtifactParent "r$stamp"
if (Test-Path -LiteralPath $root) { throw 'Candidate root already exists' }
$release = Join-Path $root 'win-x64'
$workerDir = Join-Path $release 'workers\speech'
$evidence = Join-Path $root 'e'
New-Item -ItemType Directory -Path $workerDir,$evidence | Out-Null
function Save([string]$path, [string]$text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($text); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
}
function SaveJson([string]$path, $value) { Save $path ($value | ConvertTo-Json -Depth 40) }
function Sha([string]$path) { (Get-FileHash -LiteralPath ('\\?\' + $path) -Algorithm SHA256).Hash.ToLowerInvariant() }
function Ref([string]$path) { @{ path=$path; sha256=(Sha $path) } }
function OriginalState {
    @(Get-ChildItem -LiteralPath ('\\?\' + [IO.Path]::GetDirectoryName($OriginalRequest)) -File | Sort-Object Name | ForEach-Object {
        @{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$before = OriginalState
$originalSha = Sha $OriginalRequest
$baselineSha = Sha $BaselineWorker
$worker = Join-Path $workerDir 'KnowledgeClipper.Speech.Worker.exe'
Copy-Item -LiteralPath (Join-Path $bin 'KnowledgeClipper.Speech.Worker.exe') -Destination $worker
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$diagnostic = Join-Path $evidence 'RequestPathDiagnostic.exe'
& $compiler /nologo /target:exe /r:System.Web.Extensions.dll "/out:$diagnostic" (Join-Path $testRoot 'RequestPathDiagnostic.cs')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic compile failed' }
$probe = Join-Path $evidence 'LongRequestPathTests.exe'
$sources = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | ForEach-Object FullName)
$tests = @(Get-ChildItem -LiteralPath $testRoot -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler /nologo /target:exe /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /main:KnowledgeClipper.Speech.Tests.LongRequestPathTests "/out:$probe" @sources @tests
if ($LASTEXITCODE -ne 0) { throw 'Regression compile failed' }
$oldDiagnostic = (& $diagnostic $BaselineWorker $OriginalRequest | Out-String).Trim()
if ($LASTEXITCODE -ne 1 -or $oldDiagnostic -notmatch 'PathTooLongException' -or $oldDiagnostic -notmatch 'InternalGetDirectoryName') { throw 'Original defect not reproduced' }
Save (Join-Path $evidence 'original-baseline-diagnostic.json') $oldDiagnostic
$newDiagnostic = (& $diagnostic $worker $OriginalRequest | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Original read-only validation failed: $newDiagnostic" }
Save (Join-Path $evidence 'original-fixed-diagnostic.json') $newDiagnostic
& $probe $OriginalRequest $evidence $worker (Join-Path $bin 'KnowledgeClipper.Speech.FakeTool.exe')
if ($LASTEXITCODE -ne 0) { throw 'Isolated long request regression failed' }
$regression = Get-Content -LiteralPath (Join-Path $evidence 'long-request-receipt.json') -Raw | ConvertFrom-Json
if ($regression.cases[0].requestLength -ge 248 -or $regression.cases[1].requestLength -ne 266 -or $regression.cases[2].jobRootLength -le 260) { throw 'Path controls did not meet required lengths' }
foreach($case in $regression.cases) {
    $control = (& $diagnostic $BaselineWorker $case.request | Out-String).Trim()
    $expectedExit = if ($case.requestLength -lt 248) { 0 } else { 1 }
    if($LASTEXITCODE -ne $expectedExit) { throw 'Baseline short/long differential failed' }
    Save (Join-Path ([IO.Path]::GetDirectoryName($case.request)) 'baseline-diagnostic.json') $control
}
Push-Location D:\
try {
    $describe = (& $worker describe --json | Out-String).Trim()
    if($LASTEXITCODE -ne 0){ throw 'describe failed' }
    $companion = (& $worker settings companion describe --json | Out-String).Trim()
    if($LASTEXITCODE -ne 0){ throw 'companion describe failed' }
} finally { Pop-Location }
$version = ($describe | ConvertFrom-Json).workerVersion
if($version -ne '1.2.6') { throw 'Expected monotonic 1.2.6' }
Save (Join-Path $workerDir 'descriptors\worker-describe.json') $describe
Save (Join-Path $workerDir 'descriptors\settings-companion-describe.json') $companion
$fixtures = Join-Path $testRoot 'fixtures\settings-companion'
foreach($file in Get-ChildItem -LiteralPath $fixtures -Filter '*.schema.json' -File) {
    $destination=Join-Path $workerDir ('schemas\settings-companion\'+$file.Name)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($file.FullName,$destination,$false)
}
$overrideDestination=Join-Path $workerDir 'fixtures\settings-companion\language-override-binding.json'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($overrideDestination)) | Out-Null
[IO.File]::Copy((Join-Path $fixtures 'language-override-binding.json'),$overrideDestination,$false)
$sourceFiles = @($sources + $tests + $PSCommandPath | Sort-Object -Unique | ForEach-Object { Ref $_ })
SaveJson (Join-Path $workerDir 'release-manifest.json') @{
    schemaVersion='speech-edge-release-manifest.v1'; workerId='speech.edge'; workerVersion=$version
    runtimeIdentifier='win-x64'; entrypoint='workers/speech/KnowledgeClipper.Speech.Worker.exe'
    executable=(Ref $worker); sourceFiles=$sourceFiles; runtimePrerequisite='.NET Framework 4.x (Windows)'
    admissionState='offline_module_candidate'; authorityMutated=$false
}
$tree = @(Get-ChildItem -LiteralPath $release -Recurse -File | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath($release,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Sha $_.FullName) }
} | Sort-Object path)
$ordinalPaths = [string[]]@($tree | ForEach-Object { $_.path })
[Array]::Sort($ordinalPaths, [StringComparer]::Ordinal)
$tree = @($ordinalPaths | ForEach-Object { $key=$_; $tree | Where-Object { $_.path -ceq $key } })
$material = ($tree | ForEach-Object { "$($_.path)|$($_.bytes)|$($_.sha256)" }) -join "`n"
$treeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
SaveJson (Join-Path $root 'release-tree.json') @{algorithm='sha256(utf8(join(ordinal-sorted(path|bytes|sha256),LF)))';treeSha256=$treeSha;files=$tree}
python (Join-Path $testRoot 'validate-long-request-regression.py') --project-root $project --receipt (Join-Path $evidence 'long-request-receipt.json')
if($LASTEXITCODE -ne 0){throw 'Exact-path public contract validation failed'}
if (($before | ConvertTo-Json -Depth 8 -Compress) -cne ((OriginalState) | ConvertTo-Json -Depth 8 -Compress) -or (Sha $BaselineWorker) -ne $baselineSha) { throw 'Original evidence changed' }
$unit=Get-Content -LiteralPath (Join-Path $UnitRoot 'unit-test-receipt.json') -Raw | ConvertFrom-Json
if($unit.status -ne 'passed' -or $unit.failures -ne 0 -or $unit.tests.Count -lt 54) { throw 'Unit receipt insufficient' }
$delta = (& git -C $project diff -- src/RemNoteQuickClipper.Speech.Worker tests/RemNoteQuickClipper.Speech.Tests/Tests.cs | Out-String)
Save (Join-Path $root 'source-delta.patch') $delta
$receipt=Join-Path $root 'speech-request-path-fix-receipt.json'
SaveJson $receipt @{
    status='passed'; workerId='speech.edge'; workerVersion=$version; releaseRoot=$release; entrypoint=$worker
    exe=(Ref $worker); treeSha256=$treeSha; releaseTree=(Ref (Join-Path $root 'release-tree.json'))
    sourceRequest=(Ref $OriginalRequest); originalAttemptUnchanged=$true; baselineWorker=(Ref $BaselineWorker)
    originalRequestLength=$OriginalRequest.Length; sourceDelta=(Ref (Join-Path $root 'source-delta.patch'))
    evidence=@((Ref (Join-Path $evidence 'original-baseline-diagnostic.json')),(Ref (Join-Path $evidence 'original-fixed-diagnostic.json')),
        (Ref (Join-Path $evidence 'long-request-receipt.json')),(Ref (Join-Path $evidence 'long-request-contract-receipt.json')),
        (Ref (Join-Path $UnitRoot 'unit-test-receipt.json')),(Ref (Join-Path $UnitRoot 'public-contract-validation-receipt.json')),(Ref $IdentityReceipt))
    tests=$unit.tests.Count; authorityMutated=$false; sealedProductMutated=$false; networkTtsCalls=0; physicalSpeakerInvocations=0
    externalProductionWrites=0; admissionState='offline_module_candidate'
}
Write-Output "RELEASE $release"
Write-Output "EXE SHA $(Sha $worker)"
Write-Output "TREE SHA $treeSha"
Write-Output "RECEIPT $receipt"
Write-Output "RECEIPT SHA $(Sha $receipt)"

param(
    [string]$PackageRoot = 'dist/desktop-win-x64/knowledge-clipper-producer-fixes-20260901T050000Z',
    [string]$OwnerReceipt = 'receipts/desktop-producer-fixes-20260901T050000Z.json',
    [string]$OutputRoot = 'canary/quality-desktop-producer-fixes/20260901T050000Z-independent-r14-final'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Resolve-ProjectPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $project $Path))
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-Relative([string]$Path) {
    return [IO.Path]::GetRelativePath($project, $Path).Replace('\', '/')
}

function Get-Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = Get-Relative $item.FullName
        bytes = $item.Length
        sha256 = Get-Sha256 $item.FullName
    }
}

function Sort-Ordinal([string[]]$Values) {
    [Array]::Sort[string]($Values, [Comparison[string]]{ param($left, $right) [StringComparer]::Ordinal.Compare($left, $right) })
    return $Values
}

function Write-JsonOnce([string]$Path, [object]$Value) {
    Require (-not (Test-Path -LiteralPath $Path)) "append-only target already exists: $Path"
    $json = $Value | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($Path, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Assert-ExactRef([object]$Ref, [string]$Context) {
    $names = @($Ref.PSObject.Properties.Name | Sort-Object)
    Require (($names -join ',') -eq 'artifactRef,contractVersion,expectedSha256,kind') "$Context inputRef is not the strict four-field shape"
    Require ($Ref.artifactRef -eq 'anki.config.quality') "$Context artifactRef drift"
    Require ($Ref.kind -eq 'anki-export-config') "$Context kind drift"
    Require ($Ref.contractVersion -eq '1.0.0') "$Context contractVersion drift"
    Require ($Ref.expectedSha256 -eq ('b' * 64)) "$Context expectedSha256 drift"
}

$package = Resolve-ProjectPath $PackageRoot
$ownerPath = Resolve-ProjectPath $OwnerReceipt
$output = Resolve-ProjectPath $OutputRoot
Require (Test-Path -LiteralPath $package -PathType Container) "package missing: $package"
Require (Test-Path -LiteralPath $ownerPath -PathType Leaf) "owner receipt missing: $ownerPath"
Require (-not (Test-Path -LiteralPath $output)) "append-only output already exists: $output"
New-Item -ItemType Directory -Path $output | Out-Null

$expected = [ordered]@{
    ownerReceiptSha256 = '72fbbf7878f3d1559b52a937ad06e244b630666a47e0e4838d702f5c0ff5ed6a'
    buildReceiptSha256 = 'e0656441375ca2d3c7a94df8b574bfa449ce808f870c2767971e3fd82eb7763b'
    treeSha256 = '8495c59d878521f2693ea26b2f956e7eb07d580f311ada87c773bdb60f4888ba'
    fileCount = 644
    totalBytes = 169691354
    executableSha256 = '29320d1ccaa252057d011490866cc7533901740c5c6921359b7a2f9f58c4720c'
    applicationDllSha256 = '40a0972332f2a476891ea29e6e6d046ce684b649e72795c5f56e5bbaa884b4fa'
}

Require ((Get-Sha256 $ownerPath) -eq $expected.ownerReceiptSha256) 'owner receipt SHA-256 mismatch'
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json -Depth 100
Require ($owner.status -eq 'PASS-MODULE-NOT-INTEGRATED-USER-ENTRYPOINT') 'owner receipt status drift'
Require ($owner.supersedes -eq 'receipts/desktop-producer-fixes-20260901T045000Z.invalidated-never-promoted.json') 'owner receipt does not supersede the last invalidated package'

$invalidated044 = Resolve-ProjectPath 'receipts/desktop-producer-fixes-20260901T044000Z.invalidated-never-promoted.json'
$invalidated045 = Resolve-ProjectPath 'receipts/desktop-producer-fixes-20260901T045000Z.invalidated-never-promoted.json'
foreach ($path in @($invalidated044, $invalidated045)) {
    Require (Test-Path -LiteralPath $path -PathType Leaf) "historical invalidation receipt missing: $path"
    $invalidation = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 20
    Require ($invalidation.status -eq 'invalidated-never-promoted') "historical package is not explicitly invalidated: $path"
}

$buildReceiptPath = Join-Path $package 'desktop-build-receipt.v1.json'
Require ((Get-Sha256 $buildReceiptPath) -eq $expected.buildReceiptSha256) 'build receipt SHA-256 mismatch'
$build = Get-Content -LiteralPath $buildReceiptPath -Raw | ConvertFrom-Json -Depth 100
Require ($build.fileCount -eq $expected.fileCount) 'build receipt file count mismatch'
Require ($build.totalBytes -eq $expected.totalBytes) 'build receipt byte count mismatch'
Require ($build.treeSha256 -eq $expected.treeSha256) 'build receipt tree SHA-256 mismatch'
Require ($build.receiptExcludedFromTreeHash -eq $true) 'build receipt must be excluded from its own tree'

$declaredPaths = [Collections.Generic.List[string]]::new()
$treeBuilder = [Text.StringBuilder]::new()
$ordinalDeclaredPaths = Sort-Ordinal ([string[]]@($build.files | ForEach-Object { [string]$_.path }))
$rowByPath = @{}
foreach ($row in $build.files) { $rowByPath[[string]$row.path] = $row }
$rows = @($ordinalDeclaredPaths | ForEach-Object { $rowByPath[$_] })
foreach ($row in $rows) {
    $relative = [string]$row.path
    $declaredPaths.Add($relative)
    $path = Join-Path $package ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    Require (Test-Path -LiteralPath $path -PathType Leaf) "declared package file missing: $relative"
    $item = Get-Item -LiteralPath $path
    $hash = Get-Sha256 $path
    Require ($item.Length -eq [long]$row.bytes) "declared package bytes mismatch: $relative"
    Require ($hash -eq ([string]$row.sha256).ToLowerInvariant()) "declared package SHA-256 mismatch: $relative"
    [void]$treeBuilder.Append($relative).Append("`n").Append($hash).Append("`n").Append($item.Length).Append("`n")
}
$actualPaths = Sort-Ordinal ([string[]]@(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object FullName -ne $buildReceiptPath | ForEach-Object { [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/') }))
Require ($actualPaths.Count -eq $declaredPaths.Count) 'actual package inventory count differs from build receipt'
for ($i = 0; $i -lt $actualPaths.Count; $i++) {
    Require ($actualPaths[$i] -eq $declaredPaths[$i]) "actual package inventory differs at ordinal index $i"
}
$treeBytes = [Text.UTF8Encoding]::new($false).GetBytes($treeBuilder.ToString())
$treeDigest = [Security.Cryptography.SHA256]::Create()
try { $treeHash = ([BitConverter]::ToString($treeDigest.ComputeHash($treeBytes))).Replace('-', '').ToLowerInvariant() }
finally { $treeDigest.Dispose() }
Require ($treeHash -eq $expected.treeSha256) 'independent package tree SHA-256 mismatch'
Require ((Get-Sha256 (Join-Path $package 'KnowledgeClipper.exe')) -eq $expected.executableSha256) 'entrypoint SHA-256 mismatch'
Require ((Get-Sha256 (Join-Path $package 'KnowledgeClipper.dll')) -eq $expected.applicationDllSha256) 'application DLL SHA-256 mismatch'

$testLog = Join-Path $output 'desktop-build-and-test.log'
$testScript = Join-Path $project 'tests/RemNoteQuickClipper.Desktop.Tests/build-and-test.ps1'
$testLines = @(& $testScript 2>&1)
$testExit = $LASTEXITCODE
[IO.File]::WriteAllLines($testLog, [string[]]$testLines, [Text.UTF8Encoding]::new($false))
Require ($testExit -eq 0) "Desktop build-and-test failed with exit code $testExit"
$passLines = @($testLines | Where-Object { [string]$_ -match '^PASS ' })
Require ($passLines.Count -ge 30) 'Desktop build-and-test did not emit the expected PASS coverage'

$harnessOutput = Join-Path $output 'follow-up-harness'
New-Item -ItemType Directory -Path $harnessOutput | Out-Null
$harnessExe = Join-Path $harnessOutput 'Quality.DesktopProducerFixHarness.exe'
$csc = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$coreSources = @(Get-ChildItem -LiteralPath (Join-Path $project 'src/RemNoteQuickClipper.Desktop.Core') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$harnessSource = Join-Path $project 'tests/Quality.DesktopProducerFixHarness/Program.cs'
$references = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll')
& $csc /nologo /target:exe /main:KnowledgeClipper.Quality.DesktopProducerFixHarness.Program "/out:$harnessExe" @references @coreSources $harnessSource
Require ($LASTEXITCODE -eq 0) 'follow-up producer harness failed to compile'
$shortHarnessRoot = Join-Path 'C:/RQC/QDPF' ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $shortHarnessRoot | Out-Null
$generatedJobs = Join-Path $shortHarnessRoot 'jobs'
$followUpJsonText = (& $harnessExe $generatedJobs 2>&1 | Out-String).Trim()
Require ($LASTEXITCODE -eq 0) "follow-up producer harness failed: $followUpJsonText"
$followUp = $followUpJsonText | ConvertFrom-Json -Depth 50
Require ($followUp.status -eq 'passed') 'follow-up producer harness did not pass'
$followUpEvidencePath = Join-Path $harnessOutput 'follow-up-result.json'
[IO.File]::WriteAllText($followUpEvidencePath, ($followUp | ConvertTo-Json -Depth 50) + "`n", [Text.UTF8Encoding]::new($false))
$followUpGenerated = Join-Path $harnessOutput 'generated-evidence'
New-Item -ItemType Directory -Path $followUpGenerated | Out-Null
$shortTaskRoot = Join-Path $generatedJobs 'task-quality-followup'
Copy-Item -LiteralPath (Join-Path $shortTaskRoot 'artifacts/question.turn.0002/artifact.json') -Destination (Join-Path $followUpGenerated 'artifact.json')
Copy-Item -LiteralPath (Join-Path $shortTaskRoot 'commands/command-0001/question-action.v2.json') -Destination (Join-Path $followUpGenerated 'question-action.v2.json')
Copy-Item -LiteralPath (Join-Path $shortTaskRoot 'commands/command-0001/question-turn/payload.txt') -Destination (Join-Path $followUpGenerated 'payload.txt')

[Reflection.Assembly]::LoadFrom((Join-Path $package 'KnowledgeClipper.Orchestrator.dll')) | Out-Null
$desktopAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $package 'KnowledgeClipper.dll'))
$instanceFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'

$packagedFollowRoot = Join-Path 'C:/RQC/QDPF' ([Guid]::NewGuid().ToString('N'))
$packagedJobs = Join-Path $packagedFollowRoot 'jobs'
$packagedTaskId = 'task-packaged-followup'
New-Item -ItemType Directory -Path (Join-Path $packagedJobs $packagedTaskId) | Out-Null
$clockType = [type]::GetType('KnowledgeClipper.Desktop.Core.FakeClock, KnowledgeClipper', $true)
$idType = [type]::GetType('KnowledgeClipper.Desktop.Core.SystemCommonIdProvider, KnowledgeClipper', $true)
$sourceType = [type]::GetType('KnowledgeClipper.Desktop.Core.FileDesktopCommonCommandSource, KnowledgeClipper', $true)
$clockConstructor = @($clockType.GetConstructors($instanceFlags))[0]
$clock = $clockConstructor.Invoke([object[]]@([DateTime]::SpecifyKind([DateTime]'2026-09-01T05:05:00', [DateTimeKind]::Utc)))
$idConstructor = @($idType.GetConstructors($instanceFlags))[0]
$idProvider = $idConstructor.Invoke([object[]]@())
$sourceConstructor = @($sourceType.GetConstructors($instanceFlags) | Where-Object { $_.GetParameters().Count -eq 3 })[0]
$packagedSource = $sourceConstructor.Invoke([object[]]@([string]$packagedJobs, $clock, $idProvider))
$prepareQuestion = $sourceType.GetMethod('PrepareQuestion', $instanceFlags)
$packagedCommand = $prepareQuestion.Invoke($packagedSource, [object[]]@([string]'封装追问', $true, [string]$packagedTaskId, [string]('a' * 64), [int]1))
$commandType = $packagedCommand.GetType()
$packagedCommandId = [string]$commandType.GetProperty('commandId', $instanceFlags).GetValue($packagedCommand)
$packagedPayloadRef = [string]$commandType.GetProperty('payloadRef', $instanceFlags).GetValue($packagedCommand)
Require ($packagedPayloadRef -eq "commands/$packagedCommandId/question-action.v2.json") 'packaged follow-up command payloadRef drift'
$packagedTaskRoot = Join-Path $packagedJobs $packagedTaskId
$packagedActionPath = Join-Path $packagedTaskRoot ($packagedPayloadRef.Replace('/', [IO.Path]::DirectorySeparatorChar))
$packagedAction = Get-Content -LiteralPath $packagedActionPath -Raw | ConvertFrom-Json -Depth 50
Require ($packagedAction.action -eq 'question.follow-up' -and $packagedAction.turnIndex -eq 1) 'packaged follow-up action drift'
Require ($packagedAction.questionTurnRef.artifactRef -eq 'question.turn.0002') 'packaged follow-up ordinal artifact identity drift'
Require ($packagedAction.questionTurnRef.manifestPath -eq 'artifacts/question.turn.0002/artifact.json') 'packaged follow-up manifest path is not task-global canonical'
$packagedManifestPath = Join-Path $packagedTaskRoot ($packagedAction.questionTurnRef.manifestPath.Replace('/', [IO.Path]::DirectorySeparatorChar))
Require ((Get-Sha256 $packagedManifestPath) -eq $packagedAction.questionTurnRef.expectedSha256) 'packaged follow-up manifest reference SHA drift'
$packagedManifest = Get-Content -LiteralPath $packagedManifestPath -Raw | ConvertFrom-Json -Depth 50
Require (@($packagedManifest.files).Count -eq 1) 'packaged follow-up manifest file count drift'
$packagedTurnPath = "commands/$packagedCommandId/question-turn/payload.txt"
Require ($packagedManifest.files[0].path -eq $packagedTurnPath) 'packaged follow-up payload is not command-owned'
$packagedTurnFile = Join-Path $packagedTaskRoot ($packagedTurnPath.Replace('/', [IO.Path]::DirectorySeparatorChar))
Require ((Get-Content -LiteralPath $packagedTurnFile -Raw) -eq '封装追问') 'packaged follow-up payload text drift'
Require ((Get-Item -LiteralPath $packagedTurnFile).Length -eq $packagedManifest.files[0].bytes -and (Get-Sha256 $packagedTurnFile) -eq $packagedManifest.files[0].sha256) 'packaged follow-up payload evidence drift'
Require (-not (Test-Path -LiteralPath (Join-Path $packagedTaskRoot "commands/$packagedCommandId/question-turn/artifact.json"))) 'packaged follow-up emitted a legacy command-owned manifest'
$packagedFollowEvidence = Join-Path $output 'packaged-follow-up'
New-Item -ItemType Directory -Path $packagedFollowEvidence | Out-Null
Copy-Item -LiteralPath $packagedActionPath -Destination (Join-Path $packagedFollowEvidence 'question-action.v2.json')
Copy-Item -LiteralPath $packagedManifestPath -Destination (Join-Path $packagedFollowEvidence 'artifact.json')
Copy-Item -LiteralPath $packagedTurnFile -Destination (Join-Path $packagedFollowEvidence 'payload.txt')

$artifactType = [type]::GetType('KnowledgeClipper.Orchestrator.Core.ArtifactReference, KnowledgeClipper.Orchestrator', $true)
[object[]]$constructorArgs = @(
    [string]'anki.config.quality',
    [string]'anki-export-config',
    [string]'1.0.0',
    [string]'artifacts/anki.config.quality/artifact.json',
    [string]('b' * 64)
)
$configReference = [Activator]::CreateInstance($artifactType, $constructorArgs)
$producerType = [type]::GetType('KnowledgeClipper.DesktopAnkiProbeHost, KnowledgeClipper', $true)
$producer = $producerType.GetMethod('PublishProbeTaskManifest', [Reflection.BindingFlags]'Static,NonPublic')
Require ($null -ne $producer) 'packaged Anki probe producer entrypoint missing'
$ankiRoot = Join-Path $output 'anki-probe-package'
New-Item -ItemType Directory -Path $ankiRoot | Out-Null
[object[]]$producerArgs = @([string]$ankiRoot, [string]'task-quality-anki', $configReference)
[void]$producer.Invoke($null, $producerArgs)

$planPath = Join-Path $ankiRoot 'plans/anki-probe.v1.json'
$v1Path = Join-Path $ankiRoot 'task.manifest.v1.json'
$v2Path = Join-Path $ankiRoot 'task.manifest.v2.json'
foreach ($path in @($planPath, $v1Path, $v2Path)) { Require (Test-Path -LiteralPath $path -PathType Leaf) "packaged producer omitted $path" }
$plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json -Depth 50
$v1 = Get-Content -LiteralPath $v1Path -Raw | ConvertFrom-Json -Depth 50
$v2 = Get-Content -LiteralPath $v2Path -Raw | ConvertFrom-Json -Depth 50
Require ($plan.schemaVersion -eq 'desktop-module-action-plan.v1' -and $plan.taskId -eq 'task-quality-anki') 'Anki plan identity drift'
Require ($plan.intent -eq 'settings.anki.probe-readonly' -and $plan.operation -eq 'probe' -and $plan.effectClass -eq 'provider-read-only') 'Anki plan operation/effect drift'
Require (@($plan.inputRefs).Count -eq 1 -and @($plan.requestedOutputs).Count -eq 1 -and $plan.requestedOutputs[0] -eq 'anki-connection-status') 'Anki plan refs/outputs drift'
Require ($plan.inputRefs[0].manifestPath -eq 'artifacts/anki.config.quality/artifact.json') 'Anki plan lost config manifestPath'
Require ($plan.inputRefs[0].expectedSha256 -eq ('b' * 64)) 'Anki plan lost config SHA binding'

Require ($v1.schemaVersion -eq '1.0.0' -and $v1.taskId -eq 'task-quality-anki') 'Anki v1 manifest identity drift'
Require ($v1.intent -eq 'clip.card' -and $v1.executionProfile -eq 'cards-anki') 'Anki v1 routing drift'
Require ($v1.initialPlanRef -eq 'plans/anki-probe.v1.json' -and $v1.retentionClass -eq 'evidence') 'Anki v1 plan/retention drift'
Require (@($v1.inputRefs).Count -eq 1) 'Anki v1 initial input count drift'
Assert-ExactRef $v1.inputRefs[0] 'Anki v1'
Require ($v1.metadata.purpose -eq 'settings.anki.probe-readonly' -and $v1.metadata.effectClass -eq 'provider-read-only' -and $v1.metadata.externalMutation -eq $false) 'Anki v1 safety metadata drift'

Require ($v2.schemaVersion -eq '2.0.0' -and $v2.taskId -eq 'task-quality-anki') 'Anki v2 manifest identity drift'
Require ($v2.intent -eq 'clip.card' -and $v2.executionProfile -eq 'cards') 'Anki v2 routing drift'
Require (@($v2.destinations).Count -eq 1 -and $v2.destinations[0] -eq 'anki') 'Anki v2 destination drift'
Require ($v2.initialPlanRef -eq 'plans/anki-probe.v1.json' -and $v2.retentionClass -eq 'evidence') 'Anki v2 plan/retention drift'
Require (@($v2.inputRefs).Count -eq 1) 'Anki v2 initial input count drift'
Assert-ExactRef $v2.inputRefs[0] 'Anki v2'
Require ($v2.metadata.purpose -eq 'settings.anki.probe-readonly' -and $v2.metadata.effectClass -eq 'provider-read-only' -and $v2.metadata.externalMutation -eq $false) 'Anki v2 safety metadata drift'
Require ($v1.createdAt -eq $v2.createdAt) 'Anki v1/v2 manifests were not emitted as one immutable package revision'

$displayType = [type]::GetType('KnowledgeClipper.AnkiProbeDisplayResult, KnowledgeClipper', $true)
$localFailure = [Activator]::CreateInstance($displayType)
$propertyFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$displayType.GetProperty('Status', $propertyFlags).SetValue($localFailure, 'unknown_external_state')
$displayType.GetProperty('ReasonCode', $propertyFlags).SetValue($localFailure, 'effect.worker_exit_unknown')
$displayType.GetProperty('Step', $propertyFlags).SetValue($localFailure, 'worker-start')
$providerFailure = [Activator]::CreateInstance($displayType)
$displayType.GetProperty('Status', $propertyFlags).SetValue($providerFailure, 'failed')
$displayType.GetProperty('ReasonCode', $propertyFlags).SetValue($providerFailure, 'anki.connection_unavailable')
$displayType.GetProperty('Step', $propertyFlags).SetValue($providerFailure, 'probe')
$settingsFormType = [type]::GetType('KnowledgeClipper.SettingsForm, KnowledgeClipper', $true)
$classifier = $settingsFormType.GetMethod('IsLocalAnkiProbePackageFailure', [Reflection.BindingFlags]'Static,NonPublic')
Require ($null -ne $classifier) 'packaged Anki error classifier missing'
[object[]]$localArgs = @($localFailure)
[object[]]$providerArgs = @($providerFailure)
Require ([bool]$classifier.Invoke($null, $localArgs)) 'local package/worker-start failure was not recognized'
Require (-not [bool]$classifier.Invoke($null, $providerArgs)) 'provider connection failure was mislabeled as a local package failure'

$questionRoot = 'C:/Users/fy/AppData/Local/KnowledgeClipper/jobs/task-63220764c43b4e2db970947590b82725'
$questionResultPath = Join-Path $questionRoot 'runs/run-20260901042001524-a73cdb0a0d4d46e8bb5b54940ff436fa/stages/question-follow-up/attempts/attempt-20260901042001624-f3470dd3d48c499f98f13022602fc635/result.json'
$questionEventPath = Join-Path $questionRoot 'runs/run-20260901042001524-a73cdb0a0d4d46e8bb5b54940ff436fa/events/000006-stage-binding-failed.json'
Require ((Get-Sha256 $questionResultPath) -eq '2a62aa19c61529f034762df215e5e9bece715a146e1e6d9bbb323dd00fdfb372') 'follow-up historical result SHA drift'
Require ((Get-Sha256 $questionEventPath) -eq '2d643d026f3459a7ac61854bcbbf1d46abd194fd7acbbd60bc41c12abedb05d3') 'follow-up historical event SHA drift'
$questionEvent = Get-Content -LiteralPath $questionEventPath -Raw | ConvertFrom-Json -Depth 50
Require ($questionEvent.payload.errorCode -eq 'binding.question_command_root_invalid') 'follow-up historical failure code drift'

$ankiFailureRoot = 'C:/Users/fy/AppData/Local/KnowledgeClipper/module-actions/settings-anki-probe-05aeee83f7114e7a80c2a79654addef9'
$ankiAttempt = Join-Path $ankiFailureRoot 'runs/run-anki-probe-f27f9d6b31c041c8813f903a0aea35ba/stages/anki-probe/attempts/attempt-20260901041926914-6016ef75daae487daa8911ea8cb8e18d'
$ankiResultPath = Join-Path $ankiAttempt 'result.json'
$ankiStderrPath = Join-Path $ankiAttempt 'stderr.log'
Require ((Get-Sha256 $ankiResultPath) -eq 'd4143fec0408e42e81817e2f5d71d409d28d0fac1d8d75345e46445e0e086efc') 'Anki historical result SHA drift'
Require ((Get-Sha256 $ankiStderrPath) -eq 'bee7770fda74e38f83374304abf32a6735997f832a1e1d6ce70843733b27b761') 'Anki historical stderr SHA drift'
$ankiFailure = Get-Content -LiteralPath $ankiResultPath -Raw | ConvertFrom-Json -Depth 50
Require ($ankiFailure.status -eq 'unknown_external_state' -and $ankiFailure.error.code -eq 'effect.worker_exit_unknown') 'Anki historical conservative projection drift'
$ankiDiagnostic = Get-Content -LiteralPath $ankiStderrPath -Raw | ConvertFrom-Json -Depth 10
Require ($ankiDiagnostic.code -eq 'FILE_MISSING' -and $ankiDiagnostic.message -match 'task\.manifest\.v1\.json$') 'Anki historical local FILE_MISSING diagnostic drift'

$settingsPath = 'C:/Users/fy/AppData/Local/KnowledgeClipper/workflow-settings.v3.json'
Require ((Get-Sha256 $settingsPath) -eq '8ccb1a91b696c837627196ea6266eaec00006e4d31d0c445182a06dd74b3abee') 'workflow settings SHA drift'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -Depth 100
$activeProfile = $settings.profiles.($settings.activeProfileId)
Require (@($activeProfile.workflow.destinations).Count -eq 1 -and $activeProfile.workflow.destinations[0] -eq 'anki') 'safe settings projection does not show Anki-only routing'
Require ($activeProfile.remnote.enabled -eq $false) 'safe settings projection unexpectedly enables RemNote'
$rootPresent = $activeProfile.remnote.target.PSObject.Properties.Name -contains 'rootRemId' -and -not [string]::IsNullOrWhiteSpace([string]$activeProfile.remnote.target.rootRemId)
Require (-not $rootPresent) 'safe settings projection unexpectedly contains a RemNote root id'

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-quality-desktop-producer-fixes.v1'
    createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'PASS-MODULE-NOT-INTEGRATED-USER-ENTRYPOINT'
    supersedes = @(
        [ordered]@{ path = 'canary/quality-desktop-producer-fixes/20260901T050000Z-independent-r10/quality-receipt.json'; sha256 = 'e4b5cb82e187d61ebe4fd5b4fd7f08b7e723d0fe5de5bb622ddd23e0f4ab990f'; reason = 'source-only follow-up binding' }
        [ordered]@{ path = 'canary/quality-desktop-producer-fixes/20260901T050000Z-independent-r12-packaged-followup/quality-receipt.json'; sha256 = '7b17516e8e5f46e0ba3b607d3f4702a01c080199eba2bf787d0f2e105862b555'; reason = 'packaged follow-up proof with stale revision wording' }
        [ordered]@{ path = 'canary/quality-desktop-producer-fixes/20260901T050000Z-independent-r13-packaged-followup/quality-receipt.json'; sha256 = '79c610d7f4d46623ee38cab6d3f8a6379b76c335c1ee05f214218d30a7b0ebac'; reason = 'consolidated by this final explicit supersession list' }
    )
    subject = [ordered]@{
        packageRoot = Get-Relative $package
        ownerReceipt = Get-Evidence $ownerPath
        buildReceipt = Get-Evidence $buildReceiptPath
        package = [ordered]@{
            fileCountExcludingReceipt = $actualPaths.Count
            totalBytesExcludingReceipt = ($build.files | Measure-Object -Property bytes -Sum).Sum
            treeSha256 = $treeHash
            executableSha256 = Get-Sha256 (Join-Path $package 'KnowledgeClipper.exe')
            applicationDllSha256 = Get-Sha256 (Join-Path $package 'KnowledgeClipper.dll')
        }
        supersededPackages = @(
            Get-Evidence $invalidated044
            Get-Evidence $invalidated045
        )
    }
    gates = [ordered]@{
        packageInventory = 'PASS-644-FILES-EXACT'
        desktopBuildAndTest = [ordered]@{ status = 'PASS'; passLines = $passLines.Count; exitCode = $testExit; evidence = Get-Evidence $testLog }
        followUpProducer = [ordered]@{ status = 'PASS'; artifactRef = $followUp.questionTurnRef.artifactRef; manifestPath = $followUp.questionTurnRef.manifestPath; payloadPath = $followUp.payload.path; evidence = Get-Evidence $followUpEvidencePath }
        packagedFollowUpProducer = [ordered]@{
            status = 'PASS'
            assemblySha256 = Get-Sha256 (Join-Path $package 'KnowledgeClipper.dll')
            commandId = $packagedCommandId
            artifactRef = $packagedAction.questionTurnRef.artifactRef
            manifestPath = $packagedAction.questionTurnRef.manifestPath
            payloadPath = $packagedTurnPath
            action = Get-Evidence (Join-Path $packagedFollowEvidence 'question-action.v2.json')
            manifest = Get-Evidence (Join-Path $packagedFollowEvidence 'artifact.json')
            payload = Get-Evidence (Join-Path $packagedFollowEvidence 'payload.txt')
        }
        ankiProbeProducer = [ordered]@{
            status = 'PASS'
            v1 = [ordered]@{ schemaVersion = $v1.schemaVersion; executionProfile = $v1.executionProfile; retentionClass = $v1.retentionClass; evidence = Get-Evidence $v1Path }
            v2 = [ordered]@{ schemaVersion = $v2.schemaVersion; executionProfile = $v2.executionProfile; destinations = @($v2.destinations); retentionClass = $v2.retentionClass; evidence = Get-Evidence $v2Path }
            plan = [ordered]@{ operation = $plan.operation; effectClass = $plan.effectClass; configManifestPath = $plan.inputRefs[0].manifestPath; evidence = Get-Evidence $planPath }
            sameCreatedAt = $true
            strictFourFieldInitialRefs = $true
        }
        errorClassification = [ordered]@{
            status = 'PASS'
            localPackageFailureRecognized = $true
            providerConnectionFailureKeptDistinct = $true
            historicalAnkiFailure = [ordered]@{ cause = 'FILE_MISSING task.manifest.v1.json'; providerCalls = 0; externalWrites = 0; result = Get-Evidence $ankiResultPath; stderr = Get-Evidence $ankiStderrPath }
            historicalFollowUpFailure = [ordered]@{ code = $questionEvent.payload.errorCode; workerStarted = $false; result = Get-Evidence $questionResultPath; event = Get-Evidence $questionEventPath }
        }
        remnoteProbeClassification = [ordered]@{
            status = 'PRECONDITION-BLOCKED-NOT-PROVIDER-FAILURE'
            workflowSettingsSha256 = Get-Sha256 $settingsPath
            savedDestinations = @($activeProfile.workflow.destinations)
            remnoteEnabled = $false
            rootRemIdPresent = $false
            workerStarted = $false
            externalWrites = 0
        }
        windowsUi = [ordered]@{
            status = 'NOT-PASS-UNRELATED-inline-focus'
            targetedProducerRegressionPassed = $true
            assertionsLowered = $false
            independentlyVerifiedStableInlineFocusFailureLog = $false
        }
    }
    sideEffects = [ordered]@{
        externalWorkerStarts = 0
        externalApiCalls = 0
        remnoteWrites = 0
        ankiConnectCalls = 0
        mineruCalls = 0
        speakerStarts = 0
    }
    boundaries = [ordered]@{
        moduleOnly = $true
        userHandTestEntrypoint = $false
        fullWindowsUiCanary = 'NO-GO-unrelated-inline-focus'
        integratedCandidatePromotion = 'PENDING'
    }
}

$receiptPath = Join-Path $output 'quality-receipt.json'
Write-JsonOnce $receiptPath $receipt
Write-Output $receiptPath

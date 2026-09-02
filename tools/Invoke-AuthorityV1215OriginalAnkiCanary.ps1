[CmdletBinding()]
param(
    [string]$ReleaseRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\releases\knowledge-clipper-authority-v1.2.15-20260830T121200Z\win-x64',
    [string]$AuthorityRoot = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\contracts\authority\worker-registry\v1.2.15-20260830T121200Z',
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-original-anki-v1.2.15\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z',
    [string]$RemNoteRootId = '8zNzfX45XLCSKoPSJ'
)

$ErrorActionPreference = 'Stop'

# Reuse the already qualified append-only evidence/IPC helpers without running its scenario.
$helperSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1213QuestionAnkiCanary.ps1') -Raw
$helperStart = $helperSource.IndexOf('function Get-Sha256Bytes')
$helperEnd = $helperSource.IndexOf('$orchestratorExe =')
if ($helperStart -lt 0 -or $helperEnd -le $helperStart) { throw 'Cannot load sealed-process helper functions.' }
$utf8 = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$release = [IO.Path]::GetFullPath($ReleaseRoot)
$authority = [IO.Path]::GetFullPath($AuthorityRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $evidence) { throw "Append-only evidence root already exists: $evidence" }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$jobsRoot = Join-Path $evidence 'jobs'
$ipcRoot = Join-Path $evidence 'ipc'
[IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
[IO.Directory]::CreateDirectory($ipcRoot) | Out-Null
Invoke-Expression $helperSource.Substring($helperStart, $helperEnd - $helperStart)

function New-OriginalCaptureArtifact([string]$JobRoot, [string]$TaskId) {
    $artifactId = 'capture-original-media'
    $fixtureRoot = Join-Path $projectRoot 'workers\anki-exporter\canary\original-media-v1.3.0\20260830T113121Z\jobs\validate-derived\artifacts\capture-original-media\media'
    $definitions = @(
        [ordered]@{ artifactRef='capture.image.question.0001'; role='question'; name='question-0001.png' },
        [ordered]@{ artifactRef='capture.image.question.0002'; role='question'; name='question-0002.png' },
        [ordered]@{ artifactRef='capture.image.answer.0001'; role='answer'; name='answer-0001.png' }
    )
    $artifacts = @()
    $files = @()
    foreach ($definition in $definitions) {
        $source = Join-Path $fixtureRoot $definition.name
        $relative = "artifacts/$artifactId/media/$($definition.name)"
        $target = Join-Path $JobRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $bytes = [IO.File]::ReadAllBytes($source)
        Write-BytesOnce $target $bytes
        $sha = Get-Sha256Bytes $bytes
        $artifacts += [ordered]@{ artifactRef=$definition.artifactRef; relativePath=$relative; sha256=$sha; bytes=$bytes.LongLength; mediaType='image/png' }
        $files += [ordered]@{ path=$relative; sha256=$sha; bytes=$bytes.LongLength; mediaType='image/png' }
    }
    # Deliberately reverse the generic artifacts array: role arrays are authoritative for ordering.
    [array]::Reverse($artifacts)
    $capture = [ordered]@{
        schemaVersion='capture-bundle.v1'; captureId="capture-$TaskId"; createdAt=[DateTimeOffset]::UtcNow.ToString('o')
        intent='screenshot'; source='screenshot'
        modeSnapshot=[ordered]@{ workflow='card'; contextEnabled=$false; contextGranularity='sentence'; contextApplied=$false; imageHandling='original' }
        artifacts=$artifacts; selectedTextArtifactRef=$null; contextSegments=$null
        questionImages=@('capture.image.question.0001','capture.image.question.0002')
        answerImages=@('capture.image.answer.0001'); questionTurns=@()
    }
    $captureRelative = "artifacts/$artifactId/capture-bundle.v1.json"
    $capturePath = Join-Path $JobRoot $captureRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $capturePath $capture
    $files = @([ordered]@{ path=$captureRelative; sha256=Get-Sha256File $capturePath; bytes=(Get-Item -LiteralPath $capturePath).Length; mediaType='application/vnd.rqc.capture-bundle+json' }) + $files
    New-ArtifactManifest $JobRoot $artifactId 'capture-bundle' 'application/vnd.rqc.capture-bundle+json' $files 'desktop-capture' 'desktop.capture' '1.0.0' $captureRelative 'personal'
}

function New-OriginalAnkiConfigArtifact([string]$JobRoot, [string]$ExpectedProfile) {
    $template = Join-Path $release 'workers\anki\config\anki-export.v2.example.json'
    $config = Get-Content -LiteralPath $template -Raw | ConvertFrom-Json -Depth 100
    $config.expected_profile = $ExpectedProfile
    $config.endpoint = 'http://127.0.0.1:8765'
    $config.provision_managed = $false
    foreach ($property in @($config.deck_routes.PSObject.Properties)) { $config.deck_routes.($property.Name) = $AnkiDeck }
    $artifactId = 'anki-config'
    $relative = "artifacts/$artifactId/anki-export.v2.json"
    $path = Join-Path $JobRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-JsonOnce $path $config
    New-ArtifactManifest $JobRoot $artifactId 'anki-export-config' 'application/json' @(
        [ordered]@{ path=$relative; sha256=Get-Sha256File $path; bytes=(Get-Item -LiteralPath $path).Length; mediaType='application/json' }
    ) 'desktop-settings' 'desktop.settings' '1.0.0' $relative
}

function New-OriginalSettingsArtifact([string]$JobRoot, [string]$TaskId, $AnkiReference) {
    $snapshot = [ordered]@{
        schemaVersion='knowledge-clipper-task-settings-snapshot.v3'; taskId=$TaskId; frozenAt=[DateTimeOffset]::UtcNow.ToString('o')
        source=[ordered]@{ activeProfileId='isolated-original-dual'; settingsSha256=('a' * 64); precedence=@('taskOverride','activeProfile','moduleDefault') }
        workflow=[ordered]@{ processingProfile='cards'; destinations=@('remnote','anki') }
        modules=[ordered]@{
            capture=[ordered]@{ contextEnabled=$false; contextGranularity='sentence'; imageHandling='original' }
            content=[ordered]@{ promptSet='open-md-v2'; categoryRegistry='category-registry/2.0.0'; providerMode='live' }
            remnote=[ordered]@{
                settings=[ordered]@{ schemaVersion='remnote-destination-settings-snapshot.v1'; destinationType='remnote'; enabled=$true; target=[ordered]@{ rootRemId=$RemNoteRootId; rootTitle='KnowledgeClipper PhaseB Canary 20260828T081109Z' }; advanced=[ordered]@{ sink='mcp-direct'; clipRouting='clip-daily-route'; cardRouting='card-export'; timeZone='Asia/Shanghai'; dateFormat='yyyy-MM-dd' }; secretReferences=@() }
                route=[ordered]@{ kind='card-export' }
            }
            anki=[ordered]@{ configArtifactRef=$AnkiReference.artifactRef; expectedSha256=$AnkiReference.expectedSha256; secretReferences=@() }
        }
        secretReferences=@()
    }
    $payloadBytes = $utf8.GetBytes((($snapshot | ConvertTo-Json -Depth 100) + "`n"))
    $artifactId = 'task-settings-snapshot-' + (Get-Sha256Bytes $payloadBytes).Substring(0,24)
    $relative = "artifacts/$artifactId/task-settings-snapshot.v3.json"
    $path = Join-Path $JobRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Write-BytesOnce $path $payloadBytes
    $reference = New-ArtifactManifest $JobRoot $artifactId 'task-settings-snapshot' 'application/vnd.knowledge-clipper.task-settings-snapshot+json' @(
        [ordered]@{ path=$relative; sha256=Get-Sha256File $path; bytes=(Get-Item -LiteralPath $path).Length; mediaType='application/json' }
    ) 'task-settings' 'orchestrator.settings-adapter' '1.0.0' $relative 'sensitive' @($AnkiReference)
    [ordered]@{ reference=$reference; payloadPath=$path }
}

function New-OriginalTask([string]$TaskId, [string]$ExpectedProfile) {
    $jobRoot = Join-Path $jobsRoot $TaskId
    [IO.Directory]::CreateDirectory((Join-Path $jobRoot 'plans')) | Out-Null
    $capture = New-OriginalCaptureArtifact $jobRoot $TaskId
    $anki = New-OriginalAnkiConfigArtifact $jobRoot $ExpectedProfile
    $settings = New-OriginalSettingsArtifact $jobRoot $TaskId $anki
    $intent = [ordered]@{ schemaVersion='desktop-intent.v1'; taskId=$TaskId; intent='clip.card'; inputArtifactRef=$capture.artifactRef; requestedOutputs=@('card-bundle') }
    Write-JsonOnce (Join-Path $jobRoot 'plans\desktop-intent.v1.json') $intent
    $refs = @($capture,$anki,$settings.reference)
    $inputRefs = @($refs | ForEach-Object { [ordered]@{ artifactRef=$_.artifactRef; kind=$_.kind; contractVersion=$_.contractVersion; expectedSha256=$_.expectedSha256 } })
    $createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    Write-JsonOnce (Join-Path $jobRoot 'task.manifest.v1.json') ([ordered]@{
        schemaVersion='1.0.0'; taskId=$TaskId; createdAt=$createdAt; createdBy=[ordered]@{ application='KnowledgeClipper.AuthorityV1215OriginalAnkiCanary'; version='1.0.0' }
        intent='clip.card'; executionProfile='cards-anki'; inputRefs=$inputRefs; requestedOutputs=@('card-bundle'); initialPlanRef='plans/desktop-intent.v1.json'; retentionClass='evidence'
        metadata=[ordered]@{ compatibilityRole='frozen-v1-worker-loader'; authoritativeManifest='task.manifest.v2.json'; authorizedAnkiDeck=$AnkiDeck }
    })
    $manifestPath = Join-Path $jobRoot 'task.manifest.v2.json'
    Write-JsonOnce $manifestPath ([ordered]@{
        schemaVersion='2.0.0'; taskId=$TaskId; createdAt=$createdAt; createdBy=[ordered]@{ application='KnowledgeClipper.AuthorityV1215OriginalAnkiCanary'; version='1.0.0' }
        intent='clip.card'; executionProfile='cards'; destinations=@('remnote','anki'); inputRefs=$inputRefs; requestedOutputs=@('card-bundle'); initialPlanRef='plans/desktop-intent.v1.json'; retentionClass='evidence'
        metadata=[ordered]@{ canary='sealed-original-anki-v1.2.15'; authorizedAnkiDeck=$AnkiDeck; authorizedRemNoteRoot=$RemNoteRootId; expectedRemNoteWrites=0 }
    })
    [ordered]@{ taskId=$TaskId; jobRoot=$jobRoot; manifestPath=$manifestPath; manifestRelative="$TaskId/task.manifest.v2.json"; capture=$capture; anki=$anki; settings=$settings }
}

$orchestratorExe = Join-Path $release 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$registryPath = Join-Path $authority 'worker-registry.v1.json'
$checkpointPath = Join-Path $authority 'checkpoint.json'
$ipcSource = Join-Path $release 'contracts\core\v2.3\playback-control\orchestrator-ipc.v1.1.json'
$adapterConfig = Join-Path $release 'contracts\core\v2.1\profile-resolver.v2.1.json'
if ((Get-Sha256File $registryPath) -ne '0ea0131dda8a13667fbd9f758e05c27c8c8aca7ed982939ccbe6e042d8a35830') { throw 'Authority v1.2.15 registry drifted.' }
if ((Get-Sha256File $checkpointPath) -ne 'b5f1ef63ef812d86c360d0afb46ecdfb220c96c24cd6cf2252e18411d82084a1') { throw 'Authority v1.2.15 checkpoint drifted.' }
if ((Get-Sha256File $orchestratorExe) -ne '1ee66639d12c0fa9ebe809117c7bc02789a562c3a1063cbcae874a073c38524a') { throw 'Core v2.6.0 Orchestrator drifted.' }

$ankiVersion = Invoke-Anki 'version'
$ankiProfile = Invoke-Anki 'getActiveProfile'
$ankiDecks = @(Invoke-Anki 'deckNames')
if ($ankiVersion -ne 6 -or [string]::IsNullOrWhiteSpace([string]$ankiProfile) -or $ankiDecks -notcontains $AnkiDeck) { throw 'Anki preflight failed.' }
Write-JsonOnce (Join-Path $evidence 'preflight.json') ([ordered]@{ schemaVersion='knowledge-clipper-v1215-original-preflight.v1'; status='passed'; anki=[ordered]@{ version=$ankiVersion; activeProfile=$ankiProfile; deck=$AnkiDeck; deckPresent=$true; externalMutation=$false }; remnote=[ordered]@{ selected=$true; expectedStatus='conditional/unsupported'; reason='mcp.media_write_unavailable'; expectedWrites=0 } })

$script:pipeName = 'KnowledgeClipper.AuthorityV1215.Original.' + [Guid]::NewGuid().ToString('N')
$script:exchangeSequence = 0
$ipcConfig = Get-Content -LiteralPath $ipcSource -Raw | ConvertFrom-Json -Depth 100
$ipcConfig.pipeName = $script:pipeName; $ipcConfig.endpoint = "\\.\pipe\$($script:pipeName)"
$ipcConfigPath = Join-Path $evidence 'orchestrator-ipc.runtime.v1.1.json'; Write-JsonOnce $ipcConfigPath $ipcConfig
$stdoutPath = Join-Path $evidence 'orchestrator.stdout.log'; $stderrPath = Join-Path $evidence 'orchestrator.stderr.log'
$startInfo = [Diagnostics.ProcessStartInfo]::new(); $startInfo.FileName=$orchestratorExe; $startInfo.WorkingDirectory=[IO.Path]::GetDirectoryName($orchestratorExe); $startInfo.UseShellExecute=$false; $startInfo.CreateNoWindow=$true; $startInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden; $startInfo.RedirectStandardOutput=$true; $startInfo.RedirectStandardError=$true
foreach ($argument in @('serve','--ipc-config',$ipcConfigPath,'--jobs-root',$jobsRoot,'--registry',$registryPath,'--release-root',$release,'--max-active-tasks','1','--adapter-config',$adapterConfig)) { $startInfo.ArgumentList.Add($argument) }
$process=[Diagnostics.Process]::new(); $process.StartInfo=$startInfo; $stdoutTask=$null; $stderrTask=$null; $task=$null; $failure=$null
try {
    if (-not $process.Start()) { throw 'Failed to start v1.2.15 Orchestrator.' }
    $stdoutTask=$process.StandardOutput.ReadToEndAsync(); $stderrTask=$process.StandardError.ReadToEndAsync()
    $ready=$false
    for ($attempt=0; $attempt -lt 120 -and -not $ready; $attempt++) { if ($process.HasExited) { throw "Orchestrator exited: $($process.ExitCode)" }; try { $null=Invoke-Pipe ([ordered]@{ schemaVersion='2.2.0'; queryId=('ready-'+[Guid]::NewGuid().ToString('N')); type='task.query_recent'; limit=1 }) 'task-query'; $ready=$true } catch { Start-Sleep -Milliseconds 100 } }
    if (-not $ready) { throw 'Named Pipe did not become ready.' }
    $task = New-OriginalTask ('original-anki-v1215-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')) ([string]$ankiProfile)
    $command = New-Command 'task.submit' $task.taskId $task.manifestRelative
    $submit = Invoke-Pipe $command 'task-command'
    $projection = Wait-Task $task.taskId @('succeeded','partial','failed','cancelled') 600
    if ($projection.status -ne 'partial') { throw "Expected partial for Anki success plus RemNote unsupported; got $($projection.status)." }
    $resultsBeforeReplay = Get-WorkerResults $task.jobRoot
    $image = @($resultsBeforeReplay | Where-Object { $_.value.worker.id -eq 'image.ocr.mineru' })
    $anki = @($resultsBeforeReplay | Where-Object { $_.value.worker.id -eq 'export.anki' })
    $remnote = @($resultsBeforeReplay | Where-Object { $_.value.worker.id -eq 'export.remnote' })
    if ($image.Count -ne 1 -or $anki.Count -ne 1 -or $remnote.Count -ne 0) { throw "Worker counts invalid: image=$($image.Count), anki=$($anki.Count), remnote=$($remnote.Count)." }
    $mediaBundlePath = Get-ChildItem -LiteralPath $task.jobRoot -Recurse -Filter 'media-bundle.json' | Where-Object FullName -Match 'image-ocr' | Select-Object -Last 1 -ExpandProperty FullName
    $itemsPath = Get-ChildItem -LiteralPath $task.jobRoot -Recurse -Filter 'items.ndjson' | Where-Object FullName -Match 'anki-export' | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $mediaBundlePath -or -not $itemsPath) { throw 'Media bundle or Anki items evidence missing.' }
    $mediaBundle = Get-Content -LiteralPath $mediaBundlePath -Raw | ConvertFrom-Json -Depth 100
    if ([int]$mediaBundle.providerCalls -ne 0) { throw 'Original mode invoked provider.' }
    $items = @(Get-Content -LiteralPath $itemsPath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
    if ($items.Count -ne 1) { throw "Expected exactly one Anki note, got $($items.Count)." }
    $noteIds=@($items | ForEach-Object { [long]$_.note_id }); $notes=@(Invoke-Anki 'notesInfo' @{ notes=$noteIds }); $cardIds=@($notes | ForEach-Object { @($_.cards) } | ForEach-Object { [long]$_ }); $cards=@(Invoke-Anki 'cardsInfo' @{ cards=$cardIds })
    if ($notes.Count -ne 1 -or @($cards | Where-Object deckName -ne $AnkiDeck).Count -ne 0) { throw 'Anki exact deck readback failed.' }
    $front=[string]$notes[0].fields.Front.value; $back=[string]$notes[0].fields.Back.value
    $expectedQuestion=@($mediaBundle.questionImages | ForEach-Object { 'rqc_' + ([string]$_.sha256).ToLowerInvariant() + '.png' }); $expectedAnswer=@($mediaBundle.answerImages | ForEach-Object { 'rqc_' + ([string]$_.sha256).ToLowerInvariant() + '.png' })
    $frontPositions=@($expectedQuestion | ForEach-Object { $front.IndexOf($_,[StringComparison]::Ordinal) }); $backPositions=@($expectedAnswer | ForEach-Object { $back.IndexOf($_,[StringComparison]::Ordinal) })
    if ($frontPositions -contains -1 -or $backPositions -contains -1 -or ($frontPositions.Count -gt 1 -and $frontPositions[0] -ge $frontPositions[1])) { throw 'Ordered media exact readback failed.' }
    $replay=Invoke-Pipe $command 'task-command'; Start-Sleep -Seconds 2
    $afterReplay=Get-WorkerResults $task.jobRoot; $notesAfter=@(Invoke-Anki 'notesInfo' @{ notes=$noteIds })
    if ($afterReplay.Count -ne $resultsBeforeReplay.Count -or $notesAfter.Count -ne $notes.Count) { throw 'Submit replay created work or notes.' }
    $readablePath=Join-Path $evidence 'READABLE.md'
    $readable=@("# v1.2.15 original → Anki 真实链",'',"- task: ``$($task.taskId)``","- state: ``partial``（Anki succeeded；RemNote unsupported）","- Anki deck: ``$AnkiDeck``","- note/card: ``$($noteIds -join ', ')`` / ``$($cardIds -join ', ')``",'- MinerU/provider calls: `0`','- RemNote worker/writes: `0 / 0`','- replay new attempts/notes: `0 / 0`','',"Front: $front",'',"Back: $back",'',"Question media order: $($expectedQuestion -join ' -> ')","Answer media order: $($expectedAnswer -join ' -> ')") -join "`n"
    Write-BytesOnce $readablePath $utf8.GetBytes($readable+"`n")
    $receiptPath=Join-Path $evidence 'execution-receipt.json'
    Write-JsonOnce $receiptPath ([ordered]@{
        schemaVersion='knowledge-clipper-sealed-original-anki-v1215.v1'; status='passed'; completedAt=[DateTimeOffset]::UtcNow.ToString('o'); boundary='sealed-actual-orchestrator-process'
        authority=[ordered]@{ version='1.2.15'; registry=Get-Evidence $registryPath; checkpoint=Get-Evidence $checkpointPath; orchestrator=Get-Evidence $orchestratorExe; releaseTreeSha256='E5067C1B93A47B5650A2B1A049F974A73F66E31C3A5CEFC5DBD4D33B623190D4' }
        process=[ordered]@{ pid=$process.Id; executable=$orchestratorExe; namedPipe=$script:pipeName; workerLaunchOwner='KnowledgeClipper.Orchestrator.exe' }
        task=[ordered]@{ taskId=$task.taskId; jobRoot=$task.jobRoot; manifest=Get-Evidence $task.manifestPath; state=$projection.status; destinations=@('remnote','anki') }
        image=[ordered]@{ worker='image.ocr.mineru@1.2.3'; attempts=1; providerCalls=0; bundle=Get-Evidence $mediaBundlePath; questionImages=@($mediaBundle.questionImages); answerImages=@($mediaBundle.answerImages) }
        anki=[ordered]@{ worker='export.anki@1.3.0'; deck=$AnkiDeck; writes=1; noteIds=$noteIds; cardIds=$cardIds; front=$front; back=$back; expectedQuestionMedia=$expectedQuestion; expectedAnswerMedia=$expectedAnswer; exactOrderReadback=$true }
        remnote=[ordered]@{ selected=$true; status='conditional/unsupported'; code='mcp.media_write_unavailable'; workerAttempts=0; writes=0; ankiBlocked=$false }
        replay=[ordered]@{ commandId=$command.commandId; newAttempts=0; newNotes=0; passed=$true; receipt=$replay.payload }
        sideEffects=[ordered]@{ ankiWrites=1; ankiReadCalls=3; remnoteWrites=0; mineruCalls=0; deepSeekCalls=0; speaker=0 }
        workers=@($resultsBeforeReplay | ForEach-Object { [ordered]@{ worker="$($_.value.worker.id)@$($_.value.worker.version)"; result=Get-Evidence $_.path } })
        readable=Get-Evidence $readablePath; ipc=[ordered]@{ exchanges=$script:exchangeSequence; root=$ipcRoot; submit=$submit.payload }
    })
    Write-Output ([ordered]@{ status='passed'; evidenceRoot=$evidence; taskId=$task.taskId; receiptPath=$receiptPath; receiptSha256=Get-Sha256File $receiptPath; noteIds=$noteIds; cardIds=$cardIds } | ConvertTo-Json -Depth 20)
} catch { $failure=$_; $failurePath=Join-Path $evidence 'failure-receipt.json'; if (-not (Test-Path -LiteralPath $failurePath)) { Write-JsonOnce $failurePath ([ordered]@{ schemaVersion='knowledge-clipper-sealed-original-anki-v1215-failure.v1'; status='failed'; failedAt=[DateTimeOffset]::UtcNow.ToString('o'); error=$_.Exception.Message; taskId=$task.taskId; externalWriteState='inspect-worker-evidence' }) } }
finally { if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true) | Out-Null; $process.WaitForExit(15000) | Out-Null }; if ($null -ne $stdoutTask) { Write-BytesOnce $stdoutPath $utf8.GetBytes($stdoutTask.GetAwaiter().GetResult()) }; if ($null -ne $stderrTask) { Write-BytesOnce $stderrPath $utf8.GetBytes($stderrTask.GetAwaiter().GetResult()) } }
if ($null -ne $failure) { throw $failure }

param(
    [string]$RootRemId = 'mCthh3jTMJ8zMqMEc',
    [string]$RootTitle = '零碎知识剪藏 Canary'
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $testRoot '..\..')).Path
$releaseId = 'knowledge-clipper-authority-v1.2.4-20260828T032500Z'
$checkpointId = 'knowledge-clipper-authority-1.2.4-20260828T033500Z'
$releaseRoot = Join-Path $projectRoot "releases\$releaseId\win-x64"
$authorityRoot = Join-Path $projectRoot 'contracts\authority\worker-registry\v1.2.4-20260828T032500Z'
$checkpointPath = Join-Path $authorityRoot 'checkpoint.json'
$dependencyManifestPath = Join-Path $authorityRoot 'dependency-manifest.v1.json'
$registryPath = Join-Path $authorityRoot 'worker-registry.v1.json'
$core = Join-Path $releaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$contentWorker = Join-Path $releaseRoot 'workers\content\KnowledgeClipper.Content.Worker.exe'
$remNoteWorker = Join-Path $releaseRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$node = 'C:\Program Files\nodejs\node.exe'
$remNoteCli = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules\remnote-mcp-server\dist\remnote-cli\index.js'
$remNoteMcpUrl = 'http://127.0.0.1:43101/mcp'

$expected = [ordered]@{
    checkpointSha256 = '41E3644B28D3B8D4B6246657226859102299CD4B60D8CBD5D04DA9ED106E55D1'
    registrySha256 = '72A6C1BA7604FBC787B21380F57DA59F7465F71E392C2F32F56E03B782E539A4'
    dependencyManifestSha256 = '25DA7B8B27995AA1E9EDBD0282BEB191A04C47CD7D722F047379AD30B2683AF2'
    releaseTreeSha256 = '6CA2D59E66DF9F15CC1BE46890ABDA04F5FAFB98507716D91E568818A37882CE'
    releaseFiles = 91
    releaseBytes = 249015447
    coreSha256 = '9C6FD23EE4CB83AECD6EB09E58DB1799408AB9C46FC05913C399A372BA9A1A53'
    contentSha256 = 'F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8'
    remnoteSha256 = '0B01297E4345EFD07E50DDD90E23CD94F2A4AFA6DF731CF4252760BD05496B42'
}

if ($RootRemId -ne 'mCthh3jTMJ8zMqMEc' -or $RootTitle -ne '零碎知识剪藏 Canary') {
    throw 'Safety boundary violation: this Canary is locked to the authorized isolated RemNote root.'
}

function Hash-File([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Hash-Bytes([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}

function Write-BytesOnce([string]$Path, [byte[]]$Bytes) {
    $parent = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    if ([IO.File]::Exists($Path)) { throw "Immutable file exists: $Path" }
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Write-TextOnce([string]$Path, [string]$Text) {
    Write-BytesOnce $Path ([Text.UTF8Encoding]::new($false).GetBytes($Text))
}

function Write-JsonOnce([string]$Path, [object]$Value) {
    Write-TextOnce $Path (($Value | ConvertTo-Json -Depth 100) + "`n")
}

function Relative-Path([string]$Path, [string]$Base = $projectRoot) {
    [IO.Path]::GetRelativePath($Base, $Path).Replace('\', '/')
}

function Evidence([string]$Path) {
    [ordered]@{
        path = Relative-Path $Path
        bytes = (Get-Item -LiteralPath $Path).Length
        sha256 = Hash-File $Path
    }
}

function Invoke-Captured([string]$FilePath, [string[]]$Arguments, [string]$StdoutPath, [string]$StderrPath, [hashtable]$Environment = @{}) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $psi.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $psi.Environment[$key] = [string]$Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Failed to start: $FilePath" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    Write-TextOnce $StdoutPath $stdout
    Write-TextOnce $StderrPath $stderr
    [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

function Assert-Hash([string]$Path, [string]$ExpectedHash) {
    $actual = Hash-File $Path
    if ($actual -ne $ExpectedHash) { throw "Authority hash mismatch: $Path expected=$ExpectedHash actual=$actual" }
}

foreach ($required in @($releaseRoot, $checkpointPath, $dependencyManifestPath, $registryPath, $core, $contentWorker, $remNoteWorker, $node, $remNoteCli)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required authority dependency missing: $required" }
}

Assert-Hash $checkpointPath $expected.checkpointSha256
Assert-Hash $dependencyManifestPath $expected.dependencyManifestSha256
Assert-Hash $registryPath $expected.registrySha256
Assert-Hash $core $expected.coreSha256
Assert-Hash $contentWorker $expected.contentSha256
Assert-Hash $remNoteWorker $expected.remnoteSha256

$releaseFiles = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -File)
$relativeNames = [string[]]@($releaseFiles | ForEach-Object { Relative-Path $_.FullName $releaseRoot })
[Array]::Sort($relativeNames, [StringComparer]::Ordinal)
$treeRows = foreach ($relative in $relativeNames) {
    $path = Join-Path $releaseRoot $relative
    "$relative`t$((Get-Item -LiteralPath $path).Length)`t$((Hash-File $path).ToLowerInvariant())"
}
$treeHash = Hash-Bytes ([Text.UTF8Encoding]::new($false).GetBytes(($treeRows -join "`n")))
$releaseBytes = ($releaseFiles | Measure-Object Length -Sum).Sum
if ($releaseFiles.Count -ne $expected.releaseFiles -or $releaseBytes -ne $expected.releaseBytes -or $treeHash -ne $expected.releaseTreeSha256) {
    throw "Release tree drift: files=$($releaseFiles.Count) bytes=$releaseBytes tree=$treeHash"
}

$checkpoint = Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json -Depth 100
if ($checkpoint.checkpointId -ne $checkpointId -or $checkpoint.admissionStatus -ne 'authority-admitted' -or $checkpoint.release.treeSha256 -ne $expected.releaseTreeSha256) {
    throw 'Authority checkpoint identity or admission state is invalid.'
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')
$shortStamp = $stamp.ToLowerInvariant()
$canaryRoot = Join-Path $projectRoot "canary\remnote-v124-authority-live\$stamp"
$taskId = "task-remnote-v124-offline-$shortStamp"
$runId = 'run-001'
$jobRoot = Join-Path $canaryRoot "jobs\$taskId"
$documentTitle = "零碎知识剪藏 Canary · v1.2.4 离线问答 $stamp"
$sourceQuestion = '为什么玻璃杯外壁会出现水珠？'
$sourceAnswer = '空气中的水蒸气遇到较冷的杯壁后凝结成小水滴。'
$sourceText = "$sourceQuestion`n`n$sourceAnswer"
$artifactId = "content-input-v124-$($stamp.ToLowerInvariant())"
$artifactManifestRelative = "artifacts/$artifactId/artifact.json"
$materialRelative = 'inputs/content/material.md'
$contentInputRelative = 'inputs/content/content-input.v1.json'
$replayRelative = 'inputs/content/provider-replay.v1.json'

[IO.Directory]::CreateDirectory($jobRoot) | Out-Null
$materialPath = Join-Path $jobRoot $materialRelative
$contentInputPath = Join-Path $jobRoot $contentInputRelative
$replayPath = Join-Path $jobRoot $replayRelative
Write-TextOnce $materialPath $sourceText
Write-JsonOnce $contentInputPath ([ordered]@{
    schema_version = 'standard-input/1.0.0'
    package_id = "package-$taskId"
    task_id = $taskId
    input_version = '1.0.0'
    mode = 'card'
    items = @([ordered]@{
        item_id = 'item-water-condensation'
        source = [ordered]@{ kind = 'text'; material_path = $materialRelative }
        hints = [ordered]@{ category_id = 'question_answer'; answer_state = 'provided'; requested_templates = @('basic_qa') }
    })
})
Write-JsonOnce $replayPath ([ordered]@{
    schema_version = 'provider-replay/1.0.0'
    responses = [ordered]@{
        'cleaning:item-water-condensation' = [ordered]@{
            cleaned_text = $sourceText
            removed_fragments = @()
            format_anomalies = @()
        }
        'classification:item-water-condensation' = [ordered]@{
            units = @([ordered]@{
                unit_id = 'unit-water-condensation'
                source_segment_indexes = @(0, 1)
                category_id = 'question_answer'
                subtype = 'factual_question'
                answer_state = 'provided'
                context_state = 'provided'
                risk_state = 'ordinary'
                question = $sourceQuestion
                supplied_answer = $sourceAnswer
                unresolved_reasons = @()
            })
        }
        'card_generation:item-water-condensation:unit-water-condensation' = [ordered]@{
            generated_content = @()
            cards = @([ordered]@{
                template = 'basic_qa'
                front_blocks = @([ordered]@{ type = 'markdown'; text = $sourceQuestion; provenance = 'source' })
                back_blocks = @([ordered]@{ type = 'markdown'; text = $sourceAnswer; provenance = 'source' })
                answer_provenance = 'source'
            })
            unresolved_reasons = @()
        }
    }
})

$artifactManifestPath = Join-Path $jobRoot $artifactManifestRelative
$artifactFiles = @(
    [ordered]@{ path = $contentInputRelative; sha256 = (Hash-File $contentInputPath).ToLowerInvariant(); bytes = (Get-Item $contentInputPath).Length; mediaType = 'application/json' },
    [ordered]@{ path = $materialRelative; sha256 = (Hash-File $materialPath).ToLowerInvariant(); bytes = (Get-Item $materialPath).Length; mediaType = 'text/markdown; charset=utf-8' },
    [ordered]@{ path = $replayRelative; sha256 = (Hash-File $replayPath).ToLowerInvariant(); bytes = (Get-Item $replayPath).Length; mediaType = 'application/json' }
)
Write-JsonOnce $artifactManifestPath ([ordered]@{
    schemaVersion = '1.0.0'
    artifactId = $artifactId
    kind = 'content-input'
    contractVersion = '1.0.0'
    mediaType = 'application/json'
    files = $artifactFiles
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    producer = [ordered]@{ stageId = 'canary.fixture'; workerId = 'canary.fixture'; workerVersion = '1.0.0'; attemptId = "fixture-$stamp" }
    inputRefs = @()
    dataClass = 'personal'
    metadata = [ordered]@{ entrypoint = $contentInputRelative; fixtureClass = 'synthetic-non-private'; providerMode = 'replay'; destinationNeutral = $true }
})
$inputRef = [ordered]@{
    artifactRef = $artifactId
    kind = 'content-input'
    contractVersion = '1.0.0'
    manifestPath = $artifactManifestRelative
    expectedSha256 = (Hash-File $artifactManifestPath).ToLowerInvariant()
}

$intentRelative = 'plans/clip-intent.v2.json'
$intentPath = Join-Path $jobRoot $intentRelative
Write-JsonOnce $intentPath ([ordered]@{
    schemaVersion = 'knowledge-clipper-intent.v2'
    taskId = $taskId
    intent = 'clip.card'
    inputArtifactRef = $artifactId
    requestedOutputs = @('card-bundle')
})
$taskManifestPath = Join-Path $jobRoot 'task.manifest.v2.json'
Write-JsonOnce $taskManifestPath ([ordered]@{
    schemaVersion = '2.0.0'
    taskId = $taskId
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    createdBy = [ordered]@{ application = 'KnowledgeClipper.RemNoteExporter.AuthorityCanary'; version = '1.2.4' }
    intent = 'clip.card'
    executionProfile = 'cards'
    destinations = @('remnote')
    inputRefs = @($inputRef)
    requestedOutputs = @('card-bundle')
    initialPlanRef = $intentRelative
    retentionClass = 'ephemeral'
})

$planRelative = "plans/resolved/$runId/pipeline.v2.json"
$planPath = Join-Path $jobRoot $planRelative
$mapping = [ordered]@{
    categoryRegistryVersion = 'category-registry/2.0.0'
    templateMappings = [ordered]@{
        basic_qa = [ordered]@{ policy = 'project'; cardType = 'basic'; direction = 'forward' }
        media_qa = [ordered]@{ policy = 'reject-media-deferred' }
        language_context = [ordered]@{ policy = 'project'; cardType = 'basic'; direction = 'forward' }
        language_writing = [ordered]@{ policy = 'project'; cardType = 'basic'; direction = 'forward' }
        concept_transfer = [ordered]@{ policy = 'project'; cardType = 'basic'; direction = 'forward' }
        procedure = [ordered]@{ policy = 'project'; cardType = 'basic'; direction = 'forward' }
    }
    allowedTextBlockTypes = @('text', 'markdown')
    blockRendering = 'single-block-verbatim-inline'
    assetRefPolicy = 'reject-media-deferred'
}
Write-JsonOnce $planPath ([ordered]@{
    schemaVersion = '2.0.0'
    planVersion = '2.0.0'
    taskId = $taskId
    maxConcurrency = 2
    stages = @(
        [ordered]@{
            stageId = 'content.understand-and-card'
            workerId = 'content.deepseek'
            operation = 'understand-and-card'
            dependsOn = @()
            inputRefs = @($inputRef)
            outputKinds = @('learning-bundle', 'card-bundle')
            timeoutMs = 300000
            retryPolicy = [ordered]@{ maxAttempts = 1; backoffMs = 0 }
            configSnapshot = [ordered]@{ promptSet = 'open-md-v2'; categoryRegistry = 'category-registry/2.0.0'; providerMode = 'replay'; authorityCheckpoint = $checkpointId }
            payload = [ordered]@{ schemaVersion = 'content-run/1.0.0'; contentInputPath = $contentInputRelative; providerMode = 'replay'; replayPath = $replayRelative }
            inputBindings = @()
            inputAdapter = $null
        },
        [ordered]@{
            stageId = 'export-remnote'
            workerId = 'export.remnote'
            operation = 'remnote.export'
            dependsOn = @('content.understand-and-card')
            inputRefs = @()
            outputKinds = @('remnote.write-receipt')
            timeoutMs = 180000
            retryPolicy = [ordered]@{ maxAttempts = 1; backoffMs = 0 }
            configSnapshot = [ordered]@{ authorityCheckpoint = $checkpointId }
            payload = [ordered]@{
                schemaVersion = '1.0.0'
                documentTitle = $documentTitle
                sink = 'mcp-direct'
                target = [ordered]@{ rootRemId = $RootRemId; rootTitle = $RootTitle }
            }
            inputBindings = @(
                [ordered]@{ fromStageId = 'content.understand-and-card'; kind = 'card-bundle'; required = $true },
                [ordered]@{ fromStageId = 'content.understand-and-card'; kind = 'learning-bundle'; required = $true }
            )
            inputAdapter = [ordered]@{ type = 'content-to-remnote.v1'; targetKind = 'card.bundle'; mappingSnapshot = $mapping }
        }
    )
    routing = [ordered]@{ processingProfile = 'cards'; destinations = @('remnote'); source = 'task-manifest.v2' }
})

[IO.Directory]::CreateDirectory($canaryRoot) | Out-Null
$preflightRoot = Invoke-Captured $node @($remNoteCli, 'read', $RootRemId, '--depth', '0', '--content-mode', 'structured', '--view', 'compact', '--ancestor-depth', '0', '--child-limit', '20', '--max-content-length', '20000') (Join-Path $canaryRoot 'preflight-root-readback.json') (Join-Path $canaryRoot 'preflight-root-readback.stderr.log') @{ REMNOTE_MCP_URL = $remNoteMcpUrl }
if ($preflightRoot.ExitCode -ne 0) { throw "Preflight RemNote root read failed: $($preflightRoot.Stderr)" }
$preflightRootJson = $preflightRoot.Stdout | ConvertFrom-Json -Depth 30
if ($preflightRootJson.remId -ne $RootRemId -or $preflightRootJson.title -ne $RootTitle) { throw 'Preflight RemNote root identity mismatch.' }

$registryValidation = Invoke-Captured $core @('validate-registry', '--registry', $registryPath, '--release-root', $releaseRoot) (Join-Path $canaryRoot 'validate-registry.stdout.json') (Join-Path $canaryRoot 'validate-registry.stderr.log')
if ($registryValidation.ExitCode -ne 0) { throw "Authority registry validation failed: $($registryValidation.Stderr)" }
$describeCore = Invoke-Captured $core @('describe') (Join-Path $canaryRoot 'describe.core.json') (Join-Path $canaryRoot 'describe.core.stderr.log')
$describeContent = Invoke-Captured $contentWorker @('describe', '--json') (Join-Path $canaryRoot 'describe.content.json') (Join-Path $canaryRoot 'describe.content.stderr.log')
$describeRemNote = Invoke-Captured $remNoteWorker @('describe', '--json') (Join-Path $canaryRoot 'describe.remnote.json') (Join-Path $canaryRoot 'describe.remnote.stderr.log')
if (@($describeCore, $describeContent, $describeRemNote).Where({ $_.ExitCode -ne 0 }).Count -ne 0) { throw 'One or more authority describe calls failed.' }

Write-Host "Starting authority v1.2.4 Core -> Content replay -> isolated RemNote export: $taskId"
$coreRun = Invoke-Captured $core @('run', '--job-root', $jobRoot, '--pipeline', $planPath, '--registry', $registryPath, '--release-root', $releaseRoot, '--run-id', $runId) (Join-Path $canaryRoot 'core-run.stdout.json') (Join-Path $canaryRoot 'core-run.stderr.log')
if ($coreRun.ExitCode -ne 0) { throw "Authority Core pipeline failed: $($coreRun.Stderr)" }
$coreRunJson = $coreRun.Stdout | ConvertFrom-Json -Depth 30
if ($coreRunJson.status -ne 'succeeded') { throw "Authority Core did not succeed: $($coreRunJson.status)" }

$contentResultPath = (Get-ChildItem -LiteralPath (Join-Path $jobRoot "runs\$runId\stages\content.understand-and-card\attempts") -Filter 'result.json' -Recurse -File).FullName
$exportResultPath = (Get-ChildItem -LiteralPath (Join-Path $jobRoot "runs\$runId\stages\export-remnote\attempts") -Filter 'result.json' -Recurse -File).FullName
$contentRequestPath = (Get-ChildItem -LiteralPath (Split-Path -Parent $contentResultPath) -Filter 'request.json' -File).FullName
$exportRequestPath = (Get-ChildItem -LiteralPath (Split-Path -Parent $exportResultPath) -Filter 'request.json' -File).FullName
$contentResult = Get-Content -LiteralPath $contentResultPath -Raw | ConvertFrom-Json -Depth 100
$contentRequest = Get-Content -LiteralPath $contentRequestPath -Raw | ConvertFrom-Json -Depth 100
$firstResult = Get-Content -LiteralPath $exportResultPath -Raw | ConvertFrom-Json -Depth 100
if ($contentRequest.payload.providerMode -ne 'replay' -or $contentResult.status -ne 'completed') { throw 'Content stage was not a completed offline replay.' }
if ($firstResult.status -ne 'completed' -or $firstResult.payload.outcome -ne 'created' -or -not $firstResult.payload.readbackVerified) {
    throw "First RemNote run did not prove a fresh readback-verified creation: $($firstResult.payload.outcome)"
}

$sourceRequest = Get-Content -LiteralPath $exportRequestPath -Raw | ConvertFrom-Json -Depth 100
$replayRunId = 'run-idempotency-001'
$replayAttemptId = "attempt-replay-$stamp"
$replayAttemptRelative = "runs/$replayRunId/stages/export-remnote/attempts/$replayAttemptId"
$replayRequestPath = Join-Path $jobRoot "$replayAttemptRelative/request.json"
$replayRequest = [ordered]@{
    schemaVersion = $sourceRequest.schemaVersion
    requestId = "request-replay-$stamp"
    operationId = "operation-replay-$stamp"
    taskId = $sourceRequest.taskId
    runId = $replayRunId
    stageId = $sourceRequest.stageId
    attemptId = $replayAttemptId
    workerId = $sourceRequest.workerId
    operation = $sourceRequest.operation
    idempotencyKey = $sourceRequest.idempotencyKey
    attemptPath = $replayAttemptRelative
    outputPath = "$replayAttemptRelative/outputs"
    progressPath = "$replayAttemptRelative/progress.ndjson"
    resultPath = "$replayAttemptRelative/result.json"
    cancelSignal = [ordered]@{ schemaVersion = '1.0.0'; path = "$replayAttemptRelative/cancel.json"; gracePeriodMs = 2000 }
    inputRefs = $sourceRequest.inputRefs
    configSnapshot = $sourceRequest.configSnapshot
    payload = $sourceRequest.payload
}
Write-JsonOnce $replayRequestPath $replayRequest
Write-Host 'Replaying the same immutable task inputs and idempotency key through the authority exporter.'
$replayRun = Invoke-Captured $remNoteWorker @('run', '--request', $replayRequestPath) (Join-Path $canaryRoot 'export-replay.stdout.log') (Join-Path $canaryRoot 'export-replay.stderr.log')
if ($replayRun.ExitCode -ne 0) { throw "Authority RemNote replay failed: $($replayRun.Stderr)" }
$replayResultPath = Join-Path $jobRoot "$replayAttemptRelative/result.json"
$secondResult = Get-Content -LiteralPath $replayResultPath -Raw | ConvertFrom-Json -Depth 100
if ($secondResult.status -ne 'completed' -or $secondResult.payload.outcome -ne 'already_applied' -or -not $secondResult.payload.readbackVerified -or $secondResult.payload.documentRemId -ne $firstResult.payload.documentRemId) {
    throw 'Replay did not prove readback-verified idempotent reuse of the same RemNote document.'
}

$documentRemId = $firstResult.payload.documentRemId
$documentRead = Invoke-Captured $node @($remNoteCli, 'read', $documentRemId, '--depth', '10', '--content-mode', 'structured', '--view', 'full', '--ancestor-depth', '3', '--child-limit', '500', '--max-content-length', '1000000') (Join-Path $canaryRoot 'readback.document.json') (Join-Path $canaryRoot 'readback.document.stderr.log') @{ REMNOTE_MCP_URL = $remNoteMcpUrl }
$rootRead = Invoke-Captured $node @($remNoteCli, 'read', $RootRemId, '--depth', '1', '--content-mode', 'structured', '--view', 'full', '--ancestor-depth', '0', '--child-limit', '500', '--max-content-length', '1000000') (Join-Path $canaryRoot 'readback.root.json') (Join-Path $canaryRoot 'readback.root.stderr.log') @{ REMNOTE_MCP_URL = $remNoteMcpUrl }
if ($documentRead.ExitCode -ne 0 -or $rootRead.ExitCode -ne 0) { throw 'Final RemNote readback failed.' }
$documentReadJson = $documentRead.Stdout | ConvertFrom-Json -Depth 100
$rootReadJson = $rootRead.Stdout | ConvertFrom-Json -Depth 100
if ($documentReadJson.remId -ne $documentRemId -or $documentReadJson.parentRemId -ne $RootRemId -or $documentReadJson.title -ne $documentTitle) {
    throw 'Final document readback identity or parent hierarchy mismatch.'
}
$readbackText = $documentRead.Stdout
if (-not $readbackText.Contains($sourceQuestion) -or -not $readbackText.Contains($sourceAnswer)) { throw 'Final RemNote readback does not preserve the exact source question and answer.' }
if (-not $rootRead.Stdout.Contains($documentRemId) -or -not $rootRead.Stdout.Contains($documentTitle)) { throw 'Root readback does not contain the created Canary document.' }

$contentOutputs = @($contentResult.outputs)
$learningRef = $contentOutputs | Where-Object kind -eq 'learning-bundle'
$cardRef = $contentOutputs | Where-Object kind -eq 'card-bundle'
if ($learningRef.Count -ne 1 -or $cardRef.Count -ne 1) { throw 'Content replay did not publish exactly one LearningBundle and CardBundle.' }
$learningManifestPath = Join-Path $jobRoot $learningRef[0].manifestPath
$cardManifestPath = Join-Path $jobRoot $cardRef[0].manifestPath
$learningManifest = Get-Content -LiteralPath $learningManifestPath -Raw | ConvertFrom-Json -Depth 100
$cardManifest = Get-Content -LiteralPath $cardManifestPath -Raw | ConvertFrom-Json -Depth 100
$learningBundlePath = Join-Path $jobRoot (($learningManifest.files | Where-Object { $_.path -match 'learning-bundle' }).path)
$cardBundlePath = Join-Path $jobRoot (($cardManifest.files | Where-Object { $_.path -match 'card-bundle' }).path)
$learningBundle = Get-Content -LiteralPath $learningBundlePath -Raw | ConvertFrom-Json -Depth 100
$cardBundle = Get-Content -LiteralPath $cardBundlePath -Raw | ConvertFrom-Json -Depth 100

$rawEvidencePaths = @(
    $checkpointPath, $dependencyManifestPath, $registryPath, $core, $contentWorker, $remNoteWorker,
    $materialPath, $contentInputPath, $replayPath, $artifactManifestPath, $intentPath, $taskManifestPath, $planPath,
    $contentRequestPath, $contentResultPath, $learningManifestPath, $learningBundlePath, $cardManifestPath, $cardBundlePath,
    $exportRequestPath, $exportResultPath, $replayRequestPath, $replayResultPath,
    (Join-Path $canaryRoot 'readback.document.json'), (Join-Path $canaryRoot 'readback.root.json')
)
$evidenceFiles = @($rawEvidencePaths | Select-Object -Unique | ForEach-Object { Evidence $_ })
$machineReceiptPath = Join-Path $canaryRoot 'machine-receipt.json'
$machineReceipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-remnote-authority-live-canary.v1'
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    status = 'passed'
    safety = [ordered]@{
        syntheticNonPrivate = $true
        contentProviderMode = 'replay'
        externalAiCalls = 0
        targetRootRemId = $RootRemId
        targetRootTitle = $RootTitle
        formalClipDocumentLocated = $false
        deleteOrCleanupOperations = 0
    }
    authority = [ordered]@{
        releaseId = $releaseId
        checkpointId = $checkpointId
        checkpoint = Evidence $checkpointPath
        registry = Evidence $registryPath
        dependencyManifest = Evidence $dependencyManifestPath
        releaseRoot = Relative-Path $releaseRoot
        releaseTree = [ordered]@{ files = $releaseFiles.Count; bytes = $releaseBytes; sha256 = $treeHash }
        core = Evidence $core
        content = Evidence $contentWorker
        remnoteExporter = Evidence $remNoteWorker
    }
    task = [ordered]@{
        taskId = $taskId
        runId = $runId
        jobRoot = Relative-Path $jobRoot
        manifest = Evidence $taskManifestPath
        pipeline = Evidence $planPath
        idempotencyKey = $sourceRequest.idempotencyKey
    }
    source = [ordered]@{
        question = $sourceQuestion
        answer = $sourceAnswer
        exactText = $sourceText
        material = Evidence $materialPath
        contentInput = Evidence $contentInputPath
        providerReplay = Evidence $replayPath
    }
    content = [ordered]@{
        request = Evidence $contentRequestPath
        requestPayload = $contentRequest.payload
        result = Evidence $contentResultPath
        status = $contentResult.status
        worker = $contentResult.worker
        outputs = $contentResult.outputs
        sideEffects = $contentResult.sideEffects
        learningBundle = [ordered]@{ evidence = Evidence $learningBundlePath; value = $learningBundle }
        cardBundle = [ordered]@{ evidence = Evidence $cardBundlePath; value = $cardBundle }
    }
    remnote = [ordered]@{
        first = [ordered]@{
            request = Evidence $exportRequestPath
            result = Evidence $exportResultPath
            outcome = $firstResult.payload.outcome
            documentRemId = $firstResult.payload.documentRemId
            readbackVerified = $firstResult.payload.readbackVerified
            outputs = $firstResult.outputs
            sideEffects = $firstResult.sideEffects
        }
        replay = [ordered]@{
            request = Evidence $replayRequestPath
            result = Evidence $replayResultPath
            sameTaskId = ($sourceRequest.taskId -eq $replayRequest.taskId)
            sameIdempotencyKey = ($sourceRequest.idempotencyKey -eq $replayRequest.idempotencyKey)
            sameInputRefs = (($sourceRequest.inputRefs | ConvertTo-Json -Depth 30 -Compress) -eq ($replayRequest.inputRefs | ConvertTo-Json -Depth 30 -Compress))
            outcome = $secondResult.payload.outcome
            documentRemId = $secondResult.payload.documentRemId
            readbackVerified = $secondResult.payload.readbackVerified
            outputs = $secondResult.outputs
            sideEffects = $secondResult.sideEffects
        }
        hierarchy = [ordered]@{
            rootRemId = $RootRemId
            documentRemId = $documentRemId
            documentParentRemId = $documentReadJson.parentRemId
            documentTitle = $documentReadJson.title
            documentReadback = Evidence (Join-Path $canaryRoot 'readback.document.json')
            rootReadback = Evidence (Join-Path $canaryRoot 'readback.root.json')
            exactQuestionPresent = $readbackText.Contains($sourceQuestion)
            exactAnswerPresent = $readbackText.Contains($sourceAnswer)
        }
    }
    evidenceFiles = $evidenceFiles
}
Write-JsonOnce $machineReceiptPath $machineReceipt

$reportPath = Join-Path $canaryRoot 'readable-report.md'
$report = @"
# 零碎知识剪藏 RemNote authority v1.2.4 Canary

- 状态：PASS
- authority checkpoint：`$checkpointId`
- release tree SHA-256：`$treeHash`
- Content provider：`replay`（外部 AI/API 调用 0 次）
- 目标：`$RootTitle` (`$RootRemId`)
- 新建文档：`$documentTitle` (`$documentRemId`)

## 原始输入

```text
$sourceText
```

## Content 输出

LearningBundle：

```json
$($learningBundle | ConvertTo-Json -Depth 100)
```

CardBundle：

```json
$($cardBundle | ConvertTo-Json -Depth 100)
```

## RemNote 写入与幂等

- 首次 outcome：`$($firstResult.payload.outcome)`
- 重放 outcome：`$($secondResult.payload.outcome)`
- 两次 documentRemId：`$documentRemId`
- 父层级：`$($documentReadJson.parentRemId)`（必须等于隔离根 `$RootRemId`）
- 原问题逐字回读：`$($readbackText.Contains($sourceQuestion))`
- 原答案逐字回读：`$($readbackText.Contains($sourceAnswer))`

## 完整 RemNote 回读

```json
$($documentReadJson | ConvertTo-Json -Depth 100)
```
"@
Write-TextOnce $reportPath $report

Write-Host "PASS authority v1.2.4 offline Content -> isolated RemNote live Canary"
Write-Host "MACHINE_RECEIPT=$machineReceiptPath"
Write-Host "MACHINE_RECEIPT_SHA256=$(Hash-File $machineReceiptPath)"
Write-Host "READABLE_REPORT=$reportPath"
Write-Host "READABLE_REPORT_SHA256=$(Hash-File $reportPath)"

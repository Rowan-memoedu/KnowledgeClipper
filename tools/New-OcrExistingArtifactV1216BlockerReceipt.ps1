[CmdletBinding()]
param([string]$EvidenceRoot = '')

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repo ('canary\sealed-ocr-human-resume-v1.2.16\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
}
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $evidence) { throw "EVIDENCE_ROOT_EXISTS: $evidence" }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

function File-Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Write-TextOnce([string]$Path, [string]$Text) {
    if (Test-Path -LiteralPath $Path) { throw "FILE_EXISTS: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

$ocrRoot = Join-Path $repo 'canary\image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2'
$jobRoot = Join-Path $ocrRoot 'jobs\task-image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2'
$imageResultPath = Join-Path $jobRoot 'runs\run-image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2\stages\image-ocr\attempts\attempt-0001\result.json'
$handoffPath = Join-Path $ocrRoot 'content-consumer-evidence.v1.json'
$content09ResultPath = Join-Path $jobRoot 'runs\run-content-ocr-human-20260830T113540Z\stages\content.understand-and-card\attempts\attempt-content-ocr-human-user_correction-live-0001\result.json'
$enginePath = Join-Path $repo 'orchestrator\Core\OrchestratorEngine.cs'
$resolverPath = Join-Path $repo 'orchestrator\Core\StageInputResolver.cs'
$registryPath = Join-Path $repo 'contracts\authority\worker-registry\v1.2.16-20260830T125200Z\worker-registry.v1.json'
$checkpointPath = Join-Path $repo 'contracts\authority\worker-registry\v1.2.16-20260830T125200Z\checkpoint.json'
$imageExePath = Join-Path $repo 'releases\knowledge-clipper-authority-v1.2.16-20260830T125200Z\win-x64\workers\image-ocr\KnowledgeClipper.ImageOcr.Worker.exe'
foreach ($path in @($imageResultPath,$handoffPath,$content09ResultPath,$enginePath,$resolverPath,$registryPath,$checkpointPath,$imageExePath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "REQUIRED_EVIDENCE_MISSING: $path" }
}

$imageResult = Get-Content -Raw -LiteralPath $imageResultPath | ConvertFrom-Json -Depth 100
$handoff = Get-Content -Raw -LiteralPath $handoffPath | ConvertFrom-Json -Depth 100
$observedKinds = @($imageResult.outputs | ForEach-Object kind | Sort-Object -Unique)
$requiredKinds = @('image-ocr.markdown','image.processing.provenance','image.original')
if ($imageResult.worker.id -ne 'image.ocr.mineru' -or $imageResult.worker.version -ne '1.2.2' -or $imageResult.status -ne 'completed') {
    throw 'SOURCE_IMAGE_RESULT_IDENTITY_DRIFT'
}
if ($observedKinds -contains 'image.processing.provenance' -or $observedKinds -contains 'image.original') {
    throw 'SOURCE_UNEXPECTEDLY_HAS_V123_CONTINUATION_EVIDENCE'
}
if ((File-Evidence $handoffPath).sha256 -ne '2834a225c66e22072f4e49bd829ec1d803cf0a4052b462dd6dbeb018b3cb7d87' -or
    $handoff.markdown.sha256 -ne '1b43e9f82b60c3ca998a587fd5ac577aaa98e69c51e4a238347c27579649acaa') {
    throw 'OCR_HANDOFF_IDENTITY_DRIFT'
}
if ((File-Evidence $registryPath).sha256 -ne '42250cc251eea1bebe2cb4e072e19c73f7cf25417010b5c33c922755ea46e757' -or
    (File-Evidence $checkpointPath).sha256 -ne '48d6e25e967f73f1d0489df1a6ba8c8f93212b5fad1fad832c5c09a3d58ac046' -or
    (File-Evidence $imageExePath).sha256 -ne '61e10d6b89b6be29eb653d526fed9872adc9c3a376fffb1857af10f2ebb82c9a') {
    throw 'AUTHORITY_IMAGE_IDENTITY_DRIFT'
}

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-sealed-ocr-existing-artifact-blocker.v1'
    status = 'conditional-blocked'
    blockerCode = 'core.precomputed_ocr_import_not_admitted'
    recordedAt = [DateTimeOffset]::UtcNow
    requestedOutcome = 'existing real MinerU artifact -> unresolved -> waiting_input -> submit_correction -> Content 0.9 -> Anki 1.3.1'
    authority = [ordered]@{
        version = '1.2.16'
        registry = File-Evidence $registryPath
        checkpoint = File-Evidence $checkpointPath
        admittedImageWorker = [ordered]@{ id = 'image.ocr.mineru'; version = '1.2.3'; executable = File-Evidence $imageExePath }
    }
    sourceEvidence = [ordered]@{
        workerResult = File-Evidence $imageResultPath
        worker = [ordered]@{ id = $imageResult.worker.id; version = $imageResult.worker.version; status = $imageResult.status }
        taskId = $imageResult.taskId
        runId = $imageResult.runId
        attemptId = $imageResult.attemptId
        outputKinds = $observedKinds
        handoff = File-Evidence $handoffPath
        markdownPayloadSha256 = $handoff.markdown.sha256
        structuredPayloadSha256 = $handoff.structured.sha256
        content09HumanCorrectionModuleResult = File-Evidence $content09ResultPath
        content09Boundary = 'module-owned live result; not a sealed Core waiting_input/resume attempt'
    }
    coreAdmissionRequirements = [ordered]@{
        sameRunCompletedImageResult = $true
        exactOwner = 'image.ocr.mineru'
        requiredOutputKinds = $requiredKinds
        processingProvenance = [ordered]@{
            schemaVersion = 'image-processing-provenance.v1'
            defaultOcrRerun = $false
            originalArtifactHashMatch = $true
            successfulOutputHashMatch = $true
        }
        source = [ordered]@{
            engine = File-Evidence $enginePath
            resolver = File-Evidence $resolverPath
        }
    }
    mismatch = [ordered]@{
        sourceWorkerVersion = '1.2.2'
        admittedWorkerVersion = '1.2.3'
        missingOutputKinds = @('image.processing.provenance','image.original')
        crossRunImportContract = $null
        manualResultCopyAllowed = $false
        reason = 'Rewriting or copying the v1.2.2 result into a v1.2.16 run would falsify run/attempt ownership and still lack the hash-bound provenance required by Core.'
    }
    execution = [ordered]@{
        newTaskSubmitted = $false
        newWorkerAttempts = 0
        mineruRerun = 0
        mineruProviderCalls = 0
        contentCalls = 0
        exporterAttempts = 0
        ankiWrites = 0
        remnoteWrites = 0
        speakerCalls = 0
    }
    resolutionBoundary = 'Requires a future additive, authority-bound precomputed-OCR import/reuse contract or a newly authorized OCR rerun. Neither is allowed in this batch.'
}

$receiptPath = Join-Path $evidence 'blocker-receipt.json'
Write-TextOnce $receiptPath (($receipt | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
$readable = @"
# Existing OCR artifact sealed-resume boundary

- Status: `conditional-blocked`
- Code: `core.precomputed_ocr_import_not_admitted`
- Existing real result: `image.ocr.mineru@1.2.2`, completed, markdown + structured only.
- v1.2.16 requirement: one completed same-run Image result with markdown + `image.processing.provenance` + `image.original`, hash-bound and `defaultOcrRerun=false`.
- The existing Content 0.9 correction result is module-owned evidence, not a sealed Core waiting_input/resume run.
- This batch created no task or attempt, called no provider, wrote no destination, and did not replay MinerU.
- A manual copy/rewrite was rejected because it would falsify immutable run/attempt ownership.
"@
$readablePath = Join-Path $evidence 'READABLE.md'
Write-TextOnce $readablePath ($readable.Trim() + [Environment]::NewLine)
[ordered]@{ receipt = File-Evidence $receiptPath; readable = File-Evidence $readablePath; evidenceRoot = $evidence } | ConvertTo-Json -Depth 20

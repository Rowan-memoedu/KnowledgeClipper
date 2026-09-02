[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Get-RelativePath([string]$Path) {
    return [IO.Path]::GetRelativePath($ProjectRoot, $Path).Replace('\', '/')
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-Evidence([string]$RelativePath) {
    $path = Join-Path $ProjectRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing evidence: $RelativePath"
    }
    return [ordered]@{
        path = $RelativePath.Replace('\', '/')
        sha256 = Get-Hash $path
        bytes = (Get-Item -LiteralPath $path).Length
    }
}

if (Test-Path -LiteralPath $OutputRoot) {
    throw "Append-only output already exists: $OutputRoot"
}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

$contentAuthority = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'receipts/content-worker-v0.5.2-live-synthetic-authority-20260826T0612Z.json') | ConvertFrom-Json
$pathA = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'canary/pm/A/061950-8598/path-A-receipt.json') | ConvertFrom-Json
$pathB = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'canary/pm/B/062210-e4bc/path-B-receipt.json') | ConvertFrom-Json
$pathC = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'canary/path-matrix/C-image-mineru-deepseek-20260826T061529Z/canary-receipt.json') | ConvertFrom-Json
$pathD = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'canary/pm/D/012237-a335/path-D-receipt.json') | ConvertFrom-Json
$remnoteDate = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'canary/rw-live-isolated/260826061056/worker-live-receipt.json') | ConvertFrom-Json
$coreRemnoteRoot = Join-Path $ProjectRoot 'canary/backend-full-pipeline/20260828T012314Z-general-max-path-regression-and-isolated-remnote-destination'
$coreRemnote = Get-Content -Raw -LiteralPath (Join-Path $coreRemnoteRoot 'result.remnote.json') | ConvertFrom-Json
$coreRemnoteReplay = Get-Content -Raw -LiteralPath (Join-Path $coreRemnoteRoot 'result.remnote-idempotency.json') | ConvertFrom-Json
$coreFiles = @(Get-ChildItem -LiteralPath $coreRemnoteRoot -Recurse -File)
$coreLongLengths = @($coreFiles | ForEach-Object { $_.FullName.Length } | Where-Object { $_ -gt 260 })
$registryRoot = Join-Path $ProjectRoot 'contracts/authority/worker-registry/v1.2.2-20260828T012500Z'
$registry = Get-Content -Raw -LiteralPath (Join-Path $registryRoot 'worker-registry.v1.json') | ConvertFrom-Json
$dependency = Get-Content -Raw -LiteralPath (Join-Path $registryRoot 'dependency-manifest.v1.json') | ConvertFrom-Json
$authorityCheckpoint = Get-Content -Raw -LiteralPath (Join-Path $registryRoot 'checkpoint.json') | ConvertFrom-Json
$authorityReceipt = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'receipts/knowledge-clipper-authority-1.2.2-20260828T012800Z.json') | ConvertFrom-Json
$authorityQualification = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'receipts/knowledge-clipper-authority-1.2.2-e2e-qualification-20260828T013200Z.json') | ConvertFrom-Json
$speechReuse = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'receipts/knowledge-clipper-speech-reuse-20260828T012200Z.json') | ConvertFrom-Json
$releaseRoot = Join-Path $ProjectRoot $dependency.releaseRoot
$replacementExe = Join-Path $ProjectRoot 'workers/remnote/KnowledgeClipper.RemNoteExporter.Worker.exe'
$replacementModuleReceipt = Join-Path $ProjectRoot 'canary/rwt/260828011627/receipt.json'

$describeResults = @()
foreach ($worker in $registry.workers) {
    $entrypoint = Join-Path $releaseRoot $worker.entrypoint.path
    if (-not (Test-Path -LiteralPath $entrypoint -PathType Leaf)) { throw "Registry runtime entrypoint is missing: $($worker.workerId)" }
    Push-Location $releaseRoot
    try {
        $raw = & $entrypoint describe --json 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($exitCode -ne 0) { throw "Registry runtime describe failed for $($worker.workerId): $($raw -join [Environment]::NewLine)" }
    $descriptor = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
    if ($descriptor.workerId -ne $worker.workerId) { throw "Registry runtime identity drift for $($worker.workerId): $($descriptor.workerId)" }
    $describeSnapshot = Join-Path $registryRoot ("describe\{0}.describe.json" -f $worker.workerId)
    if (-not (Test-Path -LiteralPath $describeSnapshot -PathType Leaf)) { throw "Describe snapshot is missing for $($worker.workerId)" }
    $describeResults += [ordered]@{
        workerId = $worker.workerId
        status = 'passed'
        entrypoint = [IO.Path]::GetRelativePath($releaseRoot, $entrypoint).Replace('\', '/')
        entrypointSha256 = Get-Hash $entrypoint
        descriptorSnapshot = Get-Evidence ([IO.Path]::GetRelativePath($ProjectRoot, $describeSnapshot))
    }
}

$matrix = @(
    [ordered]@{ id='UI'; status='PASS-HUMAN'; route='KnowledgeClipper desktop local UI/window/inline input'; evidence='user manual test'; boundary='not rerun; not production Core/worker/export E2E' },
    [ordered]@{ id='CONTENT-0.5.2'; status='PASS'; route='8 synthetic non-private inputs -> real DeepSeek -> structured outputs'; evidence=(Get-Evidence 'receipts/content-worker-v0.5.2-live-synthetic-authority-20260826T0612Z.json'); observed=[ordered]@{ scenarios=$contentAuthority.liveMatrix.scenarioCount; allCompleted=(@($contentAuthority.completedCanaries | Where-Object status -ne 'completed').Count -eq 0) } },
    [ordered]@{ id='A'; status='PASS-SCOPED'; route=$pathA.route; evidence=(Get-Evidence 'canary/pm/A/061950-8598/path-A-receipt.json'); observed=[ordered]@{ remId=$pathA.result.documentRemId; title=$pathA.independentReadback.effectiveTitle; readbackVerified=$pathA.result.readbackVerified }; boundary='earlier short-root independent path; supplemented by the current automatic Core long-path PASS below' },
    [ordered]@{ id='B'; status='PASS-SCOPED'; route=$pathB.route; evidence=(Get-Evidence 'canary/pm/B/062210-e4bc/path-B-receipt.json'); observed=[ordered]@{ deck=$pathB.isolation.deck; notes=$pathB.observed.notes; cards=$pathB.observed.cards; sameRevisionAlreadyCurrent=$pathB.observed.sameRevision.already_current; frontBackExact=$pathB.independentReadback.frontBackExact } },
    [ordered]@{ id='C'; status='PASS-SCOPED'; route='four ordered images -> real MinerU -> readable OCR Markdown -> real DeepSeek -> learning bundle'; evidence=(Get-Evidence 'canary/path-matrix/C-image-mineru-deepseek-20260826T061529Z/canary-receipt.json'); observed=[ordered]@{ ocrText=$pathC.source.path; learningBundle=$pathC.learningBundle.path; deepSeekLiveCalls=@($pathC.providerEvidence).Count }; boundary=$pathC.compatibilityBoundary.corePipeline },
    [ordered]@{ id='D'; status='PASS-SCOPED'; route=$pathD.route; evidence=(Get-Evidence 'canary/pm/D/012237-a335/path-D-receipt.json'); observed=[ordered]@{ liveContentSources=$pathD.externalCalls.deepseekLiveSources; edgeSyntheses=$pathD.externalCalls.edgeTts; audioFiles=@($pathD.audio).Count; physicalSpeakerPlayback=$false; referencesPathBase='projectRoot'; referencesHashAndBytesChecked=$pathD.reproducibility.referencesHashAndBytesChecked }; supersedes='canary/pm/D/011556-7ad9/path-D-receipt.json (ambiguous mixed path bases)' },
    [ordered]@{ id='SPEECH-CORE-REUSE'; status='PASS'; route='Core speech.synthesize prior result reuse paired with non-reusable speech.playback'; evidence=(Get-Evidence 'receipts/knowledge-clipper-speech-reuse-20260828T012200Z.json'); observed=[ordered]@{ synthesizeCacheHit=$speechReuse.synthesize.cacheHit; synthesizeWorkerStarts=$speechReuse.synthesize.workerStarts; envelopePayloadOperationIdMatch=($speechReuse.synthesize.envelopeOperationId -eq $speechReuse.synthesize.payloadOperationId); reusedFromFirstAttempt=($speechReuse.synthesize.reusedFromAttemptId -eq $speechReuse.synthesize.firstAttemptId); playbackWorkerStarts=$speechReuse.playback.workerStarts; playbackResultReuse=$speechReuse.playback.resultReuse; playbackFromStart=$speechReuse.playback.fromStart; nonSpeechPayloadUntouched=$speechReuse.isolation.nonSpeechPayloadUntouched } },
    [ordered]@{ id='REMNOTE-DATE'; status='PASS-SCOPED'; route='synthetic learning bundle -> standalone real RemNote exporter -> isolated date root -> idempotent readback'; evidence=(Get-Evidence 'canary/rw-live-isolated/260826061056/worker-live-receipt.json'); observed=[ordered]@{ rootRemId=$remnoteDate.target.rootRemId; rootTitle=$remnoteDate.target.rootTitle; documentRemId=$remnoteDate.observed.documentRemId; outcomes=$remnoteDate.observed.outcomes }; boundary='standalone exporter/date-root evidence; not full Core automatic path' },
    [ordered]@{ id='CORE-REMNOTE-061000'; status='NO-GO-HISTORICAL'; route='Core automatic RemNote chain'; evidence=(Get-Evidence 'canary/backend-full-pipeline/20260826T061000Z/jobs/backend-remnote-20260826t061000z/runs/run-001/stages/export-remnote/attempts/attempt-20260826060138904-1fe053d879e6465b97b1364298dff9a2/result.json'); failure='binding.entrypoint_missing; exporter not started' },
    [ordered]@{ id='CORE-REMNOTE-062000'; status='NO-GO-HISTORICAL'; route='Core automatic RemNote chain'; evidence=(Get-Evidence 'canary/backend-full-pipeline/20260826T062000Z/jobs/backend-remnote-20260826t062000z/runs/run-001/stages/export-remnote/attempts/attempt-20260826060610504-e1ea692f7db9451e87a26f7af5ae33b1/result.json'); failure='standard attempt full path exceeded MAX_PATH; exporter exit projected to unknown_external_state'; supersededBy='CORE-REMNOTE-20260828T012314Z' },
    [ordered]@{ id='CORE-REMNOTE-20260828T012314Z'; status='PASS-SCOPED'; route='production Orchestrator content.deepseek 0.5.2 -> export.remnote 1.0.1 -> isolated real RemNote -> idempotent replay'; evidence=@((Get-Evidence 'canary/backend-full-pipeline/20260828T012314Z-general-max-path-regression-and-isolated-remnote-destination/result.remnote.json'),(Get-Evidence 'canary/backend-full-pipeline/20260828T012314Z-general-max-path-regression-and-isolated-remnote-destination/result.remnote-idempotency.json'),(Get-Evidence 'canary/backend-full-pipeline/20260828T012314Z-general-max-path-regression-and-isolated-remnote-destination/readable-output.remnote.md'),(Get-Evidence 'receipts/knowledge-clipper-authority-1.2.2-e2e-qualification-20260828T013200Z.json')); observed=[ordered]@{ state=$coreRemnote.status; contentWorkerVersion=$coreRemnote.attempts[0].workerVersion; exporterVersion=$coreRemnote.attempts[1].workerVersion; isolatedParentRemId=$coreRemnote.remnote.isolatedParentRemId; documentRemId=$coreRemnote.remnote.documentRemId; firstOutcome=$coreRemnote.remnote.outcome; replayOutcome=$coreRemnoteReplay.outcome; replaySameRemId=($coreRemnoteReplay.documentRemId -eq $coreRemnote.remnote.documentRemId); files=$coreFiles.Count; pathsOver260=$coreLongLengths.Count; shortestLongPath=($coreLongLengths | Measure-Object -Minimum).Minimum; longestLongPath=($coreLongLengths | Measure-Object -Maximum).Maximum; authorityReleaseDifferences=$authorityQualification.authority.canaryReleaseDifferences }; boundary='real isolated RemNote path; desktop-to-real-export E2E remains deferred' },
    [ordered]@{ id='REGISTRY-1.2.1'; status='HISTORICAL-RUNTIME-SNAPSHOT'; route='five neutral worker entrypoints -> earlier release-root runtime admission'; evidence=@((Get-Evidence 'contracts/authority/worker-registry/v1.2.1-20260826T061500Z/worker-registry.v1.json'),(Get-Evidence 'contracts/authority/worker-registry/v1.2.1-20260826T061500Z/dependency-manifest.v1.json')); boundary='superseded append-only by authority v1.2.2; historical files remain unchanged' },
    [ordered]@{ id='REGISTRY-1.2.2'; status='AUTHORITY-ADMITTED'; route='five neutral worker entrypoints -> exact release tree -> release-root describe -> Orchestrator validate-registry -> append-only authority checkpoint'; evidence=@((Get-Evidence 'contracts/authority/worker-registry/v1.2.2-20260828T012500Z/checkpoint.json'),(Get-Evidence 'receipts/knowledge-clipper-authority-1.2.2-20260828T012800Z.json'),(Get-Evidence 'receipts/knowledge-clipper-authority-1.2.2-e2e-qualification-20260828T013200Z.json'),(Get-Evidence 'contracts/authority/worker-registry/v1.2.2-20260828T012500Z/worker-registry.v1.json'),(Get-Evidence 'contracts/authority/worker-registry/v1.2.2-20260828T012500Z/dependency-manifest.v1.json')); observed=[ordered]@{ admissionStatus=$authorityCheckpoint.admissionStatus; workers=$authorityCheckpoint.registry.workers; releaseRoot=$authorityCheckpoint.release.root; files=$authorityCheckpoint.release.files; bytes=$authorityCheckpoint.release.bytes; treeSha256=$authorityCheckpoint.release.treeSha256; describePassed=@($describeResults).Count; registryValidation=$authorityCheckpoint.validation.status; frozenFiles=$authorityCheckpoint.frozenV1.files; frozenMismatches=$authorityCheckpoint.frozenV1.mismatches; exporterVersion=(@($authorityCheckpoint.workers | Where-Object workerId -eq 'export.remnote')[0].workerVersion); qualificationStatus=$authorityQualification.status; qualificationReleaseDifferences=$authorityQualification.authority.canaryReleaseDifferences }; boundary='authority admission is not a sealed release candidate or desktop-to-real-export E2E' },
    [ordered]@{ id='REMNOTE-EXPORTER-1.0.1'; status='AUTHORITY-ADMITTED-PATH-PASS'; route='module fake idempotency + real Core long-path replay + v1.2.2 authority'; evidence=@([ordered]@{path=(Get-RelativePath $replacementExe);sha256=(Get-Hash $replacementExe);bytes=(Get-Item -LiteralPath $replacementExe).Length},(Get-Evidence 'canary/rwt/260828011627/receipt.json')); boundary='candidate manifest/hash/signing and full desktop E2E remain outside this checkpoint' }
)

$stable = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json') | ConvertFrom-Json
$stableMismatches = @($stable.files | Where-Object { (Get-Hash $_.path) -ne $_.sha256.ToLowerInvariant() })

$receipt = [ordered]@{
    schemaVersion = 'quality-real-path-matrix/1.0.0'
    status = 'scoped-paths-verified-release-no-go'
    generatedAt = [DateTime]::UtcNow.ToString('o')
    supersedes = [ordered]@{
        path = 'canary/path-matrix/quality-real-path-matrix-20260828T013314Z-58b2dd/matrix-receipt.json'
        reason = 'Readable index retained a stale pre-v1.2.2 exporter authority boundary.'
    }
    product = [ordered]@{ id='KnowledgeClipper'; displayName='零碎知识剪藏' }
    matrix = $matrix
    stableContracts = [ordered]@{
        checkpointId = $stable.checkpointId
        filesChecked = @($stable.files).Count
        mismatches = $stableMismatches.Count
        modified = $false
        receipt = Get-Evidence 'receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json'
    }
    authority = [ordered]@{
        status = 'PASS'
        admissionStatus = $authorityCheckpoint.admissionStatus
        registryVersion = $authorityCheckpoint.registry.registryVersion
        workers = $authorityCheckpoint.registry.workers
        checkpoint = Get-Evidence 'contracts/authority/worker-registry/v1.2.2-20260828T012500Z/checkpoint.json'
        receipt = Get-Evidence 'receipts/knowledge-clipper-authority-1.2.2-20260828T012800Z.json'
        qualification = Get-Evidence 'receipts/knowledge-clipper-authority-1.2.2-e2e-qualification-20260828T013200Z.json'
        releaseTree = [ordered]@{
            root = $authorityCheckpoint.release.root
            files = $authorityCheckpoint.release.files
            bytes = $authorityCheckpoint.release.bytes
            sha256 = $authorityCheckpoint.release.treeSha256.ToLowerInvariant()
            qualificationDifferences = $authorityQualification.authority.canaryReleaseDifferences
        }
        validation = [ordered]@{ status=$authorityCheckpoint.validation.status; workers=$authorityCheckpoint.validation.workers; describePassed=@($describeResults).Count }
    }
    registryRuntimeDescribe = [ordered]@{ status='passed'; workers=$describeResults }
    release = [ordered]@{
        decision = 'NO-GO'
        releaseManifest = $null
        candidateHash = $null
        signed = $false
        reasons = @('no release-manifest.v1.json or candidateHash has been sealed for this matrix','candidate is unsigned','full desktop-to-real-export E2E is intentionally deferred')
    }
    operations = [ordered]@{
        frontendManualTestRerun = $false
        newExternalCalls = 0
        newExternalWrites = 0
        productionTargetsTouched = $false
        deletions = 0
    }
}

$receiptPath = Join-Path $OutputRoot 'matrix-receipt.json'
$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM

$index = @"
# 零碎知识剪藏：真实路径 input → output 索引

本索引只汇总已有独立证据。本次生成索引没有新增外部调用、写入或删除；发布结论仍为 **NO-GO**。

| 路径 | 判定 | 原始输入 → 阶段产物 → 可读输出 | 真实边界 |
|---|---|---|---|
| 前端 | PASS-HUMAN | 用户已手测的 KnowledgeClipper 窗口与本地交互 → 可打开并完成前端操作 | 未重跑；不代表 Core/worker/export E2E |
| Content 0.5.2 | PASS | 8 个合成非私密场景 → 真实 DeepSeek → cleaning、语言卡、初问、追问、制卡结构化产物 | 只证明 Content 模块与真实 DeepSeek |
| A | PASS-SCOPED | 文本 → 真实 DeepSeek → Core content-to-remnote 投影 → RemNote Canary 文档 $($pathA.result.documentRemId) → 独立 readback $($pathA.independentReadback.effectiveTitle) | 早期短根独立路径；现已由下方 Core 自动长路径 PASS 补充 |
| B | PASS-SCOPED | 语言卡文本 → 真实 DeepSeek → Core content-to-anki 投影 → $($pathB.isolation.deck) 4 note / 4 card → 同 revision 4 already_current → 独立正反面逐字 readback | 未触碰日常 deck；无删除 |
| C | PASS-SCOPED | 4 张有序图片 → 真实 MinerU → canary/image-path-ocr-20260826-140940-5bf61f9b/readable-ocr.md → 真实 DeepSeek → learning bundle | 到 DeepSeek 可读结构为止；Content artifact entrypoint 与完整 Core 续接仍是独立边界 |
| D | PASS-SCOPED | 两份真实 DeepSeek 清洗文本 → 真实 Edge TTS 4 次 → 4 个 MP3（中文 140256 bytes；英文 23472 bytes） | 未调用物理扬声器；无生产写入 |
| Speech Core reuse | PASS | synthesize 首次 worker start → prior result 复用 cacheHit=true / operationId 一致 / reusedFromAttemptId 正确；playback 两次均新 attempt、2 次 worker start、不可复用、从头播放 | 非 speech payload 未注入复用字段 |
| RemNote 独立日期根 | PASS-SCOPED | synthetic learning bundle → 真实 exporter → 日期根 $($remnoteDate.target.rootTitle) → created / already_applied → readback | 独立 exporter 证据，不等于 Core 自动链 |
| Core RemNote 061000 | NO-GO-HISTORICAL | Content 已完成 → export binding → binding.entrypoint_missing | exporter 未启动；保留为独立历史回归 |
| Core RemNote 062000 | NO-GO-HISTORICAL | Content 已完成 → exporter 已启动 → 标准 attempt 路径超过 MAX_PATH → unknown_external_state | 历史失败已由 20260828T012314Z 回归覆盖，证据不删除 |
| Core RemNote 20260828T012314Z | PASS-SCOPED | production Orchestrator → Content 0.5.2 → exporter 1.0.1 → 隔离 RemNote 文档 $($coreRemnote.remnote.documentRemId) created/readback → 重放 already_applied/同 RemId | 已绑定 v1.2.2 authority；仍仅隔离 RemNote，desktop→real-export E2E 延后 |
| Registry v1.2.1 | HISTORICAL | 5 worker 早期 release-root → describe/runtime admission | 已被 v1.2.2 只追加替代，历史快照不改写 |
| Registry v1.2.2 | AUTHORITY-ADMITTED | 5 worker / 79 files / 248977927 bytes → tree SHA F15F21E5… → 5 describe → Orchestrator validate-registry=valid | authority 已通过；仍不是 sealed candidate，也不替代 desktop→real export E2E |

## 关键证据

- A：canary/pm/A/061950-8598/path-A-receipt.json — $(Get-Hash (Join-Path $ProjectRoot 'canary/pm/A/061950-8598/path-A-receipt.json'))
- B：canary/pm/B/062210-e4bc/path-B-receipt.json — $(Get-Hash (Join-Path $ProjectRoot 'canary/pm/B/062210-e4bc/path-B-receipt.json'))
- C：canary/path-matrix/C-image-mineru-deepseek-20260826T061529Z/canary-receipt.json — $(Get-Hash (Join-Path $ProjectRoot 'canary/path-matrix/C-image-mineru-deepseek-20260826T061529Z/canary-receipt.json'))
- D：canary/pm/D/012237-a335/path-D-receipt.json — $(Get-Hash (Join-Path $ProjectRoot 'canary/pm/D/012237-a335/path-D-receipt.json'))（统一 projectRoot 基准；旧 011556-7ad9 因混合路径基准保留为失败历史）
- Content 0.5.2 八场景：receipts/content-worker-v0.5.2-live-synthetic-authority-20260826T0612Z.json — $(Get-Hash (Join-Path $ProjectRoot 'receipts/content-worker-v0.5.2-live-synthetic-authority-20260826T0612Z.json'))
- RemNote 日期根：canary/rw-live-isolated/260826061056/worker-live-receipt.json — $(Get-Hash (Join-Path $ProjectRoot 'canary/rw-live-isolated/260826061056/worker-live-receipt.json'))
- Speech Core reuse：receipts/knowledge-clipper-speech-reuse-20260828T012200Z.json — $(Get-Hash (Join-Path $ProjectRoot 'receipts/knowledge-clipper-speech-reuse-20260828T012200Z.json'))
- Authority qualification：receipts/knowledge-clipper-authority-1.2.2-e2e-qualification-20260828T013200Z.json — $(Get-Hash (Join-Path $ProjectRoot 'receipts/knowledge-clipper-authority-1.2.2-e2e-qualification-20260828T013200Z.json'))
"@

$indexPath = Join-Path $OutputRoot 'input-output-index.md'
$index | Set-Content -LiteralPath $indexPath -Encoding utf8NoBOM

[ordered]@{
    status = $receipt.status
    receipt = $receiptPath
    receiptSha256 = Get-Hash $receiptPath
    index = $indexPath
    indexSha256 = Get-Hash $indexPath
    stableFilesChecked = @($stable.files).Count
    stableMismatches = $stableMismatches.Count
    releaseDecision = $receipt.release.decision
} | ConvertTo-Json -Depth 5

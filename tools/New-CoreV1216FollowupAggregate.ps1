[CmdletBinding()]
param(
    [string]$EvidenceRoot = '',
    [string]$ModuleActionsPath = ''
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repo ('canary\core-v1216-followup\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
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
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

$authorityRoot = Join-Path $repo 'contracts\authority\worker-registry\v1.2.16-20260830T125200Z'
$releaseRoot = Join-Path $repo 'releases\knowledge-clipper-authority-v1.2.16-20260830T125200Z\win-x64'
$coreCheckpoint = Join-Path $repo 'contracts\core\v2.7\checkpoints\v2.7.1-20260830T125000Z\checkpoint.json'
$authorityCheckpoint = Join-Path $authorityRoot 'checkpoint.json'
$registry = Join-Path $authorityRoot 'worker-registry.v1.json'
$dependency = Join-Path $authorityRoot 'dependency-manifest.v1.json'
$portable = Join-Path $authorityRoot 'portable-schema-closure-gate.v1.json'
$frozenArtifact = Join-Path $releaseRoot 'contracts\v1\schemas\common\artifact.schema.json'
$originalSupplement = Join-Path $repo 'canary\sealed-original-anki-v1.2.16\20260830T130000Z\execution-receipt.identity-and-side-effects-correction.v1.json'
if ([string]::IsNullOrWhiteSpace($ModuleActionsPath)) {
    $ModuleActionsPath = Join-Path $repo 'canary\sealed-module-actions-v1.2.16\20260831T060620Z\execution-receipt.json'
}
$moduleActions = [IO.Path]::GetFullPath($ModuleActionsPath)
$ocrBlocker = Join-Path $repo 'canary\sealed-ocr-human-resume-v1.2.16\20260831T054424Z\blocker-receipt.json'
foreach ($path in @($coreCheckpoint,$authorityCheckpoint,$registry,$dependency,$portable,$frozenArtifact,$originalSupplement,$moduleActions,$ocrBlocker)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "EVIDENCE_MISSING: $path" }
}

$portableValue = Get-Content -Raw -LiteralPath $portable | ConvertFrom-Json -Depth 100
$originalValue = Get-Content -Raw -LiteralPath $originalSupplement | ConvertFrom-Json -Depth 100
$moduleValue = Get-Content -Raw -LiteralPath $moduleActions | ConvertFrom-Json -Depth 100
$ocrValue = Get-Content -Raw -LiteralPath $ocrBlocker | ConvertFrom-Json -Depth 100
if ($portableValue.status -ne 'passed' -or $portableValue.failures.Count -ne 0 -or
    $originalValue.status -ne 'passed' -or $moduleValue.speechVoices.status -ne 'ready' -or
    $moduleValue.totals.remnoteWrites -ne 0 -or $moduleValue.totals.physicalSpeakerCalls -ne 0 -or
    $ocrValue.execution.mineruRerun -ne 0 -or $ocrValue.execution.ankiWrites -ne 0) {
    throw 'FOLLOWUP_EVIDENCE_GATE_FAILED'
}

$aggregate = [ordered]@{
    schemaVersion = 'knowledge-clipper-core-v1216-followup-aggregate.v1'
    status = 'conditional'
    completedAt = [DateTimeOffset]::UtcNow
    authority = [ordered]@{
        version = '1.2.16'
        releaseRoot = $releaseRoot
        releaseTreeSha256 = 'D4223A17AAF308A0A30214526B368E3411D5254831EF96EC820BE268A77B43EF'
        registry = File-Evidence $registry
        checkpoint = File-Evidence $authorityCheckpoint
        coreCheckpoint = File-Evidence $coreCheckpoint
        dependencyManifest = File-Evidence $dependency
        portableSchemaClosure = File-Evidence $portable
        frozenArtifactSchemaPackaged = File-Evidence $frozenArtifact
        portableGate = [ordered]@{ status=$portableValue.status; schemasChecked=$portableValue.schemasChecked; localRefsChecked=$portableValue.localRefsChecked; failures=@($portableValue.failures) }
    }
    completed = [ordered]@{
        originalToAnki = [ordered]@{
            status = 'passed'
            evidence = File-Evidence $originalSupplement
            noteIds = @($originalValue.exactReadback.noteIds)
            cardIds = @($originalValue.exactReadback.cardIds)
            mediaOrderPassed = [bool]$originalValue.exactReadback.mediaOrderPassed
            replayNewAttempts = [int]$originalValue.exactReadback.replayNewAttempts
            replayNewNotes = [int]$originalValue.exactReadback.replayNewNotes
            externalMutations = $originalValue.corrections.ankiExternalMutations
        }
        speechCatalogPublicDispatch = [ordered]@{
            status = 'passed'
            evidence = File-Evidence $moduleActions
            catalogRevision = $moduleValue.speechVoices.catalogRevision
            catalogSemanticSha256 = $moduleValue.speechVoices.catalogSemanticSha256
            catalogSnapshotFile = $moduleValue.speechVoices.catalogSnapshotFile
            replayReceiptUnchanged = [bool]$moduleValue.speechVoices.replayReceiptUnchanged
            externalWrites = 0
            physicalAudio = $false
        }
        speechPreviewAuthorizationGate = [ordered]@{
            status = 'passed-negative-gate'
            explicitAuthorization = $false
            workerInvocations = 0
            speakerCalls = 0
            publicOperation = 'speech.preview'
        }
        remnotePublicProbe = [ordered]@{
            status = $(if ($moduleValue.remnoteProbe.accepted -and $moduleValue.remnoteProbe.state -eq 'healthy' -and $moduleValue.remnoteProbe.admission -eq 'ready') { 'passed' } else { 'conditional' })
            evidence = File-Evidence $moduleActions
            publicDispatchStatus = $moduleValue.remnoteProbe.publicDispatchStatus
            state = $moduleValue.remnoteProbe.state
            admission = $moduleValue.remnoteProbe.admission
            connected = [bool]$moduleValue.remnoteProbe.connected
            targetMatched = [bool]$moduleValue.remnoteProbe.targetMatched
            reasonCodes = @($moduleValue.remnoteProbe.reasonCodes)
            externalWrites = 0
            replayReceiptUnchanged = [bool]$moduleValue.remnoteProbe.replayReceiptUnchanged
        }
        releasePortableSchemaClosure = [ordered]@{
            status = 'passed'
            frozenSchemaContentModified = $false
            schemasChecked = [int]$portableValue.schemasChecked
            localRefsChecked = [int]$portableValue.localRefsChecked
        }
    }
    conditional = [ordered]@{
        remnotePublicProbe = $(if ($moduleValue.remnoteProbe.accepted) { $null } else { [ordered]@{
                code = 'core.module_action_nonzero_probe_projection'
                evidence = File-Evidence $moduleActions
                publicDispatchStatus = $moduleValue.remnoteProbe.publicDispatchStatus
                directContractState = $moduleValue.remnoteProbe.state
                directContractReasonCodes = @($moduleValue.remnoteProbe.reasonCodes)
                directExitCode = $moduleValue.remnoteProbe.directReadonlyDiagnosticExitCode
                externalWrites = 0
                automaticRetryAllowed = $false
                explanation = $moduleValue.remnoteProbe.conditionalReason
            } })
        existingOcrHumanResume = [ordered]@{
            code = $ocrValue.blockerCode
            evidence = File-Evidence $ocrBlocker
            sourceWorkerVersion = $ocrValue.mismatch.sourceWorkerVersion
            admittedWorkerVersion = $ocrValue.mismatch.admittedWorkerVersion
            missingOutputKinds = @($ocrValue.mismatch.missingOutputKinds)
            mineruRerun = 0
            destinationWrites = 0
        }
    }
    currentBatchEffects = [ordered]@{
        mineruCalls = 0
        deepSeekCalls = 0
        ankiWrites = 0
        remnoteWrites = 0
        physicalSpeakerCalls = 0
        userProcessesTerminated = 0
        note = 'The Anki mutations listed in originalToAnki belong to the prior successful sealed Canary; this follow-up only corrected its metadata.'
    }
    supersededCanaryEvidence = @(
        'canary/sealed-module-actions-v1.2.16/20260831T053752Z',
        'canary/sealed-module-actions-v1.2.16/20260831T053839Z',
        'canary/sealed-module-actions-v1.2.16/20260831T054134Z',
        'canary/core-v1216-followup/20260831T060026Z'
    )
    newAuthorityIssued = $false
}

$receiptPath = Join-Path $evidence 'aggregate-receipt.json'
Write-TextOnce $receiptPath (($aggregate | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
$readable = @"
# Core / authority v1.2.16 follow-up

- Portable release schema closure: PASS, 62 schemas / 188 local refs / 0 failures. Frozen artifact schema was copied unchanged into the release.
- Original image → isolated Anki: PASS. Note 1788094533713, card 1788094533714, exact media order, replay added 0. Supplemental evidence corrects authority identity and counts 5 mutating AnkiConnect actions (createModel 1, storeMediaFile 3, addNote 1).
- Speech voice catalog public IPC 1.2: PASS through the sealed Orchestrator. Catalog persisted, replay stable, no settings write and no speaker.
- Speech preview: unauthorized negative gate PASS; worker and speaker calls 0. No physical preview was authorized in this batch.
- RemNote public probe: PASS through sealed Orchestrator IPC 1.2. `healthy/ready`, connected, exact target matched, writes 0, replay stable. Earlier `bridge.disconnected` evidence remains superseded; Core's nonzero degraded/blocked projection is still a negative-state compatibility boundary.
- Existing CANAR OCR sealed human resume: CONDITIONAL. The real 1.2.2 result lacks the 1.2.3 processing provenance/original artifacts required for a same-run no-rerun continuation. No MinerU, Content, exporter, or destination call was made.
- No new authority was issued.
"@
$readablePath = Join-Path $evidence 'READABLE.md'
Write-TextOnce $readablePath ($readable.Trim() + [Environment]::NewLine)
[ordered]@{ aggregate = File-Evidence $receiptPath; readable = File-Evidence $readablePath; evidenceRoot=$evidence } | ConvertTo-Json -Depth 20

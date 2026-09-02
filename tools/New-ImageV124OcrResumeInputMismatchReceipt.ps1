[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) { throw "Append-only evidence root exists: $EvidenceRoot" }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Evidence([string]$Relative) {
    $path = Join-Path $ProjectRoot $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence missing: $path" }
    $item = Get-Item -LiteralPath $path
    [ordered]@{ path=$Relative.Replace('\','/'); bytes=$item.Length; sha256=Sha $path }
}

$sourceRoot = 'canary/image-v124-core-ocr-20260831-142847-7957b072'
$receipt = Evidence "$sourceRoot/canary-receipt.json"
$result = Evidence "$sourceRoot/jobs/task-image-v124-core-ocr-20260831-142847-7957b072/runs/run-image-v124-core-ocr-20260831-142847-7957b072/stages/image-ocr/attempts/attempt-0001/result.json"
$handoff = Evidence "$sourceRoot/content-adapter-handoff.json"
$markdown = @(
    Evidence "$sourceRoot/jobs/task-image-v124-core-ocr-20260831-142847-7957b072/runs/run-image-v124-core-ocr-20260831-142847-7957b072/stages/image-ocr/attempts/attempt-0001/outputs/ocr.capture.image.question.0001.markdown/v0001/content.md"
    Evidence "$sourceRoot/jobs/task-image-v124-core-ocr-20260831-142847-7957b072/runs/run-image-v124-core-ocr-20260831-142847-7957b072/stages/image-ocr/attempts/attempt-0001/outputs/ocr.capture.image.question.0002.markdown/v0001/content.md"
    Evidence "$sourceRoot/jobs/task-image-v124-core-ocr-20260831-142847-7957b072/runs/run-image-v124-core-ocr-20260831-142847-7957b072/stages/image-ocr/attempts/attempt-0001/outputs/ocr.capture.image.answer.0001.markdown/v0001/content.md"
    Evidence "$sourceRoot/jobs/task-image-v124-core-ocr-20260831-142847-7957b072/runs/run-image-v124-core-ocr-20260831-142847-7957b072/stages/image-ocr/attempts/attempt-0001/outputs/ocr.capture.image.answer.0002.markdown/v0001/content.md"
)
$texts = @($markdown | ForEach-Object { Get-Content -LiteralPath (Join-Path $ProjectRoot $_.path) -Raw })
$standaloneCanar = @($texts | Where-Object { $_ -match 'CANAR(?!Y)' })
$sourceReceipt = Get-Content -LiteralPath (Join-Path $ProjectRoot $receipt.path) -Raw | ConvertFrom-Json -Depth 100
if ($receipt.sha256 -ne 'A1793972ADC9120FF9551B36F9880EC056D527667E1F98D3AC006E1F5E0D4F09' -or
    $result.sha256 -ne '4B8FA9F50DE6915CC06663C6334D313701E4DFBFC3E64C9C01E177EFF115582A' -or
    $handoff.sha256 -ne 'AE36778651F7871A523121B61080204E9B12B14C4AD25469119A6A1CF70B951F') { throw 'Delegated Image evidence identity drifted.' }
if ($standaloneCanar.Count -ne 0 -or $sourceReceipt.humanReviewRecommended -ne $false -or $sourceReceipt.status -ne 'completed') { throw 'Input no longer matches the observed ready/no-CANAR state.' }

$value = [ordered]@{
    schemaVersion='knowledge-clipper-sealed-ocr-human-resume-input-mismatch.v1'
    createdAt=[DateTimeOffset]::UtcNow.ToString('o')
    status='blocked-input-mismatch'
    requestedChain='Image immutable output -> Content unresolved -> waiting_input -> submit_correction CANAR to CANARY -> Content 0.9.0 -> isolated Anki'
    source=[ordered]@{ root=$sourceRoot; imageReceipt=$receipt; workerResult=$result; contentHandoff=$handoff; markdown=$markdown }
    observed=[ordered]@{
        sourceStatus=$sourceReceipt.status
        humanReviewRecommended=$sourceReceipt.humanReviewRecommended
        markdownItems=$texts.Count
        standaloneCanarOccurrences=0
        allMarkdownAlreadyContainsCanary=$true
        correctionDiffAvailable=$false
        expectedInitialProjection='ready-not-unresolved'
    }
    failClosedReason=[ordered]@{
        code='ocr.resume_source_not_unresolved'
        message='The delegated immutable Image 1.2.4 package already contains CANARY and explicitly does not recommend human review; it cannot truthfully produce the requested CANAR correction session.'
        prohibitedWorkaround='Do not edit immutable OCR artifacts, fabricate an unresolved result, or submit an empty/non-evidenced correction.'
    }
    authority=[ordered]@{
        version='1.2.17'
        registry=Evidence 'contracts/authority/worker-registry/v1.2.17-20260831T062800Z/worker-registry.v1.json'
        checkpoint=Evidence 'contracts/authority/worker-registry/v1.2.17-20260831T062800Z/checkpoint.json'
        orchestrator=Evidence 'releases/knowledge-clipper-authority-v1.2.17-20260831T062800Z/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe'
    }
    effects=[ordered]@{ orchestratorStarts=0; imageWorkerStarts=0; mineruSubmissions=0; deepSeekCalls=0; ankiCalls=0; ankiWrites=0; remnoteCalls=0; remnoteWrites=0; speakerInvocations=0 }
    requiredNextInput='Provide the actual immutable Image package whose published markdown contains standalone CANAR, whose processing provenance binds that markdown and original image, and whose Content 0.9 initial attempt legitimately projects unresolved/waiting_input.'
}
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$path = Join-Path $EvidenceRoot 'blocker-receipt.json'
[IO.File]::WriteAllText($path, (($value | ConvertTo-Json -Depth 100) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ receipt=$path; sha256=Sha $path; status=$value.status; effects=$value.effects } | ConvertTo-Json -Depth 20

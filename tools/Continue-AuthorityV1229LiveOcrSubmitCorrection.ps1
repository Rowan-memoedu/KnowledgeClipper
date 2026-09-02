[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Continue-AuthorityV1223LiveOcrAcceptOriginal.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.23-20260831T112000Z' = 'v1.2.29-20260901T023500Z'
    'AuthorityV1223' = 'AuthorityV1229'
    'v1223' = 'v1229'
    '1.2.23' = '1.2.29'
    'c342d7309f5c0ff0ecac5754dd38e62eef463664597b7cc399f1346de9f1201c' = '584b76d471374d96a6732afbf22d3fd2a08ffbb7ddec92c5d7733983747a9735'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) {
        throw "v1.2.29 correction runner marker missing: $($replacement.Key)"
    }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$pathMarker = '$ErrorActionPreference = ''Stop'''
if (-not $template.Contains($pathMarker, [StringComparison]::Ordinal)) { throw 'Correction runner absolute-path marker missing.' }
$template = $template.Replace($pathMarker, ($pathMarker + [Environment]::NewLine + '$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)'), [StringComparison]::Ordinal)
$continuationMarker = '$continuation = Join-Path $EvidenceRoot ''continuation'''
if (-not $template.Contains($continuationMarker, [StringComparison]::Ordinal)) { throw 'Correction runner root marker missing.' }
$template = $template.Replace($continuationMarker, '$continuation = Join-Path $EvidenceRoot ''continuation-user-correction''', [StringComparison]::Ordinal)
$actionMarker = @'
JsonOnce $actionPath ([ordered]@{
    schemaVersion='knowledge-clipper-ocr-human-resolution-action.v1'
    decisionKind='ocr-human-resolution'
    targetToken=$targetToken
    action='accept_original'
    reason='The source image itself visibly contains CANAR; preserve the source text without guessing or rerunning OCR.'
})
'@
$actionReplacement = @'
$candidateReference = $initial.projection.ocrHumanResolution.candidateTextRef
$candidateManifest = Get-Content -LiteralPath (Join-Path $job $candidateReference.manifestPath.Replace('/', [IO.Path]::DirectorySeparatorChar)) -Raw | ConvertFrom-Json -Depth 30
if (@($candidateManifest.files).Count -ne 1) { throw 'Projected OCR candidate must have exactly one immutable text payload.' }
$candidatePayload = Join-Path $job ([string]$candidateManifest.files[0].path).Replace('/', [IO.Path]::DirectorySeparatorChar)
$candidateText = [IO.File]::ReadAllText($candidatePayload, [Text.UTF8Encoding]::new($false, $true))
$start = [int]$initial.projection.ocrHumanResolution.reason[0].start
$end = [int]$initial.projection.ocrHumanResolution.reason[0].end
if ($start -lt 0 -or $end -le $start -or $end -gt $candidateText.Length -or $candidateText.Substring($start, $end - $start) -ne 'CANAR') {
    throw 'Projected CANAR correction range does not match immutable candidate text.'
}
$correctedText = $candidateText.Remove($start, $end - $start).Insert($start, 'CANARY')
$artifactId = "ocr.human-correction.$actionId"
$correctionPayloadRelative = "commands/$commandId/ocr-correction/payload.txt"
$correctionPayload = Join-Path $job $correctionPayloadRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
WriteOnce $correctionPayload $utf8.GetBytes($correctedText)
$correctionManifestRelative = "artifacts/$artifactId/artifact.json"
$correctionManifest = Join-Path $job $correctionManifestRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
JsonOnce $correctionManifest ([ordered]@{
    schemaVersion='1.0.0'; artifactId=$artifactId; kind='ocr.human-correction.text'; contractVersion='1.0.0'; mediaType='text/plain'
    files=@([ordered]@{path=$correctionPayloadRelative;sha256=Sha $correctionPayload;bytes=(Get-Item -LiteralPath $correctionPayload).Length;mediaType='text/plain'})
    createdAt=[DateTimeOffset]::UtcNow.ToString('o')
    producer=[ordered]@{stageId='desktop-ocr-human-resolution';workerId='desktop-capture';workerVersion='1.0.0';attemptId="attempt-$actionId"}
    inputRefs=@(); dataClass='sensitive'
})
$correctionReference=[ordered]@{artifactRef=$artifactId;kind='ocr.human-correction.text';contractVersion='1.0.0';manifestPath=$correctionManifestRelative;expectedSha256=Sha $correctionManifest}
JsonOnce $actionPath ([ordered]@{
    schemaVersion='knowledge-clipper-ocr-human-resolution-action.v1'
    decisionKind='ocr-human-resolution'
    targetToken=$targetToken
    action='submit_correction'
    reason='Synthetic source ground truth confirms CANAR must be corrected to CANARY.'
    correctionRef=$correctionReference
    diffs=@([ordered]@{segmentIndex=[int]$initial.projection.ocrHumanResolution.reason[0].segment_index;operation='substitution';start=$start;end=$end;before='CANAR';after='CANARY'})
})
'@
if (-not $template.Contains($actionMarker, [StringComparison]::Ordinal)) { throw 'Correction runner action marker missing.' }
$template = $template.Replace($actionMarker, $actionReplacement.TrimEnd(), [StringComparison]::Ordinal)
$template = $template.Replace('$actionId=''ocr-accept-original-v1229''', '$actionId=''ocr-submit-correction-v1229''', [StringComparison]::Ordinal)
$template = $template.Replace("decision='accept_original';targetToken=", "decision='submit_correction';targetToken=", [StringComparison]::Ordinal)
$template = $template.Replace('sourceTextPreserved=$true;ocrRerun=$false', 'sourceTextPreserved=$false;ocrRerun=$false', [StringComparison]::Ordinal)
$template = $template.Replace('接受原图文字（原图本身为 CANAR，不猜写、不重跑 OCR）', '用户校正 CANAR 为 CANARY；不重跑 OCR', [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.Continue-AuthorityV1229Correction.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "v1.2.29 correction continuation failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}

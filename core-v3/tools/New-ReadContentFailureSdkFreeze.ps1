[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$FakeReceipt,
    [string]$OutputRoot = 'D:\Artifacts\KnowledgeClipper\read-content-failure-sdk'
)
$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
$candidateRoot = [IO.Path]::GetFullPath($Candidate)
$receiptPath = [IO.Path]::GetFullPath($FakeReceipt)
$manifest = Join-Path $candidateRoot 'candidate-manifest.json'
$client = Join-Path $candidateRoot 'core-v3\KnowledgeClipper.Core.V3.Client.dll'
foreach ($path in @($manifest,$client,$receiptPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing freeze input: $path" } }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 100
if ($receipt.status -ne 'PASS' -or $receipt.contentFailure.result.pipeline -ne 'partially_failed' -or
    -not $receipt.contentFailure.result.attentionRequired -or $receipt.contentFailure.result.cardGeneration.reasonCode -ne 'content.gate-failed' -or
    $receipt.contentFailure.speechPlaybackAttempts -ne 1 -or $receipt.contentFailure.ankiAttempts -ne 0) {
    throw 'Content-failure SDK freeze requires a passing speech-independent failure gate.'
}
$id='read-content-failure-sdk-'+(Get-Date -Format 'yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$out=Join-Path $OutputRoot $id
if(Test-Path -LiteralPath $out){throw "Freeze root exists: $out"}
New-Item -ItemType Directory -Path $out|Out-Null
WriteJson (Join-Path $out 'sdk-contract.v1.json') ([ordered]@{
    schema='kc.read-content-failure-sdk-freeze/1'
    assembly=[ordered]@{path='candidate/core-v3/KnowledgeClipper.Core.V3.Client.dll';sha256=Hash $client}
    projection=[ordered]@{
        type='ReadAloudCardsProjectionSnapshot'
        pipeline='PartiallyFailed'
        playback='IdleReplayable'
        attentionRequired=$true
        cardGenerationFailure=[ordered]@{type='CardGenerationFailure';fields=@('status','reasonCode');allowedSafeReasonCodes=@('content.gate-failed','content.worker-failed')}
        cardDraft=$null
        ankiReadbackArtifact=$null
    }
    invariants=@('Speech synth/playback starts independently of Content','Content business failure never auto-retries','No card draft means no preview and no destination export','A new capture may be submitted as a new operation')
})
Set-Content -LiteralPath (Join-Path $out 'README.md') -Encoding utf8NoBOM -Value '# Read Content Failure SDK`n`nWhen Content card generation fails after Speech was accepted, render `PartiallyFailed` with `AttentionRequired=true` and `CardGenerationFailure.ReasonCode`. Do not show preview or confirm controls when `CardDraft` is null. Speech remains `IdleReplayable`; Anki is never invoked. A retry is a new user submission, not an automatic Content retry.'
$files=Get-ChildItem $out -File|Where-Object Name -ne 'freeze-manifest.json'|ForEach-Object{[ordered]@{path=$_.Name;bytes=$_.Length;sha256=Hash $_.FullName}}|Sort-Object path
WriteJson (Join-Path $out 'freeze-manifest.json') ([ordered]@{schema='kc.read-content-failure-sdk-freeze-manifest/1';freezeId=$id;createdAt=(Get-Date).ToUniversalTime().ToString('o');source=[ordered]@{candidate=$candidateRoot;candidateManifestSha256=Hash $manifest;fakeReceipt=$receiptPath;fakeReceiptSha256=Hash $receiptPath};files=$files})
Write-Output "SDK_FREEZE $out"
Write-Output "MANIFEST_SHA256 $(Hash (Join-Path $out 'freeze-manifest.json'))"

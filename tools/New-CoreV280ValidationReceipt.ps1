[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$target = Join-Path $ProjectRoot 'canary\core-v2.8.0-offline\20260901T053500Z\core-adapter-and-module-action.receipt.json'
if (Test-Path -LiteralPath $target) { throw "Append-only validation receipt already exists: $target" }

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function WriteJson([string]$Path, [object]$Value) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 64) + "`n"), [Text.UTF8Encoding]::new($false))
}

$contractsText = (& python (Join-Path $ProjectRoot 'tools\validate_contracts.py') 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Contract validation failed: $contractsText" }
$contracts = $contractsText | ConvertFrom-Json -Depth 40

$testExe = Join-Path $ProjectRoot 'tests\CoreAdapter.IntegrationTests\bin\Release\net8.0\CoreAdapter.IntegrationTests.exe'
$testText = (& $testExe $ProjectRoot 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) {
    # Preserve a bounded local failure transcript for the append-only checkpoint
    # audit.  This suite uses FakeWorker only, and raw stdout has no deployment
    # credentials or external-provider payloads.
    $failurePath = Join-Path (Split-Path -Parent $target) 'core-adapter.failure.txt'
    if (-not (Test-Path -LiteralPath $failurePath)) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $failurePath)) | Out-Null
        [IO.File]::WriteAllText($failurePath, $testText + "`n", [Text.UTF8Encoding]::new($false))
    }
    throw "CoreAdapter integration test failed: $testText"
}
$suite = $testText | ConvertFrom-Json -Depth 80
if ($suite.status -ne 'passed') { throw 'CoreAdapter integration suite did not report passed.' }
$original = @($suite.results | Where-Object name -eq 'original-media-anki-only' | Select-Object -First 1)
$discovery = @($suite.results | Where-Object name -eq 'remnote-target-discovery-v13' | Select-Object -First 1)
if ($original.Count -ne 1 -or -not $original[0].remnoteRejectedBeforeWorker -or $original[0].remnoteStarted) {
    throw 'Original-media Anki-only route gate failed.'
}
if ($discovery.Count -ne 1 -or -not $discovery[0].uniquePersistableBinding -or $discovery[0].ambiguousPersistableBinding -or $discovery[0].externalWrites -ne 0 -or $discovery[0].automaticRetryAllowed) {
    throw 'RemNote exact-title discovery gate failed.'
}

$remnoteLiveResult = Join-Path $ProjectRoot 'releases\knowledge-clipper-remnote-exporter-v2.3.0-20260901T043610Z\win-x64\evidence\live-result.json'
$remnoteLiveReceipt = Join-Path $ProjectRoot 'canary\remnote-exporter-v2.3-live-readonly-discovery\20260901T043431Z\receipt.json'
foreach ($required in @($remnoteLiveResult, $remnoteLiveReceipt)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing existing read-only evidence: $required" }
}
$live = Get-Content -LiteralPath $remnoteLiveResult -Raw | ConvertFrom-Json -Depth 40
if ($live.state -ne 'healthy' -or $live.admission -ne 'ready' -or $live.selection.resolution -ne 'unique' -or -not $live.selection.exactReadback -or -not $live.selection.persistableBinding -or [string]::IsNullOrWhiteSpace($live.selection.resolvedRootRemId) -or $live.externalWriteCount -ne 0 -or $live.fixedSemantics.automaticRetryAllowed -ne $false) {
    throw 'Existing RemNote release evidence is not a ready exact-title, no-write result.'
}

WriteJson $target ([ordered]@{
    schemaVersion = 'knowledge-clipper-core-v2.8-offline-receipt.v1'
    status = 'passed'
    generatedAt = '2026-09-01T05:35:00+08:00'
    externalCalls = 0
    externalWrites = 0
    physicalSpeakerInvocations = 0
    contracts = [ordered]@{ schemasChecked=$contracts.schemasChecked; validFixturesAccepted=$contracts.validFixturesAccepted; invalidFixturesRejected=$contracts.invalidFixturesRejected; frozenV1Modified=$contracts.frozenV1Modified }
    coreAdapter = [ordered]@{ groups=@($suite.results).Count; originalMediaAnkiOnly=$original[0]; remnoteTargetDiscovery=$discovery[0] }
    remnoteReadOnlyEvidence = [ordered]@{
        resultPath = [IO.Path]::GetRelativePath($ProjectRoot,$remnoteLiveResult).Replace('\','/')
        resultSha256 = Sha $remnoteLiveResult
        receiptPath = [IO.Path]::GetRelativePath($ProjectRoot,$remnoteLiveReceipt).Replace('\','/')
        receiptSha256 = Sha $remnoteLiveReceipt
        state = $live.state; admission=$live.admission; resolution=$live.selection.resolution; exactReadback=$live.selection.exactReadback; persistableBinding=$live.selection.persistableBinding; externalWrites=$live.externalWriteCount; automaticRetryAllowed=$live.fixedSemantics.automaticRetryAllowed
    }
})

[ordered]@{ status='passed'; path=[IO.Path]::GetRelativePath($ProjectRoot,$target).Replace('\','/'); sha256=Sha $target } | ConvertTo-Json -Depth 16

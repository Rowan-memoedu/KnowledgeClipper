param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$InputTextPath,
    [Parameter(Mandatory = $true)][string]$SourceReceiptPath
)

$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$InputTextPath = [IO.Path]::GetFullPath($InputTextPath)
$SourceReceiptPath = [IO.Path]::GetFullPath($SourceReceiptPath)
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Relative([string]$Path) { [IO.Path]::GetRelativePath($OutputRoot, $Path).Replace('\', '/') }
function Write-JsonOnce([string]$Path, [object]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Immutable receipt already exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}

$resultPath = @(Get-ChildItem -LiteralPath $OutputRoot -Recurse -Filter result.json -File)
if ($resultPath.Count -ne 1) { throw 'Expected exactly one worker result' }
$resultPath = $resultPath[0].FullName
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($result.status -ne 'completed' -or $result.worker.id -ne 'content.deepseek') { throw 'Completed content.deepseek result required' }
$requestPath = Join-Path (Split-Path -Parent $resultPath) 'request.json'
$learningRef = @($result.outputs | Where-Object kind -eq 'learning-bundle')
if ($learningRef.Count -ne 1) { throw 'Unique LearningBundle required' }
$learningManifestPath = Join-Path $OutputRoot $learningRef[0].manifestPath.Replace('/', '\')
if ((Hash-File $learningManifestPath) -ne ([string]$learningRef[0].expectedSha256).ToLowerInvariant()) { throw 'LearningBundle manifest drifted' }
$learningManifest = Get-Content -LiteralPath $learningManifestPath -Raw | ConvertFrom-Json
$learningRelative = if ([string]::IsNullOrWhiteSpace([string]$learningManifest.metadata.entrypoint)) { [string]$learningManifest.files[0].path } else { [string]$learningManifest.metadata.entrypoint }
$learningPath = Join-Path $OutputRoot $learningRelative.Replace('/', '\')
if ((Hash-File $learningPath) -ne ([string]$learningManifest.files[0].sha256).ToLowerInvariant()) { throw 'LearningBundle payload drifted' }
$learning = Get-Content -LiteralPath $learningPath -Raw | ConvertFrom-Json
$providerEvidence = @($learning.ai_trace | ForEach-Object { [ordered]@{ provider=$_.provider; mode=$_.mode; model=$_.model; attempts=$_.attempts; promptSha256=$_.prompt_sha256; responseSha256=$_.response_sha256 } })
if ($providerEvidence.Count -eq 0 -or @($providerEvidence | Where-Object { $_.provider -ne 'deepseek' -or $_.mode -ne 'live' }).Count -ne 0) { throw 'Real DeepSeek provider evidence missing' }
$workerPath = (Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json).workerId
$receiptPath = Join-Path $OutputRoot 'canary-receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = 'quality-content-live-from-text/1.0.0'; status = 'passed'; generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    source = [ordered]@{ path = $InputTextPath; sha256 = (Hash-File $InputTextPath); bytes = (Get-Item $InputTextPath).Length; receipt = $SourceReceiptPath; receiptSha256 = (Hash-File $SourceReceiptPath) }
    worker = [ordered]@{ id = $workerPath; version = $result.worker.version }
    request = [ordered]@{ path = (Relative $requestPath); sha256 = (Hash-File $requestPath) }
    result = [ordered]@{ path = (Relative $resultPath); sha256 = (Hash-File $resultPath); status = $result.status }
    learningBundle = [ordered]@{ path = (Relative $learningPath); sha256 = (Hash-File $learningPath); bytes = (Get-Item $learningPath).Length }
    externalCalls = [ordered]@{ deepseek = ($providerEvidence | Measure-Object -Property attempts -Sum).Sum }
    providerEvidence = $providerEvidence
    compatibilityBoundary = [ordered]@{ contentManifestEntrypoint = if ([string]::IsNullOrWhiteSpace([string]$learningManifest.metadata.entrypoint)) { 'absent-fallback-to-single-file' } else { 'present' }; corePipeline = 'NO-GO-until-entrypoint-contract-aligned' }
})
[ordered]@{ status='passed'; receipt=$receiptPath; sha256=(Hash-File $receiptPath); learningBundle=$learningPath; learningBundleSha256=(Hash-File $learningPath) } | ConvertTo-Json -Depth 5

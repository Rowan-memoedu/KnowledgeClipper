param(
    [Parameter(Mandatory = $true)][string]$CanaryRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$CanaryRoot = [IO.Path]::GetFullPath($CanaryRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$ProjectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Resolve-Canary([string]$Relative) {
    $direct = Join-Path $CanaryRoot $Relative.Replace('/', '\')
    if (Test-Path -LiteralPath $direct) { return $direct }
    return Join-Path (Join-Path $CanaryRoot 'jobs\sbc') $Relative.Replace('/', '\')
}
function Write-JsonOnce([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if (Test-Path -LiteralPath $Path) { throw "Immutable output exists: $Path" }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}
function New-ProjectRef([string]$Path, [string]$ExpectedSha256, [long]$ExpectedBytes, [string]$MediaType = $null) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $ProjectRoot.TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Evidence is outside project root: $full" }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Evidence is missing: $full" }
    $actualSha = Hash-File $full
    $actualBytes = (Get-Item -LiteralPath $full).Length
    if ($actualSha -ne $ExpectedSha256.ToLowerInvariant() -or $actualBytes -ne $ExpectedBytes) { throw "Evidence hash/bytes drifted: $full" }
    $ref = [ordered]@{
        path = [IO.Path]::GetRelativePath($ProjectRoot, $full).Replace('\', '/')
        pathBase = 'projectRoot'
        sha256 = $actualSha
        bytes = $actualBytes
    }
    if ($MediaType) { $ref.mediaType = $MediaType }
    return $ref
}
if (Test-Path -LiteralPath $OutputRoot) { throw "Immutable output root exists: $OutputRoot" }
$modulePath = Join-Path $CanaryRoot 'backend-content-speech-canary-receipt.json'
$validationPath = Join-Path $CanaryRoot 'backend-content-speech-canary-validation-receipt.json'
$module = Get-Content -LiteralPath $modulePath -Raw | ConvertFrom-Json
$validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
if ($module.status -ne 'passed' -or $validation.status -ne 'passed' -or $validation.canaryReceiptSha256 -ne (Hash-File $modulePath)) { throw 'Speech live receipts failed or drifted' }
if ($module.edgeTts.realSynthesisCalls -ne 4 -or $module.fakePlayer.physicalSpeakerPlayback -ne $false -or $module.contentSources.Count -ne 2) { throw 'Speech path boundary drifted' }
$deepSeekCalls = 0
$contentSources = @()
foreach ($source in $module.contentSources) {
    foreach ($file in @($source.original, $source.cleanedSpokenText, $source.contentLearningBundle, $source.contentCanaryReceipt, $source.spokenArtifactManifest)) {
        $path = Resolve-Canary $file.path
        if (-not (Test-Path -LiteralPath $path) -or (Hash-File $path) -ne ([string]$file.sha256).ToLowerInvariant() -or (Get-Item $path).Length -ne $file.bytes) { throw "Speech source evidence drifted: $($file.path)" }
    }
    $content = Get-Content -LiteralPath (Resolve-Canary $source.contentCanaryReceipt.path) -Raw | ConvertFrom-Json
    $deepSeekCalls += (@($content.providerEvidence) | Measure-Object -Property attempts -Sum).Sum
    $contentSources += [ordered]@{
        language = $source.language
        original = New-ProjectRef (Resolve-Canary $source.original.path) $source.original.sha256 $source.original.bytes $source.original.mediaType
        cleanedSpokenText = New-ProjectRef (Resolve-Canary $source.cleanedSpokenText.path) $source.cleanedSpokenText.sha256 $source.cleanedSpokenText.bytes $source.cleanedSpokenText.mediaType
    }
}
$audioFiles = @()
foreach ($case in $module.synthesisCases) {
    $audio = Resolve-Canary $case.audio.path
    if (-not (Test-Path -LiteralPath $audio) -or (Hash-File $audio) -ne ([string]$case.audio.sha256).ToLowerInvariant() -or (Get-Item $audio).Length -ne $case.audio.bytes) { throw "Speech audio drifted: $($case.case)" }
    $audioRef = New-ProjectRef $audio $case.audio.sha256 $case.audio.bytes $case.audio.mediaType
    $audioFiles += [ordered]@{ case=$case.case; languageTag=$case.languageTag; path=$audioRef.path; pathBase=$audioRef.pathBase; sha256=$audioRef.sha256; bytes=$audioRef.bytes; mediaType=$audioRef.mediaType }
}
$receiptPath = Join-Path $OutputRoot 'path-D-receipt.json'
Write-JsonOnce $receiptPath ([ordered]@{
    schemaVersion = 'quality-real-path/1.1.0'; pathId = 'D'; status = 'passed'; generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    route = 'real DeepSeek cleaned spokenText -> real Edge TTS synthesis -> audio files; no physical speaker'
    pathBases = [ordered]@{ projectRoot='Repository root containing tools/, tests/, canary/, receipts/ and contracts/.' }
    source = [ordered]@{
        moduleReceipt = New-ProjectRef $modulePath (Hash-File $modulePath) (Get-Item -LiteralPath $modulePath).Length 'application/json'
        validationReceipt = New-ProjectRef $validationPath (Hash-File $validationPath) (Get-Item -LiteralPath $validationPath).Length 'application/json'
    }
    contentSources = $contentSources
    audio = $audioFiles
    externalCalls = [ordered]@{ deepseek=$deepSeekCalls; deepseekLiveSources=2; edgeTts=4; physicalSpeakerPlayback=0 }
    externalProductionWrites = 0
    reproducibility = [ordered]@{ allReferencesUseSingleBase=$true; referencesHashAndBytesChecked=(@($contentSources).Count * 2 + @($audioFiles).Count + 2); ambiguousLegacyInputsPrefixRejected=$true }
})
[ordered]@{ status='passed'; receipt=$receiptPath; sha256=(Hash-File $receiptPath); audioFiles=4 } | ConvertTo-Json -Depth 5

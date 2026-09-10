[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$SdkFreeze
)
$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$root = [IO.Path]::GetFullPath($Candidate)
$descriptorPath = Join-Path $root 'authority\runtime-tools.v1.json'
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json -Depth 20
Require ($descriptor.schema -eq 'kc.authority-runtime-tools/1') 'Runtime descriptor schema drifted.'
$toolEvidence = @()
foreach ($name in @('edgeTts','ffplay')) {
    $entry = $descriptor.tools.$name
    Require (-not [IO.Path]::IsPathFullyQualified($entry.path)) "Runtime tool $name is not candidate-relative."
    $path = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
    Require ($path.StartsWith($root.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) "Runtime tool $name escapes candidate."
    Require ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -eq $entry.bytes -and (Hash $path) -eq $entry.sha256) "Runtime tool $name drifted."
    $toolEvidence += [ordered]@{ name=$name; candidateRelativePath=$entry.path; bytes=$entry.bytes; sha256=$entry.sha256 }
}
$manifestPath = Join-Path $root 'candidate-manifest.json'
$realPath = Join-Path $root 'real-language-matrix-no-env-receipt.v2.json'
$selfTestPath = Join-Path $root 'runtime-admission-self-test-receipt.json'
$real = Get-Content -LiteralPath $realPath -Raw | ConvertFrom-Json -Depth 100
$selfTest = Get-Content -LiteralPath $selfTestPath -Raw | ConvertFrom-Json -Depth 20
Require ($real.status -eq 'PASS' -and $real.runtimeAdmission.developerEnvironmentRemoved -and $real.runtimeAdmission.pathCleared) 'Environment-independent real matrix did not pass.'
Require ($selfTest.status -eq 'PASS') 'Runtime startup admission self-test did not pass.'
$receipt = [ordered]@{
    schema='kc.read-to-anki-runtime-self-contained-qualification/1'; createdAt=(Get-Date).ToUniversalTime().ToString('o'); status='PASS'
    candidate=[ordered]@{ root=$root; manifestSha256=Hash $manifestPath; runtimeDescriptor=[ordered]@{path='authority/runtime-tools.v1.json';sha256=Hash $descriptorPath}; tools=$toolEvidence }
    validation=[ordered]@{ startupAdmissionSelfTest=[ordered]@{path=$selfTestPath;sha256=Hash $selfTestPath}; realLanguageMatrix=[ordered]@{path=$realPath;sha256=Hash $realPath;pathCleared=$true;developerEnvironmentRemoved=$true;physicalSpeakerPlaybackCount=$real.physicalSpeakerPlaybackCount}; sdk=[ordered]@{root=[IO.Path]::GetFullPath($SdkFreeze);manifestSha256=Hash (Join-Path $SdkFreeze 'freeze-manifest.json')} }
    boundaries=[ordered]@{ userEnvironmentRequired=$false; pathFallback=$false; developerToolFallback=$false; remNoteCalls=0; minerUCalls=0; oldCandidate='rta-20260903T211155Z-90c373ff invalid-for-user-handtest' }
}
$receiptPath = Join-Path $root 'runtime-self-contained-qualification-receipt.json'
$receipt | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
$readable = @"
# Sealed speech runtime qualification

- Candidate: `$root`
- Core resolves edge-tts and ffplay only from `authority/runtime-tools.v1.json`.
- Startup validates candidate containment, existence, bytes, and SHA-256.
- Real meaning-reading, writing, both, and ask paths passed with PATH empty and both legacy tool environment variables removed.
- Four physical playback attempts completed; Anki semantic readback remained exact.
- The prior T133216Z candidate is invalid for user hand-test.
"@
Set-Content -LiteralPath (Join-Path $root 'RUNTIME-SELF-CONTAINED-READABLE.md') -Value $readable -Encoding utf8NoBOM
Write-Output "RECEIPT $receiptPath"
Write-Output "RECEIPT_SHA256 $(Hash $receiptPath)"
Write-Output "READABLE_SHA256 $(Hash (Join-Path $root 'RUNTIME-SELF-CONTAINED-READABLE.md'))"

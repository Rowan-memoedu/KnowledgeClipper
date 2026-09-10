[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ReceiptPath,
    [string]$OldCandidate = 'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260904T170626Z-f96beafd',
    [string]$OldQualityReceipt = 'D:\Artifacts\KnowledgeClipper\read-to-anki\quality\20260904T091759Z-core-v1243-sealed-pre-admission\quality-core-sealed-pre-admission.json',
    [string]$RevokedV3Manifest = 'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-observation-20260904T121454Z-2f5a3714\candidate-manifest.json'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$hashFunctions = Join-Path $repositoryRoot 'core-v3\tools\CanonicalTreeHash.ps1'
$pythonVerifier = Join-Path $repositoryRoot 'core-v3\tools\Verify-CanonicalTreeHash.py'
$generator = Join-Path $repositoryRoot 'core-v3\tools\New-ObservationMatchedCoreCandidate.ps1'
. $hashFunctions

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Sha([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-LegacyManifestOrderTreeHash([object[]]$Files) {
    $builder = [Text.StringBuilder]::new()
    foreach ($file in $Files) {
        [void]$builder.Append([string]$file.path).Append("`t").Append(
            ([long]$file.bytes).ToString([Globalization.CultureInfo]::InvariantCulture)
        ).Append("`t").Append(([string]$file.sha256).ToUpperInvariant()).Append("`n")
    }
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($builder.ToString())))
}

function Shuffle([object[]]$Values, [int]$Seed) {
    $copy = @($Values)
    $random = [Random]::new($Seed)
    for ($index = $copy.Count - 1; $index -gt 0; $index--) {
        $swap = $random.Next($index + 1)
        $temporary = $copy[$index]
        $copy[$index] = $copy[$swap]
        $copy[$swap] = $temporary
    }
    return $copy
}

function Invoke-PythonReject([object]$Document, [string]$Name, [string[]]$ExtraArguments = @()) {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('kc-tree-hash-negative-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $Document | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        $previousNativePreference = $PSNativeCommandUseErrorActionPreference
        try {
            $PSNativeCommandUseErrorActionPreference = $false
            $null = & python $pythonVerifier --input $path @ExtraArguments 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            $PSNativeCommandUseErrorActionPreference = $previousNativePreference
        }
        Require ($exitCode -ne 0) "Python verifier unexpectedly accepted $Name."
        return 'rejected'
    }
    finally {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
}

$logical = @(
    [ordered]@{ path = 'Z/Alpha.txt'; bytes = [long]7; sha256 = ('a' * 64) },
    [ordered]@{ path = 'a/alpha.txt'; bytes = [long]11; sha256 = ('B' * 64) },
    [ordered]@{ path = '资料/甲.txt'; bytes = [long]13; sha256 = ('c' * 64) },
    [ordered]@{ path = '资料/乙.txt'; bytes = [long]17; sha256 = ('D' * 64) },
    [ordered]@{ path = ('long/' + ('深' * 270) + '.bin'); bytes = [long]922337; sha256 = ('e' * 64) }
)

$representations = [ordered]@{
    orderedDictionary = @(Shuffle $logical 101 | ForEach-Object { [ordered]@{ path = $_.path; bytes = $_.bytes; sha256 = $_.sha256 } })
    psCustomObject = @(Shuffle $logical 202 | ForEach-Object { [pscustomobject]@{ sha256 = $_.sha256; path = $_.path; bytes = $_.bytes } })
    hashtable = @(Shuffle $logical 303 | ForEach-Object { @{ bytes = $_.bytes; sha256 = $_.sha256; path = $_.path } })
    reversed = @($logical[($logical.Count - 1)..0])
}

$hashes = [ordered]@{}
foreach ($entry in $representations.GetEnumerator()) {
    $hashes[$entry.Key] = Get-KcCanonicalTreeHash -Files @($entry.Value)
    $normalized = @(ConvertTo-KcCanonicalTreeRecords -Files @($entry.Value))
    Require ($normalized.Count -eq $logical.Count) "$($entry.Key) normalized count drifted."
    Require (@($normalized | Where-Object { $_.bytes -isnot [long] }).Count -eq 0) "$($entry.Key) bytes are not Int64."
}
$expected = $hashes.orderedDictionary
Require (@($hashes.Values | Select-Object -Unique).Count -eq 1) 'PowerShell representation/order invariance failed.'
$fixtureBytes = Get-KcCanonicalTreeTotalBytes -Files $logical
$fixtureDocument = [ordered]@{
    schema = 'kc.canonical-tree-hash-fixture/1'
    payload = [ordered]@{
        files = $logical.Count
        bytes = [long]$fixtureBytes
        treeSha256 = $expected
        treeAlgorithm = 'sha256(utf8 ordinal(path TAB int64(bytes) TAB uppercase(sha256) LF))'
    }
    files = $representations.hashtable
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('kc-tree-hash-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $fixtureDocument | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    $pythonRaw = & python $pythonVerifier --input $temporary
    Require ($LASTEXITCODE -eq 0) 'Independent Python verifier failed.'
    $pythonResult = $pythonRaw | ConvertFrom-Json
    Require ($pythonResult.treeSha256 -eq $expected) 'Python and PowerShell tree hashes differ.'
    Require ($pythonResult.bytes -is [long]) 'Python JSON total bytes did not deserialize as Int64.'
    Require ($pythonResult.schema -eq 'kc.canonical-tree-hash-fixture/1' -and $pythonResult.declaredValuesMatched) 'Python fixture schema dispatch failed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}

$oldManifestPath = Join-Path $OldCandidate 'candidate-manifest.json'
$oldManifest = Get-Content -LiteralPath $oldManifestPath -Raw | ConvertFrom-Json -Depth 100
$oldQuality = Get-Content -LiteralPath $OldQualityReceipt -Raw | ConvertFrom-Json -Depth 100
$oldHash = Get-KcCanonicalTreeHash -Files @($oldManifest.files)
$oldLegacyHash = Get-LegacyManifestOrderTreeHash -Files @($oldManifest.files)
Require ($oldLegacyHash -eq $oldQuality.candidate.inventoryTreeSha256) 'Old sealed candidate no longer matches its recorded legacy manifest-order tree.'

$oldPython = (& python $pythonVerifier --input $oldManifestPath --expected-tree $oldQuality.candidate.inventoryTreeSha256) | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $oldPython.treeSha256 -eq $oldLegacyHash) 'Python v2 historical dispatch failed.'
Require ($oldPython.dispatch -eq 'legacy-v2-manifest-order' -and $oldPython.declaredValuesMatched) 'Python v2 declaration gate failed.'

$revokedV3Python = (& python $pythonVerifier --input $RevokedV3Manifest) | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $revokedV3Python.schema -eq 'kc.read-to-anki-candidate-manifest/3') 'Python v3 manifest dispatch failed.'
Require ($revokedV3Python.dispatch -eq 'canonical-v3-ordinal' -and $revokedV3Python.declaredValuesMatched) 'Python v3 declared aggregate gate failed.'

$negativeCases = [ordered]@{}
foreach ($case in @(
    [pscustomobject]@{ name = 'duplicate-path'; files = @($logical[0], $logical[0]) },
    [pscustomobject]@{ name = 'negative-bytes'; files = @([ordered]@{ path = 'x'; bytes = -1; sha256 = ('A' * 64) }) },
    [pscustomobject]@{ name = 'invalid-sha'; files = @([ordered]@{ path = 'x'; bytes = 1; sha256 = 'not-a-sha' }) }
)) {
    try {
        $null = Get-KcCanonicalTreeHash -Files @($case.files)
        $negativeCases[$case.name] = 'unexpected-pass'
    }
    catch {
        $negativeCases[$case.name] = 'rejected'
    }
}
Require (@($negativeCases.Values | Where-Object { $_ -ne 'rejected' }).Count -eq 0) 'Negative tree record gate failed.'

$pythonNegativeCases = [ordered]@{}
$pythonNegativeCases.unknownSchema = Invoke-PythonReject -Name 'unknown-schema' -Document ([ordered]@{
    schema = 'kc.unknown/1'; payload = $fixtureDocument.payload; files = $logical
})
$pythonNegativeCases.unknownAlgorithm = Invoke-PythonReject -Name 'unknown-algorithm' -Document ([ordered]@{
    schema = 'kc.canonical-tree-hash-fixture/1'
    payload = [ordered]@{ files = $logical.Count; bytes = $fixtureBytes; treeSha256 = $expected; treeAlgorithm = 'sha256(unknown)' }
    files = $logical
})
$pythonNegativeCases.missingAlgorithm = Invoke-PythonReject -Name 'missing-algorithm' -Document ([ordered]@{
    schema = 'kc.read-to-anki-candidate-manifest/3'
    payload = [ordered]@{ files = $logical.Count; bytes = $fixtureBytes; treeSha256 = $expected }
    files = $logical
})
$pythonNegativeCases.tamperedFileCount = Invoke-PythonReject -Name 'tampered-file-count' -Document ([ordered]@{
    schema = 'kc.canonical-tree-hash-fixture/1'
    payload = [ordered]@{ files = $logical.Count + 1; bytes = $fixtureBytes; treeSha256 = $expected; treeAlgorithm = $fixtureDocument.payload.treeAlgorithm }
    files = $logical
})
$pythonNegativeCases.tamperedTotalBytes = Invoke-PythonReject -Name 'tampered-total-bytes' -Document ([ordered]@{
    schema = 'kc.canonical-tree-hash-fixture/1'
    payload = [ordered]@{ files = $logical.Count; bytes = $fixtureBytes + 1; treeSha256 = $expected; treeAlgorithm = $fixtureDocument.payload.treeAlgorithm }
    files = $logical
})
$pythonNegativeCases.tamperedTree = Invoke-PythonReject -Name 'tampered-tree' -Document ([ordered]@{
    schema = 'kc.canonical-tree-hash-fixture/1'
    payload = [ordered]@{ files = $logical.Count; bytes = $fixtureBytes; treeSha256 = ('0' * 64); treeAlgorithm = $fixtureDocument.payload.treeAlgorithm }
    files = $logical
})
$pythonNegativeCases.v2MissingHistoricalTree = Invoke-PythonReject -Name 'v2-missing-historical-tree' -Document $oldManifest
Require (@($pythonNegativeCases.Values | Where-Object { $_ -ne 'rejected' }).Count -eq 0) 'Python schema-aware negative gate failed.'

$receiptDirectory = Split-Path -Parent $ReceiptPath
if (-not (Test-Path -LiteralPath $receiptDirectory)) { New-Item -ItemType Directory -Path $receiptDirectory | Out-Null }
if (Test-Path -LiteralPath $ReceiptPath) { throw "Append-only receipt already exists: $ReceiptPath" }
[ordered]@{
    schema = 'kc.observation-candidate-canonical-tree-hash-qualification/1'
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    status = 'PASS'
    candidateCount = 0
    algorithm = 'sha256(utf8 ordinal(path TAB int64(bytes) TAB uppercase(sha256) LF))'
    fixture = [ordered]@{
        files = $logical.Count
        includesNonAsciiCaseSensitivePaths = $true
        includesLongPath = $true
        maximumPathCharacters = @($logical.path | ForEach-Object Length | Measure-Object -Maximum).Maximum
        representations = @($hashes.Keys)
        treeSha256 = $expected
        totalBytes = [long]$fixtureBytes
        powershellPythonExactMatch = $true
        schemaAwareDeclaredValuesMatched = $true
    }
    oldSealedCandidate = [ordered]@{
        root = $OldCandidate
        manifestSha256 = Sha $oldManifestPath
        files = $oldManifest.files.Count
        totalBytes = Get-KcCanonicalTreeTotalBytes -Files @($oldManifest.files)
        recordedTreeSha256 = $oldQuality.candidate.inventoryTreeSha256
        legacyManifestOrderRecomputedTreeSha256 = $oldLegacyHash
        legacyRecordedTreeStillValid = $true
        canonicalOrdinalTreeSha256 = $oldHash
        canonicalMigrationChangesTreeIdentity = ($oldHash -ne $oldLegacyHash)
        powershellPythonExactMatch = $true
    }
    negativeCases = $negativeCases
    pythonSchemaDispatch = [ordered]@{
        canonicalFixture = 'canonical-v3-ordinal'
        legacyV2 = 'legacy-v2-manifest-order'
        unknownSchemaOrAlgorithm = 'fail-closed'
        declaredCountBytesTreeVerified = $true
        revokedV3Manifest = [ordered]@{
            path = $RevokedV3Manifest
            sha256 = Sha $RevokedV3Manifest
            treeSha256 = $revokedV3Python.treeSha256
            status = 'verified-but-still-invalidated-never-promoted'
        }
        negativeCases = $pythonNegativeCases
    }
    source = [ordered]@{
        generator = [ordered]@{ path = $generator; sha256 = Sha $generator }
        functions = [ordered]@{ path = $hashFunctions; sha256 = Sha $hashFunctions }
        test = [ordered]@{ path = $PSCommandPath; sha256 = Sha $PSCommandPath }
        pythonVerifier = [ordered]@{ path = $pythonVerifier; sha256 = Sha $pythonVerifier }
    }
    invalidatedCandidates = @(
        [ordered]@{ id = 'rta-observation-20260904T115534Z-998fe818'; reason = 'candidate.payload.bytes-null' },
        [ordered]@{ id = 'rta-observation-20260904T115914Z-dfd5ae78'; reason = 'representation-dependent-tree-hash' },
        [ordered]@{ id = 'rta-observation-20260904T121454Z-2f5a3714'; reason = 'one-build-admission-revoked-before-seal-completed' }
    )
    boundaries = [ordered]@{
        candidatesCreated = 0
        businessWorkerRuns = 0
        externalCalls = 0
        externalWrites = 0
        physicalSpeakerPlayback = $false
    }
} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8NoBOM

Write-Output "RESULT PASS CANONICAL_TREE_HASH"
Write-Output "TREE_SHA256 $expected"
Write-Output "OLD_TREE_SHA256 $oldHash"
Write-Output "RECEIPT $ReceiptPath"
Write-Output "RECEIPT_SHA256 $(Sha $ReceiptPath)"

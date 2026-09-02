[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [Parameter(Mandatory = $true)][string]$ReleaseRoot
)

$ErrorActionPreference = 'Stop'
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$receiptPath = Join-Path $AuthorityDirectory 'assembly-receipt.v1.json'
$gatePath = Join-Path $AuthorityDirectory 'assembly-path-gate.v1.json'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "Assembly receipt is missing: $receiptPath" }
if (Test-Path -LiteralPath $gatePath) { throw "Append-only assembly path gate already exists: $gatePath" }

$receiptText = Get-Content -LiteralPath $receiptPath -Raw
if ($receiptText.Contains('.tmp', [StringComparison]::OrdinalIgnoreCase)) { throw 'Assembly receipt contains a staging path.' }
$receipt = $receiptText | ConvertFrom-Json -Depth 100
$authorityPrefix = $AuthorityDirectory.TrimEnd('\') + '\'
$releasePrefix = $ReleaseRoot.TrimEnd('\') + '\'
$evidence = @()
foreach ($workerProperty in $receipt.workers.PSObject.Properties) {
    $worker = $workerProperty.Value
    $path = [IO.Path]::GetFullPath([string]$worker.path)
    if (-not ($path.StartsWith($authorityPrefix, [StringComparison]::OrdinalIgnoreCase) -or
              $path.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Assembly path escaped final authority/release roots: $path"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Assembly path is missing: $path" }
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($item.Length -ne $worker.bytes -or $hash -ne $worker.sha256) { throw "Assembly path bytes/hash drifted: $path" }
    $evidence += [ordered]@{ workerId = $workerProperty.Name; path = $path; bytes = $item.Length; sha256 = $hash; status = 'matched' }
}
if ($evidence.Count -ne 5) { throw "Assembly path gate requires exactly five worker paths; actual=$($evidence.Count)" }

$gate = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-assembly-path-gate.v1'
    status = 'passed'
    authorityDirectory = $AuthorityDirectory
    releaseRoot = $ReleaseRoot
    assemblyReceipt = [ordered]@{
        path = $receiptPath
        bytes = (Get-Item -LiteralPath $receiptPath).Length
        sha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash
        containsTempPath = $false
    }
    pathCount = $evidence.Count
    paths = $evidence
    allExist = $true
    allWithinFinalRoots = $true
    allBytesAndHashesMatch = $true
    externalWrites = 0
    physicalSpeakerInvocations = 0
}
[IO.File]::WriteAllText($gatePath, (($gate | ConvertTo-Json -Depth 30) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ status = 'passed'; gate = $gatePath; sha256 = (Get-FileHash -LiteralPath $gatePath -Algorithm SHA256).Hash; paths = $evidence.Count } | ConvertTo-Json -Depth 5

[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$authority = Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.25-20260901T011500Z'
$checkpoint = Join-Path $authority 'checkpoint.json'
$output = Join-Path $authority 'invalidation-receipt.v1.json'
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
foreach ($path in @($authority, $checkpoint)) { if (-not (Test-Path -LiteralPath $path)) { throw "Missing v1.2.25 invalidation input: $path" } }
if (Test-Path -LiteralPath $output) { throw "Append-only invalidation already exists: $output" }
$document = [ordered]@{
    schemaVersion = 'knowledge-clipper-authority-invalidation-receipt.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'invalidated-never-promoted'
    authority = [ordered]@{ registryVersion = '1.2.25'; checkpoint = $checkpoint; checkpointSha256 = Sha $checkpoint }
    reason = 'portable-schema-closure gate passed against the release but was not bound into the authority checkpoint; superseded by v1.2.26 before any task dispatch.'
    externalWorkerRuns = 0
    externalWrites = 0
    frozenV1Modified = $false
}
[IO.File]::WriteAllText($output, (($document | ConvertTo-Json -Depth 16) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[ordered]@{ status = $document.status; receipt = $output; sha256 = Sha $output } | ConvertTo-Json -Depth 10

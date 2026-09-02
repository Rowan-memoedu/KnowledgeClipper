[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$AuthorityDirectory,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$AuthorityDirectory = [IO.Path]::GetFullPath($AuthorityDirectory)
$registry = Join-Path $AuthorityDirectory 'worker-registry.v1.json'
$dependency = Join-Path $AuthorityDirectory 'dependency-manifest.v1.json'
$output = Join-Path $AuthorityDirectory 'portable-schema-closure-gate.v1.json'
$frozenArtifact = Join-Path $ProjectRoot 'contracts\v1\schemas\common\artifact.schema.json'
$packedArtifact = Join-Path $ReleaseRoot 'contracts\v1\schemas\common\artifact.schema.json'

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
foreach ($path in @($registry, $dependency, $frozenArtifact, $packedArtifact)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Portable schema closure input is missing: $path" }
}
if (Test-Path -LiteralPath $output) { throw "Append-only portable schema closure gate already exists: $output" }
if ((Sha $frozenArtifact) -ne (Sha $packedArtifact)) { throw 'Packed frozen artifact schema hash drifted.' }

$closureText = (& python (Join-Path $PSScriptRoot 'validate_release_schema_closure.py') --release-root $ReleaseRoot 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw "Portable schema closure validator failed: $closureText" }
$closure = $closureText | ConvertFrom-Json -Depth 100
if ($closure.schemaVersion -ne 'knowledge-clipper-portable-schema-closure-gate.v1' -or $closure.status -ne 'passed' -or @($closure.failures).Count -ne 0) {
    throw "Portable schema closure validator returned an invalid result: $closureText"
}

$gate = [ordered]@{
    schemaVersion = 'knowledge-clipper-portable-schema-closure-gate.v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    releaseRoot = $ReleaseRoot
    registrySha256 = Sha $registry
    dependencyManifestSha256 = Sha $dependency
    schemasChecked = [int]$closure.schemasChecked
    localRefsChecked = [int]$closure.localRefsChecked
    failures = @()
    frozenArtifactSchemaPath = 'contracts/v1/schemas/common/artifact.schema.json'
    frozenArtifactSchemaSha256 = Sha $frozenArtifact
    frozenArtifactSchemaPackedUnchanged = $true
}
$tmp = Join-Path $AuthorityDirectory ('.portable-schema-closure-gate.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($tmp, (($gate | ConvertTo-Json -Depth 20) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($tmp, $output)
}
finally {
    if (Test-Path -LiteralPath $tmp) { [IO.File]::Delete($tmp) }
}
[ordered]@{ status = $gate.status; gate = $output; sha256 = Sha $output; schemasChecked = $gate.schemasChecked; localRefsChecked = $gate.localRefsChecked } | ConvertTo-Json -Depth 10
